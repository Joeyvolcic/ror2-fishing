// GuideData - content of the fishing guide book (shared by the host add-on GuideBook<N> and FISHING.exe).
//  * GuideFacts: every number the book prints. Defaults below were copied from the fishing code (file/line noted);
//    the host add-on overwrites them from the LIVE Fishing<N> / DayNight<N> / HotBeam<N> by reflection before each
//    opening and logs any default that disagrees with the live game (fact check), so the book never drifts.
//  * MapGuide: one table per map (MapGuides). Only snowyforest so far; add a MapGuide to support another map.
//  * Build(facts, map, sketch) -> pages (GPage = title + blocks); GuideBookLook lays them out and renders them.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public class GBlock
{
    public const int Heading = 0, Para = 1, Table = 2, Image = 3, Space = 4, Small = 5, Bullet = 6;
    public int kind; public string text; public Color color = new Color(0.06f, 0.04f, 0.02f);
    public string[][] rows; public float[] cols; public Color[] rowColors;
    public Texture2D img; public float imgH; public string caption;
}
public class GPage { public string title; public readonly List<GBlock> blocks = new List<GBlock>(); public int number; public bool cover; public string coverSub; }

public class GSpot { public string name, note; public float x, z; public Color color; public float r; }

public class MapGuide
{
    public string scene, title, lakeNote;
    public float cx, cz, size;                  // top-down sketch window (world X/Z centre, edge length in m)
    public string[] waterLevels;                // lake heights seen in real runs (from ror2_fishing.log)
    public readonly List<GSpot> spots = new List<GSpot>();
    public string[] spotNotes, castNotes;
}

