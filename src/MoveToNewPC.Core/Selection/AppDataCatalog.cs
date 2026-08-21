using System;
using System.Collections.Generic;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Profiles;

namespace MoveToNewPC.Core.Selection
{
    /// <summary>
    /// What kind of application data an entry is, so the UI can offer "all browsers" or
    /// "all mail" as one tick rather than making the operator recognise twelve folder names.
    /// </summary>
    public enum AppDataCategory
    {
        Other = 0,
        Browser = 1,
        Mail = 2
    }

    /// <summary>
    /// Tier B: a curated allow-list of application data, never all of AppData. Every entry
    /// is detected on the source machine first; entries that are not there are not shown.
    ///
    /// Honesty requirement from the spec: application-data restores are best effort and
    /// version-sensitive. Each entry carries a Note saying what the catch is, and the UI
    /// shows it.
    /// </summary>
    public sealed class AppDataItem
    {
        /// <summary>Path relative to the profile root, e.g. @"AppData\Local\Google\Chrome\User Data".</summary>
        public string RelativePath;
        public string Label;
        public string Note;
        /// <summary>Landing place under the mapped user root on the new PC.</summary>
        public string DestinationRelativeRoot;
        /// <summary>When set, the entry hangs off a known folder instead of the profile root.</summary>
        public bool UnderDocuments;
        public AppDataCategory Category;

        public AppDataItem(string relativePath, string label, string note)
            : this(relativePath, label, note, AppDataCategory.Other)
        {
        }

        public AppDataItem(string relativePath, string label, string note, AppDataCategory category)
        {
            RelativePath = relativePath;
            Label = label;
            Note = note;
            Category = category;
            DestinationRelativeRoot = relativePath;
        }
    }

    public static class AppDataCatalog
    {
        /// <summary>
        /// Every Chromium-based browser keeps the same "User Data" shape, so they all carry
        /// the same warning: the password store is encrypted with a key tied to this Windows
        /// account and will not decrypt anywhere else. Bookmarks, history and extensions do
        /// come across.
        /// </summary>
        private const string ChromiumNote =
            "Bookmarks, history and extensions come across. Saved passwords are encrypted against "
            + "this Windows account and will NOT decrypt on the new PC. Close the browser first.";

        public static List<AppDataItem> AllCandidates()
        {
            List<AppDataItem> items = new List<AppDataItem>();

            // ---- browsers -------------------------------------------------------------
            items.Add(new AppDataItem(@"AppData\Local\Google\Chrome\User Data",
                "Google Chrome", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Local\Google\Chrome Beta\User Data",
                "Google Chrome Beta", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Local\Microsoft\Edge\User Data",
                "Microsoft Edge", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Local\BraveSoftware\Brave-Browser\User Data",
                "Brave", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Local\Vivaldi\User Data",
                "Vivaldi", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Local\Chromium\User Data",
                "Chromium", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Roaming\Opera Software\Opera Stable",
                "Opera", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Roaming\Opera Software\Opera GX Stable",
                "Opera GX", ChromiumNote, AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Roaming\Mozilla\Firefox",
                "Mozilla Firefox",
                "Bookmarks, history, saved logins and extensions. Firefox keeps these in its own "
                + "profile format, so they usually restore cleanly. Close Firefox first.",
                AppDataCategory.Browser));

            items.Add(new AppDataItem(@"AppData\Roaming\Waterfox",
                "Waterfox",
                "Same profile format as Firefox. Close it before copying.",
                AppDataCategory.Browser));

            // ---- mail -----------------------------------------------------------------
            AppDataItem outlookFiles = new AppDataItem(@"Outlook Files",
                "Outlook data files (.pst)",
                "This is your actual mail. Outlook MUST be closed or the files are locked and will "
                + "be skipped. On the new PC, open them with File > Open > Outlook Data File.",
                AppDataCategory.Mail);
            outlookFiles.UnderDocuments = true;
            outlookFiles.DestinationRelativeRoot = @"Documents\Outlook Files";
            items.Add(outlookFiles);

            items.Add(new AppDataItem(@"AppData\Local\Microsoft\Outlook",
                "Outlook cached mailbox (.ost / .nst)",
                "Usually NOT worth moving: an OST is a re-downloadable copy of a server mailbox, "
                + "often many gigabytes, and Outlook rebuilds it automatically. Move it only for "
                + "an account that no longer exists on the server.",
                AppDataCategory.Mail));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Signatures",
                "Outlook signatures",
                "Small and usually restores cleanly.", AppDataCategory.Mail));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Templates",
                "Office templates (Normal.dotm and friends)",
                "Version sensitive; usually restores cleanly."));

            items.Add(new AppDataItem(@"AppData\Roaming\Thunderbird",
                "Mozilla Thunderbird",
                "Includes profiles.ini and the local mail stores, which is the mail itself. "
                + "Thunderbird MUST be closed or the stores are locked.",
                AppDataCategory.Mail));

            items.Add(new AppDataItem(@"AppData\Local\Microsoft\Windows Live Mail",
                "Windows Live Mail",
                "Common on Windows 7 machines. The app is discontinued; on the new PC you will need "
                + "to import the store into another mail program.",
                AppDataCategory.Mail));

            items.Add(new AppDataItem(@"AppData\Roaming\eM Client",
                "eM Client",
                "Includes the local database. Close eM Client before copying.",
                AppDataCategory.Mail));

            // ---- everything else ------------------------------------------------------
            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Sticky Notes",
                "Sticky Notes (legacy StickyNotes.snt)",
                "Windows 7/8 era Sticky Notes."));

            items.Add(new AppDataItem(@"AppData\Local\Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState",
                "Sticky Notes (modern plum.sqlite)",
                "Windows 10/11 Sticky Notes. The new PC needs the Sticky Notes app installed before this helps."));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Windows\Recent",
                "Recent items list",
                "Shortcuts only. They will point at paths that may not exist on the new PC."));

            return items;
        }

        /// <summary>
        /// Returns only the entries that actually exist for this profile, with their source
        /// path filled in. Entries that are absent are not shown at all - offering to move
        /// Thunderbird on a machine without Thunderbird is noise.
        /// </summary>
        public static List<SelectionRoot> DetectFor(UserProfile profile)
        {
            List<SelectionRoot> roots = new List<SelectionRoot>();
            if (profile == null || string.IsNullOrEmpty(profile.ProfilePath))
            {
                return roots;
            }

            List<AppDataItem> candidates = AllCandidates();
            for (int i = 0; i < candidates.Count; i++)
            {
                AppDataItem item = candidates[i];

                string basePath = profile.ProfilePath;
                if (item.UnderDocuments)
                {
                    string documents;
                    if (!profile.KnownFolders.TryGetValue(KnownFolder.Documents, out documents)
                        || string.IsNullOrEmpty(documents))
                    {
                        continue;
                    }
                    basePath = documents;
                }

                string source = LongPath.Combine(basePath, item.RelativePath);
                if (!NativeFile.DirectoryExists(source))
                {
                    continue;
                }

                SelectionRoot root = new SelectionRoot();
                root.Tier = SelectionTier.AppData;
                root.IsKnownFolder = false;
                root.Label = item.Label;
                root.SourcePath = source;
                root.DestinationRelativeRoot = item.DestinationRelativeRoot;
                root.Selected = false;   // Tier B is off by default, one checkbox away.
                root.Exists = true;
                root.Note = item.Note;
                root.Category = item.Category;
                roots.Add(root);
            }

            return roots;
        }
    }
}
