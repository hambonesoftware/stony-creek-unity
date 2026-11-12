"""Download the free park props from the Patch asset library (https://patch.gg/create/toolbox/assets).

Each asset page lists the original file URL and the exact credit line for its licence. Files go to
Data/props/ and the credit lines to Data/props/CREDITS.md. All props here are CC0 or CC BY 4.0.

Run from the project root:  python Tools/fetch_props.py
"""
import html, json, re, sys, urllib.parse, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "Data" / "props"
UA = {"User-Agent": "stony-creek-unity prop fetch"}

# role -> Patch asset id
PROPS = {
    "picnic_a": "polyfork:picnic-table-4ee33a",
    "picnic_b": "polyfork:campsite-picnic-table-361d51",
    "picnic_c": "polyfork:heavy-timber-picnic-table-1ac060",
    "bench": "polyfork:park-bench-dc8b62",
    "slide": "polyfork:playground-slide-8b764c",
    "roundabout": "polyfork:playground-roundabout-e4d43b",
    "canoe_a": "polyfork:camp-canoe-c65d2a",
    "canoe_b": "polyfork:wooden-canoe-1d00c5",
    "canoe_rack": "polyfork:canoe-rack-c8126e",
    "grill": "polyfork:backyard-barbecue-grill-84d4ef",
    "car_sedan": "kaykit:city-builder/car_sedan.gltf",
    "car_hatch": "kaykit:city-builder/car_hatchback.gltf",
    "car_wagon": "kaykit:city-builder/car_stationwagon.gltf",
    "car_muscle": "polyfork:muscle-car-60s-524d46",
}


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=90) as r:
        return r.read()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    credits = ["# Prop credits", "", "From the [Patch](https://patch.gg/create/toolbox/assets) free asset library.", ""]
    for role, aid in PROPS.items():
        page = get("https://patch.gg/create/toolbox/assets/" + urllib.parse.quote(aid, safe=":/")).decode("utf-8", "replace")
        files = sorted(set(re.findall(r'https://assets\.patchusercontent\.com/sha256/[0-9a-f]+/[^"\\<>\s]+', page)))
        files = [f for f in files if "_preview" not in f]
        model = [f for f in files if f.endswith((".glb", ".gltf"))]
        if not model:
            print(f"{role}: no model file found for {aid}", file=sys.stderr)
            continue
        url = model[0]
        ext = ".glb" if url.endswith(".glb") else ".gltf"
        dest = OUT / (role + ext)
        data = get(url)
        dest.write_bytes(data)
        extras = [f for f in files if f not in model]
        if ext == ".gltf":
            for f in extras:  # external buffers / textures the glTF points at
                (OUT / Path(urllib.parse.urlparse(f).path).name).write_bytes(get(f))
        m = re.search(r'([^<>"\\]*?(?:CC BY[^<>"\\]*|CC0[^<>"\\]*))', page)
        line = html.unescape(m.group(1)).strip() if m else "(see asset page)"
        print(f"{role:12s} {len(data) / 1024:7.1f} KB  {aid}  [{line[:70]}]")
        credits.append(f"- `{role}` — {line} ([{aid}](https://patch.gg/create/toolbox/assets/{aid}))")
    (OUT / "CREDITS.md").write_text("\n".join(credits) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
