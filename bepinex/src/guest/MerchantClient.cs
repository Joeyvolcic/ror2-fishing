// MerchantClient - guest payload in FISHING.exe for the FISH MERCHANT + cases (build with CaseData.cs, CaseSkinLook.cs,
// MerchantLook.cs, MerchantUI.cs). UnityEngine only; RoR2 by reflection. Does nothing on the host (FishMerchant<N> runs there).
//  [FT] MERCHANT|x|y|z|yaw                 -> builds the stall + Newt (teal outline through walls, name tag)
//  walk within 4.5 m                       -> trade menu (number keys / click); actions go to the host as hidden chat
//                                             "[FT] MERCH|sell|<fish>|<n|all>" / "trade|<0-2>" / "buy|<0-2>" / "equip|<item>"
//  [FT] CASE|player|case|item|rarity|wear|seed|dup -> my own: the CS:GO reel; others' Covert/gold: a toast + stall flash
//  [FT] CINV|name|item:wear:seed;...       -> my unboxed items (menu page 4)
//  [FT] CSKIN|name|skin|w|s|bobber|w|s     -> wear/pattern of everyone's equipped case looks
//  [FT] STAT|name|item|fish|rare|best      -> StatTrak counters (shown on the rod + my HUD line)
//  [FT] ROD|name|rod|skin|bobber           -> who has which case look equipped (FishingClient draws the shop part)
//  [FT] MREPLY|name|text                   -> host refusals for my menu actions
//  [FT] CATCH|name|species|value|total     -> rare-catch sounds (red: legendary chime, lunar: lunar sounds) - the guest half
//                                             of RareSound<N> that used to live in the (never shipped) roulette client
// Case skins are drawn on FishingClient's rod rigs: rigs whose shop skin key names a case item get CaseSkinLook on top.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public class MerchantClientGui : MonoBehaviour
{
    void OnGUI() { if (MerchantClient.enabled && MerchantClient.installed && !MerchantClient.hostMode) MerchantUI.OnGUI(); }
}

