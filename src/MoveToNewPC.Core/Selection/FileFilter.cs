using System;
using System.Collections.Generic;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Selection
{
    /// <summary>
    /// Advanced-mode include/exclude globs, extension list, size range and modified-date
    /// range. Applied to files only - a directory is never filtered out by size or date.
    /// </summary>
    public sealed class FileFilter : IFileFilter
    {
        private readonly FilterSettings _settings;

        public FileFilter(FilterSettings settings)
        {
            _settings = settings ?? new FilterSettings();
        }

        /// <summary>Returns null when the settings would accept everything, so the walker can skip the call entirely.</summary>
        public static IFileFilter CreateOrNull(FilterSettings settings)
        {
            if (settings == null || settings.IsEmpty)
            {
                return null;
            }
            return new FileFilter(settings);
        }

        public bool Accept(FsEntry entry, string relativePath, out string ruleName)
        {
            ruleName = null;

            string name = entry.Name;

            if (_settings.ExcludeGlobs.Count > 0)
            {
                for (int i = 0; i < _settings.ExcludeGlobs.Count; i++)
                {
                    string pattern = _settings.ExcludeGlobs[i];
                    if (MatchesPattern(name, relativePath, pattern))
                    {
                        ruleName = "Excluded by pattern " + pattern;
                        return false;
                    }
                }
            }

            if (_settings.IncludeGlobs.Count > 0)
            {
                bool any = false;
                for (int i = 0; i < _settings.IncludeGlobs.Count && !any; i++)
                {
                    any = MatchesPattern(name, relativePath, _settings.IncludeGlobs[i]);
                }
                if (!any)
                {
                    ruleName = "Did not match any include pattern";
                    return false;
                }
            }

            if (_settings.IncludeExtensions.Count > 0)
            {
                string extension = LongPath.GetExtension(name);
                bool any = false;
                for (int i = 0; i < _settings.IncludeExtensions.Count && !any; i++)
                {
                    any = string.Equals(extension, _settings.IncludeExtensions[i], StringComparison.OrdinalIgnoreCase);
                }
                if (!any)
                {
                    ruleName = "Extension " + (extension.Length == 0 ? "(none)" : extension) + " not in the include list";
                    return false;
                }
            }

            if (_settings.MinSizeBytes > 0 && entry.Length < _settings.MinSizeBytes)
            {
                ruleName = "Smaller than " + Format.Bytes(_settings.MinSizeBytes);
                return false;
            }

            if (_settings.MaxSizeBytes > 0 && entry.Length > _settings.MaxSizeBytes)
            {
                ruleName = "Larger than " + Format.Bytes(_settings.MaxSizeBytes);
                return false;
            }

            if (_settings.ModifiedAfterUtc.HasValue || _settings.ModifiedBeforeUtc.HasValue)
            {
                DateTime modified = FsEntry.FileTimeToDateTime(entry.LastWriteTimeUtc);
                if (_settings.ModifiedAfterUtc.HasValue && modified < _settings.ModifiedAfterUtc.Value)
                {
                    ruleName = "Modified before " + _settings.ModifiedAfterUtc.Value.ToString("yyyy-MM-dd");
                    return false;
                }
                if (_settings.ModifiedBeforeUtc.HasValue && modified > _settings.ModifiedBeforeUtc.Value)
                {
                    ruleName = "Modified after " + _settings.ModifiedBeforeUtc.Value.ToString("yyyy-MM-dd");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A pattern containing a backslash is matched against the path relative to the
        /// selected root; otherwise against the file name alone. That is what people
        /// expect from "*.jpg" versus "Photos\*.jpg".
        /// </summary>
        private static bool MatchesPattern(string name, string relativePath, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }
            if (pattern.IndexOf('\\') >= 0)
            {
                return Glob.IsMatch(relativePath, pattern);
            }
            return Glob.IsMatch(name, pattern);
        }

        /// <summary>Parses "*.jpg; *.png , *.gif" into a list of patterns.</summary>
        public static List<string> ParsePatternList(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            string[] parts = text.Split(new char[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length > 0)
                {
                    result.Add(p);
                }
            }
            return result;
        }

        /// <summary>Parses "jpg;.png" into ".jpg", ".png".</summary>
        public static List<string> ParseExtensionList(string text)
        {
            List<string> raw = ParsePatternList(text);
            List<string> result = new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string e = raw[i];
                if (e.StartsWith("*", StringComparison.Ordinal))
                {
                    e = e.Substring(1);
                }
                if (!e.StartsWith(".", StringComparison.Ordinal))
                {
                    e = "." + e;
                }
                result.Add(e);
            }
            return result;
        }
    }
}
