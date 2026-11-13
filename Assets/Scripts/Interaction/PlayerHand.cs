using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// One of the player's hands (2026-11-11), drawn where the controller is instead of the controller. The fingers follow
/// the buttons under them: the grip closes the three lower fingers and the thumb, the trigger closes the index finger.
/// PlayerHandSetup cuts the hand out of a customer avatar and fills in the joints.
///
/// What it holds, it holds by the thing's own shape (<see cref="FitTo"/>): the hand goes to the nearest edge of the box
/// the thing is, the fingers lie on one face and close until they touch, and the thumb comes round to the other side.
/// The thing itself stays where the game has it; only the drawn hand moves, and it goes back to the controller when the
/// thing is let go.
/// </summary>
public class PlayerHand : MonoBehaviour
{
    public const int Fingers = 5, JointsPerFinger = 3;
    public const int Thumb = 0, Index = 1, Middle = 2, Ring = 3, Little = 4;

    // Degrees at the three joints of each finger, from the knuckle out. The hand as modelled is 0.
    // Loose: how a hand hangs when it is doing nothing. Fist: everything closed. Point: the index finger out straight
    // while the others are closed, which is what the poke at the till needs.
    static readonly float[,] Loose = { { 4f, 0f, 4f }, { 4f, 6f, 4f }, { 8f, 10f, 6f }, { 10f, 12f, 6f }, { 12f, 12f, 6f } };
    static readonly float[,] Fist = { { 45f, 25f, 40f }, { 72f, 96f, 62f }, { 78f, 100f, 64f }, { 80f, 100f, 64f }, { 82f, 96f, 64f } };
    static readonly float[] Point = { 4f, 6f, 4f };
    // Around something the hand is not fitted to (not a box): about a handle's width.
    const float HoldClose = 0.5f;
    const float FollowPerSecond = 18f;

    // ---------- Fitting to what is held ----------

    // A thing this small every way (a coin) is pinched between the tips of the thumb and the index finger, the index
    // finger bent this share of the way to the fist.
    const float TinyThing = 0.05f, PinchClose = 0.4f;
    // A thing no wider than this and at least this many times as long (a handle, a knife) is a bar: the hand closes
    // round it across its length, the bar lying under the first joints of the fingers.
    const float BarWidth = 0.06f, BarLengths = 1.8f;
    // The palm does not go on a face narrower than this (the edge of a card), unless the thing has no other.
    const float NarrowFace = 0.025f;
    // The hand goes back along its fingers to the edge of the face it lies on, but no further than this; on a face
    // larger than that it stays where it is, flat on the face.
    const float FarthestEdge = 0.12f;
    // Among the faces the palm could go on, the two across the box's thinnest side are this much likelier.
    const float ThinFaceBonus = 0.6f;
    // Under this the thing is a sheet or a card: the fingers lie on it and do not curl on round its far edge.
    const float SheetThickness = 0.02f;
    // Up to this the thumb comes round to the opposite face; past it, it rests on the side below the edge, this far down.
    const float ThumbRoundThickness = 0.045f, ThumbDownTheSide = 0.035f;
    // Half the thickness of a fingertip, of a finger at a joint and of the thumb's tip: how near the bone a surface comes.
    const float FingerPad = 0.007f, JointPad = 0.0095f, ThumbPad = 0.008f;
    // From the index finger to the little finger across the pads, and how far in from an edge's ends the hand keeps.
    const float HandSpan = 0.07f, EdgeMargin = 0.01f;
    // A finger may open this far past how it was modelled (a share of the way to the fist, backwards) to lie on a face.
    const float OpenPastRest = -0.12f;
    // A finger's outer joints that find nothing more to touch after its inner part has landed close this much further.
    const float CurlOnWithoutTouch = 0.2f;
    const float MoveToGraspSeconds = 0.12f;

