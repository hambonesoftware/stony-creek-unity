# Stony Creek Explorer — Unity

A first-person walk through **Stony Creek Metropark** (Oakland and Macomb counties, Michigan), built in Unity 6 for the web, with a GTA-style radar minimap, compass and full-screen map.

**[Play it in the browser](https://hambonesoftware.github.io/stony-creek-unity/)** — desktop Chrome, Edge, Firefox or Safari with WebGL 2. Phones work but are heavier going.

This is the Unity counterpart of the three.js explorer in [sunward](https://github.com/hambonesoftware/sunward). Both use the same real geography.

## Controls

| | Desktop | Touch |
| --- | --- | --- |
| Walk | WASD or arrow keys | Left thumb anywhere on the lower left |
| Look | Drag with the mouse, Q / E to turn | Drag on the right side |
| Jog | Hold Shift, or J / the WALK button | WALK / JOG button |
| Map | M or the MAP button, Esc closes | Tap the radar or MAP |
| Waypoint | Click the map, click it again to clear | Tap the map |
| Map zoom / pan | Scroll wheel, drag, + / − | Pinch, drag |
| Fast travel | "Travel to" list on the map | Same |
| Hide the help line | H | — |

The radar rotates with you, like GTA's. It shows the compass ring with N/E/S/W, your heading, the trail or road you're on, named places and your waypoint. A purple GPS route follows the real trail and road network to the waypoint and recalculates as you walk.

## What's real and what isn't

**Real data**
- **Elevation:** USGS 3DEP, about 6–9 m grid. It's the same raster sunward's deploy downloads.
- **Trails, roads, lakes, habitat outlines and named landmarks:** OpenStreetMap, © OpenStreetMap contributors (ODbL).
- **Where forest, meadow, wetland, lawn and suburbs are:** USGS Annual NLCD 2024 land cover at 30 m, from the Esri image service.

**Illustrative, not surveyed**
- **Tree positions:** the land cover says where woods are, but each of the roughly 240,000 trees is placed by a seeded generator. Species mixes follow sunward's per-area art direction.
- **Grass and shrubs:** generated around you from the land cover.
- **Buildings, benches, signs and fences:** not modelled.

**Art**
- **Ground and bark:** CC0 photo textures from [Poly Haven](https://polyhaven.com). The list is in `Assets/Textures/Ground/SOURCES.md`.
- **Leaf sprays and maple bark:** from the sunward project.
- **Tree models:** procedural (`Assets/Editor/TreeMaker.cs`): oak, maple, birch and eastern white pine, built from those leaf and bark textures. Their distant billboards are rendered from the full models at build time.

**Not for navigation.** OSM trails can be out of date, and the map isn't an authoritative trail-closure source. Check [Metroparks](https://www.metroparks.com/stony-creek-metropark/) before a real visit.

## How it's built

```
Tools/fetch_textures.py   downloads the Poly Haven CC0 textures
Tools/make_textures.py    grass blades, birch bark, water ripples, ground variation
Tools/prepare_world.py    projects the GIS data, carves lake beds, places trees, paints the map
Assets/Editor/            builds the terrain, trails, lakes, tree models and scene; batch web build
Assets/Scripts/           forest renderer, grass, walker, HUD/map, routing
Assets/Shaders/           terrain, leaves, bark, billboards, grass, water, trails, map UI
test/trial.mjs            browser trial on desktop and a touch phone (Playwright + installed Edge/Chrome)
```

To rebuild, with Unity **6000.3.28f1** and Web Build Support installed:

```bash
python Tools/fetch_textures.py
python Tools/make_textures.py
python Tools/prepare_world.py
"C:/Program Files/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod StonyCreek.EditorTools.StonyBuild.BuildWeb -logFile web-build.log
npm install
npm test
```

Leave out `-nographics`, because the tree billboards are rendered on the GPU during the build. The web build lands in `Builds/Web`. GitHub Pages serves the `gh-pages` branch, which holds a copy of that folder.

The `Data/` folder holds the inputs: the elevation raster and OSM extract from sunward's Pages site, and the NLCD tile.

### Swapping in Asset Store or SpeedTree trees

Each tree species is a `ForestRenderer.Species` with one mesh (submesh 0 bark, submesh 1 leaves), two materials, and a billboard material plus mesh.

Store models can replace the procedural ones, but **don't commit paid assets to this public repo**. Keep them in a gitignored folder and publish only the built player, which their licences allow.

The renderer draws with GPU instancing, so replacement materials must have instancing enabled. The wind uses vertex colour red as "flex", which other models won't have, so they'd need their own wind shader.
