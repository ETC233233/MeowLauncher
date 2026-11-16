#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MeowLauncher.WinUI 真机截图（Python 3.15，仅标准库：ctypes + zlib 手写 PNG）。

做的事：
  1. 拉起 bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/MeowLauncher.WinUI.exe
  2. 等主窗口出现，按 DPI 把「客户区」强制成设计尺寸（默认 1440x900 / 1120x760）
  3. 可选：按侧栏导航项名称（UIA）切页，再执行一组鼠标动作（给编辑态/拖动中截图用）
  4. 截客户区到 screenshots/run/<page>_<w>x<h>.png
  5. 等窗口画完再关进程；跑完把 %LOCALAPPDATA%\\MeowLauncher.WinUI\\app.json 还原

尺寸口径：
  - 图像像素 = 设计尺寸 × DPI 缩放（本机 150% → 1440x900 出 2160x1350）。
  - 文件名里的 <w>x<h> 是设计尺寸，方便和设计稿/契约对照。
  - 截图矩阵用 1120x760（规范内最小窗口）；980x760 低于规范，仅作超范围体检，
    需显式传 --sizes 980x760，报告里要注明。
  - 低于系统最小宽时用 SWP_NOSENDCHANGING 绕过，这是故意的，不是 bug。

用法（在 mclauncher/ 下，建议套 meowlock）：
  python tools/meowlock.py shot -- python tools/shot_page.py
  python tools/meowlock.py shot -- python tools/shot_page.py --page versions --size 1440x900
  python tools/shot_page.py --page home --actions "click:1200,820;wait:400;shot:home_edit"
  python tools/shot_page.py --page versions --actions "hover:1.13.2;wait:400;shot:versions_hover"

动作串（--actions，分号分隔）：
  click:x,y / move:x,y（真实鼠标移动，会触发 hover）/ hover:<UIA 名字>（移到控件中心）
  mousedown / mouseup / clickname:<按钮名> / expect:<文字> / wait:ms / scroll:px / shot:<名字>
  crop:<输出名>@<UIA 文字>（裁该行右侧 160x56 设计区，用于 hover 证据）

退出码：0 成功；1 截图/窗口失败；2 用法错误。
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import struct
import subprocess
import sys
import time
import uuid
import zlib
from ctypes import wintypes as w
from pathlib import Path

# app.json 并发保护：和 meowlock.py 共用同一份实现（只还原窗口尺寸键）。
try:
    from prefs_guard import restore as restore_prefs, snapshot as snapshot_prefs
except ImportError:  # 单独拷走 shot_page.py 时退化为「不动 app.json」
    restore_prefs = snapshot_prefs = None

TOOLS_DIR = Path(__file__).resolve().parent
ROOT = TOOLS_DIR.parent
DEFAULT_EXE = (
    ROOT
    / "MeowLauncher.WinUI/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/MeowLauncher.WinUI.exe"
)
OUT_DIR = ROOT / "screenshots" / "run"
SIZES = [(1440, 900), (1120, 760)]

# 侧栏旧导航坐标（设计像素）。正常路径由 nav_item_center() 用 UIA 按导航项
# 名称实时定位，侧栏分组、间距、窗口高度变化都不受影响；这张表只在显式传
# --nav-y（排障）时提供 x，或给 UIA 找不到的页面做保留记录。
# 侧栏宽 218、内边距 12/10；账户按钮 50 + 12；分组标签 26；导航项高 40。
PAGES = {
    "home": (109, 141),
    "versions": (109, 257),
    "get": (109, 199),
    "instance": (109, 257),
    "download": (109, 199),
    "mods": (109, 257),
    "online": (109, 199),
    "assets": (109, 257),
    "logs": (109, 353),
    "settings": (109, 353),
    "tasks": (109, 713),   # 左下「任务与通知」dock
    "account": (109, 321),  # 侧栏「账户」
    "room": (109, 361),     # 侧栏「联机」（开发者测试开启联机后才有）
}
# 证据链里一直叫 accounts（AccountPage），沿用这个叫法，避免 --page accounts
# 落到未知页时截错图却照样落盘。
PAGES["accounts"] = PAGES["account"]

# 页头面包屑最后一段（内容区左上）。截图后必须和它对上，否则不许落盘——
# 2026-11-02 曾出现 home_*.png 实为任务页的事故（并发 lane 复用工具时贴错名字）。
# 侧栏导航项的可见文本（UIA Name）≠ 页面内页头标题，必须分开维护：
# t34 起侧栏把「实例」改名成「实例内容」，页面内页头仍写「实例」；
# 另外「获取」「实例」是宿主页，下载/在线/Mod/资源已变成它们内部的 tab。
# 导航自检要覆盖的九页（--check-nav / --dump-nav 共用）。
CHECK_PAGES = ("home", "versions", "download", "mods", "online", "assets",
               "settings", "tasks", "account")

NAV_LABELS = {
    "home": "主页",
    "versions": "全部实例",
    "get": "获取",
    "instance": "实例",
    "settings": "设置",
    "account": "账户",
    "accounts": "账户",
    "room": "联机",
    "tasks": "任务与通知",
    # 以下四项已并入宿主页，仅保留名字用于 tab 定位与报错提示。
    "download": "下载版本",
    "online": "在线资源",
    "mods": "Mod 管理",
    "assets": "实例资源",
    "logs": "日志",
}

# 页面内页头（面包屑最后一段）；截图后必须和它对上，否则不许落盘。
# download/online/mods/assets 已被 t34 并进宿主页的 tab，页头显示宿主名；
# 子页是否真的切过去由 navigate_tabbed() 用 TAB_MARKERS 再验一次。
PAGE_HEADERS = {
    "home": "主页",
    "versions": "全部实例",
    "get": "获取",
    "instance": "实例",
    "download": "获取",
    "online": "获取",
    "mods": "实例",
    "assets": "实例",
    "logs": "日志",
    "settings": "设置",
    "tasks": "任务与通知",
    "account": "账户",
    "accounts": "账户",
    "room": "联机",
    # 账户页的子视图（只允许点开，不允许点「切换/退出账户」）。
    # 玩家信息目前是弹层而非独立页，页头仍是「账户」，故不做成截图页。
    "skin": "皮肤与披风",
}

# t34 之后：下载版本/在线资源是「获取」页的 tab，Mod 管理/实例资源是「实例」页的 tab。
TAB_HOST = {
    "download": "get",
    "online": "get",
    "mods": "instance",
    "assets": "instance",
}

# 子页内容里的稳定标记（先在 Text 里找，再到 Button 名字里找）——用于确认
# tab 真的切过去了；宿主页头对这四个页面是同一个名字，单靠页头分不出来。
TAB_MARKERS = {
    "download": ("正式版", "全部版本"),
    "online": ("Modrinth", "打开项目页"),
    "mods": ("已启用", "已停用"),
    "assets": ("资源包", "光影"),
}

# 账户页子视图：先导航到账户页，再点开子视图条目。
# 严禁点「切换/退出账户」——那会改 settings4.json 的 active（2026-11-02 事故）。
SUBVIEW_HOST = {
    "skin": ("account", "皮肤与披风"),
}

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)
oleaut32 = ctypes.WinDLL("oleaut32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
LOCK_PROCESSES = {"lockapp.exe", "logonui.exe"}

# 必须放在模块级：别的 lane 用 import shot_page 复用函数时，进程也要是
# per-monitor DPI aware，否则 UIA/GetClientRect 全被虚拟化成 96dpi 逻辑坐标，
# 页头标题会被算小一半、过滤条件全部落空（2026-11-02 踩过）。
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


# --------------------------------------------------------------------------- #
# ctypes 声明
# --------------------------------------------------------------------------- #
class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    def size(self) -> tuple[int, int]:
        return self.right - self.left, self.bottom - self.top


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", w.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
        ("biPlanes", w.WORD), ("biBitCount", w.WORD), ("biCompression", w.DWORD),
        ("biSizeImage", w.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", w.DWORD), ("biClrImportant", w.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", w.DWORD * 3)]


user32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM), w.LPARAM]
user32.IsWindowVisible.argtypes = [w.HWND]
user32.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
user32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(RECT)]
user32.GetClientRect.argtypes = [w.HWND, ctypes.POINTER(RECT)]
user32.ClientToScreen.argtypes = [w.HWND, ctypes.POINTER(POINT)]
user32.GetDpiForWindow.argtypes = [w.HWND]
user32.GetDpiForWindow.restype = w.UINT
user32.GetWindowTextLengthW.argtypes = [w.HWND]
user32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
user32.SetWindowPos.argtypes = [w.HWND, w.HWND, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, w.UINT]
user32.SetWindowPos.restype = w.BOOL
user32.ShowWindow.argtypes = [w.HWND, ctypes.c_int]
user32.IsIconic.argtypes = [w.HWND]
user32.SetForegroundWindow.argtypes = [w.HWND]
user32.BringWindowToTop.argtypes = [w.HWND]
user32.GetDC.argtypes = [w.HWND]
user32.GetDC.restype = w.HDC
user32.ReleaseDC.argtypes = [w.HWND, w.HDC]
user32.FillRect.argtypes = [w.HDC, ctypes.POINTER(RECT), w.HBRUSH]
user32.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.mouse_event.argtypes = [w.DWORD, w.DWORD, w.DWORD, w.DWORD, ctypes.c_void_p]
kernel32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
kernel32.OpenProcess.restype = w.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [w.HANDLE, w.DWORD, w.LPWSTR,
                                               ctypes.POINTER(w.DWORD)]
