#define HOST
// GadgetLook - the physical fishing gadgets, drawn locally on one PC. Shared by the host add-on HotspotCompass<N>
// (-define:HOST -> class GadgetLook6, renamed per version by mkver.ps1) and the guest payload CompassClient (class GadgetLook).
// UnityEngine only (Core, IMGUI, TextRendering, Physics); RoR2's SkillIcon / Image are reached by reflection.
//
//  COMPASS (Utility skill, hold SHIFT): a brass pocket compass is raised low and centred in front of the camera, the lid
//    flips open, a red needle swings (damped spring) to point at the purple hotspot and a thin gold needle at the golden
//    hotspot; the card with N/E/S/W turns to real north. Direction only, no distance.
//  RANGE FINDER (Special skill, hold R): binocular view - camera FOV zooms in, a two-circle eyepiece mask + reticle,
//    and the distance to the aimed point ("84 m", "84 m - HOTSPOT" when the aimed water point is inside the hotspot
//    radius, "- GOLDEN HOTSPOT" likewise). Letting go restores the FOV (it is only changed for the render, see Zoom).
//  Not owned: the key shows a hint ("You need a Compass - buy one at the Rod Shop stall").
//  CAST DISTANCE TRACKER readout: a small strip at the bottom centre, only while the line is out (and 5 s after a cast).
//  Skill icons: the Shift / R HUD icons show a compass / binoculars when owned (sprite swapped just before the UI renders).
//
//  Per frame: GadgetLook.Frame(input) from the game's update; GadgetLook.OnGui(input) from OnGUI; Clear() to remove all.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
#if HOST
using CL = CompassLook6;
#else
using CL = CompassLook;
#endif

