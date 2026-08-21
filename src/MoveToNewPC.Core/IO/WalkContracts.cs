using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.IO
{
    /// <summary>Decides whether a path is excluded before we even look inside it.</summary>
    public interface IPathExclusion
    {
        /// <param name="relativePath">Path relative to the selected root, e.g. "Sub\file.txt".</param>
        /// <param name="fullPath">Full source path (may be in \\?\ form).</param>
        /// <param name="isDirectory">True when the item is a folder rather than a file.</param>
        /// <param name="ruleName">Name of the matching rule, for the report.</param>
        bool IsExcluded(string relativePath, string fullPath, bool isDirectory, out string ruleName);
    }

    /// <summary>Advanced-mode include/size/date filtering, applied to files only.</summary>
    public interface IFileFilter
    {
        bool Accept(FsEntry entry, string relativePath, out string ruleName);
    }

    public sealed class WalkOptions
    {
        /// <summary>
        /// Always false in v1. Vista+ profiles contain compatibility junctions
        /// ("Documents and Settings", "AppData\Local\Application Data", "My Documents")
        /// that point back at their own ancestors; following them is an infinite loop.
        /// </summary>
        public bool FollowReparsePoints;

        public bool IncludeHidden = true;
        public bool IncludeSystem;

        /// <summary>
        /// When false, files whose attributes say a read would trigger a cloud download
        /// are reported as CloudPlaceholder skips instead of being opened.
        /// </summary>
        public bool HydrateCloudFiles;

        /// <summary>When false, EFS-encrypted files are skipped rather than copied unreadable.</summary>
        public bool IncludeEncryptedFiles;

        public IPathExclusion Exclusions;
        public IFileFilter Filter;

        /// <summary>0 means unlimited.</summary>
        public int MaxDepth;

        /// <summary>How often OnProgress fires, in entries. 0 uses the default.</summary>
        public int ProgressInterval = 512;
    }

    /// <summary>
    /// Callbacks from a directory walk. Called on the walking thread; implementations must
    /// not touch WinForms controls directly.
    /// </summary>
    public interface IWalkObserver
    {
        void OnDirectory(FsEntry entry, string relativePath);
        void OnFile(FsEntry entry, string relativePath);
        void OnSkip(string fullPath, string relativePath, bool isDirectory, SkipReason reason, string detail, long length);
        void OnProgress(long entriesSeen, long filesSeen, long bytesSeen);
    }

    /// <summary>Counts only. Used for the background per-user size calculation.</summary>
    public sealed class CountingWalkObserver : IWalkObserver
    {
        public long Files;
        public long Directories;
        public long Bytes;
        public long Skipped;
        private readonly Action<long, long> _progress;

        public CountingWalkObserver() { }

        public CountingWalkObserver(Action<long, long> onProgressFilesBytes)
        {
            _progress = onProgressFilesBytes;
        }

        public void OnDirectory(FsEntry entry, string relativePath) { Directories++; }

        public void OnFile(FsEntry entry, string relativePath)
        {
            Files++;
            Bytes += entry.Length;
        }

        public void OnSkip(string fullPath, string relativePath, bool isDirectory, SkipReason reason, string detail, long length)
        {
            Skipped++;
        }

        public void OnProgress(long entriesSeen, long filesSeen, long bytesSeen)
        {
            if (_progress != null)
            {
                _progress(Files, Bytes);
            }
        }
    }
}
