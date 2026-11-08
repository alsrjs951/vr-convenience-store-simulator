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

SCRIPTS = Path(__file__).parent
PRODUCTS = Path('Assets/GaussianSplats/Products')
PROVISIONAL = Path('Assets/Data/Provisional')
AXES = ['+long', '-long', '+side', '-side', '+up', '-up']
MAX_GATE_SPLATS = 150000


def run(script, *args):
    subprocess.run([sys.executable, str(SCRIPTS / script), *map(str, args)], check=True, stdout=subprocess.DEVNULL)


def fit(ply, output, *options):
    run('fit_product_box.py', ply, output, *options)


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
    args = p.parse_args()

    front_fit, back_fit = args.front / 'box_fit.json', args.back / 'box_fit.json'
    fit(args.front / 'isolated.ply', front_fit, '--flat', '--front=+up', f'--top={args.front_top}')
    fit(args.back / 'isolated.ply', back_fit, '--flat', '--front=+up', f'--top={args.back_top}')

    merged = Path('capture/products') / args.capture / 'merged'
    run('merge_bag_shells.py', args.front / 'isolated.ply', front_fit, args.back / 'isolated.ply', back_fit, merged)
    report = json.loads((merged / 'merge.json').read_text())
    if report['merged'] > MAX_GATE_SPLATS:
        raise SystemExit(f"{args.id}: {report['merged']} Gaussians exceed the {MAX_GATE_SPLATS} gate.")

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
    (PROVISIONAL / f'{args.id}.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    extents = json.loads(box_fit.read_text())['extents_ply_units_xyz']
    size_cm = [round(v / extents[0] * args.width_m * 100, 2) for v in extents]
    front_h, back_h = report['front_extents'][1], report['back_extents_after_scale'][1]
    print(f"{args.id}: merged={report['merged']} front_kept={report['front_kept']} back_kept={report['back_kept']} "
          f"size_cm={size_cm} height_mismatch={abs(front_h - back_h) / front_h * 100:.1f}%")


if __name__ == '__main__':
    main()
