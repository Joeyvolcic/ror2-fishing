// TROPICAL ISLAND map for the Risk of Rain 2 fishing game.
// The island is generated at runtime from a seed (IslandLook.cs) far away from the current stage's playable area
// (2.5-5 km past the edge of its world geometry, at the lake's sea level) and everyone is moved there. The fishing lake
// (FishingWater) is slid sideways under the island, so fishing, swimming, the underwater view, hotspots, weather and
// day/night all keep working. The ROD SHOP stall (RodStand<N>) and the FISH MERCHANT (FishMerchant<N>) move onto the
// island's plaza; the purple hotspot goes 30 m past the end of the dock, the golden one off the far shore.
//
// Commands:  host (JV94)  !island         open the island and send everyone there
//                         !island leave   everyone back to where they were, island closes
//                         !island seed N  (before opening) another island layout
//            anyone       !island         (while it is open) go there yourself    !island leave  go back yourself
//
// Compiled twice from this one file (+ IslandLook.cs):
//  * HOST build (class Island<N>, references RoR2.dll): owns the island, broadcasts
//        [FT] ISLAND|<seed>|<x>|<y>|<z>|<invited names, comma separated>      (every 30 s with an empty list)
//        [FT] ISLANDOFF|<seed>
//    and teleports bodies server-side (TeleportHelper.TeleportBody). A guest is only teleported after their client
//    answers "[FT] ISLANDREADY|<seed>" (it has built the island and its colliders; guest movement is client-authoritative).
//  * GUEST build (-define:ISLAND_CLIENT, class IslandClient, inside FISHING.exe / plugin, reflection only for RoR2):
//    builds the same island from the ISLAND line, slides its own lake there, answers ISLANDREADY when invited.
// Out-of-bounds MapZones: leaving a "stay inside" zone teleports you back, so every MapZone collider is switched off for
// a moment around each trip to the island (host and the travelling guest) and switched back on once the body is there.
// Stage change while open: the island closes (it belongs to the old stage); the new stage starts normally.
// Tournament (Tourney10_*): map rotations are skipped while the island is open.
// Log %TEMP%\ror2_island.log. Test commands (host): lines in %TEMP%\ror2_island_cmd.txt (probe / preview [seed] /
// shots [tag] [light] / test / destroy / open / close / seed N).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
#if !ISLAND_CLIENT
using RoR2;
using UnityEngine.Networking;
#endif

