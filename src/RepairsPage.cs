using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sweepfella
{
    /// <summary>
    /// Registry settings malware is known to change, each compared with Windows' default. Read-only for now: it
    /// explains what differs and what to do, and changes nothing.
    /// </summary>
    sealed class RepairsPage : UserControl
    {
        readonly BufferedListView list = new BufferedListView { MultiSelect = false, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label intro = new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8), ForeColor = Theme.Warning, Text = L.T("repairs.intro") };
        readonly RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, DetectUrls = false };
        readonly SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("repairs.checking") };
        readonly Button check;
        bool busy;

        public RepairsPage()
        {
            Dock = DockStyle.Fill;
            list.Columns.Add(L.T("repairs.col.check"), 300);
            list.Columns.Add(L.T("repairs.col.status"), 90);
            list.Columns.Add(L.T("repairs.col.found"), 360);
            list.SelectedIndexChanged += (s, e) => ShowDetails();
            list.Resize += (s, e) => Ui.FillColumn(list);

            check = Ui.MakeButton(L.T("repairs.check_again"), async (s, e) => await ScanAsync());

            var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(status, 0, 0);
            bottom.Controls.Add(check, 1, 0);

            details.Text = L.T("repairs.details.hint");
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);
            split.Panel2.Padding = new Padding(4, 8, 4, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            root.Resize += (s, e) => intro.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
            Controls.Add(root);
        }

        public bool IsBusy { get { return busy; } }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.ScaleColumns(list);
            Ui.FillColumn(list);
            Ui.DockDetailsPane(split, 210);
            await ScanAsync();   // quick: a few dozen registry reads
        }

        async Task ScanAsync()
        {
            busy = true;
            check.Enabled = false;
            status.Text = L.T("repairs.checking");
            List<RepairFinding> findings;
            try { findings = await Task.Run(() => RepairScanner.Scan()); }
            finally
            {
                busy = false;
                check.Enabled = true;
            }
            if (IsDisposed) return;
            Fill(findings);
            int changed = findings.Count(f => f.Status == RepairStatus.Changed), unusual = findings.Count(f => f.Status == RepairStatus.Unusual);
            status.Text = changed + unusual == 0 ? L.T("repairs.summary_ok", findings.Count) : L.T("repairs.summary", changed, unusual, findings.Count);
        }

        void Fill(List<RepairFinding> findings)
        {
            var selected = list.SelectedItems.Count > 0 ? Key((RepairFinding)list.SelectedItems[0].Tag) : null;
            list.SaveGroupStates();
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = RepairScanner.Areas.ToDictionary(a => a, a => list.Groups.Add(a, L.T("repairs.area." + a)));
            // Within each area, what needs attention first; otherwise the order the checks run in.
            foreach (var f in findings.Select((f, i) => new { f, i }).OrderBy(x => x.f.Status == RepairStatus.Ok ? 1 : 0).ThenBy(x => x.i).Select(x => x.f))
            {
                string name = L.T("repair." + f.Id + ".name") + (f.Subject != null ? ": " + f.Subject : "");
                var item = new ListViewItem(name, groups[f.Area]) { Tag = f, UseItemStyleForSubItems = false, ToolTipText = string.Join("\n", f.Locations) };
                var state = item.SubItems.Add(L.T("repairs.status." + f.Status.ToString().ToLowerInvariant()));
                item.SubItems.Add(f.Found);
                if (f.Status == RepairStatus.Changed) { state.ForeColor = Theme.Warning; state.Font = new Font(list.Font, FontStyle.Bold); }
                else if (f.Status == RepairStatus.Unusual) state.ForeColor = Theme.Warning;
                else state.ForeColor = Theme.Subtle;
                item.SubItems[2].ForeColor = f.Status == RepairStatus.Ok ? Theme.Subtle : Theme.Text;
                list.Items.Add(item);
                if (Key(f) == selected) item.Selected = true;
            }
            foreach (var g in groups.Values.Where(g => g.Items.Count == 0).ToList()) list.Groups.Remove(g);
            list.EndUpdate();
            list.MakeGroupsCollapsible();
            ShowDetails();
        }

        static string Key(RepairFinding f) { return f.Id + "|" + f.Subject; }

        void ShowDetails()
        {
            details.Clear();
            if (list.SelectedItems.Count == 0)
            {
                details.Text = L.T("repairs.details.hint");
                return;
            }
            var f = (RepairFinding)list.SelectedItems[0].Tag;
            RichText.Title(details, L.T("repair." + f.Id + ".name") + (f.Subject != null ? ": " + f.Subject : ""));
            Color? statusColor = f.Status == RepairStatus.Ok ? (Color?)Theme.Subtle : Theme.Warning;
            RichText.Append(details, L.T("repairs.status." + f.Status.ToString().ToLowerInvariant()) + "\n\n", null, statusColor);
            RichText.Section(details, L.T("repairs.details.what"), L.T("repair." + f.Id + ".about"), null);
            string legit = L.Has("repair." + f.Id + ".legit") ? L.T("repair." + f.Id + ".legit") : null;
            if (f.Note != null) legit = f.Note + (legit != null ? " " + legit : "");
            if (legit != null) RichText.Section(details, L.T("repairs.details.legit"), legit, f.Status == RepairStatus.Unusual ? (Color?)Theme.Warning : null);
            RichText.Section(details, L.T("repairs.details.found"), f.Found, f.Status == RepairStatus.Ok ? (Color?)null : Theme.Warning);
            RichText.Section(details, L.T("repairs.details.expected"), f.Expected, null);
            RichText.Section(details, L.T("repairs.details.next"), L.T("repairs.next." + f.Status.ToString().ToLowerInvariant()), null);
            RichText.Section(details, L.T("repairs.details.where"), string.Join("\n", f.Locations), Theme.Subtle);
            details.SelectionStart = 0;
            details.ScrollToCaret();
        }
    }
}
