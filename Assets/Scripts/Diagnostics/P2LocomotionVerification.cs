using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Jump;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

/// <summary>
/// Runs only with -p2-locomotion-verify. Walks and turns through virtual Keyboard and Mouse devices, the same path as
/// the XR Device Simulator's user, and only reads state.
///
/// While one hand is manipulated, the simulator sends I/J/K/L to the other hand's stick and W/A/S/D to the manipulated
/// hand's stick. So with the right hand selected (Y), I/J/K/L is the left stick and W/A/S/D the right stick. In FPS mode
/// W/A/S/D moves the HMD itself, which does not go through a provider: that is the room-scale path, not stick movement.
///
/// This is simulator evidence. The keys give a full stick deflection only. It says nothing about a real thumbstick,
/// how the movement feels, or sickness.
/// </summary>
public class P2LocomotionVerification : MonoBehaviour
{
    // Input right after entering Play is dropped while the editor settles.
    const float StartupSeconds = 4f;
    // Near what an HMD shows. At the default 60 degrees the screen ends before the vignette begins.
    const float ViewFieldOfView = 90f;
    const float LookMouseUnitsPerSecond = 300f;

    const float Speed = 1.5f;
    const float SpeedTolerance = 0.03f;
    const float DirectionToleranceDegrees = 1.5f;
    const float SnapDegrees = 45f;
    const float SnapDebounceWait = 0.65f;
    const float SnapTapSeconds = 0.1f;
    const float RestAperture = 1f;
    const float MovingAperture = 0.7f;
    const float ApertureTolerance = 0.01f;
    const float VignetteEaseSeconds = 0.3f;

    // From the layout in P2StoreBuilder (2026-11-09: the reference photo's plan, fridges along the far wall, chilled cases
    // along the front wall, the tobacco wall behind the clerk, one row of gondolas A + B facing the counter).
    const float CounterNearEdge = 0.35f, CounterFarEdge = 0.95f;
    // Behind the clerk is now the front of the tobacco wall. The lane along z = -0.1 clears the waste bin (z up to -0.5).
    const float TobaccoFront = -1.35f, ClerkLaneZ = -0.1f, RightWall = 4.45f;
    const float ShelfAFrontEdge = 2.95f, ShelfAX = -2.65f;
    // The lane up past the end of the counter's extension (x 2.0), the open floor in front of the row (z = 2.0), and gondola B.
    const float RightLaneX = 2.6f, FloorLaneZ = 2f, ShelfBFrontEdge = 2.95f, ShelfBX = -1.15f;
    const float BodyRadius = 0.2f;
    const float SkinWidth = 0.02f;
    const float ContactTolerance = 0.02f;

    [Serializable] class Stage { public string name; public bool pass; public string details; public float time; }

    [Serializable] class Walk
    {
        public string name;
        public float seconds, meanSpeed, firstTenthSecondSpeed, minWindowSpeed, maxWindowSpeed, directionErrorDegrees, headHeightRange, coastMeters, minAperture;
        public string start, end;
    }

    [Serializable] class Report
    {
        public bool pass;
        public string unity, gpu, api, runtime, scope;
        public bool hmdRuntimeActive;
        public float externalMouseMotion;
        public float meanDesktopFps;
        public List<Walk> walks = new List<Walk>();
        public List<Stage> stages = new List<Stage>();
        public List<string> notes = new List<string>();
    }

    readonly Report report = new Report();
    readonly HashSet<Key> heldKeys = new HashSet<Key>();
    Keyboard keyboard;
    Mouse mouse;
    Vector2 mouseDelta;
    string output;
    Camera view;
    XRDeviceSimulator simulator;
    XROrigin rig;
    CharacterController capsule;
    RoomScaleBody body;
    DynamicMoveProvider move;
    SnapTurnProvider snap;
    TunnelingVignetteController vignette;
    Vector2 headAngles;
    CornerBrightness restShot, lastShot;
    float started;
    int frames;
    bool finished;

    Vector3 Origin => rig.Origin.transform.position;
    Vector3 Head => view.transform.position;
    float Aperture => vignette.currentParameters.apertureSize;
    float RigYaw => rig.Origin.transform.eulerAngles.y;

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-p2-locomotion-verify") < 0) return;
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        output = Path.Combine(Application.dataPath, "../docs/p2/evidence/locomotion/editor");
        int index = Array.IndexOf(args, "-p2-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        keyboard = InputSystem.AddDevice<Keyboard>("P2 Locomotion Keyboard");
        mouse = InputSystem.AddDevice<Mouse>("P2 Locomotion Mouse");
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
        foreach (var device in InputSystem.devices)
            if (device is Mouse real && real != mouse)
                report.externalMouseMotion += real.delta.ReadValue().magnitude;
    }

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
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;

