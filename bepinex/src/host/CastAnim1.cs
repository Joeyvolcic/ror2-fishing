// Injected into JV94's (host) game. Visual-only cast animation for the newest Fishing<N>'s rod rigs (CastAnimLook.cs).
// Runs in the same LateUpdate event as the newest WhipAnim<N> (its RodHandAttach.Late), subscribed after it, so the frame
// order is: Fishing onUpdate (rig placed) -> Animator -> WhipAnim late (rod into the hand, whip swing, bobber follow)
// -> CastAnim late (cast/reel/bite/idle pose, rod bend, bobber arc/bob, curved line). Gameplay is untouched: it only reads
// the rig's castTime/reelTime/flight/landing point and the angler state. Pet rigs ("<owner>'s pet") are left alone.
// Disables older CastAnim<N> (they restore the meshes/line they changed). Tuning: %TEMP%\ror2_castanim.cfg (live).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

public static class CastAnim1
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static bool enabled = true;
    public static int lateFrames;
    static bool wasOn = true;
    static Type fishing, whip, rha, msl;
    static FieldInfo fRigs, fAnglers, fClock, fWaterY, fHasWater, fTable;
    static MethodInfo mAttach, mIsSwinging;
    static Type rigType; static FieldInfo rBody, rPole, rBob, rLine, rCast, rReel, rFlight, rArc, rTx, rTy, rTz;
    static Type angType; static FieldInfo aState;
    static readonly Dictionary<string, CastAnimLook.Input> inputs = new Dictionary<string, CastAnimLook.Input>();
    static readonly Dictionary<string, float> pitchCache = new Dictionary<string, float>();
    static float pitchCacheAt;
    static bool hookedLate; static int frames, errs, lastFrame = -1;
    static GameObject ownDriver;

    static void Log(string s) { CastAnimLook.Log(typeof(CastAnim1).Name + ": " + s); }

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
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("CastAnim") && t != typeof(CastAnim1) && t.Name != "CastAnimLook")
                {
                    int n; if (!int.TryParse(t.Name.Substring(8), out n)) continue;
                    FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false);
                }
        CastAnimLook.SplashType = "FishFx";
        Bind();
        Type app = null;
        foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("RoR2.RoR2Application"); if (t != null) app = t; }
        Action u = Frame;
        app.GetEvent("onUpdate", All).AddEventHandler(null, u);
        Log("installed: fishing=" + (fishing == null ? "-" : fishing.Name) + " whip=" + (whip == null ? "-" : whip.Name) + " lateHook=" + hookedLate);
    }

    static void Bind()
    {
        Type f = Newest("Fishing");
        if (f != null && f != fishing)
        {
            fishing = f;
            fRigs = f.GetField("rigs", All); fAnglers = f.GetField("anglers", All); fClock = f.GetField("clock", All);
            fWaterY = f.GetField("waterY", All); fHasWater = f.GetField("hasWater", All);
            rigType = null; angType = null;
            Log("bound to " + f.Name + " (rigs=" + (fRigs != null) + " anglers=" + (fAnglers != null) + " clock=" + (fClock != null) + " water=" + (fWaterY != null) + ")");
        }
        Type w = Newest("WhipAnim");
        if (w != null && w != whip)
        {
            whip = w; rha = w.Assembly.GetType("RodHandAttach"); msl = w.Assembly.GetType("MercSwingLook");
            mAttach = rha == null ? null : rha.GetMethod("Attach", All);
            fTable = rha == null ? null : rha.GetField("table", All);
            mIsSwinging = msl == null ? null : msl.GetMethod("IsSwinging", All);
            if (mAttach != null)
            {
                MethodInfo at = mAttach;
                CastAnimLook.Reseat = (b, p) => { object r = at.Invoke(null, new object[] { b, p, false, false, false }); return r is bool && (bool)r; };
            }
            EventInfo ev = rha == null ? null : rha.GetEvent("Late", All);
            if (ev != null)
            {
                try { Action h = LateFrame; ev.AddEventHandler(null, h); hookedLate = true; if (ownDriver != null) { UnityEngine.Object.Destroy(ownDriver); ownDriver = null; } }
                catch (Exception e) { Log("late hook on " + w.Name + " failed: " + e.Message); }
            }
            Log("bound to " + w.Name + " (RodHandAttach=" + (rha != null) + " Attach=" + (mAttach != null) + " MercSwingLook=" + (msl != null) + " late=" + hookedLate + ")");
        }
    }

    public class Driver : MonoBehaviour { void LateUpdate() { CastAnim1.LateFrame(); } }

    static void Frame()
    {
        try
        {
            if (++frames % 300 == 0) Bind();
            if (!hookedLate && ownDriver == null && frames > 2)
            {   // no WhipAnim with a late event: drive ourselves (order vs other LateUpdates not guaranteed)
                ownDriver = new GameObject("CastAnim1Late"); UnityEngine.Object.DontDestroyOnLoad(ownDriver);
                ownDriver.hideFlags = HideFlags.HideInHierarchy; ownDriver.AddComponent<Driver>();
                Log("no WhipAnim late event - own LateUpdate driver");
            }
        }
        catch (Exception e) { if (errs++ < 10) Log("frame error: " + e); }
    }

    static object F(object o, FieldInfo f) { return f == null ? null : f.GetValue(o); }
    static float Fl(object o, FieldInfo f, float d) { object v = F(o, f); return v is float ? (float)v : d; }

    public static void LateFrame()
    {
        if (!enabled)
        {
            if (wasOn) { wasOn = false; CastAnimLook.RestoreAll(); Log("disabled - rigs restored"); }
            return;
        }
        wasOn = true;
        if (Time.frameCount == lastFrame) return;   // subscribed to an older WhipAnim's event too: once per frame
        lastFrame = Time.frameCount;
        try
        {
            CastAnimLook.CheckCfg();
            if (fishing == null || fRigs == null) return;
            var rigs = fRigs.GetValue(null) as IDictionary;
            if (rigs == null) return;
            var anglers = fAnglers == null ? null : fAnglers.GetValue(null) as IDictionary;
            Stopwatch clock = fClock == null ? null : fClock.GetValue(null) as Stopwatch;
            float now = clock != null ? (float)clock.Elapsed.TotalSeconds : Time.realtimeSinceStartup;
            bool hasWater = fHasWater != null && (bool)fHasWater.GetValue(null);
            float waterY = fWaterY == null ? 0f : (float)fWaterY.GetValue(null);
            if (Time.realtimeSinceStartup - pitchCacheAt > 2f) { pitchCache.Clear(); pitchCacheAt = Time.realtimeSinceStartup; }
            lateFrames++;
            foreach (DictionaryEntry e in rigs)
            {
                string name = Convert.ToString(e.Key); object rig = e.Value;
                if (rig == null || name.EndsWith("'s pet")) continue;
                if (rigType != rig.GetType())
                {
                    rigType = rig.GetType();
                    rBody = rigType.GetField("body"); rPole = rigType.GetField("poleTf"); rBob = rigType.GetField("bobTf"); rLine = rigType.GetField("line");
                    rCast = rigType.GetField("castTime"); rReel = rigType.GetField("reelTime"); rFlight = rigType.GetField("flight"); rArc = rigType.GetField("arcH");
                    rTx = rigType.GetField("tx"); rTy = rigType.GetField("ty"); rTz = rigType.GetField("tz");
                }
                CastAnimLook.Input I;
                if (!inputs.TryGetValue(name, out I)) inputs[name] = I = new CastAnimLook.Input { key = name };
                I.body = F(rig, rBody) as Component;
                I.pole = F(rig, rPole) as Transform; I.bob = F(rig, rBob) as Transform; I.line = F(rig, rLine) as LineRenderer;
                if (I.pole == null || I.body == null) continue;
                I.castTime = Fl(rig, rCast, -100f); I.reelTime = Fl(rig, rReel, -100f); I.flight = Fl(rig, rFlight, 0.6f); I.arcH = Fl(rig, rArc, 3f);
                I.target = new Vector3(Fl(rig, rTx, 0f), Fl(rig, rTy, 0f), Fl(rig, rTz, 0f));
                I.now = now; I.hasWater = hasWater; I.waterY = waterY;
                I.state = 0;
                if (anglers != null && anglers.Contains(name))
                {
                    object a = anglers[name];
                    if (a != null && angType != a.GetType()) { angType = a.GetType(); aState = angType.GetField("state", All); }
                    object st = F(a, aState); if (st is int) I.state = (int)st;
                }
                I.swinging = false;
                if (mIsSwinging != null) { try { I.swinging = (bool)mIsSwinging.Invoke(null, new object[] { name }); } catch { } }
                I.extraPitch = RodPitch(I.body);
                CastAnimLook.Animate(I);
            }
            CastAnimLook.EndFrame();
            if (lateFrames == 1) Log("first late frame: " + rigs.Count + " rig(s), now=" + now.ToString("0.0") + " water=" + hasWater + " y=" + waterY.ToString("0.00"));
        }
        catch (Exception e) { if (errs++ < 10) Log("late error: " + e); }
    }

    // per-body extra pitch the rod-in-hand table applies (kept when the cast pose replaces the tilt)
    static float RodPitch(Component body)
    {
        if (fTable == null || body == null) return 0f;
        string n = body.gameObject.name.Replace("(Clone)", "").Trim();
        float p;
        if (pitchCache.TryGetValue(n, out p)) return p;
        p = 0f;
        try
        {
            var t = fTable.GetValue(null) as IDictionary;
            if (t != null && t.Contains(n)) { object en = t[n]; FieldInfo f = en.GetType().GetField("rodPitch"); if (f != null) p = (float)f.GetValue(en); }
        }
        catch { }
        pitchCache[n] = p;
        return p;
    }
}
