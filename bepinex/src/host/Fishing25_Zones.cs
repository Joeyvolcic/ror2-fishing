// =====================================================================================================
// FISHING ZONES (guide agent) - partial class of Fishing25, added by agents\guide\patch_zones.py.
//  * 3-4 small named zones per map on open water near the shore (r 12-20 m). Names + modifiers come from the data table
//    ZoneTables below (one ZoneMap per scene, "*" = any other map); positions are DETERMINISTIC: a 6 m grid of open-water
//    cells around the lake centre (rounded to 50 m) is shuffled with a fixed per-map seed and the first cell where the whole
//    circle is open water and walkable shore is within ~25 m of the rim wins. Same map + same lake = same zones, also after a
//    game restart. Re-placed when the lake moves (island, !water) - never while a TestSandbox is open.
//  * Rules (ZoneRule): [cond>]target*mult. cond = rod:A,B | bobber:A,B | lure:A | event:Night | weather:Rain | time:5-7,
//    combined with '&'. target = species list | white|green|red|lunar|rare | bite (bite wait multiplier).
//    "place" rules (no rod/bobber/lure condition) describe the water: they shape the fish that live in the zone (school
//    spawn roll) AND every pipeline roll there. "angler" rules (rod/bobber/lure) apply to the angler's rolls only.
//  * Odds pipeline: SpeciesMult x ZoneMult (gpZone context), BiteMult x zone bite rules. School fish are now regional:
//    a bobber in the hotspot / a zone only attracts fish anchored in that same region (each keeps >= 3 fish while a
//    player is near), the lake only lake fish. Repick weighs candidates by ANGLER multipliers only (events + place rules
//    are already in the spawn roll; before this version events were applied twice). Free fish re-roll their species when
//    the conditions change (event / zone time window), so the visible school always follows the current odds.
//  * Guests: [FT] ZONE|scene|idx|name|x|y|z|r|cue|target (on placement + every 30 s), [FT] ZONECAST|player|idx|name.
//  * Guide / tests: ZoneDefsFor(scene), ZonesLive(), ZoneNameAt(x,z), WhatIf(...), BaseTableOf(where), zoneTestForce.
// =====================================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

public static partial class Fishing25
{
    public class ZoneRule { public string cond = "", target = ""; public double mult = 1; public bool angler; public string src; }
    public class ZoneDef { public string name, cue, target, blurb; public float r; public readonly List<ZoneRule> rules = new List<ZoneRule>(); }
    public class ZoneMap { public string scene, title; public int seed; public readonly List<ZoneDef> zones = new List<ZoneDef>(); }
    public class ZonePlaced { public int idx; public ZoneDef def; public float x, y, z, r; }

    public static bool zonesEnabled = true;
    public static string zoneTestForce;            // test (sandbox preset "zone:<name>"): every water cast counts as this zone, guaranteed bite 2-6 s
    public static float hourOverride = -1f;         // what-if: Hour() returns this when >= 0
    public static int zoneStock = 3;                // fish kept anchored in a zone / the hotspot while a player is within 65 m
    public static readonly List<ZonePlaced> zones = new List<ZonePlaced>();
    public static string zoneKey, zoneScene;
    public static string zoneHostFile = FishingPaths.Data("fishing_zones.json");
    static ZoneDef gpZone;                          // zone context of the current roll (null = none)
    static readonly Dictionary<string, int> castZone = new Dictionary<string, int>();
    static float zoneTryAt = -100f, zoneBroadcastAt = -100f;
    static readonly Dictionary<int, float> zoneSpawnAt = new Dictionary<int, float>();
    static string zoneSig;
    static int zoneErrors;
    static readonly string ZoneLogPath = Path.Combine(Path.GetTempPath(), "ror2_zones.log");
    static void ZLog(string s) { try { File.AppendAllText(ZoneLogPath, DateTime.Now.ToString("HH:mm:ss ") + "Fishing25: " + s + Environment.NewLine); } catch { } }

    // ================================================================= data table (edit here to add a map)
    //  zone line: name | radius m | cue (reeds/lilies/buoy/ice) | featured target | blurb | rules ';'-separated
    static readonly string[][] ZoneTables = {
        new[] { "snowyforest", "Siphoned Forest", "1",
            "Frozen Reeds|16|reeds|Perch,Catfish|Shallow water among frozen reeds - panfish country.|Perch,Catfish*2.5; weather:Rain>Catfish,Trout*1.5; rod:Quick Rod>bite*0.75",
            "Moonlit Hollow|14|ice|lunar|A still pocket between ice floes that wakes up after dark.|event:Night>Moon Eel,Ghost Fish*2.5; bobber:Glow Bobber,Lantern Bobber&event:Night>bite*0.6; bobber:Firefly Bobber,Lantern Bobber&event:Night>lunar*1.4",
            "Deep Drop|20|buoy|red|The bottom falls away under the old buoy - big-fish water, few small fry.|red*1.5; white*0.75; rod:Long Cast Rod>rare*1.6",
            "Koi Bend|12|lilies|Golden Koi|Lily pads in a quiet bend. The koi rise at dawn.|time:5-7>Golden Koi*3; Salmon*1.5; lure:Golden Fly>Golden Koi*1.5" },
        new[] { "blackbeach", "Distant Roost", "2",
            "Tide Pools|15|reeds|Clownfish,Pufferfish|Rocky shallows full of small bright fish.|Clownfish*3; Pufferfish*2; lure:Coral Lure>Swordfish*3.5",
            "Wreck Shadow|18|buoy|red|Dark water by a marker buoy. Storms stir up the big ones.|weather:Storm>Swordfish,Anglerfish*2; rod:Lucky Rod>rare*1.4",
            "Black Sand Cove|14|lilies|Salmon|A warm cove where the salmon run at dusk.|time:17.5-20>Salmon*2.5; rod:Quick Rod>bite*0.7" },
        new[] { "goolake", "Abandoned Aqueduct", "3",
            "Oasis Reeds|15|reeds|Catfish,Pufferfish|Reeds around a desert spring.|Catfish*2.5; Pufferfish*2; lure:Sand Lure>Pufferfish*1.5",
            "Sunken Arch|20|buoy|rare|Deep water over a fallen aqueduct arch.|red*1.4; lunar*1.3; rod:Steady Rod>rare*1.5",
            "Night Spring|13|lilies|Anglerfish|Cool spring water; anglerfish hunt here at night.|event:Night>Anglerfish*2.5; bobber:Glow Bobber,Lantern Bobber&event:Night>bite*0.6" },
        new[] { "*", "", "7",
            "Reed Shallows|15|reeds|Perch,Catfish|Shallow water in the reeds.|Perch,Catfish*2.5; weather:Rain>Trout*2",
            "Deep Water|20|buoy|red|Deep water by a marker buoy.|red*1.4; white*0.8; rod:Long Cast Rod>rare*1.5",
            "Lily Cove|13|lilies|Golden Koi|A quiet cove of lily pads.|time:5-7>Golden Koi*3; bobber:Glow Bobber,Lantern Bobber&event:Night>bite*0.6" } };

