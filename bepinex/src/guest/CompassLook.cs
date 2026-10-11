// CompassLook - the hotspot COMPASS + RANGE FINDER + CAST DISTANCE TRACKER HUD (IMGUI) and the 3D cast-landing marker.
// Shared by the host add-on HotspotCompass<N> (compiled with -define:HOST -> class CompassLook1) and the guest payload
// CompassClient (FISHING.exe -> class CompassLook). Needs only UnityEngine (CoreModule, IMGUIModule, TextRenderingModule,
// PhysicsModule). Every call on the Unity MAIN thread; Draw() only from OnGUI.
//
//   Draw(data, rightX, topY)   round heading-up compass (turns with the camera): N/E/S/W ticks, purple needle + marker
//                              towards the hotspot, gold marker towards the golden far hotspot; under it the range finder
//                              ("Hotspot 84 m", "Golden 212 m", "Aim 72 m") and, for Cast Distance Tracker owners,
//                              last cast distance + live bobber-to-hotspot distance (green "in zone!" inside the radius).
//   Predict(...)               where a cast would land right now (same rule as the host's Fishing<N>.RigCast).
//   MarkerBuild() / MarkerSet() a pulsing ring on the water at the predicted landing point (tracker owners, while aiming).
using System;
using UnityEngine;

