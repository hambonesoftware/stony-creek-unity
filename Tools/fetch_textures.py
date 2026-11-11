"""Download the CC0 Poly Haven photo textures this project uses (1K diffuse + OpenGL normal).

Run from the project root:  python Tools/fetch_textures.py
Everything here is CC0 (https://polyhaven.com/license); sources are recorded in
Assets/Textures/Ground/SOURCES.md.
"""
import json, os, sys, urllib.request

TEXTURES = {
    # role            Poly Haven asset id
    "lawn":          "leafy_grass",
    "meadow":        "withered_grass",
    "forest_floor":  "forest_leaves_02",
    "trail":         "gravel_ground_01",
    "road":          "asphalt_02",
    "sand":          "park_sand",
    "bark_oak":      "bark_brown_02",
    "bark_pine":     "pine_bark",
}
OUT = os.path.join("Assets", "Textures", "Ground")
UA = {"User-Agent": "stony-creek-unity texture fetch"}


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60) as r:
        return r.read()


def main():
    os.makedirs(OUT, exist_ok=True)
    lines = ["# Ground and bark textures", "", "All from [Poly Haven](https://polyhaven.com), CC0 1.0.", ""]
    for role, asset in TEXTURES.items():
        files = json.loads(get(f"https://api.polyhaven.com/files/{asset}"))
        for key, suffix in (("Diffuse", ""), ("nor_gl", "_n")):
            entry = files.get(key, {}).get("1k", {}).get("jpg")
            if not entry:
                sys.exit(f"{asset}: no 1k {key} jpg")
            path = os.path.join(OUT, f"{role}{suffix}.jpg")
            if not os.path.exists(path):
                with open(path, "wb") as f:
                    f.write(get(entry["url"]))
            print(path, entry["size"])
        lines.append(f"- `{role}` — [{asset}](https://polyhaven.com/a/{asset})")
    with open(os.path.join(OUT, "SOURCES.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
