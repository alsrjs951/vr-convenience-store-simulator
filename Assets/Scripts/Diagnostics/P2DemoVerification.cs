using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// -p2-demo-verify: the demo for a first-time player (GameManager.Demo, -demo), through the same virtual devices as the
/// other checks. Starting it over loads the scene again, which takes this check with it, so the check is run three times
/// by the game it checks and carries what it found from one run to the next in statics:
///   1. from the start: put at the counter with no title, the tutorial and the three customers by hand, the settlement,
///      then the "다시 하기" button;
///   2. everything as new, then F5 in the middle of the tutorial with the product in the hand;
///   3. everything as new again, the player moved away and put back at the counter by F6.
/// Simulator evidence only.
/// </summary>
public partial class P2DayVerification
{
    const int DemoCustomers = 3;
    const float AtTheSpotMeters = 0.05f, FacingDegrees = 5f;

    static int demoRuns;
    static readonly List<Stage> demoCarried = new List<Stage>();

    IEnumerator RunDemoCheck()
    {
        int run = ++demoRuns;
        report.stages.AddRange(demoCarried);
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;

        // Before anything moves the view: where the game put the player.
        Check($"demo.run{run}.at_the_counter_without_the_title",
            Game.Demo && !Game.UsesTitle && !Game.Growth && Game.State != DayState.Title && Game.State != DayState.Commute && Game.State != DayState.PunchIn
                && OffTheCounterSpot() < AtTheSpotMeters && Mathf.Abs(TurnedFromTheCounter()) < FacingDegrees && GameManager.DemoRestarts == run - 1,
            $"demo={Game.Demo}; title={Game.UsesTitle}; state={Game.State}; head {OffTheCounterSpot() * 100f:0.0} cm from the clerk's spot, turned {TurnedFromTheCounter():0.0} deg from the counter; started over {GameManager.DemoRestarts} time(s)");
        Check($"demo.run{run}.everything_as_new",
            Game.Day == 1 && Game.Results.Count == 0 && Game.Grade == "" && Game.Director.Paid == 0 && Game.Director.Sales == 0 && Game.Director.Tips == 0 && Game.Director.TillShort == 0
                && Game.Pos.Current == null && Game.Pos.CancelCount == 0 && (Game.State == DayState.Intro || Game.State == DayState.Tutorial) && StationLabel.Shown
                && !Game.NextDayButton.transform.parent.gameObject.activeInHierarchy && FindObjectsByType<GameManager>(FindObjectsSortMode.None).Length == 1,
            $"day={Game.Day}; results={Game.Results.Count}; grade='{Game.Grade}'; paid={Game.Director.Paid}; sales={Game.Director.Sales}; till short={Game.Director.TillShort}; order on the POS={Game.Pos.Current != null}; cancels={Game.Pos.CancelCount}; state={Game.State}; name tags on={StationLabel.Shown}; button shown={Game.NextDayButton.transform.parent.gameObject.activeInHierarchy}; customers in the store={Game.Director.Present.Count}; games in the scene={FindObjectsByType<GameManager>(FindObjectsSortMode.None).Length}; board={Game.BoardText.Replace("\n", " / ")}");

        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Check($"demo.run{run}.hands_answer", simulator.manipulatingRightController && simulator.mouseTransformationMode == UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulator.TransformationMode.Translate, $"mode={simulator.mouseTransformationMode}");

        if (run == 1) yield return DemoFromStartToTheButton();
        else if (run == 2) yield return DemoStartedOverMidTutorial();
        else yield return DemoPutBackAtTheCounter();
    }

    /// <summary>Flat distance of the head from the clerk's spot, and how far it is turned from facing the counter.</summary>
    float OffTheCounterSpot()
    {
        var off = Game.Title.Rig.Camera.transform.position - Game.Title.CounterSpot.position;
        off.y = 0f;
        return off.magnitude;
    }

    float TurnedFromTheCounter()
    {
        var look = Vector3.ProjectOnPlane(Game.Title.Rig.Camera.transform.forward, Vector3.up);
        return Vector3.SignedAngle(look, Game.Title.CounterSpot.forward, Vector3.up);
    }

