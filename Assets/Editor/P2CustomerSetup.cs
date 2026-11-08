using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Prepares the Rocketbox customer characters (MIT, Assets/ThirdParty/Rocketbox, see README there): texture and model
/// import settings, URP materials, one animator controller per sex and one prefab per avatar. Idempotent and only
/// reimports what changed, so P2StoreBuilder.Build runs it every time.
/// </summary>
public static class P2CustomerSetup
{
    public const string Root = "Assets/ThirdParty/Rocketbox";
    public const int CustomerLayer = 15;
    const string MaterialFolder = "Assets/Materials/Customers";
    const string ControllerFolder = "Assets/Data/Customers";
    const string PrefabFolder = "Assets/Prefabs/Customers";
    const int TextureSize = 1024;
    // Rocketbox men are about 1.84 m and women 1.77 m tall; closer to Korean adults this way.
    const float ModelScale = 0.95f;
    const float AgentRadius = 0.25f;
    const float AgentSpeed = 1.1f;
    // The female wave runs 5.5 s; three seconds of it reads as a goodbye without holding the line up.
    const float MaxLeaveSeconds = 3f;

    static readonly (string name, string prefix, bool female)[] Avatars =
    {
        ("Male_Adult_08", "m014", false),
        ("Male_Adult_16", "m019", false),
        ("Female_Adult_01", "f001", true),
        ("Female_Adult_17", "f006", true),
    };

    // State name -> clip file stem without the m_/f_ prefix. Doc D4: idle, walk, leave; browse is the idle looking around.
    static readonly (string state, string stem, bool loop, bool moves)[] Clips =
    {
        (CustomerCharacter.IdleState, "idle_neutral_01", true, false),
        (CustomerCharacter.BrowseState, "idle_look_around_01", true, false),
        (CustomerCharacter.WalkState, "walk_neutral_01", true, true),
        (CustomerCharacter.LeaveState, "wave_01", false, false),
    };

    [MenuItem("Tools/P2/Setup Customer Characters")]
    public static void SetupMenu() => Setup();

    public static CustomerCharacter[] Setup()
    {
        EnsureLayer();
        Directory.CreateDirectory(MaterialFolder);
        Directory.CreateDirectory(ControllerFolder);
        Directory.CreateDirectory(PrefabFolder);
        AssetDatabase.Refresh();

        foreach (var path in AssetDatabase.FindAssets("t:Texture2D", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath))
            ConfigureTexture(path);

        var avatars = new Dictionary<string, Avatar>();
        foreach (var avatar in Avatars)
        {
            var path = ModelPath(avatar.name);
            ConfigureAvatarModel(path, avatar.prefix, avatar.name);
            avatars[avatar.name] = LoadAvatar(path);
        }

        var maleSource = avatars["Male_Adult_08"];
        var femaleSource = avatars["Female_Adult_01"];
        var controllers = new Dictionary<bool, AnimatorController>();
        var walkClips = new Dictionary<bool, AnimationClip>();
        var leaveClips = new Dictionary<bool, AnimationClip>();
        foreach (bool female in new[] { false, true })
        {
            var states = new List<(string state, AnimationClip clip)>();
            foreach (var clip in Clips)
            {
                var path = $"{Root}/Animations/{(female ? "f" : "m")}_{clip.stem}.fbx";
                var loaded = ConfigureClip(path, female ? femaleSource : maleSource, clip.loop, clip.moves);
                states.Add((clip.state, loaded));
                if (clip.state == CustomerCharacter.WalkState) walkClips[female] = loaded;
                if (clip.state == CustomerCharacter.LeaveState) leaveClips[female] = loaded;
            }
            controllers[female] = BuildController($"{ControllerFolder}/Customer_{(female ? "Female" : "Male")}.controller", states);
        }

        var prefabs = new List<CustomerCharacter>();
        foreach (var avatar in Avatars)
            prefabs.Add(BuildPrefab(avatar.name, controllers[avatar.female], avatars[avatar.name], leaveClips[avatar.female]));
        AssetDatabase.SaveAssets();
        Debug.Log($"[Customers] setup done: {prefabs.Count} prefabs; walk clip speed male={walkClips[false].averageSpeed.magnitude:0.000} female={walkClips[true].averageSpeed.magnitude:0.000} (normalized)");
        return prefabs.ToArray();
    }

    static string ModelPath(string name) => $"{Root}/Avatars/{name}/{name}.fbx";

