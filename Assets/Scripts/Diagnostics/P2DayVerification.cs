using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Runs only with -p2-verify. Plays all of Day 1 through virtual Keyboard and Mouse devices, the same path as the
/// XR Device Simulator's user: it never moves objects or calls game methods, and only reads state. Besides the
/// normal flow, it tries the mistakes the rules must stop. With -p2-walk it also walks with the left stick during the
/// shift, standing somewhere else for each order and carrying things, to show walking and the hand inputs do not collide.
/// This is simulator evidence. It says nothing about a real HMD, controllers, reach, or how the game feels.
/// </summary>
public partial class P2DayVerification : MonoBehaviour
{
    // Input right after entering Play is dropped while the editor settles.
    const float StartupSeconds = 4f;
    // For screenshots only: near what an HMD shows, so the counter, screens and board fit in one picture.
    const float ViewFieldOfView = 90f;
    const float MaxHandSpeed = 0.8f;
    const float MoveGain = 0.35f;
    const float MoveTolerance = 0.006f;
    const float MoveTimeout = 15f;
    const float StepTimeout = 30f;
    const float RotateDegreesPerSecond = 270f;
    const float LookMouseUnitsPerSecond = 300f;
    const float LookDownMouseUnits = -75f;
    const float CounterTop = 0.9f;
    const float TravelHeight = 1.32f;
    const float AbovePad = 0.05f;
    const float PressStep = 0.004f;
    const float LowerTimeout = 8f;
    const float BagDropHeight = 0.32f;
    const float ScanCooldown = 0.9f;
    // XRI throws what is let go using the last quarter second of hand motion, so a test that means to set something
    // down has to stop first. Otherwise a light bill flies, and the tray is a centimeter from the counter's far edge.
    const float SettleBeforeRelease = 0.35f;
    const float SettledSpeed = 0.1f;
    // Just past ChangeTrayZone.StoppedAddingSeconds.
    const float StoppedAddingWait = 8.6f;
    static readonly Vector3 OffCounterDrop = new Vector3(0.35f, 1.05f, 0.18f);
    // How far in front of the card slot the card is lined up before it goes in.
    const float CardApproach = 0.12f;
    const float WalkSpeed = 1.5f;
    const float CarriedDrift = 0.03f;
    static readonly int[] Denominations = { 5000, 1000, 500, 100 };
    // Customers (D12): the counter's customer-side face, and how long the store may take to empty after the day.
    const float CounterFarEdge = 0.95f;
    const float EmptyStoreTimeout = 60f;
    const float SpawnGapTolerance = 0.25f;

    public GameManager Game;
    public POSScreen Screen;
    public ScannerZone Scanner;
    public BagZone Bag;
    public PaymentDesk Desk;

    [Serializable] class Stage { public string name; public bool pass; public string details; public float time; }

    [Serializable] class Report
    {
        public bool pass;
        public string unity, gpu, api, runtime, scope;
        public bool hmdRuntimeActive;
        public float externalMouseMotion;
        public float meanDesktopFps;
        public float maxHeldSpeedOnArrival;
        public float maxHeldSpeedAtRelease;
        public string grade;
        public float score;
        public int sales, possibleSales, paid, orders, left, cancels;
        public bool queueAndBrowsers;
        public int maxCustomersPresent, maxForOrdersInStore, maxWaiting, maxLine, maxBrowsers, browsersSpawned, stuckWarps, shortArrivals;
        public int customerFrames, settledFrames, offNavMeshFrames, overlapFrames, clerkSideFrames, movingFrames, walkAnimatedFrames, standingFrames, standingAnimatedFrames;
        public List<string> customers = new List<string>();
        public List<Stage> stages = new List<Stage>();
    }

    readonly Report report = new Report();
    readonly HashSet<Key> heldKeys = new HashSet<Key>();
    Keyboard keyboard;
    Mouse mouse;
    Vector2 mouseDelta;
    bool leftButton;
    string output;
    Camera view;
    XRDeviceSimulator simulator;
    NearFarInteractor rightInteractor;
    Transform rightHand;
    Vector3 handEuler;
    float started;
    int frames;
    bool finished;
    int scansSeen;
    int bagsSeen;
    int cardsRejected;
    bool walking;
    bool walkedHoldingProduct;
    bool walkedHoldingProp;

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        day2Mode = Array.IndexOf(args, "-p2-day2-verify") >= 0;
        titleMode = Array.IndexOf(args, "-p2-title-verify") >= 0;
        if (Array.IndexOf(args, "-p2-verify") < 0 && !day2Mode && !titleMode) return;
        walking = Array.IndexOf(args, "-p2-walk") >= 0;
        nextDayCheck = Array.IndexOf(args, "-p2-next-day") >= 0;
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        output = Path.Combine(Application.dataPath, titleMode ? "../docs/p2/evidence/title/editor" : day2Mode ? "../docs/p2/evidence/day2/editor" : "../docs/p2/evidence/day1/editor");
        int index = Array.IndexOf(args, "-p2-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        keyboard = InputSystem.AddDevice<Keyboard>("P2 Verify Keyboard");
        mouse = InputSystem.AddDevice<Mouse>("P2 Verify Mouse");
        started = Time.realtimeSinceStartup;
        Scanner.Scanned += _ => scansSeen++;
        Bag.Bagged += _ => bagsSeen++;
        Desk.CardSlot.Rejected += () => cardsRejected++;
        StartCoroutine(titleMode ? RunTitleCheck() : day2Mode ? RunDay2() : Run());
    }

    void Update()
    {
        if (keyboard == null) return;
        frames++;
        foreach (var device in InputSystem.devices)
            if (device is Mouse real && real != mouse)
                report.externalMouseMotion += real.delta.ReadValue().magnitude;
        SampleCustomers();
    }

    /// <summary>
    /// Every frame, every customer: on the NavMesh, clear of walls, shelves and the counter, never on the clerk's side, and
    /// animated as it moves (walk) or stands (anything but walk). Read-only.
    /// </summary>
    void SampleCustomers()
    {
        if (Game == null || Game.Director == null) return;
        foreach (var customer in Game.Director.Present)
        {
            if (customer == null) continue;
            report.customerFrames++;
            var position = customer.transform.position;
            // A standing customer has its agent off and is a carved obstacle; only walking ones can be off the mesh.
            bool walking = customer.Agent.enabled;
            if (!walking) report.settledFrames++;
            else if (!customer.Agent.isOnNavMesh) report.offNavMeshFrames++;
            if (position.z < CounterFarEdge + 0.1f) report.clerkSideFrames++;
            if (Physics.CheckCapsule(position + Vector3.up * 0.4f, position + Vector3.up * 1.4f, 0.15f, 1, QueryTriggerInteraction.Ignore))
                report.overlapFrames++;
            if (customer.Animator == null) continue;
            float speed = walking ? customer.Agent.velocity.magnitude : 0f;
            if (speed > 0.35f)
            {
                report.movingFrames++;
                if (InState(customer.Animator, CustomerCharacter.WalkState)) report.walkAnimatedFrames++;
            }
            else if (speed < 0.02f && (!walking || !customer.Agent.hasPath))
            {
                report.standingFrames++;
                if (!InState(customer.Animator, CustomerCharacter.WalkState) || customer.Animator.IsInTransition(0)) report.standingAnimatedFrames++;
            }
        }
    }

