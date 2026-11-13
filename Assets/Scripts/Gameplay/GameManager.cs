using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public enum DayState { Intro, Tutorial, Shift, Settlement, DayEnd, Title, Commute, PunchIn, Ending }

/// <summary>One day's settlement, kept for the report after the last day (doc 6.5).</summary>
public class DayResult
{
    public int Day, Sales, PossibleSales, Paid, Orders, Left, Cancels;
    public int Shelved, Delivered, Disposed, Expired, Tips, TillShort;
    public float Score, Satisfaction;
    public string Grade;
}

/// <summary>
/// Runs the days: Intro, the Day 1 tutorial or the Day 2 intake tutorial, Shift, then Settlement (doc 6.2, game design 11).
/// After a day's settlement a "N일차 시작" button under the board starts the next day (user decision 2026-11-08: a button
/// pressed with the ray and trigger, like the POS [-]); after the last day the board adds the report of all days.
/// Around the days runs the title, the walk in and the punch-in, then the punch-out, the ending and back to the title for the
/// next person (TitleFlow, 2026-11-09). -start-day 2 starts at Day 2 without them, and so does every automatic check.
/// </summary>
public class GameManager : MonoBehaviour
{
    const float IntroSeconds = 3f;
    const float BoardMetersPerPixel = 0.0009f;
    const float BoardWidth = 1500f, BoardHeight = 220f, ReportBoardHeight = 560f;
    // Doc 5.7.4: the first customer comes when the intake tutorial is done, or this long after it began.
    public const float IntakeTutorialMaxSeconds = 90f;
    const int IntakeTutorialDay = 2;
    const float BriefSeconds = 7f;
    const float RequestGuideSeconds = 12f;
    const int AmbientCustomers = 2;
    static readonly string[] NoTitleArguments = { "-skip-title", "-start-day", "-p0-verify", "-p1-verify", "-p2-verify", "-p2-face-verify", "-p2-day2-verify", "-p2-locomotion-verify", "-p2-load", "-hmd-perf", "-hmd-guard-check" };

    // ---------- A store that is kept and grows (docs/p2/해금_성장_기획_2026-11-10.md). All numbers are [제안]. ----------
    /// <summary>The days of learning. From the next one the customers follow from what the store sells.</summary>
    public const int TrainingDays = 3;
    /// <summary>The share of a day's sales that comes back as budget for the ordering tablet; tips are added whole.</summary>
    public const float BudgetShare = 0.2f;
    public const int GraduationGrant = 4000;
    public const float NewScannerAngle = 65f, MusicLineGrace = 65f;
    /// <summary>
    /// Products ordered in come as boxes to shelve, two a day at most: the cart holds two, and more would be more shelving
    /// than a shift with customers has room for. What is left waits for the next morning. [제안]
    /// </summary>
    public const int NewBoxesPerDay = 2;
    /// <summary>
    /// The looks of the equipment that only changes a number: groups under "Equipment Looks" (and a child of each cutter)
    /// that the scene builder makes switched off and OpenStore switches on.
    /// </summary>
    public const string EquipmentLooks = "Equipment Looks", ScannerLook = "Scanner Upgrade", CoinLook = "Coin Dispenser", MusicLook = "Music Speakers", CutterLook = "New Model";
    public const int NewCutterCheckpoints = 4;
    const float LeaveSeconds = 4f;
    const int SampleSpots = 2;
    const float SampleSpacing = 0.22f;
    // With these the game is the fixed three days it was, the whole store stocked and nothing saved: the checks written
    // for them go through the title. Those that skip the title (NoTitleArguments) are the fixed days anyway.
    static readonly string[] FixedDaysArguments = { "-fixed-days", "-p2-title-verify", "-p2-intro-verify" };

    public int Day = 1;
    public int LastDay = 3;
    public TextAsset Scenario;
    /// <summary>More rows of the same schema (doc 6.5), for later days.</summary>
    public TextAsset[] MoreScenarios = new TextAsset[0];
    public ProductCatalog Catalog;
    public POSController Pos;
    public CustomerDirector Director;
    public TutorialController Tutorial;
    public IntakeDirector Intake;
    public IntakeTutorial IntakeTutorial;
    public TitleFlow Title;
    public ManagerNpc Manager;
    public bool RunTutorial = true;
    public Transform BoardAnchor;
    /// <summary>Data/unlocks.csv: what the ordering tablet offers.</summary>
    public TextAsset Unlocks;
    public OrderTablet Tablet;
    /// <summary>Data/training_scenarios.csv and training_deliveries.csv: the short days of learning of a store that is kept.</summary>
    public TextAsset TrainingScenario, TrainingDeliveries;
    public int TrainingExpiredPerDay = 2;

    /// <summary>The store is kept between sessions and grows by what the player orders. False under every automatic check of the fixed days.</summary>
    public bool Growth { get; private set; }
    public StoreProgress Progress { get; private set; }
    public readonly List<UnlockItem> UnlockItems = new List<UnlockItem>();
    public Button LeaveButton { get; private set; }
    /// <summary>What the last finished day added to the budget.</summary>
    public int LastEarned { get; private set; }
    /// <summary>Products ordered today, standing on the counter to be looked at until the next morning. An empty place is null.</summary>
    public readonly ProductPrefab[] Samples = new ProductPrefab[SampleSpots];

    public DayState State { get; private set; } = DayState.Intro;
    public int OrderCount { get; private set; }
    public int PossibleSales { get; private set; }
    public float Score { get; private set; }
    public string Grade { get; private set; } = "";
    public float Accuracy { get; private set; }
    public float SalesRate { get; private set; }
    public float TaskRate { get; private set; }
    /// <summary>The day's customers on average: 1 served before the hurry, 0 gone.</summary>
    public float Satisfaction { get; private set; }
    public int DaysFinished { get; private set; }
    public float ShiftStartedAt { get; private set; } = -1f;
    public string BoardText => board != null ? board.text : "";
    public Button NextDayButton { get; private set; }
    public int NextDayPresses { get; private set; }
    public bool UsesTitle { get; private set; }
    public string FinalGrade { get; private set; } = "";
    public float FinalAverage { get; private set; }
    /// <summary>Every finished day, in order.</summary>
    public readonly List<DayResult> Results = new List<DayResult>();

