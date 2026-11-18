using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sweepfella
{
    sealed class LeftoversPage : UserControl
    {
        static Color Warning { get { return Theme.Warning; } }

        readonly bool isAdmin;
        readonly ListView list = new BufferedListView { CheckBoxes = true, MultiSelect = true, ShowItemToolTips = true };
        readonly Label intro = new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8), Text = L.T("leftovers.intro") };
        readonly RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, DetectUrls = false };
        readonly LinkLabel webSearch = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 2), Visible = false };
        readonly SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        CancellationTokenSource detailsCts;
        readonly ProgressBar progress = new ProgressBar { Width = 160, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false, Anchor = AnchorStyles.Left };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("leftovers.status.start") };
        readonly Button find, open, ignore, remove, cancel;
        readonly HashSet<string> ignored = LeftoverFinder.LoadIgnored();
        readonly CheckBox showInUse = new CheckBox { AutoSize = true, Margin = new Padding(3, 0, 3, 6), Text = L.T("leftovers.show_in_use") };
        List<Leftover> found = new List<Leftover>();
        CancellationTokenSource cts;
        bool busy;

        public LeftoversPage(bool isAdmin)
        {
            this.isAdmin = isAdmin;
            Dock = DockStyle.Fill;

            // The Folder column stretches; the rest have fixed widths that fit in 830 px.
            list.Columns.Add(L.T("leftovers.col.folder"), 220);
            list.Columns.Add(L.T("leftovers.col.location"), 150);
            list.Columns.Add(L.T("leftovers.col.size"), 70, HorizontalAlignment.Right);
            list.Columns.Add(L.T("leftovers.col.files"), 65, HorizontalAlignment.Right);
            list.Columns.Add(L.T("leftovers.col.last_change"), 100);
            list.Columns.Add(L.T("leftovers.col.note"), 170);
            ColumnSorter.Attach(list);
            list.SelectedIndexChanged += (s, e) => { UpdateSelection(); ShowDetails(); };
            webSearch.LinkClicked += (s, e) => SearchWeb();
            list.ItemChecked += (s, e) => UpdateSelection();
            // Folders in use can be opened and inspected, never deleted or ignored from here.
            list.ItemCheck += (s, e) => { if (((Leftover)list.Items[e.Index].Tag).InUse) e.NewValue = CheckState.Unchecked; };
            showInUse.CheckedChanged += (s, e) => Fill();
            list.DoubleClick += (s, e) => OnOpen(s, e);
            list.Resize += (s, e) => Ui.FillColumn(list, 0);

            find = Ui.MakeButton(L.T("leftovers.find"), OnFind);
            open = Ui.MakeButton(L.T("leftovers.open"), OnOpen);
            ignore = Ui.MakeButton(L.T("leftovers.ignore"), OnIgnore);
            remove = Ui.MakeButton(L.T("leftovers.remove"), OnRemove);
            cancel = Ui.MakeButton(L.T("common.cancel"), (s, e) => { if (cts != null) cts.Cancel(); });
            cancel.Visible = false;

            var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 7, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 5; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(progress, 0, 0);
            bottom.Controls.Add(status, 1, 0);
            bottom.Controls.Add(cancel, 2, 0);
            bottom.Controls.Add(find, 3, 0);
            bottom.Controls.Add(open, 4, 0);
            bottom.Controls.Add(ignore, 5, 0);
            bottom.Controls.Add(remove, 6, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            details.Text = L.T("leftovers.details.hint");
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);
            split.Panel2.Controls.Add(webSearch);
            split.Panel2.Padding = new Padding(4, 8, 4, 0);
            root.RowStyles.Insert(1, new RowStyle(SizeType.AutoSize));
            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(showInUse, 0, 1);
            root.Controls.Add(split, 0, 2);
            root.Controls.Add(bottom, 0, 3);
            root.Resize += (s, e) => intro.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
            Controls.Add(root);
            UpdateSelection();
        }

        public bool IsBusy { get { return busy; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.ScaleColumns(list);
            Ui.FillColumn(list, 0);
            Ui.DockDetailsPane(split, 190);
        }

        IEnumerable<Leftover> Checked
        {
            get { return list.CheckedItems.Cast<ListViewItem>().Select(i => (Leftover)i.Tag); }
        }

        IEnumerable<ListViewItem> Targets
        {
            // Actions apply to checked items, or to the selection when nothing is checked.
            get
            {
                var items = list.CheckedItems.Cast<ListViewItem>().ToList();
                if (items.Count == 0) items = list.SelectedItems.Cast<ListViewItem>().ToList();
                return items.Where(i => !((Leftover)i.Tag).InUse).ToList();
            }
        }

        void UpdateSelection()
        {
            var sel = Selected;
            var checkedItems = Checked.ToList();
            remove.Text = checkedItems.Count > 0
                ? L.T("leftovers.remove_count", checkedItems.Count, Bytes.Format(checkedItems.Sum(l => l.Bytes)))
                : L.T("leftovers.remove");
            bool any = Targets.Any();
            open.Enabled = !busy && sel != null;
            ignore.Enabled = remove.Enabled = !busy && any;
            find.Enabled = !busy;
        }

        async void OnFind(object sender, EventArgs e)
        {
            SetBusy(true, true);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            bool running = true;
            var report = new Progress<string>(s => { if (running && !IsDisposed) status.Text = s.Length > 90 ? "…" + s.Substring(s.Length - 89) : s; });
            try
            {
                found = await Task.Run(() => LeftoverFinder.Find(ignored, report, token));
                running = false;
                if (IsDisposed) return;
                Fill();
                var leftovers = found.Where(f => !f.InUse).ToList();
                int inUse = found.Count - leftovers.Count;
                status.Text = (leftovers.Count == 0
                    ? L.T("leftovers.none")
                    : L.T("leftovers.found", leftovers.Count, Bytes.Format(leftovers.Sum(f => f.Bytes))))
                    + (inUse > 0 ? " " + L.T("leftovers.in_use_count", inUse) : "");
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
                SetBusy(false, false);
            }
        }

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var l in found.Where(f => !f.InUse).OrderByDescending(f => f.Bytes))
            {
                ListViewGroup g;
                if (!groups.TryGetValue(l.Location, out g))
                {
                    g = new ListViewGroup(l.Location, l.Location);
                    groups.Add(l.Location, g);
                    list.Groups.Add(g);
                }
                var item = new ListViewItem(l.Name, g) { Tag = l, UseItemStyleForSubItems = false, ToolTipText = l.Path };
                item.SubItems[0].Tag = l.Name;
                Ui.AddSub(item, l.Location, l.Location);
                Ui.AddSub(item, Bytes.Format(l.Bytes), l.Bytes);
                Ui.AddSub(item, l.Files.ToString("N0"), (long)l.Files);
                Ui.AddSub(item, Ui.Ago(l.LastChange), l.LastChange);
                string note = l.RequiresAdmin && !isAdmin ? L.T("leftovers.note.admin")
                            : l.RecentlyUsed ? L.T("leftovers.note.recent")
                            : l.Files == 0 ? L.T("leftovers.note.empty") : "";
                var sub = Ui.AddSub(item, note, note);
                if (l.RecentlyUsed) sub.ForeColor = Warning;
                list.Items.Add(item);
            }
            if (showInUse.Checked)
            {
                // A separate, greyed-out group that can't be checked: here only to answer "whose folder is this?".
                var inUse = new ListViewGroup("in_use", L.T("leftovers.group.in_use"));
                list.Groups.Add(inUse);
                foreach (var l in found.Where(f => f.InUse).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = new ListViewItem(l.Name, inUse) { Tag = l, UseItemStyleForSubItems = false, ToolTipText = l.Path };
                    item.SubItems[0].Tag = l.Name;
                    Ui.AddSub(item, l.Location, l.Location);
                    Ui.AddSub(item, "", -1L);
                    Ui.AddSub(item, "", -1L);
                    Ui.AddSub(item, Ui.Ago(l.LastChange), l.LastChange);
                    string note = l.Owner.Describe();
                    Ui.AddSub(item, note, note);
                    foreach (ListViewItem.ListViewSubItem s in item.SubItems) s.ForeColor = Theme.Subtle;
                    list.Items.Add(item);
                }
            }
            list.EndUpdate();
            Ui.FillColumn(list, 0);
            UpdateSelection();
            ShowDetails();
        }

        Leftover Selected
        {
            get { return list.SelectedItems.Count > 0 ? (Leftover)list.SelectedItems[0].Tag : null; }
        }

        /// <summary>Shows the selected folder's details, then fills in the clues found by looking inside it.</summary>
        async void ShowDetails()
        {
            if (detailsCts != null) detailsCts.Cancel();
            var leftover = Selected;
            webSearch.Visible = leftover != null;
            if (leftover == null)
            {
                details.Clear();
                details.Text = L.T("leftovers.details.hint");
                return;
            }
            webSearch.Text = L.T("leftovers.search_web", leftover.Name);
            Render(leftover, null);

            var cts = detailsCts = new CancellationTokenSource();
            Clues clues;
            try { clues = await Task.Run(() => LeftoverFinder.Investigate(leftover.Path, cts.Token)); }
            catch (OperationCanceledException) { return; }
            if (IsDisposed || cts != detailsCts || Selected != leftover) return;
            Render(leftover, clues);
        }

        void Render(Leftover l, Clues c)
        {
            details.Clear();
            RichText.Title(details, l.Name);
            RichText.Append(details, l.Path + "\n", null, Theme.Subtle);
            string summary = l.Bytes >= 0 ? L.T("leftovers.details.summary", Bytes.Format(l.Bytes), l.Files, Ui.Ago(l.LastChange))
                           : c != null ? L.T("leftovers.details.summary", Bytes.Format(c.TotalBytes), c.TotalFiles, Ui.Ago(l.LastChange))
                           : L.T("leftovers.details.summary_unmeasured", Ui.Ago(l.LastChange));
            RichText.Append(details, summary + "\n\n", null, Theme.Subtle);
            if (l.InUse)
                RichText.Section(details, L.T("leftovers.clue.owner"), L.T("leftovers.clue.owner_body", l.Owner.Describe(), l.Owner.Owner), Warning);
            if (c == null)
            {
                RichText.Append(details, L.T("leftovers.details.looking"), null, Theme.Subtle);
                return;
            }

            if (l.SteamOrphan)
                RichText.Section(details, L.T("leftovers.clue.steam_orphan"), L.T("leftovers.clue.steam_orphan_body"), null);
            if (c.Running.Count > 0)
                RichText.Section(details, L.T("leftovers.clue.running"), L.T("leftovers.clue.running_body", string.Join(", ", c.Running)), Warning);
            if (c.OnlyCaches)
                RichText.Section(details, L.T("leftovers.clue.caches"), L.T("leftovers.clue.caches_body"), null);
            if (c.UnityGames.Count > 0)
                RichText.Section(details, L.T("leftovers.clue.unity"), L.T("leftovers.clue.unity_body", string.Join("”, “", c.UnityGames)), Warning);
            if (c.WebView)
                RichText.Section(details, L.T("leftovers.clue.webview"), L.T("leftovers.clue.webview_body"), null);
            if (c.Sources.Count > 0)
                RichText.Section(details, L.T("leftovers.clue.sources"), string.Join("\n", c.Sources.Select(s => "•  " + s)), null);
            else if (!c.OnlyCaches && c.UnityGames.Count == 0 && !c.WebView && c.Contents.Count > 0)
                RichText.Section(details, L.T("leftovers.clue.no_programs"), L.T("leftovers.clue.no_programs_body"), null);

            if (c.Contents.Count == 0)
                RichText.Section(details, L.T("leftovers.clue.contents"), L.T("leftovers.clue.empty"), null);
            else
            {
                string contents = string.Join("\n", c.Contents.Take(8).Select(kv => "•  " + kv.Key + "   " + Bytes.Format(kv.Value)));
                if (c.Contents.Count > 8) contents += "\n" + L.T("programs.more", c.Contents.Count - 8);
                if (c.Truncated) contents += "\n" + L.T("leftovers.clue.contents_partial");
                RichText.Section(details, L.T("leftovers.clue.contents"), contents, null);
            }
            if (c.LatestFile != null)
                RichText.Section(details, L.T("leftovers.clue.latest"), c.LatestFile + "   (" + Ui.Ago(c.LatestChange) + ")", Theme.Subtle);
            details.SelectionStart = 0;
            details.ScrollToCaret();
        }

        void SearchWeb()
        {
            var l = Selected;
            if (l == null) return;
            string query = "\"" + l.Name + "\" folder " + l.Location;
            try { using (System.Diagnostics.Process.Start("https://duckduckgo.com/?q=" + Uri.EscapeDataString(query))) { } }
            catch (System.ComponentModel.Win32Exception) { }
        }

        void OnOpen(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count > 0) Shell.Open(((Leftover)list.SelectedItems[0].Tag).Path);
        }

        void OnIgnore(object sender, EventArgs e)
        {
            var items = Targets.ToList();
            if (items.Count == 0) return;
            foreach (var item in items)
            {
                ignored.Add(((Leftover)item.Tag).Path);
                found.Remove((Leftover)item.Tag);
                list.Items.Remove(item);
            }
            LeftoverFinder.SaveIgnored(ignored);
            status.Text = L.T("leftovers.ignored", items.Count);
            UpdateSelection();
        }

        async void OnRemove(object sender, EventArgs e)
        {
            var items = Targets.ToList();
            if (items.Count == 0) return;
            var leftovers = items.Select(i => (Leftover)i.Tag).ToList();

            var sb = new StringBuilder(L.T("leftovers.confirm.header")).Append("\n\n");
            foreach (var l in leftovers.Take(15)) sb.Append("•  ").Append(l.Path).Append("  (").Append(Bytes.Format(l.Bytes)).Append(")\n");
            if (leftovers.Count > 15) sb.Append(L.T("leftovers.confirm.more", leftovers.Count - 15)).Append('\n');
            if (leftovers.Any(l => l.RecentlyUsed)) sb.Append('\n').Append(L.T("leftovers.confirm.recent")).Append('\n');
            sb.Append('\n').Append(L.T("leftovers.confirm.footer"));
            if (MessageBox.Show(this, sb.ToString(), L.T("leftovers.confirm.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetBusy(true, false);
            int ok = 0;
            long freed = 0;
            var errors = new List<string>();
            foreach (var item in items)
            {
                var l = (Leftover)item.Tag;
                status.Text = L.T("leftovers.moving", l.Name);
                string error = await Task.Run(() =>
                {
                    try { LeftoverFinder.MoveToRecycleBin(l.Path); return null; }
                    catch (OperationCanceledException) { return L.T("leftovers.err.cancelled"); }
                    catch (IOException ex) { return ex.Message; }
                    catch (UnauthorizedAccessException) { return L.T("leftovers.err.denied") + (isAdmin ? "" : " " + L.T("leftovers.err.try_admin")); }
                    catch (InvalidOperationException ex) { return ex.Message; }
                });
                if (IsDisposed) return;
                if (error == null && !Directory.Exists(l.Path))
                {
                    ok++;
                    freed += l.Bytes;
                    found.Remove(l);
                    list.Items.Remove(item);
                }
                else errors.Add(l.Name + ": " + (error ?? L.T("leftovers.err.not_moved")));
            }
            SetBusy(false, false);
            status.Text = L.T("leftovers.moved", ok, Bytes.Format(freed)) + (errors.Count > 0 ? " " + L.T("leftovers.moved_errors", errors.Count) : "");
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors.Take(20)), L.T("leftovers.errors.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void SetBusy(bool value, bool cancellable)
        {
            busy = value;
            cancel.Visible = value && cancellable;
            progress.Visible = value;
            progress.MarqueeAnimationSpeed = value ? 30 : 0;
            UpdateSelection();
        }

        public void CancelWork()
        {
            if (cts != null) cts.Cancel();
        }
    }
}
