// Host add-on Swim3 (JV94's game): swimming instead of instant drowning.
//  1. Turns off the fishing mod's instant drown WITHOUT editing it: every 0.1 s the newest enabled Fishing<N>'s private
//     drownGrace[name] is pushed to float.MaxValue for every player, so its DrownTick always skips them. Disabling this
//     add-on puts the grace back to "now + 5 s" (the old rule returns after 5 s).
//  2. Swimming physics for JV94's own body (FishingSwimLook, shared with the guest client).
//  3. Breath, decided here for EVERY player (server reads body positions, no client trust needed):
//     floating / wading drains over SurfaceSeconds (60), head under water over UnderSeconds (15), refills on land over
//     RefillSeconds (4). Empty -> TickDamage x max HP every TickEvery s (non-lethal), after DrownAfter s -> drown:
//     HealthComponent.Suicide + the SAME chat line as before ("<name> fell in the water and drowned! ...") so
//     DrownRespawn2's 10 s dry-land respawn, spectating and the Moon Eel revive all work unchanged.
//     Ghost Fish buff (Fishing<N>.BuffOn) = no breath loss. Small ponds (Fishing<N>.pools) never count.
//     [FT] BREATH|name|fraction[|G] goes out for the guests' bubble bar (only while it changes, <= 2/s per player).
//  4. Anyone below the surface gets IgnoreFallDamage (removed once they're back on dry land).
// DryRun=1 in %TEMP%\ror2_swim.cfg -> logs "would take damage" / "would drown now" instead of hurting anyone.
// Log %TEMP%\ror2_swim.log. Build: build.ps1 (agents\swim).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

public static class Swim3
{
    public static bool enabled = true;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static bool hooked, wasActive;
    static int errors;
    static void Log(string s) { FishingSwimLook.Log(s); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    public static void Run()
    {
        try
        {
            string me = typeof(Swim3).Name;
            foreach (var a in FishingScope.Assemblies())
                foreach (Type t in SafeTypes(a))
                {
                    if (!t.Name.StartsWith("Swim") || t.Name == me) continue;
                    int n; if (!int.TryParse(t.Name.Substring(4), out n)) continue;
                    FieldInfo f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) { f.SetValue(null, false); Log(t.Name + ".enabled = false"); }
                }
            if (hooked) { enabled = true; Log(me + ": Run() again: enabled"); return; }
            FishingSwimLook.ServerFixed = ServerFixed;
            FishingSwimLook.InPool = InPool;
            FishingSwimLook.Install("[" + me + "] ", () => enabled, true);
            hooked = true;
            Log(me + " installed");
        }
        catch (Exception e) { Log("Run error: " + e); }
    }

