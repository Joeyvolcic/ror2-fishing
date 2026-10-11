// Guest flashlight (FISHING.exe, compiled into the FishingClient payload): the client version of the host's Flashlight<N>
// (agents\weathertune\Flashlight1.cs). Press L in a run to toggle a spotlight that follows YOUR camera (points where you look).
// Local only: nobody else sees your beam, nothing is sent to the host. A one-time hint appears in chat the first time it gets
// dark (Night / Rain / Storm event or night on the synced clock) while the light is off.
// Driven from FishingClient.Update (main thread): FishingFlashlight.Tick(inRun). Never runs on the host (FishingClient returns
// before calling it there), so it can't double the host's own Flashlight<N>.
using System;
using System.Reflection;
using UnityEngine;

public static class FishingFlashlight
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static KeyCode key = KeyCode.L;
    public static float range = 48f, angle = 52f, intensity = 3.2f;   // same as Flashlight1
    public static bool on;
    public static string eventName = "";      // last [FT] EVENT (set by FishingClient)
    public static float hour = -1f;           // synced clock (-1 = no [FT] TIME yet)
    public static Action<string> Log;

    static GameObject go; static Light spot;
    static bool hinted;
    static int errs;
    static Type chatBoxType, chatType, utilType, appType;
    static FieldInfo reqOpen; static float nextReqFind;

    static void L(string s) { try { if (Log != null) Log(s); } catch { } }

    static Type Find(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(name); if (t != null) return t; }
        return null;
    }

    // true while something else wants the keyboard: the game's chat box or the F2 request panel's text field
    static bool Typing()
    {
        try
        {
            if (chatBoxType == null) chatBoxType = Find("RoR2.UI.ChatBox");
            if (chatBoxType != null)
                foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(chatBoxType))
                {
                    PropertyInfo p = chatBoxType.GetProperty("showInput", All);
                    object v = p != null ? p.GetValue(b, null) : null;
                    if (v is bool && (bool)v) return true;
                }
            if (reqOpen == null && Time.unscaledTime >= nextReqFind)
            {
                nextReqFind = Time.unscaledTime + 5f;
                Type rp = Find("ReqPanelClient");
                if (rp != null) reqOpen = rp.GetField("open", All);
            }
            if (reqOpen != null) { object o = reqOpen.GetValue(null); if (o is bool && (bool)o) return true; }
        }
        catch { }
        return false;
    }

    static void Sound(string ev)
    {
        try
        {
            if (utilType == null) { utilType = Find("RoR2.Util"); appType = Find("RoR2.RoR2Application"); }
            object app = appType.GetProperty("instance", All).GetValue(null, null);
            GameObject g = app == null ? null : ((Component)app).gameObject;
            MethodInfo m = utilType.GetMethod("PlaySound", new[] { typeof(string), typeof(GameObject) });
            if (m != null && g != null) m.Invoke(null, new object[] { ev, g });
        }
        catch { }
    }

    static void Hint(string text)
    {
        try
        {
            if (chatType == null) chatType = Find("RoR2.Chat");
            MethodInfo m = chatType.GetMethod("AddMessage", new[] { typeof(string) });
            if (m != null) m.Invoke(null, new object[] { text });
        }
        catch { }
    }

    static bool Dark()
    {
        if (eventName.Contains("Night") || eventName.Contains("Rain") || eventName.Contains("Storm")) return true;
        return hour >= 0f && (hour >= 20f || hour < 5f);
    }

    public static void Tick(bool inRun)
    {
        try
        {
            Camera cam = inRun ? Camera.main : null;
            if (cam == null) { Off(); return; }
            if (Input.GetKeyDown(key) && !Typing())
            {
                on = !on;
                L("flashlight " + (on ? "on" : "off"));
                Sound(on ? "Play_UI_menuClick" : "Play_UI_menuBack");
            }
            if (!on && !hinted && Dark())
            {
                hinted = true;
                Hint("<color=#ffd27f>It's getting dark - press L to switch your flashlight on.</color>");
            }
            if (!on) { if (go != null && go.activeSelf) go.SetActive(false); return; }
            if (go == null)
            {
                go = new GameObject("FishingClientFlashlight");
                UnityEngine.Object.DontDestroyOnLoad(go);
                spot = go.AddComponent<Light>();
                spot.type = LightType.Spot; spot.shadows = LightShadows.None;
                spot.color = new Color(1f, 0.95f, 0.85f);
                spot.renderMode = LightRenderMode.ForcePixel;
                L("spotlight created");
            }
            if (!go.activeSelf) go.SetActive(true);
            spot.range = range; spot.spotAngle = angle; spot.intensity = intensity;
            Transform c = cam.transform;
            go.transform.position = c.position + c.right * 0.35f - c.up * 0.25f;
            go.transform.rotation = Quaternion.LookRotation(c.forward, c.up);
        }
        catch (Exception e) { if (errs++ < 20) L("error: " + e.Message); }
    }

    // run ended / disabled / host: hide the beam (the on/off choice is kept for the next run)
    public static void Off() { if (go != null && go.activeSelf) go.SetActive(false); }
    public static void Destroy() { if (go != null) { UnityEngine.Object.Destroy(go); go = null; spot = null; } }
}
