using System;
using System.Collections.Generic;
using UnityEngine;

namespace StonyCreek.EditorTools
{
    /// Simple buildings raised from OpenStreetMap footprints: walls with a gable or flat roof,
    /// and open picnic shelters (a roof on posts). Shapes are generic; only the footprints are real.
    public static class BuildingMaker
    {
        public class Geo
        {
            public readonly List<Vector3> v = new(), n = new();
            public readonly List<Vector2> uv = new();
            public readonly List<Color> c = new();
            public readonly List<int> t = new();
        }

        // type: 0 building, 1 house, 2 garage/shed, 3 shelter, 4 toilets
        static readonly Color[] HouseWalls = { new(0.78f, 0.72f, 0.6f), new(0.72f, 0.72f, 0.7f), new(0.56f, 0.3f, 0.24f), new(0.86f, 0.85f, 0.8f), new(0.58f, 0.64f, 0.68f), new(0.7f, 0.62f, 0.48f) };
        static readonly Color[] HouseRoofs = { new(0.24f, 0.23f, 0.22f), new(0.33f, 0.27f, 0.22f), new(0.3f, 0.3f, 0.31f), new(0.36f, 0.22f, 0.18f) };
        static readonly Color[] PlainWalls = { new(0.6f, 0.36f, 0.28f), new(0.74f, 0.68f, 0.56f), new(0.66f, 0.66f, 0.63f), new(0.7f, 0.6f, 0.46f) };
        static readonly Color ParkWall = new(0.5f, 0.38f, 0.27f), ParkRoof = new(0.25f, 0.3f, 0.22f), Post = new(0.36f, 0.27f, 0.2f);
        // vertex colour alpha tells the shader what a surface is
        const float Windowed = 0f, Plain = 0.5f, Roof = 1f;

        public static void Add(Geo g, List<Vector2> poly, int type, int levels, Func<float, float, float> ground, System.Random rng)
        {
            if (poly.Count < 3) return;
            float area = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) area += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            if (area < 0) poly.Reverse();   // counter-clockwise in x/z
            area = Mathf.Abs(area) * 0.5f;
            if (area < 4) return;

            float gmin = float.MaxValue, gmax = float.MinValue;
            Vector2 centroid = Vector2.zero;
            foreach (var p in poly) { float h = ground(p.x, p.y); gmin = Mathf.Min(gmin, h); gmax = Mathf.Max(gmax, h); centroid += p; }
            centroid /= poly.Count;
            float wallH = type switch { 1 => levels > 1 ? levels * 2.8f : 3.0f, 2 => 2.5f, 3 => 2.7f, 4 => 3.0f, _ => levels > 1 ? levels * 3.2f : 4.2f };
            float baseY = gmin - 0.6f, eave = gmax + wallH;

            bool park = type == 3 || type == 4;
            Color wall = park ? ParkWall * (0.9f + 0.2f * (float)rng.NextDouble()) : type == 0 ? PlainWalls[rng.Next(PlainWalls.Length)] : HouseWalls[rng.Next(HouseWalls.Length)];
            Color roof = park ? ParkRoof : HouseRoofs[rng.Next(HouseRoofs.Length)];
            wall.a = type == 2 ? Plain : Windowed; roof.a = Roof;
            var plainWall = wall; plainWall.a = Plain;

            // oriented box along the longest wall
            Vector2 axis = Vector2.right; float longest = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var e = poly[(i + 1) % poly.Count] - poly[i];
                if (e.magnitude > longest) { longest = e.magnitude; axis = e.normalized; }
            }
            var perp = new Vector2(-axis.y, axis.x);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            foreach (var p in poly)
            {
                float u = Vector2.Dot(p, axis), v = Vector2.Dot(p, perp);
                u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
            }
            if (u1 - u0 < v1 - v0) { (axis, perp) = (perp, -axis); (u0, u1, v0, v1) = (v0, v1, -u1, -u0); }
            float fill = area / Mathf.Max((u1 - u0) * (v1 - v0), 0.01f);
            bool gable = type == 3 || (type != 0 && fill > 0.7f) || (type == 0 && fill > 0.8f && (v1 - v0) < 16);
            Vector3 W(float u, float v, float y) { var p = axis * u + perp * v; return new Vector3(p.x, y, p.y); }

