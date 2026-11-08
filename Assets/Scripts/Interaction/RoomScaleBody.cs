using Unity.XR.CoreUtils;
using UnityEngine;
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
/// </summary>
[RequireComponent(typeof(CharacterController), typeof(XROrigin))]
public class RoomScaleBody : MonoBehaviour, IXRBodyPositionEvaluator
{
    const int MaxSlides = 3;
    const float NoMotion = 1e-8f;

    // Just short of the capsule's radius plus skin, so that a head leaning at a wall stays on this side of its surface.
    public float MaxLean = 0.2f;

    XROrigin rig;
    CharacterController capsule;
    int solidLayers;
    Vector3 body;

    public Vector3 BodyWorldPosition => transform.TransformPoint(body);
    public Vector3 Lean => HeadInOrigin() - body;

    void Awake()
    {
        rig = GetComponent<XROrigin>();
        capsule = GetComponent<CharacterController>();
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(gameObject.layer, layer))
                solidLayers |= 1 << layer;
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
