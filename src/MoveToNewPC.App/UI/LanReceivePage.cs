using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Net;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Receiver side of the LAN transport: choose where the files land, show the pairing
    /// code, and wait for the old PC to connect.
    ///
    /// The listener runs on a worker thread. Everything it reports comes back through
    /// BeginInvoke, because there is no async/await on this target and touching controls
    /// from the worker would be a race.
    /// </summary>
    public sealed class LanReceivePage : WizardPage
    {
        private TextBox _destinationBox;
        private Button _browseButton;
        private Button _startButton;
        private Label _codeLabel;
        private Label _statusLabel;
        private Label _addressLabel;
        private Label _warningLabel;
        private LayoutChoice _layoutChoice;
        private Label _destinationLabel;

        private NetworkReceiver _receiver;
        private FirewallRule _firewall;
        private Thread _worker;
        private CancellationTokenSource _cancel;
        private volatile bool _listening;
        private volatile bool _paired;

        public override string Title
        {
            get { return "Waiting for the old PC"; }
        }

        public override string Subtitle
        {
            get { return "Choose where the files should land, then type the code below into the old PC."; }
        }

        public override bool CanGoNext
        {
            get { return false; }      // the connection advances the wizard, not the button
        }

        public LanReceivePage()
        {
            Build();
        }

        private void Build()
        {
            Padding = new Padding(24, 16, 24, 12);

            _layoutChoice = new LayoutChoice();
            _layoutChoice.Location = new Point(24, 8);
            _layoutChoice.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _layoutChoice.LayoutChanged += LayoutOnChanged;
            Controls.Add(_layoutChoice);

            _destinationLabel = new Label();
            _destinationLabel.Text = "Put the files into:";
            _destinationLabel.Location = new Point(24, 142);
            _destinationLabel.Size = new Size(320, 20);
            Controls.Add(_destinationLabel);

            _destinationBox = new TextBox();
            _destinationBox.Location = new Point(24, 164);
            _destinationBox.Size = new Size(600, 22);
            _destinationBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_destinationBox);

            _browseButton = Ui.MakeButton("&Browse...", 92);
            _browseButton.Location = new Point(632, 163);
            _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseButton.Click += BrowseOnClick;
            Controls.Add(_browseButton);

            _startButton = Ui.MakeButton("&Start waiting", 140);
            _startButton.Location = new Point(24, 202);
            _startButton.Click += StartOnClick;
            Controls.Add(_startButton);

            Label codePrompt = new Label();
            codePrompt.Text = "Pairing code for the old PC:";
            codePrompt.Location = new Point(24, 244);
            codePrompt.Size = new Size(300, 20);
            Controls.Add(codePrompt);

            _codeLabel = new Label();
            _codeLabel.Location = new Point(24, 266);
            _codeLabel.Size = new Size(400, 46);
            _codeLabel.Font = new Font(Ui.DefaultFont.FontFamily, 24f, FontStyle.Bold);
            _codeLabel.Text = "- - - - - -";
            Controls.Add(_codeLabel);

            _addressLabel = new Label();
            _addressLabel.Location = new Point(24, 322);
            _addressLabel.Size = new Size(720, 20);
            _addressLabel.ForeColor = SystemColors.GrayText;
            _addressLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_addressLabel);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(24, 346);
            _statusLabel.Size = new Size(720, 40);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_statusLabel);

            _warningLabel = new Label();
            _warningLabel.Location = new Point(24, 388);
            _warningLabel.Size = new Size(720, 40);
            _warningLabel.ForeColor = Color.FromArgb(168, 0, 0);
            _warningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_warningLabel);

            Label explain = new Label();
            explain.Location = new Point(24, 430);
            explain.Size = new Size(720, 56);
            explain.ForeColor = SystemColors.GrayText;
            explain.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            explain.Text = "The code proves the two PCs are talking to each other and not to somebody else on "
                           + "the network. Type it into the old PC exactly as shown."
                           + Environment.NewLine
                           + "After three wrong attempts this PC stops listening and you start again with a "
                           + "new code.";
            Controls.Add(explain);
        }

        public override void OnActivated()
        {
            if (string.IsNullOrEmpty(_destinationBox.Text))
            {
                try
                {
                    _destinationBox.Text = LongPath.ToDisplay(LongPath.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "Received from old PC"));
                }
                catch (Exception)
                {
                }
            }
            _addressLabel.Text = "This PC: " + Environment.MachineName + "   " + DescribeLocalAddresses();
        }

        private static string DescribeLocalAddresses()
        {
            try
            {
                IPHostEntry entry = Dns.GetHostEntry(Dns.GetHostName());
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < entry.AddressList.Length; i++)
                {
                    if (entry.AddressList[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (sb.Length > 0) { sb.Append(", "); }
                        sb.Append(entry.AddressList[i].ToString());
                    }
                }
                return sb.Length > 0 ? "(" + sb.ToString() + ")" : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void LayoutOnChanged(object sender, EventArgs e)
        {
            // In matching mode the chosen folder only holds the resume journal.
            _destinationLabel.Text = _layoutChoice.NeedsDestinationFolder
                ? "Put the files into:"
                : "Folder to keep the transfer record in:";
        }

        private void BrowseOnClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where the received files should go";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(_destinationBox.Text) && Directory.Exists(_destinationBox.Text))
                {
                    dialog.SelectedPath = _destinationBox.Text;
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _destinationBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void StartOnClick(object sender, EventArgs e)
        {
            if (_listening)
            {
                return;
            }

            string destination = _destinationBox.Text.Trim();
            if (destination.Length == 0)
            {
                _warningLabel.Text = "Choose a folder for the files first.";
                return;
            }

            try
            {
                int error;
                if (!NativeFile.CreateDirectoryRecursive(destination, out error))
                {
                    _warningLabel.Text = "That folder could not be created: " + NativeFile.DescribeError(error);
                    return;
                }
            }
            catch (Exception ex)
            {
                _warningLabel.Text = "That folder could not be used: " + ex.Message;
                return;
            }

            Session.DestinationFolder = destination;
            Session.Layout = _layoutChoice.SelectedLayout;
            _warningLabel.Text = string.Empty;

            _receiver = new NetworkReceiver(NetworkProtocol.TransferPort);
            _codeLabel.Text = NetworkProtocol.FormatCodeForDisplay(_receiver.PairingCode);

            _firewall = new FirewallRule();
            if (!_firewall.TryAdd(NetworkProtocol.TransferPort, NetworkProtocol.DiscoveryPort))
            {
                _warningLabel.Text = "Could not open the firewall automatically. If the old PC cannot "
                                     + "find this one, allow MoveToNewPC through the firewall by hand.";
            }

            try
            {
                _receiver.Start(true);
            }
            catch (Exception ex)
            {
                _warningLabel.Text = "Could not start listening: " + ex.Message;
                CleanUp();
                return;
            }

            _receiver.AttemptFailed += ReceiverOnAttemptFailed;

            _listening = true;
            _startButton.Enabled = false;
            _destinationBox.Enabled = false;
            _browseButton.Enabled = false;
            _layoutChoice.Enabled = false;
            _statusLabel.Text = "Waiting for the old PC to connect...";

            _cancel = new CancellationTokenSource();
            _worker = new Thread(WaitForPeer);
            _worker.IsBackground = true;
            _worker.Name = "MTNPC pairing";
            _worker.Start();
        }

        private void ReceiverOnAttemptFailed(object sender, NetworkReceiver.PeerEventArgs e)
        {
            NetworkReceiver.PeerEventArgs args = e;
            BeginInvoke((MethodInvoker)delegate
            {
                _warningLabel.Text = args.Message + "  ("
                    + args.AttemptsRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " attempt(s) left)";
            });
        }

        /// <summary>
        /// Waits for one successful pairing. The transfer itself belongs to TransferPage, so
        /// this only carries the channel across and then hands over.
        /// </summary>
        private void WaitForPeer()
        {
            TcpListenerHandoff handoff = new TcpListenerHandoff();
            try
            {
                handoff.Channel = _receiver.AcceptOnePeer(_cancel.Token);
            }
            catch (Exception ex)
            {
                handoff.Failure = ex.Message;
            }

            if (IsDisposed || Disposing)
            {
                if (handoff.Channel != null) { handoff.Channel.Dispose(); }
                return;
            }

            TcpListenerHandoff captured = handoff;
            try
            {
                BeginInvoke((MethodInvoker)delegate { OnPeerSettled(captured); });
            }
            catch (InvalidOperationException)
            {
                // The window went away while we were waiting.
                if (captured.Channel != null) { captured.Channel.Dispose(); }
            }
        }

        private sealed class TcpListenerHandoff
        {
            public SecureChannel Channel;
            public string Failure;
        }

        private void OnPeerSettled(TcpListenerHandoff handoff)
        {
            if (handoff.Channel == null)
            {
                _statusLabel.Text = string.Empty;
                _warningLabel.Text = handoff.Failure ?? "Stopped waiting.";
                _listening = false;
                _startButton.Enabled = true;
                _destinationBox.Enabled = true;
                _browseButton.Enabled = true;
                _layoutChoice.Enabled = true;
                CleanUp();
                return;
            }

            _paired = true;
            Session.Channel = handoff.Channel;
            Session.PeerMachineName = handoff.Channel.PeerMachineName;
            _statusLabel.Text = "Connected to " + handoff.Channel.PeerMachineName + ". Receiving...";

            Log.Info("Paired with " + handoff.Channel.PeerMachineName + "; moving to the transfer screen.");
            Host.Navigate(new TransferPage());
        }

        private void CleanUp()
        {
            if (_receiver != null)
            {
                _receiver.AttemptFailed -= ReceiverOnAttemptFailed;
                _receiver.Dispose();
                _receiver = null;
            }
            if (_firewall != null)
            {
                _firewall.Dispose();
                _firewall = null;
            }
        }

        public override void OnDeactivating()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
            }

            // The listener and its beacon are finished either way once we leave this screen.
            if (_receiver != null)
            {
                _receiver.AttemptFailed -= ReceiverOnAttemptFailed;
                _receiver.Dispose();
                _receiver = null;
            }

            // The firewall rule was only ever needed to be found and connected to. Once we
            // are paired the socket is already open, so it can come down immediately - and
            // it must, because a rule that outlives the transfer is a hole nobody asked for.
            if (_firewall != null)
            {
                _firewall.Dispose();
                _firewall = null;
            }
        }
    }
}
