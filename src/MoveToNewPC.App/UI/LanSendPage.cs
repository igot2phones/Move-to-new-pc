using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Net;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Sender side of the LAN transport: find the new PC (or type its address), enter the
    /// pairing code it is showing, and connect.
    /// </summary>
    public sealed class LanSendPage : WizardPage
    {
        private ListBox _foundList;
        private TextBox _addressBox;
        private TextBox _codeBox;
        private Button _connectButton;
        private Label _statusLabel;
        private Label _warningLabel;
        // Qualified: System.Threading is also in scope here and also has a Timer.
        private System.Windows.Forms.Timer _refreshTimer;

        private DiscoveryListener _discovery;
        private Thread _worker;
        private volatile bool _connecting;

        public LanSendPage()
        {
            Build();
        }

        public override string Title
        {
            get { return "Find the new PC"; }
        }

        public override string Subtitle
        {
            get { return "The new PC should be showing a six-digit pairing code. Type it in below."; }
        }

        public override bool CanGoNext
        {
            get { return false; }      // Connect advances the wizard
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            Label foundPrompt = new Label();
            foundPrompt.Text = "PCs waiting to receive:";
            foundPrompt.Location = new Point(24, 12);
            foundPrompt.Size = new Size(300, 20);
            Controls.Add(foundPrompt);

            _foundList = new ListBox();
            _foundList.Location = new Point(24, 34);
            _foundList.Size = new Size(500, 92);
            _foundList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _foundList.SelectedIndexChanged += FoundOnSelected;
            Controls.Add(_foundList);

            Label addressPrompt = new Label();
            addressPrompt.Text = "Or type its name or IP address:";
            addressPrompt.Location = new Point(24, 138);
            addressPrompt.Size = new Size(300, 20);
            Controls.Add(addressPrompt);

            _addressBox = new TextBox();
            _addressBox.Location = new Point(24, 160);
            _addressBox.Size = new Size(300, 22);
            Controls.Add(_addressBox);

            Label codePrompt = new Label();
            codePrompt.Text = "Pairing code shown on the new PC:";
            codePrompt.Location = new Point(344, 138);
            codePrompt.Size = new Size(300, 20);
            Controls.Add(codePrompt);

            _codeBox = new TextBox();
            _codeBox.Location = new Point(344, 160);
            _codeBox.Size = new Size(180, 22);
            _codeBox.Font = new Font(Ui.DefaultFont.FontFamily, 12f, FontStyle.Bold);
            _codeBox.MaxLength = 16;
            Controls.Add(_codeBox);

            _connectButton = Ui.MakeButton("&Connect", 140);
            _connectButton.Location = new Point(24, 198);
            _connectButton.Click += ConnectOnClick;
            Controls.Add(_connectButton);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(24, 236);
            _statusLabel.Size = new Size(720, 40);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_statusLabel);

            _warningLabel = new Label();
            _warningLabel.Location = new Point(24, 278);
            _warningLabel.Size = new Size(720, 48);
            _warningLabel.ForeColor = Color.FromArgb(168, 0, 0);
            _warningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_warningLabel);

            Label explain = new Label();
            explain.Location = new Point(24, 334);
            explain.Size = new Size(720, 60);
            explain.ForeColor = SystemColors.GrayText;
            explain.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            explain.Text = "Nothing is sent until both PCs have proved they know the same code. If the code "
                           + "is wrong the connection is refused and no files leave this PC."
                           + Environment.NewLine
                           + "With a direct Ethernet cable, give both PCs up to a minute to sort out addresses.";
            Controls.Add(explain);
        }

        public override void OnActivated()
        {
            try
            {
                _discovery = new DiscoveryListener();
                _discovery.Start();
            }
            catch (Exception ex)
            {
                // Discovery is a convenience; typing the address still works.
                Log.Warn("Could not listen for beacons: " + ex.Message);
                _statusLabel.Text = "Automatic discovery is unavailable - type the address instead.";
            }

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 1000;
            _refreshTimer.Tick += RefreshOnTick;
            _refreshTimer.Start();
        }

        private void RefreshOnTick(object sender, EventArgs e)
        {
            if (_discovery == null || _connecting)
            {
                return;
            }

            List<DiscoveredReceiver> found = _discovery.GetCurrent();

            // Only rebuild when something changed, so the selection does not flicker away
            // from under the operator once a second.
            if (found.Count == _foundList.Items.Count)
            {
                bool same = true;
                for (int i = 0; i < found.Count; i++)
                {
                    if (!string.Equals(found[i].ToString(), _foundList.Items[i].ToString(), StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) { return; }
            }

            object selected = _foundList.SelectedItem;
            _foundList.BeginUpdate();
            _foundList.Items.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                _foundList.Items.Add(found[i]);
            }
            if (selected != null)
            {
                int index = _foundList.FindStringExact(selected.ToString());
                if (index >= 0) { _foundList.SelectedIndex = index; }
            }
            _foundList.EndUpdate();
        }

        private void FoundOnSelected(object sender, EventArgs e)
        {
            DiscoveredReceiver receiver = _foundList.SelectedItem as DiscoveredReceiver;
            if (receiver != null)
            {
                _addressBox.Text = receiver.Address;
            }
        }

        private void ConnectOnClick(object sender, EventArgs e)
        {
            if (_connecting)
            {
                return;
            }

            string address = _addressBox.Text.Trim();
            string code = NetworkProtocol.NormaliseCode(_codeBox.Text);

            _warningLabel.Text = string.Empty;

            if (address.Length == 0)
            {
                _warningLabel.Text = "Choose a PC from the list, or type its name or IP address.";
                return;
            }
            if (code.Length != 6)
            {
                _warningLabel.Text = "The pairing code is six digits. Type the code shown on the new PC.";
                return;
            }

            _connecting = true;
            _connectButton.Enabled = false;
            _statusLabel.Text = "Connecting to " + address + "...";

            string capturedAddress = address;
            string capturedCode = code;

            _worker = new Thread(delegate()
            {
                SecureChannel channel = null;
                string failure = null;
                try
                {
                    channel = SecureChannel.Connect(capturedAddress, NetworkProtocol.TransferPort,
                                                    Environment.MachineName, capturedCode);
                }
                catch (HandshakeException ex)
                {
                    failure = ex.Message;
                }
                catch (Exception ex)
                {
                    failure = "Could not reach that PC: " + ex.Message;
                }

                SecureChannel captured = channel;
                string capturedFailure = failure;

                if (IsDisposed || Disposing)
                {
                    if (captured != null) { captured.Dispose(); }
                    return;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnConnectFinished(captured, capturedFailure); });
                }
                catch (InvalidOperationException)
                {
                    if (captured != null) { captured.Dispose(); }
                }
            });
            _worker.IsBackground = true;
            _worker.Name = "MTNPC connect";
            _worker.Start();
        }

        private void OnConnectFinished(SecureChannel channel, string failure)
        {
            _connecting = false;
            _connectButton.Enabled = true;

            if (channel == null)
            {
                _statusLabel.Text = string.Empty;
                _warningLabel.Text = failure ?? "Could not connect.";
                return;
            }

            Session.Channel = channel;
            Session.PeerMachineName = channel.PeerMachineName;
            _statusLabel.Text = "Paired with " + channel.PeerMachineName + ".";

            Log.Info("Paired with " + channel.PeerMachineName + "; starting the transfer.");
            Host.Navigate(new TransferPage());
        }

        public override void OnDeactivating()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
            if (_discovery != null)
            {
                _discovery.Dispose();
                _discovery = null;
            }
        }
    }
}
