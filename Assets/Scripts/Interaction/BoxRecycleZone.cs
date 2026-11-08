using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The empty-box bin at the warehouse door (doc 5.7.1 B7 BoxRecycleZone). An empty box let go in here is recycled.
/// A box that still has products goes back where it came from, with a notice.
/// </summary>
[RequireComponent(typeof(Collider))]
public class BoxRecycleZone : MonoBehaviour
{
    public IntakeDirector Intake;

    readonly HashSet<StockBox> warned = new HashSet<StockBox>();

    void OnTriggerStay(Collider other)
    {
        var box = other.attachedRigidbody != null ? other.attachedRigidbody.GetComponent<StockBox>() : null;
        if (box == null || box.Grab.isSelected || box.Stage == BoxStage.Recycled || Intake == null) return;
        if (box.Stage == BoxStage.Empty)
        {
            Intake.Recycle(box, transform.position);
            return;
        }
        if (warned.Add(box))
        {
            if (Intake.Audio) Intake.Audio.PlayError();
            box.ShowNotice("빈 박스만 버릴 수 있어요");
            box.ReturnHome();
        }
    }

    void OnTriggerExit(Collider other)
    {
        var box = other.attachedRigidbody != null ? other.attachedRigidbody.GetComponent<StockBox>() : null;
        if (box != null) warned.Remove(box);
    }
}