    /// <summary>Doc 5.1: customers touch only the environment. Nothing else uses layer 15.</summary>
    static void EnsureLayer()
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var name = tagManager.FindProperty("layers").GetArrayElementAtIndex(CustomerLayer);
        if (name.stringValue != "Customer")
        {
            if (!string.IsNullOrEmpty(name.stringValue)) throw new Exception($"Layer {CustomerLayer} is already used by {name.stringValue}.");
            name.stringValue = "Customer";
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }
        for (int other = 0; other < 32; other++)
            Physics.IgnoreLayerCollision(CustomerLayer, other, other != 0);
        EditorUtility.SetDirty(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset")[0]);
    }

    static void ConfigureTexture(string path)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        bool normal = path.Contains("_normal");
        bool opacity = path.Contains("_opacity");
        var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        bool changed = importer.textureType != type || importer.maxTextureSize != TextureSize || importer.sRGBTexture == normal
                       || importer.alphaIsTransparency != opacity || !importer.mipmapEnabled;
        if (!changed) return;
        importer.textureType = type;
        importer.sRGBTexture = !normal;
        importer.alphaSource = opacity ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
        importer.alphaIsTransparency = opacity;
        importer.maxTextureSize = TextureSize;
        importer.mipmapEnabled = true;
        importer.SaveAndReimport();
    }

    /// <summary>Humanoid from its own skeleton (Biped names map automatically), no clips, Rocketbox's materials swapped for URP ones.</summary>
    static void ConfigureAvatarModel(string path, string prefix, string avatarName)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path) ?? throw new Exception("Missing model " + path);
        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Human) { importer.animationType = ModelImporterAnimationType.Human; changed = true; }
        if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel) { importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; changed = true; }
        if (importer.importAnimation) { importer.importAnimation = false; changed = true; }
        if (importer.importCameras || importer.importLights) { importer.importCameras = false; importer.importLights = false; changed = true; }
        if (importer.importBlendShapes) { importer.importBlendShapes = false; changed = true; }
        if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription) { importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription; changed = true; }

        var textures = $"{Root}/Avatars/{avatarName}/Textures/{prefix}";
        var wanted = new Dictionary<string, Material>
        {
            [$"{prefix}_body"] = LitMaterial($"{MaterialFolder}/{avatarName}_body.mat", Texture($"{textures}_body_color.png"), Texture($"{textures}_body_normal.png"), false),
            [$"{prefix}_head"] = LitMaterial($"{MaterialFolder}/{avatarName}_head.mat", Texture($"{textures}_head_color.png"), Texture($"{textures}_head_normal.png"), false),
        };
        var hair = AssetDatabase.LoadAssetAtPath<Texture2D>($"{textures}_opacity_color.png");
        if (hair != null) wanted[$"{prefix}_opacity"] = LitMaterial($"{MaterialFolder}/{avatarName}_opacity.mat", hair, null, true);

        var map = importer.GetExternalObjectMap();
        foreach (var pair in wanted)
        {
            var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), pair.Key);
            if (map.TryGetValue(id, out var current) && current == pair.Value) continue;
            importer.AddRemap(id, pair.Value);
            changed = true;
        }
        if (changed) importer.SaveAndReimport();
    }

    static Avatar LoadAvatar(string path)
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman) throw new Exception($"No valid humanoid avatar in {path} (valid={avatar?.isValid} human={avatar?.isHuman}).");
        return avatar;
    }

    /// <summary>
    /// Humanoid, using the avatar of a model with the same Biped skeleton. Rotation and height are baked into the pose so
    /// the agent decides facing and the feet stay on the floor; the walk keeps its forward motion as root motion, which the
    /// Animator discards (applyRootMotion off), so it walks in place under the NavMeshAgent.
    /// </summary>
    static AnimationClip ConfigureClip(string path, Avatar source, bool loop, bool moves)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path) ?? throw new Exception("Missing clip " + path);
        bool changed = importer.animationType != ModelImporterAnimationType.Human || importer.avatarSetup != ModelImporterAvatarSetup.CopyFromOther
                       || importer.sourceAvatar != source || importer.materialImportMode != ModelImporterMaterialImportMode.None || !importer.importAnimation;
        if (changed)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = source;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = true;
            importer.SaveAndReimport();
        }
        var stem = Path.GetFileNameWithoutExtension(path);
        var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
        if (clips.Length != 1) throw new Exception($"{path} has {clips.Length} takes, expected one.");
        var clip = clips[0];
        bool clipChanged = clip.name != stem || clip.loopTime != loop || clip.loopPose != loop || !clip.lockRootRotation || !clip.lockRootHeightY
                           || clip.lockRootPositionXZ == moves || clip.keepOriginalOrientation || clip.keepOriginalPositionY || !clip.heightFromFeet
                           || clip.keepOriginalPositionXZ;
        if (clipChanged || importer.clipAnimations.Length == 0)
        {
            clip.name = stem;
            clip.loopTime = loop;
            clip.loopPose = loop;
            clip.lockRootRotation = true;
            clip.keepOriginalOrientation = false;
            clip.lockRootHeightY = true;
            clip.keepOriginalPositionY = false;
            clip.heightFromFeet = true;
            clip.lockRootPositionXZ = !moves;
            clip.keepOriginalPositionXZ = false;
            importer.clipAnimations = new[] { clip };
            importer.SaveAndReimport();
        }
        var loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => c.name == stem)
                     ?? throw new Exception($"Clip {stem} missing after import of {path}.");
        if (!loaded.humanMotion) throw new Exception($"Clip {stem} did not import as humanoid.");
        return loaded;
    }

    static AnimatorController BuildController(string path, List<(string state, AnimationClip clip)> states)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path) ?? AnimatorController.CreateAnimatorControllerAtPath(path);
        var machine = controller.layers[0].stateMachine;
        foreach (var (state, clip) in states)
        {
            var existing = machine.states.FirstOrDefault(s => s.state.name == state).state ?? machine.AddState(state);
            existing.motion = clip;
            existing.writeDefaultValues = true;
            if (state == CustomerCharacter.IdleState) machine.defaultState = existing;
        }
        EditorUtility.SetDirty(controller);
        return controller;
    }

    static CustomerCharacter BuildPrefab(string name, AnimatorController controller, Avatar avatar, AnimationClip leave)
    {
        var root = new GameObject(name) { layer = CustomerLayer };
        try
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath(name));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Model";
            instance.transform.localScale = Vector3.one * ModelScale;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = CustomerLayer;
            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            foreach (var skinned in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skinned.updateWhenOffscreen = false;
                skinned.skinnedMotionVectors = false;
                skinned.shadowCastingMode = ShadowCastingMode.On;
            }

            var agent = root.AddComponent<NavMeshAgent>();
            agent.agentTypeID = 0;
            agent.radius = AgentRadius;
            agent.height = 1.75f;
            agent.speed = AgentSpeed;
            agent.angularSpeed = 360f;
            agent.acceleration = 6f;
            agent.stoppingDistance = 0.05f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;

            var character = root.AddComponent<CustomerCharacter>();
            character.Animator = animator;
            character.LeaveSeconds = Mathf.Min(leave.length, MaxLeaveSeconds);
            // Off until the customer stands still (CustomerCharacter.Settle); then the others' paths go around it.
            var obstacle = root.GetComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Capsule;
            obstacle.radius = AgentRadius;
            obstacle.height = agent.height;
            obstacle.center = Vector3.up * (agent.height * 0.5f);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
            obstacle.enabled = false;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/{name}.prefab");
            return prefab.GetComponent<CustomerCharacter>();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    static Texture2D Texture(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? throw new Exception("Missing texture " + path);

    static Material LitMaterial(string path, Texture2D baseMap, Texture2D normal, bool cutout)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? throw new Exception("URP Lit missing."));
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetTexture("_BaseMap", baseMap);
        material.SetTexture("_MainTex", baseMap);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.2f);
        if (normal != null)
        {
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
        }
        if (cutout)
        {
            // Hair cards: alpha clipping, both faces. Blending would sort badly against the 3DGS pass.
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.4f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    [MenuItem("Tools/P2/Probe Customer Characters")]
    public static void Probe()
    {
        var log = new StringBuilder("[Customers] probe\n");
        foreach (var path in AssetDatabase.FindAssets("t:Model", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            log.AppendLine($"== {path} type={importer.animationType} avatarSetup={importer.avatarSetup} materials={importer.materialImportMode}");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                var tris = mesh == null ? "none" : string.Join("+", Enumerable.Range(0, mesh.subMeshCount).Select(i => mesh.GetSubMesh(i).indexCount / 3));
                log.AppendLine($"  renderer {renderer.name} tris={tris} bounds={renderer.bounds.size.ToString("F2")} materials=[{string.Join(", ", renderer.sharedMaterials.Select(m => m ? $"{m.name}({AssetDatabase.GetAssetPath(m)})" : "null"))}]");
            }
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is Avatar avatar) log.AppendLine($"  avatar {avatar.name} valid={avatar.isValid} human={avatar.isHuman}");
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                    log.AppendLine($"  clip {clip.name} length={clip.length:0.00} human={clip.humanMotion} loop={clip.isLooping} averageSpeed={clip.averageSpeed.ToString("F3")}");
            }
        }
        foreach (var path in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var character = prefab.GetComponent<CustomerCharacter>();
            var animator = character != null ? character.Animator : null;
            log.AppendLine($"== prefab {path} layer={prefab.layer} animator={(animator ? animator.runtimeAnimatorController?.name : "none")} avatar={(animator ? animator.avatar?.name : "none")} leave={character?.LeaveSeconds:0.00}");
        }
        Debug.Log(log.ToString());
    }
}
