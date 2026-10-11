// RodStandLook - the rod shop market stall (UnityEngine only; shared by the host add-on RodStand<N> and FISHING.exe).
//   Build(pos, yaw, prices)  wooden counter, 4 posts, red/white striped awning, "ROD SHOP" sign, a rack of display rods in
//                            their real skins (StandSkinLook = the rod shop's own skin code) with bobbers on their lines, the
//                            three glowing bobbers on a little plinth, price tags, the guide book on the counter, a warm light
//                            and a teal outline shell that shows through walls (same x-ray trick as the roulette table).
//   Tick()                   every frame: skin pulses, glow bobbers, sign facing, light flicker.
// Local frame: +Z = the side customers stand on (faces the lake), counter centre at (0, 0, 0.35).
using System;
using System.Collections.Generic;
using UnityEngine;

public static class RodStandLook
{
    public static GameObject root;
    public static Vector3 position; public static float yaw;
    public static Action<string> log;
    public static Transform bookProp;
    static Light glow;
    static Shader colShader;
    static readonly List<Material> mats = new List<Material>();
    class Display { public GameObject pole, bob; public string skin, bobber, rodType; public StandSkinLook.State st = new StandSkinLook.State(); }
    static readonly List<Display> displays = new List<Display>();
    static readonly List<Transform> faceCam = new List<Transform>();

    static int faceErrs;
    static GameObject xray3;   // x-ray box: only from far away (close up it would tint players standing in front of the stall)
    static void L(string s) { if (log != null) try { log(s); } catch { } }

