using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// The card, the customer's money and the change out of the till (doc 5.5). These are ordinary props, so plain meshes are
/// fine here; only products must be 3DGS.
///
/// Cash, as at a real till (2026-11-10): once the order is ready the drawer springs open. The customer's note goes into the
/// drawer, the change comes out of its slots and cups onto the tray, and the POS shows what was received, what is owed back
/// and what lies on the tray. A piece taken by mistake can be put back in the drawer. The drawer shuts when the sale is over.
/// </summary>
public class PaymentDesk : MonoBehaviour
{
    /// <summary>What one kind of note or coin looks like. The scene builder fills these in.</summary>
    [Serializable]
    public class CashLook
    {
        public int Value;
        public bool Coin;
        /// <summary>Length (a coin's diameter), thickness and width of what is seen, in meters.</summary>
        public Vector3 Size;
        public Material Material;
    }

    const float StockMovedMeters = 0.03f;
    const float SpawnClearance = 0.015f;
    // What physics sees is thicker than the paper and metal that are drawn: a body a millimetre thick would not rest well.
    public const float NoteBody = 0.005f, CoinBody = 0.006f;
    // Long side toward the clerk, so the card picked up and pushed straight ahead goes in short edge first, as a real one does.
    // The terminal's slot and guide are built to this size.
    public static readonly Vector3 CardSize = new Vector3(0.065f, 0.008f, 0.1f);
    static readonly int[] BillValues = { 1000, 5000, 10000, 50000 };

    public CardSlotZone CardSlot;
    public CashDrawerZone Drawer;
    public ChangeTrayZone Tray;
    public CashRegister Register;
    public GameAudio Audio;
    public Transform CustomerPlate;
    public Material CardMaterial;
    public Mesh NoteMesh, CoinMesh;
    public CashLook[] Cash = new CashLook[0];

    public PaymentProp CustomerCard { get; private set; }
    public PaymentProp CustomerBill { get; private set; }
    /// <summary>The piece lying on top in each slot of the open drawer, by its value.</summary>
    public readonly Dictionary<int, PaymentProp> StockItems = new Dictionary<int, PaymentProp>();
    /// <summary>What to say when a card or bill comes too early. Set for customers' orders; the tutorial keeps the default.</summary>
    public Func<string> NotReadyNotice;
    /// <summary>The coin dispenser (an equipment unlock): the coins of the change land on the tray by themselves; bills are still handed over.</summary>
    public bool AutoCoins;
    /// <summary>Coins the dispenser has put out for the payment going on, for the checks.</summary>
    public int CoinsDispensed { get; private set; }

    /// <summary>A cash sale is going on, from the customer laying the money down until the sale ends.</summary>
    public bool CashActive { get; private set; }
    /// <summary>What the customer owes.</summary>
    public int CashDue => amountDue != null ? amountDue() : 0;
    /// <summary>What has gone into the drawer.</summary>
    public int CashReceived { get; private set; }
    public int ChangeDue => Mathf.Max(0, CashReceived - CashDue);
    /// <summary>What lies on the change tray now.</summary>
    public int ChangeLaid => Tray != null ? Tray.Amount : 0;
    /// <summary>A note or coin taken out of the till is in a hand.</summary>
    public bool ChangeInHand
    {
        get
        {
            foreach (var prop in spawned)
                if (prop && prop.Kind == PaymentKind.Change && !prop.Docked && prop.Grab.isSelected) return true;
            return false;
        }
    }
    /// <summary>Anything of the above changed. The POS screen draws from it.</summary>
    public event Action CashChanged;

    readonly List<PaymentProp> spawned = new List<PaymentProp>();
    Action done;
    Func<bool> ready;
    Func<int> amountDue;
    Action<int> received;
    Action<string> notify;

    void OnEnable()
    {
        CardSlot.Approved += OnCardApproved;
        CardSlot.Rejected += OnPaymentNotReady;
        Drawer.BillReceived += OnBillReceived;
        Drawer.ChangeReturned += OnChangeReturned;
        Tray.Matched += OnChangeMatched;
        Tray.WrongAmount += OnChangeWrong;
        Tray.AmountChanged += OnChangeAmount;
    }

