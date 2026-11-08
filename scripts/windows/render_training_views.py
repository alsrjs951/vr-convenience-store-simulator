"""Renders a splat PLY from chosen registered frames next to the original frame, to see whether training views
are reproduced (fit) before judging novel views. Uses the same pose conversion as compare_capture_geometry.py.
Run with the nerfstudio environment's python (gsplat, torch, plyfile)."""
import argparse
import json
from pathlib import Path

import numpy as np
import torch
from gsplat import rasterization
from PIL import Image
from plyfile import PlyData


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('session', type=Path)
    p.add_argument('--run', type=Path, required=True)
    p.add_argument('--ply', type=Path, required=True)
    p.add_argument('--frames', required=True, help='Comma-separated frame numbers (frame_%%04d).')
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--scale', type=float, default=0.5)
    args = p.parse_args()
    tf = json.loads((args.session / 'nerfstudio' / 'transforms.json').read_text())
    dp = json.loads((args.run / 'dataparser_transforms.json').read_text())
    T = np.vstack([np.array(dp['transform']), [0, 0, 0, 1]])
    if 'applied_transform' in tf:
        T = T @ np.linalg.inv(np.vstack([np.array(tf['applied_transform']), [0, 0, 0, 1]]))
    T, s = T[:3], float(dp['scale'])
    v = PlyData.read(str(args.ply))['vertex'].data
    a = lambda names: np.stack([v[n] for n in names], axis=-1)
    g = lambda x: torch.tensor(x, dtype=torch.float32, device='cuda')
    rest = sorted([n for n in v.dtype.names if n.startswith('f_rest_')], key=lambda n: int(n[7:]))
    colors = np.concatenate([a([f'f_dc_{i}' for i in range(3)])[:, None, :], a(rest).reshape(len(v), 3, -1).transpose(0, 2, 1)], axis=1)
    means, quats, scales = g(a(['x', 'y', 'z'])), g(a([f'rot_{i}' for i in range(4)])), g(np.exp(a([f'scale_{i}' for i in range(3)])))
    opac, cols = torch.sigmoid(g(v['opacity'].copy())), g(colors)
    sh_degree = int(np.sqrt(colors.shape[1]) - 1)
    W, H = int(tf['w'] * args.scale), int(tf['h'] * args.scale)
    K = np.array([[tf['fl_x'] * args.scale, 0, tf['cx'] * args.scale], [0, tf['fl_y'] * args.scale, tf['cy'] * args.scale], [0, 0, 1]])
    byindex = {int(f['file_path'].split('_')[-1].split('.')[0]): f for f in tf['frames']}
    rows = []
    for n in [int(x) for x in args.frames.split(',')]:
        c2w = np.array(byindex[n]['transform_matrix'])[:3]
        R = T[:3, :3] @ c2w[:, :3]
        t = s * (T[:3, :3] @ c2w[:, 3] + T[:3, 3])
        R_cv = R @ np.diag([1, -1, -1])  # OpenGL camera -> OpenCV camera
        w2c = np.eye(4); w2c[:3, :3] = R_cv.T; w2c[:3, 3] = -R_cv.T @ t
        with torch.no_grad():
            img, _, _ = rasterization(means, quats, scales, opac, cols, g(w2c[None]), g(K[None]), W, H, sh_degree=sh_degree,
                                      backgrounds=g([[0.16, 0.18, 0.22]]))
        render = Image.fromarray((img[0].cpu().numpy().clip(0, 1) * 255).astype(np.uint8))
        frame = Image.open(args.session / 'frames' / f'frame_{n:04d}.jpg').convert('RGB').resize((W, H))
        row = Image.new('RGB', (W * 2, H)); row.paste(frame, (0, 0)); row.paste(render, (W, 0)); rows.append(row)
    sheet = Image.new('RGB', (W * 2, H * len(rows)))
    for i, r in enumerate(rows):
        sheet.paste(r, (0, i * H))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.output, quality=88)
    print(args.output)


if __name__ == '__main__':
    main()
