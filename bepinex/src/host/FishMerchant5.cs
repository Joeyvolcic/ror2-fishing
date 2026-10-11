// FishMerchant<N> - host add-on for the fishing game: replaces the roulette table with a FISH MERCHANT + CS:GO-style cases.
// Build with CaseData.cs, CaseSkinLook.cs, MerchantLook.cs, MerchantUI.cs (agents\merchant\inject.ps1 -Name FishMerchant5 -Extra ...).
//
//  * Each stage: the merchant's stall (Newt shopkeeper sitting behind a fish counter, teal outline through walls, name tag) on
//    flat land near the lake, chosen like the roulette table (nearest flat open ground to the hotspot, above the water), away
//    from other add-ons' spots (any static *pos* Vector3 in types named *Stand*/*Store*/*Shop*). Broadcast to guests as
//    "[FT] MERCHANT|x|y|z|yaw" (every 20 s). Disables Roulette<N> and removes its table.
//  * Within 4.5 m: an on-screen menu (host: this add-on; guests: FISHING.exe) or chat:
//      !sell <fish> [n|all]   !trade <fish|common|uncommon|rare>   !case [common|uncommon|rare]   !skins   !merchant (help + odds)
//    Guests' menu sends hidden "[FT] MERCH|sell|<fish>|<n|all>" / "trade|<0-2>" / "buy|<0-2>" / "equip|<item>"; the host checks
//    the distance (4.5 m + 1 m lag slack) and everything else. Fish = the carrier items from the newest Fishing<N>.fishCarrierItem
//    (removed server-side), plus collections[name] decremented and "[FT] COLL" re-sent - the same way eating does.
//  * Cases open at once: the host rolls (CaseData odds), broadcasts "[FT] CASE|player|caseKey|item|rarity|wear|seed|dup",
//    and everyone's game plays the same reel for that player. Chat announcement after the reel lands (~5.5 s).
//  * Unboxed items go into the rod shop's ownership: case-only skins/bobbers are added to the newest Fishing<N>.RodCatalog
//    (kind skin/bobber, a "market" price), owned via rodOwners[name].owned, equipped with the normal !equip, saved by the shop's
//    SaveRods() in rod_owners.json. Because a fresh Fishing<N> drops unknown names when it loads that file, the case items and
//    their wear/pattern are ALSO kept in host\case_items.json and re-added (and re-equipped) whenever a new Fishing<N> shows up.
//    Duplicates are paid out in money. "[FT] CINV|name|item:wear:seed;..." and "[FT] CSKIN|name|skin|wear|seed|bobber|wear|seed".
//  * Visuals: every frame the host's rod rigs (Fishing<N>.rigs) whose skin key names a case item get the CaseSkinLook look on
//    top of the shop's default; the materials go into the rig's skinMats so the shop's next re-skin cleans them up.
//  * Test players: names starting with "~" use a fake fish inventory (testFish), skip the distance check, never touch the shop
//    ownership or files, and their chat/broadcast lines go to the log only. Queue test actions with testQueue.
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

public class FishMerchant5Gui : MonoBehaviour
{
    void OnGUI() { if (FishMerchant5.enabled && FishMerchant5.uiOn) MerchantUI.OnGUI(); }
}

public static class FishMerchant5
{
    public static bool enabled = true;
    public static bool uiOn = true;
    public static bool installed;
    public static bool showTestReel = true;                    // test players' reels play on the host screen (marked TEST)
    public static Vector3 merchantPos; public static float merchantYaw; public static bool placed;
    public const float TradeRange = 4.5f;
    public static string savePath = FishingPaths.Data("case_items.json");
    public static readonly Dictionary<string, Dictionary<string, float[]>> caseItems = new Dictionary<string, Dictionary<string, float[]>>();   // name -> item -> {wear, seed}
    public static readonly Dictionary<string, string[]> caseEquip = new Dictionary<string, string[]>();                                   // name -> {case skin, case bobber}
    public static readonly Dictionary<string, Dictionary<string, int>> testFish = new Dictionary<string, Dictionary<string, int>>();
    public static readonly List<string> testQueue = new List<string>();                                                                 // "name|action|arg|arg"
    public static readonly List<string> testLog = new List<string>();
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly System.Random rng = new System.Random();
    static readonly List<string> queue = new List<string>();
    static string lastLine, scene;
    static float nextPlaceTry, nextAnnounce, nextObserve, nextInvBroadcast, nextAvoidCheck, nextPrune;
    static Type F, lastF;
    static readonly Dictionary<string, float> busyUntil = new Dictionary<string, float>();
    static readonly List<KeyValuePair<float, Action>> later = new List<KeyValuePair<float, Action>>();
    static GameObject guiGo;

    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_merchant.log");
    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + typeof(FishMerchant5).Name + "] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static bool IsTest(string name) { return name != null && name.StartsWith("~"); }