    void OnDisable()
    {
        CardSlot.Approved -= OnCardApproved;
        CardSlot.Rejected -= OnPaymentNotReady;
        Drawer.BillReceived -= OnBillReceived;
        Drawer.ChangeReturned -= OnChangeReturned;
        Tray.Matched -= OnChangeMatched;
        Tray.WrongAmount -= OnChangeWrong;
        Tray.AmountChanged -= OnChangeAmount;
    }

    /// <summary>The note a customer pays with: the smallest one that covers the amount.</summary>
    public static int BillFor(int amount)
    {
        foreach (var value in BillValues)
            if (value >= amount) return value;
        return BillValues[BillValues.Length - 1];
    }

    /// <param name="label">What the card says on it: the customer's card, or the clerk's time card on the way in and out.</param>
    public void BeginCard(Func<bool> canPay, Action<string> onNotice, Action onDone, string label = "카드")
    {
        ready = canPay;
        notify = onNotice;
        done = onDone;
        CardSlot.CanApprove = canPay;
        CustomerCard = SpawnCard(label, CustomerPlate.position);
    }

    public void BeginCash(int billValue, Func<bool> canPay, Func<int> due, Action<int> onReceived, Action<string> onNotice, Action onDone)
    {
        ready = canPay;
        amountDue = due;
        received = onReceived;
        notify = onNotice;
        done = onDone;
        CashActive = true;
        CashReceived = 0;
        CustomerBill = SpawnCash(billValue, PaymentKind.CustomerCash, CustomerPlate.position + Vector3.up * SpawnClearance, Quaternion.identity);
        Drawer.Expected = CustomerBill;
        CashChanged?.Invoke();
    }

    /// <summary>Removes everything this payment spawned, including change on the tray, and shuts the till.</summary>
    public void EndPayment()
    {
        Tray.End();
        CashActive = false;
        CashReceived = 0;
        if (Register) Register.Close();
        Drawer.Expected = null;
        CardSlot.CanApprove = null;
        foreach (var prop in spawned)
            if (prop) Destroy(prop.gameObject);
        spawned.Clear();
        StockItems.Clear();
        CustomerCard = null;
        CustomerBill = null;
        done = null;
        amountDue = null;
        NotReadyNotice = null;
        CashChanged?.Invoke();
    }

    string NotReadyText => NotReadyNotice != null ? NotReadyNotice() : "스캔과 봉투 담기를 먼저 끝내세요.";

    void Update()
    {
        // The guide answers "where, and which way" while the customer's card is in the hand. Once the card is in, the lit frame says so.
        CardSlot.ShowGuide(CustomerCard != null && CustomerCard.Grab.isSelected && !CardSlot.CardInside);
        if (!CashActive) return;
        // Rung up and the customer is paying cash: the till lets its drawer go, as a POS does on the cash key.
        if (!Register.IsOpen && (ready == null || ready()))
        {
            Register.Open();
            CashChanged?.Invoke();
        }
        if (!Register.IsOpen) return;
        // Taking a note or coin leaves the next one lying there, so the till never runs out.
        foreach (var slot in Register.Slots) CheckStock(slot);
    }

    void CheckStock(CashRegister.Slot slot)
    {
        if (StockItems.TryGetValue(slot.Value, out var item) && item && !item.Leaving
            && (item.Docked || Vector3.Distance(item.transform.position, slot.Anchor.position) < StockMovedMeters))
            return;
        var fresh = SpawnCash(slot.Value, PaymentKind.Change, slot.Anchor.position, slot.Anchor.rotation);
        fresh.Dock(slot.Anchor);
        StockItems[slot.Value] = fresh;
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
        if (!Register.IsOpen || (ready != null && !ready()))
        {
            if (Audio) Audio.PlayError();
            notify?.Invoke(NotReadyText);
            bill.ReturnHome();
            Drawer.Expected = bill;
            return;
        }
        int value = bill.Value;
        IntoTheTill(bill);
        CustomerBill = null;
        CashReceived += value;
        received?.Invoke(value);
        int change = value - amountDue();
        CashChanged?.Invoke();
        if (change <= 0)
        {
            OnChangeMatched();
            return;
        }
        Tray.Begin(change);
        if (AutoCoins) DispenseCoins(change % 1000);
    }

