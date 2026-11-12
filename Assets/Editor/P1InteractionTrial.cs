using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

/// <summary>
/// Builds the provisional product prefab and a separate interaction scene from the refined
/// dakdari result. The source scene and splat asset are read, never saved.
/// </summary>
public static class P1InteractionTrial
{
    const string ManifestFolder = "Assets/Data/Provisional";
    const string DakdariManifest = ManifestFolder + "/dev_dakdari_01.json";
    const string SourceScene = "Assets/Scenes/DakdariRefined.unity";
    const string PrefabFolder = "Assets/Prefabs/Products";
    const string DataFolder = "Assets/Data/SO";
    const string MaterialFolder = "Assets/Materials/P1";
    const string BuildEvidence = "docs/p1/evidence/dakdari_interaction";
    const string PlayerPath = "Builds/P1Interaction/P1Interaction.exe";

    const float BenchTopY = 0.85f;
    const float BarcodeTriggerThickness = 0.02f;
    const int MaxGateSplats = 150000;
    static readonly Vector3 BenchCenter = new Vector3(0f, BenchTopY * 0.5f, 0.55f);
    static readonly Vector3 BenchSize = new Vector3(1.2f, BenchTopY, 0.5f);
    static readonly Vector3 ScannerPosition = new Vector3(-0.22f, BenchTopY + 0.2f, 0.5f);
    const float MaxScannerSize = 0.08f;
    // On a thin product the zone must fit inside the product's depth, or the body cannot enter it without the barcode.
    const float ScannerToDepthRatio = 0.8f;

    [Serializable] class Manifest
    {
        public string id, capture_id, display_name, splat_asset, box_fit, barcode_face, scene, evidence_dir, note;
        public int price;
        public float provisional_width_m, provisional_mass_kg;
        public Vector3 barcode_local_normal;
    }

    [Serializable] class BoxAxes { public float[] x, y, z_front; }

    [Serializable] class BoxFit
    {
        public BoxAxes axes_in_ply;
        public float[] center_in_ply, extents_ply_units_xyz;
        public float basis_determinant;
    }

    [MenuItem("Tools/P1/Build Dakdari Interaction Trial")]
    public static void Build() => BuildFromManifest(DakdariManifest);

    /// <summary>-executeMethod entry point: -p1-manifest Assets/Data/Provisional/&lt;id&gt;.json</summary>
    public static void BuildFromArgs() => BuildFromManifest(RequireArgument("-p1-manifest"));

    static void BuildFromManifest(string manifestPath)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Exit Play first.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        AssetDatabase.Refresh();

        var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        var fit = JsonUtility.FromJson<BoxFit>(File.ReadAllText(manifest.box_fit));
        var ply = Path.ChangeExtension(manifest.splat_asset, ".ply").Replace('\\', '/');
        // -p1-reimport-splat: the PLY was replaced by a better reconstruction, so the splat asset is made again from it.
        // Its .meta stays, so the prefabs and scenes that point at the asset keep pointing at it.
        bool reimport = Array.IndexOf(Environment.GetCommandLineArgs(), "-p1-reimport-splat") >= 0;
        bool assetExisted = File.Exists(manifest.splat_asset) && !reimport;
        var preserved = assetExisted ? new[] { SourceScene, manifest.splat_asset, ply } : new[] { SourceScene, ply };
        var before = Array.ConvertAll(preserved, Hash);
        var asset = LoadOrCreateAsset(manifest.splat_asset, ply, reimport);

