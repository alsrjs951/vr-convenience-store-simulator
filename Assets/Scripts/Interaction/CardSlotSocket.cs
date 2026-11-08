using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Pulls the customer's card into the terminal slot. A card brought within a few centimetres of the opening snaps to the
/// inserted pose even while it is still held, so the hand need not line up a 2 cm opening exactly. Let go near the opening
/// and it stays in the slot. Only the card is taken.
///
/// While the card is at the opening it does not collide with the terminal's body. In the editor a held card sagged a
/// centimetre, caught the slot's lip tilted 13 to 23 degrees and wedged there, where the snap could not straighten it.
/// Away from the opening the body is solid again, so a card dropped on the terminal still rests on it.
/// </summary>
public class CardSlotSocket : XRSocketInteractor
{
    public Collider[] TerminalBody;

    public override bool CanHover(IXRHoverInteractable interactable) => IsCard(interactable) && base.CanHover(interactable);

    public override bool CanSelect(IXRSelectInteractable interactable) => IsCard(interactable) && base.CanSelect(interactable);

    protected override void OnHoverEntered(HoverEnterEventArgs args)
    {
        PassThroughBody(args.interactableObject, true);
        base.OnHoverEntered(args);
    }

    protected override void OnHoverExited(HoverExitEventArgs args)
    {
        base.OnHoverExited(args);
        PassThroughBody(args.interactableObject, false);
    }

    void PassThroughBody(IXRInteractable interactable, bool pass)
    {
        if (TerminalBody == null || !(interactable is XRBaseInteractable card) || card == null) return;
        foreach (var own in card.colliders)
            foreach (var part in TerminalBody)
                if (own != null && part != null) Physics.IgnoreCollision(own, part, pass);
    }

    static bool IsCard(IXRInteractable interactable)
    {
        var prop = interactable.transform.GetComponent<PaymentProp>();
        return prop != null && prop.IsCard;
    }
}
