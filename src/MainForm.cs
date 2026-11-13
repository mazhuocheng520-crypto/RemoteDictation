using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal sealed class Settings
    {
        public int PasteKey { get; set; }
        public int VoiceKey { get; set; }
        public bool AutoPaste { get; set; }
        public bool RestoreClipboard { get; set; }
        public int TextDelay { get; set; }
        public int ImageDelay { get; set; }
        public bool OneKeyEnabled { get; set; }
        public bool StartInTray { get; set; }
        public bool AutoDetectVoice { get; set; }
        public bool DiagnosticLog { get; set; }
        public string[] ExtraRemoteProcesses { get; set; }
        public Dictionary<string, int> PasteKeyByApp { get; set; }
        public Settings() { ExtraRemoteProcesses = new string[0]; PasteKeyByApp = new Dictionary<string, int>(); TextDelay = 800; ImageDelay = 10000; OneKeyEnabled = true; StartInTray = true; AutoDetectVoice = true; }
    }

    // 平时只在托盘运行。这个窗口只做状态和少量设置：听写开关、开机自启、粘贴键、退出。截图用远控软件自带功能。
    // 远控窗口在按住语音键时自动识别，豆包语音键自动读取，改动即时保存。
    internal sealed class MainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(14, 18, 27);
        private static readonly Color Card = Color.FromArgb(22, 28, 40);
        private static readonly Color TextColor = Color.FromArgb(241, 245, 249);
        private static readonly Color Muted = Color.FromArgb(148, 163, 184);
        private static readonly Color Cyan = Color.FromArgb(34, 211, 238);
        private static readonly Color Amber = Color.FromArgb(251, 191, 36);
        private static readonly string[] VoiceKeyNames = { "左 Alt", "右 Alt", "左 Ctrl", "右 Ctrl" };
        private static readonly int[] VoiceKeyCodes = { 0xA4, 0xA5, 0xA2, 0xA3 };

        private readonly CheckBox enabledBox = new CheckBox(), autoStartBox = new CheckBox();
        private readonly ComboBox pasteKey = new ComboBox(), voiceKey = new ComboBox();
        private readonly Label status = new Label(), targetLabel = new Label();
        private readonly Button exitButton;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem pauseItem;
        private readonly TransferEngine engine;
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer { Interval = 8000 };
        private readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge-settings.json");
        private readonly bool smokeMode;
        private Settings settings = new Settings();
        private OneKeyController oneKey;
        private RemoteWindow lastTarget;
        private string lastExternalProcess;
        private readonly ToolStripMenuItem markItem = new ToolStripMenuItem(), pasteMenu = new ToolStripMenuItem();
        private readonly ScreenPasteProbe probe = new ScreenPasteProbe();
        private readonly PasteNotice notice = new PasteNotice();
        private string pendingText, pendingApp; private RemoteWindow pendingTarget; private int pendingKey;
        private static readonly string[] PasteKeyNames = { "Ctrl+V", "Win+V" };
                private int voiceKeyIndex;
        private bool voiceConfigurationValid, exitRequested, closeRequested, loading, hookFailed;
        private bool Busy { get { return engine.Busy || oneKey != null && oneKey.Busy; } }

        internal MainForm(bool smokeMode)
        {
            this.smokeMode = smokeMode;
            Text = "远控听写 · Windows " + Program.Version; Icon = AppIcon.Load(32);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 372); BackColor = Background; ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 10);
            engine = new TransferEngine(new SystemTransferClipboard(), Native.Paste, Task.Delay);

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(22, 18, 22, 14), BackColor = Background };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "远控听写", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = TextColor, AutoSize = true, Margin = new Padding(0) });
            status.Dock = DockStyle.Fill; status.ForeColor = Cyan; status.Text = "正在启动…"; status.Padding = new Padding(0, 4, 0, 0);
            layout.Controls.Add(status);

            enabledBox.Text = "在远控画面里按住语音键听写"; enabledBox.AutoSize = true; enabledBox.Checked = true; enabledBox.ForeColor = TextColor;
            enabledBox.CheckedChanged += delegate { if (!loading) { ApplyEnabled(); SaveSettings(); } };
            layout.Controls.Add(enabledBox);
            autoStartBox.Text = "开机自动启动（后台托盘运行）"; autoStartBox.AutoSize = true; autoStartBox.ForeColor = TextColor;
            autoStartBox.CheckedChanged += delegate { if (!loading) ApplyAutoStart(); };
            layout.Controls.Add(autoStartBox);

            TableLayoutPanel card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, BackColor = Card, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 4, 0, 4) };
            for (int i = 0; i < 3; i++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Controls.Add(Caption("语音键"), 0, 0);
            voiceKey.DropDownStyle = ComboBoxStyle.DropDownList; voiceKey.FlatStyle = FlatStyle.Flat; voiceKey.Width = 200; voiceKey.DropDownWidth = 260;
            voiceKey.Margin = new Padding(0, 2, 0, 2); voiceKey.BackColor = Card; voiceKey.ForeColor = TextColor;
            voiceKey.Items.AddRange(new object[] { "自动（读取豆包设置）", "左 Alt", "右 Alt", "左 Ctrl", "右 Ctrl" }); voiceKey.SelectedIndex = 0;
            voiceKey.SelectedIndexChanged += delegate { if (!loading) { settings.AutoDetectVoice = voiceKey.SelectedIndex == 0; if (voiceKey.SelectedIndex > 0) settings.VoiceKey = voiceKey.SelectedIndex - 1; ReadVoiceKey(); ApplyEnabled(); SaveSettings(); } };
            card.Controls.Add(voiceKey, 1, 0);
            card.Controls.Add(Caption("远控窗口"), 0, 1); targetLabel.AutoSize = true; targetLabel.Margin = new Padding(0, 4, 0, 4); targetLabel.ForeColor = TextColor; card.Controls.Add(targetLabel, 1, 1);
            card.Controls.Add(Caption("粘贴键"), 0, 2);
            pasteKey.DropDownStyle = ComboBoxStyle.DropDownList; pasteKey.FlatStyle = FlatStyle.Flat; pasteKey.Width = 200; pasteKey.DropDownWidth = 320; pasteKey.Margin = new Padding(0, 2, 0, 2);
            pasteKey.BackColor = Card; pasteKey.ForeColor = TextColor;
            pasteKey.Items.AddRange(new object[] { "Ctrl+V 先试", "Win+V 先试" }); pasteKey.SelectedIndex = 0;
            pasteKey.SelectedIndexChanged += delegate { if (!loading) SaveSettings(); };
            card.Controls.Add(pasteKey, 1, 2);
            layout.Controls.Add(card);

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            exitButton = Action("退出程序", false, delegate { exitRequested = true; Close(); });
            actions.Controls.Add(exitButton);
            layout.Controls.Add(actions);
            layout.Controls.Add(new Label { Text = "关闭此窗口后仍在托盘运行。截图请用远控软件自带的功能。", ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(396, 0) });

            tray = new NotifyIcon { Icon = AppIcon.Load(16), Text = "远控听写", Visible = !smokeMode };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开设置", null, delegate { ShowSettings(); });
            pauseItem = new ToolStripMenuItem("暂停听写", null, delegate { enabledBox.Checked = !enabledBox.Checked; });
            menu.Items.Add(pauseItem);
            menu.Items.Add("查看上次听写文字", null, delegate { if (oneKey != null) oneKey.ReviewPending(); });
            markItem.Click += delegate { ToggleRemoteProcess(lastExternalProcess); };
            menu.Items.Add(markItem);
            for (int i = -1; i < 2; i++)
            {
                int choice = i;
                ToolStripMenuItem option = new ToolStripMenuItem(choice < 0 ? "自动识别（试出来后记住）" : PasteKeyNames[choice]);
                option.Click += delegate { SetAppPasteKey(lastExternalProcess, choice); };
                pasteMenu.DropDownItems.Add(option);
            }
            menu.Items.Add(pasteMenu);
            menu.Opening += delegate
            {
                string name = lastExternalProcess;
                markItem.Visible = !string.IsNullOrEmpty(name);
                pasteMenu.Visible = !string.IsNullOrEmpty(name) && RemoteWindow.ProviderForProcess(name) != null;
                if (string.IsNullOrEmpty(name)) return;
                pasteMenu.Text = "「" + name + "」的粘贴键";
                int stored = -1; bool has = settings.PasteKeyByApp != null && settings.PasteKeyByApp.TryGetValue(name, out stored);
                for (int i = 0; i < pasteMenu.DropDownItems.Count; i++)
                    ((ToolStripMenuItem)pasteMenu.DropDownItems[i]).Checked = has ? i - 1 == stored : i == 0;
                bool builtIn = RemoteWindow.ProviderForProcess(name) != null && !IsExtra(name);
                markItem.Enabled = !builtIn;
                markItem.Text = builtIn ? "「" + name + "」已是支持的远控软件" : IsExtra(name) ? "不再把「" + name + "」当作远控软件" : "把「" + name + "」当作远控软件";
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, delegate { exitRequested = true; Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowSettings(); };

            refresh.Tick += delegate { if (Visible) UpdateTargetLabel(); };
            watch.Tick += delegate { RememberExternalForeground(); };
            trim.Tick += delegate { trim.Stop(); if (!Busy && !Visible) Memory.Trim(); };
            Shown += delegate
            {
                UpdateTargetLabel();
                if (smokeMode) return;
                LoadSettings(); ReadVoiceKey(); InitializeOneKey(); refresh.Start(); trim.Start(); watch.Start();
                loading = true; try { autoStartBox.Checked = AutoStart.Enabled; } catch { } finally { loading = false; }
                if (settings.StartInTray && oneKey != null && !hookFailed)
                    BeginInvoke(new Action(delegate { Hide(); tray.ShowBalloonTip(3000, "远控听写已就绪", "在远控画面里按住" + VoiceKeyNames[voiceKeyIndex] + "说话，松开自动粘贴到远端。", ToolTipIcon.Info); }));
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!smokeMode && !exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                if (oneKey != null) oneKey.Cancel("程序正在退出，已取消自动听写。");
                if (Busy) { closeRequested = true; e.Cancel = true; SetStatus("正在取消本次操作…", false); }
            };
            FormClosed += delegate
            {
                refresh.Dispose(); trim.Dispose(); watch.Dispose(); notice.Dispose();
                if (oneKey != null) oneKey.Dispose();
                Native.RemoveProp(Handle, "RemoteDictation.HoldEnabled"); Native.RemoveProp(Handle, "RemoteDictation.HoldTriggers");
                tray.Visible = false; tray.Dispose();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 以管理员权限运行时，仍允许普通权限启动的第二个副本发来“显示设置/退出”。
            try { Native.ChangeWindowMessageFilterEx(Handle, Native.WM_BRIDGE_SHOW, 1, IntPtr.Zero); Native.ChangeWindowMessageFilterEx(Handle, Native.WM_BRIDGE_EXIT, 1, IntPtr.Zero); }
            catch { }
        }
        private Label Caption(string text) { return new Label { Text = text, ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 4, 0, 4) }; }
        private Button Action(string text, bool primary, EventHandler handler)
        {
            Button button = new Button { Text = text, Width = primary ? 150 : 110, Height = 34, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0) };
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            button.BackColor = primary ? Cyan : Card; button.ForeColor = primary ? Color.FromArgb(8, 13, 22) : TextColor;
            button.Font = new Font("Microsoft YaHei UI", 10, primary ? FontStyle.Bold : FontStyle.Regular);
            button.Click += handler; return button;
        }
        internal void SetStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? Amber : Cyan; }
        private void ShowSettings()
        {
            if (oneKey != null) oneKey.HideForSettings();
            UpdateTargetLabel(); Show(); WindowState = FormWindowState.Normal; Native.ActivateOwnWindow(Handle);
        }
        private void UpdateTargetLabel()
        {
            var windows = RemoteWindow.FindAll();
            if (lastTarget != null && lastTarget.IsAlive) targetLabel.Text = Shorten(lastTarget.ToString());
            else if (windows.Count == 1) targetLabel.Text = Shorten(windows[0].ToString());
            else targetLabel.Text = windows.Count == 0 ? "未找到（先连接远端 Mac）" : "找到 " + windows.Count + " 个，按住时自动用当前那个";
        }
        private static string Shorten(string text) { return text.Length > 26 ? text.Substring(0, 25) + "…" : text; }
        private void ReadVoiceKey()
        {
            int index = -1; string message;
            bool detected = settings.AutoDetectVoice && DoubaoShortcut.TryRead(out index, out message);
            voiceConfigurationValid = detected || !settings.AutoDetectVoice;
            voiceKeyIndex = detected ? index : Math.Max(0, Math.Min(3, settings.VoiceKey));
            bool wasLoading = loading; loading = true;
            try
            {
                voiceKey.Items[0] = settings.AutoDetectVoice ? (detected ? "自动（豆包：" + VoiceKeyNames[voiceKeyIndex] + "）" : "自动（未读到，暂用" + VoiceKeyNames[voiceKeyIndex] + "）") : "自动（读取豆包设置）";
                voiceKey.SelectedIndex = settings.AutoDetectVoice ? 0 : voiceKeyIndex + 1;
            }
            finally { loading = wasLoading; }
            if (oneKey != null) oneKey.VoiceKey = VoiceKeyCodes[voiceKeyIndex];
        }
        private void ApplyEnabled()
        {
            if (oneKey != null) oneKey.Enabled = enabledBox.Checked;
            Native.SetProp(Handle, "RemoteDictation.HoldEnabled", oneKey != null && oneKey.Enabled ? new IntPtr(1) : IntPtr.Zero);
            pauseItem.Text = enabledBox.Checked ? "暂停听写" : "恢复听写";
            if (!enabledBox.Checked) SetStatus("已暂停。勾选上面的开关即可恢复。", true);
            else if (!voiceConfigurationValid) SetStatus("已启用，但没读到豆包语音键，暂按" + VoiceKeyNames[voiceKeyIndex] + "处理。", true);
            else SetStatus("● 已就绪：在远控画面里按住" + VoiceKeyNames[voiceKeyIndex] + "说话，松开自动粘贴", false);
        }
        // 这个远控软件该用哪个粘贴键：先看记住的；UU 从它自己的设置里读“哪个键映射成 Command”；都没有就要现场试。
        private bool KnownPasteKey(string app, out int key)
        {
            key = 0;
            if (string.IsNullOrEmpty(app)) return false;
            if (settings.PasteKeyByApp != null && settings.PasteKeyByApp.TryGetValue(app, out key)) return true;
            if (RemoteWindow.ProviderForProcess(app) == "UU远程")
            {
                try
                {
                    string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameViewer", "setting.ini");
                    foreach (string line in File.ReadAllLines(ini))
                        if (line.Trim().StartsWith("macos_key_mapping", StringComparison.OrdinalIgnoreCase))
                        {
                            // 顺序是 Win、Alt、Ctrl 各自映射成的 Mac 键，例如 ["Command","Option","Control"]。
                            string[] parts = line.Substring(line.IndexOf('=') + 1).Replace("\\", "").Replace("\"", "").Trim('[', ']', ' ', '"').Split(',');
                            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim(' ', '"', '[', ']');
                            if (parts.Length == 3 && parts[0] == "Command") { key = 1; return true; }
                            if (parts.Length == 3 && parts[2] == "Command") { key = 0; return true; }
                        }
                }
                catch { }
            }
            return false;
        }
        private void ShowPasteNotice(string text, RemoteWindow destination, string app, int key)
        {
            pendingText = text; pendingTarget = destination; pendingApp = app; pendingKey = key;
            notice.Show("没看到远端画面有变化，可能没贴上。文字还留着，可以再试一次：", PasteKeyNames[1 - key]);
            SetStatus("上一次可能没贴上，文字已保留。", true);
        }
        private async void RetryPending(bool swapKey)
        {
            if (pendingText == null || pendingTarget == null || Busy) return;
            if (!pendingTarget.IsAlive) { SetStatus("原远控窗口已关闭，没法再贴。", true); return; }
            int key = swapKey ? 1 - pendingKey : pendingKey;
            string text = pendingText; RemoteWindow destination = pendingTarget; string app = pendingApp;
            try
            {
                int used = await engine.Deliver(Payloads.Text(text), destination, Math.Max(500, Math.Min(5000, settings.TextDelay)), new[] { key }, true, CancellationToken.None, text, probe);
                if (used >= 0)
                {
                    pendingText = null;
                    if (swapKey) RememberPasteKey(app, used); else SetStatus("这次贴上了。", false);
                    return;
                }
                ShowPasteNotice(text, destination, app, key);
            }
            catch (Exception error) { pendingKey = key; notice.Show("没能再贴：" + error.Message, PasteKeyNames[1 - key]); }
        }
        private void RememberPasteKey(string app, int key)
        {
            if (string.IsNullOrEmpty(app)) return;
            if (settings.PasteKeyByApp == null) settings.PasteKeyByApp = new Dictionary<string, int>();
            settings.PasteKeyByApp[app] = key; SaveSettings();
            SetStatus("已记住：「" + app + "」用 " + PasteKeyNames[key] + " 粘贴。", false);
        }
        private void SetAppPasteKey(string app, int key)
        {
            if (string.IsNullOrEmpty(app)) return;
            if (settings.PasteKeyByApp == null) settings.PasteKeyByApp = new Dictionary<string, int>();
            if (key < 0) settings.PasteKeyByApp.Remove(app); else settings.PasteKeyByApp[app] = key;
            SaveSettings();
            tray.ShowBalloonTip(2500, "远控听写", key < 0 ? "「" + app + "」的粘贴键改为自动识别，下次粘贴时会试出来并记住。" : "「" + app + "」改用 " + PasteKeyNames[key] + " 粘贴。", ToolTipIcon.Info);
        }
        // 记住最近一次在前台的“别的程序”（不含本程序、任务栏、桌面），供托盘菜单把它加为远控软件。
        private void RememberExternalForeground()
        {
            try
            {
                IntPtr foreground = Native.GetForegroundWindow(); uint pid;
                Native.GetWindowThreadProcessId(foreground, out pid);
                if (pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName;
                    if (name == "explorer" || name == "ShellExperienceHost" || name == "SearchHost" || name == "StartMenuExperienceHost") return;
                    lastExternalProcess = name;
                }
            }
            catch { }
        }
        private bool IsExtra(string name)
        {
            foreach (string item in settings.ExtraRemoteProcesses ?? new string[0])
                if (string.Equals(item.Trim().Replace(".exe", ""), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private void ToggleRemoteProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var list = new System.Collections.Generic.List<string>(settings.ExtraRemoteProcesses ?? new string[0]);
            bool removing = IsExtra(name);
            if (removing) list.RemoveAll(delegate(string item) { return string.Equals(item.Trim().Replace(".exe", ""), name, StringComparison.OrdinalIgnoreCase); });
            else list.Add(name);
            settings.ExtraRemoteProcesses = list.ToArray(); RemoteWindow.ExtraProcesses = settings.ExtraRemoteProcesses;
            SaveSettings();
            tray.ShowBalloonTip(2500, "远控听写", removing ? "已不再把「" + name + "」当作远控软件。" : "已把「" + name + "」当作远控软件，在它的画面里按住语音键即可听写。", ToolTipIcon.Info);
        }
        private void ApplyAutoStart()
        {
            try
            {
                if (autoStartBox.Checked) AutoStart.Enable(); else AutoStart.Disable();
                SetStatus(autoStartBox.Checked ? "已设置开机自动启动，登录后在托盘运行。" : "已取消开机自动启动。", false);
            }
            catch (Exception error)
            {
                loading = true; try { autoStartBox.Checked = !autoStartBox.Checked; } finally { loading = false; }
                SetStatus(error.Message, true);
            }
        }
        private void InitializeOneKey()
        {
            oneKey = new OneKeyController(this, delegate(RemoteWindow destination)
            {
                int known; string app = destination.ProcessName ?? "";
                bool learned = KnownPasteKey(app, out known);
                int first = pasteKey.SelectedIndex == 1 ? 1 : 0;
                return new OneKeyOptions { PasteKeys = learned ? new[] { known } : new[] { first, 1 - first }, Learning = !learned, AppName = app,
                    PasteDelay = Math.Max(500, Math.Min(5000, settings.TextDelay)), RestoreClipboard = true };
            }, delegate { return !engine.Busy; },
            async delegate(string text, RemoteWindow destination, OneKeyOptions frozen, CancellationToken token)
            {
                int used = await engine.Deliver(Payloads.Text(text), destination, frozen.PasteDelay, frozen.PasteKeys, frozen.RestoreClipboard, token, text, probe);
                HoldTrace.Add("paste app=" + frozen.AppName + " tried=" + string.Join("/", Array.ConvertAll(frozen.PasteKeys, delegate(int k) { return PasteKeyNames[k]; })) + " screenChanged=" + (used >= 0 ? PasteKeyNames[used] : "no") + " chars=" + text.Length);
                if (used >= 0) { if (frozen.Learning) RememberPasteKey(frozen.AppName, used); return; }
                // 没看到远端画面变化：可能没贴上。文字留着，提醒用户可以再贴或换粘贴键。
                ShowPasteNotice(text, destination, frozen.AppName, frozen.PasteKeys[0]);
            });
            notice.RetryRequested += delegate { RetryPending(false); };
            notice.SwapRequested += delegate { RetryPending(true); };
            oneKey.VoiceKey = VoiceKeyCodes[voiceKeyIndex];
            oneKey.TargetCaptured += delegate(RemoteWindow current) { lastTarget = current; };
            oneKey.StatusChanged += SetStatus;
            oneKey.ActivityChanged += delegate(bool active)
            {
                if (closeRequested && !Busy) BeginInvoke(new Action(Close));
                if (!active) { trim.Stop(); trim.Start(); }
            };
            try { oneKey.Start(settings.DiagnosticLog); ApplyEnabled(); }
            catch (Exception error)
            {
                hookFailed = true;
                loading = true; try { enabledBox.Checked = false; } finally { loading = false; }
                SetStatus(error.Message + " 请退出后重新打开。", true);
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.WM_BRIDGE_EXIT) { exitRequested = true; Close(); return; }
            if (message.Msg == Native.WM_BRIDGE_SHOW) { ShowSettings(); return; }
            base.WndProc(ref message);
        }
        private void LoadSettings()
        {
            loading = true;
            try
            {
                if (File.Exists(settingsFile)) settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(settingsFile)) ?? new Settings();
                pasteKey.SelectedIndex = settings.PasteKey == 1 ? 1 : 0;
                RemoteWindow.ExtraProcesses = settings.ExtraRemoteProcesses ?? new string[0];
                enabledBox.Checked = settings.OneKeyEnabled;
            }
            catch { settings = new Settings(); SetStatus("设置文件无法读取，已使用默认设置。", true); }
            finally { loading = false; }
        }
        private void SaveSettings()
        {
            try
            {
                settings.PasteKey = pasteKey.SelectedIndex; settings.OneKeyEnabled = enabledBox.Checked;
                string pending = settingsFile + ".new";
                File.WriteAllText(pending, new JavaScriptSerializer().Serialize(settings), new System.Text.UTF8Encoding(false));
                if (File.Exists(settingsFile)) File.Replace(pending, settingsFile, settingsFile + ".bak");
                else File.Move(pending, settingsFile);
            }
            catch (Exception error) { SetStatus("无法保存设置：" + error.Message, true); }
        }
    }
}
