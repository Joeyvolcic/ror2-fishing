// Animated water look for the fishing water sheet (shared by the guest client and the host add-on).
// Two layers:
//   1. the water sheet itself gets a tileable 256x256 ripple texture (two shades of blue, alpha ~0.6)
//   2. a child copy of the sheet 3 cm above it carries a mostly-transparent caustic-highlight texture
// Each layer's mesh UVs are tiled (~8 m / ~11 m per tile) and scrolled every frame in different
// directions, so it looks like moving water. UVs are used instead of _MainTex_ST because
// Sprites/Default ignores texture tiling/offset. All Unity calls must run on the main thread.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

public sealed class FishingWaterLook
{
    const int N = 256;
    const float TileBase = 8f, TileOverlay = 11f;   // metres per texture repeat
    static UnityEngine.Texture2D texBase, texOverlay;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    public static float Now { get { return (float)clock.Elapsed.TotalSeconds; } }

    public UnityEngine.GameObject water, overlay;
    UnityEngine.Mesh baseMesh, ovMesh;
    UnityEngine.Material baseMat, ovMat;
    UnityEngine.Texture oldTex;
    UnityEngine.Color oldColor;
    UnityEngine.Vector2[] orig, uvA, uvB;
    float tilesA, tilesB;
    public UnityEngine.Vector2 offA, offB;

    // ------------------------------------------------------------------ texture generation
    static float Frac(float v) { return v - (float)Math.Floor(v); }
    static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static float Smooth(float a, float b, float v) { float t = Clamp01((v - a) / (b - a)); return t * t * (3 - 2 * t); }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    // tileable value noise: g cells across the texture, wraps at the edges
    static float Noise(float x, float y, int g, int seed)
    {
        float fx = x * g / N, fy = y * g / N;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        int xa = ((x0 % g) + g) % g, ya = ((y0 % g) + g) % g, xb = (xa + 1) % g, yb = (ya + 1) % g;
        float a = Hash(xa, ya, seed), b = Hash(xb, ya, seed), c = Hash(xa, yb, seed), d = Hash(xb, yb, seed);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    // Returns RGBA bytes (row 0 = bottom) for the ripple layer and the caustic layer.
    public static void Generate(out UnityEngine.Color32[] rip, out UnityEngine.Color32[] cau)
    {
        rip = new UnityEngine.Color32[N * N];
        cau = new UnityEngine.Color32[N * N];
        const float P = (float)(2 * Math.PI) / N;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = x * P, v = y * P;   // integer frequencies below keep everything tileable
                float h = 0.30f * (float)Math.Sin(2 * u + 3 * v + 0.4f)
                        + 0.24f * (float)Math.Sin(5 * u - 2 * v + 1.7f)
                        + 0.18f * (float)Math.Sin(-3 * u + 7 * v + 2.9f)
                        + 0.11f * (float)Math.Sin(9 * u + 4 * v + 0.3f)
                        + 0.07f * (float)Math.Sin(13 * u - 11 * v + 4.0f)
                        + 0.45f * (Noise(x, y, 8, 1) - 0.5f) + 0.25f * (Noise(x, y, 16, 2) - 0.5f) + 0.12f * (Noise(x, y, 32, 3) - 0.5f);
                float k = Clamp01(0.5f + h * 0.65f);
                float crest = Smooth(0.78f, 0.97f, k);
                float r = 14 + (44 - 14) * k, g = 62 + (138 - 62) * k, b = 118 + (196 - 118) * k;   // deep -> shallow blue
                r += (180 - r) * crest * 0.6f; g += (225 - g) * crest * 0.6f; b += (245 - b) * crest * 0.6f;
                float a = 0.56f + 0.10f * k + 0.12f * crest;
                rip[y * N + x] = new UnityEngine.Color32((byte)r, (byte)g, (byte)b, (byte)(a * 255));

                // caustic web: bright where two warped sine fields cancel
                float s1 = (float)Math.Sin(6 * u + 1.7f * Math.Sin(4 * v + 0.5f) + 0.8f * Math.Sin(2 * u - 3 * v));
                float s2 = (float)Math.Sin(6 * v + 1.7f * Math.Sin(5 * u + 1.1f) + 0.8f * Math.Sin(3 * u + 2 * v));
                float c = (float)Math.Pow(Clamp01(1 - Math.Abs(s1 + s2) * 0.85f), 5) * (0.6f + 0.4f * Noise(x, y, 8, 7));
                cau[y * N + x] = new UnityEngine.Color32(205, 238, 255, (byte)(Clamp01(c * 0.6f) * 255));
            }
    }

