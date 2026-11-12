using System;
using System.Collections.Generic;
using UnityEngine;

namespace StonyCreek.EditorTools
{
    /// Buildings raised from OpenStreetMap footprints. The footprints are real; the shapes are modelled
    /// by hand from reference views of the actual buildings where we have them:
    ///   style 1  timber lodge (the Nature Center): tan wood walls, low gable wings, steep entry gable on timber posts
    ///   style 2  flat-roofed brick (beach bathhouses and restrooms): brick walls, wide flat overhang on concrete columns
    ///   style 4  mid-century modern: smooth white walls under one 10 degree roof plane that slopes to the rear (the park office, per the user)
    ///   type 3   open picnic shelter: dark metal roof on timber posts over a concrete slab
    ///   other    generic houses, garages and sheds
    public static class BuildingMaker
    {
        public class Geo
        {
            public readonly List<Vector3> v = new(), n = new();
            public readonly List<Vector2> uv = new();
            public readonly List<Color> c = new();
            public readonly List<int> t = new();
        }

        // vertex colour alpha tells the shader what a surface is
        const float Windowed = 0f, WindowedBrick = 0.15f, Brick = 0.25f, Lodge = 0.35f, Plain = 0.5f, Concrete = 0.6f, Smooth = 0.7f, Roof = 1f;

        // type: 0 building, 1 house, 2 garage/shed, 3 shelter, 4 toilets
        static readonly Color[] HouseWalls = { new(0.78f, 0.72f, 0.6f), new(0.72f, 0.72f, 0.7f), new(0.56f, 0.3f, 0.24f), new(0.86f, 0.85f, 0.8f), new(0.58f, 0.64f, 0.68f), new(0.7f, 0.62f, 0.48f) };
        static readonly Color[] HouseRoofs = { new(0.24f, 0.23f, 0.22f), new(0.33f, 0.27f, 0.22f), new(0.3f, 0.3f, 0.31f), new(0.36f, 0.22f, 0.18f) };
        static readonly Color[] PlainWalls = { new(0.6f, 0.36f, 0.28f), new(0.74f, 0.68f, 0.56f), new(0.66f, 0.66f, 0.63f), new(0.7f, 0.6f, 0.46f) };

        static Color A(Color c, float a) { c.a = a; return c; }
        static Vector3 P(Vector2 c, Vector2 du, Vector2 dv, float u, float v, float y) { var p = c + du * u + dv * v; return new Vector3(p.x, y, p.y); }
        static Vector3 N(Vector2 d) => new Vector3(d.x, 0, d.y);

        public static void Add(Geo g, List<Vector2> poly, int type, int levels, int style, int front, Func<float, float, float> ground, System.Random rng)
        {
            if (poly.Count < 3) return;
            float area = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) area += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            if (area < 0) poly.Reverse();   // counter-clockwise in x/z
            area = Mathf.Abs(area) * 0.5f;
            if (area < 4) return;

            float gmin = float.MaxValue, gmax = float.MinValue;
            foreach (var p in poly) { float h = ground(p.x, p.y); gmin = Mathf.Min(gmin, h); gmax = Mathf.Max(gmax, h); }
            float vary = 0.92f + 0.16f * (float)rng.NextDouble();

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
            var ctr = axis * ((u0 + u1) / 2) + perp * ((v0 + v1) / 2);
            float halfU = (u1 - u0) / 2, halfV = (v1 - v0) / 2;
            float fill = area / Mathf.Max((u1 - u0) * (v1 - v0), 0.01f);
            float baseY = gmin - 0.6f;

            if (style == 1) { LodgeBuilding(g, poly, ctr, axis, perp, halfU, halfV, front, baseY, gmax, vary); return; }
            if (style == 2) { BrickFlat(g, poly, ctr, axis, perp, halfU, halfV, fill, area, baseY, gmax, vary); return; }
            if (style == 4) { ModernOffice(g, poly, ctr, axis, perp, halfU, halfV, front, baseY, gmax); return; }
            if (type == 3) { Shelter(g, ctr, axis, perp, halfU, halfV, baseY, gmax); return; }

