using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace StonyCreek
{
    [Serializable] public class NamedPoint { public string name; public float x, z; }
    [Serializable] public class WorldInfo
    {
        public float width, depth, terrainHeight, datum, mapMetersPerPixel;
        public int mapWidth, mapHeight;
        public NamedPoint[] places, landmarks, parking, beaches;
        public string[] sources;
    }

    public enum PathKind : byte { Path, Footway, Cycleway, Track, Service, Residential }

    public class ParkPath
    {
        public PathKind kind; public string name; public Vector2[] pts;
        public bool IsRoad => kind == PathKind.Service || kind == PathKind.Residential;
        public float Width => kind switch
        {
            PathKind.Residential => 5.5f, PathKind.Service => 4.8f, PathKind.Cycleway => 3.2f,
            PathKind.Track => 3.1f, PathKind.Footway => 2.3f, _ => 2.1f
        };
    }

    public class Lake { public float level; public Vector2[] pts; public Rect bounds; }

    /// A mapped outline: kind 3 = parking lot, 4 = building (type 3 = open picnic shelter).
    public class Outline { public int kind, type, levels; public Vector2[] pts; public Rect bounds; }

    /// Real park geography baked by Tools/prepare_world.py (x east, z north, metres).
    public class ParkData
    {
        public WorldInfo info;
        public List<ParkPath> paths = new();
        public List<Lake> lakes = new();
        public byte[] landcover; public int lcW, lcH;
        public List<Outline> outlines = new();
        byte[] featureMask; int fmW, fmH; float fmMpp;
        public Texture2D map;

        // routing graph over shared OSM nodes
        public List<Vector2> nodes = new();
        public List<int>[] edges; public List<float>[] edgeLen;

        // spatial hash of path segments for "what trail am I on" and keeping plants off paths
        const float Cell = 40f;
        readonly Dictionary<long, List<(int path, int seg)>> segGrid = new();

        public static ParkData Load()
        {
            var d = new ParkData();
            d.info = JsonUtility.FromJson<WorldInfo>(Resources.Load<TextAsset>("World/world").text);
            d.ReadPaths(Resources.Load<TextAsset>("World/paths").bytes);
            d.ReadLakes(Resources.Load<TextAsset>("World/lakes").bytes);
            d.ReadLandcover(Resources.Load<TextAsset>("World/landcover").bytes);
            d.ReadFeatures(Resources.Load<TextAsset>("World/featuremask").bytes, Resources.Load<TextAsset>("World/features").bytes);
            d.map = Resources.Load<Texture2D>("World/parkmap");
            d.BuildGraph();
            return d;
        }

        void ReadPaths(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32(), blobLen = r.ReadInt32();
            var names = Encoding.UTF8.GetString(r.ReadBytes(blobLen)).Split('\n');
            for (int i = 0; i < count; i++)
            {
                var p = new ParkPath { kind = (PathKind)r.ReadByte() };
                int nameIndex = r.ReadUInt16(), n = r.ReadUInt16();
                p.name = nameIndex > 0 ? names[nameIndex - 1] : "";
                p.pts = new Vector2[n];
                for (int k = 0; k < n; k++) p.pts[k] = new Vector2(r.ReadSingle(), r.ReadSingle());
                paths.Add(p);
                for (int k = 1; k < n; k++) IndexSegment(paths.Count - 1, k);
            }
        }

        void ReadLakes(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var l = new Lake { level = r.ReadSingle() };
                int n = r.ReadInt32();
                l.pts = new Vector2[n];
                Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
                for (int k = 0; k < n; k++)
                {
                    var p = new Vector2(r.ReadSingle(), r.ReadSingle());
                    l.pts[k] = p; min = Vector2.Min(min, p); max = Vector2.Max(max, p);
                }
                l.bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                lakes.Add(l);
            }
        }

        void ReadLandcover(byte[] bytes)
        {
            lcW = BitConverter.ToUInt16(bytes, 4); lcH = BitConverter.ToUInt16(bytes, 6);
            landcover = new byte[lcW * lcH];
            Array.Copy(bytes, 8, landcover, 0, landcover.Length);
        }

        void ReadFeatures(byte[] mask, byte[] shapes)
        {
            fmW = BitConverter.ToUInt16(mask, 4); fmH = BitConverter.ToUInt16(mask, 6); fmMpp = BitConverter.ToSingle(mask, 8);
            featureMask = new byte[fmW * fmH];
            Array.Copy(mask, 12, featureMask, 0, featureMask.Length);
            outlines = ReadOutlines(shapes);
        }

        public static List<Outline> ReadOutlines(byte[] bytes)
        {
            var list = new List<Outline>();
            using var r = new BinaryReader(new MemoryStream(bytes));
            r.ReadBytes(4);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var o = new Outline { kind = r.ReadByte(), type = r.ReadByte(), levels = r.ReadByte() };
                int n = r.ReadUInt16();
                o.pts = new Vector2[n];
                Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
                for (int k = 0; k < n; k++)
                {
                    var p = new Vector2(r.ReadSingle(), r.ReadSingle());
                    o.pts[k] = p; min = Vector2.Min(min, p); max = Vector2.Max(max, p);
                }
                o.bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                list.Add(o);
            }
            return list;
        }

        /// Mapped ground at a point: 0 none, 1 mown grass, 2 sand, 3 parking lot, 4 building.
        public int FeatureAt(float x, float z)
        {
            int c = (int)(x / fmMpp), r = (int)((info.depth - z) / fmMpp);
            if (c < 0 || r < 0 || c >= fmW || r >= fmH) return 0;
            return featureMask[r * fmW + c];
        }

        /// True inside a walled building (picnic shelters are open and can be walked through).
        public bool InBuilding(float x, float z)
        {
            var p = new Vector2(x, z);
            foreach (var o in outlines)
                if (o.kind == 4 && o.type != 3 && o.bounds.Contains(p) && Inside(o.pts, x, z)) return true;
            return false;
        }

        /// NLCD class at a point (41 deciduous, 71 grassland, 21 developed open space, ...).
        public int LandcoverAt(float x, float z)
        {
            int c = Mathf.Clamp((int)(x / info.width * lcW), 0, lcW - 1);
            int r = Mathf.Clamp((int)((1 - z / info.depth) * lcH), 0, lcH - 1);
            return landcover[r * lcW + c];
        }

        public Lake LakeAt(float x, float z)
        {
            foreach (var l in lakes)
                if (l.bounds.Contains(new Vector2(x, z)) && Inside(l.pts, x, z)) return l;
            return null;
        }

        static bool Inside(Vector2[] poly, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.y > z) != (b.y > z) && x < (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        void IndexSegment(int path, int seg)
        {
            var a = paths[path].pts[seg - 1]; var b = paths[path].pts[seg];
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / Cell);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / Cell);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!segGrid.TryGetValue(Key(cx, cz), out var list)) segGrid[Key(cx, cz)] = list = new List<(int, int)>();
                    list.Add((path, seg));
                }
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector2.Distance(p, a + t * ab);
        }

        /// Distance to the nearest path edge (negative when standing on the surface), and that path.
        public float PathDistance(Vector2 p, out ParkPath nearest, float searchRadius = 40f)
        {
            nearest = null; float best = float.MaxValue;
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.y / Cell);
            int reach = Mathf.CeilToInt(searchRadius / Cell);
            for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    if (!segGrid.TryGetValue(Key(cx + dx, cz + dz), out var list)) continue;
                    foreach (var (pi, si) in list)
                    {
                        var path = paths[pi];
                        float d = SegDist(p, path.pts[si - 1], path.pts[si]) - path.Width * 0.5f;
                        if (d < best) { best = d; nearest = path; }
                    }
                }
            return best;
        }

        /// GTA-style area name: the named trail or road you are on or beside.
        public string NearbyName(Vector2 p)
        {
            string best = ""; float bd = 45f;
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.y / Cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!segGrid.TryGetValue(Key(cx + dx, cz + dz), out var list)) continue;
                    foreach (var (pi, si) in list)
                    {
                        var path = paths[pi];
                        if (string.IsNullOrEmpty(path.name)) continue;
                        float d = SegDist(p, path.pts[si - 1], path.pts[si]);
                        if (d < bd) { bd = d; best = path.name; }
                    }
                }
            return best;
        }

        void BuildGraph()
        {
            var ids = new Dictionary<long, int>();
            var e = new List<List<int>>(); var el = new List<List<float>>();
            int Id(Vector2 v)
            {
                long key = ((long)Mathf.RoundToInt(v.x * 100) << 32) ^ (uint)Mathf.RoundToInt(v.y * 100);
                if (!ids.TryGetValue(key, out int id))
                {
                    id = nodes.Count; ids[key] = id; nodes.Add(v); e.Add(new List<int>()); el.Add(new List<float>());
                }
                return id;
            }
            foreach (var p in paths)
            {
                int prev = Id(p.pts[0]);
                for (int i = 1; i < p.pts.Length; i++)
                {
                    int cur = Id(p.pts[i]);
                    if (cur == prev) continue;
                    float len = Vector2.Distance(nodes[prev], nodes[cur]);
                    e[prev].Add(cur); el[prev].Add(len); e[cur].Add(prev); el[cur].Add(len);
                    prev = cur;
                }
            }
            edges = e.ToArray(); edgeLen = el.ToArray();
        }

        int NearestNode(Vector2 p)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (edges[i].Count == 0) continue;
                float d = (nodes[i] - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// Shortest walk along mapped trails and roads (Dijkstra). Null when unreachable.
        public List<Vector2> Route(Vector2 from, Vector2 to, out float length)
        {
            length = 0;
            int s = NearestNode(from), g = NearestNode(to);
            if (s < 0 || g < 0) return null;
            int n = nodes.Count;
            var dist = new float[n]; var prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            var heap = new MinHeap(); dist[s] = 0; heap.Push(0, s);
            while (heap.Count > 0)
            {
                var (d, u) = heap.Pop();
                if (u == g) break;
                if (d > dist[u]) continue;
                var eu = edges[u]; var lu = edgeLen[u];
                for (int i = 0; i < eu.Count; i++)
                {
                    int v = eu[i]; float nd = d + lu[i];
                    if (nd < dist[v]) { dist[v] = nd; prev[v] = u; heap.Push(nd, v); }
                }
            }
            if (dist[g] == float.MaxValue) return null;
            var route = new List<Vector2>();
            for (int u = g; u != -1; u = prev[u]) route.Add(nodes[u]);
            route.Reverse();
            route.Insert(0, from); route.Add(to);
            length = dist[g] + Vector2.Distance(from, nodes[s]) + Vector2.Distance(to, nodes[g]);
            return route;
        }

        class MinHeap
        {
            readonly List<float> k = new(); readonly List<int> v = new();
            public int Count => k.Count;
            public void Push(float key, int val)
            {
                k.Add(key); v.Add(val); int i = k.Count - 1;
                while (i > 0) { int p = (i - 1) >> 1; if (k[p] <= key) break; k[i] = k[p]; v[i] = v[p]; i = p; }
                k[i] = key; v[i] = val;
            }
            public (float, int) Pop()
            {
                var top = (k[0], v[0]); int last = k.Count - 1;
                float key = k[last]; int val = v[last]; k.RemoveAt(last); v.RemoveAt(last);
                if (k.Count > 0)
                {
                    int i = 0, n = k.Count;
                    while (true)
                    {
                        int c = 2 * i + 1; if (c >= n) break;
                        if (c + 1 < n && k[c + 1] < k[c]) c++;
                        if (k[c] >= key) break;
                        k[i] = k[c]; v[i] = v[c]; i = c;
                    }
                    k[i] = key; v[i] = val;
                }
                return top;
            }
        }
    }
}
