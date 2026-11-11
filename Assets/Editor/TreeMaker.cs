using System.Collections.Generic;
using UnityEngine;

namespace StonyCreek.EditorTools
{
    /// Procedural tree models built from real bark photos and leaf-spray cards.
    /// Submesh 0 = bark, 1 = leaf cards. Vertex colour: r = wind flex, g = phase, b = ambient occlusion.
    public static class TreeMaker
    {
        public class Spec
        {
            public string name;
            public float height, trunkRadius, crownBase, crownWidth;
            public int primaries, secondaries, cardsPerBranch, fillCards;
            public float minAngle, maxAngle, cardSize, droop;
            public bool conifer;
        }

        public static readonly Spec[] Specs =
        {
            new Spec { name = "oak", height = 15f, trunkRadius = 0.38f, crownBase = 0.3f, crownWidth = 6.2f, primaries = 9, secondaries = 4, cardsPerBranch = 5, fillCards = 46, minAngle = 48, maxAngle = 74, cardSize = 2.0f, droop = 0.05f },
            new Spec { name = "maple", height = 17f, trunkRadius = 0.33f, crownBase = 0.34f, crownWidth = 5.2f, primaries = 10, secondaries = 4, cardsPerBranch = 5, fillCards = 44, minAngle = 32, maxAngle = 56, cardSize = 1.9f, droop = 0.08f },
            new Spec { name = "birch", height = 14.5f, trunkRadius = 0.19f, crownBase = 0.4f, crownWidth = 3.3f, primaries = 12, secondaries = 3, cardsPerBranch = 5, fillCards = 30, minAngle = 26, maxAngle = 44, cardSize = 1.45f, droop = 0.35f },
            new Spec { name = "pine", height = 20f, trunkRadius = 0.34f, crownBase = 0.26f, crownWidth = 5.0f, primaries = 0, secondaries = 3, cardsPerBranch = 4, fillCards = 24, minAngle = 78, maxAngle = 96, cardSize = 1.9f, droop = 0.12f, conifer = true },
        };

        class Builder
        {
            public readonly List<Vector3> v = new(), n = new();
            public readonly List<Vector2> uv = new();
            public readonly List<Color> c = new();
            public readonly List<int> bark = new(), leaves = new();
        }

