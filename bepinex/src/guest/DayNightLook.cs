// DayNightLook: day/night lighting for the fishing game. Shared by the host add-on (DayNight<N>, which owns the
// clock and broadcasts "[FT] TIME|<hour>|<dayMinutes>") and the guest client in FISHING.exe (driven by those lines).
//
//  * Call DayNightLook.Update(hour) every frame on the MAIN thread (RoR2Application.onUpdate), DayNightLook.Restore()
//    when switched off. Only UnityEngine.CoreModule is needed at compile time; RoR2 / post-processing types are
//    reached by reflection.
//  * Everything is RELATIVE to the stage's own look, captured ~2 s after each scene change:
//      - sun: the scene's main directional light (RenderSettings.sun, else the brightest one). Its direction follows a
//        sun path through the day (exactly the stage's own direction at 12:00) and a moon path at night; colour and
//        intensity are multiplied by per-hour curves. Other directional lights are scaled with the ambient curve.
//        If RoR2's SceneWeatherController drives that light, its weather params are scaled instead (it rewrites the
//        light every frame).
//      - RenderSettings: ambient colours (Flat/Trilight) or ambient probe (Skybox mode), reflection intensity,
//        fog colour; the skybox material is swapped for a tinted instance.
//      - post-processing (PPv2): every scene volume's profile is instanced (volume.profile) and RoR2's RampFog colours /
//        intensity are tinted; ColorGrading.colorFilter multiplies the whole frame (added to the top global volume
//        when the stage has no ColorGrading). Restore = volume.profile = null (back to the shared asset).
//      - stars (a few hundred additive quads on a far sphere around the camera) and a moon disc at night.
//  * Dark stages (low original ambient/fog brightness) get a weaker effect so nights stay readable
//    (night ~35-45 % of the stage's daytime brightness).
//  * Stage change: everything still alive is restored first (RenderSettings only if they still hold what we wrote),
//    then the new stage is captured.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class DayNightLook
{
    public static Action<string> Log;
    public static float Strength = 1f;          // 0 = stage's own look, 1 = full effect
    public static float NightLevel = 1f;        // >1 brighter nights, <1 darker
    public static bool Stars = true;
    public static string Phase = "";            // "night" / "morning" / "day" / "sunset" (for HUD / logs)

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static void L(string s) { try { if (Log != null) Log(s); } catch { } }

    // ------------------------------------------------------------------ per-hour curves
    struct K
    {
        public float h; public Color sun; public float sunMul; public Color amb; public float ambMul;
        public Color fog; public float fogMul; public Color filter; public float stars;
        public K(float h, Color sun, float sunMul, Color amb, float ambMul, Color fog, float fogMul, Color filter, float stars)
        { this.h = h; this.sun = sun; this.sunMul = sunMul; this.amb = amb; this.ambMul = ambMul; this.fog = fog; this.fogMul = fogMul; this.filter = filter; this.stars = stars; }
    }
    static Color C(float r, float g, float b) { return new Color(r, g, b, 1f); }
    static readonly K Night = new K(0f, C(0.55f, 0.68f, 1f), 0.42f, C(0.55f, 0.65f, 1f), 0.62f, C(0.26f, 0.33f, 0.58f), 1f, C(0.58f, 0.64f, 0.86f), 1f);
    static readonly K[] Keys = {
        Night,
        At(4.6f, Night),
        new K(5.5f,  C(1f, 0.55f, 0.55f), 0.35f, C(0.85f, 0.66f, 0.76f), 0.66f, C(0.95f, 0.62f, 0.68f), 1.15f, C(0.86f, 0.73f, 0.77f), 0.3f),   // dawn, pink
        new K(6.5f,  C(1f, 0.62f, 0.40f), 0.75f, C(1f, 0.80f, 0.74f),    0.80f, C(1f, 0.76f, 0.64f),    1.15f, C(1f, 0.88f, 0.82f),    0f),     // sunrise, orange + haze
        new K(8.5f,  C(1f, 0.88f, 0.75f), 0.95f, C(1f, 0.95f, 0.90f),    0.95f, C(1f, 0.93f, 0.88f),    1.05f, C(1f, 0.97f, 0.94f),    0f),
        new K(10f,   C(1f, 1f, 1f),       1f,    C(1f, 1f, 1f),          1f,    C(1f, 1f, 1f),          1f,    C(1f, 1f, 1f),          0f),     // bright day = stage as designed
        new K(15.5f, C(1f, 1f, 1f),       1f,    C(1f, 1f, 1f),          1f,    C(1f, 1f, 1f),          1f,    C(1f, 1f, 1f),          0f),
        new K(17f,   C(1f, 0.80f, 0.50f), 0.95f, C(1f, 0.88f, 0.72f),    0.92f, C(1f, 0.82f, 0.60f),    1f,    C(1.02f, 0.92f, 0.80f), 0f),     // golden hour
        new K(18.2f, C(1f, 0.48f, 0.28f), 0.70f, C(0.95f, 0.62f, 0.55f), 0.75f, C(1f, 0.55f, 0.40f),    1.05f, C(0.95f, 0.75f, 0.68f), 0f),     // sunset, red
        new K(19.2f, C(0.6f, 0.5f, 0.8f), 0.30f, C(0.60f, 0.58f, 0.85f), 0.66f, C(0.45f, 0.42f, 0.70f), 1f,    C(0.70f, 0.68f, 0.86f), 0.5f),   // dusk
        At(20.5f, Night),
        At(24f, Night),
    };
    static K At(float h, K k) { k.h = h; return k; }

    static K Sample(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        for (int i = 0; i < Keys.Length - 1; i++)
        {
            K a = Keys[i], b = Keys[i + 1];
            if (hour < a.h || hour > b.h) continue;
            float t = Mathf.SmoothStep(0f, 1f, (hour - a.h) / Mathf.Max(0.001f, b.h - a.h));
            return new K(hour, Color.Lerp(a.sun, b.sun, t), Mathf.Lerp(a.sunMul, b.sunMul, t), Color.Lerp(a.amb, b.amb, t), Mathf.Lerp(a.ambMul, b.ambMul, t),
                Color.Lerp(a.fog, b.fog, t), Mathf.Lerp(a.fogMul, b.fogMul, t), Color.Lerp(a.filter, b.filter, t), Mathf.Lerp(a.stars, b.stars, t));
        }
        return Night;
    }

    public static string PhaseOf(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        if (hour >= 19.5f || hour < 5f) return "night";
        if (hour < 9f) return "morning";
        if (hour < 17f) return "day";
        return "sunset";
    }

    // ------------------------------------------------------------------ captured stage state
    class LightState { public Light l; public Color col; public float inten; public Quaternion rot; }
    class ParamState { public object param; public FieldInfo valueF, overF; public object val; public bool over; }
    class VolState { public Component vol; public object profile; public List<ParamState> parms = new List<ParamState>(); public List<object> added = new List<object>(); }

    static int sceneHandle = int.MinValue;
    static float sceneSince;
    static bool captured;
    static LightState sun;
    static readonly List<LightState> fills = new List<LightState>();
    static float e0, a0;                          // original sun elevation / azimuth (deg)
    static Component weather; static FieldInfo wInit, wFinal; static object wInitOrig, wFinalOrig;
    // RenderSettings
    static AmbientMode ambMode; static Color ambSky, ambEq, ambGround; static float ambInt, reflInt; static SphericalHarmonicsL2 probe;
    static Color fogCol; static Material skyOrig, skyInst; static readonly List<KeyValuePair<string, Color>> skyCols = new List<KeyValuePair<string, Color>>();
    static readonly List<KeyValuePair<string, float>> skyExp = new List<KeyValuePair<string, float>>();
    static Color wAmbSky, wFog; static bool wrote;   // last values we wrote (to tell a scene reset from our own values)
    // post-processing
    static readonly List<VolState> vols = new List<VolState>();
    static readonly List<ParamState> rampCols = new List<ParamState>(), rampInt = new List<ParamState>(), filters = new List<ParamState>();
    static float strength = 1f;                   // per stage (weaker on dark stages)
    static float nextPP;
    static string stageName = "";

    static readonly string[] SkipScenes = { "title", "lobby", "loadingbasic", "splash", "intro", "outro", "crystalworld", "eclipseworld", "infinitetowerworld", "logbook" };

    // ------------------------------------------------------------------ public API
    public static void Update(float hour)
    {
        Scene sc = SceneManager.GetActiveScene();
        if (sc.handle != sceneHandle)
        {
            Restore();
            sceneHandle = sc.handle; sceneSince = Time.realtimeSinceStartup; stageName = sc.name;
            L("scene " + sc.name + ": waiting to capture");
        }
        if (!captured)
        {
            if (Time.realtimeSinceStartup - sceneSince < 2f || !RunActive() || Array.IndexOf(SkipScenes, stageName.ToLowerInvariant()) >= 0) return;
            Capture();
        }
        Apply(hour);
    }

    public static void Restore()
    {
        if (!captured) { DestroySky(); return; }
        captured = false;
        try
        {
            if (sun != null && sun.l != null) { sun.l.color = sun.col; sun.l.intensity = sun.inten; sun.l.transform.rotation = sun.rot; }
            foreach (var f in fills) if (f.l != null) { f.l.color = f.col; f.l.intensity = f.inten; }
            if (weather != null) { wInit.SetValue(weather, wInitOrig); wFinal.SetValue(weather, wFinalOrig); }
            // RenderSettings: only if they still hold our values (a new scene brings its own settings)
            if (wrote && RenderSettings.ambientSkyColor == wAmbSky && RenderSettings.fogColor == wFog)
            {
                RenderSettings.ambientSkyColor = ambSky; RenderSettings.ambientEquatorColor = ambEq; RenderSettings.ambientGroundColor = ambGround;
                RenderSettings.ambientIntensity = ambInt; RenderSettings.reflectionIntensity = reflInt; RenderSettings.fogColor = fogCol;
                if (ambMode == AmbientMode.Skybox) RenderSettings.ambientProbe = probe;
                if (skyInst != null && RenderSettings.skybox == skyInst) RenderSettings.skybox = skyOrig;
                L("RenderSettings restored");
            }
            foreach (var v in vols)
            {
                if (v.vol == null) continue;
                object inst = v.vol.GetType().GetField("m_InternalProfile", All).GetValue(v.vol);
                v.vol.GetType().GetProperty("profile").SetValue(v.vol, null, null);
                DestroyProfile(inst);
            }
            if (vols.Count > 0) DispatchPP();
        }
        catch (Exception e) { L("restore error: " + e.Message); }
        if (skyInst != null) UnityEngine.Object.Destroy(skyInst);
        skyInst = null; sun = null; fills.Clear(); weather = null; vols.Clear(); rampCols.Clear(); rampInt.Clear(); filters.Clear(); wrote = false;
        DestroySky();
        L("restored stage look");
    }

    // ------------------------------------------------------------------ capture
    static Type runType;
    static bool RunActive()
    {
        try
        {
            if (runType == null) foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "RoR2") runType = a.GetType("RoR2.Run");
            object inst = runType.GetProperty("instance", All).GetValue(null, null);
            return inst != null && (UnityEngine.Object)inst != null;
        }
        catch { return false; }
    }

    static float Lum(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

    static void Capture()
    {
        captured = true;
        var sb = new System.Text.StringBuilder("capture " + stageName + ":");
        // sun
        Light best = RenderSettings.sun;
        if (best != null && (!best.isActiveAndEnabled || best.type != LightType.Directional)) best = null;
        var dirs = new List<Light>();
        foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            if (l.type == LightType.Directional && l.isActiveAndEnabled) dirs.Add(l);
        if (best == null) foreach (Light l in dirs) if (best == null || l.intensity * Lum(l.color) > best.intensity * Lum(best.color)) best = l;
        foreach (Light l in dirs)
        {
            var s = new LightState { l = l, col = l.color, inten = l.intensity, rot = l.transform.rotation };
            if (l == best) sun = s; else fills.Add(s);
            sb.Append(" [dir '" + l.name + "' col=" + l.color + " int=" + l.intensity.ToString("0.##") + " rot=" + l.transform.eulerAngles + (l == best ? " SUN" : "") + "]");
        }
        if (sun != null)
        {
            Vector3 f = sun.rot * Vector3.forward;
            e0 = Mathf.Asin(Mathf.Clamp(-f.y, -1f, 1f)) * Mathf.Rad2Deg;
            a0 = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            sb.Append(" sunElev=" + e0.ToString("0") + " az=" + a0.ToString("0"));
        }
        else sb.Append(" NO directional light");
        // weather controller (rewrites the sun every frame)
        try
        {
            foreach (var a in FishingScope.Assemblies())
            {
                if (a.GetName().Name != "RoR2") continue;
                Type wt = a.GetType("RoR2.SceneWeatherController");
                var found = UnityEngine.Object.FindObjectOfType(wt) as Component;
                if (found != null && ((Behaviour)found).isActiveAndEnabled)
                {
                    weather = found; wInit = wt.GetField("initialWeatherParams", All); wFinal = wt.GetField("finalWeatherParams", All);
                    wInitOrig = wInit.GetValue(weather); wFinalOrig = wFinal.GetValue(weather);
                    Light wsun = wt.GetField("sun", All).GetValue(weather) as Light;
                    sb.Append(" weatherCtl(sun=" + (wsun == null ? "null" : wsun.name) + ")");
                    if (wsun != null && (sun == null || wsun != sun.l))
                    {   // the controller's sun is the one that matters
                        LightState ws = null; foreach (var x in fills) if (x.l == wsun) ws = x;
                        if (ws != null) { fills.Remove(ws); if (sun != null) fills.Add(sun); sun = ws; Vector3 f = sun.rot * Vector3.forward; e0 = Mathf.Asin(-f.y) * Mathf.Rad2Deg; a0 = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }
                    }
                }
            }
        }
        catch (Exception e) { sb.Append(" weather err " + e.Message); weather = null; }
        // RenderSettings
        ambMode = RenderSettings.ambientMode; ambSky = RenderSettings.ambientSkyColor; ambEq = RenderSettings.ambientEquatorColor; ambGround = RenderSettings.ambientGroundColor;
        ambInt = RenderSettings.ambientIntensity; reflInt = RenderSettings.reflectionIntensity; probe = RenderSettings.ambientProbe; fogCol = RenderSettings.fogColor;
        sb.Append(" ambient=" + ambMode + " sky=" + ambSky + " eq=" + ambEq + " int=" + ambInt.ToString("0.##") + " fog=" + RenderSettings.fog + " fogCol=" + fogCol);
        skyOrig = RenderSettings.skybox; skyCols.Clear(); skyExp.Clear();
        if (skyOrig != null)
        {
            try
            {
                skyInst = new Material(skyOrig) { name = skyOrig.name + " (DayNight)" };
                Shader sh = skyOrig.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i);
                    if (sh.GetPropertyType(i) == ShaderPropertyType.Color) skyCols.Add(new KeyValuePair<string, Color>(n, skyOrig.GetColor(n)));
                    else if (n == "_Exposure") skyExp.Add(new KeyValuePair<string, float>(n, skyOrig.GetFloat(n)));
                }
                RenderSettings.skybox = skyInst;
                sb.Append(" skybox=" + skyOrig.name + "/" + sh.name + " (" + skyCols.Count + " colours)");
            }
            catch (Exception e) { sb.Append(" skybox err " + e.Message); skyInst = null; }
        }
        // post-processing volumes
        try { CapturePP(sb); } catch (Exception e) { sb.Append(" PP err " + e); }
        // weaker effect on stages that are already dark
        float baseLum = ambMode == AmbientMode.Skybox ? ProbeLum(probe) * Mathf.Max(0.1f, ambInt) : Mathf.Max(Lum(ambSky), Lum(ambEq));
        float fogLum = 1f; foreach (var p in rampCols) fogLum = Mathf.Min(fogLum, Mathf.Max(0.0001f, Lum((Color)p.val)));
        if (rampCols.Count == 0) fogLum = Lum(fogCol);
        strength = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(Mathf.Max(baseLum, fogLum) / 0.3f));
        sb.Append(" ambLum=" + baseLum.ToString("0.00") + " fogLum=" + fogLum.ToString("0.00") + " strength=" + strength.ToString("0.00"));
        L(sb.ToString());
        if (Stars) BuildSky();
    }

    static float ProbeLum(SphericalHarmonicsL2 sh) { return Lum(new Color(sh[0, 0], sh[1, 0], sh[2, 0])) * 0.886f; }   // DC term ~ average irradiance

    static Type ppVolT, ppProfileT, colorGradingT, rampFogT; static MethodInfo dispatch;
    static void CapturePP(System.Text.StringBuilder sb)
    {
        if (ppVolT == null)
            foreach (var a in FishingScope.Assemblies())
            {
                string n = a.GetName().Name;
                if (n == "Unity.Postprocessing.Runtime")
                {
                    ppVolT = a.GetType("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                    ppProfileT = a.GetType("UnityEngine.Rendering.PostProcessing.PostProcessProfile");
                    colorGradingT = a.GetType("UnityEngine.Rendering.PostProcessing.ColorGrading");
                    dispatch = ppVolT.GetMethod("DispatchVolumeSettingsChangedEvent", All);
                }
                if (n == "RoR2") rampFogT = a.GetType("RampFog");
            }
        if (ppVolT == null) { sb.Append(" no PPv2"); return; }
        Component topGlobal = null; float topPrio = float.MinValue; bool anyGrading = false;
        var list = new List<Component>();
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(ppVolT))
        {
            var v = (Component)o;
            if (!((Behaviour)v).isActiveAndEnabled) continue;
            bool temp = false;
            foreach (var c in v.GetComponents<Component>()) if (c != null && c.GetType().Name == "PostProcessDuration") temp = true;
            if (temp) continue;
            object shared = ppVolT.GetField("sharedProfile").GetValue(v);
            if (shared == null || (UnityEngine.Object)shared == null) continue;
            list.Add(v);
        }
        foreach (var v in list)
        {
            var vs = new VolState { vol = v };
            object prof = ppVolT.GetProperty("profile").GetValue(v, null);   // instanced copy, shared asset untouched
            vs.profile = prof;
            bool global = (bool)ppVolT.GetField("isGlobal").GetValue(v);
            float prio = Convert.ToSingle(ppVolT.GetField("priority").GetValue(v));
            float weight = (float)ppVolT.GetField("weight").GetValue(v);
            sb.Append(" [PPV '" + v.name + "' global=" + global + " prio=" + prio + " w=" + weight.ToString("0.##") + ":");
            foreach (object s in (IList)ppProfileT.GetField("settings").GetValue(prof))
            {
                if (s == null) continue;
                Type st = s.GetType();
                bool on = (bool)st.GetField("active").GetValue(s) && (bool)ParamValue(st.GetField("enabled").GetValue(s));
                sb.Append(" " + st.Name + (on ? "" : "(off)"));
                if (!on) continue;
                if (st == rampFogT)
                {
                    foreach (string n in new[] { "fogColorStart", "fogColorMid", "fogColorEnd" }) { var p = Track(s, n); rampCols.Add(p); vs.parms.Add(p); }
                    var pi = Track(s, "fogIntensity"); rampInt.Add(pi); vs.parms.Add(pi);
                    sb.Append("{" + rampCols[rampCols.Count - 3].val + "," + rampCols[rampCols.Count - 1].val + " i=" + pi.val + " sky=" + ParamValue(st.GetField("skyboxStrength").GetValue(s)) + "}");
                }
                else if (st == colorGradingT)
                {
                    anyGrading = true;
                    var p = Track(s, "colorFilter"); filters.Add(p); vs.parms.Add(p);
                    sb.Append("{mode=" + ParamValue(st.GetField("gradingMode").GetValue(s)) + " filter=" + p.val + (p.over ? "*" : "") + "}");
                }
            }
            sb.Append("]");
            vols.Add(vs);
            if (global && weight > 0f && prio > topPrio) { topPrio = prio; topGlobal = v; }
        }
        if (!anyGrading && topGlobal != null && colorGradingT != null)
        {   // stage has no colour grading: add one (only the colour filter overridden) to the top global volume's instance
            VolState vs = vols.Find(x => x.vol == topGlobal);
            var cg = ScriptableObject.CreateInstance(colorGradingT);
            ((ScriptableObject)cg).name = "DayNight ColorGrading";
            object en = colorGradingT.GetField("enabled").GetValue(cg);
            en.GetType().GetMethod("Override").Invoke(en, new object[] { true });
            ((IList)ppProfileT.GetField("settings").GetValue(vs.profile)).Add(cg);
            var p = Track(cg, "colorFilter"); p.val = Color.white; filters.Add(p); vs.added.Add(cg);
            sb.Append(" +ColorGrading on '" + topGlobal.name + "'");
        }
        DispatchPP();
    }

    static object ParamValue(object p) { return p.GetType().GetField("value").GetValue(p); }
    static ParamState Track(object settings, string field)
    {
        object p = settings.GetType().GetField(field).GetValue(settings);
        var ps = new ParamState { param = p, valueF = p.GetType().GetField("value"), overF = p.GetType().GetField("overrideState") };
        ps.val = ps.valueF.GetValue(p); ps.over = (bool)ps.overF.GetValue(p);
        if (!ps.over && field == "colorFilter") ps.val = Color.white;
        return ps;
    }
    static void DestroyProfile(object prof)
    {
        if (prof == null || (UnityEngine.Object)prof == null) return;
        foreach (object s in (IList)ppProfileT.GetField("settings").GetValue(prof)) if (s != null) UnityEngine.Object.Destroy((UnityEngine.Object)s);
        UnityEngine.Object.Destroy((UnityEngine.Object)prof);
    }
    static void DispatchPP() { try { if (dispatch != null) dispatch.Invoke(null, null); } catch { } }

    // ------------------------------------------------------------------ apply
    static Color Mul(Color a, Color b) { return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a); }
    static Color Rel(Color tint, float s) { return Color.Lerp(Color.white, tint, s); }
    static float Rel(float m, float s) { return Mathf.Lerp(1f, m, s); }

    static void Apply(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        Phase = PhaseOf(hour);
        K k = Sample(hour);
        float s = Mathf.Clamp01(strength * Strength);
        float night = k.stars;
        float lift = Mathf.Lerp(1f, NightLevel, night);
        Color sunTint = Rel(k.sun, s), ambTint = Rel(k.amb, s), fogTint = Rel(k.fog, s), filt = Rel(k.filter, s);
        float sunMul = Rel(k.sunMul, s) * lift, ambMul = Rel(k.ambMul, s) * lift, fogMul = Rel(k.fogMul, s);
        filt = Color.Lerp(filt, Color.white, Mathf.Clamp01(lift - 1f));

        // sun / moon direction; dims to 0 where the sun hands over to the moon (5:00, 19:00)
        float handover = Mathf.Min(Mathf.Abs(hour - 5f), Mathf.Abs(hour - 19f));
        float dip = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(handover / 0.4f));
        if (sun != null && sun.l != null)
        {
            Quaternion rot;
            if (hour >= 5f && hour < 19f)
            {
                float p = (hour - 5f) / 14f;
                float maxE = Mathf.Clamp(e0, 35f, 80f);
                float elev = 7f + (maxE - 7f) * Mathf.Pow(Mathf.Sin(Mathf.PI * p), 0.8f);
                rot = Quaternion.Euler(elev, a0 + (p - 0.5f) * 150f, 0f);
                if (Mathf.Abs(hour - 12f) < 0.01f && e0 >= 35f && e0 <= 80f) rot = sun.rot;
            }
            else
            {
                float q = Mathf.Repeat(hour - 19f, 24f) / 10f;
                rot = Quaternion.Euler(22f + 33f * Mathf.Sin(Mathf.PI * q), a0 + 180f + (q - 0.5f) * 120f, 0f);
            }
            sun.l.transform.rotation = rot;
            float inten = sun.inten * sunMul * dip;
            if (weather != null) ScaleWeather(sunTint, sunMul * dip);
            else { sun.l.color = Mul(sun.col, sunTint); sun.l.intensity = inten; }
        }
        foreach (var f in fills) if (f.l != null) { f.l.color = Mul(f.col, ambTint); f.l.intensity = f.inten * ambMul; }

        // ambient + fog + skybox
        Color at = ambTint * ambMul; at.a = 1f;
        if (ambMode == AmbientMode.Skybox)
        {
            var sh = probe;
            for (int c = 0; c < 9; c++) { sh[0, c] *= at.r; sh[1, c] *= at.g; sh[2, c] *= at.b; }
            RenderSettings.ambientProbe = sh;
        }
        else
        {
            RenderSettings.ambientSkyColor = Mul(ambSky, at); RenderSettings.ambientEquatorColor = Mul(ambEq, at); RenderSettings.ambientGroundColor = Mul(ambGround, at);
        }
        RenderSettings.reflectionIntensity = reflInt * ambMul;
        RenderSettings.fogColor = Mul(fogCol, fogTint);
        wAmbSky = RenderSettings.ambientSkyColor; wFog = RenderSettings.fogColor; wrote = true;
        if (skyInst != null)
        {
            foreach (var kv in skyCols) skyInst.SetColor(kv.Key, Mul(kv.Value, fogTint * Mathf.Lerp(1f, 0.8f, night)));
            foreach (var kv in skyExp) skyInst.SetFloat(kv.Key, kv.Value * Mathf.Lerp(1f, 0.6f, night * s));
        }

        // post-processing (5 Hz is plenty for a 10-minute day)
        if (Time.realtimeSinceStartup >= nextPP)
        {
            nextPP = Time.realtimeSinceStartup + 0.2f;
            try
            {
                foreach (var p in rampCols) { p.valueF.SetValue(p.param, Mul((Color)p.val, fogTint)); }
                foreach (var p in rampInt) { p.valueF.SetValue(p.param, Mathf.Clamp01((float)p.val * fogMul)); }
                foreach (var p in filters) { p.overF.SetValue(p.param, true); p.valueF.SetValue(p.param, Mul((Color)p.val, filt)); }
                if (rampCols.Count + filters.Count > 0) DispatchPP();
            }
            catch (Exception e) { L("PP apply error: " + e.Message); rampCols.Clear(); rampInt.Clear(); filters.Clear(); }
        }

        AnimateSky(night * s, sun != null && sun.l != null ? sun.l.transform.forward : Vector3.down);
    }

    static void ScaleWeather(Color tint, float mul)
    {
        try
        {
            foreach (var pair in new[] { new KeyValuePair<FieldInfo, object>(wInit, wInitOrig), new KeyValuePair<FieldInfo, object>(wFinal, wFinalOrig) })
            {
                object orig = pair.Value;
                Type t = orig.GetType();
                object copy = orig;   // boxed copy below
                copy = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) f.SetValue(copy, f.GetValue(orig));
                t.GetField("sunColor").SetValue(copy, Mul((Color)t.GetField("sunColor").GetValue(orig), tint));
                t.GetField("sunIntensity").SetValue(copy, (float)t.GetField("sunIntensity").GetValue(orig) * mul);
                pair.Key.SetValue(weather, copy);
            }
        }
        catch (Exception e) { L("weather scale error: " + e.Message); weather = null; }
    }

    // ------------------------------------------------------------------ stars + moon
    static GameObject skyRoot, moon; static Material starMat, moonMat; static string tintProp = "_TintColor";
    static void BuildSky()
    {
        try
        {
            DestroySky();
            Shader sh = Shader.Find("Legacy Shaders/Particles/Additive");
            if (sh == null) { sh = Shader.Find("Sprites/Default"); tintProp = "_Color"; }
            if (sh == null) { L("stars: no shader"); return; }
            skyRoot = new GameObject("DayNightStars");
            var rnd = new System.Random(1234);
            const int N = 420;
            var v = new Vector3[N * 4]; var c = new Color[N * 4]; var uv = new Vector2[N * 4]; var tri = new int[N * 6];
            for (int i = 0; i < N; i++)
            {
                // upper hemisphere (+ a little below the horizon)
                Vector3 d;
                do { d = new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1); } while (d.sqrMagnitude > 1f || d.sqrMagnitude < 0.01f);
                d.Normalize(); d.y = Mathf.Abs(d.y) * 0.95f + 0.05f; d.Normalize();
                Vector3 r = Vector3.Cross(d, Mathf.Abs(d.y) > 0.9f ? Vector3.right : Vector3.up).normalized, u = Vector3.Cross(r, d);
                float size = 0.0016f + 0.0034f * (float)Math.Pow(rnd.NextDouble(), 3);   // fraction of the radius
                float b = 0.45f + 0.55f * (float)rnd.NextDouble();
                Color col = new Color(b * (0.85f + 0.15f * (float)rnd.NextDouble()), b * 0.92f, b, 1f);
                for (int k = 0; k < 4; k++)
                {
                    float sx = (k == 0 || k == 3) ? -1 : 1, sy = k < 2 ? -1 : 1;
                    v[i * 4 + k] = d + (r * sx + u * sy) * size; c[i * 4 + k] = col; uv[i * 4 + k] = new Vector2(sx * 0.5f + 0.5f, sy * 0.5f + 0.5f);
                }
                tri[i * 6] = i * 4; tri[i * 6 + 1] = i * 4 + 1; tri[i * 6 + 2] = i * 4 + 2; tri[i * 6 + 3] = i * 4; tri[i * 6 + 4] = i * 4 + 2; tri[i * 6 + 5] = i * 4 + 3;
            }
            var mesh = new Mesh { name = "DayNightStars", vertices = v, colors = c, uv = uv, triangles = tri };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            skyRoot.AddComponent<MeshFilter>().sharedMesh = mesh;
            starMat = new Material(sh) { name = "DayNightStars", mainTexture = Disc(32, 0.15f), renderQueue = 2990 };
            var mr = skyRoot.AddComponent<MeshRenderer>(); mr.sharedMaterial = starMat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;

            moon = new GameObject("DayNightMoon");
            var qm = new Mesh { name = "DayNightMoonQuad", vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) }, colors = new[] { Color.white, Color.white, Color.white, Color.white }, triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            qm.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            moon.AddComponent<MeshFilter>().sharedMesh = qm;
            moon.AddComponent<MeshRenderer>();
            moonMat = new Material(sh) { name = "DayNightMoon", mainTexture = Disc(128, 0.04f), renderQueue = 2991 };
            var mr2 = moon.GetComponent<MeshRenderer>(); mr2.sharedMaterial = moonMat; mr2.shadowCastingMode = ShadowCastingMode.Off; mr2.receiveShadows = false;
            moon.transform.SetParent(skyRoot.transform, false);
            skyRoot.SetActive(false);
            L("stars + moon built (" + sh.name + ")");
        }
        catch (Exception e) { L("stars error: " + e.Message); DestroySky(); }
    }

    static Texture2D Disc(int n, float soft)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1, r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - r) / Mathf.Max(0.01f, soft * 2f));
                if (soft > 0.1f) a = Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f);   // stars: soft glow
                else a = Mathf.Clamp01(a) * (0.9f + 0.1f * Mathf.Sin(dx * 9f) * Mathf.Cos(dy * 7f)) + Mathf.Pow(Mathf.Clamp01(1.25f - r), 3f) * 0.25f;   // moon: disc + faint halo
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
        t.SetPixels(px); t.Apply();
        return t;
    }

    static void AnimateSky(float night, Vector3 lightFwd)
    {
        if (skyRoot == null) return;
        bool show = Stars && night > 0.02f;
        if (skyRoot.activeSelf != show) skyRoot.SetActive(show);
        if (!show) return;
        Camera cam = Camera.main;
        if (cam == null) foreach (Camera c in Camera.allCameras) if (cam == null || c.depth > cam.depth) cam = c;
        if (cam == null) return;
        float R = Mathf.Min(cam.farClipPlane * 0.85f, 3000f);
        skyRoot.transform.position = cam.transform.position;
        skyRoot.transform.localScale = Vector3.one * R;
        skyRoot.transform.rotation = Quaternion.Euler(0f, Time.realtimeSinceStartup * 0.2f, 0f);   // slow drift
        float tw = 0.9f + 0.1f * Mathf.Sin(Time.realtimeSinceStartup * 3.1f);
        starMat.SetColor(tintProp, new Color(0.9f, 0.95f, 1f, 0.5f * night * tw) * (tintProp == "_TintColor" ? 1f : 1.6f));
        if (moon != null)
        {
            Vector3 dir = -lightFwd; if (dir.y < 0.05f) dir.y = 0.05f; dir.Normalize();
            moon.transform.position = cam.transform.position + dir * R * 0.97f;
            moon.transform.rotation = Quaternion.LookRotation(dir);   // quad faces -Z: look away so it faces the camera
            moon.transform.localScale = Vector3.one * (0.07f);        // parent scale R -> 7 % of the radius
            moonMat.SetColor(tintProp, new Color(0.85f, 0.9f, 1f, 0.55f * night));
        }
    }

    static void DestroySky()
    {
        if (skyRoot != null) UnityEngine.Object.Destroy(skyRoot);
        if (starMat != null) { UnityEngine.Object.Destroy(starMat.mainTexture); UnityEngine.Object.Destroy(starMat); }
        if (moonMat != null) { UnityEngine.Object.Destroy(moonMat.mainTexture); UnityEngine.Object.Destroy(moonMat); }
        skyRoot = null; moon = null; starMat = null; moonMat = null;
    }
}
