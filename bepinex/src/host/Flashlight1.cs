// Flashlight for the host's fishing game: press L to toggle a spotlight that follows the camera (points where you
// look). A one-time local hint appears in chat the first time it gets dark (night, rain or storm) with the light off.
// Local only (other players don't see your beam yet; guests get it in the next client release).
using System;
using System.Reflection;
using RoR2;
using UnityEngine;

public static class Flashlight1
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static bool enabled = true;
    public static KeyCode key = KeyCode.L;
    public static float range = 48f, angle = 52f, intensity = 3.2f;
    public static bool on;

    static bool installed, hinted;
    static GameObject go; static Light spot;
    static FieldInfo fEvent; static float nextFind;
    static int errs;

    static void Log(string s) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_flashlight.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
            foreach (Type t in ts)
                if (t != null && t.Name.StartsWith("Flashlight") && t.Name != typeof(Flashlight1).Name)
                { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); }
        }
        if (installed) return;
        installed = true;
        RoR2Application.onUpdate += Tick;
        Log("installed (key " + key + ")");
    }

    static bool ChatOpen()
    {
        try
        {
            foreach (var b in UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>())
            {
                PropertyInfo p = b.GetType().GetProperty("showInput", All);
                object v = p != null ? p.GetValue(b, null) : null;
                if (v is bool && (bool)v) return true;
            }
        }
        catch { }
        return false;
    }

    static string EventName()
    {
        if (Time.realtimeSinceStartup >= nextFind && fEvent == null)
        {
            nextFind = Time.realtimeSinceStartup + 5f; int best = -1;
            foreach (var a in FishingScope.Assemblies())
            {
                if (!a.GetName().Name.StartsWith("Fishing")) continue;
                Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                foreach (Type t in ts)
                {
                    if (t == null || !t.Name.StartsWith("GameplayFx")) continue;
                    int v; int.TryParse(t.Name.Substring(10), out v);
                    FieldInfo f = t.GetField("eventName", All);
                    if (f != null && v > best) { best = v; fEvent = f; }
                }
            }
        }
        return fEvent == null ? "" : (fEvent.GetValue(null) as string ?? "");
    }

    static void Tick()
    {
        try
        {
            if (!enabled) { if (go != null) go.SetActive(false); return; }
            Camera cam = Camera.main;
            bool inRun = RoR2.Run.instance != null && cam != null;
            if (inRun && Input.GetKeyDown(key) && !ChatOpen())
            {
                on = !on;
                Log("flashlight " + (on ? "on" : "off"));
                Util.PlaySound(on ? "Play_UI_menuClick" : "Play_UI_menuBack", RoR2Application.instance.gameObject);
            }
            if (inRun && !on && !hinted)
            {
                string ev = EventName();
                if (ev.Contains("Night") || ev.Contains("Rain") || ev.Contains("Storm"))
                {
                    hinted = true;
                    Chat.AddMessage("<color=#ffd27f>It's getting dark - press L to switch your flashlight on.</color>");
                }
            }
            if (!inRun || !on) { if (go != null && go.activeSelf) go.SetActive(false); return; }
            if (go == null)
            {
                go = new GameObject("FishingFlashlight");
                UnityEngine.Object.DontDestroyOnLoad(go);
                spot = go.AddComponent<Light>();
                spot.type = LightType.Spot; spot.shadows = LightShadows.None;
                spot.color = new Color(1f, 0.95f, 0.85f);
                spot.renderMode = LightRenderMode.ForcePixel;
                Log("spotlight created");
            }
            if (!go.activeSelf) go.SetActive(true);
            spot.range = range; spot.spotAngle = angle; spot.intensity = intensity;
            Transform c = cam.transform;
            go.transform.position = c.position + c.right * 0.35f - c.up * 0.25f;
            go.transform.rotation = Quaternion.LookRotation(c.forward, c.up);
        }
        catch (Exception e) { if (errs++ < 20) Log("error: " + e.Message); }
    }
}

