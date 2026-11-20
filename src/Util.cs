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

    /// <summary>What happens to an item sent to the Recycle Bin.</summary>
    enum RecycleFate { Recycled, TooBig, NoBin }

    /// <summary>
    /// The Recycle Bin can't keep everything: a drive may have no bin (network shares, USB sticks), the bin may be
    /// turned off for it, and an item bigger than the bin's maximum size can't go in at all. In those cases Windows
    /// deletes the item permanently, and when confirmations are off it does so silently, so check before promising
    /// the user they can restore it.
    /// </summary>
    static class RecycleBin
    {
        const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";

        /// <summary>Predicts whether an item of <paramref name="bytes"/> at <paramref name="path"/> can go to the Recycle Bin.</summary>
        public static RecycleFate Predict(string path, long bytes)
        {
            long max = MaxBytes(path);
            if (max == 0) return RecycleFate.NoBin;
            return max > 0 && bytes > max ? RecycleFate.TooBig : RecycleFate.Recycled;   // unknown: assume it fits (Move still asks)
        }

        /// <summary>Maximum size of the Recycle Bin on the path's drive: 0 when there's none or it's off, -1 when unknown.</summary>
        public static long MaxBytes(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (root.StartsWith(@"\\")) return 0;   // network share
                var type = new DriveInfo(root).DriveType;
                if (type == DriveType.Network || type == DriveType.Removable || type == DriveType.CDRom) return 0;

                // The bin's settings are kept per volume GUID; GetVolumePathName also resolves folders mounted as drives.
                var mount = new System.Text.StringBuilder(260);
                var volume = new System.Text.StringBuilder(64);
                if (!GetVolumePathName(path, mount, mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
                    return -1;
                string name = volume.ToString();   // \\?\Volume{guid}\
                int open = name.IndexOf('{'), close = name.IndexOf('}');
                if (open < 0 || close < open) return -1;
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BitBucket + name.Substring(open, close - open + 1)))
                {
                    if (key == null) return -1;
                    object nuke = key.GetValue("NukeOnDelete"), max = key.GetValue("MaxCapacity");
                    if (nuke is int && (int)nuke != 0) return 0;
                    return max is int ? (long)(uint)(int)max * 1024 * 1024 : -1;   // stored in MB
                }
            }
            catch (ArgumentException) { return -1; }
            catch (IOException) { return -1; }
            catch (System.Security.SecurityException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
            catch (NotSupportedException) { return -1; }
        }

        /// <summary>
        /// Sends a file or folder to the Recycle Bin. If Windows can't keep it there it asks the user before deleting it
        /// for good. That question only appears when the operation has an owner window; without one Windows deletes the
        /// item silently, so pass the form's handle.
        /// </summary>
        public static void Move(string path, IntPtr owner)
        {
            Run(path, owner, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING);
        }

        /// <summary>Deletes a file or folder permanently; only after the user agreed to exactly that.</summary>
        public static void DeletePermanently(string path, IntPtr owner)
        {
            Run(path, owner, FOF_NOCONFIRMATION);
        }

        static void Run(string path, IntPtr owner, ushort flags)
        {
            var op = new SHFILEOPSTRUCT { hwnd = owner, wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = flags };
            int result = 0;
            Shell.RunSta(() => result = SHFileOperation(ref op));
            if (op.fAnyOperationsAborted || result == ERROR_CANCELLED) throw new OperationCanceledException();
            if (result == ERROR_ACCESS_DENIED || result == DE_ACCESSDENIEDSRC) throw new UnauthorizedAccessException();
            if (result != 0) throw new IOException(L.T("common.shell_error", result.ToString("X")));
        }

        /// <summary>An item in the current user's Recycle Bin.</summary>
        public sealed class Item
        {
            public string IndexPath;     // $I file: original path, size and deletion time
            public string DataPath;      // $R file or folder: the deleted item itself
            public string OriginalPath;
            public long Size;
            public DateTime Deleted;

            public Entry ToEntry()
            {
                return new Entry
                {
                    Path = DataPath, Name = Path.GetFileName(OriginalPath), Location = Path.GetDirectoryName(OriginalPath),
                    Deleted = Deleted, Bytes = Size, Files = 1,
                };
            }
        }

        /// <summary>
        /// Lists the current user's Recycle Bin items on every local drive by reading the $I index files that
        /// Windows keeps in X:\$Recycle.Bin\&lt;SID&gt;. Each $Ixxxxxx pairs with a $Rxxxxxx holding the item.
        /// </summary>
        public static List<Item> Items()
        {
            var items = new List<Item>();
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null) return items;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)) continue;
                    var bin = new DirectoryInfo(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", user.Value));
                    if (!bin.Exists) continue;
                    foreach (var index in bin.GetFiles("$I*"))
                    {
                        var item = ReadIndex(index);
                        if (item != null) items.Add(item);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return items;
        }

        /// <summary>Parses a $I file: version 1 (Vista to 8.1) has a fixed 260-character path, version 2 (Windows 10+) a counted one.</summary>
        static Item ReadIndex(FileInfo index)
        {
            byte[] b;
            try { b = File.ReadAllBytes(index.FullName); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0);
            string original;
            if (version == 1 && b.Length >= 24 + 520) original = System.Text.Encoding.Unicode.GetString(b, 24, 520);
            else if (version == 2 && b.Length >= 28)
            {
                int chars = BitConverter.ToInt32(b, 24);
                if (chars <= 0 || 28 + chars * 2L > b.Length) return null;
                original = System.Text.Encoding.Unicode.GetString(b, 28, chars * 2);
            }
            else return null;
            original = original.TrimEnd('\0');
            int nul = original.IndexOf('\0');
            if (nul >= 0) original = original.Substring(0, nul);

            string data = Path.Combine(index.DirectoryName, "$R" + index.Name.Substring(2));
            if (!File.Exists(data) && !Directory.Exists(data)) return null;   // orphaned index; Explorer doesn't show it either
            DateTime deleted;
            try { deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16)); }
            catch (ArgumentOutOfRangeException) { deleted = index.LastWriteTime; }
            return new Item { IndexPath = index.FullName, DataPath = data, OriginalPath = original, Size = BitConverter.ToInt64(b, 8), Deleted = deleted };
        }

        /// <summary>Deletes one item for good: its data first, then its index so it disappears from the bin. True if it's gone.</summary>
        public static bool Destroy(Item item)
        {
            try
            {
                FileSystemInfo data = Directory.Exists(item.DataPath) ? (FileSystemInfo)new DirectoryInfo(item.DataPath) : new FileInfo(item.DataPath);
                if (data.Exists && (Env.IsReparsePoint(data) ? !DeleteLink(data) : !Cleaner.DeleteTree(data))) return false;
                File.Delete(item.IndexPath);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static bool DeleteLink(FileSystemInfo link)
        {
            var dir = link as DirectoryInfo;
            if (dir != null) Directory.Delete(dir.FullName, false);   // removes the link itself, never what it points to
            else File.Delete(link.FullName);
            return true;
        }

        /// <summary>Lets Explorer redraw the Recycle Bin icon (full/empty) after items were deleted directly.</summary>
        public static void RefreshIcon()
        {
            try { SHUpdateRecycleBinIcon(); }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("shell32.dll")]
        static extern void SHUpdateRecycleBinIcon();

        const uint FO_DELETE = 3;
        const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
        const int ERROR_ACCESS_DENIED = 5, DE_ACCESSDENIEDSRC = 0x78, ERROR_CANCELLED = 1223;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumePathName(string lpszFileName, System.Text.StringBuilder lpszVolumePathName, int cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, int cchBufferLength);
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

    /// <summary>
    /// The list used across the app: double-buffered details view whose groups can be collapsed (Windows' own
    /// chevron) and whose group headers stay readable in dark mode, where Windows draws them like plain items.
    /// </summary>
    class BufferedListView : ListView
    {
        readonly HashSet<string> collapsed = new HashSet<string>(StringComparer.CurrentCulture);
        bool collapsible;
        Font headerFont;

        public BufferedListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            Dock = DockStyle.Fill;
        }

        /// <summary>Remembers which groups the user collapsed; call before clearing the groups to refill them.</summary>
        public void SaveGroupStates()
        {
            if (!IsHandleCreated) return;
            foreach (var g in NativeGroups())
            {
                if ((g.State & LVGS_COLLAPSED) != 0) collapsed.Add(g.Header);
                else collapsed.Remove(g.Header);
            }
        }

        /// <summary>Lets every group be collapsed, restoring the ones the user had collapsed. Call after (re)filling the groups.</summary>
        public void MakeGroupsCollapsible()
        {
            collapsible = true;
            if (!IsHandleCreated) return;   // applied once the handle exists
            foreach (var g in NativeGroups())
            {
                uint state = LVGS_COLLAPSIBLE | (collapsed.Contains(g.Header) ? LVGS_COLLAPSED : 0);
                var info = new LVGROUP { cbSize = Marshal.SizeOf(typeof(LVGROUP)), mask = LVGF_STATE, stateMask = LVGS_COLLAPSIBLE | LVGS_COLLAPSED, state = state };
                SendMessage(Handle, LVM_SETGROUPINFO, (IntPtr)g.Id, ref info);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (collapsible) BeginInvoke((Action)MakeGroupsCollapsible);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (headerFont != null) { headerFont.Dispose(); headerFont = null; }
        }

        protected override void WndProc(ref Message m)
        {
            // WinForms handles left clicks itself and the native collapse never happens, so toggle the group here.
            // The whole header is the target, not just the small chevron.
            if (collapsible && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK) && ToggleGroupAt(m.LParam)) return;

            // Dark mode: Windows draws group headers like plain items (white, no separator line), so after it draws
            // one, repaint its text in bold accent color with a line, leaving the collapse chevron alone.
            if (m.Msg == WM_REFLECT_NOTIFY && Theme.IsDark && Groups.Count > 0)
            {
                var hdr = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (hdr.code == NM_CUSTOMDRAW)
                {
                    var cd = (NMLVCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMLVCUSTOMDRAW));
                    if (cd.dwItemType == LVCDI_GROUP)
                    {
                        if (cd.nmcd.dwDrawStage == CDDS_PREPAINT) { m.Result = (IntPtr)CDRF_NOTIFYPOSTPAINT; return; }
                        if (cd.nmcd.dwDrawStage == CDDS_POSTPAINT)
                        {
                            PaintGroupHeader(cd.nmcd.hdc, (int)cd.nmcd.dwItemSpec.ToInt64());
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }
                }
            }
            base.WndProc(ref m);
        }

        bool ToggleGroupAt(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            var hit = new LVHITTESTINFO { pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)), iGroup = -1 };
            SendMessage(Handle, LVM_HITTEST, (IntPtr)(-1), ref hit);   // wParam -1: also report group parts
            if ((hit.flags & (LVHT_EX_GROUP_HEADER | LVHT_EX_GROUP_COLLAPSE)) == 0 || hit.iGroup < 0) return false;
            var group = NativeGroup(hit.iGroup);
            if (group == null || (group.State & LVGS_COLLAPSIBLE) == 0) return false;
            bool collapse = (group.State & LVGS_COLLAPSED) == 0;
            var info = new LVGROUP { cbSize = Marshal.SizeOf(typeof(LVGROUP)), mask = LVGF_STATE, stateMask = LVGS_COLLAPSED, state = collapse ? LVGS_COLLAPSED : 0 };
            SendMessage(Handle, LVM_SETGROUPINFO, (IntPtr)group.Id, ref info);
            if (collapse) collapsed.Add(group.Header);
            else collapsed.Remove(group.Header);
            Focus();
            return true;
        }

        void PaintGroupHeader(IntPtr hdc, int id)
        {
            var rect = new RECT { top = LVGGR_HEADER };
            if (SendMessage(Handle, LVM_GETGROUPRECT, (IntPtr)id, ref rect) == IntPtr.Zero) return;
            var group = NativeGroup(id);
            if (group == null) return;
            var header = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            // Leave the collapse chevron (drawn by Windows at the right end) as it is.
            if ((group.State & LVGS_COLLAPSIBLE) != 0) header.Width -= header.Height + 6;
            if (header.Width <= 0) return;
            if (headerFont == null) headerFont = new Font(Font, FontStyle.Bold);
            using (var g = Graphics.FromHdc(hdc))
            {
                using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, header);
                int pad = (int)Math.Round(8 * g.DpiX / 96f);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                var textRect = new Rectangle(header.Left + pad, header.Top, header.Width - pad, header.Height);
                TextRenderer.DrawText(g, group.Header, headerFont, textRect, Theme.Accent, flags);
                int textWidth = TextRenderer.MeasureText(g, group.Header, headerFont, textRect.Size, flags).Width;
                int lineStart = textRect.Left + textWidth + pad, y = header.Top + header.Height / 2;
                if (lineStart < header.Right - pad)
                    using (var line = new Pen(Theme.Border)) g.DrawLine(line, lineStart, y, header.Right - pad, y);
            }
        }

        sealed class GroupInfo
        {
            public int Id;
            public string Header;
            public uint State;
        }

        IEnumerable<GroupInfo> NativeGroups()
        {
            int count = (int)SendMessage(Handle, LVM_GETGROUPCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count; i++)
            {
                var g = QueryGroup(LVM_GETGROUPINFOBYINDEX, i);
                if (g != null) yield return g;
            }
        }

        GroupInfo NativeGroup(int id) { return QueryGroup(LVM_GETGROUPINFO, id); }

        GroupInfo QueryGroup(int message, int key)
        {
            const int Chars = 260;
            IntPtr buffer = Marshal.AllocHGlobal(Chars * 2);
            try
            {
                var info = new LVGROUP
                {
                    cbSize = Marshal.SizeOf(typeof(LVGROUP)), mask = LVGF_HEADER | LVGF_GROUPID | LVGF_STATE,
                    stateMask = LVGS_COLLAPSIBLE | LVGS_COLLAPSED, pszHeader = buffer, cchHeader = Chars,
                };
                if (SendMessage(Handle, message, (IntPtr)key, ref info) == IntPtr.Zero && message == LVM_GETGROUPINFOBYINDEX) return null;
                return new GroupInfo { Id = info.iGroupId, Header = Marshal.PtrToStringUni(buffer) ?? "", State = info.state };
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        const int WM_REFLECT_NOTIFY = 0x204E, NM_CUSTOMDRAW = -12, LVCDI_GROUP = 1;
        const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONDBLCLK = 0x203, LVM_HITTEST = 0x1012;
        const uint LVHT_EX_GROUP_HEADER = 0x10000000, LVHT_EX_GROUP_COLLAPSE = 0x40000000;
        const int CDDS_PREPAINT = 0x1, CDDS_POSTPAINT = 0x2, CDRF_NOTIFYPOSTPAINT = 0x10;
        const int LVM_GETGROUPINFO = 0x1095, LVM_SETGROUPINFO = 0x1093, LVM_GETGROUPCOUNT = 0x1098, LVM_GETGROUPINFOBYINDEX = 0x1099, LVM_GETGROUPRECT = 0x1062;
        const int LVGGR_HEADER = 1;
        const uint LVGF_HEADER = 0x1, LVGF_STATE = 0x4, LVGF_GROUPID = 0x10, LVGS_COLLAPSED = 0x1, LVGS_COLLAPSIBLE = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }

        [StructLayout(LayoutKind.Sequential)]
        struct LVHITTESTINFO { public Point pt; public uint flags; public int iItem; public int iSubItem; public int iGroup; }

        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW
        {
            public NMHDR hdr;
            public int dwDrawStage;
            public IntPtr hdc;
            public RECT rc;
            public IntPtr dwItemSpec;
            public uint uItemState;
            public IntPtr lItemlParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMLVCUSTOMDRAW
        {
            public NMCUSTOMDRAW nmcd;
            public int clrText, clrTextBk, iSubItem, dwItemType, clrFace, iIconEffect, iIconPhase, iPartId, iStateId;
            public RECT rcText;
            public uint uAlign;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct LVGROUP
        {
            public int cbSize;
            public uint mask;
            public IntPtr pszHeader;
            public int cchHeader;
            public IntPtr pszFooter;
            public int cchFooter;
            public int iGroupId;
            public uint stateMask;
            public uint state;
            public uint uAlign;
            public IntPtr pszSubtitle;
            public uint cchSubtitle;
            public IntPtr pszTask;
            public uint cchTask;
            public IntPtr pszDescriptionTop;
            public uint cchDescriptionTop;
            public IntPtr pszDescriptionBottom;
            public uint cchDescriptionBottom;
            public int iTitleImage;
            public int iExtendedImage;
            public int iFirstItem;
            public uint cItems;
            public IntPtr pszSubsetTitle;
            public uint cchSubsetTitle;
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVGROUP lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVHITTESTINFO lParam);
    }

    /// <summary>Formatted text for the read-only details panes (Programs and Leftovers tabs).</summary>
    static class RichText
    {
        public static void Append(RichTextBox box, string text, Font font, Color? color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font ?? box.Font;
            box.SelectionColor = color ?? box.ForeColor;
            box.AppendText(text);
        }

        public static void Section(RichTextBox box, string heading, string body, Color? color)
        {
            Append(box, heading + "\n", new Font(box.Font, FontStyle.Bold), null);
            Append(box, body + "\n\n", null, color);
        }

        public static void Title(RichTextBox box, string text)
        {
            Append(box, text + "\n", new Font(box.Font.FontFamily, box.Font.Size + 2, FontStyle.Bold), null);
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

        /// <summary>
        /// Gives the bottom panel of a horizontal SplitContainer about <paramref name="height"/> px (at 96 DPI).
        /// It's applied the first time the container has a real size — a page that hasn't been shown yet may still be
        /// 150×150 — and always clamped to the valid range, since an out-of-range SplitterDistance throws.
        /// </summary>
        public static void DockDetailsPane(SplitContainer split, int height)
        {
            bool done = false;
            Action apply = () =>
            {
                int pane = (int)(height * DpiScale(split));
                if (done || split.Height < pane * 2) return;
                int max = split.Height - split.Panel2MinSize - split.SplitterWidth;
                if (max <= split.Panel1MinSize) return;
                split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(max, split.Height - pane));
                done = true;
            };
            split.SizeChanged += (s, e) => apply();
            apply();
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
