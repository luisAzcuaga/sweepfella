using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Sweepfella
{
    /// <summary>
    /// Keeps the app alive while swapping windows: changing the language (or theme) opens a new window in the
    /// same place and closes the old one, so everything is built and DPI-scaled from scratch with the new texts.
    /// </summary>
    sealed class WindowHost : ApplicationContext
    {
        public WindowHost()
        {
            Settings.SaveFailed += () => MessageBox.Show(
                L.T("settings.readonly", AppDomain.CurrentDomain.BaseDirectory), L.T("settings.readonly.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Show(new MainForm(this, 0));
        }

        public void Replace(MainForm old, int tabIndex)
        {
            var next = new MainForm(this, tabIndex) { StartPosition = FormStartPosition.Manual };
            if (old.WindowState == FormWindowState.Normal) next.Bounds = old.Bounds;
            else { next.Bounds = old.RestoreBounds; next.WindowState = old.WindowState; }
            Show(next);
            old.Close();
        }

        void Show(Form form)
        {
            MainForm = form;   // the app exits when the main window closes, not the previous one
            form.Show();
        }
    }

    sealed class MainForm : Form
    {
        readonly WindowHost host;
        readonly bool isAdmin = Elevation.IsAdmin();
        readonly CleanerPage cleaner;
        readonly ProgramsPage programs;
        readonly LeftoversPage leftovers;
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        readonly ComboBox language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        readonly LinkLabel about = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

        public MainForm(WindowHost host, int tabIndex)
        {
            this.host = host;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = L.T(isAdmin ? "app.title_admin" : "app.title");
            using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("sweepfella.ico"))
                if (icon != null) Icon = new Icon(icon);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = LoadSize() ?? DefaultClientSize;
            MinimumSize = new Size(720, 480);

            cleaner = new CleanerPage(isAdmin);
            programs = new ProgramsPage();
            leftovers = new LeftoversPage(isAdmin);
            tabs.TabPages.Add(Page(L.T("main.tab.cleanup"), cleaner));
            tabs.TabPages.Add(Page(L.T("main.tab.programs"), programs));
            tabs.TabPages.Add(Page(L.T("main.tab.leftovers"), leftovers));
            tabs.SelectedIndex = Math.Max(0, Math.Min(tabIndex, tabs.TabCount - 1));
            Controls.Add(tabs);
            if (!isAdmin) Controls.Add(AdminBanner());   // added last so it docks above the tabs

            // About link and language picker floating to the right of the tabs.
            about.Text = L.T("main.about");
            about.LinkClicked += (s, e) =>
            {
                using (var dialog = new AboutForm())
                {
                    dialog.ShowDialog(this);
                    if (dialog.DataDeleted) Close();
                }
            };
            Controls.Add(about);
            about.BringToFront();
            language.DisplayMember = "Value";
            language.ValueMember = "Key";
            language.DataSource = L.Available();
            Controls.Add(language);
            language.BringToFront();
            tabs.SizeChanged += (s, e) => PlaceLanguage();
            language.SelectionChangeCommitted += OnLanguageChanged;

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            language.SelectedValue = L.Code;
            PlaceLanguage();
        }

        void PlaceLanguage()
        {
            int strip = tabs.TabCount > 0 ? tabs.GetTabRect(0).Height : language.Height;
            language.Location = new Point(tabs.Right - language.Width - 4, tabs.Top + Math.Max(0, (strip - language.Height) / 2));
            about.Location = new Point(language.Left - about.Width - 12, tabs.Top + Math.Max(0, (strip - about.Height) / 2));
        }

        void OnLanguageChanged(object sender, EventArgs e)
        {
            string code = language.SelectedValue as string;
            if (code == null || code == L.Code) return;
            if (cleaner.IsBusy || programs.IsBusy || leftovers.IsBusy)
            {
                MessageBox.Show(this, L.T("main.busy"), L.T("main.busy.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                language.SelectedValue = L.Code;
                return;
            }
            L.Use(code);
            Settings.Set("language", code);
            host.Replace(this, tabs.SelectedIndex);
        }

        static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { UseVisualStyleBackColor = true };
            page.Controls.Add(content);
            return page;
        }

        Control AdminBanner()
        {
            var restart = Ui.MakeButton(L.T("main.restart_admin"), (s, e) =>
            {
                if (Elevation.RestartAsAdmin()) Close();
            });
            restart.HandleCreated += (s, e) => Ui.SetShield(restart);
            var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("main.admin_banner") };
            var banner = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 4, 8, 4),
                BackColor = Color.FromArgb(255, 244, 206),
            };
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            banner.Controls.Add(label, 0, 0);
            banner.Controls.Add(restart, 1, 0);
            return banner;
        }

        // ---- Window size remembered between sessions (in 96-DPI units) ----

        static readonly Size DefaultClientSize = new Size(830, 640);

        static Size? LoadSize()
        {
            var parts = (Settings.Get("window") ?? "").Split('x');
            int w, h;
            if (parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h))
                return new Size(Math.Max(700, w), Math.Max(440, h));
            return null;
        }

        /// <summary>Saves the window size only when the user actually resized it, so an untouched app writes nothing.</summary>
        void SaveSize()
        {
            if (WindowState != FormWindowState.Normal) return;
            float scale = Ui.DpiScale(this);
            var size = new Size((int)Math.Round(ClientSize.Width / scale), (int)Math.Round(ClientSize.Height / scale));
            var saved = LoadSize() ?? DefaultClientSize;
            if (Math.Abs(size.Width - saved.Width) > 2 || Math.Abs(size.Height - saved.Height) > 2)
                Settings.Set("window", size.Width + "x" + size.Height);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSize();
            cleaner.CancelWork();
            programs.CancelWork();
            leftovers.CancelWork();
            base.OnFormClosing(e);
        }
    }
}
