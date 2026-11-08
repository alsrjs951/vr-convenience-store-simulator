"""Tries to decode real barcodes from images, to compare source frames with reconstruction renders.

Each image is tried as-is and enlarged, because a decoder needs enough pixels per bar. A miss only says
this decoder failed on this image at this size. It does not prove the bars are unreadable to every
scanner, and a hit does not measure what a player can read in an HMD. --expect separates correct reads
from misreads, which do happen.
"""
import argparse
import json
import sys
from pathlib import Path

import cv2

SCALES = (1, 2, 3, 4)
IMAGE_SUFFIXES = {'.jpg', '.jpeg', '.png'}


def try_decode(detector, image):
    for scale in SCALES:
        enlarged = image if scale == 1 else cv2.resize(image, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
        found, values, _, _ = detector.detectAndDecodeWithType(enlarged)
        values = [v for v in values if v] if found else []
        if values:
            return values, scale
    return [], None


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('inputs', type=Path, nargs='+', help='Images, or folders searched recursively.')
    p.add_argument('--expect', help='The number printed on the package.')
    p.add_argument('--output', type=Path)
    args = p.parse_args()
    if not hasattr(cv2, 'barcode'):
        raise SystemExit(f'OpenCV {cv2.__version__} has no barcode module.')

    paths = []
    for item in args.inputs:
        paths += sorted(f for f in item.rglob('*') if f.suffix.lower() in IMAGE_SUFFIXES) if item.is_dir() else [item]
    detector = cv2.barcode.BarcodeDetector()
    results = []
    for path in paths:
        image = cv2.imread(str(path))
        values, scale = try_decode(detector, image)
        results.append(dict(image=path.as_posix(), width=image.shape[1], height=image.shape[0], decoded=values, scale=scale,
                            correct=bool(args.expect and args.expect in values)))
    correct = sum(r['correct'] for r in results)
    wrong = sum(bool(r['decoded']) and not r['correct'] for r in results)
    summary = dict(opencv=cv2.__version__, images=len(results), correct=correct, wrong=wrong, expected=args.expect)
    print(json.dumps(summary))
    for r in results:
        if r['decoded']:
            print(f"  {Path(r['image']).name}: {r['decoded']} at x{r['scale']} {'OK' if r['correct'] else 'WRONG'}")
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(dict(summary=summary, results=results), ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
