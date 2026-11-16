#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成实例的「加载器徽标」：Fabric / Quilt / Forge / NeoForge（原版沿用 Assets/default_icon.png）。

全部是自己画的简化几何图形（圆角方块 + 白色图形），不拷贝各加载器的官方 logo，也不用任何字体，
避免商标 / 许可问题。输出到 MeowLauncher.WinUI/Assets/Icons/loaders/，128×128 透明底 PNG。

用法（在 mclauncher/ 下）：python tools/make_loader_icons.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "MeowLauncher.WinUI" / "Assets" / "Icons" / "loaders"
S = 128          # 输出尺寸
SS = 4           # 超采样倍数（抗锯齿）
W = (255, 255, 255, 255)


def canvas(bg):
    im = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, S * SS - 1, S * SS - 1], radius=26 * SS, fill=bg)
    return im, d


def finish(im, name):
    OUT.mkdir(parents=True, exist_ok=True)
    im.resize((S, S), Image.LANCZOS).save(OUT / f"{name}.png")


def u(v):
    return int(v * SS)


def fabric():
    # 编织：三条斜向白色条带，像经纬线
    im, d = canvas((184, 158, 112, 255))
    for k, off in enumerate((-30, 6, 42)):
        d.polygon([(u(20 + off), u(110)), (u(46 + off), u(110)), (u(112 + off), u(18)), (u(86 + off), u(18))], fill=W)
    # 裁掉超出圆角方块的部分
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S * SS - 1, S * SS - 1], radius=26 * SS, fill=255)
    base = Image.new("RGBA", im.size, (0, 0, 0, 0))
    base.paste(im, (0, 0), mask)
    finish(base, "fabric")


def quilt():
    # 拼布：2×2 四块白色方块，间隔均匀，四个色调略不同
    im, d = canvas((142, 91, 209, 255))
    tones = [(255, 255, 255, 255), (232, 220, 250, 255), (232, 220, 250, 255), (255, 255, 255, 255)]
    size, gap, x0, y0 = 36, 8, 26, 26
    for i in range(4):
        cx, cy = x0 + (i % 2) * (size + gap), y0 + (i // 2) * (size + gap)
        d.rounded_rectangle([u(cx), u(cy), u(cx + size), u(cy + size)], radius=u(6), fill=tones[i])
    finish(im, "quilt")


def anvil(d, color):
    # 铁砧：顶面 + 腰 + 底座
    d.polygon([(u(22), u(38)), (u(100), u(38)), (u(112), u(52)), (u(78), u(58)), (u(70), u(78)), (u(88), u(96)),
               (u(88), u(104)), (u(40), u(104)), (u(40), u(96)), (u(58), u(78)), (u(52), u(58)), (u(22), u(52))], fill=color)


def forge():
    im, d = canvas((52, 67, 92, 255))
    anvil(d, (240, 140, 60, 255))
    finish(im, "forge")


def neoforge():
    im, d = canvas((232, 129, 47, 255))
    anvil(d, W)
    finish(im, "neoforge")


def vanilla():
    """原版：自己画的像素风草方块（等距视角，16×16 面纹理，固定随机种子）。不拷贝 Mojang 的材质文件。"""
    import random
    rnd = random.Random(7)
    G = [(98, 160, 62), (88, 148, 54), (108, 172, 70), (80, 138, 50)]
    D = [(134, 96, 67), (121, 85, 58), (146, 108, 76), (110, 76, 52)]
    im = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    T, R, B, L = (64, 8), (118, 36), (64, 64), (10, 36)
    H = 54

    def face(o, a, b, cell):
        for i in range(16):
            for j in range(16):
                col = cell(i, j)
                pts = [(o[0] + a[0] * i / 16 + b[0] * j / 16, o[1] + a[1] * i / 16 + b[1] * j / 16),
                       (o[0] + a[0] * (i + 1) / 16 + b[0] * j / 16, o[1] + a[1] * (i + 1) / 16 + b[1] * j / 16),
                       (o[0] + a[0] * (i + 1) / 16 + b[0] * (j + 1) / 16, o[1] + a[1] * (i + 1) / 16 + b[1] * (j + 1) / 16),
                       (o[0] + a[0] * i / 16 + b[0] * (j + 1) / 16, o[1] + a[1] * i / 16 + b[1] * (j + 1) / 16)]
                d.polygon([(u(x), u(y)) for x, y in pts], fill=col + (255,), outline=col + (255,))

    def shade(c, f):
        return tuple(int(v * f) for v in c)

    def top(i, j):
        return G[rnd.randrange(4)]

    def side(f):
        def cell(i, j):
            fringe = 3 + (rnd.randrange(3) if (i * 7 + j) % 3 else 0)
            if j < fringe - 1 or (j < fringe and rnd.random() < 0.5):
                return shade(G[rnd.randrange(4)], f)
            return shade(D[rnd.randrange(4)], f)
        return cell

    face(T, (R[0] - T[0], R[1] - T[1]), (L[0] - T[0], L[1] - T[1]), top)
    face(L, (B[0] - L[0], B[1] - L[1]), (0, H), side(0.78))
    face(B, (R[0] - B[0], R[1] - B[1]), (0, H), side(0.58))
    finish(im, "vanilla")


if __name__ == "__main__":
    fabric()
    quilt()
    forge()
    neoforge()
    vanilla()
    print("loader icons ->", OUT)
