﻿using System;
using System.Collections.Generic;
using Voron.Trees;

namespace Voron.Impl
{
    public unsafe interface IVirtualPager : IDisposable
    {
        PagerState PagerState { get; }

		byte* AcquirePagePointer(long pageNumber);
        Page Get(Transaction tx, long pageNumber, bool errorOnChange = false);
		void AllocateMorePages(Transaction tx, long newLength);

        Page TempPage { get; }

        long NumberOfAllocatedPages { get; }
        int PageSize { get; }
        int MaxNodeSize { get; }
        int PageMaxSpace { get; }
        int PageMinSpace { get; }

		void Flush(List<long> sortedPagesToFlush);
		void Flush(long headerPageId);

	    void Sync();

        PagerState TransactionBegan();

        void EnsureContinuous(Transaction tx, long requestedPageNumber, int pageCount);
        void Write(Page page);
    }
}