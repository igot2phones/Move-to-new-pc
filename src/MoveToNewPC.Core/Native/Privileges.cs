using System;
using System.Runtime.InteropServices;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Core.Native
{
    /// <summary>
    /// Enables token privileges. Being in the Administrators group is not the same as
    /// holding SeBackupPrivilege/SeRestorePrivilege: they are present but DISABLED in an
    /// elevated token and must be switched on before RegLoadKey will work.
    /// </summary>
    public static class Privileges
    {
        private static readonly object Gate = new object();

        public static bool Enable(string privilegeName)
        {
            lock (Gate)
            {
                IntPtr token = IntPtr.Zero;
                try
                {
                    if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                                                        NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                                                        out token))
                    {
                        Log.Warn("OpenProcessToken failed: " + Marshal.GetLastWin32Error());
                        return false;
                    }

                    NativeMethods.LUID luid;
                    if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, out luid))
                    {
                        Log.Warn("LookupPrivilegeValue failed for " + privilegeName + ": " + Marshal.GetLastWin32Error());
                        return false;
                    }

                    NativeMethods.TOKEN_PRIVILEGES tp = new NativeMethods.TOKEN_PRIVILEGES();
                    tp.PrivilegeCount = 1;
                    tp.Privilege0.Luid = luid;
                    tp.Privilege0.Attributes = NativeMethods.SE_PRIVILEGE_ENABLED;

                    if (!NativeMethods.AdjustTokenPrivileges(token, false, ref tp,
                                                             (uint)Marshal.SizeOf(typeof(NativeMethods.TOKEN_PRIVILEGES)),
                                                             IntPtr.Zero, IntPtr.Zero))
                    {
                        Log.Warn("AdjustTokenPrivileges failed for " + privilegeName + ": " + Marshal.GetLastWin32Error());
                        return false;
                    }

                    // AdjustTokenPrivileges returns TRUE even when it could not assign every
                    // privilege; ERROR_NOT_ALL_ASSIGNED is the only way to know.
                    int err = Marshal.GetLastWin32Error();
                    if (err != NativeMethods.ERROR_SUCCESS)
                    {
                        Log.Warn("Privilege " + privilegeName + " not assigned (error " + err + ")");
                        return false;
                    }

                    Log.Debug("Enabled privilege " + privilegeName);
                    return true;
                }
                finally
                {
                    if (token != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(token);
                    }
                }
            }
        }

        /// <summary>Enables the privileges the profile/hive code needs. Best effort.</summary>
        public static void EnableBackupAndRestore()
        {
            Enable(NativeMethods.SE_BACKUP_NAME);
            Enable(NativeMethods.SE_RESTORE_NAME);
        }
    }
}
