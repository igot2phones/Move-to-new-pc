using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Selection;

namespace MoveToNewPC.Core.Manifests
{
    /// <summary>
    /// The manifest is the unit of resumability and the contract between the two machines.
    /// It is written and read as a streamed line format (see docs/PROTOCOL.md) rather than
    /// held entirely in memory: a 200 GB profile is millions of entries.
    /// </summary>
    public sealed class ManifestRoot
    {
        public int UserIndex;
        public int RootIndex;
        public SelectionTier Tier;
        /// <summary>Source folder on the old PC (display form, no \\?\ prefix).</summary>
        public string SourcePath;
        /// <summary>Where it lands under the mapped user root, e.g. "Documents".</summary>
        public string DestinationRelativeRoot;
        public string Label;
    }

    public sealed class ManifestUser
    {
        public int UserIndex;
        public string Sid;
        public string AccountName;
        public string ProfilePath;
        /// <summary>Suggested local account on the new PC; the receiver may override it.</summary>
        public string DestinationHint;
        public List<ManifestRoot> Roots = new List<ManifestRoot>();
    }

    /// <summary>One file. Timestamps are FILETIME ticks.</summary>
    public sealed class ManifestEntry
    {
        public int UserIndex;
        public int RootIndex;
        public string RelativePath;
        public long Length;
        public uint Attributes;
        public long CreationTimeUtc;
        public long LastAccessTimeUtc;
        public long LastWriteTimeUtc;
        /// <summary>Lower-case hex SHA-256. Null until the sender has actually read the file.</summary>
        public string Sha256;

        public string Key
        {
            get
            {
                return UserIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                       + RootIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                       + RelativePath;
            }
        }
    }

    public sealed class ManifestDirectory
    {
        public int UserIndex;
        public int RootIndex;
        public string RelativePath;
        public uint Attributes;
        public long CreationTimeUtc;
        public long LastAccessTimeUtc;
        public long LastWriteTimeUtc;
    }

    public sealed class ManifestTotals
    {
        public long FileCount;
        public long ByteCount;
        public long DirectoryCount;
        public long SkippedCount;
        public long SkippedBytes;
    }

    /// <summary>
    /// In-memory manifest header: users, roots, totals and skips. File entries are streamed
    /// separately by ManifestWriter/ManifestReader and are NOT held here.
    /// </summary>
    public sealed class TransferManifest
    {
        public const int FormatVersion = 1;

        /// <summary>Random id; the receiver's resume journal is keyed on it.</summary>
        public string ManifestId;
        public DateTime CreatedUtc;
        public string SourceMachine;
        public string ToolVersion;

        public List<ManifestUser> Users = new List<ManifestUser>();
        public ManifestTotals Totals = new ManifestTotals();
        public List<SkippedItem> ScanSkips = new List<SkippedItem>();

        public ManifestUser FindUser(int userIndex)
        {
            for (int i = 0; i < Users.Count; i++)
            {
                if (Users[i].UserIndex == userIndex)
                {
                    return Users[i];
                }
            }
            return null;
        }

        public ManifestRoot FindRoot(int userIndex, int rootIndex)
        {
            ManifestUser u = FindUser(userIndex);
            if (u == null)
            {
                return null;
            }
            for (int i = 0; i < u.Roots.Count; i++)
            {
                if (u.Roots[i].RootIndex == rootIndex)
                {
                    return u.Roots[i];
                }
            }
            return null;
        }
    }
}
