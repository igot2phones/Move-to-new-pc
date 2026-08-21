using System;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// Tests that touch no Win32 API and no disk. They are kept separate so they can also be
    /// compiled and RUN on a non-Windows build machine (see tools/verify-pure.sh) - which is
    /// the only way to execute any of this code without a Windows box in front of you.
    ///
    /// Everything security-critical that is pure lives here: path traversal rejection,
    /// manifest escaping, and constant-time comparison.
    /// </summary>
    public static class PureTests
    {
        public static void Register(TestRunner runner)
        {
            RegisterGlob(runner);
            RegisterFormat(runner);
            RegisterManifestText(runner);
            RegisterLongPath(runner);
            RegisterPathValidation(runner);
        }

        private static void RegisterGlob(TestRunner runner)
        {
            runner.Group("Glob");

            runner.Test("literal match is case-insensitive", delegate
            {
                Assert.True(Glob.IsMatch("Thumbs.db", "thumbs.DB"), "case-insensitive literal");
                Assert.False(Glob.IsMatch("Thumbs.db", "thumbs.dbx"), "different literal");
            });

            runner.Test("star matches any run including empty", delegate
            {
                Assert.True(Glob.IsMatch("NTUSER.DAT", "NTUSER.DAT*"), "trailing star, empty tail");
                Assert.True(Glob.IsMatch("NTUSER.DAT.LOG1", "NTUSER.DAT*"), "trailing star, real tail");
                Assert.True(Glob.IsMatch("anything.jpg", "*.jpg"), "leading star");
                Assert.True(Glob.IsMatch(".jpg", "*.jpg"), "leading star with empty stem");
                Assert.True(Glob.IsMatch("abc", "*"), "bare star");
                Assert.True(Glob.IsMatch("", "*"), "bare star matches empty");
            });

            runner.Test("question mark matches exactly one character", delegate
            {
                Assert.True(Glob.IsMatch("a.txt", "?.txt"), "single char");
                Assert.False(Glob.IsMatch("ab.txt", "?.txt"), "two chars should not match");
                Assert.False(Glob.IsMatch(".txt", "?.txt"), "zero chars should not match");
            });

            runner.Test("backtracking works with several stars", delegate
            {
                // The classic case a naive greedy matcher gets wrong.
                Assert.True(Glob.IsMatch("aXbXc", "a*b*c"), "two stars");
                Assert.True(Glob.IsMatch("aaa", "a*a"), "overlapping");
                Assert.False(Glob.IsMatch("aXbXd", "a*b*c"), "must still reject");
                Assert.True(Glob.IsMatch("filename.tar.gz", "*.tar.gz"), "double extension");
                Assert.False(Glob.IsMatch("filename.tar.gz.txt", "*.tar.gz"), "not at the end");
            });

            runner.Test("empty pattern matches only empty text", delegate
            {
                Assert.True(Glob.IsMatch("", ""), "empty/empty");
                Assert.False(Glob.IsMatch("x", ""), "empty pattern, non-empty text");
            });

            runner.Test("null pattern never matches", delegate
            {
                Assert.False(Glob.IsMatch("x", null), "null pattern");
            });
        }

        private static void RegisterFormat(TestRunner runner)
        {
            runner.Group("Format");

            runner.Test("hex round-trips", delegate
            {
                byte[] data = new byte[] { 0x00, 0x0f, 0x10, 0xff, 0x7f, 0x80 };
                string hex = Format.ToHex(data);
                Assert.Equal("000f10ff7f80", hex, "hex encoding");

                byte[] back = Format.FromHex(hex);
                Assert.NotNull(back, "decoded");
                Assert.Equal(data.Length, back.Length, "length");
                for (int i = 0; i < data.Length; i++)
                {
                    Assert.Equal(data[i], back[i], "byte " + i);
                }
            });

            runner.Test("FromHex rejects malformed input", delegate
            {
                Assert.Null(Format.FromHex("abc"), "odd length");
                Assert.Null(Format.FromHex("zz"), "non-hex characters");
                Assert.Null(Format.FromHex(null), "null");
            });

            runner.Test("ConstantTimeEquals compares correctly", delegate
            {
                byte[] a = new byte[] { 1, 2, 3, 4 };
                byte[] b = new byte[] { 1, 2, 3, 4 };
                byte[] c = new byte[] { 1, 2, 3, 5 };
                byte[] shorter = new byte[] { 1, 2, 3 };

                Assert.True(Format.ConstantTimeEquals(a, b), "identical");
                Assert.False(Format.ConstantTimeEquals(a, c), "last byte differs");
                Assert.False(Format.ConstantTimeEquals(a, shorter), "different length");
                Assert.False(Format.ConstantTimeEquals(a, null), "null");
                Assert.False(Format.ConstantTimeEquals(null, null), "both null is not equal");
            });

            runner.Test("byte formatting is readable", delegate
            {
                Assert.Equal("0 bytes", Format.Bytes(0), "zero");
                Assert.Equal("1023 bytes", Format.Bytes(1023), "just under 1 KB");
                Assert.True(Format.Bytes(1024).StartsWith("1.00 KB"), "one KB");
                Assert.True(Format.Bytes(5L * 1024 * 1024 * 1024).IndexOf("GB") > 0, "gigabytes");
                Assert.Equal("?", Format.Bytes(-1), "negative");
            });

            runner.Test("EllipsisPath keeps the file name", delegate
            {
                string path = @"C:\Users\somebody\Documents\Projects\Deep\Deeper\report-final-v2.docx";
                string shortened = Format.EllipsisPath(path, 30);
                Assert.True(shortened.Length <= 30, "respects the limit");
                Assert.True(shortened.EndsWith("report-final-v2.docx"), "keeps the file name");
                Assert.Equal(path, Format.EllipsisPath(path, 500), "short enough is unchanged");
            });
        }

        private static void RegisterManifestText(TestRunner runner)
        {
            runner.Group("Manifest escaping");

            runner.Test("path separators are escaped, not passed through", delegate
            {
                // Backslash MUST be escaped even though it is the normal path separator:
                // otherwise a file genuinely called "report\there.txt" would come back with a
                // tab in its name. The cost is that every manifest line doubles its slashes.
                Assert.Equal(@"Documents\\report.docx", ManifestText.Escape(@"Documents\report.docx"),
                             "backslash is doubled");
                Assert.Equal(@"Documents\report.docx", ManifestText.Unescape(@"Documents\\report.docx"),
                             "and comes back single");
            });

            runner.Test("round-trips the characters that would break the format", delegate
            {
                string[] cases = new string[]
                {
                    "plain",
                    @"C:\Users\bob\Documents",
                    "tab\there",
                    "newline\nhere",
                    "carriage\rreturn",
                    "backslash\\\\double",
                    "everything\t\n\r\\at once",
                    "unicode caf\u00e9 \u65e5\u672c\u8a9e \U0001F600",
                    "trailing backslash\\",
                    ""
                };

                for (int i = 0; i < cases.Length; i++)
                {
                    string escaped = ManifestText.Escape(cases[i]);
                    Assert.False(escaped.IndexOf('\t') >= 0, "no raw tab survives escaping: " + cases[i]);
                    Assert.False(escaped.IndexOf('\n') >= 0, "no raw newline survives escaping");
                    Assert.False(escaped.IndexOf('\r') >= 0, "no raw CR survives escaping");
                    Assert.Equal(cases[i], ManifestText.Unescape(escaped), "round-trip case " + i);
                }
            });

            runner.Test("a file name that looks like an escape survives", delegate
            {
                // A real file can be called "report\tuesday.txt" - the backslash-t is two
                // literal characters and must not come back as a tab.
                string name = @"report\tuesday.txt";
                Assert.Equal(name, ManifestText.Unescape(ManifestText.Escape(name)), "literal backslash-t");
            });

            runner.Test("number parsing is culture-invariant and safe", delegate
            {
                Assert.Equal(0, ManifestText.ParseLong("not a number"), "garbage becomes zero");
                Assert.Equal(0, ManifestText.ParseLong(""), "empty becomes zero");
                Assert.Equal(9007199254740993L, ManifestText.ParseLong("9007199254740993"), "large long");
                Assert.Equal(4294967295L, ManifestText.ParseUInt("4294967295"), "max uint");
            });
        }

        private static void RegisterLongPath(TestRunner runner)
        {
            runner.Group("LongPath");

            runner.Test("extended prefix is applied and removed", delegate
            {
                Assert.Equal(@"\\?\C:\Users\bob", LongPath.ToExtended(@"C:\Users\bob"), "local path");
                Assert.Equal(@"\\?\UNC\server\share\folder", LongPath.ToExtended(@"\\server\share\folder"), "UNC");
                Assert.Equal(@"\\?\C:\x", LongPath.ToExtended(@"\\?\C:\x"), "already extended is unchanged");

                Assert.Equal(@"C:\Users\bob", LongPath.ToDisplay(@"\\?\C:\Users\bob"), "strip local");
                Assert.Equal(@"\\server\share", LongPath.ToDisplay(@"\\?\UNC\server\share"), "strip UNC");
                Assert.Equal(@"C:\plain", LongPath.ToDisplay(@"C:\plain"), "already plain");
            });

            runner.Test("Combine does not double or drop separators", delegate
            {
                Assert.Equal(@"a\b", LongPath.Combine("a", "b"), "neither has a separator");
                Assert.Equal(@"a\b", LongPath.Combine(@"a\", "b"), "left has one");
                Assert.Equal(@"a\b", LongPath.Combine("a", @"\b"), "right has one");
                Assert.Equal(@"a\b", LongPath.Combine(@"a\", @"\b"), "both have one");
                Assert.Equal("b", LongPath.Combine("", "b"), "empty left");
                Assert.Equal("a", LongPath.Combine("a", ""), "empty right");
            });

            runner.Test("name and extension splitting", delegate
            {
                Assert.Equal("file.txt", LongPath.GetFileName(@"C:\dir\file.txt"), "file name");
                Assert.Equal(@"C:\dir", LongPath.GetDirectoryName(@"C:\dir\file.txt"), "directory");
                Assert.Equal(".txt", LongPath.GetExtension(@"C:\dir\file.txt"), "extension");
                Assert.Equal(".gz", LongPath.GetExtension("archive.tar.gz"), "last extension only");
                Assert.Equal("", LongPath.GetExtension("noextension"), "no extension");
                Assert.Equal("archive.tar", LongPath.GetFileNameWithoutExtension("archive.tar.gz"), "stem");
                Assert.Equal(".hidden", LongPath.GetFileName(".hidden"), "dotfile name");
                Assert.Equal("", LongPath.GetExtension(".hidden"), "dotfile has no extension");
            });

            runner.Test("trailing separators are trimmed but roots survive", delegate
            {
                Assert.Equal(@"C:\dir", LongPath.TrimTrailingSeparators(@"C:\dir\"), "trailing slash");
                Assert.Equal(@"C:\dir", LongPath.TrimTrailingSeparators(@"C:\dir\\\"), "several");
                Assert.Equal(@"C:\", LongPath.TrimTrailingSeparators(@"C:\"), "drive root keeps its slash");
            });

            runner.Test("GetRelativePath is case-insensitive and boundary-safe", delegate
            {
                Assert.Equal(@"Sub\file.txt",
                             LongPath.GetRelativePath(@"C:\Root", @"c:\root\Sub\file.txt"),
                             "case-insensitive");
                Assert.Equal("", LongPath.GetRelativePath(@"C:\Root", @"C:\Root"), "the root itself");
                Assert.Equal(@"file.txt",
                             LongPath.GetRelativePath(@"C:\Root\", @"C:\Root\file.txt"),
                             "root with a trailing separator");

                // The bug this test exists for: "C:\Root2" must not be treated as inside "C:\Root".
                Assert.Null(LongPath.GetRelativePath(@"C:\Root", @"C:\Root2\file.txt"),
                            "sibling with a shared prefix is not inside");
                Assert.Null(LongPath.GetRelativePath(@"C:\Root", @"D:\Root\file.txt"), "different drive");
                Assert.Null(LongPath.GetRelativePath(@"C:\Root\Sub", @"C:\Root"), "parent is not inside child");
            });

            runner.Test("reserved device names are recognised", delegate
            {
                Assert.True(LongPath.IsReservedDeviceName("CON"), "CON");
                Assert.True(LongPath.IsReservedDeviceName("con"), "lower case");
                Assert.True(LongPath.IsReservedDeviceName("CON.txt"), "with an extension");
                Assert.True(LongPath.IsReservedDeviceName("LPT1"), "LPT1");
                Assert.True(LongPath.IsReservedDeviceName("NUL.anything.here"), "NUL with extensions");
                Assert.True(LongPath.IsReservedDeviceName("CON "), "trailing space still reserved");

                Assert.False(LongPath.IsReservedDeviceName("CONTACT"), "longer word is fine");
                Assert.False(LongPath.IsReservedDeviceName("COM10"), "COM10 is not reserved");
                Assert.False(LongPath.IsReservedDeviceName("MyCON"), "suffix is fine");
                Assert.False(LongPath.IsReservedDeviceName(""), "empty");
            });
        }

        private static void RegisterPathValidation(TestRunner runner)
        {
            runner.Group("PathValidation (hostile input)");

            runner.Test("ordinary relative paths are accepted", delegate
            {
                string reason;
                Assert.True(PathValidation.IsSafeRelativePath(@"Documents\report.docx", out reason), "simple");
                Assert.True(PathValidation.IsSafeRelativePath("file.txt", out reason), "no folder");
                Assert.True(PathValidation.IsSafeRelativePath(@"a\b\c\d\e\f.txt", out reason), "deep");
                Assert.True(PathValidation.IsSafeRelativePath("caf\u00e9 \u65e5\u672c\u8a9e.txt", out reason), "unicode");
                Assert.True(PathValidation.IsSafeRelativePath("file..name.txt", out reason),
                            "dots inside a name are fine");
            });

            runner.Test("directory traversal is rejected", delegate
            {
                string reason;
                Assert.False(PathValidation.IsSafeRelativePath(@"..\..\Windows\System32\evil.dll", out reason),
                             "classic traversal");
                Assert.False(PathValidation.IsSafeRelativePath(@"Documents\..\..\evil.txt", out reason),
                             "traversal in the middle");
                Assert.False(PathValidation.IsSafeRelativePath("..", out reason), "bare ..");
                Assert.False(PathValidation.IsSafeRelativePath(".", out reason), "bare .");
                Assert.False(PathValidation.IsSafeRelativePath(@".\file.txt", out reason), "leading ./");
            });

            runner.Test("absolute and device paths are rejected", delegate
            {
                string reason;
                Assert.False(PathValidation.IsSafeRelativePath(@"C:\Windows\evil.dll", out reason), "drive letter");
                Assert.False(PathValidation.IsSafeRelativePath(@"\Windows\evil.dll", out reason), "rooted");
                Assert.False(PathValidation.IsSafeRelativePath(@"\\server\share\evil.dll", out reason), "UNC");
                Assert.False(PathValidation.IsSafeRelativePath(@"\\?\C:\evil.dll", out reason), "extended prefix");
                Assert.False(PathValidation.IsSafeRelativePath("/etc/passwd", out reason), "forward slashes");
            });

            runner.Test("alternate data streams and control characters are rejected", delegate
            {
                string reason;
                Assert.False(PathValidation.IsSafeRelativePath("file.txt:hidden", out reason), "ADS");
                Assert.False(PathValidation.IsSafeRelativePath("file.txt:$DATA", out reason), "ADS with $DATA");
                Assert.False(PathValidation.IsSafeRelativePath("file\0.txt", out reason), "null byte");
                Assert.False(PathValidation.IsSafeRelativePath("file\u0001.txt", out reason), "control char");
                Assert.False(PathValidation.IsSafeRelativePath("wild*card.txt", out reason), "wildcard");
                Assert.False(PathValidation.IsSafeRelativePath("pipe|char.txt", out reason), "pipe");
            });

            runner.Test("reserved names and trailing dots/spaces are rejected", delegate
            {
                string reason;
                Assert.False(PathValidation.IsSafeRelativePath(@"folder\CON", out reason), "CON in a segment");
                Assert.False(PathValidation.IsSafeRelativePath(@"CON\file.txt", out reason), "CON as a folder");
                Assert.False(PathValidation.IsSafeRelativePath("evil.exe.", out reason), "trailing dot");
                Assert.False(PathValidation.IsSafeRelativePath("evil.exe ", out reason), "trailing space");
                Assert.False(PathValidation.IsSafeRelativePath(@"folder \file.txt", out reason),
                             "trailing space on a folder");
            });

            runner.Test("empty segments and empty paths are rejected", delegate
            {
                string reason;
                Assert.False(PathValidation.IsSafeRelativePath("", out reason), "empty");
                Assert.False(PathValidation.IsSafeRelativePath(null, out reason), "null");
                Assert.False(PathValidation.IsSafeRelativePath(@"a\\b", out reason), "double separator");
                Assert.False(PathValidation.IsSafeRelativePath(@"a\", out reason), "trailing separator");
            });

            runner.Test("ResolveUnderRoot stays inside the root", delegate
            {
                string reason;

                string resolved = PathValidation.ResolveUnderRoot(@"C:\dest", @"Documents\a.txt", out reason);
                Assert.Equal(@"\\?\C:\dest\Documents\a.txt", resolved, "normal resolution");

                Assert.Null(PathValidation.ResolveUnderRoot(@"C:\dest", @"..\elsewhere\a.txt", out reason),
                            "traversal is refused");
                Assert.Null(PathValidation.ResolveUnderRoot(@"C:\dest", @"C:\elsewhere\a.txt", out reason),
                            "absolute is refused");
                Assert.Null(PathValidation.ResolveUnderRoot(null, "a.txt", out reason), "no root");
            });

            runner.Test("SanitiseSegment produces a usable folder name", delegate
            {
                Assert.Equal("bob", PathValidation.SanitiseSegment("bob", "fallback"), "already fine");
                Assert.Equal("DOMAIN_bob", PathValidation.SanitiseSegment(@"DOMAIN\bob", "fallback"),
                             "backslash replaced");
                Assert.Equal("a_b", PathValidation.SanitiseSegment("a:b", "fallback"), "colon replaced");
                Assert.Equal("fallback", PathValidation.SanitiseSegment("CON", "fallback"), "reserved name");
                Assert.Equal("fallback", PathValidation.SanitiseSegment("", "fallback"), "empty");
                Assert.Equal("fallback", PathValidation.SanitiseSegment("...", "fallback"),
                             "dots only becomes empty then falls back");
            });
        }
    }
}
