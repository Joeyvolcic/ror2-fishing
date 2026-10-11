// TEST SANDBOX geometry (host add-on TestSandbox<N>): a 60 x 60 m concrete deck at sea level far from the stage, with a
// square pool cut into it (the moved fishing lake shows through the hole), a short dock out to sea, a "TEST SANDBOX" sign,
// two whip target dummies, grass planters, an invisible sea floor + walls, and the lake's own material out to the horizon.
// Local frame: +z = north (pool, sign, dock), spawn on the south side looking north. All solid parts = BoxColliders on the
// World layer (exact, cheap); rendering = one combined mesh per material with world-scaled UVs.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class SandboxLook
{
    public const float Half = 30f;           // deck half size (60 x 60 m)
    public const float Deck = 0.9f;          // deck top above sea level
    public const float PoolHalf = 7f;        // pool 14 x 14 m
    public const float PoolZ = 12f;          // pool centre (local z)
    public const float PoolDepth = 8f;       // pool floor below sea level
    public const float FloorDepth = 14f;     // invisible sea floor below sea level
    public const float WallHalf = 220f;      // invisible walls
    public const float DockZ0 = 30f, DockZ1 = 52f, DockHalfW = 2f;
    public static string ShaderName = "";

    public class Dummy { public Transform pivot; public TextMesh label; public Vector3 centre; public int hits; public float hitAt = -100f; }
    public class Info
    {
        public Vector3 origin; public GameObject root;
        public Vector3 spawn, stall, merchant, poolCentre, dockEnd, sign;
        public float spawnYaw, stallYaw, merchantYaw;
        public List<Dummy> dummies = new List<Dummy>();
        public List<Mesh> meshes = new List<Mesh>(); public List<Texture2D> textures = new List<Texture2D>(); public List<Material> materials = new List<Material>();
        public GameObject ocean; public Mesh oceanMesh; public float oceanHalf;
        public int boxes, verts; public float buildMs;
    }

    // ------------------------------------------------------------------ shaders / materials
    static Shader lit, sprite; static bool litHopoo;
    static void FindShaders()
    {
        if (lit != null) return;
        try
        {
            foreach (Material mt in Resources.FindObjectsOfTypeAll<Material>())
                if (mt != null && mt.shader != null && mt.shader.name == "Hopoo Games/Deferred/Standard") { lit = mt.shader; litHopoo = true; break; }
        }
        catch { }
        if (lit == null) lit = Shader.Find("Standard");
        if (lit == null) lit = Shader.Find("Legacy Shaders/Diffuse");
        sprite = Shader.Find("Sprites/Default");
        if (lit == null) lit = sprite;
        ShaderName = (lit != null ? lit.name : "NONE");
    }
    static Material Lit(Info info, string name, Texture2D tex, float smooth)
    {
        var m = new Material(lit); m.name = name;
        m.mainTexture = tex;
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (litHopoo)
        {
            m.shaderKeywords = new string[0];
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_SpecularStrength")) m.SetFloat("_SpecularStrength", 0.06f);
            if (m.HasProperty("_SpecularExponent")) m.SetFloat("_SpecularExponent", 4f);
        }
        else
        {
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        }
        info.materials.Add(m);
        return m;
    }

    // ------------------------------------------------------------------ textures (tileable, 64 px)
    static float H(int x, int y, int s) { unchecked { int h = x * 374761393 + y * 668265263 + s * 982451653; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xffff) / 65535f; } }
    static Texture2D Tex(Info info, string name, Func<int, int, Color> f)
    {
        var t = new Texture2D(64, 64, TextureFormat.RGBA32, true); t.name = name; t.wrapMode = TextureWrapMode.Repeat; t.filterMode = FilterMode.Bilinear;
        var px = new Color[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) px[y * 64 + x] = f(x, y);
        t.SetPixels(px); t.Apply(true);
        info.textures.Add(t);
        return t;
    }
    static Color Mul(Color c, float k) { return new Color(c.r * k, c.g * k, c.b * k, 1f); }

    // ------------------------------------------------------------------ mesh builder with world-scaled UVs
    class MB
    {
        public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector3> n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>(); public readonly List<int> t = new List<int>();
        public float tile = 1f;
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm, Vector3 ua, Vector3 va)
        {
            int i = v.Count;
            foreach (Vector3 p in new[] { a, b, c, d }) { v.Add(p); n.Add(nrm); uv.Add(new Vector2(Vector3.Dot(p, ua) / tile, Vector3.Dot(p, va) / tile)); }
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }
        // axis-aligned box (local coords), every face
        public void Box(Vector3 min, Vector3 max, bool bottom)
        {
            Vector3 X = Vector3.right, Y = Vector3.up, Z = Vector3.forward;
            float x0 = min.x, y0 = min.y, z0 = min.z, x1 = max.x, y1 = max.y, z1 = max.z;
            Quad(new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Y, X, Z);          // top
            if (bottom) Quad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), -Y, X, Z);
            Quad(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), Z, X, Y);          // north
            Quad(new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), -Z, X, Y);         // south
            Quad(new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), X, Z, Y);          // east
            Quad(new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), -X, Z, Y);         // west
        }
        public Mesh ToMesh(string name)
        {
            var m = new Mesh(); m.name = name;
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
            return m;
        }
    }

    static void Collider(Transform root, string name, Vector3 min, Vector3 max, int layer, Info info)
    {
        var g = new GameObject(name); g.layer = layer; g.transform.SetParent(root, false);
        var bc = g.AddComponent<BoxCollider>(); bc.center = (min + max) * 0.5f; bc.size = max - min;
        info.boxes++;
    }
    static GameObject MeshGo(string name, Transform parent, Mesh mesh, Material mat, Info info)
    {
        var g = new GameObject(name); g.transform.SetParent(parent, false);
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = g.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        info.meshes.Add(mesh); info.verts += mesh.vertexCount;
        return g;
    }
    // solid box: rendered in builder mb + collider
    static void Solid(MB mb, Transform root, string name, Vector3 min, Vector3 max, int layer, Info info, bool bottom = false)
    {
        mb.Box(min, max, bottom);
        Collider(root, name, min, max, layer, info);
    }

    static Font font;
    static TextMesh Text(Transform parent, string name, string s, Vector3 local, float yaw, float size, Color c, Info info)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = local; go.transform.localRotation = Quaternion.Euler(0f, yaw + 180f, 0f);   // TextMesh reads along -Z: +180 makes it face +Z rotated by yaw
        var tm = go.AddComponent<TextMesh>();
        if (font == null) { try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        if (font == null) { try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { } }
        if (font != null) { tm.font = font; go.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
        tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
        tm.characterSize = size; tm.fontSize = 64; tm.text = s; tm.color = c;
        return tm;
    }

    // ================================================================== build
    public static Info Build(Vector3 origin, int layer)
    {
        float t0 = Time.realtimeSinceStartup;
        FindShaders();
        var info = new Info { origin = origin };
        var root = new GameObject("TestSandboxRoot"); root.transform.position = origin; info.root = root;
        Transform R = root.transform;
        float D = Deck, P = PoolHalf, Z = PoolZ;

        // ---- textures + materials
        Texture2D concreteT = Tex(info, "SbConcrete", (x, y) =>
        {
            float g = 0.60f + (H(x, y, 1) - 0.5f) * 0.06f + (H(x / 4, y / 4, 2) - 0.5f) * 0.05f;
            bool grout = x == 0 || y == 0 || x == 63 || y == 63;
            return grout ? new Color(0.42f, 0.42f, 0.41f) : new Color(g, g, g * 0.97f);
        });
        Texture2D tileT = Tex(info, "SbPoolTile", (x, y) =>
        {
            bool grout = (x % 16) == 0 || (y % 16) == 0;
            float k = 0.95f + (H(x / 16, y / 16, 3) - 0.5f) * 0.1f;
            return grout ? new Color(0.85f, 0.9f, 0.92f) : Mul(new Color(0.25f, 0.62f, 0.78f), k);
        });
        Texture2D woodT = Tex(info, "SbWood", (x, y) =>
        {
            int plank = y / 16; bool gap = (y % 16) == 0;
            float k = 0.85f + H(plank, 0, 4) * 0.25f + (H(x / 3, y, 5) - 0.5f) * 0.08f;
            return gap ? new Color(0.18f, 0.12f, 0.07f) : Mul(new Color(0.55f, 0.38f, 0.22f), k);
        });
        Texture2D grassT = Tex(info, "SbGrass", (x, y) =>
        {
            float k = 0.8f + H(x, y, 6) * 0.35f + (H(x / 8, y / 8, 7) - 0.5f) * 0.15f;
            return Mul(new Color(0.30f, 0.55f, 0.20f), k);
        });
        Texture2D paintT = Tex(info, "SbPaint", (x, y) => Mul(new Color(0.08f, 0.32f, 0.22f), 0.95f + H(x, y, 8) * 0.1f));
        Texture2D strawT = Tex(info, "SbStraw", (x, y) =>
        {
            float k = 0.8f + H(x, y / 6, 9) * 0.35f;
            bool ring = Mathf.Abs(Mathf.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)) - 18f) < 3f || ((x - 32) * (x - 32) + (y - 32) * (y - 32)) < 36;
            return ring ? new Color(0.85f, 0.12f, 0.1f) : Mul(new Color(0.85f, 0.72f, 0.35f), k);
        });
        Texture2D stripeT = Tex(info, "SbStripe", (x, y) => ((x + y) / 8) % 2 == 0 ? new Color(0.95f, 0.78f, 0.1f) : new Color(0.12f, 0.12f, 0.12f));
        Texture2D strawPlainT = Tex(info, "SbStrawPlain", (x, y) => Mul(new Color(0.85f, 0.72f, 0.35f), 0.8f + H(x, y / 6, 10) * 0.35f));
        Material strawPlain = Lit(info, "SbStrawPlain", strawPlainT, 0.05f);
        Material concrete = Lit(info, "SbConcrete", concreteT, 0.15f), tile = Lit(info, "SbPoolTile", tileT, 0.5f), wood = Lit(info, "SbWood", woodT, 0.1f),
                 grass = Lit(info, "SbGrass", grassT, 0.05f), paint = Lit(info, "SbPaint", paintT, 0.2f), straw = Lit(info, "SbStraw", strawT, 0.05f), stripe = Lit(info, "SbStripe", stripeT, 0.2f);

        var mbC = new MB { tile = 2f }; var mbT = new MB { tile = 1f }; var mbW = new MB { tile = 1.2f }; var mbG = new MB { tile = 2f }; var mbP = new MB { tile = 1f }; var mbS = new MB { tile = 0.6f };

        // ---- deck (4 slabs around the pool hole), top at +D, 3 m thick (hides the lake sheet underneath)
        float yb = D - 3f;
        Solid(mbC, R, "SbDeckS", new Vector3(-Half, yb, -Half), new Vector3(Half, D, Z - P), layer, info);
        Solid(mbC, R, "SbDeckN", new Vector3(-Half, yb, Z + P), new Vector3(Half, D, Half), layer, info);
        Solid(mbC, R, "SbDeckW", new Vector3(-Half, yb, Z - P), new Vector3(-P, D, Z + P), layer, info);
        Solid(mbC, R, "SbDeckE", new Vector3(P, yb, Z - P), new Vector3(Half, D, Z + P), layer, info);
        // ---- pool: tiled walls (0.5 m thick, inside the hole) from the floor up to the deck, floor at -PoolDepth, one submerged step
        float pw = 0.5f, fy = -PoolDepth;
        Solid(mbT, R, "SbPoolWallS", new Vector3(-P, fy, Z - P), new Vector3(P, D + 0.02f, Z - P + pw), layer, info);
        Solid(mbT, R, "SbPoolWallN", new Vector3(-P, fy, Z + P - pw), new Vector3(P, D + 0.02f, Z + P), layer, info);
        Solid(mbT, R, "SbPoolWallW", new Vector3(-P, fy, Z - P + pw), new Vector3(-P + pw, D + 0.02f, Z + P - pw), layer, info);
        Solid(mbT, R, "SbPoolWallE", new Vector3(P - pw, fy, Z - P + pw), new Vector3(P, D + 0.02f, Z + P - pw), layer, info);
        Solid(mbT, R, "SbPoolFloor", new Vector3(-P, fy - 1f, Z - P), new Vector3(P, fy, Z + P), layer, info);
        Solid(mbT, R, "SbPoolStep", new Vector3(-2f, fy, Z - P + pw), new Vector3(2f, -0.45f, Z - P + pw + 1.3f), layer, info);
        // yellow/black safety edge around the pool (decor, thin)
        float e = 0.35f;
        mbS.Box(new Vector3(-P - e, D, Z - P - e), new Vector3(P + e, D + 0.03f, Z - P), false);
        mbS.Box(new Vector3(-P - e, D, Z + P), new Vector3(P + e, D + 0.03f, Z + P + e), false);
        mbS.Box(new Vector3(-P - e, D, Z - P), new Vector3(-P, D + 0.03f, Z + P), false);
        mbS.Box(new Vector3(P, D, Z - P), new Vector3(P + e, D + 0.03f, Z + P), false);

        // ---- dock: planks from the north edge out to sea, posts down to the sea floor
        Solid(mbW, R, "SbDock", new Vector3(-DockHalfW, D - 0.3f, DockZ0 - 0.5f), new Vector3(DockHalfW, D, DockZ1), layer, info);
        for (float z = DockZ0 + 3f; z <= DockZ1 + 0.01f; z += 4.75f)
            foreach (float x in new[] { -DockHalfW + 0.2f, DockHalfW - 0.2f })
                Solid(mbW, R, "SbDockPost", new Vector3(x - 0.15f, -FloorDepth, z - 0.15f), new Vector3(x + 0.15f, D + 0.5f, z + 0.15f), layer, info);
        // low rail stops at the end of the dock
        Solid(mbW, R, "SbDockEnd", new Vector3(-DockHalfW, D, DockZ1 - 0.25f), new Vector3(DockHalfW, D + 0.35f, DockZ1), layer, info);

        // ---- grass planters in the 4 corners (low, walkable)
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
            {
                Vector3 c = new Vector3(sx * (Half - 5f), 0, sz * (Half - 5f));
                if (sz > 0 && Mathf.Abs(c.x) < 8f) continue;
                Solid(mbG, R, "SbPlanter", new Vector3(c.x - 3.5f, D, c.z - 3.5f), new Vector3(c.x + 3.5f, D + 0.25f, c.z + 3.5f), layer, info);
            }

        // ---- sign: two posts at the dock entrance (x = +-5, z = 25) and a board facing the spawn (south)
        float sgZ = Half - 5f;
        Solid(mbW, R, "SbSignPostW", new Vector3(-5.3f, D, sgZ - 0.2f), new Vector3(-4.9f, D + 5.2f, sgZ + 0.2f), layer, info);
        Solid(mbW, R, "SbSignPostE", new Vector3(4.9f, D, sgZ - 0.2f), new Vector3(5.3f, D + 5.2f, sgZ + 0.2f), layer, info);
        mbP.Box(new Vector3(-5.6f, D + 3.6f, sgZ - 0.12f), new Vector3(5.6f, D + 5.4f, sgZ + 0.12f), true);
        Collider(R, "SbSignBoard", new Vector3(-5.6f, D + 3.6f, sgZ - 0.12f), new Vector3(5.6f, D + 5.4f, sgZ + 0.12f), layer, info);
        Text(R, "SbSignText", "TEST SANDBOX", new Vector3(0, D + 4.5f, sgZ - 0.15f), 180f, 0.13f, new Color(1f, 0.92f, 0.55f), info);
        Text(R, "SbPoolLabel", "POOL  (connected to the lake)", new Vector3(0, D + 0.05f, Z - P - 1.2f), 180f, 0.035f, new Color(0.9f, 0.95f, 1f), info)
            .transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        Text(R, "SbDockLabel", "DOCK", new Vector3(0, D + 0.05f, DockZ0 + 1.5f), 180f, 0.04f, new Color(1f, 0.95f, 0.8f), info)
            .transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // ---- whip target dummies (decor only: a post, a straw body with a red target, a head) - hit test is done in code
        foreach (float dx in new[] { -6f, 6f })
        {
            Vector3 b = new Vector3(dx, D, -14f);
            Solid(mbW, R, "SbDummyBase", b + new Vector3(-0.5f, 0f, -0.5f), b + new Vector3(0.5f, 0.15f, 0.5f), layer, info);
            var pivot = new GameObject("SbDummyPivot"); pivot.transform.SetParent(R, false); pivot.transform.localPosition = b + Vector3.up * 0.15f;
            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder); UnityEngine.Object.Destroy(post.GetComponent<Collider>());
            post.name = "SbDummyPost"; post.transform.SetParent(pivot.transform, false); post.transform.localPosition = new Vector3(0, 0.6f, 0); post.transform.localScale = new Vector3(0.12f, 0.6f, 0.12f);
            post.GetComponent<Renderer>().sharedMaterial = wood;
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder); UnityEngine.Object.Destroy(body.GetComponent<Collider>());
            body.name = "SbDummyBody"; body.transform.SetParent(pivot.transform, false); body.transform.localPosition = new Vector3(0, 1.45f, 0); body.transform.localScale = new Vector3(0.6f, 0.45f, 0.6f);
            body.GetComponent<Renderer>().sharedMaterial = straw;
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere); UnityEngine.Object.Destroy(head.GetComponent<Collider>());
            head.name = "SbDummyHead"; head.transform.SetParent(pivot.transform, false); head.transform.localPosition = new Vector3(0, 2.15f, 0); head.transform.localScale = new Vector3(0.42f, 0.42f, 0.42f);
            head.GetComponent<Renderer>().sharedMaterial = strawPlain;
            var arms = GameObject.CreatePrimitive(PrimitiveType.Cube); UnityEngine.Object.Destroy(arms.GetComponent<Collider>());
            arms.name = "SbDummyArms"; arms.transform.SetParent(pivot.transform, false); arms.transform.localPosition = new Vector3(0, 1.65f, 0); arms.transform.localScale = new Vector3(1.3f, 0.12f, 0.12f);
            arms.GetComponent<Renderer>().sharedMaterial = wood;
            var lab = Text(pivot.transform, "SbDummyLabel", "WHIP ME", new Vector3(0, 2.75f, 0), 180f, 0.03f, Color.white, info);
            info.dummies.Add(new Dummy { pivot = pivot.transform, label = lab, centre = origin + b + Vector3.up * 1.45f });
        }

        // ---- render meshes
        MeshGo("SbConcrete", R, mbC.ToMesh("SbConcrete"), concrete, info);
        MeshGo("SbTiles", R, mbT.ToMesh("SbTiles"), tile, info);
        MeshGo("SbWood", R, mbW.ToMesh("SbWood"), wood, info);
        MeshGo("SbGrass", R, mbG.ToMesh("SbGrass"), grass, info);
        MeshGo("SbPaint", R, mbP.ToMesh("SbPaint"), paint, info);
        MeshGo("SbStripe", R, mbS.ToMesh("SbStripe"), stripe, info);

        // ---- invisible sea floor + walls (players can't sink forever or swim off into the void)
        Collider(R, "SbSeaFloor", new Vector3(-WallHalf, -FloorDepth - 2f, -WallHalf), new Vector3(WallHalf, -FloorDepth, WallHalf), layer, info);
        Collider(R, "SbWallE", new Vector3(WallHalf, -FloorDepth, -WallHalf), new Vector3(WallHalf + 4f, 60f, WallHalf), layer, info);
        Collider(R, "SbWallW", new Vector3(-WallHalf - 4f, -FloorDepth, -WallHalf), new Vector3(-WallHalf, 60f, WallHalf), layer, info);
        Collider(R, "SbWallN", new Vector3(-WallHalf, -FloorDepth, WallHalf), new Vector3(WallHalf, 60f, WallHalf + 4f), layer, info);
        Collider(R, "SbWallS", new Vector3(-WallHalf, -FloorDepth, -WallHalf - 4f), new Vector3(WallHalf, 60f, -WallHalf), layer, info);

        // ---- named spots (world)
        info.spawn = origin + new Vector3(0f, D + 0.2f, -20f); info.spawnYaw = 0f;
        info.stall = origin + new Vector3(-18f, D, -6f); info.stallYaw = 90f;        // customers stand on the +x side (towards the middle)
        info.merchant = origin + new Vector3(18f, D, -6f); info.merchantYaw = -90f;
        info.poolCentre = origin + new Vector3(0f, 0f, Z);
        info.dockEnd = origin + new Vector3(0f, D, DockZ1);
        info.sign = origin + new Vector3(0f, D + 4.5f, sgZ);
        Physics.SyncTransforms();
        info.buildMs = (Time.realtimeSinceStartup - t0) * 1000f;
        return info;
    }

    // ================================================================== lake material out to the horizon (as IslandLook.Ocean)
    public const float OceanOuter = 3800f;
    public static void Ocean(Info info, GameObject water)
    {
        if (info == null || info.root == null || water == null) return;
        var wr = water.GetComponent<Renderer>();
        if (wr == null || wr.sharedMaterial == null) return;
        Bounds b = wr.bounds;
        float half = Mathf.Min(b.extents.x, b.extents.z);
        if (half < 50f || half > OceanOuter - 100f) return;
        if (info.ocean == null || Mathf.Abs(info.oceanHalf - half) > 0.5f)
        {
            if (info.ocean != null) UnityEngine.Object.Destroy(info.ocean);
            if (info.oceanMesh != null) UnityEngine.Object.Destroy(info.oceanMesh);
            const int SEG = 128;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int k = 0; k <= SEG; k++)
            {
                float a = k * Mathf.PI * 2f / SEG;
                Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                float ti = half / Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
                Vector3 pi = d * ti, po = d * OceanOuter;
                v.Add(pi); n.Add(Vector3.up); uv.Add(new Vector2(pi.x / (2f * half) + 0.5f, pi.z / (2f * half) + 0.5f));
                v.Add(po); n.Add(Vector3.up); uv.Add(new Vector2(po.x / (2f * half) + 0.5f, po.z / (2f * half) + 0.5f));
            }
            for (int k = 0; k < SEG; k++) { int i0 = k * 2; t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3); t.Add(i0); t.Add(i0 + 3); t.Add(i0 + 1); }
            var m = new Mesh(); m.name = "SbOcean"; m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
            info.oceanMesh = m;
            info.ocean = new GameObject("SbOcean");
            info.ocean.transform.SetParent(info.root.transform, false);
            info.ocean.AddComponent<MeshFilter>().sharedMesh = m;
            var r = info.ocean.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            info.oceanHalf = half;
        }
        var mr = info.ocean.GetComponent<MeshRenderer>();
        if (mr.sharedMaterial != wr.sharedMaterial) mr.sharedMaterial = wr.sharedMaterial;
        Vector3 c = new Vector3(b.center.x, b.max.y, b.center.z);
        if ((info.ocean.transform.position - c).sqrMagnitude > 1e-4f) info.ocean.transform.position = c;
        if (info.ocean.layer != water.layer) info.ocean.layer = water.layer;
    }

    // ================================================================== per frame: dummies wobble after a hit
    public static void Animate(Info info, float t)
    {
        if (info == null) return;
        foreach (Dummy d in info.dummies)
        {
            if (d.pivot == null) continue;
            float s = t - d.hitAt;
            float ang = s >= 0f && s < 2.5f ? 22f * Mathf.Exp(-s * 2.2f) * Mathf.Sin(s * 14f) : 0f;
            d.pivot.localRotation = Quaternion.Euler(ang, 0f, ang * 0.3f);
            if (d.label != null)
            {
                d.label.color = s < 1.2f ? new Color(1f, 0.35f, 0.25f) : Color.white;
                Camera cam = Camera.main;
                if (cam != null) { Vector3 dir = d.label.transform.position - cam.transform.position; dir.y = 0; if (dir.sqrMagnitude > 0.01f) d.label.transform.rotation = Quaternion.LookRotation(dir); }
            }
        }
    }

    public static bool Inside(Info info, Vector3 p)
    {
        if (info == null || info.root == null) return false;
        float dx = p.x - info.origin.x, dz = p.z - info.origin.z;
        return Mathf.Abs(dx) < WallHalf + 20f && Mathf.Abs(dz) < WallHalf + 20f && p.y > info.origin.y - FloorDepth - 30f && p.y < info.origin.y + 200f;
    }

    public static void Destroy(Info info)
    {
        if (info == null) return;
        if (info.root != null) UnityEngine.Object.Destroy(info.root);
        if (info.oceanMesh != null) UnityEngine.Object.Destroy(info.oceanMesh);
        foreach (var m in info.meshes) if (m != null) UnityEngine.Object.Destroy(m);
        foreach (var t in info.textures) if (t != null) UnityEngine.Object.Destroy(t);
        foreach (var m in info.materials) if (m != null) UnityEngine.Object.Destroy(m);
        info.root = null;
    }
}
