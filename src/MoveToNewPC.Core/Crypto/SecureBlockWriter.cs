using System;
using System.IO;
using System.Security.Cryptography;

namespace MoveToNewPC.Core.Crypto
{
    /// <summary>
    /// Turns a continuous plaintext byte stream into a sequence of independently
    /// authenticated frames:
    ///
    ///     [payloadLength:int32][iv:16][ciphertext][mac:32]
    ///
    /// Encrypt-then-MAC: the MAC covers the frame sequence number, the length and the IV as
    /// well as the ciphertext, so a frame cannot be truncated, reordered, replayed or
    /// swapped between packages without the MAC failing. The sequence number is never
    /// transmitted - both sides count independently, so an attacker cannot renumber frames.
    ///
    /// Callers just Write(); framing happens when the buffer fills or on Flush().
    /// </summary>
    public sealed class SecureBlockWriter : IDisposable
    {
        private readonly Stream _output;
        private readonly byte[] _macKey;
        private readonly SymmetricAlgorithm _aes;
        private readonly byte[] _buffer;
        private int _used;
        private long _sequence;
        private bool _disposed;

        /// <summary>Plaintext bytes buffered before a frame is emitted.</summary>
        public const int BlockSize = 64 * 1024;

        public SecureBlockWriter(Stream output, PackageCrypto.SessionKeys keys)
        {
            if (output == null) { throw new ArgumentNullException("output"); }
            if (keys == null) { throw new ArgumentNullException("keys"); }

            _output = output;
            _macKey = keys.MacKey;
            _aes = PackageCrypto.CreateAes(keys.EncryptionKey);
            _buffer = new byte[BlockSize];
        }

        public void Write(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                int room = _buffer.Length - _used;
                if (room == 0)
                {
                    FlushBlock();
                    continue;
                }

                int take = count < room ? count : room;
                Buffer.BlockCopy(data, offset, _buffer, _used, take);
                _used += take;
                offset += take;
                count -= take;
            }
        }

        public void WriteByte(byte value)
        {
            if (_used == _buffer.Length)
            {
                FlushBlock();
            }
            _buffer[_used++] = value;
        }

        /// <summary>Emits everything buffered so far as one frame.</summary>
        public void Flush()
        {
            if (_used > 0)
            {
                FlushBlock();
            }
            _output.Flush();
        }

        private void FlushBlock()
        {
            if (_used == 0)
            {
                return;
            }

            byte[] iv = PackageCrypto.RandomBytes(PackageCrypto.IvBytes);
            _aes.IV = iv;

            byte[] ciphertext;
            using (ICryptoTransform encryptor = _aes.CreateEncryptor())
            {
                ciphertext = encryptor.TransformFinalBlock(_buffer, 0, _used);
            }
            _used = 0;

            // Authenticate sequence || length || iv || ciphertext, in that order.
            byte[] signed = new byte[8 + 4 + iv.Length + ciphertext.Length];
            PackageCrypto.WriteInt64(signed, 0, _sequence);
            PackageCrypto.WriteInt32(signed, 8, ciphertext.Length);
            Buffer.BlockCopy(iv, 0, signed, 12, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, signed, 12 + iv.Length, ciphertext.Length);

            byte[] mac = PackageCrypto.ComputeMac(_macKey, signed, 0, signed.Length);

            byte[] lengthPrefix = new byte[4];
            PackageCrypto.WriteInt32(lengthPrefix, 0, ciphertext.Length);
            _output.Write(lengthPrefix, 0, 4);
            _output.Write(iv, 0, iv.Length);
            _output.Write(ciphertext, 0, ciphertext.Length);
            _output.Write(mac, 0, mac.Length);

            _sequence++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                Flush();
            }
            finally
            {
                _aes.Clear();
            }
        }
    }
}
