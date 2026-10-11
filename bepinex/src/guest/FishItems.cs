// FishItems - "just fish" for the Risk of Rain 2 fishing session.
//   Every machine (host build FishItems<N>, guest build FishItemsClient inside FISHING.exe):
//     re-skins the 14 "fish carrier" items so they look like the fish in the item bar
//     (icon = fish picture on a rarity-tinted rounded tile, name/description = the fish).
//   HOST build only (#if HOST, server side):
//     - strips every player's items except the fish carriers (+ hidden helper items) at install and on each stage
//     - destroys item sources (chests, shops, printers, scrappers, shrines of chance/order, lunar pods, ...)
//       during the first 30 s of each stage, and non-fish item pickups on the ground for the whole stage.
// All Unity work happens on the main thread (RoR2Application.onUpdate / onFixedUpdate); Run() only subscribes.
// enabled = false restores the original icons/names and stops all removal.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

#if !OFF
#if HOST
public static class FishItems4
#else
public static class FishItemsClient
#endif
{
    public static bool enabled = true;
    public static int version = 3;   // 3 = mod 2.0; 2 = 14 species (Clownfish, Catfish, Pufferfish, Anglerfish, Ghost Fish)
#if HOST
    const string Me = "FishItems4";
    const bool IsHostBuild = true;
#else
    const string Me = "FishItemsClient";
    const bool IsHostBuild = false;
#endif
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ror2_fishitems.log");
    public static string savePreviewDir;   // host: set by Run() to a folder to dump the icon PNGs

    // ======================================================================================
    // FISH TABLES - extend here (a new species needs: a carrier item, a perk, and FishArt art for the icon)
    // --------------------------------------------------------------------------------------
    // species -> carrier item. Carriers are combat-only items (inert in peaceful mode) except the Moon Eel's
    // ExtraLife (real free revive) and the Old Boot's ScrapWhite (does nothing). The host also writes these
    // into the fishing mod's live Fishing<N>.fishCarrierItem so new catches give them.
    public static readonly Dictionary<string, string> desiredCarrier = new Dictionary<string, string> {
        { "Minnow", "Crowbar" }, { "Perch", "BleedOnHit" }, { "Trout", "StickyBomb" }, { "Bass", "IgniteOnKill" },
        { "Salmon", "StunChanceOnHit" }, { "Swordfish", "Missile" }, { "Golden Koi", "ChainLightning" },
        { "Moon Eel", "ExtraLife" }, { "Old Boot", "ScrapWhite" },
        { "Clownfish", "FireRing" }, { "Catfish", "NearbyDamageBonus" }, { "Pufferfish", "BounceNearby" },
        { "Anglerfish", "Behemoth" }, { "Ghost Fish", "ShockNearby" } };
    // the previous carriers (converted 1:1 to the new ones in players' inventories on the host)
    public static readonly Dictionary<string, string> oldCarrier = new Dictionary<string, string> {
        { "Minnow", "Hoof" }, { "Perch", "Syringe" }, { "Trout", "HealWhileSafe" }, { "Bass", "Bear" }, { "Salmon", "Feather" },
        { "Swordfish", "CritGlasses" }, { "Golden Koi", "Tooth" }, { "Moon Eel", "ExtraLife" }, { "Old Boot", "ScrapWhite" } };
    // combat-only stand-ins if a carrier item doesn't exist in this game version
    static readonly string[] substituteCarriers = { "BossDamageBonus", "IceRing", "SlowOnHit", "Thorns", "BleedOnHitAndExplode", "ExplodeOnDeath", "Dagger" };

    // FISH PERKS (movement only, per fish held, computed by each player's own game for its local player)
    //   move = +move speed fraction per fish, sprint = +sprint speed fraction per fish, jump = +jumpPower fraction per fish,
    //   jumpsPer = +1 extra jump per N fish (0 = none). Caps below.
    public class Perk
    {
        public float move, sprint, jump; public int jumpsPer; public string text;
        public Perk(float move, float sprint, float jump, int jumpsPer, string text) { this.move = move; this.sprint = sprint; this.jump = jump; this.jumpsPer = jumpsPer; this.text = text; }
    }
    public static readonly Dictionary<string, Perk> Perks = new Dictionary<string, Perk> {
        { "Minnow",     new Perk(0.02f, 0,     0,     0, "+2% move speed") },
        { "Perch",      new Perk(0,     0.02f, 0,     0, "+2% sprint speed") },
        { "Trout",      new Perk(0,     0,     0.03f, 0, "+3% jump height") },
        { "Bass",       new Perk(0.01f, 0,     0.01f, 0, "+1% move speed, +1% jump height") },
        { "Salmon",     new Perk(0,     0,     0,     3, "+1 extra jump per 3 Salmon") },
        { "Swordfish",  new Perk(0.05f, 0,     0,     0, "+5% move speed") },
        { "Golden Koi", new Perk(0,     0.04f, 0,     0, "+4% sprint speed, next 3 catches x2") },
        { "Moon Eel",   new Perk(0,     0,     0,     0, "free revive") },
        { "Old Boot",   new Perk(0,     0,     0,     0, "+2% fishing luck") },   // placebo (request #7)
        { "Clownfish",  new Perk(0,     0,     0.02f, 0, "+2% jump height") },
        { "Catfish",    new Perk(0.01f, 0.01f, 0,     0, "+1% move speed, +1% sprint speed") },
        { "Pufferfish", new Perk(0,     0,     0,     3, "+1 extra jump per 3 Pufferfish") },
        { "Anglerfish", new Perk(0,     0.06f, 0,     0, "+6% sprint speed") },
        { "Ghost Fish", new Perk(0.06f, 0,     0.06f, 0, "+6% move speed, +6% jump height") } };
    // keep runtime perk-text edits made to an older version (e.g. placebo texts), except obsolete defaults
    static void CarryPerkTexts(Type old)
    {
        try
        {
            FieldInfo pf = old.GetField("Perks", BindingFlags.Public | BindingFlags.Static);
            IDictionary od = pf == null ? null : pf.GetValue(null) as IDictionary;
            if (od == null) return;
            foreach (DictionaryEntry e in od)
            {
                string sp = e.Key as string; Perk mine;
                if (sp == null || e.Value == null || !Perks.TryGetValue(sp, out mine)) continue;
                FieldInfo tf = e.Value.GetType().GetField("text");
                string txt = tf == null ? null : tf.GetValue(e.Value) as string;
                if (string.IsNullOrEmpty(txt) || txt == mine.text || txt == "+4% sprint speed" || txt == "nothing") continue;
                Log("perk text carried over from " + old.Name + ": " + sp + " = " + txt + " (was " + mine.text + ")");
                mine.text = txt;
            }
        }
        catch (Exception ex) { Log("perk text carry-over: " + ex.Message); }
    }
    public const float MaxMove = 0.30f, MaxSprint = 0.30f, MaxJump = 0.30f; public const int MaxExtraJumps = 2;
    // ======================================================================================

    static readonly string[] Species = { "Minnow", "Perch", "Trout", "Bass", "Salmon", "Swordfish", "Golden Koi", "Moon Eel", "Old Boot",
                                         "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };
    static string PickupText(string sp) { Perk p; return Perks.TryGetValue(sp, out p) ? p.text : ""; }
    static string DescText(string sp)
    {
        Perk p; if (!Perks.TryGetValue(sp, out p)) return sp;
        if (p.move == 0 && p.sprint == 0 && p.jump == 0 && p.jumpsPer == 0)
            return sp + " — " + p.text + (sp == "Moon Eel" ? ": when you die it is consumed and you return to life." : ". (It's a boot.)");
        return sp + " — <style=cIsUtility>" + p.text + "</style> per fish. Fish movement perks stack up to +30% move speed, +30% sprint speed, +30% jump height and +2 jumps.";
    }
    // tile tint by fish rarity: 0 common (white), 1 uncommon (green), 2 rare (red), 3 lunar (blue), 4 junk (grey-brown)
    static readonly int[] Rarity = { 0, 0, 0, 1, 1, 2, 2, 3, 4, 0, 0, 1, 2, 3 };

    static Assembly ror2;
    static Type netServerType, itemCatalogType, itemIndexType, pickupCatalogType, languageType, itemIconType, rawImageType, stageType, runType;
    static bool hostSkip, installed, failed;
    static int errors;
    static float nextLangCheck, nextIconScan;

    static readonly Dictionary<string, string> carrier = new Dictionary<string, string>();   // species -> item name (applied)
    static readonly Texture2D[] tex = new Texture2D[14];
    static readonly Sprite[] spr = new Sprite[14];
    static int texNext;
    static bool skinned;
    static readonly Dictionary<int, int> skinnedItem = new Dictionary<int, int>();   // itemIndex -> species index
    class Orig { public object def; public Sprite icon; public string name, pickup, desc; public object pdef; public Sprite pIcon; public Texture pTex; public string pName, pPickup; public bool unhide; }
    static readonly List<Orig> originals = new List<Orig>();
    static readonly List<Component> icons = new List<Component>();
    static FieldInfo iconIndexField, iconImageField;
    static PropertyInfo rawTexProp;
    static bool loggedIcons;
    static int enforced, enforceSeen;
    static float nextEnforceLog = 30f;

    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + "[" + Me + "] " + s + Environment.NewLine); } catch { } }

    static object Get(object o, string name)
    {
        if (o == null) return null;
        Type t = o as Type ?? o.GetType(); object inst = o is Type ? null : o;
        PropertyInfo p = t.GetProperty(name, All);
        if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(inst, null);
        FieldInfo f = t.GetField(name, All);
        return f != null ? f.GetValue(inst) : null;
    }
    static void Set(object o, string name, object v)
    {
        FieldInfo f = o.GetType().GetField(name, All);
        if (f != null) { f.SetValue(o, v); return; }
        PropertyInfo p = o.GetType().GetProperty(name, All);
        if (p != null) p.SetValue(o, v, null);
    }
    static MethodInfo M(Type t, string name, params Type[] ps)
    {
        foreach (MethodInfo m in t.GetMethods(All))
        {
            if (m.Name != name) continue;
            ParameterInfo[] q = m.GetParameters();
            if (q.Length != ps.Length) continue;
            bool ok = true;
            for (int i = 0; i < q.Length; i++) if (ps[i] != null && q[i].ParameterType != ps[i]) ok = false;
            if (ok) return m;
        }
        return null;
    }
    static bool ServerActive() { try { return netServerType != null && (bool)Get(netServerType, "active"); } catch { return false; } }
    static object Idx(int i) { return Enum.ToObject(itemIndexType, i); }
    static int ItemCount() { return Convert.ToInt32(Get(itemCatalogType, "itemCount")); }
    static object ItemDef(int i) { return M(itemCatalogType, "GetItemDef", itemIndexType).Invoke(null, new[] { Idx(i) }); }
    static int FindItem(string n) { return Convert.ToInt32(M(itemCatalogType, "FindItemIndex", typeof(string)).Invoke(null, new object[] { n })); }
    static string ItemName(int i) { object d = ItemDef(i); return d == null ? "#" + i : (string)Get(d, "name"); }

    public static void Run()
    {
        try
        {
            foreach (var a in FishingScope.Assemblies())
            {
                if (a.GetName().Name == "RoR2") ror2 = a;
                Type t = a.GetType("UnityEngine.Networking.NetworkServer"); if (t != null) netServerType = t;
                t = a.GetType("UnityEngine.UI.RawImage"); if (t != null) rawImageType = t;
            }
            // newer versions of this mod replace older ones; a second copy of the same build does nothing
            foreach (var a in FishingScope.Assemblies())
            {
                Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                foreach (Type t in ts)
                {
                    if (t == null || t == typeof(
#if HOST
                        FishItems4
#else
                        FishItemsClient
#endif
                        ) || !Regex.IsMatch(t.Name, "^FishItems(\\d+|Client)$")) continue;
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en == null || !(bool)en.GetValue(null)) continue;
                    if (t.Name == Me)
                    {   // same build loaded again (guest re-ran FISHING.exe): keep the running copy unless this one is newer
                        FieldInfo vf = t.GetField("version", BindingFlags.Public | BindingFlags.Static);
                        int ov = vf != null ? Convert.ToInt32(vf.GetValue(null)) : 1;
                        if (ov >= version) { Log("already running - nothing changed"); return; }
                        en.SetValue(null, false); Log("replaced older copy (version " + ov + " -> " + version + ")");
                        continue;
                    }
                    if (IsHostBuild && t.Name != "FishItemsClient")
                    {
                        en.SetValue(null, false); Log("disabled older " + t.Name);
                        CarryPerkTexts(t);
#if HOST
                        FieldInfo an = t.GetField("announced", All); if (an != null && (bool)an.GetValue(null)) announced = true;
#endif
                    }
                    if (!IsHostBuild && t.Name != "FishItemsClient")
                    {   // a host build (FishItems<N>) is running here, so this game is the host (maybe still in the menu)
                        hostSkip = true; Log("host detected (" + t.Name + " is running) - the host runs its own FishItems mod, nothing changed"); return;
                    }
                }
            }
            if (!IsHostBuild && ServerActive()) { hostSkip = true; Log("host detected - the host runs its own FishItems mod, nothing changed"); return; }
            itemCatalogType = ror2.GetType("RoR2.ItemCatalog");
            itemIndexType = ror2.GetType("RoR2.ItemIndex");
            pickupCatalogType = ror2.GetType("RoR2.PickupCatalog");
            languageType = ror2.GetType("RoR2.Language");
            itemIconType = ror2.GetType("RoR2.UI.ItemIcon");
            stageType = ror2.GetType("RoR2.Stage");
            runType = ror2.GetType("RoR2.Run");
            if (itemIconType != null)
            {
                iconIndexField = itemIconType.GetField("_itemIndex", All);
                iconImageField = itemIconType.GetField("image", All);
            }
            if (rawImageType != null) rawTexProp = rawImageType.GetProperty("texture", All);
#if HOST
            savePreviewDir = null;   // BepInEx build: no PNG dumps (and Assembly.Location is empty for an embedded assembly)
#endif
            Type app = ror2.GetType("RoR2.RoR2Application");
            Action h = Tick;
            app.GetEvent("onFixedUpdate", All).AddEventHandler(null, h);
            EventInfo upd = app.GetEvent("onUpdate", All);
            if (upd != null && upd.EventHandlerType == typeof(Action)) { Action u = FrameUpdate; upd.AddEventHandler(null, u); }
            installed = true;
            Log("installed (host build=" + IsHostBuild + ", itemIcon=" + (itemIconType != null) + " rawImage=" + (rawImageType != null) + " onUpdate=" + (upd != null) + ")");
        }
        catch (Exception e) { Log("Run error: " + e); }
    }

    // ------------------------------------------------------------------ main-thread ticks
    static bool restored;
    static void Tick()
    {
        if (failed || hostSkip) return;
        // the guest copy was loaded at the menu of a game that then became the host: stop for good (the host's own
        // FishItems<N> does everything there; two copies would apply the movement perks twice)
        if (!IsHostBuild && ServerActive()) { hostSkip = true; Log("this game is now the host - guest copy stopped"); return; }
        try
        {
            if (!enabled) { if (!restored) { restored = true; Restore(); RestorePerks(); } return; }
            if (ItemCount() <= 0) return;   // catalog not ready yet
            if (texNext < Species.Length) { BuildIcon(texNext); texNext++; return; }
#if HOST
            if (Time.time >= nextCarrierCheck) { nextCarrierCheck = Time.time + 5f; CheckCarrierTable(); }
#endif
            if (!skinned) { if (carrier.Count == 0) foreach (var kv in ResolveCarriers()) carrier[kv.Key] = kv.Value; Apply(); }
            if (Time.time >= nextLangCheck) { nextLangCheck = Time.time + 3f; EnsureTokens(); }
            PerkTick();
#if HOST
            HostTick();
#endif
        }
        catch (Exception e) { if (++errors <= 15) Log("tick error: " + (e.InnerException ?? e)); if (errors > 300) { failed = true; Log("too many errors - stopped"); } }
    }

    static void FrameUpdate()
    {
        if (failed || hostSkip || !skinned) return;
        try { EnforceIcons(); }
        catch (Exception e) { if (++errors <= 15) Log("icon error: " + (e.InnerException ?? e)); }
    }

    // ------------------------------------------------------------------ icons
    static void BuildIcon(int i)
    {
        const int S = 128;
        float[] f = FishArt.Render(Species[i]);   // 128x64 straight-alpha RGBA, row 0 = top
        Color tint; Color edge;
        switch (Rarity[i])
        {
            case 1: tint = new Color(0.30f, 0.62f, 0.25f); edge = new Color(0.62f, 0.92f, 0.45f); break;
            case 2: tint = new Color(0.70f, 0.17f, 0.15f); edge = new Color(1.00f, 0.50f, 0.40f); break;
            case 3: tint = new Color(0.20f, 0.36f, 0.80f); edge = new Color(0.55f, 0.75f, 1.00f); break;
            case 4: tint = new Color(0.36f, 0.32f, 0.27f); edge = new Color(0.60f, 0.55f, 0.48f); break;
            default: tint = new Color(0.55f, 0.58f, 0.62f); edge = new Color(0.92f, 0.94f, 0.96f); break;
        }
        var cols = new Color32[S * S];
        const float inset = 4f, rad = 20f;
        // fish box: 116 x 58, centred
        const float fw = 116f, fh = 58f, fx0 = (S - fw) / 2f, fy0 = (S - fh) / 2f;
        for (int y = 0; y < S; y++)          // y = row from TOP
            for (int x = 0; x < S; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // rounded-square signed distance
                float qx = Math.Abs(px - S / 2f) - (S / 2f - inset - rad), qy = Math.Abs(py - S / 2f) - (S / 2f - inset - rad);
                float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                float d = (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - rad;
                float tileA = Mathf.Clamp01(0.5f - d);
                float g = 1.15f - 0.45f * (py / S);                  // lighter top, darker bottom
                float rx = (px - S / 2f) / (S / 2f), ry = (py - S * 0.42f) / (S / 2f);
                float glow = Mathf.Clamp01(1f - (float)Math.Sqrt(rx * rx + ry * ry)) * 0.35f;
                Color bg = new Color(tint.r * g + glow, tint.g * g + glow, tint.b * g + glow, 1f);
                float border = Mathf.Clamp01(1f - Math.Abs(d + 2.5f) / 1.6f);
                bg = Color.Lerp(bg, edge, border * 0.9f);
                // fish (bilinear, premultiplied), plus a soft drop shadow offset (+2,+3)
                float fa; Color fc = SampleFish(f, (px - fx0) * (128f / fw) - 0.5f, (py - fy0) * (64f / fh) - 0.5f, out fa);
                float sa; SampleFish(f, (px - 2f - fx0) * (128f / fw) - 0.5f, (py - 3f - fy0) * (64f / fh) - 0.5f, out sa);
                Color c = bg;
                c = Color.Lerp(c, new Color(0, 0, 0, 1), sa * 0.45f);
                c = Color.Lerp(c, new Color(fc.r, fc.g, fc.b, 1), fa);
                float a = Math.Max(tileA, fa);
                cols[(S - 1 - y) * S + x] = new Color32((byte)(Mathf.Clamp01(c.r) * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), (byte)(a * 255));
            }
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
        t.SetPixels32(cols);
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
        t.name = "FishIcon_" + Species[i].Replace(" ", "");
        t.Apply(false);
        tex[i] = t;
        Sprite s = Sprite.Create(t, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        s.name = "FishIcon_" + Species[i].Replace(" ", "");
        spr[i] = s;
        SavePng(t, Species[i]);
        if (i == Species.Length - 1) Log(Species.Length + " fish icons created (128x128)");
    }

    static Color SampleFish(float[] f, float u, float v, out float alpha)
    {
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float tx = u - x0, ty = v - y0;
        float r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int xx = x0 + i, yy = y0 + j;
                if (xx < 0 || yy < 0 || xx >= 128 || yy >= 64) continue;
                float w = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                int p = (yy * 128 + xx) * 4;
                float pa = f[p + 3] * w;
                r += f[p] * pa; g += f[p + 1] * pa; b += f[p + 2] * pa; a += pa;
            }
        alpha = Mathf.Clamp01(a);
        return a > 0.0001f ? new Color(r / a, g / a, b / a, 1) : new Color(0, 0, 0, 0);
    }

    static void SavePng(Texture2D t, string name)
    {
        if (savePreviewDir == null) return;
        try
        {
            Type ic = null;
            foreach (var a in FishingScope.Assemblies()) { Type x = a.GetType("UnityEngine.ImageConversion"); if (x != null) ic = x; }
            MethodInfo enc = ic == null ? null : ic.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });
            if (enc == null) { savePreviewDir = null; return; }
            Directory.CreateDirectory(savePreviewDir);
            File.WriteAllBytes(Path.Combine(savePreviewDir, name.Replace(" ", "") + ".png"), (byte[])enc.Invoke(null, new object[] { t }));
        }
        catch (Exception e) { Log("png save error: " + (e.InnerException ?? e).Message); savePreviewDir = null; }
    }

    // ------------------------------------------------------------------ re-skin
    static string Tok(string sp, string kind) { return "FISH_" + sp.ToUpper().Replace(" ", "") + "_" + kind; }

    static void Apply()
    {
        skinned = true; skinnedItem.Clear(); originals.Clear();
        var sb = new StringBuilder();
        for (int i = 0; i < Species.Length; i++)
        {
            string itemName; if (!carrier.TryGetValue(Species[i], out itemName)) continue;
            int idx = FindItem(itemName);
            if (idx < 0) { Log("carrier item not found: " + itemName); continue; }
            object def = ItemDef(idx);
            var o = new Orig { def = def, icon = (Sprite)Get(def, "pickupIconSprite"), name = (string)Get(def, "nameToken"), pickup = (string)Get(def, "pickupToken"), desc = (string)Get(def, "descriptionToken") };
            Set(def, "pickupIconSprite", spr[i]);
            Set(def, "nameToken", Tok(Species[i], "NAME"));
            Set(def, "pickupToken", Tok(Species[i], "PICKUP"));
            Set(def, "descriptionToken", Tok(Species[i], "DESC"));
            try
            {
                MethodInfo fp = M(pickupCatalogType, "FindPickupIndex", itemIndexType);
                object pi = fp.Invoke(null, new[] { Idx(idx) });
                object pdef = M(pickupCatalogType, "GetPickupDef", pi.GetType()).Invoke(null, new[] { pi });
                if (pdef != null)
                {
                    o.pdef = pdef; o.pIcon = (Sprite)Get(pdef, "iconSprite"); o.pTex = (Texture)Get(pdef, "iconTexture");
                    o.pName = (string)Get(pdef, "nameToken"); o.pPickup = (string)Get(pdef, "pickupToken");
                    Set(pdef, "iconSprite", spr[i]); Set(pdef, "iconTexture", tex[i]);
                    Set(pdef, "nameToken", Tok(Species[i], "NAME")); Set(pdef, "pickupToken", Tok(Species[i], "PICKUP"));
                }
            }
            catch (Exception e) { Log("pickupdef patch " + itemName + ": " + (e.InnerException ?? e).Message); }
            originals.Add(o);
            skinnedItem[idx] = i;
        }
        EnsureTokens();
        // verification: what the game now resolves for each carrier
        for (int i = 0; i < Species.Length; i++)
        {
            string itemName; if (!carrier.TryGetValue(Species[i], out itemName)) continue;
            int idx = FindItem(itemName); if (idx < 0) continue;
            object def = ItemDef(idx);
            Sprite s = (Sprite)Get(def, "pickupIconSprite");
            string nm = (string)M(languageType, "GetString", typeof(string)).Invoke(null, new object[] { (string)Get(def, "nameToken") });
            string pk = (string)M(languageType, "GetString", typeof(string)).Invoke(null, new object[] { (string)Get(def, "pickupToken") });
            sb.Append("\n   " + itemName + " (#" + idx + ") -> icon=" + (s != null ? s.name : "null") + " name='" + nm + "' pickup='" + pk + "' hidden=" + Get(def, "hidden"));
        }
        int bd = FindItem("BoostDamage");
        if (bd >= 0)
        {
            object bdef = ItemDef(bd);
            bool wasHidden = (bool)Get(bdef, "hidden");
            if (!wasHidden) { Set(bdef, "hidden", true); originals.Add(new Orig { def = bdef, unhide = true }); }
            sb.Append("\n   BoostDamage hidden: was " + wasHidden + ", now " + Get(bdef, "hidden"));
        }
        Log("re-skinned " + skinnedItem.Count + " carrier items:" + sb);
        RefreshDisplays();
    }

    static void EnsureTokens()
    {
        object cur = Get(languageType, "currentLanguage");
        object eng = Get(languageType, "english");
        foreach (object lang in new[] { cur, eng })
        {
            if (lang == null) continue;
            MethodInfo getTok = M(languageType, "GetLocalizedStringByToken", typeof(string));
            if (getTok != null && (string)getTok.Invoke(lang, new object[] { Tok("Minnow", "PICKUP") }) == PickupText("Minnow")
                && (string)getTok.Invoke(lang, new object[] { Tok("Old Boot", "DESC") }) == DescText("Old Boot")
                && (string)getTok.Invoke(lang, new object[] { Tok("Ghost Fish", "DESC") }) == DescText("Ghost Fish")
                && (string)getTok.Invoke(lang, new object[] { Tok("Golden Koi", "PICKUP") }) == PickupText("Golden Koi")) continue;
            MethodInfo set = M(languageType, "SetStringByToken", typeof(string), typeof(string));
            foreach (string sp in Species)
            {
                set.Invoke(lang, new object[] { Tok(sp, "NAME"), sp });
                set.Invoke(lang, new object[] { Tok(sp, "PICKUP"), PickupText(sp) });
                set.Invoke(lang, new object[] { Tok(sp, "DESC"), DescText(sp) });
            }
            Log("registered fish tokens in language '" + Get(lang, "name") + "'");
        }
    }

    static void Restore()
    {
        foreach (Orig o in originals)
        {
            try
            {
                if (o.unhide) { Set(o.def, "hidden", false); continue; }
                Set(o.def, "pickupIconSprite", o.icon); Set(o.def, "nameToken", o.name); Set(o.def, "pickupToken", o.pickup); Set(o.def, "descriptionToken", o.desc);
                if (o.pdef != null) { Set(o.pdef, "iconSprite", o.pIcon); Set(o.pdef, "iconTexture", o.pTex); Set(o.pdef, "nameToken", o.pName); Set(o.pdef, "pickupToken", o.pPickup); }
            }
            catch (Exception e) { Log("restore error: " + e.Message); }
        }
        // put the original textures back on visible icons
        try
        {
            ScanIcons();
            foreach (Component ic in icons)
            {
                if (ic == null) continue;
                int idx = Convert.ToInt32(iconIndexField.GetValue(ic));
                int sp; if (!skinnedItem.TryGetValue(idx, out sp)) continue;
                object img = iconImageField.GetValue(ic);
                foreach (Orig o in originals) if (o.def == ItemDef(idx) && o.icon != null) rawTexProp.SetValue(img, o.icon.texture, null);
            }
        }
        catch { }
        skinnedItem.Clear(); skinned = false;
        RefreshDisplays();
        Log("disabled - original item icons/names restored");
    }

    static void RefreshDisplays()
    {
        try
        {
            Type disp = ror2.GetType("RoR2.UI.ItemInventoryDisplay");
            int n = 0;
            foreach (UnityEngine.Object d in UnityEngine.Object.FindObjectsOfType(disp))
            {
                MethodInfo m = disp.GetMethod("RequestUpdateDisplay", All) ?? disp.GetMethod("UpdateDisplay", All);
                if (m != null) { m.Invoke(d, null); n++; }
            }
            Log("refreshed " + n + " item displays");
        }
        catch (Exception e) { Log("refresh error: " + (e.InnerException ?? e).Message); }
    }

    static void ScanIcons()
    {
        icons.Clear();
        if (itemIconType == null) return;
        foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(itemIconType)) icons.Add((Component)o);
    }

    // The item bar draws icons through an AsyncRawImage that may load the original sprite asynchronously,
    // so every frame make sure carrier icons show the fish texture.
    static void EnforceIcons()
    {
        if (iconIndexField == null || iconImageField == null || rawTexProp == null) return;
        if (Time.unscaledTime >= nextIconScan) { nextIconScan = Time.unscaledTime + 1f; ScanIcons(); }
        int fixedN = 0, seen = 0;
        var sb = loggedIcons ? null : new StringBuilder();
        foreach (Component ic in icons)
        {
            if (ic == null) continue;
            int idx = Convert.ToInt32(iconIndexField.GetValue(ic));
            int sp; if (!skinnedItem.TryGetValue(idx, out sp)) continue;
            object img = iconImageField.GetValue(ic);
            if (img == null || (img is UnityEngine.Object && (UnityEngine.Object)img == null)) continue;
            seen++;
            Texture cur = (Texture)rawTexProp.GetValue(img, null);
            if (sb != null) sb.Append(" " + Species[sp] + ":" + (cur != null ? cur.name : "null"));
            if (cur != tex[sp]) { rawTexProp.SetValue(img, tex[sp], null); fixedN++; }
        }
        enforced += fixedN; enforceSeen = Math.Max(enforceSeen, seen);
        if (Time.unscaledTime >= nextEnforceLog)
        {
            nextEnforceLog = Time.unscaledTime + 60f;
            if (enforced > 0 || enforceSeen > 0) Log("item bar: " + enforceSeen + " carrier icons visible, " + enforced + " texture corrections in the last minute");
            enforced = 0; enforceSeen = 0;
        }
        if (sb != null && seen > 0) { loggedIcons = true; Log("item bar carrier icons before enforce:" + sb + " -> set " + fixedN + " to fish textures"); }
    }


    // ------------------------------------------------------------------ carriers + movement perks (every machine)
    static Dictionary<string, string> ResolveCarriers()
    {
        var r = new Dictionary<string, string>(); var used = new HashSet<string>();
        foreach (var kv in desiredCarrier)
        {
            string n = kv.Value;
            if (FindItem(n) < 0)
            {
                string sub = null;
                foreach (string c in substituteCarriers) if (!used.Contains(c) && !desiredCarrier.ContainsValue(c) && FindItem(c) >= 0) { sub = c; break; }
                Log("carrier " + n + " for " + kv.Key + " not found in this game version -> " + (sub ?? "none"));
                if (sub == null) continue;
                n = sub;
            }
            used.Add(n); r[kv.Key] = n;
        }
        return r;
    }

    static Type luMgrType;
    static object perkBody;
    static bool perkCaptured;
    static float baseMove, setMove, baseJump, setJump, baseSprint, setSprint;
    static int baseJumps, setJumps;
    static string lastPerkKey;

    static float GetF(object o, string n) { return Convert.ToSingle(Get(o, n)); }

    // Movement stats are computed by each player's own game for its own body, so this runs on every machine
    // for the LOCAL player only. RecalculateStats overwrites moveSpeed/jumpPower/maxJumpCount; a value that
    // differs from what we last set means the game recalculated -> it becomes the new base, and we re-apply.
    static void PerkTick()
    {
        if (luMgrType == null) luMgrType = ror2.GetType("RoR2.LocalUserManager");
        object lu = M(luMgrType, "GetFirstLocalUser").Invoke(null, null);
        object body = lu == null ? null : Get(lu, "cachedBody");
        if (body == null || (UnityEngine.Object)body == null) { perkBody = null; return; }
        if (!ReferenceEquals(body, perkBody)) { perkBody = body; perkCaptured = false; lastPerkKey = null; }
        object inv = Get(body, "inventory"); if (inv == null || (UnityEngine.Object)inv == null) return;
        MethodInfo cnt = M(inv.GetType(), "GetItemCount", itemIndexType);
        float mv = 0, sp = 0, jp = 0; int xj = 0; var counts = new StringBuilder();
        foreach (var kv in carrier)
        {
            Perk p; if (!Perks.TryGetValue(kv.Key, out p)) continue;
            int idx = FindItem(kv.Value); if (idx < 0) continue;
            int c = Convert.ToInt32(cnt.Invoke(inv, new[] { Idx(idx) }));
            if (c <= 0) continue;
            counts.Append(kv.Key + "x" + c + " ");
            mv += p.move * c; sp += p.sprint * c; jp += p.jump * c; if (p.jumpsPer > 0) xj += c / p.jumpsPer;
        }
        mv = Math.Min(mv, MaxMove); sp = Math.Min(sp, MaxSprint); jp = Math.Min(jp, MaxJump); xj = Math.Min(xj, MaxExtraJumps);

        float curMove = GetF(body, "moveSpeed"), curJump = GetF(body, "jumpPower"), curSprint = GetF(body, "sprintingSpeedMultiplier");
        int curJumps = Convert.ToInt32(Get(body, "maxJumpCount"));
        if (!perkCaptured || curMove != setMove) baseMove = curMove;
        if (!perkCaptured || curJump != setJump) baseJump = curJump;
        if (!perkCaptured || curJumps != setJumps) baseJumps = curJumps;
        if (!perkCaptured || curSprint != setSprint) baseSprint = curSprint;
        perkCaptured = true;
        setMove = baseMove * (1 + mv); setJump = baseJump * (1 + jp); setJumps = baseJumps + xj; setSprint = baseSprint * (1 + sp);
        if (curMove != setMove) Set(body, "moveSpeed", setMove);
        if (curJump != setJump) Set(body, "jumpPower", setJump);
        if (curJumps != setJumps) Set(body, "maxJumpCount", setJumps);
        if (curSprint != setSprint) Set(body, "sprintingSpeedMultiplier", setSprint);
        string key = counts + "|" + mv + "|" + sp + "|" + jp + "|" + xj;
        if (key != lastPerkKey)
        {
            lastPerkKey = key;
            string who = "?"; try { who = (string)Get(Get(lu, "currentNetworkUser"), "userName"); } catch { }
            Log("perks for local player " + who + " (" + ((UnityEngine.Object)body).name + "): fish " + (counts.Length > 0 ? counts.ToString() : "(none) ")
                + "-> move +" + Math.Round(mv * 100, 1) + "% (" + baseMove.ToString("0.00") + "->" + setMove.ToString("0.00") + "), sprint +" + Math.Round(sp * 100, 1)
                + "% (x" + baseSprint.ToString("0.000") + "->x" + setSprint.ToString("0.000") + "), jump +" + Math.Round(jp * 100, 1) + "% (" + baseJump.ToString("0.00") + "->" + setJump.ToString("0.00")
                + "), jumps " + baseJumps + "->" + setJumps);
        }
    }

    static void RestorePerks()
    {
        try
        {
            object body = perkBody;
            if (body == null || (UnityEngine.Object)body == null || !perkCaptured) return;
            if (GetF(body, "moveSpeed") == setMove) Set(body, "moveSpeed", baseMove);
            if (GetF(body, "jumpPower") == setJump) Set(body, "jumpPower", baseJump);
            if (Convert.ToInt32(Get(body, "maxJumpCount")) == setJumps) Set(body, "maxJumpCount", baseJumps);
            Set(body, "sprintingSpeedMultiplier", baseSprint);
            Log("perks removed from local body");
        }
        catch (Exception e) { Log("perk restore error: " + e.Message); }
    }

