using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.IO
{
    /// <summary>
    /// File primitives built directly on CreateFileW and friends so they work past
    /// MAX_PATH. .NET 4.0's System.IO cannot be used here: it validates path length in
    /// managed code and throws PathTooLongException before the syscall ever happens.
    ///
    /// Every method takes a normal or extended path and applies LongPath.ToExtended itself.
    /// Methods that can legitimately fail return bool plus a Win32 error rather than
    /// throwing, because "skip this file and keep going" is the common case.
    /// </summary>
    public static class NativeFile
    {
        public const int DefaultChunkSize = 512 * 1024;

        public static uint GetAttributes(string path)
        {
            return NativeMethods.GetFileAttributesW(LongPath.ToExtended(path));
        }

        public static bool TryGetInfo(string path, out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data, out int error)
        {
            error = 0;
            if (NativeMethods.GetFileAttributesExW(LongPath.ToExtended(path),
                                                   NativeMethods.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard,
                                                   out data))
            {
                return true;
            }
            error = Marshal.GetLastWin32Error();
            return false;
        }

        public static bool Exists(string path)
        {
            return GetAttributes(path) != NativeMethods.INVALID_FILE_ATTRIBUTES;
        }

        public static bool FileExists(string path)
        {
            uint attrs = GetAttributes(path);
            return attrs != NativeMethods.INVALID_FILE_ATTRIBUTES
                   && (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0;
        }

        public static bool DirectoryExists(string path)
        {
            uint attrs = GetAttributes(path);
            return attrs != NativeMethods.INVALID_FILE_ATTRIBUTES
                   && (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
        }

        /// <summary>
        /// Opens a source file for reading with the most permissive share mode there is,
        /// so files an application already has open can still be read.
        /// <paramref name="allowRecall"/> false adds FILE_FLAG_OPEN_NO_RECALL, which stops
        /// the open itself from dragging a cloud placeholder down from the network.
        /// </summary>
        public static SafeFileHandle OpenReadHandle(string path, bool allowRecall, out int error)
        {
            uint flags = NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN;
            if (!allowRecall)
            {
                flags |= NativeMethods.FILE_FLAG_OPEN_NO_RECALL;
            }

            SafeFileHandle handle = NativeMethods.CreateFileW(
                LongPath.ToExtended(path),
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_ALL,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                flags,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return null;
            }

            error = 0;
            return handle;
        }

        public static FileStream OpenRead(string path, bool allowRecall, int bufferSize, out int error)
        {
            SafeFileHandle handle = OpenReadHandle(path, allowRecall, out error);
            if (handle == null)
            {
                return null;
            }

            try
            {
                return new FileStream(handle, FileAccess.Read, bufferSize, false);
            }
            catch (Exception)
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>Creates or truncates a destination file. Fails if the parent is missing.</summary>
        public static SafeFileHandle CreateWriteHandle(string path, bool overwrite, out int error)
        {
            SafeFileHandle handle = NativeMethods.CreateFileW(
                LongPath.ToExtended(path),
                NativeMethods.GENERIC_WRITE | NativeMethods.FILE_WRITE_ATTRIBUTES,
                NativeMethods.FILE_SHARE_READ,
                IntPtr.Zero,
                overwrite ? NativeMethods.CREATE_ALWAYS : NativeMethods.CREATE_NEW,
                NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return null;
            }

            error = 0;
            return handle;
        }

        public static FileStream CreateWrite(string path, bool overwrite, int bufferSize, out int error)
        {
            SafeFileHandle handle = CreateWriteHandle(path, overwrite, out error);
            if (handle == null)
            {
                return null;
            }

            try
            {
                return new FileStream(handle, FileAccess.Write, bufferSize, false);
            }
            catch (Exception)
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing file for append/resume. Returns null with the Win32 error when
        /// it does not exist.
        /// </summary>
        public static FileStream OpenForResume(string path, int bufferSize, out int error)
        {
            SafeFileHandle handle = NativeMethods.CreateFileW(
                LongPath.ToExtended(path),
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE | NativeMethods.FILE_WRITE_ATTRIBUTES,
                NativeMethods.FILE_SHARE_READ,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return null;
            }

            error = 0;
            try
            {
                return new FileStream(handle, FileAccess.ReadWrite, bufferSize, false);
            }
            catch (Exception)
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Applies creation/access/write times to an open handle. Pass 0 for "leave alone".
        /// Timestamps are FILETIME ticks, not DateTime ticks.
        /// </summary>
        public static bool SetTimes(SafeFileHandle handle, long creationUtc, long lastAccessUtc, long lastWriteUtc)
        {
            long c = creationUtc;
            long a = lastAccessUtc;
            long w = lastWriteUtc;
            return NativeMethods.SetFileTime(handle, ref c, ref a, ref w);
        }

        /// <summary>Same as the handle overload of SetTimes, but opens the path itself.</summary>
        public static bool SetTimes(string path, long creationUtc, long lastAccessUtc, long lastWriteUtc, out int error)
        {
            SafeFileHandle handle = NativeMethods.CreateFileW(
                LongPath.ToExtended(path),
                NativeMethods.FILE_WRITE_ATTRIBUTES,
                NativeMethods.FILE_SHARE_ALL,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                // BACKUP_SEMANTICS so this works on directories too; OPEN_REPARSE_POINT so
                // we stamp the link rather than whatever it points at.
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return false;
            }

            try
            {
                bool ok = SetTimes(handle, creationUtc, lastAccessUtc, lastWriteUtc);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }
            finally
            {
                handle.Dispose();
            }
        }

        public static bool SetAttributes(string path, uint attributes, out int error)
        {
            if (NativeMethods.SetFileAttributesW(LongPath.ToExtended(path), attributes))
            {
                error = 0;
                return true;
            }
            error = Marshal.GetLastWin32Error();
            return false;
        }

        public static bool Delete(string path, out int error)
        {
            string extended = LongPath.ToExtended(path);
            if (NativeMethods.DeleteFileW(extended))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_ACCESS_DENIED)
            {
                // Most likely read-only; clear the bit and try once more.
                uint attrs = NativeMethods.GetFileAttributesW(extended);
                if (attrs != NativeMethods.INVALID_FILE_ATTRIBUTES
                    && (attrs & NativeMethods.FILE_ATTRIBUTE_READONLY) != 0)
                {
                    NativeMethods.SetFileAttributesW(extended, attrs & ~NativeMethods.FILE_ATTRIBUTE_READONLY);
                    if (NativeMethods.DeleteFileW(extended))
                    {
                        error = 0;
                        return true;
                    }
                    error = Marshal.GetLastWin32Error();
                }
            }
            return false;
        }

        public static bool Move(string source, string destination, bool replaceExisting, out int error)
        {
            uint flags = NativeMethods.MOVEFILE_COPY_ALLOWED;
            if (replaceExisting)
            {
                flags |= NativeMethods.MOVEFILE_REPLACE_EXISTING;
            }

            if (NativeMethods.MoveFileExW(LongPath.ToExtended(source), LongPath.ToExtended(destination), flags))
            {
                error = 0;
                return true;
            }
            error = Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>
        /// Creates a directory and every missing parent. Walks upward first because
        /// CreateDirectoryW does not create intermediate levels.
        /// </summary>
        public static bool CreateDirectoryRecursive(string path, out int error)
        {
            error = 0;
            if (string.IsNullOrEmpty(path))
            {
                error = NativeMethods.ERROR_INVALID_NAME;
                return false;
            }

            string extended = LongPath.ToExtended(LongPath.TrimTrailingSeparators(path));
            if (DirectoryExists(extended))
            {
                return true;
            }

            System.Collections.Generic.List<string> stack = new System.Collections.Generic.List<string>();
            string current = extended;
            while (!string.IsNullOrEmpty(current))
            {
                if (DirectoryExists(current))
                {
                    break;
                }
                if (IsExtendedRoot(current))
                {
                    break;
                }
                stack.Add(current);
                string parent = LongPath.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.Ordinal))
                {
                    break;
                }
                current = parent;
            }

            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (!NativeMethods.CreateDirectoryW(stack[i], IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_ALREADY_EXISTS)
                    {
                        continue;
                    }
                    error = err;
                    return false;
                }
            }

            return true;
        }

        private static bool IsExtendedRoot(string extendedPath)
        {
            // \\?\C:\  or  \\?\UNC\server\share
            string body = LongPath.ToDisplay(extendedPath);
            if (body.Length <= 3 && body.Length >= 2 && body[1] == ':')
            {
                return true;
            }
            if (body.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int slashes = 0;
                for (int i = 2; i < body.Length; i++)
                {
                    if (body[i] == '\\') { slashes++; }
                }
                return slashes <= 1;
            }
            return false;
        }

        /// <summary>
        /// Free bytes available to the calling user on the volume holding
        /// <paramref name="path"/>. Uses lpFreeBytesAvailable, so disk quotas are honoured.
        /// </summary>
        public static bool TryGetFreeSpace(string path, out long freeBytes, out long totalBytes)
        {
            freeBytes = 0;
            totalBytes = 0;

            string dir = path;
            if (FileExists(dir))
            {
                dir = LongPath.GetDirectoryName(dir);
            }

            ulong free, total, totalFree;
            // GetDiskFreeSpaceExW wants a plain path; the \\?\ form is accepted but the
            // directory must exist, so walk up until we find one that does.
            string probe = LongPath.TrimTrailingSeparators(LongPath.ToDisplay(dir));
            while (!string.IsNullOrEmpty(probe) && !DirectoryExists(probe))
            {
                string parent = LongPath.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
                {
                    break;
                }
                probe = parent;
            }

            if (string.IsNullOrEmpty(probe))
            {
                return false;
            }

            if (!probe.EndsWith("\\", StringComparison.Ordinal))
            {
                probe = probe + "\\";
            }

            if (!NativeMethods.GetDiskFreeSpaceExW(probe, out free, out total, out totalFree))
            {
                return false;
            }

            freeBytes = (long)Math.Min(free, long.MaxValue);
            totalBytes = (long)Math.Min(total, long.MaxValue);
            return true;
        }

        /// <summary>Volume root for a path, e.g. "C:\". Empty when it cannot be determined.</summary>
        public static string GetVolumeRoot(string path)
        {
            StringBuilder sb = new StringBuilder(1024);
            if (NativeMethods.GetVolumePathNameW(LongPath.ToDisplay(path), sb, (uint)sb.Capacity))
            {
                return sb.ToString();
            }
            return string.Empty;
        }

        public static string DescribeError(int win32Error)
        {
            if (win32Error == 0)
            {
                return "OK";
            }
            try
            {
                return new Win32Exception(win32Error).Message + " (" + win32Error + ")";
            }
            catch (Exception)
            {
                return "Win32 error " + win32Error;
            }
        }
    }
}
