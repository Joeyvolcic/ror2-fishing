// MerchantLook - the Fish Merchant's stall + NPC, shared by the host add-on (FishMerchant<N>) and the guest payload
// (MerchantClient in FISHING.exe). UnityEngine only; the game's Newt shopkeeper model is loaded by reflection (Addressables
// + the model's own ModelSkinController, run as a COROUTINE - never SkinDef.Apply(), which busy-waits on the main thread
// forever). If the model can't be loaded a primitive fisherman stands in.
//   Build(pos, yaw)  stall (counter, fish on ice, striped awning, sign), the sitting Newt behind the counter, a floating golden
//                    case, a glow light, a "Fish Merchant" name tag, and a teal outline that shows through walls
//   Tick()           call every frame (name tag faces the camera, case spins, glow pulses)
//   Celebrate(c)     short light flash in a rarity colour (someone unboxed something good)
// Local space: the merchant faces +Z (towards the lake / the customers), counter front at z = +1.55.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class MerchantLookRunner : MonoBehaviour { }

public static class MerchantLook
{
    public static Action<string> log;
    public static GameObject root;
    public static Vector3 position;
    public static float yaw;
    public static float modelYawOffset = 0f;      // turn the Newt if the model faces the wrong way
    public static float modelScale = 0.35f;
    public static string modelKey = "RoR2/Base/Shopkeeper/ShopkeeperBody.prefab";
    public static string modelState = "none";     // none / loading / newt / fallback
    static Transform label, caseTf;
    static Renderer newtXray;          // teal see-through silhouette, only from further than xrayFrom metres
    public static float xrayFrom = 14f;
    static Light glow;
    static Shader colShader;
    static float flashUntil, built;
    static Color flashCol;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static void L(string s) { if (log != null) try { log(s); } catch { } }

