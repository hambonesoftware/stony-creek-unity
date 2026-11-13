"""Bake Stony Creek's real geography into compact files the Unity project loads.

Inputs (Data/):
  stony-creek-dem.json/.bin  USGS 3DEP elevation (from sunward's Pages build)
  stony-creek-map.json       OpenStreetMap paths, water, habitat, landmarks
  stony-foliage-zones.json   sunward's per-area tree mix (art direction)
  nlcd.tif                   USGS Annual NLCD 2024 land cover, 30 m
  stony-creek-features.json  OpenStreetMap beaches, sand, parking lots, buildings, golf, mown grass, woods, boat rental
  naip_rgb.png, naip_nir.png USDA NAIP aerial photo, 1 m, colour + near-infrared (Tools/fetch_naip.py)

Outputs:
  Data/terrain_heights.r16         1025x1025 uint16, row 0 = south, lakes carved
  Data/splat.png                   terrain layer weights RGBA = lawn, meadow, forest floor, sand (row 0 = north)
  Assets/Textures/Generated/macro.png  the aerial photo as ground colour, 4096 x 4096 (row 0 = north)
  Assets/Resources/World/*.bytes   trees, paths, lakes, land cover for runtime
  Assets/Resources/World/parkmap.png  GTA-style map art (north up)
  Assets/Resources/World/world.json   bounds, sizes, places, landmarks

Unity coordinates: x metres east of the west edge, z metres north of the south edge.
Run from the project root:  python Tools/prepare_world.py
"""
import json, math, struct
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage
from scipy.spatial import cKDTree
from matplotlib.path import Path as MPath

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Data"
OUT = ROOT / "Assets" / "Resources" / "World"
OUT.mkdir(parents=True, exist_ok=True)
rng = np.random.default_rng(20261110)

dem_meta = json.loads((DATA / "stony-creek-dem.json").read_text())
B = dem_meta["bounds"]
C_LAT = (B["north"] + B["south"]) / 2
W = (B["east"] - B["west"]) * 111320 * math.cos(math.radians(C_LAT))
D = (B["north"] - B["south"]) * 111132
N = dem_meta["width"]
assert dem_meta["height"] == N
enc = dem_meta["encoding"]
raw = np.fromfile(DATA / enc["file"], dtype="<u2").reshape(N, N)  # row 0 = north
elev = enc["offset"] + enc["scale"] * raw.astype(np.float64)
DATUM = math.floor(dem_meta["minimum"])
TERRAIN_H = 110.0


def to_xz(lon, lat):
    return ((lon - B["west"]) / (B["east"] - B["west"]) * W, (lat - B["south"]) / (B["north"] - B["south"]) * D)


def height(x, z):
    """Bilinear elevation above DATUM (metres) at Unity x/z, vectorised."""
    fx = np.clip(np.asarray(x) / W * (N - 1), 0, N - 1)
    fr = np.clip((1 - np.asarray(z) / D) * (N - 1), 0, N - 1)
    x0 = np.floor(fx).astype(int); r0 = np.floor(fr).astype(int)
    x1 = np.minimum(x0 + 1, N - 1); r1 = np.minimum(r0 + 1, N - 1)
    tx = fx - x0; tr = fr - r0
    v = (elev[r0, x0] * (1 - tx) + elev[r0, x1] * tx) * (1 - tr) + (elev[r1, x0] * (1 - tx) + elev[r1, x1] * tx) * tr
    return v - DATUM


osm = json.loads((DATA / "stony-creek-map.json").read_text(encoding="utf-8"))
zones = json.loads((DATA / "stony-foliage-zones.json").read_text(encoding="utf-8"))

# mapped ground features (Tools/fetch_features.py), projected to metres
features = []
_feat_json = json.loads((DATA / "stony-creek-features.json").read_text(encoding="utf-8"))
boat_rentals = [to_xz(p["lon"], p["lat"]) for p in _feat_json.get("points", []) if p["kind"] == "boat_rental"]
for f in _feat_json["features"]:
    pts = np.array([to_xz(lon, lat) for lon, lat in f["coords"][:-1]])
    if len(pts) >= 3:
        features.append({"kind": f["kind"], "name": f.get("name", ""), "type": f.get("type", ""), "levels": f.get("levels", 0), "pts": pts})
MOWN = {"fairway", "green", "tee", "grass", "pitch", "playground"}
SANDY = {"beach", "sand"}

