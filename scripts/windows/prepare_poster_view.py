"""Recreate the Unity preview camera from the verified Nerfstudio training run."""
import json
from pathlib import Path
import numpy as np

work = Path.home() / "nerfstudio-work"
metadata = json.loads((work / "data/nerfstudio/poster/transforms.json").read_text())
parser = json.loads((work / "outputs/p0-smoke/splatfacto/2026-11-06_050617/dataparser_transforms.json").read_text())
transform = np.eye(4)
transform[:3] = parser["transform"]
applied = np.eye(4)
applied[:3] = metadata["applied_transform"]
# Saved dataparser transform includes the preprocessing transform, while the
# dataset's cameras already have it applied. Undo that factor exactly once.
camera = transform @ np.linalg.inv(applied) @ np.array(metadata["frames"][0]["transform_matrix"])
camera[:3, 3] *= parser["scale"]


def vector(values):
    return dict(zip(("x", "y", "z"), map(float, values)))


view = dict(position=vector(camera[:3, 3]), forward=vector(-camera[:3, 2]), up=vector(camera[:3, 1]))
Path("Assets/GaussianSplats/Poster/view.json").write_text(json.dumps(view, indent=2))