#if ISLAND_CLIENT
public static class IslandClient
#else
public static class Island5
#endif
{
#if ISLAND_CLIENT
    const string Me = "IslandClient";
#else
    const string Me = "Island5";
#endif
    public static bool enabled = true;
    public static bool running;
    public static int version = 5;
    public const int DefaultSeed = 1;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_island.log");
    static int errors;
    public static IslandLook.Info info;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Me + ": " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
        catch { return new Type[0]; }
    }
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
    static bool Dead(object o) { return o == null || ((o is UnityEngine.Object) && (UnityEngine.Object)o == null); }
    static string ChatPlain(string raw) { return Regex.Replace(raw ?? "", "<[^>]*>", "").Trim(); }
    public static string CleanName(string s) { return (s ?? "").Replace("|", "/").Replace(",", ".").Replace(":", "."); }
    static int WorldLayer() { int l = LayerMask.NameToLayer("World"); return l >= 0 ? l : 11; }

    // ---------------------------------------------------------------- lake (slide it under the island and back)
    static readonly Dictionary<GameObject, Vector2> waterHome = new Dictionary<GameObject, Vector2>();
    static readonly string[] WaterNames = { "FishingWater", "FishingClientWater" };
    static void WaterToIsland()
    {
        if (info == null) return;
        foreach (string n in WaterNames)
        {
            GameObject w = GameObject.Find(n);
            if (w == null) continue;
            Vector3 p = w.transform.position;
            if (Mathf.Abs(p.x - info.origin.x) < 0.01f && Mathf.Abs(p.z - info.origin.z) < 0.01f) { IslandLook.Ocean(info, w); continue; }
            if (!waterHome.ContainsKey(w)) waterHome[w] = new Vector2(p.x, p.z);
            w.transform.position = new Vector3(info.origin.x, p.y, info.origin.z);
            Log("lake '" + n + "' moved under the island (was at " + p.x.ToString("0.0") + ", " + p.z.ToString("0.0") + ")");
            IslandLook.Ocean(info, w);
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

    // ---------------------------------------------------------------- out-of-bounds zones (off around a trip)
    static readonly List<Collider> zonesOff = new List<Collider>();
    static Type mapZoneType;
    static void ZonesOff(string why)
    {
        try
        {
            if (mapZoneType == null)
                foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("RoR2.MapZone"); if (t != null) { mapZoneType = t; break; } }
            if (mapZoneType == null) return;
            int n = 0;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(mapZoneType))
            {
                var c = ((Component)o).GetComponents<Collider>();
                foreach (Collider col in c) if (col != null && col.enabled) { col.enabled = false; zonesOff.Add(col); n++; }
            }
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

    static string IslandLine(string invited)
    {
        return "[FT] ISLAND|" + info.seed + "|" + info.origin.x.ToString("0.00", Ci) + "|" + info.origin.y.ToString("0.00", Ci) + "|" + info.origin.z.ToString("0.00", Ci) + "|" + invited;
    }
    static bool ParseLine(string p, out int seed, out Vector3 o, out string invited)
    {
        seed = 0; o = Vector3.zero; invited = "";
        string[] a = p.Substring("[FT] ISLAND|".Length).Split('|');
        if (a.Length < 4) return false;
        float x, y, z;
        if (!int.TryParse(a[0], NumberStyles.Integer, Ci, out seed) || !float.TryParse(a[1], NumberStyles.Float, Ci, out x)
            || !float.TryParse(a[2], NumberStyles.Float, Ci, out y) || !float.TryParse(a[3], NumberStyles.Float, Ci, out z)) return false;
        o = new Vector3(x, y, z); invited = a.Length > 4 ? a[4] : "";
        return true;
    }
    static void BuildAt(int seed, Vector3 o, string why)
    {
        if (info != null) IslandLook.Destroy(info);
        info = IslandLook.Build(seed, o, WorldLayer());
#if !ISLAND_CLIENT
        lastOrigin = o;
#endif
        Physics.SyncTransforms();
        Log("island built (" + why + "): seed " + seed + " at " + o.ToString("0.0") + ", " + info.verts + " verts / " + info.tris + " tris, " + info.trees + " palms, "
            + info.rocks + " rocks, " + info.bushes + " bushes, " + info.buildMs.ToString("0") + " ms, shaders " + IslandLook.ShaderName);
    }

#if ISLAND_CLIENT
    // =====================================================================================================
    // GUEST (inside FISHING.exe): reflection only for RoR2
    // =====================================================================================================
    static Assembly ror2;
    static Type chatType, nuType, chatBoxType;
    static Action fixedHook;
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine, lastScene;
    static bool chatDirty;
    static float zonesUntil = -1f, arrivedAt = -1f, nextSlow;

    public static void Run()
    {
        try
        {
            foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "RoR2") ror2 = a;
            Assembly mine = MethodBase.GetCurrentMethod().DeclaringType.Assembly;
            foreach (var asm in FishingScope.Assemblies())
            {
                if (asm == mine) continue;
                foreach (Type t in SafeTypes(asm))
                {
                    int hn;
                    bool hostCopy = t.Name.StartsWith("Island") && int.TryParse(t.Name.Substring(6), out hn);
                    if (!hostCopy && t.Name != Me) continue;
                    FieldInfo r = t.GetField("running", BindingFlags.Public | BindingFlags.Static), en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    FieldInfo vf = t.GetField("version", BindingFlags.Public | BindingFlags.Static);
                    bool live = r != null && en != null && (bool)r.GetValue(null) && (bool)en.GetValue(null);
                    if (hostCopy && live) { Log("host detected (" + t.Name + " is running) - guest island not started"); return; }
                    if (!hostCopy)
                    {
                        int ov = vf != null ? Convert.ToInt32(vf.GetValue(null)) : 0;
                        if (live && ov >= version) { Log("already running - nothing changed"); return; }
                        if (en != null) en.SetValue(null, false);
                        Log("disabled older " + t.FullName + " v" + ov);
                    }
                }
            }
            chatType = ror2.GetType("RoR2.Chat");
            nuType = ror2.GetType("RoR2.NetworkUser");
            chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc != null && cc.EventHandlerType == typeof(Action)) { Action a = OnChatChanged; cc.AddEventHandler(null, a); }
            fixedHook = FirstTick;
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, fixedHook);
            running = true;
            Log("installed v" + version + " (chatHook=" + (cc != null) + ")");
        }
        catch (Exception e) { Log("install error: " + e); }
    }

    static GameObject driver;
    static void FirstTick()
    {
        try
        {
            ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).RemoveEventHandler(null, fixedHook);
            if (driver != null) return;
            driver = new GameObject("IslandClientDriver");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            driver.AddComponent<IslandClientBehaviour>();
            lastScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Log("main-thread driver created");
        }
        catch (Exception e) { Log("first tick error: " + e); }
    }

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
            string p = ChatPlain(line);
            if (p.StartsWith("[FT] ISLAND")) { lock (chatQueue) chatQueue.Add(p); chatDirty = true; }
            else if (p.Contains(": [FT] ISLANDREADY|")) chatDirty = true;
        }
        catch { }
    }

    static void Drop(string why)
    {
        if (info != null) { IslandLook.Destroy(info); info = null; Log("island removed: " + why); }
        WaterHome(); ZonesOn(why); zonesUntil = -1f; arrivedAt = -1f;
    }

    public static void Update()
    {
        if (!enabled) { if (info != null) Drop("disabled"); return; }
        try
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != lastScene) { lastScene = scene; waterHome.Clear(); zonesOff.Clear(); if (info != null) { IslandLook.Destroy(info); info = null; Log("island removed: scene " + scene); } }
            string[] lines = null;
            lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
            if (lines != null) foreach (string l in lines) Handle(l);
            if (chatDirty) HideTaggedLines();
            if (info == null) return;
            IslandLook.Animate(info, Time.time);
            if (Time.time >= nextSlow)
            {
                nextSlow = Time.time + 0.5f;
                WaterToIsland();
                if (zonesOff.Count > 0)
                {
                    Vector3 foot; bool there = LocalFoot(out foot) && IslandLook.OnIsland(info, foot);
                    if (there && arrivedAt < 0f) { arrivedAt = Time.time; Log("arrived on the island at " + foot.ToString("0.0")); }
                    if ((arrivedAt >= 0f && Time.time - arrivedAt > 2f) || Time.time > zonesUntil) { ZonesOn(arrivedAt >= 0f ? "on the island" : "trip timed out"); arrivedAt = -1f; }
                }
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("update error: " + e); }
    }

    static void Handle(string p)
    {
        try
        {
            if (p.StartsWith("[FT] ISLANDOFF")) { Drop("host closed it"); return; }
            if (!p.StartsWith("[FT] ISLAND|")) return;
            int seed; Vector3 o; string invited;
            if (!ParseLine(p, out seed, out o, out invited)) { Log("bad line: " + p); return; }
            if (info == null || info.root == null || info.seed != seed || (info.origin - o).sqrMagnitude > 0.01f) BuildAt(seed, o, "host line");
            WaterToIsland();
            string me = LocalName();
            if (me != null && invited.Length > 0 && Array.IndexOf(invited.Split(','), CleanName(me)) >= 0)
            {
                ZonesOff("invited to the island"); zonesUntil = Time.time + 15f; arrivedAt = -1f;
                Say("[FT] ISLANDREADY|" + seed);
                Log("ready for the island trip");
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("line error: " + e); }
    }

    static object LocalUser()
    {
        try { return ror2.GetType("RoR2.LocalUserManager").GetMethod("GetFirstLocalUser", All).Invoke(null, null); } catch { return null; }
    }
    static bool LocalFoot(out Vector3 foot)
    {
        foot = Vector3.zero;
        try
        {
            object b = Get(LocalUser(), "cachedBody");
            if (Dead(b)) return false;
            foot = (Vector3)Get(b, "footPosition"); return true;
        }
        catch { return false; }
    }
    static string LocalName()
    {
        try { object nu = Get(LocalUser(), "currentNetworkUser"); return Dead(nu) ? null : Convert.ToString(Get(nu, "userName")); }
        catch { return null; }
    }
    static void Say(string msg)
    {
        try
        {
            object nu = Get(LocalUser(), "currentNetworkUser");
            if (Dead(nu)) { Log("say failed: no local network user"); return; }
            string esc = msg.Replace("\\", "\\\\").Replace("\"", "\\\"");
            object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
            foreach (MethodInfo m in console.GetType().GetMethods(All))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "SubmitCmd" && ps.Length == 3 && ps[0].ParameterType == nuType)
                { m.Invoke(console, new object[] { nu, "say \"" + esc + "\"", false }); return; }
            }
            Log("say failed: SubmitCmd not found");
        }
        catch (Exception e) { Log("say error: " + (e.InnerException ?? e).Message); }
    }
    static void HideTaggedLines()
    {
        chatDirty = false;
        try
        {
            List<string> log = GetStatic(chatType, "log") as List<string>;
            if (log == null) return;
            int removed = 0;
            for (int i = log.Count - 1; i >= 0; i--)
            {
                string p = ChatPlain(log[i]);
                if (p.StartsWith("[FT] ISLAND") || p.Contains(": [FT] ISLANDREADY|")) { log.RemoveAt(i); removed++; }
            }
            if (removed == 0 || chatBoxType == null) return;
            foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(chatBoxType))
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
        }
        catch { }
    }
