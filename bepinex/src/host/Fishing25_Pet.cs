// Drone-pet fishing for the host's Fishing<N> (partial class; Fishing25 is replaced by the version name).
// DronePets<N> (the add-on that spawns the drones) calls PetCast(owner, drone x/y/z) every 20-30 s per pet.
//  - target: a free school fish whose position at landing is within petReach (horizontal) of the drone, picked at
//    random weighted by petTierMult (white 1 / green 0.5 / red 0.2 / lunar 0.1, Old Boot = white) - so pets mostly
//    hook common fish. The bobber lands 0-6 m from it on open water; the fish swims to the bobber like for players.
//  - reel success = lerp(95% at 0 m, 35% at 15 m) x petChanceMult (0.6) x petFishChanceMult[tier]
//    (white 1 / green 0.6 / red 0.3 / lunar 0.3); no hotspot bonus.
//  - a hooked fish that left the school meanwhile: species rolled from the base tier odds (white 60 / green 30 /
//    red 8 / lunar 2) rescaled by petTierMult and renormalised, with the same success chance at 15 m.
//  - value = normal catch value x petValueMult (0.5); the owner gets money, collection, the fish's carrier item / perk
//    (and the Golden Koi x2 bonus counter, but pet catches never use it up). Only red / lunar catches are announced.
//  Lines: [FT] PETCAST|<owner>|<x>|<y>|<z>  (bobber target)   [FT] PETCATCH|<owner>|<species or none>
//  plus the usual [FT] CATCH / [FT] PERK lines for the owner (HUD money / collection on guests).
//  Lures / weather events can combine with the public multipliers below.
using System;
using System.Collections.Generic;
using System.Globalization;

public static partial class Fishing25
{
    public static float petReach = 30f;                                   // drone -> fish horizontal reach (m)
    public static float petChanceMult = 0.6f;                             // overall reel-success multiplier
    public static float petValueMult = 0.5f;                              // money multiplier
    public static float[] petTierMult = { 1f, 0.5f, 0.2f, 0.1f };         // white, green, red, lunar: tier weights
    public static float[] petFishChanceMult = { 1f, 0.6f, 0.3f, 0.3f };   // extra reel-success multiplier for a hooked fish of that tier
    public static int petCasts, petCatches, petMisses;

    class PetHook { public int fishId = -1; public float dist, chance, land, biteAt, resolveAt, delay, bx, bz; public bool activated; }
    static readonly Dictionary<string, PetHook> petHooks = new Dictionary<string, PetHook>();
    static bool petOddsLogged;

