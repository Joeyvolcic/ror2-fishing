// Compiled into both FishingHost.dll and FishingGuest.dll (BepInEx build).
// The original add-ons find each other ("disable the older class", "find the newest Fishing<N>") by scanning
// every loaded assembly. Under BepInEx that would include every other mod (and the huge MMHOOK assemblies), so
// the scans are limited to the game's own assemblies (everything in Risk of Rain 2_Data\Managed, e.g. RoR2,
// UnityEngine.*, Rewired_Core, Unity.Postprocessing.Runtime) plus this mod's two halves.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class FishingScope
{
    static Assembly[] cache = new Assembly[0];
    static int seen = -1;
    static string managed;

    public static Assembly[] Assemblies()
    {
        Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
        if (all.Length == seen) return cache;
        if (managed == null)
        {
            managed = "";
            try { managed = Path.GetFullPath(Path.GetDirectoryName(typeof(UnityEngine.Object).Assembly.Location)).TrimEnd('\\', '/'); } catch { }
        }
        var keep = new List<Assembly>();
        foreach (Assembly a in all)
        {
            string n;
            try { n = a.GetName().Name; } catch { continue; }
            if (IsMod(a) || n == "RoR2" || n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n == "Rewired_Core" || n == "Rewired_Windows"
                || n == "com.unity.multiplayer-hlapi.Runtime" || n == "LegacyResourcesAPI" || n == "Assembly-CSharp" || n == "KinematicCharacterController"
                || n == "HGCSharpUtils" || n == "HGUnityUtils" || n == "AK.Wwise.Unity.API" || n == "mscorlib" || n == "System" || n == "System.Core")
            { keep.Add(a); continue; }
            try
            {   // any other assembly the game itself loaded from its Managed folder (not BepInEx plugins / MMHOOK)
                string loc = a.IsDynamic ? "" : a.Location;
                if (managed != "" && !string.IsNullOrEmpty(loc) && string.Equals(Path.GetFullPath(Path.GetDirectoryName(loc)).TrimEnd('\\', '/'), managed, StringComparison.OrdinalIgnoreCase))
                    keep.Add(a);
            }
            catch { }
        }
        cache = keep.ToArray();
        seen = all.Length;
        return cache;
    }

    // Every "a.GetTypes()" of the copied add-ons is rewritten to this (tools\sync-sources.ps1). The scans only ever look for
    // the add-ons' own classes (Fishing<N>, DayNight<N>, ...), which all live in this mod's assemblies now, so game
    // assemblies (RoR2.dll: many thousands of types) yield nothing and this mod's type list is cached. This is what keeps
    // the per-frame "newest Fishing<N>" lookups cheap (the injected FishMerchant4/GuideBook9 cost ~18 ms/frame each).
    static readonly Dictionary<Assembly, Type[]> typeCache = new Dictionary<Assembly, Type[]>();
    static readonly Type[] none = new Type[0];
    public static Type[] TypesOf(Assembly a)
    {
        if (a == null || !IsMod(a)) return none;
        Type[] ts;
        lock (typeCache)
        {
            if (typeCache.TryGetValue(a, out ts)) return ts;
            try { ts = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, t => t != null); }
            catch { ts = none; }
            typeCache[a] = ts;
        }
        return ts;
    }

    // This mod's own halves. The injected add-ons each lived in an assembly named like their class (Island5.dll,
    // TourneyEnd1.dll, ...); a few lookups filter on that name and accept this mod's assemblies too.
    public static bool IsMod(Assembly a)
    {
        try { string n = a.GetName().Name; return n == "FishingHost" || n == "FishingGuest"; } catch { return false; }
    }

    // The local (host's own) player's name. Replaces hard-coded "JV94" checks of the injected builds.
    static string localName = ""; static float localNameAt = -10f;
    public static string LocalName()
    {
        float now = UnityEngine.Time.unscaledTime;
        if (now - localNameAt > 2f || now < localNameAt)
        {
            localNameAt = now;
            try { foreach (var nu in RoR2.NetworkUser.readOnlyLocalPlayersList) { if (nu != null) { localName = nu.userName ?? ""; break; } } } catch { }
        }
        return localName;
    }
}

// Where the mod keeps its files (set by the plugin before any component runs):
//   DataDir  = BepInEx\config\JV94.Fishing  (rod owners, case items, gadget/glow owners, inventory slots, zones, sandbox snapshot)
//   ToolsDir = config Dev.ToolsFolder, default DataDir (test_queue.json, test_results.jsonl, vote_candidates.json, votes.jsonl, vote_totals.json)
//   DebugDir = %TEMP%\ror2_fishing_debug (screenshots of the island / sandbox tools; only written on request)
public static class FishingPaths
{
    public static string DataDir = Path.Combine(Path.GetTempPath(), "ror2_fishing_data");
    public static string ToolsDir = "";
    public static string DebugDir = Path.Combine(Path.GetTempPath(), "ror2_fishing_debug");

    public static string Data(string file) { return Path.Combine(DataDir, file); }
    public static string Tools(string file) { return Path.Combine(ToolsRoot(), file); }
    public static string ToolsRoot() { return string.IsNullOrEmpty(ToolsDir) ? DataDir : ToolsDir; }
    public static string Debug(string sub) { return Path.Combine(DebugDir, sub) + Path.DirectorySeparatorChar; }
}
