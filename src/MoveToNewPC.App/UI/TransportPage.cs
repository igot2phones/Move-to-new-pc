using System;
using System.Drawing;
using System.Windows.Forms;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// How the two machines will talk. The file engine sits behind one sink interface, so
    /// this choice only decides which sink gets constructed later.
    /// </summary>
    public sealed class TransportPage : WizardPage
    {
        private RadioButton _lan;
        private RadioButton _cable;
        private RadioButton _offline;

        public TransportPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "How should the files travel?"; }
        }

        public override string Subtitle
        {
            get { return "Both computers must use the same option."; }
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            _lan = Add("Same &network (recommended)",
                       "Both PCs are on the same Wi-Fi or wired network. They find each other automatically.",
                       20, true, null);
            _lan.Checked = true;

            _cable = Add("Direct &Ethernet cable between the two PCs",
                         "No router needed. Plug a cable between both PCs and wait about a minute for them to sort out addresses.",
                         88, true, null);

            _offline = Add("&External drive or shared folder",
                           "Use a USB disk, an external drive or a shared folder. Writes one encrypted package you carry across, then restores it on the new PC.",
                           156, true, null);

            Label note = new Label();
            note.AutoSize = false;
            note.Location = new Point(28, 232);
            note.Size = new Size(720, 88);
            note.ForeColor = SystemColors.GrayText;
            note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            note.Text = "Everything is encrypted either way. Over the network the two PCs prove they know "
                        + "the same six-digit code before a single file moves; on a drive, the package is "
                        + "locked with a password you choose."
                        + Environment.NewLine + Environment.NewLine
                        + "Run this program on both PCs: choose 'old' on the one you are leaving and 'new' "
                        + "on the one you are moving to.";
            Controls.Add(note);
        }

        private RadioButton Add(string text, string description, int top, bool enabled, string unavailableNote)
        {
            RadioButton button = new RadioButton();
            button.Text = text;
            button.Font = Ui.Bold(Ui.DefaultFont);
            button.Location = new Point(28, top);
            button.Size = new Size(600, 22);
            button.Enabled = enabled;
            button.UseVisualStyleBackColor = true;
            Controls.Add(button);

            Label label = new Label();
            label.AutoSize = false;
            label.Text = description + (unavailableNote == null ? string.Empty : "   [" + unavailableNote + "]");
            label.Location = new Point(50, top + 22);
            label.Size = new Size(700, 40);
            label.ForeColor = enabled ? SystemColors.GrayText : SystemColors.GrayText;
            label.Enabled = enabled;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(label);

            return button;
        }

        public override WizardPage OnNext()
        {
            if (_lan.Checked) { Session.Transport = TransportKind.Lan; }
            else if (_cable.Checked) { Session.Transport = TransportKind.DirectCable; }
            else { Session.Transport = TransportKind.OfflinePackage; }

            MoveToNewPC.Core.Diagnostics.Log.Info("Transport chosen: " + Session.Transport);

            bool overNetwork = Session.Transport == TransportKind.Lan
                               || Session.Transport == TransportKind.DirectCable;

            if (Session.Role == AppRole.Receiver)
            {
                // The receiver never chooses what to send: it waits, or opens a package.
                return overNetwork ? (WizardPage)new LanReceivePage() : new PackageRestorePage();
            }

            return new UserSelectPage();
        }
    }
}
