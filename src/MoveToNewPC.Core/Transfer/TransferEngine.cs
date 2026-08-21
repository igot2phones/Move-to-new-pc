using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Native;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Transfer
{
    /// <summary>
    /// Pass two: read the manifest and move the bytes. Knows nothing about where they are
    /// going - that is entirely the sink's business.
    ///
    /// The contract this class exists to honour: one bad file never stops the run. Every
    /// per-file failure becomes a skip with a reason and the loop continues.
    /// </summary>
    public sealed class TransferEngine
    {
        private readonly CopyOptions _options;
        private readonly ITransferSink _sink;
        private readonly ITransferObserver _observer;

        /// <summary>Cap on the skip list held in memory; the log keeps everything.</summary>
        private const int MaxInMemorySkips = 5000;

        public TransferEngine(CopyOptions options, ITransferSink sink, ITransferObserver observer)
        {
            _options = options ?? CopyOptions.Defaults();
            _sink = sink;
            _observer = observer;
        }

        public TransferResult Run(TransferManifest manifest, string manifestPath,
                                  CancellationToken cancel, PauseGate pause)
        {
            TransferResult result = new TransferResult();
            result.StartedUtc = DateTime.UtcNow;

            if (manifest == null || string.IsNullOrEmpty(manifestPath))
            {
                result.FailureMessage = "No manifest to transfer.";
                result.FinishedUtc = DateTime.UtcNow;
                return result;
            }

            long totalBytes = manifest.Totals.ByteCount;
            long totalFiles = manifest.Totals.FileCount;

            if (!CheckFreeSpace(totalBytes, result))
            {
                result.FinishedUtc = DateTime.UtcNow;
                return result;
            }

            byte[] buffer = new byte[Math.Max(64 * 1024, _options.ChunkSize)];
            ManifestRecord record = new ManifestRecord();

            try
            {
                _sink.BeginSession(manifest);
                Status("Starting...");

                using (ManifestReader reader = new ManifestReader(manifestPath))
                {
                    while (reader.Read(record))
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            break;
                        }
                        if (!pause.Wait(cancel))
                        {
                            result.Cancelled = true;
                            break;
                        }

                        switch (record.Kind)
                        {
                            case ManifestRecordKind.Directory:
                                HandleDirectory(manifest, record.Directory, result);
                                break;

                            case ManifestRecordKind.File:
                                CopyOne(manifest, record.File, buffer, result, cancel, pause);
                                ReportTotals(result, totalFiles, totalBytes);
                                break;

                            case ManifestRecordKind.Skip:
                                // Carried through from the scan so the final report is complete.
                                RecordSkip(result, record.Skip, false);
                                break;
                        }
                    }
                }

                result.Completed = !result.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Error("Transfer failed", ex);
                result.FailureMessage = ex.Message;
                result.Completed = false;
            }
            finally
            {
                try
                {
                    _sink.EndSession(result.Completed);
                }
                catch (Exception ex)
                {
                    Log.Error("Sink shutdown failed", ex);
                }

                result.FinishedUtc = DateTime.UtcNow;
            }

            Log.Info("Transfer finished: copied=" + result.FilesCopied
                     + " skipped=" + result.FilesSkipped
                     + " failed=" + result.FilesFailed
                     + " bytes=" + result.BytesCopied
                     + " cancelled=" + result.Cancelled);

            return result;
        }

        private bool CheckFreeSpace(long totalBytes, TransferResult result)
        {
            if (_options.DryRun || totalBytes <= 0)
            {
                return true;
            }

            long available = _sink.GetAvailableBytes();
            if (available < 0)
            {
                return true;   // unknown, e.g. a network sink
            }

            long required = totalBytes + _options.FreeSpaceMarginBytes;
            if (available >= required)
            {
                return true;
            }

            // Refuse with a real number rather than dying halfway through.
            result.FailureMessage =
                "Not enough free space at the destination." + Environment.NewLine
                + "Needed:    " + Format.Bytes(required) + Environment.NewLine
                + "Available: " + Format.Bytes(available) + Environment.NewLine
                + "Short by:  " + Format.Bytes(required - available);
            Log.Error(result.FailureMessage.Replace(Environment.NewLine, " | "));
            return false;
        }

        private void HandleDirectory(TransferManifest manifest, ManifestDirectory directory, TransferResult result)
        {
            ManifestUser user = manifest.FindUser(directory.UserIndex);
            ManifestRoot root = manifest.FindRoot(directory.UserIndex, directory.RootIndex);
            if (user == null || root == null)
            {
                return;
            }

            try
            {
                _sink.EnsureDirectory(user, root, directory);
                result.DirectoriesCreated++;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not create directory " + directory.RelativePath + ": " + ex.Message);
                RecordSkip(result, new SkippedItem(directory.RelativePath, true,
                                                   SkipReason.WriteError, ex.Message, 0), true);
            }
        }

        private void CopyOne(TransferManifest manifest, ManifestEntry entry, byte[] buffer,
                             TransferResult result, CancellationToken cancel, PauseGate pause)
        {
            ManifestUser user = manifest.FindUser(entry.UserIndex);
            ManifestRoot root = manifest.FindRoot(entry.UserIndex, entry.RootIndex);
            if (user == null || root == null)
            {
                RecordSkip(result, new SkippedItem(entry.RelativePath, false, SkipReason.InvalidPath,
                                                   "Manifest entry references an unknown root", entry.Length), true);
                return;
            }

            string sourcePath = LongPath.Combine(root.SourcePath, entry.RelativePath);
            string displaySource = LongPath.ToDisplay(sourcePath);

            int attempts = Math.Max(1, _options.RetryCount + 1);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (cancel.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return;
                }

                if (attempt > 0)
                {
                    // Backoff, doubling. Locks are usually transient: a browser finishing a
                    // write, an indexer letting go.
                    int delay = _options.RetryDelayMs * (1 << (attempt - 1));
                    Log.Debug("Retry " + attempt + " for " + displaySource + " in " + delay + "ms");
                    if (cancel.WaitHandle.WaitOne(delay))
                    {
                        result.Cancelled = true;
                        return;
                    }
                }

                SkipReason failure;
                string detail;
                bool retryable;

                if (TryCopyOnce(user, root, entry, sourcePath, displaySource, buffer, result,
                                cancel, pause, out failure, out detail, out retryable))
                {
                    return;
                }

                if (result.Cancelled)
                {
                    return;
                }

                if (!retryable || attempt == attempts - 1)
                {
                    RecordSkip(result, new SkippedItem(displaySource, false, failure, detail, entry.Length), true);
                    return;
                }
            }
        }

        /// <summary>Returns true when the file is done (copied or deliberately skipped).</summary>
        private bool TryCopyOnce(ManifestUser user, ManifestRoot root, ManifestEntry entry,
                                 string sourcePath, string displaySource, byte[] buffer,
                                 TransferResult result, CancellationToken cancel, PauseGate pause,
                                 out SkipReason failure, out string detail, out bool retryable)
        {
            failure = SkipReason.UnknownError;
            detail = null;
            retryable = false;

            int error;
            FileStream source = null;

            try
            {
                source = NativeFile.OpenRead(sourcePath, _options.HydrateCloudFiles, _options.ChunkSize, out error);
            }
            catch (Exception ex)
            {
                failure = SkipReason.ReadError;
                detail = ex.Message;
                return false;
            }

            if (source == null)
            {
                failure = DirectoryWalker.MapError(error);
                detail = NativeFile.DescribeError(error);
                retryable = error == NativeMethods.ERROR_SHARING_VIOLATION
                            || error == NativeMethods.ERROR_LOCK_VIOLATION
                            || error == NativeMethods.ERROR_ACCESS_DENIED;
                return false;
            }

            bool sinkOpen = false;

            using (source)
            {
                string destinationDisplay;
                SinkFileDecision decision;

                try
                {
                    decision = _sink.BeginFile(user, root, entry, out destinationDisplay);
                }
                catch (TransferVerificationException ex)
                {
                    failure = SkipReason.InvalidPath;
                    detail = ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    failure = SkipReason.WriteError;
                    detail = ex.Message;
                    retryable = true;
                    return false;
                }

                if (decision == SinkFileDecision.Skip)
                {
                    RecordSkip(result, new SkippedItem(displaySource, false, SkipReason.DestinationExists,
                                                       "Already present at the destination", entry.Length), false);
                    return true;
                }

                if (decision == SinkFileDecision.AlreadyComplete)
                {
                    RecordSkip(result, new SkippedItem(displaySource, false, SkipReason.AlreadyTransferred,
                                                       "Transferred by an earlier run", entry.Length), false);
                    return true;
                }

                sinkOpen = true;

                if (_observer != null)
                {
                    _observer.OnFileStarted(displaySource, destinationDisplay, entry.Length);
                }

                HashAlgorithm hash = _options.VerifyHash ? HashFactory.CreateSha256() : null;

                try
                {
                    long copied = 0;
                    while (true)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            _sink.AbortFile(SkipReason.Cancelled, "Cancelled by the operator");
                            sinkOpen = false;
                            return false;
                        }
                        if (!pause.Wait(cancel))
                        {
                            result.Cancelled = true;
                            _sink.AbortFile(SkipReason.Cancelled, "Cancelled by the operator");
                            sinkOpen = false;
                            return false;
                        }

                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        _sink.WriteChunk(buffer, 0, read);
                        HashFactory.Update(hash, buffer, 0, read);
                        copied += read;

                        if (_observer != null)
                        {
                            _observer.OnBytesTransferred(read);
                        }
                    }

                    byte[] digest = HashFactory.Finish(hash);
                    _sink.EndFile(digest);
                    sinkOpen = false;

                    result.FilesCopied++;
                    result.BytesCopied += copied;

                    if (_observer != null)
                    {
                        _observer.OnFileCompleted(displaySource, copied);
                    }

                    return true;
                }
                catch (TransferVerificationException ex)
                {
                    // Worth one retry: a checksum mismatch is occasionally a transient disk
                    // or memory glitch rather than a real corruption.
                    failure = SkipReason.HashMismatch;
                    detail = ex.Message;
                    retryable = true;
                    return false;
                }
                catch (IOException ex)
                {
                    failure = SkipReason.ReadError;
                    detail = ex.Message;
                    retryable = true;
                    return false;
                }
                catch (UnauthorizedAccessException ex)
                {
                    failure = SkipReason.AccessDenied;
                    detail = ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    failure = SkipReason.UnknownError;
                    detail = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
                finally
                {
                    if (hash != null)
                    {
                        try { hash.Clear(); }
                        catch (Exception) { }
                    }

                    if (sinkOpen)
                    {
                        try
                        {
                            _sink.AbortFile(failure, detail ?? "Aborted");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("AbortFile threw: " + ex.Message);
                        }
                    }
                }
            }
        }

        private void RecordSkip(TransferResult result, SkippedItem item, bool isFailure)
        {
            if (item == null)
            {
                return;
            }

            if (isFailure || SkipReasons.IsFailure(item.Reason))
            {
                result.FilesFailed++;
            }
            else
            {
                result.FilesSkipped++;
            }

            result.BytesSkipped += item.Length;

            if (result.Skipped.Count < MaxInMemorySkips)
            {
                result.Skipped.Add(item);
            }

            Log.Info("SKIP [" + item.Reason + "] " + item.Path
                     + (string.IsNullOrEmpty(item.Detail) ? string.Empty : " - " + item.Detail));

            if (_observer != null)
            {
                _observer.OnSkipped(item);
            }
        }

        private void ReportTotals(TransferResult result, long totalFiles, long totalBytes)
        {
            if (_observer == null)
            {
                return;
            }
            _observer.OnTotals(result.FilesCopied + result.FilesSkipped + result.FilesFailed,
                               totalFiles, result.BytesCopied + result.BytesSkipped, totalBytes);
        }

        private void Status(string message)
        {
            if (_observer != null)
            {
                _observer.OnStatus(message);
            }
        }
    }
}
