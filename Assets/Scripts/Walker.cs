using System.Collections.Generic;
using UnityEngine;

namespace StonyCreek
{
    /// First-person walking with keyboard + mouse drag, or a touch thumbstick + drag to look.
    /// Also routes every pointer to the HUD first so buttons, the minimap and the full map work by touch.
    public class Walker : MonoBehaviour
    {
        public Terrain terrain;
        public ForestRenderer forest;
        public MapHUD hud;
        public float walkSpeed = 2.2f, jogSpeed = 6f, eyeHeight = 1.68f;

        public float Yaw { get; private set; }          // degrees clockwise from north
        public float Pitch { get; private set; }
        public bool Jog { get; set; }
        public Vector2 Position2 => new(transform.position.x, transform.position.z);
        public bool Started { get; set; }
        public Vector2 Stick => stick;
        public Vector2 StickOrigin => stickOrigin;
        public bool StickActive => stickId != int.MinValue;

        ParkData data;
        Vector2 stick, stickOrigin;
        int stickId = int.MinValue, lookId = int.MinValue;
        Vector2 lastLook;
        float bob, groundY;
        bool touchMode;

        public struct PointerEvent { public int id; public Vector2 pos; public TouchPhase phase; }
        readonly List<PointerEvent> events = new();
        readonly HashSet<int> liveTouches = new();

        public void Init(ParkData d)
        {
            data = d;
            Input.simulateMouseWithTouches = false;
            touchMode = Input.touchSupported && Application.isMobilePlatform;
        }

        public bool TouchMode => touchMode;

        public void TeleportTo(Vector2 p, float yaw)
        {
            var td = terrain.terrainData;
            groundY = td.GetInterpolatedHeight(p.x / data.info.width, p.y / data.info.depth);
            transform.position = new Vector3(p.x, groundY + eyeHeight, p.y);
            Yaw = yaw; Pitch = -2;
            ApplyRotation();
        }

        void ApplyRotation() => transform.rotation = Quaternion.Euler(-Pitch, Yaw, 0);

        static Vector2 Gui(Vector2 screen) => new(screen.x, Screen.height - screen.y);

        void GatherPointers()
        {
            events.Clear();
            if (Input.touchCount > 0) touchMode = true;
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                var p = Gui(t.position);
                bool ending = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                // a tap shorter than one frame can arrive as a bare "ended": give it its start
                if (t.phase != TouchPhase.Began && !liveTouches.Contains(t.fingerId))
                    events.Add(new PointerEvent { id = t.fingerId, pos = p, phase = TouchPhase.Began });
                events.Add(new PointerEvent { id = t.fingerId, pos = p, phase = t.phase });
                if (ending) liveTouches.Remove(t.fingerId); else liveTouches.Add(t.fingerId);
            }
            if (Input.touchCount == 0)
            {
                liveTouches.Clear();
                var mp = Gui(Input.mousePosition);
                bool down = Input.GetMouseButtonDown(0), up = Input.GetMouseButtonUp(0);
                // press and release can land in the same frame on a slow device: report both
                if (down) events.Add(new PointerEvent { id = -1, pos = mp, phase = TouchPhase.Began });
                if (Input.GetMouseButton(0) && !down) events.Add(new PointerEvent { id = -1, pos = mp, phase = TouchPhase.Moved });
                if (up) events.Add(new PointerEvent { id = -1, pos = mp, phase = TouchPhase.Ended });
            }
        }

        void Update()
        {
            if (data == null) return;
            GatherPointers();
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float lookX = 0, lookY = 0;
            float ratio = hud.PixelRatio;

            foreach (var e in events)
            {
                bool ending = e.phase == TouchPhase.Ended || e.phase == TouchPhase.Canceled;
                if (hud.HandlePointer(e)) { if (e.id == stickId && ending) stickId = int.MinValue; continue; }
                if (!Started) continue;
                if (e.phase == TouchPhase.Began)
                {
                    bool leftZone = e.pos.x < Screen.width * 0.42f && e.pos.y > Screen.height * 0.3f;
                    if (touchMode && e.id >= 0 && leftZone && stickId == int.MinValue) { stickId = e.id; stickOrigin = e.pos; stick = Vector2.zero; }
                    else if (lookId == int.MinValue) { lookId = e.id; lastLook = e.pos; }
                }
                else if (e.phase == TouchPhase.Moved || e.phase == TouchPhase.Stationary)
                {
                    if (e.id == stickId)
                    {
                        float max = 60f * hud.UiScale;
                        var v = e.pos - stickOrigin;
                        stick = Vector2.ClampMagnitude(v / max, 1f);
                        stick.y = -stick.y; // up on screen = forward
                    }
                    else if (e.id == lookId)
                    {
                        var dlt = (e.pos - lastLook) / ratio;
                        lookX += dlt.x; lookY += dlt.y; lastLook = e.pos;
                    }
                }
                if (ending)
                {
                    if (e.id == stickId) { stickId = int.MinValue; stick = Vector2.zero; }
                    if (e.id == lookId) lookId = int.MinValue;
                }
            }

            if (!Started || hud.MapOpen) { stick = StickActive ? stick : Vector2.zero; return; }

            float sens = touchMode ? 0.26f : 0.2f;
            Yaw = Mathf.Repeat(Yaw + lookX * sens, 360f);
            Pitch = Mathf.Clamp(Pitch - lookY * sens, -80f, 80f);
            float turn = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            Yaw = Mathf.Repeat(Yaw + turn * 90f * dt, 360f);

            float fx = 0, fz = 0;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) fz += 1;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) fz -= 1;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) fx += 1;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) fx -= 1;
            var move = Vector2.ClampMagnitude(new Vector2(fx, fz) + stick, 1f);
            bool jogging = Jog || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float speed = jogging ? jogSpeed : walkSpeed;

            float yr = Yaw * Mathf.Deg2Rad;
            var forward = new Vector2(Mathf.Sin(yr), Mathf.Cos(yr));   // (east, north)
            var right = new Vector2(Mathf.Cos(yr), -Mathf.Sin(yr));
            var step = (forward * move.y + right * move.x) * speed * dt;
            var pos = Position2;
            var next = TryMove(pos, step);
            if (forest != null) next = forest.PushOut(next, 0.45f);
            bool moving = (next - pos).sqrMagnitude > 1e-6f;

            var td = terrain.terrainData;
            float target = td.GetInterpolatedHeight(next.x / data.info.width, next.y / data.info.depth);
            groundY = Mathf.Lerp(groundY, target, 1 - Mathf.Exp(-dt * 14f));
            bob = moving ? bob + dt * speed * 2.1f : Mathf.MoveTowards(bob, Mathf.Round(bob / Mathf.PI) * Mathf.PI, dt * 3);
            float bobY = Mathf.Sin(bob * 2) * (jogging ? 0.05f : 0.025f);
            transform.position = new Vector3(next.x, groundY + eyeHeight + bobY, next.y);
            ApplyRotation();
        }

        Vector2 TryMove(Vector2 from, Vector2 step)
        {
            bool Ok(Vector2 p) =>
                p.x > 6 && p.y > 6 && p.x < data.info.width - 6 && p.y < data.info.depth - 6 && data.LakeAt(p.x, p.y) == null && !data.InBuilding(p.x, p.y);
            var full = from + step;
            if (Ok(full)) return full;
            var sx = from + new Vector2(step.x, 0);
            if (Ok(sx)) return sx;
            var sz = from + new Vector2(0, step.y);
            if (Ok(sz)) return sz;
            return from;
        }
    }
}
