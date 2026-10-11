// RoR2 Fishing - BepInEx plugin (replaces the runtime injector / LOAD-FISHING.ps1 / FISHING.exe).
//
// One plugin for everybody. It carries the two halves of the fishing game as embedded assemblies and
// starts the right one on the game's main thread:
//   * FishingHost.dll  - the host-authoritative game (every LOAD-FISHING.ps1 add-on): started the first time this
//                        game becomes the server (hosting a lobby, or single player).
//   * FishingGuest.dll - the guest client (every FISHING.exe 2.1 payload): started the first time this game is
//                        connected to somebody else's server.
// The host runs its own host-side visuals, so it never starts the guest copies (no doubled perks/fish/rods).
// Every component keeps its original "public static void Run()" + "public static bool enabled" shape and is
// called once, in the original LOAD-FISHING.ps1 / FISHING.exe order (FishingCore.HostOrder / GuestOrder).
// Settings: BepInEx\config\com.jv94.fishing.cfg. Saved game state: BepInEx\config\JV94.Fishing\.
//
// Never run this plugin and the injected classes (LOAD-FISHING.ps1 / FISHING.exe) in the same game: the plugin
// checks every loaded assembly and stays off (or switches itself off) when it sees injected Fishing* classes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

[BepInPlugin(FishingCore.Guid, FishingCore.Name, FishingCore.Version)]
public class RoR2FishingPlugin : BaseUnityPlugin
{
    internal static ManualLogSource log;
    FishingSettings S;

    enum Role { None, Host, Guest }
    Role role = Role.None;
    Assembly hostAsm, guestAsm;
    bool hostStarted, guestStarted, guestPending, ready, warnedSwitch;
    float nextGateCheck;
    int chatSeen;
    string dataDir;
    volatile string injectedSeen;   // set from AppDomain.AssemblyLoad (any thread), handled in Update
    bool injectedHandled, injectedChatShown;

    void Awake()
    {
        log = Logger;
        FishingCore.Info = s => log.LogInfo(s);
        FishingCore.Warn = s => log.LogWarning(s);
        FishingCore.Error = s => log.LogError(s);

        S = new FishingSettings(Config);
        S.HostSettingChanged += delegate { if (hostStarted && hostAsm != null) FishingCore.ApplyHostConfig(hostAsm, S); };

        dataDir = Path.Combine(Paths.ConfigPath, FishingCore.DataFolderName);
        FishingCore.PrepareDataDir(dataDir, S.migrateFrom.Value);

        // injected copies of the game (LOAD-FISHING.ps1 / FISHING.exe) must not run next to the plugin
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { string n = SafeName(a); if (FishingCore.IsInjectedName(n)) { injectedSeen = n; break; } }
        AppDomain.CurrentDomain.AssemblyLoad += (s, e) => { string n = SafeName(e.LoadedAssembly); if (FishingCore.IsInjectedName(n)) injectedSeen = n; };

        if (S.requireSameMod.Value) RegisterNetworkMod();
        log.LogInfo(FishingCore.Name + " " + FishingCore.Version + " loaded; data folder " + dataDir + "; waiting to host or join a game.");
    }

    static string SafeName(Assembly a) { try { return a.GetName().Name; } catch { return ""; } }

