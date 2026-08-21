using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Profiles;

namespace MoveToNewPC.Core.Selection
{
    public enum SelectionTier
    {
        /// <summary>Tier A: Desktop, Documents, ... On by default.</summary>
        KnownFolder = 0,
        /// <summary>Tier B: curated application data. Off by default, one checkbox away.</summary>
        AppData = 1,
        /// <summary>Tier C: anything the operator adds by hand. Advanced mode only.</summary>
        Custom = 2
    }

    /// <summary>One source folder chosen for transfer, and where it lands on the new PC.</summary>
    public sealed class SelectionRoot
    {
        public SelectionTier Tier;
        /// <summary>Set only for Tier A.</summary>
        public KnownFolder Folder;
        public bool IsKnownFolder;

        public string Label;
        public string SourcePath;
        /// <summary>Destination path relative to the mapped user root, e.g. "Documents".</summary>
        public string DestinationRelativeRoot;

        public bool Selected;
        public bool Exists;
        /// <summary>Shown next to the entry: "re-creatable", "version sensitive", etc.</summary>
        public string Note;

        public long EstimatedBytes = -1;
        public long EstimatedFiles = -1;
        public SizeState SizeState = SizeState.NotStarted;

        public override string ToString()
        {
            return Label + " -> " + DestinationRelativeRoot;
        }
    }

    public sealed class UserSelection
    {
        public UserProfile Profile;
        public bool Selected;
        public List<SelectionRoot> Roots = new List<SelectionRoot>();

        public long SelectedBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Roots.Count; i++)
                {
                    if (Roots[i].Selected && Roots[i].EstimatedBytes > 0)
                    {
                        total += Roots[i].EstimatedBytes;
                    }
                }
                return total;
            }
        }
    }

    /// <summary>Advanced-mode filters. All optional; null/0 means "no constraint".</summary>
    public sealed class FilterSettings
    {
        public List<string> IncludeGlobs = new List<string>();
        public List<string> ExcludeGlobs = new List<string>();
        /// <summary>Extensions with the dot, e.g. ".jpg". Empty means all.</summary>
        public List<string> IncludeExtensions = new List<string>();
        public long MinSizeBytes;
        public long MaxSizeBytes;
        public DateTime? ModifiedAfterUtc;
        public DateTime? ModifiedBeforeUtc;

        public bool IsEmpty
        {
            get
            {
                return IncludeGlobs.Count == 0
                       && ExcludeGlobs.Count == 0
                       && IncludeExtensions.Count == 0
                       && MinSizeBytes <= 0
                       && MaxSizeBytes <= 0
                       && !ModifiedAfterUtc.HasValue
                       && !ModifiedBeforeUtc.HasValue;
            }
        }
    }

    /// <summary>Everything the operator picked, plus how to treat it.</summary>
    public sealed class TransferSelection
    {
        public List<UserSelection> Users = new List<UserSelection>();
        public FilterSettings Filters = new FilterSettings();
        /// <summary>Populated by ExclusionRules.CreateDefault(); editable in Advanced mode.</summary>
        public ExclusionRules Exclusions;

        public bool IncludeAppData;
        public bool HydrateCloudFiles;
        public bool IncludeEncryptedFiles;
        public bool IncludeHidden = true;
        public bool IncludeSystem;
    }
}
