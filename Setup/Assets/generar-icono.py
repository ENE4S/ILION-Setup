# Icono de ILION SETUP: el «Pulso» B (el mismo dibujo que Setup/Sputnik.cs) sobre la placa óxido de la barra de título.
# Uso: python generar-icono.py <salida.ico>   (escribe también <salida>.png para revisarlo)
import sys
from PIL import Image, ImageDraw

RUST, CREAM, YELLOW, INK = (140, 41, 31, 255), (235, 222, 189, 255), (255, 214, 26, 255), (23, 23, 20, 255)
S = 1024
k = S / 210.0                      # lienzo del diseño: 210 × 200 (se centra en vertical)
dy = (210 - 200) / 2

def P(x, y): return (x * k, (y + dy) * k)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(34 * k), fill=RUST, outline=INK, width=int(4 * k))

# Dos anillos de onda (en el icono quietos): el interior pleno, el exterior más tenue
for scale, alpha in ((0.62, 255), (0.98, 150)):
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    r = 80 * scale
    ImageDraw.Draw(layer).ellipse([P(105 - r, 100 - r), P(105 + r, 100 + r)], outline=YELLOW[:3] + (alpha,), width=int(5 * k))
    img = Image.alpha_composite(img, layer)

d = ImageDraw.Draw(img)
antennas = [(92, 112, 44, 156), (98, 118, 62, 166), (106, 120, 90, 172), (86, 104, 36, 124)]
for colour, width in ((INK, 8), (YELLOW, 4)):
    for x1, y1, x2, y2 in antennas:
        d.line([P(x1, y1), P(x2, y2)], fill=colour, width=int(width * k))
        for x, y in ((x1, y1), (x2, y2)):
            rr = width * k / 2
            cx, cy = P(x, y)
            d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=colour)
d.ellipse([P(82, 64), P(142, 124)], fill=CREAM, outline=INK, width=int(5 * k))
d.ellipse([P(94, 76), P(108, 90)], fill=(255, 255, 255, 255))

out = img.resize((256, 256), Image.LANCZOS)
out.save(sys.argv[1], sizes=[(s, s) for s in (256, 128, 64, 48, 32, 24, 16)])
out.save(sys.argv[1].replace(".ico", ".png"))
print("ok")
