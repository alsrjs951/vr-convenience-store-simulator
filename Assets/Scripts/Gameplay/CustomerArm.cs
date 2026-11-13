using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A customer's left arm reaching for a point (2026-11-11): what sets their goods down on the counter, where before the
/// goods came down by themselves beside a customer standing still. The clips Rocketbox ships only stand and walk, so the
/// reach is added on top of them after the animator has posed the body: the upper body leans forward and turns its left
/// shoulder in as far as the reach needs, then the upper arm and forearm are turned so the wrist is on the point. The left arm, because the goods stand to
/// the customer's left and the right hand is the one that waves.
/// </summary>
// Before CustomerFace turns the head (default order), so the look at the clerk is set on the leaning body, not leant with it.
[DefaultExecutionOrder(-40)]
public class CustomerArm : MonoBehaviour
{
    // Reaching as far as it will, the body leans this far forward and turns this far to bring the left shoulder in; less
    // of both for a nearer point. The lean is shared between the hips and the chest. The arm is never stretched quite straight.
    // 50 degrees of lean, not 35 (2026-11-12): the standing clip has the shoulders some 17 degrees behind the hips, so 35
    // only brought them as far forward again and no lower, and a low thing far along the counter stayed out of a short arm's reach.
    const float MaxLeanDegrees = 50f, MaxTurnDegrees = 25f, LeanAtHips = 0.6f, ReachShare = 0.97f;
    const float BlendSeconds = 0.18f;
    // Closed all the way on a thing, each finger turns this far at its knuckle, its middle joint and its last joint, and
    // the thumb comes this far across under the palm and bends this far. The clips leave the hand open and flat.
    const float CurlAtKnuckle = 55f, CurlAtMiddle = 60f, CurlAtTip = 35f, ThumbAcross = 40f, ThumbBend = 20f;
    const float CloseSeconds = 0.12f;

    /// <summary>The hand is on its point, or on its way there.</summary>
    public bool Reaching { get; private set; }
    /// <summary>The farthest the wrist has been from where it was asked to be while fully reaching, for the checks.</summary>
    public float WorstMiss { get; private set; }

    CustomerCharacter owner;
    // Toward this arm's side of the body, in the body's frame: -1 the left arm, 1 the right (2026-11-12: the right hand
    // takes the change off the tray, which lies to the customer's right).
    float side = -1f;
    Transform spine, chest, upper, lower, hand;
    Quaternion gripFromBone = Quaternion.identity;
    float armLength, weight, effort;
    // Where the animator has the hips, chest and shoulder this frame, before any lean is added.
    Vector3 hipsAt, chestAt, shoulderAt;
    bool posed;
    Vector3 target;
    Quaternion grip = Quaternion.identity;

    /// <summary>One joint of a finger or of the thumb, and how it turns as the hand closes.</summary>
    class Joint
    {
        public Transform Bone;
        // In the hand bone's own frame, so it goes with the hand however the wrist is turned.
        public Vector3 Axis;
        public float Degrees;
        public bool Thumb;
        // How the animator last left it, and how this left it after that.
        public Quaternion Animated, Written;
        public bool Wrote;
    }

    readonly List<Joint> joints = new List<Joint>();
    float fingersAsked, thumbAsked, fingersNow, thumbNow;

    /// <summary>Finger and thumb joints this hand can close. None on a skeleton without finger bones.</summary>
    public int FingerJoints => joints.Count;
    /// <summary>How far the fingers are closed right now, 0 to 1.</summary>
    public float Closed => fingersNow;

