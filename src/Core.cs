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
        string CurrentText();
    }
    internal sealed class SystemTransferClipboard : ITransferClipboard
    {
        public uint Sequence { get { return Native.GetClipboardSequenceNumber(); } }
        public IDisposable Backup() { return ClipboardBackup.Capture(); }
        public uint Stage(object payload, uint expected) { return NativeClipboard.Stage((DataObject)payload, expected); }
        public void Restore(IDisposable backup, uint expected)
        { if (!((ClipboardBackup)backup).Restore(expected)) throw new InvalidOperationException("原剪贴板未能还原。"); }
        public string CurrentText() { return NativeClipboard.CurrentText(); }
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
        internal bool Busy { get; private set; }
        internal TransferEngine(ITransferClipboard clipboard, Action<int> paste, Func<int, Task> delay)
        { this.clipboard = clipboard; this.paste = paste; this.delay = delay; }
        // 写入剪贴板 → 激活原远控窗口 → 等远控软件同步 → 发粘贴键 → 等远端读取后还原原剪贴板。
        // pasteKeys：要尝试的粘贴键（0=Ctrl+V，1=Win+V）。给了 probe 时，每发一次都看远控画面有没有变化：
        //            变了就停，返回生效的那个键；都没看出变化返回 -1（调用方会提醒用户，文字保留）。不给 probe 时只发第一个并返回它。
        // echoText：远控软件可能把同样的文字回写到本机剪贴板（序号变了但内容就是我们写的），这种情况视为仍是我们的内容。
        internal async Task<int> Deliver(object payload, IDestination target, int waitMilliseconds, int[] pasteKeys, bool restoreAfter,
            CancellationToken cancellation, string echoText = null, IPasteProbe probe = null)
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
                backup = clipboard.Backup();
                if (before != clipboard.Sequence) throw new InvalidOperationException("备份期间剪贴板发生变化，已取消。");
                try { stagedSequence = clipboard.Stage(payload, before); staged = true; }
                catch (ClipboardMutationException error) { stagedSequence = error.Sequence; staged = true; throw; }
                target.Activate();
                // Foreground activation may finish on the following UI turn. Do not inject before it succeeds.
                await delay(100);
                Check(target, stagedSequence, echoText);
                for (int elapsed = 0; elapsed < waitMilliseconds; elapsed += 50)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Check(target, stagedSequence, echoText);
                    await delay(Math.Min(50, waitMilliseconds - elapsed));
                }
                bool probing = probe != null; bool waitedAfterPaste = false;
                for (int i = 0; i < pasteKeys.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Check(target, stagedSequence, echoText);
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
                    try { RestoreIfStillOurs(backup, stagedSequence, echoText); }
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
                        try { RestoreIfStillOurs(backup, stagedSequence, echoText); }
                        catch (Exception) { /* 还原失败时保留已写入的文字，并保留原始错误。 */ }
                    }
                }
                finally { try { if (backup != null) backup.Dispose(); } finally { Busy = false; } }
            }
        }
        private bool StillOurs(uint stagedSequence, string echoText)
        { return clipboard.Sequence == stagedSequence || echoText != null && clipboard.CurrentText() == echoText; }
        private void Check(IDestination target, uint stagedSequence, string echoText)
        { Policy.EnsureTransfer(target.IsAlive, target.IsFocused, stagedSequence, StillOurs(stagedSequence, echoText) ? stagedSequence : clipboard.Sequence); }
        private void RestoreIfStillOurs(IDisposable backup, uint stagedSequence, string echoText)
        {
            if (StillOurs(stagedSequence, echoText)) clipboard.Restore(backup, clipboard.Sequence);
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
