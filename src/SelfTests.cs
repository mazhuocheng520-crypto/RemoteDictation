using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal static class SelfTests
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        internal static void FocusOwnedWindow(Form form, Control editor)
        {
            // Test harness only: a hidden process launcher does not receive foreground rights.
            // Attach briefly to establish the app-owned test window; never inject into another app.
            uint ignored;
            uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            uint currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                Native.ShowWindow(form.Handle, 5); Native.SetForegroundWindow(form.Handle);
                if (editor != null) { editor.Focus(); SetFocus(editor.Handle); }
            }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
        private static void Assert(bool success, string message)
        { if (!success) throw new InvalidOperationException(message); }
        private sealed class FakeBackup : IDisposable
        {
            internal object Value;
            internal bool Disposed;
            public void Dispose() { Disposed = true; }
        }
        private sealed class FakeClipboard : ITransferClipboard
        {
            internal uint Counter = 1;
            internal object Value = "before";
            internal bool BackupFails; internal int BackupDelay;
            internal bool PartialStageFails, RestoreFails;
            internal int Stages, Restores;
            internal FakeBackup LastBackup;
            public uint Sequence { get { return Counter; } }
            public IDisposable Backup()
            {
                if (BackupDelay > 0) Thread.Sleep(BackupDelay);
                if (BackupFails) throw new InvalidOperationException("backup unsupported");
                LastBackup = new FakeBackup { Value = Value }; return LastBackup;
            }
            public uint Stage(object payload, uint expected)
            {
                if (expected != Counter) throw new InvalidOperationException("changed"); Value = payload; Counter++; Stages++;
                if (PartialStageFails) throw new ClipboardMutationException("simulated native clipboard write failure", Counter, null);
                return Counter;
            }
            internal int Owner;
            public int OwnerProcessId { get { return Owner; } }

            public void Restore(IDisposable backup, uint expected) { if (RestoreFails) throw new InvalidOperationException("simulated restore failure"); if (expected != Counter) return; Value = ((FakeBackup)backup).Value; Counter++; Restores++; }
        }
        private sealed class FakeTarget : IDestination
        {
            internal bool Alive = true, Focused, RefuseFocus;
            public bool IsAlive { get { return Alive; } }
            public bool IsFocused { get { return Focused; } }
            public bool WroteClipboard(int ownerProcessId) { return ownerProcessId == 4242; }
            public void Activate() { Focused = !RefuseFocus; }
        }
        private sealed class FakeProbe : IPasteProbe
        {
            internal int WorksWith, Last = -1;
            public object Capture(IDestination target) { Last = -1; return new object(); }
            public bool Changed(object before, IDestination target) { return Last == WorksWith; }
        }
        private static Task CompletedDelay(int ms) { return Task.FromResult(0); }
        private static bool Throws(Action action)
        { try { action(); return false; } catch (Exception) { return true; } }

        internal static int Run(string path)
        {
            List<object> results = new List<object>();
            Action<string, Action> test = delegate(string name, Action action)
            {
                try { action(); results.Add(new { name = name, passed = true }); }
                catch (Exception error) { results.Add(new { name = name, passed = false, error = error.GetBaseException().Message }); }
            };
            test("native x64 INPUT layout and complete Ctrl-V sequence", delegate
            {
                Assert(Marshal.SizeOf(typeof(Native.Input)) == 40, "Invalid Win32 INPUT size");
                Native.Input[] keys = Native.PasteEvents(0);
                Assert(keys.Length == 4 && keys[0].data.keyboard.key == 0xA2 && keys[1].data.keyboard.key == 0x56, "Wrong paste keys");
                Assert(keys[2].data.keyboard.flags == 2 && keys[3].data.keyboard.flags == 2, "Missing key up");
                foreach (Native.Input key in keys) Assert(key.data.keyboard.key != 0x0D, "Return must never be injected");
                Assert(Native.PasteEvents(1)[0].data.keyboard.key == 0x5B, "Mac Win mapping unavailable");
            });
            test("transcript requires settled text and finished IME composition", delegate
            {
                Assert(!Policy.TranscriptReady("", false, 2000, 2000), "Empty accepted");
                Assert(!Policy.TranscriptReady("文字", true, 2000, 2000), "Composition accepted");
                Assert(!Policy.TranscriptReady("文字", false, 649, 2000), "Released too soon");
                Assert(!Policy.TranscriptReady("文字", false, 2000, 699), "Unstable accepted");
                Assert(Policy.TranscriptReady("文字", false, 650, 700), "Stable rejected");
                Assert(!Policy.TranscriptReady("old text", false, 2000, 2000, false), "Old transcript accepted");
            });
            test("only recognized remote-control client processes selected", delegate
            {
                Assert(RemoteWindow.IsSupportedProcess("GameViewer") && RemoteWindow.IsSupportedProcess("UURemote")
                    && RemoteWindow.IsSupportedProcess("UU_Remote") && RemoteWindow.IsSupportedProcess("ToDesk")
                    && RemoteWindow.IsSupportedProcess("todesk") && RemoteWindow.IsSupportedProcess("SunloginClient")
                    && RemoteWindow.IsSupportedProcess("rustdesk") && RemoteWindow.IsSupportedProcess("AnyDesk") && RemoteWindow.IsSupportedProcess("TeamViewer")
                    && RemoteWindow.IsSupportedProcess("mstsc") && RemoteWindow.IsSupportedProcess("parsecd"), "Supported client rejected");
                foreach (string name in new[] { "notepad", "GameViewerService", "ToDeskService", "ToDesk_Service", "ToDeskHelper", "ToDeskFake" })
                    Assert(!RemoteWindow.IsSupportedProcess(name), "Unrelated process accepted");
            });
            test("custom remote process names can be added", delegate
            {
                string[] saved = RemoteWindow.ExtraProcesses;
                try
                {
                    Assert(!RemoteWindow.IsSupportedProcess("MyRemote"), "Unknown accepted");
                    RemoteWindow.ExtraProcesses = new[] { "MyRemote.exe" };
                    Assert(RemoteWindow.IsSupportedProcess("myremote"), "Custom process rejected");
                }
                finally { RemoteWindow.ExtraProcesses = saved; }
            });
            test("provider labels distinguish identical remote window titles", delegate
            {
                RemoteWindow uu = new RemoteWindow { ProviderName = RemoteWindow.ProviderForProcess("GameViewer"), Title = "Mac mini" };
                RemoteWindow td = new RemoteWindow { ProviderName = RemoteWindow.ProviderForProcess("ToDesk"), Title = "Mac mini" };
                Assert(uu.ToString() == "UU远程 · Mac mini" && td.ToString() == "ToDesk · Mac mini", "Provider label missing");
            });
            test("successful transfer retains new clipboard and sends one paste", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); FakeTarget target = new FakeTarget(); int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay);
                engine.Deliver("text", target, 800, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult();
                Assert(pastes == 1 && (string)clipboard.Value == "text" && clipboard.Restores == 0 && !engine.Busy && clipboard.LastBackup.Disposed, "Transfer wrong");
            });
            test("foreground activation refused: no paste, restore owned clipboard", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); FakeTarget target = new FakeTarget { RefuseFocus = true }; int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay);
                Assert(Throws(delegate { engine.Deliver("text", target, 500, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Focus denial accepted");
                Assert(pastes == 0 && (string)clipboard.Value == "before" && !engine.Busy, "Unsafe focus result");
            });
            test("focus switch during synchronization cancels and restores", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); FakeTarget target = new FakeTarget(); int pastes = 0, calls = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, delegate(int ms)
                { if (++calls == 3) target.Focused = false; return Task.FromResult(0); });
                Assert(Throws(delegate { engine.Deliver("text", target, 800, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Switch accepted");
                Assert(pastes == 0 && clipboard.Restores == 1, "Wrong focus protection");
            });
            test("new user clipboard during synchronization is never overwritten", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); FakeTarget target = new FakeTarget(); int pastes = 0, calls = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, delegate(int ms)
                { if (++calls == 3) { clipboard.Value = "user copy"; clipboard.Counter++; } return Task.FromResult(0); });
                Assert(Throws(delegate { engine.Deliver("text", target, 800, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Clipboard change accepted");
                Assert(pastes == 0 && clipboard.Restores == 0 && (string)clipboard.Value == "user copy", "User clipboard overwritten");
            });
            test("closed target is rejected before clipboard mutation", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { throw new Exception("paste should not run"); }, CompletedDelay);
                Assert(Throws(delegate { engine.Deliver("text", new FakeTarget { Alive = false }, 800, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Dead target accepted");
                Assert(clipboard.Stages == 0 && !engine.Busy, "Clipboard changed for dead target");
            });
            test("if the original clipboard cannot be backed up, nothing is pasted and it stays untouched", delegate
            {
                FakeClipboard clipboard = new FakeClipboard { BackupFails = true }; int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay);
                Assert(Throws(delegate { engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None).GetAwaiter().GetResult(); }), "Backup failure ignored");
                Assert(pastes == 0 && clipboard.Stages == 0 && (string)clipboard.Value == "before" && !engine.Busy, "Clipboard changed without a backup");
            });
            test("unknown remote client: tries the next paste key until the screen changes, reports the one that worked", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); List<int> sent = new List<int>();
                FakeProbe probe = new FakeProbe(); probe.WorksWith = 1;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { sent.Add(key); probe.Last = key; }, CompletedDelay);
                int used = engine.Deliver("text", new FakeTarget(), 800, new[] { 0, 1 }, true, CancellationToken.None, true, probe).GetAwaiter().GetResult();
                Assert(used == 1 && sent.Count == 2 && sent[0] == 0 && sent[1] == 1 && (string)clipboard.Value == "before", "Paste key not learned");
                sent.Clear(); probe.WorksWith = 0;
                used = engine.Deliver("text", new FakeTarget(), 800, new[] { 0, 1 }, true, CancellationToken.None, true, probe).GetAwaiter().GetResult();
                Assert(used == 0 && sent.Count == 1, "Kept trying after the first key worked");
            });
            test("paste that does not change the remote screen is reported so the user can retry", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); List<int> sent = new List<int>();
                FakeProbe probe = new FakeProbe(); probe.WorksWith = 9;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { sent.Add(key); probe.Last = key; }, CompletedDelay);
                int used = engine.Deliver("text", new FakeTarget(), 800, new[] { 1 }, true, CancellationToken.None, true, probe).GetAwaiter().GetResult();
                Assert(used == -1 && sent.Count == 1 && (string)clipboard.Value == "before", "Unconfirmed paste not reported");
            });
            test("known paste key is sent once without probing", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); List<int> sent = new List<int>();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { sent.Add(key); }, CompletedDelay);
                int used = engine.Deliver("text", new FakeTarget(), 800, new[] { 1 }, true, CancellationToken.None, true, null).GetAwaiter().GetResult();
                Assert(used == 1 && sent.Count == 1 && sent[0] == 1, "Known key not used directly");
            });
            test("original clipboard is restored after paste", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay);
                engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None, true).GetAwaiter().GetResult();
                Assert(pastes == 1 && (string)clipboard.Value == "before" && clipboard.Restores == 1, "Original clipboard not restored");
            });
            test("restore still happens when the remote client writes the same text back", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { }, delegate(int ms)
                { if (ms == 2000) { clipboard.Counter++; clipboard.Owner = 4242; } return Task.FromResult(0); });
                engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None, true).GetAwaiter().GetResult();
                Assert((string)clipboard.Value == "before" && clipboard.Restores == 1, "Echoed clipboard blocked restore");
            });
            test("remote client writing the clipboard during the wait does not block or cancel the paste", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); int pastes = 0, calls = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, delegate(int ms)
                { if (++calls == 3) { clipboard.Counter++; clipboard.Owner = 4242; } return Task.FromResult(0); });
                engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None, true).GetAwaiter().GetResult();
                Assert(pastes == 1 && clipboard.Restores == 1 && clipboard.Stages == 2, "Remote echo cancelled the paste or our text was not re-staged");
            });
            test("slow clipboard backup times out without touching the clipboard", delegate
            {
                FakeClipboard clipboard = new FakeClipboard { BackupDelay = 400 }; int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay) { BackupTimeoutMilliseconds = 50 };
                Assert(Throws(delegate { engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None, true).GetAwaiter().GetResult(); }), "Slow backup not bounded");
                Assert(Throws(delegate { engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None, true).GetAwaiter().GetResult(); }), "Second backup started while the first still holds the clipboard");
                Thread.Sleep(600);
                Assert(pastes == 0 && clipboard.Stages == 0 && !engine.Busy, "Clipboard changed after backup timeout");
            });
            test("failed restore after a sent paste is not reported as a failed paste", delegate
            {
                FakeClipboard clipboard = new FakeClipboard { RestoreFails = true }; int pastes = 0;
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { pastes++; }, CompletedDelay);
                engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None).GetAwaiter().GetResult();
                Assert(pastes == 1 && !engine.Busy, "Restore failure turned success into failure");
            });
            test("partial clipboard write failure restores original owned generation", delegate
            {
                FakeClipboard clipboard = new FakeClipboard { PartialStageFails = true };
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { throw new Exception("paste should not run"); }, CompletedDelay);
                Assert(Throws(delegate { engine.Deliver("partial", new FakeTarget(), 800, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Partial write ignored");
                Assert((string)clipboard.Value == "before" && clipboard.Restores == 1 && !engine.Busy, "Partial write not rolled back");
            });
            test("cancellation is handled before mutation", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); CancellationTokenSource token = new CancellationTokenSource(); token.Cancel();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { }, CompletedDelay);
                Assert(Throws(delegate { engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, false, token.Token).GetAwaiter().GetResult(); }), "Cancel ignored");
                Assert(clipboard.Stages == 0 && !engine.Busy, "Canceled write"); token.Dispose();
            });
            test("optional restore respects clipboard ownership after paste", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { }, delegate(int ms)
                { if (ms == 2000) { clipboard.Value = "user latest"; clipboard.Counter++; } return Task.FromResult(0); });
                engine.Deliver("text", new FakeTarget(), 800, new[] { 0 }, true, CancellationToken.None).GetAwaiter().GetResult();
                Assert(clipboard.Restores == 0 && (string)clipboard.Value == "user latest", "Late clipboard overwrite");
            });
            test("overlapping transfers are rejected", delegate
            {
                FakeClipboard clipboard = new FakeClipboard(); TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>();
                TransferEngine engine = new TransferEngine(clipboard, delegate(int key) { }, delegate(int ms) { return gate.Task; });
                Task first = engine.Deliver("first", new FakeTarget(), 100, new[] { 0 }, false, CancellationToken.None);
                Assert(Throws(delegate { engine.Deliver("second", new FakeTarget(), 100, new[] { 0 }, false, CancellationToken.None).GetAwaiter().GetResult(); }), "Overlap accepted");
                gate.SetResult(true); first.GetAwaiter().GetResult(); Assert(clipboard.Stages == 1 && !engine.Busy, "Concurrent staging");
            });
            test("hold-to-talk and tray defaults survive settings serialization", delegate
            {
                Settings defaults = new Settings();
                Assert(defaults.OneKeyEnabled && defaults.StartInTray && defaults.AutoDetectVoice, "One-key defaults off");
                Settings restored = new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(defaults));
                Assert(restored.OneKeyEnabled && restored.StartInTray && restored.AutoDetectVoice, "Settings lost");
            });
            test("installed Doubao modifier values map to physical left/right keys", delegate
            {
                Assert(DoubaoShortcut.ModifierIndex(0, 1025) == 0 && DoubaoShortcut.ModifierIndex(0, 2049) == 1, "Alt mapping invalid");
                Assert(DoubaoShortcut.ModifierIndex(0, 258) == 2 && DoubaoShortcut.ModifierIndex(0, 514) == 3, "Ctrl mapping invalid");
                Assert(DoubaoShortcut.ModifierIndex(65, 1025) == -1 && DoubaoShortcut.ModifierIndex(0, 999) == -1, "Unknown shortcut guessed");
            });
            test("hold switches only for a lone voice key pressed in the same remote window", delegate
            {
                Assert(HoldPolicy.ShouldTrigger(true, true, true, true, true), "Valid hold rejected");
                Assert(!HoldPolicy.ShouldTrigger(true, false, true, true, true), "Option combination accepted");
                Assert(!HoldPolicy.ShouldTrigger(true, true, false, true, true), "Local window hijacked");
                Assert(!HoldPolicy.ShouldTrigger(true, true, true, false, true), "Foreground change accepted");
                Assert(!HoldPolicy.ShouldTrigger(true, true, true, true, false), "Busy session restarted");
                Assert(!HoldPolicy.ShouldTrigger(false, true, true, true, true), "Released key accepted");
            });
            test("release is debounced briefly and taps are distinguished from holds", delegate
            {
                Assert(!HoldPolicy.Released(false, 199) && HoldPolicy.Released(false, 200) && !HoldPolicy.Released(true, 5000), "Release debounce wrong");
                Assert(HoldPolicy.TapMilliseconds >= 300 && HoldPolicy.TapMilliseconds <= 800, "Tap window unreasonable");
            });
            test("finish waits for Doubao composition and organizing before pasting", delegate
            {
                Assert(HoldPolicy.Decide(true, true, "", false, 5000, 5000) == FinishDecision.Wait, "Composition not awaited");
                Assert(HoldPolicy.Decide(true, false, "文字", true, 900, 1499) == FinishDecision.Wait, "Unstable text delivered");
                Assert(HoldPolicy.Decide(true, false, "文字", true, 700, 5000) == FinishDecision.Wait, "Delivered too soon after release");
                Assert(HoldPolicy.Decide(true, false, "文字", true, 900, 1500) == FinishDecision.Deliver, "Settled text rejected");
                Assert(HoldPolicy.Decide(true, false, "old", false, 3000, 3000) == FinishDecision.Wait, "Old text delivered");
                Assert(HoldPolicy.Decide(true, false, "", false, 5000, 5000) == FinishDecision.Wait, "Organizing Doubao abandoned early");
                Assert(HoldPolicy.Decide(true, false, "", false, 12000, 12000) == FinishDecision.Abandon, "Empty result never abandoned");
                Assert(HoldPolicy.Decide(false, false, "", false, 1400, 1400) == FinishDecision.Wait && HoldPolicy.Decide(false, false, "", false, 1500, 1500) == FinishDecision.Abandon, "Unstarted Doubao handling wrong");
                Assert(HoldPolicy.Decide(true, true, "x", true, 20000, 0) == FinishDecision.Timeout, "Endless composition not bounded");
            });
            test("hold session delivers once and rejects stale generations", delegate
            {
                HoldSession state = new HoldSession(); int id = state.Begin(0, 3);
                Assert(state.Phase == HoldPhase.Listening && state.Begin(10, 9) == -1 && state.StartingTextVersion == 3, "Repeated begin replaced session");
                Assert(!state.TryDeliver(id), "Delivered while holding");
                Assert(state.Release(500) && state.Phase == HoldPhase.Finalizing && !state.Release(600), "Release wrong");
                Assert(!state.TryDeliver(id - 1) && state.TryDeliver(id) && !state.TryDeliver(id), "Delivery not exactly once");
                state.Fail(); Assert(!state.Active && state.Begin(1000, 4) > id, "Failed session blocks retry");
            });
            test("injected paste keys carry scan codes and correct extended flags", delegate
            {
                Assert(Native.Key(0xA2, false).data.keyboard.scan != 0 && Native.Key(0x56, false).data.keyboard.scan != 0, "Missing scan code");
                Assert(Native.Key(0xA5, false).data.keyboard.flags == 1 && Native.Key(0xA3, true).data.keyboard.flags == 3, "Extended modifier invalid");
                Assert(Native.Key(0xA4, true).data.keyboard.flags == 2, "Left Alt encoding changed");
            });
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(new { scope = "isolated automated tests; no remote Mac or live dictation", tests = results });
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
            return json.Contains("\"passed\":false") ? 1 : 0;
        }

        private static string MarkerText(object value)
        {
            string text = value as string;
            if (text != null) return text;
            MemoryStream stream = value as MemoryStream;
            return stream == null ? null : System.Text.Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
        }

        private static async Task Until(Func<bool> condition, int milliseconds)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            { if (clock.ElapsedMilliseconds > milliseconds) throw new InvalidOperationException("hold test timed out waiting for expected phase"); await Task.Delay(30); }
        }
        // 本机界面流程：假按键状态、假投递；不录音、不写剪贴板、不连远端。
        internal static async Task HoldIntegration(MainForm main, string directory)
        {
            List<object> results = new List<object>();
            HoldSnapshot fake = new HoldSnapshot();
            int transfers = 0; string delivered = null;
            OneKeyController controller = null;
            using (Form remote = new Form { Text = "远控窗口替身", ClientSize = new Size(380, 140), StartPosition = FormStartPosition.CenterScreen })
            {
                remote.Controls.Add(new Label { Text = "隔离测试：不录音、不写剪贴板、不连接远端", Dock = DockStyle.Fill });
                Action press = delegate { fake.Held = true; fake.Alone = true; fake.DownAt = HoldClock.Now; fake.DownForeground = Native.GetForegroundWindow(); fake.PressSerial++; };
                Action release = delegate { fake.Held = false; fake.UpAt = HoldClock.Now; };
                try
                {
                    remote.Show(main); FocusOwnedWindow(remote, null); await Task.Delay(150);
                    RemoteWindow original = RemoteWindow.Capture(remote.Handle, true);
                    controller = new OneKeyController(main, delegate(RemoteWindow w) { return new OneKeyOptions { PasteDelay = 800, PasteKeys = new[] { 0 } }; }, delegate { return true; },
                        delegate(string text, RemoteWindow destination, OneKeyOptions options, CancellationToken token)
                        {
                            Assert(destination.Handle == original.Handle, "Original target replaced");
                            delivered = text; transfers++; destination.Activate(); return Task.FromResult(0);
                        }, delegate { return fake; });
                    controller.Enabled = true;
                    OneKeyController c = controller;

                    press(); await Task.Delay(120); release(); await Task.Delay(500);
                    Assert(!c.Busy && !c.Surface.Visible, "Quick tap opened dictation");
                    FocusOwnedWindow(remote, null); press(); fake.Alone = false; await Task.Delay(500); release();
                    Assert(!c.Busy && !c.Surface.Visible, "Option combination opened dictation");
                    results.Add(new { name = "short taps and key combinations stay with the remote window", passed = true });

                    FocusOwnedWindow(remote, null); await Task.Delay(100); press();
                    await Until(delegate { return c.TestPhase == HoldPhase.Listening && c.Surface.Ready; }, 2500);
                    results.Add(new { name = "lone hold opens and focuses the local dictation box", passed = true });
                    c.Surface.Editor.Text = "这是远程听写测试"; await Task.Delay(300);
                    using (Bitmap shot = new Bitmap(c.Surface.Width, c.Surface.Height))
                    {
                        using (Graphics graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(c.Surface.Location, Point.Empty, c.Surface.Size);
                        shot.Save(Path.Combine(directory, "hold-listening.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    c.Surface.Editor.Clear();
                    fake.Held = false; fake.UpAt = HoldClock.Now; await Task.Delay(100);
                    fake.Held = true; fake.PressSerial++; fake.DownAt = HoldClock.Now; await Task.Delay(700);
                    Assert(c.TestPhase == HoldPhase.Listening, "Spurious key-up ended listening");
                    results.Add(new { name = "brief remote focus-loss key-up does not end listening", passed = true });
                    c.Surface.Editor.Text = "远控按住听写隔离验证"; release();
                    await Task.Delay(1000); Assert(transfers == 0, "Delivered before text settled");
                    await Until(delegate { return transfers == 1 && !c.Busy; }, 5000);
                    Assert(delivered == "远控按住听写隔离验证", "Unexpected transcript");
                    results.Add(new { name = "release waits for settled text and delivers exactly once to the original window (fake delivery)", passed = true });

                    FocusOwnedWindow(remote, null); await Task.Delay(100); press();
                    await Until(delegate { return c.TestPhase == HoldPhase.Listening && c.Surface.Ready; }, 2500);
                    release(); await Until(delegate { return !c.Busy; }, 4000); await Task.Delay(150);
                    Assert(transfers == 1 && !c.Surface.Visible && Native.GetForegroundWindow() == remote.Handle, "Abandon did not return quietly");
                    results.Add(new { name = "no recognized text returns quietly to the remote window", passed = true });

                    FocusOwnedWindow(remote, null); await Task.Delay(100); press();
                    await Until(delegate { return c.TestPhase == HoldPhase.Listening && c.Surface.Ready; }, 2500);
                    c.Surface.Editor.Text = "取消后保留的本机测试文字"; fake.EscapeSerial++; await Task.Delay(250); release();
                    Assert(c.TestPhase == HoldPhase.Failed && c.Surface.Editor.TextLength > 0, "Escape lost text");
                    await Task.Delay(100);
                    using (Bitmap screenshot = new Bitmap(c.Surface.ClientSize.Width, c.Surface.ClientSize.Height))
                    {
                        using (Graphics graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(c.Surface.PointToScreen(Point.Empty), Point.Empty, c.Surface.ClientSize);
                        screenshot.Save(Path.Combine(directory, "hold-review.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    FocusOwnedWindow(remote, null); await Task.Delay(100); press(); await Task.Delay(600); release(); await Task.Delay(200);
                    Assert(c.TestPhase == HoldPhase.Failed && c.Surface.Editor.Text == "取消后保留的本机测试文字" && transfers == 1, "Pending text overwritten by new hold");
                    results.Add(new { name = "Esc keeps text and a new hold cannot overwrite pending text", passed = true });

                    c.Surface.Editor.Clear(); FocusOwnedWindow(remote, null); await Task.Delay(100); press();
                    await Until(delegate { return c.TestPhase == HoldPhase.Listening; }, 2500);
                    c.Dispose(); release();
                    results.Add(new { name = "exit during listening is clean", passed = true });
                }
                catch (Exception error)
                { results.Add(new { name = "hold local UI flow", passed = false, error = error.Message,
                    state = controller == null ? null : controller.TestDiagnostics, transfers = transfers }); Environment.ExitCode = 1; }
                finally { if (controller != null) controller.Dispose(); remote.Close(); }
            }
            File.WriteAllText(Path.Combine(directory, "hold-results.json"), new JavaScriptSerializer().Serialize(new {
                scope = "real local floating UI; fake key state and fake delivery; no microphone, clipboard writes or remote messages", tests = results
            }));
        }

        internal static async Task Integration(MainForm main, string directory)
        {
            string marker = Guid.NewGuid().ToString("N");
            const string markerFormat = "RemoteDictation.TestOwner";
            ClipboardBackup original = ClipboardBackup.Capture();
            List<object> results = new List<object>();
            uint originalSequence = original.Sequence;
            List<string> trace = new List<string>();
            try
            {
                using (Form receiver = new Form { Text = "远控听写 · 本机测试接收框", ClientSize = new Size(520, 260), StartPosition = FormStartPosition.CenterScreen })
                using (RichTextBox box = new RichTextBox { Dock = DockStyle.Fill })
                {
                    int returns = 0;
                    box.KeyDown += delegate(object sender, KeyEventArgs e)
                    { if (e.KeyCode == Keys.Enter) returns++; if (e.KeyCode == Keys.V) trace.Add("target V key received; control=" + e.Control); };
                    receiver.Controls.Add(box); receiver.Show(main); FocusOwnedWindow(receiver, box);
                    await Task.Delay(350);
                    box.Focus();
                    trace.Add("receiver focused=" + (Native.GetForegroundWindow() == receiver.Handle) + "; editor focused=" + box.Focused + "; native focus matches=" + (GetFocus() == box.Handle));
                    RemoteWindow target = RemoteWindow.Capture(receiver.Handle, true);
                    TransferEngine engine = new TransferEngine(new SystemTransferClipboard(), Native.Paste, Task.Delay);
                    DataObject text = Payloads.Text("远控听写：本机文字粘贴验证"); text.SetData(markerFormat, marker);
                    if (Native.GetClipboardSequenceNumber() != originalSequence) throw new InvalidOperationException("Clipboard changed before test; canceled without mutation");
                    await engine.Deliver(text, target, 100, new[] { 0 }, false, CancellationToken.None);
                    await Task.Delay(200);
                    trace.Add("clipboard text matches=" + (Clipboard.GetText() == "远控听写：本机文字粘贴验证") + "; receiver text length=" + box.TextLength + "; editor focused=" + box.Focused);
                    Assert(box.Text == "远控听写：本机文字粘贴验证", "Local Ctrl-V did not paste expected text");
                    results.Add(new { name = "real Win32 SendInput text to app-owned RichTextBox", passed = true });
                    box.Clear();
                    Assert(returns == 0, "Return sent unexpectedly");
                    results.Add(new { name = "no Return received by local target", passed = true });
                    uint staleSequence = Native.GetClipboardSequenceNumber();
                    DataObject userCopy = Payloads.Text("本机测试：模拟后来复制的新内容"); userCopy.SetData(markerFormat, marker);
                    NativeClipboard.Stage(userCopy, staleSequence);
                    Assert(!original.Restore(staleSequence), "Stale restore overwrote new clipboard");
                    Assert(Clipboard.GetText() == "本机测试：模拟后来复制的新内容", "Changed clipboard did not survive");
                    results.Add(new { name = "real native clipboard restore refuses stale generation", passed = true });
                    receiver.Close();
                }
            }
            catch (Exception error)
            { results.Add(new { name = "local integration", passed = false, error = error.Message,
                expectedSequence = error.Data["ExpectedSequence"], actualSequence = error.Data["ActualSequence"] }); Environment.ExitCode = 1; }
            finally
            {
                try
                {
                    uint expected = Native.GetClipboardSequenceNumber();
                    bool restored = Clipboard.ContainsData(markerFormat) && MarkerText(Clipboard.GetData(markerFormat)) == marker && original.Restore(expected);
                    results.Add(new { name = "original clipboard restored after local test", passed = restored });
                    if (!restored) Environment.ExitCode = 1;
                }
                catch (Exception error) { results.Add(new { name = "clipboard cleanup", passed = false, error = error.Message }); Environment.ExitCode = 1; }
                finally { original.Dispose(); }
                File.WriteAllText(Path.Combine(directory, "integration-results.json"), new JavaScriptSerializer().Serialize(new {
                    scope = "local app-owned window; not UU or ToDesk, not Mac, not live voice recognition", tests = results, diagnosticTrace = trace
                }));
            }
        }
    }
}
