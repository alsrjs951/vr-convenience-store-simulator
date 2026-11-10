"""Generates the money the till works with (2026-11-10): the printed faces of the four won notes and four coins, the
label stickers on the coin cups, and the three sounds of handling cash. Nothing is downloaded.

The notes are the game's own design, not copies of Bank of Korea notes: each keeps only what tells a real one apart at a
glance, its size, its colour (1,000 blue, 5,000 orange, 10,000 green, 50,000 yellow) and its number. No portraits, no
bank name, no seals. Coins carry their number, as the back of a real one does.

Usage: python -I scripts/windows/make_cash_assets.py Assets/Art/Cash Assets/Audio/Generated
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

art, audio = sys.argv[1], sys.argv[2]
os.makedirs(art, exist_ok=True)
os.makedirs(audio, exist_ok=True)
rng = np.random.default_rng(1110)

FONTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'Resources', 'Fonts')
REGULAR = os.path.join(FONTS, 'Pretendard-Regular.ttf')
BOLD = os.path.join(FONTS, 'Pretendard-Bold.ttf')
HEAVY = os.path.join(FONTS, 'Pretendard-ExtraBold.ttf')


def font(size, face=BOLD):
    return ImageFont.truetype(face, int(size))


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


# ---------- Notes ----------

# (value, length in mm, ink, paper, words). All four are 68 mm tall.
NOTES = [
    (1000, 136, (38, 92, 150), (226, 235, 244), '천원'),
    (5000, 142, (176, 84, 44), (247, 231, 216), '오천원'),
    (10000, 148, (34, 112, 78), (224, 239, 228), '만원'),
    (50000, 154, (168, 118, 20), (250, 239, 203), '오만원'),
]


def wave_lines(size, ink, count, amplitude, period, phase, alpha, width=1):
    """Fine wavy lines across the whole note, the engraved ground of banknote paper."""
    w, h = size
    layer = Image.new('RGBA', size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    xs = np.arange(0, w + 4, 4)
    for i in range(count):
        y0 = (i + 0.5) * h / count
        ys = y0 + amplitude * np.sin(xs / period * 2 * math.pi + phase + i * 0.35)
        draw.line(list(zip(xs.tolist(), ys.tolist())), fill=ink + (alpha,), width=width)
    return layer


def rosette(radius, ink, petals, turns, alpha, width=2):
    """A guilloche rosette: a rose curve drawn several times at slightly different depths."""
    size = int(radius * 2 + 8)
    layer = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    c = size / 2
    theta = np.linspace(0, 2 * math.pi * turns, 2400)
    for k, depth in enumerate((0.18, 0.3, 0.42, 0.54)):
        r = radius * (1 - depth + depth * np.cos(petals * theta + k * 0.6))
        pts = list(zip((c + r * np.cos(theta)).tolist(), (c + r * np.sin(theta)).tolist()))
        draw.line(pts, fill=ink + (alpha,), width=width)
    return layer


def note(value, length, ink, paper, words):
    w = 1024
    h = int(round(w * 68 / length))
    dark = mix(ink, (10, 14, 20), 0.35)
    image = Image.new('RGBA', (w, h), paper + (255,))

    # Paper: a soft tint toward the middle, then the engraved ground.
    tint = Image.new('RGBA', (w, h), mix(paper, ink, 0.2) + (255,))
    mask = Image.new('L', (w, h), 0)
    ImageDraw.Draw(mask).ellipse((w * 0.18, -h * 0.3, w * 0.82, h * 1.3), fill=150)
    image.paste(tint, (0, 0), mask.filter(ImageFilter.GaussianBlur(60)))
    image.alpha_composite(wave_lines((w, h), ink, 44, 5, 96, 0.0, 52))
    image.alpha_composite(wave_lines((w, h), ink, 30, 9, 170, 1.3, 34))

    draw = ImageDraw.Draw(image)
    margin = 20
    # The printed field stops short of the edge, as on a real note: a plain paper border all round.
    draw.rectangle((0, 0, w - 1, h - 1), outline=paper + (255,), width=margin)
    draw.rectangle((margin, margin, w - margin - 1, h - margin - 1), outline=ink + (255,), width=3)
    draw.rectangle((margin + 9, margin + 9, w - margin - 10, h - margin - 10), outline=mix(ink, paper, 0.45) + (255,), width=1)

    # Left: a medallion with a rosette where a portrait would be.
    cx, cy, r = int(w * 0.2), h // 2, int(h * 0.36)
    draw.ellipse((cx - r - 10, cy - r - 10, cx + r + 10, cy + r + 10), fill=mix(paper, (255, 255, 255), 0.5) + (255,), outline=ink + (255,), width=4)
    art_layer = rosette(r, ink, 12, 1, 215, 2)
    image.alpha_composite(art_layer, (cx - art_layer.width // 2, cy - art_layer.height // 2))
    inner = rosette(int(r * 0.46), dark, 8, 1, 235, 2)
    image.alpha_composite(inner, (cx - inner.width // 2, cy - inner.height // 2))

    # Right: a solid band with the number reversed out of it, the part that shows when notes overlap in a hand.
    band_left = int(w * 0.8)
    draw.rectangle((band_left, margin + 16, w - margin - 17, h - margin - 17), fill=ink + (255,))
    band = wave_lines((w - margin - 17 - band_left, h - 2 * margin - 33), paper, 16, 4, 60, 0.5, 60)
    image.alpha_composite(band, (band_left, margin + 16))
    number = f'{value:,}'
    draw = ImageDraw.Draw(image)
    vertical = Image.new('RGBA', (h, w - margin - 17 - band_left), (0, 0, 0, 0))
    vd = ImageDraw.Draw(vertical)
    size = 150
    while vd.textlength(number, font=font(size, HEAVY)) > h - 2 * margin - 70:
        size -= 2
    vd.text((h / 2, vertical.height / 2), number, font=font(size, HEAVY), fill=paper + (255,), anchor='mm')
    image.alpha_composite(vertical.rotate(90, expand=True), (band_left, 0))

    # Middle: the number large, the words under it, a small number in the top corner.
    mid_left, mid_right = cx + r + 34, band_left - 30
    size = 250
    while draw.textlength(number, font=font(size, HEAVY)) > mid_right - mid_left:
        size -= 2
    mid = (mid_left + mid_right) / 2
    draw.text((mid + 3, h * 0.46 + 3), number, font=font(size, HEAVY), fill=mix(paper, ink, 0.35) + (255,), anchor='mm')
    draw.text((mid, h * 0.46), number, font=font(size, HEAVY), fill=dark + (255,), anchor='mm')
    draw.text((mid, h * 0.82), words, font=font(54, BOLD), fill=ink + (255,), anchor='mm')
    draw.text((mid_left + 4, margin + 22), number, font=font(46, BOLD), fill=ink + (255,), anchor='la')
    draw.text((mid_right - 4, margin + 22), '원', font=font(46, BOLD), fill=ink + (255,), anchor='ra')

    # Paper is never perfectly even.
    grain = rng.normal(0, 3.0, (h, w, 1)).repeat(3, axis=2)
    pixels = np.clip(np.asarray(image.convert('RGB'), dtype=float) + grain, 0, 255).astype(np.uint8)
    Image.fromarray(pixels).save(os.path.join(art, f'note_{value}.png'), optimize=True)


# ---------- Coins ----------

# (value, metal, numeral size). 10 won is copper coloured, 50 won a little yellow, the rest white metal.
COINS = [
    (500, (203, 204, 200), 0.36),
    (100, (190, 192, 192), 0.36),
    (50, (204, 197, 172), 0.42),
    (10, (178, 112, 76), 0.42),
]


def coin(value, metal, numeral):
    s = 256
    c = s / 2
    light = mix(metal, (255, 255, 255), 0.5)
    shade = mix(metal, (0, 0, 0), 0.42)
    # Outside the disc is the same metal: the mesh's edge samples it.
    image = Image.new('RGB', (s, s), mix(metal, (0, 0, 0), 0.18))
    draw = ImageDraw.Draw(image)
    draw.ellipse((2, 2, s - 3, s - 3), fill=metal)
    # Raised rim and a ring of beads inside it.
    draw.ellipse((2, 2, s - 3, s - 3), outline=light, width=5)
    draw.ellipse((14, 14, s - 15, s - 15), outline=shade, width=3)
    for i in range(48):
        a = i / 48 * 2 * math.pi
        x, y = c + 100 * math.cos(a), c + 100 * math.sin(a)
        draw.ellipse((x - 2.5, y - 2.5, x + 2.5, y + 2.5), fill=shade)
    # The number struck into the face: a light edge on one side of it and a dark one on the other.
    text = str(value)
    f = font(s * numeral, HEAVY)
    draw.text((c - 2, c - 6 - 2), text, font=f, fill=light, anchor='mm')
    draw.text((c + 2, c - 6 + 2), text, font=f, fill=shade, anchor='mm')
    draw.text((c, c - 6), text, font=f, fill=mix(metal, (0, 0, 0), 0.22), anchor='mm')
    draw.text((c, c + s * 0.25), '원', font=font(s * 0.13, BOLD), fill=shade, anchor='mm')
    # Brushed metal: faint streaks one way.
    streaks = rng.normal(0, 5.0, (s, 1, 1)).repeat(s, axis=1).repeat(3, axis=2) + rng.normal(0, 2.0, (s, s, 1)).repeat(3, axis=2)
    pixels = np.clip(np.asarray(image, dtype=float) + streaks, 0, 255).astype(np.uint8)
    Image.fromarray(pixels).save(os.path.join(art, f'coin_{value}.png'), optimize=True)


# ---------- Labels on the coin cups ----------

def label(value):
    """A strip from a label printer, stuck on the lip of the cup."""
    w, h = 192, 72
    image = Image.new('RGB', (w, h), (246, 246, 242))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, w - 1, h - 1), outline=(205, 205, 200), width=2)
    draw.text((w / 2, h / 2 - 1), str(value), font=font(50, HEAVY), fill=(24, 26, 30), anchor='mm')
    image.save(os.path.join(art, f'label_{value}.png'), optimize=True)


# ---------- Sounds ----------

SR = 44100


def env(n, attack, release):
    t = np.arange(n) / SR
    return np.clip(t / max(attack, 1e-4), 0, 1) * np.exp(-t / release)


def tone(freq, seconds, partials, attack=0.001, release=0.1):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    return sum(wt * np.sin(2 * np.pi * freq * k * t) for k, wt in partials) * env(n, attack, release)


def noise(seconds, lo=None, hi=None):
    x = rng.standard_normal(int(seconds * SR))
    if lo and hi:
        sos = butter(4, [lo, hi], btype='band', fs=SR, output='sos')
    elif hi:
        sos = butter(4, hi, btype='low', fs=SR, output='sos')
    else:
        sos = butter(4, lo, btype='high', fs=SR, output='sos')
    return sosfilt(sos, x)


def layer(*parts):
    """Each part is (start seconds, samples)."""
    n = max(int(start * SR) + len(y) for start, y in parts)
    out = np.zeros(n)
    for start, y in parts:
        i = int(start * SR)
        out[i:i + len(y)] += y
    return out


def save(name, y, peak_db):
    y = np.asarray(y, dtype=float)
    f = int(0.004 * SR)
    y[:f] *= np.linspace(0, 1, f)
    y[-f:] *= np.linspace(1, 0, f)
    y = y / (np.max(np.abs(y)) or 1.0) * (10 ** (peak_db / 20))
    wavfile.write(os.path.join(audio, name + '.wav'), SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))


def sounds():
    bell = ((1, 1.0), (2.76, 0.55), (5.4, 0.3), (8.93, 0.14))
    # The drawer let go: the latch, the till bell, the tray rolling out on its runners, and the stop at the end.
    latch = noise(0.03, 900, 5200) * env(int(0.03 * SR), 0.0005, 0.007)
    ring = tone(2350, 0.7, bell, release=0.2) * 0.55
    n = int(0.24 * SR)
    roll = noise(0.24, 180, 1600) * np.sin(np.linspace(0, math.pi, n)) ** 0.6 * 0.55
    stop = layer((0, tone(130, 0.12, ((1, 1.0), (2.3, 0.4)), release=0.03)), (0, noise(0.05, 500, 3000) * env(int(0.05 * SR), 0.0005, 0.012) * 0.5))
    rattle = sum(tone(f, 0.12, ((1, 1.0), (2.7, 0.4)), release=0.03) * a for f, a in ((3900, 0.16), (5200, 0.12), (4600, 0.1)))
    save('sfx_drawer_open', layer((0, latch), (0.012, ring), (0.03, roll), (0.27, stop), (0.275, rattle)), -4)

    # A coin against other coins: two short high rings a few milliseconds apart.
    clink = layer((0, tone(4300, 0.16, ((1, 1.0), (1.48, 0.6), (2.31, 0.35)), release=0.035)),
                  (0.022, tone(5100, 0.14, ((1, 0.8), (1.52, 0.5)), release=0.03) * 0.7),
                  (0, noise(0.012, 2500, 9000) * env(int(0.012 * SR), 0.0003, 0.003) * 0.6))
    save('sfx_coin', clink, -9)

    # A note: a short dry rustle and the snap of paper.
    n = int(0.14 * SR)
    rustle = noise(0.14, 2200, 9000) * (np.sin(np.linspace(0, math.pi, n)) ** 2) * (0.6 + 0.4 * np.sin(np.linspace(0, 9 * math.pi, n)) ** 2)
    snap = noise(0.02, 1500, 7000) * env(int(0.02 * SR), 0.0004, 0.005)
    save('sfx_note', layer((0, rustle * 0.7), (0.1, snap)), -12)


for spec in NOTES:
    note(*spec)
for spec in COINS:
    coin(*spec)
for value, *_ in COINS:
    label(value)
sounds()
print('cash assets written to', art, 'and', audio)
