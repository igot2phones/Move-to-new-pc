using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.Core.Reporting
{
    /// <summary>
    /// Everything the final screen and the saved report file need. Built from a
    /// <see cref="TransferResult"/> plus context the engine does not know about.
    /// </summary>
    public sealed class TransferReport
    {
        public string Title = "Move to New PC";
        /// <summary>"Dry run", "Local copy", "LAN transfer (sender)", ...</summary>
        public string Mode;
        public string SourceMachine;
        public string DestinationMachine;
        public string DestinationDescription;

        public DateTime StartedUtc;
        public DateTime FinishedUtc;

        public long FilesCopied;
        public long FilesSkipped;
        public long FilesFailed;
        public long DirectoriesCreated;
        public long BytesCopied;
        public long BytesSkipped;

        public bool Cancelled;
        public string FailureMessage;

        public string LogFilePath;
        public List<SkippedItem> Skipped = new List<SkippedItem>();
        public List<string> Notes = new List<string>();

        public TimeSpan Duration
        {
            get { return FinishedUtc > StartedUtc ? FinishedUtc - StartedUtc : TimeSpan.Zero; }
        }

        public double AverageBytesPerSecond
        {
            get
            {
                double seconds = Duration.TotalSeconds;
                return seconds > 0.001 ? BytesCopied / seconds : 0;
            }
        }

        public static TransferReport FromResult(TransferResult result, string mode)
        {
            TransferReport r = new TransferReport();
            r.Mode = mode;
            r.StartedUtc = result.StartedUtc;
            r.FinishedUtc = result.FinishedUtc;
            r.FilesCopied = result.FilesCopied;
            r.FilesSkipped = result.FilesSkipped;
            r.FilesFailed = result.FilesFailed;
            r.DirectoriesCreated = result.DirectoriesCreated;
            r.BytesCopied = result.BytesCopied;
            r.BytesSkipped = result.BytesSkipped;
            r.Cancelled = result.Cancelled;
            r.FailureMessage = result.FailureMessage;
            r.Skipped.AddRange(result.Skipped);
            return r;
        }
    }
}
