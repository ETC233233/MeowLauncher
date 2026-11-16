# mclauncher/tools

正式工具（构建/守卫/截图/锁/上下文交接）：

| 文件 | 用途 |
| --- | --- |
| `guard.py` | 设计红线扫描（8+2 规则） |
| `meowlock.py` | build / run / shot 统一文件锁 |
| `shot_page.py` | 真机截图（UIA 导航、页头+内容就绪校验、hover 动作） |
| `prefs_guard.py` | shot 期间 app.json 的窗口键保护 |
| `context.py` | 新会话 30 秒接续（契约/验收/ADR/红线/截图） |

`probes/` 是一次性诊断脚本，不参与构建与验收。保留原因：需要时回查当时怎么量的，别再重写。
