#define AQUA_CLIENT
#define HAVE_FISH3D
// PERSONAL AQUARIUMS for the Risk of Rain 2 fishing game.
// Every player has a "house": an enclosed room (floor, walls, ceiling, warm lights, door frame, sofa, rug, plants,
// a name sign) far above the playable map of the CURRENT stage, with a big glass fish tank showing every fish that
// player has caught (one swimming fish per species, more for big catches, max 30; rare ones get a little spotlight;
// the Old Boot lies on the sand). Houses are built on demand and vanish with the stage.
//
// Chat commands (any player):  !house  -> your own house      !visit <name>  -> that player's house (partial name)
//                              !home / !back  -> back to where you were on the map
//
// Compiled twice from this one file:
//  * HOST build (class Aquarium<N>, references RoR2.dll): reads the commands, owns the house positions, reads the
//    fishing mod's public static `collections` (newest Fishing<N>), builds the houses locally, broadcasts
//      [FT] HOUSE|<owner>|<x>|<y>|<z>|<species:count;...>|<visitor>
//    and teleports bodies server-side (TeleportHelper.TeleportBody, synced to everyone).
//    A guest is only teleported after their FISHING.exe answers "[FT] AQREADY|<owner>" (their game has built the room
//    and its colliders - guest movement is client-authoritative, so without the floor on their machine they'd fall).
//  * GUEST build (-define:AQUA_CLIENT, class AquariumClient, inside FISHING.exe, reflection only): builds the same
//    room + tank from [FT] HOUSE lines; when the line names the local player as visitor it answers [FT] AQREADY.
// All Unity objects are made on the main thread. Disable: set the static field  enabled = false  (houses hide).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
#if !AQUA_CLIENT
using RoR2;
using UnityEngine.Networking;
#endif