    RectTransform boardPanel;
    Text board;
    Text nextDayLabel;
    bool nextDayPressed, leavePressed;
    Coroutine intakeTutorialRun, briefMessage;
    string shiftText = "";
    int peakStartsAt = -1;
    int preparedDay;
    ScannerZone scanner;
    float builtScanAngle;
    Transform cookingCorner, equipmentLooks;
    string arrivedToday = "";
    bool completedToday;
    readonly Dictionary<int, ShiftPlan> plans = new Dictionary<int, ShiftPlan>();
    readonly List<UnlockItem> boxesToday = new List<UnlockItem>();

    /// <summary>The products whose boxes are on the cart this morning, each for its display shelf.</summary>
    public IReadOnlyList<UnlockItem> BoxesToday => boxesToday;
    /// <summary>How many units of today's new products were paid for.</summary>
    public int NewSoldToday { get; private set; }
    /// <summary>Order lines left out today because their product was still in its box when the customer came.</summary>
    public int MissedNewToday { get; private set; }
    int samplesPlaced;
    TextAsset storeDeliveries;
    int storeExpiredPerDay;

    /// <summary>A day of learning in a store that is kept: short, unscored, and nobody leaves.</summary>
    public bool IsTraining(int day) => Growth && day <= TrainingDays;

    void Awake()
    {
        boardPanel = WorldUi.CreatePanel(BoardAnchor, "Guide Board", BoardAnchor.position, BoardAnchor.rotation, new Vector2(BoardWidth, BoardHeight), BoardMetersPerPixel, WorldUi.Palette.Panel);
        var panel = boardPanel;
        board = WorldUi.AddText(panel, 40f, 18f, 1420f, 190f, 46, TextAnchor.MiddleCenter);
        // Drawn as a headline and what follows it; the text is still set and read through board.
        HeadlineText.On(board);
        Tutorial.Board = board;
        if (IntakeTutorial) IntakeTutorial.Board = board;

        // Under the board, shown only after a settlement when another day follows.
        var buttonPosition = BoardAnchor.position + BoardAnchor.rotation * new Vector3(0f, -0.25f, 0f);
        var buttonPanel = WorldUi.CreatePanel(BoardAnchor, "Next Day Panel", buttonPosition, BoardAnchor.rotation, new Vector2(460f, 120f), BoardMetersPerPixel, new Color(0f, 0f, 0f, 0f), interactive: true);
        NextDayButton = WorldUi.AddButton(buttonPanel, 0f, 0f, 460f, 120f, "", 52);
        NextDayButton.GetComponent<Image>().color = WorldUi.Palette.Accent;
        nextDayLabel = NextDayButton.GetComponentInChildren<Text>();
        NextDayButton.onClick.AddListener(() =>
        {
            nextDayPressed = true;
            NextDayPresses++;
        });
        buttonPanel.gameObject.SetActive(false);

        // Beside it once the store is the player's: going home for the day, which is also where the game is left.
        var leavePosition = BoardAnchor.position + BoardAnchor.rotation * new Vector3(0.36f, -0.25f, 0f);
        var leavePanel = WorldUi.CreatePanel(BoardAnchor, "Leave Panel", leavePosition, BoardAnchor.rotation, new Vector2(300f, 120f), BoardMetersPerPixel, new Color(0f, 0f, 0f, 0f), interactive: true);
        LeaveButton = WorldUi.AddButton(leavePanel, 0f, 0f, 300f, 120f, "퇴근", 52);
        LeaveButton.GetComponent<Image>().color = new Color(0.17f, 0.29f, 0.47f);
        LeaveButton.onClick.AddListener(() => leavePressed = true);
        leavePanel.gameObject.SetActive(false);

        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-start-day");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int start) && start >= 1) Day = start;
        // -with-title: through the title all the same, for the store that is kept with the headset recorder on (-hmd-perf).
        UsesTitle = Title != null && (Array.IndexOf(args, "-with-title") >= 0 || !Array.Exists(args, argument => Array.IndexOf(NoTitleArguments, argument) >= 0));
        Director.OrderArrived += OnOrderArrived;
        Director.OrderEnded += OnOrderEnded;

        // Statics outlive a scene; every run starts from the store as built.
        StockBox.CheckpointsPerBox = StockBox.DefaultCheckpoints;
        CustomerDirector.LineGraceSeconds = CustomerDirector.DefaultLineGrace;
        Growth = UsesTitle && Unlocks != null && Tablet != null && Intake != null
                 && !Array.Exists(args, argument => Array.IndexOf(FixedDaysArguments, argument) >= 0);
        if (!Growth) return;
        UnlockItems.AddRange(UnlockCatalog.Parse(Unlocks.text, Catalog));
        Progress = StoreProgress.Load(SavePath(args));
        scanner = FindFirstObjectByType<ScannerZone>();
        builtScanAngle = scanner != null ? scanner.MaxBarcodeAngle : 0f;
        var corner = GameObject.Find("Microwave Cabinet");
        cookingCorner = corner != null ? corner.transform : null;
        var looks = GameObject.Find(EquipmentLooks);
        equipmentLooks = looks != null ? looks.transform : null;
        storeDeliveries = Intake.Deliveries;
        storeExpiredPerDay = Intake.ExpiredPerDay;
        // A store saved before products came in boxes: what it had was on sale, so it is on the shelves.
        if (Progress.version < StoreProgress.CurrentVersion)
        {
            foreach (var item in UnlockItems)
                if (item.Kind == UnlockKind.Product && Progress.Has(item.Id) && !Progress.Shelved(item.Id)) Progress.shelved.Add(item.Id);
            Progress.version = StoreProgress.CurrentVersion;
        }
        foreach (var slot in Intake.DecorSlots)
            if (slot != null) slot.Shelved += OnDisplayShelved;
        Director.OrderReachedCounter += OnOrderReachedCounter;
        if (IntakeTutorial != null) IntakeTutorial.TeachDisposal = true;
        // The tutorial's product is one the store sells from the start.
        var opening = OpeningAssortment();
        if (opening.Count > 0) Tutorial.ProductId = opening[0].Data.Id;
        Tablet.Bought += OnBought;
        Debug.Log($"[Game] Growing store: {UnlockItems.Count} things to order, save at {(string.IsNullOrEmpty(Progress.Path) ? "(none)" : Progress.Path)}");
    }