# ---------- lakes: OSM polygons with a shoreline water level (same rule as sunward) ----------
lakes = []
for ring in osm["water"]:
    if len(ring) < 4:
        continue
    pts = np.array([to_xz(lon, lat) for lon, lat in ring])
    step = max(1, len(pts) // 22)
    shore = np.sort(height(pts[::step, 0], pts[::step, 1]))
    level = float(shore[int(len(shore) * 0.32)])
    lakes.append((level, pts))


def raster(width_px, height_px, mpp, draw_fn, mode="L"):
    img = Image.new(mode, (width_px, height_px), 0)
    draw_fn(ImageDraw.Draw(img), lambda x, z: (x / mpp, (D - z) / mpp))
    return np.array(img)


# ---------- terrain heights with carved lake beds ----------
dem_mpp_x = W / (N - 1); dem_mpp_z = D / (N - 1)
lake_img = Image.new("F", (N, N), 9999.0)
ld = ImageDraw.Draw(lake_img)
for level, pts in lakes:
    ld.polygon([(x / dem_mpp_x, (D - z) / dem_mpp_z) for x, z in pts], fill=float(level))
lake_level = np.array(lake_img)
h = elev - DATUM
inside = lake_level < 9000
h = np.where(inside, np.minimum(h, lake_level - 0.9), h)
# soften the shore one cell outward
grown = ndimage.grey_erosion(np.where(inside, lake_level, 9999.0), size=3)
ring = (~inside) & (grown < 9000)
h = np.where(ring, np.minimum(h, grown + 0.15), h)
# parking lots: graded to a best-fit plane (they are, in reality) so the paved surface sits flush
gcol, grow = np.meshgrid(np.arange(N) * dem_mpp_x, D - np.arange(N) * dem_mpp_z)
for f in features:
    if f["kind"] != "parking":
        continue
    img = Image.new("L", (N, N), 0)
    ImageDraw.Draw(img).polygon([(x / dem_mpp_x, (D - z) / dem_mpp_z) for x, z in f["pts"]], fill=1, outline=1)
    m = ndimage.binary_dilation(np.array(img) > 0, structure=np.ones((3, 3)), iterations=2) & ~inside
    if m.sum() < 3:
        continue
    A = np.stack([gcol[m], grow[m], np.ones(m.sum())], 1)
    coef, *_ = np.linalg.lstsq(A, h[m], rcond=None)
    h[m] = A @ coef
h = np.clip(h, 0, TERRAIN_H)
heights16 = np.round(h / TERRAIN_H * 65535).astype("<u2")[::-1]  # row 0 = south for Unity
heights16.tofile(DATA / "terrain_heights.r16")

# ---------- land cover ----------
nlcd = np.array(Image.open(DATA / "nlcd.tif"))  # row 0 = north
NL_H, NL_W = nlcd.shape


def landcover(x, z):
    c = np.clip((np.asarray(x) / W * NL_W).astype(int), 0, NL_W - 1)
    r = np.clip(((1 - np.asarray(z) / D) * NL_H).astype(int), 0, NL_H - 1)
    return nlcd[r, c]


FOREST = {41, 42, 43, 90}
MEADOW = {52, 71, 81, 82, 95}
LAWN = {21, 22, 23, 24, 11}
SAND = {31}

# ---------- masks at 2 m ----------
MPP = 2.0
MW, MH = int(W / MPP) + 1, int(D / MPP) + 1
KIND_TREE_CLEAR = {"residential": 9.0, "service": 7.0, "cycleway": 6.5, "track": 6.0, "footway": 5.5, "path": 5.5}
KIND_WIDTH = {"residential": 5.5, "service": 4.8, "cycleway": 3.2, "track": 3.1, "footway": 2.3, "path": 2.1}
paths = []
for p in osm["paths"]:
    if p.get("foot") == "no" or len(p["coords"]) < 2:
        continue
    paths.append({"kind": p["kind"], "name": p.get("name") or "", "pts": np.array([to_xz(lon, lat) for lon, lat in p["coords"]])})


def draw_water(d, t):
    for _, pts in lakes:
        d.polygon([t(x, z) for x, z in pts], fill=255)


def draw_tree_clear(d, t):
    for p in paths:
        d.line([t(x, z) for x, z in p["pts"]], fill=255, width=max(1, int(2 * KIND_TREE_CLEAR.get(p["kind"], 5.5) / MPP)), joint="curve")


def draw_features(d, t):
    # later kinds win: mown grass 1, sand 2, parking 3, building 4
    for value, kinds in ((1, MOWN), (2, SANDY), (3, {"parking"}), (4, {"building"})):
        for f in features:
            if f["kind"] in kinds:
                d.polygon([t(x, z) for x, z in f["pts"]], fill=value)


def draw_woods(d, t):
    for f in features:
        if f["kind"] == "wood":
            d.polygon([t(x, z) for x, z in f["pts"]], fill=255)


feature_mask = raster(MW, MH, MPP, draw_features)
wood_mask = raster(MW, MH, MPP, draw_woods) > 0
def draw_roads(d, t):
    for p in paths:
        if p["kind"] in ("service", "residential"):
            d.line([t(x, z) for x, z in p["pts"]], fill=255, width=max(1, int(7.5 / MPP)), joint="curve")


road_mask = raster(MW, MH, MPP, draw_roads) > 0
water_mask = raster(MW, MH, MPP, draw_water) > 0
feature_mask[water_mask & (feature_mask != 4)] = 0
# no trees on or right beside mown grass, sand, parking and buildings
feature_clear = ndimage.binary_dilation(feature_mask > 0, iterations=2) | ndimage.binary_dilation(feature_mask == 4, iterations=4)
clear_mask = raster(MW, MH, MPP, draw_tree_clear) > 0
# keep trees a few metres off the shoreline
water_buffer = ndimage.binary_dilation(water_mask, iterations=3)


def mask_at(mask, x, z):
    c = np.clip((np.asarray(x) / MPP).astype(int), 0, MW - 1)
    r = np.clip(((D - np.asarray(z)) / MPP).astype(int), 0, MH - 1)
    return mask[r, c]


# ---------- aerial photo (Tools/fetch_naip.py): USDA NAIP, 1 m, red/green/blue + near-infrared ----------
# The photo decides what the ground is wherever OpenStreetMap does not say: green mown grass vs dry meadow,
# bare sand, pavement and tree canopy. Measured on the park's mapped areas, the classes separate cleanly:
#   mown grass  NDVI 0.23  brightness 134  texture 2.4 (smooth)
#   woods       NDVI 0.40  brightness  92  texture 9.1 (rough, dark, shadowed crowns)
#   sand        NDVI -0.02 brightness 190  warm (red well above blue)
#   pavement    NDVI -0.04 brightness 187  grey
# NDVI = (near-infrared - red) / (near-infrared + red): living plants reflect near-infrared strongly.
Image.MAX_IMAGE_PIXELS = None
photo_rgb_img = Image.open(DATA / "naip_rgb.png").convert("RGB")
photo_nir_img = Image.open(DATA / "naip_nir.png").convert("L")
assert photo_rgb_img.size == (int(round(W)), int(round(D))), f"photo is {photo_rgb_img.size}, expected about {W:.0f} x {D:.0f}"


def photo_features(w, h):
    """Photo measurements averaged into a w x h grid over the park box (row 0 = north)."""
    rgb = np.asarray(photo_rgb_img.resize((w, h), Image.BOX), np.float32)
    nir = np.asarray(photo_nir_img.resize((w, h), Image.BOX), np.float32)
    lum1 = np.asarray(photo_rgb_img.convert("L"), np.float32)
    m1 = np.asarray(Image.fromarray(lum1).resize((w, h), Image.BOX), np.float32)
    m2 = np.asarray(Image.fromarray(lum1 * lum1).resize((w, h), Image.BOX), np.float32)
    texture = ndimage.uniform_filter(np.sqrt(np.clip(m2 - m1 * m1, 0, None)), 3)
    r, b = rgb[..., 0], rgb[..., 2]
    return {"rgb": rgb, "ndvi": (nir - r) / (nir + r + 1e-3), "lum": rgb.mean(-1), "warm": r - b, "green": rgb[..., 1] - r, "texture": texture}


def ss(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def photo_cover(p):
    """Soft class weights from photo measurements: canopy, lawn, meadow, sand, pavement (each 0..1, sum 1)."""
    veg = ss(0.04, 0.16, p["ndvi"])
    canopy = veg * ss(0.27, 0.38, p["ndvi"]) * ss(128, 100, p["lum"]) * ss(4.0, 7.0, p["texture"])
    grass = veg - canopy
    # mown grass is green (green above red) and smooth; dry or long grass reads tan and rougher
    lawn = grass * ss(-6, 8, p["green"]) * ss(0.1, 0.22, p["ndvi"]) * ss(8, 4.5, p["texture"])
    meadow = grass - lawn
    bare = 1 - veg
    sand = bare * ss(140, 175, p["lum"]) * ss(14, 26, p["warm"])
    pave = bare - sand
    return canopy, lawn, meadow, sand, pave


# on the 2 m mask grid: tree canopy for tree placement, classes for the runtime grass
pf2 = photo_features(MW, MH)
canopy2, lawn2, meadow2, sand2, pave2 = photo_cover(pf2)
canopy2 = ndimage.gaussian_filter(canopy2, 1.2)


# ---------- terrain splat weights (2048 x 2048 cells over W x D) ----------
S = 2048
gx, gz = np.meshgrid((np.arange(S) + 0.5) / S * W, (1 - (np.arange(S) + 0.5) / S) * D)  # row 0 = north
cls = landcover(gx, gz)
noise = ndimage.gaussian_filter(rng.standard_normal((S, S)), 12)
noise = noise / (np.abs(noise).max() + 1e-6)
pfS = photo_features(S, S)
canopyS, lawnS, meadowS, sandS, paveS = photo_cover(pfS)
# pavement and rooftops have no layer of their own: the neutral meadow layer, coloured grey by the photo
# Bright bare ground away from the water is late-summer dry grass or dirt, not sand: in a park the real sand is
# the lakeshore and the mapped beaches and bunkers (which OSM paints below). So photo sand only near water.
shore = np.asarray(Image.fromarray(ndimage.binary_dilation(water_mask, iterations=30).astype(np.uint8) * 255).resize((S, S), Image.BILINEAR), np.float32) / 255
weights = np.stack([lawnS, meadowS + paveS + sandS * (1 - shore), canopyS, sandS * shore * 1.3], -1).astype(np.float32)
weights = ndimage.gaussian_filter(weights, sigma=(1.0, 1.0, 0))
# mapped features are exact: sand on the mapped beaches and bunkers, lawn on mown grass, parking and around buildings
fm = mask_at(feature_mask, gx, gz)
wd = mask_at(wood_mask, gx, gz) & (fm == 0)
weights[wd] = (0, 0, 1, 0)
weights[(fm == 1) | (fm == 3) | (fm == 4)] = (1, 0, 0, 0)
weights[fm == 2] = (0, 0, 0, 1)
weights = ndimage.gaussian_filter(weights, sigma=(0.7, 0.7, 0))
weights[weights.sum(-1) < 1e-4, 0] = 1
weights /= weights.sum(-1, keepdims=True)
Image.fromarray(np.round(weights * 255).astype(np.uint8), "RGBA").save(DATA / "splat.png")

# ground colour = the aerial photo, a touch softer and less saturated than the raw scan so it sits with the
# lit ground textures (the photo already carries the sun's light and the trees' shadows)
macro = np.asarray(photo_rgb_img.resize((4096, 4096), Image.LANCZOS), np.float32)
grey = macro.mean(-1, keepdims=True)
macro = grey + (macro - grey) * 0.86
macro = 255 * np.power(np.clip(macro / 255, 0, 1), 1.06)
macro_img = Image.fromarray(np.clip(macro, 0, 255).astype(np.uint8), "RGB")
macro_img.save(ROOT / "Assets" / "Textures" / "Generated" / "macro.png")


def srgb_to_linear(c):
    c = c / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


# linear average colour of each ground texture, so the shader can match the photo's colour up close
layer_avg = []
for name in ("lawn", "meadow", "forest_floor", "sand"):
    t = np.asarray(Image.open(ROOT / "Assets" / "Textures" / "Ground" / f"{name}.jpg").convert("RGB").resize((256, 256), Image.BOX), np.float32)
    layer_avg += [round(float(v), 5) for v in srgb_to_linear(t).reshape(-1, 3).mean(0)]

# ---------- trees ----------
SPECIES = ["oak", "maple", "birch", "pine"]
DEFAULT_MIX = {"oak": 38, "maple": 38, "birch": 12, "pine": 12}
CLASS_DENSITY = {41: 1.0, 42: 1.0, 43: 0.95, 90: 0.72, 52: 0.12, 71: 0.025, 81: 0.012, 82: 0.0, 95: 0.03,
                 21: 0.045, 22: 0.11, 23: 0.035, 24: 0.01, 31: 0.0, 11: 0.0}
CLASS_MIX = {42: {"pine": 1}, 43: {"oak": 25, "maple": 20, "birch": 10, "pine": 45}, 90: {"maple": 55, "birch": 22, "oak": 13, "pine": 10},
             21: {"maple": 45, "oak": 35, "birch": 8, "pine": 12}, 22: {"maple": 45, "oak": 30, "birch": 10, "pine": 15}}
SPACING = 7.0
xs, zs = np.meshgrid(np.arange(SPACING / 2, W, SPACING), np.arange(SPACING / 2, D, SPACING))
xs = (xs + rng.uniform(-0.45, 0.45, xs.shape) * SPACING).ravel()
zs = (zs + rng.uniform(-0.45, 0.45, zs.shape) * SPACING).ravel()
ok = (xs > 2) & (xs < W - 2) & (zs > 2) & (zs < D - 2)
xs, zs = xs[ok], zs[ok]
cl = landcover(xs, zs)
dens = np.vectorize(lambda c: CLASS_DENSITY.get(int(c), 0.0))(cl)
# woodland clumps rather than uniform carpets in open land
clump = ndimage.gaussian_filter(rng.standard_normal((MH // 16 + 1, MW // 16 + 1)), 2.2)
clump = (clump - clump.mean()) / clump.std()
cz = np.clip(((D - zs) / (MPP * 16)).astype(int), 0, clump.shape[0] - 1); cx = np.clip((xs / (MPP * 16)).astype(int), 0, clump.shape[1] - 1)
dens = dens * np.clip(0.75 + 0.35 * clump[cz, cx], 0.15, 1.2)
# where the trees stand comes from the photo's canopy; NLCD only sets the species mix
can = mask_at(canopy2, xs, zs)
dens = np.clip((can - 0.12) / 0.55, 0, 1) * np.clip(0.9 + 0.15 * clump[cz, cx], 0.7, 1.1)
dens = np.where(mask_at(wood_mask, xs, zs), np.maximum(dens, 0.5 * np.clip(can * 2, 0, 1)), dens)
hz = height(xs, zs)
slope = np.hypot(height(xs + 3, zs) - height(xs - 3, zs), height(xs, zs + 3) - height(xs, zs - 3)) / 6
keep = (rng.random(len(xs)) < dens) & ~mask_at(water_buffer, xs, zs) & ~mask_at(clear_mask, xs, zs) & ~mask_at(feature_clear, xs, zs) & (slope < 0.75)
xs, zs, cl = xs[keep], zs[keep], cl[keep]

zone_pts = [(to_xz(zn["center"]["lon"], zn["center"]["lat"]), zn["radiusMeters"] * 1.6, zn["treeWeights"]) for zn in zones["zones"]]


def mix_for(c, x, z):
    if int(c) in CLASS_MIX:
        return CLASS_MIX[int(c)]
    for (zx, zz), r, wts in zone_pts:
        if (x - zx) ** 2 + (z - zz) ** 2 < r * r:
            m = {k: v for k, v in wts.items() if k in SPECIES}
            m["oak"] = m.get("oak", 0) + wts.get("hickory", 0)  # hickory shares the oak model
            return m
    return DEFAULT_MIX


species = np.zeros(len(xs), np.uint8)
for i, (c, x, z) in enumerate(zip(cl, xs, zs)):
    m = mix_for(c, x, z)
    keys = list(m); p = np.array([m[k] for k in keys], float); p /= p.sum()
    species[i] = SPECIES.index(keys[rng.choice(len(keys), p=p)])
in_forest = np.isin(cl, list(FOREST))
scale = np.where(in_forest, rng.uniform(0.85, 1.3, len(xs)), rng.uniform(0.7, 1.15, len(xs)))
scale_q = np.clip(np.round((scale - 0.6) / 0.8 * 31), 0, 31).astype(np.uint8)
rot = rng.integers(0, 256, len(xs), dtype=np.uint8)
order = rng.permutation(len(xs))  # random order lets far LODs draw a thinned subset
with open(OUT / "trees.bytes", "wb") as f:
    f.write(struct.pack("<4sI", b"TREE", len(xs)))
    qx = np.round(xs / W * 65535).astype("<u2"); qz = np.round(zs / D * 65535).astype("<u2")
    rec = np.zeros(len(xs), dtype=[("x", "<u2"), ("z", "<u2"), ("s", "u1"), ("r", "u1")])
    rec["x"] = qx[order]; rec["z"] = qz[order]; rec["s"] = (species[order] << 5) | scale_q[order]; rec["r"] = rot[order]
    f.write(rec.tobytes())
counts = {SPECIES[i]: int((species == i).sum()) for i in range(4)}

# ---------- runtime path / lake / land-cover data ----------
KINDS = ["path", "footway", "cycleway", "track", "service", "residential"]
names = sorted({p["name"] for p in paths if p["name"]})
with open(OUT / "paths.bytes", "wb") as f:
    blob = "\n".join(names).encode("utf-8")
    f.write(struct.pack("<4sII", b"PATH", len(paths), len(blob))); f.write(blob)
    for p in paths:
        kind = KINDS.index(p["kind"]) if p["kind"] in KINDS else 0
        f.write(struct.pack("<BHH", kind, names.index(p["name"]) + 1 if p["name"] else 0, len(p["pts"])))
        f.write(p["pts"].astype("<f4").tobytes())
with open(OUT / "lakes.bytes", "wb") as f:
    f.write(struct.pack("<4sI", b"LAKE", len(lakes)))
    for level, pts in lakes:
        f.write(struct.pack("<fI", level, len(pts))); f.write(pts.astype("<f4").tobytes())
# runtime ground classes from the photo at 6 m, in NLCD codes so the grass field's rules still apply:
# 41 woods, 71 meadow, 21 lawn, 31 bare sand or pavement; NLCD wetlands (90, 95) kept where the photo shows plants
LCW, LCH = int(round(W / 6)), int(round(D / 6))
pf6 = photo_features(LCW, LCH)
c6 = np.stack(photo_cover(pf6), -1)
c6 = np.stack([c6[..., 0], c6[..., 1], c6[..., 2], c6[..., 3] + c6[..., 4]], -1)
codes = np.array([41, 21, 71, 31], np.uint8)[c6.argmax(-1)]
# dry bright grass reads 'bare' from the air; away from the shore and pavement it still grows grass
shore6 = np.asarray(Image.fromarray(ndimage.binary_dilation(water_mask, iterations=30).astype(np.uint8) * 255).resize((LCW, LCH), Image.NEAREST)) > 0
codes = np.where((codes == 31) & ~shore6 & (c6[..., 3] > 0) & (pf6["lum"] < 175) & (np.abs(pf6["warm"]) > 14), 71, codes)
nl6 = np.asarray(Image.fromarray(nlcd.astype(np.uint8)).resize((LCW, LCH), Image.NEAREST))
codes = np.where(np.isin(nl6, [90, 95]) & (codes != 31), nl6, codes)
codes = np.where(np.asarray(Image.fromarray(water_mask.astype(np.uint8) * 255).resize((LCW, LCH), Image.NEAREST)) > 0, 11, codes)
with open(OUT / "landcover.bytes", "wb") as f:
    f.write(struct.pack("<4sHH", b"NLCD", LCW, LCH)); f.write(codes.astype(np.uint8).tobytes())

# mapped features for the runtime: a 4 m mask (0 none, 1 mown, 2 sand, 3 parking, 4 building) and the outlines
fmask = feature_mask[::2, ::2]
with open(OUT / "featuremask.bytes", "wb") as f:
    f.write(struct.pack("<4sHHf", b"FMSK", fmask.shape[1], fmask.shape[0], MPP * 2)); f.write(fmask.astype(np.uint8).tobytes())
def inner_point(pts, want):
    """A point inside the outline whose mask value is `want` (centroids can fall outside curved shapes)."""
    c = pts.mean(0)
    cand = [c] + [c + (q - c) * k for k in (0.3, 0.6, 0.85) for q in pts[:: max(1, len(pts) // 12)]]
    for q in cand:
        if mask_at(feature_mask, q[0], q[1]) == want and not mask_at(water_mask, q[0], q[1]):
            return q
    return None


def named_points(kind, want, default):
    out = []
    for f in features:
        if f["kind"] != kind:
            continue
        q = inner_point(f["pts"], want)
        if q is not None:
            out.append({"name": f["name"] or default, "x": round(float(q[0]), 1), "z": round(float(q[1]), 1)})
    return out


parking_pts = named_points("parking", 3, "Parking")
beach_pts = named_points("beach", 2, "Beach")

# ---------- building styles (from Street View reference) ----------
def area_m2(pts):
    x, z = pts[:, 0], pts[:, 1]
    return abs(float(np.dot(x, np.roll(z, -1)) - np.dot(z, np.roll(x, -1)))) / 2


BTYPE = {"building": 0, "house": 1, "residential": 1, "detached": 1, "garage": 2, "garages": 2, "shed": 2, "shelter": 3, "toilets": 4}
# style 0 generic, 1 timber lodge (Nature Center), 2 flat-roofed brick (beach bathhouses and restrooms), 4 civic brick
dense = []
for pth in paths:
    q = pth["pts"]
    for i in range(1, len(q)):
        n = max(1, int(np.hypot(*(q[i] - q[i - 1])) / 5))
        dense.extend(q[i - 1] + (q[i] - q[i - 1]) * (k / n) for k in range(n))
path_tree = cKDTree(np.array(dense))
beach_xy = [(b["x"], b["z"]) for b in beach_pts]
outlines = [f for f in features if f["kind"] in ("parking", "building")]
style_counts = {}
with open(OUT / "features.bytes", "wb") as f:
    f.write(struct.pack("<4sI", b"FEAT", len(outlines)))
    for o in outlines:
        style, front = 0, 0
        if o["kind"] == "building":
            c = o["pts"].mean(0)
            near_beach = any(math.hypot(c[0] - bx, c[1] - bz) < 220 for bx, bz in beach_xy)
            ar = area_m2(o["pts"])
            if "Nature Center" in o["name"]:
                style = 1
            elif o["name"] == "Park Office":
                style = 4
            elif o["type"] == "toilets" or (o["type"] == "building" and near_beach and 40 <= ar <= 900):
                style = 2
            _, i = path_tree.query(c)
            v = path_tree.data[i] - c
            front = int(round(math.atan2(v[1], v[0]) / (2 * math.pi) * 256)) % 256
            style_counts[style] = style_counts.get(style, 0) + 1
        f.write(struct.pack("<BBBBBH", 3 if o["kind"] == "parking" else 4, BTYPE.get(o["type"], 0), min(o["levels"], 9), style, front, len(o["pts"])))
        f.write(o["pts"].astype("<f4").tobytes())

# ---------- props (picnic tables, benches, playgrounds, canoes, grills, parked cars) ----------
ROLES = ["picnic_a", "picnic_b", "picnic_c", "bench", "slide", "roundabout", "canoe_a", "canoe_b", "canoe_rack", "grill",
         "car_sedan", "car_hatch", "car_wagon", "car_muscle"]
prng = np.random.default_rng(777)
props = []
water_dist = ndimage.distance_transform_edt(~water_mask) * MPP


def add_prop(role, x, z, yaw, scale=1.0):
    props.append((ROLES.index(role), float(x), float(z), float(yaw) % 360, float(scale)))


def yaw_of(d):  # Unity yaw: clockwise from north (+z) toward east (+x)
    return math.degrees(math.atan2(d[0], d[1]))


def long_axes(pts):
    e = np.roll(pts, -1, 0) - pts
    L = np.hypot(e[:, 0], e[:, 1])
    a = e[int(L.argmax())] / max(L.max(), 1e-6)
    return a, np.array([-a[1], a[0]])


def dry(x, z):
    return not mask_at(water_mask, x, z) and mask_at(feature_mask, x, z) not in (3, 4)


# shelters: tables underneath, a grill beside
for fb in features:
    if fb["kind"] != "building" or fb["type"] != "shelter":
        continue
    a, b = long_axes(fb["pts"])
    c = fb["pts"].mean(0)
    ua, ub = fb["pts"] @ a, fb["pts"] @ b
    length, width = ua.max() - ua.min(), ub.max() - ub.min()
    if width > length:
        a, b, length, width = b, -a, width, length
    n = 2 if length >= 7 else 1
    for k in range(n):
        o = c + a * ((k - (n - 1) / 2) * min(3.6, length * 0.4))
        add_prop(["picnic_a", "picnic_b", "picnic_c"][int(prng.integers(0, 3))], o[0], o[1], yaw_of(a) + (90 if width < 4 else 0) + (180 if prng.random() < 0.5 else 0))
    g = c + b * (width / 2 + 3.2) + a * (length * 0.3)
    if dry(*g):
        add_prop("grill", g[0], g[1], prng.uniform(0, 360))

# playgrounds
for fp in features:
    if fp["kind"] != "playground":
        continue
    q = inner_point(fp["pts"], 1)
    if q is None:
        continue
    poly = MPath(fp["pts"])
    add_prop("slide", q[0], q[1], prng.uniform(0, 360))
    for ang in prng.permutation(8) * 45:
        r2 = q + 5.2 * np.array([math.cos(math.radians(ang)), math.sin(math.radians(ang))])
        if poly.contains_point(r2) and dry(*r2):
            add_prop("roundabout", r2[0], r2[1], prng.uniform(0, 360))
            break

# beaches: a few picnic tables and benches on the lawn behind the sand
for bp in beach_pts:
    if "Beach" not in bp["name"] or bp["name"] == "Beach":
        continue
    placed = []
    for _ in range(400):
        ang = prng.uniform(0, 2 * math.pi)
        r = prng.uniform(25, 90)
        x, z = bp["x"] + r * math.cos(ang), bp["z"] + r * math.sin(ang)
        wd = water_dist[int(np.clip((D - z) / MPP, 0, MH - 1)), int(np.clip(x / MPP, 0, MW - 1))]
        if not (14 <= wd <= 55) or not dry(x, z) or mask_at(feature_mask, x, z) == 2 or any(math.hypot(x - px, z - pz) < 14 for px, pz in placed):
            continue
        placed.append((x, z))
        add_prop("picnic_" + "abc"[len(placed) % 3], x, z, prng.uniform(0, 360))
        if len(placed) >= 5:
            break

# benches along the paved bike path and named footpaths
bench_pts = []
for pth in paths:
    if pth["kind"] not in ("cycleway", "footway") or (pth["kind"] == "footway" and not pth["name"]):
        continue
    q = pth["pts"]
    acc, nxt, side = 0.0, prng.uniform(60, 380), 1
    for i in range(1, len(q)):
        seg = q[i] - q[i - 1]
        L = float(np.hypot(*seg))
        if L < 1e-3:
            continue
        d = seg / L
        while acc + L >= nxt and len(bench_pts) < 150:
            t = (nxt - acc) / L
            pos = q[i - 1] + seg * t + np.array([-d[1], d[0]]) * 2.8 * side
            if dry(*pos) and all(math.hypot(pos[0] - bx, pos[1] - bz) > 25 for bx, bz in bench_pts) and not mask_at(road_mask, pos[0], pos[1]):
                bench_pts.append((pos[0], pos[1]))
                add_prop("bench", pos[0], pos[1], yaw_of(d))
                side = -side
            nxt += prng.uniform(300, 520)
        acc += L

# boat rental: a canoe rack with a couple of canoes beside it
for bx, bz in boat_rentals:
    ix, iz = int(np.clip(bx / MPP, 0, MW - 1)), int(np.clip((D - bz) / MPP, 0, MH - 1))
    if water_mask[iz, ix]:
        idx = ndimage.distance_transform_edt(water_mask, return_indices=True)[1]
        iz, ix = idx[0][iz, ix], idx[1][iz, ix]
        bx, bz = ix * MPP + 6, D - iz * MPP
    add_prop("canoe_rack", bx, bz, 90)
    add_prop("canoe_a", bx + 0, bz + 5, 90)
    add_prop("canoe_b", bx + 0, bz - 5, 90)

# parked cars: stalls laid out in each mapped lot, never on a mapped aisle
car_roles, car_w = ["car_sedan", "car_hatch", "car_wagon", "car_muscle"], [0.31, 0.31, 0.31, 0.07]
muscle = 0
cars = 0
for fl in features:
    if fl["kind"] != "parking":
        continue
    pts = fl["pts"]
    a, b = long_axes(pts)
    c = pts.mean(0)
    ua, ub = (pts - c) @ a, (pts - c) @ b
    poly = MPath(pts)
    occ = 0.5 if any(math.hypot(c[0] - bx, c[1] - bz) < 450 for bx, bz in beach_xy) else 0.26
    lot = 0
    for u in np.arange(ua.min() + 1.8, ua.max() - 1.2, 2.8):
        for v in np.arange(ub.min() + 3.2, ub.max() - 2.4, 5.9):
            if prng.random() > occ or lot >= 70 or cars >= 1100:
                continue
            ctr = c + a * u + b * v
            corners = [ctr + a * sa * 1.05 + b * sb * 2.4 for sa in (-1, 1) for sb in (-1, 1)]
            if not poly.contains_point(ctr) or not all(poly.contains_point(k) for k in corners):
                continue
            if any(mask_at(water_mask, k[0], k[1]) or mask_at(road_mask, k[0], k[1]) for k in corners + [ctr]):
                continue
            role = car_roles[int(prng.choice(4, p=car_w))]
            if role == "car_muscle":
                if muscle >= 30:
                    role = "car_sedan"
                muscle += 1
            add_prop(role, ctr[0], ctr[1], yaw_of(b) + (180 if prng.random() < 0.5 else 0) + prng.uniform(-2.5, 2.5), prng.uniform(0.96, 1.04))
            lot += 1
            cars += 1

with open(OUT / "props.bytes", "wb") as f:
    blob = "\n".join(ROLES).encode()
    f.write(struct.pack("<4sII", b"PROP", len(props), len(blob))); f.write(blob)
    for role, x, z, yaw, sc in props:
        f.write(struct.pack("<Bffff", role, x, z, yaw, sc))
prop_counts = {ROLES[i]: sum(1 for p in props if p[0] == i) for i in range(len(ROLES))}
prop_samples = {}
for p in props:
    key = "car" if ROLES[p[0]].startswith("car_") else "picnic" if ROLES[p[0]].startswith("picnic") else "canoe" if ROLES[p[0]].startswith("canoe") else ROLES[p[0]]
    if len(prop_samples.setdefault(key, [])) < 4:
        prop_samples[key].append({"x": round(p[1], 1), "z": round(p[2], 1), "yaw": round(p[3], 1)})

# ---------- GTA-style map art (north up, ~3.2 m per pixel) ----------
MAP_W = 2048; MAP_MPP = W / MAP_W; MAP_H = int(round(D / MAP_MPP))
SS = 2
MAP_COL = {41: (96, 128, 72), 42: (76, 110, 70), 43: (90, 122, 70), 90: (98, 132, 96), 52: (150, 150, 100), 71: (164, 166, 112),
           81: (160, 164, 104), 82: (156, 146, 106), 95: (122, 148, 112), 21: (140, 162, 104), 22: (146, 150, 120),
           23: (150, 148, 138), 24: (156, 152, 146), 31: (186, 172, 140), 11: (132, 160, 112)}
mx, mz = np.meshgrid((np.arange(MAP_W) + 0.5) * MAP_MPP, D - (np.arange(MAP_H) + 0.5) * MAP_MPP)
mcls = landcover(mx, mz)
base = np.zeros((MAP_H, MAP_W, 3), np.float32)
for k, col in MAP_COL.items():
    base[mcls == k] = col
base = ndimage.gaussian_filter(base, sigma=(4.5, 4.5, 0))
# hillshade from the DEM, light from the north-west
hh = height(mx, mz)
dzdx = np.gradient(hh, axis=1) / MAP_MPP; dzdy = -np.gradient(hh, axis=0) / MAP_MPP
nx, ny, nz = -dzdx * 3, -dzdy * 3, np.ones_like(hh)
ln = np.sqrt(nx * nx + ny * ny + nz * nz)
lit = np.clip((nx * -0.6 + ny * 0.6 + nz * 0.55) / ln, 0, 1)
base *= (0.72 + 0.42 * lit)[..., None]
img = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), "RGB").resize((MAP_W * SS, MAP_H * SS), Image.BILINEAR)
d = ImageDraw.Draw(img)
tm = lambda x, z: (x / MAP_MPP * SS, (D - z) / MAP_MPP * SS)
for _, pts in lakes:
    d.polygon([tm(x, z) for x, z in pts], fill=(84, 142, 178), outline=(64, 116, 150))
FEATURE_COL = {"fairway": (150, 182, 108), "rough": (132, 160, 100), "grass": (148, 176, 108), "pitch": (146, 180, 106), "playground": (176, 170, 128),
               "tee": (160, 194, 116), "green": (168, 204, 122), "beach": (228, 210, 160), "sand": (228, 210, 160)}
for order_kinds in (("fairway", "rough", "grass", "pitch", "playground"), ("tee", "green", "beach", "sand")):
    for f in features:
        if f["kind"] in order_kinds:
            d.polygon([tm(x, z) for x, z in f["pts"]], fill=FEATURE_COL[f["kind"]])
for f in features:
    if f["kind"] == "parking":
        d.polygon([tm(x, z) for x, z in f["pts"]], fill=(170, 172, 170), outline=(120, 122, 120))
for f in features:
    if f["kind"] == "building":
        d.polygon([tm(x, z) for x, z in f["pts"]], fill=(128, 108, 92), outline=(92, 76, 64))
px = 1 / MAP_MPP * SS  # pixels per metre at supersampled scale
layers = [("track", (122, 96, 64), 2.6), ("path", (122, 96, 64), 2.2), ("footway", (130, 104, 72), 2.2)]
for kind, col, wm_ in layers:
    for p in paths:
        if p["kind"] == kind:
            d.line([tm(x, z) for x, z in p["pts"]], fill=col, width=max(2, int(wm_ * px)), joint="curve")
for p in paths:
    if p["kind"] in ("service", "residential"):
        d.line([tm(x, z) for x, z in p["pts"]], fill=(98, 100, 96), width=int((10 if p["kind"] == "residential" else 8) * px), joint="curve")
for p in paths:
    if p["kind"] == "cycleway":
        d.line([tm(x, z) for x, z in p["pts"]], fill=(150, 104, 40), width=int(9 * px), joint="curve")
for p in paths:
    if p["kind"] in ("service", "residential"):
        d.line([tm(x, z) for x, z in p["pts"]], fill=(240, 238, 228), width=int((6.5 if p["kind"] == "residential" else 5) * px), joint="curve")
for p in paths:
    if p["kind"] == "cycleway":
        d.line([tm(x, z) for x, z in p["pts"]], fill=(226, 178, 90), width=int(5.5 * px), joint="curve")
img = img.resize((MAP_W, MAP_H), Image.LANCZOS)
img.save(OUT / "parkmap.png", optimize=True)

# ---------- world.json ----------
PLACES = [("Nature Center trails", 42.76078, -83.0745), ("Eastwood Beach", 42.722, -83.078), ("Baypoint Beach", 42.7284, -83.09064),
          ("Shelden Trails", 42.73046, -83.1072), ("Trolley Trails", 42.71823, -83.079)]
seen = []
for l in osm["landmarks"]:
    if not l["name"] or l["name"] == "Beach":
        continue
    x, z = to_xz(l["lon"], l["lat"])
    if any(s["name"] == l["name"] and math.hypot(s["x"] - x, s["z"] - z) < 500 for s in seen):
        continue
    seen.append({"name": l["name"], "x": round(x, 1), "z": round(z, 1)})
world = {
    "width": W, "depth": D, "terrainHeight": TERRAIN_H, "datum": DATUM, "bounds": B,
    "places": [{"name": n, "x": round(to_xz(lon, lat)[0], 1), "z": round(to_xz(lon, lat)[1], 1)} for n, lat, lon in PLACES],
    "landmarks": seen,
    "parking": parking_pts,
    "beaches": beach_pts,
    "propCounts": prop_counts,
    "propSamples": prop_samples,
    "styleCounts": {str(k): v for k, v in style_counts.items()},
    "mapWidth": MAP_W, "mapHeight": MAP_H, "mapMetersPerPixel": MAP_MPP,
    "speciesCounts": counts,
    "sources": [dem_meta["source"], osm["source"], "USDA NAIP aerial photo via USGS The National Map (public domain)", "USGS Annual NLCD 2024 land cover (Esri image service)"],
    "layerAverages": layer_avg,
}
(OUT / "world.json").write_text(json.dumps(world, indent=1))
on_feature = int(np.count_nonzero(mask_at(feature_mask, xs, zs)))
assert on_feature == 0, f"{on_feature} trees stand on mown grass, sand, parking or buildings"
print(f"W={W:.0f} D={D:.0f} lakes={len(lakes)} paths={len(paths)} trees={len(xs)} {counts}")
print("props:", {k: v for k, v in prop_counts.items() if v}, "| building styles:", style_counts)
print(f"features: {len(features)} outlines, parking points {len(parking_pts)}, beaches {[b['name'] for b in beach_pts]}")
