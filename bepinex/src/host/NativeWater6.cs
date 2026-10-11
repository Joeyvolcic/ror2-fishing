// FINAL host add-on (replaces WaterFx1 in LOAD-FISHING.ps1): turns off WaterFx1 (and the NativeWater1-5 test builds) (procedural ripple texture + caustic overlay) and gives the fishing
// lake ("FishingWater", recreated each stage) Risk of Rain 2's own water material - Gilded Coast's matGSWater (C:\RoR2-FishingClient\host\NativeWaterLook.cs).
// Run() only subscribes to RoR2Application.onUpdate; all Unity work happens there.
// Disable: NativeWater6.enabled = false (restores the plain sheet). Switch material: NativeWater6.key = "<addressable key>".
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class NativeWater6
{
    public static bool enabled = true;
    public static string key;
    static string appliedKey;
    static bool hooked;
    static GameObject cur;
    static float nextCheck, startAt = -1;
    static int errors;

    static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_nativewater.log"), DateTime.Now.ToString("HH:mm:ss ") + "[NativeWater6] " + s + Environment.NewLine); } catch { } }

    static readonly string[] Older = { "WaterFx1", "NativeWater1", "NativeWater2", "NativeWater3", "NativeWater4", "NativeWater5" };
    static void DisableWaterFx()
    {
        foreach (var a in FishingScope.Assemblies())
        foreach (var name in Older)
        {
            Type t = null;
            try { t = a.GetType(name); } catch { }
            if (t == null) continue;
            var f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
            if (f != null && (bool)f.GetValue(null)) { f.SetValue(null, false); Log(name + ".enabled = false"); }
        }
    }

    public static void Run()
    {
        try
        {
            NativeWaterLook.Log = Log;
            if (hooked) { enabled = true; Log("Run() again: enabled"); return; }
            Type app = null;
            foreach (var a in FishingScope.Assemblies())
                if (a.GetName().Name == "RoR2") app = a.GetType("RoR2.RoR2Application");
            EventInfo ev = app.GetEvent("onUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Action h = Tick;
            ev.AddEventHandler(null, h);
            hooked = true;
            Log("installed");
        }
        catch (Exception e) { Log("Run error: " + e); }
    }

    static void Tick()
    {
        try
        {
            float t = Time.realtimeSinceStartup;
            if (!enabled)
            {
                if (cur != null) { NativeWaterLook.Remove(cur); Log("disabled: original material restored"); }
                cur = null; return;
            }
            if (t < nextCheck) return;
            nextCheck = t + 1f;
            DisableWaterFx();                          // also catches a WaterFx1 injected later
            if (startAt < 0) { startAt = t + 1.5f; return; }   // give WaterFx1 a frame to remove its overlay
            if (t < startAt) return;
            var go = GameObject.Find("FishingWater");
            if (go == null) { cur = null; return; }
            if (key != appliedKey) { NativeWaterLook.Key = key; appliedKey = key; if (go == cur) { NativeWaterLook.Apply(go); return; } }
            if (go != cur) { if (NativeWaterLook.Apply(go)) cur = go; }
            else if (NativeWaterLook.Animate(go)) Log("material was replaced by someone else - re-applied");
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + e); }
    }
}
