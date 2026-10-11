// RodStand7 - host add-on: the physical ROD SHOP market stall + the glowing bobbers.
// Build with RodStandLook.cs StandSkinLook.cs GlowBobberLook.cs.
//  * Each stage: a stall on flat dry land near the lake (searched outward from the purple hotspot like the roulette
//    table: above the water, flat, open sky, no props/podiums/roulette in the footprint), facing the lake, with a teal
//    outline that shows through walls. Broadcast: "[FT] STAND|x|y|z|yaw" every 20 s.
//  * JV94 (host) within 4 m: the shop panel shows (rods / skins / bobbers with prices, owned, equipped).
//    Left/Right = category, number key = equip an owned item, or press the number TWICE to buy. E = open the guide book.
//    Purchases go through the running Fishing<N>.RodCommand("!buy ...") - same prices, money, saving and broadcasts.
//  * Guests (FISHING.exe, later): hidden chat "[FT] SHOPBUY|<item>" / "[FT] SHOPEQUIP|<item>" / "[FT] SHOPLIST"; the host checks
//    that the guest stands within 5.5 m of the stall, then runs the same command. Catalogue for guests:
//    "[FT] SHOPCAT|seq|part/parts|kind~name~price~desc;..." (every 60 s), ownership "[FT] SHOPOWN|name|rod~skin~bobber|item,item".
//  * Glow bobbers (Glow / Firefly / Lantern): added to the live Fishing<N>.RodCatalog at runtime (a Fishing<N> that already
//    has them is left alone), glow look on everyone's rig on the host. Ownership is mirrored to host\glow_owners.txt and
//    restored when a newer Fishing<N> loads before it knows the glow items. The NIGHT BONUS needs the Fishing patch
//    (Fishing<N>.glowBonusLive) - until then they only glow.
//  * Chat !buy / !equip keep working everywhere unless the running Fishing<N> has rodBuyGate (then: only at the stall).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

public static class RodStand7
{
    public static bool enabled = true;
    public static bool dryRun;                       // purchases: Fishing<N>.rodDryRun for the call (nothing saved/broadcast)
    public static Vector3 standPos; public static float standYaw; public static bool placed;
    public static float Range = 4f, GuestRange = 5.5f;
    public static int bookRequests;                  // GuideBook<N> opens the book when this changes
    public static string testPlaceAt;                // test: "x|y|z|yaw" forces the stall there on the next tick
    public static readonly string[][] GlowItems = {
        new[] { "Glow Bobber", "2500", "glows; at night bites 25% faster" },
        new[] { "Firefly Bobber", "4000", "glows; at night +15% rare fish" },
        new[] { "Lantern Bobber", "6000", "glows; at night bites 25% faster and +15% rare fish" } };
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static readonly System.Random rng = new System.Random();
    static readonly string MirrorPath = FishingPaths.Data("glow_owners.txt");

    static string scene; static float sceneAt, nextPlaceTry, nextStandLine, nextCat, nextOwn, nextGuard;
    static Type fishing, lastFishing; static int catSeq, errors;
    static readonly List<string> queue = new List<string>(); static string lastLine;
    static readonly Dictionary<string, string[]> mirror = new Dictionary<string, string[]>();   // name -> {owned csv, equipped glow}

