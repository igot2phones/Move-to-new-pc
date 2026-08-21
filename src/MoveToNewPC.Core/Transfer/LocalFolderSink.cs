using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Native;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Transfer
{
    /// <summary>Thrown when what landed on disk does not match what was sent.</summary>
    public sealed class TransferVerificationException : Exception
    {
        public TransferVerificationException(string message) : base(message) { }
    }

    /// <summary>
    /// Writes into a folder on this machine. Used directly for local/offline copies, and
    /// used unchanged by the receiver once the network transport exists - the engine above
    /// it never learns the difference.
    ///
    /// Every incoming relative path is re-validated here, not trusted from the manifest.
    /// </summary>
    public sealed class LocalFolderSink : ITransferSink
    {
        public const string PartialExtension = ".mtnpc-part";

        private sealed class PendingDirectoryTimes
        {
            public string Path;
            public long Creation;
            public long LastAccess;
            public long LastWrite;
        }

        private readonly string _destinationRoot;
        private readonly CopyOptions _options;
        private readonly CompletionJournal _journal;
        private readonly Dictionary<int, string> _userRoots = new Dictionary<int, string>();
        /// <summary>Absolute base folder for each (user, root) pair, keyed "userIndex/rootIndex".</summary>
        private readonly Dictionary<string, string> _rootBases = new Dictionary<string, string>();
        private readonly DestinationLayout _layout;
        private readonly List<PendingDirectoryTimes> _directoryTimes = new List<PendingDirectoryTimes>();

        // Directory timestamps are applied at the end, deepest first: writing files into a
        // folder updates its own mtime, so stamping on creation would be pointless.
        private const int MaxTrackedDirectories = 200000;
        private bool _directoryTrackingTruncated;

        private FileStream _current;
        private HashAlgorithm _hash;
        private string _currentPartPath;
        private string _currentFinalPath;
        private ManifestEntry _currentEntry;
        private long _currentWritten;
        private bool _disposed;

        public LocalFolderSink(string destinationRoot, CopyOptions options, CompletionJournal journal)
            : this(destinationRoot, options, journal, DestinationLayout.SingleFolder)
        {
        }

        public LocalFolderSink(string destinationRoot, CopyOptions options, CompletionJournal journal,
                               DestinationLayout layout)
        {
            if (string.IsNullOrEmpty(destinationRoot))
            {
                throw new ArgumentNullException("destinationRoot");
            }

            _destinationRoot = LongPath.TrimTrailingSeparators(destinationRoot);
            _options = options ?? CopyOptions.Defaults();
            _journal = journal;
            _layout = layout;
        }

        public string DestinationRoot
        {
            get { return _destinationRoot; }
        }

        public void BeginSession(TransferManifest manifest)
        {
            int error;
            if (!NativeFile.CreateDirectoryRecursive(_destinationRoot, out error))
            {
                throw new IOException("Could not create destination folder "
                                      + LongPath.ToDisplay(_destinationRoot) + ": "
                                      + NativeFile.DescribeError(error));
            }

            _userRoots.Clear();
            _rootBases.Clear();

            // In MatchingFolders mode anything that is not one of this PC's own known
            // folders lands here, so a migration never scatters unrecognised folders across
            // the Desktop itself.
            string strayRoot = null;
            if (_layout == DestinationLayout.MatchingFolders)
            {
                strayRoot = BuildStrayRoot(manifest);
            }

            for (int u = 0; u < manifest.Users.Count; u++)
            {
                ManifestUser user = manifest.Users[u];

                string folderName = PathValidation.SanitiseSegment(
                    string.IsNullOrEmpty(user.DestinationHint) ? user.AccountName : user.DestinationHint,
                    "User " + user.UserIndex.ToString(CultureInfo.InvariantCulture));

                // Only the first account can be mapped onto this PC's own folders: merging
                // two people's Documents into one place would be a data-loss bug, not a
                // convenience. Everyone else goes to the stray root under their own name.
                bool mapOntoThisPc = _layout == DestinationLayout.MatchingFolders && u == 0;

                string userRoot;
                if (_layout == DestinationLayout.MatchingFolders)
                {
                    userRoot = mapOntoThisPc
                        ? strayRoot
                        : LongPath.Combine(strayRoot, folderName);
                }
                else
                {
                    userRoot = LongPath.Combine(_destinationRoot, folderName);
                }

                _userRoots[user.UserIndex] = userRoot;

                if (!NativeFile.CreateDirectoryRecursive(userRoot, out error))
                {
                    throw new IOException("Could not create folder " + LongPath.ToDisplay(userRoot)
                                          + ": " + NativeFile.DescribeError(error));
                }

                // Create each root up front so an empty selected folder still appears on the
                // new PC rather than silently vanishing.
                for (int r = 0; r < user.Roots.Count; r++)
                {
                    ManifestRoot manifestRoot = user.Roots[r];
                    string rootPath = null;

                    if (mapOntoThisPc)
                    {
                        rootPath = ResolveOntoThisPc(manifestRoot);
                    }

                    if (rootPath == null)
                    {
                        string reason;
                        rootPath = PathValidation.ResolveUnderRoot(userRoot,
                                                                   manifestRoot.DestinationRelativeRoot,
                                                                   out reason);
                        if (rootPath == null)
                        {
                            throw new IOException("Refusing destination folder \""
                                                  + manifestRoot.DestinationRelativeRoot + "\": " + reason);
                        }
                    }

                    _rootBases[BaseKey(user.UserIndex, manifestRoot.RootIndex)] = rootPath;

                    if (!NativeFile.CreateDirectoryRecursive(rootPath, out error))
                    {
                        Log.Warn("Could not pre-create " + LongPath.ToDisplay(rootPath) + ": "
                                 + NativeFile.DescribeError(error));
                    }
                }
            }

            if (_layout == DestinationLayout.MatchingFolders)
            {
                Log.Info("Restoring into this PC's own folders; anything else goes to "
                         + LongPath.ToDisplay(strayRoot));
            }
            else
            {
                Log.Info("Destination: " + LongPath.ToDisplay(_destinationRoot)
                         + " (" + manifest.Users.Count + " user folder(s))");
            }
        }

        private static string BaseKey(int userIndex, int rootIndex)
        {
            return userIndex.ToString(CultureInfo.InvariantCulture) + "/"
                   + rootIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The folder on this PC's Desktop that receives everything which is not a known
        /// folder. Named after the machine it came from so two migrations do not merge.
        /// </summary>
        private string BuildStrayRoot(TransferManifest manifest)
        {
            string desktop = LocalKnownFolders.Resolve(KnownFolder.Desktop);
            if (string.IsNullOrEmpty(desktop))
            {
                // No Desktop is close to impossible, but falling back to the chosen folder
                // is better than refusing the whole transfer.
                Log.Warn("Could not find this PC's Desktop; using the chosen folder instead.");
                return _destinationRoot;
            }

            string source = PathValidation.SanitiseSegment(manifest.SourceMachine, "old PC");
            return LongPath.Combine(desktop, "From " + source);
        }

        /// <summary>
        /// Returns this PC's real folder for a Tier A root whose name we recognise, or null
        /// when the caller should fall back to the stray root. Only the short list in
        /// LocalKnownFolders is mapped: those are the folders whose meaning is the same on
        /// every Windows PC.
        /// </summary>
        private string ResolveOntoThisPc(ManifestRoot root)
        {
            if (root.Tier != SelectionTier.KnownFolder)
            {
                return null;
            }

            KnownFolder folder;
            if (!LocalKnownFolders.TryParseDestinationName(root.DestinationRelativeRoot, out folder))
            {
                return null;
            }

            string path = LocalKnownFolders.Resolve(folder);
            if (string.IsNullOrEmpty(path))
            {
                Log.Warn("Could not resolve this PC's " + folder + "; it will go to the Desktop folder instead.");
                return null;
            }

            Log.Info(root.DestinationRelativeRoot + " -> " + LongPath.ToDisplay(path));
            return path;
        }

        public void EnsureDirectory(ManifestUser user, ManifestRoot root, ManifestDirectory directory)
        {
            string full = Resolve(user, root, directory.RelativePath);
            if (full == null)
            {
                return;
            }

            int error;
            if (!NativeFile.CreateDirectoryRecursive(full, out error))
            {
                Log.Warn("Could not create directory " + LongPath.ToDisplay(full) + ": "
                         + NativeFile.DescribeError(error));
                return;
            }

            if (_options.PreserveTimestamps && directory.LastWriteTimeUtc > 0)
            {
                if (_directoryTimes.Count < MaxTrackedDirectories)
                {
                    PendingDirectoryTimes pending = new PendingDirectoryTimes();
                    pending.Path = full;
                    pending.Creation = directory.CreationTimeUtc;
                    pending.LastAccess = directory.LastAccessTimeUtc;
                    pending.LastWrite = directory.LastWriteTimeUtc;
                    _directoryTimes.Add(pending);
                }
                else if (!_directoryTrackingTruncated)
                {
                    _directoryTrackingTruncated = true;
                    Log.Warn("More than " + MaxTrackedDirectories
                             + " directories; folder timestamps beyond that will not be restored.");
                }
            }
        }

        public SinkFileDecision BeginFile(ManifestUser user, ManifestRoot root, ManifestEntry entry,
                                          out string destinationDisplayPath)
        {
            destinationDisplayPath = null;

            if (_current != null)
            {
                // Programming error, not a data error - fail loudly.
                throw new InvalidOperationException("BeginFile called while a file is still open.");
            }

            if (_journal != null && _journal.IsComplete(entry.UserIndex, entry.RootIndex, entry.RelativePath))
            {
                return SinkFileDecision.AlreadyComplete;
            }

            string full = Resolve(user, root, entry.RelativePath);
            if (full == null)
            {
                throw new TransferVerificationException("Rejected unsafe path: " + entry.RelativePath);
            }

            bool overwrite = false;

            if (NativeFile.Exists(full))
            {
                switch (_options.Collision)
                {
                    case CollisionPolicy.Skip:
                        destinationDisplayPath = LongPath.ToDisplay(full);
                        return SinkFileDecision.Skip;

                    case CollisionPolicy.Overwrite:
                        overwrite = true;
                        break;

                    case CollisionPolicy.KeepBoth:
                        full = MakeUniquePath(full);
                        break;
                }
            }

            string parent = LongPath.GetDirectoryName(full);
            int error;
            if (!NativeFile.CreateDirectoryRecursive(parent, out error))
            {
                throw new IOException("Could not create " + LongPath.ToDisplay(parent) + ": "
                                      + NativeFile.DescribeError(error));
            }

            string partPath = full + PartialExtension;

            // Debris from an interrupted run: the journal is the only thing that says a file
            // is complete, so a stray .mtnpc-part is always garbage.
            if (NativeFile.Exists(partPath))
            {
                int deleteError;
                if (!NativeFile.Delete(partPath, out deleteError))
                {
                    throw new IOException("Could not remove stale partial file "
                                          + LongPath.ToDisplay(partPath) + ": "
                                          + NativeFile.DescribeError(deleteError));
                }
            }

            FileStream stream = NativeFile.CreateWrite(partPath, true, _options.ChunkSize, out error);
            if (stream == null)
            {
                throw new IOException("Could not create " + LongPath.ToDisplay(partPath) + ": "
                                      + NativeFile.DescribeError(error));
            }

            _current = stream;
            _currentPartPath = partPath;
            _currentFinalPath = full;
            _currentEntry = entry;
            _currentWritten = 0;
            _hash = _options.VerifyHash ? HashFactory.CreateSha256() : null;

            // Remember whether we are replacing something, so the rename can say so.
            _replaceExisting = overwrite;

            destinationDisplayPath = LongPath.ToDisplay(full);
            return SinkFileDecision.Write;
        }

        private bool _replaceExisting;

        public void WriteChunk(byte[] buffer, int offset, int count)
        {
            if (_current == null)
            {
                throw new InvalidOperationException("WriteChunk called with no file open.");
            }
            if (count <= 0)
            {
                return;
            }

            // The declared length from the manifest is a hard limit, not a hint: a sender
            // that keeps sending past it would otherwise fill the disk.
            if (_currentEntry != null && _currentWritten + count > _currentEntry.Length)
            {
                throw new TransferVerificationException(
                    "File is longer than declared (" + _currentEntry.Length + " bytes): "
                    + _currentEntry.RelativePath);
            }

            _current.Write(buffer, offset, count);
            _currentWritten += count;
            HashFactory.Update(_hash, buffer, offset, count);
        }

        public void EndFile(byte[] sha256)
        {
            if (_current == null)
            {
                throw new InvalidOperationException("EndFile called with no file open.");
            }

            byte[] written = null;
            try
            {
                if (_currentEntry != null && _currentWritten != _currentEntry.Length)
                {
                    throw new TransferVerificationException(
                        "Expected " + _currentEntry.Length + " bytes but received " + _currentWritten
                        + " for " + _currentEntry.RelativePath);
                }

                written = HashFactory.Finish(_hash);

                if (_options.VerifyHash && sha256 != null && written != null
                    && !Format.ConstantTimeEquals(sha256, written))
                {
                    throw new TransferVerificationException(
                        "Checksum mismatch for " + _currentEntry.RelativePath);
                }

                // Timestamps go on before the rename: they survive it, and the file is still
                // ours exclusively at this point.
                if (_options.PreserveTimestamps && _currentEntry != null)
                {
                    NativeFile.SetTimes(_current.SafeFileHandle,
                                        _currentEntry.CreationTimeUtc,
                                        _currentEntry.LastAccessTimeUtc,
                                        _currentEntry.LastWriteTimeUtc);
                }

                _current.Flush();
                _current.Dispose();
                _current = null;

                int error;
                if (!NativeFile.Move(_currentPartPath, _currentFinalPath, _replaceExisting, out error))
                {
                    if (error == NativeMethods.ERROR_ALREADY_EXISTS || error == NativeMethods.ERROR_FILE_EXISTS)
                    {
                        // Someone created it between our check and now. Never silently
                        // overwrite: keep the partial file out of the way and report.
                        NativeFile.Delete(_currentPartPath, out error);
                        throw new IOException("Destination appeared while copying: "
                                              + LongPath.ToDisplay(_currentFinalPath));
                    }
                    throw new IOException("Could not finalise " + LongPath.ToDisplay(_currentFinalPath)
                                          + ": " + NativeFile.DescribeError(error));
                }

                // Attributes last: setting READONLY before the rename would block it.
                if (_options.PreserveAttributes && _currentEntry != null && _currentEntry.Attributes != 0)
                {
                    int attributeError;
                    NativeFile.SetAttributes(_currentFinalPath, _currentEntry.Attributes, out attributeError);
                }

                if (_journal != null && _currentEntry != null)
                {
                    _journal.MarkComplete(_currentEntry.UserIndex, _currentEntry.RootIndex,
                                          _currentEntry.RelativePath, _currentWritten,
                                          written == null ? null : Format.ToHex(written));
                }
            }
            finally
            {
                CleanUpCurrent(false);
            }
        }

        public void AbortFile(SkipReason reason, string detail)
        {
            if (_current == null)
            {
                return;
            }

            if (_journal != null && _currentEntry != null)
            {
                _journal.MarkFailed(_currentEntry.UserIndex, _currentEntry.RootIndex,
                                    _currentEntry.RelativePath, reason, detail);
            }

            CleanUpCurrent(true);
        }

        private void CleanUpCurrent(bool deletePartial)
        {
            if (_current != null)
            {
                try
                {
                    _current.Dispose();
                }
                catch (IOException)
                {
                }
                _current = null;
            }

            if (deletePartial && !string.IsNullOrEmpty(_currentPartPath))
            {
                int error;
                if (!NativeFile.Delete(_currentPartPath, out error)
                    && error != NativeMethods.ERROR_FILE_NOT_FOUND)
                {
                    Log.Warn("Could not remove partial file " + LongPath.ToDisplay(_currentPartPath)
                             + ": " + NativeFile.DescribeError(error));
                }
            }

            if (_hash != null)
            {
                try
                {
                    _hash.Clear();
                }
                catch (Exception)
                {
                }
                _hash = null;
            }

            _currentPartPath = null;
            _currentFinalPath = null;
            _currentEntry = null;
            _currentWritten = 0;
            _replaceExisting = false;
        }

        public void EndSession(bool completedNormally)
        {
            AbortFile(SkipReason.Cancelled, "Session ended with a file open");
            ApplyDirectoryTimes();
        }

        private void ApplyDirectoryTimes()
        {
            if (_directoryTimes.Count == 0)
            {
                return;
            }

            // Deepest first, so stamping a parent is not undone by still writing children.
            _directoryTimes.Sort(delegate(PendingDirectoryTimes a, PendingDirectoryTimes b)
            {
                return b.Path.Length.CompareTo(a.Path.Length);
            });

            int applied = 0;
            for (int i = 0; i < _directoryTimes.Count; i++)
            {
                PendingDirectoryTimes pending = _directoryTimes[i];
                int error;
                if (NativeFile.SetTimes(pending.Path, pending.Creation, pending.LastAccess,
                                        pending.LastWrite, out error))
                {
                    applied++;
                }
            }

            Log.Info("Restored timestamps on " + applied + " of " + _directoryTimes.Count + " folders.");
            _directoryTimes.Clear();
        }

        public long GetAvailableBytes()
        {
            long free;
            long total;
            if (NativeFile.TryGetFreeSpace(_destinationRoot, out free, out total))
            {
                return free;
            }
            return -1;
        }

        private string Resolve(ManifestUser user, ManifestRoot root, string relativePath)
        {
            // The base for this root was decided once in BeginSession - it may be a folder
            // under the chosen destination, or one of this PC's own known folders. Either
            // way, containment is re-checked here against that base.
            string rootBase;
            if (!_rootBases.TryGetValue(BaseKey(user.UserIndex, root.RootIndex), out rootBase))
            {
                Log.Warn("No destination base for user " + user.UserIndex + " root " + root.RootIndex);
                return null;
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                return rootBase;
            }

            string reason;
            string full = PathValidation.ResolveUnderRoot(rootBase, relativePath, out reason);
            if (full == null)
            {
                Log.Warn("Rejected path \"" + relativePath + "\": " + reason);
            }
            return full;
        }

        /// <summary>Produces "name (1).ext", "name (2).ext", ... for the KeepBoth policy.</summary>
        private static string MakeUniquePath(string path)
        {
            string directory = LongPath.GetDirectoryName(path);
            string name = LongPath.GetFileName(path);
            string stem = LongPath.GetFileNameWithoutExtension(name);
            string extension = LongPath.GetExtension(name);

            for (int i = 1; i < 10000; i++)
            {
                string candidate = LongPath.Combine(directory,
                    stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                if (!NativeFile.Exists(candidate))
                {
                    return candidate;
                }
            }

            return LongPath.Combine(directory, stem + " (" + Guid.NewGuid().ToString("N") + ")" + extension);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CleanUpCurrent(true);
        }
    }
}
