using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MoveToNewPC.Core.Diagnostics;

namespace MoveToNewPC.UI
{
    /// <summary>
    /// Small WinForms helpers. Deliberately boring: SystemColors and SystemFonts
    /// everywhere so the app looks like a Windows utility from Vista through 11, with no
    /// custom-drawn chrome and no third-party controls.
    /// </summary>
    public static class Ui
    {
        /// <summary>
        /// Marshals an action onto the UI thread. There is no async/await on this target,
        /// so every worker-thread callback goes through here.
        /// Safe to call before the handle exists or after disposal - it just drops.
        /// </summary>
        public static void Post(Control control, Action action)
        {
            if (control == null || action == null)
            {
                return;
            }

            try
            {
                if (control.IsDisposed || !control.IsHandleCreated)
                {
                    return;
                }
                if (control.InvokeRequired)
                {
                    control.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
                // Handle destroyed between the check and the call.
            }
        }

        /// <summary>Synchronous variant, for the rare case a worker needs an answer back.</summary>
        public static void Send(Control control, Action action)
        {
            if (control == null || action == null)
            {
                return;
            }
            try
            {
                if (control.IsDisposed || !control.IsHandleCreated)
                {
                    return;
                }
                if (control.InvokeRequired)
                {
                    control.Invoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        public static Font DefaultFont
        {
            get { return SystemFonts.MessageBoxFont ?? Control.DefaultFont; }
        }

        public static Font Bold(Font source)
        {
            return new Font(source, FontStyle.Bold);
        }

        public static Font Heading(Font source)
        {
            return new Font(source.FontFamily, source.Size + 2.25f, FontStyle.Bold);
        }

        public static Label MakeLabel(string text, bool bold, int x, int y, int width)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Text = text;
            label.Location = new Point(x, y);
            label.Width = width;
            label.Height = 18;
            label.TextAlign = ContentAlignment.MiddleLeft;
            if (bold)
            {
                label.Font = Bold(DefaultFont);
            }
            return label;
        }

        public static Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 26;
            b.UseVisualStyleBackColor = true;
            b.FlatStyle = FlatStyle.Standard;
            return b;
        }

        public static DialogResult Warn(IWin32Window owner, string message)
        {
            return MessageBox.Show(owner, message, Program.ProductName,
                                   MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static DialogResult Error(IWin32Window owner, string message)
        {
            return MessageBox.Show(owner, message, Program.ProductName,
                                   MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static DialogResult Info(IWin32Window owner, string message)
        {
            return MessageBox.Show(owner, message, Program.ProductName,
                                   MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool Confirm(IWin32Window owner, string message)
        {
            return MessageBox.Show(owner, message, Program.ProductName,
                                   MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Warn("Clipboard copy failed: " + ex.Message);
            }
        }

        /// <summary>Opens Explorer with the file selected, or the folder if that fails.</summary>
        public static void ShowInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start("explorer.exe", "\"" + folder + "\"");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open Explorer for " + path + ": " + ex.Message);
            }
        }

        public static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open " + path + ": " + ex.Message);
            }
        }
    }
}
