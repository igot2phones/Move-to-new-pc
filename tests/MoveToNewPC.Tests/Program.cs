using System;
using System.Text;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// Full test run. Must be executed on Windows, elevated, because half of these tests
    /// exercise Win32 behaviour that has no equivalent anywhere else.
    ///
    /// Usage: MoveToNewPC.Tests.exe [filter] [--no-pause]
    ///
    ///   filter      run only tests whose group or name contains this text
    ///   --no-pause  never wait for a keypress at the end (use this from any script)
    ///
    /// Exit code is 0 when everything passed and 1 when anything failed, so a build or an
    /// automation agent can just look at ERRORLEVEL.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string filter = null;
            // Default to pausing only for a human who double-clicked the EXE. Any script
            // should pass --no-pause; without it an unattended run would hang here forever.
            bool pause = Environment.UserInteractive;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (string.IsNullOrEmpty(argument))
                {
                    continue;
                }

                if (string.Equals(argument, "--no-pause", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, "--ci", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, "/no-pause", StringComparison.OrdinalIgnoreCase))
                {
                    pause = false;
                }
                else if (argument.StartsWith("-", StringComparison.Ordinal))
                {
                    Console.WriteLine("Ignoring unknown option: " + argument);
                }
                else if (filter == null)
                {
                    filter = argument;
                }
            }

            // Test names include Japanese, Hebrew and Arabic on purpose. Without this the
            // console mangles them and a real failure becomes unreadable.
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // Legacy console host that will not take it; the tests still run.
            }

            Log.Initialise(null);
            Log.MinimumLevel = LogLevel.Warn;

            Console.WriteLine("MoveToNewPC test harness");
            Console.WriteLine("========================");
            Console.WriteLine("OS:       " + Environment.OSVersion.VersionString);
            Console.WriteLine("CLR:      " + Environment.Version);
            Console.WriteLine("Process:  " + (IntPtr.Size == 8 ? "64-bit" : "32-bit"));
            Console.WriteLine("Elevated: " + IsElevated());
            Console.WriteLine("Log:      " + Log.FilePath);
            if (filter != null)
            {
                Console.WriteLine("Filter:   " + filter);
            }

            TestRunner runner = new TestRunner();
            PureTests.Register(runner);
            WindowsTests.Register(runner);

            int exitCode = runner.Run(filter);

            Log.Close();

            if (pause)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }

            return exitCode;
        }

        private static string IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                           System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal =
                        new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                           ? "yes" : "NO - many tests will fail";
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
