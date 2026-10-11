// ShopStandClient - guest payload for FISHING.exe (NOT shipped yet). UnityEngine only; RoR2 by reflection.
// Build with RodStandLook.cs StandSkinLook.cs GlowBobberLook.cs TimeHudLook.cs GuideBookLook.cs GuideData.cs GuideSketch.cs
// (+ PhysicsModule / IMGUIModule / TextRenderingModule / ImageConversionModule refs).
//  * [FT] STAND|x|y|z|yaw            -> builds the ROD SHOP stall (with its teal outline) on this machine
//  * [FT] SHOPCAT / SHOPOWN          -> the host's live catalogue (prices are never hard-coded here) + what I own
//  * walk up to the stall (4 m)      -> shop panel; number key = buy (press twice) / equip, Left/Right = category,
//                                       E = guide book. Sends hidden chat "[FT] SHOPBUY|item" / "[FT] SHOPEQUIP|item";
//                                       the host checks the distance and runs its own rod-shop purchase.
//  * [FT] TIME|hour|dayMinutes       -> fishing clock (advanced locally between lines) for the HUD clock + glow bobbers
//  * [FT] EVENT|name|secsLeft        -> weather / time event on the clock
//  * G or typing !guide              -> the 3D guide book (facts: built-in defaults + host lines: catalogue, hotspots, ponds...)
// Does nothing on the host (the host runs RodStand<N> / TimeHud<N> / GuideBook<N>). A newer copy replaces an older one.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

public static class ShopStandClient
{
    public static bool enabled = true, installed;
    public static int version = 3;
    public static float hour = -1f, dayMinutes = 10f;      // read by GlowBobberLook (by name) for the glow
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static Assembly ror2; static Type netServer;
    static readonly List<string> queue = new List<string>(); static string lastLine;
    static float lastReal = -1f;
    // host state seen in chat
    static bool standOn; static Vector3 standPos; static float standYaw; static string standScene;
    public static string eventName, eventLeft; static float eventAt;
    static readonly List<string[]> catalog = new List<string[]>(); static readonly List<string[]> catParts = new List<string[]>(); static int catSeq = -1, catNext;
    static readonly List<string> owned = new List<string>(); static string eqRod = "Stick Rod", eqSkin = "Default", eqBob = "Plain";
    static int money; static string myName;
    static float hotX = float.NaN, hotZ = float.NaN, hotR = 10f, waterY = float.NaN, hot2X = float.NaN, hot2Z = float.NaN, tableX = float.NaN, tableZ = float.NaN;
    static readonly Dictionary<string, float[]> pools = new Dictionary<string, float[]>();
    static bool gameplaySeen;
    // panel
    public static bool panelVisible; public static int tab; static int pendingKey = -1; static float pendingUntil, msgUntil; static string pendingItem, msg;
    static readonly bool[] keyWas = new bool[16];
    static GameObject gui; static int lastSpread;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_shopstand_client.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static object SGet(Type t, string n) { if (t == null) return null; FieldInfo f = t.GetField(n, All); if (f != null) return f.GetValue(null); PropertyInfo p = t.GetProperty(n, All); return p != null ? p.GetValue(null, null) : null; }
    static object IGet(object o, string n) { if (o == null) return null; PropertyInfo p = o.GetType().GetProperty(n, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null); FieldInfo f = o.GetType().GetField(n, All); return f != null ? f.GetValue(o) : null; }
    static bool IsHost() { try { return (bool)netServer.GetProperty("active").GetValue(null, null); } catch { return false; } }
    static bool InRun() { try { object r = SGet(ror2.GetType("RoR2.Run"), "instance"); return r != null && !r.Equals(null); } catch { return false; } }

