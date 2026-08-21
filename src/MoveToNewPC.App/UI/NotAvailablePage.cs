using System;
using System.Drawing;
using System.Windows.Forms;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// An honest dead end. Better than a disabled button with no explanation: it says which
    /// milestone the feature belongs to and what to do instead.
    /// </summary>
    public sealed class NotAvailablePage : WizardPage
    {
        private readonly string _title;

        public NotAvailablePage(string title, string body)
        {
            _title = title;
            Padding = new Padding(24, 20, 24, 12);

            Label label = new Label();
            label.AutoSize = false;
            label.Location = new Point(24, 20);
            label.Size = new Size(720, 240);
            label.Text = body;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(label);
        }

        public override string Title
        {
            get { return _title; }
        }

        public override bool ShowNext
        {
            get { return false; }
        }

        public override string CancelText
        {
            get { return "Close"; }
        }
    }
}
