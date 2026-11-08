using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A scanned product let go inside the bag and slowed down is bagged: it stops being physical
/// but keeps rendering (doc 5.4).
/// </summary>
[RequireComponent(typeof(Collider))]
public class BagZone : MonoBehaviour
{
    const float MaxSpeed = 0.5f;
    const float SettleSeconds = 0.3f;

    public POSController Pos;
    public GameAudio Audio;
    /// <summary>When set, this decides what may be bagged. The tutorial uses it, because no order is open.</summary>
    public Func<ProductPrefab, bool> AcceptOverride;

    public event Action<ProductPrefab> Bagged;

    readonly Dictionary<ProductPrefab, float> settling = new Dictionary<ProductPrefab, float>();
    readonly HashSet<ProductPrefab> warned = new HashSet<ProductPrefab>();

    void OnTriggerStay(Collider other)
    {
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
}
