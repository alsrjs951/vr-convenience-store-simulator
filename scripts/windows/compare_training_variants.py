"""Compares training variants of one session on the product only, on views held out of training.

ns-eval's numbers cover the whole frame, most of which is bedding. This reads the images it saved with
--render-output-path (ground truth left, render right), finds the product in the ground truth, and measures only there:
PSNR, SSIM and LPIPS over the product's bounding box, and how much of the print's fine detail survives (the energy of the
Laplacian in the render over that in the ground truth, 1.0 meaning all of it).

Writes variants.json and a sheet of the print-heaviest crop of a few views. Run with the nerfstudio environment's python.
"""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np
import torch
from skimage.metrics import structural_similarity
from torchmetrics.image.lpip import LearnedPerceptualImagePatchSimilarity

CROP = 300


def product_mask(gt):
    hsv = cv2.cvtColor(gt, cv2.COLOR_BGR2HSV)
    mask = (hsv[..., 1] > 70).astype(np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((61, 61), np.uint8))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask)
    if count < 2:
        return None
    largest = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    contours, _ = cv2.findContours((labels == largest).astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    hull = cv2.convexHull(np.vstack(contours))
    out = np.zeros_like(mask)
    cv2.fillConvexPoly(out, hull, 1)
    return out.astype(bool)


def split(path):
    image = cv2.imread(str(path))
    half = image.shape[1] // 2
    return image[:, :half], image[:, half:]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('session', type=Path)
    p.add_argument('--variants', required=True, help='Comma-separated name=folder pairs; the folders are the eval-images folders.')
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--sheet-views', type=int, default=4)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    variants = [v.split('=', 1) for v in args.variants.split(',')]
    lpips = LearnedPerceptualImagePatchSimilarity(net_type='alex', normalize=True).cuda()

    names = sorted(q.name for q in (args.session / variants[0][1]).glob('*_img.png')) or sorted(q.name for q in (args.session / variants[0][1]).glob('*img*.png'))
    if not names:
        raise SystemExit('No saved evaluation images.')
    results, crops = {}, []
    # The crop for each sheet view is chosen once, where the ground truth has the most print.
    gt_first = {}
    for name in names:
        gt, _ = split(args.session / variants[0][1] / name)
        mask = product_mask(gt)
        if mask is None:
            continue
        energy = np.abs(cv2.Laplacian(cv2.cvtColor(gt, cv2.COLOR_BGR2GRAY), cv2.CV_32F))
        energy[~mask] = 0
        blurred = cv2.blur(energy, (CROP, CROP))
        y, x = np.unravel_index(np.argmax(blurred), blurred.shape)
        h, w = gt.shape[:2]
        x0, y0 = int(np.clip(x - CROP // 2, 0, w - CROP)), int(np.clip(y - CROP // 2, 0, h - CROP))
        gt_first[name] = (gt, mask, x0, y0, float(blurred.max()))
    chosen = sorted(gt_first, key=lambda n: -gt_first[n][4])[:args.sheet_views]

    for label, folder in variants:
        rows = []
        for name in gt_first:
            gt, mask, _, _, _ = gt_first[name]
            _, render = split(args.session / folder / name)
            ys, xs = np.where(mask)
            y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
            a, b = gt[y0:y1, x0:x1], render[y0:y1, x0:x1]
            inside = mask[y0:y1, x0:x1]
            mse = float(((a[inside].astype(np.float32) - b[inside].astype(np.float32)) ** 2).mean())
            psnr = 10 * np.log10(255 ** 2 / max(mse, 1e-6))
            ssim = structural_similarity(a, b, channel_axis=2)
            ta = torch.from_numpy(a[..., ::-1].copy()).permute(2, 0, 1)[None].float().cuda() / 255
            tb = torch.from_numpy(b[..., ::-1].copy()).permute(2, 0, 1)[None].float().cuda() / 255
            with torch.no_grad():
                perceptual = float(lpips(ta, tb))
            ga = cv2.Laplacian(cv2.cvtColor(a, cv2.COLOR_BGR2GRAY), cv2.CV_32F)[inside]
            gb = cv2.Laplacian(cv2.cvtColor(b, cv2.COLOR_BGR2GRAY), cv2.CV_32F)[inside]
            rows.append((psnr, ssim, perceptual, float(gb.var() / max(ga.var(), 1e-6))))
        rows = np.array(rows)
        results[label] = {'views': len(rows), 'psnr': round(float(rows[:, 0].mean()), 2), 'ssim': round(float(rows[:, 1].mean()), 4),
                          'lpips': round(float(rows[:, 2].mean()), 4), 'detail_kept': round(float(rows[:, 3].mean()), 3)}
        print(label, results[label], flush=True)

    (args.output / 'variants.json').write_text(json.dumps(results, indent=1))
    sheet = []
    for name in chosen:
        gt, _, x0, y0, _ = gt_first[name]
        row = [gt[y0:y0 + CROP, x0:x0 + CROP]]
        for _, folder in variants:
            _, render = split(args.session / folder / name)
            row.append(render[y0:y0 + CROP, x0:x0 + CROP])
        sheet.append(np.hstack(row))
    header = np.full((36, sheet[0].shape[1], 3), 24, np.uint8)
    for i, text in enumerate(['photo'] + [v[0] for v in variants]):
        cv2.putText(header, text, (6 + i * CROP, 26), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(args.output / 'variants_crops.jpg'), np.vstack([header] + sheet), [cv2.IMWRITE_JPEG_QUALITY, 92])


if __name__ == '__main__':
    main()
