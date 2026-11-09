using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// -p2-title-verify: the title, the walk in, the punch-in, one short day, the punch-out, the ending and back to the title,
/// through the same virtual devices as the other checks. To keep it short the day is Day 1 alone with no tutorial and the
/// customers are paid for directly; the hand work at the counter is what -p2-verify covers. Simulator evidence only.
/// </summary>
public partial class P2DayVerification
{
    public TitleFlow Title;

    bool titleMode;
    bool faceMode;
    bool introMode;

    /// <summary>
    /// -p2-intro-verify: what a first-time player gets, from the title through the tutorial's two customers to the start of
    /// the shift, with nothing skipped. The pictures are what the player's view shows at each moment. Simulator evidence only.
    /// </summary>
    IEnumerator RunIntroCheck()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        rig = FindFirstObjectByType<XROrigin>().transform;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Game.LastDay = 1;

        var director = Game.Director;
        var manager = Game.Manager;
        yield return WaitUntil(() => Game.State == DayState.Title && Title.PanelShown, 8f, "intro.title_shown");
        if (finished) yield break;
        yield return WaitUntil(NearTitleSpot, 6f, "intro.player_at_door");
        if (finished) yield break;
        yield return new WaitForSeconds(2f);
        Check("intro.manager_in_the_store", manager != null && manager.InStore && manager.Body.FaceRig != null && manager.Body.FaceRig.HasFaceBones, manager == null || manager.Body == null ? "no manager" : $"face={manager.Body.FaceRig != null}");
        Check("intro.people_already_shopping", director.Ambient.Count == 2 && director.Present.Count == 0 && director.Records.Count == 0, $"shopping={director.Ambient.Count}; present={director.Present.Count}; records={director.Records.Count}");
        Check("intro.nobody_is_a_twin", TwinCount(manager, director) == 0, $"same avatar as another in the store: {TwinCount(manager, director)}");
        Check("intro.one_thing_to_press", Title.StartButton.gameObject.activeInHierarchy && !Title.Day2Button.gameObject.activeInHierarchy, "only 출근하기 and 설정 are shown");
        Check("intro.store_stocked_on_the_shelves_in_view", DecorPlaces() == 27, $"display-only places shown={DecorPlaces()}");
        yield return Capture("i01_title_through_glass");
        yield return PressButton(Title.StartButton, "intro.start");
        yield return WaitUntil(() => Game.State == DayState.Commute, 3f, "intro.commute");
        if (finished) yield break;
        yield return new WaitForSeconds(0.5f);
        var managerFace = manager.Body.FaceRig;
        Check("intro.manager_waves_and_speaks", manager.Waves == 1 && managerFace.Talking && manager.Body.Playing == CustomerCharacter.LeaveState, $"waves={manager.Waves}; talking={managerFace.Talking}; playing={manager.Body.Playing}");
        Check("intro.manager_looks_at_player", managerFace.GazeErrorDegrees < 35f, $"off by {managerFace.GazeErrorDegrees:0.0}°");
        yield return Capture("i02_door_opens_and_she_waves");
        yield return WaitUntil(() => Title.GuideShown, 3f, "intro.guide_shown");
        if (finished) yield break;
        var hands = Game.Tutorial.Hands;
        yield return null;
        Check("intro.how_to_walk_said_at_once", Title.GuideText.Contains("왼손 스틱") && hands.LeftText.Contains("스틱"), $"guide={Title.GuideText}; left={hands.LeftText}");
        yield return WalkInAndPunch();
        if (finished) yield break;
        Check("intro.manager_nods_at_the_punch", managerFace.Nods >= 1, $"nods={managerFace.Nods}");

