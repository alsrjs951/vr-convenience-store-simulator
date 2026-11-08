using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using Random = UnityEngine.Random;

/// <summary>
/// Customers for one day (doc 6.1, 6.2, D12). One order at a time at the counter. Up to two of the next customers come in
/// early, look at one or two shelves and line up behind the counter (three at most with the one paying). The one at the
/// front of the line is called to the counter spawn_after_prev_sec after the previous order ends, or a new customer walks
/// in from the door if nobody is waiting. The time limit starts when the customer stands at the counter, and the 3DGS
/// products are made then too: until that moment the customer only carries an empty basket. One or two browsing-only
/// customers walk between the shelves and leave without ordering.
///
/// QueueAndBrowsers = false falls back to one customer at a time from the door (D12 ④), with the same characters and
/// NavMesh. Scenario numbers (count, spacing, time limit) come from the CSV either way.
/// </summary>
public class CustomerDirector : MonoBehaviour
{
    // [제안] values from game design 8 and 19: 1-2 shelves for 3-8 s each, 1-2 browsing-only customers.
    const float BrowseMinSeconds = 3f;
    const float BrowseMaxSeconds = 8f;
    const int MaxWaiting = 2;
    const int LineCapacity = 3;
    const float EarlyEntryGapMin = 2f;
    const float EarlyEntryGapMax = 6f;
    const float BrowserFirstMin = 3f;
    const float BrowserFirstMax = 6f;
    const float BrowserGapMin = 8f;
    const float BrowserGapMax = 20f;
    const int MaxCharacters = 5;
    const float ItemSpacing = 0.25f;
    const float SpawnClearance = 0.004f;
    const float MetersPerPixel = 0.0007f;
    // Leaving customers vanish once inside the dark porch, within this of the door point that several may head for at once.
    const float DoorArriveRadius = 0.6f;
    /// <summary>Keep right in the doorway too: people come in on the right half and leave by the other half.</summary>
    const float DoorLane = 0.45f;
    const float MouthHeight = 1.5f;
    const float GreetDelaySeconds = 0.3f;
    const float GreetAfterBellSeconds = 1.0f;
    const float LinePauseSeconds = 0.35f;
    const float HurryVoiceDelay = 0.45f;
    const float DoorChimeHeight = 2.2f;
    const float MoveUpDelay = 0.4f;
    // Day 2 puts the clerk in front of the shelves. A spot the clerk stands at would leave a customer walking into them
    // until the stuck warp, so such spots are skipped (2026-11-08). Day 1 keeps the clerk behind the counter, far from all.
    const float ClerkClearance = 0.9f;
    const float BasketWidth = 0.3f;
    const float BasketWall = 0.012f;
    const float BasketHeight = 0.14f;

    public POSController Pos;
    public PaymentDesk Desk;
    public GameAudio Audio;
    public Transform Door, Counter, BasketAnchor;
    /// <summary>Front of the line first. Each marker's forward is the way the customer faces there.</summary>
    public Transform[] QueueSlots = new Transform[0];
    /// <summary>Spots in front of shelves. Each marker's forward looks at the shelf.</summary>
    public Transform[] BrowseSpots = new Transform[0];
    public CustomerCharacter[] Characters = new CustomerCharacter[0];
    public Material BasketMaterial;
    public Material BodyMaterial;
    public bool QueueAndBrowsers = true;
    public int BrowserTarget = 2;
    /// <summary>Doc 6.3: on Days 2 and 3 the day ends at once when this many customers have left.</summary>
    public int LeaveLimit = 3;

