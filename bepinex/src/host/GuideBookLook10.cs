// GuideBookLook10 - the PHYSICAL fishing guide book (UnityEngine only; host add-on GuideBook<N> and FISHING.exe share it).
//  * Pages (GuideData10 GPage10 lists) are laid out with TextMesh text + image quads in a hidden "studio" 20 km below the map
//    and rendered by a private orthographic camera into one RenderTexture per page (main thread, when the book opens).
//  * The book is a 3D object parented to the main camera: leather covers, spine, page blocks, left/right page quads
//    (Standard shader, page texture as emission so it reads in the dark), and a bendable leaf mesh for page turns.
//  * Open(): rises from the bottom of the screen, cover swings open. Next()/Prev(): animated page turn around the spine
//    (the leaf curls while it turns). Close(): cover shuts, book lowers, everything is destroyed.
//  * Sounds go through GuideBookLook10.play("open" | "flip" | "close"); the owner maps them to game (Wwise) sounds.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class GuideBookLook10
{
    public static Action<string> log, play;
    public static bool IsOpen { get { return state != 0; } }
    public static int Spread { get { return spread; } }
    public static int SpreadCount { get { return (pageTex.Count + 1) / 2; } }
    public static float distance = 0.62f, screenFill = 0.7f;     // tuning
    public static int texW = 640, texH = 889;

    const float PW = 0.72f, PH = 1f;          // studio page size (units)
    static readonly Color Paper = new Color(0.93f, 0.87f, 0.72f), Leather = new Color(0.36f, 0.12f, 0.07f);
    static readonly List<RenderTexture> pageTex = new List<RenderTexture>();
    static RenderTexture coverTex, endTex;
    static GameObject book; static Transform holder, coverPivot, leaf;
    static GameObject leftBlock;
    static Renderer leftR, rightR; static MeshFilter leafFront, leafBack; static Material leftM, rightM, frontM, backM, coverM, endM;
    static readonly List<Material> mats = new List<Material>();
    static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    static float W, H;
    static int state;          // 0 closed/hidden, 1 rising, 2 cover opening, 3 open, 4 flipping, 5 cover closing, 6 lowering
    static float t0, flipDir; static int spread, flipTarget;
    static Shader sprite;

    static void L(string s) { if (log != null) try { log(s); } catch { } }
    static void Snd(string s) { if (play != null) try { play(s); } catch { } }
    static float Ease(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // ================================================================== page rendering (studio)
    static Font font; static float kAdv = -1f, lineAt1 = -1f; const int FS = 48;
    static Transform studio; static Camera studioCam;

    static Font GetFont()
    {
        if (font != null) return font;
        try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        if (font == null) try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        return font;
    }

    static TextMesh MakeText(Transform parent, string s, Vector3 pos, float cs, Color c, FontStyle st, TextAnchor anchor)
    {
        var go = new GameObject("t"); go.transform.SetParent(parent, false); go.transform.localPosition = pos;
        var tm = go.AddComponent<TextMesh>();
        Font f = GetFont(); if (f != null) { tm.font = f; go.GetComponent<MeshRenderer>().sharedMaterial = f.material; }
        tm.fontSize = FS; tm.characterSize = cs; tm.richText = true; tm.fontStyle = st; tm.color = c; tm.anchor = anchor;
        tm.alignment = anchor == TextAnchor.UpperCenter ? TextAlignment.Center : anchor == TextAnchor.UpperRight ? TextAlignment.Right : TextAlignment.Left;
        tm.text = s;
        return tm;
    }

    static void Calibrate()
    {
        if (kAdv > 0) return;
        var tmp = new GameObject("calib").transform; tmp.position = new Vector3(0f, -20000f, 50f);
        try
        {
            var a = MakeText(tmp, "MMMMMMMMMM", Vector3.zero, 1f, Color.black, FontStyle.Normal, TextAnchor.UpperLeft);
            float wM = a.GetComponent<Renderer>().bounds.size.x;
            float adv = Adv("MMMMMMMMMM", FontStyle.Normal);
            kAdv = adv > 0 && wM > 0 ? wM / adv : 0.1f / FS;
            var b = MakeText(tmp, "A", Vector3.zero, 1f, Color.black, FontStyle.Normal, TextAnchor.UpperLeft);
            var c = MakeText(tmp, "A\nA", Vector3.zero, 1f, Color.black, FontStyle.Normal, TextAnchor.UpperLeft);
            lineAt1 = c.GetComponent<Renderer>().bounds.size.y - b.GetComponent<Renderer>().bounds.size.y;
            if (lineAt1 <= 0) lineAt1 = FS * 0.1f * 1.15f / 10f;
            L("text calibration: width/adv " + kAdv.ToString("0.00000") + ", line at cs=1 " + lineAt1.ToString("0.0000") + ", M width " + wM.ToString("0.000"));
        }
        finally { UnityEngine.Object.DestroyImmediate(tmp.gameObject); }
    }

    static float Adv(string s, FontStyle st)
    {
        Font f = GetFont(); if (f == null) return s.Length * FS * 0.55f;
        f.RequestCharactersInTexture(s, FS, st);
        float w = 0; CharacterInfo ci;
        foreach (char ch in s) { if (f.GetCharacterInfo(ch, out ci, FS, st)) w += ci.advance; else w += FS * 0.5f; }
        return w;
    }
    static string Strip(string s) { return Regex.Replace(s, "<[^>]*>", ""); }
    static float Width(string s, float cs, FontStyle st) { return Adv(Strip(s), st) * kAdv * cs; }
    static float CsFor(float lineH) { return lineH / lineAt1; }

    // word wrap (tags are kept inside words; a tag span crossing a line break is re-opened on the next line)
    static List<string> Wrap(string text, float cs, float maxW, FontStyle st)
    {
        var lines = new List<string>();
        foreach (string para in text.Split('\n'))
        {
            string[] words = para.Split(' ');
            var cur = new StringBuilder();
            foreach (string w in words)
            {
                string trial = cur.Length == 0 ? w : cur + " " + w;
                if (cur.Length > 0 && Width(trial, cs, st) > maxW) { lines.Add(cur.ToString()); cur.Length = 0; cur.Append(w); }
                else { cur.Length = 0; cur.Append(trial); }
            }
            lines.Add(cur.ToString());
        }
        // balance <color>/<b> tags per line
        for (int i = 0; i < lines.Count; i++)
        {
            var open = new List<string>();
            foreach (Match m in Regex.Matches(lines[i], "<(/?)(b|i|color)(=[^>]*)?>"))
            { if (m.Groups[1].Value == "") open.Add(m.Value); else if (open.Count > 0) open.RemoveAt(open.Count - 1); }
            if (open.Count == 0 || i + 1 >= lines.Count) continue;
            var close = new StringBuilder(); for (int k = open.Count - 1; k >= 0; k--) close.Append("</" + Regex.Match(open[k], "<(b|i|color)").Groups[1].Value + ">");
            lines[i] += close.ToString(); lines[i + 1] = string.Join("", open.ToArray()) + lines[i + 1];
        }
        return lines;
    }

    static string Hex(Color c) { return "#" + ColorUtility.ToHtmlStringRGB(c); }

    // ---- layout model: pages flow into "sheets" (what one page texture shows)
    class Item { public string text; public float x, y, cs; public Color c; public FontStyle st; public TextAnchor a = TextAnchor.UpperLeft; public Texture2D img; public float w, h; public bool rule; }
    class Sheet { public string title; public readonly List<Item> items = new List<Item>(); public float y; public int number; }

    const float M = 0.055f, Top = 0.06f, Bottom = 0.07f;
    static float Y0 { get { return PH / 2f - Top; } }
    static float YMin { get { return -PH / 2f + Bottom; } }
    static float CW { get { return PW - 2 * M; } }

    static Sheet NewSheet(List<Sheet> sheets, string title, bool cont)
    {
        var s = new Sheet { title = title }; sheets.Add(s);
        s.y = Y0;
        if (!string.IsNullOrEmpty(title))
        {
            float cs = CsFor(0.05f);
            s.items.Add(new Item { text = "<b>" + title + (cont ? " (cont.)" : "") + "</b>", x = 0f, y = s.y, cs = cs, c = GuideData10.Red, st = FontStyle.Normal, a = TextAnchor.UpperCenter });
            s.y -= 0.058f;
            s.items.Add(new Item { rule = true, x = -CW / 2f, y = s.y, w = CW, h = 0.004f, c = GuideData10.Faint });
            s.y -= 0.022f;
        }
        return s;
    }

    static List<Sheet> Layout(List<GPage10> pages)
    {
        Calibrate();
        var sheets = new List<Sheet>();
        foreach (GPage10 p in pages)
        {
            Sheet sh = NewSheet(sheets, p.title, false);
            foreach (GBlock10 b in p.blocks)
            {
                float lh; FontStyle st = FontStyle.Normal; float indent = 0f; string prefix = "";
                switch (b.kind)
                {
                    case GBlock10.Space: sh.y -= 0.018f; continue;
                    case GBlock10.Heading: lh = 0.036f; st = FontStyle.Bold; if (sh.y < Y0 - 0.1f) sh.y -= 0.01f; if (sh.y - 0.16f < YMin) sh = NewSheet(sheets, p.title, true); break;
                    case GBlock10.Small: lh = 0.0235f; st = FontStyle.Italic; break;
                    case GBlock10.Bullet: lh = 0.027f; indent = 0.03f; prefix = "•"; break;
                    case GBlock10.Image: lh = 0f; break;
                    case GBlock10.Table: lh = 0.024f; break;
                    default: lh = 0.0285f; break;
                }
                float cs = lh > 0 ? CsFor(lh / 1.12f) : 0f;
                if (b.kind == GBlock10.Image)
                {
                    float h = b.imgH * PH, w = Mathf.Min(CW, h * (b.img != null ? (float)b.img.width / b.img.height : 1f));
                    if (sh.y - h < YMin) sh = NewSheet(sheets, p.title, true);
                    sh.items.Add(new Item { img = b.img, x = -w / 2f, y = sh.y, w = w, h = h });
                    sh.y -= h + 0.01f;
                    if (!string.IsNullOrEmpty(b.caption))
                    {
                        float ccs = CsFor(0.021f);
                        foreach (string l in Wrap(b.caption, ccs, CW, FontStyle.Italic)) { sh.items.Add(new Item { text = l, x = -CW / 2f, y = sh.y, cs = ccs, c = GuideData10.Faint, st = FontStyle.Italic }); sh.y -= 0.024f; }
                    }
                    sh.y -= 0.01f;
                    continue;
                }
                if (b.kind == GBlock10.Table)
                {
                    float[] cols = b.cols; float tot = 0; foreach (float c in cols) tot += c;
                    foreach (string[] row in b.rows)
                    {
                        var cellLines = new List<List<string>>(); int n = 1;
                        for (int i = 0; i < cols.Length; i++)
                        {
                            float cw = CW * cols[i] / tot - 0.008f;
                            var wl = Wrap(i < row.Length ? row[i] ?? "" : "", cs, cw, FontStyle.Normal);
                            cellLines.Add(wl); n = Math.Max(n, wl.Count);
                        }
                        float rh = n * lh + 0.006f;
                        if (sh.y - rh < YMin) sh = NewSheet(sheets, p.title, true);
                        float x = -CW / 2f;
                        for (int i = 0; i < cols.Length; i++)
                        {
                            for (int k = 0; k < cellLines[i].Count; k++)
                                sh.items.Add(new Item { text = cellLines[i][k], x = x, y = sh.y - k * lh, cs = cs, c = b.color, st = FontStyle.Normal });
                            x += CW * cols[i] / tot;
                        }
                        sh.y -= rh;
                        sh.items.Add(new Item { rule = true, x = -CW / 2f, y = sh.y + 0.004f, w = CW, h = 0.0015f, c = new Color(0.6f, 0.5f, 0.36f) });
                    }
                    sh.y -= 0.012f;
                    continue;
                }
                float maxW = CW - indent;
                var lines = Wrap(b.text ?? "", cs, maxW, st);
                for (int k = 0; k < lines.Count; k++)
                {
                    if (sh.y - lh < YMin) sh = NewSheet(sheets, p.title, true);
                    if (k == 0 && prefix.Length > 0) sh.items.Add(new Item { text = prefix, x = -CW / 2f + 0.008f, y = sh.y, cs = cs, c = GuideData10.Red, st = FontStyle.Bold });
                    sh.items.Add(new Item { text = lines[k], x = -CW / 2f + indent, y = sh.y, cs = cs, c = b.color, st = st });
                    sh.y -= lh;
                }
                sh.y -= b.kind == GBlock10.Heading ? 0.004f : 0.009f;
            }
        }
        for (int i = 0; i < sheets.Count; i++) sheets[i].number = i + 1;
        return sheets;
    }

    // procedural textures
    static Texture2D paperTex, leatherTex;
    static Texture2D Noise(Color baseC, float amount, float vignette, int seed)
    {
        var t = new Texture2D(128, 178, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
        var r = new System.Random(seed); var px = new Color[t.width * t.height];
        for (int y = 0; y < t.height; y++)
            for (int x = 0; x < t.width; x++)
            {
                float u = x / (float)(t.width - 1) - 0.5f, v = y / (float)(t.height - 1) - 0.5f;
                float n = (float)r.NextDouble() - 0.5f, fib = Mathf.Sin(y * 0.9f + x * 0.13f) * 0.3f;
                float vg = 1f - vignette * Mathf.Clamp01((u * u + v * v) * 2.2f);
                Color c = baseC * (vg + amount * (n + fib * 0.5f)); c.a = 1f; px[y * t.width + x] = c;
            }
        t.SetPixels(px); t.Apply(); owned.Add(t);
        return t;
    }

    static Material SpriteMat(Texture tex, Color c)
    {
        if (sprite == null) sprite = Shader.Find("Sprites/Default");
        var m = new Material(sprite); m.mainTexture = tex; m.color = c; owned.Add(m); return m;
    }

    static GameObject Quad(Transform parent, Vector3 center, float w, float h, Material m)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Quad);
        foreach (var comp in g.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(comp);
        g.transform.SetParent(parent, false); g.transform.localPosition = center; g.transform.localScale = new Vector3(w, h, 1f);
        var r = g.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return g;
    }

    static void EnsureStudio()
    {
        if (studio != null) return;
        var s = new GameObject("GuideBookStudio"); studio = s.transform; studio.position = new Vector3(0f, -20000f, 0f);
        var cg = new GameObject("GuideBookStudioCam"); cg.transform.SetParent(studio, false); cg.transform.localPosition = new Vector3(0f, 0f, -1f);
        studioCam = cg.AddComponent<Camera>(); studioCam.enabled = false; studioCam.orthographic = true; studioCam.orthographicSize = PH / 2f;
        studioCam.nearClipPlane = 0.05f; studioCam.farClipPlane = 2f; studioCam.clearFlags = CameraClearFlags.SolidColor; studioCam.backgroundColor = Paper;
        studioCam.allowHDR = false; studioCam.allowMSAA = false; studioCam.useOcclusionCulling = false;
        owned.Add(s);
    }

    static RenderTexture RenderSheet(Sheet sh, bool cover, string coverTitle, string coverSub)
    {
        EnsureStudio();
        var page = new GameObject("page").transform; page.SetParent(studio, false);
        try
        {
            if (paperTex == null) paperTex = Noise(Paper, 0.05f, 0.25f, 7);
            if (leatherTex == null) leatherTex = Noise(Leather, 0.18f, 0.45f, 11);
            Quad(page, new Vector3(0f, 0f, 0.5f), PW, PH, SpriteMat(cover ? leatherTex : paperTex, Color.white));
            if (cover)
            {
                Color gold = new Color(1f, 0.8f, 0.3f);
                var frame = SpriteMat(Texture2D.whiteTexture, gold);
                foreach (var fr in new[] { new Vector4(0f, 0.43f, PW - 0.1f, 0.006f), new Vector4(0f, -0.43f, PW - 0.1f, 0.006f), new Vector4(-PW / 2f + 0.05f, 0f, 0.006f, 0.86f), new Vector4(PW / 2f - 0.05f, 0f, 0.006f, 0.86f) })
                    Quad(page, new Vector3(fr.x, fr.y, 0.4f), fr.z, fr.w, frame);
                float cs = CsFor(0.075f);
                MakeText(page, "<b>Angler's</b>", new Vector3(0f, 0.27f, 0f), cs, gold, FontStyle.Normal, TextAnchor.UpperCenter);
                MakeText(page, "<b>Guide</b>", new Vector3(0f, 0.18f, 0f), cs * 1.3f, gold, FontStyle.Normal, TextAnchor.UpperCenter);
                var fish = SpriteMat(Texture2D.whiteTexture, gold);
                var body = Quad(page, new Vector3(0f, -0.05f, 0.3f), 0.26f, 0.09f, fish); body.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
                var tail = Quad(page, new Vector3(0.16f, -0.05f, 0.3f), 0.07f, 0.07f, fish); tail.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                var eye = Quad(page, new Vector3(-0.08f, -0.035f, 0.2f), 0.018f, 0.018f, SpriteMat(Texture2D.whiteTexture, Leather));
                MakeText(page, coverTitle ?? "", new Vector3(0f, -0.2f, 0f), CsFor(0.04f), gold, FontStyle.Italic, TextAnchor.UpperCenter);
                MakeText(page, coverSub ?? "", new Vector3(0f, -0.3f, 0f), CsFor(0.025f), new Color(0.9f, 0.7f, 0.35f), FontStyle.Normal, TextAnchor.UpperCenter);
                eye.name = "eye";
            }
            else if (sh != null)
            {
                foreach (Item it in sh.items)
                {
                    if (it.rule) { Quad(page, new Vector3(it.x + it.w / 2f, it.y, 0.4f), it.w, it.h, SpriteMat(Texture2D.whiteTexture, it.c)); continue; }
                    if (it.img != null) { Quad(page, new Vector3(it.x + it.w / 2f, it.y - it.h / 2f, 0.3f), it.w, it.h, SpriteMat(it.img, Color.white)); continue; }
                    MakeText(page, it.text, new Vector3(it.x, it.y, 0f), it.cs, it.c, it.st, it.a);
                }
                if (sh.number > 0) MakeText(page, "- " + sh.number + " -", new Vector3(0f, -PH / 2f + 0.05f, 0f), CsFor(0.02f), GuideData10.Faint, FontStyle.Italic, TextAnchor.UpperCenter);
            }
            var rt = new RenderTexture(texW, texH, 16, RenderTextureFormat.ARGB32); rt.Create(); owned.Add(rt);
            studioCam.targetTexture = rt;
            studioCam.Render();
            studioCam.targetTexture = null;
            return rt;
        }
        finally
        {
            foreach (var r in page.GetComponentsInChildren<Renderer>(true)) { var m = r.sharedMaterial; if (m != null && owned.Contains(m) && m.shader == sprite) { owned.Remove(m); UnityEngine.Object.DestroyImmediate(m); } }
            UnityEngine.Object.DestroyImmediate(page.gameObject);
        }
    }

    // builds every page texture (call on the main thread). Returns the number of page textures.
    public static int Prepare(List<GPage10> pages, string coverTitle, string coverSub)
    {
        foreach (var rt in pageTex) if (rt != null) { owned.Remove(rt); rt.Release(); UnityEngine.Object.Destroy(rt); }
        pageTex.Clear();
        var sheets = Layout(pages);
        foreach (var sh in sheets) pageTex.Add(RenderSheet(sh, false, null, null));
        if (coverTex == null) coverTex = RenderSheet(null, true, coverTitle, coverSub);
        if (endTex == null) { var s = new Sheet(); endTex = RenderSheet(s, false, null, null); }
        L("rendered " + pageTex.Count + " page textures (" + texW + "x" + texH + ")");
        return pageTex.Count;
    }

    // test helper: copy one page texture into a Texture2D (e.g. to save as PNG)
    public static Texture2D ReadPage(int i)
    {
        RenderTexture rt = i < 0 ? coverTex : i < pageTex.Count ? pageTex[i] : null; if (rt == null) return null;
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false); t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); t.Apply();
        RenderTexture.active = prev; return t;
    }

    // ================================================================== the 3D book
    // In this game build Standard's emission renders black, so the book uses UNLIT shaders: Unlit/Texture for the page
    // textures (opaque, back faces culled, writes depth) and Hidden/Internal-Colored for plain-coloured parts.
    static Shader unlitTex, flatCol;
    static Material Emit(Texture tex, Color c, float strength)
    {
        if (unlitTex == null) unlitTex = Shader.Find("Unlit/Texture");
        if (flatCol == null) flatCol = Shader.Find("Hidden/Internal-Colored");
        Material m;
        if (tex != null && unlitTex != null) { m = new Material(unlitTex); m.mainTexture = tex; }
        else
        {
            m = new Material(flatCol ?? unlitTex); Color k = c * strength; k.a = 1f; m.color = k;
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1);
            if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 4);
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", 2);
            if (tex != null) m.mainTexture = tex;
        }
        m.renderQueue = 2000;
        mats.Add(m); return m;
    }
    static void SetTex(Material m, Texture t) { if (m != null) m.mainTexture = t; }

    static GameObject Box(Transform parent, Vector3 c, Vector3 s, Material m)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        foreach (var comp in g.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(comp);
        g.transform.SetParent(parent, false); g.transform.localPosition = c; g.transform.localScale = s;
        var r = g.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return g;
    }

    const int Cols = 18;
    static MeshFilter LeafMesh(Transform parent, Material m, bool back)
    {
        var g = new GameObject(back ? "LeafBack" : "LeafFront"); g.transform.SetParent(parent, false);
        var mf = g.AddComponent<MeshFilter>(); var mr = g.AddComponent<MeshRenderer>(); mr.sharedMaterial = m;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        var mesh = new Mesh(); mesh.MarkDynamic(); owned.Add(mesh);
        var v = new Vector3[(Cols + 1) * 2]; var uv = new Vector2[v.Length]; var tri = new int[Cols * 6];
        for (int i = 0; i <= Cols; i++)
        {
            float u = i / (float)Cols;
            uv[i * 2] = new Vector2(back ? 1f - u : u, 0f); uv[i * 2 + 1] = new Vector2(back ? 1f - u : u, 1f);
        }
        for (int i = 0; i < Cols; i++)
        {
            int a = i * 2, b = a + 1, c = a + 3, d = a + 2, k = i * 6;   // a=(i,bottom) b=(i,top) c=(i+1,top) d=(i+1,bottom)
            if (!back) { tri[k] = a; tri[k + 1] = b; tri[k + 2] = c; tri[k + 3] = a; tri[k + 4] = c; tri[k + 5] = d; }
            else { tri[k] = a; tri[k + 1] = c; tri[k + 2] = b; tri[k + 3] = a; tri[k + 4] = d; tri[k + 5] = c; }
        }
        mesh.vertices = v; mesh.uv = uv; mesh.triangles = tri;
        mf.sharedMesh = mesh;
        return mf;
    }

    // theta 0 = lying flat on the right, PI = flat on the left; the free edge lags behind (curl)
    static void BendLeaf(float theta)
    {
        float kappa = 0.55f * Mathf.Sin(theta);
        var v = new Vector3[(Cols + 1) * 2];
        float x = 0f, z = 0f, du = W / Cols;
        for (int i = 0; i <= Cols; i++)
        {
            if (i > 0)
            {
                float uMid = (i - 0.5f) / Cols, phi = theta - kappa * uMid * flipDir;
                x += Mathf.Cos(phi) * du; z -= Mathf.Sin(phi) * du;
            }
            v[i * 2] = new Vector3(x, -H / 2f, z - 0.0025f); v[i * 2 + 1] = new Vector3(x, H / 2f, z - 0.0025f);
        }
        foreach (var mf in new[] { leafFront, leafBack })
        {
            var m = mf.sharedMesh; m.vertices = v; m.RecalculateNormals(); m.RecalculateBounds();
        }
    }

    static Texture PageAt(int i) { return i >= 0 && i < pageTex.Count ? (Texture)pageTex[i] : endTex; }

    public static bool Open(Camera cam, int startSpread)
    {
        if (cam == null || pageTex.Count == 0) return false;
        DestroyBook();
        float fov = cam.fieldOfView > 1f ? cam.fieldOfView : 60f;
        float d = Mathf.Max(distance, cam.nearClipPlane * 3f);
        float vis = 2f * d * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        H = vis * screenFill; W = H * PW / PH;
        book = new GameObject("FishingGuideBook");
        book.transform.SetParent(cam.transform, false);
        book.transform.localPosition = new Vector3(0f, 0f, d);
        holder = new GameObject("Holder").transform; holder.SetParent(book.transform, false);
        Material leather = Emit(null, Leather, 1f), edge = Emit(null, new Color(0.86f, 0.8f, 0.64f), 0.75f), endM2 = Emit(null, new Color(0.5f, 0.22f, 0.15f), 0.6f);
        float th = H * 0.012f;
        Box(holder, new Vector3(W / 2f + th * 0.3f, 0f, th * 2.4f), new Vector3(W + th * 0.8f, H + th * 1.6f, th), leather);      // back board
        Box(holder, new Vector3(0f, 0f, th * 2.4f), new Vector3(th * 3f, H + th * 1.6f, th * 1.4f), leather);                     // spine
        Box(holder, new Vector3(W / 2f, 0f, th * 1.1f), new Vector3(W * 0.985f, H * 0.985f, th * 1.6f), edge);                    // right page block
        leftBlock = Box(holder, new Vector3(-W / 2f, 0f, th * 1.1f), new Vector3(W * 0.985f, H * 0.985f, th * 1.6f), edge);       // left page block
        leftM = Emit(PageAt(0), Color.white, 0.82f); rightM = Emit(PageAt(1), Color.white, 0.82f);
        leftR = Quad(holder, new Vector3(-W / 2f, 0f, 0f), W, H, leftM).GetComponent<Renderer>();
        rightR = Quad(holder, new Vector3(W / 2f, 0f, 0f), W, H, rightM).GetComponent<Renderer>();
        frontM = Emit(PageAt(1), Color.white, 0.82f); backM = Emit(PageAt(2), Color.white, 0.82f);
        leaf = new GameObject("Leaf").transform; leaf.SetParent(holder, false);
        leafFront = LeafMesh(leaf, frontM, false); leafBack = LeafMesh(leaf, backM, true);
        leaf.gameObject.SetActive(false);
        // front cover: pivot on the spine; closed = over the right pages, open = rotated 180 deg to the left
        coverPivot = new GameObject("CoverPivot").transform; coverPivot.SetParent(holder, false); coverPivot.localPosition = new Vector3(0f, 0f, th * 1.1f);
        // rotating 180 deg about the spine maps local z -> -z: closed the board sits in front of the right pages,
        // open it lies behind the left page block
        Box(coverPivot, new Vector3(W / 2f + th * 0.3f, 0f, -th * 2.1f), new Vector3(W + th * 0.8f, H + th * 1.6f, th), leather);
        coverM = Emit(coverTex, Color.white, 0.85f);
        Quad(coverPivot, new Vector3(W / 2f + th * 0.3f, 0f, -th * 2.65f), W * 0.98f, H * 0.98f, coverM);
        endM = endM2;
        var inside = Quad(coverPivot, new Vector3(W / 2f, 0f, -th * 1.55f), W * 0.97f, H * 0.97f, endM2);
        inside.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        spread = Mathf.Clamp(startSpread, 0, Math.Max(0, SpreadCount - 1));
        ShowSpread();
        state = 1; t0 = Time.unscaledTime;
        Pose(0f, 0f);
        Snd("open");
        L("book opened: " + pageTex.Count + " pages, size " + W.ToString("0.000") + "x" + H.ToString("0.000") + " at " + d.ToString("0.00") + " m, fov " + fov.ToString("0") + ", near " + cam.nearClipPlane.ToString("0.00") + ", shader " + (unlitTex != null ? unlitTex.name : "none"));
        return true;
    }

    static void ShowSpread() { SetTex(leftM, PageAt(spread * 2)); SetTex(rightM, PageAt(spread * 2 + 1)); }

    // raise: 0 = below the screen, 1 = held up; cover: 0 closed, 1 open
    static void Pose(float raise, float cover)
    {
        float e = Ease(raise);
        holder.localPosition = new Vector3(0f, Mathf.Lerp(-H * 1.25f, -H * 0.04f, e), Mathf.Lerp(H * 0.25f, 0f, e));
        holder.localRotation = Quaternion.Euler(Mathf.Lerp(50f, 7f, e), Mathf.Lerp(-12f, 0f, e), Mathf.Lerp(8f, 0f, e));
        // closed book: shift so the closed cover sits in the middle of the screen
        float shift = Mathf.Lerp(-W / 2f, 0f, Ease(cover));
        holder.localPosition += new Vector3(shift, 0f, 0f);
        coverPivot.localRotation = Quaternion.Euler(0f, 180f * Ease(cover), 0f);
        // a closed book has no left half: the left pages appear once the cover has swung past upright
        bool leftOn = Ease(cover) > 0.5f;
        if (leftR != null && leftR.gameObject.activeSelf != leftOn) { leftR.gameObject.SetActive(leftOn); if (leftBlock != null) leftBlock.SetActive(leftOn); }
    }

    public static void Next() { StartFlip(+1); }
    public static void Prev() { StartFlip(-1); }
    static void StartFlip(int dir)
    {
        if (state != 3) return;
        int target = spread + dir;
        if (target < 0 || target >= SpreadCount) { Snd("bump"); return; }
        flipDir = dir; flipTarget = target; state = 4; t0 = Time.unscaledTime;
        if (dir > 0)
        {   // leaf = current right page (front) / next left page (back); next right page shows underneath at once
            SetTex(frontM, PageAt(spread * 2 + 1)); SetTex(backM, PageAt(target * 2));
            SetTex(rightM, PageAt(target * 2 + 1));
        }
        else
        {   // leaf starts on the left: back = current left, front = previous right
            SetTex(backM, PageAt(spread * 2)); SetTex(frontM, PageAt(target * 2 + 1));
            SetTex(leftM, PageAt(target * 2));
        }
        leaf.gameObject.SetActive(true);
        BendLeaf(dir > 0 ? 0f : Mathf.PI);
        Snd("flip");
    }

    public static void Close()
    {
        if (state == 0 || state >= 5) return;
        if (state == 4) { spread = flipTarget; leaf.gameObject.SetActive(false); ShowSpread(); }
        state = 5; t0 = Time.unscaledTime; Snd("close");
    }

    public static void Tick()
    {
        if (state == 0) return;
        try
        {
            if (book == null || holder == null) { state = 0; return; }
            float u = Time.unscaledTime - t0;
            switch (state)
            {
                case 1: Pose(u / 0.35f, 0f); if (u >= 0.35f) { state = 2; t0 = Time.unscaledTime; } break;
                case 2: Pose(1f, u / 0.5f); if (u >= 0.5f) { state = 3; Pose(1f, 1f); } break;
                case 3: break;
                case 4:
                    {
                        float k = Ease(u / 0.6f);
                        BendLeaf(flipDir > 0 ? Mathf.PI * k : Mathf.PI * (1f - k));
                        if (u >= 0.6f) { spread = flipTarget; leaf.gameObject.SetActive(false); ShowSpread(); state = 3; }
                        break;
                    }
                case 5: Pose(1f, 1f - u / 0.4f); if (u >= 0.4f) { state = 6; t0 = Time.unscaledTime; } break;
                case 6: Pose(1f - u / 0.3f, 0f); if (u >= 0.3f) { DestroyBook(); state = 0; } break;
            }
        }
        catch (Exception e) { L("book tick error: " + e.Message); DestroyBook(); state = 0; }
    }

    static void DestroyBook()
    {
        if (book != null) UnityEngine.Object.Destroy(book);
        book = null; holder = coverPivot = leaf = null; leftR = rightR = null; leafFront = leafBack = null;
        foreach (var m in mats) if (m != null) UnityEngine.Object.Destroy(m);
        mats.Clear();
    }


    // test only: render the 3D book from a temporary camera far below the map (nobody sees it). coverK 0 = closed,
    // 1 = open; flipK > 0 shows the leaf of a forward page turn at that progress. Returns a Texture2D (caller saves it).
    public static Texture2D TestShot(int spreadIdx, float coverK, float flipK, int w, int h)
    {
        var go = new GameObject("GuideBookTestCam"); go.transform.position = new Vector3(300f, -20000f, 0f);
        var cam = go.AddComponent<Camera>(); cam.enabled = false; cam.fieldOfView = 60f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 20f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.25f, 0.3f, 0.35f);
        var oldPlay = play; play = null;
        try
        {
            if (!Open(cam, spreadIdx)) return null;
            Pose(1f, coverK);
            if (flipK > 0f) { state = 3; StartFlip(+1); BendLeaf(Mathf.PI * flipK); }
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32); rt.Create();
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGB24, false); t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
            RenderTexture.active = prev; rt.Release(); UnityEngine.Object.Destroy(rt);
            return t;
        }
        finally { play = oldPlay; DestroyBook(); state = 0; UnityEngine.Object.DestroyImmediate(go); }
    }

    // drop everything (pages, studio, book)
    public static void DestroyAll()
    {
        DestroyBook(); state = 0;
        foreach (var rt in pageTex) if (rt != null) rt.Release();
        pageTex.Clear(); coverTex = null; endTex = null; paperTex = null; leatherTex = null;
        foreach (var o in owned) if (o != null) UnityEngine.Object.Destroy(o);
        owned.Clear(); studio = null; studioCam = null;
    }
}
