// GlowBobberLook - the three glowing bobber skins of the rod shop (UnityEngine only: host add-on, rod stand display and
// FISHING.exe share this file). They glow a little by day and much brighter at night (20:00-05:00 on the fishing clock).
//   Glow Bobber    - pale green luminous ball with a soft halo
//   Firefly Bobber - dark beetle body, glowing yellow tail, three little sparks circling it
//   Lantern Bobber - brass mini lantern with a warm orange flame core and a real point light
// Apply(bob, name) builds children named "GlowBobDeco" under the bobber root (unit sphere = 1 wide) and a GlowBobberFx
// component that animates them every frame from GlowBobberLook.Hour (set by the host / client to the day-night clock).
using System;
using UnityEngine;

public static class GlowBobberLook
{
    public static readonly string[] Names = { "Glow Bobber", "Firefly Bobber", "Lantern Bobber" };
    public static bool IsGlow(string n) { return n != null && Array.IndexOf(Names, n) >= 0; }
    public static Func<float> Hour;            // fishing clock (0-24); null = treated as midday
    public static float lightScale = 1f;       // tuning: point-light range multiplier

    // 0 by day, 1 at night (20:00-05:00), 1 h ramps at dusk (19-20) and dawn (5-6)
    public static float NightFactor(float h)
    {
        h = Mathf.Repeat(h, 24f);
        if (h >= 20f || h < 5f) return 1f;
        if (h >= 19f) return h - 19f;
        if (h < 6f) return 1f - (h - 5f);
        return 0f;
    }
    public static float Night() { float h = 12f; try { if (Hour == null) FindClock(); if (Hour != null) h = Hour(); } catch { } return NightFactor(h); }
    // no clock given (e.g. FISHING.exe's FishingClient copy): use a static float "hour" of ShopStandClient (guest) or DayNight<N> (host)
    static float clockLookAt = -100f;
    static void FindClock()
    {
        if (Time.unscaledTime - clockLookAt < 5f) return;
        clockLookAt = Time.unscaledTime;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (Type t in ts)
                if (t.Name == "ShopStandClient" || System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^DayNight\d+$"))
                {
                    var f = t.GetField("hour", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(float)) { Hour = () => (float)f.GetValue(null); return; }
                }
        }
    }

