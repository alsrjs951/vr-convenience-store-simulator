"""Prepares ONE reconstruction of a product face from several videos of that face (for example the 11/08 video from
above and the 11/12 video from a low angle), so the result has both the straight-on views and the side views.

The product lay somewhere else in each video, so the background disagrees between them. Each source session has
already been reconstructed on its own; its isolated product is rendered into every one of its frames to get a mask
of the product. COLMAP then looks for features only inside the masks and sees one rigid product filmed by all the
cameras, and training compares only the masked pixels (nerfstudio's mask_path).

ASSUMPTION: the product kept its shape between the videos. A bag that was dented in between comes out doubled or
blurred; look at the renders before using the result.

Writes <out>/frames, <out>/masks, <out>/sparse and the nerfstudio dataset <out>/nerfstudio. Training is a separate
step. Run from the project root with the nerfstudio environment active (colmap, ns-process-data on PATH).

--variant with --region makes a second set of masks over the same poses, in which one source only contributes part
of the face (for example the top half, where its print is clean, and not the half where it filmed a glare). The
variant is trained from <out>/nerfstudio/transforms_<variant>.json.
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
import torch
from gsplat import rasterization
from plyfile import PlyData


def run(command, log):
    with open(log, 'w', encoding='utf-8') as f:
        result = subprocess.run(command, stdout=f, stderr=subprocess.STDOUT, shell=isinstance(command, str))
    if result.returncode != 0:
        raise RuntimeError(f'failed ({result.returncode}), see {log}')


def write_masks(tag, session, run_dir, ply, out, alpha_threshold, grow_px, masks='masks', region=None):
    """Copies the session's registered frames and writes the product mask of each one.

    region = (fit_product_box json, low, high) keeps only the part of the product between those fractions of the
    print's height (0 = bottom, 1 = top) in the mask."""
    tf = json.loads((session / 'nerfstudio' / 'transforms.json').read_text())
    dp = json.loads((run_dir / 'dataparser_transforms.json').read_text())
    T = np.vstack([np.array(dp['transform']), [0, 0, 0, 1]])
    if 'applied_transform' in tf:
        T = T @ np.linalg.inv(np.vstack([np.array(tf['applied_transform']), [0, 0, 0, 1]]))
    T, s = T[:3], float(dp['scale'])
    v = PlyData.read(str(ply))['vertex'].data
    a = lambda names: np.stack([v[n] for n in names], axis=-1)
    g = lambda x: torch.tensor(x, dtype=torch.float32, device='cuda')
    means, quats, scales = g(a(['x', 'y', 'z'])), g(a([f'rot_{i}' for i in range(4)])), g(np.exp(a([f'scale_{i}' for i in range(3)])))
    opacity = torch.sigmoid(g(v['opacity'].copy()))
    if region:
        fit, low, high = region
        fit = json.loads(Path(fit).read_text())
        height = (a(['x', 'y', 'z']).astype(np.float64) - np.array(fit['center_in_ply'])) @ np.array(fit['axes_in_ply']['y'])
        height = height / fit['extents_ply_units_xyz'][1] + 0.5
        inside = g(((height >= low) & (height <= high)).astype(np.float32))
        opacity = opacity * inside
    white = torch.ones((len(v), 3), device='cuda')
    W, H = int(tf['w']), int(tf['h'])
    K = np.array([[tf['fl_x'], 0, tf['cx']], [0, tf['fl_y'], tf['cy']], [0, 0, 1]])
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * grow_px + 1, 2 * grow_px + 1))
    coverage = []
    for frame in sorted(tf['frames'], key=lambda f: f['file_path']):
        source = session / 'nerfstudio' / frame['file_path']
        name = f'{tag}_{source.name}'
        c2w = np.array(frame['transform_matrix'])[:3]
        R = (T[:3, :3] @ c2w[:, :3]) @ np.diag([1, -1, -1])  # OpenGL camera -> OpenCV camera
        t = s * (T[:3, :3] @ c2w[:, 3] + T[:3, 3])
        w2c = np.eye(4)
        w2c[:3, :3], w2c[:3, 3] = R.T, -R.T @ t
        with torch.no_grad():
            _, alpha, _ = rasterization(means, quats, scales, opacity, white, g(w2c[None]), g(K[None]), W, H)
        mask = (alpha[0, :, :, 0].cpu().numpy() >= alpha_threshold).astype(np.uint8) * 255
        # Close pinholes, keep the largest blob, then grow a little: the mask comes from a pinhole render of an
        # approximate model, and a mask cut inside the outline would crop the product's edge.
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask)
        if count > 1:
            mask = np.where(labels == 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA]), 255, 0).astype(np.uint8)
        mask = cv2.dilate(mask, kernel)
        if masks == 'masks':
            shutil.copy2(source, out / 'frames' / name)
        cv2.imwrite(str(out / masks / f'{name}.png'), mask)  # COLMAP reads <image name>.png
        coverage.append(float((mask > 0).mean()))
    return dict(tag=tag, session=str(session), ply=str(ply), frames=len(coverage), mask_fraction_median=float(np.median(coverage)))


