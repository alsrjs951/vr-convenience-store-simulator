using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// The store's light (2026-11-10), baked so that it costs nothing while playing: the ceiling strips and the chilled
/// cases' own strips light the room as area lights, the light bounces, and the shadow under a shelf or beside a wall is
/// worked out instead of painted into the floor. Things that move (customers, hands, boxes) take their light from light
/// probes, and one box-projected reflection probe gives the polished floor, the steel and the glass a room to reflect.
/// The Gaussian splat products are unlit and do not change.
///
/// P2StoreBuilder.Build bakes at the end (about two minutes on the CPU lightmapper, so a training run on the GPU is not
/// disturbed). -no-bake skips it and leaves the old even ambient light, for quick rebuilds that are not about looks.
/// -executeMethod P3StoreLighting.Rebake bakes the saved store scene again without rebuilding it.
/// </summary>
public static class P3StoreLighting
{
    const string StoreScene = "Assets/Scenes/StoreScene.unity";
    const string SettingsPath = "Assets/Settings/StoreLighting.lighting";
    const string PipelinePath = "Assets/Settings/PC_RPAsset.asset";
    const string CeilingMaterial = "Assets/Materials/P3/P3_Ceiling.mat";
    const string RootName = "Store Lighting";
    const float CeilingHeight = 3f;

    // Tuned by eye on the still views (P3StoreDressing.RenderViews): a white fixture under a strip comes out just short of white.
    const float StripIntensity = 20f, CaseIntensity = 14f, DaylightIntensity = 6f;
    /// <summary>A little light of the ceiling's own, for what the strips' housings throw sideways onto it.</summary>
    const float CeilingFill = 0.3f;
    static readonly Color StripColor = new Color(1f, 0.99f, 0.97f), DaylightColor = new Color(0.93f, 0.97f, 1f);

