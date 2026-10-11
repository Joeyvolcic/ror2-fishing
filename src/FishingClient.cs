// Risk of Rain 2 - fishing VISUALS for guests (client side only).
//
// The host runs the actual fishing feature (casts, bites, catches, item drops) and announces
// everything in chat. This payload only listens to those chat lines and draws a rod, line and
// bobber for each player who is fishing, plus a translucent water sheet, on THIS PC.
// It changes no gameplay, sends nothing over the network, and does nothing at all if this
// game is the host (NetworkServer.active) or if the host isn't running the fishing feature.
//
// Build:  csc -target:library -out:FishingClient.dll FishingClient.cs
// Entry:  FishingClient.Run()   (safe to call any number of times)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
#if TESTMODE
using Owner = FishingClientTest;
#else
using Owner = FishingClient;
#endif

// IMGUI overlay for the fishing tournament (right side of the screen). All state lives in Owner.
#if TESTMODE
public class FishHudTest : UnityEngine.MonoBehaviour
#else
public class FishHud : UnityEngine.MonoBehaviour
#endif
{
    void OnGUI() { Owner.DrawHud(); }
}

#if TESTMODE
public static class FishingClientTest
#else
public static class FishingClient
#endif
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_fishing_client.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
#if TESTMODE
    const string Tag = "[TEST] ";
#else
    const string Tag = "";
#endif

    public static bool enabled = true;
    public static bool installed;
    public static int runCount;

    static Assembly ror2, core;
    static Type netServer, v3, uObj, chatType, nuType, runType;
    static int errors;

    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Tag + s + Environment.NewLine); } catch { } }

    // ---------------------------------------------------------------- reflection helpers
    static object Get(object o, string name)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(name, All);
        return f != null ? f.GetValue(o) : null;
    }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }
    static void Set(object o, string name, object value)
    {
        FieldInfo f = o.GetType().GetField(name, All);
        if (f != null) { f.SetValue(o, value); return; }
        o.GetType().GetProperty(name, All).SetValue(o, value, null);
    }
    static bool IsNull(object o) { return o == null || (bool)uObj.GetMethod("op_Equality").Invoke(null, new object[] { o, null }); }
    static object Vec(float x, float y, float z) { return Activator.CreateInstance(v3, new object[] { x, y, z }); }
    static float F(object vec, string c) { return (float)v3.GetField(c).GetValue(vec); }
    static bool IsHost() { return (bool)netServer.GetProperty("active").GetValue(null, null); }

    // ---------------------------------------------------------------- entry point
    public static void Run()
    {
        runCount++;
        try
        {
            Log("Run() call #" + runCount + " (payload " + typeof(Self).Assembly.GetName().Name + ", installed=" + installed + ")");
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                if (a.GetName().Name == "UnityEngine.CoreModule") core = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer");
                if (t != null) netServer = t;
            }
            if (ror2 == null || core == null || netServer == null) { Log("game assemblies not found (ror2=" + (ror2 != null) + ", core=" + (core != null) + ", net=" + (netServer != null) + ")"); return; }
            v3 = core.GetType("UnityEngine.Vector3");
            uObj = core.GetType("UnityEngine.Object");
            chatType = ror2.GetType("RoR2.Chat");
            nuType = ror2.GetType("RoR2.NetworkUser");
            runType = ror2.GetType("RoR2.Run");

#if !TESTMODE
            if (IsHost()) { Log("host detected, client visuals not needed"); return; }
#endif
            // If a copy of this payload was loaded as a separate assembly and is already running, don't double up.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(Self).Assembly) continue;
                Type other = null;
                try { other = asm.GetType(typeof(Self).DeclaringType.FullName); } catch { }
                if (other == null) continue;
                FieldInfo inst = other.GetField("installed", BindingFlags.Public | BindingFlags.Static);
                FieldInfo en = other.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (inst != null && (bool)inst.GetValue(null) && en != null && (bool)en.GetValue(null))
                {
                    // an OLDER copy (from an earlier FISHING.exe in this game session) is replaced so new features work
                    // without restarting the game; the same or a newer version stays and this copy does nothing
                    FieldInfo vf = other.GetField("version", BindingFlags.Public | BindingFlags.Static);
                    int ov = vf != null && vf.FieldType == typeof(int) ? (int)vf.GetValue(null) : 1;
                    if (ov >= version) { Log("another copy (version " + ov + ") is already running - nothing to do"); return; }
                    en.SetValue(null, false);   // it removes its rigs/water/HUD on its next frame
                    TakeStateFrom(other);
                    Log("replaced an older copy (version " + ov + " -> " + version + ")");
                }
            }

            InitTypes();
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available (" + (cc == null ? "none" : cc.EventHandlerType.Name) + ")"); return; }
            Action c = OnChatChanged; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            if (upd == null) upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All);
            Action u = Update; upd.AddEventHandler(null, u);
            RodHandAttach.Late += LateRender;   // rods are drawn in LateUpdate so they follow the animated hand
            CastAnimLook.SplashType = "FishFxClient";   // splash rings from the guest FishFx build
            CastAnimLook.Reseat = (b, p) => RodHandAttach.Attach(b, p, false, false, false);
            installed = true;
            try { FishingSwimLook.Install("[client] ", () => enabled, false); } catch (Exception e) { Log("swim install error: " + e.Message); }
            GameplayClient.Log = Log;                                   // eat buffs, weather, golden hotspot, pools, lures
            DayNightLook.Log = s => Log("[daynight] " + s);             // day/night lighting from [FT] TIME
            FishingFlashlight.Log = s => Log("[flashlight] " + s);      // L = local flashlight
            FishingPetLines.Log = Log;                                  // drone pets' lines
            FishingZoneCues.Log = Log;                                  // fishing zone cues (reeds / lilies / buoy / ice)
            Log("fishing client installed: chat hook ok, frame hook=" + upd.Name + ", host=" + IsHost());
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    class Self { }   // marker: typeof(Self).DeclaringType is this class whatever it's compiled as

    // 2 = handles [FT] WHIP (rod whip, host Fishing16+); 3 = [FT] GROUND bounce-back + 14 species perk texts;
    // 4 = mod 2.0: teleporter-sparkle hotspot, bigger rods + Mercenary-style whip swing, native lake water, panel button;
    // a newer copy replaces an older running one
    // 5 = mod 2.1: rod in the hand + cast animation, underwater view, swimming, eat buffs / weather / golden hotspot / pools /
    //     lures, day/night lighting, glow bobbers + rod type models, flashlight (L)
    // 6 = mod 3.0 (renumbered 2.1 feature set; payloads renamed _v30)
    public static int version = 6;
    public const string ModVersion = "3.0";
    public const string ReleaseUrl = "https://github.com/Joeyvolcic/ror2-fishing/releases/latest";

    static void TakeStateFrom(Type other)   // tournament HUD data + hotspot of the replaced copy (managed data only)
    {
        try
        {
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (string n in new[] { "board", "myColl", "perks", "loadouts" })
            {
                FieldInfo of = other.GetField(n, S), nf = typeof(Self).DeclaringType.GetField(n, S);
                IDictionary od = of == null ? null : of.GetValue(null) as IDictionary, nd = nf == null ? null : nf.GetValue(null) as IDictionary;
                if (od == null || nd == null) continue;
                foreach (DictionaryEntry e in od) nd[e.Key] = e.Value;
            }
            FieldInfo oc = other.GetField("collOrder", S);
            IList ol = oc == null ? null : oc.GetValue(null) as IList;
            if (ol != null) foreach (object o in ol) if (!collOrder.Contains((string)o)) collOrder.Add((string)o);
            foreach (string n in new[] { "myMoney", "tourney", "hasHot", "hotX", "hotY", "hotZ", "hotR", "hotScene" })
            {
                FieldInfo of = other.GetField(n, S), nf = typeof(Self).DeclaringType.GetField(n, S);
                if (of != null && nf != null && of.FieldType == nf.FieldType && !nf.IsInitOnly) nf.SetValue(null, of.GetValue(null));
            }
            Log("took over HUD state: money $" + myMoney + ", " + board.Count + " on board, " + collOrder.Count + " species");
        }
        catch (Exception e) { Log("take-over error: " + e.Message); }
    }

    // ---------------------------------------------------------------- chat -> angler state
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine;

    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            lock (chatQueue) chatQueue.Add(line);
        }
        catch { }
    }

    // state: 0 idle, 1 waiting (line in the water), 2 biting
    class Angler { public int state; public float since; }
    static readonly Dictionary<string, Angler> anglers = new Dictionary<string, Angler>();

    static List<string> PlayerNames()
    {
        var l = new List<string>();
        foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
        {
            string n = Convert.ToString(Get(nu, "userName"));
            if (!string.IsNullOrEmpty(n)) l.Add(n);
        }
        l.Sort((a, b) => b.Length.CompareTo(a.Length));   // longest first, so "Sam the Angler" beats "Sam"
        return l;
    }

    // Parses one chat line. Returns "cast"/"bite"/"reel" and the angler name, or null.
    // Only lines broadcast by the host's fishing feature count: they are system messages
    // wrapped in <color=#66ccff>; anything typed by a player ("Name: ...") is ignored.
    public static string Parse(string raw, List<string> names, out string who)
    {
        who = null;
        if (raw == null) return null;
        string s = Regex.Replace(raw, "<[^>]*>", "").Trim();
        if (s.StartsWith("[FT]")) return null;   // tournament data lines are for the HUD only

        if (s.StartsWith("Something's biting! ")) { who = ""; return "bite"; }   // the host's own F-key cast (no name)
        if (s.StartsWith("Something's biting "))
        {
            foreach (string n in names) if (s.StartsWith("Something's biting " + n + "'s line")) { who = n; return "bite"; }
            return null;
        }
        if (s.StartsWith("The fish got away from "))
        {
            foreach (string n in names) if (s == "The fish got away from " + n + "!") { who = n; return "reel"; }
            return null;
        }
        foreach (string n in names)
        {
            if (!s.StartsWith(n + " ")) continue;
            string rest = s.Substring(n.Length + 1);
            if (rest.StartsWith(": ")) return null;
            who = n;
            if (rest.StartsWith("casts a line...") || rest.StartsWith("is auto-fishing...")) return "cast";   // host may append " right into the hotspot!"
            if (rest.StartsWith("caught ") || rest.StartsWith("reeled in ") || rest.StartsWith("hooked a ")
                || rest.StartsWith("yanked too early") || rest.EndsWith("stopped auto-fishing.")) return "reel";
            who = null;
            return null;
        }
        return null;
    }

    static void HandleLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        if (plain.StartsWith("[FT] "))
        {
#if TESTMODE
            if (!testInjecting) { Log("real [FT] line seen (ignored in test mode): " + plain); return; }
#endif
            HandleFT(plain);
            return;
        }
        int wk = plain.IndexOf(": [FT] WHIP");
        if (wk > 0) { Swing(plain.Substring(0, wk).Trim()); return; }   // a player's hidden whip signal (hit or miss)
        string who;
        string ev = Parse(raw, PlayerNames(), out who);
        if (ev == null) return;
        if (ev == "bite" && who == "")
        {   // nameless bite = the host's own cast: pick the angler who's waiting (prefer the host)
            string pick = null;
            foreach (var kv in anglers) if (kv.Value.state == 1 && (pick == null || IsHostUser(kv.Key))) pick = kv.Key;
            if (pick == null) return;
            who = pick;
        }