    public int Paid { get; private set; }
    public int Sales { get; private set; }
    public int Left { get; private set; }
    public int CustomerNumber { get; private set; }
    /// <summary>The day ended because LeaveLimit customers left (Days 2 and 3 only).</summary>
    public bool EndedEarly { get; private set; }
    public int Hurries { get; private set; }
    /// <summary>Times a shelf spot was passed over because the clerk stood at it.</summary>
    public int SpotsSkippedForClerk { get; private set; }
    public int Bells { get; private set; }
    public bool HudFlashing => Time.time < flashUntil;
    public string HudText => hud != null ? hud.text : "";
    public event Action<Order, bool> OrderEnded;
    public float Remaining => remaining;
    public bool ShiftRunning { get; private set; }
    public float ShiftStartedAt { get; private set; } = -1f;
    public CustomerCharacter AtCounter { get; private set; }
    /// <summary>Products of the current order, for the HUD and for tests.</summary>
    public readonly List<ProductPrefab> OrderProducts = new List<ProductPrefab>();
    /// <summary>Came in early for an order, not called yet: browsing, walking or in line. In the order they came in.</summary>
    public readonly List<CustomerCharacter> Waiting = new List<CustomerCharacter>();
    /// <summary>The part of Waiting that stands in line, front first.</summary>
    public readonly List<CustomerCharacter> Line = new List<CustomerCharacter>();
    public readonly List<CustomerCharacter> Browsers = new List<CustomerCharacter>();
    public readonly List<CustomerCharacter> Present = new List<CustomerCharacter>();
    public readonly List<CustomerRecord> Records = new List<CustomerRecord>();
    public int MaxPresent { get; private set; }
    public int MaxWaitingSeen { get; private set; }
    public int MaxLineSeen { get; private set; }
    public int MaxForOrdersInStore { get; private set; }
    public int MaxBrowsersSeen { get; private set; }
    public int MaxAtCounterSeen { get; private set; }
    public int BrowsersSpawned { get; private set; }
    public Transform CounterBasket { get; private set; }

    Text hud, hudTitle, hudPayText, hudBagText;
    Image hudPay, hudBag, hudTime;
    Image hudBackground;
    Color hudColor;
    float flashUntil = -1f;
    bool hurried;
    float remaining;
    bool orderPaid;
    List<Order> dayOrders = new List<Order>();
    int calledCount;
    int spawnedForOrders;
    int spawnedTotal;
    readonly Dictionary<CustomerCharacter, Coroutine> behaviours = new Dictionary<CustomerCharacter, Coroutine>();
    readonly Dictionary<Transform, CustomerCharacter> reservedSpots = new Dictionary<Transform, CustomerCharacter>();
    readonly Dictionary<CustomerCharacter, Transform> lastSpot = new Dictionary<CustomerCharacter, Transform>();

    void Start()
    {
        var panel = WorldUi.CreatePanel(transform, "Order HUD", transform.position, transform.rotation, new Vector2(620f, 470f), MetersPerPixel, WorldUi.Palette.Panel);
        hudTitle = WorldUi.AddText(panel, 28f, 20f, 560f, 50f, 36);
        hudTitle.fontStyle = FontStyle.Bold;
        hud = WorldUi.AddText(panel, 28f, 82f, 564f, 250f, 36);
        hudPay = WorldUi.AddBox(panel, 28f, 340f, 170f, 52f, WorldUi.Palette.Accent);
        hudPayText = WorldUi.AddText((RectTransform)hudPay.transform, 0f, 0f, 170f, 52f, 30, TextAnchor.MiddleCenter);
        hudBag = WorldUi.AddBox(panel, 212f, 340f, 200f, 52f, WorldUi.Palette.Grey);
        hudBagText = WorldUi.AddText((RectTransform)hudBag.transform, 0f, 0f, 200f, 52f, 30, TextAnchor.MiddleCenter);
        WorldUi.AddBox(panel, 28f, 414f, 564f, 22f, new Color(1f, 1f, 1f, 0.15f));
        hudTime = WorldUi.AddBox(panel, 28f, 414f, 564f, 22f, WorldUi.Palette.Good);
        hudTime.type = Image.Type.Filled;
        hudTime.fillMethod = Image.FillMethod.Horizontal;
        hudTime.sprite = null;
        SetHudVisible(false);
        hudBackground = panel.GetComponent<Image>();
        hudColor = hudBackground.color;
        Pos.Changed += RefreshHud;
        Pos.PaymentCompleted += OnPaid;
        CustomerCharacter.StuckWarps = 0;
        CustomerCharacter.ShortArrivals = 0;
        CustomerCharacter.Nudges = 0;
        CustomerCharacter.ShortArrivalsAtClerk = 0;
        CustomerCharacter.StepAsides = 0;
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-customers-sequential") >= 0) QueueAndBrowsers = false;
    }