            float wallH = type switch { 1 => levels > 1 ? levels * 2.8f : 3.0f, 2 => 2.5f, 4 => 3.0f, _ => levels > 1 ? levels * 3.2f : 4.2f };
            float eave = gmax + wallH;
            Color wall, roof;
            {
                wall = type == 0 ? PlainWalls[rng.Next(PlainWalls.Length)] : HouseWalls[rng.Next(HouseWalls.Length)];
                roof = A(HouseRoofs[rng.Next(HouseRoofs.Length)], Roof);
                wall.a = type == 2 ? Plain : Windowed;
            }
            Walls(g, poly, baseY, eave, wall);
            bool gable = (type != 0 && fill > 0.7f) || (type == 0 && fill > 0.8f && halfV * 2 < 16);
            if (gable)
            {
                float rise = Mathf.Min(halfV * 0.55f, 2.6f);
                Gable(g, ctr, axis, perp, halfU, halfV, eave, rise, 0.45f, roof, A(wall, Plain), false);
            }
            else FlatRoof(g, poly, axis, perp, eave, roof);
        }

        // ---------------- styles ----------------

        // The Nature Center: stepped timber wing with a low gable roof and a steep entry gable on posts.
        static void LodgeBuilding(Geo g, List<Vector2> poly, Vector2 ctr, Vector2 axis, Vector2 perp, float halfU, float halfV, int front, float baseY, float gmax, float vary)
        {
            const float wallH = 3.9f;
            float eave = gmax + wallH;
            var wall = A(new Color(0.78f, 0.58f, 0.34f) * vary, Lodge);
            var roof = A(new Color(0.56f, 0.4f, 0.26f), Roof);
            Walls(g, poly, baseY, eave, wall);
            float rise = Mathf.Min(halfV * 0.3f, 2.3f);
            Gable(g, ctr, axis, perp, halfU, halfV, eave, rise, 0.9f, roof, A(wall, Plain), false);

            // the entrance faces whichever long side points toward the nearest path
            float ang = front / 256f * Mathf.PI * 2f;
            var toPath = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            float side = Vector2.Dot(toPath, perp) >= 0 ? 1f : -1f;
            float porchHalfW = 4.6f, project = 3.4f, eaveE = eave + 0.5f, riseE = 3.1f;
            var ec = ctr + perp * (side * (halfV + project / 2 - 2f));
            // steep gable, ridge running toward the visitor
            Gable(g, ec, perp * side, axis, project / 2 + 2.4f, porchHalfW, eaveE, riseE, 0.7f, roof, A(wall, Lodge), true);
            // timber posts on stone piers at the two outer corners of the porch
            var postC = A(new Color(0.42f, 0.28f, 0.17f), Plain);
            var stone = A(new Color(0.5f, 0.46f, 0.42f), Concrete);
            foreach (float sa in new[] { -1f, 1f })
            {
                var pc = ctr + perp * (side * (halfV + project - 1.4f)) + axis * (sa * (porchHalfW - 0.4f));
                BoxFrame(g, pc, axis, perp, 0.3f, 0.3f, baseY, gmax + 1.1f, stone);
                BoxFrame(g, pc, axis, perp, 0.17f, 0.17f, gmax + 1.1f, eaveE, postC);
            }
            // a slab under the porch
            BoxFrame(g, ctr + perp * (side * (halfV + project / 2 - 1.2f)), axis, perp, porchHalfW + 0.5f, project / 2 + 0.7f, baseY, gmax + 0.15f, A(new Color(0.6f, 0.6f, 0.58f), Concrete));
        }

