using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal static class HoldClock
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        internal static double Now { get { return clock.Elapsed.TotalMilliseconds; } }
    }

    // 诊断记录：只记时间、按键码、窗口类名和字数，不记听写正文。
    internal static class HoldTrace
    {
        private static readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        private static string path;
        internal static bool Enabled { get { return path != null; } }
        internal static void Init(string file) { path = file; Add("trace start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
        internal static void Add(string text) { if (path != null && queue.Count < 5000) queue.Enqueue(HoldClock.Now.ToString("F0") + " " + text); }
        internal static void Flush()
        {
            if (path == null || queue.IsEmpty) return;
            StringBuilder lines = new StringBuilder(); string line;
            while (queue.TryDequeue(out line)) lines.AppendLine(line);
            try { if (!File.Exists(path) || new FileInfo(path).Length < 4 * 1024 * 1024) File.AppendAllText(path, lines.ToString(), new UTF8Encoding(false)); }
            catch { }
        }
    }

    // 低级键盘/鼠标监听跑在独立线程上。
    // 实测 1：ToDesk 每次到前台都会装自己的低级键盘钩子并吞掉物理按键；只有比它晚装的钩子能看到。
    //         所以远控窗口在前台时，本监听会延迟重装一次，并每 3 秒补装一次，保持排在最前。
    // 实测 2：按住 Alt 不会自动重复，整个按住过程只有一次“按下”。豆包只认真实按键，
    //         所以必须在这次“按下”送达之前（钩子回调里）就把前台切到本机听写框，再放行它。
    internal sealed class RemoteHoldHook : IDisposable
    {
        private readonly Native.KeyboardProc keyboardCallback, mouseCallback;
        private readonly object gate = new object();
        private readonly HoldSnapshot state = new HoldSnapshot();
        // 进程号会被系统复用，缓存 5 秒后重新判断。
        private readonly Dictionary<uint, KeyValuePair<bool, double>> remotePids = new Dictionary<uint, KeyValuePair<bool, double>>();
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread thread;
        private IntPtr keyboardHook, mouseHook;
        private volatile bool stopping;
        private volatile int voiceKey = 0xA4;
        private string startError;
        private IntPtr lastForeground;
        private bool lastForegroundRemote, downRemote;
        private double remoteSince = -1, lastInstall;
        private int installs;
        internal volatile bool Armed;
        // 由控制器提供：同步切到本机听写框（返回是否已切过去）；Alt+其他键时撤回并切回远控。
        internal Func<IntPtr, int, bool> PreSwitch;
        internal Func<bool> ComboAbort;
        internal int Installs { get { return installs; } }
        internal int VoiceKey { get { return voiceKey; } set { voiceKey = value; lock (gate) { state.Held = false; state.Alone = false; } } }

        internal RemoteHoldHook() { keyboardCallback = Keyboard; mouseCallback = Mouse; }

        internal void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "RemoteHoldHook" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            ready.WaitOne(3000);
            if (startError != null) throw new InvalidOperationException(startError);
        }
        internal HoldSnapshot Snapshot()
        {
            lock (gate)
                return new HoldSnapshot { Held = state.Held, Alone = state.Alone, DownAt = state.DownAt, UpAt = state.UpAt,
                    DownForeground = state.DownForeground, PressSerial = state.PressSerial, EscapeSerial = state.EscapeSerial };
        }
        private void Run()
        {
            if (!Install()) { startError = "按键监听未能启用，仍可打开控制面板手动粘贴。"; ready.Set(); return; }
            ready.Set();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 })
            {
                timer.Tick += delegate { Maintain(); };
                timer.Start();
                Application.Run();
            }
            Remove();
        }
        private bool Install()
        {
            Remove();
            IntPtr module = Native.GetModuleHandle(null);
            keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            lastInstall = HoldClock.Now; installs++;
            return keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
        }
        private void Remove()
        {
            if (keyboardHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        }
        private void Maintain()
        {
            if (stopping) { Application.ExitThread(); return; }
            double now = HoldClock.Now;
            IntPtr foreground = Native.GetForegroundWindow();
            bool remote = IsRemote(foreground);
            if (foreground != lastForeground) { lastForeground = foreground; remoteSince = remote ? now : -1; }
            lastForegroundRemote = remote;
            bool reinstall = remote && remoteSince >= 0 && now - remoteSince >= 300 && (lastInstall < remoteSince + 300 || now - lastInstall >= 3000);
            // 按住语音键期间不重装，避免漏掉抬起事件。
            bool holding; lock (gate) holding = state.Held;
            if (!holding && (reinstall || now - lastInstall >= 30000)) Install();
        }
        internal bool IsRemote(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid; Native.GetWindowThreadProcessId(Native.GetAncestor(hwnd, 2), out pid);
            double now = HoldClock.Now; KeyValuePair<bool, double> cached;
            lock (remotePids) if (remotePids.TryGetValue(pid, out cached) && now - cached.Value < 5000) return cached.Key;
            bool known;
            try { using (Process process = Process.GetProcessById((int)pid)) known = RemoteWindow.IsSupportedProcess(process.ProcessName); }
            catch { known = false; }
            lock (remotePids) { if (remotePids.Count > 256) remotePids.Clear(); remotePids[pid] = new KeyValuePair<bool, double>(known, now); }
            return known;
        }
        private bool ForegroundIsRemote(IntPtr foreground)
        { return foreground == lastForeground ? lastForegroundRemote : IsRemote(foreground); }
        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0)
                {
                    Native.KeyboardData value = (Native.KeyboardData)Marshal.PtrToStructure(data, typeof(Native.KeyboardData));
                    if ((value.flags & 0x10) == 0 && value.extra != Native.InputMarker)
                    {
                        int kind = message.ToInt32(); bool down = kind == 0x100 || kind == 0x104, up = kind == 0x101 || kind == 0x105;
                        bool requestSwitch = false, requestCombo = false; int serial = 0; IntPtr foreground = IntPtr.Zero; double downAt = 0;
                        lock (gate)
                        {
                            if (value.key == voiceKey)
                            {
                                if (down && !state.Held)
                                {
                                    foreground = Native.GetForegroundWindow();
                                    state.Held = true; state.Alone = true; state.DownAt = HoldClock.Now;
                                    state.DownForeground = foreground; serial = ++state.PressSerial;
                                    requestSwitch = Armed && ForegroundIsRemote(foreground);
                                    downRemote = requestSwitch;
                                }
                                else if (up && state.Held) { state.Held = false; state.UpAt = HoldClock.Now; HoldTrace.Add("hook up heldMs=" + (state.UpAt - state.DownAt).ToString("F0")); }
                            }
                            else if (down)
                            {
                                if (state.Held)
                                {
                                    state.Alone = false;
                                    // 不依赖切换是否“按时”完成：只要这次按下发生在远控前台，就交给界面判断要不要撤回。
                                    requestCombo = downRemote && HoldClock.Now - state.DownAt < 2500;
                                    downAt = state.DownAt;
                                }
                                if (value.key == 0x1B) state.EscapeSerial++;
                            }
                        }
                        if (requestSwitch && PreSwitch != null)
                        {
                            double started = HoldClock.Now;
                            bool switched = PreSwitch(foreground, serial);
                            HoldTrace.Add("hook down preSwitch=" + (switched ? 1 : 0) + " cost=" + (HoldClock.Now - started).ToString("F0"));
                        }
                        if (requestCombo && ComboAbort != null && ComboAbort())
                        {
                            // Alt+其他键是给 Mac 的 Option 组合：已切回远控，按原顺序补发 Alt 按下和这个键，吞掉原始这一下。
                            downRemote = false;
                            Native.Replay(Native.Key((ushort)voiceKey, false), Native.RawKey(value.key, value.scanCode, (value.flags & 1) != 0, false));
                            HoldTrace.Add("hook combo-replay key=" + value.key.ToString("X"));
                            return new IntPtr(1);
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(keyboardHook, code, message, data);
        }
        private IntPtr Mouse(int code, IntPtr message, IntPtr data)
        {
            try
            {
                int kind = message.ToInt32();
                if (code >= 0 && (kind == 0x201 || kind == 0x204 || kind == 0x207 || kind == 0x20B || kind == 0x20A))
                    lock (gate) if (state.Held) state.Alone = false;
            }
            catch { }
            return Native.CallNextHookEx(mouseHook, code, message, data);
        }
        public void Dispose()
        {
            stopping = true; Armed = false;
            if (thread != null && thread.IsAlive) thread.Join(1000);
            if (thread == null || !thread.IsAlive) Remove();
        }
    }

    internal sealed class OneKeyController : IDisposable
    {
        private const int SwitchBudgetMilliseconds = 200;
        private readonly HoldSession session = new HoldSession();
        private readonly RemoteHoldHook hook;
        private readonly Func<HoldSnapshot> snapshotSource;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Control owner;
        internal readonly DictationFloat Surface = new DictationFloat();
        private readonly Func<OneKeyOptions> optionsFactory;
        private readonly Func<bool> canBegin;
        private readonly Func<string, RemoteWindow, OneKeyOptions, CancellationToken, Task> deliver;
        private readonly bool testMode;
        private CancellationTokenSource cancellation;
        private RemoteWindow target;
        private OneKeyOptions options;
        private int textVersion, lastTriggeredSerial, lastEscapeSerial, triggers;
        private double changedAt, lastImeScan;
        private volatile bool disposing;
        private bool enabled, composeLogged, textLogged;
        private string lastImeWindows = "";
        internal event Action<bool> ActivityChanged;
        internal event Action<string, bool> StatusChanged;
        internal event Action<RemoteWindow> TargetCaptured;
        internal bool Busy { get { return session.Active; } }
        internal int Triggers { get { return triggers; } }
        internal int HookInstalls { get { return hook == null ? 0 : hook.Installs; } }
        internal bool Enabled { get { return enabled; } set { if (!value) Cancel("远控按住听写已关闭。"); enabled = value; if (hook != null) hook.Armed = value; } }
        internal int VoiceKey { set { if (hook != null) hook.VoiceKey = value; } }

        internal OneKeyController(Control owner, Func<OneKeyOptions> optionsFactory, Func<bool> canBegin,
            Func<string, RemoteWindow, OneKeyOptions, CancellationToken, Task> deliver, Func<HoldSnapshot> testSnapshots = null)
        {
            this.owner = owner; this.optionsFactory = optionsFactory; this.canBegin = canBegin; this.deliver = deliver;
            testMode = testSnapshots != null;
            if (!testMode)
            {
                hook = new RemoteHoldHook(); snapshotSource = hook.Snapshot;
                hook.PreSwitch = delegate(IntPtr foreground, int serial)
                {
                    double deadline = HoldClock.Now + SwitchBudgetMilliseconds;
                    return RunOnUi(delegate { return HoldClock.Now <= deadline && PreSwitch(foreground, serial); });
                };
                hook.ComboAbort = delegate { return RunOnUi(ComboAbort); };
            }
            else snapshotSource = testSnapshots;
            Surface.Editor.TextChanged += delegate { textVersion++; changedAt = HoldClock.Now; };
            Surface.Editor.CompositionFinished += delegate { changedAt = HoldClock.Now; };
            Surface.PasteRequested += ManualPaste;
            Surface.CancelRequested += delegate { Cancel("已取消自动粘贴。文字仍保留。"); if (Surface.Editor.TextLength == 0) Surface.Hide(); };
            Surface.DiscardRequested += delegate { Cancel("已丢弃本次听写。"); if (!session.Active) { Surface.Editor.Clear(); session.Reset(); Surface.Hide(); } };
            timer.Interval = 40; timer.Tick += Tick; timer.Start();
        }
        internal void Start(bool diagnosticLog)
        {
            if (testMode) throw new InvalidOperationException("隔离测试不会安装全局监听。");
            if (diagnosticLog) HoldTrace.Init(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hold-trace.log"));
            IntPtr created = Surface.Handle; created = Surface.Editor.Handle; // 预先建好窗口，按下时只需显示和切前台
            hook.Start(); enabled = true; hook.Armed = true;
        }
        // 在界面线程上同步执行，最多等 SwitchBudgetMilliseconds；超时就放弃（这次按下照常给远控）。
        private bool RunOnUi(Func<bool> action)
        {
            if (disposing || owner.IsDisposed || !owner.IsHandleCreated) return false;
            ManualResetEvent done = new ManualResetEvent(false); bool result = false;
            try { owner.BeginInvoke(new Action(delegate { try { if (!disposing) result = action(); } catch { } finally { try { done.Set(); } catch (ObjectDisposedException) { } } })); }
            catch { done.Close(); return false; }
            bool finished = done.WaitOne(SwitchBudgetMilliseconds);
            done.Close();
            return finished && result;
        }
        private bool PreSwitch(IntPtr foreground, int serial)
        {
            if (!enabled || session.Active || Native.GetForegroundWindow() != foreground || !IsRemote(foreground)) return false;
            lastTriggeredSerial = serial;
            return Begin(foreground);
        }
        // Alt+其他键：豆包还没开始，就撤回本次听写并切回远控，由监听补发组合键。
        private bool ComboAbort()
        {
            if (session.Phase != HoldPhase.Listening || Surface.Editor.Composing || textVersion != session.StartingTextVersion) return false;
            Abandon(true, false);
            return target != null && Native.GetForegroundWindow() == target.Handle;
        }
        private void TraceImeWindows()
        {
            if (testMode || !HoldTrace.Enabled) return;
            List<string> windows = new List<string>();
            uint ime = 0;
            foreach (Process process in Process.GetProcessesByName("ImeService")) { ime = (uint)process.Id; process.Dispose(); }
            if (ime == 0) return;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == ime && Native.IsWindowVisible(hwnd))
                {
                    StringBuilder kind = new StringBuilder(64); Native.GetClassName(hwnd, kind, 64);
                    Native.RECT r; Native.GetWindowRect(hwnd, out r);
                    windows.Add(kind + ":" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
                }
                return true;
            }, IntPtr.Zero);
            windows.Sort(); string joined = string.Join(",", windows.ToArray());
            if (joined != lastImeWindows) { lastImeWindows = joined; HoldTrace.Add("ime-windows " + joined); }
        }
        internal HoldPhase TestPhase { get { return session.Phase; } }
        internal string TestDiagnostics
        { get { return "phase=" + session.Phase + "; focused=" + Surface.Ready + "; composing=" + Surface.Editor.Composing + "; everComposed=" + session.EverComposed +
            "; version=" + textVersion + "/" + session.StartingTextVersion + "; triggers=" + triggers; } }

        private bool IsRemote(IntPtr hwnd)
        {
            if (testMode) return RemoteWindow.Capture(hwnd, true) != null;
            return hook.IsRemote(hwnd);
        }
        private void Announce(string message, bool error)
        { if (StatusChanged != null) StatusChanged(message, error); }

        private void Tick(object sender, EventArgs e)
        {
            if (disposing) return;
            HoldTrace.Flush();
            try
            {
                HoldSnapshot input = snapshotSource();
                double now = HoldClock.Now;
                if (session.Active && now - lastImeScan >= 150) { lastImeScan = now; TraceImeWindows(); }
                if (input.EscapeSerial != lastEscapeSerial)
                {
                    lastEscapeSerial = input.EscapeSerial;
                    if (session.Phase == HoldPhase.Listening || session.Phase == HoldPhase.Finalizing) { Cancel("已取消自动粘贴。文字仍保留。"); return; }
                }
                if (!session.Active)
                {
                    // 真实环境只由监听在按下瞬间同步切换；超时就不再补切（按下已给了远控，补切会让远端 Option 卡住）。
                    // 隔离测试没有监听，用这里模拟触发。
                    if (!testMode || !enabled || input.PressSerial == lastTriggeredSerial) return;
                    IntPtr foreground = Native.GetForegroundWindow();
                    if (!HoldPolicy.ShouldTrigger(input.Held, input.Alone, IsRemote(foreground), foreground == input.DownForeground, true)) return;
                    lastTriggeredSerial = input.PressSerial;
                    HoldTrace.Add("ui fallback-trigger");
                    Begin(foreground);
                    return;
                }
                if (session.Phase == HoldPhase.Delivering) return;
                if (target == null || !target.IsAlive) { Fail("原远控窗口已关闭，听写已取消。"); return; }
                bool nothingYet = !session.EverComposed && !Surface.Editor.Composing && textVersion == session.StartingTextVersion;
                if (Surface.Editor.Composing) { session.EverComposed = true; if (!composeLogged) { composeLogged = true; HoldTrace.Add("ui composition-started"); } }
                if (!textLogged && textVersion > session.StartingTextVersion) { textLogged = true; HoldTrace.Add("ui first-text len=" + Surface.Editor.TextLength); }
                if (session.Phase == HoldPhase.Listening)
                {
                    // 按住时点了鼠标或按了别的键，且豆包还没开始：当作不是听写，安静撤回。
                    if (!input.Alone && nothingYet) { Abandon(Surface.Ready, false); return; }
                    if (!Surface.Ready && now - session.StartedAt > HoldPolicy.FocusGraceMilliseconds)
                    { if (nothingYet) Abandon(false, false); else Fail("本机听写框的焦点被切走，已停止自动粘贴。文字仍保留。"); return; }
                    if (now - session.StartedAt >= HoldPolicy.MaximumHoldMilliseconds) { Fail("单次听写达到 5 分钟，请确认文字后粘贴。"); return; }
                    if (HoldPolicy.Released(input.Held, now - input.UpAt))
                    {
                        // 只是点了一下 Alt：立刻回到远控。
                        if (nothingYet && input.UpAt - input.DownAt < HoldPolicy.TapMilliseconds) { Abandon(true, false); return; }
                        if (session.Release(now)) { Surface.Organizing(); HoldTrace.Add("ui released heldMs=" + (input.UpAt - input.DownAt).ToString("F0")); }
                    }
                    return;
                }
                if (!Surface.Ready && now - session.StartedAt > HoldPolicy.FocusGraceMilliseconds)
                { Fail("本机听写框的焦点被切走，已停止自动粘贴。文字仍保留。"); return; }
                if (session.Phase == HoldPhase.Finalizing)
                {
                    // 又按住了语音键：豆包会继续往本机听写框里追加，松开后再一起判断。
                    if (input.Held) return;
                    FinishDecision decision = HoldPolicy.Decide(session.EverComposed, Surface.Editor.Composing, Surface.Editor.Text,
                        textVersion > session.StartingTextVersion, now - session.ReleasedAt, now - changedAt);
                    if (decision != FinishDecision.Wait) HoldTrace.Add("ui decision=" + decision + " len=" + Surface.Editor.TextLength + " composed=" + session.EverComposed);
                    if (decision == FinishDecision.Deliver && !Native.ModifiersHeld() && session.TryDeliver(session.Generation)) SendCurrent();
                    else if (decision == FinishDecision.Abandon) Abandon(true, true);
                    else if (decision == FinishDecision.Timeout) Fail("豆包还没给出最终文字，已停止自动粘贴。文字仍保留，可确认后手动粘贴。");
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }
        private bool Begin(IntPtr foreground)
        {
            if (disposing || !canBegin()) return false;
            if (session.Phase == HoldPhase.Failed && Surface.Editor.TextLength > 0)
            { Surface.Review("上一次文字仍保留，请粘贴、手动编辑或丢弃后再开始。"); return false; }
            RemoteWindow destination = RemoteWindow.Capture(foreground, testMode);
            if (destination == null || !destination.IsAlive) return false;
            try { options = optionsFactory(); }
            catch (Exception error) { Announce(error.Message, true); return false; }
            target = destination; triggers++;
            if (!testMode) Native.SetProp(owner.Handle, "RemoteDictation.HoldTriggers", new IntPtr(triggers));
            session.Begin(HoldClock.Now, textVersion);
            composeLogged = false; textLogged = false; lastImeWindows = "";
            HoldTrace.Add("ui trigger provider=" + target.ProviderName);
            Surface.Prepare(target.ProviderName);
            if (TargetCaptured != null) TargetCaptured(target);
            if (ActivityChanged != null) ActivityChanged(true);
            return Native.GetForegroundWindow() == Surface.Handle;
        }
        // 豆包没有开始听写（只是点了一下 Alt、Alt+其他键、按住时点鼠标）：不留痕迹地回到远控窗口。
        private void Abandon(bool returnFocus, bool announce)
        {
            bool ownFocus = Surface.Visible && Native.GetForegroundWindow() == Surface.Handle;
            session.Reset(); Surface.Editor.Clear(); Surface.Hide();
            if (returnFocus && ownFocus && target != null && target.IsAlive) { try { target.Activate(); } catch { } }
            HoldTrace.Add("ui abandon");
            if (announce) Announce("没有识别到文字，已回到远控窗口。", false);
            if (ActivityChanged != null) ActivityChanged(false);
        }
        private async void SendCurrent()
        {
            string text = Surface.Editor.Text;
            RemoteWindow destination = target; OneKeyOptions frozen = options;
            cancellation = new CancellationTokenSource();
            CancellationTokenSource current = cancellation;
            Surface.Editor.ReadOnly = true; Surface.Hide();
            try
            {
                await deliver(text, destination, frozen, current.Token);
                session.Reset(); Surface.Editor.Clear();
                Announce("已向原远控窗口发出粘贴指令，请在远端检查。", false);
            }
            catch (Exception error)
            {
                session.Fail();
                if (!disposing) Surface.Review(error is OperationCanceledException ? "本次粘贴已取消，文字仍保留。" : error.Message);
                Announce("自动粘贴未完成，文字仍保留在本机。", true);
            }
            finally
            {
                current.Dispose(); cancellation = null; Surface.Editor.ReadOnly = false;
                if (ActivityChanged != null) ActivityChanged(false);
            }
        }
        private void ManualPaste()
        {
            if (Busy || target == null || options == null) return;
            if (Surface.Editor.Composing || string.IsNullOrWhiteSpace(Surface.Editor.Text))
            { Surface.Status("请等文字完整上屏后再粘贴。", true, true); return; }
            session.BeginManualDelivery(); if (ActivityChanged != null) ActivityChanged(true); SendCurrent();
        }
        private void Fail(string message)
        {
            bool wasActive = session.Active;
            session.Fail();
            Announce(message, true);
            if (!disposing) Surface.Review(message);
            if (wasActive && ActivityChanged != null) ActivityChanged(false);
        }
        internal void Cancel(string message)
        {
            if (disposing) return;
            if (session.Phase == HoldPhase.Delivering) { if (cancellation != null) cancellation.Cancel(); return; }
            if (!session.Active) return;
            Fail(message);
        }
        internal void ReviewPending()
        { if (Surface.Editor.TextLength > 0 && !session.Active) Surface.Review("上一次本机听写文字。确认后可重新粘贴。"); }
        internal void HideForSettings()
        {
            Cancel("已切换到控制面板，文字仍保留。");
            if (session.Phase != HoldPhase.Delivering) Surface.Hide();
        }
        public void Dispose()
        {
            if (disposing) return;
            disposing = true; timer.Stop();
            if (hook != null) hook.Dispose();
            if (cancellation != null) cancellation.Cancel();
            HoldTrace.Flush();
            timer.Dispose(); Surface.Shutdown();
        }
    }
}