    public static string PetKey(string owner) { return owner + "'s pet"; }
    static bool PetHolds(SchoolFish f)
    {
        if (f.busy == null || !f.busy.EndsWith("'s pet")) return false;
        PetHook h; return petHooks.TryGetValue(f.busy.Substring(0, f.busy.Length - 6), out h) && h.fishId == f.id;
    }
    static int TierIdx(string species)
    {
        string t = FishSchool.TierOf(species);
        return t == "Lunar" ? 3 : t == "Tier3" ? 2 : t == "Tier2" ? 1 : 0;
    }
    static string PetFmt(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

    static void PetOddsLog()
    {
        petOddsLogged = true;
        double[] b = { 60, 30, 8, 2 }; double s = 0; double[] w = new double[4];
        for (int i = 0; i < 4; i++) { w[i] = b[i] * petTierMult[i]; s += w[i]; }
        string[] n = { "white", "green", "red", "lunar" };
        var parts = new List<string>();
        for (int i = 0; i < 4; i++) parts.Add(n[i] + " " + (100 * w[i] / s).ToString("0.00") + "% (reel x" + (petChanceMult * petFishChanceMult[i]).ToString("0.00") + ")");
        string msg = "PET odds: tier weights x{" + string.Join(",", Array.ConvertAll(petTierMult, x => x.ToString("0.0#"))) + "} -> species roll " + string.Join(", ", parts.ToArray())
            + "; reel success = lerp(95%,35%, dist/15 m) x " + petChanceMult + " x tier mult; value x" + petValueMult + "; no hotspot bonus; reach " + petReach + " m";
        Log(msg); LogC(msg);
    }

    public static string PetCast(string owner, float px, float py, float pz)
    {
        try
        {
            if (!enabled) return "disabled";
            if (!petOddsLogged) PetOddsLog();
            if (!tournamentOpen) return "closed";
            if (!hasWater || IsNull(water)) return "nowater";
            if (petHooks.ContainsKey(owner)) return "busy";
            float t = SimNow;
            var cands = new List<SchoolFish>(); var wts = new List<double>(); double sum = 0;
            foreach (SchoolFish f in school.Values)
            {
                if (f.busy != null) continue;
                float x, z; f.Pos(t, out x, out z);
                float d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                f.Pos(t + FlightTime(Math.Max(lobMinDist, d)), out x, out z);
                d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                if (d > petReach) continue;
                double w = petTierMult[TierIdx(FishSchool.Species[f.sp])];
                if (w <= 0) continue;
                cands.Add(f); wts.Add(w); sum += w;
            }
            if (cands.Count == 0) return "nofish";
            double r = rng.NextDouble() * sum; int pick = 0;
            for (; pick < cands.Count - 1; pick++) { r -= wts[pick]; if (r < 0) break; }
            SchoolFish best = cands[pick];
            float cx, cz; best.Pos(t, out cx, out cz);
            float dd = (float)Math.Sqrt((cx - px) * (cx - px) + (cz - pz) * (cz - pz));
            float flight = FlightTime(Math.Max(lobMinDist, dd));
            float fx, fz; best.Pos(t + flight, out fx, out fz);
            float bx = fx, bz = fz, off = 0f, gh;
            for (int i = 0; i < 10; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2, o = rng.NextDouble() * 6;
                float tx = fx + (float)(Math.Cos(a) * o), tz = fz + (float)(Math.Sin(a) * o);
                if (OpenWater(tx, tz, out gh)) { bx = tx; bz = tz; off = (float)o; break; }
            }
            string sp = FishSchool.Species[best.sp];
            int tier = TierIdx(sp);
            var h = new PetHook { fishId = best.id, dist = off, bx = bx, bz = bz };
            h.chance = Math.Max(0f, Math.Min(0.98f, Lerp(0.95f, 0.35f, Math.Min(1f, off / InterestRange)) * petChanceMult * petFishChanceMult[tier]));
            h.delay = 1f + 2f * (off / InterestRange);
            h.land = now + flight; h.biteAt = h.land + h.delay; h.resolveAt = h.biteAt + 0.8f;
            best.busy = PetKey(owner);
            petHooks[owner] = h; petCasts++;
            float by = waterY + 0.05f;
            var ci = CultureInfo.InvariantCulture;
            SendRaw("[FT] PETCAST|" + owner + "|" + bx.ToString("0.00", ci) + "|" + by.ToString("0.00", ci) + "|" + bz.ToString("0.00", ci));
            LogC("PET " + owner + ": cast from (" + px.ToString("0.0") + ", " + pz.ToString("0.0") + ") at fish #" + best.id + " " + sp + " (" + cands.Count + " in reach), bobber "
                 + off.ToString("0.0") + " m from it, flight " + flight.ToString("0.00") + " s, bite in " + h.delay.ToString("0.0") + " s, chance " + (h.chance * 100).ToString("0") + "%");
            return "ok|" + PetFmt(bx) + "|" + PetFmt(by) + "|" + PetFmt(bz) + "|" + PetFmt(flight) + "|" + PetFmt(h.resolveAt - now) + "|" + PetFmt(h.biteAt - now);
        }
        catch (Exception e) { LogC("PET cast error: " + e); return "error"; }
    }

    // every fixed tick (after SchoolTimers)
    static void PetTick()
    {
        if (petHooks.Count == 0) return;
        foreach (string owner in new List<string>(petHooks.Keys))
        {
            PetHook h = petHooks[owner];
            SchoolFish f = null;
            if (h.fishId >= 0 && school.TryGetValue(h.fishId, out f) && f.busy != PetKey(owner)) f = null;
            if (!h.activated && now >= h.land)
            {
                h.activated = true;
                if (f != null)
                {
                    float t = SimNow, x, z; f.Pos(t, out x, out z);
                    var n = new SchoolFish { id = f.id, sp = f.sp, seed = f.seed, ax = h.bx, az = h.bz, R = 0.5f, sx = x, sz = z, t0 = t, D = Math.Max(0.6f, h.delay * 0.85f), busy = f.busy };
                    school[f.id] = n; f = n;
                    SendFish(n);
                }
            }
            if (!tournamentOpen || FindUser(owner) == null)
            {
                petHooks.Remove(owner);
                if (f != null) { f.busy = null; DartOff(f, owner + "'s pet stopped"); }
                SendRaw("[FT] PETCATCH|" + owner + "|none");
                continue;
            }
            if (now < h.resolveAt) continue;
            petHooks.Remove(owner);
            string sp;
            float chance = h.chance;
            if (f != null) sp = FishSchool.Species[f.sp];
            else
            {   // the fish left the school: rescaled tier roll, chance as at 15 m
                double[] b = { 60, 30, 8, 2 }; double s = 0;
                for (int i = 0; i < 4; i++) s += b[i] * petTierMult[i];
                double r = rng.NextDouble() * s; int ti = 0;
                for (; ti < 3; ti++) { r -= b[ti] * petTierMult[ti]; if (r < 0) break; }
                sp = RollSpecies(null, "lake", null, true); ti = TierIdx(sp);
                chance = 0.35f * petChanceMult * petFishChanceMult[ti];
            }
            double roll = rng.NextDouble();
            if (roll < chance)
            {
                if (f != null)
                {
                    float x, z; f.Pos(SimNow, out x, out z);
                    school.Remove(f.id); farSince.Remove(f.id);
                    SendGone(f.id, "caught");
                    if (FishRegion(f) < 0) respawns.Add(new Respawn { at = now + 10f, x = x, z = z, since = now });   // zone / hotspot fish: StockRegions refills
                }
                PetScore(owner, sp);
                petCatches++;
                LogC("PET " + owner + ": CAUGHT " + sp + (f != null ? " (school fish #" + f.id + ")" : " (fish gone, tier roll)") + " chance " + (chance * 100).ToString("0") + "% roll " + (roll * 100).ToString("0")
                     + " [pet totals: " + petCasts + " casts, " + petCatches + " catches, " + petMisses + " misses]");
            }
            else
            {
                petMisses++;
                if (f != null) { f.busy = null; DartOff(f, "got away from " + owner + "'s pet"); }
                SendRaw("[FT] PETCATCH|" + owner + "|none");
                LogC("PET " + owner + ": " + sp + " got away (chance " + (chance * 100).ToString("0") + "%, roll " + (roll * 100).ToString("0") + ")");
            }
        }
    }

    static void PetScore(string owner, string species)
    {
        int ti = TierIdx(species);
        int lo, hi;
        if (species == "Old Boot") { lo = 0; hi = 0; }
        else if (ti == 3) { lo = 500; hi = 500; }
        else if (ti == 2) { lo = 150; hi = 300; }
        else if (ti == 1) { lo = 40; hi = 80; }
        else { lo = 10; hi = 30; }
        int value = (int)Math.Round(rng.Next(lo, hi + 1) * petValueMult);
        int total; money.TryGetValue(owner, out total); total += value; money[owner] = total;
        Dictionary<string, int> c; if (!collections.TryGetValue(owner, out c)) { c = new Dictionary<string, int>(); collections[owner] = c; }
        int n; c.TryGetValue(species, out n); c[species] = n + 1;
        if (species == "Golden Koi") { int dl; doubleLeft.TryGetValue(owner, out dl); doubleLeft[owner] = dl + 3; }
        string perk = GivePerk(owner, species);
        ChatRaw("[FT] CATCH|" + owner + "|" + species + "|" + value + "|" + total);
        ChatRaw("[FT] PERK|" + owner + "|" + species + "|" + perk);
        SendRaw("[FT] PETCATCH|" + owner + "|" + species);
        if (ti >= 2) Chat(owner + "'s drone caught a " + species + "! (+$" + value + ")");
        Log(owner + "'s pet caught " + species + " worth $" + value + " (total $" + total + ")");
        if (++totalCatches % 5 == 0) Board();
    }
}
