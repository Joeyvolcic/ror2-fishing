// ROD SHOP part of the host fishing mod (compile with Fishing<N>.cs; Fishing25 is replaced by patch_rods.py).
//  Chat: !shop (catalogue + what you own), !buy <name>, !equip <name>, !rod (your current rod / skin / bobber).
//  Purchases spend tournament money (money[name]); ownership per player name, saved to rod_owners.json on the host
//  (survives game restarts) and carried over to the next Fishing<N> via RodSnapshot().
//  Rod types change fishing (host-authoritative): cast range, rare-fish luck, bite speed, reel window.
//  Skins are cosmetic: host draws them on the rod rigs; guests get "[FT] ROD|<name>|<rodType>|<rodSkin>|<bobberSkin>"
//  (on every change + every 30 s) and FISHING.exe draws the same look.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

public static partial class Fishing25
{
    public class RodItem
    {
        public string name, kind, desc;   // kind: rod / skin / bobber
        public int price;
        public float range = 1f, luck = 0f, bite = 1f, window = 1f;
    }
    public class RodOwner { public readonly List<string> owned = new List<string>(); public string rod = "Stick Rod", skin = "Default", bobber = "Plain"; }

    public static readonly List<RodItem> RodCatalog = new List<RodItem> {
        new RodItem { name = "Stick Rod",     kind = "rod", price = 0,     desc = "a crooked branch: -25% cast range, bites 20% slower, -20% reel window, -10% rare fish", range = 0.75f, luck = -0.10f, bite = 1.2f, window = 0.8f },
        new RodItem { name = "Basic Rod",     kind = "rod", price = 500,   desc = "a real rod: no bonus, no penalty" },
        new RodItem { name = "Long Cast Rod", kind = "rod", price = 2000,  desc = "+25% cast range", range = 1.25f },
        new RodItem { name = "Steady Rod",    kind = "rod", price = 3000,  desc = "+50% reel window", window = 1.5f },
        new RodItem { name = "Quick Rod",     kind = "rod", price = 3500,  desc = "bites 30% faster", bite = 0.7f },
        new RodItem { name = "Lucky Rod",     kind = "rod", price = 5000,  desc = "+20% rare fish", luck = 0.20f },
        new RodItem { name = "Golden Rod",    kind = "rod", price = 15000, desc = "+10% of everything", range = 1.10f, luck = 0.10f, bite = 0.9f, window = 1.10f },
        new RodItem { name = "Default",       kind = "skin", price = 0,    desc = "wood" },
        new RodItem { name = "Bamboo",        kind = "skin", price = 500,  desc = "bamboo" },
        new RodItem { name = "Carbon",        kind = "skin", price = 1000, desc = "black carbon" },
        new RodItem { name = "Neon",          kind = "skin", price = 1500, desc = "cyan/pink pulse" },
        new RodItem { name = "Gold",          kind = "skin", price = 2000, desc = "metallic gold" },
        new RodItem { name = "Lunar Glass",   kind = "skin", price = 2000, desc = "translucent blue" },
        new RodItem { name = "Void",          kind = "skin", price = 2500, desc = "purple glow" },
        new RodItem { name = "Plain",         kind = "bobber", price = 0,    desc = "red ball" },
        new RodItem { name = "Classic",       kind = "bobber", price = 500,  desc = "red/white" },
        new RodItem { name = "Star",          kind = "bobber", price = 1000, desc = "gold star" },
        new RodItem { name = "Duck",          kind = "bobber", price = 1250, desc = "rubber duck" },
        new RodItem { name = "Skull",         kind = "bobber", price = 1500, desc = "skull" },
        new RodItem { name = "Glow Bobber",    kind = "bobber", price = 2500, desc = "glows; at night bites 25% faster" },
        new RodItem { name = "Firefly Bobber", kind = "bobber", price = 4000, desc = "glows; at night +15% rare fish" },
        new RodItem { name = "Lantern Bobber", kind = "bobber", price = 6000, desc = "glows; at night bites 25% faster and +15% rare fish" },
        new RodItem { name = "Angler's Guide", kind = "item",  price = 250,  desc = "the fishing guide book - read it with G or !guide" } };