    /// <summary>-save-path FILE keeps the store there (the checks use their own); -no-save keeps it in memory only.</summary>
    static string SavePath(string[] args)
    {
        if (Array.IndexOf(args, "-no-save") >= 0) return null;
        int index = Array.IndexOf(args, "-save-path");
        if (index >= 0 && index + 1 < args.Length) return args[index + 1];
        return System.IO.Path.Combine(Application.persistentDataPath, StoreProgress.FileName);
    }

    void OnDestroy()
    {
        if (Tablet != null) Tablet.Bought -= OnBought;
        if (Director == null) return;
        Director.OrderArrived -= OnOrderArrived;
        Director.OrderEnded -= OnOrderEnded;
        Director.OrderReachedCounter -= OnOrderReachedCounter;
    }

    IEnumerator Start()
    {
        if (!UsesTitle)
        {
            yield return RunDays();
            yield break;
        }
        while (true)
        {
            yield return RunTitle();
            if (Growth)
            {
                yield return RunStoreDays();
                yield return RunPunchOut();
            }
            else
            {
                yield return RunDays();
                yield return RunEnding();
            }
            ResetRun();
        }
    }

    // ---------- A store that is kept and grows ----------

    /// <summary>
    /// Day after day with no last one: each ends in its settlement, what it earned for the budget, the ordering tablet
    /// (once the training is done), and the choice between the next day and going home.
    /// </summary>
    IEnumerator RunStoreDays()
    {
        while (true)
        {
            yield return RunDay(Day);
            DaysFinished++;
            bool graduatedNow = !Progress.graduated && Day >= TrainingDays;
            // What went out of the till as too much change comes out of what the day earned (2026-11-13).
            LastEarned = Mathf.Max(0, BudgetFor(Director.Sales, Director.Tips, Grade) - Director.TillShort) + (graduatedNow ? GraduationGrant : 0);
            Progress.budget += LastEarned;
            Progress.totalSales += Director.Sales;
            Progress.daysWorked++;
            Progress.day = Day + 1;
            Progress.graduated |= graduatedNow;
            Progress.Save();

            // One more line on the board, which grows by it.
            if (graduatedNow)
            {
                // The training is over: the manager is back, and the store's ordering is handed to the player.
                board.fontSize = 40;
                board.text = $"수습 수료\n{board.text.Substring(board.text.IndexOf('\n') + 1)}\n계산대 오른쪽 끝 발주 단말기에서 내일 들일 것을 고르세요";
                if (Manager != null) Manager.Arrive();
                if (Pos.Audio) Pos.Audio.NarrateManager(ManagerLine.Graduated);
            }
            board.text += graduatedNow ? $"\n발주 예산 {Progress.budget:N0}원  (수료 지원금 {GraduationGrant:N0}원 포함)" : $"\n발주 예산 +{LastEarned:N0}원  ·  잔액 {Progress.budget:N0}원";
            // The settlement is four lines; the budget, and on a day with new products what they sold, come under it.
            int moreLines = Mathf.Max(1, board.text.Split('\n').Length - 4);
            boardPanel.sizeDelta = new Vector2(BoardWidth, BoardHeight + 50f * moreLines);
            board.rectTransform.sizeDelta = new Vector2(board.rectTransform.sizeDelta.x, BoardHeight - 30f + 50f * moreLines);
            if (Intake.Active) Intake.SetGuide(board.text);
            Debug.Log($"[Game] Store day={Day} earned={LastEarned} budget={Progress.budget} graduated={Progress.graduated} owned={Progress.owned.Count}");

            if (Progress.graduated) Tablet.Open(Progress, UnlockItems, OpeningAssortment().Count, graduatedNow ? "내일 들일 것을 골라 보세요" : "");
            // The arrow goes to the terminal the first time, until something is ordered or the day is left.
            var pointer = Title != null ? Title.Pointer : null;
            if (graduatedNow && pointer != null)
            {
                var top = Tablet.transform.position + Vector3.up * (OrderTablet.Size.y * OrderTablet.MetersPerPixel * 0.5f + 0.03f);
                pointer.Aim = () => Progress.arriving.Count == 0 ? PointerAim.Down(top, "발주") : (PointerAim?)null;
            }
            yield return WaitForChoice($"{Day + 1}일차 출근");
            if (graduatedNow && pointer != null) pointer.Aim = null;
            Tablet.Close();
            if (leavePressed) yield break;
            Day++;
        }
    }

    /// <summary>What a day adds to the budget: a share of the sales, the tips, and a bonus for a day graded well.</summary>
    public static int BudgetFor(int sales, int tips, string grade) =>
        Mathf.RoundToInt(sales * BudgetShare) + tips + (grade == "S" ? 2000 : grade == "A" ? 1000 : grade == "B" ? 500 : 0);

    /// <summary>Going home: the time card again, and the title is the door to the same store next time.</summary>
    IEnumerator RunPunchOut()
    {
        State = DayState.Ending;
        yield return Title.Punch("퇴근 카드", ManagerLine.PunchOut, Say);
        RestoreBoard();
        Say($"퇴근\n다음 출근은 {Progress.day}일차입니다");
        yield return new WaitForSeconds(LeaveSeconds);
    }

    IEnumerator WaitForChoice(string nextLabel)
    {
        nextDayPressed = leavePressed = false;
        nextDayLabel.text = nextLabel;
        NextDayButton.transform.parent.gameObject.SetActive(true);
        LeaveButton.transform.parent.gameObject.SetActive(true);
        while (!nextDayPressed && !leavePressed) yield return null;
        NextDayButton.transform.parent.gameObject.SetActive(false);
        LeaveButton.transform.parent.gameObject.SetActive(false);
    }

