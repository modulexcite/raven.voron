﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voron.Impl.Paging;

namespace Voron.Impl.Journal
{
	public unsafe class PureMemoryJournalWriter : IJournalWriter
	{
		internal class Buffer
		{
			public byte* Pointer;
			public long Size;
			public IntPtr Handle;
		}

		private readonly List<Buffer> _buffers = new List<Buffer>();
		private long _lastPos;

		private readonly ReaderWriterLockSlim _locker = new ReaderWriterLockSlim();

		public PureMemoryJournalWriter(long journalSize)
		{
			NumberOfAllocatedPages = journalSize;
		}

		public long NumberOfAllocatedPages { get; private set; }
		
		public IVirtualPager CreatePager()
		{
			_locker.EnterReadLock();
			try
			{
				return new FragmentedPureMemoryPager(_buffers.ToArray());
			}
			finally
			{
				_locker.ExitReadLock();
			}		
		}

		public void Dispose()
		{
			foreach (var buffer in _buffers)
			{
				Marshal.FreeHGlobal(buffer.Handle);
			}
		}

		public Task WriteGatherAsync(long position, byte*[] pages)
		{
			_locker.EnterWriteLock();
			try
			{
				if (position != _lastPos + 1)
					throw new InvalidOperationException("Journal writes must be to the next location in the journal");

				var size = pages.Length * AbstractPager.PageSize;
				_lastPos += size;

				var handle = Marshal.AllocHGlobal(size);

				var buffer = new Buffer
				{
					Handle = handle,
					Pointer = (byte*)handle.ToPointer(),
					Size = size
				};
				_buffers.Add(buffer);

				for (int index = 0; index < pages.Length; index++)
				{
					NativeMethods.memcpy(buffer.Pointer + (index * AbstractPager.PageSize), pages[index], AbstractPager.PageSize);
				}

				return Task.FromResult(1);
			}
			finally
			{
				_locker.ExitWriteLock();
			}
		}
	}
}