#define HOST
// "No weapons" mode for Risk of Rain 2: replaces the LOCAL player's four skills with
//   M1 (primary) = "Fishing Rod" (does nothing by itself; left click fishes)
//   M2 (secondary) = "Rod Whip" (does nothing by itself; right click whips - see below)
//   Shift/R      = disabled
// Skills are controlled by each player's own game, so this runs on every PC.
//   HOST build (NoWeapons<N>): the host fishing mod already fishes on left click; this only swaps skills.
//   CLIENT build (NoWeaponsClient, inside FISHING.exe): on left click sends the chat line "[FT] FISH"
//   that the host's fishing mod treats like typing "fish" (cast, or reel when it bites); on right click sends
//   "[FT] WHIP" (host Fishing16+: 1 damage to the player in front + snaps their line back). The host build
//   doesn't send anything: the host fishing mod reads the host's right mouse button itself.
// All Unity objects are created on the game's main thread (RoR2Application.onUpdate), never in Run().
// Setting enabled = false removes the overrides again (weapons come back).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

#if HOST
public static class NoWeapons4
#else
public static class NoWeaponsClient
#endif
{
    public static bool enabled = true;
    public static int version = 3;   // 3 = mod 2.0; 2 = right click Rod Whip; a newer client copy replaces an older running one
#if HOST
    static readonly bool IsHostBuild = true;
    const string Me = "NoWeapons4";
#else
    static readonly bool IsHostBuild = false;
    const string Me = "NoWeaponsClient";
#endif
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_noweapons.log");
    static readonly object source = new object();   // override owner
    static bool installed;
    static Assembly ror2;
    static Type netServer, skillDefType, genericSkillType, prioType, runType, lumType, localUserType;
    static MethodInfo setOverride, unsetOverride, firstLocalUser;
    static object prioReplacement;
    static ScriptableObject rodDef, offDef, whipDef;
    static bool wasDown2;
    static float lastWhipSend = -10f;
    static int whipCount;
    static bool defsTried;
    static int errors;

    static object curBody;
    static readonly List<object> applied = new List<object>();   // GenericSkill instances we overrode (current body)
    static float nextCheck;
    static bool wasDown;
    static float lastSend = -10f;
    static int pressCount;
    static bool verifiedHud;
    static float verifyAt;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + Me + "] " + s + Environment.NewLine); } catch { } }

    static object Get(object o, string name)
    {
        if (o == null) return null;
        Type t = o.GetType();
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(o) : null;
    }
    static object GetStatic(Type t, string name)
    {
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null) return p.GetValue(null, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(null) : null;
    }
    static void SetF(object o, string name, object v)
    {
        FieldInfo f = o.GetType().GetField(name, All);
        if (f != null) f.SetValue(o, v); else Log("field missing: " + name);
    }
    static bool Alive(object o) { UnityEngine.Object u = o as UnityEngine.Object; return u != null; }

    public static void Run()
    {
        try
        {
            if (installed) { Log("already running"); return; }
            installed = true;
            foreach (Assembly a in FishingScope.Assemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer");
                if (t != null) netServer = t;
            }
            skillDefType = ror2.GetType("RoR2.Skills.SkillDef");
            genericSkillType = ror2.GetType("RoR2.GenericSkill");
            prioType = ror2.GetType("RoR2.GenericSkill+SkillOverridePriority");
            prioReplacement = Enum.Parse(prioType, "Replacement");
            setOverride = genericSkillType.GetMethod("SetSkillOverride", All);
            unsetOverride = genericSkillType.GetMethod("UnsetSkillOverride", All);
            runType = ror2.GetType("RoR2.Run");
            lumType = ror2.GetType("RoR2.LocalUserManager");
            localUserType = ror2.GetType("RoR2.LocalUser");
            firstLocalUser = lumType.GetMethod("GetFirstLocalUser", All);

            // turn off older host versions (NoWeapons<N>); they remove their own overrides on their next tick
            // (host build only: the client build must never switch off the host's NoWeapons<N>)
            if (IsHostBuild) foreach (Assembly a in FishingScope.Assemblies())
            {
                Type[] ts; try { ts = FishingScope.TypesOf(a); } catch { continue; }
                foreach (Type t in ts)
                {
                    if (t.Name == Me || !Regex.IsMatch(t.Name, "^NoWeapons\\d+$")) continue;
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en != null && en.FieldType == typeof(bool) && (bool)en.GetValue(null)) { en.SetValue(null, false); Log("disabled older " + t.Name); }
                }
            }

            // client build: a copy from an earlier FISHING.exe run in this game session is replaced if it is older
            // (it removes its own overrides on its next tick); the same/newer version keeps running and this one exits
            if (!IsHostBuild) foreach (Assembly a in FishingScope.Assemblies())
            {
                if (a == MethodBase.GetCurrentMethod().DeclaringType.Assembly) continue;
                Type t; try { t = a.GetType("NoWeaponsClient"); } catch { continue; }
                if (t == null) continue;
                FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                FieldInfo ins = t.GetField("installed", All);
                if (en == null || !(bool)en.GetValue(null) || (ins != null && !(bool)ins.GetValue(null))) continue;
                FieldInfo vf = t.GetField("version", BindingFlags.Public | BindingFlags.Static);
                int ov = vf != null && vf.FieldType == typeof(int) ? (int)vf.GetValue(null) : 1;
                if (ov >= version) { Log("already running (another copy, version " + ov + ")"); return; }
                en.SetValue(null, false);
                Log("replaced an older copy (version " + ov + " -> " + version + ")");
            }

            EventInfo ev = ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All);
            ev.AddEventHandler(null, (Action)Tick);
            Log("installed (" + (IsHostBuild ? "host" : "client") + " build): subscribed to RoR2Application.onUpdate");
        }
        catch (Exception e) { Log("Run failed: " + e); }
    }


    static bool ServerActive() { try { return netServer != null && (bool)netServer.GetProperty("active").GetValue(null, null); } catch { return false; } }

    // ---------- main thread only ----------
    static Sprite MakeRodSprite()
    {
        float[] px = NoWeaponsIconPixels.Draw();
        int n = NoWeaponsIconPixels.N;
        Color[] cols = new Color[n * n];
        for (int i = 0; i < cols.Length; i++) cols[i] = new Color(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]);
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.SetPixels(cols); tex.Apply();
        tex.name = "FishingRodIcon";
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        sp.name = "FishingRodIcon";
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    static Sprite MakeWhipSprite()
    {
        float[] px = NoWeaponsIconPixels.DrawWhip();
        int n = NoWeaponsIconPixels.N;
        Color[] cols = new Color[n * n];
        for (int i = 0; i < cols.Length; i++) cols[i] = new Color(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]);
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.SetPixels(cols); tex.Apply();
        tex.name = "RodWhipIcon";
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        sp.name = "RodWhipIcon";
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    static Sprite MakeOffSprite()   // grey tile with a dark X
    {
        int n = 64;
        Color[] cols = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool cross = Math.Abs(x - y) < 4 || Math.Abs(x - (n - 1 - y)) < 4;
                bool inner = x > 12 && x < n - 13 && y > 12 && y < n - 13;
                cols[y * n + x] = (cross && inner) ? new Color(0.12f, 0.12f, 0.12f, 1f) : new Color(0.32f, 0.32f, 0.34f, 1f);
            }
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.SetPixels(cols); tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        sp.name = "NoWeaponIcon";
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    static ScriptableObject NewDef(string name, string nameTok, string descTok, Sprite icon)
    {
        ScriptableObject d = ScriptableObject.CreateInstance(skillDefType);
        d.name = name;
        d.hideFlags = HideFlags.DontUnloadUnusedAsset;
        SetF(d, "skillName", name);
        SetF(d, "skillNameToken", nameTok);
        SetF(d, "skillDescriptionToken", descTok);
        SetF(d, "keywordTokens", new string[0]);
        SetF(d, "icon", icon);
        // state machine that no body has -> the skill can never execute (no state change, nothing networked)
        SetF(d, "activationStateMachineName", "NoWeaponsNone");
        Type idle = ror2.GetType("EntityStates.Idle");
        Type sest = ror2.GetType("EntityStates.SerializableEntityStateType");
        if (idle != null && sest != null) SetF(d, "activationState", Activator.CreateInstance(sest, new object[] { idle }));
        Type ip = ror2.GetType("EntityStates.InterruptPriority");
        if (ip != null) SetF(d, "interruptPriority", Enum.Parse(ip, "Any"));
        SetF(d, "baseRechargeInterval", 0f);
        SetF(d, "baseMaxStock", 1);
        SetF(d, "rechargeStock", 1);
        SetF(d, "requiredStock", 0);
        SetF(d, "stockToConsume", 0);
        SetF(d, "fullRestockOnAssign", true);
        SetF(d, "cancelSprintingOnActivation", false);
        SetF(d, "canceledFromSprinting", false);
        SetF(d, "isCombatSkill", false);
        SetF(d, "mustKeyPress", true);
        SetF(d, "hideStockCount", true);
        SetF(d, "hideCooldown", true);
        return d;
    }

    static void MakeDefs()
    {
        defsTried = true;
        rodDef = NewDef("FishingRod", "Fishing Rod", "Left click to cast. Left click again when something bites to reel it in.", MakeRodSprite());
        whipDef = NewDef("RodWhip", "Rod Whip", "Right click: whip the player in front of you (12 m) with your rod for 1 damage and snap their fishing line back - their fish gets away. 1.5 s cooldown.", MakeWhipSprite());
        object dis = null;
        try { Type ca = ror2.GetType("RoR2.CharacterBody+CommonAssets"); if (ca != null) dis = GetStatic(ca, "disabledSkill"); } catch { }
        if (Alive(dis)) { offDef = (ScriptableObject)dis; Log("using the game's own disabledSkill for M2/Shift/R (name=" + Get(dis, "skillName") + ", icon=" + IconName(Get(dis, "icon")) + ")"); }
        else { offDef = NewDef("Disabled", "Disabled", "Weapons are removed - it's a fishing trip.", MakeOffSprite()); Log("built own 'Disabled' skill for M2/Shift/R"); }
        Log("skill defs created on main thread (rod icon " + IconName(Get(rodDef, "icon")) + ")");
    }

    static string IconName(object sp) { UnityEngine.Object o = sp as UnityEngine.Object; return o != null ? o.name : "null"; }

    static void Unapply(string why)
    {
        int n = 0;
        foreach (object gs in applied)
        {
            if (!Alive(gs)) continue;
            try
            {
                object def = gs == null ? null : Get(gs, "skillDef");
                // remove whichever of our defs was put on this slot
                unsetOverride.Invoke(gs, new object[] { source, rodDef, prioReplacement });
                unsetOverride.Invoke(gs, new object[] { source, offDef, prioReplacement });
                if (whipDef != null) unsetOverride.Invoke(gs, new object[] { source, whipDef, prioReplacement });
                n++;
            }
            catch (Exception e) { Log("unset error: " + (e.InnerException ?? e).Message); }
        }
        applied.Clear();
        curBody = null;
        if (n > 0) Log("overrides removed from " + n + " slots (" + why + ") - weapons restored");
    }

    static readonly string[] Slots = { "primary", "secondary", "utility", "special" };

    static void Apply(object body)
    {
        applied.Clear();
        object loc = Get(body, "skillLocator");
        if (!Alive(loc)) { Log("body has no skillLocator"); return; }
        var sb = new System.Text.StringBuilder();
        foreach (string slot in Slots)
        {
            object gs = Get(loc, slot);
            if (!Alive(gs)) { sb.Append(slot + "=none "); continue; }
            object def = slot == "primary" ? (object)rodDef : slot == "secondary" && whipDef != null ? whipDef : offDef;
            setOverride.Invoke(gs, new object[] { source, def, prioReplacement });
            applied.Add(gs);
            sb.Append(slot + "->" + Get(Get(gs, "skillDef"), "skillName") + " ");
        }
        Log("applied on " + Get(body, "name") + ": " + sb.ToString().Trim() + " | primary icon=" + IconName(Get(Get(loc, "primary"), "icon")));
        verifiedHud = false; verifyAt = Time.unscaledTime + 2f;
    }

    static void CheckStillOurs(object body)
    {
        object loc = Get(body, "skillLocator");
        foreach (object gs in applied)
        {
            if (!Alive(gs)) continue;
            object def = Get(gs, "skillDef");
            if (!ReferenceEquals(def, rodDef) && !ReferenceEquals(def, offDef) && !ReferenceEquals(def, whipDef))
            {
                // something else (e.g. a lunar replacement item) took the slot - put ours back on top
                Log("slot taken by " + Get(def, "skillName") + ", re-applying");
                unsetOverride.Invoke(gs, new object[] { source, rodDef, prioReplacement });
                unsetOverride.Invoke(gs, new object[] { source, offDef, prioReplacement });
                if (whipDef != null) unsetOverride.Invoke(gs, new object[] { source, whipDef, prioReplacement });
                bool prim = ReferenceEquals(gs, Get(loc, "primary"));
                bool sec = ReferenceEquals(gs, Get(loc, "secondary"));
                setOverride.Invoke(gs, new object[] { source, prim ? (object)rodDef : sec && whipDef != null ? whipDef : offDef, prioReplacement });
            }
        }
    }

    static void VerifyHud()
    {
        verifiedHud = true;
        try
        {
            Type si = ror2.GetType("RoR2.UI.SkillIcon");
            UnityEngine.Object[] icons = UnityEngine.Object.FindObjectsOfType(si);
            var sb = new System.Text.StringBuilder();
            foreach (UnityEngine.Object ic in icons)
            {
                object img = Get(ic, "iconImage");
                object ts = Get(ic, "targetSkill");
                sb.Append(Get(ic, "targetSkillSlot") + ":" + (Alive(ts) ? Get(Get(ts, "skillDef"), "skillName") : "-") + "/sprite=" + (Alive(img) ? IconName(Get(img, "sprite")) : "?") + "  ");
            }
            Log("HUD skill icons (" + icons.Length + "): " + sb.ToString().Trim());
        }
        catch (Exception e) { Log("HUD verify error: " + (e.InnerException ?? e).Message); }
    }

    // =============== hide the weapon meshes in every player's hands (purely visual, this screen only) ===============
    // Renderer (object or parent) names containing one of these words are hidden.
    static readonly string[] WeaponWords = { "gun", "pistol", "rifle", "weapon", "blade", "sword", "bow", "arrow", "launcher",
        "cannon", "hammer", "staff", "nailgun", "knife", "dagger", "shotgun", "revolver", "scepter", "flamethrower", "katana",
        "crossbow", "grenade", "spear", "lance", "scythe", "glaive", "axe", "mace", "whip", "harpoon", "bazooka", "blaster" };
    // ...but never if the name contains one of these (bones / unrelated words that contain a weapon word)
    static readonly string[] NotWeaponWords = { "elbow", "rainbow", "taxe", "maxe", "relax", "balance", "staffbone" };
    // Per survivor tweaks (key = body prefab name without "(Clone)", value = renderer names, case-insensitive substring).
    static readonly Dictionary<string, string[]> AlwaysHide = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        { "HuntressBody", new[] { "BowMesh", "ArrowMesh" } },
        { "CommandoBody", new[] { "GunMeshL", "GunMeshR" } },
        { "MercBody", new[] { "MercSwordMesh" } },
    };
    static readonly Dictionary<string, string[]> NeverHide = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        // e.g. { "CaptainBody", new[] { "CaptainGunArm" } },   // keep Captain's arm
    };
    public static bool hideWeaponMeshes = true;

    class HiddenBody { public object body; public string prefab; public List<Renderer> hidden = new List<Renderer>(); public float checkAt; public bool checkedOnce; }
    static readonly Dictionary<int, HiddenBody> hiddenBodies = new Dictionary<int, HiddenBody>();
    static readonly HashSet<string> loggedPrefabs = new HashSet<string>();
    static float nextBodyScan;
    static Type cmType;

    static bool Listed(Dictionary<string, string[]> d, string prefab, string rname)
    {
        string[] l; if (!d.TryGetValue(prefab, out l)) return false;
        foreach (string s in l) if (rname.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static bool WeaponName(string n)
    {
        string l = n.ToLowerInvariant();
        foreach (string x in NotWeaponWords) if (l.Contains(x)) return false;
        foreach (string w in WeaponWords) if (l.Contains(w)) return true;
        return false;
    }

    static void HideWeapons(object body)
    {
        HiddenBody hb = new HiddenBody { body = body, checkAt = Time.unscaledTime + 10f };
        hb.prefab = ((string)Get(body, "name") ?? "?").Replace("(Clone)", "").Trim();
        Component ml = Get(body, "modelLocator") as Component;
        Transform model = ml != null ? Get(ml, "modelTransform") as Transform : null;
        hiddenBodies[((UnityEngine.Object)body).GetInstanceID()] = hb;
        if (model == null) { Log("no model for " + hb.prefab); return; }
        if (cmType == null) cmType = ror2.GetType("RoR2.CharacterModel");
        Component cm = model.GetComponent(cmType);
        Renderer main = null;
        try { if (cm != null) main = cmType.GetMethod("GetMainRenderer", All).Invoke(cm, null) as Renderer; } catch { }

        // candidate renderers: the model's own renderer list + mesh renderers under the model (not item displays / effects)
        var cands = new List<Renderer>();
        Array infos = cm != null ? Get(cm, "baseRendererInfos") as Array : null;
        if (infos != null) foreach (object ri in infos) { Renderer r = Get(ri, "renderer") as Renderer; if (r != null && !cands.Contains(r)) cands.Add(r); }
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
        {
            if (cands.Contains(r)) continue;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            bool display = false;
            for (Transform t = r.transform; t != null && t != model; t = t.parent) if (t.name.StartsWith("Display", StringComparison.OrdinalIgnoreCase) || t.name.Contains("Effect")) { display = true; break; }
            if (!display) cands.Add(r);
        }
        var kept = new List<string>();
        foreach (Renderer r in cands)
        {
            string rn = r.gameObject.name, pn = r.transform.parent != null ? r.transform.parent.name : "";
            bool hide = Listed(AlwaysHide, hb.prefab, rn) ||
                        (r != main && !Listed(NeverHide, hb.prefab, rn) && (WeaponName(rn) || (WeaponName(pn) && pn != model.name)));
            if (hide) { r.forceRenderingOff = true; r.enabled = false; hb.hidden.Add(r); }
            else kept.Add(rn);
        }
        if (loggedPrefabs.Add(hb.prefab))
        {
            if (hb.hidden.Count == 0) Log("no separate weapon mesh for " + hb.prefab + " (renderers: " + string.Join(", ", kept.ToArray()) + ")");
            else
            {
                var hn = new List<string>(); foreach (Renderer r in hb.hidden) hn.Add(r.gameObject.name);
                Log("weapon meshes hidden on " + hb.prefab + ": " + string.Join(", ", hn.ToArray()) + "  | kept: " + string.Join(", ", kept.ToArray()));
            }
        }
    }

    static void WeaponMeshTick()
    {
        float t = Time.unscaledTime;
        // keep the hidden renderers off (CharacterModel toggles .enabled; forceRenderingOff is not touched by it)
        List<int> gone = null;
        foreach (var kv in hiddenBodies)
        {
            HiddenBody hb = kv.Value;
            if (!Alive(hb.body)) { (gone = gone ?? new List<int>()).Add(kv.Key); continue; }
            foreach (Renderer r in hb.hidden) if (r != null && !r.forceRenderingOff) r.forceRenderingOff = true;
            if (!hb.checkedOnce && t >= hb.checkAt && hb.hidden.Count > 0)
            {
                hb.checkedOnce = true;
                int off = 0, vis = 0;
                foreach (Renderer r in hb.hidden) if (r != null) { if (r.forceRenderingOff) off++; if (r.isVisible) vis++; }
                Log("10 s check " + hb.prefab + ": " + off + "/" + hb.hidden.Count + " weapon renderers still forced off, " + vis + " visible to a camera");
            }
        }
        if (gone != null) foreach (int k in gone) hiddenBodies.Remove(k);
        if (t < nextBodyScan) return;
        nextBodyScan = t + 0.5f;
        IEnumerable users = GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyInstancesList") as IEnumerable;
        if (users == null) return;
        foreach (object nu in users)
        {
            object master = Get(nu, "master");
            if (!Alive(master)) continue;
            object body = master.GetType().GetMethod("GetBody", Type.EmptyTypes).Invoke(master, null);
            if (!Alive(body) || hiddenBodies.ContainsKey(((UnityEngine.Object)body).GetInstanceID())) continue;
            try { HideWeapons(body); } catch (Exception e) { Log("hide error: " + (e.InnerException ?? e).Message); }
        }
    }

    static void ShowWeapons()
    {
        int n = 0;
        foreach (HiddenBody hb in hiddenBodies.Values) foreach (Renderer r in hb.hidden) if (r != null) { r.forceRenderingOff = false; r.enabled = true; n++; }
        hiddenBodies.Clear(); loggedPrefabs.Clear();
        if (n > 0) Log("weapon meshes shown again (" + n + " renderers)");
    }

    static void OnM1()
    {
        pressCount++;
        float t = Time.unscaledTime;
        if (IsHostBuild)
        {
            if (pressCount <= 20) Log("M1 pressed by local player (host fishing mod handles left click: " + HostLeftClick() + ")");
            return;
        }
        if (t - lastSend < 0.3f) return;
        lastSend = t;
        try
        {
            object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
            IList locals = GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyLocalPlayersList") as IList;
            if (console == null || locals == null || locals.Count == 0) { Log("M1: no console/local network user, nothing sent"); return; }
            MethodInfo submit = null;
            foreach (MethodInfo m in console.GetType().GetMethods(All))
                if (m.Name == "SubmitCmd" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "NetworkUser") submit = m;
            submit.Invoke(console, new object[] { locals[0], "say \"[FT] FISH\"", false });
            if (pressCount <= 30) Log("M1 pressed -> sent [FT] FISH");
        }
        catch (Exception e) { Log("send error: " + (e.InnerException ?? e).Message); }
    }

    static void OnM2()   // right click = Rod Whip
    {
        whipCount++;
        float t = Time.unscaledTime;
        if (IsHostBuild)
        {
            if (whipCount <= 20) Log("M2 pressed by local player (host Fishing16+ reads the right mouse button itself)");
            return;
        }
        if (t - lastWhipSend < 0.5f) return;   // the host enforces the real 1.5 s cooldown
        lastWhipSend = t;
        try
        {
            object console = GetStatic(ror2.GetType("RoR2.Console"), "instance");
            IList locals = GetStatic(ror2.GetType("RoR2.NetworkUser"), "readOnlyLocalPlayersList") as IList;
            if (console == null || locals == null || locals.Count == 0) { Log("M2: no console/local network user, nothing sent"); return; }
            MethodInfo submit = null;
            foreach (MethodInfo m in console.GetType().GetMethods(All))
                if (m.Name == "SubmitCmd" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "NetworkUser") submit = m;
            submit.Invoke(console, new object[] { locals[0], "say \"[FT] WHIP\"", false });
            if (whipCount <= 30) Log("M2 pressed -> sent [FT] WHIP");
        }
        catch (Exception e) { Log("whip send error: " + (e.InnerException ?? e).Message); }
    }

    static string HostLeftClick()
    {
        try
        {
            int best = -1; FieldInfo f = null;
            foreach (Assembly a in FishingScope.Assemblies())
            {
                Type[] ts; try { ts = FishingScope.TypesOf(a); } catch { continue; }
                foreach (Type t in ts)
                {
                    Match m = Regex.Match(t.Name, "^Fishing(\\d+)$");
                    if (!m.Success) continue;
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en == null || !(bool)en.GetValue(null)) continue;
                    int n = int.Parse(m.Groups[1].Value);
                    if (n > best) { best = n; f = t.GetField("hostLeftClickFishing", BindingFlags.Public | BindingFlags.Static); }
                }
            }
            if (best < 0) return "no active Fishing mod";
            return "Fishing" + best + ".hostLeftClickFishing=" + (f == null ? "n/a" : f.GetValue(null).ToString());
        }
        catch { return "?"; }
    }

    static void Tick()
    {
        try
        {
            if (!enabled) { if (applied.Count > 0 || curBody != null) Unapply("disabled"); if (hiddenBodies.Count > 0) ShowWeapons(); return; }
            if (!IsHostBuild && ServerActive()) { if (applied.Count > 0) Unapply("this game is the host"); if (hiddenBodies.Count > 0) ShowWeapons(); return; }   // client build idles on the host
            object run = GetStatic(runType, "instance");
            if (!Alive(run)) { if (curBody != null) { applied.Clear(); curBody = null; } hiddenBodies.Clear(); return; }
            if (hideWeaponMeshes) { try { WeaponMeshTick(); } catch (Exception e) { if (errors++ < 10) Log("mesh tick error: " + (e.InnerException ?? e)); } }
            else if (hiddenBodies.Count > 0) ShowWeapons();
            object user = firstLocalUser.Invoke(null, null);
            object body = user != null ? Get(user, "cachedBody") : null;
            if (!Alive(body)) { wasDown = false; wasDown2 = false; return; }
            if (!defsTried) MakeDefs();
            if (!ReferenceEquals(body, curBody)) { curBody = body; Apply(body); }
            float t = Time.unscaledTime;
            if (t >= nextCheck) { nextCheck = t + 1f; CheckStillOurs(body); }
            if (!verifiedHud && t >= verifyAt) VerifyHud();

            // left click = rising edge of the primary-skill button
            object bank = Get(body, "inputBank");
            if (Alive(bank))
            {
                object st = Get(bank, "skill1");
                bool down = st != null && (bool)Get(st, "down");
                if (down && !wasDown) OnM1();
                wasDown = down;
                object st2 = Get(bank, "skill2");
                bool down2 = st2 != null && (bool)Get(st2, "down");
                if (down2 && !wasDown2) OnM2();
                wasDown2 = down2;
            }
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + (e.InnerException ?? e)); }
    }
}