        // Beach bathhouse style: brick box under a thick flat roof that overhangs on concrete columns.
        static void BrickFlat(Geo g, List<Vector2> poly, Vector2 ctr, Vector2 axis, Vector2 perp, float halfU, float halfV, float fill, float area, float baseY, float gmax, float vary)
        {
            const float wallH = 3.4f, ov = 1.0f, slab = 0.38f;
            float eave = gmax + wallH;
            var brick = A(new Color(0.47f, 0.22f, 0.16f) * vary, area > 150 ? WindowedBrick : Brick);
            var dark = new Color(0.2f, 0.15f, 0.12f);
            Walls(g, poly, baseY, eave, brick);
            if (fill >= 0.6f)
            {
                float hu = halfU + ov, hv = halfV + ov;
                BoxFrame(g, ctr, axis, perp, hu, hv, eave, eave + slab, A(dark, Plain));                  // fascia
                Cap(g, P(ctr, axis, perp, 0, 0, eave + slab), axis, perp, hu, hv, A(dark, Roof), true);   // roof top
                Cap(g, P(ctr, axis, perp, 0, 0, eave), axis, perp, hu, hv, A(dark * 0.8f, Plain), false); // soffit
                // concrete columns just outside the walls
                var conc = A(new Color(0.64f, 0.63f, 0.6f), Concrete);
                int nu = Mathf.Max(1, Mathf.CeilToInt(halfU * 2 / 5.5f));
                for (int i = 0; i <= nu; i++)
                    foreach (float sv in new[] { -1f, 1f })
                        BoxFrame(g, ctr + axis * (-halfU + 2 * halfU * i / nu) + perp * (sv * (halfV + ov - 0.4f)), axis, perp, 0.2f, 0.2f, baseY, eave, conc);
            }
            else FlatRoof(g, poly, axis, perp, eave + slab, A(dark, Roof));
        }

        // The park office: smooth white walls with ribbon glazing under a single roof plane, 10 degrees,
        // high at the front (the side facing the nearest path) and falling to the rear, with a deep front overhang.
        static void ModernOffice(Geo g, List<Vector2> poly, Vector2 ctr, Vector2 axis, Vector2 perp, float halfU, float halfV, int front, float baseY, float gmax)
        {
            float ang = front / 256f * Mathf.PI * 2f;
            var toPath = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            Vector2 f = Vector2.Dot(toPath, perp) >= 0 ? perp : -perp;   // unit vector toward the front
            float tan = Mathf.Tan(10f * Mathf.Deg2Rad);
            float halfD = halfV;                                         // depth runs front to rear along f
            float rearWall = 3.2f;
            float frontY = gmax + rearWall + 2f * halfD * tan;           // roof plane height at the front wall
            float Plane(float d) => frontY - (halfD - d) * tan;          // d = distance from centre toward the front
            var white = A(new Color(0.94f, 0.94f, 0.91f), Smooth);
            Walls(g, poly, baseY, p => Plane(Vector2.Dot(p - ctr, f)), white);

            const float thick = 0.32f, ovFront = 1.1f, ovRear = 0.5f, ovSide = 0.6f;
            float dF = halfD + ovFront, dR = -(halfD + ovRear), s = halfU + ovSide;
            Vector3 Pt(float d, float sd, float up) { var p = ctr + f * d + axis * sd; return new Vector3(p.x, Plane(d) + up, p.y); }
            var bf0 = Pt(dF, -s, 0.03f); var bf1 = Pt(dF, s, 0.03f); var br1 = Pt(dR, s, 0.03f); var br0 = Pt(dR, -s, 0.03f);
            var tf0 = Pt(dF, -s, thick); var tf1 = Pt(dF, s, thick); var tr1 = Pt(dR, s, thick); var tr0 = Pt(dR, -s, thick);
            var nTop = Vector3.Cross(tf1 - tf0, tr0 - tf0).normalized;
            if (nTop.y < 0) nTop = -nTop;
            var membrane = A(new Color(0.3f, 0.3f, 0.32f), Roof);
            var fascia = A(new Color(0.95f, 0.95f, 0.93f), Plain);
            Quad(g, tf0, tf1, tr1, tr0, nTop, membrane, new Vector2(0, 0), new Vector2(2 * s, 0), new Vector2(2 * s, dF - dR), new Vector2(0, dF - dR));
            Quad(g, bf0, bf1, br1, br0, -nTop, fascia, new Vector2(0, 0), new Vector2(2 * s, 0), new Vector2(2 * s, dF - dR), new Vector2(0, dF - dR));
            Quad(g, bf0, bf1, tf1, tf0, N(f), fascia, new Vector2(0, 0), new Vector2(2 * s, 0), new Vector2(2 * s, thick), new Vector2(0, thick));    // front fascia
            Quad(g, br1, br0, tr0, tr1, -N(f), fascia, new Vector2(0, 0), new Vector2(2 * s, 0), new Vector2(2 * s, thick), new Vector2(0, thick));   // rear fascia
            Quad(g, bf1, br1, tr1, tf1, N(axis), fascia, new Vector2(0, 0), new Vector2(dF - dR, 0), new Vector2(dF - dR, thick), new Vector2(0, thick));
            Quad(g, br0, bf0, tf0, tr0, -N(axis), fascia, new Vector2(0, 0), new Vector2(dF - dR, 0), new Vector2(dF - dR, thick), new Vector2(0, thick));
        }

