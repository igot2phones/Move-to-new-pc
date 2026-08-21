using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Selection;

namespace MoveToNewPC.Core.Transfer
{
    public interface IScanObserver
    {
        void OnStatus(string message);
        void OnProgress(long files, long bytes, long skipped, string currentPath);
        void OnSkipped(SkippedItem item);
    }

    /// <summary>
    /// Pass one: walk everything the operator selected and write a manifest.
    ///
    /// Scanning separately from copying costs a second enumeration, and buys three things
    /// the spec requires: a free-space check against a real total before a byte moves, an
    /// ETA based on bytes rather than file count, and a dry run that produces the complete
    /// report without transferring anything.
    /// </summary>
    public sealed class ScanEngine
    {
        private readonly TransferSelection _selection;
        private readonly CopyOptions _options;

        public ScanEngine(TransferSelection selection, CopyOptions options)
        {
            _selection = selection;
            _options = options ?? CopyOptions.Defaults();
        }

        public TransferManifest Scan(string manifestPath, IScanObserver observer, CancellationToken cancel)
        {
            TransferManifest manifest = new TransferManifest();
            manifest.ManifestId = Guid.NewGuid().ToString("N");
            manifest.CreatedUtc = DateTime.UtcNow;
            manifest.SourceMachine = SafeMachineName();
            manifest.ToolVersion = ToolVersion;

            BuildStructure(manifest);

            if (manifest.Users.Count == 0)
            {
                Log.Warn("Scan produced no users; nothing was selected.");
                return manifest;
            }

            WalkOptions walkOptions = new WalkOptions();
            walkOptions.Exclusions = _selection.Exclusions;
            walkOptions.Filter = FileFilter.CreateOrNull(_selection.Filters);
            walkOptions.IncludeHidden = _selection.IncludeHidden;
            walkOptions.IncludeSystem = _selection.IncludeSystem;
            walkOptions.FollowReparsePoints = false;
            walkOptions.HydrateCloudFiles = _options.HydrateCloudFiles;
            walkOptions.IncludeEncryptedFiles = _options.IncludeEncryptedFiles;

            using (ManifestWriter writer = new ManifestWriter(manifestPath, manifest))
            {
                ScanObserver sink = new ScanObserver(writer, manifest, observer, cancel);

                for (int u = 0; u < manifest.Users.Count && !cancel.IsCancellationRequested; u++)
                {
                    ManifestUser user = manifest.Users[u];

                    for (int r = 0; r < user.Roots.Count && !cancel.IsCancellationRequested; r++)
                    {
                        ManifestRoot root = user.Roots[r];

                        if (observer != null)
                        {
                            observer.OnStatus("Scanning " + user.AccountName + " - " + root.Label);
                        }
                        Log.Info("Scanning root " + root.SourcePath + " -> " + root.DestinationRelativeRoot);

                        sink.BeginRoot(user.UserIndex, root.RootIndex);
                        DirectoryWalker.Walk(root.SourcePath, walkOptions, sink, cancel);
                    }
                }

                manifest.Totals.FileCount = sink.Files;
                manifest.Totals.ByteCount = sink.Bytes;
                manifest.Totals.DirectoryCount = sink.Directories;
                manifest.Totals.SkippedCount = sink.Skipped;
                manifest.Totals.SkippedBytes = sink.SkippedBytes;
                writer.WriteTotals(manifest.Totals);
            }

            Log.Info("Scan complete: " + manifest.Totals.FileCount + " files, "
                     + manifest.Totals.ByteCount + " bytes, "
                     + manifest.Totals.DirectoryCount + " directories, "
                     + manifest.Totals.SkippedCount + " skipped.");

            return manifest;
        }

        private void BuildStructure(TransferManifest manifest)
        {
            int userIndex = 0;

            for (int u = 0; u < _selection.Users.Count; u++)
            {
                UserSelection user = _selection.Users[u];
                if (!user.Selected)
                {
                    continue;
                }

                List<SelectionRoot> chosen = new List<SelectionRoot>();
                for (int r = 0; r < user.Roots.Count; r++)
                {
                    SelectionRoot root = user.Roots[r];
                    if (root.Selected && root.Exists && !string.IsNullOrEmpty(root.SourcePath))
                    {
                        chosen.Add(root);
                    }
                }

                if (chosen.Count == 0)
                {
                    Log.Info("User " + user.Profile.DisplayName + " selected but has no selected folders; skipping.");
                    continue;
                }

                ManifestUser manifestUser = new ManifestUser();
                manifestUser.UserIndex = userIndex++;
                manifestUser.Sid = user.Profile.Sid;
                manifestUser.AccountName = user.Profile.AccountName;
                manifestUser.ProfilePath = LongPath.ToDisplay(user.Profile.ProfilePath);
                manifestUser.DestinationHint = user.Profile.AccountName;

                for (int r = 0; r < chosen.Count; r++)
                {
                    SelectionRoot root = chosen[r];
                    ManifestRoot manifestRoot = new ManifestRoot();
                    manifestRoot.UserIndex = manifestUser.UserIndex;
                    manifestRoot.RootIndex = r;
                    manifestRoot.Tier = root.Tier;
                    manifestRoot.SourcePath = LongPath.ToDisplay(root.SourcePath);
                    manifestRoot.DestinationRelativeRoot = root.DestinationRelativeRoot;
                    manifestRoot.Label = root.Label;
                    manifestUser.Roots.Add(manifestRoot);
                }

                manifest.Users.Add(manifestUser);
            }
        }