    // ================================================================ install
    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                if (t == typeof(FishMerchant5) || t.Namespace != null) continue;
                bool older = t.Name.StartsWith("FishMerchant") && !t.Name.EndsWith("Gui") && t.Name != "FishMerchantLook";
                bool roulette = t.Name.StartsWith("Roulette") && t.Name != "RouletteLook" && !t.Name.StartsWith("RouletteTest") && !t.Name.StartsWith("RouletteProbe") && !t.Name.StartsWith("RouletteClient");
                if (!older && !roulette) continue;
                FieldInfo f = t.GetField("enabled", All);
                if (f != null && f.FieldType == typeof(bool) && f.IsStatic) { f.SetValue(null, false); Log("disabled " + a.GetName().Name + ":" + t.Name); }
                if (older) CarryOver(t);
            }
        LoadMine();
        MerchantLook.log = Log; CaseSkinLook.log = Log;
        MerchantUI.log = Log;
        MerchantUI.play = s => Play(s);
        MerchantUI.me = () => LocalName();
        MerchantUI.fishCount = sp => { string me = LocalName(); return me == null ? 0 : FishHave(me, sp); };   // the carrier items really in the inventory (what can be sold)
        MerchantUI.money = () => { var m = MoneyDict(); int v; string me = LocalName(); return m != null && me != null && m.TryGetValue(me, out v) ? v : 0; };
        MerchantUI.myItems = () => { string me = LocalName(); Dictionary<string, float[]> d; return me != null && caseItems.TryGetValue(me, out d) ? new List<string>(d.Keys) : new List<string>(); };
        MerchantUI.wearOf = it => { string me = LocalName(); Dictionary<string, float[]> d; float[] v; return me != null && caseItems.TryGetValue(me, out d) && d.TryGetValue(it, out v) ? v[0] : -1f; };
        MerchantUI.equippedSkin = () => OwnerField(LocalName(), "skin");
        MerchantUI.equippedBobber = () => OwnerField(LocalName(), "bobber");
        MerchantUI.statLine = () => StatLine(LocalName());
        MerchantUI.send = a => { string me = LocalName(); if (me != null) Act(me, a.Split('|'), false); };
        Chat.onChatChanged += OnChat;
        RoR2Application.onUpdate += Tick;
        installed = true;
        Log("installed; " + caseItems.Count + " player(s) with case items in " + savePath);
    }

    static void CarryOver(Type old)
    {
        try
        {
            var ci = old.GetField("caseItems", All) != null ? old.GetField("caseItems", All).GetValue(null) as IDictionary : null;
            if (ci != null && ci.Count > 0) Log("older " + old.Name + " had " + ci.Count + " player(s); this version reloads " + savePath);
        }
        catch { }
    }

    // ================================================================ fishing mod access (reflection on the newest Fishing<N>)
    static float nextNewest;
    static Type Newest()
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            if (a.GetName().Name.StartsWith("Fishing"))
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith("Fishing") || t.Namespace != null || !int.TryParse(t.Name.Substring(7), out n) || n <= bn) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en == null || !(en.GetValue(null) is bool) || !(bool)en.GetValue(null)) continue;
                if (t.GetField("fishCarrierItem", All) == null || t.GetField("money", All) == null) continue;
                best = t; bn = n;
            }
        return best;
    }
    static object SF(string n) { if (F == null) return null; FieldInfo f = F.GetField(n, All); return f != null ? f.GetValue(null) : null; }
    static object Call(string m, params object[] a)
    {
        if (F == null) return null;
        foreach (MethodInfo mi in F.GetMethods(All))
            if (mi.Name == m && mi.IsStatic && mi.GetParameters().Length == a.Length) return mi.Invoke(null, a);
        Log("no method " + F.Name + "." + m + "/" + a.Length);
        return null;
    }
    static Dictionary<string, int> MoneyDict() { return SF("money") as Dictionary<string, int>; }
    static Dictionary<string, Dictionary<string, int>> Collections() { return SF("collections") as Dictionary<string, Dictionary<string, int>>; }
    static Dictionary<string, string> Carriers() { return SF("fishCarrierItem") as Dictionary<string, string>; }
    static IDictionary RodOwners() { return SF("rodOwners") as IDictionary; }
    static object Owner(string name) { var o = RodOwners(); return o != null && name != null && o.Contains(name) ? o[name] : null; }
    static string OwnerField(string name, string f) { object o = Owner(name); if (o == null) return null; FieldInfo fi = o.GetType().GetField(f, All); return fi != null ? fi.GetValue(o) as string : null; }
    static List<string> OwnedList(object o) { if (o == null) return null; FieldInfo fi = o.GetType().GetField("owned", All); return fi != null ? fi.GetValue(o) as List<string> : null; }

    public static string LocalName()
    {
        try
        {
            string n = SF("hudLocalName") as string;
            if (!string.IsNullOrEmpty(n)) return n;
            var lu = LocalUserManager.GetFirstLocalUser();
            return lu != null && lu.currentNetworkUser != null ? lu.currentNetworkUser.userName : null;
        }
        catch { return null; }
    }

    static void Say(string text, string who)
    {
        if (IsTest(who)) { Log("[test chat] " + text); lock (testLog) testLog.Add(text); return; }
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text });
    }
    static void Teal(string text, string who) { Say("<color=#55eedd>" + text + "</color>", who); }
    static void Raw(string line, string who)
    {
        if (IsTest(who)) { Log("[test ft] " + line); lock (testLog) testLog.Add(line); return; }
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = line });
    }

    static void Play(string s)
    {
        try
        {
            var lu = LocalUserManager.GetFirstLocalUser();
            GameObject go = lu != null && lu.cachedBody != null ? lu.cachedBody.gameObject : MerchantLook.root;
            if (go == null && Camera.main != null) go = Camera.main.gameObject;
            if (go != null) Util.PlaySound(s, go);
        }
        catch { }
    }

    // ================================================================ chat
    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            var log = Chat.readOnlyLog;
            if (log.Count == 0) return;
            // every new line since the last one we saw (several can arrive in one frame)
            int start = log.Count - 1;
            while (start > 0 && !ReferenceEquals(log[start - 1], lastLine) && log.Count - start < 8) start--;
            if (lastLine == null) start = log.Count - 1;
            for (int i = start; i < log.Count; i++)
            {
                string line = log[i];
                if (line == null || ReferenceEquals(line, lastLine)) continue;
                lock (queue) queue.Add(line);
            }
            lastLine = log[log.Count - 1];
        }
        catch { }
    }

    static void HandleLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        if (plain.StartsWith("[FT] CATCH|")) { StatCatch(plain); return; }
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim(), text = plain.Substring(i + 2).Trim();
        if (!text.StartsWith("!") && !text.StartsWith("[FT] MERCH|")) return;
        NetworkUser user = User(name);
        if (user == null) return;
        if (text.StartsWith("[FT] MERCH|")) { Act(name, text.Substring(11).Split('|'), false); return; }
        string[] tok = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string cmd = tok[0].ToLowerInvariant();
        string rest = tok.Length > 1 ? string.Join(" ", tok, 1, tok.Length - 1) : "";
        switch (cmd)
        {
            case "!merchant": case "!cases": Help(name); break;
            case "!skins": case "!inventory": Skins(name); break;
            case "!sell":
                {
                    if (tok.Length < 2) { Teal("Fish Merchant: !sell <fish> [count|all], e.g. !sell minnow 2", name); return; }
                    string cnt = "1"; string sp = rest;
                    string last = tok[tok.Length - 1].ToLowerInvariant();
                    int dummy;
                    if (tok.Length > 2 && (last == "all" || int.TryParse(last, out dummy))) { cnt = last; sp = string.Join(" ", tok, 1, tok.Length - 2); }
                    Act(name, new[] { "sell", sp, cnt }, true);
                }
                break;
            case "!trade":
                if (tok.Length < 2) { Teal("Fish Merchant: !trade <fish> (3 white = Tackle Case, 1 green = Deep Water Case, 1 red/lunar = Abyssal Case) or !trade common|uncommon|rare", name); return; }
                Act(name, new[] { "trade", rest }, true);
                break;
            case "!case": case "!opencase": case "!buycase":
                Act(name, new[] { "buy", rest.Length == 0 ? "common" : rest }, true);
                break;
        }
    }

    // ---- StatTrak: "[FT] CATCH|<name>|<species>|<value>|<total>" from the newest Fishing<N> -> +1 on the StatTrak rod the
    //      catcher has equipped (only their own catches; Old Boots don't count). Rare = red/lunar tier. Best = highest value.
    public static string StatCatch(string plain)
    {
        string[] p = plain.Substring(5).Split('|');
        if (p.Length < 4) return "bad";
        string name = p[1], sp = p[2]; int val; int.TryParse(p[3], out val);
        if (sp == "Old Boot") return "boot";
        string skin = IsTest(name) ? testSkin : OwnerField(name, "skin");
        if (!CaseData.IsStat(skin)) return "no stattrak";
        Dictionary<string, float[]> d; float[] v;
        if (!caseItems.TryGetValue(name, out d) || !d.TryGetValue(skin, out v)) return "not owned";
        if (v.Length < 5) { var nv = new float[5]; Array.Copy(v, nv, v.Length); v = nv; d[skin] = v; }
        v[2] += 1; string t = CaseData.TierOf(sp); if (t == "red" || t == "lunar") v[3] += 1; if (val > v[4]) v[4] = val;
        var c = new[] { (int)v[2], (int)v[3], (int)v[4] };
        CaseSkinLook.statCounts[name + "|" + skin] = c;
        if (!IsTest(name)) SaveMine();
        Raw("[FT] STAT|" + name + "|" + skin + "|" + c[0] + "|" + c[1] + "|" + c[2], name);
        Log("STATTRAK " + name + " " + skin + ": " + c[0] + " fish, " + c[1] + " rare, best $" + c[2] + " (" + sp + " $" + val + ")");
        return skin + " " + c[0];
    }
    public static string testSkin;   // equipped skin of "~" test players (they have no shop owner entry)
    static string StatLine(string name)
    {
        string skin = OwnerField(name, "skin");
        if (!CaseData.IsStat(skin)) return null;
        int[] c; CaseSkinLook.statCounts.TryGetValue(name + "|" + skin, out c);
        c = c ?? new int[3];
        return "<color=#cf6a32>StatTrak\u2122</color> " + CaseData.Base(skin) + ": <color=#ff9933><b>" + c[0] + "</b></color> fish  -  " + c[1] + " rare  -  best " + CaseData.Money(c[2]);
    }
    static void BroadcastStats(string name)
    {
        Dictionary<string, float[]> d; if (IsTest(name) || !caseItems.TryGetValue(name, out d)) return;
        foreach (var kv in d) if (CaseData.IsStat(kv.Key) && kv.Value.Length >= 5) Raw("[FT] STAT|" + name + "|" + kv.Key + "|" + (int)kv.Value[2] + "|" + (int)kv.Value[3] + "|" + (int)kv.Value[4], name);
    }

    static NetworkUser User(string name) { foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu.userName == name) return nu; return null; }

    static void Help(string name)
    {
        Teal("FISH MERCHANT (stand at the stall near the lake): !sell <fish> [n|all], !trade <fish>, !case [common|uncommon|rare], !skins - or use the on-screen menu (number keys).", name);
        for (int c = 0; c < 3; c++)
            Teal(CaseData.Cases[c].name + ": " + CaseData.Money(CaseData.Cases[c].price) + " or " + CaseData.Cases[c].fishText + " - " + CaseData.OddsLine(c), name);
        Teal("Drops: " + CaseData.DropTable(), name);
        Teal("StatTrak\u2122: 10% of case rod skins come as StatTrak - an orange counter on the rod counts your catches with it (case-only, never sold).", name);
        Teal("Fish prices: white $15, green $50, red $180, lunar $400. Duplicates pay $25 / $100 / $400 / $1,200 / $4,000.", name);
    }

    static void Skins(string name)
    {
        Dictionary<string, float[]> d;
        if (!caseItems.TryGetValue(name, out d) || d.Count == 0) { Teal(name + " hasn't unboxed anything yet.", name); return; }
        var parts = new List<string>();
        foreach (var kv in d) { var it = CaseData.Find(kv.Key); parts.Add("<color=" + (it != null ? CaseData.RarityHex[it.rarity] : "#ffffff") + ">" + kv.Key + "</color> (" + CaseData.WearShort(kv.Value[0]) + ")"); }
        Teal(name + "'s case skins: " + string.Join(", ", parts.ToArray()) + " - !equip <name>", name);
    }

    // ================================================================ actions
    static string Refuse(string name, string text, bool fromChat)
    {
        Log("refused " + name + ": " + text);
        if (IsTest(name)) { lock (testLog) testLog.Add("REFUSED: " + text); return "refused: " + text; }
        bool local = name == LocalName();
        if (local) { MerchantUI.SetStatus("<color=#ff8866>" + text + "</color>"); Play("Play_UI_insufficient_funds"); }
        if (fromChat) Teal("Fish Merchant: " + text, name);
        else if (!local) Raw("[FT] MREPLY|" + name + "|" + text, name);
        return "refused: " + text;
    }

    public static string Act(string name, string[] a, bool fromChat)
    {
        try
        {
            if (a.Length == 0) return "nothing";
            string action = a[0].Trim().ToLowerInvariant();
            if (F == null) return Refuse(name, "the fishing game isn't running.", fromChat);
            if (!placed) return Refuse(name, "the merchant hasn't set up shop on this stage yet.", fromChat);
            if (action != "equip" && !IsTest(name))
            {
                NetworkUser u = User(name);
                CharacterBody body = u != null && u.master != null ? u.master.GetBody() : null;
                if (body == null) return Refuse(name, name + ", you need to be alive to trade.", fromChat);
                float slack = name == LocalName() ? 0.3f : 1.0f;
                float d = Vector3.Distance(body.footPosition, merchantPos);
                if (d > TradeRange + slack) return Refuse(name, name + ", walk up to the Fish Merchant to trade (" + d.ToString("0") + " m away).", fromChat);
            }
            switch (action)
            {
                case "sell": return Sell(name, a.Length > 1 ? a[1] : "", a.Length > 2 ? a[2] : "1", fromChat);
                case "trade": return Trade(name, a.Length > 1 ? a[1] : "", fromChat);
                case "buy": return Buy(name, a.Length > 1 ? a[1] : "common", fromChat);
                case "equip": return Equip(name, a.Length > 1 ? a[1] : "", fromChat);
            }
            return "unknown action " + action;
        }
        catch (Exception e) { Log("action error (" + name + " " + string.Join("|", a) + "): " + e); return "error"; }
    }

    static string MatchSpecies(string s)
    {
        s = (s ?? "").Trim().TrimEnd('!', '.');
        if (s.Length == 0) return null;
        var names = new List<string>(CaseData.AllFish);
        var map = Carriers(); if (map != null) foreach (var k in map.Keys) if (!names.Contains(k)) names.Add(k);
        foreach (var n in names) if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) return n;
        foreach (var n in names) if (string.Equals(n + "s", s, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Replace(" ", ""), s.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return n;
        string hit = null; int hits = 0;
        foreach (var n in names) if (n.StartsWith(s, StringComparison.OrdinalIgnoreCase)) { hit = n; hits++; }
        return hits == 1 ? hit : null;
    }

    // ---- fish inventory: carrier items (server side) + collections + [FT] COLL, like !eat
    static MethodInfo InvMethod(object inv, string a, string b, Type p0, Type p1)
    {
        foreach (string n in new[] { a, b })
            foreach (MethodInfo m in inv.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != n) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == (p1 == null ? 1 : 2) && ps[0].ParameterType == p0 && (p1 == null || ps[1].ParameterType == p1)) return m;
            }
        return null;
    }
    static MethodInfo invCount;
    static Inventory InvOf(string name) { NetworkUser u = User(name); return u != null && u.master != null ? u.master.inventory : null; }
    static ItemIndex CarrierIdx(string sp) { var map = Carriers(); string c; return map != null && map.TryGetValue(sp, out c) && !string.IsNullOrEmpty(c) ? ItemCatalog.FindItemIndex(c) : ItemIndex.None; }
    static int FishHave(string name, string sp)
    {
        if (IsTest(name)) { Dictionary<string, int> d; int n; return testFish.TryGetValue(name, out d) && d.TryGetValue(sp, out n) ? n : 0; }
        Inventory inv = InvOf(name); ItemIndex idx = CarrierIdx(sp);
        if (inv == null || idx == ItemIndex.None) return 0;
        MethodInfo m = invCount ?? (invCount = InvMethod(inv, "GetItemCountPermanent", "GetItemCount", typeof(ItemIndex), null));
        return m == null ? 0 : Convert.ToInt32(m.Invoke(inv, new object[] { idx }));
    }
    static void RemoveFish(string name, string sp, int n)
    {
        if (n <= 0) return;
        if (IsTest(name)) { testFish[name][sp] = FishHave(name, sp) - n; Log("[test] removed " + n + " " + sp + " from " + name); return; }
        Inventory inv = InvOf(name); ItemIndex idx = CarrierIdx(sp);
        MethodInfo m = InvMethod(inv, "RemoveItemPermanent", "RemoveItem", typeof(ItemIndex), typeof(int));
        m.Invoke(inv, new object[] { idx, n });
        var col = Collections();
        Dictionary<string, int> c;
        if (col != null && col.TryGetValue(name, out c)) { int k; if (c.TryGetValue(sp, out k)) { if (k <= n) c.Remove(sp); else c[sp] = k - n; } }
        Call("Coll", name);
        Log(name + ": removed " + n + " " + sp + " (carrier " + Carriers()[sp] + ")");
    }
    static int AddMoney(string name, int delta)
    {
        var m = MoneyDict(); int v; m.TryGetValue(name, out v); v += delta; m[name] = v;
        if (!IsTest(name)) { Call("ChatRaw", "[FT] MONEY|" + name + "|" + v); Call("Board"); }
        return v;
    }

    static string Sell(string name, string spRaw, string cnt, bool fromChat)
    {
        string sp = MatchSpecies(spRaw);
        if (sp == null) return Refuse(name, "\"" + spRaw + "\" isn't a fish I buy.", fromChat);
        int have = FishHave(name, sp);
        int n = 1;
        if (cnt != null && cnt.Trim().ToLowerInvariant() == "all") n = have; else if (!int.TryParse(cnt, out n)) n = 1;
        if (have <= 0) return Refuse(name, name + ", you don't have a " + sp + ".", fromChat);
        if (n <= 0) n = 1;
        if (n > have) return Refuse(name, name + ", you only have " + have + " " + sp + ".", fromChat);
        int price = CaseData.SellPrice(sp) * n;
        RemoveFish(name, sp, n);
        int total = AddMoney(name, price);
        Teal(name + " sold " + n + " " + sp + " to the Fish Merchant for " + CaseData.Money(price) + " (now " + CaseData.Money(total) + ")." + (sp == "Old Boot" ? " \"...thanks, I guess.\"" : ""), name);
        if (name == LocalName()) { MerchantUI.SetStatus("Sold " + n + " " + sp + " for " + CaseData.Money(price)); Play("Play_UI_coin"); }
        Log(name + ": SOLD " + n + " " + sp + " for " + price + " -> " + total);
        return "sold " + n + " " + sp + " for " + price;
    }

    static bool Busy(string name) { float t; return busyUntil.TryGetValue(name, out t) && Time.time < t; }

    static string Trade(string name, string arg, bool fromChat)
    {
        string prefer = null; int ci = -1;
        int num;
        if (int.TryParse((arg ?? "").Trim(), out num) && num >= 0 && num <= 2) ci = num;
        else
        {
            var cs = CaseData.FindCase(arg);
            string sp = MatchSpecies(arg);
            if (sp != null) { ci = CaseData.TradeCaseOf(sp); prefer = sp; if (ci < 0) return Refuse(name, "the merchant won't take a " + sp + " for a case.", fromChat); }
            else if (cs != null) ci = CaseData.CaseIndex(cs.key);
        }
        if (ci < 0) return Refuse(name, "trade what? e.g. !trade minnow, !trade salmon, !trade swordfish (or common/uncommon/rare).", fromChat);
        if (Busy(name)) return Refuse(name, name + ", wait for your case to finish opening.", fromChat);
        int need = CaseData.FishNeeded(ci);
        var pick = new List<KeyValuePair<string, int>>();
        var cands = new List<string>();
        foreach (string sp in CaseData.AllFish) if (CaseData.FishFits(ci, sp) && FishHave(name, sp) > 0) cands.Add(sp);
        cands.Sort((x, y) =>
        {
            if (x == prefer) return -1; if (y == prefer) return 1;
            int tx = CaseData.TierOf(x) == "lunar" ? 1 : 0, ty = CaseData.TierOf(y) == "lunar" ? 1 : 0;   // keep lunar fish if a red one will do
            if (tx != ty) return tx.CompareTo(ty);
            return FishHave(name, y).CompareTo(FishHave(name, x));
        });
        int got = 0;
        foreach (string sp in cands)
        {
            int take = Math.Min(FishHave(name, sp), need - got);
            if (take > 0) { pick.Add(new KeyValuePair<string, int>(sp, take)); got += take; }
            if (got >= need) break;
        }
        if (got < need) return Refuse(name, name + ", a " + CaseData.Cases[ci].name + " costs " + CaseData.Cases[ci].fishText + " (you have " + got + ").", fromChat);
        var paid = new List<string>();
        foreach (var kv in pick) { RemoveFish(name, kv.Key, kv.Value); paid.Add(kv.Value + " " + kv.Key); }
        return Open(name, ci, "traded " + string.Join(" + ", paid.ToArray()) + " for");
    }

    static string Buy(string name, string arg, bool fromChat)
    {
        int ci; int num;
        if (int.TryParse((arg ?? "").Trim(), out num) && num >= 0 && num <= 2) ci = num;
        else { var cs = CaseData.FindCase(arg); ci = cs == null ? -1 : CaseData.CaseIndex(cs.key); }
        if (ci < 0) return Refuse(name, "which case? !case common, !case uncommon or !case rare.", fromChat);
        if (Busy(name)) return Refuse(name, name + ", wait for your case to finish opening.", fromChat);
        int price = CaseData.Cases[ci].price;
        var m = MoneyDict(); int have; m.TryGetValue(name, out have);
        if (have < price) { return Refuse(name, name + ", a " + CaseData.Cases[ci].name + " costs " + CaseData.Money(price) + " (you have " + CaseData.Money(have) + ").", fromChat); }
        AddMoney(name, -price);
        return Open(name, ci, "paid " + CaseData.Money(price) + " for");
    }

    static string Equip(string name, string item, bool fromChat)
    {
        string cn = CaseData.Canon(item);
        if (cn == null) return Refuse(name, "no case item called \"" + item + "\".", fromChat);
        if (IsTest(name)) { Log("[test] equip " + cn); return "equip (test)"; }
        EnsureCatalogItem(cn);
        object ok = Call("RodCommand", name, "!equip " + cn);
        ObserveEquip();
        return "equip " + cn + " -> " + ok;
    }

    // ---- the roll
    static string Open(string name, int ci, string how)
    {
        CaseData.Item item; string nm; float wear; int seed;
        CaseData.Roll(rng, ci, out item, out nm, out wear, out seed);
        bool owned = Owns(name, nm);
        int dup = owned ? CaseData.DupValue[item.rarity] : 0;
        if (owned) AddMoney(name, dup); else Grant(name, nm, wear, seed);
        busyUntil[name] = Time.time + MerchantUI.ReelSeconds + 0.6f;
        string line = "[FT] CASE|" + name + "|" + CaseData.Cases[ci].key + "|" + nm + "|" + item.rarity + "|" + CaseData.F4(wear) + "|" + seed + "|" + dup;
        Raw(line, name);
        Log(name + ": " + how + " a " + CaseData.Cases[ci].name + " -> " + nm + " (" + CaseData.RarityNames[item.rarity] + ", " + CaseData.WearName(wear) + " " + CaseData.F4(wear) + ", seed " + seed + ")" + (owned ? " DUPLICATE +$" + dup : " NEW"));
        if (name == LocalName() || (IsTest(name) && showTestReel)) MerchantUI.StartReel(name, ci, nm, item.rarity, wear, seed, dup, IsTest(name));
        else if (item.rarity >= 3) Later(MerchantUI.ReelSeconds, () => MerchantUI.AddToast(name + " unboxed " + nm + "!", MerchantUI.RarityColor(item.rarity)));
        Teal(name + " " + how + " " + (CaseData.Cases[ci].name.StartsWith("A") ? "an " : "a ") + CaseData.Cases[ci].name + "... the case is opening!", name);
        string pat = MerchantUI.PatternName(nm, seed);
        string ann = name + " unboxed " + (CaseData.IsStat(nm) ? "<color=#cf6a32>StatTrak\u2122</color> " : "") + "<color=" + CaseData.RarityHex[item.rarity] + ">" + CaseData.Base(nm) + (pat != null ? " | " + pat : "") + "</color> (" + CaseData.WearName(wear) + ")"
                     + (owned ? " - duplicate, the merchant pays " + CaseData.Money(dup) : "!" + (item.kind == "bobber" || item.kind == "skin" ? " Type !equip " + nm : ""));
        int rar = item.rarity;
        Later(MerchantUI.ReelSeconds, () =>
        {
            Say(rar >= 3 ? "<color=#ffd700><b>" + (rar == 4 ? "\u2605 RARE SPECIAL! " : "COVERT! ") + "</b></color>" + ann : rar == 2 ? "<color=#d32ce6>Classified!</color> " + ann : "<color=#cccccc>" + ann + "</color>", name);
            if (rar >= 2 && !IsTest(name)) MerchantLook.Celebrate(MerchantUI.RarityColor(rar));
        });
        return item.name + " r" + item.rarity + " w" + CaseData.F4(wear) + (owned ? " dup " + dup : " new");
    }
    static void Later(float secs, Action a) { later.Add(new KeyValuePair<float, Action>(Time.time + secs, a)); }

    static bool Owns(string name, string item)
    {
        Dictionary<string, float[]> d;
        if (caseItems.TryGetValue(name, out d) && d.ContainsKey(item)) return true;
        if (IsTest(name)) return false;
        var l = OwnedList(Owner(name));
        return l != null && l.Contains(item);
    }

    static void Grant(string name, string item, float wear, int seed)
    {
        Dictionary<string, float[]> d;
        if (!caseItems.TryGetValue(name, out d)) { d = new Dictionary<string, float[]>(); caseItems[name] = d; }
        d[item] = new[] { wear, seed, 0f, 0f, 0f };
        if (IsTest(name)) return;
        try
        {
            EnsureCatalogItem(item);
            object o = Call("MakeOwner", name);
            var l = OwnedList(o);
            if (l != null && !l.Contains(item)) l.Add(item);
            Call("SaveRods");
        }
        catch (Exception e) { Log("grant (shop ownership) error: " + (e.InnerException ?? e).Message); }
        SaveMine();
        BroadcastInv(name);
    }

    // ================================================================ shop catalogue + ownership sync with Fishing<N>
    // StatTrak items go into the catalogue only once somebody owns one (keeps !shop short); price = int.MaxValue = never buyable
    static void EnsureCatalogItem(string name)
    {
        if (F == null || !CaseData.IsStat(name)) return;
        var cat = SF("RodCatalog") as IList;
        Type it = F.GetNestedType("RodItem", All);
        if (cat == null || it == null) return;
        foreach (object o in cat) if ((string)it.GetField("name").GetValue(o) == name) return;
        var b = CaseData.Find(name);
        object n = Activator.CreateInstance(it);
        it.GetField("name").SetValue(n, name);
        it.GetField("kind").SetValue(n, "skin");
        it.GetField("price").SetValue(n, int.MaxValue);
        it.GetField("desc").SetValue(n, "StatTrak case drop (" + (b != null ? CaseData.RarityNames[b.rarity] : "?") + "), not for sale");
        cat.Add(n);
        Log(F.Name + ".RodCatalog: added " + name);
    }

    static void InstallCatalog()
    {
        var cat = SF("RodCatalog") as IList;
        Type it = F.GetNestedType("RodItem", All);
        if (cat == null || it == null) { Log(F.Name + " has no RodCatalog - case skins can't be equipped through the shop"); return; }
        var have = new HashSet<string>();
        foreach (object o in cat) have.Add((string)it.GetField("name").GetValue(o));
        int added = 0;
        foreach (var ci in CaseData.Items)
        {
            if (ci.shop || have.Contains(ci.name)) continue;
            object o = Activator.CreateInstance(it);
            it.GetField("name").SetValue(o, ci.name);
            it.GetField("kind").SetValue(o, ci.kind);
            it.GetField("price").SetValue(o, CaseData.MarketPrice[ci.rarity]);
            it.GetField("desc").SetValue(o, "case drop: " + CaseData.RarityNames[ci.rarity]);
            cat.Add(o); added++;
        }
        Log(F.Name + ".RodCatalog: added " + added + " case items (now " + cat.Count + ")");
        foreach (var kv in caseItems) foreach (string item in kv.Value.Keys) EnsureCatalogItem(item);
    }

    static void Resync()
    {
        int n = 0;
        foreach (var kv in caseItems)
        {
            if (IsTest(kv.Key)) continue;
            try
            {
                object o = Call("MakeOwner", kv.Key);
                var l = OwnedList(o);
                foreach (string item in kv.Value.Keys) if (l != null && !l.Contains(item)) { l.Add(item); n++; }
                string[] eq; caseEquip.TryGetValue(kv.Key, out eq);
                if (eq != null)
                {
                    if (eq[0] != null && l.Contains(eq[0])) o.GetType().GetField("skin", All).SetValue(o, eq[0]);
                    if (eq[1] != null && l.Contains(eq[1])) o.GetType().GetField("bobber", All).SetValue(o, eq[1]);
                }
                Call("BroadcastRod", kv.Key);
            }
            catch (Exception e) { Log("resync error " + kv.Key + ": " + (e.InnerException ?? e).Message); }
        }
        try { Call("SaveRods"); } catch { }
        Log("resync with " + F.Name + ": re-added " + n + " owned item(s) for " + caseItems.Count + " player(s)");
    }

    // remember which case skin/bobber each player has equipped (so it survives a new Fishing<N> / a restart);
    // pick up case items bought straight from the shop (!buy) too
    static void ObserveEquip()
    {
        var owners = RodOwners();
        if (owners == null) return;
        bool changed = false;
        foreach (DictionaryEntry e in owners)
        {
            string name = (string)e.Key; object o = e.Value;
            string skin = o.GetType().GetField("skin", All).GetValue(o) as string, bob = o.GetType().GetField("bobber", All).GetValue(o) as string;
            string[] eq; if (!caseEquip.TryGetValue(name, out eq)) { eq = new string[2]; caseEquip[name] = eq; }
            string ns = CaseSkinLook.IsSkin(skin) ? skin : null, nb = CaseSkinLook.IsBobber(bob) ? bob : null;
            if (ns != eq[0] || nb != eq[1]) { eq[0] = ns; eq[1] = nb; changed = true; BroadcastSkin(name); Log(name + ": case look now " + (ns ?? "-") + " / " + (nb ?? "-")); }
            var l = OwnedList(o);
            if (l != null)
                foreach (string item in l)
                    if (CaseData.IsCaseOnly(item))
                    {
                        Dictionary<string, float[]> d; if (!caseItems.TryGetValue(name, out d)) { d = new Dictionary<string, float[]>(); caseItems[name] = d; }
                        if (!d.ContainsKey(item)) { d[item] = new float[] { 0f, rng.Next(1000), 0f, 0f, 0f }; changed = true; Log(name + ": " + item + " bought from the shop, recorded"); }
                    }
        }
        if (changed) SaveMine();
    }

    static void BroadcastSkin(string name)
    {
        if (IsTest(name)) return;
        string[] eq; caseEquip.TryGetValue(name, out eq);
        Dictionary<string, float[]> d; caseItems.TryGetValue(name, out d);
        Func<string, string> part = it =>
        {
            float[] v; if (it == null) return "-|0|0";
            if (d != null && d.TryGetValue(it, out v)) return it + "|" + CaseData.F4(v[0]) + "|" + (int)v[1];
            return it + "|0.0500|0";
        };
        Raw("[FT] CSKIN|" + name + "|" + part(eq != null ? eq[0] : null) + "|" + part(eq != null ? eq[1] : null), name);
    }
    static void BroadcastInv(string name)
    {
        if (IsTest(name)) return;
        Dictionary<string, float[]> d; if (!caseItems.TryGetValue(name, out d)) return;
        var parts = new List<string>();
        foreach (var kv in d) parts.Add(kv.Key + ":" + CaseData.F4(kv.Value[0]) + ":" + (int)kv.Value[1]);
        Raw("[FT] CINV|" + name + "|" + string.Join(";", parts.ToArray()), name);
    }

    // ================================================================ save file (host\case_items.json)
    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static void SaveMine()
    {
        try
        {
            var sb = new StringBuilder("{\n");
            var names = new List<string>();
            foreach (var k in caseItems.Keys) if (!IsTest(k)) names.Add(k);
            foreach (var k in caseEquip.Keys) if (!IsTest(k) && !names.Contains(k)) names.Add(k);
            for (int i = 0; i < names.Count; i++)
            {
                string n = names[i];
                Dictionary<string, float[]> d; caseItems.TryGetValue(n, out d);
                string[] eq; caseEquip.TryGetValue(n, out eq);
                var its = new List<string>();
                if (d != null) foreach (var kv in d) its.Add(Q(kv.Key) + ": [" + CaseData.F4(kv.Value[0]) + ", " + (int)kv.Value[1] + (kv.Value.Length >= 5 ? ", " + (int)kv.Value[2] + ", " + (int)kv.Value[3] + ", " + (int)kv.Value[4] : "") + "]");
                sb.Append("  " + Q(n) + ": { \"items\": { " + string.Join(", ", its.ToArray()) + " }, \"skin\": " + (eq != null && eq[0] != null ? Q(eq[0]) : "null") + ", \"bobber\": " + (eq != null && eq[1] != null ? Q(eq[1]) : "null") + " }" + (i < names.Count - 1 ? "," : "") + "\n");
            }
            sb.Append("}\n");
            string tmp = savePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(savePath)) File.Delete(savePath);
            File.Move(tmp, savePath);
        }
        catch (Exception e) { Log("save error: " + e.Message); }
    }

    static void LoadMine()
    {
        try
        {
            if (!File.Exists(savePath)) { Log("no " + savePath + " yet"); return; }
            string s = File.ReadAllText(savePath, Encoding.UTF8);
            int p = 0;
            var root = JParse(s, ref p) as Dictionary<string, object>;
            if (root == null) return;
            caseItems.Clear(); caseEquip.Clear();
            foreach (var kv in root)
            {
                var o = kv.Value as Dictionary<string, object>; if (o == null) continue;
                var d = new Dictionary<string, float[]>();
                object its; if (o.TryGetValue("items", out its) && its is Dictionary<string, object>)
                    foreach (var iv in (Dictionary<string, object>)its)
                    {
                        var arr = iv.Value as List<object>;
                        string cn = CaseData.Canon(iv.Key);
                        if (cn == null) continue;
                        var v = new float[5];
                        for (int k = 0; k < 5; k++) v[k] = arr != null && arr.Count > k ? CaseData.ParseF(arr[k] as string, k == 0 ? 0.1f : 0f) : (k == 0 ? 0.1f : 0f);
                        d[cn] = v;
                        if (CaseData.IsStat(cn)) CaseSkinLook.statCounts[kv.Key + "|" + cn] = new[] { (int)v[2], (int)v[3], (int)v[4] };
                    }
                caseItems[kv.Key] = d;
                object sk, bb; o.TryGetValue("skin", out sk); o.TryGetValue("bobber", out bb);
                caseEquip[kv.Key] = new[] { sk as string == "null" ? null : sk as string, bb as string == "null" ? null : bb as string };
            }
            Log("loaded " + caseItems.Count + " player(s) from " + savePath);
        }
        catch (Exception e) { Log("load error: " + e.Message); }
    }

    static object JParse(string s, ref int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length) return null;
        char c = s[p];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); p++;
            while (true)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) return d;
                if (s[p] == '}') { p++; return d; }
                if (s[p] == ',') { p++; continue; }
                string key = JParse(s, ref p) as string;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p < s.Length && s[p] == ':') p++;
                object val = JParse(s, ref p);
                if (key != null) d[key] = val;
            }
        }
        if (c == '[')
        {
            var l = new List<object>(); p++;
            while (true)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) return l;
                if (s[p] == ']') { p++; return l; }
                if (s[p] == ',') { p++; continue; }
                l.Add(JParse(s, ref p));
            }
        }
        if (c == '"')
        {
            var sb = new StringBuilder(); p++;
            while (p < s.Length && s[p] != '"')
            {
                if (s[p] == '\\' && p + 1 < s.Length)
                {
                    p++; char e = s[p];
                    if (e == 'n') sb.Append('\n'); else if (e == 'u' && p + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(p + 1, 4), 16)); p += 4; } else sb.Append(e);
                }
                else sb.Append(s[p]);
                p++;
            }
            p++;
            return sb.ToString();
        }
        int st = p;
        while (p < s.Length && ",}] \t\r\n".IndexOf(s[p]) < 0) p++;
        return s.Substring(st, Math.Max(0, p - st));
    }

    // ================================================================ main loop
    static void Tick()
    {
        if (!enabled)
        {
            if (MerchantLook.root != null) MerchantLook.Destroy();
            MerchantUI.near = false;
            return;
        }
        try
        {
            if (guiGo == null) { guiGo = new GameObject("FishMerchant5Gui"); UnityEngine.Object.DontDestroyOnLoad(guiGo); guiGo.AddComponent<FishMerchant5Gui>(); }
            KillRoulette();
            if (!NetworkServer.active || RoR2.Run.instance == null) { if (placed || MerchantLook.root != null) { MerchantLook.Destroy(); placed = false; } scene = null; MerchantUI.near = false; return; }
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != scene) { scene = sc; placed = false; MerchantLook.Destroy(); nextPlaceTry = Time.time + 8f; Log("scene " + sc); }
            if (F == null || Time.realtimeSinceStartup >= nextNewest) { nextNewest = Time.realtimeSinceStartup + 2f; F = Newest(); }   // cached: scanning every type each frame cost ~18 ms
            if (F != null && F != lastF) { lastF = F; Log("fishing mod is " + F.Name); InstallCatalog(); Resync(); }
            if (F == null) return;
            if (!placed && Time.time >= nextPlaceTry) { nextPlaceTry = Time.time + 3f; TryPlace(); }
            if (placed && MerchantLook.root == null) MerchantLook.Build(merchantPos, merchantYaw);
            if (placed && Time.time >= nextAnnounce) { nextAnnounce = Time.time + 20f; Raw(MerchantLine(), null); }
            if (placed && Time.time >= nextAvoidCheck) { nextAvoidCheck = Time.time + 10f; CheckOverlap(); }
            MerchantLook.Tick();
            // local player in range -> menu
            try
            {
                var lu = LocalUserManager.GetFirstLocalUser();
                MerchantUI.near = placed && lu != null && lu.cachedBody != null && Vector3.Distance(lu.cachedBody.footPosition, merchantPos) <= TradeRange;
            }
            catch { MerchantUI.near = false; }
            string[] lines;
            lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) { try { HandleLine(l); } catch (Exception e) { Log("line error: " + e.Message); } }
            string[] tests;
            lock (testQueue) { tests = testQueue.ToArray(); testQueue.Clear(); }
            foreach (string t in tests) { string[] q = t.Split('|'); var a = new string[q.Length - 1]; Array.Copy(q, 1, a, 0, a.Length); string res = Act(q[0], a, false); Log("TEST " + t + " -> " + res); lock (testLog) testLog.Add("RESULT " + t + " -> " + res); }
            for (int i = later.Count - 1; i >= 0; i--) if (Time.time >= later[i].Key) { var a = later[i].Value; later.RemoveAt(i); try { a(); } catch (Exception e) { Log("later error: " + e.Message); } }
            if (Time.time >= nextObserve) { nextObserve = Time.time + 0.5f; ObserveEquip(); }
            if (Time.time >= nextInvBroadcast)
            {
                nextInvBroadcast = Time.time + 45f;
                foreach (var nu in NetworkUser.readOnlyInstancesList) { if (caseItems.ContainsKey(nu.userName)) { BroadcastInv(nu.userName); BroadcastSkin(nu.userName); BroadcastStats(nu.userName); } }
            }
            SkinRigs();
        }
        catch (Exception e) { Log("tick error: " + e); }
    }

    static string MerchantLine() { return "[FT] MERCHANT|" + merchantPos.x.ToString("0.00", CultureInfo.InvariantCulture) + "|" + merchantPos.y.ToString("0.00", CultureInfo.InvariantCulture) + "|" + merchantPos.z.ToString("0.00", CultureInfo.InvariantCulture) + "|" + merchantYaw.ToString("0", CultureInfo.InvariantCulture); }

    // the roulette table is replaced by the merchant: switch Roulette<N> off and remove its table
    static float nextRouletteCheck;
    static void KillRoulette()
    {
        if (Time.time < nextRouletteCheck) return;
        nextRouletteCheck = Time.time + 5f;
        foreach (var a in FishingScope.Assemblies())
        {
            if (!a.GetName().Name.StartsWith("Roulette")) continue;
            Type look = a.GetType("RouletteLook"), r = null;
            foreach (Type t in SafeTypes(a)) if (t.Name.StartsWith("Roulette") && t.Name != "RouletteLook" && t.GetField("enabled", All) != null) r = t;
            if (r != null && (bool)r.GetField("enabled", All).GetValue(null)) { r.GetField("enabled", All).SetValue(null, false); Log("disabled " + r.Name); }
            if (look != null)
            {
                var root = look.GetField("root", All) != null ? look.GetField("root", All).GetValue(null) as GameObject : null;
                if (root != null) { look.GetMethod("Destroy", All).Invoke(null, null); Log("removed the roulette table (" + a.GetName().Name + ")"); }
            }
        }
        var tbl = GameObject.Find("FishingRoulette");
        if (tbl != null) { tbl.name = "FishingRoulette_removed"; UnityEngine.Object.Destroy(tbl); Log("removed a leftover roulette table object"); }
    }

    // ================================================================ placement
    static List<Vector3> AvoidSpots()
    {
        var l = new List<Vector3>();
        foreach (var a in FishingScope.Assemblies())
        {
            string an = a.GetName().Name;
            if (an == "RoR2" || an.StartsWith("Unity") || an.StartsWith("System") || an == "mscorlib" || an.StartsWith("FishMerchant")) continue;
            foreach (Type t in SafeTypes(a))
            {
                string n = t.Name.ToLowerInvariant();
                if (t.Namespace != null || !(n.Contains("stand") || n.Contains("store") || n.Contains("shop"))) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(Vector3) || !f.Name.ToLowerInvariant().Contains("pos")) continue;
                    try { var v = (Vector3)f.GetValue(null); if (v != Vector3.zero) l.Add(v); } catch { }
                }
            }
        }
        return l;
    }

    static void CheckOverlap()
    {
        foreach (var v in AvoidSpots())
            if (Vector3.Distance(v, merchantPos) < 8f) { Log("another stand at " + v + " is within 8 m - moving the merchant"); placed = false; MerchantLook.Destroy(); nextPlaceTry = Time.time + 1f; return; }
    }

    static void TryPlace()
    {
        object hp = SF("hotspotPos"), wy = SF("waterY"), water = SF("water");
        if (!(hp is Vector3) || !(wy is float) || water == null || ((water is UnityEngine.Object) && !(UnityEngine.Object)water)) return;
        Vector3 c = (Vector3)hp; float waterY = (float)wy;
        if (c == Vector3.zero) return;
        var avoid = AvoidSpots();
        int mask = LayerIndex.world.mask.value;
        for (float r = 12f; r <= 220f; r += 4f)
        {
            int steps = Mathf.Max(16, (int)(r * 0.6f));
            int start = rng.Next(steps);
            for (int k = 0; k < steps; k++)
            {
                float ang = (start + k) * Mathf.PI * 2f / steps;
                Vector3 p = new Vector3(c.x + Mathf.Cos(ang) * r, 0f, c.z + Mathf.Sin(ang) * r);
                float gy; Vector3 n;
                if (!Ground(p.x, p.z, waterY, mask, out gy, out n)) continue;
                if (gy < waterY + 0.8f || gy > waterY + 25f || n.y < 0.93f) continue;
                bool far = true; foreach (var v in avoid) if (Vector3.Distance(v, new Vector3(p.x, gy, p.z)) < 12f) far = false;
                if (!far) continue;
                bool flat = true;
                for (int q = 0; q < 8 && flat; q++)
                {
                    float qa = q * Mathf.PI / 4f, qy; Vector3 qn;
                    if (!Ground(p.x + Mathf.Cos(qa) * 2.4f, p.z + Mathf.Sin(qa) * 2.4f, waterY, mask, out qy, out qn) || Mathf.Abs(qy - gy) > 0.5f) flat = false;
                }
                if (!flat) continue;
                Vector3 g = new Vector3(p.x, gy, p.z);
                if (Physics.Raycast(g + Vector3.up * 0.3f, Vector3.up, 5f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.CheckCapsule(g + Vector3.up * 1.95f, g + Vector3.up * 3.2f, 1.6f, mask, QueryTriggerInteraction.Ignore)) continue;   // bottom of the capsule 0.35 m above the ground
                merchantPos = g;
                Vector3 toLake = c - g; toLake.y = 0f;
                merchantYaw = Mathf.Atan2(toLake.x, toLake.z) * Mathf.Rad2Deg;
                MerchantLook.Build(merchantPos, merchantYaw);
                placed = true; nextAnnounce = Time.time + 20f;
                Raw(MerchantLine(), null);
                Teal("The FISH MERCHANT has set up shop near the lake! Walk up to sell fish, trade them for cases and unbox rod skins (!merchant for help).", null);
                Log("merchant placed on " + scene + " at " + merchantPos + " yaw " + merchantYaw.ToString("0") + " (" + r + " m from hotspot " + c + ", water Y " + waterY.ToString("0.00") + ", avoided " + avoid.Count + " spot(s))");
                return;
            }
        }
        Log("no flat land found near hotspot " + c + ", retrying");
        nextPlaceTry = Time.time + 15f;
    }

    static bool Ground(float x, float z, float waterY, int mask, out float y, out Vector3 normal)
    {
        RaycastHit h; y = 0f; normal = Vector3.up;
        if (!Physics.Raycast(new Vector3(x, waterY + 150f, z), Vector3.down, out h, 300f, mask, QueryTriggerInteraction.Ignore)) return false;
        y = h.point.y; normal = h.normal; return true;
    }

    // ================================================================ case skins on the host's rod rigs
    class Applied { public string key; public Material poleMat, bobMat; public List<Material> mats = new List<Material>(); }
    static readonly Dictionary<object, Applied> applied = new Dictionary<object, Applied>();
    static FieldInfo rPole, rBob, rLine, rKey, rMats; static Type rigType;
    static int skinErrors;

    static float[] WearSeed(string name, string item)
    {
        Dictionary<string, float[]> d; float[] v;
        if (name != null && caseItems.TryGetValue(name, out d) && d.TryGetValue(item, out v)) return v;
        return new[] { 0.05f, 0f, 0f, 0f, 0f };
    }

    static void SkinRigs()
    {
        var rigs = SF("rigs") as IDictionary;
        if (rigs == null) return;
        foreach (DictionaryEntry e in rigs)
        {
            object r = e.Value; string name = e.Key as string;
            try
            {
                if (rigType != r.GetType())
                {
                    rigType = r.GetType();
                    rPole = rigType.GetField("pole", All); rBob = rigType.GetField("bobber", All); rLine = rigType.GetField("line", All);
                    rKey = rigType.GetField("skinKey", All); rMats = rigType.GetField("skinMats", All);
                }
                string key = rKey != null ? rKey.GetValue(r) as string : null;
                if (key == null) continue;
                int bar = key.IndexOf('|');
                string skin = bar >= 0 ? key.Substring(0, bar) : key, bob = bar >= 0 ? key.Substring(bar + 1) : "Plain";
                bool cs = CaseSkinLook.IsSkin(skin), cb = CaseSkinLook.IsBobber(bob);
                Applied st; applied.TryGetValue(r, out st);
                if (!cs && !cb) { if (st != null) applied.Remove(r); continue; }
                var pole = rPole.GetValue(r) as GameObject; var bobGo = rBob.GetValue(r) as GameObject;
                if (pole == null || bobGo == null) continue;
                float[] ws = cs ? WearSeed(name, skin) : null, wb = cb ? WearSeed(name, bob) : null;
                string want = key + (cs ? "|" + ws[0] + "/" + ws[1] : "") + (cb ? "|" + wb[0] + "/" + wb[1] : "");
                var pr = pole.GetComponent<Renderer>(); var br = bobGo.GetComponent<Renderer>();
                if (st != null && st.key == want && (!cs || (pr != null && pr.sharedMaterial == st.poleMat)) && (!cb || (br != null && br.sharedMaterial == st.bobMat))) continue;
                if (st != null) foreach (var m in st.mats) if (m != null) UnityEngine.Object.Destroy(m);
                st = new Applied { key = want };
                if (cs) st.poleMat = CaseSkinLook.ApplyRod(pole, rLine != null ? rLine.GetValue(r) as LineRenderer : null, skin, ws[0], (int)ws[1], st.mats, name + "|" + skin);
                if (cb) st.bobMat = CaseSkinLook.ApplyBobber(bobGo, bob, wb[0], (int)wb[1], st.mats);
                var shopMats = rMats != null ? rMats.GetValue(r) as List<Material> : null;
                if (shopMats != null) shopMats.AddRange(st.mats);   // the shop's next re-skin destroys them
                applied[r] = st;
                Log(name + ": case look applied " + (cs ? skin : "-") + " / " + (cb ? bob : "-"));
            }
            catch (Exception ex) { if (skinErrors++ < 10) Log("skin error (" + name + "): " + ex.Message); }
        }
        if (Time.time >= nextPrune)
        {
            nextPrune = Time.time + 3f;
            var live = new HashSet<object>(); foreach (DictionaryEntry e in rigs) live.Add(e.Value);
            var dead = new List<object>(); foreach (var k in applied.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) applied.Remove(k);
        }
    }
}