kernel32.QueryFullProcessImageNameW.restype = w.BOOL
kernel32.CloseHandle.argtypes = [w.HANDLE]
user32.keybd_event.argtypes = [w.BYTE, w.BYTE, w.DWORD, ctypes.c_void_p]
user32.PrintWindow.argtypes = [w.HWND, w.HDC, w.UINT]
user32.PrintWindow.restype = w.BOOL

gdi32.CreateCompatibleDC.argtypes = [w.HDC]
gdi32.CreateCompatibleDC.restype = w.HDC
gdi32.CreateCompatibleBitmap.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = w.HBITMAP
gdi32.SelectObject.argtypes = [w.HDC, w.HGDIOBJ]
gdi32.SelectObject.restype = w.HGDIOBJ
gdi32.BitBlt.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         w.HDC, ctypes.c_int, ctypes.c_int, w.DWORD]
gdi32.BitBlt.restype = w.BOOL
gdi32.GetDIBits.argtypes = [w.HDC, w.HBITMAP, w.UINT, w.UINT, ctypes.c_void_p,
                            ctypes.POINTER(BITMAPINFO), w.UINT]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.DeleteObject.argtypes = [w.HGDIOBJ]
gdi32.DeleteDC.argtypes = [w.HDC]

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
HWND_TOPMOST = w.HWND(-1)
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOZORDER = 0x0004
SWP_SHOWWINDOW = 0x0040
SWP_NOSENDCHANGING = 0x0400
SW_RESTORE = 9
WM_CLOSE = 0x0010
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
PW_RENDERFULLCONTENT = 0x00000002
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_WHEEL = 0x0800
WHEEL_DELTA = 120
VK_MENU = 0x12
VK_ESCAPE = 0x1B
KEYEVENTF_KEYUP = 0x0002


# --------------------------------------------------------------------------- #
# UI Automation：读页头标题，给截图做「这张图到底是不是这个页」的自检
# --------------------------------------------------------------------------- #
class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_ulong),
        ("Data2", ctypes.c_ushort),
        ("Data3", ctypes.c_ushort),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class VARIANT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [
            ("lVal", ctypes.c_long),
            ("llVal", ctypes.c_longlong),
            ("punkVal", ctypes.c_void_p),
            ("pwszVal", ctypes.c_wchar_p),
        ]

    _fields_ = [
        ("vt", ctypes.c_ushort),
        ("wReserved1", ctypes.c_ushort),
        ("wReserved2", ctypes.c_ushort),
        ("wReserved3", ctypes.c_ushort),
        ("_u", _U),
    ]


CLSID_CUIAutomation = "ff48dba4-60ef-4201-aa87-54103eef594e"
IID_IUIAutomation = "30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"
UIA_ControlTypePropertyId = 30003
UIA_ButtonControlTypeId = 50000
UIA_TextControlTypeId = 50020
# 内容特征不只出现在 Text/Button 上：在线资源页的「Modrinth」是 ComboBox，
# 搜索框占位文字挂在 Edit 上。标记扫描因此要覆盖这几类常见控件。
UIA_MARKER_CONTROL_TYPES = (
    UIA_TextControlTypeId,
    UIA_ButtonControlTypeId,
    50003,  # ComboBox
    50004,  # Edit
    50005,  # Hyperlink
)
UIA_TreeScope_Descendants = 4
UIA_CLSCTX_INPROC_SERVER = 1

_uia_root = None


def _guid(text: str) -> GUID:
    import uuid

    g = GUID()
    ctypes.memmove(ctypes.byref(g), uuid.UUID(text).bytes_le, 16)
    return g


def _com_call(obj, index: int, restype, *argtypes):
    """取 COM 对象 vtable 上的第 index 个函数。"""
    vtbl = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))[0]
    return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(vtbl[index])


def _hr(value: int) -> int:
    return ctypes.c_long(value).value


def uia_elements(hwnd, control_type: int) -> list[tuple[str, int, int, int, int]]:
    """窗口内指定 ControlType 的元素：(name, left, top, right, bottom)，屏幕物理像素。"""
    global _uia_root
    try:
        if _uia_root is None:
            _hr(ole32.CoInitializeEx(None, 2))
            clsid, iid = _guid(CLSID_CUIAutomation), _guid(IID_IUIAutomation)
            uia = ctypes.c_void_p()
            hr = _hr(ole32.CoCreateInstance(
                ctypes.byref(clsid), None, UIA_CLSCTX_INPROC_SERVER,
                ctypes.byref(iid), ctypes.byref(uia)))
            if hr < 0:
                return []
            _uia_root = uia
        uia = _uia_root
        elem = ctypes.c_void_p()
        if _hr(_com_call(uia, 6, ctypes.c_long, w.HWND,
                         ctypes.POINTER(ctypes.c_void_p))(
                uia, hwnd, ctypes.byref(elem))) < 0:
            return []
        cond = ctypes.c_void_p()
        var = VARIANT()
        var.vt = 3  # VT_I4
        var._u.lVal = control_type
        if _hr(_com_call(uia, 23, ctypes.c_long, ctypes.c_int, VARIANT,
                         ctypes.POINTER(ctypes.c_void_p))(
                uia, UIA_ControlTypePropertyId, var, ctypes.byref(cond))) < 0:
            _com_call(elem, 2, ctypes.c_ulong)(elem)
            return []
        arr = ctypes.c_void_p()
        if _hr(_com_call(elem, 6, ctypes.c_long, ctypes.c_int, ctypes.c_void_p,
                         ctypes.POINTER(ctypes.c_void_p))(
                elem, UIA_TreeScope_Descendants, cond, ctypes.byref(arr))) < 0:
            _com_call(cond, 2, ctypes.c_ulong)(cond)
            _com_call(elem, 2, ctypes.c_ulong)(elem)
            return []
        count = ctypes.c_int()
        _com_call(arr, 3, ctypes.c_long, ctypes.POINTER(ctypes.c_int))(
            arr, ctypes.byref(count))
        out = []
        for i in range(count.value):
            child = ctypes.c_void_p()
            if _hr(_com_call(arr, 4, ctypes.c_long, ctypes.c_int,
                             ctypes.POINTER(ctypes.c_void_p))(
                    arr, i, ctypes.byref(child))) < 0:
                continue
            name_ptr = ctypes.c_void_p()
            _com_call(child, 23, ctypes.c_long, ctypes.POINTER(ctypes.c_void_p))(
                child, ctypes.byref(name_ptr))
            offscreen = ctypes.c_int(0)
            _com_call(child, 38, ctypes.c_long, ctypes.POINTER(ctypes.c_int))(
                child, ctypes.byref(offscreen))
            rc = RECT()
            _com_call(child, 43, ctypes.c_long, ctypes.POINTER(RECT))(
                child, ctypes.byref(rc))
            name = ctypes.wstring_at(name_ptr.value) if name_ptr.value else ""
            if name_ptr.value:
                oleaut32.SysFreeString(name_ptr)
            # Frame 会把导航过的页面缓存住，旧页的文本仍留在 UIA 树里；
            # IsOffscreen 能把它们剔掉，否则会读到上一页的面包屑。
            if name and not offscreen.value:
                out.append((name, rc.left, rc.top, rc.right, rc.bottom))
            _com_call(child, 2, ctypes.c_ulong)(child)
        _com_call(arr, 2, ctypes.c_ulong)(arr)
        _com_call(cond, 2, ctypes.c_ulong)(cond)
        _com_call(elem, 2, ctypes.c_ulong)(elem)
        return out
    except Exception as exc:  # UIA 不可用时只降级为「不校验」，不拖垮截图
        print(f"[shot] UIA 读取失败：{exc}", file=sys.stderr)
        return []


def uia_texts(hwnd) -> list[tuple[str, int, int, int, int]]:
    """窗口内所有 Text 控件。"""
    return uia_elements(hwnd, UIA_TextControlTypeId)


def uia_buttons(hwnd) -> list[tuple[str, int, int, int, int]]:
    """窗口内所有 Button / RadioButton 控件（分段筛选是 RadioButton）。"""
    return (uia_elements(hwnd, UIA_ButtonControlTypeId)
            + uia_elements(hwnd, getattr(sys.modules[__name__], "UIA_RadioButtonControlTypeId", 50013)))


def uia_names(hwnd) -> list[str]:
    """Text/Button/ComboBox/Edit/Hyperlink 的名字合集（去重，保持顺序）。

    内容特征不一定落在 Text 上：ComboBox 的当前值、Edit 的占位文字都算。
    """
    names: list[str] = []
    seen: set[str] = set()
    for control_type in UIA_MARKER_CONTROL_TYPES:
        for item in uia_elements(hwnd, control_type):
            name = item[0]
            if name and name not in seen:
                seen.add(name)
                names.append(name)
    return names