#else
    // =====================================================================================================
    // HOST (JV94's game)
    // =====================================================================================================
    public static bool active;                 // island open: lake moved, shops moved, people travelling
    public static int seed = DefaultSeed;
    public static string ShotDir = FishingPaths.Debug("island_shots");
    static readonly string CmdPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_island_cmd.txt");
    static readonly Dictionary<string, Vector3> returnPos = new Dictionary<string, Vector3>();
    static readonly Dictionary<string, float> pending = new Dictionary<string, float>();      // guest -> deadline for ISLANDREADY
    static readonly Dictionary<string, float> travelAt = new Dictionary<string, float>();     // name -> time to teleport
    static readonly List<string> chatQueue = new List<string>();
    static readonly Dictionary<string, float> cooldown = new Dictionary<string, float>();
    static string lastChatLine;
    static Stage lastStage;
    static float nextSlow, nextLine, nextCmd, hotAt = -1f, zonesOnAt = -1f, closeAt = -1f;
    static int spawnIdx;
    static GameObject previewWater;
    static bool preview;                       // built for inspection only (nothing moved)

    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }
    static void Info(string text) { Say("<color=#5fe0c8>" + text + "</color>"); }

    public static void Run()
    {
        Type mine = MethodBase.GetCurrentMethod().DeclaringType;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t == mine || !t.Name.StartsWith("Island") || !int.TryParse(t.Name.Substring(6), out n)) continue;
                FieldInfo f = t.GetField("enabled", All);
                if (f != null) f.SetValue(null, false);
                MethodInfo c = t.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static);
                if (c != null) RoR2Application.onNextUpdate += () => { try { c.Invoke(null, null); } catch { } };
                Log("disabled older " + t.Name);
            }
        Chat.onChatChanged += OnChatChanged;
        RoR2Application.onUpdate += Frame;
        running = true;
        Log("host installed v" + version + " (!island, !island leave, !island seed N)");
    }

    // a newer Island<N> took over: put everything back (main thread)
    public static void Cleanup()
    {
        try
        {
            if (active) CloseNow("replaced by a newer version", true);
            if (info != null) { IslandLook.Destroy(info); info = null; }
            if (previewWater != null) UnityEngine.Object.Destroy(previewWater);
            ZonesOn("cleanup");
        }
        catch (Exception e) { Log("cleanup error: " + e.Message); }
    }

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
            if (p.Contains(": !island") || p.Contains(": [FT] ISLANDREADY|")) lock (chatQueue) chatQueue.Add(p);
        }
        catch { }
    }

    // ---------------------------------------------------------------- newest Fishing<N> (lake + hotspots)
    static Type fish; static float fishAt = -100f;
    static Type Newest(string prefix, string needField)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int v;
                if (!t.Name.StartsWith(prefix) || !int.TryParse(t.Name.Substring(prefix.Length), out v) || v <= bn) continue;
                if (needField != null && t.GetField(needField, All) == null) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                best = t; bn = v;
            }
        return best;
    }
    static Type Fish()
    {
        if (fish == null || Time.time - fishAt > 10f)
        {
            fishAt = Time.time;
            Type t = Newest("Fishing", "waterY");
            if (t != fish) Log("fishing mod is " + (t == null ? "none" : t.Name));
            fish = t;
        }
        return fish;
    }
    static object FG(string f) { Type t = Fish(); if (t == null) return null; FieldInfo fi = t.GetField(f, All); return fi == null ? null : fi.GetValue(null); }
    static void FS(string f, object v) { Type t = Fish(); if (t == null) return; FieldInfo fi = t.GetField(f, All); if (fi != null) fi.SetValue(null, v); }
    static void FCall(string m, params object[] args)
    {
        Type t = Fish(); if (t == null) return;
        foreach (MethodInfo mi in t.GetMethods(All))
            if (mi.Name == m && mi.GetParameters().Length == args.Length) { try { mi.Invoke(null, args); } catch (Exception e) { Log(m + " error: " + (e.InnerException ?? e).Message); } return; }
        Log("method " + m + " not found on " + t.Name);
    }
    static bool LakeY(out float y)
    {
        y = 0f;
        object w = FG("water"), hy = FG("waterY"), hs = FG("hasWater"), sc = FG("waterScene");
        if (Dead(w) || !(hy is float) || (hs is bool && !(bool)hs) || Convert.ToString(sc) != UnityEngine.SceneManagement.SceneManager.GetActiveScene().name) return false;
        y = (float)hy; return true;
    }

    // ---------------------------------------------------------------- main loop
    static void Frame()
    {
        if (!enabled) return;
        try
        {
            if (Stage.instance != lastStage)
            {
                lastStage = Stage.instance;
                if (active || info != null) StageChanged();
            }
            string[] lines = null;
            lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
            if (lines != null) foreach (string l in lines) Handle(l);
            if (Time.unscaledTime >= nextCmd) { nextCmd = Time.unscaledTime + 1f; FileCommands(); }
            if (info != null) IslandLook.Animate(info, Time.time);
            if (!NetworkServer.active) return;

            // teleports due now
            if (travelAt.Count > 0)
                foreach (var kv in new List<KeyValuePair<string, float>>(travelAt))
                    if (Time.time >= kv.Value) { travelAt.Remove(kv.Key); NetworkUser nu = User(kv.Key); if (nu != null) Travel(nu); }
            if (zonesOnAt > 0f && Time.time >= zonesOnAt) { zonesOnAt = -1f; ZonesOn("travellers arrived"); }
            if (closeAt > 0f && Time.time >= closeAt) { closeAt = -1f; CloseNow("everyone left", false); }
            if (!active || Time.time < nextSlow) return;
            nextSlow = Time.time + 0.5f;

            foreach (var kv in new List<KeyValuePair<string, float>>(pending))
                if (Time.time > kv.Value)
                {
                    pending.Remove(kv.Key);
                    Info(kv.Key + ": your FISHING.exe can't build the island yet - restart it after it updates, then type !island.");
                    Log("trip " + kv.Key + " timed out (no ISLANDREADY)");
                }
            WaterToIsland();
            if (hotAt > 0f && Time.time >= hotAt) { hotAt = -1f; HotspotsToIsland(); }
            PauseTourney();
            if (Time.time >= nextLine) { nextLine = Time.time + 30f; Say(IslandLine("")); }
        }
        catch (Exception e) { if (errors++ < 30) Log("frame error: " + e); }
    }

    static NetworkUser User(string name)
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu.userName == name) return nu;
        return null;
    }
    static string HostName()
    {
        try { var lu = LocalUserManager.GetFirstLocalUser(); if (lu != null && lu.currentNetworkUser != null) return lu.currentNetworkUser.userName; } catch { }
        return null;
    }
    static CharacterBody Body(NetworkUser nu) { return nu == null || nu.master == null ? null : nu.master.GetBody(); }

    static void Handle(string p)
    {
        int i = p.IndexOf(": ");
        if (i <= 0) return;
        string name = p.Substring(0, i).Trim(), text = p.Substring(i + 2).Trim();
        NetworkUser nu = User(name);
        if (nu == null) return;
        if (text.StartsWith("[FT] ISLANDREADY|"))
        {
            int s;
            if (pending.ContainsKey(name) && int.TryParse(text.Substring(17), NumberStyles.Integer, Ci, out s) && info != null && s == info.seed)
            { pending.Remove(name); ZonesOff(name + " travelling"); zonesOnAt = Time.time + 3f; travelAt[name] = Time.time + 0.1f; Log(name + " is ready"); }
            return;
        }
        string low = text.ToLowerInvariant();
        string[] a = low.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0 || a[0] != "!island") return;
        float cd;
        if (cooldown.TryGetValue(name, out cd) && Time.time < cd) return;
        cooldown[name] = Time.time + 1.5f;
        Log(name + ": " + text);
        bool host = name == HostName();
        string sub = a.Length > 1 ? a[1] : "";
        if (sub == "leave" || sub == "back" || sub == "off" || sub == "close")
        {
            if (!active) { Info("The island isn't open."); return; }
            if (host) CloseAll(name + " closed it");
            else GoBack(nu, true);
            return;
        }
        if (sub == "seed")
        {
            int s;
            if (!host) return;
            if (a.Length > 2 && int.TryParse(a[2], out s)) { seed = s; Info("Island layout #" + s + " - it is used the next time the island opens."); }
            else Info("Island layout #" + seed);
            return;
        }
        if (sub == "help" || sub == "?") { Info("!island = sail to the Tropical Island (host opens it for everyone), !island leave = back to the map."); return; }
        if (!active)
        {
            if (!host) { Info("Only the host can open the Tropical Island - ask them to type !island."); return; }
            if (!Open(name)) return;
            InviteAll();
        }
        else if (host) InviteAll();
        else Invite(nu);
    }

    // ---------------------------------------------------------------- open / close
    static bool Open(string by)
    {
        if (!NetworkServer.active) return false;
        float seaY;
        if (!LakeY(out seaY)) { Info("The lake isn't ready yet on this stage - try again in a few seconds."); return false; }
        if (info == null || info.root == null || info.seed != seed || Mathf.Abs(info.origin.y - seaY) > 0.05f)
        {
            Vector3? o = FindOrigin(seaY);
            if (o == null) { Info("No room for the island on this stage, sorry!"); return false; }
            Info("Building the Tropical Island...");
            BuildAt(seed, o.Value, "opened by " + by);
        }
        if (previewWater != null) { UnityEngine.Object.Destroy(previewWater); previewWater = null; }
        preview = false;
        active = true; spawnIdx = 0; nextLine = Time.time + 30f;
        WaterToIsland();
        ShopsToIsland();
        Log("island OPEN at " + info.origin.ToString("0.0") + " (sea Y " + seaY.ToString("0.00") + ") by " + by);
        return true;
    }

    static void InviteAll()
    {
        var names = new List<string>();
        foreach (var nu in NetworkUser.readOnlyInstancesList)
        {
            CharacterBody b = Body(nu);
            if (b == null || !b.healthComponent.alive || IslandLook.OnIsland(info, b.footPosition)) continue;
            names.Add(nu.userName);
        }
        Say(IslandLine(string.Join(",", names.ConvertAll<string>(CleanName).ToArray())));
        Info("All aboard for the TROPICAL ISLAND! (dock, rod shop + fish merchant on the beach, hotspot past the end of the dock - !island leave to go back)");
        foreach (string n in names) Invite(User(n), false);
    }
    static void Invite(NetworkUser nu, bool announce = true)
    {
        if (nu == null || info == null) return;
        CharacterBody b = Body(nu);
        if (b == null || !b.healthComponent.alive) { Info(nu.userName + ": you need to be alive to travel."); return; }
        if (IslandLook.OnIsland(info, b.footPosition)) { Info(nu.userName + " is already on the island."); return; }
        if (nu.isLocalPlayer) { ZonesOff(nu.userName + " travelling"); zonesOnAt = Time.time + 3f; travelAt[nu.userName] = Time.time + 0.25f; return; }
        if (announce) Say(IslandLine(CleanName(nu.userName)));
        pending[nu.userName] = Time.time + 10f;
    }

    static void Travel(NetworkUser nu)
    {
        if (!active || info == null) return;
        CharacterBody b = Body(nu);
        if (b == null || !b.healthComponent.alive) return;
        if (!IslandLook.OnIsland(info, b.footPosition)) returnPos[nu.userName] = b.footPosition;
        Vector3 target = info.spawns[spawnIdx++ % info.spawns.Count];
        TeleportHelper.TeleportBody(b, target);
        Info(nu.userName + " arrived on the Tropical Island!");
        Log(nu.userName + " -> island " + target.ToString("0.0") + " (return " + (returnPos.ContainsKey(nu.userName) ? returnPos[nu.userName].ToString("0.0") : "-") + ")");
        if (hotAt < 0f && !hotsDone) hotAt = Time.time + 1.5f;
    }

    static void GoBack(NetworkUser nu, bool closeIfEmpty)
    {
        CharacterBody b = Body(nu);
        if (b == null || !b.healthComponent.alive) { returnPos.Remove(nu.userName); return; }
        if (!IslandLook.OnIsland(info, b.footPosition)) { returnPos.Remove(nu.userName); return; }
        Vector3 back;
        if (!returnPos.TryGetValue(nu.userName, out back))
        {
            bool found = false;
            foreach (var other in NetworkUser.readOnlyInstancesList)
            {
                CharacterBody ob = Body(other);
                if (ob == null || ob == b || !ob.healthComponent.alive || IslandLook.OnIsland(info, ob.footPosition)) continue;
                back = ob.footPosition + new Vector3(1.5f, 0.5f, 0); found = true; break;
            }
            if (!found)
            {
                var sps = SpawnPoint.readOnlyInstancesList;
                if (sps.Count == 0) { Info(nu.userName + ": no way back found, sorry!"); return; }
                back = sps[0].transform.position;
            }
        }
        returnPos.Remove(nu.userName);
        TeleportHelper.TeleportBody(b, back);
        Log(nu.userName + " back to " + back.ToString("0.0"));
        if (closeIfEmpty)
        {
            Info(nu.userName + " sailed back to the map.");
            bool anyone = false;
            foreach (var o in NetworkUser.readOnlyInstancesList) { CharacterBody ob = Body(o); if (ob != null && ob != b && ob.healthComponent.alive && IslandLook.OnIsland(info, ob.footPosition)) anyone = true; }
            if (!anyone) { closeAt = Time.time + 2.5f; Log("nobody left on the island - closing"); }
        }
    }

    static void CloseAll(string why)
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList) GoBack(nu, false);
        pending.Clear(); travelAt.Clear();
        Info("Everyone sailed back from the Tropical Island.");
        closeAt = Time.time + 2.5f;
        Log("closing: " + why);
    }

    static void CloseNow(string why, bool teleport)
    {
        if (teleport) foreach (var nu in NetworkUser.readOnlyInstancesList) GoBack(nu, false);
        int s = info != null ? info.seed : seed;
        active = false; closeAt = -1f; hotAt = -1f; hotsDone = false;
        pending.Clear(); travelAt.Clear(); returnPos.Clear();
        WaterHome();
        ShopsHome();
        HotspotsHome();
        PruneDrySpots();
        ZonesOn("island closed");
        if (info != null) { IslandLook.Destroy(info); info = null; }
        try { Say("[FT] ISLANDOFF|" + s); } catch { }
        Log("island CLOSED (" + why + ")");
    }

    static void StageChanged()
    {
        Log("stage changed while the island was " + (active ? "open" : "built (preview)") + " - closing it");
        bool wasActive = active;
        active = false; preview = false; closeAt = -1f; hotAt = -1f; hotsDone = false;
        pending.Clear(); travelAt.Clear(); returnPos.Clear(); waterHome.Clear(); zonesOff.Clear(); shopsSaved = false;
        if (info != null) { IslandLook.Destroy(info); info = null; }
        previewWater = null;
        if (wasActive) { try { Say("[FT] ISLANDOFF|" + seed); Info("The Tropical Island trip ended with the new stage."); } catch { } }
    }

    // ---------------------------------------------------------------- where: far outside the stage, at sea level
    static Vector3? FindOrigin(float seaY)
    {
        // everything the stage draws (sky domes / backdrops > 6 km skipped, lake sheets skipped) + its world colliders
        Bounds wb = new Bounds(); bool any = false; int n = 0;
        Renderer[] rends = UnityEngine.Object.FindObjectsOfType<Renderer>();
        foreach (Renderer r in rends)
        {
            if (r == null || !r.enabled) continue;
            if (info != null && info.root != null && r.transform.IsChildOf(info.root.transform)) continue;
            string rn = r.gameObject.name;
            if (rn == "FishingWater" || rn == "FishingClientWater" || rn == "IslandPreviewWater") continue;
            Bounds rb = r.bounds;
            if (rb.size.magnitude > 6000f) continue;
            if (!any) { wb = rb; any = true; } else wb.Encapsulate(rb);
            n++;
        }
        int world = LayerIndex.world.intVal;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger || !c.enabled || c.gameObject.layer != world) continue;
            if (info != null && info.root != null && c.transform.IsChildOf(info.root.transform)) continue;
            Bounds cb = c.bounds;
            if (cb.size.magnitude > 6000f) continue;
            if (!any) { wb = cb; any = true; } else wb.Encapsulate(cb);
        }
        if (!any) wb = new Bounds(Vector3.zero, Vector3.one * 1000f);
        // the island square (+walls) must stay > 4.3 km from anything the stage draws: the scene camera's far clip is 4 km
        float ext = new Vector2(wb.extents.x, wb.extents.z).magnitude;
        float half = IslandLook.WallHalf + 40f;
        foreach (float extra in new[] { 4300f, 5500f, 7000f })
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f + 0.3f, D = ext + half * 1.42f + extra;
                Vector3 o = new Vector3(Mathf.Round(wb.center.x + Mathf.Cos(a) * D), seaY, Mathf.Round(wb.center.z + Mathf.Sin(a) * D));
                if (Physics.CheckBox(new Vector3(o.x, seaY + 40f, o.z), new Vector3(half, 100f, half), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)) continue;
                var box = new Bounds(new Vector3(o.x, seaY + 40f, o.z), new Vector3(half * 2f, 200f, half * 2f));
                bool clash = false;
                foreach (Renderer r in rends) { if (r != null && r.enabled && r.bounds.size.magnitude <= 6000f && r.bounds.Intersects(box)) { clash = true; break; } }
                if (clash) continue;
                Log("island spot " + o.ToString("0") + " (" + D.ToString("0") + " m from the stage centre " + wb.center.ToString("0") + ", stage bounds " + wb.size.ToString("0") + " from " + n + " renderers)");
                return o;
            }
        Log("no clear island spot found around " + wb.center.ToString("0"));
        return null;
    }
    // ---------------------------------------------------------------- hotspots
    static bool hotsDone;
    static void HotspotsToIsland()
    {
        float y; if (!LakeY(out y) || info == null) return;
        FS("hotspotPos", new Vector3(info.hotspot.x, y, info.hotspot.z));
        FS("hasHotspot", true); FS("hotFarSince", -1f);
        FCall("BroadcastHot");
        object now = FG("now");
        FS("hot2Pos", new Vector3(info.hot2.x, y, info.hot2.z));
        FS("hasHot2", true);
        if (now is float) FS("hot2MovedAt", (float)now);
        FCall("BroadcastHot2");
        hotsDone = true;
        Log("hotspots moved: purple " + info.hotspot.ToString("0.0") + ", golden " + info.hot2.ToString("0.0"));
    }
    static Vector3 lastOrigin = new Vector3(float.NaN, 0, 0);
    static bool InIslandArea(Vector3 p)
    {
        if (float.IsNaN(lastOrigin.x)) return false;
        return Mathf.Abs(p.x - lastOrigin.x) < IslandLook.WallHalf + 400f && Mathf.Abs(p.z - lastOrigin.z) < IslandLook.WallHalf + 400f;
    }
    // hotspots still out at the island -> back near the players (purple first: the golden one is placed relative to it)
    static void HotspotsHome()
    {
        object hp = FG("hotspotPos");
        if (hp is Vector3 && InIslandArea((Vector3)hp)) { FCall("PlaceHotspot", true); Log("purple hotspot re-placed on the map -> " + FG("hotspotPos")); }
        object h2 = FG("hot2Pos");
        if (h2 is Vector3 && InIslandArea((Vector3)h2))
        {
            FCall("PlaceHot2", true, "island closed");
            object n2 = FG("hot2Pos");
            if (n2 is Vector3 && InIslandArea((Vector3)n2)) { FS("hasHot2", false); Log("golden hotspot: no spot right now - Fishing will place it again by itself"); }
            else Log("golden hotspot re-placed on the map -> " + n2);
        }
        hotsDone = false;
    }
    // ---------------------------------------------------------------- rod shop + fish merchant
    static bool shopsSaved, standWas, merchWas;
    static Vector3 standPos0, merchPos0; static float standYaw0, merchYaw0;
    static void ShopsToIsland()
    {
        try
        {
            Type st = Newest("RodStand", "testPlaceAt"), me = Newest("FishMerchant", "merchantPos");
            if (!shopsSaved)
            {
                shopsSaved = true;
                if (st != null) { standWas = (bool)st.GetField("placed", All).GetValue(null); standPos0 = (Vector3)st.GetField("standPos", All).GetValue(null); standYaw0 = (float)st.GetField("standYaw", All).GetValue(null); }
                if (me != null) { merchWas = (bool)me.GetField("placed", All).GetValue(null); merchPos0 = (Vector3)me.GetField("merchantPos", All).GetValue(null); merchYaw0 = (float)me.GetField("merchantYaw", All).GetValue(null); }
            }
            if (st != null)
            {
                st.GetField("testPlaceAt", All).SetValue(null, info.stall.x.ToString("0.00", Ci) + "|" + info.stall.y.ToString("0.00", Ci) + "|" + info.stall.z.ToString("0.00", Ci) + "|" + info.stallYaw.ToString("0.0", Ci));
                Log("rod shop -> " + info.stall.ToString("0.0") + " (" + st.Name + ")");
            }
            if (me != null) MoveMerchant(me, info.merchant, info.merchantYaw, true);
        }
        catch (Exception e) { Log("shops error: " + e); }
    }
    static void MoveMerchant(Type me, Vector3 pos, float yaw, bool placed)
    {
        me.GetField("merchantPos", All).SetValue(null, pos);
        me.GetField("merchantYaw", All).SetValue(null, yaw);
        Type look = me.Assembly.GetType("MerchantLook");
        if (look != null) { MethodInfo d = look.GetMethod("Destroy", All); if (d != null) d.Invoke(null, null); }
        me.GetField("placed", All).SetValue(null, placed);
        FieldInfo na = me.GetField("nextAnnounce", All); if (na != null) na.SetValue(null, 0f);
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
                if (standWas) st.GetField("testPlaceAt", All).SetValue(null, standPos0.x.ToString("0.00", Ci) + "|" + standPos0.y.ToString("0.00", Ci) + "|" + standPos0.z.ToString("0.00", Ci) + "|" + standYaw0.ToString("0.0", Ci));
                else st.GetField("placed", All).SetValue(null, false);
                Log("rod shop back to " + (standWas ? standPos0.ToString("0.0") : "(auto placement)"));
            }
            if (me != null) MoveMerchant(me, merchPos0, merchYaw0, merchWas);
        }
        catch (Exception e) { Log("shops home error: " + e); }
    }

    // ---------------------------------------------------------------- tournament: no map rotation while on the island
    static void PauseTourney()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            string an = a.GetName().Name;
            if (!an.StartsWith("Tourney") && !FishingScope.IsMod(a)) continue;
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("Tourney")) continue;
                FieldInfo en = t.GetField("enabled", All), rd = t.GetField("rotationsDone", All), sa = t.GetField("startedAt", All), stf = t.GetField("started", All), fi = t.GetField("finished", All);
                if (en == null || rd == null || sa == null || stf == null) continue;
                if (!(bool)en.GetValue(null) || !(bool)stf.GetValue(null) || (fi != null && (bool)fi.GetValue(null))) continue;
                double el = (DateTime.UtcNow - (DateTime)sa.GetValue(null)).TotalSeconds;
                FieldInfo rs = t.GetField("RotateSeconds", All);
                double every = rs != null ? Convert.ToDouble(rs.GetValue(null)) : 120.0;
                int need = (int)(el / every), done = (int)rd.GetValue(null);
                if (done < need) { rd.SetValue(null, need); Log(t.Name + ": map rotation skipped (island open)"); }
            }
        }
    }

    static void PruneDrySpots()
    {
        if (info == null) return;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("DrownRespawn")) continue;
                var f = t.GetField("lastDry", All);
                var d = f == null ? null : f.GetValue(null) as Dictionary<string, Vector3>;
                if (d == null) continue;
                foreach (string k in new List<string>(d.Keys)) if (IslandLook.OnIsland(info, d[k])) d.Remove(k);
            }
    }

    // ---------------------------------------------------------------- test hooks (file commands)
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
                    case "probe": Probe(); break;
                    case "seed": seed = int.Parse(a[1], Ci); Log("seed = " + seed); break;
                    case "preview":
                        {
                            if (active) { Log("preview refused: island is open"); break; }
                            if (a.Length > 1) seed = int.Parse(a[1], Ci);
                            float y; if (!LakeY(out y)) { Log("preview: no lake"); break; }
                            Vector3? o = FindOrigin(y);
                            if (o == null) break;
                            BuildAt(seed, o.Value, "preview");
                            preview = true;
                            if (previewWater != null) UnityEngine.Object.Destroy(previewWater);
                            GameObject w = FG("water") as GameObject;
                            if (w != null)
                            {
                                previewWater = UnityEngine.Object.Instantiate(w);
                                previewWater.name = "IslandPreviewWater";
                                previewWater.transform.position = new Vector3(o.Value.x, w.transform.position.y, o.Value.z);
                                foreach (Collider col in previewWater.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(col);
                                IslandLook.Ocean(info, previewWater);
                            }
                            break;
                        }
                    case "destroy":
                        if (active) { Log("destroy refused: island is open"); break; }
                        if (info != null) { IslandLook.Destroy(info); info = null; }
                        if (previewWater != null) { UnityEngine.Object.Destroy(previewWater); previewWater = null; }
                        Log("destroyed"); break;
                    case "shots": Shots(a.Length > 1 ? a[1] : "v", Array.IndexOf(a, "light") > 0); break;
                    case "shot": Shot(a[1], new Vector3(float.Parse(a[2], Ci), float.Parse(a[3], Ci), float.Parse(a[4], Ci)), new Vector3(float.Parse(a[5], Ci), float.Parse(a[6], Ci), float.Parse(a[7], Ci)), a.Length > 8 ? float.Parse(a[8], Ci) : 60f, Array.IndexOf(a, "light") > 0); break;
                    case "test": Test(); break;
                    case "open": if (Open("file command")) InviteAll(); break;
                    case "close": if (active) CloseAll("file command"); break;
                    case "hotspots": HotspotsToIsland(); break;
                    case "homehots": if (!active) HotspotsHome(); break;
                    case "fakechat": Handle(c.Substring(9).Trim()); break;   // test: run a chat line ("Name: !island") without posting it
                    case "origin": lastOrigin = new Vector3(float.Parse(a[1], Ci), float.Parse(a[2], Ci), float.Parse(a[3], Ci)); break;
                    case "openhost":
                        {
                            NetworkUser me = User(HostName());
                            if (me == null) { Log("openhost: no host user"); break; }
                            Info("Test trip: " + me.userName + " is sailing to the new TROPICAL ISLAND (type !island leave to come back).");
                            if (Open("file command (host only)")) Invite(me, false);
                            break;
                        }
                    case "where":
                        foreach (var nu in NetworkUser.readOnlyInstancesList)
                        {
                            CharacterBody b = Body(nu);
                            if (b == null) { Log("where: " + nu.userName + " has no body"); continue; }
                            Log("where: " + nu.userName + " foot " + b.footPosition.ToString("0.00") + " alive=" + b.healthComponent.alive + " grounded=" + (b.characterMotor != null && b.characterMotor.isGrounded)
                                + " onIsland=" + IslandLook.OnIsland(info, b.footPosition) + " local=" + nu.isLocalPlayer);
                        }
                        { float y; Log("where: lake " + (LakeY(out y) ? y.ToString("0.00") : "none") + " at " + (FG("water") is GameObject ? ((GameObject)FG("water")).transform.position.ToString("0.0") : "-") + ", hotspot " + FG("hotspotPos") + ", hot2 " + FG("hot2Pos") + ", zonesOff " + zonesOff.Count); }
                        break;
                    case "view":
                        {
                            Camera sc = SceneCam();
                            if (sc != null) Shot(a.Length > 1 ? a[1] : "view", sc.transform.position, sc.transform.position + sc.transform.forward * 10f, sc.fieldOfView, false);
                            break;
                        }
                }
            }
            catch (Exception e) { Log("cmd error: " + e); }
        }
    }

    static void Probe()
    {
        float y; bool lake = LakeY(out y);
        Log("probe: scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ", lake " + (lake ? y.ToString("0.00") : "none") + ", world layer " + LayerIndex.world.intVal + " (NameToLayer " + LayerMask.NameToLayer("World") + ")"
            + ", fishing " + (Fish() == null ? "none" : Fish().Name) + ", RodStand " + Newest("RodStand", "testPlaceAt") + ", merchant " + Newest("FishMerchant", "merchantPos"));
        var zl = new List<string>();
        foreach (var z in UnityEngine.Object.FindObjectsOfType<MapZone>()) { var zc = z.GetComponent<Collider>(); zl.Add(z.triggerType + "/" + z.zoneType + (zc == null ? "" : " " + zc.GetType().Name + " " + zc.bounds.min.ToString("0") + ".." + zc.bounds.max.ToString("0"))); }
        Log("probe: " + zl.Count + " map zones: " + string.Join("; ", zl.ToArray()));
        Camera sc = SceneCam();
        Log("probe: scene cam " + (sc == null ? "none" : sc.name + " far " + sc.farClipPlane + " path " + sc.actualRenderingPath) + ", fog " + RenderSettings.fog + " " + RenderSettings.fogMode + " " + RenderSettings.fogEndDistance);
        if (info != null) Log("probe: island at " + info.origin.ToString("0.0") + " active=" + active + " preview=" + preview);
    }

    static Camera SceneCam()
    {
        foreach (var c in CameraRigController.readOnlyInstancesList) if (c != null && c.sceneCam != null) return c.sceneCam;
        return Camera.main;
    }

    static void Shots(string tag, bool light)
    {
        if (info == null) { Log("shots: no island"); return; }
        Vector3 o = info.origin, f = info.fwd, r = info.right, up = Vector3.up;
        Shot(tag + "_overview", o + f * 230f + r * 120f + up * 110f, o + up * 4f, 55f, light);
        Shot(tag + "_overview2", o - f * 210f - r * 150f + up * 95f, o + up * 4f, 55f, light);
        Shot(tag + "_top", o + up * 430f + f * 1f, o, 50f, light);
        Shot(tag + "_plaza", info.dockStart + f * 8f + up * 3.5f, info.plaza - f * 18f + up * 1f, 70f, light);
        Shot(tag + "_dock", info.dockEnd + f * 10f + up * 4f, info.plaza + up * 2f, 65f, light);
        Shot(tag + "_hut", info.hut + f * 9f - r * 4f + up * 2.5f, info.hut + up * 1.6f, 65f, light);
        Shot(tag + "_campfire", info.campfire + f * 6f + r * 5f + up * 2.6f, info.campfire + up * 0.4f, 65f, light);
        Vector3 lo = (info.lagoon - o); lo.y = 0; lo.Normalize();
        Shot(tag + "_lagoon", info.lagoon - lo * 40f + up * 16f, info.lagoon, 65f, light);
        Shot(tag + "_hill", info.hill + up * 3f, info.plaza + up * 1f, 70f, light);
        Shot(tag + "_beach", info.plaza + r * 30f + f * 12f + up * 1.8f, info.plaza - r * 30f + f * 4f + up * 3f, 70f, light);
    }

    static void Shot(string name, Vector3 from, Vector3 at, float fov, bool light)
    {
        Directory.CreateDirectory(ShotDir);
        Camera sc = SceneCam();
        var go = new GameObject("IslandShotCam");
        GameObject lg = null;
        try
        {
            var cam = go.AddComponent<Camera>();
            if (sc != null) cam.CopyFrom(sc);
            go.transform.position = from; go.transform.LookAt(at);
            cam.fieldOfView = fov; cam.enabled = false; cam.farClipPlane = Mathf.Max(cam.farClipPlane, 1500f);
            if (light) { lg = new GameObject("IslandShotLight"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.1f; l.color = new Color(1f, 0.96f, 0.88f); lg.transform.rotation = Quaternion.Euler(50f, 30f, 0f); }
            var rt = new RenderTexture(1600, 900, 24);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            string path = ShotDir + name + ".png";
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex); rt.Release(); UnityEngine.Object.Destroy(rt);
            Log("shot " + path);
        }
        finally { UnityEngine.Object.Destroy(go); if (lg != null) UnityEngine.Object.DestroyImmediate(lg); }
    }

    static GameObject probeBall; static float probeAt;
    static void Test()
    {
        if (info == null) { Log("test: no island"); return; }
        int mask = LayerIndex.world.mask.value;
        Vector3 o = info.origin;
        int hits = 0, misses = 0, land = 0; float worst = 0f;
        for (float x = -110f; x <= 110f; x += 10f)
            for (float z = -110f; z <= 110f; z += 10f)
            {
                RaycastHit h;
                if (Physics.Raycast(new Vector3(o.x + x, o.y + 120f, o.z + z), Vector3.down, out h, 200f, mask, QueryTriggerInteraction.Ignore)) { hits++; if (h.point.y > o.y) land++; }
                else misses++;
            }
        Log("test: grid raycasts " + hits + " hit / " + misses + " miss (" + land + " above sea level)");
        foreach (var s in info.spawns)
        {
            RaycastHit h;
            bool g = Physics.Raycast(s + Vector3.up * 2f, Vector3.down, out h, 6f, mask, QueryTriggerInteraction.Ignore);
            bool blocked = Physics.CheckCapsule(s + Vector3.up * 0.7f, s + Vector3.up * 1.6f, 0.45f, mask, QueryTriggerInteraction.Ignore);
            Log("test: spawn " + s.ToString("0.0") + " ground " + (g ? h.point.y.ToString("0.00") + " (" + h.collider.name + ")" : "NONE") + (blocked ? " BLOCKED" : " clear"));
            if (g) worst = Mathf.Max(worst, Mathf.Abs(h.point.y - (s.y - 0.2f)));
        }
        Vector3[] probes = { info.dockStart, (info.dockStart + info.dockEnd) * 0.5f, info.dockEnd, info.hut + Vector3.up * 0.6f, info.campfire, info.stall, info.merchant, info.hill, info.lagoon };
        string[] names = { "dock start", "dock middle", "dock end", "hut floor", "campfire", "stall spot", "merchant spot", "hill top", "lagoon" };
        for (int i = 0; i < probes.Length; i++)
        {
            RaycastHit h; Vector3 p = probes[i];
            bool g = Physics.Raycast(new Vector3(p.x, o.y + 60f, p.z), Vector3.down, out h, 120f, mask, QueryTriggerInteraction.Ignore);
            Log("test: " + names[i] + " expected y " + p.y.ToString("0.00") + " -> " + (g ? h.point.y.ToString("0.00") + " on " + h.collider.name + " normal.y " + h.normal.y.ToString("0.00") : "NO HIT")
                + " (sea " + o.y.ToString("0.00") + ")");
        }
        RaycastHit sh;
        bool sea = Physics.Raycast(info.hotspot + Vector3.up * 50f, Vector3.down, out sh, 100f, mask, QueryTriggerInteraction.Ignore);
        Log("test: hotspot water depth " + (sea ? (o.y - sh.point.y).ToString("0.0") + " m (" + sh.collider.name + ")" : "no floor") + ", open water=" + (!sea || sh.point.y < o.y - 0.2f));
        // physics drop test: a capsule falls onto the plaza and should come to rest on the ground
        if (probeBall != null) UnityEngine.Object.Destroy(probeBall);
        probeBall = new GameObject("IslandDropTest");
        probeBall.transform.position = info.spawns[3] + Vector3.up * 4f;
        var cc = probeBall.AddComponent<CapsuleCollider>(); cc.radius = 0.4f; cc.height = 1.8f;
        var rb = probeBall.AddComponent<Rigidbody>(); rb.mass = 80f;
        probeBall.layer = 0;
        probeAt = Time.time;
        RoR2Application.onUpdate += DropCheck;
        Log("test: worst spawn ground offset " + worst.ToString("0.00") + " m; drop test started at " + probeBall.transform.position.ToString("0.0") + " (layer 0 collides with world: " + !Physics.GetIgnoreLayerCollision(0, LayerIndex.world.intVal) + ")");
    }
    static void DropCheck()
    {
        if (probeBall == null) { RoR2Application.onUpdate -= DropCheck; return; }
        if (Time.time - probeAt < 3f) return;
        RoR2Application.onUpdate -= DropCheck;
        Log("test: drop capsule after 3 s at " + probeBall.transform.position.ToString("0.00") + " (spawn " + info.spawns[3].ToString("0.00") + ")");
        UnityEngine.Object.Destroy(probeBall); probeBall = null;
    }
#endif
}

#if ISLAND_CLIENT
public class IslandClientBehaviour : MonoBehaviour
{
    void Update() { IslandClient.Update(); }
}
#endif