    void OnDestroy()
    {
        if (!Pos) return;
        Pos.Changed -= RefreshHud;
        Pos.PaymentCompleted -= OnPaid;
    }

    void Update()
    {
        int forOrders = Waiting.Count + (AtCounter != null ? 1 : 0);
        MaxPresent = Mathf.Max(MaxPresent, Present.Count);
        MaxWaitingSeen = Mathf.Max(MaxWaitingSeen, Waiting.Count);
        MaxLineSeen = Mathf.Max(MaxLineSeen, Line.Count);
        MaxForOrdersInStore = Mathf.Max(MaxForOrdersInStore, forOrders);
        MaxBrowsersSeen = Mathf.Max(MaxBrowsersSeen, Browsers.Count);
        int atCounter = 0;
        foreach (var customer in Present)
            if (customer != null && customer.State == CustomerState.AtCounter) atCounter++;
        MaxAtCounterSeen = Mathf.Max(MaxAtCounterSeen, atCounter);
        if (hudBackground != null)
            hudBackground.color = HudFlashing && Mathf.Repeat(Time.time, 0.5f) < 0.25f ? new Color(0.62f, 0.44f, 0.06f, 0.96f) : hudColor;
    }

    /// <summary>Clears the day's numbers and records before the next day. Customers still walking out finish leaving.</summary>
    public void ResetDay()
    {
        Paid = Sales = Left = 0;
        CustomerNumber = 0;
        EndedEarly = false;
        Hurries = Bells = 0;
        Records.Clear();
        MaxPresent = MaxWaitingSeen = MaxLineSeen = MaxForOrdersInStore = MaxBrowsersSeen = MaxAtCounterSeen = 0;
        BrowsersSpawned = 0;
        calledCount = 0;
        spawnedForOrders = 0;
        ShiftStartedAt = -1f;
        CustomerCharacter.StuckWarps = 0;
        CustomerCharacter.ShortArrivals = 0;
        CustomerCharacter.Nudges = 0;
        CustomerCharacter.ShortArrivalsAtClerk = 0;
        CustomerCharacter.StepAsides = 0;
        if (hud != null) hud.text = "";
    }

    public IEnumerator RunDay(List<Order> orders)
    {
        dayOrders = orders;
        calledCount = 0;
        ShiftRunning = true;
        ShiftStartedAt = Time.time;
        if (QueueAndBrowsers)
        {
            StartCoroutine(EarlyEntries());
            if (BrowserTarget > 0) StartCoroutine(BrowsingOnly());
        }
        float previousEnd = Time.time;
        foreach (var order in orders)
        {
            float callAt = previousEnd + order.SpawnAfterPreviousSeconds;
            while (Time.time < callAt) yield return null;
            var customer = CallNext(order);
            yield return RunOrder(order, customer);
            previousEnd = Time.time;
            if (order.Day > 1 && Left >= LeaveLimit)
            {
                EndedEarly = true;
                break;
            }
        }
        ShiftRunning = false;
        if (EndedEarly)
        {
            // Doc 6.3: no game over; whoever is still waiting for a turn goes home too.
            foreach (var waiting in Waiting.ToArray())
            {
                StopBehaviour(waiting);
                Waiting.Remove(waiting);
                Line.Remove(waiting);
                ReleaseSpot(waiting);
                behaviours[waiting] = StartCoroutine(LeaveStore(waiting, false));
            }
            hud.text = $"이탈 손님 {Left}명. 오늘 영업을 마감합니다.";
        }
        else
        {
            hud.text = "오늘 손님이 모두 끝났습니다.";
        }
    }

    // ---------- Orders ----------

    /// <summary>The front of the line, else whoever came in first and is still looking around, else someone new at the door.</summary>
    CustomerCharacter CallNext(Order order)
    {
        CustomerCharacter next = null;
        string from = "door";
        if (Line.Count > 0)
        {
            next = Line[0];
            from = "line";
        }
        else if (Waiting.Count > 0)
        {
            next = Waiting[0];
            from = "browsing";
        }
        if (next != null)
        {
            StopBehaviour(next);
            Waiting.Remove(next);
            Line.Remove(next);
            ReleaseSpot(next);
        }
        else
        {
            next = Spawn(true);
        }
        calledCount++;
        CustomerNumber = order.Sequence;
        AtCounter = next;
        next.Record.Sequence = order.Sequence;
        next.Record.CalledAt = Time.time;
        next.Record.CalledFrom = from;
        next.State = CustomerState.ToCounter;
        // Called away from a shelf mid-look: stand at the counter with the idle clip, not the looking-around one.
        next.Resting = CustomerCharacter.IdleState;
        next.Agent.avoidancePriority = 30;
        return next;
    }

