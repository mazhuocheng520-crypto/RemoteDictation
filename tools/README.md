# 工具

- `MakeIcon.cs`：生成 `app.ico`（青紫渐变圆底 + 白色麦克风）。编译：`csc /reference:System.Drawing.dll MakeIcon.cs`，运行：`MakeIcon.exe ..\app.ico`。
- `InputProbe.cs`：排查用的诊断工具，见 [docs/原理.md](../docs/原理.md)。模式：`watch`（物理按键计数）、`chain`、`altseq`（会向自己的输入框注入 Alt，运行时请别打字）、`altswitch`、`preswitch`。只记次数、键码和字数，不记正文。
