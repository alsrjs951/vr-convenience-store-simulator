"""Audit whether the existing opening-face photograph registers to a video model.

Uses isolated database copies. A COLMAP success is not an acceptance decision:
repeated package logos and the stationary background can produce false matches.
"""
import argparse
import hashlib
import json
import shutil
import sqlite3
import subprocess
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageOps
from nerfstudio.data.utils.colmap_parsing_utils import read_images_binary


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(executable, output, label, *arguments):
    command = [str(executable), *map(str, arguments)]
    with (output / (label + '.log')).open('w', encoding='utf-8') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'{label} failed; see {output}')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('session', type=Path)
    p.add_argument('photo', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--colmap', type=Path, required=True)
    p.add_argument('--modes', nargs='+', choices=['full_photo','product_mask','unique_detail'], default=['full_photo','product_mask'])
    args = p.parse_args()
    if args.output.exists():
        raise FileExistsError('Use a new output directory to preserve experiment evidence')
    args.output.mkdir(parents=True)
    originals = {str(x): sha(x) for x in [args.photo, args.session/'database.db', args.session/'sparse/0/images.bin']}
    images = args.output/'images'
    shutil.copytree(args.session/'frames', images)
    photo = ImageOps.exif_transpose(Image.open(args.photo)).convert('RGB')
    photo.save(images/'opening_photo.png')
    rgb = np.array(photo)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    red = (((hsv[:,:,0]<12)|(hsv[:,:,0]>170)) & (hsv[:,:,1]>95) & (hsv[:,:,2]>45)).astype(np.uint8)*255
    contours,_ = cv2.findContours(red, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    hull = cv2.convexHull(max(contours,key=cv2.contourArea))
    mask = np.zeros(rgb.shape[:2],np.uint8)
    cv2.fillConvexPoly(mask,hull,255)
    # Erode the silhouette slightly to exclude bedding at the package boundary.
    mask = cv2.erode(mask,np.ones((9,9),np.uint8))
    masks = args.output/'masks'; masks.mkdir()
    Image.fromarray(mask).save(masks/'opening_photo.png.png')
    unique_masks=args.output/'unique_masks';unique_masks.mkdir()
    unique=mask.copy()
    # Manual diagnostic ROI: the large snack illustration left of the repeated
    # logo. This deliberately tests a non-logo detail, not the whole photograph.
    unique[:,int(photo.width*.37):]=0
    Image.fromarray(unique).save(unique_masks/'opening_photo.png.png')
    (args.output/'photo_list.txt').write_text('opening_photo.png\n')
    names = sorted(x.name for x in images.glob('frame_*.jpg'))
    (args.output/'pairs.txt').write_text(''.join(f'opening_photo.png {x}\n' for x in names))
    report = {'source_photo':str(args.photo),'session':str(args.session),'source_hashes':originals,'runs':[]}
    for mode in args.modes:
        folder = args.output/mode;folder.mkdir()
        db = folder/'database.db';shutil.copy2(args.session/'database.db',db)
        extra = [] if mode=='full_photo' else ['--ImageReader.mask_path',unique_masks if mode=='unique_detail' else masks]
        run(args.colmap,folder,'features','feature_extractor','--database_path',db,
            '--image_path',images,'--image_list_path',args.output/'photo_list.txt',
            '--ImageReader.camera_model','SIMPLE_RADIAL','--ImageReader.single_camera','0',
            '--FeatureExtraction.max_image_size','1600',*extra)
        run(args.colmap,folder,'matches','matches_importer','--database_path',db,
            '--match_list_path',args.output/'pairs.txt','--match_type','pairs')
        model = folder/'registered';model.mkdir()
        run(args.colmap,folder,'registration','image_registrator','--database_path',db,
            '--input_path',args.session/'sparse/0','--output_path',model)
        connection = sqlite3.connect(db)
        image_names = dict(connection.execute('SELECT image_id,name FROM images'))
        photo_id = next(k for k,v in image_names.items() if v=='opening_photo.png')
        pairs = []
        for pair_id, rows, cols, data in connection.execute('SELECT pair_id,rows,cols,data FROM two_view_geometries'):
            first,second = divmod(pair_id,2147483647)
            if photo_id in (first,second) and rows:
                pairs.append({'other':image_names[second if first==photo_id else first],'verified_matches':rows,
                              'first':first,'second':second,'indices':np.frombuffer(data,np.uint32).reshape(rows,cols)})
        pairs.sort(key=lambda x:x['verified_matches'],reverse=True)
        sheet=Image.new('RGB',(1200,400*min(4,len(pairs)) or 100),'white')
        draw=ImageDraw.Draw(sheet)
        for i,pair in enumerate(pairs[:4]):
            other=Image.open(images/pair['other']).convert('RGB')
            for im,x in [(photo,0),(other,600)]:
                thumb=im.copy();thumb.thumbnail((600,350));sheet.paste(thumb,(x,i*400+40))
            points=[]
            for image_id,column,im in [(photo_id,0 if pair['first']==photo_id else 1,photo),
                                        (pair['second'] if pair['first']==photo_id else pair['first'],1 if pair['first']==photo_id else 0,other)]:
                rows,cols,data=connection.execute('SELECT rows,cols,data FROM keypoints WHERE image_id=?',(image_id,)).fetchone()
                kp=np.frombuffer(data,np.float32).reshape(rows,cols)
                points.append(kp[pair['indices'][:,column],:2]*min(600/im.width,350/im.height))
            draw.text((10,i*400+10),f"{mode}: {pair['other']} / {pair['verified_matches']} verified matches",fill='black')
            for a,b in zip(*points):
                draw.line((a[0],a[1]+i*400+40,b[0]+600,b[1]+i*400+40),fill=(0,190,80),width=1)
        sheet.save(folder/'match_review.jpg')
        registered=read_images_binary(model/'images.bin')
        added=[im for im in registered.values() if im.name=='opening_photo.png']
        entry={'mode':mode,'registered_images':len(registered),'photo_registered':bool(added),
               'photo_keypoints':connection.execute('SELECT rows FROM keypoints WHERE image_id=?',(photo_id,)).fetchone()[0],
               'verified_pairs':[{'other':x['other'],'verified_matches':x['verified_matches']} for x in pairs]}
        if added:
            im=added[0]
            entry.update(photo_camera_id=int(im.camera_id),photo_qvec=im.qvec.tolist(),photo_tvec=im.tvec.tolist(),
                         photo_3d_correspondences=int((im.point3D_ids>=0).sum()))
        connection.close();report['runs'].append(entry)
        print(json.dumps(entry),flush=True)
    report['originals_unchanged']=all(sha(Path(path))==digest for path,digest in originals.items())
    assert report['originals_unchanged']
    (args.output/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')


if __name__=='__main__':
    main()
