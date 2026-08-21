using System;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.Core.Net
{
    /// <summary>
    /// Sender side of a LAN transfer: an <see cref="ITransferSink"/> that puts the record
    /// stream on an encrypted socket instead of into a file.
    ///
    /// The engine above it is unchanged and cannot tell the difference - which is the point
    /// of the sink interface, and the reason the network path needed no new scan, filter,
    /// hash or report code.
    /// </summary>
    public sealed class NetworkSink : ITransferSink
    {
        private SecureChannel _channel;
        private RecordSink _records;
        private readonly bool _ownsChannel;
        private bool _disposed;

        public NetworkSink(SecureChannel channel, bool ownsChannel)
        {
            if (channel == null) { throw new ArgumentNullException("channel"); }
            _channel = channel;
            _ownsChannel = ownsChannel;
            _records = new RecordSink(channel.Writer, channel.PeerMachineName);
        }

        public void BeginSession(TransferManifest manifest)
        {
            _records.BeginSession(manifest);
        }

        public void EnsureDirectory(ManifestUser user, ManifestRoot root, ManifestDirectory directory)
        {
            _records.EnsureDirectory(user, root, directory);
        }

        public SinkFileDecision BeginFile(ManifestUser user, ManifestRoot root, ManifestEntry entry,
                                          out string destinationDisplayPath)
        {
            return _records.BeginFile(user, root, entry, out destinationDisplayPath);
        }

        public void WriteChunk(byte[] buffer, int offset, int count)
        {
            _records.WriteChunk(buffer, offset, count);
        }

        public void EndFile(byte[] sha256)
        {
            _records.EndFile(sha256);
        }

        public void AbortFile(SkipReason reason, string detail)
        {
            _records.AbortFile(reason, detail);
        }

        public void EndSession(bool completedNormally)
        {
            if (_records == null)
            {
                return;
            }
            _records.EndSession(completedNormally);
        }

        /// <summary>
        /// Unknown by design. The free space that matters is on the far machine, and asking
        /// it would be a round trip per query for a number that can change anyway; the
        /// receiver refuses the transfer itself if it runs short.
        /// </summary>
        public long GetAvailableBytes()
        {
            return -1;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _records = null;

            if (_ownsChannel && _channel != null)
            {
                try { _channel.Dispose(); }
                catch (Exception ex) { Log.Debug("Closing the channel failed: " + ex.Message); }
            }
            _channel = null;
        }
    }
}
