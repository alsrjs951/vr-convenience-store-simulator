using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The waist-high box stand in front of a shelf (doc 5.7.1 B2 BoxStandSocket). A box let go over it snaps onto it in place
/// and facing the aisle. Any box may dock on any stand; the target slot is only highlighted (doc 5.7.1 B2).
/// The trigger is large enough to take a box by its hand holes, which are the only colliders XRI knows the box by.
/// </summary>
public class BoxStandSocket : XRSocketInteractor
{
    /// <summary>The shelf this stand serves, like "A".</summary>
    public string Shelf;

    public StockBox Docked => hasSelection ? firstInteractableSelected?.transform.GetComponent<StockBox>() : null;

    public override bool CanHover(IXRHoverInteractable interactable) => IsBox(interactable) && base.CanHover(interactable);

    public override bool CanSelect(IXRSelectInteractable interactable) => IsBox(interactable) && base.CanSelect(interactable);

    static bool IsBox(IXRInteractable interactable)
    {
        var box = interactable.transform.GetComponent<StockBox>();
        return box != null && box.Stage != BoxStage.Recycled;
    }
}
