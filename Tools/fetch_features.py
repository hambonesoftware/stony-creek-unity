"""Download mapped ground features for the park box from OpenStreetMap (Overpass API).

Beaches, sand, parking lots, building footprints, golf course parts, mown grass, pitches,
playgrounds and mapped woods, as closed outlines. Output: Data/stony-creek-features.json
(c) OpenStreetMap contributors, ODbL.

Run from the project root:  python Tools/fetch_features.py
"""
import json, sys, urllib.parse, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BOUNDS = json.loads((ROOT / "Data" / "stony-creek-dem.json").read_text())["bounds"]
BBOX = f'{BOUNDS["south"]},{BOUNDS["west"]},{BOUNDS["north"]},{BOUNDS["east"]}'
QUERY = f"""
[out:json][timeout:120][bbox:{BBOX}];
(
  way["natural"~"^(beach|sand|wood)$"]; relation["natural"~"^(beach|sand|wood)$"];
  way["amenity"="parking"]; relation["amenity"="parking"];
  way["building"]; relation["building"];
  way["golf"~"^(fairway|green|tee|bunker|rough)$"];
  way["landuse"~"^(grass|forest|meadow)$"]; relation["landuse"~"^(grass|forest|meadow)$"];
  way["leisure"~"^(pitch|playground)$"];
);
out geom;
"""
HOSTS = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter"]


def kind_of(t):
    if "building" in t:
        return "building"
    if t.get("amenity") == "parking":
        return "parking"
    if t.get("natural") == "beach":
        return "beach"
    if t.get("natural") == "sand" or t.get("golf") == "bunker":
        return "sand"
    if t.get("golf") in ("fairway", "green", "tee", "rough"):
        return t["golf"]
    if t.get("leisure") in ("pitch", "playground"):
        return t["leisure"]
    if t.get("natural") == "wood" or t.get("landuse") == "forest":
        return "wood"
    if t.get("landuse") in ("grass", "meadow"):
        return t["landuse"]
    return None


def building_type(t):
    if t.get("amenity") == "shelter" or t.get("building") in ("roof", "carport") or "shelter" in t.get("name", "").lower():
        return "shelter"
    if t.get("amenity") == "toilets" or t.get("building") == "toilets":
        return "toilets"
    if t.get("building") in ("house", "residential", "detached", "garage", "garages", "shed"):
        return t["building"]
    return "building"


def main():
    data = None
    for host in HOSTS:
        try:
            req = urllib.request.Request(host, data=urllib.parse.urlencode({"data": QUERY}).encode(), headers={"User-Agent": "stony-creek-unity feature fetch"})
            with urllib.request.urlopen(req, timeout=180) as r:
                data = json.loads(r.read())
            break
        except Exception as e:  # try the next mirror
            print(host, "failed:", e, file=sys.stderr)
    if data is None:
        sys.exit("All Overpass mirrors failed")

    out = []
    for e in data["elements"]:
        t = e.get("tags", {})
        kind = kind_of(t)
        if not kind:
            continue
        rings = []
        if e["type"] == "way" and "geometry" in e:
            rings.append(e["geometry"])
        elif e["type"] == "relation":
            rings += [m["geometry"] for m in e.get("members", []) if m.get("role") == "outer" and "geometry" in m]
        for g in rings:
            coords = [[round(p["lon"], 7), round(p["lat"], 7)] for p in g]
            if len(coords) < 4 or coords[0] != coords[-1]:
                continue  # open way: not an area
            f = {"kind": kind, "name": t.get("name", ""), "coords": coords}
            if kind == "building":
                f["type"] = building_type(t)
                if t.get("building:levels", "").isdigit():
                    f["levels"] = int(t["building:levels"])
            out.append(f)
    counts = {}
    for f in out:
        counts[f["kind"]] = counts.get(f["kind"], 0) + 1
    (ROOT / "Data" / "stony-creek-features.json").write_text(json.dumps({"source": "(c) OpenStreetMap contributors, Open Database License", "features": out}, separators=(",", ":")), encoding="utf-8")
    print(counts)


if __name__ == "__main__":
    main()
