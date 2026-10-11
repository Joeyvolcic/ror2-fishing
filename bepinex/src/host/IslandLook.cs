// IslandLook - builds the TROPICAL ISLAND map at runtime, deterministically from (seed, origin).
// Shared by the host add-on (Island<N>) and the guest payload (IslandClient): the same seed + origin gives the same
// terrain, palms, dock, hut, campfire and colliders on every machine (own xorshift RNG + hash noise, no UnityEngine.Random).
// Needs only UnityEngine (CoreModule + PhysicsModule). Main thread only.
//
//   IslandLook.Info info = IslandLook.Build(seed, origin, worldLayer);   // origin.y = sea level
//   IslandLook.Animate(info, Time.time);                                  // every frame: flames + firelight flicker
//   IslandLook.Destroy(info);
//
// Layout (local, relative to origin; f = towards the dock, r = right of it):
//   island ~150-230 m across: beach slope -> grassy plateau -> one hill (~18 m) on the far side, a shallow lagoon with a
//   channel to the sea on one flank. A sandy plaza at the dock root holds the spawn spots, the ROD SHOP spot (plaza + r*9.5)
//   and the FISH MERCHANT spot (plaza - r*9.5). Hut + hammock inland right, campfire with log benches inland left.
//   Wooden dock 4 m wide running ~45 m out to a T-platform; purple hotspot 24 m past its end; golden one off the far shore.
// Draw calls: terrain 1, solid props 1 (+MeshCollider), soft props (leaves, bushes, grass, coconuts) 1, foam 1, 5 flames.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class IslandLook
{
    public const float SeaDepth = 18f;          // flat sea floor below sea level (matches UnderwaterFx's default floorDepth)
    public const float Extent = 175.5f;         // terrain mesh half size
    public const float Step = 1.5f;             // terrain grid step
    public const float WallHalf = 650f;         // invisible walls (players can't swim off into the void)
    public static string ShaderName = "";
    public static string LastError = "";

    public class Info
    {
        public int seed; public Vector3 origin; public GameObject root;
        public float R0, dockAngle;
        public Vector3 fwd, right;
        public Vector3 plaza, dockStart, dockEnd, hut, campfire, stall, merchant, hotspot, hot2, lagoon, hill;
        public float stallYaw, merchantYaw, plazaH;
        public readonly List<Vector3> spawns = new List<Vector3>();
        public readonly List<Transform> flames = new List<Transform>();
        public readonly List<Vector3> flameBase = new List<Vector3>();
        public readonly List<Light> lights = new List<Light>();
        public readonly List<float> lightBase = new List<float>();
        public int verts, tris, trees, rocks, bushes;
        public float buildMs;
        public Mesh[] meshes = new Mesh[0];
        public Texture2D[] textures = new Texture2D[0];
        public Material[] materials = new Material[0];
        public GameObject ocean; public Mesh oceanMesh; public float oceanHalf;
    }

    // ================================================================ deterministic helpers
    class Rng
    {
        uint s;
        public Rng(int seed) { s = (uint)seed * 2654435761u + 0x9E3779B9u; if (s == 0) s = 1; for (int i = 0; i < 4; i++) Next(); }
        public uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        public float F() { return (Next() & 0xFFFFFF) / 16777216f; }
        public float R(float a, float b) { return a + (b - a) * F(); }
        public int I(int a, int b) { return a + (int)(F() * (b - a)); }
    }
    static float Hash(int x, int z, int s)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + s * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }
    static float VNoise(float x, float z, int s)
    {
        int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
        float fx = x - ix, fz = z - iz;
        fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
        float a = Hash(ix, iz, s), b = Hash(ix + 1, iz, s), c = Hash(ix, iz + 1, s), d = Hash(ix + 1, iz + 1, s);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }
    static float Fbm(float x, float z, int s)   // -1..1
    {
        return (VNoise(x, z, s) * 0.5f + VNoise(x * 2.03f, z * 2.03f, s + 1) * 0.25f + VNoise(x * 4.1f, z * 4.1f, s + 2) * 0.125f) / 0.875f * 2f - 1f;
    }
    static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

    // ================================================================ island shape (heights relative to sea level)
    class Pad { public float x, z, r, h; }
    class Shape
    {
        public int seed; public float R0;
        public float[] amp = new float[6], ph = new float[6];
        public float hx, hz, hh, hr;
        public float lx, lz, lux, luz, lr1, lr2;
        public readonly List<Pad> pads = new List<Pad>();
        public float Shore(float th) { float r = 1f; for (int k = 2; k < 6; k++) r += amp[k] * Mathf.Sin(k * th + ph[k]); return R0 * r; }
        public float Inside(float x, float z) { return Shore(Mathf.Atan2(z, x)) - Mathf.Sqrt(x * x + z * z); }   // metres inside the shoreline (approx.)
        public float Base(float x, float z)
        {
            float s = Inside(x, z), h;
            if (s < 0f) h = -(SeaDepth + 0.6f) * Smooth(0f, 80f, -s) + 0.3f * Fbm(x * 0.05f, z * 0.05f, seed + 5) * Smooth(2f, 12f, -s) * (1f - Smooth(35f, 60f, -s));
            else
            {
                h = 1.6f * Smooth(0f, 13f, s) + 2.6f * Smooth(10f, 55f, s);
                h += 1.4f * Fbm(x * 0.025f, z * 0.025f, seed + 1) * Smooth(8f, 30f, s);
            }
            float dx = x - hx, dz = z - hz;
            float g = Mathf.Exp(-(dx * dx + dz * dz) / (2f * hr * hr));
            if (g > 0.001f) h += hh * g * (1f + 0.3f * Fbm(x * 0.07f, z * 0.07f, seed + 2));
            // lagoon: shallow elliptic basin + a channel out to the sea
            float rx = x - lx, rz = z - lz;
            float u = rx * lux + rz * luz, v = -rx * luz + rz * lux;
            float e = Mathf.Sqrt((u / lr1) * (u / lr1) + (v / lr2) * (v / lr2));
            float w = 1f - Smooth(0.7f, 1.15f, e);
            if (w > 0f) h = Mathf.Lerp(h, -1.5f + 0.9f * e * e, w);
            if (u > 0f)
            {
                float wc = (1f - Smooth(3f, 6.5f, Mathf.Abs(v))) * Smooth(lr1 * 0.3f, lr1 * 0.8f, u);
                if (wc > 0f) h = Mathf.Lerp(h, Mathf.Min(h, -1.2f), wc);
            }
            return h;
        }
        public float H(float x, float z)
        {
            float h = Base(x, z);
            for (int i = 0; i < pads.Count; i++)
            {
                Pad p = pads[i];
                float dx = x - p.x, dz = z - p.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d >= p.r + 6f) continue;
                h = Mathf.Lerp(h, p.h, 1f - Smooth(p.r, p.r + 6f, d));
            }
            return h;
        }
    }

    // ================================================================ mesh builder
    class MB
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Vector3> n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<int> t = new List<int>();
        public int V(Vector3 p, Vector3 nn, Vector2 u) { v.Add(p); n.Add(nn); uv.Add(u); return v.Count - 1; }
        public void Tri(int a, int b, int c) { t.Add(a); t.Add(b); t.Add(c); }
        public void Quad(int a, int b, int c, int d) { t.Add(a); t.Add(b); t.Add(c); t.Add(a); t.Add(c); t.Add(d); }
        public Mesh ToMesh(string name)
        {
            var m = new Mesh(); m.name = name;
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    // atlas: 256x256, 8x8 cells of 32 px
    const int C_BARK = 0, C_BARK2 = 1, C_LEAF = 2, C_LEAF2 = 3, C_DEAD = 4, C_COCO = 5, C_ROCK = 6, C_ROCK2 = 7,
              C_BUSH = 8, C_BUSH2 = 9, C_PLANK = 10, C_PLANK2 = 11, C_THATCH = 12, C_LOG = 13, C_STONE = 14, C_CHAR = 15,
              C_GRASS = 16, C_RED = 17, C_YELLOW = 18, C_ROPE = 19, C_CLOTH = 20, C_WHITE = 21, C_DARK = 22, C_GRASS2 = 23;
    static Vector2 UV(int cell, float u, float v)
    {
        int cx = cell % 8, cy = cell / 8;
        return new Vector2((cx * 32 + 2f + Mathf.Clamp01(u) * 28f) / 256f, (cy * 32 + 2f + Mathf.Clamp01(v) * 28f) / 256f);
    }

    static void Box(MB m, Vector3 c, Vector3 size, Quaternion q, int cell)
    {
        Vector3 h = size * 0.5f;
        Vector3[] N = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        for (int f = 0; f < 6; f++)
        {
            Vector3 nn = N[f];
            Vector3 a = (f < 2) ? Vector3.forward : Vector3.right;        // face tangent 1
            Vector3 b = Vector3.Cross(nn, a);                            // face tangent 2
            Vector3 ha = Vector3.Scale(a, h), hb = Vector3.Scale(b, h), hn = Vector3.Scale(nn, h);
            Vector3 wn = q * nn;
            int i0 = m.V(c + q * (hn - ha - hb), wn, UV(cell, 0, 0));
            int i1 = m.V(c + q * (hn + ha - hb), wn, UV(cell, 1, 0));
            int i2 = m.V(c + q * (hn + ha + hb), wn, UV(cell, 1, 1));
            int i3 = m.V(c + q * (hn - ha + hb), wn, UV(cell, 0, 1));
            // winding: outward facing (clockwise seen from outside in Unity)
            Vector3 cr = Vector3.Cross((q * (ha)), (q * (hb)));
            if (Vector3.Dot(cr, wn) > 0f) { m.Tri(i0, i1, i2); m.Tri(i0, i2, i3); }
            else { m.Tri(i0, i2, i1); m.Tri(i0, i3, i2); }
        }
    }

    // cylinder / cone from a to b
    static void Cyl(MB m, Vector3 a, Vector3 b, float ra, float rb, int sides, int cell, bool capTop, bool capBottom = false)
    {
        Vector3 ax = (b - a).normalized;
        Vector3 s = Vector3.Cross(ax, Mathf.Abs(ax.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
        Vector3 t = Vector3.Cross(ax, s);
        int i0 = m.v.Count;
        for (int k = 0; k <= sides; k++)
        {
            float ang = k * Mathf.PI * 2f / sides;
            Vector3 d = s * Mathf.Cos(ang) + t * Mathf.Sin(ang);
            m.V(a + d * ra, d, UV(cell, (float)k / sides, 0f));
            m.V(b + d * rb, d, UV(cell, (float)k / sides, 1f));
        }
        for (int k = 0; k < sides; k++) { int p = i0 + k * 2; m.Tri(p, p + 3, p + 1); m.Tri(p, p + 2, p + 3); }
        if (capTop)
        {
            int c = m.V(b, ax, UV(cell, 0.5f, 0.5f));
            int r0 = m.v.Count;
            for (int k = 0; k <= sides; k++) { float ang = k * Mathf.PI * 2f / sides; Vector3 d = s * Mathf.Cos(ang) + t * Mathf.Sin(ang); m.V(b + d * rb, ax, UV(cell, 0.5f + 0.5f * Mathf.Cos(ang), 0.5f + 0.5f * Mathf.Sin(ang))); }
            for (int k = 0; k < sides; k++) m.Tri(c, r0 + k, r0 + k + 1);
        }
        if (capBottom)
        {
            int c = m.V(a, -ax, UV(cell, 0.5f, 0.5f));
            int r0 = m.v.Count;
            for (int k = 0; k <= sides; k++) { float ang = k * Mathf.PI * 2f / sides; Vector3 d = s * Mathf.Cos(ang) + t * Mathf.Sin(ang); m.V(a + d * ra, -ax, UV(cell, 0.5f + 0.5f * Mathf.Cos(ang), 0.5f + 0.5f * Mathf.Sin(ang))); }
            for (int k = 0; k < sides; k++) m.Tri(c, r0 + k + 1, r0 + k);
        }
    }

    // lumpy ellipsoid (rocks, bushes, coconuts); normals from the displaced grid
    static void Blob(MB m, Vector3 c, Vector3 radii, Quaternion q, int cell, int lat, int lon, float lump, int nseed)
    {
        var P = new Vector3[lat + 1, lon + 1];
        for (int i = 0; i <= lat; i++)
        {
            float th = Mathf.PI * i / lat;
            for (int j = 0; j <= lon; j++)
            {
                float ph = 2f * Mathf.PI * (j % lon) / lon;
                Vector3 d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                float k = 1f + lump * (Hash(i == 0 ? 0 : (i == lat ? 0 : j % lon), i, nseed) - 0.5f) * 2f;
                P[i, j] = c + q * Vector3.Scale(d * k, radii);
            }
        }
        int baseI = m.v.Count;
        for (int i = 0; i <= lat; i++)
            for (int j = 0; j <= lon; j++)
            {
                Vector3 a = P[Mathf.Min(i + 1, lat), j] - P[Mathf.Max(i - 1, 0), j];
                Vector3 b = P[i, (j + 1) % lon] - P[i, (j + lon - 1) % lon];
                Vector3 nn = Vector3.Cross(b, a);
                Vector3 outw = P[i, j] - c;
                if (nn.sqrMagnitude < 1e-8f) nn = outw;
                if (Vector3.Dot(nn, outw) < 0f) nn = -nn;
                m.V(P[i, j], nn.normalized, UV(cell, (float)j / lon, (float)i / lat));
            }
        for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                int a = baseI + i * (lon + 1) + j, b = a + 1, cc = a + (lon + 1), d = cc + 1;
                m.Tri(a, b, d); m.Tri(a, d, cc);
            }
    }

    // ================================================================ textures
    static Color Noisy(Color c, float n, float amt) { float k = 1f + (n - 0.5f) * amt; return new Color(c.r * k, c.g * k, c.b * k, 1f); }

    static Texture2D Atlas(int seed)
    {
        var tex = new Texture2D(256, 256, TextureFormat.RGBA32, true);
        tex.name = "IslandAtlas"; tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int cell = (y / 32) * 8 + (x / 32);
                float u = ((x % 32) - 2f) / 28f, v = ((y % 32) - 2f) / 28f;
                float n = Hash(x, y, seed + 77), n2 = VNoise(x * 0.25f, y * 0.25f, seed + 78);
                Color c;
                switch (cell)
                {
                    case C_BARK:
                    case C_BARK2:
                        {
                            c = cell == C_BARK ? new Color(0.47f, 0.38f, 0.28f) : new Color(0.55f, 0.50f, 0.42f);
                            float band = Mathf.Abs(v - 0.06f) < 0.07f || v > 0.95f ? 0.62f : 1f;   // ring at each segment joint
                            float fib = 0.9f + 0.2f * VNoise(u * 40f, v * 3f, seed + 79);
                            c = Noisy(c * band * fib, n, 0.15f);
                            break;
                        }
                    case C_LEAF:
                    case C_LEAF2:
                    case C_DEAD:
                        {
                            c = cell == C_LEAF ? new Color(0.17f, 0.47f, 0.14f) : cell == C_LEAF2 ? new Color(0.33f, 0.56f, 0.16f) : new Color(0.56f, 0.42f, 0.22f);
                            float side = Mathf.Abs(u - 0.5f) * 2f;
                            float stripe = Mathf.Repeat(side * 1.2f + v * 16f, 1f);
                            if (stripe < 0.12f) c *= 0.62f;                                        // gaps between leaflets
                            c *= 0.85f + 0.3f * (1f - side);                                         // lighter towards the rib
                            if (side < 0.07f) c = Color.Lerp(c, new Color(0.75f, 0.72f, 0.38f), 0.6f); // midrib
                            c = Noisy(c, n, 0.12f);
                            break;
                        }
                    case C_COCO: c = Noisy(new Color(0.36f, 0.27f, 0.13f), n2, 0.5f); break;
                    case C_ROCK: c = Noisy(new Color(0.53f, 0.50f, 0.46f), n * 0.5f + n2 * 0.5f, 0.45f); break;
                    case C_ROCK2: c = Noisy(new Color(0.38f, 0.37f, 0.35f), n * 0.5f + n2 * 0.5f, 0.45f); break;
                    case C_BUSH: c = Noisy(new Color(0.16f, 0.40f, 0.13f), n2, 0.7f); break;
                    case C_BUSH2: c = Noisy(new Color(0.25f, 0.49f, 0.16f), n2, 0.7f); break;
                    case C_PLANK:
                    case C_PLANK2:
                        {
                            c = cell == C_PLANK ? new Color(0.64f, 0.48f, 0.31f) : new Color(0.44f, 0.31f, 0.20f);
                            float grain = 0.88f + 0.24f * VNoise(u * 2f, v * 26f, seed + 80);
                            c = Noisy(c * grain, n, 0.1f);
                            if (u < 0.04f || u > 0.96f) c *= 0.7f;
                            break;
                        }
                    case C_THATCH: { c = new Color(0.80f, 0.67f, 0.38f) * (0.8f + 0.35f * VNoise(u * 30f, v * 2f, seed + 81)); c = Noisy(c, n, 0.15f); break; }
                    case C_LOG: { c = new Color(0.40f, 0.29f, 0.19f) * (0.8f + 0.3f * VNoise(u * 24f, v * 3f, seed + 82)); break; }
                    case C_STONE: c = Noisy(new Color(0.32f, 0.31f, 0.30f), n2, 0.5f); break;
                    case C_CHAR: c = Noisy(new Color(0.09f, 0.07f, 0.06f), n, 0.6f); break;
                    case C_GRASS: c = Color.Lerp(new Color(0.16f, 0.36f, 0.10f), new Color(0.42f, 0.66f, 0.24f), v); break;
                    case C_GRASS2: c = Color.Lerp(new Color(0.22f, 0.38f, 0.10f), new Color(0.62f, 0.66f, 0.28f), v); break;
                    case C_RED: c = new Color(0.86f, 0.18f, 0.28f); break;
                    case C_YELLOW: c = new Color(0.97f, 0.80f, 0.20f); break;
                    case C_ROPE: c = Noisy(new Color(0.72f, 0.62f, 0.43f), n, 0.2f); break;
                    case C_CLOTH:
                        {
                            bool stripe = Mathf.Repeat(u * 5f, 1f) < 0.5f;
                            c = stripe ? new Color(0.86f, 0.30f, 0.20f) : new Color(0.95f, 0.86f, 0.62f);
                            c = Noisy(c, n, 0.08f); break;
                        }
                    case C_WHITE: c = new Color(0.92f, 0.90f, 0.85f); break;
                    case C_DARK: c = new Color(0.15f, 0.12f, 0.10f); break;
                    default: c = new Color(1f, 0f, 1f); break;
                }
                c.a = 1f;
                px[y * 256 + x] = c;
            }
        tex.SetPixels32(px); tex.Apply(true);
        return tex;
    }

    static Texture2D FlameTex()
    {
        var t = new Texture2D(32, 64, TextureFormat.RGBA32, false); t.name = "IslandFlame"; t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[32 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 32; x++)
            {
                float u = (x + 0.5f) / 32f * 2f - 1f, v = (y + 0.5f) / 64f;
                float width = Mathf.Sin(Mathf.PI * Mathf.Pow(v, 0.6f)) * (1f - v * 0.55f);
                float d = Mathf.Abs(u) / Mathf.Max(0.001f, width);
                float a = Mathf.Clamp01(1f - d) * Mathf.Clamp01((1f - v) * 1.4f);
                Color c = Color.Lerp(new Color(1f, 0.95f, 0.6f), new Color(1f, 0.35f, 0.05f), Mathf.Clamp01(v * 1.3f + d * 0.5f));
                c *= a; c.a = a;
                px[y * 32 + x] = c;
            }
        t.SetPixels32(px); t.Apply(false);
        return t;
    }

    static Texture2D FoamTex(int seed)
    {
        var t = new Texture2D(256, 32, TextureFormat.RGBA32, true); t.name = "IslandFoam"; t.wrapMode = TextureWrapMode.Repeat;
        var px = new Color32[256 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 256; x++)
            {
                float v = (y + 0.5f) / 32f;
                float edge = Mathf.Pow(1f - Mathf.Abs(v - 0.45f) * 2f, 1.3f);
                float n = VNoise(x * 0.12f, y * 0.35f, seed + 90) * 0.7f + VNoise(x * 0.5f, y * 0.8f, seed + 91) * 0.3f;
                float a = Mathf.Clamp01(edge * (n * 1.6f - 0.35f));
                px[y * 256 + x] = new Color(1f, 1f, 1f, a * 0.85f);
            }
        t.SetPixels32(px); t.Apply(true);
        return t;
    }

    // ================================================================ materials
    static Shader lit, sprite, additive; static bool litHopoo;
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
        additive = Shader.Find("Legacy Shaders/Particles/Additive");
        if (lit == null) lit = sprite;
        ShaderName = (lit != null ? lit.name : "NONE") + " / " + (additive != null ? additive.name : (sprite != null ? sprite.name : "NONE"));
    }
    static Material Lit(string name, Texture2D tex, float smooth)
    {
        var m = new Material(lit); m.name = name;
        m.mainTexture = tex;
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (litHopoo)
        {
            m.shaderKeywords = new string[0];
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_SpecularStrength")) m.SetFloat("_SpecularStrength", 0.08f);
            if (m.HasProperty("_SpecularExponent")) m.SetFloat("_SpecularExponent", 4f);
        }
        else
        {
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        }
        return m;
    }

    // ================================================================ build
    public static Info Build(int seed, Vector3 origin, int worldLayer)
    {
        float t0 = Time.realtimeSinceStartup;
        FindShaders();
        var info = new Info { seed = seed, origin = origin };
        var rng = new Rng(seed);
        var S = new Shape { seed = seed };
        S.R0 = rng.R(84f, 98f);
        float[] amps = { 0, 0, 0.075f, 0.05f, 0.035f, 0.02f };
        for (int k = 2; k < 6; k++) { S.amp[k] = amps[k] * rng.R(0.6f, 1.2f); S.ph[k] = rng.R(0f, 6.2832f); }
        float da = rng.R(0f, 6.2832f);
        info.dockAngle = da;
        Vector3 f = new Vector3(Mathf.Cos(da), 0, Mathf.Sin(da)), r = new Vector3(f.z, 0, -f.x);
        info.fwd = f; info.right = r; info.R0 = S.R0;
        // hill on the far side
        float ha = da + Mathf.PI + rng.R(-0.5f, 0.5f), hd = S.R0 * rng.R(0.25f, 0.38f);
        S.hx = Mathf.Cos(ha) * hd; S.hz = Mathf.Sin(ha) * hd; S.hh = rng.R(12f, 16f); S.hr = rng.R(19f, 24f);
        // lagoon on one flank
        float la = da + (rng.F() < 0.5f ? 1f : -1f) * rng.R(1.6f, 2.1f);
        S.lr1 = rng.R(15f, 19f); S.lr2 = rng.R(22f, 28f);
        float ld = S.Shore(la) - S.lr1 * 1.25f;
        S.lux = Mathf.Cos(la); S.luz = Mathf.Sin(la); S.lx = S.lux * ld; S.lz = S.luz * ld;
        // flat pads: plaza (dock root, stall, merchant, spawns), hut, campfire
        float plazaD = S.Shore(da) - 17f;
        Vector3 P = f * plazaD;
        float plazaH = 1.4f;
        S.pads.Add(new Pad { x = P.x, z = P.z, r = 13.5f, h = plazaH });
        Vector3 Hc = P - f * 23f + r * 17f;
        S.pads.Add(new Pad { x = Hc.x, z = Hc.z, r = 6f, h = Mathf.Clamp(S.Base(Hc.x, Hc.z), 1.7f, 6f) });
        Vector3 Cc = P - f * 20f - r * 14f;
        S.pads.Add(new Pad { x = Cc.x, z = Cc.z, r = 5f, h = Mathf.Clamp(S.Base(Cc.x, Cc.z), 1.7f, 6f) });

        info.plazaH = plazaH;
        info.plaza = origin + P + Vector3.up * plazaH;
        info.hill = origin + new Vector3(S.hx, S.H(S.hx, S.hz), S.hz);
        info.lagoon = origin + new Vector3(S.lx, -1.4f, S.lz);
        info.hut = origin + Hc + Vector3.up * S.H(Hc.x, Hc.z);
        info.campfire = origin + Cc + Vector3.up * S.H(Cc.x, Cc.z);
        Vector3 st = P + r * 9.5f - f * 1.5f, me = P - r * 9.5f - f * 1.5f;
        info.stall = origin + st + Vector3.up * S.H(st.x, st.z);
        info.merchant = origin + me + Vector3.up * S.H(me.x, me.z);
        info.stallYaw = info.merchantYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        for (int k = -3; k <= 3; k++) { Vector3 sp = P - f * 5f + r * (k * 1.8f); info.spawns.Add(origin + sp + Vector3.up * (S.H(sp.x, sp.z) + 0.2f)); }

        var root = new GameObject("TropicalIsland");
        root.transform.position = origin;
        info.root = root;
        var meshes = new List<Mesh>(); var texs = new List<Texture2D>(); var mats = new List<Material>();

        // ------------------------------------------------ terrain
        int N = Mathf.RoundToInt(Extent * 2f / Step) + 1;
        var Hg = new float[N, N];
        for (int i = 0; i < N; i++) for (int j = 0; j < N; j++) Hg[i, j] = S.H(-Extent + j * Step, -Extent + i * Step);
        var tm = new MB();
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                float x = -Extent + j * Step, z = -Extent + i * Step;
                float hl = Hg[i, Mathf.Max(j - 1, 0)], hr2 = Hg[i, Mathf.Min(j + 1, N - 1)], hd2 = Hg[Mathf.Max(i - 1, 0), j], hu = Hg[Mathf.Min(i + 1, N - 1), j];
                Vector3 nn = new Vector3(hl - hr2, 2f * Step, hd2 - hu).normalized;
                tm.V(new Vector3(x, Hg[i, j], z), nn, new Vector2((float)j / (N - 1), (float)i / (N - 1)));
            }
        for (int i = 0; i < N - 1; i++)
            for (int j = 0; j < N - 1; j++)
            {
                int a = i * N + j, b = a + 1, c = a + N, d = c + 1;
                tm.Tri(a, c, d); tm.Tri(a, d, b);
            }
        Mesh terrainMesh = tm.ToMesh("IslandTerrain"); meshes.Add(terrainMesh);
        Texture2D ttex = TerrainTex(S, Hg, N, info, seed); texs.Add(ttex);
        Material tmat = Lit("IslandTerrain", ttex, 0.05f); mats.Add(tmat);
        var tgo = MeshGo("IslandTerrain", root.transform, terrainMesh, tmat, worldLayer);
        var tcol = tgo.AddComponent<MeshCollider>(); tcol.sharedMesh = terrainMesh;

        // ------------------------------------------------ props
        var solid = new MB(); var soft = new MB();
        var avoid = new List<Vector4>();   // x, z, radius (local)
        avoid.Add(new Vector4(P.x, P.z, 15f, 0)); avoid.Add(new Vector4(Hc.x, Hc.z, 8f, 0)); avoid.Add(new Vector4(Cc.x, Cc.z, 7f, 0));
        avoid.Add(new Vector4(S.lx, S.lz, Mathf.Max(S.lr1, S.lr2) + 2f, 0));

        // dock
        float shoreD = S.Shore(da);
        float dockStartD = plazaD + 5f, dockEndD = shoreD + 34f;
        BuildDock(solid, root.transform, S, f, r, dockStartD, dockEndD, plazaH + 0.05f, worldLayer, info, origin);
        for (float dd = dockStartD - 2f; dd < dockEndD + 8f; dd += 4f) avoid.Add(new Vector4(f.x * dd, f.z * dd, 5f, 0));
        info.dockStart = origin + f * dockStartD + Vector3.up * (plazaH + 0.05f);
        info.dockEnd = origin + f * (dockEndD + 3f) + Vector3.up * (plazaH + 0.05f);
        info.hotspot = origin + f * (dockEndD + 30f) + r * rng.R(-8f, 8f);
        float h2a = da + Mathf.PI + rng.R(-0.6f, 0.6f);
        info.hot2 = origin + new Vector3(Mathf.Cos(h2a), 0, Mathf.Sin(h2a)) * (S.Shore(h2a) + 60f);

        // hut (+ two palms with a hammock), campfire
        BuildHut(solid, soft, S, Hc, f, r);
        BuildCampfire(solid, soft, S, Cc, root.transform, info, origin);

        // palms
        var palms = new List<Vector3>();
        Vector3 ham1 = Hc + r * 7.5f + f * 2.5f, ham2 = Hc + r * 7.5f - f * 3.5f;
        palms.Add(ham1); palms.Add(ham2);
        int want = rng.I(30, 38), tries = 0;
        while (palms.Count < want && tries++ < 4000)
        {
            float a = rng.R(0f, 6.2832f), s = rng.R(3f, 52f);
            if (rng.F() < 0.45f) s = rng.R(3f, 14f);      // many along the beach
            float d = S.Shore(a) - s;
            Vector3 p = new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            float h = S.H(p.x, p.z);
            if (h < 0.5f) continue;
            if (Blocked(avoid, p, 1f)) continue;
            bool near = false; foreach (var q in palms) if ((q - p).sqrMagnitude < 36f) { near = true; break; }
            if (near) continue;
            palms.Add(p);
        }
        for (int i = 0; i < palms.Count; i++)
        {
            Vector3 p = palms[i]; p.y = S.H(p.x, p.z);
            Vector3 outw = new Vector3(p.x, 0, p.z).normalized;
            if (i < 2) outw = (i == 0 ? f : -f) * 0.3f + r * 0.7f;
            BuildPalm(solid, soft, rng, p, outw.normalized, i < 2);
        }
        info.trees = palms.Count;
        // hammock between the two hut palms
        Hammock(soft, new Vector3(ham1.x, S.H(ham1.x, ham1.z) + 1.5f, ham1.z), new Vector3(ham2.x, S.H(ham2.x, ham2.z) + 1.5f, ham2.z));
        foreach (var q in palms) avoid.Add(new Vector4(q.x, q.z, 2.5f, 0));

        // rocks: beach + shallows, hill, lagoon rim
        int rockN = 0;
        for (int i = 0; i < 70 && rockN < 26; i++)
        {
            float a = rng.R(0f, 6.2832f); float s;
            int kind = i % 3;
            Vector3 p;
            if (kind == 0) { s = rng.R(-9f, 3f); float d = S.Shore(a) - s; p = new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d); }
            else if (kind == 1) { float rr = rng.R(0f, S.hr * 1.1f); p = new Vector3(S.hx + Mathf.Cos(a) * rr, 0, S.hz + Mathf.Sin(a) * rr); }
            else { float cu = Mathf.Cos(a) * S.lr1 * 1.1f, cv = Mathf.Sin(a) * S.lr2 * 1.1f; p = new Vector3(S.lx + S.lux * cu - S.luz * cv, 0, S.lz + S.luz * cu + S.lux * cv); }
            if (Blocked(avoid, p, 1.5f)) continue;
            float sz = kind == 1 ? rng.R(1.2f, 3.2f) : rng.R(0.6f, 1.9f);
            p.y = S.H(p.x, p.z) - sz * 0.25f;
            Quaternion q = Quaternion.Euler(rng.R(-15f, 15f), rng.R(0f, 360f), rng.R(-15f, 15f));
            Blob(solid, p, new Vector3(sz * rng.R(0.9f, 1.4f), sz * rng.R(0.55f, 0.85f), sz * rng.R(0.8f, 1.2f)), q, rng.F() < 0.6f ? C_ROCK : C_ROCK2, 6, 9, 0.22f, seed + 300 + i);
            avoid.Add(new Vector4(p.x, p.z, sz + 0.5f, 0));
            rockN++;
        }
        info.rocks = rockN;

        // bushes (+ a few flowers) and grass tufts
        int bushN = 0;
        for (int i = 0; i < 200 && bushN < 34; i++)
        {
            float a = rng.R(0f, 6.2832f), s = rng.R(10f, 60f), d = S.Shore(a) - s;
            Vector3 p = new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            float h = S.H(p.x, p.z);
            if (h < 1.3f || Blocked(avoid, p, 0.5f)) continue;
            int parts = rng.I(3, 6);
            int cell = rng.F() < 0.5f ? C_BUSH : C_BUSH2;
            for (int k = 0; k < parts; k++)
            {
                Vector3 o = new Vector3(rng.R(-0.9f, 0.9f), 0, rng.R(-0.9f, 0.9f));
                float sz = rng.R(0.55f, 1.0f);
                Vector3 c = p + o; c.y = S.H(c.x, c.z) + sz * 0.45f;
                Blob(soft, c, new Vector3(sz, sz * 0.75f, sz), Quaternion.Euler(0, rng.R(0, 360), 0), cell, 4, 7, 0.25f, seed + 500 + i * 7 + k);
            }
            if (rng.F() < 0.45f)
                for (int k = 0; k < 4; k++)
                {
                    Vector3 c = p + new Vector3(rng.R(-1f, 1f), 0, rng.R(-1f, 1f)); c.y = S.H(c.x, c.z) + rng.R(0.8f, 1.2f);
                    Blob(soft, c, Vector3.one * 0.13f, Quaternion.identity, rng.F() < 0.5f ? C_RED : C_YELLOW, 3, 5, 0f, 0);
                }
            avoid.Add(new Vector4(p.x, p.z, 1.6f, 0));
            bushN++;
        }
        info.bushes = bushN;
        for (int i = 0, tufts = 0; i < 900 && tufts < 220; i++)
        {
            float a = rng.R(0f, 6.2832f), s = rng.R(9f, 70f), d = S.Shore(a) - s;
            Vector3 p = new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            float h = S.H(p.x, p.z);
            if (h < 1.9f || h > 12f || Blocked(avoid, p, 0f)) continue;
            Tuft(soft, rng, new Vector3(p.x, h - 0.05f, p.z));
            tufts++;
        }

        Texture2D atlas = Atlas(seed); texs.Add(atlas);
        Material pmat = Lit("IslandProps", atlas, 0.1f); mats.Add(pmat);
        Mesh sm = solid.ToMesh("IslandSolid"), so = soft.ToMesh("IslandSoft");
        meshes.Add(sm); meshes.Add(so);
        var sgo = MeshGo("IslandSolid", root.transform, sm, pmat, worldLayer);
        var scol = sgo.AddComponent<MeshCollider>(); scol.sharedMesh = sm;
        MeshGo("IslandSoft", root.transform, so, pmat, 0);

        // ------------------------------------------------ shoreline foam
        if (sprite != null)
        {
            Texture2D ft = FoamTex(seed); texs.Add(ft);
            var fm = new Material(sprite) { name = "IslandFoam", mainTexture = ft, renderQueue = 3010 }; mats.Add(fm);
            Mesh foam = FoamMesh(S); meshes.Add(foam);
            var fgo = MeshGo("IslandFoam", root.transform, foam, fm, 0);
            var fr = fgo.GetComponent<MeshRenderer>(); fr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; fr.receiveShadows = false;
        }

        // ------------------------------------------------ flame materials
        Shader fsh = additive != null ? additive : sprite;
        if (fsh != null)
        {
            Texture2D fl = FlameTex(); texs.Add(fl);
            var flm = new Material(fsh) { name = "IslandFlame", mainTexture = fl, renderQueue = 3100 };
            if (flm.HasProperty("_TintColor")) flm.SetColor("_TintColor", new Color(0.9f, 0.6f, 0.35f, 0.7f));
            mats.Add(flm);
            Mesh fq = FlameMesh(); meshes.Add(fq);
            foreach (Transform tr in root.GetComponentsInChildren<Transform>())
                if (tr.name == "IslandFlameAnchor")
                {
                    var g = MeshGo("IslandFlame", tr, fq, flm, 0);
                    var rr = g.GetComponent<MeshRenderer>(); rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rr.receiveShadows = false;
                    info.flames.Add(g.transform); info.flameBase.Add(tr.localScale);
                }
        }

        // ------------------------------------------------ invisible sea floor + walls
        var floor = new GameObject("IslandSeaFloor"); floor.layer = worldLayer; floor.transform.SetParent(root.transform, false);
        var fb = floor.AddComponent<BoxCollider>(); fb.center = new Vector3(0, -SeaDepth - 1f, 0); fb.size = new Vector3(WallHalf * 2f + 20f, 2f, WallHalf * 2f + 20f);
        for (int k = 0; k < 4; k++)
        {
            var w = new GameObject("IslandWall"); w.layer = worldLayer; w.transform.SetParent(root.transform, false);
            var bc = w.AddComponent<BoxCollider>();
            bool xw = k < 2; float sgn = (k % 2 == 0) ? 1f : -1f;
            bc.center = xw ? new Vector3(sgn * WallHalf, 20f, 0) : new Vector3(0, 20f, sgn * WallHalf);
            bc.size = xw ? new Vector3(4f, 90f, WallHalf * 2f + 8f) : new Vector3(WallHalf * 2f + 8f, 90f, 4f);
        }

        info.meshes = meshes.ToArray(); info.textures = texs.ToArray(); info.materials = mats.ToArray();
        foreach (var m in meshes) { info.verts += m.vertexCount; info.tris += (int)(m.GetIndexCount(0) / 3); }
        info.buildMs = (Time.realtimeSinceStartup - t0) * 1000f;
        return info;
    }

    static bool Blocked(List<Vector4> avoid, Vector3 p, float extra)
    {
        foreach (var a in avoid) { float dx = p.x - a.x, dz = p.z - a.y, rr = a.z + extra; if (dx * dx + dz * dz < rr * rr) return true; }
        return false;
    }

    static GameObject MeshGo(string name, Transform parent, Mesh mesh, Material mat, int layer)
    {
        var g = new GameObject(name); g.layer = layer;
        g.transform.SetParent(parent, false);
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = g.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        return g;
    }

    // ------------------------------------------------ terrain colour (baked 1024x1024 over the whole terrain square)
    static Texture2D TerrainTex(Shape S, float[,] Hg, int N, Info info, int seed)
    {
        const int T = 1024;
        var tex = new Texture2D(T, T, TextureFormat.RGBA32, true); tex.name = "IslandGround"; tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[T * T];
        Color deep = new Color(0.50f, 0.47f, 0.36f), wet = new Color(0.80f, 0.71f, 0.52f), sand = new Color(0.95f, 0.88f, 0.69f),
              grass = new Color(0.30f, 0.55f, 0.20f), grass2 = new Color(0.44f, 0.64f, 0.25f), dirt = new Color(0.56f, 0.45f, 0.30f),
              rock = new Color(0.50f, 0.48f, 0.44f), path = new Color(0.78f, 0.67f, 0.47f),
              turq = new Color(0.45f, 0.82f, 0.76f), deepT = new Color(0.18f, 0.50f, 0.56f), floorSand = new Color(0.58f, 0.50f, 0.36f);
        Vector3 o = info.origin;
        Vector2 pl = new Vector2(info.plaza.x - o.x, info.plaza.z - o.z), hu = new Vector2(info.hut.x - o.x, info.hut.z - o.z), cf = new Vector2(info.campfire.x - o.x, info.campfire.z - o.z);
        float scale = (N - 1) / (float)T;
        for (int y = 0; y < T; y++)
        {
            float gy = (y + 0.5f) * scale; int i0 = Mathf.Min((int)gy, N - 2); float fy = gy - i0;
            float z = -Extent + gy * Step;
            for (int x = 0; x < T; x++)
            {
                float gx = (x + 0.5f) * scale; int j0 = Mathf.Min((int)gx, N - 2); float fx = gx - j0;
                float h = Mathf.Lerp(Mathf.Lerp(Hg[i0, j0], Hg[i0, j0 + 1], fx), Mathf.Lerp(Hg[i0 + 1, j0], Hg[i0 + 1, j0 + 1], fx), fy);
                float sx = Hg[i0, j0 + 1] - Hg[i0, j0], sz = Hg[i0 + 1, j0] - Hg[i0, j0];
                float slope = Mathf.Sqrt(sx * sx + sz * sz) / Step;
                float xw = -Extent + gx * Step;
                float n1 = VNoise(xw * 0.15f, z * 0.15f, seed + 11), n2 = Hash(x, y, seed + 12), n3 = VNoise(xw * 0.04f, z * 0.04f, seed + 13);
                Color c;
                if (h < -0.15f) { c = Color.Lerp(wet, turq, Smooth(-0.15f, -2.2f, h)); c = Color.Lerp(c, deepT, Smooth(-2.2f, -9f, h)); c = Color.Lerp(c, floorSand, Smooth(-11f, -17.5f, h)); }
                else if (h < 0.45f) c = Color.Lerp(wet, sand, Smooth(0.0f, 0.45f, h));
                else c = sand;
                float gline = 1.65f + (n1 - 0.5f) * 0.9f;
                if (h > gline)
                {
                    float gk = Smooth(gline, gline + 0.35f, h);
                    Color g = Color.Lerp(grass, grass2, n3);
                    if (n1 > 0.72f) g = Color.Lerp(g, dirt, (n1 - 0.72f) * 2.5f);
                    c = Color.Lerp(c, g, gk);
                }
                float rk = Smooth(0.55f, 0.85f, slope + (n1 - 0.5f) * 0.3f);
                if (h > 1f && rk > 0f) c = Color.Lerp(c, rock, rk);
                // sandy paths plaza -> hut / campfire
                Vector2 p2 = new Vector2(xw, z);
                float pd = Mathf.Min(SegDist(p2, pl, hu), SegDist(p2, pl, cf));
                float pw = 1.1f + (n1 - 0.5f) * 0.8f;
                if (pd < pw + 0.6f && h > 0.4f) c = Color.Lerp(c, path, 1f - Smooth(pw, pw + 0.6f, pd));
                // plaza: sandy
                float plz = (p2 - pl).magnitude;
                if (plz < 15f) c = Color.Lerp(c, Color.Lerp(sand, path, 0.35f), 1f - Smooth(11f, 15f, plz + (n1 - 0.5f) * 3f));
                float k = 0.9f + 0.2f * n2;
                px[y * T + x] = new Color(c.r * k, c.g * k, c.b * k, 1f);
            }
        }
        tex.SetPixels32(px); tex.Apply(true);
        return tex;
    }
    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
        return (p - (a + ab * t)).magnitude;
    }

    // ------------------------------------------------ palm tree
    static void BuildPalm(MB solid, MB soft, Rng rng, Vector3 b, Vector3 lean, bool straight)
    {
        float H = rng.R(7.5f, 11.5f), leanAmt = straight ? rng.R(0.4f, 1.0f) : rng.R(1.6f, 3.6f);
        int segs = 11, sides = 7;
        int barkCell = rng.F() < 0.7f ? C_BARK : C_BARK2;
        Vector3 prevC = Vector3.zero, top = Vector3.zero, topT = Vector3.up;
        Vector3[] C = new Vector3[segs + 1]; Vector3[] Tn = new Vector3[segs + 1]; float[] R = new float[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float t = (float)i / segs;
            C[i] = b + Vector3.up * (H * t - 0.6f) + lean * (leanAmt * t * t);
            float rr = Mathf.Lerp(0.34f, 0.19f, t);
            if (t < 0.12f) rr *= 1f + 0.7f * (1f - t / 0.12f) * (1f - t / 0.12f);
            R[i] = rr;
        }
        for (int i = 0; i <= segs; i++) Tn[i] = (C[Mathf.Min(i + 1, segs)] - C[Mathf.Max(i - 1, 0)]).normalized;
        for (int i = 0; i < segs; i++)
        {
            int baseI = solid.v.Count;
            for (int e = 0; e < 2; e++)
            {
                int ri = i + e;
                Vector3 s = Vector3.Cross(Tn[ri], Vector3.right).normalized, t2 = Vector3.Cross(Tn[ri], s);
                float bulge = e == 0 ? 1.06f : 0.97f;
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    Vector3 d = s * Mathf.Cos(a) + t2 * Mathf.Sin(a);
                    solid.V(C[ri] + d * R[ri] * bulge, d, UV(barkCell, (float)k / sides, e));
                }
            }
            for (int k = 0; k < sides; k++)
            {
                int a0 = baseI + k, a1 = a0 + 1, b0 = baseI + sides + 1 + k, b1 = b0 + 1;
                solid.Tri(a0, b1, b0); solid.Tri(a0, a1, b1);
            }
        }
        top = C[segs]; topT = Tn[segs];
        // crown knob + coconuts
        Blob(soft, top + topT * 0.1f, new Vector3(0.38f, 0.32f, 0.38f), Quaternion.identity, C_COCO, 4, 6, 0.15f, (int)(rng.Next() & 0xFFFF));
        int coco = rng.I(0, 5);
        for (int i = 0; i < coco; i++)
        {
            float a = rng.R(0f, 6.2832f);
            Vector3 c = top + new Vector3(Mathf.Cos(a) * 0.38f, -0.28f - rng.R(0f, 0.15f), Mathf.Sin(a) * 0.38f);
            Blob(soft, c, Vector3.one * 0.17f, Quaternion.identity, C_COCO, 4, 6, 0.08f, 0);
        }
        // fronds
        int fronds = rng.I(8, 12);
        float a0f = rng.R(0f, 6.2832f);
        int leafCell = rng.F() < 0.65f ? C_LEAF : C_LEAF2;
        for (int i = 0; i < fronds; i++)
        {
            float ang = a0f + i * 6.2832f / fronds + rng.R(-0.2f, 0.2f);
            bool dead = i == 0 && rng.F() < 0.5f;
            float L = rng.R(4.2f, 5.6f), elev = rng.R(0.35f, 0.75f), droop = dead ? rng.R(1.4f, 1.8f) : rng.R(0.7f, 1.1f), W = rng.R(0.85f, 1.15f);
            Frond(soft, top, new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)), L, elev, droop, W, dead ? C_DEAD : leafCell);
        }
    }

    static void Frond(MB m, Vector3 top, Vector3 hd, float L, float elev, float droop, float W, int cell)
    {
        const int S = 9;
        Vector3 side = Vector3.Cross(Vector3.up, hd).normalized;
        var P = new Vector3[S + 1];
        for (int j = 0; j <= S; j++)
        {
            float u = (float)j / S;
            P[j] = top + hd * (L * u) + Vector3.up * (L * (Mathf.Sin(elev) * u - droop * u * u));
        }
        for (int pass = 0; pass < 2; pass++)
        {
            int baseI = m.v.Count;
            for (int j = 0; j <= S; j++)
            {
                float u = (float)j / S;
                Vector3 T = (P[Mathf.Min(j + 1, S)] - P[Mathf.Max(j - 1, 0)]).normalized;
                Vector3 N = Vector3.Cross(T, side).normalized; if (N.y < 0f) N = -N;
                float w = W * Mathf.Sin(Mathf.PI * Mathf.Pow(Mathf.Max(u, 0.02f), 0.7f)) * (j == S ? 0f : 1f);
                Vector3 c = P[j] + N * (0.16f * w);
                Vector3 l = P[j] - side * w - N * (0.12f * w), rr = P[j] + side * w - N * (0.12f * w);
                Vector3 nn = pass == 0 ? N : -N;
                m.V(l, nn, UV(cell, 0f, u)); m.V(c, nn, UV(cell, 0.5f, u)); m.V(rr, nn, UV(cell, 1f, u));
            }
            for (int j = 0; j < S; j++)
            {
                int a = baseI + j * 3, b = a + 3;
                if (pass == 0) { m.Tri(a, b, b + 1); m.Tri(a, b + 1, a + 1); m.Tri(a + 1, b + 1, b + 2); m.Tri(a + 1, b + 2, a + 2); }
                else { m.Tri(a, b + 1, b); m.Tri(a, a + 1, b + 1); m.Tri(a + 1, b + 2, b + 1); m.Tri(a + 1, a + 2, b + 2); }
            }
        }
    }

    static void Tuft(MB m, Rng rng, Vector3 p)
    {
        int blades = rng.I(4, 7);
        int cell = rng.F() < 0.5f ? C_GRASS : C_GRASS2;
        for (int i = 0; i < blades; i++)
        {
            float a = rng.R(0f, 6.2832f), h = rng.R(0.35f, 0.7f), w = rng.R(0.06f, 0.1f), tilt = rng.R(0.05f, 0.3f);
            Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), s = new Vector3(-d.z, 0, d.x);
            Vector3 b0 = p + d * 0.08f - s * w, b1 = p + d * 0.08f + s * w, tip = p + d * (0.08f + tilt) + Vector3.up * h;
            Vector3 n = Vector3.Cross(b1 - b0, tip - b0).normalized; if (n.y < 0) n = -n;
            n = (n + Vector3.up).normalized;
            int i0 = m.V(b0, n, UV(cell, 0, 0)), i1 = m.V(b1, n, UV(cell, 1, 0)), i2 = m.V(tip, n, UV(cell, 0.5f, 1));
            m.Tri(i0, i2, i1); m.Tri(i0, i1, i2);
        }
    }

    static void Hammock(MB m, Vector3 a, Vector3 b)
    {
        const int S = 12;
        Vector3 ab = b - a; Vector3 side = Vector3.Cross(Vector3.up, ab.normalized).normalized;
        for (int pass = 0; pass < 2; pass++)
        {
            int baseI = m.v.Count;
            for (int j = 0; j <= S; j++)
            {
                float u = (float)j / S;
                float sag = 0.75f * 4f * u * (1f - u);
                Vector3 c = a + ab * u + Vector3.down * sag;
                float w = 0.15f + 0.45f * Mathf.Sin(Mathf.PI * u);
                Vector3 nn = pass == 0 ? Vector3.up : Vector3.down;
                m.V(c - side * w + Vector3.up * 0.08f * Mathf.Sin(Mathf.PI * u), nn, UV(C_CLOTH, 0f, u));
                m.V(c + side * w + Vector3.up * 0.08f * Mathf.Sin(Mathf.PI * u), nn, UV(C_CLOTH, 1f, u));
            }
            for (int j = 0; j < S; j++)
            {
                int i0 = baseI + j * 2;
                if (pass == 0) { m.Tri(i0, i0 + 2, i0 + 3); m.Tri(i0, i0 + 3, i0 + 1); }
                else { m.Tri(i0, i0 + 3, i0 + 2); m.Tri(i0, i0 + 1, i0 + 3); }
            }
        }
        // ropes to the trunks
        Cyl(m, a, a + ab * 0.03f + Vector3.down * 0.1f, 0.025f, 0.025f, 4, C_ROPE, false);
        Cyl(m, b, b - ab * 0.03f + Vector3.down * 0.1f, 0.025f, 0.025f, 4, C_ROPE, false);
    }

    // ------------------------------------------------ dock (planks are visual; the walkable deck is a box collider)
    static void BuildDock(MB solid, Transform root, Shape S, Vector3 f, Vector3 r, float d0, float d1, float topY, int layer, Info info, Vector3 origin)
    {
        float width = 4f, pd = 0.30f, gap = 0.05f, th = 0.09f;
        Quaternion q = Quaternion.LookRotation(f, Vector3.up);
        for (float d = d0; d < d1; d += pd + gap)
        {
            Vector3 c = f * (d + pd * 0.5f) + Vector3.up * (topY - th * 0.5f);
            Box(solid, c, new Vector3(width, th, pd), q, Mathf.Abs(Hash((int)(d * 10f), 3, 5) - 0.5f) < 0.3f ? C_PLANK : C_PLANK2);
        }
        // stringers + posts
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            Vector3 c = f * ((d0 + d1) * 0.5f) + r * (sgn * (width * 0.5f - 0.25f)) + Vector3.up * (topY - th - 0.14f);
            Box(solid, c, new Vector3(0.18f, 0.26f, d1 - d0), q, C_PLANK2);
        }
        for (float d = d0 + 1f; d <= d1 + 0.01f; d += 3.5f)
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector3 p = f * d + r * (sgn * (width * 0.5f - 0.1f));
                float gy = S.H(p.x, p.z);
                if (gy > topY - 0.4f) continue;
                Cyl(solid, new Vector3(p.x, gy - 0.6f, p.z), new Vector3(p.x, topY - 0.05f, p.z), 0.15f, 0.14f, 7, C_LOG, false);
            }
        // T platform at the end
        float pw = 10f, pl = 6.5f;
        Quaternion qr = Quaternion.LookRotation(r, Vector3.up);
        for (float k = 0f; k < pl; k += pd + gap)
        {
            Vector3 c = f * (d1 + k + pd * 0.5f) + Vector3.up * (topY - th * 0.5f);
            Box(solid, c, new Vector3(pw, th, pd), q, Mathf.Abs(Hash((int)(k * 10f), 9, 5) - 0.5f) < 0.3f ? C_PLANK : C_PLANK2);
        }
        for (int a = -1; a <= 1; a++)
            for (int b = 0; b <= 1; b++)
            {
                Vector3 p = f * (d1 + (b == 0 ? 0.2f : pl - 0.2f)) + r * (a * (pw * 0.5f - 0.2f));
                float gy = S.H(p.x, p.z);
                Cyl(solid, new Vector3(p.x, gy - 0.6f, p.z), new Vector3(p.x, topY + (a != 0 ? 0.7f : -0.05f), p.z), 0.17f, 0.15f, 7, C_LOG, a != 0);
            }
        // crate + barrel on the platform
        Vector3 cr = f * (d1 + pl - 1.2f) + r * (pw * 0.5f - 1.4f) + Vector3.up * (topY + 0.4f);
        Box(solid, cr, new Vector3(0.8f, 0.8f, 0.8f), q * Quaternion.Euler(0, 17f, 0), C_PLANK2);
        Vector3 br = f * (d1 + pl - 1.3f) - r * (pw * 0.5f - 1.3f);
        Cyl(solid, br + Vector3.up * topY, br + Vector3.up * (topY + 1.0f), 0.38f, 0.38f, 10, C_PLANK, true, true);
        // tiki torches at the dock root and the platform corners
        Torch(solid, root, f * (d0 + 0.6f) + r * (width * 0.5f + 0.5f), S, info, origin);
        Torch(solid, root, f * (d0 + 0.6f) - r * (width * 0.5f + 0.5f), S, info, origin);
        Torch(solid, root, f * (d1 + pl - 0.25f) + r * (pw * 0.5f - 0.25f), S, info, origin, topY);
        Torch(solid, root, f * (d1 + pl - 0.25f) - r * (pw * 0.5f - 0.25f), S, info, origin, topY);
        // walkable colliders (flat, exactly at the plank tops)
        var deck = new GameObject("IslandDockDeck"); deck.layer = layer; deck.transform.SetParent(root, false);
        deck.transform.localPosition = f * ((d0 + d1) * 0.5f) + Vector3.up * (topY - 0.25f); deck.transform.localRotation = q;
        var bc = deck.AddComponent<BoxCollider>(); bc.size = new Vector3(width, 0.5f, d1 - d0);
        var plat = new GameObject("IslandDockPlatform"); plat.layer = layer; plat.transform.SetParent(root, false);
        plat.transform.localPosition = f * (d1 + pl * 0.5f) + Vector3.up * (topY - 0.25f); plat.transform.localRotation = q;
        var pc = plat.AddComponent<BoxCollider>(); pc.size = new Vector3(pw, 0.5f, pl);
    }

    static void Torch(MB solid, Transform root, Vector3 p, Shape S, Info info, Vector3 origin, float baseY = float.NaN)
    {
        float gy = float.IsNaN(baseY) ? S.H(p.x, p.z) : baseY;
        Vector3 b = new Vector3(p.x, gy - 0.3f, p.z), t = new Vector3(p.x, gy + 2.1f, p.z);
        Cyl(solid, b, t, 0.07f, 0.06f, 6, C_LOG, false);
        Cyl(solid, t, t + Vector3.up * 0.32f, 0.12f, 0.16f, 6, C_DARK, true);
        Flame(root, t + Vector3.up * 0.32f, 0.45f, 9f, 1.6f, info);
    }

    static void Flame(Transform root, Vector3 local, float size, float lightRange, float lightIntensity, Info info)
    {
        var a = new GameObject("IslandFlameAnchor"); a.transform.SetParent(root, false);
        a.transform.localPosition = local; a.transform.localScale = new Vector3(size, size * 1.8f, size);
        var lg = new GameObject("IslandFireLight"); lg.transform.SetParent(root, false); lg.transform.localPosition = local + Vector3.up * 0.3f;
        var l = lg.AddComponent<Light>(); l.type = LightType.Point; l.color = new Color(1f, 0.62f, 0.3f); l.range = lightRange; l.intensity = lightIntensity; l.shadows = LightShadows.None;
        info.lights.Add(l); info.lightBase.Add(lightIntensity);
    }

    static Mesh FlameMesh()
    {
        var m = new MB();
        for (int k = 0; k < 3; k++)
        {
            float a = k * Mathf.PI / 3f;
            Vector3 s = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.5f, n = new Vector3(-s.z, 0, s.x).normalized;
            int i0 = m.V(-s, n, new Vector2(0, 0)), i1 = m.V(s, n, new Vector2(1, 0)), i2 = m.V(s + Vector3.up, n, new Vector2(1, 1)), i3 = m.V(-s + Vector3.up, n, new Vector2(0, 1));
            m.Tri(i0, i2, i1); m.Tri(i0, i3, i2); m.Tri(i0, i1, i2); m.Tri(i0, i2, i3);
        }
        return m.ToMesh("IslandFlame");
    }

    // ------------------------------------------------ hut: raised plank floor, posts, plank walls with a door, thatch gable roof
    static void BuildHut(MB solid, MB soft, Shape S, Vector3 c, Vector3 f, Vector3 r)
    {
        float gy = S.H(c.x, c.z);
        Vector3 toPlaza = f;                          // door faces the sea / plaza side
        Quaternion q = Quaternion.LookRotation(toPlaza, Vector3.up);
        Vector3 fw = toPlaza, rt = Vector3.Cross(Vector3.up, fw).normalized;
        float W = 5.6f, D = 5.0f, floorY = gy + 0.6f, wallH = 2.3f;
        Box(solid, c + Vector3.up * (floorY - 0.2f), new Vector3(W, 0.4f, D), q, C_PLANK2);
        // steps
        Box(solid, c + fw * (D * 0.5f + 0.35f) + Vector3.up * (gy + 0.2f), new Vector3(1.6f, 0.4f, 0.7f), q, C_PLANK);
        // corner posts
        for (int a = -1; a <= 1; a += 2) for (int b = -1; b <= 1; b += 2)
            {
                Vector3 p = c + rt * (a * (W * 0.5f - 0.12f)) + fw * (b * (D * 0.5f - 0.12f));
                Box(solid, new Vector3(p.x, gy + 1.5f, p.z), new Vector3(0.24f, 3.4f, 0.24f), q, C_LOG);
            }
        // walls: horizontal planks
        int rows = 6; float rh = wallH / rows;
        for (int i = 0; i < rows; i++)
        {
            float y = floorY + rh * (i + 0.5f);
            int cell = (i % 2 == 0) ? C_PLANK : C_PLANK2;
            Box(solid, c - fw * (D * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(W - 0.3f, rh * 0.92f, 0.1f), q, cell);                       // back
            Box(solid, c + rt * (W * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(0.1f, rh * 0.92f, D - 0.3f), q, cell);                       // right
            Box(solid, c - rt * (W * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(0.1f, rh * 0.92f, D - 0.3f), q, cell);                       // left
            if (y < floorY + 2.0f)
            {   // front with a 1.3 m door gap
                float seg = (W - 0.3f - 1.3f) * 0.5f;
                Box(solid, c + fw * (D * 0.5f - 0.08f) + rt * (0.65f + seg * 0.5f) + Vector3.up * (y - c.y), new Vector3(seg, rh * 0.92f, 0.1f), q, cell);
                Box(solid, c + fw * (D * 0.5f - 0.08f) - rt * (0.65f + seg * 0.5f) + Vector3.up * (y - c.y), new Vector3(seg, rh * 0.92f, 0.1f), q, cell);
            }
            else Box(solid, c + fw * (D * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(W - 0.3f, rh * 0.92f, 0.1f), q, cell);
        }
        // gable roof (ridge along rt)
        float roofY = floorY + wallH, rise = 1.6f, over = 0.7f;
        float halfD = D * 0.5f + over, slopeL = Mathf.Sqrt(halfD * halfD + rise * rise);
        float ang = Mathf.Atan2(rise, halfD) * Mathf.Rad2Deg;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 mid = c + fw * (s * halfD * 0.5f) + Vector3.up * (roofY + rise * 0.5f - c.y + 0.1f);
            Quaternion qs = q * Quaternion.Euler(s * ang, 0, 0);
            Box(solid, mid, new Vector3(W + 2f * over, 0.22f, slopeL), qs, C_THATCH);
        }
        Box(solid, c + Vector3.up * (roofY + rise + 0.15f - c.y), new Vector3(W + 2f * over + 0.2f, 0.22f, 0.3f), q, C_THATCH);
        // gable triangles (thatch-coloured planks, stepped)
        for (int i = 0; i < 4; i++)
        {
            float y = roofY + rise * (i + 0.5f) / 4f, w = D * (1f - (i + 0.5f) / 4f);
            Box(solid, c + rt * (W * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(0.1f, rise / 4f, w), q, C_PLANK2);
            Box(solid, c - rt * (W * 0.5f - 0.08f) + Vector3.up * (y - c.y), new Vector3(0.1f, rise / 4f, w), q, C_PLANK2);
        }
        // inside: a table + a stool, a striped mat
        Box(solid, c - fw * 1.2f + rt * 1.2f + Vector3.up * (floorY + 0.75f - c.y), new Vector3(1.4f, 0.08f, 0.8f), q, C_PLANK);
        Box(solid, c - fw * 1.2f + rt * 1.2f + Vector3.up * (floorY + 0.37f - c.y), new Vector3(0.12f, 0.74f, 0.12f), q, C_LOG);
        Box(soft, c + fw * 0.4f + Vector3.up * (floorY + 0.01f - c.y), new Vector3(2.2f, 0.02f, 1.4f), q, C_CLOTH);
    }

    // ------------------------------------------------ campfire: stone ring, crossed logs, flames + light, log benches
    static void BuildCampfire(MB solid, MB soft, Shape S, Vector3 c, Transform root, Info info, Vector3 origin)
    {
        float gy = S.H(c.x, c.z);
        Vector3 g = new Vector3(c.x, gy, c.z);
        for (int i = 0; i < 9; i++)
        {
            float a = i * Mathf.PI * 2f / 9f;
            Blob(solid, g + new Vector3(Mathf.Cos(a) * 0.95f, 0.08f, Mathf.Sin(a) * 0.95f), new Vector3(0.26f, 0.2f, 0.24f), Quaternion.Euler(0, a * 57f, 0), C_STONE, 4, 6, 0.2f, 900 + i);
        }
        Box(soft, g + Vector3.up * 0.02f, new Vector3(1.5f, 0.04f, 1.5f), Quaternion.Euler(0, 20f, 0), C_CHAR);
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.PI * 0.5f + 0.3f;
            Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            Cyl(soft, g + d * 0.65f + Vector3.up * 0.05f, g + Vector3.up * 0.55f - d * 0.05f, 0.08f, 0.06f, 6, C_LOG, true, true);
        }
        Flame(root, g + Vector3.up * 0.15f, 1.1f, 16f, 3f, info);
        // benches
        for (int i = 0; i < 3; i++)
        {
            float a = i * Mathf.PI * 2f / 3f + 0.5f;
            Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), s = new Vector3(-d.z, 0, d.x);
            Vector3 m = g + d * 3.0f;
            float y = S.H(m.x, m.z) + 0.28f;
            Cyl(solid, new Vector3(m.x, y, m.z) - s * 1.1f, new Vector3(m.x, y, m.z) + s * 1.1f, 0.28f, 0.28f, 8, C_LOG, true, true);
        }
    }

    // ------------------------------------------------ shoreline foam ring (outermost waterline found by bisection)
    static Mesh FoamMesh(Shape S)
    {
        const int SEG = 320;
        var m = new MB();
        var rad = new float[SEG + 1];
        for (int k = 0; k <= SEG; k++)
        {
            float a = k * Mathf.PI * 2f / SEG;
            Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            float lo = 0f, hi = Extent - 2f;               // H(hi) < 0 (sea), walk inwards to the first dry point
            float rr = hi;
            for (float x = hi; x > 5f; x -= 1f) { if (S.H(d.x * x, d.z * x) > 0f) { lo = x; hi = x + 1f; break; } }
            for (int it = 0; it < 12; it++) { float mid = (lo + hi) * 0.5f; if (S.H(d.x * mid, d.z * mid) > 0f) lo = mid; else hi = mid; }
            rr = (lo + hi) * 0.5f; rad[k] = rr;
            float u = k / (float)SEG * 40f;
            m.V(d * (rr - 1.6f) + Vector3.up * 0.06f, Vector3.up, new Vector2(u, 1f));
            m.V(d * (rr + 2.6f) + Vector3.up * 0.06f, Vector3.up, new Vector2(u, 0f));
        }
        for (int k = 0; k < SEG; k++) { if (Mathf.Abs(rad[k + 1] - rad[k]) > 3f) continue; int i0 = k * 2; m.Tri(i0, i0 + 2, i0 + 3); m.Tri(i0, i0 + 3, i0 + 1); }
        return m.ToMesh("IslandFoam");
    }

    // ================================================================ distant ocean: a ring from the lake sheet's square edge out to 3.8 km,
    // drawn with the lake's own material, so the sea reaches the horizon instead of ending 1 km out. Call after the lake
    // sheet (FishingWater / FishingClientWater) is centred on the island; cheap to call every tick (follows its height).
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
            var m = new MB();
            for (int k = 0; k <= SEG; k++)
            {
                float a = k * Mathf.PI * 2f / SEG;
                Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                float ti = half / Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
                Vector3 pi = d * ti, po = d * OceanOuter;
                m.V(pi, Vector3.up, new Vector2(pi.x / (2f * half) + 0.5f, pi.z / (2f * half) + 0.5f));
                m.V(po, Vector3.up, new Vector2(po.x / (2f * half) + 0.5f, po.z / (2f * half) + 0.5f));
            }
            for (int k = 0; k < SEG; k++) { int i0 = k * 2; m.Tri(i0, i0 + 2, i0 + 3); m.Tri(i0, i0 + 3, i0 + 1); }
            info.oceanMesh = m.ToMesh("IslandOcean");
            info.ocean = new GameObject("IslandOcean");
            info.ocean.transform.SetParent(info.root.transform, false);
            info.ocean.AddComponent<MeshFilter>().sharedMesh = info.oceanMesh;
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

    // ================================================================ per frame
    public static void Animate(Info info, float t)
    {
        if (info == null) return;
        for (int i = 0; i < info.flames.Count; i++)
        {
            Transform tr = info.flames[i]; if (tr == null) continue;
            float k = 1f + 0.12f * Mathf.Sin(t * 9.1f + i * 1.7f) + 0.07f * Mathf.Sin(t * 15.3f + i);
            tr.localScale = new Vector3(1f / k, k, 1f / k);
            tr.localRotation = Quaternion.Euler(0f, t * 40f + i * 50f, 0f);
        }
        for (int i = 0; i < info.lights.Count; i++)
        {
            Light l = info.lights[i]; if (l == null) continue;
            l.intensity = info.lightBase[i] * (0.85f + 0.1f * Mathf.Sin(t * 11f + i * 2.1f) + 0.05f * Mathf.Sin(t * 23f + i));
        }
    }

    public static bool OnIsland(Info info, Vector3 p)
    {
        if (info == null || info.root == null) return false;
        float dx = p.x - info.origin.x, dz = p.z - info.origin.z;
        return Mathf.Abs(dx) < WallHalf + 20f && Mathf.Abs(dz) < WallHalf + 20f && p.y > info.origin.y - SeaDepth - 30f && p.y < info.origin.y + 200f;
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
