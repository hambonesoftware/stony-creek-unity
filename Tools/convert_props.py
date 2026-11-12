"""Bake the downloaded .glb props into compact vertex-coloured meshes Unity loads directly.

Reads Data/props/*.glb, writes Assets/Resources/Props/<name>.bytes. Colours (material factors,
vertex colours and texture colours sampled at each vertex) are baked into per-vertex colours, so
no glTF importer or extra shader is needed. Meshes are converted to Unity's left-handed axes,
sat on y = 0 with the long horizontal axis along +Z, and scaled to a real-world length.

Run from the project root:  python Tools/convert_props.py
"""
import io, json, site, struct, sys
from pathlib import Path
try:
    import numpy as np
except ImportError:  # running with -I (isolated) hides the user site-packages; add just that folder back
    sys.path.append(site.getusersitepackages())
    import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "Data" / "props"
OUT = ROOT / "Assets" / "Resources" / "Props"

# target length in metres along the long horizontal axis (None keeps the model's own size)
LENGTH = {
    "picnic_a": 1.9, "picnic_b": 1.9, "picnic_c": 1.9, "bench": 1.8, "slide": 3.4, "roundabout": 2.6,
    "canoe_a": 4.9, "canoe_b": 4.9, "canoe_rack": 5.2, "grill": 0.9,
    "car_sedan": 4.7, "car_hatch": 4.1, "car_wagon": 4.8, "car_muscle": 4.9,
}
COMP = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def to_linear(c):
    c = np.asarray(c, np.float32)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


class Glb:
    def __init__(self, path):
        data = Path(path).read_bytes()
        magic, _, _ = struct.unpack_from("<4sII", data, 0)
        if magic != b"glTF":
            raise ValueError("not a glb")
        off, self.bin = 12, b""
        while off < len(data):
            n, kind = struct.unpack_from("<I4s", data, off)
            chunk = data[off + 8: off + 8 + n]
            if kind == b"JSON":
                self.j = json.loads(chunk)
            elif kind.startswith(b"BIN"):
                self.bin = chunk
            off += 8 + n

    def view(self, i):
        v = self.j["bufferViews"][i]
        o = v.get("byteOffset", 0)
        return self.bin[o: o + v["byteLength"]], v.get("byteStride")

    def accessor(self, i):
        a = self.j["accessors"][i]
        n = NCOMP[a["type"]]
        dt = np.dtype(COMP[a["componentType"]])
        if "bufferView" not in a:
            return np.zeros((a["count"], n), dt)
        raw, stride = self.view(a["bufferView"])
        o = a.get("byteOffset", 0)
        esz = dt.itemsize * n
        step = stride or esz
        arr = np.ndarray(shape=(a["count"], n), dtype=dt, buffer=raw, offset=o, strides=(step, dt.itemsize)).copy()
        if a.get("normalized"):
            arr = arr.astype(np.float32) / np.iinfo(dt).max if dt.kind in "ui" else arr
        return arr

    def image(self, tex_index):
        t = self.j["textures"][tex_index]
        img = self.j["images"][t["source"]]
        if "bufferView" in img:
            raw, _ = self.view(img["bufferView"])
            return np.asarray(Image.open(io.BytesIO(raw)).convert("RGBA"), np.float32) / 255
        return None


