using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The hands' part of a store that is kept (-p2-growth-hands, with -save-path FILE holding a store whose first morning
/// brings two boxes of new products: docs/p2/해금_성장_기획_2026-11-10.md). P2GrowthVerification opens those boxes and
/// places their products by code; this does it the way P2DayVerification does Day 2, through the virtual keyboard and
/// mouse the XR Device Simulator reads: the narrow box of three carried by its hand holes to a stand, its short tape cut,
/// its flaps opened, and each product put on a display shelf through the placement preview. One box is for a shelf beside
/// the stand (E-3, over B-2), the other for row D, a walk away. At the end the ordering terminal's button is pressed with
/// the ray and trigger.
///
/// Not by hand, because other checks have them: the title, the walk in and the time card are passed by code, and the
/// customers are served by code in the background so the shift goes on while the hands are at the shelves.
/// Simulator evidence: nothing about a real HMD, reach, comfort, or how the work feels.
/// </summary>
public partial class P2DayVerification
{
    bool growthHandsMode;
    int servedByCode;

    IEnumerator RunGrowthHands()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        rig = FindFirstObjectByType<XROrigin>().transform;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;

        var title = Game.Title;
        yield return WaitUntil(() => Game.State == DayState.Title && title.PanelShown, 20f, "gh.title_shown");
        if (finished) yield break;
        var progress = Game.Progress;
        var far = Game.Growth ? Game.UnlockItems.Find(item => item.Slot == "D-1") : null;
        var near = Game.Growth ? Game.UnlockItems.Find(item => item.Slot == "E-3") : null;
        bool ready = Game.Growth && progress.graduated && far != null && near != null && Game.BoxesToday.Count == 2 && Intake.Boxes.Count == 2
                     && !progress.Shelved(far.Id) && !progress.Shelved(near.Id);
        Check("gh.setup.store_with_two_boxes", ready, Game.Growth
            ? $"day={progress.day}; graduated={progress.graduated}; owned={string.Join(",", progress.owned)}; boxes this morning={Game.BoxesToday.Count}; on the cart={Intake.Boxes.Count}; save={progress.Path}"
            : "the store is not one that is kept: run with -p2-growth-hands -save-path FILE and no fixed-day argument");
        if (!ready)
        {
            Finish();
            yield break;
        }
        var farBox = Intake.Boxes.Find(box => box.Delivery.Product == far.Product);
        var nearBox = Intake.Boxes.Find(box => box.Delivery.Product == near.Product);
        var farSlot = Intake.SlotFor(far.Slot);
        var nearSlot = Intake.SlotFor(near.Slot);

        // The title, the walk in and the time card belong to the title check; here the player is put through them.
        title.StartButton.onClick.Invoke();
        yield return WaitUntil(() => Game.State == DayState.Commute, 5f, "gh.continue_chosen");
        if (finished) yield break;
        yield return new WaitForSeconds(1.6f);
        title.PlacePlayer(title.CounterSpot);
        yield return WaitUntil(() => Game.State == DayState.PunchIn, 10f, "gh.at_the_counter");
        if (finished) yield break;
        yield return CardInByCode(() => Game.State != DayState.PunchIn);
        yield return WaitUntil(() => Game.State == DayState.Shift, 60f, "gh.shift");
        if (finished) yield break;
        StartCoroutine(ServeByCode());

        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Check("input.ready", simulator.manipulatingRightController && simulator.mouseTransformationMode == UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulator.TransformationMode.Translate, $"mode={simulator.mouseTransformationMode}");
        at = "C";

        var plan = Game.PlanFor(Game.Day);
        int asked = 0;
        foreach (var order in OrderGenerator.Build(plan))
            foreach (var line in order.Lines)
                if (line.Product == far.Product || line.Product == near.Product) asked += line.Quantity;
        Check("gh.setup.shelves_wait", farSlot.IsTarget && nearSlot.IsTarget && farSlot.Filled == 0 && nearSlot.Filled == 0 && farSlot.Zone.enabled && nearSlot.Zone.enabled
                && farBox.Units.Length == 3 && nearBox.Units.Length == 3 && Game.OrderCount == OrderGenerator.CustomersFor(2, 0) && asked >= 2,
            $"{far.Slot}: target={farSlot.IsTarget}, {farSlot.CountText}; {near.Slot}: target={nearSlot.IsTarget}, {nearSlot.CountText}; boxes of {farBox.Units.Length} and {nearBox.Units.Length}; "
            + $"customers={Game.OrderCount}; lines asking for the two new products={asked}; box sizes {farBox.Inner.ToString("F3")} and {nearBox.Inner.ToString("F3")}");
        yield return CaptureFrom("gh_01_two_boxes_on_the_cart", new Vector3(2.2f, 1.6f, 0.2f), new Vector3(3.4f, 0.8f, -1.6f), 60f);