    // host panel state
    public static bool panelVisible; public static int tab, page, pages = 1; public static string panelMsg; static float msgUntil;
    static int pendingKey = -1; static float pendingUntil; static string pendingItem;
    static readonly bool[] keyWas = new bool[16];
    static GameObject hudGo;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_rodstand.log"), DateTime.Now.ToString("HH:mm:ss ") + "[RodStand7] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static Type Newest(string prefix, string need)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith(prefix) || !int.TryParse(t.Name.Substring(prefix.Length), out n) || n <= bn) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                if (need != null && t.GetField(need, All) == null) continue;
                best = t; bn = n;
            }
        return best;
    }
    static object SGet(Type t, string n) { if (t == null) return null; FieldInfo f = t.GetField(n, All); if (f != null) return f.GetValue(null); PropertyInfo p = t.GetProperty(n, All); return p != null ? p.GetValue(null, null) : null; }
    static object IGet(object o, string n) { if (o == null) return null; FieldInfo f = o.GetType().GetField(n, All); if (f != null) return f.GetValue(o); PropertyInfo p = o.GetType().GetProperty(n, All); return p != null ? p.GetValue(o, null) : null; }
    static void ISet(object o, string n, object v) { o.GetType().GetField(n, All).SetValue(o, v); }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }
    static string Money(int v) { return "$" + v.ToString("#,0", Ci); }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t != typeof(RodStand7) && Regex.IsMatch(t.Name, "^RodStand\\d+$")) { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); Log("disabled " + t.Name); }
        RodStandLook.log = Log;
        GlowBobberLook.Hour = ClockHour;
        LoadMirror();
        Chat.onChatChanged += OnChat;
        RoR2Application.onUpdate += Tick;
        Log("installed (range " + Range + " m, guests " + GuestRange + " m)");
    }

    // ------------------------------------------------------------------ fishing clock (for the glow)
    static Type dayNight; static float dnLook = -100f;
    static float ClockHour()
    {
        if (Time.unscaledTime - dnLook > 5f) { dnLook = Time.unscaledTime; dayNight = Newest("DayNight", "hour"); }
        return dayNight == null ? 12f : Convert.ToSingle(SGet(dayNight, "hour"));
    }

    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            var log = Chat.readOnlyLog; if (log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            if (line.Contains("[FT] SHOP")) lock (queue) queue.Add(line);
        }
        catch { }
    }

    static void Tick()
    {
        if (!enabled) { if (RodStandLook.root != null) RodStandLook.Destroy(); placed = false; panelVisible = false; return; }
        try
        {
            if (Time.unscaledTime >= nextGuard) { nextGuard = Time.unscaledTime + 2f; fishing = Newest("Fishing", "RodCatalog"); if (fishing != null) { EnsureCatalog(); Guard(); } }
            if (fishing != null) GlowRigs();
            if (hudGo == null) { hudGo = new GameObject(typeof(RodStand7).Name + "Hud"); UnityEngine.Object.DontDestroyOnLoad(hudGo); hudGo.AddComponent<RodStand7Gui>(); }
            if (!NetworkServer.active || RoR2.Run.instance == null) { if (RodStandLook.root != null) RodStandLook.Destroy(); placed = false; scene = null; panelVisible = false; return; }
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != scene) { scene = sc; placed = false; RodStandLook.Destroy(); sceneAt = Time.time; nextPlaceTry = Time.time + 12f; Log("scene " + sc); }
            if (testPlaceAt != null)
            {
                string[] p = testPlaceAt.Split('|'); testPlaceAt = null;
                Place(new Vector3(float.Parse(p[0], Ci), float.Parse(p[1], Ci), float.Parse(p[2], Ci)), float.Parse(p[3], Ci), "test");
            }
            if (!placed && Time.time >= nextPlaceTry) { nextPlaceTry = Time.time + 3f; TryPlace(); }
            if (placed && RodStandLook.root == null) RodStandLook.Build(standPos, standYaw, Prices());
            if (placed)
            {
                if (Time.time >= nextStandLine) { nextStandLine = Time.time + 20f; Say(StandLine()); }
                if (Time.time >= nextCat) { nextCat = Time.time + 60f; SendCatalog(); }
                if (Time.time >= nextOwn) { nextOwn = Time.time + 15f; foreach (var nu in NetworkUser.readOnlyInstancesList) if (Near(nu, 12f)) SendOwn(nu.userName); }
            }
            RodStandLook.Tick();
            string[] lines; lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) GuestLine(l);
            HostPanel();
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    // ------------------------------------------------------------------ catalogue + glow ownership guard
    static IList Catalog() { return SGet(fishing, "RodCatalog") as IList; }
    static IDictionary Owners() { return SGet(fishing, "rodOwners") as IDictionary; }
    static IDictionary MoneyMap() { return SGet(fishing, "money") as IDictionary; }
    static object FindCat(string n) { var c = Catalog(); if (c != null) foreach (object i in c) if ((string)IGet(i, "name") == n) return i; return null; }

    static void EnsureCatalog()
    {
        var cat = Catalog(); if (cat == null) return;
        Type itemT = fishing.GetNestedType("RodItem", All);
        int added = 0;
        foreach (var g in GlowItems)
        {
            if (FindCat(g[0]) != null) continue;
            object it = Activator.CreateInstance(itemT);
            ISet(it, "name", g[0]); ISet(it, "kind", "bobber"); ISet(it, "price", int.Parse(g[1], Ci)); ISet(it, "desc", g[2]);
            cat.Add(it); added++;
        }
        bool newFishing = fishing != lastFishing;
        lastFishing = fishing;
        if (added > 0 || newFishing) Log((added > 0 ? "added " + added + " glow bobbers to " : "glow bobbers already in ") + fishing.Name + ".RodCatalog");
        if (added > 0 || newFishing) Restore();
    }

    static void LoadMirror()
    {
        try
        {
            if (!File.Exists(MirrorPath)) return;
            foreach (string l in File.ReadAllLines(MirrorPath)) { string[] p = l.Split('\t'); if (p.Length >= 3) mirror[p[0]] = new[] { p[1], p[2] }; }
            Log("glow mirror loaded: " + mirror.Count + " owner(s)");
        }
        catch (Exception e) { Log("mirror load error: " + e.Message); }
    }
    static void SaveMirror()
    {
        try
        {
            var sb = new StringBuilder(); foreach (var kv in mirror) sb.Append(kv.Key + "\t" + kv.Value[0] + "\t" + kv.Value[1] + "\n");
            File.WriteAllText(MirrorPath, sb.ToString());
        }
        catch (Exception e) { Log("mirror save error: " + e.Message); }
    }

    // a newer Fishing<N> (or a game restart) dropped glow items it didn't know at load time: give them back
    static void Restore()
    {
        var owners = Owners(); if (owners == null) return;
        Type ownerT = fishing.GetNestedType("RodOwner", All);
        int fixedN = 0;
        foreach (var kv in mirror)
        {
            object o = owners.Contains(kv.Key) ? owners[kv.Key] : null;
            if (o == null) { o = Activator.CreateInstance(ownerT); owners[kv.Key] = o; }
            var owned = (List<string>)IGet(o, "owned");
            foreach (string it in kv.Value[0].Split(',')) if (it.Length > 0 && !owned.Contains(it)) { owned.Add(it); fixedN++; }
            if (kv.Value[1].Length > 0 && (string)IGet(o, "bobber") == "Plain") { ISet(o, "bobber", kv.Value[1]); fixedN++; }
        }
        if (fixedN > 0)
        {
            MethodInfo save = fishing.GetMethod("SaveRods", All); if (save != null && !dryRun) save.Invoke(null, null);
            Log("restored " + fixedN + " glow item(s)/loadout(s) into " + fishing.Name + " from the mirror");
        }
    }

    static void Guard()
    {
        var owners = Owners(); if (owners == null) return;
        bool changed = false;
        foreach (DictionaryEntry e in owners)
        {
            var owned = (List<string>)IGet(e.Value, "owned");
            var g = new List<string>(); foreach (string it in owned) if (GlowBobberLook.IsGlow(it)) g.Add(it);
            string bob = (string)IGet(e.Value, "bobber"); string eq = GlowBobberLook.IsGlow(bob) ? bob : "";
            if (g.Count == 0 && !mirror.ContainsKey((string)e.Key)) continue;
            string[] old; mirror.TryGetValue((string)e.Key, out old);
            if (old != null) foreach (string it in old[0].Split(',')) if (it.Length > 0 && !g.Contains(it)) g.Add(it);   // never forget a purchase
            var now = new[] { string.Join(",", g.ToArray()), eq };
            if (old == null || old[0] != now[0] || old[1] != now[1]) { mirror[(string)e.Key] = now; changed = true; }
        }
        if (changed && !dryRun) { SaveMirror(); Log("glow mirror saved (" + mirror.Count + " owner(s))"); }
    }

    // ------------------------------------------------------------------ glow look on the host's rod rigs
    static void GlowRigs()
    {
        var rigs = SGet(fishing, "rigs") as IDictionary; var owners = Owners();
        if (rigs == null) return;
        string local = Convert.ToString(SGet(fishing, "hudLocalName")), preview = SGet(fishing, "previewBobber") as string;
        foreach (DictionaryEntry e in rigs)
        {
            try
            {
                var bob = IGet(e.Value, "bobber") as GameObject; if (bob == null) continue;
                object o = owners != null && owners.Contains(e.Key) ? owners[e.Key] : null;
                string want = o == null ? "Plain" : (string)IGet(o, "bobber");
                if ((string)e.Key == local && preview != null) want = preview;
                var fx = bob.GetComponent<GlowBobberFx>();
                if (GlowBobberLook.IsGlow(want))
                {
                    int k = Array.IndexOf(GlowBobberLook.Names, want);
                    if (fx == null || fx.kind != k) { GlowBobberLook.Apply(bob, want); Log(e.Key + ": glow look " + want); }
                    else { var r = bob.GetComponent<Renderer>(); if (r != null && r.sharedMaterial != fx.body) r.sharedMaterial = fx.body; }
                }
                else if (fx != null) GlowBobberLook.Clear(bob);
            }
            catch (Exception ex) { if (errors++ < 20) Log("glow rig error: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ placement
    static string StandLine() { return "[FT] STAND|" + standPos.x.ToString("0.00", Ci) + "|" + standPos.y.ToString("0.00", Ci) + "|" + standPos.z.ToString("0.00", Ci) + "|" + standYaw.ToString("0", Ci); }

    static Dictionary<string, string> Prices()
    {
        var d = new Dictionary<string, string>(); var c = Catalog();
        if (c != null) foreach (object i in c) { int p = Convert.ToInt32(IGet(i, "price")); d[(string)IGet(i, "name")] = p == 0 ? "free" : Money(p); }
        return d;
    }

    static bool Ground(float x, float z, float top, int mask, out float y, out Vector3 n)
    {
        RaycastHit h; y = 0f; n = Vector3.up;
        if (!Physics.Raycast(new Vector3(x, top, z), Vector3.down, out h, 400f, mask, QueryTriggerInteraction.Ignore)) return false;
        y = h.point.y; n = h.normal; return true;
    }

    static void TryPlace()
    {
        if (fishing == null) return;
        object hp = SGet(fishing, "hotspotPos"), wy = SGet(fishing, "waterY"), water = SGet(fishing, "water"), hh = SGet(fishing, "hasHotspot");
        if (!(hp is Vector3) || !(wy is float) || water == null || ((water is UnityEngine.Object) && !(UnityEngine.Object)water) || !(hh is bool) || !(bool)hh) return;
        Type rt = Newest("Roulette", "tablePos");
        bool rPlaced = rt != null && SGet(rt, "placed") is bool && (bool)SGet(rt, "placed");
        if (rt != null && !rPlaced && Time.time - sceneAt < 45f) return;   // let the roulette table go first
        Vector3 table = rPlaced ? (Vector3)SGet(rt, "tablePos") : new Vector3(float.NaN, 0, 0);
        Vector3 c = (Vector3)hp; float waterY = (float)wy;
        int mask = LayerIndex.world.mask.value;
        for (float r = 10f; r <= 200f; r += 4f)
        {
            int steps = Mathf.Max(16, (int)(r * 0.6f)), start = rng.Next(steps);
            for (int k = 0; k < steps; k++)
            {
                float ang = (start + k) * Mathf.PI * 2f / steps;
                float x = c.x + Mathf.Cos(ang) * r, z = c.z + Mathf.Sin(ang) * r, gy; Vector3 n;
                if (!Ground(x, z, waterY + 150f, mask, out gy, out n)) continue;
                if (gy < waterY + 0.8f || gy > waterY + 20f || n.y < 0.94f) continue;
                if (!float.IsNaN(table.x) && new Vector2(x - table.x, z - table.z).magnitude < 12f) continue;
                Vector3 toLake = c - new Vector3(x, gy, z); toLake.y = 0f;
                float yaw = Mathf.Atan2(toLake.x, toLake.z) * Mathf.Rad2Deg;
                string why = Fits(new Vector3(x, gy, z), yaw, waterY, mask);
                if (why != null) continue;
                Place(new Vector3(x, gy, z), yaw, r.ToString("0") + " m from hotspot " + c);
                return;
            }
        }
        Log("no spot for the stall near hotspot " + c + " (water Y " + waterY.ToString("0.00") + "), retrying in 15 s");
        nextPlaceTry = Time.time + 15f;
    }

    // null = the footprint is flat dry land with open sky and nothing else standing there
    static string Fits(Vector3 p, float yaw, float waterY, int mask)
    {
        Quaternion q = Quaternion.Euler(0f, yaw, 0f);
        foreach (var o in new[] { new Vector3(-1.8f, 0, -1.2f), new Vector3(1.8f, 0, -1.2f), new Vector3(-1.8f, 0, 1.4f), new Vector3(1.8f, 0, 1.4f), new Vector3(0, 0, 2.6f), new Vector3(0, 0, -1.2f) })
        {
            Vector3 w = p + q * o; float y; Vector3 n;
            if (!Ground(w.x, w.z, waterY + 150f, mask, out y, out n)) return "no ground";
            if (Mathf.Abs(y - p.y) > (o.z > 2f ? 0.8f : 0.4f)) return "not flat";
            if (y < waterY + 0.6f) return "water";
        }
        foreach (var o in new[] { Vector3.zero, new Vector3(-1.5f, 0, 0), new Vector3(1.5f, 0, 0) })
            if (Physics.Raycast(p + q * o + Vector3.up * 0.3f, Vector3.up, 6f, mask, QueryTriggerInteraction.Ignore)) return "roof";
        foreach (var col in Physics.OverlapBox(p + Vector3.up * 1.8f, new Vector3(2.6f, 1.4f, 2.0f), q, ~0, QueryTriggerInteraction.Ignore))
        {
            if (col == null) continue;
            if (col.GetComponentInParent<CharacterBody>() != null || col.GetComponentInParent<HurtBox>() != null) continue;
            return "occupied by " + col.name;
        }
        foreach (string n in new[] { "FishingRoulette", "FishingRodStand" }) { var g = GameObject.Find(n); if (g != null && (g.transform.position - p).magnitude < 10f) return "near " + n; }
        return null;
    }

    static void Place(Vector3 p, float yaw, string why)
    {
        standPos = p; standYaw = yaw;
        RodStandLook.Build(p, yaw, Prices());
        placed = true; nextStandLine = 0f; nextCat = 0f; nextOwn = Time.time + 2f;
        Say("<color=#66ffcc>The ROD SHOP stall is open by the lake! Walk up to it to browse rods, skins and bobbers.</color>");
        Log("stall placed on " + scene + " at " + p + " yaw " + yaw.ToString("0") + " (" + why + ")");
    }

    // ------------------------------------------------------------------ distance
    public static Vector3 Front { get { return standPos + Quaternion.Euler(0f, standYaw, 0f) * new Vector3(0f, 0f, 1.3f); } }
    public static float DistTo(Vector3 foot) { Vector3 d = foot - Front; float dy = Mathf.Abs(d.y); d.y = 0f; return dy > 3f ? 999f : d.magnitude; }
    static CharacterBody BodyOf(NetworkUser nu) { return nu != null && nu.master != null ? nu.master.GetBody() : null; }
    static bool Near(NetworkUser nu, float r) { var b = BodyOf(nu); return placed && b != null && b.healthComponent != null && b.healthComponent.alive && DistTo(b.footPosition) <= r; }
    static NetworkUser User(string name) { foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu.userName == name) return nu; return null; }
    // used by the Fishing<N> rodBuyGate (stall-only buying): null = allowed
    public static string Gate(string name)
    {
        if (!enabled || !placed) return null;   // no stall on this stage: chat buying stays open
        var nu = User(name);
        return Near(nu, GuestRange) ? null : name + ", rods and bobbers are sold at the ROD SHOP stall by the lake - walk up to it to buy.";
    }

    // ------------------------------------------------------------------ purchases (all through Fishing<N>.RodCommand)
    public static string Command(string name, string cmd)
    {
        if (fishing == null) return "no fishing game";
        MethodInfo rc = fishing.GetMethod("RodCommand", All); if (rc == null) return "no RodCommand";
        FieldInfo dry = fishing.GetField("rodDryRun", All);
        bool was = dry != null && (bool)dry.GetValue(null);
        FieldInfo standCall = fishing.GetField("rodStandCall", All);
        try
        {
            if (dryRun && dry != null) dry.SetValue(null, true);
            if (standCall != null) standCall.SetValue(null, true);
            object ok = rc.Invoke(null, new object[] { name, cmd });
            Log(name + ": " + cmd + " -> " + ok + (dryRun ? " (dry run)" : ""));
            return Convert.ToString(ok);
        }
        finally { if (dry != null) dry.SetValue(null, was); if (standCall != null) standCall.SetValue(null, false); }
    }

    static void GuestLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": [FT] SHOP"); if (i <= 0) return;
        string name = plain.Substring(0, i).Trim(), body = plain.Substring(i + 7);
        string[] p = body.Split('|');
        var nu = User(name); if (nu == null) return;
        if (p[0] == "SHOPLIST") { SendCatalog(); SendOwn(name); return; }
        if ((p[0] != "SHOPBUY" && p[0] != "SHOPEQUIP") || p.Length < 2) return;
        if (!Near(nu, GuestRange)) { Say("<color=#66ffcc>" + name + ", walk up to the ROD SHOP stall first.</color>"); Log(name + ": " + p[0] + " " + p[1] + " refused (not at the stall)"); return; }
        Command(name, (p[0] == "SHOPBUY" ? "!buy " : "!equip ") + p[1]);
        SendOwn(name);
    }

    static void SendCatalog()
    {
        var c = Catalog(); if (c == null) return;
        var entries = new List<string>();
        foreach (object it in c) entries.Add(IGet(it, "kind") + "~" + IGet(it, "name") + "~" + IGet(it, "price") + "~" + Convert.ToString(IGet(it, "desc")).Replace("|", "/").Replace(";", ",").Replace("~", "-"));
        var chunks = new List<string>(); var sb = new StringBuilder();
        foreach (string e in entries) { if (sb.Length > 0 && sb.Length + e.Length > 320) { chunks.Add(sb.ToString()); sb.Length = 0; } if (sb.Length > 0) sb.Append(';'); sb.Append(e); }
        chunks.Add(sb.ToString());
        catSeq++;
        for (int i = 0; i < chunks.Count; i++) Say("[FT] SHOPCAT|" + catSeq + "|" + (i + 1) + "/" + chunks.Count + "|" + chunks[i]);
    }
    static void SendOwn(string name)
    {
        var owners = Owners(); object o = owners != null && owners.Contains(name) ? owners[name] : null;
        string eq = o == null ? DefaultRod() + "~Default~Plain" : IGet(o, "rod") + "~" + IGet(o, "skin") + "~" + IGet(o, "bobber");
        string owned = o == null ? "" : string.Join(",", ((List<string>)IGet(o, "owned")).ToArray());
        Say("[FT] SHOPOWN|" + name + "|" + eq + "|" + owned);
    }

    // ------------------------------------------------------------------ host panel + keys
    public class Row { public string name, kind, desc, tag; public int price; public bool owned, equipped; }
    public static readonly string[] Tabs = { "rod", "skin", "bobber", "item" };
    public static List<Row> Rows(string me, int tabIdx, out int money)
    {
        var rows = new List<Row>(); money = 0;
        var c = Catalog(); if (c == null) return rows;
        var mm = MoneyMap(); if (mm != null && me != null && mm.Contains(me)) money = Convert.ToInt32(mm[me]);
        var owners = Owners(); object o = owners != null && me != null && owners.Contains(me) ? owners[me] : null;
        var owned = o == null ? new List<string>() : (List<string>)IGet(o, "owned");
        string eq = Tabs[tabIdx] == "rod" ? (o == null ? DefaultRod() : (string)IGet(o, "rod")) : Tabs[tabIdx] == "skin" ? (o == null ? "Default" : (string)IGet(o, "skin")) : Tabs[tabIdx] == "item" ? "" : (o == null ? "Plain" : (string)IGet(o, "bobber"));
        foreach (object it in c)
        {
            if ((string)IGet(it, "kind") != Tabs[tabIdx]) continue;
            if (Convert.ToString(IGet(it, "desc")).Contains("not for sale")) continue;   // merchant StatTrak copies
            var r = new Row { name = (string)IGet(it, "name"), kind = Tabs[tabIdx], desc = (string)IGet(it, "desc"), price = Convert.ToInt32(IGet(it, "price")) };
            r.owned = r.price == 0 || owned.Contains(r.name); r.equipped = r.name == eq;
            rows.Add(r);
        }
        pages = Math.Max(1, (rows.Count + 8) / 9);
        if (page >= pages) page = pages - 1;
        if (page < 0) page = 0;
        return rows.GetRange(page * 9, Math.Min(9, rows.Count - page * 9));
    }
    static string DefaultRod() { var c = Catalog(); if (c != null) foreach (object i in c) if ((string)IGet(i, "kind") == "rod" && Convert.ToInt32(IGet(i, "price")) == 0) return (string)IGet(i, "name"); return "Basic Rod"; }
    public static bool GlowBonusLive { get { FieldInfo f = fishing == null ? null : fishing.GetField("glowBonusLive", All); return f != null && (bool)f.GetValue(null); } }
    public static string LocalName() { foreach (var lu in LocalUserManager.readOnlyLocalUsersList) if (lu.currentNetworkUser != null) return lu.currentNetworkUser.userName; return null; }

    static bool Focused() { uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg); return fg == myPid; }
    static bool Edge(int slot, int vk) { bool d = (GetAsyncKeyState(vk) & 0x8000) != 0; bool e = d && !keyWas[slot]; keyWas[slot] = d; return e; }
    static bool ChatOpen()
    {
        try { foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>()) { object o = IGet(b, "showInput"); if (o is bool && (bool)o) return true; } } catch { }
        return false;
    }
    static bool BookOpen() { Type g = Newest("GuideBook", null); object v = g == null ? null : SGet(g, "isOpen"); return v is bool && (bool)v; }
    public static void Msg(string s) { panelMsg = s; msgUntil = Time.unscaledTime + 5f; }
    public static string CurrentMsg { get { return Time.unscaledTime < msgUntil ? panelMsg : null; } }
    public static string PendingText { get { return pendingKey >= 0 && Time.unscaledTime < pendingUntil ? pendingItem : null; } }

    static void HostPanel()
    {
        var lu = LocalUserManager.GetFirstLocalUser();
        var body = lu != null ? lu.cachedBody : null;
        bool near = placed && body != null && body.healthComponent != null && body.healthComponent.alive && DistTo(body.footPosition) <= Range;
        if (near != panelVisible) { panelVisible = near; pendingKey = -1; if (near) Log("host at the stall (panel shown)"); }
        // key edges are tracked even when far away so a held key doesn't fire on arrival
        bool focus = Focused();
        bool left = Edge(10, 0x25), right = Edge(11, 0x27), e = Edge(12, 0x45), up = Edge(13, 0x26), down = Edge(14, 0x28);
        int num = -1; for (int k = 1; k <= 9; k++) if (Edge(k, 0x30 + k)) num = k;
        if (!near || !focus) return;
        if (!left && !right && !e && !up && !down && num < 0) return;
        if (ChatOpen() || BookOpen()) return;
        if (e) { bookRequests++; Log("E at the stall -> guide book"); return; }
        if (left) { tab = (tab + Tabs.Length - 1) % Tabs.Length; page = 0; pendingKey = -1; return; }
        if (right) { tab = (tab + 1) % Tabs.Length; page = 0; pendingKey = -1; return; }
        if (up) { page = Math.Max(0, page - 1); pendingKey = -1; return; }
        if (down) { page = Math.Min(pages - 1, page + 1); pendingKey = -1; return; }
        string me = LocalName(); int money;
        var rows = Rows(me, tab, out money);
        if (num > rows.Count) return;
        Row r = rows[num - 1];
        if (r.equipped) { Msg(r.name + " is already equipped."); return; }
        if (r.owned && r.kind == "item") { if (r.name == "Angler's Guide") { bookRequests++; Log("read the guide from the panel"); } else Msg("You own the " + r.name + "."); return; }
        if (r.owned) { Command(me, "!equip " + r.name); Msg("Equipped the " + r.name + "."); return; }
        if (money < r.price) { Msg("Not enough money for the " + r.name + " (" + Money(r.price) + ", you have " + Money(money) + ")."); pendingKey = -1; return; }
        if (pendingKey == num && Time.unscaledTime < pendingUntil && pendingItem != null && pendingItem.StartsWith(r.name + " "))
        {
            pendingKey = -1;
            Command(me, "!buy " + r.name);
            Msg("Bought the " + r.name + "!");
            return;
        }
        pendingKey = num; pendingUntil = Time.unscaledTime + 4f; pendingItem = r.name + " for " + Money(r.price);
    }
}

