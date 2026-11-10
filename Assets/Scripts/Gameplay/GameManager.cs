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
    public int Shelved, Delivered, Disposed, Expired, Tips;
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
    bool nextDayPressed;
    Coroutine intakeTutorialRun, briefMessage;
    string shiftText = "";
    int peakStartsAt = -1;
    int preparedDay;

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

        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-start-day");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int start) && start >= 1) Day = start;
        UsesTitle = Title != null && !Array.Exists(args, argument => Array.IndexOf(NoTitleArguments, argument) >= 0);
        Director.OrderArrived += OnOrderArrived;
        Director.OrderEnded += OnOrderEnded;
    }

    void OnDestroy()
    {
        if (Director == null) return;
        Director.OrderArrived -= OnOrderArrived;
        Director.OrderEnded -= OnOrderEnded;
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
            yield return RunDays();
            yield return RunEnding();
            ResetRun();
        }
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
        // Behind the closed door the store is already stocked, so the first look through it is the finished store.
        PrepareStore(1);
        // People are already there behind the glass: the manager at the counter's end and a couple of customers shopping.
        if (Manager != null) Manager.Arrive();
        Director.StartAmbient(AmbientCustomers);
        yield return Title.ChooseDay();
        Day = Title.ChosenDay;
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
        Intake.BeginDay(day, ParseOrders(day).Count);
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
        board.fontSize = 46;
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

        board.text = !intake ? $"{day}일차 출근\n계산대에서 손님을 맞습니다"
            : day > IntakeTutorialDay ? $"{day}일차 출근\n피크 타임 · 특수 요청 · 중간 입고"
            : $"{day}일차 출근\n입고 · 진열 · 폐기가 더해집니다";
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
        shiftText = intake ? $"{day}일차 근무 중\n손님이 없을 때 진열 · 폐기" : $"{day}일차 근무 중\n시간 막대가 절반을 넘기 전에 끝내면 팁";
        if (!intake) board.text = shiftText;
        else if (!guidedIntake || IntakeTutorial.Step == IntakeTutorialStep.Done) Say(shiftText);
        yield return Director.RunDay(orders);

        State = DayState.Settlement;
        // A tutorial nobody finished must not go on writing on the board through the next day.
        if (intakeTutorialRun != null) StopCoroutine(intakeTutorialRun);
        intakeTutorialRun = null;
        if (intake) Intake.EndDay();
        CalculateScore(day, intake);
        RecordResult(day, intake);
        if (intake)
        {
            board.fontSize = 36;
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점{(Director.EndedEarly ? "   (이탈로 조기 마감)" : "")}\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}\n" +
                         $"손님 만족 {Satisfaction * 100f:0}% · 팁 {Director.Tips:N0}원\n" +
                         $"진열 {Intake.Shelved}/{Intake.DeliveredTotal} · 폐기 {Intake.Disposed}/{Intake.ExpiredTotal} · 오진열 {Intake.Misplaced} · 폐기 오탐 {Intake.FalseDisposals} · 빈 박스 {Intake.UnrecycledEmptyBoxes}";
            Intake.SetGuide(board.text);
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount} shelved={Intake.Shelved}/{Intake.DeliveredTotal} disposed={Intake.Disposed}/{Intake.ExpiredTotal} misplaced={Intake.Misplaced} falseDisposals={Intake.FalseDisposals} unrecycled={Intake.UnrecycledEmptyBoxes} endedEarly={Director.EndedEarly}");
        }
        else
        {
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}\n" +
                         $"손님 만족 {Satisfaction * 100f:0}% · 팁 {Director.Tips:N0}원";
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount}");
        }
        State = DayState.DayEnd;
    }

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
            Tips = Director.Tips, Satisfaction = Satisfaction,
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
        }
        float average = Results.Count > 0 ? scores / Results.Count : 0f;
        FinalAverage = average;
        FinalGrade = GradeFor(average);

        var text = new StringBuilder(board.text);
        text.Append($"\n\n{Results.Count}일 근무 리포트   {GradeFor(average)}  ·  평균 {average:0}점\n");
        foreach (var result in Results)
            text.Append($"{result.Day}일차  {result.Grade}  {result.Score:0}점  ·  매출 {result.Sales:N0} / {result.PossibleSales:N0}원  ·  손님 {result.Paid}/{result.Orders}\n");
        text.Append($"합계 매출 {total.Sales:N0} / {total.PossibleSales:N0}원 · 손님 {total.Paid}/{total.Orders} · 이탈 {total.Left}명 · 취소 {total.Cancels} · 진열 {total.Shelved}/{total.Delivered} · 폐기 {total.Disposed}/{total.Expired} · 팁 {total.Tips:N0}원");

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
            Accuracy = Mathf.Max(0f, 1f - 0.1f * Pos.CancelCount);
            TaskRate = 1f;
            Score = 50f * SalesRate + 30f * service + 20f * Accuracy;
        }
        else
        {
            int mistakes = Pos.CancelCount + Intake.FalseDisposals + Intake.Misplaced + Intake.UnrecycledEmptyBoxes;
            Accuracy = Mathf.Max(0f, 1f - 0.1f * mistakes);
            TaskRate = Intake.TaskRate;
            Score = 40f * SalesRate + 25f * Satisfaction + 15f * Accuracy + 20f * TaskRate;
        }
        Grade = GradeFor(Score);
    }
}