    public bool Left;
    /// <summary>Finger * 3 + joint, from the knuckle out.</summary>
    public Transform[] Joints = new Transform[Fingers * JointsPerFinger];
    /// <summary>Local rotations as modelled.</summary>
    public Quaternion[] Rest = new Quaternion[Fingers * JointsPerFinger];
    /// <summary>The hinge of each joint in its parent's space. A positive angle closes the finger.</summary>
    public Vector3[] CurlAxis = new Vector3[Fingers * JointsPerFinger];
    /// <summary>
    /// The thumb's first joint turns two ways. <see cref="CurlAxis"/> brings it round in front of the palm; this one, also
    /// in its parent's space, swings it down away from the palm. Only the fit to a held thing uses it.
    /// </summary>
    public Vector3 ThumbDownAxis = Vector3.right;
    /// <summary>The end of each finger, for whatever needs to know where a finger touches.</summary>
    public Transform[] Tips = new Transform[Fingers];
    /// <summary>Where the index finger ends and which way it goes when it points, in this hand's space before its scale.</summary>
    public Vector3 PointTip, PointDirection = Vector3.forward;
    /// <summary>
    /// The flat thing the four fingers would lie on, in this hand's space before its scale: a point of it, the way out of
    /// it toward the back of the hand, and the way the fingers run along it.
    /// </summary>
    public Vector3 PadPoint, PadNormal = Vector3.up, PadForward = Vector3.forward;
    /// <summary>The skin between the thumb and the index finger, where the edge of a held thing sits.</summary>
    public Vector3 Web;
    /// <summary>Whose buttons these fingers follow. The scene builder sets it; without one the hand stays loose.</summary>
    public XRBaseInputInteractor Interactor;

    public float Grip { get; private set; }
    public float Trigger { get; private set; }
    public bool Holding { get; private set; }
    /// <summary>On the thing it holds, by that thing's shape, rather than on the controller.</summary>
    public bool Fitted => fitted && heldFrame != null;
    /// <summary>All the way back where the controller is.</summary>
    public bool OnController => !Fitted && fit <= 0f;

    // The last of each is the thumb's swing down (ThumbDownAxis).
    readonly float[] angles = new float[Fingers * JointsPerFinger + 1];
    readonly float[] graspAngles = new float[Fingers * JointsPerFinger + 1];
    const int ThumbDown = Fingers * JointsPerFinger;
    bool posed;

    Transform heldThing, heldFrame;
    BoxCollider heldBox;
    bool fitted;
    float fit;
    Vector3 graspPosition, homePosition, leavePosition;
    Quaternion graspRotation = Quaternion.identity, homeRotation = Quaternion.identity, leaveRotation = Quaternion.identity;

    // The box being fitted to, in the world.
    Vector3 boxCenter, boxHalf;
    Quaternion boxRotation = Quaternion.identity;

    /// <summary>
    /// "-show-controllers" on the command line draws the rig's controller models again and no hands, to compare the two
    /// (in a headset: whether the hand lies where the real one is).
    /// </summary>
    void Awake()
    {
        homePosition = transform.localPosition;
        homeRotation = transform.localRotation;
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-show-controllers") < 0) return;
        if (transform.parent != null)
            foreach (Transform sibling in transform.parent)
                if (sibling.name.EndsWith("Controller Visual")) sibling.gameObject.SetActive(true);
        gameObject.SetActive(false);
    }

    void OnEnable() => Application.onBeforeRender += Place;
    void OnDisable() => Application.onBeforeRender -= Place;

    void LateUpdate()
    {
        if (Interactor != null)
        {
            Grip = Mathf.Clamp01(Interactor.selectInput.ReadValue());
            Trigger = Mathf.Clamp01(Interactor.activateInput.ReadValue());
            Holding = Interactor.hasSelection;
            var thing = Holding ? Interactor.interactablesSelected[0] : null;
            var now = thing != null ? thing.transform : null;
            if (now != heldThing)
            {
                if (heldThing != null || fitted) LetGo();
                if (now != null) Take(thing);
                heldThing = now;
            }
        }
        if (fitted && heldFrame == null) LetGo();
        fit = Mathf.MoveTowards(fit, fitted ? 1f : 0f, Time.deltaTime / MoveToGraspSeconds);
        Apply(Grip, Trigger, Holding, posed ? 1f - Mathf.Exp(-FollowPerSecond * Time.deltaTime) : 1f);
        Place();
    }

