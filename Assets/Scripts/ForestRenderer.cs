using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace StonyCreek
{
    /// Draws ~240k placed trees with GPU instancing: full meshes close up, impostor billboards out to the fog.
    public class ForestRenderer : MonoBehaviour
    {
        [System.Serializable]
        public class Species
        {
            public string name;
            public Mesh mesh;            // submesh 0 bark, 1 leaves
            public Material bark, leaves;
            public Material billboard;
            public Mesh billboardMesh;   // crossed quads sized to this species
        }

        public Species[] species;
        public Terrain terrain;
        public float nearDistance = 80f;
        public float fadeBand = 14f;
        public float farDistance = 1150f;
        public float[] trunkRadius = { 0.4f, 0.34f, 0.2f, 0.32f };

        const float CellSize = 64f;
        int cols, rows;
        List<Matrix4x4>[][] cells;      // [cell][species]
        readonly List<Matrix4x4>[] nearList = new List<Matrix4x4>[4];
        readonly List<Matrix4x4>[] farList = new List<Matrix4x4>[4];
        Vector3 lastNearPos = new(1e9f, 0, 0), lastFarPos = new(1e9f, 0, 0);
        float lastYaw = 999;
        public int TreeCount { get; private set; }
        public int NearDrawn { get; private set; }
        public int FarDrawn { get; private set; }

        public void Init(ParkData data)
        {
            var td = terrain.terrainData;
            cols = Mathf.CeilToInt(data.info.width / CellSize);
            rows = Mathf.CeilToInt(data.info.depth / CellSize);
            cells = new List<Matrix4x4>[cols * rows][];
            for (int s = 0; s < 4; s++) { nearList[s] = new List<Matrix4x4>(2048); farList[s] = new List<Matrix4x4>(16384); }

            var bytes = Resources.Load<TextAsset>("World/trees").bytes;
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32();
            TreeCount = count;
            float w = data.info.width, d = data.info.depth;
            for (int i = 0; i < count; i++)
            {
                float x = r.ReadUInt16() / 65535f * w, z = r.ReadUInt16() / 65535f * d;
                byte sb = r.ReadByte(), rb = r.ReadByte();
                int sp = Mathf.Min(sb >> 5, species.Length - 1);
                float scale = 0.6f + (sb & 31) / 31f * 0.8f;
                float y = td.GetInterpolatedHeight(x / w, z / d) - 0.25f * scale;
                var m = Matrix4x4.TRS(new Vector3(x, y, z), Quaternion.Euler(0, rb / 256f * 360f, 0), Vector3.one * scale);
                int c = Mathf.Min((int)(x / CellSize), cols - 1) + Mathf.Min((int)(z / CellSize), rows - 1) * cols;
                cells[c] ??= new List<Matrix4x4>[4];
                (cells[c][sp] ??= new List<Matrix4x4>()).Add(m);
            }
            foreach (var s in species)
            {
                s.bark.SetVector("_LodFade", new Vector4(nearDistance - fadeBand, nearDistance, 0, 0));
                s.leaves.SetVector("_LodFade", new Vector4(nearDistance - fadeBand, nearDistance, 0, 0));
                s.billboard.SetVector("_LodFade", new Vector4(nearDistance - fadeBand, nearDistance, 0, 0));
            }
        }

        public void Refresh() { lastNearPos = lastFarPos = new Vector3(1e9f, 0, 0); }

        /// Keeps the walker out of tree trunks.
        public Vector2 PushOut(Vector2 p, float radius)
        {
            if (cells == null) return p;
            int cx = (int)(p.x / CellSize), cz = (int)(p.y / CellSize);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= cols || z >= rows) continue;
                    var cell = cells[x + z * cols];
                    if (cell == null) continue;
                    for (int s = 0; s < 4; s++)
                    {
                        if (cell[s] == null) continue;
                        foreach (var m in cell[s])
                        {
                            var t = new Vector2(m.m03, m.m23);
                            float r = radius + trunkRadius[Mathf.Min(s, trunkRadius.Length - 1)] * m.m11;
                            var d = p - t;
                            float dd = d.sqrMagnitude;
                            if (dd < r * r && dd > 1e-6f) p = t + d / Mathf.Sqrt(dd) * r;
                        }
                    }
                }
            return p;
        }

        void Collect(Vector3 cam, float minD, float maxD, List<Matrix4x4>[] into, Plane[] frustum)
        {
            for (int s = 0; s < 4; s++) into[s].Clear();
            int c0 = Mathf.Max(0, (int)((cam.x - maxD) / CellSize)), c1 = Mathf.Min(cols - 1, (int)((cam.x + maxD) / CellSize));
            int r0 = Mathf.Max(0, (int)((cam.z - maxD) / CellSize)), r1 = Mathf.Min(rows - 1, (int)((cam.z + maxD) / CellSize));
            float maxSq = (maxD + CellSize) * (maxD + CellSize);
            for (int rz = r0; rz <= r1; rz++)
                for (int cx = c0; cx <= c1; cx++)
                {
                    var cell = cells[cx + rz * cols];
                    if (cell == null) continue;
                    float ccx = (cx + 0.5f) * CellSize - cam.x, ccz = (rz + 0.5f) * CellSize - cam.z;
                    float dd = ccx * ccx + ccz * ccz;
                    if (dd > maxSq) continue;
                    float dist = Mathf.Sqrt(dd);
                    if (dist + CellSize * 0.71f < minD) continue;
                    for (int s = 0; s < 4; s++)
                    {
                        var list = cell[s];
                        if (list == null) continue;
                        if (frustum == null) { into[s].AddRange(list); continue; }
                        foreach (var m in list)
                        {
                            var p = new Vector3(m.m03, m.m13, m.m23);
                            float tx = p.x - cam.x, tz = p.z - cam.z;
                            if (tx * tx + tz * tz > maxD * maxD) continue;
                            if (!GeometryUtility.TestPlanesAABB(frustum, new Bounds(p + Vector3.up * 9, new Vector3(16, 22, 16)))) continue;
                            into[s].Add(m);
                        }
                    }
                }
        }

        void LateUpdate()
        {
            if (cells == null) return;
            var camera = Camera.main;
            var cam = camera.transform.position;
            float yaw = camera.transform.eulerAngles.y;

            if ((cam - lastNearPos).sqrMagnitude > 9f || Mathf.Abs(Mathf.DeltaAngle(yaw, lastYaw)) > 12f)
            {
                // a widened frustum so small turns don't need a rebuild
                float fov = camera.fieldOfView;
                camera.fieldOfView = Mathf.Min(fov + 34f, 150f);
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                camera.fieldOfView = fov;
                Collect(cam, 0, nearDistance, nearList, planes);
                lastNearPos = cam; lastYaw = yaw;
            }
            if ((cam - lastFarPos).sqrMagnitude > 100f)
            {
                Collect(cam, nearDistance - fadeBand, farDistance, farList, null);
                lastFarPos = cam;
            }

            int near = 0, far = 0;
            for (int s = 0; s < species.Length; s++)
            {
                var sp = species[s];
                if (nearList[s].Count > 0)
                {
                    var bark = new RenderParams(sp.bark) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, worldBounds = new Bounds(cam, Vector3.one * 4000) };
                    var leaves = new RenderParams(sp.leaves) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, worldBounds = bark.worldBounds };
                    Graphics.RenderMeshInstanced(bark, sp.mesh, 0, nearList[s]);
                    Graphics.RenderMeshInstanced(leaves, sp.mesh, 1, nearList[s]);
                    near += nearList[s].Count;
                }
                if (farList[s].Count > 0)
                {
                    var bb = new RenderParams(sp.billboard) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true, worldBounds = new Bounds(cam, Vector3.one * 4000) };
                    Graphics.RenderMeshInstanced(bb, sp.billboardMesh, 0, farList[s]);
                    far += farList[s].Count;
                }
            }
            NearDrawn = near; FarDrawn = far;
        }
    }
}
