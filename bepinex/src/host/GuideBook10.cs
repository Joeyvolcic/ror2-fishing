// GuideBook10 - host add-on: the physical fishing guide book for JV94 (agents\guide\book\build.ps1: GuideBook.cs + GuideBookLook10.cs
//  + GuideData10.cs + GuideSketch10.cs; helper classes are renamed *9 at build time so every class name is unique).
//  v9 (guide agent): odds from Fishing<N>.WhatIf (the game's own code) for the reader's rod + bobber, fishing ZONE pages
//  (zone table + live positions from Fishing25+, zone sketch), accuracy fixes (agents\guide\audit.md).
//  * G (game window focused, chat closed) or typing !guide: the book rises in front of the camera and opens.
//    Left/Right or A/D: animated page turns. G or Esc: the book shuts and lowers.
//  * Walking up to the rod shop stall and pressing E also opens it (RodStand<N>.bookRequests counter).
//  * Before every opening the facts are re-read from the LIVE game (Fishing<N>, DayNight<N>, RodStand<N>, Roulette<N>) and
//    compared with GuideData10's defaults; any difference is logged to %TEMP%\ror2_guidebook.log ("FACT CHECK") and the
//    live value is printed. Unity work happens only in RoR2Application.onUpdate (main thread).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;

public static class GuideBook10
{
    public static bool enabled = true;
    public static bool isOpen { get { return GuideBookLook10.IsOpen; } }
    public static int testOpen;            // test: >0 opens the book on the next frame (and closes it after testOpen seconds)
    public static string testShotDir;      // test: save every page texture as PNG here, then clear
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static int lastSpread, seenChat, errors, lastStandReq = -1;
    static bool hooked, gDown, escDown, lDown, rDown, aDown, dDown;
    static float testCloseAt = -1f;
    static readonly Dictionary<string, string> soundPick = new Dictionary<string, string>();

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_guidebook.log"), DateTime.Now.ToString("HH:mm:ss ") + "[GuideBook10] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }

    static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.KeyValuePair<float, Type>> newestCache = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.KeyValuePair<float, Type>>();
    static Type Newest(string prefix, string needField)
    {
        System.Collections.Generic.KeyValuePair<float, Type> c;
        if (newestCache.TryGetValue(prefix + "|" + needField, out c) && Time.realtimeSinceStartup < c.Key) return c.Value;   // cached: scanning every type each frame cost ~18 ms
        Type found = NewestScan(prefix, needField);
        newestCache[prefix + "|" + needField] = new System.Collections.Generic.KeyValuePair<float, Type>(Time.realtimeSinceStartup + 2f, found);
        return found;
    }
    static Type NewestScan(string prefix, string needField)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            if (a.GetName().Name.StartsWith(prefix) || FishingScope.IsMod(a))
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith(prefix) || !int.TryParse(t.Name.Substring(prefix.Length), out n) || n <= bn) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                if (needField != null && t.GetField(needField, All) == null && t.GetProperty(needField, All) == null) continue;
                best = t; bn = n;
            }
        return best;
    }
    static object SGet(Type t, string name)
    {
        if (t == null) return null;
        FieldInfo f = t.GetField(name, All); if (f != null) return f.GetValue(null);
        PropertyInfo p = t.GetProperty(name, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null, null);
        return null;
    }
    static object IGet(object o, string name)
    {
        if (o == null) return null;
        FieldInfo f = o.GetType().GetField(name, All); if (f != null) return f.GetValue(o);
        PropertyInfo p = o.GetType().GetProperty(name, All); return p != null ? p.GetValue(o, null) : null;
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t != typeof(GuideBook10) && Regex.IsMatch(t.Name, "^GuideBook\\d+$")) { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); }
        if (hooked) { enabled = true; return; }
        GuideBookLook10.log = Log;
        GuideBookLook10.play = PlayKind;
        seenChat = Chat.readOnlyLog.Count;
        RoR2Application.onUpdate += Tick;
        hooked = true;
        Log("installed (G / !guide / E at the rod stand)");
    }

    static bool Focused() { uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg); return fg == myPid; }
    static bool Key(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    static bool ChatOpen()
    {
        try { foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>()) { object o = IGet(b, "showInput"); if (o is bool && (bool)o) return true; } } catch { }
        return false;
    }
    static bool Edge(int vk, ref bool was) { bool d = Key(vk); bool e = d && !was; was = d; return e; }

    static void Tick()
    {
        if (!enabled) { if (GuideBookLook10.IsOpen) GuideBookLook10.Close(); GuideBookLook10.Tick(); return; }
        try
        {
            GuideBookLook10.Tick();
            if (testOpen > 0 && !GuideBookLook10.IsOpen) { testCloseAt = Time.unscaledTime + testOpen; testOpen = 0; Open("test"); }
            if (testCloseAt > 0 && Time.unscaledTime > testCloseAt) { testCloseAt = -1f; GuideBookLook10.Close(); }
            if (testShotDir != null) { string d = testShotDir; testShotDir = null; Shots(d); }

            ChatCommands();
            Type stand = Newest("RodStand", "bookRequests");
            if (stand != null)
            {
                int req = Convert.ToInt32(SGet(stand, "bookRequests"));
                if (lastStandReq < 0) lastStandReq = req;
                else if (req != lastStandReq) { lastStandReq = req; if (!GuideBookLook10.IsOpen) Open("rod stand"); }
            }
            bool focus = Focused();
            bool g = focus && Edge(0x47, ref gDown), esc = focus && Edge(0x1B, ref escDown);
            bool left = focus && Edge(0x25, ref lDown), right = focus && Edge(0x27, ref rDown), a = focus && Edge(0x41, ref aDown), d2 = focus && Edge(0x44, ref dDown);
            if (!g && !esc && !left && !right && !a && !d2) return;
            if (ChatOpen()) return;
            if (g) { if (GuideBookLook10.IsOpen) GuideBookLook10.Close(); else Open("G key"); return; }
            if (!GuideBookLook10.IsOpen) return;
            if (esc) GuideBookLook10.Close();
            else if (left || a) GuideBookLook10.Prev();
            else if (right || d2) GuideBookLook10.Next();
            lastSpread = GuideBookLook10.Spread;
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    static string LocalName()
    {
        foreach (var lu in LocalUserManager.readOnlyLocalUsersList) if (lu.currentNetworkUser != null) return lu.currentNetworkUser.userName;
        return null;
    }

    static void ChatCommands()
    {
        var log = Chat.readOnlyLog;
        if (log.Count < seenChat) seenChat = 0;
        if (log.Count == seenChat) return;
        string me = LocalName();
        for (int i = seenChat; i < log.Count; i++)
        {
            string s = Regex.Replace(log[i] ?? "", "<[^>]*>", "").Trim();
            if (me != null && (s == me + ": !guide" || s == me + ": !book")) { if (!GuideBookLook10.IsOpen) Open("!guide"); }
        }
        seenChat = log.Count;
    }

    // ------------------------------------------------------------------ sounds
    static readonly Dictionary<string, string[]> SoundCands = new Dictionary<string, string[]> {
        { "open", new[] { "Play_CursedBook_Open", "Play_scav_backpack_open", "Play_UI_menuClick" } },
        { "flip", new[] { "Play_flyingVermin_wingFlap", "Play_vulture_wingFlap", "Play_UI_menuHover", "Play_UI_menuClick" } },
        { "close", new[] { "Play_CursedBook_Close", "Play_UI_menuBack", "Play_UI_menuClick" } },
        { "bump", new[] { "Play_UI_menuBack", "Play_UI_menuClick" } } };
    static void PlayKind(string kind)
    {
        try
        {
            GameObject go = null;
            var lu = LocalUserManager.GetFirstLocalUser();
            if (lu != null && lu.cachedBody != null) go = lu.cachedBody.gameObject;
            if (go == null && Camera.main != null) go = Camera.main.gameObject;
            if (go == null) return;
            string pick; string[] c;
            if (soundPick.TryGetValue(kind, out pick)) { Util.PlaySound(pick, go); return; }
            if (!SoundCands.TryGetValue(kind, out c)) return;
            foreach (string s in c)
            {
                uint id = Util.PlaySound(s, go);
                if (id != 0) { soundPick[kind] = s; Log("sound " + kind + " = " + s + " (id " + id + ")"); return; }
                Log("sound " + s + " -> 0 (not loaded)");
            }
            soundPick[kind] = c[c.Length - 1];
        }
        catch (Exception e) { if (errors++ < 20) Log("sound error: " + e.Message); }
    }

    // ------------------------------------------------------------------ facts (live) + fact check
    static void Check(string what, string def, string live) { if (def != live) Log("FACT CHECK " + what + ": book default '" + def + "' != live '" + live + "' -> using live"); }

    public static GuideFacts10 Facts(out string scene)
    {
        var f = new GuideFacts10();
        scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        f.reader = LocalName() ?? "";
        Type F = Newest("Fishing", "money");
        if (F == null) { Log("no Fishing<N> running - book uses its built-in facts"); return f; }
        f.fishingVersion = F.Name;
        try
        {
            Type school = F.Assembly.GetType("FishSchool");
            if (school != null)
                foreach (string tier in new[] { "White", "Green", "Red", "Lunar" })
                {
                    var arr = SGet(school, tier) as string[]; if (arr == null) continue;
                    string[] def = tier == "White" ? f.white : tier == "Green" ? f.green : tier == "Red" ? f.red : f.lunar;
                    Check("tier " + tier, string.Join(",", def), string.Join(",", arr));
                    if (tier == "White") f.white = arr; else if (tier == "Green") f.green = arr; else if (tier == "Red") f.red = arr; else f.lunar = arr;
                }
            var perks = SGet(F, "PerkText") as IDictionary;
            if (perks != null)
                foreach (DictionaryEntry e in perks)
                {
                    string k = (string)e.Key, v = (string)e.Value, d; f.perks.TryGetValue(k, out d);
                    Check("perk " + k, d, v); f.perks[k] = v;
                }
            var cat = SGet(F, "RodCatalog") as IList;
            if (cat != null)
            {
                var live = new List<string[]>();
                foreach (object it in cat) live.Add(new[] { (string)IGet(it, "kind"), (string)IGet(it, "name"), Convert.ToString(IGet(it, "price"), Ci), (string)IGet(it, "desc") });
                var defs = new Dictionary<string, string>(); foreach (var d in f.shop) defs[d[1]] = d[2] + "/" + d[3];
                foreach (var l in live) { string d; if (defs.TryGetValue(l[1], out d)) Check("shop " + l[1], d, l[2] + "/" + l[3]); }
                f.shop = live;
            }
            foreach (string fn in new[] { "hotspotRadius", "castRange", "whipRange", "whipAngle", "whipCooldown" })
            {
                object v = SGet(F, fn); if (!(v is float)) continue;
                float fv = (float)v, old = fn == "hotspotRadius" ? f.hotspotRadius : fn == "castRange" ? f.castRange : fn == "whipRange" ? f.whipRange : fn == "whipAngle" ? f.whipAngle : f.whipCooldown;
                Check(fn, old.ToString(Ci), fv.ToString(Ci));
                if (fn == "hotspotRadius") f.hotspotRadius = fv; else if (fn == "castRange") f.castRange = fv; else if (fn == "whipRange") f.whipRange = fv; else if (fn == "whipAngle") f.whipAngle = fv; else f.whipCooldown = fv;
            }
            object hw = SGet(F, "hasWater"), ws = SGet(F, "waterScene"), wy = SGet(F, "waterY");
            if (hw is bool && (bool)hw && Convert.ToString(ws) == scene && wy is float) f.waterY = (float)wy;
            object hh = SGet(F, "hasHotspot"), hp = SGet(F, "hotspotPos");
            if (hh is bool && (bool)hh && hp is Vector3) { f.hotX = ((Vector3)hp).x; f.hotZ = ((Vector3)hp).z; }
            // gameplay update (Fishing21+)
            f.events = F.GetField("eventName", All) != null;
            if (f.events) f.eventName = Convert.ToString(SGet(F, "eventName"));
            f.eating = F.GetMethod("Eat", All) != null;
            var eats = SGet(F, "Eats") as IDictionary;
            if (eats != null)
                foreach (DictionaryEntry e in eats)
                {
                    float secs = Convert.ToSingle(IGet(e.Value, "secs")); string txt = Convert.ToString(IGet(e.Value, "text"));
                    string v = secs > 0 ? secs.ToString("0", Ci) + " s: " + txt : txt, d; f.eat.TryGetValue((string)e.Key, out d);
                    Check("eat " + e.Key, d, v); f.eat[(string)e.Key] = v;
                }
            var lures = SGet(F, "Lures") as IDictionary; var order = SGet(F, "LureNames") as string[];
            if (lures != null && order != null)
            {
                f.lureShop = true;
                var defs = new Dictionary<string, string>(); foreach (var d in f.lures) defs[d[0]] = d[1] + "/" + d[2] + "/" + (d[4] ?? "");
                var live = new List<string[]>();
                foreach (string n in order)
                {
                    object ld = lures[n]; string[] maps = IGet(ld, "maps") as string[];
                    var row = new[] { n, Convert.ToString(IGet(ld, "price"), Ci), Convert.ToString(IGet(ld, "uses"), Ci), Convert.ToString(IGet(ld, "text")), maps == null ? null : string.Join(",", maps) };
                    string d; defs.TryGetValue(n, out d); Check("lure " + n, d, row[1] + "/" + row[2] + "/" + (row[4] ?? ""));
                    // map lure texts in the code start with the map names - keep only the effect part after ": "
                    int c = row[3].IndexOf(": "); if (row[4] != null && c > 0) row[3] = row[3].Substring(c + 2);
                    live.Add(row);
                }
                f.lures = live;
            }
            var pools = SGet(F, "pools") as IList;
            f.pools = pools != null;
            if (pools != null) foreach (object p in pools) f.poolSpots.Add(new[] { Convert.ToSingle(IGet(p, "x")), Convert.ToSingle(IGet(p, "z")), Convert.ToSingle(IGet(p, "r")) });
            object h2 = SGet(F, "hasHot2"); f.hot2 = h2 is bool;
            { object r2 = SGet(F, "hot2Radius"); if (r2 is float) f.hot2Radius = (float)r2; }
            // v9: the game's own odds + the reader's gear + zones
            MethodInfo wi = null; foreach (MethodInfo m in F.GetMethods(All)) if (m.Name == "WhatIf" && m.GetParameters().Length == 7) wi = m;
            if (wi != null) f.whatIf = (w, z, r, b, l, e, h) => (Dictionary<string, double>)wi.Invoke(null, new object[] { w, z, r, b, l, e, h });
            MethodInfo bt = F.GetMethod("BaseTableOf", All);
            if (bt != null)
                foreach (string where in new[] { "lake", "hot", "hot2", "pool" })
                {
                    var arr = bt.Invoke(null, new object[] { where }) as double[]; if (arr == null) continue;
                    double[] def = where == "lake" ? f.lake : where == "hot" ? f.hot : where == "hot2" ? f.hot2Table : f.pool;
                    Check("base table " + where, string.Join(",", Array.ConvertAll(def, d => d.ToString(Ci))), string.Join(",", Array.ConvertAll(arr, d => d.ToString(Ci))));
                    if (where == "lake") f.lake = arr; else if (where == "hot") f.hot = arr; else if (where == "hot2") f.hot2Table = arr; else f.pool = arr;
                }
            if (f.reader.Length > 0)
            {
                try
                {
                    object rod = F.GetMethod("RodOf", All).Invoke(null, new object[] { f.reader });
                    f.myRod = (string)IGet(rod, "name"); f.myRange = Convert.ToSingle(IGet(rod, "range")); f.myWindow = Convert.ToSingle(IGet(rod, "window"));
                    MethodInfo bo = F.GetMethod("BobberOf", All); if (bo != null) f.myBobber = (string)bo.Invoke(null, new object[] { f.reader });
                }
                catch (Exception e) { Log("gear lookup: " + e.Message); }
            }
            MethodInfo zd = F.GetMethod("ZoneDefsFor", All), zl = F.GetMethod("ZonesLive", All);
            if (zd != null)
            {
                object zon = SGet(F, "zonesEnabled"); f.zonesOn = !(zon is bool) || (bool)zon;
                f.zoneDefs = (List<string[]>)zd.Invoke(null, new object[] { scene });
                if (zl != null) f.zonesLive = (List<string[]>)zl.Invoke(null, null);
            }
            if (h2 is bool && (bool)h2) { var v = (Vector3)SGet(F, "hot2Pos"); f.hot2X = v.x; f.hot2Z = v.z; }
            f.glowBonus = F.GetField("glowBonusLive", All) != null && (bool)SGet(F, "glowBonusLive");
        }
        catch (Exception e) { Log("facts error: " + e); }
        try
        {
            Type dn = Newest("DayNight", "hour");
            if (dn != null) { f.hour = Convert.ToSingle(SGet(dn, "hour")); float dm = Convert.ToSingle(SGet(dn, "dayMinutes")); Check("dayMinutes", f.dayMinutes.ToString(Ci), dm.ToString(Ci)); f.dayMinutes = dm; }
            Type st = Newest("RodStand", "standPos");
            if (st != null && SGet(st, "placed") is bool && (bool)SGet(st, "placed")) { var v = (Vector3)SGet(st, "standPos"); f.standX = v.x; f.standZ = v.z; }
            f.stand = st != null;
            Type hc = Newest("HotspotCompass", null);
            f.compass = hc != null;
            if (hc != null) foreach (Type t in SafeTypes(hc.Assembly)) if (t.Name.StartsWith("GadgetLook") && t.GetField("zoneAt", All) != null) f.rangeZones = true;
            Type sw = Newest("Swim", null);
            if (sw != null)
            {
                f.swim = true;
                Type look = sw.Assembly.GetType("FishingSwimLook");
                if (look != null) { object a = SGet(look, "SurfaceSeconds"), u = SGet(look, "UnderSeconds"); if (a != null) f.swimSurface = Convert.ToSingle(a); if (u != null) f.swimUnder = Convert.ToSingle(u); }
            }
            Type dr = Newest("DrownRespawn", null);
            if (dr != null) { f.drownRespawn = true; object d = SGet(dr, "RespawnDelay"); if (d != null) f.respawnDelay = Convert.ToSingle(d); }
            f.waterCmd = Newest("WaterLevel", null) != null;
            f.merchant = Newest("FishMerchant", null) != null;
            if (hc != null)
            {
                object tp = SGet(hc, "trackerPrice") ?? SGet(hc, "TrackerPrice");
                if (tp is int) f.trackerPrice = "$" + ((int)tp).ToString("#,0", Ci); else if (tp is string) f.trackerPrice = (string)tp;
            }
            foreach (var it in f.shop) if (it[1] == "Cast Distance Tracker") f.trackerPrice = "$" + int.Parse(it[2], Ci).ToString("#,0", Ci);
            Type rt = Newest("Roulette", "tablePos");
            if (rt != null && SGet(rt, "placed") is bool && (bool)SGet(rt, "placed")) { var v = (Vector3)SGet(rt, "tablePos"); f.tableX = v.x; f.tableZ = v.z; f.roulette = true; }
            var lu = LocalUserManager.GetFirstLocalUser();
            if (lu != null && lu.cachedBody != null) { f.meX = lu.cachedBody.footPosition.x; f.meZ = lu.cachedBody.footPosition.z; }
        }
        catch (Exception e) { Log("facts(2) error: " + e); }
        return f;
    }

    public static Texture2D Sketch(GuideFacts10 f, MapGuide10 map, string scene)
    {
        float cx, cz, size;
        if (map != null) { cx = map.cx; cz = map.cz; size = map.size; }
        else if (!float.IsNaN(f.hotX)) { cx = f.hotX; cz = f.hotZ; size = 300f; }
        else if (!float.IsNaN(f.meX)) { cx = f.meX; cz = f.meZ; size = 300f; }
        else return null;
        var marks = new List<GuideSketch10.Mark>();
        Color purple = new Color(0.55f, 0.2f, 0.75f), purpleFaint = new Color(0.7f, 0.5f, 0.85f), green = new Color(0.12f, 0.5f, 0.15f), gold = new Color(0.85f, 0.6f, 0.05f);
        if (map != null)
            foreach (var s in map.spots)
            {
                if (s.color == GuideData10.Purple) marks.Add(new GuideSketch10.Mark { x = s.x, z = s.z, r = s.r, c = purpleFaint, shape = GuideSketch10.Ring });
                else if (s.color == GuideData10.Green) marks.Add(new GuideSketch10.Mark { x = s.x, z = s.z, r = s.r, c = green, shape = GuideSketch10.Ring });
                else if (s.color == GuideData10.Gold) { if (float.IsNaN(f.tableX)) marks.Add(new GuideSketch10.Mark { x = s.x, z = s.z, c = gold, shape = GuideSketch10.Square }); }
                else marks.Add(new GuideSketch10.Mark { x = s.x, z = s.z, r = s.r, c = new Color(0.2f, 0.2f, 0.2f), shape = GuideSketch10.Ring });
            }
        foreach (var p in f.poolSpots) marks.Add(new GuideSketch10.Mark { x = p[0], z = p[1], r = Math.Max(p[2], 4f), c = new Color(0.1f, 0.45f, 0.72f), shape = GuideSketch10.RingDot });
        // live hotspot positions are NOT drawn: hotspots are found with the HUD compass / range finder (HotspotCompass<N>)
        if (!float.IsNaN(f.tableX)) marks.Add(new GuideSketch10.Mark { x = f.tableX, z = f.tableZ, c = gold, shape = GuideSketch10.Square });
        if (!float.IsNaN(f.standX)) marks.Add(new GuideSketch10.Mark { x = f.standX, z = f.standZ, c = new Color(0.1f, 0.63f, 0.53f), shape = GuideSketch10.Square });
        if (!float.IsNaN(f.meX)) marks.Add(new GuideSketch10.Mark { x = f.meX, z = f.meZ, c = new Color(0.75f, 0.2f, 0.16f), shape = GuideSketch10.Cross });
        ZoneMarks(f, marks);
        float wy = f.waterY;
        if (float.IsNaN(wy) && map != null && map.scene == scene) wy = 42.5f;
        return GuideSketch10.Build(scene, cx, cz, size, wy, LayerIndex.world.mask.value, marks);
    }

    static void ZoneMarks(GuideFacts10 f, List<GuideSketch10.Mark> marks)
    {
        for (int i = 0; i < f.zoneDefs.Count; i++)
            foreach (var z in f.zonesLive)
                if (z[1] == f.zoneDefs[i][0])
                    marks.Add(new GuideSketch10.Mark { x = float.Parse(z[2], Ci), z = float.Parse(z[4], Ci), r = float.Parse(z[5], Ci), c = GuideData10.ZoneColors[i % GuideData10.ZoneColors.Length], shape = GuideSketch10.RingDot, label = (char)('A' + i) });
    }
    // second sketch framed on the zones (+ you and the stall)
    public static Texture2D ZoneSketch(GuideFacts10 f, string scene)
    {
        if (f.zonesLive.Count == 0 || float.IsNaN(f.waterY)) return null;
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var z in f.zonesLive) { float x = float.Parse(z[2], Ci), zz = float.Parse(z[4], Ci), r = float.Parse(z[5], Ci); x0 = Math.Min(x0, x - r); x1 = Math.Max(x1, x + r); z0 = Math.Min(z0, zz - r); z1 = Math.Max(z1, zz + r); }
        if (!float.IsNaN(f.meX) && Math.Abs(f.meX - (x0 + x1) / 2) < 250 && Math.Abs(f.meZ - (z0 + z1) / 2) < 250) { x0 = Math.Min(x0, f.meX); x1 = Math.Max(x1, f.meX); z0 = Math.Min(z0, f.meZ); z1 = Math.Max(z1, f.meZ); }
        float size = Mathf.Ceil(Math.Max(200f, Math.Max(x1 - x0, z1 - z0) + 80f) / 10f) * 10f, cx = (x0 + x1) / 2f, cz = (z0 + z1) / 2f;
        var marks = new List<GuideSketch10.Mark>();
        ZoneMarks(f, marks);
        if (!float.IsNaN(f.standX)) marks.Add(new GuideSketch10.Mark { x = f.standX, z = f.standZ, c = new Color(0.1f, 0.63f, 0.53f), shape = GuideSketch10.Square });
        if (!float.IsNaN(f.meX)) marks.Add(new GuideSketch10.Mark { x = f.meX, z = f.meZ, c = new Color(0.75f, 0.2f, 0.16f), shape = GuideSketch10.Cross });
        return GuideSketch10.Build("zones", scene, cx, cz, size, f.waterY, LayerIndex.world.mask.value, marks);
    }

    public static List<GPage10> Pages(out string scene, out MapGuide10 map)
    {
        GuideFacts10 f = Facts(out scene);
        map = GuideData10.For(scene);
        Texture2D sk = null;
        try { sk = Sketch(f, map, scene); } catch (Exception e) { Log("sketch error: " + e); }
        try { f.zoneSketch = ZoneSketch(f, scene); } catch (Exception e) { Log("zone sketch error: " + e); }
        Log("facts: " + f.fishingVersion + " whatIf=" + (f.whatIf != null) + " gear=" + f.myRod + "/" + f.myBobber + " zones " + f.zonesLive.Count + "/" + f.zoneDefs.Count + " swim=" + f.swim + " respawn=" + f.drownRespawn + " roulette=" + f.roulette + " merchant=" + f.merchant + " rangeZones=" + f.rangeZones);
        return GuideData10.Build(f, map, scene, sk);
    }

    // ---------------------------------------------------------------- the book is a shop item ("Angler's Guide")
    public const string ItemName = "Angler's Guide";
    public static readonly string[] InventoryItem = { "Item", ItemName, "book" };   // kind, name, icon id - for Inventory<N>
    public static string hint; public static float hintUntil;
    static GameObject guiGo;
    public static bool Owns(string who)
    {
        Type F = Newest("Fishing", "money");
        MethodInfo m = F == null ? null : F.GetMethod("OwnsItem", All);
        if (m == null) return true;                      // a fishing version without shop items: the book is free
        return who != null && (bool)m.Invoke(null, new object[] { who, ItemName });
    }
    static string GuidePrice()
    {
        Type F = Newest("Fishing", "money"); var cat = SGet(F, "RodCatalog") as IList;
        if (cat != null) foreach (object it in cat) if ((string)IGet(it, "name") == ItemName) return "$" + Convert.ToInt32(IGet(it, "price")).ToString("#,0", Ci);
        return "$250";
    }
    // ---- Inventory<N> provider (agents\inventory: any enabled type with these two public static methods)
    //   row = {kind, id, name, rarity, desc, extra, equipped}; "Use" opens the book (host: here; a guest: on their own
    //   machine via the hidden line "[FT] GUIDEOPEN|<name>" that FISHING.exe's ShopStandClient listens for)
    public static IEnumerable InventoryItems(string player)
    {
        var l = new List<string[]>();
        if (player != null && Owns(player) && OwnsReal(player))
            l.Add(new[] { "item", "anglers_guide", ItemName, "common", "The fishing guide book: best times, spots, fish, rods, tips. Use = read it (or press G).", "", "0" });
        return l;
    }
    public static void InventoryUse(string player, string id)
    {
        if (id != "anglers_guide") return;
        if (player == LocalName()) { Open("inventory"); return; }
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = "[FT] GUIDEOPEN|" + player });
        Log("inventory use by guest " + player + " -> [FT] GUIDEOPEN");
    }
    static bool OwnsReal(string who) { Type F = Newest("Fishing", "money"); return F != null && F.GetMethod("OwnsItem", All) != null; }   // only list it when it is a real shop item
    public static bool Use(string who) { if (who != null && who != LocalName()) return false; Open("inventory"); return GuideBookLook10.IsOpen; }
    static Texture2D icon;
    public static Texture2D Icon()
    {
        if (icon != null) return icon;
        int n = 64; icon = new Texture2D(n, n, TextureFormat.RGBA32, false); icon.wrapMode = TextureWrapMode.Clamp;
        Color cover = new Color(0.45f, 0.14f, 0.08f), page = new Color(0.95f, 0.9f, 0.76f), gold = new Color(1f, 0.8f, 0.3f), none = new Color(0, 0, 0, 0);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                Color c = none;
                if (x >= 12 && x < 54 && y >= 6 && y < 58) c = cover;
                if (x >= 50 && x < 54 && y >= 9 && y < 55) c = page;                       // page edges
                if (x >= 12 && x < 17 && y >= 6 && y < 58) c = cover * 0.75f;              // spine
                if ((y == 46 || y == 47 || y == 16 || y == 17) && x >= 22 && x < 46) c = gold;   // gold bands
                if (x >= 27 && x < 41 && y >= 26 && y < 36 && Mathf.Abs(x - 34) + Mathf.Abs(y - 31) < 7) c = gold;   // emblem
                c.a = c == none ? 0f : 1f; icon.SetPixel(x, y, c);
            }
        icon.Apply(); return icon;
    }

    static void Open(string why)
    {
        if (why != "test")
        {
            string me = LocalName();
            if (!Owns(me))
            {
                hint = "Buy the Angler's Guide at the Rod Shop (" + GuidePrice() + ")"; hintUntil = Time.unscaledTime + 5f;
                if (guiGo == null) { guiGo = new GameObject("GuideBook1Hint"); UnityEngine.Object.DontDestroyOnLoad(guiGo); guiGo.AddComponent<GuideBook10Gui>(); }
                Log("open (" + why + ") refused: " + me + " doesn't own the " + ItemName);
                return;
            }
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string scene; MapGuide10 map;
            var pages = Pages(out scene, out map);
            int n = GuideBookLook10.Prepare(pages, map != null ? map.title : scene, "fishing notes");
            bool ok = GuideBookLook10.Open(Camera.main, lastSpread);
            Log("open (" + why + ") on " + scene + ": " + pages.Count + " sections, " + n + " pages, " + sw.ElapsedMilliseconds + " ms, camera " + (Camera.main != null ? Camera.main.name : "none") + " -> " + ok);
        }
        catch (Exception e) { Log("open error: " + e); }
    }

    static void Shots(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string scene; MapGuide10 map;
            var pages = Pages(out scene, out map);
            int n = GuideBookLook10.Prepare(pages, map != null ? map.title : scene, "fishing notes");
            for (int i = -1; i < n; i++)
            {
                var t = GuideBookLook10.ReadPage(i);
                if (t == null) continue;
                File.WriteAllBytes(System.IO.Path.Combine(dir, i < 0 ? "cover.png" : "page" + (i + 1).ToString("00") + ".png"), ImageConversion.EncodeToPNG(t));
                UnityEngine.Object.Destroy(t);
            }
            Log("saved " + (n + 1) + " page PNGs to " + dir);
        }
        catch (Exception e) { Log("shots error: " + e); }
    }
}

// "Buy the Angler's Guide at the Rod Shop ($250)" hint (IMGUI, 5 s)
public class GuideBook10Gui : MonoBehaviour
{
    GUIStyle st;
    void OnGUI()
    {
        if (!GuideBook10.enabled || GuideBook10.hint == null || Time.unscaledTime > GuideBook10.hintUntil) return;
        if (st == null) st = new GUIStyle(GUI.skin.box) { richText = true, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f); st.fontSize = (int)(20 * s);
        float w = 560f * s, h = 54f * s;
        GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.62f, w, h), "<color=#ffd27a>" + GuideBook10.hint + "</color>", st);
    }
}
