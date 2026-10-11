// Host add-on (JV94's game): owns the fishing game's day/night clock and lights the host's own screen.
//  * DayNight1.hour (0-24) advances by 24 h per DayNight1.dayMinutes real minutes (default 10). Read it from other
//    add-ons (gameplay: time-of-day fishing events) via reflection or DayNight1.Phase ("night"/"morning"/"day"/"sunset").
//  * Broadcasts the hidden line "[FT] TIME|<hour>|<dayMinutes>" every 10 s (and at once when the clock is changed);
//    guests' FISHING.exe interpolate in between and run the same DayNightLook.cs.
//  * Chat on phase changes ("The sun is rising...", "Night falls...").
//  * Host chat commands (typed by the host): "!time <0-24>", "!daylength <minutes>".
//  * Disable: DayNight1.enabled = false (restores the stage look). Log: %TEMP%\ror2_daynight.log
// Run() only subscribes to RoR2Application.onUpdate; all Unity work happens there.
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;

public static class DayNight1
{
    public static bool enabled = true;
    public static float hour = 7f;              // start in the morning
    public static float dayMinutes = 10f;
    public static float broadcastEvery = 10f;
    public static string Phase { get { return DayNightLook.PhaseOf(hour); } }
    public static bool forceBroadcast;

    static bool hooked, wasEnabled = true;
    static float lastReal = -1f, nextBroadcast;
    static string lastPhase;
    static int seenLog, errors;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_daynight.log"), DateTime.Now.ToString("HH:mm:ss ") + "[DayNight1] " + s + Environment.NewLine); } catch { } }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }

    public static void Run()
    {
        try
        {
            DayNightLook.Log = Log;
            if (hooked) { enabled = true; Log("Run() again: enabled"); return; }
            seenLog = Chat.readOnlyLog.Count;
            RoR2Application.onUpdate += Tick;
            hooked = true;
            Log("installed: hour=" + hour + " dayMinutes=" + dayMinutes);
        }
        catch (Exception e) { Log("Run error: " + e); }
    }

    static void Tick()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            float dt = lastReal < 0 ? 0f : Mathf.Min(now - lastReal, 1f);
            lastReal = now;
            if (!enabled)
            {
                if (wasEnabled) { DayNightLook.Restore(); wasEnabled = false; Log("disabled"); }
                return;
            }
            wasEnabled = true;
            hour = Mathf.Repeat(hour + dt * 24f / (Mathf.Max(0.1f, dayMinutes) * 60f), 24f);
            Commands();

            bool server = UnityEngine.Networking.NetworkServer.active && RoR2.Run.instance != null;
            if (server)
            {
                if (forceBroadcast || now >= nextBroadcast)
                {
                    forceBroadcast = false; nextBroadcast = now + broadcastEvery;
                    Say("[FT] TIME|" + hour.ToString("0.000", CI) + "|" + dayMinutes.ToString("0.###", CI));
                }
                string ph = DayNightLook.PhaseOf(hour);
                if (ph != lastPhase)
                {
                    if (lastPhase != null) Announce(ph);
                    Log("phase " + ph + " at " + hour.ToString("0.00"));
                    lastPhase = ph;
                }
            }
            DayNightLook.Update(hour);
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    static void Announce(string ph)
    {
        string t = ph == "morning" ? "The sun is rising over the water..."
                 : ph == "day" ? "Bright daylight. Clear skies over the lake."
                 : ph == "sunset" ? "Golden hour - the sun is setting..."
                 : "Night falls. Strange things stir in the dark water...";
        Say("<color=#ffcc66>" + t + "</color>");
    }

    static string LocalName()
    {
        foreach (var lu in LocalUserManager.readOnlyLocalUsersList) if (lu.currentNetworkUser != null) return lu.currentNetworkUser.userName;
        return null;
    }

    static void Commands()
    {
        var log = Chat.readOnlyLog;
        if (log.Count < seenLog) seenLog = 0;
        if (log.Count == seenLog) return;
        string me = LocalName();
        for (int i = seenLog; i < log.Count; i++)
        {
            string s = Regex.Replace(log[i] ?? "", "<[^>]*>", "").Trim();
            if (me == null || !s.StartsWith(me + ": !")) continue;
            string[] w = s.Substring(me.Length + 2).Trim().Split(' ');
            float v;
            if (w.Length >= 2 && w[0] == "!time" && float.TryParse(w[1], NumberStyles.Float, CI, out v))
            { hour = Mathf.Repeat(v, 24f); forceBroadcast = true; Say("<color=#ffcc66>Time set to " + Clock(hour) + ".</color>"); Log("!time " + v); }
            else if (w.Length >= 2 && w[0] == "!daylength" && float.TryParse(w[1], NumberStyles.Float, CI, out v) && v >= 0.5f && v <= 240f)
            { dayMinutes = v; forceBroadcast = true; Say("<color=#ffcc66>A day now lasts " + v.ToString("0.#", CI) + " minutes.</color>"); Log("!daylength " + v); }
        }
        seenLog = log.Count;
    }

    public static string Clock(float h) { int m = (int)(Mathf.Repeat(h, 24f) * 60f); return (m / 60).ToString("00") + ":" + (m % 60).ToString("00"); }
}
