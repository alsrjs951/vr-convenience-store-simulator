using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// The card, the customer's bill and the drawer's change stock (doc 5.5). These are ordinary props, so
/// plain meshes are fine here; only products must be 3DGS.
/// </summary>
public class PaymentDesk : MonoBehaviour
{
    const float StockMovedMeters = 0.03f;
    const float SpawnClearance = 0.015f;
    // Long side toward the clerk, so the card picked up and pushed straight ahead goes in short edge first, as a real one does.
    // The terminal's slot and guide are built to this size.
    public static readonly Vector3 CardSize = new Vector3(0.065f, 0.008f, 0.1f);
    static readonly int[] BillValues = { 1000, 5000, 10000, 50000 };

    public CardSlotZone CardSlot;
    public CashDrawerZone Drawer;
    public ChangeTrayZone Tray;
    public GameAudio Audio;
    public Transform CustomerPlate;
    public Transform Stock100, Stock500, Stock1000, Stock5000;
    public Material CardMaterial, BillMaterial, CoinMaterial;

    public PaymentProp CustomerCard { get; private set; }
    public PaymentProp CustomerBill { get; private set; }
    public readonly Dictionary<int, PaymentProp> StockItems = new Dictionary<int, PaymentProp>();
    /// <summary>What to say when a card or bill comes too early. Set for customers' orders; the tutorial keeps the default.</summary>
    public Func<string> NotReadyNotice;

    readonly List<PaymentProp> spawned = new List<PaymentProp>();
    Action done;
    Func<bool> ready;
    Func<int> amountDue;
    Action<int> received;
    Action<string> notify;
    bool stockActive;

    void OnEnable()
    {
        CardSlot.Approved += OnCardApproved;
        CardSlot.Rejected += OnPaymentNotReady;
        Drawer.BillReceived += OnBillReceived;
        Tray.Matched += OnChangeMatched;
        Tray.WrongAmount += OnChangeWrong;
        Tray.AmountChanged += OnChangeAmount;
    }

    void OnDisable()
    {
        CardSlot.Approved -= OnCardApproved;
        CardSlot.Rejected -= OnPaymentNotReady;
        Drawer.BillReceived -= OnBillReceived;
        Tray.Matched -= OnChangeMatched;
        Tray.WrongAmount -= OnChangeWrong;
        Tray.AmountChanged -= OnChangeAmount;
    }

    public static int BillFor(int amount)
    {
        foreach (var value in BillValues)
            if (value >= amount) return value;
        return BillValues[BillValues.Length - 1];
    }

    public void BeginCard(Func<bool> canPay, Action<string> onNotice, Action onDone)
    {
        ready = canPay;
        notify = onNotice;
        done = onDone;
        CardSlot.CanApprove = canPay;
        CustomerCard = Spawn("Customer Card", CardSize, CardMaterial, 0, true, "카드", CustomerPlate.position);
    }

    public void BeginCash(int billValue, Func<bool> canPay, Func<int> due, Action<int> onReceived, Action<string> onNotice, Action onDone)
    {
        ready = canPay;
        amountDue = due;
        received = onReceived;
        notify = onNotice;
        done = onDone;
        CustomerBill = Spawn("Customer Bill", new Vector3(0.16f, 0.005f, 0.075f), BillMaterial, billValue, false, $"{billValue:N0}원", CustomerPlate.position);
        Drawer.Expected = CustomerBill;
    }

    /// <summary>Removes everything this payment spawned, including change on the tray.</summary>
    public void EndPayment()
    {
        Tray.End();
        stockActive = false;
        Drawer.Expected = null;
        CardSlot.CanApprove = null;
        foreach (var prop in spawned)
            if (prop) Destroy(prop.gameObject);
        spawned.Clear();
        StockItems.Clear();
        CustomerCard = null;
        CustomerBill = null;
        done = null;
        NotReadyNotice = null;
    }

    string NotReadyText => NotReadyNotice != null ? NotReadyNotice() : "스캔과 봉투 담기를 먼저 끝내세요.";

