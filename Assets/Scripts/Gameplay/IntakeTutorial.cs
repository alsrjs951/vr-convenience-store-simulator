using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public enum IntakeTutorialStep { NotStarted, Carry, Cut, Open, Shelve, Done }

/// <summary>
/// Doc 5.7.4: the four intake steps, on the first box of Day 2 only. Guidance is text on the guide board and the panels by
/// the shelves and the cart, plus narration I1 to I4. The first customer's timer starts when this is done or 90 s
/// after it began, whichever is first (GameManager waits on that).
/// </summary>
public class IntakeTutorial : MonoBehaviour
{
    const float CarryHintSeconds = 20f;
    const float FlapHintSeconds = 15f;
    const float ShelveHintSeconds = 15f;
    const int BeadsAfterResets = 2;
    const int AutoCutAfterResets = 4;

    public IntakeDirector Intake;
    public PlacementPreview Preview;
    public Text Board;

    public IntakeTutorialStep Step { get; private set; } = IntakeTutorialStep.NotStarted;
    public StockBox Box { get; private set; }
    public float StartedAt { get; private set; } = -1f;
    public float DoneAt { get; private set; } = -1f;
    public bool AutoCutUsed { get; private set; }
    public bool BeadsUsed { get; private set; }
    public int HintsShown { get; private set; }

    public IEnumerator Run()
    {
        StartedAt = Time.time;
        Box = Intake.Boxes.Count > 0 ? Intake.Boxes[0] : null;
        if (Box == null)
        {
            Step = IntakeTutorialStep.Done;
            DoneAt = Time.time;
            yield break;
        }
        string product = WorldUi.Clean(Box.Delivery.Product.Data.Name);
        StationLabel.ShowAll(true);
        Intake.ShowGuides(true);
        string slot = Box.Delivery.TargetSlot;
        string shelf = slot.Substring(0, 1);

        Step = IntakeTutorialStep.Carry;
        Narrate(1);
        Say($"입고 1/4  박스 옮기기\n카트의 {product} 박스 → 진열대 {shelf} 옆 거치대");
        float begun = Time.time;
        bool hinted = false;
        while (Box != null && Box.Place != BoxPlace.Docked)
        {
            if (!hinted && Box.Place == BoxPlace.Cart && Time.time - begun > CarryHintSeconds)
            {
                hinted = true;
                HintsShown++;
                Say($"입고 1/4  박스 옮기기\n옆면 검은 손잡이를 잡고 → {slot} 옆 거치대");
            }
            yield return null;
        }
        if (Box == null) yield break;

        Step = IntakeTutorialStep.Cut;
        Narrate(2);
        Say("입고 2/4  테이프 자르기\n커터로 끝에서 끝까지");
        while (Box != null && Box.Stage == BoxStage.Sealed)
        {
            if (Box.CutResets >= AutoCutAfterResets)
            {
                AutoCutUsed = true;
                Box.AutoCut();
                break;
            }
            if (Box.CutResets >= BeadsAfterResets && !BeadsUsed)
            {
                BeadsUsed = true;
                HintsShown++;
                Box.ShowBeads(true);
                Say("입고 2/4  테이프 자르기\n노란 구슬을 차례로, 멈추지 말고");
            }
            yield return null;
        }
        if (Box == null) yield break;
        Box.ShowBeads(false);

        Step = IntakeTutorialStep.Open;
        Narrate(3);
        Say("입고 3/4  박스 열기\n날개를 바깥으로 젖히기");
        begun = Time.time;
        hinted = false;
        while (Box != null && Box.Stage == BoxStage.TapeCut)
        {
            if (!hinted && Time.time - begun > FlapHintSeconds)
            {
                hinted = true;
                HintsShown++;
                Say("입고 3/4  박스 열기\n깜빡이는 날개 끝을 잡고 아래로");
            }
            yield return null;
        }
        if (Box == null) yield break;

        Step = IntakeTutorialStep.Shelve;
        Narrate(4);
        Say($"입고 4/4  진열\n상품을 들고 노란 칸({slot})을 바라보기");
        begun = Time.time;
        hinted = false;
        int shown = Preview != null ? Preview.GhostsShown : 0;
        while (Intake.Shelved == 0)
        {
            if (Preview != null && Preview.GhostsShown != shown) begun = Time.time;
            shown = Preview != null ? Preview.GhostsShown : 0;
            if (!hinted && Time.time - begun > ShelveHintSeconds)
            {
                hinted = true;
                HintsShown++;
                Say($"입고 4/4  진열\n노란 칸({slot}) 가까이 가져가기");
            }
            yield return null;
        }

        Step = IntakeTutorialStep.Done;
        DoneAt = Time.time;
        Say("입고 안내 끝\n나머지 진열과 폐기는 손님이 없을 때");
    }

    void Narrate(int step)
    {
        if (Intake != null && Intake.Audio != null) Intake.Audio.NarrateIntake(step);
    }

    void Say(string text)
    {
        if (Board) Board.text = text;
        Intake.SetGuide(text);
    }
}