            if (type != 3)
            {
                float along = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                    var d = b - a; float len = d.magnitude; if (len < 0.01f) continue;
                    var outward = new Vector3(d.y, 0, -d.x).normalized;
                    Quad(g, new Vector3(a.x, baseY, a.y), new Vector3(b.x, baseY, b.y), new Vector3(b.x, eave, b.y), new Vector3(a.x, eave, a.y), outward, wall,
                        new Vector2(along, 0), new Vector2(along + len, 0), new Vector2(along + len, eave - baseY), new Vector2(along, eave - baseY));
                    along += len;
                }
            }
            else
            {
                // posts at the four corners of the shelter
                const float inset = 0.35f, r = 0.11f;
                foreach (var (pu, pv) in new[] { (u0 + inset, v0 + inset), (u1 - inset, v0 + inset), (u1 - inset, v1 - inset), (u0 + inset, v1 - inset) })
                {
                    var c = W(pu, pv, 0);
                    Vector3[] dirs = { Vector3.right, Vector3.forward, Vector3.left, Vector3.back };
                    for (int k = 0; k < 4; k++)
                    {
                        var nrm = dirs[k]; var side = dirs[(k + 1) % 4];
                        var p0 = c + nrm * r - side * r; var p1 = c + nrm * r + side * r;
                        var post = Post; post.a = Plain;
                        Quad(g, new Vector3(p0.x, baseY, p0.z), new Vector3(p1.x, baseY, p1.z), new Vector3(p1.x, eave, p1.z), new Vector3(p0.x, eave, p0.z), nrm, post,
                            new Vector2(0, 0), new Vector2(r * 2, 0), new Vector2(r * 2, eave - baseY), new Vector2(0, eave - baseY));
                    }
                }
            }

            if (gable)
            {
                float vc = (v0 + v1) / 2, half = (v1 - v0) / 2;
                float rise = Mathf.Min(half * 0.55f, 2.6f);
                const float ov = 0.45f;
                float drop = ov * rise / Mathf.Max(half, 0.1f);
                float slopeLen = Mathf.Sqrt((half + ov) * (half + ov) + (rise + drop) * (rise + drop));
                for (int s = -1; s <= 1; s += 2)
                {
                    var e0 = W(u0 - ov, vc + s * (half + ov), eave - drop); var e1 = W(u1 + ov, vc + s * (half + ov), eave - drop);
                    var r0 = W(u0 - ov, vc, eave + rise); var r1 = W(u1 + ov, vc, eave + rise);
                    var pn = new Vector3(perp.x, 0, perp.y) * s;
                    var nrm = (Vector3.up * (half + ov) + pn * (rise + drop)).normalized;
                    float ulen = u1 - u0 + ov * 2;
                    Quad(g, e0, e1, r1, r0, nrm, roof, new Vector2(0, 0), new Vector2(ulen, 0), new Vector2(ulen, slopeLen), new Vector2(0, slopeLen));
                    var under = roof * 0.6f; under.a = Roof;
                    Quad(g, e0, e1, r1, r0, -nrm, under, new Vector2(0, 0), new Vector2(ulen, 0), new Vector2(ulen, slopeLen), new Vector2(0, slopeLen));
                }
                // gable ends close the wall up to the ridge
                foreach (float u in new[] { u0, u1 })
                {
                    var nrm = new Vector3(axis.x, 0, axis.y) * (u == u0 ? -1 : 1);
                    var a = W(u, vc - half, eave); var b = W(u, vc + half, eave); var top = W(u, vc, eave + rise);
                    Tri(g, a, b, top, nrm, plainWall, new Vector2(0, eave - baseY), new Vector2(half * 2, eave - baseY), new Vector2(half, eave - baseY + rise));
                    if (type == 3) Tri(g, a, b, top, -nrm, plainWall, new Vector2(0, 0), new Vector2(half * 2, 0), new Vector2(half, rise));
                }
            }
            else
            {
                var idx = StonyWorldBuilder.EarClip(poly);
                int b0 = g.v.Count;
                foreach (var p in poly)
                {
                    g.v.Add(new Vector3(p.x, eave, p.y)); g.n.Add(Vector3.up); g.c.Add(roof);
                    g.uv.Add(new Vector2(Vector2.Dot(p, axis), Vector2.Dot(p, perp)));
                }
                foreach (int k in idx) g.t.Add(b0 + k);
            }
        }

        static void Quad(Geo g, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color col, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            Tri(g, a, b, c, normal, col, ua, ub, uc);
            Tri(g, a, c, d, normal, col, ua, uc, ud);
        }

        // Winds the triangle so its front face points along `normal`.
        static void Tri(Geo g, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color col, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            int i = g.v.Count;
            g.v.Add(a); g.v.Add(b); g.v.Add(c);
            g.n.Add(normal); g.n.Add(normal); g.n.Add(normal);
            g.c.Add(col); g.c.Add(col); g.c.Add(col);
            g.uv.Add(ua); g.uv.Add(ub); g.uv.Add(uc);
            bool flip = Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0;
            g.t.Add(i); g.t.Add(flip ? i + 2 : i + 1); g.t.Add(flip ? i + 1 : i + 2);
        }
    }
}
