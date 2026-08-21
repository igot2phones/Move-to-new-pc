using System;
using System.Security.Cryptography;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Core.Util
{
    /// <summary>
    /// Creates SHA-256 implementations that actually work across the whole OS range.
    ///
    /// SHA256Managed is the obvious choice and the wrong one: it throws on any machine
    /// where the "FIPS compliant algorithms only" policy is set, which is common on
    /// corporate builds. CNG (Vista+) and the CryptoServiceProvider are both FIPS-validated,
    /// so try those first and keep the managed one as a last resort.
    /// </summary>
    public static class HashFactory
    {
        private static int _preferred = -1;

        public static HashAlgorithm CreateSha256()
        {
            if (_preferred == 0 || _preferred == -1)
            {
                try
                {
                    HashAlgorithm cng = new SHA256Cng();
                    _preferred = 0;
                    return cng;
                }
                catch (Exception ex)
                {
                    if (_preferred == -1)
                    {
                        Log.Debug("SHA256Cng unavailable (" + ex.GetType().Name + "), trying CryptoServiceProvider.");
                    }
                }
            }

            if (_preferred == 1 || _preferred == -1)
            {
                try
                {
                    HashAlgorithm csp = new SHA256CryptoServiceProvider();
                    _preferred = 1;
                    return csp;
                }
                catch (Exception ex)
                {
                    if (_preferred == -1)
                    {
                        Log.Debug("SHA256CryptoServiceProvider unavailable (" + ex.GetType().Name + ").");
                    }
                }
            }

            _preferred = 2;
            return new SHA256Managed();
        }

        /// <summary>
        /// Feeds a block into a running hash. TransformBlock rather than ComputeHash because
        /// files here are streamed in chunks and may be larger than memory.
        /// </summary>
        public static void Update(HashAlgorithm hash, byte[] buffer, int offset, int count)
        {
            if (hash == null || count <= 0)
            {
                return;
            }
            hash.TransformBlock(buffer, offset, count, null, 0);
        }

        public static byte[] Finish(HashAlgorithm hash)
        {
            if (hash == null)
            {
                return null;
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            return hash.Hash;
        }
    }
}
