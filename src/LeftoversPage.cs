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
        static readonly Color Warning = Color.FromArgb(176, 96, 0);

        readonly bool isAdmin;
        readonly ListView list = new BufferedListView { CheckBoxes = true, MultiSelect = true, ShowItemToolTips = true };
        readonly Label intro = new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8), Text = L.T("leftovers.intro") };
        readonly Label pathLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 2) };
        readonly ProgressBar progress = new ProgressBar { Width = 160, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false, Anchor = AnchorStyles.Left };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("leftovers.status.start") };
        readonly Button find, open, ignore, remove, cancel;
        readonly HashSet<string> ignored = LeftoverFinder.LoadIgnored();
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
            list.SelectedIndexChanged += (s, e) => UpdateSelection();
            list.ItemChecked += (s, e) => UpdateSelection();
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
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(list, 0, 1);
            root.Controls.Add(pathLabel, 0, 2);
            root.Controls.Add(bottom, 0, 3);
            root.Resize += (s, e) =>
            {
                var max = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
                intro.MaximumSize = max;
                pathLabel.MaximumSize = max;
            };
            Controls.Add(root);
            UpdateSelection();
        }

        public bool IsBusy { get { return busy; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.ScaleColumns(list);
            Ui.FillColumn(list, 0);
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
                return items.Count > 0 ? items : list.SelectedItems.Cast<ListViewItem>().ToList();
            }
        }

        void UpdateSelection()
        {
            var sel = list.SelectedItems.Count > 0 ? (Leftover)list.SelectedItems[0].Tag : null;
            pathLabel.Text = sel != null ? sel.Path : "";
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
                var found = await Task.Run(() => LeftoverFinder.Find(ignored, report, token));
                running = false;
                if (IsDisposed) return;
                Fill(found);
                status.Text = found.Count == 0
                    ? L.T("leftovers.none")
                    : L.T("leftovers.found", found.Count, Bytes.Format(found.Sum(f => f.Bytes)));
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

        void Fill(List<Leftover> found)
        {
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var l in found.OrderByDescending(f => f.Bytes))
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
            list.EndUpdate();
            Ui.FillColumn(list, 0);
            UpdateSelection();
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
                list.Items.Remove(item);
            }
            try { LeftoverFinder.SaveIgnored(ignored); }
            catch (IOException ex) { status.Text = L.T("leftovers.ignore_save_failed", ex.Message); return; }
            catch (UnauthorizedAccessException ex) { status.Text = L.T("leftovers.ignore_save_failed", ex.Message); return; }
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
