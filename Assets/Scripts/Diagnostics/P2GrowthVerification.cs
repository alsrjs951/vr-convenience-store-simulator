using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Checks the store that is kept and grows (docs/p2/해금_성장_기획_2026-11-10.md): the order generator and the budget
/// rule as plain functions, then the real scene from the title through two generated days, the ordering terminal, what
/// arrives the next morning, the save file, and back to the title.
///
/// Run with -p2-growth-verify -save-path FILE (a file that does not exist yet) [-p2-evidence FOLDER].
///
/// It has no hands: buttons are pressed through their onClick, the player is stood at the counter, the time card is put
/// in the slot, and customers are paid for through the POS. So it says nothing about reaching or pressing anything, and
/// nothing about how the days feel. What the hands do is the other checks' business.
/// </summary>
public class P2GrowthVerification : MonoBehaviour
{
    const float StartupSeconds = 5f;
    const float CustomerTimeout = 150f;

    public GameManager Game;
    public IntakeDirector Intake;

    [Serializable] class Stage { public string name; public bool pass; public string details; public float time; }

    [Serializable]
    class Report
    {
        public bool pass;
        public string unity, runtime, scope, savePath;
        public float meanDesktopFps;
        public int checks, failed;
        public List<Stage> stages = new List<Stage>();
    }

    readonly Report report = new Report();
    string output;
    float started;
    int frames;
    bool finished;
    Camera view;
    PaymentDesk desk;
    ScannerZone scanner;
    Transform cooking;
    bool coinsTried;
    bool musicWasSilent;
    bool trainingRun;

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-p2-growth-verify") < 0) return;
        // With -p2-training the run is the three days of learning from a new store, up to the first morning after them.
        // The Day 1 tutorial needs hands (it is the Day 1 check's), so it is left out here.
        trainingRun = Array.IndexOf(args, "-p2-training") >= 0;
        if (trainingRun) Game.RunTutorial = false;
        Application.runInBackground = true;
        output = Path.Combine(Application.dataPath, "../docs/p2/evidence/growth/editor");
        int index = Array.IndexOf(args, "-p2-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        started = Time.realtimeSinceStartup;
        StartCoroutine(Run());
    }

    void Update()
    {
        if (output != null) frames++;
    }

    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        view = Camera.main;
        desk = Game.Director.Desk;
        scanner = FindFirstObjectByType<ScannerZone>();
        var corner = GameObject.Find("Microwave Cabinet");
        cooking = corner != null ? corner.transform : null;
        if (!Check("setup.growing_store", Game.Growth && Game.Progress != null && Game.Tablet != null && Game.UnlockItems.Count == 16 && view != null && desk != null && scanner != null && cooking != null,
                $"growth={Game.Growth}; unlocks={Game.UnlockItems.Count}; tablet={Game.Tablet != null}; cabinet={cooking != null}"))
        {
            Finish();
            yield break;
        }
        report.savePath = Game.Progress.Path ?? "";
        if (!Check("setup.new_store", !Game.Progress.Started && Game.Progress.budget == 0 && Game.Progress.owned.Count == 0 && !string.IsNullOrEmpty(Game.Progress.Path),
                $"day={Game.Progress.day}; graduated={Game.Progress.graduated}; budget={Game.Progress.budget}; path={Game.Progress.Path}"))
        {
            Finish();
            yield break;
        }

        if (trainingRun)
        {
            yield return Training();
            Finish();
            yield break;
        }

        Rules();
        SaveRoundTrip();

