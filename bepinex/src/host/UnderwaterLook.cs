// UnderwaterLook: what you see when the camera goes below the fishing lake's surface ("FishingWater" on the host,
// "FishingClientWater" in FISHING.exe). Shared by the host add-on (UnderwaterFx<N>) and the guest client.
//
//  * Call UnderwaterLook.Tick() every frame on the MAIN thread (RoR2Application.onUpdate) while a run is active and
//    UnderwaterLook.Restore() when switched off / no run. Never from an injector thread: Tick() creates Unity objects lazily.
//    Only UnityEngine.CoreModule is needed at compile time; RoR2 / post-processing types are reached by reflection.
//  * Underwater = active scene camera (RoR2 CameraRigController.sceneCam of the local player, else Camera.main) below the
//    lake surface and inside its horizontal bounds. Fades in over ~0.25 s, out over ~0.12 s.
//  * Fog without a custom shader: up to 14 camera-facing "fog shells" (transparent quads at increasing distances, depth
//    tested against the scene) CLIPPED to the part below the water plane, drawn far-to-near in one mesh. A view ray
//    therefore gets fog only for the stretch of it that is under water; the last shell (fogEnd) is opaque, so far away
//    is a blue-green wall instead of the void. Vertex colours darken with depth below the surface.
//    + a near full-screen tint quad (darker the deeper the camera is).
//    + optional (cfg ppFog=1): a high-priority global PPv2 volume overriding RoR2's RampFog colours to the water colours,
//      so the stage's own post fog doesn't paint far pixels orange/grey. Blended by weight = fade.
//    + optional (cfg unityFog=1, default off): RenderSettings exp2 fog (affects forward/transparent shaders only in
//      RoR2's deferred renderer). Saved on entry and restored on exit (only if the scene still holds our values).
//  * Underside of the surface: two down-facing shimmering semi-transparent sheets just under the surface (underwater only).
//  * Lake floor: a sandy lit plane (Standard shader) floorDepth metres under the surface + additive caustic sheet, always
//    present while the lake exists (visible from the shore through the water). Rocks + swaying weeds are scattered around
//    the camera the first time it goes underwater and follow it in 45 m steps. NO colliders anywhere (meshes are built
//    from primitive meshes, never AddComponent<Collider>), so casts, bobbers and drowning physics are untouched.
//  * Bubbles: 36 camera-facing quads rising around the camera (no ParticleSystem module needed).
//  * Two copies (host add-on + guest client in one game) never both draw: a frame-heartbeat lock in AppDomain data.
//  * Tuning: %TEMP%\ror2_underwater.cfg (re-read within 2 s of a save). Log: %TEMP%\ror2_underwater.log (state changes only).
//  * No per-frame allocations: meshes are rebuilt from reused lists in Camera.onPreCull (no 1-frame lag behind the camera).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class UnderwaterLook
{
    public static Action<string> Log;                    // optional extra sink (the file log is always written when log=1)
    public static string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_underwater.log");
    public static string CfgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_underwater.cfg");
    public static string Tag = "";                        // log prefix, e.g. "[UnderwaterFx1] " / "[client] "
    public static string[] WaterNames = { "FishingWater", "FishingClientWater" };
    public static bool IsUnderwater { get { return under; } }
    public static float Depth { get { return camDepth; } }   // metres below the surface (when underwater)
    public static float Fade { get { return k; } }

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // ------------------------------------------------------------------ tuning (cfg keys in brackets)
    static bool cEnabled = true, cLog = true;                                  // [enabled] [log]
    static Color cFog = new Color(0.10f, 0.42f, 0.44f, 1f);                    // [fogColor] shallow water colour
    static Color cDeep = new Color(0.015f, 0.08f, 0.12f, 1f);                  // [deepColor] colour at darkDepth and below
    static float cDensity = 0.07f;                                             // [density] fog per metre (0.07: ~50% at 10 m)
    static float cFogEnd = 45f;                                                // [fogEnd] m, opaque wall distance
    static int cShells = 12;                                                   // [shells] 4..14
    static Color cTint = new Color(0.05f, 0.38f, 0.42f, 0.20f);                // [tint] near full-screen tint (alpha = strength)
    static float cDarken = 0.45f;                                              // [darken] extra tint alpha at darkDepth
    static float cDarkDepth = 25f;                                             // [darkDepth] m below surface = fully deep colour
    static Color cSurf = new Color(0.65f, 0.95f, 0.95f, 0.35f);                // [surfColor] underside of the surface
    static bool cUnderside = true, cFloor = true, cCaustics = true, cRocks = true, cWeeds = true, cBubbles = true;   // [underside] [floor] [caustics] [rocks] [weeds] [bubbles]
    static float cFloorDepth = 18f;                                            // [floorDepth] m under the surface
    static Color cFloorColor = new Color(0.58f, 0.50f, 0.36f, 1f);             // [floorColor] sand
    static int cRockCount = 28, cWeedCount = 46, cBubbleCount = 36;            // [rockCount] [weedCount] [bubbleCount] (applied on next build)
    static bool cPP = true;                                                    // [ppFog] RampFog colour override volume
    static float cPPIntensity = -1f;                                           // [ppIntensity] <0 = keep the stage's
    static bool cUnityFog = false;                                             // [unityFog]
    static float cUnityFogDensity = 0.06f;                                     // [unityFogDensity]
    static float cAmbient = 1f;                                                // [ambient] multiplier on RenderSettings ambient while underwater (1 = off)
    static float cFadeIn = 0.25f, cFadeOut = 0.12f;                            // [fadeIn] [fadeOut] s

    // ------------------------------------------------------------------ state
    static bool under, active;                     // under: camera is under the surface now; active: we have something to undo
    static float k;                                // fade 0..1
    static float camDepth;
    static int sceneHandle = int.MinValue;
    static GameObject water; static float waterY; static Vector3 wMin, wMax, wCenter; static float nextWaterFind, nextCamFind, nextCfg;
    static Camera cam;
    static int errors, lastPreCullFrame = -10;
    static bool hookedPreCull;
    static Camera.CameraCallback preCullCb;

    static void L(string s)
    {
        if (!cLog) return;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Tag + s + Environment.NewLine); } catch { }
        try { if (Log != null) Log("underwater: " + s); } catch { }
    }
    static void Err(string where, Exception e) { if (errors++ < 12) L(where + " error: " + (e.InnerException ?? e).Message); }

    // ------------------------------------------------------------------ cross-assembly lock (host add-on + guest client)
    static int[] slot; static int myId;
    static bool Claim()
    {
        if (slot == null)
        {
            const string key = "ror2.underwater.lock";
            slot = AppDomain.CurrentDomain.GetData(key) as int[];
            if (slot == null) { slot = new int[2]; AppDomain.CurrentDomain.SetData(key, slot); }
            myId = (typeof(UnderwaterLook).Assembly.ManifestModule.ModuleVersionId.GetHashCode() & 0x7ffffffe) | 1;
        }
        int f = Time.frameCount;
        if (slot[0] == myId || slot[0] == 0 || f - slot[1] > 3 || f < slot[1])
        {
            if (slot[0] != myId) L("drawing the underwater view from " + typeof(UnderwaterLook).Assembly.GetName().Name);
            slot[0] = myId; slot[1] = f; return true;
        }
        return false;
    }
    static void Release() { if (slot != null && slot[0] == myId) slot[0] = 0; }

    // ------------------------------------------------------------------ public API
    public static void Tick()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextCfg) { nextCfg = now + 2f; ReadCfg(); }
            if (!cEnabled) { Restore(); return; }
            if (!Claim()) { if (active) Teardown("another copy is drawing"); return; }

            Scene sc = SceneManager.GetActiveScene();
            if (sc.handle != sceneHandle)
            {
                if (sceneHandle != int.MinValue) Teardown("scene change -> " + sc.name);
                sceneHandle = sc.handle; water = null; cam = null; nextWaterFind = 0f; nextCamFind = 0f; ppFailed = false;
            }
            if (!hookedPreCull) { preCullCb = PreCull; Camera.onPreCull += preCullCb; hookedPreCull = true; }

            // water (re-found once a second; it is recreated every stage and can move to the host's level)
            if (now >= nextWaterFind)
            {
                nextWaterFind = now + 1f;
                GameObject w = water;
                if (w == null) for (int i = 0; i < WaterNames.Length && w == null; i++) w = GameObject.Find(WaterNames[i]);
                if (w != water) { water = w; if (w != null) L("lake found '" + w.name + "'"); else if (active) Teardown("lake gone"); }
                if (water != null) ReadWater();
            }
            if (water == null) { under = false; if (active) Teardown("no lake"); return; }
            active = true;

            if (cam == null || !cam.isActiveAndEnabled || now >= nextCamFind) { nextCamFind = now + 0.5f; cam = FindCam(); }

            // underwater test (small hysteresis so bobbing at the surface doesn't flicker)
            bool was = under;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                bool inside = p.x > wMin.x && p.x < wMax.x && p.z > wMin.z && p.z < wMax.z;
                camDepth = waterY - p.y;
                under = inside && (under ? camDepth > -0.02f : camDepth > 0.05f);
            }
            else under = false;
            if (under != was) L(under ? "ENTER underwater (depth " + camDepth.ToString("0.0") + " m)" : "EXIT underwater");

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            k = Mathf.MoveTowards(k, under ? 1f : 0f, dt / Mathf.Max(0.01f, under ? cFadeIn : cFadeOut));

            UpdateFloor(now);
            bool show = k > 0.001f;
            if (show) { EnsureUnderwater(); UpdateUnderwater(now, dt); }
            SetUnderwaterActive(show);
            UpdatePP(show);
            UpdateUnityFog(show);
            if (lastPreCullFrame < Time.frameCount - 2 && show && cam != null) BuildViewMeshes(cam);   // fallback if onPreCull never fires
        }
        catch (Exception e) { Err("tick", e); }
    }

    // Undo everything (objects destroyed, fog/ambient/PP restored, lock released). Cheap when nothing is active.
    public static void Restore()
    {
        if (active || floorRoot != null || uwRoot != null || ppGo != null || fogApplied) Teardown("restore");
        Release();
    }

    static void Teardown(string why)
    {
        try { UpdateUnityFog(false); } catch (Exception e) { Err("fog restore", e); }
        try { DestroyPP(); } catch (Exception e) { Err("pp destroy", e); }
        Kill(ref floorRoot); Kill(ref uwRoot);
        shellGo = null; tintGo = null; bubbleGo = null; underA = null; underB = null; caustic = null; rocks.Clear(); weeds.Clear();
        floorFor = null; scatterBuilt = false;
        if (under || k > 0f) L("EXIT underwater (" + why + ")");
        under = false; k = 0f; active = false;
        L("cleaned up: " + why);
    }
    static void Kill(ref GameObject go) { if (go != null) UnityEngine.Object.Destroy(go); go = null; }

    // ------------------------------------------------------------------ helpers
    static void ReadWater()
    {
        var r = water.GetComponent<Renderer>();
        Bounds b = r != null ? r.bounds : new Bounds(water.transform.position, new Vector3(2000f, 0.05f, 2000f));
        float y = b.max.y;
        if (Mathf.Abs(y - waterY) > 0.01f || b.min != wMin) L("lake surface y=" + y.ToString("0.00") + " size " + b.size.x.ToString("0") + "x" + b.size.z.ToString("0") + " m");
        waterY = y; wMin = b.min; wMax = b.max; wCenter = b.center;
    }

    static Type rigT; static IList rigList; static FieldInfo rigCamF, rigViewerF;
    static Camera FindCam()
    {
        try
        {
            if (rigT == null)
                foreach (var a in FishingScope.Assemblies())
                    if (a.GetName().Name == "RoR2")
                    {
                        rigT = a.GetType("RoR2.CameraRigController");
                        if (rigT != null)
                        {
                            rigList = rigT.GetField("instancesList", All).GetValue(null) as IList;
                            rigCamF = rigT.GetField("sceneCam", All); rigViewerF = rigT.GetField("_localUserViewer", All);
                        }
                    }
            if (rigList != null)
                for (int i = 0; i < rigList.Count; i++)
                {
                    object rig = rigList[i];
                    if (rig == null || (UnityEngine.Object)rig == null) continue;
                    if (rigViewerF != null && rigViewerF.GetValue(rig) == null) continue;
                    var c = rigCamF.GetValue(rig) as Camera;
                    if (c != null && c.isActiveAndEnabled) return c;
                }
        }
        catch (Exception e) { Err("camera rig", e); rigList = null; }
        return Camera.main;
    }

    static Shader spriteSh, addSh, litSh;
    static void Shaders()
    {
        if (spriteSh == null) spriteSh = Shader.Find("Sprites/Default");
        if (addSh == null) addSh = Shader.Find("Legacy Shaders/Particles/Additive");
        if (litSh == null) litSh = Shader.Find("Standard");
    }
    static Material SpriteMat(string name, Texture tex, int queue)
    {
        var m = new Material(spriteSh) { name = "Underwater " + name, renderQueue = queue };
        m.mainTexture = tex;
        return m;
    }
    static Material LitMat(string name, Texture tex, Color c, float gloss)
    {
        Material m;
        if (litSh != null) { m = new Material(litSh); if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss); if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f); }
        else { m = new Material(spriteSh); c *= 0.6f; c.a = 1f; m.renderQueue = 2450; }   // unlit fallback (still drawn before the fog shells)
        m.name = "Underwater " + name; m.color = c; if (tex != null) m.mainTexture = tex;
        return m;
    }
    static GameObject MeshObj(string name, Transform parent, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        if (water != null) go.layer = water.layer;
        if (parent != null) go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return go;
    }
    static Mesh Quad(string name, float size, float tile, bool up)
    {
        float h = size * 0.5f;
        var m = new Mesh { name = "Underwater " + name };
        m.vertices = new[] { new Vector3(-h, 0, -h), new Vector3(h, 0, -h), new Vector3(h, 0, h), new Vector3(-h, 0, h) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(tile, 0), new Vector2(tile, tile), new Vector2(0, tile) };
        m.triangles = up ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
        m.normals = new[] { up ? Vector3.up : Vector3.down, up ? Vector3.up : Vector3.down, up ? Vector3.up : Vector3.down, up ? Vector3.up : Vector3.down };
        m.bounds = new Bounds(Vector3.zero, new Vector3(size, 1f, size));
        return m;
    }

    // ------------------------------------------------------------------ procedural textures (main thread, built once)
    static Texture2D texWhite, texSand, texCaustic, texRipple, texBubble, texRock;
    static float Hash(int x, int y, int seed) { unchecked { int h = x * 374761393 + y * 668265263 + seed * 2147483; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xffff) / 65535f; } }
    static float VNoise(float u, float v, int cells, int seed)   // tileable value noise, u,v in [0,1)
    {
        float x = u * cells, y = v * cells; int x0 = (int)Mathf.Floor(x), y0 = (int)Mathf.Floor(y);
        float fx = x - x0, fy = y - y0; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells; x0 %= cells; y0 %= cells;
        return Mathf.Lerp(Mathf.Lerp(Hash(x0, y0, seed), Hash(x1, y0, seed), fx), Mathf.Lerp(Hash(x0, y1, seed), Hash(x1, y1, seed), fx), fy);
    }
    static Texture2D NewTex(int n, bool clamp)
    {
        return new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 4 };
    }
    static void Textures()
    {
        if (texWhite != null) return;
        texWhite = NewTex(4, true); var w = new Color[16]; for (int i = 0; i < 16; i++) w[i] = Color.white; texWhite.SetPixels(w); texWhite.Apply();

        const int N = 128; var px = new Color[N * N];
        // sand: soft value noise, ripples, dark specks (greyscale; tinted by floorColor)
        texSand = NewTex(N, false);
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float u = x / (float)N, v = y / (float)N;
                float n = 0.55f * VNoise(u, v, 8, 1) + 0.3f * VNoise(u, v, 32, 2) + 0.15f * VNoise(u, v, 64, 3);
                float rip = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (5f * u + 2f * v + 0.6f * VNoise(u, v, 4, 4)));
                float g = 0.72f + 0.30f * n + 0.08f * rip;
                if (Hash(x, y, 9) > 0.985f) g *= 0.55f;
                px[y * N + x] = new Color(g, g * 0.97f, g * 0.9f, 1f);
            }
        texSand.SetPixels(px); texSand.Apply();
        // caustics: bright web of lines (tileable: integer frequencies only); alpha = brightness
        texCaustic = NewTex(N, false); texRipple = NewTex(N, false);
        var px2 = new Color[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float u = x / (float)N, v = y / (float)N, tp = 2f * Mathf.PI;
                float a = Mathf.Sin(tp * (2f * u + 0.45f * Mathf.Sin(tp * (3f * v + VNoise(u, v, 4, 5)))));
                float b = Mathf.Sin(tp * (3f * v + 0.45f * Mathf.Sin(tp * (2f * u + v))));
                float c = Mathf.Sin(tp * (2f * u + 2f * v + 0.5f * VNoise(u, v, 8, 6)));
                float web = Mathf.Pow(1f - Mathf.Min(Mathf.Abs(a), Mathf.Min(Mathf.Abs(b), Mathf.Abs(c))), 7f);
                px[y * N + x] = new Color(web, web, web, web);
                float rp = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(tp * (3f * u + 0.6f * Mathf.Sin(tp * (2f * v)) + 0.4f * VNoise(u, v, 8, 7))), 6f);
                float al = 0.25f + 0.75f * Mathf.Max(rp, web * 0.8f);
                px2[y * N + x] = new Color(1f, 1f, 1f, al);
            }
        texCaustic.SetPixels(px); texCaustic.Apply();
        texRipple.SetPixels(px2); texRipple.Apply();
        // rock: mottled grey
        texRock = NewTex(64, false); var px3 = new Color[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                float g = 0.55f + 0.35f * VNoise(x / 64f, y / 64f, 8, 11) + 0.1f * VNoise(x / 64f, y / 64f, 32, 12);
                px3[y * 64 + x] = new Color(g, g, g * 0.95f, 1f);
            }
        texRock.SetPixels(px3); texRock.Apply();
        // bubble: ring + highlight
        const int B = 32; texBubble = NewTex(B, true); var px4 = new Color[B * B];
        for (int y = 0; y < B; y++) for (int x = 0; x < B; x++)
            {
                float dx = (x + 0.5f) / B * 2 - 1, dy = (y + 0.5f) / B * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) / 0.14f);
                float fill = r < 0.9f ? 0.12f : 0f;
                float hl = Mathf.Clamp01(1f - Mathf.Sqrt((dx + 0.35f) * (dx + 0.35f) + (dy - 0.35f) * (dy - 0.35f)) / 0.22f);
                px4[y * B + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(Mathf.Max(ring * 0.85f, fill) + hl));
            }
        texBubble.SetPixels(px4); texBubble.Apply();
    }

    // ------------------------------------------------------------------ lake floor (+ caustics, rocks, weeds)
    static GameObject floorRoot, floorFor, caustic;
    static Material floorMat, causticMat, rockMat, weedMat;
    static Mesh floorMesh, causticMesh, sphereMesh, bladeMesh;
    static readonly List<Transform> rocks = new List<Transform>(), weeds = new List<Transform>();
    static readonly List<float> weedPhase = new List<float>();
    static bool scatterBuilt; static Vector3 scatterCenter;
    static readonly System.Random rnd = new System.Random(4242);
    static float R01() { return (float)rnd.NextDouble(); }

    static void UpdateFloor(float now)
    {
        if (!cFloor) { if (floorRoot != null) { Kill(ref floorRoot); floorFor = null; scatterBuilt = false; rocks.Clear(); weeds.Clear(); L("floor off (cfg)"); } return; }
        if (floorRoot == null || floorFor != water)
        {
            Kill(ref floorRoot); rocks.Clear(); weeds.Clear(); weedPhase.Clear(); scatterBuilt = false;
            Shaders(); Textures();
            if (spriteSh == null) { if (errors++ < 3) L("no Sprites/Default shader - floor skipped"); return; }
            float size = Mathf.Max(wMax.x - wMin.x, wMax.z - wMin.z);
            if (floorMat == null) floorMat = LitMat("Floor", texSand, cFloorColor, 0.08f);
            if (floorMesh != null) UnityEngine.Object.Destroy(floorMesh);
            if (causticMesh != null) UnityEngine.Object.Destroy(causticMesh);
            floorMesh = Quad("Floor", size, size / 7f, true);
            floorRoot = new GameObject("UnderwaterFloor");
            floorRoot.layer = water.layer;
            var f = MeshObj("UnderwaterFloorPlane", floorRoot.transform, floorMesh, floorMat);
            f.GetComponent<MeshRenderer>().receiveShadows = true;
            if (causticMat == null) { causticMat = addSh != null ? new Material(addSh) { name = "Underwater Caustics", mainTexture = texCaustic, renderQueue = 2990 } : SpriteMat("Caustics", texCaustic, 2990); }
            causticMesh = Quad("Caustics", size, size / 9f, true);
            caustic = MeshObj("UnderwaterCaustics", floorRoot.transform, causticMesh, causticMat);
            caustic.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            caustic.SetActive(cCaustics);
            floorFor = water;
            ApplyColours();
            L("floor built: " + size.ToString("0") + " m, " + floorDepth() + " m under the surface, shader " + (litSh != null ? litSh.name : spriteSh.name) + (addSh != null ? ", additive caustics" : ", sprite caustics"));
        }
        floorRoot.transform.position = new Vector3(wCenter.x, waterY - floorDepth(), wCenter.z);
        if (caustic != null)
        {
            if (caustic.activeSelf != cCaustics) caustic.SetActive(cCaustics);
            if (cCaustics) causticMat.mainTextureOffset = new Vector2(Mathf.Sin(now * 0.21f) * 0.35f + now * 0.012f, Mathf.Cos(now * 0.17f) * 0.35f);
        }
        if (k > 0.001f) Scatter();
    }
    static float floorDepth() { return Mathf.Max(1f, cFloorDepth); }

    static void Scatter()
    {
        if (cam == null || floorRoot == null) return;
        Vector3 c = cam.transform.position; c.y = waterY - floorDepth();
        if (!scatterBuilt)
        {
            scatterBuilt = true;
            if (sphereMesh == null) { var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere); sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh; UnityEngine.Object.DestroyImmediate(tmp); }
            if (bladeMesh == null) bladeMesh = Blade();
            if (rockMat == null) rockMat = LitMat("Rock", texRock, new Color(0.30f, 0.29f, 0.27f, 1f), 0.15f);
            if (weedMat == null) weedMat = LitMat("Weed", null, new Color(0.16f, 0.42f, 0.18f, 1f), 0.25f);
            if (cRocks) for (int i = 0; i < cRockCount; i++) rocks.Add(MeshObj("UnderwaterRock", floorRoot.transform, sphereMesh, rockMat).transform);
            if (cWeeds) for (int i = 0; i < cWeedCount; i++) { weeds.Add(MeshObj("UnderwaterWeed", floorRoot.transform, bladeMesh, weedMat).transform); weedPhase.Add(R01() * 6.28f); }
            Place(c);
            L("scattered " + rocks.Count + " rocks + " + weeds.Count + " weeds around the camera");
        }
        else if ((new Vector2(c.x - scatterCenter.x, c.z - scatterCenter.z)).sqrMagnitude > 45f * 45f) Place(c);
    }
    static void Place(Vector3 c)
    {
        scatterCenter = c;
        for (int i = 0; i < rocks.Count; i++)
        {
            float a = R01() * 6.283f, r = 3f + 52f * Mathf.Sqrt(R01()), s = 0.5f + 2.6f * R01() * R01();
            var t = rocks[i];
            t.position = c + new Vector3(Mathf.Cos(a) * r, s * 0.15f, Mathf.Sin(a) * r);
            t.rotation = Quaternion.Euler(R01() * 20f - 10f, R01() * 360f, R01() * 20f - 10f);
            t.localScale = new Vector3(s * (0.8f + 0.5f * R01()), s * (0.35f + 0.35f * R01()), s * (0.7f + 0.5f * R01()));
        }
        for (int i = 0; i < weeds.Count; i++)
        {
            float a = R01() * 6.283f, r = 2f + 45f * Mathf.Sqrt(R01()), h = 1.2f + 3.8f * R01();
            var t = weeds[i];
            t.position = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            t.rotation = Quaternion.Euler(0f, R01() * 360f, 0f);
            t.localScale = new Vector3(0.8f + 0.6f * R01(), h, 1f);
        }
    }
    // a tapered, slightly curved blade 1 m tall, both faces (own normals so the lit shader shades both sides)
    static Mesh Blade()
    {
        const int S = 6; var v = new List<Vector3>(); var n = new List<Vector3>(); var tri = new List<int>();
        for (int side = 0; side < 2; side++)
        {
            int b0 = v.Count;
            for (int i = 0; i <= S; i++)
            {
                float t = i / (float)S, w = 0.16f * (1f - t * 0.85f), bend = 0.25f * t * t;
                v.Add(new Vector3(-w, t, bend)); v.Add(new Vector3(w, t, bend));
                Vector3 nn = side == 0 ? new Vector3(0, -0.25f * t, -1f).normalized : new Vector3(0, 0.25f * t, 1f).normalized;
                n.Add(nn); n.Add(nn);
            }
            for (int i = 0; i < S; i++)
            {
                int a = b0 + i * 2;
                if (side == 0) { tri.Add(a); tri.Add(a + 2); tri.Add(a + 1); tri.Add(a + 1); tri.Add(a + 2); tri.Add(a + 3); }
                else { tri.Add(a); tri.Add(a + 1); tri.Add(a + 2); tri.Add(a + 1); tri.Add(a + 3); tri.Add(a + 2); }
            }
        }
        var m = new Mesh { name = "Underwater Weed" }; m.SetVertices(v); m.SetNormals(n); m.SetTriangles(tri, 0); m.RecalculateBounds();
        return m;
    }

    // ------------------------------------------------------------------ underwater-only objects
    static GameObject uwRoot, shellGo, tintGo, bubbleGo, underA, underB;
    static Material shellMat, tintMat, bubbleMat, underMatA, underMatB;
    static Mesh shellMesh, tintMesh, bubbleMesh, underMeshA, underMeshB;

    static void EnsureUnderwater()
    {
        if (uwRoot != null) return;
        Shaders(); Textures();
        if (spriteSh == null) { if (errors++ < 3) L("no Sprites/Default shader - underwater view skipped"); return; }
        uwRoot = new GameObject("UnderwaterView");
        uwRoot.layer = water.layer;
        // fog shells / tint / bubbles: world-space meshes rebuilt every frame from the camera, root at the origin
        if (shellMat == null) shellMat = SpriteMat("Fog", texWhite, 3100);
        if (bubbleMat == null) bubbleMat = SpriteMat("Bubbles", texBubble, 3110);
        if (tintMat == null) tintMat = SpriteMat("Tint", texWhite, 3120);
        if (shellMesh == null) { shellMesh = new Mesh { name = "Underwater Fog" }; shellMesh.MarkDynamic(); }
        if (tintMesh == null) { tintMesh = new Mesh { name = "Underwater Tint" }; tintMesh.MarkDynamic(); }
        if (bubbleMesh == null) { bubbleMesh = new Mesh { name = "Underwater Bubbles" }; bubbleMesh.MarkDynamic(); }
        shellGo = MeshObj("UnderwaterFog", uwRoot.transform, shellMesh, shellMat);
        tintGo = MeshObj("UnderwaterTint", uwRoot.transform, tintMesh, tintMat);
        bubbleGo = MeshObj("UnderwaterBubbles", uwRoot.transform, bubbleMesh, bubbleMat);
        // underside of the surface: two sheets scrolling against each other, 600 m, following the camera
        if (underMatA == null) { underMatA = SpriteMat("Underside A", texRipple, 3010); underMatB = SpriteMat("Underside B", texRipple, 3011); }
        if (underMeshA == null) { underMeshA = Quad("Underside A", 600f, 600f / 7f, false); underMeshB = Quad("Underside B", 600f, 600f / 11f, false); }
        underA = MeshObj("UnderwaterSurfaceA", uwRoot.transform, underMeshA, underMatA);
        underB = MeshObj("UnderwaterSurfaceB", uwRoot.transform, underMeshB, underMatB);
        bubbles = null;
        ApplyColours();
        L("underwater view built (fog shells " + Mathf.Clamp(cShells, 4, 14) + ", fogEnd " + cFogEnd + " m, " + spriteSh.name + ")");
    }

    static void SetUnderwaterActive(bool on)
    {
        if (uwRoot == null) return;
        if (uwRoot.activeSelf != on) uwRoot.SetActive(on);
        if (on)
        {
            if (underA.activeSelf != cUnderside) { underA.SetActive(cUnderside); underB.SetActive(cUnderside); }
            if (bubbleGo.activeSelf != cBubbles) bubbleGo.SetActive(cBubbles);
        }
    }

    static Color Alpha(Color c, float a) { c.a = a; return c; }
    static void ApplyColours()
    {
        if (floorMat != null) floorMat.color = cFloorColor;
        if (causticMat != null) { Color cc = new Color(0.55f, 0.75f, 0.70f, 0.35f); if (causticMat.HasProperty("_TintColor")) causticMat.SetColor("_TintColor", cc); else causticMat.color = cc; }
    }

    static void UpdateUnderwater(float now, float dt)
    {
        if (uwRoot == null || cam == null) return;
        Vector3 cp = cam.transform.position;
        if (cUnderside)
        {
            underA.transform.position = new Vector3(cp.x, waterY - 0.03f, cp.z);
            underB.transform.position = new Vector3(cp.x, waterY - 0.06f, cp.z);
            // world-locked texture: offset by the camera's position in tiles, plus a slow drift each way
            underMatA.mainTextureOffset = new Vector2(cp.x / 7f + now * 0.020f, cp.z / 7f + Mathf.Sin(now * 0.4f) * 0.05f);
            underMatB.mainTextureOffset = new Vector2(cp.x / 11f - now * 0.013f, cp.z / 11f - now * 0.017f);
            float a = cSurf.a * k;
            underMatA.color = Alpha(cSurf, a * (0.85f + 0.15f * Mathf.Sin(now * 1.7f)));
            underMatB.color = Alpha(cSurf, a * 0.6f * (0.85f + 0.15f * Mathf.Sin(now * 1.3f + 1f)));
        }
        // weeds sway
        for (int i = 0; i < weeds.Count; i++)
        {
            float ph = weedPhase[i];
            weeds[i].localRotation = Quaternion.Euler(Mathf.Sin(now * 0.9f + ph) * 7f, ph * 57.3f, Mathf.Sin(now * 0.7f + ph * 1.7f) * 9f);
        }
        if (cBubbles) StepBubbles(cp, dt, now);
    }

    // ------------------------------------------------------------------ bubbles (simulated in Tick, drawn in PreCull)
    static Vector3[] bubbles; static float[] bSpeed, bSize, bPhase;
    static void StepBubbles(Vector3 cp, float dt, float now)
    {
        int n = Mathf.Clamp(cBubbleCount, 0, 200);
        if (bubbles == null || bubbles.Length != n)
        {
            bubbles = new Vector3[n]; bSpeed = new float[n]; bSize = new float[n]; bPhase = new float[n];
            for (int i = 0; i < n; i++) SpawnBubble(i, cp, true);
        }
        for (int i = 0; i < n; i++)
        {
            Vector3 b = bubbles[i];
            b.y += bSpeed[i] * dt;
            b.x += Mathf.Sin(now * 2.3f + bPhase[i]) * 0.25f * dt;
            b.z += Mathf.Cos(now * 1.9f + bPhase[i]) * 0.25f * dt;
            bubbles[i] = b;
            float dx = b.x - cp.x, dz = b.z - cp.z;
            if (b.y > waterY - 0.05f || b.y > cp.y + 4f || dx * dx + dz * dz > 49f) SpawnBubble(i, cp, false);
        }
    }
    static void SpawnBubble(int i, Vector3 cp, bool anyHeight)
    {
        Vector3 f = cam != null ? cam.transform.forward : Vector3.forward; f.y = 0f;
        Vector3 c = cp + f.normalized * 2.5f;
        float y = anyHeight ? cp.y - 3f + 6f * R01() : cp.y - 3f - 1.5f * R01();
        bubbles[i] = new Vector3(c.x + (R01() * 2f - 1f) * 4.5f, Mathf.Min(y, waterY - 0.2f), c.z + (R01() * 2f - 1f) * 4.5f);
        bSpeed[i] = 0.5f + 1.0f * R01(); bSize[i] = 0.025f + 0.07f * R01() * R01(); bPhase[i] = R01() * 6.28f;
    }

    // ------------------------------------------------------------------ per-camera mesh rebuild (no allocations)
    static readonly List<Vector3> sv = new List<Vector3>(512), bv = new List<Vector3>(256), tv = new List<Vector3>(16);
    static readonly List<Color> sc = new List<Color>(512), bc = new List<Color>(256), tc = new List<Color>(16);
    static readonly List<Vector2> su = new List<Vector2>(512), bu = new List<Vector2>(256), tu = new List<Vector2>(16);
    static readonly List<int> st = new List<int>(1024), bt = new List<int>(512), tt = new List<int>(32);
    static readonly Vector3[] polyA = new Vector3[8], polyB = new Vector3[8];
    static readonly float[] shellD = new float[16];

    static void PreCull(Camera c)
    {
        try
        {
            if (c == null || c != cam || uwRoot == null || !uwRoot.activeSelf || k <= 0.001f) return;
            lastPreCullFrame = Time.frameCount;
            BuildViewMeshes(c);
        }
        catch (Exception e) { Err("precull", e); }
    }

    static Color DepthCol(float y, float a)
    {
        Color col = Color.Lerp(cFog, cDeep, Mathf.Clamp01((waterY - y) / Mathf.Max(1f, cDarkDepth)));
        col.a = a; return col;
    }

    static void BuildViewMeshes(Camera c)
    {
        Transform tf = c.transform;
        Vector3 cp = tf.position, f = tf.forward, u = tf.up, r = tf.right;
        float tanH = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.25f, aspect = Mathf.Max(0.2f, c.aspect) * 1.05f;
        int n = Mathf.Clamp(cShells, 4, 14);
        float d0 = Mathf.Max(c.nearClipPlane * 2.5f, 0.6f), dEnd = Mathf.Clamp(cFogEnd, d0 + 2f, c.farClipPlane * 0.9f);
        for (int i = 0; i < n; i++) shellD[i] = d0 * Mathf.Pow(dEnd / d0, i / (float)(n - 1));

        // fog shells, far to near; shell i covers (d[i-1], d[i]]; the farthest one is opaque
        sv.Clear(); sc.Clear(); su.Clear(); st.Clear();
        for (int i = n - 1; i >= 0; i--)
        {
            float a = i == n - 1 ? 1f : 1f - Mathf.Exp(-Mathf.Max(0f, cDensity) * (shellD[i] - (i > 0 ? shellD[i - 1] : 0f)));
            AddShell(cp, f, u, r, shellD[i], tanH, aspect, a * k, false);
        }
        Push(shellMesh, sv, sc, su, st);

        // near tint (darker with depth), clipped at the surface too (half-in/half-out view at the waterline)
        tv.Clear(); tc.Clear(); tu.Clear(); tt.Clear();
        float depthFrac = Mathf.Clamp01(camDepth / Mathf.Max(1f, cDarkDepth));
        float ta = Mathf.Clamp01(cTint.a + cDarken * depthFrac) * k;
        Color tcol = Color.Lerp(cTint, cDeep * 0.6f, depthFrac);
        tcol.a = ta;
        AddShellInto(tv, tc, tu, tt, cp, f, u, r, Mathf.Max(c.nearClipPlane * 1.5f, 0.12f), tanH * 1.1f, aspect, tcol);
        Push(tintMesh, tv, tc, tu, tt);

        // bubbles: camera-facing quads
        bv.Clear(); bc.Clear(); bu.Clear(); bt.Clear();
        if (cBubbles && bubbles != null)
            for (int i = 0; i < bubbles.Length; i++)
            {
                Vector3 p = bubbles[i]; float s = bSize[i];
                if (Vector3.Dot(p - cp, f) < d0) continue;
                Vector3 rr = r * s, uu = u * s; int b0 = bv.Count;
                bv.Add(p - rr - uu); bv.Add(p + rr - uu); bv.Add(p + rr + uu); bv.Add(p - rr + uu);
                Color bcol = new Color(0.85f, 1f, 1f, 0.75f * k);
                bc.Add(bcol); bc.Add(bcol); bc.Add(bcol); bc.Add(bcol);
                bu.Add(new Vector2(0, 0)); bu.Add(new Vector2(1, 0)); bu.Add(new Vector2(1, 1)); bu.Add(new Vector2(0, 1));
                bt.Add(b0); bt.Add(b0 + 1); bt.Add(b0 + 2); bt.Add(b0); bt.Add(b0 + 2); bt.Add(b0 + 3);
            }
        Push(bubbleMesh, bv, bc, bu, bt);
    }

    static void AddShell(Vector3 cp, Vector3 f, Vector3 u, Vector3 r, float d, float tanH, float aspect, float a, bool flat)
    {
        if (a <= 0.002f) return;
        AddShellInto(sv, sc, su, st, cp, f, u, r, d, tanH, aspect, new Color(-1f, 0f, 0f, a));
    }
    // camera-facing quad at view depth d, clipped to y <= waterY, fan-triangulated.
    // colour.r < 0 => per-vertex depth colour with alpha colour.a; else the given colour.
    static void AddShellInto(List<Vector3> v, List<Color> col, List<Vector2> uv, List<int> tri, Vector3 cp, Vector3 f, Vector3 u, Vector3 r, float d, float tanH, float aspect, Color colour)
    {
        float hh = d * tanH, hw = hh * aspect;
        Vector3 c = cp + f * d;
        polyA[0] = c - r * hw - u * hh; polyA[1] = c + r * hw - u * hh; polyA[2] = c + r * hw + u * hh; polyA[3] = c - r * hw + u * hh;
        int m = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = polyA[i], b = polyA[(i + 1) & 3];
            bool ina = a.y <= waterY, inb = b.y <= waterY;
            if (ina) polyB[m++] = a;
            if (ina != inb) { float t = (waterY - a.y) / (b.y - a.y); polyB[m++] = a + (b - a) * t; }
        }
        if (m < 3) return;
        int b0 = v.Count;
        for (int i = 0; i < m; i++)
        {
            v.Add(polyB[i]);
            col.Add(colour.r < 0f ? DepthCol(polyB[i].y, colour.a) : colour);
            uv.Add(new Vector2(0.5f, 0.5f));
        }
        for (int i = 1; i < m - 1; i++) { tri.Add(b0); tri.Add(b0 + i); tri.Add(b0 + i + 1); }
    }

    static void Push(Mesh mesh, List<Vector3> v, List<Color> c, List<Vector2> uv, List<int> t)
    {
        mesh.Clear(false);
        mesh.SetVertices(v); mesh.SetColors(c); mesh.SetUVs(0, uv);
        mesh.SetTriangles(t, 0, true);
    }

    // ------------------------------------------------------------------ PPv2: RoR2 RampFog colour override (best effort)
    static Type ppVolT, ppProfT, ppLayerT, rampT; static MethodInfo dispatchM, addSettingsM;
    static FieldInfo vWeight, vPrio, vGlobal, vShared, layerMaskF;
    static GameObject ppGo; static Component ppVol; static ScriptableObject ppProf, ppRamp; static float ppW = -1f; static bool ppFailed;

    static void UpdatePP(bool show)
    {
        if (!cPP || !show)
        {
            if (ppVol != null && ((Behaviour)ppVol).enabled) { vWeight.SetValue(ppVol, 0f); ((Behaviour)ppVol).enabled = false; ppW = 0f; Dispatch(); }
            if (!cPP && ppGo != null) DestroyPP();
            return;
        }
        if (ppVol == null && !ppFailed && cam != null) CreatePP();
        if (ppVol == null) return;
        if (!((Behaviour)ppVol).enabled) ((Behaviour)ppVol).enabled = true;
        if (Mathf.Abs(k - ppW) > 0.01f || (k == 1f && ppW != 1f)) { ppW = k; vWeight.SetValue(ppVol, k); Dispatch(); }
    }
    static void Dispatch() { try { if (dispatchM != null) dispatchM.Invoke(null, null); } catch { } }

    static void CreatePP()
    {
        ppFailed = true;   // one attempt per scene
        try
        {
            if (ppVolT == null)
                foreach (var a in FishingScope.Assemblies())
                {
                    string n = a.GetName().Name;
                    if (n == "Unity.Postprocessing.Runtime")
                    {
                        ppVolT = a.GetType("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                        ppProfT = a.GetType("UnityEngine.Rendering.PostProcessing.PostProcessProfile");
                        ppLayerT = a.GetType("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                    }
                    else if (n == "RoR2") rampT = a.GetType("RampFog");
                }
            if (ppVolT == null || ppProfT == null || ppLayerT == null || rampT == null) { L("PP fog: types not found (vol=" + (ppVolT != null) + " ramp=" + (rampT != null) + ") - fog shells only"); return; }
            dispatchM = ppVolT.GetMethod("DispatchVolumeSettingsChangedEvent", All);
            vWeight = ppVolT.GetField("weight"); vPrio = ppVolT.GetField("priority"); vGlobal = ppVolT.GetField("isGlobal"); vShared = ppVolT.GetField("sharedProfile");
            layerMaskF = ppLayerT.GetField("volumeLayer");
            addSettingsM = ppProfT.GetMethod("AddSettings", new[] { typeof(Type) });
            var layerComp = cam.GetComponent(ppLayerT);
            if (layerComp == null) { L("PP fog: camera '" + cam.name + "' has no PostProcessLayer - fog shells only"); return; }
            int mask = ((LayerMask)layerMaskF.GetValue(layerComp)).value, layer = -1;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { layer = i; break; }
            if (layer < 0) { L("PP fog: empty volume layer mask - fog shells only"); return; }

            ppProf = ScriptableObject.CreateInstance(ppProfT); ppProf.name = "Underwater PP";
            ppRamp = (ScriptableObject)addSettingsM.Invoke(ppProf, new object[] { rampT });
            SetRamp();
            ppGo = new GameObject("UnderwaterPPVolume"); ppGo.layer = layer; ppGo.SetActive(false);
            ppVol = ppGo.AddComponent(ppVolT);
            vGlobal.SetValue(ppVol, true); vPrio.SetValue(ppVol, 10000f); vWeight.SetValue(ppVol, 0f); vShared.SetValue(ppVol, ppProf);
            ppGo.SetActive(true); ppW = 0f;
            ppFailed = false;
            L("PP fog: RampFog override volume on layer " + layer + " ('" + LayerMask.LayerToName(layer) + "'), priority 10000");
        }
        catch (Exception e) { Err("PP fog create", e); DestroyPP(); ppFailed = true; }
    }
    static void Over(object settings, string field, object value)
    {
        object p = settings.GetType().GetField(field).GetValue(settings);
        p.GetType().GetField("value").SetValue(p, value);
        p.GetType().GetField("overrideState").SetValue(p, true);
    }
    static void SetRamp()
    {
        if (ppRamp == null) return;
        Over(ppRamp, "enabled", true);
        Over(ppRamp, "fogColorStart", Alpha(cFog, 0f));
        Over(ppRamp, "fogColorMid", Alpha(Color.Lerp(cFog, cDeep, 0.5f), 0.6f));
        Over(ppRamp, "fogColorEnd", Alpha(cDeep, 1f));
        if (cPPIntensity >= 0f) Over(ppRamp, "fogIntensity", cPPIntensity);
        Dispatch();
    }
    static void DestroyPP()
    {
        if (ppGo != null) UnityEngine.Object.Destroy(ppGo);
        if (ppRamp != null) UnityEngine.Object.Destroy(ppRamp);
        if (ppProf != null) UnityEngine.Object.Destroy(ppProf);
        bool had = ppGo != null;
        ppGo = null; ppVol = null; ppRamp = null; ppProf = null; ppW = -1f;
        if (had) Dispatch();
    }

    // ------------------------------------------------------------------ RenderSettings fog / ambient (optional, saved + restored)
    static bool fogApplied; static bool sFog; static FogMode sMode; static Color sFogCol; static float sDens, sStart, sEnd;
    static Color sAmbSky, sAmbEq, sAmbGround; static float sAmbInt; static Color wFogCol, wAmbSky;
    static void UpdateUnityFog(bool show)
    {
        bool want = show && (cUnityFog || cAmbient != 1f);
        if (want)
        {
            if (!fogApplied)
            {
                sFog = RenderSettings.fog; sMode = RenderSettings.fogMode; sFogCol = RenderSettings.fogColor; sDens = RenderSettings.fogDensity;
                sStart = RenderSettings.fogStartDistance; sEnd = RenderSettings.fogEndDistance;
                sAmbSky = RenderSettings.ambientSkyColor; sAmbEq = RenderSettings.ambientEquatorColor; sAmbGround = RenderSettings.ambientGroundColor;
                sAmbInt = RenderSettings.ambientIntensity;
                fogApplied = true;
                L("RenderSettings saved (fog=" + sFog + " " + sMode + " col=" + sFogCol + ")");
            }
            if (cUnityFog)
            {
                RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = Color.Lerp(sFogCol, Color.Lerp(cFog, cDeep, Mathf.Clamp01(camDepth / Mathf.Max(1f, cDarkDepth))), k);
                RenderSettings.fogDensity = Mathf.Lerp(sFog ? sDens : 0f, cUnityFogDensity, k);
            }
            if (cAmbient != 1f)
            {
                float m = Mathf.Lerp(1f, cAmbient, k);
                Color tint = Color.Lerp(Color.white, new Color(0.7f, 1f, 1f), k);
                RenderSettings.ambientSkyColor = sAmbSky * tint * m; RenderSettings.ambientEquatorColor = sAmbEq * tint * m; RenderSettings.ambientGroundColor = sAmbGround * tint * m;
            }
            wFogCol = RenderSettings.fogColor; wAmbSky = RenderSettings.ambientSkyColor;
        }
        else if (fogApplied)
        {
            fogApplied = false;
            // a new scene brings its own settings: only restore if they still hold what we wrote
            if (RenderSettings.fogColor == wFogCol && RenderSettings.ambientSkyColor == wAmbSky)
            {
                RenderSettings.fog = sFog; RenderSettings.fogMode = sMode; RenderSettings.fogColor = sFogCol; RenderSettings.fogDensity = sDens;
                RenderSettings.fogStartDistance = sStart; RenderSettings.fogEndDistance = sEnd;
                RenderSettings.ambientSkyColor = sAmbSky; RenderSettings.ambientEquatorColor = sAmbEq; RenderSettings.ambientGroundColor = sAmbGround;
                RenderSettings.ambientIntensity = sAmbInt;
                L("RenderSettings restored");
            }
            else L("RenderSettings changed by someone else (scene/day-night) - not restored");
        }
    }

    // ------------------------------------------------------------------ cfg
    static DateTime cfgStamp;
    static void ReadCfg()
    {
        try
        {
            if (!File.Exists(CfgPath)) { if (cfgStamp != DateTime.MinValue) { cfgStamp = DateTime.MinValue; L("cfg removed - keeping the last values"); } return; }
            DateTime st = File.GetLastWriteTimeUtc(CfgPath);
            if (st == cfgStamp) return;
            cfgStamp = st;
            var sb = new System.Text.StringBuilder();
            foreach (string raw in File.ReadAllLines(CfgPath))
            {
                string line = raw; int h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                foreach (string tok in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('='); if (eq <= 0) continue;
                    string key = tok.Substring(0, eq).Trim().ToLowerInvariant(), val = tok.Substring(eq + 1).Trim();
                    if (Set(key, val)) sb.Append(' ').Append(key).Append('=').Append(val); else sb.Append(" ?").Append(key);
                }
            }
            cShells = Mathf.Clamp(cShells, 4, 14);
            ApplyColours(); SetRamp();
            if (caustic != null) caustic.SetActive(cCaustics && cFloor);
            bool l = cLog; cLog = true; L("cfg loaded:" + sb); cLog = l;
        }
        catch (Exception e) { Err("cfg", e); }
    }
    static float Fl(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
    static bool B(string s) { return s == "1" || s.ToLowerInvariant() == "true" || s.ToLowerInvariant() == "on"; }
    static Color Col(string s, float defA)
    {
        string[] p = s.Split(',');
        return new Color(Fl(p[0]), Fl(p[1]), Fl(p[2]), p.Length > 3 ? Fl(p[3]) : defA);
    }
    static bool Set(string key, string v)
    {
        switch (key)
        {
            case "enabled": cEnabled = B(v); return true;
            case "log": cLog = B(v); return true;
            case "fogcolor": cFog = Col(v, 1f); return true;
            case "deepcolor": cDeep = Col(v, 1f); return true;
            case "density": cDensity = Fl(v); return true;
            case "fogend": cFogEnd = Fl(v); return true;
            case "shells": cShells = (int)Fl(v); return true;
            case "tint": cTint = Col(v, cTint.a); return true;
            case "darken": cDarken = Fl(v); return true;
            case "darkdepth": cDarkDepth = Fl(v); return true;
            case "surfcolor": cSurf = Col(v, cSurf.a); return true;
            case "underside": cUnderside = B(v); return true;
            case "floor": cFloor = B(v); return true;
            case "floordepth": cFloorDepth = Fl(v); return true;
            case "floorcolor": cFloorColor = Col(v, 1f); return true;
            case "caustics": cCaustics = B(v); return true;
            case "rocks": cRocks = B(v); return true;
            case "weeds": cWeeds = B(v); return true;
            case "rockcount": cRockCount = Mathf.Clamp((int)Fl(v), 0, 200); return true;
            case "weedcount": cWeedCount = Mathf.Clamp((int)Fl(v), 0, 300); return true;
            case "bubbles": cBubbles = B(v); return true;
            case "bubblecount": cBubbleCount = Mathf.Clamp((int)Fl(v), 0, 200); return true;
            case "ppfog": cPP = B(v); return true;
            case "ppintensity": cPPIntensity = Fl(v); return true;
            case "unityfog": cUnityFog = B(v); return true;
            case "unityfogdensity": cUnityFogDensity = Fl(v); return true;
            case "ambient": cAmbient = Fl(v); return true;
            case "fadein": cFadeIn = Fl(v); return true;
            case "fadeout": cFadeOut = Fl(v); return true;
        }
        return false;
    }
}