    /// <summary>Null on a body without a human skeleton (the placeholder).</summary>
    public static CustomerArm Attach(CustomerCharacter character, bool right = false)
    {
        HumanBodyBones Of(HumanBodyBones onTheLeft, HumanBodyBones onTheRight) => right ? onTheRight : onTheLeft;
        var animator = character.Animator;
        if (animator == null || !animator.isHuman) return null;
        var upper = animator.GetBoneTransform(Of(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm));
        var lower = animator.GetBoneTransform(Of(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm));
        var hand = animator.GetBoneTransform(Of(HumanBodyBones.LeftHand, HumanBodyBones.RightHand));
        var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        if (upper == null || lower == null || hand == null || spine == null) return null;
        var arm = character.gameObject.AddComponent<CustomerArm>();
        arm.owner = character;
        arm.side = right ? 1f : -1f;
        arm.upper = upper;
        arm.lower = lower;
        arm.hand = hand;
        arm.spine = spine;
        arm.chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest) ?? spine;
        arm.armLength = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
        // Which way this hand's bone has its fingers and its palm, read off the finger bones, so a hold can be asked for
        // in those terms. Thumb side x fingers is the way out of the palm of a left hand.
        var middle = animator.GetBoneTransform(Of(HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal));
        var index = animator.GetBoneTransform(Of(HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal));
        var little = animator.GetBoneTransform(Of(HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal));
        if (middle != null && index != null && little != null)
        {
            var fingers = hand.InverseTransformDirection(middle.position - hand.position).normalized;
            var thumbSide = hand.InverseTransformDirection(index.position - little.position).normalized;
            // (And of a right hand, the other way round.)
            var palm = (right ? Vector3.Cross(fingers, thumbSide) : Vector3.Cross(thumbSide, fingers)).normalized;
            arm.gripFromBone = Quaternion.Inverse(Quaternion.LookRotation(fingers, palm));

            // The fingers close toward the palm. Each finger's three joints follow one another in the list of bones.
            var close = Vector3.Cross(fingers, palm).normalized;
            float[] degrees = { CurlAtKnuckle, CurlAtMiddle, CurlAtTip };
            int first = (int)Of(HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal);
            for (int bone = first; bone <= (int)Of(HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal); bone++)
            {
                var joint = animator.GetBoneTransform((HumanBodyBones)bone);
                if (joint != null) arm.joints.Add(new Joint { Bone = joint, Axis = close, Degrees = degrees[(bone - first) % 3] });
            }
            // The thumb lies beside the palm. It comes across under it by turning about the way the fingers point, and
            // then bends at its two outer joints.
            var thumbBase = animator.GetBoneTransform(Of(HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal));
            var thumbMiddle = animator.GetBoneTransform(Of(HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate));
            var thumbTip = animator.GetBoneTransform(Of(HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal));
            if (thumbBase != null && thumbMiddle != null && thumbTip != null)
            {
                var thumb = hand.InverseTransformDirection(thumbTip.position - thumbBase.position).normalized;
                var across = fingers * Mathf.Sign(Vector3.Dot(Vector3.Cross(fingers, thumb), palm));
                var bend = Vector3.Cross(thumb, palm);
                bend = bend.sqrMagnitude > 1e-4f ? bend.normalized : close;
                arm.joints.Add(new Joint { Bone = thumbBase, Axis = across, Degrees = ThumbAcross, Thumb = true });
                arm.joints.Add(new Joint { Bone = thumbMiddle, Axis = bend, Degrees = ThumbBend, Thumb = true });
                arm.joints.Add(new Joint { Bone = thumbTip, Axis = bend, Degrees = ThumbBend, Thumb = true });
            }
        }
        return arm;
    }

    /// <summary>
    /// Closes the hand this far (0 open as the clip has it, 1 all the way) on what it holds, the fingers and the thumb
    /// each by their own share: a hand over the top of a bag, a card between the fingers and the thumb, the handles of a
    /// carried bag. It stays so until asked otherwise, whether or not the arm is reaching.
    /// </summary>
    public void Hold(float fingers, float thumb)
    {
        fingersAsked = Mathf.Clamp01(fingers);
        thumbAsked = Mathf.Clamp01(thumb);
    }

    public void Open() => Hold(0f, 0f);

    /// <summary>
    /// The wrist goes to this point and stays on it, call after call, until <see cref="LetGo"/>. The fingers point along
    /// <paramref name="fingers"/> and the palm faces <paramref name="palm"/>.
    /// </summary>
    public void ReachFor(Vector3 point, Vector3 fingers, Vector3 palm)
    {
        target = point;
        grip = Quaternion.LookRotation(fingers, palm) * gripFromBone;
        Reaching = true;
    }

    public void LetGo() => Reaching = false;

    /// <summary>
    /// How far along the way from <paramref name="from"/> to <paramref name="wanted"/> the wrist can get, leaning as far
    /// as the body will. The customer stands back from the counter, and the far end of the row of goods is out of reach.
    /// </summary>
    /// <param name="stepped">How far the body will have moved from where it stands now when it reaches.</param>
    public Vector3 Reachable(Vector3 from, Vector3 wanted, Vector3 stepped = default)
    {
        var shoulder = ShoulderAt(1f) + stepped;
        float reach = armLength * ReachShare;
        if ((wanted - shoulder).magnitude <= reach) return wanted;
        float near = 0f, far = 1f;
        for (int i = 0; i < 12; i++)
        {
            float middle = (near + far) * 0.5f;
            if ((Vector3.Lerp(from, wanted, middle) - shoulder).magnitude <= reach) near = middle;
            else far = middle;
        }
        return Vector3.Lerp(from, wanted, near);
    }

    /// <summary>The wrist can get to this point, the body leaning as far as it will (and moved <paramref name="stepped"/> from where it stands now).</summary>
    public bool Reaches(Vector3 point, Vector3 stepped = default) => (point - stepped - ShoulderAt(1f)).magnitude <= armLength * ReachShare + 0.001f;

    /// <summary>Where the shoulder is with the body leaning as far as it will.</summary>
    public Vector3 BentShoulder => ShoulderAt(1f);

    /// <summary>The numbers of a reach, for the log when something could not be reached.</summary>
    public string Describe(Vector3 wanted)
    {
        var bent = ShoulderAt(1f);
        return $"arm {armLength:0.000} m (reaches {armLength * ReachShare:0.000}); shoulder {shoulderAt.ToString("F3")}, bent as far as it goes {bent.ToString("F3")}; hips {hipsAt.ToString("F3")}, chest {chestAt.ToString("F3")}; wrist wanted at {wanted.ToString("F3")}, {(wanted - bent).magnitude:0.000} m from the bent shoulder";
    }

    /// <summary>How the hips and the chest turn for this share (0 to 1) of the body's whole reach.</summary>
    void Bend(float share, out Quaternion atHips, out Quaternion atChest)
    {
        var right = owner.transform.right;
        // Turning clockwise seen from above brings the left shoulder forward.
        // (And the other way for the right arm.)
        var turn = Quaternion.AngleAxis(-side * MaxTurnDegrees * share * 0.5f, Vector3.up);
        atHips = turn * Quaternion.AngleAxis(MaxLeanDegrees * share * LeanAtHips, right);
        atChest = turn * Quaternion.AngleAxis(MaxLeanDegrees * share * (1f - LeanAtHips), right);
    }

    /// <summary>Where the shoulder is once the body has bent that share of its whole reach from how the animator has it now.</summary>
    Vector3 ShoulderAt(float share)
    {
        if (!posed) Read();
        Bend(share, out var atHips, out var atChest);
        return hipsAt + atHips * (chestAt - hipsAt + atChest * (shoulderAt - chestAt));
    }

    void Read()
    {
        hipsAt = spine.position;
        chestAt = chest.position;
        shoulderAt = upper.position;
        posed = true;
    }

    // After the animator has posed the body for this frame.
    void LateUpdate()
    {
        Read();
        weight = Mathf.MoveTowards(weight, Reaching ? 1f : 0f, Time.deltaTime / BlendSeconds);
        if (weight > 0f) Reach();
        else effort = 0f;
        Close();
    }

    /// <summary>The fingers, after the wrist is where it will be this frame.</summary>
    void Close()
    {
        fingersNow = Mathf.MoveTowards(fingersNow, fingersAsked, Time.deltaTime / CloseSeconds);
        thumbNow = Mathf.MoveTowards(thumbNow, thumbAsked, Time.deltaTime / CloseSeconds);
        // The animator poses the fingers afresh each frame. Should it ever leave one as this turned it (a body not
        // animated this frame), the turn must not be added a second time.
        foreach (var joint in joints)
        {
            if (joint.Wrote && joint.Bone.localRotation == joint.Written) joint.Bone.localRotation = joint.Animated;
            else joint.Animated = joint.Bone.localRotation;
            joint.Wrote = false;
        }
        if (fingersNow <= 0f && thumbNow <= 0f) return;
        // From the knuckle outward: each joint carries the ones after it.
        foreach (var joint in joints)
        {
            float degrees = joint.Degrees * (joint.Thumb ? thumbNow : fingersNow);
            joint.Bone.rotation = Quaternion.AngleAxis(degrees, hand.TransformDirection(joint.Axis)) * joint.Bone.rotation;
        }
        foreach (var joint in joints)
        {
            joint.Written = joint.Bone.localRotation;
            joint.Wrote = true;
        }
    }

    void Reach()
    {
        float share = Mathf.SmoothStep(0f, 1f, weight);

        // The body bends no further than the reach needs.
        float reach = armLength * ReachShare, needed = 0f;
        if ((target - shoulderAt).magnitude > reach)
        {
            needed = 1f;
            if ((target - ShoulderAt(1f)).magnitude <= reach)
            {
                float enough = 1f, tooLittle = 0f;
                for (int i = 0; i < 8; i++)
                {
                    float middle = (enough + tooLittle) * 0.5f;
                    if ((target - ShoulderAt(middle)).magnitude <= reach) enough = middle;
                    else tooLittle = middle;
                }
                needed = enough;
            }
            else
            {
                // Out of reach however far the body leans: it leans as far as brings the shoulder nearest. A point low
                // beside the hip is not got nearer by leaning forward, and leaning all the way after it left the wrist
                // 9 to 18 cm off (a thing put away at the customer's side, 2026-11-13).
                float nearest = (target - ShoulderAt(1f)).magnitude;
                for (int step = 0; step < 10; step++)
                {
                    float off = (target - ShoulderAt(step * 0.1f)).magnitude;
                    if (off >= nearest) continue;
                    nearest = off;
                    needed = step * 0.1f;
                }
            }
        }
        // At once: the point itself moves smoothly, and a bend that lagged behind it would leave the wrist short.
        effort = needed;
        if (effort * share > 0.001f)
        {
            // The chest first: turning the hips then carries the chest's turn with it.
            Bend(effort * share, out var atHips, out var atChest);
            if (chest != spine) chest.rotation = atChest * chest.rotation;
            spine.rotation = atHips * spine.rotation;
        }

        // The arm: the elbow out to the customer's left and down, the wrist on the point.
        var wrist = Vector3.Lerp(hand.position, target, share);
        var body = owner.transform;
        Solve(wrist, body.right * (side * 0.7f) + Vector3.down * 0.6f - body.forward * 0.35f);
        hand.rotation = Quaternion.Slerp(hand.rotation, grip, share);
        if (weight >= 1f) WorstMiss = Mathf.Max(WorstMiss, Vector3.Distance(hand.position, target));
    }

    /// <summary>Turns the upper arm and the forearm so the wrist is on <paramref name="point"/>, the elbow toward <paramref name="elbowWay"/>.</summary>
    void Solve(Vector3 point, Vector3 elbowWay) => Solve(upper, lower, hand, point, elbowWay);

    /// <summary>The same for any limb of two bones: a leg, with the knee toward <paramref name="elbowWay"/> (CustomerFeet).</summary>
    public static void Solve(Transform upper, Transform lower, Transform hand, Vector3 point, Vector3 elbowWay)
    {
        var shoulder = upper.position;
        float upperLength = Vector3.Distance(shoulder, lower.position), lowerLength = Vector3.Distance(lower.position, hand.position);
        var toPoint = point - shoulder;
        float distance = Mathf.Clamp(toPoint.magnitude, 0.05f, (upperLength + lowerLength) * 0.999f);
        var along = toPoint.normalized;
        // The elbow lies on a circle round the line from the shoulder to the point; of that circle, the place toward elbowWay.
        float cos = Mathf.Clamp((upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * upperLength * distance), -1f, 1f);
        var off = Vector3.ProjectOnPlane(elbowWay, along);
        if (off.sqrMagnitude < 1e-6f) off = Vector3.ProjectOnPlane(Vector3.down, along);
        var elbow = shoulder + along * (upperLength * cos) + off.normalized * (upperLength * Mathf.Sqrt(1f - cos * cos));
        upper.rotation = Quaternion.FromToRotation(lower.position - shoulder, elbow - shoulder) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, shoulder + along * distance - lower.position) * lower.rotation;
    }
}
