using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MoveToNewPC.Core.IO;
using MoveToNewPC.Core.Native;

namespace MoveToNewPC.Tests
{
    /// <summary>Scratch-directory helpers. Long-path aware, because the tests deliberately
    /// build trees that System.IO cannot even delete.</summary>
    public static class TestFs
    {
        public static string CreateScratch()
        {
            string path = Path.Combine(Path.GetTempPath(), "mtnpc-tests-" + Guid.NewGuid().ToString("N"));
            int error;
            if (!NativeFile.CreateDirectoryRecursive(path, out error))
            {
                throw new IOException("Could not create scratch directory: " + NativeFile.DescribeError(error));
            }
            return path;
        }

        public static string WriteFile(string path, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            return WriteFile(path, bytes);
        }

        public static string WriteFile(string path, byte[] content)
        {
            int error;
            string parent = LongPath.GetDirectoryName(path);
            if (!NativeFile.CreateDirectoryRecursive(parent, out error))
            {
                throw new IOException("Could not create " + LongPath.ToDisplay(parent) + ": "
                                      + NativeFile.DescribeError(error));
            }

            using (FileStream stream = NativeFile.CreateWrite(path, true, 4096, out error))
            {
                if (stream == null)
                {
                    throw new IOException("Could not create " + LongPath.ToDisplay(path) + ": "
                                          + NativeFile.DescribeError(error));
                }
                if (content != null && content.Length > 0)
                {
                    stream.Write(content, 0, content.Length);
                }
            }
            return path;
        }

        public static byte[] ReadAllBytes(string path)
        {
            int error;
            using (FileStream stream = NativeFile.OpenRead(path, true, 4096, out error))
            {
                if (stream == null)
                {
                    throw new IOException("Could not read " + LongPath.ToDisplay(path) + ": "
                                          + NativeFile.DescribeError(error));
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, read);
                    }
                    return memory.ToArray();
                }
            }
        }

        public static string ReadAllText(string path)
        {
            return Encoding.UTF8.GetString(ReadAllBytes(path));
        }

        /// <summary>Recursive delete that copes with long paths, read-only bits and junctions.</summary>
        public static void DeleteTree(string path)
        {
            string extended = LongPath.ToExtended(LongPath.TrimTrailingSeparators(path));

            uint attributes = NativeMethods.GetFileAttributesW(extended);
            if (attributes == NativeMethods.INVALID_FILE_ATTRIBUTES)
            {
                return;
            }

            if ((attributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                int error;
                NativeFile.Delete(extended, out error);
                return;
            }

            // A junction is removed with RemoveDirectory - descending into it would delete
            // the target's contents, which in these tests is the rest of the scratch tree.
            if ((attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) == 0)
            {
                NativeMethods.WIN32_FIND_DATA data;
                using (SafeFindHandle find = NativeMethods.FindFirstFileW(
                           LongPath.Combine(extended, "*"), out data))
                {
                    if (!find.IsInvalid)
                    {
                        do
                        {
                            if (data.cFileName == "." || data.cFileName == "..")
                            {
                                continue;
                            }
                            DeleteTree(LongPath.Combine(extended, data.cFileName));
                        }
                        while (NativeMethods.FindNextFileW(find, out data));
                    }
                }
            }

            if (!NativeMethods.RemoveDirectoryW(extended))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ERROR_ACCESS_DENIED)
                {
                    NativeMethods.SetFileAttributesW(extended, NativeMethods.FILE_ATTRIBUTE_NORMAL);
                    NativeMethods.RemoveDirectoryW(extended);
                }
            }
        }

        /// <summary>Builds a path well past MAX_PATH under the given root.</summary>
        public static string MakeDeepPath(string root, int levels, string leafName)
        {
            string current = root;
            for (int i = 0; i < levels; i++)
            {
                current = LongPath.Combine(current, "lvl" + i + "-" + new string('d', 30));
            }
            return LongPath.Combine(current, leafName);
        }

        public static bool TryMakeJunction(string linkPath, string targetPath)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + LongPath.ToDisplay(linkPath) + "\" \"" + LongPath.ToDisplay(targetPath) + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(info))
                {
                    process.WaitForExit(15000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
