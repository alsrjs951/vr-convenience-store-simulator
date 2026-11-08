"""Renders one session's isolated PLY from fixed views in a product frame, so single sessions can be compared
without the other session of a bag join.

The product frame comes from a fit_product_box.py result (x, y = top of print, z = front; extents). Distances
are in multiples of the fitted width (x extent), so two reconstructions of the same product are seen at the
same relative distance whatever their arbitrary scale. If the fit belongs to another run of the SAME COLMAP
model (a frame-subset ablation), pass --fit-run: cameras are defined in that run's export frame and carried
into this run's export frame through COLMAP space, so the splat and its view-dependent color stay untouched.
gsplat render with a 40 degree vertical FOV, like P1CaptureCompare. Pictures for people to judge.
Run with the nerfstudio environment's python.
"""
import argparse
import json
from pathlib import Path

import numpy as np
import torch
from gsplat import rasterization
from PIL import Image
from plyfile import PlyData

VIEWS = [  # name, yaw, pitch (degrees, product frame), distance in widths
    ('01_front', 0, 0, 3.5), ('02_oblique_right', 45, 20, 3.5), ('03_edge_a', 90, 0, 3.5),
    ('04_edge_b', 270, 0, 3.5), ('05_edge_top', 0, 89, 3.5), ('06_edge_bottom', 0, -89, 3.5),
    ('07_low_front', 0, -35, 3.5), ('08_underside', 180, 0, 3.5), ('09_held_front', 0, 0, 2.0),
    ('10_held_edge_a', 90, 0, 2.0), ('11_close_front', 0, 0, 1.25),
]


def export_affine(run, session):
    """x_export = s * (R @ p_saved + t) for points p in the saved transforms.json space."""
    tf = json.loads((session / 'nerfstudio' / 'transforms.json').read_text())
    dp = json.loads((run / 'dataparser_transforms.json').read_text())
    T = np.vstack([np.array(dp['transform']), [0, 0, 0, 1]])
    if 'applied_transform' in tf:
        T = T @ np.linalg.inv(np.vstack([np.array(tf['applied_transform']), [0, 0, 0, 1]]))
    return T[:3, :3], T[:3, 3], float(dp['scale'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('ply', type=Path)
    p.add_argument('fit', type=Path)
    p.add_argument('output', type=Path, help='Folder for <prefix>__<view>.png')
    p.add_argument('--prefix', required=True)
    p.add_argument('--run', type=Path, help='This PLY\'s run folder (with --fit-run).')
    p.add_argument('--session', type=Path, help='This PLY\'s session folder (with --fit-run).')
    p.add_argument('--fit-run', type=Path, help='Run folder the fit belongs to, if not this one.')
    p.add_argument('--fit-session', type=Path)
    args = p.parse_args()
    fit = json.loads(args.fit.read_text())
    ax = fit['axes_in_ply']
    B = np.stack([ax['x'], ax['y'], ax['z_front']], axis=1)
    center = np.array(fit['center_in_ply'])
    width = float(fit['extents_ply_units_xyz'][0])

    to_this = lambda x: x
    rot_this = lambda d: d
    if args.fit_run:
        Rf, tf_, sf = export_affine(args.fit_run, args.fit_session)
        Rt, tt, st = export_affine(args.run, args.session)
        to_this = lambda x: st * (Rt @ (Rf.T @ (x / sf - tf_)) + tt)
        rot_this = lambda d: Rt @ (Rf.T @ d)

    v = PlyData.read(str(args.ply))['vertex'].data
    a = lambda names: np.stack([v[n] for n in names], axis=-1)
    g = lambda x: torch.tensor(np.asarray(x), dtype=torch.float32, device='cuda')
    rest = sorted([n for n in v.dtype.names if n.startswith('f_rest_')], key=lambda n: int(n[7:]))
    colors = np.concatenate([a([f'f_dc_{i}' for i in range(3)])[:, None, :], a(rest).reshape(len(v), 3, -1).transpose(0, 2, 1)], axis=1)
    means, quats, scales = g(a(['x', 'y', 'z'])), g(a([f'rot_{i}' for i in range(4)])), g(np.exp(a([f'scale_{i}' for i in range(3)])))
    opac, cols = torch.sigmoid(g(v['opacity'].copy())), g(colors)
    sh = int(np.sqrt(colors.shape[1]) - 1)
    W, H = 1200, 900
    f = (H / 2) / np.tan(np.radians(20))
    K = np.array([[f, 0, W / 2], [0, f, H / 2], [0, 0, 1]])
    args.output.mkdir(parents=True, exist_ok=True)
    for name, yaw, pitch, dist in VIEWS:
        y, pt = np.radians(yaw), np.radians(pitch)
        d_local = np.array([np.sin(y) * np.cos(pt), np.sin(pt), np.cos(y) * np.cos(pt)])
        up_local = np.array([0, 0, -1.0]) if pitch > 80 else np.array([0, 0, 1.0]) if pitch < -80 else np.array([0, 1.0, 0])
        eye = to_this(center + B @ d_local * dist * width)
        target = to_this(center)
        up = rot_this(B @ up_local)
        fwd = (target - eye) / np.linalg.norm(target - eye)
        right = np.cross(fwd, up); right /= np.linalg.norm(right)
        down = np.cross(fwd, right)
        w2c = np.eye(4); w2c[:3, :3] = np.stack([right, down, fwd]); w2c[:3, 3] = -w2c[:3, :3] @ eye
        with torch.no_grad():
            img, _, _ = rasterization(means, quats, scales, opac, cols, g(w2c[None]), g(K[None]), W, H, sh_degree=sh,
                                      backgrounds=g([[0.16, 0.18, 0.22]]))
        Image.fromarray((img[0].cpu().numpy().clip(0, 1) * 255).astype(np.uint8)).save(args.output / f'{args.prefix}__{name}.png')
    print(args.output, args.prefix, len(v))


if __name__ == '__main__':
    main()
