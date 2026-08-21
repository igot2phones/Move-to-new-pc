using System;
using System.IO;
using System.Threading;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Transfer;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Package
{
    /// <summary>
    /// Opens an encrypted package on the new PC and replays it into an
    /// <see cref="ITransferSink"/>.
    ///
    /// The record decoding is <see cref="RecordRestorer"/> - shared with the LAN receiver.
    /// This class only adds the file and the header.
    /// </summary>
    public sealed class PackageReader : IDisposable
    {
        private FileStream _file;
        private SecureBlockReader _reader;
        private readonly RecordRestorer _restorer;
        private bool _disposed;

        public TransferManifest Manifest
        {
            get { return _restorer.Manifest; }
        }

        private PackageReader(FileStream file, SecureBlockReader reader, RecordRestorer restorer)
        {
            _file = file;
            _reader = reader;
            _restorer = restorer;
        }

        /// <summary>
        /// Opens a package and reads its header. Returns null with a reason when the
        /// passphrase is wrong or the file is not a package - a wrong password is an
        /// ordinary outcome, not an exception.
        /// </summary>
        public static PackageReader Open(string packagePath, string passphrase, out string error)
        {
            error = null;
            FileStream file = null;
            try
            {
                file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

                PackageCrypto.SessionKeys keys;
                if (!PackageCrypto.TryReadHeader(file, passphrase, out keys, out error))
                {
                    file.Dispose();
                    return null;
                }

                SecureBlockReader reader = new SecureBlockReader(file, keys);
                RecordRestorer restorer = new RecordRestorer(reader);
                restorer.ReadHeader();
                return new PackageReader(file, reader, restorer);
            }
            catch (SecureChannelException ex)
            {
                if (file != null) { file.Dispose(); }
                error = ex.Message;
                return null;
            }
            catch (IOException ex)
            {
                if (file != null) { file.Dispose(); }
                error = "Could not read the package: " + ex.Message;
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (file != null) { file.Dispose(); }
                error = "Access denied reading the package: " + ex.Message;
                return null;
            }
        }

        public TransferResult Restore(ITransferSink sink, ITransferObserver observer,
                                      CancellationToken cancellation, PauseGate gate)
        {
            return _restorer.Restore(sink, observer, cancellation, gate);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_reader != null) { _reader.Dispose(); _reader = null; }
            if (_file != null)
            {
                try { _file.Dispose(); } catch (IOException) { }
                _file = null;
            }
        }
    }
}
