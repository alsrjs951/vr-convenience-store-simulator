using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders product prefabs from the same fixed views and distances, for side-by-side capture comparisons
/// (docs/p1/촬영기법_비교_2026-11-07.md). No XR rig and no simulator overlay: one plain camera, a solid
/// background, the prefab alone at the origin with physics frozen.
///
/// batch (with XR_RUNTIME_JSON pointing at a missing file so no headset runtime starts):
///   -executeMethod P1CaptureCompare.Run -p1cc-products dev_cornchip_01,dev_cornchip_02 -p1cc-out &lt;folder&gt;
///
/// Views are in the product frame (front +Z, up +Y). The far distance is about where a held product sits;
/// 1200x900 at a 40 degree vertical field of view is about 22 px/degree, near Quest 2's center resolution.
/// These are pictures for people to judge, not measurements, and not what the headset shows.
/// </summary>
public static class P1CaptureCompare
{
    const int Width = 1200, Height = 900, SettleFrames = 6;
    const float VerticalFov = 40f;
    static readonly Color Background = new Color(0.16f, 0.18f, 0.22f, 1f);

    // Whole product at 0.7 m (fits the 36 cm bag), held distance 0.4 m, print reading at 0.25 m.
    static readonly (string name, float yaw, float pitch, float distance)[] Views =
    {
        ("01_front", 0f, 0f, 0.7f),
        ("02_oblique_right", 45f, 20f, 0.7f),
        ("03_side_right", 90f, 0f, 0.7f),
        ("04_back", 180f, 0f, 0.7f),
        ("05_side_left", 270f, 0f, 0.7f),
        ("06_top", 0f, 89f, 0.7f),
        ("07_bottom", 0f, -89f, 0.7f),
        ("08_low_front", 0f, -35f, 0.7f),
        ("09_oblique_left_high", -45f, 50f, 0.7f),
        ("10_held_front", 0f, 0f, 0.4f),
        ("11_held_side_right", 90f, 0f, 0.4f),
        ("12_held_back", 180f, 0f, 0.4f),
        ("13_close_front", 0f, 0f, 0.25f),
        ("14_close_back", 180f, 0f, 0.25f),
        ("15_close_side_right", 90f, 0f, 0.25f),
    };

    static List<string> products;
    static string output;
    static int productIndex, viewIndex, wait;
    static GameObject instance;
    static Camera cam;
    static RenderTexture target;
    static readonly List<string> log = new List<string>();

    public static void Run()
    {
        products = Argument("-p1cc-products").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        output = Path.GetFullPath(Argument("-p1cc-out"));
        Directory.CreateDirectory(output);
        foreach (var id in products)
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(id)) == null) Fail("Prefab missing: " + PrefabPath(id));
        if (!EditorSettings.enterPlayModeOptionsEnabled || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0)
            Fail("Needs Enter Play Mode Options with domain reload disabled (already the project setting).");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        productIndex = viewIndex = 0;
        wait = -1;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    static string PrefabPath(string id) => $"Assets/Prefabs/Products/P_{id}.prefab";

    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        try
        {
            if (cam == null) CreateCamera();
            if (instance == null)
            {
                if (productIndex >= products.Count) { Finish(); return; }
                Spawn(products[productIndex]);
                viewIndex = 0;
                Place();
                return;
            }
            if (--wait > 0) return;
            Save($"{products[productIndex]}__{Views[viewIndex].name}.png");
            if (++viewIndex < Views.Length) { Place(); return; }
            UnityEngine.Object.Destroy(instance);
            instance = null;
            productIndex++;
        }
        catch (Exception e) { Fail(e.ToString()); }
    }

    static void CreateCamera()
    {
        target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
        cam = new GameObject("CompareCam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Background;
        cam.fieldOfView = VerticalFov;
        cam.nearClipPlane = 0.02f;
        cam.farClipPlane = 10f;
        cam.targetTexture = target;
    }

    static void Spawn(string id)
    {
        instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(id)), Vector3.zero, Quaternion.identity);
        var body = instance.GetComponent<Rigidbody>();
        if (body) { body.isKinematic = true; body.detectCollisions = false; }
        var proxy = instance.GetComponent<BoxCollider>();
        log.Add($"{id}: proxy size_m={(proxy ? proxy.size.ToString("F4") : "none")}");
    }

    static void Place()
    {
        var v = Views[viewIndex];
        var direction = Quaternion.Euler(-v.pitch, v.yaw, 0f) * Vector3.forward;
        cam.transform.position = instance.transform.TransformPoint(direction * v.distance);
        var up = Mathf.Abs(v.pitch) > 80f ? instance.transform.TransformDirection(v.pitch > 0f ? Vector3.back : Vector3.forward) : instance.transform.up;
        cam.transform.LookAt(instance.transform.position, up);
        wait = SettleFrames;
    }

    static void Save(string file)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        texture.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(Path.Combine(output, file), texture.EncodeToPNG());
        UnityEngine.Object.Destroy(texture);
    }

    static void Finish()
    {
        File.WriteAllText(Path.Combine(output, "capture_compare.txt"),
            $"Unity {Application.unityVersion}; {SystemInfo.graphicsDeviceType}; color space {QualitySettings.activeColorSpace}; " +
            $"{Width}x{Height}, vertical FOV {VerticalFov}; views (name yaw pitch distance_m): " +
            string.Join("; ", Views.Select(v => $"{v.name} {v.yaw} {v.pitch} {v.distance}")) + "\n" + string.Join("\n", log) + "\n");
        Debug.Log("[P1 Capture Compare] PASS " + output);
        EditorApplication.update -= Tick;
        EditorApplication.isPlaying = false;
        EditorApplication.Exit(0);
    }

    static void Fail(string message)
    {
        Debug.LogError("[P1 Capture Compare] FAIL " + message);
        EditorApplication.Exit(3);
    }

    static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new Exception("Missing argument " + name);
        return args[index + 1];
    }
}