#if TESTMODE
        if (!testInjecting) { Log("parsed real chat (not drawn in test mode): " + ev + " " + who); return; }
#endif
        Angler a;
        if (!anglers.TryGetValue(who, out a)) { a = new Angler(); anglers[who] = a; }
        if (ev == "cast") { a.state = 1; a.since = T; RigCast(who); }
        else if (ev == "bite") { if (a.state == 0) RigCast(who); a.state = 2; a.since = T; }
        else if (ev == "reel") { if (a.state != 0) RigReel(who); a.state = 0; a.since = T; }
        Log(who + ": " + ev);
    }

    static bool IsHostUser(string name)
    {
        // a guest can't see connection ids; the host's NetworkUser is spawned first, so it has the lowest netId
        try
        {
            string best = null; uint bestId = uint.MaxValue;
            foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
            {
                object nid = Get(nu, "netId");
                uint v = nid == null ? uint.MaxValue : Convert.ToUInt32(Get(nid, "Value"));
                if (v < bestId) { bestId = v; best = Convert.ToString(Get(nu, "userName")); }
            }
            return best != null && best == name;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- fishing tournament (HUD data)
    // Host lines (tag verbatim):
    //   [FT] CATCH|<name>|<species>|<value>|<total>
    //   [FT] PERK|<name>|<species>|<perk description>
    //   [FT] BOARD|<name1>:<total1>|<name2>:<total2>|...
    //   [FT] COLL|<name>|<species>:<count>|...
    //   [FT] HOT|<x>|<y>|<z>|<radius>      purple hotspot on the water (best fish), y = host water level
    //   [FT] GROUND|<name>|<x>|<y>|<z>     invalid cast: the bobber landed on ground there (sits ~1 s, then reels back)
    //   [FT] ROD|<name>|<rodType>|<rodSkin>|<bobberSkin>   rod shop loadout (on change + every 30 s; skins in RodSkinLook.cs)
    //   [FT] MONEY|<name>|<total>          money after a rod shop purchase
    // movement perks per fish held (applied by FishItems on each player's own game); the host's PERK lines override these
    static readonly string[][] perkTable = {
        new[] { "Minnow", "+2% move speed" }, new[] { "Perch", "+2% sprint speed" }, new[] { "Trout", "+3% jump height" },
        new[] { "Clownfish", "+2% jump height" }, new[] { "Catfish", "+1% move speed, +1% sprint speed" },
        new[] { "Bass", "+1% move speed, +1% jump height" }, new[] { "Salmon", "+1 extra jump per 3 Salmon" },
        new[] { "Pufferfish", "+1 extra jump per 3 Pufferfish" },
        new[] { "Swordfish", "+5% move speed" }, new[] { "Golden Koi", "+4% sprint speed, next 3 catches x2" },
        new[] { "Anglerfish", "+6% sprint speed" },
        new[] { "Moon Eel", "free revive" }, new[] { "Ghost Fish", "+6% move speed, +6% jump height" },
        new[] { "Old Boot", "+2% fishing luck" } };
    static readonly Dictionary<string, string> perks = new Dictionary<string, string>();
    static readonly Dictionary<string, double> board = new Dictionary<string, double>();
    static readonly Dictionary<string, int> myColl = new Dictionary<string, int>();
    static readonly List<string> collOrder = new List<string>();
    static double myMoney;
    static bool tourney, inRun;
    static string myName, flashText;
    static float flashAt = -100f;
    static UnityEngine.GameObject hudGo;

    static string PerkOf(string species)
    {
        string p;
        if (perks.TryGetValue(species, out p)) return p;
        foreach (string[] e in perkTable) if (string.Equals(e[0], species, StringComparison.OrdinalIgnoreCase)) return e[1];
        return "";
    }

    static double Num(string s)
    {
        string d = Regex.Replace(s ?? "", "[^0-9.\\-]", "");
        double v; return double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    static string MyName()
    {
        try
        {
            IList lus = GetStatic(ror2.GetType("RoR2.LocalUserManager"), "readOnlyLocalUsersList") as IList;
            if (lus != null && lus.Count > 0)
            {
                object nu = Get(lus[0], "currentNetworkUser");
                if (!IsNull(nu)) { string n = Convert.ToString(Get(nu, "userName")); if (!string.IsNullOrEmpty(n)) return n; }
            }
        }
        catch { }
        return LocalName();
    }

    static void AddColl(string species, int n)
    {
        if (!myColl.ContainsKey(species)) { myColl[species] = 0; collOrder.Add(species); }
        myColl[species] += n;
    }

    public static void HandleFT(string s)
    {
        try
        {
            string[] p = s.Substring(5).Split('|');
            string me = myName ?? MyName();
            tourney = true;
            if (p[0] == "TIME") { DayNightLine(p); return; }
            bool extra = false;
            try { extra = RouteExtras(p, me); } catch (Exception e) { if (errors++ < 10) Log("FT extras error on '" + s + "': " + e.Message); }
            if (extra) return;
            if (p[0] == "CATCH" && p.Length >= 5)
            {
                string name = p[1], species = p[2];
                double value = Num(p[3]), total = Num(p[4]);
                board[name] = total;
                if (name == me)
                {
                    myMoney = total; AddColl(species, 1);
                    flashAt = T; flashText = "+$" + value.ToString("#,0") + "  " + species + "!";
                }
                Log("FT catch: " + name + " " + species + " $" + value + " total $" + total + (name == me ? " (me)" : ""));
            }
            else if (p[0] == "PERK" && p.Length >= 4)
            {
                if (p[3].Trim().Length > 0) perks[p[2]] = p[3].Trim();
                Log("FT perk: " + p[2] + " = " + p[3]);
            }
            else if (p[0] == "BOARD")
            {
                board.Clear();
                for (int i = 1; i < p.Length; i++)
                {
                    int c = p[i].LastIndexOf(':');
                    if (c <= 0) continue;
                    string name = p[i].Substring(0, c);
                    board[name] = Num(p[i].Substring(c + 1));
                    if (name == me) myMoney = board[name];
                }
                Log("FT board: " + board.Count + " players, my money $" + myMoney);
            }
            else if (p[0] == "HOT" && p.Length >= 5)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                float hx = float.Parse(p[1], ci), hy = float.Parse(p[2], ci), hz = float.Parse(p[3], ci), hr = float.Parse(p[4], ci);
                bool changed = !hasHot || Math.Abs(hx - hotX) > 0.01f || Math.Abs(hy - hotY) > 0.01f || Math.Abs(hz - hotZ) > 0.01f;
                hotX = hx; hotY = hy; hotZ = hz; hotR = hr; hasHot = true; hotScene = SceneName();
                if (changed) Log("FT hotspot at (" + hx.ToString("0.0") + ", " + hy.ToString("0.00") + ", " + hz.ToString("0.0") + ") r=" + hr + " on " + hotScene);
                SyncWaterToHost(hy);
            }
            else if (p[0] == "GROUND" && p.Length >= 5)
            {   // host Fishing15+: invalid cast that landed on ground -> bobber flies there, sits ~1 s, reels back
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string who = p[1];
                float gx = float.Parse(p[2], ci), gy = float.Parse(p[3], ci), gz = float.Parse(p[4], ci);
                Rig r = GetRig(who, true);
                if (r != null)
                {
                    float fx = gx, fz = gz;
                    try { object foot = Get(r.body, "footPosition"); fx = F(foot, "x"); fz = F(foot, "z"); } catch { }
                    float hd = (float)Math.Sqrt((gx - fx) * (gx - fx) + (gz - fz) * (gz - fz));
                    r.tx = gx; r.ty = gy; r.tz = gz;
                    r.flight = FlightTime(hd); r.arcH = Apex(hd);
                    r.castTime = T; r.reelTime = -100f;
                    r.groundReelAt = T + r.flight + 1f;
                    Angler a;
                    if (!anglers.TryGetValue(who, out a)) { a = new Angler(); anglers[who] = a; }
                    a.state = 1; a.since = T;
                }
                Log("FT ground cast: " + who + " at (" + gx.ToString("0.0") + ", " + gy.ToString("0.0") + ", " + gz.ToString("0.0") + ")" + (r == null ? " (no body)" : ""));
            }
            else if (p[0] == "WHIP" && p.Length >= 3)
            {   // host Fishing16+: p[1] whipped p[2] with a rod (1 damage, their line snaps back = fish got away)
                string atk = p[1], tgt = p[2];
                Swing(atk);
                QueueHitSpark(atk, tgt);
                Angler a;
                if (anglers.TryGetValue(tgt, out a) && a.state != 0) { RigReel(tgt); a.state = 0; a.since = T; }
                if (tgt == me) { flashAt = T; flashText = "WHIPPED by " + atk + "! Line snapped back"; }
                else if (atk == me) { flashAt = T; flashText = "You whipped " + tgt + "!"; }
                Log("FT whip: " + atk + " -> " + tgt + (tgt == me ? " (me)" : ""));
            }
            else if (p[0] == "ROD" && p.Length >= 5)
            {   // rod shop loadout: rod type (cast range prediction) + rod / bobber skin
                string[] old; loadouts.TryGetValue(p[1], out old);
                loadouts[p[1]] = new[] { p[2], p[3], p[4] };
                if (old == null || old[0] != p[2] || old[1] != p[3] || old[2] != p[4]) Log("FT rod: " + p[1] + " = " + p[2] + " / " + p[3] + " / " + p[4] + " bobber");
            }
            else if (p[0] == "MONEY" && p.Length >= 3)
            {
                board[p[1]] = Num(p[2]);
                if (p[1] == me) myMoney = board[p[1]];
                Log("FT money: " + p[1] + " $" + p[2]);
            }
            else if (p[0] == "COLL" && p.Length >= 2)
            {
                if (p[1] != me) return;
                myColl.Clear(); collOrder.Clear();
                for (int i = 2; i < p.Length; i++)
                {
                    int c = p[i].LastIndexOf(':');
                    if (c <= 0) continue;
                    AddColl(p[i].Substring(0, c), (int)Num(p[i].Substring(c + 1)));
                }
                Log("FT collection replaced: " + CollSummary());
            }
        }
        catch (Exception e) { if (errors++ < 10) Log("FT parse error on '" + s + "': " + e.Message); }
    }

    // gameplay (eat buffs, weather, golden hotspot, pools, lures) and drone-pet lines. A separate, non-inlined method so an
    // error in those handlers can never stop the tournament lines (CATCH/BOARD/...) handled after it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool RouteExtras(string[] p, string me)
    {
        if (GameplayClient.HandleFT(p, me)) { FishingFlashlight.eventName = GameplayClient.eventName; return true; }
        string zf;
        if (FishingZoneCues.Line(p, me, out zf)) { if (zf != null) { flashAt = T; flashText = zf; } return true; }   // fishing zones ([FT] ZONE / ZONECAST)
        return FishingPetLines.Line(p);   // drone pets' fishing line ([FT] PETCAST / PETCATCH)
    }

    static string CollSummary()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string sp in collOrder) sb.Append(sp + " x" + myColl[sp] + " [" + PerkOf(sp) + "]; ");
        return sb.ToString();
    }

    static List<KeyValuePair<string, double>> TopBoard(int n)
    {
        var l = new List<KeyValuePair<string, double>>(board);
        l.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (l.Count > n) l.RemoveRange(n, l.Count - n);
        return l;
    }

    static UnityEngine.GUIStyle stTitle, stText, stSmall, stFlash;
    static bool hudLogged;

    static string Esc(string s) { return (s ?? "").Replace("<", "‹").Replace(">", "›"); }

    public static void DrawHud()   // called from FishHud.OnGUI (main thread)
    {
        if (!enabled || !tourney || !inRun) return;
        try
        {
            if (stText == null)
            {
                stText = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label); stText.richText = true; stText.fontSize = 17; stText.wordWrap = false;
                stSmall = new UnityEngine.GUIStyle(stText); stSmall.fontSize = 13;
                stTitle = new UnityEngine.GUIStyle(stText); stTitle.fontSize = 15; stTitle.fontStyle = UnityEngine.FontStyle.Bold;
                stFlash = new UnityEngine.GUIStyle(stText); stFlash.fontSize = 20; stFlash.fontStyle = UnityEngine.FontStyle.Bold; stFlash.alignment = UnityEngine.TextAnchor.MiddleCenter;
            }
            float sc = UnityEngine.Screen.height / 1080f;
            UnityEngine.Matrix4x4 old = UnityEngine.GUI.matrix;
            UnityEngine.GUI.matrix = UnityEngine.Matrix4x4.Scale(new UnityEngine.Vector3(sc, sc, 1f));
            float W = 300f, x = UnityEngine.Screen.width / sc - W - 20f, y = 330f;   // right side (y adjusted below)
            var top = TopBoard(4);
            int rows = Math.Max(1, collOrder.Count);
            List<string> gp = null; try { gp = GameplayClient.HudLines(); } catch { }
            int gpn = gp == null ? 0 : gp.Count;
            bool compact = collOrder.Count > 6;            // many species: one line each (perk next to the name) so the panel fits
            float rowH = compact ? 22f : 40f;
            float h = 12 + 30 + gpn * 22 + (gpn > 0 ? 4 : 0) + 26 + rows * rowH + 26 + Math.Max(1, top.Count) * 24 + 8 + 26 + 10;
            y = Math.Max(130f, Math.Min(330f, 842f - h));   // grow upward when tall: stay above the F2 line (bottom right, 78-84 %) and below the clock

            float since = T - flashAt;
            bool flash = since >= 0 && since < 3f;
            UnityEngine.Color oldCol = UnityEngine.GUI.color;
            UnityEngine.GUI.color = new UnityEngine.Color(0f, 0f, 0f, 0.62f);
            UnityEngine.GUI.DrawTexture(new UnityEngine.Rect(x, y, W, h), UnityEngine.Texture2D.whiteTexture);
            if (flash)
            {
                float a = (1f - since / 3f) * (0.35f + 0.25f * (float)Math.Sin(since * 12f));
                UnityEngine.GUI.color = new UnityEngine.Color(1f, 0.8f, 0.2f, Math.Max(0f, a));
                UnityEngine.GUI.DrawTexture(new UnityEngine.Rect(x - 3, y - 3, W + 6, h + 6), UnityEngine.Texture2D.whiteTexture);
                UnityEngine.GUI.color = new UnityEngine.Color(0f, 0f, 0f, 0.62f);
                UnityEngine.GUI.DrawTexture(new UnityEngine.Rect(x, y, W, h), UnityEngine.Texture2D.whiteTexture);
            }
            UnityEngine.GUI.color = oldCol;

            float cy = y + 10, lx = x + 14, lw = W - 28;
            UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 30), "<color=#ffd75e>Your money: $" + myMoney.ToString("#,0") + "</color>", stText); cy += 30;
            if (gpn > 0)
            {
                foreach (string gl in gp) { UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 22), gl, stSmall); cy += 22; }
                cy += 4;
            }
            UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 24), "<color=#66ccff>FISH INVENTORY</color>", stTitle); cy += 26;
            if (collOrder.Count == 0) { UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 40), "<color=#aaaaaa>nothing caught yet</color>", stSmall); cy += 40; }
            foreach (string sp in collOrder)
            {
                if (compact)
                {
                    UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 22), "<color=#ffffff>" + Esc(sp) + "</color> <color=#ffd75e>x" + myColl[sp] + "</color>  <size=12><color=#9fd6a0>" + Esc(PerkOf(sp)) + "</color></size>", stSmall);
                    cy += 22;
                    continue;
                }
                UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 22), "<color=#ffffff>" + Esc(sp) + "</color> <color=#ffd75e>x" + myColl[sp] + "</color>", stText);
                UnityEngine.GUI.Label(new UnityEngine.Rect(lx + 12, cy + 20, lw - 12, 18), "<color=#9fd6a0>" + Esc(PerkOf(sp)) + "</color>", stSmall);
                cy += 40;
            }
            UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 24), "<color=#66ccff>LEADERBOARD</color>", stTitle); cy += 26;
            if (top.Count == 0) { UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 24), "<color=#aaaaaa>no scores yet</color>", stSmall); cy += 24; }
            for (int i = 0; i < top.Count; i++)
            {
                string n = top[i].Key; if (n.Length > 20) n = n.Substring(0, 19) + "…";
                string col = top[i].Key == myName ? "#ffd75e" : "#ffffff";
                UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 24), "<color=" + col + ">" + (i + 1) + ". " + Esc(n) + "</color>", stText);
                UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy, lw, 24), "<color=" + col + ">$" + top[i].Value.ToString("#,0") + "</color>", RightAligned());
                cy += 24;
            }
            cy += 8;
            UnityEngine.GUI.Label(new UnityEngine.Rect(lx, cy + 3, 120, 20), "<color=#8a8a8a>Fishing mod v" + ModVersion + "</color>", stSmall);
            if (stButton == null) { stButton = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.button); stButton.fontSize = 12; }
            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + W - 14 - 160, cy, 160, 24), "Download latest release", stButton))
            {
                try { UnityEngine.Application.OpenURL(ReleaseUrl); Log("opened " + ReleaseUrl); } catch (Exception e) { Log("open url error: " + e.Message); }
            }
            if (flash && flashText != null)
                UnityEngine.GUI.Label(new UnityEngine.Rect(x, y - 40, W, 34), "<color=#ffd75e>" + Esc(flashText) + "</color>", stFlash);
            UnityEngine.GUI.matrix = old;
            if (!hudLogged) { hudLogged = true; Log("HUD drawn at x=" + x.ToString("0") + " y=" + y + " (screen " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + "), money $" + myMoney + ", " + collOrder.Count + " species, " + top.Count + " on board"); }
        }
        catch (Exception e) { if (errors++ < 10) Log("HUD error: " + e); }
    }

    static UnityEngine.GUIStyle stRight, stButton;
    static UnityEngine.GUIStyle RightAligned()
    {
        if (stRight == null) { stRight = new UnityEngine.GUIStyle(stText); stRight.alignment = UnityEngine.TextAnchor.UpperRight; }
        return stRight;
    }

    static void EnsureHud()   // main thread
    {
        if (hudGo != null) return;
        hudGo = new UnityEngine.GameObject("FishingClientHud");
        UnityEngine.Object.DontDestroyOnLoad(hudGo);
#if TESTMODE
        hudGo.AddComponent<FishHudTest>();
#else
        hudGo.AddComponent<FishHud>();
#endif
        Log("HUD object created");
    }

    static void KillHud()
    {
        if (hudGo != null) { UnityEngine.Object.Destroy(hudGo); hudGo = null; Log("HUD removed"); }
    }

    // ---------------------------------------------------------------- per-frame
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static float T { get { return (float)clock.Elapsed.TotalSeconds; } }
    static float nextSlow;

    static void Update()
    {
        if (!enabled)
        {
            if (rigs.Count > 0 || !IsNull(water) || hotGo != null) KillAll("disabled");
            KillHud();
            UnderwaterLook.Restore();
            ClientExtrasOff();
            return;
        }
        try
        {
            inRun = false;
#if !TESTMODE
            if (IsHost()) { if (rigs.Count > 0 || !IsNull(water) || hotGo != null) KillAll("this game is now the host"); ClientExtrasOff(); return; }
#endif
            if (tourney) EnsureHud();
            if (GetStatic(runType, "instance") == null)
            {
                if (rigs.Count > 0 || !IsNull(water) || hotGo != null) KillAll("run ended");
                anglers.Clear();
                lock (chatQueue) chatQueue.Clear();
                UnderwaterLook.Restore();
                ClientExtrasOff();
                return;
            }
            inRun = true;
            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (lines != null) foreach (string l in lines) HandleLine(l);

            if (T >= nextSlow)
            {
                nextSlow = T + 0.5f;
                myName = MyName();
                WaterTick();
                EnsureRigs();
                // safety: a missed chat line must not leave a rod out forever
                foreach (var kv in anglers)
                {
                    Angler a = kv.Value;
                    if ((a.state == 1 && T - a.since > 20f) || (a.state == 2 && T - a.since > 8f))
                    { a.state = 0; a.since = T; RigReel(kv.Key); Log(kv.Key + ": timed out, reeling in"); }
                }
#if TESTMODE
                TestStep();
#endif
            }
            foreach (var kv in rigs)
                if (kv.Value.groundReelAt > 0f && T >= kv.Value.groundReelAt)
                {
                    kv.Value.groundReelAt = 0f;
                    Angler ga; anglers.TryGetValue(kv.Key, out ga);
                    if (ga != null && ga.state != 0) { RigReel(kv.Key); ga.state = 0; ga.since = T; }
                }
            try { WhipInput(); } catch (Exception e) { if (rigErrors++ < 10) Log("whip input error: " + e.Message); }
            if (!RodHandAttach.EnsureLateHook()) Render();   // normally LateRender() draws them after the Animator
            try { WhipFxTick(); } catch (Exception e) { if (rigErrors++ < 10) Log("whip fx error: " + e.Message); }
            try { HotRender(); } catch (Exception e) { if (rigErrors++ < 10) Log("hotspot render error: " + e); }
            try { UnderwaterLook.Tag = "[client] "; UnderwaterLook.Tick(); } catch (Exception e) { if (rigErrors++ < 10) Log("underwater error: " + e.Message); }
            try { GameplayClient.Frame(hasWater, waterY, myName); } catch (Exception e) { if (rigErrors++ < 10) Log("gameplay fx error: " + e.Message); }
            FishingPetLines.Frame();
            if (FishingZoneCues.Any) FishingZoneCues.Frame(SceneName(), hasWater, waterY);
            try { DayNightFrame(); } catch (Exception e) { if (rigErrors++ < 10) Log("day/night error: " + e.Message); }
            try { FishingFlashlight.Tick(true); } catch (Exception e) { if (rigErrors++ < 10) Log("flashlight error: " + e.Message); }
            if (waterLook != null && waterLook.Alive) waterLook.Animate();
        }
        catch (Exception e) { if (errors++ < 10) Log("update error: " + (e.InnerException ?? e)); }
    }

    static object Body(string name)
    {
        foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
        {
            if (Convert.ToString(Get(nu, "userName")) != name) continue;
            object master = Get(nu, "master");
            if (IsNull(master)) return null;
            object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
            return IsNull(body) ? null : body;
        }
        return null;
    }

    static string LocalName()
    {
        foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
        {
            object l = Get(nu, "isLocalPlayer");
            if (l is bool && (bool)l) return Convert.ToString(Get(nu, "userName"));
        }
        return null;
    }

    static object AimFlat(object body)
    {
        object dir = null;
        object ib = Get(body, "inputBank");
        if (!IsNull(ib)) dir = Get(ib, "aimDirection");
        if (dir == null) dir = Get(Get(body, "transform"), "forward");
        float x = F(dir, "x"), z = F(dir, "z");
        float len = (float)Math.Sqrt(x * x + z * z);
        if (len < 0.01f) { x = 0; z = 1; len = 1; }
        return Vec(x / len, 0, z / len);
    }

    // ---------------------------------------------------------------- rod / line / bobber (adapted from Fishing7)
    class Rig
    {
        public object body, pole, bobber, lineGo, line, poleTf, bobTf;
        public float castTime = -100f, reelTime = -100f, arcH = 3f, flight = 0.6f;
        public float groundReelAt;   // > 0: a ground cast; reel back at this time
        public readonly RodSkinLook.State skin = new RodSkinLook.State();
        public float tx, ty, tz;
        public float bx, by, bz;
    }
    static readonly Dictionary<string, Rig> rigs = new Dictionary<string, Rig>();
    static Type goType, tfType, quatType, colorType, lineType, colliderType, rendererType, matType, shaderType;
    static PropertyInfo tfPos, tfRot, tfScale, goTf, rendMat, matColor;
    static MethodInfo setActive, fromTo, lineSetPos, destroy, getComp, addComp, createPrim;
    static object unlitShader;
    static bool shaderTried;
    static int rigErrors;

    static void InitTypes()
    {
        goType = core.GetType("UnityEngine.GameObject");
        tfType = core.GetType("UnityEngine.Transform");
        quatType = core.GetType("UnityEngine.Quaternion");
        colorType = core.GetType("UnityEngine.Color");
        lineType = core.GetType("UnityEngine.LineRenderer");
        rendererType = core.GetType("UnityEngine.Renderer");
        matType = core.GetType("UnityEngine.Material");
        shaderType = core.GetType("UnityEngine.Shader");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType("UnityEngine.Collider"); if (t != null) colliderType = t; }
        tfPos = tfType.GetProperty("position"); tfRot = tfType.GetProperty("rotation"); tfScale = tfType.GetProperty("localScale");
        goTf = goType.GetProperty("transform");
        rendMat = rendererType.GetProperty("material"); matColor = matType.GetProperty("color");
        setActive = goType.GetMethod("SetActive");
        fromTo = quatType.GetMethod("FromToRotation", new[] { v3, v3 });
        lineSetPos = lineType.GetMethod("SetPosition", new[] { typeof(int), v3 });
        destroy = uObj.GetMethod("Destroy", new[] { uObj });
        getComp = goType.GetMethod("GetComponent", new[] { typeof(Type) });
        addComp = goType.GetMethod("AddComponent", new[] { typeof(Type) });
        createPrim = goType.GetMethod("CreatePrimitive");
    }

    static object Col(float r, float g, float b, float a) { return Activator.CreateInstance(colorType, new object[] { r, g, b, a }); }

    static object UnlitShader()   // main thread
    {
        if (shaderTried) return unlitShader;
        shaderTried = true;
        MethodInfo find = shaderType.GetMethod("Find", new[] { typeof(string) });
        foreach (string sh in new[] { "Sprites/Default", "Hidden/Internal-Colored", "Unlit/Color" })
        {
            object shader = find.Invoke(null, new object[] { sh });
            if (!IsNull(shader)) { unlitShader = shader; Log("unlit shader: " + sh); break; }
        }
        return unlitShader;
    }

    static object UnlitMat(object color)
    {
        object sh = UnlitShader();
        if (sh == null) return null;
        object m = Activator.CreateInstance(matType, new object[] { sh });
        matColor.SetValue(m, color, null);
        return m;
    }

    // A primitive with no collider. Uses the default material if its shader works in this game,
    // otherwise (it would render pink) an unlit colored material.
    static object Prim(string kind, string name, float sx, float sy, float sz, object color, bool forceUnlit)
    {
        object go = createPrim.Invoke(null, new object[] { Enum.Parse(createPrim.GetParameters()[0].ParameterType, kind) });
        Set(go, "name", name);
        if (colliderType != null) { object c = getComp.Invoke(go, new object[] { colliderType }); if (!IsNull(c)) destroy.Invoke(null, new object[] { c }); }
        tfScale.SetValue(goTf.GetValue(go, null), Vec(sx, sy, sz), null);
        object rend = getComp.Invoke(go, new object[] { rendererType });
        if (!IsNull(rend))
        {
            object mat = rendMat.GetValue(rend, null);
            object shader = IsNull(mat) ? null : Get(mat, "shader");
            string shName = IsNull(shader) ? "none" : Convert.ToString(Get(shader, "name"));
            object sup = IsNull(shader) ? null : Get(shader, "isSupported");
            bool broken = IsNull(shader) || shName.Contains("InternalError") || (sup is bool && !(bool)sup);
            if (forceUnlit || broken)
            {
                object um = UnlitMat(color);
                if (um != null) Set(rend, "material", um);
                else if (!IsNull(mat)) matColor.SetValue(mat, color, null);
            }
            else matColor.SetValue(mat, color, null);
            if (!loggedShader) { loggedShader = true; Log("primitive default shader '" + shName + "' supported=" + sup + " -> " + (forceUnlit || broken ? "unlit" : "default")); }
            try { Set(rend, "receiveShadows", false); Set(rend, "shadowCastingMode", Enum.Parse(rendererType.GetProperty("shadowCastingMode").PropertyType, "Off")); } catch { }
        }
        return go;
    }
    static bool loggedShader;

    static Rig GetRig(string name, bool create)
    {
        object body = Body(name);
        Rig r;
        rigs.TryGetValue(name, out r);
        if (r != null && (body == null || !ReferenceEquals(r.body, body) || IsNull(r.pole))) { KillRig(r); rigs.Remove(name); r = null; }
        if (r == null && create && body != null)
        {
            r = new Rig { body = body };
            r.pole = Prim("Cylinder", "FishingClientPole", RodThickness, RodLength, RodThickness, Col(0.45f, 0.27f, 0.10f, 1f), false);
            r.bobber = Prim("Sphere", "FishingClientBobber", 0.25f, 0.25f, 0.25f, Col(0.95f, 0.1f, 0.1f, 1f), false);
            r.poleTf = goTf.GetValue(r.pole, null); r.bobTf = goTf.GetValue(r.bobber, null);
            r.lineGo = Activator.CreateInstance(goType, new object[] { "FishingClientLine" });
            r.line = addComp.Invoke(r.lineGo, new object[] { lineType });
            Set(r.line, "positionCount", 2);
            Set(r.line, "useWorldSpace", true);
            Set(r.line, "startWidth", 0.02f); Set(r.line, "endWidth", 0.02f);
            object m = UnlitMat(Col(1, 1, 1, 1)); if (m != null) Set(r.line, "material", m);
            Set(r.line, "startColor", Col(1, 1, 1, 1)); Set(r.line, "endColor", Col(1, 1, 1, 1));
            ShowRig(r, false);
            rigs[name] = r;
            Log(name + ": rig created (pole, line, bobber)");
        }
        return r;
    }

    static void KillRig(Rig r)
    {
        foreach (object o in new[] { r.pole, r.bobber, r.lineGo }) try { if (!IsNull(o)) destroy.Invoke(null, new object[] { o }); } catch { }
    }

    static void KillAll(string why)
    {
        try
        {
            int n = rigs.Count;
            foreach (Rig r in rigs.Values) KillRig(r);
            rigs.Clear();
            bool w = !IsNull(water);
            RemoveWater();
            waterScene = null;
            if (hotGo != null) { UnityEngine.Object.Destroy(hotGo); hotGo = null; }
            hasHot = false;
            pendingHits.Clear();
            try { GameplayClient.Clear(); } catch { }
            try { FishingPetLines.Clear(); } catch { }
            try { FishingZoneCues.Clear(); } catch { }
            foreach (Angler a in anglers.Values) a.state = 0;
            if (n > 0 || w) Log("removed " + n + " rigs" + (w ? " and the water" : "") + " (" + why + ")");
        }
        catch (Exception e) { Log("cleanup error: " + (e.InnerException ?? e).Message); }
    }

    static void ShowRig(Rig r, bool on)
    {
        foreach (object o in new[] { r.pole, r.bobber, r.lineGo }) if (!IsNull(o)) setActive.Invoke(o, new object[] { on });
    }

    static void RigCast(string name)
    {
        try
        {
            Rig r = GetRig(name, true);
            if (r == null) { Log(name + ": no body to attach a rod to"); return; }
            float rm = RodRange(name);
            // same landing rule as the host (Fishing14): aim ray hits the water within 200 m -> land there,
            // else lob along the aim with pitch -10..+45 deg -> 15..200 m
            object foot = Get(r.body, "footPosition");
            object a = AimFlat(r.body);
            object ib = Get(r.body, "inputBank");
            object o = IsNull(ib) ? null : Get(ib, "aimOrigin");
            object dir = IsNull(ib) ? null : Get(ib, "aimDirection");
            float fx = F(foot, "x"), fz = F(foot, "z");
            float surf = hasWater ? waterY : F(foot, "y");
            r.ty = surf + (hasWater ? 0.05f : 0.1f);
            bool hit = false;
            if (hasWater && o != null && dir != null && F(dir, "y") < -0.01f)
            {
                float k = (waterY - F(o, "y")) / F(dir, "y");
                float hx = F(o, "x") + F(dir, "x") * k, hz = F(o, "z") + F(dir, "z") * k;
                float ddx = hx - fx, ddz = hz - fz;
                if (k > 0 && ddx * ddx + ddz * ddz <= CastRange * rm * CastRange * rm) { r.tx = hx; r.tz = hz; hit = true; }
            }
            if (!hit)
            {
                float pitch = dir == null ? 0f : (float)(Math.Asin(Math.Max(-1f, Math.Min(1f, F(dir, "y")))) * 180.0 / Math.PI);
                float u = Math.Max(0f, Math.Min(1f, (pitch - LobPitchMin) / (LobPitchMax - LobPitchMin)));
                float dist = Math.Max(LobMinDist * rm, Math.Min(CastRange * rm, Lerp(LobMinDist * rm, CastRange * rm, u)));
                r.tx = fx + F(a, "x") * dist; r.tz = fz + F(a, "z") * dist;
            }
            GameplayClient.AdjustCast(name, ref r.tx, ref r.ty, ref r.tz);   // a cast into a small pond ([FT] PCAST)
            float hd; { float hx2 = r.tx - fx, hz2 = r.tz - fz; hd = (float)Math.Sqrt(hx2 * hx2 + hz2 * hz2); }
            r.flight = FlightTime(hd); r.arcH = Apex(hd);
            r.castTime = T; r.reelTime = -100f; r.groundReelAt = 0f;
            Log(name + ": bobber target (" + r.tx.ToString("0.0") + ", " + r.ty.ToString("0.0") + ", " + r.tz.ToString("0.0") + ") " + (hit ? "aim-ray" : "lob")
                + ", dist " + hd.ToString("0.0") + " m, flight " + r.flight.ToString("0.00") + " s, apex " + r.arcH.ToString("0.0") + " m");
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("rig cast error: " + (e.InnerException ?? e)); }
    }

    // cast constants, identical to the host's Fishing14
    const float CastRange = 200f, LobMinDist = 15f, LobPitchMin = -10f, LobPitchMax = 45f;
    static float FlightTime(float d) { return 0.6f + 1.6f * Math.Max(0f, Math.Min(1f, (d - LobMinDist) / (CastRange - LobMinDist))); }
    static float Apex(float d) { return Math.Min(50f, 0.25f * d); }

    static void EnsureRigs()   // every 0.5 s: every living player always carries a rod
    {
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
            {
                string n = Convert.ToString(Get(nu, "userName"));
                if (string.IsNullOrEmpty(n)) continue;
                object b = Body(n);
                if (b == null || !Convert.ToBoolean(Get(Get(b, "healthComponent"), "alive"))) continue;
                GetRig(n, true);
            }
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("ensure rigs error: " + (e.InnerException ?? e)); }
    }

    static void RigReel(string name)
    {
        Rig r; if (!rigs.TryGetValue(name, out r)) return;
        try { object p = tfPos.GetValue(r.bobTf, null); r.bx = F(p, "x"); r.by = F(p, "y"); r.bz = F(p, "z"); } catch { r.bx = r.tx; r.by = r.ty; r.bz = r.tz; }
        r.reelTime = T;
    }

    static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

    static void LateRender() { if (enabled && inRun) { Render(); CastAnimFrame(); } }   // RodHandAttach's LateUpdate

    // CastAnimLook (same code as the host's CastAnim<N>): wind-up/flick cast, bobber arc + splash, float bob, bite tugs,
    // reel skips, idle sway, rod bend and a sagging line. Visual only; tuning in %TEMP%\ror2_castanim.cfg.
    static readonly Dictionary<string, CastAnimLook.Input> castAnimIn = new Dictionary<string, CastAnimLook.Input>();
    static void CastAnimFrame()
    {
        try
        {
            CastAnimLook.CheckCfg();
            float t = T;
            foreach (var kv in rigs)
            {
                Rig r = kv.Value;
                CastAnimLook.Input I;
                if (!castAnimIn.TryGetValue(kv.Key, out I)) castAnimIn[kv.Key] = I = new CastAnimLook.Input { key = kv.Key };
                I.body = r.body as UnityEngine.Component; I.pole = r.poleTf as UnityEngine.Transform;
                I.bob = r.bobTf as UnityEngine.Transform; I.line = r.line as UnityEngine.LineRenderer;
                if (I.body == null || I.pole == null) continue;
                Angler ang; anglers.TryGetValue(kv.Key, out ang);
                I.state = ang == null ? 0 : ang.state;
                if (I.state != 0 && r.groundReelAt > 0f) I.state = 3;
                I.now = t; I.castTime = r.castTime; I.reelTime = r.reelTime; I.flight = r.flight; I.arcH = r.arcH;
                I.target = new UnityEngine.Vector3(r.tx, r.ty, r.tz);
                I.hasWater = hasWater; I.waterY = waterY;
                I.swinging = FishingMercSwingLook.IsSwinging(kv.Key);
                RodHandAttach.Entry re;
                I.extraPitch = RodHandAttach.table.TryGetValue(I.body.gameObject.name.Replace("(Clone)", "").Trim(), out re) ? re.rodPitch : 0f;
                CastAnimLook.Animate(I);
            }
            CastAnimLook.EndFrame();
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("cast anim error: " + e.Message); }
    }

    static void Render()
    {
        if (rigs.Count == 0) return;
        try
        {
            float t = T;
            List<string> dead = null;
            foreach (var kv in rigs)
            {
                Rig r = kv.Value;
                object body = Body(kv.Key);
                if (body == null || !ReferenceEquals(body, r.body) || IsNull(r.pole)) { (dead = dead ?? new List<string>()).Add(kv.Key); continue; }
                Angler ang; anglers.TryGetValue(kv.Key, out ang);
                int state = ang == null ? 0 : ang.state;
                float sinceCast = t - r.castTime, sinceReel = t - r.reelTime;
                bool reeling = r.reelTime > r.castTime;
                if (!Convert.ToBoolean(Get(Get(body, "healthComponent"), "alive"))) { ShowRig(r, false); continue; }   // hidden only while dead
                bool idle = state == 0 && (!reeling || sinceReel > 0.6f);   // rod always equipped; bobber dangles when idle

                object core0 = Get(body, "corePosition");
                object a = AimFlat(body);
                float ax = F(a, "x"), az = F(a, "z");
                float rx = az, rz = -ax;
                float tilt;
                if (idle) tilt = 30f + (float)Math.Sin(t * 1.5f) * 2f;
                else if (reeling) tilt = sinceReel < 0.25f ? Lerp(35f, 75f, sinceReel / 0.25f) : Lerp(75f, 30f, Math.Min(1f, (sinceReel - 0.25f) / 0.35f));
                else if (sinceCast < 0.15f) tilt = Lerp(35f, 80f, sinceCast / 0.15f);
                else if (sinceCast < 0.35f) tilt = Lerp(80f, 25f, (sinceCast - 0.15f) / 0.2f);
                else tilt = state == 2 ? 20f + (float)Math.Sin(t * 40f) * 6f : 30f + (float)Math.Sin(t * 1.5f) * 2f;
                double rad = tilt * Math.PI / 180.0;
                float dx = ax * (float)Math.Cos(rad), dy = (float)Math.Sin(rad), dz = az * (float)Math.Cos(rad);
                float baseX = F(core0, "x") + rx * 0.45f + ax * 0.2f, baseY = F(core0, "y") + 0.1f, baseZ = F(core0, "z") + rz * 0.45f + az * 0.2f;
                float len = 2f * RodLength;
                tfPos.SetValue(r.poleTf, Vec(baseX + dx * len / 2, baseY + dy * len / 2, baseZ + dz * len / 2), null);
                tfRot.SetValue(r.poleTf, fromTo.Invoke(null, new object[] { Vec(0, 1, 0), Vec(dx, dy, dz) }), null);
                float tipX = baseX + dx * len, tipY = baseY + dy * len, tipZ = baseZ + dz * len;
                var pt = (UnityEngine.Transform)r.poleTf;
                bool sw = FishingMercSwingLook.IsSwinging(kv.Key);
                bool moved = RodHandAttach.Attach(body, pt, sw, true, !sw);   // handle into the right hand (+ arm pose)
                if (ApplySwing(kv.Key, r, body))
                {   // the swing moved the rod about the hand; the arm follows it, the handle stays in the hand
                    moved = true;
                    RodHandAttach.Attach(body, pt, true, false, true);
                }
                else if (sw) RodHandAttach.Attach(body, pt, false, false, true);
                if (moved)
                {   // the line starts at the rod's new tip
                    UnityEngine.Vector3 tip = pt.position + pt.up * pt.localScale.y;
                    tipX = tip.x; tipY = tip.y; tipZ = tip.z;
                }

                float bx, by, bz;
                float swX = (float)Math.Sin(t * 1.7f) * 0.12f, swZ = (float)Math.Cos(t * 1.3f) * 0.12f;
                float dangX = tipX + swX, dangY = tipY - 0.6f + (float)Math.Sin(t * 2.3f) * 0.03f, dangZ = tipZ + swZ;
                if (idle) { bx = dangX; by = dangY; bz = dangZ; }
                else if (reeling)
                {
                    float k = Math.Min(1f, sinceReel / 0.5f);
                    bx = Lerp(r.bx, dangX, k); bz = Lerp(r.bz, dangZ, k);
                    by = Lerp(r.by, dangY, k) + 2f * (float)Math.Sin(Math.PI * k);
                }
                else if (sinceCast < r.flight)
                {   // parabola like the host: apex = min(50, 0.25*dist), flight 0.6..2.2 s
                    float k = sinceCast / r.flight;
                    bx = Lerp(dangX, r.tx, k); bz = Lerp(dangZ, r.tz, k);
                    by = Lerp(dangY, r.ty, k) + 4f * r.arcH * k * (1f - k);
                }
                else if (state == 2)
                {
                    bx = r.tx + (float)Math.Sin(t * 53f) * 0.08f; bz = r.tz + (float)Math.Cos(t * 47f) * 0.08f;
                    by = r.ty - 0.35f + (float)Math.Sin(t * 31f) * 0.12f;
                }
                else { bx = r.tx; bz = r.tz; by = r.ty + (float)Math.Sin(t * 3f) * 0.08f; }
                tfPos.SetValue(r.bobTf, Vec(bx, by, bz), null);
                lineSetPos.Invoke(r.line, new object[] { 0, Vec(tipX, tipY, tipZ) });
                lineSetPos.Invoke(r.line, new object[] { 1, Vec(bx, by, bz) });
                ShowRig(r, true);
#if TESTMODE
                if (!testRendered) { testRendered = true; Log(kv.Key + ": rig rendered: pole tip (" + tipX.ToString("0.0") + ", " + tipY.ToString("0.0") + ", " + tipZ.ToString("0.0") + "), bobber (" + bx.ToString("0.0") + ", " + by.ToString("0.0") + ", " + bz.ToString("0.0") + ")"); }
#endif
            }
            if (dead != null) foreach (string n in dead) { KillRig(rigs[n]); rigs.Remove(n); Log(n + ": rig removed (body changed/died)"); }
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("render error: " + (e.InnerException ?? e)); }
        RodSkinsFrame();
    }

    // ---------------------------------------------------------------- rod shop skins (from [FT] ROD)
    static readonly Dictionary<string, string[]> loadouts = new Dictionary<string, string[]>();   // name -> {rod, skin, bobber}
    static float RodRange(string name) { string[] lo; return loadouts.TryGetValue(name, out lo) ? RodSkinLook.RangeMult(lo[0]) : 1f; }
    static void RodSkinsFrame()
    {
        float t = T;
        foreach (var kv in rigs)
        {
            Rig r = kv.Value;
            try
            {
                var pole = r.pole as UnityEngine.GameObject; var bob = r.bobber as UnityEngine.GameObject;
                if (pole == null || bob == null || !pole.activeSelf) continue;
                string[] lo; loadouts.TryGetValue(kv.Key, out lo);
                string skin = lo == null ? "Default" : lo[1], bobber = lo == null ? "Plain" : lo[2];
                string rodT = lo == null ? null : lo[0];
                if (r.skin.key == null && skin == "Default" && bobber == "Plain" && rodT == null) continue;   // never changed: keep the stock look
                var line = r.line as UnityEngine.LineRenderer;
                if (r.skin.key != skin + "|" + bobber) { RodSkinLook.Apply(r.skin, pole, bob, line, skin, bobber); GlowBobberLook.Apply(bob, bobber); Log(kv.Key + ": rod skin " + skin + " / " + bobber + " bobber"); }
                var body = r.body as UnityEngine.Component;
                RodSkinLook.Animate(r.skin, bob, line, body == null ? null : body.transform, skin, bobber, t);
                if (rodT != null) { RodTypeLook.Apply(pole, rodT); RodTypeLook.Tick(pole); }
            }
            catch (Exception e) { if (rigErrors++ < 10) Log("rod skin error (" + kv.Key + "): " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- day/night lighting (DayNightLook.cs)
    // Host DayNight<N> sends "[FT] TIME|<hour 0-24>|<dayMinutes>" every 10 s. This copy runs its own clock between lines
    // (24 h per dayMinutes real minutes), eases toward the host value, snaps when > 0.5 h off, and restores the stage's
    // own lighting when the run ends, the client is disabled, or no TIME line came for 60 s (host without DayNight).
    static bool dnHas, dnActive;
    static float dnHour, dnMinutes = 10f, dnCorr, dnLastLine = -100f, dnLastT = -1f;
    static void DayNightLine(string[] p)
    {
        if (p.Length < 2) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float h, dm;
        if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out h)) return;
        h = ((h % 24f) + 24f) % 24f;
        if (p.Length > 2 && float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out dm) && dm > 0.05f) dnMinutes = dm;
        float diff = h - dnHour;
        if (diff > 12f) diff -= 24f; else if (diff < -12f) diff += 24f;
        if (!dnHas || Math.Abs(diff) > 0.5f) { dnHour = h; dnCorr = 0f; Log("FT time " + h.ToString("0.00") + " (day = " + dnMinutes + " min)" + (dnHas ? ", clock snapped" : "")); }
        else dnCorr = diff;
        dnHas = true; dnLastLine = T;
    }
    static void DayNightFrame()   // every frame in a run (main thread)
    {
        float t = T, dt = dnLastT < 0f ? 0f : Math.Min(0.5f, t - dnLastT);
        dnLastT = t;
        if (!dnHas) return;
        if (t - dnLastLine > 60f)
        {
            dnHas = false; FishingFlashlight.hour = -1f;
            if (dnActive) { DayNightLook.Restore(); dnActive = false; Log("no [FT] TIME for 60 s - stage lighting restored"); }
            return;
        }
        float step = dt * 24f / (Math.Max(0.1f, dnMinutes) * 60f);
        float k = Math.Min(1f, dt * 0.5f), c = dnCorr * k;   // ease out the difference to the host's clock over ~2 s
        dnCorr -= c;
        dnHour = (((dnHour + step + c) % 24f) + 24f) % 24f;
        DayNightLook.Update(dnHour);
        if (!dnActive) { dnActive = true; Log("day/night lighting on at " + dnHour.ToString("0.00")); }
        FishingFlashlight.hour = dnHour;
    }

    // everything this client draws besides rigs/water/hotspot: called when disabled, on the host, and outside a run
    static void ClientExtrasOff()
    {
        try { if (dnActive) { DayNightLook.Restore(); dnActive = false; Log("day/night lighting restored"); } } catch (Exception e) { if (rigErrors++ < 10) Log("day/night restore error: " + e.Message); }
        dnHas = false; dnLastT = -1f;
        try { FishingFlashlight.Off(); } catch { }
    }

    // ---------------------------------------------------------------- water sheet (one per stage)
    static object water;
    static FishingWaterLook waterLook;   // animated ripple/caustic texture (WaterLook.cs)
    static bool hasWater;
    static float waterY;
    static string waterScene;

    static string SceneName()
    {
        Type sm = core.GetType("UnityEngine.SceneManagement.SceneManager");
        object sc = sm.GetMethod("GetActiveScene").Invoke(null, null);
        return Convert.ToString(Get(sc, "name"));
    }

    static void WaterTick()   // every 0.5 s, main thread
    {
        try
        {
#if TESTMODE
            if (!testWater) return;
#endif
            string scene = SceneName();
            // lowest GROUNDED living player's feet (drop pods / airborne ignored), like the host
            float low = float.MaxValue;
            foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
            {
                object b = Body(Convert.ToString(Get(nu, "userName")));
                if (b == null || !Convert.ToBoolean(Get(Get(b, "healthComponent"), "alive"))) continue;
                object motor = Get(b, "characterMotor");
                if (IsNull(motor) || !Convert.ToBoolean(Get(motor, "isGrounded"))) continue;
                low = Math.Min(low, F(Get(b, "footPosition"), "y"));
            }
            // height is frozen for the stage (the host drowns anyone below it); the host's [FT] HOT line carries
            // its water level, and SyncWaterToHost() snaps this sheet to it
            if (scene == waterScene && hasWater && !IsNull(water))
            {
                if (nativeWater && T >= nextNativeCheck)
                {
                    nextNativeCheck = T + 1f;
                    try { if (FishingNativeWaterLook.Animate((UnityEngine.GameObject)water)) Log("native water material re-applied"); } catch (Exception e) { if (rigErrors++ < 10) Log("native water check error: " + e.Message); }
                }
                return;
            }
            if (scene != waterScene) { RemoveWater(); }
            bool fromHost = hasHot && hotScene == scene;
            if (low == float.MaxValue && !fromHost) return;   // nobody landed yet
            waterY = fromHost ? hotY : low - 3f;
            object go = Prim("Cube", "FishingClientWater", 2000f, 0.05f, 2000f, Col(0.1f, 0.4f, 0.8f, 0.55f), true);
            object lb = Body(LocalName());
            object lp = lb == null ? null : Get(lb, "footPosition");
            float cx = lp != null ? F(lp, "x") : 0f, cz = lp != null ? F(lp, "z") : 0f;
            tfPos.SetValue(goTf.GetValue(go, null), Vec(cx, waterY, cz), null);
            water = go; hasWater = true; waterScene = scene;
            // Risk of Rain 2's own water material (NativeWaterLook.cs); the procedural ripples are the fallback
            nativeWater = false;
            try { FishingNativeWaterLook.Log = Log; nativeWater = FishingNativeWaterLook.Apply((UnityEngine.GameObject)go); } catch (Exception e) { Log("native water error: " + e.Message); }
            if (!nativeWater)
                try { waterLook = FishingWaterLook.Apply((UnityEngine.GameObject)go, null, Log); } catch (Exception e) { waterLook = null; Log("water texture error: " + e.Message); }
            Log("water created on " + scene + " at Y=" + waterY.ToString("0.00") + (fromHost ? " (host level from [FT] HOT)" : " (lowest grounded foot " + low.ToString("0.00") + ")")
                + (nativeWater ? ", native water " + FishingNativeWaterLook.UsedKey : ", procedural ripples"));
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("water error: " + (e.InnerException ?? e)); }
    }

    static bool nativeWater;
    static float nextNativeCheck;

    static void RemoveWater()   // main thread: the sheet and whichever look it has
    {
        if (!IsNull(water))
        {
            if (nativeWater) try { FishingNativeWaterLook.Remove((UnityEngine.GameObject)water); } catch { }
            destroy.Invoke(null, new object[] { water });
        }
        if (waterLook != null) { waterLook.Remove(true); waterLook = null; }
        hasWater = false; water = null; nativeWater = false;
    }

    static void SyncWaterToHost(float y)   // main thread (called from HandleFT inside Update)
    {
        try
        {
            if (!hasWater || IsNull(water) || waterScene != SceneName() || Math.Abs(y - waterY) < 0.01f) return;
            float old = waterY; waterY = y;
            object tf = goTf.GetValue(water, null); object p = tfPos.GetValue(tf, null);
            tfPos.SetValue(tf, Vec(F(p, "x"), waterY, F(p, "z")), null);
            Log("water moved to the host's level Y=" + waterY.ToString("0.00") + " (was " + old.ToString("0.00") + ")");
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("water sync error: " + (e.InnerException ?? e)); }
    }

    // ---------------------------------------------------------------- purple hotspot (from [FT] HOT)
    // Teleporter-sparkle look (HotspotLook.cs): built on the first [FT] HOT of a stage, moved/pulsed every frame,
    // rebuilt when the radius changes, destroyed when the hotspot is gone (scene change / run end / disabled).
    static bool hasHot;
    static float hotX, hotY, hotZ, hotR = 10f;
    static string hotScene;
    static UnityEngine.GameObject hotGo;
    static float hotBuiltR;

    // Mod 2.1: hotspots are invisible; players find them with the Compass / Range Finder (CompassClient payload, host
    // HotspotCompass<N>). While a CompassClient copy runs in this game the purple and golden looks are not built at all
    // (CompassClient also hides any it finds); without it (older FISHING.exe mix) they are drawn as in 2.0.
    static bool compassOn; static float nextCompassCheck = -1f;
    static bool CompassRunning()
    {
        if (T < nextCompassCheck) return compassOn;
        nextCompassCheck = T + 2f;
        bool on = false;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType("CompassClient");
                if (t == null) continue;
                FieldInfo i = t.GetField("installed", BindingFlags.Public | BindingFlags.Static), e = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (i != null && e != null && (bool)i.GetValue(null) && (bool)e.GetValue(null)) { on = true; break; }
            }
        }
        catch { }
        if (on != compassOn) Log(on ? "compass client running: hotspot looks off" : "no compass client: hotspot looks on");
        compassOn = on;
        GameplayClient.hotspotsHidden = on;
        return on;
    }

    static void HotRender()   // every frame, main thread
    {
        bool show = hasHot && hotScene == SceneName() && !CompassRunning();
        if (!show)
        {
            if (hotGo != null) { UnityEngine.Object.Destroy(hotGo); hotGo = null; Log("hotspot look removed"); }
            return;
        }
        var pos = new UnityEngine.Vector3(hotX, hotY, hotZ);
        if (hotGo != null && Math.Abs(hotBuiltR - hotR) > 0.01f) { UnityEngine.Object.Destroy(hotGo); hotGo = null; }
        if (hotGo == null)   // also Unity-null after a scene change -> rebuild
        {
            if (FishingHotspotLook.DiscTexture == null) FishingHotspotLook.DiscTexture = FishingWaterLook.RippleTexture;
            hotGo = FishingHotspotLook.Build(pos, hotR);
            hotBuiltR = hotR;
            Log("hotspot look built at (" + hotX.ToString("0.0") + ", " + hotY.ToString("0.00") + ", " + hotZ.ToString("0.0") + ") r=" + hotR + " [" + FishingHotspotLook.Info.Trim() + "]");
        }
        FishingHotspotLook.Animate(hotGo, pos, hotR);
    }

    // ---------------------------------------------------------------- rod whip: bigger rods + Mercenary-style swing
    // Rods are 1.4x longer and 0.06 thick (as the host's WhipAnim). A whip plays MercSwingLook.cs: wind up right, fast
    // slash to the left with Merc's own sword-slash effect and swing sound, follow-through, settle (~0.55 s); a confirmed
    // hit also gets Merc's impact spark on the target. Triggers: "[FT] WHIP|attacker|target" hit lines, players' hidden
    // "Name: [FT] WHIP" signals, and the local player's right click.
    const float RodLength = 1.4f, RodThickness = 0.06f;
    static bool rightWasDown;
    static readonly List<KeyValuePair<float, string[]>> pendingHits = new List<KeyValuePair<float, string[]>>();

    static void Swing(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try { FishingMercSwingLook.Begin(name); } catch (Exception e) { if (rigErrors++ < 10) Log("swing error: " + e.Message); }
    }

    static void QueueHitSpark(string attacker, string target)   // lined up with the slash, ~0.1 s after Begin
    {
        pendingHits.Add(new KeyValuePair<float, string[]>(T + 0.1f, new[] { attacker, target }));
    }

    static UnityEngine.Vector3 CorePos(object body)
    {
        object c = Get(body, "corePosition");
        return new UnityEngine.Vector3(F(c, "x"), F(c, "y"), F(c, "z"));
    }

    static void WhipFxTick()   // every frame
    {
        for (int i = pendingHits.Count - 1; i >= 0; i--)
        {
            if (T < pendingHits[i].Key) continue;
            string[] h = pendingHits[i].Value;
            pendingHits.RemoveAt(i);
            object tb = Body(h[1]), ab = Body(h[0]);
            if (tb == null) continue;
            UnityEngine.Vector3 at = CorePos(tb);
            FishingMercSwingLook.HitSpark(at, ab != null ? CorePos(ab) : at - UnityEngine.Vector3.forward);
        }
        FishingMercSwingLook.TickFallback();
    }

    // after the rod has been placed this frame: swing it if this player is whipping; true if it was moved
    static bool ApplySwing(string name, Rig r, object body)
    {
        if (!FishingMercSwingLook.IsSwinging(name)) return false;
        var pole = r.poleTf as UnityEngine.Transform;
        var comp = body as UnityEngine.Component;
        if (pole == null || comp == null) return false;
        UnityEngine.Vector3 aim = comp.transform.forward;
        object ib = Get(body, "inputBank");
        if (!IsNull(ib)) { object d = Get(ib, "aimDirection"); if (d != null) aim = new UnityEngine.Vector3(F(d, "x"), F(d, "y"), F(d, "z")); }
        return FishingMercSwingLook.Apply(name, pole, comp.transform, aim, name == myName, comp.gameObject);
    }

    static void WhipInput()   // every frame: the local player's right click (rising edge of the secondary-skill button)
    {
        string me = myName;
        object body = me == null ? null : Body(me);
        object bank = body == null ? null : Get(body, "inputBank");
        bool down = false;
        if (!IsNull(bank)) { object st = Get(bank, "skill2"); down = st != null && (bool)Get(st, "down"); }
        if (down && !rightWasDown) Swing(me);
        rightWasDown = down;
    }

