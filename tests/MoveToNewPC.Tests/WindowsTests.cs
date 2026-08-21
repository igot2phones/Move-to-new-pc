using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Native;
using MoveToNewPC.Core.Net;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Profiles;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Transfer;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// Tests that need a real Windows file system: long paths, reparse points, locked files,
    /// attribute and timestamp handling, and the scan/copy engines end to end.
    /// </summary>
    public static class WindowsTests
    {
        public static void Register(TestRunner runner)
        {
            RegisterWalker(runner);
            RegisterExclusions(runner);
            RegisterManifestIo(runner);
            RegisterJournal(runner);
            RegisterEngine(runner);
            RegisterPackage(runner);
            RegisterNetwork(runner);
            RegisterRestoreLayout(runner);
            RegisterProfiles(runner);
        }

        // ---- collecting observer ------------------------------------------------

        private sealed class Collector : IWalkObserver
        {
            public readonly List<string> Files = new List<string>();
            public readonly List<string> Directories = new List<string>();
            public readonly List<SkippedItem> Skips = new List<SkippedItem>();
            public long Bytes;

            public void OnDirectory(FsEntry entry, string relativePath) { Directories.Add(relativePath); }

            public void OnFile(FsEntry entry, string relativePath)
            {
                Files.Add(relativePath);
                Bytes += entry.Length;
            }

            public void OnSkip(string fullPath, string relativePath, bool isDirectory, SkipReason reason,
                               string detail, long length)
            {
                Skips.Add(new SkippedItem(relativePath ?? fullPath, isDirectory, reason, detail, length));
            }

            public void OnProgress(long entriesSeen, long filesSeen, long bytesSeen) { }

            public bool HasFile(string relativePath)
            {
                for (int i = 0; i < Files.Count; i++)
                {
                    if (string.Equals(Files[i], relativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            public SkippedItem FindSkip(SkipReason reason)
            {
                for (int i = 0; i < Skips.Count; i++)
                {
                    if (Skips[i].Reason == reason)
                    {
                        return Skips[i];
                    }
                }
                return null;
            }
        }

        private sealed class SilentScanObserver : IScanObserver
        {
            public void OnStatus(string message) { }
            public void OnProgress(long files, long bytes, long skipped, string currentPath) { }
            public void OnSkipped(SkippedItem item) { }
        }

        private sealed class SilentTransferObserver : ITransferObserver
        {
            public void OnStatus(string message) { }
            public void OnFileStarted(string s, string d, long length) { }
            public void OnBytesTransferred(long deltaBytes) { }
            public void OnFileCompleted(string s, long length) { }
            public void OnSkipped(SkippedItem item) { }
            public void OnTotals(long fd, long ft, long bd, long bt) { }
        }

        // ---- walker -------------------------------------------------------------

        private static void RegisterWalker(TestRunner runner)
        {
            runner.Group("Walker");

            runner.Test("finds files past MAX_PATH", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string deep = TestFs.MakeDeepPath(scratch, 10, "deep.txt");
                    Assert.True(LongPath.ToDisplay(deep).Length > 260,
                                "the test path really is over MAX_PATH (was "
                                + LongPath.ToDisplay(deep).Length + ")");
                    TestFs.WriteFile(deep, "deep content");

                    Collector collector = new Collector();
                    DirectoryWalker.Walk(scratch, new WalkOptions(), collector, CancellationToken.None);

                    Assert.Equal(1, collector.Files.Count, "exactly one file found");
                    Assert.True(collector.Files[0].EndsWith("deep.txt"), "and it is the deep one");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("handles zero-byte, large-name and unicode files", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    TestFs.WriteFile(LongPath.Combine(scratch, "empty.txt"), new byte[0]);
                    TestFs.WriteFile(LongPath.Combine(scratch, "caf\u00e9 \u65e5\u672c\u8a9e.txt"), "unicode");
                    TestFs.WriteFile(LongPath.Combine(scratch, "\u05e2\u05d1\u05e8\u05d9\u05ea.txt"), "rtl");
                    TestFs.WriteFile(LongPath.Combine(scratch, new string('n', 200) + ".txt"), "long name");

                    Collector collector = new Collector();
                    DirectoryWalker.Walk(scratch, new WalkOptions(), collector, CancellationToken.None);

                    Assert.Equal(4, collector.Files.Count, "all four found");
                    Assert.True(collector.HasFile("empty.txt"), "zero-byte file is enumerated");
                    Assert.True(collector.HasFile("caf\u00e9 \u65e5\u672c\u8a9e.txt"), "unicode name intact");
                    Assert.True(collector.HasFile("\u05e2\u05d1\u05e8\u05d9\u05ea.txt"), "RTL name intact");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("creates and enumerates reserved and trailing-dot names", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    // Only possible through the \\?\ layer. System.IO cannot make these.
                    TestFs.WriteFile(LongPath.Combine(scratch, "CON"), "reserved");
                    TestFs.WriteFile(LongPath.Combine(scratch, "trailing."), "dot");
                    TestFs.WriteFile(LongPath.Combine(scratch, "trailing "), "space");

                    Collector collector = new Collector();
                    DirectoryWalker.Walk(scratch, new WalkOptions(), collector, CancellationToken.None);

                    Assert.Equal(3, collector.Files.Count, "all three enumerated");
                    Assert.True(collector.HasFile("CON"), "reserved name enumerated");
                    Assert.True(collector.HasFile("trailing."), "trailing dot preserved");
                    Assert.True(collector.HasFile("trailing "), "trailing space preserved");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("does not follow junctions", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string real = LongPath.Combine(scratch, "real");
                    TestFs.WriteFile(LongPath.Combine(real, "inside.txt"), "content");

                    string link = LongPath.Combine(scratch, "link");
                    if (!TestFs.TryMakeJunction(link, real))
                    {
                        Assert.Skip("could not create a junction (mklink unavailable)");
                    }

                    Collector collector = new Collector();
                    DirectoryWalker.Walk(scratch, new WalkOptions(), collector, CancellationToken.None);

                    // One file, not two: the junction must not be descended into.
                    Assert.Equal(1, collector.Files.Count, "the junction was not followed");
                    Assert.NotNull(collector.FindSkip(SkipReason.ReparsePoint),
                                   "and it was reported as a reparse point");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a junction pointing at its own parent does not loop forever", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    TestFs.WriteFile(LongPath.Combine(scratch, "file.txt"), "content");
                    string selfLink = LongPath.Combine(scratch, "self");
                    if (!TestFs.TryMakeJunction(selfLink, scratch))
                    {
                        Assert.Skip("could not create a junction (mklink unavailable)");
                    }

                    Collector collector = new Collector();
                    DirectoryWalker.Walk(scratch, new WalkOptions(), collector, CancellationToken.None);

                    Assert.Equal(1, collector.Files.Count, "walk terminated with one file");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("hidden and system files respect the options", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string hidden = TestFs.WriteFile(LongPath.Combine(scratch, "hidden.txt"), "h");
                    string normal = TestFs.WriteFile(LongPath.Combine(scratch, "normal.txt"), "n");
                    int error;
                    NativeFile.SetAttributes(hidden, NativeMethods.FILE_ATTRIBUTE_HIDDEN, out error);

                    WalkOptions withHidden = new WalkOptions();
                    withHidden.IncludeHidden = true;
                    Collector a = new Collector();
                    DirectoryWalker.Walk(scratch, withHidden, a, CancellationToken.None);
                    Assert.Equal(2, a.Files.Count, "hidden included");

                    WalkOptions withoutHidden = new WalkOptions();
                    withoutHidden.IncludeHidden = false;
                    Collector b = new Collector();
                    DirectoryWalker.Walk(scratch, withoutHidden, b, CancellationToken.None);
                    Assert.Equal(1, b.Files.Count, "hidden excluded");
                    Assert.True(b.HasFile("normal.txt"), "the normal one survives");
                    GC.KeepAlive(normal);
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a missing root is reported, not thrown", delegate
            {
                Collector collector = new Collector();
                DirectoryWalker.Walk(@"C:\this-does-not-exist-" + Guid.NewGuid().ToString("N"),
                                     new WalkOptions(), collector, CancellationToken.None);
                Assert.Equal(0, collector.Files.Count, "no files");
                Assert.NotNull(collector.FindSkip(SkipReason.NotFound), "reported as not found");
            });

            runner.Test("cancellation stops the walk", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        TestFs.WriteFile(LongPath.Combine(scratch, "f" + i + ".txt"), "x");
                    }

                    using (CancellationTokenSource source = new CancellationTokenSource())
                    {
                        source.Cancel();
                        Collector collector = new Collector();
                        DirectoryWalker.Walk(scratch, new WalkOptions(), collector, source.Token);
                        Assert.True(collector.Files.Count < 50, "stopped early");
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        // ---- exclusions ---------------------------------------------------------

        private static void RegisterExclusions(TestRunner runner)
        {
            runner.Group("Exclusions");

            runner.Test("the default list drops the things it must", delegate
            {
                ExclusionRules rules = ExclusionRules.CreateDefault();
                string reason;

                Assert.True(rules.IsExcluded("Thumbs.db", @"C:\x\Thumbs.db", false, out reason), "Thumbs.db");
                Assert.True(rules.IsExcluded("desktop.ini", @"C:\x\desktop.ini", false, out reason), "desktop.ini");
                Assert.True(rules.IsExcluded("NTUSER.DAT", @"C:\x\NTUSER.DAT", false, out reason), "NTUSER.DAT");
                Assert.True(rules.IsExcluded("NTUSER.DAT.LOG1", @"C:\x\NTUSER.DAT.LOG1", false, out reason),
                            "NTUSER.DAT.LOG1");
                Assert.True(rules.IsExcluded("UsrClass.dat", @"C:\x\UsrClass.dat", false, out reason), "UsrClass.dat");
                Assert.True(rules.IsExcluded("a.mtnpc-part", @"C:\x\a.mtnpc-part", false, out reason),
                            "our own partial files");
                Assert.True(rules.IsExcluded(@"AppData\Local\Temp", @"C:\u\AppData\Local\Temp", true, out reason),
                            "the Temp folder");
                Assert.True(rules.IsExcluded(@"AppData\LocalLow", @"C:\u\AppData\LocalLow", true, out reason),
                            "LocalLow");
            });

            runner.Test("ordinary files are not excluded", delegate
            {
                ExclusionRules rules = ExclusionRules.CreateDefault();
                string reason;

                Assert.False(rules.IsExcluded(@"Documents\report.docx", @"C:\u\Documents\report.docx",
                                              false, out reason), "a document");
                Assert.False(rules.IsExcluded("holiday.jpg", @"C:\u\Pictures\holiday.jpg", false, out reason),
                             "a photo");
                Assert.False(rules.IsExcluded("Templates", @"C:\u\Templates", true, out reason),
                             "\"Templates\" is not \"Temp\"");
            });

            runner.Test("the Windows folder is refused as a source", delegate
            {
                ExclusionRules rules = ExclusionRules.CreateDefault();
                string reason;
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                Assert.True(rules.IsExcluded("System32", LongPath.Combine(windows, "System32"), true, out reason),
                            "inside the Windows directory");
            });

            runner.Test("file filters apply size, extension and pattern", delegate
            {
                FilterSettings settings = new FilterSettings();
                settings.IncludeExtensions.Add(".jpg");
                settings.MinSizeBytes = 100;

                IFileFilter filter = FileFilter.CreateOrNull(settings);
                Assert.NotNull(filter, "a non-empty filter is created");

                FsEntry big = new FsEntry();
                big.Name = "photo.jpg";
                big.Length = 5000;

                FsEntry small = new FsEntry();
                small.Name = "thumb.jpg";
                small.Length = 10;

                FsEntry wrongType = new FsEntry();
                wrongType.Name = "notes.txt";
                wrongType.Length = 5000;

                string rule;
                Assert.True(filter.Accept(big, "photo.jpg", out rule), "large jpg accepted");
                Assert.False(filter.Accept(small, "thumb.jpg", out rule), "small jpg rejected");
                Assert.False(filter.Accept(wrongType, "notes.txt", out rule), "txt rejected");

                Assert.Null(FileFilter.CreateOrNull(new FilterSettings()),
                            "an empty filter is null so the walker can skip it");
            });
        }

        // ---- manifest -----------------------------------------------------------

        private static void RegisterManifestIo(TestRunner runner)
        {
            runner.Group("Manifest round-trip");

            runner.Test("writes and reads back awkward names exactly", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string path = LongPath.ToDisplay(LongPath.Combine(scratch, "test.mtnpc-manifest"));

                    TransferManifest manifest = new TransferManifest();
                    manifest.ManifestId = "abc123";
                    manifest.CreatedUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                    manifest.SourceMachine = "OLD-PC";
                    manifest.ToolVersion = "0.3.0.0";

                    ManifestUser user = new ManifestUser();
                    user.UserIndex = 0;
                    user.Sid = "S-1-5-21-1-2-3-1001";
                    user.AccountName = "bob";
                    user.ProfilePath = @"C:\Users\bob";
                    user.DestinationHint = "bob";

                    ManifestRoot root = new ManifestRoot();
                    root.UserIndex = 0;
                    root.RootIndex = 0;
                    root.Tier = SelectionTier.KnownFolder;
                    root.SourcePath = @"C:\Users\bob\Documents";
                    root.DestinationRelativeRoot = "Documents";
                    root.Label = "Documents";
                    user.Roots.Add(root);
                    manifest.Users.Add(user);

                    string[] awkward = new string[]
                    {
                        @"normal\file.txt",
                        "caf\u00e9 \u65e5\u672c\u8a9e.txt",
                        @"has\ttab-looking-name.txt",
                        "trailing dot.",
                        @"deep\" + new string('x', 200) + ".txt"
                    };

                    using (ManifestWriter writer = new ManifestWriter(path, manifest))
                    {
                        ManifestDirectory directory = new ManifestDirectory();
                        directory.RelativePath = "normal";
                        directory.LastWriteTimeUtc = 130000000000000000L;
                        writer.WriteDirectory(directory);

                        for (int i = 0; i < awkward.Length; i++)
                        {
                            ManifestEntry entry = new ManifestEntry();
                            entry.RelativePath = awkward[i];
                            entry.Length = 1000 + i;
                            entry.Attributes = NativeMethods.FILE_ATTRIBUTE_ARCHIVE;
                            entry.LastWriteTimeUtc = 130000000000000000L + i;
                            writer.WriteFile(entry);
                        }

                        writer.WriteSkip(0, 0, "locked.txt", SkipReason.Locked, 42, "In use by Outlook");

                        ManifestTotals totals = new ManifestTotals();
                        totals.FileCount = awkward.Length;
                        totals.ByteCount = 5010;
                        totals.DirectoryCount = 1;
                        totals.SkippedCount = 1;
                        writer.WriteTotals(totals);
                    }

                    List<string> readBack = new List<string>();
                    int skips = 0;
                    int directories = 0;

                    using (ManifestReader reader = new ManifestReader(path))
                    {
                        Assert.Equal("abc123", reader.Manifest.ManifestId, "manifest id");
                        Assert.Equal("OLD-PC", reader.Manifest.SourceMachine, "machine name");
                        Assert.Equal(1, reader.Manifest.Users.Count, "one user");
                        Assert.Equal("bob", reader.Manifest.Users[0].AccountName, "account name");
                        Assert.Equal(1, reader.Manifest.Users[0].Roots.Count, "one root");
                        Assert.Equal(@"C:\Users\bob\Documents",
                                     reader.Manifest.Users[0].Roots[0].SourcePath, "root path");

                        ManifestRecord record = new ManifestRecord();
                        while (reader.Read(record))
                        {
                            if (record.Kind == ManifestRecordKind.File)
                            {
                                readBack.Add(record.File.RelativePath);
                            }
                            else if (record.Kind == ManifestRecordKind.Skip)
                            {
                                skips++;
                                Assert.Equal("In use by Outlook", record.Skip.Detail, "skip detail");
                            }
                            else if (record.Kind == ManifestRecordKind.Directory)
                            {
                                directories++;
                            }
                        }
                    }

                    Assert.Equal(awkward.Length, readBack.Count, "all files read back");
                    for (int i = 0; i < awkward.Length; i++)
                    {
                        Assert.Equal(awkward[i], readBack[i], "path " + i + " survived the round trip");
                    }
                    Assert.Equal(1, skips, "the skip record");
                    Assert.Equal(1, directories, "the directory record");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("refuses a manifest from a future format version", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string path = LongPath.ToDisplay(LongPath.Combine(scratch, "future.mtnpc-manifest"));
                    File.WriteAllText(path, "MTNPC-MANIFEST\t99\n", new UTF8Encoding(false));

                    Assert.Throws(typeof(InvalidDataException), delegate
                    {
                        using (ManifestReader reader = new ManifestReader(path))
                        {
                            GC.KeepAlive(reader);
                        }
                    }, "an unknown version is refused rather than guessed at");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        // ---- journal ------------------------------------------------------------

        private static void RegisterJournal(TestRunner runner)
        {
            runner.Group("Resume journal");

            runner.Test("replays completed entries after a restart", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string path = LongPath.ToDisplay(LongPath.Combine(scratch, "t" + CompletionJournal.FileExtension));

                    using (CompletionJournal journal = CompletionJournal.OpenOrCreate(path, "manifest-1"))
                    {
                        journal.MarkComplete(0, 0, @"Documents\a.txt", 100, "aa");
                        journal.MarkComplete(0, 0, "caf\u00e9.txt", 200, "bb");
                        journal.MarkFailed(0, 0, "locked.txt", SkipReason.Locked, "in use");
                    }

                    using (CompletionJournal reopened = CompletionJournal.OpenOrCreate(path, "manifest-1"))
                    {
                        Assert.Equal(2, reopened.CompletedCount, "two completed entries replayed");
                        Assert.Equal(300, reopened.CompletedBytes, "bytes replayed");
                        Assert.True(reopened.IsComplete(0, 0, @"Documents\a.txt"), "first file remembered");
                        Assert.True(reopened.IsComplete(0, 0, "caf\u00e9.txt"), "unicode name remembered");
                        Assert.False(reopened.IsComplete(0, 0, "locked.txt"),
                                     "a failed file is retried, not treated as done");
                        Assert.False(reopened.IsComplete(0, 0, "never-seen.txt"), "unknown file");
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a journal from a different transfer is discarded", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string path = LongPath.ToDisplay(LongPath.Combine(scratch, "t" + CompletionJournal.FileExtension));

                    using (CompletionJournal journal = CompletionJournal.OpenOrCreate(path, "manifest-1"))
                    {
                        journal.MarkComplete(0, 0, "a.txt", 100, "aa");
                    }

                    using (CompletionJournal other = CompletionJournal.OpenOrCreate(path, "manifest-2"))
                    {
                        Assert.Equal(0, other.CompletedCount, "nothing carried over from the other transfer");
                        Assert.False(other.IsComplete(0, 0, "a.txt"), "and that file will be sent again");
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        // ---- end to end ---------------------------------------------------------

        private static TransferSelection BuildSelection(string sourceRoot, string label)
        {
            UserProfile profile = new UserProfile();
            profile.Sid = "S-1-5-21-0-0-0-1001";
            profile.AccountName = "testuser";
            profile.ProfilePath = sourceRoot;
            profile.ProfileExists = true;

            SelectionRoot root = new SelectionRoot();
            root.Tier = SelectionTier.Custom;
            root.Label = label;
            root.SourcePath = sourceRoot;
            root.DestinationRelativeRoot = label;
            root.Selected = true;
            root.Exists = true;

            UserSelection user = new UserSelection();
            user.Profile = profile;
            user.Selected = true;
            user.Roots.Add(root);

            TransferSelection selection = new TransferSelection();
            selection.Users.Add(user);
            selection.Exclusions = ExclusionRules.CreateDefault();
            selection.IncludeHidden = true;
            return selection;
        }

        private static TransferResult RunTransfer(string sourceRoot, string destinationRoot,
                                                  CopyOptions options, out TransferManifest manifest)
        {
            string manifestPath = LongPath.ToDisplay(
                LongPath.Combine(destinationRoot, "run.mtnpc-manifest"));

            int error;
            NativeFile.CreateDirectoryRecursive(destinationRoot, out error);

            TransferSelection selection = BuildSelection(sourceRoot, "Data");
            ScanEngine scanner = new ScanEngine(selection, options);
            manifest = scanner.Scan(manifestPath, new SilentScanObserver(), CancellationToken.None);

            using (CompletionJournal journal = CompletionJournal.OpenOrCreate(
                       LongPath.ToDisplay(LongPath.Combine(destinationRoot, "run" + CompletionJournal.FileExtension)),
                       manifest.ManifestId))
            using (LocalFolderSink sink = new LocalFolderSink(destinationRoot, options, journal))
            {
                TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                using (PauseGate gate = new PauseGate())
                {
                    return engine.Run(manifest, manifestPath, CancellationToken.None, gate);
                }
            }
        }

        private static void RegisterEngine(TestRunner runner)
        {
            runner.Group("Scan and copy, end to end");

            runner.Test("copies content, names and sizes faithfully", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");

                    TestFs.WriteFile(LongPath.Combine(source, "plain.txt"), "hello world");
                    TestFs.WriteFile(LongPath.Combine(source, "empty.txt"), new byte[0]);
                    TestFs.WriteFile(LongPath.Combine(source, "caf\u00e9 \u65e5\u672c.txt"), "unicode body");
                    TestFs.WriteFile(LongPath.Combine(source, @"sub\nested.txt"), "nested body");
                    TestFs.WriteFile(TestFs.MakeDeepPath(LongPath.Combine(source, "deep"), 9, "far.txt"),
                                     "a long way down");

                    byte[] binary = new byte[300000];
                    for (int i = 0; i < binary.Length; i++)
                    {
                        binary[i] = (byte)(i * 31);
                    }
                    TestFs.WriteFile(LongPath.Combine(source, "binary.bin"), binary);

                    TransferManifest manifest;
                    TransferResult result = RunTransfer(source, destination, CopyOptions.Defaults(), out manifest);

                    Assert.Equal(0, result.FilesFailed, "nothing failed");
                    Assert.Equal(6, result.FilesCopied, "six files copied");

                    string userRoot = LongPath.Combine(destination, "testuser");
                    string dataRoot = LongPath.Combine(userRoot, "Data");

                    Assert.Equal("hello world", TestFs.ReadAllText(LongPath.Combine(dataRoot, "plain.txt")),
                                 "plain content");
                    Assert.Equal("unicode body",
                                 TestFs.ReadAllText(LongPath.Combine(dataRoot, "caf\u00e9 \u65e5\u672c.txt")),
                                 "unicode file content");
                    Assert.Equal("nested body",
                                 TestFs.ReadAllText(LongPath.Combine(dataRoot, @"sub\nested.txt")), "nested content");
                    Assert.Equal("a long way down",
                                 TestFs.ReadAllText(TestFs.MakeDeepPath(LongPath.Combine(dataRoot, "deep"), 9, "far.txt")),
                                 "content past MAX_PATH");
                    Assert.Equal(0, TestFs.ReadAllBytes(LongPath.Combine(dataRoot, "empty.txt")).Length,
                                 "zero-byte file is still zero bytes");

                    byte[] copiedBinary = TestFs.ReadAllBytes(LongPath.Combine(dataRoot, "binary.bin"));
                    Assert.Equal(binary.Length, copiedBinary.Length, "binary length");
                    for (int i = 0; i < binary.Length; i += 4093)
                    {
                        Assert.Equal(binary[i], copiedBinary[i], "binary byte " + i);
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("preserves timestamps and the portable attributes", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");

                    string file = TestFs.WriteFile(LongPath.Combine(source, "stamped.txt"), "content");
                    DateTime when = new DateTime(2014, 7, 4, 12, 30, 15, DateTimeKind.Utc);
                    long fileTime = when.ToFileTimeUtc();

                    int error;
                    NativeFile.SetTimes(file, fileTime, fileTime, fileTime, out error);
                    NativeFile.SetAttributes(file, NativeMethods.FILE_ATTRIBUTE_READONLY, out error);

                    TransferManifest manifest;
                    TransferResult result = RunTransfer(source, destination, CopyOptions.Defaults(), out manifest);
                    Assert.Equal(1, result.FilesCopied, "copied");

                    string copied = LongPath.Combine(LongPath.Combine(destination, "testuser"),
                                                     LongPath.Combine("Data", "stamped.txt"));

                    NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data;
                    Assert.True(NativeFile.TryGetInfo(copied, out data, out error), "destination file exists");
                    Assert.Equal(fileTime, data.ftLastWriteTime.ToTicks(), "last write time preserved");
                    Assert.True((data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_READONLY) != 0,
                                "read-only attribute preserved");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("collision policies behave as documented", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    TestFs.WriteFile(LongPath.Combine(source, "file.txt"), "NEW");

                    // Skip: existing content is left alone.
                    string skipDestination = LongPath.Combine(scratch, "dst-skip");
                    string existing = LongPath.Combine(skipDestination,
                        LongPath.Combine("testuser", LongPath.Combine("Data", "file.txt")));
                    TestFs.WriteFile(existing, "OLD");

                    TransferManifest manifest;
                    CopyOptions skipOptions = CopyOptions.Defaults();
                    skipOptions.Collision = CollisionPolicy.Skip;
                    TransferResult skipResult = RunTransfer(source, skipDestination, skipOptions, out manifest);

                    Assert.Equal("OLD", TestFs.ReadAllText(existing), "Skip leaves the existing file alone");
                    Assert.Equal(0, skipResult.FilesCopied, "and copies nothing");
                    Assert.Equal(1, skipResult.FilesSkipped, "and says so");

                    // Overwrite: replaced.
                    string overwriteDestination = LongPath.Combine(scratch, "dst-over");
                    string overwriteExisting = LongPath.Combine(overwriteDestination,
                        LongPath.Combine("testuser", LongPath.Combine("Data", "file.txt")));
                    TestFs.WriteFile(overwriteExisting, "OLD");

                    CopyOptions overwriteOptions = CopyOptions.Defaults();
                    overwriteOptions.Collision = CollisionPolicy.Overwrite;
                    RunTransfer(source, overwriteDestination, overwriteOptions, out manifest);
                    Assert.Equal("NEW", TestFs.ReadAllText(overwriteExisting), "Overwrite replaces it");

                    // KeepBoth: both survive.
                    string keepDestination = LongPath.Combine(scratch, "dst-keep");
                    string keepExisting = LongPath.Combine(keepDestination,
                        LongPath.Combine("testuser", LongPath.Combine("Data", "file.txt")));
                    TestFs.WriteFile(keepExisting, "OLD");

                    CopyOptions keepOptions = CopyOptions.Defaults();
                    keepOptions.Collision = CollisionPolicy.KeepBoth;
                    RunTransfer(source, keepDestination, keepOptions, out manifest);

                    Assert.Equal("OLD", TestFs.ReadAllText(keepExisting), "KeepBoth leaves the original");
                    string sibling = LongPath.Combine(keepDestination,
                        LongPath.Combine("testuser", LongPath.Combine("Data", "file (1).txt")));
                    Assert.Equal("NEW", TestFs.ReadAllText(sibling), "and writes the new one beside it");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a locked file is skipped with a reason and the run continues", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");

                    string locked = TestFs.WriteFile(LongPath.Combine(source, "locked.txt"), "secret");
                    TestFs.WriteFile(LongPath.Combine(source, "readable.txt"), "fine");

                    SafeFileHandle handle = NativeMethods.CreateFileW(
                        LongPath.ToExtended(locked),
                        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                        0,                              // deny all sharing
                        IntPtr.Zero,
                        NativeMethods.OPEN_EXISTING,
                        NativeMethods.FILE_ATTRIBUTE_NORMAL,
                        IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        Assert.Skip("could not take an exclusive lock");
                    }

                    try
                    {
                        CopyOptions options = CopyOptions.Defaults();
                        options.RetryCount = 1;
                        options.RetryDelayMs = 10;

                        TransferManifest manifest;
                        TransferResult result = RunTransfer(source, destination, options, out manifest);

                        Assert.Equal(1, result.FilesCopied, "the readable file still copied");
                        Assert.Equal(1, result.FilesFailed, "the locked one is counted as failed");

                        bool found = false;
                        for (int i = 0; i < result.Skipped.Count; i++)
                        {
                            if (result.Skipped[i].Reason == SkipReason.Locked
                                || result.Skipped[i].Reason == SkipReason.AccessDenied)
                            {
                                found = true;
                            }
                        }
                        Assert.True(found, "and it is reported as locked or denied, with a reason");
                    }
                    finally
                    {
                        handle.Dispose();
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("resuming skips what already arrived", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");

                    for (int i = 0; i < 5; i++)
                    {
                        TestFs.WriteFile(LongPath.Combine(source, "f" + i + ".txt"), "body " + i);
                    }

                    TransferManifest manifest;
                    TransferResult first = RunTransfer(source, destination, CopyOptions.Defaults(), out manifest);
                    Assert.Equal(5, first.FilesCopied, "all five copied the first time");

                    // Same manifest id: the journal must recognise every file as already done.
                    string manifestPath = LongPath.ToDisplay(LongPath.Combine(destination, "run.mtnpc-manifest"));
                    CopyOptions options = CopyOptions.Defaults();

                    using (CompletionJournal journal = CompletionJournal.OpenOrCreate(
                               LongPath.ToDisplay(LongPath.Combine(destination, "run" + CompletionJournal.FileExtension)),
                               manifest.ManifestId))
                    {
                        Assert.Equal(5, journal.CompletedCount, "the journal recorded all five");

                        using (LocalFolderSink sink = new LocalFolderSink(destination, options, journal))
                        using (PauseGate gate = new PauseGate())
                        {
                            TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                            TransferResult second = engine.Run(manifest, manifestPath, CancellationToken.None, gate);

                            Assert.Equal(0, second.FilesCopied, "the second run copies nothing");
                            Assert.Equal(5, second.FilesSkipped, "and skips all five as already transferred");
                        }
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a dry run writes nothing at all", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    TestFs.WriteFile(LongPath.Combine(source, "a.txt"), "content");
                    TestFs.WriteFile(LongPath.Combine(source, @"sub\b.txt"), "content");

                    int error;
                    NativeFile.CreateDirectoryRecursive(destination, out error);

                    string manifestPath = LongPath.ToDisplay(LongPath.Combine(destination, "dry.mtnpc-manifest"));
                    CopyOptions options = CopyOptions.Defaults();
                    options.DryRun = true;

                    TransferSelection selection = BuildSelection(source, "Data");
                    ScanEngine scanner = new ScanEngine(selection, options);
                    TransferManifest manifest = scanner.Scan(manifestPath, new SilentScanObserver(),
                                                             CancellationToken.None);

                    using (NullSink sink = new NullSink())
                    using (PauseGate gate = new PauseGate())
                    {
                        TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                        TransferResult result = engine.Run(manifest, manifestPath, CancellationToken.None, gate);

                        Assert.Equal(2, result.FilesCopied, "the dry run accounts for both files");
                        Assert.Equal(2, sink.Files, "and the null sink saw both");
                        Assert.False(NativeFile.DirectoryExists(LongPath.Combine(destination, "testuser")),
                                     "but nothing was written to disk");
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("no .mtnpc-part files are left behind", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    TestFs.WriteFile(LongPath.Combine(source, "a.txt"), "content");
                    TestFs.WriteFile(LongPath.Combine(source, "b.txt"), "content");

                    TransferManifest manifest;
                    RunTransfer(source, destination, CopyOptions.Defaults(), out manifest);

                    Collector collector = new Collector();
                    WalkOptions options = new WalkOptions();
                    DirectoryWalker.Walk(destination, options, collector, CancellationToken.None);

                    for (int i = 0; i < collector.Files.Count; i++)
                    {
                        Assert.False(collector.Files[i].EndsWith(LocalFolderSink.PartialExtension,
                                                                 StringComparison.OrdinalIgnoreCase),
                                     "no partial file survived: " + collector.Files[i]);
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("the destination stays inside the chosen folder", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string destination = LongPath.Combine(scratch, "dst");
                    int error;
                    NativeFile.CreateDirectoryRecursive(destination, out error);

                    TransferManifest manifest = new TransferManifest();
                    manifest.ManifestId = "x";
                    ManifestUser user = new ManifestUser();
                    user.UserIndex = 0;
                    user.AccountName = "victim";
                    user.DestinationHint = "victim";

                    ManifestRoot root = new ManifestRoot();
                    root.UserIndex = 0;
                    root.RootIndex = 0;
                    root.DestinationRelativeRoot = "Data";
                    root.SourcePath = @"C:\nowhere";
                    user.Roots.Add(root);
                    manifest.Users.Add(user);

                    using (LocalFolderSink sink = new LocalFolderSink(destination, CopyOptions.Defaults(), null))
                    {
                        sink.BeginSession(manifest);

                        ManifestEntry hostile = new ManifestEntry();
                        hostile.UserIndex = 0;
                        hostile.RootIndex = 0;
                        hostile.RelativePath = @"..\..\..\..\Windows\System32\pwned.dll";
                        hostile.Length = 4;

                        string destinationPath;
                        Assert.Throws(typeof(TransferVerificationException), delegate
                        {
                            sink.BeginFile(user, root, hostile, out destinationPath);
                        }, "a traversal path is refused by the sink itself");
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        // ---- profiles -----------------------------------------------------------

        // ---- encrypted package (M6) ---------------------------------------------

        /// <summary>
        /// Packs a source tree into an encrypted package and restores it somewhere else,
        /// returning the restore result.
        /// </summary>
        private static TransferResult PackAndRestore(string source, string packagePath,
                                                     string destination, string writePassphrase,
                                                     string readPassphrase, out string openError)
        {
            CopyOptions options = CopyOptions.Defaults();
            string manifestPath = LongPath.ToDisplay(LongPath.Combine(
                LongPath.GetDirectoryName(packagePath), "pack.mtnpc-manifest"));

            TransferSelection selection = BuildSelection(source, "Data");
            ScanEngine scanner = new ScanEngine(selection, options);
            TransferManifest manifest = scanner.Scan(manifestPath, new SilentScanObserver(), CancellationToken.None);

            using (PackageSink sink = new PackageSink(packagePath, writePassphrase))
            using (PauseGate gate = new PauseGate())
            {
                TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                engine.Run(manifest, manifestPath, CancellationToken.None, gate);
            }

            int error;
            NativeFile.CreateDirectoryRecursive(destination, out error);

            using (PackageReader reader = PackageReader.Open(packagePath, readPassphrase, out openError))
            {
                if (reader == null)
                {
                    return null;
                }

                using (CompletionJournal journal = CompletionJournal.OpenOrCreate(
                           LongPath.ToDisplay(LongPath.Combine(destination, "restore" + CompletionJournal.FileExtension)),
                           reader.Manifest.ManifestId))
                using (LocalFolderSink target = new LocalFolderSink(destination, options, journal))
                using (PauseGate gate = new PauseGate())
                {
                    return reader.Restore(target, new SilentTransferObserver(), CancellationToken.None, gate);
                }
            }
        }

        private static void RegisterPackage(TestRunner runner)
        {
            runner.Group("Encrypted package");

            runner.Test("round-trips content, names and sizes through the package", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    string package = LongPath.ToDisplay(LongPath.Combine(scratch, "carry" + PackageSink.FileExtension));

                    TestFs.WriteFile(LongPath.Combine(source, "notes.txt"), "hello package");
                    TestFs.WriteFile(LongPath.Combine(source, "sub\\deeper\\data.bin"), new byte[] { 1, 2, 3, 4, 5 });
                    TestFs.WriteFile(LongPath.Combine(source, "unicode-åäö-ΑΒ.txt"), "greek and nordic");
                    TestFs.WriteFile(LongPath.Combine(source, "empty.txt"), string.Empty);

                    string openError;
                    TransferResult result = PackAndRestore(source, package, destination,
                                                           "correct horse battery", "correct horse battery",
                                                           out openError);

                    Assert.NotNull(result, "the package opened");
                    Assert.Equal(4, result.FilesCopied, "all four files restored");
                    Assert.Equal(0, result.FilesFailed, "nothing failed");

                    // The sink maps into <destination>\<account>\<root label>\...
                    string restoredRoot = LongPath.Combine(LongPath.Combine(destination, "testuser"), "Data");
                    Assert.Equal("hello package",
                                 TestFs.ReadAllText(LongPath.Combine(restoredRoot, "notes.txt")), "content survives");
                    Assert.Equal("greek and nordic",
                                 TestFs.ReadAllText(LongPath.Combine(restoredRoot, "unicode-åäö-ΑΒ.txt")),
                                 "unicode names survive");
                    Assert.Equal(5,
                                 TestFs.ReadAllBytes(LongPath.Combine(restoredRoot, "sub\\deeper\\data.bin")).Length,
                                 "nested binary survives");
                    Assert.Equal(0,
                                 TestFs.ReadAllBytes(LongPath.Combine(restoredRoot, "empty.txt")).Length,
                                 "zero-byte file survives");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a file larger than one frame survives intact", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    string package = LongPath.ToDisplay(LongPath.Combine(scratch, "big" + PackageSink.FileExtension));

                    // Deliberately spans several 64 KB frames and is not a multiple of the
                    // block size, so padding and frame boundaries both get exercised.
                    byte[] payload = new byte[300000];
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(i * 31);
                    }
                    TestFs.WriteFile(LongPath.Combine(source, "large.bin"), payload);

                    string openError;
                    TransferResult result = PackAndRestore(source, package, destination,
                                                           "pw", "pw", out openError);

                    Assert.NotNull(result, "the package opened");
                    Assert.Equal(1, result.FilesCopied, "the large file restored");

                    byte[] restored = TestFs.ReadAllBytes(LongPath.Combine(
                        LongPath.Combine(LongPath.Combine(destination, "testuser"), "Data"), "large.bin"));
                    Assert.Equal(payload.Length, restored.Length, "length matches");

                    bool identical = true;
                    for (int i = 0; i < payload.Length; i++)
                    {
                        if (payload[i] != restored[i]) { identical = false; break; }
                    }
                    Assert.True(identical, "every byte matches");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("the wrong password is refused and nothing is written", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    string package = LongPath.ToDisplay(LongPath.Combine(scratch, "secret" + PackageSink.FileExtension));

                    TestFs.WriteFile(LongPath.Combine(source, "private.txt"), "confidential");

                    string openError;
                    TransferResult result = PackAndRestore(source, package, destination,
                                                           "the-right-one", "the-WRONG-one", out openError);

                    Assert.Null(result, "the restore did not run");
                    Assert.NotNull(openError, "a reason was given");
                    Assert.True(openError.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0,
                                "the reason mentions the password: " + openError);

                    // Nothing from the package may reach the disk on a bad passphrase.
                    Assert.False(NativeFile.Exists(LongPath.Combine(LongPath.Combine(
                                     LongPath.Combine(destination, "testuser"), "Data"), "private.txt")),
                                 "no file was written");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a tampered package is rejected, not partially restored", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    string package = LongPath.ToDisplay(LongPath.Combine(scratch, "tamper" + PackageSink.FileExtension));

                    TestFs.WriteFile(LongPath.Combine(source, "a.txt"), new string('a', 5000));

                    CopyOptions options = CopyOptions.Defaults();
                    string manifestPath = LongPath.ToDisplay(LongPath.Combine(scratch, "t.mtnpc-manifest"));
                    TransferSelection selection = BuildSelection(source, "Data");
                    ScanEngine scanner = new ScanEngine(selection, options);
                    TransferManifest manifest = scanner.Scan(manifestPath, new SilentScanObserver(), CancellationToken.None);

                    using (PackageSink sink = new PackageSink(package, "pw"))
                    using (PauseGate gate = new PauseGate())
                    {
                        TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                        engine.Run(manifest, manifestPath, CancellationToken.None, gate);
                    }

                    // Flip a byte deep inside the ciphertext, past the header and first frame
                    // header, so it lands in encrypted payload rather than metadata.
                    byte[] raw = File.ReadAllBytes(package);
                    int target = raw.Length - 200;
                    raw[target] = (byte)(raw[target] ^ 0xFF);
                    File.WriteAllBytes(package, raw);

                    int error;
                    NativeFile.CreateDirectoryRecursive(destination, out error);

                    string openError;
                    bool rejected = false;
                    using (PackageReader reader = PackageReader.Open(package, "pw", out openError))
                    {
                        if (reader == null)
                        {
                            rejected = true;
                        }
                        else
                        {
                            using (LocalFolderSink target2 = new LocalFolderSink(destination, options, null))
                            using (PauseGate gate = new PauseGate())
                            {
                                TransferResult r = reader.Restore(target2, new SilentTransferObserver(),
                                                                  CancellationToken.None, gate);
                                // The MAC must fail; the restore reports it rather than
                                // pretending the transfer was clean.
                                rejected = r.FailureMessage != null || r.FilesFailed > 0;
                            }
                        }
                    }

                    Assert.True(rejected, "tampering was detected and reported");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("an interrupted package leaves no usable file behind", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string package = LongPath.ToDisplay(LongPath.Combine(scratch, "aborted" + PackageSink.FileExtension));

                    // Dispose without EndSession: the same shape as a crash mid-write.
                    using (PackageSink sink = new PackageSink(package, "pw"))
                    {
                        TransferManifest manifest = new TransferManifest();
                        manifest.ManifestId = "abc";
                        manifest.CreatedUtc = DateTime.UtcNow;
                        manifest.SourceMachine = "test";
                        manifest.ToolVersion = "test";
                        sink.BeginSession(manifest);
                    }

                    Assert.False(File.Exists(package), "no finished package was left");
                    Assert.False(File.Exists(package + ".mtnpc-part"), "no part file was left either");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        // ---- LAN transport (M3) --------------------------------------------------

        /// <summary>Holds what the receiver thread produced, so the test thread can assert on it.</summary>
        private sealed class ReceiverOutcome
        {
            public TransferResult Result;
            public Exception Failure;
            public string PeerMachine;
        }

        /// <summary>
        /// Runs a real paired transfer over loopback. Returns the receiver's result, or
        /// leaves Failure set if the receiver threw.
        /// </summary>
        private static ReceiverOutcome RunLanTransfer(string source, string destination, int port,
                                                      string senderCode, out TransferResult sendResult,
                                                      out Exception sendFailure)
        {
            ReceiverOutcome outcome = new ReceiverOutcome();
            CopyOptions options = CopyOptions.Defaults();

            NetworkReceiver receiver = new NetworkReceiver(port);
            receiver.Start(false);          // no beacon: loopback needs no discovery
            string realCode = receiver.PairingCode;

            CancellationTokenSource receiverCancel = new CancellationTokenSource();
            string destinationCopy = destination;

            Thread receiverThread = new Thread(delegate()
            {
                try
                {
                    using (PauseGate gate = new PauseGate())
                    using (SecureChannel channel = receiver.AcceptOnePeer(receiverCancel.Token))
                    {
                        if (channel != null)
                        {
                            using (LocalFolderSink sink = new LocalFolderSink(destinationCopy, options, null))
                            {
                                outcome.Result = receiver.ReceiveInto(
                                    channel, sink, new SilentTransferObserver(),
                                    delegate(TransferManifestInfo info)
                                    {
                                        outcome.PeerMachine = info.PeerMachineName;
                                    },
                                    receiverCancel.Token, gate);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    outcome.Failure = ex;
                }
            });
            receiverThread.IsBackground = true;
            receiverThread.Start();

            // Give the listener a moment to reach Pending().
            Thread.Sleep(300);

            sendResult = null;
            sendFailure = null;
            try
            {
                string code = senderCode ?? realCode;
                using (SecureChannel channel = SecureChannel.Connect("127.0.0.1", port, "test-sender", code))
                {
                    string manifestPath = LongPath.ToDisplay(LongPath.Combine(
                        LongPath.GetDirectoryName(destination), "lan.mtnpc-manifest"));

                    TransferSelection selection = BuildSelection(source, "Data");
                    ScanEngine scanner = new ScanEngine(selection, options);
                    TransferManifest manifest = scanner.Scan(manifestPath, new SilentScanObserver(),
                                                             CancellationToken.None);

                    using (NetworkSink sink = new NetworkSink(channel, false))
                    using (PauseGate gate = new PauseGate())
                    {
                        TransferEngine engine = new TransferEngine(options, sink, new SilentTransferObserver());
                        sendResult = engine.Run(manifest, manifestPath, CancellationToken.None, gate);
                    }
                }
            }
            catch (Exception ex)
            {
                sendFailure = ex;
            }

            if (!receiverThread.Join(30000))
            {
                receiverCancel.Cancel();
                receiverThread.Join(5000);
            }
            receiver.Dispose();
            receiverCancel.Dispose();
            return outcome;
        }

        private static void RegisterNetwork(TestRunner runner)
        {
            runner.Group("LAN transfer");

            runner.Test("a paired transfer moves files over a socket", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");

                    TestFs.WriteFile(LongPath.Combine(source, "notes.txt"), "sent over the wire");
                    TestFs.WriteFile(LongPath.Combine(source, "sub\\nested.bin"), new byte[] { 9, 8, 7, 6 });
                    TestFs.WriteFile(LongPath.Combine(source, "unicode-ΑΒΓ-日本.txt"), "unicode on the wire");

                    // Deliberately spans several frames.
                    byte[] payload = new byte[200000];
                    for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i * 17); }
                    TestFs.WriteFile(LongPath.Combine(source, "large.bin"), payload);

                    TransferResult sendResult;
                    Exception sendFailure;
                    ReceiverOutcome outcome = RunLanTransfer(source, destination, 51799, null,
                                                             out sendResult, out sendFailure);

                    Assert.Null(sendFailure, "the sender did not throw");
                    Assert.Null(outcome.Failure, "the receiver did not throw");
                    Assert.NotNull(outcome.Result, "the receiver produced a result");
                    Assert.Equal(4, outcome.Result.FilesCopied, "all four files arrived");
                    Assert.Equal(0, outcome.Result.FilesFailed, "nothing failed");
                    Assert.Equal("test-sender", outcome.PeerMachine, "the peer name came across");

                    string root = LongPath.Combine(LongPath.Combine(destination, "testuser"), "Data");
                    Assert.Equal("sent over the wire",
                                 TestFs.ReadAllText(LongPath.Combine(root, "notes.txt")), "content survives");
                    Assert.Equal("unicode on the wire",
                                 TestFs.ReadAllText(LongPath.Combine(root, "unicode-ΑΒΓ-日本.txt")),
                                 "unicode names survive");

                    byte[] arrived = TestFs.ReadAllBytes(LongPath.Combine(root, "large.bin"));
                    Assert.Equal(payload.Length, arrived.Length, "the large file is the right size");

                    bool identical = true;
                    for (int i = 0; i < payload.Length; i++)
                    {
                        if (payload[i] != arrived[i]) { identical = false; break; }
                    }
                    Assert.True(identical, "every byte of the large file matches");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("the wrong pairing code is refused and nothing is written", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    TestFs.WriteFile(LongPath.Combine(source, "private.txt"), "must not arrive");

                    TransferResult sendResult;
                    Exception sendFailure;
                    // "000000" will not match a randomly generated code except once in a
                    // million; regenerate if it ever collides rather than asserting blindly.
                    ReceiverOutcome outcome = RunLanTransfer(source, destination, 51800, "000000",
                                                             out sendResult, out sendFailure);

                    Assert.NotNull(sendFailure, "the sender was rejected");
                    Assert.True(sendFailure is HandshakeException,
                                "rejected by the handshake, not by something else: "
                                + sendFailure.GetType().Name);

                    Assert.False(NativeFile.Exists(LongPath.Combine(LongPath.Combine(
                                     LongPath.Combine(destination, "testuser"), "Data"), "private.txt")),
                                 "no file was written");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("both sides derive the same keys, and a wrong code derives different ones", delegate
            {
                byte[] senderPublic = new byte[] { 1, 2, 3, 4 };
                byte[] receiverPublic = new byte[] { 5, 6, 7, 8 };

                byte[] a = NetworkProtocol.ComputeTranscript(1, senderPublic, receiverPublic,
                                                             "OLD-PC", "NEW-PC", "123456");
                byte[] b = NetworkProtocol.ComputeTranscript(1, senderPublic, receiverPublic,
                                                             "OLD-PC", "NEW-PC", "123456");
                byte[] wrongCode = NetworkProtocol.ComputeTranscript(1, senderPublic, receiverPublic,
                                                                     "OLD-PC", "NEW-PC", "123457");
                byte[] swappedKey = NetworkProtocol.ComputeTranscript(1, receiverPublic, senderPublic,
                                                                      "OLD-PC", "NEW-PC", "123456");

                Assert.True(Format.ConstantTimeEquals(a, b), "the transcript is deterministic");
                Assert.False(Format.ConstantTimeEquals(a, wrongCode), "one different digit changes it");
                Assert.False(Format.ConstantTimeEquals(a, swappedKey),
                             "substituting a public key changes it - this is what stops a man in the middle");
            });

            runner.Test("the discovery beacon carries no pairing code and no user data", delegate
            {
                string code = NetworkProtocol.NewPairingCode();
                byte[] beacon = DiscoveryBeacon.BuildBeacon("MY-NEW-PC", NetworkProtocol.TransferPort);
                string asText = Encoding.UTF8.GetString(beacon);

                Assert.True(asText.IndexOf("MY-NEW-PC", StringComparison.Ordinal) >= 0,
                            "the machine name is present, which is the point of the beacon");
                Assert.True(asText.IndexOf(code, StringComparison.Ordinal) < 0,
                            "the pairing code is NOT in the broadcast");
                Assert.True(asText.IndexOf(Environment.UserName, StringComparison.OrdinalIgnoreCase) < 0,
                            "the user name is not in the broadcast");
                Assert.True(beacon.Length < 300, "the beacon is small enough to be a single datagram");
            });

            runner.Test("pairing codes are six digits and vary", delegate
            {
                bool allSix = true;
                bool sawDifference = false;
                string first = NetworkProtocol.NewPairingCode();

                for (int i = 0; i < 50; i++)
                {
                    string code = NetworkProtocol.NewPairingCode();
                    if (code.Length != 6) { allSix = false; }
                    for (int c = 0; c < code.Length; c++)
                    {
                        if (code[c] < '0' || code[c] > '9') { allSix = false; }
                    }
                    if (code != first) { sawDifference = true; }
                }

                Assert.True(allSix, "every code is exactly six digits");
                Assert.True(sawDifference, "codes are not constant");
                Assert.Equal("123 456", NetworkProtocol.FormatCodeForDisplay("123456"), "display grouping");
                Assert.Equal("123456", NetworkProtocol.NormaliseCode(" 123-456 "), "typed codes are cleaned up");
            });
        }

        // ---- restore layout on the new PC ---------------------------------------

        /// <summary>
        /// Builds a selection with one Tier A known folder and one Tier C custom folder, so
        /// a restore exercises both the "goes home" and the "goes to the Desktop" paths.
        /// </summary>
        private static TransferSelection BuildMixedSelection(string knownSource, string customSource)
        {
            UserProfile profile = new UserProfile();
            profile.Sid = "S-1-5-21-0-0-0-1001";
            profile.AccountName = "testuser";
            profile.ProfilePath = knownSource;
            profile.ProfileExists = true;

            SelectionRoot documents = new SelectionRoot();
            documents.Tier = SelectionTier.KnownFolder;
            documents.Folder = KnownFolder.Documents;
            documents.IsKnownFolder = true;
            documents.Label = "Documents";
            documents.SourcePath = knownSource;
            documents.DestinationRelativeRoot = "Documents";
            documents.Selected = true;
            documents.Exists = true;

            SelectionRoot custom = new SelectionRoot();
            custom.Tier = SelectionTier.Custom;
            custom.Label = "Work";
            custom.SourcePath = customSource;
            custom.DestinationRelativeRoot = @"Moved folders\Work";
            custom.Selected = true;
            custom.Exists = true;

            UserSelection user = new UserSelection();
            user.Profile = profile;
            user.Selected = true;
            user.Roots.Add(documents);
            user.Roots.Add(custom);

            TransferSelection selection = new TransferSelection();
            selection.Users.Add(user);
            selection.Exclusions = ExclusionRules.CreateDefault();
            selection.IncludeHidden = true;
            return selection;
        }

        /// <summary>Points the known-folder resolver at a scratch profile for one test.</summary>
        private static void UseFakeProfile(string fakeProfile, out string desktop, out string documents)
        {
            desktop = LongPath.ToDisplay(LongPath.Combine(fakeProfile, "Desktop"));
            documents = LongPath.ToDisplay(LongPath.Combine(fakeProfile, "Documents"));

            int error;
            NativeFile.CreateDirectoryRecursive(desktop, out error);
            NativeFile.CreateDirectoryRecursive(documents, out error);

            string capturedDesktop = desktop;
            string capturedDocuments = documents;
            string capturedProfile = fakeProfile;

            LocalKnownFolders.ResolveOverride = delegate(KnownFolder f)
            {
                if (f == KnownFolder.Desktop) { return capturedDesktop; }
                if (f == KnownFolder.Documents) { return capturedDocuments; }
                return LongPath.ToDisplay(LongPath.Combine(capturedProfile, f.ToString()));
            };
        }

        private static void RegisterRestoreLayout(TestRunner runner)
        {
            runner.Group("Restore layout");

            runner.Test("only the five agreed known folders are put back in place", delegate
            {
                Assert.Equal(5, LocalKnownFolders.Restorable.Length, "exactly five folders");

                KnownFolder parsed;
                Assert.True(LocalKnownFolders.TryParseDestinationName("Documents", out parsed)
                            && parsed == KnownFolder.Documents, "Documents is restorable");
                Assert.True(LocalKnownFolders.TryParseDestinationName("Downloads", out parsed)
                            && parsed == KnownFolder.Downloads, "Downloads is restorable");
                Assert.True(LocalKnownFolders.TryParseDestinationName("Music", out parsed)
                            && parsed == KnownFolder.Music, "Music is restorable");
                Assert.True(LocalKnownFolders.TryParseDestinationName("Videos", out parsed)
                            && parsed == KnownFolder.Videos, "Videos is restorable");
                Assert.True(LocalKnownFolders.TryParseDestinationName("Desktop", out parsed)
                            && parsed == KnownFolder.Desktop, "Desktop is restorable");

                // Real known folders that are deliberately NOT put back in place.
                Assert.False(LocalKnownFolders.TryParseDestinationName("Pictures", out parsed),
                             "Pictures is not in the agreed list");
                Assert.False(LocalKnownFolders.TryParseDestinationName("Favorites", out parsed),
                             "Favorites is not in the agreed list");
                Assert.False(LocalKnownFolders.TryParseDestinationName(@"Moved folders\Work", out parsed),
                             "a custom folder is never treated as a known one");
            });

            runner.Test("the folders on this PC actually resolve", delegate
            {
                for (int i = 0; i < LocalKnownFolders.Restorable.Length; i++)
                {
                    KnownFolder folder = LocalKnownFolders.Restorable[i];
                    string path = LocalKnownFolders.Resolve(folder);
                    Assert.NotNull(path, folder + " resolves to a path");
                    Assert.True(path.Length > 3, folder + " looks like a real path: " + path);
                }
            });

            runner.Test("known folders go home and everything else lands on the Desktop", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string knownSource = LongPath.Combine(scratch, "src-docs");
                    string customSource = LongPath.Combine(scratch, "src-work");
                    string destination = LongPath.Combine(scratch, "chosen");

                    TestFs.WriteFile(LongPath.Combine(knownSource, "letter.txt"), "a document");
                    TestFs.WriteFile(LongPath.Combine(customSource, "notes.txt"), "some work");

                    string fakeDesktop;
                    string fakeDocuments;
                    UseFakeProfile(LongPath.Combine(scratch, "newpc"), out fakeDesktop, out fakeDocuments);

                    try
                    {
                        CopyOptions options = CopyOptions.Defaults();
                        string manifestPath = LongPath.ToDisplay(
                            LongPath.Combine(scratch, "layout.mtnpc-manifest"));

                        int error;
                        NativeFile.CreateDirectoryRecursive(destination, out error);

                        TransferSelection selection = BuildMixedSelection(knownSource, customSource);
                        ScanEngine scanner = new ScanEngine(selection, options);
                        TransferManifest manifest = scanner.Scan(manifestPath, new SilentScanObserver(),
                                                                 CancellationToken.None);
                        manifest.SourceMachine = "OLDBOX";

                        using (LocalFolderSink sink = new LocalFolderSink(
                                   destination, options, null, DestinationLayout.MatchingFolders))
                        using (PauseGate gate = new PauseGate())
                        {
                            TransferEngine engine = new TransferEngine(options, sink,
                                                                       new SilentTransferObserver());
                            TransferResult result = engine.Run(manifest, manifestPath,
                                                               CancellationToken.None, gate);
                            Assert.Equal(0, result.FilesFailed, "nothing failed");
                            Assert.Equal(2, result.FilesCopied, "both files were written");
                        }

                        // Documents went home, with no account folder in between.
                        Assert.Equal("a document",
                                     TestFs.ReadAllText(LongPath.Combine(fakeDocuments, "letter.txt")),
                                     "the document landed in this PC own Documents");

                        // The custom folder went to one folder on the Desktop, named after
                        // the machine it came from.
                        string stray = LongPath.Combine(fakeDesktop, "From OLDBOX");
                        Assert.Equal("some work",
                                     TestFs.ReadAllText(LongPath.Combine(
                                         LongPath.Combine(stray, @"Moved folders\Work"), "notes.txt")),
                                     "the custom folder landed on the Desktop");

                        // And nothing was dumped into the folder the operator picked.
                        Assert.False(NativeFile.Exists(LongPath.Combine(
                                         LongPath.Combine(destination, "testuser"), @"Documents\letter.txt")),
                                     "the single-folder layout was not used as well");
                    }
                    finally
                    {
                        LocalKnownFolders.ResolveOverride = null;
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("the single-folder layout is unchanged and stays the default", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string source = LongPath.Combine(scratch, "src");
                    string destination = LongPath.Combine(scratch, "dst");
                    TestFs.WriteFile(LongPath.Combine(source, "plain.txt"), "hello");

                    TransferManifest manifest;
                    TransferResult result = RunTransfer(source, destination,
                                                        CopyOptions.Defaults(), out manifest);

                    Assert.Equal(0, result.FilesFailed, "nothing failed");
                    Assert.Equal("hello",
                                 TestFs.ReadAllText(LongPath.Combine(LongPath.Combine(
                                     LongPath.Combine(destination, "testuser"), "Data"), "plain.txt")),
                                 "still lands under destination, account, root");
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });

            runner.Test("a second account never merges into the folders of this PC", delegate
            {
                string scratch = TestFs.CreateScratch();
                try
                {
                    string fakeDesktop;
                    string fakeDocuments;
                    UseFakeProfile(LongPath.Combine(scratch, "newpc"), out fakeDesktop, out fakeDocuments);

                    try
                    {
                        // Two accounts, both with a Documents root. Merging them would be a
                        // data-loss bug, so only the first may be mapped onto this PC.
                        TransferManifest manifest = new TransferManifest();
                        manifest.ManifestId = "two-users";
                        manifest.CreatedUtc = DateTime.UtcNow;
                        manifest.SourceMachine = "OLDBOX";
                        manifest.ToolVersion = "test";

                        for (int u = 0; u < 2; u++)
                        {
                            ManifestUser user = new ManifestUser();
                            user.UserIndex = u;
                            user.Sid = "S-1-5-21-0-0-0-100" + u.ToString();
                            user.AccountName = u == 0 ? "alice" : "bob";
                            user.ProfilePath = @"C:\Users\" + user.AccountName;

                            ManifestRoot root = new ManifestRoot();
                            root.UserIndex = u;
                            root.RootIndex = 0;
                            root.Tier = SelectionTier.KnownFolder;
                            root.SourcePath = user.ProfilePath + @"\Documents";
                            root.DestinationRelativeRoot = "Documents";
                            root.Label = "Documents";
                            user.Roots.Add(root);

                            manifest.Users.Add(user);
                        }

                        string destination = LongPath.Combine(scratch, "chosen");
                        int error;
                        NativeFile.CreateDirectoryRecursive(destination, out error);

                        using (LocalFolderSink sink = new LocalFolderSink(
                                   destination, CopyOptions.Defaults(), null,
                                   DestinationLayout.MatchingFolders))
                        {
                            sink.BeginSession(manifest);
                            sink.EndSession(true);
                        }

                        // The second account gets its own folder under the Desktop drop.
                        string bobRoot = LongPath.Combine(
                            LongPath.Combine(LongPath.Combine(fakeDesktop, "From OLDBOX"), "bob"),
                            "Documents");
                        Assert.True(NativeFile.DirectoryExists(bobRoot),
                                    "the second account went to its own folder: "
                                    + LongPath.ToDisplay(bobRoot));
                    }
                    finally
                    {
                        LocalKnownFolders.ResolveOverride = null;
                    }
                }
                finally
                {
                    TestFs.DeleteTree(scratch);
                }
            });
        }

        private static void RegisterProfiles(TestRunner runner)
        {
            runner.Group("Profile discovery");

            runner.Test("finds at least the current user and leaves the hives unloaded", delegate
            {
                ProfileEnumerationResult result = ProfileEnumerator.Enumerate();

                Assert.True(result.Profiles.Count > 0, "at least one profile was found");

                bool foundCurrent = false;
                for (int i = 0; i < result.Profiles.Count; i++)
                {
                    UserProfile profile = result.Profiles[i];
                    Assert.True(!string.IsNullOrEmpty(profile.Sid), "every profile has a SID");
                    Assert.True(!string.IsNullOrEmpty(profile.ProfilePath), "every profile has a path");
                    Assert.True(NativeFile.DirectoryExists(profile.ProfilePath),
                                "the profile folder exists: " + profile.ProfilePath);
                    if (profile.IsCurrentUser)
                    {
                        foundCurrent = true;
                    }
                }

                Assert.True(foundCurrent, "the signed-in user is flagged");

                // Nothing this tool mounted may still be mounted. A leaked hive locks the
                // profile and can stop that user logging in.
                using (Microsoft.Win32.RegistryKey users =
                           Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.Users,
                                                                   Microsoft.Win32.RegistryView.Default))
                {
                    string[] names = users.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        Assert.False(names[i].StartsWith("MTNPC_", StringComparison.Ordinal),
                                     "no hive was left mounted: HKU\\" + names[i]);
                    }
                }
            });

            runner.Test("the current user's known folders resolve to real places", delegate
            {
                ProfileEnumerationResult result = ProfileEnumerator.Enumerate();

                UserProfile current = null;
                for (int i = 0; i < result.Profiles.Count; i++)
                {
                    if (result.Profiles[i].IsCurrentUser)
                    {
                        current = result.Profiles[i];
                    }
                }

                if (current == null)
                {
                    Assert.Skip("the current user has no profile entry");
                }

                Assert.True(current.KnownFolders.Count > 0, "some known folders were resolved");

                foreach (KeyValuePair<KnownFolder, string> pair in current.KnownFolders)
                {
                    Assert.True(NativeFile.DirectoryExists(pair.Value),
                                pair.Key + " resolves to a real folder (" + pair.Value + ")");
                    Assert.False(pair.Value.IndexOf('%') >= 0,
                                 pair.Key + " has no unexpanded environment variable");
                }

                string desktop;
                if (current.KnownFolders.TryGetValue(KnownFolder.Desktop, out desktop))
                {
                    string expected = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    Assert.Equal(LongPath.TrimTrailingSeparators(expected).ToUpperInvariant(),
                                 LongPath.TrimTrailingSeparators(desktop).ToUpperInvariant(),
                                 "Desktop matches what Windows itself reports");
                }
            });
        }
    }
}
