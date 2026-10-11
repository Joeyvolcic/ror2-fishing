// DRONE PETS add-on for the host's fishing game (server side, JV94's game).
//  Chat:  !pet             buy a drone pet for $3,000 fishing money (Gunner Drone), or turn yours back on
//         !pet <type>      buy one with that look / change your pet's look (gunner, healing, missile, equipment)
//         !pet off | on    hide / bring back your pet (keeps ownership)
//  The pet is a real ally drone (MasterSummon, player team, owner = leader) whose AI skill drivers that use skills or
//  equipment are switched off, so it only follows its owner. Peaceful2 only kills Monster/Lunar/Void teams and
//  FishItems4 only strips player inventories / item sources, so neither touches it.
//  Auto-fishing: every 20-30 s while its owner is alive the pet asks the newest Fishing<N>.PetCast(owner, drone pos);
//  that version picks the nearest school fish in reach, broadcasts [FT] PETCAST / [FT] PETCATCH and credits the owner.
//  This class only draws the pet's line + bobber on the host (rig "<owner>'s pet") and keeps the drone alive / near.
//  Older Fishing versions without PetCast: the pet just follows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;

public static class DronePets1
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static bool enabled = true;
    public static int price = 3000;
    public static float castMin = 20f, castMax = 30f;
    public static string grantQueue;          // test hook: "Name|Type" -> free pet (set by a test class, handled on the main thread)
    public static string removeQueue;         // test hook: "Name" -> delete that pet entirely (no refund)

    static readonly string[][] Types = {
        new[] { "Gunner Drone", "Drone1Master", "gunner" },
        new[] { "Healing Drone", "Drone2Master", "heal" },
        new[] { "Missile Drone", "MissileDroneMaster", "missile" },
        new[] { "Equipment Drone", "EquipmentDroneMaster", "equip" } };

    public class Pet
    {
        public string owner, type; public bool on = true;
        public CharacterMaster master;
        public float respawnAt, nextCastAt, farSince = -1f;
        // rig
        public GameObject lineGo, bobber; public LineRenderer line;
        public Vector3 target, reelFrom; public float castT = -100f, reelT = -100f, flight = 1f, biteT = 1e9f, resolveT = 1e9f; public bool casting;
    }
    public static readonly Dictionary<string, Pet> pets = new Dictionary<string, Pet>();
    static readonly List<string> chatQueue = new List<string>();
    static string lastLine;
    static readonly System.Random rng = new System.Random();
    static int errors;
    static bool cleaned;
    static Material lineMat;

    static void Log(string s) { try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_pets.log"), DateTime.Now.ToString("HH:mm:ss ") + "[DronePets1] " + s + Environment.NewLine); } catch { } }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = "<color=#ffcc66>" + text + "</color>" }); Log("CHAT " + text); }
    static void SayRaw(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); Log("SEND " + text); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    static Type fishType; static float fishLookAt = -100f;
    static Type Fishing()   // newest enabled Fishing<N>
    {
        if (fishType != null && Time.time - fishLookAt < 10f)
        {
            FieldInfo en = fishType.GetField("enabled", All);
            if (en != null && (bool)en.GetValue(null)) return fishType;
        }
        fishLookAt = Time.time; fishType = null; int best = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                Match m = Regex.Match(t.Name, "^Fishing(\\d+)$");
                if (!m.Success) continue;
                FieldInfo en = t.GetField("enabled", All);
                if (en == null || !(bool)en.GetValue(null)) continue;
                int n = int.Parse(m.Groups[1].Value);
                if (n > best) { best = n; fishType = t; }
            }
        return fishType;
    }

    public static void Run()
    {
        // adopt pets from an older DronePets version, then retire it (it leaves its drones alive for us)
        Type prev = null; int prevN = -1;
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
            {
                Match m = Regex.Match(t.Name, "^DronePets(\\d+)$");
                if (!m.Success || t == typeof(DronePets1)) continue;
                FieldInfo en = t.GetField("enabled", All); if (en != null) en.SetValue(null, false);
                int n = int.Parse(m.Groups[1].Value); if (n > prevN) { prevN = n; prev = t; }
            }
        if (prev != null)
        {
            try
            {
                var list = (List<object[]>)prev.GetMethod("Export", All).Invoke(null, null);
                foreach (object[] e in list) pets[(string)e[0]] = new Pet { owner = (string)e[0], type = (string)e[1], on = (bool)e[2], master = e[3] as CharacterMaster };
                Log("adopted " + list.Count + " pet(s) from " + prev.Name);
            }
            catch (Exception e) { Log("adopt error: " + e); }
        }
        RoR2Application.onFixedUpdate += Tick;
        RoR2Application.onUpdate += Render;
        Chat.onChatChanged += OnChat;
        Log("installed (price $" + price + ", casts every " + castMin + "-" + castMax + " s)");
    }

    // newer versions call this to take over the pets (owner, type, on, master)
    public static List<object[]> Export()
    {
        var l = new List<object[]>();
        foreach (Pet p in pets.Values) l.Add(new object[] { p.owner, p.type, p.on, p.master });
        return l;
    }

    static void OnChat()
    {
        if (!enabled) return;
        try
        {
            var log = Chat.readOnlyLog;
            if (log.Count == 0) return;
            string line = log[log.Count - 1];
            if (line == null || ReferenceEquals(line, lastLine)) return;
            lastLine = line;
            lock (chatQueue) chatQueue.Add(line);
        }
        catch { }
    }

    static NetworkUser User(string name)
    {
        foreach (var nu in NetworkUser.readOnlyInstancesList) if (nu != null && nu.userName == name) return nu;
        return null;
    }
    static CharacterBody OwnerBody(string name)
    {
        NetworkUser nu = User(name);
        if (nu == null || nu.master == null) return null;
        CharacterBody b = nu.master.GetBody();
        return b != null && b.healthComponent != null && b.healthComponent.alive ? b : null;
    }
    static CharacterBody PetBody(Pet p) { return p.master != null ? p.master.GetBody() : null; }

    // ------------------------------------------------------------------ main-thread tick (server)
    static void Tick()
    {
        if (!enabled) { if (!cleaned) { cleaned = true; foreach (Pet p in pets.Values) KillRig(p); Log("disabled (drones left for the newer version)"); } return; }
        if (!NetworkServer.active || RoR2.Run.instance == null) return;
        try
        {
            string[] lines = null;
            lock (chatQueue) { if (chatQueue.Count > 0) { lines = chatQueue.ToArray(); chatQueue.Clear(); } }
            if (lines != null) foreach (string l in lines) HandleLine(l);
            if (grantQueue != null) { string g = grantQueue; grantQueue = null; Grant(g); }
            if (removeQueue != null) { string r = removeQueue; removeQueue = null; Pet p; if (pets.TryGetValue(r, out p)) { Despawn(p, "test removal"); pets.Remove(r); Log("TEST removed pet of " + r); } }
            foreach (Pet p in new List<Pet>(pets.Values)) PetTick(p);
        }
        catch (Exception e) { if (errors++ < 20) Log("tick error: " + e); }
    }

    static void HandleLine(string raw)
    {
        string plain = Regex.Replace(raw, "<[^>]*>", "").Trim();
        int i = plain.IndexOf(": ");
        if (i <= 0) return;
        string name = plain.Substring(0, i).Trim(), text = plain.Substring(i + 2).Trim().ToLowerInvariant();
        if (text != "!pet" && !text.StartsWith("!pet ") && text != "!pets") return;
        if (User(name) == null) return;
        string arg = text.Length > 4 ? text.Substring(4).Trim() : "";
        Log(name + ": " + text);
        Pet p; pets.TryGetValue(name, out p);
        if (arg == "help" || text == "!pets")
        {
            Say("Drone pets: !pet buys one for $" + price.ToString("N0") + " (it fishes for you, weaker than you). !pet gunner/healing/missile/equipment picks its look, !pet off / !pet on.");
            return;
        }
        if (arg == "off")
        {
            if (p == null) { Say(name + " has no drone pet. Type !pet to buy one ($" + price.ToString("N0") + ")."); return; }
            p.on = false; Despawn(p, "!pet off"); Say(name + "'s drone pet powers down. (!pet on to bring it back)");
            return;
        }
        if (arg == "on" || arg == "")
        {
            if (p != null)
            {
                if (!p.on) { p.on = true; p.respawnAt = 0; Say(name + "'s " + p.type + " is back!"); }
                else Say(name + " already has a " + p.type + ". !pet gunner/healing/missile/equipment changes its look, !pet off hides it.");
                return;
            }
            Buy(name, Types[0][0]);
            return;
        }
        string type = null;
        foreach (string[] t in Types) if (arg.StartsWith(t[2]) || t[0].ToLowerInvariant().StartsWith(arg)) { type = t[0]; break; }
        if (type == null) { Say("Unknown pet type '" + arg + "'. Pick gunner, healing, missile or equipment."); return; }
        if (p == null) { Buy(name, type); return; }
        if (p.type == type && p.on) { Say(name + "'s pet is already a " + type + "."); return; }
        p.type = type; p.on = true; Despawn(p, "new look"); p.respawnAt = 0;
        Say(name + "'s pet is now a " + type + ".");
    }

    static void Buy(string name, string type)
    {
        Type f = Fishing();
        var money = f == null ? null : f.GetField("money", All).GetValue(null) as Dictionary<string, int>;
        if (money == null) { Say("Drone pets need the fishing game running."); return; }
        int have; money.TryGetValue(name, out have);
        if (have < price) { Say(name + " needs $" + price.ToString("N0") + " for a drone pet (has $" + have.ToString("N0") + ")."); return; }
        money[name] = have - price;
        pets[name] = new Pet { owner = name, type = type };
        Log("BUY " + name + " " + type + ": $" + have + " -> $" + money[name]);
        Say(name + " bought a " + type + " pet for $" + price.ToString("N0") + "! It follows you and fishes on its own. ($" + money[name].ToString("N0") + " left)");
        Board(f);
    }

    static void Grant(string spec)
    {
        string[] s = spec.Split('|');
        string name = s[0], type = s.Length > 1 ? s[1] : Types[0][0];
        Pet p; if (pets.TryGetValue(name, out p)) Despawn(p, "test grant");
        pets[name] = new Pet { owner = name, type = type };
        Log("TEST grant: free " + type + " for " + name + " (no money taken)");
    }

    static void Board(Type f)
    {
        try
        {
            var st = f.GetMethod("Standings", All).Invoke(null, null) as List<KeyValuePair<string, int>>;
            if (st == null) return;
            var parts = new List<string>();
            foreach (var kv in st) parts.Add(kv.Key + ":" + kv.Value);
            SayRaw("[FT] BOARD|" + string.Join("|", parts.ToArray()));
        }
        catch (Exception e) { Log("board error: " + e.Message); }
    }

    static void PetTick(Pet p)
    {
        CharacterBody ob = OwnerBody(p.owner);
        CharacterBody pb = PetBody(p);
        bool petAlive = pb != null && pb.healthComponent != null && pb.healthComponent.alive;
        if (!p.on) { if (p.master != null) Despawn(p, "off"); return; }
        if (User(p.owner) == null) { if (p.master != null) Despawn(p, "owner left"); return; }
        if (!petAlive)
        {
            if (p.casting) { p.casting = false; p.reelT = Time.time; }
            if (ob == null) return;                              // wait for the owner to be alive
            if (p.respawnAt == 0f) p.respawnAt = Time.time + (p.master == null ? 0.5f : 3f);   // a stage change may still respawn the old body
            if (Time.time < p.respawnAt) return;
            p.respawnAt = 0f;
            if (p.master != null) Despawn(p, "body gone");
            Spawn(p, ob);
            return;
        }
        p.respawnAt = 0f;
        if (ob == null) return;
        // keep it near: teleport back if it's been > 60 m away for 5 s
        float d = Vector3.Distance(pb.corePosition, ob.corePosition);
        if (d > 60f) { if (p.farSince < 0) p.farSince = Time.time; else if (Time.time - p.farSince > 5f) { TeleportHelper.TeleportBody(pb, ob.corePosition + Vector3.up * 3f + Vector3.right * 2f); p.farSince = -1f; Log(p.owner + "'s pet teleported back (was " + d.ToString("0") + " m away)"); } }
        else p.farSince = -1f;

        // auto-fishing
        if (p.casting && Time.time >= p.resolveT + 0.1f) { p.casting = false; p.reelT = Time.time; p.reelFrom = p.bobber != null ? p.bobber.transform.position : p.target; }
        if (p.casting || Time.time < p.nextCastAt) return;
        if (p.nextCastAt == 0f) { p.nextCastAt = Time.time + 8f + (float)rng.NextDouble() * 6f; return; }
        Type f = Fishing();
        MethodInfo cast = f == null ? null : f.GetMethod("PetCast", All);
        if (cast == null) { p.nextCastAt = Time.time + 30f; return; }
        Vector3 at = pb.corePosition;
        string res = (string)cast.Invoke(null, new object[] { p.owner, at.x, at.y, at.z });
        if (res == null || !res.StartsWith("ok|"))
        {
            p.nextCastAt = Time.time + 5f;
            if (res != lastRes) { lastRes = res; Log(p.owner + "'s pet: no cast (" + res + ")"); }
            return;
        }
        lastRes = null;
        string[] r = res.Split('|');
        var ci = CultureInfo.InvariantCulture;
        p.target = new Vector3(float.Parse(r[1], ci), float.Parse(r[2], ci), float.Parse(r[3], ci));
        p.flight = float.Parse(r[4], ci);
        p.resolveT = Time.time + float.Parse(r[5], ci);
        p.biteT = Time.time + float.Parse(r[6], ci);
        p.castT = Time.time; p.reelT = -100f; p.casting = true;
        p.nextCastAt = Time.time + castMin + (float)rng.NextDouble() * (castMax - castMin);
        Log(p.owner + "'s pet casts from " + V(at) + " to " + V(p.target) + " (" + res + ")");
    }
    static string lastRes;
    static string V(Vector3 v) { return "(" + v.x.ToString("0.0") + ", " + v.y.ToString("0.0") + ", " + v.z.ToString("0.0") + ")"; }

    static void Spawn(Pet p, CharacterBody ob)
    {
        string masterName = Types[0][1];
        foreach (string[] t in Types) if (t[0] == p.type) masterName = t[1];
        GameObject prefab = MasterCatalog.FindMasterPrefab(masterName);
        if (prefab == null) { Log("no master prefab " + masterName); p.respawnAt = Time.time + 30f; return; }
        Vector3 pos = ob.corePosition + Vector3.up * 3f + ob.transform.right * 2f;
        int off = 0, kept = 0;
        var ms = new MasterSummon
        {
            masterPrefab = prefab,
            position = pos,
            rotation = Quaternion.identity,
            summonerBodyObject = ob.gameObject,
            ignoreTeamMemberLimit = true,
            teamIndexOverride = TeamIndex.Player,
            preSpawnSetupCallback = m =>
            {
                foreach (AISkillDriver d in m.GetComponents<AISkillDriver>())
                {
                    if (d.skillSlot != SkillSlot.None || d.shouldFireEquipment || d.requireEquipmentReady) { d.enabled = false; off++; }
                    else kept++;
                }
            }
        };
        CharacterMaster master = ms.Perform();
        if (master == null) { Log("MasterSummon failed for " + p.owner); p.respawnAt = Time.time + 10f; return; }
        try { if (ob.master != null) master.minionOwnership.SetOwner(ob.master); } catch (Exception e) { Log("SetOwner: " + e.Message); }
        p.master = master; p.nextCastAt = 0f; p.casting = false;
        CharacterBody pb = master.GetBody();
        Log("SPAWN " + p.owner + "'s " + p.type + " (" + masterName + ") at " + V(pos) + " body=" + (pb != null ? pb.name : "none") + ", skill drivers off " + off + " / kept " + kept + " (follow only)");
    }

    static void Despawn(Pet p, string why)
    {
        p.casting = false;
        KillRig(p);
        if (p.master == null) return;
        try
        {
            GameObject body = p.master.GetBodyObject();
            if (body != null) NetworkServer.Destroy(body);
            NetworkServer.Destroy(p.master.gameObject);
        }
        catch (Exception e) { Log("despawn error: " + e.Message); }
        Log("DESPAWN " + p.owner + "'s " + p.type + " (" + why + ")");
        p.master = null;
    }

    // ------------------------------------------------------------------ host visuals: rig "<owner>'s pet"
    static void EnsureRig(Pet p)
    {
        if (p.lineGo != null && p.bobber != null) return;
        KillRig(p);
        if (lineMat == null) { Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored"); if (sh != null) lineMat = new Material(sh); }
        p.lineGo = new GameObject(p.owner + "'s pet line");
        p.line = p.lineGo.AddComponent<LineRenderer>();
        p.line.positionCount = 2; p.line.useWorldSpace = true; p.line.startWidth = 0.015f; p.line.endWidth = 0.015f;
        if (lineMat != null) p.line.material = lineMat;
        p.line.startColor = new Color(0.85f, 0.95f, 1f); p.line.endColor = new Color(0.85f, 0.95f, 1f);
        p.bobber = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        p.bobber.name = p.owner + "'s pet bobber";
        var col = p.bobber.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
        p.bobber.transform.localScale = Vector3.one * 0.18f;
        var rend = p.bobber.GetComponent<Renderer>(); if (rend != null) rend.material.color = new Color(1f, 0.6f, 0.1f);
    }
    static void KillRig(Pet p)
    {
        try { if (p.lineGo != null) UnityEngine.Object.Destroy(p.lineGo); if (p.bobber != null) UnityEngine.Object.Destroy(p.bobber); } catch { }
        p.lineGo = null; p.bobber = null; p.line = null;
    }

    static void Render()
    {
        if (!enabled) return;
        try
        {
            float t = Time.time;
            foreach (Pet p in pets.Values)
            {
                CharacterBody pb = PetBody(p);
                bool reeling = p.reelT > p.castT && t - p.reelT < 0.5f;
                if (pb == null || (!p.casting && !reeling)) { if (p.lineGo != null) { p.lineGo.SetActive(false); p.bobber.SetActive(false); } continue; }
                EnsureRig(p);
                Vector3 tip = pb.corePosition + Vector3.down * 0.4f;
                Vector3 b;
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
                else if (t >= p.biteT)
                    b = p.target + new Vector3(Mathf.Sin(t * 53f) * 0.06f, -0.25f + Mathf.Sin(t * 31f) * 0.1f, Mathf.Cos(t * 47f) * 0.06f);
                else b = p.target + Vector3.up * (Mathf.Sin(t * 3f) * 0.06f);
                p.bobber.transform.position = b;
                p.line.SetPosition(0, tip); p.line.SetPosition(1, b);
                p.lineGo.SetActive(true); p.bobber.SetActive(true);
            }
        }
        catch (Exception e) { if (errors++ < 20) Log("render error: " + e); }
    }
}


