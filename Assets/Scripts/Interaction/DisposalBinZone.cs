using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The waste bin (doc 6.4 dispose, DisposalBinZone). An expired product let go in here is disposed of. A good product is a
/// false disposal: an error sound, a penalty, and it goes back where it came from. Only on intake days (2 and 3).
/// </summary>
[RequireComponent(typeof(Collider))]
public class DisposalBinZone : MonoBehaviour
{
    public IntakeDirector Intake;

    readonly HashSet<ProductPrefab> handled = new HashSet<ProductPrefab>();

    void OnTriggerStay(Collider other)
    {
        if (Intake == null || !Intake.Active) return;
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product == null || other != product.Proxy || product.Grab.isSelected || product.IsBagged || product.IsShelved) return;
        if (!handled.Add(product)) return;
        if (product.IsExpired) Intake.Dispose(product, transform.position);
        else Intake.FalseDisposal(product, transform.position);
    }

    void OnTriggerExit(Collider other)
    {
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product != null) handled.Remove(product);
    }
}
