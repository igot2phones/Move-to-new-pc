using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.Manifests
{
    /// <summary>
    /// Receiver-side record of what has already landed, so a dropped connection or a reboot
    /// resumes instead of restarting. Append-only and flushed after every entry: a journal
    /// that is only correct when the process exits cleanly would be useless for the exact
    /// case it exists to handle.
    /// </summary>
    public sealed class CompletionJournal : IDisposable
    {
        public const string FileExtension = ".mtnpc-journal";

        private readonly HashSet<string> _completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private StreamWriter _writer;
        private long _completedBytes;

        private CompletionJournal(string path)
        {
            _path = path;
        }

        public string Path
        {
            get { return _path; }
        }

        public int CompletedCount
        {
            get { return _completed.Count; }
        }

        public long CompletedBytes
        {
            get { return _completedBytes; }
        }

        /// <summary>
        /// Opens the journal for a manifest, replaying any prior run. A journal belonging to
        /// a different manifest is discarded rather than half-applied.
        /// </summary>
        public static CompletionJournal OpenOrCreate(string path, string manifestId)
        {
            CompletionJournal journal = new CompletionJournal(path);
            bool reusable = false;

            try
            {
                if (File.Exists(path))
                {
                    reusable = journal.Replay(path, manifestId);
                    if (!reusable)
                    {
                        Log.Warn("Existing journal belongs to a different transfer; starting a fresh one: " + path);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read journal " + path + "; starting a fresh one", ex);
                reusable = false;
            }

            try
            {
                FileStream stream = new FileStream(path, reusable ? FileMode.Append : FileMode.Create,
                                                   FileAccess.Write, FileShare.Read, 8192);
                journal._writer = new StreamWriter(stream, new UTF8Encoding(false));
                journal._writer.NewLine = "\n";
                journal._writer.AutoFlush = true;

                if (!reusable)
                {
                    journal._completed.Clear();
                    journal._completedBytes = 0;
                    journal._writer.Write("MTNPC-JOURNAL\t1\n");
                    journal._writer.Write("M\t" + ManifestText.Escape(manifestId ?? string.Empty) + "\n");
                }
                else
                {
                    Log.Info("Resuming: journal already records " + journal._completed.Count
                             + " completed files (" + journal._completedBytes + " bytes).");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not open journal for writing: " + path, ex);
                journal._writer = null;
            }

            return journal;
        }

        private bool Replay(string path, string manifestId)
        {
            bool idMatched = false;

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), true))
            {
                string signature = reader.ReadLine();
                if (signature == null || !signature.StartsWith("MTNPC-JOURNAL", StringComparison.Ordinal))
                {
                    return false;
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] f = line.Split('\t');
                    switch (line[0])
                    {
                        case 'M':
                            if (f.Length >= 2)
                            {
                                string recorded = ManifestText.Unescape(f[1]);
                                idMatched = string.Equals(recorded, manifestId, StringComparison.Ordinal);
                                if (!idMatched)
                                {
                                    return false;
                                }
                            }
                            break;

                        case 'C':
                            if (f.Length >= 5)
                            {
                                _completed.Add(MakeKey(ManifestText.ParseInt(f[1]),
                                                       ManifestText.ParseInt(f[2]),
                                                       ManifestText.Unescape(f[3])));
                                _completedBytes += ManifestText.ParseLong(f[4]);
                            }
                            break;

                        case 'X':
                            // Recorded for the report only. Failed items ARE retried on a
                            // later run - the journal is not a blocklist.
                            break;
                    }
                }
            }

            return idMatched;
        }

        public bool IsComplete(int userIndex, int rootIndex, string relativePath)
        {
            return _completed.Contains(MakeKey(userIndex, rootIndex, relativePath));
        }

        public void MarkComplete(int userIndex, int rootIndex, string relativePath, long bytes, string sha256)
        {
            string key = MakeKey(userIndex, rootIndex, relativePath);
            if (!_completed.Add(key))
            {
                return;
            }
            _completedBytes += bytes;

            if (_writer == null)
            {
                return;
            }
            try
            {
                _writer.Write("C\t");
                _writer.Write(userIndex.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(rootIndex.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(ManifestText.Escape(relativePath));
                _writer.Write('\t');
                _writer.Write(bytes.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(sha256 ?? string.Empty);
                _writer.Write('\n');
            }
            catch (IOException ex)
            {
                Log.Warn("Journal write failed: " + ex.Message);
            }
        }

        public void MarkFailed(int userIndex, int rootIndex, string relativePath, SkipReason reason, string detail)
        {
            if (_writer == null)
            {
                return;
            }
            try
            {
                _writer.Write("X\t");
                _writer.Write(userIndex.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(rootIndex.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(ManifestText.Escape(relativePath));
                _writer.Write('\t');
                _writer.Write(((int)reason).ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(ManifestText.Escape(detail));
                _writer.Write('\n');
            }
            catch (IOException ex)
            {
                Log.Warn("Journal write failed: " + ex.Message);
            }
        }

        private static string MakeKey(int userIndex, int rootIndex, string relativePath)
        {
            return userIndex.ToString(CultureInfo.InvariantCulture) + "/"
                   + rootIndex.ToString(CultureInfo.InvariantCulture) + "/"
                   + relativePath;
        }

        public void Dispose()
        {
            if (_writer != null)
            {
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
}
