using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.IO
{
    /// <summary>
    /// Recursive directory enumeration over FindFirstFileW/FindNextFileW with \\?\ paths.
    ///
    /// Iterative rather than recursive on purpose: a real profile can nest deeply enough
    /// (especially node_modules and mail stores) that a recursive walker overflows the
    /// stack before it overflows MAX_PATH.
    ///
    /// The walker never throws for per-item problems. Access denied, a lock, a vanished
    /// file - all of them become an OnSkip callback and the walk continues. One bad file
    /// must never abort a 200 GB migration.
    /// </summary>
    public static class DirectoryWalker
    {
        private sealed class Pending
        {
            public string FullPath;
            public string RelativePath;
            public int Depth;

            public Pending(string fullPath, string relativePath, int depth)
            {
                FullPath = fullPath;
                RelativePath = relativePath;
                Depth = depth;
            }
        }

        /// <summary>
        /// Walks <paramref name="root"/> depth-first. The root itself is not reported;
        /// relative paths are relative to it.
        /// </summary>
        public static void Walk(string root, WalkOptions options, IWalkObserver observer, CancellationToken cancel)
        {
            if (observer == null)
            {
                throw new ArgumentNullException("observer");
            }
            if (options == null)
            {
                options = new WalkOptions();
            }
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            string extendedRoot = LongPath.ToExtended(LongPath.TrimTrailingSeparators(root));

            uint rootAttributes = NativeMethods.GetFileAttributesW(extendedRoot);
            if (rootAttributes == NativeMethods.INVALID_FILE_ATTRIBUTES)
            {
                int error = Marshal.GetLastWin32Error();
                observer.OnSkip(LongPath.ToDisplay(extendedRoot), string.Empty, true,
                                MapError(error), NativeFile.DescribeError(error), 0);
                return;
            }

            if ((rootAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                // A single file selected as a "root" is legitimate in Advanced mode.
                FsEntry single;
                if (TryGetEntry(extendedRoot, out single))
                {
                    HandleFile(single, single.Name, options, observer);
                }
                return;
            }

            if ((rootAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !options.FollowReparsePoints)
            {
                // Selecting a junction as a root is the one case where refusing would be
                // unhelpful, but we still say so rather than pretending we followed it.
                Log.Warn("Selected root is a reparse point and will not be followed: "
                         + LongPath.ToDisplay(extendedRoot));
                observer.OnSkip(LongPath.ToDisplay(extendedRoot), string.Empty, true,
                                SkipReason.ReparsePoint, "Selected folder is a junction or symbolic link", 0);
                return;
            }

            int progressInterval = options.ProgressInterval > 0 ? options.ProgressInterval : 512;
            long entriesSeen = 0;
            long filesSeen = 0;
            long bytesSeen = 0;
            long sinceProgress = 0;

            List<Pending> stack = new List<Pending>();
            stack.Add(new Pending(extendedRoot, string.Empty, 0));

            while (stack.Count > 0)
            {
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                Pending current = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);

                NativeMethods.WIN32_FIND_DATA data;
                string searchPattern = LongPath.Combine(current.FullPath, "*");

                if (searchPattern.Length > LongPath.MaxExtendedPath)
                {
                    observer.OnSkip(LongPath.ToDisplay(current.FullPath), current.RelativePath, true,
                                    SkipReason.PathTooLong,
                                    "Path is " + LongPath.DescribeLength(current.FullPath), 0);
                    continue;
                }

                using (SafeFindHandle find = NativeMethods.FindFirstFileW(searchPattern, out data))
                {
                    if (find.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        // An empty directory reports ERROR_FILE_NOT_FOUND on some volumes;
                        // that is not a problem worth reporting.
                        if (error != NativeMethods.ERROR_FILE_NOT_FOUND
                            && error != NativeMethods.ERROR_NO_MORE_FILES)
                        {
                            observer.OnSkip(LongPath.ToDisplay(current.FullPath), current.RelativePath, true,
                                            MapError(error), NativeFile.DescribeError(error), 0);
                        }
                        continue;
                    }

                    do
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            return;
                        }

                        if (IsDotEntry(data.cFileName))
                        {
                            continue;
                        }

                        entriesSeen++;
                        sinceProgress++;

                        FsEntry entry = FsEntry.FromFindData(current.FullPath, ref data);
                        string relative = current.RelativePath.Length == 0
                                          ? entry.Name
                                          : current.RelativePath + "\\" + entry.Name;

                        if (entry.FullPath.Length > LongPath.MaxExtendedPath)
                        {
                            observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, entry.IsDirectory,
                                            SkipReason.PathTooLong,
                                            "Path is " + LongPath.DescribeLength(entry.FullPath), entry.Length);
                            continue;
                        }

                        if (entry.IsDirectory)
                        {
                            if (!AllowedByAttributes(entry, options, observer, relative, true))
                            {
                                continue;
                            }

                            // The one that eats migration tools alive: Vista+ profiles carry
                            // compatibility junctions ("Documents and Settings",
                            // "AppData\Local\Application Data", "My Documents") that point at
                            // their own ancestors. Descending is an infinite loop.
                            if (entry.IsReparsePoint && !options.FollowReparsePoints)
                            {
                                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, true,
                                                SkipReason.ReparsePoint,
                                                DescribeReparseTag(entry.ReparseTag), 0);
                                continue;
                            }

                            string rule;
                            if (options.Exclusions != null
                                && options.Exclusions.IsExcluded(relative, entry.FullPath, true, out rule))
                            {
                                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, true,
                                                SkipReason.Excluded, rule, 0);
                                continue;
                            }

                            observer.OnDirectory(entry, relative);

                            if (options.MaxDepth <= 0 || current.Depth + 1 < options.MaxDepth)
                            {
                                stack.Add(new Pending(entry.FullPath, relative, current.Depth + 1));
                            }
                        }
                        else
                        {
                            filesSeen++;
                            bytesSeen += entry.Length;
                            HandleFile(entry, relative, options, observer);
                        }

                        if (sinceProgress >= progressInterval)
                        {
                            sinceProgress = 0;
                            observer.OnProgress(entriesSeen, filesSeen, bytesSeen);
                        }
                    }
                    while (NativeMethods.FindNextFileW(find, out data));

                    int endError = Marshal.GetLastWin32Error();
                    if (endError != NativeMethods.ERROR_NO_MORE_FILES && endError != NativeMethods.ERROR_SUCCESS)
                    {
                        observer.OnSkip(LongPath.ToDisplay(current.FullPath), current.RelativePath, true,
                                        MapError(endError),
                                        "Enumeration stopped early: " + NativeFile.DescribeError(endError), 0);
                    }
                }
            }

            observer.OnProgress(entriesSeen, filesSeen, bytesSeen);
        }

        private static void HandleFile(FsEntry entry, string relative, WalkOptions options, IWalkObserver observer)
        {
            if (!AllowedByAttributes(entry, options, observer, relative, false))
            {
                return;
            }

            // A file-level symlink points somewhere we were not asked to copy from.
            if (entry.IsReparsePoint && !options.FollowReparsePoints)
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, false,
                                SkipReason.ReparsePoint, DescribeReparseTag(entry.ReparseTag), entry.Length);
                return;
            }

            string rule;
            if (options.Exclusions != null
                && options.Exclusions.IsExcluded(relative, entry.FullPath, false, out rule))
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, false,
                                SkipReason.Excluded, rule, entry.Length);
                return;
            }

            // Reading a placeholder silently pulls the whole file down from OneDrive or
            // Dropbox - potentially many gigabytes over someone's metered connection. Skip
            // and report unless the operator explicitly opted in.
            if (entry.IsCloudPlaceholder && !options.HydrateCloudFiles)
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, false,
                                SkipReason.CloudPlaceholder,
                                "Online-only file; enable \"download cloud files\" to include it", entry.Length);
                return;
            }

            // EFS files are decryptable only with the original user's key on the original
            // machine. Copying the ciphertext produces a file nobody can open.
            if (entry.IsEncrypted && !options.IncludeEncryptedFiles)
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, false,
                                SkipReason.Encrypted,
                                "EFS-encrypted; it would not open on the new PC", entry.Length);
                return;
            }

            if (options.Filter != null && !options.Filter.Accept(entry, relative, out rule))
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, false,
                                SkipReason.FilteredOut, rule, entry.Length);
                return;
            }

            observer.OnFile(entry, relative);
        }

        private static bool AllowedByAttributes(FsEntry entry, WalkOptions options, IWalkObserver observer,
                                                string relative, bool isDirectory)
        {
            if (entry.IsHidden && !options.IncludeHidden)
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, isDirectory,
                                SkipReason.Excluded, "Hidden", entry.Length);
                return false;
            }

            if (entry.IsSystem && !options.IncludeSystem)
            {
                observer.OnSkip(LongPath.ToDisplay(entry.FullPath), relative, isDirectory,
                                SkipReason.Excluded, "System", entry.Length);
                return false;
            }

            return true;
        }

        private static bool IsDotEntry(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            if (name.Length == 1 && name[0] == '.')
            {
                return true;
            }
            return name.Length == 2 && name[0] == '.' && name[1] == '.';
        }

        // Reparse tags worth naming in a report; anything else is reported numerically.
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
        private const uint IO_REPARSE_TAG_DEDUP = 0x80000013;
        private const uint IO_REPARSE_TAG_ONEDRIVE = 0x80000021;
        private const uint IO_REPARSE_TAG_CLOUD = 0x9000001A;
        private const uint IO_REPARSE_TAG_APPEXECLINK = 0x8000001B;

        private static string DescribeReparseTag(uint tag)
        {
            switch (tag)
            {
                case IO_REPARSE_TAG_MOUNT_POINT:
                    return "Junction (not followed - these point back into the profile and loop)";
                case IO_REPARSE_TAG_SYMLINK:
                    return "Symbolic link (not followed)";
                case IO_REPARSE_TAG_DEDUP:
                    return "Deduplicated file";
                case IO_REPARSE_TAG_ONEDRIVE:
                case IO_REPARSE_TAG_CLOUD:
                    return "Cloud storage placeholder (not followed)";
                case IO_REPARSE_TAG_APPEXECLINK:
                    return "Store app execution alias (not followed)";
                default:
                    return tag == 0
                           ? "Reparse point (not followed)"
                           : "Reparse point 0x" + tag.ToString("X8",
                                 System.Globalization.CultureInfo.InvariantCulture) + " (not followed)";
            }
        }

        internal static SkipReason MapError(int win32Error)
        {
            switch (win32Error)
            {
                case NativeMethods.ERROR_ACCESS_DENIED:
                    return SkipReason.AccessDenied;
                case NativeMethods.ERROR_SHARING_VIOLATION:
                case NativeMethods.ERROR_LOCK_VIOLATION:
                    return SkipReason.Locked;
                case NativeMethods.ERROR_FILE_NOT_FOUND:
                case NativeMethods.ERROR_PATH_NOT_FOUND:
                    return SkipReason.NotFound;
                case NativeMethods.ERROR_FILENAME_EXCED_RANGE:
                    return SkipReason.PathTooLong;
                case NativeMethods.ERROR_INVALID_NAME:
                    return SkipReason.InvalidPath;
                case NativeMethods.ERROR_DISK_FULL:
                case NativeMethods.ERROR_HANDLE_DISK_FULL:
                    return SkipReason.InsufficientSpace;
                default:
                    return SkipReason.UnknownError;
            }
        }

        /// <summary>Stats a single path into an <see cref="FsEntry"/> without enumerating.</summary>
        public static bool TryGetEntry(string path, out FsEntry entry)
        {
            entry = null;
            NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data;
            int error;
            if (!NativeFile.TryGetInfo(path, out data, out error))
            {
                return false;
            }

            entry = new FsEntry();
            entry.FullPath = LongPath.ToExtended(path);
            entry.Name = LongPath.GetFileName(LongPath.TrimTrailingSeparators(path));
            entry.Attributes = data.dwFileAttributes;
            entry.Length = data.FileSize;
            entry.CreationTimeUtc = data.ftCreationTime.ToTicks();
            entry.LastAccessTimeUtc = data.ftLastAccessTime.ToTicks();
            entry.LastWriteTimeUtc = data.ftLastWriteTime.ToTicks();
            entry.ReparseTag = 0;
            return true;
        }
    }
}