    void Update()
    {
        // The guide answers "where, and which way" while the customer's card is in the hand. Once the card is in, the lit frame says so.
        CardSlot.ShowGuide(CustomerCard != null && CustomerCard.Grab.isSelected && !CardSlot.CardInside);
        if (!stockActive) return;
        // Taking a coin or bill puts a fresh one in its place, so the drawer never runs out.
        CheckStock(100, Stock100);
        CheckStock(500, Stock500);
        CheckStock(1000, Stock1000);
        CheckStock(5000, Stock5000);
    }

    void CheckStock(int value, Transform spot)
    {
        if (StockItems.TryGetValue(value, out var item) && item
            && Vector3.Distance(item.transform.position, spot.position + Vector3.up * SpawnClearance) < StockMovedMeters)
            return;
        bool isCoin = value < 1000;
        var size = isCoin ? new Vector3(0.045f, 0.008f, 0.045f) : new Vector3(0.16f, 0.005f, 0.075f);
        StockItems[value] = Spawn($"Change {value}", size, isCoin ? CoinMaterial : BillMaterial, value, false, $"{value:N0}", spot.position);
    }

    void OnCardApproved()
    {
        var finish = done;
        done = null;
        finish?.Invoke();
    }

    void OnPaymentNotReady()
    {
        // The card stays in the hand. Taking it away would feel like a glitch.
        notify?.Invoke(NotReadyText);
    }

    void OnBillReceived(PaymentProp bill)
    {
        if (ready != null && !ready())
        {
            if (Audio) Audio.PlayError();
            notify?.Invoke(NotReadyText);
            bill.ReturnHome();
            Drawer.Expected = bill;
            return;
        }
        int value = bill.Value;
        Destroy(bill.gameObject);
        CustomerBill = null;
        received?.Invoke(value);
        int change = value - amountDue();
        if (change <= 0)
        {
            OnChangeMatched();
            return;
        }
        stockActive = true;
        Tray.Begin(change);
    }

    void OnChangeMatched()
    {
        var finish = done;
        done = null;
        finish?.Invoke();
    }

    void OnChangeWrong(int amount)
    {
        var comparison = amount > Tray.Target ? "많습니다" : "적습니다";
        notify?.Invoke($"접시에 {amount:N0}원이 있습니다. 거스름돈 {Tray.Target:N0}원보다 {comparison}.");
    }

    void OnChangeAmount(int amount)
    {
        notify?.Invoke($"접시 {amount:N0}원 / 거스름돈 {Tray.Target:N0}원");
    }

    PaymentProp Spawn(string propName, Vector3 size, Material material, int value, bool isCard, string label, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = propName;
        go.layer = LayerMask.NameToLayer("Interactable");
        go.transform.SetPositionAndRotation(position + Vector3.up * SpawnClearance, Quaternion.identity);
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;

        var body = go.AddComponent<Rigidbody>();
        body.mass = 0.02f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        // A card left on the terminal must still be seen by the slot once the order is ready.
        body.sleepThreshold = 0f;
        var grab = go.AddComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.trackScale = false;
        var prop = go.AddComponent<PaymentProp>();
        prop.Value = value;
        prop.IsCard = isCard;

        // A label panel kept flat above the prop. The parent's scale is undone so its pixels stay readable.
        var panel = WorldUi.CreatePanel(go.transform, "Label", position, Quaternion.Euler(90f, 180f, 0f), new Vector2(200f, 60f), 0.0005f, new Color(0f, 0f, 0f, 0f));
        panel.localScale = new Vector3(0.0005f / size.x, 0.0005f / size.z, 1f);
        panel.localPosition = new Vector3(0f, 0.51f, 0f);
        var text = WorldUi.AddText(panel, 0f, 0f, 200f, 60f, 34, TextAnchor.MiddleCenter);
        text.text = label;
        text.color = Color.black;

        spawned.Add(prop);
        return prop;
    }
}
