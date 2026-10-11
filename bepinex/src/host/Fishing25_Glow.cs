// GLOW BOBBERS + ROD STAND part of the host fishing mod (partial class; Fishing25 replaced by patch_glow.py).
//  Night = 20:00-05:00 on the fishing clock (Hour(): DayNight<N>.hour, same window as the gameplay "Night" event).
//  At night: Glow Bobber -> bites 25% faster; Firefly Bobber -> +15% red & lunar; Lantern Bobber -> both.
//  By day they only glow (the look is drawn by RodStand<N> / FISHING.exe).
//  Buying (!buy, and the stall's own purchases) asks the newest RodStand<N>.Gate(name): only at the stall,
//  unless no stall is placed on this stage (then chat buying stays open). The stall sets rodStandCall for its own calls.
using System;
using System.Reflection;
using System.Text.RegularExpressions;

public static partial class Fishing25
{
    public static bool glowBonusLive = true;
    public static float glowNightBite = 0.75f, glowNightRare = 0.15f;
    public static bool rodStandCall;          // set by RodStand<N> around its own RodCommand calls
    public static bool standOnlyBuying = true;

    public static bool GlowNight() { float h = Hour(); return h >= 20f || h < 5f; }
    static string BobberOf(string name) { RodOwner o = OwnerOf(name); return o == null ? "Plain" : o.bobber; }
    public static float GlowLuck(string name)
    {
        if (!glowBonusLive || name == null) return 0f;
        string b = BobberOf(name);
        return (b == "Firefly Bobber" || b == "Lantern Bobber") && GlowNight() ? glowNightRare : 0f;
    }
    public static float GlowBite(string name)
    {
        if (!glowBonusLive || name == null) return 1f;
        string b = BobberOf(name);
        return (b == "Glow Bobber" || b == "Lantern Bobber") && GlowNight() ? glowNightBite : 1f;
    }

    // test: what a bobber would give at a given hour (no state touched)
    public static string GlowTest(string bobber, float hour)
    {
        bool night = hour >= 20f || hour < 5f;
        float luck = (bobber == "Firefly Bobber" || bobber == "Lantern Bobber") && night ? glowNightRare : 0f;
        float bite = (bobber == "Glow Bobber" || bobber == "Lantern Bobber") && night ? glowNightBite : 1f;
        return bobber + " at " + hour.ToString("0.0") + ": bite x" + bite.ToString("0.00") + ", red/lunar x" + (1f + luck).ToString("0.00") + (night ? " (night)" : " (day)");
    }

    static Type standType; static float standLookAt = -100f;
    static string StandGate(string name)
    {
        if (rodStandCall || !standOnlyBuying) return null;
        try
        {
            if (now - standLookAt > 5f || standType == null)
            {
                standLookAt = now; Type best = null; int bn = -1;
                foreach (var asm in FishingScope.Assemblies())
                    foreach (Type t in SafeTypes(asm))
                    {
                        Match mm = Regex.Match(t.Name, "^RodStand(\\d+)$"); if (!mm.Success) continue;
                        FieldInfo en = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                        if (en != null && !(bool)en.GetValue(null)) continue;
                        int k = int.Parse(mm.Groups[1].Value); if (k > bn) { bn = k; best = t; }
                    }
                standType = best;
            }
            if (standType == null) return null;   // no stall add-on running: chat buying stays open
            MethodInfo g = standType.GetMethod("Gate", BindingFlags.Public | BindingFlags.Static);
            return g == null ? null : g.Invoke(null, new object[] { name }) as string;
        }
        catch (Exception e) { LogR("stand gate error: " + e.Message); return null; }
    }
}