def uia_shapes(hwnd) -> list[tuple[str, int, int, int, int]]:
    """常见控件类型里带矩形 (name,left,top,right,bottom)，按 (name, rect) 去重。

    hover 需要控件的坐标，不能只看名字：Text / Button / ComboBox / Edit / Hyperlink。
    """
    out: list[tuple[str, int, int, int, int]] = []
    seen: set[tuple[str, int, int, int, int]] = set()
    for control_type in UIA_MARKER_CONTROL_TYPES:
        for item in uia_elements(hwnd, control_type):
            if item in seen:
                continue
            seen.add(item)
            out.append(item)
    return out


def page_title(hwnd) -> str | None:
    """页头面包屑最后一段：内容区左上、最高的那段文字。"""
    texts = uia_texts(hwnd)
    if not texts:
        return None
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    scale = dpi_scale(hwnd) or 1.0
    candidates = []
    for name, left, top, right, bottom in texts:
        x = (left - origin.x) / scale
        y = (top - origin.y) / scale
        height = (bottom - top) / scale
        if x < 230 or y < 0 or y > 120 or height < 24:
            continue
        candidates.append((height, x, name))
    if not candidates:
        # 窄窗口档（侧栏 64 图标栏）：内容左移到 x≈64，标题 28→22 后高度也低于 24，
        # 上面那套「排除侧栏 + 高度 ≥24」的阈值会全部落空。此时改用窄档阈值重扫一次，
        # 侧栏在窄档只剩图标（无文字），收紧 y/高度仍能把页头挑出来。
        for name, left, top, right, bottom in texts:
            x = (left - origin.x) / scale
            y = (top - origin.y) / scale
            height = (bottom - top) / scale
            if x < 56 or y < 0 or y > 120 or height < 16:
                continue
            candidates.append((height, x, name))
    if not candidates:
        return None
    # 同高时取最右：面包屑最后一段就是当前页，左边那几段是「主页 ›」
    candidates.sort(reverse=True)
    return candidates[0][2]


def title_matches(hwnd, page: str) -> tuple[bool, str | None]:
    expected = PAGE_HEADERS.get(page)
    actual = page_title(hwnd)
    if expected is None:
        return True, actual
    return actual == expected, actual


# 侧栏宽度（设计像素）。导航项的文字标签落在这一带，内容区标题不会。
SIDEBAR_WIDTH = 230

# 首页骨架期只有灰块，WidgetGrid 里的这些文字要等数据到了才出现在 UIA 树里。
HOME_READY_MARKERS = ("每日一帖", "截图墙", "快捷操作", "最近启动", "服务状态", "时钟")


def nav_item_center(hwnd, page: str) -> tuple[float, float] | None:
    """用 UIA 找侧栏导航项，返回其中心（设计像素）；找不到返回 None。

    侧栏已按「玩 / 获取 / 实例 / 我的」分四组，硬编码 Y 会随版本漂移；
    优先按 ToggleButton 的 AutomationProperties.Name（UIA Name）定位，
    组标题只是 Text、不会被误当成导航项。
    """
    label = NAV_LABELS.get(page)
    if not label:
        return None
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    scale = dpi_scale(hwnd) or 1.0

    def rect_of(item):
        name, left, top, right, bottom = item
        return name.strip(), (left - origin.x) / scale, (top - origin.y) / scale, \
            (right - left) / scale, (bottom - top) / scale

    # 1) Button 控件：Name 就是 AutomationProperties.Name，最可靠。
    matches = []
    for item in uia_buttons(hwnd):
        name, x, y, w, h = rect_of(item)
        if name != label or x < 0 or x + w > SIDEBAR_WIDTH + 8 or y < 120 or h < 20:
            continue
        matches.append((y, x, w, h))
    if matches:
        matches.sort()
        y, x, w, h = matches[0]
        return x + w / 2, y + h / 2

    # 2) 旧构建只把文字暴露成 Text：同名取最下面（组标题在上、导航项在下）。
    for item in uia_texts(hwnd):
        name, x, y, w, h = rect_of(item)
        if name != label or x < 0 or x + w > SIDEBAR_WIDTH or y < 40 or h < 12:
            continue
        matches.append((y, x, w, h))
    if not matches:
        return None
    matches.sort()
    y, x, w, h = matches[-1]
    return x + w / 2, y + h / 2


# 页面级「内容就绪」表：页头对只是第一步，内容可能还在加载。
# - absent：出现任一即视为未就绪（加载中/骨架文案）
# - any/min：至少要命中 min 个特征词
# - list：必须出现至少一个列表项特征，避免「空列表」被当成已加载
PAGE_READY: dict[str, dict] = {
    "home": {"absent": ("正在读取",), "any": HOME_READY_MARKERS, "min": 2},
    "versions": {"absent": ("正在扫描",),
                 "any": ("还没有本地实例", "个 Mod", "无 Mod", "当前")},
    "account": {"absent": ("正在读取账户",),
                "any": ("玩家档案", "离线账户", "添加账户")},
    "accounts": {"absent": ("正在读取账户",),
                 "any": ("已登录", "离线账户", "添加账户")},
    "tasks": {"any": ("进行中", "已完成", "失败", "还没有任务")},
    "instance": {"any": ("Mod 管理", "资源包", "存档", "截图"), "min": 2},
    "download": {"any": ("正式版",), "list": ("安装",)},
    "online": {"any": ("整合包", "资源包", "光影"), "list": ("打开项目页", "安装")},
    "mods": {"any": ("已启用", "已停用"), "list": ("KB", "MB", "GB")},
    "assets": {"any": ("资源包", "光影"), "list": ("KB", "MB", "GB")},
}


def home_page_indicator(names: list[str]) -> tuple[int, int] | None:
    """从 UIA 文字里找首页分页指示器（形如 "2 / 2"），返回 (当前页, 总页数)。"""
    for name in names:
        compact = name.replace(" ", "").strip()
        left, sep, right = compact.partition("/")
        if not sep or not left.isdigit() or not right.isdigit():
            continue
        total = int(right)
        if total > 1:
            return int(left), total
    return None


def content_ready(hwnd, page: str) -> bool:
    """页面内容是否真的加载完（只看 UIA 文本/按钮名，不做像素判断）。"""
    spec = PAGE_READY.get(page)
    if spec is None:
        return True
    names = uia_names(hwnd)
    if not names:
        return False
    if any(token in name for name in names for token in spec.get("absent", ())):
        return False
    hits = sum(1 for token in spec.get("any", ())
               if any(token in name for name in names))
    if hits < spec.get("min", 1) and page == "home":
        # 首页可能翻到第 2 页而该页为空：等 300ms 再看一次；
        # 若仍无卡、但分页器显示当前页 > 1，则按「空页」放过，不算超时。
        time.sleep(0.3)
        names = uia_names(hwnd)
        hits = sum(1 for token in spec.get("any", ())
                   if any(token in name for name in names))
        if hits < spec.get("min", 1):
            indicator = home_page_indicator(names)
            if indicator and indicator[0] > 1:
                print(f"[shot] home 位于第 {indicator[0]}/{indicator[1]} 页且无卡，按空页放过")
                return True
    if hits < spec.get("min", 1):
        return False
    list_tokens = spec.get("list", ())
    if list_tokens and not any(token in name for name in names
                               for token in list_tokens):
        return False
    if page in TAB_HOST and not tab_ready(hwnd, page):
        return False
    return True


def wait_page_ready(hwnd, page: str, timeout: float = 15.0,
                    interval: float = 0.2) -> tuple[bool, float]:
    """轮询到内容就绪：返回 (是否就绪, 等待秒数)。无判定表的页面等 100ms 稳定。"""
    started = time.time()
    if page not in PAGE_READY:
        time.sleep(0.1)
        return True, time.time() - started
    while True:
        if content_ready(hwnd, page):
            return True, time.time() - started
        if time.time() - started >= timeout:
            break
        time.sleep(interval)
    print(f"[shot] 内容就绪超时（{page}，{timeout:.0f}s）", file=sys.stderr)
    sample = uia_names(hwnd)[:24]
    print(f"[shot] 当前可见文字样本：{sample}", file=sys.stderr)
    return False, time.time() - started


# --------------------------------------------------------------------------- #
# PNG
# --------------------------------------------------------------------------- #
def write_png(path: Path, width: int, height: int, rgb: bytes) -> None:
    stride = width * 3
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter: none
        raw += rgb[y * stride:(y + 1) * stride]

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
        + chunk(b"IEND", b"")
    )


# --------------------------------------------------------------------------- #
# 截图
# --------------------------------------------------------------------------- #
def bgra_to_rgb(data: bytes, width: int, height: int) -> bytes:
    out = bytearray(width * height * 3)
    j = 0
    for i in range(0, width * height * 4, 4):
        out[j] = data[i + 2]
        out[j + 1] = data[i + 1]
        out[j + 2] = data[i]
        j += 3
    return bytes(out)


