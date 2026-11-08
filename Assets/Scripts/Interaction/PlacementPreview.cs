using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Doc 5.7.5 placement preview. While a product is held, the head's forward ray (Quest 2 has no eye tracking) picks a
/// point on the intake target slots; if it hits none, the held product's position is the point. Candidates are target
/// slots within 0.6 m of the product. The slot that contains the point wins, else the nearest within 15 cm. A slot
/// that takes this product shows a faint copy of it (opacity 0.08, render order 5) on the next empty place of the column
/// nearest the point, with a green outline on the board; a slot that does not (other product, full) shows only a red
/// outline. A change of place needs to be 5 cm nearer and hold for 0.2 s. Letting go on green accepts the product,
/// on red counts a misplacement and sends it back, on nothing lets it fall as usual.
/// </summary>
public class PlacementPreview : MonoBehaviour
{
    public const float CandidateRadius = 0.6f;
    const float NearSlot = 0.15f;
    const float SwitchCloser = 0.05f;
    const float SwitchHold = 0.2f;
    // Doc 5.7.5 says about 0.3. At 0.3 a product still drew almost solid, because its splats overlap many layers deep; side by
    // side at 1.0 / 0.3 / 0.15 / 0.08 / 0.04, 0.08 was the first that read as faint while staying recognisable (2026-11-08,
    // desktop shots in docs/p2/evidence/day2/ghost_opacity/). The final look is for the Quest 2 check.
    public const float GhostOpacity = 0.08f;
    public const int GhostRenderOrder = 5;

    public IntakeDirector Intake;

    public ProductPrefab Held { get; private set; }
    public ShelfSlotZone Slot { get; private set; }
    public int Unit { get; private set; } = -1;
    public bool GhostVisible { get; private set; }
    public bool RejectVisible { get; private set; }
    /// <summary>Whether the last choice came from the head ray (true) or the held product's position (false).</summary>
    public bool UsedHeadRay { get; private set; }
    public GaussianSplatRenderer Ghost { get; private set; }
    public int GhostsShown { get; private set; }
    public int Switches { get; private set; }

    readonly Dictionary<ProductData, GameObject> ghosts = new Dictionary<ProductData, GameObject>();
    readonly HashSet<ProductPrefab> watched = new HashSet<ProductPrefab>();
    ShelfSlotZone pendingSlot;
    int pendingUnit = -1;
    float pendingSince;
    int zoneMask;
    Camera view;

    void Awake() => zoneMask = 1 << LayerMask.NameToLayer("Zone");

    void LateUpdate()
    {
        if (Intake == null || !Intake.Active)
        {
            if (Held != null) Hide();
            return;
        }
        if (view == null) view = Camera.main;
        var held = FindHeld();
        if (held != Held)
        {
            Hide();
            Held = held;
            if (held != null && watched.Add(held)) held.Released += OnReleased;
        }
        if (Held == null || view == null) return;
        Choose(Held);
    }

    ProductPrefab FindHeld()
    {
        foreach (var product in Intake.HeldProducts())
            return product;
        return null;
    }

