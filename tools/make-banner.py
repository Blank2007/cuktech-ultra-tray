# -*- coding: utf-8 -*-
"""生成仓库顶部 banner：assets/intro-zh.png 与 assets/intro-en.png。"""
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H = 1280, 400
BG_TOP = (15, 18, 22)
BG_BOT = (26, 32, 40)
GREEN = (52, 211, 153)
WHITE = (238, 242, 247)
MUTED = (150, 162, 178)
DIM = (110, 122, 138)

MSYH = "C:/Windows/Fonts/msyh.ttc"
MSYHBD = "C:/Windows/Fonts/msyhbd.ttc"
SEGOE = "C:/Windows/Fonts/segoeui.ttf"
SEGOEB = "C:/Windows/Fonts/segoeuib.ttf"


def font(path, size, index=0):
    return ImageFont.truetype(path, size, index=index)


def gradient(w, h):
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / (h - 1)
        c = tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3))
        d.line([(0, y), (w, y)], fill=c)
    return img


def rounded_panel(path, target_h, radius=16):
    src = Image.open(path).convert("RGBA")
    r = target_h / src.height
    w = max(1, int(src.width * r))
    src = src.resize((w, target_h), Image.LANCZOS)

    mask = Image.new("L", (w, target_h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, target_h - 1], radius=radius, fill=255)
    src.putalpha(mask)

    border = Image.new("RGBA", (w, target_h), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle(
        [0, 0, w - 1, target_h - 1], radius=radius, outline=(58, 68, 84, 255), width=2
    )
    return Image.alpha_composite(src, border)


def build(out_path, lang):
    img = gradient(W, H).convert("RGBA")

    # 右侧面板截图 + 柔和阴影
    panel_h = 340
    shot = rounded_panel("docs/shot-panel.png", panel_h)
    px = W - shot.width - 76
    py = (H - shot.height) // 2

    shadow = Image.new("RGBA", (shot.width + 80, shot.height + 80), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([40, 40, 40 + shot.width, 40 + shot.height], radius=16, fill=(0, 0, 0, 160))
    shadow = shadow.filter(ImageFilter.GaussianBlur(18))
    img.alpha_composite(shadow, (px - 40, py - 34))
    img.alpha_composite(shot, (px, py))

    d = ImageDraw.Draw(img)
    x = 68

    if lang == "zh":
        tag = "CUKTECH 10 ULTRA  ·  WINDOWS TRAY GAUGE"
        title = ["酷态科 10 号 Ultra", "托盘功率表"]
        sub = ["托盘图标上直接读总功率，四口电压 / 电流 / 功率一秒一刷",
               "蓝牙直连充电器，不用米家 App 常驻，不用网关"]
        foot = "Windows 10 / 11  ·  单文件绿色版 + 安装包  ·  MIT"
        tag_font, title_font, sub_font, foot_font = font(SEGOEB, 19), font(MSYHBD, 50), font(MSYH, 22), font(MSYH, 17)
    else:
        tag = "CUKTECH 10 ULTRA  ·  WINDOWS TRAY GAUGE"
        title = ["CUKTECH 10 Ultra", "Power Gauge for Windows"]
        sub = ["Live wattage right on the tray icon; all four ports, one refresh per second.",
               "Talks to the charger over BLE directly - no Mi Home app, no gateway."]
        foot = "Windows 10 / 11  ·  portable exe + installer  ·  MIT"
        tag_font, title_font, sub_font, foot_font = font(SEGOEB, 19), font(SEGOEB, 44), font(SEGOE, 21), font(SEGOE, 16)

    d.text((x, 58), tag, font=tag_font, fill=GREEN)
    y = 100
    for line in title:
        d.text((x, y), line, font=title_font, fill=WHITE)
        y += title_font.size + 12
    y += 14
    for line in sub:
        d.text((x, y), line, font=sub_font, fill=MUTED)
        y += sub_font.size + 12
    d.text((x, H - 52), foot, font=foot_font, fill=DIM)

    # 顶部/底部细线
    d.line([(0, 0), (W, 0)], fill=(40, 48, 60, 255), width=1)

    img.convert("RGB").save(out_path, "PNG", optimize=True)
    print("saved", out_path, img.size)


build("assets/intro-zh.png", "zh")
build("assets/intro-en.png", "en")
