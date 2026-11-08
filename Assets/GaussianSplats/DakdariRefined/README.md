# Refined Dakdari development asset

Same V01 capture and COLMAP reconstruction as DakdariIsolated, freshly trained for 10,000 iterations after checkpoint continuation failed on a Gaussian-count mismatch. Source checkpoint: `capture/products/capture_01/session_v01/outputs/dakdari-v01/splatfacto/trial-10000-fresh/nerfstudio_models/step-000009999.ckpt`.

Full export: 154,376 Gaussians. Multiview silhouette + largest-component filtering retained 50,854 Gaussians (12,613,322-byte PLY). All PLY attributes checked finite. Input captures and the earlier 3,000-step assets remain intact.

On the identical seven held-out source images, mean product-ROI PSNR improved from 20.59 to 26.59 dB. This measures the full model before isolation, not barcode decodability, physical scale, or unseen-face completeness. See `docs/p1/닭다리_기존자료_품질개선_2026-11-06.md`.

Preview: `Assets/Scenes/DakdariRefined.unity`. Units are uncalibrated. Underside is unobserved; some residual edges/reflection errors remain. This is a development asset, not an HMD-tested final product.
