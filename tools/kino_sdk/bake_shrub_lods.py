# Bakes the far levels of the thicket shrubs (NatureFX ShrubRenderer, docs/map-authoring.md 5.6) from Poly Haven's
# scans, in Cycles:
#   impostor  the plant from N x N directions over the upper half of a sphere (hemi-octahedral, NFX_Impostor.hlsl),
#             one tile each, orthographic, into one atlas
#   cloud     a billboard cloud: the plant's triangles grouped into K flat slabs (k-means on place and facing), each
#             slab rendered alone, straight on, into its own tile of a second atlas; the cards' corners go to the json
# Normals are written in each tile's camera axes (x right, y up, z towards the camera), which the shader and the cards'
# tangents use. Positions and axes are in Unity's object space of the prototype: the plant centred on x, z, standing
# on y = 0 (polyhaven_import.py writes the OBJ the SDK places: Unity's OBJ importer turns glTF x into -x).
# tools/kino_sdk/shrubs_to_png.py then makes the PNGs.
#
#   blender -b --factory-startup --python tools/kino_sdk/bake_shrub_lods.py -- <polyhaven folder> <out folder>
import bpy, bmesh, json, os, sys
import numpy as np
import mathutils

src, out = sys.argv[sys.argv.index('--') + 1:][:2]
os.makedirs(out, exist_ok=True)
FRAMES = 8          # impostor frames per side
TILE = 256          # impostor tile (px): a 2048 atlas
ATLAS = 2048        # cloud atlas
CARDS = 32          # billboard cloud cards

# (prototype name, asset, node)
SHRUBS = [
    ('shrub01', 'shrub_01', 'shrub_01_a'),
    ('shrub02a', 'shrub_02', 'shrub_02_a'),
    ('shrub02c', 'shrub_02', 'shrub_02_c'),
    ('shrub03a', 'shrub_03', 'shrub_03_a'),
    ('shrub04', 'shrub_04', 'shrub_04'),
    ('fern02', 'fern_02', 'fern_02_b'),
]


# Unity <-> Blender (glTF import: Blender = (xg, -zg, yg); Unity = (-xg, yg, zg))
def u2b(v):
    return np.array([-v[0], -v[2], v[1]])


def b2u(v):
    return np.array([-v[0], v[2], -v[1]])


def frame_axes(d):
    # NFX_Impostor.hlsl NFX_ImpostorAxes (Unity space)
    ref = np.array([0.0, 0.0, 1.0]) if abs(d[1]) > 0.999 else np.array([0.0, 1.0, 0.0])
    r = np.cross(d, ref)
    r /= np.linalg.norm(r)
    return r, np.cross(r, d)


def hemi_oct_decode(e):
    d = np.array([(e[0] + e[1]) * 0.5, 0.0, (e[0] - e[1]) * 0.5])
    d[1] = 1.0 - abs(d[0]) - abs(d[2])
    return d / np.linalg.norm(d)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 24
    sc.cycles.device = 'CPU'
    sc.cycles.use_denoising = False
    sc.render.film_transparent = True
    sc.view_settings.view_transform = 'Standard'
    vl = sc.view_layers[0]
    vl.use_pass_diffuse_color = True
    vl.use_pass_normal = True
    vl.use_pass_ambient_occlusion = True
    sc.world = bpy.data.worlds.new('w')
    sc.world.light_settings.distance = 0.25
    return sc


def add_alpha(asset):
    # Poly Haven ships the plants' alpha beside the glTF
    tex = os.path.join(src, 'models', asset, 'textures')
    for m in bpy.data.materials:
        if not m.use_nodes:
            continue
        nodes, links = m.node_tree.nodes, m.node_tree.links
        bsdf = next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
        img = next((n for n in nodes if n.type == 'TEX_IMAGE' and n.image and '_diff_' in n.image.name), None)
        if bsdf is None or img is None:
            continue
        stem = img.image.name.split('.')[0].replace('_diff_', '_alpha_')
        cand = [os.path.join(tex, stem + '.png'), os.path.join(tex, f'{asset}_alpha_2k.png')]
        path = next((p for p in cand if os.path.exists(p)), None)
        if path is None:
            continue
        a = nodes.new('ShaderNodeTexImage')
        a.image = bpy.data.images.load(path)
        a.image.colorspace_settings.name = 'Non-Color'
        uv = img.inputs['Vector'].links[0].from_socket if img.inputs['Vector'].links else None
        if uv is not None:
            links.new(uv, a.inputs['Vector'])
        links.new(a.outputs['Color'], bsdf.inputs['Alpha'])


