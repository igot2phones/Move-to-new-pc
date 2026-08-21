using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Selection;

namespace MoveToNewPC.Core.Profiles
{
    /// <summary>
    /// Resolves a user's known folders from their own registry hive, so a Documents folder
    /// redirected to D:\ is actually found instead of guessed at.
    /// </summary>
    public static class UserShellFolders
    {
        private const string UserShellFoldersPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        private const string ShellFoldersPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

        /// <summary>
        /// Fills <see cref="UserProfile.KnownFolders"/>. Only folders that actually exist on
        /// disk are recorded; the rest are logged so the operator can see what happened.
        /// </summary>
        public static void Resolve(UserProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            profile.KnownFolders.Clear();

            Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string error;

            using (RegistryHiveScope scope = RegistryHiveScope.Open(profile.Sid, profile.ProfilePath, out error))
            {
                if (scope == null)
                {
                    Log.Warn("Falling back to conventional folder names for " + profile.DisplayName
                             + " (" + (error ?? "hive unavailable") + ")");
                }
                else
                {
                    // "User Shell Folders" holds the authoritative, possibly-unexpanded value.
                    ReadValues(scope, UserShellFoldersPath, raw, true);
                    // "Shell Folders" holds already-expanded literals written by that user's
                    // own session, which is a good second opinion when the first is missing.
                    ReadValues(scope, ShellFoldersPath, raw, false);
                }
            }

            IList<KnownFolderInfo> catalog = KnownFolderCatalog.Items;
            for (int i = 0; i < catalog.Count; i++)
            {
                KnownFolderInfo info = catalog[i];
                string resolved = null;

                for (int v = 0; v < info.RegistryValueNames.Length && resolved == null; v++)
                {
                    string value;
                    if (raw.TryGetValue(info.RegistryValueNames[v], out value) && !string.IsNullOrEmpty(value))
                    {
                        string expanded = ExpandForProfile(value, profile);
                        if (!string.IsNullOrEmpty(expanded) && NativeFile.DirectoryExists(expanded))
                        {
                            resolved = expanded;
                        }
                    }
                }

                if (resolved == null)
                {
                    for (int f = 0; f < info.FallbackFolderNames.Length && resolved == null; f++)
                    {
                        string candidate = LongPath.Combine(profile.ProfilePath, info.FallbackFolderNames[f]);
                        if (NativeFile.DirectoryExists(candidate))
                        {
                            resolved = candidate;
                        }
                    }
                }

                if (resolved != null)
                {
                    profile.KnownFolders[info.Folder] = resolved;
                }
                else
                {
                    Log.Debug("Known folder " + info.Folder + " not found for " + profile.DisplayName);
                }
            }
        }

        private static void ReadValues(RegistryHiveScope scope, string path,
                                       Dictionary<string, string> into, bool overwrite)
        {
            RegistryKey key = scope.OpenSubKey(path);
            if (key == null)
            {
                return;
            }

            try
            {
                string[] names = key.GetValueNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (!overwrite && into.ContainsKey(names[i]))
                    {
                        continue;
                    }

                    // DoNotExpandEnvironmentNames matters enormously here: the default would
                    // expand %USERPROFILE% against OUR environment, silently pointing every
                    // other user's Documents at the operator's own profile.
                    object value = key.GetValue(names[i], null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string s = value as string;
                    if (!string.IsNullOrEmpty(s))
                    {
                        into[names[i]] = s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read " + path + ": " + ex.Message);
            }
            finally
            {
                key.Close();
            }
        }

        /// <summary>
        /// Expands the handful of environment variables that appear in User Shell Folders,
        /// using the TARGET user's profile rather than the current process environment.
        /// </summary>
        public static string ExpandForProfile(string value, UserProfile profile)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            if (value.IndexOf('%') < 0)
            {
                return value;
            }

            string profilePath = profile.ProfilePath ?? string.Empty;
            string systemDrive = SafeEnvironment("SystemDrive", "C:");
            string systemRoot = SafeEnvironment("SystemRoot", @"C:\Windows");
            string publicPath = SafeEnvironment("PUBLIC", LongPath.Combine(LongPath.GetDirectoryName(profilePath), "Public"));

            string homeDrive = string.Empty;
            string homePath = profilePath;
            if (profilePath.Length >= 2 && profilePath[1] == ':')
            {
                homeDrive = profilePath.Substring(0, 2);
                homePath = profilePath.Substring(2);
            }

            StringBuilder sb = new StringBuilder(value);
            Replace(sb, "%USERPROFILE%", profilePath);
            Replace(sb, "%APPDATA%", LongPath.Combine(profilePath, @"AppData\Roaming"));
            Replace(sb, "%LOCALAPPDATA%", LongPath.Combine(profilePath, @"AppData\Local"));
            Replace(sb, "%USERNAME%", profile.AccountName ?? string.Empty);
            Replace(sb, "%HOMEDRIVE%", homeDrive);
            Replace(sb, "%HOMEPATH%", homePath);
            Replace(sb, "%SystemDrive%", systemDrive);
            Replace(sb, "%SystemRoot%", systemRoot);
            Replace(sb, "%windir%", systemRoot);
            Replace(sb, "%PUBLIC%", publicPath);
            Replace(sb, "%ProgramData%", SafeEnvironment("ProgramData", @"C:\ProgramData"));

            string result = sb.ToString();

            // Anything still containing % is a variable we do not know how to expand for
            // another user; treating it as a literal path would be worse than refusing.
            if (result.IndexOf('%') >= 0)
            {
                Log.Debug("Unexpanded shell-folder value ignored: " + value);
                return null;
            }

            return result;
        }

        private static void Replace(StringBuilder sb, string token, string replacement)
        {
            if (replacement == null)
            {
                replacement = string.Empty;
            }

            // StringBuilder.Replace is ordinal and case-sensitive; shell folder values are
            // inconsistent about case, so do the scan by hand.
            string current = sb.ToString();
            int index = current.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return;
            }

            while (index >= 0)
            {
                current = current.Substring(0, index) + replacement + current.Substring(index + token.Length);
                index = current.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            }

            sb.Length = 0;
            sb.Append(current);
        }

        private static string SafeEnvironment(string name, string fallback)
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
