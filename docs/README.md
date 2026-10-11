# 知识库索引

本目录是 codex-relay-usage 的长期知识库：与代码一起版本化、随提交评审的 Markdown 文档。用户侧说明见 [README.zh-CN.md](../README.zh-CN.md)，逐版本变更见 [CHANGELOG.md](../CHANGELOG.md)。

## 阅读顺序

1. [CONTEXT.md](../CONTEXT.md)：领域词汇表，先统一说法。
2. [architecture.md](architecture.md)：模块地图与两条主链路。
3. 按主题选读：[codex-integration.md](codex-integration.md)、[cost-and-speed.md](cost-and-speed.md)、[verification.md](verification.md)。
4. [adr/](adr/)：难回退决策的“为什么”。
5. [agents/domain.md](agents/domain.md)：后续 agent 会话的消费规则。

## 文档地图

| 文档 | 内容 | 读者 |
|---|---|---|
| [CONTEXT.md](../CONTEXT.md) | 术语表：会话、账本、识别、费用、验证 | 所有人 |
| [architecture.md](architecture.md) | 模块职责与识别／用量两条链路 | 维护者 |
| [codex-integration.md](codex-integration.md) | 与 Codex 集成的事实：日志、路由、可访问性、IPC | 维护者 |
| [cost-and-speed.md](cost-and-speed.md) | 费用与速度口径 | 维护者、对账用户 |
| [verification.md](verification.md) | 构建、验证、发布与归档流程 | 维护者 |
| [adr/](adr/) | 架构与取舍决策记录（0001–0008） | 维护者 |
| [agents/domain.md](agents/domain.md) | agent 会话读取本知识库的规则 | agent |

## 何时更新哪份文档

| 变更类型 | 必须更新 |
|---|---|
| 用户可见行为 | [CHANGELOG.md](../CHANGELOG.md)；发布时追加到 [ACCEPTANCE.zh-CN.md](../ACCEPTANCE.zh-CN.md) |
| 架构或机制 | [architecture.md](architecture.md) 或 [codex-integration.md](codex-integration.md)；难回退时新增 [adr/](adr/) |
| 术语或命名 | [CONTEXT.md](../CONTEXT.md) |
| 验证或发布流程 | [verification.md](verification.md) |
| 新版本线发布 | 旧版本线移入 [ACCEPTANCE-archive.zh-CN.md](../ACCEPTANCE-archive.zh-CN.md) |

## 边界

- CHANGELOG 是版本历史的唯一来源；本知识库只记录“为什么”与“怎么工作”，不复制版本流水。
- 个人数据（真实会话、个人价格、诊断输出）留在本机，不进入仓库；事务材料保存在被忽略的 artifacts/ 下。
- 文档语言：正文中文，代码符号、文件名与命令保留英文。