    static List<ZoneMap> zoneMaps;
    static List<ZoneMap> ZoneMaps { get { if (zoneMaps == null) zoneMaps = ParseZoneTables(); return zoneMaps; } }
    static List<ZoneMap> ParseZoneTables()
    {
        var l = new List<ZoneMap>();
        foreach (string[] t in ZoneTables)
        {
            var m = new ZoneMap { scene = t[0], title = t[1], seed = int.Parse(t[2], CultureInfo.InvariantCulture) };
            for (int i = 3; i < t.Length; i++)
            {
                string[] f = t[i].Split(new[] { '|' });
                var d = new ZoneDef { name = f[0], r = float.Parse(f[1], CultureInfo.InvariantCulture), cue = f[2], target = f[3], blurb = f[4] };
                foreach (string raw in f[5].Split(new[] { ';' }))
                {
                    string s = raw.Trim(); if (s.Length == 0) continue;
                    var zr = new ZoneRule { src = s };
                    int gt = s.IndexOf('>'); if (gt >= 0) { zr.cond = s.Substring(0, gt).Trim(); s = s.Substring(gt + 1); }
                    int st = s.LastIndexOf('*');
                    zr.target = s.Substring(0, st).Trim(); zr.mult = double.Parse(s.Substring(st + 1), CultureInfo.InvariantCulture);
                    zr.angler = zr.cond.Contains("rod:") || zr.cond.Contains("bobber:") || zr.cond.Contains("lure:");
                    d.rules.Add(zr);
                }
                m.zones.Add(d);
            }
            l.Add(m);
        }
        return l;
    }
    public static ZoneMap ZoneMapFor(string scene)
    {
        foreach (var m in ZoneMaps) if (m.scene == scene) return m;
        foreach (var m in ZoneMaps) if (m.scene == "*") return m;
        return null;
    }
    static ZoneDef ZoneDefNamed(string name)
    {
        if (name == null) return null;
        foreach (var m in ZoneMaps) foreach (var d in m.zones) if (string.Equals(d.name, name, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    // ================================================================= rule evaluation
    static bool InList(string list, string v) { if (v == null) return false; foreach (string s in list.Split(new[] { ',' })) if (s.Trim() == v) return true; return false; }
    static bool HourIn(string span, float h)
    {
        string[] p = span.Split(new[] { '-' }); float a = float.Parse(p[0], CultureInfo.InvariantCulture), b = float.Parse(p[1], CultureInfo.InvariantCulture);
        return a <= b ? h >= a && h < b : h >= a || h < b;
    }
    static bool CondOk(ZoneRule zr, string name, string lure)
    {
        if (zr.cond.Length == 0) return true;
        foreach (string part in zr.cond.Split(new[] { '&' }))
        {
            string c = part.Trim(); int k = c.IndexOf(':'); string kind = c.Substring(0, k), v = c.Substring(k + 1);
            switch (kind)
            {
                case "rod": if (name == null || !InList(v, RodOf(name).name)) return false; break;
                case "bobber": if (name == null || !InList(v, BobberOf(name))) return false; break;
                case "lure": if (name == null || !InList(v, lure)) return false; break;
                case "event": case "weather": if (!eventName.Contains(v)) return false; break;
                case "time": if (!HourIn(v, Hour())) return false; break;
                default: return false;
            }
        }
        return true;
    }
    static bool TargetHit(string target, string sp, string tier)
    {
        switch (target)
        {
            case "white": return tier == "Tier1";
            case "green": return tier == "Tier2";
            case "red": return tier == "Tier3";
            case "lunar": return tier == "Lunar";
            case "rare": return tier == "Tier3" || tier == "Lunar";
            case "bite": return false;
        }
        return InList(target, sp);
    }
    // product of the zone's rules for one species; anglerOnly = only rod/bobber/lure rules
    static double ZoneMultPart(ZoneDef z, string name, string sp, string tier, string lure, bool anglerOnly)
    {
        if (z == null || sp == "Old Boot") return 1;
        double m = 1;
        foreach (var zr in z.rules)
        {
            if (anglerOnly && !zr.angler) continue;
            if (zr.target == "bite" || !TargetHit(zr.target, sp, tier) || !CondOk(zr, name, lure)) continue;
            m *= zr.mult;
        }
        return m;
    }
    static double ZoneSpeciesMult(string name, string sp, string tier, string lure) { return ZoneMultPart(gpZone, name, sp, tier, lure, false); }
    static float ZoneBiteMult(string name, string where, string lure)
    {
        if (gpZone == null || where == "pool") return 1f;
        double m = 1;
        foreach (var zr in gpZone.rules) if (zr.target == "bite" && CondOk(zr, name, lure)) m *= zr.mult;
        return (float)m;
    }
    // Repick weights: rod / lure / eaten-fish buffs / zone angler rules (no events, no place rules - those shaped the spawn)
    public static double SpeciesMultAngler(string name, string sp, string where, string lure)
    {
        string tier = GpTier(sp);
        double m = 1;
        if (name != null) m *= GpRodMult(name, tier, sp);
        LureDef ld; if (lure != null && Lures.TryGetValue(lure, out ld)) m *= ld.Mult(sp, tier);
        if (name != null)
        {
            if (BuffOn(name, "Clownfish") && (tier == "Tier3" || tier == "Lunar")) m *= 1.3;
            if (BuffOn(name, "Catfish") && tier == "Tier2") m *= 1.5;
        }
        m *= ZoneMultPart(gpZone, name, sp, tier, lure, true);
        return m;
    }

    // ================================================================= regions: -1 lake, 0 purple hotspot, 1+k zone k
    public static int ZoneIdxAt(float x, float z)
    {
        if (!zonesEnabled || zoneScene != curScene) return -1;
        for (int i = 0; i < zones.Count; i++) { var p = zones[i]; float dx = x - p.x, dz = z - p.z; if (dx * dx + dz * dz <= p.r * p.r) return i; }
        return -1;
    }
    public static string ZoneNameAt(float x, float z) { int i = ZoneIdxAt(x, z); return i < 0 ? null : zones[i].def.name; }
    public static int RegionAt(float x, float z) { if (InHot(x, z)) return 0; int k = ZoneIdxAt(x, z); return k >= 0 ? 1 + k : -1; }
    static int FishRegion(SchoolFish f) { return f == null ? -1 : RegionAt(f.ax, f.az); }
    static int BobberRegion(float bx, float bz, bool hot) { if (hot) return 0; int k = ZoneIdxAt(bx, bz); return k >= 0 ? 1 + k : -1; }
    static bool SameRegion(SchoolFish f, int region) { return zoneTestForce != null || FishRegion(f) == region; }
    static int LakeFishCount() { int n = 0; foreach (SchoolFish f in school.Values) if (FishRegion(f) < 0) n++; return n; }
    static bool DartOk(SchoolFish f, float ax, float az) { return RegionAt(ax, az) == FishRegion(f); }

    // school spawn roll at an anchor: hotspot table inside the hotspot, + the zone's place rules inside a zone
    public static string GpSchoolRollAt(float ax, float az)
    {
        ZoneDef saved = gpZone;
        try
        {
            bool hot = InHot(ax, az);
            int k = hot ? -1 : ZoneIdxAt(ax, az);
            gpZone = k >= 0 ? zones[k].def : null;
            return RollSpecies(null, hot ? "hot" : "lake", null, false);
        }
        finally { gpZone = saved; }
    }

    // ================================================================= cast / roll context
    static ZoneDef CastZoneDef(string name)
    {
        if (zoneTestForce != null) return ZoneDefNamed(zoneTestForce);
        int k; if (!castZone.TryGetValue(name, out k) || k < 0 || k >= zones.Count || zoneScene != curScene) return null;
        return zones[k].def;
    }
    static void ZoneOnCast(string name, Angler a)
    {
        castZone[name] = -1;
        Rig r; rigs.TryGetValue(name, out r);
        if (r == null) return;
        int pool; castPool.TryGetValue(name, out pool);
        if (pool >= 0 || (hasHot2 && InHot2(r.tx, r.tz))) return;
        int k = ZoneIdxAt(r.tx, r.tz);
        castZone[name] = k;
        gpZone = CastZoneDef(name);
        if (gpZone != null)
        {
            ChatRaw("[FT] ZONECAST|" + name + "|" + k + "|" + gpZone.name);
            ZLog(name + ": cast into zone " + gpZone.name + (zoneTestForce != null ? " (FORCED test)" : "") + " at (" + N2(r.tx) + ", " + N2(r.tz) + ")");
        }
    }
    static void GpOnCast(string name, Angler a)
    {
        try { ZoneOnCast(name, a); GpOnCastCore(name, a); }
        catch (Exception e) { if (zoneErrors++ < 20) ZLog("on-cast error: " + e); }
        finally { gpZone = null; }
    }
    static string GpRoll(string name, bool hot)
    {
        ZoneDef saved = gpZone;
        try
        {
            string w; castWhere.TryGetValue(name, out w);
            gpZone = (w == "pool" || w == "hot2") && zoneTestForce == null ? null : CastZoneDef(name);
            return GpRollCore(name, hot);
        }
        finally { gpZone = saved; }
    }

    // ================================================================= placement (main thread, from GameplayTick)
    static uint Fnv(string s) { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h; }
    static Type sandboxType; static float sandboxLookAt = -100f;
    static bool SandboxOpen()
    {
        try
        {
            if (now - sandboxLookAt > 10f)
            {
                sandboxLookAt = now; Type best = null; int bn = -1;
                foreach (var asm in FishingScope.Assemblies())
                    foreach (Type t in SafeTypes(asm)) { Match m = Regex.Match(t.Name, "^TestSandbox(\\d+)$"); if (m.Success && int.Parse(m.Groups[1].Value) > bn) { bn = int.Parse(m.Groups[1].Value); best = t; } }
                sandboxType = best;
            }
            if (sandboxType == null) return false;
            FieldInfo f = sandboxType.GetField("active", BindingFlags.Public | BindingFlags.Static);
            return f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null);
        }
        catch { return false; }
    }

    static bool CircleOpen(float x, float z, float r)
    {
        float gh;
        if (!OpenWater(x, z, out gh)) return false;
        for (int k = 0; k < 12; k++) { double a = k * Math.PI / 6; if (!OpenWater(x + (float)Math.Cos(a) * r, z + (float)Math.Sin(a) * r, out gh)) return false; }
        for (int k = 0; k < 6; k++) { double a = k * Math.PI / 3 + 0.5; if (!OpenWater(x + (float)Math.Cos(a) * r * 0.55f, z + (float)Math.Sin(a) * r * 0.55f, out gh)) return false; }
        return true;
    }
    // walkable shore (ground 0.2-10 m above the water) within ~25 m of the rim, in at least 2 of 16 directions
    static bool NearShore(float x, float z, float r)
    {
        int dirs = 0;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            foreach (float d in new[] { r + 3f, r + 8f, r + 14f, r + 21f, r + 28f })
            {
                float gh;
                if (OpenWater(x + (float)Math.Cos(a) * d, z + (float)Math.Sin(a) * d, out gh)) continue;
                if (gh <= waterY + 10f && gh >= waterY + 0.2f) dirs++;
                break;   // first land in this direction decides
            }
        }
        return dirs >= 2;
    }

    static void PlaceZones(string scene, string key, float cx, float cz)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        zones.Clear(); zoneScene = scene; zoneKey = key; zoneSpawnAt.Clear(); castZone.Clear();
        ZoneMap map = ZoneMapFor(scene);
        if (map == null) return;
        const float step = 6f; const int R = 42;
        var cand = new List<float[]>();
        for (int j = -R; j <= R; j++)
            for (int i = -R; i <= R; i++)
            {
                if (i * i + j * j > R * R) continue;
                float x = cx + i * step, z = cz + j * step, gh;
                if (OpenWater(x, z, out gh)) cand.Add(new[] { x, z });
            }
        var rnd = new Random(map.seed * 7919 + (int)(Fnv(scene) & 0x3fffffff));
        for (int i = cand.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); var t = cand[i]; cand[i] = cand[j]; cand[j] = t; }
        int tests = 0;
        for (int zi = 0; zi < map.zones.Count; zi++)
        {
            ZoneDef d = map.zones[zi];
            ZonePlaced got = null;
            foreach (var c in cand)
            {
                bool far = true;
                foreach (var p in zones) { float dx = p.x - c[0], dz = p.z - c[1]; float need = p.r + d.r + 25f; if (dx * dx + dz * dz < need * need) { far = false; break; } }
                if (!far) continue;
                if (++tests > 1500) break;   // caps the placement hitch (~120k raycasts worst case)
                if (!CircleOpen(c[0], c[1], d.r) || !NearShore(c[0], c[1], d.r)) continue;
                got = new ZonePlaced { idx = zones.Count, def = d, x = c[0], y = waterY, z = c[1], r = d.r };
                break;
            }
            if (got == null) { ZLog("zone " + d.name + ": no spot found on " + scene); continue; }
            zones.Add(got);
        }
        var parts = new List<string>(); foreach (var p in zones) parts.Add(p.def.name + " (" + N2(p.x) + ", " + N2(p.z) + ") r=" + p.r);
        ZLog("zones placed on " + scene + " key " + key + " (" + cand.Count + " open-water cells, " + tests + " tests, " + sw.ElapsedMilliseconds + " ms): " + string.Join("; ", parts.ToArray()));
        ZoneLook25.Build(zones, waterY, map.seed);
        BroadcastZones();
        SaveZones();
    }
    static void SaveZones()
    {
        try
        {
            var sb = new StringBuilder("{\n  \"scene\": \"" + zoneScene + "\", \"key\": \"" + zoneKey + "\", \"version\": \"Fishing25\",\n  \"zones\": [\n");
            for (int i = 0; i < zones.Count; i++)
            {
                var p = zones[i]; var rl = new List<string>(); foreach (var zr in p.def.rules) rl.Add("\"" + zr.src.Replace("\"", "'") + "\"");
                sb.Append("    { \"idx\": " + i + ", \"name\": \"" + p.def.name + "\", \"x\": " + N2(p.x) + ", \"y\": " + N2(p.y) + ", \"z\": " + N2(p.z) + ", \"r\": " + N2(p.r)
                          + ", \"cue\": \"" + p.def.cue + "\", \"target\": \"" + p.def.target + "\", \"rules\": [" + string.Join(", ", rl.ToArray()) + "] }" + (i < zones.Count - 1 ? "," : "") + "\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(zoneHostFile, sb.ToString());
        }
        catch (Exception e) { ZLog("save error: " + e.Message); }
    }
    static void BroadcastZones()
    {
        zoneBroadcastAt = now;
        foreach (var p in zones) ChatRaw("[FT] ZONE|" + zoneScene + "|" + p.idx + "|" + p.def.name + "|" + N2(p.x) + "|" + N2(p.y) + "|" + N2(p.z) + "|" + N2(p.r) + "|" + p.def.cue + "|" + p.def.target);
    }

    // every 0.5 s (GameplayTick): placement, broadcast, stocking, condition re-roll
    static void ZoneTick(string scene)
    {
        try
        {
            if (!zonesEnabled) { if (zones.Count > 0) { zones.Clear(); ZoneLook25.Clear(); } return; }
            bool water0 = hasWater && !IsNull(water) && waterScene == scene;
            if (!water0) { if (zoneScene != null && zoneScene != scene) { zones.Clear(); zoneScene = null; zoneKey = null; ZoneLook25.Clear(); } return; }
            if (now - waterPlacedAt < 3f) return;
            var wp = ((UnityEngine.GameObject)water).transform.position;
            float cx = (float)Math.Round(wp.x / 50f) * 50f, cz = (float)Math.Round(wp.z / 50f) * 50f;
            string key = scene + "|" + cx.ToString("0", Ci) + "|" + cz.ToString("0", Ci) + "|" + waterY.ToString("0.0", Ci);
            if (key != zoneKey && now - zoneTryAt > 5f && !SandboxOpen())
            {
                zoneTryAt = now;
                PlaceZones(scene, key, cx, cz);
                zoneSig = null;
            }
            if (zoneScene != scene) return;
            if (Math.Abs(zones.Count > 0 ? zones[0].y - waterY : 0f) > 0.01f) foreach (var p in zones) p.y = waterY;
            ZoneLook25.Ensure(zones, waterY);
            if (now - zoneBroadcastAt >= 30f) BroadcastZones();
            StockRegions();
            ConditionReroll();
        }
        catch (Exception e) { if (zoneErrors++ < 20) ZLog("tick error: " + e); }
    }

    static bool PlayerNear(float x, float z, float d)
    {
        foreach (var f in LivingFeet()) { float dx = f.x - x, dz = f.z - z; if (dx * dx + dz * dz <= d * d) return true; }
        return false;
    }
    static void StockRegions()
    {
        if (school.Count == 0 && schoolScene != curScene) return;
        var count = new Dictionary<int, int>();
        foreach (SchoolFish f in school.Values) { int g = FishRegion(f); if (g < 0) continue; int c; count.TryGetValue(g, out c); count[g] = c + 1; }
        // purple hotspot
        if (hasHotspot && PlayerNear(hotspotPos.x, hotspotPos.z, 65f))
        {
            int c; count.TryGetValue(0, out c); float at; zoneSpawnAt.TryGetValue(0, out at);
            if (c < zoneStock && now - at > 6f) { zoneSpawnAt[0] = now; SchoolFish n = SpawnFish(true, 0, 0, 0); if (n != null && FishRegion(n) == 0) { school[n.id] = n; SendFish(n); LogC("hotspot stock fish #" + n.id + " " + FishSchool.Species[n.sp]); } }
        }
        for (int i = 0; i < zones.Count; i++)
        {
            var p = zones[i];
            if (!PlayerNear(p.x, p.z, 65f)) continue;
            int c; count.TryGetValue(1 + i, out c); float at; zoneSpawnAt.TryGetValue(1 + i, out at);
            if (c >= zoneStock || now - at < 6f) continue;
            zoneSpawnAt[1 + i] = now;
            for (int t = 0; t < 30; t++)
            {
                double a = rng.NextDouble() * Math.PI * 2, d = Math.Sqrt(rng.NextDouble()) * Math.Max(1f, p.r - 3f);
                float ax = p.x + (float)(Math.Cos(a) * d), az = p.z + (float)(Math.Sin(a) * d);
                float R = Math.Min(2.5f + (float)rng.NextDouble() * 3f, Math.Max(1.5f, p.r * 0.4f));
                if (!AnchorOk(ax, az, R) || RegionAt(ax, az) != 1 + i) continue;
                string sp = GpSchoolRollAt(ax, az);
                var fish = new SchoolFish { id = schoolNextId++, sp = FishSchool.Index(sp), ax = ax, az = az, R = R, seed = rng.Next(1, 1000000) };
                fish.sx = ax; fish.sz = az; fish.t0 = SimNow; fish.D = 2.5f;
                school[fish.id] = fish; SendFish(fish);
                LogC("zone stock fish #" + fish.id + " " + sp + " in " + p.def.name);
                break;
            }
        }
    }

    // conditions changed (event / zone time windows): free fish re-roll their species for the new odds
    static string ConditionSig()
    {
        var sb = new StringBuilder(eventName);
        foreach (var p in zones) foreach (var zr in p.def.rules) if (!zr.angler && zr.cond.Length > 0) sb.Append(CondOk(zr, null, null) ? "1" : "0");
        return sb.ToString();
    }
    static void ConditionReroll()
    {
        string sig = ConditionSig();
        if (zoneSig == null) { zoneSig = sig; return; }
        if (sig == zoneSig) return;
        zoneSig = sig;
        int changed = 0; float t = SimNow;
        foreach (SchoolFish f in new List<SchoolFish>(school.Values))
        {
            if (f.busy != null) continue;
            string sp = GpSchoolRollAt(f.ax, f.az); int si = FishSchool.Index(sp);
            if (si == f.sp) continue;
            float x, z; f.Pos(t, out x, out z);
            var n = new SchoolFish { id = f.id, sp = si, ax = f.ax, az = f.az, R = f.R, seed = f.seed, sx = x, sz = z, t0 = t, D = 1.5f };
            school[f.id] = n; changed++;
        }
        if (changed > 0) schoolDirty = true;
        ZLog("conditions changed (" + eventName + ") -> " + changed + " free school fish re-rolled their species");
    }

    // ================================================================= HUD / carry-over / cleanup
    static int ZoneHud(StringBuilder sb, string me)
    {
        try
        {
            Angler a; if (me == null || !anglers.TryGetValue(me, out a) || a.state == 0) return 0;
            ZoneDef d = CastZoneDef(me); if (d == null) return 0;
            sb.Append("Zone: <color=#9fe0c8>" + d.name + "</color>\n"); return 1;
        }
        catch { return 0; }
    }
    static void ZoneSnapshot(StringBuilder sb)
    {
        if (zoneKey == null) return;
        sb.Append("ZK|" + zoneKey + "|" + zoneScene + "\n");
        foreach (var p in zones) sb.Append("ZP|" + p.def.name + "|" + N2(p.x) + "|" + N2(p.y) + "|" + N2(p.z) + "|" + N2(p.r) + "\n");
    }
    static void ZoneRestoreLine(string[] p)
    {
        if (p[0] == "ZK") { zoneKey = p[1] + "|" + p[2] + "|" + p[3] + "|" + p[4]; zoneScene = p[5]; zones.Clear(); return; }
        ZoneDef d = null; ZoneMap m = ZoneMapFor(zoneScene ?? "");
        if (m != null) foreach (var z in m.zones) if (z.name == p[1]) d = z;
        if (d == null) return;
        zones.Add(new ZonePlaced { idx = zones.Count, def = d, x = float.Parse(p[2], Ci), y = float.Parse(p[3], Ci), z = float.Parse(p[4], Ci), r = float.Parse(p[5], Ci) });
    }
    public static void ZoneClear() { try { zones.Clear(); ZoneLook25.Clear(); } catch { } }
    static void ZoneCleanOld()
    {
        foreach (var asm in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(asm))
            {
                if (t == typeof(Fishing25) || !Regex.IsMatch(t.Name, "^Fishing\\d+$")) continue;
                MethodInfo m = t.GetMethod("ZoneClear", BindingFlags.Public | BindingFlags.Static);
                if (m != null) try { m.Invoke(null, null); ZLog("cleared zone cues of " + t.Name); } catch { }
            }
    }

    // ================================================================= guide / test API (main thread)
    public static double[] BaseTableOf(string where) { return (double[])BaseTable(where).Clone(); }
    public static string[] ZoneSpecies() { var l = new List<string>(); foreach (var a in new[] { TierWhite, TierGreen, TierRed, TierLunar }) l.AddRange(a); return l.ToArray(); }
    // rows: name, radius, cue, target, blurb, rules (';'), human text (';')
    public static List<string[]> ZoneDefsFor(string scene)
    {
        var l = new List<string[]>(); ZoneMap m = ZoneMapFor(scene); if (m == null) return l;
        foreach (var d in m.zones)
        {
            var src = new List<string>(); var txt = new List<string>();
            foreach (var zr in d.rules) { src.Add(zr.src); txt.Add(RuleText(zr)); }
            l.Add(new[] { d.name, d.r.ToString("0", Ci), d.cue, d.target, d.blurb, string.Join(";", src.ToArray()), string.Join(";", txt.ToArray()), m.scene });
        }
        return l;
    }
    // rows: idx, name, x, y, z, r, cue, target, scene
    public static List<string[]> ZonesLive()
    {
        var l = new List<string[]>();
        if (zoneScene != curScene) return l;
        foreach (var p in zones) l.Add(new[] { p.idx.ToString(Ci), p.def.name, N2(p.x), N2(p.y), N2(p.z), N2(p.r), p.def.cue, p.def.target, zoneScene });
        return l;
    }
    static string Names(string list) { var p = list.Split(new[] { ',' }); for (int i = 0; i < p.Length; i++) p[i] = p[i].Trim(); return p.Length == 1 ? p[0] : string.Join(", ", p, 0, p.Length - 1) + " & " + p[p.Length - 1]; }
    public static string TargetText(string target)
    {
        switch (target) { case "white": return "white fish"; case "green": return "green fish"; case "red": return "red fish"; case "lunar": return "lunar fish"; case "rare": return "red & lunar fish"; }
        return Names(target);
    }
    public static string CondText(string cond)
    {
        if (cond.Length == 0) return "";
        var parts = new List<string>();
        foreach (string part in cond.Split(new[] { '&' }))
        {
            string c = part.Trim(); int k = c.IndexOf(':'); string kind = c.Substring(0, k), v = c.Substring(k + 1);
            if (kind == "time") { string[] p = v.Split(new[] { '-' }); parts.Add(Clock(float.Parse(p[0], Ci)) + "-" + Clock(float.Parse(p[1], Ci))); }
            else if (kind == "event") parts.Add(v == "Night" ? "at night" : v);
            else if (kind == "weather") parts.Add("in " + (v == "Storm" ? "a storm" : v.ToLowerInvariant()));
            else parts.Add("with a " + v.Replace(",", " or "));
        }
        return string.Join(", ", parts.ToArray());
    }
    static string Clock(float h) { int m = (int)(h * 60f + 0.5f); return (m / 60 % 24).ToString("00") + ":" + (m % 60).ToString("00"); }
    public static string RuleText(ZoneRule zr)
    {
        string c = CondText(zr.cond);
        string e = zr.target == "bite"
            ? "bites " + Math.Round(Math.Abs(1 - zr.mult) * 100).ToString("0", Ci) + "% " + (zr.mult < 1 ? "faster" : "slower")
            : TargetText(zr.target) + " x" + zr.mult.ToString("0.##", Ci);
        return c.Length > 0 ? char.ToUpper(c[0]) + c.Substring(1) + ": " + e : char.ToUpper(e[0]) + e.Substring(1);
    }

    // What-if odds through the REAL pipeline: a temporary angler "~whatif" with this rod + bobber, this lure, event and hour.
    // Returns species -> chance (incl. "Old Boot") plus "~bite" = bite-wait multiplier (gameplay x rod x glow bobber).
    public static Dictionary<string, double> WhatIf(string where, string zone, string rod, string bobber, string lure, string ev, float hour)
    {
        const string who = "~whatif";
        RodOwner saved; bool had = rodOwners.TryGetValue(who, out saved);
        string evSaved = eventName; float hSaved = hourOverride; ZoneDef zSaved = gpZone;
        try
        {
            var o = new RodOwner(); o.rod = rod ?? "Basic Rod"; o.bobber = bobber ?? "Plain"; rodOwners[who] = o;
            eventName = ev ?? "Clear"; hourOverride = hour; gpZone = ZoneDefNamed(zone);
            var t = OddsTable(who, where ?? "lake", lure, false);
            t["~bite"] = BiteMult(who, where ?? "lake", lure) * BiteMult(who) * GlowBite(who);
            return t;
        }
        finally
        {
            if (had) rodOwners[who] = saved; else rodOwners.Remove(who);
            eventName = evSaved; hourOverride = hSaved; gpZone = zSaved;
        }
    }
    // offline tests: place zones by hand (no Unity), roll through the real pipeline
    public static void ZoneTestPlace(string scene, string[] names, float[] xs, float[] zs)
    {
        zones.Clear(); zoneScene = scene; curScene = scene;
        for (int i = 0; i < names.Length; i++) { ZoneDef d = ZoneDefNamed(names[i]); if (d != null) zones.Add(new ZonePlaced { idx = zones.Count, def = d, x = xs[i], z = zs[i], r = d.r }); }
    }
    public static string WhatIfRoll(string where, string zone, string rod, string bobber, string lure, string ev, float hour)
    {
        const string who = "~whatif";
        RodOwner saved; bool had = rodOwners.TryGetValue(who, out saved);
        string evSaved = eventName; float hSaved = hourOverride; ZoneDef zSaved = gpZone;
        try
        {
            var o = new RodOwner(); o.rod = rod ?? "Basic Rod"; o.bobber = bobber ?? "Plain"; rodOwners[who] = o;
            eventName = ev ?? "Clear"; hourOverride = hour; gpZone = ZoneDefNamed(zone);
            return RollSpecies(who, where ?? "lake", lure, false);
        }
        finally
        {
            if (had) rodOwners[who] = saved; else rodOwners.Remove(who);
            eventName = evSaved; hourOverride = hSaved; gpZone = zSaved;
        }
    }
    // offline school model hooks: the same weights the live Repick / spawn use
    public static double RepickWeight(string zone, string rod, string bobber, string lure, string ev, float hour, string sp)
    {
        const string who = "~whatif";
        RodOwner saved; bool had = rodOwners.TryGetValue(who, out saved);
        string evSaved = eventName; float hSaved = hourOverride; ZoneDef zSaved = gpZone;
        try
        {
            var o = new RodOwner(); o.rod = rod ?? "Basic Rod"; o.bobber = bobber ?? "Plain"; rodOwners[who] = o;
            eventName = ev ?? "Clear"; hourOverride = hour; gpZone = ZoneDefNamed(zone);
            return sp == "Old Boot" ? 1 : SpeciesMultAngler(who, sp, "lake", lure);
        }
        finally
        {
            if (had) rodOwners[who] = saved; else rodOwners.Remove(who);
            eventName = evSaved; hourOverride = hSaved; gpZone = zSaved;
        }
    }
    public static Dictionary<string, double> SpawnTable(string where, string zone, string ev, float hour)
    {
        string evSaved = eventName; float hSaved = hourOverride; ZoneDef zSaved = gpZone;
        try { eventName = ev ?? "Clear"; hourOverride = hour; gpZone = ZoneDefNamed(zone); return OddsTable(null, where, null, false); }
        finally { eventName = evSaved; hourOverride = hSaved; gpZone = zSaved; }
    }
    public static string SpawnRoll(string where, string zone, string ev, float hour)
    {
        string evSaved = eventName; float hSaved = hourOverride; ZoneDef zSaved = gpZone;
        try { eventName = ev ?? "Clear"; hourOverride = hour; gpZone = ZoneDefNamed(zone); return RollSpecies(null, where, null, false); }
        finally { eventName = evSaved; hourOverride = hSaved; gpZone = zSaved; }
    }
}

// subtle in-world cues for the zones (host): a few reeds / lily pads / a buoy / ice floes. No labels, no lights, no colliders.
public static class ZoneLook25
{
    static readonly List<UnityEngine.GameObject> roots = new List<UnityEngine.GameObject>();
    static string builtKey;
    static UnityEngine.Material Mat(UnityEngine.Color c)
    {
        var sh = UnityEngine.Shader.Find("Standard") ?? UnityEngine.Shader.Find("Sprites/Default");
        var m = new UnityEngine.Material(sh); m.color = c;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.15f);
        return m;
    }
    static void Part(UnityEngine.GameObject root, UnityEngine.PrimitiveType t, UnityEngine.Vector3 pos, UnityEngine.Vector3 scale, UnityEngine.Vector3 euler, UnityEngine.Material m)
    {
        var go = UnityEngine.GameObject.CreatePrimitive(t);
        var col = go.GetComponent<UnityEngine.Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
        go.name = "FishingZoneCuePart";
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale; go.transform.localRotation = UnityEngine.Quaternion.Euler(euler);
        var r = go.GetComponent<UnityEngine.Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
    public static void Clear() { foreach (var g in roots) if (g != null) UnityEngine.Object.Destroy(g); roots.Clear(); builtKey = null; }
    public static void Ensure(List<Fishing25.ZonePlaced> zs, float waterY)
    {
        bool missing = roots.Count != zs.Count; foreach (var g in roots) if (g == null) missing = true;
        if (missing) Build(zs, waterY, 0);
        else for (int i = 0; i < roots.Count; i++) { var p = roots[i].transform.position; if (Math.Abs(p.y - waterY) > 0.01f) roots[i].transform.position = new UnityEngine.Vector3(p.x, waterY, p.z); }
    }
    public static void Build(List<Fishing25.ZonePlaced> zs, float waterY, int seed)
    {
        Clear();
        UnityEngine.Material reed = Mat(new UnityEngine.Color(0.30f, 0.36f, 0.16f)), reedTop = Mat(new UnityEngine.Color(0.42f, 0.30f, 0.16f)), pad = Mat(new UnityEngine.Color(0.20f, 0.42f, 0.18f)),
            bloom = Mat(new UnityEngine.Color(0.95f, 0.70f, 0.80f)), red = Mat(new UnityEngine.Color(0.75f, 0.12f, 0.08f)), white = Mat(new UnityEngine.Color(0.92f, 0.92f, 0.90f)),
            ice = Mat(new UnityEngine.Color(0.86f, 0.92f, 0.96f));
        foreach (var p in zs)
        {
            var root = new UnityEngine.GameObject("FishingZoneCue " + p.def.name);
            root.transform.position = new UnityEngine.Vector3(p.x, waterY, p.z);
            int hs = 7; foreach (char ch in p.def.name) hs = hs * 31 + ch; var r = new Random(hs & 0xffff);
            switch (p.def.cue)
            {
                case "reeds":
                    for (int c = 0; c < 3; c++)
                    {
                        double a = r.NextDouble() * Math.PI * 2; float d = p.r * (0.6f + (float)r.NextDouble() * 0.3f);
                        float cx = (float)Math.Cos(a) * d, cz = (float)Math.Sin(a) * d;
                        for (int k = 0; k < 6; k++)
                        {
                            float h = 1.0f + (float)r.NextDouble() * 1.0f, ox = cx + (float)(r.NextDouble() - 0.5) * 1.6f, oz = cz + (float)(r.NextDouble() - 0.5) * 1.6f;
                            var tilt = new UnityEngine.Vector3((float)(r.NextDouble() - 0.5) * 14f, 0, (float)(r.NextDouble() - 0.5) * 14f);
                            Part(root, UnityEngine.PrimitiveType.Cylinder, new UnityEngine.Vector3(ox, h / 2f - 0.3f, oz), new UnityEngine.Vector3(0.05f, h / 2f, 0.05f), tilt, reed);
                            if (k % 2 == 0) Part(root, UnityEngine.PrimitiveType.Capsule, new UnityEngine.Vector3(ox, h - 0.25f, oz), new UnityEngine.Vector3(0.09f, 0.16f, 0.09f), tilt, reedTop);
                        }
                    }
                    break;
                case "lilies":
                    for (int k = 0; k < 8; k++)
                    {
                        double a = r.NextDouble() * Math.PI * 2; float d = p.r * (float)Math.Sqrt(r.NextDouble()) * 0.8f, s = 0.7f + (float)r.NextDouble() * 0.6f;
                        var pos = new UnityEngine.Vector3((float)Math.Cos(a) * d, 0.03f, (float)Math.Sin(a) * d);
                        Part(root, UnityEngine.PrimitiveType.Cylinder, pos, new UnityEngine.Vector3(s, 0.01f, s), UnityEngine.Vector3.zero, pad);
                        if (k % 3 == 0) Part(root, UnityEngine.PrimitiveType.Sphere, pos + new UnityEngine.Vector3(0, 0.07f, 0), new UnityEngine.Vector3(0.16f, 0.12f, 0.16f), UnityEngine.Vector3.zero, bloom);
                    }
                    break;
                case "buoy":
                    Part(root, UnityEngine.PrimitiveType.Sphere, new UnityEngine.Vector3(1.5f, 0.1f, -1f), new UnityEngine.Vector3(0.55f, 0.5f, 0.55f), UnityEngine.Vector3.zero, red);
                    Part(root, UnityEngine.PrimitiveType.Cylinder, new UnityEngine.Vector3(1.5f, 0.42f, -1f), new UnityEngine.Vector3(0.12f, 0.25f, 0.12f), UnityEngine.Vector3.zero, white);
                    break;
                case "ice":
                    for (int k = 0; k < 6; k++)
                    {
                        double a = r.NextDouble() * Math.PI * 2; float d = p.r * (0.75f + (float)r.NextDouble() * 0.3f);
                        Part(root, UnityEngine.PrimitiveType.Cube, new UnityEngine.Vector3((float)Math.Cos(a) * d, 0.04f, (float)Math.Sin(a) * d),
                             new UnityEngine.Vector3(1.0f + (float)r.NextDouble() * 1.6f, 0.12f, 0.8f + (float)r.NextDouble() * 1.2f), new UnityEngine.Vector3(0, (float)r.NextDouble() * 180f, 0), ice);
                    }
                    break;
            }
            roots.Add(root);
        }
    }
}
