using UnityEngine;

/// <summary>
/// The bag a customer walks out with (2026-11-11): the goods that went into the counter's bag, now hanging from the left
/// hand, the right being the one that waves. It is not a child of the hand, or it would turn with the wrist; it follows
/// the hand, hangs straight down with its broad side along the leg, and trails a little behind the hand as it swings.
/// </summary>
public class CarriedBag : MonoBehaviour
{
    // How far the bag's foot trails behind for each metre per second the hand moves, and the most it ever does.
    const float TrailSeconds = 0.16f, MaxTrail = 0.28f;
    // How quickly it takes up a new trail: quick enough to swing with a step, slow enough not to twitch.
    const float Settling = 7f;
    // From the knuckles to where the fingers close on the handles; from the wrist when the body has no finger bones.
    const float BelowKnuckles = 0.03f, BelowWrist = 0.09f;
    // Clear of the trouser leg.
    const float AwayFromBody = 0.03f;
    // A body without a skeleton (the placeholder capsule): beside it at hand height.
    static readonly Vector3 BesidePlaceholder = new Vector3(-0.28f, 0.8f, 0f);

    public CustomerCharacter Carrier { get; private set; }

    Transform hand;
    bool knuckles;
    Vector3 lastGrip, trail;

    /// <summary>Into the customer's hand. The pattern in the scene is switched off; this copy switches itself on.</summary>
    public void Hang(CustomerCharacter customer)
    {
        Carrier = customer;
        hand = customer.LeftKnuckles;
        knuckles = hand != null;
        if (!knuckles) hand = customer.LeftHand;
        gameObject.SetActive(true);
        lastGrip = Grip();
        Follow(lastGrip);
    }

    Vector3 Grip()
    {
        var body = Carrier.transform;
        if (hand == null) return body.TransformPoint(BesidePlaceholder);
        return hand.position + Vector3.down * (knuckles ? BelowKnuckles : BelowWrist) - body.right * AwayFromBody;
    }

    // After the animator has posed the hand for this frame.
    void LateUpdate()
    {
        if (Carrier == null) return;
        var grip = Grip();
        if (Time.deltaTime > 0f)
        {
            var moving = (grip - lastGrip) / Time.deltaTime;
            moving.y = 0f;
            var wanted = Vector3.ClampMagnitude(-moving * TrailSeconds, MaxTrail);
            trail = Vector3.Lerp(trail, wanted, 1f - Mathf.Exp(-Settling * Time.deltaTime));
        }
        lastGrip = grip;
        Follow(grip);
    }

    void Follow(Vector3 grip)
    {
        // The mesh hangs down its own -y from the grip, its broad side along x: x goes along the way the customer faces.
        var alongLeg = Quaternion.Euler(0f, Carrier.transform.eulerAngles.y + 90f, 0f);
        var hanging = Quaternion.FromToRotation(Vector3.down, (Vector3.down + trail).normalized);
        transform.SetPositionAndRotation(grip, hanging * alongLeg);
    }
}
