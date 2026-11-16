#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ml2.net 下载引擎自测：本地起一个 HTTP/1.1 服务（不联网），验证
   keep-alive 复用、失效连接自动重试、Range 分段并发、服务器不认 Range 时退回单连接、
   重定向、404 / 半截响应不留文件、.part 清理、线程数设置读取。

用法（在 mclauncher/ 下）：python tools/dl_selftest.py
"""
from __future__ import annotations

import hashlib
import os
import socket
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from ml2 import net  # noqa: E402

failed = 0


def T(name: str, ok: bool, info: str = "") -> None:
    global failed
    print(("PASS " if ok else "FAIL ") + name + (" | " + info if info else ""))
    if not ok:
        failed += 1


BIG = os.urandom(20 * 1024 * 1024)                  # 20 MB，超过分段阈值
SMALL = [os.urandom(2000 + i) for i in range(40)]
stats = {"conns": 0, "reqs": 0, "ranges": 0, "mode_norange": False}
lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        with lock:
            stats["conns"] += 1

    def log_message(self, *a):  # 安静
        pass

    def _send(self, code, body, extra=None):
        self.send_response(code)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        with lock:
            stats["reqs"] += 1
        p = self.path
        if p.startswith("/small/"):
            self._send(200, SMALL[int(p.split("/")[-1])])
        elif p == "/big":
            rng = self.headers.get("Range")
            if rng:
                with lock:
                    stats["ranges"] += 1
                a, b = rng.split("=")[1].split("-")
                a, b = int(a), int(b)
                self._send(206, BIG[a:b + 1], {"Content-Range": f"bytes {a}-{b}/{len(BIG)}", "Accept-Ranges": "bytes"})
            else:
                self._send(200, BIG, {"Accept-Ranges": "bytes"})
        elif p == "/big-norange":
            self._send(200, BIG)                    # 不认 Range：永远回整个文件
        elif p == "/redir":
            self._send(302, b"", {"Location": "/small/3"})
        elif p == "/404":
            self._send(404, b"not found")
        elif p == "/short":
            self.send_response(200)
            self.send_header("Content-Length", "100000")
            self.end_headers()
            self.wfile.write(b"x" * 10)             # 声称 100000 字节，只发 10 字节就断
            self.close_connection = True
        elif p == "/idle-close":
            self._send(200, SMALL[5])
            self.close_connection = True            # 响应没带 Connection: close，却把连接关了（服务器空闲超时的样子）
        else:
            self._send(404, b"?")


srv = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
srv.daemon_threads = True
port = srv.server_address[1]
threading.Thread(target=srv.serve_forever, daemon=True).start()
base = f"http://127.0.0.1:{port}"
tmp = Path(tempfile.mkdtemp(prefix="dl-selftest-"))
sha = lambda b: hashlib.sha256(b).hexdigest()

# 1) 单线程连下 30 个小文件：应该复用同一条连接
stats.update(conns=0, reqs=0)
ok = True
for i in range(30):
    d = net._download(f"{base}/small/{i}", tmp / "s" / f"{i}.bin")
    ok = ok and d.read_bytes() == SMALL[i]
T("30 small files downloaded intact", ok)
T("keep-alive: 30 requests reused one connection", stats["conns"] == 1, f"connections={stats['conns']} requests={stats['reqs']}")

# 2) 并发：8 个线程各下 5 个：连接数不超过线程数（不是每个文件一条）
stats.update(conns=0, reqs=0)
from concurrent.futures import ThreadPoolExecutor
def grab(i):
    return net._download(f"{base}/small/{i}", tmp / "p" / f"{i}.bin").read_bytes() == SMALL[i]
with ThreadPoolExecutor(8) as ex:
    res = list(ex.map(grab, range(40)))
T("40 small files with 8 threads intact", all(res))
T("8 threads used at most 8 connections for 40 files", stats["conns"] <= 8, f"connections={stats['conns']}")

# 3) 服务器悄悄关了空闲连接：下一次复用失败，要自动换新连接重试
net._download(f"{base}/idle-close", tmp / "idle1.bin")
d = net._download(f"{base}/small/7", tmp / "idle2.bin")
T("stale pooled connection is retried transparently", d.read_bytes() == SMALL[7])

# 4) 重定向
d = net._download(f"{base}/redir", tmp / "redir.bin")
T("redirect followed", d.read_bytes() == SMALL[3])

# 5) 404：抛错，不留文件，也不留 .part
dest = tmp / "e" / "missing.bin"
try:
    net._download(f"{base}/404", dest)
    raised = False
except Exception as e:
    raised = "404" in str(e)
T("404 raises, no file and no .part left", raised and not dest.exists() and not dest.with_name(dest.name + ".part").exists())

# 6) 半截响应：抛错，不留文件
dest = tmp / "e" / "short.bin"
try:
    net._download(f"{base}/short", dest)
    raised = False
except Exception:
    raised = True
T("truncated response raises, no half file left", raised and not dest.exists() and not dest.with_name(dest.name + ".part").exists())

# 7) 大文件分段：内容完全一致，用了 4 个 Range 请求
stats.update(conns=0, reqs=0, ranges=0)
seen = []
dest = tmp / "big.bin"
t = time.time()
net._download(f"{base}/big", dest, on_progress=lambda a, b: seen.append((a, b)))
T("20 MB file by 4 Range segments is byte-identical", dest.read_bytes() == BIG, f"{time.time() - t:.2f}s")
T("used 4 Range requests", stats["ranges"] == 4, f"ranges={stats['ranges']}")
T("progress reached 100%", bool(seen) and seen[-1][0] == len(BIG) and seen[-1][1] == len(BIG))
T("no .part left after success", not dest.with_name(dest.name + ".part").exists())

# 8) 服务器不认 Range：退回单连接，内容一致
dest = tmp / "big2.bin"
net._download(f"{base}/big-norange", dest)
T("server ignoring Range falls back to single stream, identical", dest.read_bytes() == BIG)

# 9) 线程数设置：读得到、夹在 1~32
n = net._dl_threads()
T("download threads setting readable and within 1..32", 1 <= n <= 32, f"threads={n}")

# 10) 库并发下载：install_vanilla 用的并发逻辑——直接用 ensure 式并发验证不丢文件
import json
mc = tmp / "mc"
libs = [{"downloads": {"artifact": {"path": f"lib/{i}.jar", "url": f"{base}/small/{i}", "size": len(SMALL[i])}}} for i in range(25)]
vj = {"id": "t", "libraries": libs}
prog = []
net.install_vanilla(mc, vj, on_progress=lambda ph, d, t: prog.append((ph, d, t)))
ok = all((mc / "libraries" / "lib" / f"{i}.jar").read_bytes() == SMALL[i] for i in range(25))
T("install_vanilla downloads 25 libraries in parallel, all intact", ok)
T("library progress ends at total", any(p == ("libraries", 25, 25) for p in prog), str(prog[-3:]))

srv.shutdown()
print("ALL PASS" if failed == 0 else f"{failed} FAILED")
sys.exit(0 if failed == 0 else 1)
