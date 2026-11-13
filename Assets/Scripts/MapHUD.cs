using System.Collections.Generic;
using UnityEngine;

namespace StonyCreek
{
    /// GTA-style HUD: rotating radar minimap with compass ring, area name, full-screen pause map
    /// with waypoints and a purple GPS route along the real trail network, plus touch buttons.
    public class MapHUD : MonoBehaviour
    {
        public Material mapView, overlay;
        public Walker walker;
        public StonyExplorer explorer;

        ParkData data;
        public bool MapOpen { get; private set; }
        public float UiScale { get; private set; } = 1;
        public float PixelRatio { get; set; } = 1;
        public Rect MiniRect => miniRect;
        public Rect MapButtonRect => mapButton;
        public Rect JogButtonRect => jogButton;
        public string ButtonsJson { get; private set; } = "[]";

        // waypoint + route
        public bool HasWaypoint { get; private set; }
        public Vector2 Waypoint { get; private set; }
        public float RouteLength { get; private set; }
        List<Vector2> route;
        float routeTimer;
        string toast; float toastUntil;

        // full map view state (metres)
        Vector2 mapCenter; float mapSpan = 1200;
        readonly Dictionary<int, Vector2> mapPointers = new();
        readonly Dictionary<int, Vector2> pointerStart = new();
        readonly Dictionary<int, float> pointerTime = new();
        float pinchDist;
        bool mapDragged;

        // layout (GUI space, y down)
        Rect miniRect, mapButton, jogButton, helpRect;
        readonly List<(Rect rect, System.Action act)> mapButtons = new();
        readonly List<(Rect rect, int place)> placeHits = new();
        int pressedButtonId = int.MinValue;
        Rect pressedRect;

        float miniSpan = 190;
        string area = "", lastArea = "";
        float areaTimer, areaShown;
        Texture2D white;
        GUIStyle label, labelBold, small;
        bool showHelp = true;

        public void Init(ParkData d)
        {
            data = d;
            white = Texture2D.whiteTexture;
            mapView.mainTexture = data.map;
            mapView.SetVector("_World", new Vector4(data.info.width, data.info.depth, 0, 0));
        }

        float S(float v) => v * UiScale;

        void Layout()
        {
            float css = Mathf.Min(Screen.width, Screen.height) / Mathf.Max(PixelRatio, 0.5f);
            float f = walker.TouchMode ? Mathf.Clamp(css / 430f, 0.85f, 1.3f) : Mathf.Clamp(css / 760f, 0.8f, 1.45f);
            UiScale = PixelRatio * f;
            float m = S(18);
            float dia = walker.TouchMode ? S(150) : S(196);
            miniRect = walker.TouchMode ? new Rect(m, m, dia, dia) : new Rect(m, Screen.height - dia - m - S(34), dia, dia);
            float bw = S(walker.TouchMode ? 68 : 62), bh = S(walker.TouchMode ? 52 : 40);
            mapButton = new Rect(Screen.width - m - bw, m, bw, bh);
            jogButton = new Rect(Screen.width - m - bw * 2 - S(8), m, bw, bh);
            if (label == null)
            {
                label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false, richText = false };
                labelBold = new GUIStyle(label) { fontStyle = FontStyle.Bold };
                small = new GUIStyle(label);
            }
            label.fontSize = Mathf.RoundToInt(S(15)); labelBold.fontSize = Mathf.RoundToInt(S(15)); small.fontSize = Mathf.RoundToInt(S(12));
        }

        public void SetWaypoint(Vector2 p)
        {
            Waypoint = p; HasWaypoint = true; routeTimer = 0;
            route = data.Route(walker.Position2, p, out float len); RouteLength = len;
        }

        public void ClearWaypoint() { HasWaypoint = false; route = null; RouteLength = 0; }

        public void ToggleMap(bool open)
        {
            MapOpen = open;
            if (open) { mapCenter = walker.Position2; mapSpan = 900; }
            mapPointers.Clear();
        }

        public void Toast(string text) { toast = text; toastUntil = Time.unscaledTime + 3.2f; }

