using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.Transfer
{
    public enum CollisionPolicy
    {
        /// <summary>Default. Never silently overwrite anything on the new PC.</summary>
        Skip = 0,
        Overwrite = 1,
        /// <summary>Write "name (1).ext" beside the existing file.</summary>
        KeepBoth = 2
    }

    public sealed class CopyOptions
    {
        public CollisionPolicy Collision = CollisionPolicy.Skip;
        public bool VerifyHash = true;
        public bool PreserveTimestamps = true;
        public bool PreserveAttributes = true;
        /// <summary>Opt-in. Reading a placeholder otherwise drags gigabytes off the network.</summary>
        public bool HydrateCloudFiles;
        public bool IncludeEncryptedFiles;
        public int RetryCount = 2;
        public int RetryDelayMs = 250;
        public int ChunkSize = 512 * 1024;
        /// <summary>Produce the whole report without writing a byte.</summary>
        public bool DryRun;
        /// <summary>Refuse to start when the destination volume is short of this much slack.</summary>
        public long FreeSpaceMarginBytes = 64L * 1024 * 1024;

        public static CopyOptions Defaults()
        {
            return new CopyOptions();
        }
    }

    /// <summary>What the sink wants done with a file the engine is about to send.</summary>
    public enum SinkFileDecision
    {
        Write = 0,
        /// <summary>Collision policy says leave the destination alone.</summary>
        Skip = 1,
        /// <summary>Resume: the receiver already has this file, verified.</summary>
        AlreadyComplete = 2
    }

    /// <summary>
    /// Where copied bytes go. The file engine never knows whether this is a folder on a
    /// USB disk, an encrypted socket, or a dry-run counter - that is the entire point.
    /// Implementations are used from ONE thread at a time.
    /// </summary>
    public interface ITransferSink : IDisposable
    {
        void BeginSession(TransferManifest manifest);

        /// <summary>Creates the destination directory and stamps its metadata.</summary>
        void EnsureDirectory(ManifestUser user, ManifestRoot root, ManifestDirectory directory);

        SinkFileDecision BeginFile(ManifestUser user, ManifestRoot root, ManifestEntry entry, out string destinationDisplayPath);

        void WriteChunk(byte[] buffer, int offset, int count);

        /// <summary>Finishes the current file. <paramref name="sha256"/> may be null when verification is off.</summary>
        void EndFile(byte[] sha256);

        /// <summary>Abandons the current file, removing any partial output.</summary>
        void AbortFile(SkipReason reason, string detail);

        void EndSession(bool completedNormally);

        /// <summary>Free bytes on the destination, or -1 when unknown (e.g. network sink).</summary>
        long GetAvailableBytes();
    }

    /// <summary>
    /// Progress callbacks. Raised on the worker thread; UI implementations must marshal
    /// with Control.BeginInvoke (no async/await on this target).
    /// </summary>
    public interface ITransferObserver
    {
        void OnStatus(string message);
        void OnFileStarted(string sourceDisplayPath, string destinationDisplayPath, long length);
        void OnBytesTransferred(long deltaBytes);
        void OnFileCompleted(string sourceDisplayPath, long length);
        void OnSkipped(SkippedItem item);
        void OnTotals(long filesDone, long filesTotal, long bytesDone, long bytesTotal);
    }

    /// <summary>Discards everything. The dry-run sink.</summary>
    public sealed class NullSink : ITransferSink
    {
        public long Files;
        public long Bytes;
        public long Directories;

        public void BeginSession(TransferManifest manifest) { }

        public void EnsureDirectory(ManifestUser user, ManifestRoot root, ManifestDirectory directory)
        {
            Directories++;
        }

        public SinkFileDecision BeginFile(ManifestUser user, ManifestRoot root, ManifestEntry entry,
                                          out string destinationDisplayPath)
        {
            destinationDisplayPath = "(dry run)";
            Files++;
            return SinkFileDecision.Write;
        }

        public void WriteChunk(byte[] buffer, int offset, int count) { Bytes += count; }

        public void EndFile(byte[] sha256) { }

        public void AbortFile(SkipReason reason, string detail) { }

        public void EndSession(bool completedNormally) { }

        public long GetAvailableBytes() { return -1; }

        public void Dispose() { }
    }

    public sealed class TransferResult
    {
        public bool Completed;
        public bool Cancelled;
        public string FailureMessage;

        public long FilesCopied;
        public long FilesSkipped;
        public long FilesFailed;
        public long DirectoriesCreated;
        public long BytesCopied;
        public long BytesSkipped;

        public DateTime StartedUtc;
        public DateTime FinishedUtc;

        public List<SkippedItem> Skipped = new List<SkippedItem>();

        public TimeSpan Duration
        {
            get { return FinishedUtc > StartedUtc ? FinishedUtc - StartedUtc : TimeSpan.Zero; }
        }

        public double AverageBytesPerSecond
        {
            get
            {
                double seconds = Duration.TotalSeconds;
                return seconds > 0.001 ? BytesCopied / seconds : 0;
            }
        }
    }
}