#if HOST
    // ================================================================== HOST: remove items + item sources
    static float nextCarrierCheck;
    static object lastStage;
    static float stageStart, nextSweep, nextGround, stripAt;
    static int stageNo, sweepsThisStage;
    static bool announced;
    static readonly HashSet<int> keep = new HashSet<int>();
    static readonly string[] alwaysRemove = { "BoostDamage", "ExtraLifeConsumed", "ExtraLifeVoid", "ExtraLifeVoidConsumed", "Clover", "CloverVoid" };
    static readonly Dictionary<string, int> removedTotals = new Dictionary<string, int>();

    static void CheckCarrierTable()
    {
        // newest Fishing<N> with a public static fishCarrierItem table: force our carriers into it (extra species stay)
        Dictionary<string, string> live = null; int bestN = -1; Type from = null;
        foreach (var a in FishingScope.Assemblies())
        {
            if (a.GetName().Name.IndexOf("fish", StringComparison.OrdinalIgnoreCase) < 0) continue;
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch { continue; }
            foreach (Type t in ts)
            {
                Match m = Regex.Match(t.Name, "^Fishing(\\d+)$"); if (!m.Success) continue;
                int n = int.Parse(m.Groups[1].Value); if (n <= bestN) continue;
                FieldInfo f = t.GetField("fishCarrierItem", BindingFlags.Public | BindingFlags.Static);
                var d = f == null ? null : f.GetValue(null) as Dictionary<string, string>;
                if (d != null) { live = d; bestN = n; from = t; }
            }
        }
        var mine = ResolveCarriers();
        var want = new Dictionary<string, string>();
        if (live != null)
        {
            lock (live)
            {
                foreach (var kv in mine)
                {
                    string v; live.TryGetValue(kv.Key, out v);
                    if (v != kv.Value) { live[kv.Key] = kv.Value; Log(from.Name + ".fishCarrierItem[" + kv.Key + "]: " + (v ?? "-") + " -> " + kv.Value); }
                }
                foreach (var kv in live) want[kv.Key] = kv.Value;
            }
            // keep the fishing mod's perk text (HUD / chat) in line with the real perks
            try
            {
                FieldInfo pt = from.GetField("PerkText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var ptd = pt == null ? null : pt.GetValue(null) as Dictionary<string, string>;
                if (ptd != null)
                    lock (ptd)
                        foreach (var kv in Perks)
                        {
                            string v;
                            if (!ptd.TryGetValue(kv.Key, out v) || v != kv.Value.text) { ptd[kv.Key] = kv.Value.text; Log(from.Name + ".PerkText[" + kv.Key + "]: " + (v ?? "-") + " -> " + kv.Value.text); }
                        }
            }
            catch (Exception e) { Log("perk text update: " + e.Message); }
        }
        else foreach (var kv in mine) want[kv.Key] = kv.Value;
        bool same = carrier.Count == want.Count;
        if (same) foreach (var kv in want) { string v; if (!carrier.TryGetValue(kv.Key, out v) || v != kv.Value) { same = false; break; } }
        if (same) return;
        if (skinned) { Restore(); restored = false; }
        carrier.Clear(); foreach (var kv in want) carrier[kv.Key] = kv.Value;
        var sb = new StringBuilder(); foreach (var kv in carrier) sb.Append(kv.Key + "=" + kv.Value + "(#" + FindItem(kv.Value) + ") ");
        Log("carrier table (" + (from != null ? from.Name : "no fishing mod table") + "): " + sb);
        Apply();
        RebuildKeep();
    }

    static void RebuildKeep()
    {
        keep.Clear();
        foreach (var kv in carrier) { int i = FindItem(kv.Value); if (i >= 0) keep.Add(i); }
    }

    static void HostTick()
    {
        if (!ServerActive()) return;
        object run = Get(runType, "instance");
        if (run == null || (run is UnityEngine.Object && (UnityEngine.Object)run == null)) return;
        if (keep.Count == 0) RebuildKeep();
        object st = Get(stageType, "instance");
        if (st == null || (st is UnityEngine.Object && (UnityEngine.Object)st == null)) return;
        float now = Time.time;
        if (!ReferenceEquals(st, lastStage))
        {
            lastStage = st; stageNo++; stageStart = now; nextSweep = now + 2f; nextGround = now + 2f; stripAt = now + 1.5f; sweepsThisStage = 0;
            Log("=== stage " + stageNo + " (" + SceneName() + ")" + (stageNo == 1 ? " [at install]" : ""));
        }
        if (stripAt > 0 && now >= stripAt) { stripAt = 0; StripInventories("stage " + stageNo); }
        if (now >= nextSweep && now - stageStart <= 33f) { nextSweep = now + 5f; SweepInteractables(); }
        if (now >= nextGround) { nextGround = now + 5f; SweepGround(); }
        if (!announced && stageNo >= 1 && now - stageStart > 3f)
        {
            announced = true;
            Chat("Items removed — it's fish only now! Your catches appear in your item bar.");
        }
    }

    static string SceneName()
    {
        try { object sd = Get(ror2.GetType("RoR2.SceneCatalog"), "mostRecentSceneDef"); return sd == null ? "?" : (string)Get(sd, "cachedName"); } catch { return "?"; }
    }

    static void Chat(string text)
    {
        try
        {
            Type chat = ror2.GetType("RoR2.Chat");
            object msg = Activator.CreateInstance(chat.GetNestedType("SimpleChatMessage", All));
            Set(msg, "baseToken", "<color=#66ccff>" + text + "</color>");
            foreach (MethodInfo m in chat.GetMethods(All))
                if (m.Name == "SendBroadcastChat" && m.GetParameters().Length == 1) { m.Invoke(null, new object[] { msg }); break; }
            Log("CHAT " + text);
        }
        catch (Exception e) { Log("chat error: " + (e.InnerException ?? e).Message); }
    }

    static string Inv(object inv, MethodInfo cnt)
    {
        var sb = new StringBuilder(); int n = ItemCount();
        for (int i = 0; i < n; i++)
        {
            int c = Convert.ToInt32(cnt.Invoke(inv, new[] { Idx(i) }));
            if (c > 0) sb.Append(ItemName(i) + "x" + c + " ");
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    static void StripInventories(string why)
    {
        Type pcmc = ror2.GetType("RoR2.PlayerCharacterMasterController");
        Type invT = ror2.GetType("RoR2.Inventory");
        MethodInfo cntPerm = M(invT, "GetItemCountPermanent", itemIndexType) ?? M(invT, "GetItemCount", itemIndexType);
        MethodInfo cntAll = M(invT, "GetItemCount", itemIndexType);
        MethodInfo remPerm = M(invT, "RemoveItemPermanent", itemIndexType, typeof(int)) ?? M(invT, "RemoveItem", itemIndexType, typeof(int));
        MethodInfo remPlain = M(invT, "RemoveItem", itemIndexType, typeof(int));
        MethodInfo givePerm = M(invT, "GiveItemPermanent", itemIndexType, typeof(int)) ?? M(invT, "GiveItem", itemIndexType, typeof(int));
        MethodInfo cntTemp = M(invT, "GetItemCountTemp", itemIndexType);
        MethodInfo remTemp = M(invT, "RemoveItemTemp", itemIndexType, typeof(float));
        var removeAlways = new HashSet<int>(); foreach (string s in alwaysRemove) { int i = FindItem(s); if (i >= 0 && !keep.Contains(i)) removeAlways.Add(i); }
        int n = ItemCount(), players = 0, totalRemoved = 0;
        foreach (object p in (IEnumerable)Get(pcmc, "instances"))
        {
            object master = Get(p, "master"); if (master == null) continue;
            object inv = Get(master, "inventory"); if (inv == null) continue;
            players++;
            string who = "?"; try { who = (string)Get(Get(p, "networkUser"), "userName"); } catch { }
            string before = Inv(inv, cntAll);
            var removed = new StringBuilder();
            // convert the old carriers 1:1 into the new ones, so the fish collection is kept
            foreach (var kv in oldCarrier)
            {
                string nw; if (!carrier.TryGetValue(kv.Key, out nw) || nw == kv.Value) continue;
                int oi = FindItem(kv.Value), ni = FindItem(nw); if (oi < 0 || ni < 0 || keep.Contains(oi)) continue;
                int oc = Convert.ToInt32(cntPerm.Invoke(inv, new[] { Idx(oi) }));
                if (oc <= 0) continue;
                remPerm.Invoke(inv, new object[] { Idx(oi), oc });
                givePerm.Invoke(inv, new object[] { Idx(ni), oc });
                removed.Append(kv.Value + "x" + oc + "=>" + nw + " ");
            }
            for (int i = 0; i < n; i++)
            {
                if (keep.Contains(i)) continue;
                object def = ItemDef(i); if (def == null) continue;
                bool hidden = (bool)Get(def, "hidden");
                if (hidden && !removeAlways.Contains(i)) continue;   // hidden helper items (difficulty helpers etc.) stay
                object ix = Idx(i);
                int c = Convert.ToInt32(cntPerm.Invoke(inv, new[] { ix }));
                if (c > 0) { remPerm.Invoke(inv, new object[] { ix, c }); removed.Append((string)Get(def, "name") + "x" + c + " "); totalRemoved += c; Count((string)Get(def, "name"), c); }
                if (cntTemp != null && remTemp != null)
                {
                    float tc = Convert.ToSingle(cntTemp.Invoke(inv, new[] { ix }));
                    if (tc > 0) { remTemp.Invoke(inv, new object[] { ix, tc }); removed.Append((string)Get(def, "name") + "(temp)x" + tc + " "); }
                }
                int left = Convert.ToInt32(cntAll.Invoke(inv, new[] { ix }));
                if (left > 0 && remPlain != null && remPlain != remPerm) { remPlain.Invoke(inv, new object[] { ix, left }); removed.Append((string)Get(def, "name") + "(other)x" + left + " "); }
            }
            Log(why + " " + who + ": BEFORE " + before + "\n      removed " + (removed.Length == 0 ? "(nothing)" : removed.ToString()) + "\n      AFTER " + Inv(inv, cntAll));
        }
        Log(why + ": stripped " + totalRemoved + " items from " + players + " players");
    }

    static void Count(string k, int n) { int v; removedTotals.TryGetValue(k, out v); removedTotals[k] = v + n; }

    static readonly string[] sourceComponents = { "ChestBehavior", "ShopTerminalBehavior", "MultiShopController", "ShrineChanceBehavior", "ScrapperController",
        "OptionChestBehavior", "RouletteChestController", "ShrineRestackBehavior", "PickupDistributorBehavior" };
    static readonly Regex sourceName = new Regex("Duplicator|LunarChest|EquipmentBarrel|VoidChest|VoidTriple|Lockbox|ShrineCleanse|Scrapper|CasinoChest|ShrineChance|ShrineRestack|Chest|TripleShop|LunarShopTerminal|ScavBackpack", RegexOptions.IgnoreCase);
    static readonly Regex keepName = new Regex("Teleporter|Portal|NewtStatue|Drone|Turret|ShrineBlood|ShrineCombat|ShrineBoss|ShrineHealing|ShrineGoldshores|ShrineColossus|Seer|Barrel1|MoneyPack", RegexOptions.IgnoreCase);

    static GameObject NetRoot(GameObject go)
    {
        Type nid = null; foreach (var a in FishingScope.Assemblies()) { nid = a.GetType("UnityEngine.Networking.NetworkIdentity"); if (nid != null) break; }
        Transform t = go.transform;
        while (t != null) { if (nid != null && t.GetComponent(nid) != null) return t.gameObject; t = t.parent; }
        return go;
    }

    static void Kill(GameObject go, Dictionary<string, int> counts, string kind, HashSet<GameObject> done)
    {
        go = NetRoot(go);
        if (done.Contains(go)) return;
        done.Add(go);
        if (ror2.GetType("RoR2.TeleporterInteraction") != null && go.GetComponentInChildren(ror2.GetType("RoR2.TeleporterInteraction")) != null) return;
        string nm = go.name.Replace("(Clone)", "");
        if (keepName.IsMatch(nm) && !nm.Contains("Chest")) return;
        MethodInfo d = netServerType.GetMethod("Destroy", new[] { typeof(GameObject) });
        if (d != null) d.Invoke(null, new object[] { go }); else UnityEngine.Object.Destroy(go);
        string k = kind + ":" + nm; int v; counts.TryGetValue(k, out v); counts[k] = v + 1;
    }

    static void SweepInteractables()
    {
        sweepsThisStage++;
        var counts = new Dictionary<string, int>();
        var done = new HashSet<GameObject>();
        foreach (string cn in sourceComponents)
        {
            Type t = ror2.GetType("RoR2." + cn); if (t == null) continue;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(t)) Kill(((Component)o).gameObject, counts, cn, done);
        }
        Type pi = ror2.GetType("RoR2.PurchaseInteraction");
        var remaining = new Dictionary<string, int>();
        if (pi != null)
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(pi))
            {
                GameObject go = ((Component)o).gameObject;
                string nm = go.name.Replace("(Clone)", "");
                if (sourceName.IsMatch(nm) && !keepName.IsMatch(nm)) Kill(go, counts, "PurchaseInteraction", done);
                else if (!done.Contains(NetRoot(go))) { int v; remaining.TryGetValue(nm, out v); remaining[nm] = v + 1; }
            }
        var sb = new StringBuilder(); int total = 0;
        foreach (var kv in counts) { sb.Append(kv.Key + " x" + kv.Value + ", "); total += kv.Value; }
        if (total > 0 || sweepsThisStage == 1)
        {
            var rs = new StringBuilder(); foreach (var kv in remaining) rs.Append(kv.Key + " x" + kv.Value + ", ");
            Log("stage " + stageNo + " sweep " + sweepsThisStage + ": removed " + total + " item sources: " + (total > 0 ? sb.ToString() : "-") + (sweepsThisStage == 1 ? "\n      kept purchasables: " + (rs.Length > 0 ? rs.ToString() : "(none)") : ""));
        }
    }

    static void SweepGround()
    {
        var counts = new Dictionary<string, int>();
        var done = new HashSet<GameObject>();
        Type gpc = ror2.GetType("RoR2.GenericPickupController");
        if (gpc != null)
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(gpc))
            {
                object pIdx = Get(o, "pickupIndex");
                if (pIdx == null) { object up = Get(o, "pickup"); if (up != null) pIdx = Get(up, "pickupIndex"); }
                if (pIdx == null) continue;
                object pdef = M(pickupCatalogType, "GetPickupDef", pIdx.GetType()).Invoke(null, new[] { pIdx });
                if (pdef == null) continue;
                int item = Convert.ToInt32(Get(pdef, "itemIndex"));
                if (item < 0 || keep.Contains(item)) continue;   // equipment / fish carriers stay
                GameObject go = ((Component)o).gameObject;
                MethodInfo d = netServerType.GetMethod("Destroy", new[] { typeof(GameObject) });
                if (d != null) d.Invoke(null, new object[] { go }); else UnityEngine.Object.Destroy(go);
                string k = "pickup:" + ItemName(item); int v; counts.TryGetValue(k, out v); counts[k] = v + 1;
            }
        Type ppc = ror2.GetType("RoR2.PickupPickerController");
        if (ppc != null)
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(ppc))
            {
                GameObject go = ((Component)o).gameObject;
                if (go == null || go.name.IndexOf("Fish", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Type pit = ror2.GetType("RoR2.PurchaseInteraction");
                if (pit != null && go.GetComponent(pit) != null) continue;   // chests/scrappers are handled by the interactable sweep
                Kill(go, counts, "PickupPicker", done);
            }
        if (counts.Count > 0)
        {
            var sb = new StringBuilder(); foreach (var kv in counts) sb.Append(kv.Key + " x" + kv.Value + ", ");
            Log("stage " + stageNo + " ground sweep removed: " + sb);
        }
    }
#endif
}
#endif

#if OFF
// Tiny undo payload: inject FishItemsOff to switch every FishItems version off (restores icons/names, stops removal).
public static class FishItemsOff
{
    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
        {
            Type[] ts; try { ts = FishingScope.TypesOf(a); } catch { continue; }
            foreach (Type t in ts)
                if (t != null && Regex.IsMatch(t.Name, "^FishItems(\\d+|Client)$"))
                {
                    FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                    if (en != null) en.SetValue(null, false);
                }
        }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ror2_fishitems.log"), DateTime.Now.ToString("HH:mm:ss ") + "[FishItemsOff] all FishItems versions disabled" + Environment.NewLine); } catch { }
    }
}
#endif
