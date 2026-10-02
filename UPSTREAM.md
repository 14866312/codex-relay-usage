# 来源与修改说明

定制项目：**CodexRelayUsage 1.0.1**，2026-10-02。

上游：https://github.com/soleillevant0125/codex-token-overlay

基线提交：`b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5`。

许可：MIT，Copyright (c) 2026 soleillevant0125。原许可保留于 LICENSE。本交付是定制衍生版本，不是原作者官方发行版，也不是 OpenAI 官方插件。

复用窗口识别与跟随、DPI、原生不激活窗体、位置调整、主题绑定及本地 IPC。命名空间和工程目录保留 CodexTokenOverlay，发布 EXE 为 CodexRelayUsage。

主要修改：重写可空快照、累计用量及增量解析；按明确会话 ID 路由；增加字段缺失、异常、模型别名、手动选择/锁定和独立设置；调整中文悬浮条与面板；增加合成测试、预览及发布脚本；默认吸附顶部菜单空白区、向下展开，增加旧设置迁移及菜单／窗口按钮避让。与新统计口径不兼容的上游旧测试改用 Verify.ps1。

应用无额外第三方 NuGet 库。独立 EXE 包含 Microsoft .NET / Windows Desktop 10.0.12 运行时，许可和第三方说明在 third_party。构建使用 .NET SDK 10.0.401；SDK 不在交付包中。
