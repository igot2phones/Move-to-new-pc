using System;
using System.Globalization;
using System.IO;
using System.Text;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Selection;

namespace MoveToNewPC.Core.Manifests
{
    public enum ManifestRecordKind
    {
        None = 0,
        Directory,
        File,
        Skip,
        Totals
    }

    public sealed class ManifestRecord
    {
        public ManifestRecordKind Kind;
        public ManifestEntry File;
        public ManifestDirectory Directory;
        public SkippedItem Skip;
        public int SkipUserIndex;
        public int SkipRootIndex;
        public ManifestTotals Totals;
    }

    /// <summary>
    /// Streaming reader. Header records (H/U/R) are consumed up front and exposed through
    /// <see cref="Manifest"/>; everything after that is pulled one record at a time.
    ///
    /// A truncated final line (power cut during a scan) is discarded rather than treated as
    /// corruption: a partial manifest that stops early is still useful.
    /// </summary>
    public sealed class ManifestReader : IDisposable
    {
        private StreamReader _reader;
        private readonly TransferManifest _manifest = new TransferManifest();
        private string _pendingLine;

        public ManifestReader(string path)
        {
            FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            _reader = new StreamReader(stream, new UTF8Encoding(false), true);
            ReadHeaderSection(path);
        }

        public TransferManifest Manifest
        {
            get { return _manifest; }
        }

        /// <summary>Reads just the header and closes the file. Used to preview a package.</summary>
        public static TransferManifest ReadHeaderOnly(string path)
        {
            using (ManifestReader reader = new ManifestReader(path))
            {
                return reader.Manifest;
            }
        }

