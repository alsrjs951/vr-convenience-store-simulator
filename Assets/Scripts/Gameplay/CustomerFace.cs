using UnityEngine;

public enum CustomerMood { Neutral, Pleased, Impatient, Angry }

/// <summary>
/// A customer's face and attention (2026-11-09). The body clips only cover idle, walk and wave, so everything a person does
/// with the head and face is added here on top of them in LateUpdate, on the facial bones Rocketbox ships: the head and eyes
/// turn to the clerk, the eyes blink, the jaw follows the loudness of the line being spoken, and the mouth corners and brows
/// show a mood. A nod when paid and a shake of the head when fed up are the customer's answers to how it went.
///
/// All axes are worked out from where the eyes are in the rest pose, so nothing depends on how the Biped bones are oriented.
/// </summary>
public class CustomerFace : MonoBehaviour
{
    // How far the head and neck may go before the customer stops trying: past this the clerk is behind them.
    const float MaxYaw = 70f, MaxPitch = 35f;
    const float NeckShare = 0.3f, HeadShare = 0.75f;
    const float GazeWeightPerSecond = 3.5f;
    const float GazeFollowRate = 7f;
    const float MaxEyeDegrees = 24f;
    const float GlanceMeters = 1.3f;

    const float JawOpenDegrees = 9.5f;
    const float JawDegreesPerSecond = 160f;
    // The lid bones sit on the lids, so they slide; turning them about their own pivot barely moved the skin (2026-11-09).
    const float UpperLidMeters = 0.0095f, LowerLidMeters = 0.003f;
    const float BlinkSeconds = 0.16f;
    const float MoodPerSecond = 4f;

    const float NodSeconds = 0.75f, NodDegrees = 13f, NodCycles = 2f;
    const float ShakeSeconds = 1f, ShakeDegrees = 15f, ShakeCycles = 3f;

    /// <summary>A bone with its rest pose and the face's axes in its own frame, measured once in the rest pose.</summary>
    class Bone
    {
        public Transform Transform;
        public Quaternion RestRotation;
        public Vector3 RestPosition, Forward, Up, Right, LocalUpPerMeter, LocalRightPerMeter;
    }

    CustomerCharacter character;
    Bone head, neck, jaw, leftEye, rightEye;
    Bone[] upperLids, lowerLids, leftCorner, rightCorner, innerBrows;
    Camera clerk;

    float gazeWeight;
    Vector3 gazePoint;
    float nextAttentionChange;
    bool attentive;
    float nextBlink, blinkStarted = -10f;
    float jawAngle;
    float[] envelope;
    float talkStarted = -10f, talkSeconds;
    float nodStarted = -10f, shakeStarted = -10f, shakeStrength = 1f;
    CustomerMood mood = CustomerMood.Neutral;
    float smile, frown, browDown;

    /// <summary>For the checks: forced lids (0 open, 1 shut), or negative for the blinks of everyday.</summary>
    public float TestLids { get; set; } = -1f;
    public bool HasFaceBones { get; private set; }
    public int Blinks { get; private set; }
    public int Nods { get; private set; }
    public int Shakes { get; private set; }
    public bool Talking => Time.time - talkStarted < talkSeconds;
    public float JawAngle => jawAngle;
    public CustomerMood Mood => mood;
    /// <summary>Degrees between where the face points and the clerk's eyes, as of the last frame.</summary>
    public float GazeErrorDegrees { get; private set; } = 180f;
    public Vector3 FacePoint => leftEye != null ? (leftEye.Transform.position + rightEye.Transform.position) * 0.5f : transform.position + Vector3.up * 1.55f;
    public Vector3 FaceForward => head != null ? head.Transform.rotation * head.Forward : transform.forward;

    /// <summary>To be called while the skeleton is still in its rest pose, before the Animator has moved it.</summary>
    public static CustomerFace Attach(CustomerCharacter character)
    {
        if (character.Animator == null || !character.Animator.isHuman) return null;
        var face = character.gameObject.AddComponent<CustomerFace>();
        face.Bind(character);
        return face;
    }

