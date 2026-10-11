// Lake level control for the host's fishing game.
//   !water up <m> / !water down <m> / !water <m> (height above the stage's original level) / !water reset / !water
// Host (JV94) only. The water rises/falls smoothly over ~12 s after a 10 s chat warning ("get to higher ground").
// Moves the newest Fishing<N>'s lake (waterY + the FishingWater object) and the hotspot heights, re-broadcasts
// [FT] HOT / HOT2 so guests' water follows (their client syncs its lake to the hotspot's Y), and drops DrownRespawn's
// remembered dry spots that end up under water. The offset is remembered per map and re-applied (with the same
// warning) when that map's lake is created again.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

public static class WaterLevel3
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static bool enabled = true;
    public static float startOffset = 0f, warnSeconds = 10f, riseSeconds = 12f;

    static bool installed; static int seenLog, errs;
    static Type fish; static FieldInfo fWaterY, fWater, fScene, fHot, fHot2; static MethodInfo mHot, mHot2;
    static readonly Dictionary<string, float> offsets = new Dictionary<string, float>();   // scene -> metres above base
    static string baseScene = ""; static float baseY = float.NaN, baseCreatedY = float.NaN;
    static float fromY, toY, warnUntil = -1f, riseStart = -1f, nextBroadcast;
    static bool pending;

    static void Log(string s) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_waterlevel.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }
    static void Say(string s) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = s }); }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
                if (t != null && t.Name.StartsWith("WaterLevel") && t.Name != typeof(WaterLevel3).Name)
                { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); }
        }
        if (installed) return;
        installed = true;
        seenLog = Chat.readOnlyLog.Count;
        RoR2Application.onUpdate += Tick;
        Log("installed");
    }

    static void FindFish()
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
        {
            if (!a.GetName().Name.StartsWith("Fishing")) continue;
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
            {
                if (t == null || !t.Name.StartsWith("Fishing")) continue;
                int v; if (!int.TryParse(t.Name.Substring(7), out v)) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (t.GetField("waterY", All) != null && v > bn && (en == null || (bool)en.GetValue(null))) { best = t; bn = v; }
            }
        }
        if (best == fish) return;
        fish = best;
        if (fish == null) return;
        fWaterY = fish.GetField("waterY", All); fWater = fish.GetField("water", All); fScene = fish.GetField("waterScene", All);
        fHot = fish.GetField("hotspotPos", All); fHot2 = fish.GetField("hot2Pos", All);
        mHot = fish.GetMethod("BroadcastHot", All, null, Type.EmptyTypes, null); mHot2 = fish.GetMethod("BroadcastHot2", All, null, Type.EmptyTypes, null);
        Log("using " + fish.Name);
    }

    static float nextIsland; static bool islandOn;
    static bool IslandActive()
    {
        if (Time.realtimeSinceStartup < nextIsland) return islandOn;
        nextIsland = Time.realtimeSinceStartup + 0.5f; islandOn = false;
        foreach (var a in FishingScope.Assemblies())
        {
            string an = a.GetName().Name; if (!an.StartsWith("Island") && !an.StartsWith("TestSandbox") && !FishingScope.IsMod(a)) continue;
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts) { if (t == null || !(t.Name.StartsWith("Island") || t.Name.StartsWith("TestSandbox"))) continue; FieldInfo f = t.GetField("active", All), en = t.GetField("enabled", All); if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null) && (en == null || (bool)en.GetValue(null))) islandOn = true; }
        }
        return islandOn;
    }
    static string Scene() { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
    static GameObject WaterGo() { var w = fWater == null ? null : fWater.GetValue(null) as UnityEngine.Object; return w as GameObject ?? GameObject.Find("FishingWater"); }

    static void Tick()
    {
        if (!enabled || !NetworkServer.active) return;
        try
        {
            if (Time.frameCount % 120 == 0 || fish == null) FindFish();
            if (fish == null) return;
            if (IslandActive()) { seenLog = Chat.readOnlyLog.Count; baseScene = ""; return; }   // the island owns the lake while it is open
            ReadChat();
            GameObject w = WaterGo();
            if (w == null || Convert.ToString(fScene.GetValue(null)) != Scene()) return;
            float y = (float)fWaterY.GetValue(null);

            // New lake (new stage or rebuilt): remember its base; re-apply this map's offset.
            if (Scene() != baseScene || (riseStart < 0f && !pending && float.IsNaN(baseCreatedY)) || (riseStart < 0f && !pending && Mathf.Abs(y - baseCreatedY) > 0.05f && Mathf.Abs(y - toY) > 0.05f))
            {
                baseScene = Scene(); baseY = y; baseCreatedY = y; toY = y; riseStart = -1f; pending = false;
                Log("lake on " + baseScene + " base Y=" + y.ToString("0.00"));
                float off;
                if (!offsets.TryGetValue(baseScene, out off)) { off = startOffset; offsets[baseScene] = off; }
                if (off != 0f) Schedule(off, true);
            }

            if (pending && Time.time >= warnUntil) { pending = false; riseStart = Time.time; fromY = y; Log("rising " + fromY.ToString("0.00") + " -> " + toY.ToString("0.00")); }
            if (riseStart >= 0f)
            {
                float k = Mathf.Clamp01((Time.time - riseStart) / riseSeconds);
                float ny = Mathf.Lerp(fromY, toY, k * k * (3f - 2f * k));
                SetLevel(w, ny);
                if (Time.time >= nextBroadcast || k >= 1f) { nextBroadcast = Time.time + 1f; Broadcast(); }
                if (k >= 1f)
                {
                    riseStart = -1f; baseCreatedY = ny;
                    PruneDrySpots(ny);
                    Say("<color=#66ccff>The lake is now " + (ny - baseY).ToString("+0.#;-0.#;0") + " m from its normal level.</color>");
                    Log("level set to " + ny.ToString("0.00") + " (offset " + (ny - baseY).ToString("0.0") + ")");
                }
            }
        }
        catch (Exception e) { if (errs++ < 20) Log("error: " + e); }
    }

    static void Schedule(float offset, bool warn)
    {
        float target = baseY + offset;
        float cur = (float)fWaterY.GetValue(null);
        toY = target; offsets[baseScene] = offset;
        if (target > cur + 0.05f && warn)
        {
            pending = true; warnUntil = Time.time + warnSeconds;
            Say("<color=#ff9955>The water is rising " + (target - cur).ToString("0.#") + " m in " + (int)warnSeconds + " seconds - get to higher ground!</color>");
        }
        else { pending = false; riseStart = Time.time; fromY = cur; }
        Log("scheduled offset " + offset + " (target Y=" + target.ToString("0.00") + ")");
    }

    static void SetLevel(GameObject w, float y)
    {
        fWaterY.SetValue(null, y);
        Vector3 p = w.transform.position; w.transform.position = new Vector3(p.x, y, p.z);
        if (fHot != null) { Vector3 h = (Vector3)fHot.GetValue(null); h.y = y; fHot.SetValue(null, h); }
        if (fHot2 != null) { Vector3 h = (Vector3)fHot2.GetValue(null); if (h != Vector3.zero) { h.y = y; fHot2.SetValue(null, h); } }
    }

    static void Broadcast()
    {
        try { if (mHot != null) mHot.Invoke(null, null); } catch { }
        try { if (mHot2 != null) mHot2.Invoke(null, null); } catch { }
    }

    static void PruneDrySpots(float y)
    {
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
            {
                if (t == null || !t.Name.StartsWith("DrownRespawn")) continue;
                var f = t.GetField("lastDry", All);
                var d = f == null ? null : f.GetValue(null) as Dictionary<string, Vector3>;
                if (d == null) continue;
                foreach (string k in new List<string>(d.Keys)) if (d[k].y < y + 0.5f) d.Remove(k);
            }
        }
    }

    static string Strip(string s)
    {
        while (true) { int a = s.IndexOf('<'), b = s.IndexOf('>', a + 1); if (a < 0 || b < 0) return s.Trim(); s = s.Remove(a, b - a + 1); }
    }

    static void ReadChat()
    {
        var log = Chat.readOnlyLog;
        if (log.Count < seenLog) seenLog = 0;
        string host = null;
        try { var lu = LocalUserManager.GetFirstLocalUser(); if (lu != null && lu.currentNetworkUser != null) host = lu.currentNetworkUser.userName; } catch { }
        for (int i = seenLog; i < log.Count; i++)
        {
            string line = Strip(log[i]);
            int k = line.IndexOf(": !water");
            if (k < 0 || host == null || line.Substring(0, k) != host) continue;
            string[] a = line.Substring(k + 2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float cur; offsets.TryGetValue(baseScene, out cur);
            float v;
            if (a.Length == 1) { Say("<color=#66ccff>Lake level: " + cur.ToString("+0.#;-0.#;0") + " m (use !water up 5, !water down 3, !water 10, !water reset)</color>"); continue; }
            if (a[1] == "reset") Schedule(0f, false);
            else if ((a[1] == "up" || a[1] == "down") && a.Length > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                Schedule(Mathf.Clamp(cur + (a[1] == "up" ? v : -v), -10f, 80f), true);
            else if (float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                Schedule(Mathf.Clamp(v, -10f, 80f), true);
        }
        seenLog = log.Count;
    }
}


