# Prepares Poly Haven CC0 assets for the Kino Content SDK, which has no
# glTF importer: every glTF node with a mesh becomes an OBJ (its own local geometry, one group per material), the
# textures become what HDRP/Lit reads (base colour with the plants' alpha cut in, OpenGL normal maps as they are, and a
# mask map: R metal, G AO, B detail, A smoothness from Poly Haven's ARM or AO + roughness), and nfx_assets.json lists
# it all for the SDK's map scripts.
#
#   python tools/kino_sdk/polyhaven_import.py <polyhaven download folder> <SDK>/Assets/Content/NfxAssets
import json, os, shutil, struct, sys
import numpy as np
from PIL import Image

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
manifest = {'models': {}, 'textures': {}}

def mask_from_arm(arm_path, out_path):
    arm = np.asarray(Image.open(arm_path).convert('RGB')).astype(np.uint8)
    out = np.zeros(arm.shape[:2] + (4,), np.uint8)
    out[..., 0] = arm[..., 2]          # metal
    out[..., 1] = arm[..., 0]          # AO
    out[..., 2] = 0
    out[..., 3] = 255 - arm[..., 1]    # smoothness = 1 - roughness
    Image.fromarray(out, 'RGBA').save(out_path)

def mask_from(ao_path, rough_path, out_path):
    rough = np.asarray(Image.open(rough_path).convert('L'))
    ao = np.asarray(Image.open(ao_path).convert('L')) if ao_path else np.full_like(rough, 255)
    out = np.zeros(rough.shape + (4,), np.uint8)
    out[..., 1] = ao
    out[..., 3] = 255 - rough
    Image.fromarray(out, 'RGBA').save(out_path)

# --- tiling textures
tex_root = os.path.join(src, 'textures')
for name in sorted(os.listdir(tex_root)):
    folder = os.path.join(tex_root, name)
    out = os.path.join(dst, 'textures', name)
    os.makedirs(out, exist_ok=True)
    files = os.listdir(folder)
    pick = lambda key: next((os.path.join(folder, f) for f in files if key in f), None)
    entry = {}
    for key, tag in [('_diff', 'base'), ('_diffuse', 'base'), ('_nor_gl', 'normal'), ('_disp', 'height')]:
        p = pick(key)
        if p and tag not in entry:
            target = os.path.join(out, os.path.basename(p))
            shutil.copyfile(p, target)
            entry[tag] = os.path.relpath(target, dst).replace('\\', '/')
    mask = os.path.join(out, f'{name}_mask.png')
    mask_from(pick('_ao_'), pick('_rough_'), mask)
    entry['mask'] = os.path.relpath(mask, dst).replace('\\', '/')
    manifest['textures'][name] = entry
    print('texture', name)

# --- models
COMPONENTS = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
WIDTH = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}

def accessor(g, buffers, index):
    a = g['accessors'][index]
    view = g['bufferViews'][a['bufferView']]
    data = buffers[view['buffer']]
    dtype = np.dtype(COMPONENTS[a['componentType']])
    width = WIDTH[a['type']]
    offset = view.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = view.get('byteStride', 0) or dtype.itemsize * width
    count = a['count']
    raw = np.frombuffer(data, np.uint8, count=stride * (count - 1) + dtype.itemsize * width, offset=offset)
    rows = np.lib.stride_tricks.as_strided(raw, shape=(count, dtype.itemsize * width), strides=(stride, 1))
    return np.ascontiguousarray(rows).view(dtype).reshape(count, width)