    public static readonly Dictionary<string, RodOwner> rodOwners = new Dictionary<string, RodOwner>();
    public static string rodSavePath = FishingPaths.Data("rod_owners.json");
    public static bool rodDryRun;                       // test: chat -> log only, no save, no broadcast
    public static string previewSkin, previewBobber, previewRod;    // test: host-local look on the LOCAL player's rig (never broadcast)
    static float rodBroadcastAt = -100f;
    static readonly string RodLogPath = Path.Combine(Path.GetTempPath(), "ror2_rods.log");
    static void LogR(string s) { try { File.AppendAllText(RodLogPath, DateTime.Now.ToString("HH:mm:ss ") + "Fishing25: " + s + Environment.NewLine); } catch { } }

    static string Money(int v) { return "$" + v.ToString("#,0", CultureInfo.InvariantCulture); }
    static RodItem Item(string n) { foreach (RodItem i in RodCatalog) if (i.name == n) return i; return null; }
    static RodOwner OwnerOf(string name) { RodOwner o; rodOwners.TryGetValue(name ?? "", out o); return o; }
    public static RodItem RodOf(string name) { RodOwner o = OwnerOf(name); RodItem i = o == null ? null : Item(o.rod); return i ?? RodCatalog[0]; }
    public static float RangeMult(string name) { return RodOf(name).range; }
    public static float LuckBonus(string name) { return RodOf(name).luck; }
    public static float BiteMult(string name) { return RodOf(name).bite; }
    public static float WindowMult(string name) { return RodOf(name).window; }
    static bool Owns(RodOwner o, RodItem i) { return i.price == 0 || (o != null && o.owned.Contains(i.name)); }
    static string Equipped(RodOwner o, string kind) { return kind == "rod" ? (o == null ? "Stick Rod" : o.rod) : kind == "skin" ? (o == null ? "Default" : o.skin) : (o == null ? "Plain" : o.bobber); }

    static void RodSay(string text) { if (rodDryRun) LogR("[dry-run chat] " + text); else Chat(text); }

