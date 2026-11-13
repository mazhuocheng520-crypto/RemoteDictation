using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("远控听写")]
[assembly: AssemblyDescription("在远程控制画面里按住输入法语音键说话，松开粘贴到远端")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyCopyright("MIT License. Based on DoubaoVoiceBridge (c) 2026 yupeng316888-create.")]

namespace RemoteDictation
{
    internal static class Program
    {
        internal const string Version = "1.0.1";
        [STAThread]
        private static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 1 && args[0] == "--shutdown") return Native.SignalRunningBridge(Native.WM_BRIDGE_EXIT) ? 0 : 1;
            if (args.Length == 2 && args[0] == "--autostart")
            {
                try { if (args[1] == "on") AutoStart.Enable(); else AutoStart.Disable(); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "--diagnostics")
            {
                var windows = RemoteWindow.FindAll();
                int detectedVoiceKey; string voiceMessage;
                bool voiceReadable = DoubaoShortcut.TryRead(out detectedVoiceKey, out voiceMessage);
                File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new {
                    appVersion = Version, executable = Assembly.GetExecutingAssembly().Location, elevated = AutoStart.Elevated,
                    autoStart = AutoStart.Enabled, supportedRemoteWindows = windows.Count,
                    toDeskWindows = windows.FindAll(delegate(RemoteWindow item) { return item.ProviderName == "ToDesk"; }).Count,
                    holdArmedInstances = Native.CountArmedBridges(), holdTriggers = Native.CountHoldTriggers(),
                    doubaoVoiceConfigurationReadable = voiceReadable, detectedVoiceKeyIndex = detectedVoiceKey
                })); return 0;
            }
            if (args.Length == 2 && (args[0] == "--integration-test" || args[0] == "--hold-smoke"))
            {
                MainForm preview = new MainForm(true);
                preview.Shown += async delegate
                {
                    try
                    {
                        SelfTests.FocusOwnedWindow(preview, null);
                        await System.Threading.Tasks.Task.Delay(300);
                        if (args[0] == "--integration-test") await SelfTests.Integration(preview, args[1]);
                        if (args[0] == "--hold-smoke") await SelfTests.HoldIntegration(preview, args[1]);
                    }
                    catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "ui-error.txt"), error.ToString()); Environment.ExitCode = 1; }
                    finally { preview.Close(); }
                };
                Application.Run(preview); return Environment.ExitCode;
            }
            // ToDesk 以管理员权限运行；本程序权限更低时收不到它前台的按键，也发不了粘贴。
            // 手动双击启动且已设置开机自启时，改由计划任务以最高权限启动，不弹 UAC。
            if (args.Length == 0)
            {
                if (Native.SignalRunningBridge(Native.WM_BRIDGE_SHOW)) return 0;
                if (!AutoStart.Elevated && AutoStart.Enabled && AutoStart.RunNow()) return 0;
            }
            bool created; Mutex mutex;
            try { mutex = new Mutex(true, "Local\\RemoteDictation.v1", out created); }
            catch (UnauthorizedAccessException) { Native.SignalRunningBridge(Native.WM_BRIDGE_SHOW); return 0; }
            using (mutex)
            {
                if (!created) { Native.SignalRunningBridge(Native.WM_BRIDGE_SHOW); return 0; }
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                { MessageBox.Show("程序遇到错误：" + e.Exception.Message + "\n请关闭后重新打开。", "远控听写"); };
                Application.Run(new MainForm(false));
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }

    internal static class AppIcon
    {
        // app.ico 由 tools/MakeIcon.cs 生成，编译时同时作为程序图标和内嵌资源。
        internal static Icon Load(int size)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteDictation.app.ico"))
                    if (stream != null) return new Icon(stream, new Size(size, size));
            }
            catch { }
            return SystemIcons.Application;
        }
    }

    // 开机自启：任务计划程序“登录时、最高权限”启动，后台进托盘。不用启动文件夹，因为那样只能普通权限运行。
    internal static class AutoStart
    {
        private const string TaskName = "RemoteDictation";
        internal static bool Elevated
        { get { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        internal static bool Enabled { get { return Schtasks("/Query /TN " + TaskName) == 0; } }
        internal static bool RunNow() { return Schtasks("/Run /TN " + TaskName) == 0; }
        internal static void Disable() { if (Enabled && Schtasks("/Delete /TN " + TaskName + " /F") != 0) throw new InvalidOperationException("无法取消开机自启。"); }
        internal static void Enable()
        {
            if (!Elevated) throw new InvalidOperationException("设置开机自启需要管理员权限运行本程序。");
            string exe = Assembly.GetExecutingAssembly().Location, user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>RemoteDictation：登录后在托盘运行</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><WorkingDirectory>" +
                SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
            string file = Path.Combine(Path.GetTempPath(), "RemoteDictation-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { if (Schtasks("/Create /TN " + TaskName + " /XML \"" + file + "\" /F") != 0) throw new InvalidOperationException("无法设置开机自启。"); }
            finally { try { File.Delete(file); } catch { } }
        }
        private static int Schtasks(string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(info)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(10000); return process.HasExited ? process.ExitCode : -1; }
        }
    }

    internal static class Memory
    {
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
        // 空闲时把不用的内存页交还系统，任务管理器里显示的占用会明显变小；下次用到时系统再调回。
        internal static void Trim()
        { try { GC.Collect(); GC.WaitForPendingFinalizers(); SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { } }
    }
}
