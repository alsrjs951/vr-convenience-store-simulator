"""Keeps one product's Gaussians from a full-scene PLY using a 3D color-seeded convex hull.

Assumes a colorful package on a low-saturation background such as the gray bedding in these captures.
1. Seeds: opaque Gaussians whose base color is saturated.
2. The largest dense cluster of seeds is the product core.
3. Every Gaussian inside that core's slightly enlarged convex hull is kept, whatever its color,
   so white labels and dark print survive while the background outside the hull goes.

This replaces per-view color outlines, which cut the Goraebap box wherever a label or shadow split its
colored regions. isolate_dakdari.py stays as the record of that earlier run. It is still a heuristic:
check the review render for every product. It only removes Gaussians and cannot create unseen faces.

--background-distance also seeds colors far from the scene's dominant color. A package whose white label
or black band reaches its edge needs it: saturation-only seeds cut those regions off (Shin Ramyun back).
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from plyfile import PlyData, PlyElement
from scipy.spatial import ConvexHull, Delaunay
from sklearn.cluster import DBSCAN
from sklearn.neighbors import NearestNeighbors

SH_C0 = 0.28209479177387814
NEIGHBORS_FOR_DENSITY = 8
MIN_KEPT = 100


def base_color(vertex):
    dc = np.stack([vertex[f'f_dc_{i}'] for i in range(3)], axis=-1).astype(np.float64)
    return np.clip(0.5 + SH_C0 * dc, 0.0, 1.0)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('ply', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--min-saturation', type=float, default=0.45)
    p.add_argument('--min-value', type=float, default=0.25)
    p.add_argument('--min-opacity', type=float, default=0.5)
    p.add_argument('--background-distance', type=float, default=None,
                   help='Also seed colors at least this far (RGB, 0-1) from the median scene color.')
    p.add_argument('--cluster-scale', type=float, default=3.0,
                   help='DBSCAN eps, as a multiple of the median distance to the 8th nearest seed.')
    p.add_argument('--hull-margin', type=float, default=0.02, help='Enlarges the hull about its centroid.')
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    before = hashlib.sha256(args.ply.read_bytes()).hexdigest()
    vertex = PlyData.read(args.ply)['vertex'].data
    xyz = np.column_stack([vertex[n] for n in ('x', 'y', 'z')]).astype(np.float64)
    opacity = 1 / (1 + np.exp(-np.asarray(vertex['opacity'], dtype=np.float64)))
    rgb = base_color(vertex)
    maximum = rgb.max(axis=1)
    saturation = (maximum - rgb.min(axis=1)) / np.maximum(maximum, 1e-6)
    opaque = opacity >= args.min_opacity
    seed_mask = opaque & (saturation >= args.min_saturation) & (maximum >= args.min_value)
    background = np.median(rgb[opaque], axis=0)
    if args.background_distance is not None:
        seed_mask |= opaque & (np.linalg.norm(rgb - background, axis=1) >= args.background_distance)
    seeds = np.flatnonzero(seed_mask)
    if len(seeds) < MIN_KEPT:
        raise RuntimeError(f'Too few color seeds: {len(seeds)}')

    positions = xyz[seeds]
    distances, _ = NearestNeighbors(n_neighbors=NEIGHBORS_FOR_DENSITY + 1).fit(positions).kneighbors(positions)
    eps = float(np.median(distances[:, -1]) * args.cluster_scale)
    labels = DBSCAN(eps=eps, min_samples=NEIGHBORS_FOR_DENSITY, n_jobs=4).fit_predict(positions)
    ids, counts = np.unique(labels[labels >= 0], return_counts=True)
    if not len(ids):
        raise RuntimeError('No dense color cluster found')
    core = positions[labels == ids[np.argmax(counts)]]

    # Enlarge the hull about its centroid, then keep everything inside it.
    hull_points = core[ConvexHull(core).vertices]
    centroid = hull_points.mean(axis=0)
    inside = Delaunay(centroid + (hull_points - centroid) * (1 + args.hull_margin)).find_simplex(xyz) >= 0
    if inside.sum() < MIN_KEPT:
        raise RuntimeError('Crop rejected: insufficient remaining Gaussians')
    if not all(np.isfinite(vertex[n][inside]).all() for n in vertex.dtype.names):
        raise RuntimeError('Non-finite PLY attribute in kept Gaussians')
    PlyData([PlyElement.describe(vertex[inside], 'vertex')], text=False).write(args.output / 'isolated.ply')

    assert before == hashlib.sha256(args.ply.read_bytes()).hexdigest()
    report = dict(source=str(args.ply), source_sha256=before, source_count=len(vertex), seeds=len(seeds),
                  clusters=len(ids), largest_cluster_seeds=int(counts.max()), second_cluster_seeds=int(np.sort(counts)[-2]) if len(ids) > 1 else 0,
                  kept=int(inside.sum()), removed=int((~inside).sum()), hull_margin=args.hull_margin,
                  min_saturation=args.min_saturation, min_value=args.min_value, min_opacity=args.min_opacity,
                  background_distance=args.background_distance, median_scene_color=background.tolist(),
                  cluster_eps=eps, all_attributes_finite=True,
                  limitations='Color-seeded heuristic. Removes Gaussians only; cannot create unseen faces. Check the review render.')
    (args.output / 'isolation.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report[k] for k in ('source_count', 'seeds', 'clusters', 'largest_cluster_seeds', 'second_cluster_seeds', 'kept', 'removed')}))


if __name__ == '__main__':
    main()