    /// <summary>Lays the coin part of the change on the tray, side by side so none lands in another: the same coins as the till's.</summary>
    void DispenseCoins(int amount)
    {
        const float spacing = 0.07f;
        CoinsDispensed = 0;
        var coins = new List<CashLook>();
        foreach (var look in Cash)
            if (look.Coin) coins.Add(look);
        coins.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var look in coins)
        {
            for (; amount >= look.Value; amount -= look.Value, CoinsDispensed++)
            {
                var offset = new Vector3((CoinsDispensed % 3 - 1) * spacing, SpawnClearance, CoinsDispensed / 3 % 2 == 0 ? 0.035f : -0.035f);
                SpawnCash(look.Value, PaymentKind.Change, new Vector3(Tray.transform.position.x, CustomerPlate.position.y, Tray.transform.position.z) + offset, Quaternion.identity);
            }
        }
    }

    void OnChangeReturned(PaymentProp piece)
    {
        if (Register.IsOpen) IntoTheTill(piece);
    }

    /// <summary>Slides a note or coin into its own slot. Without a slot for it, it is just taken.</summary>
    void IntoTheTill(PaymentProp piece)
    {
        var slot = Register.SlotFor(piece.Value);
        if (slot != null) piece.SlideInto(slot);
        else Destroy(piece.gameObject);
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
        CashChanged?.Invoke();
    }

    PaymentProp SpawnCard(string label, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Customer Card";
        go.transform.SetPositionAndRotation(position + Vector3.up * SpawnClearance, Quaternion.identity);
        go.transform.localScale = CardSize;
        go.GetComponent<Renderer>().sharedMaterial = CardMaterial;
        var prop = MakeProp(go, PaymentKind.Card, 0, false);

        // A label panel kept flat above the card. The parent's scale is undone so its pixels stay readable.
        var panel = WorldUi.CreatePanel(go.transform, "Label", position, Quaternion.Euler(90f, 180f, 0f), new Vector2(200f, 60f), 0.0005f, new Color(0f, 0f, 0f, 0f));
        panel.localScale = new Vector3(0.0005f / CardSize.x, 0.0005f / CardSize.z, 1f);
        panel.localPosition = new Vector3(0f, 0.51f, 0f);
        var text = WorldUi.AddText(panel, 0f, 0f, 200f, 60f, 34, TextAnchor.MiddleCenter);
        text.text = label;
        text.color = Color.black;
        return prop;
    }

    /// <summary>A note or a coin, printed as itself: what it is worth is read off it, as off real money.</summary>
    PaymentProp SpawnCash(int value, PaymentKind kind, Vector3 position, Quaternion rotation)
    {
        var look = LookFor(value);
        float bodyHeight = look.Coin ? CoinBody : NoteBody;
        var go = new GameObject((kind == PaymentKind.CustomerCash ? "Customer " : "Change ") + value);
        go.transform.SetPositionAndRotation(position, rotation);
        go.AddComponent<BoxCollider>().size = new Vector3(look.Size.x, bodyHeight, look.Size.z);

        // Drawn at the bottom of the body, so it is seen lying on what it rests on.
        var face = new GameObject("Face", typeof(MeshFilter), typeof(MeshRenderer));
        face.transform.SetParent(go.transform, false);
        face.transform.localPosition = new Vector3(0f, (look.Size.y - bodyHeight) * 0.5f, 0f);
        face.transform.localScale = look.Size;
        face.GetComponent<MeshFilter>().sharedMesh = look.Coin ? CoinMesh : NoteMesh;
        var renderer = face.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = look.Material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return MakeProp(go, kind, value, look.Coin);
    }

    CashLook LookFor(int value)
    {
        foreach (var look in Cash)
            if (look.Value == value) return look;
        throw new InvalidOperationException($"The payment desk has no look for {value} won.");
    }

    PaymentProp MakeProp(GameObject go, PaymentKind kind, int value, bool coin)
    {
        go.layer = LayerMask.NameToLayer("Interactable");
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
        prop.Kind = kind;
        prop.IsCoin = coin;
        prop.Audio = Audio;
        spawned.Add(prop);
        return prop;
    }
}
