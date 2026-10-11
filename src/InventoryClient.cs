// InventoryClient - guest payload in FISHING.exe: the same INVENTORY window as the host (build with InvLook.cs).
//  * Opens with I or by typing !inv (closes with I / Esc / X). On open it asks the host with the chat line "[REQ] INV".
//  * Data: the host (Inventory<N>) broadcasts "[FT] INV|<player>|<part>/<parts>|<entries>" for each player whose inventory
//    changed, and at once after "[REQ] INV" / "!inv". Entries (';'-joined): code[*[slot]]:name:rarity:extra:desc with code
//    r rod, s skin, b bobber, l lure, g gadget, p pet, h charm, i item, c case, f fish (extra = count), $ money.
//    Only host system lines are trusted (a player's "name: [FT] ..." is ignored).
//  * Equipping = the normal chat commands, sent with "say" like the chat box: !equip <name>, !lure <name>|off,
//    !pet <look>|off, !gadget shift|r <name>|off, !charm <name>|off, !eat <fish>, !sell <fish> 1|all, !use <item>.
//  * While open: cursor shown, movement / skills / camera off (so a click never fires the rod). Does nothing on the host.
// UnityEngine + IMGUI only; RoR2 by reflection. A newer copy replaces an older running one.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public class InventoryClientHud : MonoBehaviour
{
    void OnGUI()
    {
        if (!InventoryClient.enabled || InventoryClient.hostMode) return;
        Event e = Event.current;
        if (!InvLook.open && e.type == EventType.KeyDown && e.keyCode == KeyCode.I && InvLook.KeyToggleOk() && InventoryClient.CanOpen())
        { InvLook.MarkToggle(); InventoryClient.OpenWindow("I key"); e.Use(); return; }
        InvLook.OnGUI();
    }
}

public static class InventoryClient
{
    public static bool enabled = true;
    public static bool installed, hostMode;
    public static int version = 1;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly ror2;
    static Type netServer, chatType, nuType, chatBoxType, runType;
    static readonly List<string> queue = new List<string>();
    static string lastLine;
    static GameObject hudGo;
    static int errors;
    static string[] parts; static float lastData = -100f, lastReq = -100f;
    public static string lastSent;   // test

