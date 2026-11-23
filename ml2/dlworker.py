"""下载工作进程：读一份文件清单，用几个线程下完，把字节数报给父进程。

父进程按下面的协议读 stdout（一行一条，中文路径不进协议，避免编码问题）：

    +<bytes>        这一批又下好了多少字节
    =<dest>         某个文件完成了（跟的是 dest 原样字符串）
    !<dest>|<err>   某个文件失败

为什么不直接在线程里下：CPython 的 GIL 让「16 个线程」实际还是一次跑一个字节流，
小文件一多就互相等；拆成几个进程之后每个进程各自持有一条完整的下载流水线，
CPU 和磁盘队列都能吃满，总耗时和「愣一下」的次数都会明显下降。
"""

import hashlib
import json
import os
import sys
import threading
from collections import deque
from pathlib import Path

FLUSH_BYTES = 1024 * 1024
BATCH_FILES = 16


def main() -> int:
    if len(sys.argv) < 2:
        return 2
    job_file = Path(sys.argv[1])
    threads = int(sys.argv[2]) if len(sys.argv) > 2 else 2
    jobs = json.loads(job_file.read_text(encoding="utf-8"))
    if not jobs:
        return 0

    root = Path(__file__).resolve().parents[1]
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    from ml2 import net  # noqa: WPS433  延迟导入，避免父进程重复加载

    out_lock = threading.Lock()

    # 小文件专用「轻下载」：每个线程自己开一个 requests.Session，复用 TCP/TLS 连接。
    # 资源包是几千个几十 KB 的 objects，一条连接一个文件时握手比正文还贵——实测
    # 400 个文件：每次新建连接 19.3 文件/秒，复用连接 86.9 文件/秒。
    # 之前这里只看「没有 sha1」，而资源对象全都带 sha1，导致这条快路一直没被走到；
    # 现在按体积判断，sha1 在下载完成后本地校验，不符就换下一个候选地址重来。
    local = threading.local()

    def _session():
        sess = getattr(local, "sess", None)
        if sess is None:
            import requests  # noqa: WPS433
            sess = requests.Session()
            sess.headers.update({"User-Agent": net.UA, "Accept": "*/*"})
            local.sess = sess
        return sess

    def _mkdir(parent: Path) -> None:
        # 几千个对象只落在 256 个前缀目录里，每个文件都 mkdir 一次纯属白烧系统调用。
        seen = getattr(local, "dirs", None)
        if seen is None:
            seen = set()
            local.dirs = seen
        key = str(parent)
        if key not in seen:
            parent.mkdir(parents=True, exist_ok=True)
            seen.add(key)

    def light(url: str, dest: Path, on_bytes, sha1: str = "", size: int = 0) -> None:
        _mkdir(dest.parent)
        tmp = dest.with_name(dest.name + ".part")
        # 候选地址：镜像（自家 CDN / BMCLAPI）优先，失败退回官方。
        urls: list[str] = []
        try:
            alt = net._mirrored(url)
            if alt and alt != url:
                urls.append(alt)
        except Exception:  # noqa: BLE001
            pass
        urls.append(url)
        last: Exception | None = None
        for _attempt in range(2):
            for u in urls:
                try:
                    sess = _session()
                    # 连接 5 秒、读 15 秒：个别镜像会挂住不回数据，卡 20 秒就把整批进度拖死。
                    with sess.get(u, timeout=(5, 15), stream=True) as resp:
                        resp.raise_for_status()
                        digest = hashlib.sha1() if sha1 else None
                        got = 0
                        with open(tmp, "wb") as fh:
                            for chunk in resp.iter_content(net._CHUNK):
                                if not chunk:
                                    continue
                                fh.write(chunk)
                                if digest is not None:
                                    digest.update(chunk)
                                got += len(chunk)
                                on_bytes(got, size or got)
                        if size and got != size:
                            raise ValueError(f"长度不符：{got} != {size}")
                        if digest is not None and digest.hexdigest() != sha1:
                            raise ValueError("sha1 校验失败")
                    os.replace(tmp, dest)
                    return
                except Exception as exc:  # noqa: BLE001
                    last = exc
                    try:
                        tmp.unlink(missing_ok=True)
                    except Exception:  # noqa: BLE001
                        pass
        if last is not None:
            raise last

    def emit(line: str) -> None:
        with out_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    def run(job: dict) -> None:
        url = str(job.get("url") or "")
        dest = Path(str(job.get("dest") or ""))
        acc = [0]

        def on_bytes(done: int, _total: int) -> None:
            # 线程内先攒着再报：每个文件都跨进程报一次字节，几千个小文件就是几千次
            # 带全局锁的 write+flush，父进程还得逐行解析——这笔开销比下载本身还贵。
            delta = done - acc[0]
            acc[0] = done
            if delta <= 0:
                return
            buf = getattr(local, "buf", None)
            if buf is None:
                buf = [0]
                local.buf = buf
            buf[0] += delta
            if buf[0] >= FLUSH_BYTES:
                emit(f"+{buf[0]}")
                buf[0] = 0

        sha1 = str(job.get("sha1") or "")
        size = int(job.get("size") or 0)
        # 小于分段阈值的都走轻下载（连接复用）；大文件继续交给 net._download 做 Range 分段。
        small = size < getattr(net, "SEGMENT_MIN", 8 * 1024 * 1024)
        try:
            ok = False
            if small and getattr(net, "HAS_REQUESTS", False):
                try:
                    light(url, dest, on_bytes, sha1=sha1, size=size)
                    ok = True
                except Exception:  # noqa: BLE001
                    # 轻下载彻底失败时才回退到通用下载器，避免双重失败链。
                    try:
                        dest.unlink(missing_ok=True)
                    except Exception:  # noqa: BLE001
                        pass
            if not ok:
                net._download(url, dest, on_bytes, sha1=sha1, size=size)
            return ("=", str(dest))
        except Exception as exc:  # noqa: BLE001
            try:
                dest.unlink(missing_ok=True)
            except Exception:
                pass
            return ("!", f"{dest}|{exc}")

    pending = deque(jobs)
    lock = threading.Lock()

    def flush_batch(batch: list) -> None:
        # 文件完成消息也批量发：一次管道写带一次 flush，一条消息带 16 个文件。
        oks = [p for kind, p in batch if kind == "="]
        if oks:
            emit("=" + "\t".join(oks))
        for kind, p in batch:
            if kind == "!":
                emit("!" + p)

    def worker() -> None:
        batch: list = []
        while True:
            with lock:
                job = pending.popleft() if pending else None
            if job is None:
                break
            batch.append(run(job))
            if len(batch) >= BATCH_FILES:
                flush_batch(batch)
                batch = []
        if batch:
            flush_batch(batch)
        buf = getattr(local, "buf", None)
        if buf and buf[0]:
            emit(f"+{buf[0]}")
            buf[0] = 0

    threads = max(1, min(threads, len(jobs)))
    pool = [threading.Thread(target=worker, daemon=True) for _ in range(threads)]
    for t in pool:
        t.start()
    for t in pool:
        t.join()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