    // ------------------------------------------------------------ networking compatibility (no R2API needed)
    // Same thing R2API's NetworkCompatibility(EveryoneMustHaveMod) does: the game hashes this list into its build id,
    // so players with a different version (or without the mod) can't see or join each other's lobbies.
    void RegisterNetworkMod()
    {
        try
        {
            Type h = typeof(RoR2.RoR2Application).Assembly.GetType("RoR2.NetworkModCompatibilityHelper");
            PropertyInfo p = h == null ? null : h.GetProperty("networkModList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo set = p == null ? null : p.GetSetMethod(true);
            if (set == null) { log.LogWarning("NetworkModCompatibilityHelper.networkModList not found - lobby version check not registered"); return; }
            var list = new List<string>();
            IEnumerable cur = p.GetValue(null, null) as IEnumerable;
            if (cur != null) foreach (object o in cur) if (o != null) list.Add(o.ToString());
            string me = FishingCore.Guid + ";" + FishingCore.Version;
            if (!list.Contains(me)) list.Add(me);
            set.Invoke(null, new object[] { list.ToArray() });
            log.LogInfo("registered " + me + " in the game's network mod list (everyone needs the same version)");
        }
        catch (Exception e) { log.LogWarning("network mod registration failed: " + e); }
    }

    // Every mod in this list must be installed (same version) by everybody in the lobby. Logged once so a host whose
    // friends can't see the lobby can compare lists (other "everyone needs it" mods in the profile count too).
    static void LogNetworkModList()
    {
        try
        {
            Type h = typeof(RoR2.RoR2Application).Assembly.GetType("RoR2.NetworkModCompatibilityHelper");
            IEnumerable cur = h.GetProperty("networkModList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null, null) as IEnumerable;
            var names = new List<string>();
            if (cur != null) foreach (object o in cur) names.Add(Convert.ToString(o));
            log.LogInfo("mods everyone in the lobby needs (game's network mod list): " + (names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray())));
        }
        catch (Exception e) { log.LogWarning("could not read the network mod list: " + e.Message); }
    }

    // ------------------------------------------------------------ main-thread role watcher
    void Update()
    {
        try
        {
            if (injectedSeen != null && !injectedHandled) OnInjected();
            if (injectedHandled) { InjectedChatHint(); return; }
            if (!ready)
            {
                if (RoR2.RoR2Application.instance == null) return;   // game still booting
                ready = true;
                LogNetworkModList();
                try { RoR2.Chat.onChatChanged += OnChatChanged; } catch (Exception e) { log.LogWarning("chat hook for !prof failed: " + e.Message); }
            }
            if (profRequested) { profRequested = false; StartProfiler(); }
            Role now = NetworkServer.active ? Role.Host : (NetworkClient.active ? Role.Guest : Role.None);
            if (now != role)
            {
                log.LogInfo("role " + role + " -> " + now);
                role = now;
                OnRole(now);
            }
            if (guestPending && Time.unscaledTime >= nextGateCheck)
            {
                nextGateCheck = Time.unscaledTime + 1f;
                if (role != Role.Guest) guestPending = false;
                else if (S.requireSameMod.Value || HostTalksFishing()) { guestPending = false; StartGuest(); }
            }
        }
        catch (Exception e) { log.LogError("update error: " + e); }
    }

    // ------------------------------------------------------------ dev command: "!prof" typed by the local player in chat
    // runs FrameProf2 from the started half: wraps every RoR2Application.onUpdate/onFixedUpdate/onLateUpdate subscriber for
    // 8 s, then writes ms/frame per subscriber to %TEMP%\ror2_prof.log and restores the events.
    bool profRequested;
    void OnChatChanged()
    {
        try
        {
            var l = RoR2.Chat.readOnlyLog;
            if (l == null || l.Count == 0) return;
            string plain = Regex.Replace(l[l.Count - 1] ?? "", "<[^>]*>", "").Trim();
            if (!plain.EndsWith(": !prof")) return;
            foreach (var nu in RoR2.NetworkUser.readOnlyLocalPlayersList)
                if (nu != null && plain == nu.userName + ": !prof") { profRequested = true; return; }
        }
        catch { }
    }

    void StartProfiler()
    {
        Assembly a = hostStarted ? hostAsm : guestAsm;
        if (a == null) { log.LogInfo("!prof: start or join a game first"); return; }
        if (FishingCore.RunComponent(a, "FrameProf2"))
            try { RoR2.Chat.AddMessage("<color=#66ccff>Fishing mod: profiling 8 s -> %TEMP%\\ror2_prof.log</color>"); } catch { }
    }

    void OnInjected()
    {
        injectedHandled = true;
        log.LogError("Injected fishing classes found in this game (assembly '" + injectedSeen + "', from LOAD-FISHING.ps1 / Injector.exe or FISHING.exe). "
            + "The plugin and the injected copies must never run in the same game: they disable each other's classes and double every visual. "
            + (hostStarted || guestStarted ? "The plugin's components are now switched OFF for this session." : "The plugin will NOT start in this session.")
            + " Restart the game and use either the plugin (r2modman 'Start modded') or the injected setup, not both.");
        foreach (string c in FishingCore.HostOrder) FishingCore.SetEnabled(hostAsm, c, false);
        foreach (string c in FishingCore.GuestOrder) FishingCore.SetEnabled(guestAsm, c, false);
        guestPending = false;
    }

    void InjectedChatHint()
    {
        if (injectedChatShown || RoR2.Run.instance == null) return;
        injectedChatShown = true;
        try { RoR2.Chat.AddMessage("<color=#ff6666>Fishing mod (BepInEx) is OFF: injected fishing classes were found in this game. Restart the game and use one or the other.</color>"); } catch { }
    }

    void OnRole(Role r)
    {
        if (r == Role.Host)
        {
            guestPending = false;
            // guest copies idle by themselves on a host (they check NetworkServer.active); the fish renderer is switched
            // off explicitly because the host's FishFx4 only does that when it starts for the first time
            if (guestStarted) FishingCore.SetEnabled(guestAsm, "FishFxClient", false);
            if (!hostStarted) StartHost();
            else
            {
                foreach (string c in FishingCore.HostPlan(S, null)) FishingCore.SetEnabled(hostAsm, c, true);
                FishingCore.ApplyHostConfig(hostAsm, S);
                log.LogInfo("host add-ons re-enabled");
            }
        }
        else if (r == Role.Guest)
        {
            if (hostStarted)
            {   // hosted earlier in this game session: switch the host add-ons off so they don't fight the guest client
                foreach (string c in FishingCore.HostOrder) FishingCore.SetEnabled(hostAsm, c, false);
                log.LogInfo("host add-ons disabled (now a guest)");
                if (!warnedSwitch)
                {
                    warnedSwitch = true;
                    try { RoR2.Chat.AddMessage("<color=#66ccff>Fishing mod: you hosted earlier in this session. If the fishing visuals look wrong, restart the game before joining.</color>"); } catch { }
                }
            }
            if (!guestStarted) { guestPending = true; nextGateCheck = 0f; chatSeen = 0; }
            else { foreach (string c in FishingCore.GuestPlan(S, null)) FishingCore.SetEnabled(guestAsm, c, true); }
        }
    }

    // RequireSameModVersion off: only start the guest half once the host's fishing game has sent a hidden "[FT] " line
    // (so joining a vanilla host never removes your weapons).
    bool HostTalksFishing()
    {
        IList l = RoR2.Chat.readOnlyLog as IList;
        if (l == null) return false;
        if (chatSeen > l.Count) chatSeen = 0;
        for (int i = chatSeen; i < l.Count; i++)
        {
            string s = l[i] as string;
            if (s != null && s.Contains("[FT] ")) { chatSeen = l.Count; return true; }
        }
        chatSeen = l.Count;
        return false;
    }

    // ------------------------------------------------------------ starting the halves
    void StartHost()
    {
        hostStarted = true;
        hostAsm = FishingCore.LoadEmbedded(typeof(RoR2FishingPlugin).Assembly, "FishingHost.dll");
        if (hostAsm == null) return;
        FishingCore.SetPaths(hostAsm, dataDir, S.toolsFolder.Value);
        FishingCore.ApplyHostConfig(hostAsm, S);
        var skipped = new List<string>();
        List<string> plan = FishingCore.HostPlan(S, skipped);
        foreach (string s in skipped) log.LogInfo("skip " + s);
        foreach (string c in plan) FishingCore.RunComponent(hostAsm, c);
        log.LogInfo("host fishing game started (" + plan.Count + " add-ons)");
    }

    void StartGuest()
    {
        guestStarted = true;
        guestAsm = FishingCore.LoadEmbedded(typeof(RoR2FishingPlugin).Assembly, "FishingGuest.dll");
        if (guestAsm == null) return;
        FishingCore.SetPaths(guestAsm, dataDir, S.toolsFolder.Value);
        var skipped = new List<string>();
        List<string> plan = FishingCore.GuestPlan(S, skipped);
        foreach (string s in skipped) log.LogInfo("skip " + s);
        foreach (string c in plan) FishingCore.RunComponent(guestAsm, c);
        log.LogInfo("guest fishing client started (" + plan.Count + " parts)");
    }
}

// ================================================================ settings (BepInEx\config\com.jv94.fishing.cfg)
public class FishingSettings
{
    public ConfigEntry<bool> requireSameMod;
    public ConfigEntry<bool> peaceful, mapPicker, drownRespawn, leftClickFishing, whipOn;
    public ConfigEntry<float> castRange, whipRange, whipAngle, whipCooldown, voteMinutes;
    public ConfigEntry<string> autoFishPlayer;
    public ConfigEntry<bool> autoStartTourney, lunarPrize;
    public ConfigEntry<float> tourneyMinutes;
    public ConfigEntry<string> wormTarget;
    public ConfigEntry<float> wormChance, poopChance;
    public ConfigEntry<bool> requestPanel, testSandbox;
    public ConfigEntry<string> toolsFolder, migrateFrom;
    public event EventHandler HostSettingChanged;

