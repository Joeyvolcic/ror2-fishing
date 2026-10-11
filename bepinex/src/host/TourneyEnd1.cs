// Winner-takes-all ending for the fishing tournament (host-side, reflection only).
//  - Host (local player) types "!endtournament" in chat -> tournament closes, top money earner gets ALL other players' lunar coins.
//  - Anyone types "!tournamentstatus" -> time info + current leader.
//  - Optional auto-end: set TourneyEndN.autoEndMinutes > 0 (minutes after the tournament was first seen). 0 = off (default).
//  - Dry run: set TourneyEndN.dryRunRequests = 1 from a test class -> computes and logs the plan, changes nothing, no chat.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

public static class TourneyEnd1
{
    const string Me = "TourneyEnd1";
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_tourneyend.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static bool enabled = true;
    public static string endCommand = "!endtournament";
    public static string statusCommand = "!tournamentstatus";
    public static float autoEndMinutes = 0f;      // 0 = only the host's chat command ends it
    public static int dryRunRequests;             // set by a test class; processed on the main thread
    public static int realEndRequests;            // same as the host typing the end command
    public static bool ended;
    public static bool lunarCoinPrize = true;     // BepInEx: config Tournament.WinnerTakesLunarCoins

    static Assembly ror2;
    static Type netServer, uObj;
    static Type fishType;
    static float now, seenAt = -1f, nextScan, pendingVerifyAt = -1f;
    static int errors, tick;
    static string lastChatLine;
    static readonly List<string> chatQueue = new List<string>();
    // tie-break tracking: when each player's money last changed (only trusted if we saw the change happen)
    static readonly Dictionary<string, int> lastMoney = new Dictionary<string, int>();
    static readonly Dictionary<string, float> reachedAt = new Dictionary<string, float>();
    static readonly HashSet<string> observedChange = new HashSet<string>();
    static readonly List<KeyValuePair<object, string>> verifyUsers = new List<KeyValuePair<object, string>>();

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
        catch (Exception e) { Log("chat error: " + (e.InnerException ?? e).Message); }
        Log("CHAT " + text);
    }
    static void Chat(string text) { ChatRaw("<color=#ffd700>" + text + "</color>"); }

    public static void Run()
    {
        Assembly core = null;
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetName().Name == "RoR2") ror2 = a;
            if (a.GetName().Name == "UnityEngine.CoreModule") core = a;
            Type t = a.GetType("UnityEngine.Networking.NetworkServer");
            if (t != null) netServer = t;
        }
        uObj = core.GetType("UnityEngine.Object");

        // disable older versions of this feature
        foreach (var a in FishingScope.Assemblies())
            for (int n = 1; n < 50; n++)
            {
                Type t = a.GetType("TourneyEnd" + n);
                if (t == null || t == typeof(TourneyEnd1)) continue;
                FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (en != null && en.FieldType == typeof(bool)) { en.SetValue(null, false); Log("disabled old " + t.Name); }
            }

        Type nu = ror2.GetType("RoR2.NetworkUser");
        Log(Me + " installed. lunarCoins=" + (nu.GetProperty("lunarCoins", All) != null)
            + " Deduct=" + (nu.GetMethod("DeductLunarCoins", All) != null) + " Award=" + (nu.GetMethod("AwardLunarCoins", All) != null)
            + " host=" + netServer.GetProperty("active").GetValue(null, null));
        Action h = Tick;
        ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, h);
        EventInfo cc = ror2.GetType("RoR2.Chat").GetEvent("onChatChanged", All);
        if (cc != null && cc.EventHandlerType == typeof(Action)) { Action c = OnChatChanged; cc.AddEventHandler(null, c); }
        Log("chat hook=" + (cc != null ? cc.EventHandlerType.Name : "none"));
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
        }
        catch { }
    }

    // ---- find the tournament: highest FishingN that has money + tournamentOpen ----
    static void ScanFishing()
    {
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
            for (int n = 9; n < 60; n++)
            {
                Type t; try { t = a.GetType("Fishing" + n); } catch { continue; }
                if (t == null || n <= bestN) continue;
                FieldInfo m = t.GetField("money", BindingFlags.Public | BindingFlags.Static);
                FieldInfo o = t.GetField("tournamentOpen", BindingFlags.Public | BindingFlags.Static);
                if (m != null && o != null && typeof(Dictionary<string, int>).IsAssignableFrom(m.FieldType) && o.FieldType == typeof(bool)) { best = t; bestN = n; }
            }
        if (best != null && best != fishType)
        {
            Log("tournament source -> " + best.Name + " (" + best.Assembly.GetName().Name + ")");
            fishType = best;
            lastMoney.Clear(); reachedAt.Clear(); observedChange.Clear();
            if (seenAt < 0) seenAt = now;
        }
    }
    static Dictionary<string, int> Money() { return fishType == null ? null : fishType.GetField("money", BindingFlags.Public | BindingFlags.Static).GetValue(null) as Dictionary<string, int>; }
    static bool Open() { return fishType != null && (bool)fishType.GetField("tournamentOpen", BindingFlags.Public | BindingFlags.Static).GetValue(null); }
    static void SetOpen(bool v) { fishType.GetField("tournamentOpen", BindingFlags.Public | BindingFlags.Static).SetValue(null, v); }

    static IEnumerable Users() { return (IEnumerable)GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList"); }
    static string UName(object nu) { return Convert.ToString(Get(nu, "userName")); }
    static object FindUser(string name) { foreach (object nu in Users()) if (UName(nu) == name) return nu; return null; }
    static string LocalName()
    {
        foreach (object nu in Users()) { object l = Get(nu, "isLocalPlayer"); if (l is bool && (bool)l) return UName(nu); }
        return null;
    }
    static uint Coins(object nu) { return Convert.ToUInt32(Get(nu, "lunarCoins")); }

    static void Tick()
    {
        now += 0.02f;
        if (!enabled) return;
        try
        {
            if (!(bool)netServer.GetProperty("active").GetValue(null, null)) return;
            if (IsNull(GetStatic(ror2.GetType("RoR2.Run"), "instance"))) return;
            tick++;
            if (now >= nextScan) { nextScan = now + 5f; ScanFishing(); }

            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (fishType == null) return;   // keep queue drained, wait for the tournament mod

            TrackMoney();
            if (lines != null) foreach (string l in lines) HandleChatLine(l);
            while (dryRunRequests > 0) { dryRunRequests--; EndTournament(true, "dry-run request"); }
            while (realEndRequests > 0) { realEndRequests--; EndTournament(false, "injected request"); }
            if (!ended && autoEndMinutes > 0 && seenAt >= 0 && now - seenAt >= autoEndMinutes * 60f) EndTournament(false, "timer (" + autoEndMinutes + " min)");
            if (pendingVerifyAt > 0 && now >= pendingVerifyAt) { pendingVerifyAt = -1f; VerifyBalances(); }
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + (e.InnerException ?? e)); }
    }

    static void TrackMoney()
    {
        var money = Money(); if (money == null) return;
        foreach (var kv in money)
        {
            int prev;
            if (!lastMoney.TryGetValue(kv.Key, out prev)) { lastMoney[kv.Key] = kv.Value; reachedAt[kv.Key] = now; continue; }
            if (prev != kv.Value) { lastMoney[kv.Key] = kv.Value; reachedAt[kv.Key] = now; observedChange.Add(kv.Key); }
        }
    }

    static void HandleChatLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim();
        string text = plain.Substring(i + 2).Trim().ToLowerInvariant();
        if (text == statusCommand) { if (FindUser(name) != null) Status(); return; }
        if (text != endCommand) return;
        if (FindUser(name) == null) return;
        if (name != LocalName()) { Chat("Only the host can end the tournament."); Log(name + " tried " + endCommand + " (refused, not host)"); return; }
        if (ended) { Chat("The tournament has already ended."); return; }
        EndTournament(false, "host chat command by " + name);
    }

    static string Mins(float s) { int t = (int)s; return (t / 60) + "m " + (t % 60).ToString("00") + "s"; }

    static List<KeyValuePair<string, int>> Standings()
    {
        var all = new Dictionary<string, int>(Money());
        foreach (object nu in Users()) { string n = UName(nu); if (!all.ContainsKey(n)) all[n] = 0; }
        var list = new List<KeyValuePair<string, int>>(all);
        list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.Compare(a.Key, b.Key));
        return list;
    }

    static void Status()
    {
        var st = Standings();
        string runTime = "?";
        try { object run = GetStatic(ror2.GetType("RoR2.Run"), "instance"); runTime = Mins(Convert.ToSingle(run.GetType().GetMethod("GetRunStopwatch", All).Invoke(run, null))); } catch { }
        string leader = st.Count == 0 || st[0].Value == 0 ? "nobody yet" : st[0].Key + " ($" + st[0].Value + ")";
        if (st.Count > 1 && st[0].Value > 0 && st[1].Value == st[0].Value) leader += " - TIED with " + st[1].Key;
        string state = ended || !Open() ? "OVER" : "OPEN";
        string end = autoEndMinutes > 0 ? "auto-ends in " + Mins(Math.Max(0, autoEndMinutes * 60f - (now - seenAt))) : "ends when the host types " + endCommand;
        Chat("Tournament " + state + " - running " + Mins(now - seenAt) + " (run time " + runTime + "). Leader: " + leader + ". " + (lunarCoinPrize ? "Winner takes ALL lunar coins; " : "") + end + ".");
    }

    public static void EndTournament(bool dryRun, string reason)
    {
        string tag = dryRun ? "[DRY RUN] " : "";
        Log(tag + "=== END TOURNAMENT (" + reason + ") ===");
        if (!dryRun && ended) { Log("already ended, ignored"); return; }
        if (!dryRun) { ended = true; SetOpen(false); Log("set " + fishType.Name + ".tournamentOpen = false"); }

        var st = Standings();
        foreach (var kv in st) Log(tag + "standing: " + kv.Key + " $" + kv.Value + (FindUser(kv.Key) == null ? " (not connected)" : ""));

        // winners: top score among CONNECTED players
        var connected = new List<KeyValuePair<string, int>>();
        foreach (var kv in st) if (FindUser(kv.Key) != null) connected.Add(kv);
        if (connected.Count == 0) { Log(tag + "no connected players, nothing to do"); return; }
        int top = connected[0].Value;
        var tied = new List<string>();
        foreach (var kv in connected) if (kv.Value == top) tied.Add(kv.Key);
        string tieNote = "";
        if (tied.Count > 1)
        {
            bool allKnown = true;
            foreach (string n in tied) if (!observedChange.Contains(n)) allKnown = false;
            if (allKnown && top > 0)
            {
                tied.Sort((a, b) => reachedAt[a].CompareTo(reachedAt[b]));
                if (reachedAt[tied[0]] < reachedAt[tied[1]]) { tieNote = " (tie at $" + top + " broken: " + tied[0] + " reached it first)"; tied = new List<string> { tied[0] }; }
            }
            if (tied.Count > 1) tieNote = " (TIE at $" + top + " - pot split)";
        }
        Log(tag + "winner(s): " + string.Join(", ", tied.ToArray()) + tieNote);

        // collect
        ulong pot = 0;
        var payers = new List<string>();
        var winnerUsers = new List<object>();
        foreach (string w in tied) winnerUsers.Add(FindUser(w));
        verifyUsers.Clear();
        MethodInfo deduct = ror2.GetType("RoR2.NetworkUser").GetMethod("DeductLunarCoins", All);
        MethodInfo award = ror2.GetType("RoR2.NetworkUser").GetMethod("AwardLunarCoins", All);
        foreach (var kv in connected)
        {
            if (tied.Contains(kv.Key)) continue;
            object nu = FindUser(kv.Key);
            uint before = Coins(nu);
            pot += before;
            payers.Add(kv.Key + " " + before);
            if (before == 0) { Log(tag + "pay: " + kv.Key + " has 0 coins, nothing taken"); continue; }
            if (!dryRun && lunarCoinPrize) { deduct.Invoke(nu, new object[] { before }); verifyUsers.Add(new KeyValuePair<object, string>(nu, kv.Key)); }
            Log(tag + "pay: " + kv.Key + " " + before + " -> 0 (deducted " + before + ")");
        }

        // award (split evenly; remainder goes one coin at a time in standings order)
        int nw = tied.Count;
        for (int i = 0; i < nw; i++)
        {
            object nu = winnerUsers[i];
            uint before = Coins(nu);
            ulong share = pot / (ulong)nw + ((ulong)i < pot % (ulong)nw ? 1UL : 0UL);
            ulong room = uint.MaxValue - (ulong)before;
            if (share > room) { Log(tag + "overflow guard: " + tied[i] + " share " + share + " clamped to " + room); share = room; }
            if (!dryRun && lunarCoinPrize && share > 0) { award.Invoke(nu, new object[] { (uint)share }); verifyUsers.Add(new KeyValuePair<object, string>(nu, tied[i])); }
            Log(tag + "award: " + tied[i] + " " + before + " -> " + ((ulong)before + share) + " (+" + share + ")");
        }
        Log(tag + "total pot " + pot + " lunar coins");

        string winners = string.Join(" & ", tied.ToArray());
        if (dryRun) { Log("[DRY RUN] (no chat, no coins moved; tournamentOpen unchanged = " + Open() + ")"); return; }

        var sb = new List<string>();
        for (int i = 0; i < st.Count; i++) sb.Add((i + 1) + ". " + st[i].Key + " $" + st[i].Value);
        Chat("THE FISHING TOURNAMENT IS OVER! Final standings: " + string.Join(", ", sb.ToArray()));
        if (lunarCoinPrize) Chat("Lunar coins paid: " + (payers.Count > 0 ? string.Join(", ", payers.ToArray()) : "nobody") + ". Total pot: " + pot + ".");
        if (lunarCoinPrize) Chat((nw > 1 ? "TIE! " + winners + " split the pot" : "WINNER: " + winners + " takes it ALL") + " - " + pot + " lunar coins!" + tieNote); else Chat((nw > 1 ? "TIE! Winners: " + winners : "WINNER: " + winners) + "!" + tieNote);
        ChatRaw("[FT] END|" + winners + "|" + pot);
        pendingVerifyAt = now + 6f;
    }

    static void VerifyBalances()
    {
        foreach (var kv in verifyUsers)
            try { Log("verify (6 s later): " + kv.Value + " now has " + Coins(kv.Key) + " lunar coins"); } catch (Exception e) { Log("verify error " + kv.Value + ": " + e.Message); }
    }
}
