using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal static class Policy
    {
        internal const long MaximumBytes = 16 * 1024 * 1024;
        internal static bool TranscriptReady(string text, bool composition, double elapsed, double stable, bool newText = true)
        { return newText && !string.IsNullOrWhiteSpace(text) && !composition && elapsed >= 650 && stable >= 700; }
        internal static void EnsureTransfer(bool alive, bool focused, uint expected, uint actual)
        {
            if (!alive) throw new InvalidOperationException("原远控窗口已关闭，本次已取消。");
            if (!focused) throw new InvalidOperationException("前台窗口已改变，本次自动粘贴已取消。");
            if (expected != actual)
            {
                InvalidOperationException error = new InvalidOperationException("剪贴板已改变，本次自动粘贴已取消。");
                error.Data["ExpectedSequence"] = expected; error.Data["ActualSequence"] = actual; throw error;
            }
        }
    }

    internal sealed class ClipboardBackup : IDisposable
    {
        internal readonly List<NativeClipboard.Entry> Entries = new List<NativeClipboard.Entry>();
        internal uint Sequence;
        internal static ClipboardBackup Capture() { return NativeClipboard.Capture(); }
        internal bool Restore(uint expected)
        { uint sequence; return NativeClipboard.Write(Entries, expected, out sequence); }
        public void Dispose() { foreach (NativeClipboard.Entry item in Entries) item.Dispose(); Entries.Clear(); }
    }

    internal interface ITransferClipboard
    {
        uint Sequence { get; }
        IDisposable Backup();
        uint Stage(object payload, uint expected);
        void Restore(IDisposable backup, uint expected);
        int OwnerProcessId { get; }
    }
    internal sealed class SystemTransferClipboard : ITransferClipboard
    {
        // 在创建它的线程（界面线程）上建好剪贴板所有者窗口；之后备份可以放到后台线程做。
        internal SystemTransferClipboard() { NativeClipboard.Prepare(); }
        public uint Sequence { get { return Native.GetClipboardSequenceNumber(); } }
        public IDisposable Backup() { return ClipboardBackup.Capture(); }
        public uint Stage(object payload, uint expected) { return NativeClipboard.Stage((DataObject)payload, expected); }
        public void Restore(IDisposable backup, uint expected)
        { if (!((ClipboardBackup)backup).Restore(expected)) throw new InvalidOperationException("原剪贴板未能还原。"); }
        public int OwnerProcessId { get { return NativeClipboard.OwnerProcessId(); } }
    }

    // 判断粘贴是否生效：发粘贴键前后各截一次远控窗口（缩小成灰度图，只在内存里比较，不保存），画面有明显变化就算贴上了。
    internal interface IPasteProbe
    {
        object Capture(IDestination target);
        bool Changed(object before, IDestination target);
    }
    internal sealed class ScreenPasteProbe : IPasteProbe
    {
        private const int Width = 480;
        public object Capture(IDestination target)
        {
            RemoteWindow window = target as RemoteWindow;
            if (window == null) return null;
            Native.RECT r; if (!Native.GetWindowRect(window.Handle, out r)) return null;
            Rectangle area = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), SystemInformation.VirtualScreen);
            if (area.Width < 50 || area.Height < 50) return null;
            int height = Math.Max(1, area.Height * Width / area.Width);
            using (Bitmap full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
            using (Bitmap small = new Bitmap(Width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(full)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                using (Graphics g = Graphics.FromImage(small)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear; g.DrawImage(full, 0, 0, Width, height); }
                BitmapData data = small.LockBits(new Rectangle(0, 0, Width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] raw = new byte[data.Stride * height]; System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                    byte[] gray = new byte[Width * height];
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < Width; x++)
                        { int i = y * data.Stride + x * 3; gray[y * Width + x] = (byte)((raw[i] * 29 + raw[i + 1] * 150 + raw[i + 2] * 77) >> 8); }
                    return gray;
                }
                finally { small.UnlockBits(data); }
            }
        }
        public bool Changed(object before, IDestination target)
        {
            byte[] a = before as byte[], b = Capture(target) as byte[];
            if (a == null || b == null || a.Length != b.Length) return false;
            int changed = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 28) changed++;
            // 光标闪烁只有几个点；贴进一句话通常有几十上百个点变化。
            return changed >= 20;
        }
    }

    internal sealed class TransferEngine
    {
        private readonly ITransferClipboard clipboard;
        private readonly Action<int> paste;
        private readonly Func<int, Task> delay;
        private static int backupsInFlight;
        internal bool Busy { get; private set; }
        internal int BackupTimeoutMilliseconds = 5000;
        internal TransferEngine(ITransferClipboard clipboard, Action<int> paste, Func<int, Task> delay)
        { this.clipboard = clipboard; this.paste = paste; this.delay = delay; }
        // 写入剪贴板 → 激活原远控窗口 → 等远控软件同步 → 发粘贴键 → 等远端读取后还原原剪贴板。
        // pasteKeys：要尝试的粘贴键（0=Ctrl+V，1=Win+V）。给了 probe 时，每发一次都看远控画面有没有变化：
        //            变了就停，返回生效的那个键；都没看出变化返回 -1（调用方会提醒用户，文字保留）。不给 probe 时只发第一个并返回它。
        // allowRemoteEcho：远控软件同步剪贴板时可能把内容回写到本机（序号变了），这时按“仍是我们的”处理，见 StillOurs。
        internal async Task<int> Deliver(object payload, IDestination target, int waitMilliseconds, int[] pasteKeys, bool restoreAfter,
            CancellationToken cancellation, bool allowRemoteEcho = false, IPasteProbe probe = null)
        {
            if (Busy) throw new InvalidOperationException("正在同步，请等待本次完成。");
            if (target == null || !target.IsAlive) throw new InvalidOperationException("原远控窗口已关闭，请重新连接远端。");
            if (pasteKeys == null || pasteKeys.Length == 0) pasteKeys = new[] { 0 };
            Busy = true;
            IDisposable backup = null; uint stagedSequence = 0; bool staged = false, succeeded = false; int used = -1;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                uint before = clipboard.Sequence;
                // 先完整备份原剪贴板；备份不了就不粘贴（文字留在浮窗），保证原剪贴板一定能还原。
                backup = await BackupWithin(BackupTimeoutMilliseconds);
                cancellation.ThrowIfCancellationRequested();
                if (before != clipboard.Sequence) throw new InvalidOperationException("备份期间剪贴板发生变化，已取消。");
                try { stagedSequence = clipboard.Stage(payload, before); staged = true; }
                catch (ClipboardMutationException error) { stagedSequence = error.Sequence; staged = true; throw; }
                target.Activate();
                // Foreground activation may finish on the following UI turn. Do not inject before it succeeds.
                await delay(100);
                Check(target, stagedSequence, allowRemoteEcho);
                for (int elapsed = 0; elapsed < waitMilliseconds; elapsed += 50)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Check(target, stagedSequence, allowRemoteEcho);
                    await delay(Math.Min(50, waitMilliseconds - elapsed));
                }
                bool probing = probe != null; bool waitedAfterPaste = false;
                for (int i = 0; i < pasteKeys.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Check(target, stagedSequence, allowRemoteEcho);
                    // 远控软件动过剪贴板（序号变了）：不读内容去比对（可能卡住），而是把我们的文字再写一遍并稍等同步，
                    // 确保远端剪贴板里一定是这次听写的文字，而不是远端原来的内容。
                    if (clipboard.Sequence != stagedSequence)
                    {
                        stagedSequence = clipboard.Stage(payload, clipboard.Sequence);
                        await delay(Math.Max(300, waitMilliseconds / 2));
                        Check(target, stagedSequence, allowRemoteEcho);
                    }
                    object snapshot = probing ? probe.Capture(target) : null;
                    paste(pasteKeys[i]); succeeded = true;
                    if (!probing || snapshot == null) { used = pasteKeys[i]; break; }
                    await delay(1500); waitedAfterPaste = true; // 远控画面回传有延迟，等够再判断，避免把慢到的第一次粘贴误算到下一种键上
                    if (probe.Changed(snapshot, target)) { used = pasteKeys[i]; break; }
                }
                if (restoreAfter)
                {
                    // 给远端留出读取时间再还原。用户这期间自己复制了新内容就不覆盖。
                    // 粘贴已发出，还原失败不能再当成粘贴失败，否则用户重试会重复粘贴。
                    await delay(waitedAfterPaste ? 500 : 2000);
                    try { RestoreIfStillOurs(target, backup, stagedSequence, allowRemoteEcho); }
                    catch (Exception) { }
                }
                return used;
            }
            finally
            {
                try
                {
                    if (!succeeded && staged && backup != null)
                    {
                        try { RestoreIfStillOurs(target, backup, stagedSequence, allowRemoteEcho); }
                        catch (Exception) { /* 还原失败时保留已写入的文字，并保留原始错误。 */ }
                    }
                }
                finally { try { if (backup != null) backup.Dispose(); } finally { Busy = false; } }
            }
        }
        // 序号没变 = 还是我们写的。序号变了、但剪贴板现在属于这个远控软件（或它的辅助进程）= 它在同步我们写的文字，也算我们的。
        // 只看“归谁”、不读内容：读内容可能触发远控软件“用到时才从远端取”的延迟渲染，最长卡 30 秒（UU 上实际发生过）。
        // 代价：如果你恰好在这 2 秒里在 Mac 上复制了新内容并被同步下来，会被还原覆盖。
        private bool StillOurs(IDestination target, uint stagedSequence, bool allowRemoteEcho)
        {
            if (clipboard.Sequence == stagedSequence) return true;
            return allowRemoteEcho && target.WroteClipboard(clipboard.OwnerProcessId);
        }
        // 备份放到后台线程并限时：读剪贴板可能要等别的程序渲染内容。超时就不粘贴（保证原剪贴板不丢）。
        private async Task<IDisposable> BackupWithin(int milliseconds)
        {
            if (Volatile.Read(ref backupsInFlight) > 0)
                throw new InvalidOperationException("上一次读取剪贴板还没结束（远控软件可能正在同步剪贴板），文字已保留，请稍等几秒再点「粘贴到远端」。");
            Interlocked.Increment(ref backupsInFlight);
            Task<IDisposable> work = Task.Run(delegate
            {
                try { return clipboard.Backup(); }
                finally { Interlocked.Decrement(ref backupsInFlight); }
            });
            using (CancellationTokenSource timer = new CancellationTokenSource())
            {
                Task timeout = Task.Delay(milliseconds, timer.Token);
                if (await Task.WhenAny(work, timeout) == work) { timer.Cancel(); return await work; }
            }
            // 后台那次备份迟早会结束：成功就释放结果，出错就把异常读掉，避免未观察的任务异常。
            Task ignored = work.ContinueWith(delegate(Task<IDisposable> late)
            {
                if (late.IsFaulted) { Exception unused = late.Exception; }
                else if (late.Status == TaskStatus.RanToCompletion && late.Result != null) late.Result.Dispose();
            });
            throw new InvalidOperationException("剪贴板暂时读不出来（可能远控软件正在同步剪贴板），为保证能还原，这次没有粘贴。文字已保留，稍等几秒再点「粘贴到远端」。");
        }
        private void Check(IDestination target, uint stagedSequence, bool allowRemoteEcho)
        { Policy.EnsureTransfer(target.IsAlive, target.IsFocused, stagedSequence, StillOurs(target, stagedSequence, allowRemoteEcho) ? stagedSequence : clipboard.Sequence); }
        private void RestoreIfStillOurs(IDestination target, IDisposable backup, uint stagedSequence, bool allowRemoteEcho)
        {
            if (StillOurs(target, stagedSequence, allowRemoteEcho)) clipboard.Restore(backup, clipboard.Sequence);
        }
    }

    internal static class Payloads
    {
        internal static DataObject Text(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请先输入文字。");
            if (Encoding.UTF8.GetByteCount(text) > Policy.MaximumBytes) throw new InvalidOperationException("文字超过 16 MiB。");
            DataObject data = new DataObject(); data.SetData(DataFormats.UnicodeText, text); return data;
        }
    }
}
