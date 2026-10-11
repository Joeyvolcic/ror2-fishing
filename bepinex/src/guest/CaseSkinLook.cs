// CaseSkinLook - the case-exclusive rod and bobber skins (CaseData.Items with shop == false). UnityEngine only, shared by the
// host add-on and FISHING.exe. Works on the same rig parts the rod shop skins use: the pole is a unit cylinder (local y -1..1,
// y = -1 is the handle end), the bobber a unit sphere; decorations are children named "RodSkinDeco" so the shop's own re-skin
// (ClearDeco) removes them. Textures are procedural (seeded pattern + cosmetic wear scratches) and cached.
//   ApplyRod(pole, line, skin, wear, seed, mats)   ApplyBobber(bob, bobber, wear, seed, mats)   Swatch(item, wear, seed) for UI cards
// Animated looks (Howl flames, Doppler shimmer, Legendary sparkle, Disco spin, Kraken tentacles, the Eyeball watching you)
// run from a small CaseSkinAnim component on a deco child, so they stop when the skin is removed.
using System;
using System.Collections.Generic;
using UnityEngine;

public class CaseSkinAnim : MonoBehaviour
{
    public string mode;
    public Material mat;
    public Transform root;          // pole or bobber
    public Transform[] parts;
    public Vector3[] basePos;
    public LineRenderer line;
    public float seed;
    public TextMesh text;
    public string statKey;
    string shown;
    void Update()
    {
        try
        {
            float t = Time.time + seed;
            switch (mode)
            {
                case "stattrak":
                    if (text != null)
                    {
                        int[] v; CaseSkinLook.statCounts.TryGetValue(statKey ?? "", out v);
                        string s = (v != null ? v[0] : 0).ToString("00000");
                        if (s != shown) { shown = s; text.text = s; }
                        text.color = new Color(1f, 0.55f, 0.15f) * (0.85f + 0.15f * Mathf.Sin(t * 5f));
                    }
                    break;
                case "howl":
                    if (mat != null) { float k = 0.8f + 0.35f * Mathf.Sin(t * 7f) + 0.15f * Mathf.Sin(t * 17f); mat.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * k); mat.mainTextureOffset = new Vector2(0f, -t * 0.25f); }
                    break;
                case "dragon":
                    if (mat != null) mat.SetColor("_EmissionColor", new Color(0.35f, 0.1f, 0.02f) * (0.6f + 0.4f * Mathf.Sin(t * 2.2f)));
                    if (parts != null) foreach (var p in parts) if (p != null) p.localScale = Vector3.one * (0.05f + 0.015f * Mathf.Sin(t * 9f));
                    break;
                case "legendary":
                    if (mat != null) mat.SetColor("_EmissionColor", new Color(0.55f, 0.42f, 0.1f) * (0.8f + 0.5f * Mathf.Abs(Mathf.Sin(t * 2.5f))));
                    if (parts != null && parts.Length > 0 && parts[0] != null) parts[0].localRotation = Quaternion.Euler(45f, t * 120f, 45f);
                    if (line != null) { var c = Color.Lerp(new Color(1f, 0.85f, 0.3f), Color.white, 0.5f + 0.5f * Mathf.Sin(t * 4f)); line.startColor = c; line.endColor = c; }
                    break;
                case "doppler":
                    if (mat != null) mat.mainTextureOffset = new Vector2(t * 0.05f, t * 0.03f);
                    break;
                case "fade":
                    if (mat != null) mat.mainTextureOffset = new Vector2(0f, 0.04f * Mathf.Sin(t * 0.8f));
                    break;
                case "spin":
                    if (root != null) root.rotation = Quaternion.Euler(0f, t * 140f, 0f);
                    if (mat != null) mat.SetColor("_EmissionColor", Color.white * (0.15f + 0.35f * Mathf.Abs(Mathf.Sin(t * 6f))));
                    break;
                case "crown":
                    if (root != null) root.rotation = Quaternion.Euler(0f, t * 45f, 0f);
                    break;
                case "watch":   // eyeball / eight ball face the camera
                    {
                        var cam = Camera.main;
                        if (root != null && cam != null) { Vector3 d = cam.transform.position - root.position; if (d.sqrMagnitude > 0.01f) root.rotation = Quaternion.LookRotation(d.normalized); }
                    }
                    break;
                case "bubble":
                    if (parts != null && parts.Length > 0 && parts[0] != null) { float s = 0.75f + 0.25f * Mathf.Sin(t * 1.7f); parts[0].localScale = new Vector3(s, s, s); }
                    break;
                case "kraken":
                    if (root != null) root.rotation = Quaternion.Euler(0f, t * 30f, 0f);
                    if (parts != null)
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] == null) continue;
                            int seg = i % 5, arm = i / 5;
                            float w = Mathf.Sin(t * 3f + arm * 1.3f + seg * 0.8f) * 0.08f * (seg + 1);
                            Vector3 bp = basePos[i];
                            parts[i].localPosition = bp + new Vector3(Mathf.Cos(arm * 1.047f) * w, 0f, Mathf.Sin(arm * 1.047f) * w);
                        }
                    if (mat != null) mat.SetColor("_EmissionColor", new Color(0.5f, 0.38f, 0.05f) * (0.7f + 0.3f * Mathf.Sin(t * 3f)));
                    break;
            }
        }
        catch { }
    }
}

