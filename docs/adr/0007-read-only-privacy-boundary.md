# 只读与隐私边界

工具只读取本地日志、应用主文档与侧栏元数据、本地调试连接的路由元数据；不读取对话正文、消息、请求体、凭据或密钥，不修改 Codex 安装文件与页面，不调用中转接口。诊断输出含个人会话标识与元数据，只留在本机、不随源码或发布包分发；仓库不包含真实会话、个人价格与设置。

来源：UPSTREAM.md（1.1.3）；README.zh-CN.md（目录与数据、诊断说明）；CodexDebugRouteReader.cs 头部注释；AGENTS.md（Security and Local Data）。