    static float Now { get { return Time.unscaledTime; } }
    static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_inventory_client.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static bool IsHost() { try { return (bool)netServer.GetProperty("active").GetValue(null, null); } catch { return false; } }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }
    static object Get(object o, string n)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(n, All); if (p != null) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(n, All); return f != null ? f.GetValue(o) : null;
    }

    public static void Run()
    {
        try
        {
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer");
                if (t != null) netServer = t;
            }
            if (ror2 == null || netServer == null) { Log("game assemblies not found"); return; }
            if (IsHost()) { Log("host detected, the host runs its own Inventory add-on"); return; }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(InventoryClient).Assembly) continue;
                Type other = null; try { other = asm.GetType("InventoryClient"); } catch { }
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
            runType = ror2.GetType("RoR2.Run");
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            Action c = OnChat; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            Action u = Update; upd.AddEventHandler(null, u);
            InvLook.log = s => Log("ui: " + s);
            installed = true;
            Log("inventory client installed (version " + version + ")");
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
            if (line.Contains("[FT] INV|") || line.Contains("!inv") || line.Contains("!bag")) lock (queue) queue.Add(line);
        }
        catch { }
    }

    static string MyName()
    {
        IList l = GetStatic(nuType, "readOnlyLocalPlayersList") as IList;
        return l != null && l.Count > 0 ? Convert.ToString(Get(l[0], "userName")) : null;
    }

    public static bool CanOpen()
    {
        if (!enabled || hostMode) return false;
        object run = GetStatic(runType, "instance");
        if (run == null || (run is UnityEngine.Object && (UnityEngine.Object)run == null)) return false;
        return !ChatOpen();
    }
    static bool ChatOpen()
    {
        try
        {
            Array boxes = (Array)typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) }).Invoke(null, new object[] { chatBoxType });
            PropertyInfo p = chatBoxType.GetProperty("showInput", All);
            foreach (object b in boxes) if (p != null && (bool)p.GetValue(b, null)) return true;
        }
        catch { }
        return false;
    }

    public static void OpenWindow(string why)
    {
        if (InvLook.open) return;
        InvLook.title = "INVENTORY  <size=" + Mathf.RoundToInt(15 * Mathf.Max(0.75f, Screen.height / 1080f)) + "><color=#8a93a3>" + (MyName() ?? "") + "</color></size>";
        if (Now - lastData > 3f && Now - lastReq > 5f) { lastReq = Now; Say("[REQ] INV"); }
        InvLook.note = lastData < 0 ? "asking the host for your items..." : "";
        InvLook.Open();
        Log("window opened (" + why + ")");
    }

    static void Update()
    {
        if (!enabled) { if (InvLook.open) InvLook.Close("disabled"); InvLook.Unblock(); return; }
        try
        {
            hostMode = IsHost();
            if (hostMode) { if (InvLook.open) InvLook.Close("host"); InvLook.Unblock(); return; }
            if (hudGo == null) { hudGo = new GameObject("InventoryClientHud"); UnityEngine.Object.DontDestroyOnLoad(hudGo); hudGo.AddComponent<InventoryClientHud>(); }
            object run = GetStatic(runType, "instance");
            if (InvLook.open && (run == null || (run is UnityEngine.Object && (UnityEngine.Object)run == null))) InvLook.Close("run ended");
            if (InvLook.open) FishingKeys.Claim("inventory", 3);   // number keys belong to the window while it is open
            InvLook.InputTick();
            string[] lines;
            lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) Handle(l);
            string[][] acts = null;
            lock (InvLook.pending) if (InvLook.pending.Count > 0) { acts = InvLook.pending.ToArray(); InvLook.pending.Clear(); }
            if (acts != null) foreach (string[] a in acts) Act(a);
        }
        catch (Exception e) { if (errors++ < 20) Log("update error: " + e.Message); }
    }

    public static void Handle(string line)
    {
        string plain = Regex.Replace(line.Replace("<noparse>", "").Replace("</noparse>", ""), "<[^>]*>", "").Trim();
        string me = MyName();
        if (me != null && (plain == me + ": !inv" || plain == me + ": !bag")) { if (!InvLook.open && CanOpen()) OpenWindow("!inv"); return; }
        if (!plain.StartsWith("[FT] INV|")) return;   // host system lines only
        string[] p = plain.Substring(9).Split(new[] { '|' }, 3);
        if (p.Length < 3 || p[0] != me) return;
        string[] kn = p[1].Split('/'); int k, n;
        if (kn.Length != 2 || !int.TryParse(kn[0], out k) || !int.TryParse(kn[1], out n) || k < 1 || n < 1 || k > n || n > 50) return;
        if (k == 1 || parts == null || parts.Length != n) parts = new string[n];
        parts[k - 1] = p[2];
        foreach (string s in parts) if (s == null) return;
        string all = string.Concat(parts); parts = null;
        Apply(all);
    }

    static readonly Dictionary<char, string> Kinds = new Dictionary<char, string> {
        { 'r', "rod" }, { 's', "skin" }, { 'b', "bobber" }, { 'l', "lure" }, { 'g', "gadget" }, { 'p', "pet" }, { 'h', "charm" }, { 'i', "item" }, { 'c', "case" }, { 'f', "fish" } };

    public static void Apply(string all)
    {
        var items = new List<InvItem>();
        foreach (string e in all.Split(';'))
        {
            string[] f = e.Split(':');
            if (f.Length < 2 || f[0].Length == 0) continue;
            if (f[0] == "$") { int m; int.TryParse(f[1], out m); InvLook.money = "<color=#ffd24a>$" + m.ToString("#,0") + "</color>"; continue; }
            string kind; if (!Kinds.TryGetValue(f[0][0], out kind)) continue;
            int star = f[0].IndexOf('*');
            var it = new InvItem { kind = kind, name = f[1], rarity = f.Length > 2 ? f[2] : "", extra = f.Length > 3 ? f[3] : "", desc = f.Length > 4 ? f[4].Replace(" / ", "\n") : "", equipped = star >= 0 };
            if (star >= 0 && kind == "gadget") it.slot = f[0].Substring(star + 1).Length > 0 ? f[0].Substring(star + 1) : "utility";
            if (kind == "gadget") { string ln = it.name.ToLowerInvariant(); it.prefSlot = ln.Contains("compass") ? "utility" : ln.Contains("range") ? "special" : null; }
            it.isDefault = it.rarity == "default" && (kind == "rod" || kind == "skin" || kind == "bobber");
            if (kind == "fish") { int c; int.TryParse(it.extra, out c); it.count = c; it.extra = ""; it.actions = new[] { "Eat", "Sell 1", "Sell all" }; }
            if (kind == "item" || kind == "case") it.actions = new[] { kind == "case" ? "Open" : "Use" };
            it.stat = it.name.StartsWith("StatTrak");
            items.Add(it);
        }
        InvLook.items = items;
        InvLook.note = "";
        lastData = Now;
        Log("inventory update: " + items.Count + " item(s)");
    }

    // ---- commands (the same strings the host's Inventory<N> runs for JV94)
    static string Cmd(string[] a)
    {
        if (a[0] == "equip")
        {
            string kind = a[1], name = a[2], slot = a.Length > 3 ? a[3] : null;
            switch (kind)
            {
                case "rod": case "skin": case "bobber": return "!equip " + name;
                case "lure": return "!lure " + name;
                case "pet": return "!pet " + name.ToLowerInvariant();
                case "gadget": return "!gadget " + (slot == "special" ? "r " : slot == "utility" ? "shift " : "") + name;
                case "charm": return "!charm " + name;
            }
        }
        else if (a[0] == "unequip")
        {
            switch (a[1])
            {
                case "rod": { string def = "Stick Rod"; foreach (InvItem it in InvLook.items) if (it.kind == "rod" && it.isDefault) def = it.name; return "!equip " + def; } case "skin": return "!equip Default"; case "bobber": return "!equip Plain";
                case "lure": return "!lure off"; case "pet": return "!pet off"; case "utility": return "!gadget shift off"; case "special": return "!gadget r off"; case "charm": return "!charm off";
            }
        }
        else if (a[0] == "act")
        {
            if (a[1] == "fish") return a[3] == "Eat" ? "!eat " + a[2] : a[3] == "Sell 1" ? "!sell " + a[2] + " 1" : a[3] == "Sell all" ? "!sell " + a[2] + " all" : null;
            return "!use " + a[2];
        }
        return null;
    }
    static float lastCmd = -10f;
    static void Act(string[] a)
    {
        string cmd = Cmd(a);
        if (cmd == null) return;
        if (Now - lastCmd < 0.4f) { InvLook.Status("Slow down a little..."); return; }
        lastCmd = Now;
        bool ok = Say(cmd);
        InvLook.Status(ok ? "Sent: " + cmd : "Couldn't send " + cmd);
        Log((ok ? "sent " : "FAILED ") + cmd);
    }

    // a normal "say" chat message, exactly like the game's chat box sends it
    static bool Say(string msg)
    {
        lastSent = msg;
        try
        {
            IList l = GetStatic(nuType, "readOnlyLocalPlayersList") as IList;
            object nu = l != null && l.Count > 0 ? l[0] : null;
            if (nu == null) return false;
            object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
            foreach (MethodInfo m in console.GetType().GetMethods(All))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "SubmitCmd" && ps.Length == 3 && ps[0].ParameterType == nuType)
                { m.Invoke(console, new object[] { nu, "say \"" + msg.Replace("\"", "'") + "\"", false }); return true; }
            }
        }
        catch (Exception e) { Log("say error: " + e.Message); }
        return false;
    }
}
