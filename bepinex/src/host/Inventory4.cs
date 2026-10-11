// Inventory<N> - host add-on for the fishing game: the INVENTORY window (I key or !inv) with equip slots + item grid.
// Build with InvLook.cs (agents\inventory\inject.ps1 -Name Inventory4).
//
//  * Everything is read live from the other add-ons (nothing is copied or saved twice):
//      rods / rod skins / bobbers  newest Fishing<N>.RodCatalog + rodOwners (rod_owners.json) - incl. the merchant's case
//                                  items (rarity from CaseData, wear from FishMerchant<N>.caseItems, StatTrak counter from
//                                  CaseSkinLook.statCounts)
//      lures                       Fishing<N>.lureUses / lureEquipped (+ the private Lures table for text and price)
//      pet                         DronePets<N>.pets (4 looks once you own a pet - changing the look is free)
//      fish                        Fishing<N>.collections (the carrier items in the item bar) - Eat / Sell 1 / Sell all
//      gadgets / charms / items    the registry below (+ the built-in Flashlight<N> gadget, host only)
//  * Equipping goes through the SAME chat commands everyone uses, called directly as that player:
//      !equip <name> -> Fishing<N>.RodCommand   !lure <name>|off -> Fishing<N>.GpCommand   !pet <look>|off -> DronePets<N>.HandleLine
//      !eat <fish> -> Fishing<N>.Eat            !sell <fish> 1|all -> FishMerchant<N>.HandleLine
//      !gadget <name>|off, !charm <name>|off, !use <name> -> this add-on (registry)
//    Unequip = the default item (Basic Rod / Default / Plain) or "off". If a method is missing the command is said in chat.
//  * Guests: the host broadcasts "[FT] INV|<player>|<part>/<parts>|<entries>" when a player's inventory changes (max every 8 s
//    each) and at once on "[REQ] INV" or "!inv" from that player. Entry = code[*]:name:rarity:extra:desc, joined by ';'
//    (code r rod, s skin, b bobber, l lure, g gadget, p pet, h charm, i item, c case, f fish, $ money; * = equipped, for gadgets
//    followed by the slot: g*utility = on Shift, g*special = on R;
//    ':' ';' '|' removed from the text fields). Guests equip by sending the chat commands above (InventoryClient).
//  * REGISTRY for other add-ons (call by reflection on the newest enabled Inventory<N>; re-register when a newer one loads,
//    or expose a provider - see STOPPING-POINT.txt):
//      RegisterItem(kind, id, name, rarity, owner, desc)                                      plain item, slot state kept here
//      RegisterItemEx(kind, id, name, rarity, owner, desc, Func<string,bool> owns, Func<string,bool> isEquipped,
//                     Action<string,bool> setEquipped, Action<string> use, string useCommand)  full form (any arg may be null)
//      UnregisterItem(kind, id)   SetExtra(owner, id, text)   Equipped(owner, slotKind) -> id or null
//    kind: gadget | charm | item (usable, e.g. a book: "Use" button) | case | rod | skin | bobber | lure | pet
//    owner: player name, or "*" for everyone (owns callback wins when given). rarity: default common uncommon rare legendary
//    lunar boss blue purple pink red gold or #rrggbb.
//  * While the window is open the host's own left/right click fishing/whip is ignored through ReqPanel20.open (Fishing<N>
//    asks the highest ReqPanel<1-20> type - the FIRST loaded one of that number - for an "open" bool before acting on a click).
//    The enabled Inventory<N> writes every loaded ReqPanel19 / ReqPanel20 shim; disabled versions stop writing (Inventory1,
//    the first version, keeps mirroring ReqPanel20 into its ReqPanel19, which is harmless).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

// Fishing<N>.InputBlocked() reads the "open" bool of the highest ReqPanel<1-20> type: this shim makes it see the inventory.
// Future Inventory<N> versions keep this name (ReqPanel20) and write all copies.
public static class ReqPanel20
{
    public static bool open;
}

public class Inventory4Gui : MonoBehaviour
{
    void OnGUI()
    {
        if (!Inventory4.enabled) return;
        Event e = Event.current;
        if (!InvLook.open && e.type == EventType.KeyDown && e.keyCode == Inventory4.key && Inventory4.CanOpen() && InvLook.KeyToggleOk())
        { InvLook.MarkToggle(); Inventory4.OpenWindow("I key"); e.Use(); return; }
        InvLook.OnGUI();
    }
}

public static class Inventory4
{
    public static bool enabled = true;
    public static KeyCode key = KeyCode.I;
    public static bool installed;
    public static string slotsPath = FishingPaths.Data("inventory_slots.json");
    public static bool broadcast = true;
    public static string testCommandLog;                 // test: last command run through RunCommand
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public class Reg
    {
        public string kind, id, name, rarity, owner, desc, useCommand, prefSlot;
        public Func<string, bool> owns, isEquipped;
        public Action<string, bool> setEquipped;
        public Action<string> use;
    }
    public static readonly List<Reg> registry = new List<Reg>();
    public static readonly Dictionary<string, Dictionary<string, string>> slots = new Dictionary<string, Dictionary<string, string>>();   // player -> slot kind -> id
    public static readonly Dictionary<string, string> extras = new Dictionary<string, string>();                                       // player|id -> text

    static GameObject guiGo;
    static bool retired;
    static readonly List<string> chatQueue = new List<string>();
    static string lastLine;
    static int errors;
    static float nextLook, nextRefresh, nextBcast, nextShim;
    static Type F, pets, merch, flash, caseData, caseLook;
    static readonly List<FieldInfo> shimFields = new List<FieldInfo>(), reqFields = new List<FieldInfo>();
    static readonly Dictionary<string, string> sentKey = new Dictionary<string, string>();
    static readonly Dictionary<string, float> sentAt = new Dictionary<string, float>();
    static readonly HashSet<string> forced = new HashSet<string>();
    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_inventory.log");
    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + typeof(Inventory4).Name + "] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static float Now { get { return Time.unscaledTime; } }

