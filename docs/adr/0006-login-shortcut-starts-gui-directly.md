# 启动入口收敛为登录快捷方式直指 GUI EXE

登录快捷方式直接指向无控制台 Windows GUI EXE（参数留空），移除旧常驻 PowerShell 监视器与登录／每分钟计划任务；旧入口（--watch-codex、--login-startup、--launch-codex）兼容为进入真正的图形单例。此前“监视器拉起工具”的架构会分配控制台窗口，且同名进程检查会把监视器误认为悬浮工具而漏启动。

来源：ACCEPTANCE-archive.zh-CN.md（1.1.8 段）；CHANGELOG 1.1.8、1.3.5；ACCEPTANCE.zh-CN.md（1.3.5 段）。
