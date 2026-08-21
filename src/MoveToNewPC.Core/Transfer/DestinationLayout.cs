using System;
using System.Collections.Generic;
using Microsoft.Win32;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Selection;

namespace MoveToNewPC.Core.Transfer
{
    /// <summary>Where the receiver puts what arrives.</summary>
    public enum DestinationLayout
    {
        /// <summary>
        /// Everything under one folder the operator chose, one sub-folder per incoming
        /// account. Nothing existing on this PC is touched, which is why it is the default.
        /// </summary>
        SingleFolder = 0,

        /// <summary>
        /// Desktop, Documents, Downloads, Music and Videos go back into this PC's real
        /// folders of the same name. Everything else lands in one folder on the Desktop.
        /// </summary>
        MatchingFolders = 1
    }

    /// <summary>
    /// Resolves the *current* user's own known folders on this machine, so a restore can
    /// put Documents back into Documents.
    ///
    /// Deliberately separate from UserShellFolders, which reads another user's hive: here we
    /// are the logged-on user, so the live environment is both correct and cheaper. Downloads
    /// is the exception - .NET 4.0 has no SpecialFolder for it, so it comes from the
    /// registry by its KNOWNFOLDERID.
    /// </summary>
    public static class LocalKnownFolders
    {
        private const string UserShellFoldersPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

        private const string DownloadsGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

        /// <summary>
        /// The folders a restore will put back in place. Deliberately short: these are the
        /// ones whose meaning is identical on any Windows PC. Pictures is absent on purpose -
        /// add it here if that turns out to be wanted.
        /// </summary>
        public static readonly KnownFolder[] Restorable = new KnownFolder[]
        {
            KnownFolder.Desktop,
            KnownFolder.Documents,
            KnownFolder.Downloads,
            KnownFolder.Music,
            KnownFolder.Videos
        };

        public static bool IsRestorable(KnownFolder folder)
        {
            for (int i = 0; i < Restorable.Length; i++)
            {
                if (Restorable[i] == folder)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Maps a manifest destination name ("Documents", "Music", ...) back to the known
        /// folder it came from. The manifest carries the canonical name rather than the
        /// enum, so this is how the receiver recognises one.
        /// </summary>
        public static bool TryParseDestinationName(string destinationName, out KnownFolder folder)
        {
            folder = KnownFolder.Desktop;
            if (string.IsNullOrEmpty(destinationName))
            {
                return false;
            }

            IList<KnownFolderInfo> catalog = KnownFolderCatalog.Items;
            for (int i = 0; i < catalog.Count; i++)
            {
                if (string.Equals(catalog[i].DestinationName, destinationName,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    folder = catalog[i].Folder;
                    return IsRestorable(folder);
                }
            }
            return false;
        }

        /// <summary>
        /// Test seam. The suite points this at a scratch folder so running the tests never
        /// writes into the operator's own Documents or Desktop. Null in normal operation,
        /// and there is no way to set it from the UI.
        /// </summary>
        internal static Func<KnownFolder, string> ResolveOverride;

        /// <summary>Returns the live path, or null when it cannot be resolved.</summary>
        public static string Resolve(KnownFolder folder)
        {
            Func<KnownFolder, string> hook = ResolveOverride;
            if (hook != null)
            {
                return hook(folder);
            }

            string path = null;
            try
            {
                switch (folder)
                {
                    case KnownFolder.Desktop:
                        path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        break;
                    case KnownFolder.Documents:
                        path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        break;
                    case KnownFolder.Music:
                        path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                        break;
                    case KnownFolder.Videos:
                        path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                        break;
                    case KnownFolder.Pictures:
                        path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        break;
                    case KnownFolder.Downloads:
                        path = ResolveDownloads();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not resolve local known folder " + folder + ": " + ex.Message);
                return null;
            }

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return LongPath.TrimTrailingSeparators(path);
        }

        private static string ResolveDownloads()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserShellFoldersPath))
                {
                    if (key != null)
                    {
                        // DoNotExpandEnvironmentNames then expand ourselves: the stored value
                        // is normally %USERPROFILE%\Downloads.
                        object raw = key.GetValue(DownloadsGuid, null,
                                                  RegistryValueOptions.DoNotExpandEnvironmentNames);
                        string value = raw as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            string expanded = Environment.ExpandEnvironmentVariables(value);
                            if (!string.IsNullOrEmpty(expanded))
                            {
                                return expanded;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Downloads folder not in the registry: " + ex.Message);
            }

            // Every supported Windows version puts it here by default.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
            {
                return null;
            }
            return LongPath.ToDisplay(LongPath.Combine(profile, "Downloads"));
        }
    }
}
