using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// The merged splats of one shelf slot (doc 7.3 steps 2 to 4). One renderer stands for every product place in the slot, a
/// box cutout per place hides the places that are empty or whose product is in a hand, and a lighter copy takes over when
/// the player is far away. Everything game code cares about stays on the ordinary products: this only decides what is drawn.
/// </summary>
public class SplatShelfSlot : MonoBehaviour
{
    /// <summary>
    /// Doc 7.3 step 4: two distances, so standing near one of them does not flip the copies back and forth. Both are inside
    /// the 2.9 m or more from the till to row B, so serving at the counter never keeps a shelf on its near copy.
    /// </summary>
    public const float NearEnterMeters = 2.4f;
    public const float FarLeaveMeters = 2.8f;
    // Store stock nobody takes from (2026-11-09) only comes up close to someone walking right past it, so the gaussians
    // stay available for the shelves being worked on.
    const float DisplayNearEnterMeters = 1.6f;
    const float DisplayFarLeaveMeters = 2.2f;
    // The working shelves get the gaussian budget first (doc 7.2): while any of them is close, display stock stays on its far
    // copy. Otherwise row D's near copies come up behind someone stocking row A + B from the aisle between them.
    static readonly List<SplatShelfSlot> workingSlots = new List<SplatShelfSlot>();
    // Display stock stands on one face of a gondola 0.5 m deep with a full-height spine down the middle. From behind the
    // spine it cannot be seen, so it is not drawn: on Day 2 that is row D while stocking row A + B (doc 7.2 budget).
    const float SpineBehindFace = 0.25f, BehindSpineMargin = 0.05f;
    // What a place holds: the product's box plus 9 mm at the sides, which is short of the next place (2 cm away), and more
    // above, where crumpled bag tops stick out. P4ShelfSplats drops whatever lies outside, so hiding this box leaves no crumbs.
    const float SideMargin = 0.009f, TopMargin = 0.06f, BottomMargin = 0.02f;
    const float CutoutTolerance = 0.001f;

    // Waking a renderer uploads its data, so at most one slot changes copy in a frame.
    static int lastSwitchFrame = -1;

    public GaussianSplatRenderer Near { get; private set; }
    public GaussianSplatRenderer Far { get; private set; }
    public bool ShowingNear { get; private set; }
    public int LodSwitches { get; private set; }
    public int ShownUnits { get; private set; }
    public GaussianSplatRenderer Active => ShowingNear ? Near : Far;

    GaussianCutout[] cutouts = new GaussianCutout[0];
    Vector3 center;
    float nearEnter = NearEnterMeters, farLeave = FarLeaveMeters;
    bool displayOnly, behindSpine;
    Vector3 shelfFace, intoShelf;
    /// <summary>Hidden behind its own shelf: neither copy is drawn.</summary>
    public bool BehindSpine => behindSpine;

    /// <summary>The volume one product may occupy in a merged splat, in the product root's frame.</summary>
    public static Bounds PlaceVolume(BoxCollider proxy)
    {
        var center = proxy.center + Vector3.up * ((TopMargin - BottomMargin) * 0.5f);
        var size = proxy.size + new Vector3(SideMargin * 2f, TopMargin + BottomMargin, SideMargin * 2f);
        return new Bounds(center, size);
    }

