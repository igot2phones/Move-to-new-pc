using System;
using System.IO;
using System.Net.Sockets;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Core.Net
{
    /// <summary>
    /// An authenticated, encrypted, framed connection between the two PCs.
    ///
    /// Once the handshake has produced per-direction keys, the frame layer is exactly the
    /// one the encrypted package uses (<see cref="SecureBlockWriter"/> /
    /// <see cref="SecureBlockReader"/>) - same construction, same replay protection, one
    /// implementation. See docs/PROTOCOL.md §4 and §6.
    /// </summary>
    public sealed class SecureChannel : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private bool _disposed;

        public SecureBlockWriter Writer { get; private set; }
        public SecureBlockReader Reader { get; private set; }
        public string PeerMachineName { get; private set; }

        private SecureChannel(TcpClient client, NetworkStream stream, Handshake.Result handshake)
        {
            _client = client;
            _stream = stream;
            PeerMachineName = handshake.PeerMachineName;

            Writer = new SecureBlockWriter(stream, handshake.SendKeys);
            Reader = new SecureBlockReader(stream, handshake.ReceiveKeys);
        }

        /// <summary>Sender side: dial the receiver and pair with it.</summary>
        public static SecureChannel Connect(string host, int port, string localMachine, string pairingCode)
        {
            TcpClient client = new TcpClient();
            NetworkStream stream = null;
            try
            {
                client.NoDelay = true;
                client.Connect(host, port);

                stream = client.GetStream();
                stream.ReadTimeout = NetworkProtocol.HandshakeTimeoutMs;
                stream.WriteTimeout = NetworkProtocol.HandshakeTimeoutMs;

                Handshake.Result handshake = Handshake.RunAsSender(stream, localMachine, pairingCode);

                stream.ReadTimeout = NetworkProtocol.IdleTimeoutMs;
                stream.WriteTimeout = NetworkProtocol.IdleTimeoutMs;

                return new SecureChannel(client, stream, handshake);
            }
            catch (Exception)
            {
                if (stream != null) { try { stream.Dispose(); } catch (Exception) { } }
                try { client.Close(); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>Receiver side: pair over an already-accepted socket.</summary>
        public static SecureChannel Accept(TcpClient client, string localMachine, string pairingCode)
        {
            NetworkStream stream = null;
            try
            {
                client.NoDelay = true;
                stream = client.GetStream();
                stream.ReadTimeout = NetworkProtocol.HandshakeTimeoutMs;
                stream.WriteTimeout = NetworkProtocol.HandshakeTimeoutMs;

                Handshake.Result handshake = Handshake.RunAsReceiver(stream, localMachine, pairingCode);

                stream.ReadTimeout = NetworkProtocol.IdleTimeoutMs;
                stream.WriteTimeout = NetworkProtocol.IdleTimeoutMs;

                return new SecureChannel(client, stream, handshake);
            }
            catch (Exception)
            {
                if (stream != null) { try { stream.Dispose(); } catch (Exception) { } }
                try { client.Close(); } catch (Exception) { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (Writer != null)
            {
                try { Writer.Dispose(); }
                catch (Exception ex) { Log.Debug("Closing the channel writer failed: " + ex.Message); }
                Writer = null;
            }
            if (Reader != null) { Reader.Dispose(); Reader = null; }
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch (Exception) { }
                _stream = null;
            }
            if (_client != null)
            {
                try { _client.Close(); } catch (Exception) { }
                _client = null;
            }
        }
    }
}
