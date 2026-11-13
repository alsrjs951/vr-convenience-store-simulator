using System;
using System.Collections;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One shelf slot (doc 5.7.3 ShelfSlotZone): slotId like A-2, the product it holds, and Columns x Rows unit places on the
/// board. Units fill front to back in each column; which column is up to the placement preview (doc 5.7.5).
///
/// The transform sits at the front edge of the slot on the board's top, local +z into the shelf, local x along it. The
/// zone collider (trigger, layer Zone) covers the slot's volume and is what the head ray hits. Before merged shelf
/// splats exist (doc 7.3 step 1) this is Individual mode: an accepted product stays as a fixed, kinematic product.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class ShelfSlotZone : MonoBehaviour
{
    const float SnapSeconds = 0.15f;
    const float LineWidth = 0.008f;
    const float MetersPerPixel = 0.0006f;

    public string SlotId;
    public string ProductId;
    public int Columns = 2;
    public int Rows = 1;
    public float CellWidth = 0.22f;
    public float CellDepth = 0.08f;
    public float ProductHeight = 0.25f;
    public Material Yellow, Green, Red;
    public GameAudio Audio;
    /// <summary>Doc 7.3: one merged splat for all places. Without them, or with -individual-shelves, every product draws itself.</summary>
    public GaussianSplatAsset MergedNear, MergedFar;
    /// <summary>
    /// Store stock that is only seen (2026-11-09): no deliveries, no preview, no zone, never cleared. See IntakeDirector.StockDecor.
    /// In a store that is kept it is a target for one day, the day its product comes in a box (IntakeDirector.BeginDay).
    /// </summary>
    public bool DisplayOnly;
    /// <summary>For display stock only seen from afar: the lighter copy is all there is.</summary>
    public bool FarOnly;
    public SplatShelfSlot Merged { get; private set; }
    public static readonly bool IndividualOnly = Array.IndexOf(Environment.GetCommandLineArgs(), "-individual-shelves") >= 0;

    public int Capacity => Columns * Rows;
    public int Filled { get; private set; }
    /// <summary>An intake target today: highlighted, counted, and a candidate for the preview.</summary>
    public bool IsTarget { get; private set; }
    public BoxCollider Zone { get; private set; }
    public ProductPrefab[] Occupants { get; private set; }
    public bool RejectShown => rejectFrame != null && rejectFrame.activeSelf;
    public bool HighlightShown => highlightFrame != null && highlightFrame.activeSelf;
    public int GreenUnit { get; private set; } = -1;
    public string CountText => countText != null ? countText.text : "";
    public event Action<ShelfSlotZone, ProductPrefab> Shelved;
    /// <summary>Day 1 display stock on this slot (see Display).</summary>
    public IReadOnlyList<GameObject> Displays => displays;

    readonly List<GameObject> displays = new List<GameObject>();

    bool[] filled;
    GameObject highlightFrame, rejectFrame, unitFrame;
    Text countText;

    void Awake()
    {
        Zone = GetComponent<BoxCollider>();
        Occupants = new ProductPrefab[Capacity];
        filled = new bool[Capacity];
        float width = Columns * CellWidth;
        float height = ProductHeight + 0.03f;
        highlightFrame = Frame("Target Highlight", new Vector3(0f, height * 0.5f, -0.004f), new Vector2(width + 0.02f, height), Yellow, true);
        rejectFrame = Frame("Reject Outline", new Vector3(0f, height * 0.5f, -0.008f), new Vector2(width + 0.04f, height + 0.02f), Red, true);
        unitFrame = Frame("Unit Outline", Vector3.zero, new Vector2(CellWidth - 0.01f, CellDepth - 0.005f), Green, false);
        highlightFrame.SetActive(false);
        rejectFrame.SetActive(false);
        unitFrame.SetActive(false);

        var label = WorldUi.CreatePanel(transform, "Slot Label", transform.TransformPoint(new Vector3(0f, -0.045f, -0.012f)), transform.rotation,
            new Vector2(300f, 60f), MetersPerPixel, WorldUi.Palette.Panel);
        countText = WorldUi.AddText(label, 0f, 0f, 300f, 60f, 40, TextAnchor.MiddleCenter);
        label.gameObject.SetActive(false);
    }

    public Vector3 UnitPosition(int unit)
    {
        int column = unit / Rows, row = unit % Rows;
        float x = (column - (Columns - 1) * 0.5f) * CellWidth;
        float z = (row + 0.5f) * CellDepth;
        return transform.TransformPoint(new Vector3(x, ProductHeight * 0.5f + 0.003f, z));
    }

    /// <summary>Front to the aisle (doc 5.7.5: placed facing out).</summary>
    public Quaternion UnitRotation => transform.rotation * Quaternion.Euler(0f, 180f, 0f);

    /// <summary>Where a product's root goes so its proxy box sits on the place.</summary>
    public Vector3 RootPosition(ProductPrefab product, int unit) => UnitPosition(unit) - UnitRotation * product.Proxy.center;

    /// <summary>
    /// The next empty place in a column: the deepest one that is free, so a column is filled from the back forward
    /// (2026-11-13; it was front first, and the thing in front then stood in the way of the place behind it). -1 when full.
    /// </summary>
    public int NextFreeInColumn(int column)
    {
        for (int row = Rows - 1; row >= 0; row--)
        {
            int unit = column * Rows + row;
            if (!filled[unit]) return unit;
        }
        return -1;
    }

    public bool IsFilled(int unit) => filled[unit];

    public bool CanAccept(string productId) => IsTarget && productId == ProductId && Filled < Capacity;

    /// <summary>Today's role. A target is highlighted yellow and shows its count.</summary>
    public void SetTarget(bool target)
    {
        IsTarget = target;
        // A display shelf has no zone on other days, so nothing held near it is offered a place there.
        if (DisplayOnly && Zone != null) Zone.enabled = target;
        highlightFrame.SetActive(target && Filled < Capacity);
        countText.transform.parent.gameObject.SetActive(target);
        RefreshCount();
    }

    /// <summary>Stocks a place at day start, standing still and grabbable (shelf stock, possibly expired).</summary>
    public ProductPrefab Stock(ProductPrefab prefab, int unit)
    {
        bool merged = EnsureMerged(prefab);
        var product = ProductPrefab.Spawn(prefab, RootPosition(prefab, unit), UnitRotation, p => { if (merged) p.DrawOnShelf(this, unit); });
        if (merged) Merged.SetUnitShown(unit, true);
        product.RestHere();
        Occupants[unit] = product;
        product.Grabbed += OnStockTaken;
        return product;
    }

    /// <summary>
    /// Day 1 has no intake, but a store with bare shelves looks closed: each place shows the product's splat and nothing
    /// else. Only the splat child is copied, so there is no body, collider, grab or ProductPrefab, and no game code or
    /// check that looks for products ever sees it (D12's "no product until the customer is at the counter" stays true).
    /// It sits where Stock would put the product, facing the aisle.
    /// </summary>
    public GaussianSplatRenderer Display(ProductPrefab prefab, int unit)
    {
        if (EnsureMerged(prefab))
        {
            Merged.SetUnitShown(unit, true);
            return Merged.Active;
        }
        var holder = new GameObject($"Display {SlotId} #{unit + 1}").transform;
        holder.SetPositionAndRotation(RootPosition(prefab, unit), UnitRotation);
        holder.SetParent(transform, true);
        var source = prefab.Splat.transform;
        var splat = Instantiate(prefab.Splat, holder);
        splat.transform.localPosition = source.localPosition;
        splat.transform.localRotation = source.localRotation;
        splat.transform.localScale = source.localScale;
        splat.gameObject.name = "Splat";
        displays.Add(holder.gameObject);
        return splat;
    }

    public void PrepareMerged(ProductPrefab product) => EnsureMerged(product);

    bool EnsureMerged(ProductPrefab product)
    {
        if (Merged != null) return true;
        if (IndividualOnly || MergedNear == null || MergedFar == null) return false;
        Merged = SplatShelfSlot.Create(this, product, MergedNear, MergedFar);
        return true;
    }

    /// <summary>A place of the merged splat, shown or hidden. Does nothing when every product draws itself.</summary>
    public void ShowOnShelf(int unit, bool shown)
    {
        if (Merged != null) Merged.SetUnitShown(unit, shown);
    }

    void OnStockTaken(ProductPrefab product)
    {
        product.Grabbed -= OnStockTaken;
        int unit = Array.IndexOf(Occupants, product);
        if (unit >= 0) Occupants[unit] = null;
    }

    /// <summary>Doc 5.7.1 B6: snaps into the place over 0.15 s, then stays there.</summary>
    public void Commit(ProductPrefab product, int unit)
    {
        filled[unit] = true;
        Occupants[unit] = product;
        Filled++;
        StartCoroutine(Snap(product, unit));
    }

    IEnumerator Snap(ProductPrefab product, int unit)
    {
        // XRI finishes the release (and its throw) after the release callbacks.
        yield return null;
        product.MarkShelved(transform);
        var fromPosition = product.transform.position;
        var fromRotation = product.transform.rotation;
        var toPosition = RootPosition(product, unit);
        var toRotation = UnitRotation;
        float t = 0f;
        while (t < SnapSeconds)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / SnapSeconds);
            product.transform.SetPositionAndRotation(Vector3.Lerp(fromPosition, toPosition, k), Quaternion.Slerp(fromRotation, toRotation, k));
            yield return null;
        }
        product.transform.SetPositionAndRotation(toPosition, toRotation);
        if (EnsureMerged(product))
        {
            product.DrawOnShelf(this, unit);
            Merged.SetUnitShown(unit, true);
        }
        if (Audio) Audio.PlayAt(StoreSound.Place, toPosition);
        if (Filled >= Capacity) highlightFrame.SetActive(false);
        RefreshCount();
        Shelved?.Invoke(this, product);
    }

    public void ShowReject(bool show) => rejectFrame.SetActive(show);

    /// <summary>The green outline on the board around one place, or none with -1.</summary>
    public void ShowUnit(int unit)
    {
        GreenUnit = unit;
        unitFrame.SetActive(unit >= 0);
        if (unit < 0) return;
        var position = UnitPosition(unit);
        unitFrame.transform.position = new Vector3(position.x, transform.position.y + 0.004f, position.z);
        unitFrame.transform.rotation = transform.rotation;
    }

    /// <summary>Removes everything this slot holds, for a new day.</summary>
    public void Clear()
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (Occupants[i] != null) Destroy(Occupants[i].gameObject);
            Occupants[i] = null;
            filled[i] = false;
        }
        foreach (var display in displays)
            if (display != null) Destroy(display);
        displays.Clear();
        if (Merged != null) Merged.HideAll();
        Filled = 0;
        ShowReject(false);
        ShowUnit(-1);
        SetTarget(false);
    }

    void RefreshCount()
    {
        if (countText != null) countText.text = $"{SlotId}  {Filled}/{Capacity}";
    }

    /// <summary>A rectangle of thin opaque bars: upright in the slot's front plane, or flat on the board. Opaque so it never blends with splats (doc 5.7.5).</summary>
    GameObject Frame(string name, Vector3 center, Vector2 size, Material material, bool upright)
    {
        var root = new GameObject(name).transform;
        root.SetParent(transform, false);
        root.localPosition = center;
        float w = size.x, h = size.y;
        if (upright)
        {
            Bar(root, new Vector3(0f, h * 0.5f, 0f), new Vector3(w, LineWidth, LineWidth), material);
            Bar(root, new Vector3(0f, -h * 0.5f, 0f), new Vector3(w, LineWidth, LineWidth), material);
            Bar(root, new Vector3(-w * 0.5f, 0f, 0f), new Vector3(LineWidth, h, LineWidth), material);
            Bar(root, new Vector3(w * 0.5f, 0f, 0f), new Vector3(LineWidth, h, LineWidth), material);
        }
        else
        {
            Bar(root, new Vector3(0f, 0f, h * 0.5f), new Vector3(w, LineWidth * 0.5f, LineWidth), material);
            Bar(root, new Vector3(0f, 0f, -h * 0.5f), new Vector3(w, LineWidth * 0.5f, LineWidth), material);
            Bar(root, new Vector3(-w * 0.5f, 0f, 0f), new Vector3(LineWidth, LineWidth * 0.5f, h), material);
            Bar(root, new Vector3(w * 0.5f, 0f, 0f), new Vector3(LineWidth, LineWidth * 0.5f, h), material);
        }
        return root.gameObject;
    }

    static void Bar(Transform parent, Vector3 position, Vector3 size, Material material)
    {
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.localPosition = position;
        bar.transform.localScale = size;
        var renderer = bar.GetComponent<Renderer>();
        if (material) renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
}
