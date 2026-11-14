using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace Sweepfella
{
    static class Env
    {
        public static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        public static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        public static readonly string LocalLow = Combine(UserProfile, "AppData", "LocalLow");

        /// <summary>Path.Combine that returns null when the base folder doesn't exist on this machine.</summary>
        public static string Combine(string root, params string[] parts)
        {
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(new[] { root }.Concat(parts).ToArray());
        }

        public static IEnumerable<string> SubDirs(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return new string[0];
            try { return Directory.GetDirectories(dir); }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        public static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }

        public static bool IsReparsePoint(FileSystemInfo info)
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>Strips quotes, environment variables and arguments to get the executable path.</summary>
        public static string ExecutableFromCommand(string command)
        {
            string file, args;
            SplitCommand(command, out file, out args);
            return file;
        }

        public static void SplitCommand(string command, out string file, out string args)
        {
            file = "";
            args = "";
            if (string.IsNullOrWhiteSpace(command)) return;
            string cmd = Environment.ExpandEnvironmentVariables(command.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end < 0) { file = cmd.Trim('"'); return; }
                file = cmd.Substring(1, end - 1);
                args = cmd.Substring(end + 1).Trim();
                return;
            }
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0)
            {
                file = cmd.Substring(0, exe + 4);
                args = cmd.Substring(exe + 4).Trim();
                return;
            }
            int comma = cmd.IndexOf(',');   // DisplayIcon: "C:\x\app.ico,0"
            file = comma > 0 ? cmd.Substring(0, comma) : cmd;
        }
    }

    static class Bytes
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Format(long bytes)
        {
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? bytes + " B" : v.ToString(v < 10 ? "0.0" : "0") + " " + Units[u];
        }
    }

    /// <summary>Refuses to empty critical folders, even if a path is miscomputed.</summary>
    static class PathGuard
    {
        static readonly HashSet<string> Forbidden = BuildForbidden();

        static HashSet<string> BuildForbidden()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new[]
            {
                Env.Windows, Env.UserProfile, Env.Local, Env.Roaming, Env.LocalLow, Env.ProgramData,
                Env.ProgramFiles, Env.ProgramFilesX86,
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Env.Combine(Env.UserProfile, "Downloads"),
                string.IsNullOrEmpty(Env.UserProfile) ? null : Path.GetDirectoryName(Env.UserProfile),
            };
            foreach (var f in folders)
                if (!string.IsNullOrEmpty(f)) set.Add(Env.Normalize(f));
            return set;
        }

        public static bool IsSafeToEmpty(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir)) return false;
            string full;
            try { full = Env.Normalize(dir); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            if (string.Equals(full, Env.Normalize(Path.GetPathRoot(full)), StringComparison.OrdinalIgnoreCase)) return false;
            return !Forbidden.Contains(full);
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int BCM_SETSHIELD = 0x160C;

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);
    }

    static class Elevation
    {
        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool RestartAsAdmin()
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" })) { }
                return true;
            }
            catch (Win32Exception) { return false; }   // the user cancelled the UAC prompt
        }
    }

    static class Shell
    {
        public static void Open(string path)
        {
            if (Directory.Exists(path))
                using (Process.Start("explorer.exe", "\"" + path + "\"")) { }
        }

        /// <summary>Runs an action on a dedicated STA thread (shell APIs prefer it) and waits for it to finish.</summary>
        public static void RunSta(Action action)
        {
            Exception error = null;
            var t = new System.Threading.Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw error;
        }
    }

    /// <summary>Sorts a ListView by column using each subitem's Tag (number, date) or its text.</summary>
    sealed class ColumnSorter : IComparer
    {
        public int Column;
        public bool Descending;

        public int Compare(object x, object y)
        {
            var a = Key((ListViewItem)x);
            var b = Key((ListViewItem)y);
            int r;
            if (a is string || b is string || a == null || b == null || a.GetType() != b.GetType())
                r = string.Compare(Convert.ToString(a), Convert.ToString(b), StringComparison.CurrentCultureIgnoreCase);
            else
                r = ((IComparable)a).CompareTo(b);
            return Descending ? -r : r;
        }

        object Key(ListViewItem item)
        {
            if (Column >= item.SubItems.Count) return "";
            var sub = item.SubItems[Column];
            return sub.Tag ?? sub.Text;
        }

        public static void Attach(ListView list)
        {
            var sorter = new ColumnSorter();
            list.ListViewItemSorter = sorter;
            list.ColumnClick += (s, e) =>
            {
                if (sorter.Column == e.Column) sorter.Descending = !sorter.Descending;
                else
                {
                    sorter.Column = e.Column;
                    // Sizes and dates: largest / newest first.
                    object sample = list.Items.Count > 0 && list.Items[0].SubItems.Count > e.Column ? list.Items[0].SubItems[e.Column].Tag : null;
                    sorter.Descending = sample is long || sample is DateTime;
                }
                list.Sort();
            };
        }
    }

    sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            Dock = DockStyle.Fill;
        }
    }

    static class Ui
    {
        public static float DpiScale(Control c)
        {
            using (var g = c.CreateGraphics()) return g.DpiX / 96f;
        }

        public static void ScaleColumns(ListView list)
        {
            float s = DpiScale(list);
            if (Math.Abs(s - 1f) < 0.01f) return;
            foreach (ColumnHeader col in list.Columns) col.Width = (int)(col.Width * s);
        }

        /// <summary>
        /// Stretches a column to fill the free space without overflowing
        /// (Width = -2 causes a horizontal scrollbar). Defaults to the last column.
        /// </summary>
        public static void FillColumn(ListView list, int index = -1)
        {
            int n = list.Columns.Count;
            if (n == 0) return;
            if (index < 0) index = n - 1;
            int used = 0;
            for (int i = 0; i < n; i++)
                if (i != index) used += list.Columns[i].Width;
            int width = Math.Max((int)(120 * DpiScale(list)), list.ClientSize.Width - used - 1);
            if (list.Columns[index].Width != width) list.Columns[index].Width = width;
        }

        public static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 28), Margin = new Padding(6, 3, 0, 3), FlatStyle = FlatStyle.System };
            b.Click += onClick;
            return b;
        }

        public static void SetShield(Button b)
        {
            Native.SendMessage(b.Handle, Native.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
        }

        public static ListViewItem.ListViewSubItem AddSub(ListViewItem item, string text, object sortKey)
        {
            var sub = item.SubItems.Add(text ?? "");
            sub.Tag = sortKey;
            return sub;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            p.Controls.AddRange(controls);
            return p;
        }

        public static string Ago(DateTime when)
        {
            var days = (int)(DateTime.Now - when).TotalDays;
            if (days < 1) return L.T("ago.today");
            if (days < 2) return L.T("ago.yesterday");
            if (days < 60) return L.T("ago.days", days);
            if (days < 730) return L.T("ago.months", days / 30);
            return L.T("ago.years", days / 365);
        }
    }
}
