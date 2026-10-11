// FeatureVote<N> - host add-on for the fishing game: in-game feature polls ("what should we build next?").
//  * Every intervalMinutes (default 12) during a run - never so that it overlaps a tournament's final minute - the host
//    starts a poll with 3 random candidates from C:\RoR2-FishingClient\vote_candidates.json (re-read before every poll;
//    candidates that appeared less often are more likely). Poll length pollSeconds (default 75).
//  * Players vote with "!vote 1|2|3" in chat; JV94 can also press 1 / 2 / 3 while the poll card is on screen.
//    One vote per player, changing it is allowed (last one counts).
//  * Chat announcement + result; IMGUI poll card on the host (options, live counts, countdown, then the result).
//  * Hidden lines for guests' clients (VoteClient in FISHING.exe):
//      [FT] POLL|id|secondsLeft|opt1|opt2|opt3          (start; re-sent every 20 s for late joiners)
//      [FT] POLLCOUNT|id|secondsLeft|c1|c2|c3           (after vote changes, at most once a second, and every 10 s)
//      [FT] POLLEND|id|winner|c1|c2|c3                  (winner = option name, "A & B" on a tie, "none" without votes)
//  * Log: C:\RoR2-FishingClient\votes.jsonl (one JSON object per line: type poll / vote / result);
//    totals per feature: C:\RoR2-FishingClient\vote_totals.json (rebuilt from votes.jsonl; test polls excluded).
//  * Host commands (JV94 only): !poll (start now), !poll off / !poll on (automatic polls), !poll every <min>, !poll status.
//  * Test hooks (main thread): pendingStartSeconds > 0 starts a poll of that length (pendingIsTest marks it as a test),
//    pendingKey = 1..3 acts like the host pressing that number key.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using Path = System.IO.Path;

public class FeatureVote1Hud : MonoBehaviour
{
    void OnGUI() { FeatureVote1.OnGui(); }
}

public static class FeatureVote1
{
    public static bool enabled = true;
    public static bool installed;
    public static bool autoPolls = true;
    public static float intervalMinutes = 12f;
    public static int pollSeconds = 75;           // automatic / !poll length (60-90 s)
    public static float resultSeconds = 10f;      // the result stays on the card this long
    // test hooks
    public static int pendingStartSeconds;
    public static bool pendingIsTest;
    public static int pendingKey;

    static readonly string Dir = FishingPaths.ToolsRoot();   // BepInEx: config Dev.ToolsFolder
    static readonly string CandFile = Path.Combine(Dir, "vote_candidates.json");
    static readonly string VotesFile = Path.Combine(Dir, "votes.jsonl");
    static readonly string TotalsFile = Path.Combine(Dir, "vote_totals.json");
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_vote.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly System.Random rng = new System.Random();
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    class Cand { public string id, name, pitch; }
    class Feature { public string id, name, pitch; public int appearances, votes, wins, ties; }

    // current poll
    static bool active, isTest;
    static int pollId, lastPollId;
    static Cand[] opts;
    static float endsAt, showResultUntil, nextCountAt, nextRepostAt;
    static bool countDirty, reminded;
    static readonly Dictionary<string, int> ballots = new Dictionary<string, int>();
    static int[] finalCounts = new int[3];
    static string finalWinner = "";
    static float nextAutoAt;
    static string trigger = "";

    static readonly List<string> queue = new List<string>();
    static string lastLine;
    static GameObject hudGo;
    static int errors;
    static readonly Dictionary<string, int> appearances = new Dictionary<string, int>();

    class Self { }
    static string Me { get { return typeof(Self).DeclaringType.Name; } }
    static float Now { get { return Time.unscaledTime; } }

    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + Me + "] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }
    static void Blue(string text) { Say("<color=#66ccff>" + text + "</color>"); }

