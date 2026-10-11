// Injected into JV94's (host) game. Two approved player requests, as an add-on to the fishing mod:
//  #4 (player request): 1 in 4 of the configured target's catches spawn a live Magma Worm next to them. Peaceful mode is
//     paused while it's alive (without re-enabling normal spawns); the worm despawns after 30 s.
//  #5 (player request): about 1 in 50 catches (never from the hotspot) turn out to be POOP worth -$1,000,000.
// Reads the fishing mod's "[FT] CATCH|name|species|value|total" chat lines as they arrive.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RoR2;
using UnityEngine;

public static class Pranks2
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static string WormTarget = "";   // BepInEx: config Pranks.MagmaWormTarget ("" = off)
    public static float WormChance = 0.25f, WormSeconds = 30f, PoopChance = 0f;   // BepInEx: config Pranks.*
    const int PoopValue = -1000000;
    public static bool enabled = true;
    static readonly System.Random rng = new System.Random();
    static int seenLog;
    static readonly List<KeyValuePair<CharacterMaster, float>> worms = new List<KeyValuePair<CharacterMaster, float>>();
    static string lastFriendly = "";

    static void Log(string s) { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_pranks.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    static Type Newest(string prefix)
    {
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t.Name.StartsWith(prefix) && int.TryParse(t.Name.Substring(prefix.Length), out n) && n > bestN) { best = t; bestN = n; }
            }
        return best;
    }

    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("Pranks") && t.Name != typeof(Pranks2).Name)
                {
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null) f.SetValue(null, false);
                }
        seenLog = Chat.readOnlyLog.Count;
        RoR2Application.onFixedUpdate += Tick;
        Log("pranks installed (magma worms for " + WormTarget + ", poop " + (PoopChance * 100) + "%)");
    }

    static void Tick()
    {
        if (!enabled || !UnityEngine.Networking.NetworkServer.active) return;
        try
        {
            var log = Chat.readOnlyLog;
            if (log.Count < seenLog) seenLog = 0;            // chat log was trimmed/cleared
            for (int i = seenLog; i < log.Count; i++) Handle(log[i]);
            seenLog = log.Count;
            ExpireWorms();
        }
        catch (Exception e) { Log("tick error: " + e); }
    }

    static void Handle(string line)
    {
        if (!line.Contains("[FT] CATCH|")) { lastFriendly = line; return; }
        string[] p = line.Substring(line.IndexOf("[FT] CATCH|") + 11).Split('|');
        if (p.Length < 4) return;
        string name = p[0], species = p[1];
        if (species == "Old Boot") return;

        // #5 Poop: about 1 in 50 catches, never from inside the hotspot.
        if (rng.NextDouble() < PoopChance && !lastFriendly.ToLower().Contains("hotspot"))
        {
            Type fishing = Newest("Fishing");
            var money = (Dictionary<string, int>)fishing.GetField("money", All).GetValue(null);
            int before; money.TryGetValue(name, out before);
            money[name] = before + PoopValue;
            Say("<color=#8b5a2b>" + name + " reeled in the " + species + "... wait, that's not a fish. It's POOP! -$1,000,000 (now $" + money[name].ToString("N0") + ")</color>");
            Say("[FT] CATCH|" + name + "|Poop|" + PoopValue + "|" + money[name]);
            Log("POOP: " + name + " " + before + " -> " + money[name]);
        }

        // #4 Magma worm for the configured target: 1 in 4 of their catches.
        if (WormTarget != "" && name == WormTarget && rng.NextDouble() < WormChance) SpawnWorm(name);
    }

    static void SpawnWorm(string name)
    {
        CharacterBody target = null;
        foreach (var nu in NetworkUser.readOnlyInstancesList)
            if (nu.userName == name && nu.master != null && nu.master.GetBody() != null) target = nu.master.GetBody();
        if (target == null) return;

        // Pause peaceful mode without bringing normal spawns back.
        SetPeaceful(false);

        var card = LegacyResourcesAPI.Load<SpawnCard>("SpawnCards/CharacterSpawnCards/cscMagmaWorm");
        if (card == null) { Log("no cscMagmaWorm spawn card"); SetPeaceful(true); return; }
        var rule = new DirectorPlacementRule
        {
            placementMode = DirectorPlacementRule.PlacementMode.Approximate,
            minDistance = 8f,
            maxDistance = 30f,
            position = target.footPosition
        };
        var request = new DirectorSpawnRequest(card, rule, RoR2Application.rng) { teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true };
        GameObject spawned = DirectorCore.instance.TrySpawnObject(request);
        if (spawned == null) { Log("magma worm spawn failed near " + target.footPosition); SetPeaceful(true); return; }
        var master = spawned.GetComponent<CharacterMaster>();
        worms.Add(new KeyValuePair<CharacterMaster, float>(master, Time.time + WormSeconds));
        Say("<color=#ff5500>" + name + " hooked something HUGE... it's a MAGMA WORM! RUN! (30 s)</color>");
        Log("magma worm spawned for " + name + " at " + spawned.transform.position);
    }

    static void ExpireWorms()
    {
        if (worms.Count == 0) return;
        for (int i = worms.Count - 1; i >= 0; i--)
        {
            var w = worms[i];
            bool gone = w.Key == null || w.Key.GetBody() == null;
            if (!gone && Time.time < w.Value) continue;
            if (!gone) w.Key.TrueKill();
            worms.RemoveAt(i);
            Say("<color=#ff5500>The Magma Worm burrows back into the lake.</color>");
            Log("magma worm removed");
        }
        if (worms.Count == 0) SetPeaceful(true);
    }

    // Peaceful2: when disabling, first set restoreOnDisable=false so normal spawning stays off.
    static void SetPeaceful(bool on)
    {
        Type peaceful = Newest("Peaceful");
        if (peaceful == null) return;
        FieldInfo restore = peaceful.GetField("restoreOnDisable", All);
        if (!on && restore != null) restore.SetValue(null, false);
        peaceful.GetField("enabled", All).SetValue(null, on);
        Log("peaceful " + (on ? "resumed" : "paused") + " (" + peaceful.Name + ")");
    }
}

