// Injected into JV94's (host) game. Starts a fresh 10-minute fishing tournament:
// resets every player's tournament money to $0 on the newest Fishing<N>, re-opens it, restarts
// TourneyEnd1's clock with a 10-minute auto-end, and announces it in chat (plus a BOARD line so
// the guests' HUDs reset too). Fish collections / item-bar fish are left alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class StartTourney3
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static float Minutes = 10f;   // BepInEx: config Tournament.Minutes
    static Assembly ror2;
    static Action handler;
    static EventInfo tickEvent;
    static int ticks, readyChecks;
    static object startedFor;                     // BepInEx: the run the last tournament was started for
    public static bool enabled = true;

    static void Log(string s) { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_stuckfix.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "RoR2") ror2 = a;
        tickEvent = ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All);
        handler = OnTick;
        tickEvent.AddEventHandler(null, handler);
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

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    static void Chat(string text)
    {
        Type chat = ror2.GetType("RoR2.Chat");
        object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
        msg.GetType().GetField("baseToken", All).SetValue(msg, text);
        foreach (MethodInfo m in chat.GetMethods(All))
            if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
    }

    // Waits (checking once a second) until a run is active and both mods are loaded, then starts.
    static void OnTick()
    {
        if (++ticks % 50 != 0) return;
        object run = ror2.GetType("RoR2.Run").GetProperty("instance", All).GetValue(null, null);
        if (run == null) { readyChecks = 0; return; }
        if (!enabled || !UnityEngine.Networking.NetworkServer.active || ReferenceEquals(run, startedFor) || Newest("Fishing") == null || Newest("TourneyEnd") == null) return;
        if (++readyChecks < 5) return;   // give the mods ~5 s in the run to set themselves up
        startedFor = run;   // BepInEx: stay subscribed, the next run gets a fresh tournament
        try
        {
            Type fishing = Newest("Fishing"); Log("step: fishing type = " + fishing + " in " + (fishing == null ? "" : fishing.Assembly.GetName().Name));
            FieldInfo mf = fishing.GetField("money", All); Log("step: money field = " + mf + " value=" + (mf == null ? "n/a" : Convert.ToString(mf.GetValue(null))));
            var money = (Dictionary<string, int>)mf.GetValue(null);
            string before = "";
            foreach (var kv in money) before += kv.Key + "=$" + kv.Value + " ";
            var names = new List<string>(money.Keys);
            foreach (string n in names) money[n] = 0;
            fishing.GetField("tournamentOpen", All).SetValue(null, true);
            Log("tournament reset on " + fishing.Name + " (before: " + before.Trim() + ")");

            Type end = Newest("TourneyEnd"); Log("step: end type = " + end);
            foreach (string fn in new[] { "ended", "autoEndMinutes", "seenAt", "now" }) Log("step: field " + fn + " = " + end.GetField(fn, All));
            end.GetField("ended", All).SetValue(null, false);
            end.GetField("autoEndMinutes", All).SetValue(null, Minutes);
            end.GetField("seenAt", All).SetValue(null, end.GetField("now", All).GetValue(null));
            Log(end.Name + ": clock restarted, auto-end in " + Minutes + " min");

            string board = "[FT] BOARD";
            foreach (string n in names) board += "|" + n + ":0";
            Chat("<color=#ffcc33>FISHING TOURNAMENT STARTS NOW! " + Minutes + " minutes - everyone is back to $0. Most money when the timer runs out wins" + (TourneyEnd1.lunarCoinPrize ? " ALL the lunar coins!" : "!") + " (!tournamentstatus for time left)</color>");
            Chat(board);
        }
        catch (Exception e) { Log("tournament start failed: " + (e.InnerException ?? e)); }
    }
}
