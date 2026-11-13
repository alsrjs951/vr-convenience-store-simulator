using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Runs only with -p1-verify. Plays the grab flow through virtual Keyboard and Mouse devices:
/// keys/mouse -> XR Device Simulator -> simulated controller -> XRI input actions -> interactor.
/// It only reads state. It never moves the product or calls the interaction manager.
/// This is simulator evidence. It says nothing about a real HMD, controllers, or hand feel.
/// </summary>
public class P1InteractionVerification : MonoBehaviour
{
    const float SettleSeconds = 0.6f;
    // Time-based so the motion stays near hand speed whatever the frame rate.
    const float MaxHandSpeed = 0.8f;
    const float RotateDegreesPerSecond = 270f;
    const float LookMouseUnitsPerSecond = 300f;
    const float MoveGain = 0.35f;
    const float MoveTolerance = 0.006f;
    const float MoveTimeout = 15f;
    const float MaxSettledPositionError = 0.02f;
    const float MaxSettledAngleError = 5f;
    const float HeldViewDistance = 0.38f;
    const float MaxRestSpeed = 0.03f;
    const float MaxRestAngularSpeed = 0.2f;
    const float LookDownMouseUnits = -150f;
    const float MaxPressSinkRatio = 0.5f;
    const float PressDepth = 0.12f;

    public ProductPrefab product;
    public ScannerZone scanner;
    public float benchTopY;
    public Vector3 benchMin, benchMax;
    public int expectedSplatCount;
    public Material wireMaterial;

    [Serializable] class Stage { public string name; public bool pass; public string details; public float time; }

    [Serializable] class Report
    {
        public bool pass;
        public string unity, gpu, api, runtime, scope;
        public bool hmdRuntimeActive;
        public float averageFps;
        public float peakMovingLagMeters;
        public float externalMouseMotion;
        public List<Stage> stages = new List<Stage>();
    }

    readonly Report report = new Report();
    readonly HashSet<Key> heldKeys = new HashSet<Key>();
    Keyboard keyboard;
    Mouse mouse;
    Vector2 mouseDelta;
    string output;
    Camera view;
    XRDeviceSimulator simulator;
    NearFarInteractor rightInteractor;
    Transform rightHand;
    XRGrabInteractable grab;
    Rigidbody body;
    LineRenderer wire;
    Vector3 holdOffset;
    Quaternion holdRotation;
    int frames;
    float started;
    bool finished;
    bool trackLag;
    bool pressingIntoBench;
    float peakMovingLag;
    float externalMouseMotion;

#if !UNITY_EDITOR
    // A player holds several product scenes; -p1-scene <name> picks one.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void LoadRequestedScene()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-p1-scene");
        if (index >= 0 && index + 1 < args.Length && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != args[index + 1])
            UnityEngine.SceneManagement.SceneManager.LoadScene(args[index + 1]);
    }
