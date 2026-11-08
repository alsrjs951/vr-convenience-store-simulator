using UnityEngine;

/// <summary>Doc 6.4 hot water: the scanned cup under the tap for 2 s, held or set down, with the sound and the stream of water.</summary>
public class WaterDispenserZone : CookZone
{
    /// <summary>The stream from the tap to the tray.</summary>
    public GameObject Stream;

    protected override string Request => Order.HotWater;
    protected override float Seconds => 2f;
    protected override bool MustBeLetGo => false;
    protected override string WaitingPrompt => "탭 아래에 대고 기다리세요";
    protected override string WorkingPrompt => "물 붓는 중";
    protected override string DonePrompt => "다 됐어요";

    protected override void ShowWorking(bool working)
    {
        if (Stream) Stream.SetActive(working);
    }
}