def grab_client(hwnd) -> tuple[int, int, bytes] | None:
    """截客户区，返回 (宽, 高, RGB)。先 BitBlt 屏幕，失败/全黑再 PrintWindow。"""
    rect = RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rect))
    width, height = rect.size()
    if width < 50 or height < 50:
        return None
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))

    screen = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, width, height)
    old = gdi32.SelectObject(mem, bmp)
    rgb: bytes | None = None
    try:
        ok = gdi32.BitBlt(mem, 0, 0, width, height, screen, origin.x, origin.y, SRCCOPY)
        if ok:
            rgb = read_dib(mem, bmp, width, height)
        if not rgb or is_flat(rgb):
            # 有些显卡/远程桌面下 BitBlt 拿不到 DirectComposition 内容
            mem2 = gdi32.CreateCompatibleDC(screen)
            bmp2 = gdi32.CreateCompatibleBitmap(screen, width, height)
            old2 = gdi32.SelectObject(mem2, bmp2)
            try:
                if user32.PrintWindow(hwnd, mem2, PW_RENDERFULLCONTENT):
                    alt = read_dib(mem2, bmp2, width, height)
                    if alt and not is_flat(alt):
                        rgb = alt
            finally:
                gdi32.SelectObject(mem2, old2)
                gdi32.DeleteObject(bmp2)
                gdi32.DeleteDC(mem2)
    finally:
        gdi32.SelectObject(mem, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(None, screen)
    if not rgb:
        return None
    return width, height, rgb


def read_dib(mem, bmp, width: int, height: int) -> bytes:
    info = BITMAPINFO()
    info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    info.bmiHeader.biWidth = width
    info.bmiHeader.biHeight = -height  # top-down
    info.bmiHeader.biPlanes = 1
    info.bmiHeader.biBitCount = 32
    info.bmiHeader.biCompression = 0
    buf = ctypes.create_string_buffer(width * height * 4)
    copied = gdi32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(info), DIB_RGB_COLORS)
    if copied == 0:
        return b""
    return bgra_to_rgb(buf.raw, width, height)


def is_flat(rgb: bytes) -> bool:
    """全黑/全白/单色说明没画出来。"""
    if not rgb:
        return True
    step = max(3, len(rgb) // 4000 * 3)
    seen = set()
    for i in range(0, len(rgb) - 2, step):
        seen.add(rgb[i:i + 3])
        if len(seen) > 8:
            return False
    return True


# --------------------------------------------------------------------------- #
# 窗口操作
# --------------------------------------------------------------------------- #
def find_main_window(pid: int, timeout: float) -> int | None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        hits: list[int] = []

        @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        def callback(hwnd, _lparam):
            owner = w.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value != pid or not user32.IsWindowVisible(hwnd):
                return True
            rect = RECT()
            user32.GetClientRect(hwnd, ctypes.byref(rect))
            if rect.size()[0] > 300 and rect.size()[1] > 200:
                hits.append(hwnd)
            return True

        user32.EnumWindows(callback, 0)
        if hits:
            return hits[0]
        time.sleep(0.2)
    return None


def window_title(hwnd) -> str:
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def dpi_scale(hwnd) -> float:
    dpi = user32.GetDpiForWindow(hwnd)
    return (dpi or 96) / 96.0


def frame_delta(hwnd) -> tuple[int, int]:
    wr, cr = RECT(), RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(wr))
    user32.GetClientRect(hwnd, ctypes.byref(cr))
    return wr.size()[0] - cr.size()[0], wr.size()[1] - cr.size()[1]


def force_client_size(hwnd, design_w: int, design_h: int) -> tuple[int, int]:
    """把客户区强制成 design*scale；SWP_NOSENDCHANGING 绕过最小窗口尺寸。"""
    scale = dpi_scale(hwnd)
    target_w = round(design_w * scale)
    target_h = round(design_h * scale)
    for _ in range(4):
        dx, dy = frame_delta(hwnd)
        outer_w, outer_h = target_w + dx, target_h + dy
        screen_w = user32.GetSystemMetrics(0)
        screen_h = user32.GetSystemMetrics(1)
        x = max(0, (screen_w - outer_w) // 2)
        y = 0
        user32.SetWindowPos(
            hwnd, HWND_TOPMOST, x, y, outer_w, outer_h,
            SWP_SHOWWINDOW | SWP_NOSENDCHANGING,
        )
        time.sleep(0.45)
        rect = RECT()
        user32.GetClientRect(hwnd, ctypes.byref(rect))
        if abs(rect.size()[0] - target_w) <= 2 and abs(rect.size()[1] - target_h) <= 2:
            break
    rect = RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rect))
    return rect.size()


def focus_window(hwnd) -> None:
    if user32.IsIconic(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)
    if user32.GetForegroundWindow() != hwnd:
        # 前台锁定时先敲一下 ALT，再抢一次
        user32.keybd_event(VK_MENU, 0, 0, None)
        user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, None)
        user32.SetForegroundWindow(hwnd)
    time.sleep(0.35)


def click_design(hwnd, x: float, y: float) -> None:
    scale = dpi_scale(hwnd)
    origin = POINT(int(round(x * scale)), int(round(y * scale)))
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    user32.SetCursorPos(origin.x, origin.y)
    time.sleep(0.08)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, None)
    time.sleep(0.05)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, None)


def click_screen(x: float, y: float) -> None:
    """点屏幕物理坐标（UIA 给的 rect 就是物理像素）。"""
    user32.SetCursorPos(int(round(x)), int(round(y)))
    time.sleep(0.08)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, None)
    time.sleep(0.05)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, None)


def pointer_move(hwnd, x: float, y: float, *, settle: float = 0.35) -> None:
    """把鼠标真实移到屏幕坐标 (x, y)，让 WinUI 收到 PointerMoved / hover。

    只 SetCursorPos 时光标虽然动了，但目标窗口收不到 WM_MOUSEMOVE，
    卡片的 hover 态（例如版本行上的 ↑↓）不会出现。这里先把光标放到目标
    左侧 40px，再用相对 MOVE 走进目标点，最后发一次原地 MOVE；移动结束
    就停在目标点上，不再移开——移开会让 hover 立刻消失。
    """
    focus_window(hwnd)
    time.sleep(0.2)
    ix, iy = int(round(x)), int(round(y))
    user32.SetCursorPos(ix - 40, iy)
    time.sleep(0.06)
    user32.mouse_event(MOUSEEVENTF_MOVE, 40, 0, 0, None)
    time.sleep(0.06)
    user32.mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, None)
    time.sleep(0.05)
    user32.mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, None)
    user32.mouse_event(MOUSEEVENTF_MOVE, -1, 0, 0, None)
    time.sleep(settle)


def press_escape(hwnd=None) -> None:
    """Esc 关闭弹层：投递到主窗口（前台被弹层抢走时 keybd_event 不一定送达）。"""
    if hwnd is not None:
        user32.PostMessageW(hwnd, WM_KEYDOWN, VK_ESCAPE, 0)
        user32.PostMessageW(hwnd, WM_KEYUP, VK_ESCAPE, 0)
    else:
        user32.keybd_event(VK_ESCAPE, 0, 0, None)
        user32.keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, None)
    time.sleep(0.3)


def pid_of(hwnd) -> int:
    pid = w.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def process_name(pid: int) -> str | None:
    """按 pid 取可执行文件名（小写，含 .exe）；取不到返回 None。"""
    if not pid:
        return None
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = w.DWORD(len(buf))
        if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return Path(buf.value).name.lower()
    finally:
        kernel32.CloseHandle(handle)
    return None


def foreground_process() -> str | None:
    fg = user32.GetForegroundWindow()
    if not fg:
        return None
    return process_name(pid_of(fg))


def screen_locked() -> bool:
    """桌面是否处于锁屏：前台进程是 LockApp/LogonUI；前台拿不到时再看 LogonUI 是否在。

    注意不能只看 tasklist：LogonUI 有时解锁后仍残留，按进程存在判定会把正常
    桌面误判成锁屏、把所有截图都挡掉。前台能拿到普通进程时一律视为已解锁。
    """
    fg = foreground_process()
    if fg in LOCK_PROCESSES:
        return True
    if fg:
        return False
    # 前台拿不到（锁屏/安全桌面切换瞬间）：再用 LogonUI 兜底。
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq LogonUI.exe", "/NH"],
            capture_output=True, timeout=8).stdout.lower()
    except (OSError, subprocess.SubprocessError):
        return False
    return b"logonui.exe" in out


def app_windows(hwnd) -> list[int]:
    """主窗口 + 同进程的可见弹层（Flyout 是独立 HWND）。"""
    pid = pid_of(hwnd)
    hits = [hwnd]

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def callback(win, _lparam):
        owner = w.DWORD()
        user32.GetWindowThreadProcessId(win, ctypes.byref(owner))
        if owner.value != pid or win == hwnd or not user32.IsWindowVisible(win):
            return True
        rc = RECT()
        user32.GetWindowRect(win, ctypes.byref(rc))
        if rc.right - rc.left >= 180 and rc.bottom - rc.top >= 80:
            hits.append(win)
        return True

    user32.EnumWindows(callback, 0)
    return hits


