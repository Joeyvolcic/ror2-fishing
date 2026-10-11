// InvLook - the fishing game's INVENTORY window (IMGUI), shared by the host add-on (Inventory<N>) and the guest payload
// (InventoryClient in FISHING.exe). UnityEngine + IMGUI only; the game (RoR2 / Rewired) is reached by reflection, so the
// guest build needs no extra references.
//
//  Left: equip slots drawn like an RPG paper doll (Rod, Rod Skin, Bobber | Lure, Gadget, Pet | Charm).
//  Right: tabs (All, Rods, Skins, Bobbers, Lures, Gadgets, Pets, Cases, Fish) and a grid of item tiles, coloured by rarity,
//  with a tooltip. Click a tile or drag it onto its slot to equip; right-click a slot to unequip (back to the default).
//  Fish / case tiles select instead and show their action buttons (Eat, Sell 1, Sell all, ...).
//  The owner fills `items` and handles `pending` (the window never changes game state itself).
//  While open: mouse cursor shown (MPEventSystem.cursorOpenerCount + 1), the local user is "UI focused" (no movement / skills,
//  same trick as the chat box), the Rewired keyboard + mouse maps are off (no camera turn, no pause on Esc), and `blocking`
//  stays true until the mouse buttons / Esc / I are released after closing.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

public class InvItem
{
    public string kind;       // rod skin bobber lure gadget pet charm | item (usable: book, ...) case fish
    public string name;       // shown name, also what the chat commands take
    public string rarity;     // default common uncommon rare legendary lunar boss | blue purple pink red gold | #rrggbb
    public string desc = "";  // what it does
    public string extra = ""; // "18 casts left", "StatTrak 12 fish", "Field-Tested", ...
    public int count;         // fish: how many (0 = no badge)
    public bool equipped, isDefault, stat;
    public string slot;       // gadgets: "utility" (Shift) or "special" (R) when equipped
    public string prefSlot;   // gadgets: the slot a click puts it in (null = first free)
    public string[] actions;  // non-equip buttons (fish: "Eat", "Sell 1", "Sell all")
}

public static class InvLook
{
    // ------------------------------------------------------------------ owner API
    public static bool open;
    public static bool blocking;                 // true while open and until the keys used to close it are released
    public static string title = "INVENTORY", money = "", status = "", note = "";
    public static float statusUntil;
    public static List<InvItem> items = new List<InvItem>();
    public static readonly List<string[]> pending = new List<string[]>();   // {"equip", kind, name, slotId} {"unequip", slotId} {"act", kind, name, action}
    public static string logTag = "InvLook";
    public static Action<string> log;

    // slot ids (what the slot is called) and the item kind each one takes; two gadget slots = the Shift and R skills
    public static readonly string[] SlotIds = { "rod", "skin", "bobber", "lure", "utility", "special", "pet", "charm" };
    public static readonly string[] SlotAccepts = { "rod", "skin", "bobber", "lure", "gadget", "gadget", "pet", "charm" };
    public static readonly string[] SlotNames = { "Rod", "Rod Skin", "Bobber", "Lure", "Shift \u2013 Utility", "R \u2013 Special", "Pet", "Charm" };
    public static readonly string[] SlotKinds = { "rod", "skin", "bobber", "lure", "gadget", "pet", "charm" };
    static readonly string[] TabNames = { "All", "Rods", "Skins", "Bobbers", "Lures", "Gadgets", "Pets", "Items", "Cases", "Fish" };
    static readonly string[] TabKinds = { null, "rod", "skin", "bobber", "lure", "gadget", "pet", "item", "case", "fish" };

    public static void Status(string s) { status = s; statusUntil = Time.unscaledTime + 6f; }
    public static bool IsSlotKind(string k) { return Array.IndexOf(SlotKinds, k) >= 0; }
    public static string SlotName(string k) { if (k == "gadget") return "Shift / R gadget"; int i = Array.IndexOf(SlotIds, k); return i >= 0 ? SlotNames[i] : k; }
    public static string AcceptOf(string slotId) { int i = Array.IndexOf(SlotIds, slotId); return i >= 0 ? SlotAccepts[i] : slotId; }

    public static void Open() { if (open) return; open = true; blocking = true; tab = Math.Max(0, tab); scroll = 0f; selected = null; press = null; dragging = false; Log("opened"); }
    public static void Close() { Close("code"); }
    public static void Close(string why) { if (!open) return; open = false; closedAt = Time.unscaledTime; press = null; dragging = false; Log("closed (" + why + ")"); }
    public static void Toggle() { if (open) Close(); else Open(); }

    static void Log(string s) { if (log != null) try { log(s); } catch { } }