def write_variant(args, out):
    """Masks <out>/masks_<variant> and <out>/nerfstudio/transforms_<variant>.json, with the poses left as they are."""
    regions = {}
    for spec in args.region:
        tag, fit, span = spec.split('=')
        regions[tag] = (fit, *map(float, span.split(':')))
    masks = f'masks_{args.variant}'
    (out / masks).mkdir(exist_ok=True)
    for spec in args.source:
        tag, session, run_dir, ply = spec.split('=')
        write_masks(tag, Path(session), Path(run_dir), Path(ply), out, args.alpha, args.grow_px, masks, regions.get(tag))
    dataset = out / 'nerfstudio'
    (dataset / masks).mkdir(exist_ok=True)
    tf = json.loads((dataset / 'transforms.json').read_text())
    for frame in tf['frames']:
        mask_name = f'{Path(frame["file_path"]).stem}.png'
        shutil.copy2(out / masks / f'{frame["source_frame"]}.png', dataset / masks / mask_name)
        frame['mask_path'] = f'{masks}/{mask_name}'
    (dataset / f'transforms_{args.variant}.json').write_text(json.dumps(tf, indent=4))
    print(f'PASS variant {args.variant}: {dataset / f"transforms_{args.variant}.json"} regions {regions}')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('output', type=Path)
    p.add_argument('--source', action='append', required=True,
                   help='tag=session_folder=run_folder=isolated.ply (repeat). The run folder holds dataparser_transforms.json.')
    p.add_argument('--alpha', type=float, default=0.5)
    p.add_argument('--grow-px', type=int, default=6)
    p.add_argument('--variant', help='Name of a second mask set over the poses that are already there.')
    p.add_argument('--region', action='append', default=[],
                   help='With --variant: tag=fit_product_box.json=low:high, the part of the print height this source keeps.')
    args = p.parse_args()
    out = args.output
    for name in ('frames', 'masks', 'sparse'):
        (out / name).mkdir(parents=True, exist_ok=True)

    sources = []
    if not (out / 'sources.json').exists():
        for spec in args.source:
            tag, session, run_dir, ply = spec.split('=')
            sources.append(write_masks(tag, Path(session), Path(run_dir), Path(ply), out, args.alpha, args.grow_px))
        (out / 'sources.json').write_text(json.dumps(sources, indent=2))
    else:
        sources = json.loads((out / 'sources.json').read_text())
    print(json.dumps(sources))
    if args.variant:
        write_variant(args, out)
        return

    database = out / 'database.db'
    if not (out / 'sparse' / '0' / 'images.bin').exists():
        if database.exists():
            database.unlink()
        run(f'colmap feature_extractor --database_path "{database}" --image_path "{out / "frames"}" '
            f'--ImageReader.mask_path "{out / "masks"}" --ImageReader.camera_model SIMPLE_RADIAL --ImageReader.single_camera 1',
            out / 'features.log')
        run(f'colmap exhaustive_matcher --database_path "{database}"', out / 'matching.log')
        run(f'colmap mapper --database_path "{database}" --image_path "{out / "frames"}" --output_path "{out / "sparse"}"',
            out / 'mapping.log')
    run([sys.executable, 'scripts/windows/audit_product_reconstruction.py', str(out)], out / 'audit.log')
    models = json.loads((out / 'reconstruction_summary.json').read_text())
    models = models if isinstance(models, list) else [models]
    best = max(models, key=lambda m: m['registered'])
    per_tag = {s['tag']: sum(1 for f in best['registered_files'] if Path(f).name.startswith(s['tag'] + '_')) for s in sources}
    print(f"model {best['model']}: {best['registered']}/{best['input_frames']} registered, per source {per_tag}, "
          f"points {best['points']}, error {best['reprojection_error_mean_px']:.3f}px")

    dataset = out / 'nerfstudio'
    if not (dataset / 'transforms.json').exists():
        run(f'ns-process-data images --data "{out / "frames"}" --output-dir "{dataset}" --skip-colmap '
            f'--colmap-model-path ..\\sparse\\{best["model"]} --num-downscales 2', out / 'processing.log')
    # ns-process-data renames the images to frame_00001.jpg ... in sorted order; give every frame its mask under that name.
    originals = sorted(f.name for f in (out / 'frames').iterdir())
    (dataset / 'masks').mkdir(exist_ok=True)
    tf = json.loads((dataset / 'transforms.json').read_text())
    for frame in tf['frames']:
        index = int(Path(frame['file_path']).stem.split('_')[-1])
        mask_name = f'{Path(frame["file_path"]).stem}.png'
        shutil.copy2(out / 'masks' / f'{originals[index - 1]}.png', dataset / 'masks' / mask_name)
        frame['mask_path'] = f'masks/{mask_name}'
        frame['source_frame'] = originals[index - 1]
    (dataset / 'transforms.json').write_text(json.dumps(tf, indent=4))
    print(f'PASS prepared: {out} ({len(tf["frames"])} frames with masks)')


if __name__ == '__main__':
    main()