    /// <summary>The hand as it was modelled, every joint at 0.</summary>
    public void PoseAsModelled()
    {
        for (int i = 0; i < angles.Length; i++) angles[i] = 0f;
        ShowAngles(angles);
    }

    /// <summary>Puts the fingers where these inputs lead at once. The editor's pictures use it; play goes through LateUpdate.</summary>
    public void PoseNow(float grip, float trigger, bool holding)
    {
        Grip = grip;
        Trigger = trigger;
        Holding = holding;
        fitted = false;
        Apply(grip, trigger, holding, 1f);
    }

    /// <summary>Puts the hand and its fingers on the box of the last <see cref="FitTo"/> at once. For the editor's pictures.</summary>
    public void PoseFitNow()
    {
        if (heldFrame == null) return;
        fitted = true;
        fit = 1f;
        transform.SetPositionAndRotation(heldFrame.TransformPoint(graspPosition), heldFrame.rotation * graspRotation);
        for (int i = 0; i < angles.Length; i++) angles[i] = graspAngles[i];
        ShowAngles(angles);
        posed = true;
    }

    void Apply(float grip, float trigger, bool holding, float follow)
    {
        posed = true;
        bool onThing = Fitted;
        for (int finger = 0; finger < Fingers; finger++)
            for (int joint = 0; joint < JointsPerFinger; joint++)
            {
                int i = finger * JointsPerFinger + joint;
                float target = onThing ? graspAngles[i] : Target(finger, joint, grip, trigger, holding);
                angles[i] = Mathf.Lerp(angles[i], target, follow);
            }
        angles[ThumbDown] = Mathf.Lerp(angles[ThumbDown], onThing ? graspAngles[ThumbDown] : 0f, follow);
        ShowAngles(angles);
    }

    static float Target(int finger, int joint, float grip, float trigger, bool holding)
    {
        float loose = Loose[finger, joint], fist = Fist[finger, joint];
        if (holding) return Mathf.Lerp(loose, fist, HoldClose);
        if (finger == Index)
        {
            // The more the other fingers close, the straighter the index finger stands out from them, until the trigger pulls it in.
            float open = Mathf.Lerp(loose, Point[joint], grip);
            return Mathf.Lerp(open, fist, trigger);
        }
        if (finger == Thumb) return Mathf.Lerp(loose, fist, Mathf.Max(grip, trigger * 0.5f));
        return Mathf.Lerp(loose, fist, grip);
    }

    void ShowAngles(float[] degrees)
    {
        for (int i = 1; i < Joints.Length; i++)
            if (Joints[i] != null) Joints[i].localRotation = Quaternion.AngleAxis(degrees[i], CurlAxis[i]) * Rest[i];
        if (Joints[0] != null) SetThumbBase(degrees[0], degrees[ThumbDown]);
    }

    void SetJoint(int finger, int joint, float degrees)
    {
        int i = finger * JointsPerFinger + joint;
        Joints[i].localRotation = Quaternion.AngleAxis(degrees, CurlAxis[i]) * Rest[i];
    }

    void SetThumbBase(float round, float down) =>
        Joints[0].localRotation = Quaternion.AngleAxis(round, CurlAxis[0]) * Quaternion.AngleAxis(down, ThumbDownAxis) * Rest[0];

    /// <summary>All three joints of a finger, each this share of the way to the fist.</summary>
    void SetFinger(int finger, float share)
    {
        for (int joint = 0; joint < JointsPerFinger; joint++) SetJoint(finger, joint, share * Fist[finger, joint]);
    }

    // ---------- Taking and letting go ----------