    IEnumerator RunOrder(Order order, CustomerCharacter customer)
    {
        yield return customer.WalkTo(Counter.position);
        yield return customer.Face(Counter.rotation);
        customer.State = CustomerState.AtCounter;
        customer.Resting = CustomerCharacter.IdleState;

        // Arrived (doc 6.2): basket down, products made now, HUD, then the timer.
        PlaceBasket(customer, order);
        SpawnProducts(order);
        Pos.BeginOrder(order);
        BeginPayment(order);
        remaining = order.TimeLimitSeconds;
        customer.Record.ArrivedAt = Time.time;
        customer.Record.RemainingAtArrival = remaining;
        orderPaid = false;
        bool warned = false;
        hurried = false;
        if (order.Day > 1)
        {
            // Doc 5.7.2: someone may be off shelving, so the arrival is announced with the door bell and a blinking HUD.
            Bells++;
            if (Audio) Audio.PlayAt(StoreSound.Bell, Mouth(customer));
            flashUntil = Time.time + 2f;
        }
        if (Audio) StartCoroutine(SpeakOnArrival(customer, order));
        RefreshHud();

        while (!orderPaid)
        {
            remaining -= Time.deltaTime;
            if (order.Day > 1 && !hurried && remaining <= order.TimeLimitSeconds * 0.5f)
            {
                // Game design 8: at half time the customer hurries the clerk (voice clip pending, P3-K3).
                hurried = true;
                Hurries++;
                customer.Record.HurriedAt = Time.time;
                if (Audio)
                {
                    Audio.PlayAt(StoreSound.Hurry, Mouth(customer));
                    StartCoroutine(SayLater(customer, CustomerLine.Hurry, HurryVoiceDelay));
                }
                flashUntil = Time.time + 1.5f;
            }
            RefreshHud();
            if (remaining <= 0f)
            {
                // Day 1 only warns (doc 6.3); later days lose the customer.
                if (order.Day > 1) break;
                if (!warned)
                {
                    warned = true;
                    if (Audio) Audio.PlayError();
                }
            }
            yield return null;
        }

        if (orderPaid)
        {
            Paid++;
            Sales += Pos.Total;
        }
        else
        {
            Left++;
            customer.Record.TimedOut = true;
            if (Audio) Audio.Say(CustomerLine.Leave, GameAudio.IsFemale(customer.Record.Avatar), Mouth(customer));
        }
        customer.Record.DoneAt = Time.time;
        Desk.EndPayment();
        Pos.EndOrder();
        foreach (var product in OrderProducts)
            if (product) Destroy(product.gameObject);
        OrderProducts.Clear();
        if (CounterBasket) Destroy(CounterBasket.gameObject);
        CounterBasket = null;
        hud.text = "";
        SetHudVisible(false);
        AtCounter = null;
        behaviours[customer] = StartCoroutine(LeaveStore(customer, orderPaid));
        OrderEnded?.Invoke(order, orderPaid);
    }

    // ---------- Voices (doc 6.1) ----------

    static Vector3 Mouth(CustomerCharacter customer) => customer.transform.position + Vector3.up * MouthHeight;