        // Open picnic shelter: low dark roof on thick timber posts, with a concrete slab.
        static void Shelter(Geo g, Vector2 ctr, Vector2 axis, Vector2 perp, float halfU, float halfV, float baseY, float gmax)
        {
            const float postH = 2.7f;
            float eave = gmax + postH;
            var post = A(new Color(0.34f, 0.24f, 0.17f), Plain);
            var roof = A(new Color(0.27f, 0.21f, 0.17f), Roof);
            BoxFrame(g, ctr, axis, perp, halfU + 0.7f, halfV + 0.7f, baseY, gmax + 0.14f, A(new Color(0.62f, 0.62f, 0.6f), Concrete));
            int nu = Mathf.Max(1, Mathf.RoundToInt(halfU * 2 / 3.2f));
            for (int i = 0; i <= nu; i++)
                foreach (float sv in new[] { -1f, 1f })
                    BoxFrame(g, ctr + axis * (-halfU + 0.35f + (2 * halfU - 0.7f) * i / nu) + perp * (sv * (halfV - 0.35f)), axis, perp, 0.11f, 0.11f, gmax + 0.14f, eave, post);
            BoxFrame(g, ctr + perp * 0, axis, perp, halfU, halfV, eave - 0.3f, eave, A(new Color(0.3f, 0.21f, 0.15f), Plain));   // tie beam
            float rise = Mathf.Min(halfV * 0.34f, 1.5f);
            Gable(g, ctr, axis, perp, halfU, halfV, eave, rise, 0.55f, roof, A(new Color(0.3f, 0.21f, 0.15f), Plain), true);
        }

        // ---------------- building blocks ----------------

        static void Walls(Geo g, List<Vector2> poly, float baseY, float eave, Color wall)
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

