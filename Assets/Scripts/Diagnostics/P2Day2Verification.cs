using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Day 2 half of P2DayVerification (-p2-day2-verify, with -start-day 2). Same rules as Day 1: only the virtual keyboard
/// and mouse the XR Device Simulator reads, no moving objects, no calling game methods, state is only read. It walks with
/// the left stick and turns with the right, carries the boxes by their hand holes, cuts the tape with the cutter, opens the
/// flaps, shelves every product through the placement preview, recycles both boxes, throws the expired products away,
/// serves the customers and lets one of them run out of time on purpose. -p2-day2-idle instead does nothing at all, to
/// see the first customer come after 90 s and the day end after three customers leave. -p2-next-day (with -p2-verify)
/// presses the "2일차 시작" button after Day 1.
/// This is simulator evidence: nothing about a real HMD, reach, comfort, or how the work feels.
/// </summary>
public partial class P2DayVerification
{
    public IntakeDirector Intake;
    public PlacementPreview Preview;
    public IntakeTutorial IntakeTutorial;
    public DisposalBinZone Disposal;
    public BoxRecycleZone RecycleBin;

    const int TimeoutOrder = 2;
    const float ArriveTolerance = 0.07f;
    const float WalkTimeout = 25f;
    const float TurnSettle = 0.75f;

    bool day2Mode;
    bool nextDayCheck;
    bool idleMode;
    Transform rig;
    int facingSteps;
    int servedOrders;
    bool falseDisposalDone;
    readonly HashSet<int> arrivalsChecked = new HashSet<int>();

    [Serializable] class Day2Details
    {
        public float tutorialStartedAt, tutorialDoneAt, shiftStartedAt;
        public int shelved, delivered, disposed, expired, misplaced, falseDisposals, recycled, unrecycled;
        public float taskRate, accuracy, salesRate, score;
        public string grade;
        public int bells, hurries, left, paid, orders, sales, possibleSales;
        public bool endedEarly;
        public int splatRenderersAtPeak;
        public long gaussiansAtPeak;
        public List<string> expiredSlots = new List<string>();
        public List<string> customers = new List<string>();
        public List<string> boxes = new List<string>();
        public List<string> fastReleases = new List<string>();
        public int spotsSkippedForClerk;
        public int nudges;
        public int shortArrivalsAtClerk;
    }

    readonly Day2Details details = new Day2Details();

    // ---------- Walking nodes: straight lines between neighbours are clear of everything solid ----------

    // Behind the counter the walk uses a lane along z = -0.3, in front of the bins and the cart, and steps back from it.
    static readonly Dictionary<string, Vector2> Nodes = new Dictionary<string, Vector2>
    {
        { "C", new Vector2(0f, 0f) }, { "KA", new Vector2(0.6f, -0.3f) }, { "KD", new Vector2(2.45f, -0.3f) }, { "D", new Vector2(-0.4f, -0.7f) },
        { "KR", new Vector2(2.6f, -0.3f) }, { "R", new Vector2(2.6f, -0.95f) }, { "KC1", new Vector2(3.22f, -0.3f) }, { "CART1", new Vector2(3.22f, -0.95f) },
        { "KC2", new Vector2(3.78f, -0.3f) }, { "CART2", new Vector2(3.78f, -0.95f) },
        // PR: past the end of the counter's extension.
        { "PR", new Vector2(2.45f, 1.7f) }, { "PL0", new Vector2(-1.6f, 0f) },
        // SA/SB: between a box stand (the row's ends) and the delivery slot (A-2, B-1). SA1/SB2: in front of the other slots.
        { "PL", new Vector2(-1.6f, 1.5f) }, { "SA", new Vector2(-3.25f, 2.6f) }, { "SA1", new Vector2(-2.35f, 2.6f) },
        { "SB", new Vector2(-0.55f, 2.6f) }, { "SB2", new Vector2(-1.25f, 2.6f) },
    };

    static readonly string[][] Edges =
    {
        new[] { "C", "KA" }, new[] { "KA", "KD" }, new[] { "C", "D" }, new[] { "KD", "KR" }, new[] { "KR", "R" }, new[] { "KR", "KC1" },
        new[] { "KC1", "CART1" }, new[] { "KC1", "KC2" }, new[] { "KC2", "CART2" }, new[] { "KD", "PR" },
        new[] { "C", "PL0" }, new[] { "PL0", "PL" },
        new[] { "PL", "SA" }, new[] { "SA", "SA1" }, new[] { "PL", "PR" }, new[] { "PR", "SB" }, new[] { "SB", "SB2" }, new[] { "SA", "SB" },
    };

    string at = "C";

