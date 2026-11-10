using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Scan list, totals and payment for the current order (doc 5.3, 5.5).</summary>
public class POSController : MonoBehaviour
{
    public class Line
    {
        public ProductData Data;
        public int Scanned;
        public int Ordered;
    }

    public ScannerZone Scanner;
    public GameAudio Audio;

    public event Action Changed;
    public event Action<ProductData> ItemScanned;
    public event Action PaymentCompleted;

    public readonly List<Line> Lines = new List<Line>();
    public Order Current { get; private set; }
    public int CancelCount { get; private set; }
    public int Received { get; private set; }
    public string Notice { get; private set; } = "";

    readonly HashSet<ProductPrefab> scannedProducts = new HashSet<ProductPrefab>();
    readonly HashSet<ProductPrefab> baggedProducts = new HashSet<ProductPrefab>();
    readonly HashSet<ProductPrefab> cookedProducts = new HashSet<ProductPrefab>();

    public int Total
    {
        get
        {
            int total = 0;
            foreach (var line in Lines) total += LinePrice(line);
            return total;
        }
    }

    /// <summary>What the row costs now. On a 1+1 order only every other unit is charged (doc 6.4 oneplus).</summary>
    public int LinePrice(Line line) => line.Data.Price * (Current != null ? Current.ChargedUnits(line.Ordered, line.Scanned) : line.Scanned);

    public bool IsOnePlusLine(Line line) => Current != null && Current.Special == Order.OnePlus && line.Ordered >= 2;

    public int ChangeDue => Mathf.Max(0, Received - Total);

    public bool AllScanned
    {
        get
        {
            if (Current == null) return false;
            foreach (var line in Lines)
                if (line.Scanned != line.Ordered) return false;
            return true;
        }
    }

    public bool AllBagged
    {
        get
        {
            if (Current == null || !Current.NeedsBag) return true;
            int ordered = 0;
            foreach (var line in Current.Lines) ordered += line.Quantity;
            return baggedProducts.Count >= ordered;
        }
    }

    /// <summary>Doc 6.4: a heating request has to be done before the order can be paid for.</summary>
    public bool AllCooked => Current == null || !Current.NeedsCooking || cookedProducts.Count >= Current.CookCount;

    public bool ReadyForPayment => AllScanned && AllCooked && AllBagged;

    /// <summary>This product is the one the order wants heated, and it has not been yet.</summary>
    public bool NeedsCooking(ProductPrefab product) => Current != null && Current.NeedsCooking && product.Data == Current.CookTarget && !product.IsProcessed;

    /// <summary>Doc 6.2: a row is ticked when it is scanned and, for the item of a heating request, heated too.</summary>
    public bool LineDone(Line line) =>
        line.Scanned == line.Ordered && (Current == null || !Current.NeedsCooking || line.Data != Current.CookTarget || AllCooked);

    public string CookFirstNotice => Current != null && Current.Special == Order.HotWater ? "온수를 먼저 부으세요." : "전자레인지에 먼저 데우세요.";

    public string NotReadyNotice => AllScanned && !AllCooked ? CookFirstNotice : "스캔과 봉투 담기를 먼저 끝내세요.";

    void OnEnable() { if (Scanner) Scanner.Scanned += AddScannedItem; }
    void OnDisable() { if (Scanner) Scanner.Scanned -= AddScannedItem; }

    /// <summary>A new day: the cancel count is per day (settlement, doc 6.5).</summary>
    public void ResetDay()
    {
        EndOrder();
        CancelCount = 0;
    }

    public void BeginOrder(Order order)
    {
        Current = order;
        Lines.Clear();
        scannedProducts.Clear();
        baggedProducts.Clear();
        cookedProducts.Clear();
        Received = 0;
        Notice = "";
        foreach (var line in order.Lines)
            Lines.Add(new Line { Data = line.Product.Data, Ordered = line.Quantity });
        Changed?.Invoke();
    }

    public void EndOrder()
    {
        Current = null;
        Lines.Clear();
        scannedProducts.Clear();
        baggedProducts.Clear();
        cookedProducts.Clear();
        Received = 0;
        Notice = "";
        Changed?.Invoke();
    }

    void AddScannedItem(ProductPrefab product)
    {
        if (Current == null) return; // Tutorial or idle: the scanner still beeps, but nothing is charged.
        var line = Lines.Find(l => l.Data == product.Data);
        if (line == null)
        {
            line = new Line { Data = product.Data, Ordered = 0 };
            Lines.Add(line);
        }
        line.Scanned++;
        scannedProducts.Add(product);
        Notice = line.Scanned > line.Ordered ? $"{product.Data.Name}: 주문보다 많이 찍혔습니다. [-]로 취소하세요."
            : NeedsCooking(product) ? (Current.Special == Order.HotWater ? "전자레인지 옆 온수기 아래에 두세요." : "왼쪽 냉장고 옆 전자레인지에 넣으세요.")
            : "";
        ItemScanned?.Invoke(product.Data);
        Changed?.Invoke();
    }

    /// <summary>The [-] button on a POS row.</summary>
    public void CancelOne(ProductData data)
    {
        var line = Lines.Find(l => l.Data == data);
        if (line == null || line.Scanned == 0) return;
        line.Scanned--;
        CancelCount++;
        if (line.Scanned == 0 && line.Ordered == 0) Lines.Remove(line);
        Notice = "";
        if (Audio) Audio.PlayCancel();
        Changed?.Invoke();
    }

    public bool IsScanned(ProductPrefab product) => scannedProducts.Contains(product);

    public void MarkBagged(ProductPrefab product)
    {
        baggedProducts.Add(product);
        Changed?.Invoke();
    }

    /// <summary>A microwave or water dispenser finished with this product.</summary>
    public void MarkCooked(ProductPrefab product)
    {
        cookedProducts.Add(product);
        Notice = !AllCooked ? "" : Current.NeedsBag ? "다 됐습니다. 봉투에 담고 결제하세요." : "다 됐습니다. 결제하세요.";
        Changed?.Invoke();
    }

    public void SetNotice(string notice)
    {
        Notice = notice;
        Changed?.Invoke();
    }

    public void CompleteCard()
    {
        if (Audio) Audio.PlayApproved();
        Finish();
    }

    public void ReceiveCash(int amount)
    {
        Received += amount;
        Notice = ChangeDue > 0 ? $"거스름돈 {ChangeDue:N0}원을 접시에 놓으세요." : "";
        if (Audio) Audio.PlayDrawer();
        Changed?.Invoke();
    }

    public void CompleteCash() => Finish();

    void Finish() => PaymentCompleted?.Invoke();
}
