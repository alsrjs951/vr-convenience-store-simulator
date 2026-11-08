using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A main flap (doc 5.7.1 B4). While held, the flap turns to the angle of the hand around the hinge, measured in the plane
/// across the hinge (0° closed, 90° upright, 180° flat outside, clamped to 210°). Let go at 110° or more and it falls open
/// to 210°; less and it closes again.
/// </summary>
public class BoxFlap : MonoBehaviour
{
    public const float OpenAngle = 210f;
    public const float OpenFrom = 110f;
    const float SettleDegreesPerSecond = 600f;

    public StockBox Box { get; private set; }
    public Collider Panel { get; private set; }
    public XRSimpleInteractable Interactable { get; private set; }
    public float Angle { get; private set; }
    public bool IsOpen { get; private set; }
    public float ReleasedAt { get; private set; } = -1f;
    public int Closes { get; private set; }
    /// <summary>Box-local direction from the hinge over the opening.</summary>
    public Vector3 Inward { get; private set; }

    Transform hand;
    Renderer panelRenderer;
    Coroutine settling;

    public static BoxFlap Create(StockBox box, string name, Vector3 hinge, Vector3 inward, float length, float width, Material material)
    {
        var pivot = new GameObject(name);
        pivot.transform.SetParent(box.transform, false);
        pivot.transform.localPosition = hinge;
        var flap = pivot.AddComponent<BoxFlap>();
        flap.Box = box;
        flap.Inward = inward;
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "Panel";
        panel.transform.SetParent(pivot.transform, false);
        panel.transform.localPosition = inward * (width * 0.5f) + Vector3.up * 0.002f;
        panel.transform.localScale = new Vector3(length, 0.004f, width);
        flap.panelRenderer = panel.GetComponent<Renderer>();
        flap.panelRenderer.sharedMaterial = material;
        flap.Panel = panel.GetComponent<Collider>();
        flap.Interactable = panel.AddComponent<XRSimpleInteractable>();
        flap.Interactable.colliders.Clear();
        flap.Interactable.colliders.Add(flap.Panel);
        flap.Interactable.selectEntered.AddListener(flap.OnGrabbed);
        flap.Interactable.selectExited.AddListener(flap.OnReleased);
        flap.Interactable.enabled = false;
        return flap;
    }

    public void SetInteractable(bool on)
    {
        Interactable.enabled = on;
        if (!on && IsOpen) Panel.enabled = false;
    }

    public void SetMaterial(Material material)
    {
        if (panelRenderer.sharedMaterial != material) panelRenderer.sharedMaterial = material;
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (settling != null) StopCoroutine(settling);
        hand = args.interactorObject.GetAttachTransform(Interactable);
        if (args.interactorObject is UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInputInteractor input) input.SendHapticImpulse(0.3f, 0.05f);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        hand = null;
        ReleasedAt = Time.time;
        bool open = Angle >= OpenFrom;
        if (!open && Angle > 1f) Closes++;
        settling = StartCoroutine(SettleTo(open ? OpenAngle : 0f, open));
    }

    void Update()
    {
        if (hand == null) return;
        SetAngle(AngleOf(hand.position));
    }

    /// <summary>The hand's angle around the hinge: 0 over the opening, 90 straight up, 180 out over the side.</summary>
    public float AngleOf(Vector3 world)
    {
        var p = Box.transform.InverseTransformPoint(world) - transform.localPosition;
        float across = Vector3.Dot(new Vector3(0f, 0f, p.z), Inward);
        float degrees = Mathf.Atan2(p.y, across) * Mathf.Rad2Deg;
        if (degrees < -90f) degrees += 360f;
        return Mathf.Clamp(degrees, 0f, OpenAngle);
    }

    /// <summary>The point at a given angle and distance from the hinge, in world space. For tests aiming a hand.</summary>
    public Vector3 PointAt(float degrees, float radius)
    {
        float r = degrees * Mathf.Deg2Rad;
        var local = transform.localPosition + Inward * (Mathf.Cos(r) * radius) + Vector3.up * (Mathf.Sin(r) * radius);
        return Box.transform.TransformPoint(local);
    }

    void SetAngle(float degrees)
    {
        Angle = degrees;
        // Turning the inward direction up: about +x for a flap whose inward is -z, about -x for one whose inward is +z.
        float sign = Inward.z < 0f ? 1f : -1f;
        transform.localRotation = Quaternion.AngleAxis(sign * degrees, Vector3.right);
    }

    IEnumerator SettleTo(float target, bool open)
    {
        while (!Mathf.Approximately(Angle, target))
        {
            SetAngle(Mathf.MoveTowards(Angle, target, SettleDegreesPerSecond * Time.deltaTime));
            yield return null;
        }
        settling = null;
        if (open && !IsOpen)
        {
            IsOpen = true;
            if (Box.Intake != null && Box.Intake.Audio) Box.Intake.Audio.PlayAt(StoreSound.Flap, transform.position);
        }
        Box.OnFlapChanged();
    }
}
