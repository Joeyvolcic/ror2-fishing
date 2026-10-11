// Risk of Rain 2 - "Requests for Claude" panel (IMGUI, right side of the screen).
//
// Every player: press F2 to open the panel, type a feature request, Enter (or the Send button) sends it,
// F2 / Esc closes. While the box is open the character ignores keyboard + mouse (see Block()).
//
// Transport = the game's own chat, with tagged lines that this payload hides from the chat box:
//   player -> everyone : "<name>: [REQ] NEW|<text>"     (a normal "say" chat message; name = chat metadata)
//   player -> everyone : "<name>: [REQ] SYNC"           (a guest asks the host for the recent list)
//   host   -> everyone : "[REQ] LIST|<id>|<name>|<status>[: <note>]|<text>"   (system message, no name prefix)
// The HOST (NetworkServer.active, host payload) numbers requests, appends them to C:\RoR2-Requests\requests.jsonl,
// polls C:\RoR2-Requests\status.jsonl (written by Claude) every 2 s and broadcasts status changes.
//
// The same source builds the guest payload (class ReqPanelClient, does nothing on the host) and the host
// payload (class renamed to ReqPanel<N> by the build, so every host injection has a unique name).
// Entry: <Class>.Run()  (safe to call any number of times)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

public class ReqPanelClientHud : UnityEngine.MonoBehaviour
{
    void OnGUI() { ReqPanelClient.OnGui(); }
}

