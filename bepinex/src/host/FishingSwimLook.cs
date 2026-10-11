// FishingSwimLook - swimming in the fishing lake ("FishingWater" on the host, "FishingClientWater" in FISHING.exe).
// Shared by the host add-on Swim<N> (JV94's own body + the host-side breath/drowning) and the guest client (FISHING.exe:
// the guest's own body + the breath bar from the host's [FT] BREATH lines).
//
// Movement is client-authoritative in RoR2, so each machine only moves its OWN body (motor.hasEffectiveAuthority):
//   - below the surface: gravity off (motor.useGravity = false), buoyancy pulls you up until the water is at chest/neck
//     height (FloatLevel x the foot->chest distance), gentle bobbing (BobAmp / BobPeriod), water drag on vertical speed.
//   - horizontal speed x SwimSpeed (motor.walkSpeedPenaltyCoefficient + a soft cap), wading in the shallows blends to it.
//   - hold JUMP: swim up; at the surface: jump out (JumpOut x jumpPower) - climbs onto a ledge at the edge.
//   - hold X (DiveKey / DiveKey2): dive down to explore the lake floor / underwater view.
//   - vanilla jumps are blocked while swimming (jumpCount = max), the jump-out replaces them.
//   - falling in: vertical speed x EntryDamp on entry, then drag, so you never hit the floor hard (the host also sets
//     IgnoreFallDamage on anyone below the surface).
//   - splash ring (newest FishFx<N>.Splash / FishFxClient.Splash) when ANY player drops through the surface.
// Breath bar (IMGUI, bubbles at the centre of the screen below the crosshair): fed by the host (Swim<N> sets hud* directly
// on the host; guests parse [FT] BREATH|name|fraction).
// Tuning: %TEMP%\ror2_swim.cfg (re-read within 1 s of saving; see ror2_swim.cfg.sample). Log: %TEMP%\ror2_swim.log.
// Threads: Install() only subscribes; every Unity object is touched from onUpdate / onFixedUpdate / OnGUI (main thread).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

public static class FishingSwimLook
{
    // ------------------------------------------------------------------ tunables (cfg names = field names)
    public static bool Swim = true;                 // swimming physics for the local body
    public static float SwimSpeed = 0.6f;           // horizontal speed multiplier in the water
    public static float FloatLevel = 1.1f;          // float with the surface at FloatLevel x (chest - feet) above the feet
    public static float BobAmp = 0.12f, BobPeriod = 2.4f;
    public static float BuoyK = 2.5f, MaxRise = 4f, MaxSink = 3f;   // buoyancy spring (1/s) and its speed limits (m/s)
    public static float WaterDrag = 3f;             // how fast vertical speed follows the target (1/s)
    public static float EntryDamp = 0.35f;          // falling speed kept when you hit the surface
    public static float SwimUp = 5f, DiveSpeed = 4.5f;
    public static float JumpOut = 0.85f;            // jump-out speed = JumpOut x jumpPower
    public static float HorizDecel = 25f;           // m/s^2 used to bring over-speed down to the swim speed
    public static int DiveKey = 0x58, DiveKey2 = 0;      // X (Ctrl is sprint on JV94's binding, C is a HUD slot) (Windows virtual-key codes; 0 = off)
    public static bool Splashes = true;
    public static float SplashMinSpeed = 2.5f, SplashSize = 1.0f;
    public static bool Hud = true;
    public static float HudY = 0.68f, HudScale = 1f;
    public static int Bubbles = 10;
    public static bool HudTest;                     // cfg HudTest=1: show the bar with a looping fake breath value
    // host-side breath (Swim<N> reads these; on guests they do nothing)
    public static float SurfaceSeconds = 60f;       // breath lasts this long floating at the surface / wading
    public static float UnderSeconds = 15f;         // ... and this long with the head under water
    public static float RefillSeconds = 4f;         // empty -> full on land
    public static float TickEvery = 1f, TickDamage = 0.1f;   // when empty: TickDamage x max HP every TickEvery s (non-lethal)
    public static float DrownAfter = 4f;            // empty this long -> drown (same death + chat line as before)
    public static float HeadLevel = 1.6f;           // head height = HeadLevel x (chest - feet) above the feet
    public static float BroadcastEvery = 0.5f;
    public static bool DryRun;                      // log "would take damage / would drown now" instead of hurting anyone
    public static bool NoFallDamage = true;

