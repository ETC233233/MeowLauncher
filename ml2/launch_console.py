"""Show a launcher's file-backed game output in a separate Windows console."""

import ctypes
from ctypes import wintypes
from pathlib import Path
import sys


def show_log(path: Path, offset: int, pid: int) -> None:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel.SetConsoleTitleW.argtypes = (wintypes.LPCWSTR,)
    kernel.SetConsoleTitleW(f"MeowLauncher — {path.stem}")

    process = kernel.OpenProcess(0x00100000, False, pid)
    try:
        with path.open("rb") as stream:
            stream.seek(offset)
            while True:
                for line in stream:
                    try:
                        text = line.decode("utf-8")
                    except UnicodeDecodeError:
                        text = line.decode("gbk", errors="replace")
                    print(text, end="", flush=True)
                if not process or kernel.WaitForSingleObject(process, 200) != 258:
                    break
    finally:
        if process:
            kernel.CloseHandle(process)


if __name__ == "__main__":
    show_log(Path(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]))
