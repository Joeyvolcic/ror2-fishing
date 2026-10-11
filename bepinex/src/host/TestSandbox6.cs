// TEST SANDBOX for the Risk of Rain 2 fishing game (host add-on, JV94's game). Template: TestSandbox6 -> TestSandbox<N>.
//
//  !sandbox             (host) build a small test area far from the stage (SandboxLook.cs: 60 x 60 m deck, a pool cut into it
//                       that shows the moved lake, a short dock, sign, 2 whip dummies) and go there. The lake slides under it,
//                       the ROD SHOP stall + FISH MERCHANT move onto the deck, the purple hotspot goes into the pool, the golden
//                       one off the dock end, daylight (12:00, slow clock), clear weather, sandbox wallet $10,000.
//  !sandbox leave       back to the exact spot; real money / items / fish / lures / pet / time / weather / lake / shops restored.
//  !sandbox force       open even with guests online (the lake + hotspots + shops leave the map for everybody meanwhile)
//  !test                show the queue (C:\RoR2-FishingClient\test_queue.json) | !test <number|id> start a feature test
//                       (applies its presets, shows its steps as a checklist) | !test next (next untested) | !test reset
//                       (re-apply the presets) | !test off | !check <n> tick a step
//  !note <text>         comment on the feature under test (verdict null) | !pass <text> | !fail <text>
//                       -> appended to C:\RoR2-FishingClient\test_results.jsonl, chat "Noted for <feature>"
//  !test sync           re-read test_queue.json + test_results.jsonl (no re-inject needed; also picked up by itself every 5 s)
//  !sandbox goto <spot> stall | merchant | pool | dock | dummy | spawn | gold (golden hotspot) | hotspot - also works on the map
//  F6                   panel focus: number keys 1-0 / click a row = start that test, PageUp/PageDown page, Esc/F6 unfocus.
//  N                    "Write a note" box for the current test: type, Enter = note, Shift+Enter = PASS, Ctrl+Enter = FAIL, Esc = cancel.
// INPUT OWNER: while the panel has focus or the note box is open the sandbox owns the keyboard: inputOwner = "panel"|"note",
//   AppDomain data "FT.InputOwner", and ReqPanel17.open = true (Inventory<N> ORs ReqPanel1-18.open into the ReqPanel19/20 shim that
//   Fishing<N>.InputBlocked reads). The other add-ons' current versions don't know the flag, so their key edges are held by
//   reflection (RodStand keyWas[], GuideBook gDown.., Fishing fWasDown.., WhipAnim rightWasDown), the merchant menu is dismissed,
//   Inventory/Flashlight keys are parked on KeyCode.None, IMGUI keyboardControl is taken (FeatureVote ignores keys then) and the
//   game's own input is blocked like ReqPanel1/InvLook (selected focus object + Rewired keyboard/mouse maps off + cursor).
// Presets (feature "setup" lists): time:<h|night|golden|day>  night  golden  weather:<Clear|Rain|Storm|Night|Golden hour, + combines>
//   money:<n>  give:<item|case skin[@wear]|gadgets|caseskin|casebobber|case:common>  equip:<item|case skin|lure>  fish:<species>x<n>
//   pet:<type>  spawnschool[:species]  hotspot:near  hotspot:gold  goto:<spot>  lightning[:fast]  bite:fast|<seconds>
//   fishatbobber[:species] (every cast gets a fish right at the bobber)  spawnfish:bobber (one now, at the line that is out)
// Everything a test gives is temporary: the player's real state is snapshotted on entry and put back on every test start,
// on leave and on a stage change (persistent parts also in C:\RoR2-FishingClient\host\sandbox_snapshot.txt for a crash).
// Mutually exclusive with the Tropical Island (Island<N>): sandbox refuses while it is open and switches Island<N> off while
// the sandbox is open. Feature polls (FeatureVote<N>.autoPolls) and tournament map rotation / auto-end are held while inside.
// Log %TEMP%\ror2_sandbox.log. Test hooks: lines in %TEMP%\ror2_sandbox_cmd.txt (see FileCommands).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

public class TestSandbox6Gui : MonoBehaviour
{
    void OnGUI() { TestSandbox6.OnGui(); }
}

// Fishing<N>.InputBlocked() reads "open" of the highest ReqPanel<1-20>; Inventory<N> ORs ReqPanel1-18 into its ReqPanel19/20 shim.
public static class ReqPanel17
{
    public static bool open;
}

// MerchantUI.TextFocused() treats a selected object with an "...InputField" component as typing: the sandbox's focus object has one.
public class SandboxNoteInputField : MonoBehaviour { }

