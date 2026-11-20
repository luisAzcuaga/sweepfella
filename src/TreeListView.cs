using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Sweepfella
{
    /// <summary>
    /// A list whose rows can expand into indented child rows (Cleanup and Leftovers tabs). It draws the expand
    /// arrows and a "mixed" checkbox for rows with only some children selected, and reports what the user asks for;
    /// the page keeps the rows and decides what checking means.
    /// Custom checkbox images make the native control cycle through all of them on every click, so clicks on a
    /// checkbox and Space are taken over and reported through ToggleRequested instead.
    /// </summary>
    class TreeListView : BufferedListView
    {
        public const int Unchecked = 0, Checked = 1, Mixed = 2;          // checkbox (state) images
        public const int Collapsed = 0, Expanded = 1, NoArrow = -1;      // row (small) images

        public Action<ListViewItem> ToggleRequested;   // a click or Space on a checkbox
        public Action<ListViewItem> ExpandRequested;   // a click on an arrow, Right on a collapsed row, Left on an expanded one
        public Func<ListViewItem, ListViewItem> ParentOf;   // lets Left on a child row jump to its parent

        ImageList states;

        public TreeListView()
        {
            CheckBoxes = true;
            SmallImageList = MakeArrows();
        }

        /// <summary>Shows a row's checkbox as unchecked, checked or mixed without raising check events for checked ↔ mixed.</summary>
        public void SetMark(ListViewItem item, int mark)
        {
            if (IsHandleCreated && states != null && item.StateImageIndex != mark) item.StateImageIndex = mark;
        }

        public bool IsMixed(ListViewItem item)
        {
            return IsHandleCreated && item.StateImageIndex == Mixed;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_KEYDOWN = 0x100, WM_LBUTTONDOWN = 0x201, WM_LBUTTONDBLCLK = 0x203, VK_SPACE = 0x20;
            if (ToggleRequested != null && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK))
            {
                long lp = m.LParam.ToInt64();
                var hit = HitTest(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (hit.Item != null && hit.Location == ListViewHitTestLocations.StateImage)
                {
                    Focus();
                    ToggleRequested(hit.Item);
                    return;
                }
            }
            else if (ToggleRequested != null && m.Msg == WM_KEYDOWN && m.WParam.ToInt64() == VK_SPACE && FocusedItem != null)
            {
                ToggleRequested(FocusedItem);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || ExpandRequested == null) return;
            var hit = HitTest(e.Location);
            if (hit.Item != null && hit.Item.ImageIndex != NoArrow && hit.Location == ListViewHitTestLocations.Image) ExpandRequested(hit.Item);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var item = FocusedItem;
            if (e.Handled || item == null) return;
            if (ExpandRequested != null && ((e.KeyCode == Keys.Right && item.ImageIndex == Collapsed) || (e.KeyCode == Keys.Left && item.ImageIndex == Expanded)))
            {
                ExpandRequested(item);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Left && ParentOf != null)
            {
                var parent = ParentOf(item);
                if (parent == null) return;
                SelectedItems.Clear();
                parent.Selected = parent.Focused = true;
                parent.EnsureVisible();
                e.Handled = true;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke((Action)InstallStates);   // after the theme has been applied, so the copied boxes match it
        }

        /// <summary>Expand/collapse arrows (▶ ▼), drawn in the theme's subtle color at the screen's DPI.</summary>
        static ImageList MakeArrows()
        {
            float dpi;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;
            int s = Math.Max(16, (int)Math.Round(16 * dpi));
            var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(s, s) };
            images.Images.Add(Arrow(s, new[] { new PointF(s * 0.38f, s * 0.26f), new PointF(s * 0.38f, s * 0.74f), new PointF(s * 0.68f, s * 0.5f) }));
            images.Images.Add(Arrow(s, new[] { new PointF(s * 0.26f, s * 0.38f), new PointF(s * 0.74f, s * 0.38f), new PointF(s * 0.5f, s * 0.68f) }));
            return images;
        }

        static Bitmap Arrow(int size, PointF[] triangle)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Theme.Subtle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPolygon(brush, triangle);
            }
            return bmp;
        }

        /// <summary>
        /// Replaces the list's checkbox images with copies of Windows' own unchecked and checked boxes plus a mixed
        /// one made from the checked box (tick painted over, dash drawn), so all three match the current theme.
        /// </summary>
        void InstallStates()
        {
            if (IsDisposed || !IsHandleCreated) return;
            IntPtr current = Native.SendMessage(Handle, LVM_GETIMAGELIST, (IntPtr)LVSIL_STATE, IntPtr.Zero);
            if (current == IntPtr.Zero || ImageList_GetImageCount(current) != 2) return;
            Bitmap off = Grab(current, 0), on = Grab(current, 1);
            if (off == null || on == null) return;
            states = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = on.Size };
            states.Images.Add(off);
            states.Images.Add(on);
            states.Images.Add(MakeMixed(on));
            Native.SendMessage(Handle, LVM_SETIMAGELIST, (IntPtr)LVSIL_STATE, states.Handle);
            Invalidate();
            if (StatesReady != null) StatesReady();
        }

        /// <summary>Raised once the mixed checkbox can be shown, so marks set before that can be applied.</summary>
        public Action StatesReady;

        static Bitmap Grab(IntPtr imageList, int index)
        {
            IntPtr icon = ImageList_GetIcon(imageList, index, 0);
            if (icon == IntPtr.Zero) return null;
            try { using (var i = Icon.FromHandle(icon)) return i.ToBitmap(); }
            finally { DestroyIcon(icon); }
        }

        static Bitmap MakeMixed(Bitmap check)
        {
            var b = new Bitmap(check);
            // The box's fill is its most common opaque color; the tick is the opaque color farthest from it.
            var counts = new Dictionary<int, int>();
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.A < 250) continue;
                    int n;
                    counts.TryGetValue(c.ToArgb(), out n);
                    counts[c.ToArgb()] = n + 1;
                }
            if (counts.Count == 0) return b;
            var fill = Color.FromArgb(counts.OrderByDescending(kv => kv.Value).First().Key);
            var mark = counts.Keys.Select(Color.FromArgb).OrderByDescending(c => Distance(c, fill)).First();
            int s = b.Width;
            using (var g = Graphics.FromImage(b))
            {
                using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, s * 0.2f, s * 0.2f, s * 0.6f, s * 0.6f);
                float h = Math.Max(2f, s / 8f);
                using (var brush = new SolidBrush(mark)) g.FillRectangle(brush, s * 0.3f, (s - h) / 2f, s * 0.4f, h);
            }
            return b;
        }

        static int Distance(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }

        const int LVM_GETIMAGELIST = 0x1002, LVM_SETIMAGELIST = 0x1003, LVSIL_STATE = 2;

        [DllImport("comctl32.dll")]
        static extern int ImageList_GetImageCount(IntPtr himl);

        [DllImport("comctl32.dll")]
        static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
    }
}