    // In this game build the Standard shader's emission renders black, so the glow is done with UNLIT colours
    // (Hidden/Internal-Colored, like the roulette table): the colour itself is the brightness, pulsed in GlowBobberFx.
    static Shader flat, unlit;
    static void Shaders()
    {
        if (flat == null) flat = Shader.Find("Hidden/Internal-Colored");
        if (unlit == null) unlit = Shader.Find("Sprites/Default") ?? flat;
    }
    public static Material Flat(Color c)
    {
        Shaders();
        var m = new Material(flat ?? unlit); m.color = c;
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1);
        if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 4);
        if (m.HasProperty("_Cull")) m.SetInt("_Cull", 2);
        m.renderQueue = 2000;
        return m;
    }
    // c = colour when fully lit (night); emit kept for the pulse colour
    static Material Emissive(Color c, Color emit) { return Flat(c); }
    static Material Halo(Color c) { Shaders(); var m = new Material(unlit ?? flat); m.color = c; m.renderQueue = 3002; return m; }

    static GameObject Deco(Transform parent, PrimitiveType kind, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(kind);
        go.name = "GlowBobDeco";
        foreach (var comp in go.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(comp);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }

    public static void Clear(GameObject bob)
    {
        if (bob == null) return;
        var fx = bob.GetComponent<GlowBobberFx>();
        if (fx != null) { fx.Release(); UnityEngine.Object.Destroy(fx); }
        // copies of this file in other assemblies (older add-on versions / the client) use their own GlowBobberFx type
        foreach (var mb in bob.GetComponents<MonoBehaviour>()) if (mb != null && mb != fx && mb.GetType().Name == "GlowBobberFx") UnityEngine.Object.Destroy(mb);
        for (int i = bob.transform.childCount - 1; i >= 0; i--)
        {
            var c = bob.transform.GetChild(i);
            if (c.name == "GlowBobDeco") { c.name = "GlowBobDeco_old"; c.SetParent(null); UnityEngine.Object.Destroy(c.gameObject); }
        }
    }

    // builds the look; returns the fx component (null if `name` is not a glow bobber)
    public static GlowBobberFx Apply(GameObject bob, string name)
    {
        Clear(bob);
        if (bob == null || !IsGlow(name)) return null;
        var fx = bob.AddComponent<GlowBobberFx>();
        fx.kind = Array.IndexOf(Names, name);
        Transform t = bob.transform;
        Vector3 one = Vector3.one;
        switch (fx.kind)
        {
            case 0:   // Glow Bobber
                fx.baseEmit = new Color(0.55f, 1f, 0.7f);
                fx.body = Emissive(fx.baseEmit, fx.baseEmit);
                fx.haloMat = Halo(new Color(0.4f, 1f, 0.6f, 0.15f));
                fx.halo = Deco(t, PrimitiveType.Sphere, Vector3.zero, one * 1.9f, fx.haloMat).transform;
                fx.lightCol = new Color(0.4f, 1f, 0.6f); fx.lightRange = 4f;
                break;
            case 1:   // Firefly Bobber
                fx.baseEmit = new Color(1f, 0.92f, 0.3f);
                fx.body = Emissive(new Color(0.16f, 0.13f, 0.08f), Color.black);
                fx.tailMat = Emissive(fx.baseEmit, fx.baseEmit);
                Deco(t, PrimitiveType.Sphere, new Vector3(0f, -0.08f, -0.42f), new Vector3(0.75f, 0.7f, 0.85f), fx.tailMat);
                var wing = Emissive(new Color(0.55f, 0.62f, 0.7f), Color.black);
                fx.extra.Add(wing);
                Deco(t, PrimitiveType.Sphere, new Vector3(0.28f, 0.38f, -0.1f), new Vector3(0.45f, 0.08f, 0.7f), wing);
                Deco(t, PrimitiveType.Sphere, new Vector3(-0.28f, 0.38f, -0.1f), new Vector3(0.45f, 0.08f, 0.7f), wing);
                fx.haloMat = Halo(new Color(1f, 0.9f, 0.3f, 0.12f));
                fx.halo = Deco(t, PrimitiveType.Sphere, new Vector3(0f, -0.08f, -0.42f), one * 1.5f, fx.haloMat).transform;
                fx.sparkMat = Emissive(new Color(1f, 1f, 0.6f), Color.black);
                for (int i = 0; i < 3; i++) fx.sparks.Add(Deco(t, PrimitiveType.Sphere, Vector3.zero, one * 0.12f, fx.sparkMat).transform);
                fx.lightCol = new Color(1f, 0.85f, 0.3f); fx.lightRange = 3.5f;
                break;
            default:  // Lantern Bobber
                fx.baseEmit = new Color(1f, 0.62f, 0.2f);
                var brass = Emissive(new Color(0.62f, 0.45f, 0.16f), Color.black);
                fx.extra.Add(brass);
                fx.body = Halo(new Color(1f, 0.75f, 0.4f, 0.35f));   // glass
                fx.tailMat = Emissive(fx.baseEmit, fx.baseEmit);
                Deco(t, PrimitiveType.Sphere, Vector3.zero, one * 0.55f, fx.tailMat);                                   // flame core
                Deco(t, PrimitiveType.Cylinder, new Vector3(0f, 0.5f, 0f), new Vector3(0.8f, 0.07f, 0.8f), brass);       // top cap
                Deco(t, PrimitiveType.Cylinder, new Vector3(0f, -0.5f, 0f), new Vector3(0.8f, 0.07f, 0.8f), brass);      // bottom cap
                Deco(t, PrimitiveType.Cylinder, new Vector3(0f, 0.68f, 0f), new Vector3(0.08f, 0.12f, 0.08f), brass);    // ring stem
                Deco(t, PrimitiveType.Sphere, new Vector3(0f, 0.82f, 0f), new Vector3(0.28f, 0.28f, 0.06f), brass);      // ring
                fx.haloMat = Halo(new Color(1f, 0.6f, 0.2f, 0.12f));
                fx.halo = Deco(t, PrimitiveType.Sphere, Vector3.zero, one * 2.2f, fx.haloMat).transform;
                fx.lightCol = new Color(1f, 0.6f, 0.25f); fx.lightRange = 7f;
                break;
        }
        var r = bob.GetComponent<Renderer>(); if (r != null) r.sharedMaterial = fx.body;
        var lg = new GameObject("GlowBobDeco"); lg.transform.SetParent(t, false);
        fx.light = lg.AddComponent<Light>(); fx.light.type = LightType.Point; fx.light.color = fx.lightCol; fx.light.shadows = LightShadows.None;
        fx.Tick();
        return fx;
    }
}

