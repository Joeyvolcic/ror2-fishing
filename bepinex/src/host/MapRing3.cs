// Injected into JV94's (host) game. On every stage, once the players have landed, spawns a ring of free
// Lunar Seer stations (the Bazaar's map-preview podiums) around the spawn area, one per playable map.
// The first player to use one picks the next map: chat countdown, then Run.AdvanceStage(that map).
// The podiums are real networked objects, so guests see the previews natively. Host-only floating labels.
using System;
using System.Collections.Generic;
using System.Reflection;
using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

public static class MapRing3
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string PrefabKey = "RoR2/Base/bazaar/SeerStation.prefab";
    const string Tag = "SeerStation_MapRing";
    public static bool enabled = true;

    class Podium { public GameObject go; public SceneDef scene; public Vector3 pos; public Quaternion rot; public TextMesh label; public int respawns; }

    static GameObject prefab;
    static readonly List<Podium> ring = new List<Podium>();
    static Stage lastStage;
    static float stageSeenAt, groundedAt = -1f, chosenAt = -1f, nextCheck;
    static bool spawnedThisStage, choiceMade, travelled;
    static SceneDef chosen;
    static string chooser;
    static int countdownShown, ticks;

    static void Log(string s) { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ror2_mapring.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }
    static void Say(string text) { Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }); }

    static Type[] SafeTypes(Assembly a)
    {
        try { return FishingScope.TypesOf(a); }
        catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); }
        catch { return new Type[0]; }
    }

    public static void Run()
    {
        foreach (var a in FishingScope.Assemblies())
            foreach (Type t in SafeTypes(a))
                if (t.Name.StartsWith("MapRing") && t.Name != typeof(MapRing3).Name)
                {
                    FieldInfo f = t.GetField("enabled", All);
                    if (f != null) f.SetValue(null, false);
                    MethodInfo c = t.GetMethod("Cleanup", All);
                    if (c != null) RoR2Application.onNextUpdate += () => { try { c.Invoke(null, null); } catch { } };
                }
        RoR2Application.onFixedUpdate += Tick;
        RoR2Application.onUpdate += Billboard;
        Log("MapRing3 installed");
    }

    // Called by newer versions: remove this version's podiums.
    public static void Cleanup()
    {
        foreach (var p in ring) if (p.go != null) { if (NetworkServer.active) NetworkServer.Destroy(p.go); else UnityEngine.Object.Destroy(p.go); }
        ring.Clear();
    }

    static float WaterY()
    {
        GameObject w = GameObject.Find("FishingWater");
        return w == null ? float.NaN : w.transform.position.y;
    }

    static List<CharacterBody> PlayerBodies()
    {
        var list = new List<CharacterBody>();
        foreach (var pcmc in PlayerCharacterMasterController.instances)
        {
            if (pcmc == null || pcmc.master == null) continue;
            CharacterBody b = pcmc.master.GetBody();
            if (b != null && b.healthComponent != null && b.healthComponent.alive) list.Add(b);
        }
        return list;
    }

    static bool RingStage(SceneDef sd)
    {
        if (sd == null || sd.sceneType != SceneType.Stage) return false;
        string n = sd.cachedName;
        return n != "moon" && n != "moon2" && n != "voidraid" && n != "meridian" && n != "bazaar" && !n.StartsWith("it");
    }

    static List<SceneDef> MapList(SceneDef current)
    {
        var list = new List<SceneDef>();
        var names = new HashSet<string>();
        foreach (SceneDef sd in SceneCatalog.allStageSceneDefs)
        {
            if (sd == null || sd.sceneType != SceneType.Stage) continue;
            if (sd.stageOrder < 1 || sd.stageOrder > 5) continue;           // excludes moon (6), Simulacrum (97), hidden realms (99)
            if (sd.filterOutOfBazaar) continue;                             // the game itself never offers these via seers
            if (sd.requiredExpansion != null && !RoR2.Run.instance.IsExpansionEnabled(sd.requiredExpansion)) continue;
            if (current != null && (sd == current || sd.baseSceneName == current.baseSceneName)) continue;
            string disp = Language.GetString(sd.nameToken);
            if (!names.Add(disp)) continue;                                 // golemplains2 / blackbeach2 are variants of the same map
            list.Add(sd);
        }
        list.Sort((a, b) => a.stageOrder != b.stageOrder ? a.stageOrder.CompareTo(b.stageOrder) : string.Compare(a.cachedName, b.cachedName, StringComparison.Ordinal));
        return list;
    }

    static void Tick()
    {
        if (!enabled || !NetworkServer.active || RoR2.Run.instance == null) return;
        try
        {
            ticks++;
            Stage st = Stage.instance;
            if (st != lastStage)
            {
                lastStage = st;
                ring.Clear();
                spawnedThisStage = choiceMade = travelled = false;
                chosen = null; chooser = null; groundedAt = -1f; chosenAt = -1f; countdownShown = 0;
                stageSeenAt = Time.time;
                if (st != null) Log("stage " + (st.sceneDef == null ? "?" : st.sceneDef.cachedName) + " seen");
            }
            if (st == null || !RingStage(st.sceneDef)) return;

            if (!spawnedThisStage)
            {
                if (Time.time - stageSeenAt < 1f) return;
                var bodies = PlayerBodies();
                bool grounded = false;
                foreach (var b in bodies) if (b.characterMotor == null || b.characterMotor.isGrounded) grounded = true;
                if (!grounded) return;
                if (groundedAt < 0f) { groundedAt = Time.time; return; }
                if (Time.time - groundedAt < 2.5f) return;                  // give everyone a moment to land
                spawnedThisStage = true;
                SpawnRing(st.sceneDef, bodies);
                return;
            }

            if (choiceMade)
            {
                float left = 5f - (Time.time - chosenAt);
                int sec = Mathf.CeilToInt(left);
                if (sec < countdownShown && sec >= 1 && sec <= 3) { countdownShown = sec; Say("<color=#c8a2ff>" + sec + "...</color>"); }
                if (left <= 0f && !travelled)
                {
                    travelled = true;
                    Cleanup();
                    Log("travelling to " + chosen.cachedName + " (chosen by " + chooser + ")");
                    RoR2.Run.instance.AdvanceStage(chosen);
                }
                return;
            }

            // Keep the ring intact: if something removed a podium before a choice was made, put it back.
            if (Time.time >= nextCheck && ring.Count > 0)
            {
                nextCheck = Time.time + 2f;
                foreach (var p in ring)
                    if (p.go == null && p.respawns < 3)
                    {
                        p.respawns++;
                        Log("podium for " + p.scene.cachedName + " disappeared - respawning (" + p.respawns + ")");
                        SpawnPodium(p);
                    }
            }
        }
        catch (Exception e) { if (ticks % 250 == 1) Log("tick error: " + e); }
    }

    static void SpawnRing(SceneDef current, List<CharacterBody> bodies)
    {
        if (prefab == null) prefab = Addressables.LoadAssetAsync<GameObject>(PrefabKey).WaitForCompletion();
        if (prefab == null) { Log("SeerStation prefab not found"); return; }
        if (bodies.Count == 0) { Log("no player bodies"); return; }

        Vector3 center = Vector3.zero;
        foreach (var b in bodies) center += b.footPosition;
        center /= bodies.Count;

        List<SceneDef> maps = MapList(current);
        int n = maps.Count;
        if (n == 0) { Log("no maps"); return; }
        float radius = Mathf.Clamp(n * 4.5f / (2f * Mathf.PI), 12f, 24f);
        float wy = WaterY();
        int mask = LayerIndex.world.mask;
        // Collect valid ground spots on rings around the group, then give each podium the free spot nearest
        // to its ideal place on the circle (min 3.5 m apart), so none float or sit in the lake.
        var cands = new List<Vector3>();
        for (float r = 7f; r <= radius + 22f; r += 2f)
        {
            int k = Mathf.Max(12, Mathf.RoundToInt(2f * Mathf.PI * r / 1.5f));
            for (int j = 0; j < k; j++)
            {
                float a = j * 2f * Mathf.PI / k;
                Vector3 g;
                if (TryGround(center + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r, center.y, wy, mask, out g)) cands.Add(g);
            }
        }
        float step = 360f / n;
        var names = new List<string>();
        var used = new List<Vector3>();
        int placed = 0, off = 0;
        for (int i = 0; i < n; i++)
        {
            float ang = i * step * Mathf.Deg2Rad;
            Vector3 ideal = center + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * radius;
            Vector3 spot = Vector3.zero; float best = float.MaxValue; bool ok = false;
            foreach (var c in cands)
            {
                bool clash = false;
                foreach (var u in used) if ((u - c).sqrMagnitude < 3.5f * 3.5f) { clash = true; break; }
                if (clash) continue;
                Vector3 d = c - ideal; d.y *= 2f;
                float dd = d.sqrMagnitude;
                if (dd < best) { best = dd; spot = c; ok = true; }
            }
            if (!ok) { off++; Log("no free ground for " + maps[i].cachedName + " - using ideal spot"); spot = ideal; }
            used.Add(spot);
            Vector3 look = center - spot; look.y = 0f;
            var p = new Podium { scene = maps[i], pos = spot, rot = look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look) : Quaternion.identity };
            ring.Add(p);
            SpawnPodium(p);
            if (p.go != null) placed++;
            names.Add(maps[i].cachedName + "='" + Language.GetString(maps[i].nameToken) + "'");
        }
        Log(cands.Count + " ground candidates, " + off + " podium(s) without ground");
        Log("ring on " + current.cachedName + ": " + placed + "/" + n + " podiums, radius " + radius.ToString("0.0") + " around " + center + " (water y " + wy + "): " + string.Join(", ", names.ToArray()));
        Say("<color=#c8a2ff>Pick your next map: walk up to a podium and press E.</color>");
    }

    static bool TryGround(Vector3 xz, float cy, float wy, int mask, out Vector3 spot)
    {
        spot = Vector3.zero;
        Vector3 origin = new Vector3(xz.x, cy + 10f, xz.z);
        RaycastHit hit;
        if (!Physics.Raycast(origin, Vector3.down, out hit, 35f, mask, QueryTriggerInteraction.Ignore)) return false;
        if (hit.normal.y < 0.75f) return false;                                       // too steep
        if (Mathf.Abs(hit.point.y - cy) > 12f) return false;                          // a ledge far above/below the group
        if (!float.IsNaN(wy) && hit.point.y < wy + 0.4f) return false;               // under/at the fishing lake surface
        if (Physics.CheckCapsule(hit.point + Vector3.up * 0.9f, hit.point + Vector3.up * 2.5f, 0.6f, mask, QueryTriggerInteraction.Ignore)) return false; // inside geometry
        spot = hit.point;
        return true;
    }

    static void SpawnPodium(Podium p)
    {
        GameObject go = UnityEngine.Object.Instantiate(prefab, p.pos, p.rot);
        go.name = Tag + "_" + p.scene.cachedName;
        var ssc = go.GetComponent<SeerStationController>();
        ssc.SetTargetScene(p.scene);
        var pi = go.GetComponent<PurchaseInteraction>();
        pi.costType = CostTypeIndex.None;
        pi.cost = 0;
        pi.Networkcost = 0;
        var dn = go.GetComponent<GenericDisplayNameProvider>();
        if (dn != null) dn.displayToken = Language.GetString(p.scene.nameToken);
        SceneDef target = p.scene;
        pi.onPurchase.AddListener(interactor => OnChosen(interactor, target));
        NetworkServer.Spawn(go);
        p.go = go;

        // Host-local floating label.
        try
        {
            var lg = new GameObject(Tag + "_Label");
            lg.transform.SetParent(go.transform, false);
            lg.transform.position = p.pos + Vector3.up * 7.6f;
            var tm = lg.AddComponent<TextMesh>();
            tm.text = Language.GetString(p.scene.nameToken);
            tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            lg.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
            tm.fontSize = 48; tm.characterSize = 0.06f;
            tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
            tm.color = new Color(0.85f, 0.75f, 1f);
            p.label = tm;
        }
        catch (Exception e) { Log("label error: " + e.Message); }

        Log("  podium " + go.name + " at " + p.pos + " target=" + SceneCatalog.GetSceneDef((SceneIndex)ssc.NetworktargetSceneDefIndex).cachedName + " cost=" + pi.cost + " costType=" + pi.costType + " netId=" + go.GetComponent<NetworkIdentity>().netId);
    }

    static void OnChosen(Interactor interactor, SceneDef target)
    {
        try
        {
            if (!enabled || choiceMade || travelled) return;
            choiceMade = true;
            chosen = target;
            chosenAt = Time.time;
            countdownShown = 4;
            CharacterBody b = interactor == null ? null : interactor.GetComponent<CharacterBody>();
            chooser = b != null ? b.GetUserName() : "Someone";
            string map = Language.GetString(target.nameToken);
            Say("<color=#c8a2ff>" + chooser + " chose " + map + "! Travelling in 5...</color>");
            Log(chooser + " chose " + target.cachedName);
            // Lock the other podiums so nobody can change it.
            foreach (var p in ring) if (p.go != null) { var pi = p.go.GetComponent<PurchaseInteraction>(); if (pi != null) pi.SetAvailable(false); }
        }
        catch (Exception e) { Log("choice error: " + e); }
    }

    static void Billboard()
    {
        if (ring.Count == 0) return;
        try
        {
            Camera cam = null;
            foreach (var rig in CameraRigController.readOnlyInstancesList) if (rig != null && rig.sceneCam != null) { cam = rig.sceneCam; break; }
            if (cam == null) return;
            foreach (var p in ring)
                if (p.label != null) p.label.transform.rotation = Quaternion.LookRotation(p.label.transform.position - cam.transform.position);
        }
        catch { }
    }
}
