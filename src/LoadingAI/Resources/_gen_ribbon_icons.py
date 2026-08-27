"""One-shot: 16/32 ribbon glyphs. Transparent bg, charcoal chat bubble."""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent
CHARCOAL = (43, 43, 43, 255)
WHITE = (255, 255, 255, 255)


def make(final: int) -> Image.Image:
    s = final * 8
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = s // 10
    # Bubble body leaves room for the tail.
    body = [pad, pad, s - 1 - pad, int(s * 0.72)]
    d.rounded_rectangle(body, radius=s // 6, fill=CHARCOAL)
    # Tail (bottom-left).
    tail = [
        (int(s * 0.28), int(s * 0.68)),
        (int(s * 0.28), s - 1 - pad),
        (int(s * 0.52), int(s * 0.68)),
    ]
    d.polygon(tail, fill=CHARCOAL)
    # Three dots (chat).
    cy = int((body[1] + body[3]) / 2)
    r = max(s // 18, 4)
    gap = s // 5
    cx = s // 2
    for dx in (-gap, 0, gap):
        d.ellipse([cx + dx - r, cy - r, cx + dx + r, cy + r], fill=WHITE)
    return img.resize((final, final), Image.Resampling.LANCZOS)


make(32).save(OUT / "Ribbon32.png")
make(16).save(OUT / "Ribbon16.png")
print("wrote", OUT / "Ribbon32.png", OUT / "Ribbon16.png")