public static class MerchantClient
{
    public static bool enabled = true;
    public static bool installed, hostMode;
    public static int version = 1;
    public const float TradeRange = 4.5f;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly ror2;
    static Type netServer, chatType;
    static MethodInfo playSound, firstLocalUser, findItem;
    static readonly List<string> queue = new List<string>();
    static string lastLine, scene;
    static bool built;
    static int money;
    static readonly Dictionary<string, float[]> myItems = new Dictionary<string, float[]>();
    static readonly Dictionary<string, string[]> looks = new Dictionary<string, string[]>();        // name -> {skin, bobber} from [FT] ROD
    static readonly Dictionary<string, float[]> wear = new Dictionary<string, float[]>();           // "name|item" -> {wear, seed}
    static readonly HashSet<string> red = new HashSet<string> { "Swordfish", "Golden Koi", "Anglerfish" };
    static readonly HashSet<string> lunar = new HashSet<string> { "Moon Eel", "Ghost Fish" };
    static readonly Dictionary<string, string> carriers = new Dictionary<string, string> {
        { "Minnow", "Crowbar" }, { "Perch", "BleedOnHit" }, { "Trout", "StickyBomb" }, { "Bass", "IgniteOnKill" }, { "Salmon", "StunChanceOnHit" },
        { "Swordfish", "Missile" }, { "Golden Koi", "ChainLightning" }, { "Moon Eel", "ExtraLife" }, { "Old Boot", "ScrapWhite" },
        { "Clownfish", "FireRing" }, { "Catfish", "NearbyDamageBonus" }, { "Pufferfish", "BounceNearby" }, { "Anglerfish", "Behemoth" }, { "Ghost Fish", "ShockNearby" } };
    static GameObject guiGo;

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_merchant_client.log");
    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static bool IsHost() { try { return (bool)netServer.GetProperty("active").GetValue(null, null); } catch { return false; } }
    static object Member(object o, string n)
    {
        if (o == null) return null;
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(n, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            var f = t.GetField(n, All); if (f != null) return f.GetValue(o);
        }
        return null;
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
            if (IsHost()) { Log("host detected, the host runs FishMerchant<N> itself"); return; }
            foreach (var asm in FishingScope.Assemblies())
            {
                if (asm == typeof(MerchantClient).Assembly) continue;
                Type other = null; try { other = asm.GetType("MerchantClient"); } catch { }
                if (other == null) continue;
                FieldInfo inst = other.GetField("installed", All), en = other.GetField("enabled", All), vf = other.GetField("version", All);
                if (inst == null || en == null || !(bool)inst.GetValue(null) || !(bool)en.GetValue(null)) continue;
                int ov = vf != null ? (int)vf.GetValue(null) : 0;
                if (ov >= version) { Log("another copy (version " + ov + ") is already running - nothing to do"); return; }
                en.SetValue(null, false);
                Log("replaced an older copy (version " + ov + " -> " + version + ")");
            }
            chatType = ror2.GetType("RoR2.Chat");
            playSound = ror2.GetType("RoR2.Util").GetMethod("PlaySound", new[] { typeof(string), typeof(GameObject) });
            firstLocalUser = ror2.GetType("RoR2.LocalUserManager").GetMethod("GetFirstLocalUser", All);
            findItem = ror2.GetType("RoR2.ItemCatalog").GetMethod("FindItemIndex", All);
            MerchantLook.log = Log; CaseSkinLook.log = Log; MerchantUI.log = Log;
            MerchantUI.play = s => Play(s, null);
            MerchantUI.me = () => Me();
            MerchantUI.fishCount = sp => FishCount(sp);
            MerchantUI.money = () => money;
            MerchantUI.myItems = () => new List<string>(myItems.Keys);
            MerchantUI.wearOf = it => { float[] v; return myItems.TryGetValue(it, out v) ? v[0] : -1f; };
            MerchantUI.equippedSkin = () => { string[] l; string m = Me(); return m != null && looks.TryGetValue(m, out l) ? l[0] : null; };
            MerchantUI.equippedBobber = () => { string[] l; string m = Me(); return m != null && looks.TryGetValue(m, out l) ? l[1] : null; };
            MerchantUI.statLine = () => StatLine();
            MerchantUI.send = a => Send("[FT] MERCH|" + a);
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            Action c = OnChat; cc.AddEventHandler(null, c);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            Action u = Update; upd.AddEventHandler(null, u);
            installed = true;
            Log("merchant client installed (version " + version + ")");
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    // ---------------------------------------------------------------- game access
    static object LocalUser() { try { return firstLocalUser.Invoke(null, null); } catch { return null; } }
    static string Me() { object nu = Member(LocalUser(), "currentNetworkUser"); return nu == null ? null : Member(nu, "userName") as string; }
    static Component LocalBody() { return Member(LocalUser(), "cachedBody") as Component; }
    static int FishCount(string sp)
    {
        try
        {
            string c; if (!carriers.TryGetValue(sp, out c)) return 0;
            object inv = Member(Member(Member(LocalUser(), "currentNetworkUser"), "master"), "inventory");
            if (inv == null) return 0;
            object idx = findItem.Invoke(null, new object[] { c });
            foreach (string n in new[] { "GetItemCountPermanent", "GetItemCount" })
                foreach (MethodInfo m in inv.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == n && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == idx.GetType()) return Convert.ToInt32(m.Invoke(inv, new[] { idx }));
        }
        catch { }
        return 0;
    }
    static void Play(string s, GameObject go)
    {
        try
        {
            if (playSound == null) return;
            if (go == null) { var b = LocalBody(); go = b != null ? b.gameObject : null; }
            if (go == null && Camera.main != null) go = Camera.main.gameObject;
            if (go != null) playSound.Invoke(null, new object[] { s, go });
        }
        catch { }
    }
    static void Send(string line)
    {
        try
        {
            object console = ror2.GetType("RoR2.Console").GetProperty("instance", All) != null ? ror2.GetType("RoR2.Console").GetProperty("instance", All).GetValue(null, null) : ror2.GetType("RoR2.Console").GetField("instance", All).GetValue(null);
            object lp = ror2.GetType("RoR2.NetworkUser").GetProperty("readOnlyLocalPlayersList", All) != null ? ror2.GetType("RoR2.NetworkUser").GetProperty("readOnlyLocalPlayersList", All).GetValue(null, null) : ror2.GetType("RoR2.NetworkUser").GetField("readOnlyLocalPlayersList", All).GetValue(null);
            IList locals = lp as IList;
            if (console == null || locals == null || locals.Count == 0) { Log("send: no console/local user"); return; }
            MethodInfo submit = null;
            foreach (MethodInfo m in console.GetType().GetMethods(All))
                if (m.Name == "SubmitCmd" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "NetworkUser") submit = m;
            submit.Invoke(console, new object[] { locals[0], "say \"" + line.Replace("\"", "'") + "\"", false });
            Log("sent " + line);
        }
        catch (Exception e) { Log("send error: " + (e.InnerException ?? e).Message); }
    }