    // ------------------------------------------------------------------ state shared with the host add-on
    public static string Tag = "";
    public static bool hostMode;
    public static Func<bool> Enabled = () => true;
    public static Action<float> ServerFixed;        // host: breath + drowning, called every fixed step
    public static Func<Vector3, bool> InPool;       // host: small ponds never count as the lake
    public static bool cfgEnabled = true;
    // HUD (host: set by Swim<N>; guest: from [FT] BREATH)
    public static float hudBreath = 1f, hudSetAt = -100f;
    public static bool hudGhost, hudInWater;
    public static bool localInWater, localUnder, localSwimming;

    static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_swim.log");
    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + Tag + s + Environment.NewLine); } catch { } }
    static int errors;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    static bool installed;
    public static void Install(string tag, Func<bool> enabled, bool host)
    {
        Tag = tag; Enabled = enabled; hostMode = host;
        if (installed) { Log("Install() again"); return; }
        RoR2Application.onFixedUpdate += FixedTick;
        RoR2Application.onUpdate += FrameTick;
        if (!host) Chat.onChatChanged += OnChat;
        installed = true;
        Log("installed (" + (host ? "host" : "guest") + "), cfg " + CfgPath);
    }

    static bool Active()
    {
        if (!Enabled() || !cfgEnabled) return false;
        if (!hostMode && NetworkServer.active) return false;   // the host runs Swim<N> instead of the client copy
        return Run.instance != null;
    }

    // ------------------------------------------------------------------ the lake
    public static readonly string[] WaterNames = { "FishingWater", "FishingClientWater" };
    static GameObject water; static float nextFind;
    public static bool WaterY(out float y, out Vector3 center)
    {
        y = 0f; center = Vector3.zero;
        if (water == null && Time.unscaledTime >= nextFind)
        {
            nextFind = Time.unscaledTime + 1f;
            foreach (string n in hostMode ? new[] { WaterNames[0] } : WaterNames) { water = GameObject.Find(n); if (water != null) break; }
        }
        if (water == null || !water.activeInHierarchy) return false;
        center = water.transform.position; y = center.y;
        return true;
    }
    // inside the 2000 x 2000 m sheet and not in a small pond
    public static bool InLake(Vector3 p, out float wy)
    {
        Vector3 c;
        if (!WaterY(out wy, out c)) return false;
        if (Mathf.Abs(p.x - c.x) > 995f || Mathf.Abs(p.z - c.z) > 995f) return false;
        if (InPool != null) { try { if (InPool(p)) return false; } catch { } }
        return true;
    }

    // ------------------------------------------------------------------ local body physics (fixed step)
    static CharacterMotor curMotor;
    static bool swimming, penaltyOwned, wasIn;
    static float savedPenalty = 1f, jumpOutUntil, bodyH = -1f;
    static bool diveLatch, diveWasDown, chatTypingAtPress;

    static CharacterBody LocalBody()
    {
        LocalUser lu = LocalUserManager.GetFirstLocalUser();
        return lu == null ? null : lu.cachedBody;
    }
    public static string LocalName()
    {
        LocalUser lu = LocalUserManager.GetFirstLocalUser();
        return lu == null || lu.currentNetworkUser == null ? null : lu.currentNetworkUser.userName;
    }

    static void FixedTick()
    {
        float dt = Time.fixedDeltaTime;
        try
        {
            if (!Active()) { RestoreLocal(); localInWater = localUnder = false; if (hostMode && ServerFixed != null) ServerFixed(-1f); return; }
            LocalPhysics(dt);
        }
        catch (Exception e) { if (errors++ < 20) Log("physics error: " + e); }
        try { if (hostMode && ServerFixed != null) ServerFixed(dt); }
        catch (Exception e) { if (errors++ < 20) Log("server error: " + e); }
    }

    static void LocalPhysics(float dt)
    {
        CharacterBody b = LocalBody();
        CharacterMotor m = b == null ? null : b.characterMotor;
        if (m != curMotor) { RestoreLocal(); curMotor = m; bodyH = -1f; swimming = false; penaltyOwned = false; gravOwned = false; wasIn = false; }
        if (m == null || b.healthComponent == null || !b.healthComponent.alive || !m.hasEffectiveAuthority) { localInWater = localUnder = false; return; }
        float wy;
        Vector3 foot = b.footPosition;
        if (!Swim || !InLake(foot, out wy)) { Leave(m, false); RestorePenalty(m); localInWater = localUnder = wasIn = false; return; }

        float h0 = Mathf.Clamp(b.corePosition.y - foot.y, 0.4f, 4f);
        bodyH = bodyH < 0f ? h0 : Mathf.Min(bodyH, h0);   // core height wobbles with animations; use the standing value
        float h = bodyH;
        float depth = wy - foot.y;                 // > 0: feet below the surface
        float floatD = h * FloatLevel;
        bool inWater = depth > (wasIn ? -0.15f : 0.03f);
        if (inWater && !wasIn) Log("ENTER water (depth " + depth.ToString("0.00") + ", vy " + m.velocity.y.ToString("0.0") + ")");
        if (!inWater && wasIn) Log("EXIT water (depth " + depth.ToString("0.00") + ")");
        wasIn = inWater;
        localInWater = inWater;
        localUnder = inWater && foot.y + h * HeadLevel < wy;

        bool jumpingOut = Time.fixedTime < jumpOutUntil;
        bool swim = inWater && !jumpingOut && (!m.isGrounded || depth > floatD * 0.75f);
        if (!swim)
        {
            Leave(m, false);
            if (inWater && !jumpingOut) SetPenalty(m, Mathf.Lerp(1f, SwimSpeed, Mathf.Clamp01(depth / floatD)));   // wading
            else RestorePenalty(m);
            return;
        }

        Vector3 v = m.velocity;
        if (!swimming)
        {
            swimming = true;
            if (v.y < 0f) v.y *= EntryDamp;
            Log("swimming (entry vy " + m.velocity.y.ToString("0.0") + " -> " + v.y.ToString("0.0") + ", float depth " + floatD.ToString("0.00") + ")");
        }
        localSwimming = true;
        GravityOff(m, true);
        SetPenalty(m, SwimSpeed);
        m.jumpCount = Math.Max(m.jumpCount, b.maxJumpCount);   // no vanilla jumps in the water

        InputBankTest ib = b.inputBank;
        bool up = ib != null && ib.jump.down;
        bool dive = DiveHeld();
        float bob = BobAmp * Mathf.Sin(Time.fixedTime * 2f * Mathf.PI / Mathf.Max(0.2f, BobPeriod));
        float target;
        if (dive) target = -DiveSpeed;
        else if (up)
        {
            if (depth <= floatD + 0.25f)
            {   // at the surface: jump out (onto a ledge at the edge, or a hop in open water)
                v.y = Mathf.Max(b.jumpPower * JumpOut, 6f);
                m.velocity = v;
                m.Motor.ForceUnground(0.1f);
                jumpOutUntil = Time.fixedTime + 0.45f;
                Leave(m, true);
                RestorePenalty(m);
                Splash(foot, wy, SplashSize * 0.6f);
                Log("jump out (vy " + v.y.ToString("0.0") + ")");
                return;
            }
            target = SwimUp;
        }
        else target = Mathf.Clamp((depth - (floatD + bob)) * BuoyK, -MaxSink, MaxRise);

        v.y = Mathf.Lerp(v.y, target, 1f - Mathf.Exp(-WaterDrag * dt));
        if (target > 0.05f && m.isGrounded) m.Motor.ForceUnground(0.1f);   // standing on the lake floor -> float up

        float lim = Mathf.Max(1f, b.moveSpeed * SwimSpeed);
        Vector3 hv = new Vector3(v.x, 0f, v.z);
        float mag = hv.magnitude;
        if (mag > lim) { hv *= Mathf.MoveTowards(mag, lim, HorizDecel * dt) / mag; v.x = hv.x; v.z = hv.z; }
        m.velocity = v;
    }

    // gravity off the way RoR2's own anti-gravity zones do it: one more "environmental anti-gravity granter"
    static bool gravOwned;
    static void GravityOff(CharacterMotor m, bool off)
    {
        if (off == gravOwned || m == null) return;
        CharacterGravityParameters gp = m.gravityParameters;
        gp.environmentalAntiGravityGranterCount = Math.Max(0, gp.environmentalAntiGravityGranterCount + (off ? 1 : -1));
        m.gravityParameters = gp;
        gravOwned = off;
    }

    static void SetPenalty(CharacterMotor m, float k)
    {
        if (!penaltyOwned) { savedPenalty = m.walkSpeedPenaltyCoefficient; penaltyOwned = true; }
        m.walkSpeedPenaltyCoefficient = k;
    }
    static void RestorePenalty(CharacterMotor m)
    {
        if (!penaltyOwned) return;
        penaltyOwned = false;
        if (m != null) m.walkSpeedPenaltyCoefficient = savedPenalty;
    }
    static void Leave(CharacterMotor m, bool jumped)
    {
        localSwimming = false;
        if (!swimming) return;
        swimming = false;
        if (m == null) return;
        GravityOff(m, false);
        m.jumpCount = jumped ? 1 : Math.Min(m.jumpCount, 1);
        if (!jumped) Log("stopped swimming");
    }
    static void RestoreLocal()
    {
        CharacterMotor m = curMotor;
        if (m == null) { swimming = false; penaltyOwned = false; gravOwned = false; localSwimming = false; return; }
        Leave(m, false);
        RestorePenalty(m);
    }

    static bool DiveHeld()
    {
        uint fg; GetWindowThreadProcessId(GetForegroundWindow(), out fg);
        bool down = fg == myPid && ((DiveKey != 0 && (GetAsyncKeyState(DiveKey) & 0x8000) != 0) || (DiveKey2 != 0 && (GetAsyncKeyState(DiveKey2) & 0x8000) != 0));
        if (down && !diveWasDown) chatTypingAtPress = ChatTyping();   // typing a 'c' in chat is not a dive
        diveWasDown = down;
        diveLatch = down && !chatTypingAtPress;
        return diveLatch;
    }
    static bool ChatTyping()
    {
        try
        {
            foreach (RoR2.UI.ChatBox cb in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>())
            {
                if (cb == null) continue;
                Type t = cb.GetType();
                PropertyInfo p = t.GetProperty("showInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object o = p != null ? p.GetValue(cb, null) : null;
                if (o == null) { FieldInfo f = t.GetField("showInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (f != null) o = f.GetValue(cb); }
                if (o is bool && (bool)o) return true;
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ per frame: cfg, splashes, HUD object
    static GameObject hudGo;
    static readonly Dictionary<int, float> lastFoot = new Dictionary<int, float>();
    static string lastScene;
    static void FrameTick()
    {
        try
        {
            CfgTick();
            bool act = Active();
            if (!act)
            {
                if (hudGo != null) { UnityEngine.Object.Destroy(hudGo); hudGo = null; }
                if (!Enabled()) { lastFoot.Clear(); }
                return;
            }
            if (Hud && hudGo == null) { hudGo = new GameObject("FishingSwimHud" + Tag.Trim(' ', '[', ']')); UnityEngine.Object.DontDestroyOnLoad(hudGo); hudGo.AddComponent<FishingSwimGui>(); Log("HUD object created"); }
            if (!Hud && hudGo != null) { UnityEngine.Object.Destroy(hudGo); hudGo = null; }
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != lastScene) { lastScene = sc; lastFoot.Clear(); water = null; nextFind = 0f; }
            SplashTick();
        }
        catch (Exception e) { if (errors++ < 20) Log("frame error: " + e); }
    }

    static void SplashTick()
    {
        if (!Splashes) return;
        float dt = Mathf.Max(Time.deltaTime, 0.001f);
        foreach (PlayerCharacterMasterController pc in PlayerCharacterMasterController.instances)
        {
            CharacterBody b = pc == null || pc.master == null ? null : pc.master.GetBody();
            if (b == null) continue;
            int id = b.GetInstanceID();
            Vector3 f = b.footPosition;
            float wy, prev;
            bool had = lastFoot.TryGetValue(id, out prev);
            lastFoot[id] = f.y;
            if (!had || !InLake(f, out wy)) continue;
            if (prev >= wy && f.y < wy)
            {
                float vy = (prev - f.y) / dt;
                if (vy >= SplashMinSpeed) Splash(f, wy, SplashSize * Mathf.Clamp(0.7f + vy * 0.05f, 0.7f, 2.6f));
            }
        }
        if (lastFoot.Count > 64) lastFoot.Clear();
    }

    static MethodInfo splashM; static float splashLook = -100f;
    public static void Splash(Vector3 at, float wy, float size)
    {
        if (!Splashes || size <= 0f) return;
        try
        {
            if (splashM == null && Time.realtimeSinceStartup - splashLook > 10f)
            {
                splashLook = Time.realtimeSinceStartup;
                Type best = null; int bestN = -1;
                Type[] sig = { typeof(float), typeof(float), typeof(float), typeof(float) };
                const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                foreach (Assembly a in FishingScope.Assemblies())
                {
                    Type[] ts;
                    try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                    foreach (Type t in ts)
                    {
                        if (t == null || !t.Name.StartsWith("FishFx")) continue;
                        string rest = t.Name.Substring(6);
                        int n;
                        if (rest.StartsWith("Client")) { if (hostMode) continue; n = 1000; }
                        else if (!int.TryParse(rest, out n)) continue;
                        if (n > bestN && t.GetMethod("Splash", S, null, sig, null) != null) { best = t; bestN = n; }
                    }
                }
                if (best != null) { splashM = best.GetMethod("Splash", S, null, sig, null); Log("splash ring from " + best.FullName); }
                else Log("no FishFx*.Splash found (no splash rings)");
            }
            if (splashM != null) splashM.Invoke(null, new object[] { at.x, wy, at.z, size });
        }
        catch (Exception e) { if (errors++ < 20) Log("splash error: " + (e.InnerException ?? e).Message); splashM = null; }
    }

    // ------------------------------------------------------------------ guest: [FT] BREATH|name|fraction
    static string lastChat;
    static void OnChat()
    {
        try
        {
            if (!Enabled()) return;
            var log = Chat.readOnlyLog;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastChat)) return;
            lastChat = line;
            int k = line.IndexOf("[FT] BREATH|");
            if (k < 0) return;
            string s = line.Substring(k + 12);
            int lt = s.IndexOf('<'); if (lt >= 0) s = s.Substring(0, lt);
            string[] p = s.Trim().Split('|');
            if (p.Length < 2) return;
            float f;
            if (p[0] != LocalName() || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return;
            hudBreath = Mathf.Clamp01(f); hudSetAt = Time.unscaledTime;
            hudGhost = p.Length > 2 && p[2] == "G";
        }
        catch { }
    }

    // ------------------------------------------------------------------ HUD (bubbles)
    static Texture2D bubbleTex, ringTex, white;
    static GUIStyle label;
    static float hudAlpha;
    static Texture2D MakeBubble(bool ring)
    {
        int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = (i + 0.5f) / n * 2f - 1f, y = (j + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(x * x + y * y);
                Color c = new Color(0, 0, 0, 0);
                if (r < 1f)
                {
                    float edge = Mathf.Clamp01((1f - r) * 24f);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(r - 0.88f) * 9f);
                    if (ring) c = new Color(0.8f, 0.9f, 1f, rim * 0.55f * edge);
                    else
                    {
                        float hl = Mathf.Clamp01(1f - Mathf.Sqrt((x + 0.35f) * (x + 0.35f) + (y - 0.38f) * (y - 0.38f)) * 3.2f);
                        Color body = Color.Lerp(new Color(0.35f, 0.7f, 1f, 0.35f), new Color(0.55f, 0.85f, 1f, 0.75f), r);
                        c = Color.Lerp(body, new Color(0.9f, 0.97f, 1f, 1f), Mathf.Max(rim * 0.9f, hl));
                        c.a *= edge;
                    }
                }
                px[j * n + i] = c;
            }
        t.SetPixels(px); t.Apply(); return t;
    }

    public static void DrawHud()
    {
        if (!Hud || !Active()) return;
        float breath; bool inW, ghost;
        if (HudTest) { breath = Mathf.Repeat(-Time.unscaledTime / 12f, 1.2f); breath = Mathf.Clamp01(breath); inW = true; ghost = false; }
        else
        {
            breath = hudBreath; ghost = hudGhost;
            inW = hostMode ? hudInWater : localInWater;
            if (!hostMode && Time.unscaledTime - hudSetAt > 6f && !localInWater) breath = 1f;   // stale
        }
        bool show = inW || breath < 0.999f;
        hudAlpha = Mathf.MoveTowards(hudAlpha, show ? 1f : 0f, Time.unscaledDeltaTime * (show ? 4f : 1.2f));
        if (hudAlpha <= 0.001f) return;
        if (bubbleTex == null) { bubbleTex = MakeBubble(false); ringTex = MakeBubble(true); white = Texture2D.whiteTexture; }
        if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true, fontStyle = FontStyle.Bold };

        float s = Screen.height / 1080f * Mathf.Max(0.3f, HudScale);
        int n = Mathf.Clamp(Bubbles, 3, 20);
        float sz = 26f * s, gap = 5f * s;
        float W = n * sz + (n - 1) * gap;
        float x0 = (Screen.width - W) / 2f, y0 = Screen.height * HudY;
        Color old = GUI.color;
        bool empty = breath <= 0.001f && inW;
        GUI.color = new Color(0.02f, 0.06f, 0.12f, 0.45f * hudAlpha);
        GUI.DrawTexture(new Rect(x0 - 10f * s, y0 - 6f * s, W + 20f * s, sz + 12f * s), white);
        float units = breath * n;
        for (int i = 0; i < n; i++)
        {
            float fill = Mathf.Clamp01(units - i);
            Rect r = new Rect(x0 + i * (sz + gap), y0, sz, sz);
            GUI.color = new Color(1f, 1f, 1f, hudAlpha * (empty ? 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) : 1f));
            GUI.DrawTexture(r, ringTex);
            if (fill > 0f)
            {
                float k = 0.35f + 0.65f * fill;
                Rect rb = new Rect(r.center.x - sz * k / 2f, r.center.y - sz * k / 2f, sz * k, sz * k);
                GUI.color = ghost ? new Color(0.85f, 0.75f, 1f, hudAlpha * (0.5f + 0.5f * fill)) : new Color(1f, 1f, 1f, hudAlpha * (0.45f + 0.55f * fill));
                GUI.DrawTexture(rb, bubbleTex);
            }
        }
        label.fontSize = Mathf.Max(10, (int)(17f * s));
        string txt = empty ? "<color=#ff5555>DROWNING! Get out of the water!</color>"
                   : ghost ? "<color=#d8c8ff>Ghostly - no breath loss</color>"
                   : breath < 0.3f && inW ? "<color=#ffcc66>Low on air</color>" : "<color=#cfe8ff>Breath</color>";
        GUI.color = new Color(1f, 1f, 1f, hudAlpha);
        GUI.Label(new Rect(x0 - 100f * s, y0 + sz + 6f * s, W + 200f * s, 24f * s), txt, label);
        GUI.color = old;
    }

    // ------------------------------------------------------------------ cfg (%TEMP%\ror2_swim.cfg, live reload)
    public static readonly string CfgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_swim.cfg");
    static DateTime cfgStamp = DateTime.MinValue; static float nextCfg;
    static void CfgTick()
    {
        if (Time.unscaledTime < nextCfg) return;
        nextCfg = Time.unscaledTime + 1f;
        try
        {
            DateTime t = File.Exists(CfgPath) ? File.GetLastWriteTimeUtc(CfgPath) : DateTime.MinValue;
            if (t == cfgStamp) return;
            cfgStamp = t;
            if (t == DateTime.MinValue) { Log("cfg removed (values stay as they are)"); return; }
            var applied = new List<string>();
            foreach (string raw in File.ReadAllLines(CfgPath))
            {
                string line = raw; int h = line.IndexOf('#'); if (h >= 0) line = line.Substring(0, h);
                foreach (string tok in line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('='); if (eq <= 0) continue;
                    string k = tok.Substring(0, eq).Trim(), v = tok.Substring(eq + 1).Trim();
                    if (k.Equals("enabled", StringComparison.OrdinalIgnoreCase)) { cfgEnabled = v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase); applied.Add("enabled=" + cfgEnabled); continue; }
                    FieldInfo f = typeof(FishingSwimLook).GetField(k, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (f == null || f.IsInitOnly || f.IsLiteral) { applied.Add(k + "=?"); continue; }
                    try
                    {
                        if (f.FieldType == typeof(float)) f.SetValue(null, float.Parse(v, CultureInfo.InvariantCulture));
                        else if (f.FieldType == typeof(int)) f.SetValue(null, v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(v.Substring(2), 16) : int.Parse(v, CultureInfo.InvariantCulture));
                        else if (f.FieldType == typeof(bool)) f.SetValue(null, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else { applied.Add(k + "=?"); continue; }
                        applied.Add(f.Name + "=" + f.GetValue(null));
                    }
                    catch { applied.Add(k + "=bad"); }
                }
            }
            Log("cfg applied: " + string.Join(" ", applied.ToArray()));
        }
        catch (Exception e) { Log("cfg error: " + e.Message); }
    }
}

public class FishingSwimGui : MonoBehaviour
{
    void OnGUI()
    {
        try { FishingSwimLook.DrawHud(); } catch { }
    }
}
