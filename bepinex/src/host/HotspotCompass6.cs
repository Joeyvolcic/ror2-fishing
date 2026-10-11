#define HOST
// HotspotCompass<N> - host add-on (JV94's game). TEMPLATE: build a version with mkver.ps1 -N <n> (renames HotspotCompass6,
// CompassHud6, CompassLook6, GadgetLook6, HotBeam13). Sources: this file + CompassLook.cs + GadgetLook.cs, -define:HOST.
//  * Hotspots invisible: the purple hotspot looks ("FishingHotspotPurple" HotBeam7, "FishingHotspot" Fishing<N>) and the golden
//    one ("FishingHotspotGold", GameplayFx<N>/GoldLook) are hidden (renderers + lights off). They still exist, move, give odds.
//  * HotBeam13 (below) = HotBeam7 without visuals: radius 10 m + reachability relocation, re-broadcasts [FT] HOT after a move.
//  * Three shop ITEMS added to the live Fishing<N>.RodCatalog (kind "item" -> the rod stall's ITEMS tab, !shop, !buy, the
//    inventory window; same money, same stall-only rule, saved in rod_owners.json by the fishing mod):
//      Compass $1,500        hold SHIFT (Utility skill): a brass pocket compass, needle -> hotspot, gold needle -> golden hotspot
//      Range Finder $3,000   hold R (Special skill): zoomed binocular view + distance to the aimed point, "- HOTSPOT" on it
//      Cast Distance Tracker $4,000   strip at the bottom while fishing: cast distance + bobber-to-hotspot distance; landing ring
//    A Fishing<N> loaded later doesn't know these names and drops them from its owners table: ownership is mirrored to
//    C:\RoR2-FishingClient\host\gadget_owners.txt ("player|item") and put back every second.
//    Aliases handled here: "!buy tracker" / "cdt" / "binoculars" -> RodCommand with the real name.
//  * Guests (FISHING.exe CompassClient): "[FT] GADGETS|name|compass,rangefinder,tracker|rangeMult|castRange|lobMin|pitchMin|pitchMax"
//    (on change + every 15 s), "[FT] CASTAT|name|dist|x|y|z|water" / "[FT] CASTEND|name" for tracker owners.
// Log: %TEMP%\ror2_compass.log
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
using CL = CompassLook6;
using GL = GadgetLook6;

public class CompassHud6 : MonoBehaviour
{
    void OnGUI() { HotspotCompass6.OnGui(); }
}

