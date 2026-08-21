using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Native;
using MoveToNewPC.UI;

namespace MoveToNewPC
{
    internal static class Program
    {
        internal const string ProductName = "Move to New PC";
        internal const string Version = "0.6.0 (M0-M3, M6)";

        [STAThread]
        private static void Main(string[] args)
        {
            // Must happen before any control exists.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string exeDirectory = GetExeDirectory();
            Log.Initialise(exeDirectory);
            Log.Info(ProductName + " " + Version);
            Log.Info("Machine: " + SafeMachineName() + "  OS: " + Environment.OSVersion.VersionString
                     + "  Process: " + (IntPtr.Size == 8 ? "64-bit" : "32-bit")
                     + "  CLR: " + Environment.Version);
            Log.Info("EXE directory: " + exeDirectory);
            Log.Info("Data directory: " + Log.DataDirectory);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // The manifest requires administrator, so we should already be elevated. This is
            // a sanity check for odd hosting cases, not a self-elevation path (banned by spec).
            if (!IsElevated())
            {
                Log.Warn("Process does not appear to be elevated despite requireAdministrator.");
                MessageBox.Show(
                    ProductName + " needs to run as an administrator to read other users' files."
                    + Environment.NewLine + Environment.NewLine
                    + "Close it, right-click the program and choose \"Run as administrator\".",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // RegLoadKey needs these switched on; being in Administrators is not enough.
            Privileges.EnableBackupAndRestore();

            try
            {
                using (MainForm form = new MainForm())
                {
                    Application.Run(form);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Fatal error in message loop", ex);
                ShowCrash(ex);
            }
            finally
            {
                Log.Info("Shutting down.");
                Log.Close();
            }
        }

        internal static string GetExeDirectory()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly() ?? typeof(Program).Assembly;
                string location = asm.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return Path.GetDirectoryName(location);
                }
                return Path.GetDirectoryName(new Uri(asm.CodeBase).LocalPath);
            }
            catch (Exception)
            {
                return Environment.CurrentDirectory;
            }
        }

        internal static string SafeMachineName()
        {
            try
            {
                return Environment.MachineName;
            }
            catch (Exception)
            {
                return "(unknown)";
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                           System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal =
                        new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not determine elevation: " + ex.Message);
                return true;
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("Unhandled UI exception", e.Exception);
            ShowCrash(e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log.Error("Unhandled exception (terminating=" + e.IsTerminating + ")", ex);
            if (e.IsTerminating)
            {
                Log.Flush();
            }
        }

        private static void ShowCrash(Exception ex)
        {
            string message = "Something went wrong." + Environment.NewLine + Environment.NewLine
                             + (ex == null ? "(no details)" : ex.Message) + Environment.NewLine + Environment.NewLine
                             + "The log file may help:" + Environment.NewLine + Log.FilePath;
            try
            {
                MessageBox.Show(message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }
    }
}
