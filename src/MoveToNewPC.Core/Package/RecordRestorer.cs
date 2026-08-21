using System;
using System.Threading;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Transfer;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Package
{
    /// <summary>
    /// Reads the record stream of docs/PROTOCOL.md §6 out of a
    /// <see cref="SecureBlockReader"/> and replays it into an <see cref="ITransferSink"/> -
    /// normally a <c>LocalFolderSink</c>.
    ///
    /// Underneath the reader may be a file (an encrypted package) or a socket (the LAN
    /// receiver); this class cannot tell the difference and does not need to.
    ///
    /// Replaying into the ordinary sink rather than writing files directly is the whole
    /// design: path validation, the collision policy, hash verification, timestamp
    /// preservation and the resume journal are all existing, tested code.
    ///
    /// The far end is assumed hostile (docs/PROTOCOL.md §5). Frame MACs stop tampering, and
    /// the sink re-validates every relative path regardless of what the stream claims.
    /// </summary>
    public sealed class RecordRestorer
    {
        private readonly SecureBlockReader _reader;

        public TransferManifest Manifest { get; private set; }

        public RecordRestorer(SecureBlockReader reader)
        {
            if (reader == null) { throw new ArgumentNullException("reader"); }
            _reader = reader;
        }

        /// <summary>
        /// Reads the leading header record. Must be called once before <see cref="Restore"/>.
        /// </summary>
        public void ReadHeader()
        {
            int tag = _reader.ReadByteOrMinusOne();
            if (tag != PackageFormat.TagHeader)
            {
                throw new SecureChannelException("The transfer does not start with a header record.");
            }

            TransferManifest manifest = new TransferManifest();
            manifest.ManifestId = PackageFormat.ReadString(_reader);
            manifest.CreatedUtc = new DateTime(PackageFormat.ReadInt64(_reader), DateTimeKind.Utc);
            manifest.SourceMachine = PackageFormat.ReadString(_reader);
            manifest.ToolVersion = PackageFormat.ReadString(_reader);

            manifest.Totals.FileCount = PackageFormat.ReadInt64(_reader);
            manifest.Totals.ByteCount = PackageFormat.ReadInt64(_reader);
            manifest.Totals.DirectoryCount = PackageFormat.ReadInt64(_reader);

            int userCount = PackageFormat.ReadInt32(_reader);
            if (userCount < 0 || userCount > 4096)
            {
                throw new SecureChannelException("The transfer declares an implausible number of users.");
            }

            for (int u = 0; u < userCount; u++)
            {
                ManifestUser user = new ManifestUser();
                user.UserIndex = PackageFormat.ReadInt32(_reader);
                user.Sid = PackageFormat.ReadString(_reader);
                user.AccountName = PackageFormat.ReadString(_reader);
                user.ProfilePath = PackageFormat.ReadString(_reader);
                user.DestinationHint = PackageFormat.ReadString(_reader);

                int rootCount = PackageFormat.ReadInt32(_reader);
                if (rootCount < 0 || rootCount > 4096)
                {
                    throw new SecureChannelException("The transfer declares an implausible number of folders.");
                }

                for (int r = 0; r < rootCount; r++)
                {
                    ManifestRoot root = new ManifestRoot();
                    root.UserIndex = PackageFormat.ReadInt32(_reader);
                    root.RootIndex = PackageFormat.ReadInt32(_reader);
                    root.Tier = (MoveToNewPC.Core.Selection.SelectionTier)PackageFormat.ReadInt32(_reader);
                    root.SourcePath = PackageFormat.ReadString(_reader);
                    root.DestinationRelativeRoot = PackageFormat.ReadString(_reader);
                    root.Label = PackageFormat.ReadString(_reader);
                    user.Roots.Add(root);
                }

                manifest.Users.Add(user);
            }

            Manifest = manifest;
            Log.Info("Transfer header: from " + (manifest.SourceMachine ?? "?")
                     + ", written by " + (manifest.ToolVersion ?? "?")
                     + ", " + manifest.Users.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " user(s), "
                     + manifest.Totals.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " file(s).");
        }

        /// <summary>
        /// Replays the stream into the sink. Never throws for a single bad file: that file
        /// is recorded as failed and the run continues, exactly like a locked file during a
        /// normal copy. Structural damage does stop it, because nothing after that point
        /// can be trusted.
        /// </summary>
        public TransferResult Restore(ITransferSink sink, ITransferObserver observer,
                                      CancellationToken cancellation, PauseGate gate)
        {
            if (sink == null) { throw new ArgumentNullException("sink"); }
            if (Manifest == null) { throw new InvalidOperationException("ReadHeader was not called."); }

            TransferResult result = new TransferResult();
            result.StartedUtc = DateTime.UtcNow;

            byte[] chunkBuffer = new byte[SecureBlockWriter.BlockSize];
            sink.BeginSession(Manifest);

            bool completed = false;
            try
            {
                while (true)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    if (gate != null && !gate.Wait(cancellation))
                    {
                        result.Cancelled = true;
                        break;
                    }

                    int tag = _reader.ReadByteOrMinusOne();
                    if (tag < 0 || tag == PackageFormat.TagEnd)
                    {
                        completed = tag == PackageFormat.TagEnd;
                        break;
                    }

                    switch (tag)
                    {
                        case PackageFormat.TagDirectory:
                            RestoreDirectory(sink, result);
                            break;

                        case PackageFormat.TagFileBegin:
                            RestoreFile(sink, observer, result, chunkBuffer);
                            break;

                        case PackageFormat.TagSkip:
                            RestoreSkipRecord(observer, result);
                            break;

                        default:
                            throw new SecureChannelException(
                                "The transfer contains an unknown record type ("
                                + tag.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
                    }
                }
            }
            catch (SecureChannelException ex)
            {
                result.FailureMessage = ex.Message;
                Log.Error("Restore stopped: " + ex.Message);
            }
            finally
            {
                try { sink.EndSession(completed && !result.Cancelled); }
                catch (Exception ex) { Log.Warn("Closing the destination failed: " + ex.Message); }
                result.Completed = completed && !result.Cancelled && result.FailureMessage == null;
                result.FinishedUtc = DateTime.UtcNow;
            }

            return result;
        }

        private void RestoreDirectory(ITransferSink sink, TransferResult result)
        {
            ManifestDirectory directory = new ManifestDirectory();
            directory.UserIndex = PackageFormat.ReadInt32(_reader);
            directory.RootIndex = PackageFormat.ReadInt32(_reader);
            directory.RelativePath = PackageFormat.ReadString(_reader);
            directory.Attributes = unchecked((uint)PackageFormat.ReadInt32(_reader));
            directory.CreationTimeUtc = PackageFormat.ReadInt64(_reader);
            directory.LastAccessTimeUtc = PackageFormat.ReadInt64(_reader);
            directory.LastWriteTimeUtc = PackageFormat.ReadInt64(_reader);

            ManifestUser user = Manifest.FindUser(directory.UserIndex);
            ManifestRoot root = Manifest.FindRoot(directory.UserIndex, directory.RootIndex);
            if (user == null || root == null)
            {
                Log.Warn("Directory record refers to an unknown user or folder; ignored: "
                         + directory.RelativePath);
                return;
            }

            try
            {
                sink.EnsureDirectory(user, root, directory);
                result.DirectoriesCreated++;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not create " + directory.RelativePath + ": " + ex.Message);
            }
        }

        private void RestoreFile(ITransferSink sink, ITransferObserver observer, TransferResult result,
                                 byte[] chunkBuffer)
        {
            ManifestEntry entry = new ManifestEntry();
            entry.UserIndex = PackageFormat.ReadInt32(_reader);
            entry.RootIndex = PackageFormat.ReadInt32(_reader);
            entry.RelativePath = PackageFormat.ReadString(_reader);
            entry.Length = PackageFormat.ReadInt64(_reader);
            entry.Attributes = unchecked((uint)PackageFormat.ReadInt32(_reader));
            entry.CreationTimeUtc = PackageFormat.ReadInt64(_reader);
            entry.LastAccessTimeUtc = PackageFormat.ReadInt64(_reader);
            entry.LastWriteTimeUtc = PackageFormat.ReadInt64(_reader);

            ManifestUser user = Manifest.FindUser(entry.UserIndex);
            ManifestRoot root = Manifest.FindRoot(entry.UserIndex, entry.RootIndex);

            bool opened = false;
            string destination = null;
            SinkFileDecision decision = SinkFileDecision.Skip;
            string openFailure = null;

            if (user == null || root == null)
            {
                openFailure = "The transfer refers to an unknown user or folder.";
            }
            else
            {
                try
                {
                    decision = sink.BeginFile(user, root, entry, out destination);
                    opened = decision == SinkFileDecision.Write;
                }
                catch (Exception ex)
                {
                    openFailure = ex.Message;
                }
            }

            if (opened && observer != null)
            {
                observer.OnFileStarted(entry.RelativePath, destination, entry.Length);
            }

            // The chunk stream must be consumed either way: skipping a file still means
            // reading past its bytes to reach the next record.
            long consumed = 0;
            byte[] recordedHash = null;
            long recordedLength = 0;
            bool aborted = false;
            string failure = openFailure;

            while (true)
            {
                int tag = _reader.ReadByteOrMinusOne();
                if (tag == PackageFormat.TagChunk)
                {
                    int count = PackageFormat.ReadInt32(_reader);
                    if (count < 0 || count > SecureBlockWriter.BlockSize * 16)
                    {
                        throw new SecureChannelException("The transfer declares an implausible chunk size.");
                    }

                    int remaining = count;
                    while (remaining > 0)
                    {
                        int take = remaining < chunkBuffer.Length ? remaining : chunkBuffer.Length;
                        _reader.ReadExactly(chunkBuffer, 0, take);
                        remaining -= take;
                        consumed += take;

                        if (opened && failure == null)
                        {
                            try
                            {
                                sink.WriteChunk(chunkBuffer, 0, take);
                                if (observer != null)
                                {
                                    observer.OnBytesTransferred(take);
                                }
                            }
                            catch (Exception ex)
                            {
                                failure = ex.Message;
                            }
                        }
                    }
                }
                else if (tag == PackageFormat.TagFileEnd)
                {
                    recordedLength = PackageFormat.ReadInt64(_reader);
                    recordedHash = PackageFormat.ReadBytes(_reader, 64);
                    aborted = recordedLength < 0;
                    break;
                }
                else
                {
                    throw new SecureChannelException(
                        "The transfer is malformed: expected file data but found record type "
                        + tag.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                }
            }

            if (!opened)
            {
                if (failure != null)
                {
                    RecordFailure(observer, result, entry, failure);
                }
                else
                {
                    // The sink declined it: already present, or already done on a resume.
                    result.FilesSkipped++;
                    if (decision == SinkFileDecision.AlreadyComplete && observer != null)
                    {
                        observer.OnFileCompleted(entry.RelativePath, entry.Length);
                    }
                }
                return;
            }

            if (aborted)
            {
                SafeAbort(sink, SkipReason.ReadError, "The file could not be read on the old PC.");
                RecordFailure(observer, result, entry, "Not fully captured on the old PC.");
                return;
            }

            if (failure != null)
            {
                SafeAbort(sink, SkipReason.WriteError, failure);
                RecordFailure(observer, result, entry, failure);
                return;
            }

            try
            {
                sink.EndFile(recordedHash);
                result.FilesCopied++;
                result.BytesCopied += consumed;
                if (observer != null)
                {
                    observer.OnFileCompleted(entry.RelativePath, consumed);
                }
            }
            catch (Exception ex)
            {
                SafeAbort(sink, SkipReason.HashMismatch, ex.Message);
                RecordFailure(observer, result, entry, ex.Message);
            }
        }

        private static void SafeAbort(ITransferSink sink, SkipReason reason, string detail)
        {
            try { sink.AbortFile(reason, detail); }
            catch (Exception ex) { Log.Debug("AbortFile failed: " + ex.Message); }
        }

        private static void RecordFailure(ITransferObserver observer, TransferResult result,
                                          ManifestEntry entry, string detail)
        {
            SkippedItem item = new SkippedItem();
            item.Path = entry.RelativePath;
            item.Reason = SkipReason.WriteError;
            item.Detail = detail;
            item.Length = entry.Length;

            result.FilesFailed++;
            result.BytesSkipped += entry.Length > 0 ? entry.Length : 0;
            result.Skipped.Add(item);

            if (observer != null)
            {
                observer.OnSkipped(item);
            }
            Log.Warn("Could not write " + entry.RelativePath + ": " + detail);
        }

        private void RestoreSkipRecord(ITransferObserver observer, TransferResult result)
        {
            SkippedItem item = new SkippedItem();
            PackageFormat.ReadInt32(_reader);
            PackageFormat.ReadInt32(_reader);
            item.Path = PackageFormat.ReadString(_reader);
            item.Reason = (SkipReason)PackageFormat.ReadInt32(_reader);
            item.Length = PackageFormat.ReadInt64(_reader);
            item.Detail = PackageFormat.ReadString(_reader);

            result.FilesSkipped++;
            result.Skipped.Add(item);
            if (observer != null)
            {
                observer.OnSkipped(item);
            }
        }
    }
}
