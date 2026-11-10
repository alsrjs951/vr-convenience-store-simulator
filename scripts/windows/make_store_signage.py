"""Generates the store's printed and painted surfaces (no downloads, no product pictures): the floor with its light and
shadow painted in, the wall shading, price rails, posters, hanging signs, fridge door films and floor stickers.

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

BOLD = 'C:/Windows/Fonts/malgunbd.ttf'
REGULAR = 'C:/Windows/Fonts/malgun.ttf'

NAVY = (4, 30, 66)
WHITE = (250, 250, 247)
BLACK = (26, 26, 28)
YELLOW = (255, 208, 0)
RED = (214, 40, 40)
SKY = (120, 180, 230)
GREY = (120, 126, 130)


def font(size, bold=True):
    return ImageFont.truetype(BOLD if bold else REGULAR, size)


def save(image, name):
    image.convert('RGB').save(os.path.join(out, name + '.png'), optimize=True)


def centered(draw, width, y, text, size, fill, bold=True):
    draw.text((width / 2, y), text, font=font(size, bold), fill=fill, anchor='mm')


def fitted(draw, text, max_width, size, bold=True):
    """The largest size at or below size that keeps the text within max_width."""
    while size > 8 and draw.textlength(text, font=font(size, bold)) > max_width:
        size -= 1
    return size


# ---------- Floor: tiles with the room's light and contact shadows painted in ----------
# Room x -6..4.5, z -2.5..5.5. Image row 0 is z = 5.5 and column 0 is x = -6, which is how a floor quad maps it.

ROOM_X0, ROOM_Z0, ROOM_W, ROOM_D = -6.0, -2.5, 10.5, 8.0
FLOOR_W, FLOOR_H = 4096, 3120
LOW = 4  # the light and shadow fields are smooth, so they are made small and enlarged


def field_axes(width, height):
    x = ROOM_X0 + (np.arange(width) + 0.5) / width * ROOM_W
    z = ROOM_Z0 + ROOM_D - (np.arange(height) + 0.5) / height * ROOM_D
    return np.meshgrid(x, z)


def resize_field(field, width, height):
    return np.asarray(Image.fromarray(field.astype(np.float32), 'F').resize((width, height), Image.BICUBIC), np.float32)


def blurred(mask, radius_m, width):
    radius_px = radius_m / ROOM_W * width
    return gaussian_filter(mask.astype(np.float32), radius_px, mode='nearest')


low_w, low_h = FLOOR_W // LOW, FLOOR_H // LOW
lx, lz = field_axes(low_w, low_h)

# Bands of light under the rows of strip lights (P3StoreDressing.LightRows, LightSpans), over a dimmer room, and the
# polished floor's mirror image of each strip as a narrower, brighter streak.
light_rows = (-5.1, -3.7, -2.3, -0.9, 0.5, 1.9, 3.3)
light_spans = ((-1.2, 0.95), (1.1, 3.25), (3.4, 5.3))


def strip_field(xs, zs, width):
    field = np.zeros_like(xs, np.float32)
    for row in light_rows:
        for z0, z1 in light_spans:
            beyond = np.maximum(np.maximum(z0 - zs, zs - z1), 0)
            field += np.exp(-((xs - row) ** 2 + beyond ** 2) / (2 * width ** 2))
    return field


light = strip_field(lx, lz, 0.75)
light = 0.78 + 0.26 * np.clip(light, 0, 1.3) / 1.3
sheen = strip_field(lx, lz, 0.16)

# Contact shadow where furniture stands on the floor. x0, x1, z0, z1 in metres.
solids = [
    (-3.4, -0.4, 2.95, 3.45), (-4.05, -2.55, 0.55, 1.05),   # gondola row A + B, row D
    (-2.55, -2.23, 0.55, 1.05),                             # row D's end bay
    (-3.9, -3.4, 3.0, 3.5), (-0.4, 0.1, 3.0, 3.5),          # box stands
    (-1.0, 2.0, 0.35, 0.95),                                # counter with its extension
    (-5.94, -5.0, -1.55, 3.19),                             # fridges and their filler
    (-5.94, -5.44, 3.25, 4.55),                             # microwave cabinet
    (-3.8, 4.2, 4.6, 5.44), (-5.44, -3.82, 4.6, 5.44),      # chilled cases, corner cupboard
    (-1.7, 1.85, -1.75, -1.35),                             # tobacco wall
    (0.35, 1.1, 2.75, 4.15),                                # chest freezer
    (3.99, 4.44, 1.4, 4.42),                                # shelf C
    (2.95, 4.05, -1.85, -1.25), (2.3, 2.9, -1.9, -1.25),    # cart, empty-box bin
    (-1.2, -0.8, -0.9, -0.5),                               # waste bin
]
occupied = np.zeros((low_h, low_w), np.float32)
for x0, x1, z0, z1 in solids:
    occupied[(lx >= x0) & (lx <= x1) & (lz >= z0) & (lz <= z1)] = 1.0
walls = np.zeros((low_h, low_w), np.float32)
walls[(lx < -5.94) | (lx > 4.44) | (lz < -2.44) | (lz > 5.44)] = 1.0
tight = blurred(occupied, 0.05, low_w)
wide = blurred(occupied, 0.28, low_w)
wall_wide = blurred(walls, 0.35, low_w)
shade = (1 - 0.62 * np.clip(tight * 1.3, 0, 1)) * (1 - 0.34 * np.clip(wide * 1.4, 0, 1)) * (1 - 0.22 * np.clip(wall_wide * 1.6, 0, 1))
shade[occupied > 0.5] = 0.3

lighting = resize_field(light * shade, FLOOR_W, FLOOR_H)
reflection = resize_field(sheen * (1 - np.clip(wide * 1.6, 0, 1)), FLOOR_W, FLOOR_H)

fx, fz = field_axes(FLOOR_W, FLOOR_H)
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
floor *= lighting[..., None]
floor += reflection[..., None] * np.array([0.21, 0.21, 0.205], np.float32)
# The strip of pavement outside the street front.
outside = (fx < -1.75) & (fz < -1.6)
pavement = (0.5 + (mottle[outside] - 0.5) * 0.12)
floor[outside] = np.stack([pavement, pavement, pavement * 0.97], -1)
save(Image.fromarray((np.clip(floor, 0, 1) * 255 + 0.5).astype(np.uint8)), 'floor_baked')
del floor, lighting, reflection, fx, fz, x_in, z_in, edge, mottle, bevel, tile_tone, tile_col, tile_row, dark_chips, light_chips, outside, pavement

# ---------- Ceiling: one 60 cm T-bar tile, speckled board in a slim metal grid; the material repeats it ----------

w, h = 256, 256
board = np.full((h, w, 3), 0.93, np.float32)
board -= gaussian_filter(rng.random((h, w), dtype=np.float32), 0.7)[..., None] * 0.05
holes = gaussian_filter((rng.random((h, w)) < 0.05).astype(np.float32), 0.6)
board -= holes[..., None] * 0.28
board[:5, :] = board[-5:, :] = 0.8
board[:, :5] = board[:, -5:] = 0.8
save(Image.fromarray((np.clip(board, 0, 1) * 255 + 0.5).astype(np.uint8)), 'ceiling_tile')

# ---------- Walls: one tall strip, dark at the floor and a little dark at the ceiling (row 0 is the top) ----------

h = np.linspace(1.0, 0.0, 1024, dtype=np.float32)   # 1 at the ceiling
tone = 1.0 - 0.26 * np.exp(-(h / 0.09) ** 1.2) - 0.09 * np.exp(-((1 - h) / 0.12) ** 1.2) - 0.05 * (1 - h)
wall = np.stack([tone * 1.0, tone * 0.988, tone * 0.965], -1)
wall = np.tile(wall[:, None, :], (1, 8, 1))
save(Image.fromarray((np.clip(wall, 0, 1) * 255 + 0.5).astype(np.uint8)), 'wall_shade')


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
        size = fitted(draw, label, 118, 22)
        draw.text((x0 + 9, height / 2), label, font=font(size, False), fill=BLACK, anchor='lm')
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


# ---------- Posters: words and colour only, never a picture of a product ----------

def poster_frame(width, height, background):
    image = Image.new('RGB', (width, height), background)
    return image, ImageDraw.Draw(image)


def footer(draw, width, height, fill=NAVY, text=WHITE):
    draw.rectangle((0, height - 96, width, height), fill=fill)
    centered(draw, width, height - 48, '동아편의점', 50, text)


def event_poster(name, big, top, line1, line2, background, ink, band):
    w, h = 768, 1088
    image, draw = poster_frame(w, h, background)
    draw.rectangle((0, 0, w, 150), fill=band)
    centered(draw, w, 75, top, 74, WHITE)
    centered(draw, w, 450, big, 300, ink)
    centered(draw, w, 760, line1, 62, BLACK if background == YELLOW else WHITE)
    centered(draw, w, 850, line2, 46, BLACK if background == YELLOW else WHITE, False)
    footer(draw, w, h)
    save(image, name)


event_poster('poster_1plus1', '1+1', '이번 주 행사', '하나 사면 하나 더', '라면 · 과자 코너', YELLOW, RED, RED)
event_poster('poster_2plus1', '2+1', '골라 담기', '세 개 담고 두 개 값', '음료 · 유제품 코너', RED, YELLOW, NAVY)


def notice_poster(name, title, lines, background, ink, accent):
    w, h = 768, 1088
    image, draw = poster_frame(w, h, background)
    draw.rectangle((0, 0, w, 22), fill=accent)
    y = 190
    for row in title:
        centered(draw, w, y, row, fitted(draw, row, w - 90, 96), ink)
        y += 130
    draw.rectangle((90, y - 20, w - 90, y - 14), fill=accent)
    y += 70
    for row in lines:
        centered(draw, w, y, row, fitted(draw, row, w - 100, 50, False), ink, False)
        y += 78
    footer(draw, w, h)
    save(image, name)


notice_poster('poster_recruit', ['함께 일할', '알바생 모집'], ['주말 · 야간 근무', '초보 환영, 3일이면 충분해요', '문의: 점장'], WHITE, NAVY, YELLOW)
notice_poster('poster_nineteen', ['19세 미만', '판매 금지'], ['술 · 담배는', '신분증을 확인합니다', '양해 부탁드립니다'], YELLOW, BLACK, RED)
notice_poster('poster_service', ['택배 접수', '교통카드 충전'], ['공과금 수납', '휴대폰 충전 서비스', '계산대에 문의하세요'], NAVY, WHITE, YELLOW)
notice_poster('poster_expiry', ['유통기한', '매일 확인'], ['기한이 지난 상품은', '바로 폐기합니다', '믿고 드세요'], WHITE, RED, RED)


def endcap(name, title, sub, background, ink):
    w, h = 512, 1024
    image, draw = poster_frame(w, h, background)
    y = 300
    for ch in title:
        centered(draw, w, y, ch, 150, ink)
        y += 180
    centered(draw, w, h - 150, sub, fitted(draw, sub, w - 50, 46, False), ink, False)
    save(image, name)


endcap('endcap_noodle', '라면', '끓여 먹는 든든함', RED, WHITE)
endcap('endcap_snack', '과자', '오늘의 간식', YELLOW, NAVY)
endcap('endcap_ready', '즉석', '데우면 바로 한 끼', NAVY, WHITE)
endcap('endcap_daily', '생활', '급할 때 여기에서', SKY, NAVY)


# ---------- Hanging signs ----------

def hanging(name, big, small, background, ink):
    w, h = 1024, 512
    image, draw = poster_frame(w, h, background)
    centered(draw, w, 210, big, fitted(draw, big, w - 80, 190), ink)
    centered(draw, w, 410, small, fitted(draw, small, w - 120, 66, False), ink, False)
    save(image, name)


hanging('hang_event', '1+1 행사', '이번 주 라면 · 과자', YELLOW, RED)
hanging('hang_drinks', '시원한 음료', '안쪽 냉장고에 있어요', NAVY, WHITE)
hanging('hang_new', '신상품 입고', '매일 새로 채웁니다', RED, WHITE)
hanging('hang_two', '2+1 골라 담기', '음료 · 유제품', WHITE, NAVY)


# ---------- Fridge door film: cold, misted glass with a category header, so the bare shelves behind do not show ----------

def fridge(name, title, sub, tint, badge=None):
    w, h = 512, 1152
    top = np.array(tint, np.float32) / 255.0
    bottom = np.array((236, 244, 250), np.float32) / 255.0
    t = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
    gradient = np.tile(top * (1 - t) + bottom * t, (1, w, 1))
    mist = np.asarray(Image.fromarray((rng.random((9, 4)) * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC), np.float32) / 255.0
    gradient = gradient * (0.96 + 0.06 * mist[..., None])
    # Beads of condensation: small bright dots, thicker toward the bottom of the door.
    dots = rng.random((h, w), dtype=np.float32) < (0.0009 + 0.0035 * t[:, 0, 0][:, None])
    bead = np.asarray(Image.fromarray((dots * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.9)), np.float32) / 255.0
    gradient = np.clip(gradient + bead[..., None] * 0.9, 0, 1)
    image = Image.fromarray((gradient * 255 + 0.5).astype(np.uint8))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, w, 170), fill=NAVY)
    centered(draw, w, 85, title, fitted(draw, title, w - 50, 92), WHITE)
    centered(draw, w, 240, sub, fitted(draw, sub, w - 60, 42, False), NAVY, False)
    if badge:
        draw.ellipse((w / 2 - 150, 410, w / 2 + 150, 710), fill=RED)
        centered(draw, w, 560, badge, 130, WHITE)
    save(image, name)


fridge('fridge_soda', '탄산음료', '톡 쏘는 시원함', (150, 205, 235))
fridge('fridge_juice', '주스 · 이온', '갈증 날 때', (250, 205, 130), '2+1')
fridge('fridge_coffee', '커피', '캔 · 페트 · 컵', (190, 165, 140))
fridge('fridge_milk', '우유 · 요거트', '매일 신선하게', (205, 225, 245), '1+1')
fridge('fridge_water', '생수', '500ml · 2L', (170, 215, 240))


# ---------- On the floor and small things ----------

w, h = 1024, 512
image, draw = poster_frame(w, h, NAVY)
draw.rectangle((16, 16, w - 16, h - 16), outline=YELLOW, width=10)
centered(draw, w, 190, '어서 오세요', 130, WHITE)
centered(draw, w, 345, '동아편의점', 86, YELLOW)
save(image, 'floor_mat')

w, h = 512, 512
image, draw = poster_frame(w, h, YELLOW)
draw.rectangle((10, 10, w - 10, h - 10), outline=BLACK, width=14)
centered(draw, w, 190, '여기서', 100, BLACK)
centered(draw, w, 320, '기다려 주세요', 62, BLACK)
save(image, 'floor_queue')

for name, text, sub in (('carton_snack', '스낵', '24입'), ('carton_noodle', '라면', '40입'), ('carton_daily', '생활용품', '12입'), ('carton_ready', '즉석식품', '20입')):
    w, h = 256, 128
    image, draw = poster_frame(w, h, WHITE)
    draw.rectangle((0, 0, w, 16), fill=NAVY)
    centered(draw, w, 62, text, fitted(draw, text, w - 24, 50), NAVY)
    centered(draw, w, 106, sub, 26, GREY, False)
    save(image, name)

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


# ---------- Tobacco wall: colour blocks with shelf numbers, no pack pictures and no brands (D9) ----------

def tobacco_grid(name, start):
    w, h = 1560, 1250
    rows, cols = 5, 13
    palette = [(238, 240, 244), (206, 216, 232), (70, 110, 170), (28, 62, 120), (190, 42, 48), (232, 190, 80), (120, 160, 120), (150, 150, 158), (56, 58, 66), (240, 226, 190)]
    image = Image.new('RGB', (w, h), (18, 20, 26))
    draw = ImageDraw.Draw(image)
    row_h = h / rows
    cell = w / cols
    number = start
    local = np.random.default_rng(start)
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
            ink = BLACK if sum(base) > 400 else WHITE
            draw.text(((x0 + x1) / 2, y0 + (y1 - y0) * 0.64), str(number), font=font(46), fill=ink, anchor='mm')
            number += 1
    save(image, name)


tobacco_grid('tobacco_grid_1', 1)
tobacco_grid('tobacco_grid_2', 66)

w, h = 2048, 472
top = np.array((8, 24, 58), np.float32) / 255.0
bottom = np.array((20, 66, 130), np.float32) / 255.0
t = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
x = np.linspace(-1, 1, w, dtype=np.float32)[None, :, None]
glow = np.exp(-((x - 0.45) ** 2) / 0.09) * 0.24
gradient = np.clip(top * (1 - t) + bottom * t + glow, 0, 1)
image = Image.fromarray((gradient * 255 + 0.5).astype(np.uint8))
draw = ImageDraw.Draw(image)
draw.text((90, 180), '언제나 가까운 동아편의점', font=font(124), fill=WHITE, anchor='lm')
draw.rounded_rectangle((90, 300, 330, 396), radius=12, fill=YELLOW)
draw.text((210, 348), '안내', font=font(66), fill=NAVY, anchor='mm')
draw.text((370, 348), '19세 미만 담배 판매 금지', font=font(76, False), fill=WHITE, anchor='lm')
save(image, 'header_tobacco')

w, h = 256, 1980
image = Image.new('RGB', (w, h), NAVY)
draw = ImageDraw.Draw(image)
draw.rectangle((0, h - 320, w, h), fill=(240, 242, 246))
y = 200
for ch in '신분증을보여주세요':
    centered(draw, w, y, ch, 126, WHITE)
    y += 170
centered(draw, w, h - 200, '성인', 96, NAVY)
centered(draw, w, h - 90, '확인', 96, NAVY)
save(image, 'lightbox_tobacco')


# ---------- Other new prints ----------

w, h = 744, 600
image, draw = poster_frame(w, h, (42, 56, 130))
centered(draw, w, 140, '아이스크림', 120, WHITE)
centered(draw, w, 280, '할인', 120, WHITE)
draw.rounded_rectangle((110, 396, w - 110, 526), radius=60, fill=WHITE)
centered(draw, w, 460, '2개 3,500원', 80, RED)
save(image, 'poster_icecream')

w, h = 512, 724
image, draw = poster_frame(w, h, WHITE)
draw.rectangle((0, 0, w, 40), fill=RED)
centered(draw, w, 190, '따뜻한', 96, BLACK)
centered(draw, w, 330, '컵라면', 120, RED)
centered(draw, w, 480, '온수 무료', 62, BLACK, False)
centered(draw, w, 640, '동아편의점', 44, NAVY)
save(image, 'poster_hot_noodle')

image, draw = poster_frame(w, h, NAVY)
centered(draw, w, 220, '간편식', 124, WHITE)
centered(draw, w, 370, '데워 드려요', 70, YELLOW)
centered(draw, w, 520, '전자레인지 이용', 52, WHITE, False)
save(image, 'poster_ready_meal')

w, h = 700, 780
image, draw = poster_frame(w, h, NAVY)
draw.ellipse((w / 2 - 130, 110, w / 2 + 130, 370), outline=WHITE, width=16)
draw.rectangle((w / 2 - 70, 190, w / 2 + 70, 290), fill=WHITE)
centered(draw, w, 560, '간편식', 170, WHITE)
save(image, 'sign_ready_meals')

w, h = 400, 560
image, draw = poster_frame(w, h, NAVY)
centered(draw, w, 130, '동아', 104, WHITE)
centered(draw, w, 250, '멤버십', 92, YELLOW)
centered(draw, w, 400, '포인트 적립하고', 42, WHITE, False)
centered(draw, w, 462, '혜택 받으세요', 42, WHITE, False)
save(image, 'sign_membership')

w, h = 256, 256
image, draw = poster_frame(w, h, WHITE)
draw.ellipse((22, 22, w - 22, h - 22), outline=NAVY, width=16)
centered(draw, w, h / 2, '동아', 74, NAVY)
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


# ---------- Chilled case decks: stackable delivery trays with their content written on them, no food shown (D9) ----------

def tray_strip(name, label, colour):
    w, h = 1024, 96
    image = Image.new('RGB', (w, h), colour)
    draw = ImageDraw.Draw(image)
    light = tuple(min(255, c + 34) for c in colour)
    dark = tuple(max(0, c - 44) for c in colour)
    draw.rectangle((0, 0, w, 12), fill=light)
    draw.rectangle((0, h - 8, w, h), fill=dark)
    n = 5
    cell = w / n
    for i in range(n):
        x0 = i * cell + 7
        x1 = (i + 1) * cell - 7
        draw.rounded_rectangle((x0, 18, x1, h - 13), radius=6, outline=dark, width=3)
        draw.rounded_rectangle((x0 + 34, 28, x1 - 34, h - 22), radius=4, fill=WHITE)
        draw.text(((x0 + x1) / 2, 53), label, font=font(fitted(draw, label, x1 - x0 - 90, 34)), fill=NAVY, anchor='mm')
    save(image, name)


tray_strip('tray_lunchbox', '도시락', (58, 96, 150))
tray_strip('tray_kimbap', '김밥', (70, 130, 112))
tray_strip('tray_sandwich', '샌드위치', (160, 116, 62))
tray_strip('tray_salad', '샐러드', (96, 140, 76))
tray_strip('tray_milk', '유제품', (110, 138, 176))

print('wrote', len([n for n in os.listdir(out) if n.endswith('.png')]), 'textures')
