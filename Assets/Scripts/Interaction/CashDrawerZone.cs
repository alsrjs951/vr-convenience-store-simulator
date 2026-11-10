using System;
using UnityEngine;

/// <summary>
/// The space of the till's open drawer (doc 5.5). The customer's money let go in it is taken; change let go in it goes back
/// where it came from, so a piece picked up by mistake can be put back.
/// </summary>
[RequireComponent(typeof(Collider))]
public class CashDrawerZone : MonoBehaviour
{
    public event Action<PaymentProp> BillReceived;
    public event Action<PaymentProp> ChangeReturned;

    /// <summary>The customer's money that is still to come in.</summary>
    public PaymentProp Expected;

    void OnTriggerStay(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        // The pieces lying in their slots are in here too, and so is one on its way in.
        if (prop == null || prop.Grab.isSelected || prop.Docked || prop.Leaving) return;
        if (prop == Expected)
        {
            Expected = null;
            BillReceived?.Invoke(prop);
        }
        else if (prop.Kind == PaymentKind.Change) ChangeReturned?.Invoke(prop);
    }
}