    public FishingSettings(ConfigFile c)
    {
        requireSameMod = c.Bind("General", "RequireSameModVersion", true,
            "Everybody in a lobby must have this mod (same version). Lobbies with a different version are hidden/refused by the game. "
            + "Turning this off only makes sense for testing; guests then start the fishing visuals only after the host's fishing game talks to them.");

        peaceful = c.Bind("Host", "PeacefulMode", true, "Host only: no enemies (fishing-only game).");
        mapPicker = c.Bind("Host", "MapPickerRing", true, "Host only: a ring of Lunar Seer podiums on every stage; the first player to use one picks the next map.");
        drownRespawn = c.Bind("Host", "DrownRespawn", true, "Host only: players who drown in the lake respawn on dry land after 10 s.");
        leftClickFishing = c.Bind("Host", "HostLeftClickFishing", true, "Host only: left click casts/reels for the host (F always works).");
        castRange = c.Bind("Host", "CastRange", 200f, "Host only: maximum cast distance in metres (rod types scale it, e.g. Stick Rod x0.75).");
        whipOn = c.Bind("Host", "RodWhip", true, "Host only: right click = Rod Whip (1 damage, snaps the target's line).");
        whipRange = c.Bind("Host", "WhipRange", 20f, "Host only: Rod Whip reach in metres.");
        whipAngle = c.Bind("Host", "WhipAngle", 60f, "Host only: Rod Whip aim cone in degrees.");
        whipCooldown = c.Bind("Host", "WhipCooldown", 1.5f, "Host only: seconds between whips per player.");
        autoFishPlayer = c.Bind("Host", "AutoFishPlayer", "", "Host only: exact in-game name of ONE player who auto-fishes while standing idle (empty = nobody).");
        voteMinutes = c.Bind("Host", "FeatureVoteMinutes", 12f, "Host only: minutes between automatic feature polls (needs vote_candidates.json in the tools folder). 0 = no automatic polls (!poll still works).");

        autoStartTourney = c.Bind("Tournament", "AutoStart", true, "Host only: start a timed tournament ~5 s after each run begins (otherwise it is open-ended; the host types !endtournament).");
        tourneyMinutes = c.Bind("Tournament", "Minutes", 10f, "Host only: length of the auto-started tournament.");
        lunarPrize = c.Bind("Tournament", "WinnerTakesLunarCoins", true,
            "Host only: when the tournament ends, the winner gets ALL other connected players' lunar coins (real, saved coins!). Off = bragging rights only.");

        wormTarget = c.Bind("Pranks", "MagmaWormTarget", "", "Host only: exact in-game name of a player whose catches can hook a live Magma Worm (empty = off).");
        wormChance = c.Bind("Pranks", "MagmaWormChance", 0.25f, "Host only: chance per catch of that player.");
        poopChance = c.Bind("Pranks", "PoopChance", 0f, "Host only: chance that any catch (outside the hotspot) is Poop worth -$1,000,000. 0 = off.");

        requestPanel = c.Bind("Dev", "RequestPanel", false,
            "F2 'requests' panel (type a feature request, the host's game appends it to C:\\RoR2-Requests\\requests.jsonl). Host and guests each switch their own panel.");
        testSandbox = c.Bind("Dev", "TestSandbox", true, "Host only: the test sandbox (!sandbox / !test / F6 panel). Reads test_queue.json and writes test_results.jsonl in the tools folder.");
        toolsFolder = c.Bind("Dev", "ToolsFolder", "",
            "Folder for test_queue.json, test_results.jsonl, vote_candidates.json, votes.jsonl and vote_totals.json. Empty = BepInEx\\config\\JV94.Fishing.");
        migrateFrom = c.Bind("Dev", "MigrateFromFolder", "",
            "One-time copy of saved state (rod_owners.json, case_items.json, ...) from an older install's folder into BepInEx\\config\\JV94.Fishing. "
            + "Runs once (marker file .migrated); existing files are never overwritten. Empty = off.");

        foreach (ConfigEntryBase e in new ConfigEntryBase[] { castRange, leftClickFishing, whipOn, whipRange, whipAngle, whipCooldown, autoFishPlayer, voteMinutes, tourneyMinutes, lunarPrize, wormTarget, wormChance, poopChance })
            Hook(e);
    }

