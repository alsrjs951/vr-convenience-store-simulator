using UnityEngine;

/// <summary>
/// A standing customer's feet when the body is moved a little way (2026-11-12): the step up to the counter, the steps
/// along it to where the next thing goes, the step back before walking off (CustomerCharacter.StepTo). The clips
/// Rocketbox ships only stand and walk, and the walk goes forward at a walk's pace; under a body moved a hand's breadth
/// sideways the standing clip's feet simply slid over the floor. So while the body moves, each foot stays where it
/// stands on the floor, and they take turns to lift and come down again under the body. Walking on the NavMesh is left
/// to the walk clip.
/// </summary>
// Before CustomerArm reads where the hips are: a wide stance lowers them.
[DefaultExecutionOrder(-50)]
public class CustomerFeet : MonoBehaviour
{
    // A foot is in the air this long and rises this high at the middle of it. It comes down ahead of where the clip has
    // it by half of what the body will move during the other foot's step, no farther than LeadMost.
    const float SwingSeconds = 0.15f, SwingLift = 0.035f, LeadMost = 0.1f;
    // The body moving: a foot lifts once it is this far behind. The body still: a foot more than CloseUp from its place
    // takes one more small step there; less than that it stays where it came down, for as long as the customer stands
    // there. (Let go of to the clip after each step, 2026-11-12, the feet crept a centimetre every time: 5 to 7 cm at the counter.)
    // What is left when the customer walks off goes at WalkingSettleSpeed, into the walk.
    const float StartBehind = 0.01f, CloseUp = 0.01f, StillSpeed = 0.03f, SettleSpeed = 0.1f, WalkingSettleSpeed = 0.6f;
    // Moved farther than this in one frame it was put somewhere, not stepped there.
    const float PutSomewhere = 0.3f;
    // The leg is never stretched quite straight; the hips come down instead.
    const float LegShare = 0.995f;

    class Leg
    {
        public Transform Upper, Lower, Foot;
        public float Length;
        // Toward this leg's side of the body, in the body's frame: -1 left, 1 right.
        public float Side;
        // From where the clip has the foot to where it is held, on the floor.
        public Vector3 Lag;
        // Standing on the floor at Planted, wherever the body and the clip go.
        public bool Held;
        public Vector3 Planted;
        public bool Swinging;
        public float Swung;
        public Vector3 SwingFrom;
        // For the measure of sliding: where the foot was drawn when it last came to stand on the floor, and how far from
        // there it is drawn now.
        public bool Standing;
        public Vector3 StoodAt;
        public float Drift;
        // Came down this frame: the last of its way through the air is not sliding.
        public bool Landed;
    }

    /// <summary>Steps taken, feet lifted and set down again, since this customer was made.</summary>
    public int Steps { get; private set; }
    /// <summary>How far the body has been moved while standing, in metres.</summary>
    public float Carried { get; private set; }
    /// <summary>
    /// How far, in all, a foot on the floor has ended up from where it came down, while the body was being moved. For the
    /// checks. From where it came down, not frame by frame: a foot held still for half a minute is drawn a few
    /// thousandths of a millimetre off in each of several thousand frames, and summed up those came to centimetres.
    /// </summary>
    public float Slid => slid + left.Drift + right.Drift;
    float slid;
    /// <summary>The farthest a held foot has been drawn from where it was to be, and the most the hips came down for a stance.</summary>
    public float WorstMiss { get; private set; }
    public float DeepestDrop { get; private set; }
    /// <summary>A foot is in the air.</summary>
    public bool Swinging => left.Swinging || right.Swinging;

    CustomerCharacter owner;
    Transform hips;
    readonly Leg left = new Leg { Side = -1f }, right = new Leg { Side = 1f };
    Vector3 lastBody, velocity;
    bool known;