        // The shelf beside the stand: E-3 is on gondola B's top board, by stand B.
        float begun = Time.realtimeSinceStartup;
        yield return OpenOnStand(nearBox, 1, "SB", "gh.near");
        if (finished) yield break;
        for (int n = 1; n <= 3 && !finished; n++) yield return ShelveByHand(nearBox, nearSlot, "SB", "SB", $"gh.near.unit{n}", near);
        if (finished) yield break;
        CheckDisplayShelfFull(nearBox, nearSlot, near, "gh.near", Time.realtimeSinceStartup - begun);
        yield return CaptureFrom("gh_13_near_shelf_full", nearSlot.transform.position + new Vector3(0.1f, 0.25f, -0.9f), nearSlot.transform.position + new Vector3(0f, 0.12f, 0.05f), 55f);

        // Row D: no stand of its own, so each product is carried from a stand across the store. Stand A is the nearer one.
        begun = Time.realtimeSinceStartup;
        yield return OpenOnStand(farBox, 0, "SA", "gh.far");
        if (finished) yield break;
        for (int n = 1; n <= 3 && !finished; n++) yield return ShelveByHand(farBox, farSlot, "SA", "SD", $"gh.far.unit{n}", far);
        if (finished) yield break;
        CheckDisplayShelfFull(farBox, farSlot, far, "gh.far", Time.realtimeSinceStartup - begun);
        yield return CaptureFrom("gh_23_far_shelf_full", farSlot.transform.position + new Vector3(0.1f, 0.25f, -0.9f), farSlot.transform.position + new Vector3(0f, 0.12f, 0.05f), 55f);
        float fromStandA = WalkLength("SA", "SD"), fromStandB = WalkLength("SB", "SD");
        Check("gh.far.walk_measured", fromStandA > 0f && fromStandB > fromStandA, $"along the lanes the check walks, row D is {fromStandA:0.0} m from stand A and {fromStandB:0.0} m from stand B, one way (this run used stand A); three products are {fromStandA * 5f:0} m of walking, the last way back not counted");

