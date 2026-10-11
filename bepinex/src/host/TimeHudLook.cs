// TimeHudLook - the fishing clock on the HUD (IMGUI; host add-on TimeHud<N> and FISHING.exe share it).
// Draw(hour, eventName, eventLeft, glowLine) from OnGUI: a small panel at the top centre with a sun or moon icon,
// "07:30 Morning", a sky bar (night-dawn-day-dusk-night) with a marker for now, and the weather/time event if known.
using System;
using UnityEngine;

public static class TimeHudLook
{
    public static float y = 8f, scale = 1f;
    static Texture2D sun, moon, sky, white;
    static GUIStyle big, small;

    public static string PhaseName(float h)
    {
        h = Mathf.Repeat(h, 24f);
        if (h >= 19.5f || h < 5f) return "Night";
        if (h < 9f) return "Morning";
        if (h < 17f) return "Day";
        return "Sunset";
    }
    public static string Clock(float h) { int m = (int)(Mathf.Repeat(h, 24f) * 60f); return (m / 60).ToString("00") + ":" + (m % 60).ToString("00"); }

    static Texture2D Icon(bool isSun)
    {
        int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = i - n / 2f + 0.5f, y2 = j - n / 2f + 0.5f, r = Mathf.Sqrt(x * x + y2 * y2);
                Color c = new Color(0, 0, 0, 0);
                if (isSun)
                {
                    float ang = Mathf.Atan2(y2, x);
                    float ray = 0.5f + 0.5f * Mathf.Cos(ang * 8f);
                    if (r < 15f) c = Color.Lerp(new Color(1f, 0.95f, 0.5f), new Color(1f, 0.75f, 0.15f), r / 15f);
                    else if (r < 18f + 11f * ray && r < 30f) c = new Color(1f, 0.78f, 0.2f, Mathf.Clamp01(1.2f - (r - 18f) / 12f));
                }
                else
                {
                    float x2 = x - 9f, y3 = y2 + 6f, r2 = Mathf.Sqrt(x2 * x2 + y3 * y3);
                    if (r < 22f && r2 > 18f) c = Color.Lerp(new Color(0.95f, 0.95f, 1f), new Color(0.75f, 0.8f, 0.95f), r / 22f);
                    if (c.a == 0f && r < 26f && r2 > 15f) c = new Color(0.7f, 0.8f, 1f, 0.25f * (1f - (r - 22f) / 4f) * (r >= 22f ? 1f : 0f));
                }
                px[j * n + i] = c;
            }
        t.SetPixels(px); t.Apply(); return t;
    }

    static Color SkyAt(float h)
    {
        Color night = new Color(0.08f, 0.1f, 0.25f), dawn = new Color(0.95f, 0.55f, 0.45f), day = new Color(0.45f, 0.75f, 1f), dusk = new Color(1f, 0.55f, 0.2f);
        if (h < 5f) return night;
        if (h < 6.5f) return Color.Lerp(night, dawn, (h - 5f) / 1.5f);
        if (h < 9f) return Color.Lerp(dawn, day, (h - 6.5f) / 2.5f);
        if (h < 17f) return day;
        if (h < 18.5f) return Color.Lerp(day, dusk, (h - 17f) / 1.5f);
        if (h < 20f) return Color.Lerp(dusk, night, (h - 18.5f) / 1.5f);
        return night;
    }

    static void Init()
    {
        if (sun != null) return;
        sun = Icon(true); moon = Icon(false);
        sky = new Texture2D(240, 1, TextureFormat.RGBA32, false); sky.wrapMode = TextureWrapMode.Clamp;
        for (int i = 0; i < 240; i++) sky.SetPixel(i, 0, SkyAt(i / 10f));
        sky.Apply();
        white = Texture2D.whiteTexture;
        big = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, richText = true };
        small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = false };
    }

    public static void Draw(float hour, string eventName, string eventLeft, string glowLine)
    {
        Init();
        float s = scale * Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
        big.fontSize = (int)(20 * s); small.fontSize = (int)(13 * s);
        bool hasEv = !string.IsNullOrEmpty(eventName), hasGlow = !string.IsNullOrEmpty(glowLine);
        float w = 260f * s, h = (62f + (hasEv ? 20f : 0f) + (hasGlow ? 18f : 0f)) * s;
        var r = new Rect((Screen.width - w) / 2f, y * s, w, h);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(r, white); GUI.color = prev;
        string ph = PhaseName(hour);
        bool isSun = hour >= 5.5f && hour < 19.5f;
        GUI.DrawTexture(new Rect(r.x + 10f * s, r.y + 6f * s, 34f * s, 34f * s), isSun ? sun : moon);
        string col = ph == "Night" ? "#aab8ff" : ph == "Morning" ? "#ffc89a" : ph == "Sunset" ? "#ffb060" : "#fff2a0";
        GUI.Label(new Rect(r.x + 52f * s, r.y + 4f * s, w - 60f * s, 38f * s), Clock(hour) + "  <color=" + col + ">" + ph + "</color>", big);
        // sky bar + marker
        var bar = new Rect(r.x + 12f * s, r.y + 44f * s, w - 24f * s, 8f * s);
        GUI.DrawTexture(bar, sky);
        float mx = bar.x + bar.width * Mathf.Repeat(hour, 24f) / 24f;
        GUI.color = Color.white; GUI.DrawTexture(new Rect(mx - 1.5f * s, bar.y - 3f * s, 3f * s, bar.height + 6f * s), white); GUI.color = prev;
        float yy = r.y + 56f * s;
        if (hasEv)
        {
            string ec = eventName.Contains("Storm") ? "#9fb4ff" : eventName.Contains("Rain") ? "#7fc8ff" : eventName.Contains("Night") ? "#b59cff" : eventName.Contains("Golden") ? "#ffcc55" : "#aaddaa";
            GUI.Label(new Rect(r.x, yy, w, 20f * s), "<color=" + ec + ">" + eventName.Replace("+", " + ") + "</color>" + (string.IsNullOrEmpty(eventLeft) ? "" : "  <color=#aaaaaa>" + eventLeft + "</color>"), small);
            yy += 20f * s;
        }
        if (hasGlow) GUI.Label(new Rect(r.x, yy, w, 18f * s), glowLine, small);
    }
}