    /// <summary>What the store sells before anything is ordered in: the products of the shelves the clerk stocks.</summary>
    List<ProductPrefab> OpeningAssortment()
    {
        var products = new List<ProductPrefab>();
        foreach (var slot in Intake.Slots)
        {
            var product = slot != null ? Catalog.Find(slot.ProductId) : null;
            if (product != null && !products.Contains(product)) products.Add(product);
        }
        return products;
    }

    /// <summary>The store as the day's customers find it: the opening assortment, what was ordered in, and what the equipment brought.</summary>
    public ShiftPlan PlanFor(int day)
    {
        // Made once a morning: what is shelved during the day must not change the day's customers after the fact.
        if (plans.TryGetValue(day, out var made)) return made;
        var plan = new ShiftPlan { Day = day };
        plan.Assortment.AddRange(OpeningAssortment());
        foreach (var item in UnlockItems)
        {
            if (!Progress.Has(item.Id)) continue;
            if (item.Kind == UnlockKind.Product)
            {
                // On sale once it stands on its shelf. In a box this morning: for the customers who come after it does.
                // Still waiting for its box: not yet.
                if (item.Product == null) continue;
                if (Progress.Shelved(item.Id))
                {
                    plan.OrderedProducts++;
                    if (!plan.Assortment.Contains(item.Product)) plan.Assortment.Add(item.Product);
                }
                else if (boxesToday.Contains(item))
                {
                    plan.OrderedProducts++;
                    plan.NewToday.Add(item.Product);
                }
                continue;
            }
            if (item.Product != null && !plan.Assortment.Contains(item.Product)) plan.Assortment.Add(item.Product);
            if (item.Id == UnlockCatalog.Microwave) plan.MicrowaveItem = item.Product;
            else if (item.Id == UnlockCatalog.HotWater) plan.HotWaterItem = item.Product;
            else if (item.Id == UnlockCatalog.OnePlus) plan.OnePlus = true;
        }
        plans[day] = plan;
        return plan;
    }

    /// <summary>
    /// The morning: what was ordered yesterday is in the store, and the equipment the store has is switched to what it does.
    /// The shelves follow in PrepareStore, after the day's stock is laid out.
    /// </summary>
    void OpenStore()
    {
        var arrived = new List<string>();
        foreach (var item in UnlockItems)
            if (Progress.arriving.Contains(item.Id)) arrived.Add(item.Name);
        Progress.ReceiveArrivals();
        arrivedToday = string.Join(" · ", arrived);
        ClearSamples();

        // The boxes of the morning: products in the store that have not been put on their shelf, first ordered first.
        plans.Clear();
        boxesToday.Clear();
        foreach (var item in UnlockItems)
            if (boxesToday.Count < NewBoxesPerDay && item.Kind == UnlockKind.Product && item.Product != null
                && Progress.Has(item.Id) && !Progress.Shelved(item.Id) && Intake.SlotFor(item.Slot) != null)
                boxesToday.Add(item);

        if (scanner != null) scanner.MaxBarcodeAngle = Progress.Has(UnlockCatalog.Scanner) ? NewScannerAngle : builtScanAngle;
        StockBox.CheckpointsPerBox = Progress.Has(UnlockCatalog.Cutter) ? NewCutterCheckpoints : StockBox.DefaultCheckpoints;
        CustomerDirector.LineGraceSeconds = Progress.Has(UnlockCatalog.Music) ? MusicLineGrace : CustomerDirector.DefaultLineGrace;
        if (Pos.Audio) Pos.Audio.SetMusic(Progress.Has(UnlockCatalog.Music));
        if (Director.Desk != null) Director.Desk.AutoCoins = Progress.Has(UnlockCatalog.Coins);
        ShowCooking(Progress.Has(UnlockCatalog.Microwave), Progress.Has(UnlockCatalog.HotWater));
        // What was paid for is seen in the store, not only felt in a number.
        ShowLook(ScannerLook, Progress.Has(UnlockCatalog.Scanner));
        ShowLook(CoinLook, Progress.Has(UnlockCatalog.Coins));
        ShowLook(MusicLook, Progress.Has(UnlockCatalog.Music));
        foreach (var cutter in Intake.Cutters)
        {
            var model = cutter != null ? cutter.transform.Find(CutterLook) : null;
            if (model != null) model.gameObject.SetActive(Progress.Has(UnlockCatalog.Cutter));
        }

        // The first morning with everything in: said once, on the board of that day.
        bool everything = UnlockItems.Count > 0 && UnlockItems.TrueForAll(item => Progress.Has(item.Id) && (item.Kind != UnlockKind.Product || Progress.Shelved(item.Id)));
        completedToday |= everything && !Progress.completed;
        Progress.completed = everything;
    }

    void ShowLook(string look, bool there)
    {
        var group = equipmentLooks != null ? equipmentLooks.Find(look) : null;
        if (group != null) group.gameObject.SetActive(there);
    }

    /// <summary>Whether the looks of a piece of equipment stand in the store (one of the ...Look names).</summary>
    public bool LookShown(string look)
    {
        if (look == CutterLook)
            return Intake.Cutters.Length > 0 && Array.TrueForAll(Intake.Cutters, cutter => cutter != null && cutter.transform.Find(CutterLook) is Transform model && model.gameObject.activeInHierarchy);
        var group = equipmentLooks != null ? equipmentLooks.Find(look) : null;
        return group != null && group.gameObject.activeInHierarchy;
    }