        Directory.CreateDirectory(PrefabFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(MaterialFolder);
        Directory.CreateDirectory(manifest.evidence_dir);
        AssetDatabase.Refresh();

        var scale = manifest.provisional_width_m / fit.extents_ply_units_xyz[0];
        var size = new Vector3(fit.extents_ply_units_xyz[0], fit.extents_ply_units_xyz[1], fit.extents_ply_units_xyz[2]) * scale;
        var data = CreateProductData(manifest, asset, size);
        var prefab = CreatePrefab(manifest, fit, data, asset, scale, size, out var gateReport);
        CreateScene(manifest, prefab, size, asset.splatCount);

        var after = Array.ConvertAll(preserved, Hash);
        for (int i = 0; i < preserved.Length; i++)
            if (before[i] != after[i]) throw new Exception("Preserved file changed: " + preserved[i]);

        AssetDatabase.SaveAssets();
        File.WriteAllText(manifest.evidence_dir + "/prefab_build.txt",
            $"PASS {gateReport}\nprovisional size_m={size.ToString("F4")} (width is a placeholder; height/depth follow reconstruction ratios)\n" +
            $"uniform scale={scale:F5}; mass_kg={manifest.provisional_mass_kg} (placeholder)\n" +
            $"preserved unchanged: {string.Join(", ", preserved)}\n");
        Debug.Log($"[P1 Interaction] Build PASS {manifest.id}: {gateReport}");
    }

    static GaussianSplatAsset LoadOrCreateAsset(string assetPath, string plyPath, bool reimport = false)
    {
        var asset = reimport ? null : AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
        if (asset != null) return asset;
        if (!File.Exists(plyPath)) throw new Exception("PLY missing: " + plyPath);
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            var serialized = new SerializedObject(creator);
            serialized.FindProperty("m_InputFile").stringValue = Path.GetFullPath(plyPath);
            serialized.FindProperty("m_OutputFolder").stringValue = Path.GetDirectoryName(plyPath).Replace('\\', '/');
            foreach (var format in new[] { "m_FormatPos", "m_FormatScale", "m_FormatColor", "m_FormatSH" })
                serialized.FindProperty(format).enumValueIndex = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            typeof(GaussianSplatAssetCreator).GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(creator, null);
            var error = typeof(GaussianSplatAssetCreator).GetField("m_ErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(creator) as string;
            if (!string.IsNullOrEmpty(error)) throw new Exception(error);
        }
        finally { UnityEngine.Object.DestroyImmediate(creator); }
        asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
        return asset != null ? asset : throw new Exception("Asset creation produced nothing at " + assetPath);
    }

    static ProductData CreateProductData(Manifest manifest, GaussianSplatAsset asset, Vector3 size)
    {
        var path = $"{DataFolder}/{manifest.id}.asset";
        var data = AssetDatabase.LoadAssetAtPath<ProductData>(path);
        if (data == null)
        {
            data = ScriptableObject.CreateInstance<ProductData>();
            AssetDatabase.CreateAsset(data, path);
        }
        data.Id = manifest.id;
        data.Name = manifest.display_name;
        data.Price = manifest.price;
        data.SizeCm = size * 100f;
        data.SizeIsMeasured = false;
        data.BarcodeFace = (BarcodeFace)Enum.Parse(typeof(BarcodeFace), manifest.barcode_face);
        data.BarcodeLocalNormal = manifest.barcode_local_normal;
        data.SplatAsset = asset;
        EditorUtility.SetDirty(data);
        return data;
    }