def app_button_points(hwnd, name: str) -> list[tuple[float, float]]:
    """在应用的所有窗口里找按钮（含弹层），返回中心点（屏幕物理像素）。"""
    points = []
    for win in app_windows(hwnd):
        for text, left, top, right, bottom in uia_buttons(win):
            if text.strip() == name:
                points.append(((left + right) / 2, (top + bottom) / 2))
    return points


def content_text_center(hwnd, label: str) -> tuple[float, float] | None:
    """内容区（x>230）里某段文字的中心（设计像素），同名取最下面一个。"""
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    scale = dpi_scale(hwnd) or 1.0
    hits = []
    for name, left, top, right, bottom in uia_texts(hwnd):
        if name.strip() != label:
            continue
        x = (left - origin.x) / scale
        y = (top - origin.y) / scale
        w = (right - left) / scale
        h = (bottom - top) / scale
        if x <= SIDEBAR_WIDTH or y < 40:
            continue
        hits.append((y, x, w, h))
    if not hits:
        return None
    hits.sort()
    y, x, w, h = hits[-1]
    return x + w / 2, y + h / 2


def control_center(hwnd, label: str) -> tuple[float, float] | None:
    """按 UIA 名字找控件中心（屏幕物理像素），供 hover 用。

    优先内容区（x > SIDEBAR_WIDTH、y >= 40）；同名多个时取**面积最小**的
    那个（通常是行标题文字，而不是整行容器），这样目标点稳定落在行内，
    不会因为窗口位置偏移几十像素就落到卡片外。
    """
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    scale = dpi_scale(hwnd) or 1.0
    hits = []
    for name, left, top, right, bottom in uia_shapes(hwnd):
        if name.strip() != label:
            continue
        x = (left - origin.x) / scale
        y = (top - origin.y) / scale
        area = max(0, right - left) * max(0, bottom - top)
        hits.append((area, y, x, (left + right) / 2, (top + bottom) / 2))
    if not hits:
        return None
    content = [h for h in hits if h[2] > SIDEBAR_WIDTH and h[1] >= 40]
    pool = content or hits
    pool.sort(key=lambda h: (h[0], h[1]))
    return pool[0][3], pool[0][4]


def tab_button_center(hwnd, label: str) -> tuple[float, float] | None:
    """内容区里名为 label 的 tab 按钮中心（屏幕物理像素）。

    排除侧栏（x<=230）与整行大按钮，取最上面一个（tab 行在页头下方）。
    """
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    scale = dpi_scale(hwnd) or 1.0
    hits = []
    for name, left, top, right, bottom in uia_buttons(hwnd):
        if name.strip() != label:
            continue
        x = (left - origin.x) / scale
        y = (top - origin.y) / scale
        bw = (right - left) / scale
        bh = (bottom - top) / scale
        if x <= SIDEBAR_WIDTH or y < 40:
            continue
        if bw > 260 or bh > 60:  # 整行按钮不是 tab
            continue
        hits.append((y, x, (left + right) / 2, (top + bottom) / 2))
    if not hits:
        return None
    hits.sort()
    return hits[0][2], hits[0][3]


def tab_ready(hwnd, page: str) -> bool:
    """tab 子页的内容特征是否已出现（这四个页面的页头都是宿主名，分不出来）。"""
    markers = TAB_MARKERS.get(page, ())
    if not markers:
        return True
    blob = "\n".join(uia_names(hwnd))
    return any(marker in blob for marker in markers)


def scroll_by(hwnd, design_px: float) -> None:
    """在内容区滚轮滚动约 design_px 像素（正=向下），给设置页下半截用。"""
    rc = RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rc))
    pt = POINT(int(rc.right * 0.62), int(rc.bottom * 0.55))
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    user32.SetCursorPos(pt.x, pt.y)
    time.sleep(0.1)
    notches = max(1, round(abs(design_px) / 60.0))
    step = -WHEEL_DELTA if design_px > 0 else WHEEL_DELTA
    for _ in range(notches):
        user32.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, step & 0xFFFFFFFF, None)
        time.sleep(0.02)


def find_text_rect(hwnd, needle: str) -> tuple[int, int, int, int] | None:
    """按文本找元素（屏幕物理像素），先精确再包含。"""
    texts = uia_texts(hwnd)
    for match in (lambda n: n.strip() == needle, lambda n: needle in n):
        for name, left, top, right, bottom in texts:
            if match(name):
                return (left, top, right, bottom)
    return None


def scroll_to(hwnd, needle: str, max_steps: int = 30) -> bool:
    """滚到指定文字完整进入视口。"""
    scale = dpi_scale(hwnd) or 1.0
    rc = RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rc))
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    top_limit = origin.y + int(48 * scale)
    bottom_limit = origin.y + rc.bottom - int(16 * scale)
    for _ in range(max_steps):
        rect = find_text_rect(hwnd, needle)
        if rect is None:
            scroll_by(hwnd, 320)
        elif rect[1] >= top_limit and rect[3] <= bottom_limit:
            print(f"[shot] 已滚到 {needle!r}（rect={rect}）")
            return True
        elif rect[1] > bottom_limit:
            scroll_by(hwnd, 320)
        else:
            scroll_by(hwnd, -320)
        time.sleep(0.35)
    print(f"[shot] 滚不到 {needle!r}", file=sys.stderr)
    return False


def mean_diff(a: bytes, b: bytes) -> float:
    """两张同尺寸 RGB 的抽样平均绝对差（0..255）。"""
    if len(a) != len(b):
        return 255.0
    step = 3 * 7  # 每 7 个像素取一个
    total = 0
    count = 0
    for i in range(0, len(a) - 2, step):
        total += abs(a[i] - b[i]) + abs(a[i + 1] - b[i + 1]) + abs(a[i + 2] - b[i + 2])
        count += 3
    return total / count if count else 0.0


def navigate_tabbed(hwnd, page: str) -> bool:
    """download/online 走「获取」页 tab，mods/assets 走「实例」页 tab。

    先按 UIA 名字切到宿主页，再点内容区里同名的 tab 按钮，最后用 tab 内容
    特征（TAB_MARKERS）确认子页真的换了；页头只能证明宿主对。
    """
    host = TAB_HOST[page]
    label = NAV_LABELS[page]
    for attempt in (1, 2, 3):
        if not navigate(hwnd, host):
            return False
        wait_page_ready(hwnd, host)
        center = tab_button_center(hwnd, label)
        if center is not None:
            click_screen(*center)
            where = f"UIA tab ({center[0]:.0f},{center[1]:.0f})"
        else:
            pos = content_text_center(hwnd, label)
            if pos is None:
                print(f"[shot] 切页失败：{page} 找不到 {host} 页的 tab {label!r}",
                      file=sys.stderr)
                return False
            click_design(hwnd, *pos)
            where = f"文字 tab ({pos[0]:.0f},{pos[1]:.0f})"
        for _ in range(16):
            time.sleep(0.5)
            if tab_ready(hwnd, page):
                ok, actual = title_matches(hwnd, page)
                if ok:
                    print(f"[shot] 切页 {page}：{host} 页 tab {label!r}，{where}，"
                          f"页头={actual!r}，内容已就绪")
                    return True
        print(f"[shot] 切页 {page}：第 {attempt} 轮 tab 内容未就绪"
              f"（期望 {TAB_MARKERS.get(page)}），重试…", file=sys.stderr)
    print(f"[shot] 切页失败：{page}（tab {label!r} 点了 3 轮内容仍对不上）",
          file=sys.stderr)
    return False


def navigate_tasks(hwnd) -> bool:
    """任务页不在侧栏：点左下「任务与通知」dock，再点弹层里的「查看全部任务与通知」。

    首页改版后（继续玩/组件板两态）不再固定放任务卡，所以优先走 dock 弹层；
    旧布局首页有任务卡时，用 content_text_center("任务") 兜底。
    """
    expected = PAGE_HEADERS["tasks"]
    for attempt in (1, 2, 3):
        press_escape(None)  # 清掉上一轮可能残留的弹层
        if not navigate(hwnd, "home"):
            return False
        wait_page_ready(hwnd, "home")
        time.sleep(0.3)

        dock = app_button_points(hwnd, "任务与通知")
        if dock:
            click_screen(*dock[0])
            where = f"dock ({dock[0][0]:.0f},{dock[0][1]:.0f})"
        else:
            click_design(hwnd, 109, 800)  # dock 贴底（1440x900 设计坐标）
            where = "dock (109,800)"
        time.sleep(1.0)

        more = app_button_points(hwnd, "查看全部任务与通知")
        if more:
            click_screen(*more[0])
            where += " → 查看全部任务与通知"
            time.sleep(1.2)

        ok, actual = title_matches(hwnd, "tasks")
        if ok:
            print(f"[shot] 切页 tasks：{where}，页头={actual!r}，已确认")
            return True

        # 旧首页任务卡兜底（新版首页没有这张卡，点了也只是无操作）
        center = content_text_center(hwnd, "任务")
        if center:
            click_design(hwnd, *center)
            time.sleep(1.2)
            ok, actual = title_matches(hwnd, "tasks")
            if ok:
                print(f"[shot] 切页 tasks：首页任务卡 ({center[0]:.0f},{center[1]:.0f})，"
                      f"页头={actual!r}，已确认")
                return True

        print(f"[shot] 切页 tasks：第 {attempt} 次失败，页头={actual!r}"
              f"（期望 {expected!r}），重试…", file=sys.stderr)
    print("[shot] 切页失败：tasks（dock 弹层与首页任务卡都没能进任务页）",
          file=sys.stderr)
    return False