def setup_output(sc, tmp):
    nt = bpy.data.node_groups.new('comp', 'CompositorNodeTree')
    sc.compositing_node_group = nt
    rl = nt.nodes.new('CompositorNodeRLayers')
    fo = nt.nodes.new('CompositorNodeOutputFile')
    fo.directory = tmp
    fo.file_name = ''
    if hasattr(fo.format, 'media_type'):
        fo.format.media_type = 'IMAGE'
    fo.format.file_format = 'OPEN_EXR'
    fo.format.color_depth = '32'
    passes = {'DiffCol': 'Diffuse Color', 'Normal': 'Normal', 'AO': 'Ambient Occlusion', 'Alpha': 'Alpha'}
    for sock, out_name in passes.items():
        fo.file_output_items.new('RGBA', sock)
        nt.links.new(rl.outputs[out_name], fo.inputs[sock])
    return list(passes)


def render(sc, cam, right_b, up_b, dir_b, centre_b, scale, w, h, tmp, passes):
    # an orthographic view along -dir_b centred on centre_b, scale m across the larger side, w x h pixels;
    # returns albedo, camera space normal, AO, alpha (rows bottom to top)
    cam.data.ortho_scale = scale
    rot = mathutils.Matrix((right_b, up_b, dir_b)).transposed()
    cam.matrix_world = mathutils.Matrix.Translation(mathutils.Vector(centre_b + dir_b * 50.0)) @ rot.to_4x4()
    sc.render.resolution_x, sc.render.resolution_y = w, h
    for f in os.listdir(tmp) if os.path.exists(tmp) else []:
        os.remove(os.path.join(tmp, f))
    bpy.ops.render.render(write_still=False)
    res = {}
    files = [os.path.join(tmp, x) for x in os.listdir(tmp)]
    for sock in passes:
        f = next(p for p in files if os.path.basename(p).startswith(sock) or sock in os.path.basename(p))
        im = bpy.data.images.load(f)
        im.colorspace_settings.name = 'Non-Color'
        px = np.empty(w * h * 4, np.float32)
        im.pixels.foreach_get(px)
        bpy.data.images.remove(im)
        res[sock] = px.reshape(h, w, 4)
    nb = res['Normal'][..., :3]
    ncam = np.stack([nb @ right_b, nb @ up_b, nb @ dir_b], -1)
    return res['DiffCol'][..., :3], ncam, res['AO'][..., 0], res['Alpha'][..., 0]


def kmeans(feat, weight, k, iters=16, seed=1):
    rng = np.random.default_rng(seed)
    p = weight / weight.sum()
    centres = feat[rng.choice(len(feat), k, replace=False, p=p)]
    for _ in range(iters):
        d = ((feat[:, None, :] - centres[None, :, :]) ** 2).sum(-1)
        lab = d.argmin(1)
        for c in range(k):
            m = lab == c
            if m.any():
                centres[c] = (feat[m] * weight[m, None]).sum(0) / weight[m].sum()
            else:
                centres[c] = feat[rng.choice(len(feat), p=p)]
    return lab


