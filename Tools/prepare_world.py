"""Bake Stony Creek's real geography into compact files the Unity project loads.

Inputs (Data/):
  stony-creek-dem.json/.bin  USGS 3DEP elevation (from sunward's Pages build)
  stony-creek-map.json       OpenStreetMap paths, water, habitat, landmarks
  stony-foliage-zones.json   sunward's per-area tree mix (art direction)
  nlcd.tif                   USGS Annual NLCD 2024 land cover, 30 m

Outputs:
  Data/terrain_heights.r16         1025x1025 uint16, row 0 = south, lakes carved
  Data/splat.png                   terrain layer weights RGBA = lawn, meadow, forest floor, sand (row 0 = north)
  Data/macro.png                   far-distance ground colour (row 0 = north)
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


water_mask = raster(MW, MH, MPP, draw_water) > 0
clear_mask = raster(MW, MH, MPP, draw_tree_clear) > 0
# keep trees a few metres off the shoreline
water_buffer = ndimage.binary_dilation(water_mask, iterations=3)


def mask_at(mask, x, z):
    c = np.clip((np.asarray(x) / MPP).astype(int), 0, MW - 1)
    r = np.clip(((D - np.asarray(z)) / MPP).astype(int), 0, MH - 1)
    return mask[r, c]


# ---------- terrain splat weights (1024 x 1024 cells over W x D) ----------
S = 1024
gx, gz = np.meshgrid((np.arange(S) + 0.5) / S * W, (1 - (np.arange(S) + 0.5) / S) * D)  # row 0 = north
cls = landcover(gx, gz)
lawn = np.isin(cls, list(LAWN)).astype(np.float32)
meadow = np.isin(cls, list(MEADOW)).astype(np.float32)
forest = np.isin(cls, list(FOREST)).astype(np.float32)
sand = np.isin(cls, list(SAND)).astype(np.float32)
# beaches: sandy shoreline near mapped beach landmarks
beach_pts = [to_xz(l["lon"], l["lat"]) for l in osm["landmarks"] if "beach" in l["name"].lower()]
wm = mask_at(water_mask, gx, gz)
near_water = ndimage.binary_dilation(wm, iterations=3) & ~wm
near_beach = np.zeros_like(near_water)
for bx, bz in beach_pts:
    near_beach |= (gx - bx) ** 2 + (gz - bz) ** 2 < 140 ** 2
sand = np.maximum(sand, (near_water & near_beach).astype(np.float32))
noise = ndimage.gaussian_filter(rng.standard_normal((S, S)), 6)
noise = noise / (np.abs(noise).max() + 1e-6)
weights = np.stack([lawn + 0.25 * np.clip(noise, 0, 1) * meadow, meadow + 0.3 * np.clip(-noise, 0, 1) * lawn, forest, sand * 1.5], -1)
weights = ndimage.gaussian_filter(weights, sigma=(1.6, 1.6, 0))
weights[weights.sum(-1) < 1e-4, 0] = 1
weights /= weights.sum(-1, keepdims=True)
Image.fromarray(np.round(weights * 255).astype(np.uint8), "RGBA").save(DATA / "splat.png")

# far-distance colours: forest reads as canopy from afar
MACRO = {41: (62, 86, 42), 42: (40, 64, 38), 43: (55, 78, 40), 90: (66, 88, 48), 52: (118, 116, 70), 71: (139, 136, 84),
         81: (132, 136, 76), 82: (126, 118, 82), 95: (104, 120, 78), 21: (104, 128, 70), 22: (110, 124, 84), 23: (128, 128, 112),
         24: (136, 134, 124), 31: (170, 156, 124), 11: (92, 118, 96)}
macro = np.zeros((S, S, 3), np.float32)
for k, col in MACRO.items():
    macro[cls == k] = col
macro = ndimage.gaussian_filter(macro, sigma=(1.2, 1.2, 0)) * (1 + 0.08 * noise[..., None])
Image.fromarray(np.clip(macro, 0, 255).astype(np.uint8), "RGB").save(DATA / "macro.png")

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
hz = height(xs, zs)
slope = np.hypot(height(xs + 3, zs) - height(xs - 3, zs), height(xs, zs + 3) - height(xs, zs - 3)) / 6
keep = (rng.random(len(xs)) < dens) & ~mask_at(water_buffer, xs, zs) & ~mask_at(clear_mask, xs, zs) & (slope < 0.75)
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
with open(OUT / "landcover.bytes", "wb") as f:
    f.write(struct.pack("<4sHH", b"NLCD", NL_W, NL_H)); f.write(nlcd.astype(np.uint8).tobytes())

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
    "mapWidth": MAP_W, "mapHeight": MAP_H, "mapMetersPerPixel": MAP_MPP,
    "speciesCounts": counts,
    "sources": [dem_meta["source"], osm["source"], "USGS Annual NLCD 2024 land cover (Esri image service)"],
}
(OUT / "world.json").write_text(json.dumps(world, indent=1))
print(f"W={W:.0f} D={D:.0f} lakes={len(lakes)} paths={len(paths)} trees={len(xs)} {counts}")
