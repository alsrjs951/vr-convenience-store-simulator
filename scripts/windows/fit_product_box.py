"""Fit an approximate oriented box to an isolated Gaussian PLY, for proxy collider setup.

Output is in PLY units. It is a geometric estimate from the reconstruction, not a
measurement: scale is arbitrary and unobserved faces can distort the box. The caller
chooses which face is the front from a visual review (see review_product_splat.py).

Product axes follow docs (front=+Z, up=+Y). The PLY is right-handed and Unity's view of
it is left-handed, so the product frame is stored as a reflection (det = -1). Removing
the reflection would mirror the printed text.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from plyfile import PlyData


def rotation(rotvec):
    angle = np.linalg.norm(rotvec)
    if angle < 1e-12:
        return np.eye(3)
    k = rotvec / angle
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(angle) * K + (1 - np.cos(angle)) * K @ K


def initial_axes(xyz, flat=False):
    """Largest PCA axis is the length. The height axis depends on the product's shape.

    Box: the axis closest to the capture's world Z. Flat bag: the thinnest axis, because world Z is unreliable
    for a bag filmed from above (it can lie in the bag's plane, or even equal the length axis and give NaN).
    Its sign points at the observed face, where Gaussians pack tightly against the surface.
    """
    _, axes = np.linalg.eigh(np.cov(xyz.T))
    long = axes[:, 2]
    if flat:
        up = axes[:, 0]
        height = xyz @ up
        middle = np.median(height)
        if np.percentile(height, 99) - middle > middle - np.percentile(height, 1):
            up = -up
    else:
        up = axes[:, np.argmax(abs(axes[2]))]
        if up[2] < 0:
            up = -up
    side = np.cross(up, long)
    side /= np.linalg.norm(side)
    up = np.cross(long, side)
    return long, side, up


def trimmed_bounds(values, trim):
    return np.percentile(values, trim, axis=0), np.percentile(values, 100 - trim, axis=0)


def refine(points, basis, trim):
    """Coordinate descent on trimmed box volume; only small rotations are expected."""
    best = basis.copy()

    def volume(candidate):
        lower, upper = trimmed_bounds(points @ candidate, trim)
        return float(np.prod(upper - lower))

    best_volume = start_volume = volume(best)
    for step_deg in (2.0, 1.0, 0.5, 0.25):
        improved = True
        while improved:
            improved = False
            for axis in np.eye(3):
                for sign in (-1, 1):
                    candidate = rotation(axis * sign * np.radians(step_deg)) @ best
                    value = volume(candidate)
                    if value < best_volume - 1e-12:
                        best, best_volume, improved = candidate, value, True
    return best, start_volume, best_volume


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('ply', type=Path)
    p.add_argument('output', type=Path)
    axes = ['+long', '-long', '+side', '-side', '+up', '-up']
    p.add_argument('--front', choices=axes, default='+side',
                   help='Review direction whose render shows the front: side_a=+side, side_b=-side, end_a=+long, '
                        'end_b=-long, top=+up, underside=-up.')
    p.add_argument('--top', choices=axes, default='+up',
                   help='Review direction the top of the print points toward. A flat bag needs this set.')
    p.add_argument('--flat', action='store_true', help='The product is a flat bag; see initial_axes.')
    p.add_argument('--axes-from', type=Path,
                   help='Keep the axes of this earlier fit and only recompute center and extents. A merged bag is '
                        'in its front session\'s space, and a new PCA could flip the axis signs.')
    p.add_argument('--trim', type=float, default=1.0, help='Percent trimmed from each end of each axis.')
    p.add_argument('--min-opacity', type=float, default=0.3)
    args = p.parse_args()

    before = hashlib.sha256(args.ply.read_bytes()).hexdigest()
    data = PlyData.read(args.ply)['vertex'].data
    xyz = np.stack([data[n] for n in ('x', 'y', 'z')], axis=-1).astype(np.float64)
    opacity = 1 / (1 + np.exp(-np.asarray(data['opacity'], dtype=np.float64)))
    kept = xyz[opacity >= args.min_opacity]

    if args.axes_from:
        axes = json.loads(args.axes_from.read_text())['axes_in_ply']
        basis = np.stack([axes['x'], axes['y'], axes['z_front']], axis=1)
        refined = basis
        start_volume = end_volume = float(np.prod(np.subtract(*trimmed_bounds(kept @ basis, args.trim)[::-1])))
    else:
        long, side, up = initial_axes(xyz, args.flat)
        named = {'long': long, 'side': side, 'up': up}

        def axis(choice):
            return named[choice[1:]] * (1.0 if choice[0] == '+' else -1.0)

        front, top = axis(args.front), axis(args.top)
        if abs(np.dot(front, top)) > 0.5:
            raise SystemExit('--front and --top must be different axes.')
        # Choose +X so that (x, y, z) has det = -1: seen from the front in Unity, this
        # keeps the print readable. See the module docstring.
        x = np.cross(front, top)
        basis = np.stack([x, top, front], axis=1)
        assert np.linalg.det(basis) < 0
        refined, start_volume, end_volume = refine(kept, basis, args.trim)
    local = kept @ refined
    lower, upper = trimmed_bounds(local, args.trim)
    center = refined @ ((lower + upper) / 2)
    extents = upper - lower

    # Per-axis distribution near each face, to judge how sharp each bound is.
    profiles = {}
    for name, index in (('x', 0), ('y', 1), ('z', 2)):
        values = local[:, index]
        grid = [float(np.percentile(values, q)) for q in (0.1, 0.5, 1, 2, 5, 95, 98, 99, 99.5, 99.9)]
        profiles[name] = dict(zip(('p0.1', 'p0.5', 'p1', 'p2', 'p5', 'p95', 'p98', 'p99', 'p99.5', 'p99.9'), grid))

    assert before == hashlib.sha256(args.ply.read_bytes()).hexdigest()
    report = {
        'source_ply': args.ply.as_posix(),
        'source_sha256': before,
        'count': int(len(data)),
        'count_used_for_fit': int(len(kept)),
        'min_opacity': args.min_opacity,
        'trim_percent': args.trim,
        'front_choice': args.front,
        'top_choice': args.top,
        'axes_in_ply': {'x': refined[:, 0].tolist(), 'y': refined[:, 1].tolist(), 'z_front': refined[:, 2].tolist()},
        'basis_determinant': float(np.linalg.det(refined)),
        'center_in_ply': center.tolist(),
        'extents_ply_units_xyz': extents.tolist(),
        'trimmed_volume_before_refine': start_volume,
        'trimmed_volume_after_refine': end_volume,
        'refine_rotation_degrees': float(np.degrees(np.arccos(np.clip((np.trace(basis.T @ refined) - 1) / 2, -1, 1)))),
        'axis_percentiles': profiles,
        'limitations': 'Estimated from the reconstruction. No physical scale. Unobserved faces can bias this box.',
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report[k] for k in ('count', 'count_used_for_fit', 'basis_determinant', 'center_in_ply',
                                             'extents_ply_units_xyz', 'refine_rotation_degrees',
                                             'trimmed_volume_before_refine', 'trimmed_volume_after_refine',
                                             'axis_percentiles')}, indent=1))


if __name__ == '__main__':
    main()
