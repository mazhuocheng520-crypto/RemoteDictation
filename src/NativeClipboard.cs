using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal sealed class ClipboardMutationException : InvalidOperationException
    {
        internal readonly uint Sequence;
        internal ClipboardMutationException(string message, uint sequence, Exception inner) : base(message, inner) { Sequence = sequence; }
    }
    // Clipboard sequence checks and writes share one native OpenClipboard lock.
    // In particular, restoration never retries an OLE write after a user has copied new data.
    internal static class NativeClipboard
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder name, int count);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, byte[] buffer);
        [DllImport("gdi32.dll")] private static extern IntPtr SetEnhMetaFileBits(uint size, byte[] buffer);
        [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr metafile);
        private static NativeWindow owner;
        internal enum EntryKind { Memory, Bitmap, EnhancedMetafile }
        internal sealed class Entry : IDisposable
        {
            internal uint Format;
            internal EntryKind Kind;
            internal byte[] Bytes;
            internal Bitmap Bitmap;
            public void Dispose() { if (Bitmap != null) Bitmap.Dispose(); }
        }
        private static IntPtr Owner
        {
            get
            {
                if (owner == null) { owner = new NativeWindow(); owner.CreateHandle(new CreateParams { Caption = "RemoteDictation.Clipboard", Parent = new IntPtr(-3) }); }
                return owner.Handle;
            }
        }
        private static string FormatName(uint format)
        { StringBuilder name = new StringBuilder(256); GetClipboardFormatName(format, name, name.Capacity); return name.ToString(); }
        private static bool IsTransportMetadata(string name)
        { return name == "Ole Private Data" || name == "DataObject"; }
        private static bool IsEmbeddedObject(string name)
        { return name == "Embed Source" || name == "Embedded Object" || name == "Link Source" || name == "Object Descriptor" || name == "Link Source Descriptor"; }

        // 粘贴后必须还原原剪贴板，所以备份要尽量完整：
        // - 只备份“原始”格式，系统会从它自动生成的格式（文本的 ANSI 版、位图的其他版本、调色板）不备份，还原后系统会再生成；
        // - 普通数据（文字、RTF、HTML、DIB 图片、文件列表、Office 的各种格式）按内存块完整复制；
        // - 增强图元文件单独复制；GDI 私有句柄类格式无法跨进程复制，跳过（极少见）。
        internal const long MaximumBackupBytes = 256L * 1024 * 1024;
        private static bool Synthesized(uint format, HashSet<uint> present)
        {
            if ((format == 1 || format == 7) && present.Contains(13)) return true;      // CF_TEXT/CF_OEMTEXT 由 Unicode 文字生成
            if (format == 2 && (present.Contains(8) || present.Contains(17))) return true; // CF_BITMAP 由 DIB 生成
            if (format == 8 && present.Contains(17)) return true;                         // CF_DIB 与 CF_DIBV5 互相生成
            if (format == 9) return true;                                                 // CF_PALETTE
            if (format == 3) return true;                                                 // CF_METAFILEPICT 含句柄，由 EMF 生成
            if (format >= 0x80 && format <= 0x8F) return true;                            // 所有者绘制/显示格式
            if (format >= 0x200 && format <= 0x3FF) return true;                          // 私有/GDI 句柄格式
            return false;
        }
        internal static ClipboardBackup Capture()
        {
            ClipboardBackup snapshot = new ClipboardBackup();
            bool opened = false;
            for (int attempt = 0; attempt < 6 && !opened; attempt++) { opened = OpenClipboard(Owner); if (!opened) System.Threading.Thread.Sleep(50); }
            if (!opened) throw new InvalidOperationException("剪贴板正被其他程序占用，为保证能还原，本次没有粘贴。文字仍保留。");
            try
            {
                List<uint> formats = new List<uint>(); uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0) formats.Add(format);
                HashSet<uint> present = new HashSet<uint>(formats);
                long total = 0;
                foreach (uint id in formats)
                {
                    if (IsTransportMetadata(FormatName(id)) || Synthesized(id, present)) continue;
                    IntPtr handle = GetClipboardData(id);
                    if (handle == IntPtr.Zero) continue;
                    Entry entry = new Entry { Format = id };
                    if (id == 14)
                    {
                        uint size = GetEnhMetaFileBits(handle, 0, null);
                        if (size == 0) continue;
                        entry.Bytes = new byte[size]; GetEnhMetaFileBits(handle, size, entry.Bytes); entry.Kind = EntryKind.EnhancedMetafile;
                    }
                    else if (id == 2)
                    {
                        using (Bitmap source = System.Drawing.Image.FromHbitmap(handle)) entry.Bitmap = new Bitmap(source);
                        entry.Kind = EntryKind.Bitmap;
                        total += (long)entry.Bitmap.Width * entry.Bitmap.Height * 4;
                    }
                    else
                    {
                        ulong bytes = GlobalSize(handle).ToUInt64();
                        if (bytes == 0) continue;
                        if ((ulong)total + bytes > (ulong)MaximumBackupBytes)
                            throw new InvalidOperationException("原剪贴板内容超过 256 MB，为保证能还原，本次没有粘贴。文字仍保留。");
                        IntPtr pointer = GlobalLock(handle);
                        if (pointer == IntPtr.Zero) continue;
                        try { entry.Bytes = new byte[(long)bytes]; Marshal.Copy(pointer, entry.Bytes, 0, entry.Bytes.Length); }
                        finally { GlobalUnlock(handle); }
                    }
                    if (entry.Bytes != null) total += entry.Bytes.Length;
                    if (total > MaximumBackupBytes) throw new InvalidOperationException("原剪贴板内容超过 256 MB，为保证能还原，本次没有粘贴。文字仍保留。");
                    snapshot.Entries.Add(entry);
                }
                snapshot.Sequence = Native.GetClipboardSequenceNumber();
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
            finally { CloseClipboard(); }
        }
        // 必须在界面线程先建好剪贴板所有者窗口：它要能收消息，否则别的程序清空剪贴板时会被卡住。
        internal static void Prepare() { IntPtr created = Owner; }
        // 只问“现在剪贴板是谁的”，不读内容，绝不会触发延迟渲染而卡住。
        internal static int OwnerProcessId()
        {
            IntPtr owner = GetClipboardOwner(); if (owner == IntPtr.Zero) return 0;
            uint pid; Native.GetWindowThreadProcessId(owner, out pid); return (int)pid;
        }
        private static List<Entry> Serialize(DataObject payload)
        {
            List<Entry> entries = new List<Entry>();
            try
            {
                foreach (string format in payload.GetFormats(false))
                {
                    uint id = format == DataFormats.UnicodeText ? 13u : format == DataFormats.Bitmap ? 2u : RegisterClipboardFormat(format);
                    if (id == 0) throw new InvalidOperationException("无法注册剪贴板格式。");
                    object value = payload.GetData(format, false);
                    Entry entry = new Entry { Format = id };
                    if (value is string) entry.Bytes = Encoding.Unicode.GetBytes((string)value + "\0");
                    else if (value is MemoryStream) entry.Bytes = ((MemoryStream)value).ToArray();
                    else if (value is Bitmap) { entry.Bitmap = new Bitmap((Bitmap)value); entry.Kind = EntryKind.Bitmap; }
                    else throw new InvalidOperationException("不支持的粘贴内容格式。");
                    entries.Add(entry);
                }
                return entries;
            }
            catch { foreach (Entry entry in entries) entry.Dispose(); throw; }
        }
        internal static uint Stage(DataObject payload, uint expected)
        {
            List<Entry> entries = Serialize(payload);
            try
            {
                uint result;
                if (!Write(entries, expected, out result)) throw new InvalidOperationException("剪贴板已改变或正被占用，本次已取消。");
                return result;
            }
            finally { foreach (Entry entry in entries) entry.Dispose(); }
        }
        internal static bool Write(List<Entry> entries, uint expected, out uint sequence)
        {
            sequence = 0;
            List<IntPtr> allocated = new List<IntPtr>(); bool opened = false, changed = false, written = false;
            Exception failure = null;
            try
            {
                // Allocate all native objects before changing clipboard contents.
                foreach (Entry entry in entries)
                {
                    IntPtr handle;
                    if (entry.Kind == EntryKind.Bitmap) handle = entry.Bitmap.GetHbitmap();
                    else if (entry.Kind == EntryKind.EnhancedMetafile) handle = SetEnhMetaFileBits((uint)entry.Bytes.Length, entry.Bytes);
                    else
                    {
                        handle = GlobalAlloc(2, new UIntPtr((ulong)Math.Max(1, entry.Bytes.Length)));
                        if (handle != IntPtr.Zero)
                        {
                            allocated.Add(handle);
                            IntPtr pointer = GlobalLock(handle);
                            if (pointer == IntPtr.Zero) throw new InvalidOperationException("无法分配剪贴板内存。");
                            try { Marshal.Copy(entry.Bytes, 0, pointer, entry.Bytes.Length); }
                            finally { GlobalUnlock(handle); }
                            continue;
                        }
                    }
                    if (handle == IntPtr.Zero) throw new InvalidOperationException("无法分配剪贴板内存。");
                    allocated.Add(handle);
                }
                // 远控软件等剪贴板监听程序会在内容变化后短暂占用剪贴板，稍等重试。
                for (int attempt = 0; attempt < 10 && !opened; attempt++) { opened = OpenClipboard(Owner); if (!opened) System.Threading.Thread.Sleep(50); }
                if (!opened) return false;
                if (Native.GetClipboardSequenceNumber() != expected) return false;
                if (!EmptyClipboard()) throw new InvalidOperationException("无法更新剪贴板。");
                changed = true;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (SetClipboardData(entries[i].Format, allocated[i]) == IntPtr.Zero)
                        throw new InvalidOperationException("无法写入完整剪贴板数据。");
                    allocated[i] = IntPtr.Zero; // Windows now owns this allocation.
                }
                written = true;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (opened) CloseClipboard();
                for (int i = 0; i < allocated.Count; i++)
                    if (allocated[i] != IntPtr.Zero)
                    {
                        if (entries[i].Kind == EntryKind.Bitmap) DeleteObject(allocated[i]);
                        else if (entries[i].Kind == EntryKind.EnhancedMetafile) DeleteEnhMetaFile(allocated[i]);
                        else GlobalFree(allocated[i]);
                    }
            }
            // Windows adds synthesized clipboard formats when CloseClipboard runs.
            // Use the committed generation, and verify we still own it before returning.
            if (changed)
            {
                sequence = Native.GetClipboardSequenceNumber();
                if (GetClipboardOwner() != Owner)
                    throw new InvalidOperationException("剪贴板已由其他程序更新，本次已取消。");
                if (failure != null) throw new ClipboardMutationException(failure.Message, sequence, failure);
            }
            if (failure != null) throw failure;
            return written;
        }
    }
}
