using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

        readonly bool isAdmin;
        readonly List<Category> categories = Catalog.Build();
        readonly Dictionary<Category, Result> lastScan = new Dictionary<Category, Result>();
        readonly ListView list = new BufferedListView { CheckBoxes = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label description = new Label { AutoSize = true, Padding = new Padding(0, 8, 0, 4), ForeColor = SystemColors.GrayText, Text = L.T("cleaner.hint") };
        readonly CheckBox keepRecent = new CheckBox { AutoSize = true, Checked = true, Margin = new Padding(3, 6, 12, 3), Text = L.T("cleaner.keep_recent") };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 4) };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("cleaner.status.start") };
        readonly Button analyze, clean, cancel;
        CancellationTokenSource cts;
        bool busy, populating;

        public CleanerPage(bool isAdmin)
        {
            this.isAdmin = isAdmin;
            Dock = DockStyle.Fill;

            list.Columns.Add(L.T("cleaner.col.category"), 280);
            list.Columns.Add(L.T("cleaner.col.files"), 90, HorizontalAlignment.Right);
            list.Columns.Add(L.T("cleaner.col.size"), 100, HorizontalAlignment.Right);
            list.Columns.Add(L.T("cleaner.col.state"), 260);
            list.ItemCheck += OnItemCheck;
            list.SelectedIndexChanged += OnSelectionChanged;
            list.Resize += (s, e) => Ui.FillColumn(list);
            keepRecent.CheckedChanged += (s, e) => lastScan.Clear();

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
            foreach (var c in categories)
            {
                ListViewGroup g;
                if (!groups.TryGetValue(c.Group, out g))
                {
                    g = new ListViewGroup(c.Group, c.Group);
                    groups.Add(c.Group, g);
                    list.Groups.Add(g);
                }
                bool locked = c.RequiresAdmin && !isAdmin;
                var item = new ListViewItem(new[] { c.Name, "", "", locked ? L.T("common.requires_admin") : "" }, g)
                {
                    Tag = c,
                    Checked = c.DefaultChecked && !locked,
                };
                if (locked) item.ForeColor = SystemColors.GrayText;
                list.Items.Add(item);
            }
            populating = false;
        }

        void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (populating) return;
            var c = (Category)list.Items[e.Index].Tag;
            if (busy || (c.RequiresAdmin && !isAdmin)) e.NewValue = e.CurrentValue;
        }

        void OnSelectionChanged(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0) return;
            var c = (Category)list.SelectedItems[0].Tag;
            description.ForeColor = ForeColor;
            description.Text = c.Description + (c.RequiresAdmin && !isAdmin ? " " + L.T("cleaner.needs_admin") : "");
        }

        List<ListViewItem> CheckedItems()
        {
            return list.CheckedItems.Cast<ListViewItem>().ToList();
        }

        async void OnAnalyze(object sender, EventArgs e)
        {
            var items = CheckedItems();
            if (items.Count == 0) { status.Text = L.T("cleaner.check_one"); return; }
            await RunAsync(items, false);
        }

        async void OnClean(object sender, EventArgs e)
        {
            var items = CheckedItems();
            if (items.Count == 0) { status.Text = L.T("cleaner.check_one"); return; }
            if (!Confirm(items.Select(i => (Category)i.Tag).ToList())) return;
            await RunAsync(items, true);
        }

        void OnCancel(object sender, EventArgs e)
        {
            if (cts == null) return;
            cts.Cancel();
            cancel.Enabled = false;
            status.Text = L.T("cleaner.cancelling");
        }

        bool Confirm(List<Category> cats)
        {
            var sb = new StringBuilder(L.T("cleaner.confirm.header")).Append("\n\n");
            foreach (var c in cats) sb.Append("•  ").Append(c.Name).Append('\n');
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

        async Task RunAsync(List<ListViewItem> items, bool doClean)
        {
            SetBusy(true);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            DateTime cutoff = keepRecent.Checked ? DateTime.UtcNow.AddHours(-24) : DateTime.MinValue;
            string queued = L.T("cleaner.state.queued");
            progress.Maximum = items.Count;
            progress.Value = 0;
            foreach (var item in items)
            {
                item.SubItems[ColFiles].Text = "";
                item.SubItems[ColSize].Text = "";
                item.SubItems[ColState].Text = queued;
            }

            long total = 0;
            int failed = 0;
            bool cancelled = false;
            foreach (var item in items)
            {
                var cat = (Category)item.Tag;
                item.SubItems[ColState].Text = L.T(doClean ? "cleaner.state.cleaning" : "cleaner.state.analyzing");
                status.Text = L.T(doClean ? "cleaner.status.cleaning" : "cleaner.status.analyzing", cat.Name);
                item.EnsureVisible();

                Result r;
                try
                {
                    r = await Task.Run(() => Cleaner.Run(cat, doClean, cutoff, token));
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
                    try { remaining = await Task.Run(() => Cleaner.Run(cat, false, cutoff, CancellationToken.None)); }
                    catch (Exception) { remaining = new Result { Files = r.Failed }; }
                }
                item.SubItems[ColFiles].Text = remaining.Files.ToString("N0");
                item.SubItems[ColSize].Text = Bytes.Format(remaining.Bytes);
                if (doClean)
                {
                    item.SubItems[ColState].Text = L.T("cleaner.state.freed", Bytes.Format(r.Bytes))
                        + (remaining.Files > 0 ? " · " + L.T("cleaner.state.in_use", remaining.Files) : "");
                }
                else
                {
                    item.SubItems[ColState].Text = L.T(r.Files == 0 ? "cleaner.state.nothing" : "cleaner.state.can_clean");
                }
                lastScan[cat] = remaining;
                if (doClean) Stats.Add(r.Bytes);
                total += r.Bytes;
                failed += r.Failed;
                progress.Value++;
            }

            foreach (var item in items.Where(i => i.SubItems[ColState].Text == queued))
                item.SubItems[ColState].Text = "";

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
