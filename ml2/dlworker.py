"""下载工作进程：读一份文件清单，用几个线程下完，把字节数报给父进程。

父进程按下面的协议读 stdout（一行一条，中文路径不进协议，避免编码问题）：

    +<bytes>        这一批又下好了多少字节
    =<dest>         某个文件完成了（跟的是 dest 原样字符串）
    !<dest>|<err>   某个文件失败

为什么不直接在线程里下：CPython 的 GIL 让「16 个线程」实际还是一次跑一个字节流，
小文件一多就互相等；拆成几个进程之后每个进程各自持有一条完整的下载流水线，
CPU 和磁盘队列都能吃满，总耗时和「愣一下」的次数都会明显下降。
"""

import json
import os
import sys
import threading
from pathlib import Path

FLUSH_BYTES = 512 * 1024


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
    local = threading.local()

    def _session():
        sess = getattr(local, "sess", None)
        if sess is None:
            import requests  # noqa: WPS433
            sess = requests.Session()
            sess.headers.update({"User-Agent": net.UA, "Accept": "*/*"})
            local.sess = sess
        return sess

    def light(url: str, dest: Path, on_bytes) -> None:
        sess = _session()
        resp = sess.get(url, timeout=20, stream=True)
        resp.raise_for_status()
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_name(dest.name + ".part")
        with open(tmp, "wb") as fh:
            for chunk in resp.iter_content(net._CHUNK):
                if not chunk:
                    continue
                fh.write(chunk)
                on_bytes(len(chunk), 0)
        os.replace(tmp, dest)

    def emit(line: str) -> None:
        with out_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    def run(job: dict) -> None:
        url = str(job.get("url") or "")
        dest = Path(str(job.get("dest") or ""))
        acc = [0]

        def on_bytes(done: int, _total: int) -> None:
            delta = done - acc[0]
            acc[0] = done
            if delta > 0:
                emit(f"+{delta}")

        sha1 = str(job.get("sha1") or "")
        size = int(job.get("size") or 0)
        small = not sha1 and (size == 0 or size < 4 * 1024 * 1024)
        try:
            if small and getattr(net, "HAS_REQUESTS", False):
                light(url, dest, on_bytes)
            else:
                net._download(url, dest, on_bytes, sha1=sha1, size=size)
            emit(f"={dest}")
        except Exception as exc:  # noqa: BLE001
            try:
                dest.unlink(missing_ok=True)
            except Exception:
                pass
            emit(f"!{dest}|{exc}")

    pending = list(jobs)
    lock = threading.Lock()

    def worker() -> None:
        while True:
            with lock:
                if not pending:
                    return
                job = pending.pop()
            run(job)

    threads = max(1, min(threads, len(jobs)))
    pool = [threading.Thread(target=worker, daemon=True) for _ in range(threads)]
    for t in pool:
        t.start()
    for t in pool:
        t.join()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
