using System;
using System.Diagnostics;
using System.Globalization;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Core.Net
{
    /// <summary>
    /// Opens the listening ports for the length of one transfer, then closes them again.
    ///
    /// netsh rather than the COM firewall API: the API's interop surface changed between
    /// Vista and 7, and netsh advfirewall has been stable across the whole supported range.
    /// The cost is shelling out, which is why this is done once per session and not per
    /// connection.
    ///
    /// Failing to add a rule is never fatal. Plenty of machines have a third-party firewall,
    /// or a policy that forbids this, and the operator can always allow it by hand; refusing
    /// to run would be worse than trying and reporting.
    /// </summary>
    public sealed class FirewallRule : IDisposable
    {
        private const string RuleName = "MoveToNewPC (temporary)";
        private bool _added;
        private bool _disposed;

        public bool Added
        {
            get { return _added; }
        }

        /// <summary>Adds inbound TCP and UDP allowances. Returns false when it could not.</summary>
        public bool TryAdd(int tcpPort, int udpPort)
        {
            // Remove any rule left behind by a previous run that did not shut down cleanly.
            Remove();

            bool tcp = Run("advfirewall firewall add rule name=\"" + RuleName + "\""
                           + " dir=in action=allow protocol=TCP localport="
                           + tcpPort.ToString(CultureInfo.InvariantCulture)
                           + " profile=private,domain");

            bool udp = Run("advfirewall firewall add rule name=\"" + RuleName + "\""
                           + " dir=in action=allow protocol=UDP localport="
                           + udpPort.ToString(CultureInfo.InvariantCulture)
                           + " profile=private,domain");

            _added = tcp || udp;

            if (_added)
            {
                Log.Info("Temporary firewall rule added for TCP "
                         + tcpPort.ToString(CultureInfo.InvariantCulture)
                         + " and UDP " + udpPort.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                Log.Warn("Could not add a firewall rule. If the other PC cannot see this one, "
                         + "allow MoveToNewPC through the firewall by hand.");
            }
            return _added;
        }

        /// <summary>
        /// Deliberately public and idempotent: the rule must not outlive the transfer, so
        /// this is called from Dispose and can safely be called again.
        /// </summary>
        public void Remove()
        {
            Run("advfirewall firewall delete rule name=\"" + RuleName + "\"");
            _added = false;
        }

        private static bool Run(string arguments)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("netsh", arguments);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    // Drain both pipes before waiting: a full buffer would deadlock us.
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("netsh failed: " + ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_added)
            {
                Remove();
                Log.Info("Temporary firewall rule removed.");
            }
        }
    }
}