def navigate_subview(hwnd, page: str) -> bool:
    """账户页子视图：先到账户页，再点开子视图条目（只点开，不切换账户）。"""
    host, label = SUBVIEW_HOST[page]
    expected = PAGE_HEADERS[page]
    for attempt in (1, 2, 3):
        if not navigate(hwnd, host):
            return False
        wait_page_ready(hwnd, host)
        time.sleep(0.4)
        points = app_button_points(hwnd, label)
        if points:
            click_screen(*points[0])
            where = f"UIA 按钮 ({points[0][0]:.0f},{points[0][1]:.0f})"
        else:
            pos = content_text_center(hwnd, label)
            if pos is None:
                print(f"[shot] 切页失败：{page} 找不到账户页子视图 {label!r}",
                      file=sys.stderr)
                return False
            click_design(hwnd, *pos)
            where = f"文字行 ({pos[0]:.0f},{pos[1]:.0f})"
        time.sleep(1.0)
        ok, actual = title_matches(hwnd, page)
        if ok:
            print(f"[shot] 切页 {page}：账户页点开 {label!r}，{where}，"
                  f"页头={actual!r}，已确认")
            return True
        print(f"[shot] 切页 {page}：第 {attempt} 次页头={actual!r}"
              f"（期望 {expected!r}），重试…", file=sys.stderr)
    print(f"[shot] 切页失败：{page}（账户页子视图 {label!r} 打不开）", file=sys.stderr)
    return False


def navigate(hwnd, page: str, nav_y: int | None = None) -> bool:
    """点侧栏切页，并用页头标题确认。

    优先按导航项名称走 UIA（取控件 rect 中心真实点击）；UIA 找不到或点了
    两次页头仍不对，才回落到 --nav-y / PAGES 里的固定坐标。两端都失败就
    直接返回 False，绝不把别的页当目标页截下去。
    任务页不在侧栏，走 navigate_tasks() 的首页任务卡路径。
    """
    if page == "tasks" and nav_y is None:
        return navigate_tasks(hwnd)
    if page in TAB_HOST and nav_y is None:
        return navigate_tabbed(hwnd, page)
    if page in SUBVIEW_HOST and nav_y is None:
        return navigate_subview(hwnd, page)
    expected = PAGE_HEADERS.get(page)

    # 1) 首选 UIA：按导航项名称取 rect 中心真实点击。侧栏分组/字号一改，
    #    写死 Y 必漂（settings 就栽在这上面），所以 --nav-y 也不能抢在它前面。
    center = nav_item_center(hwnd, page)
    if center is not None:
        x, y = center
        for attempt in (1, 2):
            click_design(hwnd, x, y)
            time.sleep(0.9)
            ok, actual = title_matches(hwnd, page)
            if ok:
                print(f"[shot] 切页 {page}：UIA 点击 ({x:.0f},{y:.0f})，"
                      f"页头={actual!r}，已确认")
                return True
            print(f"[shot] 切页 {page}：UIA ({x:.0f},{y:.0f}) 第 {attempt} 次，"
                  f"页头={actual!r}（期望 {expected!r}），重试…")
        print(f"[shot] UIA 点击 {page} 两次未生效，改用固定坐标兜底", file=sys.stderr)
    else:
        print(f"[shot] 侧栏 UIA 未找到 {NAV_LABELS.get(page)!r}，改用固定坐标兜底",
              file=sys.stderr)

    # 2) 兜底：--nav-y 优先，其次 PAGES 里最后一次测量值。
    base_x, base_y = PAGES.get(page, PAGES["home"])
    fallback_y = nav_y if nav_y is not None else base_y
    for offset in (0, -6, 6, -12, 12, -18, 18):
        y = fallback_y + offset
        for attempt in (1, 2):
            click_design(hwnd, base_x, y)
            time.sleep(0.9)
            ok, actual = title_matches(hwnd, page)
            if ok:
                print(f"[shot] 切页 {page}：固定坐标 ({base_x},{y})，"
                      f"页头={actual!r}，已确认")
                return True
    print(f"[shot] 切页失败：{page}（UIA 与固定坐标都没能让页头对上）",
          file=sys.stderr)
    return False


# --------------------------------------------------------------------------- #
# 主流程
# --------------------------------------------------------------------------- #
def prefs_path() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData/Local")
    return Path(base) / "MeowLauncher.WinUI" / "app.json"


def account_forbidden_hit(hwnd, screen_x: float, screen_y: float,
                          page: str) -> str | None:
    """账户页上如果点击落点压到「切换/退出账户」，返回按钮名；否则 None。

    settings4.json 的 active 事故就是自动点击点到「切换」造成的。坐标点击
    也要挡：按 UIA 拿到这些按钮的矩形中心，落点相距在按钮范围内即拒绝。
    """
    if page not in ("account", "accounts", "skin"):
        return None
    for bad in ("切换", "退出账户", "退出登录", "删除"):
        for bx, by in app_button_points(hwnd, bad):
            if abs(screen_x - bx) <= 70 and abs(screen_y - by) <= 26:
                return bad
    # 其他账户是「整行点击即切换」：点到带「离线账户」标签的行也会切走 active。
    for name, left, top, right, bottom in uia_shapes(hwnd):
        if name.strip() in ("离线账户",):
            if (left - 120 <= screen_x <= right + 120
                    and top - 30 <= screen_y <= bottom + 30):
                return "其他账户行（点击会切换账户）"
    return None


def run_actions(hwnd, actions: str, out_dir: Path, page: str, design_w: int, design_h: int,
                allow_mismatch: bool = False) -> None:
    for raw in [a.strip() for a in actions.split(";") if a.strip()]:
        name, _, value = raw.partition(":")
        name = name.lower()
        if name == "click":
            x_s, y_s = value.split(",")
            scale = dpi_scale(hwnd) or 1.0
            pt = POINT(int(round(float(x_s) * scale)), int(round(float(y_s) * scale)))
            user32.ClientToScreen(hwnd, ctypes.byref(pt))
            bad = account_forbidden_hit(hwnd, pt.x, pt.y, page)
            if bad:
                raise ValueError(
                    f"账户页截图禁止点击「{bad}」位置：切换/退出账户会改 settings4.json "
                    f"的 active 配置；只允许点开子视图，多账户画面请用滚动/翻页")
            focus_window(hwnd)
            click_design(hwnd, float(x_s), float(y_s))
            time.sleep(0.5)
        elif name == "move":
            x_s, y_s = value.split(",")
            scale = dpi_scale(hwnd)
            pt = POINT(int(round(float(x_s) * scale)), int(round(float(y_s) * scale)))
            user32.ClientToScreen(hwnd, ctypes.byref(pt))
            pointer_move(hwnd, pt.x, pt.y, settle=0.2)
        elif name == "hover":
            label = value.strip()
            center = None
            for _ in range(3):
                center = control_center(hwnd, label)
                if center is not None:
                    break
                time.sleep(0.3)
            if center is None:
                raise ValueError(f"hover 找不到控件：{label!r}")
            pointer_move(hwnd, center[0], center[1], settle=0.35)
            print(f"[shot] hover {label!r} -> ({center[0]:.0f},{center[1]:.0f})，"
                  f"等待 350ms 让 hover 态出现")
            time.sleep(0.35)
        elif name == "mousedown":
            user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, None)
            time.sleep(0.15)
        elif name == "mouseup":
            user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, None)
            time.sleep(0.2)
        elif name == "clickname":
            label = value.strip()
            if page in ("account", "accounts", "skin") and any(
                    word in label for word in ("切换", "退出")):
                raise ValueError(
                    f"账户页截图禁止点击「{label}」：切换/退出账户会改 settings4.json 的 "
                    f"active 配置；只允许点开子视图，多账户画面请用滚动/翻页")
            origin = POINT(0, 0)
            user32.ClientToScreen(hwnd, ctypes.byref(origin))
            scale = dpi_scale(hwnd) or 1.0
            hit = False
            for _ in range(3):
                focus_window(hwnd)
                time.sleep(0.35)
                points = app_button_points(hwnd, label)
                # 优先内容区，避免同名按钮在侧栏/标题栏抢走点击
                content = [p for p in points
                           if (p[0] - origin.x) / scale > SIDEBAR_WIDTH]
                pts = content or points
                if not pts:
                    time.sleep(0.4)
                    continue
                pts.sort(key=lambda p: (p[1], p[0]))
                click_screen(*pts[0])
                hit = True
                break
            if not hit:
                raise ValueError(f"clickname 找不到按钮：{label!r}")
            time.sleep(0.7)
        elif name == "expect":
            needle = value.strip()
            deadline = time.time() + 8.0
            hit = False
            while time.time() < deadline:
                names = uia_names(hwnd)
                if any(needle in item for item in names):
                    hit = True
                    break
                time.sleep(0.4)
            if not hit:
                raise ValueError(f"expect 等待超时，未出现：{needle!r}")
            print(f"[shot] 动作校验通过：出现 {needle!r}")
        elif name == "wait":
            time.sleep(float(value) / 1000.0)
        elif name == "scroll":
            scroll_by(hwnd, float(value))
            time.sleep(0.4)
        elif name == "shot":
            ok, actual = title_matches(hwnd, page)
            if not ok and not allow_mismatch:
                raise PageMismatch(page, PAGE_HEADERS.get(page), actual)
            grabbed = grab_client(hwnd)
            if not grabbed:
                raise RuntimeError("动作截图失败")
            width, height, rgb = grabbed
            target = out_dir / f"{value or page}_{design_w}x{design_h}.png"
            write_png(target, width, height, rgb)
            write_sidecar(target, {
                "page": page, "expected_title": PAGE_HEADERS.get(page),
                "actual_title": actual, "verified": ok,
                "pixels": f"{width}x{height}",
            })
            print(f"[shot] 动作截图 -> {target.as_posix()} ({width}x{height})，页头={actual!r} {'✓' if ok else '(与 --page 不符，已按 --allow-mismatch 保存)'}")
        elif name == "crop":
            # crop:<输出名>@<UIA 文字>：裁「该文字所在行右侧 160x56（设计）」，
            # 用于 hover 证据（↑↓ 按钮就在行右侧）。
            out_name, _, target_label = value.partition("@")
            out_name, target_label = out_name.strip(), target_label.strip()
            center = control_center(hwnd, target_label) if target_label else None
            if center is None:
                raise ValueError(f"crop 找不到控件：{target_label!r}")
            origin = POINT(0, 0)
            user32.ClientToScreen(hwnd, ctypes.byref(origin))
            scale = dpi_scale(hwnd) or 1.0
            cy = (center[1] - origin.y) / scale
            box_w, box_h = 160.0, 56.0
            x0 = max(0.0, design_w - 220.0)
            y0 = max(0.0, cy - box_h / 2)
            grabbed = grab_client(hwnd)
            if not grabbed:
                raise RuntimeError("裁图失败：抓不到客户区")
            width, height, rgb = grabbed
            px0 = max(0, min(int(round(x0 * scale)), width - 1))
            py0 = max(0, min(int(round(y0 * scale)), height - 1))
            pw = min(int(round(box_w * scale)), width - px0)
            ph = min(int(round(box_h * scale)), height - py0)
            rows = b"".join(
                rgb[((py0 + r) * width + px0) * 3:((py0 + r) * width + px0 + pw) * 3]
                for r in range(ph))
            target = out_dir / f"{out_name or 'crop'}.png"
            write_png(target, pw, ph, rows)
            print(f"[shot] 裁图 -> {target.as_posix()} ({pw}x{ph})，锚点 {target_label!r} 行右侧 160x56（设计）")
        else:
            raise ValueError(f"未知动作：{raw}")


