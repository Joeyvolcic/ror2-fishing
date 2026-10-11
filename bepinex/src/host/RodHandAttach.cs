// RodHandAttach: puts the fishing rod's handle in a character's RIGHT HAND (every frame) and optionally poses the right
// arm so the character looks like it is holding the rod. Self-contained, shared by the host add-on (WhipAnim9+) and the
// guest client (FishingClient.cs). Needs only UnityEngine.CoreModule; RoR2 types (CharacterBody, ModelLocator,
// ChildLocator, InputBankTest) are reached by reflection.
//
// Hand lookup per body (cached per body instance, logged once per body type to %TEMP%\ror2_rodhand.log):
//   1. the body's override table entry -> ChildLocator names (e.g. "HandR"), then bone names
//   2. generic ChildLocator names: HandR, Hand.R, HandRight, RightHand, Hand_R, R_Hand, MuzzleRight
//   3. hierarchy search: deepest transform whose name has "hand" + a right-side marker, no finger words
//   4. nothing found -> Attach() returns false and the rod stays where the fishing mod put it (shoulder placement)
//   A hand that turns out to sit on the body's LEFT side (mirrored rig) is swapped for its left-named counterpart.
//
// Timing: bones are written by the Animator AFTER Update, so the rod must be placed in LateUpdate. EnsureLateHook()
// (call it from the main thread, e.g. from RoR2Application.onUpdate) creates a hidden DontDestroyOnLoad object whose
// LateUpdate fires the static event Late; hosts put their rod code there. LateReady says whether that worked.
//
// Live tuning without re-injecting: %TEMP%\ror2_rodhand.cfg is re-read whenever it changes (checked every 2 s):
//   *            enabled=1 armPose=1 armWeight=0.7 butt=0.12 swingArm=1
//   CommandoBody grip=0,0.02,0.04 pitch=10 yaw=0 arm=1 child=HandR,GunR bone=hand.r hand=1
//   (grip = metres in body space: right, up, forward; pitch = extra degrees up; yaw = extra degrees to the right;
//    arm = pose the arm; hand = 0 to disable the hand attachment for that body)
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class RodHandAttach
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static bool enabled = true;
    public static bool armPose = true;            // optional second layer: pose the right arm
    public static bool armPoseDuringSwing = true; // keep posing during the whip (the forearm follows the swinging rod)
    public static float armWeight = 0.7f;         // 0 = animated pose, 1 = full holding pose
    public static float buttLength = 0.12f;       // how far the rod's butt sticks out behind the fist (m)
    public static float palmFrac = 0.6f;          // grip point: wrist bone -> towards its children (fingers)
    public static string LogPath = Path.Combine(Path.GetTempPath(), "ror2_rodhand.log");
    public static string CfgPath = Path.Combine(Path.GetTempPath(), "ror2_rodhand.cfg");

    public class Entry
    {
        public string[] childNames = new string[0];  // ChildLocator names to try, in order
        public string[] boneNames = new string[0];   // transform names (case-insensitive, exact first, then contains)
        public Vector3 gripOffset;                   // body space: x = right, y = up, z = forward (aim), metres
        public float rodPitch;                       // extra degrees the rod is raised above the fishing mod's direction
        public float rodYaw;                         // extra degrees turned to the right
        public bool armPose = true;                  // pose the right arm for this body
        public bool useHand = true;                  // false = keep the old shoulder placement
        public Entry(string children, string bones, float gx, float gy, float gz, float pitch, float yaw, bool arm)
        {
            childNames = Split(children); boneNames = Split(bones);
            gripOffset = new Vector3(gx, gy, gz); rodPitch = pitch; rodYaw = yaw; armPose = arm;
        }
    }

    // Defaults: ChildLocator names are the ones RoR2's item display rules use (HandR/LowerArmR/...). Bodies with no
    // real hand get a sensible stand-in (lower arm / weapon platform) and no arm pose. Tweak via the cfg file.
    public static readonly Dictionary<string, Entry> table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
    {
        { "CommandoBody",      new Entry("HandR,GunR,MuzzleRight",            "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "HuntressBody",      new Entry("HandR",                             "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },   // bow is in the LEFT hand
        { "MercBody",          new Entry("HandR",                             "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "Bandit2Body",       new Entry("HandR,MainWeapon",                  "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "ToolbotBody",       new Entry("HandR,LowerArmR",                   "",                0.1f, 0f, 0.4f, 0f, 0f, false) },  // MUL-T: no hand, use the right tool arm
        { "EngiBody",          new Entry("HandR,LowerArmR",                   "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "MageBody",          new Entry("HandR,LowerArmR",                   "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "LoaderBody",        new Entry("MechHandR,HandR,MechLowerArmR",     "mechhand.r",      0f, 0f, 0.05f,  0f, 0f, false) },  // big mech gauntlet
        { "CrocoBody",         new Entry("HandR,LowerArmR",                   "hand.r",          0f, 0.05f, 0.05f, 10f, 0f, false) }, // Acrid walks on its hands
        { "CaptainBody",       new Entry("HandR,LowerArmR",                   "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "TreebotBody",       new Entry("HandR,PlatformBase,WeaponPlatform", "",                0.3f, 0.2f, 0.2f, 0f, 0f, false) }, // REX: no hands, rod on the platform
        { "RailgunnerBody",    new Entry("HandR",                             "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "VoidSurvivorBody",  new Entry("HandR,ForeArmR,LowerArmR",          "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "SeekerBody",        new Entry("HandR",                             "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "ChefBody",          new Entry("HandR",                             "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "FalseSonBody",      new Entry("HandR",                             "hand.r",          0f, 0f, 0.03f,  0f, 0f, true) },
        { "Enforcer2Body",     new Entry("HandR,GunR",                        "hand.r",          0f, 0f, 0.02f,  0f, 0f, true) },
        { "HereticBody",       new Entry("HandR",                             "hand.r",          0f, 0f, 0.05f,  0f, 0f, false) },
    };
    static readonly string[] genericChildren = { "HandR", "Hand.R", "HandRight", "RightHand", "Hand_R", "R_Hand", "Hand R" };
    static readonly string[] fingerWords = { "thumb", "index", "finger", "middle", "ring", "pinky", "pinkie", "digit", "nail", "ik", "target", "pole", "end", "null", "_end", "twist", "fx", "vfx", "muzzle", "weapon", "gun" };

    class Info
    {
        public Component body; public Transform model, hand, lower, upper; public Entry e; public string name, how;
        public float w; public int poseFrame = -1; public int sideSamples; public float sideSum; public bool sideChecked;
    }
    static readonly Dictionary<int, Info> infos = new Dictionary<int, Info>();
    static readonly HashSet<string> logged = new HashSet<string>();
    static int errors;

    static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static string[] Split(string s) { return string.IsNullOrEmpty(s) ? new string[0] : s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); }

    // ------------------------------------------------------------------ late hook
    public static event Action Late;
    public static bool LateReady { get { return driver != null; } }
    static GameObject driverGo; static Component driver; static bool hookFailed;
    class Driver : MonoBehaviour { void LateUpdate() { RodHandAttach.FireLate(); } }
    static void FireLate()
    {
        CheckCfg();
        var l = Late; if (l == null) return;
        foreach (Action a in l.GetInvocationList())
            try { a(); } catch (Exception ex) { if (errors++ < 20) Log("late callback error: " + (ex.InnerException ?? ex)); }
    }
    // main thread only (e.g. from RoR2Application.onUpdate); cheap to call every frame
    public static bool EnsureLateHook()
    {
        if (driver != null) return true;
        if (hookFailed) return false;
        try
        {
            driverGo = new GameObject("RodHandAttachLate");
            UnityEngine.Object.DontDestroyOnLoad(driverGo);
            driverGo.hideFlags = HideFlags.HideInHierarchy;
            driver = driverGo.AddComponent<Driver>();
            Log("late hook installed (" + typeof(RodHandAttach).Assembly.GetName().Name + ")");
        }
        catch (Exception ex) { hookFailed = true; Log("late hook FAILED, callers fall back to Update timing: " + ex.Message); }
        return driver != null;
    }

    // ------------------------------------------------------------------ public API
    // Grip point in the right hand (palm) + the hand bone's rotation. False if the body has no usable hand.
    public static bool TryGetGrip(object body, out Vector3 gripPos, out Quaternion handRot)
    {
        gripPos = Vector3.zero; handRot = Quaternion.identity;
        Info i = Get(body as Component);
        if (i == null || i.hand == null) return false;
        Vector3 up, fwd, right; Frame(i, out up, out fwd, out right);
        gripPos = Grip(i, up, fwd, right); handRot = i.hand.rotation;
        return true;
    }

    // Re-anchors the rod cylinder (centre +/- pole.up * localScale.y) so its handle sits in the right hand.
    //   swinging : this player is whipping (arm pose may fade out, see armPoseDuringSwing)
    //   tweakDir : apply the body's rodPitch/rodYaw (do this once per frame, before the swing)
    //   pose     : pose the arm towards the rod's current direction (at most once per frame per body)
    // The rod direction is kept (only the per-body tweak is added), so cast/whip animations still play.
    public static bool Attach(object body, Transform pole, bool swinging, bool tweakDir, bool pose)
    {
        if (!enabled || pole == null) return false;
        try
        {
            Info i = Get(body as Component);
            if (i == null || i.hand == null || !i.e.useHand) return false;
            Vector3 up, fwd, right; Frame(i, out up, out fwd, out right);
            Vector3 dir = pole.up;
            if (tweakDir && (i.e.rodPitch != 0f || i.e.rodYaw != 0f))
            {
                Quaternion q = Quaternion.AngleAxis(i.e.rodYaw, up) * Quaternion.AngleAxis(-i.e.rodPitch, right);
                pole.rotation = q * pole.rotation; dir = pole.up;
            }
            if (pose && i.poseFrame != Time.frameCount)
            {
                i.poseFrame = Time.frameCount;
                float target = armPose && i.e.armPose && (!swinging || armPoseDuringSwing) ? armWeight : 0f;
                i.w = Mathf.MoveTowards(i.w, target, Mathf.Min(Time.deltaTime, 0.1f) * 4f);
                if (i.w > 0.001f && i.upper != null && i.lower != null) PoseArm(i, up, fwd, right, dir, i.w);
            }
            Vector3 grip = Grip(i, up, fwd, right);
            pole.position = grip + dir * (pole.localScale.y - buttLength);
            return true;
        }
        catch (Exception ex) { if (errors++ < 20) Log("attach error: " + ex); return false; }
    }

    // ------------------------------------------------------------------ internals
    static Info Get(Component body)
    {
        if (body == null) return null;
        int id = body.GetInstanceID();
        Info i;
        if (infos.TryGetValue(id, out i) && i.model != null && (i.hand != null || i.how != null)) { SideCheck(i); return i; }
        if (infos.Count > 64) infos.Clear();
        i = new Info { body = body, name = body.gameObject.name.Replace("(Clone)", "").Trim() };
        object ml = Prop(body, "modelLocator");
        i.model = Prop(ml, "modelTransform") as Transform;
        if (i.model == null) return null;   // model not spawned yet: try again next frame
        Entry e; if (!table.TryGetValue(i.name, out e)) e = new Entry("", "", 0f, 0f, 0.02f, 0f, 0f, true);
        i.e = e;
        Resolve(i);
        infos[id] = i;
        return i;
    }

    static void Resolve(Info i)
    {
        var loc = Locator(i.model);
        i.hand = null; i.how = "none";
        foreach (string n in i.e.childNames) if ((i.hand = loc.Find(n)) != null) { i.how = "ChildLocator '" + n + "' (table)"; break; }
        if (i.hand == null) foreach (string n in genericChildren) if ((i.hand = loc.Find(n)) != null) { i.how = "ChildLocator '" + n + "'"; break; }
        Transform[] all = i.model.GetComponentsInChildren<Transform>(true);
        if (i.hand == null) foreach (string n in i.e.boneNames) if ((i.hand = ByName(all, n)) != null) { i.how = "bone '" + i.hand.name + "' (table)"; break; }
        if (i.hand == null) { i.hand = SearchRightHand(all); if (i.hand != null) i.how = "bone search '" + i.hand.name + "'"; }
        if (i.hand == null && loc.Find("MuzzleRight") != null) { i.hand = loc.Find("MuzzleRight"); i.how = "ChildLocator 'MuzzleRight' (last resort)"; }
        FindArm(i, loc, all);
        if (!logged.Add(i.name)) return;
        Log(i.name + ": ChildLocator = [" + string.Join(", ", loc.names.ToArray()) + "]");
        var armish = new List<string>();
        foreach (Transform t in all)
        {
            string l = t.name.ToLowerInvariant();
            if ((l.Contains("hand") || l.Contains("arm") || l.Contains("shoulder") || l.Contains("clavicle") || l.Contains("wrist")) && armish.Count < 80) armish.Add(BonePath(t, i.model));
        }
        Log(i.name + ": arm/hand bones = [" + string.Join(", ", armish.ToArray()) + "]");
        Log(i.name + ": hand = " + (i.hand == null ? "NONE (rod stays at the shoulder)" : BonePath(i.hand, i.model)) + " via " + i.how
            + "; lower arm = " + (i.lower == null ? "-" : i.lower.name) + "; upper arm = " + (i.upper == null ? "-" : i.upper.name)
            + "; arm pose " + (i.e.armPose && i.upper != null && i.lower != null ? "ON" : "off"));
    }

    // After ~30 frames: a "right" hand that sits on the body's left (mirrored rig) is replaced by its left-named twin.
    static void SideCheck(Info i)
    {
        if (i.sideChecked || i.hand == null) return;
        Vector3 up, fwd, right; Frame(i, out up, out fwd, out right);
        Vector3 c = i.body.transform.position;
        object core = Prop(i.body, "corePosition"); if (core is Vector3) c = (Vector3)core;
        i.sideSum += Vector3.Dot(i.hand.position - c, right); i.sideSamples++;
        if (i.sideSamples < 30) return;
        i.sideChecked = true;
        float side = i.sideSum / i.sideSamples;
        Log(i.name + ": hand '" + i.hand.name + "' average side offset " + side.ToString("0.00") + " m (" + (side >= 0 ? "right, ok" : "LEFT") + ")");
        if (side > -0.05f) return;
        string twin = Mirror(i.hand.name);
        if (twin == null) return;
        Transform[] all = i.model.GetComponentsInChildren<Transform>(true);
        Transform t = ByName(all, twin);
        if (t == null) return;
        Log(i.name + ": mirrored rig, using '" + t.name + "' instead");
        i.hand = t; i.how += " -> mirrored to " + t.name;
        i.lower = i.upper = null; FindArm(i, Locator(i.model), all);
    }

    static string Mirror(string n)
    {
        string[,] pairs = { { ".r", ".l" }, { "_r", "_l" }, { "R", "L" }, { "Right", "Left" }, { "right", "left" } };
        for (int k = 0; k < pairs.GetLength(0); k++)
        {
            string a = pairs[k, 0], b = pairs[k, 1];
            if (n.EndsWith(a)) return n.Substring(0, n.Length - a.Length) + b;
            if (n.EndsWith(b)) return n.Substring(0, n.Length - b.Length) + a;
            if (a.Length > 1 && n.Contains(a)) return n.Replace(a, b);
        }
        return null;
    }

    static void FindArm(Info i, Loc loc, Transform[] all)
    {
        if (i.hand == null) return;
        bool flipped = i.how.Contains("mirrored");
        foreach (string n in flipped ? new[] { "LowerArmL", "ForeArmL", "LowerArm.L" } : new[] { "LowerArmR", "ForeArmR", "LowerArm.R", "ForearmR" })
            if (i.lower == null) i.lower = loc.Find(n);
        foreach (string n in flipped ? new[] { "UpperArmL", "UpperArm.L" } : new[] { "UpperArmR", "UpperArm.R" })
            if (i.upper == null) i.upper = loc.Find(n);
        // the hand's ancestors that look like arm bones (most rigs: upper_arm -> lower_arm -> hand)
        if (i.lower == null || !i.hand.IsChildOf(i.lower))
        {
            i.lower = null;
            Transform p = i.hand.parent;
            for (int d = 0; d < 3 && p != null && i.lower == null; d++, p = p.parent) if (Armish(p.name) && !p.name.ToLowerInvariant().Contains("upper")) i.lower = p;
        }
        if (i.lower != null && (i.upper == null || !i.lower.IsChildOf(i.upper)))
        {
            i.upper = null;
            Transform p = i.lower.parent;
            for (int d = 0; d < 3 && p != null && i.upper == null; d++, p = p.parent) if (Armish(p.name)) i.upper = p;
        }
        if (i.upper == i.lower) i.upper = null;
    }
    static bool Armish(string n) { n = n.ToLowerInvariant(); return n.Contains("arm") && !n.Contains("armor") && !n.Contains("armature"); }

    static bool IsRight(string l)   // l = lowercase name
    {
        if (l.Contains("right")) return true;
        if (l.EndsWith(".r") || l.EndsWith("_r") || l.EndsWith("-r") || l.EndsWith(" r")) return true;
        if (l.StartsWith("r_") || l.StartsWith("r.") || l.Contains(".r.") || l.Contains("_r_")) return true;
        return l.EndsWith("handr") || l.EndsWith("armr");
    }

    static Transform SearchRightHand(Transform[] all)
    {
        Transform best = null; int bestDepth = -1;
        foreach (Transform t in all)
        {
            string l = t.name.ToLowerInvariant();
            if (!l.Contains("hand") || !IsRight(l)) continue;
            bool finger = false; foreach (string f in fingerWords) if (l.Contains(f)) { finger = true; break; }
            if (finger) continue;
            int depth = 0; for (Transform p = t.parent; p != null; p = p.parent) depth++;
            // the hand proper is the shallowest "hand" in its own chain; prefer the deepest chain overall
            bool parentIsHand = t.parent != null && t.parent.name.ToLowerInvariant().Contains("hand");
            if (parentIsHand) continue;
            if (depth > bestDepth) { best = t; bestDepth = depth; }
        }
        return best;
    }

    static Transform ByName(Transform[] all, string n)
    {
        foreach (Transform t in all) if (string.Equals(t.name, n, StringComparison.OrdinalIgnoreCase)) return t;
        string l = n.ToLowerInvariant();
        foreach (Transform t in all) if (t.name.ToLowerInvariant().Contains(l)) return t;
        return null;
    }

    static string BonePath(Transform t, Transform root)
    {
        string s = t.name;
        for (Transform p = t.parent; p != null && p != root; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    class Loc
    {
        public readonly List<string> names = new List<string>(); readonly List<Transform> tfs = new List<Transform>();
        public void Add(string n, Transform t) { names.Add(n); tfs.Add(t); }
        public Transform Find(string n)
        {
            for (int k = 0; k < names.Count; k++) if (string.Equals(names[k], n, StringComparison.OrdinalIgnoreCase) && tfs[k] != null) return tfs[k];
            return null;
        }
    }
    static Loc Locator(Transform model)
    {
        var loc = new Loc();
        foreach (Component c in model.GetComponents<Component>())
        {
            if (c == null || c.GetType().Name != "ChildLocator") continue;
            FieldInfo f = c.GetType().GetField("transformPairs", All);
            var arr = f == null ? null : f.GetValue(c) as Array;
            if (arr == null) continue;
            foreach (object p in arr)
            {
                if (p == null) continue;
                FieldInfo fn = p.GetType().GetField("name", All), ft = p.GetType().GetField("transform", All);
                loc.Add(fn == null ? "?" : Convert.ToString(fn.GetValue(p)), ft == null ? null : ft.GetValue(p) as Transform);
            }
        }
        return loc;
    }

    static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();
    static object Prop(object o, string n)
    {
        if (o == null || (o is UnityEngine.Object && (UnityEngine.Object)o == null)) return null;
        Type t = o.GetType(); string key = t.FullName + "." + n;
        MemberInfo m;
        if (!members.TryGetValue(key, out m))
        {
            m = t.GetProperty(n, All);
            if (m == null || ((PropertyInfo)m).GetIndexParameters().Length != 0) m = t.GetField(n, All);
            members[key] = m;
        }
        if (m is PropertyInfo) return ((PropertyInfo)m).GetValue(o, null);
        if (m is FieldInfo) return ((FieldInfo)m).GetValue(o);
        return null;
    }

    static void Frame(Info i, out Vector3 up, out Vector3 fwd, out Vector3 right)
    {
        Transform bt = i.body.transform;
        up = bt.up;
        Vector3 aim = bt.forward;
        object d = Prop(Prop(i.body, "inputBank"), "aimDirection");
        if (d is Vector3) aim = (Vector3)d;
        fwd = Vector3.ProjectOnPlane(aim, up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = bt.forward;
        fwd.Normalize();
        right = Vector3.Cross(up, fwd);
    }

    static Vector3 Grip(Info i, Vector3 up, Vector3 fwd, Vector3 right)
    {
        Vector3 h = i.hand.position, palm = h;
        int n = i.hand.childCount;
        if (n > 0)
        {
            Vector3 avg = Vector3.zero;
            for (int k = 0; k < n; k++) avg += i.hand.GetChild(k).position;
            palm = h + Vector3.ClampMagnitude((avg / n - h) * palmFrac, 0.15f);
        }
        Vector3 o = i.e.gripOffset;
        return palm + right * o.x + up * o.y + fwd * o.z;
    }

    // Rotates upper arm then forearm (world-space deltas, so any bone axis convention works) towards a "holding a rod
    // in front" pose: upper arm down and slightly forward/out, forearm forward along the rod's heading, slightly up.
    static void PoseArm(Info i, Vector3 up, Vector3 fwd, Vector3 right, Vector3 rodDir, float w)
    {
        Vector3 rodH = Vector3.ProjectOnPlane(rodDir, up);
        rodH = rodH.sqrMagnitude < 1e-4f ? fwd : rodH.normalized;
        Vector3 heading = Vector3.Slerp(fwd, rodH, 0.6f);
        Vector3 upperT = (-up * 0.85f + heading * 0.35f + right * 0.15f).normalized;
        Vector3 lowerT = (heading * 0.9f + up * 0.25f - right * 0.1f).normalized;
        Aim(i.upper, i.lower.position - i.upper.position, upperT, w);
        Aim(i.lower, i.hand.position - i.lower.position, lowerT, w);
    }
    static void Aim(Transform bone, Vector3 cur, Vector3 target, float w)
    {
        if (cur.sqrMagnitude < 1e-6f) return;
        Quaternion q = Quaternion.FromToRotation(cur, target);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, q, w) * bone.rotation;
    }

    // ------------------------------------------------------------------ live config (%TEMP%\ror2_rodhand.cfg)
    static float nextCfg; static DateTime cfgTime;
    static void CheckCfg()
    {
        if (Time.realtimeSinceStartup < nextCfg) return;
        nextCfg = Time.realtimeSinceStartup + 2f;
        try
        {
            if (!File.Exists(CfgPath)) return;
            DateTime t = File.GetLastWriteTimeUtc(CfgPath);
            if (t == cfgTime) return;
            cfgTime = t;
            foreach (string raw in File.ReadAllLines(CfgPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Entry e = null;
                if (p[0] != "*" && !table.TryGetValue(p[0], out e)) table[p[0]] = e = new Entry("", "", 0f, 0f, 0.02f, 0f, 0f, true);
                for (int k = 1; k < p.Length; k++)
                {
                    int eq = p[k].IndexOf('='); if (eq < 0) continue;
                    string key = p[k].Substring(0, eq).ToLowerInvariant(), v = p[k].Substring(eq + 1);
                    if (e == null)
                    {
                        if (key == "enabled") enabled = v != "0";
                        else if (key == "armpose") armPose = v != "0";
                        else if (key == "swingarm") armPoseDuringSwing = v != "0";
                        else if (key == "armweight") armWeight = Num(v);
                        else if (key == "butt") buttLength = Num(v);
                        else if (key == "palm") palmFrac = Num(v);
                        continue;
                    }
                    if (key == "grip") { string[] g = v.Split(','); if (g.Length == 3) e.gripOffset = new Vector3(Num(g[0]), Num(g[1]), Num(g[2])); }
                    else if (key == "pitch") e.rodPitch = Num(v);
                    else if (key == "yaw") e.rodYaw = Num(v);
                    else if (key == "arm") e.armPose = v != "0";
                    else if (key == "hand") e.useHand = v != "0";
                    else if (key == "child") e.childNames = Split(v);
                    else if (key == "bone") e.boneNames = Split(v);
                }
            }
            infos.Clear(); logged.Clear();   // re-resolve hands with the new names (and log again)
            Log("config loaded from " + CfgPath);
        }
        catch (Exception ex) { Log("config error: " + ex.Message); }
    }
    static float Num(string s) { float f; float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f); return f; }
}