    // ------------------------------------------------------------------ colours
    public static Color RarityColor(string r)
    {
        switch ((r ?? "").ToLowerInvariant())
        {
            case "blue": return Hex("#4b69ff");
            case "purple": return Hex("#8847ff");
            case "pink": return Hex("#d32ce6");
            case "red": return Hex("#eb4b4b");
            case "gold": return Hex("#ffd700");
            case "common": return Hex("#e8e8e8");
            case "uncommon": return Hex("#77d35a");
            case "rare": return Hex("#e5523f");
            case "legendary": case "boss": return Hex("#f2c14e");
            case "lunar": return Hex("#5ea8ff");
            case "default": case "": return Hex("#8b8f96");
        }
        return r.StartsWith("#") ? Hex(r) : Hex("#8b8f96");
    }
    public static string RarityLabel(string r)
    {
        switch ((r ?? "").ToLowerInvariant())
        {
            case "blue": return "Mil-Spec"; case "purple": return "Restricted"; case "pink": return "Classified";
            case "red": return "Covert"; case "gold": return "Rare Special"; case "common": return "Common";
            case "uncommon": return "Uncommon"; case "rare": return "Rare"; case "legendary": return "Legendary";
            case "boss": return "Boss"; case "lunar": return "Lunar"; case "default": case "": return "Starter";
        }
        return "";
    }
    public static Color Hex(string h)
    {
        Color c; return ColorUtility.TryParseHtmlString(h, out c) ? c : Color.gray;
    }
    // item colour for its icon (skins / bobbers look like what they are; others use the rarity)
    static readonly Dictionary<string, Color> tintCache = new Dictionary<string, Color>();
    static Color Tint(InvItem it)
    {
        string key = it.kind + "|" + it.name;
        Color c;
        if (tintCache.TryGetValue(key, out c)) return c;
        string n = (it.name ?? "").ToLowerInvariant();
        if (n.Contains("gold") || n.Contains("legendary") || n.Contains("crown") || n.Contains("kraken") || n.Contains("star")) c = Hex("#ffcf3a");
        else if (n.Contains("neon")) c = Hex("#33fff0");
        else if (n.Contains("void") || n.Contains("doppler")) c = Hex("#a45cff");
        else if (n.Contains("lunar") || n.Contains("glass") || n.Contains("moon") || n.Contains("ghost")) c = Hex("#9fd8ff");
        else if (n.Contains("bamboo") || n.Contains("sand") || n.Contains("cork")) c = Hex("#d9c27a");
        else if (n.Contains("carbon") || n.Contains("night") || n.Contains("eight")) c = Hex("#5a5d66");
        else if (n.Contains("dragon") || n.Contains("safari") || n.Contains("frost") || n.Contains("swamp")) c = Hex("#5fd18a");
        else if (n.Contains("howl") || n.Contains("redline") || n.Contains("fire")) c = Hex("#ff7a2e");
        else if (n.Contains("fade") || n.Contains("bubble") || n.Contains("koi")) c = Hex("#ff8fd0");
        else if (n.Contains("duck")) c = Hex("#ffd21a");
        else if (n.Contains("skull") || n.Contains("eyeball")) c = Hex("#eee6cc");
        else if (n.Contains("glow") || n.Contains("lantern") || n.Contains("firefly") || n.Contains("flashlight")) c = Hex("#fff3a0");
        else if (n == "default" || n.Contains("basic")) c = Hex("#a4703a");
        else if (n == "plain" || n.Contains("classic")) c = Hex("#ff4a4a");
        else c = Color.Lerp(RarityColor(it.rarity), Color.white, 0.35f);
        tintCache[key] = c;
        return c;
    }

    // ------------------------------------------------------------------ textures (main thread, OnGUI only)
    static Texture2D white;
    static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
    static Texture2D roundTile, ring;

    static Texture2D Icon(string kind)
    {
        Texture2D t;
        if (icons.TryGetValue(kind ?? "", out t) && t != null) return t;
        t = DrawIcon(kind ?? "");
        icons[kind ?? ""] = t;
        return t;
    }