#if HOST
public static class CompassLook1
#else
public static class CompassLook
#endif
{
    public class Data
    {
        public float camYaw;                       // degrees, Unity yaw (0 = +Z = north, 90 = +X = east)
        public bool hasMe; public Vector3 me;
        public bool hot; public Vector3 hotPos; public float hotR = 10f;
        public bool gold; public Vector3 goldPos; public float goldR = 8f;
        public bool aim; public float aimDist; public bool aimWater = true; public bool aimHot, aimGold;
        public bool tracker; public string trackerPrice = "$4,000";
        public float lastCast = -1f; public bool lastWater = true;
        public bool lineOut; public Vector3 bobber;
        public bool buyHint = true;
    }

    public static float scale = 1f;
    public static bool visible = true;
    public static float panelWidth = 236f, dialSize = 148f;   // at 1080p
    static Texture2D disc, diamond, arrow, white;
    static GUIStyle lab, small, letter;

    // ---------------------------------------------------------------- textures (generated once)
    static Texture2D Tex(int n, Func<float, float, Color> f)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
        var px = new Color[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) px[j * n + i] = f((i + 0.5f) / n * 2f - 1f, (j + 0.5f) / n * 2f - 1f);
        t.SetPixels(px); t.Apply(); t.hideFlags = HideFlags.DontUnloadUnusedAsset; return t;
    }

    static void Init()
    {
        if (disc != null && lab != null) return;
        if (disc == null)
        {
            disc = Tex(256, (x, y) =>
            {
                float r = Mathf.Sqrt(x * x + y * y);
                if (r > 1f) return new Color(0, 0, 0, 0);
                float edge = Mathf.Clamp01((1f - r) * 90f);                                     // anti-aliased outer edge
                Color c = Color.Lerp(new Color(0.05f, 0.07f, 0.10f, 0.78f), new Color(0.02f, 0.03f, 0.05f, 0.62f), r);
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.955f) * 60f);                 // bright outer ring
                float ring2 = Mathf.Clamp01(1f - Mathf.Abs(r - 0.62f) * 120f) * 0.35f;        // faint inner ring
                c = Color.Lerp(c, new Color(0.75f, 0.85f, 0.95f, 0.9f), ring * 0.8f);
                c = Color.Lerp(c, new Color(0.6f, 0.7f, 0.8f, 0.5f), ring2);
                c.a *= edge; return c;
            });
            diamond = Tex(64, (x, y) =>
            {
                float d = Mathf.Abs(x) + Mathf.Abs(y);
                return new Color(1f, 1f, 1f, Mathf.Clamp01((0.92f - d) * 20f));
            });
            arrow = Tex(64, (x, y) =>
            {
                // triangle pointing UP in GUI space (texture row 0 is the bottom -> tip at y = +1)
                float yy = y;                             // -1 bottom .. +1 top
                float half = (0.95f - yy) * 0.45f;        // width shrinks towards the tip
                bool notch = yy < -0.4f && Mathf.Abs(x) < (-0.4f - yy) * 0.9f;   // arrow-head notch at the back
                bool inside = yy > -0.85f && yy < 0.95f && Mathf.Abs(x) < half && !notch;
                return new Color(1f, 1f, 1f, inside ? 1f : 0f);
            });
            white = Texture2D.whiteTexture;
        }
        lab = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Overflow };
        small = new GUIStyle(lab) { fontSize = 12 };
        letter = new GUIStyle(lab) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
    }

    public static string Dist(float m) { return m >= 1000f ? (m / 1000f).ToString("0.0") + " km" : Mathf.RoundToInt(m) + " m"; }
    public static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }
    public static float Bearing(Vector3 from, Vector3 to) { return Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg; }

    static void Fill(Rect r, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = p; }
    static void Tint(Rect r, Texture t, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(r, t); GUI.color = p; }

    // rect drawn rotated by ang degrees (clockwise) around pivot
    static void Rot(float ang, Vector2 pivot, Rect r, Texture t, Color c)
    {
        Matrix4x4 m = GUI.matrix;
        GUIUtility.RotateAroundPivot(ang, pivot);
        Tint(r, t, c);
        GUI.matrix = m;
    }

    public static readonly Color Purple = new Color(0.78f, 0.49f, 1f), Gold = new Color(1f, 0.80f, 0.25f), Green = new Color(0.40f, 1f, 0.53f);

    public static void Draw(Data d, float rightX, float topY)
    {
        if (!visible || d == null) return;
        Init();
        float s = scale * Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
        lab.fontSize = Mathf.RoundToInt(15 * s); small.fontSize = Mathf.RoundToInt(12 * s); letter.fontSize = Mathf.RoundToInt(14 * s);
        float W = panelWidth * s, D = dialSize * s, R = D / 2f, lh = 20f * s, t = Time.unscaledTime;

        int lines = 1 + (d.gold ? 1 : 0) + (d.aim ? 1 : 0);
        int tlines = d.tracker ? 3 : (d.buyHint ? 1 : 0);
        float H = 8f * s + D + 8f * s + lines * lh + (tlines > 0 ? 6f * s + tlines * lh : 0f) + 8f * s;
        var panel = new Rect(rightX - W, topY, W, H);
        Fill(panel, new Color(0f, 0f, 0f, 0.62f));
        Fill(new Rect(panel.x, panel.y, panel.width, 3f * s), new Color(Purple.r, Purple.g, Purple.b, 0.85f));

        var c = new Vector2(panel.x + W / 2f, panel.y + 8f * s + R);
        GUI.DrawTexture(new Rect(c.x - R, c.y - R, D, D), disc);

        // ticks every 15 deg (cardinal = long), letters N/E/S/W kept upright
        for (int b = 0; b < 360; b += 15)
        {
            bool major = b % 90 == 0, mid = b % 45 == 0;
            float len = (major ? 11f : mid ? 8f : 5f) * s, w = (major ? 2.4f : 1.3f) * s;
            Rot(b - d.camYaw, c, new Rect(c.x - w / 2f, c.y - R + 4f * s, w, len), white, new Color(1f, 1f, 1f, major ? 0.95f : mid ? 0.7f : 0.45f));
        }
        string[] L = { "N", "E", "S", "W" };
        for (int k = 0; k < 4; k++)
        {
            float a = (k * 90f - d.camYaw) * Mathf.Deg2Rad, rr = R - 25f * s;
            var p = new Vector2(c.x + Mathf.Sin(a) * rr, c.y - Mathf.Cos(a) * rr);
            string col = k == 0 ? "#ff6b5b" : "#e8eef5";
            GUI.Label(new Rect(p.x - 12f * s, p.y - 11f * s, 24f * s, 22f * s), "<color=" + col + ">" + L[k] + "</color>", letter);
        }

        // golden far hotspot: gold marker on the rim (+ faint needle)
        if (d.gold && d.hasMe) Marker(d.goldPos, d.goldR, d.me, d.camYaw, c, R, s, t, Gold, false);
        // purple hotspot: needle + marker
        if (d.hot && d.hasMe) Marker(d.hotPos, d.hotR, d.me, d.camYaw, c, R, s, t, Purple, true);

        // you: arrow in the middle, pointing where the camera looks (always up)
        float aw = 15f * s;
        Tint(new Rect(c.x - aw / 2f, c.y - aw * 0.6f, aw, aw * 1.15f), arrow, new Color(1f, 1f, 1f, 0.95f));

        // ---- range finder lines
        float x = panel.x + 12f * s, y = c.y + R + 8f * s, tw = W - 24f * s, ic = 10f * s;
        if (d.hot && d.hasMe)
        {
            float dd = Flat(d.me, d.hotPos);
            Row(x, y, tw, lh, ic, Purple, dd <= d.hotR ? "<color=#d9b3ff>Hotspot</color>  <color=#66ff88><b>you're in it!</b></color>" : "<color=#d9b3ff>Hotspot</color>  <b>" + Dist(dd) + "</b>");
        }
        else Row(x, y, tw, lh, ic, new Color(0.5f, 0.5f, 0.5f), "<color=#999999>Hotspot  -  none on this stage yet</color>");
        y += lh;
        if (d.gold)
        {
            float dd = d.hasMe ? Flat(d.me, d.goldPos) : 0f;
            Row(x, y, tw, lh, ic, Gold, dd <= d.goldR ? "<color=#ffd75e>Golden</color>  <color=#66ff88><b>you're in it!</b></color>" : "<color=#ffd75e>Golden</color>  <b>" + Dist(dd) + "</b>");
            y += lh;
        }
        if (d.aim)
        {
            string where = !d.aimWater ? "  <color=#ff8a7a>ground</color>" : d.aimHot ? "  <color=#66ff88>in hotspot</color>" : d.aimGold ? "  <color=#ffd75e>in golden</color>" : "";
            Cross(x, y, lh, ic);
            GUI.Label(new Rect(x + ic + 8f * s, y, tw - ic - 8f * s, lh), "<color=#cfe8ff>Aim</color>  <b>" + Dist(d.aimDist) + "</b>" + where, lab);
            y += lh;
        }

        // ---- Cast Distance Tracker
        if (tlines > 0)
        {
            y += 2f * s; Fill(new Rect(x, y, tw, 1f * s), new Color(1f, 1f, 1f, 0.18f)); y += 4f * s;
            if (d.tracker)
            {
                GUI.Label(new Rect(x, y, tw, lh), "<color=#5fe0d0><b>CAST DISTANCE TRACKER</b></color>", small); y += lh;
                GUI.Label(new Rect(x, y, tw, lh), d.lastCast < 0f ? "<color=#999999>Last cast  -</color>"
                    : "Last cast  <b>" + Dist(d.lastCast) + "</b>" + (d.lastWater ? "" : "  <color=#ff8a7a>(ground)</color>"), lab);
                y += lh;
                string bob;
                if (!d.lineOut) bob = "<color=#999999>Bobber  -  line in</color>";
                else if (d.gold && Flat(d.bobber, d.goldPos) <= d.goldR) bob = "<color=#ffd75e><b>Bobber in the GOLDEN zone!</b></color>";
                else if (!d.hot) bob = "<color=#999999>Bobber out - no hotspot</color>";
                else
                {
                    float bd = Flat(d.bobber, d.hotPos);
                    bob = bd <= d.hotR ? "<color=#66ff88><b>Bobber " + Dist(bd) + " from hotspot - in zone!</b></color>"
                                       : "Bobber <b>" + Dist(bd) + "</b> from hotspot";
                }
                GUI.Label(new Rect(x, y, tw, lh), bob, lab);
            }
            else GUI.Label(new Rect(x, y, tw, lh), "<color=#8a9aa5>Cast Distance Tracker: <b>!buy tracker</b> (" + d.trackerPrice + ")</color>", small);
        }
    }

    static void Row(float x, float y, float tw, float lh, float ic, Color col, string text)
    {
        Tint(new Rect(x, y + (lh - ic) / 2f, ic, ic), diamond, col);
        GUI.Label(new Rect(x + ic + 8f * (lh / 20f), y, tw - ic, lh), text, lab);
    }

    static void Cross(float x, float y, float lh, float ic)
    {
        float cx = x + ic / 2f, cy = y + lh / 2f, th = Mathf.Max(1f, ic * 0.16f);
        Fill(new Rect(cx - ic / 2f, cy - th / 2f, ic, th), new Color(0.8f, 0.9f, 1f, 0.95f));
        Fill(new Rect(cx - th / 2f, cy - ic / 2f, th, ic), new Color(0.8f, 0.9f, 1f, 0.95f));
    }

    static void Marker(Vector3 target, float radius, Vector3 me, float camYaw, Vector2 c, float R, float s, float t, Color col, bool needle)
    {
        float dist = Flat(me, target);
        if (dist <= radius)   // standing in it: pulsing ring round the centre arrow
        {
            float pr = (16f + 4f * Mathf.Sin(t * 5f)) * s;
            for (int k = 0; k < 24; k++) Rot(k * 15f, c, new Rect(c.x - 1.2f * s, c.y - pr, 2.4f * s, 4f * s), white, col);
            return;
        }
        float ang = Bearing(me, target) - camYaw;
        float rel = Mathf.Abs(Mathf.DeltaAngle(0f, ang));
        float boost = rel < 10f ? 1f + 0.18f * (0.5f + 0.5f * Mathf.Sin(t * 8f)) : 1f;   // facing it: the marker pulses
        if (needle)
        {
            float w = 3f * s;
            Rot(ang, c, new Rect(c.x - w / 2f, c.y - R + 16f * s, w, R - 26f * s), white, new Color(col.r, col.g, col.b, 0.85f));
        }
        else
        {
            float w = 1.6f * s;
            Rot(ang, c, new Rect(c.x - w / 2f, c.y - R + 16f * s, w, R - 26f * s), white, new Color(col.r, col.g, col.b, 0.45f));
        }
        float m = (needle ? 17f : 14f) * s * boost;
        // diamond sitting on the rim, outer glow first
        Rot(ang, c, new Rect(c.x - m * 0.9f, c.y - R - m * 0.4f, m * 1.8f, m * 1.8f), diamond, new Color(col.r, col.g, col.b, 0.25f));
        Rot(ang, c, new Rect(c.x - m / 2f, c.y - R + 0.05f * m, m, m), diamond, col);
    }

    // ---------------------------------------------------------------- cast landing prediction (= Fishing<N>.RigCast, minus pools/auto)
    // range / lobMin already include the rod's range multiplier. Returns false if there is nothing to predict.
    public static bool Predict(Vector3 foot, Vector3 aimO, Vector3 aimD, bool hasWater, float waterY, float range, float lobMin,
                               float pitchMin, float pitchMax, int mask, out Vector3 land, out bool water)
    {
        land = foot; water = false;
        if (aimD.sqrMagnitude < 1e-6f) return false;
        Vector3 dv = aimD.normalized, flat = new Vector3(dv.x, 0f, dv.z);
        if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward; flat.Normalize();
        bool landed = false, ground = false;
        if (dv.y < -0.01f)
        {
            float k = hasWater ? (waterY - aimO.y) / dv.y : 400f;
            RaycastHit h;
            if (Physics.Raycast(aimO, dv, out h, Mathf.Max(0.1f, Mathf.Min(k, 400f)), mask, QueryTriggerInteraction.Ignore) && Flat(h.point, foot) <= range)
            { landed = true; ground = true; land = h.point; }
            if (!landed && hasWater && k > 0f)
            {
                var p = new Vector3(aimO.x + dv.x * k, waterY, aimO.z + dv.z * k);
                if (Flat(p, foot) <= range) { landed = true; land = p; }
            }
        }
        if (!landed)
        {
            float pitch = Mathf.Asin(Mathf.Clamp(dv.y, -1f, 1f)) * Mathf.Rad2Deg;
            float u = Mathf.Clamp01((pitch - pitchMin) / (pitchMax - pitchMin));
            float dist = Mathf.Clamp(Mathf.Lerp(lobMin, range, u), lobMin, range);
            land = new Vector3(foot.x + flat.x * dist, hasWater ? waterY : foot.y, foot.z + flat.z * dist);
        }
        if (!ground)
        {
            float gy;
            if (!hasWater) { ground = true; land.y = Ground(land, foot.y, mask); }
            else if (!OpenWater(land.x, land.z, waterY, mask, out gy)) { ground = true; land.y = gy; }
        }
        water = !ground;
        return true;
    }

    public static bool OpenWater(float x, float z, float waterY, int mask, out float gy)
    {
        gy = waterY; RaycastHit h;
        if (Physics.Raycast(new Vector3(x, waterY + 200f, z), Vector3.down, out h, 200.2f, mask, QueryTriggerInteraction.Ignore) && h.point.y > waterY - 0.2f)
        { gy = h.point.y; return false; }
        return true;
    }

    static float Ground(Vector3 p, float y, int mask)
    {
        RaycastHit h;
        return Physics.Raycast(new Vector3(p.x, y + 50f, p.z), Vector3.down, out h, 400f, mask, QueryTriggerInteraction.Ignore) ? h.point.y : y;
    }

    // ---------------------------------------------------------------- landing marker (3D, local only)
    const int Seg = 48;
    public static GameObject MarkerBuild()
    {
        var go = new GameObject("CompassCastMarker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        Shader sh = Shader.Find("Sprites/Default");
        foreach (string n in new[] { "Ring", "Dot" })
        {
            var g = new GameObject(n); g.transform.SetParent(go.transform, false);
            var lr = g.AddComponent<LineRenderer>();
            lr.useWorldSpace = false; lr.loop = true; lr.positionCount = Seg;
            lr.startWidth = lr.endWidth = n == "Ring" ? 0.14f : 0.1f;
            if (sh != null) lr.material = new Material(sh) { renderQueue = 3100 };
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            float r = n == "Ring" ? 1.6f : 0.35f;
            for (int i = 0; i < Seg; i++) { float a = i * Mathf.PI * 2f / Seg; lr.SetPosition(i, new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r)); }
        }
        go.SetActive(false);
        return go;
    }

    public static void MarkerSet(GameObject go, bool show, Vector3 p, Color col)
    {
        if (go == null) return;
        if (go.activeSelf != show) go.SetActive(show);
        if (!show) return;
        float t = Time.unscaledTime, pulse = 1f + 0.08f * Mathf.Sin(t * 4f);
        go.transform.position = p + Vector3.up * 0.08f;
        go.transform.localScale = new Vector3(pulse, 1f, pulse);
        foreach (LineRenderer lr in go.GetComponentsInChildren<LineRenderer>())
        {
            Color c = col; c.a = lr.name == "Ring" ? 0.95f : 0.8f;
            lr.startColor = lr.endColor = c;
        }
    }

    // ---------------------------------------------------------------- hide a hotspot look (renderers + lights off, sparkles stopped)
    // 'into' (optional) collects what was switched off so it can be switched back on (Show).
    public static int Hide(GameObject root, System.Collections.Generic.List<Component> into)
    {
        if (root == null) return 0;
        int n = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; n++; if (into != null) into.Add(r); }
        foreach (Light l in root.GetComponentsInChildren<Light>(true)) if (l.enabled) { l.enabled = false; n++; if (into != null) into.Add(l); }
        return n;
    }

    public static void Show(System.Collections.Generic.List<Component> list)
    {
        foreach (Component c in list)
        {
            if (c == null) continue;
            if (c is Renderer) ((Renderer)c).enabled = true; else if (c is Light) ((Light)c).enabled = true;
        }
        list.Clear();
    }
}
