using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Debug = UnityEngine.Debug;

/// <summary>
/// Runs only with -p2-load. Stops the game, fills the view with products from the catalog, and measures frame time
/// as the count grows, uncapped so the cost shows past VSync. A trend scan for design decisions: it is shorter than the
/// 60s x 3 protocol in the implementation doc, and with the Mock Runtime it has no Link encoding or compositor cost.
/// It is not a Quest 2 result.
/// </summary>
public class P2LoadTest : MonoBehaviour
{
    const float SettleSeconds = 2f;
    const float SampleSeconds = 6f;
    const float FrameBudgetMs = 1000f / 72f;
    const int Quest2EyeWidth = 1832;
    const int Quest2EyeHeight = 1920;
    const float ColumnSpacing = 0.24f;
    const float RowSpacing = 0.33f;
    const int Columns = 8;
    const float NearMeters = 0.3f;
    const float MidMeters = 1f;
    const float FarMeters = 3.5f;
    const float BehindMeters = -1f;
    const float EyeHeight = 1.36f;
    const float ImpossibleGpuMs = 1000f;
    const float CullConeDegrees = 75f;
    const float RingMeters = 1.5f;
    const float TurnDegreesPerSecond = 90f;
    const int RingProducts = 24;
    const int ComparePoses = 8;
    // Not a divisor of the ring's 15 degree spacing, so each pose has the products in a different place.
    const float ComparePoseDegrees = 46.875f;
    const float PlacedToleranceMeters = 0.01f;
    static readonly Quaternion FacingPlayer = Quaternion.Euler(0f, 180f, 0f);
    static readonly int[] Counts = { 1, 2, 4, 8, 12, 16, 24 };
    static readonly float[] RowOffsets = { 0f, RowSpacing, -RowSpacing };

    ProductCatalog catalog;
    Camera view;
    string outputPath;
    readonly List<ProductPrefab> instances = new List<ProductPrefab>();
    readonly StringBuilder csv = new StringBuilder("scenario,products,gaussians,render_scale,eye_width,eye_height,sort_nth,sh_order,frames,ms_avg,ms_p50,ms_p95,ms_p99,fps_avg,over_budget_pct,gpu_ms_avg,gpu_ms_p95,cpu_main_ms_avg,cpu_render_ms_avg,graphics_mib,spawn_call_ms_max,spawn_frame_ms_max,ms_max,culled_avg\n");
    readonly FrameTiming[] timing = new FrameTiming[1];
    float spawnCallMax;
    float spawnFrameMax;
    float externalMouseMotion;
    int sortNth = 1;
    int shOrder = 3;
    float baseRenderScale = 1f;
    Transform floorOffset;
    Action eachFrame;
    readonly Dictionary<ProductPrefab, Vector3> placed = new Dictionary<ProductPrefab, Vector3>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Begin()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-p2-load") < 0) return;
        var game = FindFirstObjectByType<GameManager>();
        if (game == null)
        {
            Debug.LogError("[P2 Load] GameManager not found; open StoreScene.");
            return;
        }
        game.gameObject.SetActive(false);
        var runner = new GameObject("P2 Load Test").AddComponent<P2LoadTest>();
        runner.catalog = game.Catalog;
        int index = Array.IndexOf(args, "-p2-load-output");
        runner.outputPath = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Application.persistentDataPath, "p2_load.csv");
    }

    IEnumerator Start()
    {
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        yield return new WaitForSecondsRealtime(4f);
        view = Camera.main;

        // A headset is worn at standing height, but the Mock Runtime reports its head near the floor, which would put
        // everything measured behind the counter. Moving the origin does not last, since the rig's gravity puts it back,
        // and the reported height keeps settling for a while, so LateUpdate holds it there for the whole run.
        floorOffset = FindFirstObjectByType<XROrigin>().CameraFloorOffsetObject.transform;
        yield return new WaitForSeconds(2f);
        if (Mathf.Abs(view.transform.position.y - EyeHeight) > 0.05f)
        {
            Debug.LogError($"[P2 Load] Camera is at y={view.transform.position.y:0.000}, not eye height. These numbers would not mean anything; stopping.");
            Application.Quit(2);
            yield break;
        }

        if (XRSettings.isDeviceActive)
        {
            // Same pixel count as a Quest 2 panel, whatever the runtime recommends. URP overwrites the eye texture
            // scale from its own render scale each frame, so this is the setting that counts.
            float pixels = XRSettings.eyeTextureWidth * XRSettings.eyeTextureHeight;
            baseRenderScale = Mathf.Sqrt(Quest2EyeWidth * Quest2EyeHeight / pixels);
            UniversalRenderPipeline.asset.renderScale = baseRenderScale;
            yield return new WaitForSeconds(1f);
        }
        Debug.Log($"[P2 Load] xr={XRSettings.isDeviceActive} eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} screen={Screen.width}x{Screen.height} camera={view.transform.position.ToString("F3")} forward={view.transform.forward.ToString("F3")} gpu={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType}");

        Debug.Log($"[P2 Load] widest view from forward: {WidestViewDegrees(view):0.0} degrees; cone {CullConeDegrees} degrees");
        yield return Measure("scene_only", 0, NearMeters);

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-p2-load-culling") >= 0)
        {
            yield return CullingSuite();
            Finish();
            yield break;
        }

        foreach (var count in Counts) yield return Measure("shelf_1m", count, MidMeters);
        foreach (var count in Counts) yield return Measure("shelf_30cm", count, NearMeters);
        foreach (var count in Counts) yield return Measure("shelf_3.5m", count, FarMeters);
        yield return MeasureHeldAndShelf();

        // Nothing here is on screen. Whatever this costs is spent on products the player cannot see.
        yield return Measure("behind_camera", 8, BehindMeters);
        yield return Measure("behind_camera", 24, BehindMeters);

        // The doc's mitigations, on one close and one large case.
        yield return Mitigations("shelf_30cm", 8, NearMeters);
        yield return Mitigations("shelf_1m", 24, MidMeters);

        Finish();
    }

    void Finish(int exitCode = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, csv.ToString());
        UniversalRenderPipeline.asset.renderScale = 1f;
        GaussianSplatRenderer.ViewCullHalfAngleDegrees = 0f;
        Debug.Log($"[P2 Load] done output={outputPath} externalMouseMotion={externalMouseMotion:0}");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(exitCode);