#if HOST
public static class GadgetLook6
#else
public static class GadgetLook
#endif
{
    public class In
    {
        public Camera cam; public bool hasMe; public Vector3 me;
        public bool ownCompass, ownRange, ownTracker;
        public bool utilDown, specialDown, blocked;      // blocked: chat / menu / inventory open
        public string utilGadget, specialGadget;         // what Shift / R hold: "compass", "rangefinder", other id (someone else's) or null
        public string utilHint = "", specialHint = "";   // toast when the key is pressed with nothing on it
        public bool hot; public Vector3 hotPos; public float hotR = 10f;
        public bool gold; public Vector3 goldPos; public float goldR = 8f;
        public bool hasWater; public float waterY; public int mask = 1 << 11;
        public bool lineOut; public Vector3 bobber; public float lastCast = -1f; public bool lastWater = true; public float lastCastAt = -100f;
        public bool aim; public Vector3 aimLand; public bool aimWater;   // predicted landing (tracker ring)
        public string compassPrice = "$1,500", rangePrice = "$3,000";
    }

    public static bool forceCompass, forceRange;   // tests: raise without the key / without owning
    public static float ZoomFov = 14f;
    public static Func<Vector3, string> zoneAt;   // fishing zone name at a water point (host: Fishing<N>.ZoneNameAt; guest: [FT] ZONE lines)
    public static Action<string> Log;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static float raise, lid, needle, needleVel, goldN, goldVel, card, cardVel, zoom;
    static bool wasUtil, wasSpecial, usingCompass;
    static string toast; static float toastUntil;
    static GameObject model, ring; static Transform lidPivot, needleTf, goldTf, cardTf; static Material glassMat;
    static float measDist = -1f; static string measTag = "";
    static Camera zoomCam; static float savedFov; static bool fovSaved, camHooked;
    public static bool CompassUp { get { return raise > 0.01f; } }
    public static bool RangeUp { get { return zoom > 0.01f; } }

    static void L(string s) { if (Log != null) try { Log(s); } catch { } }
    static float Ease(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    // ================================================================ per frame
    public static void Frame(In d)
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f), now = Time.unscaledTime;
        bool util = d.hasMe && !d.blocked && d.utilDown, spec = d.hasMe && !d.blocked && d.specialDown;
        if (util && !wasUtil && d.utilGadget == null && !forceCompass && d.utilHint.Length > 0) Toast(d.utilHint);
        if (spec && !wasSpecial && d.specialGadget == null && !forceRange && d.specialHint.Length > 0) Toast(d.specialHint);
        wasUtil = util; wasSpecial = spec;

        bool wantC = d.hasMe && d.cam != null && ((util && d.utilGadget == "compass") || (spec && d.specialGadget == "compass") || forceCompass);
        bool wantR = d.hasMe && d.cam != null && ((util && d.utilGadget == "rangefinder") || (spec && d.specialGadget == "rangefinder") || forceRange);
        if (wantC != usingCompass) { usingCompass = wantC; L(wantC ? "compass raised" : "compass lowered"); }

        // ---- compass
        raise = Mathf.MoveTowards(raise, wantC ? 1f : 0f, dt / (wantC ? 0.32f : 0.22f));
        lid = Mathf.MoveTowards(lid, wantC && raise > 0.55f ? 1f : 0f, dt / (wantC ? 0.28f : 0.14f));
        if (raise > 0.001f && d.cam != null)
        {
            if (model == null) BuildCompass();
            if (model.transform.parent != d.cam.transform) model.transform.SetParent(d.cam.transform, false);
            if (!model.activeSelf) model.SetActive(true);
            float e = Ease(raise);
            model.transform.localPosition = Vector3.Lerp(new Vector3(0.02f, -0.42f, 0.5f), new Vector3(0f, -0.16f, 0.47f), e);
            model.transform.localRotation = Quaternion.Euler(Mathf.Lerp(-20f, -58f, e), 0f, Mathf.Lerp(12f, 0f, e));
            lidPivot.localRotation = Quaternion.Euler(Ease(lid) * 105f, 0f, 0f);
            float yaw = d.cam.transform.eulerAngles.y;
            Spring(ref card, ref cardVel, -yaw, 40f, 9f, dt);
            if (d.hot) Spring(ref needle, ref needleVel, CL.Bearing(d.me, d.hotPos) - yaw, 55f, 5.5f, dt);
            else { needleVel += Mathf.Sin(now * 1.7f) * 40f * dt; needle += needleVel * dt; needleVel *= 0.97f; }   // no hotspot: drifts
            if (d.gold) Spring(ref goldN, ref goldVel, CL.Bearing(d.me, d.goldPos) - yaw, 45f, 5f, dt);
            cardTf.localRotation = Quaternion.Euler(0f, card, 0f);
            needleTf.localRotation = Quaternion.Euler(0f, needle, 0f);
            goldTf.gameObject.SetActive(d.gold);
            goldTf.localRotation = Quaternion.Euler(0f, goldN, 0f);
        }
        else if (model != null && model.activeSelf) model.SetActive(false);

        // ---- range finder
        zoom = Mathf.MoveTowards(zoom, wantR ? 1f : 0f, dt / 0.2f);
        if (zoom > 0f && d.cam != null) { zoomCam = d.cam; HookCam(); Measure(d); }

        // ---- tracker ring (where the cast will land) while the line is in
        bool ringOn = d.ownTracker && d.aim && !d.lineOut && zoom <= 0f && raise <= 0f;
        if (ringOn && ring == null) ring = CL.MarkerBuild();
        CL.MarkerSet(ring, ringOn, d.aimLand, !d.aimWater ? new Color(1f, 0.45f, 0.4f)
            : d.hot && CL.Flat(d.aimLand, d.hotPos) <= d.hotR ? CL.Green : d.gold && CL.Flat(d.aimLand, d.goldPos) <= d.goldR ? CL.Gold : new Color(0.55f, 0.95f, 1f));

        Icons(d);
    }

    static void Spring(ref float a, ref float v, float target, float k, float damp, float dt)
    {
        float err = Mathf.DeltaAngle(a, target);
        v += (err * k - v * damp) * dt;
        a += v * dt;
    }

    public static void Toast(string s) { toast = s; toastUntil = Time.unscaledTime + 3f; }

    public static void Clear()
    {
        if (model != null) UnityEngine.Object.Destroy(model); model = null;
        if (ring != null) UnityEngine.Object.Destroy(ring); ring = null;
        raise = lid = zoom = 0f; forceCompass = forceRange = false;
        if (camHooked) { Camera.onPreCull -= preCull; Camera.onPostRender -= postRender; camHooked = false; }
        if (iconHooked) { Canvas.willRenderCanvases -= iconHook; iconHooked = false; }
        RestoreIcons();
    }

    // ================================================================ range finder: zoom + measurement
    static Camera.CameraCallback preCull, postRender;
    static void HookCam()
    {
        if (camHooked) return;
        preCull = c =>
        {
            if (c != zoomCam || zoom <= 0f) return;
            savedFov = c.fieldOfView; fovSaved = true;
            c.fieldOfView = Mathf.Lerp(savedFov, ZoomFov, Ease(zoom));
        };
        postRender = c => { if (c == zoomCam && fovSaved) { c.fieldOfView = savedFov; fovSaved = false; } };
        Camera.onPreCull += preCull; Camera.onPostRender += postRender;
        camHooked = true;
    }

    static void Measure(In d)
    {
        Transform t = d.cam.transform;
        Vector3 o = t.position, dir = t.forward;
        float best = 1500f; bool water = false; Vector3 p = Vector3.zero; bool hit = false;
        RaycastHit h;
        if (Physics.Raycast(o, dir, out h, best, d.mask, QueryTriggerInteraction.Ignore)) { best = h.distance; p = h.point; hit = true; }
        if (d.hasWater && dir.y < -0.0005f)
        {
            float k = (d.waterY - o.y) / dir.y;
            if (k > 0f && k < best) { best = k; p = o + dir * k; hit = true; water = true; }
        }
        if (!hit) { measDist = -1f; measTag = ""; return; }
        measDist = CL.Flat(d.me, p);
        measTag = water && d.hot && CL.Flat(p, d.hotPos) <= d.hotR ? "HOTSPOT" : water && d.gold && CL.Flat(p, d.goldPos) <= d.goldR ? "GOLDEN HOTSPOT" : water ? ZoneTag(p) : "";
    }

    // ================================================================ OnGUI
    static Texture2D mask, white;
    static GUIStyle big, mid, small;

    static string ZoneTag(Vector3 p)
    {
        try { string z = zoneAt == null ? null : zoneAt(p); return string.IsNullOrEmpty(z) ? "water" : "ZONE:" + z; } catch { return "water"; }
    }
    public static void OnGui(In d)
    {
        if (white == null) white = Texture2D.whiteTexture;
        if (big == null)
        {
            big = new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            mid = new GUIStyle(big) { fontStyle = FontStyle.Normal };
            small = new GUIStyle(mid);
        }
        float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f), W = Screen.width, H = Screen.height;
        big.fontSize = Mathf.RoundToInt(34 * s); mid.fontSize = Mathf.RoundToInt(17 * s); small.fontSize = Mathf.RoundToInt(13 * s);

        if (zoom > 0f) Eyepiece(d, s, W, H);

        // tracker readout: only while fishing
        if (d.ownTracker && d.hasMe && (d.lineOut || Time.unscaledTime - d.lastCastAt < 5f) && zoom <= 0f)
        {
            string a = d.lastCast < 0f ? "Cast  -" : "Cast  <b>" + CL.Dist(d.lastCast) + "</b>" + (d.lastWater ? "" : "  <color=#ff8a7a>(ground)</color>");
            string b;
            if (!d.lineOut) b = "<color=#999999>line in</color>";
            else if (d.gold && CL.Flat(d.bobber, d.goldPos) <= d.goldR) b = "<color=#ffd75e><b>bobber in the GOLDEN zone!</b></color>";
            else if (!d.hot) b = "<color=#999999>no hotspot on this stage</color>";
            else { float bd = CL.Flat(d.bobber, d.hotPos); b = bd <= d.hotR ? "<color=#66ff88><b>bobber " + CL.Dist(bd) + " from hotspot - in zone!</b></color>" : "bobber <b>" + CL.Dist(bd) + "</b> from hotspot"; }
            float w = 460f * s, h = 34f * s;
            var r = new Rect((W - w) / 2f, H * 0.80f, w, h);
            Fill(r, new Color(0f, 0f, 0f, 0.6f)); Fill(new Rect(r.x, r.y, 4f * s, h), new Color(0.37f, 0.88f, 0.82f, 1f));
            mid.alignment = TextAnchor.MiddleCenter;
            GUI.Label(r, "<color=#5fe0d0>TRACKER</color>   " + a + "   <color=#666666>|</color>   " + b, mid);
        }

        if (toast != null && Time.unscaledTime < toastUntil)
        {
            float w = 640f * s, h = 36f * s; var r = new Rect((W - w) / 2f, H * 0.70f, w, h);
            float al = Mathf.Clamp01((toastUntil - Time.unscaledTime) * 2f);
            Fill(r, new Color(0f, 0f, 0f, 0.65f * al)); Fill(new Rect(r.x, r.yMax - 3f * s, w, 3f * s), new Color(1f, 0.75f, 0.3f, al));
            var p = GUI.color; GUI.color = new Color(1, 1, 1, al); GUI.Label(r, toast, mid); GUI.color = p;
        }
    }

    static void Fill(Rect r, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = p; }

    static void Eyepiece(In d, float s, float W, float H)
    {
        if (mask == null)
        {
            int w = 512, h = 256; mask = new Texture2D(w, h, TextureFormat.RGBA32, false); mask.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[w * h];
            for (int j = 0; j < h; j++) for (int i = 0; i < w; i++)
                {
                    float y = (j + 0.5f) / h - 0.5f, x = (i + 0.5f) / w;   // x 0..1, y -0.5..0.5 ; circles r 0.47 (in height units) at x 0.29 / 0.71
                    float dx1 = (x - 0.295f) * 2f, dx2 = (x - 0.705f) * 2f;
                    float d1 = Mathf.Sqrt(dx1 * dx1 + y * y), d2 = Mathf.Sqrt(dx2 * dx2 + y * y), dd = Mathf.Min(d1, d2);
                    float a = Mathf.Clamp01((dd - 0.44f) / 0.05f);
                    px[j * w + i] = new Color(0.01f, 0.012f, 0.015f, a);
                }
            mask.SetPixels(px); mask.Apply();
        }
        float z = Ease(zoom);
        var prev = GUI.color;
        // mask: 2:1 rect covering the screen height (slightly larger while raising), black outside it
        float mh = H * Mathf.Lerp(1.6f, 1.04f, z), mw = mh * 2f;
        var mr = new Rect((W - mw) / 2f, (H - mh) / 2f, mw, mh);
        GUI.color = new Color(1, 1, 1, z);
        GUI.DrawTexture(mr, mask);
        if (mr.x > 0) { Fill(new Rect(0, 0, mr.x + 1, H), new Color(0.01f, 0.012f, 0.015f, z)); Fill(new Rect(mr.xMax - 1, 0, W - mr.xMax + 1, H), new Color(0.01f, 0.012f, 0.015f, z)); }
        if (mr.y > 0) { Fill(new Rect(0, 0, W, mr.y + 1), new Color(0.01f, 0.012f, 0.015f, z)); Fill(new Rect(0, mr.yMax - 1, W, H - mr.yMax + 1), new Color(0.01f, 0.012f, 0.015f, z)); }
        GUI.color = prev;
        if (z < 0.6f) return;

        // reticle
        float cx = W / 2f, cy = H / 2f, g = 14f * s, len = 70f * s, th = Mathf.Max(1f, 2f * s);
        bool onHot = measTag == "HOTSPOT", onGold = measTag == "GOLDEN HOTSPOT";
        Color rc = onHot ? new Color(0.78f, 0.49f, 1f, 0.95f) : onGold ? new Color(1f, 0.8f, 0.25f, 0.95f) : new Color(0.75f, 1f, 0.8f, 0.9f);
        Fill(new Rect(cx - g - len, cy - th / 2f, len, th), rc); Fill(new Rect(cx + g, cy - th / 2f, len, th), rc);
        Fill(new Rect(cx - th / 2f, cy + g, th, len), rc); Fill(new Rect(cx - th / 2f, cy - g - len * 0.5f, th, len * 0.5f), rc);
        for (int k = 1; k <= 4; k++)
        {
            float o = g + k * len / 4.5f, tl = (k % 2 == 0 ? 10f : 6f) * s;
            Fill(new Rect(cx - o, cy - tl / 2f, th, tl), rc); Fill(new Rect(cx + o, cy - tl / 2f, th, tl), rc);
            Fill(new Rect(cx - tl / 2f, cy + o, tl, th), rc);
        }
        Fill(new Rect(cx - 2f * s, cy - 2f * s, 4f * s, 4f * s), rc);

        string dist = measDist < 0f ? "- - -" : CL.Dist(measDist);
        string tag = onHot ? "  <color=#d9a6ff>- HOTSPOT</color>" : onGold ? "  <color=#ffd75e>- GOLDEN HOTSPOT</color>" : measTag.StartsWith("ZONE:") ? "  <color=#9fe0c8>- " + measTag.Substring(5) + "</color>" : "";
        GUI.Label(new Rect(cx - 400f * s, cy + g + len + 10f * s, 800f * s, 46f * s), "<color=#e6fff0>" + dist + "</color>" + tag, big);
        GUI.Label(new Rect(cx - 300f * s, cy - g - len * 0.5f - 34f * s, 600f * s, 24f * s), "<color=#9fbfaa>RANGE FINDER  x" + (60f / ZoomFov).ToString("0") + "</color>", small);
    }

    // ================================================================ 3D pocket compass (primitives, unlit)
    static Material Mat(Color c)
    {
        Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
        var m = new Material(sh); m.color = c; return m;
    }

    static GameObject Part(PrimitiveType pt, string name, Transform parent, Vector3 pos, Vector3 scale, Vector3 euler, Material m)
    {
        var g = GameObject.CreatePrimitive(pt); g.name = name;
        var col = g.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
        g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localScale = scale; g.transform.localEulerAngles = euler;
        var r = g.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return g;
    }

    static void BuildCompass()
    {
        const float R = 0.046f;   // case radius (m). NB: Unity cylinders are 2 units tall -> scale.y = half thickness
        model = new GameObject("GadgetCompass");
        UnityEngine.Object.DontDestroyOnLoad(model);
        Transform root = model.transform;
        Material brass = Mat(new Color(0.80f, 0.60f, 0.24f)), brassDark = Mat(new Color(0.52f, 0.36f, 0.12f)), brassLight = Mat(new Color(0.95f, 0.80f, 0.42f));
        Material face = Mat(new Color(0.96f, 0.93f, 0.84f)), ink = Mat(new Color(0.12f, 0.1f, 0.08f)), red = Mat(new Color(0.86f, 0.1f, 0.08f));
        Material tail = Mat(new Color(0.85f, 0.85f, 0.88f)), goldM = Mat(new Color(1f, 0.78f, 0.15f));

        // case: body, rim ring (slightly bigger, dark), bow (hanging ring) and crown at the far edge (+Z)
        Part(PrimitiveType.Cylinder, "Case", root, new Vector3(0, 0, 0), new Vector3(R * 2f, 0.006f, R * 2f), Vector3.zero, brass);
        Part(PrimitiveType.Cylinder, "Bezel", root, new Vector3(0, 0.0062f, 0), new Vector3(R * 2.08f, 0.0006f, R * 2.08f), Vector3.zero, brassDark);
        Part(PrimitiveType.Cylinder, "Crown", root, new Vector3(0, 0.002f, R + 0.006f), new Vector3(0.012f, 0.005f, 0.012f), new Vector3(90, 0, 0), brassLight);
        Part(PrimitiveType.Cylinder, "Bow", root, new Vector3(0, 0.002f, R + 0.016f), new Vector3(0.022f, 0.0015f, 0.022f), new Vector3(90, 0, 0), brassDark);
        // face
        Part(PrimitiveType.Cylinder, "Face", root, new Vector3(0, 0.0075f, 0), new Vector3(R * 1.78f, 0.0004f, R * 1.78f), Vector3.zero, face);

        // card: turns to real north - ticks + N/E/S/W
        cardTf = new GameObject("Card").transform; cardTf.SetParent(root, false); cardTf.localPosition = new Vector3(0, 0.0082f, 0);
        for (int k = 0; k < 32; k++)
        {
            bool major = k % 8 == 0, mid = k % 4 == 0;
            float a = k * 360f / 32f, len = major ? 0.011f : mid ? 0.008f : 0.004f, w = major ? 0.0022f : 0.0012f;
            var piv = new GameObject("t").transform; piv.SetParent(cardTf, false); piv.localEulerAngles = new Vector3(0, a, 0);
            Part(PrimitiveType.Cube, "Tick", piv, new Vector3(0, 0, R * 0.86f - len / 2f), new Vector3(w, 0.0003f, len), Vector3.zero, k == 0 ? red : ink);
        }
        Font font = null; try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        if (font == null) try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        string[] letters = { "N", "E", "S", "W" };
        for (int k = 0; k < 4; k++)
        {
            if (font == null) break;
            var piv = new GameObject("L").transform; piv.SetParent(cardTf, false); piv.localEulerAngles = new Vector3(0, k * 90f, 0);
            var g = new GameObject("Letter" + letters[k]); g.transform.SetParent(piv, false);
            g.transform.localPosition = new Vector3(0, 0.0004f, R * 0.56f);
            g.transform.localEulerAngles = new Vector3(90f, 0f, 0f);     // lie flat on the card, reading outward
            var tm = g.AddComponent<TextMesh>(); tm.font = font; tm.text = letters[k]; tm.fontSize = 64; tm.characterSize = 0.0026f;
            tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center; tm.fontStyle = FontStyle.Bold;
            tm.color = k == 0 ? new Color(0.8f, 0.08f, 0.05f) : new Color(0.12f, 0.1f, 0.08f);
            var mr = g.GetComponent<MeshRenderer>(); mr.sharedMaterial = font.material;
        }

        // gold needle (golden hotspot) under the red one
        goldTf = new GameObject("GoldNeedle").transform; goldTf.SetParent(root, false); goldTf.localPosition = new Vector3(0, 0.0092f, 0);
        Part(PrimitiveType.Cube, "Gold", goldTf, new Vector3(0, 0, R * 0.36f), new Vector3(0.0026f, 0.0007f, R * 0.72f), Vector3.zero, goldM);
        Part(PrimitiveType.Cube, "GoldTip", goldTf, new Vector3(0, 0, R * 0.70f), new Vector3(0.005f, 0.0007f, 0.005f), new Vector3(0, 45, 0), goldM);
        // red needle (hotspot): diamond-ish = red front half + pale tail
        needleTf = new GameObject("Needle").transform; needleTf.SetParent(root, false); needleTf.localPosition = new Vector3(0, 0.0100f, 0);
        Part(PrimitiveType.Cube, "Front", needleTf, new Vector3(0, 0, R * 0.38f), new Vector3(0.0055f, 0.0012f, R * 0.76f), Vector3.zero, red);
        Part(PrimitiveType.Cube, "Tip", needleTf, new Vector3(0, 0, R * 0.74f), new Vector3(0.0065f, 0.0012f, 0.0065f), new Vector3(0, 45, 0), red);
        Part(PrimitiveType.Cube, "Tail", needleTf, new Vector3(0, 0, -R * 0.26f), new Vector3(0.0045f, 0.0011f, R * 0.52f), Vector3.zero, tail);
        Part(PrimitiveType.Cylinder, "Pin", root, new Vector3(0, 0.0104f, 0), new Vector3(0.006f, 0.0007f, 0.006f), Vector3.zero, brassLight);

        // glass
        Shader gs = Shader.Find("Sprites/Default");
        glassMat = new Material(gs != null ? gs : Shader.Find("Unlit/Color")); glassMat.color = new Color(0.8f, 0.9f, 1f, 0.12f); glassMat.renderQueue = 3001;
        Part(PrimitiveType.Cylinder, "Glass", root, new Vector3(0, 0.0118f, 0), new Vector3(R * 1.8f, 0.0002f, R * 1.8f), Vector3.zero, glassMat);

        // lid: hinge at the far edge (+Z); closed = lying over the glass, open = swung up behind the compass
        lidPivot = new GameObject("LidHinge").transform; lidPivot.SetParent(root, false); lidPivot.localPosition = new Vector3(0, 0.0125f, R);
        Part(PrimitiveType.Cylinder, "Lid", lidPivot, new Vector3(0, 0.0025f, -R), new Vector3(R * 2.04f, 0.0018f, R * 2.04f), Vector3.zero, brassDark);
        Part(PrimitiveType.Cylinder, "LidInner", lidPivot, new Vector3(0, 0.0004f, -R), new Vector3(R * 1.8f, 0.0004f, R * 1.8f), Vector3.zero, brassLight);
        Part(PrimitiveType.Cylinder, "LidEmblem", lidPivot, new Vector3(0, 0.0045f, -R), new Vector3(R * 0.9f, 0.0003f, R * 0.9f), Vector3.zero, brass);
        Part(PrimitiveType.Cylinder, "Hinge", root, new Vector3(0, 0.0115f, R), new Vector3(0.006f, 0.01f, 0.006f), new Vector3(0, 0, 90), brassDark);

        // everything draws on top of the world at this distance: keep it in front of the character, no fog
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true)) { r.gameObject.layer = 0; }
        model.SetActive(false);
        L("compass model built (" + model.GetComponentsInChildren<Renderer>(true).Length + " parts, font " + (font != null ? font.name : "none") + ")");
    }

    // ================================================================ skill icons (Shift = compass, R = binoculars)
    static Sprite compassIcon, rangeIcon;
    static Canvas.WillRenderCanvases iconHook; static bool iconHooked;
    static Sprite showUtil, showSpecial;
    static Type skillIconType; static FieldInfo fSlot, fImage; static PropertyInfo pSprite;
    static UnityEngine.Object[] icons; static float iconsAt = -100f;

    static void Icons(In d)
    {
        if (compassIcon == null) { compassIcon = MakeSprite(true); rangeIcon = MakeSprite(false); }
        showUtil = d.utilGadget == "compass" ? compassIcon : d.utilGadget == "rangefinder" ? rangeIcon : null;
        showSpecial = d.specialGadget == "compass" ? compassIcon : d.specialGadget == "rangefinder" ? rangeIcon : null;
        if (!iconHooked) { iconHook = SwapIcons; Canvas.willRenderCanvases += iconHook; iconHooked = true; }
    }

    static void SwapIcons()
    {
        try
        {
            if (showUtil == null && showSpecial == null) return;
            if (skillIconType == null)
            {
                foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("RoR2.UI.SkillIcon"); if (t != null) skillIconType = t; }
                if (skillIconType == null) return;
                fSlot = skillIconType.GetField("targetSkillSlot", All); fImage = skillIconType.GetField("iconImage", All);
            }
            if (icons == null || Time.unscaledTime - iconsAt > 2f) { icons = UnityEngine.Object.FindObjectsOfType(skillIconType); iconsAt = Time.unscaledTime; }
            foreach (UnityEngine.Object ic in icons)
            {
                if (ic == null) continue;
                int slot = Convert.ToInt32(fSlot.GetValue(ic));   // SkillSlot: 0 primary, 1 secondary, 2 utility, 3 special
                Sprite want = slot == 2 ? showUtil : slot == 3 ? showSpecial : null;
                if (want == null) continue;
                object img = fImage.GetValue(ic); if (img == null || (img is UnityEngine.Object && (UnityEngine.Object)img == null)) continue;
                if (pSprite == null) pSprite = img.GetType().GetProperty("sprite");
                if (!ReferenceEquals(pSprite.GetValue(img, null), want)) pSprite.SetValue(img, want, null);
            }
        }
        catch (Exception e) { L("icon error: " + e.Message); showUtil = showSpecial = null; }
    }

    static void RestoreIcons()
    {
        try
        {
            if (icons == null || skillIconType == null) return;
            FieldInfo fs = skillIconType.GetField("targetSkill", All);
            foreach (UnityEngine.Object ic in icons)
            {
                if (ic == null) continue;
                object img = fImage.GetValue(ic), gs = fs == null ? null : fs.GetValue(ic);
                if (img == null || gs == null || pSprite == null) continue;
                PropertyInfo pi = gs.GetType().GetProperty("icon", All);
                if (pi != null) pSprite.SetValue(img, pi.GetValue(gs, null), null);
            }
        }
        catch { }
    }

    public static Sprite MakeSprite(bool compass)
    {
        int n = 128; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[n * n];
        Color bg1 = compass ? new Color(0.10f, 0.16f, 0.22f) : new Color(0.12f, 0.18f, 0.14f), bg2 = compass ? new Color(0.20f, 0.30f, 0.38f) : new Color(0.22f, 0.32f, 0.24f);
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
            {
                float x = (i + 0.5f) / n * 2f - 1f, y = (j + 0.5f) / n * 2f - 1f;
                Color c = Color.Lerp(bg1, bg2, (y + 1f) / 2f);
                if (compass)
                {
                    float r = Mathf.Sqrt(x * x + (y + 0.06f) * (y + 0.06f));
                    if (Mathf.Abs(x) < 0.09f && y > 0.72f && y < 0.9f) c = new Color(0.85f, 0.66f, 0.28f);                 // crown
                    if (Mathf.Abs(Mathf.Sqrt(x * x + (y - 0.86f) * (y - 0.86f)) - 0.11f) < 0.035f) c = new Color(0.62f, 0.45f, 0.16f);   // bow
                    if (r < 0.74f) c = new Color(0.86f, 0.66f, 0.27f);
                    if (r < 0.66f) c = new Color(0.62f, 0.44f, 0.15f);
                    if (r < 0.61f) c = new Color(0.97f, 0.94f, 0.85f);
                    // needle towards upper right: diamond along direction (0.6,0.8)
                    float u = x * 0.6f + (y + 0.06f) * 0.8f, v = -x * 0.8f + (y + 0.06f) * 0.6f;
                    if (Mathf.Abs(v) < 0.11f * (1f - Mathf.Abs(u) / 0.52f) && Mathf.Abs(u) < 0.52f) c = u > 0 ? new Color(0.88f, 0.1f, 0.08f) : new Color(0.35f, 0.35f, 0.4f);
                    if (r < 0.05f) c = new Color(0.85f, 0.66f, 0.28f);
                }
                else
                {
                    // binoculars: two barrels + bridge, lenses facing us
                    float dl = Mathf.Sqrt((x + 0.38f) * (x + 0.38f) + (y + 0.12f) * (y + 0.12f)), dr = Mathf.Sqrt((x - 0.38f) * (x - 0.38f) + (y + 0.12f) * (y + 0.12f));
                    float d = Mathf.Min(dl, dr);
                    if (Mathf.Abs(x) < 0.2f && y > -0.05f && y < 0.25f) c = new Color(0.16f, 0.16f, 0.18f);
                    if ((Mathf.Abs(x + 0.38f) < 0.3f || Mathf.Abs(x - 0.38f) < 0.3f) && y > -0.12f && y < 0.62f) c = new Color(0.2f, 0.2f, 0.23f);
                    if (d < 0.36f) c = new Color(0.1f, 0.1f, 0.12f);
                    if (d < 0.27f) c = Color.Lerp(new Color(0.3f, 0.75f, 0.95f), new Color(0.1f, 0.3f, 0.6f), d / 0.27f);
                    float hl = Mathf.Min(Mathf.Sqrt((x + 0.46f) * (x + 0.46f) + (y + 0.04f) * (y + 0.04f)), Mathf.Sqrt((x - 0.30f) * (x - 0.30f) + (y + 0.04f) * (y + 0.04f)));
                    if (hl < 0.06f) c = new Color(0.9f, 0.97f, 1f);
                    if (Mathf.Abs(x) < 0.05f && y > 0.25f && y < 0.5f) c = new Color(0.5f, 0.5f, 0.55f);   // focus wheel
                }
                px[j * n + i] = c;
            }
        t.SetPixels(px); t.Apply(); t.name = compass ? "FishingCompassIcon" : "FishingRangeFinderIcon";
        var sp = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f); sp.name = t.name;
        return sp;
    }
}