        CheckConfiguration();
        yield return Rest();
        yield return Moving();
        yield return Turning();
        yield return Collisions();
        yield return RoomScale();
        Finish();
    }

    // ---------- What the scene is set to ----------

    void CheckConfiguration()
    {
        Check("config.move_provider",
            Mathf.Approximately(move.moveSpeed, Speed) && move.enableStrafe && !move.enableFly && move.leftHandMovementDirection == DynamicMoveProvider.MovementDirection.HeadRelative,
            $"speed={move.moveSpeed}; strafe={move.enableStrafe}; fly={move.enableFly}; left={move.leftHandMovementDirection}");
        Check("config.snap_turn",
            Mathf.Approximately(snap.turnAmount, SnapDegrees) && snap.enableTurnLeftRight && !snap.enableTurnAround,
            $"amount={snap.turnAmount}; leftRight={snap.enableTurnLeftRight}; turnAround={snap.enableTurnAround}");

        int player = LayerMask.NameToLayer("Player");
        var touched = new List<string>();
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(player, layer))
                touched.Add(LayerMask.LayerToName(layer) + "#" + layer);
        Check("config.capsule",
            capsule.gameObject.layer == player && player == 14 && Mathf.Approximately(capsule.radius, BodyRadius) && Mathf.Approximately(capsule.skinWidth, SkinWidth) && touched.Count == 1 && touched[0] == "Default#0",
            $"layer={capsule.gameObject.layer}; radius={capsule.radius}; skin={capsule.skinWidth}; collidesWith={string.Join(",", touched)}");

        var transformer = FindFirstObjectByType<XRBodyTransformer>();
        Check("config.body_follows_with_collision", ReferenceEquals(transformer.bodyPositionEvaluator, body), $"evaluator={transformer.bodyPositionEvaluator?.GetType().Name}");

        var active = new List<string>();
        foreach (var provider in FindObjectsByType<LocomotionProvider>(FindObjectsSortMode.None))
            if (provider.isActiveAndEnabled) active.Add(provider.GetType().Name);
        active.Sort();
        bool onlyWanted = string.Join(",", active) == "DynamicMoveProvider,GravityProvider,SnapTurnProvider";
        Check("config.only_wanted_providers", onlyWanted, $"active={string.Join(",", active)}");
        Check("config.no_teleport_climb_jump",
            FindAnyObjectByType<TeleportationProvider>() == null && FindAnyObjectByType<ClimbProvider>() == null && FindAnyObjectByType<JumpProvider>() == null && !HasActiveTeleportRay(),
            $"teleportRayActive={HasActiveTeleportRay()}");

        var leftMove = move.leftHandMoveInput.inputActionReference.action;
        var rightMove = move.rightHandMoveInput.inputActionReference.action;
        var leftTurn = snap.leftHandTurnInput.inputActionReference.action;
        var rightTurn = snap.rightHandTurnInput.inputActionReference.action;
        Check("config.sticks",
            leftMove.enabled && !rightMove.enabled && rightTurn.enabled && !leftTurn.enabled,
            $"leftMove={leftMove.enabled}[{Controls(leftMove)}]; rightMove={rightMove.enabled}; rightTurn={rightTurn.enabled}[{Controls(rightTurn)}]; leftTurn={leftTurn.enabled}");
        Check("config.vignette_on_camera",
            vignette.transform.parent == view.transform && vignette.locomotionVignetteProviders.Exists(p => p.enabled && p.locomotionProvider == move) && vignette.locomotionVignetteProviders.Exists(p => p.enabled && p.locomotionProvider == snap)
            && Mathf.Approximately(vignette.defaultParameters.apertureSize, MovingAperture),
            $"parent={vignette.transform.parent.name}; providers={vignette.locomotionVignetteProviders.Count}; aperture={vignette.defaultParameters.apertureSize}");
    }

    static bool HasActiveTeleportRay()
    {
        foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            if (ray.GetType() == typeof(XRRayInteractor)) return true;
        return false;
    }

    static string Controls(InputAction action)
    {
        var paths = new List<string>();
        foreach (var control in action.controls) paths.Add(control.path);
        return string.Join("|", paths);
    }

    // ---------- Standing still ----------

    IEnumerator Rest()
    {
        var before = Origin;
        var head = Head;
        float aperture = Aperture;
        yield return new WaitForSeconds(1f);
        Check("rest.no_drift", Vector3.Distance(before, Origin) < 0.001f && Vector3.Distance(head, Head) < 0.001f,
            $"origin {before.ToString("F4")}->{Origin.ToString("F4")}");
        Check("rest.no_vignette", Mathf.Approximately(aperture, RestAperture) && Mathf.Approximately(Aperture, RestAperture), $"aperture={Aperture}");
        yield return Capture("01_rest");
    }

    // ---------- Left stick ----------

    IEnumerator Moving()
    {
        yield return Look(90f, 0f);
        yield return SelectRightHand();
        Check("move.input_path_is_stick_not_hmd", simulator.manipulatingRightController && !simulator.manipulatingFPS, $"right={simulator.manipulatingRightController}; fps={simulator.manipulatingFPS}");

        // Facing +x, away from the counter, with room in front.
        yield return Capture("02_before_walking");
        restShot = lastShot;
        yield return WalkFor("move.forward", 2f, Key.I, Vector3.right, "03_moving_forward");
        var movingShot = lastShot;
        Check("vignette.draws_dark_corners",
            movingShot.corners < restShot.corners * 0.5f && movingShot.middle > restShot.middle * 0.8f,
            $"corners {restShot.corners:0.000}->{movingShot.corners:0.000}; middle {restShot.middle:0.000}->{movingShot.middle:0.000}");
        yield return new WaitForSeconds(0.6f);
        Check("vignette.gone_after_stopping", Mathf.Approximately(Aperture, RestAperture), $"aperture={Aperture}");
        yield return Capture("04_stopped_after_walking");
        var stoppedShot = lastShot;
        Check("vignette.corners_lit_again", stoppedShot.corners > movingShot.corners * 2f, $"corners moving={movingShot.corners:0.000}; stopped={stoppedShot.corners:0.000}");
        yield return WalkFor("move.backward", 2f, Key.K, Vector3.left, null);
        // 0.7 s: the tobacco wall stands 1.35 m behind the start.
        yield return WalkFor("move.strafe_right", 0.7f, Key.L, Vector3.back, null);
        yield return WalkFor("move.strafe_left", 0.7f, Key.J, Vector3.forward, null);
        // The diagonal must not add up to more than the single-axis speed.
        yield return WalkFor("move.diagonal_keeps_speed", 1f, new[] { Key.I, Key.L }, (Vector3.right + Vector3.back).normalized, null);
        yield return WalkFor("move.diagonal_back", 1f, new[] { Key.K, Key.J }, (Vector3.left + Vector3.forward).normalized, null);

        // Looking down and to the side: the direction follows the head's yaw, and looking down neither slows nor lifts.
        yield return SelectFps();
        yield return Look(135f, 30f);
        yield return SelectRightHand();
        var heading = Quaternion.Euler(0f, 135f, 0f) * Vector3.forward;
        Check("move.head_turned", Vector3.Angle(Flat(view.transform.forward), heading) < 1f && view.transform.forward.y < -0.4f, $"forward={view.transform.forward.ToString("F3")}");
        yield return WalkFor("move.follows_head_looking_down", 1f, Key.I, heading, "05_moving_looking_down");
        yield return WalkFor("move.back_looking_down", 1f, Key.K, -heading, null);

        yield return SelectFps();
        yield return Look(0f, 0f);
        yield return SelectRightHand();
        yield return new WaitForSeconds(0.2f);
    }

    IEnumerator WalkFor(string name, float seconds, Key key, Vector3 expected, string captureName) =>
        WalkFor(name, seconds, new[] { key }, expected, captureName);

    /// <summary>Holds keys and measures the rig. Asks for a straight, constant speed from the first moment, with no coasting.</summary>
    IEnumerator WalkFor(string name, float seconds, Key[] keys, Vector3 expected, string captureName)
    {
        var start = Origin;
        foreach (var key in keys) heldKeys.Add(key);

        float begun = Time.realtimeSinceStartup;
        while (Vector3.Distance(Origin, start) < 1e-4f && Time.realtimeSinceStartup - begun < 1f) yield return null;
        var moving = Origin;
        float movingSince = Time.time;
        float elapsed = 0f, firstTenth = -1f, windowElapsed = 0f;
        float minHead = Head.y, maxHead = Head.y, minAperture = Aperture;
        float minWindow = float.PositiveInfinity, maxWindow = 0f;
        var windowStart = moving;
        bool captured = captureName == null;
        // Averaged over 50 ms because frame times differ and the provider and the transformer see different ones.
        const float Window = 0.05f;
        while (elapsed < seconds)
        {
            yield return null;
            elapsed = Time.time - movingSince;
            windowElapsed += Time.deltaTime;
            minHead = Mathf.Min(minHead, Head.y);
            maxHead = Mathf.Max(maxHead, Head.y);
            minAperture = Mathf.Min(minAperture, Aperture);
            if (firstTenth < 0f && elapsed >= 0.1f) firstTenth = Vector3.Distance(Origin, moving) / elapsed;
            if (windowElapsed >= Window)
            {
                float speed = Vector3.Distance(Origin, windowStart) / windowElapsed;
                minWindow = Mathf.Min(minWindow, speed);
                maxWindow = Mathf.Max(maxWindow, speed);
                windowStart = Origin;
                windowElapsed = 0f;
            }
            if (!captured && elapsed >= VignetteEaseSeconds + 0.2f)
            {
                captured = true;
                float atCapture = Aperture;
                yield return Capture(captureName);
                yield return null;
                Check(name + ".vignette_while_moving", Mathf.Abs(atCapture - MovingAperture) < ApertureTolerance, $"aperture={atCapture}");
            }
        }
        var end = Origin;
        float travelled = Vector3.Distance(end, moving);
        foreach (var key in keys) heldKeys.Remove(key);

        // Keys take a frame or two to arrive, so a few frames of the same speed are expected. Anything more is coasting.
        yield return new WaitForSeconds(0.25f);
        float coast = Vector3.Distance(Origin, end);

        var direction = Flat(end - moving);
        var walk = new Walk
        {
            name = name,
            seconds = elapsed,
            meanSpeed = travelled / elapsed,
            firstTenthSecondSpeed = firstTenth,
            minWindowSpeed = minWindow,
            maxWindowSpeed = maxWindow,
            directionErrorDegrees = Vector3.Angle(direction, expected),
            headHeightRange = maxHead - minHead,
            coastMeters = coast,
            minAperture = minAperture,
            start = start.ToString("F3"),
            end = Origin.ToString("F3"),
        };
        report.walks.Add(walk);
        bool speedOk = Mathf.Abs(walk.meanSpeed - Speed) < SpeedTolerance;
        bool noRamp = Mathf.Abs(walk.firstTenthSecondSpeed - Speed) < Speed * 0.1f;
        bool level = walk.headHeightRange < 0.001f && Mathf.Abs(end.y - start.y) < 0.001f;
        bool straight = walk.directionErrorDegrees < DirectionToleranceDegrees;
        bool stops = walk.coastMeters < Speed * 0.1f;
        Check(name,
            speedOk && noRamp && level && straight && stops && walk.minAperture < RestAperture - 0.05f,
            $"mean={walk.meanSpeed:0.000} m/s over {walk.seconds:0.00}s; first0.1s={walk.firstTenthSecondSpeed:0.000}; window={walk.minWindowSpeed:0.000}..{walk.maxWindowSpeed:0.000}; direction error={walk.directionErrorDegrees:0.00} deg; head height range={walk.headHeightRange:0.0000} m; coast={walk.coastMeters:0.000} m; minAperture={walk.minAperture:0.000}; {walk.start}->{walk.end}");
    }

    // ---------- Right stick ----------

    IEnumerator Turning()
    {
        var head = Head;
        float yaw = RigYaw;
        yield return TapStick(Key.D, "06_snap_turn_vignette");
        Check("snap.right_45", Mathf.Abs(Mathf.DeltaAngle(yaw + SnapDegrees, RigYaw)) < 0.01f, $"yaw {yaw:0.00}->{RigYaw:0.00}");
        Check("snap.head_stays_in_place", Flat(Head - head).magnitude < 0.002f, $"head moved {Flat(Head - head).magnitude:0.0000} m");

        // A full circle one step at a time, then back: each step is exactly 45, and the errors do not add up.
        for (int i = 1; i < 8; i++)
        {
            float previous = RigYaw;
            yield return TapStick(Key.D, null);
            Check($"snap.step_{i}", Mathf.Abs(Mathf.DeltaAngle(previous + SnapDegrees, RigYaw)) < 0.01f, $"yaw {previous:0.00}->{RigYaw:0.00}");
        }
        Check("snap.full_circle", Mathf.Abs(Mathf.DeltaAngle(yaw, RigYaw)) < 0.05f, $"after 8 steps {yaw:0.00}->{RigYaw:0.00}");
        float beforeLeft = RigYaw;
        yield return TapStick(Key.A, null);
        Check("snap.left_45", Mathf.Abs(Mathf.DeltaAngle(beforeLeft - SnapDegrees, RigYaw)) < 0.01f, $"yaw {beforeLeft:0.00}->{RigYaw:0.00}");

        // The head turned with the rig, so what is ahead after a turn is the rig's yaw plus the head's own.
        var heading = Flat(view.transform.forward).normalized;
        Check("snap.head_turned_with_rig", Vector3.Angle(heading, Quaternion.Euler(0f, -SnapDegrees, 0f) * Vector3.forward) < 1f, $"heading={heading.ToString("F3")}");
        yield return TapStick(Key.D, null);
        yield return SelectFps();
        yield return Look(135f, 0f);
        yield return SelectRightHand();
        yield return TapStick(Key.A, null);
        var turnedHeading = Flat(view.transform.forward).normalized;
        Check("snap.head_yaw_plus_rig_yaw", Vector3.Angle(turnedHeading, Quaternion.Euler(0f, 135f - SnapDegrees, 0f) * Vector3.forward) < 1f, $"heading={turnedHeading.ToString("F3")}");
        yield return WalkFor("move.after_snap_follows_head", 1f, Key.I, turnedHeading, null);
        yield return WalkFor("move.after_snap_back", 1f, Key.K, -turnedHeading, null);
        yield return TapStick(Key.D, null);
        yield return SelectFps();
        yield return Look(0f, 0f);
        yield return SelectRightHand();

        // Holding repeats at the debounce interval, always by whole steps.
        float heldFrom = RigYaw;
        heldKeys.Add(Key.D);
        yield return new WaitForSeconds(1.3f);
        heldKeys.Remove(Key.D);
        yield return new WaitForSeconds(SnapDebounceWait);
        float turned = Mathf.Repeat(RigYaw - heldFrom, 360f);
        int steps = Mathf.RoundToInt(turned / SnapDegrees);
        Check("snap.hold_repeats_whole_steps", steps >= 2 && Mathf.Abs(turned - steps * SnapDegrees) < 0.05f, $"held 1.3 s: {steps} steps, {turned:0.00} deg");
        for (int i = 0; i < steps; i++) yield return TapStick(Key.A, null);
        Check("snap.back_to_start", Mathf.Abs(Mathf.DeltaAngle(heldFrom, RigYaw)) < 0.05f, $"yaw={RigYaw:0.00}");

        // Stick forward used to aim a teleport, and stick back used to turn around.
        var origin = Origin;
        yaw = RigYaw;
        heldKeys.Add(Key.W);
        yield return new WaitForSeconds(0.5f);
        bool teleportRay = HasActiveTeleportRay();
        heldKeys.Remove(Key.W);
        yield return new WaitForSeconds(0.2f);
        Check("snap.stick_forward_does_nothing", Vector3.Distance(origin, Origin) < 0.001f && Mathf.Abs(Mathf.DeltaAngle(yaw, RigYaw)) < 0.01f && !teleportRay, $"moved={Vector3.Distance(origin, Origin):0.0000}; yaw={RigYaw:0.00}; teleportRay={teleportRay}");
        heldKeys.Add(Key.S);
        yield return new WaitForSeconds(0.5f);
        heldKeys.Remove(Key.S);
        yield return new WaitForSeconds(0.2f);
        Check("snap.stick_back_does_not_turn_around", Vector3.Distance(origin, Origin) < 0.001f && Mathf.Abs(Mathf.DeltaAngle(yaw, RigYaw)) < 0.01f, $"yaw={RigYaw:0.00}");
    }

    /// <summary>A short push and release, as a thumb does. The vignette must still fade in fully and then back out.</summary>
    IEnumerator TapStick(Key key, string captureName)
    {
        float faded = Time.time;
        while (!Mathf.Approximately(Aperture, RestAperture) && Time.time - faded < 1f) yield return null;
        float minAperture = Aperture;
        float start = Aperture;
        heldKeys.Add(key);
        yield return new WaitForSeconds(SnapTapSeconds);
        heldKeys.Remove(key);
        float begun = Time.time;
        bool captured = captureName == null;
        while (Time.time - begun < SnapDebounceWait)
        {
            minAperture = Mathf.Min(minAperture, Aperture);
            if (!captured && Time.time - begun >= VignetteEaseSeconds - SnapTapSeconds)
            {
                captured = true;
                yield return Capture(captureName);
            }
            yield return null;
        }
        Check("vignette.on_snap_" + key, minAperture < MovingAperture + 0.02f && Mathf.Approximately(Aperture, RestAperture) && Mathf.Approximately(start, RestAperture), $"before={start}; lowest={minAperture:0.000}; after={Aperture}");
    }

    // ---------- Not passing through things ----------

    IEnumerator Collisions()
    {
        var products = FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None);
        var props = new List<(Transform, Vector3)>();
        foreach (var product in products) props.Add((product.transform, product.transform.position));
        foreach (var prop in FindObjectsByType<PaymentProp>(FindObjectsSortMode.None)) props.Add((prop.transform, prop.transform.position));

        // From the start, facing the counter.
        yield return PushAgainst("collide.counter_near_side", Key.I, 1.2f, () => Origin.z + BodyRadius + SkinWidth, CounterNearEdge, "10_counter_near_side");
        var slideFrom = Origin;
        yield return Hold(new[] { Key.I, Key.L }, 0.5f);
        Check("collide.slides_along_counter", Origin.x > slideFrom.x + 0.3f && Mathf.Abs(Origin.z - slideFrom.z) < 0.01f, $"{slideFrom.ToString("F3")}->{Origin.ToString("F3")}");
        yield return Hold(new[] { Key.J }, 0.35f);

        yield return PushAgainst("collide.tobacco_wall", Key.K, 2f, () => Origin.z - BodyRadius - SkinWidth, TobaccoFront, "11_back_wall", true);

        // Back to the lane in front of the waste bin, then to the far wall's fridges and across to the near wall.
        yield return Hold(new[] { Key.I }, (ClerkLaneZ - Origin.z) / Speed);
        yield return PushAgainst("collide.left_wall", Key.J, 4.6f, () => Origin.x - BodyRadius - SkinWidth, FridgeFront(), null, true);
        yield return PushAgainst("collide.right_wall", Key.L, 7.2f, () => Origin.x + BodyRadius + SkinWidth, RightWall, "12_right_wall");

        // Up the lane past the end of the counter's extension to the chilled cases.
        yield return Hold(new[] { Key.J }, (Origin.x - RightLaneX) / Speed);
        yield return PushAgainst("collide.chilled_cases", Key.I, 3.6f, () => Origin.z + BodyRadius + SkinWidth, ChilledFront(), "13_chilled_cases");

        // Back onto the open floor, along it, and up to gondola B.
        yield return Hold(new[] { Key.K }, (Origin.z - FloorLaneZ) / Speed);
        yield return Hold(new[] { Key.J }, (Origin.x - ShelfBX) / Speed);
        yield return PushAgainst("collide.shelf_b", Key.I, 1.5f, () => Origin.z + BodyRadius + SkinWidth, ShelfBFrontEdge, "13b_shelf_b");

        // To the fridges along the open floor.
        yield return Hold(new[] { Key.K }, (Origin.z - FloorLaneZ) / Speed);
        yield return PushAgainst("collide.fridge_wall", Key.J, 3.6f, () => Origin.x - BodyRadius - SkinWidth, FridgeFront(), "13c_fridge_wall", true);

        // Back along the floor to the counter, from the customer side. It must stop on this side.
        yield return Hold(new[] { Key.L }, (0f - Origin.x) / Speed);
        yield return PushAgainst("collide.counter_far_side", Key.K, 1.6f, () => Origin.z - BodyRadius - SkinWidth, CounterFarEdge, null, true);
        Check("collide.still_on_customer_side", Origin.z > CounterFarEdge, $"z={Origin.z:0.000}");

        // Shelf A, from the open floor.
        yield return Hold(new[] { Key.I }, (FloorLaneZ - Origin.z) / Speed);
        yield return Hold(new[] { Key.J }, (Origin.x - ShelfAX) / Speed);
        yield return PushAgainst("collide.shelf", Key.I, 1.5f, () => Origin.z + BodyRadius + SkinWidth, ShelfAFrontEdge, "14_shelf");

        // Around the counter's far end, back to the start.
        yield return Hold(new[] { Key.L }, (-1.6f - Origin.x) / Speed);
        yield return Hold(new[] { Key.K }, Origin.z / Speed);
        yield return Hold(new[] { Key.L }, (0f - Origin.x) / Speed);
        Check("collide.walked_back_to_start", Flat(Origin).magnitude < 0.3f, $"origin={Origin.ToString("F3")}");

        var moved = new List<string>();
        foreach (var (item, position) in props)
            if (item != null && Vector3.Distance(item.position, position) > 0.01f)
                moved.Add($"{item.name} {Vector3.Distance(item.position, position):0.000} m");
        Check("collide.nothing_on_the_counter_moved", moved.Count == 0, $"checked={props.Count}; moved=[{string.Join(", ", moved)}]");
    }

    /// <summary>The fridges' front faces along the far wall (their colliders are built from the model's bounds).</summary>
    static float FridgeFront()
    {
        float front = float.MinValue;
        foreach (var collider in FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
            if (collider.name.StartsWith("Drinks Fridge")) front = Mathf.Max(front, collider.bounds.max.x);
        return front;
    }

    /// <summary>The front face of the chilled cases along the front wall.</summary>
    static float ChilledFront()
    {
        foreach (var collider in FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
            if (collider.name == "Chilled Cases") return collider.bounds.min.z;
        throw new Exception("Chilled Cases missing.");
    }

    /// <summary>
    /// Pushes well past the point of contact. Stopping is not enough, since being stuck also stops: the body's surface
    /// must end up at the obstacle, and must never have gone past it on the way.
    /// </summary>
    IEnumerator PushAgainst(string name, Key key, float seconds, Func<float> bodySurface, float obstacle, string captureName, bool surfaceIsLowSide = false)
    {
        var start = Origin;
        heldKeys.Add(key);
        float worst = float.NegativeInfinity;
        float begun = Time.time;
        while (Time.time - begun < seconds)
        {
            yield return null;
            float past = surfaceIsLowSide ? obstacle - bodySurface() : bodySurface() - obstacle;
            worst = Mathf.Max(worst, past);
        }
        var end = Origin;
        bool hit = (capsule.collisionFlags & CollisionFlags.Sides) != 0;
        heldKeys.Remove(key);
        yield return new WaitForSeconds(0.45f);
        Check(name,
            Mathf.Abs(worst) < ContactTolerance && hit,
            $"{start.ToString("F3")}->{end.ToString("F3")}; surface vs obstacle at {obstacle}: worst past={worst:0.0000} m; sideHit={hit}; pushed for {seconds:0.0}s");
        if (captureName != null) yield return Capture(captureName);
    }

    IEnumerator Hold(Key[] keys, float seconds)
    {
        foreach (var key in keys) heldKeys.Add(key);
        yield return new WaitForSeconds(Mathf.Max(0f, seconds));
        foreach (var key in keys) heldKeys.Remove(key);
        yield return new WaitForSeconds(0.1f);
    }

    // ---------- Real walking ----------

    IEnumerator RoomScale()
    {
        // In FPS mode W/A/S/D moves the simulated HMD itself, as a step in the room does. No provider is involved.
        yield return SelectFps();
        var origin = Origin;
        var head = Head;
        yield return Hold(new[] { Key.S, Key.A }, 0.25f);
        Check("roomscale.head_moves_rig_does_not",
            Vector3.Distance(origin, Origin) < 0.001f && Flat(Head - head).magnitude > 0.2f,
            $"origin moved {Vector3.Distance(origin, Origin):0.0000}; head moved {Flat(Head - head).magnitude:0.000}");
        var local = rig.Origin.transform.InverseTransformPoint(Head);
        Check("roomscale.capsule_follows_head",
            Mathf.Abs(capsule.center.x - local.x) < 0.002f && Mathf.Abs(capsule.center.z - local.z) < 0.002f && body.Lean.magnitude < 0.002f,
            $"capsule center={capsule.center.ToString("F3")}; head local={local.ToString("F3")}; lean={body.Lean.magnitude:0.0000}");
        yield return Capture("20_roomscale_step");

        // With the head behind the rig's origin, the stick must stop the body under the head, not under the origin.
        yield return SelectRightHand();
        yield return PushAgainst("roomscale.stick_stops_under_head", Key.I, 1.2f, () => Head.z + BodyRadius + SkinWidth, CounterNearEdge, "21_roomscale_counter");

        // The head height drives the capsule height.
        Check("roomscale.capsule_height_follows_head",
            Mathf.Abs(capsule.height - rig.CameraInOriginSpaceHeight) < 0.02f,
            $"capsule height={capsule.height:0.000}; head height={rig.CameraInOriginSpaceHeight:0.000}");

        // Leaning over the counter from where the body touches it. Nothing else may move.
        yield return Hold(new[] { Key.K }, 0.1f);
        yield return new WaitForSeconds(0.2f);
        yield return Hold(new[] { Key.I }, 0.3f);
        yield return new WaitForSeconds(0.2f);
        yield return SelectFps();
        float leanFrom = Head.z;
        var bodyAtCounter = body.BodyWorldPosition;
        var originAtCounter = Origin;
        yield return Hold(new[] { Key.W }, 0.15f);
        yield return new WaitForSeconds(0.1f);
        Check("roomscale.lean_over_counter_moves_only_the_head",
            Head.z > leanFrom + 0.1f && Vector3.Distance(Origin, originAtCounter) < 0.001f && Mathf.Abs(body.BodyWorldPosition.z - bodyAtCounter.z) < 0.002f,
            $"head z {leanFrom:0.000}->{Head.z:0.000}; origin moved {Vector3.Distance(Origin, originAtCounter):0.0000}; body {bodyAtCounter.ToString("F4")}->{body.BodyWorldPosition.ToString("F4")}");
        yield return Capture("22_lean_over_counter");

        // A snap turn while leaning swings the head around the body. The body may follow along the counter, not into it.
        yield return SelectRightHand();
        yield return TapStick(Key.D, null);
        Check("roomscale.snap_while_leaning_keeps_body_out_of_counter", body.BodyWorldPosition.z + BodyRadius < CounterNearEdge, $"body {bodyAtCounter.ToString("F3")}->{body.BodyWorldPosition.ToString("F3")}; capsule surface z={body.BodyWorldPosition.z + BodyRadius:0.000}; counter at {CounterNearEdge}");
        yield return TapStick(Key.A, null);

        // Stick input with the head over the counter. It used to throw the player out past the far side.
        var beforeStick = Head;
        yield return Hold(new[] { Key.L }, 0.3f);
        yield return new WaitForSeconds(0.2f);
        Check("roomscale.stick_while_leaning_does_not_jump",
            Mathf.Abs(Head.z - beforeStick.z) < 0.01f && Mathf.Abs(Head.x - beforeStick.x - Speed * 0.3f) < 0.1f && body.BodyWorldPosition.z < CounterNearEdge,
            $"head {beforeStick.ToString("F3")}->{Head.ToString("F3")}; body z={body.BodyWorldPosition.z:0.000}");
        yield return Capture("23_stick_while_leaning");

        // A real step that keeps going. The body stays, the head stops at the lean limit and the rig slides back.
        yield return SelectFps();
        bodyAtCounter = body.BodyWorldPosition;
        originAtCounter = Origin;
        yield return Hold(new[] { Key.W }, 1f);
        yield return new WaitForSeconds(0.1f);
        Check("roomscale.step_cannot_pass_counter",
            Head.z < bodyAtCounter.z + body.MaxLean + 0.005f && Mathf.Abs(body.BodyWorldPosition.z - bodyAtCounter.z) < 0.002f && Origin.z < originAtCounter.z - 0.5f,
            $"head z={Head.z:0.000}; body z {bodyAtCounter.z:0.000}->{body.BodyWorldPosition.z:0.000}; origin z {originAtCounter.z:0.000}->{Origin.z:0.000}; counter near edge at {CounterNearEdge}");
        yield return Capture("24_step_cannot_pass_counter");

        // Stepping back: the capsule is under the head again.
        yield return Hold(new[] { Key.S }, 0.5f);
        yield return new WaitForSeconds(0.1f);
        Check("roomscale.step_back_body_under_head", body.Lean.magnitude < 0.005f, $"lean={body.Lean.magnitude:0.0000}");

        // The same at a wall, where the head must not reach the surface. Then along the wall, which is free.
        yield return SelectRightHand();
        yield return PushAgainst("roomscale.walk_to_wall", Key.L, 3.4f, () => Head.x + BodyRadius + SkinWidth, RightWall, null);
        yield return SelectFps();
        var bodyAtWall = body.BodyWorldPosition;
        yield return Hold(new[] { Key.D }, 1f);
        yield return new WaitForSeconds(0.1f);
        Check("roomscale.step_cannot_pass_wall",
            Head.x < RightWall && Mathf.Abs(body.BodyWorldPosition.x - bodyAtWall.x) < 0.002f,
            $"head x={Head.x:0.000}; wall at {RightWall}; body x {bodyAtWall.x:0.000}->{body.BodyWorldPosition.x:0.000}");
        yield return Capture("25_step_cannot_pass_wall");
        var bodyBeforeSlide = body.BodyWorldPosition;
        yield return Hold(new[] { Key.S }, 0.5f);
        yield return new WaitForSeconds(0.1f);
        Check("roomscale.body_slides_along_wall",
            body.BodyWorldPosition.z < bodyBeforeSlide.z - 0.3f && Mathf.Abs(body.BodyWorldPosition.x - bodyBeforeSlide.x) < 0.002f,
            $"body {bodyBeforeSlide.ToString("F3")}->{body.BodyWorldPosition.ToString("F3")}");
    }

    // ---------- Input ----------

    IEnumerator SelectRightHand()
    {
        if (!simulator.manipulatingRightController) yield return Tap(Key.Y);
        if (!simulator.manipulatingRightController) Check("input.right_hand_selected", false, "Y did not select the right hand");
    }

    IEnumerator SelectFps()
    {
        if (simulator.manipulatingRightController) yield return Tap(Key.Y);
        if (!simulator.manipulatingFPS) Check("input.fps_selected", false, "Y did not return to FPS");
    }

    IEnumerator Tap(Key key)
    {
        heldKeys.Add(key);
        yield return null;
        yield return null;
        heldKeys.Remove(key);
        yield return null;
        yield return null;
    }

    /// <summary>Only in FPS mode, where the mouse turns the head and takes both hands along.</summary>
    IEnumerator Look(float yaw, float pitch)
    {
        var delta = new Vector2(
            (yaw - headAngles.x) / simulator.mouseXRotateSensitivity,
            -(pitch - headAngles.y) / simulator.mouseYRotateSensitivity);
        headAngles = new Vector2(yaw, pitch);
        var remaining = delta;
        while (remaining.sqrMagnitude > 0.01f)
        {
            var step = Vector2.ClampMagnitude(remaining, LookMouseUnitsPerSecond * Time.deltaTime);
            mouseDelta = step;
            remaining -= step;
            yield return null;
        }
        yield return new WaitForSeconds(0.1f);
    }

    // ---------- Evidence ----------

    class CornerBrightness { public float corners, middle; }

    /// <summary>Dark corners over a lit middle are what a vignette looks like. The aperture value alone would not show it drawing.</summary>
    static CornerBrightness Measure(Texture2D shot)
    {
        var result = new CornerBrightness { middle = shot.GetPixelBilinear(0.5f, 0.5f).grayscale };
        foreach (var (x, y) in new[] { (0.04f, 0.06f), (0.96f, 0.06f), (0.04f, 0.94f), (0.96f, 0.94f) })
            result.corners += shot.GetPixelBilinear(x, y).grayscale * 0.25f;
        return result;
    }

    IEnumerator Capture(string name)
    {
        yield return new WaitForEndOfFrame();
        var shot = ScreenCapture.CaptureScreenshotAsTexture();
        lastShot = Measure(shot);
        File.WriteAllBytes(Path.Combine(output, name + ".png"), shot.EncodeToPNG());
        Destroy(shot);
        Note("shot." + name, $"aperture={Aperture:0.000}; corners={lastShot.corners:0.000}; middle={lastShot.middle:0.000}; origin={Origin.ToString("F3")}; head={Head.ToString("F3")}");
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    bool FindRig()
    {
        view = Camera.main;
        simulator = FindFirstObjectByType<XRDeviceSimulator>();
        rig = FindFirstObjectByType<XROrigin>();
        capsule = rig ? rig.GetComponent<CharacterController>() : null;
        body = rig ? rig.GetComponent<RoomScaleBody>() : null;
        move = FindFirstObjectByType<DynamicMoveProvider>();
        snap = FindFirstObjectByType<SnapTurnProvider>();
        vignette = FindFirstObjectByType<TunnelingVignetteController>();
        bool found = view && simulator && rig && capsule && body && move && snap && vignette;
        Check("setup.rig_found", found, $"camera={(bool)view}; simulator={(bool)simulator}; rig={(bool)rig}; capsule={(bool)capsule}; body={(bool)body}; move={(bool)move}; snap={(bool)snap}; vignette={(bool)vignette}");
        if (!found) Finish();
        return found;
    }

    bool Check(string name, bool pass, string details)
    {
        report.stages.Add(new Stage { name = name, pass = pass, details = details, time = Time.realtimeSinceStartup - started });
        Debug.Log($"[P2 Locomotion] {(pass ? "PASS" : "FAIL")} {name}: {details}");
        return pass;
    }

    /// <summary>A measurement with no pass or fail of its own.</summary>
    void Note(string name, string details)
    {
        report.notes.Add($"{name}: {details}");
        Debug.Log($"[P2 Locomotion] NOTE {name}: {details}");
    }

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
        report.meanDesktopFps = frames / Mathf.Max(0.01f, Time.realtimeSinceStartup - started);
        report.scope = "XR Device Simulator with virtual keyboard/mouse, full stick deflection only. No HMD, no physical controllers, nothing about thumbstick feel or sickness. FPS is desktop-only and not a VR measurement.";
        File.WriteAllText(Path.Combine(output, "locomotion_report.json"), JsonUtility.ToJson(report, true));
        int failed = report.stages.FindAll(s => !s.pass).Count;
        Debug.Log($"[P2 Locomotion] {(report.pass ? "PASS" : "FAIL")} checks={report.stages.Count} failed={failed} mouse={report.externalMouseMotion:0} fps={report.meanDesktopFps:0}");
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
