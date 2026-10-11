// GuideData10 - content of the fishing guide book (shared by the host add-on GuideBook<N> and FISHING.exe).
//  * GuideFacts10: every number the book prints. Defaults below were copied from the fishing code; the host add-on
//    overwrites them from the LIVE Fishing<N> / DayNight<N> / Swim<N> / ... by reflection before each opening and logs
//    any default that disagrees with the live game (FACT CHECK).
//  * ACCURACY RULE (Joey, 2026-10-10): the book only calls something better when the game's own odds say so. Every odds
//    number is computed by Fishing<N>.WhatIf (the real OddsTable / BiteMult) when the host has it (Fishing25+); the
//    built-in formula below is only the fallback for older versions / guests. Claims are audited in agents\guide\audit.md.
//  * MapGuide10: per-map notes (MapGuides). Fishing ZONES come from the fishing mod's own zone table (f.zoneDefs/zonesLive).
//  * Build(facts, map, scene, sketch) -> pages (GPage10 = title + blocks); GuideBookLook10 lays them out and renders them.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public class GBlock10
{
    public const int Heading = 0, Para = 1, Table = 2, Image = 3, Space = 4, Small = 5, Bullet = 6;
    public int kind; public string text; public Color color = new Color(0.06f, 0.04f, 0.02f);
    public string[][] rows; public float[] cols; public Color[] rowColors;
    public Texture2D img; public float imgH; public string caption;
}
public class GPage10 { public string title; public readonly List<GBlock10> blocks = new List<GBlock10>(); public int number; public bool cover; public string coverSub; }

public class GSpot10 { public string name, note; public float x, z; public Color color; public float r; }

public class MapGuide10
{
    public string scene, title, lakeNote;
    public float cx, cz, size;                  // top-down sketch window (world X/Z centre, edge length in m)
    public string[] waterLevels;                // lake heights seen in real runs (from ror2_fishing.log)
    public readonly List<GSpot10> spots = new List<GSpot10>();
    public string[] spotNotes, castNotes;
}

public class GuideFacts10
{
    public string[] white = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" };
    public string[] green = { "Bass", "Salmon", "Pufferfish" };
    public string[] red = { "Swordfish", "Golden Koi", "Anglerfish" };
    public string[] lunar = { "Moon Eel", "Ghost Fish" };
    public int[] valueLo = { 10, 40, 150, 500 }, valueHi = { 30, 80, 300, 500 };
    // Fishing<N>_Gameplay.cs BaseTable: boot / white / green / red / lunar
    public double[] lake = { 0.10, 0.60, 0.30, 0.08, 0.02 }, hot = { 0.0, 0.25, 0.35, 0.30, 0.10 };
    public double[] hot2Table = { 0.0, 0.15, 0.30, 0.40, 0.15 }, pool = { 0.08, 0.70, 0.25, 0.045, 0.005 };
    public Dictionary<string, string> perks = new Dictionary<string, string> {
        { "Minnow", "+2% move speed" }, { "Perch", "+2% sprint speed" }, { "Trout", "+3% jump height" }, { "Bass", "+1% move speed, +1% jump height" },
        { "Salmon", "+1 extra jump per 3 Salmon" }, { "Swordfish", "+5% move speed" }, { "Golden Koi", "+4% sprint speed, next 3 catches x2" },
        { "Moon Eel", "free revive" }, { "Old Boot", "+2% fishing luck" }, { "Clownfish", "+2% jump height" }, { "Catfish", "+1% move speed, +1% sprint speed" },
        { "Pufferfish", "+1 extra jump per 3 Pufferfish" }, { "Anglerfish", "+6% sprint speed" }, { "Ghost Fish", "+6% move speed, +6% jump height" } };
    public Dictionary<string, string> eat = new Dictionary<string, string> {
        { "Minnow", "10 s: speed burst" }, { "Salmon", "20 s: triple jump" }, { "Pufferfish", "20 s: spiky aura (pricks players who get close)" },
        { "Anglerfish", "30 s: glowing lantern + see the fish school through walls" }, { "Ghost Fish", "15 s: ghostly: can't drown" },
        { "Golden Koi", "120 s: next catch x3" }, { "Moon Eel", "60 s: instant revive charge" }, { "Swordfish", "30 s: rod whip knocks players back" },
        { "Perch", "60 s: steady hands: +15% reel chance" }, { "Trout", "60 s: quick bites: bites 30% faster" }, { "Clownfish", "60 s: lucky: red & lunar fish x1.3" },
        { "Catfish", "60 s: whiskers: green fish x1.5" }, { "Bass", "60 s: big haul: catches worth +50%" }, { "Old Boot", "nothing. You ate a boot." } };
    public List<string[]> shop = new List<string[]> {
        new[] { "rod", "Stick Rod", "0", "a crooked branch: -25% cast range, bites 20% slower, -20% reel window, -10% rare fish" }, new[] { "rod", "Basic Rod", "500", "a real rod: no bonus, no penalty" }, new[] { "rod", "Long Cast Rod", "2000", "+25% cast range" }, new[] { "rod", "Steady Rod", "3000", "+50% reel window" },
        new[] { "rod", "Quick Rod", "3500", "bites 30% faster" }, new[] { "rod", "Lucky Rod", "5000", "+20% rare fish" }, new[] { "rod", "Golden Rod", "15000", "+10% of everything" },
        new[] { "skin", "Default", "0", "wood" }, new[] { "skin", "Bamboo", "500", "bamboo" }, new[] { "skin", "Carbon", "1000", "black carbon" }, new[] { "skin", "Neon", "1500", "cyan/pink pulse" },
        new[] { "skin", "Gold", "2000", "metallic gold" }, new[] { "skin", "Lunar Glass", "2000", "translucent blue" }, new[] { "skin", "Void", "2500", "purple glow" },
        new[] { "bobber", "Plain", "0", "red ball" }, new[] { "bobber", "Classic", "500", "red/white" }, new[] { "bobber", "Star", "1000", "gold star" },
        new[] { "bobber", "Duck", "1250", "rubber duck" }, new[] { "bobber", "Skull", "1500", "skull" },
        new[] { "bobber", "Glow Bobber", "2500", "glows; at night bites 25% faster" }, new[] { "bobber", "Firefly Bobber", "4000", "glows; at night +15% rare fish" },
        new[] { "bobber", "Lantern Bobber", "6000", "glows; at night bites 25% faster and +15% rare fish" } };
    public List<string[]> lures = new List<string[]> {
        new[] { "Spinner", "300", "20", "Trout & Perch x2.5", null }, new[] { "Glow Lure", "800", "20", "Anglerfish, Moon Eel, Ghost Fish x2", null },
        new[] { "Deep Jig", "1500", "20", "red fish x1.5, bites 50% slower", null }, new[] { "Golden Fly", "3000", "15", "Golden Koi x3", null },
        new[] { "Feather Lure", "600", "20", "Ghost Fish, Salmon, Minnow x2", "skymeadow" },
        new[] { "Coral Lure", "600", "20", "Clownfish x2.5, Swordfish x2", "blackbeach,blackbeach2,goldshores,shipgraveyard" },
        new[] { "Frost Lure", "600", "20", "Trout, Salmon x2, Moon Eel x1.5", "frozenwall,snowyforest" },
        new[] { "Swamp Lure", "600", "20", "Catfish x2.5, Bass x2, Perch x1.5", "golemplains,golemplains2,foggyswamp,wispgraveyard" },
        new[] { "Sand Lure", "600", "20", "Pufferfish x2.5, Catfish x2, Anglerfish x1.5", "goolake,ancientloft" } };
    public bool events, eating, lureShop, pools, hot2, glowBonus, stand = true;
    public bool compass, rangeZones; public string trackerPrice = "";
    public float hotspotRadius = 10f, hot2Radius = 8f;
    public float castRange = 200f, whipRange = 20f, whipAngle = 60f, whipCooldown = 1.5f;
    public float dayMinutes = 10f;
    public float glowBite = 0.75f, glowRare = 0.15f;
    public string fishingVersion = "Fishing19", reader = "";
    public float hour = -1f; public string eventName = "";
    public float waterY = float.NaN, hotX = float.NaN, hotZ = float.NaN, hot2X = float.NaN, hot2Z = float.NaN, standX = float.NaN, standZ = float.NaN, tableX = float.NaN, tableZ = float.NaN, meX = float.NaN, meZ = float.NaN;
    public List<float[]> poolSpots = new List<float[]>();   // x, z, r
    // ---- guide 9: live odds, the reader's gear, zones, the add-ons that change the rules
    public Func<string, string, string, string, string, string, float, Dictionary<string, double>> whatIf;   // where, zone, rod, bobber, lure, event, hour
    public string myRod = "Stick Rod", myBobber = "Plain";
    public float myRange = 1f, myWindow = 1f;
    public List<string[]> zoneDefs = new List<string[]>();     // name, r, cue, target, blurb, rules, rule texts, table scene
    public List<string[]> zonesLive = new List<string[]>();    // idx, name, x, y, z, r, cue, target, scene
    public bool zonesOn;
    public Texture2D zoneSketch;
    public bool swim; public float swimSurface = 60f, swimUnder = 15f;
    public bool drownRespawn; public float respawnDelay = 10f;
    public bool waterCmd, roulette, merchant;
}