    /// <summary>Null on a body without a human skeleton (the placeholder).</summary>
    public static CustomerFeet Attach(CustomerCharacter character)
    {
        var animator = character.Animator;
        if (animator == null || !animator.isHuman) return null;
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var bones = new[]
        {
            animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg), animator.GetBoneTransform(HumanBodyBones.LeftFoot),
            animator.GetBoneTransform(HumanBodyBones.RightUpperLeg), animator.GetBoneTransform(HumanBodyBones.RightLowerLeg), animator.GetBoneTransform(HumanBodyBones.RightFoot),
        };
        if (hips == null) return null;
        foreach (var bone in bones)
            if (bone == null) return null;
        var feet = character.gameObject.AddComponent<CustomerFeet>();
        feet.owner = character;
        feet.hips = hips;
        feet.Set(feet.left, bones[0], bones[1], bones[2]);
        feet.Set(feet.right, bones[3], bones[4], bones[5]);
        return feet;
    }

    void Set(Leg leg, Transform upper, Transform lower, Transform foot)
    {
        leg.Upper = upper;
        leg.Lower = lower;
        leg.Foot = foot;
        leg.Length = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, foot.position);
    }

    // After the animator has posed the body for this frame.
    void LateUpdate()
    {
        float dt = Time.deltaTime;
        var body = owner.transform.position;
        var moved = known ? body - lastBody : Vector3.zero;
        moved.y = 0f;
        lastBody = body;
        known = true;
        if (dt <= 0f) return;

        bool standing = owner.Settled && moved.magnitude < PutSomewhere;
        bool stepping = false;
        if (!standing)
        {
            // Walking, or put somewhere else: the clip has the feet. What they were held back by goes quickly.
            velocity = Vector3.zero;
            LetGo(left, dt * WalkingSettleSpeed);
            LetGo(right, dt * WalkingSettleSpeed);
        }
        else
        {
            velocity = Vector3.Lerp(velocity, moved / dt, 1f - Mathf.Exp(-dt / 0.03f));
            Carried += moved.magnitude;
            stepping = Step(moved, dt);
        }

        if (left.Swinging || right.Swinging || left.Lag.sqrMagnitude > 1e-10f || right.Lag.sqrMagnitude > 1e-10f) Pose();

        // A foot on the floor under a body being moved should be where it was.
        Measure(left, stepping);
        Measure(right, stepping);
    }

    void LetGo(Leg leg, float by)
    {
        leg.Swinging = false;
        leg.Held = false;
        leg.Lag = Vector3.MoveTowards(leg.Lag, Vector3.zero, by);
    }

    /// <summary>Where the clip has this foot, on the floor.</summary>
    static Vector3 Under(Leg leg)
    {
        var at = leg.Foot.position;
        at.y = 0f;
        return at;
    }

    /// <summary>The feet for this frame of standing. False while the body has not been moved since it walked here: the clip has them.</summary>
    bool Step(Vector3 moved, float dt)
    {
        bool moving = velocity.magnitude > StillSpeed;
        bool busy = moving || moved.sqrMagnitude > 0f || left.Swinging || right.Swinging || left.Held || right.Held;
        if (!busy)
        {
            // Left to the clip. The centimetre they may still be off by goes on going.
            bool off = left.Lag.sqrMagnitude > 1e-10f || right.Lag.sqrMagnitude > 1e-10f;
            LetGo(left, dt * SettleSpeed);
            LetGo(right, dt * SettleSpeed);
            return off;
        }

        Hold(left, dt);
        Hold(right, dt);
        if (left.Swinging || right.Swinging) return true;

        // Which foot next: while the body moves, the one farthest behind it, and of two equally far the one on the side
        // the body is going to; once it is still, whichever is not yet under it.
        var toTheRight = owner.transform.right;
        float Behind(Leg leg) => moving
            ? -Vector3.Dot(leg.Lag, velocity.normalized) + (Vector3.Dot(toTheRight * leg.Side, velocity) > 0f ? 0.005f : 0f)
            : leg.Lag.magnitude;
        var next = Behind(left) >= Behind(right) ? left : right;
        if (Behind(next) > (moving ? StartBehind : CloseUp))
        {
            next.Swinging = true;
            next.Swung = 0f;
            next.SwingFrom = next.Planted;
            return true;
        }
        return true;
    }

    /// <summary>One foot for this frame: standing where it stands, or on its way through the air to under the body.</summary>
    void Hold(Leg leg, float dt)
    {
        var under = Under(leg);
        if (!leg.Held)
        {
            // From where it is drawn now.
            leg.Held = true;
            leg.Planted = under + leg.Lag;
        }
        if (leg.Swinging)
        {
            leg.Swung += dt / SwingSeconds;
            float stillInTheAir = Mathf.Max(0f, 1f - leg.Swung) * SwingSeconds;
            var down = under + Vector3.ClampMagnitude(velocity * (stillInTheAir + SwingSeconds * 0.5f), LeadMost);
            if (leg.Swung >= 1f)
            {
                leg.Swinging = false;
                leg.Landed = true;
                leg.Planted = down;
                Steps++;
            }
            else
            {
                leg.Lag = Vector3.Lerp(leg.SwingFrom, down, Mathf.SmoothStep(0f, 1f, leg.Swung)) - under;
                return;
            }
        }
        leg.Lag = leg.Planted - under;
    }

    void Pose()
    {
        var body = owner.transform;
        var leftAt = At(left);
        var rightAt = At(right);
        var leftTurned = left.Foot.rotation;
        var rightTurned = right.Foot.rotation;
        // With the feet apart the hips come down until both legs reach.
        float drop = Mathf.Max(Drop(left, leftAt), Drop(right, rightAt));
        if (drop > 0f) hips.position += Vector3.down * drop;
        DeepestDrop = Mathf.Max(DeepestDrop, drop);
        CustomerArm.Solve(left.Upper, left.Lower, left.Foot, leftAt, body.forward - body.right * 0.2f);
        CustomerArm.Solve(right.Upper, right.Lower, right.Foot, rightAt, body.forward + body.right * 0.2f);
        left.Foot.rotation = leftTurned;
        right.Foot.rotation = rightTurned;
        if (left.Held) WorstMiss = Mathf.Max(WorstMiss, Vector3.Distance(left.Foot.position, leftAt));
        if (right.Held) WorstMiss = Mathf.Max(WorstMiss, Vector3.Distance(right.Foot.position, rightAt));
    }

    /// <summary>Where the ankle is held: on the floor where the foot stands, or along its arc through the air.</summary>
    static Vector3 At(Leg leg)
    {
        var at = leg.Foot.position + leg.Lag;
        if (leg.Swinging) at.y += SwingLift * Mathf.Sin(Mathf.PI * Mathf.Clamp01(leg.Swung));
        return at;
    }

    static float Drop(Leg leg, Vector3 ankle)
    {
        var hip = leg.Upper.position;
        // As long as the leg is now: a body made larger or smaller after it was measured has other legs.
        float reach = (Vector3.Distance(hip, leg.Lower.position) + Vector3.Distance(leg.Lower.position, leg.Foot.position)) * LegShare;
        float beside = new Vector2(ankle.x - hip.x, ankle.z - hip.z).magnitude;
        if (beside >= reach) return 0f;
        return Mathf.Max(0f, hip.y - ankle.y - Mathf.Sqrt(reach * reach - beside * beside));
    }

    void Measure(Leg leg, bool counted)
    {
        var at = leg.Foot.position;
        at.y = 0f;
        if (counted && !leg.Swinging && !leg.Landed)
        {
            if (!leg.Standing)
            {
                leg.Standing = true;
                leg.StoodAt = at;
            }
            leg.Drift = Vector3.Distance(at, leg.StoodAt);
        }
        else if (leg.Standing)
        {
            // Lifted, or left to the clip: what it had slid by then is kept.
            leg.Standing = false;
            slid += leg.Drift;
            leg.Drift = 0f;
        }
        leg.Landed = false;
    }
}