    void Bind(CustomerCharacter owner)
    {
        character = owner;
        var bones = new System.Collections.Generic.Dictionary<string, Transform>();
        foreach (var t in owner.Animator.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
        if (!bones.TryGetValue("Bip01 Head", out var headBone) || !bones.TryGetValue("Bip01 Neck", out var neckBone)
            || !bones.TryGetValue("Bip01 LEye", out var left) || !bones.TryGetValue("Bip01 REye", out var right))
        {
            enabled = false;
            return;
        }
        var faceRight = (right.position - left.position).normalized;
        var faceUp = Vector3.ProjectOnPlane(Vector3.up, faceRight).normalized;
        var faceForward = Vector3.Cross(faceRight, faceUp);

        Bone Measure(Transform t)
        {
            if (t == null) return null;
            return new Bone
            {
                Transform = t,
                RestRotation = t.localRotation,
                RestPosition = t.localPosition,
                Forward = Quaternion.Inverse(t.rotation) * faceForward,
                Up = Quaternion.Inverse(t.rotation) * faceUp,
                Right = Quaternion.Inverse(t.rotation) * faceRight,
                LocalUpPerMeter = t.parent.InverseTransformVector(faceUp),
                LocalRightPerMeter = t.parent.InverseTransformVector(faceRight),
            };
        }

        Bone Named(string name) => bones.TryGetValue(name, out var t) ? Measure(t) : null;

        head = Measure(headBone);
        neck = Measure(neckBone);
        leftEye = Measure(left);
        rightEye = Measure(right);
        jaw = Named("Bip01 MJaw");
        upperLids = new[] { Named("Bip01 LEyeBlinkTop"), Named("Bip01 REyeBlinkTop") };
        lowerLids = new[] { Named("Bip01 LEyeBlinkBottom"), Named("Bip01 REyeBlinkBottom") };
        leftCorner = new[] { Named("Bip01 LMouthCorner") };
        rightCorner = new[] { Named("Bip01 RMouthCorner") };
        innerBrows = new[] { Named("Bip01 LInnerEyebrow"), Named("Bip01 RInnerEyebrow"), Named("Bip01 MMiddleEyebrow") };
        HasFaceBones = jaw != null && upperLids[0] != null && leftCorner[0] != null && innerBrows[0] != null;
        nextBlink = Time.time + Random.Range(1f, 4f);
    }

    /// <summary>Moves the mouth to this line. A missing loudness curve still gives a plausible mouth.</summary>
    public void Talk(float[] loudness, float seconds)
    {
        envelope = loudness;
        talkStarted = Time.time;
        talkSeconds = seconds;
    }

    public void SetMood(CustomerMood value) => mood = value;

    public void Nod()
    {
        nodStarted = Time.time;
        Nods++;
    }

    public void ShakeHead(float strength = 1f)
    {
        shakeStarted = Time.time;
        shakeStrength = strength;
        Shakes++;
    }

    void LateUpdate()
    {
        if (head == null) return;
        if (clerk == null) clerk = Camera.main;
        float dt = Time.deltaTime;
        Attend(dt);
        if (gazeWeight > 0.001f)
        {
            Aim(neck, gazeWeight * NeckShare);
            Aim(head, gazeWeight * HeadShare);
        }
        Gestures();
        Eyes();
        Lids();
        Mouth(dt);
        Expression(dt);
        if (clerk != null) GazeErrorDegrees = Vector3.Angle(FaceForward, clerk.transform.position - head.Transform.position);
    }

    /// <summary>Who is being looked at: the clerk at the counter, on and off from the line, and in passing when close by.</summary>
    void Attend(float dt)
    {
        var point = head.Transform.position + transform.forward;
        bool want = false;
        if (clerk != null)
        {
            var eyes = clerk.transform.position;
            var flat = eyes - transform.position;
            flat.y = 0f;
            bool close = flat.magnitude < GlanceMeters;
            if (Time.time >= nextAttentionChange)
            {
                attentive = Random.value < (character.State == CustomerState.AtCounter ? 0.85f : 0.5f);
                nextAttentionChange = Time.time + (attentive ? Random.Range(2.5f, 6f) : Random.Range(1f, 2.5f));
            }
            switch (character.State)
            {
                case CustomerState.AtCounter:
                    // Mostly the clerk; now and then down at the things on the counter. A nod or a word is always to the clerk.
                    want = true;
                    point = attentive || Talking ? eyes : head.Transform.position + transform.forward * 0.7f + Vector3.down * 0.55f;
                    break;
                case CustomerState.Queued:
                    want = attentive;
                    point = eyes;
                    break;
                default:
                    want = close;
                    point = eyes;
                    break;
            }
            // Still looking back at the clerk while waving goodbye.
            if (character.Playing == CustomerCharacter.LeaveState)
            {
                want = true;
                point = eyes;
            }
            var local = transform.InverseTransformDirection(point - head.Transform.position);
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            if (Mathf.Abs(yaw) > MaxYaw || Mathf.Abs(pitch) > MaxPitch) want = false;
        }
        gazeWeight = Mathf.MoveTowards(gazeWeight, want ? 1f : 0f, GazeWeightPerSecond * dt);
        if (gazePoint == Vector3.zero) gazePoint = point;
        gazePoint = Vector3.Lerp(gazePoint, point, 1f - Mathf.Exp(-GazeFollowRate * dt));
    }

    void Aim(Bone bone, float weight)
    {
        var current = bone.Transform.rotation * bone.Forward;
        var delta = Quaternion.FromToRotation(current, gazePoint - bone.Transform.position);
        bone.Transform.rotation = Quaternion.Slerp(Quaternion.identity, delta, weight) * bone.Transform.rotation;
    }

    void Gestures()
    {
        float nod = (Time.time - nodStarted) / NodSeconds;
        if (nod >= 0f && nod < 1f)
        {
            float degrees = NodDegrees * Mathf.Sin(nod * Mathf.PI * 2f * NodCycles) * (1f - nod);
            head.Transform.rotation = Quaternion.AngleAxis(degrees, head.Transform.rotation * head.Right) * head.Transform.rotation;
        }
        float shake = (Time.time - shakeStarted) / ShakeSeconds;
        if (shake >= 0f && shake < 1f)
        {
            float degrees = ShakeDegrees * shakeStrength * Mathf.Sin(shake * Mathf.PI * 2f * ShakeCycles) * (1f - shake);
            head.Transform.rotation = Quaternion.AngleAxis(degrees, head.Transform.rotation * head.Up) * head.Transform.rotation;
        }
    }

    void Eyes()
    {
        Eye(leftEye);
        Eye(rightEye);
    }

    /// <summary>The eyes keep on the target while the head is still turning, which is most of what makes it feel like being looked at.</summary>
    void Eye(Bone eye)
    {
        var rest = eye.Transform.parent.rotation * eye.RestRotation;
        var forward = rest * eye.Forward;
        var wanted = gazePoint - eye.Transform.position;
        float angle = Vector3.Angle(forward, wanted);
        float weight = gazeWeight * (angle > 0.01f ? Mathf.Min(1f, MaxEyeDegrees / angle) : 1f);
        eye.Transform.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(forward, wanted), weight) * rest;
    }