        yield return WaitUntil(() => Game.State == DayState.DayEnd && Game.Tablet.Shown, 300f, "gh.day_ended");
        if (finished) yield break;
        yield return new WaitForSeconds(0.5f);
        Check("gh.day.new_products_in_the_settlement", Game.NewSoldToday + Game.MissedNewToday == asked && Game.BoardText.Contains($"새 상품 {far.Name} · {near.Name}") && Game.BoardText.Contains($"{Game.NewSoldToday}개 판매")
                && servedByCode == Game.OrderCount && Game.Director.Left == 0 && Intake.Shelved == 6,
            $"asked for {asked}: sold {Game.NewSoldToday}, left out before they were shelved {Game.MissedNewToday}; customers served by code={servedByCode}/{Game.OrderCount}; shelved by hand today={Intake.Shelved}; board={Game.BoardText.Replace("\n", " / ")}");
        yield return OrderByRay();
        Finish();
    }

    /// <summary>The length of the walk between two nodes, along the lanes Go takes.</summary>
    static float WalkLength(string from, string to)
    {
        float length = 0f;
        var path = PathBetween(from, to);
        for (int i = 1; i < path.Count; i++) length += Vector2.Distance(Nodes[path[i - 1]], Nodes[path[i]]);
        return length;
    }

    /// <summary>Whoever comes to the counter is paid for at once, without hands, so nobody leaves while the shelves are stocked.</summary>
    IEnumerator ServeByCode()
    {
        var pos = Game.Pos;
        while (!finished && Game.State == DayState.Shift)
        {
            var order = pos.Current;
            if (order != null)
            {
                yield return new WaitForSeconds(0.5f);
                if (pos.Current == order)
                {
                    foreach (var line in pos.Lines) line.Scanned = line.Ordered;
                    pos.CompleteCard();
                    servedByCode++;
                }
            }
            yield return null;
        }
    }

    IEnumerator CardInByCode(Func<bool> done)
    {
        float begun = Time.time;
        while (!done() && Desk.CustomerCard == null && Time.time - begun < 5f) yield return null;
        begun = Time.time;
        while (!done() && Desk.CustomerCard != null && Time.time - begun < 4f)
        {
            var body = Desk.CustomerCard.GetComponent<Rigidbody>();
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.position = Desk.CardSlot.transform.position;
            Desk.CustomerCard.transform.position = Desk.CardSlot.transform.position;
            yield return new WaitForFixedUpdate();
        }
        yield return WaitUntil(done, 90f, "gh.punched_in");
    }

    /// <summary>From the cart to a stand, the tape cut end to end, both flaps folded open: the three products stand in the box.</summary>
    IEnumerator OpenOnStand(StockBox box, int stand, string node, string label)
    {
        var socket = Intake.Stands[stand];
        yield return CarryBox(box, socket, node, label);
        if (finished) yield break;
        Check(label + ".narrow_box_sits_on_the_stand", box.State == StockBoxState.Docked && box.DockedAt == socket && Vector3.Distance(box.transform.position, socket.attachTransform.position) < 0.01f
                && Quaternion.Angle(box.transform.rotation, socket.attachTransform.rotation) < 1f,
            $"state={box.State}; off the seat {Vector3.Distance(box.transform.position, socket.attachTransform.position):0.000} m, {Quaternion.Angle(box.transform.rotation, socket.attachTransform.rotation):0.0}°; inner {box.Inner.ToString("F3")}");
        float tape = Vector3.Distance(box.CheckpointWorld(0), box.CheckpointWorld(box.CheckpointCount - 1));
        yield return Cut(box, Intake.Cutters[stand], label + ".cut", false);
        if (finished) yield break;
        Check(label + ".short_tape_cut_in_one_stroke", box.Stage == BoxStage.TapeCut && box.CutResets == 0, $"tape {tape * 100f:0.0} cm between its end checkpoints, {box.CheckpointCount} checkpoints; resets={box.CutResets}");
        yield return OpenFlaps(box, label);
    }

    /// <summary>
    /// One product from the open box to the display shelf. With the shelf beside the stand the head looks at the place
    /// first, as a player does; for the shelf across the store the product is carried there and held before its place.
    /// </summary>
    IEnumerator ShelveByHand(StockBox box, ShelfSlotZone slot, string boxNode, string shelfNode, string label, UnlockItem item)
    {
        var unit = NextUnit(box);
        if (unit == null)
        {
            Check(label + ".a_product_left_in_the_box", false, $"stage={box.Stage}");
            yield break;
        }
        var product = unit.Product;
        int column = -1;
        for (int c = 0; c < slot.Columns && column < 0; c++)
            if (slot.NextFreeInColumn(c) >= 0) column = c;
        int place = slot.NextFreeInColumn(column);
        var unitAt = slot.UnitPosition(place);
        bool beside = boxNode == shelfNode;
        if (at != boxNode) yield return Go(boxNode);
        if (beside) yield return LookAt(unitAt, label + ".look_at_the_place");
        yield return GrabAt(product.transform.position, () => product.Grab.interactorsSelecting.Contains(rightInteractor), label + ".grab");
        if (finished) yield break;
        yield return MoveProduct(product, product.transform.position + Vector3.up * 0.3f, label + ".lift");
        float walked = Time.realtimeSinceStartup;
        if (beside) yield return MoveProduct(product, new Vector3(product.transform.position.x, product.transform.position.y, slot.transform.position.z - 0.15f), label + ".to_aisle");
        else
        {
            // Out of the box toward the aisle first, then to the body, and carried close as a person carries a bag of crisps.
            // The stand is at the end of the gondola: straight from above the box to the body goes through the gondola's end
            // panel, and the product stays behind it while the hand goes on (first runs, 2026-11-11: 0.4 m to 1 m behind).
            yield return new WaitForSeconds(0.3f);
            var held = product.transform.position - rightHand.position;   // lifted clear of the box and at rest in the hand
            var forward = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, forward);
            var head = view.transform.position;
            var carry = new Vector3(head.x, 1.15f, head.z) + forward * 0.3f + right * 0.15f;
            var lifted = rightHand.position;
            yield return MoveHand(lifted + forward * Vector3.Dot(carry - lifted, forward), label + ".to_the_aisle");
            yield return MoveHand(carry, label + ".carry_pose");
            yield return HeldFollows(product, held, 1.5f);
            Check(label + ".out_of_the_box_to_the_body", heldBehind <= 0.03f, $"behind the hand by {heldBehind:0.000} m after the hand came to rest{heldAround}");
            walked = Time.realtimeSinceStartup;
            yield return Go(shelfNode);
            walked = Time.realtimeSinceStartup - walked;
            if (finished) yield break;
            yield return HeldFollows(product, held, 1.5f);
            Check(label + ".carried_across_the_store", product.Grab.interactorsSelecting.Contains(rightInteractor) && at == shelfNode && heldBehind <= 0.03f,
                $"still in the hand={product.Grab.isSelected}; {WalkLength(boxNode, shelfNode):0.0} m in {walked:0.0} s; behind the hand by {heldBehind:0.000} m{heldAround}");
        }
        if (finished) yield break;
        yield return MoveProduct(product, new Vector3(unitAt.x, unitAt.y + 0.06f, slot.transform.position.z - 0.1f), label + ".in_front");
        // A place already shown changes only after the nearer one has held for 0.2 s (PlacementPreview): wait for the place held before.
        yield return WaitUntil(() => Preview.GhostVisible && Preview.Slot == slot && Preview.Unit == place, 3f, label + ".green_place_on_the_display_shelf");
        if (finished) yield break;
        int target = Preview.Unit;
        Check(label + ".preview", Preview.Unit >= 0 && !slot.IsFilled(Preview.Unit) && slot.GreenUnit == Preview.Unit && !slot.RejectShown && Preview.UsedHeadRay == beside && (!beside || Preview.Unit == place),
            $"unit={Preview.Unit} (looked at {place}); by the head's ray={Preview.UsedHeadRay}; green outline={slot.GreenUnit}; red={slot.RejectShown}");
        if (slot.Filled == 0) yield return CaptureFrom(label.Replace('.', '_') + "_ghost", slot.transform.position + new Vector3(0.25f, 0.3f, -0.75f), slot.transform.position + new Vector3(0f, 0.1f, 0.05f), 50f);
        int filled = slot.Filled;
        bool onSaleBefore = Game.Progress.Shelved(item.Id);
        yield return Release(product.Body, label);
        yield return WaitUntil(() => slot.Filled == filled + 1 && product.IsShelved, 2f, label + ".accepted");
        if (finished) yield break;
        yield return new WaitForSeconds(0.3f);
        Check(label + ".snapped_and_drawn_on_the_shelf", Vector3.Distance(product.transform.position, slot.RootPosition(product, target)) < 0.005f && Quaternion.Angle(product.transform.rotation, slot.UnitRotation) < 1f
                && product.Body.isKinematic && !product.Grab.enabled && unit.Done && slot.CountText.Contains($"{filled + 1}/{slot.Capacity}")
                && slot.Merged != null && slot.Merged.IsUnitShown(target) && !product.Splat.enabled && slot.Merged.ShownUnits == filled + 1,
            $"off its place {Vector3.Distance(product.transform.position, slot.RootPosition(product, target)):0.0000} m; {slot.CountText}; drawn by the shelf's splat={slot.Merged?.IsUnitShown(target)} ({slot.Merged?.ShownUnits} shown, near copy={slot.Merged?.ShowingNear}); own splat={product.Splat.enabled}");
        if (filled == 0)
            Check(label + ".first_one_puts_it_on_sale", !onSaleBefore && Game.Progress.Shelved(item.Id) && StoreProgress.Load(Game.Progress.Path).Shelved(item.Id),
                $"on sale before={onSaleBefore}, now={Game.Progress.Shelved(item.Id)}, saved={StoreProgress.Load(Game.Progress.Path).Shelved(item.Id)}; state={Game.State}; board={Game.BoardText.Replace("\n", " / ")}");
        yield return MoveHand(rightHand.position + Vector3.up * 0.05f, label + ".hand_back");
        SamplePeak();
    }

    float heldBehind;
    string heldAround;

    /// <summary>
    /// How far a held product is from where it sat in the hand, once it has had time to catch up. A product that stays
    /// behind is caught on something solid: what is around it is named.
    /// </summary>
    IEnumerator HeldFollows(ProductPrefab product, Vector3 held, float seconds)
    {
        float begun = Time.time;
        while (Time.time - begun < seconds && (product.transform.position - rightHand.position - held).magnitude > 0.03f) yield return null;
        heldBehind = (product.transform.position - rightHand.position - held).magnitude;
        heldAround = "";
        if (heldBehind <= 0.03f) yield break;
        var center = product.transform.TransformPoint(product.Proxy.center);
        foreach (var hit in Physics.OverlapSphere(center, 0.25f, ~0, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(product.transform) && !Physics.GetIgnoreCollision(product.Proxy, hit) && !Physics.GetIgnoreLayerCollision(product.Proxy.gameObject.layer, hit.gameObject.layer))
                heldAround += (heldAround.Length == 0 ? "; solid things within 25 cm: " : ", ") + hit.name;
    }

    void CheckDisplayShelfFull(StockBox box, ShelfSlotZone slot, UnlockItem item, string label, float seconds)
    {
        Check(label + ".box_empty_and_shelf_full", box.Stage == BoxStage.Empty && slot.Filled == slot.Capacity && !slot.HighlightShown && slot.CountText.Contains($"{slot.Capacity}/{slot.Capacity}")
                && slot.Merged != null && slot.Merged.ShownUnits == slot.Capacity && Intake.Misplaced == 0,
            $"box={box.Stage}; {slot.CountText}; lit={slot.HighlightShown}; misplaced={Intake.Misplaced}; {item.Name}: cart to full shelf in {seconds:0} s of simulated hands; gaussians at the peak so far={details.gaussiansAtPeak}");
    }

    /// <summary>The ordering terminal after the day: its first button that can be afforded, with the ray and the trigger like the POS [-].</summary>
    IEnumerator OrderByRay()
    {
        var tablet = Game.Tablet;
        var progress = Game.Progress;
        yield return Go("C");
        int row = -1;
        for (int i = 0; i < tablet.BuyButtons.Count && row < 0; i++)
            if (tablet.BuyButtons[i].gameObject.activeInHierarchy && tablet.BuyButtons[i].interactable) row = i;
        if (!Check("gh.order.something_to_afford", row >= 0, $"budget={progress.budget}; tab={tablet.Tab}; {tablet.SummaryText}")) yield break;
        var button = Middle(tablet.BuyButtons[row]);
        yield return ResetHandRotation();
        yield return MoveHand(new Vector3(0.8f, 1.2f, 0.3f), "gh.order.hand");
        // The ray leaves the interactor's own origin, not the hand's pivot: aimed by what that ray does, in a few passes.
        var cast = rightInteractor.curveOrigin;
        float off = 180f;
        for (int pass = 0; pass < 4 && off > 0.4f; pass++)
        {
            var wanted = (button - cast.position).normalized;
            var now = cast.forward;
            float yaw = Mathf.DeltaAngle(Mathf.Atan2(now.x, now.z) * Mathf.Rad2Deg, Mathf.Atan2(wanted.x, wanted.z) * Mathf.Rad2Deg);
            float pitch = (-Mathf.Asin(wanted.y) - -Mathf.Asin(now.y)) * Mathf.Rad2Deg;
            yield return RotateHand(new Vector3(pitch, yaw, 0f));
            yield return new WaitForSeconds(0.15f);
            off = Vector3.Angle(cast.forward, button - cast.position);
        }
        yield return new WaitForSeconds(0.5f);
        bool onUi = rightInteractor.TryGetCurrentUIRaycastResult(out var uiHit);
        string rayOn = onUi && uiHit.gameObject != null ? $"{uiHit.gameObject.name} under {uiHit.gameObject.transform.parent?.name}, {Vector3.Distance(uiHit.worldPosition, button) * 100f:0.0} cm from the button's middle" : "no UI";
        Check("gh.order.ray_on_the_button", onUi && uiHit.gameObject != null && uiHit.gameObject.transform.IsChildOf(tablet.BuyButtons[row].transform),
            $"ray off the button by {off:0.0}°; on {rayOn}; ray from {cast.position.ToString("F2")}, hand at {rightHand.position.ToString("F2")}, button at {button.ToString("F2")}");
        int budget = progress.budget, arriving = progress.arriving.Count;
        string rowBefore = tablet.RowText(row);
        yield return CaptureFrom("gh_30_ray_on_the_order_button", new Vector3(0.35f, 1.55f, -0.25f), button, 55f);
        leftButton = true;
        yield return new WaitForSeconds(0.2f);
        leftButton = false;
        yield return new WaitForSeconds(0.4f);
        Check("gh.order.button_pressed_by_ray", progress.arriving.Count == arriving + 1 && progress.budget < budget && !tablet.BuyButtons[row].gameObject.activeInHierarchy && tablet.NoticeText.Length > 0,
            $"row {row}: {rowBefore} -> {tablet.RowText(row)}; budget {budget} -> {progress.budget}; arriving {arriving} -> {progress.arriving.Count}; notice={tablet.NoticeText}");
        yield return CaptureFrom("gh_31_ordered_by_ray", new Vector3(0.35f, 1.55f, -0.25f), button, 55f);
    }
}