    static UnityEngine.Texture2D MakeTex(UnityEngine.Color32[] px, string name)
    {
        var t = new UnityEngine.Texture2D(N, N, UnityEngine.TextureFormat.RGBA32, true);
        t.name = name;
        t.SetPixels32(px);
        t.wrapMode = UnityEngine.TextureWrapMode.Repeat;
        t.filterMode = UnityEngine.FilterMode.Trilinear;
        t.anisoLevel = 4;
        t.Apply(true, false);
        UnityEngine.Object.DontDestroyOnLoad(t);
        return t;
    }

    // the ripple texture (generated on first use); main thread
    public static UnityEngine.Texture2D RippleTexture { get { EnsureTextures(null, null); return texBase; } }

    // main thread; pngPath may be null
    public static void EnsureTextures(string pngPath, Action<string> log)
    {
        if (texBase != null && texOverlay != null) return;
        UnityEngine.Color32[] rip, cau;
        Generate(out rip, out cau);
        texBase = MakeTex(rip, "FishingWaterRipples");
        texOverlay = MakeTex(cau, "FishingWaterCaustics");
        if (log != null) log("water textures generated (" + N + "x" + N + ", ripples + caustics, mipmapped, wrap Repeat)");
        if (pngPath == null) return;
        try
        {   // preview = caustics composited over ripples
            var comp = new UnityEngine.Color32[N * N];
            for (int i = 0; i < comp.Length; i++)
            {
                float a = cau[i].a / 255f;
                comp[i] = new UnityEngine.Color32((byte)(rip[i].r + (cau[i].r - rip[i].r) * a), (byte)(rip[i].g + (cau[i].g - rip[i].g) * a),
                                                  (byte)(rip[i].b + (cau[i].b - rip[i].b) * a), (byte)(rip[i].a + (255 - rip[i].a) * a));
            }
            var t = new UnityEngine.Texture2D(N, N, UnityEngine.TextureFormat.RGBA32, false);
            t.SetPixels32(comp); t.Apply();
            byte[] png = null;
            foreach (var asm in FishingScope.Assemblies())
            {
                Type ic = asm.GetType("UnityEngine.ImageConversion");
                MethodInfo m = ic == null ? null : ic.GetMethod("EncodeToPNG", new[] { typeof(UnityEngine.Texture2D) });
                if (m != null) { png = (byte[])m.Invoke(null, new object[] { t }); break; }
            }
            UnityEngine.Object.Destroy(t);
            if (png != null) { File.WriteAllBytes(pngPath, png); if (log != null) log("preview saved: " + pngPath + " (" + png.Length + " bytes)"); }
        }
        catch (Exception e) { if (log != null) log("preview failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ apply / animate / remove
    public static FishingWaterLook Apply(UnityEngine.GameObject go, string pngPath, Action<string> log)
    {
        EnsureTextures(pngPath, log);
        var mf = go.GetComponent<UnityEngine.MeshFilter>();
        var mr = go.GetComponent<UnityEngine.MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null) { if (log != null) log(go.name + ": no MeshFilter/MeshRenderer - skipped"); return null; }

        var w = new FishingWaterLook { water = go };
        UnityEngine.Bounds bnd = mr.bounds;                    // world size before we add anything
        w.baseMat = mr.material;
        w.oldTex = w.baseMat.mainTexture; w.oldColor = w.baseMat.color;
        if (!w.baseMat.HasProperty("_MainTex"))
        {
            var sd = UnityEngine.Shader.Find("Sprites/Default");
            if (sd != null) w.baseMat.shader = sd;
        }
        w.baseMesh = mf.mesh;                                  // per-object copy
        w.orig = w.baseMesh.uv;
        float umin = float.MaxValue, umax = float.MinValue;
        foreach (var p in w.orig) { umin = Math.Min(umin, p.x); umax = Math.Max(umax, p.x); }
        float span = Math.Max(1e-3f, umax - umin);
        float size = Math.Max(bnd.size.x, bnd.size.z);
        w.tilesA = size / TileBase / span;
        w.tilesB = size / TileOverlay / span;
        w.uvA = new UnityEngine.Vector2[w.orig.Length];
        w.uvB = new UnityEngine.Vector2[w.orig.Length];
        w.SetLook();

        // caustic layer: a copy of the sheet 3 cm above, own material, drawn after the water
        w.ovMesh = UnityEngine.Object.Instantiate(w.baseMesh);
        w.overlay = new UnityEngine.GameObject(go.name + "Fx");
        w.overlay.AddComponent<UnityEngine.MeshFilter>().sharedMesh = w.ovMesh;
        var or = w.overlay.AddComponent<UnityEngine.MeshRenderer>();
        var sh = UnityEngine.Shader.Find("Sprites/Default") ?? w.baseMat.shader;
        w.ovMat = new UnityEngine.Material(sh);
        w.ovMat.mainTexture = texOverlay;
        w.ovMat.color = UnityEngine.Color.white;
        w.ovMat.renderQueue = w.baseMat.renderQueue + 1;
        or.sharedMaterial = w.ovMat;
        or.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        or.receiveShadows = false;
        var tf = w.overlay.transform;
        tf.SetParent(go.transform, false);
        float sy = Math.Abs(go.transform.lossyScale.y);
        tf.localPosition = new UnityEngine.Vector3(0, 0.03f / Math.Max(sy, 1e-4f), 0);
        tf.localRotation = UnityEngine.Quaternion.identity;
        tf.localScale = UnityEngine.Vector3.one;
        w.Animate();
        if (log != null) log("texture applied to '" + go.name + "' (size " + size.ToString("0") + " m, shader '" + w.baseMat.shader.name + "', "
            + w.orig.Length + " verts): ripples tiling " + w.tilesA.ToString("0.0") + "x" + w.tilesA.ToString("0.0") + " (" + TileBase + " m tiles), caustic layer '"
            + w.overlay.name + "' tiling " + w.tilesB.ToString("0.0") + "x" + w.tilesB.ToString("0.0") + ", queue " + w.ovMat.renderQueue);
        return w;
    }

    void SetLook()
    {
        baseMat.mainTexture = texBase;
        baseMat.color = UnityEngine.Color.white;     // the texture carries colour and alpha
        baseMat.mainTextureScale = UnityEngine.Vector2.one;
        baseMat.mainTextureOffset = UnityEngine.Vector2.zero;
    }

    // every ~1 s: if someone re-tinted / re-textured the material, put our look back
    public bool Recheck()
    {
        if (baseMat == null) return false;
        if (baseMat.mainTexture != texBase || baseMat.color != UnityEngine.Color.white) { SetLook(); return true; }
        return false;
    }

    public bool Alive { get { return water != null && baseMesh != null; } }

    public void Animate()   // every frame
    {
        float t = Now;
        offA = new UnityEngine.Vector2(Frac(0.0110f * t), Frac(0.0065f * t));     // drift one way
        offB = new UnityEngine.Vector2(Frac(-0.0085f * t), Frac(0.0140f * t));    // caustics another way
        for (int i = 0; i < orig.Length; i++)
        {
            uvA[i] = new UnityEngine.Vector2(orig[i].x * tilesA + offA.x, orig[i].y * tilesA + offA.y);
            uvB[i] = new UnityEngine.Vector2(orig[i].x * tilesB + offB.x, orig[i].y * tilesB + offB.y);
        }
        baseMesh.uv = uvA;
        if (ovMesh != null) ovMesh.uv = uvB;
    }

    // freeAll: the sheet is being/was destroyed, so also free our mesh/material copies
    public void Remove(bool freeAll = false)
    {
        try { if (overlay != null) UnityEngine.Object.Destroy(overlay); } catch { }
        try { if (ovMesh != null) UnityEngine.Object.Destroy(ovMesh); } catch { }
        try { if (ovMat != null) UnityEngine.Object.Destroy(ovMat); } catch { }
        try { if (baseMesh != null) baseMesh.uv = orig; } catch { }
        try { if (baseMat != null) { baseMat.mainTexture = oldTex; baseMat.color = oldColor; } } catch { }
        if (freeAll || water == null)
        {   // the sheet itself is gone: free our per-object mesh/material copies too
            try { if (baseMesh != null) UnityEngine.Object.Destroy(baseMesh); } catch { }
            try { if (baseMat != null) UnityEngine.Object.Destroy(baseMat); } catch { }
        }
        overlay = null; ovMesh = null; ovMat = null;
    }
}
