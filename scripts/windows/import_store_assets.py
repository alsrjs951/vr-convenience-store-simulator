"""Converts the downloaded store assets (P3-K1, see Assets/ThirdParty/StoreAssets/README.md) into what the scene builder uses.

Usage: python -I scripts/windows/import_store_assets.py <download_dir> <project_dir>

<download_dir> holds polyhaven/<id>/, ambientcg/<id>/<id>_1K-JPG.zip and khronos/CommercialRefrigerator/ as fetched.
Writes Assets/ThirdParty/StoreAssets/{PolyHaven,AmbientCG,Khronos}/<id>/.

URP Lit wants metallic in R and smoothness in A of one map, and reads occlusion from G. So every material gets a
<name>_Mask.png: R = metallic, G = ambient occlusion, B = 0, A = smoothness (1 - roughness), and the builder uses it as
both the metallic map and the occlusion map. Normals stay OpenGL style (Unity's convention).
"""
import io, json, os, shutil, struct, sys, zipfile

import numpy as np
from PIL import Image

src, project = sys.argv[1], sys.argv[2]
out_root = os.path.join(project, 'Assets', 'ThirdParty', 'StoreAssets')
MASK_SIZE = 1024


def save_mask(path, metal=None, ao=None, rough=None, size=None):
    """Each input is a float array 0..1 or None (metal 0, ao 1, rough 0.5)."""
    shape = next(a.shape for a in (metal, ao, rough) if a is not None)
    m = np.zeros(shape, np.float32) if metal is None else metal
    o = np.ones(shape, np.float32) if ao is None else ao
    r = np.full(shape, 0.5, np.float32) if rough is None else rough
    rgba = np.stack([m, o, np.zeros(shape, np.float32), 1.0 - r], axis=-1)
    im = Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA')
    if size and im.size[0] > size:
        im = im.resize((size, size), Image.LANCZOS)
    im.save(path, optimize=True)


def gray(im):
    return np.asarray(im.convert('L'), np.float32) / 255.0


def channels(im):
    a = np.asarray(im.convert('RGB'), np.float32) / 255.0
    return a[..., 0], a[..., 1], a[..., 2]


# ---------- ambientCG surfaces (CC0) ----------
# Tiles043, PaintedPlaster017, OfficeCeiling004 and mounted_fluorescent_lights were used by the first pass only (replaced
# by make_store_textures.py on 2026-11-08).
for aid in ['Metal027']:
    zf = zipfile.ZipFile(os.path.join(src, 'ambientcg', aid, f'{aid}_1K-JPG.zip'))
    names = {n.split('_')[-1].split('.')[0]: n for n in zf.namelist() if n.endswith('.jpg')}
    out = os.path.join(out_root, 'AmbientCG', aid)
    os.makedirs(out, exist_ok=True)
    read = lambda key: Image.open(io.BytesIO(zf.read(names[key]))) if key in names else None
    for key in ['Color', 'NormalGL', 'Emission']:
        if key in names:
            with open(os.path.join(out, f'{aid}_{key}.jpg'), 'wb') as f:
                f.write(zf.read(names[key]))
    metal, ao, rough = read('Metalness'), read('AmbientOcclusion'), read('Roughness')
    save_mask(os.path.join(out, f'{aid}_Mask.png'),
              gray(metal) if metal else None, gray(ao) if ao else None, gray(rough) if rough else None)
    print('ambientcg', aid, sorted(os.listdir(out)))

# ---------- Poly Haven models (CC0) ----------
for pid in ['steel_frame_shelves_01', 'industrial_pastic_container', 'hand_truck']:
    d = os.path.join(src, 'polyhaven', pid)
    out = os.path.join(out_root, 'PolyHaven', pid)
    os.makedirs(out, exist_ok=True)
    shutil.copyfile(os.path.join(d, f'{pid}_2k.fbx'), os.path.join(out, f'{pid}.fbx'))
    for key, name in [('diff', 'Color'), ('nor_gl', 'NormalGL'), ('emission', 'Emission')]:
        p = os.path.join(d, f'{pid}_{key}_2k.jpg')
        if os.path.exists(p):
            shutil.copyfile(p, os.path.join(out, f'{pid}_{name}.jpg'))
    ao, rough, metal = channels(Image.open(os.path.join(d, f'{pid}_arm_2k.jpg')))  # ARM = AO, roughness, metallic
    save_mask(os.path.join(out, f'{pid}_Mask.png'), metal, ao, rough, MASK_SIZE)
    print('polyhaven', pid, sorted(os.listdir(out)))