def node_matrix(n):
    if "matrix" in n:
        return np.array(n["matrix"], np.float64).reshape(4, 4).T
    t = np.array(n.get("translation", [0, 0, 0]), np.float64)
    q = np.array(n.get("rotation", [0, 0, 0, 1]), np.float64)
    s = np.array(n.get("scale", [1, 1, 1]), np.float64)
    x, y, z, w = q
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                  [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                  [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    M = np.eye(4)
    M[:3, :3] = R * s
    M[:3, 3] = t
    return M


def convert(path):
    g = Glb(path)
    j = g.j
    verts, norms, cols, tris = [], [], [], []
    texcache = {}

    def walk(ni, parent):
        n = j["nodes"][ni]
        M = parent @ node_matrix(n)
        if "mesh" in n:
            for prim in j["meshes"][n["mesh"]]["primitives"]:
                if prim.get("mode", 4) != 4:
                    continue
                at = prim["attributes"]
                P = g.accessor(at["POSITION"]).astype(np.float64)
                N = g.accessor(at["NORMAL"]).astype(np.float64) if "NORMAL" in at else np.tile([0, 1, 0], (len(P), 1)).astype(np.float64)
                Pw = (np.c_[P, np.ones(len(P))] @ M.T)[:, :3]
                Nw = N @ np.linalg.inv(M[:3, :3]).T
                Nw /= np.maximum(np.linalg.norm(Nw, axis=1, keepdims=True), 1e-9)
                mat = j["materials"][prim["material"]] if "material" in prim else {}
                pbr = mat.get("pbrMetallicRoughness", {})
                fac = to_linear(np.array(pbr.get("baseColorFactor", [1, 1, 1, 1]))[:3])
                col = np.tile(fac, (len(P), 1))
                if "baseColorTexture" in pbr and "TEXCOORD_0" in at:
                    ti = pbr["baseColorTexture"]["index"]
                    if ti not in texcache:
                        texcache[ti] = g.image(ti)
                    img = texcache[ti]
                    if img is not None:
                        uv = g.accessor(at["TEXCOORD_0"]).astype(np.float64)
                        h, w = img.shape[:2]
                        px = (np.mod(uv[:, 0], 1.0) * (w - 1)).round().astype(int)
                        py = (np.mod(uv[:, 1], 1.0) * (h - 1)).round().astype(int)
                        col = col * to_linear(img[py, px, :3])
                if "COLOR_0" in at:
                    vc = g.accessor(at["COLOR_0"]).astype(np.float32)[:, :3]
                    col = col * vc   # COLOR_0 is already linear in glTF
                idx = g.accessor(prim["indices"]).astype(np.int64).ravel() if "indices" in prim else np.arange(len(P))
                base = sum(len(v) for v in verts)
                verts.append(Pw); norms.append(Nw); cols.append(to_srgb(col))
                tris.append(idx.reshape(-1, 3) + base)
        for c in n.get("children", []):
            walk(c, M)

    scene = j["scenes"][j.get("scene", 0)]
    for ni in scene["nodes"]:
        walk(ni, np.eye(4))
    V = np.vstack(verts); N = np.vstack(norms); C = np.vstack(cols); T = np.vstack(tris)
    # glTF (right-handed, +Y up) -> Unity (left-handed): mirror X and flip the winding
    V[:, 0] *= -1; N[:, 0] *= -1
    T = T[:, [0, 2, 1]]
    lo, hi = V.min(0), V.max(0)
    size = hi - lo
    # put the long horizontal axis along Z
    if size[0] > size[2]:
        V = np.c_[V[:, 2], V[:, 1], -V[:, 0]]
        N = np.c_[N[:, 2], N[:, 1], -N[:, 0]]
        lo, hi = V.min(0), V.max(0)
    V -= [(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2]
    return V, N, C, T, hi - lo


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manifest = {}
    for f in sorted(SRC.glob("*.glb")):
        name = f.stem
        try:
            V, N, C, T, size = convert(f)
        except Exception as e:  # a model we cannot read is skipped, not fatal
            print(f"{name}: skipped ({e})", file=sys.stderr)
            continue
        target = LENGTH.get(name)
        k = target / size[2] if target else 1.0
        V = V * k
        size = size * k
        with open(OUT / (name + ".bytes"), "wb") as o:
            o.write(struct.pack("<4sII", b"PRPM", len(V), T.size))
            o.write(V.astype("<f4").tobytes())
            o.write(N.astype("<f4").tobytes())
            o.write(np.c_[np.clip(C, 0, 1) * 255 + 0.5, np.full(len(C), 255)].astype(np.uint8).tobytes())
            o.write(T.astype("<u4").ravel().tobytes())
        manifest[name] = {"verts": int(len(V)), "tris": int(len(T)), "size": [round(float(s), 2) for s in size], "avgColor": [int(x) for x in (C.mean(0) * 255)]}
        print(f"{name:12s} {len(V):6d} verts {len(T):6d} tris  {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} m  avg {manifest[name]['avgColor']}")
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=1))


if __name__ == "__main__":
    main()
