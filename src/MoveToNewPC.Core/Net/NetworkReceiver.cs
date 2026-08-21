using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Package;
using MoveToNewPC.Core.Transfer;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Net
{
    /// <summary>
    /// Listens for one sender, pairs with it, and writes what arrives into a sink.
    ///
    /// Exactly one session is ever active: a second connection is closed immediately rather
    /// than queued, because two senders writing into one destination folder would race.
    /// Three wrong pairing codes tear the listener down (docs/PROTOCOL.md §3 step 5).
    /// </summary>
    public sealed class NetworkReceiver : IDisposable
    {
        private TcpListener _listener;
        private DiscoveryBeacon _beacon;
        private bool _disposed;

        public string PairingCode { get; private set; }
        public int Port { get; private set; }

        /// <summary>Raised on the listener thread when a sender pairs successfully.</summary>
        public event EventHandler<PeerEventArgs> PeerConnected;

        /// <summary>Raised when a connection attempt failed. Includes wrong-code attempts.</summary>
        public event EventHandler<PeerEventArgs> AttemptFailed;

        public sealed class PeerEventArgs : EventArgs
        {
            public string MachineName;
            public string Message;
            public int AttemptsRemaining;
        }

        public NetworkReceiver(int port)
        {
            Port = port <= 0 ? NetworkProtocol.TransferPort : port;
            PairingCode = NetworkProtocol.NewPairingCode();
        }

        public void Start(bool announce)
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();

            Log.Info("Listening for a sender on port "
                     + Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (announce)
            {
                _beacon = new DiscoveryBeacon(Environment.MachineName, Port);
                _beacon.Start();
            }
        }

        /// <summary>
        /// Blocks until one sender pairs successfully, the token is cancelled, or the
        /// attempt limit is reached. Returns the paired channel, which the caller owns.
        ///
        /// Pairing is deliberately separate from the transfer: the UI wants to show the
        /// code while waiting and then hand over to its progress screen.
        /// </summary>
        public SecureChannel AcceptOnePeer(CancellationToken cancellation)
        {
            int attemptsLeft = NetworkProtocol.MaxPairingAttempts;

            while (attemptsLeft > 0 && !cancellation.IsCancellationRequested)
            {
                TcpClient client = AcceptWithCancellation(cancellation);
                if (client == null)
                {
                    break;
                }

                SecureChannel channel;
                try
                {
                    channel = SecureChannel.Accept(client, Environment.MachineName, PairingCode);
                }
                catch (HandshakeException ex)
                {
                    attemptsLeft--;
                    Log.Warn("Pairing attempt failed: " + ex.Message + " ("
                             + attemptsLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)
                             + " left)");
                    RaiseFailed(ex.Message, attemptsLeft);
                    try { client.Close(); } catch (Exception) { }
                    continue;
                }
                catch (Exception ex)
                {
                    attemptsLeft--;
                    Log.Warn("Connection failed before pairing: " + ex.Message);
                    RaiseFailed(ex.Message, attemptsLeft);
                    try { client.Close(); } catch (Exception) { }
                    continue;
                }

                // Paired. Stop advertising and stop listening: one session only, never queued.
                StopAnnouncing();
                RaiseConnected(channel.PeerMachineName);
                return channel;
            }

            if (cancellation.IsCancellationRequested)
            {
                return null;
            }
            if (attemptsLeft <= 0)
            {
                throw new HandshakeException(
                    "Three connection attempts failed. Start again on both PCs to get a new pairing code.");
            }
            return null;
        }

        /// <summary>
        /// Convenience for callers that just want the whole thing done - used by the tests.
        /// The UI drives the two halves separately.
        /// </summary>
        public TransferResult ReceiveInto(SecureChannel channel, ITransferSink sink,
                                          ITransferObserver observer,
                                          Action<TransferManifestInfo> onManifest,
                                          CancellationToken cancellation, PauseGate gate)
        {
            RecordRestorer restorer = new RecordRestorer(channel.Reader);
            restorer.ReadHeader();

            if (onManifest != null)
            {
                TransferManifestInfo info = new TransferManifestInfo();
                info.Manifest = restorer.Manifest;
                info.PeerMachineName = channel.PeerMachineName;
                onManifest(info);
            }

            return restorer.Restore(sink, observer, cancellation, gate);
        }

        /// <summary>
        /// TcpListener has no cancellable Accept on this runtime, so poll Pending() instead
        /// of blocking - otherwise Cancel would not be able to stop the wait.
        /// </summary>
        private TcpClient AcceptWithCancellation(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (_listener.Pending())
                    {
                        return _listener.AcceptTcpClient();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                catch (SocketException ex)
                {
                    Log.Warn("Accept failed: " + ex.Message);
                    return null;
                }
                Thread.Sleep(150);
            }
            return null;
        }

        private void StopAnnouncing()
        {
            if (_beacon != null)
            {
                _beacon.Dispose();
                _beacon = null;
            }
            if (_listener != null)
            {
                try { _listener.Stop(); } catch (Exception) { }
            }
        }

        private void RaiseConnected(string machine)
        {
            EventHandler<PeerEventArgs> handler = PeerConnected;
            if (handler != null)
            {
                PeerEventArgs args = new PeerEventArgs();
                args.MachineName = machine;
                handler(this, args);
            }
        }

        private void RaiseFailed(string message, int attemptsLeft)
        {
            EventHandler<PeerEventArgs> handler = AttemptFailed;
            if (handler != null)
            {
                PeerEventArgs args = new PeerEventArgs();
                args.Message = message;
                args.AttemptsRemaining = attemptsLeft;
                handler(this, args);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopAnnouncing();
            _listener = null;
        }
    }

    /// <summary>What the receiver learned from the incoming header, for the UI to show.</summary>
    public sealed class TransferManifestInfo
    {
        public MoveToNewPC.Core.Manifests.TransferManifest Manifest;
        public string PeerMachineName;
    }
}