    // ================================================================ registry API
    public static void RegisterItem(string kind, string id, string name, string rarity, string owner, string desc)
    { RegisterItemEx(kind, id, name, rarity, owner, desc, null, null, null, null, null); }
    public static void RegisterItemEx(string kind, string id, string name, string rarity, string owner, string desc,
        Func<string, bool> owns, Func<string, bool> isEquipped, Action<string, bool> setEquipped, Action<string> use, string useCommand)
    {
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(id)) return;
        kind = kind.ToLowerInvariant(); if (kind == "book") kind = "item";
        registry.RemoveAll(r => r.kind == kind && r.id == id && (r.owner ?? "*") == (owner ?? "*"));
        registry.Add(new Reg { kind = kind, id = id, name = name ?? id, rarity = rarity ?? "common", owner = owner ?? "*", desc = desc ?? "", owns = owns, isEquipped = isEquipped, setEquipped = setEquipped, use = use, useCommand = useCommand });
        Log("registered " + kind + " '" + id + "' (" + (name ?? id) + ", owner " + (owner ?? "*") + (use != null || useCommand != null ? ", usable" : "") + ")");
    }
    // gadgets for the two skill slots: prefSlot "utility" (Shift) / "special" (R) / null = first free slot
    public static void RegisterGadget(string id, string name, string rarity, string owner, string desc, string prefSlot, Func<string, bool> owns)
    {
        RegisterItemEx("gadget", id, name, rarity, owner, desc, owns, null, null, null, null);
        Reg g = registry.Find(x => x.kind == "gadget" && x.id == id && x.owner == (owner ?? "*"));
        if (g != null) g.prefSlot = NormSlot(prefSlot);
    }
    // what the Shift (slot "utility"/"shift") or R (slot "special"/"r") key should use for this player: gadget id or null
    public static string EquippedGadget(string player, string slot)
    {
        string k = NormSlot(slot); if (k == null || player == null) return null;
        Dictionary<string, string> d; string id;
        return slots.TryGetValue(player, out d) && d.TryGetValue(k, out id) ? id : null;
    }
    public static string NormSlot(string s)
    {
        switch ((s ?? "").ToLowerInvariant()) { case "utility": case "shift": case "u": return "utility"; case "special": case "r": case "s": return "special"; }
        return null;
    }
    public static void UnregisterItem(string kind, string id) { int n = registry.RemoveAll(r => r.kind == (kind ?? "").ToLowerInvariant() && r.id == id); if (n > 0) Log("unregistered " + kind + " " + id); }
    public static void SetExtra(string owner, string id, string text) { extras[(owner ?? "*") + "|" + id] = text ?? ""; }
    public static string Equipped(string owner, string slotKind)
    {
        if (slotKind == "gadget") return EquippedGadget(owner, "utility") ?? EquippedGadget(owner, "special");
        if (NormSlot(slotKind) != null) return EquippedGadget(owner, slotKind);
        foreach (Reg r in registry) if (r.kind == slotKind && r.isEquipped != null && Owns(r, owner)) { try { if (r.isEquipped(owner)) return r.id; } catch { } }
        Dictionary<string, string> d; string id;
        return owner != null && slots.TryGetValue(owner, out d) && d.TryGetValue(slotKind, out id) ? id : null;
    }
    public static List<object[]> Export()
    {
        var l = new List<object[]>();
        foreach (Reg r in registry) if (r.id != "flashlight") l.Add(new object[] { r.kind, r.id, r.name, r.rarity, r.owner, r.desc, r.owns, r.isEquipped, r.setEquipped, r.use, r.useCommand, r.prefSlot });
        return l;
    }
    static bool Owns(Reg r, string player)
    {
        if (r.owns != null) { try { return r.owns(player); } catch { return false; } }
        return r.owner == "*" || r.owner == player;
    }

    // ================================================================ install
    public static void Run()
    {
        Type prev = null; int prevN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                Match m = Regex.Match(t.Name, "^Inventory(\\d+)$");
                if (!m.Success || t == typeof(Inventory4)) continue;
                FieldInfo en = t.GetField("enabled", All); if (en != null) en.SetValue(null, false);
                int n = int.Parse(m.Groups[1].Value); if (n > prevN) { prevN = n; prev = t; }
            }
        if (prev != null)
        {
            try
            {
                var list = prev.GetMethod("Export", All).Invoke(null, null) as List<object[]>;
                if (list != null) foreach (object[] o in list)
                        RegisterItemEx((string)o[0], (string)o[1], (string)o[2], (string)o[3], (string)o[4], (string)o[5], o[6] as Func<string, bool>, o[7] as Func<string, bool>, o[8] as Action<string, bool>, o[9] as Action<string>, o[10] as string);
                        foreach (object[] o in list) if (o.Length > 11 && o[11] != null) { Reg g = registry.Find(x => x.kind == (string)o[0] && x.id == (string)o[1]); if (g != null) g.prefSlot = (string)o[11]; }
                Log("took over " + (list == null ? 0 : list.Count) + " registered item(s) from " + prev.Name);
            }
            catch (Exception e) { Log("carry-over error: " + e.Message); }
        }
        LoadSlots();
        RegisterItemEx("item", "flashlight", "Flashlight", "common", null, "Spotlight that points where you look. Always on the L key (no slot needed); Use toggles it. Only you see the beam.",
            p => p == LocalName() && FlashType() != null, null, null, p => SetFlash(!FlashOn()), null);
        if (!installed)
        {
            installed = true;
            RoR2Application.onUpdate += Update;
            Chat.onChatChanged += OnChat;
        }
        Log("installed (key " + key + ", !inv); " + registry.Count + " registered item(s), slots for " + slots.Count + " player(s)");
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
            if (line.Contains("!") || line.Contains("[REQ] INV")) lock (chatQueue) chatQueue.Add(line);
        }
        catch { }
    }

    public static string LocalName() { foreach (var lu in LocalUserManager.readOnlyLocalUsersList) if (lu.currentNetworkUser != null) return lu.currentNetworkUser.userName; return null; }
    static NetworkUser User(string name) { foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName == name) return nu; return null; }

    public static bool CanOpen()
    {
        if (!enabled || !NetworkServer.active || RoR2.Run.instance == null) return false;
        return !ChatOpen();
    }
    static bool ChatOpen()
    {
        try { foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>()) { PropertyInfo p = b.GetType().GetProperty("showInput", All); object v = p != null ? p.GetValue(b, null) : null; if (v is bool && (bool)v) return true; } } catch { }
        return false;
    }

    public static void OpenWindow(string why)
    {
        if (InvLook.open) return;
        Refresh();
        InvLook.Open();
        Log("window opened (" + why + ")");
    }

    // ================================================================ per frame (main thread)
    static void Update()
    {
        if (!enabled)
        {
            if (InvLook.open) InvLook.Close("disabled");
            if (!retired) { retired = true; InvLook.Unblock(); SetShim(false); Log("disabled"); }
            if (guiGo != null) { UnityEngine.Object.Destroy(guiGo); guiGo = null; }
            return;
        }
        try
        {
            if (guiGo == null)
            {
                guiGo = new GameObject("Inventory4Gui"); UnityEngine.Object.DontDestroyOnLoad(guiGo); guiGo.AddComponent<Inventory4Gui>();
                InvLook.log = s => Log("ui: " + s); InvLook.note = "";
                Log("GUI object created");
            }
            if (Now >= nextLook) { nextLook = Now + 5f; LookUp(); }
            if (InvLook.open && (RoR2.Run.instance == null || !NetworkServer.active)) InvLook.Close();
            InvLook.InputTick();
            SetShim(InvLook.blocking);
            if (InvLook.blocking) HoldWhipEdge();

            string[] lines = null;
            lock (chatQueue) if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); }
            if (lines != null) foreach (string l in lines) HandleLine(l);

            string[][] acts = null;
            lock (InvLook.pending) if (InvLook.pending.Count > 0) { acts = InvLook.pending.ToArray(); InvLook.pending.Clear(); }
            if (acts != null) { foreach (string[] a in acts) UiAction(a); nextRefresh = 0f; }

            if (InvLook.open && Now >= nextRefresh) { nextRefresh = Now + 0.5f; Refresh(); }
            if (broadcast && NetworkServer.active && RoR2.Run.instance != null && Now >= nextBcast) { nextBcast = Now + 2f; BroadcastTick(); }
        }
        catch (Exception e) { if (errors++ < 20) Log("update error: " + e); }
    }

    // WhipAnim<N> swings on a right-click rising edge (rightWasDown false -> button down): keep its "was down" flag true while
    // the window blocks input, so right-clicking a slot never starts a swing.
    static readonly List<FieldInfo> whipEdge = new List<FieldInfo>(); static float whipLook = -100f;
    static void HoldWhipEdge()
    {
        if (Now - whipLook > 10f)
        {
            whipLook = Now; whipEdge.Clear();
            foreach (var a in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(a))
                {
                    if (!Regex.IsMatch(t.Name, "^WhipAnim\\d+$")) continue;
                    FieldInfo f = t.GetField("rightWasDown", All);
                    if (f != null && f.IsStatic && f.FieldType == typeof(bool)) whipEdge.Add(f);
                }
        }
        foreach (FieldInfo f in whipEdge) try { f.SetValue(null, true); } catch { }
    }

    static void SetShim(bool on)
    {
        if (Now >= nextShim)
        {
            nextShim = Now + 10f; shimFields.Clear(); reqFields.Clear();
            foreach (var a in FishingScope.Assemblies())
                for (int n = 1; n <= 20; n++)
                {
                    Type t = null; try { t = a.GetType("ReqPanel" + n); } catch { }
                    FieldInfo f = t == null ? null : t.GetField("open", All);
                    if (f == null || f.FieldType != typeof(bool) || !f.IsStatic) continue;
                    if (n == 19 || n == 20) shimFields.Add(f); else reqFields.Add(f);
                }
        }
        bool v = on;
        foreach (FieldInfo f in reqFields) try { if ((bool)f.GetValue(null)) v = true; } catch { }
        foreach (FieldInfo f in shimFields) try { f.SetValue(null, v); } catch { }
    }

    // newest enabled types of the other add-ons
    static Type Newest(string prefix, string need)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith(prefix) || !int.TryParse(t.Name.Substring(prefix.Length), out n) || n <= bn) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                if (need != null && t.GetField(need, All) == null && t.GetProperty(need, All) == null) continue;
                best = t; bn = n;
            }
        return best;
    }
    static void LookUp()
    {
        Type f = Newest("Fishing", "RodCatalog");
        if (f != F) Log("fishing mod: " + (f == null ? "none" : f.Name));
        F = f;
        pets = Newest("DronePets", "pets");
        merch = Newest("FishMerchant", "caseItems");
        flash = Newest("Flashlight", "on");
        caseData = merch == null ? null : merch.Assembly.GetType("CaseData");
        caseLook = merch == null ? null : merch.Assembly.GetType("CaseSkinLook");
    }
    static Type FlashType() { return flash; }
    static bool FlashOn() { try { return flash != null && (bool)flash.GetField("on", All).GetValue(null); } catch { return false; } }
    static void SetFlash(bool on) { try { if (flash != null) flash.GetField("on", All).SetValue(null, on); } catch { } }

    static object SGet(Type t, string n)
    {
        if (t == null) return null;
        FieldInfo f = t.GetField(n, All); if (f != null) return f.GetValue(null);
        PropertyInfo p = t.GetProperty(n, All); return p != null ? p.GetValue(null, null) : null;
    }
    static object IGet(object o, string n)
    {
        if (o == null) return null;
        FieldInfo f = o.GetType().GetField(n, All); if (f != null) return f.GetValue(o);
        PropertyInfo p = o.GetType().GetProperty(n, All); return p != null ? p.GetValue(o, null) : null;
    }
    static object Call(Type t, string m, params object[] a)
    {
        if (t == null) return null;
        foreach (MethodInfo mi in t.GetMethods(All)) if (mi.Name == m && mi.IsStatic && mi.GetParameters().Length == a.Length) return mi.Invoke(null, a);
        throw new MissingMethodException(t.Name + "." + m);
    }

    // ================================================================ building the item list
    public static string DefaultRod()
    {
        IList cat = SGet(F, "RodCatalog") as IList;
        if (cat != null) foreach (object ri in cat) if ((string)IGet(ri, "kind") == "rod" && Convert.ToInt32(IGet(ri, "price")) == 0) return (string)IGet(ri, "name");
        return "Stick Rod";
    }
    static string ShopRarity(int price) { return price <= 0 ? "default" : price < 1000 ? "common" : price < 2500 ? "uncommon" : price < 6000 ? "rare" : "legendary"; }
    static readonly string[] CaseRar = { "blue", "purple", "pink", "red", "gold" };

    public static List<InvItem> BuildItems(string player)
    {
        var list = new List<InvItem>();
        if (player == null) return list;
        // ---- rods / skins / bobbers (incl. case items)
        IList cat = SGet(F, "RodCatalog") as IList;
        IDictionary owners = SGet(F, "rodOwners") as IDictionary;
        object owner = owners != null && owners.Contains(player) ? owners[player] : null;
        IList owned = IGet(owner, "owned") as IList;
        IDictionary caseItems = SGet(merch, "caseItems") as IDictionary;
        IDictionary mine = caseItems != null && caseItems.Contains(player) ? caseItems[player] as IDictionary : null;
        IDictionary stats = SGet(caseLook, "statCounts") as IDictionary;
        if (cat != null)
            foreach (object ri in cat)
            {
                string name = (string)IGet(ri, "name"), kind = (string)IGet(ri, "kind"), desc = (string)IGet(ri, "desc") ?? "";
                if (kind != "rod" && kind != "skin" && kind != "bobber") continue;   // e.g. the Angler's Guide ("item") comes from GuideBook<N>'s provider
                int price = Convert.ToInt32(IGet(ri, "price"));
                bool has = price == 0 || (owned != null && owned.Contains(name));
                if (!has) continue;
                string eq = kind == "rod" ? (owner == null ? DefaultRod() : (string)IGet(owner, "rod")) : kind == "skin" ? (owner == null ? "Default" : (string)IGet(owner, "skin")) : (owner == null ? "Plain" : (string)IGet(owner, "bobber"));
                var it = new InvItem { kind = kind, name = name, desc = desc, rarity = ShopRarity(price), equipped = eq == name, isDefault = price == 0 };
                if (caseData != null)
                {
                    try
                    {
                        string bn = (string)Call(caseData, "Base", name);
                        object ci = Call(caseData, "Find", bn);
                        if (ci != null && !(bool)IGet(ci, "shop")) { it.rarity = CaseRar[Math.Max(0, Math.Min(4, Convert.ToInt32(IGet(ci, "rarity"))))]; it.desc = (string)IGet(ci, "desc") ?? desc; it.desc = "Case item: " + it.desc; }
                        it.stat = (bool)Call(caseData, "IsStat", name);
                        if (mine != null && mine.Contains(name))
                        {
                            float[] v = mine[name] as float[];
                            if (v != null && v.Length > 0) { try { it.extra = Convert.ToString(Call(caseData, "WearName", v[0])); } catch { try { it.extra = Convert.ToString(Call(caseData, "WearShort", v[0])); } catch { } } it.extra += " (" + v[0].ToString("0.000", CultureInfo.InvariantCulture) + ")"; }
                        }
                        if (it.stat)
                        {
                            int[] c = stats != null && stats.Contains(player + "|" + name) ? stats[player + "|" + name] as int[] : null;
                            c = c ?? new int[3];
                            it.extra = (it.extra.Length > 0 ? it.extra + "\n" : "") + "StatTrak\u2122: " + c[0] + " fish \u2022 " + c[1] + " rare \u2022 best $" + c[2].ToString("#,0", CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception e) { if (errors++ < 20) Log("case info error (" + name + "): " + e.Message); }
                }
                list.Add(it);
            }
        // ---- lures
        try
        {
            var uses = SGet(F, "lureUses") as Dictionary<string, Dictionary<string, int>>;
            var leq = SGet(F, "lureEquipped") as Dictionary<string, string>;
            IDictionary defs = SGet(F, "Lures") as IDictionary;
            Dictionary<string, int> u; string le = null; if (leq != null) leq.TryGetValue(player, out le);
            if (uses != null && uses.TryGetValue(player, out u))
                foreach (var kv in u)
                {
                    if (kv.Value <= 0) continue;
                    object d = defs != null && defs.Contains(kv.Key) ? defs[kv.Key] : null;
                    int price = d == null ? 600 : Convert.ToInt32(IGet(d, "price"));
                    string[] maps = IGet(d, "maps") as string[];
                    list.Add(new InvItem { kind = "lure", name = kv.Key, equipped = le == kv.Key, desc = d == null ? "" : (string)IGet(d, "text") + (maps != null ? " (only works on those maps)" : ""), extra = kv.Value + " casts left", rarity = price < 500 ? "common" : price < 1000 ? "uncommon" : price < 2000 ? "rare" : "legendary" });
                }
        }
        catch (Exception e) { if (errors++ < 20) Log("lure read error: " + e.Message); }
        // ---- pet
        try
        {
            IDictionary pd = SGet(pets, "pets") as IDictionary;
            object p = pd != null && pd.Contains(player) ? pd[player] : null;
            if (p != null)
            {
                string type = (string)IGet(p, "type"); bool on = (bool)IGet(p, "on");
                foreach (string[] t in new[] { new[] { "Gunner Drone", "uncommon" }, new[] { "Healing Drone", "uncommon" }, new[] { "Missile Drone", "rare" }, new[] { "Equipment Drone", "legendary" } })
                    list.Add(new InvItem { kind = "pet", name = t[0], rarity = t[1], equipped = on && type == t[0], desc = "Your drone pet in this look: follows you and fishes on its own every 20-30 s (weaker than you). Changing the look is free.", extra = on ? "" : "pet is switched off" });
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("pet read error: " + e.Message); }
        // ---- registry (gadgets, charms, usable items, cases, ...) + providers
        foreach (Reg r in registry)
        {
            if (!Owns(r, player)) continue;
            bool eq = false; string gslot = null;
            if (r.kind == "gadget") { gslot = EquippedGadget(player, "utility") == r.id ? "utility" : EquippedGadget(player, "special") == r.id ? "special" : null; eq = gslot != null; }
            else if (r.isEquipped != null) { try { eq = r.isEquipped(player); } catch { } }
            else eq = Equipped(player, r.kind) == r.id;
            string ex; if (!extras.TryGetValue(player + "|" + r.id, out ex)) extras.TryGetValue("*|" + r.id, out ex);
            if (r.id == "flashlight") ex = FlashOn() ? "on" : "off";
            var it = new InvItem { kind = r.kind, name = r.name, rarity = r.rarity, desc = r.desc, extra = ex ?? "", equipped = InvLook.IsSlotKind(r.kind) && eq, slot = gslot, prefSlot = r.prefSlot };
            if (!InvLook.IsSlotKind(r.kind) && (r.use != null || r.useCommand != null)) it.actions = new[] { r.kind == "case" ? "Open" : "Use" };
            list.Add(it);
        }
        foreach (object[] pi in Providers(player))
        {
            var it = (InvItem)pi[0];
            if (it.kind == "gadget") { it.slot = EquippedGadget(player, "utility") == (string)pi[2] ? "utility" : EquippedGadget(player, "special") == (string)pi[2] ? "special" : null; it.equipped = it.slot != null; }
            list.Add(it);
        }
        // ---- fish
        try
        {
            var coll = SGet(F, "collections") as Dictionary<string, Dictionary<string, int>>;
            string[] species = SGet(F, "Species") as string[];
            var perk = SGet(F, "PerkText") as Dictionary<string, string>;
            IDictionary eats = SGet(F, "Eats") as IDictionary;
            Dictionary<string, int> c;
            if (coll != null && species != null && coll.TryGetValue(player, out c))
                foreach (string sp in species)
                {
                    int n; if (!c.TryGetValue(sp, out n) || n <= 0) continue;
                    string tier = Convert.ToString(Call(F, "GpTier", sp));
                    string pk; if (perk != null) perk.TryGetValue(sp, out pk); else pk = null;
                    object ed = eats != null && eats.Contains(sp) ? eats[sp] : null;
                    string desc = (pk != null ? "Held: " + pk : "") + (ed != null ? (pk != null ? "\n" : "") + "Eat: " + IGet(ed, "text") + (Convert.ToSingle(IGet(ed, "secs")) > 0 ? " (" + IGet(ed, "secs") + " s)" : "") : "");
                    if (caseData != null) { try { desc += "\nMerchant pays $" + Call(caseData, "SellPrice", sp); } catch { } }
                    list.Add(new InvItem { kind = "fish", name = sp, count = n, desc = desc, rarity = tier == "Tier1" ? "common" : tier == "Tier2" ? "uncommon" : tier == "Tier3" ? "rare" : tier == "Lunar" ? "lunar" : "default", actions = merch != null ? new[] { "Eat", "Sell 1", "Sell all" } : new[] { "Eat" } });
                }
        }
        catch (Exception e) { if (errors++ < 20) Log("fish read error: " + e.Message); }
        // the same item from two sources (registry + provider) is shown once
        var seen = new HashSet<string>(); var outl = new List<InvItem>();
        foreach (InvItem it in list) if (seen.Add(it.kind + "|" + it.name)) outl.Add(it);
        return outl;
    }

    // providers: any enabled type with  public static IEnumerable InventoryItems(string player)  yielding string[]
    //   {kind, id, name, rarity, desc, extra, equipped "1"/"0"}; equip / use go to InventoryEquip(player, id, bool) / InventoryUse(player, id)
    static float provLook = -100f; static readonly List<Type> provTypes = new List<Type>();
    static List<object[]> Providers(string player)
    {
        var res = new List<object[]>();
        if (Now - provLook > 10f)
        {
            provLook = Now; provTypes.Clear();
            foreach (var a in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(a))
                {
                    MethodInfo mi = t.GetMethod("InventoryItems", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (mi == null) continue;
                    FieldInfo en = t.GetField("enabled", All);
                    if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                    provTypes.Add(t);
                }
        }
        foreach (Type t in provTypes)
        {
            try
            {
                IEnumerable e = t.GetMethod("InventoryItems", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null).Invoke(null, new object[] { player }) as IEnumerable;
                if (e == null) continue;
                foreach (object o in e)
                {
                    string[] s = o as string[]; if (s == null || s.Length < 3) continue;
                    string kind = s[0].ToLowerInvariant(); if (kind == "book") kind = "item";
                    var it = new InvItem { kind = kind, name = s[2], rarity = s.Length > 3 ? s[3] : "common", desc = s.Length > 4 ? s[4] ?? "" : "", extra = s.Length > 5 ? s[5] ?? "" : "", equipped = s.Length > 6 && s[6] == "1" && InvLook.IsSlotKind(kind) };
                    if (!InvLook.IsSlotKind(kind) && t.GetMethod("InventoryUse", BindingFlags.Public | BindingFlags.Static) != null) it.actions = new[] { kind == "case" ? "Open" : "Use" };
                    res.Add(new object[] { it, t, s[1] });
                }
            }
            catch (Exception ex) { if (errors++ < 20) Log("provider " + t.Name + " error: " + (ex.InnerException ?? ex).Message); }
        }
        return res;
    }

    static void Refresh()
    {
        string me = LocalName();
        InvLook.items = BuildItems(me);
        InvLook.title = "INVENTORY  <size=" + Mathf.RoundToInt(15 * Mathf.Max(0.75f, Screen.height / 1080f)) + "><color=#8a93a3>" + (me ?? "") + "</color></size>";
        int m = 0; var money = SGet(F, "money") as Dictionary<string, int>; if (money != null && me != null) money.TryGetValue(me, out m);
        InvLook.money = "<color=#ffd24a>$" + m.ToString("#,0", CultureInfo.InvariantCulture) + "</color>";
        InvLook.note = F == null ? "fishing mod not found" : "";
    }

    // ================================================================ commands (same strings the guests send in chat)
    public static string EquipCmd(string kind, string name) { return EquipCmd(kind, name, null); }
    public static string EquipCmd(string kind, string name, string slotId)
    {
        if (kind == "gadget") return "!gadget " + (NormSlot(slotId) == "special" ? "r " : NormSlot(slotId) == "utility" ? "shift " : "") + name;
        switch (kind)
        {
            case "rod": case "skin": case "bobber": return "!equip " + name;
            case "lure": return "!lure " + name;
            case "pet": return "!pet " + name.ToLowerInvariant();
            case "gadget": return "!gadget " + name;
            case "charm": return "!charm " + name;
        }
        return null;
    }
    public static string UnequipCmd(string kind)
    {
        if (NormSlot(kind) != null) return "!gadget " + (NormSlot(kind) == "special" ? "r" : "shift") + " off";
        switch (kind)
        {
            case "rod": return "!equip " + DefaultRod(); case "skin": return "!equip Default"; case "bobber": return "!equip Plain";
            case "lure": return "!lure off"; case "pet": return "!pet off"; case "gadget": return "!gadget off"; case "charm": return "!charm off";
        }
        return null;
    }
    public static string ActCmd(string kind, string name, string action)
    {
        if (kind == "fish") return action == "Eat" ? "!eat " + name : action == "Sell 1" ? "!sell " + name + " 1" : action == "Sell all" ? "!sell " + name + " all" : null;
        return "!use " + name;
    }

    static void UiAction(string[] a)
    {
        string me = LocalName(); if (me == null) return;
        string cmd = a[0] == "equip" ? EquipCmd(a[1], a[2], a.Length > 3 ? a[3] : null) : a[0] == "unequip" ? UnequipCmd(a[1]) : a[0] == "act" ? ActCmd(a[1], a[2], a[3]) : null;
        if (cmd == null) return;
        string r = RunCommand(me, cmd);
        InvLook.Status(r);
    }

    // runs a chat command as <player> through the add-on that owns it (main thread); returns a short status line
    public static string RunCommand(string player, string cmd)
    {
        testCommandLog = player + ": " + cmd;
        string lo = cmd.ToLowerInvariant(), arg = cmd.IndexOf(' ') > 0 ? cmd.Substring(cmd.IndexOf(' ') + 1).Trim() : "";
        Log(player + " (inventory): " + cmd);
        try
        {
            if (lo.StartsWith("!equip "))
            {
                if (F == null) return Fallback(player, cmd);
                Call(F, "RodCommand", player, cmd);
                IDictionary owners = SGet(F, "rodOwners") as IDictionary;
                object o = owners != null && owners.Contains(player) ? owners[player] : null;
                string want = arg.ToLowerInvariant();
                foreach (string f in new[] { "rod", "skin", "bobber" }) if (o != null && Convert.ToString(IGet(o, f)).ToLowerInvariant() == want) return "Equipped: " + IGet(o, f);
                if (o == null && (want == DefaultRod().ToLowerInvariant() || want == "default" || want == "plain")) return "Equipped: " + arg;
                return "Couldn't equip " + arg + " (not owned?).";
            }
            if (lo.StartsWith("!lure "))
            {
                if (F == null || F.GetMethod("GpCommand", All) == null) return Fallback(player, cmd);
                Call(F, "GpCommand", player, cmd);
                return arg.ToLowerInvariant() == "off" ? "Lure taken off." : "Lure: " + arg;
            }
            if (lo.StartsWith("!pet "))
            {
                MethodInfo h = pets == null ? null : pets.GetMethod("HandleLine", All);
                if (h == null) return Fallback(player, cmd);
                h.Invoke(null, new object[] { player + ": " + cmd });
                return arg == "off" ? "Pet switched off." : "Pet: " + arg;
            }
            if (lo.StartsWith("!eat "))
            {
                if (F == null || F.GetMethod("Eat", All) == null) return Fallback(player, cmd);
                string r = Convert.ToString(Call(F, "Eat", player, arg, false));
                return r == "none" ? "No " + arg + " left." : "Ate a " + arg + ".";
            }
            if (lo.StartsWith("!sell "))
            {
                MethodInfo h = merch == null ? null : merch.GetMethod("HandleLine", All);
                if (h == null) return Fallback(player, cmd);
                h.Invoke(null, new object[] { player + ": " + cmd });
                return "Sell request sent to the Fish Merchant (you must stand at the stall).";
            }
            if (lo.StartsWith("!gadget ") || lo.StartsWith("!charm ")) return SlotCmd(player, lo.StartsWith("!gadget ") ? "gadget" : "charm", arg);
            if (lo.StartsWith("!use ")) return UseCmd(player, arg);
        }
        catch (Exception e) { Log("command error (" + cmd + "): " + (e.InnerException ?? e)); return "Couldn't do that (see ror2_inventory.log)."; }
        return Fallback(player, cmd);
    }
    static string Fallback(string player, string cmd)
    {
        if (player != LocalName()) { Log("no handler for " + cmd + " (" + player + ")"); return "Not available."; }
        return SayAsLocal(cmd) ? "Sent: " + cmd : "Couldn't send " + cmd;
    }
    static bool SayAsLocal(string msg)
    {
        try
        {
            var lu = LocalUserManager.GetFirstLocalUser();
            if (lu == null || lu.currentNetworkUser == null) return false;
            RoR2.Console.instance.SubmitCmd(lu.currentNetworkUser, "say \"" + msg.Replace("\"", "'") + "\"", false);
            return true;
        }
        catch (Exception e) { Log("say error: " + e.Message); return false; }
    }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = "<color=#66ccff>" + text + "</color>" }); }

    static Reg FindReg(string player, string kind, string q)
    {
        string n = Regex.Replace((q ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
        Reg hit = null;
        foreach (Reg r in registry)
        {
            if ((kind != null && r.kind != kind) || !Owns(r, player)) continue;
            string a = Regex.Replace(r.name.ToLowerInvariant(), "[^a-z0-9]", ""), b = Regex.Replace(r.id.ToLowerInvariant(), "[^a-z0-9]", "");
            if (a == n || b == n) return r;
            if (n.Length > 0 && (a.StartsWith(n) || b.StartsWith(n))) hit = hit == null ? r : hit;
        }
        return hit;
    }

    // !gadget [shift|r] <name>|off  and  !charm <name>|off
    static string SlotCmd(string player, string kind, string arg)
    {
        string slot = kind;
        if (kind == "gadget")
        {
            int sp = arg.IndexOf(' ');
            string first = sp > 0 ? arg.Substring(0, sp) : arg;
            if (NormSlot(first) != null && (sp > 0 || first.ToLowerInvariant() != "s")) { slot = NormSlot(first); arg = sp > 0 ? arg.Substring(sp + 1).Trim() : "off"; }
            else slot = null;
        }
        if (arg.ToLowerInvariant() == "off")
        {
            var offs = slot != null ? new[] { slot } : new[] { "utility", "special" };
            var names = new List<string>();
            foreach (string sl in offs)
            {
                string cur = EquippedGadgetOrSlot(player, sl);
                if (cur == null) continue;
                Reg old = registry.Find(x => x.kind == kind && x.id == cur && Owns(x, player));
                SetSlot(player, sl, null);
                if (old != null && old.setEquipped != null && !InSlots(player, old.id)) try { old.setEquipped(player, false); } catch (Exception e) { Log("setEquipped error: " + e.Message); }
                ProviderEquip(player, kind, cur, false);
                names.Add(old != null ? old.name : cur);
            }
            return names.Count == 0 ? "Nothing to take off." : "Took off: " + string.Join(", ", names.ToArray());
        }
        Reg it = FindReg(player, kind, arg);
        string id = it != null ? it.id : null, shown = it != null ? it.name : null, pref = it != null ? it.prefSlot : null;
        if (it == null)
            foreach (object[] pi in Providers(player))
            {
                var ii = (InvItem)pi[0];
                if (ii.kind == kind && Regex.Replace(ii.name.ToLowerInvariant(), "[^a-z0-9]", "") == Regex.Replace(arg.ToLowerInvariant(), "[^a-z0-9]", "")) { id = (string)pi[2]; shown = ii.name; pref = ii.prefSlot; break; }
            }
        if (id == null) { if (player != LocalName()) Say(player + " has no " + kind + " called \"" + arg + "\"."); return "You don't have that " + kind + "."; }
        if (kind == "gadget" && slot == null) slot = pref ?? (EquippedGadget(player, "utility") == null ? "utility" : EquippedGadget(player, "special") == null ? "special" : "utility");
        string prev = EquippedGadgetOrSlot(player, slot);
        if (prev == id) return shown + " is already equipped.";
        if (kind == "gadget") foreach (string sl in new[] { "utility", "special" }) if (sl != slot && EquippedGadget(player, sl) == id) SetSlot(player, sl, null);   // one item, one slot
        SetSlot(player, slot, id);
        if (prev != null && !InSlots(player, prev))
        {
            Reg old = registry.Find(x => x.kind == kind && x.id == prev && Owns(x, player));
            if (old != null && old.setEquipped != null) try { old.setEquipped(player, false); } catch (Exception e) { Log("setEquipped error: " + e.Message); }
            ProviderEquip(player, kind, prev, false);
        }
        if (it != null && it.setEquipped != null) try { it.setEquipped(player, true); } catch (Exception e) { Log("setEquipped error: " + e.Message); }
        ProviderEquip(player, kind, id, true);
        Log(player + ": " + slot + " slot = " + id);
        return "Equipped: " + shown + (slot == "utility" ? " (Shift)" : slot == "special" ? " (R)" : "");
    }
    static string EquippedGadgetOrSlot(string player, string slot)
    {
        Dictionary<string, string> d; string id;
        return slots.TryGetValue(player, out d) && d.TryGetValue(slot, out id) ? id : null;
    }
    static bool InSlots(string player, string id) { Dictionary<string, string> d; return slots.TryGetValue(player, out d) && d.ContainsValue(id); }
    static void ProviderEquip(string player, string kind, string id, bool on)
    {
        foreach (object[] pi in Providers(player))
        {
            if ((string)pi[2] != id || ((InvItem)pi[0]).kind != kind) continue;
            MethodInfo eqm = ((Type)pi[1]).GetMethod("InventoryEquip", BindingFlags.Public | BindingFlags.Static);
            if (eqm != null) try { eqm.Invoke(null, new object[] { player, id, on }); } catch (Exception e) { Log("InventoryEquip error: " + (e.InnerException ?? e).Message); }
        }
    }

    static string UseCmd(string player, string arg)
    {
        Reg it = FindReg(player, null, arg);
        if (it != null && it.use != null) { it.use(player); Log(player + ": used " + it.id); return "Used: " + it.name; }
        if (it != null && it.useCommand != null)
        {
            if (player == LocalName()) { SayAsLocal(it.useCommand); return "Used: " + it.name; }
            return "Not available.";
        }
        foreach (object[] pi in Providers(player))
        {
            var ii = (InvItem)pi[0];
            if (Regex.Replace(ii.name.ToLowerInvariant(), "[^a-z0-9]", "") != Regex.Replace(arg.ToLowerInvariant(), "[^a-z0-9]", "")) continue;
            MethodInfo um = ((Type)pi[1]).GetMethod("InventoryUse", BindingFlags.Public | BindingFlags.Static);
            if (um != null) { um.Invoke(null, new object[] { player, (string)pi[2] }); return "Used: " + ii.name; }
        }
        return "You don't have that.";
    }

    static void SetSlot(string player, string kind, string id)
    {
        Dictionary<string, string> d; if (!slots.TryGetValue(player, out d)) { d = new Dictionary<string, string>(); slots[player] = d; }
        if (id == null) d.Remove(kind); else d[kind] = id;
        SaveSlots(); if (player != LocalName()) forced.Add(player);
    }
    static void SaveSlots()
    {
        try
        {
            var sb = new StringBuilder("{\n"); int k = 0;
            foreach (var kv in slots)
            {
                var parts = new List<string>(); foreach (var s in kv.Value) parts.Add(Q(s.Key) + ": " + Q(s.Value));
                sb.Append("  " + Q(kv.Key) + ": { " + string.Join(", ", parts.ToArray()) + " }" + (++k < slots.Count ? "," : "") + "\n");
            }
            File.WriteAllText(slotsPath, sb.Append("}\n").ToString());
        }
        catch (Exception e) { Log("save slots error: " + e.Message); }
    }
    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static void LoadSlots()
    {
        try
        {
            if (!File.Exists(slotsPath)) return;
            foreach (Match pm in Regex.Matches(File.ReadAllText(slotsPath), "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\\{([^}]*)\\}"))
            {
                var d = new Dictionary<string, string>();
                foreach (Match kv in Regex.Matches(pm.Groups[2].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")) d[Regex.Unescape(kv.Groups[1].Value)] = Regex.Unescape(kv.Groups[2].Value);
                slots[Regex.Unescape(pm.Groups[1].Value)] = d;
            }
        }
        catch (Exception e) { Log("load slots error: " + e.Message); }
    }

    // ================================================================ chat from players
    static void HandleLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim(), text = plain.Substring(i + 2).Trim(), lo = text.ToLowerInvariant();
        if (User(name) == null) return;
        if (lo == "!inv" || lo == "!bag")
        {
            if (name == LocalName()) { if (!InvLook.open) OpenWindow("!inv"); }
            else { forced.Add(name); nextBcast = 0f; }
            return;
        }
        if (text == "[REQ] INV") { forced.Add(name); nextBcast = 0f; return; }
        if (lo.StartsWith("!gadget ") || lo.StartsWith("!charm ") || lo.StartsWith("!use "))
        {
            string r = RunCommand(name, text);
            if (name != LocalName() || !InvLook.open) Say(name + ": " + r);
            forced.Add(name);
        }
    }

    // ================================================================ [FT] INV broadcast
    static string Clean(string s) { return (s ?? "").Replace(":", " ").Replace(";", ",").Replace("|", "/").Replace("\n", " / "); }
    static string Code(string kind)
    {
        switch (kind) { case "rod": return "r"; case "skin": return "s"; case "bobber": return "b"; case "lure": return "l"; case "gadget": return "g"; case "pet": return "p"; case "charm": return "h"; case "item": return "i"; case "case": return "c"; case "fish": return "f"; }
        return "i";
    }
    public static string Compact(string player)
    {
        var parts = new List<string>();
        int m = 0; var money = SGet(F, "money") as Dictionary<string, int>; if (money != null) money.TryGetValue(player, out m);
        parts.Add("$:" + m);
        foreach (InvItem it in BuildItems(player))
        {
            if (it.name == "Flashlight" && it.kind == "item") continue;   // host-only (guests' flashlight comes with the client)
            string ex = it.kind == "fish" ? it.count.ToString() : it.extra;
            parts.Add(Code(it.kind) + (it.equipped ? "*" + (it.slot ?? "") : "") + ":" + Clean(it.name) + ":" + Clean(it.rarity) + ":" + Clean(ex) + ":" + Clean(it.desc));
        }
        return string.Join(";", parts.ToArray());
    }
    static void BroadcastTick()
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList)
        {
            if (nu == null || string.IsNullOrEmpty(nu.userName)) continue;
            string p = nu.userName;
            if (nu.isLocalPlayer && !forced.Contains(p)) continue;   // the host sees its own window
            string c;
            try { c = Compact(p); } catch (Exception e) { if (errors++ < 20) Log("compact error: " + e.Message); continue; }
            string prev; sentKey.TryGetValue(p, out prev);
            float at; sentAt.TryGetValue(p, out at);
            bool force = forced.Remove(p);
            if (!force && (c == prev || Now - at < 8f)) continue;
            sentKey[p] = c; sentAt[p] = Now;
            var chunks = new List<string>();
            for (int k = 0; k < c.Length; k += 700) chunks.Add(c.Substring(k, Math.Min(700, c.Length - k)));
            for (int k = 0; k < chunks.Count; k++) SendRaw("[FT] INV|" + p + "|" + (k + 1) + "/" + chunks.Count + "|" + chunks[k]);
            Log("INV sent for " + p + " (" + c.Length + " chars, " + chunks.Count + " part(s)" + (force ? ", requested" : "") + ")");
        }
    }
    static void SendRaw(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }
}
