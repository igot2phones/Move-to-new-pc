using System;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.Core.Package
{
    /// <summary>
    /// An <see cref="ITransferSink"/> that writes the record stream of docs/PROTOCOL.md §6
    /// into a <see cref="SecureBlockWriter"/>.
    ///
    /// It does not know or care what is underneath that writer. A file gives you the
    /// encrypted package (<see cref="PackageSink"/>); a socket gives you the LAN sender.
    /// Both therefore produce byte-identical record streams, and the receiving side is the
    /// same code in both cases.
    ///
    /// Owning the writer is the caller's job: this class never closes it, because the
    /// network case needs the channel to stay open afterwards.
    /// </summary>
    public sealed class RecordSink : ITransferSink
    {
        private readonly SecureBlockWriter _writer;
        private readonly string _destinationLabel;
        private bool _fileOpen;
        private long _currentLength;
        private long _currentWritten;
        private string _currentPath;

        public RecordSink(SecureBlockWriter writer, string destinationLabel)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }
            _writer = writer;
            _destinationLabel = destinationLabel ?? string.Empty;
        }

        /// <summary>True once the end-of-stream record has been written.</summary>
        public bool Finished { get; private set; }

        public void BeginSession(TransferManifest manifest)
        {
            _writer.WriteByte(PackageFormat.TagHeader);
            PackageFormat.WriteString(_writer, manifest.ManifestId);
            PackageFormat.WriteInt64(_writer, manifest.CreatedUtc.ToUniversalTime().Ticks);
            PackageFormat.WriteString(_writer, manifest.SourceMachine);
            PackageFormat.WriteString(_writer, manifest.ToolVersion);

            // Totals travel with the stream so the far end can show a real progress bar
            // instead of an unbounded spinner.
            PackageFormat.WriteInt64(_writer, manifest.Totals.FileCount);
            PackageFormat.WriteInt64(_writer, manifest.Totals.ByteCount);
            PackageFormat.WriteInt64(_writer, manifest.Totals.DirectoryCount);

            PackageFormat.WriteInt32(_writer, manifest.Users.Count);
            for (int u = 0; u < manifest.Users.Count; u++)
            {
                ManifestUser user = manifest.Users[u];
                PackageFormat.WriteInt32(_writer, user.UserIndex);
                PackageFormat.WriteString(_writer, user.Sid);
                PackageFormat.WriteString(_writer, user.AccountName);
                PackageFormat.WriteString(_writer, user.ProfilePath);
                PackageFormat.WriteString(_writer, user.DestinationHint);

                PackageFormat.WriteInt32(_writer, user.Roots.Count);
                for (int r = 0; r < user.Roots.Count; r++)
                {
                    ManifestRoot root = user.Roots[r];
                    PackageFormat.WriteInt32(_writer, root.UserIndex);
                    PackageFormat.WriteInt32(_writer, root.RootIndex);
                    PackageFormat.WriteInt32(_writer, (int)root.Tier);
                    PackageFormat.WriteString(_writer, root.SourcePath);
                    PackageFormat.WriteString(_writer, root.DestinationRelativeRoot);
                    PackageFormat.WriteString(_writer, root.Label);
                }
            }
        }

        public void EnsureDirectory(ManifestUser user, ManifestRoot root, ManifestDirectory directory)
        {
            _writer.WriteByte(PackageFormat.TagDirectory);
            PackageFormat.WriteInt32(_writer, directory.UserIndex);
            PackageFormat.WriteInt32(_writer, directory.RootIndex);
            PackageFormat.WriteString(_writer, directory.RelativePath);
            PackageFormat.WriteInt32(_writer, unchecked((int)directory.Attributes));
            PackageFormat.WriteInt64(_writer, directory.CreationTimeUtc);
            PackageFormat.WriteInt64(_writer, directory.LastAccessTimeUtc);
            PackageFormat.WriteInt64(_writer, directory.LastWriteTimeUtc);
        }

        public SinkFileDecision BeginFile(ManifestUser user, ManifestRoot root, ManifestEntry entry,
                                          out string destinationDisplayPath)
        {
            if (_fileOpen)
            {
                throw new InvalidOperationException("BeginFile called while a file is still open.");
            }

            destinationDisplayPath = _destinationLabel.Length > 0
                ? _destinationLabel + " : " + entry.RelativePath
                : entry.RelativePath;

            _writer.WriteByte(PackageFormat.TagFileBegin);
            PackageFormat.WriteInt32(_writer, entry.UserIndex);
            PackageFormat.WriteInt32(_writer, entry.RootIndex);
            PackageFormat.WriteString(_writer, entry.RelativePath);
            PackageFormat.WriteInt64(_writer, entry.Length);
            PackageFormat.WriteInt32(_writer, unchecked((int)entry.Attributes));
            PackageFormat.WriteInt64(_writer, entry.CreationTimeUtc);
            PackageFormat.WriteInt64(_writer, entry.LastAccessTimeUtc);
            PackageFormat.WriteInt64(_writer, entry.LastWriteTimeUtc);

            _fileOpen = true;
            _currentLength = entry.Length;
            _currentWritten = 0;
            _currentPath = entry.RelativePath;
            return SinkFileDecision.Write;
        }

        public void WriteChunk(byte[] buffer, int offset, int count)
        {
            if (!_fileOpen)
            {
                throw new InvalidOperationException("WriteChunk called with no file open.");
            }
            if (count <= 0)
            {
                return;
            }

            _writer.WriteByte(PackageFormat.TagChunk);
            PackageFormat.WriteInt32(_writer, count);
            _writer.Write(buffer, offset, count);
            _currentWritten += count;
        }

        public void EndFile(byte[] sha256)
        {
            if (!_fileOpen)
            {
                return;
            }

            _writer.WriteByte(PackageFormat.TagFileEnd);
            PackageFormat.WriteInt64(_writer, _currentWritten);
            PackageFormat.WriteBytes(_writer, sha256);

            if (_currentLength >= 0 && _currentWritten != _currentLength)
            {
                // The file changed size while we read it. Recorded, not fatal - the far end
                // trusts the length we actually sent, not the one the scan predicted.
                Log.Debug("Length changed while sending " + _currentPath + ": expected "
                          + _currentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                          + ", sent "
                          + _currentWritten.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _fileOpen = false;
            _currentPath = null;
        }

        public void AbortFile(SkipReason reason, string detail)
        {
            if (!_fileOpen)
            {
                return;
            }

            // Whatever was already sent stays in the stream; the far end discards a file
            // whose end record says it was abandoned. We cannot rewind a stream, and
            // truncating would break the frame chain.
            _writer.WriteByte(PackageFormat.TagFileEnd);
            PackageFormat.WriteInt64(_writer, -1);
            PackageFormat.WriteBytes(_writer, null);
            _fileOpen = false;

            _writer.WriteByte(PackageFormat.TagSkip);
            PackageFormat.WriteInt32(_writer, 0);
            PackageFormat.WriteInt32(_writer, 0);
            PackageFormat.WriteString(_writer, _currentPath);
            PackageFormat.WriteInt32(_writer, (int)reason);
            PackageFormat.WriteInt64(_writer, 0);
            PackageFormat.WriteString(_writer, detail);

            _currentPath = null;
        }

        public void EndSession(bool completedNormally)
        {
            if (Finished)
            {
                return;
            }

            _writer.WriteByte(PackageFormat.TagEnd);
            _writer.Flush();
            Finished = completedNormally;
        }

        /// <summary>
        /// Unknown by construction: a record stream has no volume behind it. The file and
        /// network wrappers answer this properly where they can.
        /// </summary>
        public long GetAvailableBytes()
        {
            return -1;
        }

        /// <summary>The writer belongs to whoever created it.</summary>
        public void Dispose()
        {
        }
    }
}