        internal static string ToolVersion
        {
            get
            {
                try
                {
                    return typeof(ScanEngine).Assembly.GetName().Version.ToString();
                }
                catch (Exception)
                {
                    return "0.0.0.0";
                }
            }
        }

        internal static string SafeMachineName()
        {
            try
            {
                return Environment.MachineName;
            }
            catch (Exception)
            {
                return "(unknown)";
            }
        }

        /// <summary>Turns walker callbacks into manifest records.</summary>
        private sealed class ScanObserver : IWalkObserver
        {
            private readonly ManifestWriter _writer;
            private readonly TransferManifest _manifest;
            private readonly IScanObserver _observer;
            private readonly CancellationToken _cancel;

            private int _userIndex;
            private int _rootIndex;
            private DateTime _lastNotify = DateTime.MinValue;
            private string _currentPath = string.Empty;

            public long Files;
            public long Bytes;
            public long Directories;
            public long Skipped;
            public long SkippedBytes;

            /// <summary>
            /// Scan-time skips are also kept in memory for the report, but capped: a machine
            /// with a million unreadable files must not make us run out of memory building a
            /// list nobody can read. The manifest on disk keeps every one of them.
            /// </summary>
            private const int MaxInMemorySkips = 5000;

            public ScanObserver(ManifestWriter writer, TransferManifest manifest,
                                IScanObserver observer, CancellationToken cancel)
            {
                _writer = writer;
                _manifest = manifest;
                _observer = observer;
                _cancel = cancel;
            }

            public void BeginRoot(int userIndex, int rootIndex)
            {
                _userIndex = userIndex;
                _rootIndex = rootIndex;
            }

            public void OnDirectory(FsEntry entry, string relativePath)
            {
                Directories++;

                ManifestDirectory directory = new ManifestDirectory();
                directory.UserIndex = _userIndex;
                directory.RootIndex = _rootIndex;
                directory.RelativePath = relativePath;
                directory.Attributes = entry.PortableAttributes;
                directory.CreationTimeUtc = entry.CreationTimeUtc;
                directory.LastAccessTimeUtc = entry.LastAccessTimeUtc;
                directory.LastWriteTimeUtc = entry.LastWriteTimeUtc;
                _writer.WriteDirectory(directory);
            }

            public void OnFile(FsEntry entry, string relativePath)
            {
                Files++;
                Bytes += entry.Length;
                _currentPath = relativePath;

                ManifestEntry manifestEntry = new ManifestEntry();
                manifestEntry.UserIndex = _userIndex;
                manifestEntry.RootIndex = _rootIndex;
                manifestEntry.RelativePath = relativePath;
                manifestEntry.Length = entry.Length;
                manifestEntry.Attributes = entry.PortableAttributes;
                manifestEntry.CreationTimeUtc = entry.CreationTimeUtc;
                manifestEntry.LastAccessTimeUtc = entry.LastAccessTimeUtc;
                manifestEntry.LastWriteTimeUtc = entry.LastWriteTimeUtc;
                manifestEntry.Sha256 = null;
                _writer.WriteFile(manifestEntry);
            }

            public void OnSkip(string fullPath, string relativePath, bool isDirectory, SkipReason reason,
                               string detail, long length)
            {
                Skipped++;
                SkippedBytes += length;

                _writer.WriteSkip(_userIndex, _rootIndex, relativePath ?? fullPath, reason, length, detail);

                SkippedItem item = new SkippedItem(fullPath, isDirectory, reason, detail, length);
                if (_manifest.ScanSkips.Count < MaxInMemorySkips)
                {
                    _manifest.ScanSkips.Add(item);
                }

                if (_observer != null)
                {
                    _observer.OnSkipped(item);
                }
            }

            public void OnProgress(long entriesSeen, long filesSeen, long bytesSeen)
            {
                if (_observer == null || _cancel.IsCancellationRequested)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if ((now - _lastNotify).TotalMilliseconds < 150)
                {
                    return;
                }
                _lastNotify = now;

                _observer.OnProgress(Files, Bytes, Skipped, _currentPath);
            }
        }
    }
}
