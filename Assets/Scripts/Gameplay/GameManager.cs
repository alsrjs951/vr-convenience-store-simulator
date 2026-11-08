using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public enum DayState { Intro, Tutorial, Shift, Settlement, DayEnd }

/// <summary>
/// Runs the days: Intro, the Day 1 tutorial or the Day 2 intake tutorial, Shift, then Settlement (doc 6.2, game design 11).
/// After a day's settlement a "N일차 시작" button under the board starts the next day (user decision 2026-11-08: a button
/// pressed with the ray and trigger, like the POS [-]). -start-day 2 starts at Day 2.
/// </summary>
public class GameManager : MonoBehaviour
{
    const float IntroSeconds = 3f;
    const float BoardMetersPerPixel = 0.0009f;
    // Doc 5.7.4: the first customer comes when the intake tutorial is done, or this long after it began.
    public const float IntakeTutorialMaxSeconds = 90f;

    public int Day = 1;
    public int LastDay = 2;
    public TextAsset Scenario;
    /// <summary>More rows of the same schema (doc 6.5), for later days.</summary>
    public TextAsset[] MoreScenarios = new TextAsset[0];
    public ProductCatalog Catalog;
    public POSController Pos;
    public CustomerDirector Director;
    public TutorialController Tutorial;
    public IntakeDirector Intake;
    public IntakeTutorial IntakeTutorial;
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
    public int DaysFinished { get; private set; }
    public float ShiftStartedAt { get; private set; } = -1f;
    public string BoardText => board != null ? board.text : "";
    public Button NextDayButton { get; private set; }
    public int NextDayPresses { get; private set; }

    Text board;
    Text nextDayLabel;
    bool nextDayPressed;

    void Awake()
    {
        var panel = WorldUi.CreatePanel(BoardAnchor, "Guide Board", BoardAnchor.position, BoardAnchor.rotation, new Vector2(1500f, 220f), BoardMetersPerPixel, WorldUi.Palette.Panel);
        board = WorldUi.AddText(panel, 40f, 18f, 1420f, 190f, 46, TextAnchor.MiddleCenter);
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
    }

    IEnumerator Start()
    {
        while (true)
        {
            yield return RunDay(Day);
            DaysFinished++;
            if (Day >= LastDay || ParseOrders(Day + 1).Count == 0) yield break;
            yield return WaitForNextDay(Day + 1);
            Day++;
        }
    }

    System.Collections.Generic.List<Order> ParseOrders(int day)
    {
        var text = Scenario != null ? Scenario.text : "";
        foreach (var more in MoreScenarios)
            if (more != null) text += "\n" + more.text;
        return DayScenarioLoader.Parse(text, day, Catalog);
    }

    IEnumerator WaitForNextDay(int next)
    {
        nextDayPressed = false;
        nextDayLabel.text = $"{next}일차 시작";
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
        foreach (var order in orders)
            foreach (var line in order.Lines)
                PossibleSales += line.Product.Data.Price * line.Quantity;
        if (day > 1 || DaysFinished > 0)
        {
            Pos.ResetDay();
            Director.ResetDay();
        }
        bool intake = false;
        if (Intake != null)
        {
            Intake.BeginDay(day, orders.Count);
            intake = Intake.Active;
        }

        board.text = intake ? $"{day}일차 출근\n입고 · 진열 · 폐기가 더해집니다" : $"{day}일차 출근\n계산대에서 손님을 맞습니다";
        if (intake) Intake.SetGuide(board.text);
        yield return new WaitForSeconds(IntroSeconds);

        if (day == 1 && RunTutorial)
        {
            State = DayState.Tutorial;
            yield return Tutorial.Run();
        }

        if (intake && IntakeTutorial != null)
        {
            // Doc 5.7.4: the first box gets four steps of guidance; customers start when they are done or after 90 s.
            State = DayState.Tutorial;
            StartCoroutine(IntakeTutorial.Run());
            float begun = Time.time;
            while (IntakeTutorial.Step != IntakeTutorialStep.Done && Time.time - begun < IntakeTutorialMaxSeconds) yield return null;
        }
        // Name tags and the intake panels by the shelves are for learning; the shift itself runs on the board and voice.
        StationLabel.ShowAll(false);
        if (intake) Intake.ShowGuides(false);

        State = DayState.Shift;
        ShiftStartedAt = Time.time;
        if (!intake) board.text = $"{day}일차 근무 중";
        else if (IntakeTutorial == null || IntakeTutorial.Step == IntakeTutorialStep.Done)
            Say($"{day}일차 근무 중\n손님이 없을 때 진열 · 폐기");
        yield return Director.RunDay(orders);

        State = DayState.Settlement;
        if (intake) Intake.EndDay();
        CalculateScore(day, intake);
        if (intake)
        {
            board.fontSize = 36;
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점{(Director.EndedEarly ? "   (이탈로 조기 마감)" : "")}\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}\n" +
                         $"진열 {Intake.Shelved}/{Intake.DeliveredTotal} · 폐기 {Intake.Disposed}/{Intake.ExpiredTotal} · 오진열 {Intake.Misplaced} · 폐기 오탐 {Intake.FalseDisposals} · 빈 박스 {Intake.UnrecycledEmptyBoxes}";
            Intake.SetGuide(board.text);
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount} shelved={Intake.Shelved}/{Intake.DeliveredTotal} disposed={Intake.Disposed}/{Intake.ExpiredTotal} misplaced={Intake.Misplaced} falseDisposals={Intake.FalseDisposals} unrecycled={Intake.UnrecycledEmptyBoxes} endedEarly={Director.EndedEarly}");
        }
        else
        {
            board.text = $"{day}일차 정산   {Grade}  ·  {Score:0}점\n" +
                         $"매출 {Director.Sales:N0} / {PossibleSales:N0}원 · 손님 {Director.Paid}/{OrderCount} · 이탈 {Director.Left}명 · 취소 {Pos.CancelCount}";
            Debug.Log($"[Game] Settlement day={day} grade={Grade} score={Score:0.0} sales={Director.Sales}/{PossibleSales} paid={Director.Paid}/{OrderCount} left={Director.Left} cancels={Pos.CancelCount}");
        }
        State = DayState.DayEnd;
    }

    void Say(string text)
    {
        board.text = text;
        if (Intake != null) Intake.SetGuide(text);
    }

    /// <summary>Game design 11 [제안]: Day 1 without tasks, Days 2-3 with the task rate and the intake penalties.</summary>
    void CalculateScore(int day, bool intake)
    {
        SalesRate = PossibleSales > 0 ? (float)Director.Sales / PossibleSales : 0f;
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
            Score = 40f * SalesRate + 25f * service + 15f * Accuracy + 20f * TaskRate;
        }
        Grade = Score >= 90f ? "S" : Score >= 75f ? "A" : Score >= 60f ? "B" : "C";
    }
}