#if AQUA_CLIENT
public static class AquariumClient
#else
public static class AQUACLASS
#endif
{
#if AQUA_CLIENT
    const string Me = "AquariumClient";
#else
    const string Me = "AQUACLASS";
#endif
    public static bool enabled = true;
    public static bool running;
    public static int version = 1;
    public static int housesBuilt, fishMade;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_aquarium.log");
    static int errors;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Me + ": " + s + Environment.NewLine); } catch { } }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
        catch { return new Type[0]; }
    }
    static object Get(object o, string name)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(name, All);
        return f != null ? f.GetValue(o) : null;
    }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }
    static bool Dead(object o) { return o == null || ((o is UnityEngine.Object) && (UnityEngine.Object)o == null); }
    static string ChatPlain(string raw) { return Regex.Replace(raw ?? "", "<[^>]*>", "").Trim(); }
    public static string CleanName(string s) { return (s ?? "").Replace("|", "/").Replace(";", ",").Replace(":", "."); }

    // =====================================================================================================
    // House geometry (all sizes in metres; origin = floor centre, +z = towards the tank / back wall)
    // =====================================================================================================
    public const float RoomW = 20f, RoomD = 14f, RoomH = 7f, Wall = 0.5f;
    public static readonly Vector3 SpawnLocal = new Vector3(0f, 0.2f, -5.2f);           // just inside the door
    const float TankW = 9f, TankD = 2.4f, TankH = 3.2f, StandH = 0.9f, TankZ = 4.6f;
    const int MaxFish = 30;

    public class Swimmer
    {
        public Transform t; public GameObject model;
        public Vector3 center, amp, freq, phase; public float speed, len; public bool still;
        public Vector3 last; public bool hasLast;
    }
    public class House
    {
        public string owner, fish = "\u0000"; public Vector3 pos;
        public GameObject root, fishRoot; public Bounds bounds;
        public readonly List<Swimmer> swimmers = new List<Swimmer>();
        public readonly List<Transform> bubbles = new List<Transform>(), plants = new List<Transform>();
        public readonly List<Renderer> labels = new List<Renderer>();
        public TextMesh board, sub;
        public bool labelsOn = true;
    }
    public static readonly Dictionary<string, House> houses = new Dictionary<string, House>();
    static string lastScene = "";

    // ---------------------------------------------------------------- materials
    static Shader lit, trans;
    static readonly Dictionary<int, Material> matCache = new Dictionary<int, Material>();
    static Font font;
    static void Shaders()
    {
        if (lit != null) return;
        foreach (string s in new[] { "Standard", "Legacy Shaders/Diffuse", "Hopoo Games/Deferred/Standard", "Unlit/Color", "Sprites/Default" })
        {
            Shader sh = Shader.Find(s);
            if (sh != null && sh.isSupported) { lit = sh; break; }
        }
        trans = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? lit;
        try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        if (font == null) try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        Log("shaders: lit=" + (lit == null ? "none" : lit.name) + " trans=" + (trans == null ? "none" : trans.name) + " font=" + (font != null));
    }
    static Material Mat(Color c, float gloss = 0.15f)
    {
        int key = (Mathf.RoundToInt(c.r * 255) << 24) ^ (Mathf.RoundToInt(c.g * 255) << 16) ^ (Mathf.RoundToInt(c.b * 255) << 8) ^ Mathf.RoundToInt(gloss * 100);
        Material m;
        if (matCache.TryGetValue(key, out m) && m != null) return m;
        m = new Material(lit);
        m.color = c;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        matCache[key] = m;
        return m;
    }
    static Material Glow(Color c)   // unlit (lamp bulbs, light strip)
    {
        var m = new Material(trans); m.color = c; m.renderQueue = 2450; return m;
    }
    static Material Clear(Color c, int queue)
    {
        var m = new Material(trans); m.color = c; m.renderQueue = queue; return m;
    }

    static int worldLayer = -1;
    static int WorldLayer()
    {
        if (worldLayer < 0) { worldLayer = LayerMask.NameToLayer("World"); if (worldLayer < 0) worldLayer = 11; }
        return worldLayer;
    }

    // a primitive in the house's local frame; solid = keeps its collider on the World layer (walkable / blocks)
    static GameObject Prim(PrimitiveType type, Transform parent, Vector3 lp, Vector3 scale, Material mat, bool solid, string name = null)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        if (name != null) g.name = name;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lp;
        g.transform.localScale = scale;
        var r = g.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        if (solid) g.layer = WorldLayer();
        else { var c = g.GetComponent<Collider>(); if (c != null) UnityEngine.Object.Destroy(c); }
        return g;
    }
    static GameObject Box(Transform p, Vector3 lp, Vector3 size, Material m, bool solid, string name = null) { return Prim(PrimitiveType.Cube, p, lp, size, m, solid, name); }

    static TextMesh Text(Transform parent, Vector3 lp, string text, float size, Color c, TextAnchor anchor, House h)
    {
        var g = new GameObject("AquaText");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lp;
        var tm = g.AddComponent<TextMesh>();
        tm.richText = true;
        if (font != null) { tm.font = font; g.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
        tm.fontSize = 64; tm.characterSize = size;
        tm.anchor = anchor;
        tm.alignment = anchor == TextAnchor.UpperLeft || anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : TextAlignment.Center;
        tm.color = c;
        tm.text = text;
        h.labels.Add(g.GetComponent<MeshRenderer>());
        return tm;
    }

    static Light PointLight(Transform parent, Vector3 lp, Color c, float range, float intensity)
    {
        var g = new GameObject("AquaLight");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lp;
        var l = g.AddComponent<Light>();
        l.type = LightType.Point; l.color = c; l.range = range; l.intensity = intensity; l.shadows = LightShadows.None;
        return l;
    }

    // ---------------------------------------------------------------- species data (same 14 as the fishing mod)
    static readonly string[] Species = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish", "Bass", "Salmon", "Pufferfish",
        "Swordfish", "Golden Koi", "Anglerfish", "Moon Eel", "Ghost Fish", "Old Boot" };
    static readonly float[] Length = { 0.3f, 0.5f, 0.7f, 0.3f, 0.9f, 0.8f, 1.0f, 0.5f, 2.0f, 0.7f, 1.0f, 1.8f, 1.2f, 0.45f };
    static readonly float[] Speed = { 1.6f, 1.1f, 1.3f, 1.2f, 0.8f, 1.0f, 1.4f, 0.6f, 2.2f, 0.7f, 0.7f, 0.9f, 1.1f, 0f };
    static readonly Color[] Tint = {
        new Color(0.75f, 0.8f, 0.85f), new Color(0.55f, 0.65f, 0.25f), new Color(0.6f, 0.5f, 0.45f), new Color(1f, 0.5f, 0.1f),
        new Color(0.45f, 0.4f, 0.35f), new Color(0.3f, 0.5f, 0.3f), new Color(0.95f, 0.55f, 0.45f), new Color(0.9f, 0.8f, 0.4f),
        new Color(0.3f, 0.45f, 0.8f), new Color(1f, 0.8f, 0.15f), new Color(0.35f, 0.3f, 0.4f), new Color(0.75f, 0.85f, 1f),
        new Color(0.85f, 0.95f, 1f), new Color(0.35f, 0.25f, 0.15f) };
    static readonly HashSet<string> Rare = new HashSet<string> { "Swordfish", "Golden Koi", "Anglerfish", "Moon Eel", "Ghost Fish" };
    static int SpeciesIndex(string s) { for (int i = 0; i < Species.Length; i++) if (string.Equals(Species[i], s, StringComparison.OrdinalIgnoreCase)) return i; return -1; }

    public static string FishString(IDictionary<string, int> c)
    {
        var parts = new List<string>();
        if (c != null)
        {
            foreach (string sp in Species) { int n; if (c.TryGetValue(sp, out n) && n > 0) parts.Add(sp + ":" + n); }
            foreach (var kv in c) if (SpeciesIndex(kv.Key) < 0 && kv.Value > 0 && kv.Key != "Poop") parts.Add(CleanName(kv.Key) + ":" + kv.Value);
        }
        return string.Join(";", parts.ToArray());
    }
    static List<KeyValuePair<string, int>> ParseFish(string s)
    {
        var l = new List<KeyValuePair<string, int>>();
        foreach (string part in (s ?? "").Split(';'))
        {
            int k = part.LastIndexOf(':'); int n;
            if (k > 0 && int.TryParse(part.Substring(k + 1), out n) && n > 0) l.Add(new KeyValuePair<string, int>(part.Substring(0, k), n));
        }
        return l;
    }

    // =====================================================================================================
    // Building
    // =====================================================================================================
    public static House BuildHouse(string owner, Vector3 pos, string fish)
    {
        Shaders();
        lastScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;   // houses belong to the current scene
        House h;
        if (houses.TryGetValue(owner, out h) && h.root != null && (h.pos - pos).sqrMagnitude < 0.01f)
        {
            if (h.fish != fish) FillTank(h, fish);
            return h;
        }
        if (h != null && h.root != null) UnityEngine.Object.Destroy(h.root);
        h = new House { owner = owner, pos = pos };
        houses[owner] = h;
        var root = new GameObject("Aquarium_" + owner);
        h.root = root;
        root.transform.position = pos;
        Transform R = root.transform;
        h.bounds = new Bounds(pos + Vector3.up * RoomH * 0.5f, new Vector3(RoomW + 2 * Wall, RoomH + 2 * Wall, RoomD + 2 * Wall));

        Material wood = Mat(new Color(0.55f, 0.36f, 0.2f), 0.3f), wall = Mat(new Color(0.93f, 0.87f, 0.74f), 0.05f),
                 ceil = Mat(new Color(0.96f, 0.94f, 0.9f), 0.05f), trim = Mat(new Color(0.42f, 0.26f, 0.14f), 0.25f),
                 skirt = Mat(new Color(0.35f, 0.22f, 0.12f), 0.2f);
        // shell (walkable / blocking, World layer)
        Box(R, new Vector3(0, -Wall / 2, 0), new Vector3(RoomW + 2 * Wall, Wall, RoomD + 2 * Wall), wood, true, "Floor");
        var roof = Box(R, new Vector3(0, RoomH + Wall / 2, 0), new Vector3(RoomW + 2 * Wall, Wall, RoomD + 2 * Wall), ceil, true, "Ceiling");
        roof.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // let some daylight in
        Box(R, new Vector3(0, RoomH / 2, RoomD / 2 + Wall / 2), new Vector3(RoomW + 2 * Wall, RoomH, Wall), wall, true, "WallBack");
        Box(R, new Vector3(0, RoomH / 2, -RoomD / 2 - Wall / 2), new Vector3(RoomW + 2 * Wall, RoomH, Wall), wall, true, "WallFront");
        Box(R, new Vector3(RoomW / 2 + Wall / 2, RoomH / 2, 0), new Vector3(Wall, RoomH, RoomD), wall, true, "WallRight");
        Box(R, new Vector3(-RoomW / 2 - Wall / 2, RoomH / 2, 0), new Vector3(Wall, RoomH, RoomD), wall, true, "WallLeft");
        // skirting boards
        Box(R, new Vector3(0, 0.12f, RoomD / 2 - 0.04f), new Vector3(RoomW, 0.24f, 0.08f), skirt, false);
        Box(R, new Vector3(0, 0.12f, -RoomD / 2 + 0.04f), new Vector3(RoomW, 0.24f, 0.08f), skirt, false);
        Box(R, new Vector3(RoomW / 2 - 0.04f, 0.12f, 0), new Vector3(0.08f, 0.24f, RoomD), skirt, false);
        Box(R, new Vector3(-RoomW / 2 + 0.04f, 0.12f, 0), new Vector3(0.08f, 0.24f, RoomD), skirt, false);
        // floor planks (thin stripes)
        Material plank = Mat(new Color(0.47f, 0.3f, 0.16f), 0.3f);
        for (float x = -RoomW / 2 + 1.25f; x < RoomW / 2; x += 1.25f) Box(R, new Vector3(x, 0.003f, 0), new Vector3(0.04f, 0.006f, RoomD), plank, false);

        // door frame on the front wall (closed door: outside is open sky)
        Box(R, new Vector3(-1.05f, 1.25f, -RoomD / 2 + 0.06f), new Vector3(0.22f, 2.5f, 0.14f), trim, false);
        Box(R, new Vector3(1.05f, 1.25f, -RoomD / 2 + 0.06f), new Vector3(0.22f, 2.5f, 0.14f), trim, false);
        Box(R, new Vector3(0, 2.6f, -RoomD / 2 + 0.06f), new Vector3(2.32f, 0.22f, 0.14f), trim, false);
        Box(R, new Vector3(0, 1.25f, -RoomD / 2 + 0.03f), new Vector3(1.9f, 2.5f, 0.05f), Mat(new Color(0.5f, 0.3f, 0.17f), 0.35f), false, "Door");
        Prim(PrimitiveType.Sphere, R, new Vector3(0.7f, 1.15f, -RoomD / 2 + 0.1f), Vector3.one * 0.1f, Mat(new Color(0.85f, 0.7f, 0.3f), 0.8f), false);
        // doormat
        Box(R, new Vector3(0, 0.01f, -RoomD / 2 + 0.9f), new Vector3(1.6f, 0.02f, 0.9f), Mat(new Color(0.45f, 0.35f, 0.2f), 0f), false);

        // rug + sofa facing the tank
        Box(R, new Vector3(0, 0.012f, -0.3f), new Vector3(7.5f, 0.02f, 4.6f), Mat(new Color(0.55f, 0.12f, 0.12f), 0f), false, "Rug");
        Box(R, new Vector3(0, 0.016f, -0.3f), new Vector3(6.7f, 0.02f, 3.8f), Mat(new Color(0.8f, 0.62f, 0.3f), 0f), false);
        Box(R, new Vector3(0, 0.02f, -0.3f), new Vector3(6.3f, 0.02f, 3.4f), Mat(new Color(0.55f, 0.12f, 0.12f), 0f), false);
        Material sofa = Mat(new Color(0.18f, 0.42f, 0.45f), 0.1f), cushion = Mat(new Color(0.22f, 0.5f, 0.53f), 0.1f);
        float sz = -1.6f;
        Box(R, new Vector3(0, 0.25f, sz), new Vector3(3.4f, 0.5f, 1.0f), sofa, true, "Sofa");
        Box(R, new Vector3(-0.8f, 0.58f, sz + 0.05f), new Vector3(1.55f, 0.16f, 0.85f), cushion, false);
        Box(R, new Vector3(0.8f, 0.58f, sz + 0.05f), new Vector3(1.55f, 0.16f, 0.85f), cushion, false);
        Box(R, new Vector3(0, 0.85f, sz - 0.42f), new Vector3(3.4f, 0.9f, 0.25f), sofa, true);
        Box(R, new Vector3(-1.78f, 0.55f, sz), new Vector3(0.25f, 0.7f, 1.0f), sofa, true);
        Box(R, new Vector3(1.78f, 0.55f, sz), new Vector3(0.25f, 0.7f, 1.0f), sofa, true);
        // little table between sofa and tank
        Box(R, new Vector3(0, 0.42f, 0.9f), new Vector3(1.6f, 0.08f, 0.8f), trim, true, "Table");
        foreach (float tx in new[] { -0.7f, 0.7f }) foreach (float tz in new[] { 0.6f, 1.2f })
                Box(R, new Vector3(tx, 0.19f, tz), new Vector3(0.08f, 0.38f, 0.08f), trim, false);
        // fish food can on the table
        Prim(PrimitiveType.Cylinder, R, new Vector3(0.3f, 0.55f, 0.9f), new Vector3(0.14f, 0.09f, 0.14f), Mat(new Color(0.9f, 0.3f, 0.2f), 0.4f), false);

        // potted plants in the corners
        foreach (float px in new[] { -RoomW / 2 + 0.9f, RoomW / 2 - 0.9f })
            foreach (float pz in new[] { RoomD / 2 - 0.9f, -RoomD / 2 + 0.9f })
                Plant(R, new Vector3(px, 0, pz), h);

        // ceiling lamps (unlit bulbs + warm point lights)
        foreach (float lx in new[] { -5f, 5f })
        {
            Prim(PrimitiveType.Cylinder, R, new Vector3(lx, RoomH - 0.25f, -1f), new Vector3(0.04f, 0.25f, 0.04f), trim, false);
            Prim(PrimitiveType.Sphere, R, new Vector3(lx, RoomH - 0.6f, -1f), Vector3.one * 0.35f, Glow(new Color(1f, 0.92f, 0.7f)), false);
            PointLight(R, new Vector3(lx, RoomH - 1f, -1f), new Color(1f, 0.82f, 0.6f), 15f, 1.7f);
        }
        PointLight(R, new Vector3(0, RoomH - 1.2f, -4.5f), new Color(1f, 0.85f, 0.65f), 9f, 0.9f);

        // name sign above the tank
        Box(R, new Vector3(0, 5.75f, RoomD / 2 - 0.08f), new Vector3(6.2f, 1.0f, 0.12f), trim, false, "Sign");
        Box(R, new Vector3(0, 5.75f, RoomD / 2 - 0.15f), new Vector3(5.9f, 0.8f, 0.04f), Mat(new Color(0.96f, 0.9f, 0.75f), 0.1f), false);
        var title = Text(R, new Vector3(0, 5.77f, RoomD / 2 - 0.19f), owner + "'s Aquarium", 0.065f, new Color(0.25f, 0.13f, 0.05f), TextAnchor.MiddleCenter, h);
        title.fontStyle = FontStyle.Bold;
        // collection board on the wall left of the tank
        Box(R, new Vector3(-7.4f, 3.0f, RoomD / 2 - 0.08f), new Vector3(3.6f, 4.0f, 0.1f), trim, false, "Board");
        Box(R, new Vector3(-7.4f, 3.0f, RoomD / 2 - 0.14f), new Vector3(3.3f, 3.7f, 0.04f), Mat(new Color(0.12f, 0.2f, 0.18f), 0.05f), false);
        h.board = Text(R, new Vector3(-8.9f, 4.7f, RoomD / 2 - 0.18f), "", 0.034f, new Color(0.95f, 0.95f, 0.9f), TextAnchor.UpperLeft, h);
        // a picture frame on the right wall of the back
        Box(R, new Vector3(7.4f, 3.3f, RoomD / 2 - 0.08f), new Vector3(2.6f, 2.0f, 0.1f), trim, false);
        Box(R, new Vector3(7.4f, 3.3f, RoomD / 2 - 0.14f), new Vector3(2.3f, 1.7f, 0.04f), Mat(new Color(0.35f, 0.6f, 0.85f), 0.1f), false);
        Box(R, new Vector3(7.4f, 2.85f, RoomD / 2 - 0.17f), new Vector3(2.3f, 0.6f, 0.03f), Mat(new Color(0.2f, 0.45f, 0.3f), 0.1f), false);
        h.sub = Text(R, new Vector3(7.4f, 4.6f, RoomD / 2 - 0.18f), "Gone fishin'", 0.03f, new Color(0.25f, 0.13f, 0.05f), TextAnchor.MiddleCenter, h);

        BuildTank(R, h);
        FillTank(h, fish);
        housesBuilt++;
        Log("built house for " + owner + " at " + pos + " (" + h.swimmers.Count + " fish)");
        return h;
    }

    static void Plant(Transform R, Vector3 lp, House h)
    {
        Prim(PrimitiveType.Cylinder, R, lp + new Vector3(0, 0.35f, 0), new Vector3(0.7f, 0.35f, 0.7f), Mat(new Color(0.7f, 0.35f, 0.2f), 0.2f), true);
        Prim(PrimitiveType.Cylinder, R, lp + new Vector3(0, 0.71f, 0), new Vector3(0.6f, 0.02f, 0.6f), Mat(new Color(0.3f, 0.2f, 0.1f), 0f), false);
        Material leaf = Mat(new Color(0.2f, 0.55f, 0.2f), 0.2f), leaf2 = Mat(new Color(0.3f, 0.65f, 0.25f), 0.2f);
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f;
            var g = Prim(PrimitiveType.Sphere, R, lp + new Vector3(0, 1.3f, 0), new Vector3(0.25f, 1.2f, 0.12f), i % 2 == 0 ? leaf : leaf2, false);
            g.transform.localRotation = Quaternion.Euler(0, a, 0) * Quaternion.Euler(22f, 0, 0);
            g.transform.localPosition = lp + new Vector3(0, 0.75f, 0) + g.transform.localRotation * new Vector3(0, 0.55f, 0);
        }
    }

    static void BuildTank(Transform R, House h)
    {
        Transform T = new GameObject("Tank").transform;
        T.SetParent(R, false);
        T.localPosition = new Vector3(0, 0, TankZ);
        Material cab = Mat(new Color(0.2f, 0.14f, 0.1f), 0.4f), frame = Mat(new Color(0.08f, 0.08f, 0.09f), 0.6f);
        // stand (solid)
        Box(T, new Vector3(0, StandH / 2, 0), new Vector3(TankW + 0.3f, StandH, TankD + 0.3f), cab, true, "Stand");
        for (int i = -1; i <= 1; i += 2) Box(T, new Vector3(i * TankW / 4, StandH / 2, -TankD / 2 - 0.16f), new Vector3(TankW / 2 - 0.4f, StandH - 0.25f, 0.02f), Mat(new Color(0.27f, 0.19f, 0.13f), 0.4f), false);
        float y0 = StandH, yc = StandH + TankH / 2;
        // glass panes (solid so nobody walks in) - transparent
        Material glass = Clear(new Color(0.75f, 0.9f, 1f, 0.12f), 3002);
        Box(T, new Vector3(0, yc, -TankD / 2), new Vector3(TankW, TankH, 0.05f), glass, true, "GlassFront");
        Box(T, new Vector3(0, yc, TankD / 2), new Vector3(TankW, TankH, 0.05f), glass, true, "GlassBack");
        Box(T, new Vector3(-TankW / 2, yc, 0), new Vector3(0.05f, TankH, TankD), glass, true, "GlassLeft");
        Box(T, new Vector3(TankW / 2, yc, 0), new Vector3(0.05f, TankH, TankD), glass, true, "GlassRight");
        Box(T, new Vector3(0, y0 + TankH + 0.06f, 0), new Vector3(TankW + 0.1f, 0.12f, TankD + 0.1f), frame, true, "Lid");
        // frame edges
        foreach (float sx in new[] { -1f, 1f }) foreach (float sz in new[] { -1f, 1f })
                Box(T, new Vector3(sx * TankW / 2, yc, sz * TankD / 2), new Vector3(0.1f, TankH, 0.1f), frame, false);
        foreach (float sz in new[] { -1f, 1f }) Box(T, new Vector3(0, y0 + 0.05f, sz * TankD / 2), new Vector3(TankW, 0.1f, 0.1f), frame, false);
        foreach (float sx in new[] { -1f, 1f }) Box(T, new Vector3(sx * TankW / 2, y0 + 0.05f, 0), new Vector3(0.1f, 0.1f, TankD), frame, false);
        // water volume (slightly lower than the lid) + surface
        float waterTop = y0 + TankH - 0.25f;
        Box(T, new Vector3(0, (y0 + waterTop) / 2, 0), new Vector3(TankW - 0.08f, waterTop - y0, TankD - 0.08f), Clear(new Color(0.15f, 0.5f, 0.75f, 0.16f), 3001), false, "Water");
        Box(T, new Vector3(0, waterTop, 0), new Vector3(TankW - 0.08f, 0.01f, TankD - 0.08f), Clear(new Color(0.6f, 0.85f, 1f, 0.25f), 3003), false, "WaterTop");
        // sand, pebbles, rocks
        Box(T, new Vector3(0, y0 + 0.12f, 0), new Vector3(TankW - 0.1f, 0.24f, TankD - 0.1f), Mat(new Color(0.86f, 0.76f, 0.52f), 0.05f), false, "Sand");
        var rng = new System.Random(h.owner.GetHashCode());
        Material rock = Mat(new Color(0.45f, 0.45f, 0.48f), 0.3f), rock2 = Mat(new Color(0.55f, 0.5f, 0.45f), 0.3f);
        for (int i = 0; i < 7; i++)
        {
            float s = 0.25f + (float)rng.NextDouble() * 0.45f;
            var g = Prim(PrimitiveType.Sphere, T, new Vector3(-TankW / 2 + 0.6f + (float)rng.NextDouble() * (TankW - 1.2f), y0 + 0.22f, -0.2f + (float)rng.NextDouble() * (TankD / 2 - 0.2f)),
                new Vector3(s * 1.4f, s * 0.8f, s), i % 2 == 0 ? rock : rock2, false);
            g.transform.localRotation = Quaternion.Euler(0, (float)rng.NextDouble() * 180f, 0);
        }
        // a little arch (sunken ruin)
        Material ruin = Mat(new Color(0.6f, 0.58f, 0.5f), 0.1f);
        Box(T, new Vector3(2.6f, y0 + 0.65f, 0.55f), new Vector3(0.22f, 0.9f, 0.22f), ruin, false);
        Box(T, new Vector3(3.4f, y0 + 0.65f, 0.55f), new Vector3(0.22f, 0.9f, 0.22f), ruin, false);
        Box(T, new Vector3(3.0f, y0 + 1.18f, 0.55f), new Vector3(1.1f, 0.18f, 0.26f), ruin, false);
        // swaying plants
        Material weed = Mat(new Color(0.15f, 0.6f, 0.3f), 0.3f), weed2 = Mat(new Color(0.3f, 0.7f, 0.2f), 0.3f);
        for (int i = 0; i < 9; i++)
        {
            float x = -TankW / 2 + 0.4f + (float)rng.NextDouble() * (TankW - 0.8f), z = 0.2f + (float)rng.NextDouble() * (TankD / 2 - 0.45f);
            float tall = 0.7f + (float)rng.NextDouble() * 1.3f;
            var pivot = new GameObject("Weed").transform;
            pivot.SetParent(T, false);
            pivot.localPosition = new Vector3(x, y0 + 0.2f, z);
            var g = Prim(PrimitiveType.Capsule, pivot, new Vector3(0, tall / 2, 0), new Vector3(0.09f, tall / 2, 0.09f), i % 2 == 0 ? weed : weed2, false);
            var g2 = Prim(PrimitiveType.Capsule, pivot, new Vector3(0.1f, tall * 0.35f, 0), new Vector3(0.07f, tall * 0.35f, 0.07f), weed2, false);
            g2.transform.localRotation = Quaternion.Euler(0, 0, -14f);
            h.plants.Add(pivot);
        }
        // bubbler + bubbles
        Prim(PrimitiveType.Cylinder, T, new Vector3(-3.4f, y0 + 0.3f, 0.6f), new Vector3(0.18f, 0.12f, 0.18f), frame, false);
        Material bub = Clear(new Color(0.9f, 0.97f, 1f, 0.55f), 3004);
        for (int i = 0; i < 14; i++)
        {
            var g = Prim(PrimitiveType.Sphere, T, new Vector3(-3.4f, y0 + 0.4f, 0.6f), Vector3.one * (0.05f + 0.06f * (float)rng.NextDouble()), bub, false);
            h.bubbles.Add(g.transform);
        }
        // tank light (cool) under the lid
        PointLight(T, new Vector3(0, y0 + TankH - 0.1f, -0.2f), new Color(0.7f, 0.9f, 1f), 6.5f, 1.4f);
        Box(T, new Vector3(0, y0 + TankH - 0.02f, 0), new Vector3(TankW - 0.6f, 0.03f, 0.3f), Glow(new Color(0.85f, 0.95f, 1f)), false);
    }

    // (re)fills the tank with this collection; the room itself (and its colliders) stays untouched
    public static void FillTank(House h, string fish)
    {
        h.fish = fish;
        if (h.fishRoot != null) UnityEngine.Object.Destroy(h.fishRoot);
        h.swimmers.Clear();
        h.fishRoot = new GameObject("TankFish");
        h.fishRoot.transform.SetParent(h.root.transform, false);
        h.fishRoot.transform.localPosition = new Vector3(0, 0, TankZ);
        Transform F = h.fishRoot.transform;
        var list = ParseFish(fish);

        // how many swimmers per species: 1, 2 for 2+, 3 for 4+, 4 for 8+ ... capped at MaxFish in total
        var want = new int[list.Count];
        int total = 0;
        for (int i = 0; i < list.Count; i++) { int n = list[i].Value; want[i] = n >= 8 ? 4 : n >= 4 ? 3 : n >= 2 ? 2 : 1; total += want[i]; }
        while (total > MaxFish)
        {
            int best = 0; for (int i = 1; i < want.Length; i++) if (want[i] > want[best]) best = i;
            if (want[best] <= 1) break;
            want[best]--; total--;
        }
        var rng = new System.Random(h.owner.GetHashCode() ^ 0x5eed);
        float y0 = StandH + 0.45f, y1 = StandH + TankH - 0.55f;
        int bootN = 0;
        for (int i = 0; i < list.Count; i++)
        {
            string sp = list[i].Key;
            int si = SpeciesIndex(sp);
            float len = si >= 0 ? Length[si] : 0.6f;
            float show = Mathf.Clamp(len * 0.75f, 0.3f, 1.25f);
            for (int k = 0; k < want[i]; k++)
            {
                GameObject model = MakeFish(sp, show);
                if (model == null) continue;
                fishMade++;
                var s = new Swimmer { model = model, t = model.transform, len = show };
                s.t.SetParent(F, false);
                if (sp == "Old Boot")
                {
                    s.still = true;
                    s.t.localPosition = new Vector3(-1.2f + bootN * 0.6f, StandH + 0.32f, -0.3f);
                    s.t.localRotation = Quaternion.Euler(0, 30f + 50f * bootN, 80f);
                    bootN++;
                }
                else
                {
                    float hx = TankW / 2 - 0.35f - show * 0.5f, hz = TankD / 2 - 0.3f - show * 0.2f;
                    float cy = Mathf.Lerp(y0, y1, (float)rng.NextDouble());
                    s.center = new Vector3((float)(rng.NextDouble() - 0.5) * 0.6f, cy, (float)(rng.NextDouble() - 0.5) * 0.3f);
                    s.amp = new Vector3(hx - Mathf.Abs(s.center.x), Mathf.Min(0.35f, Mathf.Min(cy - y0, y1 - cy) + 0.05f), Mathf.Max(0.05f, hz - Mathf.Abs(s.center.z)));
                    float sp0 = si >= 0 ? Speed[si] : 1f;
                    s.speed = sp0;
                    s.freq = new Vector3(0.10f + 0.06f * (float)rng.NextDouble(), 0.25f + 0.2f * (float)rng.NextDouble(), 0.17f + 0.1f * (float)rng.NextDouble()) * Mathf.Lerp(0.6f, 1.3f, sp0 / 2.2f);
                    s.phase = new Vector3((float)rng.NextDouble() * 6.28f, (float)rng.NextDouble() * 6.28f, (float)rng.NextDouble() * 6.28f);
                    if (Rare.Contains(sp))
                    {
                        Color c = si >= 0 ? Tint[si] : Color.white;
                        PointLight(s.t, new Vector3(0, 0.45f, 0), Color.Lerp(c, Color.white, 0.4f), 1.8f, 2.2f);
                    }
                }
                h.swimmers.Add(s);
            }
        }
        // collection board text
        var sb = new StringBuilder();
        int caught = 0;
        foreach (var kv in list) caught += kv.Value;
        sb.Append("<b>COLLECTION</b>\n");
        if (list.Count == 0) sb.Append("\nNo fish yet!\nGo catch some.");
        foreach (var kv in list)
        {
            string col = kv.Key == "Old Boot" ? "#b08860" : Rare.Contains(kv.Key) ? "#ffd75e" : "#e8f2ff";
            sb.Append("<color=" + col + ">" + kv.Key + "</color>  x" + kv.Value + "\n");
        }
        if (h.board != null) h.board.text = sb.ToString();
        if (h.sub != null) h.sub.text = caught + " fish caught\n" + list.Count + " / " + Species.Length + " kinds";
        h.labelsOn = true;
    }

    // ---------------------------------------------------------------- fish models
    static GameObject MakeFish(string sp, float len)
    {
#if HAVE_FISH3D
        try
        {
            if (Fish3D.log == null) Fish3D.log = Log;
            GameObject g = Fish3D.Create(sp, len);
            if (g != null) { StripColliders(g); return g; }
        }
        catch (Exception e) { if (errors++ < 10) Log("Fish3D.Create(" + sp + ") failed: " + e.Message); }
#endif
        return CapsuleFish(sp, len);
    }
    static void StripColliders(GameObject g) { foreach (var c in g.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(c); }

    // prototype fish (used when Fish3D isn't available): faces +z
    static GameObject CapsuleFish(string sp, float len)
    {
        int si = SpeciesIndex(sp);
        Color c = si >= 0 ? Tint[si] : new Color(0.6f, 0.6f, 0.7f);
        var root = new GameObject("Fish_" + sp);
        if (sp == "Old Boot")
        {
            Box(root.transform, new Vector3(0, 0.12f * len / 0.45f, 0), new Vector3(0.18f, 0.3f, 0.2f) * (len / 0.45f), Mat(c, 0.3f), false);
            Box(root.transform, new Vector3(0, 0, 0.12f * len / 0.45f), new Vector3(0.18f, 0.1f, 0.4f) * (len / 0.45f), Mat(c, 0.3f), false);
            return root;
        }
        var body = Prim(PrimitiveType.Sphere, root.transform, Vector3.zero, new Vector3(len * 0.32f, len * 0.42f, len), Mat(c, 0.5f), false);
        var tail = new GameObject("Tail").transform;
        tail.SetParent(root.transform, false);
        tail.localPosition = new Vector3(0, 0, -len * 0.45f);
        var fin = Box(tail, new Vector3(0, 0, -len * 0.12f), new Vector3(len * 0.04f, len * 0.35f, len * 0.25f), Mat(c * 0.8f, 0.3f), false);
        Material eye = Mat(Color.black, 0.9f);
        Prim(PrimitiveType.Sphere, root.transform, new Vector3(len * 0.12f, len * 0.07f, len * 0.3f), Vector3.one * len * 0.07f, eye, false);
        Prim(PrimitiveType.Sphere, root.transform, new Vector3(-len * 0.12f, len * 0.07f, len * 0.3f), Vector3.one * len * 0.07f, eye, false);
        return root;
    }

    // =====================================================================================================
    // Per-frame animation (main thread)
    // =====================================================================================================
    static readonly List<string> deadKeys = new List<string>();
    public static void Frame()
    {
        if (houses.Count == 0) return;
        try
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != lastScene) { lastScene = scene; ClearHouses("scene " + scene); return; }
            float t = Time.time, dt = Time.deltaTime;
            Vector3 cam = Vector3.zero; bool haveCam = false;
            foreach (Camera c in Camera.allCameras) if (c != null && c.enabled) { cam = c.transform.position; haveCam = true; if (c.CompareTag("MainCamera")) break; }
            deadKeys.Clear();
            foreach (var kv in houses)
            {
                House h = kv.Value;
                if (h.root == null) { deadKeys.Add(kv.Key); continue; }
                if (h.root.activeSelf != enabled) h.root.SetActive(enabled);
                if (!enabled) continue;
                // only animate / show labels when someone (this machine's camera) is near the house
                bool near = haveCam && (h.bounds.SqrDistance(cam) < 30f * 30f);
                if (near != h.labelsOn) { h.labelsOn = near; foreach (var r in h.labels) if (r != null) r.enabled = near; }
                if (!near) continue;
                foreach (var s in h.swimmers)
                {
                    if (s.t == null || s.still) continue;
                    Vector3 p = s.center + new Vector3(s.amp.x * Mathf.Sin(s.freq.x * t + s.phase.x), s.amp.y * Mathf.Sin(s.freq.y * t + s.phase.y), s.amp.z * Mathf.Sin(s.freq.z * t + s.phase.z));
                    s.t.localPosition = p;
                    if (s.hasLast)
                    {
                        Vector3 v = p - s.last;
                        if (v.sqrMagnitude > 1e-8f)
                        {
                            Vector3 flat = v; flat.y *= 0.5f;
                            Quaternion want = Quaternion.LookRotation(flat.normalized, Vector3.up) * FishFacing;
                            s.t.localRotation = Quaternion.Slerp(s.t.localRotation, want, Mathf.Clamp01(dt * 4f));
                        }
                    }
                    s.last = p; s.hasLast = true;
                    AnimateFish(s, t);
                }
                for (int i = 0; i < h.bubbles.Count; i++)
                {
                    Transform b = h.bubbles[i];
                    if (b == null) continue;
                    float cyc = (t * 0.45f + i * 0.137f) % 1f;
                    b.localPosition = new Vector3(-3.4f + 0.06f * Mathf.Sin(t * 3f + i), StandH + 0.4f + cyc * (TankH - 0.7f), 0.6f + 0.05f * Mathf.Cos(t * 2.3f + i * 2f));
                }
                for (int i = 0; i < h.plants.Count; i++)
                    if (h.plants[i] != null) h.plants[i].localRotation = Quaternion.Euler(6f * Mathf.Sin(t * 0.9f + i), 0, 7f * Mathf.Sin(t * 0.7f + i * 1.7f));
            }
            foreach (string k in deadKeys) houses.Remove(k);
        }
        catch (Exception e) { if (errors++ < 20) Log("frame error: " + e); }
    }

