using System;
using System.Text;
using MoveToNewPC.Core.Crypto;

namespace MoveToNewPC.Core.Package
{
    /// <summary>
    /// Record tags and primitive encoding for the plaintext inside an encrypted package.
    ///
    /// The plaintext is a stream of records that mirror the <c>ITransferSink</c> calls
    /// one-for-one. That is deliberate: the reader on the new PC replays them straight into
    /// a real sink, so path validation, hashing, collision policy and the resume journal are
    /// all the code that already exists and is already tested.
    ///
    /// Little-endian throughout, to match the rest of the on-disk formats.
    /// </summary>
    public static class PackageFormat
    {
        public const byte TagHeader = (byte)'H';
        public const byte TagDirectory = (byte)'D';
        public const byte TagFileBegin = (byte)'F';
        public const byte TagChunk = (byte)'C';
        public const byte TagFileEnd = (byte)'E';
        public const byte TagSkip = (byte)'S';
        public const byte TagEnd = (byte)'Z';

        /// <summary>
        /// Longest string we will accept from a package. File paths are bounded by the
        /// long-path limit; anything claiming more is damaged or hostile.
        /// </summary>
        public const int MaxStringBytes = 64 * 1024;

        public static void WriteInt32(SecureBlockWriter writer, int value)
        {
            byte[] buffer = new byte[4];
            PackageCrypto.WriteInt32(buffer, 0, value);
            writer.Write(buffer, 0, 4);
        }

        public static void WriteInt64(SecureBlockWriter writer, long value)
        {
            byte[] buffer = new byte[8];
            PackageCrypto.WriteInt64(buffer, 0, value);
            writer.Write(buffer, 0, 8);
        }

        public static void WriteString(SecureBlockWriter writer, string value)
        {
            if (value == null)
            {
                WriteInt32(writer, -1);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(writer, bytes.Length);
            if (bytes.Length > 0)
            {
                writer.Write(bytes, 0, bytes.Length);
            }
        }

        public static void WriteBytes(SecureBlockWriter writer, byte[] value)
        {
            if (value == null)
            {
                WriteInt32(writer, -1);
                return;
            }
            WriteInt32(writer, value.Length);
            if (value.Length > 0)
            {
                writer.Write(value, 0, value.Length);
            }
        }

        public static int ReadInt32(SecureBlockReader reader)
        {
            byte[] buffer = new byte[4];
            reader.ReadExactly(buffer, 0, 4);
            return PackageCrypto.ReadInt32(buffer, 0);
        }

        public static long ReadInt64(SecureBlockReader reader)
        {
            byte[] buffer = new byte[8];
            reader.ReadExactly(buffer, 0, 8);
            return PackageCrypto.ReadInt64(buffer, 0);
        }

        public static string ReadString(SecureBlockReader reader)
        {
            int length = ReadInt32(reader);
            if (length == -1)
            {
                return null;
            }
            if (length < 0 || length > MaxStringBytes)
            {
                throw new SecureChannelException("The package contains an implausible string length.");
            }
            if (length == 0)
            {
                return string.Empty;
            }
            byte[] bytes = new byte[length];
            reader.ReadExactly(bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public static byte[] ReadBytes(SecureBlockReader reader, int maximum)
        {
            int length = ReadInt32(reader);
            if (length == -1)
            {
                return null;
            }
            if (length < 0 || length > maximum)
            {
                throw new SecureChannelException("The package contains an implausible field length.");
            }
            byte[] bytes = new byte[length];
            if (length > 0)
            {
                reader.ReadExactly(bytes, 0, length);
            }
            return bytes;
        }
    }
}
