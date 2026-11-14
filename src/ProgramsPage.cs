using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sweepfella
{
    sealed class ProgramsPage : UserControl
    {
        static readonly Color Warning = Color.FromArgb(176, 96, 0);

        // The dependency scan is slow, so its result survives language changes (which rebuild the window).
        static UsageScanner lastScan;

        readonly ListView list = new BufferedListView { MultiSelect = false, ShowItemToolTips = true };
        readonly TextBox search = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 5, 12, 3) };
        readonly RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, DetectUrls = false };
        readonly SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        readonly ProgressBar progress = new ProgressBar { Width = 160, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false, Anchor = AnchorStyles.Left };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("programs.loading") };
        readonly Button scan, refresh, openLocation, uninstall, cancel;

        List<InstalledProgram> programs = new List<InstalledProgram>();
        CancellationTokenSource cts;
        bool busy;

        public ProgramsPage()
        {
            Dock = DockStyle.Fill;

            // The Name column stretches; the rest have fixed widths that fit in 830 px.
            list.Columns.Add(L.T("programs.col.name"), 260);
            list.Columns.Add(L.T("programs.col.publisher"), 140);
            list.Columns.Add(L.T("programs.col.version"), 95);
            list.Columns.Add(L.T("programs.col.size"), 65, HorizontalAlignment.Right);
            list.Columns.Add(L.T("programs.col.installed"), 80);
            list.Columns.Add(L.T("programs.col.status"), 140);
            ColumnSorter.Attach(list);
            list.SelectedIndexChanged += (s, e) => ShowDetails();
            list.Resize += (s, e) => Ui.FillColumn(list, 0);
            search.TextChanged += (s, e) => Fill();

            scan = Ui.MakeButton(L.T("programs.scan"), OnScan);
            refresh = Ui.MakeButton(L.T("programs.refresh"), async (s, e) => await ReloadAsync());
            openLocation = Ui.MakeButton(L.T("programs.open_location"), OnOpenLocation);
            uninstall = Ui.MakeButton(L.T("programs.uninstall"), OnUninstall);
            cancel = Ui.MakeButton(L.T("common.cancel"), (s, e) => { if (cts != null) cts.Cancel(); });
            cancel.Visible = false;

            var searchLabel = new Label { AutoSize = true, Text = L.T("programs.search"), Margin = new Padding(3, 8, 0, 3) };
            var top = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(searchLabel, 0, 0);
            top.Controls.Add(search, 1, 0);
            top.Controls.Add(scan, 2, 0);
            top.Controls.Add(refresh, 3, 0);

            details.Text = L.T("programs.select_hint");
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);
            split.Panel2.Padding = new Padding(4, 8, 4, 0);

            var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(progress, 0, 0);
            bottom.Controls.Add(status, 1, 0);
            bottom.Controls.Add(cancel, 2, 0);
            bottom.Controls.Add(openLocation, 3, 0);
            bottom.Controls.Add(uninstall, 4, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);
        }

        public bool IsBusy { get { return busy; } }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.ScaleColumns(list);
            split.SplitterDistance = Math.Max(100, split.Height - (int)(170 * Ui.DpiScale(this)));
            IntPtr cue = Marshal.StringToHGlobalUni(L.T("programs.search_cue"));
            Native.SendMessage(search.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), cue);
            Marshal.FreeHGlobal(cue);
            await ReloadAsync();
        }

        async Task ReloadAsync()
        {
            SetBusy(true, false);
            status.Text = L.T("programs.loading");
            programs = await Task.Run(() => ProgramRegistry.Read());
            if (IsDisposed) return;
            if (lastScan != null) lastScan.Apply(programs);
            Fill();
            SetBusy(false, false);
            status.Text = L.T("programs.count", programs.Count) + (lastScan == null ? " " + L.T("programs.scan_hint") : "");
        }

        void Fill()
        {
            var selected = Selected;
            string filter = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = ProgramKinds.Order.ToDictionary(k => k, k => list.Groups.Add(k, ProgramKinds.Title(k)));
            foreach (var p in programs)
            {
                if (filter.Length > 0 && p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0
                    && p.Publisher.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                var item = new ListViewItem(p.ShortName, groups[p.Kind]) { Tag = p, UseItemStyleForSubItems = false, ToolTipText = p.Name };
                item.SubItems[0].Tag = p.Name;
                Ui.AddSub(item, p.Publisher, p.Publisher);
                Ui.AddSub(item, p.Version, p.Runtime != null ? (object)p.Runtime.Version : p.Version);
                Ui.AddSub(item, p.SizeBytes > 0 ? Bytes.Format(p.SizeBytes) : "", p.SizeBytes);
                Ui.AddSub(item, p.InstallDate.HasValue ? p.InstallDate.Value.ToString("d") : "", p.InstallDate ?? DateTime.MinValue);
                var state = Ui.AddSub(item, p.StatusText, p.StatusText);
                if (NeedsAttention(p)) state.ForeColor = Warning;
                if (p.UninstallerMissing) foreach (ListViewItem.ListViewSubItem s in item.SubItems) s.ForeColor = SystemColors.GrayText;
                list.Items.Add(item);
                if (p == selected) item.Selected = true;
            }
            foreach (var g in groups.Values.Where(g => g.Items.Count == 0).ToList()) list.Groups.Remove(g);
            list.EndUpdate();
            Ui.FillColumn(list, 0);
            ShowDetails();
        }

        static bool NeedsAttention(InstalledProgram p)
        {
            return p.SupersededBy != null || p.SupportEnded != null || (p.Runtime != null && p.UsedBy != null && p.UsedBy.Count == 0);
        }

        InstalledProgram Selected
        {
            get { return list.SelectedItems.Count > 0 ? (InstalledProgram)list.SelectedItems[0].Tag : null; }
        }

        void ShowDetails()
        {
            var p = Selected;
            details.Clear();
            if (p == null)
            {
                details.Text = L.T("programs.select_hint");
                openLocation.Enabled = uninstall.Enabled = false;
                return;
            }

            var title = new Font(details.Font.FontFamily, details.Font.Size + 2, FontStyle.Bold);
            var bold = new Font(details.Font, FontStyle.Bold);
            Append(p.Name + "\n", title, null);
            var facts = new[]
            {
                p.Publisher,
                p.Version.Length > 0 ? L.T("programs.version", p.Version) : null,
                p.SizeBytes > 0 ? Bytes.Format(p.SizeBytes) : null,
                p.InstallDate.HasValue ? L.T("programs.installed_on", p.InstallDate.Value.ToString("d")) : null,
            }.Where(s => !string.IsNullOrEmpty(s));
            Append(string.Join(" · ", facts) + "\n\n", null, SystemColors.GrayText);

            if (p.About.Length > 0) Section(L.T("programs.section.about"), p.About, null, bold);
            string advice = p.Advice;
            if (advice != null) Section(L.T("programs.section.advice"), advice, NeedsAttention(p) ? (Color?)Warning : null, bold);
            if (p.UsedBy != null && p.UsedBy.Count > 0)
            {
                string users = string.Join("\n", p.UsedBy.Take(40).Select(u => "•  " + u));
                if (p.UsedBy.Count > 40) users += "\n" + L.T("programs.more", p.UsedBy.Count - 40);
                Section(L.T("programs.section.used_by"), users, null, bold);
            }
            if (p.InstallLocation != null) Section(L.T("programs.section.location"), p.InstallLocation, null, bold);
            Section(L.T("programs.section.uninstaller"), p.UninstallString ?? L.T("programs.none"), SystemColors.GrayText, bold);
            Section(L.T("programs.section.registry"), p.RegistryPath, SystemColors.GrayText, bold);
            details.SelectionStart = 0;
            details.ScrollToCaret();
            openLocation.Enabled = !busy && p.InstallLocation != null;
            uninstall.Enabled = !busy && !string.IsNullOrEmpty(p.UninstallString);
        }

        void Section(string heading, string body, Color? color, Font bold)
        {
            Append(heading + "\n", bold, null);
            Append(body + "\n\n", null, color);
        }

        void Append(string text, Font font, Color? color)
        {
            details.SelectionStart = details.TextLength;
            details.SelectionLength = 0;
            details.SelectionFont = font ?? details.Font;
            details.SelectionColor = color ?? details.ForeColor;
            details.AppendText(text);
        }

        async void OnScan(object sender, EventArgs e)
        {
            SetBusy(true, true);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var scanner = new UsageScanner(programs);
            bool running = true;
            var report = new Progress<string>(dir => { if (running && !IsDisposed) status.Text = L.T("programs.scanning", Shorten(dir)); });
            try
            {
                await Task.Run(() => scanner.Run(report, token));
                running = false;
                scanner.Apply(programs);
                lastScan = scanner;
                Fill();
                status.Text = L.T("programs.scan_done", scanner.FilesScanned, scanner.Roots.Count);
            }
            catch (OperationCanceledException)
            {
                running = false;
                status.Text = L.T("common.search_cancelled");
            }
            finally
            {
                cts.Dispose();
                cts = null;
                SetBusy(false, true);
            }
        }

        static string Shorten(string path)
        {
            return path.Length <= 90 ? path : "…" + path.Substring(path.Length - 89);
        }

        void OnOpenLocation(object sender, EventArgs e)
        {
            var p = Selected;
            if (p != null && p.InstallLocation != null) Shell.Open(p.InstallLocation);
        }

        async void OnUninstall(object sender, EventArgs e)
        {
            var p = Selected;
            if (p == null) return;
            if (p.UninstallerMissing)
            {
                MessageBox.Show(this, L.T("programs.uninstall.missing", p.Name), L.T("programs.uninstall.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new StringBuilder(L.T("programs.uninstall.ask", p.Name));
            bool risky = p.Runtime != null && p.SupersededBy == null && p.UsedBy != null && p.UsedBy.Count > 0;
            if (p.Runtime != null)
            {
                if (p.SupersededBy != null) sb.Append("\n\n").Append(L.T("programs.uninstall.superseded", p.SupersededBy.Name));
                else if (p.UsedBy == null) sb.Append("\n\n").Append(L.T("programs.uninstall.not_scanned"));
                else if (risky)
                    sb.Append("\n\n").Append(L.T("programs.uninstall.used", p.UsedBy.Count))
                      .Append("\n•  ").Append(string.Join("\n•  ", p.UsedBy.Take(8)))
                      .Append(p.UsedBy.Count > 8 ? "\n…" : "")
                      .Append("\n\n").Append(L.T("programs.uninstall.used_footer"));
            }
            if (MessageBox.Show(this, sb.ToString(), L.T("programs.uninstall.title"), MessageBoxButtons.YesNo,
                                risky ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            Process proc;
            try { proc = ProgramRegistry.StartUninstall(p); }
            catch (Win32Exception ex)
            {
                status.Text = L.T("programs.uninstall.start_failed", ex.Message);
                return;
            }
            if (proc == null) { status.Text = L.T("programs.uninstall.no_uninstaller"); return; }

            SetBusy(true, false);
            status.Text = L.T("programs.uninstall.waiting", p.Name);
            await Task.Run(() => { using (proc) proc.WaitForExit(); });
            if (IsDisposed) return;
            await ReloadAsync();
            bool still = programs.Any(x => x.RegistryPath == p.RegistryPath);
            status.Text = L.T(still ? "programs.uninstall.still" : "programs.uninstall.done", p.Name);
        }

        void SetBusy(bool value, bool cancellable)
        {
            busy = value;
            scan.Enabled = refresh.Enabled = !value;
            cancel.Visible = value && cancellable;
            progress.Visible = value;
            progress.MarqueeAnimationSpeed = value ? 30 : 0;
            var p = Selected;
            openLocation.Enabled = !value && p != null && p.InstallLocation != null;
            uninstall.Enabled = !value && p != null && !string.IsNullOrEmpty(p.UninstallString);
        }

        public void CancelWork()
        {
            if (cts != null) cts.Cancel();
        }
    }
}
