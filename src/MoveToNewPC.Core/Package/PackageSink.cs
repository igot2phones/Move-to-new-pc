using System;
using System.IO;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.Core.Package
{
    /// <summary>
    /// Writes the whole transfer into one encrypted file to carry across on a USB disk or
    /// leave on a shared folder.
    ///
    /// The record stream itself is <see cref="RecordSink"/> - the same one the LAN sender
    /// uses. This class only adds the file, the header and the part-file discipline.
    /// </summary>
    public sealed class PackageSink : ITransferSink
    {
        private readonly string _packagePath;
        private readonly string _partPath;
        private FileStream _file;
        private SecureBlockWriter _writer;
        private RecordSink _records;
        private bool _completed;
        private bool _disposed;

        public const string FileExtension = ".mtnpc-package";

        public PackageSink(string packagePath, string passphrase)
        {
            if (string.IsNullOrEmpty(packagePath))
            {
                throw new ArgumentException("A package path is required.", "packagePath");
            }

            _packagePath = packagePath;
            // Same discipline as a file copy: write to a .mtnpc-part and rename only on
            // success, so an interrupted run never leaves a package that looks complete.
            _partPath = packagePath + ".mtnpc-part";

            byte[] salt = PackageCrypto.RandomBytes(PackageCrypto.SaltBytes);
            PackageCrypto.SessionKeys keys =
                PackageCrypto.DeriveKeys(passphrase, salt, PackageCrypto.DefaultIterations);

            _file = new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            PackageCrypto.WriteHeader(_file, salt, PackageCrypto.DefaultIterations, keys.MacKey);
            _writer = new SecureBlockWriter(_file, keys);
            _records = new RecordSink(_writer, packagePath);

            Log.Info("Writing encrypted package: " + packagePath);
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
            _completed = _records.Finished;
        }

        /// <summary>Free space on the volume holding the package.</summary>
        public long GetAvailableBytes()
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(_packagePath);
                if (string.IsNullOrEmpty(root))
                {
                    return -1;
                }
                DriveInfo drive = new DriveInfo(root);
                return drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not determine free space for the package: " + ex.Message);
                return -1;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _records = null;

            if (_writer != null)
            {
                try { _writer.Dispose(); }
                catch (Exception ex) { Log.Warn("Closing the package writer failed: " + ex.Message); }
                _writer = null;
            }
            if (_file != null)
            {
                try { _file.Dispose(); }
                catch (IOException) { }
                _file = null;
            }

            if (_completed)
            {
                try
                {
                    if (File.Exists(_packagePath))
                    {
                        File.Delete(_packagePath);
                    }
                    File.Move(_partPath, _packagePath);
                    Log.Info("Package written: " + _packagePath);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not finalise the package: " + ex.Message);
                }
            }
            else
            {
                // Never leave a half-written package looking like a usable one.
                try
                {
                    if (File.Exists(_partPath))
                    {
                        File.Delete(_partPath);
                        Log.Info("Removed the incomplete package part file.");
                    }
                }
                catch (IOException) { }
            }
        }
    }
}
