#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""重绘原版实例使用的草方块。

用法：python tools/make_loader_icons.py
四种 Mod 加载器的图标来自官方项目，来源见 THIRD-PARTY-NOTICES.md。
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
    vanilla()
    print("loader icons ->", OUT)
