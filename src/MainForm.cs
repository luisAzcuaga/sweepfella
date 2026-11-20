using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Sweepfella
{
    /// <summary>
    /// Keeps the app alive while swapping windows: changing the language or theme opens a new window in the
    /// same place and closes the old one, so everything is built and DPI-scaled from scratch with the new settings.
    /// </summary>
    sealed class WindowHost : ApplicationContext
    {
        public WindowHost()
        {
            Settings.SaveFailed += () => MessageBox.Show(
                L.T("settings.readonly", AppDomain.CurrentDomain.BaseDirectory), L.T("settings.readonly.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Theme.Refresh();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Show(new MainForm(this, 0));
        }

        public void Replace(MainForm old, int tabIndex)
        {
            Theme.Refresh();
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

        /// <summary>With the "System" theme, follow Windows when its light/dark mode changes while the app is open.</summary>
        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            var form = MainForm as MainForm;
            if (form == null || form.IsDisposed || Theme.Mode != ThemeMode.System || Theme.SystemPrefersDark == Theme.IsDark) return;
            form.BeginInvoke(new Action(() =>
            {
                if (!form.IsDisposed && !form.IsBusy && Theme.SystemPrefersDark != Theme.IsDark) Replace(form, form.SelectedPage);
            }));
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;   // static event: don't leak the host
            base.ExitThreadCore();
        }
    }

    sealed class MainForm : Form
    {
        readonly WindowHost host;
        readonly bool isAdmin = Elevation.IsAdmin();
        readonly CleanerPage cleaner;
        readonly ProgramsPage programs;
        readonly LeftoversPage leftovers;
        readonly RepairsPage repairs;
        readonly PageHost pages = new PageHost();
        readonly LinkLabel about = new LinkLabel { AutoSize = true, Margin = new Padding(0, 10, 14, 0) };
        readonly ComboBox theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(0, 6, 8, 0) };
        readonly ComboBox language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(0, 6, 0, 0) };

        public MainForm(WindowHost host, int pageIndex)
        {
            this.host = host;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = L.T(isAdmin ? "app.title_admin" : "app.title", "v" + AboutForm.Version);
            using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("sweepfella.ico"))
                if (icon != null) Icon = new Icon(icon);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = LoadSize() ?? DefaultClientSize;
            MinimumSize = new Size(720, 480);

            cleaner = new CleanerPage(isAdmin);
            programs = new ProgramsPage();
            leftovers = new LeftoversPage(isAdmin);
            repairs = new RepairsPage();
            pages.AddPage(L.T("main.tab.cleanup"), cleaner);
            pages.AddPage(L.T("main.tab.programs"), programs);
            pages.AddPage(L.T("main.tab.leftovers"), leftovers);
            pages.AddPage(L.T("main.tab.repairs"), repairs);
            pages.SelectedIndex = pageIndex;
            Controls.Add(pages);
            if (!isAdmin) Controls.Add(AdminBanner());   // added after the pages so it docks above them
            Controls.Add(updateBanner);                 // added last: topmost, shown once a newer release is found

            // About link, theme and language pickers at the right end of the tab strip.
            about.Text = L.T("main.about");
            about.LinkClicked += (s, e) =>
            {
                using (var dialog = new AboutForm())
                {
                    dialog.ShowDialog(this);
                    if (dialog.DataDeleted) Close();
                }
            };
            theme.DisplayMember = language.DisplayMember = "Value";
            theme.ValueMember = language.ValueMember = "Key";
            theme.DataSource = new List<KeyValuePair<ThemeMode, string>>
            {
                new KeyValuePair<ThemeMode, string>(ThemeMode.System, L.T("theme.system")),
                new KeyValuePair<ThemeMode, string>(ThemeMode.Light, L.T("theme.light")),
                new KeyValuePair<ThemeMode, string>(ThemeMode.Dark, L.T("theme.dark")),
            };
            language.DataSource = L.Available();
            theme.SelectionChangeCommitted += OnThemeChanged;
            language.SelectionChangeCommitted += OnLanguageChanged;
            pages.Trailing.Controls.AddRange(new Control[] { about, theme, language });

            Theme.Apply(this);
            ResumeLayout(false);
            PerformLayout();
        }

        public bool IsBusy { get { return cleaner.IsBusy || programs.IsBusy || leftovers.IsBusy || repairs.IsBusy; } }
        public int SelectedPage { get { return pages.SelectedIndex; } }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return pages.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- "A newer version is available" banner ----

        static bool updateDismissed;   // closing the banner hides it for the rest of the session
        readonly TableLayoutPanel updateBanner = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 4, 4, 4), Visible = false,
        };

        async void ShowUpdateBannerIfNeeded()
        {
            var latest = await Updates.StartupCheck();
            if (latest == null || !latest.IsNewer || updateDismissed || IsDisposed) return;

            updateBanner.BackColor = Theme.UpdateBanner;
            var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("update.available", latest.Tag) };
            var download = Ui.MakeButton(L.T("update.download"), (s, e) =>
            {
                try { using (System.Diagnostics.Process.Start(latest.Url)) { } }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var close = new Button
            {
                Text = "×", FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(6, 3, 0, 3),
                Font = new Font(Font.FontFamily, 11F), ForeColor = Theme.Text,
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (s, e) => { updateDismissed = true; updateBanner.Visible = false; };
            updateBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            updateBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            updateBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            updateBanner.Controls.Add(label, 0, 0);
            updateBanner.Controls.Add(download, 1, 0);
            updateBanner.Controls.Add(close, 2, 0);
            Theme.Apply(updateBanner);
            updateBanner.Visible = true;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ShowUpdateBannerIfNeeded();
            theme.SelectedValue = Theme.Mode;
            language.SelectedValue = L.Code;
        }

        /// <summary>Language and theme changes rebuild the window, so wait for running tasks to finish first.</summary>
        bool CanRebuild()
        {
            if (!IsBusy) return true;
            MessageBox.Show(this, L.T("main.busy"), L.T("main.busy.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        void OnLanguageChanged(object sender, EventArgs e)
        {
            string code = language.SelectedValue as string;
            if (code == null || code == L.Code) return;
            if (!CanRebuild()) { language.SelectedValue = L.Code; return; }
            L.Use(code);
            Settings.Set("language", code);
            host.Replace(this, pages.SelectedIndex);
        }

        void OnThemeChanged(object sender, EventArgs e)
        {
            if (!(theme.SelectedValue is ThemeMode)) return;
            var mode = (ThemeMode)theme.SelectedValue;
            if (mode == Theme.Mode) return;
            if (!CanRebuild()) { theme.SelectedValue = Theme.Mode; return; }
            Theme.Mode = mode;
            host.Replace(this, pages.SelectedIndex);
        }

        Control AdminBanner()
        {
            var restart = Ui.MakeButton(L.T("main.restart_admin"), (s, e) =>
            {
                if (Elevation.RestartAsAdmin()) Close();
            });
            restart.HandleCreated += (s, e) => Ui.SetShield(restart);
            restart.Padding = new Padding(18, 0, 0, 0);   // room for the UAC shield, which AutoSize doesn't account for
            var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = L.T("main.admin_banner") };
            var banner = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 4, 8, 4),
                BackColor = Theme.AdminBanner,
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
