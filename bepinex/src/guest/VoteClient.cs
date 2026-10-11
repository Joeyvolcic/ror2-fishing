// VoteClient - guest payload in FISHING.exe: the feature-vote poll card + number-key voting.
//  * [FT] POLL|id|secondsLeft|opt1|opt2|opt3     -> show the card (re-sent by the host every 20 s, so late joiners see it)
//  * [FT] POLLCOUNT|id|secondsLeft|c1|c2|c3      -> live counts + timer correction
//  * [FT] POLLEND|id|winner|c1|c2|c3             -> result for 10 s
//  * While a poll is on screen, pressing 1 / 2 / 3 (not while typing in chat) sends "!vote N" as a normal chat message;
//    the host (FeatureVote<N>) counts it. Only host system lines (no "name: " prefix) are trusted.
// UnityEngine + IMGUI only; RoR2 by reflection. Does nothing on the host. A newer copy replaces an older running one.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public class VoteClientHud : MonoBehaviour
{
    void OnGUI() { VoteClient.OnGui(); }
}

public static class VoteClient
{
    public static bool enabled = true;
    public static bool installed;
    public static int version = 1;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly ror2;
    static Type netServer, chatType, nuType, chatBoxType;
    static readonly List<string> queue = new List<string>();
    static string lastLine;
    static GameObject hudGo;
    static int errors;

    // poll state
    static int pollId = -1, myVote = -1;
    static string[] opts = new string[3];
    static int[] counts = new int[3];
    static float endsAt, resultUntil;
    static bool active;
    static string winner = "";
    static float lastSent = -10f;

