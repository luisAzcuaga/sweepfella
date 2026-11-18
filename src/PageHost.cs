using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Sweepfella
{
    /// <summary>
    /// Tabs drawn by Sweepfella instead of the native TabControl, which always paints light tab headers.
    /// A strip of text tabs (the selected one underlined in the accent color) with room on the right for
    /// extra controls, above the selected page.
    /// </summary>
    sealed class PageHost : UserControl
    {
        const int StripHeight = 36;

        readonly Panel strip = new Panel { Dock = DockStyle.Top, Height = StripHeight + 1, Padding = new Padding(0, 0, 0, 1) };   // 1 px for the bottom border
        readonly FlowLayoutPanel tabsRow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(6, 0, 0, 0), Margin = new Padding(0) };
        readonly Panel body = new Panel { Dock = DockStyle.Fill };
        readonly List<Control> pages = new List<Control>();
        readonly List<TabButton> buttons = new List<TabButton>();
        int selected = -1;

        /// <summary>Controls shown at the right end of the tab strip.</summary>
        public readonly FlowLayoutPanel Trailing = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 6, 0), Margin = new Padding(0),
        };

        public PageHost()
        {
            Dock = DockStyle.Fill;
            strip.BackColor = Theme.Back;
            body.BackColor = Theme.Page;
            strip.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
            };
            strip.Controls.Add(tabsRow);
            strip.Controls.Add(Trailing);
            Controls.Add(body);
            Controls.Add(strip);
        }

        public void AddPage(string title, Control page)
        {
            int index = pages.Count;
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            pages.Add(page);
            body.Controls.Add(page);

            var button = new TabButton { Text = title, Height = StripHeight };
            button.Click += (s, e) => SelectedIndex = index;
            buttons.Add(button);
            tabsRow.Controls.Add(button);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (pages.Count == 0) return;
                value = Math.Max(0, Math.Min(value, pages.Count - 1));
                if (value == selected) return;
                selected = value;
                // Size the page before showing it: hidden docked controls aren't laid out, so a page that was never
                // shown would otherwise load at its default 150×150.
                pages[value].Bounds = body.ClientRectangle;
                for (int i = 0; i < pages.Count; i++)
                {
                    pages[i].Visible = i == value;
                    buttons[i].Selected = i == value;
                }
            }
        }

        /// <summary>Ctrl+Tab / Ctrl+Shift+Tab move between pages, like the native tabs did.</summary>
        public bool HandleKey(Keys keyData)
        {
            if (pages.Count == 0) return false;
            if (keyData == (Keys.Control | Keys.Tab)) { SelectedIndex = (selected + 1) % pages.Count; return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectedIndex = (selected + pages.Count - 1) % pages.Count; return true; }
            return false;
        }

        sealed class TabButton : Control
        {
            bool isSelected, hover;

            public TabButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                Margin = new Padding(0);
                BackColor = Theme.Back;
            }

            public bool Selected
            {
                get { return isSelected; }
                set { isSelected = value; Invalidate(); }
            }

            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }
            protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); }
            protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); FitWidth(); }

            void FitWidth()
            {
                Width = TextRenderer.MeasureText(Text, Font).Width + 28;
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                var color = isSelected || hover ? Theme.Text : Theme.Subtle;
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (!isSelected) return;
                float scale = e.Graphics.DpiX / 96f;
                int thickness = Math.Max(2, (int)Math.Round(3 * scale));
                int inset = (int)Math.Round(10 * scale);
                using (var brush = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(brush, inset, Height - thickness - 1, Width - 2 * inset, thickness);
            }
        }
    }
}
