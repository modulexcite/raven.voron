﻿// -----------------------------------------------------------------------
//  <copyright file="WriteAheadLog.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Voron.Impl.FileHeaders;
using Voron.Trees;

namespace Voron.Impl.Log
{
	public unsafe class WriteAheadLog : IDisposable
	{
		private readonly StorageEnvironment _env;
		private readonly Func<string, IVirtualPager> _createLogFilePager;
		private readonly IVirtualPager _dataPager;
		private readonly List<LogFile> _logFiles = new List<LogFile>();
		private readonly List<LogFile> _scheduledToFlush = new List<LogFile>();
		private readonly Func<long, string> _logName = number => string.Format("{0:D19}.txlog", number);
		private readonly bool _disposeLogFiles;
		private LogFile _currentFile;
		private LogFile _splitLogFile;
		private long _logIndex = -1;
		private FileHeader* _fileHeader;
		private IntPtr _inMemoryHeader;
		private long _dataFlushCounter = 0;

		public WriteAheadLog(StorageEnvironment env, Func<string, IVirtualPager> createLogFilePager, IVirtualPager dataPager, bool disposeLogFiles = true)
		{
			_env = env;
			_createLogFilePager = createLogFilePager;
			_dataPager = dataPager;
			_disposeLogFiles = disposeLogFiles;
			_fileHeader = GetEmptyFileHeader();
		}

		public long LogFileSize
		{
			get { return 64*1024*1024; }
		}

		private LogFile NextFile(Transaction tx)
		{
			_logIndex++;

			var logPager = _createLogFilePager(_logName(_logIndex));
			logPager.AllocateMorePages(tx, LogFileSize);

			var log = new LogFile(logPager, _logIndex);

			_logFiles.Add(log);

			UpdateLogInfo();
			WriteFileHeader(tx);

			return log;
		}

		public bool TryRecover(FileHeader* fileHeader, out TransactionHeader* lastTxHeader)
		{
			_fileHeader = fileHeader;
			var logInfo = fileHeader->LogInfo;

			lastTxHeader = null;

			if (logInfo.LogFilesCount == 0)
			{
				return false;
			}

			for (var logNumber = logInfo.RecentLog - logInfo.LogFilesCount + 1; logNumber <= logInfo.RecentLog; logNumber++)
			{
				_logFiles.Add(new LogFile(_createLogFilePager(_logName(logNumber)), logNumber));
			}

			foreach (var logItem in _logFiles)
			{
				long startRead = 0;

				if (logItem.Number == logInfo.LastSyncedLog)
					startRead = logInfo.LastSyncedPage + 1;

				lastTxHeader = logItem.RecoverAndValidate(startRead, lastTxHeader);
			}

			_logIndex = logInfo.RecentLog;
			_dataFlushCounter = logInfo.DataFlushCounter + 1;

			return true;
		}

		public void UpdateLogInfo()
		{
			_fileHeader->LogInfo.RecentLog = _logFiles.Count > 0 ? _logIndex : -1;
			_fileHeader->LogInfo.LogFilesCount = _logFiles.Count;
			_fileHeader->LogInfo.DataFlushCounter = _dataFlushCounter;
		}

		public void UpdateFileHeaderAfterDataFileSync(LogFile lastSyncedLog)
		{
			_fileHeader->TransactionId = lastSyncedLog.LastCommittedTransactionId;
			_fileHeader->LastPageNumber = lastSyncedLog.LastPageNumberOfLastCommittedTransaction;

			_fileHeader->LogInfo.LastSyncedLog = lastSyncedLog.Number;
			_fileHeader->LogInfo.LastSyncedPage = lastSyncedLog.LastSyncedPage;
			_fileHeader->LogInfo.DataFlushCounter = _dataFlushCounter;

			_env.FreeSpaceHandling.CopyStateTo(&_fileHeader->FreeSpace);
			_env.Root.State.CopyTo(&_fileHeader->Root);
		}

		internal void WriteFileHeader(Transaction tx, long? pageToWriteHeader = null)
		{
			var fileHeaderPage = _dataPager.TempPage;

			if (pageToWriteHeader == null)
				fileHeaderPage.PageNumber = _dataFlushCounter & 1;
			else
				fileHeaderPage.PageNumber = pageToWriteHeader.Value;

			var header = ((FileHeader*)fileHeaderPage.Base + Constants.PageHeaderSize);

			header->MagicMarker = Constants.MagicMarker;
			header->Version = Constants.CurrentVersion;
			header->TransactionId = _fileHeader->TransactionId;
			header->LastPageNumber = _fileHeader->LastPageNumber;
			header->FreeSpace = _fileHeader->FreeSpace;
			header->LogInfo = _fileHeader->LogInfo;
			header->Root = _fileHeader->Root;

			_dataPager.Write(tx, fileHeaderPage);
			_dataPager.Sync();
		}