// per-bobber animator (pulse, halo, firefly sparks, light) - brighter at night
public class GlowBobberFx : MonoBehaviour
{
    public int kind;
    public Material body, tailMat, haloMat, sparkMat;
    public readonly System.Collections.Generic.List<Material> extra = new System.Collections.Generic.List<Material>();
    public readonly System.Collections.Generic.List<Transform> sparks = new System.Collections.Generic.List<Transform>();
    public Transform halo;
    public Light light;
    public Color baseEmit, lightCol;
    public float lightRange = 4f, phase;
    public float overrideNight = -1f;   // stand display / previews: fixed night factor

    void Awake() { phase = UnityEngine.Random.value * 10f; }
    void Update() { Tick(); }

    public void Tick()
    {
        try
        {
            float night = overrideNight >= 0f ? overrideNight : GlowBobberLook.Night();
            float t = Time.time + phase;
            float pulse = kind == 1 ? Mathf.Pow(0.5f + 0.5f * Mathf.Sin(t * 2.4f), 3f) : 0.75f + 0.25f * Mathf.Sin(t * (kind == 2 ? 9f : 2f)) * (kind == 2 ? 0.6f : 1f);
            // unlit colour: dim by day (0.45-0.7 of full), full + pulsing at night
            float glow = Mathf.Clamp01(Mathf.Lerp(0.45f, 0.8f, night) + Mathf.Lerp(0.15f, 0.2f, night) * pulse);
            Material em = kind == 0 ? body : tailMat;
            if (em != null) { Color c = baseEmit * glow; c.a = 1f; em.color = c; }
            if (haloMat != null) { Color c = haloMat.color; c.a = Mathf.Lerp(0.03f, 0.22f, night) * (0.6f + 0.4f * pulse); haloMat.color = c; }
            if (halo != null) halo.localScale = Vector3.one * ((kind == 2 ? 2.2f : kind == 1 ? 1.5f : 1.9f) * (1f + 0.25f * night + 0.08f * pulse));
            if (light != null)
            {
                light.range = lightRange * GlowBobberLook.lightScale * Mathf.Lerp(0.5f, 1f, night);
                light.intensity = Mathf.Lerp(0.25f, 2.6f, night) * (0.6f + 0.4f * pulse);
            }
            for (int i = 0; i < sparks.Count; i++)
            {
                float a = t * (1.6f + i * 0.4f) + i * 2.1f;
                sparks[i].localPosition = new Vector3(Mathf.Cos(a) * 0.9f, 0.3f + 0.3f * Mathf.Sin(a * 1.7f), Mathf.Sin(a) * 0.9f);
                sparks[i].localScale = Vector3.one * (0.08f + 0.08f * night) * (0.6f + 0.4f * Mathf.Abs(Mathf.Sin(a * 3f)));
            }
        }
        catch { }
    }

    public void Release()
    {
        foreach (var m in new[] { body, tailMat, haloMat, sparkMat }) if (m != null) Destroy(m);
        foreach (var m in extra) if (m != null) Destroy(m);
        extra.Clear();
    }
    void OnDestroy() { Release(); }
}
