using System;
using System.Collections.Generic;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Profiles;

namespace MoveToNewPC.Core.Selection
{
    /// <summary>
    /// Turns a discovered profile into the tree of things the operator can tick.
    /// Tier A on, Tier B off, Tier C empty until they add something.
    /// </summary>
    public static class SelectionBuilder
    {
        public static UserSelection BuildFor(UserProfile profile, bool includeAppData)
        {
            UserSelection selection = new UserSelection();
            selection.Profile = profile;
            selection.Selected = false;

            IList<KnownFolderInfo> known = KnownFolderCatalog.Items;
            for (int i = 0; i < known.Count; i++)
            {
                KnownFolderInfo info = known[i];

                string path;
                if (!profile.KnownFolders.TryGetValue(info.Folder, out path) || string.IsNullOrEmpty(path))
                {
                    continue;
                }

                SelectionRoot root = new SelectionRoot();
                root.Tier = SelectionTier.KnownFolder;
                root.Folder = info.Folder;
                root.IsKnownFolder = true;
                root.Label = info.Label;
                root.SourcePath = path;
                root.DestinationRelativeRoot = info.DestinationName;
                root.Exists = NativeFile.DirectoryExists(path);
                root.Selected = root.Exists;

                if (!root.Exists)
                {
                    root.Note = "Not present on this PC";
                }
                else if (!IsUnderProfile(profile.ProfilePath, path))
                {
                    // Worth saying out loud: the folder was redirected somewhere else and we
                    // found it through the registry rather than by guessing.
                    root.Note = "Redirected to " + LongPath.ToDisplay(path);
                }

                selection.Roots.Add(root);
            }

            if (includeAppData)
            {
                selection.Roots.AddRange(AppDataCatalog.DetectFor(profile));
            }

            return selection;
        }

        /// <summary>Adds a Tier C folder the operator browsed to or dragged in.</summary>
        public static SelectionRoot AddCustomFolder(UserSelection selection, string path)
        {
            if (selection == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            string trimmed = LongPath.TrimTrailingSeparators(path);
            for (int i = 0; i < selection.Roots.Count; i++)
            {
                if (string.Equals(LongPath.ToDisplay(selection.Roots[i].SourcePath),
                                  LongPath.ToDisplay(trimmed), StringComparison.OrdinalIgnoreCase))
                {
                    return selection.Roots[i];
                }
            }

            SelectionRoot root = new SelectionRoot();
            root.Tier = SelectionTier.Custom;
            root.IsKnownFolder = false;
            root.Label = LongPath.GetFileName(trimmed);
            if (string.IsNullOrEmpty(root.Label))
            {
                root.Label = LongPath.ToDisplay(trimmed);
            }
            root.SourcePath = trimmed;
            root.DestinationRelativeRoot = MakeSafeDestinationName(trimmed);
            root.Exists = NativeFile.DirectoryExists(trimmed) || NativeFile.FileExists(trimmed);
            root.Selected = root.Exists;
            root.Note = "Added folder: " + LongPath.ToDisplay(trimmed);

            selection.Roots.Add(root);
            return root;
        }

        /// <summary>
        /// A custom folder lands under "Moved from &lt;drive&gt;\&lt;name&gt;" so a folder
        /// dragged from D:\Work never collides with the user's own Documents\Work.
        /// </summary>
        private static string MakeSafeDestinationName(string sourcePath)
        {
            string display = LongPath.ToDisplay(sourcePath);
            string name = LongPath.GetFileName(LongPath.TrimTrailingSeparators(display));
            string drive = string.Empty;

            if (display.Length >= 2 && display[1] == ':')
            {
                drive = display.Substring(0, 1).ToUpperInvariant();
            }

            if (string.IsNullOrEmpty(name))
            {
                name = "Folder";
            }

            return string.IsNullOrEmpty(drive)
                   ? LongPath.Combine("Moved folders", name)
                   : LongPath.Combine("Moved folders", drive + " drive - " + name);
        }

        private static bool IsUnderProfile(string profilePath, string candidate)
        {
            if (string.IsNullOrEmpty(profilePath) || string.IsNullOrEmpty(candidate))
            {
                return true;
            }
            return LongPath.GetRelativePath(profilePath, candidate) != null;
        }
    }
}
