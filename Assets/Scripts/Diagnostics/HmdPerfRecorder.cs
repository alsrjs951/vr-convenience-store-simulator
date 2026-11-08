using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Runs only with -hmd-perf &lt;folder&gt;. Records frame timing on a headset and writes hmd_report.json there.
/// Plain, it records the normal game, split by game state; the right A button (or Space on the PC) marks a moment and
/// saves what both eyes see. With -hmd-gate it replaces the game with the G1-H picture from doc 9.1 (a shelf slot at 1 m,
/// two products at 30 cm) and takes three 30-second windows, each started with A, placed where the head looks at that moment.
///
/// At 72 Hz the runtime paces frames to 13.89 ms however little work they take, so the doc's p95 of 13.5 ms cannot be
/// read from the frame interval. The interval gives fps and missed frames; the work budget is read from GPU and CPU time.
/// </summary>
public class HmdPerfRecorder : MonoBehaviour
{
    const float WindowSeconds = 30f;
    const int GateRuns = 3;
    const float SettleSeconds = 2f;
    const float AutoPressSeconds = 8f;
    const int AutoPressFrames = 6;
    const float LogEverySeconds = 30f;
    const float ImpossibleMs = 1000f;
    const float FallbackRefreshHz = 72f;
    const float GateFps = 72f;
    // A panel sold as 72 Hz can run a hair under it, and that is not a missed frame.
    const float GateFpsTolerance = 0.5f;
    const float GateDropPercent = 5f;
    const float WorkBudgetMs = 13.5f;
    const float WorkDropPercent = 1f;
    const int WorstFrames = 10;
    // Frames around a screenshot or a report write are the recorder's own cost, not the game's.
    const int SkipFramesAfterIo = 6;
    const int CaptureFrames = 4;
    const int InitialFrameCapacity = 200000;
    const int GateProducts = 5;
    const float ShelfMeters = 1f;
    const float HeldMeters = 0.3f;
    const float HeldBelowEye = 0.1f;
    const float ShelfSpacing = 0.24f;
    const float HeldSpacing = 0.12f;
    const string NotWorn = "not_worn";
    const string ReportFile = "hmd_report.json";

    struct Frame
    {
        public float time, ms, gpu, main, render, wait;
        public int segment;
    }

    [Serializable] public class Segment
    {
        public string name;
        public int frames;
        public float seconds;
        public float ms_avg, ms_p50, ms_p95, ms_p99, ms_max, max_at_s;
        public float fps_avg;
        public int missed_frames;
        public float drop_pct;
        public int gpu_samples;
        public float gpu_ms_avg, gpu_ms_p95, gpu_ms_max;
        public float cpu_main_ms_avg, cpu_render_ms_avg, cpu_wait_ms_avg, cpu_work_ms_p95;
        public long gaussians;
        public float head_move_m, head_turn_deg;
        public int xr_dropped = -1;
        public bool gate_pass, work_budget_pass;
    }

    [Serializable] public class Mark
    {
        public float t_s;
        public string state, picture;
        public Vector3 head_from_rig;
        public float head_yaw_from_rig_deg;
    }

    [Serializable] public class WorstFrame
    {
        public float t_s, ms, gpu_ms;
        public string segment;
    }

    [Serializable] public class Report
    {
        public string mode, finished_local;
        public string runtime_name, runtime_version, openxr_plugin, loader, xr_device;
        public bool xr_active, stereo;
        public int simulators_active;
        public string hmd_input, mark_button;
        public List<string> controllers = new List<string>();
        public string gpu, graphics_api, unity_version, stereo_mode;
        public int eye_width, eye_height;
        public float render_scale, viewport_scale;
        public int sh_order;
        public float refresh_hz;
        public bool refresh_known, run_in_background;
        public string input_background, haptic_hand;
        public int haptic_pulses;
        public string tracking_requested, tracking_current;
        public float floor_offset_m, ipd_m, widest_view_deg;
        public int gate_runs_passed;
        public int auto_presses, auto_presses_seen;
        public Segment overall;
        public List<Segment> segments = new List<Segment>();
        public List<Mark> marks = new List<Mark>();
        public List<WorstFrame> worst = new List<WorstFrame>();
    }