    static bool InState(Animator animator, string state)
    {
        var current = animator.GetCurrentAnimatorStateInfo(0);
        return current.IsName(state) || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName(state));
    }

    void LateUpdate()
    {
        if (keyboard == null) return;
        var keys = new Key[heldKeys.Count];
        heldKeys.CopyTo(keys);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
        var state = new MouseState { delta = mouseDelta };
        if (leftButton) state = state.WithButton(MouseButton.Left);
        InputSystem.QueueStateEvent(mouse, state);
        mouseDelta = Vector2.zero;
    }

    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        CheckNames();
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        Check("input.look_down_via_mouse", view.transform.forward.y < -0.2f, $"camera forward={view.transform.forward.ToString("F3")}");
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Check("input.ready", simulator.manipulatingRightController && simulator.mouseTransformationMode == XRDeviceSimulator.TransformationMode.Translate, $"mode={simulator.mouseTransformationMode}");
        yield return Capture("01_tutorial_or_intro");
        CheckDisplayStock();
        yield return CaptureFrom("01b_day1_shelf_a", new Vector3(-1.6f, 1.7f, 1.6f), new Vector3(-2.8f, 1.0f, 3.1f), 60f);
        yield return CaptureFrom("01c_day1_shelf_b", new Vector3(1.6f, 1.7f, 1.6f), new Vector3(2.8f, 1.0f, 3.1f), 60f);

        yield return Tutorial();
        if (finished) yield break;
        yield return Capture("05_tutorial_done");

        yield return Shift();
        if (finished) yield break;

        yield return WaitUntil(() => Game.State == DayState.DayEnd, 20f, "day_end");
        yield return new WaitForSeconds(0.5f);
        yield return Capture("20_settlement");
        report.grade = Game.Grade;
        report.score = Game.Score;
        report.sales = Game.Director.Sales;
        report.possibleSales = Game.PossibleSales;
        report.paid = Game.Director.Paid;
        report.orders = Game.OrderCount;
        report.left = Game.Director.Left;
        report.cancels = Game.Pos.CancelCount;
        Check("day.all_paid", report.paid == report.orders && report.orders == 5 && report.left == 0, $"paid={report.paid}/{report.orders} left={report.left}");
        Check("day.sales_match", report.sales == report.possibleSales && report.sales > 0, $"sales={report.sales}/{report.possibleSales}");
        Check("day.cancel_counted", report.cancels == 1, $"cancels={report.cancels}");
        Check("day.grade", report.grade == "S" && Mathf.Approximately(report.score, 98f), $"grade={report.grade} score={report.score:0.0} (one cancel costs 2 points)");
        Check("day.counter_cleared",
            FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None).Length == 0 && FindObjectsByType<PaymentProp>(FindObjectsSortMode.None).Length == 0,
            "no product or payment prop left in the scene");
        Check("input.set_downs_were_still", report.maxHeldSpeedAtRelease < SettledSpeed, $"max speed on arrival={report.maxHeldSpeedOnArrival:0.000} at release={report.maxHeldSpeedAtRelease:0.000} m/s");
        yield return WaitUntil(() => Game.Director.Present.Count == 0, EmptyStoreTimeout, "customers.all_left_by_the_door");
        CheckCustomers();
        if (nextDayCheck) yield return NextDay();
        Finish();
    }

    /// <summary>The whole day's customers, from the director's records and the per-frame samples.</summary>
    void CheckCustomers()
    {
        var director = Game.Director;
        bool queue = director.QueueAndBrowsers;
        report.queueAndBrowsers = queue;
        report.maxCustomersPresent = director.MaxPresent;
        report.maxForOrdersInStore = director.MaxForOrdersInStore;
        report.maxWaiting = director.MaxWaitingSeen;
        report.maxLine = director.MaxLineSeen;
        report.maxBrowsers = director.MaxBrowsersSeen;
        report.browsersSpawned = director.BrowsersSpawned;
        report.stuckWarps = CustomerCharacter.StuckWarps;
        report.shortArrivals = CustomerCharacter.ShortArrivals;
        foreach (var r in director.Records)
            report.customers.Add($"{r.Name}: order={(r.ForOrder ? r.Sequence.ToString() : "-")} spawned={r.SpawnedAt:0.0} browse={r.BrowseVisits} queued={r.QueuedAt:0.0} slots={r.QueueSlotsTaken} called={r.CalledAt:0.0} from={r.CalledFrom} arrived={r.ArrivedAt:0.0} limitLeft={r.RemainingAtArrival:0.0} done={r.DoneAt:0.0} wave={r.LeaveAnimationSeconds:0.0} gone={r.GoneAt:0.0}");

        var forOrders = director.Records.FindAll(r => r.ForOrder);
        var sequences = new HashSet<int>();
        foreach (var r in forOrders) sequences.Add(r.Sequence);
        Check("customers.one_person_per_order", forOrders.Count == Game.OrderCount && sequences.Count == Game.OrderCount && !sequences.Contains(0),
            $"customers for orders={forOrders.Count}; distinct orders={sequences.Count}; orders={Game.OrderCount}");
        Check("customers.real_characters", director.Characters.Length == 4 && director.Records.TrueForAll(r => r.Avatar != "placeholder"),
            $"prefabs={director.Characters.Length}; avatars=[{string.Join(", ", director.Records.ConvertAll(r => r.Avatar))}]");
        Check("customers.one_at_a_time_at_counter", director.MaxAtCounterSeen == 1, $"max at counter={director.MaxAtCounterSeen}");
        Check("customers.all_waved_and_left", forOrders.TrueForAll(r => r.LeaveAnimationSeconds > 0f && r.GoneAt > r.DoneAt && r.DoneAt > 0f),
            $"waves=[{string.Join(", ", forOrders.ConvertAll(r => r.LeaveAnimationSeconds.ToString("0.0")))}]");
        Check("customers.no_stuck_walks", CustomerCharacter.StuckWarps == 0, $"warps={CustomerCharacter.StuckWarps}; stepped aside when blocked={CustomerCharacter.StepAsides}");
        Check("customers.reached_exact_spots", CustomerCharacter.ShortArrivals == 0, $"walks ending over 0.2 m short of a shelf spot, line place or the counter={CustomerCharacter.ShortArrivals}");
        Check("customers.on_navmesh_every_frame", report.customerFrames > 1000 && report.offNavMeshFrames == 0 && report.settledFrames > 0,
            $"customer frames={report.customerFrames}; standing as obstacles={report.settledFrames}; walking off NavMesh={report.offNavMeshFrames}");
        Check("customers.never_inside_walls_shelves_counter", report.overlapFrames == 0, $"overlapping frames={report.overlapFrames}");
        Check("customers.never_on_clerk_side", report.clerkSideFrames == 0, $"frames nearer than z={CounterFarEdge + 0.1f}: {report.clerkSideFrames}");
        float walkShare = report.movingFrames > 0 ? (float)report.walkAnimatedFrames / report.movingFrames : 0f;
        float standShare = report.standingFrames > 0 ? (float)report.standingAnimatedFrames / report.standingFrames : 0f;
        Check("customers.walk_clip_while_moving", report.movingFrames > 300 && walkShare > 0.97f, $"moving frames={report.movingFrames}; with walk clip={walkShare:P1}");
        Check("customers.no_walk_clip_while_standing", report.standingFrames > 300 && standShare > 0.97f, $"standing frames={report.standingFrames}; not walking={standShare:P1}");
        if (queue)
        {
            Check("line.never_more_than_three", director.MaxForOrdersInStore <= 3 && director.MaxWaitingSeen <= 2 && director.MaxLineSeen <= director.QueueSlots.Length,
                $"for orders in store max={director.MaxForOrdersInStore}; waiting max={director.MaxWaitingSeen}; line max={director.MaxLineSeen}");
            Check("line.filled_up", director.MaxLineSeen == director.QueueSlots.Length && director.MaxForOrdersInStore == 3,
                $"line max={director.MaxLineSeen}/{director.QueueSlots.Length}; for orders max={director.MaxForOrdersInStore}");
            var browsers = director.Records.FindAll(r => !r.ForOrder);
            Check("browsers.came_and_never_ordered", director.BrowsersSpawned >= 1 && director.MaxBrowsersSeen >= 1 && director.MaxBrowsersSeen <= director.BrowserTarget
                    && browsers.TrueForAll(r => r.CalledAt < 0f && r.QueuedAt < 0f && r.Sequence == 0 && r.BrowseVisits >= 1),
                $"spawned={director.BrowsersSpawned}; max at once={director.MaxBrowsersSeen}; visits=[{string.Join(", ", browsers.ConvertAll(r => r.BrowseVisits.ToString()))}]");
            Check("customers.at_most_six_people", director.MaxPresent <= 6, $"max present={director.MaxPresent}");
        }
        else
        {
            Check("sequential.one_at_a_time_from_the_door", director.MaxLineSeen == 0 && director.BrowsersSpawned == 0 && forOrders.TrueForAll(r => r.CalledFrom == "door"),
                $"line max={director.MaxLineSeen}; browsers={director.BrowsersSpawned}; from=[{string.Join(", ", forOrders.ConvertAll(r => r.CalledFrom))}]");
        }
    }

    /// <summary>
    /// D12: the products are made only when the customer stands at the counter, and the time limit starts then; before
    /// that the customer came in early, looked at a shelf and waited in line with an empty basket.
    /// </summary>
    void CheckArrival(int number, Order order)
    {
        var director = Game.Director;
        var record = director.Records.Find(r => r.ForOrder && r.Sequence == number);
        if (!Check($"order{number}.customer_record", record != null, "record for this order")) return;
        int items = 0;
        foreach (var line in order.Lines) items += line.Quantity;
        int inScene = FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None).Length;
        int inBaskets = 0;
        foreach (var waiting in director.Waiting)
            if (waiting != null && waiting.Basket != null) inBaskets += waiting.Basket.GetComponentsInChildren<ProductPrefab>().Length;
        Check($"order{number}.products_made_at_counter", inScene == items && director.OrderProducts.Count == items && director.CounterBasket != null && inBaskets == 0,
            $"products in scene={inScene}; order items={items}; basket on counter={director.CounterBasket != null}; in waiting baskets={inBaskets}; waiting={director.Waiting.Count}");
        float waited = record.ArrivedAt - record.SpawnedAt;
        Check($"order{number}.timer_starts_at_counter",
            Mathf.Approximately(record.RemainingAtArrival, order.TimeLimitSeconds) && director.Remaining > order.TimeLimitSeconds - 1f,
            $"limit={order.TimeLimitSeconds}; left at arrival={record.RemainingAtArrival:0.0}; now={director.Remaining:0.0}; in store before arriving={waited:0.0}s");
        var previous = director.Records.Find(r => r.ForOrder && r.Sequence == number - 1);
        float since = number == 1 ? director.ShiftStartedAt : previous != null ? previous.DoneAt : -1f;
        float gap = record.CalledAt - since;
        Check($"order{number}.called_spawn_after_prev_after", since >= 0f && Mathf.Abs(gap - order.SpawnAfterPreviousSeconds) < SpawnGapTolerance,
            $"called {gap:0.00}s after {(number == 1 ? "the shift began" : "the previous order ended")}; scenario {order.SpawnAfterPreviousSeconds}s");
        if (director.QueueAndBrowsers)
        {
            bool early = number == 1
                ? record.CalledFrom != "door"
                : record.CalledFrom == "line" && record.BrowseVisits >= 1 && record.QueuedAt > record.SpawnedAt && waited > 10f;
            Check($"order{number}.came_in_early_and_waited", early,
                $"from={record.CalledFrom}; shelves={record.BrowseVisits}; {(record.QueuedAt >= 0f ? $"in line {record.QueuedAt - record.SpawnedAt:0.0}s after coming in" : "never in line")}; waited {waited:0.0}s; line slots={record.QueueSlotsTaken}");
        }
    }

    /// <summary>Between orders the people in line hold empty baskets and no 3DGS product exists anywhere.</summary>
    void CheckBetweenOrders(int finishedOrder)
    {
        var director = Game.Director;
        int inScene = FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None).Length;
        int baskets = 0;
        foreach (var waiting in director.Waiting)
            if (waiting != null && waiting.Basket != null) baskets++;
        Check($"order{finishedOrder}.no_products_until_next_arrives", inScene == 0 && director.CounterBasket == null && baskets == director.Waiting.Count,
            $"products={inScene}; counter basket={director.CounterBasket != null}; waiting={director.Waiting.Count} with basket={baskets}; line={director.Line.Count}");
    }

    /// <summary>
    /// A second camera over the whole customer side, drawn over the view for one frame. The test view looks down at the
    /// counter, and the simulator's mouse drives the hand, so the head cannot be turned for this without changing input.
    /// </summary>
    IEnumerator CaptureFrom(string name, Vector3 position, Vector3 lookAt, float fieldOfView)
    {
        var shot = new GameObject("Evidence Camera").AddComponent<Camera>();
        shot.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position));
        shot.fieldOfView = fieldOfView;
        shot.nearClipPlane = 0.05f;
        shot.depth = view.depth + 10f;
        yield return null;
        yield return Capture(name);
        Destroy(shot.gameObject);
        yield return null;
    }

    IEnumerator Tutorial()
    {
        var tutorial = Game.Tutorial;
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Grab, StepTimeout, "tutorial.grab_shown");
        if (finished) yield break;
        var product = tutorial.Product;
        yield return Capture("02_tutorial_grab");
        yield return GrabProduct(product, "tutorial");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Rotate, StepTimeout, "tutorial.rotate_shown");
        yield return TurnBarcodeDown(product);
        yield return MoveHeldAbovePad(product);
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Scan, StepTimeout, "tutorial.scan_shown");
        yield return Capture("03_tutorial_barcode_down");
        yield return LowerUntilScanned(product, "tutorial.scan");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Bag, StepTimeout, "tutorial.bag_shown");
        yield return PutInBag(product, "tutorial.bag");
        yield return ResetHandRotation();
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Card, StepTimeout, "tutorial.card_shown");
        yield return PayByCard(() => tutorial.Step != TutorialStep.Card, "tutorial.card");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Cash, StepTimeout, "tutorial.cash_shown");
        yield return Capture("04_tutorial_cash");
        yield return GiveBill("tutorial.cash_bill");
        yield return GiveChange(3500, 0, false, () => tutorial.Step != TutorialStep.Cash, "tutorial.cash_change");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Done, StepTimeout, "tutorial.done");
    }

    IEnumerator Shift()
    {
        var director = Game.Director;
        var pos = Game.Pos;
        for (int customer = 1; customer <= 5 && !finished; customer++)
        {
            int number = customer;
            if (number > 1)
            {
                // Destroy takes effect at the end of the frame the order ended in.
                yield return new WaitForSeconds(0.3f);
                CheckBetweenOrders(number - 1);
            }
            yield return WaitUntil(() => director.CustomerNumber == number && pos.Current != null, 40f, $"order{number}.arrived");
            if (finished) yield break;
            var order = pos.Current;
            CheckArrival(number, order);
            if (number == 2)
            {
                // What the clerk sees at the counter, and the whole customer side with the line and the shelves.
                yield return CaptureFrom("16_customer_at_counter", new Vector3(0f, 1.55f, 0.15f), new Vector3(0f, 1.25f, 1.6f), 70f);
                yield return CaptureFrom("17_customer_side_overview", new Vector3(0.2f, 2.4f, -1.4f), new Vector3(-0.4f, 0.7f, 3.4f), 80f);
                // The line from its right-hand side, where the empty baskets hang.
                yield return CaptureFrom("18_line_with_empty_baskets", new Vector3(-1.3f, 1.35f, 2.2f), new Vector3(0.25f, 0.85f, 2.55f), 60f);
            }
            if (walking) yield return WalkBeforeOrder(number);
            yield return Capture($"1{number}a_order{number}_arrived");

            if (number == 1) yield return TryCardBeforeScanning();
            if (number == 2) yield return TryBillBeforeScanning();

            var products = new List<ProductPrefab>(director.OrderProducts);
            for (int i = 0; i < products.Count && !finished; i++)
            {
                var product = products[i];
                string label = $"order{number}.item{i}";
                yield return GrabProduct(product, label);
                if (walking && !walkedHoldingProduct) yield return WalkHoldingProduct(product);
                if (number == 1 && i == 0) yield return PressUprightOnPad(product);
                if (number == 5) yield return DropOnFloorAndGrabAgain(product);
                yield return TurnBarcodeDown(product);
                if (number == 4 && i == 0) yield return TryBaggingBeforeScanning(product);
                yield return MoveHeldAbovePad(product);
                yield return LowerUntilScanned(product, label + ".scan");
                if (number == 3 && i == 0) yield return OverscanAndCancel(product);
                else if (order.NeedsBag) yield return PutInBag(product, label + ".bag");
                else yield return SetAside(product, i);
                yield return ResetHandRotation();
            }
            Check($"order{number}.ready_for_payment", pos.ReadyForPayment, $"allScanned={pos.AllScanned} allBagged={pos.AllBagged} total={pos.Total}");
            yield return Capture($"1{number}b_order{number}_ready");

            if (number == 5) yield return DropCardOffCounter();
            if (order.Payment == PaymentMethod.Card)
            {
                yield return PayByCard(() => pos.Current == null, $"order{number}.card");
            }
            else
            {
                int total = pos.Total;
                int bill = PaymentDesk.BillFor(total);
                yield return GiveBill($"order{number}.bill");
                int expectedErrors = 0;
                if (number == 4)
                {
                    yield return GiveTooMuchChange();
                    expectedErrors = 1;
                }
                bool pauseOnce = number == 2;
                if (pauseOnce) expectedErrors = 1;
                yield return GiveChange(bill - total, expectedErrors, pauseOnce, () => pos.Current == null, $"order{number}.change");
            }
            yield return WaitUntil(() => pos.Current == null, 10f, $"order{number}.completed");
        }
    }

    // ---------- Walking during the shift ----------

    /// <summary>Stands somewhere else for each order, so nothing that follows may depend on the player being at the start.</summary>
    IEnumerator WalkBeforeOrder(int number)
    {
        var rig = FindFirstObjectByType<XROrigin>().transform;
        var from = rig.position;
        switch (number)
        {
            case 1: yield return Stick(Key.L, 0.5f); break;
            case 2: yield return Stick(Key.J, 1f); break;
            case 3:
                // Up against the counter, with the payment things lying on it. What the customer just set down settles first.
                yield return new WaitForSeconds(1.5f);
                var things = new List<(Transform item, Vector3 position)>();
                foreach (var prop in FindObjectsByType<PaymentProp>(FindObjectsSortMode.None)) things.Add((prop.transform, prop.transform.position));
                foreach (var product in FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None)) things.Add((product.transform, product.transform.position));
                yield return Stick(Key.L, 0.5f);
                yield return Stick(Key.I, 0.5f);
                yield return new WaitForSeconds(0.3f);
                var moved = things.FindAll(t => t.item != null && Vector3.Distance(t.item.position, t.position) > 0.01f);
                Check("walk.counter_contact_moves_nothing", moved.Count == 0 && things.Count > 2,
                    $"checked={things.Count}; moved=[{string.Join(", ", moved.ConvertAll(t => $"{t.item.name} {t.position.ToString("F3")}->{t.item.position.ToString("F3")}"))}]; rig z={rig.position.z:0.000}");
                break;
            case 4: yield return Stick(Key.K, 0.33f); break;
            case 5: yield return Stick(Key.L, 0.4f); break;
        }
        Check($"walk.before_order{number}", Vector3.Distance(from, rig.position) > 0.1f && Mathf.Abs(rig.position.y) < 0.01f, $"{from.ToString("F3")}->{rig.position.ToString("F3")}");
    }

    IEnumerator WalkHoldingProduct(ProductPrefab product)
    {
        walkedHoldingProduct = true;
        yield return WalkHolding(product.transform, () => product.Grab.isSelected, "walk.holding_product");
    }

    /// <summary>A prop has no collision exception of its own, so it is also held inside the body, where the layers must keep them apart.</summary>
    IEnumerator WalkHoldingProp(PaymentProp prop)
    {
        walkedHoldingProp = true;
        yield return WalkHolding(prop.transform, () => prop.Grab.isSelected, "walk.holding_prop");
        var rig = FindFirstObjectByType<XROrigin>().transform;
        var before = rig.position;
        var start = prop.transform.position;
        var inside = new Vector3(view.transform.position.x, 1.0f, view.transform.position.z);
        yield return MoveProp(prop, new Vector3(start.x, TravelHeight, start.z), "walk.prop_rise");
        yield return MoveProp(prop, new Vector3(inside.x, TravelHeight, inside.z), "walk.prop_over_body");
        yield return MoveProp(prop, inside, "walk.prop_into_body");
        yield return new WaitForSeconds(0.3f);
        Check("walk.prop_inside_body_touches_nothing",
            prop.Grab.isSelected && Vector3.Distance(prop.transform.position, inside) < CarriedDrift && Vector3.Distance(before, rig.position) < 0.001f,
            $"prop off target by {Vector3.Distance(prop.transform.position, inside):0.000} m; rig moved {Vector3.Distance(before, rig.position):0.0000}");
        yield return MoveProp(prop, new Vector3(inside.x, TravelHeight, inside.z), "walk.prop_out_of_body");
    }

    IEnumerator WalkHolding(Transform held, Func<bool> stillHeld, string label)
    {
        // Off the counter first, so what it is measured against is the walking and not the things standing on the counter.
        yield return MoveHand(rightHand.position + Vector3.up * 0.25f, label + ".lift");
        yield return new WaitForSeconds(0.3f);
        var offset = held.position - rightHand.position;
        float furthest = 0f;
        foreach (var key in new[] { Key.J, Key.L })
        {
            heldKeys.Add(key);
            float begun = Time.time;
            while (Time.time - begun < 0.4f)
            {
                yield return null;
                furthest = Mathf.Max(furthest, Vector3.Distance(held.position - rightHand.position, offset));
            }
            heldKeys.Remove(key);
            yield return new WaitForSeconds(0.2f);
        }
        float settled = Vector3.Distance(held.position - rightHand.position, offset);
        Check(label, stillHeld() && settled < CarriedDrift, $"held={stillHeld()}; settled off by {settled:0.000} m; furthest {furthest:0.000} m while walking at {WalkSpeed} m/s");

        // A snap turn takes the hand half a meter in one frame, and the held thing has to catch up by physics.
        var handBefore = rightHand.position;
        foreach (var key in new[] { Key.D, Key.A })
        {
            heldKeys.Add(key);
            yield return new WaitForSeconds(0.1f);
            heldKeys.Remove(key);
            yield return new WaitForSeconds(0.7f);
            if (key == Key.D)
                Check(label + ".through_snap_turn", stillHeld() && Vector3.Distance(handBefore, rightHand.position) > 0.1f && Vector3.Distance(held.position - rightHand.position, offset) < 0.6f,
                    $"held={stillHeld()}; hand moved {Vector3.Distance(handBefore, rightHand.position):0.000} m; held thing to hand {Vector3.Distance(held.position, rightHand.position):0.000} m");
        }
        float afterTurns = Vector3.Distance(held.position - rightHand.position, offset);
        Check(label + ".after_snap_turns", stillHeld() && afterTurns < CarriedDrift && Vector3.Distance(handBefore, rightHand.position) < 0.01f, $"held={stillHeld()}; off by {afterTurns:0.000} m; hand back within {Vector3.Distance(handBefore, rightHand.position):0.000} m");
    }

    IEnumerator Stick(Key key, float meters)
    {
        heldKeys.Add(key);
        yield return new WaitForSeconds(meters / WalkSpeed);
        heldKeys.Remove(key);
        yield return new WaitForSeconds(0.45f);
    }

    // ---------- Mistakes the rules must stop ----------

    IEnumerator TryCardBeforeScanning()
    {
        var card = Desk.CustomerCard;
        int rejected = cardsRejected;
        var plate = card.transform.position;
        var slot = Desk.CardSlot;
        yield return GrabProp(card, "rule.card_early.grab");
        yield return InsertCard(card, "rule.card_early", null);
        yield return WaitUntil(() => cardsRejected > rejected, 3f, "rule.card_rejected_before_scan");
        Check("rule.card_early_not_paid",
            Game.Pos.Current != null && Game.Pos.Notice.Contains("먼저") && card != null && card.Grab.isSelected,
            $"notice={Game.Pos.Notice}; stillHeld={card != null && card.Grab.isSelected}");
        Check("rule.card_early.in_slot_frame_lit",
            slot.CardInside && slot.Frame.Length > 0 && slot.Frame[0].sharedMaterial == slot.FrameLit && !slot.Guide.activeSelf,
            $"inside={slot.CardInside}; frame={(slot.Frame.Length > 0 ? slot.Frame[0].sharedMaterial.name : "none")}; guide={slot.Guide.activeSelf}");
        yield return Capture("11x_card_rejected_before_scan");
        // Let go: the card stays seated in the slot instead of dropping, and the hand can take it back out.
        heldKeys.Remove(Key.G);
        yield return new WaitForSeconds(0.6f);
        var socket = FindFirstObjectByType<CardSlotSocket>();
        Check("rule.card_early.stays_in_slot_when_let_go",
            card != null && socket != null && socket.hasSelection && slot.CardInside && Game.Pos.Current != null,
            $"socketHolds={socket != null && socket.hasSelection}; inside={slot.CardInside}; card={(card != null ? card.transform.position.ToString("F3") : "gone")}");
        if (card == null) yield break;
        yield return MoveHand(card.transform.position, "rule.card_early.regrab.reach");
        heldKeys.Add(Key.G);
        yield return WaitUntil(() => card != null && card.Grab.interactorsSelecting.Contains(rightInteractor), 2f, "rule.card_early.regrab");
        // Out the way it went in: straight up would hit the terminal's top.
        yield return MoveProp(card, CardSlotFront(), "rule.card_early.out");
        yield return CarryProp(card, plate + Vector3.up * 0.04f, "rule.card_early.back");
        yield return SetDown(card.GetComponent<Rigidbody>());
        yield return new WaitForSeconds(0.4f);
    }

    IEnumerator TryBillBeforeScanning()
    {
        var bill = Desk.CustomerBill;
        var home = bill.transform.position;
        yield return GrabProp(bill, "rule.bill_early.grab");
        yield return CarryProp(bill, Desk.Drawer.transform.position, "rule.bill_early");
        yield return SetDown(bill.GetComponent<Rigidbody>());
        yield return new WaitForSeconds(0.5f);
        Check("rule.bill_returned_before_scan",
            bill != null && Desk.CustomerBill == bill && Game.Pos.Received == 0 && Game.Pos.Notice.Contains("먼저") && Vector3.Distance(bill.transform.position, home) < 0.03f,
            $"received={Game.Pos.Received}; notice={Game.Pos.Notice}");
        yield return Capture("12x_bill_returned_before_scan");
    }

    /// <summary>The body and the barcode's lower edge both touch the zone, but the barcode faces sideways.</summary>
    IEnumerator PressUprightOnPad(ProductPrefab product)
    {
        int scans = scansSeen;
        int bodies = Scanner.IgnoredBodyEntries;
        int angled = Scanner.AngledBarcodeEntries;
        yield return MoveHeldAbovePad(product);
        float padTop = Scanner.transform.position.y - 0.004f;
        var target = rightHand.position;
        float begun = Time.realtimeSinceStartup;
        while (LowestProxyPoint(product) > padTop + 0.002f && Time.realtimeSinceStartup - begun < LowerTimeout)
        {
            target += Vector3.down * PressStep;
            yield return MoveHand(target, "rule.upright.lower");
            yield return new WaitForSeconds(0.03f);
        }
        yield return new WaitForSeconds(0.3f);
        yield return Capture("11y_upright_on_pad_no_scan");
        var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        Check("rule.upright_press_no_scan",
            scansSeen == scans && Scanner.IgnoredBodyEntries > bodies,
            $"scans {scans}->{scansSeen}; bodyEntries {bodies}->{Scanner.IgnoredBodyEntries}; angledBarcodeEntries {angled}->{Scanner.AngledBarcodeEntries}; barcode angle from down={Vector3.Angle(normal, Vector3.down):0}; lowest={LowestProxyPoint(product):0.000}; padTop={padTop:0.000}");
    }

    /// <summary>Lets an unscanned product go in the bag, sees it refused, and picks it up again. It stays barcode-down.</summary>
    IEnumerator TryBaggingBeforeScanning(ProductPrefab product)
    {
        int bagged = bagsSeen;
        yield return CarryToBagAndRelease(product, "rule.bag_early");
        yield return new WaitForSeconds(1.2f);
        Check("rule.unscanned_not_bagged",
            bagsSeen == bagged && !product.IsBagged && Game.Pos.Notice.Contains("스캔"),
            $"bagged={product.IsBagged}; notice={Game.Pos.Notice}");
        yield return Capture("14x_unscanned_in_bag");
        yield return GrabProduct(product, "rule.bag_early.regrab");
    }

    IEnumerator DropOnFloorAndGrabAgain(ProductPrefab product)
    {
        int respawns = product.RespawnCount;
        var center = product.transform.position;
        yield return MoveProduct(product, new Vector3(center.x, TravelHeight, center.z), "rule.drop.rise");
        yield return MoveProduct(product, new Vector3(OffCounterDrop.x, TravelHeight, OffCounterDrop.z), "rule.drop.over");
        yield return MoveProduct(product, OffCounterDrop, "rule.drop.lower");
        heldKeys.Remove(Key.G);
        yield return WaitUntil(() => product.RespawnCount > respawns, 8f, "rule.dropped_product_returns");
        yield return new WaitForSeconds(0.8f);
        Check("rule.dropped_product_back_on_counter",
            product.transform.position.y > CounterTop && Mathf.Abs(product.transform.position.x - center.x) < 0.05f,
            $"position={product.transform.position.ToString("F3")}");
        yield return GrabProduct(product, "rule.drop.regrab");
    }

    IEnumerator DropCardOffCounter()
    {
        var card = Desk.CustomerCard;
        yield return GrabProp(card, "rule.card_drop.grab");
        var center = card.transform.position;
        yield return CarryProp(card, OffCounterDrop, "rule.card_drop");
        // Stop, then let go, like every other set-down. Let go while the hand still moved toward the counter, the card was
        // thrown at the counter's front: usually it bounced down and came back, once it landed on top and stayed (2026-11-08).
        yield return SetDown(card.GetComponent<Rigidbody>());
        // Log only (no check): where the card goes.
        StartCoroutine(TraceProp(card, 6.5f));
        yield return WaitUntil(() => card.transform.position.y > CounterTop && card.GetComponent<Rigidbody>().linearVelocity.magnitude < 0.05f && Vector3.Distance(card.transform.position, center) < 0.05f, 6f, "rule.dropped_card_returns");
    }

    IEnumerator TraceProp(PaymentProp prop, float seconds)
    {
        float begun = Time.realtimeSinceStartup;
        while (prop != null && Time.realtimeSinceStartup - begun < seconds)
        {
            var body = prop.GetComponent<Rigidbody>();
            Debug.Log($"[P2 Trace] {prop.name} at {prop.transform.position.ToString("F3")} v={body.linearVelocity.ToString("F2")} held={prop.Grab.isSelected} sleeping={body.IsSleeping()}");
            yield return new WaitForSeconds(0.25f);
        }
    }

    IEnumerator GiveTooMuchChange()
    {
        int errors = Desk.Tray.ErrorCount;
        yield return new WaitForSeconds(0.2f);
        var bill = Desk.StockItems[5000];
        yield return GrabProp(bill, "rule.too_much.grab");
        yield return CarryProp(bill, Desk.Tray.transform.position, "rule.too_much");
        yield return SetDown(bill.GetComponent<Rigidbody>());
        yield return WaitUntil(() => Desk.Tray.ErrorCount > errors, 3f, "rule.too_much_change_is_error");
        Check("rule.too_much_not_paid", Game.Pos.Current != null && Game.Pos.Notice.Contains("많습니다"), $"notice={Game.Pos.Notice}");
        yield return Capture("14y_too_much_change");
        yield return GrabProp(bill, "rule.too_much.take_back");
        var drawer = Desk.Drawer.transform.position;
        yield return CarryProp(bill, new Vector3(drawer.x, drawer.y, drawer.z), "rule.too_much.back");
        yield return SetDown(bill.GetComponent<Rigidbody>());
        yield return WaitUntil(() => Desk.Tray.Amount == 0, 3f, "rule.tray_empty_after_taking_back");
        yield return new WaitForSeconds(0.3f);
    }

    /// <summary>Scans again on purpose, lets go, and presses the POS [-] with the ray and trigger.</summary>
    IEnumerator OverscanAndCancel(ProductPrefab product)
    {
        yield return new WaitForSeconds(ScanCooldown);
        yield return MoveHeldAbovePad(product);
        yield return LowerUntilScanned(product, "cancel.second_scan");
        var line = Game.Pos.Lines.Find(l => l.Data == product.Data);
        Check("cancel.overscan_blocks_payment", line != null && line.Scanned == 2 && !Game.Pos.ReadyForPayment && Game.Pos.Notice.Contains("취소"), $"scanned={line?.Scanned}; notice={Game.Pos.Notice}");
        yield return Capture("13x_overscan");
        yield return SetAside(product, 0);
        yield return ResetHandRotation();

        int row = Game.Pos.Lines.IndexOf(line);
        var button = Screen.CancelButtons[row].transform.position;
        yield return MoveHand(new Vector3(-0.3f, 1.2f, 0.4f), "cancel.hand");
        var direction = (button - rightHand.position).normalized;
        float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Asin(direction.y) * Mathf.Rad2Deg;
        yield return RotateHand(new Vector3(pitch, yaw, 0f));
        yield return new WaitForSeconds(0.5f);
        int cancels = Game.Pos.CancelCount;
        leftButton = true;
        yield return new WaitForSeconds(0.2f);
        leftButton = false;
        yield return new WaitForSeconds(0.3f);
        Check("cancel.minus_button", Game.Pos.CancelCount == cancels + 1 && line.Scanned == 1 && Game.Pos.Notice.Length == 0, $"cancels {cancels}->{Game.Pos.CancelCount}; scanned={line.Scanned}; notice={Game.Pos.Notice}");
        yield return Capture("13y_after_cancel");
    }

    // ---------- Normal actions, all through the input devices ----------

    IEnumerator GrabProduct(ProductPrefab product, string label)
    {
        yield return MoveHand(new Vector3(rightHand.position.x, TravelHeight, rightHand.position.z), label + ".rise");
        var target = product.transform.position;
        yield return MoveHand(new Vector3(target.x, TravelHeight, target.z), label + ".over");
        yield return MoveHand(target, label + ".reach");
        yield return new WaitForSeconds(0.2f);
        heldKeys.Add(Key.G);
        yield return WaitUntil(() => product.Grab.isSelected, 2f, label + ".selected");
        yield return new WaitForSeconds(0.2f);
        Check(label + ".near_grab", rightInteractor.selectionRegion.Value == NearFarInteractor.Region.Near, $"region={rightInteractor.selectionRegion.Value}");
    }

    IEnumerator GrabProp(PaymentProp prop, string label)
    {
        if (prop == null)
        {
            Check(label + ".prop_exists", false, "no prop");
            yield break;
        }
        var target = prop.transform.position;
        yield return MoveHand(new Vector3(rightHand.position.x, TravelHeight, rightHand.position.z), label + ".rise");
        yield return MoveHand(new Vector3(target.x, TravelHeight, target.z), label + ".over");
        yield return MoveHand(target, label + ".reach");
        yield return new WaitForSeconds(0.2f);
        heldKeys.Add(Key.G);
        yield return WaitUntil(() => prop == null || prop.Grab.isSelected, 2f, label + ".selected");
        yield return new WaitForSeconds(0.2f);
        if (walking && !walkedHoldingProp && prop != null) yield return WalkHoldingProp(prop);
    }

    /// <summary>Stops, then lets go. Records how fast the held thing was moving, on arrival and at the release.</summary>
    IEnumerator SetDown(Rigidbody held)
    {
        report.maxHeldSpeedOnArrival = Mathf.Max(report.maxHeldSpeedOnArrival, held.linearVelocity.magnitude);
        yield return new WaitForSeconds(SettleBeforeRelease);
        report.maxHeldSpeedAtRelease = Mathf.Max(report.maxHeldSpeedAtRelease, held.linearVelocity.magnitude);
        heldKeys.Remove(Key.G);
    }

    /// <summary>Up, across and down, so a held prop does not plow through things on the counter.</summary>
    IEnumerator CarryProp(PaymentProp prop, Vector3 target, string label)
    {
        var center = prop.transform.position;
        yield return MoveProp(prop, new Vector3(center.x, TravelHeight, center.z), label + ".rise");
        yield return MoveProp(prop, new Vector3(target.x, TravelHeight, target.z), label + ".over");
        yield return MoveProp(prop, target, label + ".lower");
    }

    /// <summary>Lifts, then turns the wrist until the printed barcode faces down.</summary>
    IEnumerator TurnBarcodeDown(ProductPrefab product)
    {
        yield return MoveProduct(product, product.transform.position + Vector3.up * 0.2f, "turn.lift");
        // A product may have toppled, so its barcode can point anywhere: turn about the dominant axis, then look again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
            if (Vector3.Angle(normal, Vector3.down) < 6f) break;
            if (Mathf.Abs(normal.x) <= Mathf.Abs(normal.z) || Mathf.Abs(normal.x) < 0.1f)
                yield return RotateHand(new Vector3(Mathf.DeltaAngle(0f, 180f - Mathf.Atan2(normal.z, normal.y) * Mathf.Rad2Deg), 0f, 0f));
            else
                yield return RotateHand(new Vector3(0f, 0f, Mathf.Atan2(-normal.x, -normal.y) * Mathf.Rad2Deg));
            yield return new WaitForSeconds(0.5f);
        }
        var turned = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        Check("turn.barcode_faces_down", Vector3.Angle(turned, Vector3.down) < 10f, $"{product.Data.Id} angle from down={Vector3.Angle(turned, Vector3.down):0.0}");
    }

    IEnumerator ResetHandRotation()
    {
        yield return RotateHand(-handEuler);
    }

    /// <summary>
    /// Adds to the simulator's Euler angles for the hand. This keeps its own running total because reading angles back
    /// from a rotation is ambiguous at 90 degrees of pitch, which is exactly where a barcode-down hand sits.
    /// </summary>
    IEnumerator RotateHand(Vector3 degrees)
    {
        float xSensitivity = simulator.mouseXRotateSensitivity;
        float ySensitivity = simulator.mouseYRotateSensitivity;
        heldKeys.Add(Key.LeftCtrl);
        yield return null;
        // Mouse X is yaw, and mouse Y is pitch with an inverted sign.
        if (Mathf.Abs(degrees.x) > 0.01f || Mathf.Abs(degrees.y) > 0.01f)
            yield return FeedMouse(new Vector2(degrees.y / xSensitivity, -degrees.x / ySensitivity), RotateDegreesPerSecond / ySensitivity);
        if (Mathf.Abs(degrees.z) > 0.01f)
        {
            // Holding Z makes mouse X roll, with an inverted sign.
            heldKeys.Add(Key.Z);
            yield return null;
            yield return FeedMouse(new Vector2(-degrees.z / xSensitivity, 0f), RotateDegreesPerSecond / xSensitivity);
            heldKeys.Remove(Key.Z);
            yield return null;
        }
        yield return null;
        heldKeys.Remove(Key.LeftCtrl);
        handEuler += degrees;
        yield return null;
    }

    IEnumerator MoveHeldAbovePad(ProductPrefab product)
    {
        var pad = Scanner.transform.position;
        var center = product.transform.position;
        float aboveLowest = center.y - LowestProxyPoint(product);
        float travel = Mathf.Max(center.y, TravelHeight);
        yield return MoveProduct(product, new Vector3(center.x, travel, center.z), "pad.rise");
        yield return MoveProduct(product, new Vector3(pad.x, travel, pad.z), "pad.over");
        yield return MoveProduct(product, new Vector3(pad.x, pad.y + AbovePad + aboveLowest, pad.z), "pad.above");
    }

    IEnumerator LowerUntilScanned(ProductPrefab product, string label)
    {
        int before = scansSeen;
        var target = rightHand.position;
        float begun = Time.realtimeSinceStartup;
        while (scansSeen == before && Time.realtimeSinceStartup - begun < LowerTimeout)
        {
            target += Vector3.down * PressStep;
            yield return MoveHand(target, label + ".lower");
            yield return new WaitForSeconds(0.05f);
        }
        Check(label, scansSeen > before, $"{product.Data.Id} scans {before}->{scansSeen} lowest={LowestProxyPoint(product):0.000}");
        yield return MoveProduct(product, product.transform.position + Vector3.up * 0.1f, label + ".lift");
    }

    IEnumerator CarryToBagAndRelease(ProductPrefab product, string label)
    {
        var bag = Bag.transform.position;
        var center = product.transform.position;
        float travel = Mathf.Max(center.y, TravelHeight);
        yield return MoveProduct(product, new Vector3(center.x, travel, center.z), label + ".rise");
        yield return MoveProduct(product, new Vector3(bag.x, travel, bag.z), label + ".over");
        yield return MoveProduct(product, new Vector3(bag.x, CounterTop + BagDropHeight, bag.z), label + ".lower");
        yield return SetDown(product.GetComponent<Rigidbody>());
    }

    IEnumerator PutInBag(ProductPrefab product, string label)
    {
        int before = bagsSeen;
        yield return CarryToBagAndRelease(product, label);
        yield return WaitUntil(() => bagsSeen > before, 5f, label);
        yield return new WaitForSeconds(0.2f);
    }

    IEnumerator SetAside(ProductPrefab product, int index)
    {
        var center = product.transform.position;
        float travel = Mathf.Max(center.y, TravelHeight);
        var spot = new Vector3(0.5f - index * 0.24f, travel, 0.44f);
        float aboveLowest = center.y - LowestProxyPoint(product);
        yield return MoveProduct(product, new Vector3(center.x, travel, center.z), "aside.rise");
        yield return MoveProduct(product, spot, "aside.over");
        yield return MoveProduct(product, new Vector3(spot.x, CounterTop + aboveLowest + 0.03f, spot.z), "aside.lower");
        yield return SetDown(product.GetComponent<Rigidbody>());
        yield return new WaitForSeconds(0.6f);
    }

    IEnumerator PayByCard(Func<bool> paid, string label)
    {
        var card = Desk.CustomerCard;
        var slot = Desk.CardSlot;
        Check(label + ".guide_hidden_before_grab", slot.Guide != null && !slot.Guide.activeSelf, $"guide={(slot.Guide != null ? slot.Guide.activeSelf.ToString() : "missing")}");
        yield return GrabProp(card, label + ".grab");
        if (card == null) yield break;
        yield return InsertCard(card, label, label == "tutorial.card" ? "03c_tutorial_card_guide" : null);
        yield return WaitUntil(paid, 5f, label + ".approved");
        heldKeys.Remove(Key.G);
        yield return new WaitForSeconds(0.2f);
        Check(label + ".guide_hidden_after_payment", !slot.Guide.activeSelf && !slot.CardInside, $"guide={slot.Guide.activeSelf}; inside={slot.CardInside}");
    }

    /// <summary>
    /// The slot is an opening in the terminal's face toward the clerk, so the card comes in level from that side, the way the
    /// guide shows, not down from above.
    /// </summary>
    IEnumerator InsertCard(PaymentProp card, string label, string capture)
    {
        var guide = Desk.CardSlot.Guide.transform;
        var front = CardSlotFront();
        var center = card.transform.position;
        yield return MoveProp(card, new Vector3(center.x, TravelHeight, center.z), label + ".rise");
        yield return MoveProp(card, new Vector3(front.x, TravelHeight, front.z), label + ".over");
        yield return MoveProp(card, front, label + ".front");
        if (card == null) yield break;
        Check(label + ".guide_shown_while_held", Desk.CardSlot.Guide.activeSelf, $"guide={Desk.CardSlot.Guide.activeSelf}; held={card.Grab.isSelected}");
        if (capture != null) yield return Capture(capture);
        if (card == null) yield break;
        yield return MoveProp(card, guide.position, label + ".insert");
        // Approval may already have taken the card away; otherwise it has to be inside the slot by now.
        if (card != null)
        {
            var socket = FindFirstObjectByType<CardSlotSocket>();
            Check(label + ".card_in_slot", Desk.CardSlot.CardInside,
                $"card={card.transform.position.ToString("F3")} euler={card.transform.eulerAngles.ToString("F1")} target={guide.position.ToString("F3")} hand={rightHand.position.ToString("F3")} socketHover={socket != null && socket.hasHover}");
        }
        if (capture != null && card != null) yield return Capture(capture.Replace("guide", "inserted"));
    }

    Vector3 CardSlotFront()
    {
        var guide = Desk.CardSlot.Guide.transform;
        return guide.position - guide.forward * CardApproach;
    }

    IEnumerator GiveBill(string label)
    {
        var bill = Desk.CustomerBill;
        yield return GrabProp(bill, label + ".grab");
        if (bill == null) yield break;
        yield return CarryProp(bill, Desk.Drawer.transform.position, label);
        yield return SetDown(bill.GetComponent<Rigidbody>());
        yield return WaitUntil(() => bill == null, 3f, label + ".received");
        yield return new WaitForSeconds(0.3f);
    }

    /// <summary>Lays the change down one piece at a time, as a person would, which must not raise false errors.</summary>
    IEnumerator GiveChange(int change, int expectedErrors, bool pauseAfterFirstPiece, Func<bool> done, string label)
    {
        int remaining = change;
        int offset = 0;
        var tray = Desk.Tray.transform.position;
        foreach (var value in Denominations)
        {
            while (remaining >= value && !finished)
            {
                yield return new WaitForSeconds(0.2f);
                if (!Desk.StockItems.TryGetValue(value, out var item) || item == null)
                {
                    Check(label + ".stock", false, $"no {value} in the drawer");
                    yield break;
                }
                yield return GrabProp(item, $"{label}.take{value}");
                var spot = tray + new Vector3(-0.08f + (offset % 3) * 0.08f, -0.03f + 0.02f * offset, -0.04f + (offset / 3) * 0.08f);
                yield return CarryProp(item, spot, label);
                yield return SetDown(item.GetComponent<Rigidbody>());
                yield return new WaitForSeconds(0.25f);
                remaining -= value;
                offset++;
                if (pauseAfterFirstPiece && offset == 1 && remaining > 0)
                {
                    int errors = Desk.Tray.ErrorCount;
                    yield return new WaitForSeconds(StoppedAddingWait);
                    Check("rule.short_change_after_stopping", Desk.Tray.ErrorCount == errors + 1 && Game.Pos.Current != null && Game.Pos.Notice.Contains("적습니다"), $"errors {errors}->{Desk.Tray.ErrorCount}; notice={Game.Pos.Notice}");
                    yield return Capture("12y_short_change");
                }
            }
        }
        yield return WaitUntil(done, 5f, label + ".matched");
        Check(label + ".no_false_errors", Desk.Tray.ErrorCount == expectedErrors, $"errors={Desk.Tray.ErrorCount}; expected={expectedErrors}; pieces={offset}");
    }

    IEnumerator MoveProduct(ProductPrefab product, Vector3 target, string label)
    {
        yield return MoveHand(target - (product.transform.position - rightHand.position), label);
    }

    IEnumerator MoveProp(PaymentProp prop, Vector3 target, string label)
    {
        yield return MoveHand(target - (prop.transform.position - rightHand.position), label);
    }

    IEnumerator MoveHand(Vector3 target, string label)
    {
        float sensitivity = simulator.mouseXTranslateSensitivity;
        float begun = Time.realtimeSinceStartup;
        while (!finished)
        {
            var error = target - rightHand.position;
            float across = Vector3.Dot(error, view.transform.right);
            float upward = Vector3.Dot(error, view.transform.up);
            float depth = Vector3.Dot(error, view.transform.forward);
            bool needPlane = Mathf.Abs(across) > MoveTolerance || Mathf.Abs(upward) > MoveTolerance;
            if (!needPlane && Mathf.Abs(depth) <= MoveTolerance) break;
            if (Time.realtimeSinceStartup - begun > MoveTimeout)
            {
                Check("move." + label, false, $"timeout; error={error.magnitude:F4}m target={target.ToString("F3")}");
                break;
            }
            float maxMouse = MaxHandSpeed * Time.deltaTime / sensitivity;
            if (needPlane)
            {
                heldKeys.Remove(Key.Z);
                mouseDelta = Vector2.ClampMagnitude(new Vector2(across, upward) * MoveGain / sensitivity, maxMouse);
            }
            else
            {
                heldKeys.Add(Key.Z);
                mouseDelta = Vector2.ClampMagnitude(new Vector2(depth, 0f) * MoveGain / sensitivity, maxMouse);
            }
            yield return null;
        }
        heldKeys.Remove(Key.Z);
        mouseDelta = Vector2.zero;
        yield return null;
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

    IEnumerator WaitUntil(Func<bool> condition, float timeout, string label)
    {
        float begun = Time.realtimeSinceStartup;
        while (!condition())
        {
            if (Time.realtimeSinceStartup - begun > timeout)
            {
                Check(label, false, $"timed out after {timeout}s; state={Game.State}; tutorial={Game.Tutorial.Step}; notice={Game.Pos.Notice}; trayAmount={Desk.Tray.Amount}; hand={rightHand.position.ToString("F3")}");
                Finish();
                yield break;
            }
            yield return null;
        }
        Check(label, true, $"after {Time.realtimeSinceStartup - begun:0.0}s");
    }

    IEnumerator Capture(string name)
    {
        ScreenCapture.CaptureScreenshot(Path.Combine(output, name + ".png"), UnityEngine.Screen.width < 1200 ? 2 : 1);
        yield return null;
        yield return null;
    }

    float LowestProxyPoint(ProductPrefab product)
    {
        var half = product.Proxy.size * 0.5f;
        float lowest = float.PositiveInfinity;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    lowest = Mathf.Min(lowest, product.transform.TransformPoint(product.Proxy.center + new Vector3(x * half.x, y * half.y, z * half.z)).y);
        return lowest;
    }

    /// <summary>Korean names that went through the wrong encoding end up with question marks and hanja.</summary>
    void CheckNames()
    {
        var broken = new List<string>();
        foreach (var product in Game.Catalog.Products)
            foreach (char c in product.Data.Name)
                if (c == '?' || (c >= 0x4E00 && c <= 0x9FFF))
                {
                    broken.Add(product.Data.Id);
                    break;
                }
        Check("setup.product_names_readable", broken.Count == 0 && Game.Catalog.Products.Count == 12, $"products={Game.Catalog.Products.Count}; broken={string.Join(",", broken)}");
    }

    /// <summary>
    /// Day 1 has no intake, yet every slot is full: one splat picture per place, with no collider, body, grab or
    /// ProductPrefab, so the D12 product counts below still see only the order's products.
    /// </summary>
    void CheckDisplayStock()
    {
        if (Intake == null) return;
        int capacity = 0, displays = 0, renderers = 0, physical = 0, highlighted = 0;
        foreach (var slot in Intake.Slots)
        {
            if (slot == null) continue;
            capacity += slot.Capacity;
            if (slot.HighlightShown || slot.IsTarget) highlighted++;
            foreach (var display in slot.Displays)
            {
                if (display == null) continue;
                displays++;
                foreach (var splat in display.GetComponentsInChildren<GaussianSplatting.Runtime.GaussianSplatRenderer>())
                    if (splat.isActiveAndEnabled && splat.m_Asset != null) renderers++;
                physical += display.GetComponentsInChildren<Collider>().Length + display.GetComponentsInChildren<Rigidbody>().Length
                    + display.GetComponentsInChildren<ProductPrefab>().Length
                    + display.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>().Length;
            }
        }
        Check("day1.shelves_full_of_display_stock", Game.Day == 1 && !Intake.Active && capacity == 16 && displays == capacity && Intake.DisplayCount == capacity
                && renderers == capacity && physical == 0 && highlighted == 0,
            $"day={Game.Day}; slots={Intake.Slots.Length}; places={capacity}; displays={displays}; splats={renderers}; gaussians={Intake.DisplayGaussians}; colliders/bodies/grabs={physical}; highlighted={highlighted}");
    }

    bool FindRig()
    {
        view = Camera.main;
        simulator = FindFirstObjectByType<XRDeviceSimulator>();
        foreach (var interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var parent = interactor.transform.parent;
            if (parent != null && parent.name.Contains("Right")) { rightInteractor = interactor; rightHand = parent; }
        }
        bool found = view && simulator && rightInteractor && rightInteractor.isActiveAndEnabled;
        Check("setup.rig_found", found, $"camera={(bool)view}; simulator={(bool)simulator}; rightNearFar={(bool)rightInteractor}");
        if (!found) Finish();
        return found;
    }

    bool Check(string name, bool pass, string details)
    {
        report.stages.Add(new Stage { name = name, pass = pass, details = details, time = Time.realtimeSinceStartup - started });
        Debug.Log($"[P2 Day] {(pass ? "PASS" : "FAIL")} {name}: {details}");
        return pass;
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
        report.scope = (day2Mode ? "Day 2 (intake, disposal, leaving customers). " : "") + "XR Device Simulator with virtual keyboard/mouse. No HMD, no physical controllers, nothing about reach or comfort. FPS is desktop-only and not a VR measurement. Prices and the scenario are placeholders; customers are Rocketbox characters on a NavMesh in a placeholder store layout.";
        File.WriteAllText(Path.Combine(output, titleMode ? "title_report.json" : day2Mode ? "day2_report.json" : "day_report.json"), JsonUtility.ToJson(report, true));
        WriteDay2Details();
        int failed = report.stages.FindAll(s => !s.pass).Count;
        Debug.Log($"[P2 Day] {(report.pass ? "PASS" : "FAIL")} checks={report.stages.Count} failed={failed} grade={report.grade} sales={report.sales}/{report.possibleSales} mouse={report.externalMouseMotion:0} fps={report.meanDesktopFps:0}");
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
        if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
    }
}
