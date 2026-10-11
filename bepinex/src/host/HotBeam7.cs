// Injected into JV94's (host) game. Makes the fishing hotspot easy to find:
//  - hotspot radius 6 m -> 10 m (the bonus area on the newest Fishing<N>)
//  - hides the fishing mod's purple disc and draws a PURPLE TELEPORTER-SPARKLE look instead
//    (see HotspotLook.cs: purple-tinted lake-water disc with a glowing rim, clones of the teleporter's
//    idle sparkle particles recoloured purple, soft purple light), animated every frame
//  - keeps the hotspot on open water within casting range of land (Reachable/Relocate)
//  - a chat line whenever the hotspot appears or moves
// Built per class name: HotBeam7 -> HotBeamN, HotspotLook7 -> HotspotLookN (HotspotLook.cs is appended).
// Optional live tuning: <golden dir>\tune.txt (key=value), and <golden dir>\shot.req makes a PNG of the hotspot.
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class HotBeam7
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const float Radius = 10f, OldRadius = 6f;
    public static bool enabled = true;
    static Action handler;
    static EventInfo ev;
    static int animErrors, slowTicks;
    static float nextSlow;
    static GameObject beam, scaledDisc;
    static Vector3 lastPos = new Vector3(float.NaN, 0, 0), curPos;
    static Type fishing, chatType;
    static readonly string Dir = FishingPaths.Debug("golden");

    static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_golden.log"), DateTime.Now.ToString("HH:mm:ss ") + typeof(HotBeam7).Name + " " + s + Environment.NewLine); } catch { } }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    static Type Newest(string prefix)
    {
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t.Name.StartsWith(prefix) && int.TryParse(t.Name.Substring(prefix.Length), out n) && n > bestN) { best = t; bestN = n; }
            }
        return best;
    }

    static void Chat(string text)
    {
        object msg = Activator.CreateInstance(chatType.GetNestedType("SimpleChatMessage", All));
        msg.GetType().GetField("baseToken", All).SetValue(msg, text);
        foreach (MethodInfo m in chatType.GetMethods(All))
            if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
    }

    // ---- Reachability: the hotspot must sit on open water within casting range of land ----
    const float MaxFromLand = 150f;   // well inside the 200 m cast range
    static readonly System.Random rng = new System.Random();
    static int worldMask = -1;

    static int WorldMask()
    {
        if (worldMask != -1) return worldMask;
        try
        {
            Type li = chatType.Assembly.GetType("RoR2.LayerIndex");
            object world = li.GetField("world", All).GetValue(null);
            object mask = world.GetType().GetProperty("mask", All).GetValue(world, null);
            worldMask = (LayerMask)mask;
        }
        catch { worldMask = Physics.DefaultRaycastLayers; }
        return worldMask;
    }

    static float WaterY()
    {
        GameObject w = GameObject.Find("FishingWater");
        return w == null ? float.NaN : w.transform.position.y;
    }

    // Open water = no ground sticking out at or above the water surface at this spot.
    static bool OpenWater(Vector3 p, float waterY)
    {
        RaycastHit hit;
        if (Physics.Raycast(new Vector3(p.x, waterY + 300f, p.z), Vector3.down, out hit, 400f, WorldMask(), QueryTriggerInteraction.Ignore))
            return hit.point.y < waterY - 0.2f;
        return true;
    }

    // Feet of living players standing above the water (i.e. on land).
    static System.Collections.Generic.List<Vector3> LandPlayers(float waterY)
    {
        var list = new System.Collections.Generic.List<Vector3>();
        Assembly ror2 = chatType.Assembly;
        Type nu = ror2.GetType("RoR2.NetworkUser");
        PropertyInfo lp = nu.GetProperty("readOnlyInstancesList", All);
        object users = lp != null ? lp.GetValue(null, null) : nu.GetField("readOnlyInstancesList", All).GetValue(null);
        foreach (object u in (System.Collections.IEnumerable)users)
        {
            object master = u.GetType().GetProperty("master", All).GetValue(u, null);
            if (master == null) continue;
            object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
            if (body == null || (UnityEngine.Object)body == null) continue;
            Vector3 foot = (Vector3)body.GetType().GetProperty("footPosition", All).GetValue(body, null);
            if (foot.y > waterY + 0.5f) list.Add(foot);
        }
        return list;
    }

    static bool Reachable(Vector3 p)
    {
        float wy = WaterY();
        if (float.IsNaN(wy)) return true;
        if (!OpenWater(p, wy)) return false;
        foreach (Vector3 f in LandPlayers(wy))
            if (new Vector2(f.x - p.x, f.z - p.z).magnitude <= MaxFromLand) return true;
        return LandPlayers(wy).Count == 0;   // nobody on land right now: don't move it
    }

    static Vector3 Relocate(GameObject disc, Vector3 old)
    {
        float wy = WaterY();
        var land = LandPlayers(wy);
        if (land.Count == 0) return old;
        for (int i = 0; i < 60; i++)
        {
            Vector3 f = land[rng.Next(land.Count)];
            double ang = rng.NextDouble() * Math.PI * 2, dist = 25 + rng.NextDouble() * (MaxFromLand - 40);
            Vector3 c = new Vector3(f.x + (float)(Math.Cos(ang) * dist), wy, f.z + (float)(Math.Sin(ang) * dist));
            // the whole circle should be water: check the centre and 4 edge points
            if (!OpenWater(c, wy)) continue;
            bool ok = true;
            for (int k = 0; k < 4 && ok; k++)
                ok = OpenWater(c + new Vector3(Mathf.Cos(k * 1.5708f), 0, Mathf.Sin(k * 1.5708f)) * (Radius * 0.8f), wy);
            if (!ok) continue;
            disc.transform.position = new Vector3(c.x, disc.transform.position.y, c.z);
            if (fishing != null) fishing.GetField("hotspotPos", All).SetValue(null, new Vector3(c.x, wy, c.z));
            Log("hot beam: hotspot at " + old + " was unreachable (over land or > " + MaxFromLand + " m from land) -> moved to " + c + " (" + (int)dist + " m from a player)");
            return disc.transform.position;
        }
        Log("hot beam: couldn't find open water within " + MaxFromLand + " m of land - left hotspot at " + old);
        return old;
    }

    // ---- Purple hotspot visuals (HotspotLook) + live tuning + test screenshots ----
    static DateTime tuneStamp;

    static Color Col(string v) { var p = v.Split(','); return new Color(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), p.Length > 3 ? float.Parse(p[3]) : 1f); }

    // tune.txt: key=value per line; changing it rebuilds the visuals. Returns true if it changed.
    static bool ReadTune()
    {
        string f = Dir + "tune.txt";
        if (!File.Exists(f)) return false;
        DateTime st = File.GetLastWriteTimeUtc(f);
        if (st == tuneStamp) return false;
        tuneStamp = st;
        foreach (string line in File.ReadAllLines(f))
        {
            int i = line.IndexOf('='); if (i < 0) continue;
            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
            try
            {
                switch (k)
                {
                    case "ramp": HotspotLook7.RampName = v; break;
                    case "sparkColor": HotspotLook7.SparkColor = Col(v); break;
                    case "sparkTint": HotspotLook7.SparkTint = Col(v); break;
                    case "sparkRate": HotspotLook7.SparkRate = float.Parse(v); break;
                    case "sparkSize": HotspotLook7.SparkSize = float.Parse(v); break;
                    case "centerRate": HotspotLook7.CenterRate = float.Parse(v); break;
                    case "centerSize": HotspotLook7.CenterSize = float.Parse(v); break;
                    case "discColor": HotspotLook7.DiscColor = Col(v); break;
                    case "rimColor": HotspotLook7.RimColor = Col(v); break;
                    case "discTile": HotspotLook7.DiscTile = float.Parse(v); break;
                    case "light": HotspotLook7.LightIntensity = float.Parse(v); break;
                    case "lightColor": HotspotLook7.LightColor = Col(v); break;
                }
            }
            catch (Exception e) { Log("tune " + k + ": " + e.Message); }
        }
        Log("tune applied");
        return true;
    }

    static Camera SceneCam()
    {
        try
        {
            Type t = chatType.Assembly.GetType("RoR2.CameraRigController");
            PropertyInfo p = t.GetProperty("readOnlyInstancesList", All);
            object list = p != null ? p.GetValue(null, null) : t.GetField("readOnlyInstancesList", All).GetValue(null);
            foreach (object c in (System.Collections.IEnumerable)list)
            {
                var cam = t.GetField("sceneCam", All).GetValue(c) as Camera; if (cam != null) return cam;
            }
        }
        catch { }
        return Camera.main;
    }

    // Renders the hotspot from a temporary camera (does not touch the player's view) into a PNG.
    // shot.req content: "dist height" (optional, default "34 9").
    static void Capture(Vector3 target, string req)
    {
        float dist = 34f, h = 9f;
        try { var p = req.Trim().Split(' '); if (p.Length >= 2) { dist = float.Parse(p[0]); h = float.Parse(p[1]); } } catch { }
        Camera sc = SceneCam();
        var go = new GameObject("HotspotShotCam");
        try
        {
            var cam = go.AddComponent<Camera>();
            if (sc != null) cam.CopyFrom(sc);
            Vector3 from = sc != null ? sc.transform.position : target + new Vector3(40, 15, 0);
            Vector3 dir = target - from; dir.y = 0; if (dir.sqrMagnitude < 1) dir = Vector3.forward; dir.Normalize();
            go.transform.position = target - dir * dist + Vector3.up * h;
            go.transform.LookAt(target + Vector3.up * 3f);
            cam.fieldOfView = 55; cam.enabled = false;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            string path = Dir + "shot_" + DateTime.Now.ToString("HHmmss") + ".png";
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(rt);
            Log("shot " + path);
        }
        finally { UnityEngine.Object.Destroy(go); }
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetType("RoR2.Chat") != null) chatType = a.GetType("RoR2.Chat");
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("HotBeam") && t.Name != typeof(HotBeam7).Name)
                {
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null) f.SetValue(null, false);
                }
        }
        handler = Tick;
        Type app = chatType.Assembly.GetType("RoR2.RoR2Application");
        ev = app.GetEvent("onUpdate", All) ?? app.GetEvent("onFixedUpdate", All);
        ev.AddEventHandler(null, handler);
        Log("installed (" + ev.Name + ")");
    }

    static void Tick()
    {
        if (!enabled)
        {
            if (beam != null) { UnityEngine.Object.Destroy(beam); beam = null; }
            try { ev.RemoveEventHandler(null, handler); } catch { }
            return;
        }
        // every frame: smooth animation
        if (beam != null && beam.activeSelf)
        {
            try { HotspotLook7.Animate(beam, curPos, Radius); }
            catch (Exception e) { if (animErrors++ < 3) Log("anim error: " + e); }
        }
        if (Time.unscaledTime < nextSlow) return;
        nextSlow = Time.unscaledTime + 1f;   // once a second: game logic
        slowTicks++;
        try
        {
            if (fishing == null || slowTicks % 10 == 0) fishing = Newest("Fishing");
            if (fishing != null) fishing.GetField("hotspotRadius", All).SetValue(null, Radius);

            GameObject disc = GameObject.Find("FishingHotspot");
            if (disc == null) { if (beam != null) beam.SetActive(false); return; }

            if (disc != scaledDisc)   // new hotspot object (new stage): scale its visuals to the bigger radius
            {
                disc.transform.localScale = disc.transform.localScale * (Radius / OldRadius);
                scaledDisc = disc;
            }

            // hide the fishing mod's own disc; we draw our own
            foreach (Renderer r in disc.GetComponentsInChildren<Renderer>()) if (r.enabled) r.enabled = false;

            Vector3 pos = disc.transform.position;
            if (!Reachable(pos)) pos = Relocate(disc, pos);
            curPos = pos;

            if (ReadTune() && beam != null) { UnityEngine.Object.Destroy(beam); beam = null; }
            if (beam == null)
            {
                try { beam = HotspotLook7.Build(pos, Radius); Log("visuals built: " + HotspotLook7.Info); }
                catch (Exception e) { Log("build error: " + e); return; }
            }
            beam.SetActive(true);
            HotspotLook7.Animate(beam, pos, Radius);

            if (float.IsNaN(lastPos.x) || (pos - lastPos).sqrMagnitude > 4f)
            {
                Chat("<color=#c77dff>A purple HOTSPOT has appeared - look for the purple sparkles! Cast into it for rare fish.</color>");
                Log("hotspot at " + pos + ", radius " + Radius);
                lastPos = pos;
            }

            string req = Dir + "shot.req";
            if (File.Exists(req)) { string c = File.ReadAllText(req); File.Delete(req); Capture(pos, c); }
        }
        catch (Exception e) { if (slowTicks % 10 == 0) Log("error: " + e); }
    }
}