class LockedScreen(RuntimeError):
    """桌面锁屏：截图只会得到 PIN 画面，必须退出 3 让调用方知道是环境问题。"""


class PageMismatch(RuntimeError):
    """截到的页和 --page 对不上。宁可失败，也不给别的 lane 一张贴错名字的图。"""

    def __init__(self, page: str, expected: str | None, actual: str | None):
        super().__init__(f"页面校验失败：期望 {page}({expected})，实际 {actual!r}")
        self.page = page
        self.expected = expected
        self.actual = actual


def write_sidecar(path: Path, payload: dict) -> None:
    """截图旁边的 .json：记实际页头与截取时间，供 lane 复核证据。"""
    payload = dict(payload)
    payload.setdefault("captured_at", time.strftime("%Y-%m-%d %H:%M:%S"))
    try:
        path.with_suffix(".json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    except OSError as exc:
        print(f"[shot] 写 sidecar 失败：{exc}", file=sys.stderr)


def append_log(out_dir: Path, line: str) -> None:
    """shots.log：每次截图留一行，失败原因也落到这里，方便事后追账。"""
    try:
        with (out_dir / "shots.log").open("a", encoding="utf-8") as fh:
            fh.write(time.strftime("[%H:%M:%S] ") + line + "\n")
    except OSError:
        pass


CONTENT_PROBLEMS: list[str] = []


def capture(hwnd, out_dir: Path, page: str, design_w: int, design_h: int,
            allow_mismatch: bool = False, suffix: str = "",
            preverified_title: str | None = None,
            content_ok: bool = True, wait_seconds: float = 0.0) -> Path:
    # 锁屏时抓到的只会是 PIN 画面：不写正式文件，交给 main 统一按 exit 3 处理。
    if screen_locked():
        raise LockedScreen("检测到锁屏（LockApp/LogonUI），已跳过截图")
    # 出图前把窗口置前台，避免 BitBlt 抓到上层别的窗口
    focus_window(hwnd)
    time.sleep(0.3)
    expected = PAGE_HEADERS.get(page)
    tabbed = page in TAB_HOST
    if preverified_title is not None:
        actual = preverified_title
        title_ok = expected is None or actual == expected
    else:
        actual = page_title(hwnd)
        title_ok = expected is None or actual == expected
        content_ok = content_ok and (not tabbed or tab_ready(hwnd, page))
        if expected is not None and not title_ok:
            print(f"[shot] 页面自检失败：期望 {page}({expected}"
                  f"{' + tab 内容' if tabbed else ''})，实际 {actual!r}；"
                  f"重新切页后重试一次", file=sys.stderr)
            append_log(out_dir, f"自检失败 page={page} expected={expected!r} "
                                f"actual={actual!r} -> 重试")
            navigate(hwnd, page)
            time.sleep(0.9)
            actual = page_title(hwnd)
            title_ok = actual == expected
            content_ok = content_ok and (not tabbed or tab_ready(hwnd, page))

    grabbed = grab_client(hwnd)
    if not grabbed:
        raise RuntimeError("抓不到窗口内容（BitBlt/PrintWindow 都失败）")
    width, height, rgb = grabbed
    if is_flat(rgb):
        raise RuntimeError(f"截图是纯色（{width}x{height}），窗口可能没画出来")

    if not title_ok:
        # 页头不对：失败图单独放，绝不占用正式文件名
        target = out_dir / f"{page}_{design_w}x{design_h}{suffix}_MISMATCH.png"
        write_png(target, width, height, rgb)
        write_sidecar(target, {
            "page": page, "expected_title": expected, "actual_title": actual,
            "verified": False, "pixels": f"{width}x{height}",
            "content_ready": False, "wait_seconds": round(wait_seconds, 2),
            "reason": "title-mismatch",
        })
        append_log(out_dir, f"校验失败 page={page} expected={expected!r} "
                            f"actual={actual!r} file={target.name}")
        print(f"[shot] 页面校验失败，调试图 -> {target.as_posix()}（实际页头 {actual!r}）",
              file=sys.stderr)
        if not allow_mismatch:
            raise PageMismatch(page, expected, actual)
        return target

    verified = content_ok
    reason = None if verified else "content-timeout"
    target = out_dir / f"{page}_{design_w}x{design_h}{suffix}.png"
    write_png(target, width, height, rgb)
    write_sidecar(target, {
        "page": page, "expected_title": expected, "actual_title": actual,
        "verified": verified, "pixels": f"{width}x{height}",
        "content_ready": content_ok, "wait_seconds": round(wait_seconds, 2),
        "reason": reason,
    })
    if verified:
        append_log(out_dir, f"OK page={page} title={actual!r} "
                            f"design={design_w}x{design_h} wait={wait_seconds:.1f}s "
                            f"file={target.name}")
        print(f"[shot] {target.as_posix()}  {width}x{height}px（设计 {design_w}x{design_h}），"
              f"页头={actual!r} ✓，内容就绪 ✓（等待 {wait_seconds:.1f}s）")
    else:
        CONTENT_PROBLEMS.append(f"{page}_{design_w}x{design_h}{suffix}")
        append_log(out_dir, f"内容超时 page={page} title={actual!r} "
                            f"design={design_w}x{design_h} reason=content-timeout "
                            f"file={target.name}")
        print(f"[shot] {target.as_posix()}  已保存但内容未就绪（等待 {wait_seconds:.1f}s，"
              f"verified=false reason=content-timeout）", file=sys.stderr)
    return target


def main(argv: list[str] | None = None) -> int:
    CONTENT_PROBLEMS.clear()
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(line_buffering=True)
        except Exception:
            pass
    parser = argparse.ArgumentParser(description="MeowLauncher.WinUI 真机截图")
    parser.add_argument("--page", default="home",
                        help="home/versions/download/mods/online/assets/logs/settings/tasks/account/skin")
    parser.add_argument("--sizes", default=",".join(f"{a}x{b}" for a, b in SIZES),
                        help="设计尺寸，逗号分隔（默认 1440x900,1120x760；980x760 属超范围体检）")
    parser.add_argument("--exe", default=str(DEFAULT_EXE))
    parser.add_argument("--out", default=str(OUT_DIR))
    parser.add_argument("--actions", default="",
                        help='动作串，如 "hover:1.13.2;wait:400;shot:versions_hover"')
    parser.add_argument("--nav-y", type=int, default=None, help="固定坐标兜底：UIA 找不到/点不中侧栏项时用的 y（设计像素；仅排障用）")
    parser.add_argument("--scroll", type=float, default=0.0,
                        help="基础图出完后向下滚 N 设计像素（负=向上），另存 *_s2.png")
    parser.add_argument("--to", default="",
                        help="滚到包含该文字的控件可见再截 *_s2.png（如「关于」）")
    parser.add_argument("--timeout", type=float, default=60.0, help="等窗口超时秒数")
    parser.add_argument("--ready-timeout", type=float, default=15.0,
                        help="等内容加载完的超时秒数（超时写 verified=false reason=content-timeout）")
    parser.add_argument("--allow-mismatch", action="store_true",
                        help="页头对不上也保存（只写 *_MISMATCH.png），默认拒绝")
    parser.add_argument("--dump-nav", action="store_true",
                        help="打印应用所有窗口的 Button（名称+矩形）与九页 UIA 导航解析结果后退出")
    parser.add_argument("--check-nav", action="store_true",
                        help="只做导航自检：逐页点击并校验页头，输出 NAV <页>=PASS/FAIL，任一失败 exit 1")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    try:
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass

    exe = Path(args.exe)
    if not exe.exists():
        print(f"[shot] 找不到 exe：{exe}", file=sys.stderr)
        return 1
    sizes = []
    for item in args.sizes.split(","):
        w_s, h_s = item.strip().lower().split("x")
        sizes.append((int(w_s), int(h_s)))
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    # 锁屏是环境问题，不是页面问题：直接 exit 3，绝不产出 PIN 画面当证据。
    if screen_locked():
        print("[shot] 锁屏中（LockApp/LogonUI），不截图；解锁后重跑。", file=sys.stderr)
        append_log(out_dir, f"LOCKED page={args.page} reason=locked-screen")
        if args.json:
            print(json.dumps({"page": args.page, "verified": False,
                              "reason": "locked-screen",
                              "captured_at": time.strftime("%Y-%m-%d %H:%M:%S")},
                             ensure_ascii=False, indent=2))
        return 3

    # App 关闭时 RememberWindowSize 会写 app.json，账户页动作还可能改 settings4.json
    # 的 active。meowlock shot 会在锁内统一保护（MEOWLOCK_PREFS_GUARD=1）；
    # 直接调用时也只还原受保护键，绝不整文件回滚，避免把并发 lane 的配置覆盖掉。
    prefs = prefs_path()
    guarded = os.environ.get("MEOWLOCK_PREFS_GUARD") == "1"
    prefs_backup = None if guarded else (snapshot_prefs(prefs) if snapshot_prefs else None)
    if guarded:
        print("[shot] app.json 由 meowlock 统一保护，本次不做整文件备份/还原")
    proc = subprocess.Popen([str(exe)], cwd=str(exe.parent))
    hwnd = None
    results: list[dict] = []
    try:
        hwnd = find_main_window(proc.pid, args.timeout)
        if not hwnd:
            print("[shot] 等不到主窗口（见 _run.log？）", file=sys.stderr)
            return 1
        print(f"[shot] 窗口 0x{hwnd:X} pid={proc.pid} title={window_title(hwnd)!r} dpi={dpi_scale(hwnd)}x")
        focus_window(hwnd)
        # 首帧 + 首页数据加载
        time.sleep(1.8)

        # 先把窗口摆到首个目标尺寸再切页：UIA 坐标虽是实时的，但窗口被记成
        # 1440x1400 之类的尺寸时页头/骨架布局会变，统一尺寸后结果才可复现。
        first_w, first_h = sizes[0]
        actual = force_client_size(hwnd, first_w, first_h)
        focus_window(hwnd)
        time.sleep(0.9)
        if actual != (round(first_w * dpi_scale(hwnd)), round(first_h * dpi_scale(hwnd))):
            print(f"[shot] 警告：客户区 {actual} 没到目标设计尺寸 {first_w}x{first_h}")

        if args.check_nav:
            ok = True
            for pg in CHECK_PAGES:
                good = navigate(hwnd, pg)
                print(f"[shot] NAV {pg}={'PASS' if good else 'FAIL'}")
                ok = ok and good
            print(f"[shot] NAV 总览：{'全绿' if ok else '有失败'}")
            return 0 if ok else 1

        if args.dump_nav:
            for win in app_windows(hwnd):
                print(f"[shot] 窗口 0x{win:X} title={window_title(win)!r}")
                for name, left, top, right, bottom in uia_buttons(win):
                    print(f"[shot]   Button {name!r} rect=({left},{top},{right},{bottom})")
            print("[shot] —— 导航项 UIA 解析 ——")
            for pg in CHECK_PAGES:
                center = nav_item_center(hwnd, pg)
                print(f"[shot] NAV {pg:10s} label={NAV_LABELS.get(pg)!r:12s} "
                      f"center={center} {'OK' if center else 'MISSING'}")
            return 0

        if args.page not in PAGE_HEADERS:
            print(f"[shot] 警告：未知页面 {args.page}，无法做页头自检", file=sys.stderr)
        if not navigate(hwnd, args.page, args.nav_y):
            return 1
        # 页头对只是第一步：等页面内容真的加载完（骨架/转圈/空列表都算没就绪）。
        ready, initial_wait = wait_page_ready(hwnd, args.page, timeout=args.ready_timeout)
        print(f"[shot] {args.page} 内容就绪 {'✓' if ready else '✗'}（等待 {initial_wait:.1f}s）")
        time.sleep(0.6)

        if args.actions:
            run_actions(hwnd, args.actions, out_dir, args.page, first_w, first_h,
                        allow_mismatch=args.allow_mismatch)

        first_shot = True
        for design_w, design_h in sizes:
            actual = force_client_size(hwnd, design_w, design_h)
            focus_window(hwnd)
            time.sleep(0.9)
            ready, waited = wait_page_ready(hwnd, args.page, timeout=args.ready_timeout)
            total_wait = initial_wait + waited if first_shot else waited
            first_shot = False
            path = capture(hwnd, out_dir, args.page, design_w, design_h,
                           allow_mismatch=args.allow_mismatch,
                           content_ok=ready, wait_seconds=total_wait)
            results.append({
                "page": args.page,
                "design": f"{design_w}x{design_h}",
                "file": path.as_posix(),
                "pixels": f"{actual[0]}x{actual[1]}",
                "title": page_title(hwnd),
                "content_ready": ready,
                "wait_seconds": round(total_wait, 2),
            })

        if args.scroll or args.to:
            design_w, design_h = sizes[0]
            actual = force_client_size(hwnd, design_w, design_h)
            focus_window(hwnd)
            time.sleep(0.5)
            # 先回顶再向下定位，避免受残留滚动位置影响
            scroll_by(hwnd, -4000)
            time.sleep(0.4)
            title_now = page_title(hwnd)
            if args.to:
                if not scroll_to(hwnd, args.to):
                    print(f"[shot] 滚动后仍找不到 {args.to!r}", file=sys.stderr)
                    return 1
            else:
                scroll_by(hwnd, args.scroll)
                time.sleep(0.5)
            path = capture(hwnd, out_dir, args.page, design_w, design_h,
                           allow_mismatch=args.allow_mismatch, suffix="_s2",
                           preverified_title=title_now)
            results.append({
                "page": args.page,
                "design": f"{design_w}x{design_h}",
                "file": path.as_posix(),
                "pixels": f"{actual[0]}x{actual[1]}",
                "title": title_now,
                "suffix": "_s2",
            })
    except LockedScreen as exc:
        print(f"[shot] {exc}；未写正式截图。", file=sys.stderr)
        append_log(out_dir, f"LOCKED page={args.page} reason=locked-screen")
        if args.json:
            print(json.dumps({"page": args.page, "verified": False,
                              "reason": "locked-screen",
                              "captured_at": time.strftime("%Y-%m-%d %H:%M:%S")},
                             ensure_ascii=False, indent=2))
        return 3
    except PageMismatch as exc:
        print(f"[shot] {exc}", file=sys.stderr)
        return 1
    finally:
        if hwnd:
            user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        try:
            proc.wait(timeout=6)
        except subprocess.TimeoutExpired:
            proc.terminate()
            try:
                proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                proc.kill()
        # 只还原窗口尺寸键；McDir/McDirs 等配置键永远保持当前值。
        if not guarded and prefs_backup and restore_prefs:
            restore_prefs(prefs, prefs_backup)

    if args.json:
        print(json.dumps(results, ensure_ascii=False, indent=2))
    if CONTENT_PROBLEMS:
        print(f"[shot] 有 {len(CONTENT_PROBLEMS)} 张内容未就绪：{', '.join(CONTENT_PROBLEMS)}",
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