    static Material Col(Color c, int ztest, int cull, bool transparent, int queue)
    {
        if (colShader == null) colShader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
        var m = new Material(colShader);
        m.color = c;
        if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", ztest);
        if (m.HasProperty("_Cull")) m.SetInt("_Cull", cull);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", transparent ? 0 : 1);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", transparent ? 5 : 1);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", transparent ? 10 : 0);
        m.renderQueue = queue;
        return m;
    }
    static Material Lit(Color c, float gloss, float metal)
    {
        Shader s = Shader.Find("Standard");
        if (s == null) return Col(c, 4, 2, false, 2000);
        var m = new Material(s); m.color = c;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        return m;
    }

    static GameObject Prim(PrimitiveType t, string name, Transform parent, Vector3 lpos, Vector3 scale, Vector3 euler, Material m)
    {
        var g = GameObject.CreatePrimitive(t);
        g.name = name;
        foreach (var comp in g.GetComponents<Component>()) if (comp != null && comp.GetType().Name.EndsWith("Collider")) UnityEngine.Object.DestroyImmediate(comp);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = scale; g.transform.localRotation = Quaternion.Euler(euler);
        var r = g.GetComponent<Renderer>();
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return g;
    }

    public static void Destroy()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null; label = caseTf = null; glow = null; newtXray = null; modelState = "none";
    }

    public static void Build(Vector3 pos, float yawDeg)
    {
        Destroy();
        for (int i = 0; i < 10; i++) { var old = GameObject.Find("FishMerchantStall"); if (old == null) break; old.name = "FishMerchantStall_old"; UnityEngine.Object.Destroy(old); }
        position = pos; yaw = yawDeg; built = Time.time;
        root = new GameObject("FishMerchantStall");
        root.transform.position = pos; root.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
        Transform t = root.transform;

        Material wood = Lit(new Color(0.42f, 0.26f, 0.12f), 0.25f, 0f), woodLight = Lit(new Color(0.62f, 0.45f, 0.25f), 0.3f, 0f);
        Material teal = Lit(new Color(0.05f, 0.55f, 0.6f), 0.4f, 0f), white = Lit(new Color(0.95f, 0.95f, 0.92f), 0.4f, 0f);
        Material ice = Lit(new Color(0.75f, 0.9f, 1f), 0.9f, 0f), gold = Lit(new Color(1f, 0.78f, 0.25f), 0.9f, 1f);
        Material fishA = Lit(new Color(0.35f, 0.55f, 0.85f), 0.8f, 0.2f), fishB = Lit(new Color(1f, 0.5f, 0.1f), 0.8f, 0.2f), fishC = Lit(new Color(0.85f, 0.2f, 0.25f), 0.8f, 0.2f);

        // floor, counter, ice tray with fish
        Prim(PrimitiveType.Cylinder, "Floor", t, new Vector3(0f, 0.03f, 0.4f), new Vector3(4.6f, 0.03f, 4.6f), Vector3.zero, wood);
        Prim(PrimitiveType.Cube, "Counter", t, new Vector3(0f, 0.55f, 1.2f), new Vector3(3.2f, 1.1f, 0.7f), Vector3.zero, wood);
        Prim(PrimitiveType.Cube, "CounterTop", t, new Vector3(0f, 1.13f, 1.2f), new Vector3(3.4f, 0.08f, 0.9f), Vector3.zero, woodLight);
        Prim(PrimitiveType.Cube, "Ice", t, new Vector3(-0.6f, 1.2f, 1.25f), new Vector3(1.6f, 0.07f, 0.6f), Vector3.zero, ice);
        Material[] fm = { fishA, fishB, fishC, fishA, fishB };
        for (int i = 0; i < 5; i++)
        {
            var f = Prim(PrimitiveType.Capsule, "Fish", t, new Vector3(-1.2f + i * 0.3f, 1.27f, 1.25f + ((i & 1) == 0 ? 0.08f : -0.08f)), new Vector3(0.12f, 0.22f, 0.07f), new Vector3(0f, 10f * i, 90f), fm[i]);
            Prim(PrimitiveType.Cube, "Tail", f.transform, new Vector3(0f, -1.15f, 0f), new Vector3(1.4f, 0.5f, 0.3f), Vector3.zero, fm[i]);
        }
        // awning: 2 posts + striped canopy + sign
        foreach (float x in new[] { -1.65f, 1.65f })
        {
            Prim(PrimitiveType.Cylinder, "Post", t, new Vector3(x, 1.5f, 1.55f), new Vector3(0.12f, 1.5f, 0.12f), Vector3.zero, wood);
            Prim(PrimitiveType.Cylinder, "BackPost", t, new Vector3(x, 1.6f, -1.1f), new Vector3(0.12f, 1.6f, 0.12f), Vector3.zero, wood);
        }
        var canopy = new GameObject("Canopy").transform; canopy.SetParent(t, false);
        canopy.localPosition = new Vector3(0f, 3.12f, 0.25f); canopy.localRotation = Quaternion.Euler(-9f, 0f, 0f);
        for (int i = 0; i < 8; i++) Prim(PrimitiveType.Cube, "Stripe", canopy, new Vector3(-1.75f + i * 0.5f, 0f, 0f), new Vector3(0.5f, 0.06f, 3.0f), Vector3.zero, (i & 1) == 0 ? teal : white);
        for (int i = 0; i < 8; i++) Prim(PrimitiveType.Cube, "Flap", canopy, new Vector3(-1.75f + i * 0.5f, -0.15f, 1.5f), new Vector3(0.5f, 0.3f, 0.04f), Vector3.zero, (i & 1) == 0 ? teal : white);
        Prim(PrimitiveType.Cube, "Sign", t, new Vector3(0f, 3.62f, 1.45f), new Vector3(2.6f, 0.55f, 0.08f), Vector3.zero, woodLight);
        Text(t, "SignText", new Vector3(0f, 3.62f, 1.5f), "FISH MERCHANT", 0.045f, new Color(0.1f, 0.12f, 0.15f), false);
        // golden case on the counter
        var cs = new GameObject("Case").transform; cs.SetParent(t, false); cs.localPosition = new Vector3(0.95f, 1.55f, 1.2f);
        Prim(PrimitiveType.Cube, "CaseBody", cs, Vector3.zero, new Vector3(0.6f, 0.36f, 0.36f), Vector3.zero, Lit(new Color(0.2f, 0.25f, 0.55f), 0.8f, 0.5f));
        Prim(PrimitiveType.Cube, "CaseBand", cs, Vector3.zero, new Vector3(0.62f, 0.08f, 0.38f), Vector3.zero, gold);
        Prim(PrimitiveType.Cube, "CaseLock", cs, new Vector3(0f, 0f, 0.19f), new Vector3(0.1f, 0.14f, 0.04f), Vector3.zero, gold);
        caseTf = cs;

        // teal outline through walls (stall boxes)
        Material xray = Col(new Color(0.2f, 0.95f, 0.9f, 0.45f), 5, 2, true, 4000);
        var shell = new GameObject("OutlineXray").transform; shell.SetParent(t, false);
        Prim(PrimitiveType.Cube, "S", shell, new Vector3(0f, 0.6f, 1.2f), new Vector3(3.5f, 1.25f, 0.95f), Vector3.zero, xray);
        Prim(PrimitiveType.Cube, "S", shell, new Vector3(0f, 3.12f, 0.25f), new Vector3(4.1f, 0.12f, 3.1f), new Vector3(-9f, 0f, 0f), xray);
        Prim(PrimitiveType.Cube, "S", shell, new Vector3(0f, 3.62f, 1.45f), new Vector3(2.7f, 0.62f, 0.12f), Vector3.zero, xray);
        Material rim = Col(new Color(0.2f, 0.95f, 0.9f, 1f), 4, 1, false, 2001);
        var rimT = new GameObject("OutlineRim").transform; rimT.SetParent(t, false);
        Prim(PrimitiveType.Cube, "R", rimT, new Vector3(0f, 0.6f, 1.2f), new Vector3(3.5f, 1.25f, 0.95f), Vector3.zero, rim);

        // light + name tag
        var lg = new GameObject("Glow"); lg.transform.SetParent(t, false); lg.transform.localPosition = new Vector3(0f, 2.6f, 1.6f);
        glow = lg.AddComponent<Light>(); glow.type = LightType.Point; glow.color = new Color(0.4f, 1f, 0.95f); glow.range = 10f; glow.intensity = 2.2f;
        var lt = new GameObject("NameTag").transform; lt.SetParent(t, false); lt.localPosition = new Vector3(0f, 4.35f, 0.9f);
        Text(lt, "Name", Vector3.zero, "Fish Merchant", 0.07f, new Color(1f, 0.85f, 0.3f), true);
        Text(lt, "Sub", new Vector3(0f, -0.38f, 0f), "sell fish  -  unbox cases", 0.035f, new Color(0.6f, 1f, 0.95f), true);
        label = lt;

        // the Newt (or a primitive fisherman)
        modelState = "loading";
        try { SpawnNewt(t); } catch (Exception e) { L("newt model error: " + e.Message); Fallback(t); }
        L("merchant built at " + pos + " yaw " + yawDeg.ToString("0") + " model " + modelState);
    }

    static void Text(Transform parent, string name, Vector3 lpos, string s, float size, Color c, bool overlay)
    {
        try
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = lpos;
            var tm = go.AddComponent<TextMesh>();
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            if (f == null) try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f != null)
            {
                tm.font = f;
                var mat = new Material(f.material);
                if (overlay) mat.renderQueue = 4001;
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
            tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
            tm.characterSize = size; tm.fontSize = 64; tm.text = s; tm.color = c;
            if (!overlay) go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // sign text faces +Z (TextMesh reads along -Z)
        }
        catch (Exception e) { L("text error: " + e.Message); }
    }

    static Type FindType(string full)
    {
        foreach (var a in FishingScope.Assemblies()) { Type t = null; try { t = a.GetType(full); } catch { } if (t != null) return t; }
        return null;
    }
    static object Member(object o, string n)
    {
        if (o == null) return null;
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(n, All); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            var f = t.GetField(n, All); if (f != null) return f.GetValue(o);
        }
        return null;
    }

    static GameObject LoadPrefab(string key)
    {
        Type ad = FindType("UnityEngine.AddressableAssets.Addressables");
        if (ad == null) return null;
        MethodInfo load = null;
        foreach (var m in ad.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == "LoadAssetAsync" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object)) load = m;
        if (load == null) return null;
        object h = load.MakeGenericMethod(typeof(GameObject)).Invoke(null, new object[] { key });
        MethodInfo wait = h.GetType().GetMethod("WaitForCompletion");
        return wait.Invoke(h, null) as GameObject;
    }

    static void SpawnNewt(Transform t)
    {
        GameObject prefab = LoadPrefab(modelKey);
        if (prefab == null) { L("model prefab not found: " + modelKey); Fallback(t); return; }
        Component ml = prefab.GetComponent("ModelLocator");
        Transform mt = Member(ml, "modelTransform") as Transform;
        if (mt == null) { L("no modelTransform on " + prefab.name); Fallback(t); return; }
        var holder = new GameObject("MerchantModelHolder"); holder.SetActive(false);
        holder.transform.SetParent(t, false);
        var go = UnityEngine.Object.Instantiate(mt.gameObject, holder.transform);
        go.name = "MerchantNewt";
        string[] keep = { "Transform", "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter", "Animator", "CharacterModel", "ModelSkinController", "ChildLocator", "ParticleSystem", "ParticleSystemRenderer" };
        for (int pass = 0; pass < 3; pass++)
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (Array.IndexOf(keep, n) >= 0) continue;
                if (pass == 0 && n.EndsWith("Collider")) continue;   // HurtBoxes first, then their colliders
                try { UnityEngine.Object.DestroyImmediate(c); } catch { }
            }
        go.transform.localPosition = new Vector3(0f, 0.05f, -0.55f);
        go.transform.localRotation = Quaternion.Euler(0f, modelYawOffset, 0f);
        go.transform.localScale = Vector3.one * modelScale;
        holder.SetActive(true);
        // skin (materials + mesh) through the model's own SkinDef, as a coroutine
        var runner = root.AddComponent<MerchantLookRunner>();
        Component msc = go.GetComponent("ModelSkinController");
        Array skins = Member(msc, "skins") as Array;
        object skin = skins != null && skins.Length > 0 ? skins.GetValue(0) : null;
        MethodInfo apply = null;
        if (skin != null) foreach (var m in skin.GetType().GetMethods(All)) if (m.Name == "ApplyAsync" && m.GetParameters().Length == 4) apply = m;
        if (apply == null) { L("no SkinDef.ApplyAsync - fallback figure"); UnityEngine.Object.Destroy(holder); Fallback(t); return; }
        var ps = apply.GetParameters();
        var en = (IEnumerator)apply.Invoke(skin, new object[] { go, Activator.CreateInstance(ps[1].ParameterType), Activator.CreateInstance(ps[2].ParameterType), Enum.ToObject(ps[3].ParameterType, 3) });
        runner.StartCoroutine(SkinRoutine(en, go, holder));
        modelState = "loading";
    }

    static IEnumerator SkinRoutine(IEnumerator e, GameObject go, GameObject holder)
    {
        float t0 = Time.realtimeSinceStartup;
        while (true)
        {
            bool more;
            try { more = e.MoveNext(); } catch (Exception ex) { L("skin apply error: " + ex.Message); more = false; }
            if (!more) break;
            if (Time.realtimeSinceStartup - t0 > 20f) { L("skin apply timeout"); break; }
            yield return e.Current;
        }
        yield return null;
        if (go == null) yield break;
        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        bool ok = smr != null && smr.sharedMesh != null && smr.sharedMaterial != null;
        if (!ok)
        {
            L("newt skin incomplete (mesh " + (smr != null && smr.sharedMesh != null) + ", mat " + (smr != null && smr.sharedMaterial != null) + ") - fallback figure");
            if (holder != null) UnityEngine.Object.Destroy(holder);
            if (root != null) Fallback(root.transform);
            yield break;
        }
        // x-ray twin of the skinned mesh: same bones, teal see-through-walls material
        try
        {
            var twin = new GameObject("NewtXray"); twin.transform.SetParent(smr.transform.parent, false);
            twin.transform.localPosition = smr.transform.localPosition; twin.transform.localRotation = smr.transform.localRotation; twin.transform.localScale = smr.transform.localScale;
            var x = twin.AddComponent<SkinnedMeshRenderer>();
            x.sharedMesh = smr.sharedMesh; x.bones = smr.bones; x.rootBone = smr.rootBone;
            var xm = Col(new Color(0.2f, 0.95f, 0.9f, 0.5f), 5, 2, true, 4000);
            var arr = new Material[Math.Max(1, smr.sharedMesh.subMeshCount)];
            for (int i = 0; i < arr.Length; i++) arr[i] = xm;
            x.sharedMaterials = arr;
            x.updateWhenOffscreen = true; smr.updateWhenOffscreen = true;
            x.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            x.enabled = false; newtXray = x;
        }
        catch (Exception ex) { L("newt outline error: " + ex.Message); }
        modelState = "newt";
        L("newt skinned: mesh " + smr.sharedMesh.name + ", material " + smr.sharedMaterial.name + " (" + (Time.realtimeSinceStartup - t0).ToString("0.00") + " s)");
    }

    static void Fallback(Transform t)
    {
        try
        {
            var f = new GameObject("MerchantFigure").transform; f.SetParent(t, false); f.localPosition = new Vector3(0f, 0f, -0.1f);
            Material skin = Lit(new Color(0.95f, 0.75f, 0.6f), 0.3f, 0f), apron = Lit(new Color(0.1f, 0.35f, 0.55f), 0.3f, 0f), hat = Lit(new Color(0.85f, 0.75f, 0.3f), 0.2f, 0f), shirt = Lit(new Color(0.9f, 0.9f, 0.85f), 0.3f, 0f);
            Prim(PrimitiveType.Capsule, "Body", f, new Vector3(0f, 1.15f, 0f), new Vector3(0.75f, 0.75f, 0.55f), Vector3.zero, shirt);
            Prim(PrimitiveType.Cube, "Apron", f, new Vector3(0f, 1.05f, 0.25f), new Vector3(0.6f, 0.9f, 0.06f), Vector3.zero, apron);
            Prim(PrimitiveType.Sphere, "Head", f, new Vector3(0f, 2.1f, 0f), Vector3.one * 0.48f, Vector3.zero, skin);
            Prim(PrimitiveType.Cylinder, "HatBrim", f, new Vector3(0f, 2.3f, 0f), new Vector3(0.75f, 0.02f, 0.75f), Vector3.zero, hat);
            Prim(PrimitiveType.Cylinder, "HatTop", f, new Vector3(0f, 2.42f, 0f), new Vector3(0.42f, 0.12f, 0.42f), Vector3.zero, hat);
            Prim(PrimitiveType.Capsule, "ArmL", f, new Vector3(-0.45f, 1.35f, 0.25f), new Vector3(0.18f, 0.4f, 0.18f), new Vector3(60f, 0f, 0f), shirt);
            Prim(PrimitiveType.Capsule, "ArmR", f, new Vector3(0.45f, 1.35f, 0.25f), new Vector3(0.18f, 0.4f, 0.18f), new Vector3(60f, 0f, 0f), shirt);
            Material xray = Col(new Color(0.2f, 0.95f, 0.9f, 0.5f), 5, 2, true, 4000);
            Prim(PrimitiveType.Capsule, "X", f, new Vector3(0f, 1.4f, 0f), new Vector3(0.8f, 1.1f, 0.6f), Vector3.zero, xray);
            modelState = "fallback";
        }
        catch (Exception e) { L("fallback error: " + e.Message); }
    }

    public static void Celebrate(Color c) { flashCol = c; flashUntil = Time.time + 2.5f; }

    public static void Tick()
    {
        if (root == null) return;
        float now = Time.time;
        if (glow != null)
        {
            if (now < flashUntil) { glow.color = flashCol; glow.intensity = 4f + 3f * Mathf.Abs(Mathf.Sin(now * 10f)); glow.range = 16f; }
            else { glow.color = new Color(0.4f, 1f, 0.95f); glow.intensity = 2f + 0.6f * Mathf.Sin(now * 2.5f); glow.range = 10f; }
        }
        if (caseTf != null) { caseTf.localRotation = Quaternion.Euler(0f, now * 50f, 0f); caseTf.localPosition = new Vector3(0.95f, 1.55f + 0.06f * Mathf.Sin(now * 2f), 1.2f); }
        var cam = Camera.main;
        if (newtXray != null && cam != null) newtXray.enabled = Vector3.Distance(cam.transform.position, position) > xrayFrom;
        if (label != null && cam != null) label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);
    }
}
