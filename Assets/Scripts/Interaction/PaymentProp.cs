using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>A card, bill or coin. Falling below the counter returns it to where it started (doc 5.5 and game design 12).</summary>
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class PaymentProp : MonoBehaviour
{
    const float ReturnBelowY = 0.5f;

    public int Value;
    public bool IsCard;
    public XRGrabInteractable Grab { get; private set; }

    Vector3 homePosition;
    Quaternion homeRotation;
    Rigidbody body;

    void Awake()
    {
        Grab = GetComponent<XRGrabInteractable>();
        body = GetComponent<Rigidbody>();
        homePosition = transform.position;
        homeRotation = transform.rotation;
    }

    void FixedUpdate()
    {
        if (!Grab.isSelected && transform.position.y < ReturnBelowY) ReturnHome();
    }

    public void ReturnHome()
    {
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        transform.SetPositionAndRotation(homePosition, homeRotation);
    }
}
