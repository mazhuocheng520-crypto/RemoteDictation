using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal static class Native
    {
        internal const int WM_BRIDGE_EXIT = 0x802A;
        internal const int WM_BRIDGE_SHOW = 0x802B;
        internal const int SW_RESTORE = 9;
        internal static readonly UIntPtr InputMarker = new UIntPtr(0x44425642);
        internal delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
        internal delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int type, KeyboardProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr info);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData
        { internal uint key, scanCode, flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        { internal ushort key, scan; internal uint flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { internal int x, y; internal uint data, flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] internal KeyboardInput keyboard;
            [FieldOffset(0)] internal MouseInput mouse;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        { internal uint type; internal InputUnion data; }

        internal static bool KeyDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        internal static bool ActivateOwnWindow(IntPtr handle)
        {
            uint pid; GetWindowThreadProcessId(handle, out pid);
            if (pid != Process.GetCurrentProcess().Id) return false;
            ShowWindow(handle, SW_RESTORE); SetForegroundWindow(handle);
            if (GetForegroundWindow() == handle) return true;
            uint ignored; uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            uint currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try { SetForegroundWindow(handle); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
            return GetForegroundWindow() == handle;
        }
        // 切到别的程序的窗口：先正常请求；被系统前台锁拒绝时，临时挂接当前前台线程和目标线程的输入队列再试。
        internal static bool ActivateForeign(IntPtr handle)
        {
            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
            if (GetForegroundWindow() == handle) return true;
            uint ignored;
            uint current = GetCurrentThreadId();
            uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            uint targetThread = GetWindowThreadProcessId(handle, out ignored);
            bool attachedForeground = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
            bool attachedTarget = targetThread != 0 && targetThread != current && targetThread != foreground && AttachThreadInput(current, targetThread, true);
            try { BringWindowToTop(handle); SetForegroundWindow(handle); }
            finally
            {
                if (attachedTarget) AttachThreadInput(current, targetThread, false);
                if (attachedForeground) AttachThreadInput(current, foreground, false);
            }
            return GetForegroundWindow() == handle;
        }
        internal static bool ModifiersHeld()
        {
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if (KeyDown(key)) return true;
            return false;
        }
        internal static Input Key(ushort key, bool up)
        {
            Input item = new Input(); item.type = 1;
            uint extended = key == 0xA3 || key == 0xA5 || key == 0x5B || key == 0x5C ? 1u : 0u;
            // 远控客户端可能按扫描码转发按键，因此同时填写扫描码；标志位保持不变。
            ushort scan = (ushort)(MapVirtualKey(key, 0) & 0xFF);
            item.data.keyboard = new KeyboardInput { key = key, scan = scan, flags = (up ? 2u : 0u) | extended, extra = InputMarker };
            return item;
        }
        // 粘贴键：0 = Ctrl+V，1 = Win+V（远控软件再把它映射成远端的 Command+V / Ctrl+V）。
        internal static Input[] PasteEvents(int pasteKey)
        {
            ushort modifier = pasteKey == 1 ? (ushort)0x5B : (ushort)0xA2;
            return new[] { Key(modifier, false), Key(0x56, false), Key(0x56, true), Key(modifier, true) };
        }
        // 补发被暂扣的按键（带本程序标记，自己的监听会忽略）。用于远控里 Alt+其他键 的组合仍能发到 Mac。
        internal static void Replay(params Input[] events)
        { SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Input))); }
        internal static Input RawKey(uint key, uint scan, bool extended, bool up)
        {
            Input item = new Input(); item.type = 1;
            item.data.keyboard = new KeyboardInput { key = (ushort)key, scan = (ushort)scan, flags = (up ? 2u : 0u) | (extended ? 1u : 0u), extra = InputMarker };
            return item;
        }
        internal static void Paste(int pasteKey)
        {
            if (ModifiersHeld()) throw new InvalidOperationException("请先松开 Ctrl、Alt、Shift 和 Win 键，再粘贴。");
            Input[] events = PasteEvents(pasteKey);
            uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Input)));
            if (sent == events.Length) return;
            // Always release both keys after a partial SendInput; do not leave a modifier down.
            Input[] release = { events[2], events[3] };
            SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("Windows 未能发送完整粘贴按键。请确认远控软件与本程序权限级别相同。");
        }
        internal static string WindowTitle(IntPtr hwnd)
        { StringBuilder value = new StringBuilder(512); GetWindowText(hwnd, value, value.Capacity); return value.ToString(); }
        internal static bool SignalRunningBridge(int message)
        {
            bool found = false;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (!WindowTitle(hwnd).StartsWith("远控听写 · Windows", StringComparison.Ordinal)) return true;
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                try
                {
                    using (Process process = Process.GetProcessById((int)pid))
                        if (process.ProcessName == "RemoteDictation")
                            found |= PostMessage(hwnd, (uint)message, IntPtr.Zero, IntPtr.Zero);
                }
                catch (ArgumentException) { }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        internal static int CountArmedBridges()
        {
            int count = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            { if (WindowTitle(hwnd).StartsWith("远控听写 · Windows", StringComparison.Ordinal) && GetProp(hwnd, "RemoteDictation.HoldEnabled") == new IntPtr(1)) count++; return true; }, IntPtr.Zero);
            return count;
        }
        internal static int CountHoldTriggers()
        {
            int count = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            { if (WindowTitle(hwnd).StartsWith("远控听写 · Windows", StringComparison.Ordinal)) count += GetProp(hwnd, "RemoteDictation.HoldTriggers").ToInt32(); return true; }, IntPtr.Zero);
            return count;
        }
    }

    internal interface IDestination
    {
        bool IsAlive { get; }
        bool IsFocused { get; }
        void Activate();
    }

    internal sealed class RemoteWindow : IDestination
    {
        internal IntPtr Handle;
        internal int ProcessId;
        internal long StartTicks;
        internal string Title;
        internal string ProviderName;
        internal string ProcessName;
        private bool testWindow;
        private double verifiedAt = double.MinValue;
        // 按进程名识别远控客户端窗口（不含 .exe）。第一个是显示名，其余是进程名。
        // 只有 ToDesk 经过实测；其余按同样原理适配。其他软件可在设置文件 ExtraRemoteProcesses 里加进程名。
        internal static readonly string[][] KnownClients =
        {
            new[] { "ToDesk", "ToDesk" },
            new[] { "UU远程", "GameViewer", "UURemote", "UU_Remote" },
            new[] { "向日葵", "SunloginClient", "AweSun" },
            new[] { "RustDesk", "rustdesk" },
            new[] { "AnyDesk", "AnyDesk" },
            new[] { "TeamViewer", "TeamViewer" },
            new[] { "Parsec", "parsecd" },
            new[] { "远程桌面", "mstsc", "msrdc" },
            new[] { "VNC Viewer", "vncviewer", "tvnviewer" },
            new[] { "NoMachine", "nxplayer", "nxplayer.bin" },
            new[] { "AnyViewer", "AnyViewer" },
            new[] { "Splashtop", "strwinclt" },
            new[] { "Moonlight", "Moonlight" },
        };
        internal static string[] ExtraProcesses = new string[0];
        internal static string ProviderForProcess(string name)
        {
            foreach (string[] client in KnownClients)
                for (int i = 1; i < client.Length; i++)
                    if (string.Equals(name, client[i], StringComparison.OrdinalIgnoreCase)) return client[0];
            foreach (string extra in ExtraProcesses)
                if (!string.IsNullOrWhiteSpace(extra) && string.Equals(name, extra.Trim().Replace(".exe", ""), StringComparison.OrdinalIgnoreCase)) return extra.Trim();
            return null;
        }
        internal static bool IsSupportedProcess(string name) { return ProviderForProcess(name) != null; }
        internal static RemoteWindow Capture(IntPtr handle, bool allowOwnTestWindow)
        {
            if (handle == IntPtr.Zero || !Native.IsWindow(handle)) return null;
            handle = Native.GetAncestor(handle, 2);
            uint pid; Native.GetWindowThreadProcessId(handle, out pid);
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                {
                    bool ownTest = allowOwnTestWindow && process.Id == Process.GetCurrentProcess().Id;
                    string provider = ownTest ? "本机测试" : ProviderForProcess(process.ProcessName);
                    if (provider == null) return null;
                    return new RemoteWindow { Handle = handle, ProcessId = (int)pid,
                        StartTicks = process.StartTime.ToUniversalTime().Ticks, Title = Native.WindowTitle(handle),
                        ProviderName = provider, ProcessName = process.ProcessName, testWindow = ownTest };
                }
            }
            catch (ArgumentException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        internal static List<RemoteWindow> FindAll()
        {
            List<RemoteWindow> result = new List<RemoteWindow>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (Native.IsWindowVisible(hwnd))
                {
                    RemoteWindow item = Capture(hwnd, false);
                    if (item != null && !string.IsNullOrWhiteSpace(item.Title)) result.Add(item);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        public bool IsAlive
        {
            get
            {
                if (!Native.IsWindow(Handle)) return false;
                uint pid; Native.GetWindowThreadProcessId(Handle, out pid);
                if (pid != ProcessId) return false;
                double now = HoldClock.Now;
                if (now - verifiedAt < 2000) return true;
                RemoteWindow current = Capture(Handle, testWindow);
                bool same = current != null && current.ProcessId == ProcessId && current.StartTicks == StartTicks;
                if (same) verifiedAt = now;
                return same;
            }
        }
        public bool IsFocused { get { return IsAlive && Native.GetForegroundWindow() == Handle; } }
        public void Activate()
        {
            if (!IsAlive) throw new InvalidOperationException("原远控窗口已关闭，请重新选择远控窗口。");
            if (testWindow) { if (Native.IsIconic(Handle)) Native.ShowWindow(Handle, Native.SW_RESTORE); Native.SetForegroundWindow(Handle); return; }
            Native.ActivateForeign(Handle);
        }
        public override string ToString() { return ProviderName + " · " + Title; }
    }
}