// Purple "teleporter sparkle" look for the fishing hotspot. Self-contained: needs only UnityEngine
// (CoreModule + ParticleSystemModule). Call everything on the Unity MAIN thread.
//   GameObject root = HotspotLook.Build(position, radius);   // once (or again after Destroy(root))
//   HotspotLook.Animate(root, position, radius);              // every frame (moves + pulses it)
//   UnityEngine.Object.Destroy(root);                         // to remove it
// The sparkles are clones of the stage teleporter's own idle particles ("PassiveParticle, Sphere" and
// "PassiveParticle, Center"), recoloured with a purple remap ramp. If no teleporter is in the scene it
// builds equivalent particle systems itself using the game's matTracerBright material.
// When injecting this into a game more than once, rename the class (Mono caches types by name).

public static class HotspotLook7
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // ---- tunables (the host add-on can overwrite these from a tune file) ----
    public static string RampName = "";                                  // "" = generated purple ramp, else a game ramp e.g. texRampGhostPurple
    public static Color SparkColor = new Color(1f, 0.6f, 1f, 1f);       // particle start colour
    public static Color SparkTint = new Color(1f, 1f, 1f, 1f);           // material _TintColor
    public static float SparkRate = 33f, SparkSize = 0.2f, CenterRate = 10f, CenterSize = 0.22f;   // rates = 30% of the earlier 110/35 (user asked for 70% fewer)
    public static Color DiscColor = new Color(0.62f, 0.3f, 1f, 0.9f);    // water texture tint
    public static Color RimColor = new Color(0f, 0f, 0f, 0f);             // additive glowing rim (alpha 0 = no rim; the user asked for none)
    public static float DiscTile = 3f;                                    // water texture repeats across the disc
    public static float LightIntensity = 4f, LightRange = 22f;
    public static Color LightColor = new Color(0.7f, 0.35f, 1f);
    public static string Info = "";                                      // what was used (for logging)

    static Texture2D ramp;

    static Type FindType(string n)
    {
        foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType(n); if (t != null) return t; }
        return null;
    }

    static ParticleSystem TeleporterPS(string name)
    {
        Type tt = FindType("RoR2.TeleporterInteraction");
        if (tt == null) return null;
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(tt))
            foreach (ParticleSystem ps in ((Component)o).GetComponentsInChildren<ParticleSystem>(true))
                if (ps.name == name) return ps;
        return null;
    }

    static T FindAsset<T>(string name) where T : UnityEngine.Object
    {
        foreach (T x in Resources.FindObjectsOfTypeAll<T>()) if (x.name == name) return x;
        return null;
    }

    // Remap ramp (left = faint, right = brightest): transparent deep violet -> purple -> magenta-lilac -> near-white.
    static Texture2D PurpleRamp()
    {
        if (ramp != null) return ramp;
        ramp = new Texture2D(256, 8, TextureFormat.RGBA32, false);
        ramp.wrapMode = TextureWrapMode.Clamp;
        for (int x = 0; x < 256; x++)
        {
            float v = x / 255f;
            Color c;
            if (v < 0.35f) c = Color.Lerp(new Color(0.15f, 0f, 0.35f, 0f), new Color(0.45f, 0.1f, 0.9f, 0.6f), v / 0.35f);
            else if (v < 0.75f) c = Color.Lerp(new Color(0.45f, 0.1f, 0.9f, 0.6f), new Color(0.85f, 0.4f, 1f, 1f), (v - 0.35f) / 0.4f);
            else c = Color.Lerp(new Color(0.85f, 0.4f, 1f, 1f), new Color(1f, 0.92f, 1f, 1f), (v - 0.75f) / 0.25f);
            for (int y = 0; y < 8; y++) ramp.SetPixel(x, y, c);
        }
        ramp.Apply();
        return ramp;
    }

    static Material Purple(Material src)
    {
        var m = new Material(src);
        Texture r = RampName.Length > 0 ? (Texture)FindAsset<Texture2D>(RampName) : null;
        if (r == null) r = PurpleRamp();
        if (m.HasProperty("_RemapTex")) m.SetTexture("_RemapTex", r);
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", SparkTint);
        return m;
    }

    static void Recolor(ParticleSystem ps, float radius, float rate, float size, bool sphere)
    {
        var main = ps.main;
        main.startColor = SparkColor;
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size * 1.4f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.Max(main.maxParticles, 600);
        var em = ps.emission; em.rateOverTime = rate;
        var sh = ps.shape;
        if (sphere) sh.radius = radius; else sh.radius = radius * 0.55f;
        var col = ps.colorOverLifetime;
        if (col.enabled)   // keep its alpha fade, drop its hue
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }
        var r = ps.GetComponent<ParticleSystemRenderer>();
        if (r != null && r.sharedMaterial != null) r.sharedMaterial = Purple(r.sharedMaterial);
        ps.Clear(); ps.Play();
    }

    // Fallback sparkles built from scratch (rising, twinkling specks) when no teleporter exists.
    static ParticleSystem MakeSparkles(Transform parent, bool sphere)
    {
        var go = new GameObject(sphere ? "HotspotSparkles" : "HotspotRising");
        go.transform.SetParent(parent, false);
        go.transform.localRotation = Quaternion.Euler(-90, 0, 0);   // shape +Z -> up
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = true; main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, sphere ? 0.8f : 3f);
        main.gravityModifier = sphere ? -0.03f : -0.08f;
        var sh = ps.shape; sh.shapeType = sphere ? ParticleSystemShapeType.Hemisphere : ParticleSystemShapeType.Cone; sh.angle = 5f;
        var col = ps.colorOverLifetime; col.enabled = true;
        var sz = ps.sizeOverLifetime; sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.3f), new Keyframe(0.3f, 1f), new Keyframe(1, 0f)));
        var r = go.GetComponent<ParticleSystemRenderer>();
        Material src = FindAsset<Material>("matTracerBright");
        r.sharedMaterial = src != null ? src : new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        return ps;
    }

    static Mesh DiscMesh(float radius, int seg)
    {
        // centre + 2 rings: solid to 0.7R, fading to 0 at R
        var v = new Vector3[1 + seg * 2]; var c = new Color[v.Length]; var uv = new Vector2[v.Length];
        float[] rr = { 0.7f, 1f }; float[] aa = { 1f, 0f };
        v[0] = Vector3.zero; c[0] = Color.white;
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2 / seg;
                v[1 + k * seg + i] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius * rr[k];
                c[1 + k * seg + i] = new Color(1, 1, 1, aa[k]);
            }
        var tri = new System.Collections.Generic.List<int>();
        for (int i = 0; i < seg; i++)
        {
            int n = (i + 1) % seg;
            tri.Add(0); tri.Add(1 + n); tri.Add(1 + i);
            tri.Add(1 + i); tri.Add(1 + n); tri.Add(1 + seg + n);
            tri.Add(1 + i); tri.Add(1 + seg + n); tri.Add(1 + seg + i);
        }
        var m = new Mesh(); m.vertices = v; m.colors = c; m.uv = uv; m.triangles = tri.ToArray(); m.RecalculateBounds();
        m.bounds = new Bounds(Vector3.zero, new Vector3(radius * 2, 1, radius * 2));
        return m;
    }

    static Mesh RimMesh(float radius, int seg)
    {
        // soft glowing band 0.8R..1.08R, brightest at 0.97R
        float[] rr = { 0.8f, 0.97f, 1.08f }; float[] aa = { 0f, 1f, 0f };
        var v = new Vector3[seg * 3]; var c = new Color[v.Length]; var uv = new Vector2[v.Length];
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2 / seg;
                v[k * seg + i] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius * rr[k];
                c[k * seg + i] = new Color(aa[k], aa[k], aa[k], aa[k]);
                uv[k * seg + i] = new Vector2(0.5f, 0.5f);
            }
        var tri = new System.Collections.Generic.List<int>();
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < seg; i++)
            {
                int n = (i + 1) % seg, a0 = k * seg, a1 = (k + 1) * seg;
                tri.Add(a0 + i); tri.Add(a1 + n); tri.Add(a0 + n);
                tri.Add(a0 + i); tri.Add(a1 + i); tri.Add(a1 + n);
            }
        var m = new Mesh(); m.vertices = v; m.colors = c; m.uv = uv; m.triangles = tri.ToArray(); m.RecalculateBounds();
        return m;
    }

    class State : MonoBehaviour
    {
        public Mesh disc; public Material discMat, rimMat; public Light light; public Vector2[] uv; public Vector3[] verts; public float radius;
    }

    public static GameObject Build(Vector3 pos, float radius)
    {
        var root = new GameObject("FishingHotspotPurple");
        root.transform.position = pos;
        var st = root.AddComponent<State>();
        st.radius = radius;
        Info = "";

        // Disc: the lake's own water texture, tinted purple, soft edge.
        var d = new GameObject("HotspotDisc");
        d.transform.SetParent(root.transform, false);
        d.transform.localPosition = new Vector3(0, 0.06f, 0);
        st.disc = DiscMesh(radius, 64);
        st.verts = st.disc.vertices; st.uv = new Vector2[st.verts.Length];
        d.AddComponent<MeshFilter>().sharedMesh = st.disc;
        st.discMat = new Material(Shader.Find("Sprites/Default"));
        GameObject water = GameObject.Find("FishingWater");
        Renderer wr = water == null ? null : water.GetComponent<Renderer>();
        if (wr != null && wr.sharedMaterial != null && wr.sharedMaterial.mainTexture != null) { st.discMat.mainTexture = wr.sharedMaterial.mainTexture; Info += "disc=" + wr.sharedMaterial.mainTexture.name + " "; }
        st.discMat.color = DiscColor;
        st.discMat.renderQueue = 3004;
        d.AddComponent<MeshRenderer>().sharedMaterial = st.discMat;

        // Glowing rim (additive) - off by default.
        if (RimColor.a > 0f) {
        var rim = new GameObject("HotspotRim");
        rim.transform.SetParent(root.transform, false);
        rim.transform.localPosition = new Vector3(0, 0.1f, 0);
        rim.AddComponent<MeshFilter>().sharedMesh = RimMesh(radius, 64);
        Shader add = Shader.Find("Legacy Shaders/Particles/Additive");
        st.rimMat = new Material(add != null ? add : Shader.Find("Sprites/Default"));
        st.rimMat.mainTexture = Texture2D.whiteTexture;
        if (st.rimMat.HasProperty("_TintColor")) st.rimMat.SetColor("_TintColor", RimColor); else st.rimMat.color = RimColor;
        st.rimMat.renderQueue = 3005;
        rim.AddComponent<MeshRenderer>().sharedMaterial = st.rimMat;
        }

        // Teleporter sparkles.
        string[] names = { "PassiveParticle, Sphere", "PassiveParticle, Center" };
        for (int k = 0; k < 2; k++)
        {
            bool sphere = k == 0;
            ParticleSystem src = TeleporterPS(names[k]), ps;
            if (src != null)
            {
                var go = (GameObject)UnityEngine.Object.Instantiate(src.gameObject, pos, src.transform.rotation);
                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                    if (!(c is Transform) && !(c is ParticleSystem) && !(c is ParticleSystemRenderer)) UnityEngine.Object.Destroy(c);
                go.name = sphere ? "HotspotSparkles" : "HotspotRising";
                go.transform.SetParent(root.transform, true);
                go.transform.localPosition = Vector3.zero;
                go.transform.localScale = src.transform.localScale;
                if (!go.activeSelf) go.SetActive(true);
                ps = go.GetComponent<ParticleSystem>();
                Info += names[k] + "=teleporter clone ";
            }
            else { ps = MakeSparkles(root.transform, sphere); Info += names[k] + "=built ";}
            Recolor(ps, radius, sphere ? SparkRate : CenterRate, sphere ? SparkSize : CenterSize, sphere);
        }

        // Soft purple light.
        var lg = new GameObject("HotspotLight");
        lg.transform.SetParent(root.transform, false);
        lg.transform.localPosition = new Vector3(0, 3f, 0);
        st.light = lg.AddComponent<Light>();
        st.light.type = LightType.Point; st.light.color = LightColor; st.light.range = LightRange; st.light.intensity = LightIntensity;
        return root;
    }

    public static void Animate(GameObject root, Vector3 pos, float radius)
    {
        if (root == null) return;
        var st = root.GetComponent<State>();
        root.transform.position = pos;
        if (st == null) return;
        float t = Time.time;
        // slowly drifting water texture + gentle pulse
        if (st.disc != null)
        {
            float s = DiscTile / (2f * st.radius);
            Vector2 off = new Vector2(t * 0.02f, t * 0.013f);
            for (int i = 0; i < st.verts.Length; i++) st.uv[i] = new Vector2(st.verts[i].x * s, st.verts[i].z * s) + off;
            st.disc.uv = st.uv;
        }
        if (st.discMat != null) { Color c = DiscColor; c.a *= 0.85f + 0.15f * Mathf.Sin(t * 1.7f); st.discMat.color = c; }
        if (st.rimMat != null)
        {
            Color c = RimColor * (0.75f + 0.25f * Mathf.Sin(t * 2.3f)); c.a = RimColor.a;
            if (st.rimMat.HasProperty("_TintColor")) st.rimMat.SetColor("_TintColor", c); else st.rimMat.color = c;
        }
        if (st.light != null) st.light.intensity = LightIntensity * (0.8f + 0.2f * Mathf.Sin(t * 2.3f));
    }
}
