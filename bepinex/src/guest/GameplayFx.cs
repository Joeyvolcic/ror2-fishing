// Gameplay visuals shared by the host fishing mod and the guest client (FISHING.exe). Pure UnityEngine,
// main thread only. The host build renames the class (GameplayFx -> GameplayFx<N>, GoldLook -> GoldLook<N>).
//   rain + puddle ripples on the lake (events "Rain" / "Storm"), lightning bolts ([FT] BOLT),
//   small local pools ([FT] POOL), the golden far hotspot ([FT] HOT2), eat-buff visuals
//   (Pufferfish spiky aura, Anglerfish lantern + school x-ray for the eater, Ghost Fish glow).
using System;
using System.Collections.Generic;
using UnityEngine;

public static class GameplayFx
{
    public static bool enabled = true;
    public static Action<string> Log;
    public static Func<string, Transform> BodyTf;     // player name -> body transform (null = no body)
    public static string localPlayer;                 // x-ray is drawn only for this player's own Anglerfish buff
    public static string[] FishRoots = { "FishFxRoot_FishFxClient", "FishFxRoot_FishFx3", "FishFxRoot_FishFx4", "FishFxRoot_FishFx5" };
    static int errs;
    static void L(string s) { try { if (Log != null) Log(s); } catch { } }

    static Material Mat(Color c, int queue)
    {
        Shader sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");
        var m = new Material(sh); m.color = c; m.renderQueue = queue; return m;
    }
    static GameObject Prim(PrimitiveType t, string name, Material m)
    {
        var go = GameObject.CreatePrimitive(t); go.name = name;
        foreach (Component c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(c);   // no PhysicsModule reference needed
        var r = go.GetComponent<Renderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }

    // ================================================================ per frame
    static string scene;
    public static void Frame(bool hasWater, float waterY)
    {
        if (!enabled) { Clear(); return; }
        try
        {
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != scene) { scene = sc; ClearPools(); hot2On = false; L("scene " + sc + ": pools/hot2 reset"); }
            Camera cam = Camera.main;
            Rain(cam);
            Ripples(cam, hasWater, waterY);
            Bolts();
            PoolsFrame();
            Hot2Frame();
            Buffs();
            Xray();
        }
        catch (Exception e) { if (errs++ < 10) L("fx frame error: " + e); }
    }

    public static void Clear()
    {
        foreach (var go in new List<GameObject>(owned)) if (go != null) UnityEngine.Object.Destroy(go);
        owned.Clear(); ClearPools(); ripples.Clear(); bolts.Clear(); auras.Clear(); markers.Clear();
        rainGo = null; rainPs = null; if (ripMat != null) UnityEngine.Object.Destroy(ripMat); ripPs = null; ripMat = null; hot2Go = null; hot2On = false; xrayRoot = null;
    }
    static readonly List<GameObject> owned = new List<GameObject>();
    static GameObject Own(GameObject g) { owned.Add(g); return g; }

    // ================================================================ weather
    public static string eventName = "Clear";
    public static void SetEvent(string name) { if (name != eventName) L("event -> " + name); eventName = name ?? "Clear"; }
    static bool Raining { get { return eventName.Contains("Rain") || eventName.Contains("Storm"); } }
    static bool Storm { get { return eventName.Contains("Storm"); } }

    static GameObject rainGo; static ParticleSystem rainPs;
    static void Rain(Camera cam)
    {
        bool on = Raining && cam != null;
        if (!on) { if (rainGo != null && rainGo.activeSelf) rainGo.SetActive(false); return; }
        if (rainGo == null)
        {
            rainGo = Own(new GameObject("FishingRain"));
            rainGo.transform.rotation = Quaternion.Euler(90, 0, 0);   // +Z (emission) points down
            rainPs = rainGo.AddComponent<ParticleSystem>();
            rainPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = rainPs.main; main.loop = true; main.startLifetime = 1.3f; main.startSpeed = 28f; main.startSize = 0.05f;
            main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 1100;
            main.startColor = new Color(0.78f, 0.84f, 0.95f, 0.45f);
            var sh = rainPs.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(48, 48, 1);
            var r = rainGo.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.045f; r.lengthScale = 1f;
            r.sharedMaterial = Mat(Color.white, 3100);
            rainPs.Play();
            L("rain particles created");
        }
        if (!rainGo.activeSelf) { rainGo.SetActive(true); rainPs.Play(); }
        var em = rainPs.emission; em.rateOverTime = Storm ? 3200f : 1600f;
        Vector3 c = cam.transform.position + cam.transform.forward * 12f;
        rainGo.transform.position = new Vector3(c.x, cam.transform.position.y + 22f, c.z);
    }

