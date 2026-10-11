// Guest side of the gameplay features (compiled into the FishingClient payload together with GameplayFx.cs + GoldLook.cs).
// Reads the host's hidden lines and draws everything locally:
//   [FT] EAT|name|species|seconds     eat buffs (panel timer, Pufferfish spikes / Anglerfish lantern + x-ray / Ghost glow).
//                                      Movement buffs (Minnow speed, Salmon triple jump) come from the host as a native
//                                      timed buff / temporary Feathers, which this machine's own stat recalculation applies.
//   [FT] EVENT|name|secondsLeft       weather / time-of-day event (rain + ripples for Rain / Storm)
//   [FT] BOLT|x|y|z                   storm lightning bolt
//   [FT] HOT2|x|y|z|r                 golden far hotspot
//   [FT] POOL|id|x|y|z|r              small pond;  [FT] PCAST|name|x|y|z  a cast that landed in a pond (bobber height)
//   [FT] LURE|name|lure|casts         equipped lure (panel)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

public static class GameplayClient
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static Action<string> Log;
    static void L(string s) { try { if (Log != null) Log("[gameplay] " + s); } catch { } }

    public static string eventName = "";
    public static bool hotspotsHidden;   // set by FishingClient while the CompassClient payload runs (hotspots are found with the compass)
    static float eventEnds;
    static readonly Dictionary<string, float> myBuffs = new Dictionary<string, float>();   // species -> Time.time it ends
    static readonly Dictionary<string, string> buffText = new Dictionary<string, string> {
        { "Minnow", "speed burst" }, { "Salmon", "triple jump" }, { "Pufferfish", "spiky aura" }, { "Anglerfish", "lantern + fish x-ray" },
        { "Ghost Fish", "can't drown" }, { "Golden Koi", "next catch x3" }, { "Moon Eel", "revive charge" }, { "Swordfish", "knockback whip" },
        { "Perch", "+15% reel chance" }, { "Trout", "quick bites" }, { "Clownfish", "lucky (rare x1.3)" }, { "Catfish", "green fish x1.5" },
        { "Bass", "catches +50%" } };
    static string myLure; static int myLureCasts;
    class PCast { public float x, y, z, at; }
    static readonly Dictionary<string, PCast> pcasts = new Dictionary<string, PCast>();

    static float F(string s) { return float.Parse(s, Ci); }

    // returns true when the line was one of ours
    public static bool HandleFT(string[] p, string me)
    {
        try
        {
            switch (p[0])
            {
                case "EAT":
                    if (p.Length < 4) return true;
                    {
                        float secs = F(p[3]);
                        GameplayFx.SetBuff(p[1], p[2], secs);
                        if (p[1] == me) { if (secs > 0) myBuffs[p[2]] = Time.time + secs; else myBuffs.Remove(p[2]); }
                        L("EAT " + p[1] + " " + p[2] + " " + secs + " s" + (p[1] == me ? " (me)" : ""));
                    }
                    return true;
                case "EVENT":
                    if (p.Length < 3) return true;
                    if (p[1] != eventName) L("event " + p[1] + " (" + p[2] + " s left)");
                    eventName = p[1]; eventEnds = Time.time + F(p[2]);
                    GameplayFx.SetEvent(p[1]);
                    return true;
                case "BOLT":
                    if (p.Length >= 4) GameplayFx.LightningBolt(new Vector3(F(p[1]), F(p[2]), F(p[3])));
                    return true;
                case "HOT2":
                    if (p.Length >= 5) GameplayFx.SetHot2(!hotspotsHidden, new Vector3(F(p[1]), F(p[2]), F(p[3])), F(p[4]));
                    return true;
                case "POOL":
                    if (p.Length >= 6) GameplayFx.SetPool(int.Parse(p[1], Ci), new Vector3(F(p[2]), F(p[3]), F(p[4])), F(p[5]));
                    return true;
                case "PCAST":
                    if (p.Length >= 5) pcasts[p[1]] = new PCast { x = F(p[2]), y = F(p[3]), z = F(p[4]), at = Time.time };
                    return true;
                case "LURE":
                    if (p.Length >= 4 && p[1] == me) { myLure = p[2] == "none" ? null : p[2]; myLureCasts = int.Parse(p[3], Ci); }
                    return true;
            }
        }
        catch (Exception e) { L("parse error " + string.Join("|", p) + ": " + e.Message); }
        return false;
    }

    // FishingClient.RigCast hook: a pond cast arrives just before the cast line -> put the bobber on the pond surface
    public static void AdjustCast(string name, ref float tx, ref float ty, ref float tz)
    {
        PCast c;
        if (name == null || !pcasts.TryGetValue(name, out c)) return;
        pcasts.Remove(name);
        if (Time.time - c.at > 4f) return;
        tx = c.x; ty = c.y; tz = c.z;
    }

    static Type nuType;
    static Transform BodyTf(string name)
    {
        try
        {
            if (nuType == null) foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType("RoR2.NetworkUser"); if (t != null) nuType = t; }
            PropertyInfo lp = nuType.GetProperty("readOnlyInstancesList", All);
            object list = lp != null ? lp.GetValue(null, null) : nuType.GetField("readOnlyInstancesList", All).GetValue(null);
            foreach (object nu in (IEnumerable)list)
            {
                if (Convert.ToString(nuType.GetProperty("userName", All).GetValue(nu, null)) != name) continue;
                object master = nuType.GetProperty("master", All).GetValue(nu, null);
                if (master == null || (UnityEngine.Object)master == null) return null;
                object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
                return body == null || (UnityEngine.Object)body == null ? null : ((Component)body).transform;
            }
        }
        catch { }
        return null;
    }

    // every frame from FishingClient.Update (main thread)
    public static void Frame(bool hasWater, float waterY, string me)
    {
        if (GameplayFx.BodyTf == null) { GameplayFx.BodyTf = BodyTf; GameplayFx.Log = s => L("fx: " + s); }
        GameplayFx.localPlayer = me;
        GameplayFx.Frame(hasWater, waterY);
    }

    public static void Clear() { GameplayFx.Clear(); }

    static string Fmt(float s) { int i = Math.Max(0, (int)s); return (i / 60) + ":" + (i % 60).ToString("00"); }

    // panel lines (rich text) for FishingClient.DrawHud
    public static List<string> HudLines()
    {
        var l = new List<string>();
        if (eventName.Length > 0)
        {
            string col = eventName.Contains("Storm") ? "#9fb4ff" : eventName.Contains("Rain") ? "#7fc8ff" : eventName.Contains("Night") ? "#b59cff" : eventName.Contains("Golden") ? "#ffcc55" : "#aaddaa";
            l.Add("Event: <color=" + col + ">" + eventName.Replace("+", " + ") + "</color> <color=#aaaaaa>" + Fmt(eventEnds - Time.time) + "</color>");
        }
        l.Add("Lure: " + (myLure != null ? "<color=#ffd75e>" + myLure + "</color> <color=#aaaaaa>(" + myLureCasts + " casts)</color>" : "<color=#aaaaaa>none (!lures)</color>"));
        foreach (var kv in new List<KeyValuePair<string, float>>(myBuffs))
        {
            float left = kv.Value - Time.time;
            if (left <= 0) { myBuffs.Remove(kv.Key); continue; }
            string t; buffText.TryGetValue(kv.Key, out t);
            l.Add("<color=#7fff7f>Ate " + kv.Key + ": " + (t ?? "buff") + " " + Fmt(left) + "</color>");
        }
        return l;
    }
}
