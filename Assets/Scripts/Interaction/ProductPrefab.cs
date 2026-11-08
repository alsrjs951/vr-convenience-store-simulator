using System;
using System.Collections;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>Root holder of a product prefab: data, grab feedback, and the floor-drop safety net (doc 4.3, 5.2).</summary>
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class ProductPrefab : MonoBehaviour
{
    const int HeldRenderOrder = 10;
    const float RespawnDelaySeconds = 3f;
    const float FellOutOfWorldY = -5f;

    public ProductData Data;
    public GaussianSplatRenderer Splat;
    public BoxCollider Proxy;
    public bool IsExpired;
    public bool IsProcessed;

    public float LastScannedTime { get; private set; } = float.NegativeInfinity;
    public int RespawnCount { get; private set; }
    public XRGrabInteractable Grab { get; private set; }
    public bool IsBagged { get; private set; }

    // ---------- Day 2 stock (doc 5.7, 6.4). Day 1 products never set any of these. ----------

    /// <summary>Shelf stock and box units stand still where they are put until a hand takes them; once let go they fall as usual.</summary>
    public bool RestsKinematic { get; private set; }
    /// <summary>The box place this product came out of. A drop or a misplacement sends it back there (doc 5.7.1 B5, B6).</summary>
    public BoxUnit ReturnUnit { get; set; }
    /// <summary>Accepted into a shelf slot: fixed there and no longer grabbable (doc 5.7.3, Individual mode).</summary>
    public bool IsShelved { get; private set; }
    public event Action<ProductPrefab> Grabbed;
    public event Action<ProductPrefab> Released;

    Rigidbody body;
    Vector3 homePosition;
    Quaternion homeRotation;
    int restRenderOrder;
    float floorContactTime = -1f;

    public Rigidbody Body => body;
    /// <summary>Every enabled product, so day 2 code can find the held one without searching the scene each frame.</summary>
    public static readonly System.Collections.Generic.List<ProductPrefab> Enabled = new System.Collections.Generic.List<ProductPrefab>();

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        Grab = GetComponent<XRGrabInteractable>();
        homePosition = transform.position;
        homeRotation = transform.rotation;
        restRenderOrder = Splat ? Splat.m_RenderOrder : 0;
        // The rig's capsule would otherwise block a product brought close to the face.
        foreach (var capsule in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            Physics.IgnoreCollision(Proxy, capsule);
    }

    void OnEnable()
    {
        Grab.selectEntered.AddListener(OnGrabbed);
        Grab.selectExited.AddListener(OnReleased);
        Enabled.Add(this);
    }

    void OnDisable()
    {
        Grab.selectEntered.RemoveListener(OnGrabbed);
        Grab.selectExited.RemoveListener(OnReleased);
        Enabled.Remove(this);
    }

    public bool TryMarkScanned(float time, float cooldownSec)
    {
        if (time - LastScannedTime < cooldownSec) return false;
        LastScannedTime = time;
        return true;
    }

    public void SetSplatRenderOrder(int order)
    {
        if (Splat) Splat.m_RenderOrder = order;
    }

    /// <summary>Stops being physical but keeps rendering inside the bag (doc 5.4).</summary>
    public void MarkBagged(Transform bag)
    {
        IsBagged = true;
        Grab.enabled = false;
        body.isKinematic = true;
        foreach (var collider in GetComponentsInChildren<Collider>())
            collider.enabled = false;
        transform.SetParent(bag, true);
    }

    /// <summary>Stands still here until picked up. Where it is now becomes where a drop sends it back.</summary>
    public void RestHere()
    {
        RestsKinematic = true;
        StopMoving();
        body.isKinematic = true;
        homePosition = transform.position;
        homeRotation = transform.rotation;
    }

    /// <summary>Doc 5.7.1 B6: snapped into the shelf and fixed there. Grab is off, so it stays put.</summary>
    public void MarkShelved(Transform slot)
    {
        IsShelved = true;
        ReturnUnit = null;
        StopMoving();
        body.isKinematic = true;
        Grab.enabled = false;
        transform.SetParent(slot, true);
    }

    /// <summary>Back where it came from now (misplacement, false disposal): the box place, or the shelf or basket spot.</summary>
    public void ReturnNow()
    {
        if (gameObject.activeInHierarchy) StartCoroutine(ReturnAfterDetach());
        else Respawn();
    }

    // XRI finishes letting go (and may throw) after the release callbacks, so a product held still has to wait a frame.
    IEnumerator ReturnAfterDetach()
    {
        yield return null;
        if (!Grab.isSelected && !IsShelved) Respawn();
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        floorContactTime = -1f;
        SetSplatRenderOrder(HeldRenderOrder);
        if (args.interactorObject is XRBaseInputInteractor hand) hand.SendHapticImpulse(0.3f, 0.05f);
        // Out of the box for good: letting go must not put it back under the box (XRI re-parents by default).
        if (ReturnUnit != null) Grab.retainTransformParent = false;
        Grabbed?.Invoke(this);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        SetSplatRenderOrder(restRenderOrder);
        // XRI restored the kinematic flag it found at the grab; shelf stock and box units fall like anything else once let go.
        if (RestsKinematic && !IsShelved && !IsBagged) body.isKinematic = false;
        Released?.Invoke(this);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!Grab.isSelected && collision.collider.GetComponent<ProductRespawnSurface>())
            floorContactTime = Time.time;
    }

    void FixedUpdate()
    {
        if (Grab.isSelected || IsBagged || IsShelved || body.isKinematic) return;
        bool droppedLongEnough = floorContactTime >= 0f && Time.time - floorContactTime >= RespawnDelaySeconds;
        if (droppedLongEnough || transform.position.y < FellOutOfWorldY) Respawn();
    }

    void Respawn()
    {
        floorContactTime = -1f;
        StopMoving();
        if (ReturnUnit != null && ReturnUnit.TakeBack(this))
        {
            RespawnCount++;
            return;
        }
        TeleportTo(homePosition, homeRotation);
        if (RestsKinematic) body.isKinematic = true;
        RespawnCount++;
    }

    /// <summary>
    /// Moves the body as well as the transform. The products interpolate, and a transform set in a frame with no physics
    /// step can be overwritten by the interpolated pose before physics reads it: once a falsely disposed product stayed in
    /// the bin (Day 2 check, 2026-11-08).
    /// </summary>
    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        body.position = position;
        body.rotation = rotation;
        transform.SetPositionAndRotation(position, rotation);
    }

    void StopMoving()
    {
        if (body.isKinematic) return;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }
}