public class GuideFacts
{
    // FishSchool.cs: White / Green / Red / Lunar tier arrays
    public string[] white = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" };
    public string[] green = { "Bass", "Salmon", "Pufferfish" };
    public string[] red = { "Swordfish", "Golden Koi", "Anglerfish" };
    public string[] lunar = { "Moon Eel", "Ghost Fish" };
    // Fishing19.cs Catch(): value ranges per tier ($), lunar fixed 500, Old Boot 0
    public int[] valueLo = { 10, 40, 150, 500 }, valueHi = { 30, 80, 300, 500 };
    // Fishing19.cs Catch(): lake 10% boot then white 60 / green 30 / red 8 / lunar 2; hotspot no boots, 25/35/30/10
    public double[] lake = { 0.10, 0.60, 0.30, 0.08, 0.02 }, hot = { 0.0, 0.25, 0.35, 0.30, 0.10 };
    public double[] hot2Table = { 0.0, 0.15, 0.30, 0.40, 0.15 }, pool = { 0.08, 0.70, 0.25, 0.045, 0.005 };   // FishingGameplay.cs BaseTable
    // Fishing19.cs PerkText
    public Dictionary<string, string> perks = new Dictionary<string, string> {
        { "Minnow", "+2% move speed" }, { "Perch", "+2% sprint speed" }, { "Trout", "+3% jump height" }, { "Bass", "+1% move speed, +1% jump height" },
        { "Salmon", "+1 extra jump per 3 Salmon" }, { "Swordfish", "+5% move speed" }, { "Golden Koi", "+4% sprint speed, next 3 catches x2" },
        { "Moon Eel", "free revive" }, { "Old Boot", "+2% fishing luck" }, { "Clownfish", "+2% jump height" }, { "Catfish", "+1% move speed, +1% sprint speed" },
        { "Pufferfish", "+1 extra jump per 3 Pufferfish" }, { "Anglerfish", "+6% sprint speed" }, { "Ghost Fish", "+6% move speed, +6% jump height" } };
    // FishingGameplay.cs Eats (seconds, text)
    public Dictionary<string, string> eat = new Dictionary<string, string> {
        { "Minnow", "10 s: speed burst" }, { "Salmon", "20 s: triple jump" }, { "Pufferfish", "20 s: spiky aura (pricks players who get close)" },
        { "Anglerfish", "30 s: glowing lantern + see the fish school through walls" }, { "Ghost Fish", "15 s: ghostly: can't drown" },
        { "Golden Koi", "120 s: next catch x3" }, { "Moon Eel", "60 s: instant revive charge" }, { "Swordfish", "30 s: rod whip knocks players back" },
        { "Perch", "60 s: steady hands: +15% reel chance" }, { "Trout", "60 s: quick bites: bites 30% faster" }, { "Clownfish", "60 s: lucky: red & lunar fish x1.3" },
        { "Catfish", "60 s: whiskers: green fish x1.5" }, { "Bass", "60 s: big haul: catches worth +50%" }, { "Old Boot", "nothing. You ate a boot." } };
    // Fishing19_Rods.cs RodCatalog (kind, name, price, desc) + the 3 glow bobbers of RodStand<N>
    public List<string[]> shop = new List<string[]> {
        new[] { "rod", "Stick Rod", "0", "a crooked branch: -25% cast range, bites 20% slower, -20% reel window, -10% rare fish" }, new[] { "rod", "Basic Rod", "500", "a real rod: no bonus, no penalty" }, new[] { "rod", "Long Cast Rod", "2000", "+25% cast range" }, new[] { "rod", "Steady Rod", "3000", "+50% reel window" },
        new[] { "rod", "Quick Rod", "3500", "bites 30% faster" }, new[] { "rod", "Lucky Rod", "5000", "+20% rare fish" }, new[] { "rod", "Golden Rod", "15000", "+10% of everything" },
        new[] { "skin", "Default", "0", "wood" }, new[] { "skin", "Bamboo", "500", "bamboo" }, new[] { "skin", "Carbon", "1000", "black carbon" }, new[] { "skin", "Neon", "1500", "cyan/pink pulse" },
        new[] { "skin", "Gold", "2000", "metallic gold" }, new[] { "skin", "Lunar Glass", "2000", "translucent blue" }, new[] { "skin", "Void", "2500", "purple glow" },
        new[] { "bobber", "Plain", "0", "red ball" }, new[] { "bobber", "Classic", "500", "red/white" }, new[] { "bobber", "Star", "1000", "gold star" },
        new[] { "bobber", "Duck", "1250", "rubber duck" }, new[] { "bobber", "Skull", "1500", "skull" },
        new[] { "bobber", "Glow Bobber", "2500", "glows; at night bites 25% faster" }, new[] { "bobber", "Firefly Bobber", "4000", "glows; at night +15% rare fish" },
        new[] { "bobber", "Lantern Bobber", "6000", "glows; at night bites 25% faster and +15% rare fish" } };
    // FishingGameplay.cs InitLures: name, price, casts, effect, maps (null = all)
    public List<string[]> lures = new List<string[]> {
        new[] { "Spinner", "300", "20", "Trout & Perch x2.5", null }, new[] { "Glow Lure", "800", "20", "Anglerfish, Moon Eel, Ghost Fish x2", null },
        new[] { "Deep Jig", "1500", "20", "red fish x1.5, bites 50% slower", null }, new[] { "Golden Fly", "3000", "15", "Golden Koi x3", null },
        new[] { "Feather Lure", "600", "20", "Ghost Fish, Salmon, Minnow x2", "skymeadow" },
        new[] { "Coral Lure", "600", "20", "Clownfish x2.5, Swordfish x2", "blackbeach,blackbeach2,goldshores,shipgraveyard" },
        new[] { "Frost Lure", "600", "20", "Trout, Salmon x2, Moon Eel x1.5", "frozenwall,snowyforest" },
        new[] { "Swamp Lure", "600", "20", "Catfish x2.5, Bass x2, Perch x1.5", "golemplains,golemplains2,foggyswamp,wispgraveyard" },
        new[] { "Sand Lure", "600", "20", "Pufferfish x2.5, Catfish x2, Anglerfish x1.5", "goolake,ancientloft" } };
    // which systems are live in the running game (host: detected by reflection; guest: from host lines)
    public bool events, eating, lureShop, pools, hot2, glowBonus, stand = true;
    public bool compass; public string trackerPrice = "";   // HotspotCompass<N> running (hotspots unmarked; compass + range finder)
    public float hotspotRadius = 10f;            // HotBeam7 Radius (Fishing19 alone: 6)
    public float castRange = 200f, whipRange = 20f, whipAngle = 60f, whipCooldown = 1.5f;   // live carry-over values (ror2_fishing.log)
    public float dayMinutes = 10f;               // DayNight1.dayMinutes
    public float glowBite = 0.75f, glowRare = 0.15f;   // glow bobber night bonus
    public string fishingVersion = "Fishing19", reader = "";
    public float hour = -1f; public string eventName = "";
    public float waterY = float.NaN, hotX = float.NaN, hotZ = float.NaN, hot2X = float.NaN, hot2Z = float.NaN, standX = float.NaN, standZ = float.NaN, tableX = float.NaN, tableZ = float.NaN, meX = float.NaN, meZ = float.NaN;
    public List<float[]> poolSpots = new List<float[]>();   // x, z, r
}

