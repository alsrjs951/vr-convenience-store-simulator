"""Render an unchanged Gaussian PLY from six PCA-axis views for coverage review.

Axes are approximate review directions, not measured product coordinates.
Novel views have no ground truth and are not reconstruction accuracy tests.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import torch
from gsplat import rasterization
from PIL import Image, ImageDraw
from plyfile import PlyData

from fit_product_box import initial_axes


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('ply',type=Path)
    p.add_argument('output',type=Path)
    p.add_argument('--flat',action='store_true',help='Flat bag: the thinnest axis is up. See fit_product_box.initial_axes.')
    args=p.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    before=hashlib.sha256(args.ply.read_bytes()).hexdigest()
    data=PlyData.read(args.ply)['vertex'].data
    def array(names):
        return np.stack([data[n] for n in names],axis=-1)
    def gpu(x):
        return torch.tensor(x,dtype=torch.float32,device='cuda')
    xyz=array(['x','y','z'])
    assert all(np.isfinite(data[n]).all() for n in data.dtype.names)
    long,side,up=initial_axes(xyz.astype(np.float64),args.flat)
    basis=np.stack([long,side,up],axis=1)
    local=xyz@basis
    lower,upper=np.percentile(local,[.5,99.5],axis=0)
    center=((lower+upper)/2)@basis.T
    extent=upper-lower
    distance=float(np.linalg.norm(extent)*1.8)
    dc=array([f'f_dc_{i}' for i in range(3)])[:,None,:]
    rest_names=sorted([n for n in data.dtype.names if n.startswith('f_rest_')],key=lambda n:int(n[7:]))
    rest=array(rest_names).reshape(len(data),3,-1).transpose(0,2,1)
    colors=gpu(np.concatenate([dc,rest],axis=1))
    means=gpu(xyz);quats=gpu(array([f'rot_{i}' for i in range(4)]))
    scales=gpu(np.exp(array([f'scale_{i}' for i in range(3)])))
    opacity=torch.sigmoid(gpu(data['opacity'].copy()))
    sheet=Image.new('RGB',(1200,620),'white');draw=ImageDraw.Draw(sheet)
    views=[('side_a',side,up),('side_b',-side,up),('end_a',long,up),
           ('end_b',-long,up),('top',up,side),('underside',-up,side)]
    poses={}
    for i,(name,direction,view_up) in enumerate(views):
        eye=center+direction*distance
        forward=(center-eye)/np.linalg.norm(center-eye)
        right=np.cross(forward,view_up);right/=np.linalg.norm(right)
        down=np.cross(forward,right)
        w2c=np.eye(4);w2c[:3,:3]=np.stack([right,down,forward]);w2c[:3,3]=-w2c[:3,:3]@eye
        K=np.array([[700,0,300],[0,700,200],[0,0,1]])
        with torch.no_grad():
            rendered,alpha,_=rasterization(means,quats,scales,opacity,colors,gpu(w2c[None]),gpu(K[None]),600,400,
                sh_degree=3,backgrounds=gpu([[.16,.18,.22]]))
        pixels=(rendered[0].cpu().numpy().clip(0,1)*255).astype(np.uint8)
        im=Image.fromarray(pixels);im.save(args.output/(name+'.png'))
        x=i%3*400;y=i//3*310
        im.thumbnail((400,400));sheet.paste(im,(x,y+45))
        draw.text((x+10,y+15),f'{name} (novel view; no ground truth)',fill='black')
        poses[name]={'world_to_camera':w2c.tolist(),'intrinsics':K.tolist()}
    sheet.save(args.output/'six_view_review.jpg')
    unchanged=before==hashlib.sha256(args.ply.read_bytes()).hexdigest()
    assert unchanged
    report={'source':str(args.ply),'sha256':before,'source_unchanged':unchanged,'count':len(data),
            'all_attributes_finite':True,'center':center.tolist(),'approximate_basis':basis.tolist(),
            'robust_extents_unscaled':extent.tolist(),'views':poses,
            'limitations':'PCA axes and percentile extents are for visual review only; no measured size; unseen surfaces are not validated.'}
    (args.output/'review.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:report[k] for k in ['count','source_unchanged','all_attributes_finite','robust_extents_unscaled']}))


if __name__=='__main__':
    main()
