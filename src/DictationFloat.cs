using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDictation
{
    // 听写浮窗：无标题栏的深色胶囊。左侧麦克风光球、右上角声波动画仅为状态示意，不读取麦克风。
    // 输入框必须真实获焦，豆包才会把文字提交进来；外观改动不影响焦点/取消/粘贴逻辑。
    // 没看到远端画面变化时的小提醒：不抢焦点，8 秒后自动消失；文字保留，可一键再贴或换粘贴键再贴。
    internal sealed class PasteNotice : Form
    {
        private readonly Label message = new Label();
        private readonly Button retry = new Button(), swap = new Button(), close = new Button();
        private readonly Timer hideTimer = new Timer { Interval = 10000 };
        internal event Action RetryRequested, SwapRequested;
        internal PasteNotice()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 26, 38); ForeColor = Color.FromArgb(241, 245, 249); Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(520, 96); Icon = AppIcon.Load(32);
            message.SetBounds(16, 12, 490, 40); message.ForeColor = Color.FromArgb(251, 191, 36); Controls.Add(message);
            Style(retry, true); Style(swap, false); Style(close, false);
            retry.SetBounds(16, 56, 120, 30); swap.SetBounds(146, 56, 200, 30); close.SetBounds(356, 56, 80, 30); close.Text = "忽略";
            retry.Click += delegate { Hide(); if (RetryRequested != null) RetryRequested(); };
            swap.Click += delegate { Hide(); if (SwapRequested != null) SwapRequested(); };
            close.Click += delegate { Hide(); };
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }
        private void Style(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat; button.TabStop = false; button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            button.BackColor = primary ? Color.FromArgb(34, 211, 238) : Color.FromArgb(22, 28, 40);
            button.ForeColor = primary ? Color.FromArgb(8, 13, 22) : Color.FromArgb(241, 245, 249);
            Controls.Add(button);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; p.ClassStyle |= 0x00020000; return p; } } // 不激活、工具窗口、阴影
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen line = new Pen(Color.FromArgb(251, 191, 36), 2)) e.Graphics.DrawLine(line, 0, 1, ClientSize.Width, 1);
        }
        internal void Show(string text, string otherKeyName)
        {
            message.Text = text; retry.Text = "再贴一次"; swap.Text = "改用 " + otherKeyName + " 再贴";
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 48);
            if (!Visible) Show(); else Invalidate();
            hideTimer.Stop(); hideTimer.Start();
        }
    }

    internal sealed class DictationFloat : Form
    {
        private enum Mode { Listening, Organizing, Review }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal readonly ComposerTextBox Editor = new ComposerTextBox();
        private readonly Button paste = new Button(), discard = new Button();
        private readonly Timer animation = new Timer { Interval = 33 };
        internal event Action PasteRequested, DiscardRequested, CancelRequested;
        private readonly float scale;
        private Mode mode = Mode.Listening;
        private string message = "", destination = "";
        private bool error, disposing;
        private double phase;

        private static readonly Color Background = Color.FromArgb(14, 18, 27);
        private static readonly Color Panel = Color.FromArgb(20, 26, 38);
        private static readonly Color TextColor = Color.FromArgb(241, 245, 249);
        private static readonly Color Muted = Color.FromArgb(148, 163, 184);
        private static readonly Color Cyan = Color.FromArgb(34, 211, 238);
        private static readonly Color Violet = Color.FromArgb(139, 92, 246);
        private static readonly Color Amber = Color.FromArgb(251, 191, 36);
        private static readonly Color Rose = Color.FromArgb(248, 113, 113);

        internal DictationFloat()
        {
            Text = "远控听写"; Icon = AppIcon.Load(32); FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            TopMost = true; ShowInTaskbar = false; KeyPreview = true; BackColor = Background; MaximizeBox = false; MinimizeBox = false;
            DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Font = new Font("Microsoft YaHei UI", 9.5f);

            Editor.Multiline = true; Editor.AcceptsReturn = true; Editor.ScrollBars = ScrollBars.None; Editor.WordWrap = true;
            Editor.BorderStyle = BorderStyle.None; Editor.BackColor = Background; Editor.ForeColor = TextColor;
            Editor.Font = new Font("Microsoft YaHei UI", 13f); Editor.ImeMode = ImeMode.On; Editor.AccessibleName = "远控听写本机文字"; Editor.TraceKeys = true;
            Controls.Add(Editor);

            StyleButton(paste, "粘贴到远端", true); StyleButton(discard, "丢弃", false);
            paste.Click += delegate { if (PasteRequested != null) PasteRequested(); };
            discard.Click += delegate { if (DiscardRequested != null) DiscardRequested(); };

            animation.Tick += delegate { phase += 0.033; Invalidate(new Rectangle(0, 0, ClientSize.Width, S(40))); Invalidate(new Rectangle(0, 0, S(96), ClientSize.Height)); };
            VisibleChanged += delegate { if (Visible) animation.Start(); else animation.Stop(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (CancelRequested != null) CancelRequested(); }
                if (e.Control && e.KeyCode == Keys.Enter && !Editor.Composing) { e.SuppressKeyPress = true; if (PasteRequested != null) PasteRequested(); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            { if (!disposing) { e.Cancel = true; if (CancelRequested != null) CancelRequested(); } };
            ApplyLayout();
        }
        private int S(float value) { return (int)Math.Round(value * scale); }
        private void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text; button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand; button.TabStop = false;
            button.Font = new Font("Microsoft YaHei UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(103, 232, 249) : Color.FromArgb(30, 41, 59);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(14, 165, 233) : Color.FromArgb(15, 23, 42);
            button.BackColor = primary ? Cyan : Panel; button.ForeColor = primary ? Color.FromArgb(8, 13, 22) : TextColor;
            button.Visible = false; Controls.Add(button);
        }
        protected override CreateParams CreateParams
        { get { CreateParams p = base.CreateParams; p.ClassStyle |= 0x00020000; return p; } } // CS_DROPSHADOW
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 圆角
        }
        private void ApplyLayout()
        {
            bool review = mode == Mode.Review;
            ClientSize = new Size(S(540), S(review ? 206 : 126));
            Editor.SetBounds(S(96), S(42), S(422), S(review ? 100 : 52));
            Editor.BackColor = review ? Panel : Background;
            int y = S(160), h = S(32);
            paste.SetBounds(ClientSize.Width - S(22) - S(120), y, S(120), h);
            discard.SetBounds(paste.Left - S(10) - S(76), y, S(76), h);
            paste.Visible = discard.Visible = review;
            paste.Enabled = discard.Enabled = review;
            Invalidate();
        }

        internal void Prepare(string destination)
        {
            this.destination = destination ?? "";
            Editor.ReadOnly = false; Editor.Clear();
            mode = Mode.Listening; error = false; message = "正在聆听 · 松开自动粘贴 · Esc 取消";
            ApplyLayout();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - S(48));
            Show(); Native.ActivateOwnWindow(Handle); Editor.Focus();
        }
        internal void SetDestination(string name) { destination = name ?? ""; Invalidate(); }
        internal void Organizing()
        {
            mode = Mode.Organizing; error = false; message = "豆包正在整理文字，整理完自动粘贴 · Esc 取消";
            ApplyLayout();
        }
        internal bool Ready { get { return Visible && Native.GetForegroundWindow() == Handle && Editor.Focused; } }
        internal void Status(string text, bool isError, bool showActions)
        {
            message = text; error = isError;
            if (showActions) mode = Mode.Review;
            ApplyLayout();
        }
        internal void Review(string text)
        {
            Editor.ReadOnly = false; mode = Mode.Review; message = text; error = true;
            ApplyLayout();
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height - S(16))));
            Show(); Native.ActivateOwnWindow(Handle); Editor.Focus();
        }
        internal void Shutdown() { disposing = true; animation.Stop(); animation.Dispose(); Close(); Dispose(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle bounds = ClientRectangle;
            using (LinearGradientBrush back = new LinearGradientBrush(bounds, Color.FromArgb(17, 22, 33), Background, 90f)) g.FillRectangle(back, bounds);
            // 顶部一条青紫渐变光带
            using (LinearGradientBrush line = new LinearGradientBrush(new Rectangle(0, 0, bounds.Width, 2), Cyan, Violet, 0f))
                g.FillRectangle(line, 0, 0, bounds.Width, Math.Max(1, S(2)));
            if (mode == Mode.Review)
                using (SolidBrush panel = new SolidBrush(Panel)) g.FillRectangle(panel, Editor.Left - S(10), Editor.Top - S(6), Editor.Width + S(20), Editor.Height + S(12));
            DrawOrb(g, new PointF(S(48), S(62)));
            Color accent = mode == Mode.Review ? (error ? Amber : Cyan) : mode == Mode.Organizing ? Violet : Cyan;
            using (SolidBrush dot = new SolidBrush(accent)) g.FillEllipse(dot, S(96), S(19), S(7), S(7));
            string heading = mode == Mode.Review ? "可直接修改文字 · 还没有粘贴到远端" : (mode == Mode.Organizing ? "整理中" : "聆听中") + (destination.Length > 0 ? " · " + destination : "");
            using (Font bold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, heading, bold, new Point(S(108), S(13)), TextColor, TextFormatFlags.NoPadding);
            Rectangle statusArea = mode == Mode.Review
                ? new Rectangle(S(22), S(160), discard.Left - S(32), S(32))
                : new Rectangle(S(96), S(98), S(422), S(20));
            TextRenderer.DrawText(g, message, Font, statusArea, error ? (mode == Mode.Review ? Amber : Rose) : Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | (mode == Mode.Review ? TextFormatFlags.WordBreak : 0));
            if (mode != Mode.Review) DrawWave(g, new Rectangle(ClientSize.Width - S(150), S(12), S(128), S(20)));
            using (Pen border = new Pen(Color.FromArgb(40, 148, 163, 184))) g.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
        }
        private void DrawOrb(Graphics g, PointF center)
        {
            float radius = S(22);
            Color core = mode == Mode.Review ? Amber : mode == Mode.Organizing ? Violet : Cyan;
            if (mode == Mode.Listening)
                for (int i = 0; i < 2; i++)
                {
                    double t = (phase * 0.9 + i * 0.5) % 1.0;
                    float r = radius + (float)(t * S(16));
                    using (Pen ring = new Pen(Color.FromArgb((int)(120 * (1 - t)), core), Math.Max(1f, S(1.5f))))
                        g.DrawEllipse(ring, center.X - r, center.Y - r, r * 2, r * 2);
                }
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
                using (PathGradientBrush fill = new PathGradientBrush(path))
                {
                    fill.CenterColor = Color.FromArgb(230, Color.FromArgb(Math.Min(255, core.R + 60), Math.Min(255, core.G + 40), Math.Min(255, core.B + 20)));
                    fill.SurroundColors = new[] { Color.FromArgb(200, core.R / 2, core.G / 3, core.B) };
                    fill.CenterPoint = new PointF(center.X - radius * 0.3f, center.Y - radius * 0.35f);
                    g.FillPath(fill, path);
                }
            }
            if (mode == Mode.Organizing)
                using (Pen arc = new Pen(Color.FromArgb(220, Cyan), Math.Max(2f, S(2.5f))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float r = radius + S(6);
                    g.DrawArc(arc, center.X - r, center.Y - r, r * 2, r * 2, (float)(phase * 360 % 360), 100);
                }
            DrawMicGlyph(g, center, radius);
        }
        private void DrawMicGlyph(Graphics g, PointF c, float radius)
        {
            float w = radius * 0.46f, h = radius * 0.78f;
            using (Pen pen = new Pen(Color.FromArgb(10, 14, 22), Math.Max(1.6f, S(2f))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (SolidBrush body = new SolidBrush(Color.FromArgb(10, 14, 22)))
            using (GraphicsPath capsule = new GraphicsPath())
            {
                RectangleF r = new RectangleF(c.X - w / 2, c.Y - h * 0.62f, w, h * 0.78f);
                capsule.AddArc(r.X, r.Y, w, w, 180, 180); capsule.AddArc(r.X, r.Bottom - w, w, w, 0, 180); capsule.CloseFigure();
                g.FillPath(body, capsule);
                float cw = w * 1.7f;
                g.DrawArc(pen, c.X - cw / 2, c.Y - h * 0.38f, cw, h * 0.62f, 20, 140);
                g.DrawLine(pen, c.X, c.Y + h * 0.26f, c.X, c.Y + h * 0.44f);
            }
        }
        private void DrawWave(Graphics g, Rectangle area)
        {
            const int bars = 16;
            float gap = area.Width / (float)bars, width = Math.Max(2f, gap * 0.45f);
            using (LinearGradientBrush brush = new LinearGradientBrush(area, Cyan, Violet, 0f))
                for (int i = 0; i < bars; i++)
                {
                    double level = mode == Mode.Organizing
                        ? 0.18 + 0.22 * (0.5 + 0.5 * Math.Sin(phase * 4 - i * 0.55))
                        : 0.2 + 0.8 * Math.Abs(Math.Sin(phase * 5.3 + i * 0.7) * Math.Sin(phase * 2.1 + i * 0.33));
                    float h = Math.Max(width, (float)(area.Height * level));
                    float x = area.Left + i * gap, y = area.Top + (area.Height - h) / 2;
                    using (GraphicsPath bar = new GraphicsPath())
                    {
                        bar.AddArc(x, y, width, width, 180, 180); bar.AddArc(x, y + h - width, width, width, 0, 180); bar.CloseFigure();
                        g.FillPath(brush, bar);
                    }
                }
        }
        protected override void WndProc(ref Message message)
        {
            // 松开语音键 Alt 时不要激活系统菜单，否则会抢走输入框焦点。
            if (message.Msg == 0x0112 && (message.WParam.ToInt64() & 0xFFF0) == 0xF100) return;
            base.WndProc(ref message);
            // 空白处可拖动浮窗。
            if (message.Msg == 0x0084 && message.Result.ToInt32() == 1) message.Result = new IntPtr(2);
        }
    }
}
