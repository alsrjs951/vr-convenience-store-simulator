using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Builds StoreScene: a counter-centered Day 1 layout. Sizes and layout are working values until the
/// real layout (P3-K1) exists. Product data here is provisional and never comes from Data/products.csv.
/// </summary>
public static class P2StoreBuilder
{
    const string StoreScene = "Assets/Scenes/StoreScene.unity";
    const string BaseScene = "Assets/Scenes/DakdariRefined.unity";
    const string CatalogPath = "Assets/Data/SO/ProductCatalog.asset";
    // Doc 6.5: the order scenario is Data/scenarios.csv at the repository root (designer-owned). The build copies it into
    // the project as a TextAsset, after checking every row of every day parses against the catalog.
    const string ScenarioSourceDefault = "Data/scenarios.csv";
    const string ScenarioPathDefault = "Assets/Data/Generated/scenarios.csv";
    const int ScenarioDays = 3;
    // Doc 6.5: the intake boxes are Data/deliveries.csv at the repository root; the build copies it in like the scenario.
    const string DeliveriesSource = "Data/deliveries.csv";
    const string DeliveriesPath = "Assets/Data/Generated/deliveries.csv";
    // What the ordering terminal offers (docs/p2/해금_성장_기획_2026-11-10.md), copied in like the other two.
    const string UnlocksSource = "Data/unlocks.csv";
    const string UnlocksPath = "Assets/Data/Generated/unlocks.csv";
    // The short days of learning of a store that is kept: same schemas as the scenario and the deliveries.
    const string TrainingScenarioSource = "Data/training_scenarios.csv";
    const string TrainingScenarioPath = "Assets/Data/Generated/training_scenarios.csv";
    const string TrainingDeliveriesSource = "Data/training_deliveries.csv";
    const string TrainingDeliveriesPath = "Assets/Data/Generated/training_deliveries.csv";
    const string ManifestFolder = "Assets/Data/Provisional";
    const string MaterialFolder = "Assets/Materials/P2";
    const string Evidence = "docs/p2/evidence/day1";
    const string PlayerPath = "Builds/Store/VRStore.exe";
    const string SimulatorPrefab = "Assets/Samples/XR Interaction Toolkit/3.6.1/XR Device Simulator/XR Device Simulator.prefab";
    const string VignettePrefab = "Assets/Samples/XR Interaction Toolkit/3.6.1/Starter Assets/TunnelingVignette/TunnelingVignette.prefab";

    // Free movement, design doc D11 and 5.1.
    const int PlayerLayer = 14;
    const float MoveSpeed = 1.5f;
    const float SnapTurnDegrees = 45f;
    const float CapsuleRadius = 0.2f;
    // The Starter Assets skin of 0.08 would keep the body 0.28 m from walls instead of the 0.2 m the doc asks for.
    const float CapsuleSkin = 0.02f;

    // Customers (doc 6.1, D12). Layout of 2026-11-09, after a photo of a Korean convenience store taken from the near end of
    // the counter, looking along it. The counter and the clerk's place did not move, so the photo's view runs toward -x: its
    // back wall of drinks fridges is the far wall (x = RoomFar), its right wall of chilled cases is the front wall (z = RoomFront),
    // and the tobacco wall behind the clerk and the glass street front with the door are on its left (toward -z). Gondolas run
    // along x, in depth as the photo sees them: row D in line with the counter past its far end, row A + B behind it.
    const string NavMeshPath = "Assets/Data/Navigation/StoreNavMesh.asset";
    const float AgentRadius = 0.25f;
    const float CounterFar = 0.95f;
    public const float RoomFar = -6f, RoomNear = 4.5f, RoomBack = -2.5f, RoomFront = 5.5f;
    // The street front runs along z = StorefrontZ from the far wall to the partition at the end of the tobacco wall.
    public const float StorefrontZ = -1.6f, PartitionX = -1.75f;
    public static readonly Vector3 DoorCenter = new Vector3(-3.6f, 0f, StorefrontZ);
    // 2.4 m so a customer coming in and one going out can pass in the doorway (agents are 0.5 m across). At 1.4 m they
    // could not (2026-11-08, IL2CPP player run); at 2.0 m in the left wall one still waited out a warp there.
    public const float DoorWidth = 2.4f, DoorHeight = 2.2f, VestibuleDepth = 1.5f;

    const float CounterTop = 0.9f;
    // The counter goes on past the end where the card terminal is, as in the photo, with nothing on it the rules use.
    public const float CounterExtensionEnd = 2f;

    // Day 2 intake (doc 5.7, 6.4). Slot assignment is provisional (game design 19). Everything in the clerk's area keeps off
    // the routes the locomotion check walks: the start lane near x = 0, the walks from the start (+x along z = 0.05, the
    // diagonal to (1.04, -1.01)), the lane along z = -0.1 between the far wall and the near wall, the room-scale walk to the
    // near wall along z = -0.13 and the slide down that wall to z = -0.84 (x > 4.0), and the way back round the counter's far
    // end (x = -1.6, then z = 0). The stands sit at the ends of the row, so the clerk works away from the aisle in front of it.
    public const float ShelfFront = 2.95f, ShelfBack = 3.45f, ShelfWidth = 1.5f, ShelfHeight = 1.8f;
    public const float ShelfAX = -2.65f, ShelfBX = -1.15f;
    // Row D stands in line with the counter. It has no delivery slots.
    // Customers may walk the floor up to here between the street front and row D; wide enough for two to pass the counter's far end.
    const float CustomerFloorEnd = -1.2f;
    public const float ShelfDX = -3.3f, ShelfDFront = 0.55f, ShelfDBack = 1.05f, ShelfDBayDepth = 0.32f;
    static readonly float[] ShelfBoardTops = { 0.55f, 1.0f, 1.4f };
    const float StandTop = 0.8f;
    static readonly Vector3 StandACenter = new Vector3(ShelfAX - ShelfWidth * 0.5f - 0.25f, 0f, 3.25f), StandBCenter = new Vector3(ShelfBX + ShelfWidth * 0.5f + 0.25f, 0f, 3.25f);
    static readonly Vector3 CartCenter = new Vector3(3.5f, 0f, -1.55f);
    const float CartWidth = 1.1f, CartSpotOffset = 0.28f;
    static readonly Vector3 RecycleCenter = new Vector3(2.6f, 0f, -1.575f);
    static readonly Vector3 RecycleSize = new Vector3(0.6f, 0.7f, 0.65f);
    static readonly Vector3 DisposalCenter = new Vector3(-1.0f, 0f, -0.7f);
    // A card whose centre comes this close to its seat in the terminal slot is pulled in. The seat is half a card inside the
    // face, so this starts about 3 cm before the card's front edge reaches the opening.
    const float CardSnapMeters = 0.08f;
    // Barcode roughly toward one of the scanner's two glasses. A product standing on the flat one, with its barcode facing
    // the clerk or a side, does not scan.
    const float MaxBarcodeAngle = 50f;
    // The scanner's upright part, which P3StoreDressing draws: it stands this far behind the flat glass and leans away from
    // the clerk. The red light comes out of the glass in its face, whose middle is at ScannerWindowHeight above the counter.
    public const float ScannerTowerGap = 0.03f, ScannerTowerHeight = 0.3f, ScannerTowerLean = 0.05f, ScannerWindowHeight = 0.165f;
    // The space in front of the upright glass where a barcode is read in the air: as wide as the scanner, from just above
    // the flat glass up to about the top of the upright one, and out to the scanner's front edge.
    const float ScanReadWidth = 0.2f, ScanReadHeight = 0.22f, ScanReadDepth = 0.24f;
    // The light starts this far behind the glass, so it is already a few centimetres wide where it comes out.
    const float ScanLightBehindGlass = 0.06f;
    static readonly Vector3 EyePosition = new Vector3(0f, 1.36f, 0f);

    // The till (2026-11-10) at the counter's left end, under the POS monitor: a steel box whose drawer comes out toward the
    // clerk by DrawerTravel. RegisterFront is the body's face on the clerk's side; the drawer's own origin is the point of
    // the counter top under the middle of that face, and what follows is measured from it (z into the body).
    public const float RegisterX = -0.78f, RegisterFront = 0.53f, RegisterWidth = 0.4f, RegisterDepth = 0.41f, RegisterHeight = 0.105f, DrawerTravel = 0.3f;
    // The insert that holds the money: its floor and rim, four columns across, coin cups in front and note slots behind.
    public const float TillFloor = 0.055f, TillRim = 0.088f, TillHalfWidth = 0.182f;
    public const float CoinFront = 0.024f, CoinBack = 0.118f, NoteFront = 0.123f, NoteBack = 0.293f;
    // How high the wad of notes and the heap of coins under the piece on top stand.
    public const float NoteStack = 0.01f, CoinHeap = 0.012f;
    // Largest on the left, as tills are filled; the ones change is mostly made of end up nearest the clerk's place.
    public static readonly int[] NoteValues = { 50000, 10000, 5000, 1000 }, CoinValues = { 500, 100, 50, 10 };
    // Real notes are all 68 mm tall and grow 6 mm longer with each step in value.
    public const float NoteWidth = 0.068f, NoteThickness = 0.0012f, CoinThickness = 0.003f;
    public static float NoteLength(int value) => value >= 50000 ? 0.154f : value >= 10000 ? 0.148f : value >= 5000 ? 0.142f : 0.136f;
    // A quarter larger than real coins (26.5, 24, 21.6 and 18 mm), so the number on one can be read in a headset.
    public static float CoinDiameter(int value) => 1.25f * (value >= 500 ? 0.0265f : value >= 100 ? 0.024f : value >= 50 ? 0.0216f : 0.018f);
    public static float TillColumn(int index) => -TillHalfWidth + (index + 0.5f) * TillHalfWidth * 0.5f;
    const string CashArt = "Assets/Art/Cash";

    [Serializable] class Manifest { public string id, display_name, package; public int price; }

    [MenuItem("Tools/P2/Build Store Scene")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Exit Play first.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Directory.CreateDirectory(MaterialFolder);
        AssetDatabase.Refresh();
        EnsurePlayerLayer();
        var catalog = BuildCatalog();
        var characters = P2CustomerSetup.Setup();
        PlayerHandSetup.Setup();
        BuildScene(catalog, characters);
        AssetDatabase.SaveAssets();
        Debug.Log($"[P2] Store scene built with {catalog.Products.Count} products.");
    }

