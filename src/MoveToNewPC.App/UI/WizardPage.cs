using System;
using System.Windows.Forms;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Base for every screen. Pages are plain UserControls docked into MainForm's content
    /// area; there are no .resx files anywhere in this project so the build stays a plain
    /// csc invocation with no resource compiler in the loop.
    /// </summary>
    public abstract class WizardPage : UserControl
    {
        private MainForm _host;

        protected WizardPage()
        {
            Dock = DockStyle.Fill;
            BackColor = System.Drawing.SystemColors.Control;
            Font = Ui.DefaultFont;
            AutoScaleMode = AutoScaleMode.Font;
        }

        public MainForm Host
        {
            get { return _host; }
            internal set { _host = value; }
        }

        public AppSession Session
        {
            get { return _host == null ? null : _host.Session; }
        }

        public abstract string Title { get; }

        public virtual string Subtitle
        {
            get { return string.Empty; }
        }

        public virtual string NextText
        {
            get { return "Next >"; }
        }

        public virtual string BackText
        {
            get { return "< Back"; }
        }

        public virtual string CancelText
        {
            get { return "Cancel"; }
        }

        public virtual bool ShowBack { get { return true; } }
        public virtual bool ShowNext { get { return true; } }
        public virtual bool ShowCancel { get { return true; } }
        public virtual bool CanGoBack { get { return true; } }
        public virtual bool CanGoNext { get { return true; } }

        /// <summary>Raise whenever CanGoNext/CanGoBack may have changed.</summary>
        public event EventHandler StateChanged;

        protected void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Called after the page is shown.</summary>
        public virtual void OnActivated() { }

        /// <summary>Called before the page is replaced. Stop threads here.</summary>
        public virtual void OnDeactivating() { }

        /// <summary>Return the next page, or null to stay where we are.</summary>
        public virtual WizardPage OnNext()
        {
            return null;
        }

        /// <summary>Return false to veto going back.</summary>
        public virtual bool OnBack()
        {
            return true;
        }

        /// <summary>Return false to veto closing the application.</summary>
        public virtual bool OnCancel()
        {
            return true;
        }
    }
}
