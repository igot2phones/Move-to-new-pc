using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Selection
{
    public enum ExclusionKind
    {
        /// <summary>The item's own name, exactly (case-insensitive).</summary>
        Name = 0,
        /// <summary>The item's own name, glob.</summary>
        NamePattern = 1,
        /// <summary>Any path segment equals this (case-insensitive).</summary>
        Segment = 2,
        /// <summary>The path relative to the selected root starts with this.</summary>
        RelativePrefix = 3,
        /// <summary>The absolute source path starts with this. Used for %WINDIR% etc.</summary>
        AbsolutePrefix = 4
    }

    public sealed class ExclusionRule
    {
        public ExclusionKind Kind;
        public string Pattern;
        /// <summary>Shown in the report as the reason.</summary>
        public string Description;
        public bool AppliesToFiles = true;
        public bool AppliesToDirectories = true;
        public bool Enabled = true;
        /// <summary>Built-in rules can be disabled but not deleted, so the list stays honest.</summary>
        public bool IsBuiltIn;

        public ExclusionRule() { }

        public ExclusionRule(ExclusionKind kind, string pattern, string description,
                             bool files, bool directories, bool builtIn)
        {
            Kind = kind;
            Pattern = pattern;
            Description = description;
            AppliesToFiles = files;
            AppliesToDirectories = directories;
            IsBuiltIn = builtIn;
        }

        public override string ToString()
        {
            return Kind + ": " + Pattern;
        }
    }

    /// <summary>
    /// The "never move this" list from the spec, as data rather than scattered ifs, so the
    /// whole thing can be shown and edited in Advanced mode.
    /// </summary>
    public sealed class ExclusionRules : IPathExclusion
    {
        public readonly List<ExclusionRule> Rules = new List<ExclusionRule>();

        public static ExclusionRules CreateDefault()
        {
            ExclusionRules r = new ExclusionRules();

            // --- registry hives and per-folder metadata: meaningless on the new machine
            r.Add(ExclusionKind.NamePattern, "NTUSER.DAT*", "User registry hive (not portable between machines)", true, false);
            r.Add(ExclusionKind.NamePattern, "UsrClass.dat*", "User class registry hive (not portable)", true, false);
            r.Add(ExclusionKind.Name, "ntuser.ini", "Profile marker file", true, false);
            r.Add(ExclusionKind.Name, "ntuser.pol", "Profile policy file", true, false);
            r.Add(ExclusionKind.Name, "desktop.ini", "Folder view metadata (would override new-PC folder settings)", true, false);
            r.Add(ExclusionKind.Name, "Thumbs.db", "Thumbnail cache", true, false);
            r.Add(ExclusionKind.Name, "ehthumbs.db", "Thumbnail cache", true, false);
            r.Add(ExclusionKind.Name, "IconCache.db", "Icon cache", true, false);

            // --- system files that must never be read or written
            r.Add(ExclusionKind.Name, "pagefile.sys", "Page file", true, false);
            r.Add(ExclusionKind.Name, "hiberfil.sys", "Hibernation file", true, false);
            r.Add(ExclusionKind.Name, "swapfile.sys", "Swap file", true, false);

            // --- our own partial files, so an interrupted run never migrates its own debris
            r.Add(ExclusionKind.NamePattern, "*.mtnpc-part", "Partial file from an interrupted transfer", true, false);

            // --- volume-level directories
            r.Add(ExclusionKind.Segment, "$RECYCLE.BIN", "Recycle Bin", true, true);
            r.Add(ExclusionKind.Segment, "RECYCLER", "Recycle Bin (legacy)", true, true);
            r.Add(ExclusionKind.Segment, "System Volume Information", "System Volume Information", true, true);

            // --- temp and cache
            r.Add(ExclusionKind.RelativePrefix, @"AppData\Local\Temp", "Temporary files", true, true);
            r.Add(ExclusionKind.RelativePrefix, @"AppData\LocalLow", "AppData\\LocalLow (excluded by default)", true, true);
            r.Add(ExclusionKind.Segment, "INetCache", "Internet cache", true, true);
            r.Add(ExclusionKind.Segment, "Temporary Internet Files", "Internet cache (legacy)", true, true);
            r.Add(ExclusionKind.Segment, "WebCache", "Internet Explorer / Edge web cache", true, true);
            r.Add(ExclusionKind.Segment, "INetCookies", "Cookie cache", true, true);

            // --- browser caches. These match a folder NAME anywhere in the tree, which is
            //     blunt; they are listed and switchable in Advanced mode for exactly that
            //     reason, and anything they drop appears in the report.
            r.Add(ExclusionKind.Segment, "Cache", "Browser cache", true, true);
            r.Add(ExclusionKind.Segment, "Cache_Data", "Browser cache", true, true);
            r.Add(ExclusionKind.Segment, "Code Cache", "Browser code cache", true, true);
            r.Add(ExclusionKind.Segment, "GPUCache", "Browser GPU cache", true, true);
            r.Add(ExclusionKind.Segment, "ShaderCache", "Shader cache", true, true);
            r.Add(ExclusionKind.Segment, "GrShaderCache", "Shader cache", true, true);
            r.Add(ExclusionKind.Segment, "Service Worker", "Browser service worker cache", true, true);
            r.Add(ExclusionKind.Segment, "CacheStorage", "Browser cache storage", true, true);
            r.Add(ExclusionKind.Segment, "Crashpad", "Crash dump staging", true, true);
            r.Add(ExclusionKind.Segment, "CrashDumps", "Crash dumps", true, true);
            r.Add(ExclusionKind.Segment, "Explorer", "Explorer thumbnail cache", false, true);

            // --- never read from the OS or installed programs
            r.Add(ExclusionKind.AbsolutePrefix, ResolveWindowsDirectory(), "Windows directory", true, true);
            r.Add(ExclusionKind.AbsolutePrefix, ResolveFolder(Environment.SpecialFolder.ProgramFiles), "Program Files", true, true);
            r.Add(ExclusionKind.AbsolutePrefix, ResolveProgramFilesX86(), "Program Files (x86)", true, true);

            return r;
        }

        private void Add(ExclusionKind kind, string pattern, string description, bool files, bool directories)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }
            Rules.Add(new ExclusionRule(kind, pattern, description, files, directories, true));
        }

        public void AddUserRule(ExclusionKind kind, string pattern, string description)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }
            Rules.Add(new ExclusionRule(kind, pattern, description ?? "User rule", true, true, false));
        }

        public bool IsExcluded(string relativePath, string fullPath, bool isDirectory, out string ruleName)
        {
            ruleName = null;
            if (Rules.Count == 0)
            {
                return false;
            }

            string name = LongPath.GetFileName(relativePath);
            if (string.IsNullOrEmpty(name))
            {
                name = LongPath.GetFileName(LongPath.TrimTrailingSeparators(fullPath));
            }
            string display = LongPath.ToDisplay(fullPath);

            for (int i = 0; i < Rules.Count; i++)
            {
                ExclusionRule rule = Rules[i];
                if (!rule.Enabled)
                {
                    continue;
                }
                if (isDirectory && !rule.AppliesToDirectories)
                {
                    continue;
                }
                if (!isDirectory && !rule.AppliesToFiles)
                {
                    continue;
                }

                if (Matches(rule, name, relativePath, display, isDirectory))
                {
                    ruleName = rule.Description ?? rule.Pattern;
                    return true;
                }
            }

            return false;
        }

        private static bool Matches(ExclusionRule rule, string name, string relativePath, string displayPath,
                                    bool isDirectory)
        {
            switch (rule.Kind)
            {
                case ExclusionKind.Name:
                    return string.Equals(name, rule.Pattern, StringComparison.OrdinalIgnoreCase);

                case ExclusionKind.NamePattern:
                    return Glob.IsMatch(name, rule.Pattern);

                case ExclusionKind.Segment:
                    // For a directory, only its own name counts: parents were already tested
                    // when we walked into them, and the walker never descends into an
                    // excluded folder.
                    if (isDirectory)
                    {
                        return string.Equals(name, rule.Pattern, StringComparison.OrdinalIgnoreCase);
                    }
                    return HasSegment(relativePath, rule.Pattern);

                case ExclusionKind.RelativePrefix:
                    return StartsWithPathPrefix(relativePath, rule.Pattern);

                case ExclusionKind.AbsolutePrefix:
                    return StartsWithPathPrefix(displayPath, rule.Pattern);

                default:
                    return false;
            }
        }

        private static bool HasSegment(string path, string segment)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            int start = 0;
            while (start <= path.Length)
            {
                int end = path.IndexOf('\\', start);
                if (end < 0)
                {
                    end = path.Length;
                }
                int length = end - start;
                if (length == segment.Length
                    && string.Compare(path, start, segment, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
                start = end + 1;
            }
            return false;
        }

        /// <summary>
        /// Prefix match on whole segments, so "Temp" never matches "Templates".
        /// </summary>
        private static bool StartsWithPathPrefix(string path, string prefix)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            string p = prefix.TrimEnd('\\');
            if (path.Length < p.Length)
            {
                return false;
            }
            if (string.Compare(path, 0, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }
            return path.Length == p.Length || path[p.Length] == '\\';
        }

        // ---- persistence (Advanced mode edits) --------------------------------

        public void Save(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# MoveToNewPC exclusion rules");
                sb.AppendLine("# kind<TAB>pattern<TAB>files<TAB>dirs<TAB>enabled<TAB>description");
                for (int i = 0; i < Rules.Count; i++)
                {
                    ExclusionRule r = Rules[i];
                    sb.Append((int)r.Kind).Append('\t')
                      .Append(r.Pattern).Append('\t')
                      .Append(r.AppliesToFiles ? '1' : '0').Append('\t')
                      .Append(r.AppliesToDirectories ? '1' : '0').Append('\t')
                      .Append(r.Enabled ? '1' : '0').Append('\t')
                      .Append(r.Description ?? string.Empty).Append('\n');
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not save exclusion rules to " + path + ": " + ex.Message);
            }
        }

        public static ExclusionRules Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return CreateDefault();
                }

                ExclusionRules rules = new ExclusionRules();
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }
                    string[] parts = line.Split('\t');
                    if (parts.Length < 5)
                    {
                        continue;
                    }

                    int kind;
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind))
                    {
                        continue;
                    }

                    ExclusionRule r = new ExclusionRule();
                    r.Kind = (ExclusionKind)kind;
                    r.Pattern = parts[1];
                    r.AppliesToFiles = parts[2] == "1";
                    r.AppliesToDirectories = parts[3] == "1";
                    r.Enabled = parts[4] == "1";
                    r.Description = parts.Length > 5 ? parts[5] : parts[1];
                    rules.Rules.Add(r);
                }

                return rules.Rules.Count == 0 ? CreateDefault() : rules;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load exclusion rules from " + path + ": " + ex.Message);
                return CreateDefault();
            }
        }

        private static string ResolveWindowsDirectory()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            catch (Exception)
            {
                return @"C:\Windows";
            }
        }

        private static string ResolveFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveProgramFilesX86()
        {
            try
            {
                // Environment.SpecialFolder.ProgramFilesX86 exists on .NET 4.0, but on a
                // 32-bit OS it resolves to the same place as ProgramFiles, which is fine.
                string value = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
