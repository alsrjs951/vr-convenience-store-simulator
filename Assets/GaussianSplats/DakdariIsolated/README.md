# Dakdari isolated development asset

Source: user-supplied V01, `KakaoTalk_Video_2026-10-25-01-45-53.mp4`. Independent reconstruction, **not merged with V02**. Original captures and both full-scene reconstructions remain unchanged.

- 74 frames at 3fps; COLMAP registered 74/74, 27,298 sparse points.
- Nerfstudio splatfacto, 3,000 iterations, checkpoint step 2999.
- Full export 162,146 Gaussians; isolated export 34,417 Gaussians, 8,536,946 bytes.
- Isolation: `scripts/windows/isolate_dakdari.py`. Sixteen undistorted training views; red box convex-hull silhouettes plus a small margin; Gaussian center supported by at least 90% of valid views (minimum six); keep largest DBSCAN component. This product-specific heuristic must not be blindly applied to other products.
- Gaussian extents are not clipped. Edges can retain splat tails; underside is unobserved. Top fine print has artifacts already visible in the full-scene reconstruction.
- Preview scene: `Assets/Scenes/DakdariIsolated.unity`; menu `Tools > P1 > Prepare Isolated Dakdari`. Existing converted assets are reused. The model is in normalized reconstruction units, not calibrated meters.
- Source full checkpoint and PLY: `capture/products/capture_01/session_v01/`; selected PLY: `isolated/dakdari_isolated.ply`.

This is a background-separated development asset. It is not a final barcode-quality, fully covered, or HMD-validated product.
