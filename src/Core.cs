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
    }
    internal sealed class SystemTransferClipboard : ITransferClipboard
    {
        public uint Sequence { get { return Native.GetClipboardSequenceNumber(); } }
        public IDisposable Backup() { return ClipboardBackup.Capture(); }
        public uint Stage(object payload, uint expected) { return NativeClipboard.Stage((DataObject)payload, expected); }
        public void Restore(IDisposable backup, uint expected) { ((ClipboardBackup)backup).Restore(expected); }
    }

    internal sealed class TransferEngine
    {
        private readonly ITransferClipboard clipboard;
        private readonly Action<bool> paste;
        private readonly Func<int, Task> delay;
        internal bool Busy { get; private set; }
        internal TransferEngine(ITransferClipboard clipboard, Action<bool> paste, Func<int, Task> delay)
        { this.clipboard = clipboard; this.paste = paste; this.delay = delay; }
        internal async Task Deliver(object payload, IDestination target, int waitMilliseconds, bool windowsKey, bool restoreAfter,
            CancellationToken cancellation)
        {
            if (Busy) throw new InvalidOperationException("正在同步，请等待本次完成。");
            if (target == null || !target.IsAlive) throw new InvalidOperationException("请先连接 UU 或 ToDesk，并选好远端输入框。");
            Busy = true;
            IDisposable backup = null; uint stagedSequence = 0; bool staged = false, succeeded = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                uint before = clipboard.Sequence;
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
                    await delay(2000);
                    if (clipboard.Sequence == stagedSequence) clipboard.Restore(backup, stagedSequence);
                }
            }
            finally
            {
                try
                {
                    if (!succeeded && staged && clipboard.Sequence == stagedSequence && backup != null)
                    {
                        try { clipboard.Restore(backup, stagedSequence); }
                        catch (System.Runtime.InteropServices.ExternalException) { /* Retain staged content if clipboard is locked. */ }
                    }
                }
                finally { try { if (backup != null) backup.Dispose(); } finally { Busy = false; } }
            }
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
