# 架构

本文描述进程结构、模块职责、两条主链路（识别链路、用量链路）与验证设施。机制取舍的“为什么”见 [adr/](adr/)；与 Codex 的集成事实见 [codex-integration.md](codex-integration.md)。

## 进程与生命周期

单进程 WinForms 应用（CodexRelayUsage.exe，Windows GUI 子系统，无控制台）。启动顺序：启动设置入口（StartupHost）→ 诊断／探针入口（DiagnosticsRunner、ProbeRunner）→ 单实例互斥量（Local\CodexRelayUsage）→ 主控制器 OverlayContext。悬浮条常驻托盘：没有 Codex 主窗口时隐藏，出现后自动显示；同一时间只运行一个实例。登录启动由快捷方式直指 GUI EXE（见 [ADR-0006](adr/0006-login-shortcut-starts-gui-directly.md)）。

## 模块地图

| 区域 | 主要类型 | 职责 |
|---|---|---|
| 入口与宿主 | Program、StartupHost、OverlayContext | 参数路由、单实例、设置与价格加载、主控制器 |
| 窗口定位与跟随 | CodexWindowLocator、NativeWindowEvents、OverlayLayout | 找到主窗口、原生事件驱动跟随、DPI 与吸附布局 |
| 会话识别 | CodexVisibleThreadMonitor、CodexDebugRouteReader、CodexSidebarAccessibility、SidebarSessionResolver、ManualAttachment | 主文档／路由／侧栏／标题识别与手动绑定 |
| 日志与用量 | TokenLogMonitor、LiveTokenUsage、UsageLedger、TokenSnapshot | 会话发现、增量读取、用量视图与账本 |
| 费用与价格 | SessionCost、PricingSettings、ModelPriceForm、CostDetailsForm | 计价、重算、价格编辑与费用明细 |
| 展示与交互 | TokenStripForm、OverlayPresentation、ModernControls、WindowsTheme、SessionPickerForm | 悬浮条渲染、反馈动画、主题与对话框 |
| 验证设施 | SyntheticSelfTests、各 `*Tests.cs`、ProbeRunner、DiagnosticsRunner | 合成自检、UI／窗口探针、只读诊断 |

## 识别链路

主文档（MSAA/IA2）→ 本地路由（app:// 主页面，/local/<会话 ID>，经本地调试连接读取）→ 侧栏元数据与本地项目索引（区分跨项目同名）→ 手动绑定（兜底）。识别结果由窗口、进程、文档与导航 key 组成“代次”校验；切换、超时或条目重建会取消迟到结果（见 [ADR-0002](adr/0002-identify-by-document-and-route.md)）。IPC 订阅只用于诊断，不参与生产识别。

## 用量链路

日志目录（会话发现、活动／归档去重）→ 增量读取（追加通知加一秒扫描补偿，半行与未完成 UTF-8 字节补齐）→ 解析逐次调用报告与线程累计 → 账本（按请求身份去重，供费用）与快照（供展示：Token、缓存、上下文、轮次、速度）→ 展示层按字段生成收起与展开内容。价格改动触发后台重算；费用发布前核对会话、账本与价格版本。

## 验证设施

- 合成自检（--self-test）与 `*Tests.cs` 编译进发布 EXE，验证用户实际拿到的二进制（见 [ADR-0001](adr/0001-ship-tests-inside-release-exe.md)）。
- Verify.ps1 运行自检与 Windows 原生窗口、费用窗口、工具栏交互、剪贴板、实时更新、启动设置与无控制台入口探针。
- CI 在 windows-latest 上构建并运行合成自检与 4 项 UI 探针（费用、反馈、实时、剪贴板）。
- 命令与探针清单见 [verification.md](verification.md)。
