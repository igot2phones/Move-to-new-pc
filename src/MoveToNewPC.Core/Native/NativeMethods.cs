using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MoveToNewPC.Core.Native
{
    /// <summary>
    /// Raw Win32 surface. Everything the file engine touches goes through the wide (W)
    /// entry points so the \\?\ long-path prefix works; System.IO on .NET 4.0 hard-fails
    /// past MAX_PATH and cannot be used for profile trees.
    /// </summary>
    [SuppressUnmanagedCodeSecurity]
    public static class NativeMethods
    {
        // ---- error codes -------------------------------------------------------
        public const int ERROR_SUCCESS = 0;
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_PATH_NOT_FOUND = 3;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_INVALID_HANDLE = 6;
        public const int ERROR_NOT_ENOUGH_MEMORY = 8;
        public const int ERROR_INVALID_DRIVE = 15;
        public const int ERROR_NOT_READY = 21;
        public const int ERROR_SHARING_VIOLATION = 32;
        public const int ERROR_LOCK_VIOLATION = 33;
        public const int ERROR_HANDLE_DISK_FULL = 39;
        public const int ERROR_FILE_EXISTS = 80;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_INVALID_NAME = 123;
        public const int ERROR_DIR_NOT_EMPTY = 145;
        public const int ERROR_ALREADY_EXISTS = 183;
        public const int ERROR_FILENAME_EXCED_RANGE = 206;
        public const int ERROR_NO_MORE_FILES = 18;
        public const int ERROR_DISK_FULL = 112;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_NOT_A_REPARSE_POINT = 4390;
        public const int ERROR_CLOUD_FILE_ACCESS_DENIED = 395;   // ERROR_CLOUD_FILE_* family
        public const int ERROR_MORE_DATA = 234;
        public const int ERROR_NONE_MAPPED = 1332;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        // ---- file attributes ---------------------------------------------------
        public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
        public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
        public const uint FILE_ATTRIBUTE_DEVICE = 0x00000040;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public const uint FILE_ATTRIBUTE_TEMPORARY = 0x00000100;
        public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        public const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
        /// <summary>Classic HSM/offline bit; OneDrive and Dropbox placeholders set it.</summary>
        public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        public const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 0x00002000;
        public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;
        public const uint FILE_ATTRIBUTE_VIRTUAL = 0x00010000;
        public const uint FILE_ATTRIBUTE_INTEGRITY_STREAM = 0x00008000;
        /// <summary>Windows 10 cloud filter: opening the file triggers a download.</summary>
        public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        /// <summary>Windows 10 files-on-demand: reading the data triggers a download.</summary>
        public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        // ---- CreateFile --------------------------------------------------------
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_READ_ATTRIBUTES = 0x0080;
        public const uint FILE_WRITE_ATTRIBUTES = 0x0100;

        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        /// <summary>What we open source files with: the most permissive sharing there is,
        /// because a running Outlook or browser holds its own files open.</summary>
        public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

        public const uint CREATE_NEW = 1;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint OPEN_ALWAYS = 4;
        public const uint TRUNCATE_EXISTING = 5;

        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        public const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
        /// <summary>Required to open a directory handle at all.</summary>
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        /// <summary>Open the link itself, never the target - keeps junctions inert.</summary>
        public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        /// <summary>Do not hydrate an offline/placeholder file just because we opened it.</summary>
        public const uint FILE_FLAG_OPEN_NO_RECALL = 0x00100000;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ---- MoveFileEx --------------------------------------------------------
        public const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
        public const uint MOVEFILE_COPY_ALLOWED = 0x00000002;
        public const uint MOVEFILE_WRITE_THROUGH = 0x00000008;

        // ---- registry ----------------------------------------------------------
        public static readonly IntPtr HKEY_USERS = new IntPtr(unchecked((int)0x80000003));
        public static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

        // ---- token / privilege -------------------------------------------------
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        public const string SE_BACKUP_NAME = "SeBackupPrivilege";
        public const string SE_RESTORE_NAME = "SeRestorePrivilege";
        public const string SE_SECURITY_NAME = "SeSecurityPrivilege";
        public const string SE_TAKE_OWNERSHIP_NAME = "SeTakeOwnershipPrivilege";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            /// <summary>Reparse tag when dwFileAttributes has FILE_ATTRIBUTE_REPARSE_POINT.</summary>
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;

            public long FileSize
            {
                get { return ((long)nFileSizeHigh << 32) | (long)nFileSizeLow; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public long ToTicks()
            {
                return ((long)dwHighDateTime << 32) | (long)dwLowDateTime;
            }

            public static FILETIME FromTicks(long ticks)
            {
                FILETIME ft = new FILETIME();
                ft.dwLowDateTime = (uint)(ticks & 0xFFFFFFFF);
                ft.dwHighDateTime = (uint)(ticks >> 32);
                return ft;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;

            public long FileSize
            {
                get { return ((long)nFileSizeHigh << 32) | (long)nFileSizeLow; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privilege0;
        }

        public enum GET_FILEEX_INFO_LEVELS
        {
            GetFileExInfoStandard = 0
        }

        // ---- kernel32: enumeration --------------------------------------------
        // FindFirstFileW (not FindFirstFileExW): the Ex variant's FindExInfoBasic level
        // needs Windows 7, and this tool has to run on Vista SP2.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFindHandle FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr hFindFile);

        // ---- kernel32: files ---------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveDirectoryW(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFileW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileAttributesExW(string lpFileName, GET_FILEEX_INFO_LEVELS fInfoLevelId,
                                                       out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileTime(SafeFileHandle hFile, ref long lpCreationTime,
                                              ref long lpLastAccessTime, ref long lpLastWriteTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceExW(string lpDirectoryName,
                                                      out ulong lpFreeBytesAvailable,
                                                      out ulong lpTotalNumberOfBytes,
                                                      out ulong lpTotalNumberOfFreeBytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNameW(string lpszFileName,
                                                     [Out] StringBuilder lpszVolumePathName,
                                                     uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFullPathNameW(string lpFileName, uint nBufferLength,
                                                   [Out] StringBuilder lpBuffer, IntPtr lpFilePart);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        // ---- advapi32: SIDs, hives, privileges ---------------------------------
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupAccountSidW(string lpSystemName, byte[] Sid,
                                                    [Out] StringBuilder lpName, ref uint cchName,
                                                    [Out] StringBuilder lpReferencedDomainName, ref uint cchReferencedDomainName,
                                                    out int peUse);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertStringSidToSidW(string StringSid, out IntPtr Sid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegLoadKeyW(IntPtr hKey, string lpSubKey, string lpFile);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegUnLoadKeyW(IntPtr hKey, string lpSubKey);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle,
                                                        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
                                                        ref TOKEN_PRIVILEGES NewState,
                                                        uint BufferLength,
                                                        IntPtr PreviousState,
                                                        IntPtr ReturnLength);
    }

    /// <summary>Handle from FindFirstFileW; INVALID_HANDLE_VALUE means failure.</summary>
    public sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.FindClose(handle);
        }
    }
}
