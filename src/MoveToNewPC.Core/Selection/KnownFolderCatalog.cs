using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.Selection
{
    /// <summary>
    /// Maps each Tier A folder to the registry values that locate it and the conventional
    /// folder name to fall back on. The registry is the source of truth: a user whose
    /// Documents were redirected to D:\ must have D:\ found, not %USERPROFILE%\Documents.
    /// </summary>
    public sealed class KnownFolderInfo
    {
        public KnownFolder Folder;
        /// <summary>What the operator sees.</summary>
        public string Label;
        /// <summary>
        /// User Shell Folders value names, most specific first. Vista+ stores the newer
        /// folders under their KNOWNFOLDERID GUID rather than a friendly name.
        /// </summary>
        public string[] RegistryValueNames;
        /// <summary>Folder name under the profile if the registry says nothing.</summary>
        public string[] FallbackFolderNames;
        /// <summary>Canonical destination name on the new PC.</summary>
        public string DestinationName;
        public bool DefaultSelected = true;

        public KnownFolderInfo(KnownFolder folder, string label, string destinationName,
                               string[] registryValueNames, string[] fallbackFolderNames)
        {
            Folder = folder;
            Label = label;
            DestinationName = destinationName;
            RegistryValueNames = registryValueNames;
            FallbackFolderNames = fallbackFolderNames;
        }
    }

    public static class KnownFolderCatalog
    {
        private static readonly List<KnownFolderInfo> All = BuildAll();

        public static IList<KnownFolderInfo> Items
        {
            get { return All; }
        }

        public static KnownFolderInfo Get(KnownFolder folder)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Folder == folder)
                {
                    return All[i];
                }
            }
            return null;
        }

        private static List<KnownFolderInfo> BuildAll()
        {
            List<KnownFolderInfo> list = new List<KnownFolderInfo>();

            list.Add(new KnownFolderInfo(KnownFolder.Desktop, "Desktop", "Desktop",
                new string[] { "Desktop" },
                new string[] { "Desktop" }));

            list.Add(new KnownFolderInfo(KnownFolder.Documents, "Documents", "Documents",
                new string[] { "Personal" },
                new string[] { "Documents", "My Documents" }));

            list.Add(new KnownFolderInfo(KnownFolder.Downloads, "Downloads", "Downloads",
                new string[] { "{374DE290-123F-4565-9164-39C4925E467B}" },
                new string[] { "Downloads" }));

            list.Add(new KnownFolderInfo(KnownFolder.Pictures, "Pictures", "Pictures",
                new string[] { "My Pictures" },
                new string[] { "Pictures", "My Pictures" }));

            list.Add(new KnownFolderInfo(KnownFolder.Music, "Music", "Music",
                new string[] { "My Music" },
                new string[] { "Music", "My Music" }));

            list.Add(new KnownFolderInfo(KnownFolder.Videos, "Videos", "Videos",
                new string[] { "My Video" },
                new string[] { "Videos", "My Videos" }));

            list.Add(new KnownFolderInfo(KnownFolder.Favorites, "Favorites (Internet Explorer / Edge)", "Favorites",
                new string[] { "Favorites" },
                new string[] { "Favorites" }));

            list.Add(new KnownFolderInfo(KnownFolder.Links, "Links", "Links",
                new string[] { "{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}" },
                new string[] { "Links" }));

            list.Add(new KnownFolderInfo(KnownFolder.Contacts, "Contacts", "Contacts",
                new string[] { "{56784854-C6CB-462B-8169-88E350ACB882}" },
                new string[] { "Contacts" }));

            list.Add(new KnownFolderInfo(KnownFolder.SavedGames, "Saved Games", "Saved Games",
                new string[] { "{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}" },
                new string[] { "Saved Games" }));

            list.Add(new KnownFolderInfo(KnownFolder.Searches, "Searches", "Searches",
                new string[] { "{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}" },
                new string[] { "Searches" }));

            return list;
        }
    }
}
