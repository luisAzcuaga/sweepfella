using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Sweepfella
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>
    /// Light and dark palettes plus the native tricks WinForms needs for a dark UI: a dark title bar (DWM),
    /// Windows' own dark control themes for lists, scrollbars and buttons, and plain colors for the rest.
    /// The theme is fixed per window: switching it opens a new window, like switching the language.
    /// </summary>
    static class Theme
    {
        const string SettingKey = "theme";

        public static ThemeMode Mode
        {
            get
            {
                switch (Settings.Get(SettingKey))
                {
                    case "dark": return ThemeMode.Dark;
                    case "light": return ThemeMode.Light;
                    default: return ThemeMode.System;
                }
            }
            set { Settings.Set(SettingKey, value == ThemeMode.System ? null : value.ToString().ToLowerInvariant()); }   // default writes nothing
        }

        /// <summary>Windows' "app mode" setting (Settings → Personalization → Colors).</summary>
        public static bool SystemPrefersDark
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
        }

        public static bool IsDark { get; private set; }

        /// <summary>Re-evaluates the mode; call before building a window.</summary>
        public static void Refresh()
        {
            var mode = Mode;
            IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark);
        }

        // ---- Palette ----

        static Color Pick(Color light, Color dark) { return IsDark ? dark : light; }

        public static Color Back { get { return Pick(SystemColors.Control, Color.FromArgb(32, 32, 32)); } }               // window chrome, tab strip
        public static Color Page { get { return Pick(SystemColors.Window, Color.FromArgb(39, 39, 39)); } }                // page background
        public static Color Surface { get { return Pick(SystemColors.Window, Color.FromArgb(45, 45, 45)); } }             // lists, text boxes
        public static Color Text { get { return Pick(SystemColors.ControlText, Color.FromArgb(240, 240, 240)); } }
        public static Color Subtle { get { return Pick(SystemColors.GrayText, Color.FromArgb(160, 160, 160)); } }
        public static Color Border { get { return Pick(Color.FromArgb(213, 213, 213), Color.FromArgb(70, 70, 70)); } }
        public static Color Accent { get { return Pick(Color.FromArgb(0, 103, 192), Color.FromArgb(96, 170, 255)); } }
        public static Color Link { get { return Pick(Color.FromArgb(0, 102, 204), Color.FromArgb(110, 175, 255)); } }
        public static Color Warning { get { return Pick(Color.FromArgb(176, 96, 0), Color.FromArgb(255, 178, 80)); } }
        public static Color AdminBanner { get { return Pick(Color.FromArgb(255, 244, 206), Color.FromArgb(70, 58, 24)); } }
        public static Color UpdateBanner { get { return Pick(Color.FromArgb(222, 236, 255), Color.FromArgb(28, 50, 82)); } }

        // ---- Applying ----

        /// <summary>Themes a control tree. Light mode keeps the native look, so this only acts in dark mode.</summary>
        public static void Apply(Control root)
        {
            if (!IsDark) return;
            ApplyTo(root);
            foreach (Control child in root.Controls) Apply(child);
        }

        static void ApplyTo(Control c)
        {
            var form = c as Form;
            if (form != null)
            {
                form.BackColor = Back;
                form.ForeColor = Text;
                WhenCreated(form, DarkTitleBar);
                return;
            }
            var list = c as ListView;
            if (list != null)
            {
                list.BackColor = Surface;
                list.ForeColor = Text;
                // Windows' dark header theme darkens the background but keeps black text, so draw the headers here;
                // rows are still drawn natively (DrawDefault), keeping checkboxes, groups and per-cell colors.
                list.OwnerDraw = true;
                list.DrawColumnHeader += DrawHeader;
                list.DrawItem += (s, e) => e.DrawDefault = true;
                list.DrawSubItem += (s, e) => e.DrawDefault = true;
                WhenCreated(list, h =>
                {
                    SetWindowTheme(h, "DarkMode_ItemsView", null);   // what Explorer uses: dark group headers too
                    IntPtr header = Native.SendMessage(h, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
                });
                return;
            }
            var rich = c as RichTextBox;
            if (rich != null)
            {
                rich.BackColor = Surface;
                rich.ForeColor = Text;
                WhenCreated(rich, h => SetWindowTheme(h, "DarkMode_Explorer", null));
                return;
            }
            var text = c as TextBox;
            if (text != null)
            {
                text.BackColor = Surface;
                text.ForeColor = Text;
                text.BorderStyle = BorderStyle.FixedSingle;
                return;
            }
            var combo = c as ComboBox;
            if (combo != null)
            {
                combo.BackColor = Surface;
                combo.ForeColor = Text;
                WhenCreated(combo, h => SetWindowTheme(h, "DarkMode_CFD", null));
                return;
            }
            var button = c as Button;
            if (button != null)
            {
                // Flat buttons carry their own colors (Buy Me a Coffee, the banner's ×); native ones use Windows' dark theme,
                // which also keeps the UAC shield on "Restart as administrator".
                if (button.FlatStyle != FlatStyle.Flat) WhenCreated(button, h => SetWindowTheme(h, "DarkMode_Explorer", null));
                return;
            }
            var check = c as CheckBox;
            if (check != null)
            {
                check.FlatStyle = FlatStyle.Flat;
                check.ForeColor = Text;
                check.FlatAppearance.BorderColor = Subtle;
                check.FlatAppearance.CheckedBackColor = Surface;
                return;
            }
            var link = c as LinkLabel;
            if (link != null)
            {
                link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Link;
                return;
            }
            var progress = c as ProgressBar;
            if (progress != null)
            {
                // The themed progress bar is always light; the classic one takes custom colors.
                WhenCreated(progress, h =>
                {
                    SetWindowTheme(h, "", "");
                    Native.SendMessage(h, PBM_SETBKCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(Surface));
                    Native.SendMessage(h, PBM_SETBARCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(Accent));
                });
                return;
            }
            var split = c as SplitContainer;
            if (split != null) split.BackColor = Page;
        }

        static void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Back)) g.FillRectangle(back, e.Bounds);
            using (var line = new Pen(Border))
            {
                g.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);   // column separator
                g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);         // bottom edge
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            flags |= e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right
                   : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
            var text = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(g, e.Header.Text, e.Font, text, Subtle, flags);
        }

        static void WhenCreated(Control c, Action<IntPtr> action)
        {
            if (c.IsHandleCreated) action(c.Handle);
            else c.HandleCreated += (s, e) => action(c.Handle);
        }

        static void WhenCreated(Form f, Action<Form> action)
        {
            if (f.IsHandleCreated) action(f);
            else f.HandleCreated += (s, e) => action(f);
        }

        static void DarkTitleBar(Form form)
        {
            int on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 20H1+ and 11, 19 on older Windows 10 builds.
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }

        const int LVM_GETHEADER = 0x101F;
        const int PBM_SETBARCOLOR = 0x409;
        const int PBM_SETBKCOLOR = 0x2001;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
