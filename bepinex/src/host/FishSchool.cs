// FishSchool - the SHARED, host-authoritative fish school of the fishing game.
// The host (Fishing<N>) owns the school and broadcasts it in hidden chat lines; every machine (host FishFx<N>,
// guest FishFxClient) computes identical fish positions from the same entries + the host's clock, so the fish
// you see are the fish you can catch. No Unity calls in here (pure math; safe on any thread).
//
// Protocol (host -> everyone, tag lines hidden from the chat box):
//   [FT] SCHOOL|<seq>|<part>/<parts>|<hostTime>|<waterY>|<entry>;<entry>;...    full school (split over parts, < 400 chars each)
//   [FT] SFISH|<hostTime>|<entry>                                                 add/replace one fish
//   [FT] SGONE|<hostTime>|<id>|<reason>                                           remove one fish (reason: caught/moved)
//   entry = id,sp,ax,az,R,seed,sx,sz,t0,D
//     sp = index into Species; (ax,az) orbit anchor; R orbit radius (m); seed = path shape/speed/depth/jumps;
//     the fish swims from (sx,sz) at host time t0 onto its orbit over D seconds (spawn fade-in, dart-off, swim to a bobber).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public class SchoolFish
{
    public int id, sp, seed;
    public float ax, az, R, sx, sz, t0, D;
    public string busy;        // host only: angler this fish is interested in

    // derived from seed (lazy)
    bool init; float w, p1, ell, r2, k2, p2, depth, jumpP, jumpOff;
    void Init()
    {
        if (init) return; init = true;
        var r = new Random(seed);
        float spd = FishSchool.Speed[sp] * (0.75f + (float)r.NextDouble() * 0.5f);
        float Rr = Math.Max(0.3f, R);
        w = Math.Min(1.2f, spd / Rr) * (r.Next(2) == 0 ? 1f : -1f);
        p1 = (float)(r.NextDouble() * Math.PI * 2);
        ell = 0.55f + (float)r.NextDouble() * 0.45f;
        r2 = Rr * (0.1f + (float)r.NextDouble() * 0.25f);
        k2 = 2f + (float)r.NextDouble() * 1.5f;
        p2 = (float)(r.NextDouble() * Math.PI * 2);
        depth = sp == FishSchool.Boot ? 1.2f + (float)r.NextDouble() * 0.3f : 0.3f + (float)r.NextDouble() * 1.2f;
        jumpP = 35f + r.Next(50);
        jumpOff = (float)r.NextDouble() * jumpP;
    }

    void Orbit(float t, out float x, out float z)
    {
        float th = w * t + p1;
        x = ax + R * (float)Math.Cos(th) + r2 * (float)Math.Cos(k2 * th + p2);
        z = az + R * ell * (float)Math.Sin(th) + r2 * (float)Math.Sin(k2 * th + p2);
    }

    // horizontal position at host time t
    public void Pos(float t, out float x, out float z)
    {
        Init();
        float ox, oz; Orbit(t, out ox, out oz);
        if (D <= 0.01f || t >= t0 + D) { x = ox; z = oz; return; }
        float u = Math.Max(0f, (t - t0) / D);
        float s = u * u * (3f - 2f * u);
        x = sx + (ox - sx) * s; z = sz + (oz - sz) * s;
    }

    public float Depth { get { Init(); return depth; } }

    // 0..1 progress of a jump out of the water at time t, or -1 (deterministic schedule; never while swimming in/out)
    public float Jump(float t)
    {
        Init();
        if (sp == FishSchool.Boot || R < 1f || t < t0 + D) return -1f;
        float k = (t + jumpOff) % jumpP;
        return k < 1.1f ? k / 1.1f : -1f;
    }

    public string Encode() { return Encode(float.MinValue); }
    public string Encode(float now)   // a finished swim-in is sent as 0,0,0,0 (pure orbit) to keep the lines short
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (now >= t0 + D) return id + "," + sp + "," + ax.ToString("0.0", ci) + "," + az.ToString("0.0", ci) + "," + R.ToString("0.0", ci) + "," + seed + ",0,0,0,0";
        return id + "," + sp + "," + ax.ToString("0.0", ci) + "," + az.ToString("0.0", ci) + "," + R.ToString("0.0", ci) + "," + seed + ","
             + sx.ToString("0.0", ci) + "," + sz.ToString("0.0", ci) + "," + t0.ToString("0.0", ci) + "," + D.ToString("0.0", ci);
    }

    public static SchoolFish Decode(string s)
    {
        string[] p = s.Split(',');
        if (p.Length < 10) return null;
        CultureInfo ci = CultureInfo.InvariantCulture;
        var f = new SchoolFish();
        f.id = int.Parse(p[0], ci); f.sp = int.Parse(p[1], ci);
        f.ax = float.Parse(p[2], ci); f.az = float.Parse(p[3], ci); f.R = float.Parse(p[4], ci);
        f.seed = int.Parse(p[5], ci);
        f.sx = float.Parse(p[6], ci); f.sz = float.Parse(p[7], ci); f.t0 = float.Parse(p[8], ci); f.D = float.Parse(p[9], ci);
        if (f.sp < 0 || f.sp >= FishSchool.Species.Length) return null;
        return f;
    }
}

