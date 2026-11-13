using UnityEngine;

/// <summary>
/// A thing a customer has taken off the counter with the left hand (2026-11-13): goods bought without a bag, which
/// until then were simply gone the moment the sale was done. It is not a child of the hand, or it would turn with the
/// wrist: it follows the hand as the hand closed on it, over its top. What the customer puts away at their side is
/// gone there; the last thing stays in the hand and goes out of the store with them, and then it hangs from the
/// fingers by its top, its narrow side turned to the leg (CustomerDirector.TakeGoods).
/// </summary>
public class CarriedGoods : MonoBehaviour
{
    // Hanging, its top is this far above the knuckles, between the fingers and the thumb (3 cm under them it hung
    // below a closed fist, 2026-11-13); and this far under the wrist when the body has no finger bones.
    const float AboveKnuckles = 0.015f, BelowWrist = 0.06f;
    // Clear of the trouser leg.
    const float AwayFromBody = 0.03f;

    public CustomerCharacter Carrier { get; private set; }
    public ProductPrefab Product { get; private set; }
    /// <summary>Hanging from the fingers at the customer's side, as opposed to held over its top on the way there. For the checks.</summary>
    public bool Hanging => hung >= 1f;
    /// <summary>The middle of the thing, wherever it is drawn.</summary>
    public Vector3 Middle => Product != null ? Product.transform.TransformPoint(Product.Proxy.center) : transform.position;

    Transform wrist, knuckles;
    // From the wrist to the thing's middle as the hand closed on it, and how the thing was turned: both as the carrier's body has them.
    Vector3 held;
    Quaternion turned;
    // From the thing's own origin to its middle, as the thing has it.
    Vector3 middle;
    // Half its height as it is held, and how far it turns about the vertical to hang with its narrow side to the leg.
    float half, turn;
    float hung, perSecond;

    /// <summary>
    /// Into the customer's left hand, the wrist being <paramref name="toWrist"/> from the thing's middle. From here on
    /// the thing goes where the hand goes; nobody else can take it.
    /// </summary>
    public static CarriedGoods Carry(CustomerCharacter carrier, ProductPrefab product, Bounds box, Vector3 toWrist)
    {
        var carried = product.gameObject.AddComponent<CarriedGoods>();
        var body = carrier.transform;
        carried.Carrier = carrier;
        carried.Product = product;
        carried.wrist = carrier.LeftHand;
        carried.knuckles = carrier.LeftKnuckles;
        carried.held = Quaternion.Inverse(body.rotation) * -toWrist;
        carried.turned = Quaternion.Inverse(body.rotation) * product.transform.rotation;
        carried.middle = Quaternion.Inverse(product.transform.rotation) * (box.center - product.transform.position);
        carried.half = box.extents.y;
        // Its broad side across the body would hang through the leg.
        float across = Mathf.Abs(body.right.x) * box.extents.x + Mathf.Abs(body.right.z) * box.extents.z;
        float along = Mathf.Abs(body.forward.x) * box.extents.x + Mathf.Abs(body.forward.z) * box.extents.z;
        carried.turn = across > along ? 90f : 0f;
        product.Grab.enabled = false;
        product.Body.isKinematic = true;
        product.Body.interpolation = RigidbodyInterpolation.None;
        foreach (var part in product.GetComponentsInChildren<Collider>()) part.enabled = false;
        // It goes when its carrier goes.
        product.transform.SetParent(body, true);
        return carried;
    }

    /// <summary>From held over its top to hanging from the fingers at the customer's side, in this long.</summary>
    public void Hang(float seconds)
    {
        perSecond = 1f / Mathf.Max(0.01f, seconds);
        // Down at the customer's side and on its way to the door it is the light copy, as what stands in a box is: the
        // next customer's goods are about to be made, and the store has only so many Gaussians to draw (doc 7.3).
        if (Product != null) Product.UseLightSplat();
    }

    // After the animator has posed the hand for this frame, and after the arms have reached (CustomerArm, order -40).
    void LateUpdate()
    {
        if (Carrier == null || Product == null || wrist == null) return;
        hung = Mathf.MoveTowards(hung, 1f, perSecond * Time.deltaTime);
        float share = Mathf.SmoothStep(0f, 1f, hung);
        var body = Carrier.transform;
        var inHand = wrist.position + body.rotation * held;
        var grip = (knuckles != null ? knuckles.position + Vector3.up * AboveKnuckles : wrist.position + Vector3.down * BelowWrist) - body.right * AwayFromBody;
        var at = Vector3.Lerp(inHand, grip + Vector3.down * half, share);
        var rotation = Quaternion.AngleAxis(turn * share, Vector3.up) * body.rotation * turned;
        Product.TeleportTo(at - rotation * middle, rotation);
    }
}
