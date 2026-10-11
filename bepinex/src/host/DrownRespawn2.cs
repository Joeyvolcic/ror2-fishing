// Injected into JV94's (host) game. Players who drown (the fishing mod's "fell in the water and drowned"
// chat line, or dying while below the lake surface) are respawned after 10 seconds on dry land: at the last
// spot they were standing above the water, or next to a living teammate if that isn't known.
// Other deaths are untouched (death = spectate, as before).
using System;
using System.Collections.Generic;
using System.Reflection;
using RoR2;
using UnityEngine;

public static class DrownRespawn2
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const float RespawnDelay = 10f;
    public static bool enabled = true;
    static int seenLog, ticks;
    static readonly Dictionary<string, Vector3> lastDry = new Dictionary<string, Vector3>();
    static readonly Dictionary<string, float> pending = new Dictionary<string, float>();   // name -> time to respawn
    static string lastScene = "";

    static void Log(string s) { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_stuckfix.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("DrownRespawn") && t.Name != typeof(DrownRespawn2).Name)
                {
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null) f.SetValue(null, false);
                }
        seenLog = Chat.readOnlyLog.Count;
        RoR2Application.onFixedUpdate += Tick;
        Log("drown respawn installed (" + RespawnDelay + " s)");
    }

    static float WaterY()
    {
        GameObject w = GameObject.Find("FishingWater");
        return w == null ? float.NaN : w.transform.position.y;
    }

    static void Tick()
    {
        if (!enabled || !UnityEngine.Networking.NetworkServer.active || RoR2.Run.instance == null) return;
        try
        {
            float wy = WaterY();

            // New stage: forget dry spots from the previous map (they'd respawn people in mid-air or inside
            // walls) and any respawns that were pending when the map changed.
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != lastScene) { lastDry.Clear(); pending.Clear(); lastScene = scene; }

            // Remember where each living player last stood on dry land (twice a second).
            if (++ticks % 25 == 0 && !float.IsNaN(wy))
                foreach (var nu in NetworkUser.readOnlyInstancesList)
                {
                    CharacterBody b = nu.master == null ? null : nu.master.GetBody();
                    if (b == null || !b.healthComponent.alive) continue;
                    var motor = b.characterMotor;
                    if (b.footPosition.y > wy + 0.5f && (motor == null || motor.isGrounded)) lastDry[nu.userName] = b.footPosition;
                }

            // Drowning announcements from the fishing mod.
            var log = Chat.readOnlyLog;
            if (log.Count < seenLog) seenLog = 0;
            for (int i = seenLog; i < log.Count; i++)
            {
                string line = log[i];
                int k = line.IndexOf(" fell in the water and drowned");
                if (k < 0) continue;
                string name = StripTags(line.Substring(0, k));
                if (!pending.ContainsKey(name))
                {
                    pending[name] = Time.time + RespawnDelay;
                    Say("<color=#66ccff>" + name + " will respawn on dry land in " + (int)RespawnDelay + " seconds.</color>");
                    Log("drown respawn: " + name + " scheduled");
                }
            }
            seenLog = log.Count;

            foreach (string name in new List<string>(pending.Keys))
            {
                if (Time.time < pending[name]) continue;
                pending.Remove(name);
                Respawn(name);
            }
        }
        catch (Exception e) { if (ticks % 500 == 0) Log("drown respawn error: " + e.Message); }
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

    static void Respawn(string name)
    {
        CharacterMaster master = null;
        Vector3? spot = null;
        foreach (var nu in NetworkUser.readOnlyInstancesList)
        {
            if (nu.userName == name) master = nu.master;
            else if (spot == null && nu.master != null && nu.master.GetBody() != null && nu.master.GetBody().healthComponent.alive)
                spot = nu.master.GetBody().footPosition + new Vector3(2f, 1f, 0f);
        }
        if (master == null) return;
        CharacterBody body = master.GetBody();
        if (body != null && body.healthComponent.alive) { Log("drown respawn: " + name + " already alive"); return; }
        Vector3 dry;
        if (lastDry.TryGetValue(name, out dry)) spot = dry + Vector3.up * 1f;
        if (spot == null) { Log("drown respawn: no safe spot for " + name); return; }

        master.Respawn(spot.Value, Quaternion.identity);
        Say("<color=#66ccff>" + name + " is back on dry land!</color>");
        Log("drown respawn: " + name + " at " + spot.Value);
    }
}
