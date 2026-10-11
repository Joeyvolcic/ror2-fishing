// =====================================================================================================
// GAMEPLAY add-on for the host fishing mod (partial class of Fishing25; built by agents\gameplay\patch_gameplay.py)
//   5  !eat <species>      eat one held fish (removes its carrier item) for a timed buff        [FT] EAT|name|species|seconds
//   6  weather / time      Rain, Golden hour, Night, Storm change bite rate + species odds      [FT] EVENT|name|secondsLeft, [FT] BOLT|x|y|z
//   10 golden far hotspot  150-300 m from the purple one, best odds, moves every 4 min / prize   [FT] HOT2|x|y|z|r
//   11 small pools         2-5 ponds per stage on flat ground away from the lake, own fish mix   [FT] POOL|id|x|y|z|r, [FT] PCAST|name|x|y|z
//   12 lures               !lures, !buylure <name>, !lure <name>|off - limited casts, bought with fishing money
//
// ODDS PIPELINE (one place: OddsTable / BiteMult below). Every species roll that is not a visible school fish goes
// through RollSpecies(); school fish are picked among the fish in range with the same multipliers (Repick).
//   weight(species) = Base(location, tier) / #species-in-tier      (Old Boot = Base boot chance, never multiplied)
//                   x Location   pool: Catfish, Perch x3
//                   x Rod        GpRodMult(name, tier, species)          (rods agent; Lucky Rod etc.)
//                   x Pet        petTierMult (pets agent)                 (pet auto-catches only)
//                   x Event      Rain / Golden hour / Night / Storm (combine by multiplying)
//                   x Lure       the lure used for THIS cast (one charge per valid water cast)
//                   x Eat buff   Clownfish (red+lunar x1.3), Catfish (green x1.5)
//                   x Zone       fishing zone the bobber is in (Fishing25_Zones.cs: place rules + rod/bobber/lure rules)
//   then normalised and rolled.
//   Base tables (boot / white / green / red / lunar):
//     lake 10% / 60 30 8 2      purple hotspot 0% / 25 35 30 10      golden HOT2 0% / 15 30 40 15      pool 8% / 70 25 4.5 0.5
//   Bite delay = the fishing mod's delay x BiteMult = Event (Rain 0.7, Storm 0.8) x Lure (Deep Jig 1.5) x Eat (Trout 0.7)
//                x Location (pool 0.9) x Rod (GpRodBiteMult). Pools / HOT2 always get a bite after (2..6 s) x BiteMult.
//   Value = catch value (x2 Golden Koi held bonus) x Eat (Golden Koi eaten: next catch x3; Bass: x1.5) x HOT2 x1.5.
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
    static readonly string GpLogPath = Path.Combine(Path.GetTempPath(), "ror2_gameplay.log");
    static void GpLog(string s) { try { File.AppendAllText(GpLogPath, DateTime.Now.ToString("HH:mm:ss ") + "Fishing25: " + s + Environment.NewLine); } catch { } }
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static string N2(float v) { return v.ToString("0.00", Ci); }
    static int gpErrors;

    // ================================================================= odds pipeline
    static readonly string[] TierWhite = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" };
    static readonly string[] TierGreen = { "Bass", "Salmon", "Pufferfish" };
    static readonly string[] TierRed = { "Swordfish", "Golden Koi", "Anglerfish" };
    static readonly string[] TierLunar = { "Moon Eel", "Ghost Fish" };
    public static string GpTier(string sp)
    {
        if (Array.IndexOf(TierLunar, sp) >= 0) return "Lunar";
        if (Array.IndexOf(TierRed, sp) >= 0) return "Tier3";
        if (Array.IndexOf(TierGreen, sp) >= 0) return "Tier2";
        if (Array.IndexOf(TierWhite, sp) >= 0) return "Tier1";
        return "Boot";
    }
    // boot, white, green, red, lunar
    static double[] BaseTable(string where)
    {
        switch (where)
        {
            case "hot": return new[] { 0.0, 0.25, 0.35, 0.30, 0.10 };
            case "hot2": return new[] { 0.0, 0.15, 0.30, 0.40, 0.15 };
            case "pool": return new[] { 0.08, 0.70, 0.25, 0.045, 0.005 };
            default: return new[] { 0.10, 0.60, 0.30, 0.08, 0.02 };
        }
    }

    // product of every modifier for one species (everything except the base table)
    public static double SpeciesMult(string name, string sp, string where, string lure, bool pet, StringBuilder why)
    {
        string tier = GpTier(sp);
        double m = 1;
        if (where == "pool" && (sp == "Catfish" || sp == "Perch")) m *= 3;
        if (name != null) { double r = GpRodMult(name, tier, sp); if (r != 1 && why != null) why.Append(" rod x" + r.ToString("0.##", Ci)); m *= r; }
        if (pet) m *= GpPetMult(tier);
        m *= EventMult(sp, tier);
        LureDef ld; if (lure != null && Lures.TryGetValue(lure, out ld)) m *= ld.Mult(sp, tier);
        if (name != null)
        {
            if (BuffOn(name, "Clownfish") && (tier == "Tier3" || tier == "Lunar")) m *= 1.3;
            if (BuffOn(name, "Catfish") && tier == "Tier2") m *= 1.5;
        }
        m *= ZoneSpeciesMult(name, sp, tier, lure);
        return m;
    }

    public static Dictionary<string, double> OddsTable(string name, string where, string lure, bool pet)
    {
        double[] b = BaseTable(where);
        var w = new Dictionary<string, double>();
        string[][] tiers = { TierWhite, TierGreen, TierRed, TierLunar };
        for (int t = 0; t < 4; t++)
            foreach (string sp in tiers[t]) w[sp] = (1 - b[0]) * b[t + 1] / tiers[t].Length * SpeciesMult(name, sp, where, lure, pet, null);
        // renormalise the fish so the boot chance stays exactly the base boot chance
        double fish = 0; foreach (var kv in w) fish += kv.Value;
        var outp = new Dictionary<string, double>();
        foreach (var kv in w) outp[kv.Key] = fish > 0 ? kv.Value / fish * (1 - b[0]) : 0;
        outp["Old Boot"] = b[0];
        return outp;
    }

    public static string RollSpecies(string name, string where, string lure, bool pet)
    {
        var t = OddsTable(name, where, lure, pet);
        double r = rng.NextDouble(), acc = 0; string last = "Minnow";
        foreach (var kv in t) { acc += kv.Value; last = kv.Key; if (r < acc) return kv.Key; }
        return last;
    }

    public static float BiteMult(string name, string where, string lure)
    {
        float m = 1f;
        if (eventName.Contains("Rain")) m *= 0.7f;
        if (eventName.Contains("Storm")) m *= 0.8f;
        LureDef ld; if (lure != null && Lures.TryGetValue(lure, out ld)) m *= ld.bite;
        if (name != null && BuffOn(name, "Trout")) m *= 0.7f;
        if (where == "pool") m *= 0.9f;
        if (name != null) m *= GpRodBiteMult(name);
        m *= ZoneBiteMult(name, where, lure);
        return m;
    }

    static double EventMult(string sp, string tier)
    {
        double m = 1;
        if (eventName.Contains("Rain")) { if (sp == "Catfish" || sp == "Trout" || sp == "Bass") m *= 2; }
        if (eventName.Contains("Golden hour")) { if (tier == "Tier3" || tier == "Lunar") m *= 1.5; if (sp == "Golden Koi" || sp == "Salmon") m *= 2; }
        if (eventName.Contains("Night")) { if (sp == "Anglerfish" || sp == "Moon Eel" || sp == "Ghost Fish") m *= 3; if (tier == "Tier1") m *= 0.6; }
        if (eventName.Contains("Storm")) { if (tier == "Tier3") m *= 2; if (tier == "Lunar") m *= 2.5; }
        return m;
    }

    // ---- rods / pets (wired by patch_gameplay.py to whatever the rods / pets versions expose; neutral by default)
    static double GpRodMult(string name, string tier, string sp) { return ((tier == "Tier3" || tier == "Lunar") ? 1.0 + LuckBonus(name) + GlowLuck(name) : 1.0); }
    static float GpRodBiteMult(string name) { return 1f; }
    static double GpPetMult(string tier)
    {
        try
        {
            FieldInfo f = typeof(Fishing25).GetField("petTierMult", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) return 1;
            object v = f.GetValue(null);
            float[] fa = v as float[];   // pets agent: { white, green, red, lunar }
            if (fa != null && fa.Length >= 4) { int i = tier == "Lunar" ? 3 : tier == "Tier3" ? 2 : tier == "Tier2" ? 1 : tier == "Tier1" ? 0 : -1; return i < 0 ? 1 : fa[i]; }
            double[] da = v as double[];
            if (da != null && da.Length >= 4) { int i = tier == "Lunar" ? 3 : tier == "Tier3" ? 2 : tier == "Tier2" ? 1 : tier == "Tier1" ? 0 : -1; return i < 0 ? 1 : da[i]; }
            if (v is float) return (tier == "Tier3" || tier == "Lunar") ? (float)v : 1;
            if (v is double) return (tier == "Tier3" || tier == "Lunar") ? (double)v : 1;
            IDictionary d = v as IDictionary;
            if (d != null && d.Contains(tier)) return Convert.ToDouble(d[tier]);
        }
        catch { }
        return 1;
    }

    // school spawn mix follows the events too (Night -> Anglerfish / Moon Eel / Ghost Fish in the school)
    static string GpSchoolRoll(bool inHot) { return RollSpecies(null, inHot ? "hot" : "lake", null, false); }

    public static string GpOddsReport(string name, string where, string lure)
    {
        var t = OddsTable(name, where, lure, false);
        var parts = new List<string>(); foreach (var kv in t) parts.Add(kv.Key + " " + (kv.Value * 100).ToString("0.0", Ci) + "%");
        return where + (lure != null ? "+" + lure : "") + " [" + eventName + "] bite x" + BiteMult(name, where, lure).ToString("0.00", Ci) + ": " + string.Join(", ", parts.ToArray());
    }

    // ================================================================= per-cast context
    static readonly Dictionary<string, string> castWhere = new Dictionary<string, string>();
    static readonly Dictionary<string, string> castLure = new Dictionary<string, string>();
    static readonly Dictionary<string, int> castPool = new Dictionary<string, int>();

    // RigCast hook: the line landed on ground -> inside a pool? returns the pool's surface Y, else NaN
    static float GpPoolLanding(string name, float x, float gy, float z)
    {
        castPool[name] = -1;
        foreach (GpPool p in pools)
        {
            float dx = x - p.x, dz = z - p.z;
            if (dx * dx + dz * dz <= (p.r + 0.5f) * (p.r + 0.5f) && Math.Abs(gy - p.y) < 4f) { castPool[name] = p.id; return p.y + 0.07f; }
        }
        return float.NaN;
    }
    static float GpNoPool(string name) { castPool[name] = -1; return float.NaN; }

    // Press hook, right after SchoolOnCast (valid water cast): location, lure charge, bite time, school re-pick
    static void GpOnCastCore(string name, Angler a)
    {
        try
        {
            Rig r; rigs.TryGetValue(name, out r);
            float flight = r != null ? r.flight : 0.6f, land = now + flight;
            int pool; castPool.TryGetValue(name, out pool);
            bool inHot2 = r != null && hasHot2 && pool < 0 && InHot2(r.tx, r.tz);
            string where = pool >= 0 ? "pool" : inHot2 ? "hot2" : a.hot ? "hot" : "lake";
            castWhere[name] = where;
            string lure = UseLure(name);
            castLure[name] = lure;
            float bm = BiteMult(name, where, lure);
            Hook h = HookOf(name);
            if (where == "pool" || where == "hot2" || zoneTestForce != null)
            {
                SchoolFish f;
                if (h.fishId >= 0 && school.TryGetValue(h.fishId, out f) && f.busy == name) f.busy = null;
                h.fishId = -1; h.emptyAt = -1f; a.hot = false;
                a.biteAt = land + (2f + (float)rng.NextDouble() * 4f) * bm;
                if (where == "pool" && r != null) ChatRaw("[FT] PCAST|" + name + "|" + N2(r.tx) + "|" + N2(r.ty) + "|" + N2(r.tz));
                Chat(where == "pool" ? name + " is fishing in a little pond." : where == "hot2" ? name + "'s line landed in the GOLDEN hotspot!" : name + " casts into the test zone " + zoneTestForce + ".");
            }
            else
            {
                if (h.fishId >= 0) Repick(name, a, h, land, where, lure);
                if (h.fishId >= 0)
                {
                    h.delay *= bm; a.biteAt = land + h.delay;
                    if (BuffOn(name, "Perch")) h.chance = Math.Min(0.98f, h.chance + 0.15f);
                }
                else if (a.biteAt == float.MaxValue)
                {   // empty hook: rain / storm / quick lures turn some empty casts into bites
                    if (bm < 1f && rng.NextDouble() < 1f - bm) { a.biteAt = land + (6f + (float)rng.NextDouble() * 6f) * bm; h.emptyAt = -1f; }
                }
                else a.biteAt = land + (a.biteAt - land) * bm;
            }
            GpLog(name + ": cast where=" + where + " lure=" + (lure ?? "-") + " event=" + eventName + " biteMult=" + bm.ToString("0.00") + " bite in " + (a.biteAt == float.MaxValue ? "never" : (a.biteAt - now).ToString("0.0") + " s")
                  + (h.fishId >= 0 ? " school fish #" + h.fishId + " " + h.species + " chance " + (h.chance * 100).ToString("0") + "%" : ""));
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("on-cast error: " + e); }
    }

    // pick the school fish among those in range, weighted by the pipeline multipliers x closeness
    static void Repick(string name, Angler a, Hook h, float land, string where, string lure)
    {
        float simLand = SimNow + (land - now);
        var cands = new List<SchoolFish>(); var ws = new List<double>(); var ds = new List<float>(); double tot = 0;
        foreach (SchoolFish f in school.Values)
        {
            if ((f.busy != null && f.id != h.fishId) || !SameRegion(f, BobberRegion(h.bx, h.bz, a.hot))) continue;
            float x, z; f.Pos(simLand, out x, out z);
            float d = (float)Math.Sqrt((x - h.bx) * (x - h.bx) + (z - h.bz) * (z - h.bz));
            if (d > InterestRange) continue;
            string sp = FishSchool.Species[f.sp];
            double w = (sp == "Old Boot" ? 1 : SpeciesMultAngler(name, sp, where, lure)) * (1.2 - d / InterestRange);
            cands.Add(f); ws.Add(w); ds.Add(d); tot += w;
        }
        if (cands.Count < 2 || tot <= 0) return;
        double r = rng.NextDouble() * tot; int pick = cands.Count - 1;
        for (int i = 0; i < cands.Count; i++) { r -= ws[i]; if (r < 0) { pick = i; break; } }
        SchoolFish nf = cands[pick];
        if (nf.id == h.fishId) return;
        SchoolFish of; if (school.TryGetValue(h.fishId, out of) && of.busy == name) of.busy = null;
        nf.busy = name;
        float dd = ds[pick];
        GpLog(name + ": re-pick (" + where + (lure != null ? ", " + lure : "") + ", " + eventName + ") #" + h.fishId + " " + h.species + " -> #" + nf.id + " " + FishSchool.Species[nf.sp] + " at " + dd.ToString("0.0") + " m");
        h.fishId = nf.id; h.dist = dd; h.species = FishSchool.Species[nf.sp];
        h.chance = Math.Min(0.98f, Lerp(0.95f, 0.35f, dd / InterestRange) + (a.hot ? 0.10f : 0f));
        h.delay = 1f + 2f * (dd / InterestRange);
        a.biteAt = land + h.delay;
    }

    // Catch hook: species for a catch that is not a school fish
    static string GpRollCore(string name, bool hot)
    {
        string where; if (!castWhere.TryGetValue(name, out where)) where = "lake";
        if (where == "lake" && hot) where = "hot";
        string lure; castLure.TryGetValue(name, out lure);
        string sp = RollSpecies(name, where, lure, false);
        GpLog(name + ": pipeline roll -> " + sp + " (" + GpOddsReport(name, where, lure) + ")");
        return sp;
    }

    // Score hook: value modifiers + golden hotspot prize
    static int GpValue(string name, string species, int value)
    {
        try
        {
            string where; castWhere.TryGetValue(name, out where); castWhere.Remove(name); castLure.Remove(name);
            int v0 = value;
            if (value > 0)
            {
                if (where == "hot2") value = (int)(value * 1.5f);
                if (BuffOn(name, "Bass")) value = (int)(value * 1.5f);
                if (BuffOn(name, "Golden Koi")) { value *= 3; EndBuff(name, "Golden Koi"); Chat(name + "'s Golden Koi belly: this catch is worth x3!"); }
            }
            if (value != v0) GpLog(name + ": value " + v0 + " -> " + value + " (" + species + ", " + (where ?? "lake") + ")");
            if (where == "hot2")
            {
                string t = GpTier(species);
                if (t == "Tier3" || t == "Lunar") { Chat(name + " landed a PRIZE from the golden hotspot! It drifts away..."); PlaceHot2(true, "prize catch"); }
            }
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("value error: " + e); }
        return value;
    }

    // ================================================================= 5: eating fish
    class EatDef { public float secs; public string text; public EatDef(float s, string t) { secs = s; text = t; } }
    static readonly Dictionary<string, EatDef> Eats = new Dictionary<string, EatDef> {
        { "Minnow", new EatDef(10, "speed burst") }, { "Salmon", new EatDef(20, "triple jump") },
        { "Pufferfish", new EatDef(20, "spiky aura (pricks players who get close)") },
        { "Anglerfish", new EatDef(30, "glowing lantern + see the fish school through walls") },
        { "Ghost Fish", new EatDef(15, "ghostly: can't drown") }, { "Golden Koi", new EatDef(120, "next catch x3") },
        { "Moon Eel", new EatDef(60, "instant revive charge") }, { "Swordfish", new EatDef(30, "rod whip knocks players back") },
        { "Perch", new EatDef(60, "steady hands: +15% reel chance") }, { "Trout", new EatDef(60, "quick bites: bites 30% faster") },
        { "Clownfish", new EatDef(60, "lucky: red & lunar fish x1.3") }, { "Catfish", new EatDef(60, "whiskers: green fish x1.5") },
        { "Bass", new EatDef(60, "big haul: catches worth +50%") }, { "Old Boot", new EatDef(0, "nothing. You ate a boot.") } };
    public static readonly Dictionary<string, Dictionary<string, float>> buffUntil = new Dictionary<string, Dictionary<string, float>>();   // name -> species -> `now` it ends
    public static readonly Dictionary<string, int> tempFeathers = new Dictionary<string, int>();
    static readonly Dictionary<string, float> prickReady = new Dictionary<string, float>();

    public static bool BuffOn(string name, string sp)
    {
        Dictionary<string, float> d; float u;
        return name != null && buffUntil.TryGetValue(name, out d) && d.TryGetValue(sp, out u) && now < u;
    }
    static float BuffLeft(string name, string sp)
    {
        Dictionary<string, float> d; float u;
        return name != null && buffUntil.TryGetValue(name, out d) && d.TryGetValue(sp, out u) ? Math.Max(0f, u - now) : 0f;
    }
    static void EndBuff(string name, string sp) { Dictionary<string, float> d; if (buffUntil.TryGetValue(name, out d)) d.Remove(sp); ChatRaw("[FT] EAT|" + name + "|" + sp + "|0"); }

    static string MatchSpecies(string arg)
    {
        string a = Regex.Replace(arg.ToLowerInvariant(), "[^a-z]", "");
        if (a.Length == 0) return null;
        foreach (string sp in Species) if (Regex.Replace(sp.ToLowerInvariant(), "[^a-z]", "") == a) return sp;
        string hit = null;
        foreach (string sp in Species) if (Regex.Replace(sp.ToLowerInvariant(), "[^a-z]", "").Contains(a)) { if (hit != null) return null; hit = sp; }
        return hit;
    }

    // ---- inventory helpers (method names differ between game versions)
    static object ItemIndexOf(string item)
    {
        object idx;
        if (itemIdx.TryGetValue(item, out idx)) return idx;
        idx = ror2.GetType("RoR2.ItemCatalog").GetMethod("FindItemIndex", All).Invoke(null, new object[] { item });
        if (Convert.ToInt32(idx) < 0) return null;
        itemIdx[item] = idx; return idx;
    }
    static MethodInfo InvMethod(object inv, string a, string b, Type p0, Type p1)
    {
        foreach (string n in new[] { a, b })
        {
            if (n == null) continue;
            foreach (MethodInfo m in inv.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != n) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == (p1 == null ? 1 : 2) && ps[0].ParameterType == p0 && (p1 == null || ps[1].ParameterType == p1)) return m;
            }
        }
        return null;
    }
    static int InvCount(object inv, object idx) { MethodInfo m = InvMethod(inv, "GetItemCountPermanent", "GetItemCount", idx.GetType(), null); return m == null ? 0 : Convert.ToInt32(m.Invoke(inv, new[] { idx })); }
    static void InvRemove(object inv, object idx, int n) { MethodInfo m = InvMethod(inv, "RemoveItemPermanent", "RemoveItem", idx.GetType(), typeof(int)); if (m != null) m.Invoke(inv, new object[] { idx, n }); }
    static void InvGive(object inv, object idx, int n) { MethodInfo m = InvMethod(inv, "GiveItemPermanent", "GiveItem", idx.GetType(), typeof(int)); if (m != null) m.Invoke(inv, new object[] { idx, n }); }
    static object InvOf(string name) { object inv = Get(Get(FindUser(name), "master"), "inventory"); return IsNull(inv) ? null : inv; }

    static void TimedBuff(object body, float secs, params string[] names)
    {
        foreach (string bn in names)
        {
            RoR2.BuffIndex bi = RoR2.BuffCatalog.FindBuffIndex(bn);
            if ((int)bi < 0) { GpLog("buff " + bn + " not found"); continue; }
            ((RoR2.CharacterBody)body).AddTimedBuff(bi, secs);
            GpLog("timed buff " + bn + " (" + secs + " s) on " + ((RoR2.CharacterBody)body).name);
            return;
        }
    }

    public static string Eat(string name, string arg, bool dryRun)
    {
        string sp = MatchSpecies(arg ?? "");
        if (sp == null) { Chat("!eat what? Try e.g. !eat minnow (" + name + ")"); return "unknown"; }
        object inv = InvOf(name);
        string carrier; fishCarrierItem.TryGetValue(sp, out carrier);
        object idx = carrier == null ? null : ItemIndexOf(carrier);
        int have = inv == null || idx == null ? 0 : InvCount(inv, idx);
        if (have <= 0) { if (!dryRun) Chat(name + " has no " + sp + " to eat!"); GpLog(name + ": eat " + sp + " refused (carrier " + carrier + " x0)"); return "none"; }
        EatDef ed = Eats[sp];
        if (dryRun) { GpLog("EAT DRY-RUN " + name + ": would eat " + sp + " (carrier " + carrier + " x" + have + ") -> " + ed.text + " " + ed.secs + " s"); return "dry"; }
        object body = Body(name);
        InvRemove(inv, idx, 1);
        Dictionary<string, int> c;
        if (collections.TryGetValue(name, out c)) { int n; if (c.TryGetValue(sp, out n)) { if (n <= 1) c.Remove(sp); else c[sp] = n - 1; } }
        Coll(name);
        if (ed.secs > 0)
        {
            Dictionary<string, float> d; if (!buffUntil.TryGetValue(name, out d)) { d = new Dictionary<string, float>(); buffUntil[name] = d; }
            d[sp] = now + ed.secs;
        }
        try
        {
            if (sp == "Minnow" && body != null) TimedBuff(body, ed.secs, "CloakSpeed", "WhipBoost", "WarCryBuff", "BugWings");
            if (sp == "Salmon")
            {   // +2 jumps for 20 s: two temporary Hopoo Feathers (removed when the buff ends or the stage changes)
                object fi = ItemIndexOf("Feather");
                if (fi != null) { InvGive(inv, fi, 2); int t; tempFeathers.TryGetValue(name, out t); tempFeathers[name] = t + 2; }
            }
        }
        catch (Exception e) { GpLog("eat effect error: " + e); }
        ChatRaw("[FT] EAT|" + name + "|" + sp + "|" + ((int)ed.secs).ToString(Ci));
        GameplayFx25.SetBuff(name, sp, ed.secs);
        Chat(name + " ate a " + sp + "! " + (ed.secs > 0 ? char.ToUpper(ed.text[0]) + ed.text.Substring(1) + " for " + ed.secs + " s." : "Nothing happens. It was a boot."));
        GpLog(name + ": ATE " + sp + " (carrier " + carrier + " " + have + " -> " + (have - 1) + ") -> " + ed.text + " " + ed.secs + " s");
        return sp;
    }

    // ================================================================= 12: lures
    class LureDef
    {
        public string name, text; public int price, uses; public float bite = 1f; public string[] maps;
        public Dictionary<string, double> sp = new Dictionary<string, double>(), tier = new Dictionary<string, double>();
        public double Mult(string s, string t)
        {
            if (maps != null && Array.IndexOf(maps, curScene) < 0) return 1;
            double m = 1, v;
            if (sp.TryGetValue(s, out v)) m *= v;
            if (tier.TryGetValue(t, out v)) m *= v;
            return m;
        }
    }
    static LureDef MkLure(string n, int price, int uses, string text, string spm, string tierm, float bite, string maps)
    {
        var d = new LureDef { name = n, price = price, uses = uses, text = text, bite = bite, maps = maps == null ? null : maps.Split(new[] { ',' }) };
        if (spm != null) foreach (string p in spm.Split(new[] { ';' })) { string[] kv = p.Split(new[] { '=' }); d.sp[kv[0]] = double.Parse(kv[1], Ci); }
        if (tierm != null) foreach (string p in tierm.Split(new[] { ';' })) { string[] kv = p.Split(new[] { '=' }); d.tier[kv[0]] = double.Parse(kv[1], Ci); }
        return d;
    }
    static Dictionary<string, LureDef> lureDefs;
    static Dictionary<string, LureDef> Lures { get { if (lureDefs == null) lureOrder = InitLures(); return lureDefs; } }
    static string[] InitLures()
    {
        var l = new[] {
            MkLure("Spinner", 300, 20, "Trout & Perch x2.5", "Trout=2.5;Perch=2.5", null, 1f, null),
            MkLure("Glow Lure", 800, 20, "night fish (Anglerfish, Moon Eel, Ghost Fish) x2", "Anglerfish=2;Moon Eel=2;Ghost Fish=2", null, 1f, null),
            MkLure("Deep Jig", 1500, 20, "red fish x1.5, bites 50% slower", null, "Tier3=1.5", 1.5f, null),
            MkLure("Golden Fly", 3000, 15, "Golden Koi x3", "Golden Koi=3", null, 1f, null),
            MkLure("Feather Lure", 600, 20, "Sky Meadow: Ghost Fish, Salmon, Minnow x2", "Ghost Fish=2;Salmon=2;Minnow=2", null, 1f, "skymeadow"),
            MkLure("Coral Lure", 600, 20, "coast maps (Distant Roost, Gilded Coast, Siren's Call): Clownfish x2.5, Swordfish x2", "Clownfish=2.5;Swordfish=2", null, 1f, "blackbeach,blackbeach2,goldshores,shipgraveyard"),
            MkLure("Frost Lure", 600, 20, "snow maps (Rallypoint Delta, Siphoned Forest): Trout, Salmon x2, Moon Eel x1.5", "Trout=2;Salmon=2;Moon Eel=1.5", null, 1f, "frozenwall,snowyforest"),
            MkLure("Swamp Lure", 600, 20, "Titanic Plains, Wetland Aspect, Scorched Acres: Catfish x2.5, Bass x2, Perch x1.5", "Catfish=2.5;Bass=2;Perch=1.5", null, 1f, "golemplains,golemplains2,foggyswamp,wispgraveyard"),
            MkLure("Sand Lure", 600, 20, "Abandoned Aqueduct, Aphelian Sanctuary: Pufferfish x2.5, Catfish x2, Anglerfish x1.5", "Pufferfish=2.5;Catfish=2;Anglerfish=1.5", null, 1f, "goolake,ancientloft") };
        var order = new List<string>(); var defs = new Dictionary<string, LureDef>();
        foreach (var d in l) { defs[d.name] = d; order.Add(d.name); }
        lureDefs = defs;
        return order.ToArray();
    }
    static string[] lureOrder;
    static string[] LureNames { get { if (lureOrder == null) { var x = Lures; } return lureOrder; } }
    public static readonly Dictionary<string, Dictionary<string, int>> lureUses = new Dictionary<string, Dictionary<string, int>>();
    public static readonly Dictionary<string, string> lureEquipped = new Dictionary<string, string>();
    static string curScene = "";

    static string MatchLure(string arg)
    {
        string a = Regex.Replace((arg ?? "").ToLowerInvariant(), "[^a-z]", "");
        if (a.Length == 0) return null;
        string hit = null;
        foreach (string n in LureNames) { string k = Regex.Replace(n.ToLowerInvariant(), "[^a-z]", ""); if (k == a) return n; if (k.StartsWith(a) || k.Replace("lure", "") == a) hit = hit == null ? n : "?"; }
        return hit == "?" ? null : hit;
    }
    static int UsesOf(string name, string lure) { Dictionary<string, int> d; int n; return lureUses.TryGetValue(name, out d) && d.TryGetValue(lure, out n) ? n : 0; }

    static string UseLure(string name)
    {
        string l; if (!lureEquipped.TryGetValue(name, out l) || l == null) return null;
        LureDef d; if (!Lures.TryGetValue(l, out d)) return null;
        if (d.maps != null && Array.IndexOf(d.maps, curScene) < 0) return null;   // map lure off its map: no effect, no charge used
        int n = UsesOf(name, l);
        if (n <= 0) { lureEquipped.Remove(name); return null; }
        lureUses[name][l] = n - 1;
        if (n - 1 <= 0) { lureEquipped.Remove(name); Chat(name + "'s " + l + " is used up (last cast)."); }
        BroadcastLure(name);
        return l;
    }

    static void LureCmd(string name, string cmd, string arg)
    {
        if (cmd == "!lures")
        {
            var sb = new StringBuilder("Lures (fishing money, limited casts): ");
            for (int i = 0; i < LureNames.Length; i++) { LureDef d = Lures[LureNames[i]]; sb.Append(d.name + " $" + d.price.ToString("#,0", Ci) + " (" + d.uses + " casts) " + d.text + (i < LureNames.Length - 1 ? " | " : "")); }
            Chat(sb.ToString());
            var mine = new List<string>(); Dictionary<string, int> u;
            if (lureUses.TryGetValue(name, out u)) foreach (var kv in u) if (kv.Value > 0) mine.Add(kv.Key + " x" + kv.Value);
            string eq; lureEquipped.TryGetValue(name, out eq);
            Chat(name + ": " + (mine.Count > 0 ? "your lures " + string.Join(", ", mine.ToArray()) : "no lures yet") + (eq != null ? ", equipped " + eq : "") + ". !buylure <name>, !lure <name> / !lure off.");
            return;
        }
        if (cmd == "!lure" && Regex.Replace(arg.ToLowerInvariant(), "[^a-z]", "") == "off") { lureEquipped.Remove(name); Chat(name + " took the lure off."); BroadcastLure(name); return; }
        string l = MatchLure(arg);
        if (l == null) { Chat("Unknown lure '" + arg + "'. Type !lures for the list."); return; }
        LureDef ld = Lures[l];
        if (cmd == "!buylure")
        {
            int m; money.TryGetValue(name, out m);
            if (m < ld.price) { Chat(name + " needs $" + ld.price.ToString("#,0", Ci) + " for a " + l + " (has $" + m.ToString("#,0", Ci) + ")."); return; }
            money[name] = m - ld.price;
            Dictionary<string, int> u; if (!lureUses.TryGetValue(name, out u)) { u = new Dictionary<string, int>(); lureUses[name] = u; }
            int have; u.TryGetValue(l, out have); u[l] = have + ld.uses;
            if (!lureEquipped.ContainsKey(name)) lureEquipped[name] = l;
            Chat(name + " bought a " + l + " (" + ld.uses + " casts, " + ld.text + ") for $" + ld.price.ToString("#,0", Ci) + "." + (lureEquipped[name] == l ? " Equipped!" : " Equip it with !lure " + l.ToLowerInvariant()));
            GpLog(name + ": bought " + l + " for $" + ld.price + " (money " + m + " -> " + money[name] + ")");
            Board(); BroadcastLure(name);
            return;
        }
        if (UsesOf(name, l) <= 0) { Chat(name + " doesn't have a " + l + ". Buy one: !buylure " + l.ToLowerInvariant() + " ($" + ld.price.ToString("#,0", Ci) + ")"); return; }
        lureEquipped[name] = l;
        Chat(name + " equipped the " + l + " (" + UsesOf(name, l) + " casts left): " + ld.text + (ld.maps != null && Array.IndexOf(ld.maps, curScene) < 0 ? " - not this map, so it does nothing here" : ""));
        BroadcastLure(name);
    }
    static void BroadcastLure(string name)
    {
        string l; lureEquipped.TryGetValue(name, out l);
        ChatRaw("[FT] LURE|" + name + "|" + (l ?? "none") + "|" + (l == null ? 0 : UsesOf(name, l)));
    }

    // ================================================================= chat commands (HandleChatLine hook)
    static bool GpCommand(string name, string rawText)
    {
        string t = rawText.Trim(); string low = t.ToLowerInvariant();
        int sp = low.IndexOf(' ');
        string cmd = sp < 0 ? low : low.Substring(0, sp), arg = sp < 0 ? "" : t.Substring(sp + 1).Trim();
        if (cmd != "!eat" && cmd != "!lures" && cmd != "!buylure" && cmd != "!lure" && cmd != "!event" && cmd != "!weather" && cmd != "!odds") return false;
        if (FindUser(name) == null) return true;
        try
        {
            if (cmd == "!eat") Eat(name, arg, false);
            else if (cmd == "!event" || cmd == "!weather") Chat("Fishing event: " + eventName + " (" + Fmt(EventSecondsLeft()) + " left). " + EventText());
            else if (cmd == "!odds") { string l; lureEquipped.TryGetValue(name, out l); Chat(GpOddsReport(name, "lake", l)); }
            else LureCmd(name, cmd, arg);
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("command error: " + e); }
        return true;
    }

    // ================================================================= 6: weather / time of day
    public static string weather = "Clear", eventName = "Clear";
    public static float weatherUntil = -1f, ownHour = 9f;
    static float lastHourAt = -1f, eventBroadcastAt = -100f, nextBolt;
    static Type dayNightType; static float dayNightLookAt = -100f;
    static object boltFx; static bool boltFxTried;

    public static float Hour()
    {
        if (hourOverride >= 0f) return hourOverride;
        if (now - dayNightLookAt > 10f)
        {
            dayNightLookAt = now; Type best = null; int bn = -1;
            foreach (var asm in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(asm)) { Match m = Regex.Match(t.Name, "^DayNight(\\d+)$"); if (m.Success && int.Parse(m.Groups[1].Value) > bn) { bn = int.Parse(m.Groups[1].Value); best = t; } }
            if (best != dayNightType) { dayNightType = best; GpLog("time of day source: " + (best == null ? "own clock (10 min/day)" : best.Name + ".hour")); }
        }
        if (dayNightType != null)
        {
            try
            {
                FieldInfo f = dayNightType.GetField("hour", BindingFlags.Public | BindingFlags.Static);
                object v = f != null ? f.GetValue(null) : dayNightType.GetProperty("hour", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                FieldInfo en = dayNightType.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (en == null || (bool)en.GetValue(null)) return Convert.ToSingle(v);
            }
            catch { }
        }
        return ownHour;
    }
    static string TimeEvent(float h) { if (h < 5f || h >= 20f) return "Night"; if (h < 7f || h >= 17.5f) return "Golden hour"; return ""; }
    static float SecsToBoundary(float h)
    {
        float best = 24f;
        foreach (float b in new[] { 5f, 7f, 17.5f, 20f }) { float d = (b - h + 24f) % 24f; if (d > 0.001f && d < best) best = d; }
        return best * 600f / 24f;
    }
    static float EventSecondsLeft()
    {
        float h = Hour(), t = SecsToBoundary(h);
        if (weather != "Clear") t = Math.Min(t, Math.Max(0f, weatherUntil - now));
        return t;
    }
    static string Fmt(float s) { int i = (int)s; return (i / 60) + ":" + (i % 60).ToString("00"); }
    static string EventText()
    {
        var p = new List<string>();
        if (eventName.Contains("Storm")) p.Add("Storm: red x2, lunar x2.5, bites faster - watch the lightning!");
        if (eventName.Contains("Rain")) p.Add("Rain: more bites, Catfish/Trout/Bass x2");
        if (eventName.Contains("Golden hour")) p.Add("Golden hour: rare fish x1.5, Golden Koi & Salmon x2");
        if (eventName.Contains("Night")) p.Add("Night: Anglerfish, Moon Eel, Ghost Fish x3, fewer white fish");
        return p.Count == 0 ? "Clear skies: normal odds." : string.Join(" ", p.ToArray());
    }

    static void EventTick()   // every 0.5 s
    {
        if (lastHourAt < 0) lastHourAt = now;
        ownHour = (ownHour + (now - lastHourAt) * 24f / 600f) % 24f; lastHourAt = now;
        if (now >= weatherUntil)
        {
            double r = rng.NextDouble();
            string nw = r < 0.10 ? "Storm" : r < 0.40 ? "Rain" : "Clear";
            if (weatherUntil < 0) nw = "Clear";   // first event after install: start clear
            weather = nw; weatherUntil = now + 240f + (float)rng.NextDouble() * 120f;
            GpLog("weather -> " + weather + " for " + (weatherUntil - now).ToString("0") + " s");
        }
        string te = TimeEvent(Hour());
        var parts = new List<string>(); if (te.Length > 0) parts.Add(te); if (weather != "Clear") parts.Add(weather);
        string name = parts.Count == 0 ? "Clear" : string.Join("+", parts.ToArray());
        if (name != eventName)
        {
            string old = eventName; eventName = name;
            GpLog("EVENT " + old + " -> " + name + " (hour " + Hour().ToString("0.0") + ")");
            Chat((name == "Clear" ? "The weather clears up. " : "Fishing event: " + name.Replace("+", " + ") + "! ") + EventText());
            BroadcastEvent();
        }
        else if (now - eventBroadcastAt >= 20f) BroadcastEvent();
        GameplayFx25.SetEvent(eventName);
        if (eventName.Contains("Storm") && now >= nextBolt) { nextBolt = now + 5f + (float)rng.NextDouble() * 7f; Lightning(); }
    }
    static void BroadcastEvent() { eventBroadcastAt = now; ChatRaw("[FT] EVENT|" + eventName + "|" + ((int)EventSecondsLeft()).ToString(Ci)); }

    static void Lightning()
    {
        try
        {
            if (!hasWater || IsNull(water)) return;
            var feet = LivingFeet(); if (feet.Count == 0) return;
            for (int i = 0; i < 20; i++)
            {
                UnityEngine.Vector3 f = feet[rng.Next(feet.Count)];
                double a = rng.NextDouble() * Math.PI * 2, d = 25 + rng.NextDouble() * 45;
                float x = f.x + (float)(Math.Cos(a) * d), z = f.z + (float)(Math.Sin(a) * d), gh;
                if (!OpenWater(x, z, out gh)) continue;
                var p = new UnityEngine.Vector3(x, waterY, z);
                if (!boltFxTried)
                {
                    boltFxTried = true;
                    foreach (string path in new[] { "Prefabs/Effects/ImpactEffects/LightningStrikeImpact", "Prefabs/Effects/ImpactEffects/SimpleLightningStrikeImpact", "Prefabs/Effects/LightningStakeNova", "Prefabs/Effects/ImpactEffects/LightningImpact" })
                    { boltFx = LoadPrefab(path); GpLog("bolt fx " + path + " -> " + (boltFx != null)); if (boltFx != null) break; }
                }
                if (boltFx != null)
                {   // networked effect (sound + flash on every machine)
                    object ed = Activator.CreateInstance(ror2.GetType("RoR2.EffectData"));
                    Set(ed, "origin", p); Set(ed, "scale", 4f);
                    foreach (MethodInfo m in ror2.GetType("RoR2.EffectManager").GetMethods(All))
                        if (m.Name == "SpawnEffect" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "GameObject") { m.Invoke(null, new object[] { boltFx, ed, true }); break; }
                }
                GameplayFx25.LightningBolt(p);
                ChatRaw("[FT] BOLT|" + N2(x) + "|" + N2(waterY) + "|" + N2(z));
                return;
            }
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("lightning error: " + e); }
    }

    // ================================================================= 10: golden far hotspot
    public static UnityEngine.Vector3 hot2Pos;
    public static float hot2Radius = 8f;
    public static bool hasHot2;
    static string hot2Scene;
    static float hot2MovedAt = -1000f, hot2TryAt = -100f, hot2BroadcastAt = -100f;

    static bool InHot2(float x, float z) { float dx = x - hot2Pos.x, dz = z - hot2Pos.z; return dx * dx + dz * dz <= hot2Radius * hot2Radius; }

    static bool NearLand(float x, float z, float maxDist)
    {
        foreach (var f in LivingFeet()) if (f.y > waterY + 0.5f) { float dx = f.x - x, dz = f.z - z; if (dx * dx + dz * dz <= maxDist * maxDist) return true; }
        float gh;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            for (float d = 20f; d <= maxDist; d += 20f)
                if (!OpenWater(x + (float)(Math.Cos(a) * d), z + (float)(Math.Sin(a) * d), out gh)) return true;
        }
        return false;
    }

    static void PlaceHot2(bool moving, string why)
    {
        hot2TryAt = now;
        if (!hasHotspot || !hasWater) return;
        UnityEngine.Vector3 c = hotspotPos;
        for (int i = 0; i < 150; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2, d = 150 + rng.NextDouble() * 150;
            float x = c.x + (float)(Math.Cos(a) * d), z = c.z + (float)(Math.Sin(a) * d), gh;
            if (!OpenWater(x, z, out gh)) continue;
            bool ok = true;
            for (int k = 0; k < 4 && ok; k++) ok = OpenWater(x + (float)Math.Cos(k * 1.5708) * hot2Radius, z + (float)Math.Sin(k * 1.5708) * hot2Radius, out gh);
            if (!ok || !NearLand(x, z, 150f)) continue;
            if (moving && hasHot2) { float ox = x - hot2Pos.x, oz = z - hot2Pos.z; if (ox * ox + oz * oz < 60f * 60f) continue; }
            hot2Pos = new UnityEngine.Vector3(x, waterY, z); hasHot2 = true; hot2Scene = SceneName(); hot2MovedAt = now;
            GpLog("HOT2 " + (moving ? "moved" : "placed") + " (" + why + ") at " + V(hot2Pos) + " r=" + hot2Radius + ", " + d.ToString("0") + " m from the purple hotspot (try " + (i + 1) + ")");
            Chat("A GOLDEN HOTSPOT " + (moving ? "moved" : "appeared") + " far out on the water (" + d.ToString("0") + " m from the purple one) - the best fish in the lake! Find it by its gold sparkles, or by the gold needle of a Compass when the sparkles are hidden.");
            BroadcastHot2();
            return;
        }
        GpLog("HOT2: no open water 150-300 m from the hotspot within 150 m of land (" + why + ")");
    }
    static void BroadcastHot2() { hot2BroadcastAt = now; ChatRaw("[FT] HOT2|" + N2(hot2Pos.x) + "|" + N2(hot2Pos.y) + "|" + N2(hot2Pos.z) + "|" + N2(hot2Radius)); }

    static void Hot2Tick(string scene)
    {
        if (hot2Scene != scene) hasHot2 = false;
        bool water0 = hasWater && !IsNull(water) && waterScene == scene;
        if (!water0) return;
        if (!hasHot2) { if (hasHotspot && now - hot2TryAt > 5f) PlaceHot2(false, "new stage"); return; }
        if (Math.Abs(hot2Pos.y - waterY) > 0.01f) { hot2Pos.y = waterY; BroadcastHot2(); }
        if (now - hot2MovedAt >= 240f) PlaceHot2(true, "4 minutes");
        else if (now - hot2BroadcastAt >= 20f) BroadcastHot2();
    }

    // ================================================================= 11: small local pools
    public class GpPool { public int id; public float x, y, z, r; }
    public static readonly List<GpPool> pools = new List<GpPool>();
    static string poolScene; static int poolTries; static float poolTryAt = -100f, poolBroadcastAt = -100f; static int poolSeq;

    static void PlacePools()
    {
        poolTryAt = now; poolTries++;
        var feet = LivingFeet(); if (feet.Count == 0) return;
        int want = 2 + rng.Next(4), mask = WorldMask(), tried = 0;
        var found = new List<GpPool>();
        for (int i = 0; i < 500 && found.Count < want; i++)
        {
            tried++;
            UnityEngine.Vector3 f = feet[rng.Next(feet.Count)];
            double a = rng.NextDouble() * Math.PI * 2, d = 30 + rng.NextDouble() * 220;
            float x = f.x + (float)(Math.Cos(a) * d), z = f.z + (float)(Math.Sin(a) * d);
            UnityEngine.RaycastHit h;
            if (!UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, f.y + 150f, z), UnityEngine.Vector3.down, out h, 450f, mask, UnityEngine.QueryTriggerInteraction.Ignore)) continue;
            if (h.normal.y < 0.96f || h.point.y < waterY + 4f) continue;
            float y = h.point.y, r = 3f + (float)rng.NextDouble() * 2.5f;
            bool ok = true;
            for (int k = 0; k < 8 && ok; k++)
            {
                double b = k * Math.PI / 4;
                UnityEngine.RaycastHit h2;
                var o = new UnityEngine.Vector3(x + (float)(Math.Cos(b) * r * 1.15), y + 6f, z + (float)(Math.Sin(b) * r * 1.15));
                ok = UnityEngine.Physics.Raycast(o, UnityEngine.Vector3.down, out h2, 12f, mask, UnityEngine.QueryTriggerInteraction.Ignore) && Math.Abs(h2.point.y - y) < 0.4f && h2.distance > 5f;
            }
            if (!ok) continue;
            if (UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, y + 0.5f, z), UnityEngine.Vector3.up, 30f, mask, UnityEngine.QueryTriggerInteraction.Ignore)) continue;   // under a roof / tree
            float gh;   // away from the lake: land all around at 25 m
            for (int k = 0; k < 8 && ok; k++) { double b = k * Math.PI / 4; ok = !OpenWater(x + (float)(Math.Cos(b) * 25), z + (float)(Math.Sin(b) * 25), out gh); }
            if (!ok) continue;
            foreach (var p in found) { float dx = p.x - x, dz = p.z - z; if (dx * dx + dz * dz < 40f * 40f) { ok = false; break; } }
            if (!ok) continue;
            found.Add(new GpPool { id = ++poolSeq, x = x, y = y, z = z, r = r });
        }
        GpLog("pools on " + SceneName() + ": " + found.Count + "/" + want + " placed after " + tried + " samples (try " + poolTries + ")");
        if (found.Count == 0) return;
        pools.Clear(); pools.AddRange(found); poolScene = SceneName();
        GameplayFx25.ClearPools();
        foreach (var p in pools) { GameplayFx25.SetPool(p.id, new UnityEngine.Vector3(p.x, p.y, p.z), p.r); GpLog("  pool #" + p.id + " at (" + p.x.ToString("0.0") + ", " + p.y.ToString("0.0") + ", " + p.z.ToString("0.0") + ") r=" + p.r.ToString("0.0")); }
        Chat(pools.Count + " small fishing ponds appeared around the map - they have their own fish (lots of Catfish and Perch) and you can't drown in them.");
        BroadcastPools();
    }
    static void BroadcastPools()
    {
        poolBroadcastAt = now;
        foreach (var p in pools) ChatRaw("[FT] POOL|" + p.id + "|" + N2(p.x) + "|" + N2(p.y) + "|" + N2(p.z) + "|" + N2(p.r));
    }
    static void PoolTick(string scene)
    {
        if (poolScene != null && poolScene != scene) { pools.Clear(); poolScene = null; poolTries = 0; GameplayFx25.ClearPools(); }
        if (!hasWater || IsNull(water) || waterScene != scene) return;
        if (poolScene == null)
        {
            if (poolTries >= 6) { poolScene = scene; GpLog("pools: giving up on " + scene); return; }
            if (now - poolTryAt > 8f && now - waterPlacedAt > 3f) PlacePools();
            return;
        }
        if (pools.Count > 0 && now - poolBroadcastAt >= 30f) BroadcastPools();
        // host visuals survive a version bump via SetPool (no-op when unchanged)
        foreach (var p in pools) GameplayFx25.SetPool(p.id, new UnityEngine.Vector3(p.x, p.y, p.z), p.r);
    }

    // ================================================================= server-side buff effects
    static readonly Dictionary<string, float> deadSince = new Dictionary<string, float>();
    static readonly Dictionary<string, UnityEngine.Vector3> lastSafe = new Dictionary<string, UnityEngine.Vector3>();
    static readonly Dictionary<string, int> lastExtraLife = new Dictionary<string, int>();
    static readonly Dictionary<string, bool> dioAtDeath = new Dictionary<string, bool>();
    static object lastStageObj;

    static void BuffTick()   // every 0.1 s
    {
        // stage changed: drop the temporary Salmon feathers BEFORE the item add-on's stage strip converts them
        object st = GetStatic(ror2.GetType("RoR2.Stage"), "instance");
        if (!ReferenceEquals(st, lastStageObj)) { lastStageObj = st; foreach (string n in new List<string>(tempFeathers.Keys)) DropFeathers(n, "stage change"); }
        foreach (string name in new List<string>(buffUntil.Keys))
        {
            Dictionary<string, float> d = buffUntil[name];
            foreach (string sp in new List<string>(d.Keys))
                if (now >= d[sp])
                {
                    d.Remove(sp);
                    if (sp == "Salmon") DropFeathers(name, "buff ended");
                    GpLog(name + ": " + sp + " buff ended");
                    if (sp == "Golden Koi") Chat(name + "'s Golden Koi bonus wore off.");
                }
            if (BuffOn(name, "Pufferfish")) Prick(name);
            ReviveWatch(name);
        }
        foreach (string n in new List<string>(tempFeathers.Keys)) if (!BuffOn(n, "Salmon")) DropFeathers(n, "no Salmon buff");
    }

    static void DropFeathers(string name, string why)
    {
        int n; if (!tempFeathers.TryGetValue(name, out n)) return;
        tempFeathers.Remove(name);
        try
        {
            object inv = InvOf(name), fi = ItemIndexOf("Feather");
            if (inv == null || fi == null) return;
            int have = InvCount(inv, fi), take = Math.Min(have, n);
            if (take > 0) InvRemove(inv, fi, take);
            GpLog(name + ": removed " + take + " temporary Feather(s) (" + why + ")");
        }
        catch (Exception e) { GpLog("feather removal error: " + e.Message); }
    }

    static void Prick(string name)
    {
        object b = Body(name); if (!Alive(b)) return;
        var c = (UnityEngine.Vector3)Get(b, "corePosition");
        foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
        {
            string n = Convert.ToString(Get(nu, "userName")); if (n == name) continue;
            object tb = Body(n); if (!Alive(tb)) continue;
            if (((UnityEngine.Vector3)Get(tb, "corePosition") - c).magnitude > 3f) continue;
            float ready; if (prickReady.TryGetValue(n, out ready) && now < ready) continue;
            prickReady[n] = now + 2f;
            string res = ApplyWhipDamage(b, tb);
            HitFx((UnityEngine.Vector3)Get(tb, "corePosition"));
            GpLog(n + " pricked by " + name + "'s Pufferfish spikes: " + res);
        }
    }

    // Moon Eel: dying while the charge lasts -> back on your feet after 1 s at the last dry spot
    static void ReviveWatch(string name)
    {
        object body = Body(name);
        bool alive = Alive(body);
        object inv = InvOf(name), el = ItemIndexOf("ExtraLife");
        int extra = inv == null || el == null ? 0 : InvCount(inv, el);
        if (alive)
        {
            deadSince.Remove(name);
            var foot = (UnityEngine.Vector3)Get(body, "footPosition");
            if (!hasWater || foot.y > waterY + 0.5f) lastSafe[name] = foot;
            lastExtraLife[name] = extra;
            return;
        }
        if (!BuffOn(name, "Moon Eel")) { deadSince.Remove(name); return; }
        float since;
        if (!deadSince.TryGetValue(name, out since))
        {
            deadSince[name] = now; int before; lastExtraLife.TryGetValue(name, out before);
            dioAtDeath[name] = extra < before;   // a held Moon Eel (Dio) is reviving them already
            GpLog(name + " died with a Moon Eel charge (" + (dioAtDeath[name] ? "Dio revive in progress - charge kept" : "reviving in 1 s") + ")");
            return;
        }
        bool dio; dioAtDeath.TryGetValue(name, out dio);
        if (dio || now - since < 1f) return;
        UnityEngine.Vector3 spot;
        if (!lastSafe.TryGetValue(name, out spot)) return;
        try
        {
            var master = (RoR2.CharacterMaster)Get(FindUser(name), "master");
            if (master == null) return;
            master.Respawn(spot + UnityEngine.Vector3.up * 0.5f, UnityEngine.Quaternion.identity);
            EndBuff(name, "Moon Eel"); deadSince.Remove(name);
            Chat(name + " was revived by the Moon Eel they ate!");
            GpLog(name + ": MOON EEL REVIVE at " + V(spot));
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("revive error: " + e); }
    }

    // DrownTick hook: Ghost Fish = no drowning
    static bool GpNoDrown(string name) { return BuffOn(name, "Ghost Fish"); }

    // ApplyWhipDamage hook: Swordfish = the whip knocks the target back
    static object GpWhipForce(object attackerBody, object targetBody)
    {
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                string n = Convert.ToString(Get(nu, "userName"));
                if (!ReferenceEquals(Body(n), attackerBody) || !BuffOn(n, "Swordfish")) continue;
                var d = (UnityEngine.Vector3)Get(targetBody, "corePosition") - (UnityEngine.Vector3)Get(attackerBody, "corePosition");
                d.y = 0; if (d.sqrMagnitude < 0.01f) d = UnityEngine.Vector3.forward; d.Normalize();
                var f = d * 2400f + UnityEngine.Vector3.up * 1200f;
                GpLog(n + "'s Swordfish whip: knockback force " + V(f));
                return f;
            }
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("whip force error: " + e.Message); }
        return UnityEngine.Vector3.zero;
    }

    // ================================================================= ticks / render / HUD / carry-over
    static void GameplayTick()   // every 0.5 s (main thread)
    {
        try
        {
            string scene = SceneName(); curScene = scene;
            EventTick();
            Hot2Tick(scene);
            PoolTick(scene);
            ZoneTick(scene);
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("tick error: " + e); }
    }
    static void GameplayFast()   // every 0.02 s tick; work every 5th
    {
        if (tickNo % 5 != 0) return;
        try { BuffTick(); } catch (Exception e) { if (gpErrors++ < 20) GpLog("buff tick error: " + e); }
    }
    static bool fxInit;
    static void GpRender()   // every frame
    {
        try
        {
            if (!fxInit)
            {
                fxInit = true; GameplayFx25.Log = s => GpLog("fx: " + s);
                GameplayFx25.BodyTf = n => { object b = Body(n); return b == null ? null : ((UnityEngine.Component)b).transform; };
            }
            GameplayFx25.localPlayer = hudLocalName;
            GameplayFx25.SetHot2(hasHot2 && hot2Scene == curScene && hasWater, hot2Pos, hot2Radius);
            GameplayFx25.Frame(hasWater && waterScene == curScene && !IsNull(water), waterY);
        }
        catch (Exception e) { if (gpErrors++ < 20) GpLog("render error: " + e); }
    }

    // host panel lines (FishHud): event, lure, active buffs. Returns the number of lines added.
    public static int GameplayHud(StringBuilder sb, string me)
    {
        int n = 0;
        try
        {
            string col = eventName.Contains("Storm") ? "#9fb4ff" : eventName.Contains("Rain") ? "#7fc8ff" : eventName.Contains("Night") ? "#b59cff" : eventName.Contains("Golden") ? "#ffcc55" : "#aaddaa";
            sb.Append("Event: <color=" + col + ">" + eventName.Replace("+", " + ") + "</color> <color=#aaaaaa>" + Fmt(EventSecondsLeft()) + "</color>\n"); n++;
            string l; lureEquipped.TryGetValue(me, out l);
            sb.Append("Lure: " + (l != null ? "<color=#ffd700>" + l + "</color> (" + UsesOf(me, l) + " casts)" : "<color=#aaaaaa>none (!lures)</color>") + "\n"); n++;
            n += ZoneHud(sb, me);
            Dictionary<string, float> d;
            if (buffUntil.TryGetValue(me, out d))
                foreach (var kv in d)
                    if (now < kv.Value) { sb.Append("<color=#7fff7f>Ate " + kv.Key + ": " + Eats[kv.Key].text + " " + Fmt(kv.Value - now) + "</color>\n"); n++; }
        }
        catch { }
        return n;
    }

    // carry-over to the next Fishing<N> (managed data only)
    public static string GameplaySnapshot()
    {
        var sb = new StringBuilder();
        sb.Append("W|" + weather + "|" + N2(Math.Max(0, weatherUntil - now)) + "|" + N2(ownHour) + "\n");
        if (hasHot2) sb.Append("H|" + N2(hot2Pos.x) + "|" + N2(hot2Pos.y) + "|" + N2(hot2Pos.z) + "|" + N2(hot2Radius) + "|" + hot2Scene + "|" + N2(now - hot2MovedAt) + "\n");
        if (poolScene != null) { sb.Append("PS|" + poolScene + "|" + poolSeq + "\n"); foreach (var p in pools) sb.Append("P|" + p.id + "|" + N2(p.x) + "|" + N2(p.y) + "|" + N2(p.z) + "|" + N2(p.r) + "\n"); }
        foreach (var kv in lureUses) foreach (var u in kv.Value) sb.Append("LU|" + kv.Key + "|" + u.Key + "|" + u.Value + "\n");
        foreach (var kv in lureEquipped) sb.Append("LE|" + kv.Key + "|" + kv.Value + "\n");
        foreach (var kv in buffUntil) foreach (var b in kv.Value) if (b.Value > now) sb.Append("B|" + kv.Key + "|" + b.Key + "|" + N2(b.Value - now) + "\n");
        foreach (var kv in tempFeathers) sb.Append("F|" + kv.Key + "|" + kv.Value + "\n");
        ZoneSnapshot(sb);
        return sb.ToString();
    }
    static void GameplayRestore(string s)
    {
        foreach (string line in s.Split(new[] { '\n' }))
        {
            string[] p = line.Split(new[] { '|' });
            try
            {
                switch (p[0])
                {
                    case "W": weather = p[1]; weatherUntil = now + float.Parse(p[2], Ci); ownHour = float.Parse(p[3], Ci); break;
                    case "H": hot2Pos = new UnityEngine.Vector3(float.Parse(p[1], Ci), float.Parse(p[2], Ci), float.Parse(p[3], Ci)); hot2Radius = float.Parse(p[4], Ci); hot2Scene = p[5]; hot2MovedAt = now - float.Parse(p[6], Ci); hasHot2 = true; break;
                    case "PS": poolScene = p[1]; poolSeq = int.Parse(p[2]); break;
                    case "P": pools.Add(new GpPool { id = int.Parse(p[1]), x = float.Parse(p[2], Ci), y = float.Parse(p[3], Ci), z = float.Parse(p[4], Ci), r = float.Parse(p[5], Ci) }); break;
                    case "LU": { Dictionary<string, int> u; if (!lureUses.TryGetValue(p[1], out u)) { u = new Dictionary<string, int>(); lureUses[p[1]] = u; } u[p[2]] = int.Parse(p[3]); break; }
                    case "LE": lureEquipped[p[1]] = p[2]; break;
                    case "B": { Dictionary<string, float> d; if (!buffUntil.TryGetValue(p[1], out d)) { d = new Dictionary<string, float>(); buffUntil[p[1]] = d; } d[p[2]] = now + float.Parse(p[3], Ci); break; }
                    case "F": tempFeathers[p[1]] = int.Parse(p[2]); break;
                    case "ZK": case "ZP": ZoneRestoreLine(p); break;
                }
            }
            catch (Exception e) { GpLog("restore line '" + line + "': " + e.Message); }
        }
        GpLog("gameplay state carried over: weather " + weather + ", hot2 " + hasHot2 + ", " + pools.Count + " pools, " + lureUses.Count + " lure owners, " + buffUntil.Count + " buffed players");
    }
    static void GameplayCarry(Type old)   // CarryOver hook (managed data only)
    {
        try
        {
            MethodInfo snap = old.GetMethod("GameplaySnapshot", BindingFlags.Public | BindingFlags.Static);
            if (snap != null) GameplayRestore((string)snap.Invoke(null, null)); else GpLog("carry-over: " + old.Name + " has no gameplay state (first gameplay version)");
        }
        catch (Exception e) { GpLog("gameplay carry-over error: " + e); }
    }
    static void GameplayCleanOld()   // CleanOld hook (main thread): hide older versions' gameplay visuals
    {
        foreach (var asm in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(asm))
            {
                if (!Regex.IsMatch(t.Name, "^GameplayFx\\d+$") || t == typeof(GameplayFx25)) continue;
                try
                {
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static); if (en != null) en.SetValue(null, false);
                    MethodInfo cl = t.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static); if (cl != null) cl.Invoke(null, null);
                    GpLog("cleared old visuals of " + t.Name);
                }
                catch (Exception e) { GpLog("clean " + t.Name + ": " + e.Message); }
            }
    }

    // test only (main thread via a flag): logs what !eat would do and the odds tables, takes nothing
    public static int dryRunGameplay;
    static void GameplayDryRun()
    {
        if (dryRunGameplay <= 0) return;
        dryRunGameplay = 0;
        string me = LocalName();
        GpLog("DRY-RUN start (no fish / money taken). hour " + Hour().ToString("0.0") + ", event " + eventName + ", hot2 " + hasHot2 + " " + V(hot2Pos) + ", pools " + pools.Count);
        foreach (string w in new[] { "lake", "hot", "hot2", "pool" }) GpLog("  " + GpOddsReport(me, w, null));
        foreach (string l in LureNames) GpLog("  " + GpOddsReport(me, "lake", l));
        foreach (string sp in Species) Eat(me, sp, true);
    }
}
