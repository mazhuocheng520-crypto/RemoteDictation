<p align="center"><img src="docs/icon.png" width="96" alt="RemoteDictation 图标"></p>

<h1 align="center">RemoteDictation · 远控听写</h1>

<p align="center"><b>用 Windows 远程控制 Mac 时，也能用本机的语音输入法。</b><br>
在远控画面里按住语音键说话，松开后文字自动出现在 Mac 的输入框里。</p>

<p align="center">Windows → Mac · ToDesk / UU 远程 · 各类“按住说话”的语音输入法（推荐豆包） · 只粘贴不发送</p>

<p align="center"><a href="https://github.com/mazhuocheng520-crypto/RemoteDictation/releases/latest">下载最新版</a> · <a href="https://mazhuocheng520-crypto.github.io/RemoteDictation/">项目主页</a> · <a href="docs/原理.md">原理</a> · <a href="#常见问题">常见问题</a> · <a href="#english">English</a></p>

---

**RemoteDictation（远控听写）** 是一个免费、开源的 Windows 托盘小工具：用 ToDesk、UU 远程等远程控制软件操作 Mac 时，在远控画面里按住输入法的语音键（例如豆包输入法的左 Alt）说话，松开后识别出来的文字会自动粘贴到 Mac 当前的输入框里。它不做语音识别，识别交给你本机的输入法；也不会替你按回车发送。

关键词：远程控制语音输入 · 远控语音输入 · Windows 远程控制 Mac 语音输入 · ToDesk 语音输入 · UU 远程语音输入 · 远程桌面用豆包输入法 · 远程桌面语音转文字 · remote desktop voice typing

## 为什么做这个

很多人桌上是一台 Windows，干活却要远程到一台 Mac（Mac mini、公司的 Mac、跑 AI 工具的 Mac）。在 Windows 上用惯了语音输入（比如豆包），一进远控画面就用不了：

- **按语音键没反应**：远控软件在前台时会把键盘整个接管，输入法收不到按键；
- **识别出来也进不了 Mac**：就算本机出了字，也只在本机，不会跑到 Mac 的输入框里；
- **Windows 和 Mac 键位不一样**：Ctrl、Win、Alt 到了 Mac 上分别变成什么，每个远控软件还都不一样。

RemoteDictation 就是专门解决 **Windows 控制 Mac** 这个场景的：你一直待在远控画面里，照常按住语音键说话，松开字就到了 Mac 上，跟在本机用语音输入一样。

## 致敬原作者

