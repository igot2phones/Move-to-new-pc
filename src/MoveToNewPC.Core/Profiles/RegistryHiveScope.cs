using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Win32;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.Profiles
{
    /// <summary>
    /// Loads another user's NTUSER.DAT into HKEY_USERS so their User Shell Folders can be
    /// read, and guarantees it is unloaded again.
    ///
    /// Leaving a hive loaded is not a cosmetic bug: it keeps the profile locked, can stop
    /// the user logging in, and survives our process exiting. Every load in this class is
    /// paired with an unload in a finally block, and every RegistryKey we hand out is
    /// tracked so it can be closed before the unload (RegUnLoadKey fails while any key in
    /// the hive is still open).
    /// </summary>
    public sealed class RegistryHiveScope : IDisposable
    {
        private readonly string _mountName;
        private readonly List<RegistryKey> _openKeys = new List<RegistryKey>();
        private bool _loaded;
        private bool _disposed;

        private RegistryHiveScope(string mountName, bool loaded)
        {
            _mountName = mountName;
            _loaded = loaded;
        }

        /// <summary>Sub-key under HKEY_USERS where the hive is mounted.</summary>
        public string MountName
        {
            get { return _mountName; }
        }

        /// <summary>True when we mounted it and therefore must unmount it.</summary>
        public bool IsTemporary
        {
            get { return _loaded; }
        }

        /// <summary>
        /// Returns a scope for the given profile. If the user is logged on their hive is
        /// already under HKU\&lt;sid&gt; and nothing is mounted or unmounted.
        /// Returns null (with a reason) when the hive cannot be made available.
        /// </summary>
        public static RegistryHiveScope Open(string sid, string profilePath, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(sid))
            {
                error = "No SID";
                return null;
            }

            if (IsHiveLoaded(sid))
            {
                return new RegistryHiveScope(sid, false);
            }

            if (string.IsNullOrEmpty(profilePath))
            {
                error = "No profile path";
                return null;
            }

            string hiveFile = LongPath.Combine(profilePath, "NTUSER.DAT");
            if (!NativeFile.FileExists(hiveFile))
            {
                error = "NTUSER.DAT not found in " + LongPath.ToDisplay(profilePath);
                return null;
            }

            // A unique mount name so two instances of this tool, or a retry after a crash,
            // cannot collide on the same key.
            string mountName = "MTNPC_" + sid.Replace('-', '_') + "_"
                               + Environment.TickCount.ToString("X8", CultureInfo.InvariantCulture);

            // RegLoadKeyW takes a plain path, not \\?\ - the registry APIs do not understand
            // the extended prefix. Profile roots are short, so this is safe in practice.
            int result = NativeMethods.RegLoadKeyW(NativeMethods.HKEY_USERS, mountName,
                                                   LongPath.ToDisplay(hiveFile));
            if (result != NativeMethods.ERROR_SUCCESS)
            {
                error = "RegLoadKey failed: " + NativeFile.DescribeError(result);
                Log.Warn("Could not load hive for " + sid + ": " + error);
                return null;
            }

            Log.Debug("Loaded hive " + LongPath.ToDisplay(hiveFile) + " as HKU\\" + mountName);
            return new RegistryHiveScope(mountName, true);
        }

        /// <summary>Opens a sub-key under the mounted user hive. Returns null when absent.</summary>
        public RegistryKey OpenSubKey(string subKeyPath)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("RegistryHiveScope");
            }

            try
            {
                using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default))
                {
                    RegistryKey key = users.OpenSubKey(_mountName + "\\" + subKeyPath, false);
                    if (key != null)
                    {
                        _openKeys.Add(key);
                    }
                    return key;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open HKU\\" + _mountName + "\\" + subKeyPath + ": " + ex.Message);
                return null;
            }
        }

        public static bool IsHiveLoaded(string sid)
        {
            try
            {
                using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default))
                {
                    using (RegistryKey key = users.OpenSubKey(sid, false))
                    {
                        return key != null;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            for (int i = 0; i < _openKeys.Count; i++)
            {
                try
                {
                    _openKeys[i].Close();
                }
                catch (Exception)
                {
                }
            }
            _openKeys.Clear();

            if (!_loaded)
            {
                return;
            }

            // RegUnLoadKey fails with ERROR_ACCESS_DENIED while anything in the hive is
            // still open. The RegistryKey objects above are closed, but the CLR may not have
            // released the underlying handles yet, so collect and retry a few times rather
            // than leaving someone's profile mounted.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int result = NativeMethods.RegUnLoadKeyW(NativeMethods.HKEY_USERS, _mountName);
                if (result == NativeMethods.ERROR_SUCCESS)
                {
                    _loaded = false;
                    Log.Debug("Unloaded hive HKU\\" + _mountName);
                    return;
                }

                if (attempt == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Thread.Sleep(100 * (attempt + 1));
            }

            Log.Error("FAILED to unload registry hive HKU\\" + _mountName
                      + " - the profile may stay locked until reboot.");
        }
    }
}
