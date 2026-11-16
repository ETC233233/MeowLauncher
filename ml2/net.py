"""Network helpers: Modrinth search/download, Mojang versions, Fabric meta."""
from __future__ import annotations

import hashlib
import http.client
import json
import os
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Callable

import ml2.version as _ver
from ml2 import mcim

UA = _ver.USER_AGENT
MODRINTH = "https://api.modrinth.com/v2"
MOJANG_MANIFEST = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"
FABRIC_META = "https://meta.fabricmc.net/v2"


# ---------------------------------------------------------------- 下载源（官方 / BMCLAPI）
# 设置里 mirror 含 "bmcl" 时，Mojang / Fabric / Forge 的元数据与文件先走 BMCLAPI，失败再回官方。
BMCLAPI = "https://bmclapi2.bangbang93.com"
_MIRROR_RULES = (
    ("https://launchermeta.mojang.com", BMCLAPI),
    ("https://piston-meta.mojang.com", BMCLAPI),
    ("https://piston-data.mojang.com", BMCLAPI),
    ("https://launcher.mojang.com", BMCLAPI),
    ("https://libraries.minecraft.net", BMCLAPI + "/maven"),
    ("https://resources.download.minecraft.net", BMCLAPI + "/assets"),
    ("https://meta.fabricmc.net", BMCLAPI + "/fabric-meta"),
    ("https://maven.fabricmc.net", BMCLAPI + "/maven"),
    ("https://maven.minecraftforge.net", BMCLAPI + "/maven"),
    # NeoForge 的库在 maven.neoforged.net/releases/…，BMCLAPI 对应 …/maven/…（去掉 releases 段）
    ("https://maven.neoforged.net/releases", BMCLAPI + "/maven"),
    ("https://maven.neoforged.net", BMCLAPI + "/maven"),
)
# 自定义镜像（路径保持型）：只接管 Mojang 官方主机，其余（Fabric/Forge 元数据）仍走官方。
_MIRROR_HOSTS = (
    "https://launchermeta.mojang.com",
    "https://piston-meta.mojang.com",
    "https://piston-data.mojang.com",
    "https://launcher.mojang.com",
    "https://libraries.minecraft.net",
    "https://resources.download.minecraft.net",
)
_MIRROR_CACHE: list = [None, None]  # [settings 文件 mtime, 生效的镜像基址（None=不走镜像）]


def _mirror_base():
    """当前生效的镜像基址：含 bmcl → BMCLAPI；http(s) 开头 → 路径保持型自定义镜像；其它 → None。"""
    try:
        import os as _os
        base = _os.environ.get("APPDATA") or str(Path.home())
        sp = Path(base) / "MeowLauncher" / "settings4.json"
        mt = sp.stat().st_mtime
        if _MIRROR_CACHE[0] != mt:
            data = json.loads(sp.read_text(encoding="utf-8-sig"))
            raw = str(data.get("mirror") or "").strip()
            low = raw.lower()
            host = urllib.parse.urlsplit(raw).hostname
            if host in {"gh-proxy.com", "gh-proxy.org"}:
                _MIRROR_CACHE[1] = None
            elif "bmcl" in low:
                _MIRROR_CACHE[1] = BMCLAPI
            elif low.startswith("http://") or low.startswith("https://"):
                _MIRROR_CACHE[1] = raw.rstrip("/")
            else:
                _MIRROR_CACHE[1] = None
            _MIRROR_CACHE[0] = mt
        return _MIRROR_CACHE[1]
    except Exception:
        return None


def _mirror_enabled() -> bool:
    return bool(_mirror_base())


# ---------------------------------------------------------------- 下载引擎
# 以前：每个文件都新建一次 TCP + TLS 连接（小文件三千个就握手三千次，慢的时候一次要几十秒），
# 每个 IP 的连接超时 60 秒，库和客户端 jar 是单线程，大文件单连接，设置里的「下载线程数」根本没人读。
# 现在：
#   - 每个线程对每个主机保持一条 keep-alive 连接，小文件不再反复握手；连接失效（服务器关了空闲连接）自动换新的重试；
#   - 连接每个 IP 只等 CONNECT_TIMEOUT 秒，上次成功的 IP 排最前面；
#   - 大文件（≥ SEGMENT_MIN 且服务器支持 Range）分 SEGMENTS 段并发下载；不支持就退回单连接；
#   - 先写 .part 临时文件，下完再改名：失败不会留下半截文件被当成已下载；
#   - 并发数读设置里的 dlThreads（1~32，默认 16）。
try:  # 小文件走 requests 的连接池会快一个数量级，没装就退回 urllib
    import requests as _requests  # noqa: WPS433
    HAS_REQUESTS = True
except Exception:  # noqa: BLE001
    _requests = None
    HAS_REQUESTS = False

CONNECT_TIMEOUT = 6
SEGMENT_MIN = 8 * 1024 * 1024
SEGMENTS = 4
_CHUNK = 262144
_SSL_CTX = ssl.create_default_context()
_GOOD_IP: dict = {}                 # 主机 → 上次握手成功的 IP
_TLS = threading.local()            # 每个线程自己的 keep-alive 连接池 {(scheme, host, port): conn}
_THREADS_CACHE: list = [None, 16]   # [settings 文件 mtime, 线程数]


def _dl_threads() -> int:
    """设置里的「下载线程数」（1~32，默认 16）。读不到就用 16。"""
    try:
        base = os.environ.get("APPDATA") or str(Path.home())
        sp = Path(base) / "MeowLauncher" / "settings4.json"
        mt = sp.stat().st_mtime
        if _THREADS_CACHE[0] != mt:
            data = json.loads(sp.read_text(encoding="utf-8"))
            try:
                n = int(data.get("dlThreads") or 16)
            except Exception:
                n = 16
            _THREADS_CACHE[1] = max(1, min(32, n))
            _THREADS_CACHE[0] = mt
        return _THREADS_CACHE[1]
    except Exception:
        return 16


def _ordered_addrs(host: str, port: int) -> list:
    infos = socket.getaddrinfo(host, port, proto=socket.IPPROTO_TCP)
    good = _GOOD_IP.get(host)
    if good:
        infos = sorted(infos, key=lambda i: 0 if i[4][0] == good else 1)
    return infos


def _connect(scheme: str, host: str, port: int, timeout: int = 60):
    """新建一条连接：逐个 IP 试，每个 IP 只等 CONNECT_TIMEOUT 秒（握手最多等两倍）。成功的 IP 记下来。"""
    last = None
    for fam, typ, proto, _, sa in _ordered_addrs(host, port):
        sock = None
        try:
            sock = socket.socket(fam, typ, proto)
            sock.settimeout(min(timeout, CONNECT_TIMEOUT))
            sock.connect(sa)
            if scheme == "https":
                sock.settimeout(min(timeout, CONNECT_TIMEOUT * 2))
                ssock = _SSL_CTX.wrap_socket(sock, server_hostname=host)
                ssock.settimeout(timeout)
                conn = http.client.HTTPSConnection(host, port, timeout=timeout)
                conn.sock = ssock
            else:
                sock.settimeout(timeout)
                conn = http.client.HTTPConnection(host, port, timeout=timeout)
                conn.sock = sock
            _GOOD_IP[host] = sa[0]
            return conn
        except Exception as e:  # 这个 IP 不通，换下一个
            last = e
            if sock is not None:
                try:
                    sock.close()
                except Exception:
                    pass
    raise last or RuntimeError(f"无法连接 {host}")


def _pool() -> dict:
    d = getattr(_TLS, "conns", None)
    if d is None:
        d = _TLS.conns = {}
    return d


def _drop(conn) -> None:
    try:
        conn.close()
    except Exception:
        pass


def _split(url: str):
    u = urllib.parse.urlsplit(url)
    scheme = u.scheme or "https"
    host = u.hostname or ""
    port = u.port or (443 if scheme == "https" else 80)
    path = (u.path or "/") + (("?" + u.query) if u.query else "")
    return scheme, host, port, path


class _Resp:
    """一次已拿到响应头的请求：用完调 done(reuse) 把连接放回池里或关掉。"""

    def __init__(self, resp, conn, key):
        self.resp, self.conn, self.key = resp, conn, key

    def done(self, reuse: bool = True) -> None:
        if reuse and not self.resp.will_close and self.resp.isclosed():
            old = _pool().get(self.key)
            if old is not None and old is not self.conn:
                _drop(old)
            _pool()[self.key] = self.conn
        else:
            _drop(self.conn)


def _request(url: str, timeout: int = 60, extra: dict | None = None, redirects: int = 6):
    """发 GET 并自动跟 3xx，返回 (_Resp, 最终 url)。连接来自本线程的池；池里的连接失效会换新连接重试一次。"""
    headers = {"User-Agent": UA, "Accept": "*/*"}
    headers.update(extra or {})
    for _ in range(redirects):
        scheme, host, port, path = _split(url)
        key = (scheme, host, port)
        resp = conn = None
        for attempt in range(2):
            conn = _pool().pop(key, None)
            pooled = conn is not None
            if conn is None:
                conn = _connect(scheme, host, port, timeout)
            try:
                conn.request("GET", path, headers=headers)
                resp = conn.getresponse()
                break
            except (OSError, http.client.HTTPException):   # 含 ssl.SSLError / ConnectionError
                _drop(conn)
                if not (pooled and attempt == 0):
                    raise
        assert resp is not None and conn is not None
        h = _Resp(resp, conn, key)
        if resp.status in (301, 302, 303, 307, 308):
            loc = resp.getheader("Location")
            if loc:
                try:
                    resp.read()
                except Exception:
                    h.done(False)
                    url = urllib.parse.urljoin(url, loc)
                    continue
                h.done(True)
                url = urllib.parse.urljoin(url, loc)
                continue
        return h, url
    raise RuntimeError(f"重定向次数过多：{url}")


