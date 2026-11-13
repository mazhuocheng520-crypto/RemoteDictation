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

    internal sealed class TransferEngine
    {
        private readonly ITransferClipboard clipboard;
        private readonly Action<bool> paste;
        private readonly Func<int, Task> delay;
        internal bool Busy { get; private set; }
        internal TransferEngine(ITransferClipboard clipboard, Action<bool> paste, Func<int, Task> delay)
        { this.clipboard = clipboard; this.paste = paste; this.delay = delay; }
        // 写入剪贴板 → 激活原远控窗口 → 等远控软件同步 → 发粘贴键 → 等远端读取后还原原剪贴板。
        // echoText：远控软件可能把同样的文字回写到本机剪贴板（序号变了但内容就是我们写的），这种情况仍要还原。
        internal async Task Deliver(object payload, IDestination target, int waitMilliseconds, bool windowsKey, bool restoreAfter,
            CancellationToken cancellation, string echoText = null)
        {
            if (Busy) throw new InvalidOperationException("正在同步，请等待本次完成。");
            if (target == null || !target.IsAlive) throw new InvalidOperationException("原远控窗口已关闭，请重新连接远端。");
            Busy = true;
            IDisposable backup = null; uint stagedSequence = 0; bool staged = false, succeeded = false;
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
                Policy.EnsureTransfer(target.IsAlive, target.IsFocused, stagedSequence, clipboard.Sequence);
                for (int elapsed = 0; elapsed < waitMilliseconds; elapsed += 50)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Policy.EnsureTransfer(target.IsAlive, target.IsFocused, stagedSequence, clipboard.Sequence);
                    await delay(Math.Min(50, waitMilliseconds - elapsed));
                }
                cancellation.ThrowIfCancellationRequested();
                Policy.EnsureTransfer(target.IsAlive, target.IsFocused, stagedSequence, clipboard.Sequence);
                paste(windowsKey); succeeded = true;
                if (restoreAfter)
                {
                    // 给远端留出读取时间再还原。用户这期间自己复制了新内容就不覆盖。
                    // 粘贴已发出，还原失败不能再当成粘贴失败，否则用户重试会重复粘贴。
                    await delay(2000);
                    try { RestoreIfStillOurs(backup, stagedSequence, echoText); }
                    catch (Exception) { }
                }
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
        private void RestoreIfStillOurs(IDisposable backup, uint stagedSequence, string echoText)
        {
            uint current = clipboard.Sequence;
            if (current == stagedSequence || echoText != null && clipboard.CurrentText() == echoText)
                clipboard.Restore(backup, current);
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