    IEnumerator DemoFromStartToTheButton()
    {
        yield return Capture("demo_01_start");
        yield return Tutorial();
        if (finished) yield break;

        var director = Game.Director;
        var pos = Game.Pos;
        for (int number = 1; number <= DemoCustomers && !finished; number++)
        {
            if (number > 1)
            {
                yield return new WaitForSeconds(0.3f);
                CheckBetweenOrders(number - 1);
            }
            yield return WaitUntil(() => director.CustomerNumber == number && pos.Current != null, 40f, $"demo.order{number}.arrived");
            if (finished) yield break;
            var order = pos.Current;
            var buyer = director.AtCounter;
            Check($"demo.order{number}.is_the_demos", order.Id == $"d01_0{number}" && order.Day == 1 && Game.OrderCount == DemoCustomers,
                $"order {order.Id} of day {order.Day}; {order.Payment}; bag={order.NeedsBag}; customers today={Game.OrderCount}");
            yield return GoodsSetDown($"demo.order{number}", director.OrderProducts);
            var products = new List<ProductPrefab>(director.OrderProducts);
            for (int i = 0; i < products.Count && !finished; i++)
            {
                string label = $"demo.order{number}.item{i}";
                yield return GrabProduct(products[i], label);
                yield return TurnBarcodeDown(products[i]);
                yield return MoveHeldAbovePad(products[i]);
                yield return LowerUntilScanned(products[i], label + ".scan");
                if (order.NeedsBag) yield return PutInBag(products[i], label + ".bag");
                else yield return SetAside(products[i], i);
                yield return ResetHandRotation();
            }
            Check($"demo.order{number}.ready_for_payment", pos.ReadyForPayment, $"allScanned={pos.AllScanned} allBagged={pos.AllBagged} total={pos.Total}");
            if (order.Payment == PaymentMethod.Card)
            {
                yield return PayByCard(() => pos.Current == null, $"demo.order{number}.card");
            }
            else
            {
                int total = pos.Total;
                int bill = PaymentDesk.BillFor(total);
                yield return GiveBill($"demo.order{number}.bill");
                yield return GiveChange(bill - total, 0, false, () => pos.Current == null, $"demo.order{number}.change");
            }
            yield return WaitUntil(() => pos.Current == null, 10f, $"demo.order{number}.completed");
            yield return LeavesWithOrWithoutBag($"demo.order{number}", order, buyer);
        }
        if (finished) yield break;

        yield return WaitUntil(() => Game.State == DayState.DayEnd, 20f, "demo.day_end");
        yield return new WaitForSeconds(0.5f);
        var button = Game.NextDayButton;
        Check("demo.settlement_and_play_again",
            director.Paid == DemoCustomers && director.Left == 0 && director.Sales == Game.PossibleSales && Game.Grade != "" && Game.BoardText.Contains("1일차 정산") && Game.BoardText.Contains("시재 맞음")
                && button.transform.parent.gameObject.activeInHierarchy && button.GetComponentInChildren<UnityEngine.UI.Text>().text == "다시 하기",
            $"paid={director.Paid}/{Game.OrderCount}; sales={director.Sales}/{Game.PossibleSales}; grade={Game.Grade} {Game.Score:0}; tips={director.Tips}; button='{button.GetComponentInChildren<UnityEngine.UI.Text>().text}' shown={button.transform.parent.gameObject.activeInHierarchy}; board={Game.BoardText.Replace("\n", " / ")}");
        report.grade = Game.Grade;
        report.score = Game.Score;
        report.sales = director.Sales;
        report.possibleSales = Game.PossibleSales;
        yield return Capture("demo_02_settlement_and_play_again");

        // The button under the board, with the ray and the trigger as a player would.
        yield return MoveHand(new Vector3(0.1f, 1.25f, 0.35f), "demo.again.hand");
        var direction = (button.transform.position - rightHand.position).normalized;
        yield return RotateHand(new Vector3(-Mathf.Asin(direction.y) * Mathf.Rad2Deg, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 0f) - handEuler);
        yield return new WaitForSeconds(0.5f);
        yield return LeaveTheSceneBy(() => leftButton = true, "demo.play_again_button_starts_over");
    }

    IEnumerator DemoStartedOverMidTutorial()
    {
        Check("demo.play_again_button_started_over", GameManager.DemoRestarts == 1, $"started over {GameManager.DemoRestarts} time(s) after the button was pressed");
        var tutorial = Game.Tutorial;
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Grab && tutorial.Product != null, StepTimeout, "demo.run2.tutorial_runs_again");
        if (finished) yield break;
        // In the middle of things: the manager's first guest at the counter, their product in the clerk's hand.
        yield return GrabProduct(tutorial.Product, "demo.run2.product");
        Check("demo.run2.started_over_from_the_middle_of_the_tutorial", tutorial.Product.Grab.isSelected && tutorial.Guest != null && Game.State == DayState.Tutorial,
            $"product in the hand={tutorial.Product.Grab.isSelected}; guest there={tutorial.Guest != null}; state={Game.State}; step={tutorial.Step}");
        yield return Capture("demo_03_before_f5");
        yield return LeaveTheSceneBy(() => heldKeys.Add(Key.F5), "demo.f5_starts_over");
    }

    IEnumerator DemoPutBackAtTheCounter()
    {
        Check("demo.f5_started_over", GameManager.DemoRestarts == 2, $"started over {GameManager.DemoRestarts} time(s) after F5 was pressed");
        var tutorial = Game.Tutorial;
        yield return WaitUntil(() => tutorial.Step == TutorialStep.Grab && tutorial.Product != null, StepTimeout, "demo.run3.tutorial_runs_again");
        if (finished) yield break;
        yield return Capture("demo_04_after_f5");
        // As if whoever put the headset on stood somewhere else in the room: out by the door, facing another way.
        Game.Title.PlacePlayer(Game.Title.StandSpot);
        yield return null;
        float away = OffTheCounterSpot();
        yield return Tap(Key.F6);
        yield return null;
        Check("demo.f6_puts_the_player_at_the_counter", away > 1f && OffTheCounterSpot() < AtTheSpotMeters && Game.State == DayState.Tutorial && GameManager.DemoRestarts == 2,
            $"moved {away:0.00} m away, then F6: head {OffTheCounterSpot() * 100f:0.0} cm from the clerk's spot; state={Game.State}; started over {GameManager.DemoRestarts} time(s)");
        demoCarried.Clear();
        demoRuns = 0;
        Finish();
    }

    /// <summary>
    /// What was found so far goes into the statics, then the thing that starts the demo over is done. The scene is loaded
    /// again and this check with it, so nothing after that runs; if it does, the demo did not start over.
    /// </summary>
    IEnumerator LeaveTheSceneBy(System.Action press, string label)
    {
        demoCarried.Clear();
        demoCarried.AddRange(report.stages);
        press();
        // A button answers when the trigger is let go.
        yield return new WaitForSeconds(0.2f);
        leftButton = false;
        yield return new WaitForSecondsRealtime(8f);
        Check(label, false, $"the scene was not loaded again within 8 s; state={Game.State}; started over {GameManager.DemoRestarts} time(s)");
        demoCarried.Clear();
        demoRuns = 0;
        Finish();
    }
}
