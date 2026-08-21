using System;
using System.Text;

namespace MoveToNewPC.Core.IO
{
    /// <summary>
    /// Receiver-side validation of every relative path that arrives from the other machine.
    /// The sender is assumed hostile: this is an elevated process writing into user
    /// profiles, so a crafted path like "..\..\..\Windows\System32\evil.dll" must be
    /// impossible, not merely unlikely.
    ///
    /// Nothing here trusts the sender's claim about where a file belongs. The path is
    /// validated segment by segment and then re-checked against the destination root after
    /// being combined.
    /// </summary>
    public static class PathValidation
    {
        /// <summary>Longest relative path we will accept, leaving room for the destination root.</summary>
        public const int MaxRelativeLength = 24000;

        public static bool IsSafeRelativePath(string relativePath, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(relativePath))
            {
                reason = "Empty path";
                return false;
            }

            if (relativePath.Length > MaxRelativeLength)
            {
                reason = "Path is too long";
                return false;
            }

            // Absolute in any form.
            if (relativePath[0] == '\\' || relativePath[0] == '/')
            {
                reason = "Absolute path";
                return false;
            }
            if (relativePath.Length >= 2 && relativePath[1] == ':')
            {
                reason = "Drive-qualified path";
                return false;
            }
            if (relativePath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "UNC path";
                return false;
            }
            if (relativePath.IndexOf(@"\\?\", StringComparison.Ordinal) >= 0
                || relativePath.IndexOf(@"\\.\", StringComparison.Ordinal) >= 0)
            {
                reason = "Device path prefix";
                return false;
            }

            for (int i = 0; i < relativePath.Length; i++)
            {
                char c = relativePath[i];

                if (c == '\0')
                {
                    reason = "Contains a null character";
                    return false;
                }
                if (c < 0x20)
                {
                    reason = "Contains a control character";
                    return false;
                }
                // A colon anywhere would open an alternate data stream.
                if (c == ':')
                {
                    reason = "Contains ':' (alternate data stream)";
                    return false;
                }
                if (c == '/')
                {
                    reason = "Contains '/' (only '\\' is allowed)";
                    return false;
                }
                if (c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"')
                {
                    reason = "Contains a wildcard or reserved character";
                    return false;
                }
            }

            // Segment checks.
            int start = 0;
            while (start <= relativePath.Length)
            {
                int end = relativePath.IndexOf('\\', start);
                if (end < 0)
                {
                    end = relativePath.Length;
                }

                int length = end - start;
                if (length == 0)
                {
                    reason = "Empty path segment";
                    return false;
                }

                string segment = relativePath.Substring(start, length);

                if (segment == "." || segment == "..")
                {
                    reason = "Contains a relative segment ('" + segment + "')";
                    return false;
                }

                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ')
                {
                    // Windows silently strips these, so "evil.exe." and "evil.exe" become the
                    // same file. Refuse instead of resolving to something unexpected.
                    reason = "Segment ends with a dot or space";
                    return false;
                }

                if (LongPath.IsReservedDeviceName(segment))
                {
                    reason = "Reserved device name (" + segment + ")";
                    return false;
                }

                start = end + 1;
            }

            return true;
        }

        /// <summary>
        /// Combines a validated relative path with the destination root and proves the
        /// result is still inside that root. Returns null on rejection.
        /// </summary>
        public static string ResolveUnderRoot(string destinationRoot, string relativePath, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(destinationRoot))
            {
                reason = "No destination root";
                return null;
            }

            if (!IsSafeRelativePath(relativePath, out reason))
            {
                return null;
            }

            string root = LongPath.TrimTrailingSeparators(LongPath.ToExtended(destinationRoot));
            string combined = LongPath.Combine(root, relativePath);

            // Belt and braces: even though the segment checks above already rule out "..",
            // re-verify containment on the final string. This is the invariant that matters.
            string rootDisplay = LongPath.TrimTrailingSeparators(LongPath.ToDisplay(root));
            string combinedDisplay = LongPath.ToDisplay(combined);

            if (combinedDisplay.Length <= rootDisplay.Length
                || !combinedDisplay.StartsWith(rootDisplay, StringComparison.OrdinalIgnoreCase)
                || combinedDisplay[rootDisplay.Length] != '\\')
            {
                reason = "Resolved path escapes the destination folder";
                return null;
            }

            if (combined.Length > LongPath.MaxExtendedPath)
            {
                reason = "Resolved path is too long";
                return null;
            }

            return combined;
        }

        /// <summary>
        /// Makes an arbitrary string safe to use as a single folder name (used for mapping a
        /// received user onto a plain folder).
        /// </summary>
        public static string SanitiseSegment(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 0x20 || c == ':' || c == '\\' || c == '/' || c == '*' || c == '?'
                    || c == '<' || c == '>' || c == '|' || c == '"')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().TrimEnd('.', ' ');
            if (result.Length == 0 || LongPath.IsReservedDeviceName(result))
            {
                return fallback;
            }
            return result;
        }
    }
}
