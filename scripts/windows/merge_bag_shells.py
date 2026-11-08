"""Joins a bag's front and back sessions into one PLY, as two real reconstructed faces back to back.

A bag lying on a surface shows one face per video, and flipping it starts a new session. COLMAP has no
link between the sessions, so THE ALIGNMENT IS AN ASSUMPTION: width is matched, both outlines are centered,
and the two rims are put on one plane. It is not a measurement or a joint reconstruction. The bag can also
deform when flipped. Each face's content is observed; only their relative placement is assumed.

Each session keeps the part above its rim. Below the rim is the unobserved inside, which would show
through as smears. The back goes through a proper rotation plus uniform scale, so positions, orientations,
sizes and spherical harmonics can all follow it. Inputs are not changed.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from plyfile import PlyData, PlyElement
from scipy.spatial.transform import Rotation

FLIP_ABOUT_Y = np.diag([-1.0, 1.0, -1.0])
RIM_BAND = 0.92
OUTLINE_MARGIN = 1.03
SH_DIRECTION_SAMPLES = 4000


def sh_basis(d):
    """Real SH bands 1-3, in the coefficient order used by Inria PLYs and the Unity renderer."""
    x, y, z = d[:, 0], d[:, 1], d[:, 2]
    xx, yy, zz = x * x, y * y, z * z
    return {
        1: np.stack([-y, z, -x], axis=1),
        2: np.stack([x * y, -y * z, 2 * zz - xx - yy, -x * z, xx - yy], axis=1),
        3: np.stack([y * (3 * xx - yy), x * y * z, y * (4 * zz - xx - yy), z * (2 * zz - 3 * xx - 3 * yy),
                     x * (4 * zz - xx - yy), z * (xx - yy), x * (xx - 3 * yy)], axis=1),
    }


def sh_rotation_blocks(rotation):
    """Finds, per band, the matrix taking coefficients in the old frame to the rotated frame."""
    rng = np.random.default_rng(0)
    directions = rng.normal(size=(SH_DIRECTION_SAMPLES, 3))
    directions /= np.linalg.norm(directions, axis=1, keepdims=True)
    old, new = sh_basis(directions), sh_basis(directions @ rotation.T)
    blocks = []
    for band in (1, 2, 3):
        # new(d) = M @ old(d) for every direction, so preserving color needs c' = inverse(M).T @ c.
        M_transposed, residual, _, _ = np.linalg.lstsq(old[band], new[band], rcond=None)
        error = np.abs(old[band] @ M_transposed - new[band]).max()
        if error > 1e-6:
            raise RuntimeError(f'SH band {band} rotation fit error {error}')
        blocks.append(np.linalg.inv(M_transposed.T).T)
    return blocks


def load_shell(ply_path, fit_path):
    vertex = PlyData.read(ply_path)['vertex'].data
    fit = json.loads(Path(fit_path).read_text())
    axes = fit['axes_in_ply']
    basis = np.stack([axes['x'], axes['y'], axes['z_front']], axis=1)
    center = np.array(fit['center_in_ply'])
    extents = np.array(fit['extents_ply_units_xyz'])
    xyz = np.column_stack([vertex[n] for n in 'xyz']).astype(np.float64)
    return dict(vertex=vertex, basis=basis, center=center, extents=extents, local=(xyz - center) @ basis)


def rim_height(shell):
    """Median height of Gaussians near the outline, where a flat-lying bag's seam sits."""
    local, half = shell['local'], shell['extents'] * 0.5
    outer = np.maximum(np.abs(local[:, 0]) / half[0], np.abs(local[:, 1]) / half[1]) >= RIM_BAND
    return float(np.median(local[outer, 2]))


