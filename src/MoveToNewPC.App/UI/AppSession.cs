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
        OfflinePackage
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

        /// <summary>Where restored files land on the new PC.</summary>
        public string DestinationFolder;

        /// <summary>
        /// Receiver only. SingleFolder by default: putting everything in one place cannot
        /// disturb anything already on this PC, so it is the safe default to ship.
        /// </summary>
        public MoveToNewPC.Core.Transfer.DestinationLayout Layout =
            MoveToNewPC.Core.Transfer.DestinationLayout.SingleFolder;

        /// <summary>Full path of the .mtnpc-package being written or read.</summary>
        public string PackagePath;

        /// <summary>
        /// Never persisted and never logged. Held only for the life of the wizard so the
        /// sink and the reader can derive their keys.
        /// </summary>
        public string PackagePassphrase;

        /// <summary>
        /// The paired connection, once a LAN pairing screen has established one. The
        /// transfer page takes it over and owns closing it.
        /// </summary>
        public MoveToNewPC.Core.Net.SecureChannel Channel;

        /// <summary>Set by the receiver's pairing screen so the transfer page can show it.</summary>
        public string PeerMachineName;

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
            Layout = MoveToNewPC.Core.Transfer.DestinationLayout.SingleFolder;
            PackagePath = null;
            PackagePassphrase = null;
            if (Channel != null)
            {
                Channel.Dispose();
                Channel = null;
            }
            PeerMachineName = null;
            Manifest = null;
            ManifestPath = null;
            LastResult = null;
            LastReport = null;
            LastReportPath = null;
            SessionNotes.Clear();
        }
    }
}