    /// <summary>Greeting, then the order's special request, then the bag request, each after the last one finished.</summary>
    IEnumerator SpeakOnArrival(CustomerCharacter customer, Order order)
    {
        yield return new WaitForSeconds(order.Day > 1 ? GreetAfterBellSeconds : GreetDelaySeconds);
        if (AtCounter != customer) yield break;
        bool female = GameAudio.IsFemale(customer.Record.Avatar);
        float talking = Audio.Say(CustomerLine.Greet, female, Mouth(customer), order.Sequence);
        var requests = new List<CustomerLine>();
        switch (order.Special)
        {
            case "microwave": requests.Add(CustomerLine.Microwave); break;
            case "hotwater": requests.Add(CustomerLine.HotWater); break;
            case "oneplus": requests.Add(CustomerLine.OnePlus); break;
        }
        if (order.NeedsBag) requests.Add(CustomerLine.Bag);
        foreach (var line in requests)
        {
            yield return new WaitForSeconds(talking + LinePauseSeconds);
            if (AtCounter != customer) yield break;
            talking = Audio.Say(line, female, Mouth(customer));
        }
    }

    IEnumerator SayLater(CustomerCharacter customer, CustomerLine line, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (customer != null && AtCounter == customer) Audio.Say(line, GameAudio.IsFemale(customer.Record.Avatar), Mouth(customer));
    }

    void BeginPayment(Order order)
    {
        int orderTotal = 0;
        foreach (var line in order.Lines) orderTotal += line.Product.Data.Price * line.Quantity;
        if (order.Payment == PaymentMethod.Card)
        {
            Desk.BeginCard(() => Pos.ReadyForPayment, Pos.SetNotice, Pos.CompleteCard);
        }
        else
        {
            Desk.BeginCash(PaymentDesk.BillFor(orderTotal), () => Pos.ReadyForPayment, () => Pos.Total, Pos.ReceiveCash, Pos.SetNotice, Pos.CompleteCash);
        }
    }

    void OnPaid() => orderPaid = true;

    int ItemCount(Order order)
    {
        int count = 0;
        foreach (var line in order.Lines) count += line.Quantity;
        return count;
    }

    void SpawnProducts(Order order)
    {
        int index = 0;
        foreach (var line in order.Lines)
        {
            for (int i = 0; i < line.Quantity; i++)
            {
                var height = line.Product.Proxy.size.y;
                var position = BasketAnchor.position + Vector3.right * (index * ItemSpacing) + Vector3.up * (height * 0.5f + SpawnClearance);
                // Standing with the front to the player, so the barcode has to be found by turning it.
                OrderProducts.Add(Instantiate(line.Product, position, Quaternion.Euler(0f, 180f, 0f)));
                index++;
            }
        }
    }

    // ---------- Early entries, browsing and the line (D12) ----------

    IEnumerator EarlyEntries()
    {
        while (ShiftRunning)
        {
            if (CanLetOneIn())
            {
                if (spawnedForOrders > 0) yield return new WaitForSeconds(Random.Range(EarlyEntryGapMin, EarlyEntryGapMax));
                if (!ShiftRunning || !CanLetOneIn()) continue;
                var customer = Spawn(true);
                Waiting.Add(customer);
                behaviours[customer] = StartCoroutine(WaitForTurn(customer));
            }
            yield return null;
        }
    }

    /// <summary>Doc 6.2: fewer than three paying or waiting, at most two waiting, and never more customers than orders left.</summary>
    bool CanLetOneIn()
    {
        int notCalled = dayOrders.Count - calledCount;
        int busy = AtCounter != null ? 1 : 0;
        return Waiting.Count < MaxWaiting && Waiting.Count < notCalled && busy + Waiting.Count < LineCapacity;
    }

    IEnumerator WaitForTurn(CustomerCharacter customer)
    {
        int shelves = Random.Range(1, 3);
        for (int i = 0; i < shelves; i++) yield return Browse(customer, CustomerState.Browsing);
        // A full line keeps the customer at the shelves a little longer.
        while (Line.Count >= QueueSlots.Length) yield return Browse(customer, CustomerState.Browsing);
        Line.Add(customer);
        customer.State = CustomerState.Queued;
        customer.Record.QueuedAt = Time.time;
        customer.Resting = CustomerCharacter.IdleState;
        while (true)
        {
            int slot = Line.IndexOf(customer);
            var spot = QueueSlots[slot];
            customer.Record.QueueSlotsTaken++;
            yield return customer.WalkTo(spot.position);
            yield return customer.Face(spot.rotation);
            while (Line.IndexOf(customer) == slot) yield return null;
            // A moment to notice the line has moved, which also lets the one ahead clear the place first.
            yield return new WaitForSeconds(MoveUpDelay);
        }
    }

