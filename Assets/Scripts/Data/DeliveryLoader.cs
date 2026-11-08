using System;
using System.Collections.Generic;
using UnityEngine;

public class Delivery
{
    public string Id;
    public int Day;
    public int BoxSequence;
    public ProductPrefab Product;
    public int CaseQuantity;
    public string TargetSlot;
    public int ArriveAfterOrder;
}

/// <summary>
/// Parses the deliveries.csv schema (doc 6.5): one row is one box. Bad rows are skipped with the reason, and a summary
/// "처리 N건 / 스킵 M건" is logged, as doc 4.7 asks. Day 1 rows are ignored (Day 1 is checkout only, doc 6.3).
/// </summary>
public static class DeliveryLoader
{
    const int ExpectedColumns = 7;
    static readonly int[] CaseQuantities = { 4, 6, 8 };

    /// <param name="slots">The scene's shelf slots by id, for checks ② and ④.</param>
    /// <param name="orderCount">That day's number of orders, for check ⑤.</param>
    public static List<Delivery> Parse(string csv, int day, ProductCatalog catalog, IReadOnlyDictionary<string, ShelfSlotZone> slots, int orderCount, List<string> skippedReasons = null)
    {
        var result = new List<Delivery>();
        var perSlot = new Dictionary<string, int>();
        int skipped = 0;
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("delivery_id", StringComparison.Ordinal)) continue;
            string reason = null;
            var c = line.Split(',');
            Delivery d = null;
            if (c.Length != ExpectedColumns) reason = "column count";
            else if (!int.TryParse(c[1], out int rowDay) || !int.TryParse(c[2], out int seq) || !int.TryParse(c[4], out int qty) || !int.TryParse(c[6], out int after))
                reason = "number format";
            else
            {
                if (rowDay != day) continue;
                var product = catalog.Find(c[3]);
                slots.TryGetValue(c[5], out var slot);
                perSlot.TryGetValue(c[5], out int already);
                if (rowDay == 1) continue;
                if (product == null) reason = "unknown product " + c[3];
                else if (slot == null) reason = "no slot " + c[5] + " in the scene";
                else if (slot.ProductId != product.Data.Id) reason = $"slot {c[5]} holds {slot.ProductId}, not {product.Data.Id}";
                else if (Array.IndexOf(CaseQuantities, qty) < 0) reason = "case_qty must be 4, 6 or 8";
                else if (already + qty > slot.Capacity) reason = $"slot {c[5]} capacity {slot.Capacity} exceeded";
                else if (after < 0 || after > orderCount) reason = "arrive_after_order beyond the day's orders";
                else
                {
                    perSlot[c[5]] = already + qty;
                    d = new Delivery { Id = c[0], Day = rowDay, BoxSequence = seq, Product = product, CaseQuantity = qty, TargetSlot = c[5], ArriveAfterOrder = after };
                }
            }
            if (d != null) result.Add(d);
            else
            {
                skipped++;
                skippedReasons?.Add(reason);
                Debug.LogWarning($"[Deliveries] Skipped row: {reason}: {line}");
            }
        }
        result.Sort((a, b) => a.BoxSequence.CompareTo(b.BoxSequence));
        Debug.Log($"[Deliveries] Day {day}: 처리 {result.Count}건 / 스킵 {skipped}건");
        return result;
    }
}