meta_path = os.path.join(out, 'shrubs.json')
only = os.environ.get('NFX_SHRUBS_ONLY')
meta = json.load(open(meta_path)) if only and os.path.exists(meta_path) else {}
for name, asset, node in SHRUBS:
    if only and name not in only.split(','):
        continue
    sc = reset()
    folder = os.path.join(src, 'models', asset)
    gltf = next(os.path.join(folder, f) for f in os.listdir(folder) if f.endswith('.gltf'))
    bpy.ops.import_scene.gltf(filepath=gltf)
    add_alpha(asset)
    meshes = [o for o in sc.objects if o.type == 'MESH']
    keep = [o for o in meshes if o.name.startswith(node) or (o.parent and o.parent.name.startswith(node))]
    for o in meshes:
        if o not in keep:
            bpy.data.objects.remove(o, do_unlink=True)
    # one object, triangles, standing at the origin of Unity's frame
    bpy.ops.object.select_all(action='DESELECT')
    for o in keep:
        o.select_set(True)
    bpy.context.view_layer.objects.active = keep[0]
    if len(keep) > 1:
        bpy.ops.object.join()
    plant = bpy.context.view_layer.objects.active
    plant.parent = None
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bm = bmesh.new()
    bm.from_mesh(plant.data)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(plant.data)
    bm.free()
    vb = np.array([v.co[:] for v in plant.data.vertices])
    vu = np.array([b2u(v) for v in vb])
    lo, hi = vu.min(0), vu.max(0)
    shift_u = np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2])
    plant.data.transform(mathutils.Matrix.Translation(mathutils.Vector(-u2b(shift_u))))
    vu -= shift_u
    height = float(vu[:, 1].max())
    centre_u = np.array([0.0, height / 2, 0.0])
    radius = float(np.linalg.norm(vu - centre_u, axis=1).max())
    size = radius * 2.04

    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    cam.data.type = 'ORTHO'
    cam.data.clip_start = 1.0
    cam.data.clip_end = 100.0 + radius * 2
    sc.collection.objects.link(cam)
    sc.camera = cam
    tmp = os.path.join(out, '_tmp_' + name)
    os.makedirs(tmp, exist_ok=True)
    passes = setup_output(sc, tmp)
    sc.frame_set(1)

    # --- impostor
    n = FRAMES
    imp = {k: np.zeros((n * TILE, n * TILE, c), np.float32) for k, c in (('albedo', 3), ('normal', 3), ('ao', 1), ('alpha', 1))}
    for j in range(n):
        for i in range(n):
            d = hemi_oct_decode(((i + 0.5) / n * 2 - 1, (j + 0.5) / n * 2 - 1))
            r, up = frame_axes(d)
            alb, nrm, ao, alpha = render(sc, cam, u2b(r), u2b(up), u2b(d), u2b(centre_u), size, TILE, TILE, tmp, passes)
            ys, xs = slice(j * TILE, (j + 1) * TILE), slice(i * TILE, (i + 1) * TILE)
            imp['albedo'][ys, xs] = alb
            imp['normal'][ys, xs] = nrm
            imp['ao'][ys, xs, 0] = ao
            imp['alpha'][ys, xs, 0] = alpha
            print('impostor', name, i, j, flush=True)
    np.savez_compressed(os.path.join(out, name + '_impostor.npz'), **imp)

    # --- billboard cloud: triangles into slabs
    me = plant.data
    tri = np.array([p.vertices[:] for p in me.polygons])
    a, b, c = vu[tri[:, 0]], vu[tri[:, 1]], vu[tri[:, 2]]
    cen = (a + b + c) / 3
    nrm = np.cross(b - a, c - a)
    area = np.linalg.norm(nrm, axis=1) * 0.5 + 1e-9
    nrm /= (area * 2)[:, None]
    # facing without its sign (leaves are seen from both sides), pointing away from the plant's middle
    out_dir = cen - centre_u
    nrm *= np.where((nrm * out_dir).sum(1) < 0, -1.0, 1.0)[:, None]
    feat = np.hstack([cen / radius, nrm * 0.8])
    lab = kmeans(feat, area, min(CARDS, len(tri)))
    cards = []
    for k in range(lab.max() + 1):
        m = lab == k
        if not m.any():
            continue
        pts = np.vstack([a[m], b[m], c[m]])
        mid = pts.mean(0)
        cov = np.cov((pts - mid).T)
        w_, v_ = np.linalg.eigh(cov)
        nk = v_[:, 0]
        if np.dot(nk, mid - centre_u) < 0:
            nk = -nk
        r, up = frame_axes(nk)
        pu, pv, pt = (pts - mid) @ r, (pts - mid) @ up, (pts - mid) @ nk
        u0, u1, v0, v1 = pu.min(), pu.max(), pv.min(), pv.max()
        if u1 - u0 < 0.02 or v1 - v0 < 0.02:
            continue
        cards.append({'mask': m, 'mid': mid, 'n': nk, 'r': r, 'up': up, 'u': (u0, u1), 'v': (v0, v1), 't': (pt.min(), pt.max()),
                      'area': float(area[m].sum())})
    # tiles: as large as the card is (m), the whole set fitted into the atlas by shelves
    scale = ATLAS / np.sqrt(sum((cd['u'][1] - cd['u'][0]) * (cd['v'][1] - cd['v'][0]) for cd in cards)) * 0.8
    while True:
        x = y = shelf = 0
        ok = True
        for cd in sorted(cards, key=lambda cd: -(cd['v'][1] - cd['v'][0])):
            tw = max(8, int((cd['u'][1] - cd['u'][0]) * scale))
            th = max(8, int((cd['v'][1] - cd['v'][0]) * scale))
            if x + tw > ATLAS:
                x, y, shelf = 0, y + shelf + 2, 0
            if y + th > ATLAS or tw > ATLAS:
                ok = False
                break
            cd['tile'] = (x, y, tw, th)
            x += tw + 2
            shelf = max(shelf, th)
        if ok:
            break
        scale *= 0.92
    cloud = {k: np.zeros((ATLAS, ATLAS, ch), np.float32) for k, ch in (('albedo', 3), ('normal', 3), ('ao', 1), ('alpha', 1))}
    # each card's triangles alone: a copy of the plant with only them
    faces_of = {}
    for ci, cd in enumerate(cards):
        faces_of[ci] = np.nonzero(cd['mask'])[0]
    plant.hide_render = True
    entries = []
    for ci, cd in enumerate(cards):
        part = plant.copy()
        part.data = plant.data.copy()
        sc.collection.objects.link(part)
        part.hide_render = False
        bm = bmesh.new()
        bm.from_mesh(part.data)
        bm.faces.ensure_lookup_table()
        keepf = set(faces_of[ci].tolist())
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keepf], context='FACES')
        bm.to_mesh(part.data)
        bm.free()
        x, y, tw, th = cd['tile']
        uw, vh = cd['u'][1] - cd['u'][0], cd['v'][1] - cd['v'][0]
        # the tile has the card's aspect: the larger side spans the orthographic scale
        uc = cd['mid'] + cd['r'] * (cd['u'][0] + cd['u'][1]) / 2 + cd['up'] * (cd['v'][0] + cd['v'][1]) / 2 + cd['n'] * cd['t'][1]
        alb, nrmc, ao, alpha = render(sc, cam, u2b(cd['r']), u2b(cd['up']), u2b(cd['n']), u2b(uc), uw if tw >= th else vh,
                                       tw, th, tmp, passes)
        cloud['albedo'][y:y + th, x:x + tw] = alb
        cloud['normal'][y:y + th, x:x + tw] = nrmc
        cloud['ao'][y:y + th, x:x + tw, 0] = ao
        cloud['alpha'][y:y + th, x:x + tw, 0] = alpha
        bpy.data.objects.remove(part, do_unlink=True)
        # the card in the middle of its slab
        tmid = (cd['t'][0] + cd['t'][1]) / 2
        corners = []
        for su, sv in ((0, 0), (1, 0), (1, 1), (0, 1)):
            p = cd['mid'] + cd['r'] * cd['u'][su] + cd['up'] * cd['v'][sv] + cd['n'] * tmid
            corners.append([float(q) for q in p])
        entries.append({'corners': corners, 'uv': [x / ATLAS, y / ATLAS, (x + tw) / ATLAS, (y + th) / ATLAS],
                        'normal': [float(q) for q in cd['n']], 'tangent': [float(q) for q in cd['r']]})
        print('card', name, ci, len(cards), flush=True)
    np.savez_compressed(os.path.join(out, name + '_cloud.npz'), **cloud)
    meta[name] = {'asset': asset, 'node': node, 'frames': n, 'tile': TILE, 'size_m': size, 'centre_y': float(centre_u[1]),
                  'height_m': height, 'radius_m': radius, 'shift_u': [float(q) for q in shift_u], 'cards': entries}
    json.dump(meta, open(meta_path, 'w'), indent=1)
    print('shrub', name, 'height', round(height, 2), 'cards', len(entries), flush=True)
print('done')
