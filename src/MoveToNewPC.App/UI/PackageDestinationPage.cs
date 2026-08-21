using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Sender side of the offline transport: where to write the encrypted package, and the
    /// password that protects it.
    /// </summary>
    public sealed class PackageDestinationPage : WizardPage
    {
        private TextBox _pathBox;
        private TextBox _passwordBox;
        private TextBox _confirmBox;
        private Button _browseButton;
        private Label _requiredLabel;
        private Label _spaceLabel;
        private Label _warningLabel;

        /// <summary>Short enough to type on a second machine, long enough to be worth having.</summary>
        private const int MinimumPasswordLength = 8;

        public PackageDestinationPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Where should the package go?"; }
        }

        public override string Subtitle
        {
            get { return "One encrypted file you carry to the new PC on a USB disk, external drive or share."; }
        }

        public override bool CanGoNext
        {
            get
            {
                return _pathBox != null
                       && _pathBox.Text.Trim().Length > 0
                       && _passwordBox.Text.Length >= MinimumPasswordLength
                       && _passwordBox.Text == _confirmBox.Text;
            }
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            Label prompt = new Label();
            prompt.Text = "Package file:";
            prompt.Location = new Point(24, 12);
            prompt.Size = new Size(200, 20);
            Controls.Add(prompt);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(24, 34);
            _pathBox.Size = new Size(600, 22);
            _pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pathBox.TextChanged += AnythingChanged;
            Controls.Add(_pathBox);

            _browseButton = Ui.MakeButton("&Browse...", 92);
            _browseButton.Location = new Point(632, 33);
            _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseButton.Click += BrowseOnClick;
            Controls.Add(_browseButton);

            Label passwordPrompt = new Label();
            passwordPrompt.Text = "Password for the package:";
            passwordPrompt.Location = new Point(24, 74);
            passwordPrompt.Size = new Size(240, 20);
            Controls.Add(passwordPrompt);

            _passwordBox = new TextBox();
            _passwordBox.Location = new Point(24, 96);
            _passwordBox.Size = new Size(300, 22);
            _passwordBox.UseSystemPasswordChar = true;
            _passwordBox.TextChanged += AnythingChanged;
            Controls.Add(_passwordBox);

            Label confirmPrompt = new Label();
            confirmPrompt.Text = "Type it again:";
            confirmPrompt.Location = new Point(344, 74);
            confirmPrompt.Size = new Size(240, 20);
            Controls.Add(confirmPrompt);

            _confirmBox = new TextBox();
            _confirmBox.Location = new Point(344, 96);
            _confirmBox.Size = new Size(300, 22);
            _confirmBox.UseSystemPasswordChar = true;
            _confirmBox.TextChanged += AnythingChanged;
            Controls.Add(_confirmBox);

            _requiredLabel = new Label();
            _requiredLabel.Location = new Point(24, 132);
            _requiredLabel.Size = new Size(700, 20);
            Controls.Add(_requiredLabel);

            _spaceLabel = new Label();
            _spaceLabel.Location = new Point(24, 154);
            _spaceLabel.Size = new Size(700, 20);
            Controls.Add(_spaceLabel);

            _warningLabel = new Label();
            _warningLabel.Location = new Point(24, 182);
            _warningLabel.Size = new Size(720, 44);
            _warningLabel.ForeColor = Color.FromArgb(168, 0, 0);
            _warningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_warningLabel);

            Label explain = new Label();
            explain.Location = new Point(24, 234);
            explain.Size = new Size(720, 120);
            explain.ForeColor = SystemColors.GrayText;
            explain.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            explain.Text = "The package is encrypted with this password. There is no way to recover it and no "
                           + "back door: if you forget it, the package is lost. Write it down before you "
                           + "continue."
                           + Environment.NewLine + Environment.NewLine
                           + "File permissions are deliberately not copied. The account IDs from this PC mean "
                           + "nothing on the new one, and carrying them across is how migrations end up with "
                           + "files nobody can open.";
            Controls.Add(explain);
        }

        public override void OnActivated()
        {
            if (string.IsNullOrEmpty(_pathBox.Text))
            {
                _pathBox.Text = Session.PackagePath ?? DefaultPackagePath();
            }
            UpdateEstimates();
        }

        private static string DefaultPackagePath()
        {
            string name = "MoveToNewPC-" + Environment.MachineName + "-"
                          + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                          + PackageSink.FileExtension;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return LongPath.ToDisplay(LongPath.Combine(desktop, name));
            }
            catch (Exception)
            {
                return name;
            }
        }

        private void AnythingChanged(object sender, EventArgs e)
        {
            UpdateEstimates();
            Host.RefreshChrome();
        }

        private void BrowseOnClick(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Save the encrypted package as";
                dialog.Filter = "MoveToNewPC package (*" + PackageSink.FileExtension + ")|*"
                                + PackageSink.FileExtension + "|All files (*.*)|*.*";
                dialog.OverwritePrompt = true;
                dialog.AddExtension = true;
                dialog.DefaultExt = PackageSink.FileExtension.TrimStart('.');

                string current = _pathBox.Text.Trim();
                if (current.Length > 0)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(current);
                        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                        {
                            dialog.InitialDirectory = directory;
                        }
                        dialog.FileName = Path.GetFileName(current);
                    }
                    catch (ArgumentException)
                    {
                        // A path the dialog cannot parse: just open it at its default.
                    }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dialog.FileName;
                }
            }
        }

        private void UpdateEstimates()
        {
            long required = 0;
            for (int u = 0; u < Session.Selection.Users.Count; u++)
            {
                UserSelection user = Session.Selection.Users[u];
                if (!user.Selected) { continue; }
                for (int r = 0; r < user.Roots.Count; r++)
                {
                    if (user.Roots[r].Selected)
                    {
                        required += user.Roots[r].EstimatedBytes;
                    }
                }
            }

            _requiredLabel.Text = "Estimated size of the selection: " + Format.Bytes(required);

            _warningLabel.Text = string.Empty;
            if (_passwordBox.Text.Length > 0 && _passwordBox.Text.Length < MinimumPasswordLength)
            {
                _warningLabel.Text = "The password must be at least "
                                     + MinimumPasswordLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                     + " characters.";
            }
            else if (_confirmBox.Text.Length > 0 && _passwordBox.Text != _confirmBox.Text)
            {
                _warningLabel.Text = "The two passwords do not match.";
            }

            string path = _pathBox.Text.Trim();
            _spaceLabel.Text = string.Empty;
            if (path.Length == 0)
            {
                return;
            }

            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }
                DriveInfo drive = new DriveInfo(root);
                long free = drive.AvailableFreeSpace;
                _spaceLabel.Text = "Free space on that drive: " + Format.Bytes(free);

                if (required > 0 && free < required)
                {
                    _warningLabel.Text = "That drive does not have room for the package. It needs about "
                                         + Format.Bytes(required - free) + " more.";
                }
            }
            catch (Exception)
            {
                // An unmapped drive or a path we cannot inspect: not worth blocking on.
            }
        }

        public override WizardPage OnNext()
        {
            string path = _pathBox.Text.Trim();
            if (path.Length == 0)
            {
                return null;
            }

            if (_passwordBox.Text != _confirmBox.Text)
            {
                Ui.Error(this, "The two passwords do not match.");
                return null;
            }
            if (_passwordBox.Text.Length < MinimumPasswordLength)
            {
                Ui.Error(this, "The password must be at least "
                               + MinimumPasswordLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                               + " characters.");
                return null;
            }

            if (!path.EndsWith(PackageSink.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                path += PackageSink.FileExtension;
            }

            // The package must not land inside a folder we are about to read: the scan would
            // race the file being written into it.
            for (int u = 0; u < Session.Selection.Users.Count; u++)
            {
                UserSelection user = Session.Selection.Users[u];
                if (!user.Selected) { continue; }
                for (int r = 0; r < user.Roots.Count; r++)
                {
                    SelectionRoot root = user.Roots[r];
                    if (!root.Selected || string.IsNullOrEmpty(root.SourcePath)) { continue; }
                    if (LongPath.GetRelativePath(root.SourcePath, path) != null)
                    {
                        Ui.Error(this, "The package would be written inside a folder you are copying ("
                                       + LongPath.ToDisplay(root.SourcePath) + "). Choose somewhere else.");
                        return null;
                    }
                }
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    int error;
                    if (!NativeFile.CreateDirectoryRecursive(directory, out error))
                    {
                        Ui.Error(this, "That folder could not be created:" + Environment.NewLine
                                       + NativeFile.DescribeError(error));
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Ui.Error(this, "That location could not be used:" + Environment.NewLine + ex.Message);
                return null;
            }

            Session.PackagePath = path;
            Session.PackagePassphrase = _passwordBox.Text;
            // The manifest and journal live beside the package.
            Session.DestinationFolder = Path.GetDirectoryName(path);

            return new TransferPage();
        }
    }
}
