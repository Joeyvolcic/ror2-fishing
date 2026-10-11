// Fish3D - procedural 3D fish models for the Risk of Rain 2 fishing game (replaces the old 2D crossed quads).
//
// ============================================ API (stable - FishFx host/guest and the aquarium use it) ============================================
//   bool       Fish3D.Prepare(string species)               build + cache the mesh/texture/materials of one species (~20-30 ms).
//                                                           Call it for one species per frame to spread the cost; true = ready.
//                                                           Create() builds on demand too, so Prepare is optional.
//   GameObject Fish3D.Create(string species, float length)  new model, whole art length = `length` metres. Head toward local +X,
//                                                           back up (+Y), centred on the origin (same frame as the old quads).
//                                                           Returns null for an unknown species. Caller parents/positions/destroys it.
//   void       Fish3D.Animate(GameObject fish, float time, float swimSpeed)
//                                                           body wave + tail flick + pectoral flutter (+ lure flicker, boot tumble).
//                                                           time = any running clock (Time.time); swimSpeed 0 = hover, 1 = cruise, 2-3 = dash.
//   void       Fish3D.SetUnderwater(GameObject fish, bool under)   swap to the tinted "seen through the water" materials (drawn after the
//                                                           lake surface) or back to the normal lit materials (above water / dry land).
//   string[]   Fish3D.Species                               the 14 species names (incl. "Old Boot"), same order as FishFx / FishSchool.
//   bool       Fish3D.preferHopoo (default true)            lit shader = RoR2 'Hopoo Games/Deferred/Standard' if loaded, else Unity 'Standard'.
//   string     Fish3D.ShaderName, Fish3D.UnderShaderName    shaders actually used (for logs).    Action<string> Fish3D.log  optional logger.
// Main thread only (Unity objects). Only needs UnityEngine.CoreModule + FishArt.cs (the 2D art is projected onto the 3D bodies).
// ================================================================================================================================================
//
// How it works: every species keeps its FishArt side-view texture (128x64, head right) and the 3D geometry is built in the same pixel
// space, so a planar side projection (u = x, v = y) paints the art's colours/patterns (stripes, bars, koi patches, eyes, gills, fin rays)
// onto both flanks. Bodies are lofted rings along the spine following the art's own silhouette maths; fins are thin double-sided meshes
// shaped like the painted fins; extras: swordfish bill, catfish barbels, pufferfish spikes, anglerfish stalk + glowing bulb (+ point light),
// eel = long finless-tail body with fringe fins, boot = foot + shaft with a dark opening. Small palette strip under the art (rows 64-71) colours
// parts that have no art. Meshes are skinned (4-8 spine bones + 2 pectoral bones); Animate() only rotates bones, so one cached mesh per
// species serves every fish.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class Fish3D
{
    public static readonly string[] Species = { "Minnow", "Perch", "Trout", "Bass", "Salmon", "Swordfish", "Golden Koi", "Moon Eel", "Old Boot",
                                                "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };
    public static Action<string> log;
    public static string ShaderName = "", UnderShaderName = "";
    public static int modelsBuilt;
    public static bool preferHopoo = true;   // false = lit with Unity 'Standard' instead of RoR2's 'Hopoo Games/Deferred/Standard' (set before the first Prepare/Create)

    static void Log(string s) { if (log != null) try { log(s); } catch { } }

    // ------------------------------------------------------------------------------------------------ cache
    internal class Model
    {
        public string sp; public Mesh mesh; public Material[] mats, matsUnder; public Vector3[] bonePos; public int chain; public int kind;
        public Bounds boundsRel; public Vector3 lurePos; public bool hasLure;
    }
    static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();
    static Shader litShader, underShader, spriteShader; static bool shadersFound; static bool litIsHopoo;
    static Texture2D whiteTex;

    public static bool Prepare(string species) { return Get(species) != null; }

    internal static Model Get(string species)
    {
        if (species == null) return null;
        Model m;
        if (models.TryGetValue(species, out m)) return m;
        if (Array.IndexOf(Species, species) < 0) return null;
        try { m = Build(species); }
        catch (Exception e) { Log("Fish3D build error " + species + ": " + e); m = null; }
        models[species] = m;
        return m;
    }

    static void FindShaders()
    {
        if (shadersFound) return; shadersFound = true;
        if (preferHopoo) try
        {
            foreach (Material mt in Resources.FindObjectsOfTypeAll<Material>())
                if (mt != null && mt.shader != null && mt.shader.name == "Hopoo Games/Deferred/Standard") { litShader = mt.shader; litIsHopoo = true; break; }
        }
        catch { }
        if (litShader == null && preferHopoo) litShader = Shader.Find("Hopoo Games/Deferred/Standard");
        if (litShader == null) litShader = Shader.Find("Standard");
        if (litShader == null) litShader = Shader.Find("Legacy Shaders/Diffuse");
        spriteShader = Shader.Find("Sprites/Default");
        if (litShader == null) litShader = spriteShader;
        underShader = Shader.Find("Standard");
        if (underShader == null) underShader = Shader.Find("Legacy Shaders/Diffuse");
        if (underShader == null) underShader = spriteShader;
        ShaderName = litShader != null ? litShader.name : "NONE";
        UnderShaderName = underShader != null ? underShader.name : "NONE";
        whiteTex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var wc = new Color32[16]; for (int i = 0; i < 16; i++) wc[i] = new Color32(255, 255, 255, 255);
        whiteTex.SetPixels32(wc); whiteTex.Apply();
        Log("Fish3D shaders: lit '" + ShaderName + "', underwater '" + UnderShaderName + "', fx '" + (spriteShader != null ? spriteShader.name : "NONE") + "'");
    }

    // ------------------------------------------------------------------------------------------------ texture (art + palette)
    const int TW = 128, TH = 72, ArtH = 64;
    // palette cells (8 px wide, bottom 8 rows)
    const int PSpikeBase = 0, PSpikeTip = 1, PWhisker = 2, PStalk = 3, PBulb = 4, PBootDark = 5, PWhite = 6, PBlack = 7;
    internal static readonly Color32[] Palette = {
        new Color32(178, 148, 77, 255), new Color32(242, 230, 191, 255), new Color32(80, 66, 48, 255), new Color32(77, 51, 36, 255),
        new Color32(215, 255, 255, 255), new Color32(26, 15, 8, 255), new Color32(250, 250, 250, 255), new Color32(8, 8, 10, 255) };

    static Texture2D MakeTexture(string sp)
    {
        byte[] px = FishArt.RenderBytesBottomUp(sp);    // 128x64, row 0 = bottom
        var c = new Color32[TW * TH];
        // art -> unity rows 8..71
        for (int y = 0; y < ArtH; y++)
            for (int x = 0; x < TW; x++)
            {
                int s = (y * TW + x) * 4;
                c[(y + (TH - ArtH)) * TW + x] = new Color32(px[s], px[s + 1], px[s + 2], px[s + 3]);
            }
        // dilate colour into transparent art pixels so silhouette-edge texels (and mips) are not black
        var tmp = new Color32[c.Length];
        for (int it = 0; it < 8; it++)
        {
            Array.Copy(c, tmp, c.Length);
            for (int y = TH - ArtH; y < TH; y++)
                for (int x = 0; x < TW; x++)
                {
                    int i = y * TW + x;
                    if (tmp[i].a > 40 || (it > 0 && tmp[i].a == 1)) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || xx >= TW || yy < TH - ArtH || yy >= TH || (dx == 0 && dy == 0)) continue;
                            Color32 q = tmp[yy * TW + xx];
                            if (q.a > 40 || q.a == 1) { r += q.r; g += q.g; b += q.b; n++; }
                        }
                    if (n > 0) c[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), tmp[i].a > 1 ? tmp[i].a : (byte)1);
                }
        }
        // palette
        for (int y = 0; y < TH - ArtH; y++)
            for (int x = 0; x < TW; x++)
            {
                int cell = x / 8;
                c[y * TW + x] = cell < Palette.Length ? Palette[cell] : new Color32(128, 128, 128, 255);
            }
        var t = new Texture2D(TW, TH, TextureFormat.RGBA32, true);
        t.SetPixels32(c);
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.anisoLevel = 2; t.name = "Fish3D_" + sp;
        t.Apply(true);
        return t;
    }

    static Vector2 ArtUV(float px, float py)
    {
        px = Mathf.Clamp(px, 0.5f, TW - 0.5f); py = Mathf.Clamp(py, 0.5f, ArtH - 0.5f);
        return new Vector2(px / TW, 1f - py / TH);
    }
    static Vector2 PalUV(int cell) { return new Vector2((cell * 8 + 4f) / TW, 4f / TH); }

    // ------------------------------------------------------------------------------------------------ mesh builder (pixel space in, unit space out)
    // pixel space: x 0..128 (head right), y 0..64 DOWN (art rows), z = sideways in pixels. unit: X = (x-64)/128, Y = (32-y)/128, Z = z/128.
    class MB
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<Color> col = new List<Color>();
        public readonly List<bool> fixedCol = new List<bool>();
        public readonly List<int> bone = new List<int>();     // -1 = by spine position
        public readonly List<float> spineX = new List<float>();
        public readonly List<int>[] tri = { new List<int>(), new List<int>() };
        public Func<float, float, Vector2> map = ArtUV;

        public static Vector3 U(float x, float y, float z) { return new Vector3((x - 64f) / 128f, (32f - y) / 128f, z / 128f); }
        public int Add(float x, float y, float z) { return AddUV(x, y, z, map(x, y), -1); }
        public int AddUV(float x, float y, float z, Vector2 t, int b)
        {
            v.Add(U(x, y, z)); uv.Add(t); col.Add(Color.white); fixedCol.Add(false); bone.Add(b); spineX.Add(x);
            return v.Count - 1;
        }
        public int AddU(Vector3 p, Vector2 t, int b, float sx)
        {
            v.Add(p); uv.Add(t); col.Add(Color.white); fixedCol.Add(false); bone.Add(b); spineX.Add(sx);
            return v.Count - 1;
        }
        // triangle facing away from `inside` (unit space)
        public void TriOut(int a, int b, int c, Vector3 inside, int sub)
        {
            Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (n.sqrMagnitude < 1e-14f) return;
            Vector3 cen = (v[a] + v[b] + v[c]) / 3f;
            if (Vector3.Dot(n, cen - inside) < 0) { int t = b; b = c; c = t; }
            tri[sub].Add(a); tri[sub].Add(b); tri[sub].Add(c);
        }
        public void Tri2(int a, int b, int c, int sub) { tri[sub].Add(a); tri[sub].Add(b); tri[sub].Add(c); }
        public int Dup(int i) { return AddU(v[i], uv[i], bone[i], spineX[i]); }
        // double-sided triangle (separate vertices per side so normals stay sharp)
        public void TriDS(int a, int b, int c, int sub)
        {
            Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (n.sqrMagnitude < 1e-14f) return;
            Tri2(a, b, c, sub);
            Tri2(Dup(a), Dup(c), Dup(b), sub);
        }
    }

    // loft along x: sil(x) -> (top y, bottom y) in pixels; halfW(x, halfHeight) -> half width in pixels
    static void Loft(MB m, float xa, float xb, int n, int ring, Func<float, Vector2> sil, Func<float, float, float> halfW, float square, int sub)
    {
        var rings = new List<int[]>(); var mids = new List<Vector3>(); var xs = new List<float>();
        for (int i = 0; i < n; i++)
        {
            float x = xa + (xb - xa) * (1f - Mathf.Cos(Mathf.PI * i / (n - 1))) * 0.5f;
            Vector2 tb = sil(x);
            float h = (tb.y - tb.x) * 0.5f, mid = (tb.x + tb.y) * 0.5f;
            if (h < 0.3f) continue;
            float w = Mathf.Max(0.3f, halfW(x, h));
            var r = new int[ring];
            for (int k = 0; k < ring; k++)
            {
                float a = 2f * Mathf.PI * k / ring, s = Mathf.Sin(a), c = Mathf.Cos(a);
                float sy = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), square), cz = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), square);
                r[k] = m.Add(x, mid - h * sy * 0.985f, w * cz);
            }
            rings.Add(r); mids.Add(MB.U(x, mid, 0)); xs.Add(x);
        }
        if (rings.Count < 2) return;
        for (int i = 0; i + 1 < rings.Count; i++)
        {
            Vector3 inside = (mids[i] + mids[i + 1]) * 0.5f;
            for (int k = 0; k < ring; k++)
            {
                int k2 = (k + 1) % ring;
                m.TriOut(rings[i][k], rings[i + 1][k], rings[i + 1][k2], inside, sub);
                m.TriOut(rings[i][k], rings[i + 1][k2], rings[i][k2], inside, sub);
            }
        }
        // end caps (tip at the true end x; flat if the end station already has height)
        for (int e = 0; e < 2; e++)
        {
            int ri = e == 0 ? 0 : rings.Count - 1;
            float ex = e == 0 ? xa : xb;
            Vector2 tb = sil(xs[ri]); float mid = (tb.x + tb.y) * 0.5f;
            int tip = m.Add(ex, mid, 0);
            Vector3 inside = mids[e == 0 ? Mathf.Min(1, rings.Count - 1) : Mathf.Max(0, rings.Count - 2)];
            if (Mathf.Abs(ex - xs[ri]) < 0.01f) inside = mids[ri] + new Vector3(e == 0 ? 0.01f : -0.01f, 0, 0);
            for (int k = 0; k < ring; k++) m.TriOut(tip, rings[ri][k], rings[ri][(k + 1) % ring], inside, sub);
        }
    }

    // flat fin strip: columns (x, y0, y1) in pixel space, placed by pos(x, y) -> pixel xyz; double-sided
    static void FinStrip(MB m, List<Vector3> cols, Func<float, float, Vector3> pos, int bone)
    {
        int pa = -1, pb = -1;
        foreach (Vector3 c in cols)
        {
            Vector3 p0 = pos(c.x, c.y), p1 = pos(c.x, c.z);
            int a = m.AddUV(p0.x, p0.y, p0.z, m.map(c.x, c.y), bone), b = m.AddUV(p1.x, p1.y, p1.z, m.map(c.x, c.z), bone);
            if (pa >= 0) { m.TriDS(pa, a, b, 0); m.TriDS(pa, b, pb, 0); }
            pa = a; pb = b;
        }
    }
    static Vector3 Flat(float x, float y) { return new Vector3(x, y, 0); }

    // flat elliptical fin (fan), points in the art plane mapped by pos
    static void FinDisc(MB m, float cx, float cy, float rx, float ry, float tiltCos, float tiltSin, Func<float, float, Vector3> pos, int bone, int seg)
    {
        Vector3 pc = pos(cx, cy);
        int c = m.AddUV(pc.x, pc.y, pc.z, m.map(cx, cy), bone);
        int first = -1, prev = -1;
        for (int k = 0; k <= seg; k++)
        {
            float a = 2f * Mathf.PI * k / seg;
            float ex = Mathf.Cos(a), ey = Mathf.Sin(a);
            float x = cx + (ex * tiltCos - ey * tiltSin) * rx, y = cy + (ex * tiltSin + ey * tiltCos) * ry;
            Vector3 p = pos(x, y);
            int i = k == seg ? first : m.AddUV(p.x, p.y, p.z, m.map(x, y), bone);
            if (k == 0) first = i;
            if (prev >= 0) m.TriDS(c, prev, i, 0);
            prev = i;
        }
    }

    // tube along points (pixel space), constant palette colour
    static void Tube(MB m, List<Vector3> pts, float r0, float r1, int sides, Vector2 uv, int bone, int sub)
    {
        var rings = new List<int[]>(); var cen = new List<Vector3>();
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 a = MB.U(pts[i].x, pts[i].y, pts[i].z);
            Vector3 d = (i + 1 < pts.Count ? MB.U(pts[i + 1].x, pts[i + 1].y, pts[i + 1].z) : a) - (i > 0 ? MB.U(pts[i - 1].x, pts[i - 1].y, pts[i - 1].z) : a);
            if (d.sqrMagnitude < 1e-12f) d = Vector3.right;
            d.Normalize();
            Vector3 s1 = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized, s2 = Vector3.Cross(d, s1);
            float r = Mathf.Lerp(r0, r1, i / (float)(pts.Count - 1)) / 128f;
            var ring = new int[sides];
            for (int k = 0; k < sides; k++)
            {
                float an = 2f * Mathf.PI * k / sides;
                ring[k] = m.AddU(a + (s1 * Mathf.Cos(an) + s2 * Mathf.Sin(an)) * r, uv, bone, pts[i].x);
            }
            rings.Add(ring); cen.Add(a);
        }
        for (int i = 0; i + 1 < rings.Count; i++)
            for (int k = 0; k < sides; k++)
            {
                int k2 = (k + 1) % sides; Vector3 ins = (cen[i] + cen[i + 1]) * 0.5f;
                m.TriOut(rings[i][k], rings[i + 1][k], rings[i + 1][k2], ins, sub);
                m.TriOut(rings[i][k], rings[i + 1][k2], rings[i][k2], ins, sub);
            }
        Vector3 tipP = cen[cen.Count - 1] + (cen[cen.Count - 1] - cen[cen.Count - 2]).normalized * r1 / 128f;
        int tip = m.AddU(tipP, uv, bone, pts[pts.Count - 1].x);
        for (int k = 0; k < sides; k++) m.TriOut(tip, rings[rings.Count - 1][k], rings[rings.Count - 1][(k + 1) % sides], cen[cen.Count - 1], sub);
    }

    // sphere (unit space centre), explicit vertex colour (glow parts) or palette uv
    static void Sphere(MB m, Vector3 c, float r, int lat, int lon, Vector2 uv, Color col, bool fixedColor, int bone, float sx, int sub)
    {
        var idx = new int[lat + 1, lon];
        for (int i = 0; i <= lat; i++)
            for (int j = 0; j < lon; j++)
            {
                float th = Mathf.PI * i / lat, ph = 2f * Mathf.PI * j / lon;
                Vector3 p = c + new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)) * r;
                int vi = m.AddU(p, uv, bone, sx);
                if (fixedColor) { m.col[vi] = col; m.fixedCol[vi] = true; }
                idx[i, j] = vi;
            }
        for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                int j2 = (j + 1) % lon;
                m.TriOut(idx[i, j], idx[i + 1, j], idx[i + 1, j2], c, sub);
                m.TriOut(idx[i, j], idx[i + 1, j2], idx[i, j2], c, sub);
            }
    }

    // dome on the flank carrying the painted eye (projected uv)
    static void EyeDome(MB m, float ex, float ey, float r, float zBase, float bulge, int bone)
    {
        for (int side = -1; side <= 1; side += 2)
        {
            const int lat = 3, lon = 10;
            var idx = new int[lat + 1, lon];
            int top = m.Add(ex, ey, side * (zBase + bulge));
            for (int i = 1; i <= lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    float th = 0.5f * Mathf.PI * i / lat, ph = 2f * Mathf.PI * j / lon;
                    float x = ex + Mathf.Sin(th) * Mathf.Cos(ph) * r, y = ey + Mathf.Sin(th) * Mathf.Sin(ph) * r;
                    idx[i, j] = m.Add(x, y, side * (zBase + Mathf.Cos(th) * bulge - (i == lat ? 0.4f : 0f)));
                }
            Vector3 ins = MB.U(ex, ey, side * (zBase - 2f));
            for (int j = 0; j < lon; j++) m.TriOut(top, idx[1, j], idx[1, (j + 1) % lon], ins, 0);
            for (int i = 1; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    int j2 = (j + 1) % lon;
                    m.TriOut(idx[i, j], idx[i + 1, j], idx[i + 1, j2], ins, 0);
                    m.TriOut(idx[i, j], idx[i + 1, j2], idx[i, j2], ins, 0);
                }
        }
    }

    // ------------------------------------------------------------------------------------------------ species shapes (mirrors FishArt.Get)
    class FS
    {
        public float x0 = 5, xb = 30, x1 = 122, cy = 32, bh = 16, peak = 0.45f, ped = 0.2f, bellyK = 0.95f, tailSpread = 17, fork = 0.55f;
        public float dorsal0 = 0.42f, dorsal1 = 0.68f, dorsalH = 9, dorsal2_0, dorsal2_1, dorsal2H, analT = 0.25f, analH = 6, pelvicT = 0.55f, pelvicH = 6;
        public float eyeR = 3f, eyeT = 0.88f, widthK = 0.5f; public bool blunt;
    }
    static FS Spec(string sp)
    {
        var s = new FS();
        switch (sp)
        {
            case "Minnow": s.bh = 12; s.tailSpread = 14; s.dorsalH = 8; s.dorsal0 = 0.45f; s.dorsal1 = 0.62f; s.analH = 5; s.pelvicH = 4; s.eyeR = 3.2f; s.widthK = 0.55f; break;
            case "Perch": s.bh = 19; s.peak = 0.5f; s.tailSpread = 17; s.dorsal0 = 0.52f; s.dorsal1 = 0.8f; s.dorsalH = 12; s.dorsal2_0 = 0.25f; s.dorsal2_1 = 0.47f; s.dorsal2H = 9; s.analH = 8; s.pelvicH = 8; s.widthK = 0.42f; break;
            case "Trout": s.bh = 15; s.tailSpread = 16; s.fork = 0.3f; s.dorsal0 = 0.45f; s.dorsal1 = 0.62f; s.dorsalH = 9; s.widthK = 0.55f; break;
            case "Bass": s.bh = 19; s.x1 = 123; s.tailSpread = 17; s.fork = 0.25f; s.dorsal0 = 0.32f; s.dorsal1 = 0.72f; s.dorsalH = 10; s.eyeT = 0.86f; s.widthK = 0.5f; break;
            case "Salmon": s.bh = 16; s.peak = 0.5f; s.tailSpread = 18; s.fork = 0.35f; s.dorsal0 = 0.48f; s.dorsal1 = 0.64f; s.dorsalH = 10; s.widthK = 0.55f; break;
            case "Swordfish": s.x0 = 3; s.xb = 22; s.x1 = 96; s.bh = 14; s.tailSpread = 23; s.fork = 0.85f; s.ped = 0.15f; s.dorsal0 = 0.6f; s.dorsal1 = 0.8f; s.dorsalH = 16; s.eyeT = 0.84f; s.eyeR = 3.4f; s.widthK = 0.62f; break;
            case "Golden Koi": s.bh = 16; s.tailSpread = 20; s.fork = 0.2f; s.dorsal0 = 0.3f; s.dorsal1 = 0.7f; s.dorsalH = 8; s.analH = 8; s.pelvicH = 9; s.widthK = 0.6f; break;
            case "Clownfish": s.bh = 17; s.peak = 0.5f; s.tailSpread = 15; s.fork = 0.04f; s.dorsal0 = 0.28f; s.dorsal1 = 0.74f; s.dorsalH = 9; s.analH = 8; s.pelvicH = 7; s.eyeR = 3.4f; s.widthK = 0.5f; break;
            case "Catfish": s.x1 = 110; s.bh = 13; s.peak = 0.6f; s.ped = 0.25f; s.blunt = true; s.bellyK = 0.8f; s.tailSpread = 15; s.fork = 0.3f; s.dorsal0 = 0.6f; s.dorsal1 = 0.72f; s.dorsalH = 11; s.analT = 0.3f; s.analH = 6; s.pelvicH = 5; s.eyeR = 2.2f; s.eyeT = 0.9f; s.widthK = 0.85f; break;
            case "Ghost Fish": s.bh = 15; s.tailSpread = 20; s.fork = 0.45f; s.dorsal0 = 0.35f; s.dorsal1 = 0.7f; s.dorsalH = 11; s.analH = 8; s.pelvicH = 8; s.eyeR = 3.2f; s.widthK = 0.5f; break;
        }
        return s;
    }
    static float Profile(FS s, float t)
    {
        if (t < 0 || t > 1) return 0;
        if (t < s.peak) return Mathf.Lerp(s.ped, 1f, Mathf.Sin(t / s.peak * Mathf.PI / 2));
        float u = (t - s.peak) / (1 - s.peak);
        if (s.blunt) return Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow(u, 6))) * (1 - 0.08f * u);
        return Mathf.Sqrt(Mathf.Max(0, 1 - u * u * u)) * (1 - 0.15f * u);
    }
    static float Smooth(float e0, float e1, float x) { float t = Mathf.Clamp01((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }

    // ------------------------------------------------------------------------------------------------ build one species
    // pure geometry (no Unity engine calls - also used by the offline preview tool)
    internal class Geo
    {
        public List<Vector3> v; public List<Vector2> uv; public List<int>[] tri; public List<Color> col; public List<bool> fixedCol;
        public List<int> bone; public List<float> spineX; public float[] bones; public Vector3 pecL, pecR, lurePos; public bool hasLure; public int kind;
    }
    const int BonePecL = 100, BonePecR = 101;   // placeholders, remapped to real bone indices in Build

    static Model Build(string sp)
    {
        FindShaders();
        float t0 = Time.realtimeSinceStartup;
        Geo g0 = Geometry(sp);
        var md = new Model { sp = sp, kind = g0.kind, hasLure = g0.hasLure, lurePos = g0.lurePos };
        var m = new MB(); m.v.AddRange(g0.v); m.uv.AddRange(g0.uv); m.tri[0].AddRange(g0.tri[0]); m.tri[1].AddRange(g0.tri[1]);
        m.col.AddRange(g0.col); m.fixedCol.AddRange(g0.fixedCol); m.bone.AddRange(g0.bone); m.spineX.AddRange(g0.spineX);
        float[] bones = g0.bones; int kind = g0.kind; Vector3 pecL = g0.pecL, pecR = g0.pecR;
        return Finish(sp, md, m, bones, kind, pecL, pecR, t0);
    }

    internal static Geo Geometry(string sp)
    {
        var md = new Geo();
        var m = new MB();
        float[] bones;                 // spine bone x (pixels), head -> tail
        Vector3 pecL = Vector3.zero, pecR = Vector3.zero;   // pectoral bone pivots (pixel space)
        int kind = sp == "Moon Eel" ? 1 : sp == "Old Boot" ? 2 : sp == "Pufferfish" ? 3 : sp == "Anglerfish" ? 4 : 0;
        md.kind = kind;

        if (kind == 0)
        {
            FS s = Spec(sp);
            Func<float, Vector2> sil = x =>
            {
                float t = (x - s.xb) / (s.x1 - s.xb), p = Profile(s, t);
                return new Vector2(s.cy - s.bh * p, s.cy + s.bh * p * s.bellyK);
            };
            Func<float, float, float> hw = (x, h) =>
            {
                float t = (x - s.xb) / (s.x1 - s.xb);
                return s.widthK * h * (0.5f + 0.5f * Smooth(-0.1f, 0.55f, t));
            };
            Func<float, float, float> bodyZ = (x, y) =>
            {
                Vector2 tb = sil(x); float h = (tb.y - tb.x) * 0.5f, mid = (tb.x + tb.y) * 0.5f;
                if (h < 0.01f) return 0;
                float q = (y - mid) / h; if (q * q >= 1) return 0;
                return hw(x, h) * Mathf.Sqrt(1 - q * q);
            };
            Loft(m, s.xb, s.x1, 28, 16, sil, hw, 1f, 0);
            // caudal fin
            float tailEnd = s.xb + 3;
            var up = new List<Vector3>(); var lo = new List<Vector3>();
            for (int i = 0; i <= 10; i++)
            {
                float x = s.x0 + (tailEnd + 3 - s.x0) * i / 10f;
                float k = Mathf.Clamp01((tailEnd - x) / (tailEnd - s.x0));
                float spread = Mathf.Lerp(s.bh * s.ped, s.tailSpread, Mathf.Pow(k, 0.8f)) * 0.98f;
                float notch = spread * s.fork * Mathf.Pow(k, 1.6f);
                if (x > tailEnd) { spread = s.bh * s.ped; notch = 0; }
                up.Add(new Vector3(x, s.cy - notch, s.cy - spread));
                lo.Add(new Vector3(x, s.cy + notch, s.cy + spread));
            }
            FinStrip(m, up, Flat, -1); FinStrip(m, lo, Flat, -1);
            // dorsal fin(s)
            for (int d = 0; d < 2; d++)
            {
                float a0 = d == 0 ? s.dorsal0 : s.dorsal2_0, a1 = d == 0 ? s.dorsal1 : s.dorsal2_1, hg = d == 0 ? s.dorsalH : s.dorsal2H;
                if (hg <= 0) continue;
                float xa = s.xb + (s.x1 - s.xb) * a0, xz = s.xb + (s.x1 - s.xb) * a1;
                var cols = new List<Vector3>();
                for (int i = 0; i <= 12; i++)
                {
                    float x = (xa - 4) + (xz - (xa - 4)) * i / 12f, f = i / 12f;
                    float h = f > 0.65f ? hg * (1 - f) / 0.35f : hg * Mathf.Pow(f / 0.65f, 0.7f);
                    if (sp == "Swordfish") h = f > 0.5f ? hg * (1 - f) / 0.5f * (1.2f - 0.2f * f) : hg * f / 0.5f * 0.9f;
                    float top = sil(x).x;
                    cols.Add(new Vector3(x, top + 1.5f, top - Mathf.Max(0.3f, h * 0.97f)));
                }
                FinStrip(m, cols, Flat, -1);
            }
            // anal (median) + pelvic (paired, splayed) fins
            for (int d = 0; d < 3; d++)
            {
                float tt = d == 0 ? s.analT : s.pelvicT, hg = d == 0 ? s.analH : s.pelvicH;
                float xc = s.xb + (s.x1 - s.xb) * tt, w = hg * 1.1f;
                var cols = new List<Vector3>();
                for (int i = 0; i <= 8; i++)
                {
                    float x = (xc - w) + (w + 1) * i / 8f, f = i / 8f;
                    float h = hg * Mathf.Sqrt(Mathf.Max(0, 1 - f)) * (f < 0.2f ? f / 0.2f : 1);
                    float bot = sil(x).y;
                    cols.Add(new Vector3(x, bot - 1.5f, bot + Mathf.Max(0.3f, h * 0.97f)));
                }
                if (d == 0) { FinStrip(m, cols, Flat, -1); continue; }
                float side = d == 1 ? 1 : -1, ang = 32f * Mathf.Deg2Rad;
                float y0 = sil(xc).y - 1f;
                FinStrip(m, cols, (x, y) => new Vector3(x, y0 + (y - y0) * Mathf.Cos(ang), side * (y - y0) * Mathf.Sin(ang)), -1);
            }
            // pectoral fins (bones at the root so they can flutter)
            {
                float px = s.xb + (s.x1 - s.xb) * 0.7f, py = s.cy + s.bh * 0.25f;
                float rx = s.bh * 0.55f, ry = s.bh * 0.22f;
                float rootX = px + rx * 0.75f, rootY = py + ry * 0.2f;
                float z0 = bodyZ(rootX, rootY) - 0.3f;
                for (int side = -1; side <= 1; side += 2)
                {
                    float sd = side, splay = 28f * Mathf.Deg2Rad;
                    Func<float, float, Vector3> pos = (x, y) => { float dd = rootX - x; return new Vector3(rootX - dd * Mathf.Cos(splay), y, sd * (z0 + Mathf.Max(0, dd) * Mathf.Sin(splay))); };
                    FinDisc(m, px, py, rx, ry, 0.894f, -0.447f, pos, side < 0 ? BonePecL : BonePecR, 12);
                    if (side < 0) pecL = new Vector3(rootX, rootY, -z0); else pecR = new Vector3(rootX, rootY, z0);
                }
            }
            // eyes
            {
                float ex = s.xb + (s.x1 - s.xb) * s.eyeT, ey = s.cy - s.bh * 0.22f;
                EyeDome(m, ex, ey, s.eyeR * 1.05f, bodyZ(ex, ey) - 0.2f, s.eyeR * 0.45f, -1);
            }
            // extras
            if (sp == "Swordfish")
            {
                float by = s.cy - s.bh * 0.05f, xs = s.x1 - 8;
                Loft(m, xs, 127.5f, 10, 8, x => { float k = Mathf.Clamp01((x - (s.x1 - 6)) / (127 - (s.x1 - 6))); float hh = Mathf.Lerp(2.6f, 0.35f, k); return new Vector2(by - hh, by + hh); },
                     (x, h) => h * 0.8f, 1f, 0);
            }
            if (sp == "Catfish")
            {
                float[] W = { 108, 33, 123, 25, 126, 46, 108, 35, 119, 40, 114, 57, 104, 40, 109, 48, 100, 55, 105, 40, 113, 51, 107, 60 };
                for (int w = 0; w < W.Length; w += 6)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var pts = new List<Vector3>();
                        float zr = Mathf.Max(1f, bodyZ(W[w], W[w + 1]));
                        for (int i = 0; i <= 8; i++)
                        {
                            float t = i / 8f, u = 1 - t;
                            float x = u * u * W[w] + 2 * u * t * W[w + 2] + t * t * W[w + 4], y = u * u * W[w + 1] + 2 * u * t * W[w + 3] + t * t * W[w + 5];
                            pts.Add(new Vector3(x, y, side * (zr * (1 - 0.3f * t) + 7f * t)));
                        }
                        Tube(m, pts, 0.9f, 0.35f, 4, PalUV(PWhisker), -1, 0);
                    }
            }
            float L = s.x1 - s.xb;
            bones = new[] { s.xb + L * 0.72f, s.xb + L * 0.48f, s.xb + L * 0.24f, s.xb + 1.5f };
        }
        else if (kind == 1)   // Moon Eel: long straight body (the swim wave makes the S), fringe fins, art mapped with its wavy centre line
        {
            const float x0 = 4, x1 = 124;
            Func<float, float> cyArt = x => { float t = (x - x0) / (x1 - x0); return 32 + 5f * Mathf.Sin(t * Mathf.PI * 2.2f + 0.6f) * (1 - t * 0.6f); };
            Func<float, float> half = x =>
            {
                float t = (x - x0) / (x1 - x0); if (t < 0 || t > 1) return 0;
                float hh = 6.5f * Mathf.Pow(Mathf.Min(1, t / 0.55f), 0.75f);
                if (t > 0.9f) hh *= Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow((t - 0.9f) / 0.1f, 2)));
                return hh;
            };
            m.map = (x, y) => ArtUV(x, y + cyArt(x) - 32f);
            Loft(m, x0, x1, 44, 12, x => new Vector2(32 - half(x), 32 + half(x)), (x, h) => h * 0.8f, 1f, 0);
            var up = new List<Vector3>(); var lo = new List<Vector3>();
            for (int i = 0; i <= 30; i++)
            {
                float t = 0.04f + 0.78f * i / 30f, x = x0 + (x1 - x0) * t, hh = half(x);
                float fh = 2.6f * Mathf.Min(1, t / 0.1f) * (i == 30 ? 0.2f : 1f);
                up.Add(new Vector3(x, 32 - hh + 1f, 32 - hh - fh));
                lo.Add(new Vector3(x, 32 + hh - 1f, 32 + hh + fh));
            }
            FinStrip(m, up, Flat, -1); FinStrip(m, lo, Flat, -1);
            bones = new[] { 110f, 96, 82, 68, 54, 40, 26, 12 };
        }
        else if (kind == 2)   // Old Boot
        {
            Func<float, float, bool> foot = (x, y) =>
            {
                float tx = (x - 100) / 14f, ty = (y - 45) / 9.5f, ix = (x - 62) / 38f;
                return (x >= 30 && x <= 100 && y >= 34 && y <= 54) || (tx * tx + ty * ty < 1 && x >= 100)
                    || (x > 58 && x < 100 && y > 34 - 6 * (1 - Mathf.Clamp01(ix)) && y <= 36)
                    || (x >= 27 && x <= 112 && y >= 53 && y <= 58 && !(x > 108 && y > 55)) || (x >= 29 && x <= 48 && y >= 56 && y <= 61);
            };
            Func<float, Vector2> sil = x =>
            {
                float top = 99, bot = -99;
                for (float y = 20; y <= 62; y += 0.25f) if (foot(x, y)) { if (y < top) top = y; if (y > bot) bot = y; }
                return top > bot ? new Vector2(40, 40) : new Vector2(top, bot);
            };
            Loft(m, 27, 114, 30, 20, sil, (x, h) => 12f * (x > 100 ? 0.4f + 0.6f * Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow((x - 100) / 14.5f, 2))) : x < 31 ? 0.85f : 1f), 0.45f, 0);
            // shaft: vertical loft (leans back), dark opening on top
            const int ring = 20, n = 9;
            var rings = new List<int[]>(); var cens = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                float y = 46 - 40f * i / (n - 1);        // 46 (inside the foot) up to 6
                float c = 46 - (44 - y) * 0.12f;
                var r = new int[ring];
                for (int k = 0; k < ring; k++)
                {
                    float a = 2f * Mathf.PI * k / ring, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    float sx = Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 0.6f), sz = Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 0.6f);
                    r[k] = m.Add(c + 15.6f * sx, y, 11f * sz);
                }
                rings.Add(r); cens.Add(MB.U(c, y, 0));
            }
            for (int i = 0; i + 1 < n; i++)
                for (int k = 0; k < ring; k++)
                {
                    int k2 = (k + 1) % ring; Vector3 ins = (cens[i] + cens[i + 1]) * 0.5f;
                    m.TriOut(rings[i][k], rings[i + 1][k], rings[i + 1][k2], ins, 0);
                    m.TriOut(rings[i][k], rings[i + 1][k2], rings[i][k2], ins, 0);
                }
            // opening: rim -> inner ring (dark) -> deep centre, double-sided
            {
                float ct = 46 - (44 - 6) * 0.12f;
                int[] top = rings[n - 1]; var inner = new int[ring];
                for (int k = 0; k < ring; k++)
                {
                    float a = 2f * Mathf.PI * k / ring, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    float sx = Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 0.6f), sz = Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 0.6f);
                    inner[k] = m.AddUV(ct + 13f * sx, 7.5f, 9f * sz, PalUV(PBootDark), -1);
                }
                int deep = m.AddUV(ct, 14f, 0, PalUV(PBootDark), -1);
                for (int k = 0; k < ring; k++)
                {
                    int k2 = (k + 1) % ring;
                    int a = m.AddUV(m.spineX[top[k]], 0, 0, PalUV(PBootDark), -1); m.v[a] = m.v[top[k]];
                    int b = m.AddUV(m.spineX[top[k2]], 0, 0, PalUV(PBootDark), -1); m.v[b] = m.v[top[k2]];
                    m.TriDS(a, b, inner[k2], 0); m.TriDS(a, inner[k2], inner[k], 0);
                    m.TriDS(inner[k], inner[k2], deep, 0);
                }
            }
            bones = new[] { 64f };
        }
        else if (kind == 3)   // Pufferfish: spiky ellipsoid
        {
            const float cx = 64, cy = 33, rx = 24, ry = 21, rz = 19;
            Func<float, Vector2> sil = x => { float u = (x - cx) / rx, q = Mathf.Sqrt(Mathf.Max(0, 1 - u * u)); return new Vector2(cy - ry * q, cy + ry * q); };
            Loft(m, cx - rx, cx + rx, 20, 18, sil, (x, h) => h * rz / ry, 1f, 0);
            var up = new List<Vector3>(); var lo = new List<Vector3>();
            for (int i = 0; i <= 8; i++)
            {
                float x = 27 + 20f * i / 8f, k = Mathf.Clamp01((44 - x) / 17f), spread = Mathf.Lerp(4, 11, k);
                float notch = k > 0.7f ? 2.5f * (k - 0.7f) / 0.3f : 0;
                up.Add(new Vector3(x, cy - notch, cy - spread)); lo.Add(new Vector3(x, cy + notch, cy + spread));
            }
            FinStrip(m, up, Flat, -1); FinStrip(m, lo, Flat, -1);
            FinDisc(m, 48, 15, 5, 4, 1, 0, Flat, -1, 10);
            FinDisc(m, 48, 51, 5, 4, 1, 0, Flat, -1, 10);
            for (int side = -1; side <= 1; side += 2)
            {
                float sd = side, rootX = 74, rootY = 38, z0 = 0.9f * rz * Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow((rootX - cx) / rx, 2) - Mathf.Pow((rootY - cy) / ry, 2))), splay = 35f * Mathf.Deg2Rad;
                Func<float, float, Vector3> pos = (x, y) => { float dd = rootX - x; return new Vector3(rootX - dd * Mathf.Cos(splay), y, sd * (z0 + Mathf.Max(0, dd) * Mathf.Sin(splay))); };
                FinDisc(m, 70, 38, 5, 3, 1, 0, pos, side < 0 ? BonePecL : BonePecR, 10);
                if (side < 0) pecL = new Vector3(rootX, rootY, -z0); else pecR = new Vector3(rootX, rootY, z0);
            }
            // spikes (Fibonacci points; none on the tail root, mouth or eyes)
            const int N = 110;
            Vector3 c0 = MB.U(cx, cy, 0);
            for (int i = 0; i < N; i++)
            {
                float yN = 1 - 2 * (i + 0.5f) / N, r = Mathf.Sqrt(1 - yN * yN), th = i * 2.39996f;
                float dx = r * Mathf.Cos(th), dz = r * Mathf.Sin(th), dy = yN;      // dy up
                if (dx < -0.78f && Mathf.Abs(dy) < 0.55f) continue;
                if (dx > 0.85f) continue;
                float edx = (75 - cx) / rx, edy = (cy - 24) / ry, edz = Mathf.Sqrt(Mathf.Max(0, 1 - edx * edx - edy * edy));
                if ((dx - edx) * (dx - edx) + (dy - edy) * (dy - edy) + (Mathf.Abs(dz) - edz) * (Mathf.Abs(dz) - edz) < 0.12f) continue;
                Vector3 B = c0 + new Vector3(dx * rx, dy * ry, dz * rz) / 128f;
                Vector3 nrm = new Vector3(dx / rx, dy / ry, dz / rz).normalized;
                Vector3 t1 = Vector3.Cross(nrm, Vector3.up); if (t1.sqrMagnitude < 0.01f) t1 = Vector3.Cross(nrm, Vector3.right); t1.Normalize();
                Vector3 t2 = Vector3.Cross(nrm, t1);
                float bx = cx + dx * rx;
                int tip = m.AddU(B + nrm * (5.5f / 128f), PalUV(PSpikeTip), -1, bx);
                var bs = new int[3];
                for (int k = 0; k < 3; k++) { float a = 2f * Mathf.PI * k / 3; bs[k] = m.AddU(B - nrm * (0.8f / 128f) + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * (1.5f / 128f), PalUV(PSpikeBase), -1, bx); }
                for (int k = 0; k < 3; k++) m.TriOut(tip, bs[k], bs[(k + 1) % 3], B - nrm * (2f / 128f), 0);
            }
            {
                float ex = 75, ey = 24, ez = rz * Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow((ex - cx) / rx, 2) - Mathf.Pow((ey - cy) / ry, 2)));
                EyeDome(m, ex, ey, 7.2f, ez - 0.6f, 2.6f, -1);
            }
            bones = new[] { 78f, 60, 45 };
        }
        else   // Anglerfish
        {
            Func<float, float, float, float, float, float, float> ell = (x, y, ecx, ecy, erx, ery) =>
            { float nx = (x - ecx) / erx, ny = (y - ecy) / ery; return (Mathf.Sqrt(nx * nx + ny * ny) - 1) * Mathf.Min(erx, ery); };
            Func<float, float, float> D = (x, y) => Mathf.Min(Mathf.Min(ell(x, y, 70, 36, 32, 21), ell(x, y, 40, 37, 24, 13)), ell(x, y, 92, 45, 19, 7));
            Func<float, Vector2> sil = x =>
            {
                float top = 99, bot = -99;
                for (float y = 10; y <= 62; y += 0.25f) if (D(x, y) < 0) { if (y < top) top = y; if (y > bot) bot = y; }
                return top > bot ? new Vector2(37, 37) : new Vector2(top, bot);
            };
            Func<float, float, float> hw = (x, h) => h * (x > 98 ? 0.62f : x < 40 ? 0.6f + 0.2f * Mathf.Clamp01((x - 16) / 24f) : 0.8f);
            Loft(m, 16, 111, 32, 18, sil, hw, 1f, 0);
            var up = new List<Vector3>(); var lo = new List<Vector3>();
            for (int i = 0; i <= 8; i++)
            {
                float x = 6 + 18f * i / 8f, k = Mathf.Clamp01((22 - x) / 16f), spread = Mathf.Lerp(5, 13, Mathf.Pow(k, 0.8f)) * 0.97f;
                up.Add(new Vector3(x, 37, 37 - spread)); lo.Add(new Vector3(x, 37, 37 + spread));
            }
            FinStrip(m, up, Flat, -1); FinStrip(m, lo, Flat, -1);
            var dcols = new List<Vector3>();
            for (int i = 0; i <= 8; i++)
            {
                float x = 34 + 18f * i / 8f, top = 37 - 13 * Mathf.Sqrt(Mathf.Max(0, 1 - Mathf.Pow((x - 40) / 24f, 2))), h = 6 * Mathf.Sin((x - 34) / 18f * Mathf.PI);
                dcols.Add(new Vector3(x, top + 1, top - Mathf.Max(0.3f, h)));
            }
            FinStrip(m, dcols, Flat, -1);
            for (int side = -1; side <= 1; side += 2)
            {
                float sd = side, rootX = 64, rootY = 44, splay = 30f * Mathf.Deg2Rad;
                Vector2 tb = sil(rootX); float hh = (tb.y - tb.x) / 2, mid = (tb.x + tb.y) / 2, q = (rootY - mid) / hh;
                float z0 = hw(rootX, hh) * Mathf.Sqrt(Mathf.Max(0, 1 - q * q)) - 0.3f;
                Func<float, float, Vector3> pos = (x, y) => { float dd = rootX - x; return new Vector3(rootX - dd * Mathf.Cos(splay), y, sd * (z0 + Mathf.Max(0, dd) * Mathf.Sin(splay))); };
                FinDisc(m, 58, 44, 7, 3.5f, 1, 0, pos, side < 0 ? BonePecL : BonePecR, 12);
                if (side < 0) pecL = new Vector3(rootX, rootY, -z0); else pecR = new Vector3(rootX, rootY, z0);
            }
            {
                Vector2 tb = sil(84); float hh = (tb.y - tb.x) / 2, mid = (tb.x + tb.y) / 2, q = (25 - mid) / hh;
                EyeDome(m, 84, 25, 3.1f, hw(84, hh) * Mathf.Sqrt(Mathf.Max(0, 1 - q * q)) - 0.3f, 1.4f, -1);
            }
            // illicium + glowing lure (submesh 1 = glow material, vertex colours)
            var pts = new List<Vector3>();
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f, u = 1 - t;
                pts.Add(new Vector3(u * u * 74 + 2 * u * t * 86 + t * t * 106, u * u * 17 + 2 * u * t * -4 + t * t * 6, 0));
            }
            Tube(m, pts, 1.2f, 0.6f, 5, PalUV(PStalk), -1, 0);
            Vector3 bulb = MB.U(106, 8, 0);
            Sphere(m, bulb, 3.4f / 128f, 6, 10, PalUV(PBulb), new Color(0.85f, 1f, 1f, 1f), true, -1, 106, 1);
            Sphere(m, bulb, 6.5f / 128f, 6, 10, PalUV(PBulb), new Color(0.3f, 0.95f, 1f, 0.28f), true, -1, 106, 1);
            md.lurePos = bulb; md.hasLure = true;
            bones = new[] { 90f, 66, 42, 21 };
        }
        md.v = m.v; md.uv = m.uv; md.tri = m.tri; md.col = m.col; md.fixedCol = m.fixedCol; md.bone = m.bone; md.spineX = m.spineX;
        md.bones = bones; md.pecL = pecL; md.pecR = pecR;
        return md;
    }

    static Model Finish(string sp, Model md, MB m, float[] bones, int kind, Vector3 pecL, Vector3 pecR, float t0)
    {
        // ---- bones: spine chain + 2 pectoral bones (children of bone 0)
        int nb = bones.Length;
        md.chain = nb;
        md.bonePos = new Vector3[nb + 2];
        for (int i = 0; i < nb; i++) md.bonePos[i] = MB.U(bones[i], kind == 2 ? 40 : kind == 3 ? 33 : kind == 4 ? 37 : 32, 0);
        md.bonePos[nb] = MB.U(pecL.x, pecL.y, pecL.z);
        md.bonePos[nb + 1] = MB.U(pecR.x, pecR.y, pecR.z);
        var bw = new BoneWeight[m.v.Count];
        for (int i = 0; i < m.v.Count; i++)
        {
            int b = m.bone[i];
            if (b == BonePecL) { bw[i].boneIndex0 = nb; bw[i].weight0 = 1; continue; }
            if (b == BonePecR) { bw[i].boneIndex0 = nb + 1; bw[i].weight0 = 1; continue; }
            float x = m.spineX[i];
            int own = 0;
            for (int j = 0; j < nb; j++) if (bones[j] >= x || j == 0) own = j; else break;
            if (x < bones[nb - 1]) own = nb - 1;
            // blend across the joint nearest to x
            int bi0 = own, bi1 = own; float w0 = 1;
            for (int j = 1; j < nb; j++)
            {
                float zone = 0.35f * (bones[j - 1] - bones[j]);
                float d = x - bones[j];
                if (Mathf.Abs(d) < zone) { bi0 = j - 1; bi1 = j; w0 = 0.5f + 0.5f * d / zone; break; }
            }
            bw[i].boneIndex0 = bi0; bw[i].weight0 = w0; bw[i].boneIndex1 = bi1; bw[i].weight1 = 1 - w0;
            if (bi0 == bi1) { bw[i].weight0 = 1; bw[i].weight1 = 0; }
        }
        var bind = new Matrix4x4[nb + 2];
        for (int i = 0; i < nb + 2; i++) bind[i] = Matrix4x4.TRS(-md.bonePos[i], Quaternion.identity, Vector3.one);

        var mesh = new Mesh(); mesh.name = "Fish3D_" + sp;
        mesh.SetVertices(m.v);
        mesh.SetUVs(0, m.uv);
        bool glow = m.tri[1].Count > 0;
        mesh.subMeshCount = glow ? 2 : 1;
        mesh.SetTriangles(m.tri[0], 0);
        if (glow) mesh.SetTriangles(m.tri[1], 1);
        mesh.boneWeights = bw;
        mesh.bindposes = bind;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        // baked soft light in vertex colours (used by the Sprites/Default materials: Ghost Fish, fallbacks)
        Vector3[] nr = mesh.normals; var cols = new Color[m.v.Count];
        Vector3 L = new Vector3(0.25f, 1f, 0.35f).normalized;
        for (int i = 0; i < cols.Length; i++)
        {
            if (m.fixedCol[i]) { cols[i] = m.col[i]; continue; }
            float k = 0.62f + 0.45f * Mathf.Max(0, Vector3.Dot(nr[i], L)) - 0.12f * Mathf.Max(0, -nr[i].y);
            cols[i] = new Color(k, k, k, 1);
        }
        mesh.colors = cols;
        mesh.UploadMeshData(false);
        md.mesh = mesh;
        Bounds bb = mesh.bounds; bb.center -= md.bonePos[0]; bb.Expand(0.35f);
        md.boundsRel = bb;

        // ---- materials
        Texture2D tex = MakeTexture(sp);
        bool ghost = sp == "Ghost Fish";
        Material lit, under;
        if (ghost)
        {
            lit = new Material(spriteShader); lit.mainTexture = tex; lit.color = new Color(0.9f, 1f, 1f, 0.85f); lit.renderQueue = 3100;
            under = new Material(spriteShader); under.mainTexture = tex; under.color = new Color(0.75f, 1f, 1f, 0.6f); under.renderQueue = 3050;
        }
        else
        {
            lit = new Material(litShader); SetMain(lit, tex, Color.white);
            if (lit.shader == spriteShader) lit.renderQueue = 3100;
            under = new Material(underShader); SetMain(under, tex, new Color(0.62f, 0.8f, 0.92f, 1f)); under.renderQueue = 3050;
            if (under.HasProperty("_Glossiness")) under.SetFloat("_Glossiness", 0.35f);
        }
        lit.name = "Fish3D_" + sp; under.name = "Fish3D_" + sp + "_under";
        if (glow)
        {
            var g = new Material(spriteShader); g.mainTexture = whiteTex; g.color = Color.white; g.renderQueue = 3110; g.name = "Fish3D_glow";
            var gu = new Material(spriteShader); gu.mainTexture = whiteTex; gu.color = new Color(0.9f, 1f, 1f, 1f); gu.renderQueue = 3060; gu.name = "Fish3D_glow_under";
            md.mats = new[] { lit, g }; md.matsUnder = new[] { under, gu };
        }
        else { md.mats = new[] { lit }; md.matsUnder = new[] { under }; }
        modelsBuilt++;
        Log("Fish3D built " + sp + ": " + m.v.Count + " verts, " + (m.tri[0].Count + m.tri[1].Count) / 3 + " tris, " + (nb + 2) + " bones, "
            + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms");
        return md;
    }

    static void SetMain(Material mt, Texture2D tex, Color c)
    {
        mt.mainTexture = tex;
        if (mt.HasProperty("_MainTex")) mt.SetTexture("_MainTex", tex);
        if (mt.HasProperty("_Color")) mt.SetColor("_Color", c);
        if (litIsHopoo && mt.shader == litShader)
        {
            mt.shaderKeywords = new string[0];
            if (mt.HasProperty("_Smoothness")) mt.SetFloat("_Smoothness", 0.35f);
            if (mt.HasProperty("_SpecularStrength")) mt.SetFloat("_SpecularStrength", 0.25f);
            if (mt.HasProperty("_SpecularExponent")) mt.SetFloat("_SpecularExponent", 8f);
        }
    }

    // ------------------------------------------------------------------------------------------------ instances
    public static GameObject Create(string species, float length)
    {
        Model md = Get(species);
        if (md == null) return null;
        var go = new GameObject("Fish3D_" + species);
        var rig = go.AddComponent<Fish3DRig>();
        rig.md = md; rig.kind = md.kind;
        int nb = md.chain;
        var bones = new Transform[nb + 2];
        for (int i = 0; i < nb + 2; i++)
        {
            var b = new GameObject(i < nb ? "spine" + i : i == nb ? "pecL" : "pecR").transform;
            Transform parent = i == 0 ? go.transform : i < nb ? bones[i - 1] : bones[0];
            Vector3 ppos = i == 0 ? Vector3.zero : i < nb ? md.bonePos[i - 1] : md.bonePos[0];
            b.SetParent(parent, false);
            b.localPosition = md.bonePos[i] - ppos;
            bones[i] = b;
        }
        rig.chain = new Transform[nb]; Array.Copy(bones, rig.chain, nb);
        rig.pecL = bones[nb]; rig.pecR = bones[nb + 1];
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = md.mesh;
        smr.bones = bones;
        smr.rootBone = bones[0];
        smr.sharedMaterials = md.mats;
        smr.localBounds = md.boundsRel;
        smr.updateWhenOffscreen = false;
        smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        smr.receiveShadows = false;
        rig.smr = smr;
        rig.phase = UnityEngine.Random.value * 20f;
        if (md.hasLure)
        {
            try
            {
                var lo = new GameObject("Fish3DLure");
                lo.transform.SetParent(bones[0], false);
                lo.transform.localPosition = md.lurePos - md.bonePos[0];
                var lt = lo.AddComponent<Light>();
                lt.type = LightType.Point; lt.color = new Color(0.3f, 0.95f, 1f); lt.range = 2.5f; lt.intensity = 2f; lt.shadows = LightShadows.None;
                rig.light = lt;
            }
            catch (Exception e) { Log("Fish3D lure light error: " + e.Message); }
        }
        go.transform.localScale = new Vector3(length, length, length);
        return go;
    }

    public static void SetUnderwater(GameObject fish, bool under)
    {
        if (fish == null) return;
        Fish3DRig r = fish.GetComponent<Fish3DRig>();
        if (r == null || r.smr == null || r.under == under) return;
        r.under = under;
        r.smr.sharedMaterials = under ? r.md.matsUnder : r.md.mats;
    }

    public static void Animate(GameObject fish, float time, float swimSpeed)
    {
        if (fish == null) return;
        Fish3DRig r = fish.GetComponent<Fish3DRig>();
        if (r == null || r.chain == null) return;
        float t = time + r.phase, s = Mathf.Clamp(swimSpeed, 0f, 3f);
        float amp = 0.55f + 0.35f * Mathf.Min(s, 2f);
        Transform[] c = r.chain;
        switch (r.kind)
        {
            case 1:   // eel: travelling wave down the whole body
                {
                    float w = 2f * Mathf.PI * (0.55f + 0.45f * s);
                    for (int i = 0; i < c.Length; i++)
                        c[i].localRotation = Quaternion.Euler(0, (i == 0 ? 5f : 15f) * amp * Mathf.Sin(w * t - 1.05f * i), 0);
                    break;
                }
            case 2:   // boot: slow tumble
                c[0].localRotation = Quaternion.Euler(9f * Mathf.Sin(0.7f * t), 6f * Mathf.Sin(0.43f * t), 7f * Mathf.Sin(0.55f * t + 1f));
                break;
            default:
                {
                    float w = 2f * Mathf.PI * (r.kind == 3 ? 2.2f + 1.2f * s : 1.3f + 1.2f * s);
                    float[] A = r.kind == 3 ? PufferAmp : FishAmp;
                    for (int i = 0; i < c.Length && i < A.Length; i++)
                        c[i].localRotation = Quaternion.Euler(0, A[i] * amp * Mathf.Sin(w * t - 0.95f * i), 0);
                    break;
                }
        }
        if (r.pecL != null && r.kind != 1 && r.kind != 2)
        {
            float f = (r.kind == 3 ? 28f : 16f) * Mathf.Sin(2f * Mathf.PI * (r.kind == 3 ? 3.2f : 1.6f) * t + 0.5f);
            r.pecL.localRotation = Quaternion.Euler(0, f, 0);
            r.pecR.localRotation = Quaternion.Euler(0, -f, 0);
        }
        if (r.light != null) r.light.intensity = 1.7f + 0.5f * Mathf.Sin(3.1f * t) + 0.2f * Mathf.Sin(11f * t);
    }
    static readonly float[] FishAmp = { -3f, 6f, 10f, 20f };
    static readonly float[] PufferAmp = { -2f, 3f, 24f };
}

// per-instance data for Fish3D.Animate / SetUnderwater (no Update of its own)
public class Fish3DRig : MonoBehaviour
{
    internal Fish3D.Model md;
    public Transform[] chain; public Transform pecL, pecR; public Light light; public SkinnedMeshRenderer smr;
    public int kind; public float phase; public bool under;
}
