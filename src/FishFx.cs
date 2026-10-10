// SHARED SCHOOL (catchfish): the swimming fish are no longer a random local school. The host's fishing mod owns the
// school and broadcasts it ([FT] SCHOOL / SFISH / SGONE, see FishSchool.cs); this file renders exactly those fish from the
// shared deterministic paths + the host clock, so every player sees the same fish - and casting near one catches it.
// FishFx - visible fish for the Risk of Rain 2 fishing game (local visuals only, changes no gameplay).
//  * 16 textured fish swim 0.3-1.5 m under the lake ("FishingWater" on the host, "FishingClientWater" on guests)
//    within ~25 m of the local player: wandering, tail wiggle, the occasional jump with a splash ring.
//  * When the fishing mod announces "[FT] CATCH|<name>|<species>|<value>|<total>" in chat, that species
//    flops above the catcher's head for ~3 s with a "<species> $<value>" label, then fades out.
// Compiled twice: host build (class FishFx<N>) and guest build with -define:FISHFX_CLIENT (class FishFxClient).
// All Unity objects are created on the main thread (from RoR2Application.onFixedUpdate / MonoBehaviour.Update).
// Disable: set the static field  enabled = false  (fish and trophies are hidden immediately).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

#if FISHFX_CLIENT
public static class FishFxClient
#else
public static class FishFx3
#endif
{
#if FISHFX_CLIENT
    const string Me = "FishFxClient";
#else
    const string Me = "FishFx3";
#endif
    public static bool enabled = true;
    public static int version = 4;   // 2 = 14 species, 3 = shared host school (catchable fish), 4 = mod 2.0
    public static bool running;
    public static string testTrophy;            // "name|species|value" - set from a test class, shown on the main thread (no score)
    public static int texturesMade, fishSpawned, trophiesShown, jumps;
#if FISHFX_CLIENT
    public static string savePreviewDir;        // optional: in-game PNG copies of the textures
#else
    public static string savePreviewDir = Path.Combine(Path.GetTempPath(), "fishfx_ingame_previews");
#endif

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_fishfx.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly ror2;
    static Type runType, nuType, chatType, camRigType;
    static readonly System.Random rng = new System.Random();
    static int errors;
    static Action fixedHook;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Me + ": " + s + Environment.NewLine); } catch { } }

    static object Get(object o, string name)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(name, All);
        return f != null ? f.GetValue(o) : null;
    }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }
    static bool Dead(object o) { return o == null || ((o is UnityEngine.Object) && (UnityEngine.Object)o == null); }
    static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
        catch { return new Type[0]; }
    }

    // ------------------------------------------------------------------ install (injector thread: no Unity objects here)
    public static void Run()
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "RoR2") ror2 = a;
            // already loaded by an earlier injection of the SAME class name? then do nothing
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(FishFxMarker).Assembly) continue;
                Type t = asm.GetType(Me);
                if (t == null) continue;
                FieldInfo r = t.GetField("running", BindingFlags.Public | BindingFlags.Static), en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                FieldInfo vf = t.GetField("version", BindingFlags.Public | BindingFlags.Static);
                int ov = vf != null ? Convert.ToInt32(vf.GetValue(null)) : 1;
                if (r != null && (bool)r.GetValue(null) && en != null && (bool)en.GetValue(null) && ov >= version) { Log("already running - nothing changed"); return; }
            }
#if FISHFX_CLIENT
            // the guest payload never touches the host's own FishFx<N> (this game is the host if one is running)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (Type t in SafeTypes(asm))
                {
                    int hn;
                    if (!t.Name.StartsWith("FishFx") || !int.TryParse(t.Name.Substring(6), out hn)) continue;
                    FieldInfo hr = t.GetField("running", BindingFlags.Public | BindingFlags.Static), he = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (hr != null && he != null && (bool)hr.GetValue(null) && (bool)he.GetValue(null)) { Log("host detected (" + t.Name + " is running) - guest fish not started"); return; }
                }
#endif
            // switch off older / other FishFx versions (their root objects hide themselves)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(FishFxMarker).Assembly) continue;
                foreach (Type t in SafeTypes(asm))
#if FISHFX_CLIENT
                    if (t.Name == Me)   // only older guest copies
