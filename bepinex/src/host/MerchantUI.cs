// MerchantUI - on-screen parts of the fish merchant (IMGUI), shared by the host add-on and FISHING.exe:
//   * the trade menu while you stand at the merchant (number keys 1-9 / 0 = back, Shift+number = sell all; or click when
//     the cursor is free). Keys are ignored while a text field (the chat box) is focused.
//   * the CS:GO-style case reel: a strip of item cards scrolls fast and slows down over ~5.5 s with ticks, lands on the
//     won item under the marker, glows in its rarity colour, then shows the item name / wear / duplicate payout.
//   * small toasts at the top for other players' big unboxings.
// The host/client plug in the data sources and the send/play callbacks (see the Func/Action fields).
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class MerchantUI
{
    // ---- plugged in by the host add-on / guest client
    public static Func<string> me;
    public static Func<string, int> fishCount;
    public static Func<int> money;
    public static Func<List<string>> myItems;                 // owned case-table items (names)
    public static Func<string, float> wearOf;                 // my wear of an item
    public static Func<string> equippedSkin, equippedBobber;
    public static Action<string> send;                        // "sell|<species>|<n|all>", "trade|<caseIdx>", "buy|<caseIdx>", "equip|<item>"
    public static Action<string> play;
    public static Action<string> log;
    public static Func<string> statLine;                      // HUD line for the local player's equipped StatTrak rod (or null)
    public static bool near;                                  // set every frame: local player within trade range
    public static string status; public static float statusUntil;

    static bool dismissed, wasNear;
    static int page, skinPage;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // ---------------------------------------------------------------- reel
    class Reel
    {
        public string player, item, caseName; public int rarity, seed, dup, caseIdx; public float wear, start, finalPos; public bool landed, test;
        public List<string> strip = new List<string>(); public int lastIdx = -1; public float lastTick;
    }
    static Reel reel;
    public const float ReelSeconds = 5.5f, ResultSeconds = 4.5f;
    const int WinIndex = 38, Cards = 44;
    static readonly System.Random rng = new System.Random();
    public static bool ReelBusy { get { return reel != null && Time.realtimeSinceStartup - reel.start < ReelSeconds + 0.5f; } }

    class Toast { public string text; public Color col; public float until; }
    static readonly List<Toast> toasts = new List<Toast>();

    static void Snd(string s) { if (play != null) try { play(s); } catch { } }
    static void L(string s) { if (log != null) try { log(s); } catch { } }
    public static Color RarityColor(int r) { var c = CaseData.RarityRgb[Mathf.Clamp(r, 0, 4)]; return new Color(c[0], c[1], c[2]); }

    public static void StartReel(string player, int caseIdx, string item, int rarity, float wear, int seed, int dup, bool test)
    {
        var r = new Reel { player = player, item = item, rarity = rarity, wear = wear, seed = seed, dup = dup, caseIdx = caseIdx, test = test, start = Time.realtimeSinceStartup };
        r.caseName = caseIdx >= 0 && caseIdx < CaseData.Cases.Length ? CaseData.Cases[caseIdx].name : "Case";
        for (int i = 0; i < Cards; i++)
        {
            if (i == WinIndex) { r.strip.Add(item); continue; }
            // teaser strip: the case's odds with the rare tiers boosted a little (looks like the real thing)
            double x = rng.NextDouble(); int rr = 0; double acc = 0;
            double[] o = CaseData.Cases[Mathf.Clamp(caseIdx, 0, 2)].odds; double[] w = { o[0], o[1] * 1.6, o[2] * 2.5, o[3] * 4, o[4] * 5 }; double sum = 0; foreach (double d in w) sum += d;
            for (int k = 0; k < 5; k++) { acc += w[k] / sum; if (x < acc) { rr = k; break; } }
            r.strip.Add(CaseData.RollItemOfRarity(rng, rr).name);
        }
        float stride = CardW + Gap;
        r.finalPos = WinIndex * stride - (float)(rng.NextDouble() * 0.84 - 0.42) * CardW;
        reel = r;
        Snd("Play_UI_chest_unlock");
        L("reel start " + player + " " + item + " r" + rarity + " w" + wear + (test ? " TEST" : ""));
    }

    public static void AddToast(string text, Color c) { toasts.Add(new Toast { text = text, col = c, until = Time.realtimeSinceStartup + 7f }); if (toasts.Count > 4) toasts.RemoveAt(0); }
    public static void SetStatus(string s) { status = s; statusUntil = Time.realtimeSinceStartup + 6f; }

    // ---------------------------------------------------------------- chat focus (don't steal number keys while typing)
    static Type esType; static FieldInfo esList; static PropertyInfo esSelected;
    public static bool TextFocused()
    {
        try
        {
            if (esType == null)
                foreach (var a in FishingScope.Assemblies()) { Type t = a.GetType("UnityEngine.EventSystems.EventSystem"); if (t != null) { esType = t; break; } }
            if (esType == null) return false;
            if (esList == null) esList = esType.GetField("m_EventSystems", All);
            if (esSelected == null) esSelected = esType.GetProperty("currentSelectedGameObject", All);
            var list = esList != null ? esList.GetValue(null) as System.Collections.IList : null;
            if (list == null) return false;
            foreach (object es in list)
            {
                var go = esSelected.GetValue(es, null) as GameObject;
                if (go == null) continue;
                foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.Contains("InputField")) return true;
            }
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------------- drawing helpers
    static readonly Dictionary<int, Texture2D> solid = new Dictionary<int, Texture2D>();
    static Texture2D Solid(Color c)
    {
        int k = ((int)(c.r * 255) << 24) ^ ((int)(c.g * 255) << 16) ^ ((int)(c.b * 255) << 8) ^ (int)(c.a * 255);
        Texture2D t;
        if (solid.TryGetValue(k, out t) && t != null) return t;
        t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        solid[k] = t; return t;
    }
    static void Fill(Rect r, Color c) { GUI.DrawTexture(r, Solid(c)); }
    static void Frame(Rect r, Color c, float w) { Fill(new Rect(r.x, r.y, r.width, w), c); Fill(new Rect(r.x, r.yMax - w, r.width, w), c); Fill(new Rect(r.x, r.y, w, r.height), c); Fill(new Rect(r.xMax - w, r.y, w, r.height), c); }
    static GUIStyle lab, labC, small, smallC, big, row;
    static float S { get { return Mathf.Clamp(Screen.height / 1080f, 0.7f, 2f); } }
    const float CardW = 150f, Gap = 8f, CardH = 150f;
    static void Styles()
    {
        if (lab != null) return;
        lab = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, wordWrap = false };
        labC = new GUIStyle(lab) { alignment = TextAnchor.MiddleCenter };
        small = new GUIStyle(lab) { fontSize = 13 };
        smallC = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
        big = new GUIStyle(labC) { fontSize = 26, fontStyle = FontStyle.Bold };
        row = new GUIStyle(GUI.skin.button) { richText = true, alignment = TextAnchor.MiddleLeft, fontSize = 15 };
        row.normal.background = Solid(new Color(0.12f, 0.14f, 0.18f, 0.92f)); row.hover.background = Solid(new Color(0.18f, 0.3f, 0.34f, 0.95f)); row.active.background = Solid(new Color(0.1f, 0.45f, 0.45f, 1f));
        row.normal.textColor = row.hover.textColor = row.active.textColor = Color.white;
    }

    // ---------------------------------------------------------------- OnGUI entry (call from a MonoBehaviour's OnGUI)
    public static void OnGUI()
    {
        try
        {
            Styles();
            var m = GUI.matrix;
            float s = S;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float W = Screen.width / s, H = Screen.height / s;
            DrawToasts(W);
            string st = null; try { st = statLine != null ? statLine() : null; } catch { }
            if (st != null)
            {
                var sr = new Rect(24f, H - 150f, 520f, 26f);
                Fill(sr, new Color(0f, 0f, 0f, 0.55f)); Fill(new Rect(sr.x, sr.y, 4, sr.height), new Color(0.81f, 0.42f, 0.2f));
                GUI.Label(new Rect(sr.x + 10, sr.y + 2, sr.width - 12, 22), st, small);
            }
            if (near != wasNear) { wasNear = near; if (!near) { dismissed = false; page = 0; } }
            if (near && !dismissed) DrawMenu(W, H);
            if (reel != null) DrawReel(W, H);
            GUI.matrix = m;
        }
        catch (Exception e) { if (errs++ < 5) L("OnGUI error: " + e); }
    }
    static int errs;

    static void DrawToasts(float W)
    {
        float now = Time.realtimeSinceStartup, y = 70f;
        for (int i = toasts.Count - 1; i >= 0; i--) if (toasts[i].until < now) toasts.RemoveAt(i);
        foreach (var t in toasts)
        {
            var r = new Rect(W / 2 - 330, y, 660, 34);
            Fill(r, new Color(0f, 0f, 0f, 0.65f)); Fill(new Rect(r.x, r.yMax - 3, r.width, 3), t.col);
            GUI.Label(r, t.text, labC); y += 40;
        }
    }

    // ---------------------------------------------------------------- menu
    static int KeyPressed(out bool shift)
    {
        shift = false;
        Event e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return -1;
        int n = -1;
        if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha9) n = e.keyCode - KeyCode.Alpha0;
        else if (e.keyCode >= KeyCode.Keypad0 && e.keyCode <= KeyCode.Keypad9) n = e.keyCode - KeyCode.Keypad0;
        if (n < 0) return -1;
        if (TextFocused()) return -1;
        shift = e.shift;
        e.Use();
        return n;
    }

    static string Me() { try { return me != null ? me() : null; } catch { return null; } }
    static int Fish(string sp) { try { return fishCount != null ? fishCount(sp) : 0; } catch { return 0; } }
    static int Money() { try { return money != null ? money() : 0; } catch { return 0; } }
    static void Send(string a) { L("menu -> " + a); if (send != null) try { send(a); } catch (Exception e) { L("send error " + e.Message); } Snd("Play_UI_menuClick"); }

    static void DrawMenu(float W, float H)
    {
        bool shift; int key = KeyPressed(out shift);
        var lines = new List<KeyValuePair<string, Action>>();
        string title = "FISH MERCHANT", hint = "";
        int mny = Money();
        bool busy = ReelBusy;
        switch (page)
        {
            case 0:
                lines.Add(new KeyValuePair<string, Action>("Sell fish", () => page = 1));
                lines.Add(new KeyValuePair<string, Action>("Trade fish for a case", () => page = 2));
                lines.Add(new KeyValuePair<string, Action>("Buy a case", () => page = 3));
                lines.Add(new KeyValuePair<string, Action>("My case skins", () => { page = 4; skinPage = 0; }));
                hint = "Number keys or click. 0 = close.  Chat: !sell <fish>, !trade <fish>, !case";
                break;
            case 1:
                title = "SELL FISH";
                foreach (string sp in CaseData.AllFish)
                {
                    int n = Fish(sp); if (n <= 0) continue;
                    string spc = sp; int price = CaseData.SellPrice(sp);
                    lines.Add(new KeyValuePair<string, Action>(sp + "  <color=#aaaaaa>x" + n + "</color>   <color=#ffd700>" + CaseData.Money(price) + "</color> each",
                        () => Send("sell|" + spc + "|" + (Event.current != null && Event.current.shift ? "all" : "1"))));
                    if (lines.Count >= 9) break;
                }
                hint = lines.Count == 0 ? "You have no fish to sell. Go catch some!" : "Number = sell one, Shift+number = sell all of that fish. 0 = back.";
                break;
            case 2:
                title = "TRADE FISH FOR A CASE";
                for (int c = 0; c < 3; c++)
                {
                    int have = 0; foreach (string sp in CaseData.AllFish) if (CaseData.FishFits(c, sp)) have += Fish(sp);
                    int ci = c; bool ok = have >= CaseData.FishNeeded(c) && !busy;
                    lines.Add(new KeyValuePair<string, Action>((ok ? "" : "<color=#888888>") + CaseData.Cases[c].name + "  <-  " + CaseData.Cases[c].fishText + "  (you have " + have + ")" + (ok ? "" : "</color>"),
                        () => Send("trade|" + ci)));
                }
                hint = "The case opens right away. Odds: !merchant in chat. 0 = back.";
                break;
            case 3:
                title = "BUY A CASE";
                for (int c = 0; c < 3; c++)
                {
                    int ci = c; bool ok = mny >= CaseData.Cases[c].price && !busy;
                    lines.Add(new KeyValuePair<string, Action>((ok ? "" : "<color=#888888>") + CaseData.Cases[c].name + "   " + CaseData.Money(CaseData.Cases[c].price) + (ok ? "" : "</color>"), () => Send("buy|" + ci)));
                }
                hint = "Rare Special (gold) odds: 0.26% / 1% / 2.5%. Duplicates are paid out. 0 = back.";
                break;
            case 4:
                {
                    title = "MY CASE SKINS";
                    List<string> items = null; try { items = myItems != null ? myItems() : null; } catch { }
                    items = items ?? new List<string>();
                    items.Sort((a, b) => { var ia = CaseData.Find(a); var ib = CaseData.Find(b); int ra = ia != null ? ia.rarity : 0, rb = ib != null ? ib.rarity : 0; return rb != ra ? rb.CompareTo(ra) : string.Compare(a, b); });
                    int per = 8, pages = Math.Max(1, (items.Count + per - 1) / per);
                    if (skinPage >= pages) skinPage = 0;
                    string es = equippedSkin != null ? equippedSkin() : null, eb = equippedBobber != null ? equippedBobber() : null;
                    for (int i = skinPage * per; i < Math.Min(items.Count, (skinPage + 1) * per); i++)
                    {
                        var it = CaseData.Find(items[i]); string nm = items[i];
                        float w = wearOf != null ? wearOf(nm) : -1f;
                        string col = it != null ? CaseData.RarityHex[it.rarity] : "#ffffff";
                        bool eq = nm == es || nm == eb;
                        lines.Add(new KeyValuePair<string, Action>("<color=" + col + ">" + nm + "</color>" + (it != null && it.kind == "bobber" ? " bobber" : "") + (w >= 0 ? "  <color=#aaaaaa>" + CaseData.WearName(w) + "</color>" : "") + (eq ? "  <color=#ffd700>[equipped]</color>" : ""),
                            () => Send("equip|" + nm)));
                    }
                    if (pages > 1) lines.Add(new KeyValuePair<string, Action>("More... (page " + (skinPage + 1) + "/" + pages + ")", () => skinPage = (skinPage + 1) % pages));
                    hint = items.Count == 0 ? "Nothing unboxed yet." : "Number = equip. Everyone sees your rod. 0 = back.";
                }
                break;
        }
        float w0 = 470f, lh = 30f;
        float h0 = 70f + lines.Count * (lh + 4) + 50f;
        var box = new Rect(24f, H * 0.3f, w0, h0);
        Fill(box, new Color(0.03f, 0.05f, 0.08f, 0.88f)); Frame(box, new Color(0.2f, 0.95f, 0.9f, 0.9f), 2f);
        GUI.Label(new Rect(box.x + 14, box.y + 8, w0 - 28, 28), "<b><color=#55eedd>" + title + "</color></b>", lab);
        GUI.Label(new Rect(box.x + 14, box.y + 8, w0 - 28, 28), "<color=#ffd700>" + CaseData.Money(mny) + "</color>", new GUIStyle(lab) { alignment = TextAnchor.UpperRight });
        float y = box.y + 44;
        for (int i = 0; i < lines.Count; i++)
        {
            int num = i + 1;
            if (GUI.Button(new Rect(box.x + 12, y, w0 - 24, lh), "  <b>" + num + "</b>   " + lines[i].Key, row)) lines[i].Value();
            if (key == num) lines[i].Value();
            y += lh + 4;
        }
        if (busy) GUI.Label(new Rect(box.x + 14, y, w0 - 28, 20), "<color=#ffd700>Opening a case...</color>", small);
        else if (status != null && Time.realtimeSinceStartup < statusUntil) GUI.Label(new Rect(box.x + 14, y, w0 - 28, 20), status, small);
        GUI.Label(new Rect(box.x + 14, box.yMax - 28, w0 - 28, 22), "<color=#99aaaa>" + hint + "</color>", small);
        if (key == 0) { if (page == 0) dismissed = true; else page = 0; Snd("Play_UI_menuBack"); }
    }

    // ---------------------------------------------------------------- reel drawing
    static float Ease(float u) { return 1f - Mathf.Pow(1f - Mathf.Clamp01(u), 4f); }

    static void DrawReel(float W, float H)
    {
        Reel r = reel;
        float t = Time.realtimeSinceStartup - r.start;
        if (t > ReelSeconds + ResultSeconds + 0.6f) { reel = null; return; }
        float alpha = t > ReelSeconds + ResultSeconds ? Mathf.Clamp01(1f - (t - ReelSeconds - ResultSeconds) / 0.6f) : 1f;
        var old = GUI.color; GUI.color = new Color(1, 1, 1, alpha);
        float stride = CardW + Gap;
        float pw = Mathf.Min(W - 40f, 1000f), ph = 300f;
        var panel = new Rect((W - pw) / 2f, H * 0.22f, pw, ph);
        Fill(panel, new Color(0.02f, 0.03f, 0.05f, 0.9f)); Frame(panel, new Color(0.3f, 0.3f, 0.35f, 1f), 2f);
        GUI.Label(new Rect(panel.x, panel.y + 6, pw, 26), "<b>" + (r.player ?? "") + "</b> is opening a <b><color=#ffd700>" + r.caseName + "</color></b>" + (r.test ? "  <color=#ff8888>(TEST)</color>" : ""), labC);
        float u = Mathf.Clamp01(t / ReelSeconds);
        float pos = Ease(u) * r.finalPos;
        float cx = panel.x + pw / 2f, top = panel.y + 42f;
        var clip = new Rect(panel.x + 6, top - 4, pw - 12, CardH + 8);
        GUI.BeginGroup(clip);
        for (int i = 0; i < r.strip.Count; i++)
        {
            float x = (cx - clip.x) + i * stride - pos - CardW / 2f;
            if (x > clip.width || x + CardW < 0) continue;
            DrawCard(new Rect(x, 4, CardW, CardH), r.strip[i], i == WinIndex ? r.wear : 0.2f, i == WinIndex ? r.seed : i * 37, i == WinIndex && t >= ReelSeconds, t);
        }
        GUI.EndGroup();
        // marker
        Fill(new Rect(cx - 1.5f, top - 8, 3, CardH + 16), new Color(1f, 0.85f, 0.2f, 1f));
        Fill(new Rect(cx - 7, top - 10, 14, 4), new Color(1f, 0.85f, 0.2f, 1f)); Fill(new Rect(cx - 7, top + CardH + 6, 14, 4), new Color(1f, 0.85f, 0.2f, 1f));
        // edge fade
        for (int k = 0; k < 12; k++) { float a = 0.85f * (1f - k / 12f); Fill(new Rect(clip.x + k * 6, clip.y, 6, clip.height), new Color(0.02f, 0.03f, 0.05f, a)); Fill(new Rect(clip.xMax - (k + 1) * 6, clip.y, 6, clip.height), new Color(0.02f, 0.03f, 0.05f, a)); }
        // ticks
        int idx = Mathf.FloorToInt((pos + stride / 2f) / stride);
        if (idx != r.lastIdx && u < 1f) { r.lastIdx = idx; if (Time.realtimeSinceStartup - r.lastTick > 0.04f) { r.lastTick = Time.realtimeSinceStartup; Snd("Play_UI_menuHover"); } }
        if (u >= 1f && !r.landed)
        {
            r.landed = true;
            switch (r.rarity)
            {
                case 0: Snd("Play_UI_item_land_tier1"); break;
                case 1: Snd("Play_UI_item_land_tier2"); break;
                case 2: Snd("Play_UI_item_land_tier3"); break;
                case 3: Snd("Play_UI_item_land_tier3"); Snd("Play_UI_achievementUnlock"); break;
                default: Snd("Play_UI_achievementUnlock_enhanced"); Snd("Play_UI_lunarCache_open"); break;
            }
            if (r.dup > 0) Snd("Play_UI_coin");
        }
        if (t >= ReelSeconds)
        {
            Color rc = RarityColor(r.rarity);
            string extra = PatternName(r.item, r.seed);
            GUI.Label(new Rect(panel.x, top + CardH + 14, pw, 34), "<color=#" + ColorUtility.ToHtmlStringRGB(rc) + ">" + r.item + (extra != null ? " | " + extra : "") + "</color>", big);
            GUI.Label(new Rect(panel.x, top + CardH + 50, pw, 22), "<color=#" + ColorUtility.ToHtmlStringRGB(rc) + ">" + CaseData.RarityNames[Mathf.Clamp(r.rarity, 0, 4)] + "</color>   " + CaseData.WearName(r.wear) + " <color=#999999>(" + CaseData.F4(r.wear) + ")</color>"
                + (r.dup > 0 ? "   <color=#ffd700>Duplicate - the merchant pays you " + CaseData.Money(r.dup) + "</color>" : "   <color=#88ff88>NEW!</color>"), labC);
        }
        GUI.color = old;
    }

    public static string PatternName(string item, int seed)
    {
        item = CaseData.Base(item);
        if (item == "★ Doppler") { switch (seed % 4) { case 0: return "Ruby"; case 1: return "Sapphire"; case 2: return "Black Pearl"; default: return "Phase 2"; } }
        if (item == "Case Hardened" && seed == 661) return "Blue Gem";
        if (item == "Fade") return (80 + (seed % 100) / 5) + "% Fade";
        return null;
    }

    static void DrawCard(Rect c, string item, float wear, int seed, bool winner, float t)
    {
        var it = CaseData.Find(item);
        int rar = it != null ? it.rarity : 0;
        Color rc = RarityColor(rar);
        if (winner)
        {
            float g = 0.5f + 0.5f * Mathf.Sin(t * 6f);
            for (int k = 1; k <= 4; k++) Frame(new Rect(c.x - k * 2, c.y - k * 2, c.width + k * 4, c.height + k * 4), new Color(rc.r, rc.g, rc.b, (0.6f - k * 0.12f) * (0.6f + 0.4f * g)), 2f);
        }
        Fill(c, new Color(0.13f, 0.14f, 0.17f, 1f));
        Fill(new Rect(c.x, c.y, c.width, c.height * 0.62f), new Color(rc.r * 0.25f + 0.08f, rc.g * 0.25f + 0.08f, rc.b * 0.25f + 0.1f, 1f));
        var sw = CaseSkinLook.Swatch(item, wear, seed);
        if (sw != null) GUI.DrawTexture(new Rect(c.x + 5, c.y + 12, c.width - 10, (c.width - 10) * 72f / 160f), sw, ScaleMode.ScaleToFit, true);
        if (CaseData.IsStat(item)) GUI.Label(new Rect(c.x + 4, c.y + 2, c.width - 8, 16), "<color=#cf6a32>StatTrak™</color>", smallC);
        GUI.Label(new Rect(c.x + 4, c.y + c.height * 0.64f, c.width - 8, 36), "<b>" + CaseData.Base(item) + "</b>" + (it != null && it.kind == "bobber" ? "\n<color=#aaaaaa>bobber</color>" : "\n<color=#aaaaaa>rod skin</color>"), smallC);
        Fill(new Rect(c.x, c.yMax - 6, c.width, 6), rc);
    }
}
