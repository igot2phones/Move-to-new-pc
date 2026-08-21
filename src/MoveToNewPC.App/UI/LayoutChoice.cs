using System;
using System.Drawing;
using System.Windows.Forms;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// The "where should the files go" choice, shared by both receiving screens so the
    /// wording and the behaviour cannot drift apart between them.
    ///
    /// Single folder is the default on purpose: it cannot disturb anything already on this
    /// PC, which matters because the operator may be restoring onto a machine they have
    /// already started using.
    /// </summary>
    public sealed class LayoutChoice : Panel
    {
        private RadioButton _singleFolder;
        private RadioButton _matching;
        private Label _detail;

        public LayoutChoice()
        {
            Build();
        }

        public DestinationLayout SelectedLayout
        {
            get
            {
                return _matching.Checked
                       ? DestinationLayout.MatchingFolders
                       : DestinationLayout.SingleFolder;
            }
        }

        /// <summary>Raised when the operator switches option, so the host can relabel things.</summary>
        public event EventHandler LayoutChanged;

        private void Build()
        {
            Size = new Size(720, 128);

            _singleFolder = new RadioButton();
            _singleFolder.Text = "Put everything in the &folder I choose";
            _singleFolder.Font = Ui.Bold(Ui.DefaultFont);
            _singleFolder.Location = new Point(0, 0);
            _singleFolder.Size = new Size(560, 22);
            _singleFolder.Checked = true;
            _singleFolder.UseVisualStyleBackColor = true;
            _singleFolder.CheckedChanged += OnCheckedChanged;
            Controls.Add(_singleFolder);

            Label singleNote = new Label();
            singleNote.Text = "One folder per account, with the original folder names inside. "
                              + "Nothing already on this PC is touched.";
            singleNote.Location = new Point(20, 22);
            singleNote.Size = new Size(690, 20);
            singleNote.ForeColor = SystemColors.GrayText;
            singleNote.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(singleNote);

            _matching = new RadioButton();
            _matching.Text = "Put them back in their &normal places on this PC";
            _matching.Font = Ui.Bold(Ui.DefaultFont);
            _matching.Location = new Point(0, 50);
            _matching.Size = new Size(560, 22);
            _matching.UseVisualStyleBackColor = true;
            _matching.CheckedChanged += OnCheckedChanged;
            Controls.Add(_matching);

            _detail = new Label();
            _detail.Location = new Point(20, 72);
            _detail.Size = new Size(690, 52);
            _detail.ForeColor = SystemColors.GrayText;
            _detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _detail.Text = BuildDetailText();
            Controls.Add(_detail);
        }

        /// <summary>
        /// Names this PC's actual folders rather than saying "your Documents", because on a
        /// machine with redirected folders the real path is the useful information.
        /// </summary>
        private static string BuildDetailText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Desktop, Documents, Downloads, Music and Videos are merged into this PC's own ");
            sb.Append("folders of the same name. Everything else - including browser and mail data - ");
            sb.Append("goes into one folder on your Desktop.");

            string desktop = LocalKnownFolders.Resolve(KnownFolder.Desktop);
            if (!string.IsNullOrEmpty(desktop))
            {
                sb.Append(Environment.NewLine);
                sb.Append("Files with the same name as one already there are left alone unless you ");
                sb.Append("change that on the options screen.");
            }
            return sb.ToString();
        }

        private void OnCheckedChanged(object sender, EventArgs e)
        {
            EventHandler handler = LayoutChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// True when the chosen destination folder is still needed. In matching mode the
        /// folder is only used for the journal, so the host can relabel or grey its picker.
        /// </summary>
        public bool NeedsDestinationFolder
        {
            get { return SelectedLayout == DestinationLayout.SingleFolder; }
        }
    }
}
