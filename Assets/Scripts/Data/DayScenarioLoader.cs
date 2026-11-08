using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public enum PaymentMethod { Card, Cash }

public class OrderLine
{
    public ProductPrefab Product;
    public int Quantity;
}

public class Order
{
    public string Id;
    public int Day;
    public int Sequence;
    public float SpawnAfterPreviousSeconds;
    public float TimeLimitSeconds;
    public PaymentMethod Payment;
    public bool NeedsBag;
    /// <summary>none, microwave, hotwater or oneplus (doc 6.4). Read and checked; Day 3 is where they come into play.</summary>
    public string Special = "none";
    public readonly List<OrderLine> Lines = new List<OrderLine>();
}

/// <summary>Parses the scenarios.csv schema (doc 6.5). Bad rows are skipped and reported, as doc 4.7 asks.</summary>
public static class DayScenarioLoader
{
    const int ExpectedColumns = 9;
    static readonly string[] Specials = { "none", "microwave", "hotwater", "oneplus" };

    public static List<Order> Parse(string csv, int day, ProductCatalog catalog) => Parse(csv, day, catalog, null);

    /// <param name="skippedReasons">Gets one line per skipped row of any day, for the scene builder's check.</param>
    public static List<Order> Parse(string csv, int day, ProductCatalog catalog, List<string> skippedReasons)
    {
        var orders = new List<Order>();
        int skipped = 0;
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("order_id", StringComparison.Ordinal)) continue;
            if (TryParseRow(line, catalog, out var order, out var reason))
            {
                if (order.Day == day) orders.Add(order);
            }
            else
            {
                skipped++;
                skippedReasons?.Add($"{reason}: {line}");
                Debug.LogWarning($"[Scenario] Skipped row: {reason}: {line}");
            }
        }
        orders.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        Debug.Log($"[Scenario] Day {day}: {orders.Count} orders, {skipped} skipped");
        return orders;
    }

    static bool TryParseRow(string line, ProductCatalog catalog, out Order order, out string reason)
    {
        order = null;
        var c = line.Split(',');
        if (c.Length != ExpectedColumns) { reason = "column count"; return false; }
        var invariant = CultureInfo.InvariantCulture;
        if (!int.TryParse(c[1], out int day) || !int.TryParse(c[2], out int sequence)
            || !float.TryParse(c[3], NumberStyles.Float, invariant, out float spawn)
            || !float.TryParse(c[4], NumberStyles.Float, invariant, out float limit))
        {
            reason = "number format";
            return false;
        }
        if (c[5] != "card" && c[5] != "cash") { reason = "payment"; return false; }
        if (c[6] != "0" && c[6] != "1") { reason = "bag must be 0 or 1"; return false; }
        if (Array.IndexOf(Specials, c[7]) < 0) { reason = "special must be none, microwave, hotwater or oneplus"; return false; }
        order = new Order
        {
            Id = c[0], Day = day, Sequence = sequence, SpawnAfterPreviousSeconds = spawn, TimeLimitSeconds = limit,
            Payment = c[5] == "card" ? PaymentMethod.Card : PaymentMethod.Cash, NeedsBag = c[6] == "1", Special = c[7],
        };
        foreach (var item in c[8].Split('|'))
        {
            int x = item.LastIndexOf('x');
            if (x <= 0 || !int.TryParse(item.Substring(x + 1), out int quantity) || quantity < 1) { reason = "item format"; return false; }
            var id = item.Substring(0, x);
            var product = catalog.Find(id);
            if (product == null) { reason = "unknown product " + id; return false; }
            order.Lines.Add(new OrderLine { Product = product, Quantity = quantity });
        }
        reason = null;
        return true;
    }
}
