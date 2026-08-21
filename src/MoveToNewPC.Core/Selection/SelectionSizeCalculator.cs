using System;
using System.Collections.Generic;
using System.Threading;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Profiles;

namespace MoveToNewPC.Core.Selection
{
    /// <summary>
    /// Walks the selected folders on a background thread to produce live size estimates.
    /// The list must never block on this: a 200 GB profile takes minutes to count, and the
    /// operator has to be able to tick boxes the whole time.
    ///
    /// Single worker thread by design. Fanning out across folders would just queue more
    /// seeks on the same spindle and make the whole thing slower on the kind of machine
    /// people are migrating away from.
    /// </summary>
    public sealed class SelectionSizeCalculator : IDisposable
    {
        private readonly IPathExclusion _exclusions;
        private readonly bool _includeHidden;
        private readonly bool _includeSystem;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

        private Thread _worker;
        private Action _onChanged;
        private int _dirty;
        private bool _disposed;

        public SelectionSizeCalculator(IPathExclusion exclusions, bool includeHidden, bool includeSystem)
        {
            _exclusions = exclusions;
            _includeHidden = includeHidden;
            _includeSystem = includeSystem;
        }

        public bool IsRunning
        {
            get { return _worker != null && _worker.IsAlive; }
        }

        /// <summary>
        /// <paramref name="onChanged"/> is raised on the worker thread, at most a few times
        /// a second. UI callers must marshal it themselves.
        /// </summary>
        public void Start(IList<UserSelection> users, Action onChanged)
        {
            if (_worker != null)
            {
                return;
            }

            _onChanged = onChanged;

            List<UserSelection> snapshot = new List<UserSelection>(users);
            _worker = new Thread(delegate() { Run(snapshot); });
            _worker.IsBackground = true;
            _worker.Name = "MTNPC size calculator";
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Start();
        }

        private void Run(List<UserSelection> users)
        {
            try
            {
                WalkOptions options = new WalkOptions();
                options.Exclusions = _exclusions;
                options.IncludeHidden = _includeHidden;
                options.IncludeSystem = _includeSystem;
                options.FollowReparsePoints = false;
                // Counting must not hydrate anything; a size estimate that downloads 40 GB
                // from OneDrive would be an outrageous side effect.
                options.HydrateCloudFiles = false;
                options.IncludeEncryptedFiles = true;
                options.ProgressInterval = 2048;

                DateTime lastNotify = DateTime.MinValue;

                for (int u = 0; u < users.Count && !_cancel.IsCancellationRequested; u++)
                {
                    UserSelection user = users[u];

                    for (int r = 0; r < user.Roots.Count && !_cancel.IsCancellationRequested; r++)
                    {
                        SelectionRoot root = user.Roots[r];
                        if (!root.Exists || string.IsNullOrEmpty(root.SourcePath))
                        {
                            root.SizeState = SizeState.Done;
                            root.EstimatedBytes = 0;
                            root.EstimatedFiles = 0;
                            continue;
                        }

                        if (root.SizeState == SizeState.Done)
                        {
                            continue;
                        }

                        root.SizeState = SizeState.Calculating;
                        Notify(ref lastNotify, true);

                        SelectionRoot captured = root;
                        CountingWalkObserver counter = new CountingWalkObserver(delegate(long files, long bytes)
                        {
                            captured.EstimatedFiles = files;
                            captured.EstimatedBytes = bytes;
                            Notify(ref lastNotify, false);
                        });

                        try
                        {
                            DirectoryWalker.Walk(root.SourcePath, options, counter, _cancel.Token);
                            root.EstimatedFiles = counter.Files;
                            root.EstimatedBytes = counter.Bytes;
                            root.SizeState = _cancel.IsCancellationRequested ? SizeState.NotStarted : SizeState.Done;
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Size calculation failed for " + root.SourcePath, ex);
                            root.SizeState = SizeState.Failed;
                        }

                        UpdateProfileTotals(user);
                        Notify(ref lastNotify, true);
                    }

                    UpdateProfileTotals(user);
                    if (user.Profile != null && !_cancel.IsCancellationRequested)
                    {
                        user.Profile.SizeState = SizeState.Done;
                    }
                    Notify(ref lastNotify, true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Size calculator thread failed", ex);
            }
            finally
            {
                Action handler = _onChanged;
                if (handler != null)
                {
                    try { handler(); }
                    catch (Exception) { }
                }
            }
        }

        private static void UpdateProfileTotals(UserSelection user)
        {
            if (user.Profile == null)
            {
                return;
            }

            long bytes = 0;
            long files = 0;
            for (int i = 0; i < user.Roots.Count; i++)
            {
                if (user.Roots[i].EstimatedBytes > 0)
                {
                    bytes += user.Roots[i].EstimatedBytes;
                }
                if (user.Roots[i].EstimatedFiles > 0)
                {
                    files += user.Roots[i].EstimatedFiles;
                }
            }

            user.Profile.SizeBytes = bytes;
            user.Profile.FileCount = files;
            if (user.Profile.SizeState == SizeState.NotStarted)
            {
                user.Profile.SizeState = SizeState.Calculating;
            }
        }

        private void Notify(ref DateTime lastNotify, bool force)
        {
            Interlocked.Exchange(ref _dirty, 1);

            DateTime now = DateTime.UtcNow;
            if (!force && (now - lastNotify).TotalMilliseconds < 250)
            {
                return;
            }
            lastNotify = now;

            Action handler = _onChanged;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Log.Warn("Size calculator notification threw: " + ex.Message);
                }
            }
        }

        public void Stop()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Thread worker = _worker;
            if (worker != null && worker.IsAlive)
            {
                // Bounded wait: the walker checks cancellation every entry, so this returns
                // promptly, but we must never hang form close on a slow network volume.
                worker.Join(3000);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Stop();
            _cancel.Dispose();
        }
    }
}
