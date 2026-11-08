# Poster pipeline smoke-test asset

- Source: Nerfstudio team's poster dataset, https://huggingface.co/datasets/nerfstudioteam/datasets/tree/main/poster
- Local subset: 100 available source images; 90 training / 10 evaluation.
- Training: Nerfstudio 1.1.5, splatfacto, 150 iterations, RTX 4070.
- Run: `2026-11-06_050617`; exported checkpoint step 149.
- Export: `ns-export gaussian-splat`; 12,970 Gaussians, 3,218,152 bytes.
- PLY SHA256: `97649AF0E96666059047AA7088EB25D9996416C0AED8A77E6E2644183F9EF410`.
- Import: UnityGaussianSplatting v1.1.0, Float32 position/scale/SH and Float32x4 color.
- `view.json`: first source camera transformed into model coordinates, with the already-applied dataset preprocessing transform removed before applying the saved dataparser transform.
- Recreate camera metadata: run `scripts/windows/prepare_poster_view.py` in the installed nerfstudio environment from the repository root.
- Unity menu: `Tools > P0 > Import Trained Poster`. It reuses the existing converted asset and recreates `TrainedSplatTest`; remove neither source nor converted data during ordinary use.

The scene aligns this view to the XR simulator's starting camera and reflects X to convert handedness. Its units are normalized training coordinates, **not a calibrated physical scale**. This is an intentionally short training run: blur, holes, and floaters are expected. It verifies the data/render pipeline, not product quality or HMD stereo correctness.
