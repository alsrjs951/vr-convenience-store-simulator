"""Gives the Rocketbox customers the hair of the people who shop at a Korean convenience store, and a second set of
clothes each, so the same four models do not come back looking the same (2026-11-14).

Three of the four avatars are blond. The originals in Assets/ThirdParty/Rocketbox stay as downloaded; the repainted
textures go to Assets/Art/Generated/Customers, where P2CustomerSetup takes them from:
  <avatar>_head_color.png, <avatar>_opacity_color.png   dark hair (the hair cards keep their alpha)
  <avatar>_b_body_color.png                             the second look's clothes: the top in another colour

Blond hair and skin are nearly the same colour, so where the hair is in each head texture is written down here (HAIR)
rather than found.

  python make_customer_looks.py [--preview FOLDER]
"""
import argparse
import colorsys
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

PROJECT = Path(__file__).resolve().parents[2]
SOURCE = PROJECT / "Assets" / "ThirdParty" / "Rocketbox" / "Avatars"
OUT = PROJECT / "Assets" / "Art" / "Generated" / "Customers"

# name, texture prefix, hair colour (what the brightest strand becomes; None leaves the hair, which is brown already),
# how bright the hair is to begin with, and the top's new colour for the second look: (hue range of the top in degrees,
# new hue, saturation x, value x, and where in the picture the top is when trousers share its colour: x0, y0, x1, y1).
AVATARS = [
    ("Male_Adult_08", "m014", None, 0, ((190, 235), 0, 0.12, 0.42, None)),
    ("Male_Adult_16", "m019", (0.17, 0.15, 0.14), 0.80, ((195, 240), 95, 0.55, 0.85, [(0.22, 0.02, 0.655, 0.9), (0.0, 0.42, 0.22, 0.60), (0.67, 0.42, 1.0, 0.60)])),
    ("Female_Adult_01", "f001", (0.21, 0.17, 0.14), 0.80, ((325, 360), 40, 0.35, 1.12, None)),
    ("Female_Adult_17", "f006", (0.16, 0.14, 0.13), 0.80, ((110, 175), 44, 0.95, 1.2, None)),
]
# Where the hair ends on the left half of the head texture, from the back of the head (left edge) round the ear and up
# the temple to the middle of the forehead; read off the pictures. And a rough outline round the pieces of bun at the lower right.
HAIR = {
    "Male_Adult_16": ([(0.0, 0.40), (0.10, 0.40), (0.17, 0.385), (0.20, 0.36), (0.212, 0.315), (0.235, 0.295), (0.27, 0.30), (0.285, 0.34), (0.305, 0.32),
                       (0.325, 0.25), (0.34, 0.20), (0.37, 0.168), (0.42, 0.152), (0.5, 0.146)], []),
    "Female_Adult_01": ([(0.0, 0.40), (0.10, 0.41), (0.165, 0.40), (0.195, 0.345), (0.215, 0.305), (0.26, 0.295), (0.295, 0.285), (0.325, 0.245),
                         (0.355, 0.20), (0.38, 0.16), (0.42, 0.125), (0.46, 0.105), (0.5, 0.10)], [(0.47, 1.0), (0.47, 0.93), (0.495, 0.87), (0.51, 0.84), (0.56, 0.807), (0.62, 0.785), (0.675, 0.755), (0.70, 0.72), (0.80, 0.682),
                          (0.86, 0.662), (0.905, 0.63), (0.93, 0.59), (1.0, 0.585), (1.0, 1.0)]),
    "Female_Adult_17": ([(0.0, 0.42), (0.12, 0.42), (0.18, 0.39), (0.205, 0.34), (0.225, 0.305), (0.26, 0.30), (0.28, 0.31), (0.30, 0.27), (0.325, 0.235),
                         (0.35, 0.20), (0.385, 0.165), (0.43, 0.135), (0.5, 0.12)], [(0.49, 1.0), (0.49, 0.83), (0.53, 0.79), (0.60, 0.772), (0.70, 0.755), (0.735, 0.70), (0.78, 0.665), (0.86, 0.64), (0.95, 0.605),
                          (1.0, 0.59), (1.0, 1.0)]),
}


def luminance(rgb):
    return rgb[..., 0] * 0.3 + rgb[..., 1] * 0.59 + rgb[..., 2] * 0.11


