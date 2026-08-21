using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace MoveToNewPC.Tests
{
    /// <summary>
    /// A ~150 line test runner instead of NUnit. The spec allows either; NUnit would be a
    /// NuGet dependency in a project whose entire premise is having none, and this has to
    /// run as a bare EXE on a Vista box with nothing installed.
    /// </summary>
    public sealed class TestRunner
    {
        private sealed class TestCase
        {
            public string Group;
            public string Name;
            public Action Body;
        }

        private readonly List<TestCase> _tests = new List<TestCase>();
        private readonly List<string> _failures = new List<string>();
        private string _currentGroup = "general";
        private int _passed;
        private int _skipped;

        public void Group(string name)
        {
            _currentGroup = name;
        }

        public void Test(string name, Action body)
        {
            TestCase test = new TestCase();
            test.Group = _currentGroup;
            test.Name = name;
            test.Body = body;
            _tests.Add(test);
        }

        public int Run(string filter)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string lastGroup = null;

            for (int i = 0; i < _tests.Count; i++)
            {
                TestCase test = _tests[i];

                if (!string.IsNullOrEmpty(filter)
                    && test.Group.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _skipped++;
                    continue;
                }

                if (!string.Equals(lastGroup, test.Group, StringComparison.Ordinal))
                {
                    lastGroup = test.Group;
                    Console.WriteLine();
                    Console.WriteLine("== " + test.Group + " ==");
                }

                try
                {
                    test.Body();
                    _passed++;
                    Console.WriteLine("  PASS  " + test.Name);
                }
                catch (SkipTestException ex)
                {
                    _skipped++;
                    Console.WriteLine("  SKIP  " + test.Name + "  (" + ex.Message + ")");
                }
                catch (Exception ex)
                {
                    _failures.Add(test.Group + " / " + test.Name + ": " + ex.Message);
                    Console.WriteLine("  FAIL  " + test.Name);
                    Console.WriteLine("        " + ex.Message);
                    if (!(ex is AssertException))
                    {
                        Console.WriteLine("        " + ex.GetType().Name);
                        string trace = ex.StackTrace;
                        if (!string.IsNullOrEmpty(trace))
                        {
                            string[] lines = trace.Split('\n');
                            for (int l = 0; l < lines.Length && l < 3; l++)
                            {
                                Console.WriteLine("        " + lines[l].Trim());
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------------------");
            Console.WriteLine("passed: " + _passed
                              + "   failed: " + _failures.Count
                              + "   skipped: " + _skipped
                              + "   in " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");

            if (_failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("FAILURES:");
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("  " + _failures[i]);
                }
            }

            return _failures.Count == 0 ? 0 : 1;
        }
    }

    public sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    public sealed class SkipTestException : Exception
    {
        public SkipTestException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new AssertException("expected true: " + message);
            }
        }

        public static void False(bool condition, string message)
        {
            if (condition)
            {
                throw new AssertException("expected false: " + message);
            }
        }

        public static void Equal(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new AssertException(message + Environment.NewLine
                                          + "        expected: [" + (expected ?? "<null>") + "]"
                                          + Environment.NewLine
                                          + "        actual:   [" + (actual ?? "<null>") + "]");
            }
        }

        public static void Equal(long expected, long actual, string message)
        {
            if (expected != actual)
            {
                throw new AssertException(message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        public static void Null(object value, string message)
        {
            if (value != null)
            {
                throw new AssertException(message + " (expected null, got [" + value + "])");
            }
        }

        public static void NotNull(object value, string message)
        {
            if (value == null)
            {
                throw new AssertException(message + " (was null)");
            }
        }

        public static void Throws(Type exceptionType, Action body, string message)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                if (exceptionType.IsAssignableFrom(ex.GetType()))
                {
                    return;
                }
                throw new AssertException(message + " (threw " + ex.GetType().Name
                                          + " instead of " + exceptionType.Name + ")");
            }
            throw new AssertException(message + " (nothing was thrown)");
        }

        public static void Skip(string why)
        {
            throw new SkipTestException(why);
        }
    }
}