    IEnumerator BrowsingOnly()
    {
        yield return new WaitForSeconds(Random.Range(BrowserFirstMin, BrowserFirstMax));
        while (ShiftRunning)
        {
            // Stops once the last order has been called, so the store empties with the day.
            if (Browsers.Count < BrowserTarget && Present.Count < MaxCharacters && calledCount < dayOrders.Count)
            {
                var customer = Spawn(false);
                Browsers.Add(customer);
                BrowsersSpawned++;
                behaviours[customer] = StartCoroutine(LookAround(customer));
                yield return new WaitForSeconds(Random.Range(BrowserGapMin, BrowserGapMax));
            }
            else
            {
                yield return null;
            }
        }
    }

    IEnumerator LookAround(CustomerCharacter customer)
    {
        int shelves = Random.Range(2, 4);
        for (int i = 0; i < shelves && ShiftRunning; i++) yield return Browse(customer, CustomerState.Wandering);
        yield return LeaveStore(customer, false);
    }

    IEnumerator Browse(CustomerCharacter customer, CustomerState state)
    {
        var spot = ReserveSpot(customer);
        if (spot == null)
        {
            yield return new WaitForSeconds(1f);
            yield break;
        }
        customer.State = state;
        yield return customer.WalkTo(spot.position);
        yield return customer.Face(spot.rotation);
        customer.Resting = CustomerCharacter.BrowseState;
        yield return new WaitForSeconds(Random.Range(BrowseMinSeconds, BrowseMaxSeconds));
        customer.Resting = CustomerCharacter.IdleState;
        customer.Record.BrowseVisits++;
        ReleaseSpot(customer);
    }

    Transform ReserveSpot(CustomerCharacter customer)
    {
        var free = new List<Transform>();
        lastSpot.TryGetValue(customer, out var last);
        var clerk = Camera.main != null ? Camera.main.transform.position : new Vector3(0f, 0f, -100f);
        foreach (var spot in BrowseSpots)
        {
            if (spot == null || spot == last || reservedSpots.ContainsKey(spot)) continue;
            var gap = spot.position - clerk;
            gap.y = 0f;
            if (gap.magnitude < ClerkClearance)
            {
                SpotsSkippedForClerk++;
                continue;
            }
            free.Add(spot);
        }
        if (free.Count == 0) return null;
        var chosen = free[Random.Range(0, free.Count)];
        reservedSpots[chosen] = customer;
        lastSpot[customer] = chosen;
        return chosen;
    }

    void ReleaseSpot(CustomerCharacter customer)
    {
        Transform held = null;
        foreach (var pair in reservedSpots)
            if (pair.Value == customer) held = pair.Key;
        if (held != null) reservedSpots.Remove(held);
    }

    IEnumerator LeaveStore(CustomerCharacter customer, bool wave)
    {
        customer.State = CustomerState.Leaving;
        customer.Resting = CustomerCharacter.IdleState;
        Browsers.Remove(customer);
        ReleaseSpot(customer);
        if (wave)
        {
            customer.Record.LeaveAnimationSeconds = customer.LeaveSeconds;
            yield return customer.PlayLeave();
        }
        yield return customer.WalkTo(DoorExit, DoorArriveRadius);
        customer.Record.GoneAt = Time.time;
        Present.Remove(customer);
        behaviours.Remove(customer);
        lastSpot.Remove(customer);
        Destroy(customer.gameObject);
    }

    void StopBehaviour(CustomerCharacter customer)
    {
        if (behaviours.TryGetValue(customer, out var running) && running != null) StopCoroutine(running);
        behaviours.Remove(customer);
    }

    // ---------- Characters and baskets ----------

    // Door faces into the store, so its right is the right-hand side of someone coming in.
    Vector3 DoorEntry => Door.position + Door.right * DoorLane;
    Vector3 DoorExit => Door.position - Door.right * DoorLane;

