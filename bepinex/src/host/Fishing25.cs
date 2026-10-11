// Fishing rod for the HOST's game.
//  - JV94 (local player): press F in the focused game window to cast, F again when it bites (1.5 s).
//  - Any player: type "fish" (or "/fish") in chat to cast, type it again when it bites (5 s window).
//  Catch: 10% Old Boot, else white 60 / green 30 / red 8 / lunar 2 droplet in front of the angler.
//  Fishing25: RIGHT CLICK = "Rod Whip" (JV94: right mouse; guests: FISHING.exe sends hidden chat "[FT] WHIP").
//  Hits the nearest other living player within 12 m and 35 deg of the attacker's aim for exactly 1 damage and
//  snaps their line back (cast cancelled like a miss: no catch, no score). 1.5 s cooldown per player.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

public static partial class Fishing25
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_fishing.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static bool enabled = true;
    static Assembly ror2, core;
    static Type netServer, v3, uObj;
    static MethodInfo dropMethod, getPickupDef, langGet;
    static object bobberFx;
    static Type chatBoxType;
    static readonly System.Random rng = new System.Random();
    static float now;
    static int errors;
    static bool fWasDown, mWasDown;
    public static bool hostLeftClickFishing = true;    // JV94: left click (M1) and F both cast/reel; false = F only
    public static bool tournamentOpen = true;
    public static float castRange = 200f;    // max horizontal cast distance (aim-ray hit and lob)
    public static float lobMinDist = 15f;    // lob distance at pitch <= lobPitchMin
    public static float lobPitchMin = -10f, lobPitchMax = 45f;   // aim pitch (deg, up positive) mapped linearly to 15..200 m          // false = casts refused ("The tournament is over!")
    static bool initDone, runSeen;
    static readonly Dictionary<string, float> refusedAt = new Dictionary<string, float>();

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;

    // per-angler state
    class Angler { public int state; public float biteAt, deadline, readyAt, autoReelAt; public bool viaChat, auto, autoWillCatch, hot; }
    // state: 0 idle, 1 waiting for bite, 2 biting
    static readonly Dictionary<string, Angler> anglers = new Dictionary<string, Angler>();
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine;

    static readonly string[] bootLines = {
        "{0} reeled in an Old Boot. Smells like Lemurian.",
        "{0} caught an Old Boot! It's not even a matching pair.",
        "{0} caught an Old Boot. The Providence would be ashamed.",
        "{0} hooked a soggy Old Boot. Maybe next time, champ."
    };

    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

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

    public static void Chat(string text)
    {
        try
        {
            Type chat = ror2.GetType("RoR2.Chat");
            object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
            Set(msg, "baseToken", "<color=#66ccff>" + text + "</color>");
            foreach (MethodInfo m in chat.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
        }
        catch (Exception e) { Log("chat error: " + e.Message); }
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetName().Name == "RoR2") ror2 = a;
            if (a.GetName().Name == "UnityEngine.CoreModule") core = a;
            Type t = a.GetType("UnityEngine.Networking.NetworkServer");
            if (t != null) netServer = t;
        }
        v3 = core.GetType("UnityEngine.Vector3");
        uObj = core.GetType("UnityEngine.Object");
        getPickupDef = ror2.GetType("RoR2.PickupCatalog").GetMethod("GetPickupDef", All);
        langGet = ror2.GetType("RoR2.Language").GetMethod("GetString", new[] { typeof(string) });
        foreach (MethodInfo m in ror2.GetType("RoR2.PickupDropletController").GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] ps = m.GetParameters();
            if (m.Name == "CreatePickupDroplet" && ps.Length == 3 && ps[0].ParameterType.Name == "PickupIndex" && ps[1].ParameterType == v3) dropMethod = m;
        }
        chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
        Log("ChatBox type=" + (chatBoxType != null) + " showInput=" + (chatBoxType != null && (chatBoxType.GetProperty("showInput", All) != null || chatBoxType.GetField("showInput", All) != null)));

        // carry over tournament state from the newest older version, then disable older versions (Fishing<N> only;
        // other agents' Fishing* helper classes are left alone)
        Type prev = null; int prevN = -1;
        foreach (var asm in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(asm))
            {
                Match mm = Regex.Match(t.Name, "^Fishing(\\d+)$");
                if (!mm.Success || t == typeof(Fishing25)) continue;
                int n = int.Parse(mm.Groups[1].Value);
                if (n > prevN) { prevN = n; prev = t; }
            }
        foreach (var asm in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(asm))
                if (IsOldVersion(t))
                {
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en != null && en.FieldType == typeof(bool)) { en.SetValue(null, false); Log("disabled old " + t.Name); }
                }
        if (prev != null) CarryOver(prev);
        RodsInit(prev);   // after disabling, so no catch lands in the old dicts mid-copy

        InitRig();   // reflection lookups + event hook only; no Unity API calls here
        Action h = Tick;
        ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, h);
        EventInfo cc = ror2.GetType("RoR2.Chat").GetEvent("onChatChanged", All);
        if (cc != null && cc.EventHandlerType == typeof(Action)) { Action c = OnChatChanged; cc.AddEventHandler(null, c); }
        Log("Fishing25 installed (waiting for an active host run): dropMethod=" + (dropMethod != null) + " chatHook=" + (cc != null ? cc.EventHandlerType.Name : "none"));

    }

    static bool IsOldVersion(Type t) { return t != typeof(Fishing25) && Regex.IsMatch(t.Name, "^Fishing\\d+$"); }

    static string DumpMoney(IDictionary d)
    {
        var parts = new List<string>();
        if (d != null) foreach (DictionaryEntry e in d) parts.Add(e.Key + "=$" + e.Value);
        return "{" + string.Join(", ", parts.ToArray()) + "}";
    }

    static void CarryOver(Type old)   // pure managed data copy; no Unity API
    {
        try
        {
            IDictionary om = old.GetField("money", All).GetValue(null) as IDictionary;
            Log("carry-over from " + old.Name + ": " + old.Name + ".money BEFORE = " + DumpMoney(om) + "; Fishing25.money BEFORE = " + DumpMoney(money));
            if (om != null) foreach (DictionaryEntry e in om) money[(string)e.Key] = (int)e.Value;
            FieldInfo cf = old.GetField("collections", All) ?? old.GetField("coll", All);
            IDictionary oc = cf == null ? null : cf.GetValue(null) as IDictionary;
            if (oc != null) foreach (DictionaryEntry e in oc) collections[(string)e.Key] = new Dictionary<string, int>((Dictionary<string, int>)e.Value);
            IDictionary od = old.GetField("doubleLeft", All).GetValue(null) as IDictionary;
            if (od != null) foreach (DictionaryEntry e in od) doubleLeft[(string)e.Key] = (int)e.Value;
            FieldInfo to = old.GetField("tournamentOpen", All); if (to != null) tournamentOpen = (bool)to.GetValue(null);
            FieldInfo tc = old.GetField("totalCatches", All); if (tc != null) totalCatches = (int)tc.GetValue(null);
            FieldInfo an = old.GetField("announced", All); if (an != null) announced = (bool)an.GetValue(null);
            // tunables other mods / agents may have changed at runtime
            foreach (string fn in new[] { "hostLeftClickFishing", "castRange", "lobMinDist", "lobPitchMin", "lobPitchMax", "whipEnabled", "whipCooldown", "whipRange", "whipAngle", "whipCount" })
            {
                FieldInfo of = old.GetField(fn, All), nf = typeof(Fishing25).GetField(fn, All);
                if (of != null && nf != null && of.FieldType == nf.FieldType) { nf.SetValue(null, of.GetValue(null)); Log("carry-over " + fn + " = " + nf.GetValue(null)); }
            }
            // live text/item tables (other mods rewrite these at runtime)
            foreach (string tbl in new[] { "fishCarrierItem", "PerkText" })
            {
                FieldInfo of = old.GetField(tbl, All), nf = typeof(Fishing25).GetField(tbl, All);
                IDictionary od2 = of == null ? null : of.GetValue(null) as IDictionary;
                Dictionary<string, string> nd = nf == null ? null : nf.GetValue(null) as Dictionary<string, string>;
                if (od2 == null || nd == null) continue;
                nd.Clear();
                var parts = new List<string>();
                foreach (DictionaryEntry e in od2) { nd[(string)e.Key] = (string)e.Value; parts.Add(e.Key + "=" + e.Value); }
                Log("carry-over " + tbl + ": " + string.Join(", ", parts.ToArray()));
            }
            // the 5 new species: their carrier items are added to the carried-over live table; perk texts = the current movement perks
            lock (fishCarrierItem) foreach (string sp in NewSpecies) if (!fishCarrierItem.ContainsKey(sp)) fishCarrierItem[sp] = defaultCarrier[sp];
            lock (PerkText) foreach (var kv in defaultPerkText) if (!PerkText.ContainsKey(kv.Key)) PerkText[kv.Key] = kv.Value;
            { var parts = new List<string>(); foreach (var kv in fishCarrierItem) parts.Add(kv.Key + "=" + kv.Value); Log("fishCarrierItem after adding new species: " + string.Join(", ", parts.ToArray())); }
            // hotspot (adopted as-is; the old disc object is destroyed in CleanOld)
            FieldInfo hh = old.GetField("hasHotspot", All);
            if (hh != null && (bool)hh.GetValue(null))
            {
                hotspotPos = (UnityEngine.Vector3)old.GetField("hotspotPos", All).GetValue(null);
                hotspotRadius = (float)old.GetField("hotspotRadius", All).GetValue(null);
                hotScene = (string)old.GetField("hotScene", All).GetValue(null);
                hasHotspot = true;
                Log("carry-over hotspot at (" + hotspotPos.x.ToString("0.0") + ", " + hotspotPos.y.ToString("0.00") + ", " + hotspotPos.z.ToString("0.0") + ") r=" + hotspotRadius + " on " + hotScene);
            }
            MethodInfo snap = old.GetMethod("SchoolSnapshot", All);
            if (snap != null) SchoolRestore((string)snap.Invoke(null, null)); else LogC("carry-over: " + old.Name + " has no school");
            GameplayCarry(old);
            Log("carry-over done: Fishing25.money AFTER = " + DumpMoney(money) + "; collections for " + collections.Count + " players; doubleLeft " + DumpMoney(doubleLeft)
                + "; tournamentOpen=" + tournamentOpen + " totalCatches=" + totalCatches + " announced=" + announced);
        }
        catch (Exception e) { Log("carry-over error: " + (e.InnerException ?? e)); }
    }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); } catch { return new Type[0]; }
    }

    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(ror2.GetType("RoR2.Chat"), "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            lock (chatQueue) chatQueue.Add(line);
            if (HiddenKind(ChatPlain(line))) chatDirty = true;   // removed from the chat box next frame (after every chat handler saw it)
        }
        catch { }
    }

    // ---- hide machine lines ([FT] ..., [REQ] ...) from the host's visible chat (taken over from ReqPanel1)
    static bool chatDirty;
    static int hiddenLogged;
    static string ChatPlain(string raw) { return Regex.Replace(raw.Replace("<noparse>", "").Replace("</noparse>", ""), "</?color[^>]*>", "").Trim(); }
    static bool HiddenKind(string p)
    {
        return p.StartsWith("[FT] ") || p.Contains(": [FT] ") || p.StartsWith("[REQ] ") || p.Contains(": [REQ] ");
    }
    static void HideTaggedLines()   // main thread (onUpdate)
    {
        chatDirty = false;
        try
        {
            List<string> log = GetStatic(ror2.GetType("RoR2.Chat"), "log") as List<string>;
            if (log == null) return;
            int removed = 0;
            for (int i = log.Count - 1; i >= 0; i--)
                if (log[i] != null && HiddenKind(ChatPlain(log[i]))) { log.RemoveAt(i); removed++; }
            if (removed == 0 || chatBoxType == null) return;
            Array boxes = (Array)uObj.GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            foreach (object b in boxes)
            {
                try
                {
                    if (!(bool)Get(b, "enabled")) continue;
                    chatBoxType.GetMethod("BuildChat", All).Invoke(b, null);
                    MethodInfo sc = chatBoxType.GetMethod("ScrollToBottom", All);
                    if (sc != null) sc.Invoke(b, null);
                }
                catch { }
            }
            if (hiddenLogged++ < 3) Log("hid " + removed + " tagged chat line(s) from " + boxes.Length + " chat box(es)");
        }
        catch (Exception e) { if (errors++ < 10) Log("chat hide error: " + (e.InnerException ?? e).Message); }
    }

    static void Tick()
    {
        now += 0.02f;
        if (!enabled) return;
        try
        {
            if (!(bool)netServer.GetProperty("active").GetValue(null, null)) return;
            if (IsNull(GetStatic(ror2.GetType("RoR2.Run"), "instance"))) { runSeen = false; return; }
            if (!runSeen) { runSeen = true; Log("Fishing25: run active on host (scene " + SceneName() + ")"); }

            if (!initDone) { initDone = true; InitPerks(); }
            if (!fxTried) LoadFx();
            if (!tournamentOpen)
                foreach (var kv in anglers)
                    if (kv.Value.state != 0) { kv.Value.state = 0; kv.Value.auto = false; RigReel(kv.Key); Log(kv.Key + ": line pulled in (tournament closed)"); }

            // local player F key
            uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg);
            bool down = fg == myPid && (GetAsyncKeyState(0x46) & 0x8000) != 0;
            if (down && !fWasDown && !InputBlocked("F")) Press(LocalName(), false);
            fWasDown = down;
            bool mdown = fg == myPid && (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (hostLeftClickFishing && mdown && !mWasDown && !InputBlocked("click")) Press(LocalName(), false);
            mWasDown = mdown;
            bool rdown = fg == myPid && (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (rdown && !rWasDown && whipEnabled && !InputBlocked("right click") && !MenuOpen()) Whip(LocalName(), "right click");
            rWasDown = rdown;
            if (dryRunWhips > 0) { dryRunWhips--; WhipCore(LocalName(), "dry-run", true); }
            if (testDroneDamage) { testDroneDamage = false; TestDroneDamage(); }
            if (tickNo % 10 == 0) DrownTick();
            GuestClicks();

            // chat commands
            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (lines != null) foreach (string l in lines) HandleChatLine(l);
            if (!oldCleaned) CleanOld();
            TourneyTick();
            if (tickNo++ % 25 == 0) { AutoFish(); WaterTick(); HotspotTick(); SchoolTick(); GameplayTick(); EnsureRigs(); RodsTick(); }

            SchoolTimers(); GameplayFast(); GameplayDryRun();
            PetTick();
            // timers
            foreach (var kv in anglers)
            {
                Angler a = kv.Value;
                if (a.state == 3)
                {   // invalid (ground) cast: no bite, no catch; reel back after the bobber sat for ~1 s
                    if (now >= a.deadline) { a.state = 0; a.readyAt = now + 1f; RigReel(kv.Key); }
                    continue;
                }
                if (a.state == 1 && now >= a.biteAt)
                {
                    a.state = 2;
                    if (a.auto) { a.autoReelAt = now + 0.5f + (float)rng.NextDouble() * 0.7f; a.autoWillCatch = HookedFish(kv.Key) || rng.NextDouble() < 0.7; }
                    a.deadline = now + (a.viaChat ? 5f : 1.5f) * WindowMult(kv.Key);
                    Chat(a.viaChat ? "Something's biting " + kv.Key + "'s line! Click (or type 'fish')!" : "Something's biting! Click or press F!");
                    Bobber(kv.Key, 2f);
                }
                else if (a.state == 2 && a.auto && a.autoWillCatch && now >= a.autoReelAt)
                {
                    a.state = 0; a.readyAt = now + 2f;
                    Log(kv.Key + ": auto-reel");
                    RigReel(kv.Key); ReelIn(kv.Key);
                }
                else if (a.state == 2 && now >= a.deadline)
                {
                    a.state = 0; a.readyAt = now + 2f;
                    Chat("The fish got away from " + kv.Key + "!");
                    Log(kv.Key + ": fish got away"); RigReel(kv.Key);
                }
            }
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + (e.InnerException ?? e)); }
    }

    static bool fxTried;
    static void LoadFx()   // must run on the main thread
    {
        fxTried = true;
        foreach (string p in new[] { "Prefabs/Effects/OmniEffect/OmniImpactVFX", "Prefabs/Effects/ImpactEffects/CharacterLandImpact",
                                     "Prefabs/Effects/OmniEffect/OmniExplosionVFXQuick", "Prefabs/Effects/OmniEffect/OmniExplosionVFX" })
        {
            object fx = LoadPrefab(p);
            Log("fx " + p + " -> " + (fx != null));
            if (fx != null) { bobberFx = fx; break; }
        }
    }

    static bool ChatBoxOpen()
    {
        // only called on an F key-down edge, so FindObjectsOfType here is cheap enough
        try
        {
            if (chatBoxType == null) return false;
            Array boxes = (Array)uObj.GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            foreach (object b in boxes) { object o = Get(b, "showInput"); if (o is bool && (bool)o) return true; }
        }
        catch { }
        return false;
    }

    static void HandleChatLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim();
        string rawText = plain.Substring(i + 2).Trim();
        if (rawText == "[FT] FISH")   // hidden machine signal from FISHING.exe guests: same as typing "fish", no echo
        {
            if (FindUser(name) != null) { Log(name + ": [FT] FISH signal"); Press(name, true); }
            return;
        }
        if (rawText == "[FT] WHIP")   // hidden machine signal from FISHING.exe guests: right click = rod whip
        {
            if (FindUser(name) != null) { Log(name + ": [FT] WHIP signal"); Whip(name, "chat"); }
            return;
        }
        if (GpCommand(name, rawText)) return;
        string text = rawText.ToLowerInvariant();
        if (text == "!scores") { if (FindUser(name) != null) Board(); return; }
        if (text == "!collection") { if (FindUser(name) != null) Coll(name); return; }
        if (text.StartsWith("!") && FindUser(name) != null && RodCommand(name, rawText)) return;
        if (text != "fish" && text != "/fish" && text != "!fish") return;
        if (FindUser(name) == null) return;
        Press(name, true);
    }

    static void Press(string name, bool viaChat) { Press(name, viaChat, false); }
    static void Press(string name, bool viaChat, bool auto)
    {
        if (name == null) return;
        Angler a;
        if (!anglers.TryGetValue(name, out a)) { a = new Angler(); anglers[name] = a; }
        if (!tournamentOpen)
        {
            float last;
            if (!auto && (!refusedAt.TryGetValue(name, out last) || now - last > 3f)) { refusedAt[name] = now; Chat("The tournament is over!"); Log(name + ": cast refused (tournament over)"); }
            return;
        }
        if (a.state == 0)
        {
            if (now < a.readyAt) return;
            if (Body(name) == null) return;
            int cast = RigCast(name, auto);   // 1 = on water, 0 = landed on ground, -1 = auto-fish found no water (skip)
            if (cast < 0) { a.readyAt = now + 2f; Log(name + ": auto-fish skipped this cycle (no water in 8 directions)"); return; }
            if (cast == 0)
            {
                Rig gr; rigs.TryGetValue(name, out gr);
                a.state = 3; a.viaChat = viaChat; a.auto = auto; a.hot = false;
                a.deadline = now + (gr != null ? gr.flight : 0.6f) + 1f;   // bobber sits on the ground ~1 s, then reels back
                Chat(name + "'s line landed on the ground — cast into the water!");
                if (gr != null)
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    ChatRaw("[FT] GROUND|" + name + "|" + gr.tx.ToString("0.00", ci) + "|" + gr.ty.ToString("0.00", ci) + "|" + gr.tz.ToString("0.00", ci));
                }
                Log(name + ": INVALID cast - landed on ground (" + (auto ? "auto-idle" : viaChat ? "chat" : "F key") + ")");
                return;
            }
            a.state = 1; a.viaChat = viaChat; a.auto = auto;
            a.biteAt = now + 3f + (float)rng.NextDouble() * 5f;
            a.hot = LandsInHotspot(name);
            SchoolOnCast(name, a); GpOnCast(name, a);
            RodBite(name, a);
            Chat((auto ? name + " is auto-fishing..." : name + " casts a line...") + (a.hot ? " right into the hotspot!" : ""));
            if (a.hot) Log(name + ": bobber landed INSIDE the hotspot");
            Bobber(name, 1f);
            Log(name + ": cast (" + (auto ? "auto-idle" : viaChat ? "chat" : "F key") + ")");
        }
        else if (a.state == 1)
        {
            a.state = 0; a.readyAt = now + 2f;
            Chat(name + " yanked too early and scared the fish off!");
            Log(name + ": too early (" + (viaChat ? "chat" : "F key") + ")"); RigReel(name);
        }
        else if (a.state == 2)
        {
            a.state = 0; a.readyAt = now + 2f;
            RigReel(name); ReelIn(name);
        }
    }

    // public so a test class can call it directly
    public static string Catch(string name)
    {
        if (!tournamentOpen) { Log(name + ": catch blocked (tournament over)"); return "closed"; }
        object body = Body(name);
        if (body == null) { Log(name + ": catch but no body"); return "nobody"; }
        Angler ang; anglers.TryGetValue(name, out ang);
        bool hot = ang != null && ang.hot;
        if (ang != null) ang.hot = false;
        string forced = catchForced;
        if (forced == null) forced = GpRoll(name, hot);
        if (forced == "Old Boot" || (forced == null && !hot && rng.NextDouble() < 0.10))
        {
            Chat(string.Format(bootLines[rng.Next(bootLines.Length)], name) + (catchNote ?? ""));
            Log(name + ": caught Old Boot");
            Score(name, "Old Boot", 0, null);
            return "Old Boot";
        }
        double r = rng.NextDouble();
        // normal: lunar 2 / red 8 / green 30 / white 60;  hotspot: lunar 10 / red 30 / green 35 / white 25, no boots
        double pL = hot ? 0.10 : 0.02, pR = hot ? 0.40 : 0.10, pG = hot ? 0.75 : 0.40;
        LuckOdds(name, ref pL, ref pR);
        // tier roll is independent of the run's drop lists (items may be removed from the game)
        string tier = forced != null ? FishSchool.TierOf(forced) : (r < pL ? "Lunar" : r < pR ? "Tier3" : r < pG ? "Tier2" : "Tier1");

        // no droplet: the tier roll only picks the species; the fish's carrier item goes straight into the inventory (GivePerk)
        string color = tier.Contains("Tier3") ? "#ff4444" : tier.Contains("Tier2") ? "#77ff55" : tier.Contains("Lunar") ? "#5599ff" : "#ffffff";
        string[] sp; int lo, hi;
        if (tier.Contains("Lunar")) { sp = new[] { "Moon Eel", "Ghost Fish" }; lo = 500; hi = 500; }
        else if (tier.Contains("Tier3")) { sp = new[] { "Swordfish", "Golden Koi", "Anglerfish" }; lo = 150; hi = 300; }
        else if (tier.Contains("Tier2")) { sp = new[] { "Bass", "Salmon", "Pufferfish" }; lo = 40; hi = 80; }
        else { sp = new[] { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" }; lo = 10; hi = 30; }
        scoringHot = hot;
        string species = forced ?? sp[rng.Next(sp.Length)];
        try { Score(name, species, rng.Next(hot && forced != null ? (lo + hi) / 2 : lo, hi + 1), null); }
        finally { scoringHot = false; }
        Log(name + ": caught " + species + " (" + tier + ", no droplet)" + (hot ? " from the HOTSPOT" : ""));
        if (hot && (tier.Contains("Tier3") || tier.Contains("Lunar")))
        {
            Chat(name + " landed the PRIZE FISH! The hotspot has moved...");
            Log(name + ": PRIZE FISH -> moving hotspot");
            PlaceHotspot(true);
        }
        return species;
    }

    static void Bobber(string name, float scale)
    {
        try
        {
            if (bobberFx == null) return;
            object body = Body(name);
            if (body == null) return;
            object ed = Activator.CreateInstance(ror2.GetType("RoR2.EffectData"));
            Set(ed, "origin", FrontOf(body, 10f, 0f));
            Set(ed, "scale", scale);
            foreach (MethodInfo m in ror2.GetType("RoR2.EffectManager").GetMethods(All))
                if (m.Name == "SpawnEffect" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "GameObject")
                { m.Invoke(null, new object[] { bobberFx, ed, true }); break; }
        }
        catch (Exception e) { if (errors++ < 10) Log("bobber error: " + (e.InnerException ?? e).Message); }
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

    static object FrontOf(object body, float dist, float up)
    {
        object p = Get(body, "footPosition") ?? Get(Get(body, "transform"), "position");
        object a = AimFlat(body);
        return Vec(F(p, "x") + F(a, "x") * dist, F(p, "y") + up, F(p, "z") + F(a, "z") * dist);
    }

    static object FindUser(string name)
    {
        foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            if (Convert.ToString(Get(nu, "userName")) == name) return nu;
        return null;
    }

    static string LocalName()
    {
        foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
        {
            object l = Get(nu, "isLocalPlayer");
            if (l is bool && (bool)l) return Convert.ToString(Get(nu, "userName"));
        }
        return null;
    }

    static object Body(string name)
    {
        object nu = FindUser(name);
        object master = Get(nu, "master");
        if (IsNull(master)) return null;
        object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
        return IsNull(body) ? null : body;
    }

    static object LoadPrefab(string path)
    {
        try
        {
            Type legacy = ror2.GetType("RoR2.LegacyResourcesAPI");
            foreach (MethodInfo m in legacy.GetMethods(All))
                if (m.Name == "Load" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                {
                    object r = m.MakeGenericMethod(core.GetType("UnityEngine.GameObject")).Invoke(null, new object[] { path });
                    return IsNull(r) ? null : r;
                }
        }
        catch { }
        return null;
    }

    // ===================== visible rod / line / bobber (local-only GameObjects on the host) =====================
    class Rig
    {
        public object body, pole, bobber, lineGo, line, poleTf, bobTf;
        public float castTime = -100f, reelTime = -100f, arcH = 3f, flight = 0.6f;
        public float tx, ty, tz;          // bobber landing point (world)
        public string skinKey; public UnityEngine.Material skinMat, glowMat; public List<UnityEngine.Material> skinMats;
        public float bx, by, bz;          // bobber position when reel started
    }
    static readonly Dictionary<string, Rig> rigs = new Dictionary<string, Rig>();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static float T { get { return (float)clock.Elapsed.TotalSeconds; } }
    static Type goType, tfType, quatType, colorType, lineType, colliderType, rendererType, matType, shaderType;
    static PropertyInfo tfPos, tfRot, tfScale, goTf, rendMat, matColor;
    static MethodInfo setActive, fromTo, lineSetPos, destroy, getComp, addComp, createPrim;
    static object lineMat;
    static int rigErrors;

    static void InitRig()
    {
        try
        {
            goType = core.GetType("UnityEngine.GameObject");
            tfType = core.GetType("UnityEngine.Transform");
            quatType = core.GetType("UnityEngine.Quaternion");
            colorType = core.GetType("UnityEngine.Color");
            lineType = core.GetType("UnityEngine.LineRenderer");
            rendererType = core.GetType("UnityEngine.Renderer");
            matType = core.GetType("UnityEngine.Material");
            shaderType = core.GetType("UnityEngine.Shader");
            foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("UnityEngine.Collider"); if (t != null) colliderType = t; }
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
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            if (upd != null) { Action r = Render; upd.AddEventHandler(null, r); }
            else { Action r = Render; ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, r); }
            Log("rig init ok, render on " + (upd != null ? "onUpdate" : "onFixedUpdate") + ", collider type=" + (colliderType != null));
        }
        catch (Exception e) { Log("rig init error: " + (e.InnerException ?? e)); }
    }

    static object Col(float r, float g, float b) { return Activator.CreateInstance(colorType, new object[] { r, g, b, 1f }); }

    static object Prim(string kind, string name, float sx, float sy, float sz, object color)
    {
        object go = createPrim.Invoke(null, new object[] { Enum.Parse(createPrim.GetParameters()[0].ParameterType, kind) });
        Set(go, "name", name);
        if (colliderType != null) { object c = getComp.Invoke(go, new object[] { colliderType }); if (!IsNull(c)) destroy.Invoke(null, new object[] { c }); }
        object tf = goTf.GetValue(go, null);
        tfScale.SetValue(tf, Vec(sx, sy, sz), null);
        object rend = getComp.Invoke(go, new object[] { rendererType });
        if (!IsNull(rend)) { object mat = rendMat.GetValue(rend, null); if (mat != null) matColor.SetValue(mat, color, null); }
        return go;
    }

    static object LineMaterial()
    {
        if (lineMat != null) return lineMat;
        MethodInfo find = shaderType.GetMethod("Find", new[] { typeof(string) });
        foreach (string sh in new[] { "Sprites/Default", "Hidden/Internal-Colored", "Unlit/Color", "Standard" })
        {
            object shader = find.Invoke(null, new object[] { sh });
            if (!IsNull(shader)) { lineMat = Activator.CreateInstance(matType, new object[] { shader }); Log("line shader: " + sh); break; }
        }
        return lineMat;
    }

    static Rig GetRig(string name, bool create)
    {
        object body = Body(name);
        Rig r;
        rigs.TryGetValue(name, out r);
        if (r != null && (body == null || !ReferenceEquals(r.body, body) || IsNull(r.pole))) { KillRig(r); rigs.Remove(name); r = null; }
        if (r == null && create && body != null)
        {
            r = new Rig { body = body };
            r.pole = Prim("Cylinder", "FishingPole", 0.04f, 1.0f, 0.04f, Col(0.45f, 0.27f, 0.10f));
            r.bobber = Prim("Sphere", "FishingBobber", 0.25f, 0.25f, 0.25f, Col(0.95f, 0.1f, 0.1f));
            r.poleTf = goTf.GetValue(r.pole, null); r.bobTf = goTf.GetValue(r.bobber, null);
            r.lineGo = Activator.CreateInstance(goType, new object[] { "FishingLine" });
            r.line = addComp.Invoke(r.lineGo, new object[] { lineType });
            Set(r.line, "positionCount", 2);
            Set(r.line, "useWorldSpace", true);
            Set(r.line, "startWidth", 0.02f); Set(r.line, "endWidth", 0.02f);
            object m = LineMaterial(); if (m != null) Set(r.line, "material", m);
            Set(r.line, "startColor", Col(1, 1, 1)); Set(r.line, "endColor", Col(1, 1, 1));
            rigs[name] = r;
            Log(name + ": rig created (pole, line, bobber)");
        }
        return r;
    }

    static void KillRig(Rig r)
    {
        foreach (object o in new[] { r.pole, r.bobber, r.lineGo }) try { if (!IsNull(o)) destroy.Invoke(null, new object[] { o }); } catch { }
    }

    static void ShowRig(Rig r, bool on)
    {
        foreach (object o in new[] { r.pole, r.bobber, r.lineGo }) if (!IsNull(o)) setActive.Invoke(o, new object[] { on });
    }

    // cast flight: 0.6 s at 15 m .. 2.2 s at 200 m (linear, clamped); apex = 0.25 * horizontal distance, capped at 50 m
    public static float FlightTime(float d) { return 0.6f + 1.6f * Math.Max(0f, Math.Min(1f, (d - lobMinDist) / (castRange - lobMinDist))); }
    public static float Apex(float d) { return Math.Min(50f, 0.25f * d); }

    // ---- ground check: only open water counts (RoR2 world layer = terrain/buildings)
    static int worldMask = -1;
    static int WorldMask()
    {
        if (worldMask != -1) return worldMask;
        try
        {
            Type li = ror2.GetType("RoR2.LayerIndex");
            object w = li.GetField("world", All).GetValue(null);
            object m = Get(w, "mask");
            worldMask = Convert.ToInt32(Get(m, "value"));
            Log("world layer mask = " + worldMask);
        }
        catch (Exception e) { worldMask = 1; Log("world mask lookup failed, using layer 0: " + (e.InnerException ?? e).Message); }
        return worldMask;
    }

    // true = open water at (x,z); false = ground sticks out at/above the surface there (gy = its height)
    static bool OpenWater(float x, float z, out float gy)
    {
        gy = waterY;
        UnityEngine.RaycastHit h;
        if (UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, waterY + 200f, z), UnityEngine.Vector3.down, out h, 200.2f, WorldMask(), UnityEngine.QueryTriggerInteraction.Ignore)
            && h.point.y > waterY - 0.2f) { gy = h.point.y; return false; }
        return true;
    }

    static float GroundBelow(float x, float y, float z)
    {
        UnityEngine.RaycastHit h;
        if (UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, y + 50f, z), UnityEngine.Vector3.down, out h, 400f, WorldMask(), UnityEngine.QueryTriggerInteraction.Ignore)) return h.point.y;
        return y;
    }

    // returns 1 = lands on open water, 0 = lands on ground (bobber goes there, no fishing), -1 = auto-fish found no water
    static int RigCast(string name, bool auto)
    {
        try
        {
            Rig r = GetRig(name, true);
            if (r == null) return auto ? -1 : 0;
            float castMul = RangeMult(name);
            object foot = Get(r.body, "footPosition");
            object a = AimFlat(r.body);
            object ib = Get(r.body, "inputBank");
            object o = IsNull(ib) ? null : Get(ib, "aimOrigin");
            object dir = IsNull(ib) ? null : Get(ib, "aimDirection");
            float fx = F(foot, "x"), fy = F(foot, "y"), fz = F(foot, "z");
            string mode = "lob";
            bool landed = false, ground = false;
            float gx = 0, gyy = 0, gz = 0;
            if (auto && AutoAimFish(name, fx, fz, r)) { landed = true; mode = "auto aim at a fish"; }
            // 1) aiming down: the aim ray hits ground before the water -> ground; hits the water within 200 m -> there
            if (!landed && o != null && dir != null && F(dir, "y") < -0.01f)
            {
                var ov = (UnityEngine.Vector3)o; var dv = ((UnityEngine.Vector3)dir).normalized;
                float k = hasWater ? (waterY - ov.y) / dv.y : 400f;
                UnityEngine.RaycastHit h;
                if (UnityEngine.Physics.Raycast(ov, dv, out h, Math.Max(0.1f, Math.Min(k, 400f)), WorldMask(), UnityEngine.QueryTriggerInteraction.Ignore))
                {
                    float ddx = h.point.x - fx, ddz = h.point.z - fz;
                    if (ddx * ddx + ddz * ddz <= (castRange * castMul) * (castRange * castMul)) { landed = true; ground = true; gx = h.point.x; gyy = h.point.y; gz = h.point.z; mode = "aim-ray hit ground"; }
                }
                if (!landed && hasWater && k > 0)
                {
                    float hx = ov.x + dv.x * k, hz = ov.z + dv.z * k, ddx = hx - fx, ddz = hz - fz;
                    if (ddx * ddx + ddz * ddz <= (castRange * castMul) * (castRange * castMul)) { landed = true; r.tx = hx; r.tz = hz; mode = "aim-ray"; }
                }
            }
            // 2) otherwise a ballistic lob along the aim: pitch -10..+45 deg -> 15..200 m
            if (!landed)
            {
                float pitch = dir == null ? 0f : (float)(Math.Asin(Math.Max(-1f, Math.Min(1f, F(dir, "y")))) * 180.0 / Math.PI);
                float u = Math.Max(0f, Math.Min(1f, (pitch - lobPitchMin) / (lobPitchMax - lobPitchMin)));
                float dist = Math.Max((lobMinDist * castMul), Math.Min((castRange * castMul), Lerp((lobMinDist * castMul), (castRange * castMul), u)));
                r.tx = fx + F(a, "x") * dist; r.tz = fz + F(a, "z") * dist;
                mode = (castMul != 1f ? "x" + castMul.ToString("0.00") + " " : "") + "lob pitch " + pitch.ToString("0") + "deg";
            }
            // 3) the landing point must be open water (no ground at/above the surface there)
            if (!ground)
            {
                float gh;
                if (!hasWater) { ground = true; gx = r.tx; gz = r.tz; gyy = GroundBelow(r.tx, fy, r.tz); mode += ", no water"; }
                else if (!OpenWater(r.tx, r.tz, out gh)) { ground = true; gx = r.tx; gz = r.tz; gyy = gh; mode += ", ground under landing"; }
            }
            // 4) auto-fishing retries up to 8 random directions that land on water
            if (ground && auto)
            {
                if (!hasWater) return -1;
                bool found = false;
                for (int i = 0; i < 8 && !found; i++)
                {
                    double ang = rng.NextDouble() * Math.PI * 2, d = 15 + rng.NextDouble() * 45;
                    float tx = fx + (float)(Math.Cos(ang) * d), tz = fz + (float)(Math.Sin(ang) * d), gh;
                    if (OpenWater(tx, tz, out gh)) { r.tx = tx; r.tz = tz; found = true; mode = "auto random direction #" + (i + 1); }
                }
                if (!found) return -1;
                ground = false;
            }
            float gpY = ground ? GpPoolLanding(name, gx, gyy, gz) : GpNoPool(name);
            if (!float.IsNaN(gpY)) { ground = false; r.tx = gx; r.tz = gz; r.ty = gpY; }
            else if (ground) { r.tx = gx; r.ty = gyy + 0.1f; r.tz = gz; }
            else r.ty = waterY + 0.05f;
            float hd; { float hx2 = r.tx - fx, hz2 = r.tz - fz; hd = (float)Math.Sqrt(hx2 * hx2 + hz2 * hz2); }
            r.flight = FlightTime(hd); r.arcH = Apex(hd);
            Log(name + ": bobber target (" + r.tx.ToString("0.0") + ", " + r.ty.ToString("0.0") + ", " + r.tz.ToString("0.0") + ") " + mode + ", dist " + hd.ToString("0.0")
                + " m, flight " + r.flight.ToString("0.00") + " s -> " + (ground ? "GROUND (invalid)" : "water"));
            r.castTime = T; r.reelTime = -100f;
            ShowRig(r, true);
            return ground ? 0 : 1;
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("rig cast error: " + (e.InnerException ?? e)); return auto ? -1 : 0; }
    }

    static void EnsureRigs()   // every 0.5 s: every living player always carries a rod
    {
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                string n = Convert.ToString(Get(nu, "userName"));
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

    // runs every rendered frame: positions pole / bobber / line from each angler's state
    static void Render()
    {
        if (!enabled) return;
        if (chatDirty) HideTaggedLines();
        GpRender();
        try { HotRender(); } catch (Exception e) { if (rigErrors++ < 10) Log("hot render error: " + (e.InnerException ?? e)); }
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

                // pole: from the right shoulder, tilted up by 'tilt' degrees above the aim direction
                object core0 = Get(body, "corePosition");
                object a = AimFlat(body);
                float ax = F(a, "x"), az = F(a, "z");
                float rx = az, rz = -ax;                       // right vector
                float tilt;
                if (idle) tilt = 30f + (float)Math.Sin(t * 1.5f) * 2f;                                // held, idle
                else if (reeling) tilt = sinceReel < 0.25f ? Lerp(35f, 75f, sinceReel / 0.25f) : Lerp(75f, 30f, Math.Min(1f, (sinceReel - 0.25f) / 0.35f));   // yank back, settle
                else if (sinceCast < 0.15f) tilt = Lerp(35f, 80f, sinceCast / 0.15f);                  // wind up
                else if (sinceCast < 0.35f) tilt = Lerp(80f, 25f, (sinceCast - 0.15f) / 0.2f);         // whip forward
                else tilt = state == 2 ? 20f + (float)Math.Sin(t * 40f) * 6f : 30f + (float)Math.Sin(t * 1.5f) * 2f;
                double rad = tilt * Math.PI / 180.0;
                float dx = ax * (float)Math.Cos(rad), dy = (float)Math.Sin(rad), dz = az * (float)Math.Cos(rad);
                float baseX = F(core0, "x") + rx * 0.45f + ax * 0.2f, baseY = F(core0, "y") + 0.1f, baseZ = F(core0, "z") + rz * 0.45f + az * 0.2f;
                const float len = 2f;                           // unit cylinder is 2 m tall at y-scale 1
                tfPos.SetValue(r.poleTf, Vec(baseX + dx * len / 2, baseY + dy * len / 2, baseZ + dz * len / 2), null);
                tfRot.SetValue(r.poleTf, fromTo.Invoke(null, new object[] { Vec(0, 1, 0), Vec(dx, dy, dz) }), null);
                float tipX = baseX + dx * len, tipY = baseY + dy * len, tipZ = baseZ + dz * len;

                // bobber
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
                {   // parabola: linear in x/z, height = lerp + 4*apex*k*(1-k)
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
            }
            if (dead != null) foreach (string n in dead) { KillRig(rigs[n]); rigs.Remove(n); Log(n + ": rig removed (body changed/died)"); }
            RodSkinsFrame();
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("render error: " + (e.InnerException ?? e)); }
    }

    // ===================== idle auto-fishing for one guest =====================
    public static string AutoName = "";   // BepInEx: config Host.AutoFishPlayer ("" = off)
    static int tickNo;
    static readonly List<float[]> samples = new List<float[]>();   // {time, x, y, z}
    static object idleBody;
    static bool wasIdle;

    static void AutoFish()   // every 0.5 s
    {
        object body = Body(AutoName);
        bool alive = body != null && Convert.ToBoolean(Get(Get(body, "healthComponent"), "alive"));
        if (!alive || !ReferenceEquals(body, idleBody)) { samples.Clear(); idleBody = alive ? body : null; }
        bool idle = false;
        if (alive)
        {
            object p = Get(body, "footPosition");
            float x = F(p, "x"), y = F(p, "y"), z = F(p, "z");
            samples.Add(new[] { now, x, y, z });
            while (samples.Count > 0 && samples[0][0] < now - 10f) samples.RemoveAt(0);
            if (samples.Count > 0 && samples[0][0] <= now - 9.4f)
            {
                idle = true;
                foreach (float[] q in samples)
                {
                    float dx = q[1] - x, dy = q[2] - y, dz = q[3] - z;
                    if (dx * dx + dy * dy + dz * dz > 1f) { idle = false; break; }
                }
            }
        }
        if (idle != wasIdle) { Log(AutoName + " idle=" + idle + " (alive=" + alive + ", samples=" + samples.Count + ")"); wasIdle = idle; }

        Angler a; anglers.TryGetValue(AutoName, out a);
        if (idle)
        {
            if (a == null || (a.state == 0 && now >= a.readyAt)) Press(AutoName, true, true);
        }
        else if (a != null && a.auto && a.state != 0)
        {
            a.state = 0; a.auto = false; a.readyAt = now + 2f;
            RigReel(AutoName);
            Chat(AutoName + (alive ? " moved and stopped auto-fishing." : " stopped auto-fishing."));
            Log(AutoName + ": auto-fishing cancelled (" + (alive ? "moved" : "dead/no body") + ")");
        }
    }

    // ===================== water plane (local-only, recreated per stage) =====================
    public static object water;
    static bool hasWater, oldCleaned;
    static float waterY;
    static string waterScene;

    static void CleanOld()   // destroy rigs / water left by disabled older versions (main thread)
    {
        oldCleaned = true; GameplayCleanOld(); ZoneCleanOld();
        // hotspot discs left behind by older versions (they stop animating but stay visible)
        try
        {
            int n = 0;
            for (UnityEngine.GameObject g = UnityEngine.GameObject.Find("FishingHotspot"); g != null && g != hotGo && n < 50; g = UnityEngine.GameObject.Find("FishingHotspot"))
            { g.name = "FishingHotspot_old"; UnityEngine.Object.Destroy(g); n++; }
            Log("destroyed " + n + " old FishingHotspot object(s)");
        }
        catch (Exception e) { Log("old hotspot cleanup error: " + e.Message); }
        foreach (var asm in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(asm))
            {
                if (!IsOldVersion(t)) continue;
                try
                {
                    FieldInfo rf = t.GetField("rigs", All); MethodInfo kill = t.GetMethod("KillRig", All);
                    if (rf != null && kill != null)
                    {
                        IDictionary d = (IDictionary)rf.GetValue(null);
                        foreach (object r in d.Values) kill.Invoke(null, new object[] { r });
                        if (d.Count > 0) Log("cleaned " + d.Count + " rigs of " + t.Name);
                        d.Clear();
                    }
                    FieldInfo wf = t.GetField("water", All);
                    if (wf != null)
                    {
                        object w = wf.GetValue(null);
                        FieldInfo ys = t.GetField("waterY", All), sc = t.GetField("waterScene", All);
                        if (!IsNull(w) && ys != null && sc != null && Convert.ToString(sc.GetValue(null)) == SceneName() && !hasWater)
                        {   // adopt the existing water of this stage instead of rebuilding it
                            water = w; waterY = (float)ys.GetValue(null); waterScene = SceneName(); hasWater = true; waterPlacedAt = now;
                            wf.SetValue(null, null);
                            Log("adopted water of " + t.Name + " at Y=" + waterY.ToString("0.00"));
                        }
                        else if (!IsNull(w)) { destroy.Invoke(null, new object[] { w }); wf.SetValue(null, null); Log("cleaned water of " + t.Name); }
                    }
                }
                catch (Exception e) { Log("clean " + t.Name + " error: " + (e.InnerException ?? e).Message); }
            }
    }

    static string SceneName()
    {
        Type sm = core.GetType("UnityEngine.SceneManagement.SceneManager");
        object sc = sm.GetMethod("GetActiveScene").Invoke(null, null);
        return Convert.ToString(Get(sc, "name"));
    }

    static float highSince = -1f, waterPlacedAt = -100f;
    static void WaterTick()   // every 0.5 s
    {
        try
        {
            string scene = SceneName();
            // lowest GROUNDED living player's feet (players still in drop pods / airborne are ignored)
            float low = float.MaxValue;
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                object b = Body(Convert.ToString(Get(nu, "userName")));
                if (b == null || !Convert.ToBoolean(Get(Get(b, "healthComponent"), "alive"))) continue;
                object motor = Get(b, "characterMotor");
                if (IsNull(motor) || !Convert.ToBoolean(Get(motor, "isGrounded"))) continue;
                low = Math.Min(low, F(Get(b, "footPosition"), "y"));
            }
            if (scene == waterScene && hasWater && !IsNull(water))
            {   // height is FROZEN for the stage (touching water = drowning). Only exception: every grounded player has been
                // > 60 m above it for 30 s -> raise to lowest grounded - 3. Same GameObject is moved (material kept).
                if (low == float.MaxValue || low <= waterY + 60f) { highSince = -1f; return; }
                if (highSince < 0f) { highSince = now; return; }
                if (now - highSince >= 30f)
                {
                    highSince = -1f;
                    float old = waterY; waterY = low - 3f; waterPlacedAt = now;
                    object wtf = goTf.GetValue(water, null); object wp = tfPos.GetValue(wtf, null);
                    tfPos.SetValue(wtf, Vec(F(wp, "x"), waterY, F(wp, "z")), null);
                    Log("water moved on " + scene + " from Y=" + old.ToString("0.00") + " to Y=" + waterY.ToString("0.00") + " (lowest grounded foot " + low.ToString("0.00") + ")");
                }
                return;
            }
            if (scene != waterScene) { if (!IsNull(water)) destroy.Invoke(null, new object[] { water }); hasWater = false; water = null; }
            if (low == float.MaxValue) return;   // nobody spawned / landed yet
            waterY = low - 3f;
            object go = Prim("Cube", "FishingWater", 2000f, 0.05f, 2000f, Activator.CreateInstance(colorType, new object[] { 0.1f, 0.4f, 0.8f, 0.55f }));
            object rend = getComp.Invoke(go, new object[] { rendererType });
            object m = LineMaterial();
            if (m != null && !IsNull(rend))
            {
                object wm = Activator.CreateInstance(matType, new object[] { m });
                matColor.SetValue(wm, Activator.CreateInstance(colorType, new object[] { 0.1f, 0.4f, 0.8f, 0.55f }), null);
                Set(rend, "material", wm);
                try { Set(rend, "receiveShadows", false); Set(rend, "shadowCastingMode", Enum.Parse(rendererType.GetProperty("shadowCastingMode").PropertyType, "Off")); } catch { }
            }
            object tf = goTf.GetValue(go, null);
            object lp = Get(LocalBodyFoot(), "x") == null ? null : LocalBodyFoot();
            float cx = lp != null ? F(lp, "x") : 0f, cz = lp != null ? F(lp, "z") : 0f;
            tfPos.SetValue(tf, Vec(cx, waterY, cz), null);
            water = go; hasWater = true; waterScene = scene; waterPlacedAt = now; highSince = -1f;
            Log("water created on " + scene + " at Y=" + waterY.ToString("0.00") + " (lowest grounded foot " + low.ToString("0.00") + ")");
        }
        catch (Exception e) { if (rigErrors++ < 10) Log("water error: " + (e.InnerException ?? e)); }
    }

    static object LocalBodyFoot()
    {
        object b = Body(LocalName());
        return b == null ? null : Get(b, "footPosition");
    }

    // ===================== host input guard (game chat box / F2 request panel) =====================
    static Type reqType; static FieldInfo[] reqOpenFields = new FieldInfo[0]; static float reqLookupAt = -100f;
    static bool InputBlocked(string what)
    {
        if (ChatBoxOpen()) { Log("host " + what + " ignored: chat box open"); return true; }
        try
        {
            if (now - reqLookupAt > 10f)
            {
                reqLookupAt = now;
                Type best = null; int bestN = -1;
                foreach (var asm in FishingScope.Assemblies())
                    for (int n = 1; n <= 20; n++) { Type t = asm.GetType("ReqPanel" + n); if (t != null && n > bestN) { bestN = n; best = t; } }
                if (best != reqType)
                {
                    reqType = best;
                    var flds = new List<FieldInfo>(); var all = new List<string>();
                    if (best != null)
                        foreach (FieldInfo fi in best.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            if (fi.FieldType == typeof(bool))
                            {
                                all.Add(fi.Name);
                                string ln = fi.Name.ToLowerInvariant();
                                if (ln == "open" || ln == "isopen" || ln == "typing" || ln == "istyping" || ln == "inputopen" || ln == "panelopen") flds.Add(fi);
                            }
                    reqOpenFields = flds.ToArray();
                    Log("request panel type=" + (best == null ? "none" : best.Name) + " static bools=[" + string.Join(",", all.ToArray()) + "] using=["
                        + string.Join(",", flds.ConvertAll(x => x.Name).ToArray()) + "]");
                }
            }
            foreach (FieldInfo fi in reqOpenFields)
                if ((bool)fi.GetValue(null)) { Log("host " + what + " ignored: " + reqType.Name + "." + fi.Name + "=true"); return true; }
        }
        catch (Exception e) { if (errors++ < 10) Log("req panel check error: " + e.Message); }
        return false;
    }

    // ===================== drowning: touching the water kills (host, server-side) =====================
    static readonly Dictionary<string, object> drownBody = new Dictionary<string, object>();
    static readonly Dictionary<string, float> drownGrace = new Dictionary<string, float>();
    static readonly HashSet<object> drowned = new HashSet<object>();
    static readonly Dictionary<string, float> drownAnnounced = new Dictionary<string, float>();   // chat at most once per 60 s per player
    static void DrownTick()   // every 0.2 s
    {
        try
        {
            if (!hasWater || IsNull(water) || waterScene != SceneName()) return;
            bool stageGrace = now - waterPlacedAt < 5f;
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                string name = Convert.ToString(Get(nu, "userName"));
                object b = Body(name);
                if (b == null) { drownBody.Remove(name); continue; }
                object prevB; drownBody.TryGetValue(name, out prevB);
                object hc = Get(b, "healthComponent");
                bool alive = !IsNull(hc) && Convert.ToBoolean(Get(hc, "alive"));
                if (!ReferenceEquals(prevB, b)) { drownBody[name] = b; drownGrace[name] = now + 5f; }   // new body = spawn/respawn/revive
                if (!alive) continue;
                float g; drownGrace.TryGetValue(name, out g);
                if (stageGrace || now < g || drowned.Contains(b)) continue;
                float fy = F(Get(b, "footPosition"), "y");
                if (fy >= waterY + 0.1f || GpNoDrown(name)) continue;
                drowned.Add(b);
                MethodInfo sui = null;
                foreach (MethodInfo m in hc.GetType().GetMethods(All)) if (m.Name == "Suicide" && m.GetParameters().Length == 3) sui = m;
                ParameterInfo[] ps = sui.GetParameters();
                object dt = ps[2].ParameterType.IsValueType ? Activator.CreateInstance(ps[2].ParameterType) : null;
                sui.Invoke(hc, new object[] { null, null, dt });
                float lastAnn; drownAnnounced.TryGetValue(name, out lastAnn);
                if (lastAnn == 0f || now - lastAnn > 60f) { drownAnnounced[name] = now; Chat(name + " fell in the water and drowned! (spectating until the next stage)"); }
                Log("DROWNED " + name + ": foot Y=" + fy.ToString("0.00") + " water Y=" + waterY.ToString("0.00"));
            }
            if (drowned.Count > 200) drowned.Clear();
        }
        catch (Exception e) { if (errors++ < 10) Log("drown error: " + (e.InnerException ?? e)); }
    }

    // ===================== purple hotspot (best fish) =====================
    public static UnityEngine.Vector3 hotspotPos;
    public static float hotspotRadius = 6f;
    public static bool hasHotspot;
    static string hotScene;
    static float hotFarSince = -1f, hotBroadcastAt = -100f;
    static bool scoringHot;
    static UnityEngine.GameObject hotGo, hotDisc, hotHalo;
    static UnityEngine.Material hotDiscMat, hotHaloMat;
    static readonly List<UnityEngine.GameObject> sparks = new List<UnityEngine.GameObject>();
    static readonly List<UnityEngine.Material> sparkMats = new List<UnityEngine.Material>();
    static readonly List<float[]> sparkData = new List<float[]>();   // {angle, radius, phase, speed}

    static bool LandsInHotspot(string name)
    {
        if (!hasHotspot) return false;
        Rig r; if (!rigs.TryGetValue(name, out r)) return false;
        float dx = r.tx - hotspotPos.x, dz = r.tz - hotspotPos.z;
        return dx * dx + dz * dz <= hotspotRadius * hotspotRadius;
    }

    static List<UnityEngine.Vector3> LivingFeet()
    {
        var l = new List<UnityEngine.Vector3>();
        foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
        {
            object b = Body(Convert.ToString(Get(nu, "userName")));
            if (b == null || !Convert.ToBoolean(Get(Get(b, "healthComponent"), "alive"))) continue;
            l.Add((UnityEngine.Vector3)Get(b, "footPosition"));
        }
        return l;
    }

    static void PlaceHotspot(bool moving)
    {
        var feet = LivingFeet();
        if (feet.Count == 0) { hasHotspot = false; return; }
        UnityEngine.Vector3 old = hotspotPos; bool hadOld = hasHotspot;
        UnityEngine.Vector3 p = old;
        for (int i = 0; i < 40; i++)
        {
            UnityEngine.Vector3 f = feet[rng.Next(feet.Count)];
            double ang = rng.NextDouble() * Math.PI * 2, dist = 15 + rng.NextDouble() * 20;
            p = new UnityEngine.Vector3(f.x + (float)(Math.Cos(ang) * dist), waterY, f.z + (float)(Math.Sin(ang) * dist));
            float ox = p.x - old.x, oz = p.z - old.z;
            if (!moving || !hadOld || ox * ox + oz * oz >= 15f * 15f) break;
        }
        hotspotPos = p; hasHotspot = true; hotScene = SceneName(); hotFarSince = -1f;
        Log("hotspot " + (moving ? "moved" : "placed") + " on " + hotScene + " at (" + p.x.ToString("0.0") + ", " + p.y.ToString("0.00") + ", " + p.z.ToString("0.0") + ") r=" + hotspotRadius);
        BroadcastHot();
    }

    static void BroadcastHot()
    {
        hotBroadcastAt = now;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        ChatRaw("[FT] HOT|" + hotspotPos.x.ToString("0.00", ci) + "|" + hotspotPos.y.ToString("0.00", ci) + "|" + hotspotPos.z.ToString("0.00", ci) + "|" + hotspotRadius.ToString("0.00", ci));
    }

    static void HotspotTick()   // every 0.5 s
    {
        try
        {
            string scene = SceneName();
            if (!hasWater || IsNull(water) || waterScene != scene) { if (hotScene != scene) hasHotspot = false; return; }
            if (!hasHotspot || hotScene != scene) { PlaceHotspot(false); if (!hasHotspot) return; }
            if (Math.Abs(hotspotPos.y - waterY) > 0.01f) { hotspotPos.y = waterY; Log("hotspot follows water to Y=" + waterY.ToString("0.00")); BroadcastHot(); }
            // re-place if every living player is > 120 m away for 30 s
            var feet = LivingFeet(); bool near = false;
            foreach (var f in feet) { float dx = f.x - hotspotPos.x, dz = f.z - hotspotPos.z; if (dx * dx + dz * dz <= 120f * 120f) { near = true; break; } }
            if (near || feet.Count == 0) hotFarSince = -1f;
            else if (hotFarSince < 0f) hotFarSince = now;
            else if (now - hotFarSince >= 30f) { Log("hotspot: everyone > 120 m away for 30 s"); PlaceHotspot(true); }
            if (now - hotBroadcastAt >= 20f) BroadcastHot();
            EnsureHotVisual();
        }
        catch (Exception e) { if (errors++ < 10) Log("hotspot error: " + (e.InnerException ?? e)); }
    }

    static UnityEngine.Material HotMat(float r, float g, float b, float a, int queue)
    {
        UnityEngine.Shader sh = UnityEngine.Shader.Find("Sprites/Default");
        if (sh == null) sh = UnityEngine.Shader.Find("Hidden/Internal-Colored");
        var m = new UnityEngine.Material(sh);
        m.color = new UnityEngine.Color(r, g, b, a);
        m.renderQueue = queue;
        return m;
    }

    static UnityEngine.GameObject HotPrim(string kind, string name, UnityEngine.Material mat)
    {
        var go = (UnityEngine.GameObject)Prim(kind, name, 1f, 1f, 1f, Col(1, 1, 1));
        var rend = go.GetComponent<UnityEngine.Renderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rend.receiveShadows = false;
        go.transform.SetParent(hotGo.transform, false);
        return go;
    }

    static void EnsureHotVisual()
    {
        if (hotGo != null) return;   // Unity null: destroyed on scene change -> recreated here
        hotGo = new UnityEngine.GameObject("FishingHotspot");
        hotDiscMat = HotMat(0.65f, 0.2f, 1f, 0.55f, 3003);
        hotHaloMat = HotMat(0.7f, 0.3f, 1f, 0.2f, 3002);
        hotDisc = HotPrim("Cylinder", "HotspotDisc", hotDiscMat);
        hotHalo = HotPrim("Cylinder", "HotspotHalo", hotHaloMat);
        sparks.Clear(); sparkMats.Clear(); sparkData.Clear();
        for (int i = 0; i < 10; i++)
        {
            var m = HotMat(0.85f, 0.55f, 1f, 0.9f, 3004);
            sparks.Add(HotPrim("Cube", "HotspotSpark", m)); sparkMats.Add(m);
            sparkData.Add(new[] { (float)(rng.NextDouble() * Math.PI * 2), (float)(rng.NextDouble() * 5.5), (float)rng.NextDouble(), 0.25f + (float)rng.NextDouble() * 0.25f });
        }
        Log("hotspot disc created (FishingHotspot: disc r=" + hotspotRadius + " q3003, halo r=8 q3002, " + sparks.Count + " sparkles q3004)");
    }

    static void HotRender()   // every frame
    {
        if (hotGo == null) return;
        bool show = hasHotspot && hasWater && hotScene == waterScene;
        if (hotGo.activeSelf != show) hotGo.SetActive(show);
        if (!show) return;
        float t = T;
        hotGo.transform.position = new UnityEngine.Vector3(hotspotPos.x, hotspotPos.y + 0.05f, hotspotPos.z);
        float pulse = (float)Math.Sin(t * 2.2f);
        float d = hotspotRadius * 2f * (1f + 0.04f * pulse);
        hotDisc.transform.localScale = new UnityEngine.Vector3(d, 0.01f, d);
        hotDiscMat.color = new UnityEngine.Color(0.65f, 0.2f, 1f, 0.5f + 0.12f * pulse);
        float h = 16f * (1f + 0.06f * (float)Math.Sin(t * 1.4f + 1f));
        hotHalo.transform.localScale = new UnityEngine.Vector3(h, 0.005f, h);
        hotHalo.transform.localPosition = new UnityEngine.Vector3(0, -0.01f, 0);
        hotHaloMat.color = new UnityEngine.Color(0.7f, 0.3f, 1f, 0.18f + 0.08f * (float)Math.Sin(t * 1.4f + 1f));
        for (int i = 0; i < sparks.Count; i++)
        {
            float[] s = sparkData[i];
            float k = (t * s[3] + s[2]) % 1f;
            float a = s[0] + t * 0.3f;
            sparks[i].transform.localPosition = new UnityEngine.Vector3((float)Math.Cos(a) * s[1], k * 3f, (float)Math.Sin(a) * s[1]);
            float sz = 0.18f * (1f - k * 0.5f);
            sparks[i].transform.localScale = new UnityEngine.Vector3(sz, sz, sz);
            sparks[i].transform.rotation = UnityEngine.Quaternion.Euler(t * 90f + i * 30f, t * 70f, 0);
            sparkMats[i].color = new UnityEngine.Color(0.85f, 0.55f, 1f, 0.9f * (1f - k));
        }
    }

    // ===================== guests: primary-skill use = left click =====================
    class ClickState { public object body; public object[] machines; public Type primType; public bool init; public float lastActive = -10f, lastClick = -10f;
                       public readonly Dictionary<object, object> last = new Dictionary<object, object>(); }
    static readonly Dictionary<string, ClickState> clicks = new Dictionary<string, ClickState>();
    static Type esmType;

    static void GuestClicks()   // every tick; a few reflection reads per guest
    {
        string local = LocalName();
        if (esmType == null) esmType = ror2.GetType("RoR2.EntityStateMachine");
        foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
        {
            string name = Convert.ToString(Get(nu, "userName"));
            if (name == local) continue;
            object master = Get(nu, "master");
            object body = IsNull(master) ? null : master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
            if (IsNull(body)) { clicks.Remove(name); continue; }
            ClickState cs;
            if (!clicks.TryGetValue(name, out cs) || !ReferenceEquals(cs.body, body))
            {
                cs = new ClickState { body = body };
                object go = Get(body, "gameObject");
                Array arr = (Array)go.GetType().GetMethod("GetComponents", new[] { typeof(Type) }).Invoke(go, new object[] { esmType });
                cs.machines = new object[arr.Length]; arr.CopyTo(cs.machines, 0);
                cs.primType = Get(Get(Get(Get(Get(body, "skillLocator"), "primary"), "skillDef"), "activationState"), "stateType") as Type;
                clicks[name] = cs;
                Log(name + ": click tracking on " + cs.machines.Length + " state machines, primary state=" + (cs.primType == null ? "?" : cs.primType.Name));
            }
            if (cs.primType == null) continue;
            bool activeNow = false, entered = false;
            foreach (object esm in cs.machines)
            {
                if (IsNull(esm)) continue;
                object st = Get(esm, "state");
                object prev; cs.last.TryGetValue(esm, out prev);
                cs.last[esm] = st;
                if (st != null && st.GetType() == cs.primType)
                {
                    activeNow = true;
                    if (cs.init && !ReferenceEquals(st, prev)) entered = true;
                }
            }
            cs.init = true;
            // a "click" = primary freshly entered after >0.3 s without it (held auto-fire doesn't spam), debounced 0.4 s
            if (entered && now - cs.lastActive > 0.3f && now - cs.lastClick > 0.4f)
            {
                cs.lastClick = now;
                Log(name + ": primary-skill click detected");
                Press(name, true);
            }
            if (activeNow) cs.lastActive = now;
        }
    }

    // ===================== ROD WHIP (right click) =====================
    public static bool whipEnabled = true;
    public static float whipCooldown = 1.5f, whipRange = 12f, whipAngle = 35f;
    public static int dryRunWhips;          // >0: next tick logs who the local player WOULD hit (no damage, no cooldown)
    public static bool testDroneDamage;     // true: next tick applies the whip damage to an allied NON-player body (test)
    public static int whipCount;
    static bool rWasDown;
    static readonly Dictionary<string, float> whipReadyAt = new Dictionary<string, float>();
    static Type pauseType;
    static int menuLogged;

    static bool MenuOpen()   // ESC/pause menu up on the host: right clicks there are UI clicks
    {
        try
        {
            if (pauseType == null) pauseType = ror2.GetType("RoR2.UI.PauseScreenController");
            IList l = pauseType == null ? null : GetStatic(pauseType, "instancesList") as IList;
            if (l != null && l.Count > 0) { if (menuLogged++ < 5) Log("host right click ignored: pause menu open"); return true; }
        }
        catch { }
        return false;
    }

    static string V(UnityEngine.Vector3 v) { return "(" + v.x.ToString("0.0") + ", " + v.y.ToString("0.0") + ", " + v.z.ToString("0.0") + ")"; }

    static bool Alive(object body)
    {
        if (body == null) return false;
        object hc = Get(body, "healthComponent");
        return !IsNull(hc) && Convert.ToBoolean(Get(hc, "alive"));
    }

    public static string Whip(string attacker, string how) { return WhipCore(attacker, how, false); }

    // main thread only (Tick / chat handling run in onFixedUpdate)
    public static string WhipCore(string attacker, string how, bool dryRun)
    {
        try
        {
            if (attacker == null) return "no attacker";
            if (!dryRun)
            {
                float ready;
                if (whipReadyAt.TryGetValue(attacker, out ready) && now < ready) { Log("WHIP " + attacker + " (" + how + "): on cooldown (" + (ready - now).ToString("0.00") + " s left)"); return "cooldown"; }
            }
            object ab = Body(attacker);
            if (!Alive(ab)) { Log("WHIP " + attacker + " (" + how + "): attacker has no living body"); return "no body"; }
            if (!dryRun) whipReadyAt[attacker] = now + whipCooldown;
            var origin = (UnityEngine.Vector3)Get(ab, "corePosition");
            object ib = Get(ab, "inputBank");
            object ad = IsNull(ib) ? null : Get(ib, "aimDirection");
            UnityEngine.Vector3 aim = ad != null ? (UnityEngine.Vector3)ad : (UnityEngine.Vector3)Get(Get(ab, "transform"), "forward");
            if (aim.sqrMagnitude < 0.0001f) aim = UnityEngine.Vector3.forward;
            aim.Normalize();

            string best = null; object bestBody = null; float bestDist = float.MaxValue;
            var seen = new List<string>();
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            {
                string n = Convert.ToString(Get(nu, "userName"));
                if (n == attacker) continue;
                object tb = Body(n);
                if (!Alive(tb)) { seen.Add(n + ":dead/none"); continue; }
                var d = (UnityEngine.Vector3)Get(tb, "corePosition") - origin;
                float dist = d.magnitude;
                float ang = dist < 0.01f ? 0f : UnityEngine.Vector3.Angle(aim, d);
                bool ok = dist <= whipRange && (ang <= whipAngle || (dist <= 2.5f && ang <= 70f));
                seen.Add(n + ":" + dist.ToString("0.0") + "m/" + ang.ToString("0") + "deg" + (ok ? "*" : ""));
                if (ok && dist < bestDist) { bestDist = dist; best = n; bestBody = tb; }
            }
            string info = attacker + " (" + how + ") at " + V(origin) + " aim " + V(aim) + " candidates [" + string.Join(", ", seen.ToArray()) + "]";
            if (dryRun)
            {
                string cur = "?";
                try { cur = UnityEngine.Cursor.lockState + "/visible=" + UnityEngine.Cursor.visible; } catch { }
                Log("WHIP DRY-RUN " + info + " -> would hit " + (best ?? "nobody") + " (no damage applied; cursor " + cur + ", pause menu " + MenuOpen() + ")");
                return best ?? "nobody";
            }
            if (best == null) { Log("WHIP " + info + " -> miss (nobody in range)"); return "miss"; }

            string dmg = ApplyWhipDamage(ab, bestBody);
            bool hadLine = ForceReel(best);
            whipCount++;
            Chat(attacker + " whipped " + best + " with a fishing rod!" + (hadLine ? " Their line snapped back." : ""));
            if (hadLine) Chat("The fish got away from " + best + "!");   // older FISHING.exe clients reel the rig on this line
            ChatRaw("[FT] WHIP|" + attacker + "|" + best);
            HitFx((UnityEngine.Vector3)Get(bestBody, "corePosition"));
            Log("WHIP " + info + " -> HIT " + best + " at " + bestDist.ToString("0.0") + " m; " + dmg + "; line " + (hadLine ? "snapped back (cast cancelled, no catch)" : "was not out"));
            return best;
        }
        catch (Exception e) { if (errors++ < 20) Log("whip error: " + (e.InnerException ?? e)); return "error"; }
    }

    // exactly 1 damage, server-side. attacker = null on purpose: with an attacker on the same team TakeDamage runs
    // FriendlyFireManager.ShouldDirectHitProceed (rejects when friendly fire is off) and scales by friendlyFireScaling.
    // The whipper is passed as the inflictor instead. BypassArmor|BypassBlock keep it at exactly 1; NonLethal can't kill.
    static string ApplyWhipDamage(object attackerBody, object targetBody)
    {
        object hc = Get(targetBody, "healthComponent");
        if (IsNull(hc)) return "no healthComponent";
        float before = Convert.ToSingle(Get(hc, "combinedHealth"));
        Type diT = ror2.GetType("RoR2.DamageInfo");
        object di = Activator.CreateInstance(diT);
        Set(di, "damage", 1f);
        Set(di, "attacker", null);
        Set(di, "inflictor", Get(attackerBody, "gameObject"));
        Set(di, "position", Get(targetBody, "corePosition"));
        Set(di, "procCoefficient", 0f);
        Set(di, "crit", false);
        Set(di, "force", GpWhipForce(attackerBody, targetBody));
        Type dtT = ror2.GetType("RoR2.DamageType"), comboT = ror2.GetType("RoR2.DamageTypeCombo");
        object flags = Enum.Parse(dtT, "BypassArmor, BypassBlock, NonLethal");
        object combo = null;
        foreach (MethodInfo m in comboT.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == "op_Implicit" && m.ReturnType == comboT && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == dtT) { combo = m.Invoke(null, new[] { flags }); break; }
        if (combo != null) Set(di, "damageType", combo);
        hc.GetType().GetMethod("TakeDamage", new[] { diT }).Invoke(hc, new[] { di });
        float after = Convert.ToSingle(Get(hc, "combinedHealth"));
        return "damage: combinedHealth " + before.ToString("0.00") + " -> " + after.ToString("0.00") + " (delta " + (before - after).ToString("0.00") + ", rejected=" + Get(di, "rejected") + ", damageType set=" + (combo != null) + ")";
    }

    // cancel the target's cast exactly like a missed bite: no catch, no score, rod rig reels back
    static bool ForceReel(string name)
    {
        Angler a;
        if (!anglers.TryGetValue(name, out a) || a.state == 0) return false;
        int was = a.state;
        a.state = 0; a.auto = false; a.hot = false; a.readyAt = now + 2f;
        RigReel(name);
        Log(name + ": line force-reeled by a whip (state was " + (was == 1 ? "waiting" : was == 2 ? "BITING" : was == 3 ? "ground cast" : was.ToString()) + ") - fish got away");
        return true;
    }

    static void HitFx(UnityEngine.Vector3 pos)
    {
        try
        {
            if (bobberFx == null) return;
            object ed = Activator.CreateInstance(ror2.GetType("RoR2.EffectData"));
            Set(ed, "origin", pos);
            Set(ed, "scale", 0.6f);
            foreach (MethodInfo m in ror2.GetType("RoR2.EffectManager").GetMethods(All))
                if (m.Name == "SpawnEffect" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "GameObject")
                { m.Invoke(null, new object[] { bobberFx, ed, true }); break; }
        }
        catch (Exception e) { if (errors++ < 20) Log("whip fx error: " + (e.InnerException ?? e).Message); }
    }

    // test only: whip damage from the local player onto an allied NON-player body (a drone / turret), logs the HP change
    static void TestDroneDamage()
    {
        try
        {
            object ab = Body(LocalName());
            if (ab == null) { Log("WHIP TEST: no local body"); return; }
            object myTeam = Get(Get(ab, "teamComponent"), "teamIndex");
            Type pcmc = ror2.GetType("RoR2.PlayerCharacterMasterController");
            foreach (object b in (IEnumerable)GetStatic(ror2.GetType("RoR2.CharacterBody"), "readOnlyInstancesList"))
            {
                if (ReferenceEquals(b, ab) || !Alive(b)) continue;
                if (!Equals(Get(Get(b, "teamComponent"), "teamIndex"), myTeam)) continue;
                object master = Get(b, "master");
                object go = IsNull(master) ? null : Get(master, "gameObject");
                if (go != null && !IsNull(go.GetType().GetMethod("GetComponent", new[] { typeof(Type) }).Invoke(go, new object[] { pcmc }))) continue;   // a player
                Log("WHIP TEST on allied non-player " + Get(b, "name") + " (same team " + myTeam + " as the attacker): " + ApplyWhipDamage(ab, b));
                return;
            }
            Log("WHIP TEST: no allied non-player body found");
        }
        catch (Exception e) { Log("WHIP TEST error: " + (e.InnerException ?? e)); }
    }

    // ===================== FISHING TOURNAMENT =====================
    public static Dictionary<string, int> money = new Dictionary<string, int>();
    public static Dictionary<string, Dictionary<string, int>> collections = new Dictionary<string, Dictionary<string, int>>();
    public static readonly Dictionary<string, int> doubleLeft = new Dictionary<string, int>();
    public static readonly string[] Species = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish", "Bass", "Salmon", "Pufferfish",
        "Swordfish", "Golden Koi", "Anglerfish", "Moon Eel", "Ghost Fish", "Old Boot" };
    public static readonly string[] NewSpecies = { "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };
    // movement perks (the real effect is applied by FishItems on each player's own game; this is the text)
    public static readonly Dictionary<string, string> PerkText = new Dictionary<string, string> {
        { "Minnow", "+2% move speed" }, { "Perch", "+2% sprint speed" }, { "Trout", "+3% jump height" }, { "Bass", "+1% move speed, +1% jump height" },
        { "Salmon", "+1 extra jump per 3 Salmon" }, { "Swordfish", "+5% move speed" }, { "Golden Koi", "+4% sprint speed, next 3 catches x2" },
        { "Moon Eel", "free revive" }, { "Old Boot", "+2% fishing luck" },
        { "Clownfish", "+2% jump height" }, { "Catfish", "+1% move speed, +1% sprint speed" }, { "Pufferfish", "+1 extra jump per 3 Pufferfish" },
        { "Anglerfish", "+6% sprint speed" }, { "Ghost Fish", "+6% move speed, +6% jump height" } };
    static readonly Dictionary<string, string> defaultPerkText = new Dictionary<string, string>(PerkText);
    // ONE visible "carrier" item per caught fish (re-skinned as that fish by another mod); no item droplets any more
    public static Dictionary<string, string> fishCarrierItem = new Dictionary<string, string> {
        { "Minnow", "Crowbar" }, { "Perch", "BleedOnHit" }, { "Trout", "StickyBomb" }, { "Bass", "IgniteOnKill" }, { "Salmon", "StunChanceOnHit" },
        { "Swordfish", "Missile" }, { "Golden Koi", "ChainLightning" }, { "Moon Eel", "ExtraLife" }, { "Old Boot", "ScrapWhite" },
        { "Clownfish", "FireRing" }, { "Catfish", "NearbyDamageBonus" }, { "Pufferfish", "BounceNearby" }, { "Anglerfish", "Behemoth" }, { "Ghost Fish", "ShockNearby" } };
    static readonly Dictionary<string, string> defaultCarrier = new Dictionary<string, string>(fishCarrierItem);
    static readonly Dictionary<string, string[]> perkItems = new Dictionary<string, string[]> {
        { "Minnow", new[] { "Hoof" } }, { "Perch", new[] { "Syringe" } }, { "Trout", new[] { "HealWhileSafe" } },
        { "Bass", new[] { "Bear" } }, { "Salmon", new[] { "Feather" } }, { "Swordfish", new[] { "CritGlasses", "BoostDamage", "BoostDamage" } },
        { "Golden Koi", new[] { "Tooth" } }, { "Moon Eel", new[] { "ExtraLife" } }, { "Old Boot", new[] { "ScrapWhite" } },
        { "Clownfish", new[] { "FireRing" } }, { "Catfish", new[] { "NearbyDamageBonus" } }, { "Pufferfish", new[] { "BounceNearby" } },
        { "Anglerfish", new[] { "Behemoth" } }, { "Ghost Fish", new[] { "ShockNearby" } } };
    static readonly Dictionary<string, object> itemIdx = new Dictionary<string, object>();
    public static string hudLocalName;
    static int totalCatches;
    static bool announced;
    static object hudGo;

    static void InitPerks()
    {
        MethodInfo find = ror2.GetType("RoR2.ItemCatalog").GetMethod("FindItemIndex", All);
        foreach (var kv in perkItems)
            foreach (string n in kv.Value)
            {
                if (itemIdx.ContainsKey(n)) continue;
                object idx = find.Invoke(null, new object[] { n });
                int v = Convert.ToInt32(idx);
                Log("perk item " + n + " -> " + v);
                if (v >= 0) itemIdx[n] = idx;
            }
    }

    static void ChatRaw(string text)
    {
        try
        {
            Type chat = ror2.GetType("RoR2.Chat");
            object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
            Set(msg, "baseToken", text);
            foreach (MethodInfo m in chat.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
        }
        catch (Exception e) { Log("chatraw error: " + e.Message); }
        Log("CHAT " + text);
    }

    static void TourneyTick()
    {
        if (!announced)
        {
            announced = true;
            Chat("FISHING TOURNAMENT started! Every catch scores. Type !scores for the leaderboard, !collection for your fish.");
        }
        if (tickNo % 25 == 0) hudLocalName = LocalName();
        if (hudGo == null)
        {
            hudGo = new UnityEngine.GameObject("FishHud25");
            UnityEngine.Object.DontDestroyOnLoad((UnityEngine.GameObject)hudGo);
            ((UnityEngine.GameObject)hudGo).AddComponent<FishHud25>();
            Log("HUD object created");
        }
    }

    static void Score(string name, string species, int baseValue, string itemText)
    {
        int left; doubleLeft.TryGetValue(name, out left);
        bool dbl = left > 0 && baseValue > 0;
        if (dbl) doubleLeft[name] = left - 1;
        int value = dbl ? baseValue * 2 : baseValue;
        value = GpValue(name, species, value);
        int total; money.TryGetValue(name, out total); total += value; money[name] = total;
        Dictionary<string, int> c; if (!collections.TryGetValue(name, out c)) { c = new Dictionary<string, int>(); collections[name] = c; }
        int n; c.TryGetValue(species, out n); c[species] = n + 1;
        if (species == "Golden Koi") { int dl; doubleLeft.TryGetValue(name, out dl); doubleLeft[name] = dl + 3; }

        string perk = GivePerk(name, species);
        if (species != "Old Boot")
            Chat(name + " caught a " + species + (scoringHot ? " (hotspot!)" : "") + " worth $" + value + (dbl ? " (Golden Koi x2 bonus!)" : "") + "! (total $" + total + ")"
                 + (itemText != null ? " + " + itemText : "") + " - perk " + perk + (catchNote ?? ""));
        ChatRaw("[FT] CATCH|" + name + "|" + species + "|" + value + "|" + total);
        ChatRaw("[FT] PERK|" + name + "|" + species + "|" + perk);
        if (++totalCatches % 5 == 0) Board();
    }

    static string GivePerk(string name, string species)
    {
        string desc; PerkText.TryGetValue(species, out desc);
        // the live fishCarrierItem map decides the visible item (other mods may remap it at runtime);
        // Swordfish keeps its 2 hidden BoostDamage
        var list = new List<string>();
        string carrier; if (fishCarrierItem.TryGetValue(species, out carrier) && !string.IsNullOrEmpty(carrier)) list.Add(carrier);
        if (species == "Swordfish") { list.Add("BoostDamage"); list.Add("BoostDamage"); }
        if (list.Count == 0) return desc ?? "nothing";
        string[] items = list.ToArray();
        try
        {
            object inv = Get(Get(FindUser(name), "master"), "inventory");
            if (IsNull(inv)) { Log("perk: no inventory for " + name); return desc; }
            foreach (string it in items)
            {
                object idx;
                if (!itemIdx.TryGetValue(it, out idx))
                {
                    idx = ror2.GetType("RoR2.ItemCatalog").GetMethod("FindItemIndex", All).Invoke(null, new object[] { it });
                    if (Convert.ToInt32(idx) < 0) { Log("perk: unknown item " + it); continue; }
                    itemIdx[it] = idx;
                }
                foreach (MethodInfo m in inv.GetType().GetMethods(All))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name == "GiveItem" && ps.Length == 2 && ps[0].ParameterType == idx.GetType() && ps[1].ParameterType == typeof(int))
                    { m.Invoke(inv, new object[] { idx, 1 }); break; }
                }
            }
            Log("perk: " + name + " got " + string.Join("+", items) + " for " + species);
        }
        catch (Exception e) { Log("perk error: " + (e.InnerException ?? e)); }
        return desc;
    }

    public static List<KeyValuePair<string, int>> Standings()
    {
        var all = new Dictionary<string, int>(money);
        try
        {
            foreach (object nu in (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"))
            { string n = Convert.ToString(Get(nu, "userName")); if (!all.ContainsKey(n)) all[n] = 0; }
        }
        catch { }
        var list = new List<KeyValuePair<string, int>>(all);
        list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.Compare(a.Key, b.Key));
        return list;
    }

    static void Board()
    {
        var parts = new List<string>();
        foreach (var kv in Standings()) parts.Add(kv.Key + ":" + kv.Value);
        ChatRaw("[FT] BOARD|" + string.Join("|", parts.ToArray()));
    }

    static void Coll(string name)
    {
        var parts = new List<string>();
        Dictionary<string, int> c;
        if (collections.TryGetValue(name, out c)) foreach (string sp in Species) if (c.ContainsKey(sp)) parts.Add(sp + ":" + c[sp]);
        ChatRaw("[FT] COLL|" + name + (parts.Count > 0 ? "|" + string.Join("|", parts.ToArray()) : ""));
    }
}

