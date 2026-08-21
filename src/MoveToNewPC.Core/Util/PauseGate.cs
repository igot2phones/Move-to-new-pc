using System;
using System.Threading;

namespace MoveToNewPC.Core.Util
{
    /// <summary>
    /// Pause/resume for a worker thread. No async/await available on this target, so the
    /// worker simply blocks on a ManualResetEventSlim between chunks.
    /// </summary>
    public sealed class PauseGate : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(true);
        private volatile bool _paused;

        public bool IsPaused
        {
            get { return _paused; }
        }

        public void Pause()
        {
            _paused = true;
            _gate.Reset();
        }

        public void Resume()
        {
            _paused = false;
            _gate.Set();
        }

        /// <summary>Blocks while paused. Returns false if cancellation happened while waiting.</summary>
        public bool Wait(CancellationToken cancel)
        {
            if (!_paused)
            {
                return !cancel.IsCancellationRequested;
            }

            while (_paused)
            {
                if (cancel.IsCancellationRequested)
                {
                    return false;
                }
                if (_gate.Wait(200))
                {
                    break;
                }
            }
            return !cancel.IsCancellationRequested;
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}
