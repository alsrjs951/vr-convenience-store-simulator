"""Describes how a product video was shot, from its reconstruction, so two capture techniques can be compared.

Works in the exported splat frame: camera poses from nerfstudio/transforms.json are moved through the run's
dataparser_transforms.json, and the product is the isolated PLY. The ground is a RANSAC plane through the
full-scene splat outside the product. Per registered frame it reports:
- elevation: camera angle above the ground plane, seen from the product center (0 = level with the product)
- distance: camera to product center, in multiples of the product's largest extent (scale free)
- coverage: fraction of the image taken by the product's projected bounding box
- brightness and sharpness of the frame (mean gray, variance of the Laplacian at 960 px wide)
Elevation and azimuth tell whether the camera went low enough to see the side faces; distance and coverage
tell whether it came close. This describes the camera path only. It does not judge reconstruction quality.
Run with the nerfstudio environment's python (plyfile, cv2).
"""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np
from plyfile import PlyData


def load_xyz(path):
    v = PlyData.read(str(path))['vertex'].data
    return np.column_stack([v['x'], v['y'], v['z']]).astype(np.float64)


def fit_ground(points, rng, iterations=400):
    tolerance = 0.01 * np.linalg.norm(np.percentile(points, 95, axis=0) - np.percentile(points, 5, axis=0))
    best, best_count = None, -1
    for _ in range(iterations):
        a, b, c = points[rng.choice(len(points), 3, replace=False)]
        n = np.cross(b - a, c - a)
        if np.linalg.norm(n) < 1e-12:
            continue
        n /= np.linalg.norm(n)
        count = int((np.abs((points - a) @ n) < tolerance).sum())
        if count > best_count:
            best, best_count = (n, a), count
    n, a = best
    inliers = points[np.abs((points - a) @ n) < tolerance]
    centroid = inliers.mean(axis=0)
    n = np.linalg.svd(inliers - centroid)[2][-1]
    return n, centroid, best_count / len(points)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('session', type=Path)
    p.add_argument('--run', type=Path, required=True, help='Folder with dataparser_transforms.json.')
    p.add_argument('--scene', type=Path, required=True, help='Full-scene splat.ply.')
    p.add_argument('--product', type=Path, required=True, help='Isolated product PLY.')
    p.add_argument('--label', required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    rng = np.random.default_rng(0)

    tf = json.loads((args.session / 'nerfstudio' / 'transforms.json').read_text())
    dp = json.loads((args.run / 'dataparser_transforms.json').read_text())
    # The stored transform maps from original COLMAP space (it already includes applied_transform);
    # transforms.json poses are in the saved space, so remove applied_transform first.
    T = np.vstack([np.array(dp['transform']), [0, 0, 0, 1]])
    if 'applied_transform' in tf:
        T = T @ np.linalg.inv(np.vstack([np.array(tf['applied_transform']), [0, 0, 0, 1]]))
    T = T[:3]
    s = float(dp['scale'])
    product = load_xyz(args.product)
    lo, hi = np.percentile(product, [2, 98], axis=0)
    center = (lo + hi) / 2
    size = float(np.max(hi - lo))
    scene = load_xyz(args.scene)
    # Only the surface around the product: low shots also see walls and furniture, which can outvote the bed.
    radius = np.linalg.norm(scene - center, axis=1)
    far = (radius > 0.75 * size) & (radius < 2.5 * size)
    sample = scene[far][rng.choice(int(far.sum()), min(60000, int(far.sum())), replace=False)]
    normal, ground_point, inlier_fraction = fit_ground(sample, rng)

    w, h = tf['w'], tf['h']
    fx, fy, cx, cy = tf['fl_x'], tf['fl_y'], tf['cx'], tf['cy']
    frames_dir = args.session / 'frames'
    sub = product[rng.choice(len(product), min(20000, len(product)), replace=False)]
    rows = []
    for fr in tf['frames']:
        c2w = np.array(fr['transform_matrix'])[:3]
        R = T[:3, :3] @ c2w[:, :3]
        t = s * (T[:3, :3] @ c2w[:, 3] + T[:3, 3])
        rows.append(dict(name=Path(fr['file_path']).name, R=R, t=t))
    if np.median([(r['t'] - ground_point) @ normal for r in rows]) < 0:
        normal = -normal
    ref = np.cross(normal, [1.0, 0.0, 0.0])
    if np.linalg.norm(ref) < 0.1:
        ref = np.cross(normal, [0.0, 1.0, 0.0])
    ref /= np.linalg.norm(ref)
    ref2 = np.cross(normal, ref)
    out = []
    for r in rows:
        v = r['t'] - center
        d = np.linalg.norm(v)
        elevation = float(np.degrees(np.arcsin(np.clip(v @ normal / d, -1, 1))))
        azimuth = float(np.degrees(np.arctan2(v @ ref2, v @ ref)) % 360)
        pc = (sub - r['t']) @ r['R']  # camera coordinates, OpenGL: looks down -Z, Y up
        front = pc[:, 2] < -1e-6
        coverage = 0.0
        if front.sum() > 10:
            u = fx * pc[front, 0] / -pc[front, 2] + cx
            vv = -fy * pc[front, 1] / -pc[front, 2] + cy
            u0, u1 = np.clip(np.percentile(u, [2, 98]), 0, w)
            v0, v1 = np.clip(np.percentile(vv, [2, 98]), 0, h)
            coverage = float(max(u1 - u0, 0) * max(v1 - v0, 0) / (w * h))
        # ns-process-data copies frame_%04d.jpg to images/frame_%05d.jpg with the same index.
        index = int(r['name'].split('_')[-1].split('.')[0])
        img = cv2.imread(str(frames_dir / f'frame_{index:04d}.jpg'), cv2.IMREAD_GRAYSCALE)
        small = cv2.resize(img, (960, int(960 * img.shape[0] / img.shape[1])), interpolation=cv2.INTER_AREA)
        out.append(dict(frame=index, elevation_deg=elevation, azimuth_deg=azimuth, distance_x_size=float(d / size),
                        product_bbox_coverage=coverage, brightness=float(small.mean()),
                        sharpness_lapvar=float(cv2.Laplacian(small, cv2.CV_64F).var())))
    out.sort(key=lambda x: x['frame'])
    el = np.array([o['elevation_deg'] for o in out])
    az = np.array([o['azimuth_deg'] for o in out])
    dist = np.array([o['distance_x_size'] for o in out])
    cov = np.array([o['product_bbox_coverage'] for o in out])
    bands = [(-90, 20), (20, 35), (35, 50), (50, 65), (65, 90.1)]
    pct = lambda a, qs: {f'p{q}': round(float(np.percentile(a, q)), 3) for q in qs}
    # Sanity check of the ground plane: a product lying flat has its thinnest axis along the ground normal.
    trimmed = product[np.all((product >= lo) & (product <= hi), axis=1)]
    thin = np.linalg.svd(trimmed - trimmed.mean(axis=0), full_matrices=False)[2][-1]
    thin_vs_ground = float(np.degrees(np.arccos(min(1.0, abs(float(thin @ normal))))))
    summary = dict(
        label=args.label, frames=len(out), ground_inlier_fraction=round(inlier_fraction, 3),
        product_thin_axis_vs_ground_normal_deg=round(thin_vs_ground, 1),
        elevation_deg=pct(el, (5, 25, 50, 75, 95)),
        elevation_bands={f'{max(a, 0):.0f}-{min(b, 90):.0f}': dict(
            frames=int(((el >= a) & (el < b)).sum()),
            azimuth_bins_of_36=int(len(np.unique((az[(el >= a) & (el < b)] // 10).astype(int)))))
            for a, b in bands},
        azimuth_bins_of_36_all=int(len(np.unique((az // 10).astype(int)))),
        distance_x_size=pct(dist, (5, 50, 95)),
        product_bbox_coverage=pct(cov, (5, 50, 95)),
        frames_coverage_over_25pct=int((cov > 0.25).sum()),
        brightness_median=round(float(np.median([o['brightness'] for o in out])), 1),
        sharpness_median=round(float(np.median([o['sharpness_lapvar'] for o in out])), 1),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(summary=summary, frames=out), indent=1, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(summary, indent=1, ensure_ascii=False))


if __name__ == '__main__':
    main()