# Cardboard: the box side with the handling marks, cut out of cardboard_box_01's atlas (which is rotated 45 degrees and
# upside down there). The stock boxes are built from cubes, so they take one flat face texture rather than the model.
d = os.path.join(src, 'polyhaven', 'cardboard_box_01')
out = os.path.join(out_root, 'PolyHaven', 'cardboard_box_01')
os.makedirs(out, exist_ok=True)
CENTER, CROP = (680, 640), (60, 160, 780, 395)


def side(path):
    im = Image.open(path).convert('RGB')
    im = im.rotate(-45, center=CENTER, resample=Image.BICUBIC).rotate(180, center=CENTER)
    x0, y0 = CENTER[0] - 400, CENTER[1] - 400
    return im.crop((x0 + CROP[0], y0 + CROP[1], x0 + CROP[2], y0 + CROP[3]))


side(os.path.join(d, 'cardboard_box_01_diff_2k.jpg')).save(os.path.join(out, 'cardboard_side_Color.jpg'), quality=92)
# Rotating a normal map turns its tangent-space vectors too: rotate X/Y by the same -45 + 180 degrees.
nor = np.asarray(side(os.path.join(d, 'cardboard_box_01_nor_gl_2k.jpg')), np.float32) / 255.0 * 2 - 1
angle = np.radians(-45 + 180)
x = nor[..., 0] * np.cos(angle) - nor[..., 1] * np.sin(angle)
y = nor[..., 0] * np.sin(angle) + nor[..., 1] * np.cos(angle)
nor = np.stack([x, y, nor[..., 2]], -1)
Image.fromarray(((nor * 0.5 + 0.5) * 255 + 0.5).clip(0, 255).astype(np.uint8)).save(os.path.join(out, 'cardboard_side_NormalGL.jpg'), quality=92)
ao, rough, metal = channels(side(os.path.join(d, 'cardboard_box_01_arm_2k.jpg')))
save_mask(os.path.join(out, 'cardboard_side_Mask.png'), metal, ao, rough)
print('polyhaven cardboard_box_01', sorted(os.listdir(out)))

# ---------- Khronos CommercialRefrigerator (CC-BY 4.0) -> OBJ ----------
# The champagne bottles are left out: 113k vertices of wine with a label, not convenience store stock.
glb = open(os.path.join(src, 'khronos', 'CommercialRefrigerator', 'CommercialRefrigerator.glb'), 'rb').read()
json_len, = struct.unpack('<I', glb[12:16])
gltf = json.loads(glb[20:20 + json_len])
bin_start = 20 + json_len + 8
binary = glb[bin_start:]
COMPONENTS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
DTYPES = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}


def accessor(i):
    a = gltf['accessors'][i]
    v = gltf['bufferViews'][a['bufferView']]
    n, comps = a['count'], COMPONENTS[a['type']]
    dtype = np.dtype(DTYPES[a['componentType']])
    offset = v.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = v.get('byteStride', 0) or dtype.itemsize * comps
    raw = np.frombuffer(binary, np.uint8, count=stride * (n - 1) + dtype.itemsize * comps, offset=offset)
    out = np.lib.stride_tricks.as_strided(raw, shape=(n, dtype.itemsize * comps), strides=(stride, 1)).copy()
    return out.view(dtype).reshape(n, comps) if comps > 1 else out.view(dtype).reshape(n)