    /// <summary>The microwave and the hot water dispenser stand on their cabinet only once the store has them.</summary>
    void ShowCooking(bool microwave, bool hotWater)
    {
        if (cookingCorner == null) return;
        foreach (Transform part in cookingCorner)
        {
            bool ofMicrowave = part.name.StartsWith("Microwave", StringComparison.Ordinal);
            bool ofDispenser = part.name.StartsWith("Dispenser", StringComparison.Ordinal) || part.name.StartsWith("Water", StringComparison.Ordinal);
            if (!ofMicrowave && !ofDispenser) continue;
            bool there = ofMicrowave ? microwave : hotWater;
            // The oven's light and the stream of water are their zones' to switch on while they work.
            if (there && (part.name == "Microwave Lit" || part.name == "Dispenser Water")) continue;
            part.gameObject.SetActive(there);
        }
    }

    /// <summary>The display shelves in use: those holding a product that was shelved, and the bare ones today's boxes are for.</summary>
    HashSet<string> StockedDecor()
    {
        var slots = new HashSet<string>();
        foreach (var item in UnlockItems)
            if (item.Kind == UnlockKind.Product && (Progress.Shelved(item.Id) || boxesToday.Contains(item))) slots.Add(item.Slot);
        return slots;
    }

    string BoxNames()
    {
        var names = new List<string>();
        foreach (var item in boxesToday) names.Add(item.Name);
        return string.Join(" · ", names);
    }

    /// <summary>The first unit of a product ordered in stands on its shelf: from now on it is on sale, today and for good.</summary>
    void OnDisplayShelved(ShelfSlotZone slot, ProductPrefab product)
    {
        var item = UnlockItems.Find(candidate => candidate.Kind == UnlockKind.Product && candidate.Slot == slot.SlotId);
        if (item == null || Progress.Shelved(item.Id)) return;
        Progress.shelved.Add(item.Id);
        Progress.Save();
        Debug.Log($"[Game] {item.Id} is on its shelf {slot.SlotId}: on sale from now on");
        if (State == DayState.Shift) ShowBriefly($"{item.Name} 진열\n이제 손님이 찾습니다", BriefSeconds);
    }

    /// <summary>
    /// A customer who came for a product still in its box goes without it and pays for the rest. The day's possible sales
    /// were counted with it, so the sale shows as lost in the settlement.
    /// </summary>
    void OnOrderReachedCounter(Order order)
    {
        if (!Growth) return;
        // Never the first line: the generator keeps new products out of it.
        for (int i = order.Lines.Count - 1; i >= 1; i--)
        {
            string id = order.Lines[i].Product.Data.Id;
            if (Progress.Shelved(id) || !boxesToday.Exists(item => item.Id == id)) continue;
            order.Lines.RemoveAt(i);
            MissedNewToday++;
            Debug.Log($"[Game] Order {order.Id}: {id} was not on its shelf yet, left out");
        }
    }

    /// <summary>A product just ordered stands on the counter, the real thing to pick up and turn over before it is on the shelf.</summary>
    void OnBought(UnlockItem item)
    {
        if (item.Kind != UnlockKind.Product || item.Product == null || Director.GoodsAnchor == null) return;
        // Two places side by side; a third order takes the place of the oldest sample.
        int spot = samplesPlaced++ % SampleSpots;
        if (Samples[spot] != null) Destroy(Samples[spot].gameObject);
        var place = Director.GoodsAnchor.position + Vector3.right * (spot * SampleSpacing) + Vector3.up * (item.Product.Proxy.size.y * 0.5f + 0.004f);
        Samples[spot] = Instantiate(item.Product, place, Quaternion.Euler(0f, 180f, 0f));
    }

    void ClearSamples()
    {
        for (int i = 0; i < Samples.Length; i++)
        {
            if (Samples[i] != null) Destroy(Samples[i].gameObject);
            Samples[i] = null;
        }
        samplesPlaced = 0;
    }

    IEnumerator RunDays()
    {
        while (true)
        {
            yield return RunDay(Day);
            DaysFinished++;
            if (Day >= LastDay || ParseOrders(Day + 1).Count == 0)
            {
                ShowFinalReport();
                yield break;
            }
            yield return WaitForNextDay(Day + 1);
            Day++;
        }
    }

    // ---------- Into the shift and out of it (game design 10.0) ----------

    IEnumerator RunTitle()
    {
        State = DayState.Title;
        boardPanel.gameObject.SetActive(false);
        if (Growth) Title.OfferStore(Progress.Started ? Progress.day : 0, TrainingDays + 1);
        // Behind the closed door the store is already stocked, so the first look through it is the finished store. A store
        // that is kept shows as it will be this morning, with what was ordered yesterday already on the shelves.
        PrepareStore(Growth ? Progress.day : 1);
        // People are already there behind the glass: the manager at the counter's end and a couple of customers shopping.
        if (Manager != null) Manager.Arrive();
        Director.StartAmbient(AmbientCustomers);
        yield return Title.ChooseDay();
        if (Growth)
        {
            if (Title.Choice != TitleChoice.Continue)
            {
                Progress.Reset();
                if (Title.Choice == TitleChoice.SkipTraining)
                {
                    Progress.graduated = true;
                    Progress.day = TrainingDays + 1;
                    Progress.budget = GraduationGrant;
                }
                Progress.Save();
                preparedDay = 0;
            }
            Day = Progress.day;
        }
        else
        {
            Day = Title.ChosenDay;
        }
        PrepareStore(Day);
        State = DayState.Commute;
        yield return Title.WalkIn();
        boardPanel.gameObject.SetActive(true);
        State = DayState.PunchIn;
        yield return Title.Punch("출근 카드", ManagerLine.PunchIn, Say);
    }