// JV94's on-screen tournament panel (IMGUI), right side of the screen
public class FishHud25 : UnityEngine.MonoBehaviour
{
    UnityEngine.GUIStyle style, box;
    void OnGUI()
    {
        try
        {
            if (!Fishing25.enabled) return;
            if (!logged) { logged = true; System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_fishing.log"), System.DateTime.Now.ToString("HH:mm:ss ") + "HUD drawn (OnGUI) " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + System.Environment.NewLine); }
            string me = Fishing25.hudLocalName ?? "";
            if (style == null)
            {
                style = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label); style.richText = true; style.fontSize = 15; style.wordWrap = false;
                box = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.box);
            }
            var sb = new System.Text.StringBuilder();
            int myMoney; Fishing25.money.TryGetValue(me, out myMoney);
            sb.Append("<b><color=#66ccff>FISHING TOURNAMENT</color></b>\n");
            sb.Append("Your money: <color=#ffd700>$" + myMoney + "</color>\n");
            int dl; Fishing25.doubleLeft.TryGetValue(me, out dl);
            if (dl > 0) sb.Append("<color=#ffd700>Golden Koi: next " + dl + " catches x2</color>\n");
            int gpLines = Fishing25.GameplayHud(sb, me);
            sb.Append("\n<b>FISH INVENTORY</b>\n");
            System.Collections.Generic.Dictionary<string, int> c;
            int lines = 0;
            if (Fishing25.collections.TryGetValue(me, out c))
                foreach (string sp in Fishing25.Species)
                    if (c.ContainsKey(sp)) { sb.Append(sp + " x" + c[sp] + "  <color=#aaaaaa>(" + Fishing25.PerkText[sp] + ")</color>\n"); lines++; }
            if (lines == 0) { sb.Append("<color=#aaaaaa>(nothing yet - click or press F)</color>\n"); lines = 1; }
            sb.Append("\n<b>LEADERBOARD</b>\n");
            var st = Fishing25.Standings();
            for (int i = 0; i < st.Count && i < 4; i++)
                sb.Append((i + 1) + ". " + (st[i].Key == me ? "<color=#ffd700>" + st[i].Key + "</color>" : st[i].Key) + "  $" + st[i].Value + "\n");
            int total = 6 + gpLines + lines + Math.Min(4, st.Count) + (dl > 0 ? 1 : 0);
            float w = 330, h = total * 19 + 16;
            var r = new UnityEngine.Rect(UnityEngine.Screen.width - w - 12, UnityEngine.Screen.height * 0.28f, w, h);
            UnityEngine.GUI.Box(r, "", box);
            UnityEngine.GUI.Box(r, "", box);   // twice = darker background
            UnityEngine.GUI.Label(new UnityEngine.Rect(r.x + 10, r.y + 6, w - 20, h - 10), sb.ToString(), style);
        }
        catch (Exception e) { if (errs++ < 3) System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_fishing.log"), "HUD error: " + e + Environment.NewLine); }
    }
    static int errs;
    static bool logged;
}
















