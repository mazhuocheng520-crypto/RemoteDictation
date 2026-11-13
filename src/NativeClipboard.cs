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
        private static NativeWindow owner;
        internal sealed class Entry : IDisposable
        {
            internal uint Format;
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

        internal static ClipboardBackup Capture()
        {
            ClipboardBackup snapshot = new ClipboardBackup();
            if (!OpenClipboard(Owner)) throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。");
            try
            {
                long total = 0; uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    if (snapshot.Entries.Count >= 128) throw new InvalidOperationException("原剪贴板格式过多，无法安全备份。");
                    string name = FormatName(format);
                    if (IsTransportMetadata(name)) continue;
                    if (IsEmbeddedObject(name) || format == 3 || format == 9 || format == 14 || format >= 0x80 && format < 0x300 || format >= 0x300 && format <= 0x3FF)
                        throw new InvalidOperationException("原剪贴板含有无法安全备份的对象。请先复制一段普通文字，再重试。");
                    IntPtr handle = GetClipboardData(format);
                    if (handle == IntPtr.Zero) throw new InvalidOperationException("原剪贴板内容暂时无法读取。");
                    Entry entry = new Entry { Format = format };
                    if (format == 2)
                    {
                        using (Bitmap source = System.Drawing.Image.FromHbitmap(handle))
                        {
                            long bytes = (long)source.Width * source.Height * 4;
                            if (bytes + total > Policy.MaximumBytes) throw new InvalidOperationException("原剪贴板图片超过安全备份上限 16 MiB。");
                            entry.Bitmap = new Bitmap(source); total += bytes;
                        }
                    }
                    else
                    {
                        ulong bytes = GlobalSize(handle).ToUInt64();
                        if (bytes == 0 || bytes > (ulong)Policy.MaximumBytes || (ulong)total + bytes > (ulong)Policy.MaximumBytes)
                            throw new InvalidOperationException("原剪贴板过大或含有无法安全备份的格式，原内容已保留。");
                        IntPtr pointer = GlobalLock(handle);
                        if (pointer == IntPtr.Zero) throw new InvalidOperationException("无法安全读取原剪贴板。");
                        try { entry.Bytes = new byte[(int)bytes]; Marshal.Copy(pointer, entry.Bytes, 0, entry.Bytes.Length); }
                        finally { GlobalUnlock(handle); }
                        total += (long)bytes;
                    }
                    snapshot.Entries.Add(entry);
                }
                snapshot.Sequence = Native.GetClipboardSequenceNumber();
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
            finally { CloseClipboard(); }
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
                    else if (value is Bitmap) entry.Bitmap = new Bitmap((Bitmap)value);
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
                    if (entry.Bitmap != null) handle = entry.Bitmap.GetHbitmap();
                    else
                    {
                        handle = GlobalAlloc(2, new UIntPtr((uint)entry.Bytes.Length));
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
                if (!OpenClipboard(Owner)) return false;
                opened = true;
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
                    if (allocated[i] != IntPtr.Zero) { if (entries[i].Bitmap != null) DeleteObject(allocated[i]); else GlobalFree(allocated[i]); }
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