public static class ReqPanelClient
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_requests.log");
    const string Dir = @"C:\RoR2-Requests";
    static readonly string ReqFile = Path.Combine(Dir, "requests.jsonl");
    static readonly string StatusFile = Path.Combine(Dir, "status.jsonl");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const int MaxLen = 200;
    const float RateSeconds = 10f;

    public static bool enabled = true;
    public static bool installed;
    public static int runCount;
    public static string testSend;          // set by a test class; sent on the main thread

    static Assembly ror2, core, rewired;
    static Type netServer, chatType, nuType, runType, uObj, chatBoxType, luMgrType, ctrlType;
    static bool hostMode;
    static int errors;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static float T { get { return (float)clock.Elapsed.TotalSeconds; } }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    class Self { }   // marker: typeof(Self).DeclaringType is this class whatever it is renamed to
    static string Me { get { return typeof(Self).DeclaringType.Name; } }

    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + Me + "] " + s + Environment.NewLine); } catch { } }

    // ---------------------------------------------------------------- reflection helpers
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
    static bool IsHost() { return (bool)netServer.GetProperty("active").GetValue(null, null); }

    // ---------------------------------------------------------------- entry point
    public static void Run()
    {
        runCount++;
        try
        {
            Log("Run() call #" + runCount + " (payload " + typeof(Self).Assembly.GetName().Name + ", installed=" + installed + ")");
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n == "RoR2") ror2 = a;
                if (n == "UnityEngine.CoreModule") core = a;
                if (n == "Rewired_Core") rewired = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer");
                if (t != null) netServer = t;
            }
            if (ror2 == null || core == null || netServer == null) { Log("game assemblies not found"); return; }
            uObj = core.GetType("UnityEngine.Object");
            chatType = ror2.GetType("RoR2.Chat");
            nuType = ror2.GetType("RoR2.NetworkUser");
            runType = ror2.GetType("RoR2.Run");
            chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            luMgrType = ror2.GetType("RoR2.LocalUserManager");
            if (rewired != null) ctrlType = rewired.GetType("Rewired.ControllerType");

            bool clientPayload = Me == "ReqPanel" + "Client";   // split so the host build's rename leaves it alone
            hostMode = IsHost();
            if (clientPayload && hostMode) { Log("host detected - the host runs its own request panel payload; guest panel not started"); return; }

            // other copies (older host versions, or a host version when this is the guest payload)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (Type t in SafeTypes(asm))
                {
                    if (t == typeof(Self).DeclaringType || !t.Name.StartsWith("ReqPanel") || t.Name.Contains("Hud") || t.Name.Contains("Test")) continue;
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    FieldInfo inst = t.GetField("installed", BindingFlags.Public | BindingFlags.Static);
                    if (en == null || inst == null || !(bool)inst.GetValue(null)) continue;
                    if (clientPayload && (bool)en.GetValue(null)) { Log("another request panel (" + t.Name + ") is already running - nothing to do"); return; }
                    if (!clientPayload)
                    {
                        en.SetValue(null, false);
                        MethodInfo sd = t.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                        if (sd != null) try { sd.Invoke(null, null); } catch { }
                        Log("disabled older " + t.Name);
                    }
                }

            if (hostMode) HostInitFiles();

            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            Action c = OnChatChanged; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            if (upd == null) upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All);
            Action u = FrameUpdate; upd.AddEventHandler(null, u);
            installed = true;
            Log("request panel installed: role=" + (hostMode ? "HOST" : "guest") + ", chat hook installed, frame hook=" + upd.Name
                + ", rewired=" + (ctrlType != null) + ", requests known=" + reqs.Count);
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (Type t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
        catch { return new Type[0]; }
    }

    // called by a newer version that takes over
    public static void Shutdown()
    {
        enabled = false;
        try { Unblock(); } catch { }
        open = false;
    }

    // ---------------------------------------------------------------- requests
    class Req { public int id; public string name = "", text = "", status = "waiting", note = "", time = ""; }
    static readonly SortedDictionary<int, Req> reqs = new SortedDictionary<int, Req>();

    static string StatusText(Req r)
    {
        string s = r.status == "approved" ? "approved – in progress" : r.status == "done" ? "done" : r.status == "declined" ? "declined" : "waiting for the host";
        return r.note.Length > 0 ? s + " – " + r.note : s;
    }
    static string StatusColor(string st)
    {
        return st == "approved" ? "#66ccff" : st == "done" ? "#77ff55" : st == "declined" ? "#ff6666" : "#ffd700";
    }

    // ---------------------------------------------------------------- chat in / out
    static readonly List<string> chatQueue = new List<string>();
    static string lastChatLine;
    static bool chatDirty;

    static void OnChatChanged()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastChatLine)) return;
            lastChatLine = line;
            int k = Kind(Plain(line));
            if (k == 0) return;
            chatDirty = true;            // hidden from the chat box next frame (after every other chat handler has seen it)
            if (k == 1) lock (chatQueue) chatQueue.Add(line);
        }
        catch { }
    }

    // chat line -> text without the chat box's own markup
    static string Plain(string raw)
    {
        string s = raw.Replace("<noparse>", "").Replace("</noparse>", "");
        return Regex.Replace(s, "</?color[^>]*>", "").Trim();
    }
    // 1 = request line, 2 = fishing-tournament data line, 0 = normal chat
    static int Kind(string p)
    {
        if (p.StartsWith("[REQ] ") || p.Contains(": [REQ] ")) return 1;
        if (p.StartsWith("[FT] ")) return 2;
        return 0;
    }

    static void HideTaggedLines()
    {
        chatDirty = false;
        List<string> log = GetStatic(chatType, "log") as List<string>;
        if (log == null) return;
        int removed = 0;
        for (int i = log.Count - 1; i >= 0; i--)
            if (log[i] != null && Kind(Plain(log[i])) != 0) { log.RemoveAt(i); removed++; }
        if (removed == 0) return;
        Array boxes = (Array)uObj.GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
        foreach (object b in boxes)
        {
            try
            {
                if (!(bool)Get(b, "enabled")) continue;
                chatBoxType.GetMethod("BuildChat", All).Invoke(b, null);
                MethodInfo sc = chatBoxType.GetMethod("ScrollToBottom", All);
                if (sc != null) sc.Invoke(b, null);
            }
            catch { }
        }
        if (hiddenLogged++ < 3) Log("hid " + removed + " tagged chat line(s) from " + boxes.Length + " chat box(es)");
    }
    static int hiddenLogged;

    static object FindUser(string name)
    {
        foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
            if (Convert.ToString(Get(nu, "userName")) == name) return nu;
        return null;
    }
    static object LocalNU()
    {
        IList l = GetStatic(nuType, "readOnlyLocalPlayersList") as IList;
        return l != null && l.Count > 0 ? l[0] : null;
    }

    // a normal player chat message ("say"), exactly like the game's chat box does it
    static bool Say(string msg)
    {
        object nu = LocalNU();
        if (IsNull(nu)) { Log("say failed: no local network user"); return false; }
        string esc = msg.Replace("\\", "\\\\").Replace("\"", "\\\"");
        object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
        foreach (MethodInfo m in console.GetType().GetMethods(All))
        {
            ParameterInfo[] ps = m.GetParameters();
            if (m.Name == "SubmitCmd" && ps.Length == 3 && ps[0].ParameterType == nuType)
            { m.Invoke(console, new object[] { nu, "say \"" + esc + "\"", false }); return true; }
        }
        Log("say failed: SubmitCmd not found");
        return false;
    }

    // host -> everyone, as a system line (no "name: " prefix, so players can't forge it)
    static void Broadcast(string text)
    {
        try
        {
            object msg = Activator.CreateInstance(chatType.GetNestedType("SimpleChatMessage", All));
            Set(msg, "baseToken", text);
            foreach (MethodInfo m in chatType.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
            Log("broadcast " + text);
        }
        catch (Exception e) { Log("broadcast error: " + (e.InnerException ?? e).Message); }
    }

    static string Clean(string s, int max)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            if (ch == '<') sb.Append('‹');
            else if (ch == '>') sb.Append('›');
            else if (ch == '|') sb.Append('/');
            else if (ch < ' ') sb.Append(' ');
            else sb.Append(ch);
        }
        string r = sb.ToString().Trim();
        return r.Length > max ? r.Substring(0, max) : r;
    }

    static void HandleLine(string raw)
    {
        string p = Plain(raw);
        if (p.StartsWith("[REQ] LIST|"))
        {
            if (hostMode) return;   // the host already has the authoritative list
            string[] f = p.Substring(11).Split(new[] { '|' }, 4);
            int id;
            if (f.Length < 4 || !int.TryParse(f[0], out id)) return;
            Req r; if (!reqs.TryGetValue(id, out r)) { r = new Req { id = id }; reqs[id] = r; }
            r.name = f[1]; r.text = f[3];
            string st = f[2], note = "";
            int c = st.IndexOf(": ");
            if (c > 0) { note = st.Substring(c + 2); st = st.Substring(0, c); }
            r.status = st; r.note = note;
            while (reqs.Count > 60) { foreach (int k in reqs.Keys) { reqs.Remove(k); break; } }
            Log("list update #" + id + " " + r.name + " [" + st + "] " + r.text);
            return;
        }
        int i = p.IndexOf(": [REQ] ");
        if (i <= 0 || !hostMode) return;
        string name = p.Substring(0, i);
        string body = p.Substring(i + 8);
        if (FindUser(name) == null) { Log("ignored request line from unknown sender '" + name + "'"); return; }
        if (body.StartsWith("NEW|")) HostNew(name, body.Substring(4));
        else if (body == "SYNC") HostSync(name);
    }

    // ---------------------------------------------------------------- host side
    static int nextId = 1;
    static readonly Dictionary<string, float> lastNewFrom = new Dictionary<string, float>(), lastSyncFrom = new Dictionary<string, float>();
    static float nextPoll;
    static long statusLen = -1; static DateTime statusTime;

    static void HostInitFiles()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(ReqFile)) File.WriteAllText(ReqFile, "");
            if (!File.Exists(StatusFile)) File.WriteAllText(StatusFile, "");
            foreach (string line in ReadLines(ReqFile))
            {
                int id; if (!int.TryParse(JNum(line, "id"), out id)) continue;
                reqs[id] = new Req { id = id, name = JStr(line, "name") ?? "?", text = Clean(JStr(line, "text"), MaxLen), time = JStr(line, "time") ?? "" };
                if (id >= nextId) nextId = id + 1;
            }
            ApplyStatus(false);
            Log("host files ready in " + Dir + ": " + reqs.Count + " earlier request(s), next id " + nextId);
        }
        catch (Exception e) { Log("host file init error: " + e.Message); }
    }

    static string[] ReadLines(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs, Encoding.UTF8))
            return rd.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    static void HostNew(string name, string text)
    {
        text = Clean(text, MaxLen);
        if (text.Length == 0) return;
        float last;
        if (lastNewFrom.TryGetValue(name, out last) && T - last < RateSeconds - 2f) { Log("rate limit: dropped request from " + name + ": " + text); return; }
        lastNewFrom[name] = T;
        var r = new Req { id = nextId++, name = Clean(name, 64), text = text, time = DateTime.Now.ToString("HH:mm:ss") };
        reqs[r.id] = r;
        try
        {
            File.AppendAllText(ReqFile, "{\"id\":" + r.id + ",\"name\":" + JQ(r.name) + ",\"text\":" + JQ(r.text) + ",\"time\":" + JQ(r.time) + "}\n", new UTF8Encoding(false));
        }
        catch (Exception e) { Log("could not write requests.jsonl: " + e.Message); }
        Log("NEW request #" + r.id + " from " + r.name + ": " + r.text);
        BroadcastReq(r);
    }

    static void HostSync(string name)
    {
        float last;
        if (lastSyncFrom.TryGetValue(name, out last) && T - last < 15f) return;
        lastSyncFrom[name] = T;
        var all = new List<Req>(reqs.Values);
        Log("SYNC for " + name + ": sending " + Math.Min(8, all.Count) + " request(s)");
        for (int i = Math.Max(0, all.Count - 8); i < all.Count; i++) BroadcastReq(all[i]);
    }

    static void BroadcastReq(Req r)
    {
        string st = r.status + (r.note.Length > 0 ? ": " + r.note : "");
        Broadcast("[REQ] LIST|" + r.id + "|" + r.name + "|" + st + "|" + r.text);
    }

    // status.jsonl: {"id":N,"status":"approved|done|declined","note":"..."} - the last line per id wins
    static void ApplyStatus(bool broadcast)
    {
        if (!File.Exists(StatusFile)) return;
        var fi = new FileInfo(StatusFile);
        if (fi.Length == statusLen && fi.LastWriteTimeUtc == statusTime) return;
        statusLen = fi.Length; statusTime = fi.LastWriteTimeUtc;
        var final = new Dictionary<int, string[]>();
        foreach (string line in ReadLines(StatusFile))
        {
            int id; if (!int.TryParse(JNum(line, "id"), out id)) continue;
            string st = (JStr(line, "status") ?? "").Trim().ToLowerInvariant();
            if (st.StartsWith("approved")) st = "approved";
            if (st != "approved" && st != "done" && st != "declined" && st != "waiting") continue;
            final[id] = new[] { st, Clean(JStr(line, "note"), 80) };
        }
        foreach (var kv in final)
        {
            Req r; if (!reqs.TryGetValue(kv.Key, out r)) continue;
            if (r.status == kv.Value[0] && r.note == kv.Value[1]) continue;
            r.status = kv.Value[0]; r.note = kv.Value[1];
            if (broadcast) { Log("status #" + r.id + " -> " + r.status + (r.note.Length > 0 ? " (" + r.note + ")" : "")); BroadcastReq(r); }
        }
    }

    // ---- tiny JSON helpers (flat objects only)
    static string JQ(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
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
        Match m = Regex.Match(line, "\"" + key + "\"\\s*:\\s*\"?(\\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
    static string JStr(string line, string key)
    {
        Match m = Regex.Match(line, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return null;
        string s = m.Groups[1].Value;
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

    // ---------------------------------------------------------------- per frame (main thread)
    static UnityEngine.GameObject hudGo, focusGo;
    static bool showPanel, syncSent;

    static void FrameUpdate()
    {
        if (!enabled) { if (blocked) try { Unblock(); } catch { } return; }
        try
        {
            if (hudGo == null)
            {
                hudGo = new UnityEngine.GameObject("ReqPanelHud_" + Me);
                UnityEngine.Object.DontDestroyOnLoad(hudGo);
                hudGo.AddComponent<ReqPanelClientHud>();
                focusGo = new UnityEngine.GameObject("ReqPanelFocus_" + Me);
                UnityEngine.Object.DontDestroyOnLoad(focusGo);
                Log("HUD object created");
            }
            if (chatDirty) HideTaggedLines();
            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (lines != null) foreach (string l in lines) HandleLine(l);

            showPanel = GetStatic(runType, "instance") != null;
            if (testSend != null) { string t = testSend; testSend = null; Log("TEST send: " + t); SendRequest(t, true); }
            if (hostMode && T >= nextPoll) { nextPoll = T + 2f; ApplyStatus(true); }
            if (!hostMode && !syncSent && !IsNull(LocalNU())) { syncSent = true; Say("[REQ] SYNC"); Log("asked the host for the request list"); }

            if (open) Block();
            else if (blocked && (T - closedAt > 1f || !KeyHeld()))
                Unblock();
        }
        catch (Exception e) { if (errors++ < 10) Log("frame error: " + (e.InnerException ?? e)); }
    }

    static bool KeyHeld()
    {
        foreach (int vk in new[] { 0x1B, 0x0D, 0x71 })   // Esc, Enter, F2
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    // ---------------------------------------------------------------- input blocking while typing
    // 1) the local user's MPEventSystem gets a dummy selected GameObject -> LocalUser.isUIFocused is true ->
    //    PlayerCharacterMasterController.CanSendBodyInput() returns false (same mechanism as the game's chat box);
    // 2) the Rewired player's enabled Keyboard + Mouse maps are switched off (no pause on Esc, no scoreboard, no skills
    //    from a click) and exactly those maps are switched back on when the panel closes;
    // 3) the event system's cursorOpenerCount is raised by one so the mouse cursor can click the Send button.
    static bool blocked, cursorAdded;
    static object blockEs;
    static float closedAt;
    static readonly List<object> disabledMaps = new List<object>();
    static int blockErrors;

    static object LocalUserObj()
    {
        MethodInfo m = luMgrType.GetMethod("GetFirstLocalUser", All);
        return m != null ? m.Invoke(null, null) : null;
    }

    static void Block()
    {
        try
        {
            object lu = LocalUserObj();
            if (lu == null) return;
            object es = Get(lu, "eventSystem");
            if (!IsNull(es))
            {
                object cur = Get(es, "currentSelectedGameObject");
                if (!ReferenceEquals(cur, focusGo))
                    es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(UnityEngine.GameObject) }).Invoke(es, new object[] { focusGo });
                if (!cursorAdded)
                {
                    PropertyInfo p = es.GetType().GetProperty("cursorOpenerCount", All);
                    if (p != null) { p.SetValue(es, (int)p.GetValue(es, null) + 1, null); cursorAdded = true; blockEs = es; }
                }
            }
            object player = Get(lu, "inputPlayer");
            object maps = Get(Get(player, "controllers"), "maps");
            if (maps != null && ctrlType != null)
            {
                MethodInfo getAll = null;
                foreach (MethodInfo m in maps.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetAllMaps" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == ctrlType) { getAll = m; break; }
                foreach (string ct in new[] { "Keyboard", "Mouse" })
                {
                    IEnumerable list = getAll.Invoke(maps, new object[] { Enum.Parse(ctrlType, ct) }) as IEnumerable;
                    if (list == null) continue;
                    foreach (object map in list)
                        if ((bool)Get(map, "enabled")) { Set(map, "enabled", false); disabledMaps.Add(map); }
                }
            }
            if (!blocked) Log("input blocked (selection=" + !IsNull(es) + ", cursor=" + cursorAdded + ", maps off=" + disabledMaps.Count + ")");
            blocked = true;
        }
        catch (Exception e) { if (blockErrors++ < 5) Log("block error: " + (e.InnerException ?? e)); blocked = true; }
    }

    static void Unblock()
    {
        int n = disabledMaps.Count;
        foreach (object map in disabledMaps) try { Set(map, "enabled", true); } catch { }
        disabledMaps.Clear();
        try
        {
            if (cursorAdded && !IsNull(blockEs))
            {
                PropertyInfo p = blockEs.GetType().GetProperty("cursorOpenerCount", All);
                p.SetValue(blockEs, Math.Max(0, (int)p.GetValue(blockEs, null) - 1), null);
            }
            object lu = LocalUserObj();
            object es = lu == null ? null : Get(lu, "eventSystem");
            if (!IsNull(es) && ReferenceEquals(Get(es, "currentSelectedGameObject"), focusGo))
                es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(UnityEngine.GameObject) }).Invoke(es, new object[] { null });
        }
        catch (Exception e) { if (blockErrors++ < 5) Log("unblock error: " + (e.InnerException ?? e)); }
        cursorAdded = false; blockEs = null; blocked = false;
        Log("input restored (" + n + " maps back on)");
    }

    // ---------------------------------------------------------------- sending
    static float lastSend = -100f;
    static string feedback = ""; static float feedbackUntil;

    static bool SendRequest(string text, bool bypassRate)
    {
        text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0) return true;
        if (text.Length > MaxLen) text = text.Substring(0, MaxLen);
        float wait = lastSend + RateSeconds - T;
        if (!bypassRate && wait > 0) { Feedback("Please wait " + Math.Ceiling(wait) + " s before the next request."); return false; }
        if (Say("[REQ] NEW|" + text))
        {
            lastSend = T;
            input = "";
            Feedback("Sent! It shows up as \"waiting for the host\" in a moment.");
            Log("sent request: " + text);
            return true;
        }
        Feedback("Could not send (not connected?).");
        return false;
    }
    static void Feedback(string s) { feedback = s; feedbackUntil = T + 5f; }

    // ---------------------------------------------------------------- IMGUI panel
    static bool open, focusNext, drawLogged;
    static string input = "";
    static UnityEngine.GUIStyle label, title, box, field, button;
    static int styleSize;

    static void Open() { open = true; focusNext = true; Log("panel opened"); }
    static void Close()
    {
        if (!open) return;
        open = false; closedAt = T;
        try { UnityEngine.GUIUtility.keyboardControl = 0; } catch { }
        // Enter may also have opened the game's own chat box this frame (it reads Input.GetKeyDown(Return)) - close it again
        try
        {
            Array boxes = (Array)uObj.GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            foreach (object b in boxes) if ((bool)Get(b, "showInput")) Set(b, "showInput", false);
        }
        catch { }
        Log("panel closed");
    }

    public static void OnGui()
    {
        try
        {
            if (!enabled) return;
            UnityEngine.Event e = UnityEngine.Event.current;
            if (e.type == UnityEngine.EventType.KeyDown)
            {
                if (e.keyCode == UnityEngine.KeyCode.F2) { if (open) Close(); else if (showPanel) Open(); e.Use(); }
                else if (open && e.keyCode == UnityEngine.KeyCode.Escape) { Close(); e.Use(); }
                else if (open && (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter))
                { if (SendRequest(input, false)) Close(); e.Use(); }
            }
            if (!showPanel && !open) return;

            float sc = Math.Max(0.6f, UnityEngine.Screen.height / 1440f);
            int fs = (int)Math.Round(15 * sc);
            if (label == null || styleSize != fs)
            {
                styleSize = fs;
                label = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label); label.richText = true; label.wordWrap = true; label.fontSize = fs;
                title = new UnityEngine.GUIStyle(label); title.wordWrap = false;
                box = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.box);
                field = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.textField); field.fontSize = fs;
                button = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.button); button.fontSize = fs;
            }
            float W = UnityEngine.Screen.width, H = UnityEngine.Screen.height;
            float w = 430 * sc, pad = 10 * sc, line = fs + 6 * sc;
            float x = W - w - 12;
            float top = H * 0.58f, maxBottom = H * 0.84f;
            float inner = w - 2 * pad;

            // newest requests that fit
            float budget = (maxBottom - top) - 2 * pad - line * (open ? 3.6f : 1.2f);
            var shown = new List<string>(); var heights = new List<float>();
            var all = new List<Req>(reqs.Values);
            float used = 0;
            for (int i = all.Count - 1; i >= 0 && shown.Count < (open ? 8 : 0); i--)   // collapsed: title line only (2.1 HUD layout)
            {
                Req r = all[i];
                string s = "<b>" + r.name + "</b>: " + r.text + "  <color=" + StatusColor(r.status) + ">[" + StatusText(r) + "]</color>";
                float h = label.CalcHeight(new UnityEngine.GUIContent(s), inner);
                if (used + h > budget && shown.Count > 0) break;
                shown.Insert(0, s); heights.Insert(0, h); used += h;
            }
            if (shown.Count == 0 && open)
            {
                string s = "<color=#aaaaaa>No requests yet - press F2 to ask Claude for a feature.</color>";
                shown.Add(s); heights.Add(label.CalcHeight(new UnityEngine.GUIContent(s), inner)); used = heights[0];
            }
            bool fb = feedback.Length > 0 && T < feedbackUntil;
            float boxH = 2 * pad + line + used + (open ? line * 2.4f : 0) + (fb ? line : 0);
            float y = open ? Math.Min(top, maxBottom - boxH) : maxBottom - boxH;
            var rect = new UnityEngine.Rect(x, y, w, boxH);
            UnityEngine.GUI.Box(rect, "", box);
            UnityEngine.GUI.Box(rect, "", box);   // twice = darker
            float cy = y + pad;
            UnityEngine.GUI.Label(new UnityEngine.Rect(x + pad, cy, inner, line), "<b><color=#66ccff>Requests for Claude</color></b>  <color=#aaaaaa>" + (open ? "(Enter = send, F2/Esc = close)" : "(F2 = new request)") + "</color>", title);
            cy += line;
            for (int i = 0; i < shown.Count; i++) { UnityEngine.GUI.Label(new UnityEngine.Rect(x + pad, cy, inner, heights[i]), shown[i], label); cy += heights[i]; }
            if (fb) { UnityEngine.GUI.Label(new UnityEngine.Rect(x + pad, cy, inner, line), "<color=#ffd700>" + feedback + "</color>", title); cy += line; }
            if (open)
            {
                cy += line * 0.3f;
                float bw = 80 * sc, fh = line * 1.4f;
                UnityEngine.GUI.SetNextControlName("ReqPanelInput");
                input = UnityEngine.GUI.TextField(new UnityEngine.Rect(x + pad, cy, inner - bw - pad, fh), input ?? "", MaxLen, field);
                if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + w - pad - bw, cy, bw, fh), "Send", button)) { if (SendRequest(input, false)) Close(); }
                else if (focusNext || UnityEngine.GUI.GetNameOfFocusedControl() != "ReqPanelInput")
                {
                    UnityEngine.GUI.FocusControl("ReqPanelInput");
                    focusNext = false;
                }
                UnityEngine.GUI.Label(new UnityEngine.Rect(x + pad, cy + fh, inner, line), "<color=#aaaaaa>" + (input ?? "").Length + "/" + MaxLen + "</color>", title);
            }
            if (!drawLogged && e.type == UnityEngine.EventType.Repaint) { drawLogged = true; Log("panel drawn at x=" + (int)x + " y=" + (int)y + " w=" + (int)w + " h=" + (int)boxH + " (screen " + (int)W + "x" + (int)H + ")"); }
        }
        catch (Exception ex) { if (errors++ < 10) Log("GUI error: " + ex); }
    }
}