#else
        Application.Quit(exitCode);
#endif
    }

    /// <summary>The same scenes with the cone off and on, a head turn, and matching pictures for a pixel comparison.</summary>
    IEnumerator CullingSuite()
    {
        foreach (var cone in new[] { 0f, CullConeDegrees })
        {
            GaussianSplatRenderer.ViewCullHalfAngleDegrees = cone;
            string tag = cone > 0f ? "cone" : "full";

            yield return EnsureInstances(RingProducts);
            foreach (var product in instances) product.gameObject.SetActive(true);
            ArrangeRing(0f);
            yield return Sample($"ring_{tag}", RingProducts);

            // Turning the ring is the same to the renderer as turning the head, and products cross the cone edge.
            float turned = 0f;
            eachFrame = () =>
            {
                turned += TurnDegreesPerSecond * Time.unscaledDeltaTime;
                ArrangeRing(turned);
            };
            yield return Sample($"ring_turning_{tag}", RingProducts);
            eachFrame = null;

            yield return Measure($"shelf_1m_{tag}", RingProducts, MidMeters);
            yield return Measure($"behind_camera_{tag}", RingProducts, BehindMeters);
        }

        foreach (var product in instances) product.gameObject.SetActive(true);
        for (int pose = 0; pose < ComparePoses; pose++)
        {
            foreach (var cone in new[] { 0f, CullConeDegrees })
            {
                GaussianSplatRenderer.ViewCullHalfAngleDegrees = cone;
                ArrangeRing(pose * ComparePoseDegrees);
                for (int frame = 0; frame < 4; frame++) yield return null;
                string file = $"{Path.GetFileNameWithoutExtension(outputPath)}_compare_{pose}_{(cone > 0f ? "cone" : "full")}.png";
                ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(outputPath), file));
                for (int frame = 0; frame < 4; frame++) yield return null;
            }
        }
        GaussianSplatRenderer.ViewCullHalfAngleDegrees = 0f;
    }

    void ArrangeRing(float degrees)
    {
        var eye = view.transform.position;
        for (int i = 0; i < instances.Count; i++)
        {
            float angle = (degrees + i * 360f / instances.Count) * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * RingMeters;
            Place(instances[i], eye + offset, Quaternion.LookRotation(-offset));
        }
    }

    /// <summary>How far from straight ahead the current view reaches, at its worst corner. Reported next to the cone.</summary>
    public static float WidestViewDegrees(Camera view)
    {
        float widest = 0f;
        var projections = view.stereoEnabled
            ? new[] { view.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), view.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) }
            : new[] { view.projectionMatrix };
        foreach (var projection in projections)
        {
            var inverse = projection.inverse;
            foreach (var x in new[] { -1f, 1f })
                foreach (var y in new[] { -1f, 1f })
                    widest = Mathf.Max(widest, Vector3.Angle(Vector3.back, inverse.MultiplyPoint(new Vector3(x, y, 0.5f))));
        }
        return widest;
    }

    void Update()
    {
        foreach (var device in InputSystem.devices)
            if (device is Mouse mouse) externalMouseMotion += mouse.delta.ReadValue().magnitude;
    }

    void LateUpdate()
    {
        if (floorOffset != null)
            floorOffset.localPosition += Vector3.up * (EyeHeight - view.transform.position.y);
    }

    IEnumerator Mitigations(string scenario, int count, float meters)
    {
        var asset = UniversalRenderPipeline.asset;
        foreach (var scale in new[] { 0.85f, 0.7f })
        {
            asset.renderScale = baseRenderScale * scale;
            yield return Measure($"{scenario}_scale{scale:0.00}", count, meters);
        }
        asset.renderScale = baseRenderScale;

        sortNth = 4;
        yield return Measure($"{scenario}_sort4", count, meters);
        sortNth = 1;

        shOrder = 0;
        yield return Measure($"{scenario}_sh0", count, meters);
        shOrder = 3;
    }

    /// <summary>The G1-H picture from the doc: a shelf slot a meter away and two products held 30 cm from the eyes.</summary>
    IEnumerator MeasureHeldAndShelf()
    {
        yield return EnsureInstances(5);
        for (int i = 0; i < instances.Count; i++) instances[i].gameObject.SetActive(i < 5);
        var eye = view.transform.position;
        for (int i = 0; i < 3; i++)
            Place(instances[i], eye + new Vector3((i - 1) * ColumnSpacing, 0f, MidMeters), FacingPlayer);
        Place(instances[3], eye + new Vector3(-0.12f, -0.1f, NearMeters), FacingPlayer);
        Place(instances[4], eye + new Vector3(0.12f, -0.1f, NearMeters), FacingPlayer);
        yield return Sample("g1h_shelf1m_held30cm", 5);
    }

    IEnumerator Measure(string scenario, int count, float meters)
    {
        yield return EnsureInstances(count);
        for (int i = 0; i < instances.Count; i++)
        {
            bool active = i < count;
            instances[i].gameObject.SetActive(active);
            if (active) Place(instances[i], GridPosition(i, meters), FacingPlayer);
        }
        yield return Sample(scenario, count);
    }

    void Place(ProductPrefab product, Vector3 position, Quaternion rotation)
    {
        product.transform.SetPositionAndRotation(position, rotation);
        placed[product] = position;
    }

    /// <summary>Middle of the row first, so the first few products are the ones looked at.</summary>
    Vector3 GridPosition(int index, float meters)
    {
        int row = index / Columns;
        int slot = index % Columns;
        float offset = ((slot + 1) / 2) * ColumnSpacing * (slot % 2 == 1 ? 1f : -1f);
        return view.transform.position + new Vector3(offset, RowOffsets[row % RowOffsets.Length], meters);
    }

    IEnumerator EnsureInstances(int count)
    {
        while (instances.Count < count)
        {
            var prefab = catalog.Products[instances.Count % catalog.Products.Count];
            var clock = Stopwatch.StartNew();
            var product = Instantiate(prefab, new Vector3(0f, -1f, 0f), Quaternion.Euler(0f, 180f, 0f));
            clock.Stop();
            Freeze(product);
            instances.Add(product);
            spawnCallMax = Mathf.Max(spawnCallMax, (float)clock.Elapsed.TotalMilliseconds);
            // The frame that uploads its buffers. A customer's products arriving mid-shift have this same cost.
            yield return null;
            spawnFrameMax = Mathf.Max(spawnFrameMax, Time.unscaledDeltaTime * 1000f);
            yield return null;
        }
    }

    /// <summary>Only for this test: no physics or grabbing, so the numbers are about rendering.</summary>
    public static void Freeze(ProductPrefab product)
    {
        var body = product.GetComponent<Rigidbody>();
        body.isKinematic = true;
        // The prefab interpolates, and that puts a moved transform back to the body's old pose.
        // Early runs of this test measured products left in earlier layouts because of it.
        body.interpolation = RigidbodyInterpolation.None;
        foreach (var collider in product.GetComponentsInChildren<Collider>()) collider.enabled = false;
        var grab = product.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = false;
        product.enabled = false;
    }

    IEnumerator Sample(string scenario, int count)
    {
        long gaussians = 0;
        for (int i = 0; i < count; i++)
        {
            var splat = instances[i].Splat;
            splat.m_SortNthFrame = sortNth;
            splat.m_SHOrder = shOrder;
            gaussians += splat.splatCount;
        }

        yield return new WaitForSecondsRealtime(SettleSeconds);
        for (int i = 0; i < count; i++)
        {
            float off = Vector3.Distance(instances[i].transform.position, placed[instances[i]]);
            if (off > PlacedToleranceMeters || !instances[i].gameObject.activeInHierarchy)
            {
                Debug.LogError($"[P2 Load] {scenario}: product {i} is {off:0.000}m from where it was put. These numbers would not mean anything; stopping.");
                Finish(3);
                yield break;
            }
        }
        // Proof of what was on screen. Taken before sampling, so it does not touch the numbers.
        ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(outputPath), $"{Path.GetFileNameWithoutExtension(outputPath)}_{scenario}_{count}.png"));
        yield return null;
        yield return null;
        int graphicsMib = (int)(Profiler.GetAllocatedMemoryForGraphicsDriver() / (1024 * 1024));

        var frameMs = new List<float>();
        var gpuMs = new List<float>();
        var mainMs = new List<float>();
        var renderMs = new List<float>();
        float begun = Time.realtimeSinceStartup;
        long culledSum = 0;
        while (Time.realtimeSinceStartup - begun < SampleSeconds)
        {
            yield return null;
            eachFrame?.Invoke();
            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            for (int i = 0; i < count; i++)
                if (instances[i].Splat.IsOutsideViewCone(view)) culledSum++;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
            {
                if (timing[0].gpuFrameTime > 0 && timing[0].gpuFrameTime < ImpossibleGpuMs) gpuMs.Add((float)timing[0].gpuFrameTime);
                mainMs.Add((float)timing[0].cpuMainThreadFrameTime);
                renderMs.Add((float)timing[0].cpuRenderThreadFrameTime);
            }
        }

        frameMs.Sort();
        gpuMs.Sort();
        float average = Average(frameMs);
        int over = frameMs.FindAll(ms => ms > FrameBudgetMs).Count;
        float scale = UniversalRenderPipeline.asset.renderScale;
        csv.AppendLine(FormattableString.Invariant(
            $"{scenario},{count},{gaussians},{scale:0.00},{XRSettings.eyeTextureWidth},{XRSettings.eyeTextureHeight},{sortNth},{shOrder},{frameMs.Count},{average:0.00},{Percentile(frameMs, 0.5f):0.00},{Percentile(frameMs, 0.95f):0.00},{Percentile(frameMs, 0.99f):0.00},{1000f / Mathf.Max(0.01f, average):0.0},{100f * over / Mathf.Max(1, frameMs.Count):0.0},{Average(gpuMs):0.00},{Percentile(gpuMs, 0.95f):0.00},{Average(mainMs):0.00},{Average(renderMs):0.00},{graphicsMib},{spawnCallMax:0.0},{spawnFrameMax:0.0},{frameMs[frameMs.Count - 1]:0.00},{(float)culledSum / Mathf.Max(1, frameMs.Count):0.0}"));
        Debug.Log($"[P2 Load] {scenario} n={count} gaussians={gaussians} frames={frameMs.Count} avg={average:0.00} p95={Percentile(frameMs, 0.95f):0.00} max={frameMs[frameMs.Count - 1]:0.00} culled={(float)culledSum / Mathf.Max(1, frameMs.Count):0.0} gpu={Average(gpuMs):0.00} main={Average(mainMs):0.00} graphicsMib={graphicsMib} spawnFrameMax={spawnFrameMax:0.0}");
    }

    static float Average(List<float> values)
    {
        if (values.Count == 0) return 0f;
        float sum = 0f;
        foreach (var value in values) sum += value;
        return sum / values.Count;
    }

    static float Percentile(List<float> sorted, float fraction)
    {
        if (sorted.Count == 0) return 0f;
        return sorted[Mathf.Clamp(Mathf.CeilToInt(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
