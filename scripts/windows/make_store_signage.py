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
# Room x -4.5..4.5, z -2.5..5.5. Image row 0 is z = 5.5 and column 0 is x = -4.5, which is how a floor quad maps it.

ROOM_X0, ROOM_Z0, ROOM_W, ROOM_D = -4.5, -2.5, 9.0, 8.0
FLOOR_W, FLOOR_H = 4096, 3640
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

# Pools of light under the ceiling panels (P3StoreDressing.Ceiling), over a dimmer room.
panels = [(x, z) for x in (-3.0, -1.0, 1.0, 3.0) for z in (1.9, 3.95)] + [(x, -1.1) for x in (-2.4, 0.0, 2.4)]
light = np.zeros((low_h, low_w), np.float32)
for px, pz in panels:
    light += np.exp(-((lx - px) ** 2 + (lz - pz) ** 2) / (2 * 0.95 ** 2))
light = 0.80 + 0.26 * np.clip(light, 0, 1.25) / 1.25

# Contact shadow where furniture stands on the floor. x0, x1, z0, z1 in metres.
solids = [
    (-2.95, -1.45, 2.95, 3.45), (1.45, 2.95, 2.95, 3.45),   # gondolas A, B
    (-3.45, -2.95, 3.0, 3.5), (2.95, 3.45, 3.0, 3.5),       # box stands
    (-1.0, 1.0, 0.35, 0.95),                                # counter
    (-4.3, 4.3, 4.495, 5.5),                                # fridges
    (3.99, 4.44, 2.6, 4.42), (3.94, 4.44, 1.05, 2.35),      # shelf C, microwave cabinet
    (2.95, 4.05, -1.85, -1.25), (2.3, 2.9, -1.9, -1.25),    # cart, empty-box bin
    (-1.2, -0.8, -0.9, -0.5),                               # waste bin
    (-4.44, -3.9, -1.9, -0.5),                              # stock shelf
]
occupied = np.zeros((low_h, low_w), np.float32)
for x0, x1, z0, z1 in solids:
    occupied[(lx >= x0) & (lx <= x1) & (lz >= z0) & (lz <= z1)] = 1.0
walls = np.zeros((low_h, low_w), np.float32)
walls[(lx < -4.44) | (lx > 4.44) | (lz < -2.44) | (lz > 5.44)] = 1.0
tight = blurred(occupied, 0.05, low_w)
wide = blurred(occupied, 0.28, low_w)
wall_wide = blurred(walls, 0.35, low_w)
shade = (1 - 0.62 * np.clip(tight * 1.3, 0, 1)) * (1 - 0.34 * np.clip(wide * 1.4, 0, 1)) * (1 - 0.22 * np.clip(wall_wide * 1.6, 0, 1))
shade[occupied > 0.5] = 0.3

lighting = resize_field(light * shade, FLOOR_W, FLOOR_H)

fx, fz = field_axes(FLOOR_W, FLOOR_H)
TILE = 0.6
x_in = (fx - ROOM_X0) % TILE
z_in = (fz - ROOM_Z0) % TILE
edge = np.minimum(np.minimum(x_in, TILE - x_in), np.minimum(z_in, TILE - z_in)).astype(np.float32)
tile_col = ((fx - ROOM_X0) // TILE).astype(np.int32)
tile_row = ((fz - ROOM_Z0) // TILE).astype(np.int32)
tile_tone = rng.uniform(-0.018, 0.018, (32, 32)).astype(np.float32)[tile_row % 32, tile_col % 32]
mottle = np.asarray(Image.fromarray((rng.random((128, 146)) * 255).astype(np.uint8)).resize((FLOOR_W, FLOOR_H), Image.BICUBIC), np.float32) / 255.0
bevel = np.clip((edge - 0.0016) / 0.003, 0, 1)
floor = np.stack([np.full_like(edge, 0.80), np.full_like(edge, 0.79), np.full_like(edge, 0.765)], -1)
floor *= (1.0 + (mottle - 0.5) * 0.045 + tile_tone)[..., None]
grout = np.array([0.50, 0.49, 0.47], np.float32)
floor = floor * bevel[..., None] + grout * (1 - bevel[..., None])
floor *= lighting[..., None]
save(Image.fromarray((np.clip(floor, 0, 1) * 255 + 0.5).astype(np.uint8)), 'floor_baked')
del floor, lighting, fx, fz, x_in, z_in, edge, mottle, bevel, tile_tone, tile_col, tile_row

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

print('wrote', len([n for n in os.listdir(out) if n.endswith('.png')]), 'textures')
