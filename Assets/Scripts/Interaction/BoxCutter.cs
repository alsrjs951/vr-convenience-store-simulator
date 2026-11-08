using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The box cutter by a stand (doc 5.7.3 BoxCutter). It rests in its holder; let go anywhere and it goes back 1 s later
/// (doc 5.7.1 B3). Held with Instantaneous movement so the blade follows the hand exactly along the tape. Only the
/// handle is a collider; the blade tip is a point the boxes test against their tape checkpoints.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class BoxCutter : MonoBehaviour
{
    const float ReturnAfterSeconds = 1f;

    public Transform Tip;
    public Transform Holder;
    public XRGrabInteractable Grab { get; private set; }
    public int Returns { get; private set; }
    public bool InHolder => Holder != null && !Grab.isSelected && Vector3.Distance(transform.position, Holder.position) < 0.005f;

    Rigidbody body;
    IXRSelectInteractor holding;
    Coroutine returning;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        Grab = GetComponent<XRGrabInteractable>();
        Grab.selectEntered.AddListener(args =>
        {
            if (returning != null) StopCoroutine(returning);
            returning = null;
            holding = args.interactorObject;
            if (holding is XRBaseInputInteractor hand) hand.SendHapticImpulse(0.3f, 0.05f);
        });
        Grab.selectExited.AddListener(_ =>
        {
            holding = null;
            body.isKinematic = false;
            returning = StartCoroutine(ReturnToHolder());
        });
        foreach (var capsule in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            foreach (var own in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(own, capsule);
    }

    void Start() => PutBack();

    /// <summary>Doc 5.7.1 B3: a haptic tick as the blade passes a checkpoint.</summary>
    public void Tick()
    {
        if (holding is XRBaseInputInteractor hand) hand.SendHapticImpulse(0.2f, 0.03f);
    }

    IEnumerator ReturnToHolder()
    {
        yield return new WaitForSeconds(ReturnAfterSeconds);
        returning = null;
        if (!Grab.isSelected) PutBack();
    }

    void PutBack()
    {
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = true;
        if (Holder != null) transform.SetPositionAndRotation(Holder.position, Holder.rotation);
        Returns++;
    }
}
