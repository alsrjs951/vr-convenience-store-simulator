using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>A card, the money a customer hands over, or change out of the till.</summary>
public enum PaymentKind { Card, CustomerCash, Change }

/// <summary>
/// A card, note or coin. Falling below the counter returns a card or the customer's money to where it started (doc 5.5 and
/// game design 12); change that falls is simply gone, since the till has more. Change starts out lying in its place in the
/// till's drawer, riding with the drawer, and becomes a loose thing the first time it is picked up.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class PaymentProp : MonoBehaviour
{
    const float ReturnBelowY = 0.5f;
    const float SlideSeconds = 0.16f;
    const float SoundImpactSpeed = 0.2f;
    const float SoundGapSeconds = 0.15f;

    public int Value;
    public PaymentKind Kind;
    public bool IsCoin;
    public GameAudio Audio;

    public bool IsCard => Kind == PaymentKind.Card;
    public XRGrabInteractable Grab { get; private set; }
    /// <summary>Still lying in its slot in the till, never picked up.</summary>
    public bool Docked { get; private set; }
    /// <summary>Sliding into the till, about to be gone. Nothing may count it or pick it up.</summary>
    public bool Leaving { get; private set; }

    Vector3 homePosition;
    Quaternion homeRotation;
    Rigidbody body;
    float lastSound = float.NegativeInfinity;

    void Awake()
    {
        Grab = GetComponent<XRGrabInteractable>();
        body = GetComponent<Rigidbody>();
        homePosition = transform.position;
        homeRotation = transform.rotation;
        Grab.selectEntered.AddListener(OnPickedUp);
        Grab.selectExited.AddListener(OnLetGo);
    }

    void OnDestroy()
    {
        if (Grab == null) return;
        Grab.selectEntered.RemoveListener(OnPickedUp);
        Grab.selectExited.RemoveListener(OnLetGo);
    }

    /// <summary>Lays it in a slot of the till's drawer: it moves with the drawer and nothing pushes it about.</summary>
    public void Dock(Transform slot)
    {
        Docked = true;
        transform.SetParent(slot, true);
        // A kinematic body cannot sweep; the sweeping mode comes back when it is picked up.
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        body.isKinematic = true;
        // The drawer carries it by its transform. An interpolated body would lag a frame behind the drawer.
        body.interpolation = RigidbodyInterpolation.None;
        // Once taken out it stays out: the grab must not hand it back to the drawer when it is let go.
        Grab.retainTransformParent = false;
    }

    void OnPickedUp(SelectEnterEventArgs _)
    {
        if (!Docked) return;
        Docked = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        PlaySound();
    }

    void OnLetGo(SelectExitEventArgs _)
    {
        // The grab puts back what the body was when it was picked up. For change that was kinematic, lying in the till.
        if (Kind != PaymentKind.Change || Leaving || Grab.isSelected) return;
        body.isKinematic = false;
        body.useGravity = true;
    }

    void FixedUpdate()
    {
        if (Docked || Leaving || Grab.isSelected || transform.position.y >= ReturnBelowY) return;
        PutBack();
    }

    public float Speed => body.linearVelocity.magnitude;

    /// <summary>
    /// Gone where a hand will not find it (off the counter, or under the film of the bag): change is simply gone, the till
    /// has more; the customer's card or note is back on the plate.
    /// </summary>
    public void PutBack()
    {
        if (Kind == PaymentKind.Change) Destroy(gameObject);
        else ReturnHome();
    }

    public void ReturnHome()
    {
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        transform.SetPositionAndRotation(homePosition, homeRotation);
    }

    /// <summary>Goes into the till: slides to its slot and is gone. Money let go over the open drawer does not just vanish.</summary>
    public void SlideInto(Transform slot)
    {
        if (Leaving) return;
        Leaving = true;
        Grab.enabled = false;
        foreach (var part in GetComponentsInChildren<Collider>()) part.enabled = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.None;
        PlaySound();
        StartCoroutine(Slide(slot));
    }

    IEnumerator Slide(Transform slot)
    {
        var from = transform.position;
        var turned = transform.rotation;
        for (float t = 0f; t < 1f && slot != null; t += Time.deltaTime / SlideSeconds)
        {
            float eased = 1f - (1f - t) * (1f - t);
            transform.SetPositionAndRotation(Vector3.Lerp(from, slot.position, eased), Quaternion.Slerp(turned, slot.rotation, eased));
            yield return null;
        }
        Destroy(gameObject);
    }

    void OnCollisionEnter(Collision collision)
    {
        // A coin set down clinks, a note makes its small paper sound. A card is quiet.
        if (Docked || Leaving || IsCard || collision.relativeVelocity.magnitude < SoundImpactSpeed) return;
        PlaySound();
    }

    void PlaySound()
    {
        if (Audio == null || IsCard || Time.time - lastSound < SoundGapSeconds) return;
        lastSound = Time.time;
        Audio.PlayAt(IsCoin ? StoreSound.Coin : StoreSound.Note, transform.position);
    }
}
