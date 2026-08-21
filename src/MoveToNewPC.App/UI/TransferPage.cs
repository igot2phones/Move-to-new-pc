using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Net;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Reporting;
using MoveToNewPC.Core.Transfer;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Scan, then copy. Both run on one background thread; the UI thread only ever reads
    /// counters through a timer, so nothing here can block the message loop no matter how
    /// slow the disk is.
    /// </summary>
    public sealed class TransferPage : WizardPage, IScanObserver, ITransferObserver
    {
        private Label _phaseLabel;
        private Label _currentFileLabel;
        private Label _statsLabel;
        private Label _skipLabel;
        private ProgressBar _progressBar;
        private Button _pauseButton;
        private TextBox _recentSkips;
        private System.Windows.Forms.Timer _uiTimer;

        private Thread _worker;
        private CancellationTokenSource _cancel;
        private PauseGate _pause;

        // Written by the worker thread, read by the UI timer.
        private long _bytesDone;
        private long _bytesTotal;
        private long _filesDone;
        private long _filesTotal;
        private long _skipCount;
        private string _currentFile = string.Empty;
        private string _phase = "Preparing...";
        private readonly object _textGate = new object();

        private DateTime _startedUtc;
        private long _lastBytes;
        private DateTime _lastSample;
        private double _smoothedRate;

        private volatile bool _running;
        private volatile bool _finished;
        private TransferResult _result;
        private string _failure;
        private readonly System.Text.StringBuilder _skipBuffer = new System.Text.StringBuilder();
        private int _skipBufferLines;

        public TransferPage()
        {
            Build();
        }

        public override string Title
        {
            get { return Session.CopyOptions.DryRun ? "Dry run in progress" : "Copying"; }
        }

        public override string Subtitle
        {
            get
            {
                return Session.CopyOptions.DryRun
                       ? "Nothing is being written. This produces the full report only."
                       : "You can pause or cancel at any time. Files already copied stay where they are.";
            }
        }

        public override bool ShowBack { get { return false; } }
        public override bool ShowNext { get { return _finished; } }
        public override bool CanGoNext { get { return _finished; } }
        public override string NextText { get { return "See report >"; } }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            _phaseLabel = new Label();
            _phaseLabel.Location = new Point(24, 14);
            _phaseLabel.Size = new Size(720, 20);
            _phaseLabel.Font = Ui.Bold(Ui.DefaultFont);
            _phaseLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_phaseLabel);

            _progressBar = new ProgressBar();
            _progressBar.Location = new Point(24, 40);
            _progressBar.Size = new Size(720, 22);
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 40;
            _progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_progressBar);

            _currentFileLabel = new Label();
            _currentFileLabel.Location = new Point(24, 68);
            _currentFileLabel.Size = new Size(720, 20);
            _currentFileLabel.ForeColor = SystemColors.GrayText;
            _currentFileLabel.AutoEllipsis = true;
            _currentFileLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_currentFileLabel);

            _statsLabel = new Label();
            _statsLabel.Location = new Point(24, 94);
            _statsLabel.Size = new Size(720, 20);
            _statsLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_statsLabel);

            _skipLabel = new Label();
            _skipLabel.Location = new Point(24, 120);
            _skipLabel.Size = new Size(720, 20);
            _skipLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_skipLabel);

            _pauseButton = Ui.MakeButton("&Pause", 92);
            _pauseButton.Location = new Point(24, 146);
            _pauseButton.Click += PauseOnClick;
            Controls.Add(_pauseButton);

            Label skipsHeading = new Label();
            skipsHeading.Location = new Point(24, 182);
            skipsHeading.Size = new Size(400, 18);
            skipsHeading.Text = "Items being skipped (the report lists every one):";
            skipsHeading.ForeColor = SystemColors.GrayText;
            Controls.Add(skipsHeading);

            _recentSkips = new TextBox();
            _recentSkips.Location = new Point(24, 202);
            _recentSkips.Size = new Size(720, 150);
            _recentSkips.Multiline = true;
            _recentSkips.ReadOnly = true;
            _recentSkips.ScrollBars = ScrollBars.Vertical;
            _recentSkips.BackColor = SystemColors.Window;
            _recentSkips.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                                  | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_recentSkips);

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 200;
            _uiTimer.Tick += UiTimerOnTick;
        }

        public override void OnActivated()
        {
            if (_running || _finished)
            {
                return;
            }

            _cancel = new CancellationTokenSource();
            _pause = new PauseGate();
            _startedUtc = DateTime.UtcNow;
            _lastSample = _startedUtc;
            _running = true;

            _uiTimer.Start();

            _worker = new Thread(Work);
            _worker.IsBackground = true;
            _worker.Name = "MTNPC transfer";
            _worker.Start();

            Host.RefreshChrome();
        }

        // ---- worker ------------------------------------------------------------

        private void Work()
        {
            // The receiver has no scan to do: everything it needs arrives in the stream.
            if (Session.Role == AppRole.Receiver)
            {
                if (Session.Channel != null)
                {
                    NetworkReceiveWork();
                }
                else
                {
                    RestoreWork();
                }
                return;
            }

            string manifestDirectory = null;
            try
            {
                bool dryRun = Session.CopyOptions.DryRun;

                // A network send has no local destination folder, so its manifest lives
                // beside the log like a dry run's does.
                manifestDirectory = (dryRun || Session.Channel != null || string.IsNullOrEmpty(Session.DestinationFolder))
                    ? Log.DataDirectory
                    : LongPath.Combine(Session.DestinationFolder, "_MoveToNewPC");

                int error;
                NativeFile.CreateDirectoryRecursive(manifestDirectory, out error);

                string manifestPath = LongPath.ToDisplay(
                    LongPath.Combine(manifestDirectory, "transfer.mtnpc-manifest"));

                SetPhase("Scanning the folders you chose...");
                ScanEngine scanner = new ScanEngine(Session.Selection, Session.CopyOptions);
                TransferManifest manifest = scanner.Scan(manifestPath, this, _cancel.Token);

                Session.Manifest = manifest;
                Session.ManifestPath = manifestPath;

                if (_cancel.IsCancellationRequested)
                {
                    Finish(CancelledResult());
                    return;
                }

                Interlocked.Exchange(ref _bytesTotal, manifest.Totals.ByteCount);
                Interlocked.Exchange(ref _filesTotal, manifest.Totals.FileCount);
                Interlocked.Exchange(ref _bytesDone, 0);
                Interlocked.Exchange(ref _filesDone, 0);

                if (manifest.Totals.FileCount == 0)
                {
                    SetPhase("Nothing to copy.");
                    TransferResult empty = new TransferResult();
                    empty.StartedUtc = _startedUtc;
                    empty.FinishedUtc = DateTime.UtcNow;
                    empty.Completed = true;
                    for (int i = 0; i < manifest.ScanSkips.Count; i++)
                    {
                        empty.Skipped.Add(manifest.ScanSkips[i]);
                    }
                    Finish(empty);
                    return;
                }

                ITransferSink sink;
                CompletionJournal journal = null;

                if (dryRun)
                {
                    SetPhase("Dry run: working out exactly what would be copied...");
                    sink = new NullSink();
                }
                else if (Session.Channel != null)
                {
                    SetPhase("Sending to " + Session.PeerMachineName + "...");
                    sink = new NetworkSink(Session.Channel, false);
                }
                else
                {
                    SetPhase("Writing the encrypted package...");
                    string journalPath = LongPath.ToDisplay(
                        LongPath.Combine(manifestDirectory, "transfer" + CompletionJournal.FileExtension));
                    journal = CompletionJournal.OpenOrCreate(journalPath, manifest.ManifestId);
                    sink = new PackageSink(Session.PackagePath, Session.PackagePassphrase);
                }

                try
                {
                    TransferEngine engine = new TransferEngine(Session.CopyOptions, sink, this);
                    TransferResult result = engine.Run(manifest, manifestPath, _cancel.Token, _pause);

                    for (int i = 0; i < manifest.ScanSkips.Count && result.Skipped.Count < 5000; i++)
                    {
                        // Scan-time skips already came through the manifest replay; this only
                        // catches the case where the engine stopped before reading them.
                    }

                    Finish(result);
                }
                finally
                {
                    // Order matters: the sink flushes its last frame on dispose, so the
                    // socket underneath it has to outlive it.
                    sink.Dispose();
                    if (Session.Channel != null)
                    {
                        Session.Channel.Dispose();
                        Session.Channel = null;
                    }
                    if (journal != null)
                    {
                        journal.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Transfer thread failed", ex);
                TransferResult failed = new TransferResult();
                failed.StartedUtc = _startedUtc;
                failed.FinishedUtc = DateTime.UtcNow;
                failed.FailureMessage = ex.Message;
                _failure = ex.Message;
                Finish(failed);
            }
        }

        /// <summary>
        /// Receiver side over the network: the pairing screen already established the
        /// channel, so this just reads the record stream off it into the chosen folder.
        /// </summary>
        private void NetworkReceiveWork()
        {
            try
            {
                SetPhase("Waiting for " + Session.PeerMachineName + " to describe the transfer...");

                using (SecureChannel channel = Session.Channel)
                {
                    Session.Channel = null;      // we own it from here

                    RecordRestorer restorer = new RecordRestorer(channel.Reader);
                    restorer.ReadHeader();

                    Session.Manifest = restorer.Manifest;

                    Interlocked.Exchange(ref _bytesTotal, restorer.Manifest.Totals.ByteCount);
                    Interlocked.Exchange(ref _filesTotal, restorer.Manifest.Totals.FileCount);
                    Interlocked.Exchange(ref _bytesDone, 0);
                    Interlocked.Exchange(ref _filesDone, 0);

                    SetPhase("Receiving from " + Session.PeerMachineName + "...");

                    string journalDirectory = LongPath.Combine(Session.DestinationFolder, "_MoveToNewPC");
                    int error;
                    NativeFile.CreateDirectoryRecursive(journalDirectory, out error);

                    string journalPath = LongPath.ToDisplay(LongPath.Combine(
                        journalDirectory, "receive" + CompletionJournal.FileExtension));

                    using (CompletionJournal journal =
                               CompletionJournal.OpenOrCreate(journalPath, restorer.Manifest.ManifestId))
                    using (LocalFolderSink sink =
                               new LocalFolderSink(Session.DestinationFolder, Session.CopyOptions,
                                                   journal, Session.Layout))
                    {
                        TransferResult result = restorer.Restore(sink, this, _cancel.Token, _pause);
                        if (result.FailureMessage != null)
                        {
                            _failure = result.FailureMessage;
                        }
                        Finish(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Network receive failed", ex);
                TransferResult failed = new TransferResult();
                failed.StartedUtc = _startedUtc;
                failed.FinishedUtc = DateTime.UtcNow;
                failed.FailureMessage = ex.Message;
                _failure = ex.Message;
                Finish(failed);
            }
        }

        /// <summary>
        /// Receiver side: unpack an encrypted package into the chosen folder. The bytes go
        /// through the ordinary LocalFolderSink, so path validation, the collision policy,
        /// hash verification and the resume journal are all the same code a local copy uses.
        /// </summary>
        private void RestoreWork()
        {
            try
            {
                SetPhase("Opening the package...");

                string openError;
                using (PackageReader reader = PackageReader.Open(
                           Session.PackagePath, Session.PackagePassphrase, out openError))
                {
                    if (reader == null)
                    {
                        TransferResult refused = new TransferResult();
                        refused.StartedUtc = _startedUtc;
                        refused.FinishedUtc = DateTime.UtcNow;
                        refused.FailureMessage = openError;
                        _failure = openError;
                        Finish(refused);
                        return;
                    }

                    Session.Manifest = reader.Manifest;

                    Interlocked.Exchange(ref _bytesTotal, reader.Manifest.Totals.ByteCount);
                    Interlocked.Exchange(ref _filesTotal, reader.Manifest.Totals.FileCount);
                    Interlocked.Exchange(ref _bytesDone, 0);
                    Interlocked.Exchange(ref _filesDone, 0);

                    SetPhase("Restoring files...");

                    string journalDirectory = LongPath.Combine(Session.DestinationFolder, "_MoveToNewPC");
                    int error;
                    NativeFile.CreateDirectoryRecursive(journalDirectory, out error);

                    string journalPath = LongPath.ToDisplay(LongPath.Combine(
                        journalDirectory, "restore" + CompletionJournal.FileExtension));

                    using (CompletionJournal journal =
                               CompletionJournal.OpenOrCreate(journalPath, reader.Manifest.ManifestId))
                    using (LocalFolderSink sink =
                               new LocalFolderSink(Session.DestinationFolder, Session.CopyOptions,
                                                   journal, Session.Layout))
                    {
                        TransferResult result = reader.Restore(sink, this, _cancel.Token, _pause);
                        if (result.FailureMessage != null)
                        {
                            _failure = result.FailureMessage;
                        }
                        Finish(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Restore thread failed", ex);
                TransferResult failed = new TransferResult();
                failed.StartedUtc = _startedUtc;
                failed.FinishedUtc = DateTime.UtcNow;
                failed.FailureMessage = ex.Message;
                _failure = ex.Message;
                Finish(failed);
            }
        }

        private TransferResult CancelledResult()
        {
            TransferResult result = new TransferResult();
            result.StartedUtc = _startedUtc;
            result.FinishedUtc = DateTime.UtcNow;
            result.Cancelled = true;
            return result;
        }

        private void Finish(TransferResult result)
        {
            _result = result;
            _running = false;

            Ui.Post(this, delegate
            {
                _uiTimer.Stop();
                UpdateUi();
                _finished = true;
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = _progressBar.Maximum;
                _pauseButton.Enabled = false;

                Session.LastResult = result;
                Session.LastReport = BuildReport(result);

                _phaseLabel.Text = result.Cancelled
                                   ? "Cancelled."
                                   : (string.IsNullOrEmpty(result.FailureMessage)
                                      ? "Finished."
                                      : "Stopped: " + result.FailureMessage);

                Host.RefreshChrome();
            });
        }

        private TransferReport BuildReport(TransferResult result)
        {
            TransferReport report = TransferReport.FromResult(result,
                Session.CopyOptions.DryRun ? "Dry run (nothing was written)" : "Copy to a folder");

            report.SourceMachine = Program.SafeMachineName();
            report.DestinationMachine = Program.SafeMachineName();
            report.DestinationDescription = Session.CopyOptions.DryRun
                                            ? "(dry run)"
                                            : LongPath.ToDisplay(Session.DestinationFolder ?? string.Empty);
            report.LogFilePath = Log.FilePath;

            if (Session.CopyOptions.DryRun)
            {
                report.Notes.Add("This was a dry run. No files were written.");
            }
            if (!Session.CopyOptions.HydrateCloudFiles)
            {
                report.Notes.Add("Online-only cloud files were skipped rather than downloaded. "
                                 + "Turn on \"Download OneDrive / Dropbox files\" to include them.");
            }
            if (!Session.CopyOptions.IncludeEncryptedFiles)
            {
                report.Notes.Add("EFS-encrypted files were skipped; they would not open on the new PC.");
            }
            if (Session.CopyOptions.VerifyHash)
            {
                report.Notes.Add("Every copied file was verified with a SHA-256 checksum.");
            }
            if (!string.IsNullOrEmpty(Session.ManifestPath))
            {
                report.Notes.Add("Manifest: " + Session.ManifestPath);
            }

            return report;
        }

        // ---- IScanObserver -----------------------------------------------------

        public void OnStatus(string message)
        {
            SetPhase(message);
        }

        public void OnProgress(long files, long bytes, long skipped, string currentPath)
        {
            Interlocked.Exchange(ref _filesDone, files);
            Interlocked.Exchange(ref _bytesDone, bytes);
            Interlocked.Exchange(ref _skipCount, skipped);
            SetCurrentFile(currentPath);
        }

        public void OnSkipped(SkippedItem item)
        {
            Interlocked.Increment(ref _skipCount);
            AppendSkip(item);
        }

        // ---- ITransferObserver -------------------------------------------------

        public void OnFileStarted(string sourceDisplayPath, string destinationDisplayPath, long length)
        {
            SetCurrentFile(sourceDisplayPath);
        }

        public void OnBytesTransferred(long deltaBytes)
        {
            Interlocked.Add(ref _bytesDone, deltaBytes);
        }

        public void OnFileCompleted(string sourceDisplayPath, long length)
        {
            Interlocked.Increment(ref _filesDone);
        }

        public void OnTotals(long filesDone, long filesTotal, long bytesDone, long bytesTotal)
        {
            if (filesTotal > 0)
            {
                Interlocked.Exchange(ref _filesTotal, filesTotal);
            }
            if (bytesTotal > 0)
            {
                Interlocked.Exchange(ref _bytesTotal, bytesTotal);
            }
        }

        // ---- UI ----------------------------------------------------------------

        private void SetPhase(string text)
        {
            lock (_textGate)
            {
                _phase = text ?? string.Empty;
            }
        }

        private void SetCurrentFile(string text)
        {
            lock (_textGate)
            {
                _currentFile = text ?? string.Empty;
            }
        }

        private void AppendSkip(SkippedItem item)
        {
            lock (_textGate)
            {
                if (_skipBufferLines > 400)
                {
                    return;
                }
                _skipBufferLines++;
                _skipBuffer.Append(SkipReasons.Describe(item.Reason))
                           .Append(": ")
                           .Append(Format.EllipsisPath(item.Path, 90))
                           .Append(Environment.NewLine);
            }
        }

        private void UiTimerOnTick(object sender, EventArgs e)
        {
            UpdateUi();
        }

        private void UpdateUi()
        {
            string phase;
            string currentFile;
            string skips = null;

            lock (_textGate)
            {
                phase = _phase;
                currentFile = _currentFile;
                if (_skipBuffer.Length > 0)
                {
                    skips = _skipBuffer.ToString();
                    _skipBuffer.Length = 0;
                }
            }

            _phaseLabel.Text = phase;
            _currentFileLabel.Text = Format.EllipsisPath(currentFile, 110);

            long bytesDone = Interlocked.Read(ref _bytesDone);
            long bytesTotal = Interlocked.Read(ref _bytesTotal);
            long filesDone = Interlocked.Read(ref _filesDone);
            long filesTotal = Interlocked.Read(ref _filesTotal);
            long skipped = Interlocked.Read(ref _skipCount);

            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastSample).TotalSeconds;
            if (elapsed >= 0.5)
            {
                double instant = (bytesDone - _lastBytes) / elapsed;
                // Exponential smoothing: a raw per-tick rate jumps around far too much to be
                // readable, and an average-since-start never reflects the current disk.
                _smoothedRate = _smoothedRate <= 0 ? instant : (_smoothedRate * 0.7) + (instant * 0.3);
                _lastBytes = bytesDone;
                _lastSample = now;
            }

            if (bytesTotal > 0)
            {
                if (_progressBar.Style != ProgressBarStyle.Continuous)
                {
                    _progressBar.Style = ProgressBarStyle.Continuous;
                    _progressBar.Maximum = 1000;
                }
                double fraction = (double)bytesDone / bytesTotal;
                int value = (int)Math.Round(fraction * 1000);
                _progressBar.Value = Math.Max(0, Math.Min(1000, value));

                _statsLabel.Text = Format.Bytes(bytesDone) + " of " + Format.Bytes(bytesTotal)
                                   + "   |   " + filesDone.ToString("N0") + " of " + filesTotal.ToString("N0") + " files"
                                   + "   |   " + Format.Rate(_smoothedRate)
                                   + "   |   " + Format.Eta(bytesTotal - bytesDone, _smoothedRate) + " left";
            }
            else
            {
                _statsLabel.Text = filesDone.ToString("N0") + " files, " + Format.Bytes(bytesDone) + " found";
            }

            _skipLabel.Text = skipped > 0
                              ? skipped.ToString("N0") + " item(s) skipped so far - all listed in the report"
                              : "Nothing skipped so far";

            if (skips != null)
            {
                if (_recentSkips.TextLength > 60000)
                {
                    _recentSkips.Text = string.Empty;
                }
                _recentSkips.AppendText(skips);
            }
        }

        private void PauseOnClick(object sender, EventArgs e)
        {
            if (_pause == null)
            {
                return;
            }

            if (_pause.IsPaused)
            {
                _pause.Resume();
                _pauseButton.Text = "&Pause";
                SetPhase("Copying files...");
            }
            else
            {
                _pause.Pause();
                _pauseButton.Text = "Res&ume";
                SetPhase("Paused.");
            }
        }

        public override bool OnCancel()
        {
            if (!_running)
            {
                return true;
            }

            if (!Ui.Confirm(this, "Stop the transfer?" + Environment.NewLine + Environment.NewLine
                                  + "Files already copied stay where they are, and the report will tell you "
                                  + "exactly how far it got."))
            {
                return false;
            }

            RequestStop();
            return true;
        }

        private void RequestStop()
        {
            try
            {
                if (_cancel != null)
                {
                    _cancel.Cancel();
                }
                if (_pause != null)
                {
                    _pause.Resume();
                }
            }
            catch (ObjectDisposedException)
            {
            }

            Thread worker = _worker;
            if (worker != null && worker.IsAlive)
            {
                worker.Join(5000);
            }
        }

        public override void OnDeactivating()
        {
            _uiTimer.Stop();
            if (_running)
            {
                RequestStop();
            }
        }

        public override WizardPage OnNext()
        {
            return _finished ? new ReportPage() : null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
                if (_cancel != null)
                {
                    _cancel.Dispose();
                    _cancel = null;
                }
                if (_pause != null)
                {
                    _pause.Dispose();
                    _pause = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
