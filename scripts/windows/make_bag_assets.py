"""Generates what the counter's plastic bag is made of (2026-11-11): the printed film (colour and normal map) and the
rustle of something going into it. Nothing is downloaded.

The two textures share one layout with the mesh (P3Meshes.PlasticBag): u runs once round the bag, 0.9 m for the whole
width, and v is height at the same scale, so the lower half is the body and the rest is plain film for the handles and
the floor. u = 0.75 is the middle of the side toward the clerk, u = 0.25 the side toward the customer; the store's mark
is printed on both, above the belly, where the folds of a bag that has sunk into itself do not swallow it.

The creases are in both textures on purpose. The store's light is baked without direction, so a normal map alone only
shows in reflections; the same creases shaded into the colour are what make the film read as crumpled under flat light.

Usage: python -I scripts/windows/make_bag_assets.py Assets/Art/Generated Assets/Audio/Generated
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from scipy.io import wavfile
from scipy.ndimage import gaussian_filter
from scipy.signal import butter, sosfilt
from scipy.spatial import cKDTree

art, audio = sys.argv[1], sys.argv[2]
os.makedirs(art, exist_ok=True)
os.makedirs(audio, exist_ok=True)
rng = np.random.default_rng(1111)

FONTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'Resources', 'Fonts')
HEAVY = os.path.join(FONTS, 'Pretendard-ExtraBold.ttf')

W, H = 1024, 512
ATLAS_METERS = 0.9          # the width of the texture on the bag (P3Meshes.BagAtlasMeters)
PX = W / ATLAS_METERS       # pixels per metre, the same both ways
FILM = np.array([242, 243, 240], dtype=float)
NAVY = (4, 30, 66)


# ---------- Creases ----------

def creases(count, stretch=1.0):
    """Straight sharp folds between facets: second-nearest minus nearest distance to scattered points, wrapped in u.
    stretch > 1 makes the facets that much taller than wide, the long folds of film hanging from its handles."""
    points = rng.random((count, 2)) * (W, H / stretch)
    wrapped = np.concatenate([points + (dx, 0) for dx in (-W, 0, W)])
    ys, xs = np.mgrid[0:H, 0:W]
    distance, _ = cKDTree(wrapped).query(np.stack([xs.ravel() + 0.5, (ys.ravel() + 0.5) / stretch], axis=1), k=2)
    field = (distance[:, 1] - distance[:, 0]).reshape(H, W)
    return field / field.max()


def wrap_blur(field, sigma):
    return gaussian_filter(field, sigma, mode=('nearest', 'wrap'))


height = (1.00 * creases(26, 3.2) ** 0.8      # long folds, top to bottom
          + 0.55 * creases(130, 1.6) ** 0.9   # the crumple between them
          + 0.22 * creases(900) ** 1.0        # fine crackle
          + 0.3 * wrap_blur(rng.standard_normal((H, W)), 38) * 30)   # slow swell
height = wrap_blur(height, 0.8)

# Rows run down the image and v runs up, so the slope along v is the negative of the slope along rows.
du = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
dv = -np.gradient(height, axis=0)
slope = np.hypot(du, dv)
scale = 0.42 / np.percentile(slope, 95)


def normals_of(field):
    a = (np.roll(field, -1, axis=1) - np.roll(field, 1, axis=1)) * 0.5
    b = -np.gradient(field, axis=0)
    x, y = -a * scale, -b * scale
    n = np.sqrt(x * x + y * y + 1)
    return np.stack([x / n, y / n, 1 / n], axis=2)


normal = normals_of(height)
Image.fromarray(np.round((normal * 0.5 + 0.5) * 255).astype(np.uint8)).save(os.path.join(art, 'bag_film_NormalGL.png'), optimize=True)

# The same creases as light and shade: lit from above and a little from the left, as under the ceiling strips.
light = np.array([-0.25, 0.62, 0.74])
light /= np.linalg.norm(light)
# Only the creases, not the slow swell: a broad slope shaded into the colour looks like a stain.
lit = normals_of(height - wrap_blur(height, 9)) @ light
shade = np.clip(1 + (lit - lit.mean()) * 0.5, 0.87, 1.05)
# Deep in a fold less light gets in.
hollow = wrap_blur(height, 4) - height
shade *= np.clip(1 - np.clip(hollow, 0, None) / (np.percentile(hollow, 99) + 1e-9) * 0.07, 0.9, 1)


# ---------- Print ----------

def meters(x_m, y_m):
    """A place on the body: metres along the texture and above the counter, to pixels."""
    return x_m * PX, H - y_m * PX


ink = Image.new('L', (W, H), 0)
draw = ImageDraw.Draw(ink)
for u in (0.25, 0.75):
    cx = u * W
    _, cy = meters(0, 0.128)
    r = 0.036 * PX
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), outline=235, width=max(2, int(0.0048 * PX)))
    draw.text((cx, cy), '동아', font=ImageFont.truetype(HEAVY, int(0.028 * PX)), fill=235, anchor='mm')
ink = np.asarray(ink.filter(ImageFilter.GaussianBlur(0.6)), dtype=float)[..., None] / 255

colour = FILM[None, None, :] * (1 - ink) + np.array(NAVY, dtype=float)[None, None, :] * ink
# Ink is matt: it takes the shade of the folds but not their highlights.
colour *= np.where(ink > 0.5, np.minimum(shade[..., None], 1.0), shade[..., None])
Image.fromarray(np.clip(np.round(colour), 0, 255).astype(np.uint8)).save(os.path.join(art, 'bag_film_Color.png'), optimize=True)


# ---------- Sound ----------

SR = 44100


def band(x, lo, hi):
    return sosfilt(butter(4, [lo, hi], btype='band', fs=SR, output='sos'), x)


def rustle(seconds, crackles, lo, hi):
    """Film being pushed aside: hiss whose loudness jumps with every little fold that gives way."""
    n = int(seconds * SR)
    hits = np.zeros(n)
    at = rng.integers(0, n, crackles)
    hits[at] = rng.random(crackles) ** 2
    decay = np.exp(-np.arange(int(0.012 * SR)) / (0.0028 * SR))
    loudness = np.convolve(hits, decay)[:n] + 0.06
    return band(rng.standard_normal(n), lo, hi) * loudness


n = int(0.34 * SR)
t = np.arange(n) / SR
swell = np.clip(t / 0.02, 0, 1) * np.exp(-t / 0.11)
sound = rustle(0.34, 150, 2600, 11000) * swell
# What went in comes to rest on the counter through the film.
m = int(0.09 * SR)
tm = np.arange(m) / SR
thump = np.sin(2 * math.pi * 150 * tm) * np.exp(-tm / 0.02) * 0.35 + band(rng.standard_normal(m), 300, 1800) * np.exp(-tm / 0.012) * 0.05
start = int(0.11 * SR)
sound[start:start + m] += thump * np.max(np.abs(sound))
fade = int(0.004 * SR)
sound[:fade] *= np.linspace(0, 1, fade)
sound[-fade:] *= np.linspace(1, 0, fade)
sound = sound / np.max(np.abs(sound)) * 10 ** (-11 / 20)
wavfile.write(os.path.join(audio, 'sfx_bag.wav'), SR, (np.clip(sound, -1, 1) * 32767).astype(np.int16))

print('bag assets written to', art, 'and', audio)
