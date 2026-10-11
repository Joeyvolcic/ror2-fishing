// Weather performance + mood add-on for the host's fishing game (runs on top of Fishing21's GameplayFx21).
//  * Rain: caps the live rain particles (GameplayFx21 allowed 6000 stretched particles over a 70x70 m box) and
//    shrinks the box, so far fewer transparent streaks are drawn.
//  * Ripples: GameplayFx21 spawned a new quad GameObject + a new Material every 25-50 ms and never destroyed the
//    materials (leak -> growing stutter). Those are switched off here (its nextRipple timer is pushed into the
//    future) and replaced by one pooled particle system with a ring texture.
//  * Lightning: the per-bolt material is destroyed with its bolt (it leaked too); the flash light range is reduced.
//  * Darker rain: while Rain/Storm is on, the sun, fill lights and ambient light are dimmed (fades in/out over
//    ~4 s). DayNight1 rewrites the lighting from its captured base every frame, so the dimming is re-applied on top
//    each frame; if nothing rewrote a value since our last write we leave it alone (no compounding).
// Everything runs on the main thread from RoR2Application.onUpdate. Disable: WeatherTune1.enabled = false.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

public static class WeatherTune1
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static bool enabled = true;
    public static int rainMaxParticles = 1100;      // was 6000
    public static float rainBox = 48f;              // was 70 m
    public static float rainDim = 0.72f, stormDim = 0.58f;   // light multiplier while raining / storming
    public static float ripplesPerSec = 18f, stormRipplesPerSec = 30f;

    static bool installed;
    static Type fx; static FieldInfo fEvent, fRainPs, fNextRipple, fBolts;
    static float nextFind, dim = 1f;
    static ParticleSystem tunedRain;
    static ParticleSystem ripPs; static Material ripMat; static Texture2D ripTex;
    static readonly Dictionary<GameObject, Material> boltMats = new Dictionary<GameObject, Material>();
    static readonly List<GameObject> deadBolts = new List<GameObject>();
    static Light sunL; static Light[] fills = new Light[0]; static int lightScene = -1;
    static float wSun = -1f; static readonly Dictionary<Light, float> wFill = new Dictionary<Light, float>();
    static Color wSky, wEq, wGround; static bool wAmb;
    static int errs; static string lastEvent = "";

    static void Log(string s) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_weathertune.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
                if (t != null && t.Name.StartsWith("WeatherTune") && t.Name != typeof(WeatherTune1).Name)
                { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); }
        }
        if (installed) { Log("already installed"); return; }
        installed = true;
        RoR2Application.onUpdate += Tick;
        Log("installed (rain cap " + rainMaxParticles + ", box " + rainBox + " m, dim " + rainDim + "/" + stormDim + ")");
    }

    static void FindFx()
    {
        if (Time.realtimeSinceStartup < nextFind) return;
        nextFind = Time.realtimeSinceStartup + 5f;
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
        {
            string n = a.GetName().Name;
            if (!n.StartsWith("Fishing")) continue;
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
            {
                if (t == null || !t.Name.StartsWith("GameplayFx")) continue;
                int v; if (!int.TryParse(t.Name.Substring(10), out v)) v = 0;
                if (v > bestN && t.GetField("rainPs", All) != null) { best = t; bestN = v; }
            }
        }
        if (best != null && best != fx)
        {
            fx = best;
            fEvent = fx.GetField("eventName", All); fRainPs = fx.GetField("rainPs", All);
            fNextRipple = fx.GetField("nextRipple", All); fBolts = fx.GetField("bolts", All);
            tunedRain = null;
            Log("using " + fx.FullName + " (event=" + (fEvent != null) + " rain=" + (fRainPs != null) + " ripple=" + (fNextRipple != null) + " bolts=" + (fBolts != null) + ")");
            Resources.UnloadUnusedAssets();   // frees ripple/bolt materials leaked before this add-on
            Log("unloaded unused assets (leaked materials)");
        }
    }

    static void Tick()
    {
        if (!enabled) { RestoreDim(); if (ripPs != null) ripPs.gameObject.SetActive(false); return; }
        try
        {
            FindFx();
            if (fx == null) return;
            string ev = fEvent == null ? "Clear" : (fEvent.GetValue(null) as string ?? "Clear");
            bool storm = ev.Contains("Storm"), raining = storm || ev.Contains("Rain");
            if (ev != lastEvent) { Log("event " + lastEvent + " -> " + ev); lastEvent = ev; }

            TuneRain();
            Ripples(raining, storm);
            Bolts();
            Dim(raining ? (storm ? stormDim : rainDim) : 1f);
        }
        catch (Exception e) { if (errs++ < 20) Log("error: " + e); }
    }

    static void TuneRain()
    {
        var ps = fRainPs == null ? null : fRainPs.GetValue(null) as ParticleSystem;
        if (ps == null || ps == tunedRain) return;
        var main = ps.main; main.maxParticles = rainMaxParticles; main.startSize = 0.06f;
        var sh = ps.shape; sh.scale = new Vector3(rainBox, rainBox, 1f);
        tunedRain = ps;
        Log("rain tuned: max " + rainMaxParticles + " particles, box " + rainBox + " m");
    }

    // ---------------------------------------------------------------- pooled ripples
    static void Ripples(bool raining, bool storm)
    {
        if (fNextRipple != null) fNextRipple.SetValue(null, Time.time + 3600f);   // GameplayFx21's own ripples off
        Camera cam = Camera.main;
        GameObject water = raining ? GameObject.Find("FishingWater") : null;
        bool on = raining && cam != null && water != null && Mathf.Abs(cam.transform.position.y - water.transform.position.y) < 120f;
        if (!on) { if (ripPs != null && ripPs.gameObject.activeSelf) ripPs.gameObject.SetActive(false); return; }
        if (ripPs == null) BuildRipples();
        if (!ripPs.gameObject.activeSelf) { ripPs.gameObject.SetActive(true); ripPs.Play(); }
        var em = ripPs.emission; em.rateOverTime = storm ? stormRipplesPerSec : ripplesPerSec;
        Vector3 c = cam.transform.position + cam.transform.forward * 15f;
        ripPs.transform.position = new Vector3(c.x, water.transform.position.y + 0.07f, c.z);
    }

    static void BuildRipples()
    {
        ripTex = new Texture2D(64, 64, TextureFormat.RGBA32, false); ripTex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                float d = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 31.5f;
                px[y * 64 + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(1f - Mathf.Abs(d - 0.85f) / 0.12f)));
            }
        ripTex.SetPixels32(px); ripTex.Apply(false, true);
        Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        ripMat = new Material(sh); ripMat.mainTexture = ripTex; ripMat.renderQueue = 3010;

        var go = new GameObject("FishingRippleFx");
        UnityEngine.Object.DontDestroyOnLoad(go);
        ripPs = go.AddComponent<ParticleSystem>();
        ripPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ripPs.main; main.loop = true; main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.2f);
        main.startSpeed = 0f; main.startSize = 1.6f; main.maxParticles = 50;
        main.simulationSpace = ParticleSystemSimulationSpace.World; main.startColor = new Color(0.85f, 0.92f, 1f, 0.6f);
        var shp = ripPs.shape; shp.shapeType = ParticleSystemShapeType.Circle; shp.radius = 28f; shp.rotation = new Vector3(90f, 0f, 0f);
        var sol = ripPs.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.15f, 1f, 1f));
        var col = ripPs.colorOverLifetime; col.enabled = true;
        var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                                          new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.HorizontalBillboard; r.sharedMaterial = ripMat;
        r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        ripPs.Play();
        Log("pooled ripples built (one particle system, max 50)");
    }

    // ---------------------------------------------------------------- lightning cleanup
    static void Bolts()
    {
        var list = fBolts == null ? null : fBolts.GetValue(null) as IList;
        if (list != null)
            foreach (object b in list)
            {
                if (b == null) continue;
                Type bt = b.GetType();
                var go = bt.GetField("go").GetValue(b) as GameObject;
                if (go == null || boltMats.ContainsKey(go)) continue;
                var lr = bt.GetField("lr").GetValue(b) as LineRenderer;
                var lt = bt.GetField("light").GetValue(b) as Light;
                if (lt != null) { lt.range = 55f; lt.shadows = LightShadows.None; }
                boltMats[go] = lr != null ? lr.sharedMaterial : null;
            }
        if (boltMats.Count == 0) return;
        deadBolts.Clear();
        foreach (var kv in boltMats) if (kv.Key == null) deadBolts.Add(kv.Key);
        foreach (var k in deadBolts) { Material m; if (boltMats.TryGetValue(k, out m) && m != null) UnityEngine.Object.Destroy(m); boltMats.Remove(k); }
    }

    // ---------------------------------------------------------------- darker while raining
    static void FindLights()
    {
        int h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (h == lightScene && (sunL != null || Time.frameCount % 120 != 0)) return;
        lightScene = h; sunL = null; wSun = -1f; wFill.Clear(); wAmb = false;
        var list = new List<Light>();
        foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (l.type != LightType.Directional) continue;
            if (sunL == null || l == RenderSettings.sun || (RenderSettings.sun == null && l.intensity > sunL.intensity)) { if (sunL != null) list.Add(sunL); sunL = l; }
            else list.Add(l);
        }
        list.Remove(sunL); fills = list.ToArray();
    }

    static void Dim(float target)
    {
        dim = Mathf.MoveTowards(dim, target, Time.deltaTime / 4f);
        if (dim >= 0.999f && target >= 0.999f) { RestoreDim(); return; }
        FindLights();
        // Only scale values someone (DayNight1) rewrote since our last write -> never compounds.
        if (sunL != null && !Mathf.Approximately(sunL.intensity, wSun)) { sunL.intensity *= dim; wSun = sunL.intensity; }
        foreach (Light f in fills)
        {
            if (f == null) continue;
            float w; if (!wFill.TryGetValue(f, out w) || !Mathf.Approximately(f.intensity, w)) { f.intensity *= dim; wFill[f] = f.intensity; }
        }
        if (RenderSettings.ambientMode != AmbientMode.Skybox && (!wAmb || RenderSettings.ambientSkyColor != wSky || RenderSettings.ambientEquatorColor != wEq || RenderSettings.ambientGroundColor != wGround))
        {
            RenderSettings.ambientSkyColor = Scale(RenderSettings.ambientSkyColor, dim);
            RenderSettings.ambientEquatorColor = Scale(RenderSettings.ambientEquatorColor, dim);
            RenderSettings.ambientGroundColor = Scale(RenderSettings.ambientGroundColor, dim);
            wSky = RenderSettings.ambientSkyColor; wEq = RenderSettings.ambientEquatorColor; wGround = RenderSettings.ambientGroundColor; wAmb = true;
        }
    }
    static Color Scale(Color c, float k) { return new Color(c.r * k, c.g * k, c.b * k, c.a); }
    static void RestoreDim()
    {
        // DayNight1 rewrites the base lighting every frame, so simply stop scaling.
        wSun = -1f; wFill.Clear(); wAmb = false; dim = Mathf.Max(dim, 0.999f);
    }
}

