using System;
using System.Collections.Generic;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Profiles;

namespace MoveToNewPC.Core.Selection
{
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

        public AppDataItem(string relativePath, string label, string note)
        {
            RelativePath = relativePath;
            Label = label;
            Note = note;
            DestinationRelativeRoot = relativePath;
        }
    }

    public static class AppDataCatalog
    {
        public static List<AppDataItem> AllCandidates()
        {
            List<AppDataItem> items = new List<AppDataItem>();

            items.Add(new AppDataItem(@"AppData\Local\Google\Chrome\User Data",
                "Google Chrome profile (bookmarks, history, extensions)",
                "Best effort. Passwords are tied to this Windows account and will not decrypt on the new PC."));

            items.Add(new AppDataItem(@"AppData\Local\Microsoft\Edge\User Data",
                "Microsoft Edge profile",
                "Best effort. Saved passwords are tied to this Windows account and will not decrypt on the new PC."));

            items.Add(new AppDataItem(@"AppData\Local\BraveSoftware\Brave-Browser\User Data",
                "Brave profile",
                "Best effort. Saved passwords will not decrypt on the new PC."));

            items.Add(new AppDataItem(@"AppData\Roaming\Mozilla\Firefox",
                "Mozilla Firefox profiles",
                "Includes profiles.ini and all profile folders. Firefox should be closed before copying."));

            items.Add(new AppDataItem(@"AppData\Roaming\Thunderbird",
                "Mozilla Thunderbird profiles",
                "Includes local mail stores. Thunderbird should be closed before copying."));

            AppDataItem outlookFiles = new AppDataItem(@"Outlook Files",
                "Outlook data files (.pst) in Documents",
                "PST files are your actual mail. Outlook must be closed or they will be locked.");
            outlookFiles.UnderDocuments = true;
            outlookFiles.DestinationRelativeRoot = @"Documents\Outlook Files";
            items.Add(outlookFiles);

            items.Add(new AppDataItem(@"AppData\Local\Microsoft\Outlook",
                "Outlook local data (.ost / .nst caches)",
                "OST files are a re-downloadable cache of a server mailbox - usually NOT worth moving. "
                + "They are often many gigabytes and Outlook rebuilds them automatically."));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Sticky Notes",
                "Sticky Notes (legacy StickyNotes.snt)",
                "Windows 7/8 era Sticky Notes."));

            items.Add(new AppDataItem(@"AppData\Local\Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState",
                "Sticky Notes (modern plum.sqlite)",
                "Windows 10/11 Sticky Notes. The new PC needs the Sticky Notes app installed before this helps."));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Signatures",
                "Outlook signatures",
                "Small and usually restores cleanly."));

            items.Add(new AppDataItem(@"AppData\Roaming\Microsoft\Templates",
                "Office templates (Normal.dotm and friends)",
                "Version sensitive; usually restores cleanly."));

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
                roots.Add(root);
            }

            return roots;
        }
    }
}