    string folder;
    bool gate;
    bool auto;
    bool wornFromStart;
    float secondsOverride;
    float holdEyeHeight = -1f;
    float renderScale = -1f;
    int shOrder = -1;
    GameManager game;
    ProductCatalog catalog;

    readonly Report report = new Report();
    readonly List<Frame> frames = new List<Frame>(InitialFrameCapacity);
    readonly List<string> segmentNames = new List<string>();
    readonly FrameTiming[] timing = new FrameTiming[1];
    readonly List<ProductPrefab> instances = new List<ProductPrefab>();
    Camera view;
    XROrigin origin;
    InputAction markButton;
    Keyboard autoKeyboard;
    XRBaseInputInteractor rightHand;
    Text message;
    bool panelFollowsHead;
    bool buttonWasDown;
    bool pressedThisFrame;
    bool recording;
    bool finished;
    int skipFrames;
    float refreshHz = FallbackRefreshHz;
    float originalRenderScale = -1f;

    string openName;
    int openFirstFrame;
    float openStarted;
    Vector3 openHead;
    Vector3 openForward;
    float openMove;
    float openTurn;
    int openDroppedAtStart = -1;
    long openGaussians;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Begin()
    {
        var args = Environment.GetCommandLineArgs();
        string folder = Argument(args, "-hmd-perf");
        if (folder == null) return;
        var runner = new GameObject("HMD Perf Recorder").AddComponent<HmdPerfRecorder>();
        runner.folder = folder;
        runner.gate = Array.IndexOf(args, "-hmd-gate") >= 0;
        runner.auto = Array.IndexOf(args, "-hmd-auto") >= 0;
        runner.wornFromStart = Array.IndexOf(args, "-hmd-worn") >= 0;
        if (runner.auto)
        {
            // The virtual Space key has to reach the game whichever window has focus. A person at the headset never needs this.
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        }
        runner.secondsOverride = Number(args, "-hmd-seconds", -1f);
        runner.holdEyeHeight = Number(args, "-hmd-hold-eye-height", -1f);
        runner.renderScale = Number(args, "-hmd-render-scale", -1f);
        runner.shOrder = (int)Number(args, "-hmd-sh", -1f);
        runner.game = FindFirstObjectByType<GameManager>();
        if (runner.gate)
        {
            runner.catalog = runner.game.Catalog;
            runner.game.gameObject.SetActive(false);
        }
    }