    class Ripple { public GameObject go; public Material m; public float born, life; }
    static readonly List<Ripple> ripples = new List<Ripple>();
    static Texture2D ringTex;
    static float nextRipple;
    static Texture2D RingTex()
    {
        if (ringTex != null) return ringTex;
        ringTex = new Texture2D(64, 64, TextureFormat.RGBA32, false); ringTex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                float d = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 31.5f;
                float a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.85f) / 0.12f);
                ringTex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        ringTex.Apply(); return ringTex;
    }
    // ONE pooled particle system (one material) instead of a quad + material per ripple; still gated by nextRipple
    static ParticleSystem ripPs; static Material ripMat;
    static void Ripples(Camera cam, bool hasWater, float waterY)
    {
        bool on = Raining && hasWater && cam != null && Mathf.Abs(cam.transform.position.y - waterY) < 120f;
        float t = Time.time;
        if (!on || t < nextRipple) return;
        nextRipple = t + (Storm ? 0.033f : 0.055f);
        if (ripPs == null)
        {
            ripMat = Mat(new Color(1f, 1f, 1f, 1f), 3010); ripMat.mainTexture = RingTex();
            var go = Own(new GameObject("FishingRippleFx"));
            ripPs = go.AddComponent<ParticleSystem>();
            ripPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ripPs.main; main.loop = false; main.playOnAwake = false; main.startSpeed = 0f; main.maxParticles = 60;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ripPs.emission; em.enabled = false;
            var sol = ripPs.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.15f, 1f, 1f));
            var col = ripPs.colorOverLifetime; col.enabled = true;
            var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.HorizontalBillboard; r.sharedMaterial = ripMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            ripPs.Play();
        }
        Vector3 c = cam.transform.position + cam.transform.forward * 15f;
        Vector2 o = UnityEngine.Random.insideUnitCircle * 28f;
        var ep = new ParticleSystem.EmitParams();
        ep.position = new Vector3(c.x + o.x, waterY + 0.07f, c.z + o.y); ep.startSize = 1.75f; ep.startLifetime = 0.7f + UnityEngine.Random.value * 0.5f;
        ep.startColor = new Color(0.85f, 0.92f, 1f, 0.6f);
        ripPs.Emit(ep, 1);
    }

    // ================================================================ lightning
    class Bolt { public GameObject go; public LineRenderer lr; public Light light; public float born; }
    static readonly List<Bolt> bolts = new List<Bolt>();
    public static void LightningBolt(Vector3 p)
    {
        try
        {
            var b = new Bolt { born = Time.time };
            b.go = Own(new GameObject("FishingLightning"));
            b.lr = b.go.AddComponent<LineRenderer>();
            b.lr.useWorldSpace = true; b.lr.positionCount = 14; b.lr.startWidth = 0.9f; b.lr.endWidth = 0.35f;
            b.lr.sharedMaterial = Mat(new Color(0.85f, 0.9f, 1f, 1f), 3200);
            b.lr.startColor = new Color(0.8f, 0.85f, 1f, 1f); b.lr.endColor = Color.white;
            for (int i = 0; i < 14; i++)
            {
                float k = i / 13f;
                Vector2 j = i == 0 || i == 13 ? Vector2.zero : UnityEngine.Random.insideUnitCircle * 4f;
                b.lr.SetPosition(i, new Vector3(p.x + j.x, p.y + 110f * (1f - k), p.z + j.y));
            }
            var lg = new GameObject("FishingLightningLight"); lg.transform.SetParent(b.go.transform, false); lg.transform.position = p + Vector3.up * 8f;
            b.light = lg.AddComponent<Light>(); b.light.type = LightType.Point; b.light.range = 55f; b.light.shadows = LightShadows.None; b.light.intensity = 9f; b.light.color = new Color(0.8f, 0.85f, 1f);
            bolts.Add(b);
        }
        catch (Exception e) { if (errs++ < 10) L("bolt error: " + e.Message); }
    }
    static void Bolts()
    {
        float t = Time.time;
        for (int i = bolts.Count - 1; i >= 0; i--)
        {
            Bolt b = bolts[i]; float age = t - b.born;
            if (b.go == null || age > 0.5f) { if (b.lr != null && b.lr.sharedMaterial != null) UnityEngine.Object.Destroy(b.lr.sharedMaterial); if (b.go != null) { owned.Remove(b.go); UnityEngine.Object.Destroy(b.go); } bolts.RemoveAt(i); continue; }
            bool flick = age < 0.08f || (age > 0.14f && age < 0.22f);
            b.lr.enabled = flick || age < 0.3f;
            float a = flick ? 1f : Mathf.Clamp01(1f - age / 0.5f) * 0.5f;
            b.lr.startColor = new Color(0.8f, 0.85f, 1f, a); b.lr.endColor = new Color(1f, 1f, 1f, a);
            if (b.light != null) b.light.intensity = flick ? 9f : 2f * (1f - age / 0.5f);
        }
    }

    // ================================================================ small pools
    public class Pool { public int id; public Vector3 p; public float r; public GameObject go, rim; public MeshRenderer rend; public bool lakeMat; }
    public static readonly Dictionary<int, Pool> pools = new Dictionary<int, Pool>();
    static float nextMatCheck;

    static Mesh Disc(float r, int seg, float uvScale)
    {
        var v = new Vector3[seg + 1]; var uv = new Vector2[seg + 1]; var tri = new int[seg * 3];
        v[0] = Vector3.zero; uv[0] = Vector2.zero;
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2 / seg;
            v[i + 1] = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r); uv[i + 1] = new Vector2(v[i + 1].x * uvScale, v[i + 1].z * uvScale);
            tri[i * 3] = 0; tri[i * 3 + 1] = 1 + (i + 1) % seg; tri[i * 3 + 2] = 1 + i;
        }
        var m = new Mesh(); m.vertices = v; m.uv = uv; m.triangles = tri; m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }
    static Material LakeMat()
    {
        GameObject w = GameObject.Find("FishingWater");
        Renderer r = w == null ? null : w.GetComponent<Renderer>();
        return r == null ? null : r.sharedMaterial;
    }
    public static void SetPool(int id, Vector3 p, float r)
    {
        Pool pl;
        if (pools.TryGetValue(id, out pl) && pl.go != null && (pl.p - p).sqrMagnitude < 0.01f && Mathf.Abs(pl.r - r) < 0.01f) return;
        if (pl != null) KillPool(pl);
        pl = new Pool { id = id, p = p, r = r };
        pl.rim = Own(new GameObject("FishingPoolRim"));
        pl.rim.AddComponent<MeshFilter>().sharedMesh = Disc(r * 1.18f, 40, 0.25f);
        pl.rim.AddComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.22f, 0.16f, 0.09f, 0.9f), 2999);
        pl.rim.transform.position = p + Vector3.up * 0.03f;
        pl.go = Own(new GameObject("FishingPool"));   // NOT "FishingWater": other add-ons find the lake by that name
        pl.go.AddComponent<MeshFilter>().sharedMesh = Disc(r, 40, 0.25f);
        pl.rend = pl.go.AddComponent<MeshRenderer>();
        Material lm = LakeMat();
        pl.rend.sharedMaterial = lm != null ? lm : Mat(new Color(0.1f, 0.4f, 0.8f, 0.7f), 3001);
        pl.lakeMat = lm != null;
        pl.go.transform.position = p + Vector3.up * 0.07f;
        pools[id] = pl;
        L("pool #" + id + " at " + p + " r=" + r.ToString("0.0") + (pl.lakeMat ? " (lake material " + lm.name + ")" : " (plain blue until the lake exists)"));
    }
    static void KillPool(Pool pl)
    {
        foreach (var g in new[] { pl.go, pl.rim }) if (g != null) { owned.Remove(g); UnityEngine.Object.Destroy(g); }
    }
    public static void ClearPools() { foreach (var pl in pools.Values) KillPool(pl); pools.Clear(); }
    static void PoolsFrame()
    {
        if (pools.Count == 0 || Time.time < nextMatCheck) return;
        nextMatCheck = Time.time + 1f;
        Material lm = LakeMat();
        if (lm == null) return;
        foreach (var pl in pools.Values) if (pl.rend != null && pl.rend.sharedMaterial != lm) { pl.rend.sharedMaterial = lm; pl.lakeMat = true; }
    }

    // ================================================================ golden far hotspot
    static GameObject hot2Go; static bool hot2On; static Vector3 hot2Pos; static float hot2R;
    public static void SetHot2(bool on, Vector3 pos, float r)
    {
        if (on && hot2Go != null && Mathf.Abs(r - hot2R) > 0.01f) { owned.Remove(hot2Go); UnityEngine.Object.Destroy(hot2Go); hot2Go = null; }
        hot2On = on; hot2Pos = pos; hot2R = r;
    }
    static void Hot2Frame()
    {
        if (!hot2On) { if (hot2Go != null && hot2Go.activeSelf) hot2Go.SetActive(false); return; }
        if (hot2Go == null)
        {
            GoldLook.SparkColor = new Color(1f, 0.85f, 0.3f, 1f); GoldLook.DiscColor = new Color(1f, 0.78f, 0.15f, 0.85f);
            GoldLook.LightColor = new Color(1f, 0.8f, 0.3f); GoldLook.SparkRate = 45f;
            hot2Go = Own(GoldLook.Build(hot2Pos, hot2R)); L("golden hotspot visuals built: " + GoldLook.Info);
        }
        if (!hot2Go.activeSelf) hot2Go.SetActive(true);
        GoldLook.Animate(hot2Go, hot2Pos, hot2R);
    }

    // ================================================================ eat buffs
    class Aura { public string player, species; public float until; public GameObject go; public Light light; public List<Transform> parts = new List<Transform>(); }
    static readonly Dictionary<string, Aura> auras = new Dictionary<string, Aura>();   // key player|species
    static readonly Dictionary<string, float> xrayUntil = new Dictionary<string, float>();
    public static void SetBuff(string player, string species, float seconds)
    {
        float until = Time.time + seconds;
        if (species == "Anglerfish") xrayUntil[player] = until;
        if (species != "Pufferfish" && species != "Anglerfish" && species != "Ghost Fish") return;
        string k = player + "|" + species;
        Aura a; if (!auras.TryGetValue(k, out a)) { a = new Aura { player = player, species = species }; auras[k] = a; }
        a.until = until;
    }
    static void BuildAura(Aura a)
    {
        a.go = Own(new GameObject("FishingBuff_" + a.species)); a.parts.Clear();
        if (a.species == "Pufferfish")
        {
            var m = Mat(new Color(1f, 0.75f, 0.2f, 0.9f), 3050);
            for (int i = 0; i < 12; i++)
            {
                var s = Prim(PrimitiveType.Cube, "Spike", m); s.transform.SetParent(a.go.transform, false);
                s.transform.localScale = new Vector3(0.07f, 0.07f, 0.55f); a.parts.Add(s.transform);
            }
        }
        else if (a.species == "Anglerfish")
        {
            var orb = Prim(PrimitiveType.Sphere, "Lantern", Mat(new Color(0.75f, 1f, 0.5f, 0.95f), 3050));
            orb.transform.SetParent(a.go.transform, false); orb.transform.localScale = Vector3.one * 0.35f; a.parts.Add(orb.transform);
            var lg = new GameObject("LanternLight"); lg.transform.SetParent(orb.transform, false);
            a.light = lg.AddComponent<Light>(); a.light.type = LightType.Point; a.light.range = 22f; a.light.intensity = 3.5f; a.light.color = new Color(0.75f, 1f, 0.55f);
        }
        else
        {
            var lg = new GameObject("GhostLight"); lg.transform.SetParent(a.go.transform, false);
            a.light = lg.AddComponent<Light>(); a.light.type = LightType.Point; a.light.range = 9f; a.light.intensity = 2.5f; a.light.color = new Color(0.6f, 0.95f, 1f);
        }
    }
    static void Buffs()
    {
        float t = Time.time;
        foreach (var k in new List<string>(auras.Keys))
        {
            Aura a = auras[k];
            Transform tf = BodyTf == null ? null : BodyTf(a.player);
            if (t >= a.until || tf == null)
            {
                if (a.go != null) { owned.Remove(a.go); UnityEngine.Object.Destroy(a.go); a.go = null; }
                if (t >= a.until) auras.Remove(k);
                continue;
            }
            if (a.go == null) BuildAura(a);
            Vector3 c = tf.position + Vector3.up * 1.0f;
            a.go.transform.position = c;
            if (a.species == "Pufferfish")
                for (int i = 0; i < a.parts.Count; i++)
                {
                    float ang = i * Mathf.PI * 2 / a.parts.Count + t * 1.5f, yy = (i % 2 == 0 ? 0.25f : -0.25f);
                    Vector3 dir = new Vector3(Mathf.Cos(ang), yy, Mathf.Sin(ang)).normalized;
                    a.parts[i].localPosition = dir * (1.15f + 0.08f * Mathf.Sin(t * 6f + i));
                    a.parts[i].localRotation = Quaternion.LookRotation(dir);
                }
            else if (a.species == "Anglerfish" && a.parts.Count > 0)
            {
                a.parts[0].localPosition = new Vector3(0, 1.4f + 0.1f * Mathf.Sin(t * 2.5f), 0) + tf.forward * 0.5f;
                if (a.light != null) a.light.intensity = 3.5f + 0.6f * Mathf.Sin(t * 3f);
            }
            else if (a.light != null) a.light.intensity = 2f + 0.8f * Mathf.Sin(t * 4f);
        }
    }

    // x-ray: markers drawn through walls over every school fish, only for the local player while their Anglerfish buff lasts
    static GameObject xrayRoot; static Material xrayMat;
    static readonly List<GameObject> markers = new List<GameObject>();
    static readonly List<Transform> fishTfs = new List<Transform>();
    static float nextFishScan;
    static void Xray()
    {
        float until; bool on = localPlayer != null && xrayUntil.TryGetValue(localPlayer, out until) && Time.time < until;
        if (!on) { if (xrayRoot != null && xrayRoot.activeSelf) xrayRoot.SetActive(false); return; }
        if (xrayRoot == null)
        {
            xrayRoot = Own(new GameObject("FishingXray"));
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            xrayMat = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
            xrayMat.color = new Color(0.4f, 1f, 0.9f, 0.85f);
            if (xrayMat.HasProperty("_ZTest")) xrayMat.SetInt("_ZTest", 8);   // Always: visible through walls
            if (xrayMat.HasProperty("_ZWrite")) xrayMat.SetInt("_ZWrite", 0);
            xrayMat.renderQueue = 4000;
            markers.Clear();
        }
        if (!xrayRoot.activeSelf) xrayRoot.SetActive(true);
        if (Time.time >= nextFishScan)
        {
            nextFishScan = Time.time + 0.5f; fishTfs.Clear();
            foreach (string rn in FishRoots)
            {
                GameObject root = GameObject.Find(rn); if (root == null) continue;
                foreach (Transform ch in root.GetComponentsInChildren<Transform>(false))
                    if (ch.name.StartsWith("FishFx_") && ch.parent != null && !ch.parent.name.StartsWith("FishFx_")) fishTfs.Add(ch);
            }
        }
        while (markers.Count < fishTfs.Count && markers.Count < 60)
        {
            var g = Prim(PrimitiveType.Cube, "XrayMarker", xrayMat); g.transform.SetParent(xrayRoot.transform, false);
            g.transform.localScale = Vector3.one * 0.4f; markers.Add(g);
        }
        float t = Time.time;
        for (int i = 0; i < markers.Count; i++)
        {
            bool vis = i < fishTfs.Count && fishTfs[i] != null && fishTfs[i].gameObject.activeInHierarchy;
            if (markers[i].activeSelf != vis) markers[i].SetActive(vis);
            if (!vis) continue;
            markers[i].transform.position = fishTfs[i].position + Vector3.up * (0.8f + 0.1f * Mathf.Sin(t * 3f + i));
            markers[i].transform.rotation = Quaternion.Euler(45, t * 120f, 45);
        }
    }
}
