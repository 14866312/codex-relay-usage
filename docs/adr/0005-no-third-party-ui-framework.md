# 不引入第三方 UI 框架与常驻辅助进程

界面使用 WinForms / GDI+ 原生绘制（ModernControls、WindowsTheme），应用无额外第三方 NuGet 库、无常驻辅助进程，发布只带 .NET/Windows Desktop 运行时。代价是自绘控件与主题适配需要自己维护；收益是体积、内存与启动路径完全可控（1.1.8 起单进程，常驻内存的历史采样由约 215.1 MiB 降至约 73.1 MiB）。

来源：README.md 亮点；UPSTREAM.md（无第三方 NuGet）；CHANGELOG 1.1.8。
