import os
from collections import Counter
from PIL import Image

base = r"D:\dsh projects\cuktech-ultra-tray\docs"
for f in ["tmp-aot-read.png", "tmp-aot-control.png", "tmp-aot-mode.png"]:
    im = Image.open(os.path.join(base, f)).convert("RGB")
    w, h = im.size
    px = im.load()
    print(f, w, h, "TL", px[2, 2], "BR", px[w - 3, h - 3])
    rows = []
    for y in range(0, h, 24):
        c = sum(1 for x in range(w) if sum(px[x, y]) / 3 > 150)
        rows.append((y, c))
    print("  bright:", rows)
    print("  top5:", Counter(im.getdata()).most_common(5))