public static class CaseSkinLook
{
    static Shader lit, unlit;
    static bool shadersTried;
    static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
    public static Action<string> log;
    // StatTrak counters: key "player|item" -> {fish, rare, best value}; host fills it from catches, guests from [FT] STAT
    public static readonly Dictionary<string, int[]> statCounts = new Dictionary<string, int[]>();

    public static bool IsSkin(string n) { var i = CaseData.Find(n); return i != null && !i.shop && i.kind == "skin" && i.name == CaseData.Base(n); }
    public static bool IsBobber(string n) { var i = CaseData.Find(n); return i != null && !i.shop && i.kind == "bobber" && i.name == n; }

    static void Shaders()
    {
        if (shadersTried) return; shadersTried = true;
        lit = Shader.Find("Standard");
        if (lit != null && !lit.isSupported) lit = null;
        unlit = Shader.Find("Sprites/Default");
        if (unlit == null) unlit = Shader.Find("Unlit/Color");
    }

    static Material Mat(List<Material> mats, Color c, float metal, float gloss, Color? emit, Texture2D tex)
    {
        Material m;
        if (lit != null)
        {
            m = new Material(lit); m.color = c;
            metal = Mathf.Min(metal, 0.35f);   // full metal renders near-black here (no reflection probes at night / in rain)
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
            if (emit.HasValue) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emit.Value); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; }
        }
        else { m = new Material(unlit); m.color = c; }
        if (tex != null)
        {
            m.mainTexture = tex;
            if (lit != null && m.HasProperty("_EmissionMap"))
            {   // a faint self-glow of the pattern so the skins still read at night
                m.EnableKeyword("_EMISSION"); m.SetTexture("_EmissionMap", tex);
                m.SetColor("_EmissionColor", emit.HasValue ? emit.Value + new Color(0.18f, 0.18f, 0.18f) : new Color(0.22f, 0.22f, 0.22f));
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }
        else if (lit != null && !emit.HasValue && metal >= 0.8f)
        {   // metals get a little glow in their own colour
            m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 0.25f);
        }
        m.name = "CaseSkinMat";
        mats.Add(m);
        return m;
    }
    static Material Glass(List<Material> mats, Color c) { var m = new Material(unlit ?? lit); m.color = c; m.renderQueue = 3001; m.name = "CaseSkinGlass"; mats.Add(m); return m; }

    static GameObject Deco(GameObject parent, PrimitiveType kind, Vector3 pos, Vector3 scale, Vector3 euler, Material mat)
    {
        var go = GameObject.CreatePrimitive(kind);
        go.name = "RodSkinDeco";
        foreach (var comp in go.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(comp);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale; go.transform.localRotation = Quaternion.Euler(euler);
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }
    static CaseSkinAnim Anim(GameObject parent, string mode, Material mat, Transform root, float seed)
    {
        var go = new GameObject("RodSkinDeco");
        go.transform.SetParent(parent.transform, false);
        var a = go.AddComponent<CaseSkinAnim>();
        a.mode = mode; a.mat = mat; a.root = root; a.seed = seed;
        return a;
    }
    public static void ClearDeco(GameObject go)
    {
        if (go == null) return;
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            var c = go.transform.GetChild(i);
            if (c.name == "RodSkinDeco") { c.name = "RodSkinDeco_old"; c.SetParent(null); UnityEngine.Object.Destroy(c.gameObject); }
        }
    }

    // ------------------------------------------------------------------ procedural pattern maths
    static float Hash(int x, int y, int s) { unchecked { int h = x * 374761393 + y * 668265263 + s * 1274126177; h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16; return (h & 0xffffff) / 16777215f; } }
    static float Noise(float x, float y, int s)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y); float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        float a = Hash(xi, yi, s), b = Hash(xi + 1, yi, s), c = Hash(xi, yi + 1, s), d = Hash(xi + 1, yi + 1, s);
        return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
    }
    static float Fbm(float x, float y, int s) { return Noise(x, y, s) * 0.55f + Noise(x * 2.1f, y * 2.1f, s + 7) * 0.3f + Noise(x * 4.3f, y * 4.3f, s + 13) * 0.15f; }
    // wrap-around in u (the cylinder seam): sample noise on a circle
    static float FbmU(float u, float v, float freqU, float freqV, int s)
    {
        float a = u * Mathf.PI * 2f, r = freqU / (Mathf.PI * 2f);
        return 0.5f * (Fbm(Mathf.Cos(a) * r + 50f, v * freqV, s) + Fbm(Mathf.Sin(a) * r + 90f, v * freqV + 33f, s + 3));
    }

    // base colour of a rod skin at (u around, v along 0 = handle .. 1 = tip)
    public static Color RodColor(string skin, float u, float v, int seed)
    {
        switch (skin)
        {
            case "Safari Mesh":
                {
                    float n = FbmU(u, v, 6f, 14f, 11);
                    Color c = n < 0.42f ? new Color(0.34f, 0.40f, 0.22f) : n < 0.58f ? new Color(0.62f, 0.56f, 0.40f) : new Color(0.45f, 0.34f, 0.22f);
                    float gu = Mathf.Repeat(u * 10f, 1f), gv = Mathf.Repeat(v * 28f, 1f);
                    if (gu < 0.08f || gv < 0.06f) c *= 0.55f;
                    return c;
                }
            case "Sand Dune": { float n = FbmU(u, v, 4f, 10f, 5); return Color.Lerp(new Color(0.78f, 0.67f, 0.46f), new Color(0.9f, 0.8f, 0.58f), n); }
            case "Night Ops":
                {
                    float n = FbmU(u, v, 5f, 12f, 23);
                    return n < 0.45f ? new Color(0.10f, 0.13f, 0.22f) : n < 0.6f ? new Color(0.30f, 0.34f, 0.42f) : new Color(0.17f, 0.2f, 0.3f);
                }
            case "Case Hardened":
                {
                    if (seed == 661) { float b = FbmU(u, v, 5f, 9f, 661); return Color.Lerp(new Color(0.08f, 0.22f, 0.75f), new Color(0.25f, 0.5f, 0.95f), b); }   // the blue gem
                    float n = FbmU(u, v, 5f, 9f, seed + 100), m = FbmU(u, v, 9f, 20f, seed + 200);
                    Color blue = new Color(0.14f, 0.28f, 0.72f), gold = new Color(0.86f, 0.66f, 0.24f), purple = new Color(0.42f, 0.24f, 0.5f), steel = new Color(0.62f, 0.64f, 0.66f);
                    Color c = n < 0.4f ? blue : n < 0.55f ? purple : gold;
                    return Color.Lerp(c, steel, Mathf.Clamp01((m - 0.62f) * 4f));
                }
            case "Redline":
                {
                    int cx = Mathf.FloorToInt(u * 24f), cy = Mathf.FloorToInt(v * 90f);
                    Color c = ((cx + cy) & 1) == 0 ? new Color(0.06f, 0.06f, 0.07f) : new Color(0.12f, 0.12f, 0.13f);
                    float su = Mathf.Repeat(u * 3f, 1f);
                    if (su > 0.47f && su < 0.53f) c = new Color(0.85f, 0.05f, 0.05f);
                    return c;
                }
            case "Fade":
                {
                    float f = Mathf.Clamp01(v + (seed % 100) / 400f - 0.12f);
                    Color y = new Color(1f, 0.85f, 0.2f), p = new Color(1f, 0.3f, 0.6f), pu = new Color(0.45f, 0.2f, 0.95f);
                    return f < 0.5f ? Color.Lerp(y, p, f * 2f) : Color.Lerp(p, pu, (f - 0.5f) * 2f);
                }
            case "Hyper Koi":
                {
                    float n = FbmU(u, v, 4f, 12f, seed + 300);
                    Color c = n > 0.6f ? new Color(1f, 0.42f, 0.08f) : n > 0.55f ? new Color(0.85f, 0.1f, 0.05f) : new Color(0.96f, 0.95f, 0.92f);
                    float sc = Mathf.Repeat(u * 12f + (Mathf.FloorToInt(v * 40f) % 2) * 0.5f, 1f), sv = Mathf.Repeat(v * 40f, 1f);
                    if (Mathf.Abs(sc - 0.5f) * 2f + sv > 1.9f) c *= 0.8f;   // scale edges
                    return c;
                }
            case "Dragon Rod":
                {
                    float sc = Mathf.Repeat(u * 10f + (Mathf.FloorToInt(v * 36f) % 2) * 0.5f, 1f), sv = Mathf.Repeat(v * 36f, 1f);
                    float edge = Mathf.Abs(sc - 0.5f) * 2f + sv;
                    float n = FbmU(u, v, 4f, 10f, 77);
                    Color jade = Color.Lerp(new Color(0.08f, 0.42f, 0.26f), new Color(0.3f, 0.7f, 0.45f), n);
                    if (edge > 1.75f) return new Color(0.95f, 0.75f, 0.25f);   // gold scale rims
                    if (n > 0.68f) return new Color(0.75f, 0.12f, 0.06f);      // red dragon markings
                    return jade;
                }
            case "Howl":
                {
                    float n = FbmU(u, v, 5f, 16f, 41);
                    float h = Mathf.Clamp01(v * 0.9f + (n - 0.5f) * 0.7f);
                    return h < 0.4f ? Color.Lerp(new Color(0.35f, 0.02f, 0.02f), new Color(0.95f, 0.25f, 0.03f), h / 0.4f) : Color.Lerp(new Color(0.95f, 0.25f, 0.03f), new Color(1f, 0.85f, 0.25f), (h - 0.4f) / 0.6f);
                }
            case "★ Legendary Rod":
                {
                    float n = FbmU(u, v, 6f, 24f, 9);
                    return Color.Lerp(new Color(1f, 0.78f, 0.22f), new Color(1f, 0.95f, 0.65f), Mathf.Clamp01((n - 0.45f) * 3f));
                }
            case "★ Doppler":
                {
                    Color a, b;
                    switch (seed % 4)
                    {
                        case 0: a = new Color(0.85f, 0.04f, 0.1f); b = new Color(0.2f, 0.0f, 0.03f); break;      // Ruby
                        case 1: a = new Color(0.1f, 0.3f, 1f); b = new Color(0.0f, 0.03f, 0.2f); break;          // Sapphire
                        case 2: a = new Color(0.45f, 0.25f, 0.6f); b = new Color(0.04f, 0.02f, 0.08f); break;    // Black Pearl
                        default: a = new Color(1f, 0.35f, 0.75f); b = new Color(0.15f, 0.25f, 0.9f); break;      // Phase 2
                    }
                    float s = 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 2f * 3f + v * 26f + FbmU(u, v, 3f, 8f, seed) * 6f);
                    return Color.Lerp(b, a, s);
                }
            // shop skins (for UI swatches only)
            case "Bamboo": { float ring = Mathf.Repeat(v * 6f, 1f); return ring < 0.06f ? new Color(0.48f, 0.4f, 0.16f) : new Color(0.8f, 0.72f, 0.38f); }
            case "Carbon": { int cx = Mathf.FloorToInt(u * 24f), cy = Mathf.FloorToInt(v * 90f); return ((cx + cy) & 1) == 0 ? new Color(0.05f, 0.05f, 0.06f) : new Color(0.13f, 0.13f, 0.14f); }
            case "Neon": return Color.Lerp(new Color(0f, 1f, 1f), new Color(1f, 0.15f, 0.8f), v);
            case "Gold": return new Color(1f, 0.78f, 0.22f);
            case "Lunar Glass": return new Color(0.5f, 0.78f, 1f);
            case "Void": return new Color(0.3f, 0.08f, 0.55f);
            default: return new Color(0.45f, 0.27f, 0.1f);
        }
    }

    // cosmetic wear: chipped paint (grey metal shows through) + scratches, more of both with a higher float
    static Color Worn(Color c, float u, float v, float wear, int seed, int px, int py)
    {
        if (wear <= 0.02f) return c;
        float chip = FbmU(u, v, 9f, 30f, seed + 500);
        float th = 0.95f - wear * 0.55f;
        if (chip > th) c = Color.Lerp(c, new Color(0.42f, 0.42f, 0.44f), Mathf.Clamp01((chip - th) * 12f));
        if (Hash(px, py, seed + 900) < wear * 0.18f) c *= 0.7f;
        if (Hash(px / 3, py, seed + 901) < wear * 0.04f) c = Color.Lerp(c, new Color(0.75f, 0.75f, 0.78f), 0.6f);
        return c;
    }

    static Texture2D RodTex(string skin, float wear, int seed)
    {
        float wq = Mathf.Round(wear * 20f) / 20f;
        bool seeded = skin == "Case Hardened" || skin == "Fade" || skin == "Hyper Koi" || skin == "★ Doppler";
        string key = "rod|" + skin + "|" + wq + "|" + (seeded ? seed : 0);
        Texture2D t;
        if (texCache.TryGetValue(key, out t) && t != null) return t;
        const int W = 64, H = 256;
        t = new Texture2D(W, H, TextureFormat.RGBA32, true);
        t.wrapMode = TextureWrapMode.Repeat; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var px = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
                Color c = RodColor(skin, u, v, seed);
                px[y * W + x] = Worn(c, u, v, wq, seed, x, y);
            }
        t.SetPixels(px); t.Apply(true);
        texCache[key] = t;
        return t;
    }

    static Texture2D EightTex()
    {
        Texture2D t;
        if (texCache.TryGetValue("eight", out t) && t != null) return t;
        const int S = 64;
        t = new Texture2D(S, S, TextureFormat.RGBA32, false); t.hideFlags = HideFlags.DontUnloadUnusedAsset; t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float fx = (x + 0.5f) / S - 0.5f, fy = (y + 0.5f) / S - 0.5f;
                Color c = Color.white;
                float d1 = new Vector2(fx, fy - 0.11f).magnitude, d2 = new Vector2(fx, fy + 0.12f).magnitude;
                if (Mathf.Abs(d1 - 0.09f) < 0.03f || Mathf.Abs(d2 - 0.11f) < 0.03f) c = new Color(0.05f, 0.05f, 0.05f);
                px[y * S + x] = c;
            }
        t.SetPixels(px); t.Apply();
        texCache["eight"] = t;
        return t;
    }

    static Texture2D DiscoTex()
    {
        Texture2D t;
        if (texCache.TryGetValue("disco", out t) && t != null) return t;
        const int S = 64;
        t = new Texture2D(S, S, TextureFormat.RGBA32, true); t.hideFlags = HideFlags.DontUnloadUnusedAsset; t.filterMode = FilterMode.Point;
        var px = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                bool grout = x % 4 == 0 || y % 4 == 0;
                float g = 0.55f + 0.45f * Hash(x / 4, y / 4, 3);
                px[y * S + x] = grout ? new Color(0.15f, 0.15f, 0.17f) : new Color(g, g, g * 1.02f);
            }
        t.SetPixels(px); t.Apply(true);
        texCache["disco"] = t;
        return t;
    }

    // ------------------------------------------------------------------ rod
    // StatTrak display: an orange 5-digit counter in a dark box beside the handle (kept unskewed against the pole's scale)
    static void StatTrakCounter(GameObject pole, string statKey, List<Material> mats)
    {
        try
        {
            Vector3 ls = pole.transform.lossyScale;
            if (ls.x < 1e-4f || ls.y < 1e-4f || ls.z < 1e-4f) return;
            var holder = new GameObject("RodSkinDeco");
            holder.transform.SetParent(pole.transform, false);
            holder.transform.localPosition = new Vector3(1.2f, -0.62f, 0f);
            float size = 0.17f * Mathf.Max(0.5f, ls.x / 0.084f);   // world metres for the box width
            holder.transform.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "StatTrakBox";
            foreach (var comp in box.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(comp);
            box.transform.SetParent(holder.transform, false);
            box.transform.localScale = new Vector3(size, size * 0.42f, size * 0.25f);
            var bm = Mat(mats, new Color(0.05f, 0.05f, 0.06f), 0.3f, 0.6f, null, null);
            var br = box.GetComponent<Renderer>(); br.sharedMaterial = bm; br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach (float zSide in new[] { -1f, 1f })
            {
                var tgo = new GameObject("StatTrakText");
                tgo.transform.SetParent(holder.transform, false);
                tgo.transform.localPosition = new Vector3(0f, 0f, zSide * size * 0.13f);
                tgo.transform.localRotation = Quaternion.Euler(0f, zSide > 0 ? 180f : 0f, 0f);
                var tm = tgo.AddComponent<TextMesh>();
                Font f = null;
                try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
                if (f == null) try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
                if (f != null) { tm.font = f; tgo.GetComponent<MeshRenderer>().sharedMaterial = f.material; }
                tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
                tm.fontSize = 48; tm.characterSize = size * 0.06f; tm.fontStyle = FontStyle.Bold;
                tm.color = new Color(1f, 0.55f, 0.15f); tm.text = "00000";
                var a = tgo.AddComponent<CaseSkinAnim>();
                a.mode = "stattrak"; a.text = tm; a.statKey = statKey;
            }
        }
        catch (Exception e) { if (log != null) log("stattrak counter error: " + e.Message); }
    }

    public static Material ApplyRod(GameObject pole, LineRenderer line, string skin, float wear, int seed, List<Material> mats) { return ApplyRod(pole, line, skin, wear, seed, mats, null); }
    // skin may be "StatTrak™ <skin>": same look + the counter (statKey = "player|item")
    public static Material ApplyRod(GameObject pole, LineRenderer line, string skin, float wear, int seed, List<Material> mats, string statKey)
    {
        Shaders();
        ClearDeco(pole);
        bool stat = CaseData.IsStat(skin);
        skin = CaseData.Base(skin);
        if (stat) StatTrakCounter(pole, statKey, mats);
        var pr = pole.GetComponent<Renderer>();
        Vector3 Z = Vector3.zero;
        Texture2D tex = RodTex(skin, wear, seed);
        float gl = 1f - 0.55f * Mathf.Clamp01(wear);   // worn = duller
        Material pm;
        Color lineCol = Color.white;
        switch (skin)
        {
            case "Safari Mesh":
                pm = Mat(mats, Color.white, 0f, 0.25f * gl, null, tex);
                Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.4f, 0.15f, 1.4f), Z, Mat(mats, new Color(0.25f, 0.28f, 0.16f), 0f, 0.2f, null, null));
                break;
            case "Sand Dune":
                pm = Mat(mats, Color.white, 0f, 0.2f * gl, null, tex);
                Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.4f, 0.15f, 1.4f), Z, Mat(mats, new Color(0.5f, 0.4f, 0.26f), 0f, 0.2f, null, null));
                break;
            case "Night Ops":
                pm = Mat(mats, Color.white, 0.1f, 0.35f * gl, null, tex);
                Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.4f, 0.15f, 1.4f), Z, Mat(mats, new Color(0.08f, 0.08f, 0.1f), 0f, 0.2f, null, null));
                lineCol = new Color(0.6f, 0.65f, 0.8f);
                break;
            case "Case Hardened":
                pm = Mat(mats, Color.white, 0.85f, 0.78f * gl, null, tex);
                Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.35f, 0.14f, 1.35f), Z, Mat(mats, new Color(0.55f, 0.56f, 0.6f), 1f, 0.8f, null, null));
                break;
            case "Redline":
                {
                    pm = Mat(mats, Color.white, 0.4f, 0.88f * gl, null, tex);
                    var red = Mat(mats, new Color(0.9f, 0.05f, 0.05f), 0.3f, 0.8f, new Color(0.45f, 0.02f, 0.02f), null);
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.78f, 0), new Vector3(1.45f, 0.18f, 1.45f), Z, Mat(mats, new Color(0.12f, 0.12f, 0.13f), 0f, 0.2f, null, null));
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.56f, 0), new Vector3(1.28f, 0.025f, 1.28f), Z, red);
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, 0.9f, 0), new Vector3(1.2f, 0.02f, 1.2f), Z, red);
                    lineCol = new Color(1f, 0.4f, 0.4f);
                }
                break;
            case "Fade":
                pm = Mat(mats, Color.white, 0.6f, 0.88f * gl, null, tex);
                Anim(pole, "fade", pm, pole.transform, seed);
                lineCol = new Color(1f, 0.7f, 0.9f);
                break;
            case "Hyper Koi":
                {
                    pm = Mat(mats, Color.white, 0.1f, 0.65f * gl, null, tex);
                    var gold = Mat(mats, new Color(1f, 0.8f, 0.3f), 1f, 0.85f, new Color(0.3f, 0.2f, 0.03f), null);
                    foreach (float y in new[] { -0.82f, -0.6f, 0.95f }) Deco(pole, PrimitiveType.Cylinder, new Vector3(0, y, 0), new Vector3(1.3f, 0.03f, 1.3f), Z, gold);
                    lineCol = new Color(1f, 0.7f, 0.4f);
                }
                break;
            case "Dragon Rod":
                {
                    pm = Mat(mats, Color.white, 0.35f, 0.7f * gl, new Color(0.25f, 0.08f, 0.02f), tex);
                    var gold = Mat(mats, new Color(1f, 0.78f, 0.25f), 1f, 0.9f, new Color(0.35f, 0.24f, 0.04f), null);
                    var ember = Mat(mats, new Color(1f, 0.35f, 0.05f), 0f, 0.5f, new Color(2.5f, 0.7f, 0.05f), null);
                    foreach (float y in new[] { -0.6f, 0.1f, 0.8f }) Deco(pole, PrimitiveType.Cylinder, new Vector3(0, y, 0), new Vector3(1.35f, 0.035f, 1.35f), Z, gold);
                    Deco(pole, PrimitiveType.Capsule, new Vector3(0, -0.93f, 0), new Vector3(1.9f, 0.09f, 1.9f), Z, gold);                     // pommel
                    Deco(pole, PrimitiveType.Cube, new Vector3(0, -0.86f, 0.9f), new Vector3(0.5f, 0.08f, 1.4f), new Vector3(20f, 0, 0), gold);  // "wing" guards
                    Deco(pole, PrimitiveType.Cube, new Vector3(0, -0.86f, -0.9f), new Vector3(0.5f, 0.08f, 1.4f), new Vector3(-20f, 0, 0), gold);
                    var a = Anim(pole, "dragon", pm, pole.transform, seed);
                    var e1 = Deco(pole, PrimitiveType.Sphere, new Vector3(0.55f, -0.9f, 0), Vector3.one * 0.05f, Z, ember);
                    var e2 = Deco(pole, PrimitiveType.Sphere, new Vector3(-0.55f, -0.9f, 0), Vector3.one * 0.05f, Z, ember);
                    a.parts = new[] { e1.transform, e2.transform };
                    lineCol = new Color(0.6f, 1f, 0.7f);
                }
                break;
            case "Howl":
                {
                    pm = Mat(mats, Color.white, 0.1f, 0.6f * gl, new Color(1f, 0.35f, 0.05f), tex);
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.42f, 0.16f, 1.42f), Z, Mat(mats, new Color(0.15f, 0.04f, 0.02f), 0f, 0.2f, null, null));
                    Anim(pole, "howl", pm, pole.transform, seed);
                    lineCol = new Color(1f, 0.55f, 0.15f);
                }
                break;
            case "★ Legendary Rod":
                {
                    pm = Mat(mats, Color.white, 1f, 0.95f * gl, new Color(0.55f, 0.42f, 0.1f), tex);
                    var white = Mat(mats, new Color(1f, 0.97f, 0.88f), 1f, 1f, new Color(0.4f, 0.38f, 0.3f), null);
                    var diamond = Mat(mats, new Color(0.7f, 0.95f, 1f), 0.2f, 1f, new Color(0.5f, 1.4f, 1.8f), null);
                    foreach (float y in new[] { -0.7f, -0.5f, 0.3f, 0.92f }) Deco(pole, PrimitiveType.Cylinder, new Vector3(0, y, 0), new Vector3(1.32f, 0.025f, 1.32f), Z, white);
                    var gem = Deco(pole, PrimitiveType.Cube, new Vector3(0, -1.04f, 0), new Vector3(2.4f, 0.07f, 2.4f), new Vector3(45f, 0, 45f), diamond);
                    var a = Anim(pole, "legendary", pm, pole.transform, seed);
                    a.parts = new[] { gem.transform }; a.line = line;
                    lineCol = new Color(1f, 0.9f, 0.5f);
                }
                break;
            case "★ Doppler":
                {
                    pm = Mat(mats, Color.white, 0.9f, 0.95f * gl, null, tex);
                    var chrome = Mat(mats, new Color(0.85f, 0.85f, 0.9f), 1f, 0.95f, null, null);
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.8f, 0), new Vector3(1.4f, 0.14f, 1.4f), Z, Mat(mats, new Color(0.08f, 0.08f, 0.1f), 0.3f, 0.6f, null, null));
                    Deco(pole, PrimitiveType.Cylinder, new Vector3(0, -0.64f, 0), new Vector3(1.3f, 0.025f, 1.3f), Z, chrome);
                    Anim(pole, "doppler", pm, pole.transform, seed);
                    Color a = RodColor(skin, 0.1f, 0.5f, seed);
                    lineCol = Color.Lerp(a, Color.white, 0.5f);
                }
                break;
            default:
                pm = Mat(mats, Color.white, 0f, 0.3f, null, tex);
                break;
        }
        if (pr != null) pr.sharedMaterial = pm;
        if (line != null) { line.startColor = lineCol; line.endColor = lineCol; }
        return pm;
    }

    // ------------------------------------------------------------------ bobber (root = unit sphere scaled to ~0.25 m)
    public static Material ApplyBobber(GameObject bob, string bobber, float wear, int seed, List<Material> mats)
    {
        Shaders();
        ClearDeco(bob);
        var br = bob.GetComponent<Renderer>();
        Vector3 Z = Vector3.zero;
        float gl = 1f - 0.55f * Mathf.Clamp01(wear);
        Material bm;
        switch (bobber)
        {
            case "Cork":
                {
                    bm = Mat(mats, new Color(0.78f, 0.6f, 0.38f), 0f, 0.15f, null, null);
                    var band = Mat(mats, new Color(0.7f, 0.08f, 0.06f), 0f, 0.4f, null, null);
                    Deco(bob, PrimitiveType.Cylinder, new Vector3(0, 0.12f, 0), new Vector3(1.03f, 0.12f, 1.03f), Z, band);
                    Deco(bob, PrimitiveType.Cylinder, new Vector3(0, 0.7f, 0), new Vector3(0.08f, 0.3f, 0.08f), Z, Mat(mats, new Color(0.35f, 0.22f, 0.1f), 0f, 0.3f, null, null));
                }
                break;
            case "Bubblegum":
                {
                    bm = Mat(mats, new Color(1f, 0.5f, 0.75f), 0f, 0.7f * gl, null, null);
                    var bubble = Deco(bob, PrimitiveType.Sphere, new Vector3(0, 0.0f, 0.62f), Vector3.one * 0.8f, Z, Glass(mats, new Color(1f, 0.6f, 0.85f, 0.45f)));
                    var a = Anim(bob, "bubble", null, bob.transform, seed);
                    a.parts = new[] { bubble.transform };
                }
                break;
            case "Eyeball":
                {
                    bm = Mat(mats, new Color(0.97f, 0.95f, 0.93f), 0f, 0.85f * gl, null, null);
                    Deco(bob, PrimitiveType.Sphere, new Vector3(0, 0, 0.43f), new Vector3(0.5f, 0.5f, 0.16f), Z, Mat(mats, new Color(0.15f, 0.45f, 0.9f), 0f, 0.9f, null, null));
                    Deco(bob, PrimitiveType.Sphere, new Vector3(0, 0, 0.5f), new Vector3(0.24f, 0.24f, 0.08f), Z, Mat(mats, new Color(0.02f, 0.02f, 0.02f), 0f, 1f, null, null));
                    var vein = Mat(mats, new Color(0.85f, 0.15f, 0.15f), 0f, 0.5f, null, null);
                    for (int i = 0; i < 4; i++) Deco(bob, PrimitiveType.Cube, Quaternion.Euler(0, 0, i * 90f + 30f) * new Vector3(0.36f, 0, 0.33f), new Vector3(0.18f, 0.015f, 0.015f), new Vector3(0, 0, i * 90f + 30f), vein);
                    Anim(bob, "watch", null, bob.transform, seed);
                }
                break;
            case "Eight Ball":
                {
                    bm = Mat(mats, new Color(0.03f, 0.03f, 0.04f), 0.1f, 0.95f * gl, null, null);
                    Deco(bob, PrimitiveType.Sphere, new Vector3(0, 0, 0.44f), new Vector3(0.5f, 0.5f, 0.14f), Z, Mat(mats, Color.white, 0f, 0.8f, null, EightTex()));
                    Anim(bob, "watch", null, bob.transform, seed);
                }
                break;
            case "Disco Ball":
                {
                    bm = Mat(mats, new Color(0.9f, 0.9f, 0.95f), 1f, 1f * gl, new Color(0.3f, 0.3f, 0.3f), DiscoTex());
                    Deco(bob, PrimitiveType.Cylinder, new Vector3(0, 0.58f, 0), new Vector3(0.05f, 0.1f, 0.05f), Z, Mat(mats, new Color(0.3f, 0.3f, 0.32f), 1f, 0.6f, null, null));
                    Anim(bob, "spin", bm, bob.transform, seed);
                }
                break;
            case "Crown":
                {
                    bm = Mat(mats, new Color(0.65f, 0.08f, 0.12f), 0.2f, 0.7f * gl, null, null);   // velvet ball
                    var gold = Mat(mats, new Color(1f, 0.8f, 0.25f), 1f, 0.9f, new Color(0.35f, 0.25f, 0.04f), null);
                    var gem = Mat(mats, new Color(0.2f, 0.5f, 1f), 0.2f, 1f, new Color(0.1f, 0.3f, 0.9f), null);
                    Deco(bob, PrimitiveType.Cylinder, new Vector3(0, 0.36f, 0), new Vector3(0.95f, 0.1f, 0.95f), Z, gold);
                    for (int i = 0; i < 5; i++)
                    {
                        float ang = i * 72f;
                        Vector3 p = Quaternion.Euler(0, ang, 0) * new Vector3(0, 0.62f, 0.42f);
                        Deco(bob, PrimitiveType.Cube, p, new Vector3(0.14f, 0.42f, 0.06f), new Vector3(0, ang, 0), gold);
                        Deco(bob, PrimitiveType.Sphere, p + new Vector3(0, 0.25f, 0), Vector3.one * 0.13f, Z, gem);
                    }
                    Anim(bob, "crown", null, bob.transform, seed);
                }
                break;
            case "★ Golden Kraken":
                {
                    bm = Mat(mats, new Color(1f, 0.8f, 0.25f), 1f, 0.92f * gl, new Color(0.5f, 0.38f, 0.05f), null);
                    var eye = Mat(mats, new Color(0.05f, 0.9f, 0.6f), 0f, 1f, new Color(0.1f, 1.6f, 1f), null);
                    Deco(bob, PrimitiveType.Sphere, new Vector3(0.18f, 0.15f, 0.42f), Vector3.one * 0.14f, Z, eye);
                    Deco(bob, PrimitiveType.Sphere, new Vector3(-0.18f, 0.15f, 0.42f), Vector3.one * 0.14f, Z, eye);
                    var tent = new List<Transform>(); var bp = new List<Vector3>();
                    for (int arm = 0; arm < 6; arm++)
                        for (int seg = 0; seg < 5; seg++)
                        {
                            float ang = arm * 60f * Mathf.Deg2Rad, r = 0.3f + seg * 0.07f;
                            Vector3 p = new Vector3(Mathf.Cos(ang) * r, -0.3f - seg * 0.1f, Mathf.Sin(ang) * r);
                            var s = Deco(bob, PrimitiveType.Sphere, p, Vector3.one * (0.27f - seg * 0.035f), Z, bm);
                            tent.Add(s.transform); bp.Add(p);
                        }
                    var a = Anim(bob, "kraken", bm, bob.transform, seed);
                    a.parts = tent.ToArray(); a.basePos = bp.ToArray();
                }
                break;
            default:
                bm = Mat(mats, new Color(0.95f, 0.1f, 0.1f), 0f, 0.5f, null, null);
                break;
        }
        if (br != null) br.sharedMaterial = bm;
        return bm;
    }

    // ------------------------------------------------------------------ UI swatch for the case reel cards (160 x 72, transparent bg)
    public static Texture2D Swatch(string item, float wear, int seed)
    {
        item = CaseData.Base(item);
        string key = "sw|" + item + "|" + Mathf.Round(wear * 10f) + "|" + seed;
        Texture2D t;
        if (texCache.TryGetValue(key, out t) && t != null) return t;
        const int W = 160, H = 72;
        t = new Texture2D(W, H, TextureFormat.RGBA32, false); t.hideFlags = HideFlags.DontUnloadUnusedAsset; t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[W * H];
        var it = CaseData.Find(item);
        bool isBob = it != null && it.kind == "bobber";
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Color c = new Color(0, 0, 0, 0);
                if (!isBob)
                {
                    // rod drawn diagonally: handle bottom-left, tip top-right
                    float fx = x / (float)W, fy = y / (float)H;
                    float along = Mathf.Clamp01((fx * 0.92f + 0.04f));
                    float cy = 0.18f + along * 0.64f;
                    float half = Mathf.Lerp(0.11f, 0.035f, along);
                    float d = (fy - cy) / half;
                    if (Mathf.Abs(d) <= 1f && fx > 0.03f && fx < 0.97f)
                    {
                        float u = 0.25f + d * 0.25f;
                        c = RodColor(item, u, along, seed);
                        c = Worn(c, u, along, wear, seed, x, y);
                        float shade = 0.65f + 0.45f * (1f - Mathf.Abs(d + 0.35f));
                        c = new Color(c.r * shade, c.g * shade, c.b * shade, 1f);
                    }
                }
                else
                {
                    float fx = (x - W * 0.5f) / (H * 0.42f), fy = (y - H * 0.5f) / (H * 0.42f);
                    float r = Mathf.Sqrt(fx * fx + fy * fy);
                    if (r <= 1f)
                    {
                        c = BobColor(item, fx, fy, seed);
                        float shade = 0.7f + 0.4f * Mathf.Clamp01(1f - new Vector2(fx + 0.4f, fy - 0.4f).magnitude * 0.7f);
                        c = new Color(c.r * shade, c.g * shade, c.b * shade, 1f);
                    }
                    else if (item == "★ Golden Kraken" && fy < -0.5f && Mathf.Abs(Mathf.Sin(fx * 5f + fy * 3f)) < 0.35f && fy > -1.6f && Mathf.Abs(fx) < 1.3f) c = new Color(0.95f, 0.75f, 0.2f, 1f);
                    else if (item == "Crown" && fy > 0.6f && fy < 1.35f && Mathf.Abs(fx) < 0.75f && Mathf.Repeat(fx * 2.5f + 0.5f, 1f) < 0.45f) c = new Color(1f, 0.8f, 0.25f, 1f);
                }
                px[y * W + x] = c;
            }
        t.SetPixels(px); t.Apply();
        texCache[key] = t;
        return t;
    }

    static Color BobColor(string item, float fx, float fy, int seed)
    {
        switch (item)
        {
            case "Cork": return Mathf.Abs(fy - 0.15f) < 0.13f ? new Color(0.7f, 0.08f, 0.06f) : new Color(0.78f, 0.6f, 0.38f);
            case "Bubblegum": return new Color(1f, 0.5f, 0.75f);
            case "Eyeball": { float r = Mathf.Sqrt(fx * fx + fy * fy); return r < 0.18f ? Color.black : r < 0.42f ? new Color(0.15f, 0.45f, 0.9f) : new Color(0.97f, 0.95f, 0.93f); }
            case "Eight Ball":
                {
                    float r = Mathf.Sqrt(fx * fx + fy * fy);
                    if (r < 0.5f)
                    {
                        float d1 = new Vector2(fx, fy - 0.17f).magnitude, d2 = new Vector2(fx, fy + 0.15f).magnitude;
                        return (Mathf.Abs(d1 - 0.13f) < 0.05f || Mathf.Abs(d2 - 0.16f) < 0.05f) ? Color.black : Color.white;
                    }
                    return new Color(0.04f, 0.04f, 0.05f);
                }
            case "Disco Ball": { int gx = Mathf.FloorToInt((fx + 1f) * 7f), gy = Mathf.FloorToInt((fy + 1f) * 7f); float g = 0.5f + 0.5f * Hash(gx, gy, 3); return new Color(g, g, g); }
            case "Crown": return new Color(0.65f, 0.08f, 0.12f);
            case "★ Golden Kraken": return (new Vector2(Mathf.Abs(fx) - 0.35f, fy - 0.2f).magnitude < 0.14f) ? new Color(0.1f, 1f, 0.7f) : new Color(1f, 0.8f, 0.25f);
            case "Classic": return fy > 0f ? new Color(0.9f, 0.06f, 0.06f) : new Color(0.97f, 0.97f, 0.97f);
            case "Star": return new Color(1f, 0.85f, 0.15f);
            case "Duck": return new Color(1f, 0.85f, 0.1f);
            case "Skull": return new Color(0.93f, 0.9f, 0.8f);
            default: return new Color(0.95f, 0.1f, 0.1f);
        }
    }
}
