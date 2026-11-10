using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Day 2 and 3 side work (doc 5.7, 6.4, D10): delivery boxes on the cart, the day's target slots emptied and
/// highlighted, shelf stock with three expired products, and the counts the settlement needs. Customers and checkout are
/// CustomerDirector's; intake only runs on intake days. On Day 1 it just fills every slot with display stock.
///
/// Stock left on the shelves stands in for the merged shelf splats of doc 7.3, which do not exist yet: each one is a
/// full 3DGS product, so the number on the shelves is kept small (game-design numbers are untouched).
/// </summary>
public class IntakeDirector : MonoBehaviour
{
    const float BadgeMetersPerPixel = 0.0005f;

    public TextAsset Deliveries;
    public ProductCatalog Catalog;
    public GameAudio Audio;
    public ShelfSlotZone[] Slots = new ShelfSlotZone[0];
    /// <summary>Stock that is only there to be seen, on every day. Not intake slots: nothing here is counted or restocked.</summary>
    public ShelfSlotZone[] DecorSlots = new ShelfSlotZone[0];
    public BoxStandSocket[] Stands = new BoxStandSocket[0];
    public BoxCutter[] Cutters = new BoxCutter[0];
    public Transform[] CartSpots = new Transform[0];
    public Material Cardboard, Tape, Hole, Bead, Hint;
    /// <summary>Where copies of the guide board go: above the shelves and the cart, so instructions are in view away from the counter.</summary>
    public Transform[] GuideAnchors = new Transform[0];
    public Text[] GuideTexts { get; private set; } = new Text[0];
    public int ExpiredPerDay = 3;

    public bool Active { get; private set; }
    public int Day { get; private set; }
    /// <summary>Day 1 display stock (splat pictures, not products) on all slots, and how many gaussians they add.</summary>
    public int DisplayCount { get; private set; }
    public long DisplayGaussians { get; private set; }

    void Awake()
    {
        var texts = new List<Text>();
        foreach (var anchor in GuideAnchors)
        {
            if (anchor == null) continue;
            var panel = WorldUi.CreatePanel(anchor, "Intake Guide", anchor.position, anchor.rotation, new Vector2(1300f, 190f), 0.0007f, WorldUi.Palette.Panel);
            var text = WorldUi.AddText(panel, 30f, 14f, 1240f, 162f, 40, TextAnchor.MiddleCenter);
            HeadlineText.On(text);
            texts.Add(text);
            panel.gameObject.SetActive(false);
        }
        GuideTexts = texts.ToArray();
    }
    public readonly List<Delivery> Today = new List<Delivery>();
    public readonly List<StockBox> Boxes = new List<StockBox>();
    public readonly List<ProductPrefab> ShelfStock = new List<ProductPrefab>();
    public readonly List<ProductPrefab> ExpiredAtStart = new List<ProductPrefab>();
    public readonly List<string> SkippedDeliveries = new List<string>();
    public int Shelved { get; private set; }
    public int Misplaced { get; private set; }
    public int Disposed { get; private set; }
    public int FalseDisposals { get; private set; }
    public int Recycled { get; private set; }
    public int DeliveredTotal { get; private set; }
    public int ExpiredTotal { get; private set; }
    public int UnrecycledEmptyBoxes { get; private set; }
    public string LastGuide { get; private set; } = "";

    public StockBox OpenBox
    {
        get
        {
            foreach (var box in Boxes)
                if (box != null && box.Stage == BoxStage.Open) return box;
            return null;
        }
    }

    /// <summary>Doc 6.5: (shelved + disposed) / (delivered + expired). 1 when there was nothing to do.</summary>
    public float TaskRate => DeliveredTotal + ExpiredTotal > 0 ? (float)(Shelved + Disposed) / (DeliveredTotal + ExpiredTotal) : 1f;

    public IReadOnlyDictionary<string, ShelfSlotZone> SlotsById
    {
        get
        {
            var map = new Dictionary<string, ShelfSlotZone>();
            foreach (var slot in Slots)
                if (slot != null) map[slot.SlotId] = slot;
            return map;
        }
    }

    public IEnumerable<ProductPrefab> HeldProducts()
    {
        foreach (var product in ProductPrefab.Enabled)
            if (product != null && product.Grab.isSelected) yield return product;
    }

    public BoxStandSocket StandForSlot(string slotId)
    {
        foreach (var stand in Stands)
            if (stand != null && slotId.StartsWith(stand.Shelf)) return stand;
        return Stands.Length > 0 ? Stands[0] : null;
    }