    // ------------------------------------------------------------------ the fishing mod (newest enabled Fishing<N>)
    static Type fish; static float nextFishScan = -100f;
    static IDictionary drownGrace; static FieldInfo nowField, poolsField; static MethodInfo buffOn, chatM;
    static void FindFishing()
    {
        if (Time.unscaledTime < nextFishScan && fish != null) return;
        nextFishScan = Time.unscaledTime + 2f;
        Type best = null; int bestN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                if (!t.Name.StartsWith("Fishing")) continue;
                int n; if (!int.TryParse(t.Name.Substring(7), out n) || n <= bestN) continue;
                FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (en == null || en.FieldType != typeof(bool) || !(bool)en.GetValue(null)) continue;
                best = t; bestN = n;
            }
        if (best == fish) return;
        fish = best;
        drownGrace = null; nowField = poolsField = null; buffOn = chatM = null;
        if (fish == null) { Log("no enabled Fishing<N> found"); return; }
        FieldInfo g = fish.GetField("drownGrace", All);
        drownGrace = g == null ? null : g.GetValue(null) as IDictionary;
        nowField = fish.GetField("now", All);
        poolsField = fish.GetField("pools", All);
        buffOn = fish.GetMethod("BuffOn", All, null, new[] { typeof(string), typeof(string) }, null);
        chatM = fish.GetMethod("Chat", All, null, new[] { typeof(string) }, null);
        Log("fishing mod: " + fish.Name + " (drownGrace " + (drownGrace != null) + ", now " + (nowField != null) + ", pools " + (poolsField != null)
            + ", BuffOn " + (buffOn != null) + ", Chat " + (chatM != null) + ")");
        if (drownGrace == null) Log("WARNING: " + fish.Name + " has no drownGrace - its instant drowning can't be turned off by this add-on");
    }
    static float FishNow() { try { return nowField == null ? 0f : (float)nowField.GetValue(null); } catch { return 0f; } }

    static float nextGrace;
    static void HoldInstantDrown(bool hold)
    {
        if (drownGrace == null) return;
        float v = hold ? float.MaxValue : FishNow() + 5f;
        foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList)
        {
            if (nu == null || string.IsNullOrEmpty(nu.userName)) continue;
            object cur = drownGrace.Contains(nu.userName) ? drownGrace[nu.userName] : null;
            if (hold && cur is float && (float)cur == float.MaxValue) continue;
            if (!hold && !(cur is float && (float)cur == float.MaxValue)) continue;
            drownGrace[nu.userName] = v;
        }
    }

    static bool Ghost(string name)
    {
        try { return buffOn != null && (bool)buffOn.Invoke(null, new object[] { name, "Ghost Fish" }); } catch { return false; }
    }

    static readonly List<Vector4> poolCache = new List<Vector4>(); static float nextPools;
    static bool InPool(Vector3 p)
    {
        if (Time.unscaledTime >= nextPools)
        {
            nextPools = Time.unscaledTime + 1f;
            poolCache.Clear();
            try
            {
                IList l = poolsField == null ? null : poolsField.GetValue(null) as IList;
                if (l != null)
                    foreach (object o in l)
                    {
                        Type t = o.GetType();
                        poolCache.Add(new Vector4((float)t.GetField("x").GetValue(o), (float)t.GetField("y").GetValue(o), (float)t.GetField("z").GetValue(o), (float)t.GetField("r").GetValue(o)));
                    }
            }
            catch { }
        }
        foreach (Vector4 q in poolCache)
        {
            float dx = p.x - q.x, dz = p.z - q.z;
            if (dx * dx + dz * dz <= (q.w + 1f) * (q.w + 1f) && Mathf.Abs(p.y - q.y) < 4f) return true;
        }
        return false;
    }

    static void Say(string text)
    {
        try { if (chatM != null) { chatM.Invoke(null, new object[] { text }); return; } } catch { }
        Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = "<color=#66ccff>" + text + "</color>" });
    }
    static void SayRaw(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }

    // ------------------------------------------------------------------ breath per player (server)
    class PS
    {
        public CharacterBody body;
        public float breath = 1f, empty, nextTick, lastSent = 1f, sentAt = -100f, outSince = -100f, h = -1f;
        public bool inW, under, flagged, dryDrownLogged, drowned, lastGhost;
    }
    static readonly Dictionary<string, PS> ps = new Dictionary<string, PS>();
    static string lastScene;

    static void ServerFixed(float dt)
    {
        bool active = dt > 0f && enabled && NetworkServer.active && RoR2.Run.instance != null;
        if (!active)
        {
            if (wasActive) Deactivate();
            return;
        }
        wasActive = true;
        FindFishing();
        if (Time.unscaledTime >= nextGrace) { nextGrace = Time.unscaledTime + 0.1f; HoldInstantDrown(true); }

        string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (sc != lastScene) { lastScene = sc; foreach (PS s in ps.Values) { RemoveFlag(s); } ps.Clear(); }

        float now = Time.time;
        string local = FishingSwimLook.LocalName();
        foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList)
        {
            if (nu == null || string.IsNullOrEmpty(nu.userName)) continue;
            string name = nu.userName;
            PS s;
            if (!ps.TryGetValue(name, out s)) ps[name] = s = new PS();
            CharacterBody b = nu.master == null ? null : nu.master.GetBody();
            if (b != s.body)
            {   // spawn / respawn / revive: fresh lungs
                RemoveFlag(s);
                s.body = b; s.breath = 1f; s.empty = 0f; s.h = -1f; s.outSince = -100f; s.drowned = false; s.dryDrownLogged = false; s.inW = s.under = false;
            }
            if (b == null || b.healthComponent == null || !b.healthComponent.alive) { s.inW = s.under = false; if (name == local) HostHud(s); continue; }

            Vector3 foot = b.footPosition;
            float wy;
            bool lake = FishingSwimLook.InLake(foot, out wy);
            float h0 = Mathf.Clamp(b.corePosition.y - foot.y, 0.4f, 4f);
            s.h = s.h < 0f ? h0 : Mathf.Min(s.h, h0);   // core height wobbles with animations; the standing value is the lowest
            float h = s.h;
            bool inW = lake && foot.y < wy - 0.05f;
            bool under = inW && foot.y + h * FishingSwimLook.HeadLevel < wy;
            bool ghost = Ghost(name);
            if (inW != s.inW) Log(name + (inW ? " ENTERED the water" : " left the water") + " (breath " + s.breath.ToString("0.00") + ")");
            if (inW) s.outSince = -100f; else if (s.inW || s.outSince < 0f) s.outSince = now;
            s.inW = inW; s.under = under;
            // refill only on land: grounded, or out of the water for 1.5 s (hopping out and back in does not refill)
            bool onLand = !inW && ((b.characterMotor != null && b.characterMotor.isGrounded) || now - s.outSince >= 1.5f);

            // no fall damage below the surface
            if (FishingSwimLook.NoFallDamage)
            {
                if (lake && foot.y < wy && !s.flagged && (b.bodyFlags & CharacterBody.BodyFlags.IgnoreFallDamage) == 0)
                { b.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage; s.flagged = true; }
                else if (s.flagged && (!lake || foot.y > wy + 4f || (foot.y > wy + 0.3f && b.characterMotor != null && b.characterMotor.isGrounded)))
                    RemoveFlag(s);
            }
            else RemoveFlag(s);

            if (onLand) s.breath = Mathf.Min(1f, s.breath + dt / Mathf.Max(0.1f, FishingSwimLook.RefillSeconds));
            else if (!ghost) s.breath = Mathf.Max(0f, s.breath - dt / Mathf.Max(0.5f, under ? FishingSwimLook.UnderSeconds : FishingSwimLook.SurfaceSeconds));

            if (s.breath <= 0f && inW && !ghost && !s.drowned)
            {
                if (s.empty == 0f) { s.nextTick = now; Log(name + " is OUT OF AIR"); }
                s.empty += dt;
                if (now >= s.nextTick) { s.nextTick = now + Mathf.Max(0.2f, FishingSwimLook.TickEvery); DamageTick(name, b); }
                if (s.empty >= FishingSwimLook.DrownAfter) Drown(name, b, s, foot.y, wy);
            }
            else { s.empty = 0f; if (s.breath > 0.05f) s.dryDrownLogged = false; }

            // [FT] BREATH for the guests' bubble bar
            if (name != local)
            {
                bool changed = Mathf.Abs(s.breath - s.lastSent) >= 0.04f || (s.breath >= 1f && s.lastSent < 1f) || (s.breath <= 0f && s.lastSent > 0f) || ghost != s.lastGhost;
                bool keepAlive = (inW || s.breath < 1f) && now - s.sentAt >= 3f;
                if ((changed || keepAlive) && now - s.sentAt >= FishingSwimLook.BroadcastEvery)
                {
                    SayRaw("[FT] BREATH|" + name + "|" + s.breath.ToString("0.00", CultureInfo.InvariantCulture) + (ghost ? "|G" : ""));
                    s.lastSent = s.breath; s.sentAt = now; s.lastGhost = ghost;
                }
            }
            else HostHud(s, ghost);
        }
    }

    static void HostHud(PS s, bool ghost = false)
    {
        FishingSwimLook.hudBreath = s.breath;
        FishingSwimLook.hudInWater = s.inW;
        FishingSwimLook.hudGhost = ghost && s.inW;
        FishingSwimLook.hudSetAt = Time.unscaledTime;
    }

    static void RemoveFlag(PS s)
    {
        if (!s.flagged) return;
        s.flagged = false;
        try { if (s.body != null) s.body.bodyFlags &= ~CharacterBody.BodyFlags.IgnoreFallDamage; } catch { }
    }

    static void DamageTick(string name, CharacterBody b)
    {
        HealthComponent hc = b.healthComponent;
        float dmg = hc.fullCombinedHealth * Mathf.Clamp(FishingSwimLook.TickDamage, 0f, 1f);
        if (FishingSwimLook.DryRun) { Log("DRY-RUN " + name + ": would take a drowning tick of " + dmg.ToString("0.0") + " (non-lethal)"); return; }
        if (dmg <= 0f) return;
        var di = new DamageInfo
        {
            damage = dmg, attacker = null, inflictor = null, position = b.corePosition, procCoefficient = 0f, crit = false, force = Vector3.zero,
            damageType = (DamageTypeCombo)(DamageType.BypassArmor | DamageType.BypassBlock | DamageType.NonLethal),
        };
        hc.TakeDamage(di);
    }

    static void Drown(string name, CharacterBody b, PS s, float footY, float wy)
    {
        if (FishingSwimLook.DryRun)
        {
            if (!s.dryDrownLogged) { s.dryDrownLogged = true; Log("DRY-RUN " + name + ": would drown now (foot Y " + footY.ToString("0.00") + ", water Y " + wy.ToString("0.00") + ")"); }
            return;
        }
        s.drowned = true;
        b.healthComponent.Suicide(null, null, (DamageTypeCombo)DamageType.Generic);
        Say(name + " fell in the water and drowned! (spectating until the next stage)");
        Log("DROWNED " + name + " (out of air): foot Y=" + footY.ToString("0.00") + " water Y=" + wy.ToString("0.00"));
    }

    static void Deactivate()
    {
        wasActive = false;
        try { FindFishing(); if (!enabled) { HoldInstantDrown(false); Log("disabled: fishing mod's instant drowning restored (5 s grace)"); } } catch { }
        foreach (PS s in ps.Values) RemoveFlag(s);
        ps.Clear();
        FishingSwimLook.hudBreath = 1f; FishingSwimLook.hudInWater = false;
    }
}

