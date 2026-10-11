// TimeHud1 - host add-on: the fishing clock at the top of JV94's screen (build with TimeHudLook.cs).
// Reads DayNight<N>.hour (newest enabled), and if the running Fishing<N> has the weather/time events (gameplay update),
// its eventName + time left. Shows "Glow bobber night bonus" while it applies to the local player.
// Hidden when no run is active or the clock add-on isn't running. TimeHud1.enabled = false hides it.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;

public static class TimeHud1
{
    public static bool enabled = true;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static GameObject go;
    static Type dayNight, fishing; static float lookAt = -100f;
    public static float hour = -1f; public static string eventName, eventLeft, glowLine;
    static int errors;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_timehud.log"), DateTime.Now.ToString("HH:mm:ss ") + "[TimeHud1] " + s + Environment.NewLine); } catch { } }
    static Type[] SafeTypes(Assembly a) { try { return FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return new Type[0]; } }
    static Type Newest(string prefix, string need)
    {
        Type best = null; int bn = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                int n;
                if (!t.Name.StartsWith(prefix) || !int.TryParse(t.Name.Substring(prefix.Length), out n) || n <= bn) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en != null && en.FieldType == typeof(bool) && !(bool)en.GetValue(null)) continue;
                if (need != null && t.GetField(need, All) == null) continue;
                best = t; bn = n;
            }
        return best;
    }
    static object SGet(Type t, string n) { if (t == null) return null; FieldInfo f = t.GetField(n, All); if (f != null) return f.GetValue(null); PropertyInfo p = t.GetProperty(n, All); return p != null ? p.GetValue(null, null) : null; }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t != typeof(TimeHud1) && Regex.IsMatch(t.Name, "^TimeHud\\d+$")) { FieldInfo f = t.GetField("enabled", All); if (f != null) f.SetValue(null, false); }
        RoR2Application.onUpdate += Tick;
        Log("installed");
    }

    static void Tick()
    {
        if (!enabled) { if (go != null) { UnityEngine.Object.Destroy(go); go = null; } return; }
        try
        {
            if (go == null) { go = new GameObject("TimeHud1"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<TimeHud1Gui>(); Log("HUD object created"); }
            if (Time.unscaledTime - lookAt > 5f) { lookAt = Time.unscaledTime; dayNight = Newest("DayNight", "hour"); fishing = Newest("Fishing", "money"); }
            hour = dayNight == null || RoR2.Run.instance == null ? -1f : Convert.ToSingle(SGet(dayNight, "hour"));
            eventName = null; eventLeft = null; glowLine = null;
            if (fishing != null && fishing.GetField("eventName", All) != null)
            {
                eventName = Convert.ToString(SGet(fishing, "eventName"));
                MethodInfo left = fishing.GetMethod("EventSecondsLeft", All);
                if (left != null) { int s = (int)Convert.ToSingle(left.Invoke(null, null)); eventLeft = (s / 60) + ":" + (s % 60).ToString("00"); }
            }
            if (fishing != null && hour >= 0f && (hour >= 20f || hour < 5f))
            {
                FieldInfo live = fishing.GetField("glowBonusLive", All);
                var owners = SGet(fishing, "rodOwners") as IDictionary; string me = Convert.ToString(SGet(fishing, "hudLocalName"));
                object o = owners != null && me != null && owners.Contains(me) ? owners[me] : null;
                string bob = o == null ? null : Convert.ToString(o.GetType().GetField("bobber").GetValue(o));
                if (live != null && (bool)live.GetValue(null) && GlowName(bob)) glowLine = "<color=#9dffb0>" + bob + ": night bonus active</color>";
            }
        }
        catch (Exception e) { if (errors++ < 10) Log("tick error: " + e); }
    }
    static bool GlowName(string b) { return b == "Glow Bobber" || b == "Firefly Bobber" || b == "Lantern Bobber"; }
}

public class TimeHud1Gui : MonoBehaviour
{
    static int errs;
    void OnGUI()
    {
        try
        {
            if (!TimeHud1.enabled || TimeHud1.hour < 0f) return;
            TimeHudLook.Draw(TimeHud1.hour, TimeHud1.eventName, TimeHud1.eventLeft, TimeHud1.glowLine);
        }
        catch (Exception e) { if (errs++ < 3) File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_timehud.log"), "GUI error: " + e + Environment.NewLine); }
    }
}
