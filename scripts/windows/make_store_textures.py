"""Generates the store's own textures (no downloads): porcelain floor tiles, the flatbed scanner glass and the LED panel.

Usage: python -I scripts/windows/make_store_textures.py Assets/Art/Generated
Mask layout matches import_store_assets.py: R metallic, G occlusion, A smoothness.
"""
import os, sys

import numpy as np
from PIL import Image, ImageFilter

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(7)


def noise(size, scale, octaves=4):
    """Smooth value noise in 0..1, tileable (wrapped by using integer grid counts)."""
    acc = np.zeros((size, size), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        cells = max(1, int(scale * 2 ** o))
        grid = rng.random((cells, cells)).astype(np.float32)
        img = Image.fromarray((grid * 255).astype(np.uint8)).resize((size, size), Image.BICUBIC)
        # Wrap: tile the small grid 3x3 and crop the middle so edges continue.
        big = np.tile(grid, (3, 3))
        img = Image.fromarray((big * 255).astype(np.uint8)).resize((size * 3, size * 3), Image.BICUBIC)
        layer = np.asarray(img, np.float32)[size:2 * size, size:2 * size] / 255.0
        acc += layer * amp
        total += amp
        amp *= 0.5
    return acc / total


def normal_from_height(h, strength):
    dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * strength
    dy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * strength
    n = np.stack([-dx, dy, np.ones_like(h)], -1)  # OpenGL style (green up)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return Image.fromarray(((n * 0.5 + 0.5) * 255 + 0.5).astype(np.uint8))


def save_mask(path, ao, smooth):
    rgba = np.stack([np.zeros_like(ao), ao, np.zeros_like(ao), smooth], -1)
    Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(path, optimize=True)


# ---------- Floor: 2 x 2 porcelain tiles of 60 cm per texture (1.2 m), 3 mm grout ----------
S = 1024
tiles = 2
px_per_m = S / 1.2
grout = max(2, int(round(0.003 * px_per_m)))
yy, xx = np.mgrid[0:S, 0:S]
tile_px = S // tiles
gx = np.minimum(xx % tile_px, tile_px - 1 - xx % tile_px)
gy = np.minimum(yy % tile_px, tile_px - 1 - yy % tile_px)
edge = np.minimum(gx, gy).astype(np.float32)
is_grout = edge < grout / 2
bevel = np.clip((edge - grout / 2) / 3.0, 0, 1)  # soft tile edge

base = np.array([0.835, 0.835, 0.825], np.float32)
mottle = noise(S, 6) * 0.6 + noise(S, 24) * 0.4
tone = np.zeros((S, S), np.float32)
for ty in range(tiles):
    for tx in range(tiles):
        tone[ty * tile_px:(ty + 1) * tile_px, tx * tile_px:(tx + 1) * tile_px] = rng.uniform(-0.015, 0.015)
speck = (rng.random((S, S)) > 0.9985).astype(np.float32) * rng.uniform(-0.08, 0.05, (S, S))
lum = 1.0 + (mottle - 0.5) * 0.05 + tone + speck
albedo = base[None, None, :] * lum[..., None]
albedo[is_grout] = np.array([0.62, 0.62, 0.61])
Image.fromarray((np.clip(albedo, 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, 'floor_tile_Color.png'), optimize=True)
height = bevel * 1.0 + (noise(S, 32) - 0.5) * 0.02
normal_from_height(height, 6.0).save(os.path.join(out, 'floor_tile_NormalGL.png'), optimize=True)
ao = np.where(is_grout, 0.75, 1.0).astype(np.float32)
smooth = np.where(is_grout, 0.15, 0.62 + (noise(S, 12) - 0.5) * 0.08).astype(np.float32)
save_mask(os.path.join(out, 'floor_tile_Mask.png'), ao, smooth)

# ---------- Wall: very soft paint relief (normal only) ----------
W = 512
wall_h = noise(W, 20) * 0.6 + noise(W, 60) * 0.4
normal_from_height(wall_h, 0.6).save(os.path.join(out, 'wall_paint_NormalGL.png'), optimize=True)

# ---------- Scanner glass: dark glass with a red laser star (emission map separate) ----------
G = 512
glass = np.full((G, G, 3), 0.035, np.float32)
glass += (noise(G, 8)[..., None] - 0.5) * 0.01
Image.fromarray((np.clip(glass, 0, 1) * 255).astype(np.uint8)).save(os.path.join(out, 'scanner_glass_Color.png'), optimize=True)
em = Image.new('L', (G, G), 0)
arr = np.zeros((G, G), np.float32)
c = (G - 1) / 2
for angle in np.radians([0, 30, 60, 90, 120, 150]):
    d = np.abs((xx[:G, :G] - c) * np.sin(angle) - (yy[:G, :G] - c) * np.cos(angle))
    arr = np.maximum(arr, np.exp(-(d / 1.6) ** 2))
r = np.hypot(xx[:G, :G] - c, yy[:G, :G] - c)
arr *= np.clip(1 - (r / (G * 0.48)) ** 4, 0, 1)
rgb = np.stack([arr, arr * 0.08, arr * 0.05], -1)
Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).save(os.path.join(out, 'scanner_glass_Emission.png'), optimize=True)

# ---------- LED panel: flat white with a soft frame ----------
L = 256
frame = np.minimum(np.minimum(xx[:L, :L], L - 1 - xx[:L, :L]), np.minimum(yy[:L, :L], L - 1 - yy[:L, :L])).astype(np.float32)
lit = np.clip((frame - 10) / 14, 0, 1)
panel = np.stack([lit * 1.0 + 0.55 * (1 - lit), lit * 1.0 + 0.55 * (1 - lit), lit * 0.98 + 0.56 * (1 - lit)], -1)
Image.fromarray((panel * 255).astype(np.uint8)).save(os.path.join(out, 'led_panel_Color.png'))
glow = np.stack([lit, lit, lit * 0.97], -1)
Image.fromarray((glow * 255).astype(np.uint8)).save(os.path.join(out, 'led_panel_Emission.png'))
print('wrote', sorted(os.listdir(out)))
