// CastAnimLook: visual-only cast / float / bite / reel / idle animation for the fishing rod rigs (pole cylinder,
// bobber sphere, LineRenderer). Shared by the host add-on (CastAnim<N>) and the guest client (FishingClient.cs).
// Needs only UnityEngine.CoreModule; RoR2 types are reached by reflection.
//
// Call Animate() once per rig per frame in LateUpdate, AFTER the fishing mod placed the rig and after the rod-in-hand /
// whip code moved it (RodHandAttach + MercSwingLook). It never changes gameplay: it only reads the rig's timing
// (castTime, reelTime, flight, landing point, angler state) and re-draws:
//   rod   : wind-up over the shoulder (ease-in) -> fast flick with overshoot + settle -> rest; reel yank; bite tugs;
//           idle sway from body movement + running bob; tip flex (spring) and load bend toward the bobber (mesh bend)
//   arm   : the upper arm follows part of the rod's extra rotation (bodies with a ChildLocator "UpperArmR")
//   bobber: pendulum on the line while dangling; leaves the tip at the release point of the flick; drag-eased arc
//           with spin; lands at the same point and the same moment as before (splash ring from FishFx); float bob,
//           landing dip + squash, bite dip and tugs, skipping back over the water while reeling
//   line  : LineRenderer with LineSegs points: sagging curve (slack on the water, taut while biting / reeling)
// Everything is time based (Time.deltaTime / the fishing clock), springs are sub-stepped at 240 Hz.
//
// Live tuning: %TEMP%\ror2_castanim.cfg, "Name=value" pairs (any public static float/int/bool field below, case-insensitive,
// several per line, # comments), re-read within 1 s of saving.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class CastAnimLook
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static bool enabled = true;

    // ---- cast timing (seconds). The release moment is WindUp+Hold+Flick*ReleaseFrac after the key press; if that is
    // later than MaxReleaseFrac of the flight time, the whole wind-up/flick is time-compressed (landing time is fixed).
    public static float WindUp = 0.22f, Hold = 0.05f, Flick = 0.16f, ReleaseFrac = 0.45f, MaxReleaseFrac = 0.5f, Settle = 0.55f;
    // ---- rod angles (degrees above horizontal; >90 = back over the shoulder)
    public static float WindBack = 118f, WindYaw = 12f, FlickEnd = 6f, Overshoot = 1.5f, RestTilt = 24f, IdleTilt = 32f;
    public static float SettleWobble = 4f, YankTilt = 70f, BiteTilt = 8f;
    public static float ArmFollow = 0.45f;          // share of the rod's extra rotation the upper arm follows
    // ---- rod flex: the tip is a damped spring behind the rigid tip (whippy flick, wobble after) + line load
    public static float BendFreq = 5.5f, BendDamping = 0.32f, FlexGain = 0.8f, MaxBend = 0.5f;
    public static float LoadFloat = 0.04f, LoadBite = 0.28f, LoadReel = 0.42f;
    // ---- bobber
    public static float Dangle = 0.6f, DangleDamping = 1.6f, Gravity = 14f, HandoffSpeed = 1.5f;
    public static float FlightDrag = 0.35f, Spin = 720f;
    public static float SplashSize = 0.9f, LandDip = 0.22f, Squash = 0.28f;
    public static float BobAmp = 0.05f, BobPeriod = 1.7f, Drift = 0.04f, Rock = 6f;
    public static float BiteDip = 0.4f, TugPeriod = 0.6f, TugAmp = 0.6f, BiteJitter = 0.03f, BitePull = 0.12f;
    public static float ReelDur = 0.6f, Skips = 3f, SkipHeight = 0.35f, SkipSplash = 0.45f, ReelLift = 0.45f;
    // ---- line
    public static int LineSegs = 24;
    public static float IdleSag = 0.5f, FlightSag = 0.05f, SlackSag = 0.12f, BiteSag = 0.01f, ReelSag = 0.004f, GroundSag = 0.08f, MaxSag = 6f;
    // ---- idle sway from movement
    public static float Sway = 1f, RunBob = 2.5f, SwayFreq = 1.6f, SwayDamping = 0.45f, StrideLen = 1.5f;
    public static float Blend = 0.1f;               // cross-fade (s) on any state change, so nothing ever snaps
    public static bool Bend = true, Arm = true, Splashes = true;

    public static string CfgPath = Path.Combine(Path.GetTempPath(), "ror2_castanim.cfg");
    public static string LogPath = Path.Combine(Path.GetTempPath(), "ror2_castanim.log");
    public static string SplashType = "FishFx";     // host: newest FishFx<N>; guest client: "FishFxClient"
    public static Func<Component, Transform, bool> Reseat;   // put the rod handle back into the hand after rotating it
    public static int frames, errors;

    // per-rig inputs (filled by the caller every frame)
    public class Input
    {
        public string key; public Component body; public Transform pole, bob; public LineRenderer line;
        public int state;                 // 0 idle, 1 waiting, 2 biting, 3 ground cast (others = waiting)
        public float now, castTime = -100f, reelTime = -100f, flight = 0.6f, arcH = 3f;   // fishing clock
        public Vector3 target;            // landing point (bobber rest)
        public bool hasWater; public float waterY;
        public bool swinging;             // Rod Whip running: leave the rod direction to the whip
        public float extraPitch;          // per-body pitch the rod-in-hand code added (kept)
        public bool hasFwd; public Vector3 fwd;   // optional aim override (no body)
    }

    const int IDLE = 0, CAST = 1, FLOAT = 2, BITE = 3, GROUND = 4, REEL = 5;
    static readonly string[] PhaseNames = { "idle", "cast", "float", "bite", "ground", "reel" };

    class St
    {
        public Transform pole, bob; public LineRenderer line; public Component body;
        public float lastCast = -999f, lastReel = -999f; public int phase = -1; public bool fresh = true;
        public float tiltVis, yawVis, tiltPrev; public bool hasVis;
        public float castStartTilt, reelStartTilt;
        public float blendT0 = -99f, blendTilt, blendYaw; public Vector3 blendBob;
        public float wRod = 1f, wSway = 1f;
        public Vector3 q, qv; public bool qInit;            // flex tip spring
        public Vector3 lastBodyPos, velS; public bool bodyInit; public float swP, swPv, swY, swYv, stride;
        public Vector3 p, v, prevTip; public bool pInit;     // pendulum
        public bool released; public Vector3 launch; public float relAt;
        public bool landed; public float landT, biteT0 = -99f;
        public Vector3 reelStart; public bool reelOnWater; public int skipsDone;
        public Vector3 bobVis, bobPrev; public float sagVis; public Quaternion extra = Quaternion.identity;
        public Vector3 baseScale, lastScale; public Quaternion baseRot, lastRot; public bool bobInit;
        public List<MeshFilter> mfs; public int mfChildren = -1; public bool bent;
        public Transform upper; public bool armResolved;
        public Vector3[] pts;
        public int seenFrame;
    }
    static readonly Dictionary<string, St> sts = new Dictionary<string, St>();

    class MeshRec { public Mesh orig, work; public Vector3[] v0, buf; }
    static readonly Dictionary<MeshFilter, MeshRec> meshes = new Dictionary<MeshFilter, MeshRec>();

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

    // ------------------------------------------------------------------ easing
    static float Sat(float x) { return x < 0f ? 0f : x > 1f ? 1f : x; }
    static float Smooth(float x) { x = Sat(x); return x * x * (3f - 2f * x); }
    static float OutCubic(float x) { x = 1f - Sat(x); return 1f - x * x * x; }
    static float OutQuad(float x) { x = 1f - Sat(x); return 1f - x * x; }
    static float OutBack(float x, float c1) { x = Sat(x); float c3 = c1 + 1f, u = x - 1f; return 1f + c3 * u * u * u + c1 * u * u; }
    static float WindEase(float x) { x = Sat(x); return 0.5f * x * x * (3f - 2f * x) + 0.5f * x * x; }   // slow start, still moving at the top
    static float Pulse(float x) { return x < 0f ? 0f : x < 0.07f ? OutQuad(x / 0.07f) : Mathf.Exp(-(x - 0.07f) * 7f); }
    static float Hash(int n) { float s = Mathf.Sin(n * 12.9898f + 4.1414f) * 43758.547f; return s - Mathf.Floor(s); }
    static float Damp(float rate, float dt) { return 1f - Mathf.Exp(-rate * dt); }

    // ------------------------------------------------------------------ main entry
    public static void Animate(Input I)
    {
        if (I == null || I.pole == null || string.IsNullOrEmpty(I.key)) return;
        St s;
        if (!sts.TryGetValue(I.key, out s) || s.pole != I.pole || s.bob != I.bob)
        {
            if (s != null) Restore(s);
            s = new St { pole = I.pole, bob = I.bob, line = I.line };
            sts[I.key] = s;
        }
        s.seenFrame = Time.frameCount; s.line = I.line; s.body = I.body;
        if (!enabled) { Restore(s); return; }
        if (!I.pole.gameObject.activeInHierarchy) return;
        try { Step(I, s); }
        catch (Exception e) { if (errors++ < 20) Log("animate error (" + I.key + "): " + e); }
    }

    // forget rigs not animated this frame (rig removed / body changed); call once per frame after all Animate calls
    public static void EndFrame()
    {
        frames++;
        if (frames % 30 != 0) return;
        List<string> gone = null;
        foreach (var kv in sts) if (Time.frameCount - kv.Value.seenFrame > 30 || kv.Value.pole == null) (gone = gone ?? new List<string>()).Add(kv.Key);
        if (gone != null) foreach (string k in gone) { Restore(sts[k]); sts.Remove(k); }
    }

    // undo everything (meshes, line point count, bobber scale/rotation); safe to call every frame
    public static void RestoreAll()
    {
        foreach (var kv in sts) Restore(kv.Value);
        sts.Clear();
    }

    static void Restore(St s)
    {
        try
        {
            if (s.mfs != null) foreach (MeshFilter mf in s.mfs) RestoreMesh(mf);
            s.bent = false;
            if (s.line != null && s.line.positionCount != 2 && s.pole != null && s.bob != null)
            {
                s.line.positionCount = 2;
                s.line.SetPosition(0, s.pole.position + s.pole.up * s.pole.localScale.y);
                s.line.SetPosition(1, s.bob.position);
            }
            if (s.bob != null && s.bobInit) { s.bob.localScale = s.baseScale; s.bob.rotation = s.baseRot; s.bobInit = false; }
        }
        catch { }
    }

    static void Step(Input I, St s)
    {
        float dt = Mathf.Clamp(Time.deltaTime, 0.0001f, 0.1f);
        float tt = Time.time, now = I.now;
        Transform pole = I.pole;

        // ---- body frame
        Vector3 up = Vector3.up, fwd = Vector3.forward;
        if (I.body != null) { up = I.body.transform.up; fwd = I.body.transform.forward; object d = Prop(Prop(I.body, "inputBank"), "aimDirection"); if (d is Vector3) fwd = (Vector3)d; }
        if (I.hasFwd) fwd = I.fwd;
        fwd = Vector3.ProjectOnPlane(fwd, up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(pole.up, up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(up, fwd);

        // ---- phase
        bool reeling = I.reelTime > I.castTime;
        float sinceCast = now - I.castTime, sinceReel = now - I.reelTime;
        int ph;
        if (reeling) ph = sinceReel < ReelDur ? REEL : IDLE;
        else if (I.state == 0 || I.castTime < -50f) ph = IDLE;
        else if (sinceCast < I.flight) ph = CAST;
        else if (I.state == 2) ph = BITE;
        else if (I.state == 3) ph = GROUND;
        else ph = FLOAT;

        if (I.castTime != s.lastCast)
        {
            if (!s.fresh) { s.castStartTilt = s.hasVis ? s.tiltVis : IdleTilt; s.released = false; s.landed = false; s.biteT0 = -99f; }
            else { s.castStartTilt = IdleTilt; s.released = sinceCast > 0.5f; s.landed = sinceCast > I.flight; }
            s.lastCast = I.castTime;
        }
        if (I.reelTime != s.lastReel)
        {
            if (reeling && !s.fresh)
            {
                s.reelStart = s.hasVis ? s.bobVis : (I.bob != null ? I.bob.position : I.target);
                s.reelStartTilt = s.hasVis ? s.tiltVis : IdleTilt;
                s.reelOnWater = (s.phase == FLOAT || s.phase == BITE) && I.hasWater;
                s.skipsDone = 0;
                if (s.reelOnWater) DoSplash(s.reelStart, I.waterY, SplashSize * SkipSplash);
            }
            else if (reeling) { s.reelStart = I.bob != null ? I.bob.position : I.target; s.reelStartTilt = IdleTilt; s.reelOnWater = false; s.skipsDone = 99; }
            s.lastReel = I.reelTime;
        }
        if (ph != s.phase)
        {
            if (s.phase >= 0)
            {
                s.blendT0 = tt; s.blendTilt = s.tiltVis; s.blendYaw = s.yawVis; s.blendBob = s.bobVis;
                if (ph == IDLE && s.phase != CAST) { s.p = s.bobVis; s.v = Vector3.ClampMagnitude((s.bobVis - s.bobPrev) / dt, HandoffSpeed); s.pInit = true; }
            }
            if (ph == BITE) s.biteT0 = now;
            if (frames < 100000 && I.key == "JV94") Log(I.key + ": " + (s.phase >= 0 ? PhaseNames[s.phase] : "-") + " -> " + PhaseNames[ph] + " (state " + I.state + ", sinceCast " + sinceCast.ToString("0.00") + ", flight " + I.flight.ToString("0.00") + ")");
            s.phase = ph;
        }
        s.fresh = false;

        // ---- cast timeline (time-compressed so the release stays inside the flight)
        float relNom = WindUp + Hold + Flick * ReleaseFrac;
        float sc = Mathf.Min(1f, MaxReleaseFrac * Mathf.Max(0.2f, I.flight) / Mathf.Max(0.01f, relNom));
        float tW = WindUp * sc, tH = Hold * sc, tF = Flick * sc;
        s.relAt = tW + tH + tF * ReleaseFrac;

        // ---- rod tilt / yaw targets
        float idleT = IdleTilt + Mathf.Sin(tt * 1.5f) * 2f;
        float tilt, yaw = 0f, load = 0f, swayTarget = 0f, dip = 0f;
        if (ph == IDLE) { tilt = idleT; swayTarget = 1f; }
        else if (ph == REEL)
        {
            float u = sinceReel / ReelDur, a = 0.3f;
            tilt = u < a ? Mathf.Lerp(s.reelStartTilt, YankTilt, OutCubic(u / a)) : Mathf.Lerp(YankTilt, idleT, Smooth((u - a) / (1f - a)));
            load = LoadReel * Mathf.Pow(1f - Sat(u), 1.5f);
        }
        else
        {
            tilt = CastTilt(sinceCast, tW, tH, tF, s.castStartTilt, tt, out yaw);
            bool settled = sinceCast > tW + tH + tF + Settle;
            swayTarget = settled ? 1f : 0f;
            if (ph == FLOAT) load = LoadFloat;
            else if (ph == GROUND) load = LoadFloat * 0.5f;
            else if (ph == BITE)
            {
                dip = BiteSignal(now - s.biteT0);
                tilt -= BiteTilt * (0.35f + dip);
                load = LoadBite * (0.3f + dip);
            }
        }

        // ---- movement sway (spring, sub-stepped)
        Vector3 bpos = I.body != null ? I.body.transform.position : pole.position;
        if (!s.bodyInit || (bpos - s.lastBodyPos).sqrMagnitude > 25f) { s.bodyInit = true; s.lastBodyPos = bpos; s.velS = Vector3.zero; }
        Vector3 vel = (bpos - s.lastBodyPos) / dt; s.lastBodyPos = bpos;
        s.velS = Vector3.Lerp(s.velS, vel, Damp(10f, dt));
        float vF = Vector3.Dot(s.velS, fwd), vR = Vector3.Dot(s.velS, right), vU = Vector3.Dot(s.velS, up);
        float pT = Mathf.Clamp(vF * 0.9f + vU * 0.5f, -10f, 12f) * Sway, yT = Mathf.Clamp(-vR * 1.6f, -14f, 14f) * Sway;
        float hs = new Vector2(vF, vR).magnitude;
        s.stride += hs * dt;
        float w0 = 2f * Mathf.PI * SwayFreq;
        int n = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 24); float h = dt / n;
        for (int k = 0; k < n; k++)
        {
            s.swPv += (w0 * w0 * (pT - s.swP) - 2f * SwayDamping * w0 * s.swPv) * h; s.swP += s.swPv * h;
            s.swYv += (w0 * w0 * (yT - s.swY) - 2f * SwayDamping * w0 * s.swYv) * h; s.swY += s.swYv * h;
        }
        float runBob = Mathf.Sin(s.stride / Mathf.Max(0.2f, StrideLen) * 2f * Mathf.PI) * RunBob * Sat(hs / 6f) * Sway;
        s.wSway = Mathf.MoveTowards(s.wSway, swayTarget, dt * 3f);
        tilt += s.wSway * (s.swP + runBob); yaw += s.wSway * s.swY;

        // ---- cross-fade from the last shown pose after a state change
        float bl = Blend > 0f ? Smooth((tt - s.blendT0) / Blend) : 1f;
        if (s.hasVis && bl < 1f) { tilt = Mathf.Lerp(s.blendTilt, tilt, bl); yaw = Mathf.Lerp(s.blendYaw, yaw, bl); }
        s.tiltPrev = s.tiltVis; s.tiltVis = tilt; s.yawVis = yaw; s.hasVis = true;

        // ---- rotate the rod (about its grip) from where the fishing / hand code put it to this pose
        s.wRod = Mathf.MoveTowards(s.wRod, I.swinging ? 0f : 1f, dt * 8f);
        Vector3 dir0 = pole.up;
        float half = pole.localScale.y;
        Vector3 butt = pole.position - dir0 * half;
        Vector3 pivot = butt + dir0 * Mathf.Min(0.12f, half);
        float measured = Mathf.Asin(Mathf.Clamp(Vector3.Dot(dir0, up), -1f, 1f)) * Mathf.Rad2Deg;
        Vector3 hdir = Vector3.ProjectOnPlane(dir0, up); if (hdir.sqrMagnitude < 1e-4f) hdir = fwd; hdir.Normalize();
        Vector3 rodRight = Vector3.Cross(up, hdir).normalized;
        Quaternion q = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(-(tilt + I.extraPitch - measured), rodRight);
        if (s.wRod < 1f) q = Quaternion.Slerp(Quaternion.identity, q, s.wRod);
        pole.rotation = q * pole.rotation;
        pole.position = pivot + q * (pole.position - pivot);
        bool reseated = false;
        if (I.body != null && Arm && ArmFollow > 0f && s.wRod > 0.001f)
        {
            Transform ua = UpperArm(s, I.body);
            if (ua != null) ua.rotation = Quaternion.Slerp(Quaternion.identity, q, ArmFollow) * ua.rotation;
        }
        if (I.body != null && Reseat != null) { try { reseated = Reseat(I.body, pole); } catch { } }
        Vector3 dir = pole.up; half = pole.localScale.y;
        Vector3 tipRigid = pole.position + dir * half;

        // ---- bobber position for this phase (needs the tip; flex uses the bobber of the last frame for the load)
        Vector3 toBob = s.hasVis ? s.bobVis - tipRigid : -up;
        Vector3 loadDir = Vector3.ProjectOnPlane(toBob, dir); loadDir = loadDir.sqrMagnitude > 1e-4f ? loadDir.normalized : -up;
        Vector3 loadVec = loadDir * load;

        // ---- flex: tip spring lags behind the rigid tip
        if (!s.qInit || (s.q - tipRigid).sqrMagnitude > 9f) { s.q = tipRigid; s.qv = Vector3.zero; s.qInit = true; s.prevTip = tipRigid; }
        Vector3 prevRigid = s.prevTip;
        float wb = 2f * Mathf.PI * BendFreq;
        for (int k = 0; k < n; k++)
        {
            Vector3 tgt = Vector3.Lerp(prevRigid, tipRigid, (k + 1f) / n) + loadVec;
            s.qv += (wb * wb * (tgt - s.q) - 2f * BendDamping * wb * s.qv) * h;
            s.q += s.qv * h;
        }
        Vector3 bend = s.q - tipRigid - loadVec;
        bend = Vector3.ProjectOnPlane(bend * FlexGain, dir) + loadVec;
        bend = Vector3.ClampMagnitude(bend, MaxBend);
        if (!Bend) bend = Vector3.zero;
        ApplyBend(s, pole, bend);
        Vector3 tip = tipRigid + bend;

        // ---- bobber
        Vector3 bob;
        Quaternion extraT = Quaternion.identity; bool spinNow = false; float squash = 0f;
        Vector3 dangle = tip - up * Dangle;
        if (ph == IDLE || (ph == CAST && sinceCast < s.relAt))
        {
            bob = Pendulum(s, tip, up, dt, n);
        }
        else if (ph == CAST)
        {
            if (!s.released) { s.released = true; s.launch = s.pInit ? s.p : dangle; }
            float fl = Mathf.Max(0.05f, I.flight - s.relAt), u = Sat((sinceCast - s.relAt) / fl);
            float hu = u * (1f + FlightDrag * (1f - u));
            Vector3 a = s.launch, b = I.target;
            bob = new Vector3(Mathf.LerpUnclamped(a.x, b.x, hu), 0f, Mathf.LerpUnclamped(a.z, b.z, hu));
            bob.y = Mathf.Lerp(a.y, b.y, u) + 4f * I.arcH * u * (1f - u);
            Vector3 fd = Vector3.ProjectOnPlane(b - a, up); if (fd.sqrMagnitude < 1e-4f) fd = fwd;
            extraT = Quaternion.AngleAxis(Spin * (sinceCast - s.relAt) * (1f - 0.4f * u), Vector3.Cross(up, fd.normalized));
            spinNow = true;
            s.p = bob; s.v = Vector3.zero;
        }
        else if (ph == REEL)
        {
            float u = Sat(sinceReel / ReelDur), pu = Smooth(u);
            Vector3 a = s.reelStart, b = dangle;
            bob = new Vector3(Mathf.Lerp(a.x, b.x, pu), 0f, Mathf.Lerp(a.z, b.z, pu));
            if (s.reelOnWater)
            {
                float lift = Mathf.Clamp(ReelLift, 0.1f, 0.9f);
                float y0 = a.y;
                if (u < lift)
                {
                    float x = u / lift, hop = SkipHeight * Mathf.Abs(Mathf.Sin(Mathf.PI * Skips * x)) * (1f - 0.5f * x);
                    bob.y = y0 + hop;
                    int touched = Mathf.FloorToInt(x * Skips);
                    if (touched > s.skipsDone && touched < Skips) { s.skipsDone = touched; DoSplash(bob, I.waterY, SplashSize * SkipSplash); }
                }
                else bob.y = Mathf.Lerp(y0, b.y, Smooth((u - lift) / (1f - lift)));
            }
            else bob.y = Mathf.Lerp(a.y, b.y, pu) + Mathf.Min(2f, (b - a).magnitude * 0.08f) * Mathf.Sin(Mathf.PI * pu);
            s.p = bob; s.v = (bob - s.bobVis) / dt;
        }
        else
        {
            if (!s.landed)
            {
                s.landed = true; s.landT = now;
                if (ph != GROUND && I.hasWater) DoSplash(I.target, I.waterY, SplashSize);
            }
            float lt = now - s.landT;
            if (ph == GROUND)
            {
                bob = I.target + up * (Mathf.Abs(Mathf.Sin(lt * 9f)) * 0.3f * Mathf.Exp(-lt * 5f));
                squash = Squash * 0.6f * Mathf.Exp(-lt * 8f) * Mathf.Cos(lt * 20f);
            }
            else
            {
                float land = -LandDip * Mathf.Exp(-lt * 4.5f) * Mathf.Sin(lt * 11f);
                float bobY = BobAmp * Mathf.Sin(tt * 2f * Mathf.PI / Mathf.Max(0.2f, BobPeriod)) + 0.35f * BobAmp * Mathf.Sin(tt * 2.9f + 1.3f);
                Vector3 drift = (right * Mathf.Cos(tt * 0.41f) + fwd * Mathf.Sin(tt * 0.33f)) * Drift;
                squash = Squash * Mathf.Exp(-lt * 7f) * Mathf.Cos(lt * 18f);
                float rk = Rock;
                if (ph == BITE)
                {
                    Vector3 toAngler = Vector3.ProjectOnPlane(tip - I.target, up); toAngler = toAngler.sqrMagnitude > 1e-4f ? toAngler.normalized : -fwd;
                    Vector3 jit = (right * Mathf.Sin(tt * 53f) + fwd * Mathf.Cos(tt * 47f)) * BiteJitter * (0.3f + dip);
                    bob = I.target + up * (land + 0.4f * bobY - BiteDip * dip) + toAngler * BitePull * dip + jit + drift * 0.5f;
                    squash += 0.15f * dip; rk *= 2.2f;
                }
                else bob = I.target + up * (land + bobY) + drift;
                extraT = Quaternion.AngleAxis(rk * Mathf.Sin(tt * 1.9f), fwd) * Quaternion.AngleAxis(rk * 0.6f * Mathf.Sin(tt * 1.3f + 0.7f), right);
            }
            s.p = bob; s.v = Vector3.zero;
        }
        if (s.hasVis && bl < 1f && s.blendBob != Vector3.zero) bob = Vector3.Lerp(s.blendBob, bob, bl);
        s.bobPrev = s.bobVis; s.bobVis = bob;

        if (I.bob != null)
        {
            Transform bt = I.bob;
            Vector3 cs = bt.localScale; Quaternion cr = bt.rotation;
            if (!s.bobInit || cs != s.lastScale) s.baseScale = cs;
            if (!s.bobInit || cr != s.lastRot) s.baseRot = cr;
            s.bobInit = true;
            s.extra = spinNow ? extraT : Quaternion.Slerp(s.extra, extraT, Damp(10f, dt));
            bt.position = bob;
            Vector3 k3 = new Vector3(1f + squash * 0.6f, 1f - squash, 1f + squash * 0.6f);
            bt.localScale = s.lastScale = Vector3.Scale(s.baseScale, k3);
            bt.rotation = s.lastRot = s.extra * s.baseRot;
        }

        // ---- line
        if (I.line != null)
        {
            float dist = (bob - tip).magnitude, sagT, rate;
            switch (ph)
            {
                case CAST:
                    if (sinceCast < s.relAt) { sagT = Mathf.Max(0f, Dangle - dist) * IdleSag + 0.005f; rate = 14f; }
                    else { sagT = FlightSag * dist * Sat((sinceCast - s.relAt) / Mathf.Max(0.05f, I.flight - s.relAt)); rate = 30f; }
                    break;
                case FLOAT: sagT = SlackSag * dist; rate = 2.5f; break;
                case BITE: sagT = BiteSag * dist * (1f - 0.7f * dip); rate = 10f; break;
                case GROUND: sagT = GroundSag * dist; rate = 3f; break;
                case REEL: sagT = ReelSag * dist; rate = 14f; break;
                default: sagT = Mathf.Max(0f, Dangle - dist) * IdleSag + 0.005f; rate = 12f; break;
            }
            sagT = Mathf.Min(sagT, MaxSag);
            s.sagVis = Mathf.Lerp(s.sagVis, sagT, Damp(rate, dt));
            int N = Mathf.Clamp(LineSegs, 2, 64);
            if (s.pts == null || s.pts.Length != N) s.pts = new Vector3[N];
            bool clampW = I.hasWater && (ph == FLOAT || ph == BITE);
            Vector3 lat = Vector3.Cross(up, (bob - tip).sqrMagnitude > 1e-4f ? (bob - tip).normalized : fwd);
            for (int i = 0; i < N; i++)
            {
                float u = i / (N - 1f), arc = 4f * u * (1f - u);
                Vector3 pt = Vector3.Lerp(tip, bob, u) - up * (s.sagVis * arc);
                if (ph == BITE) pt += lat * (Mathf.Sin(u * Mathf.PI * 4f + tt * 45f) * 0.03f * dip * arc);
                if (clampW && i > 0 && i < N - 1 && pt.y < I.waterY + 0.02f) pt.y = I.waterY + 0.02f;
                s.pts[i] = pt;
            }
            if (I.line.positionCount != N) I.line.positionCount = N;
            I.line.SetPositions(s.pts);
        }
        s.prevTip = tipRigid;
    }

    // rod angle over the cast: wind-up -> hold -> flick (overshoot) -> settle to the waiting angle
    static float CastTilt(float e, float tW, float tH, float tF, float start, float tt, out float yaw)
    {
        float rest = RestTilt + Mathf.Sin(tt * 1.5f) * 1.5f;
        if (e < 0f) { yaw = 0f; return start; }
        if (e < tW) { float u = e / tW; yaw = WindYaw * Smooth(u); return Mathf.Lerp(start, WindBack, WindEase(u)); }
        e -= tW;
        if (e < tH) { yaw = WindYaw; return WindBack + 3f * Smooth(e / Mathf.Max(0.001f, tH)); }
        e -= tH;
        if (e < tF) { float u = e / tF; yaw = WindYaw * (1f - OutCubic(u)); return Mathf.LerpUnclamped(WindBack + 3f, FlickEnd, OutBack(u, Overshoot)); }
        e -= tF;
        yaw = 0f;
        float v = Sat(e / Mathf.Max(0.05f, Settle));
        return Mathf.Lerp(FlickEnd, rest, Smooth(v)) - SettleWobble * Mathf.Sin(v * Mathf.PI * 2f) * (1f - v);
    }

    // 0..1 dip signal while a fish bites: a hard first dip, then tugs every ~TugPeriod s with varied strength
    static float BiteSignal(float bt)
    {
        if (bt < 0f) return 0f;
        float sig = Pulse(bt);
        float per = Mathf.Max(0.15f, TugPeriod);
        int k = Mathf.FloorToInt((bt - 0.15f) / per);
        for (int j = Mathf.Max(1, k - 1); j <= k + 1 && j >= 1; j++)
        {
            float start = 0.15f + j * per - 0.18f * per * Hash(j);
            sig = Mathf.Max(sig, TugAmp * (0.6f + 0.4f * Hash(j + 7)) * Pulse(bt - start));
        }
        return sig;
    }

    static Vector3 Pendulum(St s, Vector3 tip, Vector3 up, float dt, int n)
    {
        if (!s.pInit || (s.p - tip).sqrMagnitude > 25f) { s.p = tip - up * Dangle; s.v = Vector3.zero; s.pInit = true; s.prevTip = tip; }
        Vector3 from = s.prevTip; float h = dt / n;
        float damp = Mathf.Exp(-DangleDamping * h);
        for (int k = 0; k < n; k++)
        {
            Vector3 t = Vector3.Lerp(from, tip, (k + 1f) / n);
            s.v += -up * Gravity * h; s.v *= damp;
            s.p += s.v * h;
            Vector3 d = s.p - t; float m = d.magnitude;
            if (m > Dangle && m > 1e-5f)
            {
                Vector3 dn = d / m; s.p = t + dn * Dangle;
                float vr = Vector3.Dot(s.v, dn); if (vr > 0f) s.v -= dn * vr;
            }
        }
        return s.p;
    }

    // ------------------------------------------------------------------ rod mesh bend (pole + its skin decorations)
    static void ApplyBend(St s, Transform pole, Vector3 bendWorld)
    {
        bool zero = bendWorld.sqrMagnitude < 1e-6f;
        if (zero && !s.bent) return;
        if (s.mfs == null || s.mfChildren != pole.childCount || frames % 60 == 0)
        {
            var list = new List<MeshFilter>(pole.GetComponentsInChildren<MeshFilter>(true));
            if (s.mfs != null) foreach (MeshFilter old in s.mfs) if (old != null && !list.Contains(old)) RestoreMesh(old);
            s.mfs = list; s.mfChildren = pole.childCount;
        }
        Matrix4x4 w2l = pole.worldToLocalMatrix;
        Vector3 bl = pole.InverseTransformVector(bendWorld);
        foreach (MeshFilter mf in s.mfs)
        {
            if (mf == null) continue;
            if (zero) { RestoreMesh(mf); continue; }
            MeshRec m = Rec(mf); if (m == null) continue;
            Matrix4x4 c2p = w2l * mf.transform.localToWorldMatrix, p2c = c2p.inverse;
            for (int i = 0; i < m.v0.Length; i++)
            {
                Vector3 pl = c2p.MultiplyPoint3x4(m.v0[i]);
                float u = Sat((pl.y + 1f) * 0.5f);
                m.buf[i] = p2c.MultiplyPoint3x4(pl + bl * (u * u));
            }
            m.work.vertices = m.buf;
            m.work.RecalculateBounds();
        }
        s.bent = !zero;
    }

    static readonly HashSet<string> meshWarned = new HashSet<string>();
    static MeshRec Rec(MeshFilter mf)
    {
        MeshRec m;
        if (meshes.TryGetValue(mf, out m)) { if (mf.sharedMesh != m.work) { meshes.Remove(mf); m = null; } else return m; }
        Mesh orig = mf.sharedMesh;
        if (orig == null) return null;
        if (orig.name.StartsWith("CastAnimBend")) { if (meshWarned.Add(mf.name)) Log("mesh on " + mf.name + " is a leftover bent copy, skipped"); return null; }
        if (!orig.isReadable) { if (meshWarned.Add(mf.name + "|r")) Log("mesh " + orig.name + " on " + mf.name + " not readable, no bend"); return null; }
        m = new MeshRec { orig = orig, work = UnityEngine.Object.Instantiate(orig) };
        m.work.name = "CastAnimBend_" + orig.name;
        m.v0 = orig.vertices; m.buf = new Vector3[m.v0.Length];
        mf.sharedMesh = m.work;
        meshes[mf] = m;
        return m;
    }

    static void RestoreMesh(MeshFilter mf)
    {
        MeshRec m;
        if (mf == null || !meshes.TryGetValue(mf, out m)) return;
        meshes.Remove(mf);
        try { if (mf.sharedMesh == m.work) mf.sharedMesh = m.orig; UnityEngine.Object.Destroy(m.work); } catch { }
    }

    // ------------------------------------------------------------------ splash ring (FishFx)
    static MethodInfo splashM; static float splashLook = -100f;
    static void DoSplash(Vector3 at, float waterY, float size)
    {
        if (!Splashes || size <= 0f) return;
        try
        {
            if (splashM == null && Time.realtimeSinceStartup - splashLook > 10f)
            {
                splashLook = Time.realtimeSinceStartup;
                Type best = null; int bestN = -1;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, x => x != null); } catch { continue; }
                    foreach (Type t in ts)
                    {
                        if (!t.Name.StartsWith(SplashType)) continue;
                        int nn; string rest = t.Name.Substring(SplashType.Length);
                        if (rest.Length == 0) nn = 0; else if (!int.TryParse(rest, out nn)) continue;
                        FieldInfo enf = t.GetField("enabled", All);   // skip a disabled older copy (e.g. a 2.0 FishFxClient replaced in this session)
                        if (enf != null && enf.FieldType == typeof(bool) && !(bool)enf.GetValue(null)) continue;
                        if (nn > bestN && t.GetMethod("Splash", All, null, new[] { typeof(float), typeof(float), typeof(float), typeof(float) }, null) != null) { best = t; bestN = nn; }
                    }
                }
                if (best != null) { splashM = best.GetMethod("Splash", All, null, new[] { typeof(float), typeof(float), typeof(float), typeof(float) }, null); Log("splash ring from " + best.FullName); }
                else Log("no " + SplashType + "*.Splash found (no splash rings)");
            }
            if (splashM != null) splashM.Invoke(null, new object[] { at.x, waterY, at.z, size });
        }
        catch (Exception e) { if (errors++ < 20) Log("splash error: " + (e.InnerException ?? e).Message); splashM = null; }
    }

    // ------------------------------------------------------------------ arm
    static readonly string[] noArm = { "ToolbotBody", "TreebotBody", "CrocoBody", "LoaderBody", "HereticBody" };
    static Transform UpperArm(St s, Component body)
    {
        if (s.armResolved) return s.upper;
        s.armResolved = true; s.upper = null;
        string name = body.gameObject.name.Replace("(Clone)", "").Trim();
        if (Array.IndexOf(noArm, name) >= 0) return null;
        Transform model = Prop(Prop(body, "modelLocator"), "modelTransform") as Transform;
        if (model == null) { s.armResolved = false; return null; }
        foreach (Component c in model.GetComponents<Component>())
        {
            if (c == null || c.GetType().Name != "ChildLocator") continue;
            MethodInfo f = c.GetType().GetMethod("FindChild", new[] { typeof(string) });
            if (f != null) { try { s.upper = f.Invoke(c, new object[] { "UpperArmR" }) as Transform; } catch { } }
        }
        Log(name + ": arm follow bone = " + (s.upper == null ? "none" : s.upper.name));
        return s.upper;
    }

    // ------------------------------------------------------------------ helpers
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

    // ------------------------------------------------------------------ live config
    static float nextCfg; static DateTime cfgTime;
    public static void CheckCfg()
    {
        if (Time.realtimeSinceStartup < nextCfg) return;
        nextCfg = Time.realtimeSinceStartup + 1f;
        try
        {
            if (!File.Exists(CfgPath)) return;
            DateTime t = File.GetLastWriteTimeUtc(CfgPath);
            if (t == cfgTime) return;
            cfgTime = t;
            var changed = new List<string>();
            foreach (string raw in File.ReadAllLines(CfgPath))
            {
                string line = raw; int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash);
                foreach (string tok in line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('='); if (eq <= 0) continue;
                    string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                    FieldInfo f = typeof(CastAnimLook).GetField(k, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (f == null) { changed.Add(k + "=?"); continue; }
                    if (f.FieldType == typeof(float)) { float x; if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) f.SetValue(null, x); }
                    else if (f.FieldType == typeof(int)) { int x; if (int.TryParse(v, out x)) f.SetValue(null, x); }
                    else if (f.FieldType == typeof(bool)) f.SetValue(null, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    else continue;
                    changed.Add(f.Name + "=" + f.GetValue(null));
                }
            }
            Log("config loaded: " + string.Join(" ", changed.ToArray()));
        }
        catch (Exception e) { Log("config error: " + e.Message); }
    }
}
