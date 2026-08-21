using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Tools.MakeTestTree
{
    /// <summary>
    /// Builds a deliberately nasty folder tree: the things that break migration tools.
    /// Run it, point the app at the result, and check the report tells the truth.
    ///
    /// Usage:
    ///   MakeTestTree.exe &lt;folder&gt; [--big] [--lock]
    ///     --big    also create a sparse file larger than 4 GB
    ///     --lock   hold some files open with no sharing and wait, so the locked-file
    ///              path can be exercised against a live lock
    /// </summary>
    internal static class Program
    {
        private static int _created;
        private static int _failed;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: MakeTestTree.exe <folder> [--big] [--lock]");
                return 1;
            }

            string root = args[0];
            bool big = HasFlag(args, "--big");
            bool hold = HasFlag(args, "--lock");

            Console.WriteLine("Building test tree in " + root);

            int error;
            if (!NativeFile.CreateDirectoryRecursive(root, out error))
            {
                Console.WriteLine("Could not create " + root + ": " + NativeFile.DescribeError(error));
                return 2;
            }

            MakeOrdinary(root);
            MakeUnicode(root);
            MakeLongPaths(root);
            MakeAwkwardNames(root);
            MakeAttributeCases(root);
            MakeJunction(root);
            if (big)
            {
                MakeHugeSparseFile(root);
            }

            Console.WriteLine();
            Console.WriteLine("Created " + _created + " item(s), " + _failed + " failure(s).");
            Console.WriteLine("Expected behaviour when the app scans this tree:");
            Console.WriteLine("  * 'loop-junction' is reported as a reparse point and NOT followed");
            Console.WriteLine("  * files under 'deep' copy correctly despite paths over 260 characters");
            Console.WriteLine("  * 'CON', 'PRN.txt' and the trailing-dot/space names are reported, not silently lost");
            Console.WriteLine("  * 'Case.txt' and 'case.TXT' are two separate files on NTFS-with-case-sensitivity"); 
            Console.WriteLine("    and one collision otherwise - the report should say which");

            if (hold)
            {
                HoldFilesOpen(root);
            }

            return _failed == 0 ? 0 : 3;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void MakeOrdinary(string root)
        {
            string dir = LongPath.Combine(root, "ordinary");
            Dir(dir);
            Write(LongPath.Combine(dir, "empty.txt"), 0);
            Write(LongPath.Combine(dir, "small.txt"), 12);
            Write(LongPath.Combine(dir, "one-megabyte.bin"), 1024 * 1024);
            Dir(LongPath.Combine(dir, "empty-folder"));
        }

        private static void MakeUnicode(string root)
        {
            string dir = LongPath.Combine(root, "unicode");
            Dir(dir);
            Write(LongPath.Combine(dir, "caf\u00e9 r\u00e9sum\u00e9.txt"), 20);
            Write(LongPath.Combine(dir, "\u65e5\u672c\u8a9e\u306e\u30d5\u30a1\u30a4\u30eb.txt"), 20);
            // Right-to-left: Arabic and Hebrew file names.
            Write(LongPath.Combine(dir, "\u0645\u0644\u0641 \u0639\u0631\u0628\u064a.txt"), 20);
            Write(LongPath.Combine(dir, "\u05e7\u05d5\u05d1\u05e5 \u05e2\u05d1\u05e8\u05d9.txt"), 20);
            Write(LongPath.Combine(dir, "emoji \U0001F600 file.txt"), 20);
            Write(LongPath.Combine(dir, "combining a\u0301e\u0301.txt"), 20);
        }

        private static void MakeLongPaths(string root)
        {
            string dir = LongPath.Combine(root, "deep");
            Dir(dir);

            // Well past MAX_PATH: 12 nested folders of 40 characters each.
            string current = dir;
            for (int i = 0; i < 12; i++)
            {
                current = LongPath.Combine(current,
                    "level-" + i.ToString("00", CultureInfo.InvariantCulture) + "-"
                    + new string('x', 30));
                Dir(current);
            }

            Write(LongPath.Combine(current, "deep-file.txt"), 64);
            Write(LongPath.Combine(current, new string('n', 200) + ".txt"), 64);

            Console.WriteLine("  deepest path is "
                              + LongPath.ToDisplay(LongPath.Combine(current, "deep-file.txt")).Length
                              + " characters");
        }

        private static void MakeAwkwardNames(string root)
        {
            string dir = LongPath.Combine(root, "awkward");
            Dir(dir);

            // All of these are only creatable through the \\?\ prefix, which is exactly why
            // they belong in this tree: a tool using System.IO cannot even make them.
            Write(LongPath.Combine(dir, "CON"), 10);
            Write(LongPath.Combine(dir, "PRN.txt"), 10);
            Write(LongPath.Combine(dir, "LPT1"), 10);
            Write(LongPath.Combine(dir, "trailing-dot."), 10);
            Write(LongPath.Combine(dir, "trailing-space "), 10);
            Write(LongPath.Combine(dir, "Case.txt"), 10);
            Write(LongPath.Combine(dir, "case.TXT"), 11);
            Write(LongPath.Combine(dir, "spaces   and   gaps.txt"), 10);
            Write(LongPath.Combine(dir, "semi;colon,comma'quote.txt"), 10);
            Write(LongPath.Combine(dir, "#hash&ersand%percent.txt"), 10);
            Write(LongPath.Combine(dir, "." + "hidden-by-name.txt"), 10);
        }

        private static void MakeAttributeCases(string root)
        {
            string dir = LongPath.Combine(root, "attributes");
            Dir(dir);

            string readOnly = LongPath.Combine(dir, "read-only.txt");
            Write(readOnly, 16);
            SetAttributes(readOnly, NativeMethods.FILE_ATTRIBUTE_READONLY);

            string hidden = LongPath.Combine(dir, "hidden.txt");
            Write(hidden, 16);
            SetAttributes(hidden, NativeMethods.FILE_ATTRIBUTE_HIDDEN);

            string system = LongPath.Combine(dir, "system.txt");
            Write(system, 16);
            SetAttributes(system, NativeMethods.FILE_ATTRIBUTE_SYSTEM);

            string both = LongPath.Combine(dir, "hidden-system.txt");
            Write(both, 16);
            SetAttributes(both, NativeMethods.FILE_ATTRIBUTE_HIDDEN | NativeMethods.FILE_ATTRIBUTE_SYSTEM);

            // Things the default exclusion list must drop.
            Write(LongPath.Combine(dir, "desktop.ini"), 40);
            Write(LongPath.Combine(dir, "Thumbs.db"), 40);
            Write(LongPath.Combine(dir, "NTUSER.DAT"), 40);
            Write(LongPath.Combine(dir, "leftover.mtnpc-part"), 40);
            Dir(LongPath.Combine(dir, "Cache"));
            Write(LongPath.Combine(LongPath.Combine(dir, "Cache"), "cached.bin"), 4096);
        }

        private static void MakeJunction(string root)
        {
            string target = LongPath.Combine(root, "ordinary");
            string link = LongPath.Combine(root, "loop-junction");

            // Shelling out to mklink rather than hand-building a REPARSE_DATA_BUFFER: this is
            // a developer tool, not shipped behaviour, and mklink is what a human would type.
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + LongPath.ToDisplay(link) + "\" \"" + LongPath.ToDisplay(target) + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                using (Process process = Process.Start(info))
                {
                    process.WaitForExit(15000);
                    if (process.ExitCode == 0)
                    {
                        _created++;
                        Console.WriteLine("  junction: loop-junction -> ordinary");
                    }
                    else
                    {
                        _failed++;
                        Console.WriteLine("  junction FAILED: " + process.StandardError.ReadToEnd().Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  junction FAILED: " + ex.Message);
            }

            // A self-referencing junction: the classic infinite loop.
            string selfLink = LongPath.Combine(root, "self-junction");
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + LongPath.ToDisplay(selfLink) + "\" \"" + LongPath.ToDisplay(root) + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit(15000);
                    if (process.ExitCode == 0)
                    {
                        _created++;
                        Console.WriteLine("  junction: self-junction -> the test root itself (infinite loop if followed)");
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private const uint FSCTL_SET_SPARSE = 0x000900C4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
                                                   IntPtr lpInBuffer, uint nInBufferSize,
                                                   IntPtr lpOutBuffer, uint nOutBufferSize,
                                                   out uint lpBytesReturned, IntPtr lpOverlapped);

        private static void MakeHugeSparseFile(string root)
        {
            string dir = LongPath.Combine(root, "huge");
            Dir(dir);
            string path = LongPath.Combine(dir, "over-four-gigabytes.bin");

            try
            {
                int error;
                using (SafeFileHandle handle = NativeFile.CreateWriteHandle(path, true, out error))
                {
                    if (handle == null)
                    {
                        _failed++;
                        Console.WriteLine("  huge file FAILED: " + NativeFile.DescribeError(error));
                        return;
                    }

                    // Sparse, so the test tree does not actually consume 4 GB of disk. The file
                    // still reports a >4 GB length, which is what exercises the 32-bit overflow
                    // bugs this case exists to catch.
                    uint returned;
                    DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);

                    using (FileStream stream = new FileStream(handle, FileAccess.Write, 4096, false))
                    {
                        stream.SetLength(5L * 1024 * 1024 * 1024);
                        stream.Seek(5L * 1024 * 1024 * 1024 - 16, SeekOrigin.Begin);
                        byte[] tail = Encoding.ASCII.GetBytes("END-OF-BIG-FILE");
                        stream.Write(tail, 0, tail.Length);
                    }
                }

                _created++;
                Console.WriteLine("  sparse file: huge\\over-four-gigabytes.bin (5 GB logical)");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  huge file FAILED: " + ex.Message);
            }
        }

        private static void HoldFilesOpen(string root)
        {
            string path = LongPath.Combine(LongPath.Combine(root, "ordinary"), "small.txt");
            int error;

            SafeFileHandle handle = NativeMethods.CreateFileW(
                LongPath.ToExtended(path),
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                0,                                   // no sharing at all
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                Console.WriteLine("Could not lock " + path + ": " + NativeFile.DescribeError(error));
                return;
            }

            using (handle)
            {
                Console.WriteLine();
                Console.WriteLine("Holding an exclusive lock on:");
                Console.WriteLine("  " + LongPath.ToDisplay(path));
                Console.WriteLine("Run the migration now; that file should be reported as in use.");
                Console.WriteLine("Press Enter to release.");
                Console.ReadLine();
            }
        }

        // ---- helpers -----------------------------------------------------------

        private static void Dir(string path)
        {
            int error;
            if (NativeFile.CreateDirectoryRecursive(path, out error))
            {
                _created++;
            }
            else
            {
                _failed++;
                Console.WriteLine("  DIR FAILED " + LongPath.ToDisplay(path) + ": " + NativeFile.DescribeError(error));
            }
        }

        private static void Write(string path, int bytes)
        {
            int error;
            try
            {
                using (FileStream stream = NativeFile.CreateWrite(path, true, 4096, out error))
                {
                    if (stream == null)
                    {
                        _failed++;
                        Console.WriteLine("  FILE FAILED " + LongPath.ToDisplay(path) + ": "
                                          + NativeFile.DescribeError(error));
                        return;
                    }

                    if (bytes > 0)
                    {
                        byte[] buffer = new byte[bytes];
                        for (int i = 0; i < bytes; i++)
                        {
                            buffer[i] = (byte)('A' + (i % 26));
                        }
                        stream.Write(buffer, 0, buffer.Length);
                    }
                }
                _created++;
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FILE FAILED " + LongPath.ToDisplay(path) + ": " + ex.Message);
            }
        }

        private static void SetAttributes(string path, uint attributes)
        {
            int error;
            if (!NativeFile.SetAttributes(path, attributes, out error))
            {
                Console.WriteLine("  attribute FAILED " + LongPath.ToDisplay(path) + ": "
                                  + NativeFile.DescribeError(error));
            }
        }
    }
}
