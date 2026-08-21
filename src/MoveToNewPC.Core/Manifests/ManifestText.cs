using System;
using System.Globalization;
using System.Text;

namespace MoveToNewPC.Core.Manifests
{
    /// <summary>
    /// Field escaping for the tab-separated manifest and journal formats
    /// (see docs/PROTOCOL.md). Only the four characters that would break the format are
    /// escaped - file names contain arbitrary Unicode and must survive byte for byte.
    /// </summary>
    public static class ManifestText
    {
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool needs = false;
            for (int i = 0; i < value.Length && !needs; i++)
            {
                char c = value[i];
                needs = c == '\\' || c == '\t' || c == '\n' || c == '\r';
            }
            if (!needs)
            {
                return value;
            }

            StringBuilder sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i == value.Length - 1)
                {
                    sb.Append(c);
                    continue;
                }

                i++;
                switch (value[i])
                {
                    case '\\': sb.Append('\\'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    default:
                        // Unknown escape: keep both characters rather than losing data.
                        sb.Append('\\').Append(value[i]);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string L(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string U(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static long ParseLong(string value)
        {
            long result;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        public static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        public static uint ParseUInt(string value)
        {
            uint result;
            return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }
    }
}