#else
                    if (t.Name.StartsWith("FishFx") && !t.Name.StartsWith("FishFxTest"))
#endif
                    {
                        FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                        if (en != null && en.FieldType == typeof(bool)) { en.SetValue(null, false); Log("disabled old " + t.Name); }
                    }
            }
            runType = ror2.GetType("RoR2.Run");
            nuType = ror2.GetType("RoR2.NetworkUser");
            chatType = ror2.GetType("RoR2.Chat");
            camRigType = ror2.GetType("RoR2.CameraRigController");
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc != null && cc.EventHandlerType == typeof(Action)) { Action c = OnChatChanged; cc.AddEventHandler(null, c); }
            // first fixed update (main thread) builds the MonoBehaviour that does everything else
            fixedHook = FirstTick;
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, fixedHook);
            running = true;
            Log("installed (chatHook=" + (cc != null) + ")");
        }
        catch (Exception e) { Log("install error: " + e); }
    }

    static GameObject root;
    static void FirstTick()
    {
        try
        {
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).RemoveEventHandler(null, fixedHook);
            if (root != null) return;
            root = new GameObject("FishFxRoot_" + Me);
            UnityEngine.Object.DontDestroyOnLoad(root);
            #if FISHFX_CLIENT
            root.AddComponent<FishFxClientBehaviour>();
#else
            root.AddComponent<FishFx3Behaviour>();
#endif
            // seed the chat-seen list so old catches aren't replayed
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log != null) foreach (object l in log) Seen(l);
            Log("main-thread root created");
        }
        catch (Exception e) { Log("first tick error: " + e); }
    }

    // ------------------------------------------------------------------ chat
    static readonly List<object> seen = new List<object>();
    static readonly List<string> chatQueue = new List<string>();
    static bool Seen(object line)
    {
        foreach (object s in seen) if (ReferenceEquals(s, line)) return true;
        seen.Add(line); if (seen.Count > 80) seen.RemoveAt(0);
        return false;
    }
    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null) return;
            for (int i = Math.Max(0, log.Count - 6); i < log.Count; i++)
            {
                string s = log[i] as string;
                if (s == null || Seen(s)) continue;
                if (s.Contains("[FT] CATCH|") || s.Contains("[FT] SCHOOL|") || s.Contains("[FT] SFISH|") || s.Contains("[FT] SGONE|")) lock (chatQueue) chatQueue.Add(s);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ species data
    static readonly string[] Species = { "Minnow", "Perch", "Trout", "Bass", "Salmon", "Swordfish", "Golden Koi", "Moon Eel", "Old Boot",
                                         "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };
    static readonly float[] Length = { 0.3f, 0.5f, 0.7f, 0.8f, 1.0f, 2.0f, 0.7f, 1.8f, 0.45f, 0.3f, 0.9f, 0.5f, 1.0f, 1.2f };
    static readonly float[] Weight = { 20, 16, 14, 10, 9, 3, 4, 2, 4, 14, 12, 8, 3, 2 };
    static readonly float[] Speed = { 1.6f, 1.1f, 1.3f, 1.0f, 1.4f, 2.2f, 0.7f, 0.9f, 0.15f, 1.2f, 0.8f, 0.6f, 0.7f, 1.1f };
    const int Angler = 12, Ghost = 13;
    static int SpeciesIndex(string s) { for (int i = 0; i < Species.Length; i++) if (string.Equals(Species[i], s, StringComparison.OrdinalIgnoreCase)) return i; return -1; }

    static Texture2D[] tex = new Texture2D[14];
    static Material[] matUnder = new Material[14], matAbove = new Material[14];
    static Mesh fishMesh, ringMesh;
    static Material ringMat;
    static Shader sprite;
    static int texNext;

    static bool BuildAssets()   // one species per call (spreads the ~20 ms per texture over frames); true when all done
    {
        if (sprite == null)
        {
            sprite = Shader.Find("Sprites/Default");
            if (sprite == null) sprite = Shader.Find("Unlit/Transparent");
            Log("shader " + (sprite != null ? sprite.name : "NONE"));
            fishMesh = CrossedQuads();
            ringMesh = FlatQuad();
        }
        if (texNext < Species.Length)
        {
            int i = texNext++;
            byte[] px = FishArt.RenderBytesBottomUp(Species[i]);
            var t = new Texture2D(FishArt.W, FishArt.H, TextureFormat.RGBA32, true);
            var cols = new Color32[FishArt.W * FishArt.H];
            for (int k = 0; k < cols.Length; k++) cols[k] = new Color32(px[4 * k], px[4 * k + 1], px[4 * k + 2], px[4 * k + 3]);
            t.SetPixels32(cols);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.name = "FishFx_" + Species[i];
            t.Apply(true);
            tex[i] = t;
            matUnder[i] = new Material(sprite); matUnder[i].mainTexture = t; matUnder[i].color = new Color(0.72f, 0.88f, 1f, 0.82f); matUnder[i].renderQueue = 3050;
            matAbove[i] = new Material(sprite); matAbove[i].mainTexture = t; matAbove[i].color = Color.white; matAbove[i].renderQueue = 3100;
            if (i == Ghost) { matUnder[i].color = new Color(0.8f, 1f, 1f, 0.5f); matAbove[i].color = new Color(1f, 1f, 1f, 0.7f); }   // semi-transparent
            texturesMade++;
            SavePng(t, Species[i]);
            if (texNext == Species.Length)
            {
                // splash ring texture
                var rt = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                var rc = new Color32[64 * 64];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                    {
                        float dx = (x - 31.5f) / 31.5f, dy = (y - 31.5f) / 31.5f, d = (float)Math.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1 - Math.Abs(d - 0.8f) / 0.12f) + 0.25f * Mathf.Clamp01(1 - Math.Abs(d - 0.45f) / 0.1f);
                        rc[y * 64 + x] = new Color32(235, 245, 255, (byte)(Mathf.Clamp01(a) * 220));
                    }
                rt.SetPixels32(rc); rt.wrapMode = TextureWrapMode.Clamp; rt.Apply();
                ringMat = new Material(sprite); ringMat.mainTexture = rt; ringMat.renderQueue = 3101;
                Log("textures created: " + texturesMade + " (128x64 each) + splash ring");
            }
            return false;
        }
        return true;
    }

    static void SavePng(Texture2D t, string name)
    {
        if (savePreviewDir == null) return;
        try
        {
            Type ic = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type x = a.GetType("UnityEngine.ImageConversion"); if (x != null) ic = x; }
            MethodInfo enc = ic == null ? null : ic.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });
            if (enc == null) { Log("EncodeToPNG not found"); savePreviewDir = null; return; }
            Directory.CreateDirectory(savePreviewDir);
            File.WriteAllBytes(Path.Combine(savePreviewDir, name + ".png"), (byte[])enc.Invoke(null, new object[] { t }));
        }
        catch (Exception e) { Log("png save error: " + (e.InnerException ?? e).Message); savePreviewDir = null; }
    }

    static Mesh CrossedQuads()   // unit fish: length 1 along +X (head at +X), side quad 0.5 tall (XY), top quad 0.22 wide (XZ)
    {
        var m = new Mesh(); m.name = "FishFxCrossedQuads";
        float hy = 0.25f, hz = 0.11f;
        m.vertices = new[] {
            new Vector3(-0.5f, -hy, 0), new Vector3(0.5f, -hy, 0), new Vector3(0.5f, hy, 0), new Vector3(-0.5f, hy, 0),
            new Vector3(-0.5f, 0, hz), new Vector3(0.5f, 0, hz), new Vector3(0.5f, 0, -hz), new Vector3(-0.5f, 0, -hz) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                       new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
        m.RecalculateBounds();
        return m;
    }
    static Mesh FlatQuad()
    {
        var m = new Mesh(); m.name = "FishFxRing";
        m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateBounds();
        return m;
    }

    static GameObject MakeObj(string name, Mesh mesh, Material mat, out Renderer rend)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        rend = mr;
        return go;
    }

    // ------------------------------------------------------------------ swimming fish
    class Fish
    {
        public GameObject go; public Transform tf; public Renderer rend; public int sp; public bool above;
        public int sid = -1;   // shared-school fish id (-1 = free in the pool)
        public float x, y, z, heading, turn, speed, depth, phase, wigFreq, wigAmp;
        public float jumpT = -1, jumpBaseY; public float nextJumpCheck;
        public GameObject lure;   // Anglerfish: small cyan point light at the lure
    }
    const int FishCount = 16;
    static readonly List<Fish> fish = new List<Fish>();

    class Ring { public GameObject go; public Transform tf; public float t0 = -100, x, y, z, size; }
    static readonly Ring[] rings = new Ring[4];
    static int ringNext;

    static int PickSpecies()
    {
        float tot = 0; foreach (float w in Weight) tot += w;
        float r = (float)rng.NextDouble() * tot;
        for (int i = 0; i < Weight.Length; i++) { r -= Weight[i]; if (r <= 0) return i; }
        return 0;
    }

    static void Place(Fish f, Vector3 c, float surf, bool anywhere)
    {
        double ang = rng.NextDouble() * Math.PI * 2;
        float rad = anywhere ? 4f + (float)Math.Sqrt(rng.NextDouble()) * 21f : 17f + (float)rng.NextDouble() * 8f;
        f.x = c.x + (float)Math.Cos(ang) * rad; f.z = c.z + (float)Math.Sin(ang) * rad;
        f.depth = f.sp == 8 ? 1.2f + (float)rng.NextDouble() * 0.3f : 0.3f + (float)rng.NextDouble() * 1.2f;
        f.y = surf - f.depth;
        // swim roughly across / toward the player area
        f.heading = (float)(Math.Atan2(c.z - f.z, c.x - f.x) + (rng.NextDouble() - 0.5) * 2.0);
        f.jumpT = -1;
    }

    static void InitFish(Fish f, int sp)
    {
        f.sp = sp;
        f.speed = Speed[sp] * (0.75f + (float)rng.NextDouble() * 0.5f);
        f.phase = (float)rng.NextDouble() * 10f;
        f.wigFreq = sp == 5 ? 5f : sp == 7 ? 4f : sp == 8 ? 0.6f : 7f + (float)rng.NextDouble() * 4f;
        f.wigAmp = sp == 8 ? 6f : sp == 7 ? 22f : 13f;
        f.go.name = "FishFx_" + Species[sp];
        f.rend.sharedMaterial = matUnder[sp]; f.above = false;
        float L = Length[sp];
        f.tf.localScale = new Vector3(L, L, L);
        if (sp == Angler && f.lure == null)
        {
            try
            {
                f.lure = new GameObject("FishFxLure");
                f.lure.transform.SetParent(f.tf, false);
                f.lure.transform.localPosition = new Vector3(0.33f, 0.19f, 0f);   // lure bulb in the 128x64 art
                var lt = f.lure.AddComponent<Light>();
                lt.type = LightType.Point; lt.color = new Color(0.3f, 0.95f, 1f); lt.range = 2.5f; lt.intensity = 2f; lt.shadows = LightShadows.None;
            }
            catch (Exception e) { Log("lure light error: " + e.Message); }
        }
        if (f.lure != null) f.lure.SetActive(sp == Angler);
    }

    static void Splash(float x, float y, float z, float size)
    {
        if (ringMat == null) return;
        int idx = ringNext; ringNext = (ringNext + 1) % rings.Length;
        Ring r = rings[idx];
        if (r == null || r.go == null)
        {
            r = new Ring(); Renderer rr;
            r.go = MakeObj("FishFxSplash", ringMesh, ringMat, out rr); r.tf = r.go.transform;
            rings[idx] = r;
        }
        r.t0 = Time.time; r.x = x; r.y = y + 0.03f; r.z = z; r.size = size;
        r.go.SetActive(true);
    }

    // ------------------------------------------------------------------ trophies
    class Trophy { public GameObject go; public Transform tf; public Material mat; public string who, label; public int sp; public float t0, size; public Transform body; }
    static readonly List<Trophy> trophies = new List<Trophy>();

    static void ShowTrophy(string who, string species, string value)
    {
        int sp = SpeciesIndex(species);
        if (sp < 0 || matAbove[sp] == null) { Log("trophy: unknown species '" + species + "' or textures not ready"); return; }
        Transform body = BodyTransform(who);
        if (body == null) { Log("trophy: no body for " + who); return; }
        for (int i = trophies.Count - 1; i >= 0; i--) if (trophies[i].who == who) { KillTrophy(trophies[i]); trophies.RemoveAt(i); }
        var tr = new Trophy { who = who, sp = sp, t0 = Time.time, body = body, label = Species[sp] + " $" + value };
        tr.mat = new Material(matAbove[sp]); tr.mat.renderQueue = 3150;
        Renderer r;
        tr.go = MakeObj("FishFxTrophy_" + who, fishMesh, tr.mat, out r); tr.tf = tr.go.transform;
        tr.size = Mathf.Clamp(Length[sp] * 1.25f, 0.8f, 2.2f);
        trophies.Add(tr);
        trophiesShown++;
        Log("trophy shown: " + who + " " + tr.label + " (size " + tr.size.ToString("0.00") + " m)");
    }
    static void KillTrophy(Trophy t) { try { if (t.go != null) UnityEngine.Object.Destroy(t.go); if (t.mat != null) UnityEngine.Object.Destroy(t.mat); } catch { } }

    static Transform BodyTransform(string name)
    {
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
            {
                if (name != null && Convert.ToString(Get(nu, "userName")) != name) continue;
                if (name == null) { object l = Get(nu, "isLocalPlayer"); if (!(l is bool) || !(bool)l) continue; }
                object master = Get(nu, "master");
                if (Dead(master)) return null;
                object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
                if (Dead(body)) return null;
                return ((Component)body).transform;
            }
        }
        catch { }
        return null;
    }

    static Camera Cam()
    {
        try
        {
            IList rigs = GetStatic(camRigType, "readOnlyInstancesList") as IList;
            if (rigs != null)
                foreach (object r in rigs)
                {
                    Camera c = Get(r, "sceneCam") as Camera;
                    if (c != null && c.isActiveAndEnabled) return c;
                }
        }
        catch { }
        return Camera.main;
    }

    // ------------------------------------------------------------------ per-frame
    static float nextSlow;
    static bool runActive, haveLake, lakeWarned;
    static GameObject lake;
    static string lakeName;
    static float surfY;
    static Transform localBody;
    static Camera cam;
    static bool wasShown;

    public static void Frame()
    {
        if (!enabled)
        {
            if (root != null && root.transform.childCount > 0)
            {
                foreach (Transform c in root.transform) c.gameObject.SetActive(false);
            }
            return;
        }
        try
        {
            if (!BuildAssets()) return;
            float t = Time.time, dt = Mathf.Min(Time.deltaTime, 0.1f);

            if (t >= nextSlow) SlowTick(t);

            // test hook + chat catches
            if (testTrophy != null)
            {
                string[] p = testTrophy.Split('|'); testTrophy = null;
                Log("TEST trophy " + string.Join(",", p));
                if (p.Length >= 3) ShowTrophy(p[0], p[1], p[2]);
            }
            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (lines != null)
                foreach (string raw in lines)
                {
                    string plain = Regex.Replace(raw, "<[^>]*>", "");
                    if (plain.Contains("[FT] S")) { SchoolLine(plain); continue; }
                    int i = plain.IndexOf("[FT] CATCH|");
                    if (i < 0) continue;
                    string[] p = plain.Substring(i + 11).Trim().Split('|');
                    if (p.Length >= 3) { Log("catch seen in chat: " + p[0] + " / " + p[1] + " / $" + p[2]); if (runActive) ShowTrophy(p[0], p[1], p[2]); }
                }

            bool show = runActive && haveLake && localBody != null && view.haveSchool;
            if (show)
            {
                if (!wasShown) Log("fish visible on " + lakeName + " (surface Y=" + surfY.ToString("0.00") + ")");
                SchoolFrame(t, dt);
            }
            else if (wasShown) { foreach (Fish f in fish) f.go.SetActive(false); Log("fish hidden (run=" + runActive + " lake=" + haveLake + " body=" + (localBody != null) + " school=" + view.haveSchool + ")"); }
            wasShown = show;
            Rings(t);
            Trophies(t);
        }
        catch (Exception e) { if (errors++ < 15) Log("frame error: " + e); }
    }

    static void SlowTick(float t)
    {
        nextSlow = t + 1f;
        runActive = !Dead(GetStatic(runType, "instance"));
        localBody = runActive ? BodyTransform(null) : null;
        cam = Cam();
        if (lake == null)
        {
            lake = GameObject.Find("FishingWater");
            if (lake == null) lake = GameObject.Find("FishingClientWater");
            if (lake != null) { lakeName = lake.name; lakeWarned = false; }
        }
        haveLake = false;
        if (lake != null && lake.activeInHierarchy)
        {
            Renderer r = lake.GetComponent<Renderer>();
            float y = r != null ? r.bounds.max.y : lake.transform.position.y;
            if (Math.Abs(y - surfY) > 0.5f && fish.Count > 0) foreach (Fish f in fish) f.y += y - surfY;
            surfY = y;
            haveLake = true;
            if (localBody != null && surfY > localBody.position.y + 30f)
            {
                haveLake = false;   // lake far above the player looks like a broken stage setup: don't draw fish in the sky
                if (!lakeWarned) { lakeWarned = true; Log("lake " + lakeName + " at Y=" + surfY.ToString("0.0") + " is >30 m above the player (" + localBody.position.y.ToString("0.0") + ") - fish paused"); }
            }
        }
    }

    static void SwimFish(float t, float dt, Vector3 c)
    {
        if (fish.Count == 0)
        {
            for (int i = 0; i < FishCount; i++)
            {
                var f = new Fish(); Renderer r;
                f.go = MakeObj("FishFx_fish", fishMesh, matUnder[0], out r); f.tf = f.go.transform; f.rend = r;
                InitFish(f, PickSpecies());
                Place(f, c, surfY, true);
                f.nextJumpCheck = t + (float)rng.NextDouble() * 5f;
                fish.Add(f);
            }
            fishSpawned += FishCount;
            var counts = new int[Species.Length]; foreach (Fish f in fish) counts[f.sp]++;
            var parts = new List<string>(); for (int i = 0; i < Species.Length; i++) if (counts[i] > 0) parts.Add(Species[i] + " x" + counts[i]);
            Log("fish spawned: " + FishCount + " (" + string.Join(", ", parts.ToArray()) + ") around (" + c.x.ToString("0") + ", " + c.z.ToString("0") + ")");
        }
        int respawned = 0;
        foreach (Fish f in fish)
        {
            if (!f.go.activeSelf) f.go.SetActive(true);
            float dx = f.x - c.x, dz = f.z - c.z, d2 = dx * dx + dz * dz;
            if (d2 > 27f * 27f && f.jumpT < 0)
            {
                InitFish(f, PickSpecies());
                Place(f, c, surfY, d2 > 70f * 70f);   // teleported / new stage: scatter everywhere; else enter from the edge
                respawned++;
            }
            // wander
            f.turn += ((float)rng.NextDouble() - 0.5f) * 3f * dt;
            f.turn = Mathf.Clamp(f.turn, -0.7f, 0.7f) * (1 - 0.3f * dt);
            if (d2 > 20f * 20f)   // steer back toward the player area
            {
                float want = (float)Math.Atan2(-dz, -dx);
                float diff = Mathf.DeltaAngle(f.heading * Mathf.Rad2Deg, want * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                f.turn += Mathf.Clamp(diff, -1, 1) * 0.8f * dt;
            }
            f.heading += f.turn * dt;
            float spd = f.jumpT >= 0 ? f.speed * 1.6f + 1f : f.speed * (0.8f + 0.3f * Mathf.Sin(t * 0.7f + f.phase));
            f.x += Mathf.Cos(f.heading) * spd * dt; f.z += Mathf.Sin(f.heading) * spd * dt;

            float pitch = 0, y;
            if (f.jumpT >= 0)
            {
                f.jumpT += dt / 1.1f;
                float k = f.jumpT;
                float peak = surfY + 0.6f + Length[f.sp] * 0.5f;
                y = Mathf.Lerp(f.jumpBaseY, f.jumpBaseY, k) + (peak - f.jumpBaseY) * 4f * k * (1 - k);
                float vy = (peak - f.jumpBaseY) * 4f * (1 - 2 * k) / 1.1f;
                pitch = Mathf.Atan2(vy, spd) * Mathf.Rad2Deg;
                bool up = y > surfY;
                if (up != f.above) { f.above = up; f.rend.sharedMaterial = up ? matAbove[f.sp] : matUnder[f.sp]; Splash(f.x, surfY, f.z, 0.6f + Length[f.sp]); }
                if (k >= 1) { f.jumpT = -1; y = f.jumpBaseY; if (f.above) { f.above = false; f.rend.sharedMaterial = matUnder[f.sp]; } }
            }
            else
            {
                y = surfY - f.depth + 0.06f * Mathf.Sin(t * 1.3f + f.phase);
                if (t >= f.nextJumpCheck)
                {
                    f.nextJumpCheck = t + 1f;
                    if (f.sp != 8 && rng.NextDouble() < 1.0 / 60.0 && d2 < 22f * 22f) { f.jumpT = 0; f.jumpBaseY = y; jumps++; }
                }
            }
            f.y = y;
            float wig = f.wigAmp * Mathf.Sin(t * f.wigFreq + f.phase);
            if (f.jumpT >= 0) wig *= 1.8f;
            f.tf.SetPositionAndRotation(new Vector3(f.x, y, f.z), Quaternion.Euler(0, -f.heading * Mathf.Rad2Deg + wig, pitch));
        }
        if (respawned > 0) { fishSpawned += respawned; }
    }

    // ------------------------------------------------------------------ shared school (host-authoritative)
    static readonly SchoolView view = new SchoolView();
    static readonly Dictionary<int, Fish> byId = new Dictionary<int, Fish>();
    static int schoolLogs;
    public static int SchoolCount { get { return view.fish.Count; } }
    public static float SchoolOffset { get { return view.offset; } }

    static void SchoolLine(string plain)
    {
        string r = view.Handle(plain, Time.realtimeSinceStartup);
        if (r != null && (schoolLogs++ < 300 || r.StartsWith("SCHOOL seq"))) Log("school: " + r);
    }

    static void SchoolFrame(float t, float dt)
    {
        float ht = Time.realtimeSinceStartup + view.offset;   // host clock
        List<int> gone = null;
        foreach (var kv in byId) if (!view.fish.ContainsKey(kv.Key)) (gone = gone ?? new List<int>()).Add(kv.Key);
        if (gone != null)
            foreach (int id in gone)
            {
                Fish f = byId[id]; byId.Remove(id);
                if (view.caughtIds.Contains(id)) Splash(f.x, surfY, f.z, 0.8f + Length[f.sp]);
                f.go.SetActive(false); f.sid = -1;
            }
        view.caughtIds.Clear();
        foreach (SchoolFish sf in view.fish.Values)
        {
            Fish f;
            if (!byId.TryGetValue(sf.id, out f))
            {
                f = null;
                foreach (Fish p in fish) if (p.sid < 0) { f = p; break; }
                if (f == null)
                {
                    f = new Fish(); Renderer r;
                    f.go = MakeObj("FishFx_fish", fishMesh, matUnder[0], out r); f.tf = f.go.transform; f.rend = r;
                    fish.Add(f);
                }
                f.sid = sf.id; InitFish(f, sf.sp); f.jumpT = -1; byId[sf.id] = f; fishSpawned++;
            }
            else if (f.sp != sf.sp) InitFish(f, sf.sp);
            if (!f.go.activeSelf) f.go.SetActive(true);
            float x, z, x2, z2;
            sf.Pos(ht, out x, out z); sf.Pos(ht + 0.15f, out x2, out z2);
            float vx = (x2 - x) / 0.15f, vz = (z2 - z) / 0.15f, spd = Mathf.Sqrt(vx * vx + vz * vz);
            if (spd > 0.05f) f.heading = Mathf.Atan2(vz, vx);
            f.x = x; f.z = z;
            float baseY = surfY - sf.Depth, pitch = 0, y;
            float jk = sf.Jump(ht);
            if (jk >= 0)
            {
                float peak = surfY + 0.6f + Length[f.sp] * 0.5f;
                y = baseY + (peak - baseY) * 4f * jk * (1 - jk);
                float vy = (peak - baseY) * 4f * (1 - 2 * jk) / 1.1f;
                pitch = Mathf.Atan2(vy, Mathf.Max(spd, 0.8f)) * Mathf.Rad2Deg;
                if (f.jumpT < 0) { f.jumpT = 0; jumps++; }
            }
            else { y = baseY + 0.06f * Mathf.Sin(t * 1.3f + f.phase); f.jumpT = -1; }
            bool up = y > surfY;
            if (up != f.above) { f.above = up; f.rend.sharedMaterial = up ? matAbove[f.sp] : matUnder[f.sp]; Splash(x, surfY, z, 0.6f + Length[f.sp]); }
            f.y = y;
            float wig = f.wigAmp * Mathf.Sin(t * f.wigFreq + f.phase);
            if (jk >= 0) wig *= 1.8f;
            f.tf.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0, -f.heading * Mathf.Rad2Deg + wig, pitch));
        }
    }

    static void Rings(float t)
    {
        foreach (Ring r in rings)
        {
            if (r == null || r.go == null || !r.go.activeSelf) continue;
            float k = (t - r.t0) / 0.7f;
            if (k >= 1 || k < 0) { r.go.SetActive(false); continue; }
            float s = r.size * (0.4f + 1.4f * k);
            r.tf.SetPositionAndRotation(new Vector3(r.x, r.y, r.z), Quaternion.identity);
            r.tf.localScale = new Vector3(s, s, s);
        }
    }

    static void Trophies(float t)
    {
        if (trophies.Count == 0) return;
        Vector3 camFwd = cam != null ? cam.transform.forward : Vector3.forward;
        for (int i = trophies.Count - 1; i >= 0; i--)
        {
            Trophy tr = trophies[i];
            float age = t - tr.t0;
            if (age > 3.2f || tr.body == null || tr.go == null) { KillTrophy(tr); trophies.RemoveAt(i); continue; }
            float pop = Mathf.Clamp01(age / 0.25f);
            float fade = 1 - Mathf.Clamp01((age - 2.5f) / 0.7f);
            Vector3 p = tr.body.position + Vector3.up * (2.3f + tr.size * 0.25f + 0.18f * Mathf.Abs(Mathf.Sin(age * 7f)));
            Vector3 f = camFwd; f.y = 0; if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
            float flop = 38f * Mathf.Sin(age * 13f) * (1 - 0.4f * age / 3f);
            Quaternion q = Quaternion.LookRotation(f.normalized, Vector3.up) * Quaternion.Euler(0, 0, flop) * Quaternion.Euler(0, 25f * Mathf.Sin(age * 5f), 0);
            tr.tf.SetPositionAndRotation(p, q);
            float s = tr.size * (pop < 1 ? Mathf.Sin(pop * Mathf.PI * 0.5f) * 1.15f : 1f);
            tr.tf.localScale = new Vector3(s, s, s);
            Color c = Color.white; c.a = fade; tr.mat.color = c;
        }
    }

    // ------------------------------------------------------------------ labels (IMGUI)
    static GUIStyle labelStyle;
    public static void Gui()
    {
        if (!enabled || trophies.Count == 0 || cam == null) return;
        try
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label); labelStyle.fontSize = 20; labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.MiddleCenter; labelStyle.richText = true; labelStyle.wordWrap = false;
            }
            float t = Time.time;
            Color old = GUI.color;
            foreach (Trophy tr in trophies)
            {
                if (tr.tf == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(tr.tf.position + Vector3.up * (tr.size * 0.35f + 0.45f));
                if (sp.z <= 0) continue;
                float age = t - tr.t0;
                float a = Mathf.Clamp01(age / 0.2f) * (1 - Mathf.Clamp01((age - 2.5f) / 0.7f));
                var r = new Rect(sp.x - 160, Screen.height - sp.y - 18 - 10f * Mathf.Clamp01(age / 3f), 320, 36);
                string name = Species[tr.sp], val = tr.label.Substring(name.Length);
                GUI.color = new Color(0, 0, 0, a * 0.85f);
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), name + val, labelStyle);
                GUI.color = new Color(1, 1, 1, a);
                GUI.Label(r, "<color=#ffffff>" + name + "</color><color=#ffd700>" + val + "</color>", labelStyle);
            }
            GUI.color = old;
        }
        catch (Exception e) { if (errors++ < 15) Log("gui error: " + e.Message); }
    }
}

// lets Run() tell its own assembly apart from earlier injections
class FishFxMarker { }

#if FISHFX_CLIENT
public class FishFxClientBehaviour : MonoBehaviour
#else
public class FishFx3Behaviour : MonoBehaviour
#endif
{
#if FISHFX_CLIENT
    void Update() { FishFxClient.Frame(); }
    void OnGUI() { FishFxClient.Gui(); }
#else
    void Update() { FishFx3.Frame(); }
    void OnGUI() { FishFx3.Gui(); }
#endif
}
