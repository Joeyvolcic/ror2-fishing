// FishingKeys: ONE owner for the number keys 0-9 across the guest payloads (each payload is its own assembly, so the
// state lives in AppDomain data, not in a static field). Compiled into VoteClient, ShopStandClient, MerchantClient and
// InventoryClient.
//   Claim(owner, prio)  every frame while a panel that uses number keys is on screen (claims last 0.25 s)
//   Owner()             current owner or null;  FreeFor(owner) = nobody else holds them
// Priorities: inventory window 3 > rod shop stall panel 2 = fish merchant menu 2 (they are >= 12 m apart, never both)
// > feature-vote card 0 (the vote never claims: it only takes 1/2/3 when they are free, otherwise "!vote N" in chat works).
using System;
using System.Globalization;
using UnityEngine;

public static class FishingKeys
{
    const string Slot = "ror2.fishing.numkeys";
    const float Hold = 0.25f;

    static bool Read(out string owner, out int prio, out float at)
    {
        owner = null; prio = 0; at = -100f;
        try
        {
            string v = AppDomain.CurrentDomain.GetData(Slot) as string;
            if (string.IsNullOrEmpty(v)) return false;
            string[] p = v.Split('|');
            if (p.Length < 3) return false;
            owner = p[0]; prio = int.Parse(p[1], CultureInfo.InvariantCulture); at = float.Parse(p[2], CultureInfo.InvariantCulture);
            return Time.unscaledTime - at <= Hold && Time.unscaledTime >= at - 1f;
        }
        catch { return false; }
    }

    public static void Claim(string owner, int prio)
    {
        try
        {
            string o; int pr; float at;
            if (Read(out o, out pr, out at) && o != owner && pr > prio) return;   // a higher-priority panel holds them
            AppDomain.CurrentDomain.SetData(Slot, owner + "|" + prio.ToString(CultureInfo.InvariantCulture) + "|" + Time.unscaledTime.ToString("R", CultureInfo.InvariantCulture));
        }
        catch { }
    }

    public static string Owner() { string o; int pr; float at; return Read(out o, out pr, out at) ? o : null; }

    public static bool FreeFor(string owner) { string o = Owner(); return o == null || o == owner; }
}