// JV94's shop panel at the stall (IMGUI)
public class RodStand7Gui : MonoBehaviour
{
    GUIStyle st, title; Texture2D bg; static int errs;
    void OnGUI()
    {
        try
        {
            if (!RodStand7.enabled || !RodStand7.panelVisible) return;
            if (st == null)
            {
                st = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, wordWrap = false };
                title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 22, fontStyle = FontStyle.Bold };
                bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0.04f, 0.08f, 0.08f, 0.82f)); bg.Apply();
            }
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            st.fontSize = (int)(15 * s); title.fontSize = (int)(22 * s);
            string me = RodStand7.LocalName(); int money;
            var rows = RodStand7.Rows(me, RodStand7.tab, out money);
            float w = 470f * s, lh = 21f * s, h = (128f + rows.Count * 21f + 40f) * s;
            var r = new Rect(24f * s, Screen.height * 0.24f, w, h);
            GUI.DrawTexture(r, bg);
            float y = r.y + 8f * s, x = r.x + 14f * s;
            GUI.Label(new Rect(x, y, w, 30f * s), "<color=#66ffcc>ROD SHOP</color>   <size=" + (int)(15 * s) + "><color=#ffd700>$" + money.ToString("#,0") + "</color></size>", title); y += 34f * s;
            string[] names = { "RODS", "SKINS", "BOBBERS", "ITEMS" };
            var tb = new System.Text.StringBuilder("<color=#888888>< </color>");
            for (int i = 0; i < names.Length; i++) tb.Append(i == RodStand7.tab ? "<b><color=#ffffff>[" + names[i] + "]</color></b>  " : "<color=#999999>" + names[i] + "</color>  ");
            tb.Append("<color=#888888>></color>");
            if (RodStand7.pages > 1) tb.Append("   <color=#aaaaaa>page " + (RodStand7.page + 1) + "/" + RodStand7.pages + " (Up/Down)</color>");
            GUI.Label(new Rect(x, y, w, lh), tb.ToString(), st); y += lh + 6f * s;
            bool glowLive = RodStand7.GlowBonusLive;
            for (int i = 0; i < rows.Count; i++)
            {
                var it = rows[i];
                string state = it.equipped ? "<color=#ffd700>equipped</color>" : it.owned ? "<color=#88ff88>owned - press " + (i + 1) + (it.kind == "item" ? " to read" : " to equip") + "</color>" : (money >= it.price ? "<color=#ffffff>$" + it.price.ToString("#,0") + "</color>" : "<color=#ff7777>$" + it.price.ToString("#,0") + "</color>");
                string desc = it.desc;
                if (GlowBobberLook.IsGlow(it.name) && !glowLive) desc = "glows at night (night bonus: next update)";
                GUI.Label(new Rect(x, y, w, lh), "<b>" + (i + 1) + "</b>  " + it.name + "  <color=#9aa>" + desc + "</color>", st);
                GUI.Label(new Rect(x + w - 190f * s, y, 180f * s, lh), state, new GUIStyle(st) { alignment = TextAnchor.UpperRight });
                y += lh;
            }
            y += 6f * s;
            string pend = RodStand7.PendingText, msg = RodStand7.CurrentMsg;
            if (pend != null) GUI.Label(new Rect(x, y, w, lh), "<color=#ffcc44>Press the same number again to buy the " + pend + "</color>", st);
            else if (msg != null) GUI.Label(new Rect(x, y, w, lh), "<color=#ccffee>" + msg + "</color>", st);
            y += lh;
            GUI.Label(new Rect(x, y, w, lh), "<color=#888888>Number = buy (twice) / equip    Left/Right = category    E = guide book</color>", st);
        }
        catch (Exception e) { if (errs++ < 3) File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_rodstand.log"), "GUI error: " + e + Environment.NewLine); }
    }
}