#if HAVE_FISH3D
    static readonly Quaternion FishFacing = Quaternion.Euler(0f, -90f, 0f);   // Fish3D heads point along +X
    static void AnimateFish(Swimmer s, float t) { try { Fish3D.Animate(s.model, t, s.speed); } catch { } }
#else
    static readonly Quaternion FishFacing = Quaternion.identity;
    static void AnimateFish(Swimmer s, float t)
    {
        Transform tail = s.t.Find("Tail");
        if (tail != null) tail.localRotation = Quaternion.Euler(0, 25f * Mathf.Sin(t * (4f + 4f * s.speed)), 0);
    }
#endif

    public static void ClearHouses(string why)
    {
        int n = 0;
        foreach (var h in houses.Values) if (h.root != null) { UnityEngine.Object.Destroy(h.root); n++; }
        houses.Clear();
        if (n > 0 || why != null) Log("houses cleared (" + why + ", " + n + " destroyed)");
    }
    // called by newer versions
    public static void Cleanup() { enabled = false; ClearHouses("cleanup"); }

    public static House HouseAt(Vector3 p)
    {
        foreach (var h in houses.Values) if (h.root != null && h.bounds.Contains(p)) return h;
        return null;
    }

    // "[FT] HOUSE|owner|x|y|z|fish|visitor" -> builds/updates; returns the visitor field ("" if none) or null if not a HOUSE line
    public static string HandleHouseLine(string plain, out House built)
    {
        built = null;
        int k = plain.IndexOf("[FT] HOUSE|");
        if (k < 0) return null;
        string[] p = plain.Substring(k + 11).Split('|');
        if (p.Length < 5) return null;
        float x, y, z;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out x) || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out y)
            || !float.TryParse(p[3], System.Globalization.NumberStyles.Float, inv, out z)) return null;
        built = BuildHouse(p[0], new Vector3(x, y, z), p[4]);
        return p.Length > 5 ? p[5] : "";
    }
    public static string HouseLine(House h, string visitor)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "[FT] HOUSE|" + CleanName(h.owner) + "|" + h.pos.x.ToString("0.00", inv) + "|" + h.pos.y.ToString("0.00", inv) + "|" + h.pos.z.ToString("0.00", inv)
            + "|" + h.fish + "|" + CleanName(visitor ?? "");
    }

