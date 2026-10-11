// Guest side of the FISHING ZONES (guide agent, host Fishing25+ / agents\guide\FishingZones.cs). Compiled into the
// FishingClient payload. Zones only change the host's odds; the guest draws the same subtle in-world cues as the host's
// ZONELOOK (a few reeds / lily pads / a buoy / ice floes; no labels, no lights, no colliders) from the hidden lines
//   [FT] ZONE|<scene>|<idx>|<name>|<x>|<y>|<z>|<r>|<cue>|<target>      (on placement + every 30 s)
//   [FT] ZONECAST|<player>|<idx>|<name>                                 (a cast that landed in that zone)
// The cue geometry is a copy of ZONELOOK.Build (same per-name seed, so host and guests see the same reeds).
// A zone not re-announced for 75 s is removed (the host moved the zones: island, !water, new stage).
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public static class FishingZoneCues
{
    public static Action<string> Log;
    static void L(string s) { try { if (Log != null) Log("[zones] " + s); } catch { } }

    class Zone { public string scene, name, cue, target; public int idx; public float x, y, z, r, seen; public GameObject root; public string key; }
    static readonly Dictionary<int, Zone> zones = new Dictionary<int, Zone>();
    static Material reed, reedTop, pad, bloom, red, white, ice;
    static int errs;

    // returns the text to flash on the panel for the local player's zone cast (or null); true = the line was a zone line
    public static bool Line(string[] p, string me, out string flash)
    {
        flash = null;
        try
        {
            var ci = CultureInfo.InvariantCulture;
            if (p[0] == "ZONE" && p.Length >= 10)
            {
                int idx = int.Parse(p[2], ci);
                var z = new Zone { scene = p[1], idx = idx, name = p[3], x = float.Parse(p[4], ci), y = float.Parse(p[5], ci), z = float.Parse(p[6], ci),
                                   r = float.Parse(p[7], ci), cue = p[8], target = p[9], seen = Time.time };
                z.key = z.scene + "|" + z.name + "|" + p[4] + "|" + p[6] + "|" + p[7] + "|" + z.cue;
                Zone old;
                if (zones.TryGetValue(idx, out old) && old.key == z.key) { old.seen = Time.time; return true; }
                if (old != null && old.root != null) UnityEngine.Object.Destroy(old.root);
                zones[idx] = z;
                L("zone " + idx + " '" + z.name + "' (" + z.cue + ", r " + z.r.ToString("0") + ") at " + z.x.ToString("0.0") + ", " + z.z.ToString("0.0") + " on " + z.scene);
                return true;
            }
            if (p[0] == "ZONECAST" && p.Length >= 4)
            {
                if (p[1] == me) flash = "Cast into " + p[3] + "!";
                return true;
            }
        }
        catch (Exception e) { if (errs++ < 10) L("line error " + string.Join("|", p) + ": " + e.Message); }
        return false;
    }

    public static bool Any { get { return zones.Count > 0; } }

    public static void Frame(string scene, bool hasWater, float waterY)
    {
        if (zones.Count == 0) return;
        try
        {
            List<int> drop = null;
            foreach (var kv in zones)
            {
                Zone z = kv.Value;
                if (z.scene != scene || Time.time - z.seen > 75f) { (drop = drop ?? new List<int>()).Add(kv.Key); continue; }
                float y = hasWater ? waterY : z.y;
                if (z.root == null) Build(z, y);
                else if (Math.Abs(z.root.transform.position.y - y) > 0.01f) z.root.transform.position = new Vector3(z.x, y, z.z);
            }
            if (drop != null) foreach (int i in drop) { Zone z = zones[i]; if (z.root != null) UnityEngine.Object.Destroy(z.root); zones.Remove(i); L("zone " + i + " '" + z.name + "' removed"); }
        }
        catch (Exception e) { if (errs++ < 10) L("frame error: " + e.Message); }
    }

    public static void Clear()
    {
        foreach (Zone z in zones.Values) if (z.root != null) UnityEngine.Object.Destroy(z.root);
        zones.Clear();
    }

    static Material Mat(Color c)
    {
        var sh = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var m = new Material(sh); m.color = c;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.15f);
        return m;
    }

    static void Part(GameObject root, PrimitiveType t, Vector3 pos, Vector3 scale, Vector3 euler, Material m)
    {
        var go = GameObject.CreatePrimitive(t);
        foreach (Component c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(c);
        go.name = "FishingZoneCuePart";
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale; go.transform.localRotation = Quaternion.Euler(euler);
        var r = go.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // same geometry as the host's ZONELOOK.Build (agents\guide\FishingZones.cs)
    static void Build(Zone p, float waterY)
    {
        if (reed == null)
        {
            reed = Mat(new Color(0.30f, 0.36f, 0.16f)); reedTop = Mat(new Color(0.42f, 0.30f, 0.16f)); pad = Mat(new Color(0.20f, 0.42f, 0.18f));
            bloom = Mat(new Color(0.95f, 0.70f, 0.80f)); red = Mat(new Color(0.75f, 0.12f, 0.08f)); white = Mat(new Color(0.92f, 0.92f, 0.90f));
            ice = Mat(new Color(0.86f, 0.92f, 0.96f));
        }
        var root = new GameObject("FishingZoneCue " + p.name + " (client)");
        root.transform.position = new Vector3(p.x, waterY, p.z);
        int hs = 7; foreach (char ch in p.name) hs = hs * 31 + ch; var r = new System.Random(hs & 0xffff);
        switch (p.cue)
        {
            case "reeds":
                for (int c = 0; c < 3; c++)
                {
                    double a = r.NextDouble() * Math.PI * 2; float d = p.r * (0.6f + (float)r.NextDouble() * 0.3f);
                    float cx = (float)Math.Cos(a) * d, cz = (float)Math.Sin(a) * d;
                    for (int k = 0; k < 6; k++)
                    {
                        float h = 1.0f + (float)r.NextDouble() * 1.0f, ox = cx + (float)(r.NextDouble() - 0.5) * 1.6f, oz = cz + (float)(r.NextDouble() - 0.5) * 1.6f;
                        var tilt = new Vector3((float)(r.NextDouble() - 0.5) * 14f, 0, (float)(r.NextDouble() - 0.5) * 14f);
                        Part(root, PrimitiveType.Cylinder, new Vector3(ox, h / 2f - 0.3f, oz), new Vector3(0.05f, h / 2f, 0.05f), tilt, reed);
                        if (k % 2 == 0) Part(root, PrimitiveType.Capsule, new Vector3(ox, h - 0.25f, oz), new Vector3(0.09f, 0.16f, 0.09f), tilt, reedTop);
                    }
                }
                break;
            case "lilies":
                for (int k = 0; k < 8; k++)
                {
                    double a = r.NextDouble() * Math.PI * 2; float d = p.r * (float)Math.Sqrt(r.NextDouble()) * 0.8f, s = 0.7f + (float)r.NextDouble() * 0.6f;
                    var pos = new Vector3((float)Math.Cos(a) * d, 0.03f, (float)Math.Sin(a) * d);
                    Part(root, PrimitiveType.Cylinder, pos, new Vector3(s, 0.01f, s), Vector3.zero, pad);
                    if (k % 3 == 0) Part(root, PrimitiveType.Sphere, pos + new Vector3(0, 0.07f, 0), new Vector3(0.16f, 0.12f, 0.16f), Vector3.zero, bloom);
                }
                break;
            case "buoy":
                Part(root, PrimitiveType.Sphere, new Vector3(1.5f, 0.1f, -1f), new Vector3(0.55f, 0.5f, 0.55f), Vector3.zero, red);
                Part(root, PrimitiveType.Cylinder, new Vector3(1.5f, 0.42f, -1f), new Vector3(0.12f, 0.25f, 0.12f), Vector3.zero, white);
                break;
            case "ice":
                for (int k = 0; k < 6; k++)
                {
                    double a = r.NextDouble() * Math.PI * 2; float d = p.r * (0.75f + (float)r.NextDouble() * 0.3f);
                    Part(root, PrimitiveType.Cube, new Vector3((float)Math.Cos(a) * d, 0.04f, (float)Math.Sin(a) * d),
                         new Vector3(1.0f + (float)r.NextDouble() * 1.6f, 0.12f, 0.8f + (float)r.NextDouble() * 1.2f), new Vector3(0, (float)r.NextDouble() * 180f, 0), ice);
                }
                break;
        }
        p.root = root;
    }
}
