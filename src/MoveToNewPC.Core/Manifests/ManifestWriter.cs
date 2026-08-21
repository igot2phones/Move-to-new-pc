using System;
using System.Globalization;
using System.IO;
using System.Text;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.Manifests
{
    /// <summary>
    /// Streams a manifest to disk as it is produced. Never holds the entry list in memory:
    /// the whole point of the format is that a scan of millions of files costs a constant
    /// amount of RAM.
    /// </summary>
    public sealed class ManifestWriter : IDisposable
    {
        private StreamWriter _writer;
        private bool _totalsWritten;

        public ManifestWriter(string path, TransferManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }

            FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _writer.NewLine = "\n";

            WriteHeader(manifest);
        }

        private void WriteHeader(TransferManifest manifest)
        {
            _writer.Write("MTNPC-MANIFEST\t");
            _writer.Write(TransferManifest.FormatVersion.ToString(CultureInfo.InvariantCulture));
            _writer.Write('\n');

            Row("H",
                ManifestText.Escape(manifest.ManifestId),
                manifest.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ManifestText.Escape(manifest.SourceMachine),
                ManifestText.Escape(manifest.ToolVersion));

            for (int u = 0; u < manifest.Users.Count; u++)
            {
                ManifestUser user = manifest.Users[u];
                Row("U",
                    ManifestText.L(user.UserIndex),
                    ManifestText.Escape(user.Sid),
                    ManifestText.Escape(user.AccountName),
                    ManifestText.Escape(user.ProfilePath),
                    ManifestText.Escape(user.DestinationHint));

                for (int r = 0; r < user.Roots.Count; r++)
                {
                    ManifestRoot root = user.Roots[r];
                    Row("R",
                        ManifestText.L(root.UserIndex),
                        ManifestText.L(root.RootIndex),
                        ManifestText.L((long)root.Tier),
                        ManifestText.Escape(root.SourcePath),
                        ManifestText.Escape(root.DestinationRelativeRoot),
                        ManifestText.Escape(root.Label));
                }
            }
        }

        public void WriteDirectory(ManifestDirectory directory)
        {
            Row("D",
                ManifestText.L(directory.UserIndex),
                ManifestText.L(directory.RootIndex),
                ManifestText.Escape(directory.RelativePath),
                ManifestText.U(directory.Attributes),
                ManifestText.L(directory.CreationTimeUtc),
                ManifestText.L(directory.LastAccessTimeUtc),
                ManifestText.L(directory.LastWriteTimeUtc));
        }

        public void WriteFile(ManifestEntry entry)
        {
            Row("F",
                ManifestText.L(entry.UserIndex),
                ManifestText.L(entry.RootIndex),
                ManifestText.Escape(entry.RelativePath),
                ManifestText.L(entry.Length),
                ManifestText.U(entry.Attributes),
                ManifestText.L(entry.CreationTimeUtc),
                ManifestText.L(entry.LastAccessTimeUtc),
                ManifestText.L(entry.LastWriteTimeUtc),
                entry.Sha256 ?? string.Empty);
        }

        public void WriteSkip(int userIndex, int rootIndex, string relativePath, SkipReason reason,
                              long length, string detail)
        {
            Row("S",
                ManifestText.L(userIndex),
                ManifestText.L(rootIndex),
                ManifestText.Escape(relativePath),
                ManifestText.L((long)reason),
                ManifestText.L(length),
                ManifestText.Escape(detail));
        }

        public void WriteTotals(ManifestTotals totals)
        {
            Row("T",
                ManifestText.L(totals.FileCount),
                ManifestText.L(totals.ByteCount),
                ManifestText.L(totals.DirectoryCount),
                ManifestText.L(totals.SkippedCount),
                ManifestText.L(totals.SkippedBytes));
            _totalsWritten = true;
            _writer.Flush();
        }

        private void Row(string tag, params string[] fields)
        {
            _writer.Write(tag);
            for (int i = 0; i < fields.Length; i++)
            {
                _writer.Write('\t');
                _writer.Write(fields[i]);
            }
            _writer.Write('\n');
        }

        public bool TotalsWritten
        {
            get { return _totalsWritten; }
        }

        public void Flush()
        {
            if (_writer != null)
            {
                _writer.Flush();
            }
        }

        public void Dispose()
        {
            if (_writer == null)
            {
                return;
            }
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
            _writer = null;
        }
    }
}
