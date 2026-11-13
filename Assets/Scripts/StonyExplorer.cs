using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StonyCreek
{
    /// Composition root: loads the baked park data, wires the renderers, walker and HUD,
    /// and talks to the web page (state for tests, pixel ratio for UI sizing).
    public class StonyExplorer : MonoBehaviour
    {
        public Terrain terrain;
        public Walker walker;
        public ForestRenderer forest;
        public GrassField grass;
        public MapHUD hud;
        public Light sun;

        ParkData data;
        Renderer[] propRenderers = new Renderer[0];

        int CountVisibleProps()
        {
            int n = 0;
            foreach (var r in propRenderers) if (r != null && r.isVisible) n++;
            return n;
        }
        float reportTimer, fpsTime; int fpsFrames; float fps;
        bool mobile;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void StonyReport(string json);
        [DllImport("__Internal")] static extern float StonyPixelRatio();
        [DllImport("__Internal")] static extern string StonyQuery();
#else
        static void StonyReport(string json) { }
        static float StonyPixelRatio() => 1f;
        static string StonyQuery() => "";
#endif
        int quality = -1;      // from ?q=0|1|2, or -1 = automatic
        float maxFar, maxNear, adaptTimer = 4;

        void Awake()
        {
            Application.targetFrameRate = 60;
            mobile = Application.isMobilePlatform;
            data = ParkData.Load();
            var q = System.Text.RegularExpressions.Regex.Match(StonyQuery() ?? "", @"[?&]q=(\d)");
            if (q.Success) quality = int.Parse(q.Groups[1].Value);
            if (quality == 0)
            {
                forest.nearDistance = 40; forest.farDistance = 380;
                grass.radius = 20; grass.densityScale = 0.4f;
                QualitySettings.shadowDistance = 25;
            }
            else if (mobile && quality != 2)
            {
                forest.nearDistance = 50; forest.farDistance = 820;
                grass.radius = 30; grass.densityScale = 0.6f;
                QualitySettings.shadowDistance = 42;
            }
            else if (quality != 2) forest.nearDistance = Mathf.Min(forest.nearDistance, 64);   // the photo-placed woods are dense
            maxFar = forest.farDistance;
            maxNear = forest.nearDistance;
            // small props vanish at a distance: cars and tables are invisible past a few hundred metres anyway
            var cull = new float[32];
            cull[8] = mobile ? 170f : 260f;
            Camera.main.layerCullDistances = cull;
            var found = new List<Renderer>();
            foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)) if (r.gameObject.layer == 8) found.Add(r);
            propRenderers = found.ToArray();
            forest.Init(data);
            grass.Init(data);
            walker.Init(data);
            hud.Init(data);
            Shader.SetGlobalFloat("_StonyWind", 1);
            Shader.SetGlobalVector("_StonyWindDir", new Vector4(0.8f, 0, 0.6f, 0) * 0.55f);
            TravelTo(0);
        }

        public void TravelTo(int place)
        {
            var p = data.info.places[Mathf.Clamp(place, 0, data.info.places.Length - 1)];
            var target = new Vector2(p.x, p.z);
            // stand on the nearest mapped trail, facing along it
            var start = target; float yaw = 0;
            float best = 260;
            foreach (var path in data.paths)
                for (int i = 1; i < path.pts.Length; i++)
                {
                    var a = path.pts[i - 1]; var b = path.pts[i];
                    var ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(target - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
                    var q = a + ab * t; float d = Vector2.Distance(q, target);
                    if (d < best && data.LakeAt(q.x, q.y) == null) { best = d; start = q; yaw = Mathf.Atan2(ab.x, ab.y) * Mathf.Rad2Deg; }
                }
            walker.TeleportTo(start, Mathf.Repeat(yaw, 360));
            hud.Toast(p.name);
        }

        public void BeginWalk()
        {
            walker.Started = true;
        }

        void Update()
        {
            hud.PixelRatio = Mathf.Max(0.5f, StonyPixelRatio());
            fpsFrames++; fpsTime += Time.unscaledDeltaTime;
            if (fpsTime > 1) { fps = fpsFrames / fpsTime; fpsFrames = 0; fpsTime = 0; }
            // automatic quality: pull the forest horizon in when the frame rate sags, push it out when there's headroom
            if (quality < 0 && walker.Started && (adaptTimer -= Time.unscaledDeltaTime) <= 0)
            {
                adaptTimer = 2;
                // the horizon comes in first; in dense woods the full close-up trees follow once the horizon is short
                if (fps < 26 && (forest.farDistance > 460 || forest.nearDistance > 44))
                {
                    if (forest.farDistance > 460) { forest.farDistance *= 0.85f; grass.radius = Mathf.Max(26, grass.radius * 0.92f); }
                    if (forest.nearDistance > 44) forest.nearDistance *= 0.9f;
                    forest.Refresh();
                }
                else if (fps > 50 && forest.nearDistance < maxNear) { forest.nearDistance = Mathf.Min(maxNear, forest.nearDistance * 1.06f); forest.Refresh(); }
                else if (fps > 50 && forest.farDistance < maxFar) { forest.farDistance = Mathf.Min(maxFar, forest.farDistance * 1.08f); forest.Refresh(); }
            }
            if ((reportTimer -= Time.unscaledDeltaTime) <= 0)
            {
                reportTimer = 0.25f;
                var p = walker.transform.position;
                var fwd = walker.transform.forward;
                StonyReport(string.Format(CultureInfo.InvariantCulture,
                    "{{\"ready\":true,\"started\":{0},\"x\":{1:0.0},\"y\":{2:0.0},\"z\":{3:0.0},\"yaw\":{4:0.0},\"pitch\":{5:0.0},\"fwdX\":{6:0.000},\"fwdZ\":{7:0.000},\"fps\":{8:0.0},\"trees\":{9},\"near\":{10},\"far\":{11},\"grass\":{12},\"mapOpen\":{13},\"waypoint\":{14},\"route\":{15:0},\"jog\":{16},\"touch\":{17},\"ui\":{18:0.00},\"screen\":[{19},{20}],\"mini\":[{21:0},{22:0},{23:0}],\"mapBtn\":[{24:0},{25:0}],\"jogBtn\":[{26:0},{27:0}],\"buttons\":{28},\"frame\":{29},\"farDistance\":{30:0},\"wet\":{31},\"feature\":{32},\"inBuilding\":{33},\"props\":{34},\"propsNear\":{35},\"pace\":\"{36}\",\"alt\":{37:0.0}}}",
                    walker.Started ? "true" : "false", p.x, p.y, p.z, walker.Yaw, walker.Pitch, fwd.x, fwd.z, fps,
                    forest.TreeCount, forest.NearDrawn, forest.FarDrawn, grass.GrassDrawn,
                    hud.MapOpen ? "true" : "false", hud.HasWaypoint ? "true" : "false", hud.RouteLength,
                    walker.Jog ? "true" : "false", walker.TouchMode ? "true" : "false", hud.UiScale, Screen.width, Screen.height,
                    hud.MiniRect.center.x, hud.MiniRect.center.y, hud.MiniRect.width, hud.MapButtonRect.center.x, hud.MapButtonRect.center.y,
                    hud.JogButtonRect.center.x, hud.JogButtonRect.center.y, hud.MapOpen ? hud.ButtonsJson : "[]", Time.frameCount, forest.farDistance, data.LakeAt(p.x, p.z) != null ? "true" : "false", data.FeatureAt(p.x, p.z), data.InBuilding(p.x, p.z) ? "true" : "false", propRenderers.Length, CountVisibleProps(), walker.Mode.ToString().ToLowerInvariant(), walker.Altitude));
            }
        }

        // Test hooks, called from the page with unityInstance.SendMessage('Explorer', 'Command', '...').
        public void Command(string cmd)
        {
            var a = cmd.Split(' ');
            float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
            switch (a[0])
            {
                case "start": BeginWalk(); break;
                case "travel": TravelTo(int.Parse(a[1])); break;
                case "teleport": walker.TeleportTo(new Vector2(F(1), F(2)), a.Length > 3 ? F(3) : walker.Yaw); break;
                case "map": hud.ToggleMap(a.Length < 2 || a[1] != "off"); break;
                case "waypoint": hud.SetWaypoint(new Vector2(F(1), F(2))); break;
                case "clear": hud.ClearWaypoint(); break;
                case "time": SetSun(F(1)); break;
                case "pace": walker.Mode = (Walker.Pace)System.Enum.Parse(typeof(Walker.Pace), a[1], true); break;
                case "shore": Shore(); break;
                case "goto": Goto(a[1], int.Parse(a[2]), a.Length > 3 ? int.Parse(a[3]) : 0, a.Length > 4 ? F(4) : float.NaN); break;
                case "prop": GoProp(a[1], int.Parse(a[2]), a.Length > 3 ? F(3) : 9f, a.Length > 4 ? F(4) : 0f); break;
            }
        }

        // Test hook: stand `dist` metres from sample prop i of a kind (picnic, bench, slide, roundabout, canoe, grill, car), looking at it.
        void GoProp(string kind, int i, float dist, float bearing)
        {
            var s = data.info.propSamples;
            PropSpot[] list = kind switch { "car" => s.car, "picnic" => s.picnic, "bench" => s.bench, "slide" => s.slide, "roundabout" => s.roundabout, "canoe" => s.canoe, "grill" => s.grill, "canoe_rack" => s.canoe_rack, _ => null };
            if (list == null || list.Length == 0) return;
            var p = list[Mathf.Clamp(i, 0, list.Length - 1)];
            float a = bearing * Mathf.Deg2Rad;
            var from = new Vector2(p.x + Mathf.Sin(a) * dist, p.z + Mathf.Cos(a) * dist);
            walker.TeleportTo(from, Mathf.Repeat(bearing + 180f, 360f));
        }

        // Test hook: stand in parking lot i, on beach i, or 7 m outside walled building i facing it.
        void Goto(string what, int i, int j = 0, float bearing = float.NaN)
        {
            if (what == "style")
            {
                // stand 26 m from the j-th building of a style (1 lodge, 2 brick flat, 3 shelter, 4 civic), looking at it
                foreach (var o in data.outlines)
                {
                    if (o.kind != 4 || (i == 3 ? o.type != 3 : o.style != i) || j-- > 0) continue;
                    var c = o.bounds.center;
                    // bearing = compass direction from the building to the viewer (clockwise from north); default is south-south-east
                    var dir = float.IsNaN(bearing) ? new Vector2(0.45f, -0.9f).normalized : new Vector2(Mathf.Sin(bearing * Mathf.Deg2Rad), Mathf.Cos(bearing * Mathf.Deg2Rad));
                    var from = c + dir * (Mathf.Max(o.bounds.width, o.bounds.height) * 0.5f + 20f);
                    var d = c - from;
                    walker.TeleportTo(from, Mathf.Repeat(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg, 360));
                    return;
                }
                return;
            }
            if (what == "parking" || what == "beach")
            {
                var list = what == "parking" ? data.info.parking : data.info.beaches;
                var p = list[Mathf.Clamp(i, 0, list.Length - 1)];
                walker.TeleportTo(new Vector2(p.x, p.z), walker.Yaw);
                return;
            }
            foreach (var o in data.outlines)
            {
                if (o.kind != 4 || o.type == 3 || o.bounds.width < 5 || data.FeatureAt(o.bounds.center.x, o.bounds.center.y) != 4 || i-- > 0) continue;
                var c = o.bounds.center;
                foreach (var dir in new[] { Vector2.up, Vector2.right, Vector2.down, Vector2.left })
                {
                    var start = c + dir * (Mathf.Max(o.bounds.width, o.bounds.height) * 0.5f + 7f);
                    if (data.FeatureAt(start.x, start.y) == 4 || data.LakeAt(start.x, start.y) != null) continue;
                    var d = c - start;
                    walker.TeleportTo(start, Mathf.Repeat(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg, 360));
                    return;
                }
            }
        }

        // Stand just outside the biggest lake, facing its middle (for the water-blocking test).
        void Shore()
        {
            Lake big = null;
            foreach (var l in data.lakes) if (big == null || l.bounds.width * l.bounds.height > big.bounds.width * big.bounds.height) big = l;
            var c = Vector2.zero; foreach (var p in big.pts) c += p; c /= big.pts.Length;
            for (int i = 0; i < big.pts.Length; i += 7)
            {
                var p = big.pts[i];
                var start = p + (p - c).normalized * 10f;
                if (data.LakeAt(start.x, start.y) != null || data.LakeAt(p.x + (c.x - p.x) * 0.02f, p.y + (c.y - p.y) * 0.02f) == null) continue;
                var d = c - start;
                walker.TeleportTo(start, Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg);
                return;
            }
        }

        void SetSun(float elevation) => sun.transform.rotation = Quaternion.Euler(elevation, sun.transform.eulerAngles.y, 0);
    }
}