model_root = os.path.join(src, 'models')
for asset in sorted(os.listdir(model_root)):
    folder = os.path.join(model_root, asset)
    gltf_path = next(os.path.join(folder, f) for f in os.listdir(folder) if f.endswith('.gltf'))
    g = json.load(open(gltf_path, encoding='utf-8'))
    buffers = [open(os.path.join(folder, b['uri']), 'rb').read() for b in g['buffers']]
    out = os.path.join(dst, 'models', asset)
    os.makedirs(out, exist_ok=True)

    # materials: textures for HDRP/Lit
    materials = []
    for mi, m in enumerate(g.get('materials', [])):
        pbr = m.get('pbrMetallicRoughness', {})
        uri = lambda t: g['images'][g['textures'][t['index']]['source']]['uri'] if t else None
        base_uri = uri(pbr.get('baseColorTexture'))
        normal_uri = uri(m.get('normalTexture'))
        arm_uri = uri(pbr.get('metallicRoughnessTexture'))
        entry = {'name': m.get('name', f'mat{mi}'), 'cutoff': m.get('alphaCutoff', 0.5) if m.get('alphaMode') == 'MASK' else None,
                 'transparent': m.get('alphaMode') == 'BLEND', 'doubleSided': m.get('doubleSided', False),
                 'color': pbr.get('baseColorFactor', [1, 1, 1, 1])}
        if base_uri:
            base_src = os.path.join(folder, base_uri)
            # the part's own alpha (pine_sapling_small_twig_diff -> _twig_alpha), else the asset's one (fern_02_alpha)
            alpha_src = os.path.join(folder, os.path.dirname(base_uri), os.path.basename(base_uri).replace('_diff_', '_alpha_').rsplit('.', 1)[0] + '.png')
            if not os.path.exists(alpha_src):
                alpha_src = os.path.join(folder, 'textures', f'{asset}_alpha_2k.png')
            elif entry['cutoff'] is None:
                # fir_sapling's twigs come as OPAQUE in the glTF, the chain-link fence's wire as BLEND, with the alpha
                # beside them: cut out (a blended wire would cast no traced shadow and sort badly)
                entry['cutoff'] = 0.5
                entry['transparent'] = False
            target = os.path.join(out, os.path.splitext(os.path.basename(base_uri))[0] + '.png')
            if entry['cutoff'] is not None and os.path.exists(alpha_src):
                rgb = Image.open(base_src).convert('RGB')
                a = Image.open(alpha_src).convert('L').resize(rgb.size)
                # the colour under the cut-out parts is black in the scans: the nearest leaf's colour instead, so the
                # mip maps do not draw dark rims round the leaves
                av = np.asarray(a)
                solid = av > 127
                if solid.any() and not solid.all():
                    from scipy import ndimage
                    _, (iy, ix) = ndimage.distance_transform_edt(~solid, return_indices=True)
                    rgb = Image.fromarray(np.asarray(rgb)[iy, ix])
                rgb.putalpha(a)
                rgb.save(target)
            else:
                Image.open(base_src).convert('RGB').save(target)
            entry['base'] = os.path.relpath(target, dst).replace('\\', '/')
        if normal_uri:
            target = os.path.join(out, os.path.basename(normal_uri))
            shutil.copyfile(os.path.join(folder, normal_uri), target)
            entry['normal'] = os.path.relpath(target, dst).replace('\\', '/')
        if arm_uri:
            p = os.path.join(folder, arm_uri)
            target = os.path.join(out, os.path.splitext(os.path.basename(arm_uri))[0] + '_mask.png')
            if '_arm_' in arm_uri:
                mask_from_arm(p, target)
            else:
                mask_from(None, p, target)
            entry['mask'] = os.path.relpath(target, dst).replace('\\', '/')
        materials.append(entry)

    pieces = {}
    for node in g['nodes']:
        if 'mesh' not in node:
            continue
        mesh = g['meshes'][node['mesh']]
        name = node.get('name', f'node{len(pieces)}').replace(' ', '_')
        mats = []
        objs = []
        # one OBJ per material: Unity's OBJ importer (materials not imported) merges every usemtl into one submesh,
        # which drew the saplings' twigs and the lamp's glass with the first material
        for k, prim in enumerate(mesh['primitives']):
            lines = [f'# {asset} / {name} / part {k}, Poly Haven CC0']
            base = 1
            at = prim['attributes']
            pos = accessor(g, buffers, at['POSITION'])
            nor = accessor(g, buffers, at['NORMAL']) if 'NORMAL' in at else None
            uv = accessor(g, buffers, at['TEXCOORD_0']) if 'TEXCOORD_0' in at else None
            idx = accessor(g, buffers, prim['indices']).reshape(-1) if 'indices' in prim else np.arange(len(pos))
            # the node's scale and rotation (Poly Haven sets some), not its position: the map script places each piece
            s = np.array(node.get('scale', [1, 1, 1]), float)
            x, y, z, w = node.get('rotation', [0, 0, 0, 1])
            R = np.array([[1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w)],
                          [2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w)],
                          [2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y)]])
            pos = (pos * s) @ R.T
            lines += [f'v {p[0]:.5f} {p[1]:.5f} {p[2]:.5f}' for p in pos]
            if nor is not None:
                nor = (nor / s) @ R.T
                nor /= np.maximum(np.linalg.norm(nor, axis=1, keepdims=True), 1e-8)
                lines += [f'vn {n[0]:.4f} {n[1]:.4f} {n[2]:.4f}' for n in nor]
            if uv is not None:
                lines += [f'vt {t[0]:.5f} {1.0 - t[1]:.5f}' for t in uv]
            mi = prim.get('material', 0)
            mat_name = materials[mi]['name'] if mi < len(materials) else 'default'
            mats.append(mat_name)
            # no 'g' line: Unity would make a separate object of every group
            lines.append(f'usemtl {mat_name}')
            tri = idx.reshape(-1, 3) + base
            if nor is not None and uv is not None:
                lines += [f'f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}' for a, b, c in tri]
            elif uv is not None:
                lines += [f'f {a}/{a} {b}/{b} {c}/{c}' for a, b, c in tri]
            else:
                lines += [f'f {a}//{a} {b}//{b} {c}//{c}' for a, b, c in tri]
            suffix = '' if len(mesh['primitives']) == 1 else f'__{k}'
            path = os.path.join(out, f'{asset}__{name}{suffix}.obj')
            open(path, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
            objs.append(os.path.relpath(path, dst).replace('\\', '/'))
        pieces[name] = {'obj': '|'.join(objs), 'materials': mats}
    manifest['models'][asset] = {'materials': {m['name']: m for m in materials}, 'pieces': pieces}
    print('model', asset, len(pieces), 'pieces')

json.dump(manifest, open(os.path.join(dst, 'nfx_assets.json'), 'w', encoding='utf-8'), indent=1)
# the same as lines for the editor script (no JSON reader there):
#   tex <name> base=<path> normal=<path> mask=<path> height=<path>
#   mat <asset> <material> base=.. normal=.. mask=.. cutoff=<v or -> double=<0|1> transparent=<0|1>
#   piece <asset> <piece> <obj path|obj path|...> <material,material,...>  (one OBJ per material)
lines = []
for name, e in manifest['textures'].items():
    lines.append(' '.join(['tex', name] + [f'{k}={v}' for k, v in e.items()]))
for asset, m in manifest['models'].items():
    for mat in m['materials'].values():
        parts = ['mat', asset, mat['name']] + [f'{k}={mat[k]}' for k in ('base', 'normal', 'mask') if k in mat]
        parts += [f"cutoff={mat['cutoff'] if mat['cutoff'] is not None else '-'}", f"double={int(mat['doubleSided'])}", f"transparent={int(mat['transparent'])}"]
        lines.append(' '.join(parts))
    for piece, e in m['pieces'].items():
        lines.append(' '.join(['piece', asset, piece, e['obj'], ','.join(e['materials'])]))
open(os.path.join(dst, 'nfx_assets.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
open(os.path.join(dst, 'LICENSE.txt'), 'w', encoding='utf-8').write(
    'Textures and models from Poly Haven (https://polyhaven.com), CC0 1.0 Universal (public domain).\n')
print('done')