#endif

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-p1-verify") < 0) return;
        // In a player the first scene runs briefly before -p1-scene replaces it.
        int requested = Array.IndexOf(args, "-p1-scene");
        if (requested >= 0 && requested + 1 < args.Length
            && Path.GetFileNameWithoutExtension(args[requested + 1]) != gameObject.scene.name) return;
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        output = Path.Combine(Application.dataPath, "../docs/p1/evidence/dakdari_interaction/editor");
        int index = Array.IndexOf(args, "-p1-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        keyboard = InputSystem.AddDevice<Keyboard>("P1 Verify Keyboard");
        mouse = InputSystem.AddDevice<Mouse>("P1 Verify Mouse");
        // The person's own mouse and keyboard are out of it until the check ends (2026-11-13): see VerificationInput.
        VerificationInput.SilenceAllBut(keyboard, mouse);
        Debug.Log($"[Verification] real input switched off for the check: {VerificationInput.Describe()}");
        started = Time.realtimeSinceStartup;
        StartCoroutine(Run());
    }

    void Update()
    {
        if (keyboard == null) return;
        frames++;
        // The simulator also reads the real mouse, so a person moving it changes the result.
        foreach (var device in InputSystem.devices)
            if (device is Mouse real && real != mouse)
                externalMouseMotion += real.delta.ReadValue().magnitude;
    }

    void FixedUpdate()
    {
        if (!trackLag || !grab.isSelected || pressingIntoBench) return;
        var expected = rightHand.position + rightHand.rotation * holdOffset;
        peakMovingLag = Mathf.Max(peakMovingLag, Vector3.Distance(expected, product.transform.position));
    }

    // Coroutines resume before LateUpdate, so this sends what the current step decided.
    void LateUpdate()
    {
        if (keyboard == null) return;
        var keys = new Key[heldKeys.Count];
        heldKeys.CopyTo(keys);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
        InputSystem.QueueStateEvent(mouse, new MouseState { delta = mouseDelta });
        mouseDelta = Vector2.zero;
    }

    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(3f);
        if (!FindRig()) yield break;

        // ---------- Placement, before any input ----------
        var scale = product.transform.lossyScale;
        var size = product.Proxy.size;
        var splat = product.Splat;
        Check("setup.render", splat && splat.HasValidRenderSetup && splat.m_Asset.splatCount == expectedSplatCount,
            $"splats={splat?.m_Asset?.splatCount}; api={SystemInfo.graphicsDeviceType}");
        Check("setup.negative_scale_only_on_visual", scale == Vector3.one && splat.transform.lossyScale.x < 0f && product.Proxy.transform == product.transform,
            $"root lossyScale={scale.ToString("F3")}; splat lossyScale={splat.transform.lossyScale.ToString("F4")}; collider on root");
        Check("setup.size_flagged_provisional", !product.Data.SizeIsMeasured, $"size_cm={product.Data.SizeCm.ToString("F2")}; measured={product.Data.SizeIsMeasured}");
        Check("setup.barcode_trigger_tagged", HasBarcodeTrigger(), "BarcodeTrigger layer/tag/isTrigger");
        yield return WaitForRest(4f);
        var home = product.transform.position;
        Check("place.at_rest_on_bench", Mathf.Abs(home.y - (benchTopY + size.y * 0.5f)) < 0.01f && IsResting(),
            $"y={home.y:F4}; expected={benchTopY + size.y * 0.5f:F4}; speed={body.linearVelocity.magnitude:F4}");

        // Look down through the simulator's FPS mouse path, so the bench is on screen.
        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        Check("input.look_down_via_mouse", view.transform.forward.y < -0.3f, $"camera forward={view.transform.forward.ToString("F3")}");
        yield return Capture("01_on_bench");
        SetWire(true);
        yield return Capture("01b_on_bench_proxy_wire");
        SetWire(false);

        yield return Tap(Key.Y);
        Check("input.right_controller_manipulated", simulator.manipulatingRightController, "Y toggles the right controller");
        if (!simulator.manipulatingRightController) { Finish(); yield break; }

        // The simulator starts with the mouse rotating the controller; R switches it to translating.
        yield return Tap(Key.R);
        Check("input.mouse_translate_mode", simulator.mouseTransformationMode == XRDeviceSimulator.TransformationMode.Translate,
            $"mode={simulator.mouseTransformationMode}");

        // ---------- First grab ----------
        var handStart = rightHand.position;
        yield return MoveHand(product.transform.position, "reach_1");
        Check("input.hand_moved_by_mouse", Vector3.Distance(handStart, rightHand.position) > 0.1f,
            $"{handStart.ToString("F3")} -> {rightHand.position.ToString("F3")}");
        yield return new WaitForSeconds(0.2f);
        Check("grab.hover_before_grip", grab.isHovered && rightInteractor.interactablesHovered.Contains(grab),
            $"hovered={grab.isHovered}; hovers={rightInteractor.interactablesHovered.Count}");
        yield return GripAndConfirm("grab.first");
        if (!grab.isSelected) { Finish(); yield break; }

        // ---------- Lift ----------
        var liftFrom = product.transform.position;
        yield return MoveProduct(liftFrom + Vector3.up * 0.2f, "lift");
        yield return new WaitForSeconds(SettleSeconds);
        Check("hold.lift_follows", product.transform.position.y - liftFrom.y > 0.15f,
            $"rose {product.transform.position.y - liftFrom.y:F3}m");
        CheckTracking("hold.lift_tracking");

        // ---------- Rotate ----------
        yield return CaptureHeld("02_held_front");
        SetWire(true);
        yield return Capture("02b_held_front_proxy_wire");
        SetWire(false);
        yield return RotateAndCheck(new Vector2(450f, 0f), 90f, "rotate.yaw_90", "03_held_barcode_end");
        SetWire(true);
        yield return Capture("03b_held_barcode_end_proxy_wire");
        SetWire(false);
        yield return RotateAndCheck(new Vector2(450f, 0f), 90f, "rotate.yaw_180", "04_held_back");
        yield return RotateAndCheck(new Vector2(450f, 0f), 90f, "rotate.yaw_270", "05_held_other_end");
        yield return RotateAndCheck(new Vector2(450f, 0f), 90f, "rotate.yaw_360", "06_held_front_again");
        yield return RotateAndCheck(new Vector2(0f, -450f), 90f, "rotate.pitch_underside", "07_held_underside_known_defect");
        yield return RotateAndCheck(new Vector2(0f, 900f), 180f, "rotate.pitch_top", "08_held_top");
        yield return RotateAndCheck(new Vector2(0f, -450f), 90f, "rotate.pitch_upright", "09_held_upright_again");

        // ---------- Barcode rule (trigger volume, not an image reader) ----------
        var zone = scanner.transform.position;
        var localNormal = product.Data.BarcodeLocalNormal.normalized;
        var towardBarcode = product.transform.TransformDirection(localNormal).normalized;
        float halfDepth = Mathf.Abs(Vector3.Dot(size * 0.5f, localNormal));
        // Go over the zone: a straight line from the view center can cross it, barcode first.
        float clearHeight = zone.y + size.magnitude * 0.5f + 0.1f;
        // MoveHand settles the view plane before depth, so the descent first swings this far toward the zone.
        float swing = (clearHeight - zone.y) * Mathf.Abs(view.transform.up.y * Vector3.Dot(view.transform.up, towardBarcode));
        var approach = zone + towardBarcode * (halfDepth + 0.12f + swing);
        var overZone = new Vector3(product.transform.position.x, clearHeight, product.transform.position.z);
        yield return MoveProduct(overZone, "scanner_rise");
        yield return MoveProduct(new Vector3(approach.x, clearHeight, approach.z), "scanner_over");
        yield return MoveProduct(approach, "scanner_approach");
        yield return new WaitForSeconds(0.2f);
        int ignoredBefore = scanner.IgnoredBodyEntries;
        Check("barcode.clear_of_zone", scanner.ScanCount == 0, $"scans={scanner.ScanCount}");

        yield return MoveProduct(zone + towardBarcode * halfDepth, "scanner_body_in");
        yield return new WaitForSeconds(0.3f);
        Check("barcode.body_entry_does_not_scan", scanner.IgnoredBodyEntries > ignoredBefore && scanner.ScanCount == 0,
            $"body entries={scanner.IgnoredBodyEntries - ignoredBefore}; scans={scanner.ScanCount}");
        yield return Capture("10_scanner_body_in_no_scan");

        // Puts the middle of the plate in the zone. A thin product's plate is thinner (P1InteractionTrial.BarcodeThickness).
        yield return MoveProduct(zone - towardBarcode * (halfDepth - Mathf.Min(0.01f, halfDepth * 0.25f)), "scanner_barcode_in");
        yield return new WaitForSeconds(0.3f);
        Check("barcode.face_entry_scans_once", scanner.ScanCount == 1 && scanner.LastScanned == product,
            $"scans={scanner.ScanCount}; last={(scanner.LastScanned ? scanner.LastScanned.Data.Id : "none")}");
        yield return Capture("11_scanner_barcode_in_scanned");

        // ---------- Release onto the bench ----------
        var benchSpot = new Vector3(0.22f, benchTopY + size.y * 0.5f + 0.06f, 0.5f);
        yield return MoveProduct(benchSpot, "over_bench");
        yield return new WaitForSeconds(SettleSeconds);
        yield return ReleaseAndConfirm("release.first");
        yield return WaitForRest(4f);
        var rest = product.transform.position;
        bool onBench = rest.x > benchMin.x && rest.x < benchMax.x && rest.z > benchMin.z && rest.z < benchMax.z;
        float lowestHalf = Mathf.Min(size.x, size.y, size.z) * 0.5f;
        Check("release.rests_on_bench", IsResting() && onBench && rest.y >= benchTopY + lowestHalf - 0.005f && rest.y < benchTopY + size.magnitude,
            $"pos={rest.ToString("F3")}; speed={body.linearVelocity.magnitude:F4}; angular={body.angularVelocity.magnitude:F4}");
        yield return Capture("12_released_on_bench");

        // ---------- Grab again from where it landed ----------
        yield return MoveHand(product.transform.position, "reach_2");
        yield return new WaitForSeconds(0.2f);
        yield return GripAndConfirm("grab.second");
        if (!grab.isSelected) { Finish(); yield break; }
        var secondFrom = product.transform.position;
        yield return MoveProduct(secondFrom + Vector3.up * 0.15f, "second_lift");
        yield return new WaitForSeconds(SettleSeconds);
        Check("hold.second_lift_follows", product.transform.position.y - secondFrom.y > 0.1f,
            $"rose {product.transform.position.y - secondFrom.y:F3}m");
        CheckTracking("hold.second_tracking");
        yield return Capture("13_second_grab_held");

        // A kinematic follow would pass through the bench. A physics-driven proxy must stop on it.
        var benchFree = product.transform.position;
        var pressedInto = new Vector3(benchFree.x, benchTopY + size.y * 0.5f - PressDepth, benchFree.z);
        pressingIntoBench = true;
        yield return MoveProduct(pressedInto, "press_into_bench");
        yield return new WaitForSeconds(SettleSeconds);
        float pressedY = product.transform.position.y;
        float handTargetY = (rightHand.position + rightHand.rotation * holdOffset).y;
        // Measured at the proxy's lowest corner, because pressing can tip a thin product flat.
        float penetration = benchTopY - LowestProxyCornerY();
        // Pass means the bench stopped it: following the hand would sink it by the whole press depth.
        // The penetration is a real soft-contact artifact, so it is recorded instead of hidden.
        Check("hold.proxy_blocked_by_bench", penetration < PressDepth * MaxPressSinkRatio && grab.isSelected,
            $"stopped {pressedY - handTargetY:F3}m above hand target; penetration into bench={penetration:F4}m");
        yield return Capture("13b_pressed_on_bench");
        yield return MoveProduct(benchFree, "lift_off_bench");
        yield return new WaitForSeconds(SettleSeconds);
        pressingIntoBench = false;

        // ---------- Floor drop returns home after 3s ----------
        var offBench = new Vector3(benchMax.x + 0.25f, benchTopY + 0.2f, 0.5f);
        yield return MoveProduct(offBench, "off_bench");
        yield return new WaitForSeconds(0.3f);
        int respawnsBefore = product.RespawnCount;
        yield return ReleaseAndConfirm("release.over_floor");
        float lowest = float.PositiveInfinity;
        float waited = 0f;
        while (product.RespawnCount == respawnsBefore && waited < 8f)
        {
            lowest = Mathf.Min(lowest, product.transform.position.y);
            waited += Time.deltaTime;
            yield return null;
        }
        Check("respawn.fell_to_floor", lowest < size.magnitude, $"lowest y={lowest:F3}");
        Check("respawn.returned_home", product.RespawnCount == respawnsBefore + 1 && Vector3.Distance(product.transform.position, home) < 0.02f,
            $"respawns={product.RespawnCount - respawnsBefore}; after {waited:F2}s; distance from home={Vector3.Distance(product.transform.position, home):F4}");
        yield return WaitForRest(3f);

        // ---------- Grab after respawn ----------
        yield return MoveHand(product.transform.position, "reach_3");
        yield return new WaitForSeconds(0.2f);
        yield return GripAndConfirm("grab.after_respawn");
        if (grab.isSelected)
        {
            yield return new WaitForSeconds(0.3f);
            yield return ReleaseAndConfirm("release.after_respawn");
        }
        Finish();
    }

    // ---------- Input steps ----------

    IEnumerator Tap(Key key)
    {
        heldKeys.Add(key);
        yield return null;
        yield return null;
        heldKeys.Remove(key);
        yield return null;
        yield return null;
    }

    IEnumerator FeedMouse(Vector2 total, float unitsPerSecond)
    {
        var remaining = total;
        while (remaining.sqrMagnitude > 0.01f)
        {
            var step = Vector2.ClampMagnitude(remaining, unitsPerSecond * Time.deltaTime);
            mouseDelta = step;
            remaining -= step;
            yield return null;
        }
    }

    /// <summary>Moves a held product by moving the hand. The product itself lags behind physics, so looping on it would oscillate.</summary>
    IEnumerator MoveProduct(Vector3 target, string label)
    {
        yield return MoveHand(target - (product.transform.position - rightHand.position), label);
    }

    /// <summary>
    /// Closed-loop on the observed hand: the simulator's screen-space mouse moves it across the view,
    /// and holding Z turns mouse motion into depth. A timeout is recorded, not hidden.
    /// </summary>
    IEnumerator MoveHand(Vector3 target, string label)
    {
        float xSens = simulator.mouseXTranslateSensitivity;
        float begun = Time.realtimeSinceStartup;
        while (true)
        {
            var error = target - rightHand.position;
            float across = Vector3.Dot(error, view.transform.right);
            float upward = Vector3.Dot(error, view.transform.up);
            float depth = Vector3.Dot(error, view.transform.forward);
            bool needPlane = Mathf.Abs(across) > MoveTolerance || Mathf.Abs(upward) > MoveTolerance;
            if (!needPlane && Mathf.Abs(depth) <= MoveTolerance) break;
            if (Time.realtimeSinceStartup - begun > MoveTimeout)
            {
                Check("move." + label, false, $"timeout; remaining error={error.magnitude:F4}m; target={target.ToString("F3")}");
                break;
            }
            float maxMouse = MaxHandSpeed * Time.deltaTime / xSens;
            if (needPlane)
            {
                heldKeys.Remove(Key.Z);
                mouseDelta = Vector2.ClampMagnitude(new Vector2(across, upward) * MoveGain / xSens, maxMouse);
            }
            else
            {
                heldKeys.Add(Key.Z);
                mouseDelta = Vector2.ClampMagnitude(new Vector2(depth, 0f) * MoveGain / xSens, maxMouse);
            }
            yield return null;
        }
        heldKeys.Remove(Key.Z);
        mouseDelta = Vector2.zero;
        yield return null;
    }

    IEnumerator GripAndConfirm(string name)
    {
        var positionBefore = product.transform.position;
        var handBefore = rightHand.position;
        float mouseBefore = externalMouseMotion;
        heldKeys.Add(Key.G);
        float begun = Time.realtimeSinceStartup;
        while (!grab.isSelected && Time.realtimeSinceStartup - begun < 2f) yield return null;
        // selectionRegion is updated when the interactor next processes, not at select time.
        yield return new WaitForSeconds(0.3f);
        bool selected = grab.isSelected && grab.interactorsSelecting.Contains(rightInteractor);
        var region = rightInteractor.selectionRegion.Value;
        Check(name + ".selected_by_right_near", selected && region == NearFarInteractor.Region.Near,
            $"selected={selected}; region={region}; interactors={grab.interactorsSelecting.Count}");
        if (!grab.isSelected)
        {
            Debug.LogError($"[P1 Interaction] grip did not select. hand={rightHand.position.ToString("F3")} product={product.transform.position.ToString("F3")} hovered={grab.isHovered}");
            yield break;
        }
        Check(name + ".physics_driven_hold", grab.movementType == XRBaseInteractable.MovementType.VelocityTracking && !body.isKinematic,
            $"movement={grab.movementType}; kinematic={body.isKinematic}");
        var jump = Vector3.Distance(positionBefore, product.transform.position);
        // Remember the hand-relative pose. Dynamic attach should keep it for the whole hold.
        holdOffset = Quaternion.Inverse(rightHand.rotation) * (product.transform.position - rightHand.position);
        holdRotation = Quaternion.Inverse(rightHand.rotation) * product.transform.rotation;
        // Nothing commands the hand here, so hand motion means outside input moved it.
        Check(name + ".no_pose_jump", jump < 0.1f,
            $"product moved {jump:F4}m on grab; hand moved {Vector3.Distance(handBefore, rightHand.position):F4}m; real mouse motion {externalMouseMotion - mouseBefore:F0} units");
        trackLag = true;
    }

    IEnumerator ReleaseAndConfirm(string name)
    {
        trackLag = false;
        heldKeys.Remove(Key.G);
        float begun = Time.realtimeSinceStartup;
        while (grab.isSelected && Time.realtimeSinceStartup - begun < 2f) yield return null;
        Check(name + ".deselected", !grab.isSelected, $"selected={grab.isSelected}");
    }

    IEnumerator RotateAndCheck(Vector2 mouseTotal, float expectedDegrees, string name, string shot)
    {
        var before = product.transform.rotation;
        heldKeys.Add(Key.LeftCtrl);
        yield return null;
        yield return FeedMouse(mouseTotal, RotateDegreesPerSecond / simulator.mouseXRotateSensitivity);
        yield return null;
        heldKeys.Remove(Key.LeftCtrl);
        yield return new WaitForSeconds(SettleSeconds);
        float turned = Quaternion.Angle(before, product.transform.rotation);
        Check(name, grab.isSelected && Mathf.Abs(turned - expectedDegrees) <= MaxSettledAngleError,
            $"product turned {turned:F2} deg; requested {expectedDegrees} deg; still held={grab.isSelected}");
        CheckTracking(name + ".tracking");
        yield return CaptureHeld(shot);
    }

    IEnumerator CaptureHeld(string shot)
    {
        var centered = view.transform.position + view.transform.forward * HeldViewDistance;
        yield return MoveProduct(centered, "center_" + shot);
        yield return new WaitForSeconds(SettleSeconds);
        yield return Capture(shot);
    }

    IEnumerator Capture(string name)
    {
        ScreenCapture.CaptureScreenshot(Path.Combine(output, name + ".png"), Screen.width < 1200 ? 2 : 1);
        yield return null;
        yield return null;
        yield return null;
    }

    IEnumerator WaitForRest(float timeout)
    {
        float begun = Time.realtimeSinceStartup;
        float restingFor = 0f;
        while (restingFor < 0.3f && Time.realtimeSinceStartup - begun < timeout)
        {
            restingFor = IsResting() ? restingFor + Time.deltaTime : 0f;
            yield return null;
        }
    }

    // ---------- State checks ----------

    bool FindRig()
    {
        view = Camera.main;
        simulator = FindFirstObjectByType<XRDeviceSimulator>();
        grab = product.Grab;
        body = product.GetComponent<Rigidbody>();
        foreach (var interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var parent = interactor.transform.parent;
            if (parent != null && parent.name.Contains("Right")) { rightInteractor = interactor; rightHand = parent; }
        }
        bool found = view && simulator && rightInteractor && rightInteractor.isActiveAndEnabled;
        Check("setup.rig_found", found, $"camera={(bool)view}; simulator={(bool)simulator}; rightNearFar={(bool)rightInteractor}; active={(rightInteractor ? rightInteractor.isActiveAndEnabled : false)}");
        if (!found) Finish();
        else BuildWire();
        return found;
    }

    bool HasBarcodeTrigger()
    {
        foreach (var collider in product.GetComponentsInChildren<Collider>())
            if (collider.isTrigger && collider.CompareTag("Barcode") && collider.gameObject.layer == LayerMask.NameToLayer("BarcodeFace"))
                return true;
        return false;
    }

    float LowestProxyCornerY()
    {
        var half = product.Proxy.size * 0.5f;
        float lowest = float.PositiveInfinity;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    lowest = Mathf.Min(lowest, product.transform.TransformPoint(new Vector3(x * half.x, y * half.y, z * half.z)).y);
        return lowest;
    }

    bool IsResting() => body.linearVelocity.magnitude < MaxRestSpeed && body.angularVelocity.magnitude < MaxRestAngularSpeed;

    void CheckTracking(string name)
    {
        var expectedPosition = rightHand.position + rightHand.rotation * holdOffset;
        var expectedRotation = rightHand.rotation * holdRotation;
        float positionError = Vector3.Distance(expectedPosition, product.transform.position);
        float angleError = Quaternion.Angle(expectedRotation, product.transform.rotation);
        Check(name, grab.isSelected && positionError < MaxSettledPositionError && angleError < MaxSettledAngleError,
            $"settled hand-relative error: {positionError:F4}m, {angleError:F2} deg");
    }

    bool Check(string name, bool pass, string details)
    {
        report.stages.Add(new Stage { name = name, pass = pass, details = details, time = Time.realtimeSinceStartup - started });
        Debug.Log($"[P1 Interaction] {(pass ? "PASS" : "FAIL")} {name}: {details}");
        return pass;
    }

    // ---------- Proxy wireframe, drawn only for evidence screenshots ----------

    void BuildWire()
    {
        wire = new GameObject("Proxy Wire (verification only)").AddComponent<LineRenderer>();
        wire.transform.SetParent(product.transform, false);
        wire.useWorldSpace = false;
        wire.sharedMaterial = wireMaterial;
        wire.widthMultiplier = 0.0012f;
        var h = product.Proxy.size * 0.5f;
        Vector3 P(int x, int y, int z) => new Vector3(x * h.x, y * h.y, z * h.z);
        var path = new[]
        {
            P(-1,-1,-1), P(1,-1,-1), P(1,-1,1), P(-1,-1,1), P(-1,-1,-1), P(-1,1,-1), P(1,1,-1), P(1,-1,-1),
            P(1,1,-1), P(1,1,1), P(1,-1,1), P(1,1,1), P(-1,1,1), P(-1,-1,1), P(-1,1,1), P(-1,1,-1),
        };
        wire.positionCount = path.Length;
        wire.SetPositions(path);
        wire.enabled = false;
    }

    void SetWire(bool visible) { if (wire) wire.enabled = visible; }

    // ---------- Finish ----------

    void Finish()
    {
        if (finished) return;
        finished = true;
        heldKeys.Clear();
        report.pass = report.stages.Count > 0 && report.stages.TrueForAll(s => s.pass);
        report.unity = Application.unityVersion;
        report.gpu = SystemInfo.graphicsDeviceName;
        report.api = SystemInfo.graphicsDeviceType.ToString();
        report.runtime = Application.isEditor ? "Editor Play Mode" : "Player";
        report.hmdRuntimeActive = UnityEngine.XR.XRSettings.isDeviceActive;
        report.averageFps = frames / Mathf.Max(Time.realtimeSinceStartup - started, 0.001f);
        report.peakMovingLagMeters = peakMovingLag;
        report.externalMouseMotion = externalMouseMotion;
        report.scope = "XR Device Simulator with virtual keyboard/mouse. No HMD, no physical controllers, no barcode image decoding. FPS is desktop-only and not a VR measurement.";
        File.WriteAllText(Path.Combine(output, "interaction_report.json"), JsonUtility.ToJson(report, true));
        int failed = report.stages.FindAll(s => !s.pass).Count;
        Debug.Log($"[P1 Interaction] {(report.pass ? "PASS" : "FAIL")} checks={report.stages.Count} failed={failed} avgFps={report.averageFps:F1} hmdActive={report.hmdRuntimeActive}");
        VerificationInput.Restore();
        if (keyboard != null) { InputSystem.RemoveDevice(keyboard); keyboard = null; }
        if (mouse != null) { InputSystem.RemoveDevice(mouse); mouse = null; }
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(report.pass ? 0 : 1);
#else
        Application.Quit(report.pass ? 0 : 1);
#endif
    }

    void OnDestroy()
    {
        VerificationInput.Restore();
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
    }
}
