"""Takes training frames from a product video by sharpness instead of at fixed times.

The video is cut into windows of 1/fps seconds and the sharpest frame of each window is kept, so a frame blurred by a
quick hand movement does not get in only because it fell on the clock. Sharpness is the variance of the Laplacian over
the colourful part of the picture: over the whole picture it would mostly measure the bedding's stitching, and the
compression treats that differently from print.

Output is frame_0001.jpg, frame_0002.jpg, ... in time order, as the sequential steps of the session script expect, and
selection.json with what was kept. Reads the video only.
"""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np

SATURATED = 70
MIN_PRODUCT_PIXELS = 500


def sharpness(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    small = cv2.resize(frame, (480, 270), interpolation=cv2.INTER_AREA)
    saturated = (cv2.cvtColor(small, cv2.COLOR_BGR2HSV)[..., 1] > SATURATED).astype(np.uint8)
    mask = cv2.resize(saturated, (gray.shape[1], gray.shape[0]), interpolation=cv2.INTER_NEAREST) > 0
    values = cv2.Laplacian(gray, cv2.CV_32F)[mask]
    return float(values.var()) if values.size >= MIN_PRODUCT_PIXELS else float(cv2.Laplacian(gray, cv2.CV_32F).var())


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('video', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--fps', type=float, default=4.0)
    p.add_argument('--quality', type=int, default=98)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    capture = cv2.VideoCapture(str(args.video))
    if not capture.isOpened():
        raise SystemExit(f'Cannot open {args.video}')
    video_fps = capture.get(cv2.CAP_PROP_FPS)
    window = max(1, round(video_fps / args.fps))
    kept, scores = [], []
    best, best_score, best_index, index, written = None, -1.0, -1, 0, 0

    def flush():
        nonlocal best, best_score, written
        if best is None:
            return
        written += 1
        cv2.imwrite(str(args.output / f'frame_{written:04d}.jpg'), best, [cv2.IMWRITE_JPEG_QUALITY, args.quality])
        kept.append(best_index)
        scores.append(best_score)
        best, best_score = None, -1.0

    while True:
        ok, frame = capture.read()
        if not ok:
            break
        score = sharpness(frame)
        if score > best_score:
            best, best_score, best_index = frame, score, index
        index += 1
        if index % window == 0:
            flush()
    # A short last window would squeeze two frames together, so it is only used when it is nearly whole.
    if index % window >= window * 0.75:
        flush()
    capture.release()

    report = {'video': str(args.video), 'video_fps': video_fps, 'frames_read': index, 'window': window, 'kept': len(kept),
              'median_sharpness': float(np.median(scores)), 'kept_frame_indices': kept}
    (args.output.parent / 'selection.json').write_text(json.dumps(report, indent=1))
    print(f"kept {len(kept)} of {index} frames, window {window}, median sharpness {report['median_sharpness']:.1f}")


if __name__ == '__main__':
    main()
