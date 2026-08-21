using System;
using System.Drawing;
using System.Windows.Forms;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// The handful of decisions that change what gets copied or how. Every default here is
    /// the cautious one; nothing destructive happens without a deliberate change.
    /// </summary>
    public sealed class OptionsPage : WizardPage
    {
        private CheckBox _hydrateCloud;
        private CheckBox _includeEncrypted;
        private CheckBox _includeHidden;
        private CheckBox _includeSystem;
        private CheckBox _verifyHash;
        private CheckBox _dryRun;
        private ComboBox _collision;
        private Label _encryptedWarning;

        public OptionsPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "How should files be handled?"; }
        }

        public override string Subtitle
        {
            get { return "The defaults are safe. Change these only if you know you need to."; }
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);
            int y = 12;

            Label collisionLabel = new Label();
            collisionLabel.Text = "If a file already exists at the destination:";
            collisionLabel.Location = new Point(24, y + 3);
            collisionLabel.Size = new Size(260, 20);
            Controls.Add(collisionLabel);

            _collision = new ComboBox();
            _collision.DropDownStyle = ComboBoxStyle.DropDownList;
            _collision.Location = new Point(292, y);
            _collision.Width = 260;
            _collision.Items.Add("Skip it, keep what is already there");
            _collision.Items.Add("Keep both (add \"(1)\" to the new one)");
            _collision.Items.Add("Overwrite it");
            _collision.SelectedIndex = 0;
            Controls.Add(_collision);

            y += 40;

            _verifyHash = Check("&Verify every file with a SHA-256 checksum", y, true,
                "Slower, but proves each file arrived intact. Strongly recommended.");
            y += 46;

            _includeHidden = Check("Include &hidden files and folders", y, true,
                "Most application data lives in hidden folders, so this is normally on.");
            y += 46;

            _includeSystem = Check("Include &system files", y, false,
                "Rarely useful and often unreadable. Leave this off unless you have a reason.");
            y += 46;

            _hydrateCloud = Check("&Download OneDrive / Dropbox files that are online-only", y, false,
                "Off by default: reading these files downloads them, which can mean many gigabytes "
                + "over your internet connection. Left off, they are skipped and listed in the report.");
            y += 56;

            _includeEncrypted = Check("Include &EFS-encrypted files", y, false,
                "These are encrypted with a key that only exists on this PC for this account.");
            _includeEncrypted.CheckedChanged += EncryptedOnCheckedChanged;
            y += 46;

            _encryptedWarning = new Label();
            _encryptedWarning.Location = new Point(48, y);
            _encryptedWarning.Size = new Size(680, 34);
            _encryptedWarning.ForeColor = Color.FromArgb(168, 0, 0);
            _encryptedWarning.Font = Ui.Bold(Ui.DefaultFont);
            _encryptedWarning.Visible = false;
            _encryptedWarning.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _encryptedWarning.Text = "Warning: copied EFS files will NOT open on the new PC unless you also "
                                     + "export and import the certificate. They will look fine and be unreadable.";
            Controls.Add(_encryptedWarning);
            y += 40;

            _dryRun = Check("&Dry run - produce the full report without copying anything", y, false,
                "Scans everything and tells you exactly what would happen. Nothing is written.");

            Controls.Add(_dryRun);
        }

        private CheckBox Check(string text, int top, bool initial, string description)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Location = new Point(24, top);
            box.Size = new Size(560, 20);
            box.Checked = initial;
            box.UseVisualStyleBackColor = true;
            Controls.Add(box);

            Label label = new Label();
            label.Text = description;
            label.Location = new Point(42, top + 20);
            label.Size = new Size(690, 34);
            label.ForeColor = SystemColors.GrayText;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(label);

            return box;
        }

        private void EncryptedOnCheckedChanged(object sender, EventArgs e)
        {
            _encryptedWarning.Visible = _includeEncrypted.Checked;
        }

        public override WizardPage OnNext()
        {
            CopyOptions options = Session.CopyOptions;

            switch (_collision.SelectedIndex)
            {
                case 1: options.Collision = CollisionPolicy.KeepBoth; break;
                case 2: options.Collision = CollisionPolicy.Overwrite; break;
                default: options.Collision = CollisionPolicy.Skip; break;
            }

            options.VerifyHash = _verifyHash.Checked;
            options.HydrateCloudFiles = _hydrateCloud.Checked;
            options.IncludeEncryptedFiles = _includeEncrypted.Checked;
            options.DryRun = _dryRun.Checked;

            Session.Selection.IncludeHidden = _includeHidden.Checked;
            Session.Selection.IncludeSystem = _includeSystem.Checked;
            Session.Selection.HydrateCloudFiles = _hydrateCloud.Checked;
            Session.Selection.IncludeEncryptedFiles = _includeEncrypted.Checked;

            if (options.Collision == CollisionPolicy.Overwrite)
            {
                if (!Ui.Confirm(this, "Overwrite means existing files at the destination will be replaced "
                                      + "and cannot be recovered." + Environment.NewLine + Environment.NewLine
                                      + "Are you sure?"))
                {
                    return null;
                }
            }

            return new DestinationPage();
        }
    }
}
