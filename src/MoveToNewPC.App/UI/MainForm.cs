using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Wizard host: white header strip, content area, Back/Next/Cancel footer. Built in
    /// code rather than the designer so there is no .resx and no designer round-tripping.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppSession _session = new AppSession();
        private readonly List<WizardPage> _stack = new List<WizardPage>();

        private Panel _headerPanel;
        private Label _titleLabel;
        private Label _subtitleLabel;
        private Panel _contentPanel;
        private Panel _footerPanel;
        private Label _statusLabel;
        private Button _backButton;
        private Button _nextButton;
        private Button _cancelButton;

        private bool _closingForReal;

        public MainForm()
        {
            BuildLayout();
            Navigate(new RolePage());
        }

        public AppSession Session
        {
            get { return _session; }
        }

        public WizardPage CurrentPage
        {
            get { return _stack.Count == 0 ? null : _stack[_stack.Count - 1]; }
        }

        private void BuildLayout()
        {
            SuspendLayout();

            Text = Program.ProductName;
            Font = Ui.DefaultFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 580);
            MinimumSize = new Size(700, 500);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            // No icon file is shipped (portable single EXE); the default is fine and keeps
            // the build free of a resource compiler step.

            _headerPanel = new Panel();
            _headerPanel.Dock = DockStyle.Top;
            _headerPanel.Height = 62;
            _headerPanel.BackColor = SystemColors.Window;
            _headerPanel.Paint += HeaderPanelOnPaint;

            _titleLabel = new Label();
            _titleLabel.AutoSize = false;
            _titleLabel.Location = new Point(16, 10);
            _titleLabel.Size = new Size(760, 22);
            _titleLabel.Font = Ui.Bold(Ui.DefaultFont);
            _titleLabel.ForeColor = SystemColors.WindowText;
            _titleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _titleLabel.BackColor = Color.Transparent;

            _subtitleLabel = new Label();
            _subtitleLabel.AutoSize = false;
            _subtitleLabel.Location = new Point(28, 32);
            _subtitleLabel.Size = new Size(748, 20);
            _subtitleLabel.ForeColor = SystemColors.GrayText;
            _subtitleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _subtitleLabel.BackColor = Color.Transparent;

            _headerPanel.Controls.Add(_titleLabel);
            _headerPanel.Controls.Add(_subtitleLabel);

            _footerPanel = new Panel();
            _footerPanel.Dock = DockStyle.Bottom;
            _footerPanel.Height = 50;
            _footerPanel.BackColor = SystemColors.Control;
            _footerPanel.Paint += FooterPanelOnPaint;

            _cancelButton = Ui.MakeButton("Cancel", 92);
            _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cancelButton.Location = new Point(_footerPanel.Width - 104, 12);
            _cancelButton.Click += CancelButtonOnClick;

            _nextButton = Ui.MakeButton("Next >", 92);
            _nextButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _nextButton.Location = new Point(_footerPanel.Width - 204, 12);
            _nextButton.Click += NextButtonOnClick;

            _backButton = Ui.MakeButton("< Back", 92);
            _backButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _backButton.Location = new Point(_footerPanel.Width - 300, 12);
            _backButton.Click += BackButtonOnClick;

            _statusLabel = new Label();
            _statusLabel.AutoSize = false;
            _statusLabel.Location = new Point(14, 16);
            _statusLabel.Size = new Size(420, 20);
            _statusLabel.ForeColor = SystemColors.GrayText;
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _footerPanel.Controls.Add(_statusLabel);
            _footerPanel.Controls.Add(_backButton);
            _footerPanel.Controls.Add(_nextButton);
            _footerPanel.Controls.Add(_cancelButton);

            _contentPanel = new Panel();
            _contentPanel.Dock = DockStyle.Fill;
            _contentPanel.BackColor = SystemColors.Control;
            _contentPanel.Padding = new Padding(0);

            Controls.Add(_contentPanel);
            Controls.Add(_footerPanel);
            Controls.Add(_headerPanel);

            AcceptButton = _nextButton;

            ResumeLayout(true);
            LayoutFooter();
            _footerPanel.Resize += delegate { LayoutFooter(); };
        }

        private void LayoutFooter()
        {
            int right = _footerPanel.ClientSize.Width - 12;
            _cancelButton.Left = right - _cancelButton.Width;
            _nextButton.Left = _cancelButton.Left - _nextButton.Width - 10;
            _backButton.Left = _nextButton.Left - _backButton.Width - 4;
            _statusLabel.Width = Math.Max(80, _backButton.Left - _statusLabel.Left - 12);
        }

        private void HeaderPanelOnPaint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(SystemColors.ControlDark))
            {
                e.Graphics.DrawLine(pen, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
            }
        }

        private void FooterPanelOnPaint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(SystemColors.ControlDark))
            {
                e.Graphics.DrawLine(pen, 0, 0, _footerPanel.Width, 0);
            }
        }

        /// <summary>Pushes a page onto the wizard stack.</summary>
        public void Navigate(WizardPage page)
        {
            if (page == null)
            {
                return;
            }

            WizardPage current = CurrentPage;
            if (current != null)
            {
                current.OnDeactivating();
                current.StateChanged -= PageOnStateChanged;
                _contentPanel.Controls.Remove(current);
            }

            page.Host = this;
            page.StateChanged += PageOnStateChanged;
            _stack.Add(page);

            _contentPanel.SuspendLayout();
            _contentPanel.Controls.Add(page);
            page.BringToFront();
            _contentPanel.ResumeLayout(true);

            Log.Info("Screen: " + page.GetType().Name);
            RefreshChrome();
            page.OnActivated();
            RefreshChrome();
        }

        /// <summary>Replaces the whole stack; used by "start over".</summary>
        public void NavigateRoot(WizardPage page)
        {
            WizardPage current = CurrentPage;
            if (current != null)
            {
                current.OnDeactivating();
                current.StateChanged -= PageOnStateChanged;
                _contentPanel.Controls.Remove(current);
            }

            for (int i = 0; i < _stack.Count; i++)
            {
                _stack[i].Dispose();
            }
            _stack.Clear();
            Navigate(page);
        }

        public void GoBack()
        {
            if (_stack.Count < 2)
            {
                return;
            }

            WizardPage current = CurrentPage;
            if (!current.OnBack())
            {
                return;
            }

            current.OnDeactivating();
            current.StateChanged -= PageOnStateChanged;
            _contentPanel.Controls.Remove(current);
            _stack.RemoveAt(_stack.Count - 1);
            current.Dispose();

            WizardPage previous = CurrentPage;
            _contentPanel.Controls.Add(previous);
            previous.BringToFront();
            RefreshChrome();
            previous.OnActivated();
            RefreshChrome();
        }

        public void SetStatus(string message)
        {
            _statusLabel.Text = message ?? string.Empty;
        }

        /// <summary>Pages call this after changing anything the footer reflects.</summary>
        public void RefreshChrome()
        {
            WizardPage page = CurrentPage;
            if (page == null)
            {
                return;
            }

            _titleLabel.Text = page.Title;
            _subtitleLabel.Text = page.Subtitle;
            _subtitleLabel.Visible = !string.IsNullOrEmpty(page.Subtitle);

            _backButton.Visible = page.ShowBack;
            _backButton.Enabled = page.CanGoBack && _stack.Count > 1;
            _backButton.Text = page.BackText;

            _nextButton.Visible = page.ShowNext;
            _nextButton.Enabled = page.CanGoNext;
            _nextButton.Text = page.NextText;

            _cancelButton.Visible = page.ShowCancel;
            _cancelButton.Text = page.CancelText;

            AcceptButton = page.ShowNext && page.CanGoNext ? _nextButton : null;
            LayoutFooter();
        }

        private void PageOnStateChanged(object sender, EventArgs e)
        {
            RefreshChrome();
        }

        private void NextButtonOnClick(object sender, EventArgs e)
        {
            WizardPage page = CurrentPage;
            if (page == null || !page.CanGoNext)
            {
                return;
            }

            WizardPage next = page.OnNext();
            if (next != null)
            {
                Navigate(next);
            }
            else
            {
                RefreshChrome();
            }
        }

        private void BackButtonOnClick(object sender, EventArgs e)
        {
            GoBack();
        }

        private void CancelButtonOnClick(object sender, EventArgs e)
        {
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closingForReal)
            {
                WizardPage page = CurrentPage;
                if (page != null && !page.OnCancel())
                {
                    e.Cancel = true;
                    base.OnFormClosing(e);
                    return;
                }
                _closingForReal = true;
            }

            WizardPage current = CurrentPage;
            if (current != null)
            {
                current.OnDeactivating();
            }

            base.OnFormClosing(e);
        }
    }
}