    public static void Run()
    {
        try
        {
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer"); if (t != null) netServer = t;
            }
            if (ror2 == null || netServer == null) { Log("game assemblies not found"); return; }
            if (IsHost()) { Log("host detected, the host runs RodStand / TimeHud / GuideBook itself"); return; }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(ShopStandClient).Assembly) continue;
                Type other = null; try { other = asm.GetType("ShopStandClient"); } catch { }
                if (other == null) continue;
                FieldInfo inst = other.GetField("installed", All), en = other.GetField("enabled", All), vf = other.GetField("version", All);
                if (inst == null || en == null || !(bool)inst.GetValue(null) || !(bool)en.GetValue(null)) continue;
                int ov = vf != null ? (int)vf.GetValue(null) : 0;
                if (ov >= version) { Log("another copy (version " + ov + ") is already running - nothing to do"); return; }
                en.SetValue(null, false); Log("replaced an older copy (version " + ov + " -> " + version + ")");
            }
            RodStandLook.log = Log; GuideBookLook.log = Log; GuideBookLook.play = PlayKind;
            GlowBobberLook.Hour = () => hour < 0f ? 12f : hour;
            Type chat = ror2.GetType("RoR2.Chat");
            EventInfo cc = chat.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            Action c = OnChat; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            Action u = Update; upd.AddEventHandler(null, u);
            installed = true;
            Log("shop stand client installed (version " + version + ")");
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            IList log = SGet(ror2.GetType("RoR2.Chat"), "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            if (line.Contains("[FT] ") || line.Contains("!guide")) lock (queue) queue.Add(line);
        }
        catch { }
    }

    static string Scene() { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
    static float F(string s) { return float.Parse(s, Ci); }

    static void Handle(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        string me = myName ?? (myName = MyName());
        if (me != null && (plain == me + ": !guide" || plain == me + ": !book")) { OpenBook("!guide"); return; }
        int i = plain.IndexOf("[FT] "); if (i < 0) return;
        if (i > 0 && plain.Substring(0, i).TrimEnd().EndsWith(":")) { if (!plain.Contains("[FT] SHOP")) return; }   // a player's own hidden line
        string[] p = plain.Substring(i + 5).Split('|');
        try
        {
            switch (p[0])
            {
                case "STAND":
                    if (p.Length < 5) break;
                    var np = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    if (!standOn || (np - standPos).sqrMagnitude > 0.01f || standScene != Scene() || RodStandLook.root == null)
                    { standPos = np; standYaw = F(p[4]); standOn = true; standScene = Scene(); RodStandLook.Build(standPos, standYaw, Prices()); Log("stall at " + np); }
                    break;
                case "SHOPCAT":
                    {
                        if (p.Length < 4) break;
                        int sq = int.Parse(p[1], Ci); string[] pp = p[2].Split('/'); int part = int.Parse(pp[0]), parts = int.Parse(pp[1]);
                        if (part == 1) { catParts.Clear(); catSeq = sq; catNext = 1; }
                        if (sq != catSeq || part != catNext) break;
                        foreach (string e in p[3].Split(';')) { string[] f = e.Split('~'); if (f.Length >= 4) catParts.Add(f); }
                        catNext++;
                        if (part == parts) { catalog.Clear(); catalog.AddRange(catParts); Log("catalogue: " + catalog.Count + " items"); if (standOn && RodStandLook.root != null && RodStandLook.root.transform.Find("Tag") == null) RodStandLook.Build(standPos, standYaw, Prices()); }
                        break;
                    }
                case "SHOPOWN":
                    if (p.Length >= 4 && p[1] == me)
                    {
                        string[] eq = p[2].Split('~'); if (eq.Length >= 3) { eqRod = eq[0]; eqSkin = eq[1]; eqBob = eq[2]; }
                        owned.Clear(); foreach (string o in p[3].Split(',')) if (o.Length > 0) owned.Add(o);
                    }
                    break;
                case "ROD": if (p.Length >= 5 && p[1] == me) { eqRod = p[2]; eqSkin = p[3]; eqBob = p[4]; } break;
                case "MONEY": if (p.Length >= 3 && p[1] == me) money = (int)double.Parse(p[2], Ci); break;
                case "CATCH": if (p.Length >= 5 && p[1] == me) money = (int)double.Parse(p[4], Ci); break;
                case "BOARD": for (int k = 1; k < p.Length; k++) { int c = p[k].LastIndexOf(':'); if (c > 0 && p[k].Substring(0, c) == me) money = (int)double.Parse(p[k].Substring(c + 1), Ci); } break;
                case "TIME":
                    if (p.Length >= 2) { float h = F(p[1]); if (hour < 0 || Mathf.Abs(Mathf.DeltaAngle(h * 15f, hour * 15f)) > 7.5f) hour = h; else hour = Mathf.Repeat(Mathf.LerpAngle(hour * 15f, h * 15f, 0.5f) / 15f, 24f); }
                    if (p.Length >= 3) dayMinutes = F(p[2]);
                    break;
                case "EVENT": if (p.Length >= 3) { eventName = p[1]; int s = int.Parse(p[2], Ci); eventLeft = (s / 60) + ":" + (s % 60).ToString("00"); eventAt = Time.unscaledTime; gameplaySeen = true; } break;
                case "HOT": if (p.Length >= 5) { hotX = F(p[1]); waterY = F(p[2]); hotZ = F(p[3]); hotR = F(p[4]); } break;
                case "HOT2": if (p.Length >= 4) { hot2X = F(p[1]); hot2Z = F(p[3]); gameplaySeen = true; } break;
                case "POOL": if (p.Length >= 6) pools[p[1]] = new[] { F(p[2]), F(p[4]), F(p[5]) }; break;
                case "GUIDEOPEN": if (p.Length >= 2 && p[1] == me) OpenBook("inventory"); break;
                case "TABLE": if (p.Length >= 4) { tableX = F(p[1]); tableZ = F(p[3]); } break;
            }
        }
        catch (Exception e) { Log("line error (" + plain + "): " + e.Message); }
    }

    static Dictionary<string, string> Prices()
    {
        var d = new Dictionary<string, string>();
        foreach (var c in catalog) { int v; int.TryParse(c[2], out v); d[c[1]] = v == 0 ? "free" : "$" + v.ToString("#,0", Ci); }
        return d;
    }

    static string MyName()
    {
        try
        {
            IList lus = SGet(ror2.GetType("RoR2.LocalUserManager"), "readOnlyLocalUsersList") as IList;
            if (lus != null && lus.Count > 0) { object nu = IGet(lus[0], "currentNetworkUser"); if (nu != null) return Convert.ToString(IGet(nu, "userName")); }
        }
        catch { }
        return null;
    }
    static object LocalBody()
    {
        try { object lu = ror2.GetType("RoR2.LocalUserManager").GetMethod("GetFirstLocalUser", All).Invoke(null, null); object b = IGet(lu, "cachedBody"); return b == null || b.Equals(null) ? null : b; }
        catch { return null; }
    }

    static void Say(string text)
    {
        try
        {
            object console = SGet(ror2.GetType("RoR2.Console"), "instance");
            IList locals = SGet(ror2.GetType("RoR2.NetworkUser"), "readOnlyLocalPlayersList") as IList;
            if (console == null || locals == null || locals.Count == 0) return;
            foreach (MethodInfo m in console.GetType().GetMethods(All))
                if (m.Name == "SubmitCmd" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "NetworkUser")
                { m.Invoke(console, new object[] { locals[0], "say \"" + text.Replace("\"", "'") + "\"", false }); Log("sent " + text); return; }
        }
        catch (Exception e) { Log("send error: " + (e.InnerException ?? e).Message); }
    }

    static readonly Dictionary<string, string[]> SoundCands = new Dictionary<string, string[]> {
        { "open", new[] { "Play_CursedBook_Open", "Play_scav_backpack_open", "Play_UI_menuClick" } },
        { "flip", new[] { "Play_flyingVermin_wingFlap", "Play_vulture_wingFlap", "Play_UI_menuClick" } },
        { "close", new[] { "Play_CursedBook_Close", "Play_UI_menuBack" } }, { "bump", new[] { "Play_UI_menuBack" } } };
    static readonly Dictionary<string, string> soundPick = new Dictionary<string, string>();
    static void PlayKind(string kind)
    {
        try
        {
            MethodInfo ps = ror2.GetType("RoR2.Util").GetMethod("PlaySound", new[] { typeof(string), typeof(GameObject) });
            var b = LocalBody() as Component; GameObject go = b != null ? b.gameObject : (Camera.main != null ? Camera.main.gameObject : null);
            if (ps == null || go == null) return;
            string pick; string[] c;
            if (soundPick.TryGetValue(kind, out pick)) { ps.Invoke(null, new object[] { pick, go }); return; }
            if (!SoundCands.TryGetValue(kind, out c)) return;
            foreach (string s in c) if (Convert.ToUInt32(ps.Invoke(null, new object[] { s, go })) != 0) { soundPick[kind] = s; return; }
            soundPick[kind] = c[c.Length - 1];
        }
        catch { }
    }

    static bool Focused() { uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg); return fg == myPid; }
    static readonly bool[] keyWas2 = new bool[4];
    static bool EdgeX(int slot, int vk) { bool d = (GetAsyncKeyState(vk) & 0x8000) != 0; bool e = d && !keyWas2[slot]; keyWas2[slot] = d; return e; }
    static bool Edge(int slot, int vk) { bool d = (GetAsyncKeyState(vk) & 0x8000) != 0; bool e = d && !keyWas[slot]; keyWas[slot] = d; return e; }
    static bool ChatOpen()
    {
        try
        {
            Type cb = ror2.GetType("RoR2.UI.ChatBox");
            foreach (object b in UnityEngine.Object.FindObjectsOfType(cb)) { object o = IGet(b, "showInput"); if (o is bool && (bool)o) return true; }
        }
        catch { }
        return false;
    }
    public static float Dist()
    {
        var b = LocalBody(); if (b == null || !standOn) return 999f;
        Vector3 foot = (Vector3)IGet(b, "footPosition"), front = standPos + Quaternion.Euler(0f, standYaw, 0f) * new Vector3(0f, 0f, 1.3f);
        Vector3 d = foot - front; if (Mathf.Abs(d.y) > 3f) return 999f; d.y = 0f; return d.magnitude;
    }

    static void Update()
    {
        if (!enabled) { if (RodStandLook.root != null) RodStandLook.Destroy(); if (GuideBookLook.IsOpen) GuideBookLook.Close(); GuideBookLook.Tick(); panelVisible = false; return; }
        try
        {
            float now = Time.realtimeSinceStartup, dt = lastReal < 0 ? 0f : Mathf.Min(1f, now - lastReal); lastReal = now;
            if (hour >= 0f) hour = Mathf.Repeat(hour + dt * 24f / (Mathf.Max(0.1f, dayMinutes) * 60f), 24f);
            if (gui == null) { gui = new GameObject("ShopStandClientGui"); UnityEngine.Object.DontDestroyOnLoad(gui); gui.AddComponent<ShopStandClientGui>(); }
            if (IsHost()) { if (RodStandLook.root != null) RodStandLook.Destroy(); panelVisible = false; return; }
            if (!InRun()) { if (RodStandLook.root != null) RodStandLook.Destroy(); standOn = false; panelVisible = false; hour = -1f; GuideBookLook.Tick(); return; }
            if (standOn && standScene != Scene()) { RodStandLook.Destroy(); standOn = false; pools.Clear(); hotX = hot2X = tableX = float.NaN; }
            string[] lines; lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) Handle(l);
            RodStandLook.Tick();
            GuideBookLook.Tick();
            Keys();
        }
        catch (Exception e) { Log("update error: " + e); }
    }

    // ------------------------------------------------------------------ panel rows (from the host's catalogue)
    public class Row { public string name, desc, kind; public int price; public bool owned, equipped; }
    public static readonly string[] Tabs = { "rod", "skin", "bobber", "item" };
    public static int page, pages = 1;
    public static string hint; public static float hintUntil;
    const string GuideName = "Angler's Guide";
    public static List<Row> Rows(int t, out int m)
    {
        m = money; var rows = new List<Row>();
        string eq = t == 0 ? eqRod : t == 1 ? eqSkin : t == 2 ? eqBob : "";
        foreach (var c in catalog)
        {
            if (c[0] != Tabs[t] || c[3].Contains("not for sale")) continue;
            int pr; int.TryParse(c[2], out pr);
            rows.Add(new Row { name = c[1], desc = c[3], price = pr, owned = pr == 0 || owned.Contains(c[1]), equipped = c[1] == eq, kind = c[0] });
        }
        pages = Math.Max(1, (rows.Count + 8) / 9); page = Mathf.Clamp(page, 0, pages - 1);
        return rows.GetRange(page * 9, Math.Min(9, rows.Count - page * 9));
    }
    public static string Pending { get { return pendingKey >= 0 && Time.unscaledTime < pendingUntil ? pendingItem : null; } }
    public static string Msg { get { return Time.unscaledTime < msgUntil ? msg : null; } }
    static void SetMsg(string s) { msg = s; msgUntil = Time.unscaledTime + 5f; }

    static void Keys()
    {
        bool near = standOn && Dist() <= 4f;
        if (near != panelVisible) { panelVisible = near; pendingKey = -1; if (near && catalog.Count == 0) Say("[FT] SHOPLIST"); }
        bool focus = Focused();
        bool left = Edge(10, 0x25), right = Edge(11, 0x27), e = Edge(12, 0x45), g = Edge(13, 0x47), esc = Edge(14, 0x1B), a = Edge(15, 0x41), d = Edge(0, 0x44);
        bool up = EdgeX(0, 0x26), down = EdgeX(1, 0x28);
        int num = -1; for (int k = 1; k <= 9; k++) if (Edge(k, 0x30 + k)) num = k;
        if (near && !GuideBookLook.IsOpen) FishingKeys.Claim("shop", 2);
        if (!FishingKeys.FreeFor("shop")) return;   // the inventory window is open: its keys, not the stall's
        if (!focus || (!left && !right && !e && !g && !esc && !a && !d && !up && !down && num < 0)) return;
        if (ChatOpen()) return;
        if (GuideBookLook.IsOpen)
        {
            if (g || esc) GuideBookLook.Close();
            else if (left || a) GuideBookLook.Prev();
            else if (right || d) GuideBookLook.Next();
            lastSpread = GuideBookLook.Spread;
            return;
        }
        if (g) { OpenBook("G key"); return; }
        if (!near) return;
        if (e) { OpenBook("E at the stall"); return; }
        if (left) { tab = (tab + Tabs.Length - 1) % Tabs.Length; page = 0; pendingKey = -1; return; }
        if (right) { tab = (tab + 1) % Tabs.Length; page = 0; pendingKey = -1; return; }
        if (up) { page = Math.Max(0, page - 1); return; }
        if (down) { page = Math.Min(pages - 1, page + 1); return; }
        if (num < 0) return;
        int m; var rows = Rows(tab, out m);
        if (num > rows.Count) return;
        Row r = rows[num - 1];
        if (r.equipped) { SetMsg(r.name + " is already equipped."); return; }
        if (r.owned && r.kind == "item") { if (r.name == GuideName) OpenBook("stall panel"); return; }
        if (r.owned) { Say("[FT] SHOPEQUIP|" + r.name); SetMsg("Equipping the " + r.name + "..."); return; }
        if (m < r.price) { SetMsg("Not enough money for the " + r.name + "."); return; }
        if (pendingKey == num && Time.unscaledTime < pendingUntil && pendingItem != null && pendingItem.StartsWith(r.name + " "))
        { pendingKey = -1; Say("[FT] SHOPBUY|" + r.name); SetMsg("Buying the " + r.name + "..."); return; }
        pendingKey = num; pendingUntil = Time.unscaledTime + 4f; pendingItem = r.name + " for $" + r.price.ToString("#,0", Ci);
    }

    // ------------------------------------------------------------------ guide book
    static void OpenBook(string why)
    {
        if (GuideBookLook.IsOpen) return;
        var gi = catalog.Find(c => c[1] == GuideName);
        if (gi != null && !owned.Contains(GuideName))
        {
            int pr; int.TryParse(gi[2], out pr);
            hint = "Buy the Angler's Guide at the Rod Shop ($" + pr.ToString("#,0", Ci) + ")"; hintUntil = Time.unscaledTime + 5f;
            Log("book (" + why + ") refused: guide not owned");
            return;
        }
        try
        {
            string scene = Scene();
            var f = new GuideFacts();
            f.fishingVersion = "the host's fishing game"; f.reader = myName ?? "";
            if (catalog.Count > 0) f.shop = new List<string[]>(catalog);
            f.events = f.eating = f.lureShop = f.pools = f.hot2 = gameplaySeen;
            f.glowBonus = gameplaySeen && catalog.Exists(c => c[1] == "Glow Bobber");
            f.hotspotRadius = hotR; f.hour = hour; f.dayMinutes = dayMinutes; f.eventName = eventName ?? "";
            f.waterY = waterY; f.hotX = hotX; f.hotZ = hotZ; f.hot2X = hot2X; f.hot2Z = hot2Z; f.tableX = tableX; f.tableZ = tableZ;
            if (standOn) { f.standX = standPos.x; f.standZ = standPos.z; }
            foreach (var p in pools.Values) f.poolSpots.Add(p);
            var b = LocalBody(); if (b != null) { Vector3 v = (Vector3)IGet(b, "footPosition"); f.meX = v.x; f.meZ = v.z; }
            MapGuide map = GuideData.For(scene);
            Texture2D sk = null;
            try { sk = Sketch(f, map, scene); } catch (Exception e) { Log("sketch error: " + e.Message); }
            var pages = GuideData.Build(f, map, scene, sk);
            GuideBookLook.Prepare(pages, map != null ? map.title : scene, "fishing notes");
            bool ok = GuideBookLook.Open(Camera.main, lastSpread);
            Log("book (" + why + ") on " + scene + " -> " + ok);
        }
        catch (Exception e) { Log("book error: " + e); }
    }

    static Texture2D Sketch(GuideFacts f, MapGuide map, string scene)
    {
        float cx, cz, size;
        if (map != null) { cx = map.cx; cz = map.cz; size = map.size; }
        else if (!float.IsNaN(f.hotX)) { cx = f.hotX; cz = f.hotZ; size = 300f; }
        else return null;
        var marks = new List<GuideSketch.Mark>();
        if (map != null) foreach (var s in map.spots) marks.Add(new GuideSketch.Mark { x = s.x, z = s.z, r = s.r, c = s.color == GuideData.Purple ? new Color(0.7f, 0.5f, 0.85f) : s.color == GuideData.Green ? new Color(0.12f, 0.5f, 0.15f) : new Color(0.2f, 0.2f, 0.2f), shape = s.color == GuideData.Gold ? GuideSketch.Square : GuideSketch.Ring });
        foreach (var p in f.poolSpots) marks.Add(new GuideSketch.Mark { x = p[0], z = p[1], r = Mathf.Max(p[2], 4f), c = new Color(0.1f, 0.45f, 0.72f), shape = GuideSketch.RingDot });
        if (!float.IsNaN(f.hotX)) marks.Add(new GuideSketch.Mark { x = f.hotX, z = f.hotZ, r = f.hotspotRadius, c = new Color(0.55f, 0.2f, 0.75f), shape = GuideSketch.RingDot });
        if (!float.IsNaN(f.hot2X)) marks.Add(new GuideSketch.Mark { x = f.hot2X, z = f.hot2Z, r = 8f, c = new Color(0.85f, 0.6f, 0.05f), shape = GuideSketch.RingDot });
        if (!float.IsNaN(f.standX)) marks.Add(new GuideSketch.Mark { x = f.standX, z = f.standZ, c = new Color(0.1f, 0.63f, 0.53f), shape = GuideSketch.Square });
        if (!float.IsNaN(f.meX)) marks.Add(new GuideSketch.Mark { x = f.meX, z = f.meZ, c = new Color(0.75f, 0.2f, 0.16f), shape = GuideSketch.Cross });
        int mask = 1;
        try { object w = SGet(ror2.GetType("RoR2.LayerIndex"), "world"); object m = IGet(w, "mask"); mask = Convert.ToInt32(IGet(m, "value")); } catch { }
        return GuideSketch.Build(scene, cx, cz, size, float.IsNaN(f.waterY) ? 0f : f.waterY, mask, marks);
    }
}

