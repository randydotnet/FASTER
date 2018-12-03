﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.IO;
using System.Diagnostics;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace FASTER.core
{
    public enum PMMFlushStatus : int { Flushed, InProgress };

    public enum PMMCloseStatus : int { Closed, Open };

    public struct FullPageStatus
    {
        public long LastFlushedUntilAddress;
        public FlushCloseStatus PageFlushCloseStatus;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct FlushCloseStatus
    {
        [FieldOffset(0)]
        public PMMFlushStatus PageFlushStatus;
        [FieldOffset(4)]
        public PMMCloseStatus PageCloseStatus;
        [FieldOffset(0)]
        public long value;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PageOffset
    {
        [FieldOffset(0)]
        public int Offset;
        [FieldOffset(4)]
        public int Page;
        [FieldOffset(0)]
        public long PageAndOffset;
    }

    public unsafe abstract class AllocatorBase<Key, Value> : IDisposable
        where Key : IKey<Key>
        where Value : IValue<Value>
    {
        // Epoch information
        protected LightEpoch epoch;

        #region Protected size definitions
        // Buffer and log page size
        protected readonly int BufferSize;
        protected readonly int LogPageSizeBits;

        // Page size
        protected readonly int PageSize; // = 1 << LogPageSizeBits;
        protected readonly int PageSizeMask; // = PageSize - 1;
        protected readonly int AlignedPageSizeBytes;

        // Segment size
        protected readonly int LogSegmentSizeBits;
        protected readonly long SegmentSize;
        protected readonly long SegmentSizeMask;
        protected readonly int SegmentBufferSize;

        // Total HLOG size
        protected readonly int LogTotalSizeBits;
        protected readonly long LogTotalSizeBytes;


        // HeadOffset lag (from tail)
        protected const int HeadOffsetLagNumPages = 4;
        protected readonly int HeadOffsetLagSize;
        protected readonly long HeadOffsetLagAddress;

        // ReadOnlyOffset lag (from tail)
        protected readonly double LogMutableFraction;
        protected readonly long ReadOnlyLagAddress;

        #endregion

        #region Public addresses
        /// <summary>
        /// Read-only address
        /// </summary>
        public long ReadOnlyAddress;

        /// <summary>
        /// Safe read-only address
        /// </summary>
        public long SafeReadOnlyAddress;

        /// <summary>
        /// Head address
        /// </summary>
        public long HeadAddress;

        /// <summary>
        ///  Safe head address
        /// </summary>
        public long SafeHeadAddress;

        /// <summary>
        /// Flushed until address
        /// </summary>
        public long FlushedUntilAddress;

        /// <summary>
        /// Begin address
        /// </summary>
        public long BeginAddress;

        #endregion

        #region Protected device info
        protected readonly IDevice device;
        protected readonly int sectorSize;
        #endregion

        #region Private page metadata
        /// <summary>
        /// Index in circular buffer, of the current tail page
        /// </summary>
        private volatile int TailPageIndex;

        // Array that indicates the status of each buffer page
        protected readonly FullPageStatus[] PageStatusIndicator;

        /// <summary>
        /// Global address of the current tail (next element to be allocated from the circular buffer) 
        /// </summary>
        private PageOffset TailPageOffset;
        #endregion

        // Read buffer pool
        protected NativeSectorAlignedBufferPool readBufferPool;

        public AllocatorBase(LogSettings settings)
        {
            // Page size
            LogPageSizeBits = settings.PageSizeBits;
            PageSize = 1 << LogPageSizeBits;
            PageSizeMask = PageSize - 1;

            // Segment size
            LogSegmentSizeBits = settings.SegmentSizeBits;
            SegmentSize = 1 << LogSegmentSizeBits;
            SegmentSizeMask = SegmentSize - 1;
            SegmentBufferSize = 1 +
                (LogTotalSizeBytes / SegmentSize < 1 ? 1 : (int)(LogTotalSizeBytes / SegmentSize));

            // Total HLOG size
            LogTotalSizeBits = settings.MemorySizeBits;
            LogTotalSizeBytes = 1L << LogTotalSizeBits;
            BufferSize = (int)(LogTotalSizeBytes / (1L << LogPageSizeBits));

            // HeadOffset lag (from tail)
            HeadOffsetLagSize = BufferSize - HeadOffsetLagNumPages;
            HeadOffsetLagAddress = (long)HeadOffsetLagSize << LogPageSizeBits;

            // ReadOnlyOffset lag (from tail)
            LogMutableFraction = settings.MutableFraction;
            ReadOnlyLagAddress = (long)(LogMutableFraction * BufferSize) << LogPageSizeBits;

            if (BufferSize < 16)
            {
                throw new Exception("HLOG buffer must be at least 16 pages");
            }

            PageStatusIndicator = new FullPageStatus[BufferSize];

            device = settings.LogDevice;
            sectorSize = (int)device.SectorSize;
            AlignedPageSizeBytes = ((PageSize + (sectorSize - 1)) & ~(sectorSize - 1));
        }

        protected void Initialize(long firstValidAddress)
        {
            readBufferPool = NativeSectorAlignedBufferPool.GetPool(1, sectorSize);
            long tailPage = firstValidAddress >> LogPageSizeBits;
            int tailPageIndex = (int)(tailPage % BufferSize);

            Debug.Assert(tailPageIndex == 0);
            AllocatePage(tailPageIndex);

            SafeReadOnlyAddress = firstValidAddress;
            ReadOnlyAddress = firstValidAddress;
            SafeHeadAddress = firstValidAddress;
            HeadAddress = firstValidAddress;
            FlushedUntilAddress = firstValidAddress;
            BeginAddress = firstValidAddress;

            TailPageOffset.Page = (int)(firstValidAddress >> LogPageSizeBits);
            TailPageOffset.Offset = (int)(firstValidAddress & PageSizeMask);

            TailPageIndex = -1;
        }


        public abstract ref RecordInfo GetInfo(long logicalAddress);
        public abstract ref Key GetKey(long logicalAddress);
        public abstract ref Value GetValue(long logicalAddress);

        public virtual void Dispose()
        {
            for (int i=0; i<PageStatusIndicator.Length; i++)
                PageStatusIndicator[i].PageFlushCloseStatus = new FlushCloseStatus { PageFlushStatus = PMMFlushStatus.Flushed, PageCloseStatus = PMMCloseStatus.Closed };

            TailPageOffset.Page = 0;
            TailPageOffset.Offset = 0;
            SafeReadOnlyAddress = 0;
            ReadOnlyAddress = 0;
            SafeHeadAddress = 0;
            HeadAddress = 0;
            BeginAddress = 1;
        }

        /// <summary>
        /// Get tail address
        /// </summary>
        /// <returns></returns>
        public long GetTailAddress()
        {
            var local = TailPageOffset;
            return ((long)local.Page << LogPageSizeBits) | (uint)local.Offset;
        }

        /// <summary>
        /// Get page
        /// </summary>
        /// <param name="logicalAddress"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetPage(long logicalAddress)
        {
            return (logicalAddress >> LogPageSizeBits);
        }

        /// <summary>
        /// Get page index for page
        /// </summary>
        /// <param name="page"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetPageIndexForPage(long page)
        {
            return (int)(page % BufferSize);
        }

        /// <summary>
        /// Get page index for address
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public int GetPageIndexForAddress(long address)
        {
            return (int)((address >> LogPageSizeBits) % BufferSize);
        }

        /// <summary>
        /// Get capacity (number of pages)
        /// </summary>
        /// <returns></returns>
        public int GetCapacityNumPages()
        {
            return BufferSize;
        }

        /// <summary>
        /// Get start logical address
        /// </summary>
        /// <param name="page"></param>
        /// <returns></returns>
        public long GetStartLogicalAddress(long page)
        {
            return page << LogPageSizeBits;
        }

        /// <summary>
        /// Get page size
        /// </summary>
        /// <returns></returns>
        public long GetPageSize()
        {
            return PageSize;
        }

        /// <summary>
        /// Get offset in page
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public long GetOffsetInPage(long address)
        {
            return address & PageSizeMask;
        }

        /// <summary>
        /// Get offset lag in pages
        /// </summary>
        /// <returns></returns>
        public long GetHeadOffsetLagInPages()
        {
            return HeadOffsetLagSize;
        }

        /// <summary>
        /// Key function used to allocate memory for a specified number of items
        /// </summary>
        /// <param name="numSlots"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Allocate(int numSlots = 1)
        {
            PageOffset localTailPageOffset = default(PageOffset);

            // Determine insertion index.
            // ReSharper disable once CSharpWarnings::CS0420
#pragma warning disable 420
            localTailPageOffset.PageAndOffset = Interlocked.Add(ref TailPageOffset.PageAndOffset, numSlots);
#pragma warning restore 420

            int page = localTailPageOffset.Page;
            int offset = localTailPageOffset.Offset - numSlots;

            #region HANDLE PAGE OVERFLOW
            /* To prove correctness of the following modifications 
             * done to TailPageOffset and the allocation itself, 
             * we should use the fact that only one thread will have any 
             * of the following cases since it is a counter and we spin-wait
             * until the tail is folded onto next page accordingly.
             */
            if (localTailPageOffset.Offset >= PageSize)
            {
                if (offset >= PageSize)
                {
                    //The tail offset value was more than page size before atomic add
                    //We consider that a failed attempt and retry again
                    var spin = new SpinWait();
                    do
                    {
                        //Just to give some more time to the thread
                        // that is handling this overflow
                        while (TailPageOffset.Offset >= PageSize)
                        {
                            spin.SpinOnce();
                        }

                        // ReSharper disable once CSharpWarnings::CS0420
#pragma warning disable 420
                        localTailPageOffset.PageAndOffset = Interlocked.Add(ref TailPageOffset.PageAndOffset, numSlots);
#pragma warning restore 420

                        page = localTailPageOffset.Page;
                        offset = localTailPageOffset.Offset - numSlots;
                    } while (offset >= PageSize);
                }


                if (localTailPageOffset.Offset == PageSize)
                {
                    //Folding over at page boundary
                    localTailPageOffset.Page++;
                    localTailPageOffset.Offset = 0;
                    TailPageOffset = localTailPageOffset;
                }
                else if (localTailPageOffset.Offset >= PageSize)
                {
                    //Overflows not allowed. We allot same space in next page.
                    localTailPageOffset.Page++;
                    localTailPageOffset.Offset = numSlots;
                    TailPageOffset = localTailPageOffset;

                    page = localTailPageOffset.Page;
                    offset = 0;
                }
            }
            #endregion

            long address = (((long)page) << LogPageSizeBits) | ((long)offset);

            // Check if TailPageIndex is appropriate and allocated!
            int pageIndex = page % BufferSize;

            if (TailPageIndex == pageIndex)
            {
                return (address);
            }

            //Invert the address if either the previous page is not flushed or if it is null
            if ((PageStatusIndicator[pageIndex].PageFlushCloseStatus.PageFlushStatus != PMMFlushStatus.Flushed) ||
                (PageStatusIndicator[pageIndex].PageFlushCloseStatus.PageCloseStatus != PMMCloseStatus.Closed) ||
                (!IsAllocated(pageIndex)))
            {
                address = -address;
            }

            // Update the read-only so that we can get more space for the tail
            if (offset == 0)
            {
                if (address >= 0)
                {
                    TailPageIndex = pageIndex;
                    Interlocked.MemoryBarrier();
                }

                long newPage = page + 1;
                int newPageIndex = (int)((page + 1) % BufferSize);

                long tailAddress = (address < 0 ? -address : address);
                PageAlignedShiftReadOnlyAddress(tailAddress);
                PageAlignedShiftHeadAddress(tailAddress);

                if ((!IsAllocated(newPageIndex)))
                {
                    AllocatePage(newPageIndex);
                }
            }

            return (address);
        }

        /// <summary>
        /// If allocator cannot allocate new memory as the head has not shifted or the previous page 
        /// is not yet closed, it allocates but returns the negative address. 
        /// This function is invoked to check if the address previously allocated has become valid to be used
        /// </summary>
        /// <param name="address"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckForAllocateComplete(ref long address)
        {
            if (address >= 0)
            {
                throw new Exception("Address already allocated!");
            }

            PageOffset p = default(PageOffset);
            p.Page = (int)((-address) >> LogPageSizeBits);
            p.Offset = (int)((-address) & PageSizeMask);

            //Check write cache
            int pageIndex = p.Page % BufferSize;
            if (TailPageIndex == pageIndex)
            {
                address = -address;
                return;
            }

            //Check if we can move the head offset
            long currentTailAddress = GetTailAddress();
            PageAlignedShiftHeadAddress(currentTailAddress);

            //Check if I can allocate pageIndex at all
            if ((PageStatusIndicator[pageIndex].PageFlushCloseStatus.PageFlushStatus != PMMFlushStatus.Flushed) ||
                (PageStatusIndicator[pageIndex].PageFlushCloseStatus.PageCloseStatus != PMMCloseStatus.Closed) ||
                (!IsAllocated(pageIndex)))
            {
                return;
            }

            //correct values and set write cache
            address = -address;
            if (p.Offset == 0)
            {
                TailPageIndex = pageIndex;
            }
            return;
        }

        /// <summary>
        /// Used by applications to make the current state of the database immutable quickly
        /// </summary>
        /// <param name="tailAddress"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ShiftReadOnlyToTail(out long tailAddress)
        {
            tailAddress = GetTailAddress();
            long localTailAddress = tailAddress;
            long currentReadOnlyOffset = ReadOnlyAddress;
            if (MonotonicUpdate(ref ReadOnlyAddress, tailAddress, out long oldReadOnlyOffset))
            {
                epoch.BumpCurrentEpoch(() => OnPagesMarkedReadOnly(localTailAddress, false));
            }
        }

        /// <summary>
        /// Shift begin address
        /// </summary>
        /// <param name="oldBeginAddress"></param>
        /// <param name="newBeginAddress"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ShiftBeginAddress(long oldBeginAddress, long newBeginAddress)
        {
            epoch.BumpCurrentEpoch(() 
                => DeleteAddressRange(oldBeginAddress, newBeginAddress));
        }

        protected virtual void DeleteAddressRange(long fromAddress, long toAddress)
        {
            device.DeleteAddressRange(fromAddress, toAddress);
        }

        /// <summary>
        /// Seal: make sure there are no longer any threads writing to the page
        /// Flush: send page to secondary store
        /// </summary>
        /// <param name="newSafeReadOnlyAddress"></param>
        /// <param name="waitForPendingFlushComplete"></param>
        public void OnPagesMarkedReadOnly(long newSafeReadOnlyAddress, bool waitForPendingFlushComplete = false)
        {
            if (MonotonicUpdate(ref SafeReadOnlyAddress, newSafeReadOnlyAddress, out long oldSafeReadOnlyAddress))
            {
                Debug.WriteLine("SafeReadOnly shifted from {0:X} to {1:X}", oldSafeReadOnlyAddress, newSafeReadOnlyAddress);
                long startPage = oldSafeReadOnlyAddress >> LogPageSizeBits;

                long endPage = (newSafeReadOnlyAddress >> LogPageSizeBits);
                int numPages = (int)(endPage - startPage);
                if (numPages > 10)
                {
                    new Thread(
                        () => AsyncFlushPages(startPage, newSafeReadOnlyAddress)).Start();
                }
                else
                {
                    AsyncFlushPages(startPage, newSafeReadOnlyAddress);
                }
            }
        }

        /// <summary>
        /// Action to be performed for when all threads have 
        /// agreed that a page range is closed.
        /// </summary>
        /// <param name="newSafeHeadAddress"></param>
        public void OnPagesClosed(long newSafeHeadAddress)
        {
            if (MonotonicUpdate(ref SafeHeadAddress, newSafeHeadAddress, out long oldSafeHeadAddress))
            {
                Debug.WriteLine("SafeHeadOffset shifted from {0:X} to {1:X}", oldSafeHeadAddress, newSafeHeadAddress);

                for (long closePageAddress = oldSafeHeadAddress; closePageAddress < newSafeHeadAddress; closePageAddress += PageSize)
                {
                    int closePage = (int)((closePageAddress >> LogPageSizeBits) % BufferSize);

                    if (!IsAllocated(closePage))
                    {
                        AllocatePage(closePage);
                    }

                    while (true)
                    {
                        var oldStatus = PageStatusIndicator[closePage].PageFlushCloseStatus;
                        if (oldStatus.PageFlushStatus == PMMFlushStatus.Flushed)
                        {
                            ClearPage(closePage, (closePageAddress >> LogPageSizeBits) == 0);

                            var thisCloseSegment = closePageAddress >> LogSegmentSizeBits;
                            var nextClosePage = (closePageAddress >> LogPageSizeBits) + 1;
                            var nextCloseSegment = nextClosePage >> (LogSegmentSizeBits - LogPageSizeBits);

                            if (thisCloseSegment != nextCloseSegment)
                            {
                                SegmentClosed(thisCloseSegment);
                            }
                        }
                        else
                        {
                            throw new Exception("Impossible");
                        }

                        var newStatus = oldStatus;
                        newStatus.PageCloseStatus = PMMCloseStatus.Closed;
                        if (oldStatus.value == Interlocked.CompareExchange(ref PageStatusIndicator[closePage].PageFlushCloseStatus.value, newStatus.value, oldStatus.value))
                        {
                            break;
                        }
                    }

                    //Necessary to propagate this change to other threads
                    Interlocked.MemoryBarrier();
                }
            }
        }

        protected abstract void SegmentClosed(long segment);

        /// <summary>
        /// Called every time a new tail page is allocated. Here the read-only is 
        /// shifted only to page boundaries unlike ShiftReadOnlyToTail where shifting
        /// can happen to any fine-grained address.
        /// </summary>
        /// <param name="currentTailAddress"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PageAlignedShiftReadOnlyAddress(long currentTailAddress)
        {
            long currentReadOnlyAddress = ReadOnlyAddress;
            long pageAlignedTailAddress = currentTailAddress & ~PageSizeMask;
            long desiredReadOnlyAddress = (pageAlignedTailAddress - ReadOnlyLagAddress);
            if (MonotonicUpdate(ref ReadOnlyAddress, desiredReadOnlyAddress, out long oldReadOnlyAddress))
            {
                Debug.WriteLine("Allocate: Moving read-only offset from {0:X} to {1:X}", oldReadOnlyAddress, desiredReadOnlyAddress);
                epoch.BumpCurrentEpoch(() => OnPagesMarkedReadOnly(desiredReadOnlyAddress));
            }
        }

        /// <summary>
        /// Called whenever a new tail page is allocated or when the user is checking for a failed memory allocation
        /// Tries to shift head address based on the head offset lag size.
        /// </summary>
        /// <param name="currentTailAddress"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PageAlignedShiftHeadAddress(long currentTailAddress)
        {
            //obtain local values of variables that can change
            long currentHeadAddress = HeadAddress;
            long currentFlushedUntilAddress = FlushedUntilAddress;
            long pageAlignedTailAddress = currentTailAddress & ~PageSizeMask;
            long desiredHeadAddress = (pageAlignedTailAddress - HeadOffsetLagAddress);

            long newHeadAddress = desiredHeadAddress;
            if (currentFlushedUntilAddress < newHeadAddress)
            {
                newHeadAddress = currentFlushedUntilAddress;
            }
            newHeadAddress = newHeadAddress & ~PageSizeMask;

            if (MonotonicUpdate(ref HeadAddress, newHeadAddress, out long oldHeadAddress))
            {
                Debug.WriteLine("Allocate: Moving head offset from {0:X} to {1:X}", oldHeadAddress, newHeadAddress);
                epoch.BumpCurrentEpoch(() => OnPagesClosed(newHeadAddress));
            }
        }

        /// <summary>
        /// Every async flush callback tries to update the flushed until address to the latest value possible
        /// Is there a better way to do this with enabling fine-grained addresses (not necessarily at page boundaries)?
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ShiftFlushedUntilAddress()
        {
            long currentFlushedUntilAddress = FlushedUntilAddress;
            long page = GetPage(currentFlushedUntilAddress);

            bool update = false;
            long pageLastFlushedAddress = PageStatusIndicator[(int)(page % BufferSize)].LastFlushedUntilAddress;
            while (pageLastFlushedAddress >= currentFlushedUntilAddress)
            {
                currentFlushedUntilAddress = pageLastFlushedAddress;
                update = true;
                page++;
                pageLastFlushedAddress = PageStatusIndicator[(int)(page % BufferSize)].LastFlushedUntilAddress;
            }

            if (update)
            {
                MonotonicUpdate(ref FlushedUntilAddress, currentFlushedUntilAddress, out long oldFlushedUntilAddress);
            }
        }



        /// <summary>
        /// Used by several functions to update the variable to newValue. Ignores if newValue is smaller or 
        /// than the current value.
        /// </summary>
        /// <param name="variable"></param>
        /// <param name="newValue"></param>
        /// <param name="oldValue"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MonotonicUpdate(ref long variable, long newValue, out long oldValue)
        {
            oldValue = variable;
            while (oldValue < newValue)
            {
                var foundValue = Interlocked.CompareExchange(ref variable, newValue, oldValue);
                if (foundValue == oldValue)
                {
                    return true;
                }
                oldValue = foundValue;
            }
            return false;
        }

        /// <summary>
        /// Reset for recovery
        /// </summary>
        /// <param name="tailAddress"></param>
        /// <param name="headAddress"></param>
        public void RecoveryReset(long tailAddress, long headAddress)
        {
            long tailPage = GetPage(tailAddress);
            long offsetInPage = GetOffsetInPage(tailAddress);
            TailPageOffset.Page = (int)tailPage;
            TailPageOffset.Offset = (int)offsetInPage;
            TailPageIndex = GetPageIndexForPage(TailPageOffset.Page);

            // issue read request to all pages until head lag
            HeadAddress = headAddress;
            SafeHeadAddress = headAddress;
            FlushedUntilAddress = headAddress;
            ReadOnlyAddress = tailAddress;
            SafeReadOnlyAddress = tailAddress;

            for (var addr = headAddress; addr < tailAddress; addr += PageSize)
            {
                var pageIndex = GetPageIndexForAddress(addr);
                PageStatusIndicator[pageIndex].PageFlushCloseStatus.PageCloseStatus = PMMCloseStatus.Open;
            }
        }

        public abstract int GetRecordSize(long physicalAddress);
        public abstract int GetRecordSizeFromLogical(long logicalAddress);

        public abstract int GetAverageRecordSize();
        public abstract int GetInitialRecordSize(ref Key key, int valueLength);

        public AddressInfo* GetKeyAddressInfo(long physicalAddress)
        {
            return (AddressInfo*)((byte*)physicalAddress + RecordInfo.GetLength());
        }

        public abstract AddressInfo* GetValueAddressInfo(long physicalAddress);
        protected abstract void AllocatePage(int index);
        protected abstract bool IsAllocated(int pageIndex);


        /// <summary>
        /// Flush page range to disk
        /// Called when all threads have agreed that a page range is sealed.
        /// </summary>
        /// <param name="startPage"></param>
        /// <param name="untilAddress"></param>
        public void AsyncFlushPages(long startPage, long untilAddress)
        {
            long endPage = (untilAddress >> LogPageSizeBits);
            int numPages = (int)(endPage - startPage);
            long offsetInEndPage = GetOffsetInPage(untilAddress);
            if (offsetInEndPage > 0)
            {
                numPages++;
            }


            /* Request asynchronous writes to the device. If waitForPendingFlushComplete
             * is set, then a CountDownEvent is set in the callback handle.
             */
            for (long flushPage = startPage; flushPage < (startPage + numPages); flushPage++)
            {
                long pageStartAddress = flushPage << LogPageSizeBits;
                long pageEndAddress = (flushPage + 1) << LogPageSizeBits;

                var asyncResult = new PageAsyncFlushResult<Empty>
                {
                    page = flushPage,
                    count = 1
                };
                if (pageEndAddress > untilAddress)
                {
                    asyncResult.partial = true;
                    asyncResult.untilAddress = untilAddress;
                }
                else
                {
                    asyncResult.partial = false;
                    asyncResult.untilAddress = pageEndAddress;

                    // Set status to in-progress
                    PageStatusIndicator[flushPage % BufferSize].PageFlushCloseStatus
                        = new FlushCloseStatus { PageFlushStatus = PMMFlushStatus.InProgress, PageCloseStatus = PMMCloseStatus.Open };
                }

                PageStatusIndicator[flushPage % BufferSize].LastFlushedUntilAddress = -1;

                WriteAsync(flushPage, asyncResult);
            }
        }

        /// <summary>
        /// Flush pages from startPage (inclusive) to endPage (exclusive)
        /// to specified log device and obj device
        /// </summary>
        /// <param name="startPage"></param>
        /// <param name="endPage"></param>
        /// <param name="device"></param>
        /// <param name="objectLogDevice"></param>
        /// <param name="completed"></param>
        public void AsyncFlushPagesToDevice(long startPage, long endPage, IDevice device, IDevice objectLogDevice, out CountdownEvent completed)
        {
            int totalNumPages = (int)(endPage - startPage);
            completed = new CountdownEvent(totalNumPages);

            for (long flushPage = startPage; flushPage < endPage; flushPage++)
            {
                var asyncResult = new PageAsyncFlushResult<Empty>
                {
                    handle = completed,
                    count = 1
                };

                long pageStartAddress = flushPage << LogPageSizeBits;
                long pageEndAddress = (flushPage + 1) << LogPageSizeBits;

                // Intended destination is flushPage
                WriteAsyncToDevice(startPage, flushPage, AsyncFlushPageToDeviceCallback, asyncResult, device, objectLogDevice);
            }
        }

        protected abstract void WriteAsyncToDevice<TContext>(long startPage, long flushPage, IOCompletionCallback callback, PageAsyncFlushResult<TContext> result, IDevice device, IDevice objectLogDevice);

        /// <summary>
        /// IOCompletion callback for page flush
        /// </summary>
        /// <param name="errorCode"></param>
        /// <param name="numBytes"></param>
        /// <param name="overlap"></param>
        private void AsyncFlushPageToDeviceCallback(uint errorCode, uint numBytes, NativeOverlapped* overlap)
        {
            if (errorCode != 0)
            {
                Trace.TraceError("OverlappedStream GetQueuedCompletionStatus error: {0}", errorCode);
            }

            PageAsyncFlushResult<Empty> result = (PageAsyncFlushResult<Empty>)Overlapped.Unpack(overlap).AsyncResult;

            if (Interlocked.Decrement(ref result.count) == 0)
            {
                result.Free();
            }
            Overlapped.Free(overlap);
        }

        /// <summary>
        /// Flush pages asynchronously
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <param name="flushPageStart"></param>
        /// <param name="numPages"></param>
        /// <param name="callback"></param>
        /// <param name="context"></param>
        public void AsyncFlushPages<TContext>(
                                        long flushPageStart,
                                        int numPages,
                                        IOCompletionCallback callback,
                                        TContext context)
        {
            for (long flushPage = flushPageStart; flushPage < (flushPageStart + numPages); flushPage++)
            {
                int pageIndex = GetPageIndexForPage(flushPage);
                var asyncResult = new PageAsyncFlushResult<TContext>()
                {
                    page = flushPage,
                    context = context,
                    count = 1,
                    partial = false,
                    untilAddress = (flushPage + 1) << LogPageSizeBits
                };

                WriteAsync(flushPage, asyncResult);
            }
        }

        protected abstract void ClearPage(int page, bool pageZero);
        protected abstract void WriteAsync<TContext>(long flushPage, PageAsyncFlushResult<TContext> asyncResult);
    }
}