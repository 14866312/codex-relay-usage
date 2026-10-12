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
| 关联用量 | RelatedSessionCatalog、SessionGroupMonitor、SessionGroupUsage、CodexRuntimeUsageReader | 显式关系归属、成员分片读取、侧聊运行时投影、分组快照 |
| 页面等待与诊断 | PageWaitRules、PageDiagnosticsRecorder | 等待阶段分档、短空窗绑定候选、可选的本机页面状态记录 |
| 费用与价格 | SessionCost、PricingSettings、ModelPriceForm、CostDetailsForm | 计价、重算、价格编辑与费用明细（含“关联用量”页） |
| 展示与交互 | TokenStripForm、OverlayPresentation、ModernControls、WindowsTheme、SessionPickerForm | 悬浮条渲染、反馈动画、主题与对话框 |
| 验证设施 | SyntheticSelfTests、各 `*Tests.cs`、ProbeRunner、DiagnosticsRunner | 合成自检、UI／窗口探针、只读诊断 |

## 识别链路

主文档（MSAA/IA2）→ 本地路由（app:// 主页面，/local/<会话 ID>，经本地调试连接读取）→ 侧栏元数据与本地项目索引（区分跨项目同名）→ 手动绑定（兜底）。识别结果由窗口、进程、文档与导航 key 组成“代次”校验；切换、超时或条目重建会取消迟到结果（见 [ADR-0002](adr/0002-identify-by-document-and-route.md)）。IPC 订阅只用于诊断，不参与生产识别。

待就绪等待属于这条链路：主文档出现但尚未填充 URL 时身份立即失效（数字清空、迟到结果取消），文案按单调时钟分档——不足 3 秒“正在读取当前对话…”，3 秒后“当前页面尚未就绪”，60 秒后“等待较久，可尝试重启 Codex”；等待起点绑定窗口生命周期（句柄、进程与进程启动时间），窗口重建、日志目录或错误类型变化时重新计时。待就绪期间保留上一条侧栏绑定作为临时候选，页面恢复后重新核对文档、侧栏条目、标题与项目归属，全部一致才恢复绑定，超时（3 秒）或不一致即丢弃。提示阶段只属于展示缓存，不递增会话选择代次。

## 用量链路

日志目录（会话发现、活动／归档去重）→ 增量读取（追加通知加一秒扫描补偿，半行与未完成 UTF-8 字节补齐）→ 解析逐次调用报告与线程累计 → 账本（按请求身份去重，供费用）与快照（供展示：Token、缓存、上下文、轮次、速度）→ 展示层按字段生成收起与展开内容。价格改动触发后台重算；费用发布前核对会话、账本与价格版本。

## 关联用量链路

主会话链路保持不变：悬浮条只发布当前会话的 Token、缓存、上下文、速度与费用。关联用量在后台另走一条链路，先发布主会话、再更新分组，因此成员日志与运行时读取不会延迟悬浮条数字。

会话元数据（只读 `session_meta` 一行）→ 关系目录（显式父关系、来源、分叉标识，关系集合变化才递增版本）→ 成员归属（多级子代理递归；冲突与成环列为未确认；普通分叉与 `agent_created_thread` 不并入）→ 成员分片读取（每成员每轮 1 MB，读取位置独立于主会话的 8 份缓存，最多 32 个成员）→ 每成员自身用量与独立计价缓存 → 分组快照（分类合计、已知合计、Token／费用覆盖状态、备注）。

侧聊不走日志：由 CodexRuntimeUsageReader 通过已确认主窗口进程的回环调试连接读取字段白名单（线程标识、侧聊标记、父导航路径与数字用量），在页面内构造结果，不读取消息正文或请求体。入口不可用时分组显示明确原因，不用其它来源替代。

分组发布前核对根会话、选择代次、关系版本与价格版本（SessionGroupPublication）；任何一项不匹配的迟到结果都会被丢弃。成员超过上限或日志被截断重写时按读取位置与去重状态重建，不重复计费。

分组结果通过核对后直接推送给费用明细窗口，不经过悬浮条的展示缓存——主会话数字不变、只有成员追加时窗口也会刷新；窗口打开时一次性带入当前费用与分组，切换会话与改价时仍先清空再等待新结果。

## 验证设施

- 合成自检（--self-test）与 `*Tests.cs` 编译进发布 EXE，验证用户实际拿到的二进制（见 [ADR-0001](adr/0001-ship-tests-inside-release-exe.md)）。
- Verify.ps1 运行自检与 Windows 原生窗口、费用窗口、工具栏交互、剪贴板、实时更新、启动设置与无控制台入口探针。
- CI 在 windows-latest 上构建并运行合成自检与 4 项 UI 探针（费用、反馈、实时、剪贴板）。
- 命令与探针清单见 [verification.md](verification.md)。
