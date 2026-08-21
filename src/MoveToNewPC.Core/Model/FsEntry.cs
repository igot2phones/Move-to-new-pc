using System;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.Model
{
    /// <summary>
    /// One directory entry as reported by FindFirstFileW/FindNextFileW. Deliberately a
    /// class with public fields and a cheap constructor: profile scans produce millions
    /// of these and FileInfo would cost an extra syscall each.
    ///
    /// All three timestamps are FILETIME ticks (100ns since 1601 UTC), not DateTime ticks.
    /// </summary>
    public sealed class FsEntry
    {
        public string FullPath;
        public string Name;
        public uint Attributes;
        public long Length;
        public long CreationTimeUtc;
        public long LastAccessTimeUtc;
        public long LastWriteTimeUtc;
        /// <summary>Reparse tag, valid only when <see cref="IsReparsePoint"/>.</summary>
        public uint ReparseTag;

        public bool IsDirectory
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0; }
        }

        public bool IsReparsePoint
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0; }
        }

        public bool IsHidden
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0; }
        }

        public bool IsSystem
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_SYSTEM) != 0; }
        }

        public bool IsReadOnly
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_READONLY) != 0; }
        }

        public bool IsEncrypted
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_ENCRYPTED) != 0; }
        }

        public bool IsSparse
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE) != 0; }
        }

        public bool IsCompressed
        {
            get { return (Attributes & NativeMethods.FILE_ATTRIBUTE_COMPRESSED) != 0; }
        }

        /// <summary>
        /// True when reading the file would pull it down from a cloud provider. Covers the
        /// classic OFFLINE bit and the Windows 10 cloud-filter bits that OneDrive
        /// files-on-demand actually uses.
        /// </summary>
        public bool IsCloudPlaceholder
        {
            get
            {
                return (Attributes & (NativeMethods.FILE_ATTRIBUTE_OFFLINE
                                      | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN
                                      | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;
            }
        }

        /// <summary>Attributes worth carrying to the new machine. ACLs are deliberately absent.</summary>
        public uint PortableAttributes
        {
            get
            {
                return Attributes & (NativeMethods.FILE_ATTRIBUTE_READONLY
                                     | NativeMethods.FILE_ATTRIBUTE_HIDDEN
                                     | NativeMethods.FILE_ATTRIBUTE_ARCHIVE);
            }
        }

        public static FsEntry FromFindData(string parentPath, ref NativeMethods.WIN32_FIND_DATA data)
        {
            FsEntry e = new FsEntry();
            e.Name = data.cFileName;
            e.FullPath = LongPath.Combine(parentPath, data.cFileName);
            e.Attributes = data.dwFileAttributes;
            e.Length = data.FileSize;
            e.CreationTimeUtc = data.ftCreationTime.ToTicks();
            e.LastAccessTimeUtc = data.ftLastAccessTime.ToTicks();
            e.LastWriteTimeUtc = data.ftLastWriteTime.ToTicks();
            e.ReparseTag = (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0
                           ? data.dwReserved0 : 0;
            return e;
        }

        public DateTime LastWriteDateTimeUtc
        {
            get { return FileTimeToDateTime(LastWriteTimeUtc); }
        }

        public static DateTime FileTimeToDateTime(long fileTime)
        {
            if (fileTime <= 0)
            {
                return DateTime.MinValue;
            }
            try
            {
                return DateTime.FromFileTimeUtc(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        public static long DateTimeToFileTime(DateTime utc)
        {
            try
            {
                return utc.ToFileTimeUtc();
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }
    }

    /// <summary>An item that did not make it, plus why. Feeds the report table directly.</summary>
    public sealed class SkippedItem
    {
        public string Path;
        public bool IsDirectory;
        public SkipReason Reason;
        public string Detail;
        public long Length;

        public SkippedItem() { }

        public SkippedItem(string path, bool isDirectory, SkipReason reason, string detail, long length)
        {
            Path = path;
            IsDirectory = isDirectory;
            Reason = reason;
            Detail = detail;
            Length = length;
        }
    }

    /// <summary>Per-user known folders. Resolved from the registry, never hardcoded.</summary>
    public enum KnownFolder
    {
        Desktop = 0,
        Documents,
        Downloads,
        Pictures,
        Music,
        Videos,
        Favorites,
        Links,
        Contacts,
        SavedGames,
        Searches
    }
}