    void Choose(ProductPrefab held)
    {
        var center = held.transform.TransformPoint(held.Proxy.center);
        var candidates = new List<ShelfSlotZone>();
        foreach (var slot in Intake.Slots)
            if (slot != null && slot.IsTarget && Vector3.Distance(slot.Zone.ClosestPoint(center), center) <= CandidateRadius)
                candidates.Add(slot);
        if (candidates.Count == 0)
        {
            Show(null, -1);
            return;
        }

        // Head ray first, the held product's position otherwise.
        Vector3 gaze = center;
        UsedHeadRay = false;
        var ray = new Ray(view.transform.position, view.transform.forward);
        float nearest = float.PositiveInfinity;
        foreach (var hit in Physics.RaycastAll(ray, 4f, zoneMask, QueryTriggerInteraction.Collide))
        {
            var slot = hit.collider.GetComponent<ShelfSlotZone>();
            if (slot == null || !candidates.Contains(slot) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            gaze = hit.point;
            UsedHeadRay = true;
        }

        ShelfSlotZone chosen = null;
        float best = float.PositiveInfinity;
        foreach (var slot in candidates)
        {
            float d = Vector3.Distance(slot.Zone.ClosestPoint(gaze), gaze);
            if (d < best) { best = d; chosen = slot; }
        }
        if (chosen == null || best > NearSlot)
        {
            Show(null, -1);
            return;
        }

        int unit = -1;
        if (chosen.CanAccept(held.Data.Id))
        {
            float closest = float.PositiveInfinity;
            for (int column = 0; column < chosen.Columns; column++)
            {
                int candidate = chosen.NextFreeInColumn(column);
                if (candidate < 0) continue;
                float d = Vector3.Distance(chosen.UnitPosition(candidate), gaze);
                if (d < closest) { closest = d; unit = candidate; }
            }
        }

        // Steady: an empty or invalid current choice changes at once, a valid one only for a clearly nearer place that holds.
        bool currentValid = Slot != null && candidates.Contains(Slot) && (Unit < 0 ? !Slot.CanAccept(held.Data.Id) : !Slot.IsFilled(Unit) && Slot.CanAccept(held.Data.Id));
        if (!currentValid || (chosen == Slot && unit == Unit))
        {
            pendingSlot = null;
            if (chosen != Slot || unit != Unit) Show(chosen, unit);
            return;
        }
        float currentDistance = Unit >= 0 ? Vector3.Distance(Slot.UnitPosition(Unit), gaze) : Vector3.Distance(Slot.Zone.ClosestPoint(gaze), gaze);
        float newDistance = unit >= 0 ? Vector3.Distance(chosen.UnitPosition(unit), gaze) : best;
        if (newDistance > currentDistance - SwitchCloser)
        {
            pendingSlot = null;
            return;
        }
        if (pendingSlot != chosen || pendingUnit != unit)
        {
            pendingSlot = chosen;
            pendingUnit = unit;
            pendingSince = Time.time;
            return;
        }
        if (Time.time - pendingSince >= SwitchHold)
        {
            pendingSlot = null;
            Switches++;
            Show(chosen, unit);
        }
    }

    void Show(ShelfSlotZone slot, int unit)
    {
        if (Slot != null && Slot != slot)
        {
            Slot.ShowReject(false);
            Slot.ShowUnit(-1);
        }
        bool changed = slot != Slot || unit != Unit;
        Slot = slot;
        Unit = unit;
        GhostVisible = slot != null && unit >= 0;
        RejectVisible = slot != null && unit < 0;
        if (slot != null)
        {
            slot.ShowReject(RejectVisible);
            slot.ShowUnit(unit);
        }
        if (GhostVisible)
        {
            var ghost = GhostFor(Held);
            ghost.SetActive(true);
            ghost.transform.SetPositionAndRotation(slot.UnitPosition(unit) - slot.UnitRotation * Held.Proxy.center, slot.UnitRotation);
            Ghost = ghost.GetComponentInChildren<GaussianSplatRenderer>();
            foreach (var pair in ghosts)
                if (pair.Value != ghost) pair.Value.SetActive(false);
            if (changed)
            {
                GhostsShown++;
                Buzz(0.1f, 0.02f);
            }
        }
        else
        {
            HideGhosts();
        }
    }

    void Hide()
    {
        pendingSlot = null;
        if (Slot != null)
        {
            Slot.ShowReject(false);
            Slot.ShowUnit(-1);
        }
        Slot = null;
        Unit = -1;
        GhostVisible = RejectVisible = false;
        HideGhosts();
        Held = null;
    }

    void HideGhosts()
    {
        foreach (var ghost in ghosts.Values) ghost.SetActive(false);
        Ghost = null;
    }

    void OnReleased(ProductPrefab product)
    {
        if (product != Held) return;
        var slot = Slot;
        int unit = Unit;
        bool green = GhostVisible, red = RejectVisible;
        Hide();
        if (green) Intake.Shelve(product, slot, unit);
        else if (red) Intake.Misplace(product, slot);
    }

    void Buzz(float amplitude, float seconds)
    {
        if (Held == null) return;
        foreach (var interactor in Held.Grab.interactorsSelecting)
            if (interactor is XRBaseInputInteractor hand) hand.SendHapticImpulse(amplitude, seconds);
    }

    /// <summary>
    /// A visual-only copy of the product: made under an inactive parent so no physics or XRI component ever wakes up,
    /// stripped, then faded and drawn between the shelf and the hand.
    /// </summary>
    GameObject GhostFor(ProductPrefab product)
    {
        if (ghosts.TryGetValue(product.Data, out var existing) && existing != null) return existing;
        var copy = VisualCopy(product, GhostOpacity, GhostRenderOrder, transform, product.Data.LightSplat);
        copy.name = $"Ghost {product.Data.Id}";
        copy.SetActive(false);
        ghosts[product.Data] = copy;
        return copy;
    }

    /// <summary>A copy of a product that only draws: made under an inactive parent so no physics or XRI component wakes up.</summary>
    public static GameObject VisualCopy(ProductPrefab product, float opacity, int renderOrder, Transform parent, GaussianSplatAsset asset = null)
    {
        var holder = new GameObject("Visual Copy Holder (inactive)");
        holder.SetActive(false);
        var copy = Instantiate(product.gameObject, holder.transform);
        foreach (var p in copy.GetComponentsInChildren<ProductPrefab>(true)) DestroyImmediate(p);
        foreach (var g in copy.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>(true)) DestroyImmediate(g);
        foreach (var b in copy.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(b);
        foreach (var c in copy.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
        foreach (var canvas in copy.GetComponentsInChildren<Canvas>(true)) DestroyImmediate(canvas.gameObject);
        foreach (var splat in copy.GetComponentsInChildren<GaussianSplatRenderer>(true))
        {
            splat.m_OpacityScale = opacity;
            splat.m_RenderOrder = renderOrder;
            if (asset != null) splat.m_Asset = asset;
        }
        copy.transform.SetParent(parent, false);
        Destroy(holder);
        return copy;
    }
}
