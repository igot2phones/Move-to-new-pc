using System;
using System.Globalization;
using System.Text;

namespace MoveToNewPC.Core.Util
{
    public static class Format
    {
        private static readonly string[] Units = new string[] { "bytes", "KB", "MB", "GB", "TB", "PB" };

        public static string Bytes(long value)
        {
            if (value < 0)
            {
                return "?";
            }
            if (value < 1024)
            {
                return value.ToString(CultureInfo.CurrentCulture) + " bytes";
            }

            double d = value;
            int unit = 0;
            while (d >= 1024 && unit < Units.Length - 1)
            {
                d /= 1024;
                unit++;
            }
            string format = d < 10 ? "0.00" : (d < 100 ? "0.0" : "0");
            return d.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
        }

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
            {
                return "-";
            }
            return Bytes((long)bytesPerSecond) + "/s";
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 0 || span.TotalDays > 99)
            {
                return "-";
            }
            if (span.TotalHours >= 1)
            {
                return ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + "h "
                       + span.Minutes.ToString("00", CultureInfo.CurrentCulture) + "m "
                       + span.Seconds.ToString("00", CultureInfo.CurrentCulture) + "s";
            }
            if (span.TotalMinutes >= 1)
            {
                return span.Minutes.ToString(CultureInfo.CurrentCulture) + "m "
                       + span.Seconds.ToString("00", CultureInfo.CurrentCulture) + "s";
            }
            return span.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + "s";
        }

        public static string Eta(long bytesRemaining, double bytesPerSecond)
        {
            if (bytesPerSecond <= 1 || bytesRemaining <= 0)
            {
                return "-";
            }
            double seconds = bytesRemaining / bytesPerSecond;
            if (seconds > 99 * 3600)
            {
                return "-";
            }
            return Duration(TimeSpan.FromSeconds(seconds));
        }

        public static string ToHex(byte[] data)
        {
            if (data == null)
            {
                return null;
            }
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || (hex.Length % 2) != 0)
            {
                return null;
            }
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                {
                    return null;
                }
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') { return c - '0'; }
            if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
            if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
            return -1;
        }

        /// <summary>
        /// Length-independent byte comparison. Used for MACs and the pairing code so a
        /// timing side channel cannot leak them.
        /// </summary>
        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            int diff = a.Length ^ b.Length;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        /// <summary>Shortens a path for display without losing the file name.</summary>
        public static string EllipsisPath(string path, int maxChars)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxChars || maxChars < 12)
            {
                return path;
            }
            int tail = maxChars - 5;
            return "..." + path.Substring(path.Length - tail);
        }
    }
}