    static string Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static float Number(string[] args, string name, float fallback)
    {
        string text = Argument(args, name);
        return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    IEnumerator Start()
    {
        Directory.CreateDirectory(folder);
        view = Camera.main;
        origin = FindFirstObjectByType<XROrigin>();
        rightHand = FindRightHand();
        markButton = new InputAction("HMD Perf Mark", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        markButton.AddBinding("<Keyboard>/space");
        markButton.Enable();
        if (renderScale > 0f)
        {
            originalRenderScale = UniversalRenderPipeline.asset.renderScale;
            UniversalRenderPipeline.asset.renderScale = renderScale;
        }
        report.mode = gate ? "gate" : "game";

        yield return new WaitForSecondsRealtime(SettleSeconds);
        RefreshEnvironment();
        Log($"start mode={report.mode} runtime={report.runtime_name} {report.runtime_version} xr={report.xr_active} eye={report.eye_width}x{report.eye_height} refresh={report.refresh_hz:0.##} known={report.refresh_known} tracking={report.tracking_current} simulators={report.simulators_active}");
        yield return gate ? RunGate() : RunGame();
    }

    IEnumerator RunGame()
    {
        // Until the first press nobody has said the headset is on, and frames from an idle headset say nothing.
        OpenSegment(NotWorn);
        recording = true;
        float begun = Time.realtimeSinceStartup;
        float nextLog = begun + LogEverySeconds;
        int nextLogFrame = 0;
        bool worn = wornFromStart;
        bool autoPressed = false;
        while (true)
        {
            yield return null;
            float now = Time.realtimeSinceStartup;
            bool pressed = pressedThisFrame;
            if (auto && !autoPressed && now - begun > AutoPressSeconds)
            {
                autoPressed = true;
                yield return AutoPress();
                pressed = true;
            }
            if (pressed)
            {
                worn = true;
                Buzz();
                StartCoroutine(MarkMoment());
            }
            string name = !worn ? NotWorn : game != null ? game.State.ToString() : "no_game";
            if (name != openName)
            {
                CloseSegment();
                // The first change is the first press, the first moment the headset is known to be on.
                RefreshEnvironment();
                WriteReport();
                skipFrames = SkipFramesAfterIo;
                OpenSegment(name);
            }
            if (now >= nextLog)
            {
                var recent = Summarize("recent", nextLogFrame, frames.Count - nextLogFrame);
                Log($"t={now - begun:0} state={name} frames={recent.frames} fps={recent.fps_avg:0.0} p95={recent.ms_p95:0.00} max={recent.ms_max:0.0} missed={recent.missed_frames} gpu={recent.gpu_ms_avg:0.00}/{recent.gpu_ms_p95:0.00} work_p95={recent.cpu_work_ms_p95:0.00}");
                nextLog = now + LogEverySeconds;
                nextLogFrame = frames.Count;
            }
            if (secondsOverride > 0f && now - begun > secondsOverride) Finish();
        }
    }

    IEnumerator RunGate()
    {
        var panel = WorldUi.CreatePanel(null, "HMD Perf Panel", view.transform.position, Quaternion.identity, new Vector2(1200f, 170f), 0.0007f, new Color(0.04f, 0.05f, 0.09f));
        message = WorldUi.AddText(panel, 25f, 15f, 1150f, 140f, 40, TextAnchor.MiddleCenter);
        message.text = "측정 준비 중";
        panelFollowsHead = true;

        for (int i = 0; i < GateProducts; i++)
        {
            var product = Instantiate(catalog.Products[i % catalog.Products.Count], new Vector3(0f, -1f, 0f), Quaternion.identity);
            P2LoadTest.Freeze(product);
            instances.Add(product);
            openGaussians += product.Splat.splatCount;
            if (shOrder >= 0) product.Splat.m_SHOrder = shOrder;
            yield return null;
        }
        report.sh_order = instances[0].Splat.m_SHOrder;
        float window = secondsOverride > 0f ? secondsOverride : WindowSeconds;
        long gaussians = openGaussians;

        for (int run = 1; run <= GateRuns; run++)
        {
            panelFollowsHead = true;
            message.text = $"{run}/{GateRuns}회: 정면을 보고 오른손 A 버튼을 누르세요.\n{window:0}초 동안 고개를 움직이지 않습니다.";
            if (auto) yield return new WaitForSecondsRealtime(AutoPressSeconds);
            if (auto) yield return AutoPress();
            else
            {
                do yield return null;
                while (!pressedThisFrame);
            }
            panelFollowsHead = false;
            Buzz();
            ArrangeProducts();
            message.text = $"{run}/{GateRuns}회 측정 중. 고개를 움직이지 마세요.";
            yield return new WaitForSecondsRealtime(SettleSeconds);

            OpenSegment($"g1h_shelf1m_held30cm_run{run}");
            openGaussians = gaussians;
            recording = true;
            yield return new WaitForSecondsRealtime(window);
            recording = false;
            CloseSegment();
            RefreshEnvironment();
            WriteReport();
            Buzz();
            yield return CaptureBothEyes($"g1h_run{run}");
            var result = report.segments[report.segments.Count - 1];
            Log($"{result.name} frames={result.frames} fps={result.fps_avg:0.0} p50={result.ms_p50:0.00} p95={result.ms_p95:0.00} p99={result.ms_p99:0.00} max={result.ms_max:0.0} drop={result.drop_pct:0.00}% gpu={result.gpu_ms_avg:0.00}/{result.gpu_ms_p95:0.00} work_p95={result.cpu_work_ms_p95:0.00} moved={result.head_move_m:0.000}m turned={result.head_turn_deg:0.0}deg gaussians={result.gaussians} gate={result.gate_pass} budget={result.work_budget_pass}");
        }

        panelFollowsHead = true;
        message.text = "측정 끝. 상품 라벨과 입체감을 확인하세요.\n끝나면 헤드셋을 벗고 PC에서 창을 닫습니다.";
        if (auto) Finish();
    }

    void ArrangeProducts()
    {
        var head = view.transform;
        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward);
        // The prefabs show their front toward -Z.
        var facing = Quaternion.LookRotation(forward) * Quaternion.Euler(0f, 180f, 0f);
        var eye = head.position;
        for (int i = 0; i < 3; i++)
            instances[i].transform.SetPositionAndRotation(eye + right * ((i - 1) * ShelfSpacing) + forward * ShelfMeters, facing);
        var held = eye + forward * HeldMeters + Vector3.down * HeldBelowEye;
        instances[3].transform.SetPositionAndRotation(held - right * HeldSpacing, facing);
        instances[4].transform.SetPositionAndRotation(held + right * HeldSpacing, facing);
        PlacePanel(1.6f, 0.45f);
    }

    void PlacePanel(float meters, float above)
    {
        var head = view.transform;
        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.01f) return;
        forward.Normalize();
        var panel = (RectTransform)message.canvas.transform;
        panel.SetPositionAndRotation(head.position + forward * meters + Vector3.up * above, Quaternion.LookRotation(forward));
    }

