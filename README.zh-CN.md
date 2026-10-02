# Codex 会话用量悬浮工具 1.0.0

为使用 API 中转站模型的 Windows Codex Desktop 用户显示当前会话用量。来源始终为 **Codex 本地日志**，无需 API 密钥或 ChatGPT 登录。

界面参考你提供的截图：收起显示 `5M tok · 缓存命中 94%`，旁边显示已记录轮次与最近调用上下文占用。点击向上展开精确数值，空间不足时向下展开。

![浅色展开示例](previews/light-expanded.png)

预览由实际窗体使用合成示例数据生成，不是你的真实对话数据。previews 目录另有浅色收起、深色展开和深色收起示例。

## 启动与操作

1. 解压 `CodexRelayUsage-1.0.0-win-x64.zip`，进入同名文件夹，双击 `CodexRelayUsage.exe`。成品自带运行时，不必安装 .NET SDK。同一时间只允许运行一个工具实例。
2. 打开 Codex 主窗口并选择一个对话。初始位置在窗口底部中央；最小化或切到其他应用时隐藏，回到 Codex 时恢复。
3. 首次位置如果没有正好落在输入框下方，右击系统托盘“Codex 会话用量”图标，选择“调整位置和大小…”。拖动悬浮条，拖右下角缩放，按 Enter 或托盘“完成调整”保存，Esc 取消。保存相对窗口的位置，随后跟随窗口移动和缩放；可“重置到 Codex 底部”。首次摆放需要手动校准，工具不识别输入框内部坐标。
4. 点击 Token 区域展开，点击外部收起。正常点击不抢 Codex 输入焦点；位置编辑、会话选择器和托盘菜单属于主动操作。
5. 托盘“主题”可选跟随 Windows、浅色或深色；“显示字段”控制明细，“收起时显示”改变两个主指标。推理 tokens 可选，默认关闭。
6. “暂时隐藏”不会退出统计进程；“退出”结束程序。没有自动开机启动。托盘图标可能在 Windows 隐藏图标菜单里。

## 会话跟随

程序通过本地 `codex-ipc` 管道接收 Codex 界面的会话跟随事件。切换到已有或已结束的对话会重新读取，后台其他日志不会替代明确选择的对话。

没有明确会话 ID、IPC 断开或出现多个跟随窗口时，显示识别状态，不按“最新日志”猜测。可用“手动选择并锁定会话…”搜索标题、ID 或工作目录。手动选择立即锁定，重启工具后仍保留；取消“已锁定当前会话”勾选即可恢复自动跟随。

首版面向一个 Codex 主窗口，多个窗口请手动锁定。远程主机没有同步到本机的日志不能统计。Codex 更新后若 IPC 格式改变，也可手动选择。

## 目录与数据

默认读取 `%USERPROFILE%/.codex/sessions` 及同级 `archived_sessions`；设置了 `CODEX_HOME` 时读取它下面的 sessions。托盘“选择 Codex 日志目录…”接受 .codex 根目录或 sessions。显式启动参数优先于保存的目录：

```powershell
.\CodexRelayUsage.exe --sessions "D:\CodexHome\sessions"
```

会话发现和首次读取在后台运行，当前文件随后增量读取。文件变化通知配合一秒目录轮询，未写完的 JSON 行等补齐再处理。同一会话的活动和归档副本去重，只选一个文件。

设置位于 `%LOCALAPPDATA%/CodexRelayUsage/settings.json`，记录位置、60%–130% 缩放、主题、字段、日志目录和锁定 ID。

程序不读取 API 密钥配置、不改写会话日志、不调用中转接口、不开放 HTTP 服务；读取元数据和 usage 事件，不保存正文副本。不要把真实日志或个人设置加入分享的源码包。

## 数字口径

| 显示项 | 计算和含义 |
|---|---|
| Token 用量 | 当前会话最新累计 `total_token_usage.total_tokens`。缺失时，仅在输入和输出完整有效时相加；累计快照不逐条叠加。 |
| 缓存读取 | `cached_input_tokens`，已包含在输入中。 |
| 未缓存输入 | 输入减缓存输入；任一字段缺失则无法计算。 |
| 缓存命中 | 缓存输入 ÷ 输入 × 100%，四舍五入到整数百分比；输入为零显示 `—`。 |
| 输出 | `output_tokens`。 |
| 推理输出 | `reasoning_output_tokens`，已含在输出中，不再次加总。 |
| 上下文占用 | 最近调用 `last_token_usage` 总量 ÷ `model_context_window`，标明“最近调用估算”，不使用会话累计总量。 |
| 轮次 | 当前日志中按 `turn_id` 去重的 `task_started` 数量，是已记录轮次。 |

未知值在明细中显示 `— / 未提供`，明确零值显示 0。负数、缓存大于输入、推理大于输出、溢出、非整数、总量不一致等显示异常提示。无效的已报告总量不会用输入加输出伪装为正常值。模型别名保持原样。

**准确性边界：**中转站可能漏报或改写 usage，Codex 也可能将缺失值归一化为零。日志不能还原上游真实值。“缓存读取 0”表示记录为零，不能证明上游完全未命中。统计不能直接作为中转站账单。

只统计当前线程，不合并子代理；费用、tok/s、执行步数和多窗口对应关系不在首版范围内。

| 状态 | 处理 |
|---|---|
| 无法识别当前对话 / IPC 未连接 | 在 Codex 中切换一次对话，仍未识别时手动选择并锁定。 |
| 等待当前会话用量 | 新对话还没有 token_count，完成一次调用并等待日志写入。 |
| 等待所选会话日志 | 检查目录和所选会话是否在本机有日志。 |
| 看不到悬浮条 | 确认 Codex 在前台、工具未“暂时隐藏”，再重置位置。 |
| 数字和中转账单不同 | 数据来源与计费口径不同，按上述边界核对。 |

## 源码构建与验证

在 Windows 上使用 .NET 10 SDK，已验证 10.0.401。global.json 指定该版本并允许同功能版本的补丁。源码不包含 SDK；无需替换原系统 SDK，可指定本地 dotnet.exe。首次构建可能下载官方 runtime packs。

在源码根目录执行：

```powershell
.\scripts\Publish-Local.ps1 -DotnetPath dotnet
# 或指定 SDK 的绝对路径
.\scripts\Publish-Local.ps1 -DotnetPath "D:\SDK\dotnet\dotnet.exe" -OutputDirectory "D:\Releases"
```

脚本发布 Windows x64 自包含 EXE，验证发布后的程序并生成预览；输出独立 ZIP、源码 ZIP 和 SHA256 到 artifacts 或指定目录。源码包排除 bin/obj、SDK、用户设置和真实日志。

单独验证与校验：

```powershell
.\scripts\Verify.ps1 -ExecutablePath ".\artifacts\CodexRelayUsage-1.0.0-win-x64\CodexRelayUsage.exe"
Get-FileHash ".\artifacts\CodexRelayUsage-1.0.0-win-x64.zip" -Algorithm SHA256
```

Verify 使用合成日志及 Windows 原生窗体探针，需要可创建 WinForms 窗口的 Windows 桌面，不依赖 Codex 登录或真实会话。结果为 self-test.json、native-result.json、verification.json。交付检查与待实际操作的验收场景见 `ACCEPTANCE.zh-CN.md`。

本工具由 `soleillevant0125/codex-token-overlay` 的 MIT 代码衍生，保留原作者版权。基线及修改说明见 `UPSTREAM.md`；.NET / Windows Desktop 许可和第三方说明在 third_party 目录。
