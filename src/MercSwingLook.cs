// MercSwingLook: Mercenary-style rod whip (motion + Merc's own slash / impact VFX + sounds). Self-contained:
// needs only UnityEngine.CoreModule at compile time (RoR2 / Addressables are reached by reflection), so it can be
// dropped into the host injector add-on (WhipAnim5) or the guest client (2.1) unchanged.
//
// WIRING (client 2.1):
//   1. When a player whips (local right click, "[FT] WHIP" signal, or "[FT] WHIP|attacker|target" line), call
//        MercSwingLook.Begin(key)                      // key = player name; returns false if already swinging
//   2. Every frame AFTER the rod (pole Transform) has been placed for that player, call
//        MercSwingLook.Apply(key, pole, bodyTf, aimDir, isLocalPlayer, bodyGo)
//      pole   = the rod cylinder (centre +/- pole.up * localScale.y, as the fishing mod draws it)
//      bodyTf = the player's CharacterBody transform, aimDir = InputBankTest.aimDirection (or bodyTf.forward)
//      bodyGo = the body GameObject (sound emitter; may be null). Returns false once the swing is over.
//      It rotates the pole about its base (wind up right, ~180 degree slash to the left, follow-through, settle,
//      0.55 s), lunges it forward slightly, and spawns Merc's MercSwordSlash effect at the start of the slash.
//   3. On a confirmed hit, call MercSwingLook.HitSpark(targetChestPosition, attackerPosition)
//      (Merc's OmniImpactVFXSlashMerc + Play_merc_sword_impact, local only). Calling it ~0.1 s after Begin
//      lines it up with the slash.
//   3b. Bobber: after placing/swinging the rod, call MercSwingLook.FollowTip(key, bobTf, line, origTip, newTip)
//      every frame (origTip = the tip before your rescale/swing, newTip = after); it keeps a dangling bobber on the
//      new tip with a springy lag and updates both line ends.
//   4. Unity main thread only. If Merc's prefab can't be loaded a procedural crescent slash is used instead;
//      call MercSwingLook.TickFallback() once per frame to animate/fade those.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class FishingMercSwingLook
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // Timing (seconds) â€” Merc's GroundLight feel: snap wind-up, very fast slash, follow-through, ease back.
    public static float WindTime = 0.09f, SlashTime = 0.13f, FollowTime = 0.10f, SettleTime = 0.23f;
    public static float WindDeg = 75f, SlashDeg = 105f, OvershootDeg = 12f;   // +right / -left about body up
    public static float LungeDist = 0.35f;
    // Slash VFX placement (tunable live).
    // MercSwordSwipe1Mesh is a ~1 m-radius crescent in the prefab's local XY plane, hanging below the pivot (size 3 => ~3 m);
    // pitching it -90 lays it flat in front of the player. Tilt rolls it about the aim (Merc's slashes are slightly diagonal).
    public static float SlashScale = 1.0f, SlashPitch = -90f, SlashTilt = -12f, SlashRoll = 0f, SlashYaw = 0f, SlashHeight = 0.1f, SlashForward = 0.0f;
    public static bool SlashMirror = false, UseMercVfx = true, PlaySounds = true;
    public static float VfxSpeed = 1f;            // test knob: particle simulation speed of the spawned slash (1 = normal)
    public static GameObject LastSlash;           // last spawned slash (debug)

    public static float Duration { get { return WindTime + SlashTime + FollowTime + SettleTime; } }

    static readonly Dictionary<string, float> start = new Dictionary<string, float>();
    static readonly Dictionary<string, bool> slashed = new Dictionary<string, bool>();
    static GameObject slashPrefab, hitPrefab; static bool loaded;
    static MethodInfo playSound, spawnEffect; static Type effectDataType;
    public static string Status = "";

    public static bool Begin(string key)
    {
        float s;
        if (start.TryGetValue(key, out s) && Time.time - s < Duration) return false;
        start[key] = Time.time; slashed[key] = false;
        return true;
    }

    public static bool IsSwinging(string key) { float s; return start.TryGetValue(key, out s) && Time.time - s < Duration; }

    // Angle (degrees, + = to the player's right) at time e seconds into the swing.
    public static float Angle(float e)
    {
        if (e < WindTime) return WindDeg * OutQuad(e / WindTime);
        e -= WindTime;
        if (e < SlashTime) return Mathf.Lerp(WindDeg, -SlashDeg, OutCubic(e / SlashTime));
        e -= SlashTime;
        if (e < FollowTime) return Mathf.Lerp(-SlashDeg, -SlashDeg - OvershootDeg, OutQuad(e / FollowTime));
        e -= FollowTime;
        return Mathf.Lerp(-SlashDeg - OvershootDeg, 0f, InOut(Mathf.Clamp01(e / SettleTime)));
    }

    // Forward lunge of the rod (metres) â€” peaks right after the slash.
    public static float Lunge(float e)
    {
        float a = WindTime, b = WindTime + SlashTime + FollowTime, d = Duration;
        if (e < a) return -0.1f * LungeDist * OutQuad(e / a);
        if (e < b) return Mathf.Lerp(-0.1f * LungeDist, LungeDist, OutCubic((e - a) / (b - a)));
        return Mathf.Lerp(LungeDist, 0f, InOut((e - b) / (d - b)));
    }

    public static bool Apply(string key, Transform pole, Transform bodyTf, Vector3 aimDir, bool local, GameObject soundGo)
    {
        float s;
        if (!start.TryGetValue(key, out s)) return false;
        float e = Time.time - s;
        if (e >= Duration) { start.Remove(key); slashed.Remove(key); return false; }
        if (pole == null || bodyTf == null) return true;
        Vector3 up = bodyTf.up;
        Vector3 fwd = Vector3.ProjectOnPlane(aimDir, up); if (fwd.sqrMagnitude < 1e-4f) fwd = bodyTf.forward; fwd.Normalize();
        Vector3 basePoint = pole.position - pole.up * pole.localScale.y;
        pole.RotateAround(basePoint, up, Angle(e));
        pole.position += fwd * Lunge(e);
        if (e >= WindTime && !slashed[key])
        {
            slashed[key] = true;
            SpawnSlash(basePoint + fwd * SlashForward + up * SlashHeight, fwd, up, pole.localScale.y * 2f, local);
            if (PlaySounds && soundGo != null) PlaySound("Play_merc_sword_swing", soundGo);
        }
        return true;
    }

    // ---- Dangling bobber that follows the (enlarged / swinging) rod tip on a short spring ----
    // origTip = where the fishing mod put the tip this frame (before any rescale/rotation), newTip = the tip after.
    // If the bobber hangs within 1.5 m of origTip it is dangling: keep its offset and spring it to newTip + offset
    // (whips around during the slash). Otherwise (cast out / flying) only the line start moves. Sets both line ends.
    class Bob { public Vector3 pos, vel; }
    static readonly Dictionary<string, Bob> bobs = new Dictionary<string, Bob>();
    public static float BobSpring = 260f, BobDamping = 14f;
    public static void FollowTip(string key, Transform bobTf, LineRenderer line, Vector3 origTip, Vector3 newTip)
    {
        if (line != null) line.SetPosition(0, newTip);
        if (bobTf == null) return;
        Vector3 off = bobTf.position - origTip;
        if (off.magnitude > 1.5f) { bobs.Remove(key); return; }
        Vector3 target = newTip + off;
        Bob b;
        if (!bobs.TryGetValue(key, out b) || (b.pos - target).magnitude > 4f) { b = new Bob { pos = target }; bobs[key] = b; }
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        b.vel += ((target - b.pos) * BobSpring - b.vel * BobDamping) * dt;
        b.pos += b.vel * dt;
        bobTf.position = b.pos;
        if (line != null) line.SetPosition(1, b.pos);
    }

    // ---- Merc assets ----
    static void Load()
    {
        if (loaded) return; loaded = true;
        slashPrefab = LoadAddressable("RoR2/Base/Merc/MercSwordSlash.prefab");
        hitPrefab = LoadAddressable("RoR2/Base/Merc/OmniImpactVFXSlashMerc.prefab");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type u = a.GetType("RoR2.Util");
            if (u != null) playSound = u.GetMethod("PlaySound", All, null, new[] { typeof(string), typeof(GameObject) }, null);
            Type em = a.GetType("RoR2.EffectManager");
            if (em != null)
            {
                effectDataType = a.GetType("RoR2.EffectData");
                foreach (var m in em.GetMethods(All))
                {
                    var p = m.GetParameters();
                    if (m.Name == "SpawnEffect" && p.Length == 3 && p[0].ParameterType == typeof(GameObject)) spawnEffect = m;
                }
            }
        }
        Status = "slash=" + (slashPrefab != null) + " hit=" + (hitPrefab != null) + " sound=" + (playSound != null) + " spawnEffect=" + (spawnEffect != null);
    }

    static GameObject LoadAddressable(string key)
    {
        try
        {
            Type ad = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { ad = a.GetType("UnityEngine.AddressableAssets.Addressables"); if (ad != null) break; }
            foreach (var m in ad.GetMethods(All))
                if (m.Name == "LoadAssetAsync" && m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object))
                {
                    object h = m.MakeGenericMethod(typeof(GameObject)).Invoke(null, new object[] { key });
                    return h.GetType().GetMethod("WaitForCompletion").Invoke(h, null) as GameObject;
                }
        }
        catch (Exception e) { Status += " load " + key + " failed: " + (e.InnerException ?? e).Message; }
        return null;
    }

    public static void PlaySound(string evt, GameObject go)
    {
        Load();
        try { if (playSound != null && go != null) playSound.Invoke(null, new object[] { evt, go }); } catch { }
    }

    // Merc's swing effect, horizontal across the player's front, sweeping right -> left.
    public static void SpawnSlash(Vector3 pivot, Vector3 fwd, Vector3 up, float rodLength, bool local)
    {
        Load();
        Quaternion rot = Quaternion.AngleAxis(SlashYaw, up) * Quaternion.LookRotation(fwd, up) * Quaternion.Euler(0f, 0f, SlashTilt) * Quaternion.Euler(SlashPitch, 0f, 0f) * Quaternion.Euler(0f, 0f, SlashRoll);
        if (UseMercVfx && slashPrefab != null)
        {
            GameObject go = UnityEngine.Object.Instantiate(slashPrefab, pivot, rot);
            float k = SlashScale * Mathf.Max(0.5f, rodLength / 2.8f);
            go.transform.localScale = new Vector3(SlashMirror ? -k : k, k, k);
            if (!local) { Component sh = go.GetComponent("ShakeEmitter"); if (sh != null) UnityEngine.Object.Destroy(sh); }
            if (go.GetComponent("DestroyOnTimer") == null || VfxSpeed != 1f) UnityEngine.Object.Destroy(go, 1.5f / Mathf.Max(0.05f, VfxSpeed));
            if (VfxSpeed != 1f) SetSimSpeed(go, VfxSpeed);
            LastSlash = go;
            return;
        }
        Crescent(pivot, fwd, up, rodLength);
    }

    static void SetSimSpeed(GameObject go, float k)
    {
        try
        {
            foreach (Component c in go.GetComponentsInChildren<Component>(true))
            {
                if (c.GetType().Name != "ParticleSystem") continue;
                PropertyInfo mp = c.GetType().GetProperty("main"); object main = mp.GetValue(c, null);
                main.GetType().GetProperty("simulationSpeed").SetValue(main, k, null);
            }
            Component dt = go.GetComponent("DestroyOnTimer"); if (dt != null) UnityEngine.Object.Destroy(dt);
        }
        catch (Exception e) { Status += " simspeed err " + e.Message; }
    }

    public static void HitSpark(Vector3 at, Vector3 from)
    {
        Load();
        Vector3 dir = at - from; if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
        if (hitPrefab != null && spawnEffect != null)
        {
            try
            {
                object d = Activator.CreateInstance(effectDataType);
                Set(d, "origin", at); Set(d, "rotation", Quaternion.LookRotation(dir.normalized)); Set(d, "scale", 1.5f);
                spawnEffect.Invoke(null, new object[] { hitPrefab, d, false });
            }
            catch (Exception e) { Status += " hit err " + (e.InnerException ?? e).Message; }
        }
        else Crescent(at - dir.normalized * 0.8f, dir.normalized, Vector3.up, 1.6f);
        GameObject snd = new GameObject("MercHitSound"); snd.transform.position = at;
        PlaySound("Play_merc_sword_impact", snd); UnityEngine.Object.Destroy(snd, 2f);
    }

    static void Set(object o, string n, object v)
    {
        var f = o.GetType().GetField(n, All); if (f != null) { f.SetValue(o, v); return; }
        var p = o.GetType().GetProperty(n, All); if (p != null) p.SetValue(o, v, null);
    }

    // ---- Procedural fallback: a crescent arc mesh (white -> light blue) that sweeps right->left and fades ----
    class Arc { public Mesh mesh; public GameObject go; public float t0; public float radius; }
    static readonly List<Arc> arcs = new List<Arc>();
    static Material arcMat;
    const int Seg = 32; const float ArcDeg = 180f, ArcLife = 0.3f;

    static void Crescent(Vector3 pivot, Vector3 fwd, Vector3 up, float radius)
    {
        if (arcMat == null)
        {
            Shader sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
            arcMat = new Material(sh); arcMat.renderQueue = 3100;
        }
        var go = new GameObject("MercCrescent");
        go.transform.position = pivot; go.transform.rotation = Quaternion.LookRotation(fwd, up);
        var mesh = new Mesh(); mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = arcMat;
        arcs.Add(new Arc { mesh = mesh, go = go, t0 = Time.time, radius = radius });
    }

    public static void TickFallback()
    {
        for (int i = arcs.Count - 1; i >= 0; i--)
        {
            Arc a = arcs[i];
            float t = (Time.time - a.t0) / ArcLife;
            if (a.go == null || t >= 1f) { if (a.go != null) UnityEngine.Object.Destroy(a.go); arcs.RemoveAt(i); continue; }
            float head = OutCubic(Mathf.Clamp01(t * 2.2f));                // leading edge sweeps right -> left
            float fade = 1f - Mathf.Clamp01((t - 0.35f) / 0.65f);
            var v = new Vector3[(Seg + 1) * 2]; var c = new Color[v.Length]; var tri = new int[Seg * 6];
            for (int s = 0; s <= Seg; s++)
            {
                float u = s / (float)Seg;                                    // 0 = right end, 1 = left end
                float ang = Mathf.Deg2Rad * (ArcDeg * 0.5f - ArcDeg * u * head);
                float width = 0.45f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u)) * a.radius * 0.25f + 0.02f;
                Vector3 dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                v[s * 2] = dir * (a.radius - width); v[s * 2 + 1] = dir * (a.radius + width * 0.3f);
                float alpha = fade * Mathf.Pow(u, 0.6f);                     // brightest at the leading edge
                Color col = Color.Lerp(new Color(0.55f, 0.85f, 1f), Color.white, u);
                c[s * 2] = new Color(col.r, col.g, col.b, alpha * 0.35f); c[s * 2 + 1] = new Color(col.r, col.g, col.b, alpha);
                if (s < Seg) { int b = s * 6, q = s * 2; tri[b] = q; tri[b + 1] = q + 1; tri[b + 2] = q + 2; tri[b + 3] = q + 1; tri[b + 4] = q + 3; tri[b + 5] = q + 2; }
            }
            a.mesh.Clear(); a.mesh.vertices = v; a.mesh.colors = c; a.mesh.triangles = tri;
            var tri2 = new int[tri.Length]; // double-sided
            a.mesh.subMeshCount = 2; a.mesh.SetTriangles(tri, 0);
            for (int j = 0; j < tri.Length; j += 3) { tri2[j] = tri[j]; tri2[j + 1] = tri[j + 2]; tri2[j + 2] = tri[j + 1]; }
            a.mesh.SetTriangles(tri2, 1);
            var mr = a.go.GetComponent<MeshRenderer>(); if (mr.sharedMaterials.Length < 2) mr.sharedMaterials = new[] { arcMat, arcMat };
        }
    }

    static float OutQuad(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x); }
    static float OutCubic(float x) { x = Mathf.Clamp01(x); float y = 1f - x; return 1f - y * y * y; }
    static float InOut(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
}