    /// <summary>Mock has no hands and its button state cannot be set from here, so tests press the Space fallback on a virtual keyboard.</summary>
    IEnumerator AutoPress()
    {
        report.auto_presses++;
        if (autoKeyboard == null) autoKeyboard = InputSystem.AddDevice<Keyboard>("HMD Perf Keyboard");
        InputSystem.QueueStateEvent(autoKeyboard, new KeyboardState(Key.Space));
        for (int frame = 0; frame < AutoPressFrames; frame++)
        {
            yield return null;
            if (pressedThisFrame)
            {
                report.auto_presses_seen++;
                break;
            }
        }
        InputSystem.QueueStateEvent(autoKeyboard, new KeyboardState());
        yield return null;
    }

    void Buzz()
    {
        // The rig switches a controller's objects on only once that controller is tracked, which on a real headset comes
        // after Start (on Quest 2 the hand was never found there), so look again until it is.
        if (rightHand == null || !rightHand.isActiveAndEnabled) rightHand = FindRightHand();
        if (rightHand == null) return;
        rightHand.SendHapticImpulse(0.6f, 0.1f);
        report.haptic_pulses++;
    }

    static XRBaseInputInteractor FindRightHand()
    {
        foreach (var hand in FindObjectsByType<XRBaseInputInteractor>(FindObjectsSortMode.None))
            if (hand.handedness == InteractorHandedness.Right) return hand;
        return null;
    }

