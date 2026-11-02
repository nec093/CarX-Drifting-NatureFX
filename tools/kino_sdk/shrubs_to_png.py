# Turns the baked shrub levels (bake_shrub_lods.py) into the textures HDRP/Lit reads, as cards_to_png.py does for
# the cards: <name>_<impostor|cloud>_diff.png (sRGB albedo with the scan's AO, colour bled past the edges, alpha in A),
# _nor.png (the tile's camera axes = the card's tangent space, OpenGL) and _mask.png (G: AO, A: smoothness).
#
#   python tools/kino_sdk/shrubs_to_png.py <bake folder> <SDK>/Assets/Content/NfxAssets/shrubs
import json, os, shutil, sys
import numpy as np
from PIL import Image
from scipy import ndimage

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
meta = json.load(open(os.path.join(src, 'shrubs.json')))


def srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


for name in meta:
    for level in ('impostor', 'cloud'):
        d = np.load(os.path.join(src, f'{name}_{level}.npz'))
        # the atlases' rows run bottom to top
        alpha = np.flipud(d['alpha'][..., 0])
        alb = np.flipud(d['albedo'])
        nrm = np.flipud(d['normal'])
        ao = np.flipud(d['ao'][..., 0])
        a = np.clip(alpha, 0, 1)
        cov = np.maximum(a, 1e-4)[..., None]
        alb = alb / cov
        nrm = nrm / cov
        solid = a > 0.5
        _, (iy, ix) = ndimage.distance_transform_edt(~solid, return_indices=True)
        alb, nrm, ao = alb[iy, ix], nrm[iy, ix], ao[iy, ix]
        shade = 0.45 + 0.55 * np.clip(ao, 0, 1)
        rgb = srgb(alb * shade[..., None])
        t = nrm.copy()
        t[..., 2] = np.abs(t[..., 2])         # leaves seen from behind: lit like the front
        t /= np.maximum(np.linalg.norm(t, axis=-1, keepdims=True), 1e-6)
        t = t * 0.75 + np.array([0, 0, 1.0]) * 0.25
        t /= np.linalg.norm(t, axis=-1, keepdims=True)
        Image.fromarray((np.dstack([rgb, a]) * 255).round().astype(np.uint8), 'RGBA').save(os.path.join(dst, f'{name}_{level}_diff.png'))
        Image.fromarray(((t * 0.5 + 0.5) * 255).round().astype(np.uint8), 'RGB').save(os.path.join(dst, f'{name}_{level}_nor.png'))
        mask = np.zeros(a.shape + (4,), np.uint8)
        mask[..., 1] = (np.clip(ao, 0, 1) * 255).astype(np.uint8)
        mask[..., 3] = 70
        Image.fromarray(mask, 'RGBA').save(os.path.join(dst, f'{name}_{level}_mask.png'))
        print(name, level, 'coverage', round(float(solid.mean()), 3))
shutil.copyfile(os.path.join(src, 'shrubs.json'), os.path.join(dst, 'shrubs.json'))
# the same as a list for Unity's JsonUtility (NfxShrubs.cs): corners flattened
listed = {'shrubs': [dict(name=k, asset=v['asset'], node=v['node'], frames=v['frames'], size_m=v['size_m'], centre_y=v['centre_y'], height_m=v['height_m'],
                          cards=[dict(corners=sum(c['corners'], []), uv=c['uv'], normal=c['normal'], tangent=c['tangent'])
                                 for c in v['cards']])
                     for k, v in meta.items()]}
json.dump(listed, open(os.path.join(dst, 'shrubs_unity.json'), 'w'))