    // ------------------------------------------------------------------ install
    public static void Run()
    {
        try
        {
            if (installed) { enabled = true; Log("already running"); return; }
            foreach (var a in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(a))
                {
                    if (t == typeof(Self).DeclaringType || !t.Name.StartsWith("FeatureVote") || t.Name.Contains("Hud") || t.Name.Contains("Test")) continue;
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) { f.SetValue(null, false); Log("disabled older " + t.Name); }
                }
            Directory.CreateDirectory(Dir);
            lastPollId = ScanVotesFile();
            RebuildTotals();
            nextAutoAt = Now + intervalMinutes * 60f;
            Chat.onChatChanged += OnChat;
            RoR2Application.onUpdate += Tick;
            installed = true;
            Log("installed: auto polls every " + intervalMinutes + " min, " + pollSeconds + " s each, last poll id " + lastPollId + ", candidates " + LoadCandidates().Count);
        }
        catch (Exception e) { Log("Run error: " + e); throw; }
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
            lock (queue) queue.Add(line);
        }
        catch { }
    }

    // ------------------------------------------------------------------ per frame (main thread)
    static void Tick()
    {
        if (!enabled) return;
        try
        {
            if (hudGo == null)
            {
                hudGo = new GameObject("FeatureVoteHud_" + Me);
                UnityEngine.Object.DontDestroyOnLoad(hudGo);
                hudGo.AddComponent<FeatureVote1Hud>();
            }
            string[] lines;
            lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            if (!NetworkServer.active) return;
            foreach (string l in lines) Handle(l);

            if (pendingStartSeconds > 0) { int s = pendingStartSeconds; pendingStartSeconds = 0; bool t = pendingIsTest; pendingIsTest = false; StartPoll(s, t, t ? "test" : "hook"); }
            if (pendingKey > 0) { int k = pendingKey; pendingKey = 0; HostKey(k); }

            if (active)
            {
                float left = endsAt - Now;
                if (left <= 0f) { EndPoll(); return; }
                if (!reminded && left <= 20f && endsAt - Now < SecondsTotal() - 15f) { reminded = true; Blue("20 s left to vote! " + CountText() + "  (!vote 1/2/3)"); }
                if ((countDirty && Now >= nextCountAt - 9f) || Now >= nextCountAt) SendCount();
                if (Now >= nextRepostAt) { nextRepostAt = Now + 20f; Say(PollLine()); }
            }
            else if (autoPolls && Now >= nextAutoAt)
            {
                if (RoR2.Run.instance == null) { nextAutoAt = Now + 30f; return; }
                if (TourneyFinalStretch(pollSeconds)) { nextAutoAt = Now + 30f; Log("auto poll postponed: tournament final minute"); return; }
                StartPoll(pollSeconds, false, "auto");
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    static float pollLength;
    static float SecondsTotal() { return pollLength; }

    // true while the tournament is in (or would be in, within needSeconds) its final minute
    static bool TourneyFinalStretch(float needSeconds)
    {
        try
        {
            Type te = Newest("TourneyEnd");
            if (te == null) return false;
            bool ended = (bool)te.GetField("ended", All).GetValue(null);
            float auto = Convert.ToSingle(te.GetField("autoEndMinutes", All).GetValue(null));
            float seen = Convert.ToSingle(te.GetField("seenAt", All).GetValue(null));
            float now = Convert.ToSingle(te.GetField("now", All).GetValue(null));
            if (ended || auto <= 0f || seen < 0f) return false;
            float left = auto * 60f - (now - seen);
            return left > 0f && left < needSeconds + 60f;
        }
        catch { return false; }
    }

    static Type Newest(string prefix)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t.Name.StartsWith(prefix) && int.TryParse(t.Name.Substring(prefix.Length), out n) && n > bn) { best = t; bn = n; }
            }
        return best;
    }

    // ------------------------------------------------------------------ chat commands
    static string HostName()
    {
        var l = NetworkUser.readOnlyLocalPlayersList;
        return l != null && l.Count > 0 && l[0] != null ? l[0].userName : null;
    }

    static void Handle(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim(), text = plain.Substring(i + 2).Trim();
        if (!text.StartsWith("!")) return;
        string[] tok = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string cmd = tok[0].ToLowerInvariant();
        if (cmd != "!vote" && cmd != "!poll") return;
        NetworkUser user = null;
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu.userName == name) user = nu;
        if (user == null) return;
        if (cmd == "!vote")
        {
            int n;
            if (tok.Length < 2 || !int.TryParse(tok[1].Trim('.', '!', '#'), out n)) { if (active) Blue("Vote with !vote 1, !vote 2 or !vote 3."); return; }
            Vote(name, n, "chat");
            return;
        }
        if (name != HostName()) return;                       // !poll is JV94's
        string arg = tok.Length > 1 ? tok[1].ToLowerInvariant() : "";
        if (arg == "off") { autoPolls = false; Blue("Automatic feature votes are OFF (!poll on to resume)."); Log("auto polls off"); }
        else if (arg == "on") { autoPolls = true; nextAutoAt = Now + intervalMinutes * 60f; Blue("Automatic feature votes are ON: one every " + intervalMinutes + " min."); Log("auto polls on"); }
        else if (arg == "every" && tok.Length > 2)
        {
            float m;
            if (float.TryParse(tok[2], out m) && m >= 1f && m <= 120f) { intervalMinutes = m; nextAutoAt = Now + m * 60f; Blue("Feature votes now every " + m + " min."); Log("interval " + m); }
        }
        else if (arg == "status")
            Blue("Feature votes: auto " + (autoPolls ? "ON, next in " + Math.Max(0, (int)((nextAutoAt - Now) / 60f + 0.5f)) + " min" : "OFF") + ", every " + intervalMinutes + " min" + (active ? ", poll #" + pollId + " running" : "") + ".");
        else if (arg == "")
        {
            if (active) Blue("A feature vote is already running.");
            else StartPoll(pollSeconds, false, "!poll");
        }
        else Blue("!poll | !poll on | !poll off | !poll every <minutes> | !poll status");
    }

    static void HostKey(int n)
    {
        string me = HostName();
        if (me != null) Vote(me, n, "key");
    }

    static void Vote(string name, int n, string via)
    {
        if (!active) return;
        if (n < 1 || n > 3) { Blue("Vote with !vote 1, !vote 2 or !vote 3."); return; }
        int prev;
        bool had = ballots.TryGetValue(name, out prev);
        if (had && prev == n - 1) return;
        ballots[name] = n - 1;
        countDirty = true;
        Append("{\"type\":\"vote\",\"poll\":" + pollId + ",\"time\":" + JQ(Stamp()) + ",\"player\":" + JQ(name) + ",\"option\":" + n
            + ",\"feature\":" + JQ(opts[n - 1].id) + ",\"changed\":" + (had ? "true" : "false") + ",\"via\":" + JQ(via) + (isTest ? ",\"test\":true" : "") + "}");
        Log("vote #" + pollId + " " + name + " -> " + n + " (" + opts[n - 1].name + ")" + (had ? " changed from " + (prev + 1) : "") + " via " + via);
    }

    // ------------------------------------------------------------------ poll lifecycle
    static void StartPoll(int seconds, bool test, string why)
    {
        if (active) return;
        var cands = LoadCandidates();
        if (cands.Count < 3) { Log("not enough candidates (" + cands.Count + ") in " + CandFile); nextAutoAt = Now + 60f; return; }
        opts = Pick(cands, 3);
        pollId = ++lastPollId;
        isTest = test; trigger = why;
        pollLength = seconds;
        endsAt = Now + seconds;
        ballots.Clear();
        active = true; reminded = false; countDirty = false;
        nextCountAt = Now + 10f; nextRepostAt = Now + 20f;
        showResultUntil = 0f;
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"poll\",\"poll\":").Append(pollId).Append(",\"time\":").Append(JQ(Stamp())).Append(",\"seconds\":").Append(seconds)
          .Append(",\"trigger\":").Append(JQ(why)).Append(",\"test\":").Append(test ? "true" : "false").Append(",\"options\":[");
        for (int i = 0; i < 3; i++) sb.Append(i > 0 ? "," : "").Append(JQ(opts[i].id));
        sb.Append("],\"names\":[");
        for (int i = 0; i < 3; i++) sb.Append(i > 0 ? "," : "").Append(JQ(opts[i].name));
        sb.Append("]}");
        Append(sb.ToString());
        if (!test) { foreach (var o in opts) { int a; appearances.TryGetValue(o.id, out a); appearances[o.id] = a + 1; } }
        Say(PollLine());
        Say("<color=#66ccff><b>FEATURE VOTE" + (test ? " (test)" : "") + "!</b> Which should we build next? Type <b>!vote 1</b>, <b>2</b> or <b>3</b> (" + seconds + " s, you can change your vote)</color>");
        for (int i = 0; i < 3; i++) Say("<color=#ffcc33>" + (i + 1) + ") " + opts[i].name + "</color> <color=#cccccc>- " + opts[i].pitch + "</color>");
        Log("poll #" + pollId + " started (" + why + (test ? ", TEST" : "") + ", " + seconds + " s): " + opts[0].id + " / " + opts[1].id + " / " + opts[2].id);
        nextAutoAt = float.MaxValue;
    }

    static string PollLine()
    {
        int left = Math.Max(0, (int)Math.Ceiling(endsAt - Now));
        return "[FT] POLL|" + pollId + "|" + left + "|" + Safe(opts[0].name) + "|" + Safe(opts[1].name) + "|" + Safe(opts[2].name);
    }

    static int[] Counts()
    {
        var c = new int[3];
        foreach (var kv in ballots) c[kv.Value]++;
        return c;
    }

    static string CountText()
    {
        int[] c = Counts();
        return opts[0].name + " " + c[0] + " · " + opts[1].name + " " + c[1] + " · " + opts[2].name + " " + c[2];
    }

    static void SendCount()
    {
        int[] c = Counts();
        Say("[FT] POLLCOUNT|" + pollId + "|" + Math.Max(0, (int)Math.Ceiling(endsAt - Now)) + "|" + c[0] + "|" + c[1] + "|" + c[2]);
        countDirty = false; nextCountAt = Now + 10f;
    }

    static void EndPoll()
    {
        active = false;
        int[] c = Counts();
        int best = Math.Max(c[0], Math.Max(c[1], c[2]));
        var win = new List<int>();
        if (best > 0) for (int i = 0; i < 3; i++) if (c[i] == best) win.Add(i);
        string winner = win.Count == 0 ? "none" : string.Join(" & ", win.ConvertAll(i => opts[i].name).ToArray());
        finalCounts = c; finalWinner = winner;
        showResultUntil = Now + resultSeconds;
        int total = c[0] + c[1] + c[2];

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"result\",\"poll\":").Append(pollId).Append(",\"time\":").Append(JQ(Stamp())).Append(",\"test\":").Append(isTest ? "true" : "false").Append(",\"options\":[");
        for (int i = 0; i < 3; i++) sb.Append(i > 0 ? "," : "").Append(JQ(opts[i].id));
        sb.Append("],\"counts\":[").Append(c[0]).Append(',').Append(c[1]).Append(',').Append(c[2]).Append("],\"winners\":[");
        for (int i = 0; i < win.Count; i++) sb.Append(i > 0 ? "," : "").Append(JQ(opts[win[i]].id));
        sb.Append("],\"ballots\":{");
        bool first = true;
        foreach (var kv in ballots) { sb.Append(first ? "" : ",").Append(JQ(kv.Key)).Append(':').Append(JQ(opts[kv.Value].id)); first = false; }
        sb.Append("}}");
        Append(sb.ToString());

        Say("[FT] POLLEND|" + pollId + "|" + Safe(winner) + "|" + c[0] + "|" + c[1] + "|" + c[2]);
        string tally = "(" + CountText() + ")";
        if (win.Count == 0) Blue("Feature vote #" + pollId + " closed with no votes. Next time!");
        else if (win.Count == 1) Say("<color=#66ccff><b>Feature vote #" + pollId + " result:</b></color> <color=#ffcc33><b>" + winner + "</b></color> <color=#66ccff>wins with " + best + " of " + total + " vote" + (total == 1 ? "" : "s") + "! " + tally + " Thanks, this helps decide what gets built next.</color>");
        else Say("<color=#66ccff><b>Feature vote #" + pollId + " result:</b> a tie between </color><color=#ffcc33><b>" + winner + "</b></color><color=#66ccff>! " + tally + " Thanks for voting!</color>");
        Log("poll #" + pollId + " ended: " + c[0] + "/" + c[1] + "/" + c[2] + " winner " + winner + (isTest ? " (TEST)" : ""));
        RebuildTotals();
        nextAutoAt = Now + intervalMinutes * 60f;
    }

    static string Safe(string s) { return (s ?? "").Replace("|", "/").Replace("<", "").Replace(">", ""); }

    static Cand[] Pick(List<Cand> cands, int k)
    {
        var pool = new List<Cand>(cands);
        var res = new Cand[k];
        for (int n = 0; n < k; n++)
        {
            double sum = 0; var w = new double[pool.Count];
            for (int i = 0; i < pool.Count; i++) { int a; appearances.TryGetValue(pool[i].id, out a); w[i] = 1.0 / ((1 + a) * (1 + a)); sum += w[i]; }
            double r = rng.NextDouble() * sum; int pick = pool.Count - 1;
            for (int i = 0; i < pool.Count; i++) { r -= w[i]; if (r <= 0) { pick = i; break; } }
            res[n] = pool[pick]; pool.RemoveAt(pick);
        }
        return res;
    }

    // ------------------------------------------------------------------ files
    static string Stamp() { return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"); }

    static void Append(string json)
    {
        try { File.AppendAllText(VotesFile, json + "\n", Utf8); }
        catch (Exception e) { Log("could not write votes.jsonl: " + e.Message); }
    }

    static string ReadAll(string path)
    {
        if (!File.Exists(path)) return "";
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs, Encoding.UTF8)) return rd.ReadToEnd();
    }

    static List<Cand> LoadCandidates()
    {
        var list = new List<Cand>();
        try
        {
            string s = ReadAll(CandFile);
            int at = s.IndexOf("\"candidates\"");
            if (at < 0) return list;
            foreach (Match m in Regex.Matches(s.Substring(at), "\\{[^{}]*\\}"))
            {
                string o = m.Value;
                if (Regex.IsMatch(o, "\"active\"\\s*:\\s*false")) continue;
                string id = JStr(o, "id"), name = JStr(o, "name");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                list.Add(new Cand { id = id, name = name, pitch = JStr(o, "pitch") ?? "" });
            }
        }
        catch (Exception e) { Log("candidates read error: " + e.Message); }
        return list;
    }

    // returns the highest poll id in votes.jsonl; fills appearances (non-test polls)
    static int ScanVotesFile()
    {
        int max = 0;
        appearances.Clear();
        foreach (string line in ReadAll(VotesFile).Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            int id; int.TryParse(JNum(line, "poll") ?? "0", out id);
            if (id > max) max = id;
            if (JStr(line, "type") == "poll" && !IsTestLine(line))
                foreach (string o in JArr(line, "options")) { int a; appearances.TryGetValue(o, out a); appearances[o] = a + 1; }
        }
        return max;
    }

    static bool IsTestLine(string line) { return Regex.IsMatch(line, "\"test\"\\s*:\\s*true"); }

    // vote_totals.json: every candidate (+ anything seen in votes.jsonl), from the non-test polls
    static void RebuildTotals()
    {
        try
        {
            var feats = new Dictionary<string, Feature>();
            var order = new List<string>();
            foreach (var c in LoadCandidates()) { feats[c.id] = new Feature { id = c.id, name = c.name, pitch = c.pitch }; order.Add(c.id); }
            Func<string, Feature> F = id => { Feature f; if (!feats.TryGetValue(id, out f)) { f = new Feature { id = id, name = id, pitch = "" }; feats[id] = f; order.Add(id); } return f; };
            int polls = 0, ballotsCast = 0;
            var players = new SortedDictionary<string, int>();
            foreach (string line in ReadAll(VotesFile).Split('\n'))
            {
                if (line.Trim().Length == 0 || IsTestLine(line)) continue;
                string type = JStr(line, "type");
                if (type == "poll")
                {
                    var ids = JArr(line, "options"); var names = JArr(line, "names");
                    for (int i = 0; i < ids.Count; i++) { var f = F(ids[i]); f.appearances++; if (f.name == f.id && i < names.Count) f.name = names[i]; }
                }
                else if (type == "result")
                {
                    polls++;
                    var ids = JArr(line, "options"); var counts = JArr(line, "counts"); var wins = JArr(line, "winners");
                    for (int i = 0; i < ids.Count && i < counts.Count; i++) { int n; int.TryParse(counts[i], out n); F(ids[i]).votes += n; ballotsCast += n; }
                    foreach (string w in wins) { if (wins.Count == 1) F(w).wins++; else F(w).ties++; }
                    Match b = Regex.Match(line, "\"ballots\"\\s*:\\s*\\{([^}]*)\\}");
                    if (b.Success) foreach (Match p in Regex.Matches(b.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:")) { string who = Unesc(p.Groups[1].Value); int k; players.TryGetValue(who, out k); players[who] = k + 1; }
                }
            }
            // feature tracker shape: {"<id>": {"name": "...", "votes": N, "polls": N, "wins": N}}  (polls = appearances in a poll)
            var sb = new StringBuilder("{\n");
            for (int i = 0; i < order.Count; i++)
            {
                var f = feats[order[i]];
                sb.Append("  ").Append(JQ(f.id)).Append(": {\"name\": ").Append(JQ(f.name)).Append(", \"votes\": ").Append(f.votes)
                  .Append(", \"polls\": ").Append(f.appearances).Append(", \"wins\": ").Append(f.wins).Append("}").Append(i + 1 < order.Count ? ",\n" : "\n");
            }
            sb.Append("}\n");
            var list = order;
            string tmp = TotalsFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Utf8);
            if (File.Exists(TotalsFile)) File.Delete(TotalsFile);
            File.Move(tmp, TotalsFile);
            Log("vote_totals.json rebuilt: " + polls + " poll(s), " + ballotsCast + " ballot(s), " + list.Count + " feature(s)");
        }
        catch (Exception e) { Log("totals error: " + e.Message); }
    }

    // ---- tiny JSON helpers (flat lines only)
    static string JQ(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? "")
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch < ' ') sb.Append("\\u" + ((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
    static string JNum(string line, string key)
    {
        Match m = Regex.Match(line, "\"" + key + "\"\\s*:\\s*(\\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
    static string JStr(string line, string key)
    {
        Match m = Regex.Match(line, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unesc(m.Groups[1].Value) : null;
    }
    static List<string> JArr(string line, string key)
    {
        var r = new List<string>();
        Match m = Regex.Match(line, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success) return r;
        string body = m.Groups[1].Value;
        if (body.Contains("\"")) foreach (Match s in Regex.Matches(body, "\"((?:[^\"\\\\]|\\\\.)*)\"")) r.Add(Unesc(s.Groups[1].Value));
        else foreach (string p in body.Split(',')) if (p.Trim().Length > 0) r.Add(p.Trim());
        return r;
    }
    static string Unesc(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char e = s[++i];
            if (e == 'n') sb.Append('\n'); else if (e == 't') sb.Append('\t'); else if (e == 'r') sb.Append('\r');
            else if (e == 'u' && i + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
            else sb.Append(e);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ host poll card (IMGUI)
    static GUIStyle sTitle, sName, sPitch, sSmall, sCount;
    static int styleSize;
    static bool drawLogged;

    static bool ChatOpen()
    {
        try
        {
            foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>())
            {
                PropertyInfo p = b.GetType().GetProperty("showInput", All);
                object v = p != null ? p.GetValue(b, null) : (b.GetType().GetField("_showInput", All) != null ? b.GetType().GetField("_showInput", All).GetValue(b) : null);
                if (v is bool && (bool)v) return true;
            }
        }
        catch { }
        return GUIUtility.keyboardControl != 0;
    }

    public static void OnGui()
    {
        try
        {
            if (!enabled || !NetworkServer.active) return;
            bool showResult = !active && Now < showResultUntil && opts != null;
            if (!active && !showResult) return;
            Event e = Event.current;
            if (active && e.type == EventType.KeyDown)
            {
                int k = 0;
                if (e.keyCode == KeyCode.Alpha1 || e.keyCode == KeyCode.Keypad1) k = 1;
                else if (e.keyCode == KeyCode.Alpha2 || e.keyCode == KeyCode.Keypad2) k = 2;
                else if (e.keyCode == KeyCode.Alpha3 || e.keyCode == KeyCode.Keypad3) k = 3;
                if (k > 0 && !ChatOpen()) HostKey(k);
            }

            float sc = Math.Max(0.6f, Screen.height / 1440f);
            int fs = (int)Math.Round(17 * sc);
            if (sTitle == null || styleSize != fs)
            {
                styleSize = fs;
                sName = new GUIStyle(GUI.skin.label) { richText = true, fontSize = fs, wordWrap = false };
                sTitle = new GUIStyle(sName) { fontSize = (int)(fs * 1.2f) };
                sPitch = new GUIStyle(sName) { fontSize = (int)(fs * 0.8f), wordWrap = true };
                sSmall = new GUIStyle(sName) { fontSize = (int)(fs * 0.8f) };
                sCount = new GUIStyle(sName) { alignment = TextAnchor.MiddleRight };
            }
            float W = 470 * sc, pad = 12 * sc, line = fs + 8 * sc;
            float x = 24 * sc, y = Screen.height * 0.26f;
            float inner = W - 2 * pad;
            int[] c = active ? Counts() : finalCounts;
            int total = c[0] + c[1] + c[2];
            string me = HostName();
            int mine = -1; if (active && me != null) ballots.TryGetValue(me, out mine); if (active && (me == null || !ballots.ContainsKey(me))) mine = -1;

            float[] ph = new float[3];
            float rows = 0;
            for (int i = 0; i < 3; i++) { ph[i] = sPitch.CalcHeight(new GUIContent(opts[i].pitch), inner - 34 * sc); rows += line + ph[i] + 8 * sc; }
            float H = pad * 2 + line * 2.3f + rows + line;
            var rect = new Rect(x, y, W, H);
            Fill(rect, new Color(0.05f, 0.07f, 0.12f, 0.88f));
            Fill(new Rect(x, y, W, 3 * sc), new Color(0.4f, 0.8f, 1f, 1f));
            float cy = y + pad;
            if (active)
            {
                int left = Math.Max(0, (int)Math.Ceiling(endsAt - Now));
                string col = left <= 10 ? "#ff6666" : "#ffcc33";
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=#66ccff>FEATURE VOTE #" + pollId + (isTest ? " (test)" : "") + "</color></b>", sTitle);
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=" + col + ">" + (left / 60) + ":" + (left % 60).ToString("00") + "</color></b>", new GUIStyle(sTitle) { alignment = TextAnchor.UpperRight });
                cy += line * 1.2f;
                GUI.Label(new Rect(x + pad, cy, inner, line), "<color=#cccccc>What should we build next?</color>", sName);
            }
            else
            {
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=#66ccff>VOTE #" + pollId + " RESULT</color></b>", sTitle);
                cy += line * 1.2f;
                GUI.Label(new Rect(x + pad, cy, inner, line), finalWinner == "none" ? "<color=#cccccc>No votes this time.</color>" : "<color=#ffcc33><b>Winner: " + finalWinner + "</b></color>", sName);
            }
            cy += line * 1.1f;
            for (int i = 0; i < 3; i++)
            {
                float rh = line + ph[i];
                bool win = !active && c[i] > 0 && c[i] == Math.Max(c[0], Math.Max(c[1], c[2]));
                Fill(new Rect(x + pad, cy, inner, rh), win ? new Color(1f, 0.8f, 0.2f, 0.25f) : i == mine ? new Color(0.4f, 0.8f, 1f, 0.22f) : new Color(1f, 1f, 1f, 0.06f));
                if (total > 0) Fill(new Rect(x + pad, cy + rh - 4 * sc, inner * c[i] / total, 4 * sc), win ? new Color(1f, 0.8f, 0.2f, 1f) : new Color(0.4f, 0.8f, 1f, 0.9f));
                GUI.Label(new Rect(x + pad + 6 * sc, cy, 30 * sc, line), "<b><color=#ffcc33>" + (i + 1) + "</color></b>", sName);
                GUI.Label(new Rect(x + pad + 34 * sc, cy, inner - 90 * sc, line), "<b>" + opts[i].name + "</b>" + (i == mine ? "  <color=#66ccff>(your vote)</color>" : ""), sName);
                GUI.Label(new Rect(x + pad, cy, inner - 8 * sc, line), "<b>" + c[i] + "</b>", sCount);
                GUI.Label(new Rect(x + pad + 34 * sc, cy + line - 4 * sc, inner - 40 * sc, ph[i]), "<color=#aaaaaa>" + opts[i].pitch + "</color>", sPitch);
                cy += rh + 8 * sc;
            }
            GUI.Label(new Rect(x + pad, cy, inner, line), active ? "<color=#aaaaaa>Press <b>1</b> / <b>2</b> / <b>3</b> or type <b>!vote 1-3</b> - " + total + " vote" + (total == 1 ? "" : "s") + " so far</color>"
                                                               : "<color=#aaaaaa>" + total + " vote" + (total == 1 ? "" : "s") + " - thanks! Results saved.</color>", sSmall);
            if (!drawLogged && e.type == EventType.Repaint) { drawLogged = true; Log("poll card drawn at " + (int)x + "," + (int)y + " " + (int)W + "x" + (int)H + " (screen " + Screen.width + "x" + Screen.height + ")"); }
        }
        catch (Exception ex) { if (errors++ < 20) Log("GUI error: " + ex); }
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color; GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
