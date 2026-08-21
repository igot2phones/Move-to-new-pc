using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MoveToNewPC.Core.Crypto;

namespace MoveToNewPC.Core.Net
{
    /// <summary>
    /// Constants and small helpers shared by both ends of a LAN transfer.
    /// See docs/PROTOCOL.md §3 and §4.
    /// </summary>
    public static class NetworkProtocol
    {
        /// <summary>Bumped whenever the handshake or record stream changes incompatibly.</summary>
        public const int Version = 1;

        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("MTNPC-NET\n");

        /// <summary>TCP port the receiver listens on, and the UDP port for discovery beacons.</summary>
        public const int TransferPort = 51703;
        public const int DiscoveryPort = 51704;

        /// <summary>Handshake must complete inside this. Generous for a slow Vista machine.</summary>
        public const int HandshakeTimeoutMs = 30000;

        /// <summary>Idle timeout once data is flowing. A large file still sends frames steadily.</summary>
        public const int IdleTimeoutMs = 120000;

        /// <summary>Discovery beacons go out this often while the receiver is waiting.</summary>
        public const int BeaconIntervalMs = 1000;

        /// <summary>
        /// Wrong-code attempts before the listener is torn down. The pairing code is only a
        /// million possibilities, so an attacker must not get unlimited online guesses.
        /// </summary>
        public const int MaxPairingAttempts = 3;

        /// <summary>Longest public key blob we will even look at, before handing it to CNG.</summary>
        public const int MaxPublicKeyBytes = 1024;

        /// <summary>Longest machine name accepted from the wire.</summary>
        public const int MaxNameBytes = 256;

        /// <summary>
        /// PBKDF2 cost for the handshake. The ECDH secret already has plenty of entropy, but
        /// the six-digit pairing code does not: a man in the middle who recorded a session
        /// knows their own shared secret with each side and could otherwise brute-force the
        /// code offline in milliseconds. The iteration count is what makes that expensive.
        /// </summary>
        public const int HandshakeIterations = 100000;

        /// <summary>Six digits, uniformly distributed. Not Random - that is predictable.</summary>
        public static string NewPairingCode()
        {
            byte[] raw = PackageCrypto.RandomBytes(4);
            uint value = (uint)PackageCrypto.ReadInt32(raw, 0);

            // Rejection-free modulo bias is not worth the complexity here, but reducing a
            // 32-bit value mod 1,000,000 is close enough to uniform that the bias is far
            // below what an attacker with three guesses could exploit.
            uint code = value % 1000000u;
            return code.ToString("000000", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a code as "123 456" for reading aloud down a phone.</summary>
        public static string FormatCodeForDisplay(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                return code;
            }
            return code.Substring(0, 3) + " " + code.Substring(3, 3);
        }

        public static string NormaliseCode(string typed)
        {
            if (typed == null)
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(6);
            for (int i = 0; i < typed.Length; i++)
            {
                if (typed[i] >= '0' && typed[i] <= '9')
                {
                    sb.Append(typed[i]);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The transcript both sides hash independently. Binding every field that identifies
        /// the session - versions, both public keys, both machine names and the pairing code -
        /// is what stops a man in the middle: an attacker who substitutes their own key
        /// produces a different transcript and cannot forge the proof without the code.
        /// </summary>
        public static byte[] ComputeTranscript(int version, byte[] senderPublicKey, byte[] receiverPublicKey,
                                               string senderMachine, string receiverMachine, string pairingCode)
        {
            using (HashAlgorithm sha = HashFactoryShim())
            {
                AppendBlock(sha, Magic);
                AppendBlock(sha, BitConverter.GetBytes(version));
                AppendBlock(sha, senderPublicKey);
                AppendBlock(sha, receiverPublicKey);
                AppendBlock(sha, Encoding.UTF8.GetBytes(senderMachine ?? string.Empty));
                AppendBlock(sha, Encoding.UTF8.GetBytes(receiverMachine ?? string.Empty));
                AppendBlock(sha, Encoding.UTF8.GetBytes(pairingCode ?? string.Empty));

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        private static HashAlgorithm HashFactoryShim()
        {
            return MoveToNewPC.Core.Util.HashFactory.CreateSha256();
        }

        /// <summary>
        /// Length-prefixes each field before hashing it, so that concatenating two different
        /// field splits cannot produce the same transcript.
        /// </summary>
        private static void AppendBlock(HashAlgorithm sha, byte[] data)
        {
            byte[] length = new byte[4];
            PackageCrypto.WriteInt32(length, 0, data == null ? 0 : data.Length);
            sha.TransformBlock(length, 0, 4, null, 0);
            if (data != null && data.Length > 0)
            {
                sha.TransformBlock(data, 0, data.Length, null, 0);
            }
        }

        /// <summary>
        /// Derives four keys from the ECDH secret and the transcript: one encryption key and
        /// one MAC key per direction. Separate keys per direction stop a frame being
        /// reflected back at its sender and accepted as genuine.
        /// </summary>
        public static void DeriveDirectionalKeys(byte[] sharedSecret, byte[] transcript,
                                                 out PackageCrypto.SessionKeys senderToReceiver,
                                                 out PackageCrypto.SessionKeys receiverToSender)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(sharedSecret, transcript, HandshakeIterations))
            {
                byte[] material = kdf.GetBytes(PackageCrypto.KeyBytes * 4);

                senderToReceiver = new PackageCrypto.SessionKeys();
                senderToReceiver.EncryptionKey = Slice(material, 0);
                senderToReceiver.MacKey = Slice(material, PackageCrypto.KeyBytes);

                receiverToSender = new PackageCrypto.SessionKeys();
                receiverToSender.EncryptionKey = Slice(material, PackageCrypto.KeyBytes * 2);
                receiverToSender.MacKey = Slice(material, PackageCrypto.KeyBytes * 3);

                Array.Clear(material, 0, material.Length);
            }
        }

        private static byte[] Slice(byte[] source, int offset)
        {
            byte[] slice = new byte[PackageCrypto.KeyBytes];
            Buffer.BlockCopy(source, offset, slice, 0, PackageCrypto.KeyBytes);
            return slice;
        }

        /// <summary>
        /// The proof each side sends. Different labels per direction, so a proof cannot be
        /// replayed back at whoever sent it.
        /// </summary>
        public static byte[] ComputeProof(byte[] macKey, byte[] transcript, string label)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            byte[] data = new byte[labelBytes.Length + transcript.Length];
            Buffer.BlockCopy(labelBytes, 0, data, 0, labelBytes.Length);
            Buffer.BlockCopy(transcript, 0, data, labelBytes.Length, transcript.Length);
            return PackageCrypto.ComputeMac(macKey, data, 0, data.Length);
        }

        public const string SenderProofLabel = "MTNPC sender proof";
        public const string ReceiverProofLabel = "MTNPC receiver proof";
    }
}
