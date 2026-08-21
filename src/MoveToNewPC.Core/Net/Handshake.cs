using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MoveToNewPC.Core.Crypto;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Net
{
    /// <summary>Raised when a handshake cannot complete. The message is shown to the operator.</summary>
    public sealed class HandshakeException : Exception
    {
        /// <summary>True when the pairing code was wrong, as opposed to a protocol failure.</summary>
        public bool WrongCode;

        public HandshakeException(string message) : base(message) { }

        public HandshakeException(string message, bool wrongCode) : base(message)
        {
            WrongCode = wrongCode;
        }
    }

    /// <summary>
    /// The pairing handshake of docs/PROTOCOL.md §3.
    ///
    /// ECDH at the application layer rather than SslStream, because Vista has no TLS 1.2 at
    /// all and this product supports Vista. The pairing code is what authenticates the
    /// exchange: without it an active man in the middle would be undetectable, which is why
    /// the proof step is not optional and not skippable.
    /// </summary>
    public static class Handshake
    {
        public sealed class Result
        {
            public PackageCrypto.SessionKeys SendKeys;
            public PackageCrypto.SessionKeys ReceiveKeys;
            public string PeerMachineName;
        }

        /// <summary>Sender side: connect, exchange keys, prove the code, then send.</summary>
        public static Result RunAsSender(Stream stream, string localMachine, string pairingCode)
        {
            using (ECDiffieHellmanCng ecdh = CreateEcdh())
            {
                byte[] localPublic = ecdh.PublicKey.ToByteArray();

                WriteHello(stream, localMachine, localPublic);

                string peerMachine;
                byte[] peerPublic;
                ReadHello(stream, out peerMachine, out peerPublic);

                byte[] transcript = NetworkProtocol.ComputeTranscript(
                    NetworkProtocol.Version, localPublic, peerPublic, localMachine, peerMachine, pairingCode);

                byte[] secret = DeriveSecret(ecdh, peerPublic);

                PackageCrypto.SessionKeys senderToReceiver;
                PackageCrypto.SessionKeys receiverToSender;
                NetworkProtocol.DeriveDirectionalKeys(secret, transcript,
                                                      out senderToReceiver, out receiverToSender);

                // Prove first, then verify theirs. Either order is safe; this one means a
                // wrong code is detected by the receiver, which is where the operator is
                // looking at the code on screen.
                byte[] ourProof = NetworkProtocol.ComputeProof(
                    senderToReceiver.MacKey, transcript, NetworkProtocol.SenderProofLabel);
                WriteBlock(stream, ourProof);
                stream.Flush();

                byte[] theirProof = ReadBlock(stream, PackageCrypto.MacBytes);
                byte[] expected = NetworkProtocol.ComputeProof(
                    receiverToSender.MacKey, transcript, NetworkProtocol.ReceiverProofLabel);

                if (!Format.ConstantTimeEquals(theirProof, expected))
                {
                    throw new HandshakeException(
                        "The other PC could not prove it knows the pairing code. Check the code shown "
                        + "on the new PC and try again.", true);
                }

                Result result = new Result();
                result.SendKeys = senderToReceiver;
                result.ReceiveKeys = receiverToSender;
                result.PeerMachineName = peerMachine;
                Log.Info("Handshake completed with " + peerMachine);
                return result;
            }
        }

        /// <summary>Receiver side: accept, exchange keys, check the sender's proof.</summary>
        public static Result RunAsReceiver(Stream stream, string localMachine, string pairingCode)
        {
            using (ECDiffieHellmanCng ecdh = CreateEcdh())
            {
                byte[] localPublic = ecdh.PublicKey.ToByteArray();

                string peerMachine;
                byte[] peerPublic;
                ReadHello(stream, out peerMachine, out peerPublic);

                WriteHello(stream, localMachine, localPublic);
                stream.Flush();

                // Sender's key goes first in the transcript on both sides.
                byte[] transcript = NetworkProtocol.ComputeTranscript(
                    NetworkProtocol.Version, peerPublic, localPublic, peerMachine, localMachine, pairingCode);

                byte[] secret = DeriveSecret(ecdh, peerPublic);

                PackageCrypto.SessionKeys senderToReceiver;
                PackageCrypto.SessionKeys receiverToSender;
                NetworkProtocol.DeriveDirectionalKeys(secret, transcript,
                                                      out senderToReceiver, out receiverToSender);

                byte[] theirProof = ReadBlock(stream, PackageCrypto.MacBytes);
                byte[] expected = NetworkProtocol.ComputeProof(
                    senderToReceiver.MacKey, transcript, NetworkProtocol.SenderProofLabel);

                if (!Format.ConstantTimeEquals(theirProof, expected))
                {
                    // Deliberately vague on the wire, specific on screen: the operator needs
                    // to know it was the code, the attacker should learn nothing.
                    throw new HandshakeException("Wrong pairing code.", true);
                }

                byte[] ourProof = NetworkProtocol.ComputeProof(
                    receiverToSender.MacKey, transcript, NetworkProtocol.ReceiverProofLabel);
                WriteBlock(stream, ourProof);
                stream.Flush();

                Result result = new Result();
                result.SendKeys = receiverToSender;
                result.ReceiveKeys = senderToReceiver;
                result.PeerMachineName = peerMachine;
                Log.Info("Handshake completed with " + peerMachine);
                return result;
            }
        }

        private static ECDiffieHellmanCng CreateEcdh()
        {
            // P-256 through CNG: present from Vista onwards, which is the whole supported
            // range. ECDiffieHellmanCng is not available on the managed-only stack, but CNG
            // is also what keeps this working with the FIPS policy enabled.
            ECDiffieHellmanCng ecdh = new ECDiffieHellmanCng(256);
            ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            ecdh.HashAlgorithm = CngAlgorithm.Sha256;
            return ecdh;
        }

        private static byte[] DeriveSecret(ECDiffieHellmanCng ecdh, byte[] peerPublicBlob)
        {
            try
            {
                using (CngKey peerKey = CngKey.Import(peerPublicBlob, CngKeyBlobFormat.EccPublicBlob))
                {
                    if (peerKey.Algorithm != CngAlgorithm.ECDiffieHellmanP256)
                    {
                        throw new HandshakeException("The other PC offered an unexpected key type.");
                    }
                    return ecdh.DeriveKeyMaterial(peerKey);
                }
            }
            catch (CryptographicException ex)
            {
                // A malformed blob must be a clean failure, not an unhandled crash.
                throw new HandshakeException("The other PC sent a key this build could not read: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new HandshakeException("The other PC sent a malformed key: " + ex.Message);
            }
        }

        private static void WriteHello(Stream stream, string machineName, byte[] publicKey)
        {
            stream.Write(NetworkProtocol.Magic, 0, NetworkProtocol.Magic.Length);

            byte[] version = new byte[4];
            PackageCrypto.WriteInt32(version, 0, NetworkProtocol.Version);
            stream.Write(version, 0, 4);

            WriteBlock(stream, Encoding.UTF8.GetBytes(machineName ?? string.Empty));
            WriteBlock(stream, publicKey);
        }

        private static void ReadHello(Stream stream, out string machineName, out byte[] publicKey)
        {
            byte[] magic = new byte[NetworkProtocol.Magic.Length];
            if (!PackageCrypto.ReadExactly(stream, magic, 0, magic.Length))
            {
                throw new HandshakeException("The other PC closed the connection during the handshake.");
            }
            for (int i = 0; i < magic.Length; i++)
            {
                if (magic[i] != NetworkProtocol.Magic[i])
                {
                    throw new HandshakeException(
                        "Whatever answered on that address is not MoveToNewPC.");
                }
            }

            byte[] versionBytes = new byte[4];
            if (!PackageCrypto.ReadExactly(stream, versionBytes, 0, 4))
            {
                throw new HandshakeException("The other PC closed the connection during the handshake.");
            }
            int version = PackageCrypto.ReadInt32(versionBytes, 0);
            if (version != NetworkProtocol.Version)
            {
                throw new HandshakeException(
                    "The other PC is running a different version of MoveToNewPC (protocol "
                    + version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", this one speaks "
                    + NetworkProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "). Use the same build on both machines.");
            }

            byte[] nameBytes = ReadVariableBlock(stream, NetworkProtocol.MaxNameBytes);
            machineName = Encoding.UTF8.GetString(nameBytes);
            publicKey = ReadVariableBlock(stream, NetworkProtocol.MaxPublicKeyBytes);
        }

        private static void WriteBlock(Stream stream, byte[] data)
        {
            byte[] length = new byte[4];
            PackageCrypto.WriteInt32(length, 0, data.Length);
            stream.Write(length, 0, 4);
            stream.Write(data, 0, data.Length);
        }

        /// <summary>Reads a length-prefixed block, refusing an implausible length before allocating.</summary>
        private static byte[] ReadVariableBlock(Stream stream, int maximum)
        {
            byte[] lengthBytes = new byte[4];
            if (!PackageCrypto.ReadExactly(stream, lengthBytes, 0, 4))
            {
                throw new HandshakeException("The other PC closed the connection during the handshake.");
            }

            int length = PackageCrypto.ReadInt32(lengthBytes, 0);
            if (length < 0 || length > maximum)
            {
                throw new HandshakeException("The other PC sent an implausibly large handshake field.");
            }

            byte[] data = new byte[length];
            if (length > 0 && !PackageCrypto.ReadExactly(stream, data, 0, length))
            {
                throw new HandshakeException("The other PC closed the connection during the handshake.");
            }
            return data;
        }

        private static byte[] ReadBlock(Stream stream, int expectedLength)
        {
            byte[] data = ReadVariableBlock(stream, expectedLength);
            if (data.Length != expectedLength)
            {
                throw new HandshakeException("The other PC sent a malformed proof.");
            }
            return data;
        }
    }
}
