using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StonyCreek
{
    /// Grass clumps and leafy understory generated around the walker from the land-cover map.
    public class GrassField : MonoBehaviour
    {
        public Mesh grassMesh, shrubMesh;
        public Material grassMaterial, shrubMaterial;
        public Terrain terrain;
        public float radius = 44f;
        public float densityScale = 1f;

        const float Cell = 6f;
        ParkData data;
        readonly Dictionary<long, (Matrix4x4[] grass, Matrix4x4[] shrubs)> cache = new();
        readonly List<Matrix4x4> grassList = new(8192), shrubList = new(1024);
        Vector3 lastPos = new(1e9f, 0, 0);
        bool pending;
        public int GrassDrawn => grassList.Count;

        public void Init(ParkData d) { data = d; }

        static uint Hash(int x, int z, uint salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ salt * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        // clumps per cell for short lawn, tall meadow and forest floor
        static (int grass, int shrubs, float tall) Density(int cls) => cls switch
        {
            41 or 42 or 43 => (5, 2, 0.8f),
            90 => (9, 2, 1.0f),
            52 or 71 or 81 or 95 => (26, 1, 1.35f),
            82 => (10, 0, 1.0f),
            21 or 22 => (14, 0, 0.55f),
            23 or 24 => (6, 0, 0.5f),
            _ => (0, 0, 1f)
        };

        (Matrix4x4[], Matrix4x4[]) Build(int cx, int cz)
        {
            var td = terrain.terrainData;
            float w = data.info.width, d = data.info.depth;
            float x0 = cx * Cell, z0 = cz * Cell;
            var grass = new List<Matrix4x4>(); var shrubs = new List<Matrix4x4>();
            var (ng, ns, tall) = Density(data.LandcoverAt(x0 + Cell / 2, z0 + Cell / 2));
            ng = Mathf.RoundToInt(ng * densityScale);
            uint h = Hash(cx, cz, 1);
            float Rand() { h = h * 1664525u + 1013904223u; return (h >> 8) / 16777216f; }
            for (int i = 0; i < ng + ns; i++)
            {
                float x = x0 + Rand() * Cell, z = z0 + Rand() * Cell;
                if (x < 1 || z < 1 || x > w - 1 || z > d - 1) continue;
                bool shrub = i >= ng;
                float clearance = data.PathDistance(new Vector2(x, z), out _, 12f);
                if (clearance < (shrub ? 2.2f : 0.15f)) continue;
                if (data.LakeAt(x, z) != null) continue;
                float y = td.GetInterpolatedHeight(x / w, z / d);
                float s = shrub ? 0.5f + Rand() * 0.6f : (0.55f + Rand() * 0.6f) * tall * (clearance < 1.2f ? 0.6f : 1f);
                var m = Matrix4x4.TRS(new Vector3(x, y - 0.03f, z), Quaternion.Euler(0, Rand() * 360f, 0), new Vector3(s, s * (shrub ? 1 : 0.8f + Rand() * 0.5f), s));
                (shrub ? shrubs : grass).Add(m);
            }
            return (grass.ToArray(), shrubs.ToArray());
        }

        void LateUpdate()
        {
            if (data == null) return;
            var cam = Camera.main.transform.position;
            if ((cam - lastPos).sqrMagnitude > 2.25f || pending)
            {
                lastPos = cam; pending = false;
                int budget = 28;
                grassList.Clear(); shrubList.Clear();
                int r = Mathf.CeilToInt(radius / Cell);
                int ccx = Mathf.FloorToInt(cam.x / Cell), ccz = Mathf.FloorToInt(cam.z / Cell);
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dz * dz > r * r) continue;
                        int cx = ccx + dx, cz = ccz + dz;
                        long key = ((long)cx << 32) ^ (uint)cz;
                        if (!cache.TryGetValue(key, out var cell))
                        {
                            // spread generation over frames so travelling never freezes the game
                            if (budget-- <= 0) { pending = true; continue; }
                            cache[key] = cell = Build(cx, cz);
                        }
                        grassList.AddRange(cell.grass); shrubList.AddRange(cell.shrubs);
                    }
                if (cache.Count > 6000) cache.Clear();
            }
            var bounds = new Bounds(cam, Vector3.one * (radius * 2 + 20));
            if (grassList.Count > 0)
                Graphics.RenderMeshInstanced(new RenderParams(grassMaterial) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true, worldBounds = bounds }, grassMesh, 0, grassList);
            if (shrubList.Count > 0)
                Graphics.RenderMeshInstanced(new RenderParams(shrubMaterial) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, worldBounds = bounds }, shrubMesh, 0, shrubList);
        }
    }
}
