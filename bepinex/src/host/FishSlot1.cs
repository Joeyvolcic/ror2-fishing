// Host-only IMGUI "skill slot" showing a fishing-pole icon with an F key label, left of the skill bar.
// All Unity object creation happens on the main thread (RoR2Application.onFixedUpdate, one shot).
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class FishSlot1
{
    public static bool enabled = true;
    static bool installed, setupDone;
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static Assembly ror2;
    static EventInfo fixedEv;
    static Action handler;
    internal static Texture2D tex;
    internal static GameObject go;

    internal static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_icon.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

    public static void Run()
    {
        try
        {
            if (installed) { Log("FishSlot1 already installed"); return; }
            installed = true;
            foreach (var a in FishingScope.Assemblies()) if (a.GetName().Name == "RoR2") ror2 = a;
            fixedEv = ror2.GetType("RoR2.RoR2Application").GetEvent("onFixedUpdate", All);
            handler = Setup;
            fixedEv.AddEventHandler(null, handler);
            Log("FishSlot1 Run: subscribed to onFixedUpdate");
        }
        catch (Exception e) { Log("FishSlot1 Run failed: " + e); }
    }

    static void Setup()
    {
        if (setupDone) return;
        setupDone = true;
        try { fixedEv.RemoveEventHandler(null, handler); } catch { }
        try
        {
            // turn off older slot versions
            foreach (var a in FishingScope.Assemblies())
            {
                Type[] ts; try { ts = FishingScope.TypesOf(a); } catch { continue; }
                foreach (Type t in ts)
                    if (t.Name.StartsWith("FishSlot") && t != typeof(FishSlot1))
                    {
                        FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                        if (en != null && en.FieldType == typeof(bool)) { en.SetValue(null, false); Log("disabled old " + t.Name); }
                    }
            }
            float[] px = IconPixels.Draw();
            int n = IconPixels.N;
            var cols = new Color[n * n];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]);
            tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.SetPixels(cols);
            tex.Apply();
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            go = new GameObject("FishSlot1");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FishSlotGui1>();
            Log("FishSlot1 setup ok on main thread: texture " + n + "x" + n + ", GUI object created");
        }
        catch (Exception e) { Log("FishSlot1 setup failed: " + e); }
    }
}

public class FishSlotGui1 : MonoBehaviour
{
    static PropertyInfo runInstance, cachedBody, userName;
    static MethodInfo firstLocalUser;
    static bool reflDone, loggedDraw;
    static int errs;
    static int cacheFrame = -1;
    static bool cacheShow;
    static int cacheState;
    GUIStyle keyStyle, tagStyle;

    static void InitRefl()
    {
        reflDone = true;
        Assembly r = FishSlot1.ror2;
        runInstance = r.GetType("RoR2.Run").GetProperty("instance", FishSlot1.All);
        Type lum = r.GetType("RoR2.LocalUserManager");
        firstLocalUser = lum.GetMethod("GetFirstLocalUser", FishSlot1.All);
        cachedBody = r.GetType("RoR2.LocalUser").GetProperty("cachedBody", FishSlot1.All);
    }

    static bool Alive(object o) { var u = o as UnityEngine.Object; return u != null; }  // Unity null check

    // Fishing state for JV94 from the active Fishing* mod, if it exposes one: 0 idle, 1 waiting, 2 biting. -1 unknown.
    static int CastState()
    {
        try
        {
            foreach (var a in FishingScope.Assemblies())
            {
                if (!a.GetName().Name.StartsWith("Fishing")) continue;
                foreach (Type t in FishingScope.TypesOf(a))
                {
                    if (!t.Name.StartsWith("Fishing") || t.Name.StartsWith("FishingTest")) continue;
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en == null || !(bool)en.GetValue(null)) continue;
                    FieldInfo af = t.GetField("anglers", FishSlot1.All);
                    var dict = af != null ? af.GetValue(null) as IDictionary : null;
                    if (dict == null) continue;
                    FieldInfo nm = t.GetField("hudLocalName", FishSlot1.All);
                    string me = (nm != null ? nm.GetValue(null) as string : null) ?? "";
                    if (!dict.Contains(me)) return 0;
                    object ang = dict[me];
                    FieldInfo st = ang.GetType().GetField("state", FishSlot1.All);
                    return st != null ? (int)st.GetValue(ang) : -1;
                }
            }
        }
        catch { }
        return -1;
    }

    void OnGUI()
    {
        try
        {
            if (!FishSlot1.enabled || FishSlot1.tex == null) return;
            if (Event.current.type != EventType.Repaint) return;
            if (!reflDone) InitRefl();
            if (cacheFrame != Time.frameCount)
            {
                cacheFrame = Time.frameCount;
                object run = runInstance.GetValue(null, null);
                object user = Alive(run) ? firstLocalUser.Invoke(null, null) : null;
                object body = user != null ? cachedBody.GetValue(user, null) : null;
                cacheShow = Alive(run) && Alive(body);
                cacheState = cacheShow ? CastState() : -1;
            }
            if (!cacheShow) return;

            float h = Screen.height, w = Screen.width;
            float size = Mathf.Round(64f * h / 1080f);
            float x = Mathf.Round(w * 0.655f - size), y = Mathf.Round(h * 0.855f);
            var r = new Rect(x, y, size, size);

            if (keyStyle == null)
            {
                keyStyle = new GUIStyle(GUI.skin.label); keyStyle.alignment = TextAnchor.MiddleCenter; keyStyle.fontStyle = FontStyle.Bold;
                tagStyle = new GUIStyle(keyStyle);
            }
            keyStyle.fontSize = Mathf.RoundToInt(22f * h / 1080f);
            tagStyle.fontSize = Mathf.RoundToInt(13f * h / 1080f);

            Color old = GUI.color;
            bool busy = cacheState == 1 || cacheState == 2;
            GUI.color = busy ? new Color(0.45f, 0.45f, 0.45f, 0.9f) : Color.white;
            GUI.DrawTexture(r, FishSlot1.tex);
            if (busy)
            {
                bool bite = cacheState == 2;
                GUI.color = bite ? (Mathf.Repeat(Time.unscaledTime, 0.5f) < 0.25f ? new Color(1f, 0.85f, 0.2f) : Color.white) : new Color(0.75f, 0.9f, 1f);
                GUI.Label(r, bite ? "BITE!" : "...", tagStyle);
            }
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(x + 2, y + size + 2, size, size * 0.45f), "F", keyStyle);   // shadow
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y + size, size, size * 0.45f), "F", keyStyle);
            GUI.color = old;

            if (!loggedDraw) { loggedDraw = true; FishSlot1.Log("slot drawn at " + x + "," + y + " size " + size + " screen " + w + "x" + h + " castState=" + cacheState); }
        }
        catch (Exception e) { if (errs++ < 3) FishSlot1.Log("FishSlotGui1 OnGUI error: " + e); }
    }
}
