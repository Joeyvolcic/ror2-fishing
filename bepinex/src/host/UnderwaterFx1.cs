// Host add-on: underwater view for the fishing lake ("FishingWater") on JV94's machine. All the work is in
// UnderwaterLook.cs (shared with the guest client); this class only drives it from RoR2Application.onUpdate.
// Run() (called on the injector thread) only subscribes; Unity objects are created lazily inside the tick (main thread).
// Disable: UnderwaterFx1.enabled = false (everything is removed and the scene's fog/ambient restored).
// Tuning: %TEMP%\ror2_underwater.cfg (see ror2_underwater.cfg.sample). Log: %TEMP%\ror2_underwater.log.
// Build: csc -target:library -nostdlib ... -out:UnderwaterFx1.dll UnderwaterFx1.cs UnderwaterLook.cs (see build_host.bat)
using System;
using System.Reflection;
using UnityEngine;

public static class UnderwaterFx1
{
    public static bool enabled = true;
    static bool hooked, drawing;
    static int errors;

    static void Log(string s) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_underwater.log"), DateTime.Now.ToString("HH:mm:ss ") + "[UnderwaterFx1] " + s + Environment.NewLine); } catch { } }

    static void DisableOlder()
    {
        string me = typeof(UnderwaterFx1).Name;
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts;
            try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
            {
                if (t == null || !t.Name.StartsWith("UnderwaterFx") || t.Name == me) continue;
                FieldInfo f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) { f.SetValue(null, false); Log(t.Name + ".enabled = false"); }
            }
        }
    }

    public static void Run()
    {
        try
        {
            DisableOlder();
            UnderwaterLook.Tag = "[UnderwaterFx1] ";
            if (hooked) { enabled = true; Log("Run() again: enabled"); return; }
            RoR2.RoR2Application.onUpdate += Tick;
            hooked = true;
            Log("installed (onUpdate); cfg " + UnderwaterLook.CfgPath);
        }
        catch (Exception e) { Log("Run error: " + e); }
    }

    static void Tick()
    {
        try
        {
            if (!enabled || RoR2.Run.instance == null)
            {
                if (drawing) { UnderwaterLook.Restore(); drawing = false; Log(enabled ? "no run: removed" : "disabled: removed, scene look restored"); }
                return;
            }
            drawing = true;
            UnderwaterLook.Tick();
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + e); }
    }
}