#if AQUA_CLIENT
    // =====================================================================================================
    // GUEST (inside FISHING.exe): reflection only
    // =====================================================================================================
    static Assembly ror2;
    static Type chatType, nuType, uObj, chatBoxType;
    static Action fixedHook;
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine;
    static bool chatDirty;

    public static void Run()
    {
        try
        {
            foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "RoR2") ror2 = a;
            Assembly mine = MethodBase.GetCurrentMethod().DeclaringType.Assembly;
            foreach (var asm in FishingScope.Assemblies())
            {
                if (asm == mine) continue;
                foreach (Type t in SafeTypes(asm))
                {
                    int hn;
                    bool hostCopy = t.Name.StartsWith("Aquarium") && int.TryParse(t.Name.Substring(8), out hn);
                    if (!hostCopy && t.Name != Me) continue;
                    FieldInfo r = t.GetField("running", BindingFlags.Public | BindingFlags.Static), en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    FieldInfo vf = t.GetField("version", BindingFlags.Public | BindingFlags.Static);
                    bool live = r != null && en != null && (bool)r.GetValue(null) && (bool)en.GetValue(null);
                    if (hostCopy && live) { Log("host detected (" + t.Name + " is running) - guest aquarium not started"); return; }
                    if (!hostCopy)
                    {
                        int ov = vf != null ? Convert.ToInt32(vf.GetValue(null)) : 0;
                        if (live && ov >= version) { Log("already running - nothing changed"); return; }
                        MethodInfo c = t.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static);
                        if (en != null) en.SetValue(null, false);
                        Log("disabled older " + t.FullName + " v" + ov + (c != null ? " (cleanup on main thread)" : ""));
                    }
                }
            }
            chatType = ror2.GetType("RoR2.Chat");
            nuType = ror2.GetType("RoR2.NetworkUser");
            chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            uObj = typeof(UnityEngine.Object);
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc != null && cc.EventHandlerType == typeof(Action)) { Action a = OnChatChanged; cc.AddEventHandler(null, a); }
            fixedHook = FirstTick;
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, fixedHook);
            running = true;
            Log("installed v" + version + " (chatHook=" + (cc != null) + ")");
        }
        catch (Exception e) { Log("install error: " + e); }
    }

    static GameObject driver;
    static void FirstTick()
    {
        try
        {
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).RemoveEventHandler(null, fixedHook);
            if (driver != null) return;
            driver = new GameObject("AquariumClientDriver");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            driver.AddComponent<AquariumClientBehaviour>();
            lastScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Log("main-thread driver created");
        }
        catch (Exception e) { Log("first tick error: " + e); }
    }

    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            string p = ChatPlain(line);
            if (p.StartsWith("[FT] HOUSE|")) { lock (chatQueue) chatQueue.Add(p); chatDirty = true; }
            else if (p.Contains(": [FT] AQREADY|")) chatDirty = true;
        }
        catch { }
    }

    static PropertyInfo netActiveProp; static bool netTried;
    static bool HostNow()   // NetworkServer.active by reflection: true on the host's PC (also when FISHING.exe was run there at the menu)
    {
        try
        {
            if (!netTried)
            {
                netTried = true;
                foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("UnityEngine.Networking.NetworkServer"); if (t != null) { netActiveProp = t.GetProperty("active", All); break; } }
            }
            return netActiveProp != null && (bool)netActiveProp.GetValue(null, null);
        }
        catch { return false; }
    }

    public static void Update()
    {
        if (!enabled) { foreach (var h in houses.Values) if (h.root != null && h.root.activeSelf) h.root.SetActive(false); return; }
        if (HostNow()) { if (houses.Count > 0) ClearHouses("this game is the host"); lock (chatQueue) chatQueue.Clear(); chatDirty = false; return; }
        string[] lines = null;
        lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
        if (lines != null)
            foreach (string l in lines)
            {
                try
                {
                    string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    if (scene != lastScene) { lastScene = scene; ClearHouses("scene " + scene); }
                    House h;
                    string visitor = HandleHouseLine(l, out h);
                    if (h == null) continue;
                    string me = LocalName();
                    if (!string.IsNullOrEmpty(visitor) && me != null && CleanName(me) == visitor)
                    {
                        Say("[FT] AQREADY|" + h.owner);
                        Log("ready to visit " + h.owner + "'s aquarium");
                    }
                }
                catch (Exception e) { if (errors++ < 20) Log("house line error: " + e); }
            }
        if (chatDirty) HideTaggedLines();
        Frame();
    }

    static string LocalName()
    {
        try
        {
            object lu = ror2.GetType("RoR2.LocalUserManager").GetMethod("GetFirstLocalUser", All).Invoke(null, null);
            object nu = Get(lu, "currentNetworkUser");
            return Dead(nu) ? null : Convert.ToString(Get(nu, "userName"));
        }
        catch { return null; }
    }
    static void Say(string msg)
    {
        try
        {
            object lu = ror2.GetType("RoR2.LocalUserManager").GetMethod("GetFirstLocalUser", All).Invoke(null, null);
            object nu = Get(lu, "currentNetworkUser");
            if (Dead(nu)) { Log("say failed: no local network user"); return; }
            string esc = msg.Replace("\\", "\\\\").Replace("\"", "\\\"");
            object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
            foreach (MethodInfo m in console.GetType().GetMethods(All))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "SubmitCmd" && ps.Length == 3 && ps[0].ParameterType == nuType)
                { m.Invoke(console, new object[] { nu, "say \"" + esc + "\"", false }); return; }
            }
            Log("say failed: SubmitCmd not found");
        }
        catch (Exception e) { Log("say error: " + (e.InnerException ?? e).Message); }
    }
    // hide this feature's machine lines from the guest's chat box (next frame, after every handler saw them)
    static void HideTaggedLines()
    {
        chatDirty = false;
        try
        {
            List<string> log = GetStatic(chatType, "log") as List<string>;
            if (log == null) return;
            int removed = 0;
            for (int i = log.Count - 1; i >= 0; i--)
            {
                string p = ChatPlain(log[i]);
                if (p.StartsWith("[FT] HOUSE|") || p.Contains(": [FT] AQREADY|")) { log.RemoveAt(i); removed++; }
            }
            if (removed == 0 || chatBoxType == null) return;
            Array boxes = (Array)uObj.GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            foreach (object b in boxes)
            {
                try
                {
                    if (!(bool)Get(b, "enabled")) continue;
                    chatBoxType.GetMethod("BuildChat", All).Invoke(b, null);
                    MethodInfo sc = chatBoxType.GetMethod("ScrollToBottom", All);
                    if (sc != null) sc.Invoke(b, null);
                }
                catch { }
            }
        }
        catch { }
    }
