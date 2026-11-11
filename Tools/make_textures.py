"""Generate the small procedural textures: grass blades, birch bark, water ripples, ground variation.

Run from the project root:  python Tools/make_textures.py
"""
import math
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

ROOT = Path(__file__).resolve().parents[1]
FOL = ROOT / "Assets" / "Textures" / "Foliage"
GEN = ROOT / "Assets" / "Textures" / "Generated"
GEN.mkdir(parents=True, exist_ok=True)
rng = np.random.default_rng(7)


def tileable_noise(size, sigma):
    n = rng.standard_normal((size, size))
    n = ndimage.gaussian_filter(n, sigma, mode="wrap")
    return (n - n.min()) / (n.max() - n.min())


# grass clump: blades rising from the bottom edge
S = 512
img = Image.new("RGBA", (S, S), (70, 92, 40, 0))
d = ImageDraw.Draw(img)
for i in range(64):
    x0 = rng.uniform(0.06, 0.94) * S
    hgt = rng.uniform(0.45, 0.98) * S
    lean = rng.uniform(-0.28, 0.28) * S
    w0 = rng.uniform(5, 11)
    dry = rng.random() < 0.18
    steps = 14
    for k in range(steps):
        t0, t1 = k / steps, (k + 1) / steps
        def at(t):
            return (x0 + lean * t * t, S - hgt * t)
        (xa, ya), (xb, yb) = at(t0), at(t1)
        wa, wb = w0 * (1 - t0) + 0.6, w0 * (1 - t1) + 0.6
        base = np.array([96, 80, 46]) if dry else np.array([44, 74, 26])
        tip = np.array([196, 176, 112]) if dry else np.array([128, 160, 70])
        col = tuple(int(v) for v in base + (tip - base) * (t0 ** 0.8)) + (255,)
        d.polygon([(xa - wa / 2, ya), (xa + wa / 2, ya), (xb + wb / 2, yb), (xb - wb / 2, yb)], fill=col)
img = img.filter(ImageFilter.SMOOTH)
img.save(GEN / "grass_blades.png")

# birch bark: cream with dark horizontal lenticels and patches
S = 512
base = 0.86 + 0.08 * tileable_noise(S, 6)[..., None] * np.array([1, 1, 0.96])
bark = np.ones((S, S, 3)) * np.array([232, 228, 214]) / 255 * base
b = Image.fromarray((np.clip(bark, 0, 1) * 255).astype(np.uint8), "RGB")
d = ImageDraw.Draw(b)
for i in range(240):
    x, y = rng.uniform(0, S), rng.uniform(0, S)
    w, hh = rng.uniform(8, 60), rng.uniform(1.5, 5)
    shade = int(rng.uniform(20, 70))
    for dx in (-S, 0, S):
        d.ellipse((x + dx - w / 2, y - hh / 2, x + dx + w / 2, y + hh / 2), fill=(shade, shade - 4, shade - 8))
for i in range(9):
    x, y = rng.uniform(0, S), rng.uniform(0, S)
    r = rng.uniform(6, 16)
    d.ellipse((x - r * 1.8, y - r * 0.45, x + r * 1.8, y + r * 0.45), fill=(96, 88, 78))
b = b.filter(ImageFilter.GaussianBlur(0.8))
b.save(FOL / "birch_bark.png")

# water ripple normal map (tileable)
S = 256
hgt = tileable_noise(S, 3) * 0.6 + tileable_noise(S, 9) * 0.4
gx = (np.roll(hgt, -1, 1) - np.roll(hgt, 1, 1)) * 6
gy = (np.roll(hgt, -1, 0) - np.roll(hgt, 1, 0)) * 6
n = np.stack([-gx, gy, np.ones_like(gx)], -1)
n /= np.linalg.norm(n, axis=-1, keepdims=True)
Image.fromarray(((n * 0.5 + 0.5) * 255).astype(np.uint8), "RGB").save(GEN / "water_normal.png")

# low-frequency ground variation (tileable greyscale)
v = tileable_noise(512, 18) * 0.65 + tileable_noise(512, 5) * 0.35
Image.fromarray((v * 255).astype(np.uint8), "L").save(GEN / "ground_variation.png")
print("ok")