    // ---------------------------------------------------------------- name lookup: "lucky", "Lucky Rod", "lunar glass", "duck bobber"
    static string Norm(string s) { var sb = new StringBuilder(); foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c); return sb.ToString(); }
    static string Strip(string n) { foreach (string suf in new[] { "bobber", "skin", "rod" }) if (n.Length > suf.Length && n.EndsWith(suf)) return n.Substring(0, n.Length - suf.Length); return n; }
    public static RodItem FindItem(string q)
    {
        string n = Norm(q); if (n.Length == 0) return null;
        if (n == "guide" || n == "book" || n == "guidebook") n = "anglersguide";
        foreach (RodItem i in RodCatalog) if (Norm(i.name) == n) return i;
        string s = Strip(n);
        foreach (RodItem i in RodCatalog) if (Strip(Norm(i.name)) == s) return i;
        RodItem hit = null; int hits = 0;
        foreach (RodItem i in RodCatalog) if (Norm(i.name).StartsWith(s)) { hit = i; hits++; }
        return hits == 1 ? hit : null;
    }

    // ---------------------------------------------------------------- chat commands (main thread)
    public static bool RodCommand(string name, string rawText)
    {
        string t = rawText.Trim(), lo = t.ToLowerInvariant();
        string arg = null, cmd = lo;
        int sp = lo.IndexOf(' ');
        if (sp > 0) { cmd = lo.Substring(0, sp); arg = t.Substring(sp + 1).Trim(); }
        try
        {
            if (cmd == "!shop" || cmd == "!rods") { Shop(name); return true; }
            if (cmd == "!rod" && arg == null) { ShowRod(name); return true; }
            if (cmd == "!buy") { Buy(name, arg); return true; }
            if (cmd == "!equip") { Equip(name, arg); return true; }
        }
        catch (Exception e) { LogR("command error (" + name + ": " + rawText + "): " + e); }
        return false;
    }

    static void Shop(string name)
    {
        RodOwner o = OwnerOf(name);
        int m; money.TryGetValue(name, out m);
        foreach (string kind in new[] { "rod", "skin", "bobber", "item" })
        {
            var parts = new List<string>();
            string eq = Equipped(o, kind);
            foreach (RodItem i in RodCatalog)
            {
                if (i.kind != kind) continue;
                string tag = i.name == eq ? " <color=#ffd700>[equipped]</color>" : Owns(o, i) ? " <color=#88ff88>[owned]</color>" : " " + Money(i.price);
                parts.Add(i.name + (kind == "rod" && i.price > 0 ? " (" + i.desc + ")" : "") + tag);
            }
            RodSay((kind == "rod" ? "RODS: " : kind == "skin" ? "ROD SKINS: " : kind == "item" ? "ITEMS: " : "BOBBERS: ") + string.Join(" | ", parts.ToArray()));
        }
        RodSay(name + " has " + Money(m) + " to spend. Type !buy <name>, !equip <name>, !rod");
        LogR(name + ": !shop (money " + m + ")");
    }

    static void ShowRod(string name)
    {
        RodOwner o = OwnerOf(name); RodItem r = RodOf(name);
        RodSay(name + "'s rod: " + r.name + (r.price > 0 ? " (" + r.desc + ")" : "") + ", skin " + Equipped(o, "skin") + ", bobber " + Equipped(o, "bobber"));
    }

    static RodOwner MakeOwner(string name) { RodOwner o = OwnerOf(name); if (o == null) { o = new RodOwner(); rodOwners[name] = o; } return o; }

    static void Buy(string name, string arg)
    {
        { string gate = StandGate(name); if (gate != null) { RodSay(gate); LogR(name + ": !buy " + arg + " refused (not at the rod stand)"); return; } }
        RodItem i = FindItem(arg);
        if (i == null) { RodSay(arg == null ? "Usage: !buy <name> (type !shop for the list)" : "No rod, skin or bobber called \"" + arg + "\" - type !shop for the list."); return; }
        RodOwner o = OwnerOf(name);
        if (Owns(o, i)) { RodSay(name + " already owns the " + Label(i) + " - type !equip " + i.name); return; }
        int m; money.TryGetValue(name, out m);
        if (m < i.price) { RodSay(name + " needs " + Money(i.price) + " for the " + Label(i) + " (has " + Money(m) + "). Keep fishing!"); LogR(name + ": buy " + i.name + " REFUSED (money " + m + " < " + i.price + ")"); return; }
        money[name] = m - i.price;
        o = MakeOwner(name);
        o.owned.Add(i.name);
        SetEquipped(o, i);
        RodSay(name + " bought the " + Label(i) + "!" + (i.kind == "rod" ? " (" + i.desc + ")" : "") + " -" + Money(i.price) + ", " + Money(m - i.price) + " left." + (i.kind == "item" ? (i.name == "Angler's Guide" ? " Press G or type !guide to read it." : "") : " Equipped."));
        LogR(name + ": BOUGHT " + i.name + " (" + i.kind + ") for " + i.price + ", money " + m + " -> " + (m - i.price));
        Changed(name, true);
    }

    static void Equip(string name, string arg)
    {
        RodItem i = FindItem(arg);
        if (i == null) { RodSay(arg == null ? "Usage: !equip <name>" : "No rod, skin or bobber called \"" + arg + "\" - type !shop for the list."); return; }
        RodOwner o = OwnerOf(name);
        if (!Owns(o, i)) { RodSay(name + " doesn't own the " + Label(i) + " yet - !buy " + i.name + " for " + Money(i.price)); return; }
        if (i.kind == "item") { RodSay(name + ": the " + i.name + " doesn't need equipping."); return; }
        if (Equipped(o, i.kind) == i.name) { RodSay(name + " already has the " + Label(i) + " equipped."); return; }
        o = MakeOwner(name);
        SetEquipped(o, i);
        RodSay(name + " equipped the " + Label(i) + "!");
        LogR(name + ": equipped " + i.name + " (" + i.kind + ")");
        Changed(name, false);
    }

    static string Label(RodItem i) { return i.kind == "rod" || i.kind == "item" ? i.name : i.kind == "skin" ? i.name + " rod skin" : i.name.EndsWith("Bobber") ? i.name : i.name + " bobber"; }
    public static bool OwnsItem(string name, string item) { RodItem i = Item(item); return i != null && Owns(OwnerOf(name), i); }
    static void SetEquipped(RodOwner o, RodItem i) { if (i.kind == "rod") o.rod = i.name; else if (i.kind == "skin") o.skin = i.name; else if (i.kind == "bobber") o.bobber = i.name; }

    static void Changed(string name, bool moneyChanged)
    {
        if (rodDryRun) return;
        SaveRods();
        BroadcastRod(name);
        if (moneyChanged) { int m; money.TryGetValue(name, out m); ChatRaw("[FT] MONEY|" + name + "|" + m); Board(); }
    }

    static void BroadcastRod(string name)
    {
        RodOwner o = OwnerOf(name);
        ChatRaw("[FT] ROD|" + name + "|" + Equipped(o, "rod") + "|" + Equipped(o, "skin") + "|" + Equipped(o, "bobber"));
    }

    // every 0.5 s from Tick: everyone's loadout every 30 s (guests that joined late / missed a line)
    static void RodsTick()
    {
        if (now - rodBroadcastAt < 30f) return;
        rodBroadcastAt = now;
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                string n = Convert.ToString(Get(nu, "userName"));
                if (!string.IsNullOrEmpty(n)) BroadcastRod(n);
            }
        }
        catch (Exception e) { LogR("rods tick error: " + e.Message); }
    }

    // ---------------------------------------------------------------- gameplay hooks
    // catch tier roll (random bites): red / lunar weights x (1 + luck), white/green bands shrink
    static void LuckOdds(string name, ref double pL, ref double pR)
    {
        float l = LuckBonus(name) + GlowLuck(name);
        if (Math.Abs(l) < 0.0001f) return;
        double L = pL * (1 + l), R = L + (pR - pL) * (1 + l);
        LogR(name + ": " + RodOf(name).name + " tier odds lunar " + (pL * 100).ToString("0.0") + "->" + (L * 100).ToString("0.0") + "%, red " + ((pR - pL) * 100).ToString("0.0") + "->" + ((R - L) * 100).ToString("0.0") + "%");
        pL = L; pR = R;
    }

    // school cast: with chance = luck, a free red/lunar fish within interest range takes the bait instead of the nearest
    static SchoolFish LuckyPick(string name, SchoolFish best, ref float bestD, float simLand, float bx, float bz)
    {
        float l = LuckBonus(name) + GlowLuck(name);
        if (l <= 0f || best == null) return best;
        string tier = FishSchool.TierOf(FishSchool.Species[best.sp]);
        if (tier == "Tier3" || tier == "Lunar") return best;
        SchoolFish rare = null; float rd = float.MaxValue;
        foreach (SchoolFish f in school.Values)
        {
            if (f.busy != null || f == best || !SameRegion(f, FishRegion(best))) continue;
            string ft = FishSchool.TierOf(FishSchool.Species[f.sp]);
            if (ft != "Tier3" && ft != "Lunar") continue;
            float x, z; f.Pos(simLand, out x, out z);
            float d = (float)Math.Sqrt((x - bx) * (x - bx) + (z - bz) * (z - bz));
            if (d <= InterestRange && d < rd) { rd = d; rare = f; }
        }
        if (rare == null) return best;
        double roll = rng.NextDouble();
        bool take = roll < l;
        LogR(name + ": " + RodOf(name).name + " luck: rare #" + rare.id + " " + FishSchool.Species[rare.sp] + " at " + rd.ToString("0.0") + " m vs nearest " + FishSchool.Species[best.sp]
             + " -> roll " + (roll * 100).ToString("0") + " vs " + (l * 100).ToString("0") + "% " + (take ? "RARE FISH BITES" : "no"));
        if (!take) return best;
        bestD = rd; return rare;
    }

    // after SchoolOnCast set the bite time: Quick / Golden rods shorten the wait after the bobber lands
    static void RodBite(string name, Angler a)
    {
        float m = BiteMult(name) * GlowBite(name);
        if (Math.Abs(m - 1f) < 0.001f) return;
        Hook h; hooks.TryGetValue(name, out h);
        Rig r; rigs.TryGetValue(name, out r);
        float land = now + (r != null ? r.flight : 0.6f), before = a.biteAt - land;
        if (h != null && h.fishId >= 0) { h.delay *= m; a.biteAt = h.activateAt + h.delay; }
        else if (a.biteAt < float.MaxValue / 2) a.biteAt = land + (a.biteAt - land) * m;
        else return;
        LogR(name + ": " + RodOf(name).name + (GlowBite(name) < 1f ? " + night glow bobber" : "") + " bite wait " + before.ToString("0.00") + " s -> " + (a.biteAt - land).ToString("0.00") + " s after landing");
    }

    // ---------------------------------------------------------------- persistence
    public static string RodSnapshot() { return RodsJson(); }

    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static string RodsJson()
    {
        var sb = new StringBuilder("{\n");
        int k = 0;
        foreach (var kv in rodOwners)
        {
            var owned = new List<string>(); foreach (string s in kv.Value.owned) owned.Add(Q(s));
            sb.Append("  " + Q(kv.Key) + ": { \"owned\": [" + string.Join(", ", owned.ToArray()) + "], \"rod\": " + Q(kv.Value.rod) + ", \"skin\": " + Q(kv.Value.skin) + ", \"bobber\": " + Q(kv.Value.bobber) + " }"
                      + (++k < rodOwners.Count ? "," : "") + "\n");
        }
        return sb.Append("}\n").ToString();
    }

    static void SaveRods()
    {
        try
        {
            string tmp = rodSavePath + ".tmp";
            File.WriteAllText(tmp, RodsJson());
            if (File.Exists(rodSavePath)) File.Delete(rodSavePath);
            File.Move(tmp, rodSavePath);
            LogR("saved " + rodOwners.Count + " owner(s) to " + rodSavePath);
        }
        catch (Exception e) { LogR("save error: " + e.Message); }
    }

    static void LoadRodsJson(string json, string from)
    {
        int p = 0;
        var root = JParse(json, ref p) as Dictionary<string, object>;
        if (root == null) { LogR("rods: nothing usable in " + from); return; }
        rodOwners.Clear();
        foreach (var kv in root)
        {
            var d = kv.Value as Dictionary<string, object>; if (d == null) continue;
            var o = new RodOwner();
            object v;
            if (d.TryGetValue("owned", out v) && v is List<object>) foreach (object s in (List<object>)v) { RodItem i = Item(s as string); if (i != null && !o.owned.Contains(i.name)) o.owned.Add(i.name); }
            foreach (string f in new[] { "rod", "skin", "bobber" })
                if (d.TryGetValue(f, out v) && v is string)
                {
                    RodItem i = Item((string)v);
                    if (i != null && i.kind == f && Owns(o, i)) SetEquipped(o, i);
                }
            rodOwners[kv.Key] = o;
        }
        var parts = new List<string>();
        foreach (var kv in rodOwners) parts.Add(kv.Key + "=" + kv.Value.rod + "/" + kv.Value.skin + "/" + kv.Value.bobber + " owns [" + string.Join(", ", kv.Value.owned.ToArray()) + "]");
        LogR("rods loaded from " + from + ": " + (parts.Count == 0 ? "(none)" : string.Join("; ", parts.ToArray())));
    }

    // called from Run() after CarryOver: older version's live table first, else the save file
    static void RodsInit(Type prev)
    {
        try
        {
            MethodInfo snap = prev == null ? null : prev.GetMethod("RodSnapshot", All);
            if (snap != null) { LoadRodsJson((string)snap.Invoke(null, null), prev.Name + ".RodSnapshot"); SaveRods(); }
            else if (File.Exists(rodSavePath)) LoadRodsJson(File.ReadAllText(rodSavePath), rodSavePath);
            else LogR("rods: no previous owners (" + (prev == null ? "no older version" : prev.Name + " has no rod shop") + ", no " + rodSavePath + ")");
            StickMigration();
        }
        catch (Exception e) { LogR("rods init error: " + (e.InnerException ?? e)); }
    }

    // minimal JSON reader: objects, arrays, strings, numbers/literals (as strings)
    static object JParse(string s, ref int p)
    {
        JWs(s, ref p);
        if (p >= s.Length) return null;
        char c = s[p];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); p++;
            while (true)
            {
                JWs(s, ref p); if (p >= s.Length) return d;
                if (s[p] == '}') { p++; return d; }
                if (s[p] == ',') { p++; continue; }
                string key = JParse(s, ref p) as string;
                JWs(s, ref p); if (p < s.Length && s[p] == ':') p++;
                object val = JParse(s, ref p);
                if (key != null) d[key] = val;
            }
        }
        if (c == '[')
        {
            var l = new List<object>(); p++;
            while (true)
            {
                JWs(s, ref p); if (p >= s.Length) return l;
                if (s[p] == ']') { p++; return l; }
                if (s[p] == ',') { p++; continue; }
                l.Add(JParse(s, ref p));
            }
        }
        if (c == '"')
        {
            var sb = new StringBuilder(); p++;
            while (p < s.Length && s[p] != '"')
            {
                if (s[p] == '\\' && p + 1 < s.Length)
                {
                    p++; char e = s[p];
                    if (e == 'n') sb.Append('\n'); else if (e == 't') sb.Append('\t');
                    else if (e == 'u' && p + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(p + 1, 4), 16)); p += 4; }
                    else sb.Append(e);
                }
                else sb.Append(s[p]);
                p++;
            }
            p++;
            return sb.ToString();
        }
        int st = p;
        while (p < s.Length && ",}] \t\r\n".IndexOf(s[p]) < 0) p++;
        return s.Substring(st, Math.Max(0, p - st));
    }
    static void JWs(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    // ---------------------------------------------------------------- skins on the host's rod rigs (every frame, after Render placed them)
    static UnityEngine.Shader litShader, unlitShader;
    static void RodSkinsFrame()
    {
        if (rigs.Count == 0) return;
        string local = hudLocalName;
        float t = T;
        foreach (var kv in rigs)
        {
            Rig r = kv.Value;
            try
            {
                var pole = r.pole as UnityEngine.GameObject; var bob = r.bobber as UnityEngine.GameObject;
                if (pole == null || bob == null) continue;
                RodOwner o = OwnerOf(kv.Key);
                string skin = Equipped(o, "skin"), bobber = Equipped(o, "bobber");
                if (kv.Key == local && previewSkin != null) skin = previewSkin;
                if (kv.Key == local && previewBobber != null) bobber = previewBobber;
                string rodType = Equipped(o, "rod"); if (kv.Key == local && previewRod != null) rodType = previewRod;
                string key = skin + "|" + bobber + "|" + rodType;
                if (r.skinKey != key) { ApplySkin(kv.Key, r, pole, bob, skin, bobber); r.skinKey = key; RodTypeLook.Apply(pole, rodType); }
                RodTypeLook.Tick(pole);
                // animated looks
                if (r.skinMat != null)
                {
                    if (skin == "Neon")
                    {
                        float k = 0.5f + 0.5f * (float)Math.Sin(t * 3.0);
                        var c = UnityEngine.Color.Lerp(new UnityEngine.Color(0f, 1f, 1f), new UnityEngine.Color(1f, 0.15f, 0.8f), k);
                        r.skinMat.color = c; r.skinMat.SetColor("_EmissionColor", c * 2.2f);
                        var line = r.line as UnityEngine.LineRenderer; if (line != null) { line.startColor = c; line.endColor = c; }
                    }
                    else if (skin == "Void")
                    {
                        float k = 0.75f + 0.45f * (float)Math.Sin(t * 2.2);
                        r.skinMat.SetColor("_EmissionColor", new UnityEngine.Color(0.45f, 0.08f, 0.9f) * k);
                        if (r.glowMat != null) r.glowMat.color = new UnityEngine.Color(0.55f, 0.1f, 1f, 0.12f + 0.1f * k);
                    }
                }
                if (bobber == "Duck" || bobber == "Skull")
                {   // face the angler
                    var body = r.body as UnityEngine.Component;
                    if (body != null)
                    {
                        UnityEngine.Vector3 d = body.transform.position - bob.transform.position; d.y = 0;
                        if (d.sqrMagnitude > 0.01f) bob.transform.rotation = UnityEngine.Quaternion.LookRotation(d.normalized);
                    }
                }
                else if (bobber == "Star") bob.transform.rotation = UnityEngine.Quaternion.Euler(0, t * 90f, 0);
            }
            catch (Exception e) { if (rigErrors++ < 10) LogR("skin frame error (" + kv.Key + "): " + e.Message); }
        }
    }

    static UnityEngine.Material NewMat(UnityEngine.Color c, float metal, float gloss, UnityEngine.Color? emit)
    {
        var m = new UnityEngine.Material(litShader);
        m.color = c;
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
        if (emit.HasValue) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emit.Value); m.globalIlluminationFlags = UnityEngine.MaterialGlobalIlluminationFlags.RealtimeEmissive; }
        return m;
    }
    static UnityEngine.Material GlassMat(UnityEngine.Color c)
    {
        var m = new UnityEngine.Material(unlitShader ?? litShader); m.color = c; m.renderQueue = 3001; return m;
    }

    static void Deco(UnityEngine.GameObject parent, string kind, UnityEngine.Vector3 pos, UnityEngine.Vector3 scale, UnityEngine.Vector3 euler, UnityEngine.Material mat)
    {
        var go = (UnityEngine.GameObject)Prim(kind, "RodSkinDeco", 1f, 1f, 1f, Col(1, 1, 1));
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale; go.transform.localRotation = UnityEngine.Quaternion.Euler(euler);
        var rend = go.GetComponent<UnityEngine.Renderer>();
        rend.sharedMaterial = mat; rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rend.receiveShadows = false;
    }

    static void ClearDeco(UnityEngine.GameObject go)
    {
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            var c = go.transform.GetChild(i);
            if (c.name == "RodSkinDeco") { c.name = "RodSkinDeco_old"; c.SetParent(null); UnityEngine.Object.Destroy(c.gameObject); }
        }
    }

    static UnityEngine.Vector3 V3(float x, float y, float z) { return new UnityEngine.Vector3(x, y, z); }
    static UnityEngine.Color C(float r, float g, float b) { return new UnityEngine.Color(r, g, b); }

    static void ApplySkin(string who, Rig r, UnityEngine.GameObject pole, UnityEngine.GameObject bob, string skin, string bobber)
    {
        var pr = pole.GetComponent<UnityEngine.Renderer>(); var br = bob.GetComponent<UnityEngine.Renderer>();
        if (litShader == null)
        {
            litShader = UnityEngine.Shader.Find("Standard");
            if (litShader == null && pr != null) litShader = pr.sharedMaterial.shader;
            unlitShader = UnityEngine.Shader.Find("Sprites/Default");
            LogR("skin shaders: lit=" + (litShader == null ? "none" : litShader.name) + " unlit=" + (unlitShader == null ? "none" : unlitShader.name));
        }
        if (r.skinMats != null) foreach (var m in r.skinMats) if (m != null) UnityEngine.Object.Destroy(m);
        r.skinMats = new List<UnityEngine.Material>();
        r.skinMat = null; r.glowMat = null;
        ClearDeco(pole); ClearDeco(bob);
        var V0 = UnityEngine.Vector3.zero;

        // ---- rod
        UnityEngine.Material pm;
        switch (skin)
        {
            case "Bamboo":
                pm = NewMat(C(0.80f, 0.72f, 0.38f), 0f, 0.35f, null);
                var node = NewMat(C(0.48f, 0.40f, 0.16f), 0f, 0.3f, null); r.skinMats.Add(node);
                foreach (float y in new[] { -0.72f, -0.3f, 0.12f, 0.52f, 0.86f }) Deco(pole, "Cylinder", V3(0, y, 0), V3(1.3f, 0.03f, 1.3f), V0, node);
                break;
            case "Carbon":
                pm = NewMat(C(0.05f, 0.05f, 0.06f), 0.4f, 0.92f, null);
                var grip = NewMat(C(0.15f, 0.15f, 0.16f), 0f, 0.2f, null); r.skinMats.Add(grip);
                var accent = NewMat(C(0.9f, 0.1f, 0.1f), 0.3f, 0.8f, C(0.4f, 0.02f, 0.02f)); r.skinMats.Add(accent);
                Deco(pole, "Cylinder", V3(0, -0.78f, 0), V3(1.45f, 0.18f, 1.45f), V0, grip);
                Deco(pole, "Cylinder", V3(0, -0.56f, 0), V3(1.25f, 0.02f, 1.25f), V0, accent);
                break;
            case "Gold":
                pm = NewMat(C(1f, 0.78f, 0.22f), 1f, 0.85f, C(0.35f, 0.24f, 0.04f));
                var gold2 = NewMat(C(1f, 0.9f, 0.5f), 1f, 0.95f, C(0.6f, 0.45f, 0.1f)); r.skinMats.Add(gold2);
                Deco(pole, "Cylinder", V3(0, -0.8f, 0), V3(1.4f, 0.12f, 1.4f), V0, gold2);
                Deco(pole, "Sphere", V3(0, 1f, 0), V3(1.5f, 0.05f, 1.5f), V0, gold2);
                break;
            case "Neon":
                pm = NewMat(C(0f, 1f, 1f), 0f, 0.6f, C(0f, 2f, 2f));
                break;
            case "Lunar Glass":
                pm = GlassMat(new UnityEngine.Color(0.5f, 0.78f, 1f, 0.42f));
                var core = GlassMat(new UnityEngine.Color(0.8f, 0.95f, 1f, 0.55f)); r.skinMats.Add(core);
                Deco(pole, "Cylinder", V0, V3(0.35f, 1f, 0.35f), V0, core);
                break;
            case "Void":
                pm = NewMat(C(0.16f, 0.03f, 0.28f), 0.2f, 0.7f, C(0.45f, 0.08f, 0.9f));
                r.glowMat = GlassMat(new UnityEngine.Color(0.55f, 0.1f, 1f, 0.2f)); r.skinMats.Add(r.glowMat);
                Deco(pole, "Cylinder", V0, V3(2.2f, 1.02f, 2.2f), V0, r.glowMat);
                break;
            default:
                pm = NewMat(C(0.45f, 0.27f, 0.10f), 0f, 0.3f, null);
                break;
        }
        r.skinMats.Add(pm); r.skinMat = pm;
        if (pr != null) pr.sharedMaterial = pm;
        var line = r.line as UnityEngine.LineRenderer;
        if (line != null && skin != "Neon")
        {
            var lc = skin == "Gold" ? C(1f, 0.9f, 0.5f) : skin == "Void" ? C(0.75f, 0.45f, 1f) : skin == "Lunar Glass" ? C(0.7f, 0.9f, 1f) : C(1, 1, 1);
            line.startColor = lc; line.endColor = lc;
        }

        // ---- bobber (root = 0.25 m sphere; children in its local units)
        UnityEngine.Material bm;
        switch (bobber)
        {
            case "Classic":
                bm = NewMat(C(0.97f, 0.97f, 0.97f), 0f, 0.6f, null);
                var red = NewMat(C(0.9f, 0.06f, 0.06f), 0f, 0.6f, null); r.skinMats.Add(red);
                Deco(bob, "Sphere", V3(0, 0.23f, 0), V3(1.04f, 0.56f, 1.04f), V0, red);
                Deco(bob, "Cylinder", V3(0, 0.72f, 0), V3(0.1f, 0.25f, 0.1f), V0, red);
                break;
            case "Duck":
                bm = NewMat(C(1f, 0.85f, 0.1f), 0f, 0.5f, null);
                var beak = NewMat(C(1f, 0.45f, 0.05f), 0f, 0.4f, null); r.skinMats.Add(beak);
                var eye = NewMat(C(0.02f, 0.02f, 0.02f), 0f, 0.9f, null); r.skinMats.Add(eye);
                Deco(bob, "Sphere", V3(0, 0.55f, 0.28f), V3(0.62f, 0.62f, 0.62f), V0, bm);
                Deco(bob, "Cube", V3(0, 0.5f, 0.66f), V3(0.3f, 0.1f, 0.3f), V0, beak);
                Deco(bob, "Sphere", V3(0.15f, 0.66f, 0.52f), V3(0.1f, 0.1f, 0.1f), V0, eye);
                Deco(bob, "Sphere", V3(-0.15f, 0.66f, 0.52f), V3(0.1f, 0.1f, 0.1f), V0, eye);
                Deco(bob, "Cube", V3(0, 0.25f, -0.5f), V3(0.3f, 0.22f, 0.25f), V3(-30f, 0, 0), bm);
                break;
            case "Skull":
                bm = NewMat(C(0.93f, 0.9f, 0.8f), 0f, 0.3f, null);
                var hole = NewMat(C(0.02f, 0.02f, 0.02f), 0f, 0.1f, null); r.skinMats.Add(hole);
                Deco(bob, "Sphere", V3(0.19f, 0.08f, 0.38f), V3(0.27f, 0.27f, 0.27f), V0, hole);
                Deco(bob, "Sphere", V3(-0.19f, 0.08f, 0.38f), V3(0.27f, 0.27f, 0.27f), V0, hole);
                Deco(bob, "Sphere", V3(0, -0.12f, 0.46f), V3(0.11f, 0.13f, 0.11f), V0, hole);
                Deco(bob, "Cube", V3(0, -0.38f, 0.12f), V3(0.55f, 0.24f, 0.55f), V0, bm);
                break;
            case "Star":
                bm = NewMat(C(1f, 0.85f, 0.15f), 0.6f, 0.8f, C(0.9f, 0.7f, 0.1f));
                for (int i = 0; i < 5; i++) Deco(bob, "Cube", V0, V3(0.22f, 1.75f, 0.22f), V3(0, 0, i * 36f), bm);
                break;
            default:
                bm = NewMat(C(0.95f, 0.1f, 0.1f), 0f, 0.5f, null);
                break;
        }
        r.skinMats.Add(bm);
        if (br != null) br.sharedMaterial = bm;
        if (bobber == "Plain" || bobber == "Classic") bob.transform.rotation = UnityEngine.Quaternion.identity;
        LogR(who + ": rig skinned " + skin + " / " + bobber + " bobber");
    }
}
