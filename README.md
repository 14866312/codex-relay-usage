# CodexRelayUsage 1.1.1

适用于 API 中转站模型的 Windows Codex 会话用量与费用估算悬浮工具，直接读取 Codex 本地会话日志，无需中转站密钥。

悬浮条默认放在顶部“文件／编辑／视图／帮助”右边的空白区，避开输入框、菜单和窗口按钮。点击向下展开 Token 总数、缓存命中率、缓存读取、未缓存输入和输出；支持当前会话跟随、手动锁定、主题和位置调整。

1.1.1 将悬浮条改为贴近 Codex 顶栏的轻量胶囊，移除厚重阴影。悬停平滑变色并显示手形光标，按住时加深、内容轻微下沉；点击展开后保留高亮、箭头翻转，明细短暂向下展开。移出后松开取消点击，切走窗口会清除按下状态；关闭系统菜单动画或高对比度时保留即时反馈。

![默认、悬停与按下状态（合成数据）](previews/light-toolbar-states.png)

1.1.0 新增托盘“模型价格…”和“费用明细…”。支持基础价乘倍率、直接折后价、逐次调用双档、完整模型别名绑定、分模型汇总与改价重算历史。单价单位为 USD / 1M tokens，首次留空；费用收起示例为 `5M tok · 估算 $0.0673`。切换会话立即清除旧费用，后台结果同时核对会话、账本和价格版本。

![费用明细预览（合成数据）](previews/light-expanded-cost.png)

![模型价格设置（合成数据）](previews/model-prices.png)

- [下载 Windows x64 独立版](https://github.com/14866312/codex-relay-usage/releases/latest)：解压后运行 CodexRelayUsage.exe，自带 .NET 10 运行时。
- [中文使用说明和构建步骤](README.zh-CN.md)
- [版本变更](CHANGELOG.md) · [验证记录](ACCEPTANCE.zh-CN.md)

费用只计入去重后的逐次调用；累计快照仅用于 Token 展示和完整性核对。完整估算、已知费用／部分记录、无法逐次计费、等待调用记录分别展示，缺失字段不会当作零。只统计当前线程，不自动合并子代理。上游缺失或被归一化的 usage 无法从本地日志还原，估算金额不作为中转站账单。

由 [codex-token-overlay](https://github.com/soleillevant0125/codex-token-overlay) 的 MIT 代码衍生，保留原作者版权；基线和修改范围见 [UPSTREAM.md](UPSTREAM.md)。