    void Lids()
    {
        if (Time.time >= nextBlink)
        {
            blinkStarted = Time.time;
            nextBlink = Time.time + Random.Range(2f, 5.5f);
            Blinks++;
        }
        float t = (Time.time - blinkStarted) / BlinkSeconds;
        float closed = t >= 0f && t < 1f ? Mathf.Sin(t * Mathf.PI) : 0f;
        if (TestLids >= 0f) closed = TestLids;
        foreach (var lid in upperLids) Slide(lid, -UpperLidMeters * closed, 0f);
        foreach (var lid in lowerLids) Slide(lid, LowerLidMeters * closed, 0f);
    }

    void Turn(Bone bone, float degrees)
    {
        if (bone != null) bone.Transform.localRotation = bone.RestRotation * Quaternion.AngleAxis(degrees, bone.Right);
    }

    void Mouth(float dt)
    {
        float open = 0f;
        if (Talking)
        {
            float elapsed = Time.time - talkStarted;
            if (envelope != null && envelope.Length > 0)
                open = envelope[Mathf.Clamp(Mathf.FloorToInt(elapsed / talkSeconds * envelope.Length), 0, envelope.Length - 1)];
            else
                open = 0.5f + 0.5f * Mathf.Sin(elapsed * 34f) * Mathf.PerlinNoise(elapsed * 6f, 3.7f);
        }
        jawAngle = Mathf.MoveTowards(jawAngle, open * JawOpenDegrees, JawDegreesPerSecond * dt);
        Turn(jaw, jawAngle);
    }

    void Expression(float dt)
    {
        float wantSmile = mood == CustomerMood.Pleased ? 1f : 0f;
        float wantFrown = mood == CustomerMood.Angry ? 1f : mood == CustomerMood.Impatient ? 0.5f : 0f;
        smile = Mathf.MoveTowards(smile, wantSmile, MoodPerSecond * dt);
        frown = Mathf.MoveTowards(frown, wantFrown, MoodPerSecond * dt);
        browDown = frown;
        float lift = 0.008f * smile - 0.005f * frown;
        foreach (var corner in leftCorner) Slide(corner, lift, -0.004f * smile);
        foreach (var corner in rightCorner) Slide(corner, lift, 0.004f * smile);
        foreach (var brow in innerBrows) Slide(brow, -0.006f * browDown, 0f);
    }

    void Slide(Bone bone, float up, float right)
    {
        if (bone != null) bone.Transform.localPosition = bone.RestPosition + bone.LocalUpPerMeter * up + bone.LocalRightPerMeter * right;
    }
}