        yield return TitleToCounter();
        if (finished) yield break;
        yield return FirstStoreDay();
        if (finished) yield break;
        yield return OrderingTerminal();
        if (finished) yield break;
        yield return SecondStoreDay();
        if (finished) yield break;
        yield return FillTheStore();
        if (finished) yield break;
        yield return HomeAndBack();
        if (finished) yield break;
        yield return BoxesComeAgain();
        if (finished) yield break;
        yield return StartOver();
        Finish();
    }

    // ---------- Plain functions ----------

    void Rules()
    {
        var opening = Game.PlanFor(GameManager.TrainingDays + 1);
        var first = OrderGenerator.Build(opening);
        var again = OrderGenerator.Build(opening);
        Check("rule.opening_store", opening.Assortment.Count == 5 && opening.OrderedProducts == 0 && opening.Services == 0 && first.Count == OrderGenerator.BaseCustomers,
            $"assortment={opening.Assortment.Count}; ordered={opening.OrderedProducts}; services={opening.Services}; customers={first.Count}");
        Check("rule.same_day_same_customers", Describe(first) == Describe(again), Describe(first));

        bool fromAssortment = true, cashSmall = true, plain = true, fewLines = true;
        foreach (var order in first)
        {
            foreach (var line in order.Lines) fromAssortment &= opening.Assortment.Contains(line.Product);
            cashSmall &= order.Payment != PaymentMethod.Cash || order.TotalPrice <= OrderGenerator.CashCeiling;
            plain &= order.Special == Order.None && Mathf.Approximately(order.TimeLimitSeconds, OrderGenerator.Limit);
            fewLines &= order.Lines.Count >= 1 && order.Lines.Count <= 2;
        }
        Check("rule.opening_orders_plain", fromAssortment && cashSmall && plain && fewLines, $"from assortment={fromAssortment}; cash small={cashSmall}; no requests or rush={plain}; 1-2 items={fewLines}");

        // A store with everything: the largest day there is.
        var full = new ShiftPlan { Day = 9, OnePlus = true };
        full.Assortment.AddRange(opening.Assortment);
        foreach (var item in Game.UnlockItems)
        {
            if (item.Kind == UnlockKind.Product) full.OrderedProducts++;
            if (item.Product != null && !full.Assortment.Contains(item.Product)) full.Assortment.Add(item.Product);
            if (item.Id == UnlockCatalog.Microwave) full.MicrowaveItem = item.Product;
            if (item.Id == UnlockCatalog.HotWater) full.HotWaterItem = item.Product;
        }
        var busy = OrderGenerator.Build(full);
        int expected = Mathf.Min(OrderGenerator.MaxCustomers, OrderGenerator.BaseCustomers + 9 / OrderGenerator.ProductsPerCustomer + 3);
        int rush = 0, microwave = 0, hotWater = 0, onePlus = 0;
        bool requestsRight = busy.Count > 0 && busy[0].Special == Order.None, cashSmallBusy = true;
        for (int i = 0; i < busy.Count; i++)
        {
            var order = busy[i];
            if (Mathf.Approximately(order.TimeLimitSeconds, OrderGenerator.RushLimit)) rush++;
            if (Mathf.Approximately(order.TimeLimitSeconds, OrderGenerator.RushLimit) != (i >= busy.Count - OrderGenerator.RushCustomers)) requestsRight = false;
            if (order.Special == Order.Microwave) { microwave++; requestsRight &= order.Lines[0].Product == full.MicrowaveItem; }
            if (order.Special == Order.HotWater) { hotWater++; requestsRight &= order.Lines[0].Product == full.HotWaterItem; }
            if (order.Special == Order.OnePlus) { onePlus++; requestsRight &= order.Lines.Exists(line => line.Quantity >= 2); }
            cashSmallBusy &= order.Payment != PaymentMethod.Cash || order.TotalPrice <= OrderGenerator.CashCeiling;
        }
        Check("rule.full_store_is_busy", full.Assortment.Count == 16 && busy.Count == expected && expected == 13 && rush == OrderGenerator.RushCustomers,
            $"assortment={full.Assortment.Count}; customers={busy.Count} (expected {expected}); rush={rush}");
        Check("rule.requests_follow_equipment", microwave >= 1 && hotWater >= 1 && onePlus >= 1 && requestsRight && cashSmallBusy,
            $"microwave={microwave}; hot water={hotWater}; 1+1={onePlus}; right product first and rush last={requestsRight}; cash small={cashSmallBusy}");
        full.Day = 10;
        Check("rule.next_day_differs", Describe(OrderGenerator.Build(full)) != Describe(busy), "day 9 against day 10");
        Check("rule.customers_capped", OrderGenerator.CustomersFor(100, 3) == OrderGenerator.MaxCustomers && OrderGenerator.CustomersFor(0, 0) == OrderGenerator.BaseCustomers,
            $"{OrderGenerator.CustomersFor(0, 0)} to {OrderGenerator.CustomersFor(100, 3)}");
        Check("rule.budget", GameManager.BudgetFor(15000, 1200, "A") == 5200 && GameManager.BudgetFor(15000, 1200, "S") == 6200 && GameManager.BudgetFor(15000, 0, "C") == 3000 && GameManager.BudgetFor(0, 0, "") == 0,
            $"A={GameManager.BudgetFor(15000, 1200, "A")}; S={GameManager.BudgetFor(15000, 1200, "S")}; C={GameManager.BudgetFor(15000, 0, "C")}");

        int products = 0, equipment = 0, total = 0;
        foreach (var item in Game.UnlockItems)
        {
            if (item.Kind == UnlockKind.Product) products++; else equipment++;
            total += item.Price;
        }
        Check("rule.catalog", products == 9 && equipment == 7 && total == 71000, $"products={products}; equipment={equipment}; total={total}");

        // The two lines this flow added have a voice: the disposal step of the training and the hand-over of the ordering.
        var audio = Game.Pos.Audio;
        int managerLines = Enum.GetValues(typeof(ManagerLine)).Length;
        bool voiced = audio != null && audio.IntakeLines.Length == 5 && audio.IntakeLines[4] != null && audio.IntakeLines[4].name == "voice_intake_5"
            && audio.ManagerLines.Length > (int)ManagerLine.Graduated && audio.ManagerLines[(int)ManagerLine.Graduated] != null && audio.ManagerLines[(int)ManagerLine.Graduated].name == "voice_manager_graduated";
        Check("rule.new_lines_voiced", voiced, audio == null ? "no audio" : $"intake lines={audio.IntakeLines.Length}; manager lines={audio.ManagerLines.Length} of {managerLines}");
    }

    static string Describe(List<Order> orders)
    {
        var parts = new List<string>();
        foreach (var order in orders)
        {
            var lines = new List<string>();
            foreach (var line in order.Lines) lines.Add($"{line.Product.Data.Id}x{line.Quantity}");
            parts.Add($"{order.Sequence}:{order.Payment}:{(order.NeedsBag ? 1 : 0)}:{order.Special}:{order.TimeLimitSeconds:0}:{string.Join("+", lines)}");
        }
        return string.Join(" ", parts);
    }

    void SaveRoundTrip()
    {
        string path = Path.Combine(output, "roundtrip_progress.json");
        var written = new StoreProgress { day = 7, graduated = true, budget = 1234, totalSales = 98700, daysWorked = 6, Path = path };
        written.owned.Add(UnlockCatalog.Cutter);
        written.arriving.Add(UnlockCatalog.Music);
        written.Save();
        var read = StoreProgress.Load(path);
        Check("save.round_trip", File.Exists(path) && read.day == 7 && read.graduated && read.budget == 1234 && read.totalSales == 98700 && read.daysWorked == 6
                && read.Has(UnlockCatalog.Cutter) && read.Ordered(UnlockCatalog.Music) && !read.Has(UnlockCatalog.Music) && read.Started,
            $"day={read.day}; budget={read.budget}; owned={string.Join(",", read.owned)}; arriving={string.Join(",", read.arriving)}");
        File.Delete(path);
        var missing = StoreProgress.Load(path);
        Check("save.missing_file_is_a_new_store", !missing.Started && missing.day == 1 && missing.budget == 0, $"day={missing.day}; started={missing.Started}");
    }

    // ---------- The scene ----------

    IEnumerator TitleToCounter()
    {
        var title = Game.Title;
        yield return Wait(() => Game.State == DayState.Title && title.PanelShown, 20f, "title.shown");
        if (finished) yield break;
        Check("title.new_store_offers_one_start", title.StartLabel == "출근하기" && title.StartButton.gameObject.activeInHierarchy && !title.FreshButton.gameObject.activeInHierarchy,
            $"start={title.StartLabel}; fresh shown={title.FreshButton.gameObject.activeInHierarchy}");
        Check("title.store_opens_sparse", DecorShown() == 0 && !PartShown("Microwave Top") && !PartShown("Dispenser Body") && !PartShown("MicrowaveZone") && !PartShown("WaterDispenserZone"),
            $"display shelves stocked={DecorShown()}/{Intake.DecorSlots.Length}; microwave={PartShown("Microwave Top")}; dispenser={PartShown("Dispenser Body")}");
        yield return Capture("g01_title_new_store");

        title.MoreButton.onClick.Invoke();
        yield return null;
        Check("title.skip_in_place_of_day_2_and_3", title.SkipButton.gameObject.activeInHierarchy && !title.Day2Button.gameObject.activeInHierarchy && !title.Day3Button.gameObject.activeInHierarchy,
            $"skip={title.SkipButton.gameObject.activeInHierarchy}; day2={title.Day2Button.gameObject.activeInHierarchy}");
        yield return Capture("g02_title_settings");
        title.SkipButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Commute, 5f, "title.skip_chosen");
        if (finished) yield break;
        int first = GameManager.TrainingDays + 1;
        Check("skip.store_starts_after_training", title.Choice == TitleChoice.SkipTraining && Game.Day == first && Game.Progress.day == first && Game.Progress.graduated && Game.Progress.budget == GameManager.GraduationGrant,
            $"choice={title.Choice}; day={Game.Day}; graduated={Game.Progress.graduated}; budget={Game.Progress.budget}");
        Check("skip.saved_at_once", File.Exists(Game.Progress.Path) && StoreProgress.Load(Game.Progress.Path).day == first, Game.Progress.Path);

        // The walk in and the time card are the title check's business; here the player is simply put through them.
        yield return new WaitForSeconds(1.6f);
        title.PlacePlayer(title.CounterSpot);
        yield return Wait(() => Game.State == DayState.PunchIn, 10f, "commute.at_the_counter");
        if (finished) yield break;
        yield return PutCardIn(() => Game.State != DayState.PunchIn, "punch_in");
    }

    IEnumerator FirstStoreDay()
    {
        int day = GameManager.TrainingDays + 1;
        yield return Wait(() => Game.Day == day && Game.State == DayState.Shift, 30f, "day4.shift");
        if (finished) yield break;
        Check("day4.opening_store", Game.OrderCount == OrderGenerator.BaseCustomers && Intake.Active && Intake.Boxes.Count == 2 && Intake.ExpiredTotal == 3 && DecorShown() == 0
                && Intake.Boxes[0].CheckpointCount == StockBox.DefaultCheckpoints && Mathf.Approximately(scanner.MaxBarcodeAngle, 50f) && !desk.AutoCoins
                && Mathf.Approximately(CustomerDirector.LineGraceSeconds, CustomerDirector.DefaultLineGrace),
            $"orders={Game.OrderCount}; boxes={Intake.Boxes.Count}; expired={Intake.ExpiredTotal}; shelves={DecorShown()}; checkpoints={Intake.Boxes[0].CheckpointCount}; scan angle={scanner.MaxBarcodeAngle}; coins={desk.AutoCoins}; line grace={CustomerDirector.LineGraceSeconds}");
        musicWasSilent = !Game.Pos.Audio.MusicOn && !Game.Pos.Audio.MusicPlaying;
        Check("day4.no_equipment_to_see_yet", LooksShown() == 0 && !PartShown("Microwave Sign") && !PartShown("Dispenser Sign"),
            $"equipment looks shown={LooksShown()}/4; microwave sign={PartShown("Microwave Sign")}; hot water sign={PartShown("Dispenser Sign")}");
        Check("day4.terminal_dark_in_the_shift",!Game.Tablet.Shown && !Game.LeaveButton.gameObject.activeInHierarchy, $"terminal={Game.Tablet.Shown}");
        yield return CaptureFrom("g03_day4_display_shelves_empty", ShelfView(0), ShelfTarget(0), 60f);

        var seen = new List<Order>();
        yield return Serve(OrderGenerator.BaseCustomers, "day4", seen);
        if (finished) yield break;
        var opening = Game.PlanFor(day).Assortment;
        bool fromStore = seen.Count == OrderGenerator.BaseCustomers;
        foreach (var order in seen)
            foreach (var line in order.Lines) fromStore &= opening.Contains(line.Product);
        Check("day4.customers_buy_what_the_store_sells", fromStore, $"orders seen={seen.Count}; assortment={opening.Count}");

        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "day4.settled");
        if (finished) yield break;
        int earned = GameManager.BudgetFor(Game.Director.Sales, Game.Director.Tips, Game.Grade);
        Check("day4.budget_earned", Game.LastEarned == earned && earned > 0 && Game.Progress.budget == GameManager.GraduationGrant + earned && Game.BoardText.Contains("발주 예산") && Game.BoardText.Contains($"{earned:N0}"),
            $"sales={Game.Director.Sales}; tips={Game.Director.Tips}; grade={Game.Grade}; earned={Game.LastEarned} (rule {earned}); budget={Game.Progress.budget}; board={Game.BoardText.Replace("\n", " / ")}");
        var saved = StoreProgress.Load(Game.Progress.Path);
        Check("day4.saved", saved.day == day + 1 && saved.budget == Game.Progress.budget && saved.daysWorked == 1 && saved.totalSales == Game.Director.Sales,
            $"day={saved.day}; budget={saved.budget}; worked={saved.daysWorked}; sales={saved.totalSales}");
    }

    IEnumerator OrderingTerminal()
    {
        var tablet = Game.Tablet;
        var progress = Game.Progress;
        Check("terminal.lit_after_the_day", tablet.Shown && tablet.Tab == UnlockKind.Product && tablet.SummaryText == "구색 5/14  ·  설비 0/7" && tablet.BudgetText == $"{progress.budget:N0}원"
                && Game.NextDayButton.gameObject.activeInHierarchy && Game.NextDayButton.GetComponentInChildren<UnityEngine.UI.Text>().text == "5일차 출근",
            $"shown={tablet.Shown}; summary={tablet.SummaryText}; budget={tablet.BudgetText}; next={Game.NextDayButton.GetComponentInChildren<UnityEngine.UI.Text>().text}");
        yield return CaptureFrom("g04_terminal_products", TerminalView(), tablet.transform.position, 38f);

        // Two products, as a player could afford them after one day.
        int before = progress.budget;
        tablet.BuyButtons[0].onClick.Invoke();
        tablet.BuyButtons[1].onClick.Invoke();
        yield return null;
        var firstProduct = Game.UnlockItems[0];
        Check("terminal.products_ordered", progress.budget == before - 6000 && progress.arriving.Count == 2 && progress.owned.Count == 0 && tablet.RowText(0).Contains("입고 예정")
                && !tablet.BuyButtons[0].gameObject.activeSelf && tablet.NoticeText.Contains("박스로") && StoreProgress.Load(progress.Path).arriving.Count == 2,
            $"budget {before} -> {progress.budget}; arriving={string.Join(",", progress.arriving)}; row0={tablet.RowText(0)}; notice={tablet.NoticeText}");
        Check("terminal.sample_on_the_counter", Game.Samples[0] != null && Game.Samples[1] != null && Game.Samples[0].Data.Id == firstProduct.Id && DecorShown() == 0,
            $"samples={(Game.Samples[0] != null ? Game.Samples[0].Data.Id : "none")},{(Game.Samples[1] != null ? Game.Samples[1].Data.Id : "none")}; shelves still empty={DecorShown() == 0}");

        tablet.EquipmentTab.onClick.Invoke();
        yield return null;
        // Too dear after two products: nothing is charged and the terminal says how much is missing.
        int coinsRow = RowOf(UnlockCatalog.Coins);
        int short1 = progress.budget;
        tablet.BuyButtons[coinsRow].onClick.Invoke();
        yield return null;
        Check("terminal.cannot_overspend", progress.budget == short1 && !progress.Ordered(UnlockCatalog.Coins) && tablet.NoticeText.Contains("모자랍니다") && !tablet.BuyButtons[coinsRow].interactable,
            $"budget={progress.budget}; notice={tablet.NoticeText}; button interactable={tablet.BuyButtons[coinsRow].interactable}");
        yield return CaptureFrom("g05_terminal_equipment", TerminalView(), tablet.transform.position, 38f);

        // The rest of the check needs every piece of equipment, which a first day cannot pay for: the budget is topped up.
        progress.budget += 50000;
        tablet.Refresh();
        foreach (var id in new[] { UnlockCatalog.HotWater, UnlockCatalog.Microwave, UnlockCatalog.OnePlus, UnlockCatalog.Cutter, UnlockCatalog.Scanner, UnlockCatalog.Music, UnlockCatalog.Coins })
            tablet.BuyButtons[RowOf(id)].onClick.Invoke();
        yield return null;
        Check("terminal.equipment_ordered", progress.arriving.Count == 9 && tablet.SummaryText == "구색 7/14  ·  설비 7/7" && !PartShown("Microwave Top") && Mathf.Approximately(scanner.MaxBarcodeAngle, 50f),
            $"arriving={progress.arriving.Count}; summary={tablet.SummaryText}; nothing in the store yet: microwave={PartShown("Microwave Top")}, scan angle={scanner.MaxBarcodeAngle}");

        Game.NextDayButton.onClick.Invoke();
    }

    IEnumerator SecondStoreDay()
    {
        int day = GameManager.TrainingDays + 2;
        yield return Wait(() => Game.Day == day && Game.State != DayState.DayEnd, 10f, "day5.begins");
        if (finished) yield break;
        yield return null;
        var progress = Game.Progress;
        Check("day5.orders_arrived", progress.owned.Count == 9 && progress.arriving.Count == 0 && Game.Samples[0] == null && Game.Samples[1] == null
                && Game.BoardText.Contains("새 상품 입고") && Game.BoardText.Contains("진열해야"),
            $"owned={progress.owned.Count}; arriving={progress.arriving.Count}; board={Game.BoardText.Replace("\n", " / ")}");
        // The two products ordered yesterday are boxes on the cart, and their shelves stand bare and marked until stocked.
        var first = Game.UnlockItems[0];
        var second = Game.UnlockItems[1];
        var firstSlot = Intake.SlotFor(first.Slot);
        var secondSlot = Intake.SlotFor(second.Slot);
        var firstBox = Intake.Boxes.Find(box => box.Delivery.Product == first.Product);
        var secondBox = Intake.Boxes.Find(box => box.Delivery.Product == second.Product);
        Check("day5.new_products_come_in_boxes", Game.BoxesToday.Count == 2 && Intake.Boxes.Count == 2 && firstBox != null && secondBox != null && firstBox.Delivery.CaseQuantity == 3 && firstBox.Units.Length == 3
                && Intake.Today.Count == 4 && Intake.Today[2].ArriveAfterOrder == 2 && Intake.Today[3].ArriveAfterOrder == 4,
            $"boxes for new products={Game.BoxesToday.Count}; on the cart={Intake.Boxes.Count}; units={(firstBox != null ? firstBox.Units.Length : 0)}; the day's boxes={Intake.Today.Count}; the usual two come after customers {(Intake.Today.Count == 4 ? $"{Intake.Today[2].ArriveAfterOrder} and {Intake.Today[3].ArriveAfterOrder}" : "?")}");
        Check("day5.shelves_wait_for_them", DecorShown() == 0 && SlotShown(first.Slot) && SlotShown(second.Slot) && firstSlot.IsTarget && secondSlot.IsTarget && firstSlot.Filled == 0 && firstSlot.Zone.enabled
                && firstSlot.HighlightShown && !progress.Shelved(first.Id) && !progress.Shelved(second.Id),
            $"display shelves stocked={DecorShown()}; {first.Slot} shown={SlotShown(first.Slot)}, target={firstSlot.IsTarget}, filled={firstSlot.Filled}, zone={firstSlot.Zone.enabled}, highlight={firstSlot.HighlightShown}; on sale yet={progress.Shelved(first.Id)}");
        var plan = Game.PlanFor(day);
        var generated = OrderGenerator.Build(plan);
        int askedForFirst = 0, askedForSecond = 0;
        bool newOnlyLater = true;
        for (int i = 0; i < generated.Count; i++)
            for (int line = 0; line < generated[i].Lines.Count; line++)
            {
                var product = generated[i].Lines[line].Product;
                if (product != first.Product && product != second.Product) continue;
                if (product == first.Product) askedForFirst++; else askedForSecond++;
                newOnlyLater &= i >= OrderGenerator.NewFromCustomer && line > 0;
            }
        Check("day5.customers_ask_for_them_later", plan.NewToday.Count == 2 && !plan.Assortment.Contains(first.Product) && askedForFirst >= 1 && askedForSecond >= 1 && newOnlyLater,
            $"new today={plan.NewToday.Count}; orders with {first.Id}={askedForFirst}, with {second.Id}={askedForSecond}; only from customer {OrderGenerator.NewFromCustomer + 1} on and never first in the basket={newOnlyLater}");

        // The first is put on its shelf (the box opened and its three placed without hands); the second stays in its box.
        firstBox.ForceOpen();
        yield return null;
        for (int unit = 0; unit < firstBox.Units.Length; unit++) Intake.Shelve(firstBox.Units[unit].Product, firstSlot, unit);
        yield return new WaitForSeconds(0.6f);
        var kept = StoreProgress.Load(progress.Path);
        Check("day5.shelved_is_on_sale", firstSlot.Filled == 3 && firstSlot.Merged != null && firstSlot.Merged.ShownUnits == 3 && !firstSlot.HighlightShown && firstBox.Stage == BoxStage.Empty
                && progress.Shelved(first.Id) && !progress.Shelved(second.Id) && kept.Shelved(first.Id) && Intake.Shelved == 3 && DecorShown() == 1,
            $"{first.Slot} filled={firstSlot.Filled}, drawn={(firstSlot.Merged != null ? firstSlot.Merged.ShownUnits : 0)}; box={firstBox.Stage}; on sale={progress.Shelved(first.Id)} (saved={kept.Shelved(first.Id)}); {second.Id} on sale={progress.Shelved(second.Id)}; shelved today={Intake.Shelved}");
        Check("day5.equipment_in_place", PartShown("Microwave Top") && PartShown("MicrowaveZone") && PartShown("Dispenser Body") && PartShown("WaterDispenserZone") && !PartShown("Microwave Lit") && !PartShown("Dispenser Water")
                && Mathf.Approximately(scanner.MaxBarcodeAngle, GameManager.NewScannerAngle) && desk.AutoCoins && Mathf.Approximately(CustomerDirector.LineGraceSeconds, GameManager.MusicLineGrace)
                && Game.Pos.Audio.MusicOn,
            $"microwave={PartShown("Microwave Top")}; dispenser={PartShown("Dispenser Body")}; lamp off={!PartShown("Microwave Lit")}; scan angle={scanner.MaxBarcodeAngle}; coins={desk.AutoCoins}; line grace={CustomerDirector.LineGraceSeconds}; music on={Game.Pos.Audio.MusicOn} (playing={Game.Pos.Audio.MusicPlaying})");

        // Loaded in the background, so it may start a moment after the store opens.
        var music = Game.Pos.Audio;
        float musicWait = Time.time;
        while (!music.MusicPlaying && Time.time - musicWait < 5f) yield return null;
        Check("day5.music_plays", music.Music != null && music.Music.name == "shop_theme" && music.MusicPlaying && musicWasSilent,
            $"clip={(music.Music != null ? music.Music.name : "none")}; length={(music.Music != null ? music.Music.length : 0f):0.0}s; playing={music.MusicPlaying}; silent before it was bought={musicWasSilent}");

        yield return Wait(() => Game.State == DayState.Shift, 30f, "day5.shift");
        if (finished) yield break;
        int expected = OrderGenerator.CustomersFor(2, 3);
        Check("day5.busier_store", Game.OrderCount == expected && expected == 10 && Intake.Boxes.Count == 2 && Intake.Boxes[0].CheckpointCount == GameManager.NewCutterCheckpoints,
            $"orders={Game.OrderCount} (expected {expected}); boxes={Intake.Boxes.Count}; checkpoints={Intake.Boxes[0].CheckpointCount}");
        yield return CaptureFrom("g06_day5_display_shelves", ShelfView(0), ShelfTarget(0), 60f);
        var cabinetTop = cooking.Find("Cabinet Top");
        if (cabinetTop != null) yield return CaptureFrom("g07_day5_cooking_corner", cabinetTop.position + new Vector3(1.5f, 0.75f, 0f), cabinetTop.position + Vector3.up * 0.2f, 55f);

        // What was ordered is seen: the scanner's head, the coin dispenser by the tray, the speakers, the cutters' grips.
        Check("day5.equipment_seen", LooksShown() == 4 && PartShown("Microwave Sign") && PartShown("Dispenser Sign"),
            $"scanner={Game.LookShown(GameManager.ScannerLook)}; coins={Game.LookShown(GameManager.CoinLook)}; speakers={Game.LookShown(GameManager.MusicLook)}; cutters={Game.LookShown(GameManager.CutterLook)}; signs={PartShown("Microwave Sign")},{PartShown("Dispenser Sign")}");
        yield return CaptureFrom("g07b_day5_counter_equipment", new Vector3(-0.2f, 1.45f, 0.05f), new Vector3(-0.2f, 1.0f, 0.78f), 62f);
        yield return CaptureFrom("g07c_day5_ceiling_speakers", new Vector3(0.4f, 1.4f, 0.1f), new Vector3(0.5f, 3f, 1.9f), 60f);
        if (Intake.Cutters.Length > 0)
        {
            var cutter = Intake.Cutters[0].transform.position;
            yield return CaptureFrom("g07d_day5_new_cutter", cutter + new Vector3(0.05f, 0.3f, -0.4f), cutter, 30f);
        }

        var seen = new List<Order>();
        yield return Serve(expected, "day5", seen);
        if (finished) yield break;
        var assortment = plan.Assortment;
        bool fromStore = seen.Count == expected, boughtSecond = false;
        int requests = 0, rush = 0, boughtFirst = 0;
        foreach (var order in seen)
        {
            if (order.Special != Order.None) requests++;
            if (Mathf.Approximately(order.TimeLimitSeconds, OrderGenerator.RushLimit)) rush++;
            foreach (var line in order.Lines)
            {
                fromStore &= assortment.Contains(line.Product) || line.Product == first.Product;
                if (line.Product == first.Product) boughtFirst += line.Quantity;
                boughtSecond |= line.Product == second.Product;
            }
        }
        Check("day5.customers_follow_the_store", fromStore && assortment.Count == 7 && requests >= 3 && rush == OrderGenerator.RushCustomers,
            $"assortment={assortment.Count}; orders seen={seen.Count}; requests={requests}; rush customers={rush}");
        // What was shelved sold the same day; what stayed in its box was left out of the baskets that wanted it.
        Check("day5.shelved_sold_boxed_missed", boughtFirst == askedForFirst && Game.NewSoldToday == askedForFirst && !boughtSecond && Game.MissedNewToday == askedForSecond
                && Game.Director.Sales < Game.PossibleSales && secondSlot.Filled == 0 && !progress.Shelved(second.Id),
            $"{first.Id}: asked {askedForFirst}, sold {Game.NewSoldToday}; {second.Id}: asked {askedForSecond}, left out {Game.MissedNewToday}, in a basket={boughtSecond}; sales {Game.Director.Sales} of {Game.PossibleSales}");

        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "day5.settled");
        if (finished) yield break;
        Check("day5.settlement_says_what_the_order_did", Game.BoardText.Contains($"새 상품 {first.Name} · {second.Name}") && Game.BoardText.Contains($"{Game.NewSoldToday}개 판매") && Game.BoardText.Contains($"놓친 주문 {Game.MissedNewToday}건"),
            $"board={Game.BoardText.Replace("\n", " / ")}");
        yield return Capture("g07e_day5_settlement_new_products");
        Check("day5.terminal_remembers", Game.Tablet.Shown && Game.Tablet.SummaryText == "구색 7/14  ·  설비 7/7" && Game.Tablet.RowText(0).Contains("판매 중") && Game.Tablet.RowText(1).Contains("진열 대기")
                && !Game.Tablet.BuyButtons[0].gameObject.activeSelf && Game.Tablet.BuyButtons[2].gameObject.activeSelf,
            $"summary={Game.Tablet.SummaryText}; row0={Game.Tablet.RowText(0)}; row1={Game.Tablet.RowText(1)}; row2 can be ordered={Game.Tablet.BuyButtons[2].gameObject.activeSelf}");
    }

    /// <summary>The rest of the catalogue, ordered at the end of the second day; the budget is topped up for it.</summary>
    IEnumerator FillTheStore()
    {
        var tablet = Game.Tablet;
        var progress = Game.Progress;
        progress.budget += 30000;
        tablet.Refresh();
        for (int row = 0; row < OrderTablet.Rows; row++)
            if (tablet.BuyButtons[row].gameObject.activeSelf) tablet.BuyButtons[row].onClick.Invoke();
        yield return null;
        bool nothingLeft = true;
        foreach (var button in tablet.BuyButtons) nothingLeft &= !button.gameObject.activeSelf;
        Check("terminal.store_filled", progress.arriving.Count == 7 && progress.owned.Count == 9 && tablet.SummaryText == "구색 14/14  ·  설비 7/7" && tablet.NoticeText.Contains("더 들일 것이 없습니다") && nothingLeft && !progress.completed,
            $"arriving={progress.arriving.Count}; owned={progress.owned.Count}; summary={tablet.SummaryText}; notice={tablet.NoticeText}; nothing left to order={nothingLeft}; completed yet={progress.completed}");
    }

    /// <summary>
    /// The third day: the box left over comes again with the next one, both are shelved this time, and the day's customers
    /// buy from them. The six products still waiting for their boxes are then put on their shelves by the check itself,
    /// in place of three more days of shelving, so that the next morning is the one with everything in.
    /// </summary>
    IEnumerator BoxesComeAgain()
    {
        var title = Game.Title;
        var progress = Game.Progress;
        int day = GameManager.TrainingDays + 3;
        title.StartButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Commute, 5f, "continue.chosen");
        if (finished) yield break;
        Check("continue.same_store", title.Choice == TitleChoice.Continue && Game.Day == day && progress.owned.Count == 16, $"choice={title.Choice}; day={Game.Day}; owned={progress.owned.Count}");
        yield return new WaitForSeconds(1.6f);
        title.PlacePlayer(title.CounterSpot);
        yield return Wait(() => Game.State == DayState.PunchIn, 10f, "continue.at_the_counter");
        if (finished) yield break;
        yield return PutCardIn(() => Game.State != DayState.PunchIn, "punch_in_again");
        if (finished) yield break;
        yield return Wait(() => Game.Day == day && Game.BoardText.Contains($"{day}일차 출근"), 15f, "day6.begins");
        if (finished) yield break;

        var again = Game.UnlockItems[1];
        var next = Game.UnlockItems[2];
        Check("day6.left_over_box_is_back", Game.BoardText.Contains($"새 상품 입고  {again.Name} · {next.Name}") && Intake.Boxes.Count == 2 && Intake.SlotFor(again.Slot).IsTarget && Intake.SlotFor(next.Slot).IsTarget
                && DecorShown() == 1 && !progress.completed,
            $"board={Game.BoardText.Replace("\n", " / ")}; boxes={Intake.Boxes.Count}; shelves stocked={DecorShown()}");
        foreach (var item in new[] { again, next })
        {
            var box = Intake.Boxes.Find(candidate => candidate.Delivery.Product == item.Product);
            var slot = Intake.SlotFor(item.Slot);
            if (box == null || slot == null) continue;
            box.ForceOpen();
            yield return null;
            for (int unit = 0; unit < box.Units.Length; unit++) Intake.Shelve(box.Units[unit].Product, slot, unit);
        }
        yield return new WaitForSeconds(0.6f);
        yield return CaptureFrom("g09_day6_both_shelved", ShelfView(1), ShelfTarget(1), 60f);

        yield return Wait(() => Game.State == DayState.Shift, 30f, "day6.shift");
        if (finished) yield break;
        int expected = OrderGenerator.CustomersFor(3, 3);
        Check("day6.both_on_sale", progress.Shelved(again.Id) && progress.Shelved(next.Id) && DecorShown() == 3 && Game.OrderCount == expected && expected == 10,
            $"on sale: {again.Id}={progress.Shelved(again.Id)}, {next.Id}={progress.Shelved(next.Id)}; shelves stocked={DecorShown()}; orders={Game.OrderCount} (expected {expected})");
        var seen = new List<Order>();
        yield return Serve(expected, "day6", seen);
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "day6.settled");
        if (finished) yield break;
        Check("day6.nothing_missed", Game.NewSoldToday >= 2 && Game.MissedNewToday == 0 && Game.Director.Sales == Game.PossibleSales && Game.BoardText.Contains($"{Game.NewSoldToday}개 판매") && !Game.BoardText.Contains("놓친"),
            $"new products sold={Game.NewSoldToday}; left out={Game.MissedNewToday}; sales {Game.Director.Sales} of {Game.PossibleSales}; board={Game.BoardText.Replace("\n", " / ")}");

        // In place of three more days: the six products whose boxes have not come yet are put on their shelves.
        foreach (var item in Game.UnlockItems)
            if (item.Kind == UnlockKind.Product && !progress.Shelved(item.Id)) progress.shelved.Add(item.Id);
        progress.Save();
    }

    IEnumerator HomeAndBack()
    {
        var title = Game.Title;
        int next = GameManager.TrainingDays + 3;
        Game.LeaveButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Ending, 5f, "leave.punch_out");
        if (finished) yield break;
        Check("leave.terminal_off", !Game.Tablet.Shown && !Game.NextDayButton.gameObject.activeInHierarchy, $"terminal={Game.Tablet.Shown}");
        yield return PutCardIn(() => Game.State == DayState.Title, "punch_out");
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.Title && title.PanelShown, 30f, "leave.back_at_the_title");
        if (finished) yield break;
        // The player is stood at the door a moment after the title comes up.
        yield return new WaitForSeconds(1f);
        Check("title.store_to_go_back_to", title.StartLabel == "이어서 출근" && title.FreshButton.gameObject.activeInHierarchy && title.FreshLabel == "새로 시작" && Game.Progress.day == next && StoreProgress.Load(Game.Progress.Path).day == next,
            $"start={title.StartLabel}; fresh={title.FreshLabel}; day={Game.Progress.day}; file day={StoreProgress.Load(Game.Progress.Path).day}");
        // Behind the door the store is as it will be tomorrow morning. One product is on its shelf. The one left in its box
        // comes again, with the first of the seven ordered tonight: two boxes a day. The other six wait, their shelves unseen.
        var left = StoreProgress.Load(Game.Progress.Path);
        var items = Game.UnlockItems;
        bool nextTwo = Game.BoxesToday.Count == 2 && Game.BoxesToday[0] == items[1] && Game.BoxesToday[1] == items[2];
        Check("title.store_as_it_will_open", DecorShown() == 1 && PartShown("Microwave Top") && left.owned.Count == 9 && left.arriving.Count == 7 && left.shelved.Count == 1
                && nextTwo && SlotShown(items[1].Slot) && SlotShown(items[2].Slot) && !SlotShown(items[3].Slot) && Game.Progress.owned.Count == 16 && !Game.Progress.completed,
            $"shelves stocked={DecorShown()}; microwave={PartShown("Microwave Top")}; file owned={left.owned.Count}, arriving={left.arriving.Count}, shelved={left.shelved.Count}; tomorrow's boxes are the one left over and the next={nextTwo}; a waiting product's shelf shown={SlotShown(items[3].Slot)}");
        yield return Capture("g08_title_store_to_go_back_to");
    }

    /// <summary>Home again after the third day, and the store given up: starting over takes two presses.</summary>
    IEnumerator StartOver()
    {
        var title = Game.Title;
        Game.LeaveButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Ending, 5f, "leave_again.punch_out");
        if (finished) yield break;
        yield return PutCardIn(() => Game.State == DayState.Title, "punch_out_again");
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.Title && title.PanelShown, 30f, "leave_again.back_at_the_title");
        if (finished) yield break;
        yield return new WaitForSeconds(1f);
        // Everything is in and on its shelf: tomorrow is the morning that says so, and nothing more comes in a box.
        var filled = StoreProgress.Load(Game.Progress.Path);
        var tomorrow = Game.PlanFor(Game.Progress.day);
        Check("title.store_complete", Game.Progress.completed && DecorShown() == 9 && Game.BoxesToday.Count == 0 && LooksShown() == 4 && Intake.Boxes.Count == 2 && filled.shelved.Count == 9
                && tomorrow.Assortment.Count == 16 && tomorrow.NewToday.Count == 0 && OrderGenerator.CustomersFor(tomorrow.OrderedProducts, tomorrow.Services) == 13,
            $"completed={Game.Progress.completed}; shelves stocked={DecorShown()}; boxes of new products={Game.BoxesToday.Count}; the usual boxes={Intake.Boxes.Count}; equipment looks={LooksShown()}/4; file shelved={filled.shelved.Count}; tomorrow's assortment={tomorrow.Assortment.Count}, customers={OrderGenerator.CustomersFor(tomorrow.OrderedProducts, tomorrow.Services)}");
        yield return Capture("g10_title_store_complete");

        title.FreshButton.onClick.Invoke();
        yield return null;
        Check("title.fresh_asks_first", Game.State == DayState.Title && title.FreshLabel.Contains("지우고") && Game.Progress.owned.Count == 16, $"state={Game.State}; label={title.FreshLabel}; owned={Game.Progress.owned.Count}");
        title.FreshButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Commute, 5f, "title.fresh_chosen");
        if (finished) yield break;
        Check("fresh.new_store", title.Choice == TitleChoice.Fresh && Game.Day == 1 && Game.Progress.day == 1 && !Game.Progress.graduated && Game.Progress.budget == 0 && Game.Progress.owned.Count == 0
                && DecorShown() == 0 && !PartShown("Microwave Top") && Mathf.Approximately(scanner.MaxBarcodeAngle, 50f) && !StoreProgress.Load(Game.Progress.Path).Started
                && LooksShown() == 0 && !Game.Progress.completed && Game.Progress.shelved.Count == 0 && Game.BoxesToday.Count == 0,
            $"day={Game.Day}; budget={Game.Progress.budget}; owned={Game.Progress.owned.Count}; shelves={DecorShown()}; microwave={PartShown("Microwave Top")}; scan angle={scanner.MaxBarcodeAngle}; equipment looks={LooksShown()}; completed={Game.Progress.completed}");
    }

    // ---------- The three days of learning ----------

    IEnumerator Training()
    {
        var title = Game.Title;
        var progress = Game.Progress;
        var opening = Game.PlanFor(GameManager.TrainingDays + 1).Assortment;
        var seen = new List<Order>();
        yield return Wait(() => Game.State == DayState.Title && title.PanelShown, 20f, "title.shown");
        if (finished) yield break;
        title.StartButton.onClick.Invoke();
        yield return Wait(() => Game.State == DayState.Commute, 5f, "training.start_chosen");
        if (finished) yield break;
        Check("training.new_store_day_1", title.Choice == TitleChoice.Fresh && Game.Day == 1 && !progress.graduated && Game.IsTraining(1) && Game.Tutorial.ProductId == opening[0].Data.Id
                && Game.IntakeTutorial.TeachDisposal,
            $"choice={title.Choice}; day={Game.Day}; tutorial product={Game.Tutorial.ProductId}; disposal taught={Game.IntakeTutorial.TeachDisposal}");
        yield return new WaitForSeconds(1.6f);
        title.PlacePlayer(title.CounterSpot);
        yield return Wait(() => Game.State == DayState.PunchIn, 10f, "commute.at_the_counter");
        if (finished) yield break;
        yield return PutCardIn(() => Game.State != DayState.PunchIn, "punch_in");
        if (finished) yield break;

        // Day 1: the counter only.
        yield return Wait(() => Game.Day == 1 && Game.State == DayState.Shift, 30f, "t1.shift");
        if (finished) yield break;
        Check("t1.two_practice_customers", Game.OrderCount == 2 && Game.Director.Lenient && !Intake.Active && DecorShown() == 0 && !PartShown("Microwave Top"),
            $"orders={Game.OrderCount}; lenient={Game.Director.Lenient}; intake={Intake.Active}; shelves={DecorShown()}");
        yield return Serve(2, "t1", seen);
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "t1.done");
        if (finished) yield break;
        int earned1 = GameManager.BudgetFor(Game.Director.Sales, Game.Director.Tips, "");
        Check("t1.not_graded_but_earning", Game.Grade == "" && Game.BoardText.StartsWith("1일차 끝") && !Game.BoardText.Contains("정산") && Game.BoardText.Contains("발주 예산") && !Game.Tablet.Shown
                && progress.budget == earned1 && earned1 > 0 && progress.day == 2 && !progress.graduated && Game.NextDayButton.GetComponentInChildren<UnityEngine.UI.Text>().text == "2일차 출근",
            $"grade='{Game.Grade}'; board={Game.BoardText.Replace("\n", " / ")}; terminal={Game.Tablet.Shown}; budget={progress.budget} (rule {earned1})");
        yield return Capture("g10_training_day1_end");
        Game.NextDayButton.onClick.Invoke();

        // Day 2: one box and two expired products; nobody does the steps here, so the customers come after the guidance's limit.
        yield return Wait(() => Game.Day == 2 && Game.State == DayState.Tutorial, 15f, "t2.intake_guided");
        if (finished) yield break;
        yield return null;
        Check("t2.one_box_two_expired", Intake.Active && Intake.Boxes.Count == 1 && Intake.Boxes[0].Delivery.TargetSlot == "A-2" && Intake.ExpiredTotal == 2 && Game.OrderCount == 3,
            $"boxes={Intake.Boxes.Count}; expired={Intake.ExpiredTotal}; orders={Game.OrderCount}; board={Game.BoardText.Replace("\n", " / ")}");
        yield return Wait(() => Game.State == DayState.Shift, GameManager.IntakeTutorialMaxSeconds + 15f, "t2.shift");
        if (finished) yield break;
        yield return Serve(3, "t2", seen);
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "t2.done");
        if (finished) yield break;
        int earned2 = GameManager.BudgetFor(Game.Director.Sales, Game.Director.Tips, "");
        Check("t2.not_graded_but_earning", Game.Grade == "" && Game.BoardText.StartsWith("2일차 끝") && !Game.Tablet.Shown && progress.budget == earned1 + earned2 && progress.day == 3 && !progress.graduated,
            $"board={Game.BoardText.Replace("\n", " / ")}; budget={progress.budget} (rule {earned1 + earned2})");
        Game.NextDayButton.onClick.Invoke();

        // Day 3: alone, with the tip rule on the board, and at its end the store's ordering.
        yield return Wait(() => Game.Day == 3 && Game.State == DayState.Shift, 30f, "t3.shift");
        if (finished) yield break;
        Check("t3.alone_with_the_tip_rule", Game.OrderCount == 4 && Intake.Boxes.Count == 1 && Intake.Boxes[0].Delivery.TargetSlot == "B-1" && Intake.ExpiredTotal == 2 && Game.Director.Lenient && Game.BoardText.Contains("팁"),
            $"orders={Game.OrderCount}; boxes={Intake.Boxes.Count}; expired={Intake.ExpiredTotal}; board={Game.BoardText.Replace("\n", " / ")}");
        yield return Serve(4, "t3", seen);
        if (finished) yield break;
        yield return Wait(() => Game.State == DayState.DayEnd && Game.LeaveButton.gameObject.activeInHierarchy, 60f, "t3.done");
        if (finished) yield break;
        yield return new WaitForSeconds(0.3f);
        int earned3 = GameManager.BudgetFor(Game.Director.Sales, Game.Director.Tips, "");
        int total = earned1 + earned2 + earned3 + GameManager.GraduationGrant;
        var pointer = title.Pointer;
        Check("t3.graduated", progress.graduated && progress.day == 4 && progress.budget == total && Game.BoardText.StartsWith("수습 수료") && Game.BoardText.Contains("발주 단말기") && Game.BoardText.Contains($"{total:N0}")
                && Game.Tablet.Shown && Game.Tablet.NoticeText.Contains("골라") && Game.Manager.InStore && StoreProgress.Load(progress.Path).graduated,
            $"graduated={progress.graduated}; budget={progress.budget} (rule {total}); board={Game.BoardText.Replace("\n", " / ")}; terminal={Game.Tablet.Shown}; manager back={Game.Manager.InStore}");
        Check("t3.manager_hands_over_aloud", Game.Pos.Audio.LastNarration == "voice_manager_graduated", $"last narration={Game.Pos.Audio.LastNarration}");
        Check("t3.arrow_at_the_terminal", pointer != null && pointer.Shown && pointer.Label == "발주", pointer == null ? "no pointer" : $"shown={pointer.Shown}; label={pointer.Label}");
        bool fromStore = seen.Count == 9;
        foreach (var order in seen)
        {
            fromStore &= order.Special == Order.None;
            foreach (var line in order.Lines) fromStore &= opening.Contains(line.Product);
        }
        Check("training.customers_buy_what_the_store_sells", fromStore, $"orders={seen.Count}; assortment={opening.Count}");
        yield return Capture("g11_graduation_board");
        yield return CaptureFrom("g12_graduation_terminal", TerminalView(), Game.Tablet.transform.position, 60f);

        Game.Tablet.BuyButtons[0].onClick.Invoke();
        yield return null;
        yield return null;
        Check("t3.first_order", progress.arriving.Count == 1 && progress.budget == total - Game.UnlockItems[0].Price && !pointer.Shown, $"arriving={progress.arriving.Count}; budget={progress.budget}; arrow={pointer.Shown}");
        Game.NextDayButton.onClick.Invoke();

        // The first morning after: the store's own days, graded and with customers who can leave.
        yield return Wait(() => Game.Day == 4 && Game.State == DayState.Shift, 30f, "day4.shift_after_training");
        if (finished) yield break;
        Check("day4.the_store_is_the_players", !Game.Director.Lenient && !Game.IsTraining(4) && Game.OrderCount == OrderGenerator.BaseCustomers && Intake.Boxes.Count == 2 && Intake.ExpiredTotal == 3
                && !Game.Manager.InStore,
            $"lenient={Game.Director.Lenient}; orders={Game.OrderCount}; boxes={Intake.Boxes.Count}; expired={Intake.ExpiredTotal}; manager gone={!Game.Manager.InStore}");
        // What was ordered at the graduation is the first box of the morning; its shelf is bare and marked until it is stocked.
        var ordered = Game.UnlockItems[0];
        var itsBox = Intake.Boxes.Find(box => box.Delivery.Product == ordered.Product);
        var itsSlot = Intake.SlotFor(ordered.Slot);
        Check("day4.first_order_is_a_box_to_shelve", Game.BoxesToday.Count == 1 && itsBox != null && itsBox.Units.Length == 3 && DecorShown() == 0 && SlotShown(ordered.Slot) && itsSlot.IsTarget && itsSlot.Filled == 0
                && !progress.Shelved(ordered.Id) && Intake.Today.Count == 3 && Intake.Today[2].ArriveAfterOrder == 2,
            $"boxes of new products={Game.BoxesToday.Count}; box of {ordered.Id}={(itsBox != null ? itsBox.Units.Length : 0)} units; its shelf {ordered.Slot}: shown={SlotShown(ordered.Slot)}, target={itsSlot.IsTarget}, filled={itsSlot.Filled}; the day's boxes={Intake.Today.Count}");
        if (itsBox != null)
        {
            // The box of three beside one of the usual boxes, then opened, as the player finds them at the warehouse door.
            var cart = itsBox.transform.position;
            yield return CaptureFrom("g13_day4_box_of_a_new_product", cart + new Vector3(0.25f, 0.75f, 1.0f), cart + new Vector3(0.25f, 0.1f, 0f), 50f);
            itsBox.ForceOpen();
            yield return new WaitForSeconds(0.3f);
            yield return CaptureFrom("g14_day4_box_of_a_new_product_open", cart + new Vector3(0f, 0.7f, 0.55f), cart + new Vector3(0f, 0.15f, 0f), 50f);
            yield return CaptureFrom("g15_day4_shelf_waiting", ShelfView(0), ShelfTarget(0), 60f);
        }
    }

    // ---------- Helpers ----------

    /// <summary>Pays for each customer in turn through the POS, with every item counted as scanned so the sale is the full bill.</summary>
    IEnumerator Serve(int customers, string label, List<Order> seen)
    {
        var pos = Game.Pos;
        for (int number = 1; number <= customers && !finished; number++)
        {
            int n = number;
            yield return Wait(() => pos.Current != null && Game.Director.CustomerNumber == n, CustomerTimeout, $"{label}.customer{n}_at_counter", quiet: true);
            if (finished) yield break;
            yield return new WaitForSeconds(0.5f);
            var order = pos.Current;
            seen.Add(order);
            foreach (var line in pos.Lines) line.Scanned = line.Ordered;
            if (desk.AutoCoins && !coinsTried && order.Payment == PaymentMethod.Cash && !order.NeedsCooking && order.TotalPrice % 1000 != 0)
            {
                coinsTried = true;
                yield return CoinsFromTheDispenser(order);
            }
            if (pos.Current == order) pos.CompleteCard();
            yield return Wait(() => pos.Current == null, 5f, $"{label}.customer{n}_paid", quiet: true);
            if (finished) yield break;
        }
        Check($"{label}.all_served", Game.Director.Paid == customers && Game.Director.Left == 0, $"paid={Game.Director.Paid}/{customers}; left={Game.Director.Left}");
    }

    /// <summary>The customer's bill goes into the drawer; the coin part of the change must be on the tray by itself.</summary>
    IEnumerator CoinsFromTheDispenser(Order order)
    {
        foreach (var product in Game.Director.OrderProducts)
            if (product != null) Game.Pos.MarkBagged(product);
        var bill = desk.CustomerBill;
        if (bill == null)
        {
            Check("coins.bill_on_the_plate", false, "no bill for a cash order");
            yield break;
        }
        int change = bill.Value - order.TotalPrice, coins = change % 1000;
        int expected = coins / 500 + coins % 500 / 100;
        var body = bill.GetComponent<Rigidbody>();
        float begun = Time.time;
        while (desk.CustomerBill != null && Time.time - begun < 3f)
        {
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.position = desk.Drawer.transform.position;
            bill.transform.position = desk.Drawer.transform.position;
            yield return new WaitForFixedUpdate();
        }
        yield return new WaitForSeconds(0.6f);
        Check("coins.dispensed_onto_the_tray", desk.CustomerBill == null && desk.CoinsDispensed == expected && desk.Tray.Amount == coins,
            $"bill={bill.Value}; total={order.TotalPrice}; change={change}; coins due={coins} in {expected} pieces; dispensed={desk.CoinsDispensed}; on tray={desk.Tray.Amount}; bill taken={desk.CustomerBill == null}");
        // Change with no bills in it is all coins: the tray then matches by itself and the customer is done.
        if (change == coins) yield return Wait(() => Game.Pos.Current != order, 4f, "coins.all_coin_change_completes", quiet: false);
        else if (Game.Pos.Current == order) Game.Pos.CompleteCash();
    }

    /// <summary>The card is set into the terminal's slot. If the slot does not take a card put there like this, the game's own rescue lets the player through.</summary>
    IEnumerator PutCardIn(Func<bool> done, string label)
    {
        yield return Wait(() => desk.CustomerCard != null || done(), 5f, label + ".card_on_the_plate", quiet: true);
        if (finished) yield break;
        float begun = Time.time;
        while (!done() && desk.CustomerCard != null && Time.time - begun < 4f)
        {
            var body = desk.CustomerCard.GetComponent<Rigidbody>();
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.position = desk.CardSlot.transform.position;
            desk.CustomerCard.transform.position = desk.CardSlot.transform.position;
            yield return new WaitForFixedUpdate();
        }
        float placedFor = Time.time - begun;
        yield return Wait(done, 90f, label + ".done");
        Debug.Log($"[P2 Growth] {label}: {(Time.time - begun < 10f ? "the slot took the card" : "let through by the game's rescue")} after {Time.time - begun:0.0}s (card held in the slot for {placedFor:0.0}s)");
    }

    int RowOf(string equipmentId)
    {
        int row = 0;
        foreach (var item in Game.UnlockItems)
        {
            if (item.Kind != UnlockKind.Equipment) continue;
            if (item.Id == equipmentId) return row;
            row++;
        }
        return 0;
    }

    int DecorShown()
    {
        int shown = 0;
        foreach (var slot in Intake.DecorSlots)
            if (slot != null && slot.gameObject.activeInHierarchy && slot.Merged != null && slot.Merged.ShownUnits == slot.Capacity) shown++;
        return shown;
    }

    /// <summary>How many of the four pieces of equipment that only change a number are seen in the store.</summary>
    int LooksShown()
    {
        int shown = 0;
        foreach (var look in new[] { GameManager.ScannerLook, GameManager.CoinLook, GameManager.MusicLook, GameManager.CutterLook })
            if (Game.LookShown(look)) shown++;
        return shown;
    }

    bool SlotShown(string slotId)
    {
        var slot = Array.Find(Intake.DecorSlots, s => s != null && s.SlotId == slotId);
        return slot != null && slot.gameObject.activeInHierarchy;
    }

    bool PartShown(string partName)
    {
        var part = cooking.Find(partName);
        return part != null && part.gameObject.activeInHierarchy;
    }

    Vector3 ShelfTarget(int item)
    {
        var slot = Array.Find(Intake.DecorSlots, s => s != null && s.SlotId == Game.UnlockItems[item].Slot);
        return slot != null ? slot.transform.position + new Vector3(0.36f, -0.25f, 0f) : Vector3.zero;
    }

    /// <summary>From the aisle the shelf faces: display slots look toward -z.</summary>
    Vector3 ShelfView(int item) => ShelfTarget(item) + new Vector3(0f, 0.45f, -1.5f);

    Vector3 TerminalView() => new Vector3(0.25f, 1.5f, 0.15f);

    IEnumerator Wait(Func<bool> condition, float timeout, string label, bool quiet = false)
    {
        float begun = Time.realtimeSinceStartup;
        while (!condition())
        {
            if (Time.realtimeSinceStartup - begun > timeout)
            {
                Check(label, false, $"timed out after {timeout}s; state={Game.State}; day={Game.Day}; customer={Game.Director.CustomerNumber}; board={Game.BoardText.Replace("\n", " / ")}");
                Finish();
                yield break;
            }
            yield return null;
        }
        if (!quiet) Check(label, true, $"after {Time.realtimeSinceStartup - begun:0.0}s");
    }

    IEnumerator Capture(string shotName)
    {
        ScreenCapture.CaptureScreenshot(Path.Combine(output, shotName + ".png"), Screen.width < 1200 ? 2 : 1);
        yield return null;
        yield return null;
    }

    IEnumerator CaptureFrom(string shotName, Vector3 position, Vector3 lookAt, float fieldOfView)
    {
        var shot = new GameObject("Evidence Camera").AddComponent<Camera>();
        shot.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position));
        shot.fieldOfView = fieldOfView;
        shot.nearClipPlane = 0.05f;
        shot.depth = view.depth + 10f;
        yield return null;
        yield return Capture(shotName);
        Destroy(shot.gameObject);
        yield return null;
    }

    bool Check(string checkName, bool pass, string details)
    {
        report.stages.Add(new Stage { name = checkName, pass = pass, details = details, time = Time.realtimeSinceStartup - started });
        Debug.Log($"[P2 Growth] {(pass ? "PASS" : "FAIL")} {checkName}: {details}");
        return pass;
    }

    void Finish()
    {
        if (finished) return;
        finished = true;
        report.checks = report.stages.Count;
        report.failed = report.stages.FindAll(s => !s.pass).Count;
        report.pass = report.checks > 0 && report.failed == 0;
        report.unity = Application.unityVersion;
        report.runtime = Application.isEditor ? "Editor Play Mode" : "Player";
        report.meanDesktopFps = frames / Mathf.Max(0.01f, Time.realtimeSinceStartup - started);
        report.scope = "The growing store's rules and its flow from the title through two generated days, the ordering terminal and the save file. No hands: buttons through onClick, customers paid through the POS, the time card set in the slot. Nothing about reach, pressing, legibility in a headset, balance or fun. Prices and numbers are proposals.";
        File.WriteAllText(Path.Combine(output, "growth_report.json"), JsonUtility.ToJson(report, true));
        Debug.Log($"[P2 Growth] {(report.pass ? "PASS" : "FAIL")} checks={report.checks} failed={report.failed} fps={report.meanDesktopFps:0}");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(report.pass ? 0 : 1);
#else
        Application.Quit(report.pass ? 0 : 1);
#endif
    }
}