    static float Now { get { return Time.unscaledTime; } }
    static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_vote_client.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static bool IsHost() { try { return (bool)netServer.GetProperty("active").GetValue(null, null); } catch { return false; } }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }

    public static void Run()
    {
        try
        {
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }
            foreach (var a in FishingScope.Assemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer");
                if (t != null) netServer = t;
            }
            if (ror2 == null || netServer == null) { Log("game assemblies not found"); return; }
            if (IsHost()) { Log("host detected, the host runs its own FeatureVote add-on"); return; }
            foreach (var asm in FishingScope.Assemblies())
            {
                if (asm == typeof(VoteClient).Assembly) continue;
                Type other = null; try { other = asm.GetType("VoteClient"); } catch { }
                if (other == null) continue;
                FieldInfo inst = other.GetField("installed", All), en = other.GetField("enabled", All), vf = other.GetField("version", All);
                if (inst == null || en == null || !(bool)inst.GetValue(null) || !(bool)en.GetValue(null)) continue;
                int ov = vf != null ? (int)vf.GetValue(null) : 0;
                if (ov >= version) { Log("another copy (version " + ov + ") is already running - nothing to do"); return; }
                en.SetValue(null, false);
                Log("replaced an older copy (version " + ov + " -> " + version + ")");
            }
            chatType = ror2.GetType("RoR2.Chat");
            nuType = ror2.GetType("RoR2.NetworkUser");
            chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            Action c = OnChat; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            Action u = Update; upd.AddEventHandler(null, u);
            installed = true;
            Log("vote client installed (version " + version + ")");
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            if (line.Contains("[FT] POLL")) lock (queue) queue.Add(line);
        }
        catch { }
    }

    static void Update()
    {
        if (!enabled) return;
        try
        {
            if (IsHost()) { active = false; resultUntil = 0f; return; }
            if (hudGo == null)
            {
                hudGo = new GameObject("VoteClientHud");
                UnityEngine.Object.DontDestroyOnLoad(hudGo);
                hudGo.AddComponent<VoteClientHud>();
            }
            string[] lines;
            lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) Handle(l);
            if (active && Now > endsAt + 5f) active = false;     // POLLEND never arrived
        }
        catch (Exception e) { if (errors++ < 20) Log("update error: " + e.Message); }
    }

    static string[] Fields(string line, string tag)
    {
        string plain = Regex.Replace(line.Replace("<noparse>", "").Replace("</noparse>", ""), "<[^>]*>", "").Trim();
        if (!plain.StartsWith(tag)) return null;               // host system lines only (a player's "name: [FT] ..." is ignored)
        return plain.Substring(tag.Length).Split('|');
    }

    static void Handle(string line)
    {
        string[] p; int id, s;
        if ((p = Fields(line, "[FT] POLL|")) != null && p.Length >= 5 && int.TryParse(p[0], out id) && int.TryParse(p[1], out s))
        {
            if (id != pollId) { pollId = id; myVote = -1; counts = new int[3]; Log("poll #" + id + ": " + p[2] + " / " + p[3] + " / " + p[4] + " (" + s + " s)"); }
            opts = new[] { p[2], p[3], p[4] };
            endsAt = Now + s; active = true; resultUntil = 0f;
        }
        else if ((p = Fields(line, "[FT] POLLCOUNT|")) != null && p.Length >= 5 && int.TryParse(p[0], out id) && int.TryParse(p[1], out s))
        {
            if (id != pollId) return;
            for (int i = 0; i < 3; i++) int.TryParse(p[2 + i], out counts[i]);
            endsAt = Now + s;
        }
        else if ((p = Fields(line, "[FT] POLLEND|")) != null && p.Length >= 5 && int.TryParse(p[0], out id))
        {
            if (id != pollId) return;
            winner = p[1];
            for (int i = 0; i < 3; i++) int.TryParse(p[2 + i], out counts[i]);
            active = false; resultUntil = Now + 10f;
            Log("poll #" + id + " ended: " + winner);
        }
    }

    // ---------------------------------------------------------------- voting
    static bool ChatOpen()
    {
        try
        {
            Array boxes = (Array)typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            foreach (object b in boxes)
            {
                PropertyInfo p = chatBoxType.GetProperty("showInput", All);
                if (p != null && (bool)p.GetValue(b, null)) return true;
            }
        }
        catch { }
        return GUIUtility.keyboardControl != 0;
    }

    static object LocalNU()
    {
        IList l = GetStatic(nuType, "readOnlyLocalPlayersList") as IList;
        return l != null && l.Count > 0 ? l[0] : null;
    }

    // a normal "say" chat message, exactly like the game's chat box sends it
    static bool Say(string msg)
    {
        object nu = LocalNU();
        if (nu == null) return false;
        object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
        foreach (MethodInfo m in console.GetType().GetMethods(All))
        {
            ParameterInfo[] ps = m.GetParameters();
            if (m.Name == "SubmitCmd" && ps.Length == 3 && ps[0].ParameterType == nuType)
            { m.Invoke(console, new object[] { nu, "say \"" + msg + "\"", false }); return true; }
        }
        return false;
    }

    static void VoteKey(int n)
    {
        if (!active || n - 1 == myVote || Now - lastSent < 0.7f) return;
        if (Say("!vote " + n)) { myVote = n - 1; lastSent = Now; Log("voted " + n + " in poll #" + pollId); }
    }

    // ---------------------------------------------------------------- poll card (IMGUI)
    static GUIStyle sTitle, sName, sSmall, sCount, sRight;
    static int styleSize;

    public static void OnGui()
    {
        try
        {
            if (!enabled) return;
            bool showResult = !active && Now < resultUntil;
            if (!active && !showResult) return;
            Event e = Event.current;
            if (active && e.type == EventType.KeyDown)
            {
                int k = 0;
                if (e.keyCode == KeyCode.Alpha1 || e.keyCode == KeyCode.Keypad1) k = 1;
                else if (e.keyCode == KeyCode.Alpha2 || e.keyCode == KeyCode.Keypad2) k = 2;
                else if (e.keyCode == KeyCode.Alpha3 || e.keyCode == KeyCode.Keypad3) k = 3;
                if (k > 0 && !ChatOpen() && FishingKeys.FreeFor("vote")) VoteKey(k);
            }
            float sc = Math.Max(0.6f, Screen.height / 1440f);
            int fs = (int)Math.Round(17 * sc);
            if (sTitle == null || styleSize != fs)
            {
                styleSize = fs;
                sName = new GUIStyle(GUI.skin.label) { richText = true, fontSize = fs, wordWrap = false };
                sTitle = new GUIStyle(sName) { fontSize = (int)(fs * 1.2f) };
                sRight = new GUIStyle(sTitle) { alignment = TextAnchor.UpperRight };
                sSmall = new GUIStyle(sName) { fontSize = (int)(fs * 0.8f) };
                sCount = new GUIStyle(sName) { alignment = TextAnchor.MiddleRight };
            }
            float W = 420 * sc, pad = 12 * sc, line = fs + 8 * sc, rh = line + 6 * sc;
            string keyOwner = FishingKeys.Owner();
            bool keysBusy = keyOwner != null && keyOwner != "vote";
            if (keyOwner == "inventory") return;   // the inventory window covers the middle of the screen; the card comes back when it closes
            // left column: the rod shop / merchant panels sit at 24-30 % on the left (470 px wide at 1080p); while one is open
            // the card moves to the right of it
            float panelS = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            float x = keysBusy ? (24f + 470f + 18f) * panelS : 24 * sc, y = Screen.height * 0.26f, inner = W - 2 * pad;
            float H = pad * 2 + line * 2.3f + 3 * (rh + 6 * sc) + line;
            Fill(new Rect(x, y, W, H), new Color(0.05f, 0.07f, 0.12f, 0.88f));
            Fill(new Rect(x, y, W, 3 * sc), new Color(0.4f, 0.8f, 1f, 1f));
            float cy = y + pad;
            int total = counts[0] + counts[1] + counts[2];
            int best = Math.Max(counts[0], Math.Max(counts[1], counts[2]));
            if (active)
            {
                int left = Math.Max(0, (int)Math.Ceiling(endsAt - Now));
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=#66ccff>FEATURE VOTE #" + pollId + "</color></b>", sTitle);
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=" + (left <= 10 ? "#ff6666" : "#ffcc33") + ">" + (left / 60) + ":" + (left % 60).ToString("00") + "</color></b>", sRight);
                cy += line * 1.2f;
                GUI.Label(new Rect(x + pad, cy, inner, line), "<color=#cccccc>What should we build next?</color>", sName);
            }
            else
            {
                GUI.Label(new Rect(x + pad, cy, inner, line * 1.2f), "<b><color=#66ccff>VOTE #" + pollId + " RESULT</color></b>", sTitle);
                cy += line * 1.2f;
                GUI.Label(new Rect(x + pad, cy, inner, line), winner == "none" ? "<color=#cccccc>No votes this time.</color>" : "<color=#ffcc33><b>Winner: " + winner + "</b></color>", sName);
            }
            cy += line * 1.1f;
            for (int i = 0; i < 3; i++)
            {
                bool win = !active && counts[i] > 0 && counts[i] == best;
                Fill(new Rect(x + pad, cy, inner, rh), win ? new Color(1f, 0.8f, 0.2f, 0.25f) : i == myVote ? new Color(0.4f, 0.8f, 1f, 0.22f) : new Color(1f, 1f, 1f, 0.06f));
                if (total > 0) Fill(new Rect(x + pad, cy + rh - 4 * sc, inner * counts[i] / total, 4 * sc), win ? new Color(1f, 0.8f, 0.2f, 1f) : new Color(0.4f, 0.8f, 1f, 0.9f));
                GUI.Label(new Rect(x + pad + 6 * sc, cy, 30 * sc, line), "<b><color=#ffcc33>" + (i + 1) + "</color></b>", sName);
                GUI.Label(new Rect(x + pad + 34 * sc, cy, inner - 90 * sc, line), "<b>" + opts[i] + "</b>" + (i == myVote ? "  <color=#66ccff>(your vote)</color>" : ""), sName);
                GUI.Label(new Rect(x + pad, cy, inner - 8 * sc, line), "<b>" + counts[i] + "</b>", sCount);
                cy += rh + 6 * sc;
            }
            GUI.Label(new Rect(x + pad, cy, inner, line), active ? (keysBusy ? "<color=#aaaaaa>Number keys are busy - type <b>!vote 1-3</b> in chat</color>" : "<color=#aaaaaa>Press <b>1</b> / <b>2</b> / <b>3</b> or type <b>!vote 1-3</b> (you can change it)</color>")
                                                               : "<color=#aaaaaa>" + total + " vote" + (total == 1 ? "" : "s") + " - thanks for voting!</color>", sSmall);
        }
        catch (Exception ex) { if (errors++ < 20) Log("GUI error: " + ex.Message); }
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color; GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
