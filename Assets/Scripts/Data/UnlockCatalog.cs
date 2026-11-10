using System;
using System.Collections.Generic;
using UnityEngine;

public enum UnlockKind { Product, Equipment }

/// <summary>One thing the player can order in on the ordering tablet: a product for an empty shelf, or a piece of equipment.</summary>
public class UnlockItem
{
    public string Id;
    public UnlockKind Kind;
    public string Name;
    public int Price;
    /// <summary>Product: the display slot it fills. Equipment: empty.</summary>
    public string Slot = "";
    /// <summary>Product: the product itself. Equipment: a product that joins the assortment with it, or null.</summary>
    public ProductPrefab Product;
    public string Note = "";
}

/// <summary>
/// Parses Data/unlocks.csv (docs/p2/해금_성장_기획_2026-11-10.md). Bad rows are skipped and reported, like the other
/// data files; the scene builder fails the build on any.
/// </summary>
public static class UnlockCatalog
{
    // The equipment the game knows how to switch on. A row with another id is skipped.
    public const string HotWater = "hotwater", Microwave = "microwave", OnePlus = "oneplus", Cutter = "cutter", Scanner = "scanner", Music = "music", Coins = "coins";
    static readonly string[] Equipment = { HotWater, Microwave, OnePlus, Cutter, Scanner, Music, Coins };
    const int ExpectedColumns = 7;

    public static List<UnlockItem> Parse(string csv, ProductCatalog catalog, List<string> skippedReasons = null)
    {
        var items = new List<UnlockItem>();
        var seen = new HashSet<string>();
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("id,", StringComparison.Ordinal)) continue;
            if (TryParseRow(line, catalog, seen, out var item, out var reason)) items.Add(item);
            else
            {
                skippedReasons?.Add($"{reason}: {line}");
                Debug.LogWarning($"[Unlocks] Skipped row: {reason}: {line}");
            }
        }
        return items;
    }

    static bool TryParseRow(string line, ProductCatalog catalog, HashSet<string> seen, out UnlockItem item, out string reason)
    {
        item = null;
        var c = line.Split(',');
        if (c.Length != ExpectedColumns) { reason = "column count"; return false; }
        if (c[1] != "product" && c[1] != "equipment") { reason = "kind must be product or equipment"; return false; }
        if (!int.TryParse(c[3], out int price) || price < 0) { reason = "price"; return false; }
        if (!seen.Add(c[0])) { reason = "duplicate id " + c[0]; return false; }
        bool isProduct = c[1] == "product";
        item = new UnlockItem { Id = c[0], Kind = isProduct ? UnlockKind.Product : UnlockKind.Equipment, Name = c[2], Price = price, Slot = c[4], Note = c[6] };
        if (isProduct)
        {
            item.Product = catalog.Find(c[0]);
            if (item.Product == null) { reason = "unknown product " + c[0]; return false; }
            if (c[4].Length == 0) { reason = "a product needs its slot"; return false; }
            if (item.Name.Length == 0) item.Name = WorldUi.Clean(item.Product.Data.Name);
        }
        else
        {
            if (Array.IndexOf(Equipment, c[0]) < 0) { reason = "unknown equipment " + c[0]; return false; }
            if (item.Name.Length == 0) { reason = "equipment needs a name"; return false; }
            if (c[5].Length > 0)
            {
                item.Product = catalog.Find(c[5]);
                if (item.Product == null) { reason = "unknown product " + c[5]; return false; }
            }
        }
        reason = null;
        return true;
    }
}