    /// <summary>Sets up a day. Day 1 has no intake: every slot is full of display stock and plain (no highlight or count).</summary>
    /// <param name="deliveriesOfDay">Which day's rows of the deliveries file to use: a day with no rows of its own (the
    /// days after the training) takes another day's boxes. 0: the day's own.</param>
    public void BeginDay(int day, int orderCount, int deliveriesOfDay = 0)
    {
        StockDecor();
        Clear();
        Day = day;
        Active = day >= 2 && Deliveries != null;
        if (!Active)
        {
            foreach (var guide in GuideTexts)
                if (guide != null) guide.transform.parent.gameObject.SetActive(false);
            StockDisplays();
            return;
        }

        Today.AddRange(DeliveryLoader.Parse(Deliveries.text, deliveriesOfDay > 0 ? deliveriesOfDay : day, Catalog, SlotsById, orderCount, SkippedDeliveries));
        var emptied = new Dictionary<string, int>();
        var targetsNow = new HashSet<string>();
        foreach (var delivery in Today)
        {
            emptied.TryGetValue(delivery.TargetSlot, out int n);
            emptied[delivery.TargetSlot] = n + delivery.CaseQuantity;
            if (delivery.ArriveAfterOrder > 0) continue;
            // A box still on its way counts from the moment it arrives, so a day cut short is not scored for it.
            DeliveredTotal += delivery.CaseQuantity;
            targetsNow.Add(delivery.TargetSlot);
        }

        // Doc 5.7.3: a target slot loses as many places as the day's boxes bring, back row first, then the front row from
        // the right, so what is left always fills each column from the front. Other slots are fully stocked. A slot waiting
        // for a mid-day box is already emptied but lights up only when the box comes.
        foreach (var slot in Slots)
        {
            if (slot == null) continue;
            emptied.TryGetValue(slot.SlotId, out int toEmpty);
            slot.SetTarget(targetsNow.Contains(slot.SlotId));
            var prefab = Catalog.Find(slot.ProductId);
            if (prefab == null) continue;
            // Made now, not at the first product shelved: waking a renderer in the middle of the shift would show as a hitch.
            slot.PrepareMerged(prefab);
            var empty = new HashSet<int>();
            for (int row = slot.Rows - 1; row >= 0 && empty.Count < toEmpty; row--)
                for (int column = slot.Columns - 1; column >= 0 && empty.Count < toEmpty; column--)
                    empty.Add(column * slot.Rows + row);
            for (int unit = 0; unit < slot.Capacity; unit++)
                if (!empty.Contains(unit)) ShelfStock.Add(slot.Stock(prefab, unit));
        }

        // Doc 6.4: three products on the shelves expire, never in a slot that takes a delivery today. A fixed seed per day
        // keeps runs comparable.
        var pool = ShelfStock.FindAll(p => p != null && !emptied.ContainsKey(SlotOf(p)));
        var random = new System.Random(day * 7919);
        for (int i = 0; i < ExpiredPerDay && pool.Count > 0; i++)
        {
            int pick = random.Next(pool.Count);
            MarkExpired(pool[pick]);
            ExpiredAtStart.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
        ExpiredTotal = ExpiredAtStart.Count;

        foreach (var delivery in Today)
            if (delivery.ArriveAfterOrder == 0) PutOnCart(delivery);
        if (Audio && Boxes.Count > 0) Audio.PlayAt(StoreSound.Delivery, Boxes[0].transform.position + Vector3.up * 0.5f);
        Debug.Log($"[Intake] Day {day}: boxes={Boxes.Count} of {Today.Count} delivered={DeliveredTotal} stock={ShelfStock.Count} expired={ExpiredTotal} skipped={SkippedDeliveries.Count}");
    }

    /// <summary>
    /// Doc 5.7.2: a box with arrive_after_order = n comes onto the cart as soon as the n-th customer is done (paid or gone),
    /// with the delivery sound. True when one came, so the caller can show "입고 도착".
    /// </summary>
    public bool ReceiveMidDayBoxes(int finishedOrder)
    {
        if (!Active) return false;
        bool arrived = false;
        var slots = SlotsById;
        foreach (var delivery in Today)
        {
            if (delivery.ArriveAfterOrder != finishedOrder) continue;
            arrived = true;
            DeliveredTotal += delivery.CaseQuantity;
            if (slots.TryGetValue(delivery.TargetSlot, out var slot)) slot.SetTarget(true);
            var box = PutOnCart(delivery);
            if (Audio) Audio.PlayAt(StoreSound.Delivery, box.transform.position + Vector3.up * 0.5f);
            Debug.Log($"[Intake] Day {Day}: box {delivery.Id} arrived after customer {finishedOrder}; delivered={DeliveredTotal}");
        }
        return arrived;
    }

    StockBox PutOnCart(Delivery delivery)
    {
        int free = FreeCartSpot();
        var spot = CartSpots.Length == 0 ? transform : CartSpots[free >= 0 ? free : CartSpots.Length - 1];
        // No free place (both boxes still on the cart): on top of the last one.
        var lift = Vector3.up * (free >= 0 ? 0f : 0.4f);
        var look = new StockBox.Look { Cardboard = Cardboard, Tape = Tape, Hole = Hole, Bead = Bead, Hint = Hint };
        var box = StockBox.Create(delivery, this, spot.position + lift + Vector3.up * 0.002f, spot.rotation, look);
        Boxes.Add(box);
        return box;
    }

    /// <summary>The first place on the cart with no box standing on it, or -1.</summary>
    int FreeCartSpot()
    {
        const float occupied = 0.3f;
        for (int i = 0; i < CartSpots.Length; i++)
        {
            bool taken = false;
            foreach (var box in Boxes)
                if (box != null && Vector3.Distance(box.transform.position, CartSpots[i].position) < occupied) taken = true;
            if (!taken) return i;
        }
        return -1;
    }

    /// <summary>
    /// Every place of every slot shows its product, as on Day 2 once all boxes are shelved: the same splats Day 2's
    /// shelves end up with. Day 1's peak is that plus the order on the counter, which Day 2 also reaches (and Day 2 adds
    /// the open box on top).
    /// </summary>
    void StockDisplays()
    {
        if (Catalog == null) return;
        foreach (var slot in Slots)
        {
            if (slot == null) continue;
            var prefab = Catalog.Find(slot.ProductId);
            if (prefab == null || prefab.Splat == null) continue;
            for (int unit = 0; unit < slot.Capacity; unit++)
            {
                var splat = slot.Display(prefab, unit);
                DisplayCount++;
                if (slot.Merged == null && splat.m_Asset != null) DisplayGaussians += splat.m_Asset.splatCount;
            }
            if (slot.Merged != null) DisplayGaussians += slot.Merged.Active.m_Asset.splatCount;
        }
        Debug.Log($"[Intake] Day {Day}: no intake; display stock={DisplayCount} on {Slots.Length} slots, gaussians={DisplayGaussians}");
    }

    bool decorStocked;

    /// <summary>
    /// The shelves seen from the door and the counter (2026-11-09), once and for good. Only with merged splats: every place
    /// as its own full-quality product would be far over the gaussian budget, so those shelves then stay as they were built.
    /// </summary>
    void StockDecor()
    {
        if (decorStocked || Catalog == null) return;
        decorStocked = true;
        if (ShelfSlotZone.IndividualOnly) return;
        int places = 0;
        long gaussians = 0;
        foreach (var slot in DecorSlots)
        {
            if (slot == null || slot.MergedNear == null || slot.MergedFar == null) continue;
            var prefab = Catalog.Find(slot.ProductId);
            if (prefab == null || prefab.Splat == null) continue;
            for (int unit = 0; unit < slot.Capacity; unit++) slot.Display(prefab, unit);
            places += slot.Capacity;
            gaussians += slot.Merged.Far.m_Asset.splatCount;
        }
        Debug.Log($"[Intake] Decor stock: {places} places on {DecorSlots.Length} slots, far gaussians={gaussians}");
    }

    /// <summary>
    /// Which of the shelves that are only looked at hold their product (docs/p2/해금_성장_기획_2026-11-10.md 4): a store
    /// that grows opens with them empty and fills one for each product ordered in. Null: all of them, as the store was built.
    /// The whole slot is switched, not its renderers, which the slot's own distance switching turns on and off.
    /// </summary>
    public void ShowDecor(ICollection<string> stockedSlotIds)
    {
        foreach (var slot in DecorSlots)
            if (slot != null) slot.gameObject.SetActive(stockedSlotIds == null || stockedSlotIds.Contains(slot.SlotId));
    }

    string SlotOf(ProductPrefab product)
    {
        foreach (var slot in Slots)
            if (slot != null && System.Array.IndexOf(slot.Occupants, product) >= 0) return slot.SlotId;
        return "";
    }

    /// <summary>Doc 6.5: an empty box still standing around when the day ends is a penalty.</summary>
    public void EndDay()
    {
        UnrecycledEmptyBoxes = 0;
        foreach (var box in Boxes)
            if (box != null && box.Stage == BoxStage.Empty) UnrecycledEmptyBoxes++;
    }

    void Clear()
    {
        foreach (var box in Boxes)
            if (box != null) Destroy(box.gameObject);
        Boxes.Clear();
        foreach (var slot in Slots)
            if (slot != null) slot.Clear();
        foreach (var product in ProductPrefab.Enabled.ToArray())
            if (product != null && (product.ReturnUnit != null || product.IsShelved || ShelfStock.Contains(product))) Destroy(product.gameObject);
        ShelfStock.Clear();
        ExpiredAtStart.Clear();
        Today.Clear();
        SkippedDeliveries.Clear();
        Shelved = Misplaced = Disposed = FalseDisposals = Recycled = DeliveredTotal = ExpiredTotal = UnrecycledEmptyBoxes = 0;
        DisplayCount = 0;
        DisplayGaussians = 0;
        Active = false;
    }

    // ---------- Results of the player's actions ----------

    public void Shelve(ProductPrefab product, ShelfSlotZone slot, int unit)
    {
        var from = product.ReturnUnit;
        slot.Commit(product, unit);
        Shelved++;
        from?.MarkDone();
        ShelfStock.Remove(product);
        if (from == null || from.Box.Stage == BoxStage.Empty) RefreshTargets();
    }

    /// <summary>Doc 5.7.1 B6: let go on a red outline. Error, one misplacement, and back to the box place (or its shelf spot).</summary>
    public void Misplace(ProductPrefab product, ShelfSlotZone slot)
    {
        Misplaced++;
        if (Audio) Audio.PlayAt(StoreSound.Error, slot.transform.position);
        product.ReturnNow();
    }

    public void Dispose(ProductPrefab product, Vector3 at)
    {
        Disposed++;
        if (Audio) Audio.PlayAt(StoreSound.Dispose, at);
        ShelfStock.Remove(product);
        Destroy(product.gameObject);
    }

    public void FalseDisposal(ProductPrefab product, Vector3 at)
    {
        FalseDisposals++;
        if (Audio) Audio.PlayAt(StoreSound.Error, at);
        product.ReturnNow();
    }

    public void Recycle(StockBox box, Vector3 at)
    {
        Recycled++;
        if (Audio) Audio.PlayAt(StoreSound.Recycle, at);
        box.Recycle();
    }

    void RefreshTargets()
    {
        foreach (var slot in Slots)
            if (slot != null && slot.IsTarget) slot.SetTarget(true);
    }

    /// <summary>Puts a guide line on the shelf-side panels too.</summary>
    /// <summary>The panels by the shelves and the cart are for the intake tutorial; the board carries the rest.</summary>
    public void ShowGuides(bool show)
    {
        guidesShown = show;
        foreach (var guide in GuideTexts)
            if (guide != null) guide.transform.parent.gameObject.SetActive(show && Active);
    }

    bool guidesShown = true;

    public void SetGuide(string text)
    {
        LastGuide = text;
        foreach (var guide in GuideTexts)
            if (guide != null)
            {
                guide.text = text;
                guide.transform.parent.gameObject.SetActive(Active && guidesShown);
            }
    }

    /// <summary>Doc 6.4: a purple badge with words and a mark, so it is not color alone (game design 13).</summary>
    void MarkExpired(ProductPrefab product)
    {
        product.IsExpired = true;
        var top = product.transform.TransformPoint(product.Proxy.center) + Vector3.up * (product.Proxy.size.y * 0.5f + 0.04f);
        // The product's front faces the aisle; a canvas is read from behind its forward.
        var panel = WorldUi.CreatePanel(product.transform, "Expired Badge", top, product.transform.rotation * Quaternion.Euler(0f, 180f, 0f),
            new Vector2(260f, 70f), BadgeMetersPerPixel, new Color(0.62f, 0.1f, 0.18f, 0.96f));
        var text = WorldUi.AddText(panel, 0f, 0f, 260f, 70f, 38, TextAnchor.MiddleCenter);
        text.text = "! 기한 지남";
        WorldUi.Bold(text);
    }
}
