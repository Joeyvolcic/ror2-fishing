// RareSound<N> - host add-on (NEXT-RELEASE item 9, request #21): a celebratory game sound on the host's own machine
// when any "[FT] CATCH|name|species|..." line has a red or lunar species. Guests get the same from FISHING.exe.
// Tiers: union of every loaded FishSchool.Red / FishSchool.Lunar table (so new species are picked up), plus the defaults.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RoR2;
using UnityEngine;

public static class RareSound1
{
    public static bool enabled = true;
    public static readonly string[] RedSounds = { "Play_UI_item_land_tier3", "Play_UI_achievementUnlock" };
    public static readonly string[] LunarSounds = { "Play_UI_lunarCache_open", "Play_ui_lunar_coin_drop" };
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static readonly List<string> queue = new List<string>();
    static string lastLine;
    static readonly HashSet<string> red = new HashSet<string> { "Swordfish", "Golden Koi", "Anglerfish" };
    static readonly HashSet<string> lunar = new HashSet<string> { "Moon Eel", "Ghost Fish" };
    static float nextTierScan;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_roulette.log"), DateTime.Now.ToString("HH:mm:ss ") + "[" + typeof(RareSound1).Name + "] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("RareSound") && t != typeof(RareSound1))
                { FieldInfo f = t.GetField("enabled", All); if (f != null && f.FieldType == typeof(bool)) f.SetValue(null, false); }
        ScanTiers();
        Chat.onChatChanged += OnChat;
        RoR2Application.onUpdate += Tick;
        Log("installed; red=" + string.Join(",", new List<string>(red).ToArray()) + " lunar=" + string.Join(",", new List<string>(lunar).ToArray()));
    }

    static void ScanTiers()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("FishSchool")) continue;
                try
                {
                    var r = t.GetField("Red", All); var l = t.GetField("Lunar", All);
                    if (r != null && r.GetValue(null) is string[]) foreach (string s in (string[])r.GetValue(null)) red.Add(s);
                    if (l != null && l.GetValue(null) is string[]) foreach (string s in (string[])l.GetValue(null)) lunar.Add(s);
                }
                catch { }
            }
    }

    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            var log = Chat.readOnlyLog;
            if (log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            if (line.Contains("[FT] CATCH|")) lock (queue) queue.Add(line);
        }
        catch { }
    }

    static void Tick()
    {
        if (!enabled) return;
        string[] lines;
        lock (queue) { if (queue.Count == 0) return; lines = queue.ToArray(); queue.Clear(); }
        if (Time.time >= nextTierScan) { nextTierScan = Time.time + 60f; ScanTiers(); }
        foreach (string line in lines)
        {
            try
            {
                string[] p = line.Substring(line.IndexOf("[FT] CATCH|") + 11).Split('|');
                if (p.Length < 2) continue;
                string species = System.Text.RegularExpressions.Regex.Replace(p[1], "<[^>]*>", "").Trim();
                string[] snd = lunar.Contains(species) ? LunarSounds : red.Contains(species) ? RedSounds : null;
                if (snd == null) continue;
                GameObject go = null;
                var lu = LocalUserManager.GetFirstLocalUser();
                if (lu != null && lu.cachedBody != null) go = lu.cachedBody.gameObject;
                if (go == null && Camera.main != null) go = Camera.main.gameObject;
                var ids = new List<string>();
                foreach (string s in snd) ids.Add(s + "=" + Util.PlaySound(s, go));
                Log("rare catch " + p[0] + " " + species + ": " + string.Join(" ", ids.ToArray()));
            }
            catch (Exception e) { Log("error: " + e.Message); }
        }
    }
}