public static class GuideData10
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static readonly Color Ink = new Color(0.06f, 0.04f, 0.02f), Faint = new Color(0.28f, 0.2f, 0.12f), Red = new Color(0.55f, 0.06f, 0.03f),
        Green = new Color(0.05f, 0.32f, 0.07f), Blue = new Color(0.04f, 0.16f, 0.45f), Gold = new Color(0.5f, 0.32f, 0f), Purple = new Color(0.34f, 0.08f, 0.47f);
    // zone letters / colours (also used by the sketch)
    public static readonly Color[] ZoneColors = { new Color(0.0f, 0.5f, 0.45f), new Color(0.8f, 0.35f, 0.0f), new Color(0.65f, 0.05f, 0.45f), new Color(0.3f, 0.35f, 0.0f), new Color(0.1f, 0.3f, 0.75f), new Color(0.5f, 0.2f, 0.1f) };
    public static string ZoneHex(int i) { return "#" + ColorUtility.ToHtmlStringRGB(ZoneColors[i % ZoneColors.Length]); }

    // ------------------------------------------------------------------ per-map tables
    public static readonly List<MapGuide10> MapGuides = new List<MapGuide10>();
    static GuideData10()
    {
        var sf = new MapGuide10
        {
            scene = "snowyforest", title = "Siphoned Forest", cx = -125f, cz = 140f, size = 340f,
            lakeNote = "The lake is not a fixed pond: when the stage starts, the water is laid 3 m below the lowest player standing on the ground and covers every hollow lower than that. Where it ends up depends on where you land: at Y 42.5 the frozen ground east and north-east of the drop spot floods into a wide lake; a lower level (Y 26.8) flooded a different hollow to the south-east instead.",
            waterLevels = new[] { "Y 42.48 (10:06 run)", "Y 42.57 (13:14 and 13:52 runs)", "Y 28.57 (02:21 run, players landed lower)", "Y 26.80 (14:06 run, lake around X 0..110, Z -240..-115)", "Y 49.11 (14:18 run)" },
            spotNotes = new[] {
                "The fishing mod first puts the purple hotspot 15-35 m from a random player. On this map that often lands on the snowy land around the drop spot, so it is moved to open water within 150 m of a player on land.",
                "Where it ended up in the Y 42.5 runs (logs): -124.7,51.1 / -123.1,197.0 / -113.5,142.2 / -129.6,107.5 / -64.9,45.9 / -144.5,149.8 / -130.4,125.4 / -93.6,191.4. It is random every stage - these are history, not a promise.",
                "The hotspot moves again after someone lands a red or lunar fish in it, or when everyone has been more than 120 m away for 30 s." },
            castNotes = new[] {
                "Where people cast most here (55 casts that landed on water, Y 42.5 runs): X -100 to -150, Z 150 to 200 - the shore north of the map ring; second: X -125 to -150, Z 100 to 150, next to the ring.",
                "These shores are POPULAR, not better: the open lake has the same odds everywhere. Only the hotspots, the ponds and the fishing zones change the odds.",
                "7 casts hit the ground and 1 player went into the water there - the shore is shallow and the lake edge is easy to step into." }
        };
        sf.spots.Add(new GSpot10 { name = "Map ring (drop spot)", x = -170.8f, z = 126.1f, r = 19.3f, color = new Color(0.25f, 0.25f, 0.25f), note = "27 portal podiums, radius 19 m" });
        sf.spots.Add(new GSpot10 { name = "Popular shore (most casts)", x = -125f, z = 175f, r = 25f, color = Green, note = "X -100..-150, Z 150..200" });
        sf.spots.Add(new GSpot10 { name = "Popular shore by the ring", x = -137f, z = 137f, r = 13f, color = Green, note = "X -125..-150, Z 125..150" });
        sf.spots.Add(new GSpot10 { name = "Hotspot seen", x = -93.6f, z = 191.4f, r = 10f, color = Purple, note = "13:14 run" });
        sf.spots.Add(new GSpot10 { name = "Hotspot seen", x = -113.5f, z = 142.2f, r = 10f, color = Purple, note = "" });
        sf.spots.Add(new GSpot10 { name = "Hotspot seen", x = -123.1f, z = 197.0f, r = 10f, color = Purple, note = "10:09 run" });
        sf.spots.Add(new GSpot10 { name = "Hotspot seen", x = -124.7f, z = 51.1f, r = 10f, color = Purple, note = "10:06 run" });
        sf.spots.Add(new GSpot10 { name = "Hotspot seen", x = -64.9f, z = 45.9f, r = 10f, color = Purple, note = "" });
        MapGuides.Add(sf);
    }
    public static MapGuide10 For(string scene) { foreach (var m in MapGuides) if (m.scene == scene) return m; return null; }

    // ------------------------------------------------------------------ odds
    public static string TierOf(GuideFacts10 f, string sp)
    {
        if (Array.IndexOf(f.lunar, sp) >= 0) return "Lunar"; if (Array.IndexOf(f.red, sp) >= 0) return "Tier3";
        if (Array.IndexOf(f.green, sp) >= 0) return "Tier2"; if (Array.IndexOf(f.white, sp) >= 0) return "Tier1"; return "Boot";
    }
    // fallback only (older Fishing<N> without WhatIf, guests): same formula as Fishing<N>_Gameplay.cs, Basic Rod, no zone
    public static double EventMult(string ev, string sp, string tier)
    {
        double m = 1;
        if (ev.Contains("Rain")) { if (sp == "Catfish" || sp == "Trout" || sp == "Bass") m *= 2; }
        if (ev.Contains("Golden hour")) { if (tier == "Tier3" || tier == "Lunar") m *= 1.5; if (sp == "Golden Koi" || sp == "Salmon") m *= 2; }
        if (ev.Contains("Night")) { if (sp == "Anglerfish" || sp == "Moon Eel" || sp == "Ghost Fish") m *= 3; if (tier == "Tier1") m *= 0.6; }
        if (ev.Contains("Storm")) { if (tier == "Tier3") m *= 2; if (tier == "Lunar") m *= 2.5; }
        return m;
    }
    public static Dictionary<string, double> Odds(GuideFacts10 f, double[] b, string ev, double rareBonus)
    {
        var w = new Dictionary<string, double>(); string[][] tiers = { f.white, f.green, f.red, f.lunar };
        for (int t = 0; t < 4; t++)
            foreach (string sp in tiers[t])
                w[sp] = (1 - b[0]) * b[t + 1] / tiers[t].Length * EventMult(ev, sp, TierOf(f, sp)) * (t >= 2 ? 1 + rareBonus : 1);
        double fish = 0; foreach (var kv in w) fish += kv.Value;
        var o = new Dictionary<string, double>(); foreach (var kv in w) o[kv.Key] = fish > 0 ? kv.Value / fish * (1 - b[0]) : 0;
        o["Old Boot"] = b[0]; o["~bite"] = 1;
        return o;
    }
    static double[] TableOf(GuideFacts10 f, string where) { return where == "hot" ? f.hot : where == "hot2" ? f.hot2Table : where == "pool" ? f.pool : f.lake; }
    // the game's own odds (Fishing<N>.WhatIf) when available
    public static Dictionary<string, double> W(GuideFacts10 f, string where, string zone, string rod, string bobber, string lure, string ev, float hour)
    {
        if (f.whatIf != null) { try { return f.whatIf(where, zone, rod, bobber, lure, ev, hour); } catch { } }
        return Odds(f, TableOf(f, where), ev ?? "", 0);
    }
    static string TimeEv(float h) { return h < 5f || h >= 20f ? "Night" : h < 7f || h >= 17.5f ? "Golden hour" : ""; }
    static string Ev(float h, string weather) { string t = TimeEv(h); return t.Length == 0 ? (weather.Length == 0 ? "Clear" : weather) : weather.Length == 0 ? t : t + "+" + weather; }
    static double Sum(GuideFacts10 f, Dictionary<string, double> d, string target) { double s = 0, v; foreach (string k in Keys(f, target)) if (d.TryGetValue(k, out v)) s += v; return s; }
    static string[] Keys(GuideFacts10 f, string target)
    {
        switch (target)
        {
            case "white": return f.white; case "green": return f.green; case "red": return f.red; case "lunar": return f.lunar;
            case "rare": { var l = new List<string>(f.red); l.AddRange(f.lunar); return l.ToArray(); }
        }
        var p = target.Split(new[] { ',' }); for (int i = 0; i < p.Length; i++) p[i] = p[i].Trim(); return p;
    }
    static string TargetName(string target)
    {
        switch (target) { case "white": return "white fish"; case "green": return "green fish"; case "red": return "red fish"; case "lunar": return "lunar fish"; case "rare": return "red & lunar fish"; }
        return target.Replace(",", " & ");
    }
    static string Pct(double v) { return (v * 100).ToString(v < 0.1 ? "0.0" : "0", Ci) + "%"; }
    static string Pct2(double v) { return (v * 100).ToString(v < 0.1 ? "0.0" : "0.0", Ci) + "%"; }
    static string Money(int v) { return "$" + v.ToString("#,0", Ci); }
    public static string Clock(float h) { int m = (int)(Mathf.Repeat(h, 24f) * 60f); return (m / 60).ToString("00") + ":" + (m % 60).ToString("00"); }
    static string Gear(GuideFacts10 f) { return f.myRod + (f.myBobber != "Plain" ? " + " + f.myBobber : ""); }

    // ------------------------------------------------------------------ pages
    static GBlock10 H(string t) { return new GBlock10 { kind = GBlock10.Heading, text = t }; }
    static GBlock10 P(string t) { return new GBlock10 { kind = GBlock10.Para, text = t }; }
    static GBlock10 P(string t, Color c) { return new GBlock10 { kind = GBlock10.Para, text = t, color = c }; }
    static GBlock10 S(string t) { return new GBlock10 { kind = GBlock10.Small, text = t, color = Faint }; }
    static string ShopPrice(GuideFacts10 f, string item)   // ", $1,500" from the live shop list, "" if unknown
    {
        foreach (var it in f.shop) if (it.Length > 2 && it[1] == item) { int p; if (int.TryParse(it[2], NumberStyles.Integer, Ci, out p)) return ", $" + p.ToString("#,0", Ci); }
        return "";
    }
    static GBlock10 B(string t) { return new GBlock10 { kind = GBlock10.Bullet, text = t }; }
    static GBlock10 Sp() { return new GBlock10 { kind = GBlock10.Space }; }
    static GBlock10 T(float[] cols, params string[][] rows) { return new GBlock10 { kind = GBlock10.Table, cols = cols, rows = rows }; }
    static string Live(bool on, string what) { return on ? "LIVE in this game" : what + " - not running in this game yet"; }
    static string TierLineW(GuideFacts10 f, Dictionary<string, double> d) { double b = d["Old Boot"]; return (b > 0 ? Pct(b) + " boots, " : "no boots, ") + "white " + Pct(Sum(f, d, "white")) + " / green " + Pct(Sum(f, d, "green")) + " / red " + Pct(Sum(f, d, "red")) + " / lunar " + Pct(Sum(f, d, "lunar")); }
    static string TierLine(double[] b) { return (b[0] > 0 ? Pct(b[0]) + " boots, " : "no boots, ") + "white " + Pct(b[1] * (1 - b[0])) + " / green " + Pct(b[2] * (1 - b[0])) + " / red " + Pct(b[3] * (1 - b[0])) + " / lunar " + Pct(b[4] * (1 - b[0])); }

    public static List<GPage10> Build(GuideFacts10 f, MapGuide10 map, string scene, Texture2D sketch)
    {
        var pages = new List<GPage10>();
        Func<string, GPage10> page = t => { var p = new GPage10 { title = t }; pages.Add(p); return p; };
        string mapName = map != null ? map.title : scene;
        bool live = f.whatIf != null;

        // 1 title
        var p0 = page("");
        p0.blocks.Add(new GBlock10 { kind = GBlock10.Heading, text = "Angler's Guide", color = Red });
        p0.blocks.Add(P("to the lake of " + mapName));
        p0.blocks.Add(Sp());
        p0.blocks.Add(S(live ? "Every chance in this book is computed by the running fishing game (" + f.fishingVersion + ") with its own odds code when you open it - for your gear (" + Gear(f) + ") unless a table says otherwise. Prices, rods, lures and zones are read live too."
                             : "Prices, rods and lures are read from the running fishing game (" + f.fishingVersion + "); odds use the game's formula with a Basic Rod."));
        if (!string.IsNullOrEmpty(f.reader)) p0.blocks.Add(S("This copy belongs to " + f.reader + "."));
        p0.blocks.Add(Sp());
        p0.blocks.Add(H("Contents"));
        foreach (string c in new[] { "The lake", "Map & fishing spots", "Fishing zones", "Best times to fish", "Weather", "Fish of the lake", "Eating fish", "Rods, skins & bobbers", "Lures", "Tips from old anglers" })
            if (c != "Fishing zones" || f.zonesOn) p0.blocks.Add(B(c));
        p0.blocks.Add(Sp());
        p0.blocks.Add(S("Turn pages: Left / Right (or A / D).  Close: G or Esc."));

        // 2 the lake
        var pl = page("The lake");
        if (map != null)
        {
            pl.blocks.Add(P(map.lakeNote));
            pl.blocks.Add(Sp());
            pl.blocks.Add(P("Water levels seen on " + map.title + ":"));
            foreach (string w in map.waterLevels) pl.blocks.Add(B(w));
        }
        else pl.blocks.Add(P("When the stage starts, the water is laid 3 m below the lowest player standing on the ground. Every hollow below that floods. No spot notes for " + scene + " yet."));
        if (!float.IsNaN(f.waterY)) pl.blocks.Add(P("Right now: water at Y " + f.waterY.ToString("0.00", Ci) + ".", Blue));
        pl.blocks.Add(Sp());
        pl.blocks.Add(P("The level stays put for the stage. It only rises by itself if every player on the ground has stood more than 60 m above it for 30 s (then to 3 m under the lowest of them)." + (f.waterCmd ? " The host can also raise or lower it with !water (with a 10 s warning)." : "")));
        if (f.swim)
            pl.blocks.Add(P("You can swim. Floating uses up your breath in " + f.swimSurface.ToString("0", Ci) + " s, diving in " + f.swimUnder.ToString("0", Ci) + " s; it refills on land. Out of breath you take damage and then drown. Eating a Ghost Fish stops breath loss. The small ponds never count." + (f.drownRespawn ? " Drowned players come back on dry land after " + f.respawnDelay.ToString("0", Ci) + " s." : ""), Red));
        else
            pl.blocks.Add(P("Touching the water drowns you (your feet less than 0.1 m above the surface). You get 5 s of grace after spawning, reviving or a new stage." + (f.drownRespawn ? " Drowned players come back on dry land after " + f.respawnDelay.ToString("0", Ci) + " s." : ""), Red));

        // 3 map + spots
        var pm = page("Map & fishing spots");
        if (sketch != null) pm.blocks.Add(new GBlock10 { kind = GBlock10.Image, img = sketch, imgH = 0.5f, caption = "Top-down, north up, " + (map != null ? map.size : 300f).ToString("0") + " m across. Blue = lake, white/green = land (darker = higher)." });
        var legend = new List<string[]> {
            new[] { "<color=#b08cd9>O</color>", "where the hotspot has been", "<color=#1f7f26><b>O</b></color>", "popular shore (no odds bonus)" },
            new[] { "<color=#1a73b8><b>O</b></color>", "small pond (now)", "<color=#333333><b>O</b></color>", "map ring (drop spot)" },
            new[] { "<color=#1aa088><b>#</b></color>", "rod shop stall", f.roulette ? "<color=#d99a0d><b>#</b></color>" : "", f.roulette ? "roulette table" : "" },
            new[] { "<color=#c0332a><b>X</b></color>", "you", f.zonesOn ? "<color=" + ZoneHex(0) + "><b>A</b></color>" : "", f.zonesOn ? "fishing zone (next pages)" : "" } };
        pm.blocks.Add(T(new[] { 0.07f, 0.43f, 0.07f, 0.43f }, legend.ToArray()));
        var ps = page("Fishing spots");
        if (map != null)
        {
            foreach (string s in map.castNotes) ps.blocks.Add(B(s));
            ps.blocks.Add(Sp());
            ps.blocks.Add(H("The purple hotspot"));
            foreach (string s in map.spotNotes) ps.blocks.Add(B(s));
        }
        else ps.blocks.Add(P("No spot notes for " + scene + " yet - the map sketch shows the live lake. The open lake has the same odds everywhere; only the hotspots, ponds and fishing zones change them."));
        {
            var lk = W(f, "lake", null, f.myRod, f.myBobber, null, "Clear", 12f); var ht = W(f, "hot", null, f.myRod, f.myBobber, null, "Clear", 12f);
            ps.blocks.Add(P("Hotspot radius " + f.hotspotRadius.ToString("0", Ci) + " m. Its own table (per bite, your gear): " + TierLineW(f, ht) + ". Only the fish living inside it bite there (it keeps at least 3 while someone is near), +10% reel chance, and every hotspot catch pays at least the middle of its value range. Red + lunar per bite with " + Gear(f) + " on a clear day: "
                + Pct(Sum(f, ht, "rare")) + " in the hotspot vs " + Pct(Sum(f, lk, "rare")) + " on the open lake.", Purple));
        }
        ps.blocks.Add(H("Finding the hotspots"));
        if (f.compass)
        {
            string cp = ShopPrice(f, "Compass"), rp = ShopPrice(f, "Range Finder"), tp = ShopPrice(f, "Cast Distance Tracker");
            if (tp.Length == 0 && f.trackerPrice.Length > 0) tp = ", " + f.trackerPrice;
            ps.blocks.Add(P("The hotspots have no marker on the water. Find them with gadgets from the Rod Shop stall (ITEMS tab):"));
            ps.blocks.Add(B("COMPASS" + cp + ": hold SHIFT (your Utility key) to take out a brass pocket compass. The red needle points to the purple hotspot, the thin gold needle to the golden hotspot. Direction only - no distance."));
            ps.blocks.Add(B("RANGE FINDER" + rp + ": hold R (your Special key) to look through it - zoomed view and the distance to what you aim at. Aim at the water: it says HOTSPOT (or GOLDEN HOTSPOT) when you are right on it" + (f.rangeZones ? ", or the name of a fishing zone." : ".")));
            ps.blocks.Add(P("Cast Distance Tracker (item" + tp + ", any rod): while your line is out, a strip at the bottom of the screen shows how far your cast went and how far the bobber is from the hotspot - green \"in zone!\" when it is inside - and a coloured ring marks your bobber on the water.", Green));
        }
        else
            ps.blocks.Add(P("Hotspots are meant to be found with the Compass (hold SHIFT) and Range Finder (hold R) from the Rod Shop stall. While those are not running in this game, look for the sparkles on the water."));
        ps.blocks.Add(H("Golden hotspot & ponds"));
        {
            var g2 = W(f, "hot2", null, f.myRod, f.myBobber, null, "Clear", 12f);
            ps.blocks.Add(P("Golden hotspot (" + Live(f.hot2, "weather & time update") + "): 150-300 m from the purple one on open water within 150 m of land, radius " + f.hot2Radius.ToString("0", Ci) + " m, own table (per bite, your gear): " + TierLineW(f, g2) + ". Every cast gets a bite after 2-6 s, catches worth x1.5. It drifts every 4 minutes or after a red/lunar prize.", Gold));
        }
        {
            var po = W(f, "pool", null, f.myRod, f.myBobber, null, "Clear", 12f); var lk = W(f, "lake", null, f.myRod, f.myBobber, null, "Clear", 12f);
            ps.blocks.Add(P("Ponds (" + Live(f.pools, "weather & time update") + "): up to 2-5 per stage on flat ground 30-250 m from a player, at least 4 m above the lake, random each stage. Their own mix (Catfish & Perch x3), per bite: " + TierLineW(f, po) + "; Catfish & Perch together " + Pct(Sum(f, po, "Catfish,Perch")) + " per bite (lake " + Pct(Sum(f, lk, "Catfish,Perch")) + "). Every pond cast gets a bite after 2-6 s (10% sooner). Ponds are for panfish - fewer rare fish than the lake." + (f.poolSpots.Count > 0 ? " " + f.poolSpots.Count + " on this stage (blue circles on the map)." : ""), Blue));
        }

        // 3b zones
        if (f.zonesOn) ZonePages(f, page, scene);

        // 4 best times
        var pt = page("Best times to fish");
        pt.blocks.Add(P("One day on the fishing clock lasts " + f.dayMinutes.ToString("0.#", Ci) + " real minutes (one hour = " + (f.dayMinutes * 60f / 24f).ToString("0", Ci) + " s)." + (f.hour >= 0 ? " It is " + Clock(f.hour) + " now." : "")));
        pt.blocks.Add(S("Time-of-day odds: " + Live(f.events, "weather & time update")));
        pt.blocks.Add(T(new[] { 0.3f, 0.7f }, new[] { "<b>05:00-07:00</b>", "Golden hour (dawn)" }, new[] { "<b>07:00-17:30</b>", "Day - normal odds" },
            new[] { "<b>17:30-20:00</b>", "Golden hour (dusk)" }, new[] { "<b>20:00-05:00</b>", "Night" }));
        pt.blocks.Add(Sp());
        pt.blocks.Add(S("Chance per bite on the open lake, no lure, " + (live ? "with your " + Gear(f) : "Basic Rod") + " (Rain = clear-sky day + rain):"));
        var day = W(f, "lake", null, f.myRod, f.myBobber, null, "Clear", 12f); var gold = W(f, "lake", null, f.myRod, f.myBobber, null, "Golden hour", 6f);
        var night = W(f, "lake", null, f.myRod, f.myBobber, null, "Night", 23f); var rain = W(f, "lake", null, f.myRod, f.myBobber, null, "Rain", 12f);
        var rows = new List<string[]> { new[] { "<b>Fish</b>", "<b>Day</b>", "<b>Gold</b>", "<b>Night</b>", "<b>Rain</b>", "<b>Best</b>" } };
        foreach (string sp in AllFish(f))
        {
            double[] v = { day[sp], gold[sp], night[sp], rain[sp] };
            string[] nm = { "day", "golden hr", "night", "rain" };
            int bi = 0; for (int i = 1; i < 4; i++) if (v[i] > v[bi] + 1e-9) bi = i;
            rows.Add(new[] { sp, Pct(v[0]), Pct(v[1]), Pct(v[2]), Pct(v[3]), nm[bi] });
        }
        rows.Add(new[] { "Old Boot", Pct(day["Old Boot"]), Pct(gold["Old Boot"]), Pct(night["Old Boot"]), Pct(rain["Old Boot"]), "-" });
        pt.blocks.Add(T(new[] { 0.28f, 0.13f, 0.13f, 0.13f, 0.13f, 0.2f }, rows.ToArray()));
        pt.blocks.Add(S("The visible fish school follows the same odds: when the time of day or the weather changes, the free fish change species, so at night you SEE more Anglerfish, Moon Eels and Ghost Fish."));
        {
            var nP = W(f, "lake", null, f.myRod, "Plain", null, "Night", 23f); var nF = W(f, "lake", null, f.myRod, "Firefly Bobber", null, "Night", 23f); var nG = W(f, "lake", null, f.myRod, "Glow Bobber", null, "Night", 23f);
            pt.blocks.Add(P("Glow bobbers work 20:00-05:00 only (by day they just glow). Glow Bobber: bite wait x" + nG["~bite"].ToString("0.00", Ci) + " instead of x" + nP["~bite"].ToString("0.00", Ci) + " (with your rod). Firefly: red + lunar " + Pct(Sum(f, nF, "rare")) + " instead of " + Pct(Sum(f, nP, "rare")) + " per bite at night. Lantern: both." + (f.glowBonus ? "" : " (Night bonus not running in this game - they only glow.)"), Green));
        }

        // 5 weather
        var pw = page("Weather");
        pw.blocks.Add(S("Weather: " + Live(f.events, "weather & time update") + (f.events && f.eventName.Length > 0 ? ". Now: " + f.eventName.Replace("+", " + ") : "")));
        pw.blocks.Add(P("The sky is re-rolled every 4-6 minutes: clear 60%, rain 30%, storm 10%. Weather and time of day stack (e.g. Night + Rain). Multipliers below act on a fish's weight; the % are the real chances per bite on the open lake with your gear."));
        var storm = W(f, "lake", null, f.myRod, f.myBobber, null, "Storm", 12f);
        pw.blocks.Add(T(new[] { 0.22f, 0.78f },
            new[] { "<b>Rain</b>", "Catfish, Trout, Bass x2 (together " + Pct(Sum(f, day, "Catfish,Trout,Bass")) + " -> " + Pct(Sum(f, rain, "Catfish,Trout,Bass")) + "). Bite wait x" + rain["~bite"].ToString("0.00", Ci) + ", and 30% of the casts that would come back empty get a bite after all." },
            new[] { "<b>Storm</b>", "Red x2, lunar x2.5 (red + lunar " + Pct(Sum(f, day, "rare")) + " -> " + Pct(Sum(f, storm, "rare")) + "), bite wait x" + storm["~bite"].ToString("0.00", Ci) + ". Lightning flashes on the water 25-70 m from players every 5-12 s (just a show - it does not hurt)." },
            new[] { "<b>Golden hour</b>", "Red & lunar x1.5, Golden Koi & Salmon x2 (Golden Koi " + Pct(day["Golden Koi"]) + " -> " + Pct(gold["Golden Koi"]) + ")." },
            new[] { "<b>Night</b>", "Anglerfish, Moon Eel, Ghost Fish x3 (together " + Pct(Sum(f, day, "Anglerfish,Moon Eel,Ghost Fish")) + " -> " + Pct(Sum(f, night, "Anglerfish,Moon Eel,Ghost Fish")) + "), white fish x0.6." }));
        pw.blocks.Add(Sp());
        {   // "best" is computed, not asserted: every time x weather combination through the game's odds
            string bestN = ""; double bestV = -1;
            foreach (float h in new[] { 12f, 6f, 23f })
                foreach (string w in new[] { "", "Rain", "Storm" })
                {
                    string ev = Ev(h, w); double v = Sum(f, W(f, "lake", null, f.myRod, f.myBobber, null, ev, h), "rare");
                    if (v > bestV + 1e-9) { bestV = v; bestN = ev; }
                }
            pw.blocks.Add(P("Best weather for red + lunar fish (all time x weather combinations checked): " + bestN.Replace("+", " + ").Replace("Night + Storm", "a stormy night") + " - " + Pct(bestV) + " per bite on the open lake, vs " + Pct(Sum(f, day, "rare")) + " on a clear day.", Purple));
        }
        pw.blocks.Add(S("Type !event (or !weather) to hear the current event; !odds prints your open-lake odds right now."));

        // 6 fish table
        var pf = page("Fish of the lake");
        var fr = new List<string[]> { new[] { "<b>Fish</b>", "<b>Tier</b>", "<b>Value</b>", "<b>Perk while held</b>" } };
        foreach (string sp in AllFish(f))
        {
            string tier = TierOf(f, sp); int ti = tier == "Tier1" ? 0 : tier == "Tier2" ? 1 : tier == "Tier3" ? 2 : 3;
            string tc = ti == 0 ? "white" : ti == 1 ? "<color=#1f6b24>green</color>" : ti == 2 ? "<color=#a01f14>red</color>" : "<color=#1a40a0>lunar</color>";
            string val = f.valueLo[ti] == f.valueHi[ti] ? Money(f.valueLo[ti]) : Money(f.valueLo[ti]) + "-" + f.valueHi[ti];
            string pk; f.perks.TryGetValue(sp, out pk);
            fr.Add(new[] { sp, tc, val, pk ?? "" });
        }
        { string pk; f.perks.TryGetValue("Old Boot", out pk); fr.Add(new[] { "Old Boot", "junk", "$0", (pk ?? "") + " <i>(only the label - the odds ignore it)</i>" }); }
        pf.blocks.Add(T(new[] { 0.25f, 0.13f, 0.17f, 0.45f }, fr.ToArray()));
        pf.blocks.Add(S("Each fish you hold is one item in your item bar and gives its perk. Golden Koi: the next 3 catches are worth double. Hotspot catches pay at least the middle of the range; golden hotspot catches x1.5."));

        // 7 eating
        var pe = page("Eating fish");
        pe.blocks.Add(S("!eat <fish>: " + Live(f.eating, "weather & time update")));
        pe.blocks.Add(P("Any fish you hold can be eaten, any time: type !eat <fish> (e.g. !eat minnow). It removes that fish (and its perk) and gives a timed effect, shown on your fishing panel."));
        var er = new List<string[]>();
        foreach (string sp in AllFish(f)) { string e; if (f.eat.TryGetValue(sp, out e)) er.Add(new[] { "<b>" + sp + "</b>", e }); }
        { string e; if (f.eat.TryGetValue("Old Boot", out e)) er.Add(new[] { "<b>Old Boot</b>", e }); }
        pe.blocks.Add(T(new[] { 0.27f, 0.73f }, er.ToArray()));
        pe.blocks.Add(S("Perch's +15% reel chance helps on visible school fish (random bites are always landed if you click in time)."));

        // 8 rods, skins, bobbers
        var pr = page("Rods, skins & bobbers");
        pr.blocks.Add(P("Buy them at the ROD SHOP stall by the lake: walk up to it (4 m), the shop list opens. Number key = buy (press twice) or equip. Left/Right = rods / skins / bobbers / items. This book itself is sold there as an item (Angler's Guide). Money is your tournament money. E at the stall opens this book." + (f.glowBonus ? " Typing !buy only works while you stand at the stall (on a stage without a stall it works anywhere); !equip and !shop work anywhere." : " !shop / !buy / !equip in chat also work.")));
        foreach (string kind in new[] { "rod", "skin", "bobber", "item" })
        {
            var r = new List<string[]>();
            foreach (var it in f.shop) if (it[0] == kind && !CaseItem(it)) r.Add(new[] { it[1], it[2] == "0" ? "free" : Money(int.Parse(it[2], Ci)), it[3] });
            if (r.Count == 0) continue;
            pr.blocks.Add(H(kind == "rod" ? "Rods (change fishing)" : kind == "skin" ? "Rod skins (look only)" : kind == "item" ? "Items" : "Bobbers"));
            pr.blocks.Add(T(new[] { 0.32f, 0.18f, 0.5f }, r.ToArray()));
            if (kind == "rod")
            {
                var rr = new List<string[]> { new[] { "<b>Rod</b>", "<b>red+lunar / bite</b>", "<b>bite wait</b>" } };
                foreach (var it in f.shop) if (it[0] == "rod") { var o = W(f, "lake", null, it[1], "Plain", null, "Clear", 12f); rr.Add(new[] { it[1] + (it[1] == f.myRod ? " (yours)" : ""), Pct(Sum(f, o, "rare")), "x" + o["~bite"].ToString("0.00", Ci) }); }
                if (live) { pr.blocks.Add(S("Measured by the game (open lake, clear day, Plain bobber):")); pr.blocks.Add(T(new[] { 0.42f, 0.33f, 0.25f }, rr.ToArray())); }
                pr.blocks.Add(S("Every rod type looks different: Stick = crooked branch with knots and twine, no reel; Basic = plain rod, small reel; Long Cast = very thin with many guide rings; Steady = thick, cork grip, big reel; Quick = short whippy tip, pistol grip, trigger reel; Lucky = clover charm + horseshoe; Golden = gold trim and a glowing gem. Skins colour the rod on top of its shape."));
            }
        }
        { int nCase = 0, lo = int.MaxValue, hi = 0; foreach (var it in f.shop) if (CaseItem(it) && !(it[3] ?? "").Contains("not for sale")) { nCase++; int pz = int.Parse(it[2], Ci); lo = Math.Min(lo, pz); hi = Math.Max(hi, pz); }
          if (nCase > 0) pr.blocks.Add(P("Plus " + nCase + " case-drop skins and bobbers from the fish merchant's cases; the stall also sells them straight (" + Money(lo) + (hi > lo ? "-" + Money(hi) : "") + "). They are looks only.", Purple)); }
        pr.blocks.Add(S("Rod bonuses: range = how far you can cast (" + f.castRange.ToString("0", Ci) + " m x your rod: " + (f.castRange * f.myRange).ToString("0", Ci) + " m for you); reel window = time to click after a bite (1.5 s, 5 s via chat, x your rod: " + (1.5f * f.myWindow).ToString("0.0#", Ci) + " s for you); rare = red & lunar odds; bites = waiting time."));

        // 9 lures
        var pu = page("Lures");
        pu.blocks.Add(S("Lures: " + Live(f.lureShop, "weather & time update")));
        pu.blocks.Add(P("!lures lists them, !buylure <name> buys one (limited casts), !lure <name> equips, !lure off. Every cast that lands on water uses one charge. Map lures only work (and only use charges) on their maps."));
        var lr = new List<string[]>();
        foreach (var l in f.lures)
        {
            bool here = l[4] == null || Array.IndexOf(l[4].Split(new[] { ',' }), scene) >= 0;
            lr.Add(new[] { (here && l[4] != null ? "<color=#1f6b24><b>" + l[0] + "</b></color>" : "<b>" + l[0] + "</b>"), Money(int.Parse(l[1], Ci)) + ", " + l[2] + " casts", l[3] + (l[4] != null ? " (" + (here ? "THIS MAP" : "maps: " + l[4]) + ")" : "") });
        }
        pu.blocks.Add(T(new[] { 0.27f, 0.25f, 0.48f }, lr.ToArray()));
        foreach (var l in f.lures)
            if (l[4] != null && Array.IndexOf(l[4].Split(new[] { ',' }), scene) >= 0)
                pu.blocks.Add(P("Here on " + mapName + " the " + l[0] + " works: " + l[3] + ".", Green));

        // 10 tips
        var px = page("Tips from old anglers");
        px.blocks.Add(B("Cast into the water near the fish you can see: a fish within 15 m swims to your bobber and bites in about 1-3 s (your rod and bobber change that). Reel chance 95% at 0 m down to 35% at 15 m (+10% in the hotspot)."));
        px.blocks.Add(B("In the hotspot and in a fishing zone only the fish that live there bite - aim inside it. On the open lake the zone and hotspot fish ignore your bobber."));
        px.blocks.Add(B("No fish within 15 m? Only half of such casts get a bite (after 6-12 s); the rest come back empty after 12 s."));
        px.blocks.Add(B("Don't click before it bites - you yank too early and scare the fish. After a bite you have " + (1.5f * f.myWindow).ToString("0.0#", Ci) + " s with your rod (" + (5f * f.myWindow).ToString("0.0#", Ci) + " s if you fish by typing 'fish')."));
        px.blocks.Add(B("A line that lands on the ground doesn't fish. Aim down at the water, or aim up to lob (" + (15f * f.myRange).ToString("0", Ci) + " m at -10 deg up to " + (f.castRange * f.myRange).ToString("0", Ci) + " m at +45 deg with your rod)."));
        px.blocks.Add(B("The Rod Whip (right click) hits the nearest player within " + f.whipRange.ToString("0", Ci) + " m and " + f.whipAngle.ToString("0", Ci) + " deg of your aim for exactly 1 damage and snaps their line back. " + f.whipCooldown.ToString("0.#", Ci) + " s cooldown."));
        if (f.swim) px.blocks.Add(B("Fell in? Swim back to land before your breath runs out" + (f.eating ? " - or eat a Ghost Fish first." : ".") + (f.drownRespawn ? " If you drown you are back on dry land " + f.respawnDelay.ToString("0", Ci) + " s later." : "")));
        else px.blocks.Add(B("Don't step into the lake: you drown" + (f.drownRespawn ? " and come back on dry land " + f.respawnDelay.ToString("0", Ci) + " s later" : " and spectate until the next stage") + " - unless you hold a Moon Eel (free revive)" + (f.eating ? " or ate a Ghost Fish." : ".")));
        px.blocks.Add(B("Old Boots: " + Pct(f.lake[0]) + " of open-lake bites, " + Pct(f.pool[0]) + " in ponds, never in a hotspot. Worth nothing; the '+2% fishing luck' on its label does not change any odds."));
        px.blocks.Add(B("A red or lunar fish from the hotspot is the PRIZE: it moves the hotspot."));
        if (f.roulette) px.blocks.Add(B("!scores = leaderboard, !collection = your fish, !bet red Salmon at the roulette table (red/black pays 2 fish, a number 10, green 14)."));
        else px.blocks.Add(B("!scores = leaderboard, !collection = your fish." + (f.merchant ? " The FISH MERCHANT by the lake buys fish (!sell <fish> [n|all]) and sells cases (!case); !merchant for help." : "")));
        return pages;
    }

    // ------------------------------------------------------------------ fishing zones
    static string CueText(string cue)
    {
        switch (cue) { case "reeds": return "a few reeds sticking out of the water"; case "lilies": return "lily pads"; case "buoy": return "a small red-and-white buoy"; case "ice": return "flat ice floes"; }
        return "nothing";
    }
    static string Dir(float dx, float dz)
    {
        string[] n = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
        double a = Math.Atan2(dz, dx) * 180 / Math.PI; int k = (int)Math.Round(((a % 360) + 360) % 360 / 45.0) % 8; return n[k];
    }
    static void ZonePages(GuideFacts10 f, Func<string, GPage10> page, string scene)
    {
        var pz = page("Fishing zones");
        pz.blocks.Add(P("Every lake has a few small named zones (" + (f.zoneDefs.Count > 0 ? f.zoneDefs.Count.ToString(Ci) : "3-4") + " here, 12-20 m across) on open water near the shore. Same map + same lake = same zones, every time. Each has its own fish and its favourite gear, and only the fish that live in a zone bite there - so what this book says about a zone is what you catch in it."));
        pz.blocks.Add(P("They are not marked. Look for " + "reeds, lily pads, a small buoy or ice floes on the water" + (f.rangeZones ? "; the Range Finder names a zone when you aim at it" : "") + ". A zone keeps at least 3 fish of its own while someone is within 65 m. While your line is in a zone, your fishing panel shows \"Zone: <name>\".", Green));
        if (f.zoneSketch != null && f.zonesLive.Count > 0)
            pz.blocks.Add(new GBlock10 { kind = GBlock10.Image, img = f.zoneSketch, imgH = 0.42f, caption = "The zones of this lake right now (letters), the stall (#) and you (X). North up." });
        var lg = new List<string[]>();
        if (f.zonesLive.Count == 0) pz.blocks.Add(P("No zones on this lake yet - they are laid out a few seconds after the lake appears. Their names and rules:", Red));
        for (int i = 0; i < f.zoneDefs.Count; i++)
        {
            var d = f.zoneDefs[i]; string[] lv = LiveZone(f, d[0]);
            lg.Add(new[] { "<color=" + ZoneHex(i) + "><b>" + (char)('A' + i) + "</b></color>", "<b>" + d[0] + "</b>", TargetName(d[3]), lv == null ? "not placed" : "X " + R0(lv[2]) + ", Z " + R0(lv[4]) });
        }
        pz.blocks.Add(T(new[] { 0.06f, 0.34f, 0.32f, 0.28f }, lg.ToArray()));
        if (f.zoneDefs.Count > 0 && f.zoneDefs[0][7] == "*") pz.blocks.Add(S("This map has no zone table of its own yet, so it uses the standard zones."));

        string[] times = { "day", "dawn", "dusk", "night" }; float[] hours = { 12f, 6f, 18.5f, 23f };
        string[] weathers = { "", "Rain", "Storm" };
        for (int i = 0; i < f.zoneDefs.Count; i++)
        {
            var d = f.zoneDefs[i]; string name = d[0], target = d[3];
            var p = page("Zone " + (char)('A' + i) + " - " + name);
            p.blocks.Add(new GBlock10 { kind = GBlock10.Para, text = "<i>" + d[4] + "</i>", color = ZoneColors[i % ZoneColors.Length] });
            string[] lv = LiveZone(f, name);
            if (lv != null)
            {
                float x = float.Parse(lv[2], Ci), z = float.Parse(lv[4], Ci);
                string where = "Where: X " + R0(lv[2]) + ", Z " + R0(lv[4]) + ", " + R0(lv[5]) + " m radius - look for " + CueText(d[2]) + ".";
                if (!float.IsNaN(f.meX)) { float dx = x - f.meX, dz = z - f.meZ; where += " " + Math.Sqrt(dx * dx + dz * dz).ToString("0", Ci) + " m " + Dir(dx, dz) + " of you."; }
                p.blocks.Add(P(where));
            }
            else p.blocks.Add(P("Not placed on this lake right now. Look for " + CueText(d[2]) + "."));
            p.blocks.Add(H("What's different here"));
            foreach (string t in d[6].Split(new[] { ';' })) if (t.Trim().Length > 0) p.blocks.Add(B(t.Trim()));
            if (f.whatIf == null) { p.blocks.Add(S("(Numbers need the zone-aware fishing game, Fishing25 or newer.)")); continue; }

            // best setup: every time x weather, then rods, bobbers, lures - all through the game's own odds
            string rod = f.myRod, bob = f.myBobber, lure = null; float bh = 12f; string bw = "", bt = "day"; double best = -1;
            for (int ti = 0; ti < 4; ti++) foreach (string w in weathers)
                {
                    double v = Sum(f, W(f, "lake", name, rod, bob, null, Ev(hours[ti], w), hours[ti]), target);
                    if (v > best + 1e-9) { best = v; bh = hours[ti]; bw = w; bt = times[ti]; }
                }
            string ev = Ev(bh, bw);
            Func<string, string, string, double> score = (r, b, l) => Sum(f, W(f, "lake", name, r, b, l, ev, bh), target);
            Func<string, string, string, double> wait = (r, b, l) => W(f, "lake", name, r, b, l, ev, bh)["~bite"];
            string bestRod = rod; double bv = score(rod, bob, null);
            foreach (var it in f.shop) if (it[0] == "rod") { double v = score(it[1], bob, null); if (v > bv + 1e-9 || (Math.Abs(v - bv) < 1e-9 && wait(it[1], bob, null) < wait(bestRod, bob, null) - 1e-6)) { bv = v; bestRod = it[1]; } }
            string bestBob = bob; double bb = score(bestRod, bob, null);
            foreach (var it in f.shop) if (it[0] == "bobber" && !CaseItem(it)) { double v = score(bestRod, it[1], null); if (v > bb + 1e-9 || (Math.Abs(v - bb) < 1e-9 && wait(bestRod, it[1], null) < wait(bestRod, bestBob, null) - 1e-6)) { bb = v; bestBob = it[1]; } }
            string bestLure = null; double bl = score(bestRod, bestBob, null);
            foreach (var l in f.lures) { if (l[4] != null && Array.IndexOf(l[4].Split(new[] { ',' }), scene) < 0) continue; double v = score(bestRod, bestBob, l[0]); if (v > bl + 1e-9) { bl = v; bestLure = l[0]; } }
            var here = W(f, "lake", name, bestRod, bestBob, bestLure, ev, bh); var lake = W(f, "lake", null, bestRod, bestBob, bestLure, ev, bh);
            var mineHere = W(f, "lake", name, f.myRod, f.myBobber, null, ev, bh);
            string tn = TargetName(target);
            p.blocks.Add(H("Best setup for " + tn));
            var rows = new List<string[]>();
            rows.Add(new[] { "<b>Time</b>", bt + (bt == "dawn" ? " (05:00-07:00)" : bt == "dusk" ? " (17:30-20:00)" : bt == "night" ? " (20:00-05:00)" : " (07:00-17:30)") + (bw.Length > 0 ? "" : "") });
            rows.Add(new[] { "<b>Weather</b>", bw.Length == 0 ? "any - rain or storm don't help here" : bw.ToLowerInvariant() + " helps" });
            rows.Add(new[] { "<b>Rod</b>", bestRod + (bestRod == f.myRod ? " (yours)" : "") + " - " + Pct2(bv) + (bestRod != f.myRod ? " vs " + Pct2(score(f.myRod, bob, null)) + " with your " + f.myRod : "") });
            rows.Add(new[] { "<b>Bobber</b>", bestBob + (bestBob == bob ? (bestBob == f.myBobber ? " (yours)" : "") : " - " + Pct2(bb) + " vs " + Pct2(score(bestRod, bob, null))) });
            rows.Add(new[] { "<b>Lure</b>", bestLure == null ? "none helps" : bestLure + " - " + Pct2(bl) + " vs " + Pct2(bb) + " without" });
            rows.Add(new[] { "<b>" + tn + "</b>", "<b>" + Pct2(Sum(f, here, target)) + " per bite here</b> vs " + Pct2(Sum(f, lake, target)) + " on the open lake (same setup)" });
            rows.Add(new[] { "<b>Your gear</b>", Pct2(Sum(f, mineHere, target)) + " here at that time with " + Gear(f) });
            rows.Add(new[] { "<b>Bite wait</b>", "x" + here["~bite"].ToString("0.00", Ci) + " here vs x" + lake["~bite"].ToString("0.00", Ci) + " on the open lake" });
            p.blocks.Add(T(new[] { 0.26f, 0.74f }, rows.ToArray()));
            // gear rules: show the measured effect of each rod / bobber / lure rule
            foreach (string src in d[5].Split(new[] { ';' }))
            {
                string s = src.Trim(); int gt = s.IndexOf('>'); if (gt < 0) continue;
                string cond = s.Substring(0, gt), rest = s.Substring(gt + 1); int st = rest.LastIndexOf('*'); string tg = rest.Substring(0, st).Trim();
                string r2 = f.myRod, b2 = f.myBobber, l2 = null; float h2 = 12f; string w2 = "";
                foreach (string part in cond.Split(new[] { '&' }))
                {
                    string[] kv = part.Split(new[] { ':' }); string v = kv[1].Split(new[] { ',' })[0];
                    if (kv[0] == "rod") r2 = v; else if (kv[0] == "bobber") b2 = v; else if (kv[0] == "lure") l2 = v;
                    else if (kv[0] == "event" && v == "Night") h2 = 23f; else if (kv[0] == "weather") w2 = v;
                    else if (kv[0] == "time") h2 = float.Parse(v.Split(new[] { '-' })[0], Ci) + 0.5f;
                }
                if (!(cond.Contains("rod:") || cond.Contains("bobber:") || cond.Contains("lure:"))) continue;
                string e2 = Ev(h2, w2);
                var with = W(f, "lake", name, r2, b2, l2, e2, h2);
                var without = W(f, "lake", name, cond.Contains("rod:") ? "Basic Rod" : r2, cond.Contains("bobber:") ? "Plain" : b2, cond.Contains("lure:") ? null : l2, e2, h2);
                string what = cond.Contains("rod:") ? r2 : cond.Contains("bobber:") ? b2 : l2;
                string other = cond.Contains("rod:") ? "a Basic Rod" : cond.Contains("bobber:") ? "a Plain bobber" : "no lure";
                if (tg == "bite") p.blocks.Add(S("Measured: " + what + " here" + (h2 >= 20 || h2 < 5 ? " at night" : "") + " - bite wait x" + with["~bite"].ToString("0.00", Ci) + " vs x" + without["~bite"].ToString("0.00", Ci) + " with " + other + "."));
                else p.blocks.Add(S("Measured: " + what + " here - " + TargetName(tg) + " " + Pct2(Sum(f, with, tg)) + " vs " + Pct2(Sum(f, without, tg)) + " with " + other + "."));
            }
        }
    }
    static string R0(string v) { float x; return float.TryParse(v, NumberStyles.Float, Ci, out x) ? x.ToString("0", Ci) : v; }
    static string[] LiveZone(GuideFacts10 f, string name) { foreach (var z in f.zonesLive) if (z[1] == name) return z; return null; }

    public static bool CaseItem(string[] it) { string d = it.Length > 3 ? it[3] ?? "" : ""; return d.StartsWith("case drop") || d.Contains("StatTrak case drop"); }
    public static IEnumerable<string> AllFish(GuideFacts10 f)
    {
        foreach (var a in new[] { f.white, f.green, f.red, f.lunar }) foreach (var s in a) yield return s;
    }
}
