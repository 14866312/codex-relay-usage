# 验证与发布流程

## 构建与发布

```powershell
dotnet build .\src\CodexTokenOverlay\CodexTokenOverlay.csproj -c Release
dotnet publish .\src\CodexTokenOverlay\CodexTokenOverlay.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\verify
.\scripts\Verify.ps1 -ExecutablePath .\artifacts\verify\CodexRelayUsage.exe
```

- 完整发布走 `\scripts\Publish-Local.ps1 -DotnetPath dotnet`：自包含 x64 EXE、独立 ZIP、源码 ZIP、SHA256 与预览图。
- SDK 由 global.json 指定（已验证 10.0.401）；源码包排除 bin/obj、SDK、用户设置与真实日志。

## 本机部署（开发机快捷方式）

本机的悬浮条由两个快捷方式启动，二者都直接指向本机发布目录里的可执行文件，不是系统安装：桌面 `Codex 用量助手.lnk`（手动入口），以及登录启动项 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Codex 用量自动启动.lnk`（由发布目录的 `scripts\Install-CodexStartup.ps1` 写入，托盘“开机启动”调用的也是它）。

升级步骤：`Publish-Local.ps1` 产出 `artifacts\CodexRelayUsage-<版本>-win-x64\` → 把桌面快捷方式指向新目录的 `CodexRelayUsage.exe` → 运行新目录的 `scripts\Install-CodexStartup.ps1`，由它刷新登录启动项并启动新版；旧目录保留，回滚时把两个入口指回去即可。2026-10-12 已按此把两个入口从 1.3.7 切到 1.3.8。日常启动请走快捷方式或双击 EXE（`explorer.exe CodexRelayUsage.exe` 等价）；不要从其它进程里以子进程方式启动悬浮条——它会随启动它的进程所在的作业一起退出，2026-10-12 实测脚本启动的实例在 Codex 重启时一并消失。

## 验证要求

- `*Tests.cs` 与合成自检编译进发布 EXE，Verify.ps1 校验的就是用户拿到的同一份二进制（见 [ADR-0001](adr/0001-ship-tests-inside-release-exe.md)）；不要在发布路径上移出或加 `#if DEBUG`。
- 每次改动通过构建与验证后立即提交，不要跨会话或跨版本累积未提交的工作树；发布时工作树应为干净状态（提交规范见 [AGENTS.md](../AGENTS.md)）。
- Verify 需要可创建 WinForms 窗口的 Windows 桌面；使用合成日志与夹具，不依赖 Codex 登录或真实会话。
- CI（windows-latest）构建后运行合成自检与 4 项 UI 探针：cost-ui-probe、feedback-ui-probe、live-ui-probe、clipboard-ui-probe。

## 探针与诊断（只读）

| 入口 | 用途 |
|---|---|
| --self-test | 合成自检 |
| --view-probe | 主文档标题、侧栏行、项目标签、识别结果 |
| --follow-probe | 会话切换与跟随（可选 --hidden） |
| --sidebar-binding-probe | 当前页临时绑定核对 |
| --cost-log-probe | 费用账本核对（内存合成价格） |
| --speed-log-probe | 纯生成速度复算 |
| --runtime-usage-probe | 运行时白名单投影核对（关系、侧聊标记与数字；需以调试参数启动的 Codex） |
| --ipc-probe | IPC 订阅诊断（不用于生产识别） |

`--runtime-usage-probe "<out.json>" [进程 ID]` 用于核实侧聊入口：省略进程 ID 时读取当前可见的 Codex 主窗口，可用时退出码 0，未启用调试连接、页面未提供字段或读取失败时退出码 1，并在 JSON 中给出原因。输出只有线程标识、侧聊标记、父导航路径与数字字段，不含消息正文；核实结果留在本机。本机 2026-10-12 在 Codex 26.1007.2314.0（`--remote-debugging-address=127.0.0.1 --remote-debugging-port=0`）上核实通过：会话内打开一个侧聊后，探针返回该侧聊的标识、`sideConversation` 标记、解析出的父会话与其它线程的数字字段。Codex 更新内部结构后需重新核实，核实不通过时侧聊在明细中显示数据不可用原因（见 [ADR-0009](adr/0009-related-usage-scope-and-read-boundary.md)）。

关联用量的合成检查在 `--self-test` 中：分类与已知合计、多级关系、冲突与缺失不误归属、分叉继承基线、去重与迁移、截断重写、侧聊生命周期与保留、发布版本核对、32 成员规模与分片读取、表达式白名单与运行时传输解析；`--live-ui-probe` 另外验证生产控制器把子代理并入分组且主会话数值不变。运行时传输检查使用本机回环合成页面，不连接真实 Codex。

需要采集 Codex 启动后的“页面就绪”时间线时，用 `--view-probe out.jsonl 2000` 连续采样（每 150 ms 一行），以 `Error` 从 `当前页面尚未就绪` 变为识别结果的时间点为恢复时刻；本机复现记录见 [codex-integration.md](codex-integration.md)。

页面等待与绑定恢复的合成检查在 `--self-test` 中：注入单调时钟的等待起点、窗口生命周期重建与日志目录变化重新计时；0.2 秒／2 秒空窗与 3 秒／60 秒阈值及三档文案；首次待就绪立即清空 Token／费用／关联用量并拒绝迟到结果；阻塞超时与调试连接断开的即时规则；手动锁定不受等待阶段影响；短空窗绑定恢复与节点重建、宽限期到期、文档变化时的丢弃。可选的本机诊断（托盘“会话与数据 → 记录页面状态”）另有 12 项合成检查与 4 项实时探针检查：关闭时不写文件、开启后区分“读到未就绪”与“显示未就绪”、恢复与停止原因、记录字段白名单、单文件上限与停止后不再写入、无法创建时的启动失败，以及生产控制器按需开启／停止并写入状态行。检查使用合成视图、注入时钟与临时目录，不依赖个人 Codex 会话，也不写入用户的诊断目录。

这些输出含个人会话标识与元数据，留在本机，不加入源码或公开发布。

## 交付验证记录与归档

- 每个版本的验证记录追加到 ACCEPTANCE.zh-CN.md，内容含版本与日期、变更点、基线／修改对照、完整发布验证项数与结果、回滚说明。
- 归档规则：主文件保留当前版本线（如 1.3.x）；新版本线开始时，旧版本线整体移入 ACCEPTANCE-archive.zh-CN.md（1.2.0 及更早已归档）。
- 事务材料（原始／修改副本、差异补丁、验证记录、回滚脚本）保存在本机 artifacts/ 下，不随仓库分发；artifacts/ 已在 .gitignore 中。

## 相关文档

知识库更新时机见 [README.md](README.md)；提交与评审规范见 [AGENTS.md](../AGENTS.md)。
