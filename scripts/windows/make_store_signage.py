"""Generates the store's printed and painted surfaces (no downloads, no product pictures): the terrazzo floor and the
pavement, price rails, the few notices, fridge door films and floor stickers. Light and shadow are not painted in.

Words only where a real store has them (2026-11-10): the store name, section names, price tags and the legal notices.
Slogans, event posters, end-cap boards and labels that only filled an empty place are gone; an empty place stays plain.
The type is Pretendard (Assets/Resources/Fonts, SIL OFL), the same family the game's UI uses.

Everything is drawn into opaque textures on purpose. World-space UI draws over the splat products, because splats write no
depth, so anything printed that a held product can pass in front of has to be an ordinary opaque surface.

Usage: python -I scripts/windows/make_store_signage.py Assets/Art/Signage
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from scipy.ndimage import gaussian_filter

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(11)

FONTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'Resources', 'Fonts')
REGULAR = os.path.join(FONTS, 'Pretendard-Regular.ttf')
BOLD = os.path.join(FONTS, 'Pretendard-Bold.ttf')
HEAVY = os.path.join(FONTS, 'Pretendard-ExtraBold.ttf')   # sign titles

NAVY = (4, 30, 66)
WHITE = (250, 250, 247)
BLACK = (26, 26, 28)
YELLOW = (255, 208, 0)
RED = (214, 40, 40)


def font(size, face=BOLD):
    return ImageFont.truetype(face, size)


def save(image, name):
    image.convert('RGB').save(os.path.join(out, name + '.png'), optimize=True)


def centered(draw, width, y, text, size, fill, face=BOLD):
    draw.text((width / 2, y), text, font=font(size, face), fill=fill, anchor='mm')


def fitted(draw, text, max_width, size, face=BOLD):
    """The largest size at or below size that keeps the text within max_width."""
    while size > 8 and draw.textlength(text, font=font(size, face)) > max_width:
        size -= 1
    return size


def nineteen(draw, cx, cy, radius):
    """The under-19 mark: a red ring and stroke with 19 over them, so the number stays readable."""
    draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=WHITE, outline=RED, width=max(3, round(radius * 0.17)))
    reach = radius * 0.64
    draw.line((cx - reach, cy - reach, cx + reach, cy + reach), fill=RED, width=max(2, round(radius * 0.1)))
    draw.text((cx, cy), '19', font=font(round(radius * 1.0), HEAVY), fill=BLACK, anchor='mm')


# ---------- Floor: polished terrazzo tiles, plain. Light and shadow are baked in Unity (P3StoreLighting) ----------
# Room x -6..4.5, z -2.5..5.5. Image row 0 is z = 5.5 and column 0 is x = -6, which is how a floor quad maps it.

ROOM_X0, ROOM_Z0, ROOM_W, ROOM_D = -6.0, -2.5, 10.5, 8.0
FLOOR_W, FLOOR_H = 4096, 3120

fx, fz = np.meshgrid(ROOM_X0 + (np.arange(FLOOR_W) + 0.5) / FLOOR_W * ROOM_W, ROOM_Z0 + ROOM_D - (np.arange(FLOOR_H) + 0.5) / FLOOR_H * ROOM_D)
TILE = 0.6
x_in = (fx - ROOM_X0) % TILE
z_in = (fz - ROOM_Z0) % TILE
edge = np.minimum(np.minimum(x_in, TILE - x_in), np.minimum(z_in, TILE - z_in)).astype(np.float32)
tile_col = ((fx - ROOM_X0) // TILE).astype(np.int32)
tile_row = ((fz - ROOM_Z0) // TILE).astype(np.int32)
tile_tone = rng.uniform(-0.03, 0.03, (32, 32)).astype(np.float32)[tile_row % 32, tile_col % 32]
mottle = np.asarray(Image.fromarray((rng.random((156, 205)) * 255).astype(np.uint8)).resize((FLOOR_W, FLOOR_H), Image.BICUBIC), np.float32) / 255.0
# Polished grey terrazzo: a mid grey with dark and light chips in it.
dark_chips = gaussian_filter((rng.random((FLOOR_H, FLOOR_W)) < 0.018).astype(np.float32), 0.8)
light_chips = gaussian_filter((rng.random((FLOOR_H, FLOOR_W)) < 0.012).astype(np.float32), 0.8)
bevel = np.clip((edge - 0.0012) / 0.0025, 0, 1)
floor = np.stack([np.full_like(edge, 0.585), np.full_like(edge, 0.585), np.full_like(edge, 0.57)], -1)
floor *= (1.0 + (mottle - 0.5) * 0.11 + tile_tone - dark_chips * 1.4 + light_chips * 1.5)[..., None]
grout = np.array([0.42, 0.42, 0.41], np.float32)
floor = floor * bevel[..., None] + grout * (1 - bevel[..., None])
# The strip of pavement outside the street front.
outside = (fx < -1.75) & (fz < -1.6)
pavement = (0.5 + (mottle[outside] - 0.5) * 0.12)
floor[outside] = np.stack([pavement, pavement, pavement * 0.97], -1)
save(Image.fromarray((np.clip(floor, 0, 1) * 255 + 0.5).astype(np.uint8)), 'floor_terrazzo')
del floor, fx, fz, x_in, z_in, edge, mottle, bevel, tile_tone, tile_col, tile_row, dark_chips, light_chips, outside, pavement

# ---------- Ceiling: one 60 cm T-bar tile, speckled board in a slim metal grid; the material repeats it ----------

w, h = 256, 256
board = np.full((h, w, 3), 0.93, np.float32)
board -= gaussian_filter(rng.random((h, w), dtype=np.float32), 0.7)[..., None] * 0.05
holes = gaussian_filter((rng.random((h, w)) < 0.05).astype(np.float32), 0.6)
board -= holes[..., None] * 0.28
board[:5, :] = board[-5:, :] = 0.8
board[:, :5] = board[:, -5:] = 0.8
save(Image.fromarray((np.clip(board, 0, 1) * 255 + 0.5).astype(np.uint8)), 'ceiling_tile')


# ---------- Price rails: 1.46 m by 4.2 cm on a navy strip, one white tag per product ----------

def rail(name, tags, width=2048, height=64):
    """tags: (position along the rail in 0..1, label, price, on sale)."""
    image = Image.new('RGB', (width, height), NAVY)
    draw = ImageDraw.Draw(image)
    tag_w = 236
    for position, label, price, sale in tags:
        x = int(position * width)
        x0, x1 = max(4, x - tag_w // 2), min(width - 4, x + tag_w // 2)
        draw.rounded_rectangle((x0, 5, x1, height - 6), radius=5, fill=YELLOW if sale else WHITE)
        size = fitted(draw, label, 118, 22, REGULAR)
        draw.text((x0 + 9, height / 2), label, font=font(size, REGULAR), fill=BLACK, anchor='lm')
        draw.text((x1 - 9, height / 2), price, font=font(30), fill=RED if sale else BLACK, anchor='rm')
    save(image, name)


def spread(names, offset=0.0):
    """Evenly spaced tags, every third one a sale."""
    step = 1.0 / len(names)
    return [((i + 0.5) * step + offset, n, p, i % 3 == 2) for i, (n, p) in enumerate(names)]


# Gondola x runs -0.73..0.73 around each centre; tags sit under the delivery and stock slots (P2StoreBuilder.BuildIntake).
def u(x, centre):
    return (x - (centre - 0.73)) / 1.46


A, B = -2.2, 2.2
rail('rail_a_1000', [(u(-2.44, A), '신라면', '1,000', False), (u(-1.86, A), '치토스', '1,700', False)])
rail('rail_a_1400', [(u(-2.62, A), '컵라면', '1,300', False), (u(-2.22, A), '볶음면', '1,500', True), (u(-1.86, A), '오징어땅콩', '1,800', False)])
rail('rail_b_1000', [(u(1.62, B), '즉석밥', '1,800', False), (u(2.07, B), '오감자', '1,800', False), (u(2.55, B), '고래밥', '2,500', False)])
rail('rail_b_1400', spread([('즉석카레', '1,500'), ('즉석국', '2,200'), ('통조림', '3,200'), ('시리얼', '4,500'), ('즉석죽', '3,000')]))
rail('rail_lower_snack', spread([('봉지과자', '1,700'), ('감자칩', '1,800'), ('크래커', '1,500'), ('초콜릿', '1,200'), ('쿠키', '2,300'), ('젤리', '1,000')]))
rail('rail_lower_noodle', spread([('봉지라면', '1,000'), ('컵라면', '1,300'), ('쌀국수', '1,800'), ('짜장라면', '1,300'), ('우동', '1,500'), ('냉면', '2,000')]))
rail('rail_back_a', spread([('사탕', '800'), ('껌', '1,000'), ('견과류', '3,000'), ('육포', '4,000'), ('오징어채', '3,500'), ('비스킷', '1,700')]))
rail('rail_back_b', spread([('휴지', '2,500'), ('물티슈', '1,500'), ('건전지', '3,000'), ('양말', '3,000'), ('우산', '6,000'), ('마스크', '1,500')]))
rail('rail_shelf_c', spread([('칫솔', '2,000'), ('치약', '3,000'), ('면도기', '4,000'), ('세제', '3,500'), ('키친타월', '2,800'), ('종이컵', '1,500'), ('수세미', '1,200'), ('고무장갑', '2,500')]), width=2048, height=56)


# ---------- The one notice on the wall: words and colour only, never a picture of a product ----------

def poster_frame(width, height, background):
    image = Image.new('RGB', (width, height), background)
    return image, ImageDraw.Draw(image)


w, h = 768, 1088
image, draw = poster_frame(w, h, WHITE)
draw.rectangle((0, 0, w, 22), fill=YELLOW)
centered(draw, w, 190, '함께 일할', 96, NAVY, HEAVY)
centered(draw, w, 320, '알바생 모집', 96, NAVY, HEAVY)
draw.rectangle((90, 430, w - 90, 436), fill=YELLOW)
for i, row in enumerate(('주말 · 야간 근무', '초보 환영, 3일이면 충분해요', '문의: 점장')):
    centered(draw, w, 520 + i * 78, row, 50, NAVY, REGULAR)
draw.rectangle((0, h - 96, w, h), fill=NAVY)
centered(draw, w, h - 48, '동아편의점', 50, WHITE)
save(image, 'poster_recruit')


# ---------- Fridge door film: cold, misted glass over the bare shelves, with nothing written on it ----------

w, h = 512, 1152
t = np.linspace(0, 1, h, dtype=np.float32)[:, None]   # 0 at the top of the door
x = np.linspace(-1, 1, w, dtype=np.float32)[None, :]
# The cabinet's own light falls off toward the floor and the door's edges.
tone = 0.9 - 0.1 * t - 0.05 * x ** 2
# The wire shelves behind the mist: a soft dark line each, and the light caught on the shelf above it.
for shelf in (0.2, 0.38, 0.56, 0.74, 0.92):
    tone = tone - 0.075 * np.exp(-((t - shelf) / 0.011) ** 2) + 0.025 * np.exp(-((t - shelf + 0.035) / 0.03) ** 2)
mist = np.asarray(Image.fromarray((rng.random((9, 4)) * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC), np.float32) / 255.0
tone = tone * (0.97 + 0.06 * mist)
film = tone[..., None] * np.array([0.9, 0.955, 1.0], np.float32)
# Beads of condensation: small bright dots, thicker toward the bottom of the door.
dots = rng.random((h, w), dtype=np.float32) < (0.0009 + 0.0035 * t)
bead = np.asarray(Image.fromarray((dots * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.9)), np.float32) / 255.0
save(Image.fromarray((np.clip(film + bead[..., None] * 0.9, 0, 1) * 255 + 0.5).astype(np.uint8)), 'fridge_mist')


# ---------- On the floor and small things ----------

w, h = 1024, 512
image, draw = poster_frame(w, h, NAVY)
draw.rectangle((16, 16, w - 16, h - 16), outline=YELLOW, width=10)
centered(draw, w, 190, '어서 오세요', 130, WHITE, HEAVY)
centered(draw, w, 345, '동아편의점', 86, YELLOW)
save(image, 'floor_mat')

w, h = 512, 512
image, draw = poster_frame(w, h, YELLOW)
draw.rectangle((10, 10, w - 10, h - 10), outline=BLACK, width=14)
centered(draw, w, 190, '여기서', 100, BLACK, HEAVY)
centered(draw, w, 320, '기다려 주세요', 62, BLACK)
save(image, 'floor_queue')

w, h = 512, 512
image = Image.new('RGB', (w, h), WHITE)
draw = ImageDraw.Draw(image)
draw.ellipse((6, 6, w - 6, h - 6), outline=NAVY, width=16)
cx, cy = w / 2, h / 2
for hour in range(1, 13):
    angle = np.radians(hour * 30 - 90)
    r = 190
    draw.text((cx + r * np.cos(angle), cy + r * np.sin(angle)), str(hour), font=font(50), fill=NAVY, anchor='mm')
for angle_deg, length, width in ((-90 + 30 * 9.0, 110, 16), (-90 + 6 * 2, 165, 10)):
    angle = np.radians(angle_deg)
    draw.line((cx, cy, cx + length * np.cos(angle), cy + length * np.sin(angle)), fill=BLACK, width=width)
draw.ellipse((cx - 14, cy - 14, cx + 14, cy + 14), fill=RED)
save(image, 'clock_face')

w, h = 512, 192
image, draw = poster_frame(w, h, (20, 150, 80))
centered(draw, w, h / 2, '비상구', 120, WHITE)
save(image, 'exit_sign')


# ---------- The street through the glass: shapes and colour only, soft as an out-of-focus background ----------

w, h = 2048, 1448
image = Image.new('RGB', (w, h), (214, 228, 240))
draw = ImageDraw.Draw(image)
for i in range(h // 2):
    t = i / (h / 2)
    draw.line((0, i, w, i), fill=(int(186 + 44 * t), int(212 + 26 * t), int(238 + 8 * t)))
for x, width, top, tone in ((0, 460, 240, 200), (420, 520, 330, 182), (900, 380, 180, 214), (1250, 430, 300, 190), (1650, 420, 220, 206)):
    draw.rectangle((x, top, x + width, 1040), fill=(tone, tone - 4, tone - 12))
    for row in range(top + 40, 980, 90):
        for col in range(x + 30, x + width - 40, 90):
            draw.rectangle((col, row, col + 50, row + 54), fill=(tone - 70, tone - 56, tone - 40))
draw.rectangle((0, 1040, w, 1200), fill=(136, 138, 142))
draw.rectangle((0, 1200, w, h), fill=(168, 168, 164))
for x, r in ((260, 190), (1000, 230), (1720, 200)):
    draw.rectangle((x - 16, 760, x + 16, 1130), fill=(92, 74, 58))
    draw.ellipse((x - r, 500, x + r, 880), fill=(88, 128, 76))
    draw.ellipse((x - r * 0.7, 420, x + r * 0.8, 760), fill=(108, 148, 86))
for x in range(0, w, 70):
    draw.rectangle((x, 1110, x + 7, 1230), fill=(70, 74, 80))
draw.rectangle((0, 1120, w, 1130), fill=(70, 74, 80))
save(image.filter(ImageFilter.GaussianBlur(5)), 'street_backdrop')


# ---------- Tobacco wall: plain colour blocks, no pack pictures and no brands (D9) ----------

def tobacco_grid(name, seed):
    w, h = 1560, 1250
    rows, cols = 5, 13
    palette = [(238, 240, 244), (206, 216, 232), (70, 110, 170), (28, 62, 120), (190, 42, 48), (232, 190, 80), (120, 160, 120), (150, 150, 158), (56, 58, 66), (240, 226, 190)]
    image = Image.new('RGB', (w, h), (18, 20, 26))
    draw = ImageDraw.Draw(image)
    row_h = h / rows
    cell = w / cols
    local = np.random.default_rng(seed)
    for r in range(rows):
        y0 = r * row_h + 34
        y1 = (r + 1) * row_h - 26
        for c in range(cols):
            x0 = c * cell + 9
            x1 = (c + 1) * cell - 9
            base = palette[int(local.integers(len(palette)))]
            accent = palette[int(local.integers(len(palette)))]
            draw.rectangle((x0, y0, x1, y1), fill=base)
            draw.rectangle((x0, y0, x1, y0 + (y1 - y0) * 0.26), fill=accent)
    save(image, name)


tobacco_grid('tobacco_grid_1', 1)
tobacco_grid('tobacco_grid_2', 66)

# The lit header over the bays: the store name and the notice the law asks for, small.
w, h = 2048, 472
top = np.array((8, 24, 58), np.float32) / 255.0
bottom = np.array((20, 66, 130), np.float32) / 255.0
t = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
x = np.linspace(-1, 1, w, dtype=np.float32)[None, :, None]
glow = np.exp(-((x - 0.45) ** 2) / 0.09) * 0.24
gradient = np.clip(top * (1 - t) + bottom * t + glow, 0, 1)
image = Image.fromarray((gradient * 255 + 0.5).astype(np.uint8))
draw = ImageDraw.Draw(image)
draw.text((100, 200), '동아편의점', font=font(132, HEAVY), fill=WHITE, anchor='lm')
nineteen(draw, 132, 364, 32)
draw.text((184, 364), '19세 미만 청소년에게 담배를 판매하지 않습니다', font=font(40, REGULAR), fill=WHITE, anchor='lm')
save(image, 'header_tobacco')

# The tall lightbox at the door end: the under-19 mark at eye height and nothing else.
w, h = 256, 1980
t = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
panel = np.tile(top * (1 - t) + bottom * t, (1, w, 1))
image = Image.fromarray((np.clip(panel, 0, 1) * 255 + 0.5).astype(np.uint8))
draw = ImageDraw.Draw(image)
nineteen(draw, w / 2, 1040, 84)
centered(draw, w, 1180, '신분증 확인', 40, WHITE)
save(image, 'lightbox_tobacco')


# ---------- Other prints ----------

w, h = 700, 780
image, draw = poster_frame(w, h, NAVY)
draw.ellipse((w / 2 - 130, 110, w / 2 + 130, 370), outline=WHITE, width=16)
draw.rectangle((w / 2 - 70, 190, w / 2 + 70, 290), fill=WHITE)
centered(draw, w, 560, '간편식', 170, WHITE, HEAVY)
save(image, 'sign_ready_meals')

w, h = 256, 256
image, draw = poster_frame(w, h, WHITE)
draw.ellipse((22, 22, w - 22, h - 22), outline=NAVY, width=16)
centered(draw, w, h / 2, '동아', 74, NAVY, HEAVY)
save(image, 'sign_bag_logo')

w, h = 256, 384
image, draw = poster_frame(w, h, (28, 30, 36))
draw.rectangle((18, 18, w - 18, 96), fill=(168, 190, 158))
draw.text((w - 34, 58), '3,500', font=font(50), fill=BLACK, anchor='rm')
for row in range(5):
    for col in range(4):
        x0 = 20 + col * 56
        y0 = 120 + row * 50
        draw.rounded_rectangle((x0, y0, x0 + 46, y0 + 38), radius=7, fill=(208, 210, 214) if col < 3 else (216, 120, 60))
save(image, 'device_calculator')


# ---------- Chilled case decks: the front of a row of stackable delivery crates, nothing written, no food shown (D9) ----------

CRATE = (70, 92, 118)   # P3StoreDetails' Tray_Body is the same colour
w, h = 1024, 96
image = Image.new('RGB', (w, h), CRATE)
draw = ImageDraw.Draw(image)
light = tuple(min(255, c + 30) for c in CRATE)
dark = tuple(max(0, c - 38) for c in CRATE)
draw.rectangle((0, 0, w, 12), fill=light)
draw.rectangle((0, h - 8, w, h), fill=dark)
cell = w / 5
for i in range(5):
    x0 = i * cell + 7
    x1 = (i + 1) * cell - 7
    draw.rounded_rectangle((x0, 18, x1, h - 13), radius=6, outline=dark, width=3)
    # A hand hole, and ribs either side of it.
    cx = (x0 + x1) / 2
    draw.rounded_rectangle((cx - 44, 32, cx + 44, 52), radius=10, fill=dark)
    for rib in (-78, -64, 64, 78):
        draw.line((cx + rib, 26, cx + rib, h - 20), fill=dark, width=3)
save(image, 'tray_crate')

print('wrote', len([n for n in os.listdir(out) if n.endswith('.png')]), 'textures')