        public static Mesh Build(Spec s, int seed, out float crownCenterY, out float crownRadius)
        {
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            var b = new Builder();
            float H = s.height;
            var crownCenter = new Vector3(0, H * (s.conifer ? 0.5f : 0.64f), 0);
            crownCenterY = crownCenter.y;
            crownRadius = s.crownWidth;

            // trunk with a slight lean and curve
            var lean = new Vector3(R() - 0.5f, 0, R() - 0.5f) * 0.5f;
            Vector3 Trunk(float t) => new Vector3(0, t * H, 0) + lean * t * t + new Vector3(Mathf.Sin(t * 5 + seed) * 0.12f, 0, Mathf.Cos(t * 4 + seed) * 0.1f) * t;
            float trunkTop = s.conifer ? 0.97f : 0.78f;
            Tube(b, Trunk, 0, trunkTop, s.trunkRadius, s.trunkRadius * (s.conifer ? 0.12f : 0.32f), 9, 14, 0f, 0.35f, R(), 1.25f, true);

            var tips = new List<(Vector3 p, Vector3 dir, float flex)>();
            if (!s.conifer)
            {
                for (int i = 0; i < s.primaries; i++)
                {
                    float t = Mathf.Lerp(s.crownBase, trunkTop - 0.02f, (i + R() * 0.6f) / s.primaries);
                    float az = i * 137.5f * Mathf.Deg2Rad + R() * 0.4f;
                    float up = Mathf.Lerp(s.maxAngle, s.minAngle, (t - s.crownBase) / (trunkTop - s.crownBase)) * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Sin(up) * Mathf.Cos(az), Mathf.Cos(up), Mathf.Sin(up) * Mathf.Sin(az));
                    // crown envelope: wider through the middle, shorter near the top
                    float env = Mathf.Sin(Mathf.Clamp01((t - s.crownBase) / (1 - s.crownBase) * 0.9f + 0.18f) * Mathf.PI);
                    float len = s.crownWidth * (0.75f + 0.35f * R()) * (0.55f + 0.6f * env);
                    var start = Trunk(t);
                    float phase = R();
                    var mid = start + dir * len * 0.5f + Vector3.up * len * 0.12f;
                    var end = start + dir * len + Vector3.down * len * s.droop;
                    Vector3 Bez(float u) => (1 - u) * (1 - u) * start + 2 * (1 - u) * u * mid + u * u * end;
                    float r0 = s.trunkRadius * Mathf.Lerp(0.55f, 0.3f, t);
                    Tube(b, Bez, 0, 1, r0, r0 * 0.25f, 6, 6, 0.3f, 0.75f, phase, 0.8f, false);
                    for (int k = 0; k < s.secondaries; k++)
                    {
                        float u = 0.35f + 0.6f * (k + R() * 0.5f) / s.secondaries;
                        var p0 = Bez(u);
                        var along = (Bez(Mathf.Min(1, u + 0.05f)) - p0).normalized;
                        var side = Vector3.Cross(along, Vector3.up).normalized * (k % 2 == 0 ? 1 : -1);
                        var d2 = (along * 0.6f + side * 0.6f + Vector3.up * (0.35f - s.droop)).normalized;
                        float l2 = len * (0.35f + 0.25f * R());
                        var e2 = p0 + d2 * l2 + Vector3.down * l2 * s.droop * 1.5f;
                        var m2 = (p0 + e2) / 2 + Vector3.up * l2 * 0.08f;
                        Vector3 Bez2(float q) => (1 - q) * (1 - q) * p0 + 2 * (1 - q) * q * m2 + q * q * e2;
                        Tube(b, Bez2, 0, 1, r0 * 0.35f, r0 * 0.08f, 4, 3, 0.6f, 0.95f, phase, 0.6f, false);
                        for (int j = 0; j < s.cardsPerBranch; j++)
                        {
                            float q = 0.35f + 0.65f * (j + R()) / s.cardsPerBranch;
                            tips.Add((Bez2(q), d2, 0.85f + 0.15f * q));
                        }
                    }
                    tips.Add((end, dir, 1f));
                }
            }
            else
            {
                // white pine: whorls of near-horizontal branches, longest low down
                int whorls = 15;
                for (int w = 0; w < whorls; w++)
                {
                    float t = Mathf.Lerp(s.crownBase, 0.96f, w / (float)(whorls - 1));
                    int count = 4 + (w % 2);
                    float baseAz = R() * Mathf.PI * 2;
                    for (int i = 0; i < count; i++)
                    {
                        if (R() < 0.12f) continue;   // gaps make the crown irregular, like a real white pine
                        float az = baseAz + i * Mathf.PI * 2 / count + R() * 0.5f;
                        float ang = Mathf.Lerp(s.maxAngle, s.minAngle, R()) * Mathf.Deg2Rad;
                        var dir = new Vector3(Mathf.Sin(ang) * Mathf.Cos(az), Mathf.Cos(ang), Mathf.Sin(ang) * Mathf.Sin(az));
                        float len = s.crownWidth * (1.08f - t) * (0.85f + 0.3f * R()) + 0.6f;
                        var start = Trunk(t);
                        var end = start + dir * len + Vector3.up * len * 0.12f;
                        var mid = (start + end) / 2 + Vector3.down * len * s.droop;
                        Vector3 Bez(float u) => (1 - u) * (1 - u) * start + 2 * (1 - u) * u * mid + u * u * end;
                        float r0 = s.trunkRadius * 0.28f * (1.1f - t);
                        Tube(b, Bez, 0, 1, r0, r0 * 0.2f, 5, 4, 0.35f, 0.85f, R(), 0.7f, false);
                        int cards = Mathf.Max(2, Mathf.RoundToInt(s.cardsPerBranch * (0.5f + len / s.crownWidth)));
                        for (int j = 0; j < cards; j++)
                        {
                            float q = 0.25f + 0.75f * (j + R() * 0.7f) / cards;
                            tips.Add((Bez(q), dir, 0.75f + 0.25f * q));
                        }
                    }
                }
                tips.Add((Trunk(0.99f) + Vector3.up * 0.4f, Vector3.up, 1f));
            }