    CustomerCharacter Spawn(bool forOrder)
    {
        spawnedTotal++;
        if (forOrder) spawnedForOrders++;
        CustomerCharacter customer;
        string avatar;
        var prefab = PickCharacter();
        if (prefab != null)
        {
            customer = Instantiate(prefab, DoorEntry, Door.rotation);
            avatar = prefab.name;
        }
        else
        {
            customer = CreatePlaceholder();
            avatar = "placeholder";
        }
        customer.name = $"{(forOrder ? "Customer" : "Browser")} {spawnedTotal} ({avatar})";
        customer.State = forOrder ? CustomerState.Entering : CustomerState.Wandering;
        // Never two walkers of the same priority: with equal weights, two face to face both give way the same and stall.
        customer.Agent.avoidancePriority = (forOrder ? 40 : 60) + spawnedTotal % 10;
        customer.Record.Name = customer.name;
        customer.Record.Avatar = avatar;
        customer.Record.ForOrder = forOrder;
        customer.Record.SpawnedAt = Time.time;
        Records.Add(customer.Record);
        Present.Add(customer);
        if (forOrder) customer.Basket = CreateBasket(customer);
        if (Audio) Audio.PlayAt(StoreSound.Door, Door.position + Vector3.up * DoorChimeHeight);
        return customer;
    }

    /// <summary>The avatar least often in the store right now, so five people are not two pairs of twins.</summary>
    CustomerCharacter PickCharacter()
    {
        if (Characters == null || Characters.Length == 0) return null;
        var best = new List<CustomerCharacter>();
        int fewest = int.MaxValue;
        foreach (var candidate in Characters)
        {
            if (candidate == null) continue;
            int count = 0;
            foreach (var present in Present)
                if (present != null && present.Record.Avatar == candidate.name) count++;
            if (count < fewest)
            {
                fewest = count;
                best.Clear();
            }
            if (count == fewest) best.Add(candidate);
        }
        return best.Count > 0 ? best[Random.Range(0, best.Count)] : null;
    }

    /// <summary>An empty red basket in the right hand. No collider: it is only seen, and the products come at the counter.</summary>
    Transform CreateBasket(CustomerCharacter customer)
    {
        var basket = BuildBasket("Basket (empty)", 0.36f, true);
        // Fresh from Instantiate the skeleton is still in the model's A-pose; take the hand where the idle clip holds it.
        if (customer.Animator != null) customer.Animator.Update(0f);
        var hand = customer.RightHand;
        if (hand != null)
        {
            // Upright and lengthwise along the body at this moment, then it swings with the hand.
            basket.SetPositionAndRotation(hand.position + Vector3.down * 0.03f, customer.transform.rotation);
            basket.SetParent(hand, true);
        }
        else
        {
            basket.SetParent(customer.transform, false);
            basket.localPosition = new Vector3(0.28f, 0.8f, 0f);
        }
        return basket;
    }

    /// <summary>Doc 6.1: the basket comes off the arm onto the counter, with the order's products standing in it.</summary>
    void PlaceBasket(CustomerCharacter customer, Order order)
    {
        if (customer.Basket != null) Destroy(customer.Basket.gameObject);
        customer.Basket = null;
        int items = ItemCount(order);
        float length = (items - 1) * ItemSpacing + 0.26f;
        CounterBasket = BuildBasket("Basket (on counter)", length, false);
        // Same row as the products, which stand where they always did; the floor sits on the counter top.
        CounterBasket.position = BasketAnchor.position + Vector3.right * ((items - 1) * ItemSpacing * 0.5f) + Vector3.up * BasketHeight;
        CounterBasket.rotation = Quaternion.Euler(0f, 90f, 0f);
    }

