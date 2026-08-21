using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Crypto
{
    /// <summary>
    /// Key derivation and the plaintext header of an encrypted package.
    ///
    /// AES-256-CBC with encrypt-then-MAC (HMAC-SHA256), not AES-GCM: AesGcm does not exist
    /// on .NET Framework 4.0 and cannot be relied on down to Vista. See docs/PROTOCOL.md §4 -
    /// the LAN channel uses the same construction, which is why this lives in Core.Crypto
    /// rather than inside the package code.
    /// </summary>
    public static class PackageCrypto
    {
        /// <summary>"MTNPC-PKG" plus a newline, so a text editor shows something sane.</summary>
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("MTNPC-PKG\n");

        public const int FormatVersion = 1;
        public const int SaltBytes = 32;
        public const int KeyBytes = 32;
        public const int MacBytes = 32;
        public const int IvBytes = 16;

        /// <summary>
        /// PBKDF2 cost. Rfc2898DeriveBytes on this runtime is HMAC-SHA1 - that is what the
        /// BCL offers on 4.0 and adding a NuGet KDF is not an option. The iteration count
        /// carries the weight instead. Deriving once per package makes this a one-off cost
        /// even on Vista-era hardware.
        /// </summary>
        public const int DefaultIterations = 100000;

        /// <summary>Hard ceiling on a single encrypted frame, checked before any allocation.</summary>
        public const int MaxFrameBytes = 8 * 1024 * 1024;

        public sealed class SessionKeys
        {
            public byte[] EncryptionKey;
            public byte[] MacKey;
        }

        public static byte[] RandomBytes(int count)
        {
            byte[] buffer = new byte[count];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buffer);
            }
            return buffer;
        }

        /// <summary>
        /// Derives independent encryption and MAC keys from one passphrase. Separate keys
        /// matter: reusing one key for both AES and HMAC is the classic way to turn
        /// encrypt-then-MAC back into something breakable.
        /// </summary>
        public static SessionKeys DeriveKeys(string passphrase, byte[] salt, int iterations)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new ArgumentException("A passphrase is required.", "passphrase");
            }
            if (salt == null || salt.Length < 16)
            {
                throw new ArgumentException("Salt is too short.", "salt");
            }
            if (iterations < 1000)
            {
                throw new ArgumentOutOfRangeException("iterations", "Refusing an unsafely low iteration count.");
            }

            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(passphrase, salt, iterations))
            {
                byte[] material = kdf.GetBytes(KeyBytes + KeyBytes);
                SessionKeys keys = new SessionKeys();
                keys.EncryptionKey = new byte[KeyBytes];
                keys.MacKey = new byte[KeyBytes];
                Buffer.BlockCopy(material, 0, keys.EncryptionKey, 0, KeyBytes);
                Buffer.BlockCopy(material, KeyBytes, keys.MacKey, 0, KeyBytes);
                Array.Clear(material, 0, material.Length);
                return keys;
            }
        }

        /// <summary>
        /// AES with the CryptoServiceProvider rather than RijndaelManaged, for the same
        /// reason HashFactory prefers CNG: the managed implementations throw when the FIPS
        /// policy is enabled, which is common on corporate machines.
        /// </summary>
        public static SymmetricAlgorithm CreateAes(byte[] key)
        {
            AesCryptoServiceProvider aes = new AesCryptoServiceProvider();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            return aes;
        }

        public static void WriteHeader(Stream stream, byte[] salt, int iterations, byte[] macKey)
        {
            byte[] header = BuildHeaderBytes(salt, iterations);
            stream.Write(header, 0, header.Length);

            // MAC the header too, so a wrong passphrase is rejected up front rather than
            // after we have decrypted a frame into garbage.
            byte[] mac = ComputeMac(macKey, header, 0, header.Length);
            stream.Write(mac, 0, mac.Length);
        }

        private static byte[] BuildHeaderBytes(byte[] salt, int iterations)
        {
            byte[] header = new byte[Magic.Length + 4 + 4 + SaltBytes];
            int offset = 0;
            Buffer.BlockCopy(Magic, 0, header, offset, Magic.Length);
            offset += Magic.Length;
            WriteInt32(header, offset, FormatVersion);
            offset += 4;
            WriteInt32(header, offset, iterations);
            offset += 4;
            Buffer.BlockCopy(salt, 0, header, offset, SaltBytes);
            return header;
        }

        /// <summary>
        /// Reads and authenticates the header. Returns false when the passphrase is wrong;
        /// throws only when the file is not a package at all or is a version we cannot read.
        /// </summary>
        public static bool TryReadHeader(Stream stream, string passphrase,
                                         out SessionKeys keys, out string error)
        {
            keys = null;
            error = null;

            byte[] header = new byte[Magic.Length + 4 + 4 + SaltBytes];
            if (!ReadExactly(stream, header, 0, header.Length))
            {
                error = "The file is too short to be a MoveToNewPC package.";
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (header[i] != Magic[i])
                {
                    error = "That file is not a MoveToNewPC package.";
                    return false;
                }
            }

            int version = ReadInt32(header, Magic.Length);
            if (version != FormatVersion)
            {
                error = "The package was written by a different version of this tool (format "
                        + version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ", this build reads "
                        + FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").";
                return false;
            }

            int iterations = ReadInt32(header, Magic.Length + 4);
            if (iterations < 1000 || iterations > 10000000)
            {
                error = "The package header is damaged (implausible iteration count).";
                return false;
            }

            byte[] salt = new byte[SaltBytes];
            Buffer.BlockCopy(header, Magic.Length + 8, salt, 0, SaltBytes);

            byte[] storedMac = new byte[MacBytes];
            if (!ReadExactly(stream, storedMac, 0, MacBytes))
            {
                error = "The package header is truncated.";
                return false;
            }

            SessionKeys candidate = DeriveKeys(passphrase, salt, iterations);
            byte[] actualMac = ComputeMac(candidate.MacKey, header, 0, header.Length);

            if (!Format.ConstantTimeEquals(storedMac, actualMac))
            {
                error = "Wrong password for this package.";
                return false;
            }

            keys = candidate;
            return true;
        }

        public static byte[] ComputeMac(byte[] macKey, byte[] data, int offset, int count)
        {
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
            {
                return hmac.ComputeHash(data, offset, count);
            }
        }

        public static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
        }

        public static void WriteInt64(byte[] buffer, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (8 * i));
            }
        }

        public static long ReadInt64(byte[] buffer, int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= ((long)buffer[offset + i]) << (8 * i);
            }
            return value;
        }

        /// <summary>Reads exactly count bytes, or returns false. Streams may return short reads.</summary>
        public static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(buffer, offset + done, count - done);
                if (read <= 0)
                {
                    return false;
                }
                done += read;
            }
            return true;
        }
    }
}
