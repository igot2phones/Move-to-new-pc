using System;
using System.Collections.Generic;
using MoveToNewPC.Core.Manifests;
using MoveToNewPC.Core.Profiles;
using MoveToNewPC.Core.Reporting;
using MoveToNewPC.Core.Selection;
using MoveToNewPC.Core.Transfer;

namespace MoveToNewPC.UI
{
    public enum AppRole
    {
        None = 0,
        Sender,
        Receiver
    }

    public enum TransportKind
    {
        None = 0,
        /// <summary>UDP discovery + encrypted TCP on the local subnet. The default path.</summary>
        Lan,
        /// <summary>Same code path as LAN; the UI helps with link-local addressing.</summary>
        DirectCable,
        /// <summary>Encrypted package on an external drive or share.</summary>
        OfflinePackage,
        /// <summary>
        /// No network at all: copy straight into a folder on this machine. Exists so the
        /// file engine can be exercised end-to-end before the network transport lands.
        /// </summary>
        LocalFolder
    }

    /// <summary>State shared by the wizard pages. One instance per MainForm.</summary>
    public sealed class AppSession
    {
        public AppRole Role = AppRole.None;
        public TransportKind Transport = TransportKind.None;

        public bool AdvancedMode;

        public ProfileEnumerationResult Profiles;
        public TransferSelection Selection = new TransferSelection();
        public CopyOptions CopyOptions = CopyOptions.Defaults();

        /// <summary>Destination folder for LocalFolder / OfflinePackage modes.</summary>
        public string DestinationFolder;

        public TransferManifest Manifest;
        public string ManifestPath;

        public TransferResult LastResult;
        public TransferReport LastReport;
        public string LastReportPath;

        public readonly List<string> SessionNotes = new List<string>();

        public void Reset()
        {
            Role = AppRole.None;
            Transport = TransportKind.None;
            Profiles = null;
            Selection = new TransferSelection();
            CopyOptions = CopyOptions.Defaults();
            DestinationFolder = null;
            Manifest = null;
            ManifestPath = null;
            LastResult = null;
            LastReport = null;
            LastReportPath = null;
            SessionNotes.Clear();
        }
    }
}