    /// <summary>Long side along local z. With a handle the pivot is the top of the handle, for the hand; without, the top of the rim.</summary>
    Transform BuildBasket(string name, float length, bool handle)
    {
        // A tote-style arch over the long side: posts on the end walls and a grip along the top.
        const float handleRise = 0.11f;
        float handleLength = length;
        var root = new GameObject(name).transform;
        float h = BasketHeight;
        float rim = handle ? -handleRise : 0f;
        Part(root, new Vector3(0f, rim - h + BasketWall * 0.5f, 0f), new Vector3(BasketWidth, BasketWall, length));
        Part(root, new Vector3(-BasketWidth * 0.5f, rim - h * 0.5f, 0f), new Vector3(BasketWall, h, length));
        Part(root, new Vector3(BasketWidth * 0.5f, rim - h * 0.5f, 0f), new Vector3(BasketWall, h, length));
        Part(root, new Vector3(0f, rim - h * 0.5f, -length * 0.5f), new Vector3(BasketWidth, h, BasketWall));
        Part(root, new Vector3(0f, rim - h * 0.5f, length * 0.5f), new Vector3(BasketWidth, h, BasketWall));
        if (handle)
        {
            Part(root, new Vector3(0f, rim * 0.5f, -handleLength * 0.5f), new Vector3(BasketWall, handleRise, BasketWall));
            Part(root, new Vector3(0f, rim * 0.5f, handleLength * 0.5f), new Vector3(BasketWall, handleRise, BasketWall));
            Part(root, Vector3.zero, new Vector3(BasketWall * 1.5f, BasketWall * 1.5f, handleLength));
        }
        return root;
    }

    void Part(Transform parent, Vector3 position, Vector3 size)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(part.GetComponent<Collider>());
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = size;
        part.layer = gameObject.layer;
        var renderer = part.GetComponent<Renderer>();
        if (BasketMaterial) renderer.sharedMaterial = BasketMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// <summary>Without the character prefabs the old primitive body still walks the same NavMesh routes.</summary>
    CustomerCharacter CreatePlaceholder()
    {
        var root = new GameObject("Customer (placeholder body)");
        root.SetActive(false);
        root.transform.SetPositionAndRotation(DoorEntry, Door.rotation);
        var torso = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        torso.transform.SetParent(root.transform, false);
        torso.transform.localPosition = Vector3.up * 0.85f;
        torso.transform.localScale = new Vector3(0.42f, 0.85f, 0.3f);
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = Vector3.up * 1.62f;
        head.transform.localScale = Vector3.one * 0.24f;
        foreach (var part in new[] { torso, head })
        {
            Destroy(part.GetComponent<Collider>());
            if (BodyMaterial) part.GetComponent<Renderer>().sharedMaterial = BodyMaterial;
        }
        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = 0.25f;
        agent.height = 1.7f;
        agent.speed = 1.1f;
        var customer = root.AddComponent<CustomerCharacter>();
        root.SetActive(true);
        return customer;
    }

    void RefreshHud()
    {
        if (Pos.Current == null || hud == null) return;
        var order = Pos.Current;
        SetHudVisible(true);
        hudTitle.text = hurried ? $"손님 {CustomerNumber}   서둘러 주세요!" : $"손님 {CustomerNumber}";
        hudTitle.color = hurried ? WorldUi.Palette.Warn : WorldUi.Palette.Text;
        var text = new StringBuilder();
        foreach (var line in Pos.Lines)
            text.AppendLine($"{(line.Scanned == line.Ordered ? "✓" : "○")}  {WorldUi.Clean(line.Data.Name)}  {line.Scanned}/{line.Ordered}");
        hud.text = text.ToString();
        hudPayText.text = order.Payment == PaymentMethod.Card ? "카드" : "현금";
        hudBagText.text = order.NeedsBag ? (Pos.AllBagged ? "봉투 ✓" : "봉투") : "봉투 없음";
        hudBag.color = order.NeedsBag ? (Pos.AllBagged ? WorldUi.Palette.Good : WorldUi.Palette.Warn) : new Color(1f, 1f, 1f, 0.15f);
        float share = order.TimeLimitSeconds > 0f ? Mathf.Clamp01(remaining / order.TimeLimitSeconds) : 0f;
        hudTime.fillAmount = share;
        hudTime.color = share > 0.5f ? WorldUi.Palette.Good : share > 0.25f ? WorldUi.Palette.Warn : WorldUi.Palette.Bad;
    }

    /// <summary>Between customers the card shows only its title line (the day's message).</summary>
    void SetHudVisible(bool order)
    {
        if (hudPay == null) return;
        hudPay.gameObject.SetActive(order);
        hudBag.gameObject.SetActive(order);
        hudTime.transform.parent.GetChild(hudTime.transform.GetSiblingIndex() - 1).gameObject.SetActive(order);
        hudTime.gameObject.SetActive(order);
        if (!order) hudTitle.text = "";
    }
}
