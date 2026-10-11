// Guest view of the drone pets' fishing (host: DronePets<N> + Fishing<N>.PetCast). Compiled into the FishingClient payload.
// The drone itself is a real networked ally, so guests already see it; this draws its fishing line + orange bobber like the
// host's DronePets<N>.Render() does, from the host's hidden lines:
//   [FT] PETCAST|<owner>|<x>|<y>|<z>          the pet cast toward that point (bobber flies there, floats)
//   [FT] PETCATCH|<owner>|<species or none>   the pet's cast resolved (bobber reels back to the drone)
// The drone is found as the owner's minion (MinionOwnership) whose body name contains "Drone". Visual only.
// Driven from FishingClient (main thread): Line(p) from HandleFT, Frame() every frame in a run, Clear() on cleanup.
using System;
using System.Collections.Generic;
using System.Globalization;
using RoR2;
using UnityEngine;

public static class FishingPetLines
{
    public static Action<string> Log;
    static void L(string s) { try { if (Log != null) Log("[pets] " + s); } catch { } }

    class Pet
    {
        public string owner; public Vector3 target, reelFrom; public float castT = -100f, reelT = -100f, flight = 1f;
        public bool casting; public GameObject lineGo, bobber; public LineRenderer line;
        public CharacterBody body; public float nextFind;
    }
    static readonly Dictionary<string, Pet> pets = new Dictionary<string, Pet>();
    static Material lineMat;
    static int errs;

    public static bool Line(string[] p)
    {
        try
        {
            if (p[0] == "PETCAST" && p.Length >= 5)
            {
                var ci = CultureInfo.InvariantCulture;
                Pet pet = Get(p[1]);
                pet.target = new Vector3(float.Parse(p[2], ci), float.Parse(p[3], ci), float.Parse(p[4], ci));
                CharacterBody b = Body(pet);
                float d = b == null ? 10f : Vector3.Distance(new Vector3(b.corePosition.x, 0f, b.corePosition.z), new Vector3(pet.target.x, 0f, pet.target.z));
                pet.flight = Mathf.Clamp(0.4f + d / 25f, 0.4f, 1.6f);
                pet.castT = Time.time; pet.reelT = -100f; pet.casting = true;
                L(p[1] + "'s pet casts to " + pet.target.ToString("0.0") + (b == null ? " (drone not found yet)" : ""));
                return true;
            }
            if (p[0] == "PETCATCH" && p.Length >= 3)
            {
                Pet pet; if (!pets.TryGetValue(p[1], out pet) || !pet.casting) return true;
                pet.casting = false; pet.reelT = Time.time;
                pet.reelFrom = pet.bobber != null ? pet.bobber.transform.position : pet.target;
                L(p[1] + "'s pet: " + p[2]);
                return true;
            }
        }
        catch (Exception e) { if (errs++ < 10) L("line error: " + e.Message); }
        return false;
    }

    static Pet Get(string owner)
    {
        Pet p; if (!pets.TryGetValue(owner, out p)) pets[owner] = p = new Pet { owner = owner };
        return p;
    }

    static CharacterBody Body(Pet p)
    {
        if (p.body != null && p.body.healthComponent != null && p.body.healthComponent.alive) return p.body;
        if (Time.time < p.nextFind) return null;
        p.nextFind = Time.time + 1f; p.body = null;
        CharacterMaster ownerMaster = null;
        foreach (NetworkUser nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName == p.owner) ownerMaster = nu.master;
        if (ownerMaster == null) return null;
        foreach (CharacterMaster m in CharacterMaster.readOnlyInstancesList)
        {
            if (m == null || m.minionOwnership == null || m.minionOwnership.ownerMaster != ownerMaster) continue;
            CharacterBody b = m.GetBody();
            if (b != null && b.name.Contains("Drone")) { p.body = b; break; }
        }
        return p.body;
    }

    static void EnsureRig(Pet p)
    {
        if (p.lineGo != null && p.bobber != null) return;
        Kill(p);
        if (lineMat == null) { Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored"); if (sh != null) lineMat = new Material(sh); }
        p.lineGo = new GameObject(p.owner + "'s pet line (client)");
        p.line = p.lineGo.AddComponent<LineRenderer>();
        p.line.positionCount = 2; p.line.useWorldSpace = true; p.line.startWidth = 0.015f; p.line.endWidth = 0.015f;
        if (lineMat != null) p.line.material = lineMat;
        p.line.startColor = new Color(0.85f, 0.95f, 1f); p.line.endColor = new Color(0.85f, 0.95f, 1f);
        p.bobber = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        p.bobber.name = p.owner + "'s pet bobber (client)";
        foreach (Component c in p.bobber.GetComponents<Component>()) if (c != null && c.GetType().Name.EndsWith("Collider")) UnityEngine.Object.Destroy(c);
        p.bobber.transform.localScale = Vector3.one * 0.18f;
        var rend = p.bobber.GetComponent<Renderer>(); if (rend != null) { if (lineMat != null) rend.material = new Material(lineMat); rend.material.color = new Color(1f, 0.6f, 0.1f); }
    }

    static void Kill(Pet p)
    {
        try { if (p.lineGo != null) UnityEngine.Object.Destroy(p.lineGo); if (p.bobber != null) UnityEngine.Object.Destroy(p.bobber); } catch { }
        p.lineGo = null; p.bobber = null; p.line = null;
    }

    public static void Frame()
    {
        if (pets.Count == 0) return;
        try
        {
            float t = Time.time;
            foreach (Pet p in pets.Values)
            {
                if (p.casting && t - p.castT > 40f) { p.casting = false; }   // missed PETCATCH: don't leave the line out forever
                bool reeling = p.reelT > p.castT && t - p.reelT < 0.5f;
                CharacterBody pb = (p.casting || reeling) ? Body(p) : null;
                if (pb == null) { if (p.lineGo != null) { p.lineGo.SetActive(false); p.bobber.SetActive(false); } continue; }
                EnsureRig(p);
                Vector3 tip = pb.corePosition + Vector3.down * 0.4f, b;
                if (reeling)
                {
                    float k = (t - p.reelT) / 0.5f;
                    b = Vector3.Lerp(p.reelFrom, tip, k) + Vector3.up * (1.5f * Mathf.Sin(Mathf.PI * k));
                }
                else if (t - p.castT < p.flight)
                {
                    float k = (t - p.castT) / p.flight;
                    float apex = Mathf.Min(10f, 0.25f * Vector3.Distance(new Vector3(tip.x, 0, tip.z), new Vector3(p.target.x, 0, p.target.z)));
                    b = Vector3.Lerp(tip, p.target, k) + Vector3.up * (4f * apex * k * (1f - k));
                }
                else b = p.target + Vector3.up * (Mathf.Sin(t * 3f) * 0.06f);
                p.bobber.transform.position = b;
                p.line.SetPosition(0, tip); p.line.SetPosition(1, b);
                p.lineGo.SetActive(true); p.bobber.SetActive(true);
            }
        }
        catch (Exception e) { if (errs++ < 10) L("frame error: " + e.Message); }
    }

    public static void Clear()
    {
        foreach (Pet p in pets.Values) { Kill(p); p.casting = false; p.body = null; }
    }
}