def keep_mask(shell, rim, margin):
    local, half = shell['local'], shell['extents'] * 0.5
    above_rim = local[:, 2] >= rim - margin
    inside_outline = (np.abs(local[:, 0]) <= half[0] * OUTLINE_MARGIN) & (np.abs(local[:, 1]) <= half[1] * OUTLINE_MARGIN)
    return above_rim & inside_outline


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('front_ply', type=Path)
    p.add_argument('front_fit', type=Path)
    p.add_argument('back_ply', type=Path)
    p.add_argument('back_fit', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--rim-margin', type=float, default=0.01, help='Keeps this fraction of the width below each rim.')
    args = p.parse_args()

    hashes = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (args.front_ply, args.back_ply)}
    front = load_shell(args.front_ply, args.front_fit)
    back = load_shell(args.back_ply, args.back_fit)
    if front['vertex'].dtype != back['vertex'].dtype:
        raise RuntimeError('The two PLYs have different properties.')

    width = front['extents'][0]
    scale = width / back['extents'][0]
    front_rim, back_rim = rim_height(front), rim_height(back)
    keep_front = keep_mask(front, front_rim, width * args.rim_margin)
    keep_back = keep_mask(back, back_rim, back['extents'][0] * args.rim_margin)

    # Back local -> flipped about Y -> scaled -> rim on the front's rim plane -> front PLY space.
    rotation = front['basis'] @ FLIP_ABOUT_Y @ back['basis'].T
    if not np.isclose(np.linalg.det(rotation), 1.0, atol=1e-6):
        raise RuntimeError('Expected a proper rotation between sessions.')
    offset = np.array([0.0, 0.0, front_rim + scale * back_rim])

    moved = back['vertex'][keep_back].copy()
    placed = scale * back['local'][keep_back] @ FLIP_ABOUT_Y + offset
    world = front['center'] + placed @ front['basis'].T
    for i, name in enumerate('xyz'):
        moved[name] = world[:, i]

    # Gaussian orientation (PLY stores w, x, y, z) and size.
    old_q = np.column_stack([moved['rot_1'], moved['rot_2'], moved['rot_3'], moved['rot_0']]).astype(np.float64)
    new_q = (Rotation.from_matrix(rotation) * Rotation.from_quat(old_q)).as_quat()
    for name, values in zip(('rot_1', 'rot_2', 'rot_3', 'rot_0'), new_q.T):
        moved[name] = values
    for i in range(3):
        moved[f'scale_{i}'] = moved[f'scale_{i}'] + np.log(scale)

    # View-dependent color, per channel and band.
    names = [f'f_rest_{i}' for i in range(45)]
    rest = np.stack([moved[n] for n in names], axis=1).astype(np.float64).reshape(len(moved), 3, 15)
    start = 0
    blocks = sh_rotation_blocks(rotation)
    for block in blocks:
        size = block.shape[0]
        rest[:, :, start:start + size] = rest[:, :, start:start + size] @ block.T
        start += size
    for name, values in zip(names, rest.reshape(len(moved), 45).T):
        moved[name] = values

    merged = np.concatenate([front['vertex'][keep_front], moved])
    if not all(np.isfinite(merged[n]).all() for n in merged.dtype.names):
        raise RuntimeError('Non-finite attribute in merged PLY')
    args.output.mkdir(parents=True, exist_ok=True)
    PlyData([PlyElement.describe(merged, 'vertex')], text=False).write(args.output / 'merged.ply')

    for path, digest in hashes.items():
        assert digest == hashlib.sha256(Path(path).read_bytes()).hexdigest()
    report = dict(front_ply=str(args.front_ply), back_ply=str(args.back_ply), input_sha256=hashes,
                  front_kept=int(keep_front.sum()), front_removed=int((~keep_front).sum()),
                  back_kept=int(keep_back.sum()), back_removed=int((~keep_back).sum()), merged=len(merged),
                  back_to_front_scale=float(scale), front_rim_z=front_rim, back_rim_z=back_rim,
                  front_extents=front['extents'].tolist(), back_extents_after_scale=(back['extents'] * scale).tolist(),
                  all_attributes_finite=True,
                  alignment='ASSUMED: width match, centered outlines, shared rim plane. Sessions share no COLMAP link. Not measured.')
    (args.output / 'merge.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report[k] for k in ('front_kept', 'front_removed', 'back_kept', 'back_removed', 'merged',
                                             'back_to_front_scale', 'front_extents', 'back_extents_after_scale')}))


if __name__ == '__main__':
    main()
