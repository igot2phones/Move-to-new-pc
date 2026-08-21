using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MoveToNewPC.Core.Diagnostics
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// Rolling plain-text log, one line per event, UTC timestamps.
    /// Never write key material, the pairing code, or file contents through this class.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 8L * 1024 * 1024;
        private const int MaxGenerations = 3;

        private static readonly object Gate = new object();
        private static string _filePath;
        private static string _dataDirectory;
        private static StreamWriter _writer;
        private static long _written;
        private static LogLevel _minimum = LogLevel.Debug;
        private static bool _initialised;

        /// <summary>Directory the log and any settings/journals live in.</summary>
        public static string DataDirectory
        {
            get { lock (Gate) { EnsureInitialised(); return _dataDirectory; } }
        }

        public static string FilePath
        {
            get { lock (Gate) { EnsureInitialised(); return _filePath; } }
        }

        public static LogLevel MinimumLevel
        {
            get { return _minimum; }
            set { _minimum = value; }
        }

        /// <summary>
        /// Chooses the data directory: next to the EXE when that is writable (portable USB
        /// use), otherwise %LOCALAPPDATA%\MoveToNewPC. Safe to call more than once.
        /// </summary>
        public static void Initialise(string preferredDirectory)
        {
            lock (Gate)
            {
                if (_initialised)
                {
                    return;
                }

                _dataDirectory = ResolveWritableDirectory(preferredDirectory);
                _filePath = Path.Combine(_dataDirectory, "MoveToNewPC.log");
                _initialised = true;

                try
                {
                    RollIfNeededNoLock();
                    OpenNoLock();
                }
                catch (Exception)
                {
                    // A machine where we cannot log at all must still be usable.
                    _writer = null;
                }

                WriteLineNoLock(LogLevel.Info, "==== MoveToNewPC session start ====");
                WriteLineNoLock(LogLevel.Info, "Log file: " + _filePath);
            }
        }

        public static void Debug(string message) { Write(LogLevel.Debug, message); }
        public static void Info(string message) { Write(LogLevel.Info, message); }
        public static void Warn(string message) { Write(LogLevel.Warn, message); }
        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Error(string message, Exception ex)
        {
            if (ex == null)
            {
                Write(LogLevel.Error, message);
                return;
            }

            Write(LogLevel.Error, message + " | " + ex.GetType().Name + ": " + ex.Message);
            Write(LogLevel.Debug, "  " + OneLine(ex.StackTrace));
        }

        public static void Write(LogLevel level, string message)
        {
            if (level < _minimum)
            {
                return;
            }

            lock (Gate)
            {
                EnsureInitialised();
                WriteLineNoLock(level, message);
            }
        }

        public static void Flush()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    try { _writer.Flush(); }
                    catch (IOException) { }
                }
            }
        }

        public static void Close()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    try
                    {
                        WriteLineNoLock(LogLevel.Info, "==== MoveToNewPC session end ====");
                        _writer.Flush();
                        _writer.Dispose();
                    }
                    catch (Exception) { }
                    _writer = null;
                }
                _initialised = false;
            }
        }

        private static void EnsureInitialised()
        {
            if (!_initialised)
            {
                string exeDir = null;
                try
                {
                    exeDir = Path.GetDirectoryName(new Uri(typeof(Log).Assembly.CodeBase).LocalPath);
                }
                catch (Exception)
                {
                    exeDir = Environment.CurrentDirectory;
                }

                // Re-entrancy: Initialise takes the same non-reentrant-safe path, so inline it.
                _dataDirectory = ResolveWritableDirectory(exeDir);
                _filePath = Path.Combine(_dataDirectory, "MoveToNewPC.log");
                _initialised = true;
                try
                {
                    RollIfNeededNoLock();
                    OpenNoLock();
                }
                catch (Exception)
                {
                    _writer = null;
                }
            }
        }

        private static void OpenNoLock()
        {
            FileStream fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write,
                                           FileShare.ReadWrite | FileShare.Delete, 4096);
            _written = fs.Length;
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
            _writer.AutoFlush = true;
        }

        private static void RollIfNeededNoLock()
        {
            try
            {
                FileInfo fi = new FileInfo(_filePath);
                if (!fi.Exists || fi.Length < MaxBytes)
                {
                    return;
                }

                string oldest = _filePath + "." + MaxGenerations.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }

                for (int i = MaxGenerations - 1; i >= 1; i--)
                {
                    string from = _filePath + "." + i.ToString(CultureInfo.InvariantCulture);
                    string to = _filePath + "." + (i + 1).ToString(CultureInfo.InvariantCulture);
                    if (File.Exists(from))
                    {
                        if (File.Exists(to)) { File.Delete(to); }
                        File.Move(from, to);
                    }
                }

                File.Move(_filePath, _filePath + ".1");
            }
            catch (Exception)
            {
                // Rolling is best effort; never block startup on it.
            }
        }

        private static void WriteLineNoLock(LogLevel level, string message)
        {
            if (_writer == null)
            {
                return;
            }

            string line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                          + "Z " + LevelTag(level) + " ["
                          + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture)
                          + "] " + OneLine(message);

            try
            {
                _writer.WriteLine(line);
                _written += line.Length + 2;
                if (_written > MaxBytes)
                {
                    _writer.Flush();
                    _writer.Dispose();
                    _writer = null;
                    RollIfNeededNoLock();
                    OpenNoLock();
                }
            }
            catch (IOException)
            {
                _writer = null;
            }
            catch (ObjectDisposedException)
            {
                _writer = null;
            }
        }

        private static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DBG";
                case LogLevel.Info: return "INF";
                case LogLevel.Warn: return "WRN";
                default: return "ERR";
            }
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            return s.Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', '|');
        }

        private static string ResolveWritableDirectory(string preferred)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(preferred))
            {
                candidates.Add(preferred);
            }

            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    candidates.Add(Path.Combine(local, "MoveToNewPC"));
                }
            }
            catch (Exception) { }

            candidates.Add(Path.GetTempPath());

            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsWritable(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return Path.GetTempPath();
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string probe = Path.Combine(directory, "mtnpc-write-probe.tmp");
                using (FileStream fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 64,
                                                      FileOptions.DeleteOnClose))
                {
                    fs.WriteByte(0);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