#if TESTMODE
    // ---------------------------------------------------------------- test driver (host machine only)
    public static bool testWater = false;
    static bool testInjecting, testRendered;
    static int testStep;
    static float testAt;
    static void TestStep()
    {
        string me = LocalName();
        if (me == null) return;
        if (testStep == 0) { testAt = T; testStep = 1; }
        float dt = T - testAt;
        var lines = new List<string>();
        if (testStep == 1)
        {
            lines.Add("<color=#66ccff>" + me + " casts a line...</color>");
            lines.Add("[FT] CATCH|Player Two|Trout|20|20");
            lines.Add("[FT] PERK|Player Two|Trout|regen");
            testStep = 2;
        }
        else if (testStep == 2 && dt > 2.5f) { lines.Add("<color=#66ccff>Something's biting! Click or press F!</color>"); testStep = 3; }
        else if (testStep == 3 && dt > 4.0f)
        {
            lines.Add("<color=#66ccff>" + me + " caught <color=#ffffff>Test Perch</color>!</color>");
            lines.Add("[FT] CATCH|" + me + "|Perch|12|32");
            lines.Add("[FT] PERK|" + me + "|Perch|+attack speed (TEST)");
            lines.Add("[FT] CATCH|" + me + "|Old Boot|0|32");
            lines.Add("[FT] BOARD|Player Two:45|" + me + ":32|Player Three:10|Player Four:7|Extra Guy:1");
            testStep = 4;
        }
        else if (testStep == 4 && dt > 6.0f)
        {
            lines.Add("[FT] COLL|" + me + "|Perch:2|Old Boot:1|Golden Koi:1");
            lines.Add("[FT] COLL|Player Two|Trout:5");   // someone else's: must be ignored
            testStep = 5;
        }
        else if (testStep == 5 && dt > 9.0f)
        {
            testStep = 6;
            Log("test sequence done, rigs=" + rigs.Count + ", anglers[" + me + "].state=" + (anglers.ContainsKey(me) ? anglers[me].state : -1));
            var top = TopBoard(4); var sb = new System.Text.StringBuilder();
            foreach (var kv in top) sb.Append(kv.Key + "=$" + kv.Value + " ");
            Log("final HUD state: me=" + myName + " money=$" + myMoney + " | inventory: " + CollSummary() + "| top4: " + sb + "| hudLogged=" + hudLogged);
            KillAll("test finished");
            enabled = false;   // next Update destroys the HUD object
            Log("test copy disabled");
        }
        foreach (string line in lines) { Log("simulating chat line: " + line); testInjecting = true; try { HandleLine(line); } finally { testInjecting = false; } }
    }
#endif
}
