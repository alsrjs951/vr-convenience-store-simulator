# Dakdari capture trial

User-supplied product capture, candidate product ID `capture_01`, video V02 (`KakaoTalk_Video_2026-10-25-01-46-01.mp4`). Original media is preserved in Downloads and hash-verified copies are under `capture/products/capture_01/raw/`.

First 84 frames sampled at 3fps were registered with COLMAP 4.2.1. The later segment was excluded after a camera-path discontinuity and visibly incorrect held-out render. Trained splatfacto for 1,000 iterations, checkpoint step 999, exported 42,274 Gaussians. Source PLY: `capture/products/capture_01/session_v02_trim/export/splat.ply`.

This asset includes the bed/background. It is a development trial, not an isolated or production-ready product. The model uses arbitrary reconstruction units, not calibrated physical dimensions. Camera orientation and handedness are converted by the editor preparation tool. See `docs/p1/닭다리_촬영본_복원시험_2026-11-06.md` for evidence and limitations.
