using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Reporting;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// The last screen. Shows the whole report, saves it to disk, and gives the operator
    /// one-click access to the log - because that log is what gets sent when something went
    /// wrong.
    /// </summary>
    public sealed class ReportPage : WizardPage
    {
        private TextBox _reportBox;
        private Label _headline;
        private Button _saveButton;
        private Button _openFolderButton;
        private Button _copyLogButton;
        private Button _startOverButton;

        public ReportPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Report"; }
        }

        public override string Subtitle
        {
            get { return "Keep this. It lists everything that was copied and everything that was not."; }
        }

        public override bool ShowNext { get { return false; } }
        public override bool ShowBack { get { return false; } }
        public override string CancelText { get { return "Close"; } }

        private void Build()
        {
            Padding = new Padding(16, 12, 16, 8);

            _headline = new Label();
            _headline.Dock = DockStyle.Top;
            _headline.Height = 26;
            _headline.Font = Ui.Heading(Ui.DefaultFont);
            Controls.Add(_headline);

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 40;

            _saveButton = Ui.MakeButton("&Save report...", 120);
            _saveButton.Location = new Point(0, 8);
            _saveButton.Click += SaveOnClick;

            _openFolderButton = Ui.MakeButton("&Open log folder", 120);
            _openFolderButton.Location = new Point(128, 8);
            _openFolderButton.Click += OpenFolderOnClick;

            _copyLogButton = Ui.MakeButton("&Copy log path", 120);
            _copyLogButton.Location = new Point(256, 8);
            _copyLogButton.Click += CopyLogOnClick;

            _startOverButton = Ui.MakeButton("Start &over", 120);
            _startOverButton.Location = new Point(384, 8);
            _startOverButton.Click += StartOverOnClick;

            buttons.Controls.Add(_saveButton);
            buttons.Controls.Add(_openFolderButton);
            buttons.Controls.Add(_copyLogButton);
            buttons.Controls.Add(_startOverButton);

            _reportBox = new TextBox();
            _reportBox.Dock = DockStyle.Fill;
            _reportBox.Multiline = true;
            _reportBox.ReadOnly = true;
            _reportBox.ScrollBars = ScrollBars.Both;
            _reportBox.WordWrap = false;
            _reportBox.BackColor = SystemColors.Window;
            _reportBox.Font = new Font(FontFamily.GenericMonospace, Ui.DefaultFont.Size);

            Controls.Add(_reportBox);
            Controls.Add(buttons);
            Controls.Add(_headline);
        }

        public override void OnActivated()
        {
            TransferReport report = Session.LastReport;
            if (report == null)
            {
                _reportBox.Text = "No report was produced.";
                return;
            }

            _reportBox.Text = ReportWriter.BuildText(report).Replace("\n", Environment.NewLine);
            _reportBox.Select(0, 0);

            if (report.FilesFailed > 0)
            {
                _headline.ForeColor = Color.FromArgb(168, 0, 0);
                _headline.Text = report.FilesCopied.ToString("N0") + " copied, "
                                 + report.FilesFailed.ToString("N0") + " failed, "
                                 + report.FilesSkipped.ToString("N0") + " skipped";
            }
            else if (report.Cancelled)
            {
                _headline.ForeColor = Color.FromArgb(140, 90, 0);
                _headline.Text = "Cancelled after copying " + report.FilesCopied.ToString("N0") + " file(s)";
            }
            else
            {
                _headline.ForeColor = Color.FromArgb(0, 100, 0);
                _headline.Text = report.FilesCopied.ToString("N0") + " file(s) copied"
                                 + (report.FilesSkipped > 0
                                    ? ", " + report.FilesSkipped.ToString("N0") + " skipped on purpose"
                                    : string.Empty);
            }

            // Save automatically: people close the window and then want the file.
            AutoSave(report);
        }

        private void AutoSave(TransferReport report)
        {
            try
            {
                string directory = Log.DataDirectory;
                string path = ReportWriter.Save(report, directory);
                if (path != null)
                {
                    Session.LastReportPath = path;
                    Host.SetStatus("Report saved to " + path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not auto-save the report: " + ex.Message);
            }
        }

        private void SaveOnClick(object sender, EventArgs e)
        {
            TransferReport report = Session.LastReport;
            if (report == null)
            {
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Text report (*.txt)|*.txt|Web page (*.html)|*.html";
                dialog.FileName = "MoveToNewPC-report-"
                                  + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    bool html = dialog.FilterIndex == 2
                                || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                    string content = html ? ReportWriter.BuildHtml(report) : ReportWriter.BuildText(report);
                    File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(true));
                    Host.SetStatus("Saved " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    Ui.Error(this, "Could not save the report:" + Environment.NewLine + ex.Message);
                }
            }
        }

        private void OpenFolderOnClick(object sender, EventArgs e)
        {
            Ui.ShowInExplorer(Session.LastReportPath ?? Log.FilePath);
        }

        private void CopyLogOnClick(object sender, EventArgs e)
        {
            Ui.CopyToClipboard(Log.FilePath);
            Host.SetStatus("Log path copied to the clipboard.");
        }

        private void StartOverOnClick(object sender, EventArgs e)
        {
            Session.Reset();
            Host.NavigateRoot(new RolePage());
        }
    }
}