    /// <summary>
    /// The display subsystem can be torn down before OnApplicationQuit, and a kept reference then throws (on Quest 2 that
    /// cost a whole report), so it is looked up each time it is needed.
    /// </summary>
    static XRDisplaySubsystem RunningDisplay()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);
        foreach (var candidate in displays)
            if (candidate.running) return candidate;
        return null;
    }

    IEnumerator MarkMoment()
    {
        string file = $"mark_{report.marks.Count + 1:00}_{(game != null ? game.State.ToString() : "none")}";
        var head = origin.transform.InverseTransformPoint(view.transform.position);
        report.marks.Add(new Mark
        {
            t_s = Time.realtimeSinceStartup,
            state = game != null ? game.State.ToString() : "",
            picture = file,
            head_from_rig = head,
            head_yaw_from_rig_deg = Vector3.SignedAngle(origin.transform.forward, Vector3.ProjectOnPlane(view.transform.forward, Vector3.up), Vector3.up),
        });
        Log($"mark {report.marks.Count} state={game?.State} head_from_rig={head.ToString("F3")} yaw={report.marks[report.marks.Count - 1].head_yaw_from_rig_deg:0.0}");
        yield return CaptureBothEyes(file);
    }

    IEnumerator CaptureBothEyes(string name)
    {
        skipFrames = SkipFramesAfterIo + 2 * CaptureFrames;
        ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + "_left.png"), ScreenCapture.StereoScreenCaptureMode.LeftEye);
        for (int frame = 0; frame < CaptureFrames; frame++) yield return null;
        ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + "_right.png"), ScreenCapture.StereoScreenCaptureMode.RightEye);
        for (int frame = 0; frame < CaptureFrames; frame++) yield return null;
    }

    void Update()
    {
        // Compared by hand: controllers also update before rendering, after this frame's Update, and a press that lands
        // there could be gone from WasPressedThisFrame by the next frame.
        bool down = markButton != null && markButton.IsPressed();
        pressedThisFrame = down && !buttonWasDown;
        buttonWasDown = down;

        if (message != null && panelFollowsHead) PlacePanel(1.2f, 0.1f);
        FrameTimingManager.CaptureFrameTimings();
        if (!recording) return;
        if (skipFrames > 0)
        {
            skipFrames--;
            return;
        }
        var frame = new Frame { time = Time.realtimeSinceStartup, ms = Time.unscaledDeltaTime * 1000f, gpu = -1f, segment = segmentNames.Count - 1 };
        if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
        {
            if (timing[0].gpuFrameTime > 0 && timing[0].gpuFrameTime < ImpossibleMs) frame.gpu = (float)timing[0].gpuFrameTime;
            frame.main = (float)timing[0].cpuMainThreadFrameTime;
            frame.render = (float)timing[0].cpuRenderThreadFrameTime;
            frame.wait = (float)timing[0].cpuMainThreadPresentWaitTime;
        }
        frames.Add(frame);
        openMove = Mathf.Max(openMove, Vector3.Distance(openHead, view.transform.position));
        openTurn = Mathf.Max(openTurn, Vector3.Angle(openForward, view.transform.forward));
    }

    void LateUpdate()
    {
        // Mock reports its head near the floor. A real headset never gets this flag.
        if (holdEyeHeight > 0f && origin != null)
            origin.CameraFloorOffsetObject.transform.localPosition += Vector3.up * (holdEyeHeight - view.transform.position.y);
    }

    void OpenSegment(string name)
    {
        openName = name;
        segmentNames.Add(name);
        openFirstFrame = frames.Count;
        openStarted = Time.realtimeSinceStartup;
        openHead = view.transform.position;
        openForward = view.transform.forward;
        openMove = 0f;
        openTurn = 0f;
        var display = RunningDisplay();
        openDroppedAtStart = display != null && display.TryGetDroppedFrameCount(out int dropped) ? dropped : -1;
    }

    void CloseSegment()
    {
        if (openName == null) return;
        var segment = Summarize(openName, openFirstFrame, frames.Count - openFirstFrame);
        segment.head_move_m = openMove;
        segment.head_turn_deg = openTurn;
        segment.gaussians = openGaussians;
        var display = RunningDisplay();
        if (openDroppedAtStart >= 0 && display != null && display.TryGetDroppedFrameCount(out int dropped))
            segment.xr_dropped = dropped - openDroppedAtStart;
        report.segments.Add(segment);
        openName = null;
    }

    Segment Summarize(string name, int first, int count)
    {
        var segment = new Segment { name = name, frames = count };
        if (count <= 0) return segment;
        var ms = new List<float>(count);
        var gpu = new List<float>(count);
        var work = new List<float>(count);
        float main = 0f, render = 0f, wait = 0f, sum = 0f;
        float interval = 1000f / refreshHz;
        for (int i = first; i < first + count; i++)
        {
            var frame = frames[i];
            ms.Add(frame.ms);
            sum += frame.ms;
            if (frame.gpu >= 0f) gpu.Add(frame.gpu);
            main += frame.main;
            render += frame.render;
            wait += frame.wait;
            // Neither thread's time includes the wait for present, which is reported on its own. Main plus wait is the interval.
            work.Add(Mathf.Max(frame.main, frame.render));
            segment.missed_frames += Mathf.Max(0, Mathf.RoundToInt(frame.ms / interval) - 1);
            if (frame.ms > segment.ms_max)
            {
                segment.ms_max = frame.ms;
                segment.max_at_s = frame.time;
            }
        }
        ms.Sort();
        gpu.Sort();
        work.Sort();
        segment.seconds = sum / 1000f;
        segment.ms_avg = sum / count;
        segment.ms_p50 = Percentile(ms, 0.5f);
        segment.ms_p95 = Percentile(ms, 0.95f);
        segment.ms_p99 = Percentile(ms, 0.99f);
        segment.fps_avg = 1000f / Mathf.Max(0.01f, segment.ms_avg);
        segment.drop_pct = 100f * segment.missed_frames / (count + segment.missed_frames);
        segment.gpu_samples = gpu.Count;
        foreach (var value in gpu) segment.gpu_ms_avg += value;
        segment.gpu_ms_avg /= Mathf.Max(1, gpu.Count);
        segment.gpu_ms_p95 = Percentile(gpu, 0.95f);
        segment.gpu_ms_max = gpu.Count > 0 ? gpu[gpu.Count - 1] : 0f;
        segment.cpu_main_ms_avg = main / count;
        segment.cpu_render_ms_avg = render / count;
        segment.cpu_wait_ms_avg = wait / count;
        segment.cpu_work_ms_p95 = Percentile(work, 0.95f);
        segment.gate_pass = segment.fps_avg >= GateFps - GateFpsTolerance && segment.drop_pct < GateDropPercent;
        segment.work_budget_pass = gpu.Count > 0 && segment.gpu_ms_p95 <= WorkBudgetMs && segment.cpu_work_ms_p95 <= WorkBudgetMs && segment.drop_pct < WorkDropPercent;
        return segment;
    }

    static float Percentile(List<float> sorted, float fraction)
    {
        if (sorted.Count == 0) return 0f;
        return sorted[Mathf.Clamp(Mathf.CeilToInt(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    /// <summary>Reads the setup again without letting a failure cost the report.</summary>
    void RefreshEnvironment()
    {
        try { ReadEnvironment(); }
        catch (Exception e) { Log($"setup not read: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Read at the start, at each change and at the end. The headset is usually off by the end (it comes off before the
    /// window closes) and may not be on yet at the start, so what only a worn headset reports is kept once seen.
    /// </summary>
    void ReadEnvironment()
    {
        report.xr_active |= XRSettings.isDeviceActive;
        report.xr_device = XRSettings.loadedDeviceName;
        var settings = XRGeneralSettings.Instance;
        var loader = settings != null && settings.Manager != null ? settings.Manager.activeLoader : null;
        report.loader = loader != null ? loader.name : "none";
        if (loader != null)
        {
            report.runtime_name = OpenXRRuntime.name;
            report.runtime_version = OpenXRRuntime.version;
            report.openxr_plugin = OpenXRRuntime.pluginVersion;
        }
        report.simulators_active = 0;
        foreach (var simulator in FindObjectsByType<XRDeviceSimulator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (simulator.isActiveAndEnabled) report.simulators_active++;

        foreach (var device in InputSystem.devices)
        {
            string name = $"{device.GetType().Name}:{device.displayName}";
            if (device is XRHMD) report.hmd_input = name;
            else if (device is UnityEngine.InputSystem.XR.XRController && !report.controllers.Contains(name)) report.controllers.Add(name);
        }
        var bound = new List<string>();
        foreach (var control in markButton.controls)
            if (control.device is not Keyboard) bound.Add(control.path);
        report.mark_button = string.Join("|", bound);

        report.gpu = SystemInfo.graphicsDeviceName;
        report.graphics_api = SystemInfo.graphicsDeviceType.ToString();
        report.unity_version = Application.unityVersion;
        report.stereo_mode = XRSettings.stereoRenderingMode.ToString();
        if (XRSettings.eyeTextureWidth > 0)
        {
            report.eye_width = XRSettings.eyeTextureWidth;
            report.eye_height = XRSettings.eyeTextureHeight;
        }
        report.render_scale = UniversalRenderPipeline.asset.renderScale;
        report.viewport_scale = XRSettings.renderViewportScale;
        report.run_in_background = Application.runInBackground;
        report.input_background = InputSystem.settings.backgroundBehavior.ToString();
        if (rightHand != null) report.haptic_hand = rightHand.name;
        else if (report.haptic_hand == null) report.haptic_hand = "";
        var display = RunningDisplay();
        if (display != null && display.TryGetDisplayRefreshRate(out float hz) && hz > 0f)
        {
            refreshHz = hz;
            report.refresh_known = true;
        }
        report.refresh_hz = refreshHz;

        report.tracking_requested = origin.RequestedTrackingOriginMode.ToString();
        if (report.tracking_current == null || origin.CurrentTrackingOriginMode != TrackingOriginModeFlags.Unknown)
            report.tracking_current = origin.CurrentTrackingOriginMode.ToString();
        report.floor_offset_m = origin.CameraFloorOffsetObject.transform.localPosition.y;
        report.stereo |= view.stereoEnabled;
        if (view.stereoEnabled)
        {
            Vector3 left = view.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
            Vector3 right = view.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
            report.ipd_m = Vector3.Distance(left, right);
        }
        report.widest_view_deg = P2LoadTest.WidestViewDegrees(view);
    }

    void WriteReport()
    {
        report.finished_local = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        report.gate_runs_passed = 0;
        if (gate)
            foreach (var segment in report.segments)
                if (segment.gate_pass) report.gate_runs_passed++;
        int skip = 0;
        while (skip < frames.Count && segmentNames[frames[skip].segment] == NotWorn) skip++;
        report.overall = Summarize("all_after_worn", skip, frames.Count - skip);
        report.worst.Clear();
        var worst = new List<Frame>(frames);
        worst.Sort((a, b) => b.ms.CompareTo(a.ms));
        for (int i = 0; i < Mathf.Min(WorstFrames, worst.Count); i++)
            report.worst.Add(new WorstFrame { t_s = worst[i].time, ms = worst[i].ms, gpu_ms = worst[i].gpu, segment = segmentNames[worst[i].segment] });
        File.WriteAllText(Path.Combine(folder, ReportFile), JsonUtility.ToJson(report, true));
    }

    static void Log(string text)
    {
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "[HMD Perf] {0}", text);
    }

    void Finish()
    {
        WrapUp();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(0);
#else
        Application.Quit(0);
#endif
    }

    void WrapUp()
    {
        if (finished) return;
        finished = true;
        recording = false;
        CloseSegment();
        RefreshEnvironment();
        WriteReport();
        Log($"done report={Path.Combine(folder, ReportFile)} segments={report.segments.Count} frames={frames.Count} marks={report.marks.Count} gate_runs_passed={report.gate_runs_passed}");
    }

    void OnApplicationQuit() => WrapUp();

    void OnDestroy()
    {
        markButton?.Dispose();
        if (autoKeyboard != null) InputSystem.RemoveDevice(autoKeyboard);
        if (originalRenderScale > 0f) UniversalRenderPipeline.asset.renderScale = originalRenderScale;
    }
}