public static class FishSchool
{
    // same order as FishFx's species table
    public static readonly string[] Species = { "Minnow", "Perch", "Trout", "Bass", "Salmon", "Swordfish", "Golden Koi", "Moon Eel", "Old Boot",
                                                "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };
    public static readonly float[] Speed = { 1.6f, 1.1f, 1.3f, 1.0f, 1.4f, 2.2f, 0.7f, 0.9f, 0.15f, 1.2f, 0.8f, 0.6f, 0.7f, 1.1f };
    public const int Boot = 8;
    public static int Index(string s) { for (int i = 0; i < Species.Length; i++) if (string.Equals(Species[i], s, StringComparison.OrdinalIgnoreCase)) return i; return -1; }

    // species roll with the catch table's tier odds: normal 10% boot, then white 60 / green 30 / red 8 / lunar 2;
    // hotspot: no boots, white 25 / green 35 / red 30 / lunar 10. Uniform species inside a tier.
    public static readonly string[] White = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" };
    public static readonly string[] Green = { "Bass", "Salmon", "Pufferfish" };
    public static readonly string[] Red = { "Swordfish", "Golden Koi", "Anglerfish" };
    public static readonly string[] Lunar = { "Moon Eel", "Ghost Fish" };
    public static string Roll(Random rng, bool hot)
    {
        if (!hot && rng.NextDouble() < 0.10) return "Old Boot";
        double r = rng.NextDouble();
        double pL = hot ? 0.10 : 0.02, pR = hot ? 0.40 : 0.10, pG = hot ? 0.75 : 0.40;
        string[] t = r < pL ? Lunar : r < pR ? Red : r < pG ? Green : White;
        return t[rng.Next(t.Length)];
    }
    public static string TierOf(string species)
    {
        if (Array.IndexOf(Lunar, species) >= 0) return "Lunar";
        if (Array.IndexOf(Red, species) >= 0) return "Tier3";
        if (Array.IndexOf(Green, species) >= 0) return "Tier2";
        if (Array.IndexOf(White, species) >= 0) return "Tier1";
        return "Boot";
    }

    public static string F2(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

    // full-school lines, each < 400 chars
    public static List<string> SchoolLines(int seq, float hostTime, float waterY, ICollection<SchoolFish> fish)
    {
        var entries = new List<string>();
        foreach (SchoolFish f in fish) entries.Add(f.Encode(hostTime));
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (string e in entries)
        {
            if (sb.Length > 0 && sb.Length + e.Length + 1 > 340) { chunks.Add(sb.ToString()); sb.Length = 0; }
            if (sb.Length > 0) sb.Append(';');
            sb.Append(e);
        }
        chunks.Add(sb.ToString());   // may be "" = empty school
        var lines = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
            lines.Add("[FT] SCHOOL|" + seq + "|" + (i + 1) + "/" + chunks.Count + "|" + F2(hostTime) + "|" + F2(waterY) + "|" + chunks[i]);
        return lines;
    }
}

// receiver side (FishFx host + guests): assembles SCHOOL parts, applies SFISH/SGONE, tracks the host clock offset
public class SchoolView
{
    public readonly Dictionary<int, SchoolFish> fish = new Dictionary<int, SchoolFish>();
    public float waterY; public bool haveWaterY, haveSchool;
    public float offset; public bool haveOffset;      // host time = local realtime + offset
    public int seq = -1, received;
    public readonly List<int> caughtIds = new List<int>();   // ids removed as "caught" since last poll (for splashes)
    Dictionary<int, SchoolFish> pending; int pendingSeq = -1, pendingNext;

    void Clock(string hostTime, float localNow)
    {
        float h;
        if (!float.TryParse(hostTime, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return;
        float s = h - localNow;
        // chat latency only ever makes the sample too small -> keep the largest; reset on a big jump (new host session)
        if (!haveOffset || s > offset || s < offset - 3f) { offset = s; haveOffset = true; }
    }

    // returns a short description for the log, or null if not a school line
    public string Handle(string plain, float localNow)
    {
        int i = plain.IndexOf("[FT] S");
        if (i < 0) return null;
        string body = plain.Substring(i + 5).Trim();
        string[] p = body.Split('|');
        try
        {
            if (p[0] == "SCHOOL" && p.Length >= 6)
            {
                int sq = int.Parse(p[1], CultureInfo.InvariantCulture);
                string[] pp = p[2].Split('/');
                int part = int.Parse(pp[0]), parts = int.Parse(pp[1]);
                Clock(p[3], localNow);
                float wy;
                if (float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out wy)) { waterY = wy; haveWaterY = true; }
                if (part == 1) { pending = new Dictionary<int, SchoolFish>(); pendingSeq = sq; pendingNext = 1; }
                if (pending == null || sq != pendingSeq || part != pendingNext) return "SCHOOL part " + part + "/" + parts + " seq " + sq + " ignored (out of order)";
                foreach (string e in p[5].Split(';'))
                {
                    if (e.Trim().Length == 0) continue;
                    SchoolFish f = SchoolFish.Decode(e.Trim());
                    if (f != null) pending[f.id] = f;
                }
                pendingNext++;
                if (part == parts)
                {
                    fish.Clear();
                    foreach (var kv in pending) fish[kv.Key] = kv.Value;
                    seq = sq; pending = null; haveSchool = true; received++;
                    return "SCHOOL seq " + sq + " complete: " + fish.Count + " fish, offset " + offset.ToString("0.00");
                }
                return "SCHOOL part " + part + "/" + parts;
            }
            if (p[0] == "SFISH" && p.Length >= 3)
            {
                Clock(p[1], localNow);
                SchoolFish f = SchoolFish.Decode(p[2].Trim());
                if (f == null) return "SFISH bad entry";
                fish[f.id] = f;
                return "SFISH " + f.id + " " + FishSchool.Species[f.sp];
            }
            if (p[0] == "SGONE" && p.Length >= 3)
            {
                Clock(p[1], localNow);
                int id = int.Parse(p[2], CultureInfo.InvariantCulture);
                fish.Remove(id);
                if (p.Length >= 4 && p[3].Trim() == "caught") caughtIds.Add(id);
                return "SGONE " + id + (p.Length >= 4 ? " " + p[3] : "");
            }
        }
        catch (Exception e) { return "school line parse error: " + e.Message; }
        return null;
    }
}
