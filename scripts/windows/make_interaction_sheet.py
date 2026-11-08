"""Summarizes one interaction run: prints the report totals and writes a labeled screenshot sheet."""
import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

SHOTS = ['01_on_bench', '02_held_front', '02b_held_front_proxy_wire', '03_held_barcode_end',
         '03b_held_barcode_end_proxy_wire', '04_held_back', '05_held_other_end',
         '07_held_underside_known_defect', '08_held_top', '10_scanner_body_in_no_scan',
         '11_scanner_barcode_in_scanned', '12_released_on_bench', '13_second_grab_held', '13b_pressed_on_bench']
WIDTH, HEIGHT, COLUMNS, LABEL = 640, 360, 3, 24


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('run', type=Path, help='Folder with interaction_report.json and screenshots.')
    p.add_argument('sheet', type=Path)
    args = p.parse_args()

    report = json.loads((args.run / 'interaction_report.json').read_text(encoding='utf-8'))
    stages = report['stages']
    failed = [s['name'] for s in stages if not s['pass']]
    print(f"{args.run}: pass={report['pass']} checks={len(stages)} failed={len(failed)} {failed} "
          f"runtime={report['runtime']} fps={report['averageFps']:.1f} lag={report['peakMovingLagMeters']:.4f} hmd={report['hmdRuntimeActive']} "
          f"real_mouse={report.get('externalMouseMotion', 'not recorded')}")
    for stage in stages:
        if stage['name'] == 'hold.proxy_blocked_by_bench':
            print('  ', stage['details'])

    rows = (len(SHOTS) + COLUMNS - 1) // COLUMNS
    sheet = Image.new('RGB', (COLUMNS * WIDTH, rows * (HEIGHT + LABEL)), 'white')
    draw = ImageDraw.Draw(sheet)
    for i, name in enumerate(SHOTS):
        x, y = i % COLUMNS * WIDTH, i // COLUMNS * (HEIGHT + LABEL)
        sheet.paste(Image.open(args.run / f'{name}.png').convert('RGB').resize((WIDTH, HEIGHT)), (x, y + LABEL))
        draw.text((x + 6, y + 6), name, fill='black')
    sheet.save(args.sheet, quality=88)


if __name__ == '__main__':
    main()
