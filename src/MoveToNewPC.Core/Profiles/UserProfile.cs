using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;

namespace MoveToNewPC.Core.Profiles
{
    public enum SizeState
    {
        NotStarted = 0,
        Calculating,
        Done,
        Failed
    }

    /// <summary>One row of HKLM\...\ProfileList that survived filtering.</summary>
    public sealed class UserProfile
    {
        public string Sid;
        /// <summary>Account name from LookupAccountSid, or the folder name if unresolvable.</summary>
        public string AccountName;
        public string DomainName;
        /// <summary>ProfileImagePath, environment-expanded. Often does NOT match AccountName.</summary>
        public string ProfilePath;

        public bool ProfileExists;
        /// <summary>True when HKU\&lt;sid&gt; is already present (user is logged on).</summary>
        public bool IsHiveLoaded;
        /// <summary>The operator's own account. Its files are the most likely to be locked.</summary>
        public bool IsCurrentUser;

        public long SizeBytes;
        public long FileCount;
        public SizeState SizeState = SizeState.NotStarted;
        public string SizeError;

        /// <summary>Resolved User Shell Folders. Missing entries mean "not redirected, not present".</summary>
        public Dictionary<KnownFolder, string> KnownFolders = new Dictionary<KnownFolder, string>();

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(DomainName) && !string.IsNullOrEmpty(AccountName))
                {
                    return DomainName + "\\" + AccountName;
                }
                return string.IsNullOrEmpty(AccountName) ? Sid : AccountName;
            }
        }

        public override string ToString()
        {
            return DisplayName + " (" + ProfilePath + ")";
        }
    }

    /// <summary>A ProfileList entry we deliberately did not show, and why.</summary>
    public sealed class FilteredProfile
    {
        public string Sid;
        public string ProfilePath;
        public string Reason;

        public FilteredProfile(string sid, string profilePath, string reason)
        {
            Sid = sid;
            ProfilePath = profilePath;
            Reason = reason;
        }
    }

    public sealed class ProfileEnumerationResult
    {
        public List<UserProfile> Profiles = new List<UserProfile>();
        public List<FilteredProfile> Filtered = new List<FilteredProfile>();
        public List<string> Warnings = new List<string>();
    }
}
