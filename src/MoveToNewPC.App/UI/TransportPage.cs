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
        private RadioButton _local;

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
                       20, false, "Arrives in milestone M3");

            _cable = Add("Direct &Ethernet cable between the two PCs",
                         "No router needed. Plug a cable between both PCs and wait about a minute for them to sort out addresses.",
                         88, false, "Arrives in milestone M3");

            _offline = Add("&External drive or shared folder",
                           "Use when the two PCs are never switched on at the same time. Writes one encrypted package you carry across.",
                           156, false, "Arrives in milestone M6");

            _local = Add("&Copy into a folder on this PC",
                         "No network at all. Copies the selected files straight into a folder you choose - a USB disk, a second drive, anywhere.",
                         224, true, null);
            _local.Checked = true;

            Label note = new Label();
            note.AutoSize = false;
            note.Location = new Point(28, 300);
            note.Size = new Size(720, 72);
            note.ForeColor = SystemColors.GrayText;
            note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            note.Text = "This build is milestone M2: profile discovery and the file engine are finished and "
                        + "usable end to end, but the network transports are not built yet."
                        + Environment.NewLine + Environment.NewLine
                        + "The last option does everything except the network hop, and uses exactly the same "
                        + "scanning, filtering, verification and reporting code the network transfer will use.";
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
            else if (_offline.Checked) { Session.Transport = TransportKind.OfflinePackage; }
            else { Session.Transport = TransportKind.LocalFolder; }

            MoveToNewPC.Core.Diagnostics.Log.Info("Transport chosen: " + Session.Transport);

            if (Session.Role == AppRole.Receiver)
            {
                return new NotAvailablePage(
                    "Receiving is not in this build yet",
                    "The receiver side arrives with the network transport in milestone M3, and the "
                    + "account mapping and resume support in M4."
                    + Environment.NewLine + Environment.NewLine
                    + "In this build, run the program on the OLD PC and copy into a folder - for example "
                    + "onto a USB disk - then carry that disk across.");
            }

            return new UserSelectPage();
        }
    }
}
