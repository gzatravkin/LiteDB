﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Manage linear memory segments to avoid re-create array buffer in heap memory
    /// Do not share same memory store with diferent files
    /// [ThreadSafe]
    /// </summary>
    internal class MemoryCache : IDisposable
    {
        /// <summary>
        /// Contains free ready-to-use pages in memory
        /// - All pages here MUST be ShareCounter = 0
        /// - All pages here MUST be Position = MaxValue
        /// </summary>
        private readonly ConcurrentQueue<PageBuffer> _free = new ConcurrentQueue<PageBuffer>();

        /// <summary>
        /// Contains only clean pages (from both data/log file) - support page concurrency use
        /// - MUST have defined Origin and Position
        /// - Contains only 1 instance per Position/Origin
        /// - Contains only pages with ShareCounter >= 0
        /// *  = 0 - Page are avaiable but are not using by anyone (can be moved into _free list in next Extend())
        /// * >= 1 - Page are in used by 1 or more threads. Page must run "Release" when finish use
        /// </summary>
        private readonly ConcurrentDictionary<long, PageBuffer> _readable = new ConcurrentDictionary<long, PageBuffer>();

        /// <summary>
        /// Get how many extends was made in this store
        /// </summary>
        private int _extends = 0;

        /// <summary>
        /// Get memory segment sizes
        /// </summary>
        private readonly int[] _segmentSizes;

        public MemoryCache(int[] memorySegmentSizes)
        {
            _segmentSizes = memorySegmentSizes;

            this.Extend();
        }

        #region Readable Pages

        /// <summary>
        /// Get page from clean cache (readable). If page not exits, create this new page and load data using factory fn
        /// </summary>
        public PageBuffer GetReadablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            // get dict key based on position/origin
            var key = this.GetReadableKey(position, origin);
            do
            {
            	//If key is not present, just create new page
                if (!_readable.TryGetValue(key, out var page))
                {
                    var newPage = GetFreePage();
                    newPage.Position = position;
                    newPage.Origin = origin;
                    newPage.ShareCounter = 1;
                    Interlocked.Exchange(ref newPage.Timestamp, DateTime.UtcNow.Ticks);

                    // load page content with disk stream
                    factory(position, newPage);
                    if (_readable.TryAdd(key, newPage))
                    {
                        return newPage;
                    }

                    newPage.ShareCounter = 0; //Wasn't able to add it? discard and try again
                    this.CleanPage(newPage);
                    _free.Enqueue(newPage);
                    continue;
                }
                else
                {
                    var shareCount = page.ShareCounter;
                    if (shareCount == 0)
                    {
                        lock (this._free) //Locking free to ensure that Extend method won't delete our page meanwhile in another thread
                        {
                            if (_readable.TryGetValue(key, out var existingPage) && existingPage == page)
                            {
                                Interlocked.Increment(ref page.ShareCounter);
                                Interlocked.Exchange(ref page.Timestamp, DateTime.UtcNow.Ticks);
                                return page;
                            }
                            else
                                continue;
                        }
                    }
                    if (Interlocked.CompareExchange(ref page.ShareCounter, shareCount + 1, shareCount) != shareCount) continue;
                    Interlocked.Exchange(ref page.Timestamp, DateTime.UtcNow.Ticks);
                    return page;
                }
            } while (true);
        }

        /// <summary>
        /// Get unique position in dictionary according with origin. Use positive/negative values
        /// </summary>
        private long GetReadableKey(long position, FileOrigin origin)
        {
            ENSURE(origin != FileOrigin.None, "file origin must be defined");

            if (origin == FileOrigin.Data)
            {
                return position;
            }
            else
            {
                if (position == 0) return long.MinValue;

                return -position;
            }
        }

        #endregion

        #region Writable Pages

        /// <summary>
        /// Request for a writable page - no other can read this page and this page has no reference
        /// Writable pages can be MoveToReadable() or DiscardWritable() - but never Released()
        /// </summary>
        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            var key = this.GetReadableKey(position, origin);

            // write pages always contains a new buffer array
            var writable = this.NewPage(position, origin);

            // if requested page already in cache, just copy buffer and avoid load from stream
            if (_readable.TryGetValue(key, out var clean))
            {
                Buffer.BlockCopy(clean.Array, clean.Offset, writable.Array, writable.Offset, PAGE_SIZE);
            }
            else
            {
                factory(position, writable);
            }

            return writable;
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable.
        /// </summary>
        public PageBuffer NewPage()
        {
            return this.NewPage(long.MaxValue, FileOrigin.None);
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable.
        /// </summary>
        private PageBuffer NewPage(long position, FileOrigin origin)
        {
            var page = this.GetFreePage();

            // set page position and page as writable
            page.Position = position;

            // define as writable
            page.ShareCounter = BUFFER_WRITABLE;

            // Timestamp = 0 means this page was never used (do not clear)
            if (page.Timestamp > 0)
            {
                page.Clear();
            }

            DEBUG(page.All(0), "new page must be full zero empty before return");

            page.Origin = origin;
            page.Timestamp = DateTime.UtcNow.Ticks;

            return page;
        }

        /// <summary>
        /// Try move this page to readable list (if not alrady in readable list)
        /// Returns true if was moved
        /// </summary>
        public bool TryMoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "page must have a position");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "page must be writable");
            ENSURE(page.Origin != FileOrigin.None, "page must has defined origin");

            var key = this.GetReadableKey(page.Position, page.Origin);

            // set page as not in use
            page.ShareCounter = 0;

            var added = _readable.TryAdd(key, page);

            // if not added, let's back ShareCounter to writable state
            if (!added)
            {
                page.ShareCounter = BUFFER_WRITABLE;
            }

            return added;
        }

        /// <summary>
        /// Move a writable page to readable list - if already exisits, override content
        /// Used after write operation that must mark page as readable becase page content was changed
        /// This method runs BEFORE send to write disk queue - but new page request must read this new content
        /// Returns readable page
        /// </summary>
        public PageBuffer MoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "page must have position to be readable");
            ENSURE(page.Origin != FileOrigin.None, "page should be a source before move to readable");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "page must be writable before from to readable dict");

            var key = this.GetReadableKey(page.Position, page.Origin);
            var added = true;

            // no concurrency in writable page
            page.ShareCounter = 1;

            var readable = _readable.AddOrUpdate(key, page, (newKey, current) =>
            {
                // if page already exist inside readable list, should never be in-used (this will be garanteed by lock control)
                ENSURE(current.ShareCounter == 0, "user must ensure this page are not in use when mark as read only");
                ENSURE(current.Origin == page.Origin, "origin must be same");

                current.ShareCounter = 1;

                // if page already in cache, this is a duplicate page in memory
                // must update cached page with new page content
                Buffer.BlockCopy(page.Array, page.Offset, current.Array, current.Offset, PAGE_SIZE);

                added = false;

                return current;
            });

            // if page was not added into readable list move page to free list
            if (added == false)
            {
                this.DiscardPage(page);
            }

            // return page that are in _readble list
            return readable;
        }

        /// <summary>
        /// Complete discard a writable page - clean content and move to free list
        /// </summary>
        public void DiscardPage(PageBuffer page)
        {
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "discarded page must be writable");

            // clear page controls
            page.ShareCounter = 0;
            page.Position = long.MaxValue;
            page.Origin = FileOrigin.None;

            // DO NOT CLEAR CONTENT
            // when this page will requested from free list will be clear if request was from NewPage()
            //  or will be overwrite by ReadPage

            // added into free list
            _free.Enqueue(page);
        }

        #endregion

        #region Cache managment

        /// <summary>
        /// Get a clean, re-usable page from store. If store are empty, can extend buffer segments
        /// </summary>
        private PageBuffer GetFreePage()
        {
            if (_free.TryDequeue(out var page))
            {
                ENSURE(page.Position == long.MaxValue, "pages in memory store must have no position defined");
                ENSURE(page.ShareCounter == 0, "pages in memory store must be non-shared");
                ENSURE(page.Origin == FileOrigin.None, "page in memory must have no page origin");

                return page;
            }
            // if no more page inside memory store - extend store/reuse non-shared pages
            else
            {
                // ensure only 1 single thread call extend method
                lock(_free)
                {
                    if (_free.Count > 0) return this.GetFreePage();

                    this.Extend();
                }

                return this.GetFreePage();
            }
        }

        private void CleanPage(PageBuffer page)
        {
            // clean controls
            page.Position = long.MaxValue;
            page.Origin = FileOrigin.None;
        }

        /// <summary>
        /// Check if it's possible move readable pages to free list - if not possible, extend memory
        /// </summary>
        private void Extend()
        {
            // count how many pages in cache are available to be re-used (is not in use at this time)
            var emptyShareCounter = _readable.Values.Count(x => x.ShareCounter == 0);

            // get segmentSize
            var segmentSize = _segmentSizes[Math.Min(_segmentSizes.Length - 1, _extends)];

            // if this count is larger than MEMORY_SEGMENT_SIZE, re-use all this pages
            if (emptyShareCounter > segmentSize)
            {
                // get all readable pages that can return to _free (slow way)
                // sort by timestamp used (set as free oldest first)
                var readables = _readable
                    .Where(x => x.Value.ShareCounter == 0)
                    .OrderBy(x => x.Value.Timestamp)
                    .Select(x => x.Key)
                    .Take(segmentSize)
                    .ToArray();

                // move pages from readable list to free list
                foreach (var key in readables)
                {
                    var removed = _readable.TryRemove(key, out var page);

                    ENSURE(removed, "page should be in readable list before move to free list");

                    // if removed page was changed between make array and now, must add back to readable list
                    if (page.ShareCounter > 0)
                    {
                        // but wait: between last "remove" and now, another thread can added this page
                        if (!_readable.TryAdd(key, page))
                        {
                            // this is a terrible situation, to avoid memory corruption I will throw expcetion for now
                            throw new LiteException(0, "MemoryCache: removed in-use memory page. This situation has no way to fix (yet). Throwing exception to avoid database corruption. No other thread can read/write from database now.");
                        }
                    }
                    else
                    {
                        ENSURE(page.ShareCounter == 0, "page should be not in use by anyone");

                        this.CleanPage(page);

                        _free.Enqueue(page);
                    }
                }

                LOG($"re-using cache pages (flushing {_free.Count} pages)", "CACHE");
            }
            else
            {
                // create big linear array in heap memory (LOH => 85Kb)
                var buffer = new byte[PAGE_SIZE * segmentSize];

                // split linear array into many array slices
                for (var i = 0; i < segmentSize; i++)
                {
                    var uniqueID = (_extends * segmentSize) + i + 1;

                    _free.Enqueue(new PageBuffer(buffer, i * PAGE_SIZE, uniqueID));
                }

                _extends++;

                LOG($"extending memory usage: (segments: {_extends})", "CACHE");
            }
        }

        /// <summary>
        /// Return how many pages are in use when call this method (ShareCounter != 0).
        /// </summary>
        public int PagesInUse => _readable.Values.Where(x => x.ShareCounter != 0).Count();

        /// <summary>
        /// Return how many pages are available completed free
        /// </summary>
        public int FreePages => _free.Count;

        /// <summary>
        /// Return how many segments already loaded in memory
        /// </summary>
        public int ExtendSegments => _extends;

        /// <summary>
        /// Get how many pages this cache extends in memory
        /// </summary>
        public int ExtendPages => Enumerable.Range(0, _extends).Select(x => _segmentSizes[Math.Min(_segmentSizes.Length - 1, x)]).Sum();

        /// <summary>
        /// Get how many pages are used as Writable at this moment
        /// </summary>
        public int WritablePages => this.ExtendPages - // total memory
            _free.Count - _readable.Count; // allocated pages

        /// <summary>
        /// Get all readable pages
        /// </summary>
        public ICollection<PageBuffer> GetPages() => _readable.Values;

        /// <summary>
        /// Clean all cache memory - moving back all readable pages into free list
        /// This command must be called inside a exclusive lock
        /// </summary>
        public int Clear()
        {
            var counter = 0;

            ENSURE(this.PagesInUse == 0, "must have no pages in used when call Clear() cache");

            foreach (var page in _readable.Values)
            {
                page.Position = long.MaxValue;
                page.Origin = FileOrigin.None;

                _free.Enqueue(page);

                counter++;
            }

            _readable.Clear();

            return counter;
        }

        #endregion

        public void Dispose()
        {
        }
    }
}