    void Hook(ConfigEntryBase e)
    {
        // ConfigEntry<T>.SettingChanged exists on every entry type; hook it through reflection so one helper covers them all
        EventInfo ev = e.GetType().GetEvent("SettingChanged");
        if (ev == null) return;
        EventHandler h = delegate { EventHandler x = HostSettingChanged; if (x != null) x(this, EventArgs.Empty); };
        ev.AddEventHandler(e, h);
    }
}

// ================================================================ everything that doesn't need Unity (offline harness drives this)
public static class FishingCore
{
    public const string Guid = "com.jv94.fishing";
    public const string Name = "RoR2 Fishing";
    public const string Version = "3.0.0";
    public const string DataFolderName = "JV94.Fishing";

    public static Action<string> Info = s => { }, Warn = s => { }, Error = s => { };

    // Host add-ons in LOAD-FISHING.ps1 order (2026-10-10 22:30). WhipTune1 is not a class here: its two values are the
    // config Host.WhipRange / Host.WhipAngle (ApplyHostConfig). HostOrderSource lists the LOAD-FISHING names 1:1.
    public static readonly string[] HostOrder = {
        "Peaceful2", "Fishing25", "FishSlot1", "NativeWater6", "FishFx4", "NoWeapons4", "FishItems4", "ReqPanel1", "HotBeam7",
        "Pranks2", "BootPlacebo1", "WhipAnim9", "CastAnim1", "DrownRespawn2", "MapRing3", "TourneyEnd1", "DayNight1",
        "RareSound1", "DronePets1", "Aquarium1", "UnderwaterFx1", "Swim3", "FeatureVote1", "RodStand7", "TimeHud1", "GuideBook10",
        "FishMerchant5", "WeatherTune1", "Flashlight1", "WaterLevel3", "Inventory4", "HotspotCompass6", "Island5", "TestSandbox6",
        "StartTourney3" };
    public static readonly string[] ConfigOnly = { "WhipTune1" };
    // Guest client in FISHING.exe 2.1 order (FishingLauncher.cs). FishingChatHide (plugin's own) hides the "[FT] ..."
    // lines from the guest's chat box; in FISHING.exe ReqPanelClient did that, which is now optional (Dev.RequestPanel).
    public static readonly string[] GuestOrder = {
        "FishingClient", "FishingChatHide", "ReqPanelClient", "FishFxClient", "NoWeaponsClient", "FishItemsClient", "CompassClient",
        "VoteClient", "InventoryClient", "ShopStandClient", "MerchantClient", "IslandClient", "AquariumClient" };

