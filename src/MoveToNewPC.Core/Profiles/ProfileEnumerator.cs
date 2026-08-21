using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Core.Profiles
{
    /// <summary>
    /// Enumerates user profiles from HKLM ProfileList. The registry is the only reliable
    /// source: the folder name under C:\Users often does not match the account name (a
    /// renamed account keeps its old folder), and some profiles live on another drive.
    /// </summary>
    public static class ProfileEnumerator
    {
        private const string ProfileListPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        public static ProfileEnumerationResult Enumerate()
        {
            ProfileEnumerationResult result = new ProfileEnumerationResult();
            string currentSid = GetCurrentUserSid();

            RegistryKey profileList = null;
            try
            {
                // Registry64 rather than Default: on a 64-bit OS a 32-bit build of this tool
                // would otherwise be redirected, and AnyCPU means we could be either.
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    profileList = hklm.OpenSubKey(ProfileListPath, false);
                    if (profileList == null)
                    {
                        result.Warnings.Add("Could not open ProfileList in the registry.");
                        Log.Error("ProfileList key missing: HKLM\\" + ProfileListPath);
                        return result;
                    }

                    string[] sids = profileList.GetSubKeyNames();
                    Log.Info("ProfileList contains " + sids.Length + " entries.");

                    for (int i = 0; i < sids.Length; i++)
                    {
                        string sid = sids[i];
                        try
                        {
                            InspectOne(profileList, sid, currentSid, result);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Failed to inspect profile " + sid, ex);
                            result.Filtered.Add(new FilteredProfile(sid, null, "Error reading entry: " + ex.Message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Profile enumeration failed", ex);
                result.Warnings.Add("Profile enumeration failed: " + ex.Message);
            }
            finally
            {
                if (profileList != null)
                {
                    profileList.Close();
                }
            }

            result.Profiles.Sort(delegate(UserProfile a, UserProfile b)
            {
                if (a.IsCurrentUser != b.IsCurrentUser)
                {
                    return a.IsCurrentUser ? -1 : 1;
                }
                return string.Compare(a.AccountName, b.AccountName, StringComparison.CurrentCultureIgnoreCase);
            });

            Log.Info("Profiles shown: " + result.Profiles.Count + ", filtered: " + result.Filtered.Count);
            return result;
        }

        private static void InspectOne(RegistryKey profileList, string sid, string currentSid,
                                       ProfileEnumerationResult result)
        {
            // ".bak" keys are left behind when Windows fails to load a profile.
            if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                result.Filtered.Add(new FilteredProfile(sid, null, "Backup entry left by a failed profile load"));
                return;
            }

            using (RegistryKey key = profileList.OpenSubKey(sid, false))
            {
                if (key == null)
                {
                    return;
                }

                string imagePath = key.GetValue("ProfileImagePath", null,
                                                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!string.IsNullOrEmpty(imagePath))
                {
                    // These contain machine-level variables only (%SystemDrive%), so the
                    // process environment is the right thing to expand against here.
                    imagePath = Environment.ExpandEnvironmentVariables(imagePath);
                }

                if (string.IsNullOrEmpty(imagePath))
                {
                    result.Filtered.Add(new FilteredProfile(sid, null, "No ProfileImagePath"));
                    return;
                }

                if (IsWellKnownServiceSid(sid))
                {
                    result.Filtered.Add(new FilteredProfile(sid, imagePath, "Windows service account"));
                    return;
                }

                int rid;
                if (TryGetRid(sid, out rid) && rid < 1000)
                {
                    result.Filtered.Add(new FilteredProfile(sid, imagePath,
                        "Built-in account (RID " + rid.ToString(CultureInfo.InvariantCulture) + ")"));
                    return;
                }

                int special = ReadDword(key, "Special");
                if (special != 0)
                {
                    result.Filtered.Add(new FilteredProfile(sid, imagePath, "Marked as a special/system profile"));
                    return;
                }

                int flags = ReadDword(key, "Flags");
                if (flags != 0 && !IsMachineOrDomainAccountSid(sid))
                {
                    // Deliberately narrower than "any non-zero Flags": some perfectly normal
                    // roaming and mandatory profiles set Flags, and hiding a real user is a
                    // far worse failure than showing an extra row. Anything hidden here is
                    // still counted and listed on the selection screen.
                    result.Filtered.Add(new FilteredProfile(sid, imagePath,
                        "Non-user profile (Flags=0x" + flags.ToString("X", CultureInfo.InvariantCulture) + ")"));
                    return;
                }

                if (!NativeFile.DirectoryExists(imagePath))
                {
                    result.Filtered.Add(new FilteredProfile(sid, imagePath, "Profile folder no longer exists"));
                    return;
                }

                UserProfile profile = new UserProfile();
                profile.Sid = sid;
                profile.ProfilePath = LongPath.TrimTrailingSeparators(imagePath);
                profile.ProfileExists = true;
                profile.IsHiveLoaded = RegistryHiveScope.IsHiveLoaded(sid);
                profile.IsCurrentUser = !string.IsNullOrEmpty(currentSid)
                                        && string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase);

                string domain;
                string account = LookupAccountName(sid, out domain);
                if (string.IsNullOrEmpty(account))
                {
                    // A deleted domain account still has a profile worth moving; fall back to
                    // the folder name and say so rather than dropping it.
                    account = LongPath.GetFileName(profile.ProfilePath);
                    profile.SizeError = "Account name could not be resolved from the SID";
                    Log.Warn("LookupAccountSid failed for " + sid + "; using folder name " + account);
                }

                profile.AccountName = account;
                profile.DomainName = domain;

                UserShellFolders.Resolve(profile);

                result.Profiles.Add(profile);
                Log.Info("Profile: " + profile.DisplayName + " -> " + profile.ProfilePath
                         + (profile.IsCurrentUser ? "  [current user]" : string.Empty)
                         + (profile.IsHiveLoaded ? "  [logged on]" : string.Empty)
                         + "  known folders: " + profile.KnownFolders.Count);
            }
        }

        private static int ReadDword(RegistryKey key, string name)
        {
            try
            {
                object value = key.GetValue(name, null);
                if (value == null)
                {
                    return 0;
                }
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static bool IsWellKnownServiceSid(string sid)
        {
            return string.Equals(sid, "S-1-5-18", StringComparison.OrdinalIgnoreCase)   // LocalSystem
                   || string.Equals(sid, "S-1-5-19", StringComparison.OrdinalIgnoreCase) // LocalService
                   || string.Equals(sid, "S-1-5-20", StringComparison.OrdinalIgnoreCase); // NetworkService
        }

        /// <summary>True for S-1-5-21-... (a real machine-local or domain account).</summary>
        private static bool IsMachineOrDomainAccountSid(string sid)
        {
            return !string.IsNullOrEmpty(sid)
                   && sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetRid(string sid, out int rid)
        {
            rid = 0;
            if (string.IsNullOrEmpty(sid))
            {
                return false;
            }
            int dash = sid.LastIndexOf('-');
            if (dash < 0 || dash == sid.Length - 1)
            {
                return false;
            }
            return int.TryParse(sid.Substring(dash + 1), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out rid);
        }

        /// <summary>SID string to "DOMAIN\name" pieces. Returns null when unresolvable.</summary>
        public static string LookupAccountName(string sidString, out string domain)
        {
            domain = null;
            IntPtr sidPtr = IntPtr.Zero;

            try
            {
                if (!NativeMethods.ConvertStringSidToSidW(sidString, out sidPtr) || sidPtr == IntPtr.Zero)
                {
                    return null;
                }

                int sidLength = GetSidLength(sidPtr);
                if (sidLength <= 0)
                {
                    return null;
                }

                byte[] sidBytes = new byte[sidLength];
                Marshal.Copy(sidPtr, sidBytes, 0, sidLength);

                uint nameLength = 256;
                uint domainLength = 256;
                StringBuilder name = new StringBuilder((int)nameLength);
                StringBuilder domainBuilder = new StringBuilder((int)domainLength);
                int use;

                if (!NativeMethods.LookupAccountSidW(null, sidBytes, name, ref nameLength,
                                                     domainBuilder, ref domainLength, out use))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                    {
                        name = new StringBuilder((int)nameLength);
                        domainBuilder = new StringBuilder((int)domainLength);
                        if (!NativeMethods.LookupAccountSidW(null, sidBytes, name, ref nameLength,
                                                             domainBuilder, ref domainLength, out use))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        // ERROR_NONE_MAPPED just means the account is gone - common and fine.
                        return null;
                    }
                }

                domain = domainBuilder.ToString();
                return name.ToString();
            }
            catch (Exception ex)
            {
                Log.Warn("LookupAccountSid threw for " + sidString + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (sidPtr != IntPtr.Zero)
                {
                    NativeMethods.LocalFree(sidPtr);
                }
            }
        }

        /// <summary>
        /// SID length from its own header: 8 fixed bytes plus 4 per sub-authority.
        /// Avoids a P/Invoke to GetLengthSid for one byte of arithmetic.
        /// </summary>
        private static int GetSidLength(IntPtr sid)
        {
            try
            {
                byte subAuthorityCount = Marshal.ReadByte(sid, 1);
                return 8 + (4 * subAuthorityCount);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static string GetCurrentUserSid()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                           System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return identity.User == null ? null : identity.User.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not determine current user SID: " + ex.Message);
                return null;
            }
        }
    }
}
