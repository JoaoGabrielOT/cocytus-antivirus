"""Gera o ícone original do Cocytus (cristal de gelo hexagonal) em .ico e .png. Requer Pillow."""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

S = 1024


def draw_icon() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # Fundo: quadrado arredondado com gradiente noturno.
    bg = Image.new("RGBA", (S, S))
    px = bg.load()
    for y in range(S):
        t = y / (S - 1)
        r = int(10 + (20 - 10) * t)
        g = int(22 + (44 - 22) * t)
        b = int(44 + (78 - 44) * t)
        for x in range(S):
            px[x, y] = (r, g, b, 255)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=220, fill=255)
    img.paste(bg, (0, 0), mask)

    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = S / 2, S / 2 + 10
    ice = (186, 230, 253, 255)
    deep = (56, 189, 248, 255)

    # Hexágono externo (contorno).
    R = 330
    hexagon = [(cx + R * math.cos(math.radians(60 * i - 90)), cy + R * math.sin(math.radians(60 * i - 90))) for i in range(6)]
    d.line(hexagon + [hexagon[0]], fill=deep, width=34, joint="curve")

    # Seis braços do cristal com ramificações.
    for i in range(6):
        a = math.radians(60 * i - 90)
        x2, y2 = cx + 250 * math.cos(a), cy + 250 * math.sin(a)
        d.line((cx, cy, x2, y2), fill=ice, width=40)
        for k, length in ((0.55, 90), (0.8, 60)):
            bx, by = cx + 250 * k * math.cos(a), cy + 250 * k * math.sin(a)
            for side in (-1, 1):
                b = a + side * math.radians(55)
                d.line((bx, by, bx + length * math.cos(b), by + length * math.sin(b)), fill=ice, width=26)
    # Núcleo.
    d.regular_polygon((cx, cy, 70), 6, rotation=0, fill=deep)
    d.regular_polygon((cx, cy, 36), 6, rotation=0, fill=ice)

    glow = layer.filter(ImageFilter.GaussianBlur(18))
    img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, layer)
    return img


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("src/Cocytus.App/Assets")
    out.mkdir(parents=True, exist_ok=True)
    icon = draw_icon()
    icon.resize((256, 256), Image.LANCZOS).save(out / "cocytus.png")
    icon.save(out / "cocytus.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