    // ---------------------------------------------------------------- chat
    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            var p = chatType.GetProperty("readOnlyLog", All);
            IList log = p != null ? p.GetValue(null, null) as IList : chatType.GetField("readOnlyLog", All).GetValue(null) as IList;
            if (log == null || log.Count == 0) return;
            int start = log.Count - 1;
            if (lastLine != null) while (start > 0 && !ReferenceEquals(log[start - 1], lastLine) && log.Count - start < 8) start--;
            for (int i = start; i < log.Count; i++)
            {
                string line = log[i] as string;
                if (line == null || ReferenceEquals(line, lastLine)) continue;
                if (line.Contains("[FT] ")) lock (queue) queue.Add(line);
            }
            lastLine = log[log.Count - 1] as string;
        }
        catch { }
    }

    static void Handle(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        if (!plain.StartsWith("[FT] ")) return;
        string[] p = plain.Substring(5).Split('|');
        string me = Me();
        switch (p[0])
        {
            case "MERCHANT":
                if (p.Length >= 5)
                {
                    var pos = new Vector3(F(p[1]), F(p[2]), F(p[3])); float yaw = F(p[4]);
                    if (!built || MerchantLook.root == null || Vector3.Distance(pos, MerchantLook.position) > 0.5f) { MerchantLook.Build(pos, yaw); built = true; Log("merchant at " + pos); }
                }
                break;
            case "CASE":
                if (p.Length >= 8)
                {
                    int ci = CaseData.CaseIndex(p[2]), rar = (int)F(p[4]), seed = (int)F(p[6]), dup = (int)F(p[7]); float w = F(p[5]);
                    if (p[1] == me) MerchantUI.StartReel(p[1], ci, p[3], rar, w, seed, dup, false);
                    else if (rar >= 3) MerchantUI.AddToast(p[1] + " is unboxing something special...", MerchantUI.RarityColor(rar));
                    if (rar >= 2) MerchantLook.Celebrate(MerchantUI.RarityColor(rar));
                    if (p[1] == me && dup == 0) myItems[p[3]] = new[] { w, seed };
                    wear[p[1] + "|" + p[3]] = new[] { w, (float)seed };
                }
                break;
            case "CINV":
                if (p.Length >= 2)
                {
                    string[] items = p.Length >= 3 ? p[2].Split(';') : new string[0];
                    foreach (string it in items)
                    {
                        string[] q = it.Split(':'); if (q.Length < 3) continue;
                        wear[p[1] + "|" + q[0]] = new[] { F(q[1]), F(q[2]) };
                        if (p[1] == me) myItems[q[0]] = new[] { F(q[1]), F(q[2]) };
                    }
                }
                break;
            case "CSKIN":
                if (p.Length >= 8)
                {
                    if (p[2] != "-") wear[p[1] + "|" + p[2]] = new[] { F(p[3]), F(p[4]) };
                    if (p[5] != "-") wear[p[1] + "|" + p[5]] = new[] { F(p[6]), F(p[7]) };
                }
                break;
            case "STAT":
                if (p.Length >= 6) CaseSkinLook.statCounts[p[1] + "|" + p[2]] = new[] { (int)F(p[3]), (int)F(p[4]), (int)F(p[5]) };
                break;
            case "ROD":
                if (p.Length >= 5) looks[p[1]] = new[] { p[3], p[4] };
                break;
            case "MREPLY":
                if (p.Length >= 3 && p[1] == me) { MerchantUI.SetStatus("<color=#ff8866>" + p[2] + "</color>"); Play("Play_UI_insufficient_funds", null); }
                break;
            case "MONEY":
                if (p.Length >= 3 && p[1] == me) money = (int)F(p[2]);
                break;
            case "BOARD":
                for (int i = 1; i < p.Length; i++) { int k = p[i].LastIndexOf(':'); if (k > 0 && p[i].Substring(0, k) == me) money = (int)F(p[i].Substring(k + 1)); }
                break;
            case "CATCH":
                if (p.Length >= 5)
                {
                    if (p[1] == me) money = (int)F(p[4]);
                    if (red.Contains(p[2])) { Play("Play_UI_item_land_tier3", null); Play("Play_UI_achievementUnlock", null); }
                    else if (lunar.Contains(p[2])) { Play("Play_UI_lunarCache_open", null); Play("Play_ui_lunar_coin_drop", null); }
                }
                break;
        }
    }
    static float F(string s) { return CaseData.ParseF(s, 0f); }

    static string StatLine()
    {
        string me = Me(); string[] l;
        if (me == null || !looks.TryGetValue(me, out l) || !CaseData.IsStat(l[0])) return null;
        int[] c; CaseSkinLook.statCounts.TryGetValue(me + "|" + l[0], out c); c = c ?? new int[3];
        return "<color=#cf6a32>StatTrak™</color> " + CaseData.Base(l[0]) + ": <color=#ff9933><b>" + c[0] + "</b></color> fish  -  " + c[1] + " rare  -  best " + CaseData.Money(c[2]);
    }

    // ---------------------------------------------------------------- frame
    static void Update()
    {
        if (!enabled) { if (MerchantLook.root != null) MerchantLook.Destroy(); MerchantUI.near = false; return; }
        try
        {
            if (IsHost()) { hostMode = true; if (MerchantLook.root != null) MerchantLook.Destroy(); MerchantUI.near = false; return; }
            hostMode = false;
            if (guiGo == null) { guiGo = new GameObject("MerchantClientGui"); UnityEngine.Object.DontDestroyOnLoad(guiGo); guiGo.AddComponent<MerchantClientGui>(); }
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != scene) { scene = sc; built = false; MerchantLook.Destroy(); }
            string[] lines;
            lock (queue) { lines = queue.ToArray(); queue.Clear(); }
            foreach (string l in lines) { try { Handle(l); } catch (Exception e) { Log("line error: " + e.Message); } }
            MerchantLook.Tick();
            var b = LocalBody();
            object fp = b != null ? Member(b, "footPosition") : null;
            MerchantUI.near = MerchantLook.root != null && fp is Vector3 && Vector3.Distance((Vector3)fp, MerchantLook.position) <= TradeRange;
            SkinRigs();
        }
        catch (Exception e) { if (errs++ < 10) Log("update error: " + e); }
    }
    static int errs;

    // ---------------------------------------------------------------- case looks on FishingClient's rigs
    class Applied { public string key; public Material poleMat, bobMat; public List<Material> mats = new List<Material>(); }
    static readonly Dictionary<object, Applied> applied = new Dictionary<object, Applied>();
    static Type fcType; static FieldInfo rigsF; static float nextFind, nextPrune;
    static int skinErrs;

    static IDictionary Rigs()
    {
        if ((fcType == null || !(bool)fcType.GetField("enabled", All).GetValue(null)) && Time.time >= nextFind)
        {
            nextFind = Time.time + 5f; fcType = null;
            foreach (var a in FishingScope.Assemblies())
            {
                Type t = null; try { t = a.GetType("FishingClient"); } catch { }
                if (t == null || t.GetField("enabled", All) == null || !(bool)t.GetField("enabled", All).GetValue(null)) continue;
                fcType = t; rigsF = t.GetField("rigs", All);
            }
        }
        return rigsF != null ? rigsF.GetValue(null) as IDictionary : null;
    }
    static float[] WearOf(string name, string item) { float[] v; return wear.TryGetValue(name + "|" + item, out v) ? v : new[] { 0.05f, 0f }; }

    static void SkinRigs()
    {
        var rigs = Rigs();
        if (rigs == null) return;
        foreach (DictionaryEntry e in rigs)
        {
            object r = e.Value; string name = e.Key as string;
            try
            {
                object state = Member(r, "skin");
                string key = Member(state, "key") as string;
                if (key == null) continue;
                int bar = key.IndexOf('|');
                string skin = bar >= 0 ? key.Substring(0, bar) : key, bob = bar >= 0 ? key.Substring(bar + 1) : "Plain";
                bool cs = CaseSkinLook.IsSkin(skin), cb = CaseSkinLook.IsBobber(bob);
                Applied st; applied.TryGetValue(r, out st);
                if (!cs && !cb) { if (st != null) applied.Remove(r); continue; }
                var pole = Member(r, "pole") as GameObject; var bobGo = Member(r, "bobber") as GameObject;
                if (pole == null || bobGo == null) continue;
                float[] ws = WearOf(name, skin), wb = WearOf(name, bob);
                string want = key + "|" + ws[0] + "/" + ws[1] + "|" + wb[0] + "/" + wb[1];
                var pr = pole.GetComponent<Renderer>(); var br = bobGo.GetComponent<Renderer>();
                if (st != null && st.key == want && (!cs || (pr != null && pr.sharedMaterial == st.poleMat)) && (!cb || (br != null && br.sharedMaterial == st.bobMat))) continue;
                if (st != null) foreach (var m in st.mats) if (m != null) UnityEngine.Object.Destroy(m);
                st = new Applied { key = want };
                if (cs) st.poleMat = CaseSkinLook.ApplyRod(pole, Member(r, "line") as LineRenderer, skin, ws[0], (int)ws[1], st.mats, name + "|" + skin);
                if (cb) st.bobMat = CaseSkinLook.ApplyBobber(bobGo, bob, wb[0], (int)wb[1], st.mats);
                var shopMats = Member(state, "mats") as List<Material>;
                if (shopMats != null) shopMats.AddRange(st.mats);
                applied[r] = st;
                Log(name + ": case look " + (cs ? skin : "-") + " / " + (cb ? bob : "-"));
            }
            catch (Exception ex) { if (skinErrs++ < 10) Log("skin error (" + name + "): " + ex.Message); }
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
