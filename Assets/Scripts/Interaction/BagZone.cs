using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The counter's open plastic bag. A scanned product let go in it and come to rest is bagged: it stops being physical
/// and is stood inside with the ones already there (doc 5.4).
/// </summary>
[RequireComponent(typeof(Collider))]
public class BagZone : MonoBehaviour
{
    const float MaxSpeed = 0.5f;
    const float SettleSeconds = 0.3f;
    const float TuckSeconds = 0.22f;
    // How things stand in a bag: leaning back a little, not square to it, a finger apart, not all in the middle.
    const float LeanDegrees = 5f, TurnDegrees = 6f, Gap = 0.006f, Shift = 0.03f;

    public POSController Pos;
    public GameAudio Audio;
    /// <summary>The middle of the bag's floor: its x runs along the bag, its z across it, away from the clerk.</summary>
    public Transform Floor;
    /// <summary>The room inside the bag, along it and across it.</summary>
    public Vector2 Inside = new Vector2(0.26f, 0.13f);
    /// <summary>When set, this decides what may be bagged. The tutorial uses it, because no order is open.</summary>
    public Func<ProductPrefab, bool> AcceptOverride;

    public event Action<ProductPrefab> Bagged;
    /// <summary>Everything bagged has reached its place in the bag.</summary>
    public bool Settled
    {
        get
        {
            foreach (var item in inside)
                if (item.Product != null && item.Moved < 1f) return false;
            return true;
        }
    }

    class Item
    {
        public ProductPrefab Product;
        /// <summary>Its size as it stands in the bag, and the turn that puts its wide side along the bag.</summary>
        public float Width, Depth, Yaw;
        public Vector3 From, To;
        public Quaternion FromRotation, ToRotation;
        public float Moved = 1f;
    }

    readonly Dictionary<ProductPrefab, float> settling = new Dictionary<ProductPrefab, float>();
    readonly HashSet<ProductPrefab> warned = new HashSet<ProductPrefab>();
    readonly List<Item> inside = new List<Item>();

    void OnTriggerStay(Collider other)
    {
        var money = other.GetComponentInParent<PaymentProp>();
        if (money != null)
        {
            // Under the film it would be out of sight, which is as good as on the floor.
            if (!money.Grab.isSelected && !money.Docked && !money.Leaving && money.Speed <= MaxSpeed) money.PutBack();
            return;
        }

        var product = other.GetComponentInParent<ProductPrefab>();
        if (product == null || other != product.Proxy || product.IsBagged || product.Grab.isSelected) return;

        bool accepted = AcceptOverride != null ? AcceptOverride(product) : Pos.IsScanned(product);
        if (!accepted)
        {
            if (warned.Add(product))
            {
                if (Audio) Audio.PlayError();
                Pos.SetNotice("스캔하지 않은 상품은 봉투에 담을 수 없습니다.");
            }
            return;
        }
        // A bagged product can no longer be picked up, so one still to be heated must stay out of the bag.
        if (AcceptOverride == null && Pos.NeedsCooking(product))
        {
            if (warned.Add(product))
            {
                if (Audio) Audio.PlayError();
                Pos.SetNotice(Pos.CookFirstNotice);
            }
            return;
        }
        if (product.GetComponent<Rigidbody>().linearVelocity.magnitude > MaxSpeed)
        {
            settling.Remove(product);
            return;
        }
        settling.TryGetValue(product, out float elapsed);
        elapsed += Time.fixedDeltaTime;
        settling[product] = elapsed;
        if (elapsed < SettleSeconds) return;

        settling.Remove(product);
        product.MarkBagged(transform);
        StandInside(product);
        if (AcceptOverride == null) Pos.MarkBagged(product);
        Bagged?.Invoke(product);
    }

    void OnTriggerExit(Collider other)
    {
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product == null) return;
        settling.Remove(product);
        warned.Remove(product);
    }

    // ---------- Inside the bag ----------

    void StandInside(ProductPrefab product)
    {
        var size = Vector3.Scale(product.Proxy.size, product.transform.lossyScale);
        // Its wide side along the bag, facing whichever way is nearer to how it was let go.
        bool sideways = size.z > size.x;
        float yaw = sideways ? 90f : 0f;
        var facing = Floor.rotation * Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        if (Vector3.Dot(product.transform.forward, facing) < 0f) yaw += 180f;
        inside.Add(new Item { Product = product, Width = sideways ? size.z : size.x, Depth = sideways ? size.x : size.z, Yaw = yaw });
        Arrange();
        if (Audio) Audio.PlayAt(StoreSound.Bag, Floor.position);
    }

    /// <summary>
    /// Everything in the bag stands in a row across it, the first in on the customer's side. What is already there makes
    /// room for what comes in, as it does in a real bag.
    /// </summary>
    void Arrange()
    {
        inside.RemoveAll(item => item.Product == null);
        float total = 0f;
        foreach (var item in inside) total += item.Depth;
        // A finger apart while there is room; closer, and then into each other, once there is not.
        float gap = inside.Count > 1 ? Mathf.Min(Gap, (Inside.y - total) / (inside.Count - 1)) : 0f;
        float z = (total + gap * (inside.Count - 1)) * 0.5f;
        for (int i = 0; i < inside.Count; i++)
        {
            var item = inside[i];
            float side = i % 2 == 0 ? -1f : 1f;
            float x = inside.Count > 1 ? side * Mathf.Min(Shift, Mathf.Max(0f, (Inside.x - item.Width) * 0.5f)) : 0f;
            z -= item.Depth * 0.5f;
            var rotation = Floor.rotation * Quaternion.Euler(LeanDegrees, 0f, 0f) * Quaternion.Euler(0f, item.Yaw + side * TurnDegrees, 0f);
            // The middle of its underside goes on the bag's floor.
            var proxy = item.Product.Proxy;
            var underside = Vector3.Scale(proxy.center - Vector3.up * (proxy.size.y * 0.5f), item.Product.transform.lossyScale);
            var position = Floor.TransformPoint(new Vector3(x, 0f, z)) - rotation * underside;
            z -= item.Depth * 0.5f + gap;

            if (item.Moved >= 1f && (position - item.To).sqrMagnitude < 1e-6f) continue;
            item.From = item.Product.transform.position;
            item.FromRotation = item.Product.transform.rotation;
            item.To = position;
            item.ToRotation = rotation;
            item.Moved = 0f;
        }
    }

    void Update()
    {
        foreach (var item in inside)
        {
            if (item.Product == null || item.Moved >= 1f) continue;
            item.Moved = Mathf.Min(1f, item.Moved + Time.deltaTime / TuckSeconds);
            float eased = Mathf.SmoothStep(0f, 1f, item.Moved);
            item.Product.TeleportTo(Vector3.Lerp(item.From, item.To, eased), Quaternion.Slerp(item.FromRotation, item.ToRotation, eased));
        }
    }
}
