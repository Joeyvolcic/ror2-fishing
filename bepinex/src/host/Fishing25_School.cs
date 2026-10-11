// Host part of the shared fish school: the visible fish are the catchable fish.
//  - the host owns the school (12-20 fish around the players, species by the catch table's tier odds) and broadcasts
//    it as hidden [FT] SCHOOL / SFISH / SGONE lines (see FishSchool.cs); everyone renders the same deterministic paths.
//  - a bobber landing on water picks the nearest free school fish within 15 m (position at the landing moment):
//    that fish swims to the bobber, bites after 1-3 s (sooner when closer), and the catch is THAT fish;
//    reel success = lerp(95% at 0 m, 35% at 15 m) (+10 points inside the hotspot). Caught fish leave the school and a
//    replacement spawns >= 25 m away after ~10 s; a fish that got away darts off to a new spot.
//  - no fish within 15 m: only half the casts get a bite at all (after 6-12 s), species from the normal tier odds.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static partial class Fishing25
{
    static readonly string CatchLogPath = Path.Combine(Path.GetTempPath(), "ror2_catchfish.log");
    static void LogC(string s)
    {
        try { File.AppendAllText(CatchLogPath, DateTime.Now.ToString("HH:mm:ss ") + "Fishing25: " + s + Environment.NewLine); } catch { }
    }

    public const float InterestRange = 15f;
    public static readonly Dictionary<int, SchoolFish> school = new Dictionary<int, SchoolFish>();
    static string schoolScene;
    static int schoolSeq, schoolNextId = 1, schoolTarget;
    static bool schoolDirty;
    static float schoolBroadcastAt = -100f, schoolChangedAt = -100f;
    static readonly Dictionary<int, float> farSince = new Dictionary<int, float>();
    class Respawn { public float at, x, z, since; }
    static readonly List<Respawn> respawns = new List<Respawn>();

    class Hook { public int fishId = -1; public float dist, chance, activateAt, delay, emptyAt = -1f, bx, bz; public bool activated; public string species; }
    static readonly Dictionary<string, Hook> hooks = new Dictionary<string, Hook>();
    static string catchForced, catchNote;

    static float SimNow { get { return UnityEngine.Time.realtimeSinceStartup; } }   // the clock in every school line

    static Hook HookOf(string name) { Hook h; if (!hooks.TryGetValue(name, out h)) { h = new Hook(); hooks[name] = h; } return h; }
    static bool HookedFish(string name) { Hook h; return hooks.TryGetValue(name, out h) && h.fishId >= 0 && school.ContainsKey(h.fishId); }

    // ---------------------------------------------------------------- carry-over (for the next version bump)
    public static string SchoolSnapshot()
    {
        var parts = new List<string>();
        parts.Add(schoolScene ?? ""); parts.Add(schoolSeq.ToString()); parts.Add(schoolNextId.ToString()); parts.Add(schoolTarget.ToString());
        foreach (SchoolFish f in school.Values) parts.Add(f.Encode());
        return string.Join("~", parts.ToArray());
    }
    static void SchoolRestore(string snap)
    {
        try
        {
            string[] p = snap.Split('~');
            schoolScene = p[0].Length > 0 ? p[0] : null;
            schoolSeq = int.Parse(p[1]) + 1; schoolNextId = int.Parse(p[2]); schoolTarget = int.Parse(p[3]);
            for (int i = 4; i < p.Length; i++) { SchoolFish f = SchoolFish.Decode(p[i]); if (f != null) school[f.id] = f; }
            schoolDirty = true;
            LogC("school carried over: " + school.Count + " fish on " + schoolScene + ", next id " + schoolNextId);
        }
        catch (Exception e) { LogC("school restore error: " + e.Message); }
    }

    // ---------------------------------------------------------------- chat out
    static void SendRaw(string text)
    {
        try
        {
            Type chat = ror2.GetType("RoR2.Chat");
            object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
            Set(msg, "baseToken", text);
            foreach (MethodInfo m in chat.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
        }
        catch (Exception e) { LogC("send error: " + e.Message); }
        LogC("SEND " + text);
    }

    static void BroadcastSchool(string why)
    {
        float t = SimNow;
        schoolBroadcastAt = now; schoolDirty = false; schoolSeq++;
        var lines = FishSchool.SchoolLines(schoolSeq, t, waterY, school.Values);
        foreach (string l in lines) SendRaw(l);
        LogC("school broadcast seq " + schoolSeq + " (" + why + "): " + school.Count + " fish in " + lines.Count + " line(s)");
    }
    static void SendFish(SchoolFish f) { SendRaw("[FT] SFISH|" + FishSchool.F2(SimNow) + "|" + f.Encode(SimNow)); }
    static void SendGone(int id, string reason) { SendRaw("[FT] SGONE|" + FishSchool.F2(SimNow) + "|" + id + "|" + reason); }

    // ---------------------------------------------------------------- spawning
    static bool AnchorOk(float x, float z, float R)
    {
        float gh;
        return OpenWater(x, z, out gh) && OpenWater(x + R, z, out gh) && OpenWater(x - R, z, out gh) && OpenWater(x, z + R, out gh) && OpenWater(x, z - R, out gh);
    }
    static bool InHot(float x, float z)
    {
        if (!hasHotspot) return false;
        float dx = x - hotspotPos.x, dz = z - hotspotPos.z;
        return dx * dx + dz * dz <= hotspotRadius * hotspotRadius;
    }

    // a new fish near a random living player (or inside the hotspot), >= avoidDist from (avoidX, avoidZ)
    static SchoolFish SpawnFish(bool inHotspot, float avoidX, float avoidZ, float avoidDist)
    {
        var feet = LivingFeet();
        if (feet.Count == 0 || !hasWater) return null;
        for (int i = 0; i < 80; i++)
        {
            float R = 2.5f + (float)rng.NextDouble() * 4.5f, ax, az;
            if (inHotspot && hasHotspot)
            {
                double a = rng.NextDouble() * Math.PI * 2, d = Math.Sqrt(rng.NextDouble()) * Math.Max(0.5f, hotspotRadius - 2f);
                ax = hotspotPos.x + (float)(Math.Cos(a) * d); az = hotspotPos.z + (float)(Math.Sin(a) * d);
                R = Math.Min(R, Math.Max(1.5f, hotspotRadius * 0.6f));
            }
            else
            {
                UnityEngine.Vector3 f = feet[rng.Next(feet.Count)];
                double a = rng.NextDouble() * Math.PI * 2, d = 7 + rng.NextDouble() * (i < 40 ? 23 : 45);
                ax = f.x + (float)(Math.Cos(a) * d); az = f.z + (float)(Math.Sin(a) * d);
            }
            if (avoidDist > 0) { float dx = ax - avoidX, dz = az - avoidZ; if (dx * dx + dz * dz < avoidDist * avoidDist) continue; }
            if (!AnchorOk(ax, az, R)) continue;
            if (!inHotspot && RegionAt(ax, az) >= 0) continue;   // zones / hotspot are stocked by StockRegions
            string sp = GpSchoolRollAt(ax, az);
            var fish = new SchoolFish { id = schoolNextId++, sp = FishSchool.Index(sp), ax = ax, az = az, R = R, seed = rng.Next(1, 1000000) };
            // swim in from the anchor over 2.5 s (FishFx fades it in)
            fish.sx = ax; fish.sz = az; fish.t0 = SimNow; fish.D = 2.5f;
            return fish;
        }
        return null;
    }

    static void Regenerate(string why)
    {
        school.Clear(); farSince.Clear(); respawns.Clear();
        foreach (Hook h in hooks.Values) h.fishId = -1;
        schoolScene = SceneName();
        schoolTarget = 12 + rng.Next(9);   // 12..20
        int inHot = hasHotspot ? 3 : 0, made = 0;
        for (int i = 0; i < schoolTarget; i++)
        {
            SchoolFish f = SpawnFish(i < inHot, 0, 0, 0);
            if (f == null) continue;
            school[f.id] = f; made++;
        }
        var counts = new SortedDictionary<string, int>();
        foreach (SchoolFish f in school.Values) { string n = FishSchool.Species[f.sp]; int c; counts.TryGetValue(n, out c); counts[n] = c + 1; }
        var parts = new List<string>(); foreach (var kv in counts) parts.Add(kv.Key + " x" + kv.Value);
        LogC("school generated on " + schoolScene + " (" + why + "): " + made + "/" + schoolTarget + " fish (" + string.Join(", ", parts.ToArray()) + "), water Y=" + waterY.ToString("0.00"));
        Log("school generated: " + made + " fish on " + schoolScene);
        BroadcastSchool(why);
    }

    // a fish that lost interest / got away swims off to a new spot 15-25 m away
    static void DartOff(SchoolFish f, string why)
    {
        float t = SimNow, x, z; f.Pos(t, out x, out z);
        SchoolFish n = null;
        for (int i = 0; i < 40 && n == null; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2, d = 15 + rng.NextDouble() * 10;
            float R = 2.5f + (float)rng.NextDouble() * 4f;
            float ax = x + (float)(Math.Cos(a) * d), az = z + (float)(Math.Sin(a) * d);
            if (AnchorOk(ax, az, R) && DartOk(f, ax, az)) n = new SchoolFish { id = f.id, sp = f.sp, ax = ax, az = az, R = R, seed = rng.Next(1, 1000000) };
        }
        if (n == null) n = new SchoolFish { id = f.id, sp = f.sp, ax = x, az = z, R = 3f, seed = rng.Next(1, 1000000) };
        float dd = (float)Math.Sqrt((n.ax - x) * (n.ax - x) + (n.az - z) * (n.az - z));
        n.sx = x; n.sz = z; n.t0 = t; n.D = Math.Max(1.5f, Math.Min(8f, dd / 5f));
        school[f.id] = n; farSince.Remove(f.id);
        SendFish(n);
        LogC("fish #" + f.id + " " + FishSchool.Species[f.sp] + " darts off (" + why + ") to (" + n.ax.ToString("0.0") + ", " + n.az.ToString("0.0") + "), " + dd.ToString("0") + " m");
    }

    // ---------------------------------------------------------------- every 0.5 s (after WaterTick / HotspotTick)
    static void SchoolTick()
    {
        try
        {
            string scene = SceneName();
            if (!hasWater || IsNull(water) || waterScene != scene)
            {
                if (school.Count > 0) { school.Clear(); respawns.Clear(); foreach (Hook h in hooks.Values) h.fishId = -1; schoolScene = null; BroadcastSchool("no water"); }
                return;
            }
            if (schoolScene != scene) { Regenerate(schoolScene == null ? "first school" : "new stage"); return; }

            // fish whose angler stopped fishing (too early, got away, moved, tournament over, died...) swim off
            foreach (SchoolFish f in new List<SchoolFish>(school.Values))
            {
                if (f.busy == null || PetHolds(f)) continue;
                Angler a; anglers.TryGetValue(f.busy, out a); Hook h; hooks.TryGetValue(f.busy, out h);
                if (a == null || h == null || h.fishId != f.id || a.state == 0 || a.state == 3)
                {
                    string who = f.busy; f.busy = null;
                    if (h != null && h.fishId == f.id) h.fishId = -1;
                    DartOff(f, who + " stopped fishing");
                }
            }

            // fish far from every living player for 15 s move back near the players
            var feet = LivingFeet();
            if (feet.Count > 0)
            {
                int moved = 0;
                foreach (SchoolFish f in new List<SchoolFish>(school.Values))
                {
                    if (f.busy != null) continue;
                    bool near = false;
                    foreach (var p in feet) { float dx = p.x - f.ax, dz = p.z - f.az; if (dx * dx + dz * dz <= 70f * 70f) { near = true; break; } }
                    float since;
                    if (near) { farSince.Remove(f.id); continue; }
                    if (!farSince.TryGetValue(f.id, out since)) { farSince[f.id] = now; continue; }
                    if (now - since < 15f) continue;
                    if (FishRegion(f) >= 0) { farSince.Remove(f.id); school.Remove(f.id); SendGone(f.id, "moved"); moved++; continue; }   // zone / hotspot fish: restocked when someone is near
                    SchoolFish n = SpawnFish(false, 0, 0, 0);
                    farSince.Remove(f.id);
                    if (n == null) continue;
                    school.Remove(f.id); school[n.id] = n; moved++;
                }
                if (moved > 0) { LogC(moved + " fish far from every player moved back near the players"); schoolDirty = true; }
            }

            // replacements for caught fish (>= 25 m from the catch, after ~10 s); top up if spawning failed earlier
            for (int i = respawns.Count - 1; i >= 0; i--)
            {
                Respawn r = respawns[i];
                if (now < r.at) continue;
                SchoolFish n = SpawnFish(false, r.x, r.z, now - r.since < 60f ? 25f : 0f);
                if (n == null) { r.at = now + 2f; continue; }
                school[n.id] = n; respawns.RemoveAt(i);
                SendFish(n);
                LogC("replacement fish #" + n.id + " " + FishSchool.Species[n.sp] + " spawned at (" + n.ax.ToString("0.0") + ", " + n.az.ToString("0.0") + ")");
            }
            if (LakeFishCount() + respawns.Count < schoolTarget && now - schoolChangedAt > 5f)
            {
                schoolChangedAt = now;
                SchoolFish n = SpawnFish(false, 0, 0, 0);
                if (n != null) { school[n.id] = n; SendFish(n); LogC("top-up fish #" + n.id + " " + FishSchool.Species[n.sp]); }
            }

            if (schoolDirty && now - schoolBroadcastAt >= 1f) BroadcastSchool("changed");
            else if (now - schoolBroadcastAt >= 20f) BroadcastSchool("periodic");
        }
        catch (Exception e) { if (errors++ < 10) LogC("school tick error: " + (e.InnerException ?? e)); }
    }

    // ---------------------------------------------------------------- casting
    // called right after a cast that lands on open water; sets the bite time
    static void SchoolOnCast(string name, Angler a)
    {
        Hook h = HookOf(name);
        h.fishId = -1; h.activated = false; h.emptyAt = -1f;
        Rig r; rigs.TryGetValue(name, out r);
        float flight = r != null ? r.flight : 0.6f, land = now + flight, simLand = SimNow + flight;
        float bx = r != null ? r.tx : 0, bz = r != null ? r.tz : 0;
        SchoolFish best = null; float bestD = float.MaxValue;
        int bReg = BobberRegion(bx, bz, a.hot);
        foreach (SchoolFish f in school.Values)
        {
            if (f.busy != null || !SameRegion(f, bReg)) continue;
            float x, z; f.Pos(simLand, out x, out z);
            float d = (float)Math.Sqrt((x - bx) * (x - bx) + (z - bz) * (z - bz));
            if (d < bestD) { bestD = d; best = f; }
        }
        string nearest = best == null ? "no free fish" : "nearest #" + best.id + " " + FishSchool.Species[best.sp] + " " + bestD.ToString("0.0") + " m";
        best = LuckyPick(name, best, ref bestD, simLand, bx, bz);
        if (best != null && bestD <= InterestRange)
        {
            best.busy = name;
            h.fishId = best.id; h.dist = bestD; h.species = FishSchool.Species[best.sp];
            h.chance = Math.Min(0.98f, Lerp(0.95f, 0.35f, bestD / InterestRange) + (a.hot ? 0.10f : 0f));
            h.delay = 1f + 2f * (bestD / InterestRange);
            h.activateAt = land; h.bx = bx; h.bz = bz;
            a.biteAt = land + h.delay;
            LogC(name + ": cast lands at (" + bx.ToString("0.0") + ", " + bz.ToString("0.0") + ") in " + flight.ToString("0.00") + " s; " + nearest
                 + " -> INTERESTED, bite in " + h.delay.ToString("0.0") + " s after landing, reel chance " + (h.chance * 100).ToString("0") + "%" + (a.hot ? " (hotspot +10)" : ""));
        }
        else
        {
            bool bite = rng.NextDouble() < 0.5;
            if (bite) a.biteAt = land + 6f + (float)rng.NextDouble() * 6f;
            else { a.biteAt = float.MaxValue; h.emptyAt = land + 12f; }
            LogC(name + ": cast lands at (" + bx.ToString("0.0") + ", " + bz.ToString("0.0") + ") in " + flight.ToString("0.00") + " s; " + nearest
                 + " -> no fish within " + InterestRange + " m, " + (bite ? "random bite in " + (a.biteAt - land).ToString("0.0") + " s" : "NO bite (empty hook in 12 s)"));
        }
    }

    // every tick, before the bite timers: bobber landed -> the fish swims to it; empty-hook timeout
    static void SchoolTimers()
    {
        foreach (var kv in hooks)
        {
            Hook h = kv.Value;
            Angler a; if (!anglers.TryGetValue(kv.Key, out a)) continue;
            if (h.fishId >= 0 && !h.activated && a.state == 1 && now >= h.activateAt)
            {
                h.activated = true;
                SchoolFish f;
                if (school.TryGetValue(h.fishId, out f))
                {
                    float t = SimNow, x, z; f.Pos(t, out x, out z);
                    var n = new SchoolFish { id = f.id, sp = f.sp, seed = f.seed, ax = h.bx, az = h.bz, R = 0.5f, sx = x, sz = z, t0 = t, D = Math.Max(0.6f, h.delay * 0.85f), busy = f.busy };
                    school[f.id] = n;
                    SendFish(n);
                }
            }
            if (h.emptyAt > 0 && a.state == 1 && now >= h.emptyAt)
            {
                h.emptyAt = -1f;
                a.state = 0; a.readyAt = now + 1f;
                RigReel(kv.Key);
                Chat(kv.Key + " reeled in an empty hook - no fish nearby. Cast closer to the fish!");
                LogC(kv.Key + ": empty hook (no fish within " + InterestRange + " m, no bite)");
            }
            else if (a.state != 1) h.emptyAt = -1f;
        }
    }

    // the angler reeled in on time (player click/F/chat, or auto-reel)
    static void ReelIn(string name)
    {
        Hook h; hooks.TryGetValue(name, out h);
        SchoolFish f = null;
        if (h != null && h.fishId >= 0) school.TryGetValue(h.fishId, out f);
        if (f == null) { if (h != null) h.fishId = -1; Catch(name); return; }
        h.fishId = -1;
        string sp = FishSchool.Species[f.sp];
        string note = " (cast " + h.dist.ToString("0.0") + " m from it!)";
        double roll = rng.NextDouble();
        if (roll < h.chance)
        {
            catchForced = sp; catchNote = note;
            string res;
            try { res = Catch(name); }
            finally { catchForced = null; catchNote = null; }
            if (res == "closed" || res == "nobody") { f.busy = null; DartOff(f, "catch blocked: " + res); return; }
            float x, z; f.Pos(SimNow, out x, out z);
            school.Remove(f.id); farSince.Remove(f.id);
            SendGone(f.id, "caught");
            if (FishRegion(f) < 0) respawns.Add(new Respawn { at = now + 10f, x = x, z = z, since = now });   // zone / hotspot fish: StockRegions refills
            LogC(name + ": CAUGHT school fish #" + f.id + " " + sp + " (cast " + h.dist.ToString("0.0") + " m from it, chance " + (h.chance * 100).ToString("0") + "%, roll " + (roll * 100).ToString("0") + ") -> replacement in 10 s, >= 25 m away");
        }
        else
        {
            Chat(name + " hooked a " + sp + " but it got away! (cast " + h.dist.ToString("0.0") + " m from it, " + (h.chance * 100).ToString("0") + "% chance)");
            Log(name + ": school fish " + sp + " got away (roll " + (roll * 100).ToString("0") + " vs " + (h.chance * 100).ToString("0") + "%)");
            LogC(name + ": school fish #" + f.id + " " + sp + " GOT AWAY (cast " + h.dist.ToString("0.0") + " m, chance " + (h.chance * 100).ToString("0") + "%, roll " + (roll * 100).ToString("0") + ")");
            f.busy = null;
            DartOff(f, "got away from " + name);
        }
    }

    // auto-fishing: aim at a random free fish 15-60 m away whose spot at landing is open water
    static bool AutoAimFish(string name, float fx, float fz, Rig r)
    {
        var cands = new List<SchoolFish>();
        foreach (SchoolFish f in school.Values) if (f.busy == null) cands.Add(f);
        float t = SimNow;
        while (cands.Count > 0)
        {
            int i = rng.Next(cands.Count); SchoolFish f = cands[i]; cands.RemoveAt(i);
            float x, z; f.Pos(t, out x, out z);
            float d = (float)Math.Sqrt((x - fx) * (x - fx) + (z - fz) * (z - fz));
            f.Pos(t + FlightTime(d), out x, out z);
            double a = rng.NextDouble() * Math.PI * 2, off = rng.NextDouble() * 5;
            x += (float)(Math.Cos(a) * off); z += (float)(Math.Sin(a) * off);
            d = (float)Math.Sqrt((x - fx) * (x - fx) + (z - fz) * (z - fz));
            if (d < lobMinDist || d > 60f || d > castRange) continue;
            float gh; if (!OpenWater(x, z, out gh)) continue;
            r.tx = x; r.tz = z;
            LogC(name + ": auto-fish aims at fish #" + f.id + " " + FishSchool.Species[f.sp] + " (" + d.ToString("0.0") + " m away, " + off.ToString("0.0") + " m offset)");
            return true;
        }
        return false;
    }
}