public static class HotspotCompass6
{
    public static bool enabled = true;
    public static bool restoreOnDisable = true;      // a newer HotspotCompass<N> sets this false before disabling us
    public static bool hideHotspots = true;
    public static bool previewLocal;                  // test: the host player counts as owning all three (nothing saved)
    public static string shotRequest;                 // test: screenshot path (taken next frame, includes the HUD)
    public const string CompassItem = "Compass", RangeItem = "Range Finder", TrackerItem = "Cast Distance Tracker";
    public static int compassPrice = 1500, rangePrice = 3000, trackerPrice = 4000;
    public static string mirrorPath = FishingPaths.Data("gadget_owners.txt");
    public static readonly HashSet<string> mirror = new HashSet<string>();   // "player|item"

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_compass.log"), DateTime.Now.ToString("HH:mm:ss ") + "[HotspotCompass6] " + s + Environment.NewLine); } catch { } }
    public static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }

    public static Type Newest(string prefix, bool needEnabled)
    {
        Type best = null; int bn = -1; var rx = new Regex("^" + prefix + "(\\d+)$");
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                Match m = rx.Match(t.Name); if (!m.Success) continue;
                if (needEnabled) { FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static); if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue; }
                int n = int.Parse(m.Groups[1].Value); if (n > bn) { bn = n; best = t; }
            }
        return best;
    }

    // ---------------------------------------------------------------- Fishing<N> by reflection
    public static Type F;
    static FieldInfo fHotPos, fHotR, fHasHot, fHot2Pos, fHot2R, fHasHot2, fHot2Scene, fWaterY, fHasWater, fWaterScene,
                     fRigs, fAnglers, fQueue, fCastRange, fLobMin, fPMin, fPMax, fCatalog, fOwners;
    static MethodInfo mScene, mChat, mChatRaw, mBroadcastHot, mRange, mRodCommand, mZoneAt;
    static Action chatHook, frame; static bool installed;

    static void RefreshFishing()
    {
        Type t = Newest("Fishing", true);
        if (t == F) return;
        F = t;
        if (F == null) { Log("no Fishing<N> running"); return; }
        if (installed && chatHook != null) { try { Chat.onChatChanged -= chatHook; Chat.onChatChanged += chatHook; } catch { } }   // stay after its chat handler
        Func<string, FieldInfo> f = n => F.GetField(n, All);
        Func<string, int, MethodInfo> m = (n, k) => { foreach (MethodInfo mi in F.GetMethods(All)) if (mi.Name == n && mi.GetParameters().Length == k) return mi; return null; };
        fHotPos = f("hotspotPos"); fHotR = f("hotspotRadius"); fHasHot = f("hasHotspot");
        fHot2Pos = f("hot2Pos"); fHot2R = f("hot2Radius"); fHasHot2 = f("hasHot2"); fHot2Scene = f("hot2Scene");
        fWaterY = f("waterY"); fHasWater = f("hasWater"); fWaterScene = f("waterScene");
        fRigs = f("rigs"); fAnglers = f("anglers"); fQueue = f("chatQueue"); fCatalog = f("RodCatalog"); fOwners = f("rodOwners");
        fCastRange = f("castRange"); fLobMin = f("lobMinDist"); fPMin = f("lobPitchMin"); fPMax = f("lobPitchMax");
        mScene = m("SceneName", 0); mChat = m("Chat", 1); mChatRaw = m("ChatRaw", 1); mBroadcastHot = m("BroadcastHot", 0); mRange = m("RangeMult", 1); mRodCommand = m("RodCommand", 2); mZoneAt = m("ZoneNameAt", 2);
        Log("fishing source -> " + F.Name + " (hot " + (fHotPos != null) + ", hot2 " + (fHot2Pos != null) + ", rigs " + (fRigs != null) + ", catalog " + (fCatalog != null)
            + ", owners " + (fOwners != null) + ", RodCommand " + (mRodCommand != null) + ", BroadcastHot " + (mBroadcastHot != null) + ")");
        catalogDone = false;
    }

    static T Sv<T>(FieldInfo fi, T def) { try { if (fi == null) return def; object v = fi.GetValue(null); return v is T ? (T)v : def; } catch { return def; } }
    public static string Scene() { try { return mScene == null ? null : mScene.Invoke(null, null) as string; } catch { return null; } }
    public static bool WaterHere() { return Sv(fHasWater, false) && Sv<string>(fWaterScene, null) == Scene(); }
    public static float WaterY() { return Sv(fWaterY, float.NaN); }
    public static bool HotHere(out Vector3 p, out float r) { p = Sv(fHotPos, Vector3.zero); r = Sv(fHotR, 10f); return Sv(fHasHot, false) && WaterHere(); }
    public static bool GoldHere(out Vector3 p, out float r) { p = Sv(fHot2Pos, Vector3.zero); r = Sv(fHot2R, 8f); return Sv(fHasHot2, false) && Sv<string>(fHot2Scene, null) == Scene() && WaterHere(); }
    static float RangeMult(string name) { try { return mRange == null ? 1f : (float)mRange.Invoke(null, new object[] { name }); } catch { return 1f; } }
    public static void FChat(string s) { try { if (mChat != null) mChat.Invoke(null, new object[] { s }); } catch (Exception e) { Log("chat error: " + e.Message); } }
    public static void FChatRaw(string s) { try { if (mChatRaw != null) mChatRaw.Invoke(null, new object[] { s }); } catch (Exception e) { Log("chatraw error: " + e.Message); } }

    // ---------------------------------------------------------------- install
    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (Regex.IsMatch(t.Name, "^HotspotCompass\\d+$") && t != typeof(HotspotCompass6))
                {
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    FieldInfo ro = t.GetField("restoreOnDisable", BindingFlags.Public | BindingFlags.Static);
                    if (ro != null) ro.SetValue(null, false);
                    if (en != null && (bool)en.GetValue(null)) { en.SetValue(null, false); Log("disabled older " + t.Name); }
                }
        LoadMirror();
        HotBeam13.DisableOlder();
        RefreshFishing();
        GL.Log = s => Log("gadget: " + s);
        frame = Frame; RoR2Application.onUpdate += frame;
        chatHook = OnChat; Chat.onChatChanged += chatHook;
        installed = true;
        Log("installed: items Compass $" + compassPrice + ", Range Finder $" + rangePrice + ", Tracker $" + trackerPrice + "; " + mirror.Count + " mirrored ownership(s)");
    }

    static void LoadMirror()
    {
        try
        {
            if (!File.Exists(mirrorPath)) return;
            foreach (string l in File.ReadAllLines(mirrorPath)) { string n = l.Trim(); if (n.Length > 0 && !n.StartsWith("#") && n.Contains("|")) mirror.Add(n); }
        }
        catch (Exception e) { Log("mirror load error: " + e.Message); }
    }

    static void SaveMirror()
    {
        try
        {
            var sb = new StringBuilder("# compass / range finder / tracker owners (HotspotCompass add-on): player|item\n");
            foreach (string n in mirror) sb.Append(n).Append('\n');
            string tmp = mirrorPath + ".tmp"; File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(mirrorPath)) File.Delete(mirrorPath);
            File.Move(tmp, mirrorPath);
        }
        catch (Exception e) { Log("mirror save error: " + e.Message); }
    }

    // ---------------------------------------------------------------- shop items in Fishing<N>.RodCatalog + ownership mirror
    static bool catalogDone;
    static readonly string[][] Items = {
        new[] { CompassItem, "1500", "gadget - hold SHIFT: a pocket compass, needle points to the hotspot (gold needle: golden hotspot)" },
        new[] { RangeItem, "3000", "gadget - hold R: zoomed view + distance to what you aim at; says HOTSPOT when you're on it" },
        new[] { TrackerItem, "4000", "rod upgrade, any rod: while fishing shows your cast distance and how far the bobber is from the hotspot" } };

    static void EnsureCatalog()
    {
        IList cat = fCatalog == null ? null : fCatalog.GetValue(null) as IList;
        Type ri = F.GetNestedType("RodItem", All);
        if (cat == null || ri == null) return;
        Items[0][1] = compassPrice.ToString(Ci); Items[1][1] = rangePrice.ToString(Ci); Items[2][1] = trackerPrice.ToString(Ci);
        foreach (string[] it in Items)
        {
            object have = null;
            foreach (object o in cat) if ((string)ri.GetField("name").GetValue(o) == it[0]) have = o;
            if (have == null)
            {
                have = Activator.CreateInstance(ri);
                ri.GetField("name").SetValue(have, it[0]); ri.GetField("kind").SetValue(have, "item");
                cat.Add(have);
                Log("added '" + it[0] + "' to " + F.Name + ".RodCatalog");
            }
            ri.GetField("price").SetValue(have, int.Parse(it[1], Ci)); ri.GetField("desc").SetValue(have, it[2]);
        }
        catalogDone = true;
    }

    static IList OwnedList(string name, bool create)
    {
        IDictionary ro = fOwners == null ? null : fOwners.GetValue(null) as IDictionary;
        if (ro == null || name == null) return null;
        object o = ro.Contains(name) ? ro[name] : null;
        if (o == null && create) { Type ot = F.GetNestedType("RodOwner", All); if (ot == null) return null; o = Activator.CreateInstance(ot); ro[name] = o; }
        return o == null ? null : o.GetType().GetField("owned").GetValue(o) as IList;
    }

    public static bool Owns(string name, string item)
    {
        if (name == null) return false;
        if (previewLocal && name == localName) return true;
        IList l = OwnedList(name, false);
        return (l != null && l.Contains(item)) || mirror.Contains(name + "|" + item);
    }

    static void SyncOwners()
    {
        // put mirrored ownership back (a newer Fishing<N> dropped names it didn't know)
        foreach (string e in mirror)
        {
            string[] p = e.Split('|'); IList l = OwnedList(p[0], true);
            if (l != null && !l.Contains(p[1])) { l.Add(p[1]); Log("restored " + p[1] + " for " + p[0]); }
        }
        // learn new purchases
        IDictionary ro = fOwners == null ? null : fOwners.GetValue(null) as IDictionary;
        if (ro == null) return;
        var fresh = new List<string[]>();
        foreach (DictionaryEntry de in ro)
        {
            string n = de.Key as string; if (n == null || n.StartsWith("~")) continue;   // "~Name" = test buyers
            IList l = de.Value.GetType().GetField("owned").GetValue(de.Value) as IList; if (l == null) continue;
            foreach (string[] it in Items) if (l.Contains(it[0]) && !mirror.Contains(n + "|" + it[0])) fresh.Add(new[] { n, it[0] });
        }
        if (fresh.Count == 0) return;
        foreach (string[] f in fresh)
        {
            mirror.Add(f[0] + "|" + f[1]);
            Log(f[0] + " now owns the " + f[1]);
            FChat(f[1] == CompassItem ? f[0] + ": hold SHIFT to take out your compass - the red needle points to the hotspot, the thin gold one to the golden hotspot."
                : f[1] == RangeItem ? f[0] + ": hold R to look through your range finder - it shows the distance to what you aim at and says HOTSPOT when you're on it."
                : f[0] + ": your Cast Distance Tracker shows each cast's distance and how far your bobber is from the hotspot while you fish.");
            GadgetLine(f[0]);
        }
        SaveMirror();
    }

    static string GadgetList(string n)
    {
        var l = new List<string>();
        if (Owns(n, CompassItem)) l.Add("compass"); if (Owns(n, RangeItem)) l.Add("rangefinder"); if (Owns(n, TrackerItem)) l.Add("tracker");
        return string.Join(",", l.ToArray());
    }

    static readonly Dictionary<string, string> lastGadgetLine = new Dictionary<string, string>();
    static readonly Dictionary<string, float> lastGadgetAt = new Dictionary<string, float>();
    static void GadgetLine(string n) { GadgetLine(n, true); }
    static void GadgetLine(string n, bool force)
    {
        string g = GadgetList(n); if (g.Length == 0) return;
        string line = "[FT] GADGETS|" + n + "|" + g + "|" + RangeMult(n).ToString("0.###", Ci) + "|" + Sv(fCastRange, 200f).ToString("0.##", Ci) + "|" + Sv(fLobMin, 15f).ToString("0.##", Ci)
                 + "|" + Sv(fPMin, -10f).ToString("0.##", Ci) + "|" + Sv(fPMax, 45f).ToString("0.##", Ci) + "|" + (SlotGadget(n, "utility") ?? "-") + "|" + (SlotGadget(n, "special") ?? "-");
        string prev; float at;
        if (!force && lastGadgetLine.TryGetValue(n, out prev) && prev == line && lastGadgetAt.TryGetValue(n, out at) && Time.unscaledTime - at < 15f) return;
        lastGadgetLine[n] = line; lastGadgetAt[n] = Time.unscaledTime;
        FChatRaw(line);
    }

    // ---------------------------------------------------------------- Inventory<N> gadget slots (Shift = "utility", R = "special")
    static Type inv; static MethodInfo mEquippedGadget;
    static void RegisterWithInventory()
    {
        Type t = Newest("Inventory", true);
        if (t == inv) return;
        inv = t; mEquippedGadget = null;
        if (inv == null) return;
        try
        {
            mEquippedGadget = inv.GetMethod("EquippedGadget", BindingFlags.Public | BindingFlags.Static);
            MethodInfo rg = inv.GetMethod("RegisterGadget", BindingFlags.Public | BindingFlags.Static);
            if (rg == null) { Log(inv.Name + " has no RegisterGadget"); return; }
            rg.Invoke(null, new object[] { "compass", CompassItem, "rare", "*", "Hold the key to take out a brass pocket compass: the red needle points to the hotspot, the gold one to the golden hotspot.", "utility", (Func<string, bool>)(p => Owns(p, CompassItem)) });
            rg.Invoke(null, new object[] { "rangefinder", RangeItem, "legendary", "*", "Hold the key to look through it: zoomed view, distance to what you aim at, says HOTSPOT when you are on it.", "special", (Func<string, bool>)(p => Owns(p, RangeItem)) });
            Log("registered the Compass + Range Finder gadgets with " + inv.Name);
        }
        catch (Exception e) { Log("inventory register error: " + (e.InnerException ?? e).Message); }
    }

    // gadget id on that key for the player: the inventory slot if one is set, else the default (Compass on Shift, Range Finder
    // on R) when owned and not already sitting on the other key
    public static string SlotGadget(string player, string slot)
    {
        if (player == null) return null;
        string other = slot == "utility" ? "special" : "utility", id = null, otherId = null;
        try { if (mEquippedGadget != null) { id = mEquippedGadget.Invoke(null, new object[] { player, slot }) as string; otherId = mEquippedGadget.Invoke(null, new object[] { player, other }) as string; } } catch { }
        if (id != null) return id;
        string def = slot == "utility" ? "compass" : "rangefinder";
        if (otherId == def) return null;
        return Owns(player, def == "compass" ? CompassItem : RangeItem) ? def : null;
    }

    // ---------------------------------------------------------------- per frame
    static GameObject hud;
    static float nextFind, nextHide, nextSlow, nextTrack, nextLines;
    static int errors, hiddenCount, slowN;
    static readonly List<Component> hidden = new List<Component>();
    static readonly GL.In gin = new GL.In();
    static bool inRun;
    static string localName;

    static void Frame()
    {
        if (!enabled) { Cleanup(); return; }
        try
        {
            float t = Time.unscaledTime;
            inRun = NetworkServer.active && RoR2.Run.instance != null;
            if (t >= nextFind) { nextFind = t + 3f; RefreshFishing(); }
            if (hud == null) { hud = new GameObject("HotspotCompassHud6"); UnityEngine.Object.DontDestroyOnLoad(hud); hud.AddComponent<CompassHud6>(); }
            if (F == null) return;
            if (!catalogDone) EnsureCatalog();
            if (t >= nextSlow) { nextSlow = t + 1f; HotBeam13.Tick(); EnsureCatalog(); SyncOwners(); if (slowN++ % 3 == 0) RegisterWithInventory(); }
            if (!inRun) { gin.hasMe = false; GL.Frame(gin); return; }
            if (t >= nextHide) { nextHide = t + 0.2f; HideLooks(); }
            if (t >= nextTrack) { nextTrack = t + 0.1f; TrackCasts(); }
            if (t >= nextLines) { nextLines = t + 1f; foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList) if (nu != null && !nu.isLocalPlayer) GadgetLine(nu.userName, false); }   // slot change: at once; else every 15 s
            Commands();
            UpdateLocal();
            GL.Frame(gin);
            if (shotRequest != null) { string p = shotRequest; shotRequest = null; ScreenCapture.CaptureScreenshot(p); Log("screenshot -> " + p); }
        }
        catch (Exception e) { if (errors++ < 20) Log("frame error: " + e); }
    }

    static void Cleanup()
    {
        try { RoR2Application.onUpdate -= frame; } catch { }
        try { Chat.onChatChanged -= chatHook; } catch { }
        if (hud != null) UnityEngine.Object.Destroy(hud); hud = null;
        try { GL.Clear(); } catch { }
        if (restoreOnDisable) try { CL.Show(hidden); } catch { }
        Log("disabled");
    }

    static readonly string[] Looks = { "FishingHotspotPurple", "FishingHotspot", "FishingHotspotGold" };
    static void HideLooks()
    {
        if (!hideHotspots) { if (hidden.Count > 0) { CL.Show(hidden); Log("hotspot looks shown again"); } return; }
        int n = 0;
        foreach (string nm in Looks) n += CL.Hide(GameObject.Find(nm), hidden);
        if (hidden.Count > 200) hidden.RemoveAll(c => c == null);
        if (n > 0 && hiddenCount++ < 30) Log("hid " + n + " hotspot renderer(s)/light(s)");
    }

    public static void OnGui()
    {
        if (!enabled || !inRun) return;
        try { GL.OnGui(gin); } catch (Exception e) { if (errors++ < 20) Log("gui error: " + e); }
    }

    static Camera Cam()
    {
        foreach (CameraRigController c in CameraRigController.readOnlyInstancesList) if (c != null && c.sceneCam != null && c.localUserViewer != null) return c.sceneCam;
        foreach (CameraRigController c in CameraRigController.readOnlyInstancesList) if (c != null && c.sceneCam != null) return c.sceneCam;
        return Camera.main;
    }

    static int AnglerState(string name)
    {
        try
        {
            IDictionary an = fAnglers == null ? null : fAnglers.GetValue(null) as IDictionary;
            if (an == null || name == null || !an.Contains(name)) return 0;
            object a = an[name]; return (int)a.GetType().GetField("state", All).GetValue(a);
        }
        catch { return 0; }
    }

    static object RigOf(string name)
    {
        IDictionary rg = fRigs == null ? null : fRigs.GetValue(null) as IDictionary;
        return rg != null && name != null && rg.Contains(name) ? rg[name] : null;
    }
    static float RF(object rig, string n) { return (float)rig.GetType().GetField(n, All).GetValue(rig); }

    static bool InputBlocked()
    {
        try { foreach (var cb in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>()) { if (cb == null) continue; PropertyInfo p = cb.GetType().GetProperty("showInput", All); object v = p != null ? p.GetValue(cb, null) : null; if (v == null) { FieldInfo fi = cb.GetType().GetField("showInput", All); if (fi != null) v = fi.GetValue(cb); } if (v is bool && (bool)v) return true; } } catch { }
        Type rp = Newest("ReqPanel", false);   // Inventory<N> / request panel "open" flag (the same one Fishing<N> asks)
        if (rp != null) { FieldInfo o = rp.GetField("open", BindingFlags.Public | BindingFlags.Static); if (o != null && o.FieldType == typeof(bool) && (bool)o.GetValue(null)) return true; }
        return false;
    }

    static void UpdateLocal()
    {
        NetworkUser me = null;
        foreach (NetworkUser nu in NetworkUser.readOnlyLocalPlayersList) { me = nu; break; }
        localName = me == null ? null : me.userName;
        CharacterBody body = me == null ? null : me.GetCurrentBody();
        gin.hasMe = body != null && body.healthComponent != null && body.healthComponent.alive;
        gin.cam = Cam();
        gin.ownCompass = Owns(localName, CompassItem); gin.ownRange = Owns(localName, RangeItem); gin.ownTracker = Owns(localName, TrackerItem);
        gin.compassPrice = "$" + compassPrice.ToString("#,0", Ci); gin.rangePrice = "$" + rangePrice.ToString("#,0", Ci);
        gin.hot = HotHere(out gin.hotPos, out gin.hotR);
        gin.gold = GoldHere(out gin.goldPos, out gin.goldR);
        GadgetLook6.zoneAt = mZoneAt == null ? null : (Func<Vector3, string>)(p => { try { return mZoneAt.Invoke(null, new object[] { p.x, p.z }) as string; } catch { return null; } });
        gin.waterY = WaterY(); gin.hasWater = WaterHere() && !float.IsNaN(gin.waterY);
        gin.mask = LayerIndex.world.mask;
        if (!gin.hasMe) { gin.utilDown = gin.specialDown = false; return; }
        gin.me = body.footPosition;
        InputBankTest ib = body.inputBank;
        gin.utilDown = ib != null && ib.skill3.down; gin.specialDown = ib != null && ib.skill4.down;
        gin.utilGadget = SlotGadget(localName, "utility"); gin.specialGadget = SlotGadget(localName, "special");
        gin.utilHint = gin.ownCompass || gin.ownRange ? "Nothing on SHIFT - put a gadget on it in your inventory (I)." : "You need a COMPASS for that - buy one at the Rod Shop stall (ITEMS, " + gin.compassPrice + ").";
        gin.specialHint = gin.ownCompass || gin.ownRange ? "Nothing on R - put a gadget on it in your inventory (I)." : "You need a RANGE FINDER for that - buy one at the Rod Shop stall (ITEMS, " + gin.rangePrice + ").";
        gin.blocked = (gin.utilDown || gin.specialDown) && InputBlocked();   // only looked up while a key is held

        int st = AnglerState(localName);
        gin.lineOut = st == 1 || st == 2;
        if (gin.lineOut) { object r = RigOf(localName); if (r != null) gin.bobber = new Vector3(RF(r, "tx"), RF(r, "ty"), RF(r, "tz")); else gin.lineOut = false; }
        CastInfo ci;
        if (casts.TryGetValue(localName ?? "", out ci)) { gin.lastCast = ci.dist; gin.lastWater = ci.water; gin.lastCastAt = ci.at; } else gin.lastCast = -1f;

        gin.aim = false;
        if (gin.ownTracker && st == 0 && ib != null)
        {
            float mult = RangeMult(localName);
            Vector3 land; bool water;
            if (CL.Predict(body.footPosition, ib.aimOrigin, ib.aimDirection, gin.hasWater, gin.waterY, Sv(fCastRange, 200f) * mult, Sv(fLobMin, 15f) * mult,
                           Sv(fPMin, -10f), Sv(fPMax, 45f), gin.mask, out land, out water))
            { gin.aim = true; gin.aimLand = land; gin.aimWater = water; }
        }
    }

    // ---------------------------------------------------------------- cast tracking (everyone; broadcast for tracker owners)
    class CastInfo { public float dist, x, y, z, at; public bool water; }
    static readonly Dictionary<string, CastInfo> casts = new Dictionary<string, CastInfo>();
    static readonly Dictionary<string, float> seenCast = new Dictionary<string, float>();
    static readonly Dictionary<string, bool> wasOut = new Dictionary<string, bool>();

    static void TrackCasts()
    {
        IDictionary rg = fRigs == null ? null : fRigs.GetValue(null) as IDictionary;
        if (rg == null) return;
        foreach (DictionaryEntry e in rg)
        {
            string name = e.Key as string; object r = e.Value; if (name == null || r == null) continue;
            float ct = RF(r, "castTime"), prev;
            bool first = !seenCast.TryGetValue(name, out prev);
            seenCast[name] = ct;
            int st = AnglerState(name);
            bool isOut = st == 1 || st == 2, was;
            if (!first && ct != prev && ct > 0f)
            {
                var body = r.GetType().GetField("body", All).GetValue(r) as CharacterBody;
                var ci = new CastInfo { x = RF(r, "tx"), y = RF(r, "ty"), z = RF(r, "tz"), water = st != 3, at = Time.unscaledTime };
                ci.dist = body != null ? CL.Flat(body.footPosition, new Vector3(ci.x, 0, ci.z)) : 0f;
                casts[name] = ci;
                bool tr = Owns(name, TrackerItem);
                if (tr) Log(name + ": cast " + ci.dist.ToString("0.0") + " m (" + (ci.water ? "water" : "ground") + ")");
                if (tr && name != localName)
                    FChatRaw("[FT] CASTAT|" + name + "|" + ci.dist.ToString("0.0", Ci) + "|" + ci.x.ToString("0.00", Ci) + "|" + ci.y.ToString("0.00", Ci) + "|" + ci.z.ToString("0.00", Ci) + "|" + (ci.water ? 1 : 0));
            }
            if (wasOut.TryGetValue(name, out was) && was && !isOut && name != localName && Owns(name, TrackerItem)) FChatRaw("[FT] CASTEND|" + name);
            wasOut[name] = isOut;
        }
    }

    // ---------------------------------------------------------------- chat: aliases + help
    static string lastLine;
    static readonly List<string[]> pending = new List<string[]>();   // {name, kind, arg}

    static string Norm(string s) { var sb = new StringBuilder(); foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c); return sb.ToString(); }
    public static string Alias(string arg)
    {
        string n = Norm(arg);
        if (n == "tracker" || n == "casttracker" || n == "distancetracker" || n == "cdt" || n == "castdistance") return TrackerItem;
        if (n == "binoculars" || n == "binocular" || n == "rangefinders" || n == "laser") return RangeItem;
        return null;
    }

    static void OnChat()
    {
        try
        {
            if (!enabled) return;
            var log = Chat.readOnlyLog; if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            string plain = Regex.Replace(line, "<[^>]*>", "").Trim();
            int i = plain.IndexOf(": "); if (i <= 0) return;
            string name = plain.Substring(0, i).Trim(), text = plain.Substring(i + 2).Trim(), lo = text.ToLowerInvariant();
            if (!IsUser(name)) return;
            if (lo.StartsWith("!buy ") || lo.StartsWith("!equip "))
            {
                string verb = lo.StartsWith("!buy ") ? "!buy" : "!equip";
                string real = Alias(text.Substring(verb.Length + 1));
                if (real == null) return;
                Steal(line);
                pending.Add(new[] { name, "cmd", verb + " " + real });
            }
            else if (lo == "!compass" || lo == "!rangefinder" || lo == "!tracker" || lo == "!gadgets") { Steal(line); pending.Add(new[] { name, "help", lo }); }
        }
        catch (Exception e) { if (errors++ < 20) Log("chat hook error: " + e.Message); }
    }

    static bool IsUser(string name) { foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName == name) return true; return false; }

    static void Steal(string line)
    {
        try
        {
            var q = fQueue == null ? null : fQueue.GetValue(null) as List<string>;
            if (q == null) return;
            int n; lock (q) n = q.RemoveAll(s => ReferenceEquals(s, line) || s == line);
            Log("command taken from " + F.Name + ".chatQueue (" + n + "): " + line);
        }
        catch (Exception e) { Log("steal error: " + e.Message); }
    }

    static void Commands()
    {
        while (pending.Count > 0)
        {
            string[] p = pending[0]; pending.RemoveAt(0);
            try
            {
                if (p[1] == "cmd") { if (mRodCommand != null) mRodCommand.Invoke(null, new object[] { p[0], p[2] }); Log(p[0] + ": alias -> " + p[2]); continue; }
                string own = (Owns(p[0], CompassItem) ? "Compass, " : "") + (Owns(p[0], RangeItem) ? "Range Finder, " : "") + (Owns(p[0], TrackerItem) ? "Tracker, " : "");
                FChat("Hotspots are invisible - find them with gadgets from the Rod Shop stall (ITEMS tab): COMPASS $" + compassPrice.ToString("#,0", Ci) + " (hold SHIFT: needle points to the hotspot), RANGE FINDER $"
                      + rangePrice.ToString("#,0", Ci) + " (hold R: distance, says HOTSPOT on it), CAST DISTANCE TRACKER $" + trackerPrice.ToString("#,0", Ci) + " (cast + bobber distances while fishing). "
                      + p[0] + " owns: " + (own.Length == 0 ? "none yet" : own.TrimEnd(',', ' ')) + ".");
            }
            catch (Exception e) { Log("command error: " + (e.InnerException ?? e)); }
        }
    }

    // test helper: remove an ownership again (mirror + the fishing mod's table)
    public static void Revoke(string name, string item)
    {
        mirror.Remove(name + "|" + item); SaveMirror();
        IList l = OwnedList(name, false); if (l != null) l.Remove(item);
        Log("revoked " + item + " from " + name);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// HotBeam13 = HotBeam7 without the visuals: radius 10 m + the reachability relocation (open water within 150 m of a
// player standing on land). Driven by HotspotCompass6's frame (once a second). Disables older HotBeam<N>.
public static class HotBeam13
{
    public static bool enabled = true;
    public const float Radius = 10f, MaxFromLand = 150f;
    static readonly System.Random rng = new System.Random();
    static Vector3 lastPos = new Vector3(float.NaN, 0, 0);
    static string lastScene;
    static int errors;
    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_golden.log"), DateTime.Now.ToString("HH:mm:ss ") + "HotBeam13 " + s + Environment.NewLine); } catch { } }

    public static void DisableOlder()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in HotspotCompass6.SafeTypes(a))
                if (Regex.IsMatch(t.Name, "^HotBeam\\d+$") && t != typeof(HotBeam13))
                {
                    FieldInfo f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (f != null && (bool)f.GetValue(null)) { f.SetValue(null, false); Log("disabled " + t.Name); }
                }
    }

    static void Broadcast(string text)
    {
        try { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); } catch (Exception e) { Log("chat error: " + e.Message); }
    }

    static bool OpenWater(Vector3 p, float wy) { float gy; return CompassLook6.OpenWater(p.x, p.z, wy, LayerIndex.world.mask, out gy); }

    static List<Vector3> LandPlayers(float wy)
    {
        var l = new List<Vector3>();
        foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList)
        {
            CharacterBody b = nu == null ? null : nu.GetCurrentBody();
            if (b == null || b.healthComponent == null || !b.healthComponent.alive) continue;
            if (b.footPosition.y > wy + 0.5f) l.Add(b.footPosition);
        }
        return l;
    }

    static bool Reachable(Vector3 p, float wy)
    {
        if (!OpenWater(p, wy)) return false;
        var land = LandPlayers(wy);
        foreach (Vector3 f in land) if (CompassLook6.Flat(f, p) <= MaxFromLand) return true;
        return land.Count == 0;
    }

    static bool Relocate(Type F, Vector3 old, float wy, out Vector3 moved)
    {
        moved = old;
        var land = LandPlayers(wy);
        if (land.Count == 0) return false;
        for (int i = 0; i < 60; i++)
        {
            Vector3 f = land[rng.Next(land.Count)];
            double ang = rng.NextDouble() * Math.PI * 2, dist = 25 + rng.NextDouble() * (MaxFromLand - 40);
            var c = new Vector3(f.x + (float)(Math.Cos(ang) * dist), wy, f.z + (float)(Math.Sin(ang) * dist));
            if (!OpenWater(c, wy)) continue;
            bool ok = true;
            for (int k = 0; k < 4 && ok; k++) ok = OpenWater(c + new Vector3(Mathf.Cos(k * 1.5708f), 0, Mathf.Sin(k * 1.5708f)) * (Radius * 0.8f), wy);
            if (!ok) continue;
            F.GetField("hotspotPos", BindingFlags.Public | BindingFlags.Static).SetValue(null, c);
            MethodInfo bh = F.GetMethod("BroadcastHot", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (bh != null) bh.Invoke(null, null);
            Log("hotspot at " + old + " was unreachable -> moved to " + c + " (" + (int)dist + " m from a player)");
            moved = c;
            return true;
        }
        Log("couldn't find open water within " + MaxFromLand + " m of land - left the hotspot at " + old);
        return false;
    }

    public static void Tick()
    {
        if (!enabled) return;
        Type F = HotspotCompass6.F;
        if (F == null) return;
        try
        {
            F.GetField("hotspotRadius", BindingFlags.Public | BindingFlags.Static).SetValue(null, Radius);
            Vector3 pos; float r;
            if (!HotspotCompass6.HotHere(out pos, out r)) return;
            float wy = HotspotCompass6.WaterY();
            if (!Reachable(pos, wy)) Relocate(F, pos, wy, out pos);
            string sc = HotspotCompass6.Scene();
            if (float.IsNaN(lastPos.x) || sc != lastScene || CompassLook6.Flat(pos, lastPos) > 2f)
            {
                if (!float.IsNaN(lastPos.x))
                    Broadcast(sc != lastScene ? "<color=#c77dff>A hidden purple HOTSPOT is out on the lake - a Compass (hold SHIFT) points to it.</color>"
                                              : "<color=#c77dff>The hidden purple HOTSPOT moved - check your compass!</color>");
                Log("hotspot at " + pos + ", radius " + Radius + " (" + sc + ")");
                lastPos = pos; lastScene = sc;
            }
        }
        catch (Exception e) { if (errors++ < 10) Log("error: " + e); }
    }
}
