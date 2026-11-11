using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What the store is on a given morning, as far as the day's customers care.</summary>
public class ShiftPlan
{
    public int Day;
    /// <summary>Everything a customer can bring to the counter today.</summary>
    public readonly List<ProductPrefab> Assortment = new List<ProductPrefab>();
    /// <summary>
    /// Products that come in a box this morning and are on sale once the player has put them on their shelf. Not part of
    /// <see cref="Assortment"/>: only customers from <see cref="OrderGenerator.NewFromCustomer"/> on ask for them, never as
    /// the first thing in the basket, so a line for one that is not on the shelf yet can simply be left out.
    /// </summary>
    public readonly List<ProductPrefab> NewToday = new List<ProductPrefab>();
    /// <summary>Products the player ordered in, beyond what the store opened with: those on the shelves and today's boxes.</summary>
    public int OrderedProducts;
    /// <summary>What gets heated or filled, when the store has the microwave or the dispenser. Null without it.</summary>
    public ProductPrefab MicrowaveItem, HotWaterItem;
    public bool OnePlus;

    public int Services => (MicrowaveItem != null ? 1 : 0) + (HotWaterItem != null ? 1 : 0) + (OnePlus ? 1 : 0);
}

/// <summary>
/// The days after the training have no scenario rows: the customers follow from what the store sells
/// (docs/p2/해금_성장_기획_2026-11-10.md 6). A bigger assortment and more services bring more people, and past ten the last
/// four come as a rush. The day number seeds it, so the same store on the same day gets the same customers.
/// All numbers are [제안].
/// </summary>
public static class OrderGenerator
{
    public const int BaseCustomers = 6, MaxCustomers = 14, ProductsPerCustomer = 2;
    public const int RushFrom = 10, RushCustomers = 4;
    public const float Gap = 15f, Limit = 90f, RushGap = 10f, RushLimit = 60f;
    /// <summary>Above this a customer pays by card: change is laid down by hand, a few pieces at most.</summary>
    public const int CashCeiling = 5000;
    const int WideAssortment = 10;
    /// <summary>The first customer (counted from 0) who may ask for a product that came in a box this morning: the two before leave time to shelve it.</summary>
    public const int NewFromCustomer = 2;

    public static int CustomersFor(int orderedProducts, int services) =>
        Mathf.Min(MaxCustomers, BaseCustomers + orderedProducts / ProductsPerCustomer + services);

    public static List<Order> Build(ShiftPlan plan)
    {
        var orders = new List<Order>();
        if (plan.Assortment.Count == 0) return orders;
        var random = new System.Random(plan.Day * 104729 + 17);
        int count = CustomersFor(plan.OrderedProducts, plan.Services);

        // Which customers come with a request. Never the first of the day.
        var specials = new string[count];
        void Assign(string special)
        {
            int share = Mathf.Max(1, count / 6);
            for (int placed = 0, tries = 0; count > 1 && placed < share && tries < 40; tries++)
            {
                int at = 1 + random.Next(count - 1);
                if (specials[at] != null) continue;
                specials[at] = special;
                placed++;
            }
        }
        if (plan.MicrowaveItem != null) Assign(Order.Microwave);
        if (plan.HotWaterItem != null) Assign(Order.HotWater);
        if (plan.OnePlus) Assign(Order.OnePlus);

        int mostLines = plan.Assortment.Count + plan.NewToday.Count >= WideAssortment ? 3 : 2;
        for (int i = 0; i < count; i++)
        {
            bool rush = count >= RushFrom && i >= count - RushCustomers;
            var order = new Order
            {
                Id = $"g{plan.Day:00}_{i + 1:00}", Day = plan.Day, Sequence = i + 1,
                SpawnAfterPreviousSeconds = rush ? RushGap : Gap, TimeLimitSeconds = rush ? RushLimit : Limit,
                Payment = random.Next(2) == 0 ? PaymentMethod.Card : PaymentMethod.Cash, NeedsBag = random.Next(2) == 0,
                Special = specials[i] ?? Order.None,
            };
            var left = new List<ProductPrefab>(plan.Assortment);
            int lines = 1 + random.Next(mostLines);

            // The request's own product goes first: heating is about the order's first item (Order.CookTarget).
            var cooked = order.Special == Order.Microwave ? plan.MicrowaveItem : order.Special == Order.HotWater ? plan.HotWaterItem : null;
            if (cooked != null)
            {
                order.Lines.Add(new OrderLine { Product = cooked, Quantity = 1 });
                left.Remove(cooked);
                lines--;
            }
            else if (order.Special == Order.OnePlus)
            {
                order.Lines.Add(new OrderLine { Product = Take(left, random), Quantity = 2 });
                lines--;
            }
            // The first thing in the basket is always something the store had yesterday.
            if (order.Lines.Count == 0)
            {
                order.Lines.Add(new OrderLine { Product = Take(left, random), Quantity = 1 });
                lines--;
            }
            if (i >= NewFromCustomer) left.AddRange(plan.NewToday);
            for (; lines > 0 && left.Count > 0; lines--)
                order.Lines.Add(new OrderLine { Product = Take(left, random), Quantity = 1 });
            orders.Add(order);
        }

        // Somebody asks for each new product on its first day: the order is felt the day the box is opened.
        for (int n = 0; n < plan.NewToday.Count && count > NewFromCustomer; n++)
        {
            var product = plan.NewToday[n];
            if (orders.Exists(order => order.Lines.Exists(line => line.Product == product))) continue;
            int at = NewFromCustomer + random.Next(count - NewFromCustomer);
            orders[at].Lines.Add(new OrderLine { Product = product, Quantity = 1 });
        }
        foreach (var order in orders)
            if (order.Payment == PaymentMethod.Cash && order.TotalPrice > CashCeiling) order.Payment = PaymentMethod.Card;
        Debug.Log($"[Orders] Day {plan.Day}: {orders.Count} generated from {plan.Assortment.Count} products ({plan.OrderedProducts} ordered in), services={plan.Services}");
        return orders;
    }

    static ProductPrefab Take(List<ProductPrefab> from, System.Random random)
    {
        int at = random.Next(from.Count);
        var product = from[at];
        from.RemoveAt(at);
        return product;
    }
}
