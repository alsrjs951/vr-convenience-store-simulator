"""Compare two render sets on identical held-out frames and fixed product ROIs."""
import argparse
import json
from pathlib import Path
import cv2
import numpy as np
from PIL import Image, ImageDraw

p=argparse.ArgumentParser()
p.add_argument('baseline',type=Path)
p.add_argument('candidate',type=Path)
p.add_argument('output',type=Path)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
files=sorted((a.baseline/'test/gt-rgb').glob('*.jpg'))
rows=[];panels=[]
for f in files:
    gt=np.array(Image.open(f).convert('RGB'))
    gt_new=np.array(Image.open(a.candidate/'test/gt-rgb'/f.name).convert('RGB'))
    if not np.array_equal(gt,gt_new):
        raise ValueError(f'Ground truth differs: {f.name}')
    old=np.array(Image.open(a.baseline/'test/rgb'/f.name).convert('RGB'))
    new=np.array(Image.open(a.candidate/'test/rgb'/f.name).convert('RGB'))
    hsv=cv2.cvtColor(gt,cv2.COLOR_RGB2HSV)
    red=(((hsv[:,:,0]<12)|(hsv[:,:,0]>170))&(hsv[:,:,1]>95)&(hsv[:,:,2]>45)).astype(np.uint8)*255
    red=cv2.morphologyEx(red,cv2.MORPH_CLOSE,np.ones((7,7),np.uint8))
    contours,_=cv2.findContours(red,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
    hull=cv2.convexHull(max(contours,key=cv2.contourArea))
    mask=np.zeros(gt.shape[:2],np.uint8);cv2.fillConvexPoly(mask,hull,255)
    def psnr(im):
        mse=np.mean((im.astype(float)[mask>0]-gt.astype(float)[mask>0])**2)
        return float(10*np.log10(255**2/max(mse,1e-10)))
    rows.append(dict(frame=f.name,roi_pixels=int((mask>0).sum()),baseline_psnr=psnr(old),candidate_psnr=psnr(new)))
    x,y,w,h=cv2.boundingRect(hull);x=max(0,x-8);y=max(0,y-8);w=min(gt.shape[1]-x,w+16);h=min(gt.shape[0]-y,h+16)
    panels.append([Image.fromarray(im[y:y+h,x:x+w]) for im in (gt,old,new)])
report=dict(identical_ground_truth=True,frames=rows,baseline_mean_object_roi_psnr=float(np.mean([r['baseline_psnr'] for r in rows])),candidate_mean_object_roi_psnr=float(np.mean([r['candidate_psnr'] for r in rows])),method='Mean per-frame PSNR inside a fixed red-box convex-hull mask derived only from ground truth. Not a barcode decoding or geometry completeness test.')
(a.output/'comparison.json').write_text(json.dumps(report,indent=2))
sheet=Image.new('RGB',(1200,len(files)*320),'white');draw=ImageDraw.Draw(sheet)
for j,images in enumerate(panels):
    for k,im in enumerate(images):
        im.thumbnail((390,285));sheet.paste(im,(k*400,j*320+30));draw.text((k*400+4,j*320+5),f'{files[j].stem}: '+('source','3000 steps','10000 steps')[k],fill='black')
sheet.save(a.output/'same_view_comparison.jpg')
print(json.dumps({k:v for k,v in report.items() if k!='frames'},indent=2))
