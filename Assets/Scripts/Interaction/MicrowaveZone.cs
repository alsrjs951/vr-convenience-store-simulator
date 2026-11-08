using UnityEngine;

/// <summary>
/// Doc 6.4 microwave: the scanned product put down inside for 3 s, with the hum and the light, then the ding. The door opens
/// for an order that asks for heating and shuts while it runs.
/// </summary>
public class MicrowaveZone : CookZone
{
    const float DoorOpenDegrees = 105f;
    const float DoorDegreesPerSecond = 420f;

    /// <summary>The door's hinge. Zero rotation is shut.</summary>
    public Transform Door;
    /// <summary>The oven light and the warm glow in the window, on while it runs.</summary>
    public GameObject LitParts;

    protected override string Request => Order.Microwave;
    protected override float Seconds => 3f;
    protected override bool MustBeLetGo => true;
    protected override string WaitingPrompt => "안에 넣고 손을 놓으세요";
    protected override string WorkingPrompt => "데우는 중";
    protected override string DonePrompt => "다 됐어요. 꺼내세요";

    float doorAngle;

    protected override void ShowWorking(bool working)
    {
        if (LitParts) LitParts.SetActive(working);
    }

    protected override void OnDone()
    {
        if (Audio) Audio.PlayAt(StoreSound.Ding, transform.position);
    }

    void LateUpdate()
    {
        if (!Door) return;
        float target = OrderHasRequest && !IsWorking ? DoorOpenDegrees : 0f;
        doorAngle = Mathf.MoveTowards(doorAngle, target, DoorDegreesPerSecond * Time.deltaTime);
        Door.localRotation = Quaternion.Euler(0f, doorAngle, 0f);
    }
}