    public static List<string> HostPlan(FishingSettings s, List<string> skipped)
    {
        var l = new List<string>();
        foreach (string c in HostOrder)
        {
            string why = null;
            if (c == "Peaceful2" && !s.peaceful.Value) why = "Host.PeacefulMode = false";
            else if (c == "MapRing3" && !s.mapPicker.Value) why = "Host.MapPickerRing = false";
            else if (c == "DrownRespawn2" && !s.drownRespawn.Value) why = "Host.DrownRespawn = false";
            else if (c == "StartTourney3" && !s.autoStartTourney.Value) why = "Tournament.AutoStart = false";
            else if (c == "ReqPanel1" && !s.requestPanel.Value) why = "Dev.RequestPanel = false";
            else if (c == "TestSandbox6" && !s.testSandbox.Value) why = "Dev.TestSandbox = false";
            if (why == null) l.Add(c); else if (skipped != null) skipped.Add(c + " (config " + why + ")");
        }
        return l;
    }

    public static List<string> GuestPlan(FishingSettings s, List<string> skipped)
    {
        var l = new List<string>();
        foreach (string c in GuestOrder)
        {
            if (c == "ReqPanelClient" && !s.requestPanel.Value) { if (skipped != null) skipped.Add(c + " (config Dev.RequestPanel = false)"); continue; }
            l.Add(c);
        }
        return l;
    }

