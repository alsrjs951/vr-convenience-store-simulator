"""Conservative multi-view silhouette filtering for the red Dakdari box trial.

This is product-specific preprocessing, not a general segmentation model.
Original captures, checkpoint, and full-scene PLY remain unchanged.
"""
import argparse
import json
from pathlib import Path
import cv2
import numpy as np
import torch
from PIL import Image, ImageDraw
from plyfile import PlyData, PlyElement
from sklearn.cluster import DBSCAN
from nerfstudio.utils.eval_utils import eval_setup

p = argparse.ArgumentParser()
p.add_argument("config", type=Path)
p.add_argument("ply", type=Path)
p.add_argument("output", type=Path)
args = p.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
_, pipeline, _, _ = eval_setup(args.config, test_mode="inference")
manager = pipeline.datamanager
cache = manager.cached_train  # Also undistorts and updates intrinsics.
cameras = manager.train_dataset.cameras
indices = np.linspace(0, len(cache)-1, 16, dtype=int)
views = []
sheet = Image.new("RGB", (1280, 4*205), "white")
draw = ImageDraw.Draw(sheet)
for j, i in enumerate(indices):
    pixels = cache[i]["image"].cpu().numpy()
    if pixels.dtype != np.uint8:
        pixels = (pixels*255).clip(0,255).astype(np.uint8)
    hsv = cv2.cvtColor(pixels, cv2.COLOR_RGB2HSV)
    red = (((hsv[:,:,0] < 12) | (hsv[:,:,0] > 170)) & (hsv[:,:,1]>95) & (hsv[:,:,2]>45)).astype(np.uint8)*255
    red = cv2.morphologyEx(red, cv2.MORPH_CLOSE, np.ones((7,7),np.uint8))
    contours,_ = cv2.findContours(red,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        continue
    contour = max(contours,key=cv2.contourArea)
    hull = cv2.convexHull(contour)
    if cv2.contourArea(hull) < pixels.shape[0]*pixels.shape[1]*.015:
        continue
    mask = np.zeros(pixels.shape[:2],np.uint8)
    cv2.fillConvexPoly(mask,hull,255)
    # Small margin preserves white top/edges and limits overly aggressive cuts.
    mask = cv2.dilate(mask,np.ones((11,11),np.uint8))
    camera = cameras[i:i+1]
    pose = camera.camera_to_worlds[0].cpu().numpy()
    intr = [float(getattr(camera,k).flatten()[0]) for k in ('fx','fy','cx','cy')]
    views.append((pose,intr,mask))
    overlay=pixels.copy();cv2.polylines(overlay,[hull],True,(0,255,0),3)
    im=Image.fromarray(overlay);im.thumbnail((320,180))
    x=j%4*320;y=j//4*205;sheet.paste(im,(x,y+20));draw.text((x+4,y+3),f'train view {i}',fill='black')
sheet.save(args.output/'silhouette_review.jpg')
if len(views)<8:
    raise RuntimeError('Too few usable silhouettes')

def select(xyz):
    seen=np.zeros(len(xyz),int); inside=np.zeros(len(xyz),int)
    for pose,intr,mask in views:
        q=(xyz-pose[:,3])@pose[:,:3]
        z=-q[:,2];fx,fy,cx,cy=intr
        safe=np.maximum(z,1e-6)
        u=np.rint(fx*q[:,0]/safe+cx).astype(np.int64)
        v=np.rint(-fy*q[:,1]/safe+cy).astype(np.int64)
        valid=(z>.01)&(u>=0)&(v>=0)&(u<mask.shape[1])&(v<mask.shape[0])
        seen+=valid
        loc=np.flatnonzero(valid);inside[loc]+=mask[v[loc],u[loc]]>0
    keep=(seen>=6)&(inside>=np.ceil(seen*.9))
    candidates=np.flatnonzero(keep)
    if len(candidates)<100:
        return keep
    positions=xyz[candidates]
    extent=np.linalg.norm(np.percentile(positions,99,axis=0)-np.percentile(positions,1,axis=0))
    labels=DBSCAN(eps=extent*.025,min_samples=12,n_jobs=4).fit_predict(positions)
    ids,counts=np.unique(labels[labels>=0],return_counts=True)
    if not len(ids):
        raise RuntimeError('No coherent product component')
    keep[candidates[labels!=ids[np.argmax(counts)]]]=False
    return keep

ply=PlyData.read(args.ply);vertex=ply['vertex'].data
keep=select(np.column_stack([vertex[n] for n in ('x','y','z')]))
if keep.sum()<100:
    raise RuntimeError('Crop rejected: insufficient remaining Gaussians')
PlyData([PlyElement.describe(vertex[keep],'vertex')],text=False).write(args.output/'dakdari_isolated.ply')
model=pipeline.model
selected=torch.as_tensor(select(model.means.detach().cpu().numpy()),device=model.device)
for name,param in list(model.gauss_params.items()):
    model.gauss_params[name]=torch.nn.Parameter(param[selected].detach(),requires_grad=False)
model.eval()
# Render retained Gaussians from known cameras for visual inspection.
for i in indices[::3]:
    with torch.no_grad():
        result=model.get_outputs_for_camera(cameras[i:i+1].to(model.device))
    rgb=(result['rgb'].cpu().numpy()*255).clip(0,255).astype(np.uint8)
    Image.fromarray(rgb).save(args.output/f'isolated_view_{i:03}.png')
report=dict(source=str(args.ply),source_count=len(vertex),kept=int(keep.sum()),removed=int((~keep).sum()),silhouettes=len(views),minimum_views=6,minimum_support=.9,method='red convex-hull multiview center filtering + largest DBSCAN component (eps=2.5% robust extent, min_samples=12); Gaussian extents are not clipped',limitations='Bottom is unobserved; not evidence of watertight geometry; inspect thin edges and residual splat tails.')
(args.output/'isolation.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
