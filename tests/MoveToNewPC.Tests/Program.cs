using System;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// Full test run. Must be executed on Windows, elevated, because half of these tests
    /// exercise Win32 behaviour that has no equivalent anywhere else.
    ///
    /// Usage: MoveToNewPC.Tests.exe [filter]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Log.Initialise(null);
            Log.MinimumLevel = LogLevel.Warn;

            Console.WriteLine("MoveToNewPC test harness");
            Console.WriteLine("========================");
            Console.WriteLine("OS:      " + Environment.OSVersion.VersionString);
            Console.WriteLine("CLR:     " + Environment.Version);
            Console.WriteLine("Process: " + (IntPtr.Size == 8 ? "64-bit" : "32-bit"));
            Console.WriteLine("Log:     " + Log.FilePath);

            TestRunner runner = new TestRunner();
            PureTests.Register(runner);
            WindowsTests.Register(runner);

            int exitCode = runner.Run(args.Length > 0 ? args[0] : null);

            Log.Close();

            if (Environment.UserInteractive && args.Length == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }

            return exitCode;
        }
    }
}
