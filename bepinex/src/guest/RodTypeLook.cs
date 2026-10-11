// RodTypeLook - every rod TYPE gets its own model (UnityEngine only; host Fishing<N>, rod stand display, FISHING.exe).
//  The rig's pole stays what every other add-on expects: a unit cylinder "FishingPole" (local y -1 = handle .. +1 = tip,
//  half-length = localScale.y), rescaled by WhipAnim<N>, re-anchored in the hand by RodHandAttach, bent by CastAnim (which
//  bends ALL child meshes), skinned by the shop / case skins (children "RodSkinDeco" + the pole renderer's material).
//  This file only ADDS one child "RodTypeDeco" (counter-scaled so its parts keep real-world proportions whatever the pole
//  scale) and hides the pole's own renderer while a type model is shown. Shaft parts copy the pole renderer's material every
//  frame, so the equipped SKIN is the colour/material on top of the type's shape.
//    Apply(pole, rodType)   build (or clear for null/unknown); Tick(pole) every frame after the skin was applied.
//  Lengths: all types share the pole's length (the line, hand grip and bend are keyed to it), so the types differ in
//  thickness, taper, grip, reel and attachments.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class RodTypeLook
{
    public const string Holder = "RodTypeDeco";
    public static readonly string[] Types = { "Stick Rod", "Basic Rod", "Long Cast Rod", "Steady Rod", "Quick Rod", "Lucky Rod", "Golden Rod" };

    // mat: 0 = skin (pole material), 1 metal, 2 dark rubber, 3 cork, 4 gold, 5 gem, 6 twine, 7 knot/bark, 8 clover green, 9 iron, 10 trigger red
    class Part { public Transform t; public float u, sy; public bool yIsU, shaft; public Vector3 off; }
    class State : MonoBehaviour
    {
        public string type; public Transform holder; public float L = -1f;
        public readonly List<Part> parts = new List<Part>();
        public readonly List<Renderer> shaft = new List<Renderer>();
        public readonly List<Material> mats = new List<Material>();
        void OnDestroy() { foreach (var m in mats) if (m != null) Destroy(m); }
    }

    static Shader lit, flat;
    static Material Mat(State s, int kind)
    {
        if (lit == null) { lit = Shader.Find("Standard"); if (lit != null && !lit.isSupported) lit = null; }
        if (flat == null) flat = Shader.Find("Hidden/Internal-Colored");
        Color c; float metal = 0f, gloss = 0.3f; bool glow = false;
        switch (kind)
        {
            case 1: c = new Color(0.78f, 0.8f, 0.84f); metal = 0.35f; gloss = 0.7f; break;
            case 2: c = new Color(0.08f, 0.08f, 0.09f); gloss = 0.15f; break;
            case 3: c = new Color(0.72f, 0.56f, 0.36f); gloss = 0.1f; break;
            case 4: c = new Color(1f, 0.8f, 0.28f); metal = 0.45f; gloss = 0.8f; break;
            case 5: c = new Color(0.2f, 0.95f, 1f); glow = true; break;
            case 6: c = new Color(0.78f, 0.68f, 0.46f); gloss = 0.05f; break;
            case 7: c = new Color(0.28f, 0.17f, 0.07f); gloss = 0.1f; break;
            case 8: c = new Color(0.15f, 0.75f, 0.25f); gloss = 0.4f; break;
            case 9: c = new Color(0.55f, 0.56f, 0.6f); metal = 0.35f; gloss = 0.5f; break;
            case 10: c = new Color(0.85f, 0.12f, 0.1f); gloss = 0.5f; break;
            default: c = Color.white; break;
        }
        Material m;
        if (glow || lit == null) { m = new Material(flat ?? lit); m.color = c; if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1); if (m.HasProperty("_Cull")) m.SetInt("_Cull", 2); m.renderQueue = 2000; }
        else { m = new Material(lit); m.color = c; if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal); if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss); }
        s.mats.Add(m); return m;
    }

    // part along the rod: u = -1 (butt) .. 1 (tip); off = sideways offset in metres; size in metres, except the
    // y size which is in "u units" (fraction of the half-length) when yIsU
    static Transform P(State s, PrimitiveType kind, float u, Vector3 off, Vector3 euler, float sx, float sy, float sz, bool yIsU, int mat)
    {
        var g = GameObject.CreatePrimitive(kind);
        g.name = "RodTypePart";
        foreach (var comp in g.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(comp);
        g.transform.SetParent(s.holder, false);
        g.transform.localRotation = Quaternion.Euler(euler);
        var r = g.GetComponent<Renderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        var p = new Part { t = g.transform, u = u, sy = sy, yIsU = yIsU, shaft = mat == 0, off = off };
        p.t.localPosition = off; p.t.localScale = new Vector3(sx, yIsU ? 0.01f : sy, sz);
        if (mat == 0) s.shaft.Add(r); else r.sharedMaterial = Mat(s, mat);
        s.parts.Add(p);
        return p.t;
    }
    // shaft segment from u0 to u1 (diameter d metres)
    static void Seg(State s, float u0, float u1, float d, float x, float z, float tiltZ, int mat)
    { P(s, PrimitiveType.Cylinder, (u0 + u1) * 0.5f, new Vector3(x, 0f, z), new Vector3(0f, 0f, tiltZ), d, (u1 - u0) * 0.5f, d, true, mat); }
    static void Band(State s, float u, float d, float h, int mat) { P(s, PrimitiveType.Cylinder, u, Vector3.zero, Vector3.zero, d, h * 0.5f, d, false, mat); }
    static void Ball(State s, float u, Vector3 off, float d, int mat) { P(s, PrimitiveType.Sphere, u, off, Vector3.zero, d, d, d, false, mat); }
    static void Box(State s, float u, Vector3 off, Vector3 euler, Vector3 size, int mat) { P(s, PrimitiveType.Cube, u, off, euler, size.x, size.y, size.z, false, mat); }
    // spinning reel hanging under the rod (-x side): stem + spool (axis along z) + crank
    static void Reel(State s, float u, float spoolD, float spoolW, int mat, bool crank)
    {
        Box(s, u, new Vector3(-spoolD * 0.35f, 0f, 0f), Vector3.zero, new Vector3(spoolD * 0.45f, 0.012f, 0.012f), mat);
        P(s, PrimitiveType.Cylinder, u, new Vector3(-spoolD * 0.85f, 0f, 0f), new Vector3(90f, 0f, 0f), spoolD, spoolW * 0.5f, spoolD, false, mat);
        if (crank)
        {
            Box(s, u, new Vector3(-spoolD * 0.85f, spoolD * 0.25f, spoolW * 0.5f + 0.006f), Vector3.zero, new Vector3(0.008f, spoolD * 0.6f, 0.006f), 9);
            Ball(s, u, new Vector3(-spoolD * 0.85f, spoolD * 0.55f, spoolW * 0.5f + 0.014f), 0.016f, 2);
        }
    }

    public static void Clear(GameObject pole)
    {
        if (pole == null) return;
        var st = pole.GetComponent<State>(); if (st != null) UnityEngine.Object.Destroy(st);
        for (int i = pole.transform.childCount - 1; i >= 0; i--)
        {
            var c = pole.transform.GetChild(i);
            if (c.name == Holder) { c.name = Holder + "_old"; c.SetParent(null); UnityEngine.Object.Destroy(c.gameObject); }
        }
        var pr = pole.GetComponent<Renderer>(); if (pr != null) pr.enabled = true;
    }

    public static string Normalize(string t)
    {
        if (string.IsNullOrEmpty(t)) return "Stick Rod";
        foreach (string k in Types) if (string.Equals(k, t, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    public static void Apply(GameObject pole, string rodType)
    {
        if (pole == null) return;
        string t = Normalize(rodType);
        var old = pole.GetComponent<State>();
        if (old != null && old.type == t && old.holder != null) return;
        Clear(pole);
        if (t == null) return;
        var s = pole.AddComponent<State>(); s.type = t;
        s.holder = new GameObject(Holder).transform; s.holder.SetParent(pole.transform, false);
        switch (t)
        {
            case "Stick Rod":   // crooked branch: 4 jogged segments, knots at the joints, a twig stub, twine at the tip, no reel
                Seg(s, -1f, -0.42f, 0.05f, 0f, 0f, 2f, 0);
                Seg(s, -0.48f, 0.12f, 0.044f, 0.012f, 0.004f, -4f, 0);
                Seg(s, 0.06f, 0.62f, 0.036f, -0.006f, -0.006f, 5f, 0);
                Seg(s, 0.56f, 1f, 0.026f, 0.008f, 0f, -3f, 0);
                Ball(s, -0.45f, new Vector3(0.006f, 0f, 0f), 0.062f, 7);
                Ball(s, 0.09f, new Vector3(0.004f, 0f, -0.002f), 0.055f, 7);
                Ball(s, 0.59f, new Vector3(0.001f, 0f, 0f), 0.044f, 7);
                Ball(s, -0.75f, new Vector3(0.018f, 0f, 0.012f), 0.03f, 7);
                P(s, PrimitiveType.Cylinder, 0.32f, new Vector3(0.035f, 0f, 0f), new Vector3(0f, 0f, -42f), 0.018f, 0.06f, 0.018f, false, 7);   // twig stub
                Ball(s, 0.36f, new Vector3(0.062f, 0f, 0f), 0.022f, 8);                                                                       // a leaf bud
                for (int i = 0; i < 4; i++) Band(s, 0.86f + i * 0.03f, 0.034f, 0.008f, 6);                                                    // twine wrap
                Band(s, -0.98f, 0.054f, 0.02f, 7);
                break;
            case "Basic Rod":   // plain straight rod, rubber grip, small reel
                Seg(s, -1f, 1f, 0.035f, 0f, 0f, 0f, 0);
                Seg(s, -1f, -0.68f, 0.046f, 0f, 0f, 0f, 2);
                Reel(s, -0.56f, 0.055f, 0.022f, 1, true);
                foreach (float u in new[] { -0.1f, 0.35f, 0.75f }) Band(s, u, 0.04f, 0.006f, 1);
                break;
            case "Long Cast Rod":   // extra thin and tapered, cork grip, MANY guide rings with stand-offs
                Seg(s, -1f, -0.05f, 0.028f, 0f, 0f, 0f, 0);
                Seg(s, -0.08f, 1f, 0.014f, 0f, 0f, 0f, 0);
                Seg(s, -1f, -0.74f, 0.038f, 0f, 0f, 0f, 3);
                Reel(s, -0.62f, 0.048f, 0.018f, 1, true);
                for (int i = 0; i < 9; i++)
                {
                    float u = -0.45f + i * 0.165f, d = Mathf.Lerp(0.034f, 0.016f, i / 8f);
                    Box(s, u, new Vector3(-0.016f, 0f, 0f), Vector3.zero, new Vector3(0.03f, 0.005f, 0.005f), 1);
                    P(s, PrimitiveType.Cylinder, u, new Vector3(-0.034f, 0f, 0f), new Vector3(90f, 0f, 0f), d, 0.003f, d, false, 1);
                }
                Band(s, 0.995f, 0.02f, 0.012f, 1);
                break;
            case "Steady Rod":   // thick and sturdy, long cork grip + fore grip, BIG reel, butt cap
                Seg(s, -1f, 1f, 0.056f, 0f, 0f, 0f, 0);
                Seg(s, -1f, -0.5f, 0.074f, 0f, 0f, 0f, 3);
                Seg(s, -0.38f, -0.26f, 0.068f, 0f, 0f, 0f, 3);
                Ball(s, -1f, Vector3.zero, 0.08f, 2);
                Reel(s, -0.44f, 0.115f, 0.05f, 9, true);
                foreach (float u in new[] { 0f, 0.45f, 0.85f }) Band(s, u, 0.066f, 0.012f, 9);
                break;
            case "Quick Rod":   // short and light: stubby butt + a thin whippy tip, pistol grip, closed trigger reel
                Seg(s, -1f, 0.15f, 0.03f, 0f, 0f, 0f, 0);
                Seg(s, 0.12f, 1f, 0.011f, 0f, 0f, 0f, 0);
                Box(s, -0.8f, new Vector3(-0.022f, 0f, 0f), new Vector3(0f, 0f, 20f), new Vector3(0.03f, 0.13f, 0.034f), 2);   // pistol grip
                Ball(s, -0.55f, new Vector3(0.03f, 0f, 0f), 0.06f, 1);                                                      // closed-face reel on top
                P(s, PrimitiveType.Cylinder, -0.47f, new Vector3(0.03f, 0f, 0f), Vector3.zero, 0.036f, 0.025f, 0.036f, false, 1);
                Box(s, -0.64f, new Vector3(-0.03f, 0f, 0f), new Vector3(0f, 0f, -25f), new Vector3(0.012f, 0.04f, 0.012f), 10);   // trigger
                Band(s, 0.13f, 0.034f, 0.01f, 10);
                break;
            case "Lucky Rod":   // straight rod with a four-leaf clover charm and a dangling horseshoe
                Seg(s, -1f, 1f, 0.035f, 0f, 0f, 0f, 0);
                Seg(s, -1f, -0.7f, 0.046f, 0f, 0f, 0f, 2);
                Reel(s, -0.56f, 0.055f, 0.022f, 1, true);
                Box(s, -0.3f, new Vector3(-0.045f, 0f, 0f), Vector3.zero, new Vector3(0.06f, 0.003f, 0.003f), 6);   // charm string
                for (int i = 0; i < 4; i++)
                {
                    float a = i * Mathf.PI / 2f + 0.785f;
                    P(s, PrimitiveType.Sphere, -0.3f, new Vector3(-0.085f + Mathf.Cos(a) * 0.016f, Mathf.Sin(a) * 0.016f, 0f), new Vector3(0f, 0f, a * Mathf.Rad2Deg), 0.026f, 0.02f, 0.008f, false, 8);
                }
                Box(s, -0.3f, new Vector3(-0.09f, -0.03f, 0f), new Vector3(0f, 0f, 15f), new Vector3(0.004f, 0.03f, 0.004f), 8);   // clover stem
                // horseshoe hanging from u 0.35 on a short string
                Box(s, 0.35f, new Vector3(-0.04f, 0f, 0f), Vector3.zero, new Vector3(0.06f, 0.003f, 0.003f), 6);
                float hx = -0.1f;
                for (int i = 0; i <= 6; i++)
                {
                    float a = Mathf.Lerp(-20f, 200f, i / 6f) * Mathf.Deg2Rad;
                    Box(s, 0.35f, new Vector3(hx - Mathf.Sin(a) * 0.025f, Mathf.Cos(a) * 0.025f, 0f), new Vector3(0f, 0f, -a * Mathf.Rad2Deg), new Vector3(0.012f, 0.016f, 0.008f), 9);
                }
                break;
            case "Golden Rod":   // ornate: gold trim bands, gold reel, a glowing gem in a gold setting, gold pommel and tip
                Seg(s, -1f, 1f, 0.04f, 0f, 0f, 0f, 0);
                Seg(s, -1f, -0.66f, 0.05f, 0f, 0f, 0f, 2);
                foreach (float u in new[] { -0.95f, -0.66f, -0.3f, 0.1f, 0.5f, 0.85f }) Band(s, u, 0.054f, 0.012f, 4);
                for (int i = 0; i < 5; i++) Band(s, -0.92f + i * 0.06f, 0.052f, 0.004f, 4);
                Ball(s, -1f, Vector3.zero, 0.065f, 4);
                Reel(s, -0.54f, 0.062f, 0.024f, 4, true);
                Ball(s, -0.5f, new Vector3(0.03f, 0f, 0f), 0.034f, 4);                     // gem setting
                Ball(s, -0.5f, new Vector3(0.042f, 0f, 0f), 0.024f, 5);                    // gem
                Ball(s, 1f, Vector3.zero, 0.022f, 4);
                break;
        }
        Layout(s, pole.transform, true);
        Tick(pole);
    }

    static void Layout(State s, Transform pole, bool force)
    {
        Vector3 ps = pole.lossyScale;
        float L = Mathf.Abs(ps.y), X = Mathf.Max(1e-4f, Mathf.Abs(ps.x)), Z = Mathf.Max(1e-4f, Mathf.Abs(ps.z));
        if (!force && Mathf.Abs(L - s.L) < 1e-4f && Mathf.Abs(s.holder.localScale.x * X - 1f) < 1e-3f && Mathf.Abs(s.holder.localScale.z * Z - 1f) < 1e-3f) return;
        s.L = L;
        s.holder.localPosition = Vector3.zero; s.holder.localRotation = Quaternion.identity;
        s.holder.localScale = new Vector3(1f / X, 1f / Mathf.Max(1e-4f, L), 1f / Z);   // holder space = metres
        foreach (var p in s.parts)
        {
            if (p.t == null) continue;
            p.t.localPosition = p.off + new Vector3(0f, p.u * L, 0f);
            if (p.yIsU) { var sc = p.t.localScale; sc.y = p.sy * L; p.t.localScale = sc; }
        }
    }

    // every frame (after skins): keep proportions under the pole's current scale, copy the skin material to the shaft
    public static void Tick(GameObject pole)
    {
        if (pole == null) return;
        var s = pole.GetComponent<State>(); if (s == null || s.holder == null) return;
        Layout(s, pole.transform, false);
        var pr = pole.GetComponent<Renderer>();
        if (pr != null)
        {
            Material m = pr.sharedMaterial;
            foreach (var r in s.shaft) if (r != null && r.sharedMaterial != m) r.sharedMaterial = m;
            if (pr.enabled) pr.enabled = false;
        }
    }
}