            // leaf cards on the branches, then a few filling the inside of the crown
            foreach (var (p, dir, flex) in tips) Card(b, p, dir, s.cardSize * (0.8f + 0.4f * R()), crownCenter, crownRadius, flex, R(), rng, s);
            for (int i = 0; i < s.fillCards; i++)
            {
                var off = Random3(rng) * 0.8f;
                off.x *= crownRadius; off.z *= crownRadius; off.y *= H * (s.conifer ? 0.4f : 0.26f);
                var p = crownCenter + off;
                if (s.conifer) { float t = Mathf.Clamp01(p.y / H); float rmax = crownRadius * (1.05f - t); var flat = new Vector2(p.x, p.z); if (flat.magnitude > rmax) { flat = flat.normalized * rmax; p.x = flat.x; p.z = flat.y; } }
                Card(b, p, (p - crownCenter).normalized, s.cardSize * 1.15f, crownCenter, crownRadius, 0.8f, R(), rng, s);
            }

            var mesh = new Mesh { name = s.name + "_tree", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(b.v); mesh.SetNormals(b.n); mesh.SetUVs(0, b.uv); mesh.SetColors(b.c);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(b.bark, 0); mesh.SetTriangles(b.leaves, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3 Random3(System.Random rng)
        {
            while (true)
            {
                var p = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
                if (p.sqrMagnitude <= 1) return p;
            }
        }

        delegate Vector3 Curve(float t);

        static void Tube(Builder b, Curve f, float t0, float t1, float r0, float r1, int sides, int segs, float flex0, float flex1, float phase, float barkTile, bool trunk)
        {
            int baseIndex = b.v.Count;
            float circ = 2 * Mathf.PI * Mathf.Max(r0, 0.05f);
            float uRepeat = Mathf.Max(1, Mathf.Round(circ / barkTile));
            float lengthSoFar = 0;
            Vector3 prev = f(t0);
            for (int i = 0; i <= segs; i++)
            {
                float t = Mathf.Lerp(t0, t1, i / (float)segs);
                var p = f(t);
                var ahead = f(Mathf.Min(t1, t + 0.01f)) - f(Mathf.Max(t0, t - 0.01f));
                var axis = ahead.sqrMagnitude > 1e-8f ? ahead.normalized : Vector3.up;
                var side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
                var other = Vector3.Cross(axis, side);
                lengthSoFar += Vector3.Distance(prev, p); prev = p;
                float k = i / (float)segs;
                float r = Mathf.Lerp(r0, r1, k);
                if (trunk && k < 0.08f) r *= 1 + (0.08f - k) * 4f;   // root flare
                float flex = Mathf.Lerp(flex0, flex1, k);
                if (trunk) flex = flex1 * k * k;
                for (int j = 0; j <= sides; j++)
                {
                    float a = j / (float)sides * Mathf.PI * 2;
                    var nrm = side * Mathf.Cos(a) + other * Mathf.Sin(a);
                    b.v.Add(p + nrm * r); b.n.Add(nrm);
                    b.uv.Add(new Vector2(j / (float)sides * uRepeat, lengthSoFar / barkTile));
                    float ao = trunk ? Mathf.Lerp(0.8f, 1f, Mathf.Clamp01(k * 3)) : 0.85f;
                    b.c.Add(new Color(flex, phase, ao, 1));
                }
            }
            for (int i = 0; i < segs; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = baseIndex + i * (sides + 1) + j, c = a + sides + 1;
                    b.bark.AddRange(new[] { a, c, a + 1, a + 1, c, c + 1 });
                }
        }

        static void Card(Builder b, Vector3 p, Vector3 dir, float size, Vector3 crownCenter, float crownRadius, float flex, float phase, System.Random rng, Spec s)
        {
            float R() => (float)rng.NextDouble();
            // the card's "up" follows the branch, rolled randomly around it
            var up = (dir + new Vector3(R() - 0.5f, R() - 0.5f - (s.droop * 0.8f), R() - 0.5f) * 0.9f).normalized;
            var rnd = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f);
            var right = Vector3.Cross(up, rnd).normalized;
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            var half = size * 0.5f;
            var center = p + up * half * 0.55f;
            var sphere = (p - crownCenter);
            sphere.y *= 0.8f;
            var normal = (sphere.normalized * 0.8f + Vector3.up * 0.35f).normalized;
            float depth = Mathf.Clamp01(sphere.magnitude / Mathf.Max(crownRadius, 0.1f));
            float ao = Mathf.Lerp(0.42f, 1f, depth) * Mathf.Lerp(0.85f, 1.05f, Mathf.Clamp01((p.y - crownCenter.y) / (crownRadius) + 0.5f));
            int i0 = b.v.Count;
            Vector3[] corners = { center - right * half - up * half, center + right * half - up * half, center + right * half + up * half, center - right * half + up * half };
            Vector2[] uvs = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
            for (int k = 0; k < 4; k++)
            {
                b.v.Add(corners[k]); b.n.Add(normal); b.uv.Add(uvs[k]);
                float f = k < 2 ? flex * 0.85f : flex;
                b.c.Add(new Color(f, phase, Mathf.Clamp01(ao), 1));
            }
            b.leaves.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
        }

        /// Three crossed quads standing on the ground, used for distant trees.
        public static Mesh Billboard(string name, float width, float height)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var tri = new List<int>();
            for (int q = 0; q < 3; q++)
            {
                float a = q * Mathf.PI / 3;
                var r = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * width * 0.5f;
                int i0 = v.Count;
                v.Add(-r + Vector3.down * 0.3f); v.Add(r + Vector3.down * 0.3f); v.Add(r + Vector3.up * height); v.Add(-r + Vector3.up * height);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) n.Add(Vector3.up);
                tri.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
            }
            var m = new Mesh { name = name };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        /// Crossed blade cards for grass clumps.
        public static Mesh Grass()
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var c = new List<Color>(); var tri = new List<int>();
            for (int q = 0; q < 3; q++)
            {
                float a = q * Mathf.PI / 3 + 0.3f;
                var r = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.38f;
                int i0 = v.Count;
                v.Add(-r); v.Add(r); v.Add(r + Vector3.up * 0.55f); v.Add(-r + Vector3.up * 0.55f);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) n.Add(Vector3.up);
                c.Add(new Color(0, 0, 0.55f)); c.Add(new Color(0, 0, 0.55f)); c.Add(new Color(1, 0, 1)); c.Add(new Color(1, 0, 1));
                tri.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
            }
            var m = new Mesh { name = "grass_clump" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetColors(c); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        /// A low leafy bush: a ball of leaf cards.
        public static Mesh Shrub()
        {
            var rng = new System.Random(5);
            var b = new Builder();
            var spec = new Spec { droop = 0.2f };
            var center = new Vector3(0, 0.55f, 0);
            for (int i = 0; i < 12; i++)
            {
                var off = Random3(rng); off.y = Mathf.Abs(off.y) * 0.7f;
                var p = new Vector3(0, 0.2f, 0) + new Vector3(off.x * 0.6f, off.y, off.z * 0.6f);
                Card(b, p, (p - center + Vector3.up * 0.6f).normalized, 0.95f, center, 0.8f, 0.6f + 0.4f * off.y, (float)rng.NextDouble(), rng, spec);
            }
            var m = new Mesh { name = "shrub" };
            m.SetVertices(b.v); m.SetNormals(b.n); m.SetUVs(0, b.uv); m.SetColors(b.c); m.SetTriangles(b.leaves, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
