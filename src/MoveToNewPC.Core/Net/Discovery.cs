using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Core.Net
{
    /// <summary>One receiver seen on the network.</summary>
    public sealed class DiscoveredReceiver
    {
        public string MachineName;
        public string Address;
        public int Port;
        public DateTime LastSeenUtc;

        public override string ToString()
        {
            return MachineName + "  (" + Address + ")";
        }
    }

    /// <summary>
    /// UDP broadcast so the old PC can find the new one without anybody typing an IP
    /// address.
    ///
    /// The beacon carries the protocol version, the machine name and the TCP port - and
    /// nothing else. No user names, no paths, no file lists, and above all no pairing code:
    /// the beacon is broadcast in clear to the whole subnet, so anything in it is public.
    /// See docs/PROTOCOL.md §4.
    /// </summary>
    public sealed class DiscoveryBeacon : IDisposable
    {
        private UdpClient _socket;
        private Thread _thread;
        private volatile bool _stop;
        private readonly string _machineName;
        private readonly int _port;

        public DiscoveryBeacon(string machineName, int transferPort)
        {
            _machineName = machineName;
            _port = transferPort;
        }

        public void Start()
        {
            _socket = new UdpClient();
            _socket.EnableBroadcast = true;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "MTNPC beacon";
            _thread.Start();
        }

        private void Loop()
        {
            byte[] payload = BuildBeacon(_machineName, _port);
            IPEndPoint destination = new IPEndPoint(IPAddress.Broadcast, NetworkProtocol.DiscoveryPort);

            while (!_stop)
            {
                try
                {
                    _socket.Send(payload, payload.Length, destination);
                }
                catch (SocketException ex)
                {
                    // A machine with no usable interface yet (cable just plugged in) throws
                    // here. Keep trying: link-local addressing can take up to a minute.
                    Log.Debug("Beacon send failed: " + ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                for (int waited = 0; waited < NetworkProtocol.BeaconIntervalMs && !_stop; waited += 100)
                {
                    Thread.Sleep(100);
                }
            }
        }

        internal static byte[] BuildBeacon(string machineName, int port)
        {
            byte[] name = Encoding.UTF8.GetBytes(machineName ?? string.Empty);
            if (name.Length > NetworkProtocol.MaxNameBytes)
            {
                Array.Resize(ref name, NetworkProtocol.MaxNameBytes);
            }

            byte[] payload = new byte[NetworkProtocol.Magic.Length + 4 + 4 + 4 + name.Length];
            int offset = 0;
            Buffer.BlockCopy(NetworkProtocol.Magic, 0, payload, offset, NetworkProtocol.Magic.Length);
            offset += NetworkProtocol.Magic.Length;
            PackageCrypto.WriteInt32(payload, offset, NetworkProtocol.Version);
            offset += 4;
            PackageCrypto.WriteInt32(payload, offset, port);
            offset += 4;
            PackageCrypto.WriteInt32(payload, offset, name.Length);
            offset += 4;
            Buffer.BlockCopy(name, 0, payload, offset, name.Length);
            return payload;
        }

        public void Dispose()
        {
            _stop = true;
            if (_socket != null)
            {
                try { _socket.Close(); } catch (Exception) { }
                _socket = null;
            }
            if (_thread != null)
            {
                try { _thread.Join(1500); } catch (Exception) { }
                _thread = null;
            }
        }
    }

    /// <summary>Listens for beacons and keeps a de-duplicated list of what it has seen.</summary>
    public sealed class DiscoveryListener : IDisposable
    {
        private UdpClient _socket;
        private Thread _thread;
        private volatile bool _stop;
        private readonly object _lock = new object();
        private readonly Dictionary<string, DiscoveredReceiver> _seen =
            new Dictionary<string, DiscoveredReceiver>(StringComparer.OrdinalIgnoreCase);

        public void Start()
        {
            _socket = new UdpClient();
            _socket.ExclusiveAddressUse = false;
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkProtocol.DiscoveryPort));

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "MTNPC discovery";
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] payload = _socket.Receive(ref from);

                    DiscoveredReceiver receiver = Parse(payload, from);
                    if (receiver == null)
                    {
                        continue;
                    }

                    lock (_lock)
                    {
                        _seen[receiver.Address] = receiver;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (_stop) { return; }
                }
            }
        }

        private static DiscoveredReceiver Parse(byte[] payload, IPEndPoint from)
        {
            int fixedSize = NetworkProtocol.Magic.Length + 12;
            if (payload == null || payload.Length < fixedSize)
            {
                return null;
            }
            for (int i = 0; i < NetworkProtocol.Magic.Length; i++)
            {
                if (payload[i] != NetworkProtocol.Magic[i])
                {
                    return null;
                }
            }

            int offset = NetworkProtocol.Magic.Length;
            int version = PackageCrypto.ReadInt32(payload, offset);
            offset += 4;
            int port = PackageCrypto.ReadInt32(payload, offset);
            offset += 4;
            int nameLength = PackageCrypto.ReadInt32(payload, offset);
            offset += 4;

            if (version != NetworkProtocol.Version) { return null; }
            if (port <= 0 || port > 65535) { return null; }
            if (nameLength < 0 || nameLength > NetworkProtocol.MaxNameBytes) { return null; }
            if (payload.Length < offset + nameLength) { return null; }

            DiscoveredReceiver receiver = new DiscoveredReceiver();
            receiver.MachineName = Encoding.UTF8.GetString(payload, offset, nameLength);
            receiver.Address = from.Address.ToString();
            receiver.Port = port;
            receiver.LastSeenUtc = DateTime.UtcNow;
            return receiver;
        }

        /// <summary>Receivers heard from in the last few seconds.</summary>
        public List<DiscoveredReceiver> GetCurrent()
        {
            List<DiscoveredReceiver> list = new List<DiscoveredReceiver>();
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-6);

            lock (_lock)
            {
                foreach (KeyValuePair<string, DiscoveredReceiver> pair in _seen)
                {
                    if (pair.Value.LastSeenUtc >= cutoff)
                    {
                        list.Add(pair.Value);
                    }
                }
            }
            return list;
        }

        public void Dispose()
        {
            _stop = true;
            if (_socket != null)
            {
                try { _socket.Close(); } catch (Exception) { }
                _socket = null;
            }
            if (_thread != null)
            {
                try { _thread.Join(1500); } catch (Exception) { }
                _thread = null;
            }
        }
    }
}
