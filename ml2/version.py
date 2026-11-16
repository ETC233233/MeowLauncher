"""版本号与 User-Agent 的唯一来源。

MCIM（mod.mcimirror.top）要求所有请求带登记过的 `名称/版本号` UA，
不能是空 UA 或 urllib 默认 UA；以前各个模块各写一份（2.0.0 / 4.0 / 4.4 / 5.0），
镜像侧看到的是不同的客户端，这里统一成一份。
"""
from __future__ import annotations

import os

APP_NAME = "MeowLauncher"
APP_VERSION = "review5.0"
# 启动器传入其构建版本；独立运行 Python 工具时沿用原有的产品 UA。
USER_AGENT = os.environ.get("MEOW_HTTP_USER_AGENT") or f"{APP_NAME}/5.0"