    /// <summary>Every place starts hidden. The assets hold shifted copies of the product in its own frame, see P4ShelfSplats.</summary>
    public static SplatShelfSlot Create(ShelfSlotZone slot, ProductPrefab product, GaussianSplatAsset nearAsset, GaussianSplatAsset farAsset)
    {
        // Under an inactive object the renderers wake up once, with their final assets and cutouts.
        var go = new GameObject("Merged Splats");
        go.SetActive(false);
        go.transform.SetParent(slot.transform, false);
        var view = go.AddComponent<SplatShelfSlot>();

        var source = product.Splat.transform;
        var position = slot.RootPosition(product, 0) + slot.UnitRotation * source.localPosition;
        var rotation = slot.UnitRotation * source.localRotation;
        view.cutouts = new GaussianCutout[slot.Capacity];
        var sum = Vector3.zero;
        for (int unit = 0; unit < slot.Capacity; unit++)
        {
            var box = new GameObject($"Cutout {unit + 1}");
            box.transform.SetParent(go.transform, false);
            var volume = PlaceVolume(product.Proxy);
            box.transform.SetPositionAndRotation(slot.RootPosition(product, unit) + slot.UnitRotation * volume.center, slot.UnitRotation);
            box.transform.localScale = volume.extents + Vector3.one * CutoutTolerance;
            var cutout = box.AddComponent<GaussianCutout>();
            cutout.m_Type = GaussianCutout.Type.Box;
            cutout.m_Invert = true;
            view.cutouts[unit] = cutout;
            sum += slot.UnitPosition(unit);
        }
        view.center = sum / slot.Capacity;
        if (slot.DisplayOnly)
        {
            view.displayOnly = true;
            view.shelfFace = slot.transform.position;
            view.intoShelf = slot.transform.forward;
            view.nearEnter = DisplayNearEnterMeters;
            view.farLeave = DisplayFarLeaveMeters;
        }

        view.Near = view.Copy(product.Splat, nearAsset, "Near", go.transform, position, rotation, source.localScale);
        view.Far = view.Copy(product.Splat, farAsset, "Far", go.transform, position, rotation, source.localScale);
        var camera = Camera.main;
        view.ShowingNear = camera != null && Vector3.Distance(camera.transform.position, view.center) < view.nearEnter;
        view.Near.gameObject.SetActive(view.ShowingNear);
        view.Far.gameObject.SetActive(!view.ShowingNear);
        go.SetActive(true);
        return view;
    }

    GaussianSplatRenderer Copy(GaussianSplatRenderer template, GaussianSplatAsset asset, string copyName, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var splat = Instantiate(template, parent);
        splat.name = copyName;
        splat.transform.SetPositionAndRotation(position, rotation);
        splat.transform.localScale = scale;
        splat.m_Asset = asset;
        splat.m_Cutouts = cutouts;
        splat.m_RenderOrder = 0;
        splat.enabled = true;
        return splat;
    }

    public void SetUnitShown(int unit, bool shown)
    {
        if (cutouts[unit].enabled != shown) return;
        cutouts[unit].enabled = !shown;
        ShownUnits += shown ? 1 : -1;
    }

    public bool IsUnitShown(int unit) => !cutouts[unit].enabled;

    public void HideAll()
    {
        foreach (var cutout in cutouts) cutout.enabled = true;
        ShownUnits = 0;
    }

    void OnEnable()
    {
        if (!displayOnly) workingSlots.Add(this);
    }

    void OnDisable() => workingSlots.Remove(this);

    void Update()
    {
        var camera = Camera.main;
        if (camera == null || Time.frameCount == lastSwitchFrame) return;
        if (displayOnly)
        {
            bool behind = Vector3.Dot(camera.transform.position - shelfFace, intoShelf) > SpineBehindFace + BehindSpineMargin;
            if (behind != behindSpine)
            {
                lastSwitchFrame = Time.frameCount;
                behindSpine = behind;
                Active.gameObject.SetActive(!behind);
                return;
            }
            if (behindSpine) return;
        }
        float distance = Vector3.Distance(camera.transform.position, center);
        bool near = ShowingNear ? distance <= farLeave : distance < nearEnter;
        if (displayOnly && workingSlots.Exists(slot => slot.ShowingNear)) near = false;
        if (near == ShowingNear) return;

        lastSwitchFrame = Time.frameCount;
        ShowingNear = !ShowingNear;
        LodSwitches++;
        Near.gameObject.SetActive(ShowingNear);
        Far.gameObject.SetActive(!ShowingNear);
    }
}