    public static bool SkipRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "-no-bake") >= 0;

    /// <summary>Sets the saved store scene up for baked light and bakes it. The scene must already be saved at its path.</summary>
    public static void Bake(Scene scene, Transform environment)
    {
        var old = environment.Find(RootName);
        if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
        var sun = (GameObject.Find("Directional Light") ?? throw new Exception("Directional Light missing.")).GetComponent<Light>();
        var ceiling = AssetDatabase.LoadAssetAtPath<Material>(CeilingMaterial) ?? throw new Exception("Ceiling material missing.");

        if (SkipRequested)
        {
            // The look before 2026-11-10: an even ambient light and a weak sun, nothing baked.
            sun.enabled = true;
            sun.color = new Color(1f, 0.99f, 0.97f);
            sun.intensity = 0.42f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.97f, 0.97f, 0.97f);
            RenderSettings.ambientEquatorColor = new Color(0.86f, 0.86f, 0.86f);
            RenderSettings.ambientGroundColor = new Color(0.56f, 0.56f, 0.55f);
            ceiling.SetColor("_EmissionColor", new Color(0.5f, 0.5f, 0.49f));
            EditorUtility.SetDirty(ceiling);
            Lightmapping.Clear();
            if (!EditorSceneManager.SaveScene(scene)) throw new Exception("Scene save failed.");
            Debug.Log("[P3] Store lighting: -no-bake, even ambient light.");
            return;
        }

        var started = DateTime.Now;
        sun.enabled = false;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.05f, 0.055f, 0.06f);
        ceiling.SetColor("_EmissionColor", Color.white * CeilingFill);
        EditorUtility.SetDirty(ceiling);
        BoxProjection();

        var root = new GameObject(RootName).transform;
        root.SetParent(environment, false);
        var counts = MarkStatic(environment);
        int lights = Lights(root);
        int probes = Probes(root);
        Reflection(root);

        Lightmapping.lightingSettings = Settings();
        Lightmapping.Clear();
        if (!Lightmapping.Bake()) throw new Exception("Light bake failed.");
        int mended = MendProbes();
        if (!EditorSceneManager.SaveScene(scene)) throw new Exception("Scene save failed.");
        AssetDatabase.SaveAssets();
        Debug.Log($"[P3] Store lighting baked in {(DateTime.Now - started).TotalSeconds:0} s: {counts.lightmapped} lightmapped, {counts.reflectedOnly} in reflections only, " +
                  $"{counts.noLightmapUv} without lightmap UVs, {lights} area lights, {probes} light probes ({mended} mended), {LightmapSettings.lightmaps.Length} lightmaps");
    }

    /// <summary>-executeMethod P3StoreLighting.Rebake: bakes the saved store scene again, for tuning the light without a rebuild.</summary>
    public static void Rebake()
    {
        var scene = EditorSceneManager.OpenScene(StoreScene);
        var environment = (GameObject.Find("Environment") ?? throw new Exception("Environment missing.")).transform;
        Bake(scene, environment);
    }

    // ---------- What is lit how ----------

    /// <summary>
    /// Everything of the room that stays where it is takes part in the bake and gets a lightmap. A printed face a few
    /// millimetres off a wall or the floor gets one too but casts no shadow, or it would darken the surface it lies on.
    /// Glass does not take part and only shows in reflections.
    /// </summary>
    static (int lightmapped, int reflectedOnly, int noLightmapUv) MarkStatic(Transform environment)
    {
        int lightmapped = 0, reflectedOnly = 0, noLightmapUv = 0;
        foreach (var renderer in environment.GetComponentsInChildren<MeshRenderer>(false))
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var material = renderer.sharedMaterial;
            if (!renderer.enabled || filter == null || filter.sharedMesh == null || material == null) continue;
            if (renderer.GetComponentInParent<Rigidbody>() != null || Moves(renderer.transform)) continue;

            var go = renderer.gameObject;
            var flags = GameObjectUtility.GetStaticEditorFlags(go) | StaticEditorFlags.ReflectionProbeStatic;
            bool transparent = material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f;
            bool print = filter.sharedMesh.name == "Quad" && go.name != "Floor Painted" && go.name != "Ceiling";
            if (transparent)
            {
                GameObjectUtility.SetStaticEditorFlags(go, flags & ~StaticEditorFlags.ContributeGI);
                reflectedOnly++;
                continue;
            }

            GameObjectUtility.SetStaticEditorFlags(go, flags | StaticEditorFlags.ContributeGI);
            // There is no realtime light in the store, so this is only for the bake.
            renderer.shadowCastingMode = print ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveGI = ReceiveGI.Lightmaps;
            lightmapped++;
            // Without a second UV set the bake uses the first: right for Unity's quad, wrong for a mesh whose faces share
            // texture space, so the count is logged.
            if (filter.sharedMesh.name != "Quad" && (filter.sharedMesh.uv2 == null || filter.sharedMesh.uv2.Length == 0)) noLightmapUv++;
        }
        return (lightmapped, reflectedOnly, noLightmapUv);
    }

    /// <summary>Under something the game turns or slides while playing.</summary>
    static bool Moves(Transform t)
    {
        for (; t != null; t = t.parent)
            if (t.name == "Microwave Door Hinge" || t.name == "Cash Drawer" || t.name.StartsWith("Title Door")) return true;
        return false;
    }

    // ---------- Lights ----------

    static int Lights(Transform root)
    {
        int count = 0;
        // The ceiling strips, each just under its lit face (P3StoreDressing.LightStrip).
        const float y = CeilingHeight - 0.035f;
        foreach (var x in P3StoreDressing.LightRows)
            foreach (var (z0, z1) in P3StoreDressing.LightSpans)
            {
                Area("Strip Light", root, new Vector3(x, y, (z0 + z1) * 0.5f), new Vector2(0.09f, z1 - z0), StripIntensity, StripColor);
                count++;
            }
        Area("Strip Light Back Of House", root, new Vector3(1f, y, -2.05f), new Vector2(2.4f, 0.09f), StripIntensity, StripColor);

        // The chilled cases: the strip under the canopy and the one under each deck (P3StoreDressing.ChilledCases).
        float xc = (P3StoreDressing.ChilledFar + P3StoreDressing.ChilledNear) * 0.5f, length = P3StoreDressing.ChilledNear - P3StoreDressing.ChilledFar - 0.1f;
        float back = P2StoreBuilder.RoomFront - 0.06f;
        Area("Case Canopy Light", root, new Vector3(xc, 2.235f, P3StoreDressing.ChilledFront + 0.2f), new Vector2(length, 0.05f), CaseIntensity, StripColor);
        foreach (var (deckY, reach) in P3StoreDressing.ChilledDecks)
        {
            if (deckY <= 0.8f) continue;
            float zDeck = back - 0.04f - reach * 0.5f;
            Area("Case Deck Light", root, new Vector3(xc, deckY - 0.03f, zDeck - reach * 0.15f), new Vector2(length, 0.03f), CaseIntensity, StripColor);
            count++;
        }

        // Daylight on the pavement outside the glass, some of which comes in by the door.
        float frontLength = P2StoreBuilder.PartitionX - P2StoreBuilder.RoomFar, frontCenter = (P2StoreBuilder.PartitionX + P2StoreBuilder.RoomFar) * 0.5f;
        Area("Daylight", root, new Vector3(frontCenter, CeilingHeight - 0.06f, P2StoreBuilder.StorefrontZ - P2StoreBuilder.VestibuleDepth * 0.5f),
            new Vector2(frontLength - 0.4f, P2StoreBuilder.VestibuleDepth - 0.3f), DaylightIntensity, DaylightColor);
        return count + 3;
    }

    /// <summary>A rectangle light shining straight down; its size is along x and z. Area lights are baked only.</summary>
    static void Area(string name, Transform parent, Vector3 position, Vector2 size, float intensity, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, 0f, 0f));
        var light = go.AddComponent<Light>();
        light.type = LightType.Rectangle;
        light.areaSize = size;
        light.intensity = intensity;
        light.color = color;
        light.range = 14f;
        light.lightmapBakeType = LightmapBakeType.Baked;
        light.shadows = LightShadows.Soft;
    }

    /// <summary>
    /// A grid of probes from just under the floor to just over the ceiling and from wall to wall, the pavement included:
    /// anything that moves and lies outside the grid, a box set down on the floor for one, would get no light at all.
    /// Probes that end up inside a fixture or outside the room are mended after the bake.
    /// </summary>
    static int Probes(Transform root)
    {
        var positions = new List<Vector3>();
        float x0 = P2StoreBuilder.RoomFar + 0.1f, x1 = P2StoreBuilder.RoomNear - 0.1f;
        float z0 = P2StoreBuilder.StorefrontZ - P2StoreBuilder.VestibuleDepth + 0.1f, z1 = P2StoreBuilder.RoomFront - 0.1f;
        int nx = Mathf.CeilToInt((x1 - x0) / 1.05f), nz = Mathf.CeilToInt((z1 - z0) / 1.05f);
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
                foreach (float y in new[] { -0.03f, 0.6f, 1.3f, 2.1f, CeilingHeight + 0.03f })
                    positions.Add(new Vector3(Mathf.Lerp(x0, x1, (float)i / nx), y, Mathf.Lerp(z0, z1, (float)j / nz)));
        var group = new GameObject("Light Probes").AddComponent<LightProbeGroup>();
        group.transform.SetParent(root, false);
        group.probePositions = positions.ToArray();
        return positions.Count;
    }

    /// <summary>
    /// A probe shut inside a fixture or beyond a wall bakes dark and would darken whatever comes near it, and the layers
    /// under the floor and over the ceiling are only there to close the grid. Each of these takes the light of the nearest
    /// probe that saw the room.
    /// </summary>
    static int MendProbes()
    {
        var probes = LightmapSettings.lightProbes ?? throw new Exception("No light probes were baked.");
        var baked = probes.bakedProbes;
        var positions = probes.positions;
        var directions = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
        var seen = new Color[directions.Length];
        float Level(SphericalHarmonicsL2 sh)
        {
            sh.Evaluate(directions, seen);
            return seen.Sum(c => Mathf.Max(0f, c.r + c.g + c.b)) / (3f * seen.Length);
        }
        bool InRoom(Vector3 p) => p.y > 0f && p.y < CeilingHeight;
        var levels = baked.Select(Level).ToArray();
        var inRoom = Enumerable.Range(0, baked.Length).Where(i => InRoom(positions[i])).ToArray();
        float dark = inRoom.Select(i => levels[i]).OrderBy(v => v).ElementAt(inRoom.Length / 2) * 0.25f;
        var lit = inRoom.Where(i => levels[i] >= dark).ToArray();
        int mended = 0;
        for (int i = 0; i < baked.Length; i++)
        {
            if (InRoom(positions[i]) && levels[i] >= dark) continue;
            int nearest = lit.OrderBy(k => (positions[k] - positions[i]).sqrMagnitude).First();
            baked[i] = baked[nearest];
            mended++;
        }
        probes.bakedProbes = baked;
        EditorUtility.SetDirty(probes);
        return mended;
    }

    /// <summary>One probe in the aisle between the counter and the rows, its box the room, so reflections sit where the walls are.</summary>
    static void Reflection(Transform root)
    {
        var go = new GameObject("Reflection Probe");
        go.transform.SetParent(root, false);
        var at = new Vector3(-0.75f, 1.6f, 1.95f);
        go.transform.position = at;
        var probe = go.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Baked;
        probe.resolution = 256;
        probe.hdr = true;
        probe.boxProjection = true;
        var min = new Vector3(P2StoreBuilder.RoomFar, 0f, P2StoreBuilder.RoomBack);
        var max = new Vector3(P2StoreBuilder.RoomNear, CeilingHeight, P2StoreBuilder.RoomFront);
        probe.center = (min + max) * 0.5f - at;
        probe.size = max - min;
        probe.nearClipPlane = 0.1f;
        probe.farClipPlane = 30f;
    }

    static void BoxProjection()
    {
        var pipeline = AssetDatabase.LoadMainAssetAtPath(PipelinePath) ?? throw new Exception("URP asset missing.");
        var so = new SerializedObject(pipeline);
        var property = so.FindProperty("m_ReflectionProbeBoxProjection") ?? throw new Exception("URP box projection setting missing.");
        if (property.boolValue) return;
        property.boolValue = true;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);
    }

    static LightingSettings Settings()
    {
        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
        if (settings == null)
        {
            settings = new LightingSettings { name = "StoreLighting" };
            AssetDatabase.CreateAsset(settings, SettingsPath);
        }
        settings.autoGenerate = false;
        settings.bakedGI = true;
        settings.realtimeGI = false;
        // The CPU lightmapper: the GPU is usually busy training splats.
        settings.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
        settings.directionalityMode = LightmapsMode.NonDirectional;
        settings.lightmapResolution = 32f;
        settings.lightmapMaxSize = 1024;
        settings.lightmapPadding = 3;
        settings.lightmapCompression = LightmapCompression.HighQuality;
        settings.directSampleCount = 32;
        settings.indirectSampleCount = 256;
        settings.environmentSampleCount = 64;
        settings.maxBounces = 3;
        settings.ao = true;
        settings.aoMaxDistance = 0.5f;
        settings.aoExponentIndirect = 1.3f;
        settings.aoExponentDirect = 0.4f;
        settings.filteringMode = LightingSettings.FilterMode.Advanced;
        settings.denoiserTypeDirect = LightingSettings.DenoiserType.OpenImage;
        settings.denoiserTypeIndirect = LightingSettings.DenoiserType.OpenImage;
        settings.denoiserTypeAO = LightingSettings.DenoiserType.OpenImage;
        settings.filterTypeDirect = LightingSettings.FilterType.None;
        settings.filterTypeIndirect = LightingSettings.FilterType.None;
        settings.filterTypeAO = LightingSettings.FilterType.None;
        EditorUtility.SetDirty(settings);
        return settings;
    }
}
