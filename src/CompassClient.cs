// CompassClient - guest payload for FISHING.exe (does nothing on the host, which runs HotspotCompass<N>).
// Build with CompassLook.cs + GadgetLook.cs (WITHOUT -define:HOST). UnityEngine Core/IMGUI/TextRendering/Physics/UI only;
// everything from RoR2 is reflection, like the other guest payloads.
//  * hides the hotspot looks this PC draws ("FishingHotspotPurple" FishingClient, "FishingHotspotGold" gameplay client)
//  * the gadgets the host says you own ([FT] GADGETS|name|compass,rangefinder,tracker|rangeMult|castRange|lobMin|pitchMin|pitchMax):
//      Compass: hold SHIFT (Utility) - pocket compass, needle -> hotspot ([FT] HOT), gold needle -> golden hotspot ([FT] HOT2)
//      Range Finder: hold R (Special) - zoomed view + distance, "- HOTSPOT" when the aimed water point is inside it
//      Cast Distance Tracker: strip while fishing ([FT] CASTAT|name|dist|x|y|z|water, [FT] CASTEND|name) + landing ring
//    Not owned: Shift / R shows "buy one at the Rod Shop stall". Buying = the stall (SHOPBUY) or chat !buy, on the host.
// Entry: CompassClient.Run()   Log: %TEMP%\ror2_compass_client.log
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public class CompassClientHud : MonoBehaviour
{
    void OnGUI() { CompassClient.OnGui(); }
}

public static class CompassClient
{
    public static bool enabled = true, installed;
    public static int version = 2;   // 2 = physical gadgets (1 = HUD compass, never shipped)
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_compass_client.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

    static Assembly ror2; static Type netServer, chatType, nuType, runType, camRigType, chatBoxType;
    static object Get(object o, string n)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(n, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(n, All); return f != null ? f.GetValue(o) : null;
    }
    static object GetStatic(Type t, string n)
    {
        PropertyInfo p = t.GetProperty(n, All); if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(n, All); return f != null ? f.GetValue(null) : null;
    }
    static bool IsHost() { return (bool)netServer.GetProperty("active").GetValue(null, null); }
    static bool Alive(object o) { return o != null && !(o is UnityEngine.Object && (UnityEngine.Object)o == null); }

    class Self { }

