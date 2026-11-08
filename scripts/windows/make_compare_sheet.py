"""Lays P1CaptureCompare renders of several products side by side, one row per view, with labels.

Optionally puts reference photos of the real product in a top row. Pictures for people to judge;
crops only remove empty background around the product, identically for every column of a row.
"""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def font(size):
    for name in ('malgun.ttf', 'arial.ttf'):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('renders', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--products', required=True, help='Comma-separated ids, one column each.')
    p.add_argument('--labels', required=True, help='Column labels separated by |.')
    p.add_argument('--views', required=True, help='Comma-separated view names, one row each.')
    p.add_argument('--photos', default='', help='Comma-separated reference photo paths for a top row.')
    p.add_argument('--cell', type=int, default=560, help='Cell width in pixels.')
    p.add_argument('--crop', type=float, default=0.0, help='Fraction cut from each side of every render.')
    p.add_argument('--title', default='')
    args = p.parse_args()
    ids, labels, views = args.products.split(','), args.labels.split('|'), args.views.split(',')
    photos = [x for x in args.photos.split(',') if x]
    w = args.cell
    h = int(w * 0.75)
    label_h, title_h, row_label_w = 30, 40 if args.title else 0, 150
    rows = len(views) + (1 if photos else 0)
    sheet = Image.new('RGB', (row_label_w + w * len(ids), title_h + label_h + rows * (h + 6)), 'white')
    d = ImageDraw.Draw(sheet)
    f, fs = font(20), font(16)
    if args.title:
        d.text((10, 8), args.title, fill='black', font=f)
    for c, lab in enumerate(labels):
        d.text((row_label_w + c * w + 8, title_h + 4), lab, fill='black', font=f)
    y = title_h + label_h
    if photos:
        d.text((6, y + 8), 'reference photo\n(real product)', fill='black', font=fs)
        cell_w = w * len(ids) // len(photos)
        for i, ph in enumerate(photos):
            im = Image.open(ph).convert('RGB')
            im.thumbnail((cell_w - 6, h))
            sheet.paste(im, (row_label_w + i * cell_w + (cell_w - im.width) // 2, y))
        y += h + 6
    for view in views:
        d.text((6, y + 8), view, fill='black', font=fs)
        for c, pid in enumerate(ids):
            im = Image.open(args.renders / f'{pid}__{view}.png').convert('RGB')
            if args.crop > 0:
                W, H = im.size
                im = im.crop((int(W * args.crop), int(H * args.crop), int(W * (1 - args.crop)), int(H * (1 - args.crop))))
            sheet.paste(im.resize((w, h), Image.LANCZOS), (row_label_w + c * w, y))
        y += h + 6
    args.output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.output, quality=90)
    print(args.output, sheet.size)


if __name__ == '__main__':
    main()
