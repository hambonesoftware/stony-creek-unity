using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace StonyCreek.EditorTools
{
    /// Builds the whole Stony Creek scene from the baked data: terrain, trails, lakes, tree models, sky.
    /// Run Tools/prepare_world.py first. Menu: Stony Creek > Build World.
    public static class StonyWorldBuilder
    {
        public const string ScenePath = "Assets/Scenes/StonyCreek.unity";
        const string Gen = "Assets/Generated";
        const string Tex = "Assets/Textures";

        [MenuItem("Stony Creek/Build World")]
        public static void Build()
        {
            Directory.CreateDirectory(Gen);
            Directory.CreateDirectory("Assets/Scenes");
            ConfigureTextures();

            var info = JsonUtility.FromJson<WorldInfo>(File.ReadAllText("Assets/Resources/World/world.json"));
            var mats = MakeMaterials(info);
            var species = MakeTrees(mats);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var terrain = MakeTerrain(info, mats);
            MakeLakes(terrain, mats);
            MakePaths(terrain, mats);
            MakeLotsAndBuildings(terrain, mats);
            Atmosphere(out var sun);

            var camGo = new GameObject("Walker");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 62; cam.nearClipPlane = 0.1f; cam.farClipPlane = 3000; cam.allowHDR = false; cam.allowMSAA = true;
            cam.clearFlags = CameraClearFlags.Skybox;
            var walker = camGo.AddComponent<Walker>();
            walker.terrain = terrain;

            var root = new GameObject("Explorer");
            var forest = root.AddComponent<ForestRenderer>();
            forest.terrain = terrain; forest.species = species;
            var grass = root.AddComponent<GrassField>();
            grass.terrain = terrain;
            grass.grassMesh = SaveMesh(TreeMaker.Grass(), "grass_clump");
            grass.shrubMesh = SaveMesh(TreeMaker.Shrub(), "shrub");
            grass.grassMaterial = mats["grass"]; grass.shrubMaterial = mats["shrub"];
            var hud = root.AddComponent<MapHUD>();
            hud.mapView = mats["mapView"]; hud.overlay = mats["mapOverlay"];
            hud.walker = walker;
            var explorer = root.AddComponent<StonyExplorer>();
            explorer.terrain = terrain; explorer.walker = walker; explorer.forest = forest; explorer.grass = grass; explorer.hud = hud; explorer.sun = sun;
            hud.explorer = explorer;
            walker.forest = forest; walker.hud = hud;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Quality();
            AssetDatabase.SaveAssets();
            Debug.Log("Stony Creek world built.");
        }

        // ---------------- textures ----------------
        static void Import(string path, System.Action<TextureImporter> set)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) { Debug.LogWarning("Missing texture " + path); return; }
            set(imp);
            imp.SaveAndReimport();
        }

        static void ConfigureTextures()
        {
            AssetDatabase.Refresh();
            foreach (var f in Directory.GetFiles(Tex + "/Ground", "*.jpg"))
            {
                bool normal = f.EndsWith("_n.jpg");
                Import(f.Replace('\\', '/'), i => { i.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default; i.maxTextureSize = 1024; i.anisoLevel = 4; i.wrapMode = TextureWrapMode.Repeat; i.mipmapEnabled = true; });
            }
            foreach (var n in new[] { "oak", "maple", "birch", "pine" })
                Import($"{Tex}/Foliage/{n}.png", Leafy);
            foreach (var n in new[] { "bark", "birch_bark" })
                Import($"{Tex}/Foliage/{n}.png", i => { i.maxTextureSize = 1024; i.wrapMode = TextureWrapMode.Repeat; i.anisoLevel = 2; });
            Import($"{Tex}/Generated/grass_blades.png", Leafy);
            Import($"{Tex}/Generated/water_normal.png", i => { i.textureType = TextureImporterType.NormalMap; i.wrapMode = TextureWrapMode.Repeat; });
            Import($"{Tex}/Generated/ground_variation.png", i => { i.sRGBTexture = false; i.wrapMode = TextureWrapMode.Repeat; i.textureCompression = TextureImporterCompression.Uncompressed; });
            Import($"{Tex}/Generated/macro.png", i => { i.wrapMode = TextureWrapMode.Clamp; i.maxTextureSize = 1024; });
            Import("Assets/Resources/World/parkmap.png", i =>
            {
                i.maxTextureSize = 4096; i.npotScale = TextureImporterNPOTScale.None; i.wrapMode = TextureWrapMode.Clamp;
                i.mipmapEnabled = true; i.filterMode = FilterMode.Trilinear; i.anisoLevel = 2; i.textureCompression = TextureImporterCompression.CompressedHQ;
            });
        }

        static void Leafy(TextureImporter i)
        {
            i.alphaIsTransparency = true; i.mipMapsPreserveCoverage = true; i.alphaTestReferenceValue = 0.42f;
            i.wrapMode = TextureWrapMode.Clamp; i.maxTextureSize = 1024; i.mipmapEnabled = true;
        }

        static T Load<T>(string path) where T : Object
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a == null) throw new System.Exception("Missing asset " + path);
            return a;
        }

        static Material SaveMat(Material m, string name)
        {
            string path = $"{Gen}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Mesh SaveMesh(Mesh m, string name)
        {
            string path = $"{Gen}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material Mat(string shader, string name, System.Action<Material> set = null)
        {
            var s = Shader.Find(shader);
            if (s == null) throw new System.Exception("Shader not found: " + shader);
            var m = new Material(s) { name = name, enableInstancing = true };
            set?.Invoke(m);
            return SaveMat(m, name);
        }

        static Dictionary<string, Material> MakeMaterials(WorldInfo info)
        {
            var variation = Load<Texture2D>($"{Tex}/Generated/ground_variation.png");
            var d = new Dictionary<string, Material>();
            d["terrain"] = Mat("Stony/Terrain", "Terrain", m =>
            {
                m.SetTexture("_Macro", Load<Texture2D>($"{Tex}/Generated/macro.png"));
                m.SetTexture("_Variation", variation);
                m.SetVector("_WorldSize", new Vector4(info.width, info.depth, 0, 0));
            });
            Material Bark(string name, string tex, string normal, Color c) => Mat("Stony/Bark", name, m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>(tex));
                m.SetTexture("_BumpMap", Load<Texture2D>(normal));
                m.SetColor("_Color", c);
            });
            d["bark_oak"] = Bark("BarkOak", $"{Tex}/Ground/bark_oak.jpg", $"{Tex}/Ground/bark_oak_n.jpg", new Color(0.82f, 0.8f, 0.78f));
            d["bark_maple"] = Bark("BarkMaple", $"{Tex}/Foliage/bark.png", $"{Tex}/Ground/bark_oak_n.jpg", new Color(0.9f, 0.88f, 0.86f));
            d["bark_birch"] = Bark("BarkBirch", $"{Tex}/Foliage/birch_bark.png", $"{Tex}/Ground/bark_oak_n.jpg", new Color(1f, 1f, 1f));
            d["bark_pine"] = Bark("BarkPine", $"{Tex}/Ground/bark_pine.jpg", $"{Tex}/Ground/bark_pine_n.jpg", new Color(0.85f, 0.82f, 0.8f));
            foreach (var n in new[] { "oak", "maple", "birch", "pine" })
                d["leaf_" + n] = Mat("Stony/Leaves", "Leaves_" + n, m =>
                {
                    m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Foliage/{n}.png"));
                    if (n == "pine") { m.SetFloat("_Flutter", 0.015f); m.SetFloat("_Translucency", 0.35f); }
                    if (n == "birch") { m.SetFloat("_Flutter", 0.08f); }
                });
            d["shrub"] = Mat("Stony/Leaves", "Shrub", m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Foliage/maple.png"));
                m.SetColor("_Color", new Color(0.78f, 0.86f, 0.7f));
                m.SetFloat("_SwayScale", 0.25f);
            });
            d["grass"] = Mat("Stony/Grass", "Grass", m => { m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Generated/grass_blades.png")); m.SetColor("_Color", new Color(0.9f, 1.06f, 0.78f)); });
            d["water"] = Mat("Stony/Water", "Water", m => m.SetTexture("_BumpMap", Load<Texture2D>($"{Tex}/Generated/water_normal.png")));
            d["trail"] = Mat("Stony/Path", "Trail", m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Ground/trail.jpg")); m.SetTexture("_BumpMap", Load<Texture2D>($"{Tex}/Ground/trail_n.jpg"));
                m.SetTexture("_Variation", variation); m.SetColor("_Color", new Color(1.22f, 1.08f, 0.9f));
            });
            d["paved"] = Mat("Stony/Path", "Paved", m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Ground/road.jpg")); m.SetTexture("_BumpMap", Load<Texture2D>($"{Tex}/Ground/road_n.jpg"));
                m.SetTexture("_Variation", variation); m.SetFloat("_Edge", 0.06f); m.SetColor("_Color", new Color(0.95f, 0.95f, 0.93f));
            });
            d["bike"] = Mat("Stony/Path", "BikeTrail", m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Ground/road.jpg")); m.SetTexture("_BumpMap", Load<Texture2D>($"{Tex}/Ground/road_n.jpg"));
                m.SetTexture("_Variation", variation); m.SetFloat("_Edge", 0.08f); m.SetColor("_Color", new Color(1.45f, 1.42f, 1.36f));
            });
            d["lot"] = Mat("Stony/Path", "ParkingLot", m =>
            {
                m.SetTexture("_MainTex", Load<Texture2D>($"{Tex}/Ground/road.jpg")); m.SetTexture("_BumpMap", Load<Texture2D>($"{Tex}/Ground/road_n.jpg"));
                m.SetTexture("_Variation", variation); m.SetColor("_Color", new Color(0.9f, 0.9f, 0.9f));
                m.renderQueue = 1950;   // after the terrain, before the trails and roads that cross the lots
            });
            d["building"] = Mat("Stony/Building", "Building", m => m.SetTexture("_Variation", variation));
            d["mapView"] = Mat("Stony/MapView", "MapView");
            d["mapOverlay"] = Mat("Stony/MapOverlay", "MapOverlay");
            return d;
        }

        // ---------------- trees ----------------
        static ForestRenderer.Species[] MakeTrees(Dictionary<string, Material> mats)
        {
            var list = new List<ForestRenderer.Species>();
            var bakeShader = Shader.Find("Hidden/Stony/BakeAlbedo");
            for (int i = 0; i < TreeMaker.Specs.Length; i++)
            {
                var spec = TreeMaker.Specs[i];
                var mesh = SaveMesh(TreeMaker.Build(spec, 1234 + i * 77, out float crownY, out float crownR), "tree_" + spec.name);
                var bounds = mesh.bounds;
                float width = Mathf.Max(bounds.size.x, bounds.size.z) * 1.02f;
                float height = bounds.max.y + 0.4f;
                float side = Mathf.Max(width, height);
                var bbMesh = SaveMesh(TreeMaker.Billboard("billboard_" + spec.name, side, side), "billboard_" + spec.name);
                var barkMat = mats["bark_" + spec.name];
                var leafMat = mats["leaf_" + spec.name];
                var impostor = BakeImpostor(mesh, barkMat, leafMat, bakeShader, side, spec.name);
                var bbMat = Mat("Stony/TreeBillboard", "Billboard_" + spec.name, m =>
                {
                    m.SetTexture("_MainTex", impostor);
                    m.SetFloat("_CrownCenter", crownY);
                });
                list.Add(new ForestRenderer.Species { name = spec.name, mesh = mesh, bark = barkMat, leaves = leafMat, billboard = bbMat, billboardMesh = bbMesh });
            }
            return list.ToArray();
        }

        static Texture2D BakeImpostor(Mesh mesh, Material bark, Material leaves, Shader bakeShader, float side, string name)
        {
            const int Size = 512;
            const int Layer = 31;
            var go = new GameObject("bake_" + name) { layer = Layer, hideFlags = HideFlags.HideAndDontSave };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mb = new Material(bakeShader); mb.SetTexture("_MainTex", bark.GetTexture("_MainTex")); mb.SetFloat("_Cutoff", -1); mb.SetColor("_Color", bark.GetColor("_Color"));
            var ml = new Material(bakeShader); ml.SetTexture("_MainTex", leaves.GetTexture("_MainTex")); ml.SetFloat("_Cutoff", 0.42f);
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { mb, ml };
            var camGo = new GameObject("bake_cam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = side / 2; cam.aspect = 1;
            cam.transform.position = new Vector3(0, side / 2 - 0.3f, -60); cam.transform.rotation = Quaternion.identity;
            cam.nearClipPlane = 1; cam.farClipPlane = 120; cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.2f, 0.26f, 0.12f, 0);
            cam.enabled = false;
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(camGo); Object.DestroyImmediate(go);
            string path = $"{Gen}/impostor_{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            Import(path, Leafy);
            return Load<Texture2D>(path);
        }

        // ---------------- terrain ----------------
        static Terrain MakeTerrain(WorldInfo info, Dictionary<string, Material> mats)
        {
            const int Res = 1025;
            var raw = File.ReadAllBytes("Data/terrain_heights.r16");
            var heights = new float[Res, Res];
            for (int y = 0; y < Res; y++)
                for (int x = 0; x < Res; x++)
                    heights[y, x] = (raw[(y * Res + x) * 2] | raw[(y * Res + x) * 2 + 1] << 8) / 65535f;

            var td = new TerrainData { heightmapResolution = Res };
            // The asset must exist before layers and alphamaps are set: Unity stores the painted
            // splat textures as sub-assets and silently drops them for an unsaved TerrainData.
            string tdPath = $"{Gen}/StonyTerrain.asset";
            AssetDatabase.DeleteAsset(tdPath);
            AssetDatabase.CreateAsset(td, tdPath);
            td.size = new Vector3(info.width, info.terrainHeight, info.depth);
            td.SetHeights(0, 0, heights);

            TerrainLayer Layer(string name, float tile)
            {
                var l = new TerrainLayer
                {
                    diffuseTexture = Load<Texture2D>($"{Tex}/Ground/{name}.jpg"),
                    normalMapTexture = Load<Texture2D>($"{Tex}/Ground/{name}_n.jpg"),
                    tileSize = new Vector2(tile, tile),
                };
                string p = $"{Gen}/layer_{name}.terrainlayer";
                AssetDatabase.DeleteAsset(p); AssetDatabase.CreateAsset(l, p);
                return l;
            }
            td.terrainLayers = new[] { Layer("lawn", 4.5f), Layer("meadow", 5f), Layer("forest_floor", 4f), Layer("sand", 4f) };

            const int A = 2048;
            td.alphamapResolution = A;
            var splatTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            splatTex.LoadImage(File.ReadAllBytes("Data/splat.png"));
            var px = splatTex.GetPixels32(); // row 0 = bottom (south) after LoadImage
            var alpha = new float[A, A, 4];
            for (int y = 0; y < A; y++)
                for (int x = 0; x < A; x++)
                {
                    var c = px[y * A + x];
                    float sum = Mathf.Max(1, c.r + c.g + c.b + c.a);
                    alpha[y, x, 0] = c.r / sum; alpha[y, x, 1] = c.g / sum; alpha[y, x, 2] = c.b / sum; alpha[y, x, 3] = c.a / sum;
                }
            td.SetAlphamaps(0, 0, alpha);
            Object.DestroyImmediate(splatTex);
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();
            if (td.alphamapTextureCount < 1) throw new System.Exception("Terrain paint was not stored");

            var go = Terrain.CreateTerrainGameObject(td);
            go.name = "Terrain";
            var t = go.GetComponent<Terrain>();
            t.materialTemplate = mats["terrain"];
            t.heightmapPixelError = 3;
            t.basemapDistance = 20000;
            t.drawTreesAndFoliage = false;
            t.shadowCastingMode = ShadowCastingMode.On;
            var col = go.GetComponent<TerrainCollider>();
            if (col) Object.DestroyImmediate(col);
            return t;
        }

        static float H(TerrainData td, float x, float z) => td.GetInterpolatedHeight(x / td.size.x, z / td.size.z);

        // ---------------- lakes ----------------
        static void MakeLakes(Terrain terrain, Dictionary<string, Material> mats)
        {
            var bytes = File.ReadAllBytes("Assets/Resources/World/lakes.bytes");
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32();
            var v = new List<Vector3>(); var tri = new List<int>();
            for (int i = 0; i < count; i++)
            {
                float level = r.ReadSingle(); int n = r.ReadInt32();
                var pts = new List<Vector2>(n);
                for (int k = 0; k < n; k++) pts.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                if (pts.Count > 3 && pts[0] == pts[pts.Count - 1]) pts.RemoveAt(pts.Count - 1);
                var idx = EarClip(pts);
                int b = v.Count;
                foreach (var p in pts) v.Add(new Vector3(p.x, level + 0.02f, p.y));
                foreach (int k in idx) tri.Add(b + k);
            }
            var mesh = new Mesh { name = "lakes", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v); mesh.SetTriangles(tri, 0);
            var normals = new Vector3[v.Count]; var tangents = new Vector4[v.Count];
            for (int i = 0; i < v.Count; i++) { normals[i] = Vector3.up; tangents[i] = new Vector4(1, 0, 0, -1); }
            mesh.normals = normals; mesh.tangents = tangents;
            mesh.RecalculateBounds();
            SaveMesh(mesh, "lakes");
            var go = new GameObject("Lakes");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mats["water"]; mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// Ear clipping for simple polygons (lake outlines). Winding is normalised so faces point up.
        internal static List<int> EarClip(List<Vector2> pts)
        {
            var result = new List<int>();
            int n = pts.Count;
            if (n < 3) return result;
            float area = 0;
            for (int i = 0, j = n - 1; i < n; j = i++) area += pts[j].x * pts[i].y - pts[i].x * pts[j].y;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(area > 0 ? i : n - 1 - i);   // counter-clockwise in x/z
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    var pa = pts[a]; var pb = pts[b]; var pc = pts[c];
                    float cross = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
                    if (cross <= 1e-6f) continue;
                    bool inside = false;
                    for (int k = 0; k < idx.Count && !inside; k++)
                    {
                        int q = idx[k]; if (q == a || q == b || q == c) continue;
                        inside = InTri(pts[q], pa, pb, pc);
                    }
                    if (inside) continue;
                    // x/z counter-clockwise seen from above needs clockwise index order for an upward face in Unity
                    result.Add(a); result.Add(c); result.Add(b);
                    idx.RemoveAt(i); clipped = true; break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) { result.Add(idx[0]); result.Add(idx[2]); result.Add(idx[1]); }
            return result;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // ---------------- trails and roads ----------------
        static void MakePaths(Terrain terrain, Dictionary<string, Material> mats)
        {
            var td = terrain.terrainData;
            var bytes = File.ReadAllBytes("Assets/Resources/World/paths.bytes");
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32(), blob = r.ReadInt32(); r.ReadBytes(blob);
            const float Chunk = 640f;
            var chunks = new Dictionary<(string, int, int), (List<Vector3> v, List<Vector2> uv, List<int> t)>();
            var root = new GameObject("Trails");
            for (int i = 0; i < count; i++)
            {
                var kind = (PathKind)r.ReadByte(); r.ReadUInt16(); int n = r.ReadUInt16();
                var pts = new List<Vector2>(n);
                for (int k = 0; k < n; k++) pts.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                string mat = kind switch { PathKind.Service or PathKind.Residential => "paved", PathKind.Cycleway => "bike", _ => "trail" };
                float width = kind switch { PathKind.Residential => 6.2f, PathKind.Service => 4.8f, PathKind.Cycleway => 3.2f, PathKind.Track => 3.1f, PathKind.Footway => 2.3f, _ => 2.0f };
                // resample to at most 3 m
                var dense = new List<Vector2> { pts[0] };
                for (int k = 1; k < pts.Count; k++)
                {
                    float d = Vector2.Distance(pts[k - 1], pts[k]);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(d / 3f));
                    for (int s = 1; s <= steps; s++) dense.Add(Vector2.Lerp(pts[k - 1], pts[k], s / (float)steps));
                }
                if (dense.Count < 2) continue;
                var mid = dense[dense.Count / 2];
                var key = (mat, (int)(mid.x / Chunk), (int)(mid.y / Chunk));
                if (!chunks.TryGetValue(key, out var ch)) chunks[key] = ch = (new List<Vector3>(), new List<Vector2>(), new List<int>());
                int b = ch.v.Count; float along = 0;
                for (int k = 0; k < dense.Count; k++)
                {
                    var p = dense[k];
                    var dir = (dense[Mathf.Min(k + 1, dense.Count - 1)] - dense[Mathf.Max(k - 1, 0)]).normalized;
                    var nrm = new Vector2(-dir.y, dir.x) * width * 0.5f;
                    if (k > 0) along += Vector2.Distance(dense[k - 1], p);
                    var L = p + nrm; var R2 = p - nrm;
                    ch.v.Add(new Vector3(L.x, H(td, L.x, L.y) + 0.06f, L.y));
                    ch.v.Add(new Vector3(R2.x, H(td, R2.x, R2.y) + 0.06f, R2.y));
                    ch.uv.Add(new Vector2(0, along / 3f)); ch.uv.Add(new Vector2(1, along / 3f));
                    if (k > 0)
                    {
                        int a0 = b + (k - 1) * 2;
                        ch.t.AddRange(new[] { a0, a0 + 2, a0 + 1, a0 + 1, a0 + 2, a0 + 3 });
                    }
                }
            }
            int meshIndex = 0;
            var all = new List<Mesh>();
            foreach (var kv in chunks)
            {
                var (mat, cx, cz) = kv.Key; var ch = kv.Value;
                var m = new Mesh { name = $"path_{mat}_{cx}_{cz}", indexFormat = IndexFormat.UInt32 };
                m.SetVertices(ch.v); m.SetUVs(0, ch.uv); m.SetTriangles(ch.t, 0);
                var normals = new Vector3[ch.v.Count]; var tangents = new Vector4[ch.v.Count];
                for (int i = 0; i < normals.Length; i++) { normals[i] = Vector3.up; tangents[i] = new Vector4(1, 0, 0, -1); }
                m.normals = normals; m.tangents = tangents;
                m.RecalculateBounds();
                all.Add(m);
                var go = new GameObject(m.name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mats[mat]; mr.shadowCastingMode = ShadowCastingMode.Off;
                meshIndex++;
            }
            string path = $"{Gen}/paths.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(all[0], path);
            for (int i = 1; i < all.Count; i++) AssetDatabase.AddObjectToAsset(all[i], path);
        }

        // ---------------- parking lots and buildings from mapped outlines ----------------
        static void MakeLotsAndBuildings(Terrain terrain, Dictionary<string, Material> mats)
        {
            var td = terrain.terrainData;
            var outlines = ParkData.ReadOutlines(File.ReadAllBytes("Assets/Resources/World/features.bytes"));

            // parking: flat paved polygons on the graded terrain
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            foreach (var o in outlines)
            {
                if (o.kind != 3) continue;
                var pts = new List<Vector2>(o.pts);
                var idx = EarClip(pts);
                int b = v.Count;
                foreach (var p in pts) { v.Add(new Vector3(p.x, H(td, p.x, p.y) + 0.07f, p.y)); uv.Add(new Vector2(0.5f, 0)); }
                foreach (int k in idx) tri.Add(b + k);
            }
            var lots = new Mesh { name = "parking_lots", indexFormat = IndexFormat.UInt32 };
            lots.SetVertices(v); lots.SetUVs(0, uv); lots.SetTriangles(tri, 0);
            var ln = new Vector3[v.Count]; var lt = new Vector4[v.Count];
            for (int i = 0; i < ln.Length; i++) { ln[i] = Vector3.up; lt[i] = new Vector4(1, 0, 0, -1); }
            lots.normals = ln; lots.tangents = lt; lots.RecalculateBounds();
            SaveMesh(lots, "parking_lots");
            var lotGo = new GameObject("Parking lots");
            lotGo.AddComponent<MeshFilter>().sharedMesh = lots;
            var lr = lotGo.AddComponent<MeshRenderer>(); lr.sharedMaterial = mats["lot"]; lr.shadowCastingMode = ShadowCastingMode.Off;

            // buildings, grouped into chunks so far ones are culled
            const float Chunk = 640f;
            var chunks = new Dictionary<(int, int), BuildingMaker.Geo>();
            var rng = new System.Random(4242);
            int count = 0;
            foreach (var o in outlines)
            {
                if (o.kind != 4) continue;
                var key = ((int)(o.bounds.center.x / Chunk), (int)(o.bounds.center.y / Chunk));
                if (!chunks.TryGetValue(key, out var geo)) chunks[key] = geo = new BuildingMaker.Geo();
                BuildingMaker.Add(geo, new List<Vector2>(o.pts), o.type, o.levels, (x, z) => H(td, x, z), rng);
                count++;
            }
            var root = new GameObject("Buildings");
            var all = new List<Mesh>();
            foreach (var kv in chunks)
            {
                var geo = kv.Value;
                if (geo.t.Count == 0) continue;
                var m = new Mesh { name = $"buildings_{kv.Key.Item1}_{kv.Key.Item2}", indexFormat = IndexFormat.UInt32 };
                m.SetVertices(geo.v); m.SetNormals(geo.n); m.SetUVs(0, geo.uv); m.SetColors(geo.c); m.SetTriangles(geo.t, 0);
                m.RecalculateBounds();
                all.Add(m);
                var go = new GameObject(m.name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = mats["building"];
            }
            string path = $"{Gen}/buildings.asset";
            AssetDatabase.DeleteAsset(path);
            if (all.Count > 0)
            {
                AssetDatabase.CreateAsset(all[0], path);
                for (int i = 1; i < all.Count; i++) AssetDatabase.AddObjectToAsset(all[i], path);
            }
            Debug.Log($"Stony Creek: {count} buildings, {tri.Count / 3} parking triangles.");
        }

        // ---------------- sky, sun, fog ----------------
        static void Atmosphere(out Light sun)
        {
            var sunGo = new GameObject("Sun");
            sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.94f, 0.84f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.78f;
            sun.shadowBias = 0.06f; sun.shadowNormalBias = 0.5f;
            sunGo.transform.rotation = Quaternion.Euler(38, 32, 0);

            var sky = new Material(Shader.Find("Skybox/Procedural")) { name = "Sky" };
            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_SunSizeConvergence", 6);
            sky.SetFloat("_AtmosphereThickness", 0.82f);
            sky.SetColor("_SkyTint", new Color(0.46f, 0.52f, 0.6f));
            sky.SetColor("_GroundColor", new Color(0.42f, 0.45f, 0.4f));
            sky.SetFloat("_Exposure", 1.2f);
            SaveMat(sky, "Sky");
            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.62f, 0.74f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.5f, 0.44f);
            RenderSettings.ambientGroundColor = new Color(0.24f, 0.23f, 0.18f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.00085f;
            RenderSettings.fogColor = new Color(0.66f, 0.73f, 0.78f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        }

        static void Quality()
        {
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowResolution = ShadowResolution.High;
                QualitySettings.shadowDistance = 70;
                QualitySettings.shadowCascades = 2;
                QualitySettings.shadowProjection = ShadowProjection.StableFit;
                QualitySettings.antiAliasing = 2;
                QualitySettings.pixelLightCount = 1;
                QualitySettings.vSyncCount = 0;
                QualitySettings.globalTextureMipmapLimit = 0;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            }
            QualitySettings.SetQualityLevel(current, false);
        }
    }
}