    public static void ApplyHostConfig(Assembly host, FishingSettings s)
    {
        if (host == null) return;
        SetStatic(host, "Fishing25", "castRange", s.castRange.Value);
        SetStatic(host, "Fishing25", "hostLeftClickFishing", s.leftClickFishing.Value);
        SetStatic(host, "Fishing25", "whipEnabled", s.whipOn.Value);
        SetStatic(host, "Fishing25", "whipRange", s.whipRange.Value);       // was WhipTune1 (20 m)
        SetStatic(host, "Fishing25", "whipAngle", s.whipAngle.Value);       // was WhipTune1 (60 deg)
        SetStatic(host, "Fishing25", "whipCooldown", s.whipCooldown.Value);
        SetStatic(host, "Fishing25", "AutoName", (s.autoFishPlayer.Value ?? "").Trim());
        float vm = s.voteMinutes.Value;
        SetStatic(host, "FeatureVote1", "autoPolls", vm > 0f);
        if (vm > 0f) SetStatic(host, "FeatureVote1", "intervalMinutes", vm);
        SetStatic(host, "StartTourney3", "Minutes", s.tourneyMinutes.Value);
        SetStatic(host, "TourneyEnd1", "lunarCoinPrize", s.lunarPrize.Value);
        SetStatic(host, "Pranks2", "WormTarget", (s.wormTarget.Value ?? "").Trim());
        SetStatic(host, "Pranks2", "WormChance", s.wormChance.Value);
        SetStatic(host, "Pranks2", "PoopChance", s.poopChance.Value);
    }

    // FishingPaths lives in each half (src\shared); set before any component's static fields are first touched.
    public static void SetPaths(Assembly a, string dataDir, string toolsDir)
    {
        SetStatic(a, "FishingPaths", "DataDir", dataDir);
        SetStatic(a, "FishingPaths", "ToolsDir", (toolsDir ?? "").Trim());
        Info(a.GetName().Name + ": data " + dataDir + ", tools " + (string.IsNullOrEmpty((toolsDir ?? "").Trim()) ? dataDir : toolsDir.Trim()));
    }

    // Saved state the host add-ons keep between games (file names as the injected builds used in C:\RoR2-FishingClient\host).
    public static readonly string[] StateFiles = { "rod_owners.json", "case_items.json", "gadget_owners.txt", "glow_owners.txt",
        "inventory_slots.json", "fishing_zones.json", "stick_migration.done", "sandbox_snapshot.txt" };

    // Creates the data folder and, once, copies saved state from an older install (config Dev.MigrateFromFolder).
    // Returns the number of files copied.
    public static int PrepareDataDir(string dataDir, string migrateFrom)
    {
        int copied = 0;
        try
        {
            Directory.CreateDirectory(dataDir);
            string marker = Path.Combine(dataDir, ".migrated");
            string from = (migrateFrom ?? "").Trim();
            if (from == "" || File.Exists(marker)) return 0;
            if (!Directory.Exists(from)) { Warn("migration: folder " + from + " not found - nothing copied (will try again next launch)"); return 0; }
            var done = new List<string>();
            foreach (string f in StateFiles)
            {
                string src = Path.Combine(from, f), dst = Path.Combine(dataDir, f);
                if (!File.Exists(src)) continue;
                if (File.Exists(dst)) { Info("migration: kept existing " + dst); continue; }
                File.Copy(src, dst);
                done.Add(f); copied++;
            }
            File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " copied from " + from + ": " + (done.Count == 0 ? "(nothing)" : string.Join(", ", done.ToArray()))
                + Environment.NewLine + "Delete this file to copy again (files that exist here are never overwritten)." + Environment.NewLine);
            Info("migration: copied " + copied + " file(s) from " + from + " to " + dataDir + (done.Count > 0 ? " (" + string.Join(", ", done.ToArray()) + ")" : ""));
        }
        catch (Exception e) { Error("data folder / migration failed: " + e); }
        return copied;
    }