		public void TransactionBegin(Transaction tx)
		{
			if(_currentFile == null)
				 _currentFile = NextFile(tx);

			if(_currentFile.AvailablePages == 0) // it must have at least one page for the transaction header
			{
				//TODO we need to write a test for this edge condition

				var fullLogFile = _currentFile;
				_currentFile = NextFile(tx);
				ScheduleFlush(fullLogFile);
			}

			_currentFile.TransactionBegin(tx);
		}

		public void TransactionCommit(Transaction tx)
		{
			_currentFile.TransactionCommit(tx);

			if (_splitLogFile != null)
			{
				_splitLogFile.TransactionCommit(tx);
				ScheduleFlush(_splitLogFile);
				_splitLogFile = null;
			}
		}

		public Page ReadPage(long pageNumber)
		{
			// read log files from the back to get the most recent version of page
			for (var i = _logFiles.Count - 1; i >= 0; i--)
			{
				var page = _logFiles[i].ReadPage(pageNumber);
				if (page != null)
					return page;
			}

			return null;
		}

		public Page Allocate(Transaction tx, long startPage, int numberOfPages)
		{
			if (_currentFile.AvailablePages < numberOfPages)
			{
				if (_splitLogFile != null) // we are already in a split transaction and don't allow to spread a transaction over more than two log files
					throw new InvalidOperationException(
						"Transaction attempted to put data in more than two log files. It's not allowed. The transaction is too large.");

				// here we need to mark that transaction is split in both log files
				// it will have th following transaction markers in the headers
				// log_1: [Start|Split] log_2: [Split|End]

				_currentFile.TransactionSplit(tx);
				_splitLogFile = _currentFile;

				_currentFile = NextFile(tx);

				_currentFile.TransactionSplit(tx);
			}

			return _currentFile.Allocate(startPage, numberOfPages);
		}

		public void Sync()
		{
			if (_splitLogFile != null)
			{
				_splitLogFile.Sync();
			}

			_currentFile.Sync();
		}

		public void ApplyLogsToDataFile(Transaction tx)
		{
			if(_scheduledToFlush.Count == 0)
				return;

			var pagesToWrite = new Dictionary<long, Page>();

			// read from the end in order to write only the most recent version of page
			for (int i = _scheduledToFlush.Count - 1; i >= 0; i--)
			{
				foreach (var pageNumber in _logFiles[i].ModifiedPageNumbers)
				{
					if (pagesToWrite.ContainsKey(pageNumber) == false)
					{
						pagesToWrite[pageNumber] = _scheduledToFlush[i].ReadPage(pageNumber);
					}
				}
			}

			var sortedPages = pagesToWrite.OrderBy(x => x.Key).Select(x => x.Value).ToList();

			if(sortedPages.Count == 0)
				return;

			foreach (var page in sortedPages)
			{
				_dataPager.Write(tx, page);
			}

			_dataPager.Sync();

			UpdateFileHeaderAfterDataFileSync(_scheduledToFlush.Last());

			foreach (var logFile in _scheduledToFlush)
			{
				_logFiles.Remove(logFile);
				logFile.Dispose();
			}

			UpdateLogInfo();

			WriteFileHeader(tx);

			_dataFlushCounter++;
		}

		private void ScheduleFlush(LogFile log)
		{
			_scheduledToFlush.Add(log);
		}

		public void Dispose()
		{
			if (_inMemoryHeader != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(_inMemoryHeader);
				_inMemoryHeader = IntPtr.Zero;
			}

			if(_disposeLogFiles)
			{
				foreach (var logFile in _logFiles)
				{
					logFile.Dispose();
				}
			}

			_logFiles.Clear();
		}

		private FileHeader* GetEmptyFileHeader()
		{
			if(_inMemoryHeader == IntPtr.Zero)
				_inMemoryHeader = Marshal.AllocHGlobal(_dataPager.PageSize);

			var header = (FileHeader*) _inMemoryHeader;

			header->MagicMarker = Constants.MagicMarker;
			header->Version = Constants.CurrentVersion;
			header->TransactionId = 0;
			header->LastPageNumber = 1;
			header->FreeSpace.FirstBufferPageNumber = -1;
			header->FreeSpace.SecondBufferPageNumber = -1;
			header->FreeSpace.NumberOfTrackedPages = 0;
			header->FreeSpace.NumberOfPagesTakenForTracking = 0;
			header->FreeSpace.PageSize = -1;
			header->FreeSpace.Checksum = 0;
			header->Root.RootPageNumber = -1;
			header->LogInfo.DataFlushCounter = -1;
			header->LogInfo.RecentLog = -1;
			header->LogInfo.LogFilesCount = 0;
			header->LogInfo.LastSyncedLog = -1;
			header->LogInfo.LastSyncedPage = -1;

			return header;
		}
	}
}