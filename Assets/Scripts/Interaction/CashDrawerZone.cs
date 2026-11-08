using System;
using UnityEngine;

/// <summary>Takes the customer's bill when it is let go inside the drawer (doc 5.5).</summary>
[RequireComponent(typeof(Collider))]
public class CashDrawerZone : MonoBehaviour
{
    public event Action<PaymentProp> BillReceived;

    /// <summary>Only the customer's bill counts. The drawer's own change stock also sits nearby.</summary>
    public PaymentProp Expected;

    void OnTriggerStay(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        if (prop == null || prop != Expected || prop.Grab.isSelected) return;
        Expected = null;
        BillReceived?.Invoke(prop);
    }
}