    public static void Run()
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer"); if (t != null) netServer = t;
            }
            if (ror2 == null || netServer == null) { Log("game assemblies not found"); return; }
            if (IsHost()) { Log("host detected - the host runs HotspotCompass<N>, nothing to do"); return; }
            if (installed) { enabled = true; Log("already running - nothing to do"); return; }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(Self).Assembly) continue;
                Type other = null; try { other = asm.GetType("CompassClient"); } catch { }
                if (other == null) continue;
                FieldInfo inst = other.GetField("installed"), en = other.GetField("enabled"), vf = other.GetField("version");
                if (inst == null || !(bool)inst.GetValue(null) || en == null || !(bool)en.GetValue(null)) continue;
                int ov = vf != null ? (int)vf.GetValue(null) : 0;
                if (ov >= version) { Log("another copy (version " + ov + ") is already running - nothing to do"); return; }
                en.SetValue(null, false); Log("replaced an older copy (version " + ov + ")");
            }
            chatType = ror2.GetType("RoR2.Chat"); nuType = ror2.GetType("RoR2.NetworkUser"); runType = ror2.GetType("RoR2.Run");
            camRigType = ror2.GetType("RoR2.CameraRigController"); chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action)) { Log("chat hook not available"); return; }
            chatHook = OnChat; cc.AddEventHandler(null, chatHook);
            EventInfo upd = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            frameHook = Frame; upd.AddEventHandler(null, frameHook);
            GadgetLook.Log = s => Log("gadget: " + s);
            installed = true;
            Log("compass client installed (version " + version + ")");
        }
        catch (Exception e) { Log("Run error: " + (e.InnerException ?? e)); throw; }
    }

    static Action chatHook, frameHook;

    // ---------------------------------------------------------------- state from the host's chat lines
    static bool hot, gold, own1, own2, own3; static string slotU, slotR; static Vector3 hotPos, goldPos; static float hotR = 10f, goldR = 8f;
    static string hotScene, goldScene;
    static float mult = 1f, castRange = 200f, lobMin = 15f, pMin = -10f, pMax = 45f;
    static float lastCast = -1f, lastAt = -100f; static bool lastWater = true, lineOut; static Vector3 bobber;
    static string lastLine;

    static string Scene() { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
    static float P(string s) { return float.Parse(s, Ci); }

    static string LocalName()
    {
        foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
        { object l = Get(nu, "isLocalPlayer"); if (l is bool && (bool)l) return Convert.ToString(Get(nu, "userName")); }
        return null;
    }

    static void OnChat()
    {
        try
        {
            if (!enabled) return;
            IList log = GetStatic(chatType, "readOnlyLog") as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            string plain = Regex.Replace(line, "<[^>]*>", "");
            int i = plain.IndexOf("[FT] "); if (i < 0) return;
            string[] p = plain.Substring(i + 5).Trim().Split('|');
            string me = LocalName();
            switch (p[0])
            {
                case "HOT": if (p.Length >= 5) { hotPos = new Vector3(P(p[1]), P(p[2]), P(p[3])); hotR = P(p[4]); hot = true; hotScene = Scene(); } break;
                case "HOT2": if (p.Length >= 5) { goldPos = new Vector3(P(p[1]), P(p[2]), P(p[3])); goldR = P(p[4]); gold = true; goldScene = Scene(); } break;
                case "GADGETS":
                    if (p.Length >= 8 && p[1] == me)
                    {
                        string g = "," + p[2] + ",";
                        bool a = g.Contains(",compass,"), b = g.Contains(",rangefinder,"), c = g.Contains(",tracker,");
                        if (a != own1 || b != own2 || c != own3) Log("gadgets: " + p[2]);
                        own1 = a; own2 = b; own3 = c;
                        mult = P(p[3]); castRange = P(p[4]); lobMin = P(p[5]); pMin = P(p[6]); pMax = P(p[7]);
                        slotU = p.Length > 8 && p[8] != "-" ? p[8] : (p.Length > 8 ? null : (own1 ? "compass" : null));
                        slotR = p.Length > 9 && p[9] != "-" ? p[9] : (p.Length > 9 ? null : (own2 ? "rangefinder" : null));
                    }
                    break;
                case "CASTAT":
                    if (p.Length >= 7 && p[1] == me)
                    { lastCast = P(p[2]); bobber = new Vector3(P(p[3]), P(p[4]), P(p[5])); lastWater = p[6] == "1"; lineOut = lastWater; lastAt = Time.unscaledTime; }
                    break;
                case "CASTEND": if (p.Length >= 2 && p[1] == me) lineOut = false; break;
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("chat error: " + e.Message); }
    }

    // ---------------------------------------------------------------- frame
    static GameObject hud;
    static float nextHide; static int errors, worldMask = -1;
    static readonly GadgetLook.In gin = new GadgetLook.In();
    static bool inRun;
    static readonly string[] Looks = { "FishingHotspotPurple", "FishingHotspotGold", "FishingHotspot" };

    static int WorldMask()
    {
        if (worldMask != -1) return worldMask;
        try { object w = ror2.GetType("RoR2.LayerIndex").GetField("world", All).GetValue(null); worldMask = (int)(LayerMask)Get(w, "mask"); }
        catch { worldMask = 1 << 11; }
        return worldMask;
    }

    static bool Down(object ib, string slot) { object bs = Get(ib, slot); object d = Get(bs, "down"); return d is bool && (bool)d; }

    static bool ChatOpen()
    {
        try
        {
            if (chatBoxType == null) return false;
            foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(chatBoxType)) { object o = Get(b, "showInput"); if (o is bool && (bool)o) return true; }
        }
        catch { }
        return false;
    }

    static void Frame()
    {
        if (!enabled) { Cleanup(); return; }
        try
        {
            inRun = !IsHost() && Alive(GetStatic(runType, "instance"));
            if (hud == null) { hud = new GameObject("CompassClientHud"); UnityEngine.Object.DontDestroyOnLoad(hud); hud.AddComponent<CompassClientHud>(); }
            if (!inRun) { gin.hasMe = false; GadgetLook.Frame(gin); return; }
            float t = Time.unscaledTime;
            if (t >= nextHide) { nextHide = t + 0.2f; foreach (string n in Looks) CompassLook.Hide(GameObject.Find(n), null); }
            string sc = Scene();
            gin.hot = hot && hotScene == sc; gin.hotPos = hotPos; gin.hotR = hotR;
            gin.gold = gold && goldScene == sc; gin.goldPos = goldPos; gin.goldR = goldR;
            gin.hasWater = gin.hot; gin.waterY = hotPos.y;   // the host's [FT] HOT carries the lake's water level
            gin.mask = WorldMask();
            gin.ownCompass = own1; gin.ownRange = own2; gin.ownTracker = own3;
            if (lineOut && t - lastAt > 90f) lineOut = false;   // missed CASTEND
            gin.lineOut = lineOut; gin.bobber = bobber; gin.lastCast = lastCast; gin.lastWater = lastWater; gin.lastCastAt = lastAt;

            object body = null; string me = LocalName();
            foreach (object nu in (IEnumerable)GetStatic(nuType, "readOnlyInstancesList"))
                if (Convert.ToString(Get(nu, "userName")) == me) { object m = Get(nu, "master"); if (Alive(m)) body = m.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(m, null); }
            object hc = Alive(body) ? Get(body, "healthComponent") : null;
            gin.hasMe = Alive(body) && Alive(hc) && Get(hc, "alive") is bool && (bool)Get(hc, "alive");
            Camera cam = null;
            foreach (object c in (IEnumerable)GetStatic(camRigType, "readOnlyInstancesList")) { cam = Get(c, "sceneCam") as Camera; if (cam != null) break; }
            gin.cam = cam != null ? cam : Camera.main;
            if (!gin.hasMe) { gin.utilDown = gin.specialDown = false; GadgetLook.Frame(gin); return; }
            Vector3 foot = (Vector3)Get(body, "footPosition"); gin.me = foot;
            object ib = Get(body, "inputBank");
            gin.utilDown = Alive(ib) && Down(ib, "skill3"); gin.specialDown = Alive(ib) && Down(ib, "skill4");
            gin.blocked = (gin.utilDown || gin.specialDown) && ChatOpen();
            gin.utilGadget = slotU; gin.specialGadget = slotR;
            gin.utilHint = own1 || own2 ? "Nothing on SHIFT - put a gadget on it in your inventory (I)." : "You need a COMPASS for that - buy one at the Rod Shop stall (ITEMS, " + gin.compassPrice + ").";
            gin.specialHint = own1 || own2 ? "Nothing on R - put a gadget on it in your inventory (I)." : "You need a RANGE FINDER for that - buy one at the Rod Shop stall (ITEMS, " + gin.rangePrice + ").";

            gin.aim = false;
            if (own3 && !lineOut && Alive(ib))
            {
                Vector3 land; bool water;
                if (CompassLook.Predict(foot, (Vector3)Get(ib, "aimOrigin"), (Vector3)Get(ib, "aimDirection"), gin.hasWater, gin.waterY, castRange * mult, lobMin * mult, pMin, pMax, gin.mask, out land, out water))
                { gin.aim = true; gin.aimLand = land; gin.aimWater = water; }
            }
            GadgetLook.Frame(gin);
        }
        catch (Exception e) { if (errors++ < 20) Log("frame error: " + e); }
    }

    static void Cleanup()
    {
        try { chatType.GetEvent("onChatChanged", All).RemoveEventHandler(null, chatHook); } catch { }
        try { ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All).RemoveEventHandler(null, frameHook); } catch { }
        if (hud != null) UnityEngine.Object.Destroy(hud); hud = null;
        try { GadgetLook.Clear(); } catch { }
        Log("disabled");
    }

    public static void OnGui()
    {
        if (!enabled || !inRun) return;
        try { GadgetLook.OnGui(gin); } catch (Exception e) { if (errors++ < 20) Log("gui error: " + e); }
    }
}