        yield return WaitUntil(() => Game.State == DayState.Tutorial, 8f, "intro.tutorial_begins");
        if (finished) yield break;
        yield return new WaitForSeconds(1.5f);
        yield return Capture("i03_first_customer_coming");
        yield return Tutorial();
        if (finished) yield break;
        yield return null;
        Check("intro.done_message_seen", Game.BoardText.Contains("튜토리얼 완료") && Game.State == DayState.Tutorial && Game.Pos.Audio.LastNarration == "voice_manager_alone", $"board={Game.BoardText}; state={Game.State}; narration={Game.Pos.Audio.LastNarration}");
        yield return Capture("i09_tutorial_done");
        yield return WaitUntil(() => Game.State == DayState.Shift, 10f, "intro.shift_starts");
        if (finished) yield break;
        yield return new WaitForSeconds(0.5f);
        Check("intro.manager_goes_out", !manager.InStore, $"in store={manager.InStore}");
        Check("intro.shoppers_gone_or_served", director.Ambient.Count == 0, $"shopping={director.Ambient.Count}");
        yield return WaitUntil(() => manager.Body == null, 30f, "intro.manager_walked_out");
        Check("intro.nothing_left_on_the_counter", director.CounterBasket == null && FindObjectsByType<ProductPrefab>(FindObjectsSortMode.None).Length <= 2, $"basket={director.CounterBasket != null}");
        report.stuckWarps = CustomerCharacter.StuckWarps;
        Check("intro.no_warps", CustomerCharacter.StuckWarps == 0, $"warps={CustomerCharacter.StuckWarps}; stepped aside={CustomerCharacter.StepAsides}");
        Finish();
    }

    int DecorPlaces()
    {
        int places = 0;
        foreach (var slot in Intake.DecorSlots)
            if (slot != null && slot.Merged != null) places += slot.Merged.ShownUnits;
        return places;
    }

    static int TwinCount(ManagerNpc manager, CustomerDirector director)
    {
        int twins = 0;
        for (int i = 0; i < director.Ambient.Count; i++)
        {
            if (director.Ambient[i].Record.Avatar == manager.Prefab.name) twins++;
            for (int j = i + 1; j < director.Ambient.Count; j++)
                if (director.Ambient[i].Record.Avatar == director.Ambient[j].Record.Avatar) twins++;
        }
        return twins;
    }

    /// <summary>-p2-face-verify: the first customer at the counter, each thing the face can do, a close picture of it.</summary>
    IEnumerator RunFaceCheck()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        var director = Game.Director;
        yield return WaitUntil(() => Game.Pos.Current != null && director.AtCounter != null, 60f, "face.customer_at_counter");
        if (finished) yield break;
        yield return new WaitForSeconds(4f);
        var face = director.AtCounter.FaceRig;
        Check("face.bones_found", face != null && face.HasFaceBones, face == null ? "no face" : $"bones={face.HasFaceBones}");
        if (face == null) { Finish(); yield break; }
        Check("face.looks_at_clerk", face.GazeErrorDegrees < 25f, $"off by {face.GazeErrorDegrees:0.0}°; forward={face.FaceForward.ToString("F2")}");
        Check("face.blinks_on_its_own", face.Blinks >= 1, $"blinks so far={face.Blinks}");

        Vector3 Eye() => face.FacePoint + director.AtCounter.transform.forward * 0.62f;
        IEnumerator Shot(string name) => CaptureFrom(name, Eye(), face.FacePoint + Vector3.down * 0.04f, 32f);

        yield return Shot("f01_neutral");
        face.TestLids = 1f;
        yield return new WaitForSeconds(0.2f);
        yield return Shot("f02_eyes_closed");
        face.TestLids = -1f;
        var loud = new float[50];
        for (int i = 0; i < loud.Length; i++) loud[i] = 1f;
        face.Talk(loud, 3f);
        yield return new WaitForSeconds(0.4f);
        Check("face.jaw_opens_when_talking", face.Talking && face.JawAngle > 5f, $"jaw={face.JawAngle:0.0}°");
        yield return Shot("f03_mouth_open");
        yield return new WaitForSeconds(3f);
        Check("face.jaw_closes_after", face.JawAngle < 0.5f, $"jaw={face.JawAngle:0.0}°");
        face.SetMood(CustomerMood.Pleased);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("f04_pleased");
        face.SetMood(CustomerMood.Angry);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("f05_angry");
        face.SetMood(CustomerMood.Neutral);
        yield return new WaitForSeconds(0.5f);
        int nods = face.Nods;
        face.Nod();
        yield return new WaitForSeconds(0.2f);
        Check("face.nods", face.Nods == nods + 1, $"nods={face.Nods}");
        yield return Shot("f06_nod");
        yield return new WaitForSeconds(1f);
        yield return Capture("f07_from_the_clerk");
        Finish();
    }

    IEnumerator RunTitleCheck()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        rig = FindFirstObjectByType<XROrigin>().transform;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        Game.RunTutorial = false;
        Game.LastDay = 1;

        CheckScoringRules();
        yield return TitleScreen();
        if (finished) yield break;
        yield return WalkInAndPunch();
        if (finished) yield break;
        yield return ShortDay();
        if (finished) yield break;
        yield return EndingAndBack();
        if (finished) yield break;
        yield return NobodyMoves();
        Finish();
    }

    void CheckScoringRules()
    {
        Check("rules.satisfaction", Mathf.Approximately(CustomerDirector.SatisfactionFor(0.8f), 1f) && Mathf.Approximately(CustomerDirector.SatisfactionFor(0.5f), 1f)
                && Mathf.Approximately(CustomerDirector.SatisfactionFor(0.25f), 0.7f) && Mathf.Approximately(CustomerDirector.SatisfactionFor(0f), 0.4f),
            $"0.8->{CustomerDirector.SatisfactionFor(0.8f):0.00}, 0.25->{CustomerDirector.SatisfactionFor(0.25f):0.00}, 0->{CustomerDirector.SatisfactionFor(0f):0.00}");
        Check("rules.tip", CustomerDirector.TipFor(3300) == 300 && CustomerDirector.TipFor(1000) == 100 && CustomerDirector.TipFor(0) == 100,
            $"3300->{CustomerDirector.TipFor(3300)}, 1000->{CustomerDirector.TipFor(1000)}, 0->{CustomerDirector.TipFor(0)}");
        Check("rules.line_penalty", Mathf.Approximately(CustomerDirector.LinePenalty(45f, 120f), 0f) && Mathf.Approximately(CustomerDirector.LinePenalty(85f, 120f), 20f) && Mathf.Approximately(CustomerDirector.LinePenalty(500f, 90f), 36f),
            $"45s->{CustomerDirector.LinePenalty(45f, 120f):0.0}, 85s->{CustomerDirector.LinePenalty(85f, 120f):0.0}, 500s of 90s->{CustomerDirector.LinePenalty(500f, 90f):0.0}");
    }

    IEnumerator TitleScreen()
    {
        yield return WaitUntil(() => Game.State == DayState.Title && Title.PanelShown, 8f, "title.shown");
        if (finished) yield break;
        Check("title.nothing_started", Game.Tutorial.Step == TutorialStep.NotStarted && !Game.Director.ShiftRunning && Game.Director.Present.Count == 0 && Title.DoorClosed && !Game.BoardAnchor.Find("Guide Board").gameObject.activeSelf,
            $"tutorial={Game.Tutorial.Step}; shift={Game.Director.ShiftRunning}; customers={Game.Director.Present.Count}; door closed={Title.DoorClosed}");
        // The player is stood there half a game second after the title appears, and the first frames of Play are few and long.
        yield return WaitUntil(NearTitleSpot, 6f, "title.player_at_door");
        if (finished) yield break;
        yield return new WaitForSeconds(0.3f);
        Check("title.store_stocked_behind_door", Intake.DisplayCount > 0, $"display stock={Intake.DisplayCount}");
        yield return Capture("t01_title");

        // The door holds. Walking into it for a second stays on the porch; the view button brings the player back.
        yield return Stick(Key.I, 1.5f);
        yield return new WaitForSeconds(0.3f);
        Check("title.door_is_solid", view.transform.position.z < -1.65f, $"head z={view.transform.position.z:0.00}");
        Check("title.only_start_and_settings", Title.StartButton.gameObject.activeInHierarchy && Title.MoreButton.gameObject.activeInHierarchy && !Title.Day2Button.gameObject.activeInHierarchy && !Title.SpeedButton.gameObject.activeInHierarchy, "more settings stay folded");
        yield return PressButton(Title.MoreButton, "title.more");
        yield return new WaitForSeconds(0.1f);
        Check("title.settings_open", Title.MoreShown && Title.Day2Button.gameObject.activeInHierarchy && !Title.StartButton.gameObject.activeInHierarchy, $"more={Title.MoreShown}");
        yield return Capture("t01b_title_settings");
        yield return PressButton(Title.RecenterButton, "title.recenter");
        yield return new WaitForSeconds(0.2f);
        Check("title.recenter_returns", NearTitleSpot(), $"head={view.transform.position.ToString("F2")}");

        yield return PressButton(Title.SpeedButton, "title.speed");
        Check("title.slow_walk", Title.SlowWalk && Mathf.Approximately(Title.Move.moveSpeed, 1f) && Title.SpeedButton.GetComponentInChildren<Text>().text.Contains("느리게"), $"speed={Title.Move.moveSpeed}");
        yield return PressButton(Title.SpeedButton, "title.speed_back");
        Check("title.speed_back", !Title.SlowWalk && Mathf.Approximately(Title.Move.moveSpeed, 1.5f), $"speed={Title.Move.moveSpeed}");
        yield return PressButton(Title.VignetteButton, "title.vignette");
        Check("title.strong_vignette", Title.StrongVignette && Mathf.Approximately(Title.Vignette.defaultParameters.apertureSize, 0.5f), $"aperture={Title.Vignette.defaultParameters.apertureSize}");
        yield return PressButton(Title.VignetteButton, "title.vignette_back");
        Check("title.vignette_back", !Title.StrongVignette && Mathf.Approximately(Title.Vignette.defaultParameters.apertureSize, 0.7f), $"aperture={Title.Vignette.defaultParameters.apertureSize}");
        yield return PressButton(Title.BackButton, "title.back");
        yield return new WaitForSeconds(0.1f);
        Check("title.settings_closed", !Title.MoreShown && Title.StartButton.gameObject.activeInHierarchy, $"more={Title.MoreShown}");

        yield return PressButton(Title.StartButton, "title.start");
        yield return WaitUntil(() => Game.State == DayState.Commute, 3f, "title.start_pressed");
        Check("title.day_one_chosen", Title.ChosenDay == 1 && Game.Day == 1 && !Title.PanelShown, $"chosen={Title.ChosenDay}; day={Game.Day}; panel={Title.PanelShown}");
    }

    bool NearTitleSpot()
    {
        var offset = view.transform.position - Title.StandSpot.position;
        offset.y = 0f;
        var look = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up);
        return offset.magnitude < 0.1f && Vector3.Angle(look, Title.StandSpot.forward) < 5f;
    }

    /// <summary>The ray and trigger, as for the POS [-]: the hand in front of the chest, turned toward the middle of the button.</summary>
    IEnumerator PressButton(Button button, string label)
    {
        var rect = (RectTransform)button.transform;
        var target = rect.TransformPoint(rect.rect.center);
        yield return MoveHand(view.transform.position + view.transform.forward * 0.35f + view.transform.right * 0.1f + Vector3.down * 0.25f, label + ".hand");
        var direction = rig.InverseTransformDirection((target - rightHand.position).normalized);
        float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Asin(direction.y) * Mathf.Rad2Deg;
        yield return RotateHand(new Vector3(pitch, yaw, 0f) - handEuler);
        yield return new WaitForSeconds(0.5f);
        leftButton = true;
        yield return new WaitForSeconds(0.2f);
        leftButton = false;
        yield return new WaitForSeconds(0.3f);
    }

    IEnumerator WalkInAndPunch()
    {
        yield return WaitUntil(() => !Title.DoorClosed, 4f, "walkin.door_opened");
        if (finished) yield break;
        yield return new WaitForSeconds(0.3f);
        Check("walkin.path_and_guide", Title.CommutePath.activeSelf && Title.GuideShown && Game.Pos.Audio.LastNarration == "voice_manager_welcome",
            $"path={Title.CommutePath.activeSelf}; guide={Title.GuideShown}; narration={Game.Pos.Audio.LastNarration}");
        yield return Capture("t02_door_open");
        yield return WalkTo(new Vector3(-3.6f, 0f, -1f), "walkin.through_door");
        yield return Capture("t03_walking_in");
        yield return WalkTo(new Vector3(-2f, 0f, -0.3f), "walkin.along_the_street_front");
        yield return WalkTo(new Vector3(-0.45f, 0f, 0f), "walkin.to_the_ring");
        yield return WaitUntil(() => Game.State == DayState.PunchIn, 6f, "walkin.arrived_at_counter");
        if (finished) yield break;
        Check("walkin.no_rescue_needed", !Title.WalkGaveUp && !Title.CommutePath.activeSelf && !Title.GuideShown, $"gave up={Title.WalkGaveUp}");
        yield return ResetHandRotation();
        // The title faces the player toward +z and walking does not turn the rig, so the counter is already in front.
        Check("punch.facing_counter", view.transform.forward.z > 0.9f, $"forward={view.transform.forward.ToString("F2")}");

        yield return WaitUntil(() => Desk.CustomerCard != null, 3f, "punch.card_on_plate");
        if (finished) yield break;
        Check("punch.time_card", Desk.CustomerCard.GetComponentInChildren<Text>().text == "출근 카드" && Game.BoardText.Contains("출근 카드") && Game.Pos.Audio.LastNarration == "voice_manager_punch_in",
            $"label={Desk.CustomerCard.GetComponentInChildren<Text>().text}; board={Game.BoardText}; narration={Game.Pos.Audio.LastNarration}");
        yield return Capture("t04_punch_in");
        yield return PayByCard(() => Game.State != DayState.PunchIn, "punch_in");
        Check("punch.done_no_rescue", !Title.PunchGaveUp && Desk.CustomerCard == null, $"gave up={Title.PunchGaveUp}");
    }

    IEnumerator ShortDay()
    {
        var director = Game.Director;
        var watch = FindFirstObjectByType<WristWatch>();
        yield return WaitUntil(() => Game.State == DayState.Shift, 10f, "day.shift_starts");
        if (finished) yield break;
        Check("day.tip_rule_on_board", Game.BoardText.Contains("팁"), $"board={Game.BoardText}");
        for (int number = 1; number <= 5 && !finished; number++)
        {
            int n = number;
            yield return WaitUntil(() => Game.Pos.Current != null && director.CustomerNumber == n, 90f, $"day.customer{n}_at_counter");
            if (finished) yield break;
            yield return new WaitForSeconds(0.6f);
            if (number == 1)
            {
                Check("watch.shows_customer", watch != null && watch.Shown && watch.Summary.Contains("손님 1") && watch.Summary.Contains("1일차") && watch.Pulses >= 1, watch == null ? "no watch" : $"{watch.Summary}; pulses={watch.Pulses}");
                var wrist = watch.transform;
                yield return CaptureFrom("t05_wrist_watch", wrist.position - wrist.forward * 0.28f, wrist.position, 40f);
                yield return Capture("t05b_counter_with_customer");
                var looking = director.AtCounter.FaceRig;
                Check("customer.looks_at_clerk", looking != null && looking.HasFaceBones && looking.GazeErrorDegrees < 35f, looking == null ? "no face" : $"off by {looking.GazeErrorDegrees:0.0}°");
            }
            var served = director.AtCounter;
            int spoken = Game.Pos.Audio.SpokenCount(CustomerLine.ThanksFast);
            Game.Pos.CompleteCard();
            yield return WaitUntil(() => Game.Pos.Current == null, 3f, $"day.customer{n}_paid");
            yield return null;
            var record = director.Records.Find(r => r.ForOrder && r.Sequence == n);
            Check($"day.customer{n}_rewarded", record != null && record.Tip > 0 && Mathf.Approximately(record.Satisfaction, 1f) && director.LastOutcome.Contains("팁") && Game.Pos.Audio.SpokenCount(CustomerLine.ThanksFast) == spoken + 1,
                record == null ? "no record" : $"tip={record.Tip}; satisfaction={record.Satisfaction:0.00}; share left={record.ShareLeft:0.00}; card={director.LastOutcome}");
            Check($"day.customer{n}_nods_and_thanks", served != null && served.FaceRig != null && served.FaceRig.Nods == 1 && served.FaceRig.Talking && served.FaceRig.Mood == CustomerMood.Pleased,
                served == null || served.FaceRig == null ? "no face" : $"nods={served.FaceRig.Nods}; talking={served.FaceRig.Talking}; mood={served.FaceRig.Mood}");
            if (number == 1) yield return Capture("t06_outcome_card");
        }
    }

    IEnumerator EndingAndBack()
    {
        var director = Game.Director;
        yield return WaitUntil(() => Game.State == DayState.Ending, 30f, "ending.reached_after_last_day");
        if (finished) yield break;
        yield return WaitUntil(() => Desk.CustomerCard != null, 3f, "ending.card_on_plate");
        if (finished) yield break;
        Check("ending.punch_out_asked", director.HudText.Contains("퇴근 카드") && Desk.CustomerCard.GetComponentInChildren<Text>().text == "퇴근 카드" && Game.BoardText.Contains("근무 리포트") && Game.Pos.Audio.LastNarration == "voice_manager_punch_out",
            $"hud={director.HudText}; board={Game.BoardText}; narration={Game.Pos.Audio.LastNarration}");
        yield return Capture("t07_punch_out");
        yield return PayByCard(() => Game.BoardText.Contains("종합"), "punch_out");
        if (finished) yield break;
        yield return new WaitForSeconds(0.2f);
        Check("ending.matches_grade", Game.FinalGrade != "" && Game.BoardText.Contains($"종합 {Game.FinalGrade}") && Game.Pos.Audio.LastNarration.StartsWith("voice_ending_", StringComparison.Ordinal)
                && Game.NextDayButton.transform.parent.gameObject.activeSelf && Game.NextDayButton.GetComponentInChildren<Text>().text == "처음으로",
            $"grade={Game.FinalGrade}; board={Game.BoardText}; narration={Game.Pos.Audio.LastNarration}");
        yield return Capture("t08_ending");

        yield return PressButton(Game.NextDayButton, "ending.restart");
        yield return WaitUntil(() => Game.State == DayState.Title && Title.PanelShown, 5f, "restart.title_again");
        if (finished) yield break;
        yield return new WaitForSeconds(1f);
        Check("restart.fresh", Title.DoorClosed && NearTitleSpot() && Game.Results.Count == 0 && Game.DaysFinished == 0 && Game.Day == 1 && Title.ChosenDay == 0
                && director.Tips == 0 && director.Paid == 0 && director.Sales == 0 && Game.FinalGrade == "" && !Game.NextDayButton.transform.parent.gameObject.activeSelf,
            $"door={Title.DoorClosed}; at door={NearTitleSpot()}; results={Game.Results.Count}; day={Game.Day}; tips={director.Tips}; grade={Game.FinalGrade}");
        yield return Capture("t09_title_again");
    }

    /// <summary>Someone who chooses a later day, then does not move or grip: the day is set up, and nobody is left stuck.</summary>
    IEnumerator NobodyMoves()
    {
        yield return ResetHandRotation();
        yield return PressButton(Title.MoreButton, "restart.more");
        yield return new WaitForSeconds(0.1f);
        yield return PressButton(Title.Day2Button, "restart.day2");
        yield return WaitUntil(() => Game.State == DayState.Commute, 3f, "restart.day2_pressed");
        if (finished) yield break;
        Check("restart.day2_store_ready", Title.ChosenDay == 2 && Game.Day == 2 && Intake.Active && Intake.Boxes.Count == 2 && Intake.DisplayCount == 0 && Intake.ExpiredTotal == 3,
            $"chosen={Title.ChosenDay}; day={Game.Day}; boxes={Intake.Boxes.Count}; displays={Intake.DisplayCount}; expired={Intake.ExpiredTotal}");
        yield return WaitUntil(() => Game.State == DayState.PunchIn, 85f, "rescue.walk_given_up");
        if (finished) yield break;
        Check("rescue.placed_at_counter", Title.WalkGaveUp && Vector3.Distance(new Vector3(view.transform.position.x, 0f, view.transform.position.z), Vector3.zero) < 0.1f, $"gave up={Title.WalkGaveUp}; head={view.transform.position.ToString("F2")}");
        yield return WaitUntil(() => Game.State != DayState.PunchIn, 80f, "rescue.punch_given_up");
        Check("rescue.punch_let_through", Title.PunchGaveUp && Desk.CustomerCard == null, $"gave up={Title.PunchGaveUp}; state={Game.State}");
    }
}