    IEnumerator RunEnding()
    {
        State = DayState.Ending;
        // The report stays on the board to be read; the order card, which is empty now, asks for the punch-out.
        yield return Title.Punch("퇴근 카드", ManagerLine.PunchOut, Director.Announce);
        Director.Announce("");
        RestoreBoard();
        var line = FinalGrade == "S" ? ManagerLine.EndingS : FinalGrade == "A" ? ManagerLine.EndingA : FinalGrade == "B" ? ManagerLine.EndingB : ManagerLine.EndingC;
        if (Pos.Audio) Pos.Audio.NarrateManager(line);
        // The words in quotes are the manager's, from her spoken ending (Data/voice_lines.json).
        string ending = FinalGrade == "S" ? "정규직 제안\n\"다음 달부터 정식으로 같이 일해요, 우리.\""
            : FinalGrade == "A" ? "다음 주 근무표 확정\n\"다음 주 근무표에도 이름 넣어 둘게요.\""
            : FinalGrade == "B" ? "수습 기간 연장\n\"손에 좀 더 익으면 금방 늘 거예요.\""
            : "재교육 대상\n\"첫 알바는 다들 그래요. 다음에 한 번 더 해 봐요.\"";
        int sales = 0, tips = 0;
        foreach (var result in Results)
        {
            sales += result.Sales;
            tips += result.Tips;
        }
        board.text = $"{ending}\n종합 {FinalGrade} · 평균 {FinalAverage:0}점 · 매출 {sales:N0}원 · 팁 {tips:N0}원";
        Debug.Log($"[Game] Ending grade={FinalGrade} average={FinalAverage:0.0} sales={sales} tips={tips}");
        yield return WaitForButton("처음으로");
    }

    /// <summary>Everything the days left behind, for the next person at the title.</summary>
    void ResetRun()
    {
        Results.Clear();
        DaysFinished = 0;
        Day = 1;
        preparedDay = 0;
        FinalGrade = "";
        FinalAverage = 0f;
        RestoreBoard();
        Pos.ResetDay();
        Director.ResetRun();
        if (Manager != null) Manager.Clear();
        if (Tablet != null) Tablet.Close();
        ClearSamples();
    }

    void RestoreBoard()
    {
        boardPanel.sizeDelta = new Vector2(BoardWidth, BoardHeight);
        board.rectTransform.sizeDelta = new Vector2(board.rectTransform.sizeDelta.x, BoardHeight - 30f);
        board.fontSize = 46;
    }

    /// <summary>Sets the shelves and boxes for a day. Doing it early, behind the closed door, must not make the day do it twice.</summary>
    void PrepareStore(int day)
    {
        if (preparedDay == day || Intake == null) return;
        if (Growth)
        {
            OpenStore();
            bool training = IsTraining(day) && TrainingDeliveries != null;
            Intake.Deliveries = training ? TrainingDeliveries : storeDeliveries;
            Intake.ExpiredPerDay = training ? TrainingExpiredPerDay : storeExpiredPerDay;
        }
        // A store that is kept hands over the morning's boxes of new products, each for its display shelf (none on most days).
        List<Delivery> newProducts = null;
        if (Growth)
        {
            newProducts = new List<Delivery>();
            if (day > TrainingDays)
                foreach (var item in boxesToday)
                    newProducts.Add(new Delivery
                    {
                        Id = "new_" + item.Id, Day = day, BoxSequence = 11 + newProducts.Count, Product = item.Product,
                        CaseQuantity = Intake.SlotFor(item.Slot).Capacity, TargetSlot = item.Slot,
                    });
        }
        // The days after the training have no delivery rows of their own: they get the boxes of the first intake day.
        Intake.BeginDay(day, ParseOrders(day).Count, Growth && day > TrainingDays ? IntakeTutorialDay : 0, newProducts);
        if (Growth) Intake.ShowDecor(StockedDecor());
        preparedDay = day;
    }

    // ---------- Day 3: what the shift announces on the board ----------

    /// <summary>Game design 10.3: the first time a request comes up, the board says what to do about it.</summary>
    void OnOrderArrived(Order order, bool firstOfItsKind)
    {
        if (!firstOfItsKind) return;
        string guide = order.Special == Order.Microwave ? "특수 요청  전자레인지\n스캔 → 왼쪽 냉장고 옆 전자레인지에 넣기 → '땡' 뒤 꺼내기"
            : order.Special == Order.HotWater ? "특수 요청  온수\n스캔 → 전자레인지 옆 온수기 탭 아래에 2초"
            : "특수 요청  1+1 행사\n2개 모두 스캔하면 1개 값만 계산됩니다";
        ShowBriefly(guide, RequestGuideSeconds);
    }

    /// <summary>Doc 5.7.2: a mid-day box arrives right after its customer. The last moment before the peak gets a warning too.</summary>
    void OnOrderEnded(Order order, bool paid)
    {
        if (paid && Growth)
            foreach (var line in order.Lines)
                if (boxesToday.Exists(item => item.Product == line.Product)) NewSoldToday += line.Quantity;
        if (Intake != null && Intake.ReceiveMidDayBoxes(order.Sequence))
        {
            ShowBriefly("입고 도착 (창고 입구)\n손님이 없을 때 진열", BriefSeconds);
            Director.Announce("입고 도착 (창고 입구)");
        }
        else if (order.Sequence + 1 == peakStartsAt)
        {
            ShowBriefly("피크 타임\n손님이 몰려옵니다", BriefSeconds);
        }
    }

    /// <summary>A message on the board for a while, then back to the day's line (unless the shift is over by then).</summary>
    void ShowBriefly(string text, float seconds)
    {
        if (briefMessage != null) StopCoroutine(briefMessage);
        briefMessage = StartCoroutine(Brief(text, seconds));
    }

    IEnumerator Brief(string text, float seconds)
    {
        Say(text);
        yield return new WaitForSeconds(seconds);
        briefMessage = null;
        if (State == DayState.Shift) Say(shiftText);
    }

    /// <summary>The first order whose time limit is shorter than the day's first: the scenario's peak (doc 6.3). -1 when there is none.</summary>
    static int FindPeakStart(List<Order> orders)
    {
        foreach (var order in orders)
            if (order.TimeLimitSeconds < orders[0].TimeLimitSeconds) return order.Sequence;
        return -1;
    }

    System.Collections.Generic.List<Order> ParseOrders(int day)
    {
        // Past the training there are no rows: the customers follow from what the store sells.
        if (Growth && day > TrainingDays) return OrderGenerator.Build(PlanFor(day));
        if (IsTraining(day) && TrainingScenario != null) return DayScenarioLoader.Parse(TrainingScenario.text, day, Catalog);
        var text = Scenario != null ? Scenario.text : "";
        foreach (var more in MoreScenarios)
            if (more != null) text += "\n" + more.text;
        return DayScenarioLoader.Parse(text, day, Catalog);
    }