    // Assembly names of the injected builds: host add-ons are compiled as <Class>.dll (Fishing25, Island5, ...), the
    // FISHING.exe payloads as <Name>_v<NN> (FishingClient_v21, ...). The plugin's own halves are FishingHost/FishingGuest.
    static readonly Regex InjectedHost = new Regex(@"^(Peaceful|Fishing|FishSlot|NativeWater|FishFx|NoWeapons|FishItems|ReqPanel|HotBeam|Pranks|BootPlacebo|WhipTune|WhipAnim|CastAnim|DrownRespawn|MapRing|TourneyEnd|Tourney|DayNight|RareSound|Roulette|DronePets|Aquarium|UnderwaterFx|Swim|FeatureVote|RodStand|TimeHud|GuideBook|FishMerchant|WeatherTune|Flashlight|WaterLevel|Inventory|HotspotCompass|Island|TestSandbox|StartTourney)\d+$");
    static readonly Regex InjectedGuest = new Regex(@"^(FishingClient|ReqPanelClient|FishFxClient|NoWeaponsClient|FishItemsClient|CompassClient|VoteClient|InventoryClient|ShopStandClient|MerchantClient|IslandClient|AquariumClient|RouletteClient)(_v\d+)?$");
    public static bool IsInjectedName(string n)
    {
        if (string.IsNullOrEmpty(n) || n == "FishingHost" || n == "FishingGuest" || n == "RoR2Fishing") return false;
        return InjectedHost.IsMatch(n) || InjectedGuest.IsMatch(n);
    }

    public static Assembly LoadEmbedded(Assembly from, string resource)
    {
        try
        {
            using (Stream s = from.GetManifestResourceStream(resource))
            {
                if (s == null) { Error("embedded " + resource + " missing - rebuild the plugin"); return null; }
                var bytes = new byte[s.Length];
                int read = 0;
                while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                Assembly a = Assembly.Load(bytes);
                Info("loaded " + a.GetName().Name + " (" + bytes.Length + " bytes)");
                return a;
            }
        }
        catch (Exception e) { Error("loading " + resource + " failed: " + e); return null; }
    }

    public static bool RunComponent(Assembly a, string type)
    {
        try
        {
            Type t = a.GetType(type);
            MethodInfo run = t == null ? null : t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (run == null) { Error(type + ".Run() not found"); return false; }
            run.Invoke(null, null);
            Info("started " + type);
            return true;
        }
        catch (Exception e) { Error(type + ".Run() failed: " + (e.InnerException ?? e)); return false; }
    }

    public static void SetEnabled(Assembly a, string type, bool on)
    {
        if (a == null) return;
        try
        {
            Type t = a.GetType(type);
            FieldInfo f = t == null ? null : t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.FieldType == typeof(bool)) f.SetValue(null, on);
        }
        catch (Exception e) { Warn(type + ".enabled = " + on + " failed: " + e.Message); }
    }

    public static bool SetStatic(Assembly a, string type, string field, object value)
    {
        try
        {
            Type t = a.GetType(type);
            FieldInfo f = t == null ? null : t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null || f.IsLiteral || f.IsInitOnly) { Warn("config: " + type + "." + field + " not settable"); return false; }
            f.SetValue(null, Convert.ChangeType(value, f.FieldType));
            return true;
        }
        catch (Exception e) { Warn("config: " + type + "." + field + " = " + value + " failed: " + (e.InnerException ?? e).Message); return false; }
    }
}
