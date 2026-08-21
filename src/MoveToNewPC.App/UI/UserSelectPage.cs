using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Profiles;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Simple mode: tick the accounts to move. Advanced: also tick individual folders.
    ///
    /// Profile discovery loads other users' registry hives, which can take a second or two
    /// each, and size calculation walks entire folder trees - so both run on background
    /// threads and the list stays usable throughout.
    /// </summary>
    public sealed class UserSelectPage : WizardPage
    {
        private ListView _userList;
        private CheckedListBox _folderList;
        private Label _folderHeading;
        private Label _statusLabel;
        private LinkLabel _filteredLink;
        private CheckBox _advancedCheck;
        private CheckBox _appDataCheck;
        private Button _browsersButton;
        private Button _mailButton;
        private Button _refreshButton;
        private System.Windows.Forms.Timer _refreshTimer;
        private Splitter _splitter;
        private Panel _folderPanel;

        private readonly List<UserSelection> _users = new List<UserSelection>();
        private SelectionSizeCalculator _sizeCalculator;
        private Thread _discoveryThread;
        private volatile bool _discovering;
        private volatile bool _discovered;
        private int _pendingRefresh;

        public UserSelectPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Which accounts should move?"; }
        }

        public override string Subtitle
        {
            get
            {
                return _discovering
                       ? "Looking for user accounts on this PC..."
                       : "Tick the accounts you want to take to the new PC.";
            }
        }

        public override bool CanGoNext
        {
            get { return !_discovering && HasAnythingSelected(); }
        }

        private void Build()
        {
            Padding = new Padding(16, 12, 16, 8);

            _userList = new ListView();
            _userList.Dock = DockStyle.Fill;
            _userList.View = View.Details;
            _userList.CheckBoxes = true;
            _userList.FullRowSelect = true;
            _userList.GridLines = false;
            _userList.HideSelection = false;
            _userList.MultiSelect = false;
            _userList.Columns.Add("Account", 170);
            _userList.Columns.Add("Profile folder", 230);
            _userList.Columns.Add("Files", 80, HorizontalAlignment.Right);
            _userList.Columns.Add("Size", 100, HorizontalAlignment.Right);
            _userList.Columns.Add("Notes", 200);
            _userList.ItemChecked += UserListOnItemChecked;
            _userList.SelectedIndexChanged += UserListOnSelectedIndexChanged;

            _folderPanel = new Panel();
            _folderPanel.Dock = DockStyle.Bottom;
            _folderPanel.Height = 190;
            _folderPanel.Visible = false;

            _folderHeading = new Label();
            _folderHeading.Dock = DockStyle.Top;
            _folderHeading.Height = 20;
            _folderHeading.Font = Ui.Bold(Ui.DefaultFont);
            _folderHeading.Text = "Folders";

            _folderList = new CheckedListBox();
            _folderList.Dock = DockStyle.Fill;
            _folderList.CheckOnClick = true;
            _folderList.IntegralHeight = false;
            _folderList.ItemCheck += FolderListOnItemCheck;
            _folderList.SelectedIndexChanged += FolderListOnSelectedIndexChanged;

            _folderPanel.Controls.Add(_folderList);
            _folderPanel.Controls.Add(_folderHeading);

            _splitter = new Splitter();
            _splitter.Dock = DockStyle.Bottom;
            _splitter.Height = 4;
            _splitter.Visible = false;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 84;

            _advancedCheck = new CheckBox();
            _advancedCheck.Text = "&Advanced: choose individual folders";
            _advancedCheck.Location = new Point(0, 4);
            _advancedCheck.Size = new Size(260, 22);
            _advancedCheck.UseVisualStyleBackColor = true;
            _advancedCheck.CheckedChanged += AdvancedOnCheckedChanged;

            _appDataCheck = new CheckBox();
            _appDataCheck.Text = "Also offer application &data (browsers, mail)";
            _appDataCheck.Location = new Point(280, 4);
            _appDataCheck.Size = new Size(310, 22);
            _appDataCheck.UseVisualStyleBackColor = true;
            _appDataCheck.CheckedChanged += AppDataOnCheckedChanged;

            // One click for the two things people actually ask for by name. They only make
            // sense once Tier B has been detected, so they follow the app-data checkbox.
            _browsersButton = Ui.MakeButton("Select &browsers", 130);
            _browsersButton.Location = new Point(0, 30);
            _browsersButton.Enabled = false;
            _browsersButton.Click += BrowsersOnClick;

            _mailButton = Ui.MakeButton("Select &email", 130);
            _mailButton.Location = new Point(138, 30);
            _mailButton.Enabled = false;
            _mailButton.Click += MailOnClick;

            _refreshButton = Ui.MakeButton("&Rescan", 92);
            _refreshButton.Location = new Point(600, 2);
            _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshButton.Click += RefreshOnClick;

            _statusLabel = new Label();
            _statusLabel.Location = new Point(0, 58);
            _statusLabel.Size = new Size(560, 20);
            _statusLabel.ForeColor = SystemColors.GrayText;
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _filteredLink = new LinkLabel();
            _filteredLink.Location = new Point(0, 58);
            _filteredLink.Size = new Size(560, 20);
            _filteredLink.Visible = false;
            _filteredLink.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filteredLink.LinkClicked += FilteredLinkOnClicked;

            top.Controls.Add(_advancedCheck);
            top.Controls.Add(_appDataCheck);
            top.Controls.Add(_browsersButton);
            top.Controls.Add(_mailButton);
            top.Controls.Add(_refreshButton);
            top.Controls.Add(_statusLabel);
            top.Controls.Add(_filteredLink);

            Controls.Add(_userList);
            Controls.Add(_splitter);
            Controls.Add(_folderPanel);
            Controls.Add(top);

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 400;
            _refreshTimer.Tick += RefreshTimerOnTick;
        }

        public override void OnActivated()
        {
            _refreshTimer.Start();

            if (Session.Profiles == null)
            {
                StartDiscovery();
            }
            else if (_users.Count == 0)
            {
                RebuildFromSession();
            }
        }

        public override void OnDeactivating()
        {
            _refreshTimer.Stop();
            StopSizeCalculator();
        }

        public override bool OnCancel()
        {
            _refreshTimer.Stop();
            StopSizeCalculator();
            return true;
        }

        // ---- discovery ---------------------------------------------------------

        private void StartDiscovery()
        {
            if (_discovering)
            {
                return;
            }

            _discovering = true;
            _discovered = false;
            _users.Clear();
            _userList.Items.Clear();
            _folderList.Items.Clear();
            _statusLabel.Text = "Reading user profiles from the registry...";
            Host.RefreshChrome();

            // A thread rather than the thread pool: this can take several seconds when other
            // users' registry hives have to be mounted, and we want it named in a debugger.
            _discoveryThread = new Thread(DiscoveryWorker);
            _discoveryThread.IsBackground = true;
            _discoveryThread.Name = "MTNPC profile discovery";
            _discoveryThread.Start();
        }

        private void DiscoveryWorker()
        {
            ProfileEnumerationResult result = null;
            try
            {
                result = ProfileEnumerator.Enumerate();
            }
            catch (Exception ex)
            {
                Log.Error("Profile discovery failed", ex);
                result = new ProfileEnumerationResult();
                result.Warnings.Add("Profile discovery failed: " + ex.Message);
            }

            ProfileEnumerationResult captured = result;
            Ui.Post(this, delegate
            {
                _discovering = false;
                _discovered = true;
                Session.Profiles = captured;
                RebuildFromSession();
                Host.RefreshChrome();
            });
        }

        private void RebuildFromSession()
        {
            StopSizeCalculator();

            _users.Clear();
            _userList.BeginUpdate();
            _userList.Items.Clear();

            if (Session.Selection.Exclusions == null)
            {
                Session.Selection.Exclusions = ExclusionRules.CreateDefault();
            }
            Session.Selection.IncludeAppData = _appDataCheck.Checked;

            ProfileEnumerationResult profiles = Session.Profiles;
            if (profiles != null)
            {
                for (int i = 0; i < profiles.Profiles.Count; i++)
                {
                    UserProfile profile = profiles.Profiles[i];
                    UserSelection selection = SelectionBuilder.BuildFor(profile, _appDataCheck.Checked);
                    selection.Selected = true;
                    _users.Add(selection);

                    ListViewItem item = new ListViewItem(profile.AccountName);
                    item.SubItems.Add(LongPath.ToDisplay(profile.ProfilePath));
                    item.SubItems.Add("-");
                    item.SubItems.Add("counting...");
                    item.SubItems.Add(BuildNote(selection));
                    item.Tag = selection;
                    item.Checked = true;
                    _userList.Items.Add(item);
                }
            }

            _userList.EndUpdate();

            Session.Selection.Users = _users;

            UpdateStatus();

            if (_userList.Items.Count > 0)
            {
                _userList.Items[0].Selected = true;
                ShowFoldersForSelectedUser();
            }

            StartSizeCalculator();
            Host.RefreshChrome();
        }

        private string BuildNote(UserSelection selection)
        {
            StringBuilder sb = new StringBuilder();
            if (selection.Profile.IsCurrentUser)
            {
                sb.Append("You are signed in as this user - some files may be locked");
            }
            else if (selection.Profile.IsHiveLoaded)
            {
                sb.Append("Signed in - some files may be locked");
            }

            if (selection.Roots.Count == 0)
            {
                if (sb.Length > 0) { sb.Append("; "); }
                sb.Append("no standard folders found");
            }

            return sb.ToString();
        }

        private void UpdateStatus()
        {
            // The category buttons can only work on Tier B roots, which only exist once the
            // app-data checkbox has caused them to be detected.
            bool anyBrowser = false;
            bool anyMail = false;
            for (int u = 0; u < _users.Count && !(anyBrowser && anyMail); u++)
            {
                List<SelectionRoot> roots = _users[u].Roots;
                for (int r = 0; r < roots.Count; r++)
                {
                    if (roots[r].Tier != SelectionTier.AppData || !roots[r].Exists) { continue; }
                    if (roots[r].Category == AppDataCategory.Browser) { anyBrowser = true; }
                    else if (roots[r].Category == AppDataCategory.Mail) { anyMail = true; }
                }
            }
            _browsersButton.Enabled = anyBrowser;
            _mailButton.Enabled = anyMail;

            ProfileEnumerationResult profiles = Session.Profiles;
            if (profiles == null)
            {
                _statusLabel.Text = string.Empty;
                return;
            }

            int shown = profiles.Profiles.Count;
            int hidden = profiles.Filtered.Count;

            _statusLabel.Visible = hidden == 0;
            _filteredLink.Visible = hidden > 0;

            if (hidden > 0)
            {
                _filteredLink.Text = shown + " account(s) found. " + hidden
                                     + " system or unusable profile(s) were left out - click to see why.";
                _filteredLink.LinkArea = new LinkArea(_filteredLink.Text.Length - 22, 22);
            }
            else
            {
                _statusLabel.Text = shown + " account(s) found.";
            }

            if (profiles.Warnings.Count > 0)
            {
                _statusLabel.Visible = true;
                _statusLabel.Text = profiles.Warnings[0];
                _statusLabel.ForeColor = Color.FromArgb(168, 0, 0);
            }
        }

        private void FilteredLinkOnClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            ProfileEnumerationResult profiles = Session.Profiles;
            if (profiles == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("These profiles were found in the registry but not offered:");
            sb.AppendLine();
            for (int i = 0; i < profiles.Filtered.Count; i++)
            {
                FilteredProfile filtered = profiles.Filtered[i];
                sb.AppendLine("  " + (filtered.ProfilePath ?? filtered.Sid));
                sb.AppendLine("      " + filtered.Reason);
            }

            MessageBox.Show(this, sb.ToString(), "Profiles that were left out",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- size calculation --------------------------------------------------

        private void StartSizeCalculator()
        {
            StopSizeCalculator();
            if (_users.Count == 0)
            {
                return;
            }

            _sizeCalculator = new SelectionSizeCalculator(Session.Selection.Exclusions,
                                                          Session.Selection.IncludeHidden,
                                                          Session.Selection.IncludeSystem);
            _sizeCalculator.Start(_users, delegate
            {
                Interlocked.Exchange(ref _pendingRefresh, 1);
            });
        }

        private void StopSizeCalculator()
        {
            SelectionSizeCalculator calculator = _sizeCalculator;
            _sizeCalculator = null;
            if (calculator != null)
            {
                calculator.Dispose();
            }
        }

        private void RefreshTimerOnTick(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _pendingRefresh, 0) == 0)
            {
                return;
            }

            _userList.BeginUpdate();
            for (int i = 0; i < _userList.Items.Count; i++)
            {
                ListViewItem item = _userList.Items[i];
                UserSelection selection = item.Tag as UserSelection;
                if (selection == null || selection.Profile == null)
                {
                    continue;
                }

                item.SubItems[2].Text = selection.Profile.FileCount > 0
                                        ? selection.Profile.FileCount.ToString("N0")
                                        : "-";
                item.SubItems[3].Text = FormatSize(selection);
            }
            _userList.EndUpdate();

            RefreshFolderLabels();
            Host.RefreshChrome();
        }

        private static string FormatSize(UserSelection selection)
        {
            if (selection.Profile.SizeState == SizeState.Failed)
            {
                return "error";
            }
            string value = Format.Bytes(selection.Profile.SizeBytes);
            return selection.Profile.SizeState == SizeState.Done ? value : value + " ...";
        }

        // ---- folder list -------------------------------------------------------

        private void AdvancedOnCheckedChanged(object sender, EventArgs e)
        {
            Session.AdvancedMode = _advancedCheck.Checked;
            _folderPanel.Visible = _advancedCheck.Checked;
            _splitter.Visible = _advancedCheck.Checked;
            if (_advancedCheck.Checked)
            {
                ShowFoldersForSelectedUser();
            }
        }

        private void BrowsersOnClick(object sender, EventArgs e)
        {
            SelectCategory(AppDataCategory.Browser, "browser");
        }

        private void MailOnClick(object sender, EventArgs e)
        {
            SelectCategory(AppDataCategory.Mail, "email");
        }

        /// <summary>
        /// Ticks every detected Tier B root of one category, across every selected account,
        /// and reports how many were found. Nothing is unticked: this adds to the selection
        /// rather than replacing it.
        /// </summary>
        private void SelectCategory(AppDataCategory category, string what)
        {
            int matched = 0;
            for (int u = 0; u < _users.Count; u++)
            {
                UserSelection user = _users[u];
                bool touched = false;

                for (int r = 0; r < user.Roots.Count; r++)
                {
                    SelectionRoot root = user.Roots[r];
                    if (root.Tier != SelectionTier.AppData || root.Category != category || !root.Exists)
                    {
                        continue;
                    }
                    if (!root.Selected)
                    {
                        root.Selected = true;
                        touched = true;
                    }
                    matched++;
                }

                // Selecting data for an account nobody ticked would silently do nothing.
                if (touched && !user.Selected)
                {
                    user.Selected = true;
                }
            }

            if (matched == 0)
            {
                _statusLabel.Text = "No " + what + " data was found on this PC.";
            }
            else
            {
                _statusLabel.Text = "Selected " + matched.ToString(CultureInfo.InvariantCulture)
                                    + " " + what + " item(s). Sizes are still being counted.";
            }

            ShowFoldersForSelectedUser();
            RefreshFolderLabels();
            StartSizeCalculator();
            UpdateStatus();
            Host.RefreshChrome();
        }

        private void AppDataOnCheckedChanged(object sender, EventArgs e)
        {
            if (!_discovered)
            {
                return;
            }
            // Rebuilding is the honest thing to do: the Tier B roots have to be detected on
            // disk, and their sizes counted, before any of them can be ticked.
            RebuildFromSession();
        }

        private void UserListOnSelectedIndexChanged(object sender, EventArgs e)
        {
            ShowFoldersForSelectedUser();
        }

        private void UserListOnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            UserSelection selection = e.Item.Tag as UserSelection;
            if (selection != null)
            {
                selection.Selected = e.Item.Checked;
            }
            Host.RefreshChrome();
        }

        private UserSelection SelectedUser
        {
            get
            {
                if (_userList.SelectedItems.Count == 0)
                {
                    return null;
                }
                return _userList.SelectedItems[0].Tag as UserSelection;
            }
        }

        private bool _suppressFolderEvents;

        private void ShowFoldersForSelectedUser()
        {
            UserSelection user = SelectedUser;

            _suppressFolderEvents = true;
            try
            {
                _folderList.Items.Clear();
                if (user == null)
                {
                    _folderHeading.Text = "Folders";
                    return;
                }

                _folderHeading.Text = "Folders for " + user.Profile.AccountName;
                for (int i = 0; i < user.Roots.Count; i++)
                {
                    _folderList.Items.Add(DescribeRoot(user.Roots[i]), user.Roots[i].Selected);
                }
            }
            finally
            {
                _suppressFolderEvents = false;
            }
        }

        private void RefreshFolderLabels()
        {
            UserSelection user = SelectedUser;
            if (user == null || _folderList.Items.Count != user.Roots.Count)
            {
                return;
            }

            _suppressFolderEvents = true;
            try
            {
                for (int i = 0; i < user.Roots.Count; i++)
                {
                    string text = DescribeRoot(user.Roots[i]);
                    if (!string.Equals(_folderList.Items[i] as string, text, StringComparison.Ordinal))
                    {
                        _folderList.Items[i] = text;
                    }
                }
            }
            finally
            {
                _suppressFolderEvents = false;
            }
        }

        private static string DescribeRoot(SelectionRoot root)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(root.Label);

            if (root.EstimatedBytes >= 0)
            {
                sb.Append("   ").Append(Format.Bytes(root.EstimatedBytes));
                if (root.SizeState == SizeState.Calculating)
                {
                    sb.Append(" ...");
                }
            }

            if (root.Tier == MoveToNewPC.Core.Selection.SelectionTier.AppData)
            {
                sb.Append("   [app data]");
            }

            if (!string.IsNullOrEmpty(root.Note))
            {
                sb.Append("   - ").Append(root.Note);
            }

            return sb.ToString();
        }

        private void FolderListOnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressFolderEvents)
            {
                return;
            }

            UserSelection user = SelectedUser;
            if (user == null || e.Index < 0 || e.Index >= user.Roots.Count)
            {
                return;
            }

            user.Roots[e.Index].Selected = e.NewValue == CheckState.Checked;
            Host.RefreshChrome();
        }

        private void FolderListOnSelectedIndexChanged(object sender, EventArgs e)
        {
            UserSelection user = SelectedUser;
            if (user == null || _folderList.SelectedIndex < 0 || _folderList.SelectedIndex >= user.Roots.Count)
            {
                return;
            }

            SelectionRoot root = user.Roots[_folderList.SelectedIndex];
            Host.SetStatus(LongPath.ToDisplay(root.SourcePath));
        }

        private void RefreshOnClick(object sender, EventArgs e)
        {
            Session.Profiles = null;
            StartDiscovery();
        }

        private bool HasAnythingSelected()
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (!_users[i].Selected)
                {
                    continue;
                }
                for (int r = 0; r < _users[i].Roots.Count; r++)
                {
                    if (_users[i].Roots[r].Selected && _users[i].Roots[r].Exists)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public override WizardPage OnNext()
        {
            StopSizeCalculator();
            Session.Selection.Users = _users;
            Session.Selection.IncludeAppData = _appDataCheck.Checked;
            return new OptionsPage();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                StopSizeCalculator();
            }
            base.Dispose(disposing);
        }
    }
}