    static GameObject CreatePrefab(Manifest manifest, BoxFit fit, ProductData data, GaussianSplatAsset asset, float scale, Vector3 size, out string gateReport)
    {
        var root = new GameObject("P_" + manifest.id) { layer = RequireLayer("Interactable") };
        try
        {
            var body = root.AddComponent<Rigidbody>();
            body.mass = manifest.provisional_mass_kg;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // All physics lives on this +1-scale root. The reflection stays on the visual child.
            var proxy = root.AddComponent<BoxCollider>();
            proxy.size = size;

            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.throwOnDetach = true;
            grab.selectMode = UnityEngine.XR.Interaction.Toolkit.Interactables.InteractableSelectMode.Single;
            grab.trackScale = false;
            // Keep the pose at the moment of grabbing. A fixed attach would spin the product
            // around, because its front (+Z) faces the player while the hand's +Z faces away.
            grab.useDynamicAttach = true;
            grab.matchAttachPosition = true;
            grab.matchAttachRotation = true;
            grab.colliders.Add(proxy);

            var splatObject = new GameObject("Splat") { layer = RequireLayer("Splat") };
            splatObject.transform.SetParent(root.transform, false);
            ApplyPlyToProductTransform(splatObject.transform, fit, scale);
            var splat = splatObject.AddComponent<GaussianSplatRenderer>();
            splat.m_Asset = asset;
            if (!splat.m_ShaderSplats || !splat.m_CSSplatUtilities)
                throw new Exception("GaussianSplatRenderer default shader references were not applied.");

            var barcode = new GameObject("BarcodeTrigger") { layer = RequireLayer("BarcodeFace"), tag = "Barcode" };
            barcode.transform.SetParent(root.transform, false);
            var trigger = barcode.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            PlaceBarcodeTrigger(trigger, manifest.barcode_local_normal, size);

            var product = root.AddComponent<ProductPrefab>();
            product.Data = data;
            product.Splat = splat;
            product.Proxy = proxy;

            gateReport = RunGates(root, proxy, trigger, splat, data, asset);
            var path = $"{PrefabFolder}/P_{manifest.id}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
            if (!saved) throw new Exception("Prefab save failed: " + path);
            return prefab;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    /// <summary>
    /// Sets local = uniformScale * B^T * (ply - center), where B's columns are the product axes in PLY
    /// space. B has det = -1, so it is split into a proper rotation and a reflection on local X.
    /// </summary>
    static void ApplyPlyToProductTransform(Transform target, BoxFit fit, float scale)
    {
        var axes = fit.axes_in_ply;
        if (fit.basis_determinant > 0f) throw new Exception("Box fit must be a reflection (det < 0).");
        Vector3 x = ToVector(axes.x), y = ToVector(axes.y), z = ToVector(axes.z_front), center = ToVector(fit.center_in_ply);

        // Rows of the 3x3 are the product axes; negating column 0 removes the reflection.
        var rotation = Matrix4x4.identity;
        rotation.SetRow(0, new Vector4(-x.x, x.y, x.z, 0));
        rotation.SetRow(1, new Vector4(-y.x, y.y, y.z, 0));
        rotation.SetRow(2, new Vector4(-z.x, z.y, z.z, 0));
        target.localRotation = rotation.rotation;
        target.localScale = new Vector3(-scale, scale, scale);
        target.localPosition = -scale * new Vector3(Vector3.Dot(x, center), Vector3.Dot(y, center), Vector3.Dot(z, center));

        // Confirm the decomposition against the intended mapping.
        var probe = center + x * 0.1f + y * 0.2f + z * 0.3f;
        var mapped = Matrix4x4.TRS(target.localPosition, target.localRotation, target.localScale).MultiplyPoint3x4(probe);
        var error = (mapped - new Vector3(0.1f, 0.2f, 0.3f) * scale).magnitude;
        if (error > 1e-3f) throw new Exception($"PLY-to-product transform error {error}");
    }

    /// <summary>A thin product's plate has to leave room for the body to reach the scanner without the barcode.</summary>
    static float BarcodeThickness(float depth) => Mathf.Min(BarcodeTriggerThickness, depth * 0.25f);

    static void PlaceBarcodeTrigger(BoxCollider trigger, Vector3 normal, Vector3 size)
    {
        normal = normal.normalized;
        var absolute = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
        if (Mathf.Abs(Mathf.Max(absolute.x, absolute.y, absolute.z) - 1f) > 1e-4f)
            throw new Exception("Barcode normal must be a product axis.");
        var triggerSize = size;
        float thickness = BarcodeThickness(Mathf.Abs(Vector3.Dot(size, normal)));
        if (absolute.x > 0.5f) triggerSize.x = thickness;
        if (absolute.y > 0.5f) triggerSize.y = thickness;
        if (absolute.z > 0.5f) triggerSize.z = thickness;
        trigger.size = triggerSize;
        trigger.center = Vector3.Scale(normal, (size - triggerSize) * 0.5f);
    }

    /// <summary>Doc 4.6 gates that can apply without real measurements. The scale gate cannot.</summary>
    static string RunGates(GameObject root, BoxCollider proxy, BoxCollider trigger, GaussianSplatRenderer splat, ProductData data, GaussianSplatAsset asset)
    {
        if (!root.GetComponent<ProductPrefab>().Data || !splat.m_Asset) throw new Exception("Gate: a prefab layer is missing.");
        if (asset.splatCount > MaxGateSplats) throw new Exception($"Gate: {asset.splatCount} splats over {MaxGateSplats}.");
        if (root.transform.lossyScale != Vector3.one) throw new Exception("Gate: root must keep +1 scale.");

        var half = proxy.size * 0.5f + Vector3.one * 1e-4f;
        var min = trigger.center - trigger.size * 0.5f;
        var max = trigger.center + trigger.size * 0.5f;
        for (int i = 0; i < 3; i++)
            if (min[i] < -half[i] || max[i] > half[i]) throw new Exception("Gate: BarcodeTrigger leaves the proxy box.");

        if (data.SizeIsMeasured) throw new Exception("Gate: provisional product must not claim measured size.");
        return $"gates: layers ok; splats={asset.splatCount}<={MaxGateSplats}; root scale +1; splat localScale={splat.transform.localScale.ToString("F5")}; " +
               $"BarcodeTrigger inside proxy; scale gate NOT APPLIED (no measured size)";
    }

    static void CreateScene(Manifest manifest, GameObject prefab, Vector3 size, int expectedSplats)
    {
        var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == "Nerfstudio Coordinate Conversion" || root.name == "Trial Verification" || root.name == "TestCam")
                UnityEngine.Object.DestroyImmediate(root);

        EnableNearGrabOnProductLayer();

        var floor = GameObject.Find("P0 Floor") ?? throw new Exception("P0 Floor missing.");
        floor.AddComponent<ProductRespawnSurface>();
        var floorMaterial = LitMaterial("P1_Floor", new Color(0.32f, 0.33f, 0.35f));
        var floorVisual = Visual("Floor Visual", new Vector3(0f, -0.05f, 1f), new Vector3(8f, 0.1f, 8f), floorMaterial);
        floorVisual.transform.SetParent(floor.transform, true);

        var bench = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bench.name = "Workbench";
        bench.transform.position = BenchCenter;
        bench.transform.localScale = BenchSize;
        bench.GetComponent<Renderer>().sharedMaterial = LitMaterial("P1_Workbench", new Color(0.72f, 0.70f, 0.66f));

        var light = new GameObject("Directional Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var scanner = new GameObject("ScannerZone") { layer = RequireLayer("Scanner") };
        scanner.transform.position = ScannerPosition;
        var scannerCollider = scanner.AddComponent<BoxCollider>();
        scannerCollider.isTrigger = true;
        var normal = manifest.barcode_local_normal.normalized;
        float depthAlongBarcode = Mathf.Abs(Vector3.Dot(size, normal));
        // The zone's half size must stay under the part of the depth the barcode trigger does not fill,
        // or the body cannot enter the zone without the barcode on a very thin product.
        float freeDepth = depthAlongBarcode - BarcodeThickness(depthAlongBarcode);
        float scannerSize = Mathf.Min(MaxScannerSize, depthAlongBarcode * ScannerToDepthRatio, 2f * freeDepth * ScannerToDepthRatio);
        scannerCollider.size = Vector3.one * scannerSize;
        var scannerZone = scanner.AddComponent<ScannerZone>();
        var indicator = Visual("Indicator", ScannerPosition, Vector3.one * scannerSize, TransparentMaterial("P1_Scanner", new Color(0.9f, 0.1f, 0.1f, 0.35f)));
        indicator.transform.SetParent(scanner.transform, true);
        scannerZone.Indicator = indicator.GetComponent<Renderer>();

        var product = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        // Front (+Z) faces the player at the origin. Standing on the unobserved underside hides that defect at rest.
        product.transform.SetPositionAndRotation(new Vector3(0.05f, BenchTopY + size.y * 0.5f + 0.002f, 0.5f), Quaternion.Euler(0f, 180f, 0f));

        var verification = new GameObject("P1 Interaction Verification").AddComponent<P1InteractionVerification>();
        verification.product = product.GetComponent<ProductPrefab>();
        verification.scanner = scannerZone;
        verification.benchTopY = BenchTopY;
        verification.benchMin = BenchCenter - BenchSize * 0.5f;
        verification.benchMax = BenchCenter + BenchSize * 0.5f;
        verification.expectedSplatCount = expectedSplats;
        verification.wireMaterial = UnlitMaterial("P1_ProxyWire", new Color(0.1f, 1f, 0.9f));

        if (!EditorSceneManager.SaveScene(scene, manifest.scene)) throw new Exception("Scene save failed.");
    }

    /// <summary>
    /// Starter Assets' near caster only sees physics layer Default, but products live on Interactable
    /// (doc 4.3), so hands could not sense them. This is saved as a scene override; the sample prefab
    /// stays as imported. The far caster is left alone, keeping products hand-grab only (doc 5.1).
    /// Any later scene with this rig, such as StoreScene, needs the same override.
    /// </summary>
    public static void EnableNearGrabOnProductLayer()
    {
        int changed = 0;
        foreach (var interactor in UnityEngine.Object.FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (interactor.nearInteractionCaster is not SphereInteractionCaster caster)
                throw new Exception("Expected a sphere near caster on " + interactor.name);
            caster.physicsLayerMask |= 1 << RequireLayer("Interactable");
            EditorUtility.SetDirty(caster);
            PrefabUtility.RecordPrefabInstancePropertyModifications(caster);
            changed++;
        }
        if (changed != 2) throw new Exception($"Expected 2 near-far interactors, found {changed}.");
    }

    static GameObject Visual(string name, Vector3 position, Vector3 scale, Material material)
    {
        var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = name;
        UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.transform.position = position;
        visual.transform.localScale = scale;
        visual.GetComponent<Renderer>().sharedMaterial = material;
        return visual;
    }

    static Material LitMaterial(string name, Color color) => SaveMaterial(name, "Universal Render Pipeline/Lit", color, false);
    static Material UnlitMaterial(string name, Color color) => SaveMaterial(name, "Universal Render Pipeline/Unlit", color, false);
    static Material TransparentMaterial(string name, Color color) => SaveMaterial(name, "Universal Render Pipeline/Unlit", color, true);

    static Material SaveMaterial(string name, string shaderName, Color color, bool transparent)
    {
        var path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find(shaderName) ?? throw new Exception("Shader missing: " + shaderName);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", color);
        if (transparent)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    static int RequireLayer(string name)
    {
        int layer = LayerMask.NameToLayer(name);
        if (layer < 0) throw new Exception("Layer missing: " + name);
        return layer;
    }

    static Vector3 ToVector(float[] v) => new Vector3(v[0], v[1], v[2]);

    static string Hash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream));
    }

