﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Voron.Impl.FreeSpace
{
	/// <summary>
	/// Here we keep data in the following format
	/// [dirty_flag][free_pages_bits][modification_bits]
	/// dirty_flag - an integer value (4 bytes) indicating if buffer is dirty or nor
	/// free_pages_bits - an array of bits that reflect if page is free (true) or not (false)
	/// modification_bits - any modification in a continuous range of 32784 free_pages_bits (which is 4096 [pageSize] bytes of data) is reflected as one bit
	///
	/// Notes: 
	/// - we always allocate twice a much memory as we actually need to track numberOfPages. That's why we calculate maximum numbers of pages that we can track here
	/// </summary>
	public unsafe class UnmanagedBits
	{
		private const int DirtyFlagSizeInBytes = sizeof(int);

		private readonly long sizeInBytes;
		private readonly long capacity;
		private readonly int pageSize;
		private readonly long allocatedPages;

		private byte* rawPtr;
		private int* freePagesPtr;
		private int* modificationBitsPtr;
		private int* dirtyFlagPtr;

		private readonly List<long> internallyCopiedPages = new List<long>();

		public UnmanagedBits(byte* ptr, long startPageNumber, long sizeInBytes, long numberOfPages, int pageSize)
		{
			this.sizeInBytes = sizeInBytes;
			this.pageSize = pageSize;
			this.allocatedPages = this.sizeInBytes/this.pageSize;

			NumberOfTrackedPages = numberOfPages;
			StartPageNumber = startPageNumber;

			var sizeForBits = sizeInBytes - DirtyFlagSizeInBytes;

			capacity = NumberOfBitsForSizeInBytes(sizeForBits);

			AllModificationBits = DivideAndRoundUp(sizeForBits, pageSize);
			MaxNumberOfPages = capacity - NumberOfBitsForSizeInBytes(BytesTakenByModificationBits);
			ModificationBitsInUse = (long)Math.Ceiling((NumberOfTrackedPages / 8f) / this.pageSize);

			Debug.Assert(AllModificationBits >= 1);
			Debug.Assert(MaxNumberOfPages >= 1);
			Debug.Assert(numberOfPages <= MaxNumberOfPages);

			SetBufferPointer(ptr);

			NativeMethods.memset(rawPtr, 0, (int)this.sizeInBytes); // clean all bits

			TotalNumberOfFreePages = 0;
		}

		public long NumberOfTrackedPages { get; private set; }

		public long MaxNumberOfPages { get; private set; }

		public long StartPageNumber { get; private set; }

		public long ModificationBitsInUse { get; private set; }

		public long AllModificationBits { get; private set; }

		public long BytesTakenByModificationBits
		{
			get { return sizeof(int) * DivideAndRoundUp(AllModificationBits, 32); }
		}

		internal long TotalNumberOfFreePages { get; set; }

		public bool IsDirty
		{
			get { return *(dirtyFlagPtr) != 0; }
			private set { *(dirtyFlagPtr) = value ? 1 : 0; }
		}

		public List<long> DirtyPages
		{
			get
			{
				var result = new HashSet<long>(internallyCopiedPages);

				// add pages where free bits were modified
				for (var i = 0; i < ModificationBitsInUse; i++)
				{
					if (GetBit(modificationBitsPtr, ModificationBitsInUse, i) == false)
						continue;

					result.Add(StartPageNumber + i);
				}

				// add last page where IsDirty flag is contained and all pages where modifications bits are contained
				// most of the cases everything will be contained in a one page
				var lastPageNumber = StartPageNumber + allocatedPages - 1;

				var numberOfPagesTakenByModificationBitsAndDirtyFlag = DivideAndRoundUp(BytesTakenByModificationBits + DirtyFlagSizeInBytes, pageSize);

				for (var i = 0; i < numberOfPagesTakenByModificationBitsAndDirtyFlag; i++)
				{
					result.Add(lastPageNumber - i);
				}

				return result.ToList();
			}
		}

		public void InitializeProcessing()
		{
			IsDirty = true;
			ResetModifiedPages();
		}

		public void Processed()
		{
			IsDirty = false;
			internallyCopiedPages.Clear();
		}

		public void MarkPage(long page, bool val)
		{
			SetBit(freePagesPtr, page, val);
			SetBit(modificationBitsPtr, page/(pageSize*8), true); // mark dirty
		}

		public void MarkPages(long startPage, long count, bool val)
		{
			SetBits(freePagesPtr, startPage, count, val);

			var fistModificationBitToSet = startPage/(pageSize*8);
			var lastModificationBitToSet = (startPage + count - 1)/(pageSize*8);
			var numberOfModificationBitsToSet = lastModificationBitToSet - fistModificationBitToSet + 1;

			SetBits(modificationBitsPtr, fistModificationBitToSet, numberOfModificationBitsToSet, true); // mark dirty
		}

		private void ResetModifiedPages()
		{
			SetBits(modificationBitsPtr, 0, AllModificationBits, false);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void SetBit(int* ptr, long pos, bool value)
		{
#if DEBUG
			if (pos < 0 || pos >= capacity)
				throw new ArgumentOutOfRangeException("pos");
#endif
			if (value)
				ptr[pos >> 5] |= (1 << (int)(pos & 31)); // '>> 5' is '/ 32', '& 31' is '% 32'
			else
				ptr[pos >> 5] &= ~(1 << (int)(pos & 31));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void SetBits(int* ptr, long startPosition, long numberOfBitsToSet, bool value)
		{
#if DEBUG
			if (startPosition < 0)
				throw new ArgumentOutOfRangeException("startPosition");
			if (startPosition + numberOfBitsToSet >= capacity)
				throw new ArgumentOutOfRangeException("numberOfBitsToSet");
#endif
			var end = startPosition + numberOfBitsToSet;
			for (var pos = startPosition; pos < end; pos++)
			{
				if (value)
					ptr[pos >> 5] |= (1 << (int)(pos & 31)); // '>> 5' is '/ 32', '& 31' is '% 32'
				else
					ptr[pos >> 5] &= ~(1 << (int)(pos & 31));
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool GetBit(int* ptr, long size, long pos)
		{
#if DEBUG
			if (pos < 0 || pos >= size)
				throw new ArgumentOutOfRangeException("pos");
#endif
			return (ptr[pos >> 5] & (1 << (int)(pos & 31))) != 0;
		}

		public static long CalculateSizeInBytesForAllocation(long numberOfPages, int pageSize)
		{
			var sizeForFreePages = GetSizeInBytesFor(numberOfPages);
			var numberOfModificationBits = DivideAndRoundUp(sizeForFreePages, pageSize);
			var sizeForModificationBits = sizeof(int) * DivideAndRoundUp(numberOfModificationBits, 32);

			return DirtyFlagSizeInBytes + sizeForFreePages + sizeForModificationBits;
		}

		public static long NumberOfBitsForSizeInBytes(long allocatedBytes)
		{
			return allocatedBytes * 8;
		}

		private static long GetSizeInBytesFor(long numberOfBits)
		{
			return sizeof(int) * ((numberOfBits - 1) / 32 + 1);
		}

		public void CopyAllTo(UnmanagedBits other)
		{
			if (sizeInBytes == other.sizeInBytes)
			{
				Debug.Assert(NumberOfTrackedPages == other.NumberOfTrackedPages && AllModificationBits == other.AllModificationBits);

				NativeMethods.memcpy(other.rawPtr, rawPtr, (int) sizeInBytes);
			}
			else
			{
				if (sizeInBytes > other.sizeInBytes)
					throw new InvalidOperationException(
						string.Format(
							"Cannot copy unmanaged bits because the size of a buffer is not enough. The size of buffer that is being copied is {0} while the size of buffer where it should be copied is {1}",
							sizeInBytes, other.sizeInBytes));

				// here we need to split the data that we copy because the size of buffers is different what means
				// that the pointers of the modification bits have different positions relative to the beginning of a buffer

				// copy all free pages
				NativeMethods.memcpy((byte*)other.freePagesPtr, (byte*)freePagesPtr, (int)GetSizeInBytesFor(NumberOfTrackedPages));

				// copy all modification bits and dirty flag from the end
				NativeMethods.memcpy((byte*)other.modificationBitsPtr, (byte*)modificationBitsPtr,
									  (int)(BytesTakenByModificationBits + DirtyFlagSizeInBytes));
			}

			other.NumberOfTrackedPages = NumberOfTrackedPages;

			
			for (int i = 0; i < other.allocatedPages; i++)
			{
				other.internallyCopiedPages.Add(other.StartPageNumber + i);
			}
		}

		public long CopyDirtyBitsTo(UnmanagedBits other, bool[] forcedModificationBitsToCopy = null)
		{
			var copied = 0;

			for (var i = 0; i < ModificationBitsInUse; i++)
			{
				if (GetBit(modificationBitsPtr, ModificationBitsInUse, i) == false && (forcedModificationBitsToCopy == null || forcedModificationBitsToCopy[i] == false))
					continue;

				var toCopy = pageSize;

				if (i == ModificationBitsInUse - 1) // last piece of free bits can take less bytes than pageSize
					toCopy = (int)DivideAndRoundUp(NumberOfTrackedPages - (pageSize * i * 8), 8);

				NativeMethods.memcpy((byte*)other.freePagesPtr + (pageSize * i), (byte*)freePagesPtr + (pageSize * i), toCopy);

				other.internallyCopiedPages.Add(other.StartPageNumber + i);

				copied += toCopy;
			}

			if(copied > 0)
				other.RefreshNumberOfFreePages();

			return copied;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsFree(long pos)
		{
			return GetBit(freePagesPtr, NumberOfTrackedPages, pos);
		}

		private static long DivideAndRoundUp(long numerator, long denominator)
		{
			return (numerator + denominator - 1) / denominator;
		}

		public void IncreaseSize(long newNumberOfPagesToTrack)
		{
			if (MaxNumberOfPages < newNumberOfPagesToTrack)
				throw new InvalidOperationException(
					string.Format(
						"Cannot increase the size of unmanaged bits buffer to {0}, because it can contains only {1} number of bits",
						newNumberOfPagesToTrack, MaxNumberOfPages));

			NumberOfTrackedPages = newNumberOfPagesToTrack;

			ModificationBitsInUse = (long)Math.Ceiling((NumberOfTrackedPages / 8f) / pageSize);
		}

		public void SetBufferPointer(byte* ptr)
		{
			rawPtr = ptr;
			freePagesPtr = (int*)rawPtr;

			var numberOfIntValuesReservedForFreeBits = MaxNumberOfPages / 32;

			modificationBitsPtr = freePagesPtr + numberOfIntValuesReservedForFreeBits;

			dirtyFlagPtr = (int*) (rawPtr + sizeInBytes - DirtyFlagSizeInBytes);
		}

		public void RefreshNumberOfFreePages()
		{
			TotalNumberOfFreePages = 0L;

			for (int i = 0; i < NumberOfTrackedPages; i++)
			{
				if (IsFree(i))
				{
					TotalNumberOfFreePages++;
				}
			}
		}

		internal bool[] GetModificationBits()
		{
			var modificationBits = new bool[ModificationBitsInUse];

			for (int i = 0; i < ModificationBitsInUse; i++)
			{
				modificationBits[i] = GetBit(modificationBitsPtr, ModificationBitsInUse, i);
			}

			return modificationBits;
		}
	}
}