def blurred(array, radius):
    return np.asarray(Image.fromarray((np.clip(array, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(radius))).astype(np.float32) / 255


def smoothstep(low, high, x):
    t = np.clip((x - low) / (high - low), 0, 1)
    return t * t * (3 - 2 * t)


def dark_hair(rgb, colour, bright):
    """The strands stay, as shades of the new colour."""
    shade = np.clip(luminance(rgb) / bright, 0, 1.3) ** 1.2
    return shade[..., None] * np.array(colour, dtype=np.float32)


def hair_mask(rgb, hairline, buns):
    """The hair of the head's left half is inside the hairline (the right half mirrors it); the buns have their own outline."""
    height, width, _ = rgb.shape
    outline = [(0.0, 0.0)] + hairline + [(0.5, 0.0)]
    image = Image.new("L", (width, height), 0)
    draw = ImageDraw.Draw(image)
    draw.polygon([(x * width, y * height) for x, y in outline], fill=255)
    draw.polygon([((1 - x) * width, y * height) for x, y in outline], fill=255)
    mask = np.asarray(image.filter(ImageFilter.GaussianBlur(width / 170))).astype(np.float32) / 255
    if buns:
        # The buns lie right under the chest with a hair's breadth of black between, so the outline alone is not enough:
        # inside it, hair is what leans to yellow (blond) against the skin's red, or is in shadow.
        area = Image.new("L", (width, height), 0)
        ImageDraw.Draw(area).polygon([(x * width, y * height) for x, y in buns], fill=255)
        total = rgb.sum(2) + 1e-4
        yellow = blurred(((rgb[..., 1] - rgb[..., 2]) - (rgb[..., 0] - rgb[..., 1])) / total * 4 + 0.5, 2)
        lum = luminance(rgb)
        hair = np.maximum(smoothstep(0.5 - 0.03 * 4, 0.5 - 0.012 * 4, yellow), 1 - smoothstep(0.3, 0.4, lum))
        closed = Image.fromarray((hair * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(11)).filter(ImageFilter.MinFilter(11)).filter(ImageFilter.GaussianBlur(1.5))
        mask = np.maximum(mask, np.asarray(closed).astype(np.float32) / 255 * (np.asarray(area).astype(np.float32) / 255) * (lum > 0.03))
    return mask


def darker_brows(rgb, colour):
    """The brows are a few light hairs on skin; they take the hair's colour by how much darker than the skin they are."""
    height, width, _ = rgb.shape
    y, x = np.mgrid[0:height, 0:width].astype(np.float32)
    x, y = x / width, y / height
    lum = luminance(rgb)
    skin = blurred(lum, 12)
    strands = smoothstep(0.01, 0.07, skin - lum)
    zone = np.zeros_like(lum)
    for cx in (0.435, 0.565):
        zone = np.maximum(zone, 1 - smoothstep(0.6, 1.0, ((x - cx) / 0.06) ** 2 + ((y - 0.262) / 0.022) ** 2))
    weight = (zone * strands * 0.7)[..., None]
    return rgb * (1 - weight) + np.array(colour, dtype=np.float32) * 1.8 * weight


def recolour_top(rgb, hues, new_hue, saturation, value, boxes):
    flat = rgb.reshape(-1, 3)
    maximum, minimum = flat.max(1), flat.min(1)
    delta = maximum - minimum + 1e-6
    hue = np.where(maximum == flat[:, 0], ((flat[:, 1] - flat[:, 2]) / delta) % 6,
                   np.where(maximum == flat[:, 1], (flat[:, 2] - flat[:, 0]) / delta + 2, (flat[:, 0] - flat[:, 1]) / delta + 4)) * 60
    sat = delta / (maximum + 1e-6)
    inside = smoothstep(hues[0] - 8, hues[0] + 4, hue) * (1 - smoothstep(hues[1] - 4, hues[1] + 8, hue)) * smoothstep(0.08, 0.2, sat)
    if boxes:
        height, width, _ = rgb.shape
        where = np.zeros((height, width), dtype=np.float32)
        for x0, y0, x1, y1 in boxes:
            where[int(y0 * height):int(y1 * height), int(x0 * width):int(x1 * width)] = 1
        inside = inside * where.reshape(-1)
    r, g, b = colorsys.hsv_to_rgb(new_hue / 360, 1, 1)
    target_sat = np.clip(sat * saturation, 0, 1)[:, None]
    painted = (np.array([r, g, b], dtype=np.float32)[None, :] * target_sat + (1 - target_sat)) * np.clip(maximum * value, 0, 1)[:, None]
    return (flat * (1 - inside[:, None]) + painted * inside[:, None]).reshape(rgb.shape), inside.reshape(rgb.shape[:2])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--preview", type=Path, default=None, help="also writes one sheet of what was made, for looking at")
    options = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    tiles, bodies = [], []
    for name, prefix, colour, bright, top in AVATARS:
        textures = SOURCE / name / "Textures"
        if colour is not None:
            head = np.asarray(Image.open(textures / f"{prefix}_head_color.png").convert("RGB")).astype(np.float32) / 255
            mask = hair_mask(head, *HAIR[name])[..., None]
            painted = darker_brows(head * (1 - mask) + dark_hair(head, colour, bright) * mask, colour)
            Image.fromarray((np.clip(painted, 0, 1) * 255).astype(np.uint8)).save(OUT / f"{name}_head_color.png")
            tiles += [painted, np.repeat(mask, 3, 2)]
            cards = textures / f"{prefix}_opacity_color.png"
            if cards.exists():
                rgba = np.asarray(Image.open(cards).convert("RGBA")).astype(np.float32) / 255
                hair = dark_hair(rgba[..., :3], colour, bright)
                Image.fromarray((np.clip(np.concatenate([hair, rgba[..., 3:]], 2), 0, 1) * 255).astype(np.uint8)).save(OUT / f"{name}_opacity_color.png")
            print(name, "hair", round(float(mask.mean()), 3))
        body = np.asarray(Image.open(textures / f"{prefix}_body_color.png").convert("RGB")).astype(np.float32) / 255
        second, _ = recolour_top(body, *top)
        Image.fromarray((np.clip(second, 0, 1) * 255).astype(np.uint8)).save(OUT / f"{name}_b_body_color.png")
        bodies.append(second)
    if options.preview:
        options.preview.mkdir(parents=True, exist_ok=True)
        for stem, pictures, columns, size in (("customer_hair", tiles, 2, 640), ("customer_clothes", bodies, 4, 480)):
            sheet = Image.new("RGB", (size * columns, size * math.ceil(len(pictures) / columns)))
            for index, tile in enumerate(pictures):
                image = Image.fromarray((np.clip(tile, 0, 1) * 255).astype(np.uint8)).resize((size, size))
                sheet.paste(image, ((index % columns) * size, (index // columns) * size))
            sheet.save(options.preview / f"{stem}.jpg", quality=88)


if __name__ == "__main__":
    main()