    /// <summary>-executeMethod entry point: -p1-verify -p1-scene Assets/Scenes/&lt;name&gt;.unity</summary>
    public static void VerifyEditor()
    {
        EditorSceneManager.OpenScene(RequireArgument("-p1-scene"));
        EditorApplication.isPlaying = true;
    }

    /// <summary>Builds every provisional product's scene. Choose one at launch with -p1-scene &lt;name&gt;.</summary>
    public static void BuildPlayer()
    {
        var scenes = Directory.GetFiles(ManifestFolder, "*.json")
            .Where(path => !path.EndsWith(".box_fit.json"))
            .OrderBy(path => path.EndsWith(Path.GetFileName(DakdariManifest)) ? 0 : 1)
            .Select(path => JsonUtility.FromJson<Manifest>(File.ReadAllText(path)).scene)
            .ToArray();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        var summary = report.summary;
        Directory.CreateDirectory(BuildEvidence);
        File.WriteAllText(BuildEvidence + "/il2cpp_build.txt",
            $"Unity {Application.unityVersion}; Windows x64 IL2CPP Development; result={summary.result}; errors={summary.totalErrors}; warnings={summary.totalWarnings}; bytes={summary.totalSize}; duration={summary.totalTime}; scenes={string.Join(", ", scenes)}; output={PlayerPath}\n");
        if (summary.result != BuildResult.Succeeded) throw new Exception("Player build failed: " + summary.result);
    }

    static string RequireArgument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new Exception("Missing argument " + name);
        return args[index + 1];
    }
}
