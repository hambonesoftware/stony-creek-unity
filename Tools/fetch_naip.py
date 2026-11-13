"""Download the USDA NAIP aerial photo of the park box from the USGS National Map image service.

NAIP is public domain (USDA Farm Service Agency, served by USGS The National Map). The service holds
4-band imagery (red, green, blue, near-infrared) at about 0.6 m. We take it at 1 m per pixel, in plain
lon/lat so pixel columns line up with Unity x (east) and rows with Unity z (north).

Outputs (row 0 = north):
  Data/naip_rgb.png   natural colour
  Data/naip_nir.png   near-infrared, for telling plants from bare ground

Run from the project root:  python Tools/fetch_naip.py
"""
import io, json, math, sys, time, urllib.parse, urllib.request
from pathlib import Path
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "Data"
B = json.loads((DATA / "stony-creek-dem.json").read_text())["bounds"]
SERVICE = "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPImagery/ImageServer/exportImage"
MPP = 1.0
MAX = 3600  # the service allows 4000 x 4000 per request

c_lat = (B["south"] + B["north"]) / 2
W_m = (B["east"] - B["west"]) * 111320 * math.cos(math.radians(c_lat))
D_m = (B["north"] - B["south"]) * 111132
PW, PH = int(round(W_m / MPP)), int(round(D_m / MPP))
# The service keeps output pixels square in the output reference system (degrees here), and silently widens
# the box if the requested size disagrees. So fetch with square-degree pixels (1 m north-south), then
# stretch east-west to the metre grid once the mosaic is assembled.
DEG = (B["north"] - B["south"]) / PH
QW, QH = int(round((B["east"] - B["west"]) / DEG)), PH
nx, ny = math.ceil(QW / MAX), math.ceil(QH / MAX)
print(f"park box {W_m:.0f} x {D_m:.0f} m -> {PW} x {PH} px (fetched as {QW} x {QH} in {nx} x {ny} tiles)")


def fetch(bbox, w, h, bands):
    q = urllib.parse.urlencode({
        "bbox": ",".join(f"{v:.7f}" for v in bbox), "bboxSR": 4326, "imageSR": 4326, "size": f"{w},{h}",
        "format": "png", "pixelType": "U8", "bandIds": bands, "interpolation": "RSP_BilinearInterpolation", "f": "image",
    })
    req = urllib.request.Request(f"{SERVICE}?{q}", headers={"User-Agent": "stony-creek-unity data prep"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                data = r.read()
            img = Image.open(io.BytesIO(data))
            img.load()
            return img
        except Exception as e:
            print(f"  retry {attempt + 1}: {e}", file=sys.stderr)
            time.sleep(4 * (attempt + 1))
    raise SystemExit("NAIP service did not answer")


rgb = Image.new("RGB", (QW, QH))
nir = Image.new("L", (QW, QH))
for j in range(ny):
    for i in range(nx):
        x0, x1 = i * QW // nx, (i + 1) * QW // nx
        y0, y1 = j * QH // ny, (j + 1) * QH // ny          # y counts down from the north edge
        bbox = (B["west"] + x0 * DEG, B["north"] - y1 * DEG, B["west"] + x1 * DEG, B["north"] - y0 * DEG)
        print(f"tile {j * nx + i + 1}/{nx * ny}: {x1 - x0} x {y1 - y0}")
        for img, band, mode in ((rgb, "0,1,2", "RGB"), (nir, "3", "L")):
            t = fetch(bbox, x1 - x0, y1 - y0, band).convert(mode)
            if t.size != (x1 - x0, y1 - y0):
                raise SystemExit(f"service returned {t.size}, asked for {(x1 - x0, y1 - y0)}")
            img.paste(t, (x0, y0))
rgb = rgb.resize((PW, PH), Image.LANCZOS)
nir = nir.resize((PW, PH), Image.LANCZOS)
rgb.save(DATA / "naip_rgb.png", optimize=True)
nir.save(DATA / "naip_nir.png", optimize=True)
meta = {"source": "USDA NAIP via USGS The National Map (public domain)", "service": SERVICE, "metresPerPixel": MPP,
        "width": PW, "height": PH, "bounds": B, "rowZero": "north", "retrievedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
(DATA / "naip.json").write_text(json.dumps(meta, indent=1))
print("saved", DATA / "naip_rgb.png", DATA / "naip_nir.png")
