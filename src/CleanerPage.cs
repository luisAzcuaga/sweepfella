using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sweepfella
{
    sealed class CleanerPage : UserControl
    {
        const int ColFiles = 1, ColSize = 2, ColState = 3;

        /// <summary>A category row. Expanding it lists what it would delete as child rows the user can uncheck.</summary>
        sealed class Node
        {
            public Category Category;
            public ListViewItem Item;
            public int Order;
            public bool Expanded;
            public int Version;   // bumped on every collapse/reload, so a listing that finishes late is dropped
            public readonly List<ListViewItem> Children = new List<ListViewItem>();
            public readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Listed = new List<string>();   // last listing, kept after collapsing so the choice sticks

            /// <summary>The listed entries left checked, or null when everything is (the plain, whole-category clean).</summary>
            public HashSet<string> Selection()
            {
                if (!Listed.Any(Excluded.Contains)) return null;
                return new HashSet<string>(Listed.Where(p => !Excluded.Contains(p)), StringComparer.OrdinalIgnoreCase);
            }
        }

        sealed class Child
        {
            public Node Node;
            public Entry Entry;
            public int Order;
        }

        /// <summary>
        /// Keeps each category's entries right under it. Grouped list views show items added later at the end of
        /// their group whatever their index, so the order has to come from sorting.
        /// </summary>
        sealed class RowOrder : System.Collections.IComparer
        {
            public int Compare(object x, object y) { return Key((ListViewItem)x).CompareTo(Key((ListViewItem)y)); }

            static long Key(ListViewItem item)
            {
                var node = item.Tag as Node;
                if (node != null) return node.Order * 1000000L;
                var child = (Child)item.Tag;
                return child.Node.Order * 1000000L + 1 + child.Order;
            }
        }

        static Entry EntryOf(ListViewItem item) { return ((Child)item.Tag).Entry; }

        readonly bool isAdmin;
        readonly List<Node> nodes;
        readonly Dictionary<Category, Result> lastScan = new Dictionary<Category, Result>();
        readonly TreeListView list = new TreeListView { MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label description = new Label { AutoSize = true, Padding = new Padding(0, 8, 0, 4), ForeColor = Theme.Subtle, Text = L.T("cleaner.hint") };
        readonly CheckBox keepRecent = new CheckBox { AutoSize = true, Checked = true, Margin = new Padding(3, 6, 12, 3), Text = L.T("cleaner.keep_recent") };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 4) };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("cleaner.status.start") };
        readonly Button analyze, clean, cancel;
        CancellationTokenSource cts;
        bool busy, populating, syncing;

        public CleanerPage(bool isAdmin)
        {
            this.isAdmin = isAdmin;
            Dock = DockStyle.Fill;
            nodes = Catalog.Build().Select((c, i) => new Node { Category = c, Order = i }).ToList();

            list.Columns.Add(L.T("cleaner.col.category"), 280);
            list.Columns.Add(L.T("cleaner.col.files"), 90, HorizontalAlignment.Right);
            list.Columns.Add(L.T("cleaner.col.size"), 100, HorizontalAlignment.Right);
            list.Columns.Add(L.T("cleaner.col.state"), 260);
            list.ToggleRequested = ToggleCheck;
            list.ExpandRequested = item => { var n = item.Tag as Node; if (n != null) Toggle(n); };
            list.ParentOf = item => { var c = item.Tag as Child; return c == null ? null : c.Node.Item; };
            list.StatesReady = () => { foreach (var n in nodes) UpdateMark(n); };
            list.ListViewItemSorter = new RowOrder();
            list.ItemCheck += OnItemCheck;
            list.ItemChecked += OnItemChecked;
            list.SelectedIndexChanged += OnSelectionChanged;
            list.Resize += (s, e) => Ui.FillColumn(list);
            keepRecent.CheckedChanged += (s, e) =>
            {
                lastScan.Clear();
                foreach (var n in nodes.Where(n => n.Expanded)) Reload(n);
            };

            analyze = Ui.MakeButton(L.T("cleaner.analyze"), OnAnalyze);
            clean = Ui.MakeButton(L.T("cleaner.clean"), OnClean);
            cancel = Ui.MakeButton(L.T("common.cancel"), OnCancel);
            cancel.Enabled = false;

            var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(status, 0, 0);
            bottom.Controls.Add(analyze, 1, 0);
            bottom.Controls.Add(clean, 2, 0);
            bottom.Controls.Add(cancel, 3, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(list, 0, 0);
            root.Controls.Add(description, 0, 1);
            root.Controls.Add(Ui.Row(keepRecent), 0, 2);
            root.Controls.Add(progress, 0, 3);
            root.Controls.Add(bottom, 0, 4);
            root.Resize += (s, e) => description.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
            Controls.Add(root);

            Populate();
        }

        public bool IsBusy { get { return busy; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.ScaleColumns(list);
            Ui.FillColumn(list);
        }

        void Populate()
        {
            populating = true;
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var n in nodes)
            {
                var c = n.Category;
                ListViewGroup g;
                if (!groups.TryGetValue(c.Group, out g))
                {
                    g = new ListViewGroup(c.Group, c.Group);
                    groups.Add(c.Group, g);
                    list.Groups.Add(g);
                }
                bool locked = Locked(c);
                n.Item = new ListViewItem(new[] { c.Name, "", "", locked ? L.T("common.requires_admin") : "" }, g)
                {
                    Tag = n,
                    Checked = c.DefaultChecked && !locked,
                    ImageIndex = TreeListView.Collapsed,
                };
                if (locked) n.Item.ForeColor = Theme.Subtle;
                list.Items.Add(n.Item);
            }
            populating = false;
        }

        bool Locked(Category c) { return c.RequiresAdmin && !isAdmin; }

        // ---- Expanding a category into what it would delete ----

        void Toggle(Node n)
        {
            if (busy) return;
            if (n.Expanded) Collapse(n);
            else
            {
                n.Expanded = true;
                n.Item.ImageIndex = TreeListView.Expanded;
                Reload(n);
            }
        }

        void Collapse(Node n)
        {
            n.Version++;
            n.Expanded = false;
            n.Item.ImageIndex = TreeListView.Collapsed;
            RemoveChildren(n);
            if (!busy && !Locked(n.Category)) n.Item.SubItems[ColState].Text = SelectionText(n);
        }

        void RemoveChildren(Node n)
        {
            list.BeginUpdate();
            foreach (var child in n.Children) list.Items.Remove(child);
            list.EndUpdate();
            n.Children.Clear();
        }

        DateTime Cutoff() { return keepRecent.Checked ? DateTime.UtcNow.AddHours(-24) : DateTime.MinValue; }

        /// <summary>(Re)lists a category's entries, keeping what the user unchecked; rows are shown only while it's expanded.</summary>
        async void Reload(Node n)
        {
            await ReloadAsync(n);
        }

        async Task ReloadAsync(Node n)
        {
            int version = ++n.Version;
            var cat = n.Category;
            DateTime cutoff = Cutoff();
            n.Item.SubItems[ColState].Text = L.T("cleaner.state.listing");
            List<Entry> entries;
            try { entries = await Task.Run(() => Cleaner.List(cat, cutoff, CancellationToken.None)); }
            catch (Exception ex)
            {
                if (version == n.Version) n.Item.SubItems[ColState].Text = L.T("common.error", ex.Message);
                return;
            }
            if (version != n.Version) return;

            n.Listed = entries.Select(e => e.Path).ToList();
            bool locked = Locked(cat);
            syncing = true;
            list.BeginUpdate();
            try
            {
                RemoveChildren(n);
                foreach (var e in n.Expanded ? entries : new List<Entry>())
                {
                    var item = new ListViewItem(new[] { e.Name, e.Files.ToString("N0"), Bytes.Format(e.Bytes), Where(e) }, n.Item.Group)
                    {
                        Tag = new Child { Node = n, Entry = e, Order = n.Children.Count },
                        IndentCount = 1,
                        ImageIndex = TreeListView.NoArrow,
                        UseItemStyleForSubItems = false,
                    };
                    item.SubItems[ColState].ForeColor = Theme.Subtle;
                    if (locked) foreach (ListViewItem.ListViewSubItem sub in item.SubItems) sub.ForeColor = Theme.Subtle;
                    list.Items.Add(item);
                    item.Checked = n.Item.Checked && !n.Excluded.Contains(e.Path);
                    n.Children.Add(item);
                }
                list.Sort();
                // After a clean only the unchecked entries may be left; with none checked, the category is off.
                if (n.Item.Checked && n.Listed.Count > 0 && n.Listed.All(n.Excluded.Contains))
                {
                    n.Excluded.Clear();
                    n.Item.Checked = false;
                }
            }
            finally
            {
                list.EndUpdate();
                syncing = false;
            }
            UpdateMark(n);
            if (!busy) n.Item.SubItems[ColState].Text = entries.Count == 0 ? L.T("cleaner.state.nothing") : SelectionText(n);
        }

        static string Where(Entry e)
        {
            return e.Deleted.HasValue ? L.T("cleaner.entry.deleted_from", e.Location, Ui.Ago(e.Deleted.Value)) : e.Location;
        }

        string SelectionText(Node n)
        {
            if (Locked(n.Category)) return L.T("common.requires_admin");
            var only = n.Item.Checked ? n.Selection() : null;
            return only == null ? "" : L.T("cleaner.state.selected", only.Count, n.Listed.Count);
        }

        // ---- Checkboxes: a category and its entries stay in step ----

        void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (populating || syncing) return;
            var item = list.Items[e.Index];
            var node = item.Tag as Node ?? ((Child)item.Tag).Node;
            if (busy || Locked(node.Category)) e.NewValue = e.CurrentValue;
        }

        void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (populating || syncing) return;
            syncing = true;
            try
            {
                var node = e.Item.Tag as Node;
                if (node != null)
                {
                    // The whole category on or off: every entry follows.
                    node.Excluded.Clear();
                    foreach (var c in node.Children) c.Checked = node.Item.Checked;
                }
                else
                {
                    var child = (Child)e.Item.Tag;
                    node = child.Node;
                    if (e.Item.Checked)
                    {
                        if (!node.Item.Checked)
                        {
                            // Checking one entry of an unchecked category picks just that entry.
                            node.Excluded.Clear();
                            foreach (var c in node.Children.Where(c => c != e.Item)) node.Excluded.Add(EntryOf(c).Path);
                            node.Item.Checked = true;
                        }
                        else node.Excluded.Remove(child.Entry.Path);
                    }
                    else
                    {
                        node.Excluded.Add(child.Entry.Path);
                        if (node.Listed.All(node.Excluded.Contains))
                        {
                            node.Excluded.Clear();
                            node.Item.Checked = false;
                        }
                    }
                }
                lastScan.Remove(node.Category);
                UpdateMark(node);
                if (!busy) node.Item.SubItems[ColState].Text = SelectionText(node);
            }
            finally { syncing = false; }
        }

        /// <summary>A click or Space on a checkbox. A mixed category becomes fully selected, like Windows' own tree checkboxes.</summary>
        void ToggleCheck(ListViewItem item)
        {
            var node = item.Tag as Node;
            if (node == null || !list.IsMixed(item))
            {
                item.Checked = !item.Checked;   // goes through OnItemCheck (which may refuse) and OnItemChecked
                return;
            }
            if (busy || Locked(node.Category)) return;
            syncing = true;
            try
            {
                node.Excluded.Clear();
                foreach (var c in node.Children) c.Checked = true;
                lastScan.Remove(node.Category);
                UpdateMark(node);
                node.Item.SubItems[ColState].Text = SelectionText(node);
            }
            finally { syncing = false; }
        }

        /// <summary>Shows a category's checkbox as mixed while only some of its entries are selected.</summary>
        void UpdateMark(Node n)
        {
            int want = !n.Item.Checked ? TreeListView.Unchecked : n.Selection() != null ? TreeListView.Mixed : TreeListView.Checked;
            bool was = syncing;
            syncing = true;   // checked <-> mixed are both "checked" to WinForms, so no check events, but be safe
            try { list.SetMark(n.Item, want); }
            finally { syncing = was; }
        }

        void OnSelectionChanged(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0) return;
            var item = list.SelectedItems[0];
            description.ForeColor = Theme.Text;
            var node = item.Tag as Node;
            if (node != null)
            {
                var c = node.Category;
                description.Text = c.Description + (Locked(c) ? " " + L.T("cleaner.needs_admin") : "");
                return;
            }
            var entry = EntryOf(item);
            description.Text = entry.Deleted.HasValue
                ? L.T("cleaner.entry.recycled", entry.Name, entry.Location, Ui.Ago(entry.Deleted.Value))
                : Path.Combine(entry.Location ?? "", entry.Name);
        }

        // ---- Analyze and clean ----

        List<Node> CheckedNodes()
        {
            return nodes.Where(n => n.Item.Checked).ToList();
        }

        async void OnAnalyze(object sender, EventArgs e)
        {
            var picked = CheckedNodes();
            if (picked.Count == 0) { status.Text = L.T("cleaner.check_one"); return; }
            await RunAsync(picked, false);
        }

        async void OnClean(object sender, EventArgs e)
        {
            var picked = CheckedNodes();
            if (picked.Count == 0) { status.Text = L.T("cleaner.check_one"); return; }
            if (!Confirm(picked)) return;
            await RunAsync(picked, true);
        }

        void OnCancel(object sender, EventArgs e)
        {
            if (cts == null) return;
            cts.Cancel();
            cancel.Enabled = false;
            status.Text = L.T("cleaner.cancelling");
        }

        bool Confirm(List<Node> picked)
        {
            var cats = picked.Select(n => n.Category).ToList();
            var sb = new StringBuilder(L.T("cleaner.confirm.header")).Append("\n\n");
            foreach (var n in picked)
            {
                sb.Append("•  ").Append(n.Category.Name);
                var only = n.Selection();
                if (only != null) sb.Append("  ").Append(L.T("cleaner.confirm.partial", only.Count, n.Listed.Count));
                sb.Append('\n');
            }
            if (cats.All(lastScan.ContainsKey))
                sb.Append('\n').Append(L.T("cleaner.confirm.estimate", Bytes.Format(cats.Sum(c => lastScan[c].Bytes)))).Append('\n');

            var open = cats.Where(c => c.AppName != null && c.Processes.Any(IsRunning)).Select(c => c.AppName).Distinct().ToList();
            if (open.Count > 0)
                sb.Append('\n').Append(L.T("cleaner.confirm.open", string.Join(", ", open))).Append('\n');
            sb.Append('\n').Append(L.T("cleaner.confirm.footer"));
            return MessageBox.Show(this, sb.ToString(), L.T("cleaner.confirm.title"), MessageBoxButtons.YesNo,
                                   MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        static bool IsRunning(string processName)
        {
            var procs = Process.GetProcessesByName(processName);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }

        async Task RunAsync(List<Node> picked, bool doClean)
        {
            SetBusy(true);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            DateTime cutoff = Cutoff();
            string queued = L.T("cleaner.state.queued");
            progress.Maximum = picked.Count;
            progress.Value = 0;
            foreach (var n in picked)
            {
                n.Item.SubItems[ColFiles].Text = "";
                n.Item.SubItems[ColSize].Text = "";
                n.Item.SubItems[ColState].Text = queued;
            }

            long total = 0;
            int failed = 0;
            bool cancelled = false;
            foreach (var n in picked)
            {
                var item = n.Item;
                var cat = n.Category;
                var only = n.Selection();   // read on the UI thread, before the work starts
                item.SubItems[ColState].Text = L.T(doClean ? "cleaner.state.cleaning" : "cleaner.state.analyzing");
                status.Text = L.T(doClean ? "cleaner.status.cleaning" : "cleaner.status.analyzing", cat.Name);
                item.EnsureVisible();

                Result r;
                try
                {
                    r = await Task.Run(() => Cleaner.Run(cat, doClean, cutoff, only, token));
                }
                catch (OperationCanceledException)
                {
                    item.SubItems[ColState].Text = L.T("common.state.cancelled");
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    item.SubItems[ColState].Text = L.T("common.error", ex.Message);
                    progress.Value++;
                    continue;
                }

                // Re-analyze after cleaning so the columns show what is left, not what was deleted.
                Result remaining = r;
                if (doClean)
                {
                    try { remaining = await Task.Run(() => Cleaner.Run(cat, false, cutoff, only, CancellationToken.None)); }
                    catch (Exception) { remaining = new Result { Files = r.Failed }; }
                }
                if (doClean && (n.Expanded || only != null)) await ReloadAsync(n);
                item.SubItems[ColFiles].Text = remaining.Files.ToString("N0");
                item.SubItems[ColSize].Text = Bytes.Format(remaining.Bytes);
                if (doClean)
                {
                    item.SubItems[ColState].Text = L.T("cleaner.state.freed", Bytes.Format(r.Bytes))
                        + (remaining.Files > 0 ? " · " + L.T("cleaner.state.in_use", remaining.Files) : "");
                }
                else
                {
                    string state = L.T(r.Files == 0 ? "cleaner.state.nothing" : "cleaner.state.can_clean");
                    string selection = SelectionText(n);
                    item.SubItems[ColState].Text = selection.Length > 0 ? state + " · " + selection : state;
                }
                lastScan[cat] = remaining;
                if (doClean) Stats.Add(r.Bytes);
                total += r.Bytes;
                failed += r.Failed;
                progress.Value++;
            }

            foreach (var n in picked.Where(n => n.Item.SubItems[ColState].Text == queued))
                n.Item.SubItems[ColState].Text = "";

            if (cancelled) status.Text = L.T("common.cancelled");
            else if (doClean)
                status.Text = L.T("cleaner.status.done", Bytes.Format(total))
                            + (failed > 0 ? " " + L.T("cleaner.status.done_failed", failed) : "")
                            + (Stats.Freed > total ? " " + L.T("cleaner.status.total", Bytes.Format(Stats.Freed)) : "");
            else status.Text = total == 0 ? L.T("cleaner.status.nothing") : L.T("cleaner.status.can_free", Bytes.Format(total));

            cts.Dispose();
            cts = null;
            SetBusy(false);
        }

        void SetBusy(bool value)
        {
            busy = value;
            analyze.Enabled = clean.Enabled = keepRecent.Enabled = !value;
            cancel.Enabled = value;
        }

        public void CancelWork()
        {
            if (cts != null) cts.Cancel();
        }
    }
}