public static class GuideData
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static readonly Color Ink = new Color(0.06f, 0.04f, 0.02f), Faint = new Color(0.28f, 0.2f, 0.12f), Red = new Color(0.55f, 0.06f, 0.03f),
        Green = new Color(0.05f, 0.32f, 0.07f), Blue = new Color(0.04f, 0.16f, 0.45f), Gold = new Color(0.5f, 0.32f, 0f), Purple = new Color(0.34f, 0.08f, 0.47f);

    // ------------------------------------------------------------------ per-map tables
    public static readonly List<MapGuide> MapGuides = new List<MapGuide>();
    static GuideData()
    {
        var sf = new MapGuide
        {
            scene = "snowyforest", title = "Siphoned Forest", cx = -125f, cz = 140f, size = 340f,
            lakeNote = "The lake is not a fixed pond: when the stage starts, the water is laid 3 m below the lowest player standing on the ground and covers every hollow lower than that. Here the frozen ground east and north-east of the drop spot floods into a wide open lake.",
            waterLevels = new[] { "Y 42.48 (10:06 run)", "Y 42.57 (13:14 run)", "Y 28.57 (02:21 run, players landed lower)" },
            spotNotes = new[] {
                "The fishing mod first puts the purple hotspot 15-35 m from a random player. On this map that lands on the snowy land around the drop spot (logged at -198.6,143.4 and -211.4,109.9), so HotBeam moves it to open water within 150 m of a player on land.",
                "Where it ended up (logs): -124.7,51.1 / -123.1,197.0 / -113.5,142.2 / -129.6,107.5 / -64.9,45.9 / -144.5,149.8 / -130.4,125.4 / -93.6,191.4. So: the open water at X -65 to -145, Z 45 to 200.",
                "The hotspot moves again after someone lands a red or lunar fish in it, or when everyone has been more than 120 m away for 30 s." },
            castNotes = new[] {
                "Real casts on this map (55 that landed on water): most went to X -100 to -150, Z 150 to 200 - the shore north of the map ring.",
                "Second favourite: X -125 to -150, Z 100 to 150, right next to the ring.",
                "7 casts hit the ground and 1 player drowned there - the shore is shallow and the lake edge is easy to step into." }
        };
        sf.spots.Add(new GSpot { name = "Map ring (drop spot)", x = -170.8f, z = 126.1f, r = 19.3f, color = new Color(0.25f, 0.25f, 0.25f), note = "27 portal podiums, radius 19 m" });
        sf.spots.Add(new GSpot { name = "Best shore (most casts)", x = -125f, z = 175f, r = 25f, color = Green, note = "X -100..-150, Z 150..200" });
        sf.spots.Add(new GSpot { name = "Shore by the ring", x = -137f, z = 137f, r = 13f, color = Green, note = "X -125..-150, Z 125..150" });
        sf.spots.Add(new GSpot { name = "Hotspot seen", x = -93.6f, z = 191.4f, r = 10f, color = Purple, note = "13:14 run" });
        sf.spots.Add(new GSpot { name = "Hotspot seen", x = -113.5f, z = 142.2f, r = 10f, color = Purple, note = "" });
        sf.spots.Add(new GSpot { name = "Hotspot seen", x = -123.1f, z = 197.0f, r = 10f, color = Purple, note = "10:09 run" });
        sf.spots.Add(new GSpot { name = "Hotspot seen", x = -124.7f, z = 51.1f, r = 10f, color = Purple, note = "10:06 run" });
        sf.spots.Add(new GSpot { name = "Hotspot seen", x = -64.9f, z = 45.9f, r = 10f, color = Purple, note = "" });
        sf.spots.Add(new GSpot { name = "Roulette (13:14 run)", x = -101.6f, z = 246.8f, r = 3f, color = Gold, note = "placed near the hotspot each stage" });
        MapGuides.Add(sf);
    }
    public static MapGuide For(string scene) { foreach (var m in MapGuides) if (m.scene == scene) return m; return null; }

    // ------------------------------------------------------------------ odds (same formula as FishingGameplay.cs OddsTable / EventMult)
    public static string TierOf(GuideFacts f, string sp)
    {
        if (Array.IndexOf(f.lunar, sp) >= 0) return "Lunar"; if (Array.IndexOf(f.red, sp) >= 0) return "Tier3";
        if (Array.IndexOf(f.green, sp) >= 0) return "Tier2"; if (Array.IndexOf(f.white, sp) >= 0) return "Tier1"; return "Boot";
    }
    public static double EventMult(string ev, string sp, string tier)
    {
        double m = 1;
        if (ev.Contains("Rain")) { if (sp == "Catfish" || sp == "Trout" || sp == "Bass") m *= 2; }
        if (ev.Contains("Golden hour")) { if (tier == "Tier3" || tier == "Lunar") m *= 1.5; if (sp == "Golden Koi" || sp == "Salmon") m *= 2; }
        if (ev.Contains("Night")) { if (sp == "Anglerfish" || sp == "Moon Eel" || sp == "Ghost Fish") m *= 3; if (tier == "Tier1") m *= 0.6; }
        if (ev.Contains("Storm")) { if (tier == "Tier3") m *= 2; if (tier == "Lunar") m *= 2.5; }
        return m;
    }
    public static Dictionary<string, double> Odds(GuideFacts f, double[] b, string ev, double rareBonus)
    {
        var w = new Dictionary<string, double>(); string[][] tiers = { f.white, f.green, f.red, f.lunar };
        for (int t = 0; t < 4; t++)
            foreach (string sp in tiers[t])
                w[sp] = (1 - b[0]) * b[t + 1] / tiers[t].Length * EventMult(ev, sp, TierOf(f, sp)) * (t >= 2 ? 1 + rareBonus : 1);
        double fish = 0; foreach (var kv in w) fish += kv.Value;
        var o = new Dictionary<string, double>(); foreach (var kv in w) o[kv.Key] = fish > 0 ? kv.Value / fish * (1 - b[0]) : 0;
        o["Old Boot"] = b[0];
        return o;
    }
    static string Pct(double v) { return (v * 100).ToString(v < 0.1 ? "0.0" : "0", Ci) + "%"; }
    static string Money(int v) { return "$" + v.ToString("#,0", Ci); }
    public static string Clock(float h) { int m = (int)(Mathf.Repeat(h, 24f) * 60f); return (m / 60).ToString("00") + ":" + (m % 60).ToString("00"); }

    // ------------------------------------------------------------------ pages
    static GBlock H(string t) { return new GBlock { kind = GBlock.Heading, text = t }; }
    static GBlock P(string t) { return new GBlock { kind = GBlock.Para, text = t }; }
    static GBlock P(string t, Color c) { return new GBlock { kind = GBlock.Para, text = t, color = c }; }
    static GBlock S(string t) { return new GBlock { kind = GBlock.Small, text = t, color = Faint }; }
    static GBlock B(string t) { return new GBlock { kind = GBlock.Bullet, text = t }; }
    static GBlock Sp() { return new GBlock { kind = GBlock.Space }; }
    static GBlock T(float[] cols, params string[][] rows) { return new GBlock { kind = GBlock.Table, cols = cols, rows = rows }; }
    static string Live(bool on, string what) { return on ? "LIVE in this game" : what + " - not running in this game yet"; }

    public static List<GPage> Build(GuideFacts f, MapGuide map, string scene, Texture2D sketch)
    {
        var pages = new List<GPage>();
        Func<string, GPage> page = t => { var p = new GPage { title = t }; pages.Add(p); return p; };
        string mapName = map != null ? map.title : scene;

        // 1 title
        var p0 = page("");
        p0.blocks.Add(new GBlock { kind = GBlock.Heading, text = "Angler's Guide", color = Red });
        p0.blocks.Add(P("to the lake of " + mapName));
        p0.blocks.Add(Sp());
        p0.blocks.Add(S("Every number in this book is read from the running fishing game (" + f.fishingVersion + ") when you open it."));
        if (!string.IsNullOrEmpty(f.reader)) p0.blocks.Add(S("This copy belongs to " + f.reader + "."));
        p0.blocks.Add(Sp());
        p0.blocks.Add(H("Contents"));
        foreach (string c in new[] { "The lake", "Map & fishing spots", "Best times to fish", "Weather", "Fish of the lake", "Eating fish", "Rods, skins & bobbers", "Lures", "Tips from old anglers" })
            p0.blocks.Add(B(c));
        p0.blocks.Add(Sp());
        p0.blocks.Add(S("Turn pages: Left / Right (or A / D).  Close: G or Esc."));

        // 2 the lake
        var pl = page("The lake");
        if (map != null)
        {
            pl.blocks.Add(P(map.lakeNote));
            pl.blocks.Add(Sp());
            pl.blocks.Add(P("Water level seen on " + map.title + ":"));
            foreach (string w in map.waterLevels) pl.blocks.Add(B(w));
        }
        else pl.blocks.Add(P("When the stage starts, the water is laid 3 m below the lowest player standing on the ground. Every hollow below that floods. No spot notes for " + scene + " yet."));
        if (!float.IsNaN(f.waterY)) pl.blocks.Add(P("Right now: water at Y " + f.waterY.ToString("0.00", Ci) + ".", Blue));
        pl.blocks.Add(Sp());
        pl.blocks.Add(P("The level stays put for the whole stage. Only if every player on the ground has stood more than 60 m above it for 30 s does it rise to 3 m under the lowest of them."));
        pl.blocks.Add(P("Touching the water drowns you (feet 0.1 m below the surface). You get 5 s of grace after spawning, reviving or a new stage.", Red));

        // 3 map + spots
        var pm = page("Map & fishing spots");
        if (sketch != null) pm.blocks.Add(new GBlock { kind = GBlock.Image, img = sketch, imgH = 0.5f, caption = "Top-down, north up, " + (map != null ? map.size : 300f).ToString("0") + " m across. Blue = lake, white/green = land (darker = higher)." });
        pm.blocks.Add(T(new[] { 0.07f, 0.43f, 0.07f, 0.43f },
            new[] { "<color=#b08cd9>O</color>", "where the hotspot has been", "<color=#1f7f26><b>O</b></color>", "good shore (real casts)" },
            new[] { "<color=#1a73b8><b>O</b></color>", "small pond (now)", "<color=#333333><b>O</b></color>", "map ring (drop spot)" },
            new[] { "<color=#1aa088><b>#</b></color>", "rod shop stall", "<color=#d99a0d><b>#</b></color>", "roulette table" },
            new[] { "<color=#c0332a><b>X</b></color>", "you", "", "" }));
        var ps = page("Fishing spots");
        if (map != null)
        {
            foreach (string s in map.castNotes) ps.blocks.Add(B(s));
            ps.blocks.Add(Sp());
            ps.blocks.Add(H("The purple hotspot"));
            foreach (string s in map.spotNotes) ps.blocks.Add(B(s));
        }
        else ps.blocks.Add(P("No spot notes for " + scene + " yet - the map sketch shows the live lake."));
        ps.blocks.Add(P("Hotspot radius " + f.hotspotRadius.ToString("0", Ci) + " m. Inside it: no boots, lunar 10% / red 30% / green 35% / white 25%, and +10% reel chance on school fish.", Purple));
        ps.blocks.Add(H("Finding the hotspots"));
        if (f.compass)
        {
            ps.blocks.Add(P("The hotspots have no marker on the water any more. Use the round compass on the right of the screen, next to the fishing panel: it turns with your camera, the purple needle points to the hotspot and the gold marker to the golden hotspot. The range finder under it shows how far away each one is and how far you are aiming."));
            ps.blocks.Add(P("Cast Distance Tracker (rod shop upgrade" + (f.trackerPrice.Length > 0 ? ", " + f.trackerPrice : "") + "): also shows your last cast distance, a ring on the water where your cast will land while you aim, and how far your bobber is from the hotspot - green \"in zone!\" when it is inside.", Green));
        }
        else
            ps.blocks.Add(P("Hotspots are meant to be found with the HUD compass and range finder (purple needle = hotspot, gold = golden hotspot) and the Cast Distance Tracker rod upgrade, which shows where your cast will land. While those are not running in this game, look for the sparkles on the water."));
        ps.blocks.Add(H("Golden hotspot & ponds"));
        ps.blocks.Add(P("Golden hotspot (" + Live(f.hot2, "weather & time update") + "): 150-300 m from the purple one on open water within 150 m of land, radius 8 m. No boots, lunar 15% / red 40% / green 30% / white 15%, catches worth x1.5. It drifts every 4 minutes or after a red/lunar prize, so it is not in the same place twice.", Gold));
        ps.blocks.Add(P("Ponds (" + Live(f.pools, "weather & time update") + "): 2-5 per stage on flat ground 30-250 m from a player, at least 4 m above the lake. Random each stage. Their own mix: lots of Catfish and Perch (x3), 8% boots; every pond cast gets a bite after 2-6 s (10% sooner), and you can't drown in them." + (f.poolSpots.Count > 0 ? " " + f.poolSpots.Count + " on this stage (blue circles on the map)." : ""), Blue));

        // 4 best times
        var pt = page("Best times to fish");
        pt.blocks.Add(P("One day on the fishing clock lasts " + f.dayMinutes.ToString("0.#", Ci) + " real minutes (one hour = " + (f.dayMinutes * 60f / 24f).ToString("0", Ci) + " s)." + (f.hour >= 0 ? " It is " + Clock(f.hour) + " now." : "")));
        pt.blocks.Add(S("Time-of-day odds: " + Live(f.events, "weather & time update")));
        pt.blocks.Add(T(new[] { 0.3f, 0.7f }, new[] { "<b>05:00-07:00</b>", "Golden hour (dawn)" }, new[] { "<b>07:00-17:30</b>", "Day - normal odds" },
            new[] { "<b>17:30-20:00</b>", "Golden hour (dusk)" }, new[] { "<b>20:00-05:00</b>", "Night" }));
        pt.blocks.Add(Sp());
        pt.blocks.Add(S("Chance per cast on the open lake, clear weather, no lure (same formula as the game):"));
        var day = Odds(f, f.lake, "", 0); var gold = Odds(f, f.lake, "Golden hour", 0); var night = Odds(f, f.lake, "Night", 0); var rain = Odds(f, f.lake, "Rain", 0);
        var rows = new List<string[]> { new[] { "<b>Fish</b>", "<b>Day</b>", "<b>Gold</b>", "<b>Night</b>", "<b>Rain</b>", "<b>Best</b>" } };
        foreach (string sp in AllFish(f))
        {
            double[] v = { day[sp], gold[sp], night[sp], rain[sp] };
            string[] nm = { "day", "golden hr", "night", "rain" };
            int bi = 0; for (int i = 1; i < 4; i++) if (v[i] > v[bi] + 1e-9) bi = i;
            rows.Add(new[] { sp, Pct(v[0]), Pct(v[1]), Pct(v[2]), Pct(v[3]), nm[bi] });
        }
        rows.Add(new[] { "Old Boot", "10%", "10%", "10%", "10%", "-" });
        var pt2 = pt;
        pt2.blocks.Add(T(new[] { 0.28f, 0.13f, 0.13f, 0.13f, 0.13f, 0.2f }, rows.ToArray()));
        pt2.blocks.Add(S("The visible fish school follows the same odds, so at night you will SEE more Anglerfish, Moon Eels and Ghost Fish."));
        pt2.blocks.Add(P("Glow bobbers (Glow, Firefly, Lantern) are made for 20:00-05:00: bites " + ((1 - f.glowBite) * 100).ToString("0", Ci) + "% faster and/or +" + (f.glowRare * 100).ToString("0", Ci) + "% red & lunar fish. " + (f.glowBonus ? "Bonus LIVE." : "The night bonus arrives with the next fishing update; today they only glow."), Green));

        // 5 weather
        var pw = page("Weather");
        pw.blocks.Add(S("Weather: " + Live(f.events, "weather & time update") + (f.events && f.eventName.Length > 0 ? ". Now: " + f.eventName.Replace("+", " + ") : "")));
        pw.blocks.Add(P("The sky is re-rolled every 4-6 minutes: clear 60%, rain 30%, storm 10%. Weather and time of day stack (e.g. Night + Rain)."));
        pw.blocks.Add(T(new[] { 0.27f, 0.73f },
            new[] { "<b>Rain</b>", "Catfish, Trout, Bass x2. Bites 30% faster, and some empty casts get a bite after all." },
            new[] { "<b>Storm</b>", "Red fish x2, lunar x2.5, bites 20% faster. Lightning flashes on the water 25-70 m from players every 5-12 s (just a show - it does not hurt)." },
            new[] { "<b>Golden hour</b>", "Red & lunar x1.5, Golden Koi & Salmon x2." },
            new[] { "<b>Night</b>", "Anglerfish, Moon Eel, Ghost Fish x3, white fish x0.6." }));
        pw.blocks.Add(Sp());
        var storm = Odds(f, f.lake, "Night+Storm", 0); double rareNS = 0; foreach (string sp in f.red) rareNS += storm[sp]; foreach (string sp in f.lunar) rareNS += storm[sp];
        double rareDay = 0; foreach (string sp in f.red) rareDay += day[sp]; foreach (string sp in f.lunar) rareDay += day[sp];
        pw.blocks.Add(P("Best of all: a stormy night. Red + lunar fish on the open lake go from " + Pct(rareDay) + " (clear day) to " + Pct(rareNS) + " per cast.", Purple));
        pw.blocks.Add(S("Type !event (or !weather) to hear the current event; !odds prints your odds right now."));

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
        { string pk; f.perks.TryGetValue("Old Boot", out pk); fr.Add(new[] { "Old Boot", "junk", "$0", pk ?? "" }); }
        pf.blocks.Add(T(new[] { 0.25f, 0.13f, 0.17f, 0.45f }, fr.ToArray()));
        pf.blocks.Add(S("Each fish you hold is one item in your item bar and gives its perk. Golden Koi: the next 3 catches are worth double. Hotspot catches of a school fish pay at least the middle of the range."));

        // 7 eating
        var pe = page("Eating fish");
        pe.blocks.Add(S("!eat <fish>: " + Live(f.eating, "weather & time update")));
        pe.blocks.Add(P("Any fish you hold can be eaten, any time: type !eat <fish> (e.g. !eat minnow). It removes that fish (and its perk) and gives a timed effect, shown on your fishing panel."));
        var er = new List<string[]>();
        foreach (string sp in AllFish(f)) { string e; if (f.eat.TryGetValue(sp, out e)) er.Add(new[] { "<b>" + sp + "</b>", e }); }
        { string e; if (f.eat.TryGetValue("Old Boot", out e)) er.Add(new[] { "<b>Old Boot</b>", e }); }
        pe.blocks.Add(T(new[] { 0.27f, 0.73f }, er.ToArray()));

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
                pr.blocks.Add(S("Every rod type looks different, so you can tell what someone fishes with: Stick = crooked branch with knots and twine, no reel; Basic = plain rod, small reel; Long Cast = very thin with many guide rings; Steady = thick, cork grip, big reel; Quick = short whippy tip, pistol grip, trigger reel; Lucky = clover charm + horseshoe; Golden = gold trim and a glowing gem. Skins colour the rod on top of its shape."));
        }
        { int nCase = 0, lo = int.MaxValue, hi = 0; foreach (var it in f.shop) if (CaseItem(it) && !(it[3] ?? "").Contains("not for sale")) { nCase++; int pz = int.Parse(it[2], Ci); lo = Math.Min(lo, pz); hi = Math.Max(hi, pz); }
          if (nCase > 0) pr.blocks.Add(P("Plus " + nCase + " case-drop skins and bobbers from the fish merchant's cases; the stall also sells them straight (" + Money(lo) + (hi > lo ? "-" + Money(hi) : "") + ").", Purple)); }
        pr.blocks.Add(S("Rod bonuses: range = how far you can cast (normally " + f.castRange.ToString("0", Ci) + " m); reel window = time to click after a bite (1.5 s, 5 s via chat); rare = red & lunar odds; bites = waiting time."));

        // 9 lures
        var pu = page("Lures");
        pu.blocks.Add(S("Lures: " + Live(f.lureShop, "weather & time update")));
        pu.blocks.Add(P("!lures lists them, !buylure <name> buys one (limited casts), !lure <name> equips, !lure off. Every cast that lands on water uses one charge. Map lures only work (and only use charges) on their maps."));
        var lr = new List<string[]>();
        foreach (var l in f.lures)
        {
            bool here = l[4] == null || Array.IndexOf(l[4].Split(','), scene) >= 0;
            lr.Add(new[] { (here && l[4] != null ? "<color=#1f6b24><b>" + l[0] + "</b></color>" : "<b>" + l[0] + "</b>"), Money(int.Parse(l[1], Ci)) + ", " + l[2] + " casts", l[3] + (l[4] != null ? " (" + (here ? "THIS MAP" : "maps: " + l[4]) + ")" : "") });
        }
        pu.blocks.Add(T(new[] { 0.27f, 0.25f, 0.48f }, lr.ToArray()));
        if (scene == "snowyforest") pu.blocks.Add(P("Here on Siphoned Forest the Frost Lure works: Trout & Salmon x2, Moon Eel x1.5.", Green));

        // 10 tips
        var px = page("Tips from old anglers");
        px.blocks.Add(B("Cast into the water near the fish you can see: a fish within 15 m swims to your bobber and bites in 1-3 s. Reel chance 95% at 0 m down to 35% at 15 m (+10% in the hotspot)."));
        px.blocks.Add(B("No fish within 15 m? Only half of such casts get a bite (after 6-12 s); the rest come back empty after 12 s."));
        px.blocks.Add(B("Don't click before it bites - you yank too early and scare the fish. After a bite you have 1.5 s (5 s if you fish by typing 'fish')."));
        px.blocks.Add(B("A line that lands on the ground doesn't fish. Aim down at the water, or aim up to lob (15 m at -10 deg up to " + f.castRange.ToString("0", Ci) + " m at +45 deg)."));
        px.blocks.Add(B("The Rod Whip (right click) hits the nearest player within " + f.whipRange.ToString("0", Ci) + " m and " + f.whipAngle.ToString("0", Ci) + " deg of your aim for exactly 1 damage and snaps their line back. " + f.whipCooldown.ToString("0.#", Ci) + " s cooldown."));
        px.blocks.Add(B("Don't step into the lake: you drown and spectate until the next stage - unless you hold a Moon Eel (free revive)" + (f.eating ? " or ate a Ghost Fish." : ".")));
        px.blocks.Add(B("Old Boots: 10% of lake casts. Never in a hotspot. Worth nothing, but +2% fishing luck."));
        px.blocks.Add(B("A red or lunar fish from the hotspot is the PRIZE: it moves the hotspot."));
        px.blocks.Add(B("!scores = leaderboard, !collection = your fish, !bet red Salmon at the roulette table (red/black pays 2 fish, a number 10, green 14)."));
        return pages;
    }

    public static bool CaseItem(string[] it) { string d = it.Length > 3 ? it[3] ?? "" : ""; return d.StartsWith("case drop") || d.Contains("StatTrak case drop"); }
    public static IEnumerable<string> AllFish(GuideFacts f)
    {
        foreach (var a in new[] { f.white, f.green, f.red, f.lunar }) foreach (var s in a) yield return s;
    }
}
