// Purple "teleporter sparkle" look for the fishing hotspot (guest client copy). Self-contained: needs only UnityEngine
// (CoreModule + ParticleSystemModule). Call everything on the Unity MAIN thread.
//   GameObject root = HotspotLook.Build(position, radius);   // once (or again after Destroy(root))
//   HotspotLook.Animate(root, position, radius);              // every frame (moves + pulses it)
//   UnityEngine.Object.Destroy(root);                         // to remove it
// The sparkles are clones of the stage teleporter's own idle particles ("PassiveParticle, Sphere" and
// "PassiveParticle, Center"), recoloured with a purple remap ramp. If no teleporter is in the scene it
// builds equivalent particle systems itself using the game's matTracerBright material.
// When injecting this into a game more than once, rename the class (Mono caches types by name).
using System;
using System.Reflection;
using UnityEngine;

public static class FishingHotspotLook
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
    public static Texture DiscTexture;                                    // if set, used for the disc instead of the lake material's texture
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
        if (DiscTexture != null) { st.discMat.mainTexture = DiscTexture; Info += "disc=" + DiscTexture.name + " "; }
        else
        {
            GameObject water = GameObject.Find("FishingClientWater");
            if (water == null) water = GameObject.Find("FishingWater");
            Renderer wr = water == null ? null : water.GetComponent<Renderer>();
            if (wr != null && wr.sharedMaterial != null && wr.sharedMaterial.HasProperty("_MainTex") && wr.sharedMaterial.mainTexture != null) { st.discMat.mainTexture = wr.sharedMaterial.mainTexture; Info += "disc=" + wr.sharedMaterial.mainTexture.name + " "; }
        }
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
