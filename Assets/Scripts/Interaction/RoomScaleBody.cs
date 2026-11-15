using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

/// <summary>
/// Keeps the CharacterController under the head as the player really walks, without ever letting it enter anything.
///
/// XRI's default puts the capsule straight under the head each frame, inside a counter or wall if that is where the
/// head went, and the next stick input makes PhysX push it out whichever way it picks. Measured: with the head 15 cm
/// over the counter's near edge, a press of the stick threw the player out past the far side.
///
/// Here the capsule stops at the obstacle. The head may lean past it by MaxLean, to reach over the counter. A step
/// beyond that slides the rig back, so a real step cannot get through either. In the open the capsule is under the head,
/// the same as XRI's default.
///
/// It also keeps the player's height (2026-11-15, after a headset session in which the view suddenly stood too high).
/// The store is one flat floor and the player's real floor is that floor, so nothing in the game may raise the eyes:
/// - the capsule's step is next to nothing (the rig's own 0.5 m made a stair of anything up to knee height);
/// - a rig that is off the floor all the same is put back on it;
/// - when the headset's poses are plainly measured from the floor, nothing is added to them. XROrigin adds its
///   Camera Y Offset (1.36 m in this scene) whenever the runtime reports the tracking origin as "Device", and a report
///   like that in the middle of a session lifts the view by that much.
/// What it does and what it sees go to the log as [Body] lines, so the next session says which of these it was.
/// </summary>
[RequireComponent(typeof(CharacterController), typeof(XROrigin))]
public class RoomScaleBody : MonoBehaviour, IXRBodyPositionEvaluator
{
    const int MaxSlides = 3;
    const float NoMotion = 1e-8f;

    // Flat floor: only a doormat's worth of height is walked onto.
    public const float StepUp = 0.05f;
    const float FloorSlack = 0.01f;
    // A head this far above the tracking origin is being measured from the floor, not from where the headset started.
    const float FloorTrackedHead = 0.9f;
    const float HeadJump = 0.3f;
    const float NoteEverySeconds = 1f;

    // Just short of the capsule's radius plus skin, so that a head leaning at a wall stays on this side of its surface.
    public float MaxLean = 0.2f;

    XROrigin rig;
    CharacterController capsule;
    int solidLayers;
    Vector3 body;
    float floorY;
    TrackingOriginModeFlags seenMode;
    float seenOffset, seenHead, nextNote;
    bool seen;

    public Vector3 BodyWorldPosition => transform.TransformPoint(body);
    public Vector3 Lean => HeadInOrigin() - body;

    void Awake()
    {
        rig = GetComponent<XROrigin>();
        capsule = GetComponent<CharacterController>();
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(gameObject.layer, layer))
                solidLayers |= 1 << layer;
        capsule.stepOffset = Mathf.Min(capsule.stepOffset, StepUp);
        floorY = transform.position.y;
    }

    public Vector3 GetBodyGroundLocalPosition(XROrigin xrOrigin) => body;

    void LateUpdate()
    {
        var head = HeadInOrigin();
        body += transform.InverseTransformVector(Sweep(transform.TransformVector(head - body)));

        var lean = head - body;
        if (lean.sqrMagnitude > MaxLean * MaxLean)
        {
            // The rig goes back and the capsule's local position forward, so the capsule stays where it is in the world.
            var excess = lean - lean.normalized * MaxLean;
            transform.position -= transform.TransformVector(excess);
            body += excess;
            Physics.SyncTransforms();
        }
        capsule.center = new Vector3(body.x, capsule.center.y, body.z);
        KeepOnTheFloor();
        KeepEyeHeight();
    }

    void KeepOnTheFloor()
    {
        float off = transform.position.y - floorY;
        if (Mathf.Abs(off) <= FloorSlack) return;
        Note($"the rig was {off:+0.000;-0.000} m off the floor at ({transform.position.x:0.00}, {transform.position.z:0.00}) and is put back. Around the feet: {AroundTheFeet()}");
        var position = transform.position;
        position.y = floorY;
        transform.position = position;
        Physics.SyncTransforms();
    }

    void KeepEyeHeight()
    {
        if (rig.CameraFloorOffsetObject == null || rig.Camera == null) return;
        var offsetTransform = rig.CameraFloorOffsetObject.transform;
        var mode = rig.CurrentTrackingOriginMode;
        float offset = offsetTransform.localPosition.y;
        float head = rig.Camera.transform.localPosition.y;
        bool headset = XRSettings.isDeviceActive;
        if (!seen) Debug.Log($"[Body] step {capsule.stepOffset:0.00} m; floor y {floorY:0.000}; headset {headset}; tracking origin {mode}; camera offset {offset:0.000} m; head {head:0.00} m above the tracking origin");
        else if (mode != seenMode || Mathf.Abs(offset - seenOffset) > 0.001f)
            Debug.Log($"[Body] tracking origin {seenMode} -> {mode}; camera offset {seenOffset:0.000} -> {offset:0.000} m; head {head:0.00} m above the tracking origin; headset {headset}");
        if (seen && headset && Mathf.Abs(head - seenHead) > HeadJump)
            Debug.Log($"[Body] the head's tracked height went {seenHead:0.00} -> {head:0.00} m in one frame (the headset's tracking, not the game)");

        if (headset && head > FloorTrackedHead && offset > 0.01f)
        {
            Debug.Log($"[Body] the head is {head:0.00} m above the tracking origin, so the poses are from the floor: the camera offset of {offset:0.000} m (tracking origin {mode}) is taken off");
            var local = offsetTransform.localPosition;
            local.y = 0f;
            offsetTransform.localPosition = local;
            offset = 0f;
        }
        seenMode = mode;
        seenOffset = offset;
        seenHead = head;
        seen = true;
    }

    void Note(string text)
    {
        if (Time.unscaledTime < nextNote) return;
        nextNote = Time.unscaledTime + NoteEverySeconds;
        Debug.Log("[Body] " + text);
    }

    string AroundTheFeet()
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (var hit in Physics.OverlapSphere(BodyWorldPosition + Vector3.up * 0.1f, capsule.radius + 0.15f, solidLayers, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(transform) && names.Count < 5) names.Add(hit.name);
        return names.Count > 0 ? string.Join(", ", names) : "nothing";
    }

    Vector3 HeadInOrigin()
    {
        var head = rig.CameraInOriginSpacePos;
        head.y = 0f;
        return head;
    }

    /// <summary>How far the capsule can go along a wanted world motion, sliding along what it meets.</summary>
    Vector3 Sweep(Vector3 wanted)
    {
        float skin = capsule.skinWidth;
        float radius = capsule.radius;
        float centerHeight = capsule.height * 0.5f + skin;
        float halfSpine = Mathf.Max(0f, capsule.height * 0.5f - radius);
        var moved = Vector3.zero;
        for (int i = 0; i < MaxSlides && wanted.sqrMagnitude > NoMotion; i++)
        {
            var center = BodyWorldPosition + moved + Vector3.up * centerHeight;
            float length = wanted.magnitude;
            var direction = wanted / length;
            if (!Physics.CapsuleCast(center - Vector3.up * halfSpine, center + Vector3.up * halfSpine, radius, direction, out var hit, length + skin, solidLayers, QueryTriggerInteraction.Ignore))
                return moved + wanted;
            float free = Mathf.Max(0f, hit.distance - skin);
            moved += direction * free;
            wanted = Vector3.ProjectOnPlane(direction * (length - free), hit.normal);
            wanted.y = 0f;
        }
        return moved;
    }
}
