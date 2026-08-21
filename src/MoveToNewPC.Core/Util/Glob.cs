using System;

namespace MoveToNewPC.Core.Util
{
    /// <summary>
    /// Case-insensitive `*` / `?` matching. Hand-rolled two-pointer matcher rather than
    /// Regex: this runs once per file on trees with millions of entries, and building a
    /// Regex per rule per file would dominate the scan.
    /// </summary>
    public static class Glob
    {
        public static bool IsMatch(string text, string pattern)
        {
            if (pattern == null)
            {
                return false;
            }
            if (text == null)
            {
                text = string.Empty;
            }
            if (pattern.Length == 0)
            {
                return text.Length == 0;
            }

            int t = 0;
            int p = 0;
            int starP = -1;
            int starT = 0;

            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || EqualsIgnoreCase(pattern[p], text[t])))
                {
                    t++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    starP = p;
                    starT = t;
                    p++;
                }
                else if (starP >= 0)
                {
                    // Backtrack: let the last '*' swallow one more character.
                    p = starP + 1;
                    starT++;
                    t = starT;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }

        /// <summary>True when the pattern is a plain string with no wildcard characters.</summary>
        public static bool IsLiteral(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }
            return pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0;
        }

        private static bool EqualsIgnoreCase(char a, char b)
        {
            if (a == b)
            {
                return true;
            }
            return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        }
    }
}
