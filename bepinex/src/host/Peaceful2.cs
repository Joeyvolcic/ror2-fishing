// Peaceful mode (host only). Removes enemies and keeps them gone until the game closes.
//  - Global spawn switch: CombatDirector.cvDirectorCombatDisable (ConVar "director_combat_disable") = true.
//    CombatDirector.FixedUpdate returns early on it, and SceneDirector.PopulateScene zeroes the
//    stage-start monster credit on it, so it covers continuous spawns, stage-start spawns, the teleporter
//    boss/bonus directors and shrine-of-combat directors. The teleporter's boss squad times out empty
//    (CombatSquadDefeatIfEmptyByTimeoutServer), so the holdout zone still completes.
//  - Every ~2 s: re-assert the ConVar, disable any enabled CombatDirector (CombatDirector.instancesList
//    only holds enabled ones), and kill every alive body whose team is Monster/Lunar/Void/EndOfTheCycle
//    via HealthComponent.Suicide (fallback: NetworkServer.Destroy). Neutral and Player teams are untouched.
//  - Turning off: set Peaceful2.enabled = false. The next tick restores the ConVar to its old value and
//    re-enables the directors this class disabled (except the teleporter's own boss/bonus directors).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class Peaceful2
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_peaceful.log");
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static bool enabled = true;
    public static bool restoreOnDisable = true;
    static bool restored, announced;
    static Assembly ror2, core;
    static Type netServer, tBody, tDirector, tTele, tUObject;
    static object cvar;
    static bool cvarOld;
    static readonly List<object> disabledDirectors = new List<object>();
    static readonly Dictionary<object, int> killAttempts = new Dictionary<object, int>();
    static int tick, errors, totalKilled, totalDirectors;
    static string lastScene = "", lastTele = "";

    static void Log(string s) { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[Peaceful2] " + s + Environment.NewLine); }

    static object Get(object o, string name)
    {
        if (o == null) return null;
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null) return p.GetValue(o, null);
        FieldInfo f = o.GetType().GetField(name, All);
        return f != null ? f.GetValue(o) : null;
    }

    static void Set(object o, string name, object v)
    {
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null && p.CanWrite) { p.SetValue(o, v, null); return; }
        o.GetType().GetField(name, All).SetValue(o, v);
    }

    static bool IsNull(object o)
    {
        return o == null || (bool)tUObject.GetMethod("op_Equality").Invoke(null, new object[] { o, null });
    }

    static string NameOf(object o) { return IsNull(o) ? "null" : Convert.ToString(Get(o, "name")); }

    static void Chat(string text)
    {
        try
        {
            Type chat = ror2.GetType("RoR2.Chat");
            object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
            msg.GetType().GetField("baseToken", All).SetValue(msg, "<color=#7fd8ff>" + text + "</color>");
            foreach (MethodInfo m in chat.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
        }
        catch (Exception e) { Log("chat error: " + e.Message); }
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetName().Name == "RoR2") ror2 = a;
            if (a.GetName().Name == "UnityEngine.CoreModule") core = a;
            Type t = a.GetType("UnityEngine.Networking.NetworkServer");
            if (t != null) netServer = t;
        }
        // Retire older Peaceful versions without letting them restore spawning.
        foreach (var a in FishingScope.Assemblies())
            if (a.GetName().Name.StartsWith("Peaceful"))
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("Peaceful") && t != typeof(Peaceful2) && t.GetField("enabled", All) != null)
                {
                    FieldInfo r = t.GetField("restoreOnDisable", All);
                    if (r != null) r.SetValue(null, false);
                    t.GetField("enabled", All).SetValue(null, false);
                    Log("retired " + t.Name);
                }
        tBody = ror2.GetType("RoR2.CharacterBody");
        tDirector = ror2.GetType("RoR2.CombatDirector");
        tTele = ror2.GetType("RoR2.TeleporterInteraction");
        tUObject = core.GetType("UnityEngine.Object");
        cvar = tDirector.GetField("cvDirectorCombatDisable", All).GetValue(null);
        cvarOld = (bool)Get(cvar, "value");
        Action h = Tick;
        ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All).AddEventHandler(null, h);
        Log("installed; director_combat_disable was " + cvarOld + "; watcher on RoR2Application.onFixedUpdate every 100 ticks (~2 s)");
    }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); } catch { return new Type[0]; }
    }

    static void Tick()
    {
        try
        {
            if (!enabled) { if (!restored) Restore(); return; }
            if (tick++ % 100 != 0) return;
            if (!(bool)netServer.GetProperty("active").GetValue(null, null)) return;
            object run = ror2.GetType("RoR2.Run").GetProperty("instance", All).GetValue(null, null);
            if (IsNull(run)) return;
            Sweep();
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + (e.InnerException ?? e)); }
    }

    static void Sweep()
    {
        // Scene change logging
        Type sm = core.GetType("UnityEngine.SceneManagement.SceneManager");
        object scene = sm.GetMethod("GetActiveScene").Invoke(null, null);
        string sceneName = Convert.ToString(Get(scene, "name"));
        if (sceneName != lastScene) { Log("scene: " + sceneName); lastScene = sceneName; killAttempts.Clear(); }

        // 1. Global spawn switch
        if (!(bool)Get(cvar, "value")) { Set(cvar, "value", true); Log("director_combat_disable set to True"); }

        // 2. Directors
        IList dirs = (IList)tDirector.GetField("instancesList", All).GetValue(null);
        object[] dcopy = new object[dirs.Count];
        dirs.CopyTo(dcopy, 0);
        foreach (object d in dcopy)
        {
            if (IsNull(d) || !(bool)Get(d, "enabled")) continue;
            Set(d, "enabled", false);
            disabledDirectors.Add(d);
            totalDirectors++;
            Log("disabled CombatDirector '" + NameOf(d) + "' (total " + totalDirectors + ")");
        }

        // 3. Monster-team bodies
        IList bodies = (IList)tBody.GetField("instancesList", All).GetValue(null);
        object[] bcopy = new object[bodies.Count];
        bodies.CopyTo(bcopy, 0);
        int killed = 0;
        var names = new List<string>();
        foreach (object b in bcopy)
        {
            if (IsNull(b)) continue;
            object tc = Get(b, "teamComponent");
            if (IsNull(tc)) continue;
            int team = Convert.ToInt32(Get(tc, "teamIndex"));
            if (team < 2) continue; // -1 None, 0 Neutral, 1 Player
            object hc = Get(b, "healthComponent");
            if (IsNull(hc) || !(bool)Get(hc, "alive")) continue;
            int tries; killAttempts.TryGetValue(b, out tries); killAttempts[b] = tries + 1;
            string how;
            if (tries < 2)
            {
                MethodInfo sui = null;
                foreach (MethodInfo m in hc.GetType().GetMethods(All)) if (m.Name == "Suicide" && m.GetParameters().Length == 3) sui = m;
                ParameterInfo[] ps = sui.GetParameters();
                object dt = ps[2].ParameterType.IsValueType ? Activator.CreateInstance(ps[2].ParameterType) : null;
                sui.Invoke(hc, new object[] { null, null, dt });
                how = "Suicide";
            }
            else
            {
                netServer.GetMethod("Destroy", new[] { core.GetType("UnityEngine.GameObject") }).Invoke(null, new object[] { Get(b, "gameObject") });
                how = "NetworkServer.Destroy";
            }
            killed++;
            names.Add(NameOf(b) + "/" + Enum.ToObject(ror2.GetType("RoR2.TeamIndex"), team) + "/" + how);
        }
        if (killed > 0)
        {
            totalKilled += killed;
            Log("removed " + killed + " monster bodies (total " + totalKilled + "): " + string.Join(", ", names.ToArray()));
        }
        if (killAttempts.Count > 500) killAttempts.Clear();

        // 4. Teleporter state (verification only, read-only)
        object tele = tTele.GetProperty("instance", All).GetValue(null, null);
        string ts = IsNull(tele) ? "none" : "charging=" + Get(tele, "isCharging") + " pct=" + Get(tele, "chargePercent") + " charged=" + Get(tele, "isCharged") + " monstersCleared=" + Get(tele, "monstersCleared") + " bossGroupEnabled=" + Get(Get(tele, "bossGroup"), "enabled");
        if (ts != lastTele) { Log("teleporter: " + ts); lastTele = ts; }

        if (!announced) { announced = true; Chat("Peaceful mode: enemies removed. Time to fish!"); Log("announced in chat"); }
    }

    static void Restore()
    {
        restored = true;
        if (!restoreOnDisable) { Log("disabled (replaced; no restore)"); return; }
        try
        {
            Set(cvar, "value", cvarOld);
            int n = 0;
            foreach (object d in disabledDirectors)
            {
                if (IsNull(d)) continue;
                object go = Get(d, "gameObject");
                object teleComp = go.GetType().GetMethod("GetComponent", new[] { typeof(Type) }).Invoke(go, new object[] { tTele });
                if (!IsNull(teleComp)) continue; // teleporter boss/bonus directors are managed by the teleporter state machine
                Set(d, "enabled", true);
                n++;
            }
            Log("peaceful mode OFF: director_combat_disable restored to " + cvarOld + ", re-enabled " + n + " directors");
            Chat("Peaceful mode off: monsters are back.");
        }
        catch (Exception e) { Log("restore error: " + (e.InnerException ?? e)); }
    }
}

