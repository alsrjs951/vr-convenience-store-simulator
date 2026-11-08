"""Summarize a COLMAP trial and plot its sparse scene and camera path."""
import argparse
import json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from nerfstudio.data.utils.colmap_parsing_utils import read_model

parser = argparse.ArgumentParser()
parser.add_argument("session", type=Path)
args = parser.parse_args()
session = args.session
summaries = []
for model in sorted((session / "sparse").iterdir()):
    if not (model / "images.bin").exists():
        continue
    cameras, images, points = read_model(model, ext=".bin")
    ordered = sorted(images.values(), key=lambda image: image.name)
    centers = np.array([-image.qvec2rotmat().T @ image.tvec for image in ordered])
    xyz = np.array([point.xyz for point in points.values()])
    rgb = np.array([point.rgb for point in points.values()]) / 255
    errors = np.array([point.error for point in points.values()])
    lengths = np.array([len(point.image_ids) for point in points.values()])
    total = len(list((session / "frames").glob("*.jpg")))
    summary = dict(model=model.name, input_frames=total, registered=len(images),
                   registration_fraction=len(images)/total, points=len(points),
                   reprojection_error_mean_px=float(errors.mean()),
                   reprojection_error_median_px=float(np.median(errors)),
                   median_track_length=float(np.median(lengths)),
                   cameras=[dict(model=c.model,params=c.params.tolist()) for c in cameras.values()],
                   registered_files=[image.name for image in ordered])
    summaries.append(summary)
    lo,hi=np.percentile(xyz,[2,98],axis=0)
    keep=((xyz>=lo)&(xyz<=hi)).all(axis=1)
    xyz,rgb=xyz[keep][::3],rgb[keep][::3]
    fig=plt.figure(figsize=(14,6))
    for i,(elev,azim) in enumerate(((25,-60),(80,-90))):
        ax=fig.add_subplot(1,2,i+1,projection="3d")
        ax.scatter(*xyz.T,c=rgb,s=1)
        ax.plot(*centers.T,color="red",linewidth=1)
        ax.scatter(*centers[::10].T,color="red",s=12)
        for j in range(0,len(ordered),10):
            ax.text(*centers[j],str(j),fontsize=7)
        ax.set_title(f"model {model.name}: {len(images)}/{total} cameras; {len(points)} points")
        ax.view_init(elev=elev,azim=azim)
        ax.set_xlabel("X");ax.set_ylabel("Y");ax.set_zlabel("Z")
    fig.tight_layout();fig.savefig(session/f"reconstruction_{model.name}.png",dpi=130);plt.close(fig)
(session/"reconstruction_summary.json").write_text(json.dumps(summaries,indent=2))
print(json.dumps([{k:v for k,v in s.items() if k!='registered_files'} for s in summaries],indent=2))
