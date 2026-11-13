// InputProbe: 诊断用，不是产品入口。只记录按键次数/少量功能键码、窗口元数据，不记录正文字符。
// 模式：
//   chain <out.json>                 在 ToDesk 远控窗口前台时注入 F24，比较“早装/晚装”两个钩子是否看到
//   alt <vk|vkscan|scan> <repeat0|repeat1> <ms> <out.json> <shot.png>
//                                    本程序自己的输入框获焦后注入左 Alt，观察豆包是否起反应（只记字数）
//   watch <seconds> <out.json>       置顶不抢焦点小窗，实时显示早装/晚装钩子看到的 F9/Alt/其他键次数；吞掉 F9
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class InputProbe
{
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    internal delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] internal struct KbData { internal uint key, scan, flags, time; internal UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { internal ushort key, scan; internal uint flags, time; internal UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int x, y; internal uint data, flags, time; internal UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] internal KeyboardInput k; [FieldOffset(0)] internal MouseInput m; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint type; internal InputUnion data; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int l, t, r, b; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int type, HookProc cb, IntPtr mod, uint thread);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint n, Input[] i, int s);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int k);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);

    internal static readonly UIntPtr Marker = new UIntPtr(0x50524F42);
    internal static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static long Ms { get { return Clock.ElapsedMilliseconds; } }

    internal sealed class Hook
    {
        internal readonly string Name; internal IntPtr Handle; internal long InstalledAt; internal int Installs;
        internal int F9Down, F9Up, AltDown, AltUp, OtherDown, F24Down, F24Up, InjectedSeen;
        internal bool SwallowF9;
        internal long FirstSeenAt = -1, LastSeenAt = -1;
        private readonly HookProc proc;
        internal Hook(string name) { Name = name; proc = Callback; }
        internal void Install()
        {
            if (Handle != IntPtr.Zero) UnhookWindowsHookEx(Handle);
            Handle = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); InstalledAt = Ms; Installs++;
        }
        internal void Remove() { if (Handle != IntPtr.Zero) { UnhookWindowsHookEx(Handle); Handle = IntPtr.Zero; } }
        private IntPtr Callback(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0)
            {
                KbData d = (KbData)Marshal.PtrToStructure(data, typeof(KbData));
                int k = msg.ToInt32(); bool down = k == 0x100 || k == 0x104, up = k == 0x101 || k == 0x105;
                if (FirstSeenAt < 0) FirstSeenAt = Ms; LastSeenAt = Ms;
                if ((d.flags & 0x10) != 0) InjectedSeen++;
                if (d.key == 0x78) { if (down) F9Down++; if (up) F9Up++; if (SwallowF9) return new IntPtr(1); }
                else if (d.key == 0xA4 || d.key == 0xA5 || d.key == 0x12) { if (down) AltDown++; if (up) AltUp++; }
                else if (d.key == 0x87) { if (down) F24Down++; if (up) F24Up++; if (d.extra == Marker) return new IntPtr(1); }
                else if (down) OtherDown++;
            }
            return CallNextHookEx(Handle, code, msg, data);
        }
        internal object Report()
        {
            return new { name = Name, installed = Handle != IntPtr.Zero, installs = Installs, installedAtMs = InstalledAt,
                f9Down = F9Down, f9Up = F9Up, altDown = AltDown, altUp = AltUp, otherKeyDown = OtherDown,
                f24Down = F24Down, f24Up = F24Up, injectedSeen = InjectedSeen, firstSeenMs = FirstSeenAt, lastSeenMs = LastSeenAt };
        }
    }

    internal static string ProcessOf(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        try { using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return "unknown"; }
    }
    internal static IntPtr FindToDeskSession()
    {
        IntPtr best = IntPtr.Zero; long area = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || ProcessOf(h) != "ToDesk") return true;
            StringBuilder t = new StringBuilder(256); GetWindowText(h, t, 256); if (t.Length == 0) return true;
            RECT r; GetWindowRect(h, out r); long a = (long)(r.r - r.l) * (r.b - r.t);
            if (a > area) { area = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
    internal static bool Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        SetForegroundWindow(h); if (GetForegroundWindow() == h) return true;
        uint ignored; uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out ignored), me = GetCurrentThreadId();
        bool at = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
        try { SetForegroundWindow(h); } finally { if (at) AttachThreadInput(me, fg, false); }
        Thread.Sleep(50); return GetForegroundWindow() == h;
    }
    internal static Input Key(ushort vk, ushort scan, uint flags)
    {
        Input i = new Input(); i.type = 1;
        i.data.k = new KeyboardInput { key = vk, scan = scan, flags = flags, extra = Marker }; return i;
    }
    internal static bool Send(params Input[] items) { return SendInput((uint)items.Length, items, Marshal.SizeOf(typeof(Input))) == items.Length; }
    internal static void Pump(int ms) { long end = Ms + ms; while (Ms < end) { Application.DoEvents(); Thread.Sleep(10); } }
    internal static bool AnyPhysicalModifier()
    { foreach (int k in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if ((GetAsyncKeyState(k) & 0x8000) != 0) return true; return false; }
    internal static void Write(string path, object value) { File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false)); }

    // 识别豆包 IME 的可见窗口（只取类名/尺寸，不读内容）
    internal static List<string> ImeWindows()
    {
        List<string> o = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            string p = ProcessOf(h); if (p != "ImeService" && !p.StartsWith("Doubao", StringComparison.OrdinalIgnoreCase)) return true;
            if (p == "Doubao") return true; // 豆包桌面版不是输入法
            StringBuilder c = new StringBuilder(128); GetClassName(h, c, 128); RECT r; GetWindowRect(h, out r);
            o.Add(p + "|" + c + "|" + (r.r - r.l) + "x" + (r.b - r.t) + "@" + r.l + "," + r.t);
            return true;
        }, IntPtr.Zero);
        o.Sort(); return o;
    }

    internal sealed class ProbeBox : TextBox
    {
        internal int StartComposition, EndComposition, CompositionMsgs, TextChanges;
        protected override void OnTextChanged(EventArgs e) { TextChanges++; base.OnTextChanged(e); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x010D) StartComposition++; if (m.Msg == 0x010E) EndComposition++; if (m.Msg == 0x010F) CompositionMsgs++;
            base.WndProc(ref m);
        }
    }
    internal sealed class ProbeForm : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF100) return; // 忽略 Alt 松开触发的系统菜单
            base.WndProc(ref m);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "chain") return Chain(args[1]);
            if (args.Length >= 6 && args[0] == "alt") return Alt(args[1], args[2] == "repeat1", int.Parse(args[3]), args[4], args[5]);
            if (args.Length >= 3 && args[0] == "watch") return Watch(int.Parse(args[1]), args[2]);
            if (args.Length >= 2 && args[0] == "altseq") return AltSequence(args[1]);
            if (args.Length >= 3 && args[0] == "altswitch") return AltSwitch(int.Parse(args[1]), args[2]);
            if (args.Length >= 3 && args[0] == "preswitch") return PreSwitch(int.Parse(args[1]), args[2]);
        }
        catch (Exception e) { try { File.WriteAllText(args[args.Length - 1] + ".error.txt", e.ToString()); } catch { } return 3; }
        return 2;
    }

    private static int Chain(string output)
    {
        IntPtr previous = GetForegroundWindow();
        IntPtr todesk = FindToDeskSession();
        if (todesk == IntPtr.Zero) { Write(output, new { error = "no ToDesk session window" }); return 1; }
        ProbeForm form = new ProbeForm { Text = "InputProbe chain", Width = 360, Height = 120, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        form.Controls.Add(new Label { Text = "输入诊断：几秒后自动关闭", Dock = DockStyle.Fill });
        form.Show(); Pump(200);
        var steps = new List<object>();
        bool probeFront = Activate(form.Handle); Pump(500);
        steps.Add(new { step = "probe-front", ok = probeFront, fg = ProcessOf(GetForegroundWindow()) });
        Hook early = new Hook("early(装在ToDesk失焦时)"); early.Install();
        bool tdFront = Activate(todesk); Pump(900);
        steps.Add(new { step = "todesk-front", ok = tdFront, fg = ProcessOf(GetForegroundWindow()) });
        object r1 = null, r2 = null;
        if (GetForegroundWindow() == todesk && !AnyPhysicalModifier())
        {
            Send(Key(0x87, 0, 0)); Pump(60); Send(Key(0x87, 0, 2)); Pump(400);
            r1 = new { afterInject1 = new[] { early.Report() } };
            Hook late = new Hook("late(装在ToDesk前台后)"); late.Install(); Pump(200);
            if (GetForegroundWindow() == todesk && !AnyPhysicalModifier())
            { Send(Key(0x87, 0, 0)); Pump(60); Send(Key(0x87, 0, 2)); Pump(400); }
            r2 = new { afterInject2 = new[] { early.Report(), late.Report() }, fg = ProcessOf(GetForegroundWindow()) };
            late.Remove();
        }
        early.Remove();
        form.Close();
        if (previous != IntPtr.Zero && GetForegroundWindow() != previous) Activate(previous);
        Write(output, new { mode = "chain", note = "F24 由本工具注入（带注入标记），用于比较钩子先后，不等同物理按键", steps, r1, r2 });
        return 0;
    }

    private static int Alt(string variant, bool repeat, int holdMs, string output, string shot)
    {
        IntPtr previous = GetForegroundWindow();
        ProbeForm form = new ProbeForm { Text = "InputProbe 豆包语音键测试", Width = 520, Height = 220, TopMost = true, StartPosition = FormStartPosition.CenterScreen, KeyPreview = true };
        ProbeBox box = new ProbeBox { Multiline = true, Dock = DockStyle.Fill, ImeMode = ImeMode.On, Font = new Font("Microsoft YaHei UI", 12) };
        form.Controls.Add(box);
        form.Show(); Pump(200);
        bool front = Activate(form.Handle); box.Focus(); Pump(400);
        var timeline = new List<object>();
        List<string> before = ImeWindows();
        if (!(GetForegroundWindow() == form.Handle && box.Focused) || AnyPhysicalModifier())
        {
            form.Close(); if (previous != IntPtr.Zero) Activate(previous);
            Write(output, new { mode = "alt", aborted = "probe not focused or modifier held", front }); return 1;
        }
        ushort scan = (ushort)MapVirtualKey(0xA4, 0);
        ushort vk = variant == "scan" ? (ushort)0 : (ushort)0xA4;
        ushort sc = variant == "vk" ? (ushort)0 : scan;
        uint flag = variant == "scan" ? 8u : 0u;
        long t0 = Ms; bool sent = Send(Key(vk, sc, flag));
        bool shotTaken = false; long lastRepeat = Ms; long lostFocusAt = -1;
        while (Ms - t0 < holdMs)
        {
            Pump(20);
            // 前台一旦离开本工具立即松开 Alt，避免与用户在其他窗口（如远控）里的按键组合。
            if (GetForegroundWindow() != form.Handle) { lostFocusAt = Ms - t0; break; }
            if (repeat && Ms - t0 > 500 && Ms - lastRepeat >= 33) { Send(Key(vk, sc, flag)); lastRepeat = Ms; }
            if ((Ms - t0) % 500 < 50) timeline.Add(new { t = Ms - t0, ime = ImeWindows(), focused = box.Focused, fg = GetForegroundWindow() == form.Handle });
            if (!shotTaken && Ms - t0 > 1500)
            {
                shotTaken = true;
                Rectangle area = Screen.FromHandle(form.Handle).Bounds;
                using (Bitmap b = new Bitmap(area.Width, area.Height))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    int w = Math.Min(1600, area.Width), h = area.Height * w / area.Width;
                    using (Bitmap s = new Bitmap(b, w, h)) s.Save(shot, ImageFormat.Png);
                }
            }
        }
        bool released = Send(Key(vk, sc, flag | 2));
        long tr = Ms;
        while (Ms - tr < 3000) { Pump(100); if ((Ms - tr) % 1000 < 100) timeline.Add(new { t = Ms - t0, ime = ImeWindows(), focused = box.Focused }); }
        object result = new { mode = "alt", variant, repeat, holdMs, scanCode = scan, sent, released, lostFocusAt,
            imeWindowsBefore = before, timeline,
            textLength = box.TextLength, textChanges = box.TextChanges,
            startComposition = box.StartComposition, endComposition = box.EndComposition, compositionMsgs = box.CompositionMsgs,
            altStillDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0 };
        box.Clear(); form.Close();
        if (previous != IntPtr.Zero) Activate(previous);
        Write(output, result); return 0;
    }

    private static int Watch(int seconds, string output)
    {
        Hook early = new Hook("early(启动时安装)") { SwallowF9 = true }; early.Install();
        Hook late = new Hook("late(每次ToDesk到前台后重装)") { SwallowF9 = true };
        Form overlay = new NoActivateForm { Text = "", FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
            BackColor = Color.FromArgb(20, 30, 30), ForeColor = Color.White, Width = 560, Height = 120, StartPosition = FormStartPosition.Manual };
        Label label = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 11), ForeColor = Color.White };
        overlay.Controls.Add(label);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea; overlay.Location = new Point(wa.Right - overlay.Width - 20, wa.Top + 20);
        var log = new List<object>();
        string lastFg = ""; long start = Ms, tdFrontSince = -1;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += delegate
        {
            string fg = ProcessOf(GetForegroundWindow());
            if (fg != lastFg) { log.Add(new { t = Ms - start, foreground = fg, early = early.F9Down, late = late.F9Down }); lastFg = fg; tdFrontSince = fg == "ToDesk" ? Ms : -1; }
            if (fg == "ToDesk" && tdFrontSince >= 0 && Ms - tdFrontSince > 400 && (late.Handle == IntPtr.Zero || Ms - late.InstalledAt > 3000)) late.Install();
            long left = seconds - (Ms - start) / 1000;
            label.Text = "输入诊断 · 已就绪 · 剩 " + left + " 秒 · 前台：" + fg + "\r\n" +
                "早装钩子  F9:" + early.F9Down + "  Alt:" + early.AltDown + "  其他键:" + early.OtherDown + "\r\n" +
                "晚装钩子  F9:" + late.F9Down + "  Alt:" + late.AltDown + "  其他键:" + late.OtherDown + "  (重装" + late.Installs + "次)\r\n" +
                "期间 F9 被本工具拦下，不会触发旧程序";
            if (left <= 0) overlay.Close();
        };
        overlay.Shown += delegate { timer.Start(); };
        Application.Run(overlay);
        timer.Stop(); early.Remove(); late.Remove();
        Write(output, new { mode = "watch", seconds, hooks = new[] { early.Report(), late.Report() }, foregroundLog = log });
        return 0;
    }
    // 用户点“开始”后，依次用三种方式模拟按住左 Alt，看豆包是否开始听写。只记字数，不记正文。
    // 任何真实按键或前台变化都会立即松开 Alt 并中止。
    private static int AltSequence(string output)
    {
        bool physicalKey = false;
        HookProc guardProc = delegate(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0)
            {
                KbData d = (KbData)Marshal.PtrToStructure(data, typeof(KbData));
                int k = msg.ToInt32();
                if ((d.flags & 0x10) == 0 && (k == 0x100 || k == 0x104)) physicalKey = true;
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        };
        ProbeForm form = new ProbeForm { Text = "豆包语音键测试（诊断工具）", Width = 640, Height = 380, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Label label = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold),
            Text = "准备好后用鼠标点下面的“开始”。\r\n测试约 40 秒，期间不要碰键盘和鼠标。\r\n看到“请说话”时，说一句：今天天气不错。" };
        ProbeBox box = new ProbeBox { Multiline = true, Dock = DockStyle.Fill, ImeMode = ImeMode.On, Font = new Font("Microsoft YaHei UI", 12) };
        Button start = new Button { Text = "开始", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 12) };
        layout.Controls.Add(label); layout.Controls.Add(box); layout.Controls.Add(start); form.Controls.Add(layout);
        var results = new List<object>(); var parts = new List<string>();
        bool started = false;
        start.Click += delegate
        {
            if (started) return; started = true; start.Enabled = false;
            IntPtr guard = SetWindowsHookEx(13, guardProc, GetModuleHandle(null), 0);
            try
            {
                string[][] variants = { new[] { "vk", "0" }, new[] { "vkscan", "1" }, new[] { "scan", "1" } };
                ushort scan = (ushort)MapVirtualKey(0xA4, 0);
                for (int i = 0; i < variants.Length; i++)
                {
                    string variant = variants[i][0]; bool repeat = variants[i][1] == "1";
                    box.Clear(); int changes0 = box.TextChanges, comp0 = box.StartComposition;
                    for (int c = 3; c > 0; c--) { label.Text = "第 " + (i + 1) + "/3 次，" + c + " 秒后开始，请别碰键盘鼠标…"; Pump(1000); }
                    if (!Activate(form.Handle)) { } box.Focus(); Pump(200);
                    physicalKey = false;
                    if (GetForegroundWindow() != form.Handle || !box.Focused || AnyPhysicalModifier())
                    { results.Add(new { variant, aborted = "not focused before start" }); break; }
                    ushort vk = variant == "scan" ? (ushort)0 : (ushort)0xA4;
                    ushort sc = variant == "vk" ? (ushort)0 : scan;
                    uint flag = variant == "scan" ? 8u : 0u;
                    var ime = new List<object>(); string abort = null;
                    long t0 = Ms, lastRepeat = Ms, lastSample = -1000;
                    Send(Key(vk, sc, flag));
                    label.Text = "第 " + (i + 1) + "/3 次：请说话（今天天气不错）…";
                    while (Ms - t0 < 5000)
                    {
                        Pump(20);
                        if (GetForegroundWindow() != form.Handle) { abort = "foreground changed"; break; }
                        if (physicalKey) { abort = "physical key pressed"; break; }
                        if (repeat && Ms - t0 > 500 && Ms - lastRepeat >= 33) { Send(Key(vk, sc, flag)); lastRepeat = Ms; }
                        if (Ms - lastSample >= 500) { lastSample = Ms; ime.Add(new { t = Ms - t0, windows = ImeWindows() }); }
                    }
                    Send(Key(vk, sc, flag | 2));
                    label.Text = "第 " + (i + 1) + "/3 次：已松开，等待出字…";
                    long tr = Ms; while (Ms - tr < 3500) { Pump(50); if (Ms - lastSample >= 1000) { lastSample = Ms; ime.Add(new { t = Ms - t0, windows = ImeWindows() }); } }
                    results.Add(new { variant, repeat, scanCode = sc, abort, textLength = box.TextLength,
                        textChanges = box.TextChanges - changes0, startComposition = box.StartComposition - comp0, imeWindows = ime });
                    parts.Add(variant + "：" + (abort ?? ("出字 " + box.TextLength + " 个")));
                    if (abort != null) break;
                }
            }
            finally { if (guard != IntPtr.Zero) UnhookWindowsHookEx(guard); }
            StringBuilder summary = new StringBuilder("测试结束（窗口 10 秒后自动关闭）：\r\n");
            summary.Append(string.Join("   ", parts.ToArray()));
            box.Clear(); label.Text = summary.ToString();
            Write(output, new { mode = "altseq", results });
            System.Windows.Forms.Timer close = new System.Windows.Forms.Timer { Interval = 10000 };
            close.Tick += delegate { close.Stop(); form.Close(); }; close.Start();
        };
        form.FormClosed += delegate { if (!started) Write(output, new { mode = "altseq", results = "not started" }); };
        Application.Run(form);
        return 0;
    }

    // 不模拟任何按键：ToDesk 前台时用户真实按住左 Alt（单独按住 ≥250ms），本工具把前台切到自己的输入框，
    // 让用户手指仍按着的 Alt 的后续自动重复由豆包看到。松开后只记字数，然后把前台还给 ToDesk。不粘贴。
    private static int AltSwitch(int seconds, string output)
    {
        bool held = false, alone = false; long downAt = 0, upAt = -1;
        Hook late = null;
        HookProc proc = delegate(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0)
            {
                KbData d = (KbData)Marshal.PtrToStructure(data, typeof(KbData));
                int k = msg.ToInt32(); bool down = k == 0x100 || k == 0x104, up = k == 0x101 || k == 0x105;
                if ((d.flags & 0x10) == 0)
                {
                    if (d.key == 0xA4) { if (down && !held) { held = true; alone = true; downAt = Ms; } if (up && held) { held = false; upAt = Ms; } }
                    else if (down && held) alone = false;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        };
        IntPtr hook = IntPtr.Zero; long hookAt = -100000;
        ProbeForm form = new ProbeForm { Text = "豆包测试框（诊断工具）", Width = 520, Height = 200, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        ProbeBox box = new ProbeBox { Multiline = true, Dock = DockStyle.Fill, ImeMode = ImeMode.On, Font = new Font("Microsoft YaHei UI", 12) };
        Label tip = new Label { Dock = DockStyle.Top, Height = 32, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Text = "继续按住左 Alt 说话，说完松开" };
        form.Controls.Add(box); form.Controls.Add(tip);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea; form.Location = new Point(wa.Left + (wa.Width - form.Width) / 2, wa.Bottom - form.Height - 40);
        Form overlay = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
            BackColor = Color.FromArgb(20, 30, 30), Width = 560, Height = 100, StartPosition = FormStartPosition.Manual };
        Label status = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 11), ForeColor = Color.White };
        overlay.Controls.Add(status); overlay.Location = new Point(wa.Right - overlay.Width - 20, wa.Top + 20);
        var trials = new List<object>(); string last = "还没有测试";
        IntPtr todesk = IntPtr.Zero; bool switched = false; long switchAt = 0, finishAt = -1; int comp0 = 0, changes0 = 0;
        long start = Ms;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += delegate
        {
            IntPtr fg = GetForegroundWindow(); string fgName = ProcessOf(fg);
            if (fgName == "ToDesk" && Ms - hookAt > 3000) { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); hookAt = Ms; }
            if (!switched && held && alone && Ms - downAt >= 250 && fgName == "ToDesk")
            {
                todesk = fg; switched = true; switchAt = Ms; box.Clear(); comp0 = box.StartComposition; changes0 = box.TextChanges;
                form.Show(); Activate(form.Handle); box.Focus();
            }
            if (switched && finishAt < 0 && !held) finishAt = Ms + 3500;
            if (switched && finishAt > 0 && Ms >= finishAt)
            {
                trials.Add(new { focusSwitchedAfterMs = switchAt - downAt, focused = GetForegroundWindow() == form.Handle, heldMs = upAt - downAt,
                    textLength = box.TextLength, textChanges = box.TextChanges - changes0, startComposition = box.StartComposition - comp0 });
                last = "第 " + trials.Count + " 次：出字 " + box.TextLength + " 个（按住 " + (upAt - downAt) + "ms）";
                box.Clear(); form.Hide(); if (todesk != IntPtr.Zero) Activate(todesk);
                switched = false; finishAt = -1;
            }
            long left = seconds - (Ms - start) / 1000;
            status.Text = "Alt 测试已就绪 · 剩 " + left + " 秒 · 前台：" + fgName + "\r\n在 ToDesk 画面里按住左 Alt 说话，说完松开\r\n" + last;
            if (left <= 0 && !switched) overlay.Close();
        };
        overlay.Shown += delegate { timer.Start(); };
        Application.Run(overlay);
        timer.Stop(); if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        Write(output, new { mode = "altswitch", trials });
        return 0;
    }

    // 不注入按键：ToDesk 前台时用户真实按下左 Alt 的瞬间（钩子回调内、这次按下送达之前），把前台切到本工具输入框，
    // 然后放行这次按下，看豆包能否用这“同一次”按下开始听写。松开 4 秒后记字数并回到 ToDesk。不粘贴。
    private static int PreSwitch(int seconds, string output)
    {
        var events = new List<object>(); var trials = new List<object>();
        long start = Ms; bool held = false; long downAt = 0, endAt = -1; IntPtr todesk = IntPtr.Zero; bool switchedOk = false;
        IntPtr hook = IntPtr.Zero; long hookAt = -100000; string lastFg = ""; long tdSince = -1;
        ProbeForm form = new ProbeForm { Text = "豆包测试框（诊断工具）", Width = 520, Height = 170, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        ProbeBox box = new ProbeBox { Multiline = true, Dock = DockStyle.Fill, ImeMode = ImeMode.On, Font = new Font("Microsoft YaHei UI", 12) };
        form.Controls.Add(box);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea; form.Location = new Point(wa.Left + (wa.Width - form.Width) / 2, wa.Bottom - form.Height - 40);
        Form overlay = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
            BackColor = Color.FromArgb(20, 30, 30), Width = 560, Height = 100, StartPosition = FormStartPosition.Manual };
        Label status = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 11), ForeColor = Color.White };
        overlay.Controls.Add(status); overlay.Location = new Point(wa.Right - overlay.Width - 20, wa.Top + 20);
        string last = "还没有测试"; int comp0 = 0;
        IntPtr created = form.Handle; created = box.Handle; // 预先建好窗口，钩子回调里只做切前台
        HookProc proc = null;
        proc = delegate(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0)
            {
                KbData d = (KbData)Marshal.PtrToStructure(data, typeof(KbData));
                int k = msg.ToInt32(); bool down = k == 0x100 || k == 0x104, up = k == 0x101 || k == 0x105;
                if ((d.flags & 0x10) == 0 && d.key == 0xA4)
                {
                    if (down && !held && endAt < 0)
                    {
                        IntPtr fg = GetForegroundWindow();
                        if (ProcessOf(fg) == "ToDesk")
                        {
                            held = true; downAt = Ms; todesk = fg; box.Clear(); comp0 = box.StartComposition;
                            if (!form.Visible) form.Show();
                            switchedOk = Activate(form.Handle); box.Focus();
                            events.Add(new { t = Ms - start, e = "alt-down-preswitch", ok = switchedOk, cost = Ms - downAt });
                        }
                    }
                    else if (up && held) { held = false; endAt = Ms + 4000; events.Add(new { t = Ms - start, e = "alt-up", heldMs = Ms - downAt }); }
                    else if (down) events.Add(new { t = Ms - start, e = "alt-down-other" });
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        };
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += delegate
        {
            string fgName = ProcessOf(GetForegroundWindow());
            if (fgName != lastFg) { lastFg = fgName; tdSince = fgName == "ToDesk" ? Ms : -1; }
            if (!held && fgName == "ToDesk" && tdSince >= 0 && Ms - tdSince > 300 && (hookAt < tdSince || Ms - hookAt > 3000))
            { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); hookAt = Ms; }
            if (endAt > 0 && Ms >= endAt)
            {
                trials.Add(new { heldMs = endAt - 4000 - downAt, switchedOk, textLength = box.TextLength, textChanges = box.TextChanges, startComposition = box.StartComposition - comp0 });
                last = "第 " + trials.Count + " 次：出字 " + box.TextLength + " 个";
                box.Clear(); form.Hide(); if (todesk != IntPtr.Zero) Activate(todesk); endAt = -1;
            }
            long left = seconds - (Ms - start) / 1000;
            status.Text = "单次按住测试 · 剩 " + left + " 秒 · 前台：" + fgName + "\r\n在 ToDesk 画面里只按一次：按住左 Alt 说话，说完松开\r\n" + last;
            if (left <= 0 && !held && endAt < 0) overlay.Close();
        };
        overlay.Shown += delegate { timer.Start(); };
        Application.Run(overlay);
        timer.Stop(); if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        Write(output, new { mode = "preswitch", trials, events });
        GC.KeepAlive(proc);
        return 0;
    }

    internal sealed class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000008 | 0x00000080; return p; } }
    }
}
