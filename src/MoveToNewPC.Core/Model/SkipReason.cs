namespace MoveToNewPC.Core.Model
{
    /// <summary>
    /// Why an item was not transferred. Every skip in this application carries one of
    /// these and ends up in the report - "it silently didn't copy" is the failure mode
    /// this enum exists to prevent.
    /// </summary>
    public enum SkipReason
    {
        None = 0,
        /// <summary>Matched the built-in or user exclusion list.</summary>
        Excluded,
        /// <summary>Did not match the Advanced-mode include/size/date filters.</summary>
        FilteredOut,
        /// <summary>A junction or symlink. Never followed; profiles are full of loops.</summary>
        ReparsePoint,
        /// <summary>OneDrive/Dropbox files-on-demand placeholder, not locally present.</summary>
        CloudPlaceholder,
        /// <summary>EFS-encrypted; unreadable on the destination machine.</summary>
        Encrypted,
        /// <summary>Sharing or lock violation after retries.</summary>
        Locked,
        /// <summary>Access denied even while elevated.</summary>
        AccessDenied,
        /// <summary>Vanished between enumeration and copy.</summary>
        NotFound,
        /// <summary>Path exceeds even the extended-path limit.</summary>
        PathTooLong,
        /// <summary>Name is an MS-DOS device name (CON, LPT1, ...).</summary>
        ReservedName,
        /// <summary>Rejected by receiver-side path validation.</summary>
        InvalidPath,
        /// <summary>Destination already had it and the policy is Skip.</summary>
        DestinationExists,
        /// <summary>Not enough room on the destination volume.</summary>
        InsufficientSpace,
        /// <summary>SHA-256 did not match after a retry.</summary>
        HashMismatch,
        /// <summary>Read failed for a reason other than locking.</summary>
        ReadError,
        /// <summary>Write failed on the destination.</summary>
        WriteError,
        /// <summary>Operator cancelled before this item was reached.</summary>
        Cancelled,
        /// <summary>Already transferred in an earlier run (resume).</summary>
        AlreadyTransferred,
        /// <summary>Anything else; Detail carries the Win32 error.</summary>
        UnknownError
    }

    public static class SkipReasons
    {
        public static string Describe(SkipReason reason)
        {
            switch (reason)
            {
                case SkipReason.None: return "Copied";
                case SkipReason.Excluded: return "Excluded by rule";
                case SkipReason.FilteredOut: return "Did not match filter";
                case SkipReason.ReparsePoint: return "Junction or symbolic link (not followed)";
                case SkipReason.CloudPlaceholder: return "Cloud placeholder (not stored locally)";
                case SkipReason.Encrypted: return "EFS-encrypted (would be unreadable on the new PC)";
                case SkipReason.Locked: return "In use by another program";
                case SkipReason.AccessDenied: return "Access denied";
                case SkipReason.NotFound: return "No longer exists";
                case SkipReason.PathTooLong: return "Path too long";
                case SkipReason.ReservedName: return "Reserved Windows device name";
                case SkipReason.InvalidPath: return "Rejected: unsafe path";
                case SkipReason.DestinationExists: return "Already exists at the destination";
                case SkipReason.InsufficientSpace: return "Not enough free space";
                case SkipReason.HashMismatch: return "Verification failed (checksum mismatch)";
                case SkipReason.ReadError: return "Could not be read";
                case SkipReason.WriteError: return "Could not be written";
                case SkipReason.Cancelled: return "Cancelled";
                case SkipReason.AlreadyTransferred: return "Already transferred (resumed run)";
                default: return "Unknown error";
            }
        }

        /// <summary>True when the item is genuinely a failure rather than a deliberate skip.</summary>
        public static bool IsFailure(SkipReason reason)
        {
            switch (reason)
            {
                case SkipReason.Locked:
                case SkipReason.AccessDenied:
                case SkipReason.NotFound:
                case SkipReason.PathTooLong:
                case SkipReason.InvalidPath:
                case SkipReason.InsufficientSpace:
                case SkipReason.HashMismatch:
                case SkipReason.ReadError:
                case SkipReason.WriteError:
                case SkipReason.UnknownError:
                    return true;
                default:
                    return false;
            }
        }
    }
}