#else
    // =====================================================================================================
    // HOST (JV94's game)
    // =====================================================================================================
    class Trip { public string visitor, owner; public float deadline; }
    static readonly Dictionary<string, Vector3> returnPos = new Dictionary<string, Vector3>();
    static readonly Dictionary<string, Trip> pending = new Dictionary<string, Trip>();
    static readonly List<string> chatQueue = new List<string>();
    static readonly Dictionary<string, float> cooldown = new Dictionary<string, float>();
    static string lastChatLine;
    static Stage lastStage;
    static float nextRefresh;
    static int ticks;

    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }
    static void Info(string text) { Say("<color=#7fd8ff>" + text + "</color>"); }

    public static void Run()
    {
        Type mine = MethodBase.GetCurrentMethod().DeclaringType;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t == mine || !t.Name.StartsWith("Aquarium") || !int.TryParse(t.Name.Substring(8), out n)) continue;
                FieldInfo f = t.GetField("enabled", All);
                if (f != null) f.SetValue(null, false);
                MethodInfo c = t.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static);
                if (c != null) RoR2Application.onNextUpdate += () => { try { c.Invoke(null, null); } catch { } };
                Log("disabled older " + t.Name);
            }
        Chat.onChatChanged += OnChatChanged;
        RoR2Application.onFixedUpdate += Tick;
        RoR2Application.onUpdate += Frame;
        running = true;
        Log("host installed v" + version + " (commands: !house, !visit <name>, !home, !back)");
    }

    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            var log = Chat.readOnlyLog;
            if (log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            string p = ChatPlain(line);
            if (p.Contains(": !") || p.Contains(": [FT] AQREADY|")) lock (chatQueue) chatQueue.Add(p);
        }
        catch { }
    }

    // newest Fishing<N> collections
    static Type fishingType; static float fishingCheckedAt = -100f;
    static IDictionary Collections()
    {
        if (fishingType == null || Time.time - fishingCheckedAt > 30f)
        {
            fishingCheckedAt = Time.time;
            Type best = null; int bestN = -1;
            foreach (var a in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(a))
                {
                    int n;
                    if (t.Name.StartsWith("Fishing") && int.TryParse(t.Name.Substring(7), out n) && n > bestN && t.GetField("collections", All) != null) { best = t; bestN = n; }
                }
            if (best != fishingType) Log("collections from " + (best == null ? "none" : best.Name));
            fishingType = best;
        }
        if (fishingType == null) return null;
        return fishingType.GetField("collections", All).GetValue(null) as IDictionary;
    }
    static string FishOf(string owner)
    {
        IDictionary all = Collections();
        if (all == null) return "";
        foreach (DictionaryEntry e in all)
            if ((string)e.Key == owner) return FishString(e.Value as IDictionary<string, int>);
        return "";
    }

    static void Tick()
    {
        if (!enabled || !NetworkServer.active) return;
        try
        {
            ticks++;
            if (Stage.instance != lastStage)
            {
                lastStage = Stage.instance;
                returnPos.Clear(); pending.Clear(); spotsCache = null;
                ClearHouses("new stage");
            }
            string[] lines = null;
            lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
            if (lines != null) foreach (string l in lines) Handle(l);

            foreach (var kv in new List<KeyValuePair<string, Trip>>(pending))
                if (Time.time > kv.Value.deadline)
                {
                    pending.Remove(kv.Key);
                    Info(kv.Key + ": your FISHING.exe is too old to visit aquariums - restart it to update.");
                    Log("trip " + kv.Key + " -> " + kv.Value.owner + " timed out (no AQREADY)");
                }

            // keep tanks current: rebuild contents when the owner catches something (re-broadcast only if someone is inside)
            if (Time.time >= nextRefresh && houses.Count > 0)
            {
                nextRefresh = Time.time + 2f;
                foreach (var h in new List<House>(houses.Values))
                {
                    if (h.root == null) continue;
                    string f = FishOf(h.owner);
                    if (f == h.fish) continue;
                    FillTank(h, f);
                    if (AnyoneInside(h)) Say(HouseLine(h, ""));
                }
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    static bool AnyoneInside(House h)
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList)
        {
            CharacterBody b = nu.master == null ? null : nu.master.GetBody();
            if (b != null && h.bounds.Contains(b.footPosition + Vector3.up * 0.5f)) return true;
        }
        return false;
    }

    static NetworkUser User(string name)
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu.userName == name) return nu;
        return null;
    }

    static void Handle(string p)
    {
        int i = p.IndexOf(": ");
        if (i <= 0) return;
        string name = p.Substring(0, i).Trim(), text = p.Substring(i + 2).Trim();
        NetworkUser nu = User(name);
        if (nu == null) return;
        if (text.StartsWith("[FT] AQREADY|"))
        {
            string owner = text.Substring(13);
            Trip trip;
            if (pending.TryGetValue(name, out trip) && CleanName(trip.owner) == owner) { pending.Remove(name); Travel(nu, trip.owner); }
            return;
        }
        string low = text.ToLowerInvariant();
        string cmd = low.Split(' ')[0];
        if (cmd != "!house" && cmd != "!aquarium" && cmd != "!visit" && cmd != "!home" && cmd != "!back") return;
        float cd;
        if (cooldown.TryGetValue(name, out cd) && Time.time < cd) return;
        cooldown[name] = Time.time + 1.5f;
        Log(name + ": " + text);
        if (cmd == "!home" || cmd == "!back") { GoBack(nu); return; }
        string owner2 = name;
        if (cmd == "!visit")
        {
            string q = text.Length > 6 ? text.Substring(6).Trim() : "";
            if (q == "") { Info("Usage: !visit <name>  (or !house for your own)"); return; }
            owner2 = MatchPlayer(q);
            if (owner2 == null) { Info("No player matching \"" + q + "\"."); return; }
        }
        Visit(nu, owner2);
    }

    static string MatchPlayer(string q)
    {
        var names = new List<string>();
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (!string.IsNullOrEmpty(nu.userName) && !names.Contains(nu.userName)) names.Add(nu.userName);
        IDictionary all = Collections();
        if (all != null) foreach (DictionaryEntry e in all) if (!names.Contains((string)e.Key)) names.Add((string)e.Key);
        string ql = q.ToLowerInvariant();
        foreach (string n in names) if (n.ToLowerInvariant() == ql) return n;
        foreach (string n in names) if (n.ToLowerInvariant().StartsWith(ql)) return n;
        foreach (string n in names) if (n.ToLowerInvariant().Contains(ql)) return n;
        return null;
    }

    static void Visit(NetworkUser nu, string owner)
    {
        CharacterBody body = nu.master == null ? null : nu.master.GetBody();
        if (body == null || !body.healthComponent.alive) { Info(nu.userName + ": you need to be alive to travel."); return; }
        House h;
        if (!houses.TryGetValue(owner, out h) || h.root == null)
        {
            Vector3? spot = FindSpot();
            if (spot == null) { Info("There's no room for a house on this map, sorry!"); return; }
            h = BuildHouse(owner, spot.Value, FishOf(owner));
        }
        else
        {
            string f = FishOf(owner);
            if (f != h.fish) FillTank(h, f);
        }
        Say(HouseLine(h, nu.userName));   // every guest builds it; the visitor's FISHING.exe answers AQREADY
        if (nu.isLocalPlayer) { Travel(nu, owner); return; }
        pending[nu.userName] = new Trip { visitor = nu.userName, owner = owner, deadline = Time.time + 6f };
    }

    static void Travel(NetworkUser nu, string owner)
    {
        House h;
        if (!houses.TryGetValue(owner, out h) || h.root == null) return;
        CharacterBody body = nu.master == null ? null : nu.master.GetBody();
        if (body == null || !body.healthComponent.alive) return;
        if (HouseAt(body.footPosition + Vector3.up * 0.5f) == null) returnPos[nu.userName] = body.footPosition;
        Vector3 target = h.pos + SpawnLocal + new Vector3(UnityEngine.Random.Range(-1.2f, 1.2f), 0, 0);
        TeleportHelper.TeleportBody(body, target);
        if (owner == nu.userName) Info(nu.userName + " went home to their aquarium. (!home to go back)");
        else Info(nu.userName + " is visiting " + owner + "'s aquarium. (!home to go back)");
        Log(nu.userName + " -> " + owner + "'s house at " + target + " (return " + (returnPos.ContainsKey(nu.userName) ? returnPos[nu.userName].ToString() : "-") + ")");
    }

    static void GoBack(NetworkUser nu)
    {
        CharacterBody body = nu.master == null ? null : nu.master.GetBody();
        if (body == null || !body.healthComponent.alive) return;
        bool inside = HouseAt(body.footPosition + Vector3.up * 0.5f) != null;
        Vector3 back;
        if (!returnPos.TryGetValue(nu.userName, out back))
        {
            if (!inside) { Info(nu.userName + ": you're not in an aquarium."); return; }
            // no saved spot (e.g. revived next to a visitor): go next to a teammate on the map
            bool found = false;
            foreach (var other in NetworkUser.readOnlyInstancesList)
            {
                CharacterBody ob = other.master == null ? null : other.master.GetBody();
                if (ob == null || ob == body || !ob.healthComponent.alive || HouseAt(ob.footPosition + Vector3.up * 0.5f) != null) continue;
                back = ob.footPosition + new Vector3(1.5f, 0.5f, 0); found = true; break;
            }
            if (!found)
            {
                var sps = SpawnPoint.readOnlyInstancesList;
                if (sps.Count == 0) { Info(nu.userName + ": no way back found, sorry!"); return; }
                back = sps[0].transform.position;
            }
        }
        returnPos.Remove(nu.userName);
        if (!inside) { Info(nu.userName + ": you're not in an aquarium."); return; }
        TeleportHelper.TeleportBody(body, back);
        Info(nu.userName + " is back on the map.");
        Log(nu.userName + " back to " + back);
    }

    // ---------------------------------------------------------------- where houses go
    // High above the map's highest walkable geometry, inside every "stay inside" map zone (TriggerExit MapZones
    // teleport you back when you leave them) and outside every "don't enter" zone, clear of all world geometry,
    // 40 m apart. Candidates are tried from high to low around the map centre.
    static List<Vector3> spotsCache;
    static Bounds worldB;
    static MapZone[] zones;
    static Vector3? FindSpot()
    {
        if (spotsCache == null)
        {
            spotsCache = new List<Vector3>();
            bool any = false; int n = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (c.isTrigger || c.gameObject.layer != LayerIndex.world.intVal || !c.enabled) continue;
                if (c.GetComponentInParent<Transform>() != null && c.transform.root.name.StartsWith("Aquarium_")) continue;
                if (!any) { worldB = c.bounds; any = true; } else worldB.Encapsulate(c.bounds);
                n++;
            }
            zones = UnityEngine.Object.FindObjectsOfType<MapZone>();
            var zl = new List<string>();
            foreach (var z in zones) { var zc = z.GetComponent<Collider>(); zl.Add(z.triggerType + "/" + z.zoneType + (zc == null ? "" : " " + zc.bounds.min + ".." + zc.bounds.max)); }
            Log("map bounds " + worldB.min + ".." + worldB.max + " (" + n + " colliders); zones: " + string.Join("; ", zl.ToArray()));
        }
        Vector3 c0 = worldB.center;
        float top = worldB.max.y;
        var heights = new List<float>();
        for (float dy = 80f; dy >= 10f; dy -= 10f) heights.Add(top + dy);
        for (float y = top; y >= worldB.min.y + 10f; y -= 15f) heights.Add(y);
        var rings = new List<Vector3>();
        rings.Add(Vector3.zero);
        for (float r = 45f; r <= 270f; r += 45f)
        {
            int k = Mathf.Max(6, Mathf.RoundToInt(2f * Mathf.PI * r / 45f));
            for (int j = 0; j < k; j++) { float a = j * 2f * Mathf.PI / k; rings.Add(new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r)); }
        }
        foreach (float y in heights)
            foreach (var o in rings)
            {
                Vector3 p = new Vector3(c0.x + o.x, y, c0.z + o.z);
                if (Valid(p)) { spotsCache.Add(p); Log("house spot " + p + " (map top " + top.ToString("0") + ")"); return p; }
            }
        // nothing passed every check: high above the map anyway
        Vector3 fb = new Vector3(c0.x + spotsCache.Count * 40f, top + 120f, c0.z);
        spotsCache.Add(fb);
        Log("no validated house spot - fallback " + fb);
        return fb;
    }

    static bool Valid(Vector3 p)
    {
        foreach (var s in spotsCache) if ((s - p).sqrMagnitude < 40f * 40f) return false;
        foreach (var h in houses.Values) if (h.root != null && (h.pos - p).sqrMagnitude < 40f * 40f) return false;
        Vector3 half = new Vector3(RoomW / 2 + Wall + 4f, RoomH / 2 + Wall + 4f, RoomD / 2 + Wall + 4f);
        Vector3 center = p + Vector3.up * (RoomH / 2);
        if (Physics.CheckBox(center, half, Quaternion.identity, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) return false;
        var hb = new Bounds(center, half * 2f);
        foreach (var z in zones)
        {
            if (z == null) continue;
            var zc = z.GetComponent<Collider>();
            if (zc == null || !zc.enabled) continue;
            if (z.triggerType == MapZone.TriggerType.TriggerEnter) { if (zc.bounds.Intersects(hb)) return false; continue; }
            // TriggerExit: the whole house must be inside
            for (int i = 0; i < 9; i++)
            {
                Vector3 q = i == 8 ? center : center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                if (!Inside(zc, q)) return false;
            }
        }
        return true;
    }
    static bool Inside(Collider c, Vector3 q)
    {
        bool exact = c is BoxCollider || c is SphereCollider || c is CapsuleCollider || (c is MeshCollider && ((MeshCollider)c).convex);
        if (!exact) return c.bounds.Contains(q);
        return (c.ClosestPoint(q) - q).sqrMagnitude < 1e-4f;
    }
#endif
}

#if AQUA_CLIENT
public class AquariumClientBehaviour : MonoBehaviour
{
    void Update() { AquariumClient.Update(); }
}
#endif
