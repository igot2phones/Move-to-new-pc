using System;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// Entry point for the portable subset only. Exists so the pure tests can be compiled
    /// and executed on a non-Windows build machine, where the Win32-dependent half of the
    /// code cannot even be compiled (no registry, no SHA256Cng, no P/Invoke targets).
    ///
    /// The net40 build selects Program.Main explicitly with -main:, so having two entry
    /// points in the project is deliberate and not an accident.
    /// </summary>
    public static class PureProgram
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("MoveToNewPC - portable (non-Windows-API) tests");
            Console.WriteLine("=============================================");

            TestRunner runner = new TestRunner();
            PureTests.Register(runner);
            return runner.Run(args.Length > 0 ? args[0] : null);
        }
    }
}