public static class TestSandbox6
{
    public static bool enabled = true, running;
    public static bool active;                       // sandbox open: JV94 there, lake/shops moved, state snapshotted (WaterLevel<N> reads it)
    public static int version = 6;
    public static string inputOwner;                 // null | "panel" | "note" - the sandbox owns the keyboard
    public static bool noteOpen;
    public static bool guestProtocol = false;        // send the [FT] SANDBOX lines (see STOPPING-POINT "guest protocol"); off until a client exists
    public static int startWallet = 10000;
    public static string QueuePath = FishingPaths.Tools("test_queue.json");
    public static string ResultsPath = FishingPaths.Tools("test_results.jsonl");
    public static string SnapPath = FishingPaths.Data("sandbox_snapshot.txt");
    public static string ShotDir = FishingPaths.Debug("sandbox_shots");
    public static string noteSource;                 // test hook: notes get "source": noteSource (agent dry runs)
    public static bool panelFocus;
    public static SandboxLook.Info info;

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_sandbox.log");
    static readonly string CmdPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_sandbox_cmd.txt");
    static readonly System.Random rng = new System.Random();
    static int errors;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "TestSandbox6: " + s + Environment.NewLine); } catch { } }
    static void Say(string text) { Log("CHAT " + Regex.Replace(text, "<[^>]*>", "")); try { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); } catch (Exception e) { Log("say error: " + e.Message); } }
    static void Info(string text) { Say("<color=#7fd8ff>" + text + "</color>"); }
    static string Money(int v) { return "$" + v.ToString("#,0", Ci); }

    // ================================================================== reflection helpers
    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
        catch { return new Type[0]; }
    }
    static readonly Dictionary<string, Type> newestCache = new Dictionary<string, Type>();
    static readonly Dictionary<string, float> newestAt = new Dictionary<string, float>();
    // newest enabled <prefix><N> type that has the field needField
    static Type Newest(string prefix, string needField)
    {
        string key = prefix + "|" + needField; Type c; float at;
        if (newestCache.TryGetValue(key, out c) && newestAt.TryGetValue(key, out at) && Time.unscaledTime - at < 10f)
        {
            if (c == null) return null;
            FieldInfo en0 = c.GetField("enabled", All);
            if (en0 == null || en0.FieldType != typeof(bool) || (bool)en0.GetValue(null)) return c;
        }
        Type best = null; int bn = -1;
        foreach (Type t in TypesNamed(prefix))
        {
            int v;
            if (!int.TryParse(t.Name.Substring(prefix.Length), out v) || v <= bn) continue;
            if (needField != null && t.GetField(needField, All) == null) continue;
            FieldInfo en = t.GetField("enabled", All);
            if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
            best = t; bn = v;
        }
        newestCache[key] = best; newestAt[key] = Time.unscaledTime;
        return best;
    }
    static object SG(Type t, string f)
    {
        if (t == null) return null;
        FieldInfo fi = t.GetField(f, All); if (fi != null) return fi.GetValue(null);
        PropertyInfo p = t.GetProperty(f, All); return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(null, null) : null;
    }
    static void SS(Type t, string f, object v) { if (t == null) return; FieldInfo fi = t.GetField(f, All); if (fi != null) fi.SetValue(null, v); }
    static object SCall(Type t, string m, params object[] args)
    {
        if (t == null) return null;
        foreach (MethodInfo mi in t.GetMethods(All))
            if (mi.Name == m && mi.IsStatic && mi.GetParameters().Length == args.Length)
            {
                try { return mi.Invoke(null, args); }
                catch (Exception e) { Log(t.Name + "." + m + " error: " + (e.InnerException ?? e).Message); return null; }
            }
        Log("method " + m + " not found on " + t.Name);
        return null;
    }
    static object IG(object o, string f) { if (o == null) return null; FieldInfo fi = o.GetType().GetField(f, All); return fi == null ? null : fi.GetValue(o); }
    static void IS(object o, string f, object v) { if (o == null) return; FieldInfo fi = o.GetType().GetField(f, All); if (fi != null) fi.SetValue(o, v); }
    static bool Dead(object o) { return o == null || ((o is UnityEngine.Object) && (UnityEngine.Object)o == null); }

    static Type Fish() { return Newest("Fishing", "waterY"); }
    static object FG(string f) { return SG(Fish(), f); }
    static void FS(string f, object v) { SS(Fish(), f, v); }
    static object FCall(string m, params object[] a) { return SCall(Fish(), m, a); }
    static float FNow() { object n = FG("now"); return n is float ? (float)n : 0f; }

    static bool LakeY(out float y)
    {
        y = 0f;
        object w = FG("water"), hy = FG("waterY"), hs = FG("hasWater"), sc = FG("waterScene");
        if (Dead(w) || !(hy is float) || (hs is bool && !(bool)hs) || Convert.ToString(sc) != UnityEngine.SceneManagement.SceneManager.GetActiveScene().name) return false;
        y = (float)hy; return true;
    }

    // ================================================================== install
    public static void Run()
    {
        Type mine = MethodBase.GetCurrentMethod().DeclaringType;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t == mine || !t.Name.StartsWith("TestSandbox") || !int.TryParse(t.Name.Substring(11), out n)) continue;
                FieldInfo f = t.GetField("enabled", All);
                if (f != null) f.SetValue(null, false);
                MethodInfo c = t.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static);
                if (c != null) RoR2Application.onNextUpdate += () => { try { c.Invoke(null, null); } catch { } };
                Log("disabled older " + t.Name);
            }
        Chat.onChatChanged += OnChatChanged;
        RoR2Application.onUpdate += Frame;
        running = true;
        recoverAt = Time.unscaledTime + 4f;
        Log("installed v" + version + " (!sandbox, !test, !test sync, !note/!pass/!fail, F6 panel, N note box)");
    }

    // a newer TestSandbox<N> took over: put everything back (main thread)
    public static void Cleanup()
    {
        try
        {
            ReleaseInput("cleanup", true);
            if (active) Close("replaced by a newer version", true, true);
            if (info != null) { SandboxLook.Destroy(info); info = null; }
            if (previewWater != null) { UnityEngine.Object.Destroy(previewWater); previewWater = null; }
            ZonesOn("cleanup");
            if (guiGo != null) UnityEngine.Object.Destroy(guiGo);
        }
        catch (Exception e) { Log("cleanup error: " + e); }
    }

    // ================================================================== chat
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine;
    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            var log = Chat.readOnlyLog;
            if (log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            string p = ChatPlain(line);
            if (active && WalletLeak(p)) boardDirty = true;   // a line carried the sandbox wallet: re-send the real money
            int i = p.IndexOf(": ");
            if (i <= 0) return;
            string text = p.Substring(i + 2).Trim().ToLowerInvariant();
            if (active && IsEndCommand(text) && p.Substring(0, i).Trim() == HostName())
            {   // TourneyEnd<N> queues the line and reads money[] on its own tick, after this callback: put the real money back right
                // now (synchronously, before any tick can run) so the sandbox wallet can never win or lose the tournament
                try { RealMoneyNow("end command typed"); endGuardAt = Time.unscaledTime; } catch (Exception e) { Log("endtournament guard error: " + e.Message); }
                closeForTourney = true;
            }
            if (text.StartsWith("!sandbox") || text.StartsWith("!test") || text.StartsWith("!note") || text.StartsWith("!pass") || text.StartsWith("!fail")
                || text.StartsWith("!check") || text.StartsWith("!island"))
                lock (chatQueue) chatQueue.Add(p);
        }
        catch { }
    }
    static string ChatPlain(string raw) { return Regex.Replace(raw ?? "", "<[^>]*>", "").Trim(); }
    static float endGuardAt = -100f;
    // "[FT] MONEY|who|n" / "[FT] BOARD|a:1|who:n|.." with n != the real money (our own corrections carry the real value: ignored)
    static bool WalletLeak(string p)
    {
        if (who == null || snap == null || !p.StartsWith("[FT] ")) return false;
        int real = snap.hadMoney ? snap.money : 0;
        string[] f = p.Split(new[] { '|' });
        if (f[0] == "[FT] MONEY") return f.Length >= 3 && f[1] == who && f[2] != real.ToString(Ci);
        if (f[0] == "[FT] BOARD") { for (int i = 1; i < f.Length; i++) { int c = f[i].LastIndexOf(':'); if (c > 0 && f[i].Substring(0, c) == who) return f[i].Substring(c + 1) != real.ToString(Ci); } }
        return false;
    }
    // TourneyEnd<N>.endCommand (default "!endtournament")
    static List<string> endCmds; static float endCmdsAt = -100f;
    static bool IsEndCommand(string low)
    {
        if (!low.StartsWith("!")) return false;
        if (endCmds == null || Time.unscaledTime - endCmdsAt > 30f)
        {
            endCmdsAt = Time.unscaledTime;
            var cmds = new List<string> { "!endtournament" };
            try
            {
                foreach (Type t in TypesNamed("TourneyEnd"))
                {
                    string c = SG(t, "endCommand") as string; if (!string.IsNullOrEmpty(c) && !cmds.Contains(c.ToLowerInvariant())) cmds.Add(c.ToLowerInvariant());
                }
            }
            catch { }
            endCmds = cmds;
        }
        foreach (string c in endCmds) if (low == c || low.StartsWith(c + " ")) return true;
        return false;
    }
    // every loaded <prefix><N> type (any version, enabled or not); cached 10 s
    // one scan of every loaded assembly builds an index "prefix" -> types named prefix + digits (re-scanned when the number of
    // loaded assemblies changes - an injection - or every 30 s)
    static Dictionary<string, List<Type>> typeIndex; static int typeIndexAsm = -1; static float typeIndexAt = -100f;
    static readonly List<Type> noTypes = new List<Type>();
    static List<Type> TypesNamed(string prefix)
    {
        var asms = FishingScope.Assemblies();
        if (typeIndex == null || asms.Length != typeIndexAsm || Time.unscaledTime - typeIndexAt > 30f)
        {
            var idx = new Dictionary<string, List<Type>>();
            foreach (var a in asms)
            {
                string an = a.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an == "mscorlib" || an.StartsWith("Mono") || an.StartsWith("Rewired") || an == "RoR2" || an.StartsWith("Wwise") || an.StartsWith("Zio") || an.StartsWith("Facepunch") || an.StartsWith("Microsoft")) continue;
                foreach (Type t in SafeTypes(a))
                {
                    string nm = t.Name; int e = nm.Length;
                    while (e > 0 && char.IsDigit(nm[e - 1])) e--;
                    if (e == nm.Length || e == 0) continue;
                    string pre = nm.Substring(0, e);
                    List<Type> l; if (!idx.TryGetValue(pre, out l)) { l = new List<Type>(); idx[pre] = l; }
                    l.Add(t);
                }
            }
            typeIndex = idx; typeIndexAsm = asms.Length; typeIndexAt = Time.unscaledTime;
        }
        List<Type> r; return typeIndex.TryGetValue(prefix, out r) ? r : noTypes;
    }
    static NetworkUser User(string name) { foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName == name) return nu; return null; }
    static string HostName()
    {
        try { var lu = LocalUserManager.GetFirstLocalUser(); if (lu != null && lu.currentNetworkUser != null) return lu.currentNetworkUser.userName; } catch { }
        return null;
    }
    static CharacterBody Body(NetworkUser nu) { return nu == null || nu.master == null ? null : nu.master.GetBody(); }
    static CharacterBody Body(string name) { return Body(User(name)); }

    static readonly Dictionary<string, float> cooldown = new Dictionary<string, float>();
    static void Handle(string p)
    {
        int i = p.IndexOf(": ");
        if (i <= 0) return;
        string name = p.Substring(0, i).Trim(), text = p.Substring(i + 2).Trim();
        if (User(name) == null) return;
        string low = text.ToLowerInvariant();
        string cmd = low.Split(new[] { ' ' })[0];
        string arg = text.Length > cmd.Length ? text.Substring(cmd.Length).Trim() : "";
        bool host = name == HostName();
        if (cmd == "!island")
        {
            if (active) Info("The TEST SANDBOX is open - the island is closed meanwhile. (" + (host ? "!sandbox leave first" : "ask the host") + ")");
            return;
        }
        if (cmd == "!note" || cmd == "!pass" || cmd == "!fail") { Note(name, cmd == "!pass" ? "pass" : cmd == "!fail" ? "fail" : null, arg); return; }
        if (!host) { if (cmd == "!sandbox" || cmd == "!test") Info("Only the host can use the test sandbox for now."); return; }
        float cd;
        if (cooldown.TryGetValue(name + cmd, out cd) && Time.unscaledTime < cd) return;
        cooldown[name + cmd] = Time.unscaledTime + 0.8f;
        Log(name + ": " + text);
        if (cmd == "!sandbox") SandboxCmd(name, arg.ToLowerInvariant());
        else if (cmd == "!test") TestCmd(name, arg);
        else if (cmd == "!check") { int n; if (int.TryParse(arg, out n)) Check(n); }
    }

    static void SandboxCmd(string name, string sub)
    {
        if (sub == "leave" || sub == "back" || sub == "close" || sub == "exit" || sub == "off")
        {
            if (!active) { Info("The test sandbox isn't open."); return; }
            Close("!sandbox leave", true, false);
            return;
        }
        if (sub == "help" || sub == "?")
        {
            Info("!sandbox = go to the test sandbox, !sandbox leave = back (everything restored). Inside: !test (queue), !test 5 / !test n-swim, !test next, !test reset, !test sync, N = write a note (Enter note, Shift+Enter pass, Ctrl+Enter fail), !note / !pass / !fail, !check 2, F6 = pick with number keys or the mouse, !sandbox goto gold|pool|dock|stall|merchant.");
            return;
        }
        if (sub == "reload" || sub == "sync") { SyncQueue(true); return; }
        if (sub.StartsWith("goto")) { Info(GotoSpot(name, sub.Substring(4).Trim())); return; }
        if (active) { Info("You're already in the test sandbox. !sandbox leave to go back."); return; }
        Open(name, sub == "force");
    }

    // ================================================================== main loop (main thread)
    static Stage lastStage;
    static float nextSlow, nextCmd, nextSync, travelAt = -1f, zonesOnAt = -1f, finishAt = -1f, hotAt = -1f, recoverAt = -1f;
    static bool closeForTourney;
    static GameObject guiGo;
    static void Frame()
    {
        if (!enabled) return;
        try
        {
            if (guiGo == null) { guiGo = new GameObject("TestSandbox6Gui"); UnityEngine.Object.DontDestroyOnLoad(guiGo); guiGo.AddComponent<TestSandbox6Gui>(); }
            if (Stage.instance != lastStage)
            {
                lastStage = Stage.instance;
                if (active) StageChanged("stage change");
            }
            if (active && RoR2.Run.instance == null) StageChanged("run ended");
            string[] lines = null;
            lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
            if (lines != null) foreach (string l in lines) { try { Handle(l); } catch (Exception e) { Log("handle error (" + l + "): " + e); } }
            if (Time.unscaledTime >= nextCmd) { nextCmd = Time.unscaledTime + 1f; FileCommands(); }
            if (Time.unscaledTime >= nextSync) { nextSync = Time.unscaledTime + 5f; AutoSync(); }
            Keys();
            InputTick();
            if (info != null) SandboxLook.Animate(info, Time.time);
            if (recoverAt > 0f && Time.unscaledTime >= recoverAt) { recoverAt = -1f; CrashRecovery(); }
            if (!NetworkServer.active) return;

            if (closeForTourney) { closeForTourney = false; if (active) { Info("The tournament is ending - leaving the test sandbox first (your real money was put back before the result)."); Close("tournament end typed", true, false); } }
            if (active) { PinnedTick(); BoardTick(); }
            if (travelAt > 0f && Time.time >= travelAt) { travelAt = -1f; Travel(); }
            if (zonesOnAt > 0f && Time.time >= zonesOnAt) { zonesOnAt = -1f; ZonesOn("arrived"); }
            if (hotAt > 0f && Time.time >= hotAt) { hotAt = -1f; if (active) { HotNear(); HotGold(); } }
            if (finishAt > 0f && Time.time >= finishAt) { finishAt = -1f; FinishClose(); }
            if (!active) return;
            WhipDummies();
            if (Time.time < nextSlow) return;
            nextSlow = Time.time + 0.5f;
            WaterToSandbox();
            if (zonesOff.Count == 0) ZonesOff("sandbox open");
            PetLeashOff();
            PauseTourney();
            HoldVotes();
            PinHotspots();
        }
        catch (Exception e) { if (errors++ < 40) Log("frame error: " + e); }
    }

    // ================================================================== lake (slide it under the sandbox and back) - as Island<N>
    static readonly Dictionary<GameObject, Vector2> waterHome = new Dictionary<GameObject, Vector2>();
    static readonly string[] WaterNames = { "FishingWater", "FishingClientWater" };
    static void WaterToSandbox()
    {
        if (info == null) return;
        foreach (string n in WaterNames)
        {
            GameObject w = GameObject.Find(n);
            if (w == null) continue;
            Vector3 p = w.transform.position;
            if (Mathf.Abs(p.x - info.origin.x) < 0.01f && Mathf.Abs(p.z - info.origin.z) < 0.01f) { SandboxLook.Ocean(info, w); continue; }
            if (!waterHome.ContainsKey(w)) waterHome[w] = new Vector2(p.x, p.z);
            w.transform.position = new Vector3(info.origin.x, p.y, info.origin.z);
            Log("lake '" + n + "' moved under the sandbox (was at " + p.x.ToString("0.0") + ", " + p.z.ToString("0.0") + ")");
            SandboxLook.Ocean(info, w);
        }
    }
    static void WaterHome()
    {
        foreach (var kv in waterHome)
        {
            if (kv.Key == null) continue;
            Vector3 p = kv.Key.transform.position;
            kv.Key.transform.position = new Vector3(kv.Value.x, p.y, kv.Value.y);
            Log("lake '" + kv.Key.name + "' moved back to " + kv.Value.x.ToString("0.0") + ", " + kv.Value.y.ToString("0.0"));
        }
        waterHome.Clear();
    }

    // ================================================================== out-of-bounds zones (off around the trip) - as Island<N>
    static readonly List<Collider> zonesOff = new List<Collider>();
    static void ZonesOff(string why)
    {
        try
        {
            int n = 0;
            foreach (MapZone z in UnityEngine.Object.FindObjectsOfType<MapZone>())
                foreach (Collider col in z.GetComponents<Collider>()) if (col != null && col.enabled) { col.enabled = false; zonesOff.Add(col); n++; }
            if (n > 0) Log("map zones off (" + n + " colliders): " + why);
        }
        catch (Exception e) { Log("zones off error: " + e.Message); }
    }
    static void ZonesOn(string why)
    {
        int n = 0;
        foreach (Collider c in zonesOff) if (c != null && !c.enabled) { c.enabled = true; n++; }
        zonesOff.Clear();
        if (n > 0) Log("map zones back on (" + n + "): " + why);
    }

    // ================================================================== open / travel / close
    static string who;                        // the player in the sandbox (host)
    static Vector3 returnPos; static bool haveReturn;
    static float openedAt;

    static bool IslandOpen()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith("Island") || !int.TryParse(t.Name.Substring(6), out n)) continue;
                FieldInfo ac = t.GetField("active", All), en = t.GetField("enabled", All);
                if (ac != null && en != null && (bool)en.GetValue(null) && (bool)ac.GetValue(null)) return true;
            }
        return false;
    }
    static readonly List<Type> islandsOff = new List<Type>();
    static void IslandsOff()
    {
        islandsOff.Clear();
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith("Island") || !int.TryParse(t.Name.Substring(6), out n)) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en == null || !(bool)en.GetValue(null)) continue;
                en.SetValue(null, false); islandsOff.Add(t);
                Log(t.Name + " switched off while the sandbox is open");
            }
    }
    static void IslandsOn()
    {
        foreach (Type t in islandsOff) { try { t.GetField("enabled", All).SetValue(null, true); Log(t.Name + " switched back on"); } catch (Exception e) { Log("island on error: " + e.Message); } }
        islandsOff.Clear();
    }

    static bool Open(string by, bool force)
    {
        if (!NetworkServer.active || RoR2.Run.instance == null) { Info("The test sandbox needs a running game (host)."); return false; }
        if (IslandOpen()) { Info("The Tropical Island is open - !island leave first, then !sandbox."); return false; }
        if (finishAt > 0f) { Info("The test sandbox is still closing - try again in a second."); return false; }
        if (File.Exists(SnapPath))
        {   // a snapshot from a crash that wasn't put back yet: never overwrite it with the (possibly wrong) current state
            recoverTries = 0; CrashRecovery();
            if (File.Exists(SnapPath)) { Info("Test sandbox: an old snapshot (" + SnapPath + ") couldn't be restored yet - not opening. See %TEMP%\\ror2_sandbox.log."); return false; }
        }
        float seaY;
        if (!LakeY(out seaY)) { Info("The lake isn't ready yet on this stage - try again in a few seconds."); return false; }
        CharacterBody b = Body(by);
        if (b == null || !b.healthComponent.alive) { Info(by + ": you need to be alive to go to the sandbox."); return false; }
        int others = 0;
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName != by) others++;
        if (others > 0 && !force)
        {
            Info("Test sandbox: " + others + " other player(s) online. While it's open the lake, hotspots and shops leave the map for everyone. Type !sandbox force to go anyway.");
            return false;
        }
        if (info == null || info.root == null || Mathf.Abs(info.origin.y - seaY) > 0.05f)
        {
            if (info != null) SandboxLook.Destroy(info);
            Vector3? o = FindOrigin(seaY);
            if (o == null) { Info("No room for the test sandbox on this stage, sorry!"); return false; }
            info = SandboxLook.Build(o.Value, WorldLayer());
            lastOrigin = info.origin;
            Log("sandbox built at " + info.origin.ToString("0.0") + " (" + info.boxes + " colliders, " + info.verts + " verts, " + info.buildMs.ToString("0") + " ms, shader " + SandboxLook.ShaderName + ")");
        }
        lastOrigin = info.origin;
        if (previewWater != null) { UnityEngine.Object.Destroy(previewWater); previewWater = null; }
        // ---- snapshot BEFORE anything changes
        who = by;
        snap = Capture(by);
        if (snap == null) { Info("Test sandbox: couldn't read your current state - not opening (nothing changed)."); return false; }
        globals = CaptureGlobals();
        fileHashes = HashFiles();
        WriteSnapFile(snap);
        lastWalletSent = int.MinValue; boardDirty = true;
        active = true; openedAt = Time.time; curTest = -1; checks.Clear();
        IslandsOff();
        WaterToSandbox();
        ShopsToSandbox();
        Baseline("open");
        // ---- the trip
        returnPos = b.footPosition; haveReturn = true;
        ZonesOff(by + " travelling (kept off while the sandbox is open: a pet teleported to its owner would otherwise be thrown back onto the map)"); travelAt = Time.time + 0.25f;
        hotAt = Time.time + 1.6f;
        LoadQueue(false); LoadResults();
        Info("TEST SANDBOX open for " + by + ": wallet " + Money(startWallet) + " (temporary), daylight, pool + dock, rod shop + fish merchant. !test for the queue, F6 to pick, !sandbox leave to go back.");
        Log("sandbox OPEN for " + by + " at " + info.origin.ToString("0.0") + " (sea Y " + seaY.ToString("0.00") + "), return " + returnPos.ToString("0.00"));
        GuestLine("OPEN|" + F2(info.origin.x) + "|" + F2(info.origin.y) + "|" + F2(info.origin.z) + "|" + by);
        return true;
    }

    static void Travel()
    {
        if (!active || info == null) return;
        CharacterBody b = Body(who);
        if (b == null || !b.healthComponent.alive) { Log("travel: " + who + " has no living body"); return; }
        if (!SandboxLook.Inside(info, b.footPosition)) { returnPos = b.footPosition; haveReturn = true; }
        TeleportHelper.TeleportBody(b, info.spawn);
        Log(who + " -> sandbox " + info.spawn.ToString("0.0") + " (return " + returnPos.ToString("0.00") + ")");
    }

    // close: state restore + lake/shops back + teleport now; geometry/hotspots/school 2 s later (players are back by then)
    static void Close(string why, bool teleport, bool immediate)
    {
        if (!active) return;
        Log("closing (" + why + ")");
        ReleaseInput("closing", true);
        int bad = RestoreAll(why, true);
        lastRestoreBad = bad;
        active = false; travelAt = -1f; hotAt = -1f; curTest = -1; panelFocus = false;
        BoardFix(true);   // guests' board: the real money again
        GuestLine("CLOSE|" + who);
        WaterHome();
        ShopsHome();
        if (teleport)
        {
            CharacterBody b = Body(who);
            if (b != null && b.healthComponent.alive && info != null && SandboxLook.Inside(info, b.footPosition))
            {
                Vector3 back = haveReturn ? returnPos : (SpawnPoint.readOnlyInstancesList.Count > 0 ? SpawnPoint.readOnlyInstancesList[0].transform.position : b.footPosition);
                TeleportHelper.TeleportBody(b, back);
                Log(who + " back to " + back.ToString("0.00") + (haveReturn ? " (exact spot)" : " (spawn point)"));
            }
        }
        ZonesOn("sandbox closed");
        PetLeashOn();
        haveReturn = false;
        Info("Test sandbox closed - you're back, everything is restored (money, items, fish, time, lake, shops).");
        if (immediate) FinishClose(); else finishAt = Time.time + 2f;
    }
    static void FinishClose()
    {
        try { HotspotsHome(); } catch (Exception e) { Log("hotspots home error: " + e.Message); }
        try { PruneDrySpots(); } catch (Exception e) { Log("prune error: " + e.Message); }
        try { FCall("Regenerate", "sandbox closed"); Log("school regenerated near the players"); } catch (Exception e) { Log("school regen error: " + e.Message); }
        if (info != null) { SandboxLook.Destroy(info); info = null; Log("sandbox geometry removed"); }
        IslandsOn();
        Verify("closed");
        if (lastRestoreBad == 0) DeleteSnapFile("closed"); else Log("snapshot file KEPT (" + lastRestoreBad + " restore step(s) failed) - the next TestSandbox<N> retries it");
    }
    static int lastRestoreBad;

    // stage change or run end while open: no teleport (the stage is gone), the stall/merchant re-place themselves on the new scene
    static void StageChanged(string why)
    {
        Log(why + " while the sandbox was open - restoring and closing");
        ReleaseInput(why, true);
        int bad = RestoreAll(why, true);
        lastRestoreBad = bad;
        active = false; travelAt = -1f; hotAt = -1f; finishAt = -1f; curTest = -1; panelFocus = false;
        try { Type st = Newest("RodStand", "testPlaceAt"); if (st != null && SG(st, "testPlaceAt") != null) { SS(st, "testPlaceAt", null); Log("pending stall move to the sandbox cancelled"); } } catch { }
        PetLeashOn();
        waterHome.Clear(); zonesOff.Clear(); shopsSaved = false; haveReturn = false; leashOff.Clear();
        if (info != null) { SandboxLook.Destroy(info); info = null; }
        IslandsOn();
        BoardFix(true);
        GuestLine("CLOSE|" + who);
        Verify(why);
        if (bad == 0) DeleteSnapFile(why); else Log("snapshot file KEPT (" + bad + " restore step(s) failed) - crash recovery retries it");
        Info("The test sandbox closed (" + why + "). Your real money, items and fish are back" + (bad == 0 ? "." : " - " + bad + " step(s) failed, see %TEMP%\\ror2_sandbox.log."));
    }

    static int WorldLayer() { int l = LayerIndex.world.intVal; return l >= 0 ? l : 11; }

    // far outside the stage at sea level (same rule as Island<N>: > 4.3 km from anything the stage draws)
    static Vector3? FindOrigin(float seaY)
    {
        Bounds wb = new Bounds(); bool any = false; int n = 0;
        Renderer[] rends = UnityEngine.Object.FindObjectsOfType<Renderer>();
        foreach (Renderer r in rends)
        {
            if (r == null || !r.enabled) continue;
            if (info != null && info.root != null && r.transform.IsChildOf(info.root.transform)) continue;
            string rn = r.gameObject.name;
            if (rn == "FishingWater" || rn == "FishingClientWater" || rn == "IslandPreviewWater" || rn == "SandboxPreviewWater") continue;
            Bounds rb = r.bounds;
            if (rb.size.magnitude > 6000f) continue;
            if (!any) { wb = rb; any = true; } else wb.Encapsulate(rb);
            n++;
        }
        int world = LayerIndex.world.intVal;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger || !c.enabled || c.gameObject.layer != world) continue;
            Bounds cb = c.bounds;
            if (cb.size.magnitude > 6000f) continue;
            if (!any) { wb = cb; any = true; } else wb.Encapsulate(cb);
        }
        if (!any) wb = new Bounds(Vector3.zero, Vector3.one * 1000f);
        float ext = new Vector2(wb.extents.x, wb.extents.z).magnitude;
        float half = SandboxLook.WallHalf + 40f;
        foreach (float extra in new[] { 4300f, 5500f, 7000f })
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f + 0.3f + Mathf.PI / 8f, D = ext + half * 1.42f + extra;   // offset from Island<N>'s angles
                Vector3 o = new Vector3(Mathf.Round(wb.center.x + Mathf.Cos(a) * D), seaY, Mathf.Round(wb.center.z + Mathf.Sin(a) * D));
                if (Physics.CheckBox(new Vector3(o.x, seaY + 40f, o.z), new Vector3(half, 100f, half), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)) continue;
                var box = new Bounds(new Vector3(o.x, seaY + 40f, o.z), new Vector3(half * 2f, 200f, half * 2f));
                bool clash = false;
                foreach (Renderer r in rends) { if (r != null && r.enabled && r.bounds.size.magnitude <= 6000f && r.bounds.Intersects(box)) { clash = true; break; } }
                if (clash) continue;
                Log("sandbox spot " + o.ToString("0") + " (" + D.ToString("0") + " m from the stage centre " + wb.center.ToString("0") + ", stage bounds " + wb.size.ToString("0") + " from " + n + " renderers)");
                return o;
            }
        Log("no clear sandbox spot found around " + wb.center.ToString("0"));
        return null;
    }

    // ================================================================== rod shop + fish merchant (as Island<N>)
    static bool shopsSaved, standWas, merchWas;
    static Vector3 standPos0, merchPos0; static float standYaw0, merchYaw0;
    static string F2(float v) { return v.ToString("0.00", Ci); }
    static void ShopsToSandbox()
    {
        try
        {
            Type st = Newest("RodStand", "testPlaceAt"), me = Newest("FishMerchant", "merchantPos");
            if (!shopsSaved)
            {
                shopsSaved = true;
                if (st != null) { standWas = (bool)SG(st, "placed"); standPos0 = (Vector3)SG(st, "standPos"); standYaw0 = (float)SG(st, "standYaw"); }
                if (me != null) { merchWas = (bool)SG(me, "placed"); merchPos0 = (Vector3)SG(me, "merchantPos"); merchYaw0 = (float)SG(me, "merchantYaw"); }
            }
            if (st != null) { SS(st, "testPlaceAt", F2(info.stall.x) + "|" + F2(info.stall.y) + "|" + F2(info.stall.z) + "|" + info.stallYaw.ToString("0.0", Ci)); Log("rod shop -> " + info.stall.ToString("0.0") + " (" + st.Name + ")"); }
            if (me != null) MoveMerchant(me, info.merchant, info.merchantYaw, true);
        }
        catch (Exception e) { Log("shops error: " + e); }
    }
    static void MoveMerchant(Type me, Vector3 pos, float yaw, bool placed)
    {
        SS(me, "merchantPos", pos); SS(me, "merchantYaw", yaw);
        Type look = me.Assembly.GetType("MerchantLook");
        if (look != null) { MethodInfo d = look.GetMethod("Destroy", All); if (d != null) d.Invoke(null, null); }
        SS(me, "placed", placed);
        if (me.GetField("nextAnnounce", All) != null) SS(me, "nextAnnounce", 0f);
        Log("fish merchant -> " + pos.ToString("0.0") + " placed=" + placed + " (" + me.Name + ")");
    }
    static void ShopsHome()
    {
        if (!shopsSaved) return;
        shopsSaved = false;
        try
        {
            Type st = Newest("RodStand", "testPlaceAt"), me = Newest("FishMerchant", "merchantPos");
            if (st != null)
            {
                if (standWas) SS(st, "testPlaceAt", F2(standPos0.x) + "|" + F2(standPos0.y) + "|" + F2(standPos0.z) + "|" + standYaw0.ToString("0.0", Ci));
                else SS(st, "placed", false);
                Log("rod shop back to " + (standWas ? standPos0.ToString("0.0") : "(auto placement)"));
            }
            if (me != null) MoveMerchant(me, merchPos0, merchYaw0, merchWas);
        }
        catch (Exception e) { Log("shops home error: " + e); }
    }

    // ================================================================== hotspots
    static bool pinNear, pinGold;
    static void HotNear()
    {
        float y; if (!LakeY(out y) || info == null) return;
        FS("hotspotPos", new Vector3(info.poolCentre.x, y, info.poolCentre.z)); FS("hasHotspot", true); FS("hotFarSince", -1f);
        FCall("BroadcastHot");
        Log("purple hotspot -> pool " + info.poolCentre.ToString("0.0"));
    }
    static void HotGold()
    {
        float y; if (!LakeY(out y) || info == null) return;
        Vector3 g = info.dockEnd + new Vector3(0f, 0f, 22f);
        FS("hot2Pos", new Vector3(g.x, y, g.z)); FS("hasHot2", true);
        FS("hot2MovedAt", FNow());
        FS("hot2Scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        FCall("BroadcastHot2");
        Log("golden hotspot -> off the dock " + g.ToString("0.0"));
    }
    static void PinHotspots()
    {
        if (info == null) return;
        if (pinNear) { object hp = FG("hotspotPos"); if (hp is Vector3) { Vector3 d = (Vector3)hp - info.poolCentre; d.y = 0; if (d.magnitude > 2f) HotNear(); } }
        if (pinGold) { object hp = FG("hot2Pos"); Vector3 g = info.dockEnd + new Vector3(0f, 0f, 22f); if (hp is Vector3) { Vector3 d = (Vector3)hp - g; d.y = 0; if (d.magnitude > 2f) HotGold(); } }
    }
    static bool InSandboxArea(Vector3 p)
    {
        if (lastOrigin == null) return false;
        Vector3 o = lastOrigin.Value;
        return Mathf.Abs(p.x - o.x) < SandboxLook.WallHalf + 400f && Mathf.Abs(p.z - o.z) < SandboxLook.WallHalf + 400f;
    }
    static Vector3? lastOrigin;
    static void HotspotsHome()
    {
        object hp = FG("hotspotPos");
        if (hp is Vector3 && InSandboxArea((Vector3)hp)) { FCall("PlaceHotspot", true); Log("purple hotspot re-placed on the map -> " + FG("hotspotPos")); }
        object h2 = FG("hot2Pos");
        if (h2 is Vector3 && InSandboxArea((Vector3)h2))
        {
            FCall("PlaceHot2", true, "sandbox closed");
            object n2 = FG("hot2Pos");
            if (n2 is Vector3 && InSandboxArea((Vector3)n2)) { FS("hasHot2", false); Log("golden hotspot: no spot right now - Fishing places it again by itself"); }
            else Log("golden hotspot re-placed on the map -> " + n2);
        }
    }
    static void PruneDrySpots()
    {
        if (lastOrigin == null) return;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("DrownRespawn")) continue;
                var d = SG(t, "lastDry") as Dictionary<string, Vector3>;
                if (d == null) continue;
                foreach (string k in new List<string>(d.Keys)) if (InSandboxArea(d[k])) { d.Remove(k); Log(t.Name + ": dropped dry spot of " + k + " in the sandbox"); }
            }
    }

    // ================================================================== tournament / votes held while inside
    static void PauseTourney()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            if (!a.GetName().Name.StartsWith("Tourney") && !FishingScope.IsMod(a)) continue;
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("Tourney")) continue;
                FieldInfo en = t.GetField("enabled", All), rd = t.GetField("rotationsDone", All), sa = t.GetField("startedAt", All), stf = t.GetField("started", All), fi = t.GetField("finished", All);
                if (en != null && rd != null && sa != null && stf != null)
                {
                    if (!(bool)en.GetValue(null) || !(bool)stf.GetValue(null) || (fi != null && (bool)fi.GetValue(null))) continue;
                    double el = (DateTime.UtcNow - (DateTime)sa.GetValue(null)).TotalSeconds;
                    FieldInfo rs = t.GetField("RotateSeconds", All);
                    double every = rs != null ? Convert.ToDouble(rs.GetValue(null)) : 120.0;
                    int need = (int)(el / every), done = (int)rd.GetValue(null);
                    if (done < need) { rd.SetValue(null, need); Log(t.Name + ": map rotation skipped (sandbox open)"); }
                }
                // TourneyEnd<N>: never let the timer end the tournament while the sandbox wallet is in money[]
                FieldInfo ae = t.GetField("autoEndMinutes", All), ended = t.GetField("ended", All), nw = t.GetField("now", All), seen = t.GetField("seenAt", All);
                if (ae != null && ended != null && nw != null && seen != null && !(bool)ended.GetValue(null))
                {
                    float m = (float)ae.GetValue(null), now = (float)nw.GetValue(null), s = (float)seen.GetValue(null);
                    if (m > 0f && s >= 0f && m * 60f - (now - s) < 20f) { ae.SetValue(null, m + 1f); Log(t.Name + ": tournament auto-end postponed by 1 min (sandbox open)"); }
                }
            }
        }
    }
    // the game's minion leash teleports a pet to the nearest map node (km away) when its owner is off the node graph;
    // DronePets then teleports it back every 5 s. Switch the leash off on the sandbox player's pet while inside.
    static readonly List<Behaviour> leashOff = new List<Behaviour>();
    static void PetLeashOff()
    {
        try
        {
            Type dp = Newest("DronePets", "pets"); if (dp == null || who == null) return;
            var pets = SG(dp, "pets") as IDictionary; if (pets == null || !pets.Contains(who)) return;
            CharacterMaster m = IG(pets[who], "master") as CharacterMaster; if (m == null) return;
            CharacterBody pb = m.GetBody(); if (pb == null) return;
            foreach (Behaviour b in pb.GetComponents<Behaviour>())
                if (b != null && b.enabled && b.GetType().Name.Contains("Leash")) { b.enabled = false; leashOff.Add(b); Log("pet leash " + b.GetType().Name + " off on " + pb.name); }
        }
        catch (Exception e) { if (errors++ < 40) Log("leash error: " + e.Message); }
    }
    static void PetLeashOn()
    {
        int n = 0; foreach (Behaviour b in leashOff) if (b != null && !b.enabled) { b.enabled = true; n++; }
        leashOff.Clear(); if (n > 0) Log("pet leash back on (" + n + ")");
    }
    static void HoldVotes()
    {
        Type v = Newest("FeatureVote", "autoPolls");
        if (v != null && (bool)SG(v, "autoPolls")) { SS(v, "autoPolls", false); Log(v.Name + ": automatic polls held while the sandbox is open"); }
    }

    // ================================================================== snapshot / restore of the player's real state
    class Snap
    {
        public string name;
        public bool hadMoney; public int money;
        public bool hadOwner; public List<string> owned = new List<string>(); public string rod, skin, bobber;
        public bool hadGlow; public string[] glow;
        public List<string> gadgets = new List<string>();
        public bool hadCase; public Dictionary<string, float[]> caseItems = new Dictionary<string, float[]>();
        public bool hadCaseEq; public string[] caseEq;
        public Dictionary<string, int[]> stat = new Dictionary<string, int[]>();
        public bool hadSlots; public Dictionary<string, string> slots = new Dictionary<string, string>();
        public bool hadLures; public Dictionary<string, int> lures = new Dictionary<string, int>(); public string lureEq;
        public Dictionary<string, int> items = new Dictionary<string, int>();
        public bool hadColl; public Dictionary<string, int> coll = new Dictionary<string, int>();
        public bool hadDouble; public int dbl;
        public bool hadBuff; public Dictionary<string, float> buffs = new Dictionary<string, float>();
        public bool hadFeathers; public int feathers;
        public bool hadPet; public string petType; public bool petOn;
        public bool noInv;
    }

    // ---------------------------------------------------------------- proof that a restore put everything back
    static Dictionary<string, string> fileHashes;
    static readonly string[] OwnershipFiles = { FishingPaths.Data("rod_owners.json"), FishingPaths.Data("glow_owners.txt"),
        FishingPaths.Data("gadget_owners.txt"), FishingPaths.Data("case_items.json"), FishingPaths.Data("inventory_slots.json") };
    static Dictionary<string, string> HashFiles()
    {
        var d = new Dictionary<string, string>();
        foreach (string f in OwnershipFiles)
        {
            try
            {
                if (!File.Exists(f)) { d[f] = "(missing)"; continue; }
                byte[] b = File.ReadAllBytes(f);
                using (var sha = System.Security.Cryptography.SHA1.Create()) d[f] = BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").Substring(0, 12) + " " + b.Length + "b";
            }
            catch (Exception e) { d[f] = "(error " + e.Message + ")"; }
        }
        return d;
    }
    static string Seq<T>(IEnumerable<T> l) { var p = new List<string>(); if (l != null) foreach (T x in l) p.Add(Convert.ToString(x, Ci)); return string.Join(",", p.ToArray()); }
    static string DictS<V>(Dictionary<string, V> d)
    {
        var keys = new List<string>(d.Keys); keys.Sort(StringComparer.Ordinal);
        var p = new List<string>();
        foreach (string k in keys) { object v = d[k]; p.Add(k + "=" + (v is Array ? Seq(ToObjs((Array)v)) :Convert.ToString(v, Ci))); }
        return "{" + string.Join(";", p.ToArray()) + "}";
    }
    static IEnumerable<object> ToObjs(Array a) { foreach (object o in a) yield return o; }
    // differences between the snapshot and the live state (persistentOnly: just the parts that live in files)
    static List<string> Diff(Snap a, Snap b, bool persistentOnly)
    {
        var d = new List<string>();
        Action<string, string, string> eq = (what, x, y) => { if (x != y) d.Add(what + ": was " + x + ", now " + y); };
        eq("owner entry", a.hadOwner.ToString(), b.hadOwner.ToString());
        eq("owned (order kept)", Seq(a.owned), Seq(b.owned));
        eq("rod/skin/bobber", a.rod + "/" + a.skin + "/" + a.bobber, b.rod + "/" + b.skin + "/" + b.bobber);
        eq("glow mirror", a.hadGlow ? Seq(a.glow) : "-", b.hadGlow ? Seq(b.glow) : "-");
        var ga = new List<string>(a.gadgets); ga.Sort(StringComparer.Ordinal); var gb = new List<string>(b.gadgets); gb.Sort(StringComparer.Ordinal);
        eq("gadgets", Seq(ga), Seq(gb));
        eq("case items", a.hadCase ? DictS(a.caseItems) : "-", b.hadCase ? DictS(b.caseItems) : "-");
        eq("case equip", a.hadCaseEq ? Seq(a.caseEq) : "-", b.hadCaseEq ? Seq(b.caseEq) : "-");
        eq("StatTrak", DictS(a.stat), DictS(b.stat));
        eq("inventory slots", a.hadSlots ? DictS(a.slots) : "-", b.hadSlots ? DictS(b.slots) : "-");
        if (persistentOnly) return d;
        eq("money", a.hadMoney ? a.money.ToString() : "-", b.hadMoney ? b.money.ToString() : "-");
        eq("lures", a.hadLures ? DictS(a.lures) : "-", b.hadLures ? DictS(b.lures) : "-");
        eq("lure equipped", a.lureEq ?? "-", b.lureEq ?? "-");
        if (!b.noInv) eq("items", DictS(a.items), DictS(b.items));
        eq("collection", a.hadColl ? DictS(a.coll) : "-", b.hadColl ? DictS(b.coll) : "-");
        eq("Golden Koi double", a.dbl.ToString(), b.dbl.ToString());
        eq("eat buffs", a.hadBuff ? DictS(a.buffs) : "-", b.hadBuff ? DictS(b.buffs) : "-");
        eq("temp feathers", a.feathers.ToString(), b.feathers.ToString());
        string pa = a.hadPet ? a.petType + (a.petOn ? "" : " off") : "-", pb = b.hadPet ? b.petType + (b.petOn ? "" : " off") : "-";
        if (pa != pb)
        {
            Type dp = Newest("DronePets", "pets");
            string gq = SG(dp, "grantQueue") as string, rq = SG(dp, "removeQueue") as string;
            bool pending = (gq != null && gq.StartsWith(a.name + "|")) || rq == a.name;
            d.Add("pet: was " + pa + ", now " + pb + (pending ? " (DronePets has the change queued - fine)" : ""));
        }
        return d;
    }
    static void Verify(string why)
    {
        try
        {
            if (snap != null)
            {
                Snap now = Capture(snap.name, false);
                if (now == null) Log("VERIFY (" + why + "): couldn't read the live state");
                else
                {
                    var diffs = Diff(snap, now, now.noInv);
                    int real = 0; foreach (string x in diffs) if (!x.Contains("queued - fine")) real++;
                    Log("VERIFY (" + why + "): state " + (diffs.Count == 0 ? "identical to the snapshot" + (now.noInv ? " (persistent parts; no inventory to read)" : "") : diffs.Count + " difference(s): " + string.Join(" | ", diffs.ToArray())));
                    if (real > 0) Info("Test sandbox restore check: " + real + " difference(s) - see %TEMP%\\ror2_sandbox.log");
                }
            }
            if (fileHashes != null)
            {
                var now = HashFiles(); int same = 0; var changed = new List<string>();
                foreach (var kv in fileHashes) { string h; now.TryGetValue(kv.Key, out h); if (h == kv.Value) same++; else changed.Add(System.IO.Path.GetFileName(kv.Key) + " " + kv.Value + " -> " + h); }
                Log("VERIFY (" + why + "): ownership files " + same + "/" + fileHashes.Count + " byte-identical" + (changed.Count > 0 ? "; CHANGED: " + string.Join(" | ", changed.ToArray()) + " (another player's purchase meanwhile also changes them)" : ""));
            }
        }
        catch (Exception e) { Log("verify error: " + e); }
    }
    class Globals { public bool haveHour; public float hour, dayMinutes, at; public string weather; public float weatherLeft; public bool haveVote, autoPolls; }
    static Snap snap; static Globals globals;

    static readonly string[] ExtraItems = { "BoostDamage", "Feather", "ExtraLifeConsumed" };
    static List<string> TrackedItems()
    {
        var l = new List<string>();
        var map = FG("fishCarrierItem") as Dictionary<string, string>;
        if (map != null) foreach (var v in map.Values) if (!string.IsNullOrEmpty(v) && !l.Contains(v)) l.Add(v);
        foreach (string s in ExtraItems) if (!l.Contains(s)) l.Add(s);
        return l;
    }
    static Inventory Inv(string name) { NetworkUser u = User(name); return u != null && u.master != null ? u.master.inventory : null; }
    static MethodInfo InvMethod(Inventory inv, string a, string b, bool two)
    {
        foreach (string n in new[] { a, b })
            foreach (MethodInfo m in typeof(Inventory).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != n) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == (two ? 2 : 1) && ps[0].ParameterType == typeof(ItemIndex) && (!two || ps[1].ParameterType == typeof(int))) return m;
            }
        return null;
    }
    static int ItemCount(Inventory inv, string item)
    {
        ItemIndex idx = ItemCatalog.FindItemIndex(item); if (idx == ItemIndex.None || inv == null) return 0;
        MethodInfo m = InvMethod(inv, "GetItemCountPermanent", "GetItemCount", false);
        return m == null ? 0 : Convert.ToInt32(m.Invoke(inv, new object[] { idx }));
    }
    static void ItemAdd(Inventory inv, string item, int delta)
    {
        ItemIndex idx = ItemCatalog.FindItemIndex(item); if (idx == ItemIndex.None || inv == null || delta == 0) return;
        MethodInfo m = delta > 0 ? InvMethod(inv, "GiveItemPermanent", "GiveItem", true) : InvMethod(inv, "RemoveItemPermanent", "RemoveItem", true);
        if (m != null) m.Invoke(inv, new object[] { idx, Math.Abs(delta) });
    }

    static Snap Capture(string name) { return Capture(name, true); }
    // needInv false: no inventory (menu / run over) is fine - items stay empty and s.noInv is set (persistent parts only)
    static Snap Capture(string name, bool needInv)
    {
        try
        {
            Type F = Fish(); if (F == null) { Log("capture: no Fishing<N>"); return null; }
            var s = new Snap { name = name };
            var money = SG(F, "money") as Dictionary<string, int>;
            if (money != null) s.hadMoney = money.TryGetValue(name, out s.money);
            var owners = SG(F, "rodOwners") as IDictionary;
            if (owners != null && owners.Contains(name))
            {
                object o = owners[name]; s.hadOwner = true;
                s.owned = new List<string>((List<string>)IG(o, "owned")); s.rod = (string)IG(o, "rod"); s.skin = (string)IG(o, "skin"); s.bobber = (string)IG(o, "bobber");
            }
            Type rs = Newest("RodStand", "mirror");
            var gm = SG(rs, "mirror") as Dictionary<string, string[]>;
            if (gm != null && gm.ContainsKey(name)) { s.hadGlow = true; s.glow = (string[])gm[name].Clone(); }
            Type hc = Newest("HotspotCompass", "mirror");
            var cm = SG(hc, "mirror") as HashSet<string>;
            if (cm != null) foreach (string e in cm) if (e.StartsWith(name + "|")) s.gadgets.Add(e.Substring(name.Length + 1));
            Type me = Newest("FishMerchant", "caseItems");
            var ci = SG(me, "caseItems") as Dictionary<string, Dictionary<string, float[]>>;
            Dictionary<string, float[]> cd;
            if (ci != null && ci.TryGetValue(name, out cd)) { s.hadCase = true; foreach (var kv in cd) s.caseItems[kv.Key] = (float[])kv.Value.Clone(); }
            var ce = SG(me, "caseEquip") as Dictionary<string, string[]>;
            if (ce != null && ce.ContainsKey(name)) { s.hadCaseEq = true; s.caseEq = (string[])ce[name].Clone(); }
            var sc = me == null ? null : SG(me.Assembly.GetType("CaseSkinLook"), "statCounts") as Dictionary<string, int[]>;
            if (sc != null) foreach (var kv in sc) if (kv.Key.StartsWith(name + "|")) s.stat[kv.Key] = (int[])kv.Value.Clone();
            Type iv = Newest("Inventory", "slots");
            var sl = SG(iv, "slots") as Dictionary<string, Dictionary<string, string>>;
            if (sl != null && sl.ContainsKey(name)) { s.hadSlots = true; s.slots = new Dictionary<string, string>(sl[name]); }
            var lu = SG(F, "lureUses") as Dictionary<string, Dictionary<string, int>>;
            if (lu != null && lu.ContainsKey(name)) { s.hadLures = true; s.lures = new Dictionary<string, int>(lu[name]); }
            var le = SG(F, "lureEquipped") as Dictionary<string, string>;
            if (le != null) le.TryGetValue(name, out s.lureEq);
            Inventory inv = Inv(name);
            if (inv == null && needInv) { Log("capture: " + name + " has no inventory"); return null; }
            if (inv == null) s.noInv = true;
            else foreach (string it in TrackedItems()) s.items[it] = ItemCount(inv, it);
            var coll = SG(F, "collections") as Dictionary<string, Dictionary<string, int>>;
            if (coll != null && coll.ContainsKey(name)) { s.hadColl = true; s.coll = new Dictionary<string, int>(coll[name]); }
            var dl = SG(F, "doubleLeft") as Dictionary<string, int>;
            if (dl != null) s.hadDouble = dl.TryGetValue(name, out s.dbl);
            var bu = SG(F, "buffUntil") as Dictionary<string, Dictionary<string, float>>;
            if (bu != null && bu.ContainsKey(name)) { s.hadBuff = true; s.buffs = new Dictionary<string, float>(bu[name]); }
            var tf = SG(F, "tempFeathers") as Dictionary<string, int>;
            if (tf != null) s.hadFeathers = tf.TryGetValue(name, out s.feathers);
            Type dp = Newest("DronePets", "pets");
            var pets = SG(dp, "pets") as IDictionary;
            if (pets != null && pets.Contains(name)) { object p = pets[name]; s.hadPet = true; s.petType = (string)IG(p, "type"); s.petOn = (bool)IG(p, "on"); }
            Log("SNAPSHOT " + name + ": money " + (s.hadMoney ? s.money.ToString() : "none") + ", rod " + (s.hadOwner ? s.rod + "/" + s.skin + "/" + s.bobber + " owns [" + string.Join(", ", s.owned.ToArray()) + "]" : "(no owner entry)")
                + ", glow " + (s.hadGlow ? s.glow[0] + "/" + s.glow[1] : "-") + ", gadgets [" + string.Join(",", s.gadgets.ToArray()) + "], case items " + s.caseItems.Count
                + ", slots " + (s.hadSlots ? s.slots.Count.ToString() : "-") + ", lures " + Dump(s.lures) + " eq " + (s.lureEq ?? "-") + ", items " + Dump(s.items)
                + ", coll " + Dump(s.coll) + ", double " + s.dbl + ", buffs " + s.buffs.Count + ", feathers " + s.feathers + ", pet " + (s.hadPet ? s.petType + (s.petOn ? "" : " (off)") : "-"));
            return s;
        }
        catch (Exception e) { Log("capture error: " + e); return null; }
    }
    static string Dump(Dictionary<string, int> d) { var l = new List<string>(); foreach (var kv in d) if (kv.Value != 0) l.Add(kv.Key + "=" + kv.Value); return "{" + string.Join(",", l.ToArray()) + "}"; }

    static Globals CaptureGlobals()
    {
        var g = new Globals();
        Type dn = Newest("DayNight", "hour");
        if (dn != null) { g.haveHour = true; g.hour = (float)SG(dn, "hour"); g.dayMinutes = (float)SG(dn, "dayMinutes"); g.at = Time.realtimeSinceStartup; }
        g.weather = FG("weather") as string ?? "Clear";
        object wu = FG("weatherUntil"); g.weatherLeft = wu is float ? Mathf.Max(0f, (float)wu - FNow()) : 0f;
        Type v = Newest("FeatureVote", "autoPolls");
        if (v != null) { g.haveVote = true; g.autoPolls = (bool)SG(v, "autoPolls"); }
        Log("globals: hour " + (g.haveHour ? g.hour.ToString("0.00") + " (" + g.dayMinutes + " min/day)" : "-") + ", weather " + g.weather + " (" + g.weatherLeft.ToString("0") + " s left), autoPolls " + (g.haveVote ? g.autoPolls.ToString() : "-"));
        return g;
    }

    // every step on its own: a failure is logged and the rest still runs
    static int Step(string what, Action a)
    {
        try { a(); Log("  restore " + what + ": ok"); return 0; }
        catch (Exception e) { Log("  restore " + what + ": ERROR " + (e.InnerException ?? e)); return 1; }
    }

    // put the player's real state back. full = also time/weather/votes (leave / stage change); test start keeps the sandbox globals
    static int RestoreAll(string why, bool full)
    {
        ClearPins();
        if (snap == null) { Log("restore (" + why + "): no snapshot!"); return 1; }
        Log("RESTORE " + snap.name + " (" + why + ")");
        int bad = RestorePlayer(snap);
        if (full && globals != null)
        {
            Globals g = globals;
            bad += Step("time", () =>
            {
                Type dn = Newest("DayNight", "hour"); if (dn == null || !g.haveHour) return;
                float el = Time.realtimeSinceStartup - g.at;
                float h = Mathf.Repeat(g.hour + el * 24f / (Mathf.Max(0.1f, g.dayMinutes) * 60f), 24f);
                SS(dn, "hour", h); SS(dn, "dayMinutes", g.dayMinutes); SS(dn, "forceBroadcast", true);
                Log("  time back to " + h.ToString("0.00") + " (as if the clock had kept running), " + g.dayMinutes + " min/day");
            });
            bad += Step("weather", () => { FS("weather", g.weather); FS("weatherUntil", FNow() + Mathf.Max(5f, g.weatherLeft)); });
            bad += Step("feature polls", () => { Type v = Newest("FeatureVote", "autoPolls"); if (v != null && g.haveVote) SS(v, "autoPolls", g.autoPolls); });
        }
        pinNear = pinGold = false;
        Log("RESTORE done (" + why + "): " + (bad == 0 ? "all steps ok" : bad + " step(s) FAILED - see above"));
        if (bad > 0) Info("Test sandbox: " + bad + " restore step(s) failed - see %TEMP%\\ror2_sandbox.log");
        return bad;
    }

    static void RealMoneyNow(string why)
    {
        if (snap == null) return;
        var money = FG("money") as Dictionary<string, int>;
        if (money == null) return;
        if (snap.hadMoney) money[snap.name] = snap.money; else money.Remove(snap.name);
        Log("real money put back now (" + why + "): " + (snap.hadMoney ? snap.money.ToString() : "none"));
    }

    static int RestorePlayer(Snap s)
    {
        int bad = 0; string n = s.name;
        Type F = Fish();
        bad += Step("money", () =>
        {
            var money = (Dictionary<string, int>)SG(F, "money");
            if (s.hadMoney) money[n] = s.money; else money.Remove(n);
            int m; money.TryGetValue(n, out m);
            FCall("ChatRaw", "[FT] MONEY|" + n + "|" + m); FCall("Board");
        });
        bad += RestorePersistent(s);
        bad += Step("lures", () =>
        {
            var lu = (Dictionary<string, Dictionary<string, int>>)SG(F, "lureUses");
            if (s.hadLures) lu[n] = new Dictionary<string, int>(s.lures); else lu.Remove(n);
            var le = (Dictionary<string, string>)SG(F, "lureEquipped");
            if (s.lureEq != null) le[n] = s.lureEq; else le.Remove(n);
            FCall("BroadcastLure", n);
        });
        bad += Step("fish + items", () =>
        {
            Inventory inv = Inv(n);
            if (inv == null) throw new Exception("no inventory for " + n);
            var parts = new List<string>();
            foreach (string it in TrackedItems())
            {
                int want; if (!s.items.TryGetValue(it, out want)) continue;
                int have = ItemCount(inv, it);
                if (have != want) { ItemAdd(inv, it, want - have); parts.Add(it + " " + have + "->" + want); }
            }
            Log("  items: " + (parts.Count == 0 ? "unchanged" : string.Join(", ", parts.ToArray())));
            var coll = (Dictionary<string, Dictionary<string, int>>)SG(F, "collections");
            if (s.hadColl) coll[n] = new Dictionary<string, int>(s.coll); else coll.Remove(n);
            FCall("Coll", n);
            var dl = (Dictionary<string, int>)SG(F, "doubleLeft");
            if (s.hadDouble) dl[n] = s.dbl; else dl.Remove(n);
            var bu = (Dictionary<string, Dictionary<string, float>>)SG(F, "buffUntil");
            if (s.hadBuff) bu[n] = new Dictionary<string, float>(s.buffs); else bu.Remove(n);
            var tf = (Dictionary<string, int>)SG(F, "tempFeathers");
            if (s.hadFeathers) tf[n] = s.feathers; else tf.Remove(n);
        });
        bad += Step("pet", () =>
        {
            Type dp = Newest("DronePets", "pets"); if (dp == null) return;
            var pets = (IDictionary)SG(dp, "pets");
            object p = pets.Contains(n) ? pets[n] : null;
            if (!s.hadPet)
            {
                string gq = SG(dp, "grantQueue") as string;
                if (gq != null && gq.StartsWith(n + "|")) { SS(dp, "grantQueue", null); Log("  pending temporary pet grant cancelled"); }
                if (p != null) { SS(dp, "removeQueue", n); Log("  temporary pet removed"); }
                return;
            }
            if (p == null) { SS(dp, "grantQueue", n + "|" + s.petType); Log("  pet was gone - re-granted " + s.petType); return; }
            bool changed = (string)IG(p, "type") != s.petType || (bool)IG(p, "on") != s.petOn;
            if (!changed) return;
            IS(p, "type", s.petType); IS(p, "on", s.petOn);
            SCall(dp, "Despawn", p, "sandbox restore");
            IS(p, "respawnAt", 0f);
            Log("  pet back to " + s.petType + (s.petOn ? "" : " (off)"));
        });
        return bad;
    }

    // the parts that live in files (rod_owners.json, glow_owners.txt, gadget_owners.txt, case_items.json, inventory_slots.json)
    static int RestorePersistent(Snap s)
    {
        int bad = 0; string n = s.name;
        Type F = Fish();
        bad += Step("rods/skins/bobbers/items ownership", () =>
        {
            var owners = (IDictionary)SG(F, "rodOwners");
            if (!s.hadOwner) { if (owners.Contains(n)) owners.Remove(n); }
            else
            {
                object o = owners.Contains(n) ? owners[n] : null;
                if (o == null) { o = Activator.CreateInstance(F.GetNestedType("RodOwner", All)); owners[n] = o; }
                var owned = (List<string>)IG(o, "owned"); owned.Clear(); owned.AddRange(s.owned);
                IS(o, "rod", s.rod); IS(o, "skin", s.skin); IS(o, "bobber", s.bobber);
            }
        });
        bad += Step("glow bobber mirror", () =>
        {
            Type rs = Newest("RodStand", "mirror"); if (rs == null) return;
            var gm = (Dictionary<string, string[]>)SG(rs, "mirror");
            if (s.hadGlow) gm[n] = (string[])s.glow.Clone(); else gm.Remove(n);
            SCall(rs, "SaveMirror");
        });
        bad += Step("gadgets (compass / range finder / tracker)", () =>
        {
            Type hc = Newest("HotspotCompass", "mirror"); if (hc == null) return;
            var cm = (HashSet<string>)SG(hc, "mirror");
            foreach (string e in new List<string>(cm))
                if (e.StartsWith(n + "|") && !s.gadgets.Contains(e.Substring(n.Length + 1))) { cm.Remove(e); Log("  gadget " + e + " revoked"); }
            foreach (string g in s.gadgets) cm.Add(n + "|" + g);
            SCall(hc, "SaveMirror");
        });
        bad += Step("case items + StatTrak", () =>
        {
            Type me = Newest("FishMerchant", "caseItems"); if (me == null) return;
            var ci = (Dictionary<string, Dictionary<string, float[]>>)SG(me, "caseItems");
            if (s.hadCase) { var d = new Dictionary<string, float[]>(); foreach (var kv in s.caseItems) d[kv.Key] = (float[])kv.Value.Clone(); ci[n] = d; } else ci.Remove(n);
            var ce = (Dictionary<string, string[]>)SG(me, "caseEquip");
            if (s.hadCaseEq) ce[n] = (string[])s.caseEq.Clone(); else ce.Remove(n);
            var sc = SG(me.Assembly.GetType("CaseSkinLook"), "statCounts") as Dictionary<string, int[]>;
            if (sc != null)
            {
                foreach (string k in new List<string>(sc.Keys)) if (k.StartsWith(n + "|") && !s.stat.ContainsKey(k)) sc.Remove(k);
                foreach (var kv in s.stat) sc[kv.Key] = (int[])kv.Value.Clone();
            }
            SCall(me, "SaveMine");
            SCall(me, "BroadcastInv", n); SCall(me, "BroadcastSkin", n);
        });
        bad += Step("inventory slots", () =>
        {
            Type iv = Newest("Inventory", "slots"); if (iv == null) return;
            var sl = (Dictionary<string, Dictionary<string, string>>)SG(iv, "slots");
            if (s.hadSlots) sl[n] = new Dictionary<string, string>(s.slots); else sl.Remove(n);
            SCall(iv, "SaveSlots");
        });
        bad += Step("save rod_owners.json + broadcast", () => { FCall("SaveRods"); FCall("BroadcastRod", n); });
        return bad;
    }

    // ---------------------------------------------------------------- snapshot file (crash safety for the persistent parts)
    static string Esc(string s) { return (s ?? "\u0000").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n"); }
    static string Unesc(string s) { if (s == "\u0000") return null; return Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value == "t" ? "\t" : m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value); }
    static void WriteSnapFile(Snap s)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("# TestSandbox snapshot - the real state of the player in the test sandbox. Deleted when the sandbox closes normally.\n");
            sb.Append("# If it is still here after a crash, the next TestSandbox<N> puts these back (rod_owners, glow/gadget mirrors, case items, slots).\n");
            sb.Append("player\t" + Esc(s.name) + "\t" + DateTime.Now.ToString("s", Ci) + "\n");
            sb.Append("owner\t" + (s.hadOwner ? "1" : "0") + "\t" + Esc(s.rod) + "\t" + Esc(s.skin) + "\t" + Esc(s.bobber) + "\n");
            foreach (string o in s.owned) sb.Append("owned\t" + Esc(o) + "\n");
            sb.Append("glow\t" + (s.hadGlow ? "1\t" + Esc(s.glow[0]) + "\t" + Esc(s.glow[1]) : "0") + "\n");
            foreach (string g in s.gadgets) sb.Append("gadget\t" + Esc(g) + "\n");
            sb.Append("case\t" + (s.hadCase ? "1" : "0") + "\n");
            foreach (var kv in s.caseItems) { var f = new List<string>(); foreach (float x in kv.Value) f.Add(x.ToString("R", Ci)); sb.Append("caseitem\t" + Esc(kv.Key) + "\t" + string.Join(",", f.ToArray()) + "\n"); }
            sb.Append("caseeq\t" + (s.hadCaseEq ? "1\t" + Esc(s.caseEq[0]) + "\t" + Esc(s.caseEq[1]) : "0") + "\n");
            foreach (var kv in s.stat) sb.Append("stat\t" + Esc(kv.Key) + "\t" + string.Join(",", Array.ConvertAll(kv.Value, x => x.ToString(Ci))) + "\n");
            sb.Append("slots\t" + (s.hadSlots ? "1" : "0") + "\n");
            foreach (var kv in s.slots) sb.Append("slot\t" + Esc(kv.Key) + "\t" + Esc(kv.Value) + "\n");
            string tmp = SnapPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(SnapPath)) File.Delete(SnapPath);
            File.Move(tmp, SnapPath);
            Log("snapshot file written: " + SnapPath);
        }
        catch (Exception e) { Log("snapshot file error: " + e.Message); }
    }
    static Snap ReadSnapFile()
    {
        var s = new Snap();
        foreach (string raw in File.ReadAllLines(SnapPath, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            string[] p = raw.Split(new[] { '\t' });
            switch (p[0])
            {
                case "player": s.name = Unesc(p[1]); break;
                case "owner": s.hadOwner = p[1] == "1"; s.rod = Unesc(p[2]); s.skin = Unesc(p[3]); s.bobber = Unesc(p[4]); break;
                case "owned": s.owned.Add(Unesc(p[1])); break;
                case "glow": s.hadGlow = p[1] == "1"; if (s.hadGlow) s.glow = new[] { Unesc(p[2]) ?? "", Unesc(p[3]) ?? "" }; break;
                case "gadget": s.gadgets.Add(Unesc(p[1])); break;
                case "case": s.hadCase = p[1] == "1"; break;
                case "caseitem": s.caseItems[Unesc(p[1])] = Array.ConvertAll(p[2].Split(new[] { ',' }), x => float.Parse(x, Ci)); break;
                case "caseeq": s.hadCaseEq = p[1] == "1"; if (s.hadCaseEq) s.caseEq = new[] { Unesc(p[2]), Unesc(p[3]) }; break;
                case "stat": s.stat[Unesc(p[1])] = Array.ConvertAll(p[2].Split(new[] { ',' }), x => int.Parse(x, Ci)); break;
                case "slots": s.hadSlots = p[1] == "1"; break;
                case "slot": s.slots[Unesc(p[1])] = Unesc(p[2]); break;
            }
        }
        return s;
    }
    static void DeleteSnapFile(string why) { try { if (File.Exists(SnapPath)) { File.Delete(SnapPath); Log("snapshot file deleted (" + why + ")"); } } catch (Exception e) { Log("snapshot delete error: " + e.Message); } }

    static int recoverTries;
    static void CrashRecovery()
    {
        try
        {
            if (active || !File.Exists(SnapPath)) return;
            if (Fish() == null || Newest("FishMerchant", "caseItems") == null || Newest("RodStand", "mirror") == null)
            {
                if (recoverTries++ < 30) { recoverAt = Time.unscaledTime + 5f; Log("crash recovery: waiting for the fishing add-ons"); }
                return;
            }
            Snap s = ReadSnapFile();
            if (string.IsNullOrEmpty(s.name)) { Log("crash recovery: bad snapshot file - left alone"); return; }
            if (Fish() != null && !(SG(Fish(), "rodOwners") is IDictionary && ((IDictionary)SG(Fish(), "rodOwners")).Count > 0) && recoverTries++ < 30)
            {   // rod_owners.json not loaded yet: restoring now would be overwritten when Fishing<N> loads it
                recoverAt = Time.unscaledTime + 5f; Log("crash recovery: waiting for rod_owners.json to be loaded"); return;
            }
            Log("CRASH RECOVERY: " + SnapPath + " found (the sandbox was open when the game/add-on went away) - restoring " + s.name + "'s saved items");
            int bad = RestorePersistent(s);
            Snap live = Capture(s.name, false);
            var diffs = live == null ? new List<string> { "live state unreadable" } : Diff(s, live, true);
            Log("CRASH RECOVERY VERIFY: " + (diffs.Count == 0 ? "persistent state identical to the snapshot file" : string.Join(" | ", diffs.ToArray())));
            if (bad == 0 && diffs.Count == 0) { File.Move(SnapPath, SnapPath + ".recovered-" + DateTime.Now.ToString("HHmmss")); Log("crash recovery done; file kept as .recovered-*"); }
            else Log("crash recovery: " + bad + " step(s) failed / " + diffs.Count + " difference(s) - snapshot file kept");
        }
        catch (Exception e) { Log("crash recovery error: " + e); }
    }

    // ================================================================== baseline + presets
    // every test start: real state back, then the sandbox defaults (wallet, daylight, clear weather, hotspots near)
    static void Baseline(string why)
    {
        Type dn = Newest("DayNight", "hour");
        if (dn != null) { SS(dn, "hour", 12f); SS(dn, "dayMinutes", 240f); SS(dn, "forceBroadcast", true); }
        FS("weather", "Clear"); FS("weatherUntil", FNow() + 3600f);
        var money = FG("money") as Dictionary<string, int>;
        if (money != null && who != null) { money[who] = startWallet; FCall("ChatRaw", "[FT] MONEY|" + who + "|" + startWallet); FCall("Board"); }
        pinNear = false; pinGold = false;
        Log("baseline (" + why + "): wallet " + startWallet + ", 12:00 (slow clock), Clear");
    }

    static string ApplyPreset(string p)
    {
        string k = p.Trim(), v = "";
        int c = k.IndexOf(':');
        if (c > 0) { v = k.Substring(c + 1).Trim(); k = k.Substring(0, c).Trim(); }
        k = k.ToLowerInvariant();
        Type F = Fish();
        switch (k)
        {
            case "time":
            case "night":
            case "golden":
            case "day":
            case "morning":
                {
                    string tv = k == "time" ? v.ToLowerInvariant() : k;
                    float h;
                    if (tv == "night") h = 22f; else if (tv.StartsWith("golden")) h = 18.5f; else if (tv == "day" || tv == "noon") h = 12f; else if (tv == "morning") h = 6f;
                    else if (!float.TryParse(tv, NumberStyles.Float, Ci, out h)) return "bad hour '" + v + "'";
                    return SetHourNow(h);
                }
            case "weather":
                {   // time words (night / golden hour / day) set the clock and keep the weather; Clear/Rain/Storm set the weather
                    string w = null; var done = new List<string>();
                    foreach (string part0 in v.Split(new[] { '+' }))
                    {
                        string part = part0.Trim().ToLowerInvariant();
                        if (part == "night") done.Add(SetHourNow(22f));
                        else if (part.StartsWith("golden")) done.Add(SetHourNow(18.5f));
                        else if (part == "day") { done.Add(SetHourNow(12f)); if (w == null) w = "Clear"; }
                        else if (part == "rain") { w = "Rain"; done.Add("rain"); }
                        else if (part == "storm") { w = "Storm"; done.Add("storm"); }
                        else if (part == "clear") { w = "Clear"; done.Add("clear"); }
                        else return "unknown weather '" + part0 + "'";
                    }
                    if (w != null) { FS("weather", w); FS("weatherUntil", FNow() + 3600f); }
                    EventNow();
                    return "weather " + string.Join(" + ", done.ToArray()) + " (event now: " + (FG("eventName") ?? "?") + ")";
                }
            case "lightning":
                {   // storm + a bolt right away; "lightning:fast" keeps one coming every ~2.5 s
                    FS("weather", "Storm"); FS("weatherUntil", FNow() + 3600f);
                    EventNow();
                    SS(Fish(), "nextBolt", 0f); FCall("Lightning");
                    pinBolts = v.ToLowerInvariant() == "fast";
                    return "storm + lightning now" + (pinBolts ? " (a bolt every ~2.5 s)" : " (then every 5-12 s)");
                }
            case "bite":
                {   // every cast by the tester gets its bite this many seconds after the bobber lands
                    float s = 1f;
                    if (v.Length > 0 && v.ToLowerInvariant() != "fast" && !float.TryParse(v, NumberStyles.Float, Ci, out s)) return "bad bite time '" + v + "'";
                    pinBite = Mathf.Clamp(s, 0.3f, 30f);
                    return "bites " + pinBite.ToString("0.0", Ci) + " s after landing";
                }
            case "fishatbobber":
                {   // every cast lands next to a fish that takes the bait (school fish spawned right at the bobber)
                    pinBobberFish = true; pinBobberSpecies = v.Length > 0 ? MatchSpecies(v) : null;
                    if (v.Length > 0 && pinBobberSpecies == null) return "unknown fish '" + v + "'";
                    if (pinBite <= 0f) pinBite = 1.2f;
                    return "a " + (pinBobberSpecies ?? "random") + " fish at the bobber on every cast (bite ~" + pinBite.ToString("0.0", Ci) + " s)";
                }
            case "spawnfish": return FishAtBobber(who, v.Length > 0 && v.ToLowerInvariant() != "bobber" ? MatchSpecies(v) : null, true);
            case "goto": return GotoSpot(who, v);
            case "money":
                {
                    int m; if (!int.TryParse(v.Replace(",", "").Replace("$", ""), out m)) return "bad amount";
                    var money = (Dictionary<string, int>)SG(F, "money"); money[who] = m;
                    FCall("ChatRaw", "[FT] MONEY|" + who + "|" + m); FCall("Board");
                    return "wallet " + Money(m);
                }
            case "give": return Give(v);
            case "equip": return EquipPreset(v);
            case "fish":
                {
                    Match mm = Regex.Match(v, @"^(.*?)\s*[xX\*]\s*(\d+)$");
                    string sp = mm.Success ? mm.Groups[1].Value : v; int cnt = mm.Success ? int.Parse(mm.Groups[2].Value) : 1;
                    string species = MatchSpecies(sp); if (species == null) return "unknown fish '" + sp + "'";
                    var map = (Dictionary<string, string>)SG(F, "fishCarrierItem");
                    string carrier; if (!map.TryGetValue(species, out carrier) || string.IsNullOrEmpty(carrier)) return "no carrier item for " + species;
                    ItemAdd(Inv(who), carrier, cnt);
                    var coll = (Dictionary<string, Dictionary<string, int>>)SG(F, "collections");
                    Dictionary<string, int> cd; if (!coll.TryGetValue(who, out cd)) { cd = new Dictionary<string, int>(); coll[who] = cd; }
                    int have; cd.TryGetValue(species, out have); cd[species] = have + cnt;
                    FCall("Coll", who);
                    return cnt + " " + species + " (" + carrier + ")";
                }
            case "pet":
                {
                    Type dp = Newest("DronePets", "pets"); if (dp == null) return "no DronePets";
                    string type = PetType(v); if (type == null) return "unknown pet '" + v + "'";
                    var pets = (IDictionary)SG(dp, "pets");
                    if (SG(dp, "removeQueue") as string == who) SS(dp, "removeQueue", null);   // a restore just queued the removal: keep it and re-skin it instead
                    if (pets.Contains(who))
                    {
                        object pt = pets[who];
                        IS(pt, "type", type); IS(pt, "on", true); SCall(dp, "Despawn", pt, "sandbox preset"); IS(pt, "respawnAt", 0f);
                    }
                    else SS(dp, "grantQueue", who + "|" + type);
                    return "pet " + type;
                }
            case "spawnschool": return SpawnSchool(v.Length > 0 ? v : null);
            case "hotspot":
                if (v == "gold" || v == "golden") { HotGold(); pinGold = true; return "golden hotspot off the dock"; }
                HotNear(); pinNear = true; return "purple hotspot in the pool";
        }
        return "unknown preset '" + p + "'";
    }

    static string MatchSpecies(string a)
    {
        var sp = FG("Species") as string[]; if (sp == null) return null;
        string q = Regex.Replace(a.ToLowerInvariant(), "[^a-z]", "");
        foreach (string s in sp) if (Regex.Replace(s.ToLowerInvariant(), "[^a-z]", "") == q) return s;
        string hit = null;
        foreach (string s in sp) if (Regex.Replace(s.ToLowerInvariant(), "[^a-z]", "").StartsWith(q)) { if (hit != null) return null; hit = s; }
        return hit;
    }
    static readonly string[][] PetTypes = { new[] { "Gunner Drone", "gunner" }, new[] { "Healing Drone", "heal" }, new[] { "Missile Drone", "missile" }, new[] { "Equipment Drone", "equip" } };
    static string PetType(string v)
    {
        string a = v.Trim().ToLowerInvariant(); if (a.Length == 0) return PetTypes[0][0];
        foreach (string[] t in PetTypes) if (a.StartsWith(t[1]) || t[0].ToLowerInvariant().StartsWith(a)) return t[0];
        return null;
    }
    static readonly string[] Lures = { "Spinner", "Glow Lure", "Deep Jig", "Golden Fly", "Feather Lure", "Coral Lure", "Frost Lure", "Swamp Lure", "Sand Lure" };
    static string MatchLure(string v)
    {
        string a = Regex.Replace(v.ToLowerInvariant(), "[^a-z]", "");
        foreach (string l in Lures) { string k = Regex.Replace(l.ToLowerInvariant(), "[^a-z]", ""); if (k == a || k.Replace("lure", "") == a) return l; }
        return null;
    }

    static string Give(string v)
    {
        Type F = Fish();
        string low = v.Trim().ToLowerInvariant();
        if (low.StartsWith("case:") || low.StartsWith("case "))
        {
            string ck = low.Substring(5).Trim();
            int price = ck.StartsWith("rare") || ck.StartsWith("abyss") ? 750 : ck.StartsWith("uncommon") || ck.StartsWith("deep") ? 250 : 100;
            try
            {
                Type me = Newest("FishMerchant", "caseItems"); Type cdT = me == null ? null : me.Assembly.GetType("CaseData");
                object cs = cdT == null ? null : SCall(cdT, "FindCase", ck);
                if (cs != null) price = (int)IG(cs, "price");
            }
            catch { }
            var money = (Dictionary<string, int>)SG(F, "money"); int m; money.TryGetValue(who, out m); money[who] = m + price;
            FCall("ChatRaw", "[FT] MONEY|" + who + "|" + (m + price)); FCall("Board");
            return "+" + Money(price) + " for a " + ck + " case (buy it at the merchant)";
        }
        // aliases
        string nl = Regex.Replace(low, "[^a-z]", "");
        if (nl == "gadgets" || nl == "allgadgets")
        {
            var r = new List<string>();
            foreach (string g in new[] { "Angler's Guide", "Compass", "Range Finder", "Cast Distance Tracker" }) r.Add(Give(g));
            return string.Join("; ", r.ToArray());
        }
        if (nl == "guide" || nl == "book" || nl == "anglersguide") v = "Angler's Guide";
        else if (nl == "tracker" || nl == "castdistancetracker" || nl == "casttracker") v = "Cast Distance Tracker";
        else if (nl == "rangefinder" || nl == "range") v = "Range Finder";
        low = v.Trim().ToLowerInvariant();
        // case-only skins / bobbers (and StatTrak): granted by the merchant exactly like a case drop (wear, pattern, inventory line)
        {
            Type me = Newest("FishMerchant", "caseItems"); Type cdT = me == null ? null : me.Assembly.GetType("CaseData");
            if (cdT != null)
            {
                string q = v; float wear = 0.05f;
                int at = q.LastIndexOf('@');
                if (at > 0) { float wv; if (float.TryParse(q.Substring(at + 1).Trim(), NumberStyles.Float, Ci, out wv)) wear = Mathf.Clamp01(wv); q = q.Substring(0, at).Trim(); }
                string ql = Regex.Replace(q.ToLowerInvariant(), "[^a-z]", "");
                if (ql == "caseskin" || ql == "casebobber" || ql == "stattrak")
                {   // a random case-only one (StatTrak = a StatTrak rod skin)
                    var items = SG(cdT, "Items") as IList; var pick = new List<string>();
                    if (items != null) foreach (object it in items)
                        {
                            if ((bool)IG(it, "shop")) continue;
                            string kind = (string)IG(it, "kind");
                            if ((ql == "casebobber") == (kind == "bobber")) pick.Add((string)IG(it, "name"));
                        }
                    if (pick.Count == 0) return "no case items found";
                    q = pick[rng.Next(pick.Count)];
                    if (ql == "stattrak") q = (string)SG(cdT, "ST") + q;
                }
                string canon = SCall(cdT, "Canon", q) as string;
                object caseOnly = canon == null ? null : SCall(cdT, "IsCaseOnly", canon);
                bool stat = canon != null && SCall(cdT, "IsStat", canon) is bool && (bool)SCall(cdT, "IsStat", canon);
                if (canon != null && ((caseOnly is bool && (bool)caseOnly) || stat))
                {
                    SCall(me, "Grant", who, canon, wear, rng.Next(1000));
                    return canon + " (case item, wear " + wear.ToString("0.00", Ci) + ", granted by the merchant)";
                }
            }
        }
        string lure = MatchLure(v);
        if (lure != null)
        {
            var lu = (Dictionary<string, Dictionary<string, int>>)SG(F, "lureUses");
            Dictionary<string, int> d; if (!lu.TryGetValue(who, out d)) { d = new Dictionary<string, int>(); lu[who] = d; }
            int have; d.TryGetValue(lure, out have); d[lure] = have + 20;
            FCall("BroadcastLure", who);
            return lure + " +20 casts";
        }
        // rod shop catalogue (rods, skins, bobbers, Angler's Guide, gadgets if they are in it, case items once known)
        object item = SCall(F, "FindItem", v);
        string itemName = item != null ? (string)IG(item, "name") : null;
        string[] gadgets = { "Compass", "Range Finder", "Cast Distance Tracker" };
        foreach (string g in gadgets) if (string.Equals(g, v.Trim(), StringComparison.OrdinalIgnoreCase) || Regex.Replace(g.ToLowerInvariant(), "[^a-z]", "") == Regex.Replace(low, "[^a-z]", "")) itemName = g;
        if (itemName == null)
        {   // a case-only skin / bobber: the merchant grants it (wear + pattern)
            Type me = Newest("FishMerchant", "caseItems"); Type cdT = me == null ? null : me.Assembly.GetType("CaseData");
            string canon = cdT == null ? null : SCall(cdT, "Canon", v) as string;
            if (canon == null) return "unknown item '" + v + "'";
            SCall(me, "Grant", who, canon, 0.05f, rng.Next(1000));
            return canon + " (case item)";
        }
        var owners = (IDictionary)SG(F, "rodOwners");
        object o = owners.Contains(who) ? owners[who] : null;
        if (o == null) { o = Activator.CreateInstance(F.GetNestedType("RodOwner", All)); owners[who] = o; }
        var owned = (List<string>)IG(o, "owned");
        string res;
        if (owned.Contains(itemName)) res = itemName + " (already owned)";
        else { owned.Add(itemName); FCall("SaveRods"); FCall("BroadcastRod", who); res = itemName + " (temporary)"; }
        // gadgets: put them on their key in the inventory (Compass -> Shift, Range Finder -> R) so the key works right away
        string gid = itemName == "Compass" ? "compass" : itemName == "Range Finder" ? "rangefinder" : null;
        if (gid != null)
        {
            Type iv = Newest("Inventory", "slots");
            var sl = SG(iv, "slots") as Dictionary<string, Dictionary<string, string>>;
            if (sl != null)
            {
                string slot = gid == "compass" ? "utility" : "special", other = gid == "compass" ? "special" : "utility";
                Dictionary<string, string> d; if (!sl.TryGetValue(who, out d)) { d = new Dictionary<string, string>(); sl[who] = d; }
                string cur; if (d.TryGetValue(other, out cur) && cur == gid) d.Remove(other);
                d[slot] = gid; SCall(iv, "SaveSlots");
                res += ", on " + (slot == "utility" ? "SHIFT" : "R");
            }
        }
        return res;
    }

    static string EquipPreset(string v)
    {
        Type F = Fish();
        string lure = MatchLure(v);
        if (lure != null)
        {
            var le = (Dictionary<string, string>)SG(F, "lureEquipped"); le[who] = lure; FCall("BroadcastLure", who);
            return "lure " + lure + " equipped";
        }
        {   // case items: the merchant's own equip (RodCommand !equip + its caseEquip bookkeeping + the case look broadcast)
            Type me = Newest("FishMerchant", "caseItems"); Type cdT = me == null ? null : me.Assembly.GetType("CaseData");
            string canon = cdT == null ? null : SCall(cdT, "Canon", v) as string;
            object co = canon == null ? null : SCall(cdT, "IsCaseOnly", canon);
            bool stat = canon != null && SCall(cdT, "IsStat", canon) is bool && (bool)SCall(cdT, "IsStat", canon);
            if (canon != null && ((co is bool && (bool)co) || stat))
            {
                var ci = SG(me, "caseItems") as Dictionary<string, Dictionary<string, float[]>>;
                Dictionary<string, float[]> mine;
                if (ci == null || !ci.TryGetValue(who, out mine) || !mine.ContainsKey(canon)) Give(canon);   // equip implies owning it
                object r = SCall(me, "Equip", who, canon, false);
                return "case item " + canon + " equipped (" + r + ")";
            }
        }
        object item = SCall(F, "FindItem", v);
        string itemName = item != null ? (string)IG(item, "name") : v.Trim();
        string kind = item != null ? (string)IG(item, "kind") : null;
        var owners = (IDictionary)SG(F, "rodOwners");
        object o = owners.Contains(who) ? owners[who] : null;
        if (o == null) { o = Activator.CreateInstance(F.GetNestedType("RodOwner", All)); owners[who] = o; }
        if (kind == null)
        {   // case item: skin or bobber? ask the merchant's look class
            Type me = Newest("FishMerchant", "caseItems"); Type look = me == null ? null : me.Assembly.GetType("CaseSkinLook");
            object isSkin = SCall(look, "IsSkin", itemName), isBob = SCall(look, "IsBobber", itemName);
            kind = isSkin is bool && (bool)isSkin ? "skin" : isBob is bool && (bool)isBob ? "bobber" : null;
        }
        if (kind != "rod" && kind != "skin" && kind != "bobber") return "can't equip '" + v + "'";
        IS(o, kind, itemName);
        FCall("SaveRods"); FCall("BroadcastRod", who);
        return kind + " " + itemName + " equipped";
    }

    static string SpawnSchool(string speciesArg)
    {
        Type F = Fish(); if (F == null || info == null) return "no fishing / sandbox";
        Type sfT = F.Assembly.GetType("SchoolFish"), fsT = F.Assembly.GetType("FishSchool");
        var school = SG(F, "school") as IDictionary;
        if (sfT == null || fsT == null || school == null) return "no school API";
        string forced = speciesArg != null ? MatchSpecies(speciesArg) : null;
        var spots = new List<Vector3>();
        Vector3 pc = info.poolCentre, de = info.dockEnd;
        spots.Add(pc + new Vector3(-2.5f, 0, -2f)); spots.Add(pc + new Vector3(2.5f, 0, 1.5f)); spots.Add(pc + new Vector3(0f, 0, 2.5f)); spots.Add(pc + new Vector3(-1f, 0, -0.5f));
        spots.Add(de + new Vector3(-7f, 0, 6f)); spots.Add(de + new Vector3(7f, 0, 6f)); spots.Add(de + new Vector3(0f, 0, 12f)); spots.Add(de + new Vector3(-4f, 0, 18f));
        var made = new List<string>();
        for (int i = 0; i < spots.Count; i++)
        {
            string sp = forced ?? (SCall(fsT, "Roll", rng, i == 2) as string) ?? "Minnow";
            int spi = Convert.ToInt32(SCall(fsT, "Index", sp));
            if (spi < 0) continue;
            int id = (int)SG(F, "schoolNextId"); SS(F, "schoolNextId", id + 1);
            object f = Activator.CreateInstance(sfT);
            float R = i < 4 ? 1.8f : 3f;
            IS(f, "id", id); IS(f, "sp", spi); IS(f, "ax", spots[i].x); IS(f, "az", spots[i].z); IS(f, "R", R); IS(f, "seed", rng.Next(1, 1000000));
            IS(f, "sx", spots[i].x); IS(f, "sz", spots[i].z); IS(f, "t0", Time.realtimeSinceStartup); IS(f, "D", 2.5f);
            school[id] = f;
            SCall(F, "SendFish", f);
            made.Add(sp);
        }
        Log("spawnschool: " + string.Join(", ", made.ToArray()));
        return made.Count + " school fish by the pool + dock (" + string.Join(", ", made.ToArray()) + ")";
    }

    // ================================================================== time / event / pinned test helpers
    // the clock jumps (DayNight<N> lighting reads hour directly, no fade) and Fishing<N>'s event is recomputed now, not in 0.5 s
    static string SetHourNow(float h)
    {
        Type dn = Newest("DayNight", "hour"); if (dn == null) return "no DayNight";
        SS(dn, "hour", Mathf.Repeat(h, 24f)); SS(dn, "forceBroadcast", true);
        EventNow();
        return "time " + DayClock(h) + " (" + (FG("eventName") ?? "?") + ")";
    }
    static string DayClock(float h) { int m = (int)(Mathf.Repeat(h, 24f) * 60f); return (m / 60).ToString("00") + ":" + (m % 60).ToString("00"); }
    static void EventNow()
    {
        Type F = Fish(); if (F == null) return;
        if (F.GetMethod("EventTick", All) != null) SCall(F, "EventTick");
        Type wt = Newest("WeatherTune", null); if (wt != null && wt.GetField("forceApply", All) != null) SS(wt, "forceApply", true);
    }

    static float pinBite = -1f; static bool pinBobberFish, pinBolts; static string pinBobberSpecies;
    static int lastAnglerState; static float lastCastBite = -1f;
    static void ClearPins() { pinBite = -1f; pinBobberFish = false; pinBolts = false; pinBobberSpecies = null; lastAnglerState = 0; }
    static object Angler(string name)
    {
        var an = SG(Fish(), "anglers") as IDictionary;
        return an != null && name != null && an.Contains(name) ? an[name] : null;
    }
    // runs every frame while open (main thread, after Fishing<N>'s fixed tick has set up a new cast)
    static void PinnedTick()
    {
        if (who == null) return;
        if (pinBolts)
        {
            object nb = FG("nextBolt"), en = FG("eventName"); float now = FNow();
            if (nb is float && (float)nb > now + 2.5f) FS("nextBolt", now + 2.5f);
            if (!(en is string) || !((string)en).Contains("Storm")) { FS("weather", "Storm"); FS("weatherUntil", now + 3600f); }
        }
        if (pinBite <= 0f && !pinBobberFish) return;
        object a = Angler(who); if (a == null) return;
        int state = Convert.ToInt32(IG(a, "state"));
        if (state == 1 && lastAnglerState != 1)
        {   // a new cast on water this frame
            if (pinBobberFish) FishAtBobber(who, pinBobberSpecies, false);
            if (pinBite > 0f)
            {
                float land = FNow() + RigFlight(who);
                float want = land + pinBite, cur = Convert.ToSingle(IG(a, "biteAt"));
                if (cur > want) { IS(a, "biteAt", want); Log("bite pinned: " + (cur >= 1e30f ? "no bite" : (cur - land).ToString("0.0") + " s") + " -> " + pinBite.ToString("0.0") + " s after landing"); }
                object h = Hook(who); if (h != null) IS(h, "emptyAt", -1f);
            }
        }
        lastAnglerState = state;
    }
    static float RigFlight(string name)
    {
        var rigs = SG(Fish(), "rigs") as IDictionary;
        object r = rigs != null && rigs.Contains(name) ? rigs[name] : null;
        object f = IG(r, "flight"); return f is float ? (float)f : 0.6f;
    }
    static object Hook(string name)
    {
        var hooks = SG(Fish(), "hooks") as IDictionary;
        return hooks != null && hooks.Contains(name) ? hooks[name] : null;
    }
    // a school fish right at the bobber that is already interested (same bookkeeping as Fishing<N>.SchoolOnCast)
    // oneShot: the line has been out for a while (the fish swims in now); else a cast that just started (bobber still flying)
    static string FishAtBobber(string name, string species, bool oneShot)
    {
        Type F = Fish(); if (F == null) return "no fishing";
        object a = Angler(name); var rigs = SG(F, "rigs") as IDictionary;
        object r = rigs != null && rigs.Contains(name) ? rigs[name] : null;
        if (a == null || r == null || Convert.ToInt32(IG(a, "state")) != 1) return "no line in the water - cast first";
        Type sfT = F.Assembly.GetType("SchoolFish"), fsT = F.Assembly.GetType("FishSchool");
        var school = SG(F, "school") as IDictionary; object h = Hook(name);
        if (sfT == null || fsT == null || school == null || h == null) return "no school API";
        float bx = Convert.ToSingle(IG(r, "tx")), bz = Convert.ToSingle(IG(r, "tz"));
        // free the fish it may already be after
        int old = Convert.ToInt32(IG(h, "fishId"));
        if (old >= 0 && school.Contains(old)) IS(school[old], "busy", null);
        string sp = species ?? (SCall(fsT, "Roll", rng, false) as string) ?? "Minnow";
        int spi = Convert.ToInt32(SCall(fsT, "Index", sp)); if (spi < 0) return "unknown species " + sp;
        int id = (int)SG(F, "schoolNextId"); SS(F, "schoolNextId", id + 1);
        object f = Activator.CreateInstance(sfT);
        float t = Time.realtimeSinceStartup, now = FNow(), flight = Convert.ToSingle(IG(r, "flight"));
        float land = oneShot ? now : now + flight;
        IS(f, "id", id); IS(f, "sp", spi); IS(f, "ax", bx + 0.8f); IS(f, "az", bz + 0.8f); IS(f, "R", 0.6f); IS(f, "seed", rng.Next(1, 1000000));
        IS(f, "sx", bx + 1.5f); IS(f, "sz", bz + 1.5f); IS(f, "t0", t); IS(f, "D", 0.8f); IS(f, "busy", name);
        school[id] = f;
        float delay = pinBite > 0f ? pinBite : 1.2f;
        IS(h, "fishId", id); IS(h, "dist", 0.5f); IS(h, "species", sp); IS(h, "chance", 0.95f); IS(h, "delay", delay);
        IS(h, "activateAt", land); IS(h, "activated", false); IS(h, "emptyAt", -1f); IS(h, "bx", bx); IS(h, "bz", bz);
        IS(a, "biteAt", land + delay);
        SCall(F, "SendFish", f);
        Log("fish at the bobber: #" + id + " " + sp + " at (" + bx.ToString("0.0") + ", " + bz.ToString("0.0") + "), bite in " + (land + delay - now).ToString("0.0") + " s");
        return sp + " spawned at the bobber (bite in " + (land + delay - now).ToString("0.0", Ci) + " s)";
    }

    // ================================================================== goto (sandbox spots, or the golden hotspot's nearest shore on the map)
    static string GotoSpot(string name, string where)
    {
        CharacterBody b = Body(name); if (b == null || !b.healthComponent.alive) return name + ": you need to be alive.";
        string w = (where ?? "").Trim().ToLowerInvariant(); if (w.Length == 0) w = "spawn";
        Vector3 t;
        if (w.StartsWith("gold") || w.StartsWith("hot"))
        {
            bool gold = w.StartsWith("gold");
            object hp = FG(gold ? "hot2Pos" : "hotspotPos"), has = FG(gold ? "hasHot2" : "hasHotspot");
            if (!(hp is Vector3) || (has is bool && !(bool)has)) return "There is no " + (gold ? "golden " : "") + "hotspot right now.";
            Vector3 hv = (Vector3)hp;
            if (active && info != null && InSandboxArea(hv))
                t = gold ? info.dockEnd + new Vector3(0f, 0.2f, -1.5f) : info.poolCentre + new Vector3(0f, SandboxLook.Deck + 0.2f, -SandboxLook.PoolHalf - 1.5f);
            else
            {
                Vector3? s = Shore(hv); if (s == null) return "No dry ground found near the " + (gold ? "golden " : "") + "hotspot.";
                t = s.Value;
            }
            TeleportHelper.TeleportBody(b, t);
            Log("goto " + w + " -> " + t.ToString("0.00") + " (hotspot at " + hv.ToString("0.0") + ", " + Vector3.Distance(new Vector3(t.x, 0, t.z), new Vector3(hv.x, 0, hv.z)).ToString("0") + " m)");
            return "Teleported next to the " + (gold ? "golden " : "") + "hotspot (" + Vector3.Distance(new Vector3(t.x, 0, t.z), new Vector3(hv.x, 0, hv.z)).ToString("0") + " m away) - cast toward it.";
        }
        if (!active || info == null) return "That spot is in the test sandbox - !sandbox first (gold / hotspot work on the map too).";
        t = w == "stall" ? info.stall + new Vector3(2.6f, 0.2f, 0f) : w == "merchant" ? info.merchant + new Vector3(-2.8f, 0.2f, 0f)
          : w == "pool" ? info.poolCentre + new Vector3(0f, SandboxLook.Deck + 0.2f, -SandboxLook.PoolHalf - 1.5f) : w == "dock" ? info.dockEnd + new Vector3(0f, 0.2f, -1.5f)
          : w == "dummy" ? info.origin + new Vector3(-6f, SandboxLook.Deck + 0.2f, -19f) : info.spawn;
        TeleportHelper.TeleportBody(b, t); Log("goto " + w + " -> " + t.ToString("0.00"));
        return "Teleported to the " + w + ".";
    }
    // nearest dry, standable ground to a point on the water (rings of 4..80 m)
    static Vector3? Shore(Vector3 p)
    {
        float wy; if (!LakeY(out wy)) wy = p.y;
        int mask = LayerIndex.world.mask.value;
        for (float r = 4f; r <= 80f; r += 3f)
            for (int k = 0; k < 24; k++)
            {
                float a = k * Mathf.PI * 2f / 24f;
                Vector3 q = new Vector3(p.x + Mathf.Cos(a) * r, wy + 60f, p.z + Mathf.Sin(a) * r);
                RaycastHit h;
                if (!Physics.Raycast(q, Vector3.down, out h, 140f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (h.point.y < wy + 0.3f || h.normal.y < 0.7f) continue;
                Vector3 s = h.point + Vector3.up * 0.3f;
                if (Physics.CheckCapsule(s + Vector3.up * 0.6f, s + Vector3.up * 1.6f, 0.4f, mask, QueryTriggerInteraction.Ignore)) continue;
                return s;
            }
        return null;
    }

    // ================================================================== leaderboard: guests see the REAL money while JV94 is inside
    // Fishing<N> / the merchant / DronePets broadcast [FT] MONEY + [FT] BOARD from money[], which holds the sandbox wallet.
    // Right after any of those (and when the wallet changes, and every 20 s) the sandbox re-sends both lines with the real money,
    // so the last line every guest client got is the real one. The host's own HUD (FishHud<N>) still shows the wallet (labelled).
    static bool boardDirty; static int lastWalletSent = int.MinValue; static float nextBoardFix, lastBoardFix = -100f;
    static void BoardTick()
    {
        var money = FG("money") as Dictionary<string, int>; if (money == null || snap == null) return;
        int w; money.TryGetValue(who, out w);
        if (w != lastWalletSent) { lastWalletSent = w; boardDirty = true; }
        if ((boardDirty && Time.unscaledTime - lastBoardFix > 0.4f) || Time.unscaledTime >= nextBoardFix) BoardFix(false);
    }
    static void BoardFix(bool closing)
    {
        try
        {
            boardDirty = false; lastBoardFix = Time.unscaledTime; nextBoardFix = Time.unscaledTime + 20f;
            var money = FG("money") as Dictionary<string, int>; if (money == null || snap == null || who == null) return;
            int real = snap.hadMoney ? snap.money : 0;
            if (closing) { int m; money.TryGetValue(who, out m); real = m; }
            var all = new Dictionary<string, int>(money);
            all[who] = real;
            foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && !all.ContainsKey(nu.userName)) all[nu.userName] = 0;
            var list = new List<KeyValuePair<string, int>>(all);
            list.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.Compare(x.Key, y.Key));
            var parts = new List<string>(); foreach (var kv in list) parts.Add(kv.Key + ":" + kv.Value);
            Raw("[FT] MONEY|" + who + "|" + real);
            Raw("[FT] BOARD|" + string.Join("|", parts.ToArray()));
        }
        catch (Exception e) { if (errors++ < 40) Log("board fix error: " + e.Message); }
    }
    static void Raw(string text) { try { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); } catch (Exception e) { Log("raw chat error: " + e.Message); } }

    // ================================================================== guest protocol (off: guestProtocol = false; see STOPPING-POINT)
    static void GuestLine(string body) { if (guestProtocol) { Raw("[FT] SANDBOX|" + body); Log("guest line [FT] SANDBOX|" + body); } }

    // ================================================================== test queue + results
    public class Feature { public string id, name, where = "sandbox", qa, status, summary; public List<string> setup = new List<string>(), steps = new List<string>(); }
    public class Result { public string verdict, text, ts, player; }
    public static readonly List<Feature> queue = new List<Feature>();
    public static readonly Dictionary<string, Result> lastResult = new Dictionary<string, Result>();          // newest note per feature
    public static readonly Dictionary<string, List<Result>> history = new Dictionary<string, List<Result>>();  // every note per feature (oldest first)
    // notes written this session: kept even when the triage job empties test_results.jsonl after filing them
    static readonly List<KeyValuePair<string, Result>> sessionNotes = new List<KeyValuePair<string, Result>>();
    public static int curTest = -1, page;
    public static readonly HashSet<int> checks = new HashSet<int>();
    static DateTime queueStamp, resultsStamp;

    static List<string> LoadQueue(bool force)
    {
        var added = new List<string>();
        try
        {
            if (!File.Exists(QueuePath)) { queue.Clear(); Log("no " + QueuePath); return added; }
            DateTime st = File.GetLastWriteTimeUtc(QueuePath);
            if (!force && st == queueStamp && queue.Count > 0) return added;
            int p = 0;
            var root = J(File.ReadAllText(QueuePath, Encoding.UTF8), ref p) as Dictionary<string, object>;
            var feats = root == null ? null : (root.ContainsKey("features") ? root["features"] : null) as List<object>;
            if (feats == null) { Log("queue: no \"features\" array in " + QueuePath + " - keeping the old queue"); return added; }
            queueStamp = st;
            string curId = curTest >= 0 && curTest < queue.Count ? queue[curTest].id : null;
            var oldIds = new HashSet<string>(); foreach (Feature f0 in queue) oldIds.Add(f0.id);
            queue.Clear();
            foreach (object fo in feats)
            {
                var d = fo as Dictionary<string, object>; if (d == null) continue;
                var f = new Feature();
                object x;
                if (d.TryGetValue("id", out x)) f.id = x as string;
                if (d.TryGetValue("name", out x)) f.name = x as string;
                if (d.TryGetValue("where", out x) && x is string) f.where = (string)x;
                if (d.TryGetValue("qa", out x) && x is string && (string)x != "null") f.qa = (string)x;
                if (d.TryGetValue("status", out x) && x is string) f.status = (string)x;
                if (d.TryGetValue("summary", out x) && x is string) f.summary = (string)x;
                if (d.TryGetValue("setup", out x) && x is List<object>) foreach (object s in (List<object>)x) if (s is string) f.setup.Add((string)s);
                if (d.TryGetValue("steps", out x) && x is List<object>) foreach (object s in (List<object>)x) if (s is string) f.steps.Add((string)s);
                if (f.id == null) continue;
                if (f.name == null) f.name = f.id;
                queue.Add(f);
                if (oldIds.Count > 0 && !oldIds.Contains(f.id)) added.Add(f.id);
            }
            // keep the running test on the same feature even if its row number moved
            int ni = -1; if (curId != null) for (int i = 0; i < queue.Count; i++) if (queue[i].id == curId) ni = i;
            if (curId != null && ni != curTest) { if (ni < 0) checks.Clear(); curTest = ni; }
            Log("queue loaded: " + queue.Count + " features from " + QueuePath + (added.Count > 0 ? " (new: " + string.Join(", ", added.ToArray()) + ")" : ""));
        }
        catch (Exception e) { Log("queue load error: " + e.Message); }
        return added;
    }
    static void LoadResults()
    {
        lastResult.Clear(); history.Clear();
        var seen = new HashSet<string>();
        try
        {
            if (File.Exists(ResultsPath))
            {
                resultsStamp = File.GetLastWriteTimeUtc(ResultsPath);
                string[] lines;
                using (var fs = new FileStream(ResultsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) lines = sr.ReadToEnd().Split(new[] { '\n' });
                foreach (string line in lines)
                {
                    if (line.Trim().Length == 0) continue;
                    int p = 0; var d = J(line, ref p) as Dictionary<string, object>; if (d == null) continue;
                    object x;
                    if (d.TryGetValue("source", out x) && x is string && (string)x == "agent-test") continue;   // dry runs don't count as tested
                    string id = d.TryGetValue("featureId", out x) ? x as string : null; if (id == null) continue;
                    var r = new Result();
                    r.verdict = d.TryGetValue("verdict", out x) ? x as string : null; if (r.verdict == "null") r.verdict = null;
                    r.text = d.TryGetValue("text", out x) ? x as string : ""; r.ts = d.TryGetValue("ts", out x) ? x as string : ""; r.player = d.TryGetValue("player", out x) ? x as string : "";
                    AddResult(id, r); seen.Add(id + "|" + r.ts + "|" + r.text);
                }
            }
            else resultsStamp = DateTime.MinValue;
        }
        catch (Exception e) { Log("results load error: " + e.Message); }
        foreach (var kv in sessionNotes) if (!seen.Contains(kv.Key + "|" + kv.Value.ts + "|" + kv.Value.text)) AddResult(kv.Key, kv.Value);
    }
    static void AddResult(string id, Result r)
    {
        lastResult[id] = r;
        List<Result> l; if (!history.TryGetValue(id, out l)) { l = new List<Result>(); history[id] = l; }
        l.Add(r);
    }
    // !test sync / !sandbox sync: re-read both files now (no re-inject)
    static void SyncQueue(bool say)
    {
        int before = queue.Count;
        List<string> added = LoadQueue(true);
        LoadResults();
        int tested = 0; foreach (Feature f in queue) if (lastResult.ContainsKey(f.id)) tested++;
        string msg = "Test queue synced: " + queue.Count + " features (" + (queue.Count - tested) + " without a note yet)" + (added.Count > 0 ? ", new: " + string.Join(", ", added.ToArray()) : "")
                     + (before > 0 && queue.Count != before ? " (was " + before + ")" : "") + ".";
        Log(msg);
        if (say) Info(msg);
    }
    // every 5 s: pick up a regenerated test_queue.json / new notes by itself
    static void AutoSync()
    {
        try
        {
            if (File.Exists(QueuePath) && File.GetLastWriteTimeUtc(QueuePath) != queueStamp && queue.Count > 0)
            {
                var added = LoadQueue(false);
                if (active || curTest >= 0) Info("Test queue updated: " + queue.Count + " features" + (added.Count > 0 ? " (new: " + string.Join(", ", added.ToArray()) + ")" : "") + ".");
            }
            DateTime rs = File.Exists(ResultsPath) ? File.GetLastWriteTimeUtc(ResultsPath) : DateTime.MinValue;
            if (rs != resultsStamp) LoadResults();
        }
        catch (Exception e) { if (errors++ < 40) Log("auto sync error: " + e.Message); }
    }

    static int FindFeature(string a)
    {
        int n;
        if (int.TryParse(a, out n)) return n >= 1 && n <= queue.Count ? n - 1 : -1;
        string q = a.Trim().ToLowerInvariant(); if (!q.StartsWith("n-")) q = "n-" + q;
        for (int i = 0; i < queue.Count; i++) if (queue[i].id.ToLowerInvariant() == q) return i;
        return -1;
    }

    static void TestCmd(string name, string arg)
    {
        LoadQueue(false);
        if (queue.Count == 0) { Info("No test queue (" + QueuePath + ")."); return; }
        string a = arg.Trim().ToLowerInvariant();
        if (a == "" || a == "list" || a == "queue") { LoadResults(); if (!noteOpen) AcquireInput("panel"); ShowQueueChat(); return; }
        if (a == "off" || a == "stop" || a == "end") { curTest = -1; checks.Clear(); ClearPins(); Info("Feature test stopped."); return; }
        if (a == "sync" || a == "reload") { SyncQueue(true); return; }
        if (a.StartsWith("preset ") || a.StartsWith("p "))
        {   // apply one preset now, in the middle of a test (e.g. !test preset lightning:fast / night / fishatbobber:Moon Eel)
            if (!active) { Info("Presets only work inside the test sandbox (!sandbox)."); return; }
            string pr = arg.Trim().Substring(a.StartsWith("p ") ? 2 : 7).Trim();
            string r; try { r = ApplyPreset(pr); } catch (Exception e) { r = "ERROR " + (e.InnerException ?? e).Message; Log("preset " + pr + " error: " + e); }
            Log("preset (chat) " + pr + " -> " + r);
            Info("Preset " + pr + ": " + r);
            return;
        }
        if (a.StartsWith("goto ")) { Info(GotoSpot(name, a.Substring(5))); return; }
        if (a == "reset" || a == "again") { if (curTest < 0) { Info("No feature test running - !test 5 (any number) first."); return; } StartTest(curTest, true); return; }
        if (a == "next")
        {
            LoadResults();
            for (int k = 1; k <= queue.Count; k++)
            {
                int i = (Math.Max(curTest, -1) + k) % queue.Count;
                if (!lastResult.ContainsKey(queue[i].id)) { StartTest(i, false); return; }
            }
            Info("Every feature in the queue has a comment. !test 5 (any number) to test one again.");
            return;
        }
        int idx = FindFeature(a);
        if (idx < 0) { Info("No test '" + arg + "'. !test shows the list (1-" + queue.Count + ")."); return; }
        StartTest(idx, false);
    }

    static void ShowQueueChat()
    {
        var l = new List<string>();
        for (int i = 0; i < queue.Count; i++)
        {
            Result r; lastResult.TryGetValue(queue[i].id, out r);
            l.Add((i + 1) + " " + queue[i].id.Substring(2) + (r == null ? "" : r.verdict == "pass" ? "+" : r.verdict == "fail" ? "x" : "*"));
        }
        Info("Test queue (" + queue.Count + "): " + string.Join(", ", l.ToArray()) + "   (+ pass, x fail, * noted) - !test 5 or F6 + number keys.");
    }

    static void StartTest(int i, bool reset)
    {
        Feature f = queue[i];
        curTest = i; checks.Clear(); panelFocus = false; page = i / 10;
        if (inputOwner == "panel") ReleaseInput("test started", false);
        var done = new List<string>();
        if (active)
        {
            int guests = 0; foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName != who) guests++;
            bool global = false;
            foreach (string p in f.setup) { string pk = p.Trim().ToLowerInvariant(); if (pk.StartsWith("time") || pk.StartsWith("weather") || pk.StartsWith("night") || pk.StartsWith("golden") || pk.StartsWith("day") || pk.StartsWith("morning") || pk.StartsWith("lightning")) global = true; }
            if (global && guests > 0) Info("Heads-up: this test changes the time/weather for EVERYONE online (" + guests + " other player" + (guests == 1 ? "" : "s") + "). It goes back when you leave the sandbox.");
            RestoreAll("test " + f.id + " start", false);
            Baseline(f.id);
            HotNear(); HotGold();
            foreach (string p in f.setup)
            {
                string r;
                try { r = ApplyPreset(p); } catch (Exception e) { r = "ERROR " + (e.InnerException ?? e).Message; Log("preset " + p + " error: " + e); }
                done.Add(r); Log("preset " + p + " -> " + r);
            }
        }
        string where = f.where == "map" ? " (a MAP test: do it on the stage" + (active ? " - !sandbox leave first" : "") + ")" : "";
        Info((reset ? "Test reset: " : "Testing ") + "#" + (i + 1) + " " + f.name + where + ". " + f.steps.Count + " steps on screen. !note (your comment) when done, or !pass / !fail."
             + (active && done.Count > 0 ? " Setup: " + string.Join("; ", done.ToArray()) : (!active && f.setup.Count > 0 ? " (presets only apply inside the sandbox)" : "")));
        Log("TEST " + (reset ? "RESET " : "START ") + f.id + " presets [" + string.Join(" | ", done.ToArray()) + "]");
    }

    static void Check(int n)
    {
        if (curTest < 0 || n < 1 || n > queue[curTest].steps.Count) return;
        if (!checks.Remove(n)) checks.Add(n);
    }

    static string Js(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\"); else if (c == '\n') sb.Append("\\n"); else if (c == '\r') sb.Append("\\r"); else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4")); else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static void Note(string player, string verdict, string text)
    {
        LoadQueue(false);
        int fi = curTest;
        string t = (text ?? "").Trim();
        // optional feature id first: "!note n-swim breath bar ok" / "!pass swim"
        string first = t.Split(new[] { ' ' })[0];
        if (first.Length > 0 && (first.StartsWith("n-") || first.StartsWith("#")))
        {
            int k = FindFeature(first.TrimStart(new[] { '#' }));
            if (k >= 0) { fi = k; t = t.Substring(first.Length).Trim(); }
        }
        if (fi < 0 || fi >= queue.Count) { Info(player + ": start a feature test first (!test 5), or write !note n-swim (your comment)."); return; }
        if (t.Length == 0 && verdict == null) { Info("!note (your comment on " + queue[fi].name + ")"); return; }
        Feature f = queue[fi];
        string line = "{\"featureId\":" + Js(f.id) + ",\"feature\":" + Js(f.name) + ",\"player\":" + Js(player) + ",\"text\":" + Js(t) + ",\"verdict\":" + (verdict == null ? "null" : Js(verdict))
                      + ",\"ts\":" + Js(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Ci)) + (noteSource != null ? ",\"source\":" + Js(noteSource) : "") + "}";
        try { File.AppendAllText(ResultsPath, line + "\n", new UTF8Encoding(false)); }
        catch (Exception e) { Log("results write error: " + e.Message); Info("Couldn't save the note: " + e.Message); return; }
        if (noteSource == null)
        {
            var r = new Result { verdict = verdict, text = t, ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Ci), player = player };
            sessionNotes.Add(new KeyValuePair<string, Result>(f.id, r)); AddResult(f.id, r);
            try { resultsStamp = File.GetLastWriteTimeUtc(ResultsPath); } catch { }
        }
        Log("NOTE " + line);
        Info("Noted for " + f.name + (verdict == "pass" ? " (PASS)" : verdict == "fail" ? " (needs more work)" : "") + ".");
        noteFlash = (verdict == "pass" ? "PASS saved" : verdict == "fail" ? "FAIL saved" : "Note saved") + " for " + f.name; noteFlashUntil = UNow() + 4f;
    }
    static string noteFlash; static float noteFlashUntil;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static float UNow() { return Time.unscaledTime; }   // separate method: Note() stays callable outside Unity (offline harness)

    // minimal JSON reader (objects, arrays, strings; numbers/true/false/null as strings)
    static object J(string s, ref int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length) return null;
        char c = s[p];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); p++;
            while (true)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) return d;
                if (s[p] == '}') { p++; return d; }
                if (s[p] == ',') { p++; continue; }
                string key = J(s, ref p) as string;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p < s.Length && s[p] == ':') p++;
                object val = J(s, ref p);
                if (key != null) d[key] = val;
            }
        }
        if (c == '[')
        {
            var l = new List<object>(); p++;
            while (true)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) return l;
                if (s[p] == ']') { p++; return l; }
                if (s[p] == ',') { p++; continue; }
                l.Add(J(s, ref p));
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
                    if (e == 'n') sb.Append('\n'); else if (e == 't') sb.Append('\t'); else if (e == 'r') sb.Append('\r');
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
        return s.Substring(st, Math.Max(1, p - st));
    }

    // ================================================================== keys (host): F6 focus, number keys, paging, N note box; right click on dummies
    static readonly bool[] keyWas = new bool[24];
    static bool Edge(int slot, int vk) { bool d = (GetAsyncKeyState(vk) & 0x8000) != 0; bool e = d && !keyWas[slot]; keyWas[slot] = d; return e; }
    static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    static bool Foreground() { uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg); return fg == myPid; }
    static readonly List<UnityEngine.Object> chatBoxes = new List<UnityEngine.Object>();
    static PropertyInfo showInputP;
    static bool ChatOpen()
    {
        try
        {
            if (showInputP == null) showInputP = typeof(RoR2.UI.ChatBox).GetProperty("showInput", All);
            foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>())
            {
                if (b == null) continue;
                FieldInfo f = typeof(RoR2.UI.ChatBox).GetField("showInput", All);
                object v = showInputP != null ? showInputP.GetValue(b, null) : f != null ? f.GetValue(b) : null;
                if (v is bool && (bool)v) return true;
            }
        }
        catch { }
        return false;
    }
    static void CloseChatBox()
    {
        try
        {
            if (showInputP == null) showInputP = typeof(RoR2.UI.ChatBox).GetProperty("showInput", All);
            foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>())
                if (b != null && showInputP != null && showInputP.CanWrite && (bool)showInputP.GetValue(b, null)) { showInputP.SetValue(b, false, null); Log("game chat box closed again (Enter in the note box opened it)"); }
        }
        catch { }
    }
    // another add-on owns the keyboard right now (ReqPanel1 typing, the inventory window, the guide book)
    static bool OthersOwnInput()
    {
        try
        {
            foreach (Type t in TypesNamed("ReqPanel"))
            {
                if (t == typeof(ReqPanel17)) continue;
                int n = int.Parse(t.Name.Substring(8));
                if (n == 19 || n == 20 || n == 17) continue;   // the shim (we write it ourselves while we own input) / other sandbox copies
                FieldInfo f = t.GetField("open", All);
                if (f != null && f.IsStatic && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) return true;
            }
            Type inv = Newest("Inventory", "slots"); Type look = inv == null ? null : inv.Assembly.GetType("InvLook");
            object o = SG(look, "open"); if (o is bool && (bool)o) return true;
            Type gb = Newest("GuideBook", null); object g = SG(gb, "isOpen"); if (g is bool && (bool)g) return true;
        }
        catch { }
        return false;
    }

    static void Keys()
    {
        if (!Foreground()) { for (int i = 0; i < keyWas.Length; i++) keyWas[i] = true; return; }
        bool f6 = Edge(0, 0x75), esc = Edge(1, 0x1B), pgUp = Edge(12, 0x21), pgDn = Edge(13, 0x22), nKey = Edge(14, 0x4E);
        int num = -1; for (int k = 0; k < 10; k++) if (Edge(2 + k, 0x30 + k)) num = k;
        if (noteOpen) return;                    // the note box reads its own keys (OnGui)
        if (f6)
        {
            if (ChatOpen() || (!panelFocus && OthersOwnInput())) return;
            LoadQueue(false); if (!panelFocus) LoadResults();
            if (panelFocus) ReleaseInput("F6", false);
            else { AcquireInput("panel"); if (curTest >= 0) page = curTest / 10; }
            return;
        }
        if (nKey && (active || curTest >= 0 || panelFocus) && !ChatOpen() && (panelFocus || !OthersOwnInput())) { OpenNote(); return; }
        if (!panelFocus) return;
        if (esc) { ReleaseInput("Esc", false); return; }
        if (ChatOpen()) return;
        int pages = Math.Max(1, (queue.Count + 9) / 10);
        if (pgUp) page = (page + pages - 1) % pages;
        if (pgDn) page = (page + 1) % pages;
        if (num >= 0)
        {
            int row = num == 0 ? 9 : num - 1, i = page * 10 + row;
            if (i < queue.Count) StartTest(i, false);
        }
    }

    // ---------------------------------------------------------------- input ownership
    static float releasedAt = -100f;
    static bool gameBlocked, cursorAdded, resetKbControl;
    static object blockEs; static GameObject focusGo;
    static readonly List<object> disabledMaps = new List<object>();
    static readonly Dictionary<FieldInfo, object> parkedKeys = new Dictionary<FieldInfo, object>();
    static Type merchUi; static bool merchSaved, merchDismissedWas;
    static int blockErrors;

    static void AcquireInput(string mode)
    {
        if (mode == "panel") panelFocus = true;
        if (mode == "note") noteOpen = true;
        string was = inputOwner;
        inputOwner = noteOpen ? "note" : panelFocus ? "panel" : null;
        try { AppDomain.CurrentDomain.SetData("FT.InputOwner", inputOwner == null ? null : "TestSandbox6:" + inputOwner); } catch { }
        if (was != inputOwner) Log("input owner: " + (was ?? "-") + " -> " + (inputOwner ?? "-"));
        HoldOthers();
    }
    // all: drop panel focus and the note box. Otherwise the note box closes first (back to the panel if it had focus).
    static void ReleaseInput(string why, bool all)
    {
        if (all) { noteOpen = false; panelFocus = false; }
        else if (noteOpen) noteOpen = false;
        else panelFocus = false;
        string was = inputOwner;
        inputOwner = noteOpen ? "note" : panelFocus ? "panel" : null;
        try { AppDomain.CurrentDomain.SetData("FT.InputOwner", inputOwner == null ? null : "TestSandbox6:" + inputOwner); } catch { }
        if (was != inputOwner) Log("input owner: " + (was ?? "-") + " -> " + (inputOwner ?? "-") + " (" + why + ")");
        if (inputOwner != null) return;
        ReqPanel17.open = false;
        try
        {
            bool others = false;
            foreach (Type t in TypesNamed("ReqPanel"))
            {
                int n = int.Parse(t.Name.Substring(8)); if (n == 17 || n == 19 || n == 20) continue;
                FieldInfo f = t.GetField("open", All); if (f != null && f.IsStatic && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) others = true;
            }
            Type inv = Newest("Inventory", "slots"); Type look = inv == null ? null : inv.Assembly.GetType("InvLook");
            object bl = SG(look, "blocking"); if (bl is bool && (bool)bl) others = true;
            foreach (Type t in TypesNamed("ReqPanel"))
            {
                int n = int.Parse(t.Name.Substring(8)); if (n != 19 && n != 20) continue;
                FieldInfo f = t.GetField("open", All); if (f != null && f.IsStatic && f.FieldType == typeof(bool)) f.SetValue(null, others);
            }
        }
        catch { }
        foreach (var kv in parkedKeys) { try { kv.Key.SetValue(null, kv.Value); Log("key back: " + kv.Key.DeclaringType.Name + "." + kv.Key.Name + " = " + kv.Value); } catch { } }
        parkedKeys.Clear();
        if (merchSaved && merchUi != null) { try { SS(merchUi, "dismissed", merchDismissedWas); } catch { } }
        merchSaved = false;
        resetKbControl = true;
        releasedAt = Time.unscaledTime;
    }
    static void SetTrue(Type t, string field)
    {
        FieldInfo f = t.GetField(field, All);
        if (f != null && f.IsStatic && f.FieldType == typeof(bool)) f.SetValue(null, true);
    }
    // keep every other add-on's key edges "already down" so nothing fires while the sandbox owns the keyboard
    static void HoldOthers()
    {
        ReqPanel17.open = true;
        try
        {
            foreach (Type t in TypesNamed("ReqPanel"))
            {
                int n = int.Parse(t.Name.Substring(8)); if (n != 19 && n != 20) continue;
                FieldInfo f = t.GetField("open", All); if (f != null && f.IsStatic && f.FieldType == typeof(bool)) f.SetValue(null, true);
            }
            foreach (Type t in TypesNamed("RodStand")) { var kw = SG(t, "keyWas") as bool[]; if (kw != null) for (int i = 0; i < kw.Length; i++) kw[i] = true; }
            foreach (Type t in TypesNamed("GuideBook")) foreach (string fn in new[] { "gDown", "escDown", "lDown", "rDown", "aDown", "dDown" }) SetTrue(t, fn);
            foreach (Type t in TypesNamed("Fishing")) foreach (string fn in new[] { "fWasDown", "mWasDown", "rWasDown" }) SetTrue(t, fn);
            foreach (Type t in TypesNamed("WhipAnim")) SetTrue(t, "rightWasDown");
            rWas = true;
            // the merchant's number-key menu: dismissed while we own the keys (put back on release)
            Type me = Newest("FishMerchant", "merchantPos"); Type mui = me == null ? null : me.Assembly.GetType("MerchantUI");
            if (mui != null)
            {
                if (!merchSaved || mui != merchUi) { merchUi = mui; object d = SG(mui, "dismissed"); merchDismissedWas = d is bool && (bool)d; merchSaved = true; }
                SS(mui, "dismissed", true);
            }
            // Unity-input hotkeys (I = inventory, L = flashlight) parked on KeyCode.None
            foreach (string pre in new[] { "Inventory", "Flashlight" })
                foreach (Type t in TypesNamed(pre))
                {
                    FieldInfo f = t.GetField("key", All);
                    if (f == null || !f.IsStatic || f.FieldType != typeof(KeyCode) || parkedKeys.ContainsKey(f)) continue;
                    object v = f.GetValue(null); if ((KeyCode)v == KeyCode.None) continue;
                    parkedKeys[f] = v; f.SetValue(null, KeyCode.None);
                }
        }
        catch (Exception e) { if (errors++ < 40) Log("hold others error: " + e.Message); }
    }
    static int closeChatFrames;
    static void InputTick()
    {
        if (closeChatFrames > 0) { closeChatFrames--; CloseChatBox(); }
        if (inputOwner != null)
        {
            if (!Foreground() && inputOwner == "panel") { }   // keep focus while alt-tabbed; keys are ignored anyway
            HoldOthers();
            BlockGame();
            return;
        }
        if (gameBlocked && (Time.unscaledTime - releasedAt > 1f || !(Down(0x1B) || Down(0x0D) || Down(0x75))))
            UnblockGame();
    }

    // the game's own input, like ReqPanel1 / InvLook: a selected focus object (LocalUser.isUIFocused -> no body input),
    // the Rewired keyboard + mouse maps off (no pause on Esc, no skills from a click) and one more cursor opener (mouse visible)
    static object LocalUserObj() { try { return LocalUserManager.GetFirstLocalUser(); } catch { return null; } }
    static object PGet(object o, string n)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(n, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(n, All); return f != null ? f.GetValue(o) : null;
    }
    static Type ctrlType;
    static void BlockGame()
    {
        try
        {
            if (focusGo == null) { focusGo = new GameObject("TestSandbox6Focus"); UnityEngine.Object.DontDestroyOnLoad(focusGo); focusGo.AddComponent<SandboxNoteInputField>(); }
            object lu = LocalUserObj(); if (lu == null) return;
            object es = PGet(lu, "eventSystem");
            if (!Dead(es))
            {
                object cur = PGet(es, "currentSelectedGameObject");
                if (!ReferenceEquals(cur, focusGo))
                {
                    MethodInfo sel = es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) });
                    if (sel != null) sel.Invoke(es, new object[] { focusGo });
                }
                if (!cursorAdded)
                {
                    PropertyInfo p = es.GetType().GetProperty("cursorOpenerCount", All);
                    if (p != null) { p.SetValue(es, (int)p.GetValue(es, null) + 1, null); cursorAdded = true; blockEs = es; }
                }
            }
            if (ctrlType == null) foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "Rewired_Core") ctrlType = a.GetType("Rewired.ControllerType");
            object maps = PGet(PGet(PGet(lu, "inputPlayer"), "controllers"), "maps");
            if (maps != null && ctrlType != null)
            {
                MethodInfo getAll = null;
                foreach (MethodInfo mi in maps.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (mi.Name == "GetAllMaps" && !mi.IsGenericMethodDefinition && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == ctrlType) { getAll = mi; break; }
                if (getAll != null)
                    foreach (string ct in new[] { "Keyboard", "Mouse" })
                    {
                        IEnumerable l = getAll.Invoke(maps, new object[] { Enum.Parse(ctrlType, ct) }) as IEnumerable;
                        if (l == null) continue;
                        foreach (object map in l)
                        {
                            PropertyInfo en = map.GetType().GetProperty("enabled", All);
                            if (en != null && (bool)en.GetValue(map, null)) { en.SetValue(map, false, null); disabledMaps.Add(map); }
                        }
                    }
            }
            if (!gameBlocked) Log("game input blocked (" + inputOwner + "): cursor=" + cursorAdded + ", maps off=" + disabledMaps.Count);
            gameBlocked = true;
        }
        catch (Exception e) { if (blockErrors++ < 5) Log("block error: " + (e.InnerException ?? e)); gameBlocked = true; }
    }
    static void UnblockGame()
    {
        int n = disabledMaps.Count;
        foreach (object map in disabledMaps) try { map.GetType().GetProperty("enabled", All).SetValue(map, true, null); } catch { }
        disabledMaps.Clear();
        try
        {
            if (cursorAdded && !Dead(blockEs))
            {
                PropertyInfo p = blockEs.GetType().GetProperty("cursorOpenerCount", All);
                p.SetValue(blockEs, Math.Max(0, (int)p.GetValue(blockEs, null) - 1), null);
            }
            object lu = LocalUserObj(); object es = PGet(lu, "eventSystem");
            if (!Dead(es) && ReferenceEquals(PGet(es, "currentSelectedGameObject"), focusGo))
                es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) }).Invoke(es, new object[] { null });
        }
        catch (Exception e) { if (blockErrors++ < 5) Log("unblock error: " + (e.InnerException ?? e)); }
        cursorAdded = false; blockEs = null; gameBlocked = false;
        Log("game input restored (" + n + " maps back on)");
    }

    static bool rWas;
    static void WhipDummies()
    {
        if (info == null || info.dummies.Count == 0) return;
        bool d = Foreground() && (GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool edge = d && !rWas; rWas = d;
        if (!edge || inputOwner != null || ChatOpen()) return;
        CharacterBody b = Body(who);
        if (b == null || b.inputBank == null) return;
        Vector3 o = b.inputBank.aimOrigin, dir = b.inputBank.aimDirection;
        SandboxLook.Dummy best = null; float bd = 999f;
        foreach (var dm in info.dummies)
        {
            Vector3 to = dm.centre - b.corePosition; float dist = to.magnitude;
            Vector3 flat = dm.centre - o;
            float ang = Vector3.Angle(dir, flat);
            if (dist <= 12.5f && ang <= 35f && dist < bd) { bd = dist; best = dm; }
        }
        if (best == null) return;
        best.hits++; best.hitAt = Time.time;
        if (best.label != null) best.label.text = "WHACK! x" + best.hits;
        Log("dummy whipped at " + bd.ToString("0.0") + " m (hits " + best.hits + ")");
    }

    // ================================================================== note box (N): type, Enter = note, Shift+Enter = PASS, Ctrl+Enter = FAIL, Esc
    static string noteText = ""; static float noteOpenedAt; static bool noteFocusNext;
    static void OpenNote()
    {
        LoadQueue(false);
        if (curTest < 0 || curTest >= queue.Count) { Info("Start a feature test first (!test 5 or F6 + a number) - then N writes a note for it."); return; }
        noteText = ""; noteOpenedAt = Time.unscaledTime; noteFocusNext = true;
        AcquireInput("note");
        Log("note box opened for " + queue[curTest].id);
    }
    static void SubmitNote(string verdict)
    {
        string t = (noteText ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (t.Length == 0 && verdict == null) { noteFlash = "Type your note first (Esc = cancel)."; noteFlashUntil = Time.unscaledTime + 3f; return; }
        Note(HostName() ?? who, verdict, t);
        noteText = "";
        ReleaseInput("note saved", false);
        closeChatFrames = 6;
    }

    // ================================================================== on-screen panel (IMGUI)
    static GUIStyle style, box, rowBtn, field, small, btn;
    static int styleSize;
    static int kbId;
    static bool OtherLeftMenuVisible()
    {
        try
        {
            Type st = Newest("RodStand", "panelVisible"); object pv = SG(st, "panelVisible"); if (pv is bool && (bool)pv) return true;
            Type me = Newest("FishMerchant", "merchantPos"); Type mui = me == null ? null : me.Assembly.GetType("MerchantUI");
            object near = SG(mui, "near"), dis = SG(mui, "dismissed");
            if (near is bool && (bool)near && !(dis is bool && (bool)dis)) return true;
            Type v = Newest("FeatureVote", "autoPolls"); object va = SG(v, "active"); if (va is bool && (bool)va) return true;
        }
        catch { }
        return false;
    }
    static string VerdictTag(Result r, Feature f)
    {
        if (r == null) return f != null && f.qa == "needs-work" ? "<color=#ff9966>tracker: needs work</color>" : "<color=#888888>untested</color>";
        string tag = r.verdict == "pass" ? "<color=#66ff66>PASS</color>" : r.verdict == "fail" ? "<color=#ff6666>MORE WORK</color>" : "<color=#ffcc55>noted</color>";
        return tag + (string.IsNullOrEmpty(r.text) ? "" : " <color=#cccccc>\"" + Hs(Short(r.text, 40)) + "\"</color>");
    }
    static string Hs(string s) { return (s ?? "").Replace("<", "â€¹").Replace(">", "â€º"); }
    static string NoteTime(string ts) { DateTime d; return DateTime.TryParse(ts, Ci, DateTimeStyles.None, out d) ? d.ToString("HH:mm") : ""; }

    public static void OnGui()
    {
        try
        {
            if (!enabled) return;
            Event e = Event.current;
            if (resetKbControl) { resetKbControl = false; if (kbId != 0 && GUIUtility.keyboardControl == kbId) GUIUtility.keyboardControl = 0; kbId = 0; }
            if (inputOwner == "panel")
            {   // FeatureVote<N> ignores its number keys while IMGUI has a keyboard control
                if (kbId == 0) kbId = GUIUtility.GetControlID(FocusType.Keyboard);
                if (GUIUtility.keyboardControl == 0) GUIUtility.keyboardControl = kbId;
            }
            bool show = active || panelFocus || curTest >= 0 || noteOpen;
            if (!show) return;
            float s = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            int fs = (int)(14 * s);
            if (style == null || styleSize != fs)
            {
                styleSize = fs;
                style = new GUIStyle(GUI.skin.label) { richText = true, fontSize = fs, wordWrap = true };
                small = new GUIStyle(style) { fontSize = (int)(12 * s) };
                box = new GUIStyle(GUI.skin.box);
                rowBtn = new GUIStyle(GUI.skin.button) { richText = true, fontSize = fs, alignment = TextAnchor.MiddleLeft, wordWrap = false };
                field = new GUIStyle(GUI.skin.textField) { fontSize = (int)(16 * s) };
                btn = new GUIStyle(GUI.skin.button) { fontSize = fs, richText = true };
            }
            bool otherMenu = OtherLeftMenuVisible();
            if (!panelFocus && !noteOpen && otherMenu) { DrawStrip(s); }
            else DrawPanel(s, otherMenu);
            if (noteOpen) DrawNoteBox(e, s);
        }
        catch (Exception ex) { if (guiErrors++ < 3) Log("gui error: " + ex); }
    }

    // one line at the top left while the stall / merchant / vote card use the left side of the screen
    static void DrawStrip(float s)
    {
        string t = "<b><color=#7fd8ff>TEST SANDBOX</color></b>";
        if (curTest >= 0 && curTest < queue.Count) t += "  #" + (curTest + 1) + " " + Hs(queue[curTest].name) + "  " + checks.Count + "/" + queue[curTest].steps.Count;
        t += "  <color=#aaaaaa>F6 panel - N note</color>";
        var r = new Rect(12f, Screen.height * 0.12f, 560f * s, 26f * s);
        GUI.Box(r, "", box); GUI.Box(r, "", box);
        GUI.Label(new Rect(r.x + 8, r.y + 3, r.width - 12, r.height), t, style);
    }

    static void DrawPanel(float s, bool otherMenu)
    {
        float w = 540f * s, pad = 9f * s, inner = w - 2 * pad;
        float x = panelFocus && otherMenu ? 500f * s + 16f : 12f;
        var parts = new List<KeyValuePair<string, int>>();   // text, row index to start on click (-1 = plain line)
        string head = "<b><color=#7fd8ff>TEST SANDBOX</color></b>" + (active ? "" : " <color=#aaaaaa>(closed - notes still work)</color>");
        if (active && who != null)
        {
            var money = FG("money") as Dictionary<string, int>; int m = 0; if (money != null) money.TryGetValue(who, out m);
            head += "   wallet <color=#ffd700>" + Money(m) + "</color> <color=#aaaaaa>(temporary; the board shows your real " + Money(snap != null && snap.hadMoney ? snap.money : 0) + ")</color>";
        }
        parts.Add(new KeyValuePair<string, int>(head, -1));
        if (panelFocus || curTest < 0)
        {
            int pages = Math.Max(1, (queue.Count + 9) / 10); page = Mathf.Clamp(page, 0, pages - 1);
            parts.Add(new KeyValuePair<string, int>(panelFocus ? "<color=#ffd700>F6 focus: number key or click = start that test, PgUp/PgDn = page " + (page + 1) + "/" + pages + ", N = note, Esc = done</color>"
                                                                : "<color=#aaaaaa>F6 = pick a test (number keys / mouse)  -  page " + (page + 1) + "/" + pages + "</color>", -1));
            for (int r = 0; r < 10; r++)
            {
                int i = page * 10 + r; if (i >= queue.Count) break;
                Feature f = queue[i]; Result res; lastResult.TryGetValue(f.id, out res);
                string key = r == 9 ? "0" : (r + 1).ToString();
                string line = (i == curTest ? "<b>" : "") + "<color=#cccccc>" + key + "</color> #" + (i + 1) + " " + Hs(Short(f.name, 28)) + (f.where == "map" ? " <color=#aaaaaa>(map)</color>" : "") + (i == curTest ? "</b>" : "") + "  " + VerdictTag(res, f);
                parts.Add(new KeyValuePair<string, int>(line, panelFocus ? i : -1));
            }
            if (queue.Count == 0) parts.Add(new KeyValuePair<string, int>("<color=#ff8866>no test_queue.json</color>", -1));
        }
        if (curTest >= 0 && curTest < queue.Count)
        {
            Feature f = queue[curTest];
            parts.Add(new KeyValuePair<string, int>("<b>#" + (curTest + 1) + " " + Hs(f.name) + "</b> <color=#aaaaaa>(" + f.id + (f.where == "map" ? ", on the MAP" : "") + (f.qa == "needs-work" ? ", tracker: needs work" : "") + ")</color>", -1));
            for (int k = 0; k < f.steps.Count; k++)
            {
                bool ok = checks.Contains(k + 1);
                parts.Add(new KeyValuePair<string, int>((ok ? "<color=#66ff66>[x]</color> " : "[ ] ") + (k + 1) + ". " + (ok ? "<color=#aaaaaa>" + Hs(f.steps[k]) + "</color>" : Hs(f.steps[k])), -1));
            }
            List<Result> hist; history.TryGetValue(f.id, out hist);
            if (hist != null && hist.Count > 0)
            {
                parts.Add(new KeyValuePair<string, int>("<color=#aaaaaa>Notes so far:</color>", -1));
                for (int k = Math.Max(0, hist.Count - 3); k < hist.Count; k++)
                {
                    Result r = hist[k];
                    string v = r.verdict == "pass" ? "<color=#66ff66>PASS</color> " : r.verdict == "fail" ? "<color=#ff6666>MORE WORK</color> " : "";
                    parts.Add(new KeyValuePair<string, int>("  <color=#aaaaaa>" + NoteTime(r.ts) + "</color> " + v + Hs(r.text), -1));
                }
            }
            else parts.Add(new KeyValuePair<string, int>("<color=#888888>No note yet for this feature.</color>", -1));
            parts.Add(new KeyValuePair<string, int>("<color=#aaaaaa>N = write a note (Enter, Shift+Enter = PASS, Ctrl+Enter = FAIL)  -  !check 2  -  !test reset  -  !test next  -  F6 = list</color>", -1));
        }
        if (noteFlash != null && Time.unscaledTime < noteFlashUntil) parts.Add(new KeyValuePair<string, int>("<color=#66ff66>" + Hs(noteFlash) + "</color>", -1));
        // measure
        float h = pad * 2; var hs = new List<float>();
        foreach (var p in parts) { float ph = p.Value >= 0 ? style.CalcHeight(new GUIContent(p.Key), inner) + 4f * s : style.CalcHeight(new GUIContent(p.Key), inner); hs.Add(ph); h += ph; }
        float y = Screen.height * 0.2f; if (y + h > Screen.height - 12f) y = Mathf.Max(8f, Screen.height - 12f - h);
        var rct = new Rect(x, y, w, h);
        GUI.Box(rct, "", box); GUI.Box(rct, "", box); GUI.Box(rct, "", box);
        float cy = y + pad;
        for (int k = 0; k < parts.Count; k++)
        {
            var lr = new Rect(x + pad, cy, inner, hs[k]);
            if (parts[k].Value >= 0) { if (GUI.Button(lr, parts[k].Key, rowBtn)) { int i = parts[k].Value; Log("row clicked: #" + (i + 1)); StartTest(i, false); } }
            else GUI.Label(lr, parts[k].Key, style);
            cy += hs[k];
        }
    }

    static void DrawNoteBox(Event e, float s)
    {
        if (curTest < 0 || curTest >= queue.Count) { ReleaseInput("no test", false); return; }
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { e.Use(); Log("note box cancelled"); ReleaseInput("note cancelled", false); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.Use(); SubmitNote(e.control ? "fail" : e.shift ? "pass" : null); return; }
        }
        Feature f = queue[curTest];
        float w = 640f * s, h = 150f * s, pad = 12f * s;
        var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.62f, w, h);
        GUI.Box(r, "", box); GUI.Box(r, "", box); GUI.Box(r, "", box);
        GUI.Label(new Rect(r.x + pad, r.y + pad * 0.6f, w - 2 * pad, 24f * s), "<b><color=#7fd8ff>Write a note</color></b> for <b>#" + (curTest + 1) + " " + Hs(f.name) + "</b>", style);
        GUI.SetNextControlName("TestSandbox6Note");
        string before = noteText;
        noteText = GUI.TextField(new Rect(r.x + pad, r.y + 34f * s, w - 2 * pad, 32f * s), noteText ?? "", 500, field);
        if (Time.unscaledTime - noteOpenedAt < 0.4f && before.Length == 0 && (noteText == "n" || noteText == "N")) noteText = "";   // the N that opened the box
        if (noteFocusNext || GUI.GetNameOfFocusedControl() != "TestSandbox6Note") { GUI.FocusControl("TestSandbox6Note"); noteFocusNext = false; }
        float by = r.y + 74f * s, bw = (w - 2 * pad - 3 * 8f) / 4f, bh = 30f * s;
        if (GUI.Button(new Rect(r.x + pad, by, bw, bh), "Save note  <color=#aaaaaa>(Enter)</color>", btn)) SubmitNote(null);
        else if (GUI.Button(new Rect(r.x + pad + (bw + 8f), by, bw, bh), "<color=#66ff66>PASS</color>  <color=#aaaaaa>(Shift+Enter)</color>", btn)) SubmitNote("pass");
        else if (GUI.Button(new Rect(r.x + pad + 2 * (bw + 8f), by, bw, bh), "<color=#ff6666>FAIL</color>  <color=#aaaaaa>(Ctrl+Enter)</color>", btn)) SubmitNote("fail");
        else if (GUI.Button(new Rect(r.x + pad + 3 * (bw + 8f), by, bw, bh), "Cancel  <color=#aaaaaa>(Esc)</color>", btn)) { Log("note box cancelled (button)"); ReleaseInput("note cancelled", false); }
        GUI.Label(new Rect(r.x + pad, r.y + 110f * s, w - 2 * pad, 30f * s), "<color=#aaaaaa>Saved to test_results.jsonl for this feature (same as !note / !pass / !fail). " + (noteText ?? "").Length + "/500</color>", small);
    }
    static int guiErrors;
    static string Short(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "â€¦"; }

    // ================================================================== test hooks (lines in %TEMP%\ror2_sandbox_cmd.txt)
    //  probe | preview | destroy | test | shots <tag> [light] | where | openhost | close | fakechat <Name: text>
    //  preset <preset> | snapshot | restore | source <tag|off> | cmd <Name: text> (same as fakechat)
    static GameObject previewWater;
    static void FileCommands()
    {
        if (!File.Exists(CmdPath)) return;
        string[] cmds;
        try { cmds = File.ReadAllLines(CmdPath); File.Delete(CmdPath); } catch { return; }
        foreach (string raw in cmds)
        {
            string c = raw.Trim(); if (c.Length == 0) continue;
            Log("cmd: " + c);
            string[] a = c.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (a[0])
                {
                    case "probe":
                        {
                            Log("probe: Fishing " + Fish() + " (EventTick " + (Fish() != null && Fish().GetMethod("EventTick", All) != null) + ", Lightning " + (Fish() != null && Fish().GetMethod("Lightning", All) != null)
                                + ", anglers " + (SG(Fish(), "anglers") != null) + ", hooks " + (SG(Fish(), "hooks") != null) + ", rigs " + (SG(Fish(), "rigs") != null) + "), GuideBook " + Newest("GuideBook", null)
                                + ", Inventory " + Newest("Inventory", "slots") + ", Flashlight " + Newest("Flashlight", "key") + ", TourneyEnd " + Newest("TourneyEnd", "ended") + ", WaterLevel " + Newest("WaterLevel", null));
                            float y; bool lake = LakeY(out y);
                            Log("probe: scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ", lake " + (lake ? y.ToString("0.00") : "none") + ", fishing " + (Fish() == null ? "none" : Fish().Name)
                                + ", RodStand " + Newest("RodStand", "testPlaceAt") + ", merchant " + Newest("FishMerchant", "merchantPos") + ", island open " + IslandOpen() + ", DayNight " + Newest("DayNight", "hour")
                                + ", vote " + Newest("FeatureVote", "autoPolls") + ", pets " + Newest("DronePets", "pets") + ", inventory " + Newest("Inventory", "slots") + ", compass " + Newest("HotspotCompass", "mirror")
                                + ", active " + active + ", host " + HostName());
                            break;
                        }
                    case "preview":
                        {
                            if (active) { Log("preview refused: open"); break; }
                            float y; if (!LakeY(out y)) { Log("preview: no lake"); break; }
                            Vector3? o = FindOrigin(y); if (o == null) break;
                            if (info != null) SandboxLook.Destroy(info);
                            info = SandboxLook.Build(o.Value, WorldLayer()); lastOrigin = info.origin;
                            Log("preview built at " + info.origin.ToString("0.0") + " (" + info.boxes + " colliders, " + info.verts + " verts, " + info.buildMs.ToString("0") + " ms, shader " + SandboxLook.ShaderName + ")");
                            if (previewWater != null) UnityEngine.Object.Destroy(previewWater);
                            GameObject w = FG("water") as GameObject;
                            if (w != null)
                            {
                                previewWater = UnityEngine.Object.Instantiate(w); previewWater.name = "SandboxPreviewWater";
                                previewWater.transform.position = new Vector3(o.Value.x, w.transform.position.y, o.Value.z);
                                foreach (Collider col in previewWater.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(col);
                                SandboxLook.Ocean(info, previewWater);
                            }
                            break;
                        }
                    case "destroy":
                        if (active) { Log("destroy refused: open"); break; }
                        if (info != null) { SandboxLook.Destroy(info); info = null; }
                        if (previewWater != null) { UnityEngine.Object.Destroy(previewWater); previewWater = null; }
                        Log("destroyed"); break;
                    case "test": GeoTest(); break;
                    case "screen": { Directory.CreateDirectory(ShotDir); string sp = ShotDir + (a.Length > 1 ? a[1] : "screen") + ".png"; ScreenCapture.CaptureScreenshot(sp); Log("screen capture -> " + sp); break; }
                    case "focus": if (a.Length > 1 && a[1] == "off") ReleaseInput("file command", true); else AcquireInput("panel"); Log("panel focus " + panelFocus + ", owner " + (inputOwner ?? "-")); break;
                    case "notebox": OpenNote(); break;
                    case "notetext": noteText = c.Length > 9 ? c.Substring(9) : ""; Log("note text = " + noteText); break;
                    case "notesubmit": SubmitNote(a.Length > 1 ? (a[1] == "pass" ? "pass" : a[1] == "fail" ? "fail" : null) : null); break;
                    case "owner":
                        {   // test: who owns input + what the other add-ons see
                            var parts = new List<string>();
                            foreach (Type t in TypesNamed("ReqPanel")) { object v = SG(t, "open"); parts.Add(t.Assembly.GetName().Name + "." + t.Name + "=" + v); }
                            foreach (Type t in TypesNamed("RodStand")) { var kw = SG(t, "keyWas") as bool[]; if (kw != null) parts.Add(t.Name + ".keyWas[1]=" + kw[1]); }
                            Type me = Newest("FishMerchant", "merchantPos"); Type mui = me == null ? null : me.Assembly.GetType("MerchantUI");
                            parts.Add("MerchantUI.dismissed=" + SG(mui, "dismissed") + " near=" + SG(mui, "near"));
                            foreach (Type t in TypesNamed("Inventory")) if (t.GetField("key", All) != null) parts.Add(t.Name + ".key=" + SG(t, "key"));
                            foreach (Type t in TypesNamed("Flashlight")) if (t.GetField("key", All) != null) parts.Add(t.Name + ".key=" + SG(t, "key"));
                            Log("owner: " + (inputOwner ?? "-") + " panelFocus=" + panelFocus + " noteOpen=" + noteOpen + " gameBlocked=" + gameBlocked + " AppDomain=" + AppDomain.CurrentDomain.GetData("FT.InputOwner")
                                + " others=" + OthersOwnInput() + " | " + string.Join(", ", parts.ToArray()));
                            break;
                        }
                    case "goto": Log("goto -> " + GotoSpot(HostName(), a.Length > 1 ? a[1] : "spawn")); break;
                    case "verify": Verify("file command"); break;
                    case "cast":   // test: the host's F key (Fishing<N>.Press(name, viaChat:true)) - cast, or reel when it bites
                        { object an = Angler(HostName()); Log("cast: state before " + (an == null ? "-" : IG(an, "state")) + " -> " + SCall(Fish(), "Press", HostName(), true) + ", after " + (Angler(HostName()) == null ? "-" : IG(Angler(HostName()), "state"))); break; }
                    case "stagesim": if (active) StageChanged("simulated stage change"); else Log("stagesim: sandbox not open"); break;
                    case "crashsim":
                        {   // test: the game "dies" inside: forget everything in memory without restoring (the snapshot file stays), then
                            // run the crash recovery a freshly loaded TestSandbox<N> would run, and check the result
                            if (!active) { Log("crashsim: open the sandbox first"); break; }
                            Log("CRASHSIM: dropping the in-memory sandbox without a restore (snapshot file " + (File.Exists(SnapPath) ? "present" : "MISSING") + ")");
                            Snap keep = snap; Dictionary<string, string> hashes = fileHashes;
                            active = false; ReleaseInput("crashsim", true); WaterHome(); ShopsHome(); ZonesOn("crashsim"); PetLeashOn(); IslandsOn();
                            CharacterBody hb = Body(who); if (hb != null && haveReturn) TeleportHelper.TeleportBody(hb, returnPos);
                            recoverTries = 0; CrashRecovery();
                            // the in-memory-only parts (money, fish, lures, pet, time) are what a real crash loses anyway: put them back too
                            snap = keep; fileHashes = hashes; lastRestoreBad = RestoreAll("crashsim cleanup", true);
                            FinishClose();   // hotspots home, geometry gone, island back on, VERIFY
                            break;
                        }
                    case "endguard":
                        {   // test: what the !endtournament guard does, without ending anything
                            if (!active) { Log("endguard: open the sandbox first"); break; }
                            var money = FG("money") as Dictionary<string, int>; int before; money.TryGetValue(who, out before);
                            RealMoneyNow("endguard test"); int after; money.TryGetValue(who, out after);
                            Log("endguard: " + who + " money " + before + " -> " + after + " (snapshot " + (snap.hadMoney ? snap.money.ToString() : "none") + ") - the tournament would read " + after);
                            money[who] = before; Log("endguard: wallet put back (" + before + ")");
                            break;
                        }
                    case "eat": { object r = FCall("Eat", HostName(), c.Substring(4).Trim(), false); Log("eat -> " + r); break; }
                    case "merch":
                        {   // test: FishMerchant<N>.Act(host, args...) e.g. "merch sell Salmon 1" / "merch buy common"
                            Type me = Newest("FishMerchant", "merchantPos");
                            var args = new List<string>(a); args.RemoveAt(0);
                            object r = SCall(me, "Act", HostName(), args.ToArray(), false); Log("merch -> " + r); break;
                        }
                    case "killstray": foreach (GameObject g in UnityEngine.Object.FindObjectsOfType<GameObject>()) if (g.name == "SandboxPreviewWater" && g != previewWater) { UnityEngine.Object.Destroy(g); Log("stray SandboxPreviewWater destroyed at " + g.transform.position.ToString("0")); } break;
                    case "recovertest": { Snap rs = ReadSnapFile(); Log("recovertest: file read for " + rs.name + " (" + rs.owned.Count + " owned, " + rs.caseItems.Count + " case items, " + rs.gadgets.Count + " gadgets)"); int bad = RestorePersistent(rs); Log("recovertest: " + bad + " failed step(s)"); break; }
                    case "check": { int cn; if (a.Length > 1 && int.TryParse(a[1], out cn)) Check(cn); break; }
                    case "shots": Shots(a.Length > 1 ? a[1] : "v", Array.IndexOf(a, "light") > 0); break;
                    case "openhost": { string h = HostName(); if (h != null) { if (Open(h, true)) Log("openhost ok"); } break; }
                    case "close": if (active) Close("file command", true, false); break;
                    case "fakechat": case "cmd": Handle(c.Substring(a[0].Length).Trim()); break;
                    case "preset": if (active) Log("preset -> " + ApplyPreset(c.Substring(7).Trim())); break;
                    case "snapshot": { Snap s = Capture(a.Length > 1 ? c.Substring(9).Trim() : HostName()); Log("snapshot test " + (s != null ? "ok" : "FAILED")); break; }
                    case "source": noteSource = a.Length > 1 && a[1] != "off" ? a[1] : null; Log("note source = " + (noteSource ?? "(none)")); break;
                    case "fishcmd":
                        {   // run a Fishing<N> rod-shop chat command for the host directly (e.g. "fishcmd !buy carbon")
                            string h = HostName(); object r = FCall("RodCommand", h, c.Substring(8).Trim()); Log("fishcmd -> " + r); break;
                        }
                    case "where":
                        foreach (var nu in NetworkUser.readOnlyInstancesList)
                        {
                            CharacterBody b = Body(nu);
                            if (b == null) { Log("where: " + nu.userName + " has no body"); continue; }
                            Log("where: " + nu.userName + " foot " + b.footPosition.ToString("0.00") + " alive=" + b.healthComponent.alive + " grounded=" + (b.characterMotor != null && b.characterMotor.isGrounded)
                                + " inSandbox=" + (info != null && SandboxLook.Inside(info, b.footPosition)) + " local=" + nu.isLocalPlayer + " moving=" + (b.characterMotor != null ? b.characterMotor.velocity.magnitude.ToString("0.0") : "-"));
                        }
                        { float y; Log("where: lake " + (LakeY(out y) ? y.ToString("0.00") : "none") + " at " + (FG("water") is GameObject ? ((GameObject)FG("water")).transform.position.ToString("0.0") : "-") + ", hotspot " + FG("hotspotPos") + ", hot2 " + FG("hot2Pos") + ", zonesOff " + zonesOff.Count + ", island open " + IslandOpen()); }
                        break;
                }
            }
            catch (Exception e) { Log("cmd error: " + e); }
        }
    }

    static Camera SceneCam()
    {
        foreach (var c in CameraRigController.readOnlyInstancesList) if (c != null && c.sceneCam != null) return c.sceneCam;
        return Camera.main;
    }
    static void Shots(string tag, bool light)
    {
        if (info == null) { Log("shots: nothing built"); return; }
        Vector3 o = info.origin, up = Vector3.up;
        Shot(tag + "_overview", o + new Vector3(45f, 32f, -55f), o + new Vector3(0, 0, 8f), 55f, light);
        Shot(tag + "_spawn", info.spawn + up * 1.7f, info.spawn + new Vector3(0, 1.5f, 10f), 70f, light);
        Shot(tag + "_pool", o + new Vector3(-9f, 4.5f, 2f), info.poolCentre + up * 0.2f, 65f, light);
        Shot(tag + "_dock", info.dockEnd + new Vector3(6f, 3f, 8f), o + new Vector3(0, 2f, 20f), 65f, light);
        Shot(tag + "_shops", o + new Vector3(0f, 3f, -12f), o + new Vector3(-18f, 1.5f, -6f), 75f, light);
        Shot(tag + "_merchant", o + new Vector3(4f, 3f, -12f), o + new Vector3(18f, 1.5f, -6f), 75f, light);
        Shot(tag + "_underwater", info.poolCentre + new Vector3(0, -3f, -5.5f), info.poolCentre + new Vector3(0, -5f, 6f), 75f, light);
    }
    static void Shot(string name, Vector3 from, Vector3 at, float fov, bool light)
    {
        Directory.CreateDirectory(ShotDir);
        Camera sc = SceneCam();
        var go = new GameObject("SandboxShotCam"); GameObject lg = null;
        try
        {
            var cam = go.AddComponent<Camera>();
            if (sc != null) cam.CopyFrom(sc);
            go.transform.position = from; go.transform.LookAt(at);
            cam.fieldOfView = fov; cam.enabled = false; cam.farClipPlane = Mathf.Max(cam.farClipPlane, 1500f);
            if (light) { lg = new GameObject("SandboxShotLight"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.1f; l.color = new Color(1f, 0.96f, 0.88f); lg.transform.rotation = Quaternion.Euler(50f, 30f, 0f); }
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            string path = ShotDir + name + ".png";
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex); rt.Release(); UnityEngine.Object.Destroy(rt);
            Log("shot " + path);
        }
        finally { UnityEngine.Object.Destroy(go); if (lg != null) UnityEngine.Object.DestroyImmediate(lg); }
    }

    static GameObject probeBall; static float probeAt;
    static void GeoTest()
    {
        if (info == null) { Log("test: nothing built"); return; }
        int mask = LayerIndex.world.mask.value;
        Vector3 o = info.origin;
        float wy = o.y;
        string[] names = { "spawn", "deck SW", "deck NE", "pool centre", "pool step", "dock start", "dock end", "stall spot", "merchant spot", "sea 40 m E", "dummy W" };
        Vector3[] pts = { info.spawn, o + new Vector3(-25f, 0, -25f), o + new Vector3(25f, 0, 25f), info.poolCentre, o + new Vector3(0, 0, SandboxLook.PoolZ - SandboxLook.PoolHalf + 1.1f),
                          o + new Vector3(0, 0, SandboxLook.DockZ0 + 1f), info.dockEnd - new Vector3(0, 0, 1f), info.stall, info.merchant, o + new Vector3(70f, 0, 0), o + new Vector3(-6f, 0, -14f) };
        for (int i = 0; i < pts.Length; i++)
        {
            RaycastHit h; Vector3 p = pts[i];
            bool g = Physics.Raycast(new Vector3(p.x, wy + 60f, p.z), Vector3.down, out h, 120f, mask, QueryTriggerInteraction.Ignore);
            Log("test: " + names[i] + " -> " + (g ? "y " + h.point.y.ToString("0.00") + " (" + (h.point.y - wy).ToString("+0.00;-0.00") + " vs sea) on " + h.collider.name : "NO HIT")
                + "  openWater(fishing rule)=" + (!g || h.point.y <= wy - 0.2f));
        }
        bool blocked = Physics.CheckCapsule(info.spawn + Vector3.up * 0.7f, info.spawn + Vector3.up * 1.6f, 0.45f, mask, QueryTriggerInteraction.Ignore);
        Log("test: spawn capsule " + (blocked ? "BLOCKED" : "clear"));
        if (probeBall != null) UnityEngine.Object.Destroy(probeBall);
        probeBall = new GameObject("SandboxDropTest"); probeBall.transform.position = info.spawn + Vector3.up * 4f;
        var cc = probeBall.AddComponent<CapsuleCollider>(); cc.radius = 0.4f; cc.height = 1.8f;
        var rb = probeBall.AddComponent<Rigidbody>(); rb.mass = 80f;
        probeAt = Time.time;
        RoR2Application.onUpdate += DropCheck;
    }
    static void DropCheck()
    {
        if (probeBall == null) { RoR2Application.onUpdate -= DropCheck; return; }
        if (Time.time - probeAt < 3f) return;
        RoR2Application.onUpdate -= DropCheck;
        Log("test: drop capsule after 3 s at " + probeBall.transform.position.ToString("0.00") + " (spawn " + (info != null ? info.spawn.ToString("0.00") : "-") + ")");
        UnityEngine.Object.Destroy(probeBall); probeBall = null;
    }
}

