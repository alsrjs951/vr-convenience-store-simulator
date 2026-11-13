using UnityEngine;

/// <summary>
/// The bag a customer walks out with (2026-11-11): the goods that went into the counter's bag, now hanging from the left
/// hand, the right being the one that waves. It is not a child of the hand, or it would turn with the wrist; it follows
/// the hand, hangs straight down with its broad side along the leg, and trails a little behind the hand as it swings.
/// Since 2026-11-12 it begins on the counter: the right hand takes it by the handles where the counter's bag stood,
/// draws it over the counter to its edge, and gives it to the left (CustomerDirector.TakeBag).
/// </summary>
public class CarriedBag : MonoBehaviour
{
    /// <summary>From the hand to the foot of the bag hanging free (P3Meshes.CarriedBag).</summary>
    public const float Length = 0.47f;
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
    // Standing on the counter with the hand not yet a bag's length above it, the film is slack: the bag is as much
    // shorter as the hand is low, and no shorter than this share of its length; and that much fatter for what it loses
    // in height. Off the counter's edge its foot drops at this speed until the handles are taut.
    const float ShortestShare = 0.55f, FatterWhenShort = 0.4f, DropSpeed = 1.8f;
    // The hand must be this far out past the edge before the foot can drop: the bag's own half depth and a little.
    const float ClearOfEdge = 0.09f;

    public CustomerCharacter Carrier { get; private set; }
    /// <summary>Its foot is still on what it stood on. For the checks.</summary>
    public bool Standing => standing;
    /// <summary>How much shorter than its length it is drawn right now: 1 hanging free. For the checks.</summary>
    public float Stretch { get; private set; } = 1f;

    Transform hand;
    bool knuckles;
    // Toward the carrying hand's side of the body: -1 the left, 1 the right.
    float side = -1f;
    Vector3 lastGrip, trail;
    Vector3 scale = Vector3.one;
    bool scaleRead;
    // What it stands on, until the hand has carried it out past the edge.
    bool standing;
    float surface, footAt;
    Vector3 edge, outward;
    // 1: its broad side as it stood on the counter. 0: along the carrier's leg. And how fast it turns from one to the other.
    float across, acrossYaw, turnPerSecond;

    /// <summary>
    /// Into the customer's hand: the left unless asked otherwise. The pattern in the scene is switched off; this copy
    /// switches itself on. Called again, it changes hands.
    /// </summary>
    public void Hang(CustomerCharacter customer, bool right = false)
    {
        Carrier = customer;
        side = right ? 1f : -1f;
        hand = right ? customer.RightKnuckles : customer.LeftKnuckles;
        knuckles = hand != null;
        if (!knuckles) hand = right ? customer.RightHand : customer.LeftHand;
        if (!scaleRead)
        {
            scale = transform.localScale;
            scaleRead = true;
        }
        gameObject.SetActive(true);
        lastGrip = Grip();
        Follow(lastGrip);
    }

    /// <summary>
    /// It is standing on a surface at this height, its broad side turned this way (degrees about the vertical). Until
    /// the hand has carried it out past <paramref name="edge"/> (a point on the surface's edge, <paramref name="outward"/>
    /// the level way off it), its foot stays on the surface however low the hand is.
    /// </summary>
    public void StandOn(float height, Vector3 edge, Vector3 outward, float yaw)
    {
        standing = true;
        surface = height;
        footAt = height;
        this.edge = edge;
        this.outward = outward.normalized;
        across = 1f;
        acrossYaw = yaw;
        turnPerSecond = 0f;
        trail = Vector3.zero;
        lastGrip = Grip();
        Follow(lastGrip);
    }

    /// <summary>From how it stood on the counter to hanging along the leg, in this long.</summary>
    public void TurnAlongLeg(float seconds) => turnPerSecond = 1f / Mathf.Max(0.01f, seconds);

    /// <summary>Where the handles are in the hand right now.</summary>
    public Vector3 Grip()
    {
        var body = Carrier.transform;
        if (hand == null) return body.TransformPoint(BesidePlaceholder);
        return hand.position + Vector3.down * (knuckles ? BelowKnuckles : BelowWrist) + body.right * (side * AwayFromBody * (1f - across));
    }

    // After the animator has posed the hand for this frame, and after the arms have reached (CustomerArm, order -40).
    void LateUpdate()
    {
        if (Carrier == null) return;
        across = Mathf.MoveTowards(across, 0f, turnPerSecond * Time.deltaTime);
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
        // The foot: a bag's length under the hand, or on what the bag stands on while the hand is over that.
        float free = grip.y - Length;
        if (standing)
        {
            bool over = Vector3.Dot(grip - edge, outward) < ClearOfEdge;
            float wanted = over ? Mathf.Max(free, surface) : free;
            footAt = wanted >= footAt ? wanted : Mathf.Max(wanted, footAt - DropSpeed * Time.deltaTime);
            if (!over && footAt <= free + 0.001f) standing = false;
        }
        if (!standing) footAt = free;
        Stretch = Mathf.Clamp((grip.y - footAt) / Length, ShortestShare, 1f);
        float fatter = 1f + (1f - Stretch) * FatterWhenShort;
        transform.localScale = Vector3.Scale(scale, new Vector3(fatter, Stretch, fatter));

        // The mesh hangs down its own -y from the grip, its broad side along x: x goes along the way the customer faces.
        float alongLeg = Carrier.transform.eulerAngles.y + 90f;
        var turned = Quaternion.Euler(0f, across > 0f ? Mathf.LerpAngle(alongLeg, acrossYaw, across) : alongLeg, 0f);
        // Dragged over the counter it leans after the hand less than it swings in the air.
        var hanging = Quaternion.FromToRotation(Vector3.down, (Vector3.down + trail * (standing ? 0.5f : 1f)).normalized);
        transform.SetPositionAndRotation(grip, hanging * turned);
    }
}