// panel at the stall + the fishing clock (IMGUI)
public class ShopStandClientGui : MonoBehaviour
{
    GUIStyle st, title; Texture2D bg; static int errs;
    void OnGUI()
    {
        try
        {
            if (!ShopStandClient.enabled || !ShopStandClient.installed) return;
            if (ShopStandClient.hour >= 0f) TimeHudLook.Draw(ShopStandClient.hour, ShopStandClient.eventName, ShopStandClient.eventLeft, null);
            if (ShopStandClient.hint != null && Time.unscaledTime < ShopStandClient.hintUntil)
            {
                var hs = new GUIStyle(GUI.skin.box) { richText = true, alignment = TextAnchor.MiddleCenter, fontSize = (int)(20 * Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f)) };
                float hw = Screen.width * 0.4f; GUI.Box(new Rect((Screen.width - hw) / 2f, Screen.height * 0.62f, hw, hs.fontSize * 2.6f), "<color=#ffd27a>" + ShopStandClient.hint + "</color>", hs);
            }
            if (!ShopStandClient.panelVisible || GuideBookLook.IsOpen) return;
            if (st == null)
            {
                st = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, wordWrap = false };
                title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 22, fontStyle = FontStyle.Bold };
                bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0.04f, 0.08f, 0.08f, 0.82f)); bg.Apply();
            }
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            st.fontSize = (int)(15 * s); title.fontSize = (int)(22 * s);
            int money; var rows = ShopStandClient.Rows(ShopStandClient.tab, out money);
            float w = 470f * s, lh = 21f * s, h = (128f + Mathf.Max(1, rows.Count) * 21f + 40f) * s;
            var r = new Rect(24f * s, Screen.height * 0.24f, w, h);
            GUI.DrawTexture(r, bg);
            float y = r.y + 8f * s, x = r.x + 14f * s;
            GUI.Label(new Rect(x, y, w, 30f * s), "<color=#66ffcc>ROD SHOP</color>   <size=" + (int)(15 * s) + "><color=#ffd700>$" + money.ToString("#,0") + "</color></size>", title); y += 34f * s;
            string[] names = { "RODS", "SKINS", "BOBBERS", "ITEMS" };
            var tb = new System.Text.StringBuilder("<color=#888888>< </color>");
            for (int i = 0; i < names.Length; i++) tb.Append(i == ShopStandClient.tab ? "<b><color=#ffffff>[" + names[i] + "]</color></b>  " : "<color=#999999>" + names[i] + "</color>  ");
            tb.Append("<color=#888888>></color>"); if (ShopStandClient.pages > 1) tb.Append("   <color=#aaaaaa>page " + (ShopStandClient.page + 1) + "/" + ShopStandClient.pages + " (Up/Down)</color>");
            GUI.Label(new Rect(x, y, w, lh), tb.ToString(), st); y += lh + 6f * s;
            if (rows.Count == 0) { GUI.Label(new Rect(x, y, w, lh), "<color=#aaaaaa>(waiting for the shop list from the host...)</color>", st); y += lh; }
            for (int i = 0; i < rows.Count; i++)
            {
                var it = rows[i];
                string state = it.equipped ? "<color=#ffd700>equipped</color>" : it.owned ? "<color=#88ff88>owned - press " + (i + 1) + (it.kind == "item" ? " to read" : "") + "</color>" : (money >= it.price ? "<color=#ffffff>$" : "<color=#ff7777>$") + it.price.ToString("#,0") + "</color>";
                GUI.Label(new Rect(x, y, w, lh), "<b>" + (i + 1) + "</b>  " + it.name + "  <color=#9aa>" + it.desc + "</color>", st);
                GUI.Label(new Rect(x + w - 190f * s, y, 180f * s, lh), state, new GUIStyle(st) { alignment = TextAnchor.UpperRight });
                y += lh;
            }
            y += 6f * s;
            string pend = ShopStandClient.Pending, msg = ShopStandClient.Msg;
            if (pend != null) GUI.Label(new Rect(x, y, w, lh), "<color=#ffcc44>Press the same number again to buy the " + pend + "</color>", st);
            else if (msg != null) GUI.Label(new Rect(x, y, w, lh), "<color=#ccffee>" + msg + "</color>", st);
            y += lh;
            GUI.Label(new Rect(x, y, w, lh), "<color=#888888>Number = buy (twice) / equip    Left/Right = category    E = guide book</color>", st);
        }
        catch (Exception e) { if (errs++ < 3) File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_shopstand_client.log"), "GUI error: " + e + Environment.NewLine); }
    }
}
