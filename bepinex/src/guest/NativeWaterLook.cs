// NativeWaterLook: gives a big flat water sheet (host "FishingWater" / guest "FishingClientWater") Risk of Rain 2's
// OWN water material instead of the procedural ripple texture of WaterLook.cs.
//  * Material: Gilded Coast's sea "matGSWater" (Addressables key RoR2/Base/goldshores/matGSWater.mat,
//    shader "CalmWater/Calm Water [DX11] [Double Sided]", large+small wave normals, cubemap reflection), loaded by reflection (no extra compile references beyond
//    UnityEngine.CoreModule) and applied as an INSTANCE - the game's asset is never modified.
//  * Only change from the stock material: world-space wave UVs (_WorldSpace=1) so the waves tile across the
//    2000 m sheet instead of stretching the cube's 0..1 UVs. Further overrides: Floats/Colors/TexScale.
//  * The shader animates itself (_Time-scrolled normals, depth fade, refraction): no per-frame work needed.
//    Animate(sheet) is a cheap ~1 s re-check that re-applies if someone replaced the material.
//  * Main thread only.
//  * The material has NO _MainTex: code that reads water.material.mainTexture gets null and Unity logs
//    "doesn't have a texture property '_MainTex'" - guard with HasProperty("_MainTex").
//
// CLIENT WIRING (FISHING.exe, src\FishingClient.cs, water object "FishingClientWater"):
//  1. build.bat: add  ..\host\NativeWaterLook.cs  to the FishingClient.dll csc line (next to WaterLook.cs).
//     No new -r: references needed (Addressables is used by reflection).
//  2. Where the sheet is created (~line 984, after Prim("Cube","FishingClientWater",...)):
//        NativeWaterLook.Log = Log;
//        if (!NativeWaterLook.Apply((UnityEngine.GameObject)go))      // game water; fall back to old look
//            waterLook = FishingWaterLook.Apply((UnityEngine.GameObject)go, null, Log);
//     (do NOT call FishingWaterLook.Apply when NativeWaterLook succeeded - its Recheck() re-textures the sheet)
//  3. In Update where waterLook.Animate() runs (~line 608), once a second:  NativeWaterLook.Animate((GameObject)water);
//  4. Where the water is destroyed (~lines 782/974): NativeWaterLook.Remove(go) is optional (sheet is destroyed);
//     call NativeWaterLook.Remove(go) BEFORE destroying the sheet so our material instance is freed (else one leaks per stage).
//  FishFx (fish under the lake) only reads the sheet's position, so it needs no change.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class FishingNativeWaterLook
{
    // Tried in order; the first one that loads is used. Override Keys/Key to pick another.
    public static string[] Keys = {
        "RoR2/Base/goldshores/matGSWater.mat",         // Gilded Coast: large + small waves, strong reflection (chosen)
        "RoR2/Base/golemplains/matGPWater.mat",        // Titanic Plains
        "RoR2/Base/foggyswamp/matFSWater.mat",         // Wetland Aspect
        "RoR2/DLC1/ancientloft/matAncientLoft_Water.mat",
    };
    public static string Key;                 // if set, tried first
    public static Action<string> Log;
    public const string Tag = " (FishingNative)";

    static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
    static readonly Dictionary<int, Material> before = new Dictionary<int, Material>();
    public static string UsedKey;

    static void L(string s) { try { if (Log != null) Log(s); } catch { } }

    static MethodInfo loadGeneric; static bool loadSearched;
    // Addressables.LoadAssetAsync<Material>(key).WaitForCompletion(), by reflection
    public static Material LoadMaterial(string key)
    {
        Material m;
        if (cache.TryGetValue(key, out m) && m != null) return m;
        try
        {
            if (!loadSearched)
            {
                loadSearched = true;
                foreach (var a in FishingScope.Assemblies())
                {
                    if (a.GetName().Name != "Unity.Addressables") continue;
                    Type t = a.GetType("UnityEngine.AddressableAssets.Addressables");
                    if (t == null) continue;
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (mi.Name != "LoadAssetAsync" || !mi.IsGenericMethodDefinition) continue;
                        var ps = mi.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(object)) { loadGeneric = mi.MakeGenericMethod(typeof(Material)); break; }
                    }
                }
                if (loadGeneric == null) L("Addressables.LoadAssetAsync not found");
            }
            if (loadGeneric == null) return null;
            object handle = loadGeneric.Invoke(null, new object[] { key });
            object res = handle.GetType().GetMethod("WaitForCompletion").Invoke(handle, null);
            m = res as Material;
            if (m != null) cache[key] = m;
            return m;
        }
        catch (Exception e) { L("load '" + key + "' failed: " + (e.InnerException ?? e).Message); return null; }
    }

    // true: use the current stage's own water material when the stage has one (big Calm Water surface)
    public static bool PreferStageWater = false;
    static Material StageWater()
    {
        Material best = null; float bestSize = 30f;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            var m = r.sharedMaterial;
            if (m == null || m.shader == null || !m.shader.name.StartsWith("CalmWater/") || m.name.EndsWith(Tag)) continue;
            float s = Math.Max(r.bounds.size.x, r.bounds.size.z);
            if (s > bestSize) { bestSize = s; best = m; }
        }
        return best;
    }

    static Material PickSource()
    {
        if (PreferStageWater) { var sw = StageWater(); if (sw != null) { UsedKey = "stage:" + sw.name; return sw; } }
        var list = new List<string>();
        if (!string.IsNullOrEmpty(Key)) list.Add(Key);
        list.AddRange(Keys);
        foreach (var k in list)
        {
            var m = LoadMaterial(k);
            if (m != null && m.shader != null && m.shader.isSupported) { UsedKey = k; return m; }
        }
        // fallback: any Calm Water material that is already loaded (e.g. the current stage's water)
        foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            if (m != null && m.shader != null && m.shader.name.StartsWith("CalmWater/") && !m.name.EndsWith(Tag)) { UsedKey = "loaded:" + m.name; return m; }
        return null;
    }

    public static bool IsApplied(GameObject sheet)
    {
        var r = sheet == null ? null : sheet.GetComponent<Renderer>();
        return r != null && r.sharedMaterial != null && r.sharedMaterial.name.EndsWith(Tag);
    }

    public static bool Apply(GameObject sheet)
    {
        if (sheet == null) return false;
        var r = sheet.GetComponent<Renderer>();
        if (r == null) { L(sheet.name + ": no renderer"); return false; }
        var src = PickSource();
        if (src == null) { L("no game water material found"); return false; }
        if (!before.ContainsKey(sheet.GetInstanceID()) && r.sharedMaterial != null && !r.sharedMaterial.name.EndsWith(Tag))
            before[sheet.GetInstanceID()] = r.sharedMaterial;
        var old = r.sharedMaterial;
        var m = new Material(src);
        m.name = src.name + Tag;
        Tune(m);
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        if (old != null && old.name.EndsWith(Tag)) UnityEngine.Object.Destroy(old);
        L("applied '" + src.name + "' (" + UsedKey + ", shader '" + m.shader.name + "', queue " + m.renderQueue + ") to '" + sheet.name + "' at " + sheet.transform.position);
        return true;
    }

    // Tweaks applied to our instance after copying (the game asset is never touched).
    // WorldSpace: sample the wave normals in world XZ so they tile across the 2000 m sheet instead of
    // stretching the sheet's 0..1 UVs (the stage meshes ship pre-tiled UVs; our cube does not).
    public static bool WorldSpace = true;
    // Optional overrides (empty = the game's look). An alternative calm "blue lake" tuning on matGPWater:
    //   Key = "RoR2/Base/golemplains/matGPWater.mat"; Colors["_DepthColor"] = new Color(0.10f,0.30f,0.38f,1f);
    //   Floats["_Depth"] = 0.35f; Floats["_Reflection"] = 0.35f; Floats["_BumpStrength"] = 0.3f; TexScale["_BumpMap"] = new Vector2(0.5f,0.5f);
    public static readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
    public static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
    public static readonly Dictionary<string, Vector4> Vectors = new Dictionary<string, Vector4>();
    public static readonly Dictionary<string, Vector2> TexScale = new Dictionary<string, Vector2>();
    static void Tune(Material m)
    {
        if (WorldSpace && m.HasProperty("_WorldSpace")) { m.SetFloat("_WorldSpace", 1f); m.EnableKeyword("_WORLDSPACE_ON"); }
        foreach (var kv in Floats) if (m.HasProperty(kv.Key)) m.SetFloat(kv.Key, kv.Value);
        foreach (var kv in Colors) if (m.HasProperty(kv.Key)) m.SetColor(kv.Key, kv.Value);
        foreach (var kv in Vectors) if (m.HasProperty(kv.Key)) m.SetVector(kv.Key, kv.Value);
        foreach (var kv in TexScale) if (m.HasProperty(kv.Key)) m.SetTextureScale(kv.Key, kv.Value);
    }
    public static void Retune(GameObject sheet) { if (IsApplied(sheet)) Tune(sheet.GetComponent<Renderer>().sharedMaterial); }

    // Every ~1 s: re-apply if someone swapped the material. Returns true if it re-applied.
    public static bool Animate(GameObject sheet)
    {
        if (sheet == null || IsApplied(sheet)) return false;
        return Apply(sheet);
    }

    public static void Remove(GameObject sheet)
    {
        if (sheet == null) return;
        var r = sheet.GetComponent<Renderer>();
        Material prev;
        if (r != null && before.TryGetValue(sheet.GetInstanceID(), out prev) && prev != null)
        {
            var cur = r.sharedMaterial;
            r.sharedMaterial = prev;
            if (cur != null && cur.name.EndsWith(Tag)) UnityEngine.Object.Destroy(cur);
        }
        before.Remove(sheet.GetInstanceID());
    }

    // Debug: one line with the shader's properties and current values.
    public static string Describe(Material m)
    {
        var sb = new System.Text.StringBuilder(m.name + " / " + m.shader.name + " q" + m.renderQueue + " kw[" + string.Join(",", m.shaderKeywords) + "]:");
        try
        {
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string p = m.shader.GetPropertyName(i);
                var t = m.shader.GetPropertyType(i);
                sb.Append(" " + p + "=");
                if (t == UnityEngine.Rendering.ShaderPropertyType.Color) sb.Append(m.GetColor(p));
                else if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) sb.Append(m.GetFloat(p).ToString("0.###"));
                else if (t == UnityEngine.Rendering.ShaderPropertyType.Vector) sb.Append(m.GetVector(p));
                else { var tx = m.GetTexture(p); sb.Append(tx == null ? "null" : tx.name); }
            }
        }
        catch (Exception e) { sb.Append(" (" + e.Message + ")"); }
        return sb.ToString();
    }
}
