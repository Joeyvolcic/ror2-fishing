// Rod / bobber skins of the fishing rod shop (guest side; the host's Fishing<N>_Rods.cs draws the same looks).
// The host announces each player's loadout as "[FT] ROD|<name>|<rodType>|<rodSkin>|<bobberSkin>" (on change + every 30 s).
// Rod skins: Default (wood), Bamboo, Carbon, Neon (cyan/pink pulse), Gold, Lunar Glass (translucent blue), Void (purple glow).
// Bobbers: Plain (red ball), Classic (red/white), Star, Duck, Skull. Children are named "RodSkinDeco" and live in the
// pole's / bobber's local space (unit cylinder = 2 tall, unit sphere = 1 wide), so they follow the rod whatever its scale.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class RodSkinLook
{
    public class State { public string key; public Material rodMat, glowMat; public readonly List<Material> mats = new List<Material>(); }

    static Shader lit, unlit;
    static bool shadersTried;

    // gameplay multiplier the guest needs to predict where the host lands the bobber
    public static float RangeMult(string rod) { return rod == "Stick Rod" ? 0.75f : rod == "Long Cast Rod" ? 1.25f : rod == "Golden Rod" ? 1.10f : 1f; }

    static void Shaders()
    {
        if (shadersTried) return; shadersTried = true;
        lit = Shader.Find("Standard");
        if (lit != null && !lit.isSupported) lit = null;
        unlit = Shader.Find("Sprites/Default");
        if (unlit == null) unlit = Shader.Find("Unlit/Color");
    }

    static Material Mat(State s, Color c, float metal, float gloss, Color? emit)
    {
        Material m;
        if (lit != null)
        {
            m = new Material(lit); m.color = c;
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
            if (emit.HasValue) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emit.Value); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; }
        }
        else { m = new Material(unlit); m.color = c; }
        s.mats.Add(m);
        return m;
    }
    static Material Glass(State s, Color c) { var m = new Material(unlit ?? lit); m.color = c; m.renderQueue = 3001; s.mats.Add(m); return m; }

    static void Deco(GameObject parent, PrimitiveType kind, Vector3 pos, Vector3 scale, Vector3 euler, Material mat)
    {
        var go = GameObject.CreatePrimitive(kind);
        go.name = "RodSkinDeco";
        foreach (var comp in go.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(comp);   // no PhysicsModule reference needed
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos; go.transform.localScale = scale; go.transform.localRotation = Quaternion.Euler(euler);
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
    }

    static void ClearDeco(GameObject go)
    {
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            var c = go.transform.GetChild(i);
            if (c.name == "RodSkinDeco") { c.name = "RodSkinDeco_old"; c.SetParent(null); UnityEngine.Object.Destroy(c.gameObject); }
        }
    }

    static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }
    static Color C(float r, float g, float b) { return new Color(r, g, b); }

    public static void Apply(State s, GameObject pole, GameObject bob, LineRenderer line, string skin, string bobber)
    {
        Shaders();
        foreach (var m in s.mats) if (m != null) UnityEngine.Object.Destroy(m);
        s.mats.Clear(); s.rodMat = null; s.glowMat = null;
        ClearDeco(pole); ClearDeco(bob);
        Vector3 Z = Vector3.zero;
        Material pm;
        switch (skin)
        {
            case "Bamboo":
                pm = Mat(s, C(0.80f, 0.72f, 0.38f), 0f, 0.35f, null);
                var node = Mat(s, C(0.48f, 0.40f, 0.16f), 0f, 0.3f, null);
                foreach (float y in new[] { -0.72f, -0.3f, 0.12f, 0.52f, 0.86f }) Deco(pole, PrimitiveType.Cylinder, V(0, y, 0), V(1.3f, 0.03f, 1.3f), Z, node);
                break;
            case "Carbon":
                pm = Mat(s, C(0.05f, 0.05f, 0.06f), 0.4f, 0.92f, null);
                Deco(pole, PrimitiveType.Cylinder, V(0, -0.78f, 0), V(1.45f, 0.18f, 1.45f), Z, Mat(s, C(0.15f, 0.15f, 0.16f), 0f, 0.2f, null));
                Deco(pole, PrimitiveType.Cylinder, V(0, -0.56f, 0), V(1.25f, 0.02f, 1.25f), Z, Mat(s, C(0.9f, 0.1f, 0.1f), 0.3f, 0.8f, C(0.4f, 0.02f, 0.02f)));
                break;
            case "Gold":
                pm = Mat(s, C(1f, 0.78f, 0.22f), 1f, 0.85f, C(0.35f, 0.24f, 0.04f));
                var g2 = Mat(s, C(1f, 0.9f, 0.5f), 1f, 0.95f, C(0.6f, 0.45f, 0.1f));
                Deco(pole, PrimitiveType.Cylinder, V(0, -0.8f, 0), V(1.4f, 0.12f, 1.4f), Z, g2);
                Deco(pole, PrimitiveType.Sphere, V(0, 1f, 0), V(1.5f, 0.05f, 1.5f), Z, g2);
                break;
            case "Neon":
                pm = Mat(s, C(0f, 1f, 1f), 0f, 0.6f, C(0f, 2f, 2f));
                break;
            case "Lunar Glass":
                pm = Glass(s, new Color(0.5f, 0.78f, 1f, 0.42f));
                Deco(pole, PrimitiveType.Cylinder, Z, V(0.35f, 1f, 0.35f), Z, Glass(s, new Color(0.8f, 0.95f, 1f, 0.55f)));
                break;
            case "Void":
                pm = Mat(s, C(0.16f, 0.03f, 0.28f), 0.2f, 0.7f, C(0.45f, 0.08f, 0.9f));
                s.glowMat = Glass(s, new Color(0.55f, 0.1f, 1f, 0.2f));
                Deco(pole, PrimitiveType.Cylinder, Z, V(2.2f, 1.02f, 2.2f), Z, s.glowMat);
                break;
            default:
                pm = Mat(s, C(0.45f, 0.27f, 0.10f), 0f, 0.3f, null);
                break;
        }
        s.rodMat = pm;
        var pr = pole.GetComponent<Renderer>(); if (pr != null) pr.sharedMaterial = pm;
        if (line != null && skin != "Neon")
        {
            Color lc = skin == "Gold" ? C(1f, 0.9f, 0.5f) : skin == "Void" ? C(0.75f, 0.45f, 1f) : skin == "Lunar Glass" ? C(0.7f, 0.9f, 1f) : C(1, 1, 1);
            line.startColor = lc; line.endColor = lc;
        }

        Material bm;
        switch (bobber)
        {
            case "Classic":
                bm = Mat(s, C(0.97f, 0.97f, 0.97f), 0f, 0.6f, null);
                var red = Mat(s, C(0.9f, 0.06f, 0.06f), 0f, 0.6f, null);
                Deco(bob, PrimitiveType.Sphere, V(0, 0.23f, 0), V(1.04f, 0.56f, 1.04f), Z, red);
                Deco(bob, PrimitiveType.Cylinder, V(0, 0.72f, 0), V(0.1f, 0.25f, 0.1f), Z, red);
                break;
            case "Duck":
                bm = Mat(s, C(1f, 0.85f, 0.1f), 0f, 0.5f, null);
                var eye = Mat(s, C(0.02f, 0.02f, 0.02f), 0f, 0.9f, null);
                Deco(bob, PrimitiveType.Sphere, V(0, 0.55f, 0.28f), V(0.62f, 0.62f, 0.62f), Z, bm);
                Deco(bob, PrimitiveType.Cube, V(0, 0.5f, 0.66f), V(0.3f, 0.1f, 0.3f), Z, Mat(s, C(1f, 0.45f, 0.05f), 0f, 0.4f, null));
                Deco(bob, PrimitiveType.Sphere, V(0.15f, 0.66f, 0.52f), V(0.1f, 0.1f, 0.1f), Z, eye);
                Deco(bob, PrimitiveType.Sphere, V(-0.15f, 0.66f, 0.52f), V(0.1f, 0.1f, 0.1f), Z, eye);
                Deco(bob, PrimitiveType.Cube, V(0, 0.25f, -0.5f), V(0.3f, 0.22f, 0.25f), V(-30f, 0, 0), bm);
                break;
            case "Skull":
                bm = Mat(s, C(0.93f, 0.9f, 0.8f), 0f, 0.3f, null);
                var hole = Mat(s, C(0.02f, 0.02f, 0.02f), 0f, 0.1f, null);
                Deco(bob, PrimitiveType.Sphere, V(0.19f, 0.08f, 0.38f), V(0.27f, 0.27f, 0.27f), Z, hole);
                Deco(bob, PrimitiveType.Sphere, V(-0.19f, 0.08f, 0.38f), V(0.27f, 0.27f, 0.27f), Z, hole);
                Deco(bob, PrimitiveType.Sphere, V(0, -0.12f, 0.46f), V(0.11f, 0.13f, 0.11f), Z, hole);
                Deco(bob, PrimitiveType.Cube, V(0, -0.38f, 0.12f), V(0.55f, 0.24f, 0.55f), Z, bm);
                break;
            case "Star":
                bm = Mat(s, C(1f, 0.85f, 0.15f), 0.6f, 0.8f, C(0.9f, 0.7f, 0.1f));
                for (int i = 0; i < 5; i++) Deco(bob, PrimitiveType.Cube, Z, V(0.22f, 1.75f, 0.22f), V(0, 0, i * 36f), bm);
                break;
            default:
                bm = Mat(s, C(0.95f, 0.1f, 0.1f), 0f, 0.5f, null);
                break;
        }
        var br = bob.GetComponent<Renderer>(); if (br != null) br.sharedMaterial = bm;
        if (bobber == "Plain" || bobber == "Classic") bob.transform.rotation = Quaternion.identity;
        s.key = skin + "|" + bobber;
    }

    // every frame after the rig was placed: pulses, line colour, bobber facing the angler
    public static void Animate(State s, GameObject bob, LineRenderer line, Transform angler, string skin, string bobber, float t)
    {
        if (s.rodMat != null)
        {
            if (skin == "Neon")
            {
                float k = 0.5f + 0.5f * (float)Math.Sin(t * 3.0);
                Color c = Color.Lerp(new Color(0f, 1f, 1f), new Color(1f, 0.15f, 0.8f), k);
                s.rodMat.color = c; if (s.rodMat.HasProperty("_EmissionColor")) s.rodMat.SetColor("_EmissionColor", c * 2.2f);
                if (line != null) { line.startColor = c; line.endColor = c; }
            }
            else if (skin == "Void")
            {
                float k = 0.75f + 0.45f * (float)Math.Sin(t * 2.2);
                if (s.rodMat.HasProperty("_EmissionColor")) s.rodMat.SetColor("_EmissionColor", new Color(0.45f, 0.08f, 0.9f) * k);
                if (s.glowMat != null) s.glowMat.color = new Color(0.55f, 0.1f, 1f, 0.12f + 0.1f * k);
            }
        }
        if ((bobber == "Duck" || bobber == "Skull") && angler != null)
        {
            Vector3 d = angler.position - bob.transform.position; d.y = 0;
            if (d.sqrMagnitude > 0.01f) bob.transform.rotation = Quaternion.LookRotation(d.normalized);
        }
        else if (bobber == "Star") bob.transform.rotation = Quaternion.Euler(0, t * 90f, 0);
    }
}