        /// Walls whose top edge follows a sloped roof plane: topY gives the roof height above a point.
        static void Walls(Geo g, List<Vector2> poly, float baseY, Func<Vector2, float> topY, Color wall)
        {
            float along = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                var d = b - a; float len = d.magnitude; if (len < 0.01f) continue;
                var outward = new Vector3(d.y, 0, -d.x).normalized;
                float ya = topY(a), yb = topY(b);
                Quad(g, new Vector3(a.x, baseY, a.y), new Vector3(b.x, baseY, b.y), new Vector3(b.x, yb, b.y), new Vector3(a.x, ya, a.y), outward, wall,
                    new Vector2(along, 0), new Vector2(along + len, 0), new Vector2(along + len, yb - baseY), new Vector2(along, ya - baseY));
                along += len;
            }
        }

        static void FlatRoof(Geo g, List<Vector2> poly, Vector2 axis, Vector2 perp, float y, Color roof)
        {
            var idx = StonyWorldBuilder.EarClip(poly);
            int b0 = g.v.Count;
            foreach (var p in poly)
            {
                g.v.Add(new Vector3(p.x, y, p.y)); g.n.Add(Vector3.up); g.c.Add(roof);
                g.uv.Add(new Vector2(Vector2.Dot(p, axis), Vector2.Dot(p, perp)));
            }
            foreach (int k in idx) g.t.Add(b0 + k);
        }

        /// Four vertical faces of a rectangular post or slab (no top, no bottom).
        static void BoxFrame(Geo g, Vector2 c, Vector2 du, Vector2 dv, float hu, float hv, float y0, float y1, Color col)
        {
            if (y1 <= y0) return;
            var sides = new (Vector2 n, Vector2 t, float hn, float ht)[] { (du, dv, hu, hv), (-du, dv, hu, hv), (dv, du, hv, hu), (-dv, du, hv, hu) };
            foreach (var s in sides)
            {
                var mid = c + s.n * s.hn;
                var a = mid - s.t * s.ht; var b = mid + s.t * s.ht;
                Quad(g, new Vector3(a.x, y0, a.y), new Vector3(b.x, y0, b.y), new Vector3(b.x, y1, b.y), new Vector3(a.x, y1, a.y), N(s.n), col,
                    new Vector2(0, 0), new Vector2(2 * s.ht, 0), new Vector2(2 * s.ht, y1 - y0), new Vector2(0, y1 - y0));
            }
            // a cap so posts and columns are closed at the top
            Cap(g, new Vector3(c.x, y1, c.y), du, dv, hu, hv, col, true);
        }

        static void Cap(Geo g, Vector3 centre, Vector2 du, Vector2 dv, float hu, float hv, Color col, bool up)
        {
            var c = new Vector2(centre.x, centre.z);
            var a = c - du * hu - dv * hv; var b = c + du * hu - dv * hv; var d = c + du * hu + dv * hv; var e = c - du * hu + dv * hv;
            var nrm = up ? Vector3.up : Vector3.down;
            Quad(g, new Vector3(a.x, centre.y, a.y), new Vector3(b.x, centre.y, b.y), new Vector3(d.x, centre.y, d.y), new Vector3(e.x, centre.y, e.y), nrm, col,
                new Vector2(0, 0), new Vector2(2 * hu, 0), new Vector2(2 * hu, 2 * hv), new Vector2(0, 2 * hv));
        }

        /// A gable roof over a rectangle. Ridge runs along du; the roof overhangs by ov and drops with the slope.
        static void Gable(Geo g, Vector2 c, Vector2 du, Vector2 dv, float halfU, float halfV, float eaveY, float rise, float ov, Color roof, Color endWall, bool openEnds)
        {
            float hv = halfV + ov, hu = halfU + ov;
            float drop = ov * rise / Mathf.Max(halfV, 0.1f);
            float slopeLen = Mathf.Sqrt(hv * hv + (rise + drop) * (rise + drop));
            float ulen = hu * 2;
            for (int s = -1; s <= 1; s += 2)
            {
                var e0 = P(c, du, dv, -hu, s * hv, eaveY - drop); var e1 = P(c, du, dv, hu, s * hv, eaveY - drop);
                var r0 = P(c, du, dv, -hu, 0, eaveY + rise); var r1 = P(c, du, dv, hu, 0, eaveY + rise);
                var nrm = (Vector3.up * hv + N(dv) * (s * (rise + drop))).normalized;
                Quad(g, e0, e1, r1, r0, nrm, roof, new Vector2(0, 0), new Vector2(ulen, 0), new Vector2(ulen, slopeLen), new Vector2(0, slopeLen));
                var under = roof * 0.6f; under.a = roof.a;
                Quad(g, e0, e1, r1, r0, -nrm, under, new Vector2(0, 0), new Vector2(ulen, 0), new Vector2(ulen, slopeLen), new Vector2(0, slopeLen));
            }
            foreach (float u in new[] { -halfU, halfU })
            {
                var nrm = N(du) * Mathf.Sign(u);
                var a = P(c, du, dv, u, -halfV, eaveY); var b = P(c, du, dv, u, halfV, eaveY); var top = P(c, du, dv, u, 0, eaveY + rise);
                Tri(g, a, b, top, nrm, endWall, new Vector2(0, 0), new Vector2(halfV * 2, 0), new Vector2(halfV, rise));
                if (openEnds) Tri(g, a, b, top, -nrm, endWall, new Vector2(0, 0), new Vector2(halfV * 2, 0), new Vector2(halfV, rise));
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
