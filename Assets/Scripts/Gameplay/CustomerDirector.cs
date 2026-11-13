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
/// products are made then too: the customer sets them straight down on the counter, as at any convenience store
/// (2026-11-10; before that they came in a basket). One or two browsing-only customers walk between the shelves and leave
/// without ordering.
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
    const float AmbientFirstLookMin = 7f, AmbientFirstLookMax = 14f;
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
    // Goods set down by hand: each turned a little, the row slanting a little, one landing after the other. The slant runs
    // toward the clerk at the end by the customer's tray and toward the customer at the end by the card terminal, so the
    // widest product turned the most still clears both.
    const float SetDownYawDegrees = 7f;
    const float SetDownSlant = 0.02f, SetDownJitter = 0.008f;
    const float SetDownReach = 0.09f, SetDownRise = 0.06f;
    const float SetDownSeconds = 0.24f, SetDownLaterSeconds = 0.08f;
    // Set down by the customer's own hand (2026-11-11), one thing after another. Each is taken up at the customer's left
    // side, low enough that the counter's edge hides it from the clerk, lifted over the edge and stood on the counter.
    // The hand goes to the first in HandToFirstSeconds and back for each next one in HandBackSeconds; the lift takes
    // HandPlaceSeconds; what stands farther than the arm reaches is pushed the rest of the way in HandPushSeconds.
    const float HandToFirstSeconds = 0.22f, HandBackSeconds = 0.14f, HandPlaceSeconds = 0.42f, HandPushSeconds = 0.2f;
    // Where a thing waits and is taken up, from the customer's feet: to their left, toward the counter, its top this far under the counter top.
    const float TakeUpLeft = 0.2f, TakeUpForward = 0.22f, TakeUpBelowCounter = 0.04f;
    // The wrist is this far above the thing's top and behind its back, the fingers coming down over its top edge.
    const float WristAboveTop = 0.015f, WristBehind = 0.09f;
    // Customers stand back from the counter and to one side of where their goods go. Before the first thing they take a
    // step up to it: this far toward the clerk and this far to their left, in this time. The row of goods runs on to
    // their left, so for each thing farther along they move over a little more: until the thing is StepBeside to the left
    // of them, but no farther than StepUpLeftMost from their spot: the head of the line stands 65 cm to that side and
    // 15 cm farther back. For a thing that is still farther along they also come right up to the counter (StepUpForwardMost,
    // which leaves 15 cm between its face and the middle of the body). A step longer than StepUpStride takes longer in proportion.
    const float StepUpForward = 0.13f, StepUpForwardMost = 0.2f, StepUpLeft = 0.12f, StepUpLeftMost = 0.35f, StepBeside = 0.3f, StepUpSeconds = 0.25f, StepUpStride = 0.15f;
    // A card or a note is flat. The hand comes over it from the customer's side with the fingers nearly level: the wrist
    // this far above where it lies and this far behind its near edge, so the fingertips are on it and the thumb under it.
    // The plate is straight in front of the spot the customer walked to, to the right of all their goods: they lay it
    // down from in front of the plate, or from no more than PlateFromFarther to the left of that.
    const float FlatWristAbove = 0.045f, FlatWristBehind = 0.115f, PlateFromFarther = 0.04f, OverThePlate = 0.05f, HandOffSeconds = 0.12f;
    // How far the hand closes (CustomerArm.Hold: fingers, thumb) over the top of a thing it stands on the counter, on a
    // card or a note, and on the handles of the bag it carries out.
    const float HoldGoods = 0.5f, HoldGoodsThumb = 0.5f, HoldFlat = 0.2f, HoldFlatThumb = 0.9f, HoldBag = 0.8f;
    // Taking the change off the tray with the right hand (2026-11-12). The tray lies to the customer's right: they step
    // over until it is StepBeside to their right, at most TrayStepMost: 20 cm would have them straight behind the
    // scanner's upright part, hidden from the clerk as customers were before the counter was laid out anew. The hand comes over the counter's edge TrayOverEdge above the tray, down onto the change,
    // closes on it (the pieces drawn in under the palm, to within TrayGather of where they lay), and goes back down to the
    // customer's side, where the change is gone from sight.
    const float TrayStepMost = 0.12f, TrayOverEdge = 0.1f, TrayOverSeconds = 0.24f, TrayDownSeconds = 0.2f, TrayCloseSeconds = 0.16f, TrayAwaySeconds = 0.36f;
    const float TrayGather = 0.25f, HoldChange = 0.6f, HoldChangeThumb = 0.7f;
    // The card back (2026-11-12): once paid, the customer waits this long for it to lie on their plate again, within
    // CardBackWithin of the plate's middle and still for CardBackStill, and then takes it as they take change.
    const float CardBackSeconds = 5f, CardBackWithin = 0.12f, CardBackStill = 0.2f;
    // The bag off the counter (2026-11-12). It stands well in from the customer's edge, to their right, between the
    // POS screen and the scanner: they step over as for the change and right up to the counter, and the right hand
    // comes down on the handles (BagHandlesUp above the bag's foot) from BagOverHandles above them, closes, and draws
    // the bag up a little and over the counter to its edge (CounterEdgeFromSpot from where a customer stands; the
    // wrist BagOverEdge above the counter there, so the slack bag's foot is still on it), while the customer steps
    // back to their spot. Past the edge the bag hangs free and the hand lowers it in front of them (BagHeldForward
    // from the spot, BagHeldAbove the counter's height), where the left hand takes the handles: the right is the one
    // that waves. The left then carries it out to their left side (BagAsideLeft, BagAsideForward, BagAsideBelow the counter's height).
    // A short arm was 3 to 7 cm short of the middle of the handles from the 12 cm to the side that the change is taken
    // from (2026-11-12): for the bag they step BagStepMost, and the hand takes the handles a little toward where it
    // comes from (BagHandlesNearer toward the customer, BagHandlesAside toward their left).
    const float BagStepMost = 0.18f, BagHandlesNearer = 0.03f, BagHandlesAside = 0.04f;
    const float BagHandlesUp = 0.33f, BagOverHandles = 0.07f, BagOverSeconds = 0.28f, BagDownSeconds = 0.2f, BagCloseSeconds = 0.14f;
    const float CounterEdgeFromSpot = 0.38f, BagOverEdge = 0.42f, BagEdgeForward = 0.22f, BagPullSeconds = 0.5f;
    const float BagHeldForward = 0.2f, BagHeldAbove = 0.18f, BagLowerSeconds = 0.26f;
    const float BagPassSeconds = 0.24f, BagPassApart = 0.025f, BagChangeHandsSeconds = 0.12f;
    const float BagAsideLeft = 0.27f, BagAsideForward = 0.08f, BagAsideBelow = 0.05f, BagAsideSeconds = 0.3f, BagTurnSeconds = 0.35f;
    // What still stands out of reach (the third thing of three) is stood short of its place, by no more than this, and pushed.
    const float LongestPush = 0.25f;
    // Goods bought without a bag, off the counter by the customer's own hand (2026-11-13): until then they were gone
    // the moment the sale was done. The left hand, as it set them down, and the other way round: over the thing from
    // the customer's side (GoodsOverTop above where the wrist will be), down onto its top, closed, up over the
    // counter's edge and down at the customer's left side, where the edge hides it and it is gone. The last thing is
    // not put away: it stays in the hand, which carries it out at the customer's side (GoodsAsideLeft, GoodsAsideForward
    // from their spot, the wrist GoodsAsideBelow the counter top), hanging from the fingers once the arm is let go.
    // They begin with the thing farthest to their right (where the money was handled) and go on to their left,
    // standing so that each is in front of the left shoulder (ShoulderBeside from the middle of the body), no farther
    // to the right than GoodsStepRightMost: behind the scanner's upright part they would be hidden.
    // A thing the wrist does not get to comes to the hand over the counter, by no more than LongestDraw: the push of
    // setting down, the other way round. What is farther than that, or behind the scanner's upright part as the arm
    // would have to go (ScannerClear round it), or not standing on the counter at all, is gone as everything was before.
    const float ShoulderBeside = 0.18f, GoodsStepRightMost = 0.12f, GoodsOverTop = 0.07f;
    const float GoodsOverSeconds = 0.22f, GoodsDownSeconds = 0.16f, GoodsCloseSeconds = 0.12f, GoodsDrawSeconds = 0.2f, GoodsAwaySeconds = 0.42f;
    const float LongestDraw = 0.25f, ScannerClear = 0.06f;
    // Standing on the counter: its foot within this of the counter top, and moving no faster than this.
    const float GoodsOnCounterWithin = 0.09f, GoodsStillBelow = 0.3f;
    // Put away at the customer's side, its far face is this far short of the counter's edge.
    const float GoodsShortOfEdge = 0.03f;
    const float GoodsAsideLeft = 0.25f, GoodsAsideForward = 0.1f, GoodsAsideBelow = -0.03f, GoodsHangSeconds = 0.3f, GoodsFadeSeconds = 0.2f;
    // Putting a thing away at their side they stand back to this far in front of their spot. Right up at the counter
    // (StepUpForwardMost) the place for it short of the counter's edge is beside and behind the hip, lower than a short
    // arm goes, and the body bent all the way forward after a wrist it could not get to: 16 to 18 cm off (2026-11-13).
    const float GoodsAwayForward = 0.06f;

    // How the day rewards speed (game design 11, 2026-11-09, [제안] values for the 11/15 playtest). Finishing before the
    // customer's half-time hurry is what counts as fast: full satisfaction and a tip. Later the satisfaction slides down.
    public const float FastShare = 0.5f;
    // The customer hurries the clerk when this share of their time is left, and never sooner than HurryNotBefore after
    // they got to the counter. It was at half time (2026-11-13, first session on the headset: "they say it takes too
    // long much too soon"); and someone who had waited long in line arrived with up to 40% of their time gone, and
    // hurried the clerk a few seconds after arriving. [제안]
    public const float HurryShare = 0.3f, HurryNotBefore = 15f;
    const float LowestPaidSatisfaction = 0.4f;
    const float TipRate = 0.1f;
    const int TipStep = 100;
    // Days 2 and 3: time in line beyond the grace comes off the customer's limit, at this rate, up to this share of it. So
    // slow service at the counter arrives as impatience from the people behind, which is what makes the peak pile up.
    public const float DefaultLineGrace = 45f;
    /// <summary>Not a constant: store music (an equipment unlock) makes the line more patient.</summary>
    public static float LineGraceSeconds = DefaultLineGrace;
    public const float LinePenaltyPerSecond = 0.5f;
    public const float MaxLinePenaltyShare = 0.4f;
    const float OutcomeSeconds = 1.8f;
    const float OutcomeRise = 0.12f;
    const float OutcomeMetersPerPixel = 0.0007f;

    public POSController Pos;
    public PaymentDesk Desk;
    public GameAudio Audio;
    /// <summary>The bag a customer walks out with: a pattern kept switched off in the scene, copied into the hand of whoever had goods bagged.</summary>
    public CarriedBag BagToCarry;
    /// <summary>
    /// The film of the bag standing on the counter. It is not drawn from when a customer's hand closes on its handles
    /// until that customer has the bag at their side: then a new one stands there. Found by name when the scene was
    /// built before this field existed.
    /// </summary>
    public Renderer BagOnCounter;
    public Transform Door, Counter;
    /// <summary>On the counter top, where the first thing a customer buys is set down. The rest follow along +x.</summary>
    public Transform GoodsAnchor;
    /// <summary>Where the "+3,300원 · 팁 300원" card appears when a customer is done.</summary>
    public Transform OutcomeAnchor;
    /// <summary>Front of the line first. Each marker's forward is the way the customer faces there.</summary>
    public Transform[] QueueSlots = new Transform[0];
    /// <summary>Spots in front of shelves. Each marker's forward looks at the shelf.</summary>
    public Transform[] BrowseSpots = new Transform[0];
    public CustomerCharacter[] Characters = new CustomerCharacter[0];
    public Material BodyMaterial;
    public bool QueueAndBrowsers = true;
    public int BrowserTarget = 2;
    /// <summary>Doc 6.3: on Days 2 and 3 the day ends at once when this many customers have left.</summary>
    public int LeaveLimit = 3;
    /// <summary>
    /// The days of learning in a store that is kept (docs/p2/해금_성장_기획_2026-11-10.md 3): nobody leaves and a long line
    /// costs nothing, on any day. Running out of time only sounds the warning, as on the fixed Day 1.
    /// </summary>
    public bool Lenient;

    public int Paid { get; private set; }
    public int Sales { get; private set; }
    public int Left { get; private set; }
    public int CustomerNumber { get; private set; }
    /// <summary>The day ended because LeaveLimit customers left (Days 2 and 3 only).</summary>
    public bool EndedEarly { get; private set; }
    public int Hurries { get; private set; }
    /// <summary>The day's tips and satisfaction added over customers (each 0 to 1; one who left counts 0).</summary>
    public int Tips { get; private set; }
    public float SatisfactionTotal { get; private set; }
    /// <summary>The last outcome card's words, for the checks.</summary>
    public string LastOutcome { get; private set; } = "";
    public event Action Hurried;
    /// <summary>Times a shelf spot was passed over because the clerk stood at it.</summary>
    public int SpotsSkippedForClerk { get; private set; }
    public int Bells { get; private set; }
    public bool HudFlashing => Time.time < flashUntil;
    public string HudText => hud != null ? hud.text : "";
    public event Action<Order, bool> OrderEnded;
    /// <summary>
    /// A customer has reached the counter and the basket is about to be laid out: the last moment the order can still
    /// change (GameManager leaves out what was not on the shelf yet).
    /// </summary>
    public event Action<Order> OrderReachedCounter;
    /// <summary>A customer stands at the counter with an order. The bool: its special request has not come up before.</summary>
    public event Action<Order, bool> OrderArrived;
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
    /// <summary>In the store before the shift (see StartAmbient). Not in Present.</summary>
    public readonly List<CustomerCharacter> Ambient = new List<CustomerCharacter>();
    /// <summary>The manager's body: nobody shopping should be her twin.</summary>
    public string ReservedAvatar { get; set; } = "";
    public int MaxPresent { get; private set; }
    public int MaxWaitingSeen { get; private set; }
    public int MaxLineSeen { get; private set; }
    public int MaxForOrdersInStore { get; private set; }
    public int MaxBrowsersSeen { get; private set; }
    public int MaxAtCounterSeen { get; private set; }
    public int BrowsersSpawned { get; private set; }
    /// <summary>Everything the customer at the counter is buying stands on it and can be picked up.</summary>
    public bool GoodsSetDown => settingDown == 0;
    int settingDown;
    // Of those, the customer's card or note on its way to the plate. It waits for the goods.
    int handingOver;
    /// <summary>Cards and notes a customer's hand has laid on the plate, and how many of those it had to slide the last of the way. For the checks.</summary>
    public int PaymentsByHand { get; private set; }
    public int PaymentsSlid { get; private set; }
    /// <summary>A card or a note is in a customer's hand right now, on its way to the plate.</summary>
    public bool PaymentInHand { get; private set; }
    // Change let go of by the desk for a customer to take, by whom; and all of it that is still lying about.
    readonly Dictionary<CustomerCharacter, List<PaymentProp>> changeFor = new Dictionary<CustomerCharacter, List<PaymentProp>>();
    readonly List<PaymentProp> looseChange = new List<PaymentProp>();
    /// <summary>Times a customer's hand has taken the change off the tray, and the pieces it took. For the checks.</summary>
    public int ChangeTakenByHand { get; private set; }
    public int PiecesTakenByHand { get; private set; }
    /// <summary>The customer's hand is closed on the change right now.</summary>
    public bool ChangeInHand { get; private set; }
    /// <summary>The farthest that hand's wrist was from the change when it closed on it, in metres.</summary>
    public float FarthestFromChange { get; private set; }
    // Cards paid with and not yet back with their customers, by whom; and all of them that are still about.
    readonly Dictionary<CustomerCharacter, PaymentProp> cardFor = new Dictionary<CustomerCharacter, PaymentProp>();
    readonly List<PaymentProp> looseCards = new List<PaymentProp>();
    /// <summary>Cards customers took back off the plate by hand, and cards that were not given back in time. For the checks.</summary>
    public int CardsTakenBack { get; private set; }
    public int CardsNotReturned { get; private set; }
    /// <summary>A paid customer is standing at the counter waiting for their card.</summary>
    public bool WaitingForCard { get; private set; }
    /// <summary>The customer's hand is closed on their card right now.</summary>
    public bool CardInHand { get; private set; }
    public float FarthestFromCard { get; private set; }
    // What went into the counter's bag for a paid customer and waits there for them to lift it, by whom; and all of it still about.
    readonly Dictionary<CustomerCharacter, List<ProductPrefab>> bagFor = new Dictionary<CustomerCharacter, List<ProductPrefab>>();
    readonly List<ProductPrefab> looseBagGoods = new List<ProductPrefab>();
    /// <summary>Paid-for goods standing in the counter's bag until their customer lifts it. For the checks.</summary>
    public int GoodsWaitingInBag
    {
        get
        {
            int waiting = 0;
            foreach (var product in looseBagGoods)
                if (product) waiting++;
            return waiting;
        }
    }
    /// <summary>Bags customers lifted off the counter by hand, and the goods that were in them. For the checks.</summary>
    public int BagsLiftedByHand { get; private set; }
    public int GoodsLiftedInBags { get; private set; }
    /// <summary>A customer's right hand is closed on the handles right now: from the counter until the left hand has it.</summary>
    public bool BagInHand { get; private set; }
    /// <summary>The farthest that hand's wrist was from where the handles wanted it when it closed on them, in metres.</summary>
    public float FarthestFromBag { get; private set; }
    /// <summary>The least the bag was drawn of its length while a hand had it over the counter (CarriedBag.Stretch).</summary>
    public float ShortestBagSoFar { get; private set; } = 1f;
    // Goods bought without a bag that stand on the counter until their customer's hand takes them, by whom; all of
    // those still about; and the ones going out of the store in a hand.
    readonly Dictionary<CustomerCharacter, List<ProductPrefab>> goodsFor = new Dictionary<CustomerCharacter, List<ProductPrefab>>();
    readonly List<ProductPrefab> looseGoods = new List<ProductPrefab>();
    readonly List<ProductPrefab> goodsCarriedOut = new List<ProductPrefab>();
    readonly HashSet<CustomerCharacter> takingGoods = new HashSet<CustomerCharacter>();
    /// <summary>Paid-for goods standing loose on the counter (or in the hand that is taking them). For the checks.</summary>
    public int GoodsWaitingOnCounter => Alive(looseGoods);
    /// <summary>Everything paid for that is still drawn somewhere: in the counter's bag, loose on the counter, or in a customer's hand on the way out.</summary>
    public int PaidGoodsAbout => GoodsWaitingInBag + Alive(looseGoods) + Alive(goodsCarriedOut);
    /// <summary>Things customers took off the counter by hand; of those, the ones that had to come to the hand first; the ones that went out in the hand; and the ones no hand could take, gone as before.</summary>
    public int GoodsTakenByHand { get; private set; }
    public int GoodsDrawnToHand { get; private set; }
    public int GoodsCarriedOut { get; private set; }
    public int GoodsNotTaken { get; private set; }
    /// <summary>The farthest a thing came over the counter to the hand so far, in metres.</summary>
    public float LongestDrawSoFar { get; private set; }
    /// <summary>A customer's left hand is closed on a thing right now, on its way off the counter.</summary>
    public bool GoodsInHand { get; private set; }
    /// <summary>This customer is taking their goods off the counter right now.</summary>
    public bool TakingGoods(CustomerCharacter customer) => customer != null && takingGoods.Contains(customer);
    Renderer scannerTower;
    bool scannerLookedFor;

    static int Alive(List<ProductPrefab> goods)
    {
        int alive = 0;
        foreach (var product in goods)
            if (product) alive++;
        return alive;
    }

    /// <summary>The counter's own bag is drawn (it is not while a customer is lifting theirs out of its place).</summary>
    public bool BagOnCounterShown
    {
        get
        {
            FindBagOnCounter();
            return BagOnCounter == null || BagOnCounter.enabled;
        }
    }
    // Whose turn it is among the goods a customer is setting down by hand.
    int setDownTurn;
    /// <summary>Things a customer's hand has stood on the counter, and how many of those it had to push the last of the way. For the checks.</summary>
    public int SetDownsByHand { get; private set; }
    public int PushedIntoPlace { get; private set; }
    /// <summary>The longest such push so far, in metres.</summary>
    public float LongestPushSoFar { get; private set; }

    Text hud, hudTitle, hudIdle, hudPayText, hudBagText, hudSpecialText;
    Image hudPay, hudBag, hudTime, hudSpecial;
    Image hudBackground, hudHalf;
    RectTransform outcomePanel;
    Text outcomeText;
    CanvasGroup outcomeGroup;
    Coroutine outcomeRun;
    float linePenalty;
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
    readonly HashSet<string> specialsSeen = new HashSet<string>();

    void Start()
    {
        // The order screen is one of the store's own screens, so it is laid out like the POS beside it: a navy header,
        // a light page, square corners.
        var panel = WorldUi.CreatePanel(transform, "Order HUD", transform.position, transform.rotation, new Vector2(620f, 470f), MetersPerPixel, WorldUi.Palette.Paper);
        WorldUi.Square(panel.GetComponent<Image>());
        WorldUi.Square(WorldUi.AddBox(panel, 0f, 0f, 620f, 64f, WorldUi.Palette.Navy));
        hudTitle = WorldUi.AddText(panel, 28f, 0f, 564f, 64f, 34, TextAnchor.MiddleLeft);
        WorldUi.Bold(hudTitle);
        hudIdle = WorldUi.AddText(panel, 28f, 0f, 564f, 64f, 26, TextAnchor.MiddleLeft);
        hudIdle.text = "주문 확인";
        hudIdle.color = WorldUi.Palette.TextDim;
        hud = WorldUi.AddText(panel, 28f, 84f, 564f, 190f, 34);
        hud.color = WorldUi.Palette.Ink;
        hudPay = WorldUi.AddBox(panel, 28f, 340f, 170f, 52f, WorldUi.Palette.Accent);
        hudPayText = WorldUi.AddText((RectTransform)hudPay.transform, 0f, 0f, 170f, 52f, 28, TextAnchor.MiddleCenter);
        WorldUi.Bold(hudPayText);
        hudBag = WorldUi.AddBox(panel, 212f, 340f, 200f, 52f, WorldUi.Palette.Band);
        hudBagText = WorldUi.AddText((RectTransform)hudBag.transform, 0f, 0f, 200f, 52f, 28, TextAnchor.MiddleCenter);
        hudBagText.color = WorldUi.Palette.Ink;
        WorldUi.Bold(hudBagText);
        WorldUi.Square(WorldUi.AddBox(panel, 28f, 414f, 564f, 22f, WorldUi.Palette.Rule));
        hudTime = WorldUi.AddBox(panel, 28f, 414f, 564f, 22f, WorldUi.Palette.Good);
        hudTime.type = Image.Type.Filled;
        hudTime.fillMethod = Image.FillMethod.Horizontal;
        hudTime.sprite = null;
        // Added after the time bar: SetHudVisible finds the bar's background as the sibling just before it.
        hudSpecial = WorldUi.AddBox(panel, 28f, 276f, 564f, 52f, WorldUi.Palette.Warn);
        hudSpecialText = WorldUi.AddText((RectTransform)hudSpecial.transform, 0f, 0f, 564f, 52f, 28, TextAnchor.MiddleCenter);
        hudSpecialText.color = WorldUi.Palette.Ink;
        WorldUi.Bold(hudSpecialText);
        // Half way along the bar: finish while the bar is past this mark and it is a tip.
        hudHalf = WorldUi.Square(WorldUi.AddBox(panel, 28f + 564f * FastShare - 2f, 408f, 4f, 34f, WorldUi.Palette.Ink));
        SetHudVisible(false);
        BuildOutcomeCard();
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
            hudBackground.color = HudFlashing && Mathf.Repeat(Time.time, 0.5f) < 0.25f ? new Color(1f, 0.82f, 0.36f) : hudColor;
    }

    /// <summary>Clears the day's numbers and records before the next day. Customers still walking out finish leaving.</summary>
    public void ResetDay()
    {
        Paid = Sales = Left = 0;
        CustomerNumber = 0;
        EndedEarly = false;
        Hurries = Bells = 0;
        Tips = 0;
        SatisfactionTotal = 0f;
        LastOutcome = "";
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

    /// <summary>Back to the title for the next person: the first-time guidance for the requests comes up again too.</summary>
    public void ResetRun()
    {
        ResetDay();
        specialsSeen.Clear();
        ClearAmbient();
    }

    // ---------- Before the shift: people already shopping, and the two the tutorial serves (2026-11-09) ----------

    /// <summary>
    /// People in the store when the player first looks in, so the glass door shows a store that is open and the tutorial's
    /// customers are people who were already there. Outside Present and Records: the shift's own numbers do not count them.
    /// </summary>
    public void StartAmbient(int count)
    {
        ClearAmbient();
        // In front of row B and at the fridges: the two browse spots that can be seen through the door.
        var opening = new[] { 2, 1 };
        for (int i = 0; i < count && i < opening.Length && opening[i] < BrowseSpots.Length; i++)
        {
            var spot = BrowseSpots[opening[i]];
            var guest = NewGuest(spot.position, spot.rotation);
            guest.State = CustomerState.Wandering;
            guest.Settle();
            guest.Resting = CustomerCharacter.BrowseState;
            reservedSpots[spot] = guest;
            lastSpot[guest] = spot;
            Ambient.Add(guest);
            behaviours[guest] = StartCoroutine(KeepShopping(guest));
        }
    }

    /// <summary>A while at a shelf, then on to another one, until called to the counter or sent home.</summary>
    IEnumerator KeepShopping(CustomerCharacter guest)
    {
        // Staggered, and long enough at first for the player to have looked in.
        yield return new WaitForSeconds(Random.Range(AmbientFirstLookMin, AmbientFirstLookMax));
        while (true)
        {
            guest.Resting = CustomerCharacter.IdleState;
            ReleaseSpot(guest);
            yield return Browse(guest, CustomerState.Wandering);
        }
    }

    /// <summary>The ones still shopping when the shift starts walk out; the shift brings its own.</summary>
    public void DismissAmbient()
    {
        foreach (var guest in Ambient)
        {
            if (guest == null) continue;
            StopBehaviour(guest);
            ReleaseSpot(guest);
            behaviours[guest] = StartCoroutine(LeaveStore(guest, false));
        }
        Ambient.Clear();
    }

    public void ClearAmbient()
    {
        foreach (var guest in Ambient)
        {
            if (guest == null) continue;
            StopBehaviour(guest);
            ReleaseSpot(guest);
            lastSpot.Remove(guest);
            Destroy(guest.gameObject);
        }
        Ambient.Clear();
    }

    /// <summary>The next person for the tutorial: someone already shopping, or else someone new from the door.</summary>
    public CustomerCharacter CallGuest()
    {
        CustomerCharacter guest;
        if (Ambient.Count > 0)
        {
            guest = Ambient[0];
            Ambient.RemoveAt(0);
            StopBehaviour(guest);
            ReleaseSpot(guest);
        }
        else
        {
            guest = NewGuest(DoorEntry, Door.rotation);
            if (Audio) Audio.PlayAt(StoreSound.Door, Door.position + Vector3.up * DoorChimeHeight);
        }
        guest.State = CustomerState.ToCounter;
        guest.Resting = CustomerCharacter.IdleState;
        guest.Agent.avoidancePriority = 30;
        return guest;
    }

    public IEnumerator BringGuest(CustomerCharacter guest)
    {
        yield return WalkToCounter(guest);
        yield return guest.Face(Counter.rotation);
        guest.State = CustomerState.AtCounter;
        guest.Resting = CustomerCharacter.IdleState;
    }

    /// <summary>
    /// One thing a customer is buying, set down on the bare counter. It stands with its front to the clerk, so the barcode
    /// still has to be found by turning it, but not on a ruled line: things put down by hand are each turned a little and
    /// stand a little nearer or farther. It comes down from the customer's side and can be picked up once it stands.
    /// </summary>
    /// <param name="index">Its place in the row.</param>
    /// <param name="count">How many the customer puts down in all.</param>
    /// <param name="by">Whose hand sets it down. Nobody: the customer at the counter.</param>
    public ProductPrefab PutOnCounter(ProductPrefab prefab, int index = 0, int count = 1, CustomerCharacter by = null)
    {
        // The same every time for the same place in the same order, so a run can be repeated.
        int place = (CustomerNumber * 16 + index) * 4;
        float along = count > 1 ? index / (count - 1f) : 0.5f;
        float depth = Mathf.Lerp(-SetDownSlant, SetDownSlant, along) + Spread(place, SetDownJitter);
        var at = GoodsAnchor.position + Vector3.right * (index * ItemSpacing) + Vector3.forward * depth + Vector3.up * (prefab.Proxy.size.y * 0.5f + SpawnClearance);
        // Front to the clerk.
        var product = Instantiate(prefab, at, Quaternion.Euler(0f, 180f + Spread(place + 1, SetDownYawDegrees), 0f));
        if (index == 0) setDownTurn = 0;
        StartCoroutine(SetDown(product, index, by != null ? by : AtCounter));
        return product;
    }

    /// <summary>
    /// A value between -range and range that looks unrelated to those of the numbers next to it. Seeded random numbers
    /// would not do: neighbouring seeds start out alike, and two things side by side came out turned the same way.
    /// </summary>
    static float Spread(int n, float range)
    {
        unchecked
        {
            uint x = (uint)n + 0x9e3779b9u;
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return ((x & 0xffffffu) / (float)0x1000000 * 2f - 1f) * range;
        }
    }

    /// <summary>
    /// Stands it on its place on the counter: by the customer's own hand when the customer has an arm to do it with,
    /// otherwise lowered from where the hands would be. Until it stands it cannot be taken.
    /// </summary>
    IEnumerator SetDown(ProductPrefab product, int index, CustomerCharacter by)
    {
        settingDown++;
        var at = product.transform.position;
        var turned = product.transform.rotation;
        product.Body.isKinematic = true;
        product.Grab.enabled = false;
        var arm = by != null ? by.Arm : null;
        if (arm != null)
        {
            yield return SetDownByHand(product, index, by, arm, at, turned);
            // The last of them down, and no card or note to lay down after them: the arm goes back to the customer's side.
            if (settingDown == 1 && arm != null) arm.LetGo();
        }
        else
        {
            var reach = Counter.position - at;
            reach.y = 0f;
            var from = at + reach.normalized * SetDownReach + Vector3.up * SetDownRise;
            product.TeleportTo(from, turned);
            // All on their way at once, each a little longer than the one before: they land one after another.
            float seconds = SetDownSeconds + index * SetDownLaterSeconds;
            for (float t = 0f; t < 1f && product != null; t += Time.deltaTime / seconds)
            {
                float eased = t * t * (3f - 2f * t);
                product.TeleportTo(Vector3.Lerp(from, at, eased), turned);
                yield return null;
            }
            if (product != null && Audio) Audio.PlayAt(StoreSound.SetDown, at);
        }
        settingDown--;
        if (product == null) yield break;
        product.TeleportTo(at, turned);
        product.Body.isKinematic = false;
        product.Grab.enabled = true;
    }

    /// <summary>
    /// One thing, when its turn comes: the customer steps up to the counter (before the first), the hand goes down to their
    /// side, comes up with the thing over the counter's edge and stands it on the counter. The row of goods runs off to
    /// the customer's left; what is still out of reach at its far end is stood as near as the arm gets and pushed into place.
    /// </summary>
    IEnumerator SetDownByHand(ProductPrefab product, int index, CustomerCharacter by, CustomerArm arm, Vector3 at, Quaternion turned)
    {
        // Waiting its turn: on its place already, not drawn.
        float opacity = product.Splat ? product.Splat.m_OpacityScale : 1f;
        if (product.Splat) product.Splat.m_OpacityScale = 0f;
        while (product != null && arm != null && setDownTurn != index) yield return null;
        if (product == null || arm == null)
        {
            if (product != null && product.Splat) product.Splat.m_OpacityScale = opacity;
            setDownTurn = Mathf.Max(setDownTurn, index + 1);
            yield break;
        }

        var body = by.transform;
        var toClerk = body.forward;
        // A step up to the counter before the first thing, and over to the left for the ones farther along the row.
        float toItsLeft = Vector3.Dot(at - by.Spot, -body.right) - StepBeside;
        var step = toClerk * (toItsLeft > StepUpLeftMost ? StepUpForwardMost : StepUpForward) - body.right * Mathf.Clamp(toItsLeft, StepUpLeft, StepUpLeftMost);
        yield return by.StepTo(step, StepSeconds(by, step));
        if (product == null || arm == null)
        {
            setDownTurn = Mathf.Max(setDownTurn, index + 1);
            yield break;
        }

        var size = Vector3.Scale(product.Proxy.size, product.transform.lossyScale);
        float half = size.y * 0.5f, top = GoodsAnchor.position.y;
        // Where the hand takes it up: at the customer's left side, low enough that the counter's edge hides it from the clerk.
        var takeUp = body.position - body.right * TakeUpLeft + toClerk * TakeUpForward;
        takeUp.y = top - TakeUpBelowCounter - half;
        // From the middle of the thing to the wrist that holds it, and how the hand lies on it.
        var hold = Vector3.up * (half + WristAboveTop) - toClerk * (size.z * 0.5f + WristBehind);
        var fingers = (toClerk + Vector3.down * 0.5f).normalized;
        var palm = (Vector3.down - toClerk * 0.5f).normalized;

        // The hand goes down to it; then it is in the hand.
        product.TeleportTo(takeUp, turned);
        arm.ReachFor(takeUp + hold, fingers, palm);
        yield return new WaitForSeconds(index == 0 ? HandToFirstSeconds : HandBackSeconds);
        if (product != null && product.Splat) product.Splat.m_OpacityScale = opacity;
        if (arm != null) arm.Hold(HoldGoods, HoldGoodsThumb);

        // Up over the edge and down onto the counter: on its place, or as near it as the arm reaches from the customer's side.
        var stand = at;
        if (arm != null)
        {
            var back = body.position - at;
            back.y = 0f;
            var nearest = at + Vector3.ClampMagnitude(back, LongestPush);
            stand = arm.Reachable(nearest + hold, at + hold) - hold;
        }
        var over = Vector3.Lerp(takeUp, stand, 0.45f);
        over.y = top + half * 2.5f + 0.05f;
        for (float t = 0f; t < 1f && product != null && arm != null; t += Time.deltaTime / HandPlaceSeconds)
        {
            float eased = t * t * (3f - 2f * t);
            var along = Vector3.Lerp(Vector3.Lerp(takeUp, over, eased), Vector3.Lerp(over, stand, eased), eased);
            product.TeleportTo(along, turned);
            arm.ReachFor(along + hold, fingers, palm);
            yield return null;
        }
        if (product != null)
        {
            product.TeleportTo(stand, turned);
            if (Audio) Audio.PlayAt(StoreSound.SetDown, stand);
            SetDownsByHand++;
            // Out of reach: a push along the counter the rest of the way, the hand staying where it stood the thing.
            if ((at - stand).sqrMagnitude > 0.01f * 0.01f)
            {
                PushedIntoPlace++;
                LongestPushSoFar = Mathf.Max(LongestPushSoFar, Vector3.Distance(at, stand));
                Debug.Log($"[Customers] {by.name} stood {product.Data.Id} {Vector3.Distance(at, stand):0.00} m short of its place and pushed it there. {(arm != null ? arm.Describe(at + hold) : "")}");
                for (float t = 0f; t < 1f && product != null; t += Time.deltaTime / HandPushSeconds)
                {
                    product.TeleportTo(Vector3.Lerp(stand, at, 1f - (1f - t) * (1f - t)), turned);
                    yield return null;
                }
            }
        }
        if (arm != null) arm.Open();
        setDownTurn = Mathf.Max(setDownTurn, index + 1);
    }

    /// <summary>
    /// The customer's card or note, made lying on the plate (PaymentDesk), is laid there by the customer's own hand
    /// instead, after their goods (2026-11-12). Until it lies there it is not seen and cannot be taken. A customer with
    /// no arm to do it with leaves it as it was made.
    /// </summary>
    public void PutOnPlate(PaymentProp payment, CustomerCharacter by)
    {
        if (payment == null || by == null || by.Arm == null) return;
        StartCoroutine(SetOnPlate(payment, by));
    }

    IEnumerator SetOnPlate(PaymentProp payment, CustomerCharacter by)
    {
        settingDown++;
        handingOver++;
        var at = payment.transform.position;
        var turned = payment.transform.rotation;
        var held = payment.GetComponent<Rigidbody>();
        var arm = by.Arm;
        // A body moved by hand cannot sweep (as in PaymentProp.Dock). Both are put back when it lies on the plate.
        var sweeping = held.collisionDetectionMode;
        held.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        held.isKinematic = true;
        payment.Grab.enabled = false;
        // Still on the customer.
        payment.gameObject.SetActive(false);
        while (payment != null && arm != null && settingDown > handingOver) yield return null;
        if (payment != null && arm != null) yield return PlateByHand(payment, held, by, arm, at, turned);
        PaymentInHand = false;
        settingDown--;
        handingOver--;
        if (arm != null)
        {
            arm.Open();
            if (settingDown == 0) arm.LetGo();
        }
        if (payment == null) yield break;
        payment.gameObject.SetActive(true);
        Carry(payment, held, at, turned);
        held.isKinematic = false;
        held.collisionDetectionMode = sweeping;
        payment.Grab.enabled = true;
    }

    /// <summary>How long a step to this far from the customer's spot takes from where they stand now.</summary>
    static float StepSeconds(CustomerCharacter by, Vector3 step) =>
        StepUpSeconds * Mathf.Max(1f, (by.Spot + step - by.transform.position).magnitude / StepUpStride);

    static void Carry(PaymentProp payment, Rigidbody held, Vector3 to, Quaternion turned)
    {
        held.position = to;
        held.rotation = turned;
        payment.transform.SetPositionAndRotation(to, turned);
    }

    /// <summary>
    /// The hand goes down to the customer's side, comes up over the counter's edge with the card or note between the
    /// fingers and the thumb, lays it on the plate and lets it go. What the arm cannot reach is laid as near as it gets
    /// and slid the rest of the way, like the goods.
    /// </summary>
    IEnumerator PlateByHand(PaymentProp payment, Rigidbody held, CustomerCharacter by, CustomerArm arm, Vector3 at, Quaternion turned)
    {
        var body = by.transform;
        var toClerk = body.forward;
        float left = Mathf.Clamp(Vector3.Dot(body.position - by.Spot, -body.right), 0f, PlateFromFarther);
        var step = toClerk * StepUpForward - body.right * left;
        var stands = by.Spot + step;

        var size = Vector3.Scale(payment.GetComponent<BoxCollider>().size, payment.transform.lossyScale);
        float deep = Mathf.Abs(Vector3.Dot(turned * Vector3.forward, toClerk)) * size.z + Mathf.Abs(Vector3.Dot(turned * Vector3.right, toClerk)) * size.x;
        var hold = Vector3.up * FlatWristAbove - toClerk * (deep * 0.5f + FlatWristBehind);
        var fingers = (toClerk + Vector3.down * 0.25f).normalized;
        var palm = (Vector3.down - toClerk * 0.25f).normalized;
        // Taken out at the customer's left side, below the counter's edge as the clerk sees it.
        var takeUp = stands - body.right * TakeUpLeft + toClerk * TakeUpForward;
        takeUp.y = GoodsAnchor.position.y - TakeUpBelowCounter - FlatWristAbove;

        // The hand goes down to it, while the customer steps to where they lay it from.
        bool fromTheGoods = arm.Reaching;
        float began = Time.time;
        arm.ReachFor(takeUp + hold, fingers, palm);
        yield return by.StepTo(step, StepSeconds(by, step));
        float still = (fromTheGoods ? HandBackSeconds : HandToFirstSeconds) - (Time.time - began);
        if (still > 0f) yield return new WaitForSeconds(still);
        if (payment == null || arm == null) yield break;

        payment.gameObject.SetActive(true);
        Carry(payment, held, takeUp, turned);
        arm.Hold(HoldFlat, HoldFlatThumb);
        PaymentInHand = true;

        var back = body.position - at;
        back.y = 0f;
        var stand = arm.Reachable(at + Vector3.ClampMagnitude(back, LongestPush) + hold, at + hold) - hold;
        var over = Vector3.Lerp(takeUp, stand, 0.45f);
        over.y = at.y + OverThePlate;
        for (float t = 0f; t < 1f && payment != null && arm != null; t += Time.deltaTime / HandPlaceSeconds)
        {
            float eased = t * t * (3f - 2f * t);
            var along = Vector3.Lerp(Vector3.Lerp(takeUp, over, eased), Vector3.Lerp(over, stand, eased), eased);
            Carry(payment, held, along, turned);
            arm.ReachFor(along + hold, fingers, palm);
            yield return null;
        }
        if (payment == null) yield break;
        Carry(payment, held, stand, turned);
        PaymentsByHand++;
        if ((at - stand).sqrMagnitude > 0.01f * 0.01f)
        {
            PaymentsSlid++;
            Debug.Log($"[Customers] {by.name} laid the {(payment.IsCard ? "card" : "note")} {Vector3.Distance(at, stand):0.00} m short of the plate and slid it there. {(arm != null ? arm.Describe(at + hold) : "")}");
            for (float t = 0f; t < 1f && payment != null; t += Time.deltaTime / HandPushSeconds)
            {
                Carry(payment, held, Vector3.Lerp(stand, at, 1f - (1f - t) * (1f - t)), turned);
                yield return null;
            }
        }
        // The fingers come off it before the hand goes.
        PaymentInHand = false;
        if (arm != null) arm.Open();
        yield return new WaitForSeconds(HandOffSeconds);
    }

    /// <summary>
    /// The change on the tray is this customer's to take when they leave: call it once they are paid, before
    /// PaymentDesk.EndPayment, which would clear it away. A customer with no arm to take it with leaves it to be cleared.
    /// </summary>
    public void LeaveChangeFor(CustomerCharacter customer)
    {
        if (customer == null || customer.RightArm == null) return;
        var change = Desk.ReleaseChange();
        if (change.Count == 0) return;
        changeFor[customer] = change;
        looseChange.AddRange(change);
    }

    /// <summary>Change nobody came to take (the customer was stopped on the way): gone before the next payment begins.</summary>
    void ClearLooseChange()
    {
        foreach (var piece in looseChange)
            if (piece) Destroy(piece.gameObject);
        looseChange.Clear();
        changeFor.Clear();
        ChangeInHand = false;
        // A card still out when the next customer pays is gone too; the one waiting for it then goes.
        foreach (var card in looseCards)
            if (card) Destroy(card.gameObject);
        looseCards.Clear();
        // And goods left in the bag by someone who never lifted it.
        foreach (var product in looseBagGoods)
            if (product) Destroy(product.gameObject);
        looseBagGoods.Clear();
        bagFor.Clear();
        BagInHand = false;
        ShowBagOnCounter(true);
        // And goods nobody came to take (what is in a hand on the way out goes with its customer).
        foreach (var product in looseGoods)
            if (product) Destroy(product.gameObject);
        looseGoods.Clear();
        goodsFor.Clear();
        goodsCarriedOut.RemoveAll(product => !product);
        GoodsInHand = false;
    }

    /// <summary>
    /// The card a customer paid with is theirs to take back when they leave: call it once they are paid, before
    /// PaymentDesk.EndPayment, which would remove it. It stays what it was, a card in the clerk's hand or in the terminal,
    /// until it lies on the plate again or the customer has waited long enough. Null: there was no card, or no arm to take it with.
    /// </summary>
    public PaymentProp LeaveCardFor(CustomerCharacter customer)
    {
        if (customer == null || customer.RightArm == null) return null;
        var card = Desk.ReleaseCard();
        if (card == null) return null;
        cardFor[customer] = card;
        looseCards.Add(card);
        return card;
    }

    /// <summary>Paid with, and not yet back with its customer or gone.</summary>
    public bool IsOut(PaymentProp card) => card != null && looseCards.Contains(card);

    /// <summary>In the clerk's hand, as opposed to lying somewhere or sitting in the terminal's slot (which also "holds" it).</summary>
    static bool InAHand(PaymentProp card)
    {
        foreach (var holder in card.Grab.interactorsSelecting)
            if (!(holder is UnityEngine.XR.Interaction.Toolkit.Interactors.XRSocketInteractor)) return true;
        return false;
    }

    /// <summary>Given back: lying still on the customer's plate, in nobody's hand and not in the terminal.</summary>
    public bool CardLiesOnPlate(PaymentProp card)
    {
        if (card == null || card.Grab.isSelected) return false;
        var from = card.transform.position - Desk.CustomerPlate.position;
        return new Vector2(from.x, from.z).magnitude < CardBackWithin && Mathf.Abs(from.y) < 0.05f && card.Speed < 0.1f;
    }

    /// <summary>
    /// The customer waits for their card to be laid on the plate, and takes it with the right hand. A card that does not
    /// come back in CardBackSeconds is gone as it used to be at once, and the customer goes without a word about it.
    /// </summary>
    IEnumerator TakeCardBack(CustomerCharacter by, PaymentProp card)
    {
        WaitingForCard = true;
        float waited = 0f, lying = 0f, inAll = 0f;
        // A card that is back while the customer is still taking their goods off the counter lies there until they are done.
        while (card != null && by != null && (lying < CardBackStill ? waited < CardBackSeconds && inAll < CardBackSeconds * 2.5f : TakingGoods(by)))
        {
            bool back = lying >= CardBackStill;
            lying = CardLiesOnPlate(card) ? lying + Time.deltaTime : 0f;
            if (!back)
            {
                // The clerk is bringing it: the customer does not walk off in the last second while it is on its way in a
                // hand. But not for ever either.
                if (!InAHand(card) || waited < CardBackSeconds - 1f) waited += Time.deltaTime;
                inAll += Time.deltaTime;
            }
            yield return null;
        }
        WaitingForCard = false;
        if (card == null) yield break;
        looseCards.Remove(card);
        if (by == null || lying < CardBackStill)
        {
            CardsNotReturned++;
            // In the log for whoever looks afterwards at how a session on the headset went.
            Debug.Log($"[Customers] {(by != null ? by.name : "A customer")} was not given the card back in {inAll:0.0} s and went without it.");
            Destroy(card.gameObject);
            yield break;
        }
        Debug.Log($"[Customers] {by.name} found the card back on the plate {inAll:0.0} s after paying and takes it.");
        // The customer's from here on.
        card.Grab.enabled = false;
        var held = card.GetComponent<Rigidbody>();
        held.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        held.isKinematic = true;
        foreach (var part in card.GetComponentsInChildren<Collider>()) part.enabled = false;
        var body = by.transform;
        float left = Mathf.Clamp(Vector3.Dot(body.position - by.Spot, -body.right), 0f, PlateFromFarther);
        yield return TakeByHand(by, new List<PaymentProp> { card }, body.forward * StepUpForward - body.right * left, PaymentDesk.CardSize.z * 0.5f, true);
        CardsTakenBack++;
    }

    IEnumerator TakeChange(CustomerCharacter by, List<PaymentProp> change)
    {
        var body = by.transform;
        var middle = Vector3.zero;
        int lying = 0;
        foreach (var piece in change)
        {
            if (!piece) continue;
            middle += piece.transform.position;
            lying++;
        }
        if (lying == 0) yield break;
        middle /= lying;
        // Over to the tray, as far as the right hand needs, and right up to the counter: from the 12 cm to the side that
        // keeps them out from behind the scanner, a short arm was still 5 cm short of the change.
        float toItsRight = Vector3.Dot(middle - by.Spot, body.right) - StepBeside;
        var step = body.forward * (toItsRight > TrayStepMost ? StepUpForwardMost : StepUpForward) + body.right * Mathf.Clamp(toItsRight, 0f, TrayStepMost);
        yield return TakeByHand(by, change, step, 0f, false);
        ChangeTakenByHand++;
        PiecesTakenByHand += lying;
    }

    /// <summary>
    /// The right hand takes what lies on the counter for this customer: over the counter's edge, down onto it, closed on
    /// it (the pieces drawn in under the palm), and back down to the customer's side, where it is gone from sight.
    /// </summary>
    /// <param name="step">Where they take it from, from the spot they walked to.</param>
    /// <param name="deep">Half of how far the thing reaches toward the customer from its middle: the wrist stays behind its near edge.</param>
    IEnumerator TakeByHand(CustomerCharacter by, List<PaymentProp> things, Vector3 step, float deep, bool card)
    {
        var arm = by.RightArm;
        var body = by.transform;
        var toClerk = body.forward;
        var middle = Vector3.zero;
        int lying = 0;
        foreach (var piece in things)
        {
            if (!piece) continue;
            middle += piece.transform.position;
            lying++;
        }
        if (lying == 0 || arm == null) yield break;
        middle /= lying;

        var stands = by.Spot + step;
        var hold = Vector3.up * FlatWristAbove - toClerk * (deep + FlatWristBehind);
        var fingers = (toClerk + Vector3.down * 0.25f).normalized;
        var palm = (Vector3.down - toClerk * 0.25f).normalized;
        // Beside the customer's right hip, below the counter's edge as the clerk sees it: where the hand comes from and goes back to.
        var beside = stands + body.right * TakeUpLeft + toClerk * TakeUpForward;
        beside.y = GoodsAnchor.position.y - TakeUpBelowCounter;

        yield return by.StepTo(step, StepSeconds(by, step));
        if (arm == null) yield break;
        // From where they stand now: what the arm reaches is asked of the shoulder as it is.
        yield return null;
        if (arm == null) yield break;
        var back = stands - middle;
        back.y = 0f;
        var on = arm.Reachable(middle + Vector3.ClampMagnitude(back, LongestPush) + hold, middle + hold);
        var over = Vector3.Lerp(beside, on, 0.6f);
        over.y = on.y + TrayOverEdge;

        // Up over the edge, then down onto it.
        arm.ReachFor(over, fingers, palm);
        yield return new WaitForSeconds(TrayOverSeconds);
        for (float t = 0f; t < 1f && arm != null; t += Time.deltaTime / TrayDownSeconds)
        {
            arm.ReachFor(Vector3.Lerp(over, on, Mathf.SmoothStep(0f, 1f, t)), fingers, palm);
            yield return null;
        }
        if (arm == null) yield break;
        arm.ReachFor(on, fingers, palm);
        float shortBy = Vector3.Distance(on, middle + hold);
        if (card) FarthestFromCard = Mathf.Max(FarthestFromCard, shortBy);
        else FarthestFromChange = Mathf.Max(FarthestFromChange, shortBy);

        // The hand closes, and what lies there is drawn in under it.
        if (card) arm.Hold(HoldFlat, HoldFlatThumb);
        else arm.Hold(HoldChange, HoldChangeThumb);
        if (card) CardInHand = true;
        else ChangeInHand = true;
        var under = on - hold;
        var lay = new List<Vector3>();
        foreach (var piece in things) lay.Add(piece ? piece.transform.position : under);
        bool rang = false;
        for (float t = 0f; t < 1f; t += Time.deltaTime / TrayCloseSeconds)
        {
            for (int i = 0; i < things.Count; i++)
            {
                if (!things[i]) continue;
                things[i].transform.position = Vector3.Lerp(lay[i], Vector3.Lerp(under, lay[i], TrayGather), Mathf.SmoothStep(0f, 1f, t));
                if (!rang && Audio && !card)
                {
                    rang = true;
                    Audio.PlayAt(things[i].IsCoin ? StoreSound.Coin : StoreSound.Note, under);
                }
            }
            yield return null;
        }

        // Away over the edge and down to the customer's side, the thing going with the hand.
        for (int i = 0; i < things.Count; i++)
            if (things[i]) lay[i] = things[i].transform.position - on;
        for (float t = 0f; t < 1f && arm != null; t += Time.deltaTime / TrayAwaySeconds)
        {
            float eased = t * t * (3f - 2f * t);
            var along = Vector3.Lerp(Vector3.Lerp(on, over, eased), Vector3.Lerp(over, beside + hold, eased), eased);
            arm.ReachFor(along, fingers, palm);
            for (int i = 0; i < things.Count; i++)
                if (things[i]) things[i].transform.position = along + lay[i];
            yield return null;
        }
        foreach (var piece in things)
        {
            looseChange.Remove(piece);
            if (piece) Destroy(piece.gameObject);
        }
        ChangeInHand = false;
        CardInHand = false;
        if (arm != null)
        {
            arm.Open();
            arm.LetGo();
        }
    }

    // ---------- Goods bought without a bag, taken off the counter by hand (2026-11-13) ----------

    /// <summary>The box a product fills as it stands or lies, level with the world: its collider's, which may be switched off.</summary>
    static Bounds BoxOf(ProductPrefab product)
    {
        var t = product.transform;
        var half = Vector3.Scale(product.Proxy.size, t.lossyScale) * 0.5f;
        Vector3 x = t.right * half.x, y = t.up * half.y, z = t.forward * half.z;
        var reach = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(t.TransformPoint(product.Proxy.center), reach * 2f);
    }

    /// <summary>On the counter top (or on the scanner's flat window), in nobody's hand and not on its way anywhere.</summary>
    bool StandsOnCounter(ProductPrefab product)
    {
        if (product.Grab.isSelected || product.IsBagged) return false;
        float foot = BoxOf(product).min.y - GoodsAnchor.position.y;
        if (foot < -0.02f || foot > GoodsOnCounterWithin) return false;
        return product.Body.isKinematic || product.Body.linearVelocity.magnitude < GoodsStillBelow;
    }

    /// <summary>The way from one point to the other goes through the scanner's upright part, or too near it for an arm.</summary>
    bool PastScanner(Vector3 from, Vector3 to)
    {
        if (!scannerLookedFor)
        {
            scannerLookedFor = true;
            var tower = GameObject.Find("Scanner Tower");
            if (tower != null) scannerTower = tower.GetComponent<Renderer>();
        }
        if (scannerTower == null) return false;
        var box = scannerTower.bounds;
        box.Expand(ScannerClear * 2f);
        if (box.Contains(from) || box.Contains(to)) return true;
        var way = to - from;
        return box.IntersectRay(new Ray(from, way.normalized), out float hit) && hit >= 0f && hit <= way.magnitude;
    }

    /// <summary>
    /// How this customer's left hand would take the thing: where they stand for it (from their spot), where the wrist
    /// wants to be on it, and where it gets to. False: the hand cannot take it, even with the thing coming LongestDraw
    /// of the way, or only through the scanner.
    /// </summary>
    bool PlanTake(CustomerCharacter by, ProductPrefab product, out Vector3 step, out Vector3 wanted, out Vector3 on)
    {
        var arm = by.Arm;
        var body = by.transform;
        var toClerk = body.forward;
        var spot = by.Spot;
        var box = BoxOf(product);
        float deep = Mathf.Abs(toClerk.x) * box.extents.x + Mathf.Abs(toClerk.z) * box.extents.z;
        wanted = box.center + Vector3.up * (box.extents.y + WristAboveTop) - toClerk * (deep + WristBehind);
        float toItsLeft = Vector3.Dot(box.center - spot, -body.right) - ShoulderBeside;
        // No nearer the counter than the thing asks for: right up to it only for what the wrist would not get to otherwise.
        step = Vector3.zero;
        on = wanted;
        Vector3 stepped = Vector3.zero;
        foreach (float forward in new[] { StepUpForward, StepUpForwardMost })
        {
            step = toClerk * forward - body.right * Mathf.Clamp(toItsLeft, -GoodsStepRightMost, StepUpLeftMost);
            // The body is not there yet: the shoulder will be this far from where it is now.
            stepped = spot + step - body.position;
            var back = spot + step - box.center;
            back.y = 0f;
            on = arm.Reachable(wanted + Vector3.ClampMagnitude(back, LongestDraw), wanted, stepped);
            if ((on - wanted).sqrMagnitude < 1e-6f) break;
        }
        if (!arm.Reaches(on, stepped)) return false;
        var shoulder = arm.BentShoulder + stepped;
        return !PastScanner(shoulder, on) && !PastScanner(shoulder, box.center);
    }

    /// <summary>
    /// Goods that stand loose on the counter when their customer has paid are that customer's to take by hand on the
    /// way out (2026-11-13). They stand where they are until then, and the clerk can no longer take them. What the
    /// hand could not take is gone now, as all of them were before; so are all of them for a customer with no arm.
    /// </summary>
    void LeaveGoodsFor(CustomerCharacter customer, List<ProductPrefab> goods)
    {
        var mine = new List<ProductPrefab>();
        foreach (var product in goods)
        {
            if (!product) continue;
            bool arm = customer != null && customer.Arm != null;
            if (arm && StandsOnCounter(product) && PlanTake(customer, product, out _, out _, out _))
            {
                product.Grab.enabled = false;
                product.Body.isKinematic = true;
                mine.Add(product);
                continue;
            }
            if (arm)
            {
                GoodsNotTaken++;
                Debug.Log($"[Customers] {customer.name} cannot take {product.Data.Id} at {product.transform.position.ToString("F2")} by hand ({(product.Grab.isSelected ? "in the clerk's hand" : !StandsOnCounter(product) ? "not standing on the counter" : "out of reach or behind the scanner")}): gone with the sale.");
            }
            Destroy(product.gameObject);
        }
        if (mine.Count == 0) return;
        if (goodsFor.TryGetValue(customer, out var already)) already.AddRange(mine);
        else goodsFor[customer] = mine;
        looseGoods.AddRange(mine);
    }

    /// <summary>A thing the hand turned out not to get to after all: it fades where it stands.</summary>
    IEnumerator FadeAway(ProductPrefab product, CustomerCharacter by)
    {
        GoodsNotTaken++;
        Debug.Log($"[Customers] {(by != null ? by.name : "A customer")} could not take {product.Data.Id} at {product.transform.position.ToString("F2")} after all: gone.");
        float opacity = product.Splat ? product.Splat.m_OpacityScale : 1f;
        for (float t = 0f; t < 1f && product != null; t += Time.deltaTime / GoodsFadeSeconds)
        {
            if (product.Splat) product.Splat.m_OpacityScale = opacity * (1f - t);
            yield return null;
        }
        looseGoods.Remove(product);
        if (product != null) Destroy(product.gameObject);
    }

    static IEnumerator StepThen(CustomerCharacter by, Vector3 step, float seconds, Action then)
    {
        yield return by.StepTo(step, seconds);
        then();
    }

    IEnumerator TakeGoods(CustomerCharacter by, List<ProductPrefab> goods, bool carryLast)
    {
        takingGoods.Add(by);
        yield return TakeOneByOne(by, goods, carryLast);
        takingGoods.Remove(by);
        GoodsInHand = false;
    }

    /// <summary>
    /// The customer takes what they bought off the counter with the left hand, one thing after another from their
    /// right to their left: over it, down on its top, closed, up over the counter's edge and down at their side, where
    /// it is gone. The last thing stays in the hand and goes out with them (unless a bag is to follow into that hand).
    /// </summary>
    IEnumerator TakeOneByOne(CustomerCharacter by, List<ProductPrefab> goods, bool carryLast)
    {
        var arm = by.Arm;
        var body = by.transform;
        var toClerk = body.forward;
        var toLeft = -body.right;
        float top = GoodsAnchor.position.y;
        // The hand lies over the thing's top from the customer's side, as it did setting it down.
        var fingers = (toClerk + Vector3.down * 0.5f).normalized;
        var palm = (Vector3.down - toClerk * 0.5f).normalized;
        goods.RemoveAll(product => !product);
        goods.Sort((a, b) => Vector3.Dot(a.transform.position - b.transform.position, toLeft).CompareTo(0f));

        for (int i = 0; i < goods.Count; i++)
        {
            var product = goods[i];
            if (by == null || arm == null) yield break;
            if (!product) continue;
            bool last = true;
            for (int next = i + 1; next < goods.Count; next++)
                if (goods[next]) last = false;

            // Over to it, the hand already on its way to above it.
            if (!PlanTake(by, product, out var step, out _, out var on))
            {
                yield return FadeAway(product, by);
                continue;
            }
            float began = Time.time;
            arm.Open();
            // The hand goes ahead of the step, as far toward the thing as the arm gets from where the body is.
            var ahead = on + Vector3.up * GoodsOverTop - toClerk * 0.04f;
            bool there = false;
            StartCoroutine(StepThen(by, step, StepSeconds(by, step), () => there = true));
            while (!there && by != null && arm != null)
            {
                var near = body.position + toLeft * TakeUpLeft + toClerk * 0.1f;
                near.y = ahead.y;
                arm.ReachFor(arm.Reachable(near, ahead), fingers, palm);
                yield return null;
            }
            // From where they stand now: what the arm reaches is asked of the shoulder as it is.
            yield return null;
            if (by == null || arm == null) yield break;
            if (!product) continue;
            if (!PlanTake(by, product, out _, out var wanted, out on))
            {
                yield return FadeAway(product, by);
                continue;
            }
            var over = on + Vector3.up * GoodsOverTop - toClerk * 0.04f;
            arm.ReachFor(over, fingers, palm);
            while (Time.time - began < GoodsOverSeconds) yield return null;
            for (float t = 0f; t < 1f && arm != null; t += Time.deltaTime / GoodsDownSeconds)
            {
                arm.ReachFor(Vector3.Lerp(over, on, Mathf.SmoothStep(0f, 1f, t)), fingers, palm);
                yield return null;
            }
            if (by == null || arm == null) yield break;
            if (!product) continue;
            arm.ReachFor(on, fingers, palm);

            // The hand closes on it. What it did not get to comes to it over the counter.
            arm.Hold(HoldGoods, HoldGoodsThumb);
            GoodsInHand = true;
            foreach (var part in product.GetComponentsInChildren<Collider>()) part.enabled = false;
            var box = BoxOf(product);
            var turned = product.transform.rotation;
            var stood = product.transform.position;
            float shortBy = Vector3.Distance(on, wanted);
            if (shortBy > 0.01f)
            {
                GoodsDrawnToHand++;
                LongestDrawSoFar = Mathf.Max(LongestDrawSoFar, shortBy);
                Debug.Log($"[Customers] {by.name}'s hand was {shortBy:0.00} m short of {product.Data.Id}, which came to it over the counter. {arm.Describe(wanted)}");
                for (float t = 0f; t < 1f && product != null; t += Time.deltaTime / GoodsDrawSeconds)
                {
                    product.TeleportTo(Vector3.Lerp(stood, stood + on - wanted, t * t * (3f - 2f * t)), turned);
                    yield return null;
                }
                if (product != null) product.TeleportTo(stood + on - wanted, turned);
            }
            else yield return new WaitForSeconds(GoodsCloseSeconds);
            if (by == null || arm == null) yield break;
            if (!product) continue;
            box = BoxOf(product);
            if (Audio) Audio.PlayTouch(TouchFeel.KindOf(product.transform) == TouchKind.Carton ? StoreSound.Carton : StoreSound.Crinkle, box.center, 0.5f);

            // In the hand from here: it goes where the wrist goes.
            var toWrist = on - box.center;
            var carried = CarriedGoods.Carry(by, product, box, toWrist);
            float deep = Mathf.Abs(toClerk.x) * box.extents.x + Mathf.Abs(toClerk.z) * box.extents.z;
            bool carry = last && carryLast;
            // Back from the counter with it: to the spot they walked to with the last one, and with the others far
            // enough for the hand to get down beside them.
            var backTo = Vector3.zero;
            if (!last)
            {
                var stepped = body.position - by.Spot;
                backTo = stepped - toClerk * Mathf.Max(0f, Vector3.Dot(stepped, toClerk) - GoodsAwayForward);
            }
            StartCoroutine(by.StepTo(backTo, GoodsAwaySeconds));
            var stands = by.Spot + backTo;
            // Where its middle goes: down at the left hip short of the counter's edge, its top under the counter top;
            // or, carried out, at the customer's side.
            Vector3 away;
            if (carry)
            {
                away = by.Spot + toLeft * GoodsAsideLeft + toClerk * GoodsAsideForward - toWrist;
                away.y = top - GoodsAsideBelow - toWrist.y;
            }
            else
            {
                float toEdge = CounterEdgeFromSpot - Vector3.Dot(stands - by.Spot, toClerk);
                away = stands + toLeft * TakeUpLeft + toClerk * (toEdge - deep - GoodsShortOfEdge);
                away.y = top - TakeUpBelowCounter - box.extents.y;
            }
            var from = box.center;
            var high = Vector3.Lerp(away, from, 0.45f);
            high.y = Mathf.Max(from.y, top + box.extents.y) + box.extents.y * 1.5f + 0.05f;
            for (float t = 0f; t < 1f && arm != null && product != null; t += Time.deltaTime / GoodsAwaySeconds)
            {
                float eased = t * t * (3f - 2f * t);
                var along = Vector3.Lerp(Vector3.Lerp(from, high, eased), Vector3.Lerp(high, away, eased), eased);
                arm.ReachFor(along + toWrist, fingers, palm);
                yield return null;
            }
            GoodsInHand = false;
            looseGoods.Remove(product);
            if (by == null || arm == null) yield break;
            GoodsTakenByHand++;
            if (!product) continue;
            if (carry)
            {
                // It stays in the hand, hanging from the fingers as the arm goes back to how the clip has it.
                arm.ReachFor(away + toWrist, fingers, palm);
                carried.Hang(GoodsHangSeconds);
                arm.LetGo();
                by.Goods = carried;
                goodsCarriedOut.Add(product);
                GoodsCarriedOut++;
                yield return new WaitForSeconds(GoodsHangSeconds);
                yield break;
            }
            Destroy(product.gameObject);
            arm.Open();
        }
        if (arm != null)
        {
            arm.Open();
            arm.LetGo();
        }
    }

    /// <summary>The line and the mouth moving with it. Returns its length.</summary>
    public float GuestSays(CustomerCharacter guest, CustomerLine line, int variant = 0) => Audio ? Speak(guest, line, variant) : 0f;

    /// <summary>
    /// What went into the counter's bag is this customer's to lift off the counter when they leave (2026-11-12): call
    /// it once they are paid, with the goods that stand in the bag, which then stay there until the hand closes on the
    /// handles. A customer with no arms to lift it with has it in the hand at once, as everyone had before.
    /// </summary>
    void LeaveBagFor(CustomerCharacter customer, List<ProductPrefab> goods)
    {
        if (BagToCarry == null || customer == null || customer.Bag != null || bagFor.ContainsKey(customer)) return;
        if (customer.Arm == null || customer.RightArm == null)
        {
            foreach (var product in goods)
                if (product) Destroy(product.gameObject);
            HandOverBag(customer);
            return;
        }
        bagFor[customer] = goods;
        looseBagGoods.AddRange(goods);
    }

    /// <summary>The bag, in the left hand at once, the hand below the counter's edge as the clerk sees it.</summary>
    void HandOverBag(CustomerCharacter customer)
    {
        if (BagToCarry == null || customer.Bag != null) return;
        var bag = Instantiate(BagToCarry, customer.transform);
        bag.name = "Bag (carried)";
        bag.Hang(customer);
        customer.Bag = bag;
        // The left hand, the one the bag hangs from, closes on its handles.
        if (customer.Arm != null) customer.Arm.Hold(HoldBag, HoldBag);
    }

    void FindBagOnCounter()
    {
        if (BagOnCounter != null) return;
        var film = GameObject.Find("Plastic Bag");
        if (film != null) BagOnCounter = film.GetComponent<Renderer>();
    }

    void ShowBagOnCounter(bool shown)
    {
        FindBagOnCounter();
        if (BagOnCounter != null) BagOnCounter.enabled = shown;
    }

    /// <summary>
    /// The customer takes their bag off the counter: the right hand on its handles, the bag drawn over the counter to
    /// the edge and off it, and into the left hand, which carries it out. From the moment the hand closes, the bag that
    /// stood on the counter with the goods in it is the one in the hand; a new one stands there when they are done.
    /// </summary>
    IEnumerator TakeBag(CustomerCharacter by, List<ProductPrefab> goods)
    {
        var right = by.RightArm;
        var left = by.Arm;
        var body = by.transform;
        var toClerk = body.forward;
        var toRight = body.right;
        // The foot of the counter's bag (the pattern is kept under it).
        var stood = BagToCarry.transform.parent;
        float top = stood.position.y;
        var handles = stood.position + Vector3.up * BagHandlesUp - toClerk * BagHandlesNearer - toRight * BagHandlesAside;
        var spot = by.Spot;

        float toItsRight = Vector3.Dot(handles - spot, toRight) - StepBeside;
        var step = toClerk * StepUpForwardMost + toRight * Mathf.Clamp(toItsRight, 0f, BagStepMost);
        // The hand comes down on the handles from above, the fingers hooked under them, the palm toward the customer.
        var fingers = (Vector3.down + toClerk * 0.5f).normalized;
        var palm = (Vector3.down * 0.5f - toClerk).normalized;
        // From where the handles are held to the wrist (CarriedBag.Grip, the other way round).
        var hold = by.RightKnuckles != null
            ? Vector3.up * 0.03f - fingers * Vector3.Distance(by.RightHand.position, by.RightKnuckles.position)
            : Vector3.up * 0.09f;

        yield return by.StepTo(step, StepSeconds(by, step));
        // From where they stand now: what the arm reaches is asked of the shoulder as it is.
        yield return null;
        if (right == null || left == null || by == null) yield break;
        var back = spot + step - handles;
        back.y = 0f;
        var on = right.Reachable(handles + Vector3.ClampMagnitude(back, LongestPush) + hold, handles + hold);
        var over = on + Vector3.up * BagOverHandles - toClerk * 0.05f;
        right.ReachFor(over, fingers, palm);
        yield return new WaitForSeconds(BagOverSeconds);
        for (float t = 0f; t < 1f && right != null; t += Time.deltaTime / BagDownSeconds)
        {
            right.ReachFor(Vector3.Lerp(over, on, Mathf.SmoothStep(0f, 1f, t)), fingers, palm);
            yield return null;
        }
        if (right == null) yield break;
        right.ReachFor(on, fingers, palm);
        float shortBy = Vector3.Distance(on, handles + hold);
        FarthestFromBag = Mathf.Max(FarthestFromBag, shortBy);
        if (shortBy > 0.01f) Debug.Log($"[Customers] {by.name}'s hand closed {shortBy:0.00} m short of the bag's handles. {right.Describe(handles + hold)}");
        right.Hold(HoldBag, HoldBag);
        yield return new WaitForSeconds(BagCloseSeconds);
        if (right == null) yield break;

        // The bag is in the hand from here: what stood on the counter is not drawn, and its goods are inside this one.
        int inside = 0;
        foreach (var product in goods)
        {
            looseBagGoods.Remove(product);
            if (!product) continue;
            inside++;
            Destroy(product.gameObject);
        }
        var bag = Instantiate(BagToCarry, body);
        bag.name = "Bag (carried)";
        bag.Hang(by, true);
        var edge = spot + toClerk * CounterEdgeFromSpot;
        edge.y = top;
        bag.StandOn(top, edge, -toClerk, stood.eulerAngles.y);
        by.Bag = bag;
        ShowBagOnCounter(false);
        BagInHand = true;
        GoodsLiftedInBags += inside;
        if (Audio) Audio.PlayAt(StoreSound.Bag, handles);

        // Up a little and over the counter to its edge, the customer stepping back to make room for it.
        var atEdge = spot + toClerk * BagEdgeForward;
        atEdge.y = top + BagOverEdge;
        // Toward the customer first, between the POS screen and the scanner's upright part, and then along the edge
        // to in front of them: straight across, the bag's corner went through the back of the scanner.
        var rise = on + Vector3.Project(atEdge - on, toClerk) * 0.75f + Vector3.Project(atEdge - on, toRight) * 0.25f;
        rise.y = Mathf.Max(on.y, atEdge.y) + 0.06f;
        StartCoroutine(by.StepTo(Vector3.zero, BagPullSeconds));
        for (float t = 0f; t < 1f && right != null; t += Time.deltaTime / BagPullSeconds)
        {
            float eased = t * t * (3f - 2f * t);
            right.ReachFor(Vector3.Lerp(Vector3.Lerp(on, rise, eased), Vector3.Lerp(rise, atEdge, eased), eased), fingers, palm);
            if (bag != null) ShortestBagSoFar = Mathf.Min(ShortestBagSoFar, bag.Stretch);
            yield return null;
        }
        // Off the edge, where it hangs free, and down in front of them.
        var held = spot + toClerk * BagHeldForward;
        held.y = top + BagHeldAbove;
        for (float t = 0f; t < 1f && right != null; t += Time.deltaTime / BagLowerSeconds)
        {
            right.ReachFor(Vector3.Lerp(atEdge, held, Mathf.SmoothStep(0f, 1f, t)), fingers, palm);
            yield return null;
        }
        if (right == null || left == null || bag == null) yield break;
        right.ReachFor(held, fingers, palm);

        // Into the left hand, which comes to the handles from its own side.
        var leftFingers = (Vector3.down + toClerk * 0.3f).normalized;
        var leftHold = by.LeftKnuckles != null
            ? Vector3.up * 0.03f - leftFingers * Vector3.Distance(by.LeftHand.position, by.LeftKnuckles.position)
            : Vector3.up * 0.09f;
        for (float t = 0f; t < 1f && left != null && bag != null; t += Time.deltaTime / BagPassSeconds)
        {
            left.ReachFor(bag.Grip() - toRight * BagPassApart + leftHold, leftFingers, toRight);
            yield return null;
        }
        if (right == null || left == null || bag == null) yield break;
        var taken = bag.Grip() - toRight * BagPassApart;
        left.ReachFor(taken + leftHold, leftFingers, toRight);
        left.Hold(HoldBag, HoldBag);
        yield return new WaitForSeconds(BagChangeHandsSeconds);
        if (right == null || left == null || bag == null) yield break;
        bag.Hang(by);
        BagInHand = false;
        right.Open();
        right.LetGo();
        bag.TurnAlongLeg(BagTurnSeconds);

        // Out to their left side, in front of the leg and not through it, and from there the arm hangs as the clip has it.
        var aside = spot - toRight * BagAsideLeft + toClerk * BagAsideForward;
        aside.y = top - BagAsideBelow;
        for (float t = 0f; t < 1f && left != null; t += Time.deltaTime / BagAsideSeconds)
        {
            left.ReachFor(Vector3.Lerp(taken, aside, Mathf.SmoothStep(0f, 1f, t)) + leftHold, leftFingers, toRight);
            yield return null;
        }
        if (left != null) left.LetGo();
        BagsLiftedByHand++;
        // A new bag on the counter for whoever comes next.
        ShowBagOnCounter(true);
    }

    /// <summary>
    /// The end of a served guest: the money on the card, thanks, a nod, a wave, and out, with the bag if something was
    /// bagged (<paramref name="inBag"/>: what stands in the counter's bag, which the guest lifts off the counter).
    /// </summary>
    public void GuestLeaves(CustomerCharacter guest, int amount, bool withBag = false, ProductPrefab inBag = null)
    {
        if (withBag)
        {
            var goods = new List<ProductPrefab>();
            if (inBag != null) goods.Add(inBag);
            LeaveBagFor(guest, goods);
        }
        else if (inBag != null) LeaveGoodsFor(guest, new List<ProductPrefab> { inBag });
        ShowOutcome($"+{amount:N0}원", WorldUi.Palette.Good);
        if (Audio) Speak(guest, CustomerLine.Thanks);
        if (guest.FaceRig != null)
        {
            guest.FaceRig.SetMood(CustomerMood.Pleased);
            guest.FaceRig.Nod();
        }
        behaviours[guest] = StartCoroutine(LeaveStore(guest, true));
    }

    CustomerCharacter NewGuest(Vector3 position, Quaternion rotation)
    {
        spawnedTotal++;
        var prefab = PickCharacter();
        string avatar = prefab != null ? prefab.name : "placeholder";
        CustomerCharacter guest;
        if (prefab != null)
        {
            guest = Instantiate(prefab, position, rotation);
        }
        else
        {
            guest = CreatePlaceholder();
            guest.Agent.Warp(position);
            guest.transform.rotation = rotation;
        }
        guest.name = $"Guest {spawnedTotal} ({avatar})";
        guest.Agent.avoidancePriority = 60 + spawnedTotal % 10;
        guest.Record.Name = guest.name;
        guest.Record.Avatar = avatar;
        return guest;
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

    /// <summary>
    /// To the spot in front of the counter. Whoever was served before may still be standing on it, taking their change
    /// or waving, and a walk that finds it taken ends short of it: 44 cm short once (2026-11-12), and that guest paid
    /// from there, the hand nowhere near the plate. So the rest of the way is walked once the spot is free.
    /// </summary>
    IEnumerator WalkToCounter(CustomerCharacter customer)
    {
        yield return customer.WalkTo(Counter.position);
        for (int again = 0; again < CounterRetries && customer != null && Vector3.Distance(customer.transform.position, Counter.position) > CounterWithin; again++)
        {
            yield return new WaitForSeconds(CounterRetrySeconds);
            if (customer == null) yield break;
            CounterRetriesSoFar++;
            yield return customer.WalkTo(Counter.position);
        }
    }

    // Enough tries to outlast a customer who waits for their card, takes it and waves.
    // (And, since the same day, lifts their bag off the counter.)
    const int CounterRetries = 20;
    const float CounterWithin = 0.2f, CounterRetrySeconds = 0.6f;
    /// <summary>Times a customer walked the rest of the way to the counter after finding the spot taken. For the checks.</summary>
    public int CounterRetriesSoFar { get; private set; }

    IEnumerator RunOrder(Order order, CustomerCharacter customer)
    {
        yield return WalkToCounter(customer);
        yield return customer.Face(Counter.rotation);
        customer.State = CustomerState.AtCounter;
        customer.Resting = CustomerCharacter.IdleState;

        // Arrived (doc 6.2): the goods go down on the counter (made now), HUD, then the timer. Whoever listens may still
        // take a line off the order before its goods are made (a new product not yet on its shelf).
        OrderReachedCounter?.Invoke(order);
        SpawnProducts(order);
        Pos.BeginOrder(order);
        BeginPayment(order, customer);
        linePenalty = order.Day > 1 && !Lenient && customer.Record.QueuedAt >= 0f ? LinePenalty(customer.Record.CalledAt - customer.Record.QueuedAt, order.TimeLimitSeconds) : 0f;
        customer.Record.LinePenalty = linePenalty;
        if (customer.FaceRig != null) customer.FaceRig.SetMood(linePenalty > 0f ? CustomerMood.Impatient : CustomerMood.Neutral);
        remaining = order.TimeLimitSeconds - linePenalty;
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
        bool firstOfItsKind = order.Special != Order.None && specialsSeen.Add(order.Special);
        if (Audio) StartCoroutine(SpeakOnArrival(customer, order, firstOfItsKind));
        RefreshHud();
        OrderArrived?.Invoke(order, firstOfItsKind);

        while (!orderPaid)
        {
            remaining -= Time.deltaTime;
            if (order.Day > 1 && !hurried && remaining <= order.TimeLimitSeconds * HurryShare && Time.time - customer.Record.ArrivedAt >= HurryNotBefore)
            {
                // Game design 8: the customer hurries the clerk (at half time until 2026-11-13).
                hurried = true;
                Hurries++;
                customer.Record.HurriedAt = Time.time;
                if (Audio)
                {
                    Audio.PlayAt(StoreSound.Hurry, Mouth(customer));
                    StartCoroutine(SayLater(customer, CustomerLine.Hurry, HurryVoiceDelay));
                }
                flashUntil = Time.time + 1.5f;
                Hurried?.Invoke();
                if (customer.FaceRig != null)
                {
                    customer.FaceRig.SetMood(CustomerMood.Impatient);
                    customer.FaceRig.ShakeHead(0.6f);
                }
            }
            RefreshHud();
            if (remaining <= 0f)
            {
                // Day 1 only warns (doc 6.3); later days lose the customer.
                if (order.Day > 1 && !Lenient) break;
                if (!warned)
                {
                    warned = true;
                    if (Audio) Audio.PlayError();
                }
            }
            yield return null;
        }

        float shareLeft = order.TimeLimitSeconds > 0f ? Mathf.Clamp01(remaining / order.TimeLimitSeconds) : 0f;
        customer.Record.ShareLeft = shareLeft;
        if (orderPaid)
        {
            int total = Pos.Total;
            Paid++;
            Sales += total;
            bool fast = shareLeft >= FastShare;
            int tip = fast ? TipFor(total) : 0;
            float satisfaction = SatisfactionFor(shareLeft);
            Tips += tip;
            SatisfactionTotal += satisfaction;
            customer.Record.Tip = tip;
            customer.Record.Satisfaction = satisfaction;
            ShowOutcome(fast ? $"+{total:N0}원  ·  팁 {tip:N0}원" : $"+{total:N0}원", fast ? WorldUi.Palette.Good : WorldUi.Palette.Text);
            if (Audio)
            {
                // Just thanks, however fast it went: people at a counter do not remark on the speed (2026-11-13, from
                // the first session on the headset). The tip and its sound are what say it was fast.
                Speak(customer, CustomerLine.Thanks);
                if (fast) Audio.PlayAt(StoreSound.Tip, Mouth(customer));
            }
            if (customer.FaceRig != null)
            {
                customer.FaceRig.SetMood(fast ? CustomerMood.Pleased : CustomerMood.Neutral);
                customer.FaceRig.Nod();
            }
        }
        else
        {
            Left++;
            customer.Record.TimedOut = true;
            ShowOutcome($"손님이 떠났습니다  ·  이탈 {Left}/{LeaveLimit}", WorldUi.Palette.Bad);
            if (Audio) Speak(customer, CustomerLine.Leave);
            if (customer.FaceRig != null)
            {
                customer.FaceRig.SetMood(CustomerMood.Angry);
                customer.FaceRig.ShakeHead();
            }
        }
        customer.Record.DoneAt = Time.time;
        if (orderPaid)
        {
            LeaveChangeFor(customer);
            LeaveCardFor(customer);
            if (order.NeedsBag)
            {
                // What stands in the bag stays there until the customer lifts the bag; the rest goes as before.
                var bagged = OrderProducts.FindAll(product => product && product.IsBagged);
                OrderProducts.RemoveAll(product => product && product.IsBagged);
                LeaveBagFor(customer, bagged);
            }
            // What stands loose on the counter is theirs to take by hand; what no hand can get at goes as it always did.
            var loose = OrderProducts.FindAll(product => product && !product.IsBagged);
            OrderProducts.RemoveAll(product => loose.Contains(product));
            LeaveGoodsFor(customer, loose);
        }
        Desk.EndPayment();
        Pos.EndOrder();
        foreach (var product in OrderProducts)
            if (product) Destroy(product.gameObject);
        OrderProducts.Clear();
        hud.text = "";
        SetHudVisible(false);
        AtCounter = null;
        behaviours[customer] = StartCoroutine(LeaveStore(customer, orderPaid));
        OrderEnded?.Invoke(order, orderPaid);
    }

    // ---------- What a customer is worth (game design 11) ----------

    /// <summary>Full when paid before the half-time hurry, then down to 0.4 as the time runs out.</summary>
    public static float SatisfactionFor(float shareLeft) => shareLeft >= FastShare ? 1f : Mathf.Lerp(LowestPaidSatisfaction, 1f, Mathf.Clamp01(shareLeft) / FastShare);

    /// <summary>A tenth of the bill to the nearest 100 won, never less than 100 won.</summary>
    public static int TipFor(int total) => Mathf.Max(TipStep, Mathf.RoundToInt(total * TipRate / TipStep) * TipStep);

    public static float LinePenalty(float waitedInLine, float timeLimit) =>
        Mathf.Clamp((waitedInLine - LineGraceSeconds) * LinePenaltyPerSecond, 0f, timeLimit * MaxLinePenaltyShare);

    void BuildOutcomeCard()
    {
        if (OutcomeAnchor == null) return;
        outcomePanel = WorldUi.CreatePanel(transform, "Outcome Card", OutcomeAnchor.position, OutcomeAnchor.rotation, new Vector2(620f, 120f), OutcomeMetersPerPixel, WorldUi.Palette.Panel);
        outcomeText = WorldUi.AddText(outcomePanel, 10f, 0f, 600f, 120f, 46, TextAnchor.MiddleCenter);
        WorldUi.Bold(outcomeText);
        outcomeGroup = outcomePanel.gameObject.AddComponent<CanvasGroup>();
        outcomePanel.gameObject.SetActive(false);
    }

    void ShowOutcome(string text, Color color)
    {
        LastOutcome = text;
        if (outcomePanel == null) return;
        outcomeText.text = text;
        outcomeText.color = color;
        if (outcomeRun != null) StopCoroutine(outcomeRun);
        outcomeRun = StartCoroutine(FloatOutcome());
    }

    /// <summary>Rises a little over the counter and fades out, so it is seen at once and gone before the next customer.</summary>
    IEnumerator FloatOutcome()
    {
        outcomePanel.gameObject.SetActive(true);
        var start = OutcomeAnchor.position;
        float elapsed = 0f;
        while (elapsed < OutcomeSeconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / OutcomeSeconds);
            outcomePanel.position = start + Vector3.up * (OutcomeRise * k);
            outcomeGroup.alpha = k < 0.7f ? 1f : Mathf.InverseLerp(1f, 0.7f, k);
            yield return null;
        }
        outcomePanel.gameObject.SetActive(false);
        outcomeRun = null;
    }

    // ---------- Voices (doc 6.1) ----------

    static Vector3 Mouth(CustomerCharacter customer) => customer.transform.position + Vector3.up * MouthHeight;

    /// <summary>
    /// Greeting, then the order's special request, then the bag request, each after the last one finished. The first time a
    /// request comes up the narrator then says what to do about it (game design 10.3).
    /// </summary>
    IEnumerator SpeakOnArrival(CustomerCharacter customer, Order order, bool firstOfItsKind)
    {
        yield return new WaitForSeconds(order.Day > 1 ? GreetAfterBellSeconds : GreetDelaySeconds);
        if (AtCounter != customer) yield break;
        float talking = linePenalty > 0f ? Speak(customer, CustomerLine.Waited) : Speak(customer, CustomerLine.Greet, order.Sequence);
        var requests = new List<CustomerLine>();
        switch (order.Special)
        {
            case Order.Microwave: requests.Add(CustomerLine.Microwave); break;
            case Order.HotWater: requests.Add(CustomerLine.HotWater); break;
            case Order.OnePlus: requests.Add(CustomerLine.OnePlus); break;
        }
        if (order.NeedsBag) requests.Add(CustomerLine.Bag);
        foreach (var line in requests)
        {
            yield return new WaitForSeconds(talking + LinePauseSeconds);
            if (AtCounter != customer) yield break;
            talking = Speak(customer, line);
        }
        if (!firstOfItsKind) yield break;
        yield return new WaitForSeconds(talking + LinePauseSeconds);
        if (AtCounter == customer) Audio.NarrateSpecial(order.Special);
    }

    IEnumerator SayLater(CustomerCharacter customer, CustomerLine line, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (customer != null && AtCounter == customer) Speak(customer, line);
    }

    /// <summary>The line from the customer's mouth, and the mouth moving with it. Returns its length, for what follows.</summary>
    float Speak(CustomerCharacter customer, CustomerLine line, int variant = 0)
    {
        bool female = GameAudio.IsFemale(customer.Record.Avatar);
        float seconds = Audio.Say(line, female, Mouth(customer), variant);
        if (customer.FaceRig != null) customer.FaceRig.Talk(Audio.EnvelopeOf(line, female, variant), seconds);
        return seconds;
    }

    void BeginPayment(Order order, CustomerCharacter customer)
    {
        ClearLooseChange();
        Desk.NotReadyNotice = () => Pos.NotReadyNotice;
        if (order.Payment == PaymentMethod.Card)
        {
            Desk.BeginCard(() => Pos.ReadyForPayment, Pos.SetNotice, Pos.CompleteCard);
        }
        else
        {
            Desk.BeginCash(PaymentDesk.BillFor(order.TotalPrice), () => Pos.ReadyForPayment, () => Pos.Total, Pos.ReceiveCash, Pos.SetNotice, Pos.CompleteCash);
        }
        PutOnPlate(order.Payment == PaymentMethod.Card ? Desk.CustomerCard : Desk.CustomerBill, customer);
    }

    /// <summary>A short line on the order card between customers, with the blink (doc 5.7.2: the mid-day delivery).</summary>
    public void Announce(string text)
    {
        flashUntil = Time.time + 2f;
        if (Pos.Current == null && hud != null) hud.text = text;
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
        int index = 0, count = ItemCount(order);
        foreach (var line in order.Lines)
            for (int i = 0; i < line.Quantity; i++)
                OrderProducts.Add(PutOnCounter(line.Product, index++, count));
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
        bool fedUp = false;
        while (true)
        {
            int slot = Line.IndexOf(customer);
            var spot = QueueSlots[slot];
            customer.Record.QueueSlotsTaken++;
            yield return customer.WalkTo(spot.position);
            yield return customer.Face(spot.rotation);
            while (Line.IndexOf(customer) == slot)
            {
                // Past the grace the wait starts to cost time at the counter (LinePenalty), and it shows on the face first.
                if (!fedUp && !Lenient && dayOrders.Count > 0 && dayOrders[0].Day > 1 && Time.time - customer.Record.QueuedAt > LineGraceSeconds)
                {
                    fedUp = true;
                    if (customer.FaceRig != null)
                    {
                        customer.FaceRig.SetMood(CustomerMood.Impatient);
                        customer.FaceRig.ShakeHead(0.5f);
                    }
                }
                yield return null;
            }
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
        // The change first, by hand; then the wave, which is the same hand's.
        if (changeFor.TryGetValue(customer, out var change))
        {
            changeFor.Remove(customer);
            yield return TakeChange(customer, change);
        }
        // What they bought without a bag, with the left hand: while the clerk still has their card, if they paid with one.
        if (goodsFor.TryGetValue(customer, out var loose))
        {
            goodsFor.Remove(customer);
            StartCoroutine(TakeGoods(customer, loose, !bagFor.ContainsKey(customer)));
        }
        if (cardFor.TryGetValue(customer, out var card))
        {
            cardFor.Remove(customer);
            yield return TakeCardBack(customer, card);
        }
        while (TakingGoods(customer)) yield return null;
        // Then the bag, off the counter and into the left hand.
        if (bagFor.TryGetValue(customer, out var bagged))
        {
            bagFor.Remove(customer);
            yield return TakeBag(customer, bagged);
        }
        // No wave (2026-11-13): a customer who raised a hand high and waved after every sale was not like anyone at a
        // real counter. They have said thanks and nodded when the sale was done; with their things in hand they go.
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

    // ---------- Characters ----------

    // Door faces into the store, so its right is the right-hand side of someone coming in.
    Vector3 DoorEntry => Door.position + Door.right * DoorLane;
    public Vector3 DoorExit => Door.position - Door.right * DoorLane;

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
            int count = candidate.name == ReservedAvatar ? 1 : 0;
            foreach (var present in Present)
                if (present != null && present.Record.Avatar == candidate.name) count++;
            foreach (var guest in Ambient)
                if (guest != null && guest.Record.Avatar == candidate.name) count++;
            if (count < fewest)
            {
                fewest = count;
                best.Clear();
            }
            if (count == fewest) best.Add(candidate);
        }
        return best.Count > 0 ? best[Random.Range(0, best.Count)] : null;
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
        hudTitle.text = hurried ? $"손님 {CustomerNumber}   서둘러 주세요!" : linePenalty > 0f ? $"손님 {CustomerNumber}   오래 기다렸어요" : $"손님 {CustomerNumber}";
        hudTitle.color = hurried || linePenalty > 0f ? WorldUi.Palette.Warn : WorldUi.Palette.Text;
        var text = new StringBuilder();
        foreach (var line in Pos.Lines)
            text.AppendLine($"{(Pos.LineDone(line) ? "✓" : "○")}  {WorldUi.Clean(line.Data.Name)}  {line.Scanned}/{line.Ordered}");
        hud.text = text.ToString();
        RefreshSpecial(order);
        hudPayText.text = order.Payment == PaymentMethod.Card ? "카드" : "현금";
        hudBagText.text = order.NeedsBag ? (Pos.AllBagged ? "봉투 ✓" : "봉투") : "봉투 없음";
        hudBag.color = order.NeedsBag ? (Pos.AllBagged ? WorldUi.Palette.Good : WorldUi.Palette.Warn) : WorldUi.Palette.Band;
        float share = order.TimeLimitSeconds > 0f ? Mathf.Clamp01(remaining / order.TimeLimitSeconds) : 0f;
        hudTime.fillAmount = share;
        hudTime.color = share > 0.5f ? WorldUi.Palette.Good : share > 0.25f ? WorldUi.Palette.Warn : WorldUi.Palette.Bad;
    }

    /// <summary>Doc 6.4: the request in words on the order card; green once it is done (1+1 needs no doing).</summary>
    void RefreshSpecial(Order order)
    {
        bool any = order.Special != Order.None;
        hudSpecial.gameObject.SetActive(any);
        if (!any) return;
        bool done = order.NeedsCooking && Pos.AllCooked;
        hudSpecialText.text = order.Special == Order.Microwave ? (done ? "전자레인지 데우기 ✓" : "전자레인지 데우기")
            : order.Special == Order.HotWater ? (done ? "온수 붓기 ✓" : "온수 붓기")
            : "1+1 행사 · 1개 값만 받기";
        bool onePlus = !done && order.Special == Order.OnePlus;
        hudSpecial.color = done ? WorldUi.Palette.Good : onePlus ? WorldUi.Palette.Accent : WorldUi.Palette.Warn;
        hudSpecialText.color = onePlus ? WorldUi.Palette.Text : WorldUi.Palette.Ink;
    }

    /// <summary>Between customers the card shows only its title line (the day's message).</summary>
    void SetHudVisible(bool order)
    {
        if (hudPay == null) return;
        if (!order) hudSpecial.gameObject.SetActive(false);
        hudPay.gameObject.SetActive(order);
        hudBag.gameObject.SetActive(order);
        hudTime.transform.parent.GetChild(hudTime.transform.GetSiblingIndex() - 1).gameObject.SetActive(order);
        hudTime.gameObject.SetActive(order);
        if (hudHalf != null) hudHalf.gameObject.SetActive(order);
        if (!order) hudTitle.text = "";
        // With no order the header names the screen instead.
        hudIdle.gameObject.SetActive(!order);
    }
}