本项目源自 [yupeng316888-create](https://github.com/yupeng316888-create) 的 **[DoubaoVoiceBridge（远控听写）](https://github.com/yupeng316888-create/DoubaoVoiceBridge)**，感谢原作者开源。

原项目是一个 macOS 菜单栏工具，解决的是 **Mac 控制 Mac**（通过 UU 远程）时用不上本机豆包语音输入的问题：在远控窗口里按住左 Control 说话，松开后把文字粘贴到另一台 Mac，还能截图粘贴。“远控听写”这个名字、“只粘贴、不发送”的原则，以及“等豆包提交最终文字再粘贴”的做法，都来自原项目。

RemoteDictation 把这个思路搬到了 **Windows 控制端**。Windows 上还要额外解决远控软件吞键、豆包不认程序模拟的按键等问题（见下文“原理”），所以代码是重新写的，并扩展到了更多远控软件。**如果你的控制端是 Mac，请直接使用原项目。**

## 它做了什么

- **键盘抢回来**：在远控软件前面拿到你按的语音键，交给本机的语音输入法听写；
- **字送过去**：识别完通过远控软件的剪贴板同步，自动贴进 Mac 当前的输入框；
- **键位自动对上**：自动识别每个远控软件把哪个键映射成 Mac 的 Command（UU 远程直接读它的设置），粘贴键不用你操心；
- **不添乱**：只粘贴，不按回车，不会替你发送；粘贴完自动还原你原来的剪贴板。

主要为 Windows → Mac 设计和实测；远端是 Windows / Linux 也能用（粘贴键一般是 Ctrl+V）。

## 用起来是这样

1. 在远控画面里点好 Mac 上的输入框（聊天框、文档、终端都行）。
2. **按住**语音键（用豆包时自动读取它的设置，比如左 Alt；其他输入法在设置里选一下），屏幕下方出现听写浮窗。
3. 说话，说完**松开**。
4. 输入法整理完文字后，自动粘贴到 Mac，浮窗消失。

其他细节：

- 只是**点一下** Alt、按 **Alt+其他键**、或按住时**点鼠标**，都不算听写，照常发给远端（Mac 上的 Option 组合键不受影响）。
- 说到一半按 **Esc** 取消，文字留在浮窗里，可以改完再点「粘贴到远端」。
- 粘贴后看不到远端画面变化（可能没贴上）时，屏幕下方会弹出不抢焦点的提醒，可以「再贴一次」或「换一种粘贴键再贴」。
- 粘贴完 2 秒后自动还原你原来的剪贴板。
- 平时只在托盘运行，空闲时几乎不占 CPU，内存很小。可设置开机自动启动。

## 能用在哪些远控软件上

原理对所有“远控窗口在本机前台、通过剪贴板同步文字”的软件都适用。内置识别以下客户端（按进程名）：

| 远控软件 | 识别的进程名 | 状态 |
| --- | --- | --- |
| ToDesk | `ToDesk` | ✅ 已实测（Windows → Mac） |
| 网易 UU 远程（推荐） | `GameViewer` / `UURemote` / `UU_Remote` | ✅ 已实测（Windows → Mac），自动跟随 UU 的按键映射 |
| 向日葵 | `SunloginClient` / `AweSun` | 已内置，待反馈 |
| RustDesk | `rustdesk` | 已内置，待反馈 |
| AnyDesk | `AnyDesk` | 已内置，待反馈 |
| TeamViewer | `TeamViewer` | 已内置，待反馈 |
| Parsec | `parsecd` | 已内置，待反馈 |
| Windows 远程桌面 | `mstsc` / `msrdc` | 已内置，待反馈 |
| VNC Viewer / TightVNC | `vncviewer` / `tvnviewer` | 已内置，待反馈 |
| Splashtop | `strwinclt` | 已内置，待反馈 |
| NoMachine | `nxplayer` / `nxplayer.bin` | 已内置，待反馈 |
| AnyViewer | `AnyViewer` | 已内置，待反馈 |
| Moonlight | `Moonlight` | 已内置，待反馈 |

**不在表里的软件**：先点进它的远控画面，再右键托盘的麦克风图标，选「把「xxx」当作远控软件」即可。也可以在 `RemoteDictation.exe` 同目录的 `bridge-settings.json` 里手动加进程名（任务管理器“详细信息”页可以看到）：

```json
{ "ExtraRemoteProcesses": ["MyRemoteClient"] }
```

远端是什么系统都行（Mac、Windows、Linux），只要远控软件开启了**剪贴板同步**。

### 粘贴键：自动试出来并记住

程序在本机发出粘贴快捷键，再由远控软件转成远端的按键。不同远控软件的映射不一样，例如 ToDesk 默认 **Ctrl→Command**，UU 远程默认 **Win→Command**。所以每个远控软件各记一个粘贴键：

1. **UU 远程**：每次都直接读取它自己的“远控 macOS 按键映射”设置，在 UU 里改了映射，这里自动跟着变，不用任何操作；
2. 其他软件已经记住的，直接用；
3. 其他软件第一次粘贴时，先发 Ctrl+V，看远控画面有没有变化；1.5 秒没变化就改发 Win+V，哪个让画面变了就记住哪个。（不会尝试 Alt+V，因为它在 Mac 上会打出“√”。）

记错了可以右键托盘 →「「xxx」的粘贴键」手动改，或改回“自动识别”。远端是 Windows/Linux 时一般是 Ctrl+V。

“看画面”的做法：粘贴前后各截一次远控窗口，缩成 480 像素宽的灰度小图在内存里逐点比较，明显变化的点超过 20 个就算贴上了。截图不保存、不上传。字很少或远端字很小时可能误判为没贴上，这时只会多弹一次提醒。

### 语音输入法：都能适配，推荐豆包

本工具不做语音识别，识别交给你本机已经在用的语音输入法，所以**只要输入法支持“按住某个键说话、松开出字”，就能用**：

- **推荐豆包输入法**：识别准、会自动整理标点和口语；本工具会自动读取它的“长按语音”快捷键，装好就能用，是唯一完整实测过的输入法。
- **其他输入法**：在托盘 → 打开设置 → 「语音键」里选成和输入法一致的按键（左/右 Alt、左/右 Ctrl）即可。
- 程序等输入法“组字结束、文字不再变化”才粘贴，所以带整理/纠错的输入法也不会被提前截断。
- 目前只支持“按住说话”型；按一下开始、再按一下结束的切换型（例如 Win+H）暂不支持。

## 安装

**直接用**：在 Releases 下载 `RemoteDictation.exe`，放到一个固定的文件夹里运行。

**自己编译**（不需要 Visual Studio，Windows 自带编译器即可）：

```powershell
.\build.ps1            # 生成 bin\RemoteDictation.exe
.\bin\RemoteDictation.exe --self-test .\bin\self-test.json   # 可选：跑一遍逻辑自测
```

运行要求：Windows 10/11 x64，.NET Framework 4.8（系统自带）。

### 关于管理员权限（重要）

很多远控软件（例如 ToDesk）是以**管理员权限**运行的。Windows 不允许普通权限的程序读取管理员窗口前台的按键，也不允许向它发送粘贴。所以：

- 第一次请**右键 →“以管理员身份运行”**，然后在设置窗里勾选「**开机自动启动**」。
- 开机自启是通过“任务计划程序”以最高权限启动的（任务名 `RemoteDictation`），之后不会再弹权限提示；以后直接双击 EXE，也会自动转交给这个任务启动。
- 取消：设置窗取消勾选，或运行 `RemoteDictation.exe --autostart off`。

## 原理（为什么要这样做）

这几点都是在真实 ToDesk 环境里抓数据确认的，详见 [docs/原理.md](docs/原理.md)：

1. **远控软件会吞键**：ToDesk 在获得前台时安装自己的低级键盘钩子，把按键拦下来发给远端。比它先装的钩子（包括输入法自己的）都收不到。本程序在远控窗口到前台后重新安装钩子，排到它前面。
2. **输入法不认模拟按键**：豆包只响应真实的物理按键，程序用 SendInput 模拟的语音键会被忽略。
3. **按住 Alt 不会自动重复**：整个按住过程只有一次“按下”。如果先让远控软件收到、再切窗口，输入法就再也等不到这次按下，只能按第二次。
4. **所以在“按下”送达之前就切前台**：在钩子回调里（这次按键还没被派发时）把前台切到本机的听写浮窗，再放行这次按下，输入法就在浮窗里开始听写。
5. **等整理完再贴**：松开后等输入法结束组字、文字 1.5 秒不再变化，再写入剪贴板，回到原远控窗口发出粘贴键。等待期间如果你切走了窗口或又复制了别的内容，就不贴，文字留在浮窗。
6. **一定还原剪贴板**：粘贴前先完整备份你原来的剪贴板（文字、截图、网页/Office 内容、复制的文件都在内），粘贴 2 秒后还原。万一备份不了（比如被别的程序长时间占用），这次就不粘贴，文字留在浮窗，保证原剪贴板不丢。

## 常见问题

### Windows 用 ToDesk / UU 远程控制 Mac，按豆包语音键没反应，怎么办？
这是因为远控软件在前台时会用自己的低级键盘钩子把按键全部拦下来发给远端，本机的输入法根本收不到。装上 RemoteDictation 后，在远控画面里按住语音键，它会先拿到按键、切到本机听写浮窗交给输入法，识别完再自动贴到 Mac。

### 远程桌面里能用本机的语音输入法吗？识别的文字怎么进到远程 Mac？
能。RemoteDictation 让本机输入法在本机浮窗里完成识别，然后借助远控软件的剪贴板同步，把文字粘贴进远端当前的输入框，相当于“本机说话、远端出字”。

### UU 远程控制 Mac 时 Ctrl+V 粘贴不了？
UU 远程默认把 Windows 的 **Win 键** 映射成 Mac 的 Command，所以要用 Win+V；ToDesk 默认是 Ctrl→Command，用 Ctrl+V。RemoteDictation 会直接读取 UU 的映射设置，其他软件第一次粘贴时自动试出正确的粘贴键并记住。

### 在远控画面里按住 Alt 说话，为什么以前要按两次才有反应？
按住 Alt 不会产生连续的“按下”信号，整个按住过程只有一次。如果这次按下先被远控软件拿走，输入法就再也等不到了。RemoteDictation 在这次按下送达之前就把前台切到本机浮窗，所以按一次就能开始听写。

### 会不会自动发送消息？会不会弄丢我的剪贴板？
不会自动发送：只发粘贴键，从不按回车。粘贴前会完整备份你原来的剪贴板（文字、截图、Office 内容、复制的文件），粘贴后自动还原；备份不了就这次不贴，文字留在浮窗里。

### 会上传我的语音吗？
不会。本工具不录音、不联网，语音识别完全由你本机的输入法完成。

### 支持哪些远控软件和语音输入法？
实测过 ToDesk、UU 远程 + 豆包输入法 → Mac；内置识别向日葵、RustDesk、AnyDesk、TeamViewer、Parsec、Windows 远程桌面、VNC、Splashtop、NoMachine 等，也可以在托盘里把任意程序加为远控软件。输入法只要支持“按住某个键说话、松开出字”就能用，推荐豆包。

### 我是用 Mac 控制另一台 Mac，能用吗？
本项目只做 Windows 控制端。Mac 控制 Mac 请用原项目 [DoubaoVoiceBridge](https://github.com/yupeng316888-create/DoubaoVoiceBridge)。

## 隐私

- 不录音、不联网、没有任何上传接口；语音识别完全由你本机的输入法完成。
- 不记录你说的内容，也不记录其他按键。
- 设置文件只保存开关和按键选择。诊断日志默认关闭（`"DiagnosticLog": true` 可开启，只记时间、键码、粘贴结果和字数）。
- 判断是否贴上时会截取远控窗口画面，只在内存里比较，比完即丢弃。

## 已知限制

- 完整实测过的组合：Windows 本机 + 豆包输入法 + ToDesk / UU 远程 → 远端 Mac。其他输入法、远控软件的组合欢迎在 Issues 反馈。
- 远控画面里单独按住语音键超过约半秒会被当成听写；如果你经常在远端单独长按 Option/Alt，可以在托盘里临时暂停。
- 按住 Alt 时点鼠标（Mac 上的 Option+点击）会被当成普通点击。
- 粘贴依赖远控软件的剪贴板同步，同步慢的软件可在设置文件里调大 `TextDelay`（毫秒，默认 800）。

## 致谢

- 原项目 [DoubaoVoiceBridge](https://github.com/yupeng316888-create/DoubaoVoiceBridge)（macOS，MIT 许可），见文首“致敬原作者”。
- 想法最初来自「生财有术 · 航海家」社群里一次关于远程办公和语音输入的讨论，在此致谢。

与豆包、字节跳动、ToDesk 及表中任何远控软件均无官方关系，相关名称归各自所有者。

## English

**RemoteDictation** is a free, open-source Windows tray app that lets you use your **local voice input / speech-to-text IME while remote-controlling a Mac** (or any computer) through ToDesk, UU Remote, Sunlogin, RustDesk, AnyDesk, TeamViewer, Parsec, Microsoft Remote Desktop, VNC and similar clients.

Hold your IME's push-to-talk key (e.g. left Alt for Doubao IME) inside the remote-desktop window, speak, release — the recognized text is pasted into the focused text field on the remote Mac. It never presses Enter, and your original clipboard is restored afterwards.

Why it is needed: remote-desktop clients install a low-level keyboard hook and swallow keys while focused, IMEs ignore synthesized keys, and modifier keys don't auto-repeat. RemoteDictation re-hooks ahead of the client, switches focus to a local dictation box *before* the key-down is dispatched so the IME sees the real key press, then pastes via the client's clipboard sync with the right modifier (auto-detected per client: Ctrl+V or Win+V → Command+V). See [docs/原理.md](docs/原理.md) (Chinese) for measurements.

Requirements: Windows 10/11 x64, .NET Framework 4.8. Run as administrator once and enable "start at login" (Task Scheduler, highest privileges). No recording, no network, no telemetry.

## 许可

[MIT](LICENSE)
