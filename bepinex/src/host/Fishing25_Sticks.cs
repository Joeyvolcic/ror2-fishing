// STICK ROD part of the host fishing mod (partial class; Fishing25 replaced by patch_sticks.py).
// One-time migration when the Stick Rod becomes the free starter: everyone the shop already knows (rod owners + the
// tournament money table) had the old free Basic Rod - they get it as an owned rod and keep it equipped if it was
// their rod. New players start with the Stick Rod. Marker file so it only ever happens once (survives restarts).
using System;
using System.Collections.Generic;
using System.IO;

public static partial class Fishing25
{
    public static string stickMigrationMarker = FishingPaths.Data("stick_migration.done");
    public static bool stickMigrationDryRun;   // test: log only

    static void StickMigration()
    {
        try
        {
            if (File.Exists(stickMigrationMarker)) { LogR("stick migration: already done (" + stickMigrationMarker + ")"); return; }
            var names = new List<string>(rodOwners.Keys);
            foreach (string k in money.Keys) if (!names.Contains(k)) names.Add(k);
            var done = new List<string>();
            foreach (string nm in names)
            {
                if (string.IsNullOrEmpty(nm) || nm.StartsWith("~") || nm.EndsWith("'s pet")) continue;
                RodOwner o = MakeOwner(nm);
                bool hadBasic = o.rod == "Stick Rod";   // loaded as the default = their rod was the old free Basic Rod
                if (!o.owned.Contains("Basic Rod")) o.owned.Add("Basic Rod");
                if (hadBasic) o.rod = "Basic Rod";
                done.Add(nm + (hadBasic ? " (Basic Rod kept equipped)" : " (keeps " + o.rod + ")"));
            }
            LogR("stick migration: " + done.Count + " existing player(s) keep the Basic Rod: " + string.Join(", ", done.ToArray()));
            if (stickMigrationDryRun) return;
            SaveRods();
            File.WriteAllText(stickMigrationMarker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + string.Join(", ", done.ToArray()) + Environment.NewLine);
        }
        catch (Exception e) { LogR("stick migration error: " + e); }
    }
}
