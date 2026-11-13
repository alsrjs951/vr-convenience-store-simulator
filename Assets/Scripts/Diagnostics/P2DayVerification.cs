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
    // Beside the scanner (x, z), where neither glass sees a held product while the wrist turns it.
    static readonly Vector2 BesideScanner = new Vector2(0.45f, 0.42f);
    // A product shown to the upright glass starts here, on the clerk's side of the counter, and goes in by this much a step.
    const float ShowFromZ = 0.2f, ShowStep = 0.02f;
    // The upright glass reads up to 0.28 m above the counter. A product whose lowest point is this high passes over that.
    const float AboveScannerSight = 0.36f;
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
    static readonly int[] Denominations = { 50000, 10000, 5000, 1000, 500, 100, 50, 10 };
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
    readonly Dictionary<DayState, int> statFrames = new Dictionary<DayState, int>();
    readonly Dictionary<DayState, float> statSeconds = new Dictionary<DayState, float>();

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        day2Mode = Array.IndexOf(args, "-p2-day2-verify") >= 0;
        titleMode = Array.IndexOf(args, "-p2-title-verify") >= 0;
        faceMode = Array.IndexOf(args, "-p2-face-verify") >= 0;
        introMode = Array.IndexOf(args, "-p2-intro-verify") >= 0;
        growthHandsMode = Array.IndexOf(args, "-p2-growth-hands") >= 0;
        if (Array.IndexOf(args, "-p2-verify") < 0 && !day2Mode && !titleMode && !faceMode && !introMode && !growthHandsMode) return;
        if (faceMode) Game.RunTutorial = false;
        walking = Array.IndexOf(args, "-p2-walk") >= 0;
        nextDayCheck = Array.IndexOf(args, "-p2-next-day") >= 0;
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        output = Path.Combine(Application.dataPath, growthHandsMode ? "../docs/p2/evidence/growth/hands" : introMode ? "../docs/p2/evidence/first_minute" : faceMode ? "../docs/p2/evidence/customer_face" : titleMode ? "../docs/p2/evidence/title/editor" : day2Mode ? "../docs/p2/evidence/day2/editor" : "../docs/p2/evidence/day1/editor");
        int index = Array.IndexOf(args, "-p2-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        keyboard = InputSystem.AddDevice<Keyboard>("P2 Verify Keyboard");
        mouse = InputSystem.AddDevice<Mouse>("P2 Verify Mouse");
        started = Time.realtimeSinceStartup;
        Scanner.Scanned += scanned =>
        {
            scansSeen++;
            // Where each read happened: the walk to the upright glass passes the near corner of its reading space, and a
            // read there instead of at the glass has to be told apart from one caused by anything else.
            var face = scanned.transform.TransformDirection(scanned.Data.BarcodeLocalNormal);
            Debug.Log($"[P2 Day] scan {scansSeen}: {scanned.Data.Id} at {scanned.transform.position.ToString("F3")}, barcode face z={BarcodeFaceZ(scanned):0.000}, {Vector3.Angle(face, Vector3.down):0} deg from down, {Vector3.Angle(face, Vector3.forward):0} deg from the upright glass's way; hand {rightHand.position.ToString("F3")}; t={Time.time:0.00}");
        };
        Bag.Bagged += _ => bagsSeen++;
        Desk.CardSlot.Rejected += () => cardsRejected++;
        StartCoroutine(growthHandsMode ? RunGrowthHands() : introMode ? RunIntroCheck() : faceMode ? RunFaceCheck() : titleMode ? RunTitleCheck() : day2Mode ? RunDay2() : Run());
    }

    void Update()
    {
        if (keyboard == null) return;
        frames++;
        if (Game != null)
        {
            statFrames[Game.State] = statFrames.TryGetValue(Game.State, out int seen) ? seen + 1 : 1;
            statSeconds[Game.State] = (statSeconds.TryGetValue(Game.State, out float spent) ? spent : 0f) + Time.unscaledDeltaTime;
        }
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
            // The floor between the street front and row D (x below -1.4) is the customers'; past it is the clerk's side.
            if (position.z < CounterFarEdge + 0.1f && position.x > -1.4f) report.clerkSideFrames++;
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
        yield return CaptureFrom("01b_day1_shelf_a", new Vector3(-1.9f, 1.7f, 1.6f), new Vector3(-3.25f, 1.0f, 3.1f), 60f);
        yield return CaptureFrom("01c_day1_shelf_b", new Vector3(-0.5f, 1.7f, 1.6f), new Vector3(-1.2f, 1.0f, 3.1f), 60f);

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
        Check("day.grade", report.grade == "S" && Mathf.Approximately(report.score, 96f), $"grade={report.grade} score={report.score:0.0} (one cancel and one customer given too much change cost 2 points each)");
        // The change can be wrong (2026-11-13): the fourth customer took too much, the second had to ask for the rest.
        Check("day.till_counted_at_the_settlement",
            Game.Director.TillShort > 0 && Game.Director.OverChanges == 1 && Game.Director.ChangeAsks == 1 && Game.BoardText.Contains($"시재 −{Game.Director.TillShort:N0}원"),
            $"till short by {Game.Director.TillShort}; customers given too much {Game.Director.OverChanges}; times asked for the rest {Game.Director.ChangeAsks}; board={Game.BoardText.Replace("\n", " / ")}");
        Check("day.counter_cleared",
            ProductsAbout() == 0 && Game.Director.GoodsWaitingOnCounter == 0 && FindObjectsByType<PaymentProp>(FindObjectsSortMode.None).Length == 0,
            $"no product or payment prop left in the scene (products other than in a customer's hand on the way out {ProductsAbout()}; paid for and still on the counter {Game.Director.GoodsWaitingOnCounter})");
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
        // Nobody waves any more (2026-11-13): thanks and a nod when the sale is done, and out with their things.
        Check("customers.all_left_without_a_wave", forOrders.TrueForAll(r => r.LeaveAnimationSeconds == 0f && r.GoneAt > r.DoneAt && r.DoneAt > 0f),
            $"waves=[{string.Join(", ", forOrders.ConvertAll(r => r.LeaveAnimationSeconds.ToString("0.0")))}]; out of the door after the sale=[{string.Join(", ", forOrders.ConvertAll(r => (r.GoneAt - r.DoneAt).ToString("0.0")))}] s");
        Check("customers.no_stuck_walks", CustomerCharacter.StuckWarps == 0, $"warps={CustomerCharacter.StuckWarps}; stepped aside when blocked={CustomerCharacter.StepAsides}");
        Check("customers.reached_exact_spots", CustomerCharacter.ShortArrivals == 0, $"walks ending over 0.2 m short of a shelf spot, line place or the counter={CustomerCharacter.ShortArrivals}");
        Check("customers.on_navmesh_every_frame", report.customerFrames > 1000 && report.offNavMeshFrames == 0 && report.settledFrames > 0,
            $"customer frames={report.customerFrames}; standing as obstacles={report.settledFrames}; walking off NavMesh={report.offNavMeshFrames}");
        Check("customers.never_inside_walls_shelves_counter", report.overlapFrames == 0, $"overlapping frames={report.overlapFrames}");
        Check("customers.never_on_clerk_side", report.clerkSideFrames == 0, $"frames nearer than z={CounterFarEdge + 0.1f} with x above -1.4: {report.clerkSideFrames}");
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
    /// that the customer came in early, looked at a shelf and waited in line.
    /// </summary>
    void CheckArrival(int number, Order order)
    {
        var director = Game.Director;
        var record = director.Records.Find(r => r.ForOrder && r.Sequence == number);
        if (!Check($"order{number}.customer_record", record != null, "record for this order")) return;
        int items = 0;
        foreach (var line in order.Lines) items += line.Quantity;
        // Other than what the customer before is still taking off the counter or carrying to the door (2026-11-13).
        int inScene = ProductsAbout();
        Check($"order{number}.products_made_at_counter", inScene == items && director.OrderProducts.Count == items,
            $"products in scene={inScene}; order items={items}; waiting={director.Waiting.Count}");
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

    /// <summary>Between orders no 3DGS product exists anywhere: the people in line have nothing on them yet.</summary>
    // Looked at right after the order was paid. The check itself comes when the next order is waited for, and since
    // 2026-11-12 the clerk first gives the card back (or the customer takes the change), which is long enough for the
    // next customer to have walked up and set their own goods down.
    int? productsRightAfterPaid;
    bool goodsDownRightAfterPaid;

    /// <summary>
    /// Products anywhere, other than paid-for ones still standing in the counter's bag: those stay until their customer
    /// lifts the bag off the counter (2026-11-12). Nor the ones bought without a bag that wait on the counter for
    /// their customer's hand, or are in that hand on the way to the door (2026-11-13).
    /// </summary>
    int ProductsAbout() => FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None).Length - Game.Director.PaidGoodsAbout;

    /// <summary>Two frames after the sale: Destroy takes effect at the end of the frame the order ended in.</summary>
    IEnumerator LookRightAfterPaid()
    {
        yield return null;
        yield return null;
        productsRightAfterPaid = ProductsAbout();
        goodsDownRightAfterPaid = Game.Director.GoodsSetDown;
    }

    void CheckBetweenOrders(int finishedOrder)
    {
        var director = Game.Director;
        int inScene = productsRightAfterPaid ?? ProductsAbout();
        bool down = productsRightAfterPaid.HasValue ? goodsDownRightAfterPaid : director.GoodsSetDown;
        Check($"order{finishedOrder}.no_products_until_next_arrives", inScene == 0 && down,
            $"products={inScene} ({(productsRightAfterPaid.HasValue ? "two frames after the sale" : "now")}); waiting={director.Waiting.Count}; line={director.Line.Count}");
        productsRightAfterPaid = null;
    }

    /// <summary>
    /// The customer puts the goods straight down on the counter (2026-11-10: there is no basket). Once they are down each
    /// stands on the counter top itself, loose and able to be picked up, and nothing shaped like a basket is in the scene.
    /// </summary>
    /// <summary>How many things customers had stood on the counter by hand the last time GoodsSetDown looked.</summary>
    int setDownsSeen;
    /// <summary>Likewise the cards and notes customers had laid on the plate by hand, the last time PaymentLaidDown looked.</summary>
    int paymentsSeen;

    /// <summary>
    /// The customer's card or note lies on the plate, laid there by their own hand after their goods (2026-11-12), and
    /// can be taken. Before that it is not in the scene to be seen.
    /// </summary>
    IEnumerator PaymentLaidDown(string label)
    {
        var director = Game.Director;
        yield return WaitUntil(() => director.GoodsSetDown, 6f, label + ".payment_laid_down");
        yield return new WaitForSeconds(0.2f);
        var payment = Desk.CustomerCard != null ? Desk.CustomerCard : Desk.CustomerBill;
        int byHand = director.PaymentsByHand - paymentsSeen;
        paymentsSeen = director.PaymentsByHand;
        var plate = Desk.CustomerPlate.position;
        var from = payment != null ? payment.transform.position - plate : Vector3.zero;
        float beside = new Vector2(from.x, from.z).magnitude;
        var body = payment != null ? payment.GetComponent<Rigidbody>() : null;
        Check(label + ".laid_on_the_plate_by_the_customers_hand",
            payment != null && byHand >= 1 && payment.gameObject.activeInHierarchy && payment.Grab.enabled && !body.isKinematic && beside < 0.03f && from.y > -0.005f && from.y < 0.03f,
            payment == null ? "nothing to pay with" : $"{(payment.IsCard ? "card" : "note " + payment.Value)}; laid by a customer's hand since the last look {byHand}; slid the last of the way so far {director.PaymentsSlid}; {beside * 100f:0.0} cm from the middle of the plate, {from.y * 100f:0.0} cm above it; shown={payment.gameObject.activeInHierarchy}; can be taken={payment.Grab.enabled && !body.isKinematic}");
    }

    IEnumerator GoodsSetDown(string label, IList<ProductPrefab> goods)
    {
        var director = Game.Director;
        // The customer sets them down by hand, one after another (CustomerArm, 2026-11-11). The first order's are looked
        // at from the side while that happens: the clerk's own view has the arm coming over the far edge of the counter.
        if (label == "order1")
        {
            yield return new WaitForSeconds(0.62f);
            yield return CaptureFrom("16b_customer_sets_down_the_first", new Vector3(1.25f, 1.5f, 2.05f), new Vector3(0.2f, 1.02f, 1.0f), 40f);
            yield return new WaitForSeconds(0.42f);
            yield return CaptureFrom("16e_customer_sets_down_the_next", new Vector3(1.25f, 1.5f, 2.05f), new Vector3(0.2f, 1.02f, 1.0f), 40f);
            yield return Capture("16d_setting_down_from_the_clerk");
        }
        var settingArm = director.AtCounter != null ? director.AtCounter.Arm : Game.Tutorial != null && Game.Tutorial.Guest != null ? Game.Tutorial.Guest.Arm : null;
        // After the goods the same hand lays the card or the note on the plate. The first two orders' are looked at on
        // the way there, from the customer's left and from the clerk's place.
        if (label == "order1" || label == "order2")
        {
            float asked = Time.time;
            while (!director.PaymentInHand && !director.GoodsSetDown && Time.time - asked < 6f) yield return null;
            yield return new WaitForSeconds(0.3f);
            string kind = Desk.CustomerCard != null ? "card" : "note";
            Check(label + ".fingers_closed_on_the_" + kind, settingArm != null && settingArm.FingerJoints >= 12 && settingArm.Closed > 0.1f,
                settingArm == null ? "no arm" : $"finger and thumb joints {settingArm.FingerJoints}; fingers closed {settingArm.Closed:0.00}; in the hand={director.PaymentInHand}");
            yield return CaptureFrom($"16i_{label}_{kind}_on_its_way_to_the_plate", new Vector3(0.95f, 1.3f, 1.45f), new Vector3(0.3f, 0.95f, 0.95f), 35f);
            yield return Capture($"16j_{label}_{kind}_from_the_clerk");
        }
        yield return WaitUntil(() => director.GoodsSetDown, 6f, label + ".goods_set_down");
        // Since the last time this looked: a customer the check did not wait on (Day 2, while the clerk was at the shelves) has set goods down too.
        int byHand = director.SetDownsByHand - setDownsSeen;
        setDownsSeen = director.SetDownsByHand;
        Check(label + ".set_down_by_the_customers_hand", settingArm != null && byHand >= goods.Count && settingArm.WorstMiss < 0.05f,
            settingArm == null ? "the customer has no arm to do it with" : $"things {goods.Count}; stood by a customer's hand since the last look {byHand}; pushed the last of the way so far {director.PushedIntoPlace}; this customer's wrist off its point at most {settingArm.WorstMiss * 100f:0.0} cm");
        yield return new WaitForSeconds(0.2f);
        bool standing = goods.Count > 0;
        var detail = new System.Text.StringBuilder();
        foreach (var product in goods)
        {
            if (product == null)
            {
                standing = false;
                continue;
            }
            float lowest = LowestProxyPoint(product);
            standing &= !product.Body.isKinematic && product.Grab.enabled && lowest > CounterTop - 0.003f && lowest < CounterTop + 0.01f;
            detail.Append($"{product.Data.Id} lowest={lowest:0.000} yaw={Mathf.DeltaAngle(180f, product.transform.eulerAngles.y):0.0}; ");
        }
        bool basket = false;
        foreach (var thing in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            basket |= thing.name.StartsWith("Basket (", StringComparison.Ordinal);
        Check(label + ".goods_stand_on_the_bare_counter", standing && !basket, $"{detail}basket in scene={basket}");
        // In the tutorial the card comes later, at its own step.
        if (label != "tutorial") yield return PaymentLaidDown(label);
        // Stepping up to the counter and along it, the customer's feet stay where they stand and take turns to lift
        // (CustomerFeet, 2026-11-12). Before, they slid under the body the whole way.
        var stepper = director.AtCounter != null ? director.AtCounter : Game.Tutorial != null ? Game.Tutorial.Guest : null;
        var feet = stepper != null ? stepper.Feet : null;
        Check(label + ".steps_instead_of_sliding", feet != null && feet.Carried > 0.1f && feet.Steps >= 2 && feet.Slid < 0.03f,
            feet == null ? "no feet to step with" : $"the body moved {feet.Carried * 100f:0.0} cm standing; steps {feet.Steps}; a foot on the floor slid {feet.Slid * 100f:0.0} cm in all; a held foot at most {feet.WorstMiss * 100f:0.0} cm off its place; the hips came down {feet.DeepestDrop * 100f:0.0} cm at most; {stepper.name}");
        // None of the fixed orders has three things, and the third place in the row is the farthest from where the customer
        // stands. So the first order's customer sets one more thing down there: they move over toward it, and the hand
        // stands it on its place or within a hand's breadth of it (then it is pushed the rest of the way).
        if (label == "order1" && goods.Count == 2)
        {
            int pushedBefore = director.PushedIntoPlace, byHandBefore = director.SetDownsByHand;
            var third = director.PutOnCounter(Game.Catalog.Products[0], 2, 3);
            // The customer has laid the card down by now and steps back along the row first. The thing is not drawn until
            // it is in the hand; a quarter of a second after that it is on its way over the counter's edge.
            float putAt = Time.time;
            var alongTheRow = director.AtCounter != null ? director.AtCounter.Feet : null;
            while (alongTheRow != null && !alongTheRow.Swinging && Time.time - putAt < 2f) yield return null;
            yield return new WaitForSeconds(0.07f);
            yield return CaptureFrom("16k_steps_along_the_row", new Vector3(-0.55f, 0.45f, 2.25f), new Vector3(0.4f, 0.2f, 1.25f), 40f);
            while (third != null && third.Splat != null && third.Splat.m_OpacityScale <= 0f && Time.time - putAt < 4f) yield return null;
            float inHandAfter = Time.time - putAt;
            yield return new WaitForSeconds(0.25f);
            yield return Capture("16f_third_thing_from_the_clerk");
            yield return CaptureFrom("16g_third_thing_from_behind", new Vector3(1.45f, 1.5f, 2.05f), new Vector3(0.55f, 1.02f, 1.0f), 40f);
            yield return WaitUntil(() => director.GoodsSetDown, 6f, "order1.third_thing_set_down");
            yield return new WaitForSeconds(0.3f);
            float lowest = third != null ? LowestProxyPoint(third) : 0f;
            float wantedX = director.GoodsAnchor.position.x + 0.5f;
            Check("order1.third_thing_stands_in_its_place", third != null && director.SetDownsByHand == byHandBefore + 1 && Mathf.Abs(third.transform.position.x - wantedX) < 0.03f
                                                          && lowest > CounterTop - 0.003f && lowest < CounterTop + 0.01f && third.Grab.enabled && !third.Body.isKinematic
                                                          && director.LongestPushSoFar < 0.08f,
                third == null ? "gone" : $"{third.Data.Id} at x={third.transform.position.x:0.000} (place {wantedX:0.000}), lowest={lowest:0.000}; stood by the hand {director.SetDownsByHand - byHandBefore}; pushed the last of the way {director.PushedIntoPlace - pushedBefore}, the longest push so far {director.LongestPushSoFar * 100f:0.0} cm; the customer {Vector3.Distance(director.AtCounter.transform.position, director.AtCounter.Spot) * 100f:0} cm from their spot; in the hand {inHandAfter:0.00} s after it was asked for");
            yield return Capture("16h_third_thing_in_its_place");
            if (third != null) Destroy(third.gameObject);
            yield return null;
            setDownsSeen = director.SetDownsByHand;
        }
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
        var pointer = tutorial.Pointer;
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Grab, StepTimeout, "tutorial.grab_shown");
        if (finished) yield break;
        var product = tutorial.Product;
        yield return new WaitForSeconds(0.2f);
        var firstCustomer = tutorial.Guest;
        Check("tutorial.first_customer_at_counter", firstCustomer != null && firstCustomer.State == CustomerState.AtCounter && firstCustomer.FaceRig != null,
            firstCustomer == null ? "nobody" : $"state={firstCustomer.State}");
        yield return GoodsSetDown("tutorial", new List<ProductPrefab> { product });
        Check("tutorial.greeted", Game.Pos.Audio.SpokenCount(CustomerLine.Greet) >= 1, $"greetings={Game.Pos.Audio.SpokenCount(CustomerLine.Greet)}");
        Check("tutorial.arrow_on_the_product_at_once", pointer.Shown && pointer.Label == "집기", $"shown={pointer.Shown}; label={pointer.Label}");
        Check("tutorial.grip_said_in_plain_words", tutorial.Hands.RightText.Contains("가운뎃손가락") && Game.BoardText.Contains("가운뎃손가락") && !Game.BoardText.Contains("그립"), $"hand={tutorial.Hands.RightText}; board={Game.BoardText}");
        yield return Capture("02_tutorial_grab");
        // The player's hands (2026-11-11): drawn in place of the controllers, open before the grip and closed on it.
        var playerHand = rightHand.GetComponentInChildren<PlayerHand>();
        var controllerModel = rightHand.Find("Right Controller Visual");
        Check("hands.drawn_instead_of_the_controllers", playerHand != null && playerHand.GetComponentInChildren<SkinnedMeshRenderer>().enabled
                                                        && controllerModel != null && !controllerModel.gameObject.activeInHierarchy
                                                        && FindObjectsByType<PlayerHand>(FindObjectsSortMode.None).Length == 2,
            $"hand={(playerHand != null)}; controller model shown={(controllerModel != null && controllerModel.gameObject.activeInHierarchy)}; hands={FindObjectsByType<PlayerHand>(FindObjectsSortMode.None).Length}");
        Check("hands.open_before_the_grip", playerHand != null && playerHand.Grip < 0.1f && !playerHand.Holding && playerHand.OnController,
            playerHand == null ? "no hand" : $"grip={playerHand.Grip:0.00}; holding={playerHand.Holding}; on the controller={playerHand.OnController}");
        yield return GrabProduct(product, "tutorial");
        yield return new WaitForSeconds(0.3f);
        // It holds the product by the product's own shape: the drawn hand has gone to an edge of its box, and the ends of
        // the thumb and at least two fingers rest on the box (within a fingertip's thickness of it, none sunk into it).
        int resting = 0;
        float deepest = 0f;
        var tipDistances = new System.Text.StringBuilder();
        if (playerHand != null)
            for (int finger = 0; finger < PlayerHand.Fingers; finger++)
            {
                float from = playerHand.TipFromHeld(finger);
                tipDistances.Append($"{from * 1000f:0} ");
                if (from <= 0.012f) resting++;
                deepest = Mathf.Min(deepest, from);
            }
        Check("hands.holds_a_thing_by_its_shape", playerHand != null && playerHand.Holding && playerHand.Grip > 0.9f && playerHand.Fitted
                                                  && playerHand.TipFromHeld(PlayerHand.Thumb) <= 0.012f && resting >= 3 && deepest > -0.004f,
            playerHand == null ? "no hand" : $"grip={playerHand.Grip:0.00}; holding={playerHand.Holding}; fitted={playerHand.Fitted}; fingertips from the product's box, thumb first, mm: {tipDistances}; hand {Vector3.Distance(playerHand.transform.position, rightHand.position) * 100f:0.0} cm from the controller");
        var besideHand = playerHand != null ? playerHand.transform.position : rightHand.position;
        yield return CaptureFrom("02h_hand_holding", besideHand + new Vector3(0.22f, 0.2f, -0.3f), besideHand + new Vector3(0f, 0f, 0.04f), 40f);
        yield return CaptureFrom("02i_hand_holding_from_the_customer", besideHand + new Vector3(-0.15f, 0.12f, 0.36f), besideHand + new Vector3(0f, 0f, 0.02f), 40f);
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Rotate, StepTimeout, "tutorial.rotate_shown");
        yield return new WaitForSeconds(0.2f);
        Check("tutorial.arrow_at_the_barcode", pointer.Shown && pointer.Label == "바코드" && tutorial.Hands.RightText == "", $"label={pointer.Label}; hand={tutorial.Hands.RightText}");
        yield return Capture("02b_tutorial_barcode_arrow");
        int rustlesBefore = Game.Pos.Audio.CountOf(StoreSound.Rustle);
        yield return TurnBarcodeDown(product);
        Check("hands.bag_rustles_as_it_is_turned", Game.Pos.Audio.CountOf(StoreSound.Rustle) > rustlesBefore, $"rustles {rustlesBefore}->{Game.Pos.Audio.CountOf(StoreSound.Rustle)}");
        yield return MoveHeldAbovePad(product);
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Scan, StepTimeout, "tutorial.scan_shown");
        yield return new WaitForSeconds(0.1f);
        Check("tutorial.arrow_at_the_scanner", pointer.Shown && pointer.Label == "여기에 대기", $"label={pointer.Label}");
        yield return Capture("03_tutorial_barcode_down");
        yield return LowerUntilScanned(product, "tutorial.scan");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Bag, StepTimeout, "tutorial.bag_shown");
        yield return new WaitForSeconds(0.1f);
        Check("tutorial.customer_asks_for_a_bag", Game.Pos.Audio.SpokenCount(CustomerLine.Bag) >= 1 && pointer.Label == "여기에 넣기", $"bag lines={Game.Pos.Audio.SpokenCount(CustomerLine.Bag)}; label={pointer.Label}");
        yield return Capture("03d_tutorial_bag_arrow");
        yield return PutInBag(product, "tutorial.bag");
        yield return ResetHandRotation();
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Card, StepTimeout, "tutorial.card_shown");
        Check("hands.back_on_the_controller_after_letting_go", playerHand != null && playerHand.OnController && !playerHand.Holding,
            playerHand == null ? "no hand" : $"on the controller={playerHand.OnController}; holding={playerHand.Holding}");
        yield return new WaitForSeconds(0.1f);
        Check("tutorial.arrow_on_the_card", pointer.Shown && pointer.Label == "집기", $"label={pointer.Label}");
        yield return PaymentLaidDown("tutorial.card");
        int thanksBefore = Game.Pos.Audio.SpokenCount(CustomerLine.Thanks);
        // Paid is when the card goes away. The step itself stays until the next customer has walked up.
        yield return PayByCard(() => Desk.CustomerCard == null, "tutorial.card");
        // The guest takes the card, lifts the bag off the counter into the left hand, and only then waves.
        yield return TakesTheBag("tutorial.first_customer", firstCustomer);
        yield return WaitUntil(() => firstCustomer == null || firstCustomer.Playing == CustomerCharacter.WalkState, 4f, "tutorial.first_customer_walks_out");
        Check("tutorial.first_customer_thanks_and_leaves", Game.Pos.Audio.SpokenCount(CustomerLine.Thanks) == thanksBefore + 1 && Game.Director.LastOutcome.StartsWith("+", StringComparison.Ordinal)
                && firstCustomer.FaceRig.Nods >= 1 && firstCustomer.FaceRig.Mood == CustomerMood.Pleased && firstCustomer.State == CustomerState.Leaving && firstCustomer.Playing == CustomerCharacter.WalkState,
            $"thanks={Game.Pos.Audio.SpokenCount(CustomerLine.Thanks)}; card={Game.Director.LastOutcome}; nods={firstCustomer.FaceRig.Nods}; mood={firstCustomer.FaceRig.Mood}; state={firstCustomer.State}; playing={firstCustomer.Playing}");
        Check("tutorial.first_customer_takes_the_bag", HoldsBag(firstCustomer), BagDetail(firstCustomer));
        yield return Capture("03c_first_customer_thanks");
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Cash, StepTimeout, "tutorial.cash_shown");
        yield return new WaitForSeconds(0.2f);
        Check("tutorial.second_customer_for_the_cash", tutorial.Guest != null && tutorial.Guest != firstCustomer && tutorial.Guest.State == CustomerState.AtCounter && pointer.Shown && pointer.Label == "집기",
            tutorial.Guest == null ? "nobody" : $"state={tutorial.Guest.State}; label={pointer.Label}");
        yield return PaymentLaidDown("tutorial.cash");
        yield return Capture("04_tutorial_cash");
        yield return GiveBill("tutorial.cash_bill");
        Check("tutorial.pos_shows_the_cash_sale", Screen.TotalText == "1,500원" && Screen.ReceivedText == "5,000" && Screen.ChangeText == "3,500",
            $"total={Screen.TotalText}; received={Screen.ReceivedText}; change={Screen.ChangeText}");
        Check("tutorial.arrow_over_the_next_piece", pointer.Shown && pointer.Label == "1,000원", $"label={pointer.Label}");
        yield return Capture("04b_tutorial_cash_drawer_open");
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
            var buyer = director.AtCounter;
            CheckArrival(number, order);
            yield return GoodsSetDown($"order{number}", director.OrderProducts);
            if (number == 2)
            {
                // What the clerk sees at the counter, and the whole customer side with the line and the shelves.
                yield return CaptureFrom("16_customer_at_counter", new Vector3(0f, 1.55f, 0.15f), new Vector3(0f, 1.25f, 1.6f), 70f);
                yield return CaptureFrom("17_customer_side_overview", new Vector3(0.2f, 2.4f, -1.4f), new Vector3(-0.4f, 0.7f, 3.4f), 80f);
                // The line from its right-hand side, and the goods on the counter from the customers' side.
                yield return CaptureFrom("18_line_at_the_counter", new Vector3(-1.3f, 1.35f, 2.2f), new Vector3(0.9f, 0.85f, 1.6f), 60f);
                yield return CaptureFrom("19_goods_on_the_counter", new Vector3(0.45f, 1.5f, 0.12f), new Vector3(0.5f, 0.95f, 0.7f), 60f);
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
                // With a customer the change can be wrong (2026-11-13): the fourth is given too much and takes it, the
                // second is left waiting with too little and asks for the rest.
                if (number == 4) yield return GiveTooMuchChange(bill - total);
                else yield return GiveChange(bill - total, 0, number == 2, () => pos.Current == null, $"order{number}.change");
            }
            yield return WaitUntil(() => pos.Current == null, 10f, $"order{number}.completed");
            yield return LeavesWithOrWithoutBag($"order{number}", order, buyer);
            // After the last order: lifting the bag off the counter takes a few seconds (2026-11-12), and following the
            // first customer out after that had the test still looking at them when the second one arrived.
            if (number == 5) yield return SeeBagCarriedOut(buyer);
        }
    }

    // ---------- The bag going out with the customer ----------

    /// <summary>In the left hand (the right one waves), hanging down from it.</summary>
    protected static bool HoldsBag(CustomerCharacter customer)
    {
        if (customer == null || customer.Bag == null || !customer.Bag.gameObject.activeInHierarchy || customer.LeftHand == null) return false;
        var bag = customer.Bag.transform;
        return Vector3.Distance(bag.position, customer.LeftHand.position) < 0.2f && Vector3.Dot(bag.up, Vector3.up) > 0.9f
            && Vector3.Dot(bag.position - customer.transform.position, customer.transform.right) < 0f;
    }

    protected static string BagDetail(CustomerCharacter customer)
    {
        if (customer == null) return "customer gone";
        if (customer.Bag == null) return "no bag";
        var bag = customer.Bag.transform;
        return $"bag at {bag.position.ToString("F2")}; left hand {(customer.LeftHand != null ? customer.LeftHand.position.ToString("F2") : "none")}; upright={Vector3.Dot(bag.up, Vector3.up):0.00}; state={customer.State}";
    }

    /// <summary>Goods that were bagged go out in the customer's hand; a customer who asked for no bag has none.</summary>
    protected IEnumerator LeavesWithOrWithoutBag(string label, Order order, CustomerCharacter buyer)
    {
        yield return null;
        if (order.NeedsBag) yield return TakesTheBag(label, buyer);
        else
        {
            yield return TakesTheGoods(label, order, buyer);
            Check(label + ".leaves_without_a_bag", buyer != null && buyer.Bag == null, BagDetail(buyer));
        }
    }

    // ---------- Goods bought without a bag going out in the customer's hand ----------

    /// <summary>The customer's hand on a thing they bought has been pictured once this run.</summary>
    bool goodsTakePictured;
    int goodsTakenSeen, goodsNotTakenSeen, goodsCarriedSeen, goodsDrawnSeen;

    /// <summary>In the left hand, on the customer's left side.</summary>
    protected static bool HoldsGoods(CustomerCharacter customer)
    {
        if (customer == null || customer.Goods == null || customer.Goods.Product == null || customer.LeftHand == null) return false;
        var middle = customer.Goods.Middle;
        return Vector3.Distance(middle, customer.LeftHand.position) < 0.4f && Vector3.Dot(middle - customer.transform.position, customer.transform.right) < 0f;
    }

    protected static string GoodsDetail(CustomerCharacter customer)
    {
        if (customer == null) return "customer gone";
        if (customer.Goods == null || customer.Goods.Product == null) return "nothing in the hand";
        var goods = customer.Goods;
        return $"{goods.Product.Data.Id} at {goods.Middle.ToString("F2")}, {Vector3.Distance(goods.Middle, customer.LeftHand.position) * 100f:0} cm from the left wrist; hanging from the fingers={goods.Hanging}; Gaussians {(goods.Product.Splat != null && goods.Product.Splat.m_Asset != null ? goods.Product.Splat.m_Asset.splatCount : 0)}; fingers closed {(customer.Arm != null ? customer.Arm.Closed : 0f):0.00}; state={customer.State}";
    }

    /// <summary>
    /// What was bought without a bag stays on the counter when the sale is done, and the customer takes it with the
    /// left hand, one thing after another; the last stays in the hand and goes out with them (2026-11-13). Before,
    /// all of it was gone the moment the sale was done. What the hand cannot get at (behind the scanner, too far over
    /// the counter, in the clerk's hand) still goes that way, and is counted.
    /// </summary>
    protected IEnumerator TakesTheGoods(string label, Order order, CustomerCharacter buyer)
    {
        var director = Game.Director;
        int items = 0;
        foreach (var line in order.Lines) items += line.Quantity;
        float asked = Time.time;
        if (!goodsTakePictured)
        {
            while (!director.GoodsInHand && (director.GoodsWaitingOnCounter > 0 || director.TakingGoods(buyer)) && Time.time - asked < 12f) yield return null;
            if (director.GoodsInHand && buyer != null)
            {
                goodsTakePictured = true;
                // The hand has just closed on the first thing; then on its way over the counter's edge.
                var hand = buyer.LeftHand != null ? buyer.LeftHand.position : buyer.transform.position + Vector3.up;
                yield return CaptureFrom("goods_take_1_hand_on_the_thing_from_the_clerk", new Vector3(0.15f, 1.5f, 0.05f), hand + Vector3.down * 0.05f, 60f);
                // From the POS side across the counter, and from above: from the customer's side the order screen and
                // the people in line are in the way.
                yield return CaptureFrom("goods_take_2_across_the_counter", new Vector3(-0.35f, 1.45f, 0.45f), hand + Vector3.down * 0.08f, 55f);
                yield return new WaitForSeconds(0.1f);
                yield return CaptureFrom("goods_take_3_from_above", new Vector3(0.45f, 2.4f, 0.5f), new Vector3(0.45f, 0.9f, 0.85f), 50f);
            }
        }
        yield return WaitUntil(() => director.GoodsWaitingOnCounter == 0 && !director.TakingGoods(buyer), 16f, label + ".goods_taken_off_the_counter");
        float took = Time.time - asked;
        yield return null;
        int taken = director.GoodsTakenByHand - goodsTakenSeen, notTaken = director.GoodsNotTaken - goodsNotTakenSeen;
        int carried = director.GoodsCarriedOut - goodsCarriedSeen, drawn = director.GoodsDrawnToHand - goodsDrawnSeen;
        goodsTakenSeen = director.GoodsTakenByHand;
        goodsNotTakenSeen = director.GoodsNotTaken;
        goodsCarriedSeen = director.GoodsCarriedOut;
        goodsDrawnSeen = director.GoodsDrawnToHand;
        var arm = buyer != null ? buyer.Arm : null;
        Check(label + ".takes_the_goods_by_hand",
            arm != null && taken >= 1 && taken + notTaken == items && !director.GoodsInHand && director.GoodsWaitingOnCounter == 0 && director.LongestDrawSoFar < 0.251f && arm.WorstMiss < 0.05f,
            $"things bought {items}; taken off the counter by the customer's hand {taken}, of which {drawn} came to the hand over the counter first (the farthest so far {director.LongestDrawSoFar * 100f:0.0} cm); out of the hand's reach and gone with the sale {notTaken}; still on the counter {director.GoodsWaitingOnCounter}; this customer's wrist off its point at most {(arm != null ? arm.WorstMiss * 100f : 0f):0.0} cm; done {took:0.0} s after the check began to look; {(buyer != null ? buyer.name : "customer gone")}");
        Check(label + ".leaves_with_the_last_thing_in_hand", carried == 1 && HoldsGoods(buyer) && buyer.Bag == null, GoodsDetail(buyer));
        if (goodsTakePictured && !goodsHeldPictured && HoldsGoods(buyer))
        {
            goodsHeldPictured = true;
            var hand = buyer.LeftHand.position;
            yield return CaptureFrom("goods_take_4_in_the_left_hand", new Vector3(1.25f, 1.3f, 2.05f), hand + Vector3.down * 0.1f, 35f);
            yield return CaptureFrom("goods_take_5_in_the_hand_from_the_front", hand + new Vector3(0.75f, 0.35f, -0.9f), hand + Vector3.down * 0.1f, 45f);
        }
    }

    bool goodsHeldPictured;

    /// <summary>The customer's hand on the bag's handles has been pictured once this run.</summary>
    bool bagLiftPictured;
    int bagsLiftedSeen;

    /// <summary>
    /// The bagged goods stay in the counter's bag until the customer lifts it: the right hand on the handles, over the
    /// counter and off its edge, and into the left hand (2026-11-12). Before, the bag was in the left hand the moment
    /// the sale was done. Called once the customer has their change or card, when the lift is just beginning.
    /// </summary>
    protected IEnumerator TakesTheBag(string label, CustomerCharacter buyer)
    {
        var director = Game.Director;
        bool waitedOnTheCounter = buyer != null && (buyer.Bag == null || director.BagInHand) || director.BagsLiftedByHand > bagsLiftedSeen;
        if (!bagLiftPictured)
        {
            float asked = Time.time;
            while (!director.BagInHand && director.BagsLiftedByHand == bagsLiftedSeen && Time.time - asked < 12f) yield return null;
            if (director.BagInHand)
            {
                bagLiftPictured = true;
                // The hand has just closed on the handles and the bag still stands on the counter; then on its way over
                // the counter; then in the left hand.
                yield return CaptureFrom("bag_lift_1_hand_on_the_handles_from_the_clerk", new Vector3(0f, 1.5f, 0.1f), new Vector3(-0.25f, 1.05f, 0.8f), 65f);
                yield return CaptureFrom("bag_lift_2_from_the_side", new Vector3(0.8f, 1.75f, 0.25f), new Vector3(-0.25f, 1.05f, 0.85f), 55f);
                yield return new WaitForSeconds(0.22f);
                yield return CaptureFrom("bag_lift_3_over_the_counter", new Vector3(0.8f, 1.75f, 0.25f), new Vector3(-0.1f, 1.05f, 0.95f), 55f);
                float began = Time.time;
                while (director.BagInHand && Time.time - began < 4f) yield return null;
                yield return CaptureFrom("bag_lift_4_into_the_left_hand", new Vector3(1.3f, 2.1f, 0.75f), new Vector3(0.42f, 0.75f, 1.3f), 50f);
            }
        }
        yield return WaitUntil(() => director.BagsLiftedByHand > bagsLiftedSeen, 14f, label + ".bag_lifted_off_the_counter");
        yield return null;
        var bag = buyer != null ? buyer.Bag : null;
        Check(label + ".lifts_the_bag_by_hand",
            waitedOnTheCounter && director.BagsLiftedByHand == bagsLiftedSeen + 1 && !director.BagInHand && director.FarthestFromBag < 0.06f && director.GoodsWaitingInBag == 0
                && director.BagOnCounterShown && bag != null && !bag.Standing && bag.Stretch > 0.99f && director.ShortestBagSoFar > 0.56f,
            $"on the counter until lifted={waitedOnTheCounter}; lifted by a customer's hand {director.BagsLiftedByHand - bagsLiftedSeen} time(s), {director.GoodsLiftedInBags} thing(s) in bags so far; the hand at most {director.FarthestFromBag * 100f:0.0} cm short of the handles so far; goods left in the counter's bag {director.GoodsWaitingInBag}; a bag stands on the counter again={director.BagOnCounterShown}; "
            + (bag == null ? "no bag on the customer" : $"hanging free={!bag.Standing}, drawn {bag.Stretch:0.00} of its length; the shortest over the counter so far {director.ShortestBagSoFar:0.00}"));
        bagsLiftedSeen = director.BagsLiftedByHand;
        Check(label + ".leaves_with_the_bag", HoldsBag(buyer), BagDetail(buyer));
    }

    /// <summary>
    /// Once, after the wave, when the customer has walked clear of the counter (alongside it the counter hides the hand):
    /// from where the clerk stands, and from ahead on the side the bag is on.
    /// </summary>
    IEnumerator SeeBagCarriedOut(CustomerCharacter buyer)
    {
        if (buyer == null) yield break;
        var from = buyer.transform.position;
        float begun = Time.realtimeSinceStartup;
        while (buyer != null && Time.realtimeSinceStartup - begun < 10f
               && !(buyer.Playing == CustomerCharacter.WalkState && Vector3.Distance(buyer.transform.position, from) > 2.4f))
            yield return null;
        if (buyer == null) yield break;
        Check("order5.still_holds_the_bag_walking", HoldsBag(buyer) && buyer.Playing == CustomerCharacter.WalkState, BagDetail(buyer) + $"; playing={buyer.Playing}");
        var body = buyer.transform;
        yield return CaptureFrom("bag_4_carried_out_from_the_clerk", new Vector3(0f, 1.6f, -0.2f), body.position + Vector3.up * 0.8f, 40f);
        if (buyer == null) yield break;
        yield return CaptureFrom("bag_5_carried_out", body.position + body.forward * 1.8f - body.right * 1.1f + Vector3.up * 1.0f, body.position + Vector3.up * 0.7f, 50f);
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
        // The steps to the side carry it past the scanner. It goes over the upright glass's sight (WalkHolding lifts it the
        // last 0.25 m), or a barcode that faces the glass would be read on the way, as it should be.
        var held = product.transform.position;
        float clear = CounterTop + AboveScannerSight + held.y - LowestProxyPoint(product) - 0.25f;
        if (held.y < clear) yield return MoveProduct(product, new Vector3(held.x, clear, held.z), "walk.holding_product.clear");
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
        Check("rule.till_stays_shut_before_scan", Desk.Register.Shut && Desk.StockItems.Count == 0 && Screen.ReceivedText == "",
            $"opened={Desk.Register.Opened:0.00}; pieces={Desk.StockItems.Count}; strip={Screen.ReceivedText}");
        yield return Capture("12x_bill_returned_before_scan");
    }

    /// <summary>
    /// The body and the barcode's lower edge both touch the flat glass, but the barcode faces sideways, toward neither glass.
    /// A barcode that happens to face the upright glass would be read by it, rightly, so that one is first turned round to
    /// the clerk's side, and turned back afterwards beside the scanner, out of its sight.
    /// </summary>
    IEnumerator PressUprightOnPad(ProductPrefab product)
    {
        var start = product.transform.position;
        var facing = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        bool turnedRound = Vector3.Dot(facing, -Scanner.Shown.up) > 0f;
        if (turnedRound)
        {
            yield return MoveProduct(product, new Vector3(start.x, Mathf.Max(start.y, TravelHeight), start.z), "rule.upright.rise");
            yield return RotateHand(new Vector3(0f, 180f, 0f));
            yield return new WaitForSeconds(0.3f);
        }
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
            $"scans {scans}->{scansSeen}; bodyEntries {bodies}->{Scanner.IgnoredBodyEntries}; angledBarcodeEntries {angled}->{Scanner.AngledBarcodeEntries}; barcode angle from down={Vector3.Angle(normal, Vector3.down):0}; from the upright glass={Vector3.Angle(normal, -Scanner.Shown.up):0}; turned round={turnedRound}; lowest={LowestProxyPoint(product):0.000}; padTop={padTop:0.000}");
        if (!turnedRound) yield break;
        var center = product.transform.position;
        yield return MoveProduct(product, new Vector3(center.x, Mathf.Max(center.y, TravelHeight), center.z), "rule.upright.lift");
        yield return MoveProduct(product, new Vector3(start.x, Mathf.Max(center.y, TravelHeight), start.z), "rule.upright.beside");
        yield return RotateHand(new Vector3(0f, -180f, 0f));
        yield return new WaitForSeconds(0.3f);
    }

    /// <summary>
    /// Holds a product, whose barcode faces down, up to the upright glass instead: turned beside the scanner, brought in
    /// from the clerk's side at the height of the glass, and moved toward it until it is read. Nothing is touched.
    /// </summary>
    IEnumerator ShowToUprightGlass(ProductPrefab product, string label)
    {
        var glass = Scanner.Shown;
        float padTop = Scanner.transform.position.y - 0.004f;
        yield return RotateHand(new Vector3(-90f, 0f, 0f));
        yield return new WaitForSeconds(0.5f);
        var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        Check(label + ".barcode_faces_the_glass", Vector3.Angle(normal, -glass.up) < 20f, $"{product.Data.Id} angle from the glass={Vector3.Angle(normal, -glass.up):0.0}");

        var center = product.transform.position;
        float height = Mathf.Max(glass.position.y, padTop + 0.04f + center.y - LowestProxyPoint(product));
        yield return MoveProduct(product, new Vector3(glass.position.x, center.y, ShowFromZ), label + ".before");
        yield return MoveProduct(product, new Vector3(glass.position.x, height, ShowFromZ), label + ".level");
        int before = scansSeen;
        var target = rightHand.position;
        float begun = Time.realtimeSinceStartup;
        while (scansSeen == before && Time.realtimeSinceStartup - begun < LowerTimeout * 2f && glass.position.z - BarcodeFaceZ(product) > 0.04f)
        {
            target += Vector3.forward * ShowStep;
            yield return MoveHand(target, label + ".approach");
            yield return new WaitForSeconds(0.05f);
        }
        float gap = glass.position.z - BarcodeFaceZ(product);
        Check(label, scansSeen == before + 1, $"{product.Data.Id} scans {before}->{scansSeen}; gap to the glass={gap:0.000}");
        Check("scan.upright_glass_reads_in_the_air", scansSeen > before && gap > 0.08f && LowestProxyPoint(product) > padTop + 0.02f,
            $"gap to the glass={gap:0.000}; lowest={LowestProxyPoint(product):0.000}; padTop={padTop:0.000}");
        yield return Capture("13w_read_at_the_upright_glass");
        // Still held there, in the scanner's sight: one read, not one every cooldown.
        yield return new WaitForSeconds(ScanCooldown + 0.4f);
        Check("scan.read_once_while_in_sight", scansSeen == before + 1, $"scans {before}->{scansSeen}");
        // From the customer's side, past the scanner: the red light on the face the clerk cannot see.
        yield return CaptureFrom("13w_red_light_on_the_product", new Vector3(-0.36f, 1.2f, 0.78f), new Vector3(0f, 1.04f, 0.47f), 45f);
        yield return RotateHand(new Vector3(90f, 0f, 0f));
        yield return new WaitForSeconds(0.3f);
    }

    /// <summary>How far along z the middle of the barcode face is.</summary>
    static float BarcodeFaceZ(ProductPrefab product)
    {
        var local = product.Data.BarcodeLocalNormal.normalized;
        var size = product.Proxy.size;
        float halfDepth = 0.5f * (Mathf.Abs(local.x) * size.x + Mathf.Abs(local.y) * size.y + Mathf.Abs(local.z) * size.z);
        return (product.transform.TransformPoint(product.Proxy.center) + product.transform.TransformDirection(local) * halfDepth).z;
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

    /// <summary>
    /// Too much change to a customer (2026-11-13; until then an error on the POS, to be taken back). Held over the tray it
    /// is not given yet. Let go, it lies there for a moment, in which it could still be snatched back, and then the
    /// customer takes it: the sale is done, the till is short by the difference and the outcome card says so.
    /// </summary>
    IEnumerator GiveTooMuchChange(int due)
    {
        var director = Game.Director;
        int shortBefore = director.TillShort, oversBefore = director.OverChanges, takenBefore = director.ChangeTakenByHand;
        yield return new WaitForSeconds(0.2f);
        var bill = Desk.StockItems[5000];
        int over = bill.Value - due;
        yield return GrabProp(bill, "rule.too_much.grab");
        yield return CarryProp(bill, Desk.Tray.transform.position, "rule.too_much");
        yield return new WaitForSeconds(ChangeTrayZone.TakenAnywaySeconds + 0.5f);
        Check("rule.too_much_in_the_hand_is_not_given", Game.Pos.Current != null && Desk.Tray.Amount == bill.Value && bill.Grab.isSelected,
            $"on the tray {Desk.Tray.Amount} of {due} due; still in the hand={bill.Grab.isSelected}; sale going on={Game.Pos.Current != null}");
        yield return SetDown(bill.GetComponent<Rigidbody>());
        float letGo = Time.time;
        while (Game.Pos.Current != null && Time.time - letGo < 5f) yield return null;
        float lay = Time.time - letGo;
        Check("rule.too_much_lies_a_moment_then_is_taken",
            Game.Pos.Current == null && lay > ChangeTrayZone.TakenAnywaySeconds - 0.2f && lay < ChangeTrayZone.TakenAnywaySeconds + 1f && Desk.Tray.ErrorCount == 0,
            $"lay {lay:0.00} s before the customer took it (rule {ChangeTrayZone.TakenAnywaySeconds:0.0} s); sale ended={Game.Pos.Current == null}; errors on the POS={Desk.Tray.ErrorCount}");
        Check("rule.too_much_leaves_the_till_short",
            director.TillShort == shortBefore + over && director.OverChanges == oversBefore + 1 && director.LastOutcome.Contains($"거스름돈 {over:N0}원 더 나감"),
            $"due {due}, given {due + over}; till short {shortBefore}->{director.TillShort}; customers given too much {oversBefore}->{director.OverChanges}; outcome={director.LastOutcome.Replace("\n", " / ")}");
        yield return LookRightAfterPaid();
        yield return Capture("14y_too_much_change_taken");
        yield return WaitUntil(() => Desk.Register.Shut, 3f, "rule.too_much.drawer_shut_after_the_sale");
        yield return WaitUntil(() => director.ChangeTakenByHand > takenBefore && !director.ChangeInHand, 6f, "rule.too_much.taken_by_the_customer");
    }

    /// <summary>
    /// Scans again on purpose, lets go, and presses the POS [-] with the ray and trigger. The first read was on the flat
    /// glass; this one is at the upright glass, after the product has been out of the scanner's sight for the cooldown.
    /// </summary>
    IEnumerator OverscanAndCancel(ProductPrefab product)
    {
        var center = product.transform.position;
        float travel = Mathf.Max(center.y, TravelHeight);
        yield return MoveProduct(product, new Vector3(center.x, travel, center.z), "cancel.rise");
        yield return MoveProduct(product, new Vector3(BesideScanner.x, travel, BesideScanner.y), "cancel.beside");
        yield return new WaitForSeconds(ScanCooldown);
        yield return ShowToUprightGlass(product, "cancel.second_scan");
        var line = Game.Pos.Lines.Find(l => l.Data == product.Data);
        Check("cancel.overscan_blocks_payment", line != null && line.Scanned == 2 && !Game.Pos.ReadyForPayment && Game.Pos.Notice.Contains("취소"), $"scanned={line?.Scanned}; notice={Game.Pos.Notice}");
        yield return Capture("13x_overscan");
        yield return SetAside(product, 0);
        yield return ResetHandRotation();

        int row = Game.Pos.Lines.IndexOf(line);
        // The middle, not the transform: a WorldUi rectangle's pivot is its top-left corner, and a ray aimed at the corner
        // lands on the button or just off it (one run of 2026-11-11 failed here, the same build passed again).
        var button = Middle(Screen.CancelButtons[row]);
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
        // What the hand hears and feels as it takes the thing (TouchFeel, 2026-11-11): a film bag crackles, a carton knocks.
        var touch = rightHand.GetComponentInChildren<TouchFeel>();
        var itsSound = product.Data.Package == ProductPackage.Carton ? StoreSound.Carton : StoreSound.Crinkle;
        int soundsBefore = Game.Pos.Audio.CountOf(itsSound), pulsesBefore = touch != null ? touch.Pulses : 0;
        heldKeys.Add(Key.G);
        yield return WaitUntil(() => product.Grab.isSelected, 2f, label + ".selected");
        yield return new WaitForSeconds(0.2f);
        Check(label + ".near_grab", rightInteractor.selectionRegion.Value == NearFarInteractor.Region.Near, $"region={rightInteractor.selectionRegion.Value}");
        Check(label + ".sounds_and_feels_like_its_package", touch != null && Game.Pos.Audio.CountOf(itsSound) == soundsBefore + 1 && touch.Pulses > pulsesBefore
                                                         && touch.Holding == (itsSound == StoreSound.Carton ? TouchKind.Carton : TouchKind.Bag),
            touch == null ? "no touch feel on the hand" : $"{product.Data.Id} is a {product.Data.Package}; {itsSound} sounds {soundsBefore}->{Game.Pos.Audio.CountOf(itsSound)}; pulses {pulsesBefore}->{touch.Pulses}; holding={touch.Holding}");
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
        // It is stood in the bag with what is already there, not left lying as it fell.
        int rustles = Game.Pos.Audio.CountOf(StoreSound.Bag);
        float begun = Time.realtimeSinceStartup;
        while (!Bag.Settled && Time.realtimeSinceStartup - begun < 2f) yield return null;
        yield return new WaitForSeconds(0.2f);
        if (product == null)
        {
            Check(label + ".stands_in_the_bag", false, "the product was gone as soon as it was bagged");
            yield break;
        }
        var floor = Bag.Floor.position;
        var middle = product.transform.TransformPoint(product.Proxy.center) - floor;
        float upright = Vector3.Dot(product.transform.up, Vector3.up);
        float lowest = LowestProxyPoint(product) - floor.y;
        Check(label + ".stands_in_the_bag",
            Bag.Settled && upright > 0.98f && Mathf.Abs(middle.x) < Bag.Inside.x * 0.5f && Mathf.Abs(middle.z) < Bag.Inside.y * 0.5f + 0.01f && Mathf.Abs(lowest) < 0.012f && rustles >= 1,
            $"settled={Bag.Settled}; upright={upright:0.000}; middle={middle.ToString("F3")}; lowest={lowest:0.000}; bag sounds={rustles}");
        // One thing in the bag as the clerk sees it; then two, from the clerk's side and from the customer's.
        if (label == "tutorial.bag") yield return CaptureFrom("bag_1_one_thing_in", new Vector3(-0.2f, 1.5f, -0.1f), new Vector3(-0.4f, 1.0f, 0.56f), 60f);
        if (label == "order1.item1.bag")
        {
            yield return CaptureFrom("bag_2_two_things_in", new Vector3(-0.2f, 1.5f, -0.1f), new Vector3(-0.4f, 1.0f, 0.56f), 60f);
            yield return CaptureFrom("bag_3_two_things_in_close", new Vector3(-0.08f, 1.25f, 0.2f), new Vector3(-0.4f, 1.02f, 0.56f), 60f);
        }
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
        int landingsBefore = Game.Pos.Audio.CountOf(StoreSound.BagDown) + Game.Pos.Audio.CountOf(StoreSound.Carton);
        yield return SetDown(product.GetComponent<Rigidbody>());
        yield return new WaitForSeconds(0.6f);
        int landings = Game.Pos.Audio.CountOf(StoreSound.BagDown) + Game.Pos.Audio.CountOf(StoreSound.Carton) - landingsBefore;
        Check("aside.lands_with_a_sound", landings >= 1, $"{product.Data.Id} let go 3 cm over the counter: landing sounds {landings}");
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
        // A customer's card is not gone the moment it is approved (2026-11-12): the clerk lays it back on their plate
        // and they take it. The clerk's own time cards still go at once.
        if (Game.Director != null && Game.Director.IsOut(card))
        {
            yield return LookRightAfterPaid();
            yield return GiveCardBack(card, label);
        }
        else
        {
            heldKeys.Remove(Key.G);
        }
        yield return new WaitForSeconds(0.2f);
        Check(label + ".guide_hidden_after_payment", !slot.Guide.activeSelf && !slot.CardInside, $"guide={slot.Guide.activeSelf}; inside={slot.CardInside}");
    }

    /// <summary>The customer's hand on the card has been pictured once this run.</summary>
    bool cardBackPictured;

    IEnumerator GiveCardBack(PaymentProp card, string label)
    {
        var director = Game.Director;
        int taken = director.CardsTakenBack, kept = director.CardsNotReturned;
        if (label == "order5.card")
        {
            // Once a run the clerk forgets: the card is left sitting in the terminal, and the customer goes after a while.
            heldKeys.Remove(Key.G);
            float left = Time.time;
            yield return WaitUntil(() => director.CardsNotReturned > kept, 9f, label + ".customer_stops_waiting");
            float waited = Time.time - left;
            yield return null;
            Check(label + ".card_not_given_back_is_gone", card == null && director.CardsTakenBack == taken && waited > 4f && waited < 7f,
                $"gone={card == null}; the customer waited {waited:0.0} s; taken back {director.CardsTakenBack - taken}; not given back {director.CardsNotReturned - kept}");
            yield break;
        }
        var plate = Desk.CustomerPlate.position;
        // Out the way it went in, and down on the plate it came from.
        yield return MoveProp(card, CardSlotFront(), label + ".out");
        yield return CarryProp(card, plate + Vector3.up * 0.04f, label + ".back");
        if (card == null) yield break;
        yield return SetDown(card.GetComponent<Rigidbody>());
        if (!cardBackPictured)
        {
            float asked = Time.time;
            while (!director.CardInHand && director.CardsTakenBack == taken && Time.time - asked < 5f) yield return null;
            if (director.CardInHand)
            {
                cardBackPictured = true;
                yield return new WaitForSeconds(0.06f);
                yield return CaptureFrom("card_back_1_customer_takes_the_card", new Vector3(0.2f, 2.3f, 1.55f), new Vector3(0.2f, 0.92f, 0.92f), 45f);
                yield return Capture("card_back_2_from_the_clerk");
            }
        }
        yield return WaitUntil(() => director.CardsTakenBack > taken, 8f, label + ".card_taken_back");
        yield return null;
        Check(label + ".customer_takes_the_card_back", card == null && director.CardsTakenBack == taken + 1 && director.CardsNotReturned == kept && director.FarthestFromCard < 0.06f,
            $"gone={card == null}; taken back by the customer's hand {director.CardsTakenBack - taken}; not given back {director.CardsNotReturned - kept}; the hand at most {director.FarthestFromCard * 100f:0.0} cm short of the card so far");
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
        // The order is ready, so the till has let its drawer go. It has to be all the way out before money goes in.
        yield return WaitUntil(() => Desk.Register.FullyOpen, 3f, label + ".drawer_open");
        yield return null;
        // Only a lesson shows what lies on the tray (2026-11-13): with a customer that cell is not there.
        Check(label + ".till_filled_and_pos_waiting", TillFilled() && Screen.ReceivedText == "–" && Screen.LaidText == (Desk.Guided ? "–" : ""),
            $"pieces={Desk.StockItems.Count}; received={Screen.ReceivedText}; laid={Screen.LaidText}");
        yield return GrabProp(bill, label + ".grab");
        if (bill == null) yield break;
        int value = bill.Value;
        yield return CarryProp(bill, Desk.Drawer.transform.position, label);
        yield return SetDown(bill.GetComponent<Rigidbody>());
        yield return WaitUntil(() => bill == null, 3f, label + ".received");
        yield return new WaitForSeconds(0.3f);
        // A note that covers the amount exactly ends the sale at once, and the strip is gone with it.
        if (!Desk.CashActive) yield break;
        if (!tillPictured)
        {
            // Once a run: the open drawer as someone standing at it sees it, and the POS with its cash strip.
            tillPictured = true;
            yield return CaptureFrom("cash_1_till_open", new Vector3(-0.66f, 1.5f, 0.02f), new Vector3(-0.78f, 0.95f, 0.42f), 60f);
            yield return CaptureFrom("cash_2_pos_strip", new Vector3(-0.3f, 1.42f, 0.25f), new Vector3(-0.62f, 1.3f, 0.98f), 48f);
        }
        if (Desk.CashActive)
            Check(label + ".pos_shows_received_and_change", Desk.CashReceived == value && Screen.ReceivedText == $"{value:N0}" && Screen.ChangeText == $"{Desk.ChangeDue:N0}" && Screen.LaidText == (Desk.Guided ? "0" : ""),
                $"desk={Desk.CashReceived}; received={Screen.ReceivedText}; change={Screen.ChangeText}; laid={Screen.LaidText}");
    }

    bool tillPictured, changePictured, plainStripPictured;

    /// <summary>Every slot and cup of the open drawer has its piece lying on top, not yet taken.</summary>
    bool TillFilled()
    {
        foreach (var slot in Desk.Register.Slots)
            if (!Desk.StockItems.TryGetValue(slot.Value, out var piece) || piece == null || !piece.Docked) return false;
        return Desk.Register.Slots.Length == Denominations.Length;
    }

    /// <summary>Lays the change down one piece at a time, as a person would, which must not raise false errors.</summary>
    /// <summary>The customer's hand on the change has been pictured once this run.</summary>
    bool takingPictured;

    IEnumerator GiveChange(int change, int expectedErrors, bool pauseAfterFirstPiece, Func<bool> done, string label)
    {
        var director = Game.Director;
        int takenBefore = director.ChangeTakenByHand, piecesBefore = director.PiecesTakenByHand;
        int tipsBefore = director.Tips, shortBefore = director.TillShort, asksBefore = director.ChangeAsks;
        bool lesson = Desk.Guided;
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
                if (offset == 0 && lesson)
                    Check(label + ".pos_shows_what_is_on_the_tray", Desk.Tray.Amount == value && Screen.LaidText == $"{value:N0}", $"tray={Desk.Tray.Amount}; laid={Screen.LaidText}");
                else if (offset == 0)
                {
                    Check(label + ".pos_leaves_the_counting_to_the_clerk", Desk.Tray.Amount == value && Screen.LaidText == "" && Screen.ChangeText == $"{change:N0}" && Game.Pos.Notice.StartsWith("거스름돈"),
                        $"tray={Desk.Tray.Amount}; change shown={Screen.ChangeText}; laid shown='{Screen.LaidText}'; notice={Game.Pos.Notice}");
                    if (!plainStripPictured)
                    {
                        // Once a run: the POS with a customer's cash sale on it, two amounts and no count of the tray.
                        plainStripPictured = true;
                        yield return CaptureFrom("cash_2b_pos_strip_with_a_customer", new Vector3(-0.3f, 1.42f, 0.25f), new Vector3(-0.62f, 1.3f, 0.98f), 48f);
                    }
                }
                yield return SetDown(item.GetComponent<Rigidbody>());
                yield return new WaitForSeconds(0.25f);
                if (!changePictured && remaining > value)
                {
                    // Once a run, with more change still to come: the first piece lying on the tray, the drawer beside it.
                    changePictured = true;
                    yield return CaptureFrom("cash_3_change_on_the_tray", new Vector3(-0.5f, 1.5f, 0.15f), new Vector3(-0.52f, 0.92f, 0.66f), 62f);
                }
                remaining -= value;
                offset++;
                if (pauseAfterFirstPiece && offset == 1 && remaining > 0)
                {
                    // Too little, and no more coming: the customer asks for the rest (2026-11-13; until then the POS said so).
                    int asks = director.ChangeAsks, said = Game.Pos.Audio.SpokenCount(CustomerLine.ChangeShort);
                    var waiting = director.AtCounter;
                    yield return new WaitForSeconds(StoppedAddingWait);
                    Check("rule.short_change_customer_asks",
                        director.ChangeAsks == asks + 1 && Game.Pos.Audio.SpokenCount(CustomerLine.ChangeShort) == said + 1 && Game.Pos.Current != null && Desk.Tray.ErrorCount == 0
                            && !Game.Pos.Notice.Contains("적습니다") && waiting != null && waiting.FaceRig != null && waiting.FaceRig.Mood == CustomerMood.Impatient,
                        $"asked {asks}->{director.ChangeAsks}; line spoken {said}->{Game.Pos.Audio.SpokenCount(CustomerLine.ChangeShort)}; clip {(Game.Pos.Audio.Female.ChangeShort != null && Game.Pos.Audio.Male.ChangeShort != null ? "there" : "MISSING")}; mood={(waiting != null && waiting.FaceRig != null ? waiting.FaceRig.Mood.ToString() : "none")}; notice={Game.Pos.Notice}; errors on the POS={Desk.Tray.ErrorCount}");
                    yield return Capture("12y_short_change_asked");
                }
            }
        }
        yield return WaitUntil(done, 5f, label + ".matched");
        Check(label + ".no_false_errors", Desk.Tray.ErrorCount == expectedErrors, $"errors={Desk.Tray.ErrorCount}; expected={expectedErrors}; pieces={offset}");
        if (pauseAfterFirstPiece)
            Check("rule.short_change_costs_the_tip", director.Tips == tipsBefore && !director.LastOutcome.Contains("팁") && director.TillShort == 0,
                $"tips {tipsBefore}->{director.Tips}; outcome={director.LastOutcome.Replace("\n", " / ")}; till short={director.TillShort}");
        else if (!lesson)
            Check(label + ".right_change_is_not_a_mistake", director.TillShort == shortBefore && director.ChangeAsks == asksBefore,
                $"till short {shortBefore}->{director.TillShort}; times a customer asked for the rest {asksBefore}->{director.ChangeAsks}");
        yield return LookRightAfterPaid();
        // The customer takes the change off the tray with the right hand before going (2026-11-12). Before, it was
        // simply gone the moment the sale was done.
        if (!takingPictured)
        {
            float asked = Time.time;
            while (!director.ChangeInHand && director.ChangeTakenByHand == takenBefore && Time.time - asked < 5f) yield return null;
            if (director.ChangeInHand)
            {
                takingPictured = true;
                yield return new WaitForSeconds(0.08f);
                // From above and behind the customer: the screens on the counter stand between the tray and anywhere lower.
                yield return CaptureFrom("cash_4_customer_takes_the_change", new Vector3(-0.3f, 2.3f, 1.5f), new Vector3(-0.2f, 0.92f, 0.92f), 45f);
                yield return Capture("cash_5_taking_the_change_from_the_clerk");
            }
        }
        yield return WaitUntil(() => Desk.Register.Shut, 3f, label + ".drawer_shut_after_the_sale");
        yield return WaitUntil(() => director.ChangeTakenByHand > takenBefore && !director.ChangeInHand, 6f, label + ".taken_by_the_customer");
        yield return null;
        int lying = 0;
        foreach (var prop in FindObjectsByType<PaymentProp>(FindObjectsSortMode.None))
            if (prop.Kind == PaymentKind.Change && !prop.Docked) lying++;
        Check(label + ".customer_takes_the_change_by_hand",
            director.ChangeTakenByHand == takenBefore + 1 && director.PiecesTakenByHand - piecesBefore == offset && lying == 0 && director.FarthestFromChange < 0.06f,
            $"taken by a customer's hand {director.ChangeTakenByHand - takenBefore} time(s), {director.PiecesTakenByHand - piecesBefore} of {offset} pieces; still lying about {lying}; the hand at most {director.FarthestFromChange * 100f:0.0} cm short of the change so far");
    }

    /// <summary>The middle of a UI element in the world. Its transform is its pivot, which WorldUi puts at the top-left corner.</summary>
    static Vector3 Middle(Component ui)
    {
        var rect = (RectTransform)ui.transform;
        return rect.TransformPoint(rect.rect.center);
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
        Check("setup.product_names_readable", broken.Count == 0 && Game.Catalog.Products.Count == 21, $"products={Game.Catalog.Products.Count}; broken={string.Join(",", broken)}");
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
            if (slot.Merged != null)
            {
                displays += slot.Merged.ShownUnits;
                if (slot.Merged.Active.isActiveAndEnabled && slot.Merged.Active.m_Asset != null) renderers += slot.Merged.ShownUnits;
                physical += slot.Merged.GetComponentsInChildren<Collider>().Length + slot.Merged.GetComponentsInChildren<ProductPrefab>().Length;
                continue;
            }
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
        foreach (var state in statFrames)
            Debug.Log($"[P2 Day] fps in {state.Key}: {state.Value / Mathf.Max(0.01f, statSeconds[state.Key]):0.0} over {statSeconds[state.Key]:0.0}s");
        report.scope = (growthHandsMode ? "A store that is kept: two boxes of new products shelved by hand on display shelves; the title, the time card and the customers by code. " : day2Mode ? "Day 2 (intake, disposal, leaving customers). " : "") + "XR Device Simulator with virtual keyboard/mouse. No HMD, no physical controllers, nothing about reach or comfort. FPS is desktop-only and not a VR measurement. Prices and the scenario are placeholders; customers are Rocketbox characters on a NavMesh in a placeholder store layout.";
        File.WriteAllText(Path.Combine(output, growthHandsMode ? "growth_hands_report.json" : introMode ? "intro_report.json" : faceMode ? "face_report.json" : titleMode ? "title_report.json" : day2Mode ? "day2_report.json" : "day_report.json"), JsonUtility.ToJson(report, true));
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