def local_matrix(node):
    if 'matrix' in node:
        return np.array(node['matrix'], np.float64).reshape(4, 4).T
    t = np.eye(4)
    t[:3, 3] = node.get('translation', [0, 0, 0])
    x, y, z, w = node.get('rotation', [0, 0, 0, 1])
    r = np.eye(4)
    r[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                 [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                 [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    s = np.diag(list(node.get('scale', [1, 1, 1])) + [1])
    return t @ r @ s


out = os.path.join(out_root, 'Khronos', 'CommercialRefrigerator')
os.makedirs(out, exist_ok=True)
lines, materials_used = ['# Converted from CommercialRefrigerator.glb (KhronosGroup/glTF-Sample-Assets), CC BY 4.0. See README.md.',
                         'mtllib CommercialRefrigerator.mtl'], []
base = 1


def walk(index, parent):
    global base
    node = gltf['nodes'][index]
    world = parent @ local_matrix(node)
    if node.get('name') != 'ChampagneBottles':
        if 'mesh' in node:
            normal_matrix = np.linalg.inv(world[:3, :3]).T
            for prim in gltf['meshes'][node['mesh']]['primitives']:
                attrs = prim['attributes']
                pos = accessor(attrs['POSITION']).astype(np.float64)
                pos = (np.c_[pos, np.ones(len(pos))] @ world.T)[:, :3]
                nrm = accessor(attrs['NORMAL']).astype(np.float64) @ normal_matrix.T
                nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
                uv = accessor(attrs['TEXCOORD_0'])
                idx = accessor(prim['indices']).reshape(-1, 3)
                material = gltf['materials'][prim['material']]['name']
                materials_used.append(material)
                lines.append(f'o {node["name"]}')
                lines.append(f'usemtl {material}')
                lines.extend([f'v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}' for p in pos])
                lines.extend([f'vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}' for n in nrm])
                lines.extend([f'vt {t[0]:.6f} {1 - t[1]:.6f}' for t in uv])  # glTF UV origin is top left
                lines.extend(['f ' + ' '.join(f'{base + k}/{base + k}/{base + k}' for k in tri) for tri in idx])
                base += len(pos)
        for child in node.get('children', []):
            walk(child, world)


for root in gltf['scenes'][gltf.get('scene', 0)]['nodes']:
    walk(root, np.eye(4))
with open(os.path.join(out, 'CommercialRefrigerator.obj'), 'w', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
with open(os.path.join(out, 'CommercialRefrigerator.mtl'), 'w', newline='\n') as f:
    f.write(''.join(f'newmtl {m}\nKd 0.8 0.8 0.8\n\n' for m in dict.fromkeys(materials_used)))


def image(tex_index):
    img = gltf['images'][gltf['textures'][tex_index]['source']]
    v = gltf['bufferViews'][img['bufferView']]
    return Image.open(io.BytesIO(binary[v.get('byteOffset', 0):v.get('byteOffset', 0) + v['byteLength']]))


for m in gltf['materials']:
    name = m['name']
    if name not in materials_used:
        continue
    pbr = m.get('pbrMetallicRoughness', {})
    if 'baseColorTexture' in pbr:
        image(pbr['baseColorTexture']['index']).convert('RGB').save(os.path.join(out, f'{name}_Color.jpg'), quality=92)
    if 'emissiveTexture' in m:
        image(m['emissiveTexture']['index']).convert('RGB').save(os.path.join(out, f'{name}_Emission.jpg'), quality=92)
    if 'metallicRoughnessTexture' in pbr:
        _, rough, metal = channels(image(pbr['metallicRoughnessTexture']['index']))  # glTF: G roughness, B metallic
        rough = rough * pbr.get('roughnessFactor', 1.0)
        metal = metal * pbr.get('metallicFactor', 1.0)
        ao = None
        if 'occlusionTexture' in m:
            occ = image(m['occlusionTexture']['index'])
            ao = channels(occ)[0] if occ.size == (len(rough[0]), len(rough)) else None
        save_mask(os.path.join(out, f'{name}_Mask.png'), metal, ao, rough, MASK_SIZE)
shutil.copyfile(os.path.join(src, 'khronos', 'CommercialRefrigerator', 'README.md'), os.path.join(out, 'SOURCE_README.md'))
print('khronos', sorted(os.listdir(out)), 'vertices', base - 1)