    static List<string> PathBetween(string from, string to)
    {
        var previous = new Dictionary<string, string> { { from, null } };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == to) break;
            foreach (var edge in Edges)
            {
                string next = edge[0] == node ? edge[1] : edge[1] == node ? edge[0] : null;
                if (next == null || previous.ContainsKey(next)) continue;
                previous[next] = node;
                queue.Enqueue(next);
            }
        }
        var path = new List<string>();
        for (var n = to; n != null; n = previous.TryGetValue(n, out var p) ? p : null) path.Insert(0, n);
        return path;
    }

    IEnumerator Go(string node)
    {
        foreach (var step in PathBetween(at, node))
        {
            if (step == at) continue;
            var target = Nodes[step];
            yield return WalkTo(new Vector3(target.x, 0f, target.y), "walk." + step);
            if (finished) yield break;
            at = step;
        }
    }

    /// <summary>Left stick by the I/J/K/L keys, steering the head's floor position to the target each frame.</summary>
    IEnumerator WalkTo(Vector3 target, string label)
    {
        float begun = Time.realtimeSinceStartup;
        while (!finished)
        {
            var head = view.transform.position;
            var error = target - head;
            error.y = 0f;
            if (error.magnitude < ArriveTolerance) break;
            if (Time.realtimeSinceStartup - begun > WalkTimeout)
            {
                Check("move." + label, false, $"timeout walking; head={head.ToString("F2")} target={target.ToString("F2")}");
                break;
            }
            var forward = view.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            float f = Vector3.Dot(error, forward), r = Vector3.Dot(error, right);
            SetKey(Key.I, f > 0.03f);
            SetKey(Key.K, f < -0.03f);
            SetKey(Key.L, r > 0.03f);
            SetKey(Key.J, r < -0.03f);
            yield return null;
        }
        foreach (var key in new[] { Key.I, Key.J, Key.K, Key.L }) heldKeys.Remove(key);
        yield return new WaitForSeconds(0.35f);
    }

    void SetKey(Key key, bool down)
    {
        if (down) heldKeys.Add(key);
        else heldKeys.Remove(key);
    }

    /// <summary>Right stick snap turns: positive steps to the right (D), negative to the left (A).</summary>
    IEnumerator Turn(int steps)
    {
        var key = steps > 0 ? Key.D : Key.A;
        for (int i = 0; i < Mathf.Abs(steps); i++)
        {
            heldKeys.Add(key);
            yield return new WaitForSeconds(0.1f);
            heldKeys.Remove(key);
            yield return new WaitForSeconds(TurnSettle);
        }
        facingSteps += steps;
    }

    /// <summary>
    /// Turns the head to look at a point, in the simulator's body mode (Y toggles it), with nothing in the hand: body mode
    /// would also carry the hand round. The hand is back to its rest rotation first so its Euler angles stay clean.
    /// </summary>
    IEnumerator LookAt(Vector3 point, string label)
    {
        yield return ResetHandRotation();
        yield return Tap(Key.Y);
        for (int pass = 0; pass < 2; pass++)
        {
            var forward = view.transform.forward;
            var wanted = (point - view.transform.position).normalized;
            float yaw = Mathf.DeltaAngle(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, Mathf.Atan2(wanted.x, wanted.z) * Mathf.Rad2Deg);
            float pitch = (-Mathf.Asin(wanted.y) - -Mathf.Asin(forward.y)) * Mathf.Rad2Deg;
            float invert = simulator.mouseYRotateInvert ? 1f : -1f;
            yield return FeedMouse(new Vector2(yaw / simulator.mouseXRotateSensitivity, pitch / (simulator.mouseYRotateSensitivity * invert)), LookMouseUnitsPerSecond * 2f);
            yield return new WaitForSeconds(0.15f);
        }
        yield return Tap(Key.Y);
        float angle = Vector3.Angle(view.transform.forward, point - view.transform.position);
        Check(label, angle < 3f && simulator.manipulatingRightController && simulator.mouseTransformationMode == UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulator.TransformationMode.Translate,
            $"gaze off by {angle:0.0}°; right hand={simulator.manipulatingRightController}; mode={simulator.mouseTransformationMode}");
    }

    // ---------- Day 2 run ----------

    IEnumerator RunDay2()
    {
        idleMode = Array.IndexOf(Environment.GetCommandLineArgs(), "-p2-day2-idle") >= 0;
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        rig = FindFirstObjectByType<XROrigin>().transform;
        CheckNames();
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Check("input.ready", simulator.manipulatingRightController && simulator.mouseTransformationMode == UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulator.TransformationMode.Translate, $"mode={simulator.mouseTransformationMode}");

        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Carry, 20f, "d2.intake_tutorial_started");
        if (finished) yield break;
        CheckSetup();
        yield return Capture("d2_01_day2_start");
        yield return CaptureFrom("d2_02_shelf_a_overview", new Vector3(-1.9f, 1.7f, 1.6f), new Vector3(-3.25f, 1.0f, 3.1f), 60f);
        yield return CaptureFrom("d2_03_cart_overview", new Vector3(2.2f, 1.6f, 0.2f), new Vector3(3.4f, 0.8f, -1.6f), 60f);

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-p2-ghost-shots") >= 0)
        {
            yield return GhostShots();
            Finish();
            yield break;
        }

        if (idleMode)
        {
            yield return Idle();
            Finish();
            yield break;
        }

        yield return TutorialCarry();
        if (finished) yield break;
        yield return TutorialCut();
        if (finished) yield break;
        yield return TutorialOpen();
        if (finished) yield break;
        yield return TutorialShelve();
        if (finished) yield break;
        yield return MisplaceRule();
        if (finished) yield break;
        yield return Go("SA");
        var box1 = Intake.Boxes[0];
        yield return ShelveOne(box1, "d2.box1.extra");
        yield return ServeIfWaiting();

        var box2 = Intake.Boxes[1];
        yield return CarryBox(box2, Intake.Stands[1], "SB", "d2.box2");
        yield return CutBlockedRule(box2);
        yield return ServeIfWaiting();

        yield return Go("SA");
        while (box1.Stage == BoxStage.Open && !finished) yield return ShelveOne(box1, "d2.box1.rest");
        CheckBoxEmpty(box1, "A-2", "d2.box1");
        yield return ServeIfWaiting();
        yield return RecycleBox(box1, "SA", "d2.box1");
        yield return ServeIfWaiting();

        yield return Go("SB");
        yield return Cut(box2, Intake.Cutters[1], "d2.box2.cut", false);
        yield return OpenFlaps(box2, "d2.box2");
        while (box2.Stage == BoxStage.Open && !finished) yield return ShelveOne(box2, "d2.box2.shelve");
        CheckBoxEmpty(box2, "B-1", "d2.box2");
        yield return ServeIfWaiting();
        yield return RecycleBox(box2, "SB", "d2.box2");
        yield return ServeIfWaiting();

        foreach (var expired in new List<ProductPrefab>(Intake.ExpiredAtStart))
        {
            if (finished) yield break;
            yield return DisposeExpired(expired);
            yield return ServeIfWaiting();
        }

        // Whatever orders are left, at the counter.
        yield return Go("C");
        yield return FaceCounter();
        while (!finished && Game.State != DayState.DayEnd)
        {
            yield return ServeIfWaiting(true);
            yield return null;
        }
        if (finished) yield break;
        yield return new WaitForSeconds(0.5f);
        yield return Capture("d2_40_settlement");
        yield return CaptureFrom("d2_41_settlement_board", new Vector3(0f, 1.6f, 0.15f), new Vector3(0f, 1.9f, 1.3f), 55f);
        CheckSettlement();
        yield return WaitUntil(() => Game.Director.Present.Count == 0, EmptyStoreTimeout, "d2.customers.all_left_by_the_door");
        CheckDay2Customers();
        Finish();
    }

    /// <summary>
    /// Evidence for choosing the preview's opacity, not a test of the game: the A-2 product drawn at several opacities,
    /// left to right, standing on the counter, then lying on the shelf where a ghost would appear.
    /// </summary>
    IEnumerator GhostShots()
    {
        var prefab = Intake.Catalog.Find(Intake.SlotsById["A-2"].ProductId);
        float[] values = { 1f, 0.3f, 0.15f, 0.08f, 0.04f };
        var copies = new List<GameObject>();
        for (int i = 0; i < values.Length; i++)
        {
            var copy = PlacementPreview.VisualCopy(prefab, values[i], 5, null);
            copy.transform.SetPositionAndRotation(new Vector3(-0.6f + 0.3f * i, 0.9f + prefab.Proxy.size.y * 0.5f + 0.003f, 0.62f), Quaternion.Euler(0f, 180f, 0f));
            copies.Add(copy);
        }
        yield return new WaitForSeconds(0.5f);
        yield return CaptureFrom("ghost_opacity_counter_1.0_0.3_0.15_0.08_0.04", new Vector3(0f, 1.35f, -0.25f), new Vector3(0f, 1.0f, 0.62f), 55f);
        var slot = Intake.SlotsById["A-2"];
        for (int i = 0; i < 3; i++)
            copies[i + 1].transform.SetPositionAndRotation(slot.RootPosition(prefab, slot.NextFreeInColumn(i)), slot.UnitRotation);
        copies[0].SetActive(false);
        copies[4].SetActive(false);
        yield return new WaitForSeconds(0.5f);
        yield return CaptureFrom("ghost_opacity_shelf_0.3_0.15_0.08", slot.transform.position + new Vector3(0.05f, 0.3f, -0.7f), slot.transform.position + new Vector3(0f, 0.12f, 0.05f), 50f);
        Check("ghost_shots.taken", true, string.Join(", ", values));
    }

    void CheckSetup()
    {
        var director = Game.Director;
        Check("d2.setup.day_two", Game.Day == 2 && Intake.Active && Intake.SkippedDeliveries.Count == 0, $"day={Game.Day}; active={Intake.Active}; skipped=[{string.Join(", ", Intake.SkippedDeliveries)}]");
        bool onCart = Intake.Boxes.Count == 2 && Intake.Boxes.TrueForAll(b => b != null && b.Stage == BoxStage.Sealed && b.Place == BoxPlace.Cart && Mathf.Abs(b.transform.position.y - 0.8f) < 0.02f);
        Check("d2.setup.two_boxes_on_cart", onCart,
            $"boxes=[{string.Join(", ", Intake.Boxes.ConvertAll(b => $"{b.Label} {b.State} y={b.transform.position.y:0.000} inner={b.Inner.ToString("F3")}"))}]");
        var slots = Intake.SlotsById;
        bool targets = slots["A-2"].IsTarget && slots["B-1"].IsTarget && slots["A-2"].Filled == 0 && slots["B-1"].Filled == 0
                       && slots["A-2"].Capacity == 6 && slots["B-1"].Capacity == 4 && slots["A-2"].HighlightShown && slots["B-1"].HighlightShown
                       && !slots["A-1"].IsTarget && !slots["A-1"].HighlightShown;
        Check("d2.setup.target_slots_empty_and_yellow", targets, $"A-2 {slots["A-2"].CountText} lit={slots["A-2"].HighlightShown}; B-1 {slots["B-1"].CountText} lit={slots["B-1"].HighlightShown}");
        var expiredSlots = new List<string>();
        foreach (var product in Intake.ExpiredAtStart)
            foreach (var slot in Intake.Slots)
                if (Array.IndexOf(slot.Occupants, product) >= 0) expiredSlots.Add(slot.SlotId);
        details.expiredSlots = expiredSlots;
        Check("d2.setup.three_expired_not_in_targets", Intake.ShelfStock.Count == 6 && Intake.ExpiredAtStart.Count == 3 && Intake.ExpiredTotal == 3
                && !expiredSlots.Contains("A-2") && !expiredSlots.Contains("B-1") && Intake.ExpiredAtStart.TrueForAll(p => p.IsExpired && p.GetComponentInChildren<Canvas>() != null),
            $"stock={Intake.ShelfStock.Count}; expired in [{string.Join(", ", expiredSlots)}]");
        Check("p4.setup.merged_slots", Array.TrueForAll(Intake.Slots, s => s.Merged != null && s.Merged.ShownUnits == (s.IsTarget ? 0 : s.Capacity)) && Intake.ShelfStock.TrueForAll(p => !p.Splat.enabled),
            string.Join("; ", Array.ConvertAll(Intake.Slots, s => $"{s.SlotId} shown {s.Merged?.ShownUnits}/{s.Capacity} near={s.Merged?.ShowingNear}")));
        Check("d2.setup.guide_shows_step1", Game.BoardText.Contains("입고 1/4") && Intake.GuideTexts.Length == 3 && Array.TrueForAll(Intake.GuideTexts, t => t.text.Contains("입고 1/4") && t.transform.parent.gameObject.activeInHierarchy),
            $"board={Game.BoardText}");
        Check("d2.setup.no_customer_before_tutorial", !director.ShiftRunning && director.Present.Count == 0 && Game.State == DayState.Tutorial, $"shift={director.ShiftRunning}; present={director.Present.Count}; state={Game.State}");
        Check("d2.setup.cutters_in_holders", Intake.Cutters.Length == 2 && Array.TrueForAll(Intake.Cutters, c => c.InHolder), "two cutters");
    }

    // ---------- Intake tutorial ----------

    IEnumerator TutorialCarry()
    {
        var box = IntakeTutorial.Box;
        Check("d2.i1.tutorial_box_is_first", box == Intake.Boxes[0] && box.Delivery.TargetSlot == "A-2", $"{box.Label}");
        int thuds = Game.Pos.Audio.CountOf(StoreSound.Thud);
        yield return CarryBox(box, Intake.Stands[0], "SA", "d2.i1");
        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Cut, 3f, "d2.i1.step_two_shown");
        Check("d2.i1.docked_with_thud", box.State == StockBoxState.Docked && box.DockedAt == Intake.Stands[0] && Game.Pos.Audio.CountOf(StoreSound.Thud) == thuds + 1
                && Vector3.Distance(box.transform.position, Intake.Stands[0].attachTransform.position) < 0.01f && Quaternion.Angle(box.transform.rotation, Intake.Stands[0].attachTransform.rotation) < 1f,
            $"state={box.State}; off seat {Vector3.Distance(box.transform.position, Intake.Stands[0].attachTransform.position):0.000} m, {Quaternion.Angle(box.transform.rotation, Intake.Stands[0].attachTransform.rotation):0.0}°; board={Game.BoardText}");
        yield return Capture("d2_10_box_docked");
    }

    IEnumerator CarryBox(StockBox box, BoxStandSocket stand, string standNode, string label)
    {
        int index = Intake.Boxes.IndexOf(box);
        yield return FaceForward(label);
        yield return Go(index == 0 ? "CART1" : "CART2");
        yield return Turn(4);
        yield return GrabBox(box, label);
        if (finished) yield break;
        Check(label + ".carried", box.State == StockBoxState.Carried && box.Grab.interactorsSelecting.Contains(rightInteractor), $"state={box.State}");
        // Up off the cart, then round to face the store again.
        yield return MoveBox(box, box.transform.position + Vector3.up * 0.15f, label + ".lift");
        yield return Turn(-4);
        yield return Go(standNode);
        var seat = stand.attachTransform.position;
        yield return MoveBox(box, seat + Vector3.up * 0.25f, label + ".over_stand");
        yield return MoveBox(box, seat + Vector3.up * 0.04f, label + ".lower");
        // Instantaneous carry: the box is kinematic in the hand and has no throw, so it is let go without the speed record.
        yield return new WaitForSeconds(SettleBeforeRelease);
        heldKeys.Remove(Key.G);
        yield return WaitUntil(() => box.Place == BoxPlace.Docked, 3f, label + ".docked");
        yield return new WaitForSeconds(0.3f);
    }

    IEnumerator GrabBox(StockBox box, string label)
    {
        Collider handle = box.Handles[0];
        foreach (var h in box.Handles)
            if (Vector3.Distance(h.transform.position, rightHand.position) < Vector3.Distance(handle.transform.position, rightHand.position)) handle = h;
        yield return GrabAt(handle.transform.position, () => box.Grab.interactorsSelecting.Contains(rightInteractor), label + ".grab_handle");
    }

    IEnumerator GrabAt(Vector3 point, Func<bool> selected, string label)
    {
        yield return MoveHand(new Vector3(rightHand.position.x, Mathf.Max(rightHand.position.y, point.y + 0.15f), rightHand.position.z), label + ".rise");
        yield return MoveHand(point + Vector3.up * 0.15f, label + ".over");
        yield return MoveHand(point, label + ".reach");
        yield return new WaitForSeconds(0.2f);
        heldKeys.Add(Key.G);
        yield return WaitUntil(selected, 2f, label);
        yield return new WaitForSeconds(0.2f);
        if (!finished) Check(label + ".near_grab", rightInteractor.selectionRegion.Value == NearFarInteractor.Region.Near, $"region={rightInteractor.selectionRegion.Value}");
    }

    IEnumerator MoveBox(StockBox box, Vector3 target, string label) => MoveHand(target - (box.transform.position - rightHand.position), label);

    /// <summary>SetDown, plus which release it was when something was still moving.</summary>
    IEnumerator Release(Rigidbody held, string label)
    {
        float before = report.maxHeldSpeedAtRelease;
        report.maxHeldSpeedAtRelease = 0f;
        yield return SetDown(held);
        float speed = report.maxHeldSpeedAtRelease;
        if (speed >= SettledSpeed) details.fastReleases.Add($"{label}: {speed:0.000} m/s at {held.name} {held.position.ToString("F3")}");
        report.maxHeldSpeedAtRelease = Mathf.Max(before, speed);
    }

    IEnumerator MoveTip(BoxCutter cutter, Vector3 target, string label) => MoveHand(target - (cutter.Tip.position - rightHand.position), label);

    /// <summary>To 6 cm before the first checkpoint, coming down from 10 cm above it: no checkpoint is touched on the way.</summary>
    IEnumerator ApproachTape(StockBox box, BoxCutter cutter, string label)
    {
        var start = box.CheckpointWorld(0) - box.transform.right * 0.06f;
        yield return MoveTip(cutter, cutter.Tip.position + Vector3.up * 0.1f, label + ".up");
        yield return MoveTip(cutter, start + Vector3.up * 0.1f, label + ".above");
        yield return MoveTip(cutter, start, label + ".down");
    }

    IEnumerator TutorialCut()
    {
        var box = IntakeTutorial.Box;
        var cutter = Intake.Cutters[0];
        yield return GrabAt(cutter.transform.position, () => cutter.Grab.interactorsSelecting.Contains(rightInteractor), "d2.i2.grab_cutter");
        if (finished) yield break;
        // A stroke that stops halfway for over 2 s starts again from nothing (doc 5.7.1 B3).
        yield return ApproachTape(box, cutter, "d2.i2.approach");
        for (int i = 0; i < 3; i++) yield return MoveTip(cutter, box.CheckpointWorld(i), "d2.i2.partial" + i);
        int progress = box.CutProgress;
        yield return new WaitForSeconds(2.6f);
        Check("d2.rule.cut_pause_resets", progress == 3 && box.CutProgress == 0 && box.CutResets == 1 && box.Stage == BoxStage.Sealed,
            $"progress {progress}->{box.CutProgress}; resets={box.CutResets}; stage={box.Stage}");
        yield return ApproachTape(box, cutter, "d2.i2.back");
        yield return Cut(box, cutter, "d2.i2", true);
        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Open, 3f, "d2.i2.step_three_shown");
    }

    /// <summary>Blade tip from one end of the tape to the other, through the six checkpoints in order, then the cutter is let go.</summary>
    IEnumerator Cut(StockBox box, BoxCutter cutter, string label, bool alreadyHeld)
    {
        if (!alreadyHeld)
        {
            yield return GrabAt(cutter.transform.position, () => cutter.Grab.interactorsSelecting.Contains(rightInteractor), label + ".grab_cutter");
            yield return ApproachTape(box, cutter, label + ".approach");
        }
        int ticks = Game.Pos.Audio.CountOf(StoreSound.Tape);
        for (int i = 0; i < box.CheckpointCount && !finished; i++) yield return MoveTip(cutter, box.CheckpointWorld(i), label + ".stroke" + i);
        yield return new WaitForSeconds(0.2f);
        Check(label + ".tape_cut", box.Stage == BoxStage.TapeCut && Game.Pos.Audio.CountOf(StoreSound.Tape) - ticks == box.CheckpointCount && Array.TrueForAll(box.MainFlaps, f => f.Interactable.enabled),
            $"stage={box.Stage}; ticks={Game.Pos.Audio.CountOf(StoreSound.Tape) - ticks}; progress={box.CutProgress}");
        yield return Capture(label.Replace('.', '_') + "_cut");
        // Let go in the air: it drops, and is back in its holder a second later.
        yield return MoveHand(rightHand.position + Vector3.up * 0.15f, label + ".lift_cutter");
        int returns = cutter.Returns;
        heldKeys.Remove(Key.G);
        yield return WaitUntil(() => cutter.InHolder && cutter.Returns > returns, 3f, label + ".cutter_back_in_holder");
    }

    IEnumerator TutorialOpen()
    {
        var box = IntakeTutorial.Box;
        var flap = box.MainFlaps[0];
        // Half way and let go: it falls shut again.
        yield return GrabAt(flap.Panel.transform.position, () => flap.Interactable.interactorsSelecting.Contains(rightInteractor), "d2.rule.flap_half.grab");
        foreach (var degrees in new[] { 30f, 60f, 85f }) yield return MoveHand(flap.PointAt(degrees, 0.07f), "d2.rule.flap_half.turn");
        float reached = flap.Angle;
        heldKeys.Remove(Key.G);
        yield return new WaitForSeconds(0.6f);
        Check("d2.rule.flap_below_110_closes", reached > 70f && reached < BoxFlap.OpenFrom && Mathf.Approximately(flap.Angle, 0f) && !flap.IsOpen && flap.Closes == 1,
            $"reached {reached:0}°, now {flap.Angle:0}°; open={flap.IsOpen}");
        yield return OpenFlaps(box, "d2.i3");
        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Shelve, 3f, "d2.i3.step_four_shown");
    }

    IEnumerator OpenFlaps(StockBox box, string label)
    {
        int flaps = Game.Pos.Audio.CountOf(StoreSound.Flap);
        foreach (var flap in box.MainFlaps)
        {
            yield return GrabAt(flap.Panel.transform.position, () => flap.Interactable.interactorsSelecting.Contains(rightInteractor), label + ".grab_flap");
            foreach (var degrees in new[] { 45f, 90f, 135f, 170f }) yield return MoveHand(flap.PointAt(degrees, 0.07f), label + ".fold");
            float reached = flap.Angle;
            heldKeys.Remove(Key.G);
            yield return new WaitForSeconds(0.5f);
            Check(label + ".flap_folds_open", reached > BoxFlap.OpenFrom && flap.IsOpen && Mathf.Approximately(flap.Angle, BoxFlap.OpenAngle), $"let go at {reached:0}°, now {flap.Angle:0}°");
            yield return MoveHand(rightHand.position + Vector3.up * 0.1f, label + ".hand_up");
        }
        yield return new WaitForSeconds(0.3f);
        int inBox = 0;
        foreach (var unit in box.Units)
            if (unit.Product != null && unit.Product.transform.parent == box.transform && unit.Product.Body.isKinematic) inBox++;
        Check(label + ".open_products_inside", box.Stage == BoxStage.Open && inBox == box.Delivery.CaseQuantity && Game.Pos.Audio.CountOf(StoreSound.Flap) - flaps >= 3,
            $"stage={box.Stage}; products in box={inBox}/{box.Delivery.CaseQuantity}; flap sounds={Game.Pos.Audio.CountOf(StoreSound.Flap) - flaps}");
        yield return CaptureFrom(label.Replace('.', '_') + "_open", box.transform.position + new Vector3(-0.35f, 0.75f, -0.55f), box.transform.position + Vector3.up * 0.15f, 55f);
    }

    BoxUnit NextUnit(StockBox box)
    {
        foreach (var unit in box.Units)
            if (!unit.Done && !unit.Taken && unit.Product != null) return unit;
        return null;
    }

    IEnumerator TutorialShelve()
    {
        var box = IntakeTutorial.Box;
        var slot = Intake.SlotsById["A-2"];

        // A drop with no outline is no penalty: it falls and is back in its box place 3 s after landing.
        var dropped = NextUnit(box);
        yield return GrabAt(dropped.Product.transform.position, () => dropped.Product.Grab.interactorsSelecting.Contains(rightInteractor), "d2.rule.drop.grab");
        int respawns = dropped.Product.RespawnCount;
        yield return MoveProduct(dropped.Product, dropped.Product.transform.position + Vector3.up * 0.25f, "d2.rule.drop.lift");
        // Open floor 0.85 m in front of gondola A, toward the counter from SA. Nearer, the held product's centre could come
        // within the preview's 0.6 m of slot A-2 depending on how it sits in the hand; with the head on the shelf that is a
        // green place, and letting go shelves it instead of dropping it (seen twice, 2026-11-08).
        yield return MoveProduct(dropped.Product, new Vector3(-2.8f, 1.1f, 2.1f), "d2.rule.drop.over_floor");
        float previewGone = Time.realtimeSinceStartup;
        while (Preview.GhostVisible && Time.realtimeSinceStartup - previewGone < 1f) yield return null;
        heldKeys.Remove(Key.G);
        // Not WaitUntil: on a timeout this says where the product ended up and what it rests on before stopping.
        float dropBegun = Time.realtimeSinceStartup;
        while (dropped.Product.RespawnCount <= respawns && Time.realtimeSinceStartup - dropBegun < 8f) yield return null;
        if (dropped.Product.RespawnCount <= respawns)
        {
            var lost = dropped.Product;
            string below = "nothing";
            var hits = Physics.RaycastAll(lost.transform.TransformPoint(lost.Proxy.center) + Vector3.up * 0.3f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider.attachedRigidbody == lost.Body) continue;
                below = $"{hit.collider.name} under {hit.collider.transform.parent?.name} (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}, respawn surface={hit.collider.GetComponent<ProductRespawnSurface>() != null}) at y {hit.point.y:0.000}";
                break;
            }
            Check("d2.rule.drop.returns", false, $"timed out after 8s; product at {lost.transform.position.ToString("F3")}; kinematic={lost.Body.isKinematic}; held={lost.Grab.isSelected}; "
                + $"velocity={lost.Body.linearVelocity.ToString("F3")}; parent={lost.transform.parent?.name}; respawns={lost.RespawnCount}; rests on {below}");
            yield return CaptureFrom("d2_rule_drop_not_back", lost.transform.position + new Vector3(0.6f, 0.9f, -0.9f), lost.transform.position, 55f);
            Finish();
            yield break;
        }
        Check("d2.rule.drop.returns", true, $"after {Time.realtimeSinceStartup - dropBegun:0.0}s");
        yield return new WaitForSeconds(0.3f);
        Check("d2.rule.dropped_back_in_box_no_penalty", dropped.Product.transform.parent == box.transform && dropped.Product.Body.isKinematic && !dropped.Taken && Intake.Misplaced == 0 && !Preview.GhostVisible,
            $"parent={dropped.Product.transform.parent?.name}; misplaced={Intake.Misplaced}");

        // I4 by the held-product point: the head looks at the floor, so its ray meets no slot. In front of the right-hand
        // column the preview goes there; moved to the left-hand column, it follows once that has held 0.2 s.
        // Straight down just in front of SA: the view's right axis is then world +x, so the hand's moves from column to
        // column stay along the shelf and do not swing out of the slot's reach on the way.
        yield return LookAt(new Vector3(-3.25f, 0.05f, 2.75f), "d2.i4.look_at_floor");
        var unit = NextUnit(box);
        var product = unit.Product;
        yield return GrabAt(product.transform.position, () => product.Grab.interactorsSelecting.Contains(rightInteractor), "d2.i4.grab_unit");
        Check("d2.i4.full_product_in_hand", product.Splat != null && product.Splat.m_RenderOrder == 10 && !product.Body.isKinematic, $"order={product.Splat?.m_RenderOrder}; kinematic={product.Body.isKinematic}");
        Check("p4.hand_full_copy", product.Data.LightSplat != null && product.Splat.m_Asset.splatCount > product.Data.LightSplat.splatCount, $"hand={product.Splat.m_Asset.splatCount}; light={product.Data.LightSplat?.splatCount}");
        yield return MoveProduct(product, product.transform.position + Vector3.up * 0.3f, "d2.i4.lift");
        yield return MoveProduct(product, new Vector3(product.transform.position.x, product.transform.position.y, slot.transform.position.z - 0.15f), "d2.i4.to_aisle");
        int right = slot.NextFreeInColumn(slot.Columns - 1);
        yield return MoveProduct(product, slot.UnitPosition(right) + new Vector3(0f, 0.06f, -0.13f), "d2.i4.to_right_column");
        yield return new WaitForSeconds(0.4f);
        Check("d2.i4.ghost_at_held_product", Preview.GhostVisible && Preview.Slot == slot && Preview.Unit == right && !Preview.UsedHeadRay && slot.GreenUnit == right
                && Preview.Ghost != null && Mathf.Approximately(Preview.Ghost.m_OpacityScale, PlacementPreview.GhostOpacity) && Preview.Ghost.m_RenderOrder == PlacementPreview.GhostRenderOrder && !slot.RejectShown,
            $"ghost={Preview.GhostVisible}; slot={Preview.Slot?.SlotId}; unit={Preview.Unit} wanted={right}; headRay={Preview.UsedHeadRay}; opacity={Preview.Ghost?.m_OpacityScale}; order={Preview.Ghost?.m_RenderOrder}");
        Check("p4.ghost_light_copy", Preview.Ghost != null && Preview.Ghost.m_Asset == product.Data.LightSplat, $"ghost gaussians={Preview.Ghost?.m_Asset?.splatCount}");
        yield return CaptureFrom("d2_20_ghost_preview", slot.transform.position + new Vector3(0.25f, 0.35f, -0.65f), slot.transform.position + new Vector3(0f, 0.1f, 0.05f), 50f);
        // From the side, so the product in the hand does not hide the faint copy on the shelf.
        var ghostAt = slot.UnitPosition(right);
        yield return CaptureFrom("d2_20b_ghost_side", ghostAt + new Vector3(-0.42f, 0.18f, -0.3f), ghostAt + new Vector3(0f, 0f, -0.04f), 45f);
        int switches = Preview.Switches;
        int left = slot.NextFreeInColumn(0);
        yield return MoveProduct(product, slot.UnitPosition(left) + new Vector3(0f, 0.06f, -0.13f), "d2.i4.to_left_column");
        yield return new WaitForSeconds(0.5f);
        Check("d2.i4.ghost_follows_after_hold", Preview.GhostVisible && !Preview.UsedHeadRay && Preview.Unit == left && Preview.Switches > switches,
            $"unit={Preview.Unit} wanted={left}; headRay={Preview.UsedHeadRay}; switches {switches}->{Preview.Switches}");
        int target = Preview.Unit;
        yield return Release(product.Body, "release1");
        yield return WaitUntil(() => slot.Filled == 1 && product.IsShelved, 2f, "d2.i4.accepted");
        yield return new WaitForSeconds(0.3f);
        Check("d2.i4.snapped_into_place", Vector3.Distance(product.transform.position, slot.RootPosition(product, target)) < 0.005f && Quaternion.Angle(product.transform.rotation, slot.UnitRotation) < 1f
                && product.Body.isKinematic && !product.Grab.enabled && unit.Done && slot.CountText.Contains("1/6") && Intake.Shelved == 1,
            $"off {Vector3.Distance(product.transform.position, slot.RootPosition(product, target)):0.0000} m; count={slot.CountText}; shelved={Intake.Shelved}");
        Check("p4.shelved_drawn_by_merged", slot.Merged != null && slot.Merged.IsUnitShown(target) && !product.Splat.enabled && slot.Merged.ShownUnits == 1 && slot.Merged.ShowingNear,
            $"unit shown={slot.Merged?.IsUnitShown(target)}; own splat={product.Splat.enabled}; shown units={slot.Merged?.ShownUnits}; near copy={slot.Merged?.ShowingNear}; switches={slot.Merged?.LodSwitches}");
        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Done && Game.State == DayState.Shift, 3f, "d2.i4.tutorial_done_shift_starts");
        details.tutorialStartedAt = IntakeTutorial.StartedAt;
        details.tutorialDoneAt = IntakeTutorial.DoneAt;
        details.shiftStartedAt = Game.ShiftStartedAt;
        Check("d2.i4.first_customer_after_tutorial", Mathf.Abs(Game.ShiftStartedAt - IntakeTutorial.DoneAt) < 0.1f && IntakeTutorial.DoneAt - IntakeTutorial.StartedAt < GameManager.IntakeTutorialMaxSeconds
                && !IntakeTutorial.AutoCutUsed && !IntakeTutorial.BeadsUsed,
            $"tutorial {IntakeTutorial.DoneAt - IntakeTutorial.StartedAt:0.0}s; shift started {Game.ShiftStartedAt - IntakeTutorial.DoneAt:0.00}s after it");
        yield return MoveHand(rightHand.position + Vector3.up * 0.05f, "d2.i4.hand_back");

        // The head ray wins over the held product: look at the right-hand column, hold the product before the middle one.
        int looked = slot.NextFreeInColumn(slot.Columns - 1);
        yield return LookAt(slot.UnitPosition(looked), "d2.i4.look_at_column");
        var second = NextUnit(box);
        yield return GrabAt(second.Product.transform.position, () => second.Product.Grab.interactorsSelecting.Contains(rightInteractor), "d2.head_ray.grab");
        yield return MoveProduct(second.Product, second.Product.transform.position + Vector3.up * 0.3f, "d2.head_ray.lift");
        yield return MoveProduct(second.Product, new Vector3(second.Product.transform.position.x, second.Product.transform.position.y, slot.transform.position.z - 0.15f), "d2.head_ray.to_aisle");
        yield return MoveProduct(second.Product, slot.UnitPosition(slot.NextFreeInColumn(1)) + new Vector3(0f, 0.08f, -0.2f), "d2.head_ray.near_middle");
        yield return new WaitForSeconds(0.4f);
        Check("d2.head_ray.ghost_where_head_looks", Preview.GhostVisible && Preview.UsedHeadRay && Preview.Slot == slot && Preview.Unit == looked && slot.GreenUnit == looked,
            $"ghost={Preview.GhostVisible}; headRay={Preview.UsedHeadRay}; unit={Preview.Unit} wanted={looked}");
        yield return Capture("d2_21_ghost_from_head");
        int filledBefore = slot.Filled;
        yield return Release(second.Product.Body, "release2");
        yield return WaitUntil(() => slot.Filled == filledBefore + 1 && second.Product.IsShelved && slot.IsFilled(looked), 2f, "d2.head_ray.accepted_where_looked");
        yield return new WaitForSeconds(0.25f);
        yield return MoveHand(rightHand.position + Vector3.up * 0.05f, "d2.head_ray.hand_back");
        yield return CaptureFrom("d2_22_first_shelved", slot.transform.position + new Vector3(0.1f, 0.3f, -0.6f), slot.transform.position + new Vector3(0f, 0.12f, 0.05f), 50f);
    }

    /// <summary>One product from the box to its slot, by the held-product point: in front of the slot, wait for green, let go.</summary>
    IEnumerator ShelveOne(StockBox box, string label)
    {
        var unit = NextUnit(box);
        if (unit == null)
        {
            yield return new WaitForSeconds(0.5f);
            yield break;
        }
        var slot = Intake.SlotsById[box.Delivery.TargetSlot];
        var product = unit.Product;
        yield return GrabAt(product.transform.position, () => product.Grab.interactorsSelecting.Contains(rightInteractor), label + ".grab");
        if (finished) yield break;
        yield return MoveProduct(product, product.transform.position + Vector3.up * 0.3f, label + ".lift");
        yield return MoveProduct(product, new Vector3(product.transform.position.x, product.transform.position.y, slot.transform.position.z - 0.15f), label + ".to_aisle");
        int column = -1;
        for (int c = 0; c < slot.Columns && column < 0; c++)
            if (slot.NextFreeInColumn(c) >= 0) column = c;
        int place = slot.NextFreeInColumn(column);
        var unitAt = slot.UnitPosition(place);
        yield return MoveProduct(product, new Vector3(unitAt.x, unitAt.y + 0.06f, slot.transform.position.z - 0.1f), label + ".in_front");
        yield return WaitUntil(() => Preview.GhostVisible && Preview.Slot == slot, 3f, label + ".green");
        if (finished) yield break;
        int filled = slot.Filled;
        yield return Release(product.Body, "release3");
        yield return WaitUntil(() => slot.Filled == filled + 1 && product.IsShelved, 2f, label + ".accepted");
        yield return new WaitForSeconds(0.25f);
        yield return MoveHand(rightHand.position + Vector3.up * 0.05f, label + ".hand_back");
        SamplePeak();
    }

    IEnumerator MisplaceRule()
    {
        var slot = Intake.SlotsById["A-2"];
        var other = Intake.SlotsById["A-1"];
        ProductPrefab stock = null;
        foreach (var p in other.Occupants)
            if (p != null && (stock == null || stock.IsExpired)) stock = p;
        var home = stock.transform.position;
        int errors = Game.Pos.Audio.CountOf(StoreSound.Error);
        int filled = slot.Filled;
        int stockUnit = Array.IndexOf(other.Occupants, stock);
        yield return Go("SA1");
        yield return GrabAt(stock.transform.position, () => stock.Grab.interactorsSelecting.Contains(rightInteractor), "d2.rule.misplace.grab");
        yield return MoveProduct(stock, stock.transform.position + new Vector3(0f, 0.05f, -0.25f), "d2.rule.misplace.out");
        Check("p4.stock_taken_hidden_on_shelf", !other.Merged.IsUnitShown(stockUnit) && stock.Splat.enabled, $"unit {stockUnit} shown={other.Merged.IsUnitShown(stockUnit)}; own splat={stock.Splat.enabled}");
        yield return MoveProduct(stock, slot.UnitPosition(slot.NextFreeInColumn(1)) + new Vector3(0f, 0.08f, -0.1f), "d2.rule.misplace.to_wrong_slot");
        yield return new WaitForSeconds(0.4f);
        Check("d2.rule.wrong_slot_red_outline", Preview.RejectVisible && !Preview.GhostVisible && Preview.Slot == slot && slot.RejectShown && slot.GreenUnit < 0,
            $"red={Preview.RejectVisible}; ghost={Preview.GhostVisible}; slot={Preview.Slot?.SlotId}");
        yield return CaptureFrom("d2_23_red_outline", slot.transform.position + new Vector3(0.2f, 0.35f, -0.65f), slot.transform.position + new Vector3(0f, 0.1f, 0.05f), 55f);
        yield return Release(stock.Body, "release4");
        yield return new WaitForSeconds(0.5f);
        Check("d2.rule.misplaced_counted_and_returned", Intake.Misplaced == 1 && Game.Pos.Audio.CountOf(StoreSound.Error) == errors + 1 && Vector3.Distance(stock.transform.position, home) < 0.01f
                && stock.Body.isKinematic && slot.Filled == filled && !slot.RejectShown,
            $"misplaced={Intake.Misplaced}; errors +{Game.Pos.Audio.CountOf(StoreSound.Error) - errors}; off home {Vector3.Distance(stock.transform.position, home):0.000} m; A-2 {slot.CountText}");
        Check("p4.stock_returned_shown_on_shelf", other.Merged.IsUnitShown(stockUnit) && !stock.Splat.enabled, $"unit {stockUnit} shown={other.Merged.IsUnitShown(stockUnit)}; own splat={stock.Splat.enabled}");
        yield return MoveHand(rightHand.position + Vector3.up * 0.05f, "d2.rule.misplace.hand_back");
    }

    IEnumerator CutBlockedRule(StockBox box)
    {
        var cutter = Intake.Cutters[1];
        yield return GrabAt(cutter.transform.position, () => cutter.Grab.interactorsSelecting.Contains(rightInteractor), "d2.rule.cut_blocked.grab");
        yield return ApproachTape(box, cutter, "d2.rule.cut_blocked.approach");
        for (int i = 0; i < 3; i++) yield return MoveTip(cutter, box.CheckpointWorld(i), "d2.rule.cut_blocked.stroke");
        Check("d2.rule.only_one_open_box", box.Stage == BoxStage.Sealed && box.CutProgress == 0 && box.Notice.Contains("열린 박스") && Intake.OpenBox == Intake.Boxes[0],
            $"stage={box.Stage}; progress={box.CutProgress}; notice={box.Notice}");
        yield return Capture("d2_24_cut_blocked");
        yield return MoveHand(rightHand.position + Vector3.up * 0.15f, "d2.rule.cut_blocked.lift");
        heldKeys.Remove(Key.G);
        yield return WaitUntil(() => cutter.InHolder, 3f, "d2.rule.cut_blocked.cutter_back");
    }

    void CheckBoxEmpty(StockBox box, string slotId, string label)
    {
        var slot = Intake.SlotsById[slotId];
        Check(label + ".empty_and_slot_full", box.Stage == BoxStage.Empty && slot.Filled == slot.Capacity && !slot.HighlightShown && slot.CountText.Contains($"{slot.Capacity}/{slot.Capacity}"),
            $"stage={box.Stage}; {slot.CountText}; lit={slot.HighlightShown}");
        details.boxes.Add($"{box.Label}: {box.State}, {slot.CountText}");
    }

    IEnumerator RecycleBox(StockBox box, string fromNode, string label)
    {
        yield return Go(fromNode);
        yield return FaceForward(label + ".recycle");
        yield return GrabBox(box, label + ".recycle");
        Check(label + ".lifted_off_stand", box.Place == BoxPlace.Carried && box.DockedAt == null && box.Stage == BoxStage.Empty, $"place={box.Place}; stage={box.Stage}");
        yield return MoveBox(box, box.transform.position + Vector3.up * 0.15f, label + ".lift");
        yield return Go("R");
        yield return Turn(4);
        var bin = RecycleBin.transform.position;
        int recycled = Intake.Recycled;
        yield return MoveBox(box, new Vector3(bin.x, 1.05f, bin.z), label + ".over_bin");
        yield return MoveBox(box, new Vector3(bin.x, 0.82f, bin.z), label + ".into_bin");
        heldKeys.Remove(Key.G);
        yield return WaitUntil(() => Intake.Recycled == recycled + 1, 4f, label + ".recycled");
        yield return new WaitForSeconds(0.3f);
        Check(label + ".gone", box == null && Game.Pos.Audio.CountOf(StoreSound.Recycle) == Intake.Recycled, $"recycled={Intake.Recycled}");
        yield return MoveHand(rightHand.position + Vector3.up * 0.1f, label + ".hand_back");
        yield return Turn(-4);
    }

    IEnumerator DisposeExpired(ProductPrefab product)
    {
        // By position: a product taken and put back is no longer listed in its slot.
        bool shelfA = product.transform.position.x < 0f;
        string node = shelfA ? "SA1" : "SB2";
        string label = "d2.dispose." + (shelfA ? "A" : "B") + Intake.Disposed;
        yield return Go(node);
        yield return GrabAt(product.transform.position, () => product.Grab.interactorsSelecting.Contains(rightInteractor), label + ".grab");
        if (finished) yield break;
        yield return MoveProduct(product, product.transform.position + new Vector3(0f, 0.05f, -0.25f), label + ".out");
        yield return Go("D");
        int disposed = Intake.Disposed;
        var bin = Disposal.transform.position;
        yield return MoveProduct(product, new Vector3(bin.x, 1.05f, bin.z), label + ".over_bin");
        yield return Release(product.Body, "release5");
        yield return WaitUntil(() => Intake.Disposed == disposed + 1, 4f, label + ".disposed");
        yield return new WaitForSeconds(0.2f);
        Check(label + ".removed", product == null && Game.Pos.Audio.CountOf(StoreSound.Dispose) == Intake.Disposed, $"disposed={Intake.Disposed}");
        yield return MoveHand(rightHand.position + Vector3.up * 0.1f, label + ".hand_back");
    }

    // ---------- Customers on Day 2 ----------

    /// <summary>Head straight along the rig and tilted down, so a carried box keeps the rig's heading.</summary>
    IEnumerator FaceForward(string label)
    {
        var head = view.transform.position;
        yield return LookAt(head + rig.forward * 1f + Vector3.down * 0.5f, label + ".face_forward");
    }

    IEnumerator FaceCounter()
    {
        yield return LookAt(new Vector3(0f, 0.95f, 0.75f), "d2.look_at_counter");
    }

    /// <summary>
    /// At a pause between tasks: serve whoever stands at the counter, except order 2, which is left to run out of time.
    /// With waitAtCounter the test is already at the counter and simply waits there.
    /// </summary>
    IEnumerator ServeIfWaiting(bool waitAtCounter = false)
    {
        var director = Game.Director;
        while (!finished && director.AtCounter != null && Game.Pos.Current != null && director.CustomerNumber != TimeoutOrder)
        {
            if (at != "C")
            {
                yield return Go("C");
                yield return FaceCounter();
            }
            yield return ServeOrder(director.CustomerNumber);
            yield return new WaitForSeconds(0.3f);
        }
        if (waitAtCounter) yield return null;
    }

    IEnumerator ServeOrder(int number)
    {
        var director = Game.Director;
        var pos = Game.Pos;
        var order = pos.Current;
        CheckDay2Arrival(number, order);
        yield return Capture($"d2_3{Mathf.Min(number, 9)}_order{number}");
        var products = new List<ProductPrefab>(director.OrderProducts);
        if (!falseDisposalDone && director.Left >= 1)
        {
            falseDisposalDone = true;
            yield return FalseDisposal(products[0]);
        }
        for (int i = 0; i < products.Count && !finished; i++)
        {
            var product = products[i];
            string label = $"d2.order{number}.item{i}";
            yield return GrabProduct(product, label);
            yield return TurnBarcodeDown(product);
            yield return MoveHeldAbovePad(product);
            yield return LowerUntilScanned(product, label + ".scan");
            if (order.NeedsBag) yield return PutInBag(product, label + ".bag");
            else yield return SetAside(product, i);
            yield return ResetHandRotation();
        }
        Check($"d2.order{number}.ready_for_payment", pos.ReadyForPayment, $"allScanned={pos.AllScanned} allBagged={pos.AllBagged} total={pos.Total}");
        if (order.Payment == PaymentMethod.Card)
        {
            yield return PayByCard(() => pos.Current == null, $"d2.order{number}.card");
        }
        else
        {
            int total = pos.Total;
            int bill = PaymentDesk.BillFor(total);
            yield return GiveBill($"d2.order{number}.bill");
            yield return GiveChange(bill - total, 0, false, () => pos.Current == null, $"d2.order{number}.change");
        }
        yield return WaitUntil(() => pos.Current == null, 10f, $"d2.order{number}.completed");
        servedOrders++;
        SamplePeak();
    }

    void CheckDay2Arrival(int number, Order order)
    {
        if (!arrivalsChecked.Add(number)) return;
        var director = Game.Director;
        var record = director.Records.Find(r => r.ForOrder && r.Sequence == number);
        if (!Check($"d2.order{number}.customer_record", record != null, "record")) return;
        Check($"d2.order{number}.limit_120_from_counter_with_bell", Mathf.Approximately(record.RemainingAtArrival, 120f - record.LinePenalty)
                && Mathf.Approximately(record.LinePenalty, record.QueuedAt >= 0f ? CustomerDirector.LinePenalty(record.CalledAt - record.QueuedAt, 120f) : 0f) && order.TimeLimitSeconds == 120f && director.Bells >= number,
            $"limit at arrival={record.RemainingAtArrival:0.0}; off for the line={record.LinePenalty:0.0} (in line {(record.QueuedAt >= 0f ? record.CalledAt - record.QueuedAt : 0f):0.0}s); bells={director.Bells}");
    }

    /// <summary>A good product in the waste bin: error, a false disposal, and it is back in the basket.</summary>
    IEnumerator FalseDisposal(ProductPrefab product)
    {
        var home = product.transform.position;
        int errors = Game.Pos.Audio.CountOf(StoreSound.Error);
        yield return GrabProduct(product, "d2.rule.false_disposal");
        // Lifted over the scanner's sight: the walk to the bin carries it along the counter, past the upright glass, and a
        // barcode going by in front of that at counter height is read, as it should be.
        var held = product.transform.position;
        float clear = CounterTop + AboveScannerSight + held.y - LowestProxyPoint(product);
        yield return MoveProduct(product, new Vector3(held.x, Mathf.Max(held.y + 0.2f, clear), held.z), "d2.rule.false_disposal.lift");
        yield return Go("D");
        var bin = Disposal.transform.position;
        yield return MoveProduct(product, new Vector3(bin.x, 1.05f, bin.z), "d2.rule.false_disposal.over_bin");
        yield return Release(product.Body, "release6");
        yield return WaitUntil(() => Intake.FalseDisposals == 1, 4f, "d2.rule.false_disposal.counted");
        yield return new WaitForSeconds(0.4f);
        Check("d2.rule.false_disposal_returns_to_basket", product != null && Vector3.Distance(product.transform.position, home) < 0.02f && Game.Pos.Audio.CountOf(StoreSound.Error) == errors + 1 && Intake.FalseDisposals == 1,
            $"off home {(product != null ? Vector3.Distance(product.transform.position, home) : -1f):0.000} m; errors +{Game.Pos.Audio.CountOf(StoreSound.Error) - errors}");
        yield return Go("C");
        yield return FaceCounter();
    }

    IEnumerator Idle()
    {
        // Nothing is touched. The first customer comes 90 s after the intake guidance began, and three leave in a row.
        yield return WaitUntil(() => Game.State == DayState.Shift, GameManager.IntakeTutorialMaxSeconds + 15f, "d2.idle.shift_starts");
        Check("d2.idle.first_customer_after_90s", Mathf.Abs(Game.ShiftStartedAt - IntakeTutorial.StartedAt - GameManager.IntakeTutorialMaxSeconds) < 0.25f && IntakeTutorial.Step == IntakeTutorialStep.Carry,
            $"shift began {Game.ShiftStartedAt - IntakeTutorial.StartedAt:0.00}s after the guidance; tutorial step={IntakeTutorial.Step}");
        yield return Capture("d2_idle_01_shift");
        yield return WaitUntil(() => Game.State == DayState.DayEnd, 3 * 160f, "d2.idle.day_ends");
        if (finished) yield break;
        yield return new WaitForSeconds(0.5f);
        yield return Capture("d2_idle_02_settlement");
        yield return CaptureFrom("d2_idle_03_settlement_board", new Vector3(0f, 1.6f, 0.15f), new Vector3(0f, 1.9f, 1.3f), 55f);
        var director = Game.Director;
        var records = director.Records.FindAll(r => r.ForOrder && r.CalledAt >= 0f);
        Check("d2.idle.three_left_ends_day", director.EndedEarly && director.Left == 3 && director.Paid == 0 && records.Count == 3 && records.TrueForAll(r => r.TimedOut && r.LeaveAnimationSeconds == 0f && r.HurriedAt > 0f),
            $"endedEarly={director.EndedEarly}; left={director.Left}; paid={director.Paid}; called={records.Count}; hurried=[{string.Join(", ", records.ConvertAll(r => (r.HurriedAt - r.ArrivedAt).ToString("0.0")))}]");
        bool timing = true;
        foreach (var r in records)
            timing &= Mathf.Abs(r.DoneAt - r.ArrivedAt - (120f - r.LinePenalty)) < 0.3f && Mathf.Abs(r.HurriedAt - r.ArrivedAt - (60f - r.LinePenalty)) < 0.3f;
        Check("d2.idle.timeout_120_hurry_60", timing, string.Join("; ", records.ConvertAll(r => $"#{r.Sequence} waited {r.DoneAt - r.ArrivedAt:0.00}s, hurried at {r.HurriedAt - r.ArrivedAt:0.00}s, line penalty {r.LinePenalty:0.0}s")));
        Check("d2.idle.grade_c_with_reason", Game.Grade == "C" && Game.BoardText.Contains("이탈 3명") && director.HudText.Contains("마감"),
            $"grade={Game.Grade} score={Game.Score:0.0}; board={Game.BoardText}");
        yield return WaitUntil(() => director.Present.Count == 0, EmptyStoreTimeout, "d2.idle.store_empties");
        Check("d2.idle.no_more_customers_called", director.Records.FindAll(r => r.ForOrder && r.CalledAt >= 0f).Count == 3 && !director.ShiftRunning, $"called={director.Records.FindAll(r => r.ForOrder && r.CalledAt >= 0f).Count}");
        FillDetails();
    }

    void CheckSettlement()
    {
        var director = Game.Director;
        var timedOut = director.Records.FindAll(r => r.ForOrder && r.TimedOut);
        Check("d2.day.eight_processed_one_left", director.Paid + director.Left == 8 && director.Left == 1 && timedOut.Count == 1 && timedOut[0].Sequence == TimeoutOrder && !director.EndedEarly,
            $"paid={director.Paid}; left={director.Left}; timed out=[{string.Join(", ", timedOut.ConvertAll(r => r.Sequence.ToString()))}]");
        var leaver = timedOut.Count > 0 ? timedOut[0] : null;
        Check("d2.day.leaver_hurried_and_no_wave", leaver != null && leaver.HurriedAt > 0f && Mathf.Abs(leaver.HurriedAt - leaver.ArrivedAt - (60f - leaver.LinePenalty)) < 0.3f && Mathf.Abs(leaver.DoneAt - leaver.ArrivedAt - (120f - leaver.LinePenalty)) < 0.3f && leaver.LeaveAnimationSeconds == 0f,
            leaver == null ? "none" : $"hurried at {leaver.HurriedAt - leaver.ArrivedAt:0.00}s; left at {leaver.DoneAt - leaver.ArrivedAt:0.00}s; wave={leaver.LeaveAnimationSeconds}");
        var next = director.Records.Find(r => r.ForOrder && r.Sequence == TimeoutOrder + 1);
        Check("d2.day.next_called_15s_after_leaver", leaver != null && next != null && Mathf.Abs(next.CalledAt - leaver.DoneAt - 15f) < SpawnGapTolerance,
            next == null || leaver == null ? "missing" : $"called {next.CalledAt - leaver.DoneAt:0.00}s after");
        Check("d2.day.intake_counts", Intake.Shelved == 10 && Intake.DeliveredTotal == 10 && Intake.Disposed == 3 && Intake.ExpiredTotal == 3 && Intake.Misplaced == 1 && Intake.FalseDisposals == 1
                && Intake.Recycled == 2 && Intake.UnrecycledEmptyBoxes == 0 && Game.Pos.CancelCount == 0,
            $"shelved {Intake.Shelved}/{Intake.DeliveredTotal}; disposed {Intake.Disposed}/{Intake.ExpiredTotal}; misplaced {Intake.Misplaced}; false {Intake.FalseDisposals}; recycled {Intake.Recycled}; unrecycled {Intake.UnrecycledEmptyBoxes}; cancels {Game.Pos.CancelCount}");
        Check("p4.end_of_day_shelves", Intake.SlotsById["A-2"].Merged.ShownUnits == 6 && Intake.SlotsById["B-1"].Merged.ShownUnits == 4
                && Intake.SlotsById["A-1"].Merged.ShownUnits + Intake.SlotsById["A-3"].Merged.ShownUnits + Intake.SlotsById["B-2"].Merged.ShownUnits == 3,
            string.Join("; ", Array.ConvertAll(Intake.Slots, s => $"{s.SlotId} shown {s.Merged?.ShownUnits}/{s.Capacity}")));
        Check("p4.peak_gaussians_in_budget", details.gaussiansAtPeak > 0 && details.gaussiansAtPeak <= 1500000, $"peak={details.gaussiansAtPeak}; renderers={details.splatRenderersAtPeak}");
        float salesRate = (float)director.Sales / Game.PossibleSales;
        float accuracy = 1f - 0.1f * (Intake.Misplaced + Intake.FalseDisposals);
        float satisfaction = 0f;
        foreach (var r in director.Records.FindAll(x => x.ForOrder)) satisfaction += r.TimedOut ? 0f : CustomerDirector.SatisfactionFor(r.ShareLeft);
        float expected = 40f * salesRate + 25f * satisfaction / 8f + 15f * accuracy + 20f * 1f;
        Check("d2.day.score_formula", Mathf.Abs(Game.Score - expected) < 0.01f && Mathf.Approximately(Game.TaskRate, 1f) && Mathf.Approximately(Game.Accuracy, accuracy)
                && Game.Grade == (expected >= 90f ? "S" : expected >= 75f ? "A" : expected >= 60f ? "B" : "C"),
            $"score={Game.Score:0.00} expected={expected:0.00} grade={Game.Grade}; sales {director.Sales}/{Game.PossibleSales}");
        Check("d2.day.board_shows_intake", Game.BoardText.Contains("진열 10/10") && Game.BoardText.Contains("폐기 3/3") && Game.BoardText.Contains("오진열 1") && Game.BoardText.Contains("폐기 오탐 1"),
            $"board={Game.BoardText}");
        Check("d2.day.next_day_button_for_day3", Game.NextDayButton != null && Game.NextDayButton.transform.parent.gameObject.activeSelf && Game.NextDayButton.GetComponentInChildren<UnityEngine.UI.Text>().text == "3일차 시작", "Day 3 follows Day 2");
        Check("d2.day.counter_cleared", FindObjectsByType<PaymentProp>(FindObjectsSortMode.None).Length == 0 && director.OrderProducts.Count == 0, "no payment prop, no order product");
        Check("input.set_downs_were_still", report.maxHeldSpeedAtRelease < SettledSpeed, $"max speed at release={report.maxHeldSpeedAtRelease:0.000} m/s");
        FillDetails();
    }

    void CheckDay2Customers()
    {
        var director = Game.Director;
        var forOrders = director.Records.FindAll(r => r.ForOrder);
        foreach (var r in director.Records)
            details.customers.Add($"{r.Name}: order={(r.ForOrder ? r.Sequence.ToString() : "-")} spawned={r.SpawnedAt:0.0} called={r.CalledAt:0.0} from={r.CalledFrom} arrived={r.ArrivedAt:0.0} limitLeft={r.RemainingAtArrival:0.0} hurried={r.HurriedAt:0.0} done={r.DoneAt:0.0} timedOut={r.TimedOut} wave={r.LeaveAnimationSeconds:0.0} gone={r.GoneAt:0.0}");
        Check("d2.customers.one_per_order", forOrders.Count == 8, $"customers for orders={forOrders.Count}");
        Check("d2.customers.one_at_a_time_at_counter", director.MaxAtCounterSeen == 1, $"max={director.MaxAtCounterSeen}");
        Check("d2.customers.paid_waved_left_did_not", forOrders.TrueForAll(r => r.TimedOut ? r.LeaveAnimationSeconds == 0f : r.LeaveAnimationSeconds > 0f), "waves only after paying");
        Check("d2.customers.no_stuck_walks", CustomerCharacter.StuckWarps == 0 && CustomerCharacter.ShortArrivals == 0,
            $"warps={CustomerCharacter.StuckWarps}; short={CustomerCharacter.ShortArrivals}; stepped aside when blocked={CustomerCharacter.StepAsides}; stopped short where the clerk stood={CustomerCharacter.ShortArrivalsAtClerk}; small hops off the clerk's hole={CustomerCharacter.Nudges}; shelf spots skipped for the clerk={director.SpotsSkippedForClerk}");
        Check("d2.customers.on_navmesh_clear_of_solids", report.offNavMeshFrames == 0 && report.overlapFrames == 0 && report.clerkSideFrames == 0 && report.customerFrames > 1000,
            $"frames={report.customerFrames}; off={report.offNavMeshFrames}; overlap={report.overlapFrames}; clerk side={report.clerkSideFrames}");
        Check("d2.customers.line_rules", director.MaxForOrdersInStore <= 3 && director.MaxWaitingSeen <= 2, $"for orders max={director.MaxForOrdersInStore}; waiting max={director.MaxWaitingSeen}");
    }

    void SamplePeak()
    {
        int renderers = 0;
        long gaussians = 0;
        foreach (var splat in FindObjectsByType<GaussianSplatting.Runtime.GaussianSplatRenderer>(FindObjectsSortMode.None))
        {
            if (!splat.isActiveAndEnabled) continue;
            renderers++;
            if (splat.m_Asset != null) gaussians += splat.m_Asset.splatCount;
        }
        if (gaussians > details.gaussiansAtPeak)
        {
            details.gaussiansAtPeak = gaussians;
            details.splatRenderersAtPeak = renderers;
        }
    }

    void FillDetails()
    {
        var director = Game.Director;
        details.shelved = Intake.Shelved;
        details.delivered = Intake.DeliveredTotal;
        details.disposed = Intake.Disposed;
        details.expired = Intake.ExpiredTotal;
        details.misplaced = Intake.Misplaced;
        details.falseDisposals = Intake.FalseDisposals;
        details.recycled = Intake.Recycled;
        details.unrecycled = Intake.UnrecycledEmptyBoxes;
        details.taskRate = Game.TaskRate;
        details.accuracy = Game.Accuracy;
        details.salesRate = Game.SalesRate;
        details.score = Game.Score;
        details.grade = Game.Grade;
        details.bells = director.Bells;
        details.hurries = director.Hurries;
        details.left = director.Left;
        details.paid = director.Paid;
        details.orders = Game.OrderCount;
        details.sales = director.Sales;
        details.possibleSales = Game.PossibleSales;
        details.endedEarly = director.EndedEarly;
        details.spotsSkippedForClerk = director.SpotsSkippedForClerk;
        details.nudges = CustomerCharacter.Nudges;
        details.shortArrivalsAtClerk = CustomerCharacter.ShortArrivalsAtClerk;
        report.grade = Game.Grade;
        report.score = Game.Score;
        report.sales = director.Sales;
        report.possibleSales = Game.PossibleSales;
        report.paid = director.Paid;
        report.orders = Game.OrderCount;
        report.left = director.Left;
        report.cancels = Game.Pos.CancelCount;
        report.stuckWarps = CustomerCharacter.StuckWarps;
    }

    void WriteDay2Details()
    {
        if (!day2Mode && !nextDayCheck) return;
        File.WriteAllText(Path.Combine(output, idleMode ? "day2_idle_details.json" : nextDayCheck ? "next_day_details.json" : "day2_details.json"), JsonUtility.ToJson(details, true));
    }

    // ---------- Day 1 → Day 2 ----------

    /// <summary>After Day 1's settlement: the "2일차 시작" button, pressed with the ray and trigger like the POS [-].</summary>
    IEnumerator NextDay()
    {
        var button = Game.NextDayButton;
        Check("next.button_shown_after_day1", button != null && button.transform.parent.gameObject.activeInHierarchy && button.GetComponentInChildren<UnityEngine.UI.Text>().text == "2일차 시작" && Game.Day == 1,
            $"shown={button != null && button.transform.parent.gameObject.activeInHierarchy}");
        yield return Capture("21_next_day_button");
        yield return MoveHand(new Vector3(0.1f, 1.25f, 0.35f), "next.hand");
        var target = button.transform.position;
        var direction = (target - rightHand.position).normalized;
        float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Asin(direction.y) * Mathf.Rad2Deg;
        yield return RotateHand(new Vector3(pitch, yaw, 0f) - handEuler);
        yield return new WaitForSeconds(0.5f);
        leftButton = true;
        yield return new WaitForSeconds(0.2f);
        leftButton = false;
        yield return WaitUntil(() => Game.Day == 2 && Game.State != DayState.DayEnd, 5f, "next.day_two_begins");
        yield return WaitUntil(() => IntakeTutorial.Step == IntakeTutorialStep.Carry, 10f, "next.intake_tutorial_starts");
        Check("next.day_two_fresh", Game.NextDayPresses == 1 && Game.Director.Paid == 0 && Game.Director.Sales == 0 && Game.Director.Left == 0 && Game.Pos.CancelCount == 0 && Game.OrderCount == 8
                && Intake.Active && Intake.Boxes.Count == 2 && Intake.ExpiredTotal == 3 && !button.transform.parent.gameObject.activeInHierarchy
                && Intake.DisplayCount == 0 && Array.TrueForAll(Intake.Slots, s => s == null || s.Displays.Count == 0) && Intake.ShelfStock.Count == 6,
            $"presses={Game.NextDayPresses}; paid={Game.Director.Paid}; cancels={Game.Pos.CancelCount}; orders={Game.OrderCount}; boxes={Intake.Boxes.Count}; day 1 displays left={Intake.DisplayCount}; shelf stock={Intake.ShelfStock.Count}");
        Check("next.board_says_day_two", Game.BoardText.Contains("입고 1/4"), $"board={Game.BoardText}");
        yield return Capture("22_day2_after_button");
        yield return ResetHandRotation();
    }
}
