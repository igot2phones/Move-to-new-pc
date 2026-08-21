using System;
using System.Drawing;
using System.Windows.Forms;

namespace MoveToNewPC.UI
{
    /// <summary>First screen: which machine am I standing at?</summary>
    public sealed class RolePage : WizardPage
    {
        private RadioButton _oldPc;
        private RadioButton _newPc;

        public RolePage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Which PC is this?"; }
        }

        public override string Subtitle
        {
            get { return "Run this program on both computers. Start with the old one."; }
        }

        public override bool ShowBack
        {
            get { return false; }
        }

        private void Build()
        {
            Padding = new Padding(24, 20, 24, 12);

            _oldPc = MakeChoice(
                "This is my &OLD PC",
                "Send files from this computer. Pick which accounts and folders to move.",
                24);
            _oldPc.Checked = true;

            _newPc = MakeChoice(
                "This is my &NEW PC",
                "Receive files onto this computer and choose where each account's files land.",
                120);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Location = new Point(28, 236);
            hint.Size = new Size(700, 80);
            hint.ForeColor = SystemColors.GrayText;
            hint.Text = "Both computers need to be running this program."
                        + Environment.NewLine
                        + "You can run it straight from a USB stick - there is nothing to install."
                        + Environment.NewLine + Environment.NewLine
                        + "Nothing is copied or changed until you confirm on the last screen.";
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Controls.Add(_oldPc);
            Controls.Add(_newPc);
            Controls.Add(hint);
        }

        private RadioButton MakeChoice(string text, string description, int top)
        {
            RadioButton button = new RadioButton();
            button.Text = text;
            button.Font = Ui.Bold(Ui.DefaultFont);
            button.Location = new Point(28, top);
            button.Size = new Size(560, 24);
            button.UseVisualStyleBackColor = true;
            button.TabStop = true;

            Label description2 = new Label();
            description2.AutoSize = false;
            description2.Text = description;
            description2.Location = new Point(50, top + 26);
            description2.Size = new Size(680, 40);
            description2.ForeColor = SystemColors.GrayText;
            description2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(description2);

            return button;
        }

        public override WizardPage OnNext()
        {
            Session.Role = _oldPc.Checked ? AppRole.Sender : AppRole.Receiver;
            MoveToNewPC.Core.Diagnostics.Log.Info("Role chosen: " + Session.Role);
            return new TransportPage();
        }
    }
}
