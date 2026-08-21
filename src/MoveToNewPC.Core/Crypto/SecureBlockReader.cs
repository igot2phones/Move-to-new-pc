using System;
using System.IO;
using System.Security.Cryptography;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Crypto
{
    /// <summary>Thrown when a frame fails authentication, is malformed, or arrives out of order.</summary>
    public sealed class SecureChannelException : Exception
    {
        public SecureChannelException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads the frames written by <see cref="SecureBlockWriter"/> back into a continuous
    /// plaintext stream.
    ///
    /// Every frame is authenticated BEFORE it is decrypted. That ordering is the whole point
    /// of encrypt-then-MAC: we never hand attacker-controlled bytes to the AES padding code,
    /// so there is no padding oracle to attack.
    /// </summary>
    public sealed class SecureBlockReader : IDisposable
    {
        private readonly Stream _input;
        private readonly byte[] _macKey;
        private readonly SymmetricAlgorithm _aes;
        private byte[] _plain = new byte[0];
        private int _plainOffset;
        private long _sequence;
        private bool _endOfStream;
        private bool _disposed;

        public SecureBlockReader(Stream input, PackageCrypto.SessionKeys keys)
        {
            if (input == null) { throw new ArgumentNullException("input"); }
            if (keys == null) { throw new ArgumentNullException("keys"); }

            _input = input;
            _macKey = keys.MacKey;
            _aes = PackageCrypto.CreateAes(keys.EncryptionKey);
        }

        /// <summary>
        /// Fills as much of the buffer as it can. Returns the number of bytes read, or 0 at
        /// the end of the stream.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int produced = 0;
            while (produced < count)
            {
                if (_plainOffset >= _plain.Length)
                {
                    if (!NextFrame())
                    {
                        break;
                    }
                    continue;
                }

                int available = _plain.Length - _plainOffset;
                int want = count - produced;
                int take = want < available ? want : available;
                Buffer.BlockCopy(_plain, _plainOffset, buffer, offset + produced, take);
                _plainOffset += take;
                produced += take;
            }
            return produced;
        }

        /// <summary>Reads exactly count bytes; throws when the stream ends early.</summary>
        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            int got = Read(buffer, offset, count);
            if (got != count)
            {
                throw new SecureChannelException("The package ended in the middle of a record.");
            }
        }

        public int ReadByteOrMinusOne()
        {
            byte[] one = new byte[1];
            return Read(one, 0, 1) == 1 ? one[0] : -1;
        }

        private bool NextFrame()
        {
            if (_endOfStream)
            {
                return false;
            }

            byte[] lengthPrefix = new byte[4];
            if (!PackageCrypto.ReadExactly(_input, lengthPrefix, 0, 4))
            {
                _endOfStream = true;
                return false;
            }

            int cipherLength = PackageCrypto.ReadInt32(lengthPrefix, 0);

            // Check the declared size before allocating anything. A hostile package must not
            // be able to make us reserve a gigabyte because it claimed to.
            if (cipherLength <= 0 || cipherLength > PackageCrypto.MaxFrameBytes)
            {
                throw new SecureChannelException("The package declares an implausible frame size ("
                    + cipherLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes). It is damaged or not a package.");
            }
            if ((cipherLength % PackageCrypto.IvBytes) != 0)
            {
                throw new SecureChannelException("The package is damaged (frame is not a whole number of blocks).");
            }

            byte[] iv = new byte[PackageCrypto.IvBytes];
            if (!PackageCrypto.ReadExactly(_input, iv, 0, iv.Length))
            {
                throw new SecureChannelException("The package is truncated (missing frame IV).");
            }

            byte[] ciphertext = new byte[cipherLength];
            if (!PackageCrypto.ReadExactly(_input, ciphertext, 0, cipherLength))
            {
                throw new SecureChannelException("The package is truncated (frame shorter than declared).");
            }

            byte[] storedMac = new byte[PackageCrypto.MacBytes];
            if (!PackageCrypto.ReadExactly(_input, storedMac, 0, storedMac.Length))
            {
                throw new SecureChannelException("The package is truncated (missing frame signature).");
            }

            byte[] signed = new byte[8 + 4 + iv.Length + ciphertext.Length];
            PackageCrypto.WriteInt64(signed, 0, _sequence);
            PackageCrypto.WriteInt32(signed, 8, cipherLength);
            Buffer.BlockCopy(iv, 0, signed, 12, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, signed, 12 + iv.Length, ciphertext.Length);

            byte[] actualMac = PackageCrypto.ComputeMac(_macKey, signed, 0, signed.Length);

            // Constant-time: a byte-by-byte early exit here would leak the correct MAC.
            if (!Format.ConstantTimeEquals(storedMac, actualMac))
            {
                throw new SecureChannelException(
                    "The package failed its integrity check. It has been altered or corrupted "
                    + "in transit, and nothing from it will be written.");
            }

            _aes.IV = iv;
            using (ICryptoTransform decryptor = _aes.CreateDecryptor())
            {
                _plain = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
            _plainOffset = 0;
            _sequence++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _aes.Clear();
        }
    }
}
