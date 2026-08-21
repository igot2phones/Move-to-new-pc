using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.UI
{
    /// <summary>Where the files land in local-folder mode.</summary>
    public sealed class DestinationPage : WizardPage
    {
        private TextBox _pathBox;
        private Button _browseButton;
        private Label _spaceLabel;
        private Label _requiredLabel;
        private Label _warningLabel;

        public DestinationPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Where should the files go?"; }
        }

        public override string Subtitle
        {
            get { return "Choose an empty folder - a USB disk, an external drive, or another drive on this PC."; }
        }

        public override bool CanGoNext
        {
            get { return _pathBox != null && _pathBox.Text.Trim().Length > 0; }
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            Label prompt = new Label();
            prompt.Text = "Destination folder:";
            prompt.Location = new Point(24, 16);
            prompt.Size = new Size(200, 20);
            Controls.Add(prompt);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(24, 38);
            _pathBox.Size = new Size(600, 22);
            _pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pathBox.TextChanged += PathOnTextChanged;
            Controls.Add(_pathBox);

            _browseButton = Ui.MakeButton("&Browse...", 92);
            _browseButton.Location = new Point(632, 37);
            _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseButton.Click += BrowseOnClick;
            Controls.Add(_browseButton);

            _requiredLabel = new Label();
            _requiredLabel.Location = new Point(24, 80);
            _requiredLabel.Size = new Size(700, 20);
            Controls.Add(_requiredLabel);

            _spaceLabel = new Label();
            _spaceLabel.Location = new Point(24, 104);
            _spaceLabel.Size = new Size(700, 20);
            Controls.Add(_spaceLabel);

            _warningLabel = new Label();
            _warningLabel.Location = new Point(24, 136);
            _warningLabel.Size = new Size(720, 60);
            _warningLabel.ForeColor = Color.FromArgb(168, 0, 0);
            _warningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_warningLabel);

            Label explain = new Label();
            explain.Location = new Point(24, 210);
            explain.Size = new Size(720, 120);
            explain.ForeColor = SystemColors.GrayText;
            explain.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            explain.Text = "Each account gets its own folder here, and inside it the folders keep their "
                           + "normal names (Desktop, Documents, and so on)."
                           + Environment.NewLine + Environment.NewLine
                           + "File permissions are deliberately not copied. The account IDs from this PC mean "
                           + "nothing on the new one, and carrying them across is how migrations end up with "
                           + "files nobody can open.";
            Controls.Add(explain);
        }

        public override void OnActivated()
        {
            if (!string.IsNullOrEmpty(Session.DestinationFolder))
            {
                _pathBox.Text = Session.DestinationFolder;
            }
            UpdateEstimates();
        }

        private void BrowseOnClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to copy the files";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(_pathBox.Text) && Directory.Exists(_pathBox.Text))
                {
                    dialog.SelectedPath = _pathBox.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void PathOnTextChanged(object sender, EventArgs e)
        {
            UpdateEstimates();
            RaiseStateChanged();
        }

        private long EstimateSelectedBytes()
        {
            long total = 0;
            for (int u = 0; u < Session.Selection.Users.Count; u++)
            {
                UserSelection user = Session.Selection.Users[u];
                if (!user.Selected)
                {
                    continue;
                }
                total += user.SelectedBytes;
            }
            return total;
        }

        private void UpdateEstimates()
        {
            long required = EstimateSelectedBytes();
            _requiredLabel.Text = "Estimated size of the selection: " + Format.Bytes(required);

            string path = _pathBox.Text.Trim();
            _warningLabel.Text = string.Empty;

            if (path.Length == 0)
            {
                _spaceLabel.Text = string.Empty;
                return;
            }

            long free;
            long total;
            if (NativeFile.TryGetFreeSpace(path, out free, out total))
            {
                _spaceLabel.Text = "Free space on that drive: " + Format.Bytes(free)
                                   + " of " + Format.Bytes(total);
                if (required > 0 && free < required)
                {
                    _warningLabel.Text = "There is not enough room: you need about "
                                         + Format.Bytes(required - free) + " more.";
                }
            }
            else
            {
                _spaceLabel.Text = "Free space: unknown (the folder does not exist yet).";
            }

            CheckOverlap(path);
        }

        /// <summary>
        /// Copying a folder into itself is a classic way to fill a disk. Catch the obvious
        /// case before anything is written.
        /// </summary>
        private void CheckOverlap(string destination)
        {
            for (int u = 0; u < Session.Selection.Users.Count; u++)
            {
                UserSelection user = Session.Selection.Users[u];
                if (!user.Selected)
                {
                    continue;
                }

                for (int r = 0; r < user.Roots.Count; r++)
                {
                    SelectionRoot root = user.Roots[r];
                    if (!root.Selected)
                    {
                        continue;
                    }

                    if (LongPath.GetRelativePath(root.SourcePath, destination) != null
                        || LongPath.GetRelativePath(destination, root.SourcePath) != null)
                    {
                        _warningLabel.Text = "That folder overlaps a folder you are copying ("
                                             + LongPath.ToDisplay(root.SourcePath)
                                             + "). Choose somewhere else.";
                        return;
                    }
                }
            }
        }

        public override WizardPage OnNext()
        {
            string path = _pathBox.Text.Trim();
            if (path.Length == 0)
            {
                return null;
            }

            if (_warningLabel.Text.Length > 0
                && _warningLabel.Text.IndexOf("overlaps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Ui.Error(this, _warningLabel.Text);
                return null;
            }

            try
            {
                int error;
                if (!NativeFile.CreateDirectoryRecursive(path, out error))
                {
                    Ui.Error(this, "That folder could not be created:" + Environment.NewLine
                                   + NativeFile.DescribeError(error));
                    return null;
                }
            }
            catch (Exception ex)
            {
                Ui.Error(this, "That folder could not be used:" + Environment.NewLine + ex.Message);
                return null;
            }

            Session.DestinationFolder = path;
            return new TransferPage();
        }
    }
}