    void Take(IXRSelectInteractable thing)
    {
        var box = NearestBox(thing);
        if (box == null || transform.parent == null) return;
        var parent = transform.parent;
        if (FitTo(box, parent.TransformPoint(homePosition), parent.rotation * homeRotation)) fit = 0f;
    }

    void LetGo()
    {
        // From wherever the hand is now, back to the controller.
        leavePosition = transform.localPosition;
        leaveRotation = transform.localRotation;
        if (fitted) fit = 1f;
        fitted = false;
        heldFrame = null;
        heldBox = null;
    }

    /// <summary>Drawn again right before the picture is made, after the rig and what it holds have had their last move.</summary>
    [BeforeRenderOrder(200)]
    void Place()
    {
        if (!Application.isPlaying || transform.parent == null) return;
        float share = Mathf.SmoothStep(0f, 1f, fit);
        if (Fitted)
        {
            var parent = transform.parent;
            var position = Vector3.Lerp(parent.TransformPoint(homePosition), heldFrame.TransformPoint(graspPosition), share);
            var rotation = Quaternion.Slerp(parent.rotation * homeRotation, heldFrame.rotation * graspRotation, share);
            transform.SetPositionAndRotation(position, rotation);
        }
        else
        {
            transform.localPosition = Vector3.Lerp(homePosition, leavePosition, share);
            transform.localRotation = Quaternion.Slerp(homeRotation, leaveRotation, share);
        }
    }

    /// <summary>The solid box of the thing that is nearest the palm. Triggers (a product's barcode face) do not count.</summary>
    BoxCollider NearestBox(IXRSelectInteractable thing)
    {
        if (!(thing is XRBaseInteractable interactable)) return null;
        var palm = transform.TransformPoint(PadPoint);
        BoxCollider nearest = null;
        float nearestDistance = float.PositiveInfinity;
        foreach (var collider in interactable.colliders)
        {
            if (!(collider is BoxCollider box) || !box.enabled || box.isTrigger) continue;
            float distance = (box.ClosestPoint(palm) - palm).sqrMagnitude;
            if (distance >= nearestDistance) continue;
            nearest = box;
            nearestDistance = distance;
        }
        return nearest;
    }

    // ---------- The fit ----------

