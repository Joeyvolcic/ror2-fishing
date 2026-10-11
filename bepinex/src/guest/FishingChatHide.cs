// BepInEx build, guest side (new file, not synced from the originals).
// Hides the fishing game's machine lines ("[FT] ..." and old "[REQ] ...") from the guest's chat box.
// In FISHING.exe this was done by ReqPanelClient (the F2 request panel), which the BepInEx build drops.
// Same rule and timing as the host's Fishing18.HideTaggedLines: a tagged line marks the chat dirty on
// Chat.onChatChanged, and it is removed on the next RoR2Application.onUpdate, after every chat reader saw it.
// Idles while this game is the server (the host's Fishing18 hides the lines there).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

public static class FishingChatHide
{
    public static bool enabled = true;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static bool installed, dirty;
    static Type chatType, chatBoxType;
    static FieldInfo logField;
    static int hiddenLogged, errors;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_fishing_client.log"), DateTime.Now.ToString("HH:mm:ss ") + "[chat hide] " + s + Environment.NewLine); } catch { } }

    public static void Run()
    {
        if (installed) { enabled = true; return; }
        installed = true;
        try
        {
            Assembly ror2 = typeof(RoR2.Chat).Assembly;
            chatType = typeof(RoR2.Chat);
            chatBoxType = ror2.GetType("RoR2.UI.ChatBox");
            logField = chatType.GetField("log", All);
            EventInfo cc = chatType.GetEvent("onChatChanged", All);
            if (cc == null || cc.EventHandlerType != typeof(Action) || logField == null) { Log("chat hook not available"); return; }
            Action c = OnChatChanged; cc.AddEventHandler(null, c);
            Action u = Tick; ror2.GetType("RoR2.RoR2Application").GetEvent("onUpdate", All).AddEventHandler(null, u);
            Log("installed");
        }
        catch (Exception e) { Log("install error: " + e); }
    }

    static string Plain(string raw) { return Regex.Replace(raw.Replace("<noparse>", "").Replace("</noparse>", ""), "</?color[^>]*>", "").Trim(); }
    static bool Hidden(string p) { return p.StartsWith("[FT] ") || p.Contains(": [FT] ") || p.StartsWith("[REQ] ") || p.Contains(": [REQ] "); }

    static void OnChatChanged()
    {
        try
        {
            if (!enabled || UnityEngine.Networking.NetworkServer.active) return;
            IList log = logField.GetValue(null) as IList;
            if (log == null || log.Count == 0) return;
            string line = log[log.Count - 1] as string;
            if (line != null && Hidden(Plain(line))) dirty = true;
        }
        catch { }
    }

    static void Tick()
    {
        if (!dirty) return;
        dirty = false;
        try
        {
            List<string> log = logField.GetValue(null) as List<string>;
            if (log == null) return;
            int removed = 0;
            for (int i = log.Count - 1; i >= 0; i--)
                if (log[i] != null && Hidden(Plain(log[i]))) { log.RemoveAt(i); removed++; }
            if (removed == 0 || chatBoxType == null) return;
            foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(chatBoxType))
            {
                try
                {
                    var mb = b as UnityEngine.Behaviour;
                    if (mb == null || !mb.enabled) continue;
                    chatBoxType.GetMethod("BuildChat", All).Invoke(b, null);
                    MethodInfo sc = chatBoxType.GetMethod("ScrollToBottom", All);
                    if (sc != null) sc.Invoke(b, null);
                }
                catch { }
            }
            if (hiddenLogged++ < 3) Log("hid " + removed + " tagged chat line(s)");
        }
        catch (Exception e) { if (errors++ < 10) Log("hide error: " + e.Message); }
    }
}
