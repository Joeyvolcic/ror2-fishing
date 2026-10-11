// CaseData - the fish merchant's price list and the CS:GO-style case drop table. Plain C# (no Unity, no RoR2), shared by the
// host add-on (FishMerchant<N>) and the guest payload (MerchantClient in FISHING.exe) so both show the same prices and odds.
//
//  Cases (opened right away when you get one):
//    0 Tackle Case     $100  or 3 white fish        odds blue 79.92 / purple 15.98 / pink 3.20 / red 0.64 / gold 0.26 %
//    1 Deep Water Case $250  or 1 green fish        odds 64 / 25 / 7.5 / 2.5 / 1 %
//    2 Abyssal Case    $750  or 1 red or lunar fish odds 45 / 33 / 14 / 5.5 / 2.5 %
//  Selling fish: white $15, green $50, red $180, lunar $400, Old Boot $1.
//  Duplicates (you already own the item) are paid out: blue $25, purple $100, pink $400, red $1,200, gold $4,000.
//  StatTrak(TM): 10% of case-only ROD SKIN drops are "StatTrak™ <skin>" - a separate, case-only item (never sold in the shop)
//  that counts the owner's fish caught while it is equipped (+ rare catches and the best catch value).
//  Wear (cosmetic float 0-1, CS:GO bands): Factory New < 0.07 <= Minimal Wear < 0.15 <= Field-Tested < 0.38 <= Well-Worn < 0.45 <= Battle-Scarred.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class CaseData
{
    public class Item
    {
        public string name, kind, desc;   // kind: "skin" (rod skin) or "bobber" - same kinds as the rod shop catalogue
        public int rarity;                // 0 blue .. 4 gold
        public bool shop;                 // an existing rod-shop item (bought ownership is shared with the shop)
    }

    public static readonly string[] RarityNames = { "Mil-Spec", "Restricted", "Classified", "Covert", "Rare Special" };
    public static readonly string[] RarityHex = { "#4b69ff", "#8847ff", "#d32ce6", "#eb4b4b", "#ffd700" };
    public static readonly float[][] RarityRgb = { new[] { 0.29f, 0.41f, 1f }, new[] { 0.53f, 0.28f, 1f }, new[] { 0.83f, 0.17f, 0.9f }, new[] { 0.92f, 0.29f, 0.29f }, new[] { 1f, 0.84f, 0f } };
    public static readonly int[] DupValue = { 25, 100, 400, 1200, 4000 };
    public static readonly int[] MarketPrice = { 4000, 8000, 15000, 40000, 100000 };   // straight "!buy" price in the rod shop for case-only items

    public static readonly List<Item> Items = new List<Item> {
        // blue - Mil-Spec
        new Item { name = "Safari Mesh",   kind = "skin",   rarity = 0, desc = "green/tan camo net" },
        new Item { name = "Sand Dune",     kind = "skin",   rarity = 0, desc = "desert tan" },
        new Item { name = "Night Ops",     kind = "skin",   rarity = 0, desc = "navy camo" },
        new Item { name = "Bamboo",        kind = "skin",   rarity = 0, shop = true },
        new Item { name = "Cork",          kind = "bobber", rarity = 0, desc = "cork float" },
        new Item { name = "Bubblegum",     kind = "bobber", rarity = 0, desc = "pink bubble" },
        new Item { name = "Classic",       kind = "bobber", rarity = 0, shop = true },
        // purple - Restricted
        new Item { name = "Case Hardened", kind = "skin",   rarity = 1, desc = "blue/gold patina (pattern 661 = blue gem)" },
        new Item { name = "Redline",       kind = "skin",   rarity = 1, desc = "carbon with red lines" },
        new Item { name = "Carbon",        kind = "skin",   rarity = 1, shop = true },
        new Item { name = "Eyeball",       kind = "bobber", rarity = 1, desc = "it watches you" },
        new Item { name = "Star",          kind = "bobber", rarity = 1, shop = true },
        // pink - Classified
        new Item { name = "Fade",          kind = "skin",   rarity = 2, desc = "yellow-pink-purple fade" },
        new Item { name = "Hyper Koi",     kind = "skin",   rarity = 2, desc = "white with koi scales" },
        new Item { name = "Neon",          kind = "skin",   rarity = 2, shop = true },
        new Item { name = "Eight Ball",    kind = "bobber", rarity = 2, desc = "magic 8" },
        new Item { name = "Disco Ball",    kind = "bobber", rarity = 2, desc = "mirror ball" },
        // red - Covert
        new Item { name = "Dragon Rod",    kind = "skin",   rarity = 3, desc = "jade dragon scales, gold, embers" },
        new Item { name = "Howl",          kind = "skin",   rarity = 3, desc = "burning orange" },
        new Item { name = "Crown",         kind = "bobber", rarity = 3, desc = "royal crown" },
        // gold - Rare Special
        new Item { name = "★ Legendary Rod", kind = "skin",   rarity = 4, desc = "chrome gold + diamond" },
        new Item { name = "★ Doppler",       kind = "skin",   rarity = 4, desc = "ruby / sapphire / black pearl" },
        new Item { name = "★ Golden Kraken", kind = "bobber", rarity = 4, desc = "gold with tentacles" } };

    public class Case { public string key, name; public int price; public string fishText; public double[] odds; }
    public static readonly Case[] Cases = {
        new Case { key = "common",   name = "Tackle Case",     price = 100, fishText = "3 white fish",       odds = new[] { 0.7992, 0.1598, 0.0320, 0.0064, 0.0026 } },
        new Case { key = "uncommon", name = "Deep Water Case", price = 250, fishText = "1 green fish",       odds = new[] { 0.640, 0.250, 0.075, 0.025, 0.010 } },
        new Case { key = "rare",     name = "Abyssal Case",    price = 750, fishText = "1 red or lunar fish", odds = new[] { 0.450, 0.330, 0.140, 0.055, 0.025 } } };

    public static readonly string[] White = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish" };
    public static readonly string[] Green = { "Bass", "Salmon", "Pufferfish" };
    public static readonly string[] Red = { "Swordfish", "Golden Koi", "Anglerfish" };
    public static readonly string[] Lunar = { "Moon Eel", "Ghost Fish" };
    public static readonly string[] AllFish = { "Minnow", "Perch", "Trout", "Clownfish", "Catfish", "Bass", "Salmon", "Pufferfish", "Swordfish", "Golden Koi", "Anglerfish", "Moon Eel", "Ghost Fish", "Old Boot" };

    // "white" / "green" / "red" / "lunar" / "boot" / null
    public static string TierOf(string sp)
    {
        if (sp == null) return null;
        if (Array.IndexOf(White, sp) >= 0) return "white";
        if (Array.IndexOf(Green, sp) >= 0) return "green";
        if (Array.IndexOf(Red, sp) >= 0) return "red";
        if (Array.IndexOf(Lunar, sp) >= 0) return "lunar";
        if (sp == "Old Boot") return "boot";
        return null;
    }
    public static int SellPrice(string sp)
    {
        switch (TierOf(sp)) { case "white": return 15; case "green": return 50; case "red": return 180; case "lunar": return 400; case "boot": return 1; default: return 0; }
    }
    // which case a fish trades into: 0 / 1 / 2, -1 = none. White needs 3.
    public static int TradeCaseOf(string sp)
    {
        switch (TierOf(sp)) { case "white": return 0; case "green": return 1; case "red": case "lunar": return 2; default: return -1; }
    }
    public static int FishNeeded(int caseIdx) { return caseIdx == 0 ? 3 : 1; }
    public static bool FishFits(int caseIdx, string sp) { string t = TierOf(sp); return caseIdx == 0 ? t == "white" : caseIdx == 1 ? t == "green" : caseIdx == 2 && (t == "red" || t == "lunar"); }

    // ---- StatTrak
    public const string ST = "StatTrak™ ";
    public const double StatTrakChance = 0.10;
    public static bool IsStat(string name) { return name != null && name.StartsWith(ST); }
    public static string Base(string name) { return IsStat(name) ? name.Substring(ST.Length) : name; }
    // canonical item name for a user query ("stattrak dragon", "StatTrak™ Dragon Rod", "dragon rod") - keeps the StatTrak prefix
    public static string Canon(string q)
    {
        if (q == null) return null;
        string t = q.Trim(); bool st = false;
        string n = Norm(t);
        if (IsStat(t)) { st = true; t = Base(t); }
        else if (n.StartsWith("stattrak")) { st = true; int i = t.ToLowerInvariant().IndexOf("stattrak"); t = t.Substring(i + 8).TrimStart('™', ' ', '-', ':'); }
        Item it = Find(t);
        if (it == null) return null;
        return st ? ST + it.name : it.name;
    }

    public static Item Find(string name)
    {
        if (name == null) return null;
        if (IsStat(name)) name = Base(name);
        foreach (Item i in Items) if (i.name == name) return i;
        string n = Norm(name);
        foreach (Item i in Items) if (Norm(i.name) == n) return i;
        foreach (Item i in Items) if (Norm(i.name) == n + "rod" || Norm(i.name) + "skin" == n || Norm(i.name) + "bobber" == n) return i;
        Item hit = null; int hits = 0;
        if (n.Length >= 3) foreach (Item i in Items) if (Norm(i.name).StartsWith(n)) { hit = i; hits++; }
        return hits == 1 ? hit : null;
    }
    public static bool IsCaseOnly(string name) { Item i = Find(name); return i != null && !i.shop && i.name == Base(name); }
    public static string Norm(string s) { var sb = new StringBuilder(); foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c); return sb.ToString(); }

    public static Case FindCase(string q)
    {
        string n = Norm(q);
        if (n.Length == 0) return null;
        foreach (Case c in Cases) if (n == c.key || n == Norm(c.name) || Norm(c.name).StartsWith(n) || c.key.StartsWith(n)) return c;
        if (n == "white" || n == "tackle") return Cases[0];
        if (n == "green" || n == "deep") return Cases[1];
        if (n == "red" || n == "lunar" || n == "abyss") return Cases[2];
        return null;
    }
    public static int CaseIndex(string key) { for (int i = 0; i < Cases.Length; i++) if (Cases[i].key == key) return i; return -1; }

    public static string WearName(float w)
    {
        return w < 0.07f ? "Factory New" : w < 0.15f ? "Minimal Wear" : w < 0.38f ? "Field-Tested" : w < 0.45f ? "Well-Worn" : "Battle-Scarred";
    }
    public static string WearShort(float w)
    {
        return w < 0.07f ? "FN" : w < 0.15f ? "MW" : w < 0.38f ? "FT" : w < 0.45f ? "WW" : "BS";
    }
    public static string F4(float v) { return v.ToString("0.0000", CultureInfo.InvariantCulture); }
    public static float ParseF(string s, float def) { float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def; }
    public static string Money(int v) { return "$" + v.ToString("#,0", CultureInfo.InvariantCulture); }
    public static string Pct(double p) { return (p * 100).ToString(p < 0.01 ? "0.00" : "0.#", CultureInfo.InvariantCulture) + "%"; }
    public static string OddsLine(int caseIdx)
    {
        var parts = new List<string>();
        for (int r = 0; r < 5; r++) parts.Add("<color=" + RarityHex[r] + ">" + RarityNames[r] + " " + Pct(Cases[caseIdx].odds[r]) + "</color>");
        return string.Join(" | ", parts.ToArray());
    }

    public static int RollRarity(Random rng, int caseIdx)
    {
        double r = rng.NextDouble(), acc = 0;
        double[] o = Cases[caseIdx].odds;
        for (int i = 0; i < o.Length; i++) { acc += o[i]; if (r < acc) return i; }
        return 0;
    }
    public static Item RollItemOfRarity(Random rng, int rarity)
    {
        var pool = new List<Item>();
        foreach (Item i in Items) if (i.rarity == rarity) pool.Add(i);
        return pool[rng.Next(pool.Count)];
    }
    // wear bands weighted like CS:GO drops (most items Field-Tested), uniform inside the band
    public static float RollWear(Random rng)
    {
        double b = rng.NextDouble();
        float lo, hi;
        if (b < 0.10) { lo = 0f; hi = 0.07f; } else if (b < 0.30) { lo = 0.07f; hi = 0.15f; } else if (b < 0.70) { lo = 0.15f; hi = 0.38f; }
        else if (b < 0.85) { lo = 0.38f; hi = 0.45f; } else { lo = 0.45f; hi = 1f; }
        return (float)Math.Round(lo + (hi - lo) * rng.NextDouble(), 4);
    }
    // name = the item's name, or "StatTrak™ <name>" (10% of case-only rod skins)
    public static void Roll(Random rng, int caseIdx, out Item item, out string name, out float wear, out int seed)
    {
        int r = RollRarity(rng, caseIdx);
        item = RollItemOfRarity(rng, r);
        wear = RollWear(rng);
        seed = rng.Next(1000);
        name = item.name;
        if (item.kind == "skin" && !item.shop && rng.NextDouble() < StatTrakChance) name = ST + item.name;
    }

    // the "drop table" text for chat (!merchant)
    public static string DropTable()
    {
        var sb = new StringBuilder();
        for (int r = 4; r >= 0; r--)
        {
            var names = new List<string>();
            foreach (Item i in Items) if (i.rarity == r) names.Add(i.name + (i.kind == "bobber" ? " (bobber)" : ""));
            sb.Append("<color=" + RarityHex[r] + ">" + RarityNames[r] + ": " + string.Join(", ", names.ToArray()) + "</color>");
            if (r > 0) sb.Append("  ");
        }
        sb.Append("  <color=#cf6a32>StatTrak™: 10% of case rod skins (counts your catches)</color>");
        return sb.ToString();
    }
}