    IEnumerator WaitForNextDay(int next) => WaitForButton($"{next}일차 시작");

    IEnumerator WaitForButton(string label)
    {
        nextDayPressed = false;
        nextDayLabel.text = label;
        NextDayButton.transform.parent.gameObject.SetActive(true);
        while (!nextDayPressed) yield return null;
        NextDayButton.transform.parent.gameObject.SetActive(false);
    }

    IEnumerator RunDay(int day)
    {
        State = DayState.Intro;
        Score = 0f;
        Grade = "";
        ShiftStartedAt = -1f;
        RestoreBoard();
        NewSoldToday = MissedNewToday = 0;
        // Before the orders are made: what came in this morning is part of what today's customers buy.
        if (Growth) PrepareStore(day);
        var orders = ParseOrders(day);
        OrderCount = orders.Count;
        PossibleSales = 0;
        foreach (var order in orders) PossibleSales += order.TotalPrice;
        peakStartsAt = orders.Count > 0 ? FindPeakStart(orders) : -1;
        if (day > 1 || DaysFinished > 0)
        {
            Pos.ResetDay();
            Director.ResetDay();
        }
        bool intake = false;
        if (Intake != null)
        {
            PrepareStore(day);
            preparedDay = 0;
            intake = Intake.Active;
        }

        bool training = IsTraining(day);
        Director.Lenient = training;
        board.text = !intake ? $"{day}일차 출근\n계산대에서 손님을 맞습니다"
            : training && day > IntakeTutorialDay ? $"{day}일차 출근\n오늘은 혼자 근무합니다"
            : Growth && day > TrainingDays && completedToday ? $"{day}일차 출근  ·  매장 완성\n들일 수 있는 상품과 설비를 모두 들였습니다"
            : Growth && day > TrainingDays && boxesToday.Count > 0 ? $"{day}일차 출근\n새 상품 입고  {BoxNames()}  ·  진열해야 팔립니다"
            : Growth && day > TrainingDays ? $"{day}일차 출근\n" + (arrivedToday.Length > 0 ? $"새로 들어옴  {arrivedToday}" : $"손님 {orders.Count}명 · 입고 {Intake.Boxes.Count}상자")
            : day > IntakeTutorialDay ? $"{day}일차 출근\n피크 타임 · 특수 요청 · 중간 입고"
            : $"{day}일차 출근\n입고 · 진열 · 폐기가 더해집니다";
        arrivedToday = "";
        if (completedToday && Pos.Audio) Pos.Audio.NarrateManager(ManagerLine.StoreComplete);
        completedToday = false;
        if (intake) Intake.SetGuide(board.text);
        yield return new WaitForSeconds(IntroSeconds);

        if (day == 1 && RunTutorial)
        {
            State = DayState.Tutorial;
            yield return Tutorial.Run();
        }
        // From here the store is the player's: whoever was only there for the way in goes.
        Director.DismissAmbient();
        if (Manager != null) Manager.Leave();

        bool guidedIntake = intake && day == IntakeTutorialDay && IntakeTutorial != null;
        if (guidedIntake)
        {
            // Doc 5.7.4: the first box gets four steps of guidance; customers start when they are done or after 90 s.
            State = DayState.Tutorial;
            intakeTutorialRun = StartCoroutine(IntakeTutorial.Run());
            float begun = Time.time;
            while (IntakeTutorial.Step != IntakeTutorialStep.Done && Time.time - begun < IntakeTutorialMaxSeconds) yield return null;
        }
        // Name tags and the intake panels by the shelves are for learning; the shift itself runs on the board and voice.
        StationLabel.ShowAll(false);
        if (intake) Intake.ShowGuides(false);

        State = DayState.Shift;
        ShiftStartedAt = Time.time;
        // The last day of learning says the tip rule again: from the next day it is what pays for the store.
        shiftText = intake && !(training && day > IntakeTutorialDay) ? $"{day}일차 근무 중\n손님이 없을 때 진열 · 폐기" : $"{day}일차 근무 중\n시간 막대가 절반을 넘기 전에 끝내면 팁";
        if (!intake) board.text = shiftText;
        else if (!guidedIntake || IntakeTutorial.Step == IntakeTutorialStep.Done) Say(shiftText);
        yield return Director.RunDay(orders);

        State = DayState.Settlement;
        // A tutorial nobody finished must not go on writing on the board through the next day.
        if (intakeTutorialRun != null) StopCoroutine(intakeTutorialRun);
        intakeTutorialRun = null;
        if (intake) Intake.EndDay();
        if (training)
        {
            // Learning is not graded: what was done, and what it brought in.
            board.text = $"{day}일차 끝\n손님 {Director.Paid}명 · 매출 {Director.Sales:N0}원 · 팁 {Director.Tips:N0}원{(Director.TillShort > 0 ? " · " + TillText : "")}";
            if (intake) Intake.SetGuide(board.text);
            Debug.Log($"[Game] Training day={day} done: paid={Director.Paid}/{OrderCount} sales={Director.Sales} tips={Director.Tips}");
            State = DayState.DayEnd;
            yield break;
        }
        CalculateScore(day, intake);
        RecordResult(day, intake);
        if (intake)
        {
            board.fontSize = 36;
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점{(Director.EndedEarly ? "   (이탈로 조기 마감)" : "")}\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}\n" +
                         $"손님 만족 {Satisfaction * 100f:0}% · 팁 {Director.Tips:N0}원 · {TillText}\n" +
                         $"진열 {Intake.Shelved}/{Intake.DeliveredTotal} · 폐기 {Intake.Disposed}/{Intake.ExpiredTotal} · 오진열 {Intake.Misplaced} · 폐기 오탐 {Intake.FalseDisposals} · 빈 박스 {Intake.UnrecycledEmptyBoxes}";
            // What yesterday's order did today: the reason to order again.
            if (Growth && boxesToday.Count > 0)
                board.text += $"\n새 상품 {BoxNames()}  ·  {NewSoldToday}개 판매" + (MissedNewToday > 0 ? $"  ·  진열 전이라 놓친 주문 {MissedNewToday}건" : "");
            Intake.SetGuide(board.text);
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount} tillShort={Director.TillShort} changeAsks={Director.ChangeAsks} tips={Director.Tips} shelved={Intake.Shelved}/{Intake.DeliveredTotal} disposed={Intake.Disposed}/{Intake.ExpiredTotal} misplaced={Intake.Misplaced} falseDisposals={Intake.FalseDisposals} unrecycled={Intake.UnrecycledEmptyBoxes} endedEarly={Director.EndedEarly}");
        }
        else
        {
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}\n" +
                         $"손님 만족 {Satisfaction * 100f:0}% · 팁 {Director.Tips:N0}원 · {TillText}";
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount} tillShort={Director.TillShort} changeAsks={Director.ChangeAsks} tips={Director.Tips}");
        }
        State = DayState.DayEnd;
    }

    /// <summary>The till counted at the end of the day (2026-11-13): short by what customers were given back too much.</summary>
    string TillText => Director.TillShort > 0 ? $"시재 −{Director.TillShort:N0}원" : "시재 맞음";

    void Say(string text)
    {
        board.text = text;
        if (Intake != null) Intake.SetGuide(text);
    }

    void RecordResult(int day, bool intake)
    {
        Results.Add(new DayResult
        {
            Day = day, Sales = Director.Sales, PossibleSales = PossibleSales, Paid = Director.Paid, Orders = OrderCount, Left = Director.Left,
            Cancels = Pos.CancelCount, Score = Score, Grade = Grade,
            Shelved = intake ? Intake.Shelved : 0, Delivered = intake ? Intake.DeliveredTotal : 0,
            Disposed = intake ? Intake.Disposed : 0, Expired = intake ? Intake.ExpiredTotal : 0,
            Tips = Director.Tips, Satisfaction = Satisfaction, TillShort = Director.TillShort,
        });
    }

    /// <summary>Doc 6.5: after the last day the board grows and adds a line per day and the totals under that day's settlement.</summary>
    void ShowFinalReport()
    {
        float scores = 0f;
        var total = new DayResult();
        foreach (var result in Results)
        {
            scores += result.Score;
            total.Sales += result.Sales;
            total.PossibleSales += result.PossibleSales;
            total.Paid += result.Paid;
            total.Orders += result.Orders;
            total.Left += result.Left;
            total.Cancels += result.Cancels;
            total.Shelved += result.Shelved;
            total.Delivered += result.Delivered;
            total.Disposed += result.Disposed;
            total.Expired += result.Expired;
            total.Tips += result.Tips;
            total.TillShort += result.TillShort;
        }
        float average = Results.Count > 0 ? scores / Results.Count : 0f;
        FinalAverage = average;
        FinalGrade = GradeFor(average);

        var text = new StringBuilder(board.text);
        text.Append($"\n\n{Results.Count}일 근무 리포트   {GradeFor(average)}  ·  평균 {average:0}점\n");
        foreach (var result in Results)
            text.Append($"{result.Day}일차  {result.Grade}  {result.Score:0}점  ·  매출 {result.Sales:N0} / {result.PossibleSales:N0}원  ·  손님 {result.Paid}/{result.Orders}\n");
        text.Append($"합계 매출 {total.Sales:N0} / {total.PossibleSales:N0}원 · 손님 {total.Paid}/{total.Orders} · 이탈 {total.Left}명 · 취소 {total.Cancels} · 진열 {total.Shelved}/{total.Delivered} · 폐기 {total.Disposed}/{total.Expired} · 팁 {total.Tips:N0}원{(total.TillShort > 0 ? $" · 시재 −{total.TillShort:N0}원" : "")}");

        boardPanel.sizeDelta = new Vector2(BoardWidth, ReportBoardHeight);
        board.rectTransform.sizeDelta = new Vector2(board.rectTransform.sizeDelta.x, ReportBoardHeight - 36f);
        board.fontSize = 34;
        board.text = text.ToString();
        if (Intake != null) Intake.SetGuide(board.text);
        Debug.Log($"[Game] Final report days={Results.Count} average={average:0.0} grade={GradeFor(average)} sales={total.Sales}/{total.PossibleSales} paid={total.Paid}/{total.Orders} left={total.Left} cancels={total.Cancels} shelved={total.Shelved}/{total.Delivered} disposed={total.Disposed}/{total.Expired}");
    }

    static string GradeFor(float score) => score >= 90f ? "S" : score >= 75f ? "A" : score >= 60f ? "B" : "C";

    /// <summary>
    /// Game design 11 [제안]: Day 1 without tasks and without punishing slowness; Days 2-3 with the task rate and the intake
    /// penalties, where service is how fast each customer was served (one never reached counts as unserved).
    /// </summary>
    void CalculateScore(int day, bool intake)
    {
        SalesRate = PossibleSales > 0 ? (float)Director.Sales / PossibleSales : 0f;
        Satisfaction = OrderCount > 0 ? Director.SatisfactionTotal / OrderCount : 0f;
        float service = OrderCount > 0 ? 1f - (float)Director.Left / OrderCount : 0f;
        if (!intake)
        {
            Accuracy = Mathf.Max(0f, 1f - 0.1f * (Pos.CancelCount + Director.OverChanges));
            TaskRate = 1f;
            Score = 50f * SalesRate + 30f * service + 20f * Accuracy;
        }
        else
        {
            int mistakes = Pos.CancelCount + Director.OverChanges + Intake.FalseDisposals + Intake.Misplaced + Intake.UnrecycledEmptyBoxes;
            Accuracy = Mathf.Max(0f, 1f - 0.1f * mistakes);
            TaskRate = Intake.TaskRate;
            Score = 40f * SalesRate + 25f * Satisfaction + 15f * Accuracy + 20f * TaskRate;
        }
        Grade = GradeFor(Score);
    }
}