    /// <summary>
    /// Works out how this hand holds a box, starting from where the hand is now (<paramref name="startPosition"/>,
    /// <paramref name="startRotation"/>): which face the fingers lie on, where along its edge, how far each finger closes
    /// before it touches, and where the thumb touches the other side. Nothing is moved; the hand goes there over the next
    /// frames (or at once with <see cref="PoseFitNow"/>).
    /// </summary>
    /// <returns>False when there is no box. The hand then holds the thing the plain way.</returns>
    public bool FitTo(BoxCollider box, Vector3 startPosition, Quaternion startRotation)
    {
        if (box == null) return false;
        var frame = box.transform;
        ReadBox(box);
        var axes = new[] { frame.right, frame.up, frame.forward };
        int thin = boxHalf.x <= boxHalf.y && boxHalf.x <= boxHalf.z ? 0 : boxHalf.y <= boxHalf.z ? 1 : 2;
        int longest = boxHalf.x >= boxHalf.y && boxHalf.x >= boxHalf.z ? 0 : boxHalf.y >= boxHalf.z ? 1 : 2;
        if (longest == thin) longest = (thin + 1) % 3;
        int middle = 3 - thin - longest;
        bool pinch = boxHalf[longest] * 2f < TinyThing;
        bool bar = !pinch && boxHalf[middle] * 2f <= BarWidth && boxHalf[longest] >= BarLengths * boxHalf[middle];
        float handScale = transform.lossyScale.x;

        // The face the fingers lie on: the one the palm already faces most. A flat thing leans toward its two broad
        // faces, where a thumb can come round to the other side; a bar is never held by its ends; and no palm goes on
        // an edge as narrow as a card's.
        var backOfHand = startRotation * PadNormal;
        int faceAxis = thin;
        var faceOut = axes[thin];
        float bestScore = float.NegativeInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float narrowest = Mathf.Min(boxHalf[(axis + 1) % 3], boxHalf[(axis + 2) % 3]) * 2f;
            if (axis != thin && (pinch || narrowest < NarrowFace)) continue;
            if (bar && axis == longest) continue;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float score = Vector3.Dot(axes[axis] * sign, backOfHand) + (axis == thin && !bar ? ThinFaceBonus : 0f);
                if (score <= bestScore) continue;
                bestScore = score;
                faceAxis = axis;
                faceOut = axes[axis] * sign;
            }
        }
        int u = (faceAxis + 1) % 3, v = (faceAxis + 2) % 3;
        float thickness = boxHalf[faceAxis] * 2f;

        // The way the fingers run across that face: as they point now, laid flat. Across a bar, never along it.
        var forward = Vector3.ProjectOnPlane(startRotation * PadForward, faceOut);
        if (forward.sqrMagnitude < 0.04f) forward = Vector3.ProjectOnPlane(boxCenter - startPosition, faceOut);
        if (forward.sqrMagnitude < 1e-6f) forward = axes[u];
        if (bar)
        {
            var acrossBar = Vector3.Cross(faceOut, axes[longest]);
            forward = Vector3.Dot(acrossBar, forward) < 0f ? -acrossBar : acrossBar;
        }
        forward.Normalize();
        var rotation = Quaternion.LookRotation(forward, faceOut) * Quaternion.Inverse(Quaternion.LookRotation(PadForward, PadNormal));

        // Where on the face. The joints are put as modelled to read where the hand's own parts are.
        var keptPosition = transform.position;
        var keptRotation = transform.rotation;
        transform.SetPositionAndRotation(startPosition, rotation);
        for (int finger = Index; finger < Fingers; finger++) SetFinger(finger, 0f);
        SetJoint(Thumb, 1, 0f);
        SetJoint(Thumb, 2, 0f);
        SetThumbBase(0f, 0f);
        Vector3 Local(Vector3 world) => transform.InverseTransformPoint(world);
        Vector3 OnPads(Vector3 local) => local - PadNormal * Vector3.Dot(local - PadPoint, PadNormal);
        // From the index finger to the little finger across the pads, as the hand is turned now.
        var toLittle = rotation * (OnPads(Local(Joints[Little * JointsPerFinger].position)) - OnPads(Local(Joints[Index * JointsPerFinger].position))).normalized;
        // Along one of the box's axes: a place for a part of the hand that keeps the whole hand's width on the box.
        float Along(int axis, float wanted, float before, float after)
        {
            float toward = Vector3.Dot(toLittle, axes[axis]) * handScale;
            float low = -boxHalf[axis] + EdgeMargin + Mathf.Max(before * toward, -after * toward);
            float high = boxHalf[axis] - EdgeMargin - Mathf.Max(after * toward, -before * toward);
            return low <= high ? Mathf.Clamp(wanted, low, high) : (before - after) * 0.5f * toward;
        }
        Vector3 anchor, anchorAt;
        // The way out of the box at the side the thumb is on, when the hand is at an edge of the face.
        var sideOut = -forward;
        bool atEdge = true;
        if (pinch)
        {
            // A coin: the middle of its face under the pad of the bent index finger.
            SetFinger(Index, PinchClose);
            anchor = Local(Tips[Index].position) - PadNormal * FingerPad;
            anchorAt = boxCenter + faceOut * boxHalf[faceAxis];
        }
        else if (bar)
        {
            // A bar: under the first joints of the fingers, the middle finger about where the hand is along the bar.
            int first = Middle * JointsPerFinger;
            anchor = OnPads((Local(Joints[first].position) + Local(Joints[first + 1].position)) * 0.5f);
            float wanted = Vector3.Dot(transform.TransformPoint(anchor) - boxCenter, axes[longest]);
            anchorAt = boxCenter + faceOut * boxHalf[faceAxis] + axes[longest] * Along(longest, wanted, HandSpan * 0.4f, HandSpan * 0.6f);
        }
        else
        {
            // Back along the fingers from where the hand is to the edge of the face: that edge goes into the web of the
            // thumb. Then along the edge, far enough from its ends for all four fingers to land on the face.
            anchor = OnPads(Web);
            var from = transform.TransformPoint(anchor) - boxCenter;
            float qu = Mathf.Clamp(Vector3.Dot(from, axes[u]), -boxHalf[u], boxHalf[u]);
            float qv = Mathf.Clamp(Vector3.Dot(from, axes[v]), -boxHalf[v], boxHalf[v]);
            float fu = Vector3.Dot(forward, axes[u]), fv = Vector3.Dot(forward, axes[v]);
            float toU = Mathf.Abs(fu) > 1e-4f ? ((fu > 0f ? -boxHalf[u] : boxHalf[u]) - qu) / -fu : float.PositiveInfinity;
            float toV = Mathf.Abs(fv) > 1e-4f ? ((fv > 0f ? -boxHalf[v] : boxHalf[v]) - qv) / -fv : float.PositiveInfinity;
            float back = Mathf.Max(0f, Mathf.Min(toU, toV));
            atEdge = back <= FarthestEdge;
            if (atEdge)
            {
                qu -= fu * back;
                qv -= fv * back;
                bool edgeRunsAlongV = toU <= toV;
                sideOut = edgeRunsAlongV ? axes[u] * (fu > 0f ? -1f : 1f) : axes[v] * (fv > 0f ? -1f : 1f);
                if (edgeRunsAlongV) qv = Along(v, qv, 0f, HandSpan);
                else qu = Along(u, qu, 0f, HandSpan);
            }
            anchorAt = boxCenter + faceOut * boxHalf[faceAxis] + axes[u] * qu + axes[v] * qv;
        }
        var position = anchorAt - rotation * (anchor * handScale);
        transform.SetPositionAndRotation(position, rotation);

        // Each finger closes until it touches. One that never touches closes a little more than those that do.
        var shares = new Vector3[Fingers];
        var touches = new bool[Fingers];
        float touching = 0f;
        int touchers = 0;
        for (int finger = Index; finger < Fingers; finger++)
        {
            if (pinch && finger == Index)
            {
                shares[finger] = Vector3.one * PinchClose;
                touches[finger] = true;
                continue;
            }
            touches[finger] = CloseFinger(finger, handScale, thickness >= SheetThickness, out shares[finger]);
            if (!touches[finger]) continue;
            touching += shares[finger].x;
            touchers++;
        }
        float free = touchers > 0 ? Mathf.Clamp(touching / touchers + 0.25f, 0.3f, 0.8f) : HoldClose;
        for (int finger = Index; finger < Fingers; finger++)
        {
            if (!touches[finger]) shares[finger] = Vector3.one * free;
            for (int joint = 0; joint < JointsPerFinger; joint++)
            {
                graspAngles[finger * JointsPerFinger + joint] = shares[finger][joint] * Fist[finger, joint];
                SetJoint(finger, joint, graspAngles[finger * JointsPerFinger + joint]);
            }
        }

        // The thumb. A pinch: right under the tip of the index finger. A thin thing: round to the other face, under the
        // index finger. A thick one: on the side, a little below the edge the hand is at.
        Vector3 aim;
        if (pinch)
            aim = Tips[Index].position - faceOut * (Vector3.Dot(Tips[Index].position - anchorAt, faceOut) + thickness + ThumbPad * handScale);
        else if (!atEdge)
            // Flat on a large face, far from its edges: the thumb lies on the same face beside the fingers.
            aim = anchorAt;
        else if (thickness <= ThumbRoundThickness)
        {
            var underIndex = (Joints[Index * JointsPerFinger].position + Joints[Index * JointsPerFinger + 1].position) * 0.5f;
            aim = underIndex - faceOut * (Vector3.Dot(underIndex - anchorAt, faceOut) + thickness + ThumbPad * handScale);
        }
        else
            aim = anchorAt + sideOut * (ThumbPad * handScale) - faceOut * Mathf.Min(ThumbDownTheSide, thickness * 0.6f);
        FitThumb(aim, handScale);

        heldFrame = frame;
        heldBox = box;
        graspPosition = frame.InverseTransformPoint(position);
        graspRotation = Quaternion.Inverse(frame.rotation) * rotation;
        fitted = true;
        // The search moved the hand and its fingers about; back to how they were shown.
        transform.SetPositionAndRotation(keptPosition, keptRotation);
        ShowAngles(angles);
        return true;
    }

    void ReadBox(BoxCollider box)
    {
        var frame = box.transform;
        var scale = frame.lossyScale;
        boxHalf = new Vector3(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y), Mathf.Abs(box.size.z * scale.z)) * 0.5f;
        boxCenter = frame.TransformPoint(box.center);
        boxRotation = frame.rotation;
    }

    /// <summary>How far the end of a finger is from the surface of the box the hand is fitted to, negative inside it. For the checks.</summary>
    public float TipFromHeld(int finger)
    {
        if (!Fitted || heldBox == null) return float.PositiveInfinity;
        ReadBox(heldBox);
        return FromBox(Tips[finger].position);
    }

    /// <summary>How the fit came out, for the reports.</summary>
    public string DescribeFit()
    {
        if (!Fitted) return "not fitted";
        var text = new System.Text.StringBuilder();
        string[] names = { "thumb", "index", "middle", "ring", "little" };
        for (int finger = 0; finger < Fingers; finger++)
            text.Append($"{names[finger]} {TipFromHeld(finger) * 1000f:0} mm ({graspAngles[finger * JointsPerFinger]:0}/{graspAngles[finger * JointsPerFinger + 1]:0}/{graspAngles[finger * JointsPerFinger + 2]:0}); ");
        text.Append($"thumb down {graspAngles[ThumbDown]:0}");
        return text.ToString();
    }

    /// <summary>How far out of the box a point is, negative inside.</summary>
    float FromBox(Vector3 point)
    {
        var local = Quaternion.Inverse(boxRotation) * (point - boxCenter);
        var over = new Vector3(Mathf.Abs(local.x) - boxHalf.x, Mathf.Abs(local.y) - boxHalf.y, Mathf.Abs(local.z) - boxHalf.z);
        float outside = Vector3.Max(over, Vector3.zero).magnitude;
        return outside > 0f ? outside : Mathf.Max(over.x, Mathf.Max(over.y, over.z));
    }

    /// <summary>
    /// The innermost part of a finger past <paramref name="joint"/> that touches the box: 1 and 2 are the finger at its
    /// second and third joints, 3 is its tip. 0 when none does.
    /// </summary>
    int Touching(int finger, int joint, float handScale)
    {
        int first = finger * JointsPerFinger;
        if (joint < 1 && FromBox(Joints[first + 1].position) <= JointPad * handScale) return 1;
        if (joint < 2 && FromBox(Joints[first + 2].position) <= JointPad * handScale) return 2;
        return FromBox(Tips[finger].position) <= FingerPad * handScale ? 3 : 0;
    }

    /// <summary>
    /// Closes a finger on the box: all its joints together until some part of it touches, then, for a thing thick enough
    /// to wrap, only the joints past that part until a part further out touches, and so on to the tip.
    /// </summary>
    /// <param name="shares">For each joint, the share of the way to the fist it ends at.</param>
    /// <returns>False when the finger closes all the way without touching anything.</returns>
    bool CloseFinger(int finger, float handScale, bool wrap, out Vector3 shares)
    {
        const float step = 0.04f;
        shares = Vector3.one * OpenPastRest;
        var at = shares;
        void Show(int from, float share)
        {
            for (int joint = 0; joint < JointsPerFinger; joint++)
                SetJoint(finger, joint, (joint >= from ? share : at[joint]) * Fist[finger, joint]);
        }
        int lead = 0;
        while (lead < JointsPerFinger)
        {
            float begin = at[lead], found = float.NaN;
            int part = 0;
            for (float share = begin; share <= 1f; share += step)
            {
                Show(lead, share);
                part = Touching(finger, lead, handScale);
                if (part == 0) continue;
                found = share;
                break;
            }
            if (float.IsNaN(found))
            {
                if (lead == 0) return false;
                // Nothing further out to land on: the outer joints only follow the curl a little way.
                for (int joint = lead; joint < JointsPerFinger; joint++) at[joint] = Mathf.Min(1f, begin + CurlOnWithoutTouch);
                break;
            }
            if (found > begin)
            {
                float open = found - step, shut = found;
                for (int i = 0; i < 5; i++)
                {
                    float middle = (open + shut) * 0.5f;
                    Show(lead, middle);
                    int there = Touching(finger, lead, handScale);
                    if (there != 0)
                    {
                        shut = middle;
                        part = there;
                    }
                    else open = middle;
                }
                found = shut;
            }
            for (int joint = lead; joint < JointsPerFinger; joint++) at[joint] = found;
            if (part >= JointsPerFinger || !wrap) break;
            lead = part;
        }
        shares = at;
        Show(JointsPerFinger, 0f);
        return true;
    }

    /// <summary>
    /// Tries the thumb's joints over their ranges and keeps the angles at which its tip rests on the box nearest
    /// <paramref name="aim"/> with no joint inside the box.
    /// </summary>
    void FitThumb(Vector3 aim, float handScale)
    {
        float pad = ThumbPad * handScale, joint = JointPad * handScale;
        var best = new Vector4(Fist[Thumb, 0], Fist[Thumb, 1], Fist[Thumb, 2], 0f) * HoldClose;
        float bestCost = float.PositiveInfinity;
        void Try(float round, float down, float second, float third)
        {
            SetJoint(Thumb, 2, third);
            var tip = Tips[Thumb].position;
            float cost = 3f * Mathf.Abs(FromBox(tip) - pad)
                         + 4f * Mathf.Max(0f, joint - FromBox(Joints[2].position))
                         + 4f * Mathf.Max(0f, joint - FromBox(Joints[1].position))
                         + Vector3.Distance(tip, aim)
                         // Of the ways to get there, the one that bends the thumb least oddly.
                         + 0.0003f * (down + Mathf.Abs(second - 15f) + Mathf.Abs(third - 20f));
            if (cost >= bestCost) return;
            bestCost = cost;
            best = new Vector4(round, second, third, down);
        }
        for (float down = 0f; down <= 75f; down += 15f)
            for (float round = 0f; round <= 84f; round += 12f)
            {
                SetThumbBase(round, down);
                for (float second = -10f; second <= 56f; second += 11f)
                {
                    SetJoint(Thumb, 1, second);
                    for (float third = 0f; third <= 72f; third += 18f) Try(round, down, second, third);
                }
            }
        var coarse = best;
        for (float down = coarse.w - 7.5f; down <= coarse.w + 7.5f; down += 3.75f)
            for (float round = coarse.x - 6f; round <= coarse.x + 6f; round += 3f)
            {
                SetThumbBase(round, down);
                for (float second = coarse.y - 5.5f; second <= coarse.y + 5.5f; second += 2.75f)
                {
                    SetJoint(Thumb, 1, second);
                    for (float third = coarse.z - 9f; third <= coarse.z + 9f; third += 4.5f) Try(round, down, second, third);
                }
            }
        graspAngles[0] = best.x;
        graspAngles[1] = best.y;
        graspAngles[2] = best.z;
        graspAngles[ThumbDown] = best.w;
        SetThumbBase(best.x, best.w);
        SetJoint(Thumb, 1, best.y);
        SetJoint(Thumb, 2, best.z);
    }
}
