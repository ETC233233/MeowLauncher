#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MeowLauncher.WinUI 红线守卫（Python 3.15，只用标准库）。

扫描 `MeowLauncher.WinUI/**/*.xaml|.cs`（跳过 obj/ bin/），输出红线清单；
有红线时退出码 1，干净时 0，自检/用法错误 2。

八条红线（与内部文档「设计契约」对齐）：

  R1 卡片自己开 AcrylicBrush：Theme 里只允许 AppShellBrush 用 Acrylic;
     Views/Controls 里任何 AcrylicBrush 都算红线。
  R2 RootVeil 默认不透明度 > 0.12：判「默认呈现」——
     MainWindow.xaml 里 RootVeil 的初始 Background、AppPreferences 的默认
     VeilOpacity、MainWindow.cs 里强制下限 Math.Max(veil, x)。用户把设置里的
     滑杆拖高属于运行时偏好，不判。
  R3 任何页面出现「保存」/「保存并应用」按钮（XAML 按钮或 C# 新建按钮）。
  R4 设置页出现路径陈列：同一张卡片里同时有路径文案与「打开」按钮。
  R5 FontSize 不在令牌表：令牌 = 界面字号规范（内部文档） +
     Themes/*.xaml 里 FontSize* 的 x:Double + 契约补充 44/15/13/11.5。
  R6 列表页（含 ListView/GridView/ItemsRepeater）缺 EmptyState。
  R7 交互按钮缺 AutomationProperties.Name（XAML 与 C# 动态按钮都查）。
  R8 卡片圆角或内边距出现裸数字（卡片 = Border 带 WidgetCardStyle /
     AppCardBrush / PanelSurfaceBrush；0 不算）。

  Advisory A1–A5（契约 10「反 AI 味」机扫项，只报告、不参与 exit 1）：
   A1 渐变用在文字前景（TextBlock/Run/Span/Hyperlink 内嵌 LinearGradientBrush）；
   A2 FontIcon 的 Glyph 是 emoji / 非 Segoe Fluent 码位（非 ASCII 字符，&#x..; 实体除外）；
   A3 同页多处 EntranceThemeTransition（全站同一种入场动画嫌疑，按页报次数）；
   A4 硬编码空数据占位（Text/Content 字面量 "0%" / "+0"）；
   A5 卡片内嵌卡片（卡片 Border 嵌套深度 > 1）。

用法：
  python tools/guard.py                 # 扫 mclauncher 根，列出红线
  python tools/guard.py --count         # 只打印数量
  python tools/guard.py --json          # 机器可读
  python tools/guard.py --report guard-baseline.md   # 写基线 md
  python tools/guard.py --selftest      # 用临时夹具验证八条规则都能触发
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
DEFAULT_ROOT = TOOLS_DIR.parent          # mclauncher/
WINUI_DIR = "MeowLauncher.WinUI"
SKIP_PARTS = {"obj", "bin", "__pycache__", ".git"}

RULE_NAMES = {
    "R1": "卡片自开 AcrylicBrush",
    "R2": "RootVeil 默认不透明度 > 0.12",
    "R3": "页面出现「保存」按钮",
    "R4": "设置页路径陈列卡",
    "R5": "FontSize 不在令牌表",
    "R6": "列表页缺 EmptyState",
    "R7": "交互按钮缺 AutomationProperties.Name",
    "R8": "卡片圆角/内边距裸数字",
}

# 契约 10「反 AI 味」advisory 机扫项：只报告、不参与 exit 1。
ADVISORY_NAMES = {
    "A1": "渐变用在文字前景（LinearGradientBrush 填 TextBlock/Run/Span/Hyperlink）",
    "A2": "FontIcon Glyph 是 emoji / 非 Segoe Fluent 码位",
    "A3": "同页多处 EntranceThemeTransition（全站同一种入场动画嫌疑）",
    "A4": "硬编码空数据占位（\"0%\" / \"+0\"）",
    "A5": "卡片内嵌卡片（嵌套深度 > 1）",
}

CARD_MARKERS = ("WidgetCardStyle", "AppCardBrush", "PanelSurfaceBrush")
PLACEHOLDER_LITERALS = ("0%", "+0")

# 契约补充的字号（T1 设计契约：display 44 / cardTitle 15 / body 13 / caption 11.5）。
CONTRACT_FONT_SIZES = {44.0, 15.0, 13.0, 11.5}

LIST_CONTROL_RE = re.compile(r"<(ListView|ListViewBase|GridView|ItemsRepeater)\b")
BUTTON_ELEMS = (
    "Button",
    "ToggleButton",
    "AppBarButton",
    "AppBarToggleButton",
    "HyperlinkButton",
    "RepeatButton",
    "SplitButton",
    "DropDownButton",
)
BUTTON_TAG_RE = re.compile(
    r"<(" + "|".join(BUTTON_ELEMS) + r")\b[^>]*?(?:/>|>.*?</\1>)", re.S
)
PATH_KEYWORDS = ("游戏目录", "配置路径", "日志路径", "日志目录", "实例目录", "安装目录")


@dataclass
class Violation:
    rule: str
    path: str
    line: int
    message: str

    def sort_key(self):
        order = list(RULE_NAMES).index(self.rule)
        return (order, self.path, self.line)


@dataclass
class Advisory:
    rule: str
    path: str
    line: int
    message: str

    def sort_key(self):
        order = list(ADVISORY_NAMES).index(self.rule)
        return (order, self.path, self.line)


# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #
def relpath(path: Path, root: Path) -> str:
    """一律输出正斜杠相对路径。"""
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def line_of(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def comment_spans(text: str) -> list[tuple[int, int]]:
    return [(m.start(), m.end()) for m in re.finditer(r"<!--.*?-->", text, re.S)]


def in_comment(spans: list[tuple[int, int]], offset: int) -> bool:
    return any(a <= offset < b for a, b in spans)


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def iter_source_files(root: Path):
    base = root / WINUI_DIR
    if not base.exists():
        return
    for path in sorted(base.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in {".xaml", ".cs"}:
            continue
        if any(part in SKIP_PARTS for part in path.relative_to(base).parts):
            continue
        yield path


def load_font_tokens(root: Path) -> tuple[set[float], list[str]]:
    """令牌表：design-system.md 字号表 + Themes/*.xaml 的 FontSize* 资源 + 契约补充。"""
    values: set[float] = set(CONTRACT_FONT_SIZES)
    sources = [f"契约补充 {sorted(CONTRACT_FONT_SIZES)}"]

    ds = root / "docs" / "design-system.md"
    if ds.exists():
        found = set()
        for line in read_text(ds).splitlines():
            m = re.search(r"`?Theme\.fs[A-Za-z0-9]+`?\s*\|\s*([0-9]+(?:\.[0-9]+)?)", line)
            if m:
                found.add(float(m.group(1)))
        if found:
            values |= found
            sources.append(f"design-system.md {sorted(found)}")

    theme_found = set()
    for theme in sorted((root / WINUI_DIR / "Themes").glob("*.xaml")):
        text = read_text(theme)
        for m in re.finditer(
            r'x:Double\s+x:Key="FontSize[A-Za-z0-9]*"\s*>\s*([0-9]+(?:\.[0-9]+)?)\s*<',
            text,
        ):
            theme_found.add(float(m.group(1)))
    if theme_found:
        values |= theme_found
        sources.append(f"Themes FontSize* {sorted(theme_found)}")

    return values, sources


def fmt_num(value: float) -> str:
    return f"{value:g}"


def tag_attr(tag: str, name: str) -> str | None:
    m = re.search(r'\b' + re.escape(name) + r'="([^"]*)"', tag)
    return m.group(1) if m else None


def numeric_tuple(value: str | None) -> list[float] | None:
    """'18' / '18,14' / '18, 14, 18, 14' -> floats；含非数字返回 None。"""
    if value is None:
        return None
    parts = [p.strip() for p in value.split(",")]
    if not parts or any(not p for p in parts):
        return None
    out = []
    for part in parts:
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", part):
            return None
        out.append(float(part))
    return out


# --------------------------------------------------------------------------- #
# rules
# --------------------------------------------------------------------------- #
def check_r1_acrylic(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    tag_re = re.compile(r"<AcrylicBrush\b[^>]*>", re.S)
    for path, text in files:
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in tag_re.finditer(text):
                if in_comment(spans, m.start()):
                    continue
                tag = m.group(0)
                key = tag_attr(tag, "x:Key") or ""
                if "Shell" in key:
                    continue  # AppShellBrush 是整窗玻璃那一层，允许
                out.append(
                    Violation(
                        "R1",
                        relpath(path, root),
                        line_of(text, m.start()),
                        f"卡片自开 AcrylicBrush（key={key or '无 key'}），只允许 AppShellBrush",
                    )
                )
        else:
            for m in re.finditer(r"AcrylicBrush", text):
                prev_line_start = text.rfind("\n", 0, m.start()) + 1
                line_text = text[prev_line_start : text.find("\n", m.start())]
                if "Shell" in line_text:
                    continue
                out.append(
                    Violation(
                        "R1",
                        relpath(path, root),
                        line_of(text, m.start()),
                        "代码里新建/引用 AcrylicBrush，只允许主题里的 AppShellBrush",
                    )
                )
    return out


def check_r2_rootveil(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    limit = 0.12
    for path, text in files:
        name = relpath(path, root)
        if path.suffix.lower() == ".xaml":
            for m in re.finditer(r"<[A-Za-z][^>]*x:Name=\"RootVeil\"[^>]*>", text, re.S):
                tag = m.group(0)
                bg = tag_attr(tag, "Background") or ""
                cm = re.fullmatch(r"#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})", bg.strip())
                if not cm:
                    continue
                hexpart = cm.group(1)
                alpha = int(hexpart[:2], 16) / 255.0 if len(hexpart) == 8 else 1.0
                if alpha > limit:
                    out.append(
                        Violation(
                            "R2",
                            name,
                            line_of(text, m.start()),
                            f"RootVeil 初始 Background={bg}（不透明度 {alpha:.3f} > {limit}）",
                        )
                    )
        else:
            for m in re.finditer(
                r"VeilOpacity\s*\{\s*get;\s*set;\s*\}\s*=\s*([0-9]+(?:\.[0-9]+)?)", text
            ):
                value = float(m.group(1))
                if value > limit:
                    out.append(
                        Violation(
                            "R2",
                            name,
                            line_of(text, m.start()),
                            f"VeilOpacity 默认值 {value:g} > {limit}（启动即生效）",
                        )
                    )
            for m in re.finditer(r"Math\.Max\(\s*veil\s*,\s*([0-9]+(?:\.[0-9]+)?)\s*\)", text):
                value = float(m.group(1))
                if value > limit:
                    out.append(
                        Violation(
                            "R2",
                            name,
                            line_of(text, m.start()),
                            f"背景图模式强制 RootVeil 下限 {value:g} > {limit}",
                        )
                    )
            for m in re.finditer(r"RootVeil\.Opacity\s*=\s*([0-9]+(?:\.[0-9]+)?)", text):
                value = float(m.group(1))
                if value > limit:
                    out.append(
                        Violation(
                            "R2",
                            name,
                            line_of(text, m.start()),
                            f"RootVeil.Opacity={value:g} > {limit}",
                        )
                    )
    return out


def check_r3_save_button(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    for path, text in files:
        name = relpath(path, root)
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in BUTTON_TAG_RE.finditer(text):
                if in_comment(spans, m.start()):
                    continue
                chunk = m.group(0)
                if "保存" in chunk:
                    snippet = re.sub(r"\s+", " ", chunk)[:90]
                    out.append(
                        Violation(
                            "R3",
                            name,
                            line_of(text, m.start()),
                            f"出现保存按钮：{snippet}",
                        )
                    )
        else:
            for m in re.finditer(r'Content\s*=\s*"[^"]*保存[^"]*"', text):
                out.append(
                    Violation(
                        "R3",
                        name,
                        line_of(text, m.start()),
                        f"代码新建保存按钮：{m.group(0)[:80]}",
                    )
                )
    return out


def border_blocks(text: str):
    """产出 (start, end, tag) —— 顶层到内层的 Border 开标签及配对范围。"""
    token_re = re.compile(r"<(/?)(Border)\b[^>]*?(/?)>", re.S)
    stack: list[tuple[int, str]] = []
    for m in token_re.finditer(text):
        closing, _, selfclosing = m.group(0)[1] == "/", m.group(1), m.group(3)
        if closing:
            if stack:
                start, tag = stack.pop()
                yield start, m.end(), tag
        elif selfclosing:
            yield m.start(), m.end(), m.group(0)
        else:
            stack.append((m.start(), m.group(0)))
    for start, tag in stack:  # 未闭合也给出
        yield start, len(text), tag


def check_r4_settings_path_card(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    for path, text in files:
        if path.suffix.lower() != ".xaml" or path.name != "SettingsPage.xaml":
            continue
        name = relpath(path, root)
        spans = comment_spans(text)
        for start, end, tag in border_blocks(text):
            if in_comment(spans, start):
                continue
            if not any(k in tag for k in ("WidgetCardStyle", "AppCardBrush", "PanelSurfaceBrush")):
                continue
            block = text[start:end]
            hit = [k for k in PATH_KEYWORDS if k in block]
            if not hit:
                continue
            has_open_button = bool(
                re.search(r"<(Button|HyperlinkButton)\b[^>]*>", block)
                and "打开" in block
            )
            if has_open_button:
                out.append(
                    Violation(
                        "R4",
                        name,
                        line_of(text, start),
                        "设置页卡片同时陈列路径（" + "/".join(hit) + "）与「打开」按钮",
                    )
                )
    return out


def check_r5_fontsize(
    files: list[tuple[Path, str]], root: Path, allowed: set[float]
) -> list[Violation]:
    out = []
    for path, text in files:
        name = relpath(path, root)
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in re.finditer(r'FontSize\s*=\s*"([^"]*)"', text):
                if in_comment(spans, m.start()):
                    continue
                raw = m.group(1).strip()
                if "{" in raw:
                    continue  # 走 StaticResource/ThemeResource 令牌
                if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", raw):
                    continue
                value = float(raw)
                if value not in allowed:
                    out.append(
                        Violation(
                            "R5",
                            name,
                            line_of(text, m.start()),
                            f'FontSize="{raw}" 不在令牌表',
                        )
                    )
        else:
            for m in re.finditer(r"FontSize\s*=\s*([0-9]+(?:\.[0-9]+)?)", text):
                value = float(m.group(1))
                if value not in allowed:
                    out.append(
                        Violation(
                            "R5",
                            name,
                            line_of(text, m.start()),
                            f"代码里 FontSize={m.group(1)} 不在令牌表",
                        )
                    )
    return out


def check_r6_empty_state(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    for path, text in files:
        if path.suffix.lower() != ".xaml":
            continue
        if path.parent.name != "Views":
            continue  # 只看「列表页」；MainWindow 的搜索下拉不算页面
        m = LIST_CONTROL_RE.search(text)
        if not m:
            continue
        if "EmptyState" in text:
            continue
        out.append(
            Violation(
                "R6",
                relpath(path, root),
                line_of(text, m.start()),
                f"列表页有 <{m.group(1)}> 却没有 EmptyState",
            )
        )
    return out


def check_r7_automation(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    for path, text in files:
        name = relpath(path, root)
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in BUTTON_TAG_RE.finditer(text):
                if in_comment(spans, m.start()):
                    continue
                tag = m.group(0)
                head = tag[: tag.find(">") + 1]
                if "AutomationProperties.Name=" in head:
                    continue
                label = tag_attr(head, "x:Name") or tag_attr(head, "Content") or tag_attr(head, "Text") or ""
                out.append(
                    Violation(
                        "R7",
                        name,
                        line_of(text, m.start()),
                        f"<{m.group(1)}> 缺 AutomationProperties.Name（{label or '无名字'}）",
                    )
                )
        else:
            for m in re.finditer(r"new\s+(?:" + "|".join(BUTTON_ELEMS) + r")\b", text):
                window = text[m.start() : m.start() + 700]
                if "AutomationProperties" in window or "SetName(" in window:
                    continue
                out.append(
                    Violation(
                        "R7",
                        name,
                        line_of(text, m.start()),
                        "代码动态创建的按钮缺 AutomationProperties.Name",
                    )
                )
    return out


def check_r8_card_numbers(files: list[tuple[Path, str]], root: Path) -> list[Violation]:
    out = []
    tag_re = re.compile(r"<Border\b[^>]*>", re.S)
    for path, text in files:
        name = relpath(path, root)
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in tag_re.finditer(text):
                if in_comment(spans, m.start()):
                    continue
                tag = m.group(0)
                if not any(k in tag for k in ("WidgetCardStyle", "AppCardBrush", "PanelSurfaceBrush")):
                    continue
                problems = []
                for attr in ("CornerRadius", "Padding"):
                    values = numeric_tuple(tag_attr(tag, attr))
                    if values and any(v > 0 for v in values):
                        problems.append(f'{attr}="{tag_attr(tag, attr)}"')
                if problems:
                    out.append(
                        Violation(
                            "R8",
                            name,
                            line_of(text, m.start()),
                            "卡片裸数字：" + " ".join(problems) + "（应走 CardCornerRadius/CardPadding）",
                        )
                    )
        else:
            for m in re.finditer(r"CornerRadius\s*=\s*new\s+CornerRadius\(\s*([0-9.]+)", text):
                value = float(m.group(1))
                if value <= 0:
                    continue
                window = text[max(0, m.start() - 400) : m.start() + 400]
                if "Card" not in window and "AppCardBrush" not in window:
                    continue
                out.append(
                    Violation(
                        "R8",
                        name,
                        line_of(text, m.start()),
                        f"代码创建卡片时圆角裸数字 {m.group(1)}",
                    )
                )
    return out


# --------------------------------------------------------------------------- #
# advisory A1–A5（契约 10「反 AI 味」，只报告不参与 exit 1）
# --------------------------------------------------------------------------- #
def check_a1_gradient_text(files: list[tuple[Path, str]], root: Path) -> list[Advisory]:
    """文字元素内嵌 LinearGradientBrush（渐变文字 / 大数字渐变填充）。"""
    out: list[Advisory] = []
    text_tag_re = re.compile(r"<(TextBlock|Run|Span|Hyperlink)\b(?![.\w])([^>]*)>")
    for path, text in files:
        if path.suffix.lower() != ".xaml":
            continue
        name = relpath(path, root)
        spans = comment_spans(text)
        for m in text_tag_re.finditer(text):
            if in_comment(spans, m.start()):
                continue
            if "/>" in m.group(0):
                continue  # 自闭合无子内容
            close = re.search(r"</" + m.group(1) + r"\s*>", text[m.end():])
            block = text[m.end(): m.end() + (close.start() if close else 4000)]
            if re.search(r"<LinearGradientBrush\b", block):
                out.append(
                    Advisory(
                        "A1",
                        name,
                        line_of(text, m.start()),
                        f"<{m.group(1)}> 内嵌 LinearGradientBrush（渐变文字/渐变数字填充）",
                    )
                )
    return out


def check_a2_fonticon_glyph(files: list[tuple[Path, str]], root: Path) -> list[Advisory]:
    """FontIcon Glyph 为 emoji / 非 Segoe Fluent 码位（&#x..; 实体除外）。"""
    out: list[Advisory] = []
    for path, text in files:
        if path.suffix.lower() != ".xaml":
            continue
        name = relpath(path, root)
        spans = comment_spans(text)
        for m in re.finditer(r'<FontIcon\b[^>]*Glyph="([^"]*)"', text):
            if in_comment(spans, m.start()):
                continue
            glyph = re.sub(r"&#x[0-9A-Fa-f]+;", "", m.group(1))
            if not glyph.isascii():
                out.append(
                    Advisory(
                        "A2",
                        name,
                        line_of(text, m.start()),
                        f'FontIcon Glyph="{m.group(1)[:24]}" 含非 ASCII 字符（emoji？应换 Segoe Fluent Glyph）',
                    )
                )
    return out


def check_a3_entrance_transitions(files: list[tuple[Path, str]], root: Path) -> list[Advisory]:
    """每页统计 EntranceThemeTransition 出现次数（全站同一种入场动画嫌疑）。"""
    out: list[Advisory] = []
    for path, text in files:
        if path.suffix.lower() != ".xaml":
            continue
        name = relpath(path, root)
        spans = comment_spans(text)
        hits = [
            m for m in re.finditer(r"<EntranceThemeTransition\b", text)
            if not in_comment(spans, m.start())
        ]
        if not hits:
            continue
        line_list = ", ".join(str(line_of(text, h.start())) for h in hits)
        out.append(
            Advisory(
                "A3",
                name,
                line_of(text, hits[0].start()),
                f"本页 EntranceThemeTransition ×{len(hits)}（行 {line_list}）——契约 10：禁止全站同一种入场动画",
            )
        )
    return out


def check_a4_placeholder_data(files: list[tuple[Path, str]], root: Path) -> list[Advisory]:
    """硬编码空数据占位（"0%" / "+0"）。"""
    out: list[Advisory] = []
    for path, text in files:
        name = relpath(path, root)
        spans = comment_spans(text)
        if path.suffix.lower() == ".xaml":
            for m in re.finditer(r'\b(Text|Content)="([^"]*)"', text):
                if in_comment(spans, m.start()):
                    continue
                if m.group(2) in PLACEHOLDER_LITERALS:
                    out.append(
                        Advisory(
                            "A4",
                            name,
                            line_of(text, m.start()),
                            f'{m.group(1)}="{m.group(2)}" 硬编码空数据占位（要么真数据要么不显示）',
                        )
                    )
        elif path.suffix.lower() in (".cs", ".xaml.cs"):
            for m in re.finditer(r'=\s*"(0%|\+0)"', text):
                if in_comment(spans, m.start()):
                    continue
                out.append(
                    Advisory(
                        "A4",
                        name,
                        line_of(text, m.start()),
                        f'代码硬编码空数据占位 "{m.group(1)}"',
                    )
                )
    return out


def check_a5_nested_cards(files: list[tuple[Path, str]], root: Path) -> list[Advisory]:
    """卡片 Border 嵌套另一张卡片（深度 > 1）。"""
    out: list[Advisory] = []
    token_re = re.compile(r"<(/?)(Border)\b[^>]*?(/?)>", re.S)
    for path, text in files:
        if path.suffix.lower() != ".xaml":
            continue
        name = relpath(path, root)
        spans = comment_spans(text)
        stack: list[tuple[int, bool]] = []  # (line, is_card)
        for m in token_re.finditer(text):
            if in_comment(spans, m.start()):
                continue
            closing = m.group(1) == "/"
            self_closing = m.group(3) == "/"
            if closing:
                if stack:
                    stack.pop()
                continue
            is_card = any(k in m.group(0) for k in CARD_MARKERS)
            if self_closing:
                continue  # 自闭合卡不嵌套任何内容
            if is_card and any(card for _, card in stack):
                outer_line = next(line for line, card in stack if card)
                out.append(
                    Advisory(
                        "A5",
                        name,
                        line_of(text, m.start()),
                        f"卡片内嵌卡片：外层卡片在行 {outer_line}（嵌套深度 > 1）",
                    )
                )
            stack.append((line_of(text, m.start()), is_card))
    return out


def advisory_checks() -> list[tuple[str, object]]:
    return [
        ("A1", check_a1_gradient_text),
        ("A2", check_a2_fonticon_glyph),
        ("A3", check_a3_entrance_transitions),
        ("A4", check_a4_placeholder_data),
        ("A5", check_a5_nested_cards),
    ]


# --------------------------------------------------------------------------- #
# scan / report
# --------------------------------------------------------------------------- #
def scan(root: Path) -> tuple[list[Violation], list[Advisory], list[str], set[float]]:
    files = [(path, read_text(path)) for path in iter_source_files(root)]
    allowed, token_sources = load_font_tokens(root)
    violations: list[Violation] = []
    violations += check_r1_acrylic(files, root)
    violations += check_r2_rootveil(files, root)
    violations += check_r3_save_button(files, root)
    violations += check_r4_settings_path_card(files, root)
    violations += check_r5_fontsize(files, root, allowed)
    violations += check_r6_empty_state(files, root)
    violations += check_r7_automation(files, root)
    violations += check_r8_card_numbers(files, root)
    violations.sort(key=Violation.sort_key)
    advisories: list[Advisory] = []
    for _rule, check in advisory_checks():
        advisories += check(files, root)
    advisories.sort(key=Advisory.sort_key)
    return violations, advisories, token_sources, allowed


def counts_by_rule(violations: list[Violation]) -> dict[str, int]:
    counts = {rule: 0 for rule in RULE_NAMES}
    for item in violations:
        counts[item.rule] = counts.get(item.rule, 0) + 1
    return counts


def render_console(
    root: Path,
    violations: list[Violation],
    advisories: list[Advisory],
    token_sources: list[str],
) -> str:
    counts = counts_by_rule(violations)
    lines = [
        f"MeowLauncher.WinUI 红线守卫  根目录: {root.as_posix()}",
        "字号令牌: " + " / ".join(token_sources),
        "",
    ]
    if not violations:
        lines.append("红线 0 条 —— 通过。")
    else:
        for rule, rule_name in RULE_NAMES.items():
            rule_items = [v for v in violations if v.rule == rule]
            if not rule_items:
                continue
            lines.append(f"[{rule}] {rule_name}  ×{len(rule_items)}")
            for item in rule_items:
                lines.append(f"  {item.path}:{item.line}  {item.message}")
            lines.append("")
        lines.append(f"红线总计 {len(violations)} 条：" + " ".join(
            f"{rule}={counts[rule]}" for rule in RULE_NAMES if counts[rule]
        ))
    if advisories:
        lines.append("")
        lines.append("advisory（只报告，不参与 exit 1）：")
        for rule, rule_name in ADVISORY_NAMES.items():
            rule_items = [a for a in advisories if a.rule == rule]
            if not rule_items:
                continue
            lines.append(f"  [{rule}] {rule_name}  ×{len(rule_items)}")
            for item in rule_items:
                lines.append(f"    {item.path}:{item.line}  {item.message}")
        lines.append(f"advisory 总计 {len(advisories)} 条。")
    return "\n".join(lines)


def render_baseline_md(
    root: Path,
    violations: list[Violation],
    advisories: list[Advisory],
    token_sources: list[str],
    allowed: set[float],
    command: str,
) -> str:
    counts = counts_by_rule(violations)
    adv_counts = counts_by_rule(advisories)
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    lines = [
        "# 红线基线（改造前）",
        "",
        f"- 生成时间：{now}",
        f"- 生成命令：`{command}`",
        f"- 扫描根：`mclauncher/`（`MeowLauncher.WinUI/**/*.xaml|.cs`，跳过 obj/bin）",
        f"- 字号令牌表：{', '.join(fmt_num(v) for v in sorted(allowed))}",
        f"- 令牌来源：{'；'.join(token_sources)}",
        "",
        "## 总计",
        "",
        f"**改造前红线 {len(violations)} 条。**",
        "",
        "| 规则 | 含义 | 条数 |",
        "| --- | --- | ---: |",
    ]
    for rule, rule_name in RULE_NAMES.items():
        lines.append(f"| {rule} | {rule_name} | {counts.get(rule, 0)} |")
    lines += [
        "",
        "## 判定口径（设计决策）",
        "",
        "- **R2** 只判默认呈现：`MainWindow.xaml` 里 RootVeil 的初始 Background、",
        "  `AppPreferences.VeilOpacity` 默认值、`MainWindow.cs` 强制下限。",
        "  设置页滑杆允许用户把遮罩拖高属于运行时偏好，不判（否则每页都要为用户选择负责）。",
        "- **R5** 令牌 = 界面字号规范（内部文档） + `Themes/*.xaml` 的 `FontSize*` + ",
        "  契约补充（44/15/13/11.5）。T3 往 AppTheme 补令牌后守卫自动认可，无需改脚本。",
        "- **R8** 卡片 = Border 带 `WidgetCardStyle` / `AppCardBrush` / `PanelSurfaceBrush`；",
        "   `Padding=\"0\"` 不算（0 不是设计决定）。",
        "- **R7** 包含 XAML 与 C# 动态创建的按钮；导航 ToggleButton 同样要求可访问名。",
        "",
        "## 红线清单",
        "",
    ]
    for rule, rule_name in RULE_NAMES.items():
        rule_items = [v for v in violations if v.rule == rule]
        lines.append(f"### {rule} {rule_name}（{len(rule_items)}）")
        lines.append("")
        if not rule_items:
            lines.append("无。")
        else:
            for item in rule_items:
                lines.append(f"- `{item.path}:{item.line}` — {item.message}")
        lines.append("")
    lines += [
        "## advisory 命中（契约 10「反 AI 味」机扫项）",
        "",
        "advisory 只报告、不参与 exit 1；判定口径见内部文档「设计契约」契约 10 与「反 AI 味」。",
        "",
        "| 规则 | 含义 | 条数 |",
        "| --- | --- | ---: |",
    ]
    for rule, rule_name in ADVISORY_NAMES.items():
        lines.append(f"| {rule} | {rule_name} | {adv_counts.get(rule, 0)} |")
    lines += ["", f"**advisory 总计 {len(advisories)} 条。**", ""]
    for rule, rule_name in ADVISORY_NAMES.items():
        rule_items = [a for a in advisories if a.rule == rule]
        lines.append(f"### {rule} {rule_name}（{len(rule_items)}）")
        lines.append("")
        if not rule_items:
            lines.append("无。")
        else:
            for item in rule_items:
                lines.append(f"- `{item.path}:{item.line}` — {item.message}")
        lines.append("")
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# selftest
# --------------------------------------------------------------------------- #
SELFTEST_FILES = {
    "MeowLauncher.WinUI/Themes/AppTheme.xaml": (
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n'
        '  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        '  <AcrylicBrush x:Key="AppCardBrush" TintColor="#20242B" />\n'
        '  <AcrylicBrush x:Key="AppShellBrush" TintColor="#12151A" />\n'
        '  <x:Double x:Key="FontSizeBody">13</x:Double>\n'
        "</ResourceDictionary>\n"
    ),
    "MeowLauncher.WinUI/MainWindow.xaml": (
        '<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n'
        '  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        '  <Border x:Name="RootVeil" Background="#4D0B0D10" />\n'
        '  <Border Style="{StaticResource WidgetCardStyle}" CornerRadius="22" Padding="18" />\n'
        '  <Button Content="保存并应用" Click="Save_Click" />\n'
        '  <Button Content="打开" AutomationProperties.Name="打开" />\n'
        '  <TextBlock><TextBlock.Foreground><LinearGradientBrush /></TextBlock.Foreground></TextBlock>\n'
        '  <FontIcon Glyph="🚀" />\n'
        '  <TextBlock Text="0%" />\n'
        '  <TextBlock Text="+0" />\n'
        '  <EntranceThemeTransition FromVerticalOffset="18" />\n'
        '  <EntranceThemeTransition FromHorizontalOffset="-24" />\n'
        '  <Border Style="{StaticResource WidgetCardStyle}">\n'
        '    <Border Style="{StaticResource WidgetCardStyle}">\n'
        "    </Border>\n"
        "  </Border>\n"
        "</Window>\n"
    ),
    "MeowLauncher.WinUI/Views/SettingsPage.xaml": (
        '<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n'
        '  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        '  <StackPanel>\n'
        '    <Border Style="{StaticResource WidgetCardStyle}">\n'
        '      <StackPanel>\n'
        '        <TextBlock Text="游戏目录" FontSize="12.5" />\n'
        '        <Button Content="打开" Click="Open_Click" />\n'
        "      </StackPanel>\n"
        "    </Border>\n"
        "  </StackPanel>\n"
        "</Page>\n"
    ),
    "MeowLauncher.WinUI/Views/OnlinePage.xaml": (
        '<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n'
        '  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        "  <ListView><ListView.ItemTemplate><DataTemplate /></ListView.ItemTemplate></ListView>\n"
        "</Page>\n"
    ),
    "MeowLauncher.WinUI/Services/AppPreferences.cs": (
        "public sealed class AppPreferences\n{\n"
        "    public double VeilOpacity { get; set; } = 0.30;\n"
        "}\n"
    ),
    "MeowLauncher.WinUI/MainWindow.xaml.cs": (
        "class M\n{\n"
        "    void F() { var a = Math.Max(veil, 0.15); }\n"
        "    void G() { var b = new Button(); }\n"
        "}\n"
    ),
}

SELFTEST_EXPECT = {"R1": 1, "R2": 3, "R3": 1, "R4": 1, "R5": 1, "R6": 1, "R7": 3, "R8": 1}
SELFTEST_EXPECT_ADV = {"A1": 1, "A2": 1, "A3": 1, "A4": 2, "A5": 1}


def selftest() -> int:
    with tempfile.TemporaryDirectory(prefix="meow-guard-") as tmp:
        root = Path(tmp)
        for rel, content in SELFTEST_FILES.items():
            target = root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
        violations, advisories, _, _ = scan(root)
        counts = counts_by_rule(violations)
        adv_counts = counts_by_rule(advisories)
        ok = True
        print("guard.py 自检（临时夹具）")
        for rule, expected in SELFTEST_EXPECT.items():
            actual = counts.get(rule, 0)
            status = "PASS" if actual == expected else "FAIL"
            if actual != expected:
                ok = False
            print(f"  {rule} {RULE_NAMES[rule]}: 期望 {expected} 实际 {actual}  {status}")
        for rule, expected in SELFTEST_EXPECT_ADV.items():
            actual = adv_counts.get(rule, 0)
            status = "PASS" if actual == expected else "FAIL"
            if actual != expected:
                ok = False
            print(f"  {rule} {ADVISORY_NAMES[rule]}: 期望 {expected} 实际 {actual}  {status}")
        if not ok:
            for item in violations:
                print(f"    {item.rule} {item.path}:{item.line} {item.message}")
            for item in advisories:
                print(f"    {item.rule} {item.path}:{item.line} {item.message}")
        print("自检通过。" if ok else "自检失败。")
        return 0 if ok else 2


# --------------------------------------------------------------------------- #
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="MeowLauncher.WinUI 红线守卫")
    parser.add_argument("--root", default=str(DEFAULT_ROOT), help="mclauncher 根目录")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--count", action="store_true", help="只输出红线数量")
    parser.add_argument("--report", help="把改造前基线写进指定 md（如 guard-baseline.md）")
    parser.add_argument("--selftest", action="store_true", help="用临时夹具验证八条红线与五条 advisory")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    root = Path(args.root).resolve()
    if not (root / WINUI_DIR).exists():
        print(f"找不到 {WINUI_DIR}/：{root}", file=sys.stderr)
        return 2

    violations, advisories, token_sources, allowed = scan(root)

    if args.report:
        report_path = Path(args.report)
        if not report_path.is_absolute():
            report_path = root / report_path
        command = "python tools/guard.py --report guard-baseline.md"
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(
            render_baseline_md(root, violations, advisories, token_sources, allowed, command),
            encoding="utf-8",
        )
        print(
            f"基线已写入 {report_path.as_posix()}（红线 {len(violations)} 条，"
            f"advisory {len(advisories)} 条）"
        )

    if args.json:
        print(
            json.dumps(
                {
                    "root": root.as_posix(),
                    "total": len(violations),
                    "counts": counts_by_rule(violations),
                    "advisoryTotal": len(advisories),
                    "advisoryCounts": counts_by_rule(advisories),
                    "fontTokens": sorted(allowed),
                    "violations": [asdict(v) for v in violations],
                    "advisories": [asdict(a) for a in advisories],
                },
                ensure_ascii=False,
                indent=2,
            )
        )
    elif args.count:
        print(len(violations))
    else:
        print(render_console(root, violations, advisories, token_sources))

    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())
