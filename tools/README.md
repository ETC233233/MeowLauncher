# mclauncher/tools

对使用者有价值、能独立跑的自检工具：

| 文件 | 用途 |
| --- | --- |
| `selfcheck_backend.py` | 后端联调自检：给一个版本号（如 `python tools/selfcheck_backend.py 26.3`），逐项检查版本解析、资源索引、库文件、启动参数，全 PASS 才算后端可用 |
| `dl_selftest.py` | 下载器自检：并发分块、断点续传、校验与失败重试的端到端验证 |

构建、截图、发版用的本机脚本（`guard.py`、`shot_page.py`、`meowlock.py`、`prefs_guard.py`、`promote.ps1` 等）写死了本机路径与流程，不随仓库发布，见 `.gitignore`。
