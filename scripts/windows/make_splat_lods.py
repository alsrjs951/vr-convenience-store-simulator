"""Makes lighter versions of a product's 3DGS PLY for shelves and boxes (implementation doc 7.2, 7.3 step 4, P4-K1, P4-K4).

Replaces the manual SuperSplat decimation. Nearby gaussians of similar color are merged by moment matching, which keeps
the covered area and the average color; a plain cut of the smallest gaussians leaves holes. The color bins in the cluster
key are what keep printed edges from blurring across a label. Gaussians alone in their cluster are copied untouched.

Levels, as in doc 7.2:  near 50,000 (front row of a merged shelf)  lod1 35,000 (back row, box contents, placement ghost)
far 20,000 (merged shelf seen from beyond 3-4 m).

With --evaluate each level is rendered next to the original from the distances it is used at, at about Quest 2's
pixels per degree, and PSNR over the product's pixels goes to the report. Run with the nerfstudio environment's python.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from plyfile import PlyData, PlyElement

LEVELS = {'near': 50000, 'lod1': 35000, 'far': 20000}
# Distances in product widths: a 20 cm product at 0.4 m, 0.7 m / 1 m / 3.5 m.
EVAL_VIEWS = {'near': [(0, 0, 2.0), (35, 15, 3.5)], 'lod1': [(0, 0, 5.0), (35, 15, 5.0)], 'far': [(0, 0, 17.5), (35, 15, 17.5)]}
COLOR_BINS = 8
MIN_ALPHA = 0.01
SH_C0 = 0.28209479


def stack(v, names):
    return np.stack([np.asarray(v[n], dtype=np.float64) for n in names], axis=-1)


def quat_to_mat(q):
    q = q / np.linalg.norm(q, axis=1, keepdims=True)
    w, x, y, z = q.T
    m = np.empty((len(q), 3, 3))
    m[:, 0, 0], m[:, 0, 1], m[:, 0, 2] = 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)
    m[:, 1, 0], m[:, 1, 1], m[:, 1, 2] = 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)
    m[:, 2, 0], m[:, 2, 1], m[:, 2, 2] = 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)
    return m


def mat_to_quat(m):
    squares = np.stack([1 + m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2], 1 + m[:, 0, 0] - m[:, 1, 1] - m[:, 2, 2],
                        1 - m[:, 0, 0] + m[:, 1, 1] - m[:, 2, 2], 1 - m[:, 0, 0] - m[:, 1, 1] + m[:, 2, 2]], axis=1)
    pick = np.argmax(squares, axis=1)
    q = np.empty((len(m), 4))
    for case in range(4):
        i = pick == case
        s = np.sqrt(np.maximum(squares[i, case], 1e-12)) * 2
        a = m[i]
        if case == 0:
            q[i] = np.stack([s / 4, (a[:, 2, 1] - a[:, 1, 2]) / s, (a[:, 0, 2] - a[:, 2, 0]) / s, (a[:, 1, 0] - a[:, 0, 1]) / s], axis=1)
        elif case == 1:
            q[i] = np.stack([(a[:, 2, 1] - a[:, 1, 2]) / s, s / 4, (a[:, 0, 1] + a[:, 1, 0]) / s, (a[:, 0, 2] + a[:, 2, 0]) / s], axis=1)
        elif case == 2:
            q[i] = np.stack([(a[:, 0, 2] - a[:, 2, 0]) / s, (a[:, 0, 1] + a[:, 1, 0]) / s, s / 4, (a[:, 1, 2] + a[:, 2, 1]) / s], axis=1)
        else:
            q[i] = np.stack([(a[:, 1, 0] - a[:, 0, 1]) / s, (a[:, 0, 2] + a[:, 2, 0]) / s, (a[:, 1, 2] + a[:, 2, 1]) / s, s / 4], axis=1)
    return q / np.linalg.norm(q, axis=1, keepdims=True)


class Splats:
    def __init__(self, data):
        self.data = data
        self.pos = stack(data, ['x', 'y', 'z'])
        self.dc = stack(data, ['f_dc_0', 'f_dc_1', 'f_dc_2'])
        self.alpha = 1 / (1 + np.exp(-np.asarray(data['opacity'], dtype=np.float64)))
        self.scale = np.exp(stack(data, ['scale_0', 'scale_1', 'scale_2']))
        self.quat = stack(data, ['rot_0', 'rot_1', 'rot_2', 'rot_3'])
        self.weight = self.alpha * np.prod(self.scale, axis=1) ** (2 / 3)


def cluster_key(s, size):
    ijk = np.floor((s.pos - s.pos.min(axis=0)) / size).astype(np.int64)
    color = np.clip(np.floor(np.clip(0.5 + SH_C0 * s.dc, 0, 0.999) * COLOR_BINS).astype(np.int64), 0, COLOR_BINS - 1)
    key = ijk[:, 0] | ijk[:, 1] << 16 | ijk[:, 2] << 32
    return key | color[:, 0] << 48 | color[:, 1] << 51 | color[:, 2] << 54


def decimate_cluster(data, target):
    s = Splats(data[(1 / (1 + np.exp(-np.asarray(data['opacity'], dtype=np.float64)))) >= MIN_ALPHA])
    if len(s.data) <= target:
        return s.data.copy()
    low, high = 1e-5, float(np.max(np.ptp(s.pos, axis=0)))
    for _ in range(40):
        mid = np.sqrt(low * high)
        n = len(np.unique(cluster_key(s, mid)))
        if n > target:
            low = mid
        else:
            high = mid
    _, inverse, counts = np.unique(cluster_key(s, high), return_inverse=True, return_counts=True)
    n, w = len(counts), s.weight
    total = np.bincount(inverse, w, n)
    mean = lambda values: np.bincount(inverse, w * values, n) / total
    center = np.stack([mean(s.pos[:, i]) for i in range(3)], axis=1)
    rotation = quat_to_mat(s.quat)
    own = (rotation * (s.scale ** 2)[:, None, :]) @ rotation.transpose(0, 2, 1)
    offset = s.pos - center[inverse]
    full = own + offset[:, :, None] * offset[:, None, :]
    covariance = np.empty((n, 3, 3))
    for a in range(3):
        for b in range(3):
            covariance[:, a, b] = mean(full[:, a, b])
    values, vectors = np.linalg.eigh(covariance)
    vectors[np.linalg.det(vectors) < 0, :, 2] *= -1
    quat = mat_to_quat(vectors)
    scale = np.sqrt(np.maximum(values, 1e-14))
    alpha = np.clip(mean(s.alpha), 1e-4, 1 - 1e-4)

    out = np.zeros(n, dtype=s.data.dtype)
    color_fields = [f for f in s.data.dtype.names if f.startswith('f_')]
    for name in color_fields:
        out[name] = mean(np.asarray(s.data[name], dtype=np.float64))
    out['x'], out['y'], out['z'] = center.T
    out['opacity'] = np.log(alpha / (1 - alpha))
    for i in range(3):
        out[f'scale_{i}'] = np.log(scale[:, i])
    for i in range(4):
        out[f'rot_{i}'] = quat[:, i]
    # A gaussian alone in its cluster is exactly what it was.
    alone = counts[inverse] == 1
    out[inverse[alone]] = s.data[alone]
    return out


def decimate_importance(data, target):
    """The plain alternative, kept to compare against: keep the heaviest gaussians and widen them to fill in."""
    s = Splats(data)
    keep = np.argsort(-s.weight)[:target]
    out = data[keep].copy()
    grow = np.log((len(data) / target) ** (1 / 3))
    for i in range(3):
        out[f'scale_{i}'] += grow
    return out


def render(data, fit, views, device, gsplat, torch):
    ax = fit['axes_in_ply']
    basis = np.stack([ax['x'], ax['y'], ax['z_front']], axis=1)
    center, width = np.array(fit['center_in_ply']), float(fit['extents_ply_units_xyz'][0])
    g = lambda x: torch.tensor(np.asarray(x), dtype=torch.float32, device=device)
    rest = sorted([n for n in data.dtype.names if n.startswith('f_rest_')], key=lambda n: int(n[7:]))
    dc = stack(data, ['f_dc_0', 'f_dc_1', 'f_dc_2'])[:, None, :]
    colors = np.concatenate([dc, stack(data, rest).reshape(len(data), 3, -1).transpose(0, 2, 1)], axis=1) if rest else dc
    sh = int(np.sqrt(colors.shape[1]) - 1)
    width_px, height_px = 900, 700
    focal = (height_px / 2) / np.tan(np.radians(16))  # about 22 pixels per degree, like Quest 2
    K = np.array([[focal, 0, width_px / 2], [0, focal, height_px / 2], [0, 0, 1]])
    frames = []
    for yaw, pitch, distance in views:
        y, p = np.radians(yaw), np.radians(pitch)
        eye = center + basis @ np.array([np.sin(y) * np.cos(p), np.sin(p), np.cos(y) * np.cos(p)]) * distance * width
        up = basis @ np.array([0, 1.0, 0])
        forward = (center - eye) / np.linalg.norm(center - eye)
        right = np.cross(forward, up)
        right /= np.linalg.norm(right)
        view = np.eye(4)
        view[:3, :3] = np.stack([right, np.cross(forward, right), forward])
        view[:3, 3] = -view[:3, :3] @ eye
        with torch.no_grad():
            image, alpha, _ = gsplat(g(stack(data, ['x', 'y', 'z'])), g(stack(data, ['rot_0', 'rot_1', 'rot_2', 'rot_3'])),
                                     g(np.exp(stack(data, ['scale_0', 'scale_1', 'scale_2']))), torch.sigmoid(g(np.asarray(data['opacity'], dtype=np.float32))),
                                     g(colors), g(view[None]), g(K[None]), width_px, height_px, sh_degree=sh, backgrounds=g([[0.16, 0.18, 0.22]]))
        frames.append((image[0].clamp(0, 1).cpu().numpy(), alpha[0, :, :, 0].cpu().numpy()))
    return frames


def psnr(original, candidate):
    mask = original[1] > 0.3
    difference = (original[0][mask] - candidate[0][mask]) ** 2
    return float(-10 * np.log10(max(difference.mean(), 1e-10)))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('products', type=Path, help='Folder of <id>.ply')
    parser.add_argument('output', type=Path, help='Folder for <id>_<level>.ply and lod_report.json')
    parser.add_argument('--ids', nargs='+', required=True)
    parser.add_argument('--fits', type=Path, help='Folder of <id>.box_fit.json (with --evaluate)')
    parser.add_argument('--evaluate', action='store_true')
    parser.add_argument('--sheets', type=Path, help='Folder for original / cluster / importance comparison pictures')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    report_path = args.output / 'lod_report.json'
    report = json.loads(report_path.read_text()) if report_path.exists() else {}
    if args.evaluate:
        import torch
        from gsplat import rasterization
        from PIL import Image

    for product in args.ids:
        data = PlyData.read(str(args.products / f'{product}.ply'))['vertex'].data
        entry = {'source_gaussians': int(len(data))}
        fit = json.loads((args.fits / f'{product}.box_fit.json').read_text()) if args.evaluate else None
        for level, target in LEVELS.items():
            light = decimate_cluster(data, target)
            PlyData([PlyElement.describe(light, 'vertex')], byte_order='<').write(str(args.output / f'{product}_{level}.ply'))
            info = {'gaussians': int(len(light))}
            if args.evaluate:
                plain = decimate_importance(data, min(target, len(data)))
                views = EVAL_VIEWS[level]
                original = render(data, fit, views, 'cuda', rasterization, torch)
                ours = render(light, fit, views, 'cuda', rasterization, torch)
                other = render(plain, fit, views, 'cuda', rasterization, torch)
                info['psnr_cluster'] = [round(psnr(a, b), 2) for a, b in zip(original, ours)]
                info['psnr_importance'] = [round(psnr(a, b), 2) for a, b in zip(original, other)]
                if args.sheets:
                    args.sheets.mkdir(parents=True, exist_ok=True)
                    picture = np.concatenate([original[0][0], ours[0][0], other[0][0]], axis=1)
                    Image.fromarray((picture * 255).astype(np.uint8)).save(args.sheets / f'{product}_{level}.jpg', quality=90)
            entry[level] = info
            print(product, level, info, flush=True)
        report[product] = entry
        report_path.write_text(json.dumps(report, indent=1))


if __name__ == '__main__':
    main()
