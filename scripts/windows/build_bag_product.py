"""Turns a bag's two isolated sessions into a provisional Unity product: fits, merges, writes the manifest.

Run from the project root in the nerfstudio environment. A person still chooses each session's isolation
result and the print's top direction from the review renders. The manifest holds placeholders, not
measurements, and the front-back alignment is assumed (see merge_bag_shells.py).
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from plyfile import PlyData, PlyElement

from make_splat_lods import decimate_cluster

SCRIPTS = Path(__file__).parent
PRODUCTS = Path('Assets/GaussianSplats/Products')
PROVISIONAL = Path('Assets/Data/Provisional')
AXES = ['+long', '-long', '+side', '-side', '+up', '-up']
MAX_GATE_SPLATS = 150000
HAZE_MIN_OPACITY = 0.15
HAZE_MAX_SIGMA = 0.05  # of the product width
LINER_GRID = 40  # cells across the width for the backing painted inside the faces
LINER_INSET = 0.02  # of the product width: how far under the face the backing sits
LINER_BAND_END = 0.88  # no backing from here to the outline (0 = middle of the face, 1 = outline)
HAZE_GRID = 48  # cells per side of the grid over the outline in which the faces are located
HAZE_MARGIN = 0.012  # of the product width: haze reaching this far outside a face is dropped


def run(script, *args):
    subprocess.run([sys.executable, str(SCRIPTS / script), *map(str, args)], check=True, stdout=subprocess.DEVNULL)


def fit(ply, output, *options):
    run('fit_product_box.py', ply, output, *options)


def remove_haze(merged, front_fit, mode):
    """Drops the faint Gaussians and the big soft ones. They carry no print and glow around the bag where the two
    sessions meet. Stricter limits (opacity 0.3, 3% of the width) open holes in the faces.

    mode 'all' drops them everywhere. That can turn a dark or glossy patch of a face see-through or blotchy, because
    such a patch is often made of faint Gaussians (Indian Bap's back, 2026-11-12). Mode 'outside' keeps the small
    ones that lie in a face and drops those that reach outside it. The faces are found per cell of a grid over the
    outline, from where the solid Gaussians end toward the front and toward the back.
    Look at the renders of both before choosing."""
    # Read the copy: plyfile maps the file it reads, and Windows cannot overwrite a mapped file.
    shutil.copy2(merged / 'merged.ply', merged / 'merged_with_haze.ply')
    vertex = PlyData.read(str(merged / 'merged_with_haze.ply'))['vertex'].data
    axes = json.loads(Path(front_fit).read_text())['axes_in_ply']
    local = np.column_stack([vertex[n] for n in 'xyz']).astype(np.float64) @ np.stack([axes['x'], axes['y'], axes['z_front']], axis=1)
    low, high = np.percentile(local[:, :2], [0.5, 99.5], axis=0)
    width = high[0] - low[0]
    opacity = 1 / (1 + np.exp(-vertex['opacity'].astype(np.float64)))
    sigma = np.exp(np.column_stack([vertex[f'scale_{i}'] for i in range(3)]).astype(np.float64)).max(axis=1)
    haze = (opacity < HAZE_MIN_OPACITY) | (sigma > HAZE_MAX_SIGMA * width)

    cells = np.floor((local[:, :2] - low) / (high - low) * HAZE_GRID).astype(int)
    inside = ((cells >= 0) & (cells < HAZE_GRID)).all(axis=1)
    index = np.where(inside, cells[:, 0] * HAZE_GRID + cells[:, 1], -1)
    solid = inside & (opacity >= 0.5) & ~haze
    front = np.full(HAZE_GRID * HAZE_GRID, np.nan)
    back = np.full(HAZE_GRID * HAZE_GRID, np.nan)
    order = np.argsort(index[solid])
    ids, heights = index[solid][order], local[solid, 2][order]
    filled, starts = np.unique(ids, return_index=True)
    for cell, group in zip(filled, np.split(heights, starts[1:])):
        if len(group) >= 4:
            front[cell], back[cell] = np.percentile(group, 90), np.percentile(group, 10)
    # A Gaussian sticks out by how far its centre is outside the face plus its own size: a big faint one centred
    # in the face still glows past it. What stays is small and lies in a face.
    known = inside & ~np.isnan(front[np.clip(index, 0, None)])
    sticks_out = np.ones(len(vertex), dtype=bool)  # where no solid Gaussian marks a face, haze has nothing to belong to
    beyond = np.maximum(local[known, 2] - front[index[known]], back[index[known]] - local[known, 2])
    sticks_out[known] = beyond + sigma[known] > HAZE_MARGIN * width
    keep = ~(haze & sticks_out) if mode == 'outside' else ~haze
    PlyData([PlyElement.describe(vertex[keep], 'vertex')], byte_order='<').write(str(merged / 'merged.ply'))
    return int(keep.sum())


def add_liner(merged, front_fit, rim_z, faces):
    """Paints the inside of each face with that face's own colours, cell by cell, so a see-through patch shows a blur
    of its own print instead of the other face's print mirrored (a dark or glossy patch often comes out half
    transparent: Kokalkon's brown, Indian Bap's black band).

    One opaque flat Gaussian per grid cell sits a little under the face, coloured with the mean of the face's
    half-transparent Gaussians in that cell. It is INVENTED content: not reconstructed, only a backing. It stays inside the
    bag, away from the outline, so it is hidden wherever the face itself is solid."""
    from scipy.ndimage import distance_transform_edt
    from scipy.spatial.transform import Rotation

    shutil.copy2(merged / 'merged.ply', merged / 'merged_without_liner.ply')
    vertex = PlyData.read(str(merged / 'merged_without_liner.ply'))['vertex'].data
    fit_data = json.loads(Path(front_fit).read_text())
    axes = fit_data['axes_in_ply']
    basis = np.stack([axes['x'], axes['y'], axes['z_front']], axis=1)
    center = np.array(fit_data['center_in_ply'])
    local = (np.column_stack([vertex[n] for n in 'xyz']).astype(np.float64) - center) @ basis
    low, high = np.percentile(local[:, :2], [0.5, 99.5], axis=0)
    width = high[0] - low[0]
    grid = np.array([LINER_GRID, max(1, round(LINER_GRID * (high[1] - low[1]) / width))])
    cell = (high - low) / grid
    cells = np.floor((local[:, :2] - low) / cell).astype(int)
    inside = ((cells >= 0) & (cells < grid)).all(axis=1)
    opacity = 1 / (1 + np.exp(-vertex['opacity'].astype(np.float64)))
    colour = np.column_stack([vertex[f'f_dc_{i}'] for i in range(3)]).astype(np.float64)

    proper = basis.copy()
    if np.linalg.det(proper) < 0:
        proper[:, 2] = -proper[:, 2]  # a flat disc looks the same either way; the quaternion needs a rotation
    quat = Rotation.from_matrix(proper).as_quat()  # x, y, z, w
    inset = LINER_INSET * width
    centres = low + (np.stack(np.meshgrid(np.arange(grid[0]), np.arange(grid[1]), indexing='ij'), axis=-1) + 0.5) * cell
    band = (np.abs(centres - (low + high) / 2) / ((high - low) / 2)).max(axis=-1)
    covered = np.zeros(grid, dtype=bool)  # cells where the bag has anything at all, from either face
    np.add.at(covered, (cells[inside, 0], cells[inside, 1]), True)
    rows = []
    for sign in {'both': (1, -1), 'front': (1,), 'back': (-1,)}[faces]:  # front face above the rim, back face below it
        face = inside & (sign * (local[:, 2] - rim_z) > 0) & (opacity >= 0.05)
        heights = np.full(grid, np.nan)
        colours = np.full((*grid, 3), np.nan)
        keys = cells[face, 0] * grid[1] + cells[face, 1]
        order = np.argsort(keys)
        keys, starts = np.unique(keys[order], return_index=True)
        for key, group in zip(keys, np.split(np.flatnonzero(face)[order], starts[1:])):
            if len(group) < 3:
                continue
            cx, cy = divmod(int(key), int(grid[1]))
            # The backing is seen only through the half-transparent Gaussians, so it takes THEIR colour. Weighting by
            # opacity instead paints a dark panel with the colour of its solid white lettering (Kokalkon's back).
            weight = 1.05 - opacity[group]
            heights[cx, cy] = np.median(local[group, 2])
            colours[cx, cy] = (colour[group] * weight[:, None]).sum(axis=0) / weight.sum()
        # A see-through patch is exactly where a face has few Gaussians, so cells without any take their nearest
        # neighbour's height and colour: the backing must not have holes the other face's backing shows through.
        missing = np.isnan(heights)
        if missing.all():
            continue
        nearest = distance_transform_edt(missing, return_distances=False, return_indices=True)
        heights, colours = heights[nearest[0], nearest[1]], colours[nearest[0], nearest[1]]
        for cx, cy in zip(*np.nonzero(covered & (band <= LINER_BAND_END))):
            height = heights[cx, cy] - sign * inset
            if sign * (height - rim_z) < 0.1 * inset:
                height = rim_z + sign * 0.1 * inset  # a flat spot: keep the two backings on their own sides of the rim plane
            rows.append((np.array([centres[cx, cy, 0], centres[cx, cy, 1], height]), colours[cx, cy]))
    liner = np.zeros(len(rows), dtype=vertex.dtype)
    for i, (position, mean_colour) in enumerate(rows):
        world = center + basis @ position
        for axis, name in enumerate('xyz'):
            liner[name][i] = world[axis]
        for channel in range(3):
            liner[f'f_dc_{channel}'][i] = mean_colour[channel]
    liner['opacity'] = 3.5  # sigmoid = 0.97
    liner['scale_0'], liner['scale_1'] = np.log(0.7 * cell[0]), np.log(0.7 * cell[1])
    liner['scale_2'] = np.log(0.002 * width)
    for name, value in zip(('rot_1', 'rot_2', 'rot_3', 'rot_0'), quat):
        liner[name] = value
    both = np.concatenate([np.asarray(vertex), liner])
    PlyData([PlyElement.describe(both, 'vertex')], byte_order='<').write(str(merged / 'merged.ply'))
    return len(liner)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('id')
    p.add_argument('--capture', required=True)
    p.add_argument('--name', required=True, help='Display name, marked as a development product.')
    p.add_argument('--scene', required=True, help='Scene file name without extension.')
    p.add_argument('--front', type=Path, required=True, help='Isolated folder of the front session.')
    p.add_argument('--back', type=Path, required=True, help='Isolated folder of the back session.')
    p.add_argument('--front-top', choices=AXES, required=True)
    p.add_argument('--back-top', choices=AXES, required=True)
    p.add_argument('--width-m', type=float, default=0.2)
    p.add_argument('--mass-kg', type=float, default=0.1)
    p.add_argument('--box-thickness', type=float, help='Flat box only: see merge_bag_shells.py.')
    p.add_argument('--remove-haze', choices=['all', 'outside'], help='Drop faint and oversized Gaussians after the join: see remove_haze.')
    p.add_argument('--liner', choices=['both', 'front', 'back'],
                   help='Back each face from inside with its own colours, against see-through patches: see add_liner.')
    p.add_argument('--keep-manifest', action='store_true',
                   help='Rebuilding a product that is already in the game: leave its manifest (name, price, size, mass) '
                        'as it is.')
    p.add_argument('--no-install', action='store_true',
                   help='Stop after the join, with merged.ply and its box fit in the capture folder, and leave Assets alone. '
                        'For looking at a rebuild before it replaces the product in the game.')
    args = p.parse_args()

    front_fit, back_fit = args.front / 'box_fit.json', args.back / 'box_fit.json'
    fit(args.front / 'isolated.ply', front_fit, '--flat', '--front=+up', f'--top={args.front_top}')
    fit(args.back / 'isolated.ply', back_fit, '--flat', '--front=+up', f'--top={args.back_top}')

    merged = Path('capture/products') / args.capture / 'merged'
    box = [f'--box-thickness={args.box_thickness}'] if args.box_thickness else []
    run('merge_bag_shells.py', args.front / 'isolated.ply', front_fit, args.back / 'isolated.ply', back_fit, merged, *box)
    report = json.loads((merged / 'merge.json').read_text())
    count = report['merged']
    if args.remove_haze:
        count = remove_haze(merged, front_fit, args.remove_haze)
        report['after_haze_removal'] = count
        (merged / 'merge.json').write_text(json.dumps(report, indent=2))
    if args.liner:
        report['liner'] = add_liner(merged, front_fit, report['front_rim_z'], args.liner)
        count += report['liner']
        (merged / 'merge.json').write_text(json.dumps(report, indent=2))
    if count > MAX_GATE_SPLATS:
        # 30,000-iteration sessions overshoot the gate a little. Merging near neighbors of similar color keeps the covered
        # area and the print's edges, where a plain cut leaves holes. The untouched result stays beside it.
        shutil.copy2(merged / 'merged.ply', merged / 'merged_full.ply')
        light = decimate_cluster(PlyData.read(str(merged / 'merged.ply'))['vertex'].data, MAX_GATE_SPLATS)
        PlyData([PlyElement.describe(light, 'vertex')], byte_order='<').write(str(merged / 'merged.ply'))
        count = len(light)

    if args.no_install:
        fit(merged / 'merged.ply', merged / 'box_fit.json', f'--axes-from={front_fit}')
        print(f"{args.id}: not installed. gaussians={count} merged={report['merged']} front_kept={report['front_kept']} "
              f"back_kept={report['back_kept']} in {merged}")
        return
    box_fit = PROVISIONAL / f'{args.id}.box_fit.json'
    fit(merged / 'merged.ply', box_fit, f'--axes-from={front_fit}')
    PRODUCTS.mkdir(parents=True, exist_ok=True)
    shutil.copy2(merged / 'merged.ply', PRODUCTS / f'{args.id}.ply')

    manifest = {
        'id': args.id,
        'capture_id': args.capture,
        'display_name': args.name,
        'price': 0,
        'splat_asset': f'{PRODUCTS.as_posix()}/{args.id}.asset',
        'box_fit': box_fit.as_posix(),
        'scene': f'Assets/Scenes/{args.scene}.unity',
        'evidence_dir': f"docs/p1/evidence/{args.id.removeprefix('dev_').removesuffix('_01')}_interaction",
        'provisional_width_m': args.width_m,
        'provisional_mass_kg': args.mass_kg,
        'barcode_face': 'Back',
        'barcode_local_normal': {'x': 0, 'y': 0, 'z': -1},
        'note': 'NOT measured. Width, mass and price are development placeholders; other sizes follow the '
                "reconstruction's ratios. Front and back are separate sessions joined by an ASSUMED alignment "
                '(scripts/windows/merge_bag_shells.py). Do not copy to Data/products.csv.',
    }
    manifest_path = PROVISIONAL / f'{args.id}.json'
    if args.keep_manifest and manifest_path.exists():
        # Only the splat and its box changed. Rewriting the file would also change its line ends and note.
        print(f'{args.id}: manifest kept ({manifest_path})')
    else:
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    extents = json.loads(box_fit.read_text())['extents_ply_units_xyz']
    size_cm = [round(v / extents[0] * args.width_m * 100, 2) for v in extents]
    front_h, back_h = report['front_extents'][1], report['back_extents_after_scale'][1]
    print(f"{args.id}: gaussians={count} merged={report['merged']} front_kept={report['front_kept']} back_kept={report['back_kept']} "
          f"size_cm={size_cm} height_mismatch={abs(front_h - back_h) / front_h * 100:.1f}%")


if __name__ == '__main__':
    main()
