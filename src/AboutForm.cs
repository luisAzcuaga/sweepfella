using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Sweepfella
{
    /// <summary>About dialog: version, project links and a Buy Me a Coffee button.</summary>
    sealed class AboutForm : Form
    {
        public const string RepoUrl = "https://github.com/luisAzcuaga/sweepfella";
        public const string CoffeeUrl = "https://buymeacoffee.com/luisazcuaga";
        public const string LicenseUrl = RepoUrl + "/blob/main/LICENSE";
        static readonly Color CoffeeYellow = Color.FromArgb(255, 221, 0);
        static readonly Color CoffeeYellowHover = Color.FromArgb(255, 230, 70);

        /// <summary>Version from AssemblyInfo, e.g. "0.1.0".</summary>
        public static string Version
        {
            get { return typeof(AboutForm).Assembly.GetName().Version.ToString(3); }
        }

        static string Copyright
        {
            get
            {
                var attr = (AssemblyCopyrightAttribute)Attribute.GetCustomAttribute(typeof(AboutForm).Assembly, typeof(AssemblyCopyrightAttribute));
                return attr != null ? attr.Copyright : "";
            }
        }

        public AboutForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = L.T("about_dialog.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 20, 20, 14);

            var logo = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 18, 0) };
            using (var stream = typeof(AboutForm).Assembly.GetManifestResourceStream("sweepfella.ico"))
                if (stream != null)
                    using (var icon = new Icon(stream, 64, 64)) logo.Image = icon.ToBitmap();

            var name = new Label { AutoSize = true, Text = "Sweepfella", Font = new Font("Segoe UI", 16F, FontStyle.Bold), Margin = new Padding(0) };
            var version = new Label { AutoSize = true, Text = L.T("about_dialog.version", Version), ForeColor = Theme.Subtle, Margin = new Padding(2, 0, 0, 10) };
            var tagline = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Text = L.T("about_dialog.tagline"), Margin = new Padding(2, 0, 0, 8) };
            var source = new LinkLabel { AutoSize = true, Text = L.T("about_dialog.source"), Margin = new Padding(2, 0, 0, 0) };
            source.LinkClicked += (s, e) => Open(RepoUrl);
            var checkNow = new LinkLabel { AutoSize = true, Text = L.T("about_dialog.check_updates"), Margin = new Padding(14, 0, 0, 0) };
            checkNow.LinkClicked += async (s, e) =>
            {
                checkNow.Enabled = false;
                checkNow.Text = L.T("about_dialog.checking");
                try
                {
                    var latest = await Updates.CheckNow();
                    if (IsDisposed) return;
                    if (!latest.IsNewer)
                        MessageBox.Show(this, L.T("about_dialog.up_to_date", "v" + Version), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else if (MessageBox.Show(this, L.T("about_dialog.update_available", latest.Tag), Text, MessageBoxButtons.YesNo,
                                             MessageBoxIcon.Information) == DialogResult.Yes)
                        Open(latest.Url);
                }
                catch (Exception ex)   // offline, timeout, unexpected response…
                {
                    if (!IsDisposed) MessageBox.Show(this, L.T("about_dialog.update_failed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    if (!IsDisposed)
                    {
                        checkNow.Text = L.T("about_dialog.check_updates");
                        checkNow.Enabled = true;
                    }
                }
            };
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            links.Controls.AddRange(new Control[] { source, checkNow });
            var atStartup = new CheckBox { AutoSize = true, Text = L.T("about_dialog.check_at_startup"), Checked = Updates.CheckAtStartup, Margin = new Padding(2, 0, 0, 16) };
            atStartup.CheckedChanged += (s, e) => Updates.CheckAtStartup = atStartup.Checked;
            var coffeeNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Text = L.T("about_dialog.coffee_note"), Margin = new Padding(2, 0, 0, 6) };

            var coffee = new Button
            {
                AutoSize = true, Text = L.T("about_dialog.coffee"), Cursor = Cursors.Hand,
                BackColor = CoffeeYellow, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.75F, FontStyle.Bold), Padding = new Padding(10, 3, 10, 3), Margin = new Padding(2, 0, 0, 0),
            };
            coffee.FlatAppearance.BorderSize = 0;
            coffee.FlatAppearance.MouseOverBackColor = CoffeeYellowHover;
            coffee.FlatAppearance.MouseDownBackColor = CoffeeYellow;
            coffee.Click += (s, e) => Open(CoffeeUrl);

            // "Space freed: 12.4 GB since November 13, 2026 · Reset"
            var freed = new Label { AutoSize = true, Margin = new Padding(2, 0, 0, 0) };
            var reset = new LinkLabel { AutoSize = true, Text = L.T("about_dialog.reset"), Margin = new Padding(6, 0, 0, 0) };
            Action showFreed = () =>
            {
                var since = Stats.Since;
                freed.Text = since == null
                    ? L.T("about_dialog.freed_none")
                    : L.T("about_dialog.freed", Bytes.Format(Stats.Freed), since.Value.ToString(L.T("format.long_date"), L.Culture));
                reset.Visible = since != null;
            };
            showFreed();
            reset.LinkClicked += (s, e) =>
            {
                if (MessageBox.Show(this, L.T("about_dialog.reset_confirm"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                Stats.Reset();
                showFreed();
            };
            var freedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 16) };
            freedRow.Controls.AddRange(new Control[] { freed, reset });

            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            text.Controls.AddRange(new Control[] { name, version, tagline, links, atStartup, freedRow, coffeeNote, coffee });
            text.Controls.AddRange(DataSection());

            var top = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            top.Controls.Add(logo, 0, 0);
            top.Controls.Add(text, 1, 0);

            var close = Ui.MakeButton(L.T("about_dialog.close"), (s, e) => Close());
            AcceptButton = close;
            CancelButton = close;
            // "© 2026 Luis Azcuaga · MIT License", with only the license part as a link.
            string license = L.T("about_dialog.license");
            var copyright = new LinkLabel { AutoSize = true, Text = Copyright + " · " + license, ForeColor = Theme.Subtle, Anchor = AnchorStyles.Left };
            copyright.LinkArea = new LinkArea(copyright.Text.Length - license.Length, license.Length);
            copyright.LinkClicked += (s, e) => Open(LicenseUrl);
            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 18, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(copyright, 0, 0);
            footer.Controls.Add(close, 1, 0);

            // An AutoSize form ignores its Padding for undocked children, so place the content inside the margin by hand.
            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0), Location = new Point(Padding.Left, Padding.Top) };
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(footer, 0, 1);
            Controls.Add(root);
            Theme.Apply(this);
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>True when the user deleted their data; the main window then closes without saving.</summary>
        public bool DataDeleted { get; private set; }

        /// <summary>Where the settings live, a warning if they can't be saved, and "Delete my data and exit".</summary>
        Control[] DataSection()
        {
            var controls = new List<Control>();
            var gray = Theme.Subtle;
            controls.Add(new Label { AutoSize = true, ForeColor = gray, Text = L.T("about_dialog.data_file"), Margin = new Padding(2, 18, 0, 0) });
            // Zero-width spaces after each backslash let long paths wrap at folder boundaries.
            string path = Settings.FilePath.Replace("\\", "\\​");
            if (!Settings.FileExists) path += " " + L.T("about_dialog.data_not_created");
            controls.Add(new Label { AutoSize = true, ForeColor = gray, MaximumSize = new Size(330, 0), Text = path, Margin = new Padding(2, 0, 0, 4) });

            if (!Settings.CanWrite)
                controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Theme.Warning,
                    Text = L.T("about_dialog.data_readonly"), Margin = new Padding(2, 2, 0, 4),
                });

            if (Settings.HasAnyData)
            {
                var delete = new LinkLabel { AutoSize = true, Text = L.T("about_dialog.delete_data"), Margin = new Padding(2, 2, 0, 0) };
                delete.LinkClicked += (s, e) =>
                {
                    if (MessageBox.Show(this, L.T("about_dialog.delete_confirm", Settings.FilePath), Text, MessageBoxButtons.YesNo,
                                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    Settings.DeleteAll();
                    DataDeleted = true;
                    Close();
                };
                controls.Add(delete);
            }
            return controls.ToArray();
        }

        static void Open(string url)
        {
            try { using (Process.Start(url)) { } }
            catch (Win32Exception) { }   // no default browser
        }
    }
}
