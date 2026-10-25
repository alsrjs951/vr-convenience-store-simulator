#!/usr/bin/env python3
"""합성 3DGS 샘플 PLY 생성기 (P0 종료 기준 1 검증용).

플러그인 저장소에 샘플 스플랫이 없어서(Unity_셋업_체크리스트.md §9 확인),
간단한 색상 공(sphere) 가우시안 스플랫을 표준 3DGS Binary PLY(SH degree 0)로 만든다.
사용: python3 scripts/tools/make_sample_splat.py [출력경로]
"""
import math
import struct
import sys
import colorsys

OUT = sys.argv[1] if len(sys.argv) > 1 else "Assets/GaussianSplats/Sample/sample_sphere.ply"
N = 1200          # 가우시안 수
RADIUS = 0.15     # 공 반지름 (m) — 상품 스케일 감
SPLAT = 0.018     # 가우시안 표준편차 (m)
C0 = 0.28209479177387814  # SH DC 계수

rows = []
golden = math.pi * (3.0 - math.sqrt(5.0))
for i in range(N):
    # 피보나치 구면 배치 + 약간의 반경 흔들림 (표면 밀도 균일)
    y = 1.0 - (i / (N - 1.0)) * 2.0
    r = math.sqrt(max(0.0, 1.0 - y * y))
    theta = golden * i
    x, z = math.cos(theta) * r, math.sin(theta) * r
    jitter = 1.0 + 0.03 * math.sin(i * 12.9898)  # 결정론적 지터
    px, py, pz = x * RADIUS * jitter, y * RADIUS * jitter, z * RADIUS * jitter

    # 위치 기반 색상 그라디언트 (HSV → RGB)
    hue = (theta / (2.0 * math.pi)) % 1.0
    rgb = colorsys.hsv_to_rgb(hue, 0.75, 1.0)
    # SH DC: color = C0 * f_dc + 0.5  →  f_dc = (color - 0.5) / C0
    f_dc = tuple((c - 0.5) / C0 for c in rgb)

    opacity = math.log(0.95 / 0.05)            # logit(0.95)
    s = SPLAT * (0.8 + 0.4 * abs(math.sin(i * 4.898)))  # 결정론적 크기 변화
    scales = tuple(math.log(s * f) for f in (1.0, 1.0, 1.0))
    rot = (1.0, 0.0, 0.0, 0.0)                 # 단위 quaternion (w,x,y,z)

    rows.append(struct.pack(
        "<3f3f3f45f1f3f4f",
        px, py, pz,
        0.0, 0.0, 0.0,                       # normals (미사용)
        f_dc[0], f_dc[1], f_dc[2],
        *([0.0] * 45),                       # f_rest_0..44 (SH degree 3 레이아웃 — 플러그인이 정점 크기 62 float 강제)
        opacity,
        scales[0], scales[1], scales[2],
        rot[0], rot[1], rot[2], rot[3],
    ))

header = "\n".join([
    "ply",
    "format binary_little_endian 1.0",
    f"element vertex {N}",
    "property float x", "property float y", "property float z",
    "property float nx", "property float ny", "property float nz",
    "property float f_dc_0", "property float f_dc_1", "property float f_dc_2",
    *(f"property float f_rest_{i}" for i in range(45)),
    "property float opacity",
    "property float scale_0", "property float scale_1", "property float scale_2",
    "property float rot_0", "property float rot_1", "property float rot_2", "property float rot_3",
    "end_header",
]).encode("ascii") + b"\n"

import os
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "wb") as f:
    f.write(header + b"".join(rows))
print(f"OK: {OUT} — {N} gaussians, {os.path.getsize(OUT)} bytes")