    /// <summary>Every scene places this prefab, so the guard on it covers them all. Re-importing the XRI sample drops it.</summary>
    [MenuItem("Tools/P2/Install Simulator HMD Guard")]
    public static void InstallSimulatorGuard()
    {
        var root = PrefabUtility.LoadPrefabContents(SimulatorPrefab);
        try
        {
            if (root.GetComponent<SimulatorHmdGuard>() != null)
            {
                Debug.Log("[P2] Simulator guard already installed.");
                return;
            }
            root.AddComponent<SimulatorHmdGuard>();
            PrefabUtility.SaveAsPrefabAsset(root, SimulatorPrefab);
            Debug.Log("[P2] Simulator guard installed.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>Doc 5.1: the player's capsule touches only the environment, never what it carries or what lies around.</summary>
    static void EnsurePlayerLayer()
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var name = tagManager.FindProperty("layers").GetArrayElementAtIndex(PlayerLayer);
        if (name.stringValue != "Player")
        {
            if (!string.IsNullOrEmpty(name.stringValue)) throw new Exception($"Layer {PlayerLayer} is already used by {name.stringValue}.");
            name.stringValue = "Player";
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }
        for (int other = 0; other < 32; other++)
            Physics.IgnoreLayerCollision(PlayerLayer, other, other != 0);
        EditorUtility.SetDirty(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset")[0]);
    }

    /// <summary>
    /// The Starter Assets rig already has the left stick moving and the right stick snap turning. This sets the doc's
    /// values, turns off everything the doc does not want (teleport, jump, climb, continuous turn, turn-around), and
    /// adds the vignette, which the rig does not include.
    /// </summary>
    static void ConfigureLocomotion()
    {
        var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>() ?? throw new Exception("XR Origin missing.");
        var root = origin.gameObject;
        root.layer = PlayerLayer;
        PrefabUtility.RecordPrefabInstancePropertyModifications(root);

        // Standing play: the counter and shelves are at real heights, so the player's real floor has to be the store's.
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
        PrefabUtility.RecordPrefabInstancePropertyModifications(origin);

        var capsule = root.GetComponent<CharacterController>();
        capsule.radius = CapsuleRadius;
        capsule.skinWidth = CapsuleSkin;
        capsule.center = new Vector3(0f, capsule.height * 0.5f + CapsuleSkin, 0f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(capsule);

        // Real walking: the capsule stops at obstacles instead of following the head into them.
        var transformer = new SerializedObject(root.GetComponentInChildren<XRBodyTransformer>(true));
        transformer.FindProperty("m_BodyPositionEvaluatorObject").objectReferenceValue = root.AddComponent<RoomScaleBody>();
        transformer.ApplyModifiedPropertiesWithoutUndo();
        var gravity = root.GetComponentInChildren<GravityProvider>(true);
        gravity.updateCharacterControllerCenterEachFrame = false;
        PrefabUtility.RecordPrefabInstancePropertyModifications(gravity);

        var move = root.GetComponentInChildren<DynamicMoveProvider>(true);
        move.moveSpeed = MoveSpeed;
        move.enableStrafe = true;
        move.enableFly = false;
        move.leftHandMovementDirection = DynamicMoveProvider.MovementDirection.HeadRelative;
        PrefabUtility.RecordPrefabInstancePropertyModifications(move);

        var snap = root.GetComponentInChildren<SnapTurnProvider>(true);
        snap.turnAmount = SnapTurnDegrees;
        snap.enableTurnLeftRight = true;
        snap.enableTurnAround = false;
        PrefabUtility.RecordPrefabInstancePropertyModifications(snap);

        var continuousTurn = root.GetComponentInChildren<ContinuousTurnProvider>(true);
        continuousTurn.enabled = false;
        PrefabUtility.RecordPrefabInstancePropertyModifications(continuousTurn);

        // Doc 5.1: customers steer around the player; no physics between them.
        root.AddComponent<NavMeshObstacle>();
        root.AddComponent<PlayerNavObstacle>();

        foreach (var path in new[]
                 {
                     "Locomotion/Teleportation", "Locomotion/Climb", "Locomotion/Jump", "Locomotion/Grab Move",
                     "Camera Offset/Left Controller Teleport Stabilized Origin", "Camera Offset/Right Controller Teleport Stabilized Origin"
                 })
            Deactivate((root.transform.Find(path) ?? throw new Exception("Rig has no " + path)).gameObject);

        foreach (var manager in root.GetComponentsInChildren<ControllerInputActionManager>(true))
        {
            var fields = new SerializedObject(manager);
            var interactor = (Component)fields.FindProperty("m_TeleportInteractor").objectReferenceValue;
            if (interactor == null) throw new Exception("No teleport interactor on " + manager.name);
            Deactivate(interactor.gameObject);
            // Without these, pushing the right stick forward would still try to aim a teleport.
            fields.FindProperty("m_TeleportInteractor").objectReferenceValue = null;
            fields.FindProperty("m_TeleportMode").objectReferenceValue = null;
            fields.FindProperty("m_TeleportModeCancel").objectReferenceValue = null;
            fields.ApplyModifiedPropertiesWithoutUndo();
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VignettePrefab) ?? throw new Exception("Vignette prefab missing.");
        var vignette = (GameObject)PrefabUtility.InstantiatePrefab(prefab, origin.Camera.transform);
        vignette.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        var controller = vignette.GetComponent<TunnelingVignetteController>();
        // A snap turn is over in a frame or two, too short to fade in unless the fade-in is allowed to finish.
        var snapParameters = new VignetteParameters();
        snapParameters.CopyFrom(controller.defaultParameters);
        snapParameters.easeInTimeLock = true;
        controller.locomotionVignetteProviders = new List<LocomotionVignetteProvider>
        {
            new LocomotionVignetteProvider { locomotionProvider = move, enabled = true },
            new LocomotionVignetteProvider { locomotionProvider = snap, enabled = true, overrideDefaultParameters = true, overrideParameters = snapParameters },
        };
        PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
    }

    static void Deactivate(GameObject target)
    {
        target.SetActive(false);
        PrefabUtility.RecordPrefabInstancePropertyModifications(target);
    }

    static ProductCatalog BuildCatalog()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<ProductCatalog>(CatalogPath);
        if (catalog == null)
        {
            catalog = ScriptableObject.CreateInstance<ProductCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }
        catalog.Products.Clear();
        foreach (var path in Directory.GetFiles(ManifestFolder, "dev_*.json"))
        {
            if (path.EndsWith(".box_fit.json")) continue;
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{manifest.id}.prefab");
            if (prefab == null) throw new Exception("Prefab missing for " + manifest.id);
            var product = prefab.GetComponent<ProductPrefab>();
            product.Data.Name = manifest.display_name;
            product.Data.Price = manifest.price;
            product.Data.Package = PlayerHandSetup.PackageOf(manifest.package);
            product.Data.LightSplat = AssetDatabase.LoadAssetAtPath<GaussianSplatting.Runtime.GaussianSplatAsset>($"Assets/GaussianSplats/Products/{manifest.id}_LOD1.asset");
            EditorUtility.SetDirty(product.Data);
            catalog.Products.Add(product);
        }
        EditorUtility.SetDirty(catalog);
        return catalog;
    }

    static void BuildScene(ProductCatalog catalog, CustomerCharacter[] characters)
    {
        var scene = EditorSceneManager.OpenScene(BaseScene, OpenSceneMode.Single);
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == "Nerfstudio Coordinate Conversion" || root.name == "Trial Verification" || root.name == "TestCam")
                UnityEngine.Object.DestroyImmediate(root);
        P1InteractionTrial.EnableNearGrabOnProductLayer();
        ConfigureLocomotion();
        PlayerHandSetup.Install((UnityEngine.Object.FindFirstObjectByType<XROrigin>() ?? throw new Exception("XR Origin missing.")).transform);

        new GameObject("EventSystem", typeof(EventSystem), typeof(XRUIInputModule));
        var light = new GameObject("Directional Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var floorCollider = GameObject.Find("P0 Floor") ?? throw new Exception("P0 Floor missing.");
        floorCollider.AddComponent<ProductRespawnSurface>();

        var white = Material("P2_Wall", new Color(0.78f, 0.78f, 0.74f));
        var floor = Material("P2_Floor", new Color(0.42f, 0.43f, 0.45f));
        var wood = Material("P2_Counter", new Color(0.62f, 0.5f, 0.38f));
        var dark = Material("P2_Device", new Color(0.13f, 0.14f, 0.16f));
        var shelf = Material("P2_Shelf", new Color(0.55f, 0.6f, 0.65f));
        var red = Material("P2_ScannerPad", new Color(0.8f, 0.08f, 0.08f));

        var environment = new GameObject("Environment").transform;
        float roomCenterX = (RoomFar + RoomNear) * 0.5f, roomWidth = RoomNear - RoomFar, roomCenterZ = (RoomBack + RoomFront) * 0.5f, roomDepth = RoomFront - RoomBack;
        Box("Floor Visual", new Vector3(roomCenterX, -0.05f, roomCenterZ), new Vector3(roomWidth, 0.1f, roomDepth), floor, environment, false);
        Box("Back Wall", new Vector3((PartitionX + RoomNear) * 0.5f, 1.5f, RoomBack), new Vector3(RoomNear - PartitionX, 3f, 0.1f), white, environment, true);
        Box("Front Wall", new Vector3(roomCenterX, 1.5f, RoomFront), new Vector3(roomWidth, 3f, 0.1f), white, environment, true);
        Box("Far Wall", new Vector3(RoomFar, 1.5f, (StorefrontZ + RoomFront) * 0.5f), new Vector3(0.1f, 3f, RoomFront - StorefrontZ), white, environment, true);
        Box("Right Wall", new Vector3(RoomNear, 1.5f, roomCenterZ), new Vector3(0.1f, 3f, roomDepth), white, environment, true);

        // The door customers come in by (D12) is in the glass street front past the counter's far end, so the clerk sees who
        // comes in: an open doorway onto a strip of pavement, so they walk in instead of appearing in the room.
        float doorLeft = DoorCenter.x - DoorWidth * 0.5f, doorRight = DoorCenter.x + DoorWidth * 0.5f;
        float frontCenter = (RoomFar + PartitionX) * 0.5f, frontLength = PartitionX - RoomFar;
        var storeGlass = TransparentMaterial("P2_StoreGlass", new Color(0.72f, 0.84f, 0.9f, 0.16f));
        Box("Storefront Header", new Vector3(frontCenter, (DoorHeight + 3f) * 0.5f, StorefrontZ), new Vector3(frontLength, 3f - DoorHeight, 0.1f), white, environment, true);
        Box("Storefront Glass Far", new Vector3((RoomFar + doorLeft) * 0.5f, DoorHeight * 0.5f, StorefrontZ), new Vector3(doorLeft - RoomFar, DoorHeight, 0.02f), storeGlass, environment, true);
        Box("Storefront Glass Near", new Vector3((doorRight + PartitionX) * 0.5f, DoorHeight * 0.5f, StorefrontZ), new Vector3(PartitionX - doorRight, DoorHeight, 0.02f), storeGlass, environment, true);
        var porch = Material("P2_Porch", new Color(0.16f, 0.17f, 0.19f));
        float porchZ = StorefrontZ - VestibuleDepth * 0.5f;
        Box("Porch Floor", new Vector3(frontCenter, -0.05f, porchZ), new Vector3(frontLength, 0.1f, VestibuleDepth), porch, environment, false);
        Box("Street Backdrop", new Vector3(frontCenter, 1.5f, StorefrontZ - VestibuleDepth - 0.05f), new Vector3(frontLength, 3f, 0.1f), porch, environment, true);
        Box("Porch Side", new Vector3(RoomFar, 1.5f, porchZ), new Vector3(0.1f, 3f, VestibuleDepth), porch, environment, true);
        Box("Partition", new Vector3(PartitionX, 1.5f, (RoomBack - 0.65f + StorefrontZ) * 0.5f), new Vector3(0.1f, 3f, StorefrontZ - RoomBack + 0.65f), white, environment, true);
        Box("Porch Roof", new Vector3(frontCenter, 3.05f, porchZ), new Vector3(frontLength, 0.1f, VestibuleDepth), porch, environment, false);

        // Gondolas with boards (Day 2 puts products on row A + B), fronts toward the counter.
        Gondola("Shelf A", ShelfAX, ShelfFront, ShelfBack, shelf, environment);
        Gondola("Shelf B", ShelfBX, ShelfFront, ShelfBack, shelf, environment);
        Gondola("Shelf D", ShelfDX, ShelfDFront, ShelfDBack, shelf, environment);
        // The solid part of the shallow bay on the row's near end. P3StoreDetails draws it; the NavMesh and the player need it here.
        Box("Row D End Bay Solid", new Vector3(ShelfDX + ShelfWidth * 0.5f + ShelfDBayDepth * 0.5f, 0.95f, (ShelfDFront + ShelfDBack) * 0.5f), new Vector3(ShelfDBayDepth, 1.9f, ShelfDBack - ShelfDFront), shelf, environment, true)
            .GetComponent<Renderer>().enabled = false;
        Box("Counter", new Vector3(0f, CounterTop * 0.5f, 0.65f), new Vector3(2f, CounterTop, 0.6f), wood, environment, true);
        Box("Counter Extension", new Vector3((1f + CounterExtensionEnd) * 0.5f, CounterTop * 0.5f, 0.65f), new Vector3(CounterExtensionEnd - 1f, CounterTop, 0.6f), wood, environment, true);

        // ---------- Scanner: two glasses, as on a shop's fixed scanner ----------
        // The flat one is a solid pad with a thin zone on top, so only the face lying on it can scan.
        var padTop = CounterTop + 0.05f;
        var padCenter = new Vector3(0f, CounterTop + 0.025f, 0.55f);
        var padSize = new Vector3(0.2f, 0.05f, 0.2f);
        Box("Scanner Pad", padCenter, padSize, dark, environment, true);
        var scannerMark = Box("Scanner Glass", new Vector3(0f, padTop + 0.001f, 0.55f), new Vector3(0.18f, 0.002f, 0.18f), red, environment, false);
        var scannerObject = new GameObject("ScannerZone") { layer = LayerMask.NameToLayer("Scanner") };
        scannerObject.transform.position = new Vector3(0f, padTop + 0.004f, 0.55f);
        var scannerCollider = scannerObject.AddComponent<BoxCollider>();
        scannerCollider.isTrigger = true;
        scannerCollider.size = new Vector3(0.18f, 0.008f, 0.18f);
        var scanner = scannerObject.AddComponent<ScannerZone>();
        scanner.Indicator = scannerMark.GetComponent<Renderer>();
        scanner.MaxBarcodeAngle = MaxBarcodeAngle;
        AddLabel(scannerObject.transform, "스캐너");

        // The upright one reads in the air in front of it, and the red light comes out of it. Its glass leans back with the
        // tower; the space it reads is kept level, so it does not rise off the flat glass toward the clerk.
        float towerSlant = Mathf.Atan2(ScannerTowerLean, ScannerTowerHeight);
        var glassOut = new Vector3(0f, Mathf.Sin(towerSlant), -Mathf.Cos(towerSlant));
        float towerFront = padCenter.z + padSize.z * 0.5f + ScannerTowerGap;
        var glassCenter = new Vector3(padCenter.x, CounterTop + ScannerWindowHeight, towerFront + ScannerTowerLean * ScannerWindowHeight / ScannerTowerHeight) + glassOut * 0.004f;
        var windowObject = new GameObject("Scan Window") { layer = scannerObject.layer };
        windowObject.transform.SetParent(scannerObject.transform, false);
        windowObject.transform.SetPositionAndRotation(
            new Vector3(padCenter.x, padTop + 0.008f + ScanReadHeight * 0.5f, glassCenter.z - 0.01f - ScanReadDepth * 0.5f),
            Quaternion.LookRotation(Vector3.up, Vector3.back));
        var windowCollider = windowObject.AddComponent<BoxCollider>();
        windowCollider.isTrigger = true;
        windowCollider.size = new Vector3(ScanReadWidth, ScanReadDepth, ScanReadHeight);
        windowObject.AddComponent<ScanWindow>().Zone = scanner;
        scanner.Aim = Marker("Scan Aim", glassCenter, Quaternion.LookRotation(Vector3.Cross(Vector3.right, glassOut), glassOut), scannerObject.transform);

        var beamObject = new GameObject("Scan Beam");
        beamObject.transform.SetParent(scannerObject.transform, false);
        var lightFrom = glassCenter - glassOut * ScanLightBehindGlass;
        beamObject.transform.SetPositionAndRotation(lightFrom, Quaternion.LookRotation(Vector3.back, Vector3.up));
        var beam = beamObject.AddComponent<ScanBeam>();
        beam.Zone = scanner;
        beam.Material = BeamMaterial();
        beam.Reaches = LayerMask.GetMask("Default", "Interactable");
        // One line that falls on the middle of the flat glass when nothing is there, and a cross above it that only shows on
        // a product held up to the upright glass.
        float toFlatGlass = Mathf.Atan2(lightFrom.y - padTop, lightFrom.z - (padCenter.z - 0.01f)) * Mathf.Rad2Deg;
        beam.Lines = new[] { new Vector2(toFlatGlass, 0f), new Vector2(6f, 24f), new Vector2(6f, -24f) };
        beam.HalfAngle = 20f;

        // ---------- Systems ----------
        var systems = new GameObject("Systems").transform;
        var audioObject = new GameObject("Counter Audio");
        audioObject.transform.SetParent(systems, false);
        audioObject.transform.position = new Vector3(0f, 1.1f, 0.7f);
        var gameAudio = audioObject.AddComponent<GameAudio>();
        AssignSounds(gameAudio, scanner);

        var pos = new GameObject("POSController").AddComponent<POSController>();
        pos.transform.SetParent(systems, false);
        pos.Scanner = scanner;
        pos.Audio = gameAudio;

        // ---------- Bag ----------
        // An open plastic bag standing on the counter. Its film is drawn by P3StoreDressing (P3Meshes.PlasticBag); here is
        // what it does. Only its foot is solid: unseen walls keep what is dropped in from sliding out under the film,
        // and above them the film gives way to a hand or a product, as film does.
        var bagCenter = new Vector3(-0.4f, CounterTop, 0.56f);
        var bagRoot = new GameObject("Bag").transform;
        bagRoot.position = bagCenter;
        // As high as the film's rim at its lowest: what is let go lying across the mouth rests on the rim.
        const float bagWidth = 0.29f, bagDepth = 0.155f, wallHeight = 0.17f, wall = 0.008f;
        const float bagMouth = 0.22f, bagReach = 0.38f;
        foreach (var (wallName, wallOffset, wallSize) in new[]
        {
            ("Bag Wall L", new Vector3(-bagWidth * 0.5f, 0f, 0f), new Vector3(wall, wallHeight, bagDepth)),
            ("Bag Wall R", new Vector3(bagWidth * 0.5f, 0f, 0f), new Vector3(wall, wallHeight, bagDepth)),
            ("Bag Wall F", new Vector3(0f, 0f, -bagDepth * 0.5f), new Vector3(bagWidth, wallHeight, wall)),
            ("Bag Wall B", new Vector3(0f, 0f, bagDepth * 0.5f), new Vector3(bagWidth, wallHeight, wall)),
        })
        {
            var bagWall = new GameObject(wallName);
            bagWall.transform.SetParent(bagRoot, false);
            bagWall.transform.position = bagCenter + wallOffset + Vector3.up * (wallHeight * 0.5f);
            bagWall.AddComponent<BoxCollider>().size = wallSize;
        }
        var bagZoneObject = new GameObject("BagZone") { layer = LayerMask.NameToLayer("Zone") };
        bagZoneObject.transform.SetParent(bagRoot, false);
        // At the bag's mouth, where the tutorial's arrow points.
        bagZoneObject.transform.position = bagCenter + Vector3.up * bagMouth;
        var bagCollider = bagZoneObject.AddComponent<BoxCollider>();
        bagCollider.isTrigger = true;
        // From the counter to well above the rim: a product let go lying across the mouth is in the bag too.
        bagCollider.size = new Vector3(0.32f, bagReach, 0.2f);
        bagCollider.center = new Vector3(0f, bagReach * 0.5f - bagMouth, 0f);
        var bagZone = bagZoneObject.AddComponent<BagZone>();
        bagZone.Pos = pos;
        bagZone.Audio = gameAudio;
        // A few millimetres up: the film's floor lies on the counter.
        bagZone.Floor = Marker("Bag Floor", bagCenter + Vector3.up * 0.004f, Quaternion.identity, bagRoot);
        bagZone.Inside = new Vector2(0.26f, 0.13f);
        // No name tag (the other stations have one in the tutorials): a bag that looks like a bag does not need one, and
        // above the handles it sat right behind the tutorial arrow's own words.

        // ---------- Payment ----------
        // The card slot is a real opening in the face toward the clerk, so a card physically goes in. On Quest 2 a plain box
        // with an invisible zone above it left the wearer guessing where and which way the card went.
        var terminal = new Vector3(0.82f, CounterTop, 0.52f);
        const float terminalWidth = 0.16f, terminalHeight = 0.08f, terminalDepth = 0.2f;
        const float slotWidth = 0.09f, slotGap = 0.02f, slotDepth = 0.07f, slotBottom = 0.015f, frameBar = 0.006f;
        float face = terminal.z - terminalDepth * 0.5f;
        float slotTop = slotBottom + slotGap;
        float slotMiddle = slotBottom + slotGap * 0.5f;
        float sideWidth = (terminalWidth - slotWidth) * 0.5f;
        float backDepth = terminalDepth - slotDepth;
        var terminalScreen = Material("P2_TerminalScreen", new Color(0.35f, 0.5f, 0.55f));
        var frameIdle = Material("P2_CardSlotFrame", new Color(0.95f, 0.78f, 0.2f));
        var frameLit = Material("P2_CardSlotFrameLit", new Color(0.25f, 0.85f, 0.35f));
        var terminalRoot = new GameObject("Card Terminal").transform;
        terminalRoot.SetParent(environment, false);
        terminalRoot.position = terminal + Vector3.up * 0.16f;
        var terminalBody = new[]
        {
            Box("Terminal Base", terminal + Vector3.up * (slotBottom * 0.5f), new Vector3(terminalWidth, slotBottom, terminalDepth), dark, terminalRoot, true),
            Box("Terminal Top", terminal + Vector3.up * ((slotTop + terminalHeight) * 0.5f), new Vector3(terminalWidth, terminalHeight - slotTop, terminalDepth), dark, terminalRoot, true),
            Box("Terminal Side L", terminal + new Vector3(-(slotWidth + sideWidth) * 0.5f, slotMiddle, 0f), new Vector3(sideWidth, slotGap, terminalDepth), dark, terminalRoot, true),
            Box("Terminal Side R", terminal + new Vector3((slotWidth + sideWidth) * 0.5f, slotMiddle, 0f), new Vector3(sideWidth, slotGap, terminalDepth), dark, terminalRoot, true),
            Box("Terminal Slot Back", new Vector3(terminal.x, CounterTop + slotMiddle, face + slotDepth + backDepth * 0.5f), new Vector3(slotWidth, slotGap, backDepth), dark, terminalRoot, true),
        };
        Box("Terminal Screen", terminal + new Vector3(0f, terminalHeight + 0.0015f, 0.03f), new Vector3(0.12f, 0.003f, 0.09f), terminalScreen, terminalRoot, false);
        float frameZ = face - 0.002f;
        var slotFrame = new[]
        {
            Box("Slot Frame Top", new Vector3(terminal.x, CounterTop + slotTop + frameBar * 0.5f, frameZ), new Vector3(slotWidth + 2f * frameBar, frameBar, 0.004f), frameIdle, terminalRoot, false),
            Box("Slot Frame Bottom", new Vector3(terminal.x, CounterTop + slotBottom - frameBar * 0.5f, frameZ), new Vector3(slotWidth + 2f * frameBar, frameBar, 0.004f), frameIdle, terminalRoot, false),
            Box("Slot Frame L", new Vector3(terminal.x - (slotWidth + frameBar) * 0.5f, CounterTop + slotMiddle, frameZ), new Vector3(frameBar, slotGap, 0.004f), frameIdle, terminalRoot, false),
            Box("Slot Frame R", new Vector3(terminal.x + (slotWidth + frameBar) * 0.5f, CounterTop + slotMiddle, frameZ), new Vector3(frameBar, slotGap, 0.004f), frameIdle, terminalRoot, false),
        };

        var cardObject = new GameObject("CardSlotZone") { layer = LayerMask.NameToLayer("Zone") };
        // Inside the opening only, from 5 mm in: the card counts once it has gone in, not while it is held in front of the slot.
        const float zoneDepth = 0.06f;
        cardObject.transform.position = new Vector3(terminal.x, CounterTop + slotMiddle, face + 0.005f + zoneDepth * 0.5f);
        var cardCollider = cardObject.AddComponent<BoxCollider>();
        cardCollider.isTrigger = true;
        cardCollider.size = new Vector3(slotWidth, slotGap, zoneDepth);
        // Half in, half out, the way a card sits in a reader. The half inside is hidden by the terminal's top.
        var guide = Box("Card Guide", new Vector3(terminal.x, CounterTop + slotMiddle, face), PaymentDesk.CardSize,
            TransparentMaterial("P2_CardGuide", new Color(0.35f, 0.6f, 1f, 0.4f)), cardObject.transform, false);
        guide.SetActive(false);

        // Catches the card at the opening and seats it in the guide's pose, held or let go.
        var socketObject = new GameObject("Card Slot Socket") { layer = LayerMask.NameToLayer("Zone") };
        socketObject.transform.SetParent(terminalRoot, false);
        socketObject.transform.position = new Vector3(terminal.x, CounterTop + slotMiddle, face - 0.03f);
        var socketCollider = socketObject.AddComponent<BoxCollider>();
        socketCollider.isTrigger = true;
        socketCollider.size = new Vector3(0.14f, 0.08f, 0.1f);
        var seat = new GameObject("Card Seat").transform;
        seat.SetParent(socketObject.transform, false);
        seat.SetPositionAndRotation(guide.transform.position, guide.transform.rotation);
        var socket = socketObject.AddComponent<CardSlotSocket>();
        socket.attachTransform = seat;
        socket.hoverSocketSnapping = true;
        socket.socketSnappingRadius = CardSnapMeters;
        socket.showInteractableHoverMeshes = false;
        socket.TerminalBody = Array.ConvertAll(terminalBody, part => part.GetComponent<Collider>());

        var cardSlot = cardObject.AddComponent<CardSlotZone>();
        cardSlot.Audio = gameAudio;
        cardSlot.Guide = guide;
        cardSlot.Frame = Array.ConvertAll(slotFrame, part => part.GetComponent<Renderer>());
        cardSlot.FrameIdle = frameIdle;
        cardSlot.FrameLit = frameLit;
        AddLabel(terminalRoot, "카드 단말기");

        var register = BuildCashRegister(environment, gameAudio, out var drawer);

        var trayCenter = new Vector3(-0.3f, CounterTop, 0.84f);
        // Solid (2026-11-13): without a collider a note lay on the counter under the tray's 4 mm, and was not seen.
        Box("Change Tray", trayCenter + Vector3.up * 0.002f, new Vector3(0.3f, 0.004f, 0.2f), shelf, environment, true);
        var trayObject = new GameObject("ChangeTrayZone") { layer = LayerMask.NameToLayer("Zone") };
        trayObject.transform.position = trayCenter + Vector3.up * 0.07f;
        var trayCollider = trayObject.AddComponent<BoxCollider>();
        trayCollider.isTrigger = true;
        trayCollider.size = new Vector3(0.3f, 0.14f, 0.2f);
        var tray = trayObject.AddComponent<ChangeTrayZone>();
        tray.Audio = gameAudio;
        AddLabel(trayObject.transform, "거스름돈 접시");

        // To the right of the scanner as the clerk sees it, not behind it (2026-11-12): the scanner's upright part stands in
        // the middle of the counter, 30 cm high, and from the clerk's place it hides the counter behind it about 12 cm to
        // either side of the middle. The plate used to begin 2 cm from the middle, and the card on it was half hidden.
        var plate = new Vector3(0.22f, CounterTop, 0.84f);
        Box("Customer Plate", plate + Vector3.up * 0.002f, new Vector3(0.24f, 0.004f, 0.18f), shelf, environment, true);
        var plateMark = new GameObject("Customer Plate Anchor").transform;
        plateMark.position = plate;
        AddLabel(plateMark, "손님 접시");

        var desk = new GameObject("PaymentDesk").AddComponent<PaymentDesk>();
        desk.transform.SetParent(systems, false);
        desk.CardSlot = cardSlot;
        desk.Drawer = drawer;
        desk.Tray = tray;
        desk.Audio = gameAudio;
        desk.CustomerPlate = plateMark;
        desk.Register = register;
        desk.CardMaterial = Material("P2_Card", new Color(0.25f, 0.45f, 0.85f));
        desk.NoteMesh = P3Meshes.Note();
        desk.CoinMesh = P3Meshes.Coin();
        desk.Cash = CashLooks();

        // ---------- Customers (doc 2.3 Customers/, 6.1, D12), tutorial, game ----------
        // Where a customer sets the goods down, straight on the counter top: between the scanner and the card terminal.
        // The row begins far enough to the right that the first thing, standing, does not hide the plate behind it.
        var goods = Marker("Goods Anchor", new Vector3(0.42f, CounterTop, 0.68f));
        var directorPosition = new Vector3(0.62f, 1.32f, 0.98f);
        var director = new GameObject("CustomerDirector").AddComponent<CustomerDirector>();
        director.transform.SetPositionAndRotation(directorPosition, Facing(directorPosition));
        director.Pos = pos;
        director.Desk = desk;
        director.Audio = gameAudio;
        director.GoodsAnchor = goods;
        // Marker forward is the way a customer faces there: toward the clerk at the counter and in line, toward the shelf when browsing.
        var customers = new GameObject("Customers").transform;
        var towardClerk = Quaternion.Euler(0f, 180f, 0f);
        var towardFront = Quaternion.identity;
        var towardFarWall = Quaternion.Euler(0f, -90f, 0f);
        director.Door = Marker("Door", new Vector3(DoorCenter.x, 0f, StorefrontZ - VestibuleDepth + 0.4f), towardFront, customers);
        // In front of the plate and the goods, beside the scanner: straight behind the scanner the customer was hidden too.
        director.Counter = Marker("Counter Front", new Vector3(0.2f, 0f, 1.35f), towardClerk, customers);
        // The line stands along the counter on the side away from the door, so leaving customers do not cut through it. Not
        // straight back toward the gondolas: a line there walls the store in two (2026-11-08 layout, customers stuck).
        director.QueueSlots = new[]
        {
            Marker("Queue 1", new Vector3(0.85f, 0f, 1.5f), towardClerk, customers),
            Marker("Queue 2", new Vector3(1.7f, 0f, 1.65f), towardClerk, customers),
        };
        director.BrowseSpots = new[]
        {
            // In front of the row, at the fridges and at the chilled cases. Not in the 1 m aisle behind the row: a customer
            // standing there is a carved obstacle and closes the aisle (2026-11-08, two warps in the day 1 and 2 runs).
            Marker("Browse Shelf A", new Vector3(ShelfAX, 0f, 2.45f), towardFront, customers),
            Marker("Browse Fridges", new Vector3(-4.5f, 0f, 1.75f), towardFarWall, customers),
            Marker("Browse Shelf B", new Vector3(ShelfBX, 0f, 2.45f), towardFront, customers),
            Marker("Browse Chilled Cases", new Vector3(2.1f, 0f, 3.98f), towardFront, customers),
        };
        director.Characters = characters;
        var outcomePosition = new Vector3(0f, 1.28f, 0.98f);
        director.OutcomeAnchor = Marker("Outcome Anchor", outcomePosition, Facing(outcomePosition), customers);
        director.BodyMaterial = Material("P2_CustomerBody", new Color(0.35f, 0.5f, 0.65f));
        // Past the counter's far end, where the door and the clerk can both see her and customers still get by. Facing the
        // street front, halfway between the two places she looks at, so her head turns to each without going past its limit.
        var managerSpot = Marker("Manager Spot", new Vector3(-1.7f, 0f, 1.55f), Quaternion.LookRotation(new Vector3(0.15f, 0f, -1f)), customers);

        // The intake furniture (stands, bins, cart) goes in first: the NavMesh has to know the stands are solid.
        var intake = BuildIntake(catalog, gameAudio, environment, systems);
        // P3-K1 looks (textures, ceiling, fridges, stock shelf) before the bake: the fridges and the shelf are solid.
        P3StoreDressing.Dress(environment);
        BuildCookingZones(pos, gameAudio, environment);
        var navMesh = BakeNavMesh(environment);
        CheckNavMesh(navMesh, director, managerSpot);
        Directory.CreateDirectory(Path.GetDirectoryName(NavMeshPath));
        AssetDatabase.CreateAsset(navMesh, NavMeshPath);
        var navObject = new GameObject("Store NavMesh").AddComponent<StoreNavMesh>();
        navObject.transform.SetParent(environment, false);
        navObject.Data = navMesh;

        var screenPosition = new Vector3(-0.62f, 1.32f, 0.98f);
        var screen = new GameObject("POSScreen").AddComponent<POSScreen>();
        screen.transform.SetPositionAndRotation(screenPosition, Facing(screenPosition));
        screen.Pos = pos;
        screen.Desk = desk;

        var tutorial = new GameObject("TutorialController").AddComponent<TutorialController>();
        tutorial.transform.SetParent(systems, false);
        tutorial.Catalog = catalog;
        tutorial.Scanner = scanner;
        tutorial.Bag = bagZone;
        tutorial.Desk = desk;
        tutorial.Director = director;

        // What shows the player where and how (2026-11-09): an arrow over the next thing, and a line at the controllers.
        var pointer = new GameObject("Target Pointer").AddComponent<TargetPointer>();
        pointer.transform.SetParent(systems, false);
        pointer.Material = Material("P2_PointerYellow", new Color(1f, 0.8f, 0.08f));
        var hands = BuildControllerHint(systems);
        tutorial.Pointer = pointer;
        tutorial.Hands = hands;

        var manager = new GameObject("Store Manager Npc").AddComponent<ManagerNpc>();
        manager.transform.SetParent(systems, false);
        // Her lines are in the female TTS voice.
        // Straight from the prefab, as for the slots: the references the setup returned can go stale when it imports assets.
        manager.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Customers/Female_Adult_17.prefab")?.GetComponent<CustomerCharacter>() ?? throw new Exception("Manager's body missing.");
        manager.Spot = managerSpot;
        manager.Director = director;
        manager.Audio = gameAudio;
        manager.ApronMaterial = Material("P2_ManagerApron", new Color(0.03f, 0.16f, 0.36f));
        tutorial.Manager = manager;

        var boardPosition = new Vector3(0f, 1.95f, 1.3f);
        var boardAnchor = new GameObject("Board Anchor").transform;
        boardAnchor.SetPositionAndRotation(boardPosition, Facing(boardPosition));
        var game = new GameObject("GameManager").AddComponent<GameManager>();
        game.transform.SetParent(systems, false);
        game.Scenario = ImportScenario(catalog);
        game.Catalog = catalog;
        game.Pos = pos;
        game.Director = director;
        game.Tutorial = tutorial;
        game.BoardAnchor = boardAnchor;
        game.Manager = manager;

        game.MoreScenarios = new TextAsset[0];
        game.LastDay = ScenarioDays;
        game.Intake = intake;
        var preview = new GameObject("PlacementPreview").AddComponent<PlacementPreview>();
        preview.transform.SetParent(systems, false);
        preview.Intake = intake;
        var intakeTutorial = new GameObject("IntakeTutorial").AddComponent<IntakeTutorial>();
        intakeTutorial.transform.SetParent(systems, false);
        intakeTutorial.Intake = intake;
        intakeTutorial.Preview = preview;
        game.IntakeTutorial = intakeTutorial;
        game.Title = BuildTitle(desk, gameAudio, systems);
        game.Title.Pointer = pointer;
        game.Title.Hands = hands;
        game.Title.Manager = manager;
        BuildWristWatch(game, director);
        game.Unlocks = ImportUnlocks(intake);
        game.TrainingScenario = ImportScenario(catalog, TrainingScenarioSource, TrainingScenarioPath);
        if (!File.Exists(TrainingDeliveriesSource)) throw new Exception("Deliveries missing: " + TrainingDeliveriesSource);
        File.Copy(TrainingDeliveriesSource, TrainingDeliveriesPath, true);
        AssetDatabase.ImportAsset(TrainingDeliveriesPath);
        game.TrainingDeliveries = AssetDatabase.LoadAssetAtPath<TextAsset>(TrainingDeliveriesPath) ?? throw new Exception("Deliveries missing: " + TrainingDeliveriesPath);
        game.Tablet = BuildOrderTerminal(environment, systems);

        var verification = new GameObject("P2 Day Verification").AddComponent<P2DayVerification>();
        verification.Intake = intake;
        verification.Preview = preview;
        verification.IntakeTutorial = intakeTutorial;
        verification.Disposal = FindInScene<DisposalBinZone>();
        verification.RecycleBin = FindInScene<BoxRecycleZone>();
        verification.Game = game;
        verification.Title = game.Title;
        verification.Screen = screen;
        verification.Scanner = scanner;
        verification.Bag = bagZone;
        verification.Desk = desk;
        new GameObject("P2 Locomotion Verification").AddComponent<P2LocomotionVerification>();
        var growth = new GameObject("P2 Growth Verification").AddComponent<P2GrowthVerification>();
        growth.Game = game;
        growth.Intake = intake;
        // P3 counter devices and screens: looks only, after everything they sit around exists.
        P3StoreDressing.DressCounter(environment, new Vector3(-0.62f, 1.32f, 0.98f), new Vector3(0.62f, 1.32f, 0.98f), EyePosition);
        // Printed and painted details, looks only, after the NavMesh bake: nothing here has a collider.
        P3StoreDetails.Apply(environment);

        if (!EditorSceneManager.SaveScene(scene, StoreScene)) throw new Exception("Scene save failed.");
        // The light is baked into the saved scene (-no-bake leaves the old even light).
        P3StoreLighting.Bake(scene, environment);
    }

    static Quaternion Facing(Vector3 position) => Quaternion.LookRotation(position - EyePosition);

    // ---------- The way in (game design 10.0, 2026-11-09) ----------

    // On the pavement, a metre from the closed door, which is where the title is read. The pavement is 1.5 m deep and the door
    // 2.4 m wide; the leaves slide along the glass either side of it and the title panel is on them.
    static readonly Vector3 TitleStand = new Vector3(DoorCenter.x, 0f, StorefrontZ - 1.05f);
    // In at the door and along the inside of the street front, past the counter's far end, to the clerk's start.
    public static readonly Vector3[] CommuteRoute = { new Vector3(DoorCenter.x, 0f, StorefrontZ + 0.6f), new Vector3(-2f, 0f, -0.3f), new Vector3(-0.45f, 0f, 0f) };

    /// <summary>
    /// The door, the title's and guide's places, and the floor arrows. All of it is made after the NavMesh bake and left
    /// switched off in the scene, so customers' routes and every check that runs without the title see the store as before.
    /// </summary>
    static TitleFlow BuildTitle(PaymentDesk desk, GameAudio audio, Transform systems)
    {
        var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>() ?? throw new Exception("XR Origin missing.");
        var root = new GameObject("Title").transform;
        var title = root.gameObject.AddComponent<TitleFlow>();
        title.transform.SetParent(systems, false);
        title.Rig = origin;
        title.Move = origin.GetComponentInChildren<DynamicMoveProvider>(true);
        title.Vignette = origin.GetComponentInChildren<TunnelingVignetteController>(true);
        title.Desk = desk;
        title.Audio = audio;
        var towardStore = Quaternion.LookRotation(Vector3.forward);
        title.StandSpot = Marker("Title Stand", TitleStand, towardStore, root);
        // Below eye level, so the store and the people in it are seen over the top of it.
        title.PanelAnchor = Marker("Title Panel Anchor", new Vector3(DoorCenter.x, 1.14f, StorefrontZ - 0.05f), towardStore, root);
        var guidePosition = new Vector3(-2.6f, 1.9f, 0.15f);
        title.GuideAnchor = Marker("Commute Guide Anchor", guidePosition, Quaternion.LookRotation(guidePosition - new Vector3(DoorCenter.x, 1.5f, DoorCenter.z)), root);
        title.CounterSpot = Marker("Counter Spot", Vector3.zero, Quaternion.identity, root);

        // Glass in aluminium, like the street front it stands in: the first thing seen is the store through it, lit and open.
        var doorGlass = TransparentMaterial("P2_StoreGlass", new Color(0.72f, 0.84f, 0.9f, 0.16f));
        var doorFrame = Material("P2_TitleDoorFrame", new Color(0.72f, 0.74f, 0.76f));
        title.DoorLeaves = new[]
        {
            GlassLeaf("Title Door Near", DoorCenter.x - DoorWidth * 0.25f - 0.002f, 1f, doorGlass, doorFrame, root),
            GlassLeaf("Title Door Far", DoorCenter.x + DoorWidth * 0.25f + 0.002f, -1f, doorGlass, doorFrame, root),
        };
        foreach (var leaf in title.DoorLeaves) leaf.gameObject.SetActive(false);
        title.LeafSlide = new Vector3(DoorWidth * 0.5f - 0.02f, 0f, 0f);

        var path = new GameObject("Commute Path").transform;
        path.SetParent(root, false);
        var yellow = UnlitMaterial("P2_OutlineYellow", new Color(1f, 0.85f, 0.1f));
        var green = UnlitMaterial("P2_OutlineGreen", new Color(0.2f, 0.95f, 0.3f));
        for (int i = 1; i < CommuteRoute.Length; i++)
        {
            var from = CommuteRoute[i - 1];
            var along = CommuteRoute[i] - from;
            int chevrons = Mathf.Max(1, Mathf.RoundToInt(along.magnitude / 0.55f));
            for (int c = 0; c < chevrons; c++)
                Chevron(path, from + along * ((c + 0.5f) / chevrons), along.normalized, yellow);
        }
        const float ringNear = -0.55f, ringFar = 0.25f, ringHalf = 0.5f, bar = 0.05f, lift = 0.008f;
        Box("Ring Near", new Vector3(0f, lift, ringNear), new Vector3(ringHalf * 2f, 0.004f, bar), green, path, false);
        Box("Ring Far", new Vector3(0f, lift, ringFar), new Vector3(ringHalf * 2f, 0.004f, bar), green, path, false);
        Box("Ring L", new Vector3(-ringHalf, lift, (ringNear + ringFar) * 0.5f), new Vector3(bar, 0.004f, ringFar - ringNear), green, path, false);
        Box("Ring R", new Vector3(ringHalf, lift, (ringNear + ringFar) * 0.5f), new Vector3(bar, 0.004f, ringFar - ringNear), green, path, false);
        path.gameObject.SetActive(false);
        title.CommutePath = path.gameObject;
        return title;
    }

    /// <summary>One sliding leaf: an empty at its centre holding the glass (the solid part) and the frame.</summary>
    /// <param name="meetingSide">+1 when the side where the two leaves meet is toward +x.</param>
    static Transform GlassLeaf(string name, float centerX, float meetingSide, Material glass, Material frame, Transform parent)
    {
        const float stile = 0.06f, rail = 0.08f, depth = 0.05f;
        float width = DoorWidth * 0.5f, y = DoorHeight * 0.5f, z = StorefrontZ;
        var leaf = new GameObject(name).transform;
        leaf.SetParent(parent, false);
        leaf.position = new Vector3(centerX, y, z);
        Box("Door Glass", leaf.position, new Vector3(width, DoorHeight, 0.02f), glass, leaf, true);
        foreach (float side in new[] { -1f, 1f })
            Box("Door Stile", new Vector3(centerX + side * (width * 0.5f - stile * 0.5f), y, z), new Vector3(stile, DoorHeight, depth), frame, leaf, false);
        Box("Door Rail Top", new Vector3(centerX, DoorHeight - rail * 0.5f, z), new Vector3(width, rail, depth), frame, leaf, false);
        Box("Door Rail Bottom", new Vector3(centerX, rail * 0.5f, z), new Vector3(width, rail, depth), frame, leaf, false);
        // Above the title sign, which covers the middle of the door at waist height.
        Box("Door Handle", new Vector3(centerX + meetingSide * (width * 0.5f - stile - 0.04f), 1.65f, z - 0.05f), new Vector3(0.025f, 0.46f, 0.025f), frame, leaf, false);
        return leaf;
    }

    /// <summary>A line on each controller while a control is new. Attached like the wrist watch.</summary>
    static ControllerHint BuildControllerHint(Transform systems)
    {
        var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>() ?? throw new Exception("XR Origin missing.");
        var hint = new GameObject("Controller Hint").AddComponent<ControllerHint>();
        hint.transform.SetParent(systems, false);
        hint.LeftHand = origin.transform.Find("Camera Offset/Left Controller") ?? throw new Exception("Left Controller missing.");
        hint.RightHand = origin.transform.Find("Camera Offset/Right Controller") ?? throw new Exception("Right Controller missing.");
        return hint;
    }

    /// <summary>A flat arrowhead on the floor, pointing along <paramref name="direction"/>. Opaque, so it never blends with splats.</summary>
    static void Chevron(Transform parent, Vector3 center, Vector3 direction, Material material)
    {
        const float reach = 0.15f, length = 0.21f, width = 0.05f, lift = 0.008f;
        var facing = Quaternion.LookRotation(direction, Vector3.up);
        foreach (float side in new[] { -1f, 1f })
        {
            var arm = Box("Chevron Arm", center + Vector3.up * lift + facing * new Vector3(side * reach * 0.5f, 0f, -reach * 0.5f), new Vector3(width, 0.004f, length), material, parent, false);
            arm.transform.rotation = facing * Quaternion.Euler(0f, -side * 45f, 0f);
        }
    }

    /// <summary>On the back of the left hand, tilted toward the eyes. The canvas reads from behind its forward; its top is toward the fingers.</summary>
    static void BuildWristWatch(GameManager game, CustomerDirector director)
    {
        var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>() ?? throw new Exception("XR Origin missing.");
        var left = origin.transform.Find("Camera Offset/Left Controller") ?? throw new Exception("Left Controller missing.");
        var watch = new GameObject("Wrist Watch").AddComponent<WristWatch>();
        watch.transform.SetParent(left, false);
        watch.transform.localPosition = new Vector3(0f, 0.04f, -0.1f);
        watch.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, -0.87f, 0.5f), new Vector3(0f, 0.5f, 0.87f));
        watch.Game = game;
        watch.Director = director;
    }

    // ---------- The till and its money (doc 5.5, 2026-11-10) ----------

    /// <summary>
    /// What the rules need of the till: the solid body, the part that slides, a place in the drawer for the piece on top of
    /// each note slot and coin cup, and the space of the open drawer. P3StoreDressing.CashRegisterLooks draws it.
    /// </summary>
    static CashRegister BuildCashRegister(Transform environment, GameAudio audio, out CashDrawerZone zone)
    {
        var origin = new Vector3(RegisterX, CounterTop, RegisterFront);
        var root = new GameObject("Cash Register");
        root.transform.SetParent(environment, false);
        root.transform.position = origin;
        // Solid, so a card or a coin let go over it lies on top of it. It stands wholly on the counter, which customers
        // cannot walk through anyway.
        var solid = root.AddComponent<BoxCollider>();
        solid.center = new Vector3(0f, RegisterHeight * 0.5f, RegisterDepth * 0.5f);
        solid.size = new Vector3(RegisterWidth, RegisterHeight, RegisterDepth);

        var drawer = new GameObject("Cash Drawer").transform;
        drawer.SetParent(root.transform, false);
        var register = root.AddComponent<CashRegister>();
        register.Drawer = drawer;
        register.Travel = DrawerTravel;
        register.Audio = audio;

        var slots = new List<CashRegister.Slot>();
        // Notes lie with their long side running into the drawer, as in any till, and with the top of their print toward
        // the far end of the counter: the clerk stands to the right of the till and reads them from there.
        for (int i = 0; i < NoteValues.Length; i++)
            slots.Add(new CashRegister.Slot
            {
                Value = NoteValues[i],
                Anchor = Marker($"Slot {NoteValues[i]}", origin + new Vector3(TillColumn(i), TillFloor + NoteStack + PaymentDesk.NoteBody * 0.5f, (NoteFront + NoteBack) * 0.5f), Quaternion.Euler(0f, -90f, 0f), drawer),
            });
        for (int i = 0; i < CoinValues.Length; i++)
            slots.Add(new CashRegister.Slot
            {
                Value = CoinValues[i],
                Anchor = Marker($"Slot {CoinValues[i]}", origin + new Vector3(TillColumn(i), TillFloor + CoinHeap + PaymentDesk.CoinBody * 0.5f, (CoinFront + CoinBack) * 0.5f), Quaternion.identity, drawer),
            });
        register.Slots = slots.ToArray();

        // Where the drawer is when it is out, from the counter top to a hand's height above the money. With the drawer
        // shut, this is the strip of counter in front of the till: a note laid there too early is noticed and sent back.
        var zoneObject = new GameObject("CashDrawerZone") { layer = LayerMask.NameToLayer("Zone") };
        zoneObject.transform.position = origin + new Vector3(0f, 0.1f, -DrawerTravel * 0.5f);
        var zoneCollider = zoneObject.AddComponent<BoxCollider>();
        zoneCollider.isTrigger = true;
        zoneCollider.size = new Vector3(RegisterWidth - 0.02f, 0.2f, DrawerTravel);
        zone = zoneObject.AddComponent<CashDrawerZone>();
        // The name tag stands on the till's front edge, clear of the arrow that points into the open drawer.
        AddLabel(Marker("Till Label Anchor", origin + new Vector3(0.06f, 0.01f, -0.07f), Quaternion.identity, root.transform), "현금 서랍");
        return register;
    }

    /// <summary>The printed faces of the notes and coins (scripts/windows/make_cash_assets.py), one material each.</summary>
    static PaymentDesk.CashLook[] CashLooks()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { CashArt }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
            // Money lies flat and is looked at from a slant.
            if (importer.anisoLevel == 8 && importer.wrapMode == TextureWrapMode.Clamp && importer.maxTextureSize == 1024) continue;
            importer.anisoLevel = 8;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }
        var looks = new List<PaymentDesk.CashLook>();
        foreach (int value in NoteValues)
            looks.Add(new PaymentDesk.CashLook
            {
                Value = value,
                Size = new Vector3(NoteLength(value), NoteThickness, NoteWidth),
                Material = CashMaterial($"P2_Note{value}", $"{CashArt}/note_{value}.png", 0.18f, 0f),
            });
        foreach (int value in CoinValues)
            looks.Add(new PaymentDesk.CashLook
            {
                Value = value,
                Coin = true,
                Size = new Vector3(CoinDiameter(value), CoinThickness, CoinDiameter(value)),
                Material = CashMaterial($"P2_Coin{value}", $"{CashArt}/coin_{value}.png", 0.62f, 0.55f),
            });
        return looks.ToArray();
    }

    static Material CashMaterial(string name, string texturePath, float smoothness, float metallic)
    {
        var material = Material(name, Color.white);
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) ?? throw new Exception("Cash texture missing: " + texturePath));
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", metallic);
        EditorUtility.SetDirty(material);
        return material;
    }

    // P3-K3 sounds, made by scripts/windows/make_game_sounds.py (effects synthesized, voices from the Windows Korean TTS).
    const string SoundFolder = "Assets/Audio/Generated";

    static AudioClip Sound(string name)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{SoundFolder}/{name}.wav");
        if (clip == null) throw new Exception($"Sound missing: {SoundFolder}/{name}.wav");
        return clip;
    }

    static CustomerVoice Voice(string gender) => new CustomerVoice
    {
        Greet = new[] { Sound($"voice_greet_1_{gender}"), Sound($"voice_greet_2_{gender}"), Sound($"voice_greet_3_{gender}"), Sound($"voice_greet_4_{gender}") },
        Bag = Sound($"voice_bag_{gender}"),
        Thanks = Sound($"voice_thanks_{gender}"),
        ThanksFast = Sound($"voice_thanks_fast_{gender}"),
        Waited = Sound($"voice_waited_{gender}"),
        Hurry = Sound($"voice_hurry_{gender}"),
        Leave = Sound($"voice_leave_{gender}"),
        Microwave = Sound($"voice_special_microwave_{gender}"),
        HotWater = Sound($"voice_special_hotwater_{gender}"),
        OnePlus = Sound($"voice_special_oneplus_{gender}"),
    };

    const string StoreMusicPath = "Assets/ThirdParty/StoreAssets/OpenGameArt/shop_theme/shop_theme.ogg";

    /// <summary>
    /// What plays once the store has bought its music (docs/p2/해금_성장_기획_2026-11-10.md 5): a CC0 loop of 88 s
    /// (Assets/ThirdParty/StoreAssets/README.md). Kept compressed in memory, as a minute and a half of stereo is some 15 MB
    /// decompressed, and loaded in the background so the scene does not wait for it.
    /// </summary>
    static AudioClip StoreMusic()
    {
        var importer = AssetImporter.GetAtPath(StoreMusicPath) as AudioImporter ?? throw new Exception($"Store music missing: {StoreMusicPath}");
        var settings = importer.defaultSampleSettings;
        if (settings.loadType != AudioClipLoadType.CompressedInMemory || settings.compressionFormat != AudioCompressionFormat.Vorbis || !importer.loadInBackground)
        {
            settings.loadType = AudioClipLoadType.CompressedInMemory;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.6f;
            importer.defaultSampleSettings = settings;
            importer.loadInBackground = true;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<AudioClip>(StoreMusicPath) ?? throw new Exception($"Store music did not import: {StoreMusicPath}");
    }

    static AudioClip[] Lines(string stem, int count)
    {
        var clips = new AudioClip[count];
        for (int i = 0; i < count; i++) clips[i] = Sound($"{stem}_{i + 1}");
        return clips;
    }

    static void AssignSounds(GameAudio audio, ScannerZone scanner)
    {
        scanner.BeepClip = Sound("sfx_scan");
        audio.ErrorClip = Sound("sfx_error");
        audio.ApprovedClip = Sound("sfx_card_approved");
        audio.DrawerClip = Sound("sfx_drawer");
        audio.DrawerOpenClip = Sound("sfx_drawer_open");
        audio.CoinClip = Sound("sfx_coin");
        audio.NoteClip = Sound("sfx_note");
        audio.BagClip = Sound("sfx_bag");
        audio.CancelClip = Sound("sfx_cancel");
        audio.BellClip = Sound("sfx_counter_bell");
        audio.DoorClip = Sound("sfx_door_chime");
        audio.HurryClip = Sound("sfx_hurry");
        audio.DeliveryClip = Sound("sfx_delivery");
        audio.Ambience = Sound("amb_store_loop");
        audio.Music = StoreMusic();
        audio.ThudClip = Sound("sfx_box_thud");
        audio.TapeClip = Sound("sfx_tape");
        audio.FlapClip = Sound("sfx_flap");
        audio.PlaceClip = Sound("sfx_place");
        audio.RecycleClip = Sound("sfx_box_recycle");
        audio.DisposeClip = Sound("sfx_dispose");
        audio.DingClip = Sound("sfx_microwave_ding");
        audio.TipClip = Sound("sfx_tip");
        PlayerHandSetup.AssignTouchSounds(audio);
        audio.Female = Voice("f");
        audio.Male = Voice("m");
        audio.TutorialLines = Lines("voice_tutorial", 6);
        // The fifth is the disposal step of the training (IntakeTutorial.TeachDisposal); the fixed Day 2 never asks for it.
        audio.IntakeLines = Lines("voice_intake", 5);
        audio.SpecialGuideLines = new[] { Sound("voice_guide_microwave"), Sound("voice_guide_hotwater"), Sound("voice_guide_oneplus") };
        audio.ManagerLines = new[]
        {
            Sound("voice_manager_welcome"), Sound("voice_manager_punch_in"), Sound("voice_manager_punch_out"),
            Sound("voice_ending_s"), Sound("voice_ending_a"), Sound("voice_ending_b"), Sound("voice_ending_c"),
            Sound("voice_manager_first_customer"), Sound("voice_manager_alone"), Sound("voice_manager_graduated"),
        };
    }

    /// <summary>
    /// Day 3's requests (doc 6.4): the rule zones in the microwave and under the hot water tap of the corner the dressing
    /// built, each with a one-line prompt above it, facing the aisle. The cabinet's own collider is the only solid part, so
    /// customers' routes and the NavMesh do not change.
    /// </summary>
    static void BuildCookingZones(POSController pos, GameAudio audio, Transform environment)
    {
        var cabinet = environment.Find("Store Dressing/Microwave Cabinet") ?? throw new Exception("Microwave Cabinet missing.");
        var towardWall = Quaternion.Euler(0f, 90f, 0f);
        // Built against the near wall with its parts at those coordinates, then the whole corner goes to the far wall below.

        var microwave = Zone<MicrowaveZone>("MicrowaveZone", cabinet, P3StoreDressing.MicrowaveCavity, P3StoreDressing.MicrowaveCavitySize);
        microwave.Pos = pos;
        microwave.Audio = audio;
        microwave.WorkingLoop = Sound("sfx_microwave_hum");
        microwave.Door = cabinet.Find("Microwave Door Hinge") ?? throw new Exception("Microwave door missing.");
        microwave.LitParts = (cabinet.Find("Microwave Lit") ?? throw new Exception("Microwave light missing.")).gameObject;
        microwave.PromptAnchor = Marker("Microwave Prompt", new Vector3(4.02f, 1.3f, P3StoreDressing.MicrowaveCavity.z), towardWall, cabinet);

        var dispenser = Zone<WaterDispenserZone>("WaterDispenserZone", cabinet, P3StoreDressing.UnderTap, P3StoreDressing.UnderTapSize);
        dispenser.Pos = pos;
        dispenser.Audio = audio;
        dispenser.WorkingLoop = Sound("sfx_water_pour");
        dispenser.Stream = (cabinet.Find("Dispenser Water") ?? throw new Exception("Dispenser water missing.")).gameObject;
        dispenser.PromptAnchor = Marker("Water Prompt", new Vector3(4.05f, 1.44f, P3StoreDressing.UnderTap.z), towardWall, cabinet);
        cabinet.SetPositionAndRotation(P3StoreDressing.CabinetPlace, Quaternion.Euler(0f, 180f, 0f));
    }

    /// <summary>Copies Data/scenarios.csv into Assets and fails the build if any row of any day is skipped or a day is empty.</summary>
    static TextAsset ImportScenario(ProductCatalog catalog, string source = ScenarioSourceDefault, string imported = ScenarioPathDefault)
    {
        if (!File.Exists(source)) throw new Exception("Scenario missing: " + source);
        var text = File.ReadAllText(source);
        // A fresh list straight from the prefabs: the catalog's references can be stale after the character setup's imports.
        _ = catalog;
        catalog = ScriptableObject.CreateInstance<ProductCatalog>();
        foreach (var path in Directory.GetFiles(ManifestFolder, "dev_*.json"))
        {
            if (path.EndsWith(".box_fit.json")) continue;
            var id = JsonUtility.FromJson<Manifest>(File.ReadAllText(path)).id;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{id}.prefab")?.GetComponent<ProductPrefab>();
            if (prefab != null) catalog.Products.Add(prefab);
        }
        var skipped = new List<string>();
        var counts = new List<string>();
        for (int day = 1; day <= ScenarioDays; day++)
        {
            var orders = DayScenarioLoader.Parse(text, day, catalog, day == 1 ? skipped : null);
            if (orders.Count == 0) throw new Exception($"{source} has no orders for day {day}.");
            counts.Add($"day {day}: {orders.Count}");
        }
        if (skipped.Count > 0) throw new Exception($"{source}: {skipped.Count} rows skipped:\n" + string.Join("\n", skipped));
        Directory.CreateDirectory(Path.GetDirectoryName(imported));
        File.WriteAllText(imported, text);
        AssetDatabase.ImportAsset(imported);
        Debug.Log($"[P2] Scenario imported from {source}: {string.Join(", ", counts)}");
        return AssetDatabase.LoadAssetAtPath<TextAsset>(imported) ?? throw new Exception("Scenario import failed: " + imported);
    }

    /// <summary>
    /// Copies Data/unlocks.csv into Assets and fails the build on a row that does not parse, or on a product whose shelf is
    /// not one of the display slots holding that product: ordering it in must fill a shelf that exists.
    /// </summary>
    static TextAsset ImportUnlocks(IntakeDirector intake)
    {
        if (!File.Exists(UnlocksSource)) throw new Exception("Unlocks missing: " + UnlocksSource);
        var text = File.ReadAllText(UnlocksSource);
        // A fresh list straight from the prefabs, as for the scenario.
        var catalog = ScriptableObject.CreateInstance<ProductCatalog>();
        foreach (var path in Directory.GetFiles(ManifestFolder, "dev_*.json"))
        {
            if (path.EndsWith(".box_fit.json")) continue;
            var id = JsonUtility.FromJson<Manifest>(File.ReadAllText(path)).id;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{id}.prefab")?.GetComponent<ProductPrefab>();
            if (prefab != null) catalog.Products.Add(prefab);
        }
        var skipped = new List<string>();
        var items = UnlockCatalog.Parse(text, catalog, skipped);
        if (skipped.Count > 0) throw new Exception($"{UnlocksSource}: {skipped.Count} rows skipped:\n" + string.Join("\n", skipped));
        int products = 0;
        foreach (var item in items)
        {
            if (item.Kind != UnlockKind.Product) continue;
            products++;
            var slot = Array.Find(intake.DecorSlots, s => s != null && s.SlotId == item.Slot);
            if (slot == null) throw new Exception($"{UnlocksSource}: {item.Id} names slot {item.Slot}, which is not a display slot.");
            if (slot.ProductId != item.Id) throw new Exception($"{UnlocksSource}: slot {item.Slot} holds {slot.ProductId}, not {item.Id}.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(UnlocksPath));
        File.WriteAllText(UnlocksPath, text);
        AssetDatabase.ImportAsset(UnlocksPath);
        Debug.Log($"[P2] Unlocks imported from {UnlocksSource}: {products} products, {items.Count - products} equipment");
        return AssetDatabase.LoadAssetAtPath<TextAsset>(UnlocksPath) ?? throw new Exception("Unlocks import failed: " + UnlocksPath);
    }

    /// <summary>
    /// The ordering terminal: a monitor on a stand at the end of the counter past the card terminal, turned to the clerk's
    /// place. It stands there all day with its screen off; OrderTablet lights the screen between days. Looks only, no
    /// colliders, like the other things on the counter's extension.
    /// </summary>
    static OrderTablet BuildOrderTerminal(Transform environment, Transform systems)
    {
        var screenCenter = new Vector3(1.25f, 1.27f, 0.8f);
        var facing = Facing(screenCenter);
        var go = new GameObject("Order Tablet");
        go.transform.SetParent(systems, false);
        go.transform.SetPositionAndRotation(screenCenter, facing);
        var tablet = go.AddComponent<OrderTablet>();

        var dark = Material("P2_OrderTerminal", new Color(0.05f, 0.055f, 0.065f));
        var root = new GameObject("Order Terminal").transform;
        root.SetParent(environment, false);
        float width = OrderTablet.Size.x * OrderTablet.MetersPerPixel, height = OrderTablet.Size.y * OrderTablet.MetersPerPixel;
        // The screen is read from the side its forward points away from, so the monitor's body is just beyond it.
        var body = Box("Order Terminal Monitor", screenCenter + facing * new Vector3(0f, 0f, 0.014f), new Vector3(width + 0.04f, height + 0.04f, 0.022f), dark, root, false);
        body.transform.rotation = facing;
        var away = Vector3.ProjectOnPlane(screenCenter - EyePosition, Vector3.up).normalized;
        var upright = Quaternion.LookRotation(away);
        var foot = new Vector3(screenCenter.x, 0f, screenCenter.z) + away * 0.06f;
        float neckTop = screenCenter.y - height * 0.25f;
        var neck = Box("Order Terminal Neck", foot + Vector3.up * (CounterTop + neckTop) * 0.5f, new Vector3(0.06f, neckTop - CounterTop, 0.03f), dark, root, false);
        neck.transform.rotation = upright;
        var stand = Box("Order Terminal Foot", foot + Vector3.up * (CounterTop + 0.008f), new Vector3(0.26f, 0.016f, 0.18f), dark, root, false);
        stand.transform.rotation = upright;
        return tablet;
    }

    static T FindInScene<T>() where T : UnityEngine.Object => UnityEngine.Object.FindFirstObjectByType<T>() ?? throw new Exception(typeof(T).Name + " missing.");

    /// <summary>A double-sided gondola: base, spine, end panels, three boards and a top, inside the old block's footprint.</summary>
    static void Gondola(string name, float centerX, float front, float back, Material material, Transform environment)
    {
        var root = new GameObject(name).transform;
        root.SetParent(environment, false);
        float z = (front + back) * 0.5f, depth = back - front;
        Box(name + " Base", new Vector3(centerX, 0.05f, z), new Vector3(ShelfWidth, 0.1f, depth), material, root, true);
        Box(name + " Spine", new Vector3(centerX, ShelfHeight * 0.5f, z), new Vector3(ShelfWidth, ShelfHeight, 0.03f), material, root, true);
        Box(name + " End L", new Vector3(centerX - ShelfWidth * 0.5f + 0.01f, ShelfHeight * 0.5f, z), new Vector3(0.02f, ShelfHeight, depth), material, root, true);
        Box(name + " End R", new Vector3(centerX + ShelfWidth * 0.5f - 0.01f, ShelfHeight * 0.5f, z), new Vector3(0.02f, ShelfHeight, depth), material, root, true);
        foreach (var top in ShelfBoardTops)
            Box($"{name} Board {top:0.00}", new Vector3(centerX, top - 0.01f, z), new Vector3(ShelfWidth, 0.02f, depth), material, root, true);
        Box(name + " Top", new Vector3(centerX, ShelfHeight - 0.01f, z), new Vector3(ShelfWidth, 0.02f, depth), material, root, true);
    }

    /// <summary>
    /// Day 2 intake (doc 5.7, 6.4): slots on the shelves' fronts, a stand with a cutter beside each shelf end, the delivery
    /// cart and the empty-box bin at the warehouse door behind the counter, the waste bin next to the counter, and guide
    /// panels by the shelves and the cart. The slot assignment is provisional (game design 19: 권도훈, 11/12).
    /// </summary>
    static IntakeDirector BuildIntake(ProductCatalog catalog, GameAudio audio, Transform environment, Transform systems)
    {
        var yellow = UnlitMaterial("P2_OutlineYellow", new Color(1f, 0.85f, 0.1f));
        var green = UnlitMaterial("P2_OutlineGreen", new Color(0.2f, 0.95f, 0.3f));
        var red = UnlitMaterial("P2_OutlineRed", new Color(0.95f, 0.15f, 0.12f));
        var cardboard = Material("P2_Cardboard", new Color(0.72f, 0.56f, 0.38f));
        var tape = Material("P2_Tape", new Color(0.55f, 0.42f, 0.25f));
        var hole = Material("P2_HandHole", new Color(0.08f, 0.07f, 0.06f));
        var standMaterial = Material("P2_Stand", new Color(0.3f, 0.33f, 0.36f));
        var cutterMaterial = Material("P2_CutterHandle", new Color(0.95f, 0.55f, 0.1f));
        var bladeMaterial = Material("P2_CutterBlade", new Color(0.75f, 0.78f, 0.8f));
        var binMaterial = Material("P2_Bin", new Color(0.2f, 0.35f, 0.25f));
        var recycleMaterial = Material("P2_RecycleBin", new Color(0.25f, 0.3f, 0.45f));

        var intake = new GameObject("IntakeDirector").AddComponent<IntakeDirector>();
        intake.transform.SetParent(systems, false);
        if (!File.Exists(DeliveriesSource)) throw new Exception("Deliveries missing: " + DeliveriesSource);
        File.Copy(DeliveriesSource, DeliveriesPath, true);
        AssetDatabase.ImportAsset(DeliveriesPath);
        intake.Deliveries = AssetDatabase.LoadAssetAtPath<TextAsset>(DeliveriesPath) ?? throw new Exception("Deliveries missing: " + DeliveriesPath);
        intake.Catalog = catalog;
        intake.Audio = audio;
        intake.Cardboard = cardboard;
        intake.Tape = tape;
        intake.Hole = hole;
        intake.Bead = yellow;
        intake.Hint = yellow;

        // Slots on the shelf fronts facing the counter. Two take deliveries on Day 2 (day2_deliveries.csv), three hold stock.
        var slots = new GameObject("Shelf Slots").transform;
        intake.Slots = new[]
        {
            // The delivery slots (A-2, B-1) on the halves by the stands.
            Slot("A-1", "dev_cheetos_01", 2, 1, ShelfAX + 0.34f, ShelfBoardTops[1], catalog, yellow, green, red, audio, slots),
            Slot("A-2", "dev_shinramen_01", 3, 2, ShelfAX - 0.24f, ShelfBoardTops[1], catalog, yellow, green, red, audio, slots),
            Slot("A-3", "dev_squidpeanut_01", 2, 1, ShelfAX + 0.34f, ShelfBoardTops[2], catalog, yellow, green, red, audio, slots),
            Slot("B-1", "dev_goraebap_01", 2, 2, ShelfBX + 0.35f, ShelfBoardTops[1], catalog, yellow, green, red, audio, slots),
            Slot("B-2", "dev_ogamja_01", 2, 1, ShelfBX - 0.13f, ShelfBoardTops[1], catalog, yellow, green, red, audio, slots),
        };
        intake.DecorSlots = BuildDecorSlots(catalog, yellow, green, red, audio, slots);

        // Stands and cutters (doc 5.7.1 B2, B3; 5.7.3 one cutter per stand).
        var stands = new List<BoxStandSocket>();
        var cutters = new List<BoxCutter>();
        foreach (var (letter, center) in new[] { ("A", StandACenter), ("B", StandBCenter) })
        {
            var standRoot = new GameObject("Box Stand " + letter).transform;
            standRoot.SetParent(environment, false);
            standRoot.position = center + Vector3.up * StandTop;
            Box($"Stand {letter} Body", center + Vector3.up * (StandTop * 0.5f), new Vector3(0.5f, StandTop, 0.5f), standMaterial, standRoot, true);
            var socketObject = new GameObject($"BoxStandSocket {letter}") { layer = LayerMask.NameToLayer("Zone") };
            socketObject.transform.SetParent(standRoot, false);
            socketObject.transform.position = center + Vector3.up * (StandTop + 0.17f);
            var trigger = socketObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(0.7f, 0.42f, 0.5f);
            var seat = new GameObject("Box Seat").transform;
            seat.SetParent(socketObject.transform, false);
            seat.SetPositionAndRotation(center + Vector3.up * StandTop, Quaternion.identity);
            var socket = socketObject.AddComponent<BoxStandSocket>();
            socket.Shelf = letter;
            socket.attachTransform = seat;
            socket.hoverSocketSnapping = false;
            socket.showInteractableHoverMeshes = false;
            stands.Add(socket);

            // The cutter lies along the stand's aisle edge, in front of the box.
            var holder = new GameObject($"Cutter Holder {letter}").transform;
            holder.SetParent(standRoot, false);
            holder.SetPositionAndRotation(center + new Vector3(0f, StandTop + 0.013f, -0.19f), Quaternion.identity);
            cutters.Add(Cutter(letter, holder, cutterMaterial, bladeMaterial));
        }
        intake.Stands = stands.ToArray();
        intake.Cutters = cutters.ToArray();

        // Warehouse door, delivery cart and the empty-box bin behind the counter (zone 6).
        var warehouse = new GameObject("Warehouse Door").transform;
        warehouse.SetParent(environment, false);
        warehouse.position = new Vector3(CartCenter.x, 2.1f, -2.35f);
        Box("Warehouse Door Panel", new Vector3(CartCenter.x, 1.05f, -2.44f), new Vector3(1.2f, 2.1f, 0.02f), Material("P2_WarehouseDoor", new Color(0.32f, 0.27f, 0.22f)), warehouse, false);
        AddBackLabel(warehouse, "창고 입구");
        var cart = new GameObject("Intake Cart").transform;
        cart.SetParent(environment, false);
        cart.position = CartCenter + Vector3.up * StandTop;
        Box("Cart Body", CartCenter + Vector3.up * 0.4f, new Vector3(CartWidth, 0.8f, 0.6f), standMaterial, cart, true);
        intake.CartSpots = new[]
        {
            Marker("Cart Spot 1", CartCenter + new Vector3(-CartSpotOffset, StandTop, 0f), Quaternion.Euler(0f, 180f, 0f), cart),
            Marker("Cart Spot 2", CartCenter + new Vector3(CartSpotOffset, StandTop, 0f), Quaternion.Euler(0f, 180f, 0f), cart),
        };
        // Beside the cart along the back, so the box walk ends where it began.
        var recycle = OpenBin("Empty Box Bin", RecycleCenter, RecycleSize, recycleMaterial, environment, false);
        var recycleZone = Zone<BoxRecycleZone>("BoxRecycleZone", recycle, RecycleCenter + Vector3.up * 0.4f, new Vector3(RecycleSize.x - 0.05f, 0.6f, RecycleSize.z - 0.05f));
        recycleZone.Intake = intake;
        AddBackLabel(recycle, "빈 박스 수거함");

        // Waste bin by the counter on the clerk's side (zone 5).
        var waste = OpenBin("Waste Bin", DisposalCenter, new Vector3(0.4f, 0.8f, 0.4f), binMaterial, environment, true);
        var wasteZone = Zone<DisposalBinZone>("DisposalBinZone", waste, DisposalCenter + Vector3.up * 0.45f, new Vector3(0.36f, 0.62f, 0.36f));
        wasteZone.Intake = intake;
        AddBackLabel(waste, "폐기함");

        // Guide panels where the work is: above each shelf, facing the aisle, and above the cart, facing the counter.
        var guides = new GameObject("Intake Guides").transform;
        guides.SetParent(systems, false);
        intake.GuideAnchors = new[]
        {
            Marker("Intake Guide A", new Vector3(ShelfAX, 2.12f, ShelfFront - 0.02f), Quaternion.identity, guides),
            Marker("Intake Guide B", new Vector3(ShelfBX, 2.12f, ShelfFront - 0.02f), Quaternion.identity, guides),
            Marker("Intake Guide Cart", new Vector3(CartCenter.x, 1.85f, -2.05f), Quaternion.Euler(0f, 180f, 0f), guides),
        };
        return intake;
    }

    /// <summary>
    /// Real products on the shelves a player sees first (2026-11-09): row D's face toward the street, passed on the walk in,
    /// and the eye-level boards of row A + B that were still cartons, seen across the counter. They are display stock only,
    /// and hold the products filmed on 2026-11-08. Row A + B's are far copies throughout, so standing at those shelves on
    /// Day 2 stays inside the gaussian budget.
    /// </summary>
    static ShelfSlotZone[] BuildDecorSlots(ProductCatalog catalog, Material yellow, Material green, Material red, GameAudio audio, Transform parent)
    {
        const float half = 0.36f;
        var made = new List<ShelfSlotZone>();
        void Add(string id, string product, float centerX, float board, float frontZ, bool farOnly)
        {
            var slot = Slot(id, product, 3, 1, centerX, board, catalog, yellow, green, red, audio, parent, frontZ, farOnly);
            slot.DisplayOnly = true;
            slot.FarOnly = farOnly;
            slot.GetComponent<BoxCollider>().enabled = false;
            made.Add(slot);
        }
        Add("D-1", "dev_pocachip_01", ShelfDX - half, ShelfBoardTops[2], ShelfDFront, false);
        Add("D-2", "dev_onionring_01", ShelfDX + half, ShelfBoardTops[2], ShelfDFront, false);
        Add("D-3", "dev_gamjakkang_01", ShelfDX - half, ShelfBoardTops[1], ShelfDFront, false);
        Add("D-4", "dev_indianbap_01", ShelfDX + half, ShelfBoardTops[1], ShelfDFront, false);
        Add("D-5", "dev_caramelcorn_01", ShelfDX - half, ShelfBoardTops[0], ShelfDFront, false);
        Add("D-6", "dev_kkokkalcorn_02", ShelfDX + half, ShelfBoardTops[0], ShelfDFront, false);
        Add("E-1", "dev_shinjjang_01", ShelfAX - half, ShelfBoardTops[2], ShelfFront, true);
        Add("E-2", "dev_jollypong_01", ShelfBX - half, ShelfBoardTops[2], ShelfFront, true);
        Add("E-3", "dev_almondchoco_01", ShelfBX + half, ShelfBoardTops[2], ShelfFront, true);
        return made.ToArray();
    }

    static ShelfSlotZone Slot(string id, string productId, int columns, int rows, float centerX, float boardTop, ProductCatalog catalog,
        Material yellow, Material green, Material red, GameAudio audio, Transform parent, float frontZ = ShelfFront, bool farOnly = false)
    {
        // Straight from the prefab: the catalog's references can go stale after the character setup imports assets.
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{productId}.prefab")?.GetComponent<ProductPrefab>()
                     ?? throw new Exception("Slot product missing: " + productId);
        _ = catalog;
        var size = prefab.Proxy.size;
        var go = new GameObject("Slot " + id) { layer = LayerMask.NameToLayer("Zone") };
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(new Vector3(centerX, boardTop, frontZ + 0.005f), Quaternion.identity);
        var slot = go.AddComponent<ShelfSlotZone>();
        slot.SlotId = id;
        slot.ProductId = productId;
        slot.Columns = columns;
        slot.Rows = rows;
        slot.CellWidth = size.x + 0.02f;
        slot.CellDepth = size.z + 0.02f;
        slot.ProductHeight = size.y;
        slot.Yellow = yellow;
        slot.Green = green;
        slot.Red = red;
        slot.Audio = audio;
        // Empty until P4ShelfSplats.Generate has run; the slot then simply draws each product.
        slot.MergedFar = AssetDatabase.LoadAssetAtPath<GaussianSplatting.Runtime.GaussianSplatAsset>($"{P4ShelfSplats.MergedFolder}/Shelf_{id}_LOD1.asset");
        slot.MergedNear = farOnly ? slot.MergedFar : AssetDatabase.LoadAssetAtPath<GaussianSplatting.Runtime.GaussianSplatAsset>($"{P4ShelfSplats.MergedFolder}/Shelf_{id}_LOD0.asset");
        var zone = go.GetComponent<BoxCollider>();
        zone.isTrigger = true;
        float height = size.y + 0.03f;
        zone.size = new Vector3(columns * slot.CellWidth, height, rows * slot.CellDepth);
        zone.center = new Vector3(0f, height * 0.5f, rows * slot.CellDepth * 0.5f);
        if (rows * slot.CellDepth + 0.005f > (ShelfBack - ShelfFront) * 0.5f - 0.015f) throw new Exception($"Slot {id} is deeper than the shelf face.");
        return slot;
    }

    static BoxCutter Cutter(string letter, Transform holder, Material handleMaterial, Material bladeMaterial)
    {
        var root = new GameObject($"Box Cutter {letter}") { layer = LayerMask.NameToLayer("Interactable") };
        root.transform.SetPositionAndRotation(holder.position, holder.rotation);
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        handle.name = "Handle";
        handle.layer = root.layer;
        handle.transform.SetParent(root.transform, false);
        handle.transform.localScale = new Vector3(0.12f, 0.024f, 0.03f);
        handle.GetComponent<Renderer>().sharedMaterial = handleMaterial;
        var blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
        blade.name = "Blade";
        UnityEngine.Object.DestroyImmediate(blade.GetComponent<Collider>());
        blade.transform.SetParent(root.transform, false);
        blade.transform.localPosition = new Vector3(0.08f, -0.004f, 0f);
        blade.transform.localScale = new Vector3(0.04f, 0.014f, 0.002f);
        blade.GetComponent<Renderer>().sharedMaterial = bladeMaterial;
        var tip = new GameObject("Blade Tip").transform;
        tip.SetParent(root.transform, false);
        tip.localPosition = new Vector3(0.1f, -0.008f, 0f);
        var body = root.AddComponent<Rigidbody>();
        body.mass = 0.15f;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        var grab = root.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        grab.movementType = UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.MovementType.Instantaneous;
        grab.throwOnDetach = false;
        grab.useDynamicAttach = true;
        var cutter = root.AddComponent<BoxCutter>();
        cutter.Tip = tip;
        cutter.Holder = holder;
        // The newer cutter a store that is kept can order in: a dark grip over the handle and a guard at the blade.
        // Looks only and off as built; GameManager.OpenStore shows it.
        var model = new GameObject(GameManager.CutterLook).transform;
        model.SetParent(root.transform, false);
        foreach (var (partName, at, scale, material) in new[]
        {
            ("Grip", new Vector3(-0.02f, 0f, 0f), new Vector3(0.085f, 0.03f, 0.036f), Material("P2_CutterGrip", new Color(0.1f, 0.11f, 0.13f))),
            ("Guard", new Vector3(0.056f, 0.002f, 0f), new Vector3(0.014f, 0.034f, 0.036f), bladeMaterial),
        })
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = partName;
            UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(model, false);
            part.transform.localPosition = at;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
        }
        model.gameObject.SetActive(false);
        root.transform.SetParent(holder.parent, true);
        return cutter;
    }

    /// <summary>An open-top bin: floor and four walls. The floor returns dropped products on non-intake days.</summary>
    static Transform OpenBin(string name, Vector3 center, Vector3 size, Material material, Transform environment, bool respawnFloor)
    {
        var root = new GameObject(name).transform;
        root.SetParent(environment, false);
        root.position = center + Vector3.up * size.y;
        const float t = 0.02f;
        var floor = Box(name + " Floor", center + Vector3.up * (t * 0.5f), new Vector3(size.x, t, size.z), material, root, true);
        if (respawnFloor) floor.AddComponent<ProductRespawnSurface>();
        Box(name + " Wall F", center + new Vector3(0f, size.y * 0.5f, -size.z * 0.5f + t * 0.5f), new Vector3(size.x, size.y, t), material, root, true);
        Box(name + " Wall B", center + new Vector3(0f, size.y * 0.5f, size.z * 0.5f - t * 0.5f), new Vector3(size.x, size.y, t), material, root, true);
        Box(name + " Wall L", center + new Vector3(-size.x * 0.5f + t * 0.5f, size.y * 0.5f, 0f), new Vector3(t, size.y, size.z), material, root, true);
        Box(name + " Wall R", center + new Vector3(size.x * 0.5f - t * 0.5f, size.y * 0.5f, 0f), new Vector3(t, size.y, size.z), material, root, true);
        return root;
    }

    static T Zone<T>(string name, Transform parent, Vector3 center, Vector3 size) where T : Component
    {
        var go = new GameObject(name) { layer = LayerMask.NameToLayer("Zone") };
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        var collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = size;
        return go.AddComponent<T>();
    }

    static Material UnlitMaterial(string name, Color color)
    {
        var path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? throw new Exception("URP Unlit missing.");
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void AddLabel(Transform target, string label) => target.gameObject.AddComponent<StationLabel>().Label = label;

    static void AddBackLabel(Transform target, string label)
    {
        var station = target.gameObject.AddComponent<StationLabel>();
        station.Label = label;
        station.FacesBackWall = true;
    }

    static Transform Marker(string name, Vector3 position)
    {
        var marker = new GameObject(name).transform;
        marker.position = position;
        return marker;
    }

    static Transform Marker(string name, Vector3 position, Quaternion rotation, Transform parent)
    {
        var marker = new GameObject(name).transform;
        marker.SetParent(parent, false);
        marker.SetPositionAndRotation(position, rotation);
        return marker;
    }

    /// <summary>
    /// Bakes with the built-in NavMeshBuilder (AI module), no package. Customers may walk the store floor in front of the
    /// counter and the porch; the clerk's side has no floor source, so they cannot path behind the counter. Every solid
    /// box in Environment blocks and is not walkable on top.
    /// </summary>
    static NavMeshData BakeNavMesh(Transform environment)
    {
        var settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = AgentRadius;
        settings.agentHeight = 1.8f;
        settings.agentClimb = 0.05f;
        settings.agentSlope = 10f;
        var sources = new List<NavMeshBuildSource>
        {
            FloorSource(new Vector3(RoomFar, 0f, CounterFar), new Vector3(RoomNear, 0f, RoomFront)),
            // Between the street front and row D, up to the gap past the counter's far end; the clerk's side has no floor source.
            FloorSource(new Vector3(RoomFar, 0f, StorefrontZ), new Vector3(CustomerFloorEnd, 0f, CounterFar)),
            FloorSource(new Vector3(DoorCenter.x - DoorWidth * 0.5f - 0.05f, 0f, StorefrontZ - VestibuleDepth), new Vector3(DoorCenter.x + DoorWidth * 0.5f + 0.05f, 0f, StorefrontZ + 0.05f)),
        };
        // Customers keep out of the aisles behind the gondolas (too narrow for two to pass); the clerk may still walk there.
        foreach (var x in new[] { ShelfAX, ShelfBX })
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = new Vector3(ShelfWidth, 2f, 1.1f),
                transform = Matrix4x4.Translate(new Vector3(x, 1f, ShelfBack + 0.55f)),
                area = 1,
            });
        // Nor the strip between the counter and the line: a walker caught there when the customer at the counter and the
        // two in line stood still was shut in with no way out (2026-11-08). The line places stay reachable from behind.
        sources.Add(new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            size = new Vector3(2.25f, 2f, 0.2f),
            transform = Matrix4x4.Translate(new Vector3(1.475f, 1f, CounterFar + 0.1f)),
            area = 1,
        });
        foreach (var collider in environment.GetComponentsInChildren<BoxCollider>())
        {
            if (collider.isTrigger) continue;
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = collider.size,
                transform = collider.transform.localToWorldMatrix * Matrix4x4.Translate(collider.center),
                area = 1,
            });
        }
        var bounds = new Bounds(new Vector3(-0.75f, 1f, 1.2f), new Vector3(12.5f, 4f, 10.5f));
        var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        if (data == null) throw new Exception("NavMesh bake failed.");
        data.name = "StoreNavMesh";
        return data;
    }

    static NavMeshBuildSource FloorSource(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        center.y = -0.05f;
        return new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            size = new Vector3(max.x - min.x, 0.1f, max.z - min.z),
            transform = Matrix4x4.Translate(center),
            area = 0,
        };
    }

    /// <summary>Fails the build if a marker is off the NavMesh, unreachable from the door, or the clerk's side is walkable.</summary>
    static void CheckNavMesh(NavMeshData data, CustomerDirector director, Transform managerSpot)
    {
        var instance = NavMesh.AddNavMeshData(data);
        try
        {
            var markers = new List<Transform> { director.Counter, managerSpot };
            markers.AddRange(director.QueueSlots);
            markers.AddRange(director.BrowseSpots);
            var door = director.Door.position;
            if (!NavMesh.SamplePosition(door, out var doorHit, 0.05f, NavMesh.AllAreas)) throw new Exception("Door marker is off the NavMesh.");
            var path = new NavMeshPath();
            var report = new List<string>();
            foreach (var marker in markers)
            {
                if (!NavMesh.SamplePosition(marker.position, out var hit, 0.05f, NavMesh.AllAreas))
                    throw new Exception($"Customer marker {marker.name} at {marker.position.ToString("F2")} is off the NavMesh.");
                if (!NavMesh.CalculatePath(doorHit.position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    throw new Exception($"No path from the door to {marker.name}: {path.status}.");
                float length = 0f;
                for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                report.Add($"{marker.name} {length:0.00}m");
            }
            if (NavMesh.SamplePosition(new Vector3(0f, 0f, 0f), out var clerk, 0.5f, NavMesh.AllAreas))
                throw new Exception($"The clerk's side is walkable for customers at {clerk.position.ToString("F2")}.");
            var triangulation = NavMesh.CalculateTriangulation();
            Debug.Log($"[P2] NavMesh baked: {triangulation.indices.Length / 3} triangles; paths from the door: {string.Join(", ", report)}");
        }
        finally
        {
            instance.Remove();
        }
    }

    static GameObject Box(string name, Vector3 center, Vector3 size, Material material, Transform parent, bool collider)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.position = center;
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        if (!collider) UnityEngine.Object.DestroyImmediate(box.GetComponent<Collider>());
        return box;
    }

    static Material Material(string name, Color color)
    {
        var path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? throw new Exception("URP Lit missing.");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>The scanner's red light where it lands: unlit and opaque, seen from both sides of the thin strip.</summary>
    static Material BeamMaterial()
    {
        var path = $"{MaterialFolder}/P2_ScanBeam.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? throw new Exception("URP Unlit missing.");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", new Color(1f, 0.06f, 0.04f));
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>URP Lit set to alpha blending, as P1InteractionTrial does. The material asset carries the keyword into the player.</summary>
    static Material TransparentMaterial(string name, Color color)
    {
        var material = Material(name, color);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>-executeMethod entry point. Add -p2-verify to play the whole day, or -p2-locomotion-verify to test free movement.</summary>
    public static void VerifyEditor()
    {
        EditorSceneManager.OpenScene(StoreScene);
        EditorApplication.isPlaying = true;
    }

    /// <summary>
    /// Makes the change tray and the customer's plate solid in the store as it is, without building it again (a build
    /// bakes the light anew). Notes and coins lay on the counter inside the trays' 4 mm and were hidden by them: seen on
    /// the headset, 2026-11-13. Also gives the director the counter's bag, which it otherwise finds by name.
    /// Unity.exe -batchmode -executeMethod P2StoreBuilder.MakeTraysSolidInStore -quit
    /// </summary>
    public static void MakeTraysSolidInStore()
    {
        var scene = EditorSceneManager.OpenScene(StoreScene, OpenSceneMode.Single);
        int made = 0;
        foreach (var name in new[] { "Change Tray", "Customer Plate" })
        {
            var tray = GameObject.Find(name) ?? throw new Exception("No " + name + " in " + StoreScene);
            if (tray.GetComponent<Collider>() != null) continue;
            tray.AddComponent<BoxCollider>();
            made++;
        }
        var director = UnityEngine.Object.FindFirstObjectByType<CustomerDirector>() ?? throw new Exception("No CustomerDirector in " + StoreScene);
        var bag = GameObject.Find("Plastic Bag");
        if (director.BagOnCounter == null && bag != null) director.BagOnCounter = bag.GetComponent<Renderer>();
        EditorUtility.SetDirty(director);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new Exception("Scene save failed.");
        Debug.Log($"[Store] trays made solid: {made}; the counter's bag given to the director: {director.BagOnCounter != null}");
    }

    public static void BuildPlayer()
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { StoreScene },
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        var summary = report.summary;
        Directory.CreateDirectory(Evidence);
        File.WriteAllText(Evidence + "/il2cpp_build.txt",
            $"Unity {Application.unityVersion}; Windows x64 IL2CPP Development; result={summary.result}; errors={summary.totalErrors}; warnings={summary.totalWarnings}; bytes={summary.totalSize}; duration={summary.totalTime}; scene={StoreScene}; output={PlayerPath}\n");
        if (summary.result != BuildResult.Succeeded) throw new Exception("Player build failed: " + summary.result);
    }
}
