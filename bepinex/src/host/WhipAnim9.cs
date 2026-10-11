// Injected into JV94's (host) game. Mercenary-style Rod Whip for the fishing rods drawn by the newest Fishing<N>:
// motion + Merc's own MercSwordSlash swing VFX / OmniImpactVFXSlashMerc hit spark / Merc sword sounds, all in MercSwingLook.cs
// (compile with MercSwingLook.cs + RodHandAttach.cs). Triggers: JV94's right click, guests'
// "[FT] WHIP" chat signal, "[FT] WHIP|attacker|target" hit line. Test hooks: static ints testSwings / testHits.
// WhipAnim9 = WhipAnim8 + rod in the RIGHT HAND (RodHandAttach.cs): the rods are re-placed in LateUpdate (after the
// Animator wrote this frame's bones and after the fishing mod's onUpdate rig update, so injection order no longer
// matters): enlarge -> handle into the hand (+ per-body pitch/yaw) -> Merc swing about the hand -> arm pose towards the
// rod -> handle back into the (posed) hand -> bobber/line follow the tip. Input/chat parsing stays on onUpdate.
// Falls back to WhipAnim8 behaviour (onUpdate, shoulder) if the late hook can't be created or a body has no hand.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

public static class WhipAnim9
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const float RodLength = 1.4f, RodThickness = 0.06f;        // pole scale (fishing mod draws 1.0 long / 0.04 thick)
    public static bool enabled = true;
    public static int testSwings, testHits;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;

    static Type fishing, chatType;
    static readonly List<string> swinging = new List<string>();
    static readonly List<KeyValuePair<string, float>> pendingHits = new List<KeyValuePair<string, float>>();  // "attacker|target", due time
    static bool rightWasDown, statusLogged;
    static int seenLog, frames;

    static void Log(string s) { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_mercswing.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    static Type Newest(string prefix)
    {
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (t.Name.StartsWith(prefix) && int.TryParse(t.Name.Substring(prefix.Length), out n) && n > bestN) { best = t; bestN = n; }
            }
        return best;
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetType("RoR2.Chat") != null) chatType = a.GetType("RoR2.Chat");
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("WhipAnim") && t.Name != typeof(WhipAnim9).Name)
                {
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null) f.SetValue(null, false);
                }
        }
        fishing = Newest("Fishing");
        seenLog = ((IList)chatType.GetProperty("readOnlyLog", All).GetValue(null, null)).Count;
        Action h = Frame;
        chatType.Assembly.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All).AddEventHandler(null, h);
        RodHandAttach.Late += LateFrame;   // the hook object itself is created from Frame (main thread)
        Log(typeof(WhipAnim9).Name + " installed (rods from " + fishing.Name + ")");
    }

    static void Swing(string name)
    {
        if (MercSwingLook.Begin(name) && !swinging.Contains(name)) swinging.Add(name);
    }

    static void Frame()
    {
        if (!enabled) return;
        try
        {
            if (++frames % 300 == 0) fishing = Newest("Fishing");

            uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg);
            bool right = fg == myPid && (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (right && !rightWasDown && FishingScope.LocalName() != "") Swing(FishingScope.LocalName());
            rightWasDown = right;
            if (testSwings > 0) { testSwings--; Swing(FishingScope.LocalName()); }
            if (testHits > 0) { testHits--; Swing(FishingScope.LocalName()); pendingHits.Add(new KeyValuePair<string, float>(FishingScope.LocalName() + "|" + FishingScope.LocalName(), Time.time + MercSwingLook.WindTime + 0.04f)); }

            var log = (IList)chatType.GetProperty("readOnlyLog", All).GetValue(null, null);
            if (log.Count < seenLog) seenLog = 0;
            for (int i = seenLog; i < log.Count; i++)
            {
                string line = Convert.ToString(log[i]);
                int k = line.IndexOf(": [FT] WHIP");
                if (k > 0) Swing(StripTags(line.Substring(0, k)));
                int h = line.IndexOf("[FT] WHIP|");
                if (h >= 0)
                {
                    string[] p = StripTags(line.Substring(h + 10)).Split('|');
                    if (p.Length > 0) Swing(p[0]);
                    if (p.Length > 1) pendingHits.Add(new KeyValuePair<string, float>(p[0] + "|" + p[1], Time.time + MercSwingLook.WindTime + 0.04f));
                }
            }
            seenLog = log.Count;

            if (!RodHandAttach.EnsureLateHook()) AnimateRods();   // no LateUpdate: old timing (rod drawn at the shoulder)
            MercSwingLook.TickFallback();
            if (MercSwingLook.LastSlash != null)
            {
                var go = MercSwingLook.LastSlash; MercSwingLook.LastSlash = null;
                var r = go.GetComponentInChildren<Renderer>();
                Log("slash at " + go.transform.position + " rot " + go.transform.eulerAngles + " child " + (r == null ? "-" : r.transform.localPosition + "/" + r.transform.localEulerAngles + "/" + r.transform.localScale));
            }
            if (!statusLogged && MercSwingLook.Status != "") { statusLogged = true; Log("MercSwingLook: " + MercSwingLook.Status); }
        }
        catch (Exception e) { if (frames % 600 == 1) Log("whip animation error: " + e); }
    }

    static int lateErrors;
    static void LateFrame()
    {
        if (!enabled) return;
        try { AnimateRods(); }
        catch (Exception e) { if (lateErrors++ % 600 == 0) Log("whip animation (late) error: " + e); }
    }

    static string StripTags(string s)
    {
        while (true)
        {
            int a = s.IndexOf('<'), b = s.IndexOf('>');
            if (a < 0 || b < a) return s.Trim();
            s = s.Remove(a, b - a + 1);
        }
    }

    static T Field<T>(object rig, string n) where T : class
    {
        var f = rig.GetType().GetField(n); if (f == null) return null;
        T v = f.GetValue(rig) as T;
        return v == null || (v is UnityEngine.Object && (UnityEngine.Object)(object)v == null) ? null : v;
    }

    static Component BodyOf(IDictionary rigs, string name)
    {
        if (!rigs.Contains(name)) return null;
        return Field<Component>(rigs[name], "body");
    }

    static Vector3 Prop(object o, string n, Vector3 dflt)
    {
        try { var p = o.GetType().GetProperty(n, All); if (p != null) return (Vector3)p.GetValue(o, null); } catch { }
        return dflt;
    }

    // Per rig, every frame after the fishing mod placed it: enlarge the pole about its base, add the Merc swing if
    // this player is whipping, then move the line start to the new tip and carry a dangling bobber along (spring).
    static void AnimateRods()
    {
        if (fishing == null) return;
        var rigs = fishing.GetField("rigs", All).GetValue(null) as IDictionary;
        if (rigs == null) return;
        foreach (DictionaryEntry e in rigs)
        {
            string name = Convert.ToString(e.Key); object rig = e.Value;
            Transform pole = Field<Transform>(rig, "poleTf");
            if (pole == null || !pole.gameObject.activeInHierarchy) continue;
            Vector3 basePoint = pole.position - pole.up * pole.localScale.y;
            Vector3 origTip = basePoint + pole.up * pole.localScale.y * 2f;
            pole.localScale = new Vector3(RodThickness, RodLength, RodThickness);
            pole.position = basePoint + pole.up * RodLength;
            Component body = Field<Component>(rig, "body");
            bool sw = swinging.Contains(name);
            RodHandAttach.Attach(body, pole, sw, true, !sw);      // handle into the right hand (+ arm pose when idle)
            if (sw)
            {
                Vector3 aim = body == null ? Vector3.forward : body.transform.forward;
                if (body != null)
                {
                    object bank = body.GetType().GetProperty("inputBank", All).GetValue(body, null);
                    if (bank != null) aim = Prop(bank, "aimDirection", aim);
                }
                bool on = MercSwingLook.Apply(name, pole, body == null ? null : body.transform, aim, name == FishingScope.LocalName(), body == null ? null : body.gameObject);
                if (!on) swinging.Remove(name);
                RodHandAttach.Attach(body, pole, on, false, true);  // arm follows the swung rod, handle stays in the hand
            }
            Vector3 newTip = pole.position + pole.up * pole.localScale.y;
            MercSwingLook.FollowTip(name, Field<Transform>(rig, "bobTf"), Field<LineRenderer>(rig, "line"), origTip, newTip);
        }
        for (int i = swinging.Count - 1; i >= 0; i--)   // swings of players without a rig
            if (!rigs.Contains(swinging[i]) && !MercSwingLook.IsSwinging(swinging[i])) swinging.RemoveAt(i);
        for (int i = pendingHits.Count - 1; i >= 0; i--)
        {
            if (Time.time < pendingHits[i].Value) continue;
            string[] p = pendingHits[i].Key.Split('|');
            pendingHits.RemoveAt(i);
            Component tb = BodyOf(rigs, p[1]), ab = BodyOf(rigs, p[0]);
            if (tb == null) continue;
            Vector3 at = Prop(tb, "corePosition", tb.transform.position + Vector3.up);
            Vector3 from = ab == null ? at - tb.transform.forward : Prop(ab, "corePosition", ab.transform.position);
            MercSwingLook.HitSpark(at, from);
        }
    }
}