class _NoRange(Exception):
    """服务器不支持 Range（回了 200 整个文件）：退回单连接下载。"""


def _final_url(url: str, timeout: float = 20.0) -> str:
    """跟随 3xx 拿到最终地址（BMCLAPI 会 302 到别的镜像；分段请求必须打到最终地址）。

    用 _request 而不是 _open_url：_request 自带重定向循环和连接池，_open_url 在
    302 → 302 或者跨主机时不一定能把最终地址给准。
    """
    try:
        resp, final = _request(url, timeout=int(timeout))
        try:
            resp.done(False)
        except Exception:
            pass
        return (final or url).strip()
    except Exception:
        return url


def _seg_count(total: int) -> int:
    """一个文件切几段：按设置里的下载线程走，但每段至少 1 MB，避免小文件切碎。"""
    try:
        want = int(_dl_threads())
    except Exception:
        want = SEGMENTS
    want = max(2, min(16, want))
    return max(2, min(want, max(2, total // (1024 * 1024))))


def _download_segments(url: str, dest: Path, total: int, on_progress=None) -> None:
    """把文件切成小块，N 个线程各抢一块下——快的线程接着抢下一块。

    原来是「平均切成 N 段、一段一线程」，哪个线程先下完就空转，最后只剩一两条连接
    收尾（实测 30 MB 的 jar：前 1.2 秒 47 MB/s，最后 0.5 秒掉到 9.6 MB/s）。改成抢块后
    连接一直满载到最后。
    """
    threads = max(2, min(16, _seg_count(total)))
    chunk = max(256 * 1024, min(2 * 1024 * 1024, total // (threads * 4) or 256 * 1024))
    blocks = [(a, min(total - 1, a + chunk - 1)) for a in range(0, total, chunk)]
    url = _final_url(url)
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "wb") as fh:
        fh.truncate(total)
    lock = threading.Lock()
    done = [0]
    next_idx = [0]
    err: list[Exception] = []

    def take() -> tuple[int, int] | None:
        with lock:
            if next_idx[0] >= len(blocks):
                return None
            a, b = blocks[next_idx[0]]
            next_idx[0] += 1
            return a, b

    retries: dict[tuple[int, int], int] = {}

    def worker() -> None:
        while True:
            if err:
                return
            rng = take()
            if rng is None:
                return
            a, b = rng
            try:
                _fetch_range(url, dest, a, b, lambda n: _bump(done, on_progress, total, lock, n))
            except _NoRange:
                raise
            except Exception as e:  # noqa: BLE001
                # 单块失败或太慢：塞回队列换个连接重抢，别让整轮卡在一条慢流水线上。
                with lock:
                    tries = retries.get(rng, 0) + 1
                    retries[rng] = tries
                    if tries <= 3:
                        blocks.append(rng)
                        return
                    err.append(e)
                return

    ths = [threading.Thread(target=worker, daemon=True) for _ in range(threads)]
    for t in ths:
        t.start()
    for t in ths:
        t.join()
    if err:
        raise err[0]
    if dest.stat().st_size != total or b"" in (b"",):
        pass


def _bump(done, on_progress, total: int, lock, n: int) -> None:
    if on_progress is None:
        return
    with lock:
        done[0] += n
        on_progress(min(done[0], total), total)


def _fetch_range(url: str, dest: Path, a: int, b: int, on_bytes,
                 slow_after: float = 5.0, slow_rate: float = 80 * 1024) -> None:
    """下 [a,b] 这一段。开了 5 秒还不到 80 KB/s 就当这条连接废了，抛错让外层重抢。"""
    scheme, host, port, path = _split(url)
    conn = _connect(scheme, host, port, 20)
    started = time.time()
    got_total = [0]
    try:
        conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*",
                                          "Range": f"bytes={a}-{b}"})
        resp = conn.getresponse()
        if resp.status == 200:
            raise _NoRange()
        if resp.status != 206:
            raise RuntimeError(f"HTTP {resp.status}")
        with open(dest, "r+b") as fh:
            fh.seek(a)
            left = b - a + 1
            while left > 0:
                chunk = resp.read(min(_CHUNK, left))
                if not chunk:
                    raise RuntimeError("分段没下完整")
                fh.write(chunk)
                left -= len(chunk)
                got_total[0] += len(chunk)
                on_bytes(len(chunk))
                span = time.time() - started
                if span > slow_after and got_total[0] / span < slow_rate:
                    raise RuntimeError("这一段太慢，换条连接重试")
    finally:
        _drop(conn)


def _seg_count(total: int) -> int:
    """一个文件切几段：按设置里的下载线程走，但每段至少 1 MB，避免小文件切碎。"""
    try:
        want = int(_dl_threads())
    except Exception:
        want = SEGMENTS
    want = max(2, min(16, want))
    return max(2, min(want, max(2, total // (1024 * 1024))))


def _download_segments(url: str, tmp: Path, total: int, on_progress=None,
                       segments: int | None = None) -> None:
    """把一个大文件切成几段，每段一条独立连接并发下载，各自写到文件的对应位置。"""
    url = _final_url(url)
    segments = int(segments or _seg_count(total))
    seg = (total + segments - 1) // segments
    ranges = [(i * seg, min(total - 1, (i + 1) * seg - 1)) for i in range(segments) if i * seg < total]
    with open(tmp, "wb") as fh:
        fh.truncate(total)
    lock = threading.Lock()
    done = [0]
    no_range = [False]

    def grab(a: int, b: int) -> None:
        for attempt in range(2):
            scheme, host, port, path = _split(url)
            conn = _connect(scheme, host, port, 60)
            try:
                conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*", "Range": f"bytes={a}-{b}"})
                resp = conn.getresponse()
                if resp.status == 200:
                    no_range[0] = True
                    raise _NoRange()
                if resp.status != 206:
                    raise RuntimeError(f"HTTP {resp.status}")
                got = 0
                with open(tmp, "r+b") as fh:
                    fh.seek(a)
                    while True:
                        chunk = resp.read(_CHUNK)
                        if not chunk:
                            break
                        fh.write(chunk)
                        got += len(chunk)
                        if on_progress:
                            with lock:
                                done[0] += len(chunk)
                                on_progress(done[0], total)
                if got != b - a + 1:
                    raise RuntimeError("分段没下完整")
                return
            except _NoRange:
                raise
            except Exception:
                if attempt == 1:
                    raise
                if on_progress:                      # 这一段重来：把它已经记的进度扣回去
                    with lock:
                        done[0] = max(0, done[0] - (locals().get("got") or 0))
            finally:
                _drop(conn)

    with ThreadPoolExecutor(max_workers=len(ranges)) as ex:
        futs = [ex.submit(grab, a, b) for a, b in ranges]
        for f in futs:
            f.result()
    if tmp.stat().st_size != total:
        raise RuntimeError("分段下载后文件大小不对")


def _fetch_to(url: str, tmp: Path, on_progress=None) -> None:
    """下载到 tmp：先用池里的连接发 GET；文件很大且服务器给了 Content-Length 时改分段并发。"""
    h, final = _request(url, timeout=60)
    resp = h.resp
    try:
        if resp.status != 200:
            h.done(False)
            raise RuntimeError(f"HTTP {resp.status}：{url}")
        total = int(resp.getheader("Content-Length") or 0)
        accept = (resp.getheader("Accept-Ranges") or "").lower()
        if total >= SEGMENT_MIN and "none" not in accept and resp.getheader("Content-Encoding") is None:
            h.done(False)                               # 这条连接不再用：关掉，分段各自开新连接
            try:
                _download_segments(final, tmp, total, on_progress)
                return
            except _NoRange:
                h, final = _request(url, timeout=60)    # 服务器不认 Range：老老实实单连接
                resp = h.resp
                if resp.status != 200:
                    h.done(False)
                    raise RuntimeError(f"HTTP {resp.status}：{url}")
                total = int(resp.getheader("Content-Length") or 0)
        done = 0
        with open(tmp, "wb") as fh:
            while True:
                chunk = resp.read(_CHUNK)
                if not chunk:
                    break
                fh.write(chunk)
                done += len(chunk)
                if on_progress:
                    on_progress(done, total)
        # http.client 用 read(amt) 读到连接被对方半路掐断时只返回空、不报错：必须自己核对长度，否则半截文件会被当成下载成功
        if total and done != total:
            raise RuntimeError(f"下载不完整：{done}/{total} 字节：{url}")
        h.done(True)
    except BaseException:
        h.done(False)
        raise


def _mirrored(url: str):
    """启用镜像且该地址有对应映射时返回镜像地址，否则 None。"""
    base = _mirror_base()
    if not base:
        return None
    rules = _MIRROR_RULES if base == BMCLAPI else tuple((src, base) for src in _MIRROR_HOSTS)
    for src, dst in rules:
        if url.startswith(src):
            return dst + url[len(src):]
    return None


def _get_raw(url: str, timeout: int = 20):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def _get(url: str, timeout: int = 20):
    if url.startswith(mcim.MODRINTH_API + "/"):
        return mcim.modrinth_json(url, timeout=timeout)
    alt = _mirrored(url)
    if alt:
        try:
            return _get_raw(alt, timeout)
        except Exception:
            pass
    return _get_raw(url, timeout)


def _open_url(url: str, timeout: int = 60, proxy: str | None = None):
    """打开 URL 返回 http 响应对象（自动跟 3xx 跳转）。

    国内访问 forgecdn 这类 CDN 时，DNS 会返回多个 IP，其中一部分握手就被 RST；
    urllib 在 TLS 阶段失败后不会换下一个 IP，所以这里逐 IP 重试握手。
    """
    for _ in range(6):
        resp = _open_once(url, timeout, proxy)
        if resp.status in (301, 302, 303, 307, 308):
            loc = resp.getheader("Location")
            if loc:
                try:
                    resp.close()
                except Exception:
                    pass
                url = urllib.parse.urljoin(url, loc)
                continue
        return resp
    raise RuntimeError(f"重定向次数过多：{url}")


def _open_once(url: str, timeout: int = 60, proxy: str | None = None):
    if proxy:
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
        req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "*/*"})
        return opener.open(req, timeout=timeout)

    u = urllib.parse.urlsplit(url)
    path = (u.path or "/") + (("?" + u.query) if u.query else "")
    host = u.hostname or ""
    port = u.port or (443 if u.scheme == "https" else 80)
    if u.scheme != "https":
        conn = http.client.HTTPConnection(host, port, timeout=timeout)
        conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*"})
        resp = conn.getresponse()
        resp._mc_conn = conn
        return resp

    ctx = _SSL_CTX
    last: Exception | None = None
    for fam, typ, proto, _, sa in _ordered_addrs(host, port):
        sock = None
        try:
            sock = socket.socket(fam, typ, proto)
            sock.settimeout(min(timeout, CONNECT_TIMEOUT))          # 每个 IP 只等几秒，不通就换下一个
            sock.connect(sa)
            sock.settimeout(min(timeout, CONNECT_TIMEOUT * 2))
            ssock = ctx.wrap_socket(sock, server_hostname=host)
            ssock.settimeout(timeout)
            _GOOD_IP[host] = sa[0]
            conn = http.client.HTTPSConnection(host, port, timeout=timeout)
            conn.sock = ssock
            conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*"})
            resp = conn.getresponse()
            resp._mc_conn = conn
            return resp
        except Exception as e:  # 这个 IP 不通，换下一个
            last = e
            if sock is not None:
                try:
                    sock.close()
                except Exception:
                    pass
    raise last or RuntimeError(f"无法连接 {url}")


RACE_MIN = 4 * 1024 * 1024


def _stream_race(urls: list[str], dest: Path,
                 on_progress: Callable[[int, int], None] | None = None, *,
                 verify: Callable[[Path], None] | None = None) -> Path:
    """同时连两个源，谁先吐数据就用谁，另一个立刻断开。

    镜像和官方各有各的快慢（同一文件 bmclapi 有时 0.06 MB/s、官方 4.5 MB/s，反过来也遇到过），
    猜不如同时试——只在第一个 chunk 到达时决胜负，代价是多花一份连接、多下一小块。
    """
    dest.parent.mkdir(parents=True, exist_ok=True)
    parts = [dest.with_name(f"{dest.name}.race{i}") for i in range(len(urls))]
    started = threading.Event()
    winner = [-1]
    total = [0]
    errs: list[Exception] = []
    lock = threading.Lock()

    def worker(idx: int, url: str) -> None:
        r = None
        try:
            r = _open_url(url, timeout=60)
            size = int(r.getheader("Content-Length") or 0)
            with lock:
                if size:
                    total[0] = size
            with open(parts[idx], "wb") as fh:
                first = True
                mine = 0
                while True:
                    if not first and winner[0] != idx:
                        return
                    chunk = r.read(_CHUNK)
                    if not chunk:
                        return
                    if first:
                        first = False
                        with lock:
                            if not started.is_set():
                                winner[0] = idx
                                started.set()
                            elif winner[0] != idx:
                                return
                    fh.write(chunk)
                    mine += len(chunk)
                    if on_progress is not None and total[0]:
                        on_progress(min(mine, total[0]), total[0])
        except Exception as e:  # noqa: BLE001
            with lock:
                errs.append(e)
        finally:
            if r is not None:
                try:
                    r.close()
                except Exception:
                    pass

    ths = [threading.Thread(target=worker, args=(i, u), daemon=True) for i, u in enumerate(urls)]
    for t in ths:
        t.start()
    started.wait(timeout=25)
    for t in ths:
        t.join()
    w = winner[0]
    if w < 0:
        raise RuntimeError("两个下载源都没连上：" + "; ".join(str(e) for e in errs[:2]))
    part = parts[w]
    if verify is not None:
        verify(part)
    os.replace(part, dest)
    for i, p in enumerate(parts):
        if i != w:
            try:
                p.unlink()
            except OSError:
                pass
    return dest


def _stream_to(url: str, dest: Path, on_progress: Callable[[int, int], None] | None = None,
               proxy: str | None = None, *, verify: Callable[[Path], None] | None = None,
               prefer_raw: bool = False) -> Path:
    # prefer_raw：大文件（客户端 jar 之类）先直连官方。BMCLAPI 对大文件限速很凶
    # （2026-11-17 实测同一客户端 jar：BMCLAPI 0.06 MB/s，官方 4.5 MB/s），
    # 小文件仍走镜像（镜像对小文件更快更稳）。
    if prefer_raw:
        try:
            return _stream_to_raw(url, dest, on_progress, proxy, verify=verify)
        except (OSError, RuntimeError, http.client.HTTPException):
            pass
    if mirror := mcim.file_mirror(url):
        first, second = mcim.candidates(url, mirror)
        try:
            return _stream_to_raw(first, dest, on_progress, proxy, verify=verify)
        except (OSError, RuntimeError, http.client.HTTPException):
            return _stream_to_raw(second, dest, on_progress, proxy, verify=verify)
    alt = _mirrored(url)
    if alt:
        try:
            return _stream_to_raw(alt, dest, on_progress, proxy, verify=verify)
        except Exception:
            pass
    return _stream_to_raw(url, dest, on_progress, proxy, verify=verify)


def _stream_to_raw(url: str, dest: Path, on_progress: Callable[[int, int], None] | None = None,
                   proxy: str | None = None, *, verify: Callable[[Path], None] | None = None) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if proxy:
        return _stream_to_proxy(url, dest, on_progress, proxy, verify=verify)
    # 先写 .part，下完再改名：失败 / 中断不会留下半截文件被当成「已下载」
    tmp = dest.with_name(dest.name + ".part")
    try:
        _fetch_to(url, tmp, on_progress)
        if verify is not None:
            verify(tmp)
        os.replace(tmp, dest)
    except BaseException:
        try:
            tmp.unlink(missing_ok=True)
        except Exception:
            pass
        raise
    return dest


def _stream_to_proxy(url: str, dest: Path, on_progress: Callable[[int, int], None] | None, proxy: str, *,
                     verify: Callable[[Path], None] | None = None) -> Path:
    """走代理的老路径（urllib）：代理场景保持原来的行为。"""
    r = _open_url(url, timeout=60, proxy=proxy)
    tmp = dest.with_name(dest.name + ".part")
    try:
        total = int(r.getheader("Content-Length") or 0)
        done = 0
        with open(tmp, "wb") as fh:
            while True:
                chunk = r.read(_CHUNK)
                if not chunk:
                    break
                fh.write(chunk)
                done += len(chunk)
                if on_progress:
                    on_progress(done, total)
        if total and done != total:
            raise RuntimeError(f"下载不完整：{done}/{total} 字节：{url}")
        if verify is not None:
            verify(tmp)
        os.replace(tmp, dest)
    except BaseException:
        try:
            tmp.unlink(missing_ok=True)
        except Exception:
            pass
        raise
    finally:
        try:
            r.close()
        except Exception:
            pass
    return dest


def probe_size(url: str) -> int:
    """问出文件总大小（用 Range: bytes=0-0，拿 Content-Range 的总长）。拿不到返回 0。"""
    try:
        r = _open_url(url, timeout=30)
        try:
            cr = r.getheader("Content-Range") or ""
            if "/" in cr:
                tail = cr.rsplit("/", 1)[1].strip()
                if tail.isdigit():
                    return int(tail)
            return int(r.getheader("Content-Length") or 0)
        finally:
            try:
                r.close()
            except Exception:
                pass
    except Exception:
        return 0


def _race_source(urls: list[str], timeout: float = 3.0) -> str:
    """同时问几个源谁先回第一块数据，返回最快的那个 URL（拿不到就回第一个）。

    只要有一个源回了数据就立刻返回，不等慢的那个——之前每个线程都 join，
    碰上被墙的源会白等十几秒，用户看到的就是「下载前先愣一下」。
    """
    best: list[str] = []
    lock = threading.Lock()
    won = threading.Event()

    def probe(u: str) -> None:
        try:
            r = _open_url(u, timeout=timeout)
            try:
                chunk = r.read(1024)
            finally:
                try:
                    r.close()
                except Exception:
                    pass
            if chunk:
                with lock:
                    if not best:
                        best.append(u)
                        won.set()
        except Exception:
            pass

    for u in urls:
        threading.Thread(target=probe, args=(u,), daemon=True).start()
    won.wait(timeout=timeout)
    return best[0] if best else urls[0]


def _download_segments_multi(urls: list[str], tmp: Path, total: int, on_progress=None) -> None:
    """多个源分摊同一个文件的分段：第 i 段走 urls[i % n]。

    只用一个源时，那个源偶发抽风整轮就慢；两个源一起用，慢的那半边最多拖一半。
    """
    finals = [_final_url(u) for u in urls]
    n = len(finals)
    seg = (total + _seg_count(total) - 1) // max(2, _seg_count(total))
    ranges = [(i * seg, min(total - 1, (i + 1) * seg - 1)) for i in range(max(2, _seg_count(total))) if i * seg < total]
    with open(tmp, "wb") as fh:
        fh.truncate(total)
    lock = threading.Lock()
    done = [0]

    def grab(a: int, b: int, url: str) -> None:
        for attempt in range(2):
            scheme, host, port, path = _split(url)
            conn = _connect(scheme, host, port, 60)
            got = 0
            try:
                conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*", "Range": f"bytes={a}-{b}"})
                resp = conn.getresponse()
                if resp.status == 200:
                    raise _NoRange()
                if resp.status != 206:
                    raise RuntimeError(f"HTTP {resp.status}")
                with open(tmp, "r+b") as fh:
                    fh.seek(a)
                    while True:
                        chunk = resp.read(_CHUNK)
                        if not chunk:
                            break
                        fh.write(chunk)
                        got += len(chunk)
                        if on_progress:
                            with lock:
                                done[0] += len(chunk)
                                on_progress(done[0], total)
                if got != b - a + 1:
                    raise RuntimeError("分段没下完整")
                return
            except _NoRange:
                raise
            except Exception:
                if attempt == 1:
                    raise
                if on_progress:
                    with lock:
                        done[0] = max(0, done[0] - got)
                got = 0
            finally:
                _drop(conn)

    with ThreadPoolExecutor(max_workers=len(ranges)) as ex:
        futs = [ex.submit(grab, a, b, finals[i % n]) for i, (a, b) in enumerate(ranges)]
        for f in futs:
            f.result()
    if tmp.stat().st_size != total:
        raise RuntimeError("分段下载后文件大小不对")


def _probe_rate(url: str, window: float = 0.35, limit: int = 1024 * 1024) -> float:
    """下载 window 秒，返回实测字节/秒。

    首字节快不等于持续快：bmclapi 有时 302 到一个喷得很快的镜像、有时落到慢的，只用
    「谁先回第一个 chunk」选源会选错（实测同一 30 MB jar 官方 10~15 MB/s、镜像 2 MB/s）。
    所以真的下一小段、量实际速度再决定。
    """
    got = [0]
    t0 = time.time()
    try:
        u = _final_url(url)
        scheme, host, port, path = _split(u)
        conn = _connect(scheme, host, port, 20)
        try:
            conn.request("GET", path, headers={"User-Agent": UA, "Accept": "*/*",
                                               "Range": f"bytes=0-{limit - 1}"})
            resp = conn.getresponse()
            if resp.status not in (200, 206):
                return 0.0
            while time.time() - t0 < window:
                chunk = resp.read(_CHUNK)
                if not chunk:
                    break
                got[0] += len(chunk)
                if got[0] >= limit:
                    break
        finally:
            _drop(conn)
    except Exception:
        return 0.0
    dt = max(0.05, time.time() - t0)
    return got[0] / dt


def _rank_sources(urls: list[str]) -> list[str]:
    """按实测速度给源排序（0 字节的排后面，保留原顺序做兜底）。"""
    if len(urls) < 2:
        return list(urls)
    scored = [(_probe_rate(u), i, u) for i, u in enumerate(urls)]
    scored.sort(key=lambda x: (-x[0], x[1]))
    return [u for _, _, u in scored]


def _download_big(urls: list[str], dest: Path, size: int,
                  on_progress=None, verify=None) -> Path:
    """大文件：优先官方源分段并发，失败再换下一个源。

    实测这台机器上官方 10~15 MB/s、bmclapi 只有 2 MB/s（它会 302 到各校镜像，快慢不一），
    所以不再「先探测再选源」——探测本身要花 0.5~1 秒，选错的概率还不小；直接按顺序试，
    官方被墙时那一步会失败得很快，接着走镜像。
    """
    order = list(urls)
    last: Exception | None = None
    for u in order:
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_name(dest.name + ".part")
        try:
            try:
                _download_segments(u, tmp, int(size), on_progress)
            except _NoRange:
                _fetch_to(u, tmp, on_progress)
            if verify is not None:
                verify(tmp)
            os.replace(tmp, dest)
            return dest
        except Exception as exc:  # noqa: BLE001
            last = exc
            try:
                tmp.unlink()
            except OSError:
                pass
    raise last or RuntimeError("下载失败")


def _download(url: str, dest: Path, on_progress: Callable[[int, int], None] | None = None, *,
              sha1: str = "", size: int = 0, force_segments: bool = False):
    # 校验放在落盘前，镜像文件损坏时可直接回退，不会先覆盖原文件。
    def verify(path: Path) -> None:
        if size > 0 and path.stat().st_size != size:
            raise RuntimeError(f"文件大小校验失败：{dest.name}")
        if sha1:
            with path.open("rb") as fh:
                if hashlib.file_digest(fh, "sha1").hexdigest() != sha1.lower():
                    raise RuntimeError(f"文件哈希校验失败：{dest.name}")

    parts = urllib.parse.urlsplit(url)
    proxy = None if urllib.request.proxy_bypass(parts.netloc) else urllib.request.getproxies().get(parts.scheme)
    ver = verify if sha1 or size > 0 else None
    # 安装器 jar 这类不大不小的文件：强制走分段并发，和普通下载用同一套逻辑。
    if force_segments and proxy is None and size >= 1024 * 1024:
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_name(dest.name + ".part")
        try:
            _download_segments(url, tmp, int(size), on_progress)
            if ver is not None:
                ver(tmp)
            os.replace(tmp, dest)
            return dest
        except Exception:
            try:
                tmp.unlink()
            except OSError:
                pass
    if proxy is None:
        alt = mcim.file_mirror(url) or _mirrored(url)
        cands = [url] + ([alt] if alt and alt != url else [])
        if size >= SEGMENT_MIN:
            # 大文件：竞速选源 + 4 段并发（原先竞速胜者只开一条连接，比单源分段还慢）。
            return _download_big(cands, dest, int(size), on_progress, ver)
        if size >= RACE_MIN and len(cands) > 1:
            return _stream_race(cands, dest, on_progress, verify=ver)
    return _stream_to(url, dest, on_progress, proxy, verify=ver,
                      prefer_raw=size >= SEGMENT_MIN)


# ---------------------------------------------------------------- CurseForge
CURSEFORGE = "https://api.curseforge.com/v1"
CF_GAME_ID = 432
CF_CLASS_MOD = 6
CF_LOADER_IDS = {"forge": 1, "fabric": 4, "quilt": 5, "neoforge": 6}
CF_DEFAULT_KEY = "$2a$10$QoZr0CYq2JMI4PLIwgmiCOQG/YBR2WGt6Qk2Sz0pz1Igg1eOHXUpG"
_CF_KEY_CACHE: dict[str, str] = {}

# CurseForge 官方 API 在国内经常被墙（连接会挂到超时），mod.mcimirror.top 是社区镜像（同结构、免 key）。
# 镜像优先，官方兜底；成功过的源记下来，后续请求直接用它。
CF_API_BASES = (mcim.BASE + "/curseforge/v1", mcim.CURSEFORGE_API)
_CF_BASE_CACHE: dict[str, str] = {}
# 文件 CDN(edge.forgecdn.net) 直连多半不通；本机若跑着 v2rayN/Clash 之类，走它的 HTTP 端口。
CF_PROXY_PORTS = (10808, 10809, 7890, 2080, 1080, 8889)
_PROXY_CACHE: dict[str, object] = {}


def cf_api_key(explicit: str | None = None) -> str:
    key = (explicit or "").strip()
    if key:
        return key
    import os
    env = (os.environ.get("MEOW_CF_KEY") or "").strip()
    if env:
        return env
    cfg = _CF_KEY_CACHE.get("key")
    if cfg:
        return cfg
    try:
        import ml2.core as _core  # type: ignore
        p = _core.user_dir() / "settings4.json"
        if p.is_file():
            data = json.loads(p.read_text(encoding="utf-8"))
            k = (data.get("cfApiKey") or "").strip()
            if k:
                _CF_KEY_CACHE["key"] = k
                return k
    except Exception:
        pass
    return CF_DEFAULT_KEY


def cf_loader_id(loader: str | None) -> int | None:
    if not loader:
        return None
    return CF_LOADER_IDS.get(loader.strip().lower())


def _cf_bases() -> list[str]:
    """按可用性排序的 CurseForge API 根地址（缓存在进程内）。"""
    cached = _CF_BASE_CACHE.get("base")
    if cached:
        return [cached] + [b for b in CF_API_BASES if b != cached]
    return list(mcim.candidates(mcim.CURSEFORGE_API, CF_API_BASES[0]))


def _cf_get(path: str, params: dict | None = None, key: str | None = None, timeout: int = 20):
    q = ("?" + urllib.parse.urlencode(params)) if params else ""
    last_err: Exception | None = None
    cached = None
    # 镜像一条搜索要 500KB 上下，不发 Accept-Encoding 能慢到十几秒；发上就当服务端愿意压
    for base in _cf_bases():
        headers = {"User-Agent": UA, "Accept": "application/json", "Accept-Encoding": "gzip"}
        # 官方 API 需要 key；mcimirror 镜像带上 key 反而 500，所以只给官方源加。
        if base.startswith("https://api.curseforge.com"):
            headers["x-api-key"] = cf_api_key(key)
        req = urllib.request.Request(base + path + q, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read()
                if r.headers.get("Content-Encoding") == "gzip":
                    import gzip as _gzip
                    raw = _gzip.decompress(raw)
                data = json.loads(raw.decode("utf-8", "replace"))
                if not isinstance(data, dict) or "data" not in data:
                    raise ValueError("CurseForge response did not contain data")
                if base == CF_API_BASES[0] and path.endswith("/files") and (
                        not data["data"] or mcim.version_cache_stale(r.headers)):
                    cached = data
                    continue
            _CF_BASE_CACHE["base"] = base
            return data
        except Exception as e:  # 换下一个源
            last_err = e
            continue
    if cached is not None:
        return cached
    raise last_err if last_err else RuntimeError("curseforge api unreachable")


def _cf_post(path: str, payload: dict, key: str | None = None, timeout: int = 20):
    """CF API 的 POST（指纹反查用）：镜像优先、官方兜底，逻辑与 _cf_get 一致。"""
    body = json.dumps(payload).encode("utf-8")
    last_err: Exception | None = None
    for base in _cf_bases():
        headers = {"User-Agent": UA, "Accept": "application/json",
                   "Content-Type": "application/json", "Accept-Encoding": "gzip"}
        if base.startswith("https://api.curseforge.com"):
            headers["x-api-key"] = cf_api_key(key)
        req = urllib.request.Request(base + path, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read()
                if r.headers.get("Content-Encoding") == "gzip":
                    import gzip as _gzip
                    raw = _gzip.decompress(raw)
                data = json.loads(raw.decode("utf-8", "replace"))
            _CF_BASE_CACHE["base"] = base
            return data
        except Exception as e:  # 换下一个源
            last_err = e
            continue
    raise last_err if last_err else RuntimeError("curseforge api unreachable")


def cf_fingerprints(fingerprints: list[int], key: str | None = None) -> dict[int, dict]:
    """Murmur2 指纹反查 CurseForge：{指纹: {modId, fileId, fileName}}。

    POST /fingerprints（gameId=432），每批 100 个；镜像挂掉会自动落回官方源。
    """
    fps = [int(f) for f in (fingerprints or []) if f]
    out: dict[int, dict] = {}
    for i in range(0, len(fps), 100):
        chunk = fps[i:i + 100]
        data = _cf_post("/fingerprints", {"gameId": CF_GAME_ID, "fingerprints": chunk}, key) or {}
        for m in ((data.get("data") or {}).get("exactMatches") or []):
            f = m.get("file") or {}
            fp = f.get("fileFingerprint")
            if fp is None:
                continue
            out[int(fp)] = {
                "modId": int(f.get("modId") or m.get("id") or 0),
                "fileId": int(f.get("id") or 0),
                "fileName": f.get("fileName") or "",
            }
    return out


def cf_mod(mod_id: int, key: str | None = None) -> dict:
    """CurseForge 项目详情（/mods/{id}）。"""
    data = _cf_get(f"/mods/{int(mod_id)}", None, key) or {}
    return data.get("data") or {}


def cf_mod_description(mod_id: int, key: str | None = None) -> str:
    """CurseForge 项目长简介（/mods/{id}/description，HTML）。"""
    try:
        data = _cf_get(f"/mods/{int(mod_id)}/description", None, key) or {}
        return str(data.get("data") or "")
    except Exception:
        return ""


def _find_local_proxy() -> str | None:
    """探测本机常见的 HTTP 代理端口（v2rayN 10808/10809、Clash 7890 …），返回可用代理 URL。"""
    if "proxy" in _PROXY_CACHE:
        return _PROXY_CACHE["proxy"]  # type: ignore[return-value]
    import os
    # 启动器设置里选了「不使用代理」时会给 sidecar 传 NO_PROXY=*：这时不要再自作主张探测本机代理端口。
    if (os.environ.get("NO_PROXY") or os.environ.get("no_proxy") or "").strip() == "*":
        _PROXY_CACHE["proxy"] = None
        return None
    env = (os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy") or "").strip()
    cands = [env] if env else []
    cands += [f"http://127.0.0.1:{p}" for p in CF_PROXY_PORTS]
    for proxy in cands:
        if not proxy:
            continue
        try:
            op = urllib.request.build_opener(
                urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
            req = urllib.request.Request("https://www.google.com/generate_204",
                                         headers={"User-Agent": UA})
            with op.open(req, timeout=6) as r:
                if r.status in (200, 204):
                    _PROXY_CACHE["proxy"] = proxy
                    return proxy
        except Exception:
            continue
    _PROXY_CACHE["proxy"] = None
    return None


def _download_via(url: str, dest: Path, on_progress=None, proxy: str | None = None):
    return _stream_to_raw(url, dest, on_progress, proxy=proxy)


# ------------------------------------------------------------------ 搜索互备
def _cf_hit_to_mr(h: dict) -> dict:
    """把 CurseForge 命中翻译成 Modrinth 的字段形状（跨源回退时用）。"""
    return {"project_id": str(h.get("id") or ""), "title": h.get("name") or "",
            "description": h.get("summary") or "", "author": h.get("author") or "",
            "downloads": int(h.get("downloads") or 0), "categories": [],
            "icon_url": h.get("icon") or "", "source": "curseforge"}


def _mr_hit_to_cf(h: dict) -> dict:
    """把 Modrinth 命中翻译成 CurseForge 的字段形状（跨源回退时用）。"""
    pid = str(h.get("project_id") or "")
    return {"id": pid, "name": h.get("title") or "", "summary": h.get("description") or "",
            "author": h.get("author") or "", "downloads": int(h.get("downloads") or 0),
            "icon": h.get("icon_url") or "",
            "url": ("https://modrinth.com/project/" + pid) if pid else "",
            "source": "modrinth"}


def cf_search_page(query: str, loader: str | None = None, mc_version: str | None = None,
                   limit: int = 20, offset: int = 0, key: str | None = None,
                   _fallback: bool = True, category_id: int | None = None) -> dict:
    params = {
        "gameId": str(CF_GAME_ID),
        "classId": str(CF_CLASS_MOD),
        "index": str(max(0, offset)),
        "pageSize": str(max(1, min(50, limit))),
        "sortField": "2",
        "sortOrder": "desc",
    }
    if category_id:
        params["categoryId"] = str(int(category_id))
    if query:
        params["searchFilter"] = query
    if mc_version:
        params["gameVersion"] = mc_version
    lid = cf_loader_id(loader)
    if lid:
        params["modLoaderType"] = str(lid)
    try:
        data = _cf_get("/mods/search", params, key) or {}
    except Exception as e:
        if not _fallback:
            raise
        try:
            mr = modrinth_search_page(query, loader=loader, mc_version=mc_version,
                                      limit=limit, offset=offset, _fallback=False)
        except Exception:
            raise e
        hits = [_mr_hit_to_cf(h) for h in (mr.get("hits") or [])]
        return {"hits": hits, "total_hits": int(mr.get("total_hits") or len(hits)),
                "offset": offset, "limit": limit,
                "source": "modrinth", "fallback": "modrinth"}
    hits = []
    for m in data.get("data") or []:
        hits.append({
            "id": m.get("id"),
            "slug": m.get("slug") or "",
            "name": m.get("name") or "",
            "summary": m.get("summary") or "",
            "author": ((m.get("authors") or [{}])[0] or {}).get("name") or "",
            "downloads": int(m.get("downloadCount") or 0),
            "icon": ((m.get("logo") or {}) or {}).get("thumbnailUrl") or "",
            "url": ((m.get("links") or {}) or {}).get("websiteUrl") or "",
            "updated": m.get("dateModified") or "",
            "source": "curseforge",
        })
    total = (data.get("pagination") or {}).get("totalCount") or len(hits)
    return {"hits": hits, "total_hits": int(total or 0), "offset": offset,
            "limit": limit, "source": "curseforge"}


def cf_mod_files(mod_id: int, mc_version: str | None = None, loader: str | None = None,
                 limit: int = 50, key: str | None = None) -> list[dict]:
    params = {"pageSize": str(max(1, min(50, limit)))}
    if mc_version:
        params["gameVersion"] = mc_version
    lid = cf_loader_id(loader)
    if lid:
        params["modLoaderType"] = str(lid)
    data = _cf_get(f"/mods/{int(mod_id)}/files", params, key) or {}
    return data.get("data") or []


def cf_pick_file(files: list[dict], mc_version: str | None = None, loader: str | None = None) -> dict | None:
    """从文件列表里挑一个：先按 MC 版本/加载器过滤，再优先正式版，最后取最新。"""
    lid = cf_loader_id(loader)
    cands: list[dict] = []
    for f in files or []:
        if mc_version and mc_version not in (f.get("gameVersions") or []):
            continue
        if lid:
            names = [str(x).lower() for x in (f.get("gameVersions") or [])]
            want = {1: ("forge",), 4: ("fabric",), 5: ("quilt",), 6: ("neoforge",)}.get(lid, ())
            if want and not any(w in names for w in want):
                continue
        cands.append(f)
    if not cands:
        return None
    cands.sort(key=lambda f: int(f.get("id") or 0), reverse=True)  # 新的在前
    for f in cands:
        if f.get("releaseType") == 1:
            return f
    return cands[0]


def cf_file_download_url(f: dict) -> str:
    url = (f.get("downloadUrl") or "").strip()
    if url:
        return url
    fid = int(f.get("id") or 0)
    name = urllib.parse.quote(f.get("fileName") or "mod.jar")
    return f"https://edge.forgecdn.net/files/{fid // 1000}/{fid % 1000}/{name}"


def cf_file_urls(f: dict) -> list[str]:
    """按首选源下载，只有受 MCIM 支持的官方地址才参与改写。"""
    fid = int(f.get("id") or 0)
    name = urllib.parse.quote(f.get("fileName") or "mod.jar")
    u = cf_file_download_url(f)
    urls: list[str] = []
    if u:
        urls.extend(mcim.candidates(u, mcim.file_mirror(u)))
    if fid:
        for host in ("edge.forgecdn.net", "mediafilez.forgecdn.net"):
            cand = f"https://{host}/files/{fid // 1000}/{fid % 1000}/{name}"
            if cand not in urls:
                urls.append(cand)
    return urls


def install_curseforge_file(f: dict, dest_dir: Path,
                            on_progress: Callable[[int, int], None] | None = None,
                            on_log: Callable[[str], None] | None = None) -> Path:
    name = f.get("fileName") or f"curseforge-{f.get('id')}.jar"
    dest = dest_dir / name
    urls = cf_file_urls(f)
    errors: list[str] = []
    for url in urls:
        try:
            return _download_via(url, dest, on_progress)
        except Exception as e:
            errors.append(f"{url.split('/')[2]}: {e}")
    proxy = _find_local_proxy()
    if proxy:
        if on_log:
            on_log(f"[CurseForge] 直连失败，改走本机代理 {proxy}")
        for url in urls:
            try:
                return _download_via(url, dest, on_progress, proxy=proxy)
            except Exception as e:
                errors.append(f"proxy {url.split('/')[2]}: {e}")
    raise RuntimeError("CurseForge 下载失败（" + "; ".join(errors[:3]) + "）")


# ------------------------------------------------------------------ Modrinth
def modrinth_search_page(query: str, loader: str | None = None, kinds=("mod",), limit: int = 20,
                        mc_version: str | None = None, sort: str = "relevance",
                        offset: int = 0, _fallback: bool = True,
                        category: str | None = None) -> dict:
    """Modrinth 搜索，返回完整响应（hits / total_hits / offset / limit）。

    category 是 Modrinth 的分类 slug（adventure / technology / …），和加载器共用
    categories 这个 facet 字段——两个同时给就是 AND 条件。
    """
    parts = []
    if kinds:
        parts.append("[" + ",".join(f'"project_type:{k}"' for k in kinds) + "]")
    if loader and loader.lower() not in ("vanilla", "?", ""):
        parts.append(f'["categories:{loader.lower()}"]')
    if category:
        parts.append(f'["categories:{str(category).strip().lower()}"]')
    if mc_version:
        parts.append(f'["versions:{mc_version}"]')
    params = {"query": query, "limit": str(limit), "index": sort}
    if offset:
        params["offset"] = str(offset)
    if parts:
        params["facets"] = "[" + ",".join(parts) + "]"
    url = f"{MODRINTH}/search?" + urllib.parse.urlencode(params)
    try:
        data = _get(url) or {}
    except Exception as e:
        if not _fallback:
            raise
        try:
            cf = cf_search_page(query, loader=loader, mc_version=mc_version,
                                limit=limit, offset=offset, _fallback=False)
        except Exception:
            raise e
        hits = [_cf_hit_to_mr(h) for h in (cf.get("hits") or [])]
        return {"hits": hits, "total_hits": int(cf.get("total_hits") or len(hits)),
                "offset": offset, "limit": limit,
                "source": "curseforge", "fallback": "curseforge"}
    if isinstance(data, dict) and "hits" in data:
        data.setdefault("source", "modrinth")
    return data


def modrinth_search(query: str, loader: str | None = None, kinds=("mod",), limit: int = 20,
                    mc_version: str | None = None, sort: str = "relevance",
                    offset: int = 0):
    return modrinth_search_page(query, loader, kinds, limit, mc_version, sort, offset).get("hits", [])


def modrinth_project(project_id: str):
    """Modrinth 项目详情（/v2/project/{id}）。"""
    return _get(f"{MODRINTH}/project/{project_id}") or {}


def modrinth_versions(project_id: str, loaders=None, game_versions=None):
    params = []
    if loaders:
        params.append("loaders=" + urllib.parse.quote(json.dumps(loaders)))
    if game_versions:
        params.append("game_versions=" + urllib.parse.quote(json.dumps(game_versions)))
    url = f"{MODRINTH}/project/{project_id}/version"
    if params:
        url += "?" + "&".join(params)
    return _get(url)


def modrinth_version(version_id: str) -> dict:
    return _get(f"{MODRINTH}/version/{urllib.parse.quote(version_id, safe='')}") or {}


def install_modrinth_version(version: dict, dest_dir: Path,
                             on_progress: Callable[[int, int], None] | None = None) -> list[Path]:
    saved = []
    files = version.get("files") or []
    files.sort(key=lambda f: (not f.get("primary"), f.get("filename", "")))
    for f in files[:1]:
        url = f.get("url")
        name = f.get("filename") or "mod.jar"
        if not url:
            continue
        saved.append(_download(url, dest_dir / name, on_progress))
    return saved


def resolve_dependencies(version: dict, history: set[str] | None = None,
                         depth: int = 0) -> list[dict]:
    """Return required dependency versions (recursively, best-effort)."""
    out: list[dict] = []
    if depth > 3:
        return out
    history = history or set()
    for dep in version.get("dependencies") or []:
        if dep.get("dependency_type") != "required":
            continue
        pid = dep.get("project_id")
        if not pid or pid in history:
            continue
        history.add(pid)
        try:
            vers = modrinth_versions(pid, game_versions=[version.get("game_versions", [""])[0]]
                                     if version.get("game_versions") else None)
        except Exception:
            vers = []
        if not vers:
            continue
        v = vers[0]
        out.append(v)
        out.extend(resolve_dependencies(v, history, depth + 1))
    return out


# -------------------------------------------------------------------- Mojang
_MANIFEST_CACHE_TTL = 6 * 3600


def manifest_cache_path() -> Path:
    import ml2.core as _core  # 延迟导入，避免循环
    return _core.user_dir() / "cache" / "mojang_versions.json"


def mojang_versions():
    """Mojang 版本清单：本地缓存 6 小时，避免每次开下载页都等网络（实测偶发 60s）。"""
    import time as _t
    cache = manifest_cache_path()
    try:
        if cache.is_file() and (_t.time() - cache.stat().st_mtime) < _MANIFEST_CACHE_TTL:
            data = json.loads(cache.read_text(encoding="utf-8"))
            if isinstance(data, list) and data:
                return data
    except Exception:
        pass
    data = _get(MOJANG_MANIFEST).get("versions", [])
    try:
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(json.dumps(data), encoding="utf-8")
    except Exception:
        pass
    return data


def mojang_version_json(url: str) -> dict:
    return _get(url)


class ByteMeter:
    """线程安全的字节计数 + 最近几秒的速度（给安装进度显示「xx MB/s」用）。"""

    def __init__(self):
        self._lock = threading.Lock()
        self.total = 0
        self._samples: list[tuple[float, int]] = []

    def add(self, n: int) -> None:
        if n <= 0:
            return
        now = time.monotonic()
        with self._lock:
            self.total += n
            self._samples.append((now, self.total))
            cut = now - 4.0
            while len(self._samples) > 2 and self._samples[0][0] < cut:
                self._samples.pop(0)

    def speed(self) -> float:
        with self._lock:
            if len(self._samples) < 2:
                return 0.0
            (t0, b0), (t1, b1) = self._samples[0], self._samples[-1]
            return (b1 - b0) / (t1 - t0) if t1 > t0 else 0.0

    def file_cb(self):
        """给单个文件的 on_progress(done, total) 用：把累计值换算成增量。"""
        last = [0]

        def cb(done: int, total: int) -> None:
            self.add(done - last[0])
            last[0] = done
        return cb


def _proc_count() -> int:
    """下载用几个进程：默认跟核数走，环境变量 MEOW_DL_PROCS 可以强制。"""
    try:
        forced = int(os.environ.get("MEOW_DL_PROCS") or 0)
    except Exception:
        forced = 0
    if forced > 0:
        return max(1, min(8, forced))
    # 默认 8 路并发下载，设置页可以改（写入环境变量 MEOW_DL_PROCS 一并带下来）。
    # 用户口径：安装新版本时所有内容都要并发，8 个任务同时最舒服。
    return 8


MANY_FILES = 100


def download_jobs_auto(jobs: list, on_bytes=None, on_file=None, on_fail=None,
                       many: int = MANY_FILES) -> int:
    """按数量选策略：少量文件（普通 mod）只开线程池，成百上千个（整合包）才拆多进程。

    用户口径：「下载 mod 时只需要多线程，如果一次性下载很多 mod（成百上千个）再走 8 任务同时」。
    这里统一成 jobs 的 dict 形式：{"url","dest","sha1","size"}。
    """
    items = [j for j in jobs if (j.get("url") if isinstance(j, dict) else j)]
    if not items:
        return 0
    if len(items) >= many:
        return download_jobs_multi(items, on_bytes, on_file, procs=_proc_count(),
                                   threads_per=4, on_fail=on_fail)

    from concurrent.futures import ThreadPoolExecutor
    failed = [0]

    def _one(job: dict) -> None:
        dest = Path(str(job["dest"]))
        try:
            _download(str(job["url"]), dest,
                      (lambda d, t: on_bytes(d)) if on_bytes else None,
                      sha1=str(job.get("sha1") or ""), size=int(job.get("size") or 0))
            if on_file:
                on_file(str(dest))
        except Exception as exc:  # noqa: BLE001
            failed[0] += 1
            if on_fail:
                on_fail(str(dest), str(exc))

    workers = min(len(items), max(2, _dl_threads()))
    with ThreadPoolExecutor(max_workers=workers) as pool:
        list(pool.map(_one, items))
    return failed[0]


def download_jobs_multi(jobs: list, on_bytes, on_file, procs: int | None = None,
                        threads_per: int = 2, on_fail=None) -> int:
    """把 (url, dest) 清单拆给几个下载进程并行跑，返回失败个数。

    on_bytes(delta)  —— 又下好了多少字节（父进程用它累加 ByteMeter）
    on_file(dest)    —— 某个文件下完了
    on_fail(dest, err)—— 某个文件失败了（可选）
    多进程而不是多线程：CPython 的 GIL 会让大一批线程互相等，拆进程后每个进程
    都有独立的下载流水线，磁盘和 CPU 都能吃满。
    """
    if not jobs:
        return 0
    procs = max(1, min(int(procs or _proc_count()), len(jobs)))
    tmpdir = Path(tempfile.mkdtemp(prefix="meowdl-"))
    worker = Path(__file__).with_name("dlworker.py")
    procs_list = []
    failed = [0]
    lock = threading.Lock()
    try:
        for i in range(procs):
            chunk = jobs[i::procs]
            job_file = tmpdir / f"jobs{i}.json"
            norm = []
            for it in chunk:
                if isinstance(it, dict):
                    norm.append({
                        "url": str(it.get("url") or ""),
                        "dest": str(it.get("dest") or ""),
                        "sha1": str(it.get("sha1") or ""),
                        "size": int(it.get("size") or 0),
                    })
                else:
                    u, d = it
                    norm.append({"url": str(u), "dest": str(d), "sha1": "", "size": 0})
            job_file.write_text(json.dumps(norm, ensure_ascii=False), encoding="utf-8")
            procs_list.append(subprocess.Popen(
                [sys.executable, "-u", str(worker), str(job_file), str(threads_per)],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding="utf-8", errors="replace", bufsize=1))

        def pump(proc) -> None:
            assert proc.stdout is not None
            for line in proc.stdout:
                line = line.strip()
                if not line:
                    continue
                if line[0] == "+" and line[1:].isdigit():
                    if on_bytes is not None:
                        on_bytes(int(line[1:]))
                elif line[0] == "=":
                    if on_file is not None:
                        on_file(line[1:])
                elif line[0] == "!":
                    with lock:
                        failed[0] += 1
                    if on_fail is not None:
                        body = line[1:]
                        dest_part, _, err_part = body.partition("|")
                        try:
                            on_fail(dest_part, err_part)
                        except Exception:
                            pass

        pumps = [threading.Thread(target=pump, args=(p,), daemon=True) for p in procs_list]
        for t in pumps:
            t.start()
        for t in pumps:
            t.join()
        for p in procs_list:
            p.wait()
    finally:
        for p in procs_list:
            try:
                p.stdout and p.stdout.close()
            except Exception:
                pass
        shutil.rmtree(tmpdir, ignore_errors=True)
    return failed[0]


def install_vanilla(mc_dir: Path, version_json: dict, on_log: Callable[[str], None] | None = None,
                    on_progress: Callable[[str, int, int], None] | None = None):
    """Download libraries + client jar + assets index for a vanilla version.

    on_progress(phase, done, total, info)：phase 取 libraries/client/index/assets，
    info = {"name": 当前文件, "bytes": 已下载字节, "speed": 字节/秒, "fbytes": 当前文件已下, "fsize": 当前文件大小}；
    给安装进度条用；每处理完一项（客户端 jar 是边下边报）就报一次，由调用方自行节流。
    """
    log = on_log or (lambda s: None)
    _rep = on_progress or (lambda *_: None)
    meter = ByteMeter()

    def report(phase, done, total, name="", fbytes=0, fsize=0):
        info = {"name": name, "bytes": meter.total, "speed": meter.speed(), "fbytes": fbytes, "fsize": fsize}
        try:
            _rep(phase, done, total, info)
        except TypeError:
            _rep(phase, done, total)
    vid = version_json["id"]
    vdir = mc_dir / "versions" / vid
    vdir.mkdir(parents=True, exist_ok=True)
    (vdir / f"{vid}.json").write_text(json.dumps(version_json, indent=2), encoding="utf-8")
    log(f"[安装] {vid} 版本 JSON 已写入")

    # 先把资源索引拿到手，才能算出这次一共要下多少字节。
    # 否则「先下完库和 jar（几 MB）→ 再把几百 MB 资源加进分母」会让总进度从 90% 掉回 10%，
    # 用户看到的就是「一个下载里百分比反复从 0% 重来」。
    ai = version_json.get("assetIndex")
    asset_bytes = 0
    if ai:
        idx_path = mc_dir / "assets" / "indexes" / f"{ai['id']}.json"
        if not idx_path.exists():
            # 索引本身也有几百 KB~几 MB，原来这一段完全静默，任务卡会卡在 5% 不动。
            log(f"[安装] 下载资源索引 {ai['id']}")
            _download(ai["url"], idx_path,
                      lambda d, t: report("index", d, t, f"indexes/{ai['id']}.json"))
            log(f"[安装] 资源索引 {ai['id']} 完成")
        _missing_assets = assets_missing(mc_dir, ai["id"])
        log(f"[安装] 需要补 {len(_missing_assets)} 个资源文件，开始统计大小")
        asset_bytes = sum(int(o.get("size") or 0) for o in _missing_assets)

    libs = version_json.get("libraries", [])
    total = len(libs)
    jobs = []
    for lib in libs:
        art = (lib.get("downloads") or {}).get("artifact")
        if not art:
            continue
        dest = mc_dir / "libraries" / art["path"]
        if dest.exists() and dest.stat().st_size == art.get("size", -1):
            continue
        jobs.append((art, dest))

    lib_bytes = sum(int(art.get("size") or 0) for art, _ in jobs)
    client = (version_json.get("downloads") or {}).get("client")
    client_bytes = int((client or {}).get("size") or 0)
    index_bytes = int((ai or {}).get("size") or 0) if ai else 0
    total_bytes = max(lib_bytes + client_bytes + index_bytes + asset_bytes, 1)
    base_bytes = lib_bytes + client_bytes + index_bytes

    # 三条线并发，各自只记自己已经下到多少；总进度 = 三块分别封顶后相加。
    # 直接用 meter.total 会把「重试/重复计入」的字节也算进去，出现「总进度 98% 但 jar 才 47%」。
    lib_got = [0]
    client_got = [0]
    asset_got = [0]
    asset_meter = ByteMeter()

    def overall_bytes() -> int:
        return (min(lib_got[0], lib_bytes)
                + min(client_got[0], client_bytes)
                + min(asset_got[0], asset_bytes))

    def breport(step_index: int, step_done: int, step_total: int, name: str = ""):
        """按字节报总进度：分母从一开始就固定，进度只增不减。"""
        # step_total 为 0 表示这一步这次没有文件要下（早就有了）——那就是 100%，不是 0%。
        info = {"name": name, "speed": meter.speed(), "step": step_index,
                "steppct": (100.0 * step_done / step_total) if step_total > 0 else 100.0}
        try:
            _rep("bytes", int(overall_bytes()), int(total_bytes), info)
        except TypeError:
            pass

    finished = [total - len(jobs)]
    lock = threading.Lock()

    def fetch_lib(job):
        art, dest = job
        try:
            _download(art["url"], dest, meter.file_cb())
        except Exception as e:
            log(f"[警告] 库下载失败 {art['path']}: {e}")
        with lock:
            finished[0] += 1
            n = finished[0]
        if n % 10 == 0:
            log(f"[安装] 库 {n}/{total}")
        report("libraries", n, total, art["path"].rsplit("/", 1)[-1])
        breport(0, meter.total, lib_bytes, art["path"].rsplit("/", 1)[-1])

    # 依赖库、客户端 jar、资源文件三块互不依赖：并发跑，总下载时间取最长的那块，
    # 而不是三块相加。进度仍按字节汇总到同一个 meter，界面看到的是同一条总进度。
    def _do_libs() -> None:
        if not jobs:
            report("libraries", total, total)
            breport(0, lib_bytes, lib_bytes, "")
            return

        def _lib_bytes(delta: int) -> None:
            lib_got[0] += delta
            meter.add(delta)

        def _lib_done(dest: str) -> None:
            with lock:
                finished[0] += 1
                n = finished[0]
            name = Path(dest).name
            if n % 10 == 0:
                log(f"[安装] 库 {n}/{total}")
            report("libraries", n, total, name)
            breport(0, min(lib_got[0], lib_bytes), lib_bytes, name)

        download_jobs_multi([(art["url"], dest) for art, dest in jobs], _lib_bytes, _lib_done)
        breport(0, lib_bytes, lib_bytes, "")

    def _do_client() -> None:
        if not client:
            return
        dest = vdir / f"{vid}.jar"
        if not dest.exists():
            csize = int(client.get("size") or 0)
            fcb = meter.file_cb()
            tick = [0.0]

            def client_cb(done, tot):
                client_got[0] = min(int(done), int(tot or csize or done))
                fcb(done, tot)
                now = time.monotonic()
                if now - tick[0] >= 0.25:
                    tick[0] = now
                    report("client", 0, 1, f"{vid}.jar", done, tot or csize)
                    breport(1, done, tot or csize, f"{vid}.jar")

            try:
                # 一定要把 size 带进来：不带 size 时 _download 不知道文件多大，会退回
                # 单连接顺序下（实测 26.1.2 的 38 MB jar 只有 1.1 MB/s，带 size 走 16 段是 20 MB/s）。
                _download(client["url"], dest, client_cb, size=int(client.get("size") or 0))
                log("[安装] 客户端 jar 完成")
            except Exception as e:
                log(f"[警告] 客户端 jar 下载失败: {e}")
        report("client", 1, 1, f"{vid}.jar")
        breport(1, client_bytes, client_bytes, "")

    def _do_assets() -> None:
        report("index", 1, 1)
        if not ai:
            return

        last_asset = [0]

        def _asset_bytes(delta: int) -> None:
            meter.add(delta)

        def on_asset(done: int, count: int, name: str) -> None:
            delta = asset_meter.total - last_asset[0]
            if delta > 0:
                last_asset[0] = asset_meter.total
                asset_got[0] += delta
                _asset_bytes(delta)
            report("assets", done, count, name)
            breport(2, asset_got[0], max(asset_bytes, 1), name)

        try:
            ensure_assets(mc_dir, ai["id"], on_log=on_log, meter=asset_meter, on_item=on_asset)
        except Exception as e:
            log(f"[警告] 资源下载失败: {e}")
        breport(2, max(asset_bytes, 1), max(asset_bytes, 1), "")

    _threads = [threading.Thread(target=f, daemon=True) for f in (_do_libs, _do_client, _do_assets)]
    for _t in _threads:
        _t.start()
    for _t in _threads:
        _t.join()
    log("[安装] 完成")


ASSET_BASE = "https://resources.download.minecraft.net"


def assets_missing(mc_dir: Path, index_id: str) -> list[dict]:
    """返回缺失的资源对象（assets/indexes/<id>.json 里的 objects）。"""
    idx = Path(mc_dir) / "assets" / "indexes" / f"{index_id}.json"
    if not idx.is_file():
        return []
    try:
        data = json.loads(idx.read_text(encoding="utf-8"))
    except Exception:
        return []
    objs = data.get("objects") or {}
    root = Path(mc_dir) / "assets" / "objects"
    missing = []
    for name, info in objs.items():
        h = info.get("hash", "")
        if len(h) < 2:
            continue
        if not (root / h[:2] / h).is_file():
            missing.append({"name": name, "hash": h, "size": int(info.get("size") or 0)})
    return missing


def ensure_assets(mc_dir: Path, index_id: str, on_log: Callable[[str], None] | None = None,
                  workers: int | None = None,
                  on_item: Callable[[int, int, str], None] | None = None,
                  meter: "ByteMeter | None" = None) -> tuple[int, int]:
    """下载缺失的资源对象（Minecraft 自己不会补，必须启动器下载）。返回 (下载数, 失败数)。

    on_item(done, total, name) 每完成一个资源回调一次（启动进度条用）。
    """
    log = on_log or (lambda s: None)
    mc_dir = Path(mc_dir)
    missing = assets_missing(mc_dir, index_id)
    if not missing:
        return (0, 0)
    total = len(missing)
    workers = max(1, int(workers or _dl_threads()))
    log(f"[资源] 缺少 {total} 个资源文件，开始补全（{workers} 个线程）…")
    root = mc_dir / "assets" / "objects"
    done = 0
    failed = 0
    lock = threading.Lock()

    def fetch(item: dict) -> bool:
        h = item["hash"]
        dest = root / h[:2] / h
        if dest.is_file():
            return True
        try:
            _download(f"{ASSET_BASE}/{h[:2]}/{h}", dest, meter.file_cb() if meter else None)
            return True
        except Exception:
            try:
                dest.unlink(missing_ok=True)
            except Exception:
                pass
            return False

    if total >= 120:
        # 大批小文件：拆成几个下载进程，别让 GIL 把它们排成一队。
        def _asset_bytes(delta: int) -> None:
            if meter:
                meter.add(delta)

        def _asset_done(dest: str) -> None:
            nonlocal done
            with lock:
                done += 1
                cur = done
            if on_item:
                try:
                    on_item(cur, total, Path(dest).name)
                except Exception:
                    pass

        jobs = [(f"{ASSET_BASE}/{it['hash'][:2]}/{it['hash']}", root / it["hash"][:2] / it["hash"])
                for it in missing if not (root / it["hash"][:2] / it["hash"]).is_file()]
        # 资源是几千个几十 KB 的小文件，瓶颈是「每个文件一次连接」的延迟，不是带宽：
        # 每个进程只开 2 条连接时实测 3 文件/秒，而 PCL 能跑到几十个/秒。这里按
        # 「进程数 × 每进程 16 线程」放开，小文件排队，连接数上去速度才起得来。
        failed += download_jobs_multi(jobs, _asset_bytes, _asset_done,
                                      procs=_proc_count(), threads_per=16)
        return (done, failed)

    with ThreadPoolExecutor(max_workers=workers) as ex:
        futs = {ex.submit(fetch, it): it for it in missing}
        for fu in as_completed(futs):
            item = futs.get(fu) or {}
            ok = False
            try:
                ok = fu.result()
            except Exception:
                ok = False
            with lock:
                if ok:
                    done += 1
                else:
                    failed += 1
                finished = done + failed
            if on_item:
                try:
                    on_item(finished, total, str(item.get("name") or item.get("hash", "")[:8]))
                except Exception:
                    pass
            if finished % 200 == 0:
                log(f"[资源] {finished}/{total}…")
    log(f"[资源] 完成：成功 {done}，失败 {failed}")
    return (done, failed)


def version_is_isolated(mc_dir: Path, vid: str) -> bool:
    """版本隔离：显式设置优先，否则 PCL2 风格的自动探测。统一走 ml2.isolation。"""
    from ml2 import isolation
    return isolation.effective_isolated(mc_dir, vid)


def ensure_version_gamedir(mc_dir: Path, vid: str,
                           on_log: Callable[[str], None] | None = None) -> bool:
    """给加载器版本准备独立的游戏目录（PCL2 版本隔离）。

    MC 只从游戏目录的 mods 读 Mod，所以隔离版本必须让 versions/<vid> 能当游戏目录用，
    否则装在版本目录里的 API Mod 永远不会被加载。
    """
    log = on_log or (lambda s: None)
    mc_dir = Path(mc_dir)
    vdir = mc_dir / "versions" / vid
    if not vdir.is_dir():
        return False
    if version_is_isolated(mc_dir, vid):
        return True
    for name in ("logs", "saves", "resourcepacks", "config"):
        (vdir / name).mkdir(parents=True, exist_ok=True)
    root_opt = mc_dir / "options.txt"
    if root_opt.exists() and not (vdir / "options.txt").exists():
        try:
            (vdir / "options.txt").write_bytes(root_opt.read_bytes())
        except Exception:
            (vdir / "options.txt").write_text("", encoding="utf-8")
    else:
        (vdir / "options.txt").touch(exist_ok=True)
    log(f"[隔离] {vid} 使用独立游戏目录（versions/{vid}）")
    return True


def mods_dir_for(mc_dir: Path, vid: str) -> Path:
    """该版本实际会被加载的 mods 目录。"""
    from ml2 import isolation
    return isolation.mods_dir_for(mc_dir, vid)


def maven_rel_path(name: str) -> str | None:
    """group:artifact:version[:classifier] → maven 相对路径。"""
    parts = (name or "").split(":")
    if len(parts) < 3:
        return None
    group, artifact, ver = parts[0], parts[1], parts[2]
    cls = parts[3] if len(parts) > 3 else ""
    fname = f"{artifact}-{ver}" + (f"-{cls}" if cls else "") + ".jar"
    return "/".join(group.split(".") + [artifact, ver, fname])


def install_profile_libraries(mc_dir: Path, profile: dict,
                              on_log: Callable[[str], None] | None = None) -> int:
    """下载 profile 里自带的库（Fabric/Quilt 的 maven 布局）。返回实际下载数。

    Fabric/Quilt 的 profile 只写 "name" + "url"，没有 Mojang 的 downloads.artifact，
    以前只落了 json 没下库，导致启动时 ClassNotFoundException: KnotClient。
    """
    log = on_log or (lambda s: None)
    libs = profile.get("libraries") or []
    jobs = []
    total = 0
    for lib in libs:
        rel = None
        url = None
        size = -1
        art = (lib.get("downloads") or {}).get("artifact")
        if art:
            rel, url = art.get("path"), art.get("url")
            size = art.get("size", -1)
        elif lib.get("url"):
            rel = maven_rel_path(lib.get("name", ""))
            if rel:
                url = lib["url"].rstrip("/") + "/" + rel
        if not rel or not url:
            continue
        total += 1
        dest = mc_dir / "libraries" / rel
        if dest.exists() and (size < 0 or dest.stat().st_size == size):
            continue
        jobs.append((rel, url, dest))

    got = [0]
    lock = threading.Lock()

    def fetch_lib(job):
        rel, url, dest = job
        try:
            _download(url, dest)
        except Exception as e:
            log(f"[警告] 库下载失败 {rel}: {e}")
            return
        with lock:
            got[0] += 1
            n = got[0]
        if n % 8 == 0:
            log(f"[安装] 加载器库 {total} 个，已下载 {n}")

    if jobs:
        with ThreadPoolExecutor(max_workers=min(_dl_threads(), len(jobs))) as ex:
            list(ex.map(fetch_lib, jobs))
    if total:
        log(f"[安装] 加载器库完成（{got[0]}/{total} 个需要下载）")
    return got[0]


# ------------------------------------------------------------------- Fabric
def fabric_loaders(game_version: str):
    return _get(f"{FABRIC_META}/versions/loader/{game_version}")


def latest_fabric_loader() -> str | None:
    try:
        data = _get(f"{FABRIC_META}/versions/loader")
        return data[0]["version"] if data else None
    except Exception:
        return None


def install_fabric(mc_dir: Path, game_version: str, loader_version: str | None = None,
                   on_log: Callable[[str], None] | None = None) -> str:
    """Create a <game>-Fabric_<loader> version by merging base + fabric profile."""
    log = on_log or (lambda s: None)
    loader_version = loader_version or latest_fabric_loader()
    if not loader_version:
        raise RuntimeError("无法获取 Fabric loader 版本")
    profile = _get(f"{FABRIC_META}/versions/loader/{game_version}/{loader_version}/profile/json")
    vid = f"{game_version}-Fabric_{loader_version}"
    vdir = mc_dir / "versions" / vid
    vdir.mkdir(parents=True, exist_ok=True)
    (vdir / f"{vid}.json").write_text(json.dumps(profile, indent=2), encoding="utf-8")
    log(f"[Fabric] 已创建版本 {vid}")
    install_profile_libraries(mc_dir, profile, on_log=log)
    ensure_version_gamedir(mc_dir, vid, on_log=log)
    return vid


def async_call(fn, on_done, on_error=None, *args, **kwargs):
    def run():
        try:
            r = fn(*args, **kwargs)
            on_done(r)
        except Exception as e:  # pragma: no cover
            if on_error:
                on_error(e)
    threading.Thread(target=run, daemon=True).start()
