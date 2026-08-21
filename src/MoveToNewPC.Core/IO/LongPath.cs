using System;
using System.Globalization;
using System.Text;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.IO
{
    /// <summary>
    /// Path helpers that work past MAX_PATH. Every path handed to a Win32 W-function in
    /// this codebase goes through <see cref="ToExtended"/> first.
    ///
    /// Note that the \\?\ prefix turns OFF Win32 path normalisation: "..", ".", trailing
    /// dots and trailing spaces are all passed through literally. That is exactly what we
    /// want when copying files a previous tool created badly, but it means callers must
    /// hand us paths that are already canonical.
    /// </summary>
    public static class LongPath
    {
        public const string ExtendedPrefix = @"\\?\";
        public const string ExtendedUncPrefix = @"\\?\UNC\";
        public const string DevicePrefix = @"\\.\";

        /// <summary>Longest path we will even attempt. Win32 caps extended paths at ~32767.</summary>
        public const int MaxExtendedPath = 32000;

        public static bool IsExtended(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4)
            {
                return false;
            }
            return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
                || path.StartsWith(DevicePrefix, StringComparison.Ordinal);
        }

        public static bool IsUnc(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return path.Length >= 2 && path[0] == '\\' && path[1] == '\\' && !IsExtended(path);
        }

        /// <summary>
        /// Converts a normal path to its \\?\ form. Already-extended paths are returned
        /// unchanged. Relative paths are resolved against the current directory via
        /// GetFullPathNameW (Path.GetFullPath would throw on anything long).
        /// </summary>
        public static string ToExtended(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (IsExtended(path))
            {
                return path;
            }

            string full = path;
            if (!IsRooted(path))
            {
                full = GetFullPathNative(path);
            }

            if (full.Length >= 2 && full[0] == '\\' && full[1] == '\\')
            {
                // \\server\share\... -> \\?\UNC\server\share\...
                return ExtendedUncPrefix + full.Substring(2);
            }

            return ExtendedPrefix + full;
        }

        /// <summary>Strips the \\?\ decoration so a path can be shown to a human.</summary>
        public static string ToDisplay(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(ExtendedUncPrefix.Length);
            }
            if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            {
                return path.Substring(ExtendedPrefix.Length);
            }
            return path;
        }

        public static bool IsRooted(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (path[0] == '\\' || path[0] == '/')
            {
                return true;
            }
            return path.Length >= 2 && path[1] == ':';
        }

        /// <summary>Joins two path fragments without any normalisation surprises.</summary>
        public static string Combine(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return right;
            }
            if (string.IsNullOrEmpty(right))
            {
                return left;
            }

            bool leftEnds = left[left.Length - 1] == '\\' || left[left.Length - 1] == '/';
            bool rightStarts = right[0] == '\\' || right[0] == '/';

            if (leftEnds && rightStarts)
            {
                return left + right.Substring(1);
            }
            if (!leftEnds && !rightStarts)
            {
                return left + "\\" + right;
            }
            return left + right;
        }

        public static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }
            int i = path.LastIndexOfAny(new char[] { '\\', '/' });
            return i < 0 ? path : path.Substring(i + 1);
        }

        public static string GetDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string trimmed = path;
            while (trimmed.Length > 0 && (trimmed[trimmed.Length - 1] == '\\' || trimmed[trimmed.Length - 1] == '/'))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            int i = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
            if (i < 0)
            {
                return string.Empty;
            }
            return trimmed.Substring(0, i);
        }

        public static string GetExtension(string path)
        {
            string name = GetFileName(path);
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                return dot == name.Length - 1 && dot > 0 ? "." : string.Empty;
            }
            return name.Substring(dot);
        }

        public static string GetFileNameWithoutExtension(string path)
        {
            string name = GetFileName(path);
            int dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        public static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            string s = path;
            // Never strip the separator off "C:\" or "\\?\C:\".
            while (s.Length > 0 && (s[s.Length - 1] == '\\' || s[s.Length - 1] == '/'))
            {
                string candidate = s.Substring(0, s.Length - 1);
                if (candidate.Length == 0 || candidate.EndsWith(":", StringComparison.Ordinal))
                {
                    break;
                }
                s = candidate;
            }
            return s;
        }

        /// <summary>
        /// The relative portion of <paramref name="path"/> under <paramref name="root"/>,
        /// or null when the path is not actually under the root. Comparison is
        /// case-insensitive because Windows file systems are.
        /// </summary>
        public static string GetRelativePath(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
            {
                return null;
            }

            string r = TrimTrailingSeparators(ToDisplay(root));
            string p = ToDisplay(path);

            if (p.Length == r.Length && string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            if (p.Length <= r.Length)
            {
                return null;
            }
            if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (p[r.Length] != '\\' && p[r.Length] != '/')
            {
                return null;
            }
            return p.Substring(r.Length + 1);
        }

        private static readonly string[] ReservedNames = new string[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// True for the MS-DOS device names. Note that "CON.txt" is reserved too - the
        /// check is against the name up to the first dot.
        /// </summary>
        public static bool IsReservedDeviceName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            string stem = fileName;
            int dot = stem.IndexOf('.');
            if (dot >= 0)
            {
                stem = stem.Substring(0, dot);
            }
            stem = stem.TrimEnd(' ');

            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string GetFullPathNative(string path)
        {
            uint size = 512;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                StringBuilder sb = new StringBuilder((int)size);
                uint needed = NativeMethods.GetFullPathNameW(path, size, sb, IntPtr.Zero);
                if (needed == 0)
                {
                    // Fall back to a naive join rather than throwing; the caller will get a
                    // Win32 error from the real operation and can report it properly.
                    return Combine(Environment.CurrentDirectory, path);
                }
                if (needed < size)
                {
                    return sb.ToString();
                }
                size = needed + 1;
            }
            return Combine(Environment.CurrentDirectory, path);
        }

        public static string DescribeLength(string path)
        {
            return ToDisplay(path).Length.ToString(CultureInfo.InvariantCulture) + " chars";
        }
    }
}