        private void ReadHeaderSection(string path)
        {
            string signature = _reader.ReadLine();
            if (signature == null)
            {
                throw new InvalidDataException("Manifest file is empty: " + path);
            }

            string[] sig = signature.Split('\t');
            if (sig.Length < 2 || !string.Equals(sig[0], "MTNPC-MANIFEST", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Not a MoveToNewPC manifest: " + path);
            }

            int version = ManifestText.ParseInt(sig[1]);
            if (version != TransferManifest.FormatVersion)
            {
                // Refuse rather than guess. A future version could mean anything.
                throw new InvalidDataException("Manifest format version " + version
                    + " is not supported by this build (expected "
                    + TransferManifest.FormatVersion.ToString(CultureInfo.InvariantCulture) + ").");
            }

            while (true)
            {
                string line = _reader.ReadLine();
                if (line == null)
                {
                    return;
                }
                if (line.Length == 0)
                {
                    continue;
                }

                char tag = line[0];
                if (tag != 'H' && tag != 'U' && tag != 'R')
                {
                    // First body record; hand it back on the next Read().
                    _pendingLine = line;
                    return;
                }

                string[] f = line.Split('\t');
                switch (tag)
                {
                    case 'H':
                        if (f.Length >= 5)
                        {
                            _manifest.ManifestId = ManifestText.Unescape(f[1]);
                            _manifest.CreatedUtc = ParseDate(f[2]);
                            _manifest.SourceMachine = ManifestText.Unescape(f[3]);
                            _manifest.ToolVersion = ManifestText.Unescape(f[4]);
                        }
                        break;

                    case 'U':
                        if (f.Length >= 6)
                        {
                            ManifestUser user = new ManifestUser();
                            user.UserIndex = ManifestText.ParseInt(f[1]);
                            user.Sid = ManifestText.Unescape(f[2]);
                            user.AccountName = ManifestText.Unescape(f[3]);
                            user.ProfilePath = ManifestText.Unescape(f[4]);
                            user.DestinationHint = ManifestText.Unescape(f[5]);
                            _manifest.Users.Add(user);
                        }
                        break;

                    case 'R':
                        if (f.Length >= 7)
                        {
                            ManifestRoot root = new ManifestRoot();
                            root.UserIndex = ManifestText.ParseInt(f[1]);
                            root.RootIndex = ManifestText.ParseInt(f[2]);
                            root.Tier = (SelectionTier)ManifestText.ParseInt(f[3]);
                            root.SourcePath = ManifestText.Unescape(f[4]);
                            root.DestinationRelativeRoot = ManifestText.Unescape(f[5]);
                            root.Label = ManifestText.Unescape(f[6]);

                            ManifestUser owner = _manifest.FindUser(root.UserIndex);
                            if (owner != null)
                            {
                                owner.Roots.Add(root);
                            }
                            else
                            {
                                Log.Warn("Manifest root references unknown user index " + root.UserIndex);
                            }
                        }
                        break;
                }
            }
        }

        public bool Read(ManifestRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException("record");
            }

            record.Kind = ManifestRecordKind.None;
            record.File = null;
            record.Directory = null;
            record.Skip = null;
            record.Totals = null;

            while (true)
            {
                string line = _pendingLine;
                _pendingLine = null;
                if (line == null)
                {
                    line = _reader.ReadLine();
                }
                if (line == null)
                {
                    return false;
                }
                if (line.Length == 0)
                {
                    continue;
                }

                string[] f = line.Split('\t');
                switch (line[0])
                {
                    case 'F':
                        if (f.Length < 10)
                        {
                            continue;   // truncated tail
                        }
                        ManifestEntry entry = new ManifestEntry();
                        entry.UserIndex = ManifestText.ParseInt(f[1]);
                        entry.RootIndex = ManifestText.ParseInt(f[2]);
                        entry.RelativePath = ManifestText.Unescape(f[3]);
                        entry.Length = ManifestText.ParseLong(f[4]);
                        entry.Attributes = ManifestText.ParseUInt(f[5]);
                        entry.CreationTimeUtc = ManifestText.ParseLong(f[6]);
                        entry.LastAccessTimeUtc = ManifestText.ParseLong(f[7]);
                        entry.LastWriteTimeUtc = ManifestText.ParseLong(f[8]);
                        entry.Sha256 = f[9].Length == 0 ? null : f[9];
                        record.Kind = ManifestRecordKind.File;
                        record.File = entry;
                        return true;

                    case 'D':
                        if (f.Length < 8)
                        {
                            continue;
                        }
                        ManifestDirectory directory = new ManifestDirectory();
                        directory.UserIndex = ManifestText.ParseInt(f[1]);
                        directory.RootIndex = ManifestText.ParseInt(f[2]);
                        directory.RelativePath = ManifestText.Unescape(f[3]);
                        directory.Attributes = ManifestText.ParseUInt(f[4]);
                        directory.CreationTimeUtc = ManifestText.ParseLong(f[5]);
                        directory.LastAccessTimeUtc = ManifestText.ParseLong(f[6]);
                        directory.LastWriteTimeUtc = ManifestText.ParseLong(f[7]);
                        record.Kind = ManifestRecordKind.Directory;
                        record.Directory = directory;
                        return true;

                    case 'S':
                        if (f.Length < 7)
                        {
                            continue;
                        }
                        record.SkipUserIndex = ManifestText.ParseInt(f[1]);
                        record.SkipRootIndex = ManifestText.ParseInt(f[2]);
                        record.Skip = new SkippedItem(
                            ManifestText.Unescape(f[3]),
                            false,
                            (SkipReason)ManifestText.ParseInt(f[4]),
                            ManifestText.Unescape(f[6]),
                            ManifestText.ParseLong(f[5]));
                        record.Kind = ManifestRecordKind.Skip;
                        return true;

                    case 'T':
                        if (f.Length < 6)
                        {
                            continue;
                        }
                        ManifestTotals totals = new ManifestTotals();
                        totals.FileCount = ManifestText.ParseLong(f[1]);
                        totals.ByteCount = ManifestText.ParseLong(f[2]);
                        totals.DirectoryCount = ManifestText.ParseLong(f[3]);
                        totals.SkippedCount = ManifestText.ParseLong(f[4]);
                        totals.SkippedBytes = ManifestText.ParseLong(f[5]);
                        _manifest.Totals = totals;
                        record.Kind = ManifestRecordKind.Totals;
                        record.Totals = totals;
                        return true;

                    default:
                        continue;
                }
            }
        }

        private static DateTime ParseDate(string value)
        {
            DateTime result;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result))
            {
                return result;
            }
            return DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
        }
    }
}