        void Update()
        {
            if (data == null) return;
            if (Input.GetKeyDown(KeyCode.M) && walker.Started) ToggleMap(!MapOpen);
            if (Input.GetKeyDown(KeyCode.Escape) && MapOpen) ToggleMap(false);
            if (Input.GetKeyDown(KeyCode.J)) walker.TogglePace(Walker.Pace.Jog);
            if (Input.GetKeyDown(KeyCode.R)) walker.TogglePace(Walker.Pace.Run);
            if (Input.GetKeyDown(KeyCode.F)) walker.TogglePace(Walker.Pace.Fly);
            if (Input.GetKeyDown(KeyCode.C)) ClearWaypoint();
            if (Input.GetKeyDown(KeyCode.H)) showHelp = !showHelp;
            if (MapOpen)
            {
                if (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus)) mapSpan *= 1 - Time.deltaTime * 1.5f;
                if (Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus)) mapSpan *= 1 + Time.deltaTime * 1.5f;
                float pan = mapSpan * Time.deltaTime;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) mapCenter.y += pan;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) mapCenter.y -= pan;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) mapCenter.x += pan;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) mapCenter.x -= pan;
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f) ZoomAt(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y), Mathf.Pow(0.88f, wheel));
                ClampMap();
            }

            // radar range opens up when jogging
            float wantSpan = walker.Mode == Walker.Pace.Fly ? 900 + walker.Altitude * 2 : walker.Mode == Walker.Pace.Run ? 520 : walker.Jog || Input.GetKey(KeyCode.LeftShift) ? 280 : 180;
            miniSpan = Mathf.Lerp(miniSpan, wantSpan, 1 - Mathf.Exp(-Time.deltaTime * 2));

            if (HasWaypoint)
            {
                float d = Vector2.Distance(walker.Position2, Waypoint);
                if (d < 14) { ClearWaypoint(); Toast("You have arrived"); }
                else if ((routeTimer -= Time.deltaTime) <= 0)
                {
                    routeTimer = 1.2f;
                    route = data.Route(walker.Position2, Waypoint, out float len); RouteLength = len;
                }
            }
            if ((areaTimer -= Time.deltaTime) <= 0)
            {
                areaTimer = 0.5f;
                area = data.NearbyName(walker.Position2);
                if (string.IsNullOrEmpty(area)) area = NearestPlaceName();
                if (area != lastArea) { lastArea = area; areaShown = Time.unscaledTime; }
            }
        }

        string NearestPlaceName()
        {
            string best = "Stony Creek Metropark"; float bd = 600;
            foreach (var p in data.info.places)
            {
                float d = Vector2.Distance(walker.Position2, new Vector2(p.x, p.z));
                if (d < bd) { bd = d; best = p.name; }
            }
            return best;
        }

        // ---------- pointer routing (called by Walker before it uses a pointer) ----------
        public bool HandlePointer(Walker.PointerEvent e)
        {
            bool began = e.phase == TouchPhase.Began, ended = e.phase == TouchPhase.Ended || e.phase == TouchPhase.Canceled;
            if (!walker.Started)
            {
                if (ended) explorer.BeginWalk();
                return true;
            }
            if (MapOpen) { MapPointer(e, began, ended); return true; }
            if (began)
            {
                if (Hit(miniRect, e.pos, S(6)) || mapButton.Contains(e.pos) || jogButton.Contains(e.pos))
                {
                    pressedButtonId = e.id; pressedRect = Hit(miniRect, e.pos, S(6)) ? miniRect : mapButton.Contains(e.pos) ? mapButton : jogButton;
                    return true;
                }
                return false;
            }
            if (e.id == pressedButtonId)
            {
                if (ended)
                {
                    pressedButtonId = int.MinValue;
                    if (pressedRect.Contains(e.pos) || Hit(miniRect, e.pos, S(10)) && pressedRect == miniRect)
                    {
                        if (pressedRect == jogButton) walker.NextPace();
                        else ToggleMap(true);
                    }
                }
                return true;
            }
            return false;
        }

        static bool Hit(Rect circle, Vector2 p, float pad) => Vector2.Distance(circle.center, p) <= circle.width / 2 + pad;

        void MapPointer(Walker.PointerEvent e, bool began, bool ended)
        {
            if (began)
            {
                mapPointers[e.id] = e.pos; pointerStart[e.id] = e.pos; pointerTime[e.id] = Time.unscaledTime;
                if (mapPointers.Count == 1) mapDragged = false;
                if (mapPointers.Count == 2) pinchDist = PinchDistance();
                return;
            }
            if (!mapPointers.ContainsKey(e.id)) return;
            if (!ended)
            {
                var prev = mapPointers[e.id];
                mapPointers[e.id] = e.pos;
                if (mapPointers.Count == 1)
                {
                    if ((e.pos - pointerStart[e.id]).magnitude > S(8)) mapDragged = true;
                    if (mapDragged)
                    {
                        float k = mapSpan / (Screen.height / 2f);
                        mapCenter += new Vector2(-(e.pos.x - prev.x), e.pos.y - prev.y) * k;
                    }
                }
                else if (mapPointers.Count == 2)
                {
                    mapDragged = true;
                    float dnow = PinchDistance();
                    if (pinchDist > 1 && dnow > 1) ZoomAt(PinchCenter(), pinchDist / dnow);
                    pinchDist = dnow;
                }
                ClampMap();
                return;
            }
            // pointer released
            bool tap = !mapDragged && mapPointers.Count == 1 && (e.pos - pointerStart[e.id]).magnitude < S(10) && Time.unscaledTime - pointerTime[e.id] < 0.6f;
            mapPointers.Remove(e.id);
            if (mapPointers.Count == 1) foreach (var k in mapPointers.Keys) pointerStart[k] = mapPointers[k];
            if (!tap) return;
            foreach (var (rect, act) in mapButtons) if (rect.Contains(e.pos)) { act(); return; }
            foreach (var (rect, place) in placeHits)
                if (rect.Contains(e.pos)) { var p = data.info.places[place]; SetWaypoint(new Vector2(p.x, p.z)); return; }
            var w = MapToWorld(e.pos);
            if (HasWaypoint && (WorldToMap(Waypoint) - e.pos).magnitude < S(22)) { ClearWaypoint(); return; }
            if (w.x > 0 && w.y > 0 && w.x < data.info.width && w.y < data.info.depth) SetWaypoint(w);
        }

        float PinchDistance() { var v = new List<Vector2>(mapPointers.Values); return v.Count < 2 ? 0 : Vector2.Distance(v[0], v[1]); }
        Vector2 PinchCenter() { var v = new List<Vector2>(mapPointers.Values); return (v[0] + v[1]) / 2; }

        void ZoomAt(Vector2 gui, float factor)
        {
            var before = MapToWorld(gui);
            mapSpan = Mathf.Clamp(mapSpan * factor, 120, Mathf.Max(data.info.width, data.info.depth) * 0.62f);
            var after = MapToWorld(gui);
            mapCenter += before - after;
        }

        void ClampMap()
        {
            mapCenter.x = Mathf.Clamp(mapCenter.x, 0, data.info.width);
            mapCenter.y = Mathf.Clamp(mapCenter.y, 0, data.info.depth);
        }

        Vector2 MapToWorld(Vector2 gui)
        {
            float k = mapSpan / (Screen.height / 2f);
            return mapCenter + new Vector2(gui.x - Screen.width / 2f, Screen.height / 2f - gui.y) * k;
        }

        Vector2 WorldToMap(Vector2 w)
        {
            float k = (Screen.height / 2f) / mapSpan;
            return new Vector2(Screen.width / 2f + (w.x - mapCenter.x) * k, Screen.height / 2f - (w.y - mapCenter.y) * k);
        }

        Vector2 WorldToMini(Vector2 w)
        {
            float b = walker.Yaw * Mathf.Deg2Rad;
            var o = w - walker.Position2;
            float sx = o.x * Mathf.Cos(b) - o.y * Mathf.Sin(b);
            float sy = o.x * Mathf.Sin(b) + o.y * Mathf.Cos(b);
            float k = (miniRect.width / 2f) / miniSpan;
            return miniRect.center + new Vector2(sx, -sy) * k;
        }

        // ---------- drawing ----------
        void OnGUI()
        {
            if (data == null) return;
            if (Event.current.type == EventType.Layout) Layout();
            if (Event.current.type != EventType.Repaint) return;
            Layout();
            if (!walker.Started) { DrawStart(); return; }
            if (MapOpen) DrawFullMap();
            else DrawRadar();
            if (!string.IsNullOrEmpty(toast) && Time.unscaledTime < toastUntil)
            {
                var r = new Rect(Screen.width / 2f - S(170), S(84), S(340), S(44));
                Panel(r, new Color(0.04f, 0.06f, 0.05f, 0.82f));
                Text(r, toast, labelBold, Color.white, TextAnchor.MiddleCenter);
            }
        }

        void Panel(Rect r, Color c, float radius = -1) =>
            GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0, c, 0, radius < 0 ? S(10) : radius);

        void Text(Rect r, string s, GUIStyle style, Color c, TextAnchor a = TextAnchor.MiddleLeft, bool shadow = true)
        {
            style.alignment = a;
            if (shadow) { style.normal.textColor = new Color(0, 0, 0, 0.7f * c.a); GUI.Label(new Rect(r.x + S(1.2f), r.y + S(1.2f), r.width, r.height), s, style); }
            style.normal.textColor = c; GUI.Label(r, s, style);
        }

        static string Cardinal(float yaw)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[Mathf.RoundToInt(Mathf.Repeat(yaw, 360) / 45f) % 8];
        }

        static string Distance(float metres)
        {
            float feet = metres * 3.28084f;
            return feet < 1000 ? $"{Mathf.RoundToInt(feet / 10) * 10} ft" : $"{metres / 1609.34f:0.0} mi";
        }

        void DrawRadar()
        {
            var r = miniRect;
            mapView.SetVector("_Center", new Vector4(walker.Position2.x, walker.Position2.y, 0, 0));
            mapView.SetFloat("_Bearing", walker.Yaw * Mathf.Deg2Rad);
            mapView.SetFloat("_Span", miniSpan);
            mapView.SetFloat("_Aspect", 1);
            mapView.SetFloat("_Circle", 1);
            GUI.DrawTexture(new Rect(r.x - S(3), r.y + S(2), r.width + S(6), r.height + S(6)), white, ScaleMode.StretchToFill, true, 0, new Color(0, 0, 0, 0.35f), 0, r.width);
            Graphics.DrawTexture(r, data.map, mapView);

            // overlays clipped to the inner circle
            float inner = r.width / 2f * 0.84f;
            BeginGL(r.center, inner);
            if (route != null && route.Count > 1)
            {
                var pts = new List<Vector2>(route.Count);
                foreach (var p in route) pts.Add(WorldToMini(p));
                Polyline(pts, S(5.5f), new Color(0.12f, 0.02f, 0.2f, 0.65f));
                Polyline(pts, S(3.4f), new Color(0.72f, 0.33f, 1f, 1f));
            }
            foreach (var p in data.info.places)
            {
                var s = WorldToMini(new Vector2(p.x, p.z));
                if ((s - r.center).magnitude < inner - S(6)) { Disc(s, S(5.5f), new Color(0.05f, 0.1f, 0.07f, 0.9f)); Disc(s, S(3.8f), new Color(0.44f, 0.86f, 0.56f)); }
            }
            EndGL();
            BeginGL(r.center, r.width);
            if (HasWaypoint)
            {
                var s = WorldToMini(Waypoint);
                var off = s - r.center;
                float lim = inner - S(4);
                if (off.magnitude > lim) s = r.center + off.normalized * lim;
                Diamond(s, S(8.5f), new Color(0.08f, 0.06f, 0.02f, 0.9f));
                Diamond(s, S(6.5f), new Color(1f, 0.8f, 0.2f));
            }
            Arrow(r.center, 0, S(10), Color.white, new Color(0.06f, 0.08f, 0.07f, 0.95f));
            EndGL();

            // compass letters on the ring
            float ringR = r.width / 2f * 0.92f;
            string[] letters = { "N", "E", "S", "W" };
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90 - walker.Yaw) * Mathf.Deg2Rad;
                var c = r.center + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * ringR;
                var lr = new Rect(c.x - S(12), c.y - S(12), S(24), S(24));
                if (i == 0) GUI.DrawTexture(new Rect(c.x - S(9.5f), c.y - S(9.5f), S(19), S(19)), white, ScaleMode.StretchToFill, true, 0, new Color(0.84f, 0.15f, 0.12f), 0, S(9.5f));
                var st = i == 0 ? labelBold : small;
                Text(lr, letters[i], st, Color.white, TextAnchor.MiddleCenter, i != 0);
            }

            // heading + area name under the radar (GTA puts the street name here)
            var hr = walker.TouchMode ? new Rect(r.x, r.yMax + S(6), S(320), S(22)) : new Rect(r.x, r.yMax + S(6), S(320), S(22));
            Text(hr, $"{Cardinal(walker.Yaw)}  {Mathf.RoundToInt(walker.Yaw) % 360:000}°", labelBold, Color.white);
            float age = Time.unscaledTime - areaShown;
            var ar = new Rect(r.xMax + S(12), r.yMax - S(46), S(360), S(26));
            if (walker.TouchMode) ar = new Rect(r.x, hr.yMax, S(320), S(22));
            Text(ar, area, labelBold, new Color(1, 1, 1, Mathf.Clamp01(1.6f - age * 0.08f) * 0.6f + 0.4f));
            if (HasWaypoint)
            {
                var wr = walker.TouchMode ? new Rect(r.x, ar.yMax, S(320), S(22)) : new Rect(r.xMax + S(12), r.yMax - S(22), S(360), S(22));
                Text(wr, $"Waypoint  {Distance(RouteLength > 0 ? RouteLength : Vector2.Distance(walker.Position2, Waypoint))}", small, new Color(1f, 0.85f, 0.4f));
            }

            // buttons
            Button(mapButton, "MAP", false);
            Button(jogButton, walker.Mode == Walker.Pace.Fly ? "FLY" : walker.Mode == Walker.Pace.Run ? "RUN" : walker.Jog ? "JOG" : "WALK", walker.Mode != Walker.Pace.Walk);
            if (walker.TouchMode && walker.StickActive)
            {
                float rad = S(60);
                var o = walker.StickOrigin;
                GUI.DrawTexture(new Rect(o.x - rad, o.y - rad, rad * 2, rad * 2), white, ScaleMode.StretchToFill, true, 0, new Color(1, 1, 1, 0.16f), 0, rad);
                var t = o + new Vector2(walker.Stick.x, -walker.Stick.y) * rad;
                GUI.DrawTexture(new Rect(t.x - S(24), t.y - S(24), S(48), S(48)), white, ScaleMode.StretchToFill, true, 0, new Color(1, 1, 1, 0.55f), 0, S(24));
            }
            if (showHelp)
            {
                string help = walker.TouchMode
                    ? "Left thumb: walk  ·  Drag right side: look  ·  WALK button: jog, run, fly  ·  Tap the radar for the map"
                    : "WASD walk  ·  Drag to look  ·  Shift / J jog  ·  R run  ·  F fly (Space up, Ctrl down)  ·  M map  ·  H hide help";
                var hrect = new Rect(Screen.width / 2f - S(300), Screen.height - S(40), S(600), S(26));
                Text(hrect, help, small, new Color(1, 1, 1, 0.85f), TextAnchor.MiddleCenter);
            }
        }

        void Button(Rect r, string text, bool on)
        {
            Panel(r, on ? new Color(0.78f, 0.86f, 0.72f, 0.92f) : new Color(0.05f, 0.08f, 0.06f, 0.72f));
            Text(r, text, labelBold, on ? new Color(0.08f, 0.16f, 0.1f) : Color.white, TextAnchor.MiddleCenter, !on);
        }

        void DrawFullMap()
        {
            var full = new Rect(0, 0, Screen.width, Screen.height);
            mapView.SetVector("_Center", new Vector4(mapCenter.x, mapCenter.y, 0, 0));
            mapView.SetFloat("_Bearing", 0);
            mapView.SetFloat("_Span", mapSpan);
            mapView.SetFloat("_Aspect", Screen.width / (float)Screen.height);
            mapView.SetFloat("_Circle", 0);
            Graphics.DrawTexture(full, data.map, mapView);

            BeginGL(Vector2.zero, 0);
            if (route != null && route.Count > 1)
            {
                var pts = new List<Vector2>(route.Count);
                foreach (var p in route) pts.Add(WorldToMap(p));
                Polyline(pts, S(7), new Color(0.12f, 0.02f, 0.2f, 0.6f));
                Polyline(pts, S(4.5f), new Color(0.72f, 0.33f, 1f, 1f));
            }
            foreach (var p in data.info.places)
            {
                var s = WorldToMap(new Vector2(p.x, p.z));
                Disc(s, S(9), new Color(0.05f, 0.1f, 0.07f, 0.9f)); Disc(s, S(6.5f), new Color(0.44f, 0.86f, 0.56f));
            }
            if (HasWaypoint)
            {
                var s = WorldToMap(Waypoint);
                Diamond(s, S(12), new Color(0.08f, 0.06f, 0.02f, 0.9f)); Diamond(s, S(9.5f), new Color(1f, 0.8f, 0.2f));
            }
            Arrow(WorldToMap(walker.Position2), walker.Yaw, S(13), Color.white, new Color(0.06f, 0.08f, 0.07f, 0.95f));
            EndGL();

            // labels
            placeHits.Clear();
            for (int i = 0; i < data.info.places.Length; i++)
            {
                var p = data.info.places[i];
                var s = WorldToMap(new Vector2(p.x, p.z));
                var r = new Rect(s.x + S(12), s.y - S(13), S(240), S(26));
                Text(r, p.name, labelBold, Color.white);
                placeHits.Add((new Rect(s.x - S(14), s.y - S(16), S(200), S(32)), i));
            }
            if (mapSpan < 1300 && data.info.parking != null)
                foreach (var l in data.info.parking)
                {
                    var s = WorldToMap(new Vector2(l.x, l.z));
                    if (s.x < -20 || s.y < -20 || s.x > Screen.width + 20 || s.y > Screen.height + 20) continue;
                    var pr = new Rect(s.x - S(9), s.y - S(9), S(18), S(18));
                    GUI.DrawTexture(pr, white, ScaleMode.StretchToFill, true, 0, new Color(0.16f, 0.36f, 0.72f, 0.95f), 0, S(4));
                    Text(pr, "P", small, Color.white, TextAnchor.MiddleCenter, false);
                }
            if (mapSpan < 1600)
                foreach (var l in data.info.landmarks)
                {
                    var s = WorldToMap(new Vector2(l.x, l.z));
                    if (s.x < -50 || s.y < -50 || s.x > Screen.width + 50 || s.y > Screen.height + 50) continue;
                    Text(new Rect(s.x - S(90), s.y - S(10), S(180), S(20)), l.name, small, new Color(1, 1, 1, 0.82f), TextAnchor.MiddleCenter);
                }

            // narrow (portrait phone) screens stack the panels instead of putting them side by side
            bool narrow = Screen.width / UiScale < 640;
            // header
            var head = new Rect(S(16), S(14), narrow ? Screen.width - S(32) : S(360), S(64));
            Panel(head, new Color(0.04f, 0.06f, 0.05f, 0.8f));
            Text(new Rect(head.x + S(14), head.y + S(6), head.width, S(28)), "STONY CREEK METROPARK", labelBold, Color.white);
            Text(new Rect(head.x + S(14), head.y + S(32), head.width, S(24)),
                HasWaypoint ? $"Waypoint · {Distance(RouteLength)} by trail" : (walker.TouchMode ? "Tap to set a waypoint" : "Click to set a waypoint · drag to pan · scroll to zoom"),
                small, new Color(0.85f, 0.9f, 0.85f));

            // legend
            var leg = new Rect(S(16), head.yMax + S(8), S(200), S(126));
            if (!narrow && (!walker.TouchMode || Screen.height / UiScale > 420))
            {
                Panel(leg, new Color(0.04f, 0.06f, 0.05f, 0.72f));
                (Color c, string t)[] keys = { (new Color(0.94f, 0.93f, 0.89f), "Road"), (new Color(0.89f, 0.7f, 0.35f), "Paved bike trail"), (new Color(0.48f, 0.38f, 0.25f), "Hiking trail"), (new Color(0.33f, 0.56f, 0.7f), "Water"), (new Color(0.72f, 0.33f, 1f), "GPS route") };
                for (int i = 0; i < keys.Length; i++)
                {
                    var row = new Rect(leg.x + S(12), leg.y + S(10) + i * S(21.5f), S(170), S(20));
                    GUI.DrawTexture(new Rect(row.x, row.y + S(7), S(22), S(6)), white, ScaleMode.StretchToFill, true, 0, keys[i].c, 0, S(3));
                    Text(new Rect(row.x + S(30), row.y, S(150), row.height), keys[i].t, small, Color.white);
                }
            }

            // buttons along the bottom
            mapButtons.Clear();
            float bh = S(walker.TouchMode ? 50 : 42), gap = S(8);
            var items = new List<(string, System.Action)>
            {
                ("Close", () => ToggleMap(false)),
                (narrow ? "Me" : "Center on me", () => mapCenter = walker.Position2),
                ("+", () => ZoomAt(new Vector2(Screen.width / 2f, Screen.height / 2f), 0.7f)),
                ("-", () => ZoomAt(new Vector2(Screen.width / 2f, Screen.height / 2f), 1.42f)),
            };
            if (HasWaypoint) items.Insert(1, (narrow ? "Clear" : "Clear waypoint", () => ClearWaypoint()));
            float x = S(16);
            foreach (var (t, act) in items)
            {
                float w = Mathf.Max(S(54), labelBold.CalcSize(new GUIContent(t)).x + S(28));
                var r = new Rect(x, Screen.height - bh - S(16), w, bh);
                Button(r, t, false);
                mapButtons.Add((r, act));
                x += w + gap;
            }
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                var b = mapButtons[i].rect;
                sb.Append(i > 0 ? "," : "").Append($"{{\"label\":\"{items[i].Item1}\",\"x\":{b.center.x:0},\"y\":{b.center.y:0}}}");
            }
            ButtonsJson = sb.Append("]").ToString();
            // fast travel list
            float tw = narrow ? Screen.width - S(32) : S(220);
            var tr = new Rect(narrow ? S(16) : Screen.width - tw - S(16), narrow ? head.yMax + S(8) : S(14), tw, S(34) + data.info.places.Length * S(walker.TouchMode ? 44 : 36));
            Panel(tr, new Color(0.04f, 0.06f, 0.05f, 0.78f));
            Text(new Rect(tr.x + S(12), tr.y + S(6), tw, S(24)), "Travel to", labelBold, Color.white);
            for (int i = 0; i < data.info.places.Length; i++)
            {
                int idx = i;
                var r = new Rect(tr.x + S(8), tr.y + S(32) + i * S(walker.TouchMode ? 44 : 36), tw - S(16), S(walker.TouchMode ? 40 : 32));
                Panel(r, new Color(1, 1, 1, 0.08f), S(7));
                Text(new Rect(r.x + S(10), r.y, r.width, r.height), data.info.places[i].name, small, Color.white);
                mapButtons.Add((r, () => { explorer.TravelTo(idx); ToggleMap(false); }));
            }
            Text(new Rect(S(16), Screen.height - bh - S(44), Screen.width, S(20)), narrow ? "© OpenStreetMap · USGS elevation and land cover" : "© OpenStreetMap contributors · USGS 3DEP elevation · USGS NLCD land cover", small, new Color(1, 1, 1, 0.7f));
        }

        void DrawStart()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white, ScaleMode.StretchToFill, true, 0, new Color(0.03f, 0.07f, 0.05f, 0.55f), 0, 0);
            float w = Mathf.Min(Screen.width - S(32), S(620));
            var r = new Rect((Screen.width - w) / 2f, Screen.height / 2f - S(150), w, S(300));
            Panel(r, new Color(0.04f, 0.07f, 0.05f, 0.86f), S(16));
            var big = new GUIStyle(labelBold) { fontSize = Mathf.RoundToInt(S(34)), wordWrap = true };
            var body = new GUIStyle(label) { fontSize = Mathf.RoundToInt(S(15)), wordWrap = true };
            Text(new Rect(r.x + S(28), r.y + S(22), w - S(56), S(46)), "Stony Creek Metropark", big, Color.white);
            Text(new Rect(r.x + S(28), r.y + S(74), w - S(56), S(120)),
                "Unity prototype. Real USGS elevation, OpenStreetMap trails and lakes, and USGS land cover deciding where the 240,000 trees grow. Trees are illustrative, not surveyed.",
                body, new Color(0.88f, 0.92f, 0.86f), TextAnchor.UpperLeft);
            Text(new Rect(r.x + S(28), r.y + S(186), w - S(56), S(50)),
                walker.TouchMode ? "Left thumb walks · drag right side to look · the WALK button jogs, runs and flies · tap the radar for the map" : "WASD to walk · drag to look · Shift to jog · R to run · F to fly · M for the map",
                body, new Color(0.75f, 0.85f, 0.75f), TextAnchor.UpperLeft);
            var b = new Rect(r.x + S(28), r.yMax - S(64), S(220), S(46));
            Panel(b, new Color(0.86f, 0.92f, 0.8f, 1), S(10));
            Text(b, walker.TouchMode ? "Tap to start" : "Click to start", labelBold, new Color(0.08f, 0.18f, 0.1f), TextAnchor.MiddleCenter, false);
        }

        // ---------- GL helpers (GUI coordinates in, flipped to GL pixels) ----------
        void BeginGL(Vector2 clipCenterGui, float clipRadius)
        {
            overlay.SetVector("_Clip", new Vector4(clipCenterGui.x, Screen.height - clipCenterGui.y, clipRadius, clipRadius > 0 ? 1 : 0));
            GL.PushMatrix();
            overlay.SetPass(0);
            GL.LoadPixelMatrix();
            GL.Begin(GL.TRIANGLES);
        }

        static void EndGL() { GL.End(); GL.PopMatrix(); }

        static void V(Vector2 gui, Color c)
        {
            float y = Screen.height - gui.y;
            GL.Color(c); GL.TexCoord2(gui.x, y); GL.Vertex3(gui.x, y, 0);
        }

        static void Tri(Vector2 a, Vector2 b, Vector2 c, Color col) { V(a, col); V(b, col); V(c, col); }

        static void Disc(Vector2 c, float r, Color col)
        {
            const int n = 14;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                Tri(c, c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, col);
            }
        }

        static void Diamond(Vector2 c, float r, Color col)
        {
            Tri(c + Vector2.up * r, c + Vector2.right * r, c + Vector2.down * r, col);
            Tri(c + Vector2.up * r, c + Vector2.down * r, c + Vector2.left * r, col);
        }

        static void Polyline(List<Vector2> pts, float width, Color col)
        {
            float h = width / 2;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                var d = b - a; if (d.sqrMagnitude < 0.01f) continue;
                var n = new Vector2(-d.y, d.x).normalized * h;
                Tri(a + n, b + n, b - n, col); Tri(a + n, b - n, a - n, col);
                Disc(b, h, col);
            }
        }

        /// GTA-style player arrow; yaw in degrees clockwise (0 = up on screen).
        static void Arrow(Vector2 c, float yaw, float size, Color fill, Color edge)
        {
            float a = yaw * Mathf.Deg2Rad;
            Vector2 R(Vector2 v) => c + new Vector2(v.x * Mathf.Cos(a) - v.y * Mathf.Sin(a), v.x * Mathf.Sin(a) + v.y * Mathf.Cos(a));
            Vector2 tip = new(0, -1.15f), l = new(-0.85f, 0.85f), m = new(0, 0.42f), rr = new(0.85f, 0.85f);
            float e = 1.32f;
            Tri(R(tip * size * e), R(l * size * e), R(m * size * e), edge); Tri(R(tip * size * e), R(m * size * e), R(rr * size * e), edge);
            Tri(R(tip * size), R(l * size), R(m * size), fill); Tri(R(tip * size), R(m * size), R(rr * size), fill);
        }
    }
}