    // signed-distance helpers in a 0..1 box (y up)
    static float Seg(float px, float py, float ax, float ay, float bx, float by, float r)
    {
        float dx = bx - ax, dy = by - ay, t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
        float qx = ax + t * dx - px, qy = ay + t * dy - py;
        return Mathf.Sqrt(qx * qx + qy * qy) - r;
    }
    static float Circ(float px, float py, float cx, float cy, float r) { return Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - r; }
    static float Ell(float px, float py, float cx, float cy, float rx, float ry) { float x = (px - cx) / rx, y = (py - cy) / ry; return (Mathf.Sqrt(x * x + y * y) - 1f) * Mathf.Min(rx, ry); }
    static float Box(float px, float py, float cx, float cy, float hx, float hy) { float dx = Mathf.Abs(px - cx) - hx, dy = Mathf.Abs(py - cy) - hy; return Mathf.Max(dx, dy); }
    static float Ring(float px, float py, float cx, float cy, float r, float w) { return Mathf.Abs(Circ(px, py, cx, cy, r)) - w; }
    static float Tri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by), d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy), d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool inside = !(((d1 < 0) || (d2 < 0) || (d3 < 0)) && ((d1 > 0) || (d2 > 0) || (d3 > 0)));
        float e = Mathf.Min(Seg(px, py, ax, ay, bx, by, 0), Mathf.Min(Seg(px, py, bx, by, cx, cy, 0), Seg(px, py, cx, cy, ax, ay, 0)));
        return inside ? -e : e;
    }
    static float U(float a, float b) { return Mathf.Min(a, b); }
    static float Sub(float a, float b) { return Mathf.Max(a, -b); }

    static float Shape(string kind, float x, float y)
    {
        switch (kind)
        {
            case "rod":
                {
                    float d = Seg(x, y, 0.16f, 0.14f, 0.86f, 0.86f, 0.028f);                 // blank
                    d = U(d, Seg(x, y, 0.13f, 0.11f, 0.30f, 0.28f, 0.05f));                  // grip
                    d = U(d, Ring(x, y, 0.36f, 0.24f, 0.075f, 0.022f));                     // reel
                    d = U(d, Seg(x, y, 0.36f, 0.24f, 0.40f, 0.33f, 0.018f));
                    d = U(d, Seg(x, y, 0.86f, 0.86f, 0.86f, 0.42f, 0.008f));                 // line
                    d = U(d, Circ(x, y, 0.86f, 0.38f, 0.045f));                              // bobber
                    return d;
                }
            case "skin":
                {   // painter's palette
                    float d = Ell(x, y, 0.5f, 0.5f, 0.40f, 0.33f);
                    d = Sub(d, Circ(x, y, 0.66f, 0.38f, 0.07f));                             // thumb hole
                    foreach (var p in new[] { new Vector2(0.32f, 0.58f), new Vector2(0.47f, 0.70f), new Vector2(0.64f, 0.66f), new Vector2(0.30f, 0.40f) })
                        d = Sub(d, Ring(x, y, p.x, p.y, 0.055f, 0.012f));
                    return d;
                }
            case "bobber":
                {
                    float d = Circ(x, y, 0.5f, 0.44f, 0.26f);
                    d = Sub(d, Box(x, y, 0.5f, 0.47f, 0.4f, 0.025f));                         // band
                    d = U(d, Seg(x, y, 0.5f, 0.70f, 0.5f, 0.88f, 0.03f));                     // stick
                    return d;
                }
            case "lure":
                {
                    float d = Ell(x, y, 0.48f, 0.58f, 0.26f, 0.12f);                          // body
                    d = U(d, Tri(x, y, 0.70f, 0.58f, 0.86f, 0.70f, 0.86f, 0.46f));           // tail
                    d = Sub(d, Circ(x, y, 0.33f, 0.61f, 0.03f));                             // eye
                    d = U(d, Ring(x, y, 0.42f, 0.30f, 0.11f, 0.02f) + (y > 0.32f && x > 0.42f ? 1f : 0f));   // hook
                    d = U(d, Seg(x, y, 0.53f, 0.30f, 0.53f, 0.46f, 0.02f));
                    d = U(d, Tri(x, y, 0.31f, 0.30f, 0.27f, 0.40f, 0.36f, 0.36f));          // barb
                    return d;
                }
            case "gadget":
                {   // compass
                    float d = Ring(x, y, 0.5f, 0.5f, 0.33f, 0.04f);
                    d = U(d, Tri(x, y, 0.5f, 0.80f, 0.43f, 0.5f, 0.57f, 0.5f));
                    d = U(d, Ring(x, y, 0.5f, 0.5f, 0.0f, 0.0f) + 1f);
                    d = U(d, Tri(x, y, 0.5f, 0.20f, 0.45f, 0.5f, 0.55f, 0.5f) + 0.012f);
                    d = U(d, Seg(x, y, 0.5f, 0.86f, 0.5f, 0.92f, 0.04f));
                    return d;
                }
            case "pet":
                {   // drone
                    float d = Box(x, y, 0.5f, 0.48f, 0.17f, 0.11f) - 0.04f;
                    d = Sub(d, Circ(x, y, 0.5f, 0.48f, 0.06f));
                    d = U(d, Circ(x, y, 0.5f, 0.48f, 0.03f));
                    d = U(d, Seg(x, y, 0.30f, 0.56f, 0.20f, 0.68f, 0.025f)); d = U(d, Seg(x, y, 0.70f, 0.56f, 0.80f, 0.68f, 0.025f));
                    d = U(d, Ell(x, y, 0.18f, 0.72f, 0.14f, 0.035f)); d = U(d, Ell(x, y, 0.82f, 0.72f, 0.14f, 0.035f));
                    d = U(d, Seg(x, y, 0.42f, 0.36f, 0.38f, 0.24f, 0.02f)); d = U(d, Seg(x, y, 0.58f, 0.36f, 0.62f, 0.24f, 0.02f));
                    return d;
                }
            case "charm":
                {   // four-leaf clover
                    float d = Circ(x, y, 0.5f, 0.68f, 0.14f);
                    d = U(d, Circ(x, y, 0.5f, 0.36f, 0.14f)); d = U(d, Circ(x, y, 0.34f, 0.52f, 0.14f)); d = U(d, Circ(x, y, 0.66f, 0.52f, 0.14f));
                    d = U(d, Seg(x, y, 0.56f, 0.40f, 0.70f, 0.12f, 0.025f));
                    return d;
                }
            case "case":
                {
                    float d = Box(x, y, 0.5f, 0.42f, 0.34f, 0.22f) - 0.03f;
                    d = Sub(d, Box(x, y, 0.5f, 0.50f, 0.36f, 0.012f));
                    d = U(d, Box(x, y, 0.5f, 0.47f, 0.05f, 0.05f));
                    d = U(d, Sub(Box(x, y, 0.5f, 0.71f, 0.12f, 0.07f) - 0.02f, Box(x, y, 0.5f, 0.69f, 0.07f, 0.04f)));
                    return d;
                }
            case "fish":
                {
                    float d = Ell(x, y, 0.44f, 0.5f, 0.30f, 0.17f);
                    d = U(d, Tri(x, y, 0.68f, 0.5f, 0.90f, 0.68f, 0.90f, 0.32f));
                    d = Sub(d, Circ(x, y, 0.27f, 0.54f, 0.035f));
                    d = Sub(d, Seg(x, y, 0.38f, 0.40f, 0.38f, 0.60f, 0.012f));
                    return d;
                }
            case "item":
                {   // closed book with a bookmark
                    float d = Box(x, y, 0.52f, 0.5f, 0.27f, 0.34f) - 0.02f;
                    d = Sub(d, Box(x, y, 0.30f, 0.5f, 0.012f, 0.36f));                       // spine line
                    d = Sub(d, Box(x, y, 0.56f, 0.62f, 0.13f, 0.035f));                      // title plate
                    d = U(d, Tri(x, y, 0.66f, 0.18f, 0.74f, 0.18f, 0.70f, 0.08f));          // ribbon tail
                    d = U(d, Box(x, y, 0.70f, 0.22f, 0.04f, 0.06f));
                    return d;
                }
            case "figure":
                {   // paper-doll angler silhouette
                    float d = Circ(x, y, 0.5f, 0.86f, 0.085f);                                // head
                    d = U(d, Seg(x, y, 0.5f, 0.70f, 0.5f, 0.42f, 0.11f));                     // torso
                    d = U(d, Seg(x, y, 0.40f, 0.70f, 0.24f, 0.48f, 0.04f)); d = U(d, Seg(x, y, 0.60f, 0.70f, 0.76f, 0.48f, 0.04f));   // arms
                    d = U(d, Seg(x, y, 0.45f, 0.36f, 0.40f, 0.06f, 0.05f)); d = U(d, Seg(x, y, 0.55f, 0.36f, 0.60f, 0.06f, 0.05f));   // legs
                    d = U(d, Ell(x, y, 0.5f, 0.94f, 0.13f, 0.025f));                          // hat brim
                    return d;
                }
        }
        return Circ(x, y, 0.5f, 0.5f, 0.3f);
    }

    static Texture2D DrawIcon(string kind)
    {
        int S = kind == "figure" ? 192 : 96;
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
        var px = new Color32[S * S];
        float aa = 1.4f / S;
        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++)
            {
                float d = Shape(kind, (i + 0.5f) / S, (j + 0.5f) / S);
                float a = Mathf.Clamp01(0.5f - d / aa);
                px[j * S + i] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        t.SetPixels32(px); t.Apply(false, true);
        return t;
    }

    static Texture2D Round(int S, float rad, bool outline)
    {
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[S * S];
        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++)
            {
                float x = Mathf.Abs(i + 0.5f - S / 2f) - (S / 2f - rad), y = Mathf.Abs(j + 0.5f - S / 2f) - (S / 2f - rad);
                float d = Mathf.Sqrt(Mathf.Max(x, 0) * Mathf.Max(x, 0) + Mathf.Max(y, 0) * Mathf.Max(y, 0)) + Mathf.Min(Mathf.Max(x, y), 0) - rad;
                float a = Mathf.Clamp01(0.5f - d);
                if (outline) a *= Mathf.Clamp01(d + 2.5f);
                px[j * S + i] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        t.SetPixels32(px); t.Apply(false, true);
        return t;
    }

    static void EnsureTex()
    {
        if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
        if (roundTile == null) roundTile = Round(64, 10f, false);
        if (ring == null) ring = Round(64, 10f, true);
    }

    // ------------------------------------------------------------------ drawing helpers
    static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = o; }
    static void RoundFill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, roundTile, ScaleMode.StretchToFill, true, 0, c, 0, 10f * sc); GUI.color = o; }
    static void Border(Rect r, Color c, float w) { Fill(new Rect(r.x, r.y, r.width, w), c); Fill(new Rect(r.x, r.yMax - w, r.width, w), c); Fill(new Rect(r.x, r.y, w, r.height), c); Fill(new Rect(r.xMax - w, r.y, w, r.height), c); }
    static void Tex(Rect r, Texture2D t, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, t, ScaleMode.ScaleToFit, true); GUI.color = o; }
    static void Panel(Rect r, Color bg, Color edge)
    {
        var o = GUI.color;
        GUI.color = bg; GUI.DrawTexture(r, white);
        GUI.color = o;
        Border(r, edge, Mathf.Max(1f, 1.5f * sc));
    }

    static GUIStyle sTitle, sText, sSmall, sTiny, sCenter, sTab, sBtn, sTipName, sTipText, sBadge;
    static int styleH;
    static float sc = 1f;
    static void Styles()
    {
        if (sTitle != null && styleH == Screen.height) return;
        styleH = Screen.height;
        int fs = Mathf.RoundToInt(15 * sc);
        sText = new GUIStyle(GUI.skin.label) { richText = true, fontSize = fs, wordWrap = false, clipping = TextClipping.Clip };
        sText.normal.textColor = new Color(0.9f, 0.92f, 0.95f);
        sTitle = new GUIStyle(sText) { fontSize = Mathf.RoundToInt(22 * sc), fontStyle = FontStyle.Bold };
        sSmall = new GUIStyle(sText) { fontSize = Mathf.RoundToInt(13 * sc) };
        sTiny = new GUIStyle(sText) { fontSize = Mathf.RoundToInt(11 * sc), alignment = TextAnchor.UpperCenter, wordWrap = true };
        sCenter = new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
        sTab = new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        sBtn = new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        sTipName = new GUIStyle(sText) { fontSize = Mathf.RoundToInt(16 * sc), fontStyle = FontStyle.Bold, wordWrap = true };
        sTipText = new GUIStyle(sSmall) { wordWrap = true };
        sBadge = new GUIStyle(sTiny) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold, wordWrap = false };
    }

    // ------------------------------------------------------------------ interaction state
    static int tab;
    static float scroll, closedAt = -100f, lastToggle = -100f;
    static InvItem press, selected; static Vector2 pressPos; static bool dragging;
    static string hoverSlot; static InvItem hoverItem; static Rect hoverRect;
    static Rect win;

    public static bool KeyToggleOk() { return Time.unscaledTime - lastToggle > 0.25f; }
    public static void MarkToggle() { lastToggle = Time.unscaledTime; }

    static List<InvItem> TabItems(int t)
    {
        var l = new List<InvItem>();
        foreach (InvItem it in items)
        {
            if (TabKinds[t] == null) { if (it.kind != "fish") l.Add(it); }
            else if (it.kind == TabKinds[t]) l.Add(it);
        }
        return l;
    }
    static InvItem EquippedIn(string slotId)
    {
        string k = AcceptOf(slotId);
        foreach (InvItem it in items) if (it.kind == k && it.equipped && (k != "gadget" || it.slot == slotId)) return it;
        return null;
    }

    // ------------------------------------------------------------------ OnGUI (call from a MonoBehaviour)
    public static void OnGUI()
    {
        if (!open) return;
        try { Draw(); }
        catch (Exception e) { Log("draw error: " + e); }
    }

    static void Draw()
    {
        EnsureTex();
        sc = Mathf.Max(0.75f, Screen.height / 1080f);
        Styles();
        GUI.depth = -50;
        Event e = Event.current;
        Vector2 m = e.mousePosition;

        float W = Mathf.Min(1180f * sc, Screen.width - 40f), H = Mathf.Min(700f * sc, Screen.height - 120f * sc);
        win = new Rect(Mathf.Round((Screen.width - W) / 2f), Mathf.Round(Mathf.Max(Screen.height * 0.12f, (Screen.height - H) / 2f)), W, H);
        float pad = 14f * sc;

        // keys (Esc / I close); the owner opens with I and filters chat typing
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.I) && KeyToggleOk()) { MarkToggle(); Close(e.keyCode + " key"); e.Use(); return; }

        // backdrop + window
        Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f));
        Panel(win, new Color(0.075f, 0.085f, 0.11f, 0.97f), new Color(0.35f, 0.75f, 1f, 0.55f));
        Fill(new Rect(win.x, win.y, win.width, 3f * sc), new Color(0.35f, 0.8f, 1f, 1f));

        // title bar
        GUI.Label(new Rect(win.x + pad, win.y + 8f * sc, W * 0.6f, 32f * sc), "<color=#66ccff>" + title + "</color>", sTitle);
        var moneyStyle = new GUIStyle(sTitle) { alignment = TextAnchor.UpperRight, fontSize = Mathf.RoundToInt(18 * sc) };
        GUI.Label(new Rect(win.xMax - 360f * sc - 44f * sc, win.y + 11f * sc, 360f * sc, 30f * sc), money, moneyStyle);
        var closeR = new Rect(win.xMax - 38f * sc, win.y + 9f * sc, 28f * sc, 28f * sc);
        bool overClose = closeR.Contains(m);
        RoundFill(closeR, overClose ? new Color(0.85f, 0.25f, 0.25f, 0.9f) : new Color(1f, 1f, 1f, 0.08f));
        GUI.Label(closeR, "<b>X</b>", sBtn);
        if (e.type == EventType.MouseDown && e.button == 0 && overClose) { e.Use(); Close("X button"); return; }

        float top = win.y + 48f * sc, bottom = win.yMax - 40f * sc;
        var left = new Rect(win.x + pad, top, W * 0.37f, bottom - top);
        var right = new Rect(left.xMax + pad, top, win.xMax - pad - left.xMax - pad, bottom - top);

        hoverSlot = null; hoverItem = null;
        DrawDoll(left, e, m);
        DrawGrid(right, e, m);

        // footer
        string st = Time.unscaledTime < statusUntil && !string.IsNullOrEmpty(status) ? status : null;
        GUI.Label(new Rect(win.x + pad, win.yMax - 32f * sc, W - 2 * pad, 24f * sc),
            st != null ? "<color=#ffe08a>" + st + "</color>" : "<color=#8a93a3>Click an item or drag it onto its slot to equip  •  Right-click a slot to unequip  •  I / Esc closes" + (note.Length > 0 ? "  •  " + note : "") + "</color>", sSmall);

        // drag ghost
        if (dragging && press != null)
        {
            float s = 64f * sc;
            var g = new Rect(m.x - s / 2, m.y - s / 2, s, s);
            RoundFill(g, Color.Lerp(RarityColor(press.rarity), Color.black, 0.55f) * new Color(1, 1, 1, 0.85f));
            Tex(new Rect(g.x + 8 * sc, g.y + 8 * sc, s - 16 * sc, s - 16 * sc), Icon(press.kind), Tint(press));
        }
        else if (hoverItem != null) Tooltip(hoverItem, m, hoverSlot);
        else if (hoverSlot != null) Tooltip(null, m, hoverSlot);

        // mouse release anywhere ends a drag
        if (e.type == EventType.MouseUp && e.button == 0 && press != null)
        {
            if (dragging) Log("drop of " + press.name + " outside a matching slot");
            press = null; dragging = false;
        }
        if (e.type == EventType.MouseDrag && press != null && !dragging && (m - pressPos).magnitude > 6f) dragging = true;
        if (dragging && (e.type == EventType.MouseDrag || e.type == EventType.MouseMove)) e.Use();
        // swallow every other click inside the window
        if (win.Contains(m) && (e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.ScrollWheel)) e.Use();
    }

    // ------------------------------------------------------------------ paper doll
    static void DrawDoll(Rect r, Event e, Vector2 m)
    {
        Panel(r, new Color(0.10f, 0.115f, 0.15f, 1f), new Color(1f, 1f, 1f, 0.07f));
        GUI.Label(new Rect(r.x + 10 * sc, r.y + 6 * sc, r.width, 22 * sc), "<b>EQUIPPED</b>", sSmall);
        float S = Mathf.Min(92f * sc, (r.height - 40 * sc - (selected != null ? 92 * sc : 0)) / 4f - 38 * sc);
        float rowH = S + 38f * sc;
        float y0 = r.y + 34f * sc;
        float lx = r.x + 18f * sc, rx = r.xMax - 18f * sc - S;
        // figure between the columns
        float fw = rx - (lx + S) - 16 * sc, fh = rowH * 4f - 38f * sc;
        var fig = new Rect(lx + S + 8 * sc, y0, fw, fh);
        Tex(fig, Icon("figure"), new Color(0.35f, 0.75f, 1f, 0.16f));
        Rect[] slots = new Rect[8];
        for (int i = 0; i < 4; i++) slots[i] = new Rect(lx, y0 + i * rowH, S, S);
        for (int i = 0; i < 4; i++) slots[4 + i] = new Rect(rx, y0 + i * rowH, S, S);
        // connector lines from the figure to the slots (thin, subtle)
        for (int i = 0; i < 8; i++)
        {
            Rect s = slots[i];
            float cy = s.center.y;
            if (i < 4) Fill(new Rect(s.xMax, cy, fig.x + fw * 0.30f - s.xMax, 1f), new Color(0.35f, 0.75f, 1f, 0.12f));
            else Fill(new Rect(fig.x + fw * 0.70f, cy, s.x - (fig.x + fw * 0.70f), 1f), new Color(0.35f, 0.75f, 1f, 0.12f));
        }
        for (int i = 0; i < 8; i++) Slot(slots[i], SlotIds[i], SlotNames[i], e, m);

        // details of the selected fish / case under the doll
        if (selected != null)
        {
            var d = new Rect(r.x + 10 * sc, r.yMax - 86f * sc, r.width - 20 * sc, 78f * sc);
            Panel(d, new Color(0.14f, 0.16f, 0.2f, 1f), RarityColor(selected.rarity) * new Color(1, 1, 1, 0.8f));
            GUI.Label(new Rect(d.x + 8 * sc, d.y + 4 * sc, d.width - 16 * sc, 22 * sc), "<b><color=#" + ColorUtility.ToHtmlStringRGB(RarityColor(selected.rarity)) + ">" + selected.name + "</color></b>" + (selected.count > 0 ? "  x" + selected.count : ""), sText);
            float bx = d.x + 8 * sc, by = d.y + 34 * sc, bw = (d.width - 16 * sc - 2 * 8 * sc) / 3f;
            if (selected.actions != null)
                for (int k = 0; k < selected.actions.Length && k < 3; k++)
                {
                    var b = new Rect(bx + k * (bw + 8 * sc), by, bw, 34 * sc);
                    bool over = b.Contains(m);
                    RoundFill(b, over ? new Color(0.35f, 0.75f, 1f, 0.9f) : new Color(0.35f, 0.75f, 1f, 0.25f));
                    GUI.Label(b, selected.actions[k], sBtn);
                    if (over && e.type == EventType.MouseDown && e.button == 0)
                    {
                        e.Use();
                        lock (pending) pending.Add(new[] { "act", selected.kind, selected.name, selected.actions[k] });
                        Status(selected.actions[k] + ": " + selected.name + " ...");
                        Log("action " + selected.actions[k] + " " + selected.name);
                    }
                }
            else GUI.Label(new Rect(bx, by, d.width - 16 * sc, 30 * sc), "<color=#8a93a3>" + selected.desc + "</color>", sSmall);
        }
    }

    static void Slot(Rect s, string slotId, string label, Event e, Vector2 m)
    {
        string kind = AcceptOf(slotId);
        InvItem eq = EquippedIn(slotId);
        bool over = s.Contains(m);
        bool match = dragging && press != null && press.kind == kind;
        Color edge = match ? new Color(0.4f, 1f, 0.5f, over ? 1f : 0.7f) : over ? new Color(0.35f, 0.75f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.12f);
        Color bg = eq != null ? Color.Lerp(RarityColor(eq.rarity), new Color(0.06f, 0.07f, 0.09f), 0.78f) : new Color(0.06f, 0.07f, 0.09f, 1f);
        RoundFill(s, bg);
        if (eq != null) Fill(new Rect(s.x + 6 * sc, s.yMax - 4 * sc, s.width - 12 * sc, 3 * sc), RarityColor(eq.rarity));
        var o = GUI.color; GUI.color = edge; GUI.DrawTexture(s, ring, ScaleMode.StretchToFill, true); GUI.color = o;
        float ip = s.width * 0.18f;
        var ir = new Rect(s.x + ip, s.y + ip, s.width - 2 * ip, s.height - 2 * ip);
        if (eq != null) Tex(ir, Icon(kind), Tint(eq));
        else Tex(ir, Icon(kind), new Color(1f, 1f, 1f, 0.10f));
        if (eq != null && eq.stat) GUI.Label(new Rect(s.x + 5 * sc, s.y + 3 * sc, s.width, 16 * sc), "<color=#ff9933><b>ST</b></color>", sTiny.Left());
        GUI.Label(new Rect(s.x - 20 * sc, s.yMax + 2 * sc, s.width + 40 * sc, 16 * sc), "<color=#7f8899>" + label.ToUpperInvariant() + "</color>", sTiny);
        GUI.Label(new Rect(s.x - 26 * sc, s.yMax + 14 * sc, s.width + 52 * sc, 16 * sc), eq != null ? "<b>" + eq.name + "</b>" : "<color=#5a6170>empty</color>", sTiny);
        if (!over) return;
        hoverSlot = slotId; hoverItem = eq;
        if (e.type == EventType.MouseDown && e.button == 1)
        {
            e.Use();
            if (eq == null || eq.isDefault) { Status(label + ": nothing to take off."); return; }
            lock (pending) pending.Add(new[] { "unequip", slotId });
            Status("Unequipping " + eq.name + " ...");
            Log("unequip " + kind + " (" + eq.name + ")");
        }
        else if (e.type == EventType.MouseUp && e.button == 0 && dragging && press != null)
        {
            InvItem it = press; press = null; dragging = false; e.Use();
            if (it.kind != kind) { Status(it.name + " goes in the " + SlotName(it.kind) + " slot."); return; }
            Equip(it, slotId);
        }
    }

    static void Equip(InvItem it, string slotId)
    {
        if (!IsSlotKind(it.kind)) { selected = it; return; }
        if (slotId == null && it.kind == "gadget")
        {   // click: the gadget's own key slot, else the first free one, else Shift
            slotId = it.prefSlot;
            if (slotId == null) slotId = EquippedIn("utility") == null ? "utility" : EquippedIn("special") == null ? "special" : "utility";
        }
        if (slotId == null) slotId = it.kind;
        if (it.equipped && (it.kind != "gadget" || it.slot == slotId)) { Status(it.name + " is already equipped."); return; }
        lock (pending) pending.Add(new[] { "equip", it.kind, it.name, slotId });
        Status("Equipping " + it.name + " ...");
        Log("equip " + it.kind + " " + it.name);
    }

    // ------------------------------------------------------------------ item grid
    static void DrawGrid(Rect r, Event e, Vector2 m)
    {
        // tabs
        float th = 30f * sc, tx = r.x;
        float tw = r.width / TabNames.Length;
        for (int t = 0; t < TabNames.Length; t++)
        {
            int n = TabItems(t).Count;
            var tr = new Rect(tx + t * tw, r.y, tw - 3 * sc, th);
            bool over = tr.Contains(m), on = t == tab;
            RoundFill(tr, on ? new Color(0.35f, 0.75f, 1f, 0.85f) : over ? new Color(1f, 1f, 1f, 0.14f) : new Color(1f, 1f, 1f, 0.06f));
            GUI.Label(tr, (on ? "<color=#08131c>" : "<color=#cfd6e0>") + TabNames[t] + (n > 0 && t > 0 ? " <size=" + Mathf.RoundToInt(10 * sc) + ">" + n + "</size>" : "") + "</color>", sTab);
            if (over && e.type == EventType.MouseDown && e.button == 0) { tab = t; scroll = 0f; e.Use(); }
        }
        var view = new Rect(r.x, r.y + th + 8 * sc, r.width, r.height - th - 8 * sc);
        Panel(view, new Color(0.10f, 0.115f, 0.15f, 1f), new Color(1f, 1f, 1f, 0.07f));
        var list = TabItems(tab);
        if (list.Count == 0)
        {
            string why = TabKinds[tab] == "case" ? "No unopened cases. Cases from the Fish Merchant open straight away; their skins land in Skins / Bobbers."
                : TabKinds[tab] == "fish" ? "No fish yet. Go catch some!"
                : TabKinds[tab] == "lure" ? "No lures. Buy them with !buylure <name> (!lures for the list)."
                : TabKinds[tab] == "pet" ? "No drone pet. !pet buys one ($3,000)."
                : TabKinds[tab] == "item" ? "No usable items yet (the Angler's Guide is sold at the rod shop stall)."
                : TabKinds[tab] == "gadget" ? "No gadgets yet."
                : TabKinds[tab] == "charm" ? "No charms yet." : "Nothing here yet.";
            GUI.Label(new Rect(view.x + 20 * sc, view.y + 20 * sc, view.width - 40 * sc, 60 * sc), "<color=#8a93a3>" + why + "</color>", sCenter);
            return;
        }
        float T = 78f * sc, gap = 9f * sc, ip = 10f * sc;
        int cols = Mathf.Max(1, (int)((view.width - ip * 2 + gap) / (T + gap)));
        float rowH = T + 22f * sc + gap;
        int rows = (list.Count + cols - 1) / cols;
        float content = rows * rowH + ip * 2, maxScroll = Mathf.Max(0f, content - view.height);
        if (e.type == EventType.ScrollWheel && view.Contains(m)) { scroll = Mathf.Clamp(scroll + e.delta.y * 28f * sc, 0f, maxScroll); e.Use(); }
        scroll = Mathf.Clamp(scroll, 0f, maxScroll);
        float x0 = view.x + ip + (view.width - ip * 2 - (cols * (T + gap) - gap)) / 2f;
        GUI.BeginGroup(view);
        for (int i = 0; i < list.Count; i++)
        {
            InvItem it = list[i];
            var g = new Rect(x0 + (i % cols) * (T + gap), view.y + ip + (i / cols) * rowH - scroll, T, T);   // global
            if (g.yMax + 22 * sc < view.y || g.y > view.yMax) continue;
            var loc = new Rect(g.x - view.x, g.y - view.y, T, T);
            bool over = g.Contains(m) && view.Contains(m) && !dragging;
            Color rc = RarityColor(it.rarity);
            RoundFill(loc, Color.Lerp(rc, new Color(0.05f, 0.06f, 0.08f), over ? 0.62f : 0.74f));
            Fill(new Rect(loc.x + 6 * sc, loc.yMax - 4 * sc, loc.width - 12 * sc, 3 * sc), rc);
            var o = GUI.color;
            GUI.color = it.equipped ? new Color(1f, 0.85f, 0.3f, 1f) : it == selected ? new Color(0.35f, 0.75f, 1f, 1f) : over ? new Color(1f, 1f, 1f, 0.5f) : new Color(1f, 1f, 1f, 0.10f);
            GUI.DrawTexture(loc, ring, ScaleMode.StretchToFill, true); GUI.color = o;
            float p = T * 0.17f;
            Tex(new Rect(loc.x + p, loc.y + p * 0.8f, T - 2 * p, T - 2 * p), Icon(it.kind), Tint(it));
            if (it.equipped) GUI.Label(new Rect(loc.x + 5 * sc, loc.y + 3 * sc, T, 16 * sc), "<color=#ffd24a><b>E</b></color>", sTiny.Left());
            if (it.stat) GUI.Label(new Rect(loc.x, loc.y + 3 * sc, T - 5 * sc, 16 * sc), "<color=#ff9933><b>ST</b></color>", sTiny.Right());
            string badge = it.count > 1 || (it.kind == "fish" && it.count > 0) ? "x" + it.count : BadgeOf(it);
            if (badge.Length > 0) GUI.Label(new Rect(loc.x, loc.y, T - 6 * sc, T - 6 * sc), "<color=#ffffff>" + badge + "</color>", sBadge);
            GUI.Label(new Rect(loc.x - 6 * sc, loc.yMax + 2 * sc, T + 12 * sc, 18 * sc), Short(it.name), sTiny);
            if (!over) continue;
            hoverItem = it; hoverRect = g;
            if (e.type == EventType.MouseDown && e.button == 0) { press = it; pressPos = m; dragging = false; e.Use(); }
            else if (e.type == EventType.MouseUp && e.button == 0 && press == it && !dragging)
            {
                press = null; e.Use();
                if (IsSlotKind(it.kind)) { selected = null; Equip(it, null); }
                else selected = it == selected ? null : it;
            }
        }
        GUI.EndGroup();
        if (maxScroll > 0)
        {
            float bh = view.height * view.height / content, by = view.y + (view.height - bh) * (scroll / maxScroll);
            Fill(new Rect(view.xMax - 5 * sc, by, 3 * sc, bh), new Color(1f, 1f, 1f, 0.25f));
        }
    }

    static string BadgeOf(InvItem it)
    {
        if (it.kind != "lure" || string.IsNullOrEmpty(it.extra)) return "";
        int sp = it.extra.IndexOf(' ');
        return sp > 0 ? it.extra.Substring(0, sp) : it.extra;
    }
    static string Short(string n)
    {
        if (n == null) return "";
        n = n.Replace("StatTrak™ ", "");
        return n.Length > 16 ? n.Substring(0, 15) + "…" : n;
    }

    static void Tooltip(InvItem it, Vector2 m, string slot)
    {
        float w = 290f * sc;
        string name, body;
        Color rc;
        if (it == null)
        {
            name = SlotName(slot) + " slot"; rc = new Color(0.6f, 0.65f, 0.75f);
            body = "<color=#8a93a3>Empty. Drag a " + KindLabel(AcceptOf(slot)).ToLowerInvariant() + " here, or click one in the grid." + (AcceptOf(slot) == "gadget" ? " The " + (slot == "utility" ? "Shift (Utility)" : "R (Special)") + " key uses what is equipped here." : "") + "</color>";
        }
        else
        {
            rc = RarityColor(it.rarity);
            name = it.name;
            string rl = RarityLabel(it.rarity);
            body = (rl.Length > 0 ? "<color=#" + ColorUtility.ToHtmlStringRGB(rc) + ">" + rl + "</color>  " : "") + "<color=#8a93a3>" + KindLabel(it.kind) + "</color>"
                 + (it.desc.Length > 0 ? "\n" + it.desc : "")
                 + (it.extra.Length > 0 ? "\n<color=#ffcf6a>" + it.extra + "</color>" : "")
                 + (it.count > 0 && it.kind == "fish" ? "\n<color=#cfd6e0>You have " + it.count + "</color>" : "")
                 + "\n<color=#6f7a8c>" + (slot != null ? (it.isDefault ? "Starter item" : "Right-click to unequip") : it.equipped ? "Equipped" + (it.slot == "utility" ? " on Shift" : it.slot == "special" ? " on R" : "") : IsSlotKind(it.kind) ? "Click or drag to the " + (it.kind == "gadget" ? (it.prefSlot == "special" ? "R \u2013 Special" : it.prefSlot == "utility" ? "Shift \u2013 Utility" : "Shift or R") : SlotName(it.kind)) + " slot" : it.actions != null ? "Click for " + string.Join(" / ", it.actions) : "") + "</color>";
        }
        float h1 = sTipName.CalcHeight(new GUIContent(name), w - 20 * sc), h2 = sTipText.CalcHeight(new GUIContent(body), w - 20 * sc);
        float h = h1 + h2 + 18 * sc;
        float x = m.x + 18 * sc, y = m.y + 14 * sc;
        if (x + w > Screen.width - 4) x = m.x - w - 12 * sc;
        if (y + h > Screen.height - 4) y = Screen.height - 4 - h;
        var r = new Rect(x, y, w, h);
        Panel(r, new Color(0.04f, 0.045f, 0.06f, 0.97f), rc * new Color(1, 1, 1, 0.9f));
        Fill(new Rect(r.x, r.y, r.width, 3 * sc), rc);
        GUI.Label(new Rect(r.x + 10 * sc, r.y + 7 * sc, w - 20 * sc, h1), "<color=#" + ColorUtility.ToHtmlStringRGB(Color.Lerp(rc, Color.white, 0.25f)) + ">" + name + "</color>", sTipName);
        GUI.Label(new Rect(r.x + 10 * sc, r.y + 9 * sc + h1, w - 20 * sc, h2), body, sTipText);
    }

    static string KindLabel(string k)
    {
        switch (k)
        {
            case "rod": return "Rod"; case "skin": return "Rod skin"; case "bobber": return "Bobber"; case "lure": return "Lure";
            case "gadget": return "Gadget"; case "pet": return "Drone pet"; case "charm": return "Charm"; case "case": return "Case"; case "fish": return "Fish"; case "item": return "Item";
        }
        return k;
    }

    static GUIStyle Left(this GUIStyle s) { return new GUIStyle(s) { alignment = TextAnchor.UpperLeft }; }
    static GUIStyle Right(this GUIStyle s) { return new GUIStyle(s) { alignment = TextAnchor.UpperRight }; }

    // ------------------------------------------------------------------ input blocking (reflection: RoR2 + Rewired)
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type luMgrType, ctrlType;
    static bool blocked, cursorAdded;
    static object blockEs;
    static GameObject focusGo;
    static readonly List<object> disabledMaps = new List<object>();
    static int blockErrors;

    static object Get(object o, string n)
    {
        if (o == null) return null;
        Type t = o.GetType();
        PropertyInfo p = t.GetProperty(n, All); if (p != null) return p.GetValue(o, null);
        FieldInfo f = t.GetField(n, All); return f != null ? f.GetValue(o) : null;
    }
    static bool IsNull(object o) { return o == null || (o is UnityEngine.Object && (UnityEngine.Object)o == null); }

    static bool KeysHeld()
    {
        foreach (int vk in new[] { 0x01, 0x02, 0x1B, 0x49 }) if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    // call every frame from the owner's update (main thread)
    public static void InputTick()
    {
        if (open) { Block(); blocking = true; return; }
        if (blocked && (Time.unscaledTime - closedAt > 1.5f || !KeysHeld())) Unblock();
        blocking = blocked || Time.unscaledTime - closedAt < 0.15f;
    }

    static void Types()
    {
        if (luMgrType != null) return;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            string an = a.GetName().Name;
            if (an == "RoR2") luMgrType = a.GetType("RoR2.LocalUserManager");
            else if (an == "Rewired_Core") ctrlType = a.GetType("Rewired.ControllerType");
        }
    }

    static void Block()
    {
        try
        {
            Types();
            if (focusGo == null) { focusGo = new GameObject("InventoryFocus"); UnityEngine.Object.DontDestroyOnLoad(focusGo); }
            object lu = luMgrType == null ? null : luMgrType.GetMethod("GetFirstLocalUser", All).Invoke(null, null);
            if (lu == null) return;
            object es = Get(lu, "eventSystem");
            if (!IsNull(es))
            {
                object cur = Get(es, "currentSelectedGameObject");
                if (!ReferenceEquals(cur, focusGo))
                    es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) }).Invoke(es, new object[] { focusGo });
                if (!cursorAdded)
                {
                    PropertyInfo p = es.GetType().GetProperty("cursorOpenerCount", All);
                    if (p != null) { p.SetValue(es, (int)p.GetValue(es, null) + 1, null); cursorAdded = true; blockEs = es; }
                }
            }
            object maps = Get(Get(Get(lu, "inputPlayer"), "controllers"), "maps");
            if (maps != null && ctrlType != null)
            {
                MethodInfo getAll = null;
                foreach (MethodInfo mi in maps.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (mi.Name == "GetAllMaps" && !mi.IsGenericMethodDefinition && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == ctrlType) { getAll = mi; break; }
                if (getAll != null)
                    foreach (string ct in new[] { "Keyboard", "Mouse" })
                    {
                        IEnumerable l = getAll.Invoke(maps, new object[] { Enum.Parse(ctrlType, ct) }) as IEnumerable;
                        if (l == null) continue;
                        foreach (object map in l)
                        {
                            PropertyInfo en = map.GetType().GetProperty("enabled", All);
                            if (en != null && (bool)en.GetValue(map, null)) { en.SetValue(map, false, null); disabledMaps.Add(map); }
                        }
                    }
            }
            if (!blocked) Log("input blocked (cursor=" + cursorAdded + ", maps off=" + disabledMaps.Count + ")");
            blocked = true;
        }
        catch (Exception ex) { if (blockErrors++ < 5) Log("block error: " + (ex.InnerException ?? ex)); blocked = true; }
    }

    public static void Unblock()
    {
        int n = disabledMaps.Count;
        foreach (object map in disabledMaps) try { map.GetType().GetProperty("enabled", All).SetValue(map, true, null); } catch { }
        disabledMaps.Clear();
        try
        {
            if (cursorAdded && !IsNull(blockEs))
            {
                PropertyInfo p = blockEs.GetType().GetProperty("cursorOpenerCount", All);
                p.SetValue(blockEs, Math.Max(0, (int)p.GetValue(blockEs, null) - 1), null);
            }
            object lu = luMgrType == null ? null : luMgrType.GetMethod("GetFirstLocalUser", All).Invoke(null, null);
            object es = lu == null ? null : Get(lu, "eventSystem");
            if (!IsNull(es) && ReferenceEquals(Get(es, "currentSelectedGameObject"), focusGo))
                es.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) }).Invoke(es, new object[] { null });
        }
        catch (Exception ex) { if (blockErrors++ < 5) Log("unblock error: " + (ex.InnerException ?? ex)); }
        if (blocked) Log("input restored (" + n + " maps back on)");
        cursorAdded = false; blockEs = null; blocked = false;
    }
}