    static Material Mat(Color c, int ztest, int cull, bool transparent, int queue)
    {
        if (colShader == null) colShader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
        var m = new Material(colShader); m.color = c;
        if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", ztest);
        if (m.HasProperty("_Cull")) m.SetInt("_Cull", cull);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", transparent ? 0 : 1);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", transparent ? 5 : 1);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", transparent ? 10 : 0);
        m.renderQueue = queue; mats.Add(m); return m;
    }
    static Material Solid(Color c) { return Mat(c, 4, 2, false, 2000); }

    static GameObject Prim(PrimitiveType t, string name, Transform parent, Vector3 lpos, Vector3 scale, Vector3 euler, Material m)
    {
        var g = GameObject.CreatePrimitive(t);
        g.name = name;
        foreach (var comp in g.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(comp);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = scale; g.transform.localRotation = Quaternion.Euler(euler);
        var r = g.GetComponent<Renderer>();
        if (m != null) r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return g;
    }

    static Font font;
    public static Font GetFont()
    {
        if (font != null) return font;
        try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        if (font == null) try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        return font;
    }
    static TextMesh Text(Transform parent, string name, Vector3 lpos, Vector3 euler, string text, float size, Color c, bool bold)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = lpos; go.transform.localRotation = Quaternion.Euler(euler);
        var tm = go.AddComponent<TextMesh>();
        Font f = GetFont();
        if (f != null) { tm.font = f; go.GetComponent<MeshRenderer>().sharedMaterial = f.material; }
        tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
        tm.fontSize = 64; tm.characterSize = size; tm.color = c; tm.text = text; tm.richText = true;
        if (bold) tm.fontStyle = FontStyle.Bold;
        return tm;
    }

    public static void Destroy()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null; glow = null; bookProp = null;
        foreach (var d in displays) foreach (var m in d.st.mats) if (m != null) UnityEngine.Object.Destroy(m);
        displays.Clear(); faceCam.Clear();
        foreach (var m in mats) if (m != null) UnityEngine.Object.Destroy(m);
        mats.Clear();
    }

    // prices: item name -> price text shown on the tags (from the host's live catalog; may be null/empty)
    public static void Build(Vector3 pos, float yawDeg, IDictionary<string, string> prices)
    {
        Destroy();
        for (int i = 0; i < 10; i++) { var old = GameObject.Find("FishingRodStand"); if (old == null) break; old.name = "FishingRodStand_old"; UnityEngine.Object.Destroy(old); }
        position = pos; yaw = yawDeg;
        root = new GameObject("FishingRodStand");
        root.transform.position = pos; root.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
        Transform t = root.transform;

        Color wood = new Color(0.42f, 0.25f, 0.11f), dark = new Color(0.27f, 0.15f, 0.06f), plank = new Color(0.55f, 0.36f, 0.17f);
        Material mWood = Solid(wood), mDark = Solid(dark), mPlank = Solid(plank);
        Material mRed = Solid(new Color(0.82f, 0.12f, 0.1f)), mWhite = Solid(new Color(0.95f, 0.93f, 0.86f));
        Material mSign = Solid(new Color(0.12f, 0.2f, 0.32f)), mGold = Solid(new Color(1f, 0.78f, 0.25f)), mTag = Solid(new Color(0.96f, 0.92f, 0.78f));

        // floor boards + counter
        Prim(PrimitiveType.Cube, "Deck", t, new Vector3(0f, 0.04f, 0f), new Vector3(3.6f, 0.08f, 2.4f), Vector3.zero, mDark);
        Prim(PrimitiveType.Cube, "CounterBody", t, new Vector3(0f, 0.55f, 0.55f), new Vector3(3.0f, 1.0f, 0.7f), Vector3.zero, mWood);
        Prim(PrimitiveType.Cube, "CounterTop", t, new Vector3(0f, 1.08f, 0.6f), new Vector3(3.2f, 0.07f, 0.9f), Vector3.zero, mPlank);
        for (int i = 0; i < 4; i++)   // front planks
            Prim(PrimitiveType.Cube, "Plank", t, new Vector3(-1.1f + i * 0.733f, 0.55f, 0.91f), new Vector3(0.05f, 0.95f, 0.03f), Vector3.zero, mDark);
        // posts
        foreach (var p in new[] { new Vector3(-1.6f, 0f, 1.05f), new Vector3(1.6f, 0f, 1.05f), new Vector3(-1.6f, 0f, -1.05f), new Vector3(1.6f, 0f, -1.05f) })
        {
            float h = p.z > 0 ? 2.45f : 2.85f;
            Prim(PrimitiveType.Cube, "Post", t, new Vector3(p.x, h / 2f, p.z), new Vector3(0.12f, h, 0.12f), Vector3.zero, mWood);
        }
        // awning: 7 stripes sloping from the back (2.9 m) to the front (2.45 m), with a scalloped front edge
        var aw = new GameObject("Awning").transform; aw.SetParent(t, false);
        aw.localPosition = new Vector3(0f, 2.7f, 0.05f); aw.localRotation = Quaternion.Euler(-12f, 0f, 0f);
        for (int i = 0; i < 7; i++)
        {
            float x = -1.71f + i * 0.57f;
            Prim(PrimitiveType.Cube, "Stripe", aw, new Vector3(x, 0f, 0f), new Vector3(0.57f, 0.05f, 2.55f), Vector3.zero, i % 2 == 0 ? mRed : mWhite);
            Prim(PrimitiveType.Sphere, "Scallop", aw, new Vector3(x, -0.02f, 1.27f), new Vector3(0.56f, 0.06f, 0.36f), Vector3.zero, i % 2 == 0 ? mRed : mWhite);
        }
        // sign on top of the awning front
        var sign = Prim(PrimitiveType.Cube, "Sign", t, new Vector3(0f, 3.25f, 1.05f), new Vector3(2.3f, 0.62f, 0.08f), Vector3.zero, mSign).transform;
        Prim(PrimitiveType.Cube, "SignFrame", t, new Vector3(0f, 3.25f, 1.05f), new Vector3(2.42f, 0.72f, 0.06f), Vector3.zero, mGold);
        Prim(PrimitiveType.Cube, "SignLegL", t, new Vector3(-0.9f, 2.85f, 1.05f), new Vector3(0.07f, 0.3f, 0.07f), Vector3.zero, mWood);
        Prim(PrimitiveType.Cube, "SignLegR", t, new Vector3(0.9f, 2.85f, 1.05f), new Vector3(0.07f, 0.3f, 0.07f), Vector3.zero, mWood);
        Text(t, "SignTextFront", new Vector3(0f, 3.25f, 1.1f), new Vector3(0f, 180f, 0f), "ROD SHOP", 0.075f, new Color(1f, 0.85f, 0.3f), true);
        sign.name = "Sign";

        // back rail + display rods leaning on it, each with its matching bobber on a short line
        Prim(PrimitiveType.Cube, "Rail", t, new Vector3(0f, 1.7f, -0.95f), new Vector3(3.1f, 0.07f, 0.07f), Vector3.zero, mDark);
        Prim(PrimitiveType.Cube, "RailLow", t, new Vector3(0f, 0.35f, -0.95f), new Vector3(3.1f, 0.07f, 0.07f), Vector3.zero, mDark);
        // one display rod per rod TYPE (its own model, RodTypeLook) in a matching shop skin, with a bobber on the line
        string[,] rods = { { "Stick Rod", "Default", "Plain" }, { "Basic Rod", "Carbon", "Classic" }, { "Long Cast Rod", "Lunar Glass", "Star" }, { "Steady Rod", "Default", "Duck" },
                           { "Quick Rod", "Neon", "Classic" }, { "Lucky Rod", "Bamboo", "Skull" }, { "Golden Rod", "Gold", "Star" } };
        for (int i = 0; i < rods.GetLength(0); i++)
        {
            float x = -1.38f + i * 0.46f;
            var d = new Display { skin = rods[i, 1], bobber = rods[i, 2], rodType = rods[i, 0] };
            d.pole = Prim(PrimitiveType.Cylinder, "DisplayRod", t, new Vector3(x, 0.95f, -0.85f), new Vector3(0.035f, 0.9f, 0.035f), new Vector3(-12f, 0f, (i % 2 == 0 ? 4f : -4f)), null);
            Vector3 tip = d.pole.transform.localPosition + d.pole.transform.localRotation * new Vector3(0f, 0.9f, 0f);
            d.bob = Prim(PrimitiveType.Sphere, "DisplayBobber", t, tip + new Vector3(0f, -0.55f, 0.05f), Vector3.one * 0.16f, Vector3.zero, null);
            Prim(PrimitiveType.Cylinder, "DisplayLine", t, tip + new Vector3(0f, -0.26f, 0.025f), new Vector3(0.006f, 0.27f, 0.006f), Vector3.zero, mWhite);
            StandSkinLook.Apply(d.st, d.pole, d.bob, null, d.skin, d.bobber);
            RodTypeLook.Apply(d.pole, d.rodType);
            displays.Add(d);
            string price; string pl = prices != null && prices.TryGetValue(d.rodType, out price) ? "\n" + price : "";
            Text(t, "RodLabel", new Vector3(x, 1.86f, -0.86f), new Vector3(0f, 180f, 0f), "<b>" + d.rodType.Replace(" Rod", "") + "</b>" + pl, 0.0055f, new Color(1f, 0.92f, 0.7f), false);
        }
        // counter: the plain bobbers in a row + the three glow bobbers on a plinth
        string[] row = { "Plain", "Classic", "Star", "Duck", "Skull" };
        for (int i = 0; i < row.Length; i++)
        {
            float x = -1.35f + i * 0.3f;
            var d = new Display { skin = "Default", bobber = row[i] };
            d.pole = Prim(PrimitiveType.Cylinder, "Peg", t, new Vector3(x, 1.16f, 0.62f), new Vector3(0.03f, 0.05f, 0.03f), Vector3.zero, mDark);
            d.bob = Prim(PrimitiveType.Sphere, "CounterBobber", t, new Vector3(x, 1.3f, 0.62f), Vector3.one * 0.16f, Vector3.zero, null);
            StandSkinLook.Apply(d.st, d.pole, d.bob, null, "Default", d.bobber);
            d.pole.GetComponent<Renderer>().sharedMaterial = mDark;
            displays.Add(d);
            Tag(t, new Vector3(x, 1.16f, 0.95f), d.bobber, prices);
        }
        Prim(PrimitiveType.Cube, "Plinth", t, new Vector3(0.75f, 1.2f, 0.6f), new Vector3(0.95f, 0.18f, 0.4f), Vector3.zero, mDark);
        for (int i = 0; i < GlowBobberLook.Names.Length; i++)
        {
            float x = 0.45f + i * 0.3f;
            var b = Prim(PrimitiveType.Sphere, "GlowDisplay", t, new Vector3(x, 1.42f, 0.6f), Vector3.one * 0.18f, Vector3.zero, Solid(Color.white));
            GlowBobberLook.Apply(b, GlowBobberLook.Names[i]);
            Tag(t, new Vector3(x, 1.2f, 0.82f), GlowBobberLook.Names[i], prices);
        }
        Text(t, "GlowNote", new Vector3(0.75f, 1.62f, 0.62f), new Vector3(0f, 180f, 0f), "<b>NEW!</b> glow at night", 0.012f, new Color(0.6f, 1f, 0.75f), false);
        // the guide book lying on the counter (walk up + E opens it)
        var book = new GameObject("GuideBookProp").transform; book.SetParent(t, false);
        book.localPosition = new Vector3(1.3f, 1.15f, 0.62f); book.localRotation = Quaternion.Euler(0f, 18f, 0f);
        Prim(PrimitiveType.Cube, "Pages", book, new Vector3(0f, 0.035f, 0f), new Vector3(0.3f, 0.05f, 0.4f), Vector3.zero, Solid(new Color(0.93f, 0.88f, 0.74f)));
        Material leather = Solid(new Color(0.35f, 0.12f, 0.08f));
        Prim(PrimitiveType.Cube, "CoverTop", book, new Vector3(0f, 0.065f, 0f), new Vector3(0.32f, 0.012f, 0.42f), Vector3.zero, leather);
        Prim(PrimitiveType.Cube, "CoverBottom", book, new Vector3(0f, 0.005f, 0f), new Vector3(0.32f, 0.012f, 0.42f), Vector3.zero, leather);
        Prim(PrimitiveType.Cube, "Spine", book, new Vector3(-0.155f, 0.035f, 0f), new Vector3(0.02f, 0.07f, 0.42f), Vector3.zero, leather);
        Prim(PrimitiveType.Cube, "Emblem", book, new Vector3(0.02f, 0.072f, 0f), new Vector3(0.12f, 0.004f, 0.12f), new Vector3(0f, 45f, 0f), mGold);
        Text(book, "BookHint", new Vector3(0f, 0.22f, 0f), new Vector3(0f, 180f, 0f), "GUIDE [E]", 0.01f, new Color(1f, 0.9f, 0.6f), true);
        bookProp = book;

        // outline shell: back faces as a rim + an x-ray pass that only draws behind walls (ZTest Greater)
        Color teal = new Color(0.25f, 1f, 0.8f);
        Material rim = Mat(teal, 4, 1, false, 2001), xray = Mat(new Color(0.25f, 1f, 0.8f, 0.5f), 5, 2, true, 4000);
        // rim (back faces of a slightly bigger copy) only around the SOLID parts: counter, sign, awning
        var rimT = new GameObject("OutlineRim").transform; rimT.SetParent(t, false);
        Prim(PrimitiveType.Cube, "S", rimT, new Vector3(0f, 0.6f, 0.6f), new Vector3(3.32f, 1.14f, 0.98f), Vector3.zero, rim);
        Prim(PrimitiveType.Cube, "S", rimT, new Vector3(0f, 3.25f, 1.05f), new Vector3(2.52f, 0.8f, 0.16f), Vector3.zero, rim);
        // x-ray: ONE box around the whole stall, front faces only, drawn only where a wall/hill is in front of it
        var xrT = new GameObject("OutlineXray").transform; xrT.SetParent(t, false); xray3 = xrT.gameObject;
        Prim(PrimitiveType.Cube, "S", xrT, new Vector3(0f, 2.25f, 0.25f), new Vector3(4.0f, 3.3f, 2.9f), Vector3.zero, xray);
        var lg = new GameObject("Glow"); lg.transform.SetParent(t, false); lg.transform.localPosition = new Vector3(0f, 2.3f, 0.9f);
        glow = lg.AddComponent<Light>(); glow.type = LightType.Point; glow.color = new Color(1f, 0.85f, 0.6f); glow.range = 8f; glow.intensity = 2f;
        // floating label above the stall (always faces the camera)
        faceCam.Add(Text(t, "FloatLabel", new Vector3(0f, 4.15f, 0.5f), new Vector3(0f, 180f, 0f), "ROD SHOP", 0.05f, new Color(0.4f, 1f, 0.85f), true).transform);
        L("rod stand built at " + pos + " yaw " + yawDeg.ToString("0") + " shader " + (colShader != null ? colShader.name : "none") + ", font " + (GetFont() != null));
    }

    static void Tag(Transform t, Vector3 lpos, string item, IDictionary<string, string> prices)
    {
        string p; if (prices == null || !prices.TryGetValue(item, out p)) return;
        var tag = Prim(PrimitiveType.Cube, "Tag", t, lpos + new Vector3(0f, 0f, 0.01f), new Vector3(0.26f, 0.09f, 0.01f), Vector3.zero, Mat(new Color(0.96f, 0.92f, 0.78f), 4, 2, false, 2000));
        Text(t, "TagText", lpos + new Vector3(0f, 0f, 0.025f), new Vector3(0f, 180f, 0f), "<b>" + item + "</b>\n" + p, 0.0055f, new Color(0.15f, 0.1f, 0.05f), false);
        tag.name = "Tag";
    }

    public static void Tick()
    {
        if (root == null) return;
        float t = Time.time;
        try
        {
            if (glow != null) glow.intensity = 1.8f + 0.25f * Mathf.Sin(t * 2.7f) + 0.1f * Mathf.Sin(t * 11f);
            foreach (var d in displays) if (d.bob != null) try { StandSkinLook.Animate(d.st, d.bob, null, null, d.skin, d.bobber, t); if (d.rodType != null) RodTypeLook.Tick(d.pole); } catch { }
        }
        catch { }
        // billboard: TextMesh reads correctly when its +Z points AWAY from the viewer (yaw only, so it stays upright)
        try
        {
            Camera cam = Camera.main;
            if (cam != null && xray3 != null)
            {
                bool far = (cam.transform.position - position).sqrMagnitude > 18f * 18f;
                if (xray3.activeSelf != far) xray3.SetActive(far);
            }
            if (cam != null)
                foreach (var f in faceCam)
                {
                    if (f == null) continue;
                    Vector3 d = f.position - cam.transform.position; d.y = 0f;
                    if (d.sqrMagnitude > 0.0001f) f.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                }
        }
        catch (Exception e) { if (faceErrs++ < 5) L("label facing error: " + e.Message); }
    }
}
