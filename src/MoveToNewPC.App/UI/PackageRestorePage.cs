using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Receiver side of the offline transport: pick the package, unlock it, and say where
    /// its contents should land.
    /// </summary>
    public sealed class PackageRestorePage : WizardPage
    {
        private TextBox _packageBox;
        private TextBox _passwordBox;
        private TextBox _destinationBox;
        private Label _statusLabel;
        private Label _warningLabel;
        private LayoutChoice _layoutChoice;
        private Label _destinationLabel;

        public PackageRestorePage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Which package should be restored?"; }
        }

        public override string Subtitle
        {
            get { return "Point at the file written on the old PC and give the password used to create it."; }
        }

        public override bool CanGoNext
        {
            get
            {
                return _packageBox != null
                       && _packageBox.Text.Trim().Length > 0
                       && _passwordBox.Text.Length > 0
                       && _destinationBox.Text.Trim().Length > 0;
            }
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            Label packagePrompt = new Label();
            packagePrompt.Text = "Package file:";
            packagePrompt.Location = new Point(24, 12);
            packagePrompt.Size = new Size(200, 20);
            Controls.Add(packagePrompt);

            _packageBox = new TextBox();
            _packageBox.Location = new Point(24, 34);
            _packageBox.Size = new Size(600, 22);
            _packageBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _packageBox.TextChanged += AnythingChanged;
            Controls.Add(_packageBox);

            Button browsePackage = Ui.MakeButton("&Browse...", 92);
            browsePackage.Location = new Point(632, 33);
            browsePackage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browsePackage.Click += BrowsePackageOnClick;
            Controls.Add(browsePackage);

            Label passwordPrompt = new Label();
            passwordPrompt.Text = "Password:";
            passwordPrompt.Location = new Point(24, 74);
            passwordPrompt.Size = new Size(240, 20);
            Controls.Add(passwordPrompt);

            _passwordBox = new TextBox();
            _passwordBox.Location = new Point(24, 96);
            _passwordBox.Size = new Size(300, 22);
            _passwordBox.UseSystemPasswordChar = true;
            _passwordBox.TextChanged += AnythingChanged;
            Controls.Add(_passwordBox);

            Button testButton = Ui.MakeButton("&Check package", 130);
            testButton.Location = new Point(344, 95);
            testButton.Click += TestOnClick;
            Controls.Add(testButton);

            _layoutChoice = new LayoutChoice();
            _layoutChoice.Location = new Point(24, 132);
            _layoutChoice.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _layoutChoice.LayoutChanged += LayoutOnChanged;
            Controls.Add(_layoutChoice);

            _destinationLabel = new Label();
            _destinationLabel.Text = "Restore the files into:";
            _destinationLabel.Location = new Point(24, 268);
            _destinationLabel.Size = new Size(320, 20);
            Controls.Add(_destinationLabel);

            _destinationBox = new TextBox();
            _destinationBox.Location = new Point(24, 290);
            _destinationBox.Size = new Size(600, 22);
            _destinationBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _destinationBox.TextChanged += AnythingChanged;
            Controls.Add(_destinationBox);

            Button browseDestination = Ui.MakeButton("B&rowse...", 92);
            browseDestination.Location = new Point(632, 289);
            browseDestination.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browseDestination.Click += BrowseDestinationOnClick;
            Controls.Add(browseDestination);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(24, 324);
            _statusLabel.Size = new Size(720, 40);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_statusLabel);

            _warningLabel = new Label();
            _warningLabel.Location = new Point(24, 366);
            _warningLabel.Size = new Size(720, 40);
            _warningLabel.ForeColor = Color.FromArgb(168, 0, 0);
            _warningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_warningLabel);

            Label explain = new Label();
            explain.Location = new Point(24, 408);
            explain.Size = new Size(720, 40);
            explain.ForeColor = SystemColors.GrayText;
            explain.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            explain.Text = "If the package has been altered or damaged since it was written, the restore "
                           + "stops and says so rather than writing files you cannot trust.";
            Controls.Add(explain);
        }

        public override void OnActivated()
        {
            if (string.IsNullOrEmpty(_destinationBox.Text))
            {
                try
                {
                    _destinationBox.Text = LongPath.ToDisplay(LongPath.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "Restored from old PC"));
                }
                catch (Exception)
                {
                    // Leave it empty; the operator can type a path.
                }
            }
        }

        private void LayoutOnChanged(object sender, EventArgs e)
        {
            // In matching mode the chosen folder is only used for the resume journal, so
            // say what it is actually for rather than leaving a misleading label.
            _destinationLabel.Text = _layoutChoice.NeedsDestinationFolder
                ? "Restore the files into:"
                : "Folder to keep the restore record in:";
        }

        private void AnythingChanged(object sender, EventArgs e)
        {
            _statusLabel.Text = string.Empty;
            _warningLabel.Text = string.Empty;
            Host.RefreshChrome();
        }

        private void BrowsePackageOnClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Choose the package written on the old PC";
                dialog.Filter = "MoveToNewPC package (*" + PackageSink.FileExtension + ")|*"
                                + PackageSink.FileExtension + "|All files (*.*)|*.*";
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _packageBox.Text = dialog.FileName;
                }
            }
        }

        private void BrowseDestinationOnClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where the restored files should go";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(_destinationBox.Text) && Directory.Exists(_destinationBox.Text))
                {
                    dialog.SelectedPath = _destinationBox.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _destinationBox.Text = dialog.SelectedPath;
                }
            }
        }

        /// <summary>
        /// Opens the package and reads only its header, so the operator finds out the
        /// password is wrong here rather than after choosing a destination.
        /// </summary>
        private void TestOnClick(object sender, EventArgs e)
        {
            _statusLabel.Text = string.Empty;
            _warningLabel.Text = string.Empty;

            string path = _packageBox.Text.Trim();
            if (path.Length == 0 || _passwordBox.Text.Length == 0)
            {
                _warningLabel.Text = "Choose a package and type its password first.";
                return;
            }

            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                string error;
                using (PackageReader reader = PackageReader.Open(path, _passwordBox.Text, out error))
                {
                    if (reader == null)
                    {
                        _warningLabel.Text = error;
                        return;
                    }

                    string machine = reader.Manifest.SourceMachine;
                    int users = reader.Manifest.Users.Count;
                    _statusLabel.Text = "Package opened. Written on "
                                        + (string.IsNullOrEmpty(machine) ? "an unknown PC" : machine)
                                        + " on "
                                        + reader.Manifest.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy, HH:mm")
                                        + ", containing "
                                        + users.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                        + (users == 1 ? " account." : " accounts.");
                }
            }
            catch (Exception ex)
            {
                _warningLabel.Text = "The package could not be read: " + ex.Message;
            }
            finally
            {
                Cursor.Current = previous;
            }
        }

        public override WizardPage OnNext()
        {
            string package = _packageBox.Text.Trim();
            string destination = _destinationBox.Text.Trim();

            if (package.Length == 0 || destination.Length == 0)
            {
                return null;
            }

            if (!File.Exists(package))
            {
                Ui.Error(this, "That package file does not exist.");
                return null;
            }

            // Unlock it here so a wrong password never reaches the progress screen.
            string error;
            using (PackageReader reader = PackageReader.Open(package, _passwordBox.Text, out error))
            {
                if (reader == null)
                {
                    Ui.Error(this, error);
                    return null;
                }
            }

            try
            {
                int code;
                if (!NativeFile.CreateDirectoryRecursive(destination, out code))
                {
                    Ui.Error(this, "That folder could not be created:" + Environment.NewLine
                                   + NativeFile.DescribeError(code));
                    return null;
                }
            }
            catch (Exception ex)
            {
                Ui.Error(this, "That folder could not be used:" + Environment.NewLine + ex.Message);
                return null;
            }

            Session.PackagePath = package;
            Session.PackagePassphrase = _passwordBox.Text;
            Session.DestinationFolder = destination;
            Session.Layout = _layoutChoice.SelectedLayout;

            return new TransferPage();
        }
    }
}
