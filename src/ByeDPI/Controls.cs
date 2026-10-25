using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ByeDPI
{
    static class Draw
    {
        // Focus outlines appear only after keyboard navigation, like native Windows controls
        public static bool KeyboardCues;

        const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, Line);
        }

        public static void TextCentered(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, Line | TextFormatFlags.HorizontalCenter);
        }

        public static void TextWrapped(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, Wrap);
        }

        public static int WrappedHeight(string s, Font f, int width, int maxLines)
        {
            int line = TextRenderer.MeasureText("Ag", f, new Size(int.MaxValue, int.MaxValue), Line).Height;
            int h = TextRenderer.MeasureText(s, f, new Size(width, int.MaxValue), Wrap).Height;
            return Math.Min(Math.Max(h, line), line * maxLines);
        }

        // Letter-spaced text like the Android connect button
        public static void Spaced(Graphics g, string s, Font f, Color c, Rectangle r, int spacing)
        {
            var widths = new int[s.Length];
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                widths[i] = TextRenderer.MeasureText(g, s[i].ToString(), f, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                total += widths[i] + (i > 0 ? spacing : 0);
            }
            int x = r.X + (r.Width - total) / 2;
            for (int i = 0; i < s.Length; i++)
            {
                TextRenderer.DrawText(g, s[i].ToString(), f, new Rectangle(x, r.Y, widths[i] + 2, r.Height), c,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                x += widths[i] + spacing;
            }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void ThemeScrollbars(Control c)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }
    }

    // Action bar: back arrow, title, settings and overflow buttons
    class TopBar : Control
    {
        public string Title = "";
        public bool ShowBack, ShowSettings, ShowMore = true;
        public event Action BackClicked, SettingsClicked, MoreClicked;
        int hover = -1, pressed = -1;

        public TopBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(56);
        }

        Rectangle Btn(int i)
        {
            int s = Theme.S(48), y = (Height - s) / 2;
            if (i == 0) return new Rectangle(Theme.S(8), y, s, s);
            if (i == 1) return new Rectangle(Width - Theme.S(8) - s * 2, y, s, s);
            return new Rectangle(Width - Theme.S(8) - s, y, s, s);
        }

        bool Has(int i)
        {
            return i == 0 ? ShowBack : i == 1 ? ShowSettings : ShowMore;
        }

        int Hit(Point p)
        {
            for (int i = 0; i < 3; i++)
                if (Has(i) && Btn(i).Contains(p)) return i;
            return -1;
        }

        public Point MenuAnchor
        {
            get { var r = Btn(2); return new Point(r.Right, r.Bottom); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string[] glyphs = { Theme.Glyph.Back, Theme.Glyph.Settings, Theme.Glyph.More };
            for (int i = 0; i < 3; i++)
            {
                if (!Has(i)) continue;
                var r = Btn(i);
                if (hover == i || pressed == i)
                    using (var b = new SolidBrush(pressed == i ? Theme.Pressed : Theme.Hover))
                        g.FillEllipse(b, r);
                Draw.TextCentered(g, glyphs[i], Theme.Icons(i == 1 ? 15 : 13), Theme.Text, r);
            }
            int x = ShowBack ? Theme.S(72) : Theme.S(18);
            int right = ShowSettings ? Btn(1).Left : ShowMore ? Btn(2).Left : Width;
            Draw.Text(g, Title, Theme.Font(15.5f), Theme.Text, new Rectangle(x, 0, right - x, Height));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = Hit(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int p = pressed;
            pressed = -1;
            Invalidate();
            if (p < 0 || p != Hit(e.Location)) return;
            var h = p == 0 ? BackClicked : p == 1 ? SettingsClicked : MoreClicked;
            if (h != null) h();
        }
    }

    // Main screen: big outlined button with status and proxy address below
    class HomeView : Control
    {
        public string ButtonText = "Connect", StatusText = "", AddressText = "", DetailText = "";
        public bool ButtonEnabled = true;
        public event Action ButtonClicked;
        bool hover, pressed;

        public HomeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        Rectangle ButtonRect
        {
            get
            {
                int w = Theme.S(170), h = Theme.S(58);
                int cy = (int)(Height * 0.56);
                return new Rectangle((Width - w) / 2, cy - h / 2, w, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ButtonRect;
            using (var path = Draw.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(4)))
            {
                if (ButtonEnabled && (hover || pressed))
                    using (var b = new SolidBrush(pressed ? Theme.Pressed : Theme.Hover)) g.FillPath(b, path);
                bool cue = Focused && Draw.KeyboardCues;
                using (var pen = new Pen(cue ? Theme.Accent : (ButtonEnabled ? Theme.Outline : Theme.TextDisabled),
                    cue ? Theme.S(2) : 1f))
                    g.DrawPath(pen, path);
            }
            Draw.Spaced(g, ButtonText, Theme.Font(21f), ButtonEnabled ? Theme.Text : Theme.TextDisabled, r, Theme.S(3));

            int y = r.Bottom + Theme.S(14);
            var f = Theme.Font(12f);
            int lh = TextRenderer.MeasureText("Ag", f).Height;
            Draw.TextCentered(g, StatusText, f, Theme.TextSecondary, new Rectangle(0, y, Width, lh));
            Draw.TextCentered(g, AddressText, f, Theme.TextSecondary, new Rectangle(0, y + lh, Width, lh));
            if (!string.IsNullOrEmpty(DetailText))
            {
                var df = Theme.Font(10f);
                int w = Width - Theme.S(48);
                int h = Draw.WrappedHeight(DetailText, df, w, 3);
                TextRenderer.DrawText(g, DetailText, df,
                    new Rectangle(Theme.S(24), y + lh * 2 + Theme.S(10), w, h), Theme.TextSecondary,
                    TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = ButtonRect.Contains(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h && ButtonEnabled ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Draw.KeyboardCues = false;
            Focus();
            pressed = ButtonRect.Contains(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = pressed && ButtonRect.Contains(e.Location);
            pressed = false;
            Invalidate();
            if (click) Fire();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Fire();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        void Fire()
        {
            if (!ButtonEnabled) return;
            var h = ButtonClicked;
            if (h != null) h();
        }
    }

    enum RowKind { Category, Divider, Item, Switch, Check }

    class PrefRow
    {
        public RowKind Kind;
        public string Title;
        public Func<string> Summary;
        public string Icon;
        public Func<bool> Visible = () => true;
        public Func<bool> Enabled = () => true;
        public Func<bool> IsChecked;
        public Action<bool> SetChecked;
        public Action Click;
        public Func<Color> IconColor;
        public Func<string> IconOf;
        public object Tag;
        public Rectangle Bounds;

        public bool Interactive
        {
            get { return Kind == RowKind.Item || Kind == RowKind.Switch || Kind == RowKind.Check; }
        }
    }

    // Scrollable Android-style PreferenceScreen
    class PrefList : ScrollableControl
    {
        readonly List<PrefRow> rows = new List<PrefRow>();
        readonly List<PrefRow> shown = new List<PrefRow>();
        PrefRow hover, pressed;
        int focus = -1;
        public Color? Background;

        public PrefList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            TabStop = true;
        }

        public PrefRow Add(PrefRow r)
        {
            rows.Add(r);
            return r;
        }

        public void RemoveAll(Predicate<PrefRow> match)
        {
            rows.RemoveAll(match);
            hover = pressed = null;
            focus = -1;
        }

        public void Category(string title, Func<bool> visible = null)
        {
            Add(new PrefRow { Kind = RowKind.Category, Title = title, Visible = visible ?? (() => true) });
        }

        public void Divider()
        {
            Add(new PrefRow { Kind = RowKind.Divider });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Draw.ThemeScrollbars(this);
            RefreshRows();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) RefreshRows();
        }

        int TextLeft { get { return Theme.S(72); } }
        int TextRight(PrefRow r)
        {
            return Math.Max(TextLeft + 50, ClientSize.Width - Theme.S(r.Kind == RowKind.Switch ? 76 : r.Kind == RowKind.Check ? 60 : 24));
        }

        public void RefreshRows()
        {
            Draw.ThemeScrollbars(this);
            shown.Clear();
            int y = 0;
            foreach (var r in rows)
            {
                if (!r.Visible()) continue;
                // collapse consecutive or leading dividers left by hidden sections
                if (r.Kind == RowKind.Divider && (shown.Count == 0 || shown[shown.Count - 1].Kind == RowKind.Divider)) continue;
                int h;
                if (r.Kind == RowKind.Category) h = Theme.S(48);
                else if (r.Kind == RowKind.Divider) h = Theme.S(9);
                else
                {
                    string sum = r.Summary == null ? null : r.Summary();
                    int titleH = TextRenderer.MeasureText("Ag", Theme.Font(12f)).Height;
                    int sumH = string.IsNullOrEmpty(sum) ? 0 :
                        Draw.WrappedHeight(sum, Theme.Font(10.5f), TextRight(r) - TextLeft, 4);
                    h = Math.Max(Theme.S(sumH > 0 ? 72 : 56), titleH + sumH + Theme.S(32));
                }
                r.Bounds = new Rectangle(0, y, ClientSize.Width, h);
                y += h;
                shown.Add(r);
            }
            if (shown.Count > 0 && shown[shown.Count - 1].Kind == RowKind.Divider) shown.RemoveAt(shown.Count - 1);
            AutoScrollMinSize = new Size(0, y + Theme.S(16));
            if (focus >= shown.Count) focus = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Background ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int oy = AutoScrollPosition.Y;
            for (int i = 0; i < shown.Count; i++)
            {
                var row = shown[i];
                var b = row.Bounds;
                b.Offset(0, oy);
                if (b.Bottom < 0 || b.Top > ClientSize.Height) continue;
                PaintRow(g, row, b, i == focus && Focused && Draw.KeyboardCues);
            }
        }

        void PaintRow(Graphics g, PrefRow row, Rectangle b, bool focused)
        {
            if (row.Kind == RowKind.Divider)
            {
                using (var p = new Pen(Theme.Divider)) g.DrawLine(p, 0, b.Y + b.Height / 2, b.Right, b.Y + b.Height / 2);
                return;
            }
            if (row.Kind == RowKind.Category)
            {
                Draw.Text(g, row.Title, Theme.Font(10.5f, FontStyle.Bold), Theme.Accent,
                    new Rectangle(TextLeft, b.Y + Theme.S(16), b.Width - TextLeft, b.Height - Theme.S(16)));
                return;
            }
            bool enabled = row.Enabled();
            if (enabled && (row == hover || row == pressed))
                using (var br = new SolidBrush(row == pressed ? Theme.Pressed : Theme.Hover)) g.FillRectangle(br, b);
            if (focused)
                using (var p = new Pen(Theme.Accent, Theme.S(2)))
                    g.DrawRectangle(p, b.X + 1, b.Y + 1, b.Width - 3, b.Height - 3);

            string icon = row.IconOf != null ? row.IconOf() : row.Icon;
            if (icon != null)
                Draw.TextCentered(g, icon, Theme.Icons(17),
                    row.IconColor != null ? row.IconColor() : enabled ? Theme.Text : Theme.TextDisabled,
                    new Rectangle(Theme.S(16), b.Y, Theme.S(40), b.Height));

            string sum = row.Summary == null ? null : row.Summary();
            var tf = Theme.Font(12f);
            var sf = Theme.Font(10.5f);
            int titleH = TextRenderer.MeasureText("Ag", tf).Height;
            int width = TextRight(row) - TextLeft;
            int sumH = string.IsNullOrEmpty(sum) ? 0 : Draw.WrappedHeight(sum, sf, width, 4);
            int top = b.Y + (b.Height - titleH - sumH) / 2;
            Draw.Text(g, row.Title, tf, enabled ? Theme.Text : Theme.TextDisabled,
                new Rectangle(TextLeft, top, width, titleH));
            if (sumH > 0)
                Draw.TextWrapped(g, sum, sf, enabled ? Theme.TextSecondary : Theme.TextDisabled,
                    new Rectangle(TextLeft, top + titleH, width, sumH));

            if (row.Kind == RowKind.Switch) PaintSwitch(g, b, row.IsChecked(), enabled);
            if (row.Kind == RowKind.Check) PaintCheck(g, b, row.IsChecked(), enabled);
        }

        void PaintSwitch(Graphics g, Rectangle b, bool on, bool enabled)
        {
            float tw = Theme.S(34), th = Theme.S(14), d = Theme.S(20);
            float x = b.Right - Theme.S(24) - tw, cy = b.Y + b.Height / 2f;
            var track = new RectangleF(x, cy - th / 2, tw, th);
            Color trackC = on ? Theme.SwitchOnTrack : Theme.SwitchOffTrack;
            Color thumbC = on ? Theme.SwitchOnThumb : Theme.SwitchOffThumb;
            if (!enabled) { trackC = Color.FromArgb(90, trackC); thumbC = Color.FromArgb(140, thumbC); }
            using (var p = Draw.Rounded(track, th / 2))
            using (var br = new SolidBrush(trackC)) g.FillPath(br, p);
            float tx = on ? track.Right - d + Theme.S(3) : track.X - Theme.S(3);
            using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillEllipse(sh, tx, cy - d / 2 + 1, d, d);
            using (var br = new SolidBrush(thumbC)) g.FillEllipse(br, tx, cy - d / 2, d, d);
        }

        void PaintCheck(Graphics g, Rectangle b, bool on, bool enabled)
        {
            float s = Theme.S(18);
            var r = new RectangleF(b.Right - Theme.S(24) - s, b.Y + (b.Height - s) / 2f, s, s);
            Color c = enabled ? (on ? Theme.SwitchOnThumb : Theme.TextSecondary) : Theme.TextDisabled;
            using (var p = Draw.Rounded(r, Theme.S(2)))
            {
                if (on) using (var br = new SolidBrush(c)) g.FillPath(br, p);
                else using (var pen = new Pen(c, Theme.S(2))) g.DrawPath(pen, p);
            }
            if (on)
                Draw.TextCentered(g, Theme.Glyph.Check, Theme.Icons(9.5f), Theme.Dark ? Theme.Bg : Color.White,
                    Rectangle.Round(r));
        }

        PrefRow HitRow(Point p)
        {
            p.Offset(0, -AutoScrollPosition.Y);
            foreach (var r in shown)
                if (r.Interactive && r.Bounds.Contains(p)) return r;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitRow(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h != null && h.Enabled() ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Draw.KeyboardCues = false;
            Focus();
            pressed = HitRow(e.Location);
            if (pressed != null) focus = shown.IndexOf(pressed);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var p = pressed;
            pressed = null;
            Invalidate();
            if (p != null && p == HitRow(e.Location)) Activate(p);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                int dir = e.KeyCode == Keys.Down ? 1 : -1;
                for (int i = focus + dir; i >= 0 && i < shown.Count; i += dir)
                {
                    if (!shown[i].Interactive) continue;
                    focus = i;
                    var b = shown[i].Bounds;
                    int top = -AutoScrollPosition.Y;
                    if (b.Top < top) AutoScrollPosition = new Point(0, b.Top);
                    else if (b.Bottom > top + ClientSize.Height) AutoScrollPosition = new Point(0, b.Bottom - ClientSize.Height);
                    break;
                }
                Invalidate();
            }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && focus >= 0)
            {
                Activate(shown[focus]);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            if (focus < 0)
                for (int i = 0; i < shown.Count; i++)
                    if (shown[i].Interactive) { focus = i; break; }
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        void Activate(PrefRow r)
        {
            if (!r.Enabled()) return;
            if ((r.Kind == RowKind.Switch || r.Kind == RowKind.Check) && r.SetChecked != null)
                r.SetChecked(!r.IsChecked());
            else if (r.Click != null)
                r.Click();
            RefreshRows();
        }
    }

    class FlatButton : Control
    {
        bool hover, pressed;

        public FlatButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Size = new Size(Theme.S(88), Theme.S(36));
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.DialogBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover || pressed || Focused)
                using (var p = Draw.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(4)))
                using (var b = new SolidBrush(pressed ? Theme.Pressed : Theme.Hover)) g.FillPath(b, p);
            Draw.TextCentered(g, Text, Theme.Font(10.5f, FontStyle.Bold), Theme.Accent, ClientRectangle);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }
    }

    // Material-style dialogs
    class ThemedDialog : Form
    {
        protected readonly FlowLayoutPanel Buttons;

        public ThemedDialog(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.DialogBg;
            ForeColor = Theme.Text;
            Font = Theme.Font(10.5f);
            AutoScaleMode = AutoScaleMode.None;
            Padding = new Padding(Theme.S(20), Theme.S(16), Theme.S(12), Theme.S(10));
            Buttons = new FlowLayoutPanel();
            Buttons.FlowDirection = FlowDirection.RightToLeft;
            Buttons.Dock = DockStyle.Bottom;
            Buttons.Height = Theme.S(44);
            Buttons.BackColor = Theme.DialogBg;
            Controls.Add(Buttons);
        }

        protected FlatButton AddButton(string text, DialogResult result)
        {
            var b = new FlatButton(text);
            b.Click += (s, e) => { DialogResult = result; Close(); };
            Buttons.Controls.Add(b);
            return b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, Theme.Dark);
        }
    }

    class InputDialog : ThemedDialog
    {
        readonly TextBox box;
        readonly Func<string, bool> validate;

        InputDialog(string title, string value, bool multiline, Func<string, bool> validate)
            : base(title)
        {
            this.validate = validate;
            ClientSize = new Size(Theme.S(420), Theme.S(multiline ? 280 : 150));
            var label = new Label { Text = title, Dock = DockStyle.Top, Height = Theme.S(34),
                Font = Theme.Font(13f), ForeColor = Theme.Text };
            box = new TextBox
            {
                Text = (value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                AcceptsReturn = multiline,
                WordWrap = true,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.InputBg,
                ForeColor = Theme.Text,
                Font = multiline ? new Font("Consolas", 11f) : Theme.Font(11.5f)
            };
            var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, Theme.S(8), Theme.S(8)) };
            holder.Controls.Add(box);
            Controls.Add(holder);
            Controls.Add(label);
            holder.BringToFront();
            var ok = AddButton("OK", DialogResult.None);
            ok.Click += (s, e) => TryAccept();
            AddButton("Cancel", DialogResult.Cancel);
            if (!multiline) AcceptButtonEnter();
            Shown += (s, e) => { box.Focus(); box.SelectAll(); Draw.ThemeScrollbars(box); };
        }

        void AcceptButtonEnter()
        {
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryAccept(); }
            };
        }

        void TryAccept()
        {
            string v = Value;
            if (validate != null && !validate(v))
            {
                MessageBox.Show(this, "Invalid value for " + Text + ": " + v, "ByeDPI",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        string Value
        {
            get { return Multiline ? box.Text.Replace("\r\n", "\n") : box.Text.Trim(); }
        }

        bool Multiline { get { return box.Multiline; } }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        public static string Show(IWin32Window owner, string title, string value, bool multiline,
            Func<string, bool> validate)
        {
            using (var d = new InputDialog(title, value, multiline, validate))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
        }
    }

    class ChoiceDialog : ThemedDialog
    {
        int selected;

        ChoiceDialog(string title, string[] entries, int current)
            : base(title)
        {
            selected = current;
            int rowH = Theme.S(44);
            ClientSize = new Size(Theme.S(340), Theme.S(60) + rowH * entries.Length + Theme.S(50));
            var label = new Label { Text = title, Dock = DockStyle.Top, Height = Theme.S(40),
                Font = Theme.Font(13f), ForeColor = Theme.Text };
            var list = new PrefList { Dock = DockStyle.Fill, Background = Theme.DialogBg };
            for (int i = 0; i < entries.Length; i++)
            {
                int idx = i;
                list.Add(new PrefRow
                {
                    Kind = RowKind.Item,
                    Title = entries[i],
                    Icon = idx == current ? "" : "",
                    Click = () => { selected = idx; DialogResult = DialogResult.OK; Close(); }
                });
            }
            Controls.Add(list);
            Controls.Add(label);
            list.BringToFront();
            AddButton("Cancel", DialogResult.Cancel);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        public static int Show(IWin32Window owner, string title, string[] entries, int current)
        {
            using (var d = new ChoiceDialog(title, entries, current))
                return d.ShowDialog(owner) == DialogResult.OK ? d.selected : -1;
        }
    }

    // Transient message at the bottom of the window, like an Android Toast
    class Toast : Control
    {
        readonly Timer timer = new Timer();

        public Toast()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Visible = false;
            timer.Tick += (s, e) => { timer.Stop(); Visible = false; };
        }

        public void Show(string text)
        {
            Text = text;
            var parent = Parent;
            int maxW = parent.ClientSize.Width - Theme.S(48);
            var size = TextRenderer.MeasureText(text, Theme.Font(10.5f), new Size(maxW - Theme.S(32), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size = new Size(Math.Min(maxW, size.Width + Theme.S(36)), size.Height + Theme.S(22));
            Location = new Point((parent.ClientSize.Width - Width) / 2, parent.ClientSize.Height - Height - Theme.S(36));
            BackColor = Theme.Bg;
            Visible = true;
            BringToFront();
            Invalidate();
            timer.Stop();
            timer.Interval = text.Length > 60 ? 6000 : 2500;
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var p = Draw.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Height > Theme.S(48) ? Theme.S(12) : Height / 2f))
            using (var b = new SolidBrush(Theme.ToastBg)) g.FillPath(b, p);
            TextRenderer.DrawText(g, Text, Theme.Font(10.5f),
                new Rectangle(Theme.S(18), 0, Width - Theme.S(36), Height), Theme.ToastText,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                TextFormatFlags.HorizontalCenter);
        }

        protected override void OnClick(EventArgs e)
        {
            Visible = false;
        }
    }

    class MenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.DialogBg);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Divider))
                e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            using (var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.Hover : Theme.DialogBg))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Divider))
                e.Graphics.DrawLine(p, 0, e.Item.Height / 2, e.Item.Width, e.Item.Height / 2);
        }

        public static ContextMenuStrip Create()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new MenuRenderer();
            m.ShowImageMargin = false;
            m.Font = Theme.Font(10.5f);
            m.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
            return m;
        }

        public static ToolStripMenuItem Item(string text, Action a)
        {
            var i = new ToolStripMenuItem(text);
            i.Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(24), Theme.S(6));
            i.Click += (s, e) => a();
            return i;
        }
    }
}
