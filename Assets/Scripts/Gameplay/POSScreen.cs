using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// POS monitor (doc 5.3, 5.5), laid out like till software: a header bar, a ruled table of what was scanned with a cancel
/// button per row, the total, and a bar at the foot for notices. During a cash sale a strip under the total shows what the
/// customer gave and what is owed back, as a till does. Counting the change out is the clerk's (2026-11-13): only in a
/// lesson (PaymentDesk.Guided) is there a third amount, what lies on the change tray so far.
/// </summary>
public class POSScreen : MonoBehaviour
{
    const int MaxRows = 6;
    const float RowHeight = 50f, RowsTop = 100f;
    const float MetersPerPixel = 0.0007f;
    const float CashTop = 478f, CashHeight = 118f, CashLeft = 30f, CashWidth = 800f;
    static readonly Vector2 Size = new Vector2(860f, 660f);
    static readonly Color NoticeBand = new Color(1f, 0.82f, 0.36f);
    // The cell for what lies on the tray: plain while it is short, green when it is right, red when it is too much.
    static readonly Color LaidExact = new Color(0.72f, 0.9f, 0.77f), LaidOver = new Color(0.97f, 0.78f, 0.75f);
    static readonly Color LaidExactInk = new Color(0.05f, 0.36f, 0.16f), LaidOverInk = new Color(0.62f, 0.1f, 0.08f);

    public POSController Pos;
    public PaymentDesk Desk;

    /// <summary>The cancel button of each row, exposed so tests can aim at the real control.</summary>
    public readonly List<Button> CancelButtons = new List<Button>();

    /// <summary>What the cash strip shows, for the checks: empty while it is hidden.</summary>
    public string ReceivedText => Shown(cash) ? received.text : Shown(plain) ? plainReceived.text : "";
    public string ChangeText => Shown(cash) ? change.text : Shown(plain) ? plainChange.text : "";
    /// <summary>Empty with a customer too: the tray is counted on the screen only in a lesson.</summary>
    public string LaidText => Shown(cash) ? laid.text : "";
    static bool Shown(RectTransform strip) => strip != null && strip.gameObject.activeSelf;
    public string TotalText => total != null ? total.text : "";

    readonly List<Text> rows = new List<Text>();
    readonly List<Text> quantities = new List<Text>();
    readonly List<Text> prices = new List<Text>();
    Text total;
    Text notice;
    Image noticeBand;
    RectTransform cash, plain;
    Text received, change, laid, laidLabel, plainReceived, plainChange;
    Image laidCell;

    void Start()
    {
        var panel = WorldUi.CreatePanel(transform, "POS Panel", transform.position, transform.rotation, Size, MetersPerPixel, WorldUi.Palette.Paper, interactive: true);
        WorldUi.Square(panel.GetComponent<Image>());

        WorldUi.Square(WorldUi.AddBox(panel, 0f, 0f, Size.x, 56f, WorldUi.Palette.Navy));
        var header = WorldUi.AddText(panel, 30f, 0f, 400f, 56f, 28, TextAnchor.MiddleLeft);
        header.text = "동아편의점";
        WorldUi.Bold(header);
        var station = WorldUi.AddText(panel, 430f, 0f, 400f, 56f, 24, TextAnchor.MiddleRight);
        station.text = "POS 01  ·  판매";
        station.color = WorldUi.Palette.TextDim;

        WorldUi.Square(WorldUi.AddBox(panel, 0f, 56f, Size.x, 40f, WorldUi.Palette.Band));
        Column(panel, 30f, 400f, "상품명", TextAnchor.MiddleLeft);
        Column(panel, 470f, 90f, "수량", TextAnchor.MiddleCenter);
        Column(panel, 560f, 170f, "금액", TextAnchor.MiddleRight);

        for (int i = 0; i < MaxRows; i++)
        {
            float y = RowsTop + i * RowHeight;
            var name = WorldUi.AddText(panel, 30f, y, 440f, RowHeight, 30, TextAnchor.MiddleLeft);
            name.color = WorldUi.Palette.Ink;
            name.horizontalOverflow = HorizontalWrapMode.Overflow;
            rows.Add(name);
            var quantity = WorldUi.AddText(panel, 470f, y, 90f, RowHeight, 30, TextAnchor.MiddleCenter);
            quantity.color = WorldUi.Palette.Ink;
            quantities.Add(quantity);
            var price = WorldUi.AddText(panel, 560f, y, 170f, RowHeight, 30, TextAnchor.MiddleRight);
            price.color = WorldUi.Palette.Ink;
            prices.Add(price);
            WorldUi.Square(WorldUi.AddBox(panel, 30f, y + RowHeight - 1f, 800f, 1f, WorldUi.Palette.Rule));
            var button = WorldUi.AddButton(panel, 756f, y + 6f, 74f, RowHeight - 12f, "−", 36);
            button.GetComponent<Image>().color = new Color(0.78f, 0.24f, 0.24f);
            int row = i;
            button.onClick.AddListener(() => CancelRow(row));
            CancelButtons.Add(button);
        }

        float foot = RowsTop + MaxRows * RowHeight;
        WorldUi.Square(WorldUi.AddBox(panel, 0f, foot, Size.x, 2f, WorldUi.Palette.Ink));
        var totalLabel = WorldUi.AddText(panel, 30f, foot + 8f, 200f, 64f, 30, TextAnchor.MiddleLeft);
        totalLabel.text = "합계";
        totalLabel.color = WorldUi.Palette.InkDim;
        total = WorldUi.AddText(panel, 230f, foot + 8f, 600f, 64f, 54, TextAnchor.MiddleRight);
        total.color = WorldUi.Palette.Ink;
        WorldUi.Bold(total);

        BuildCashStrip(panel);

        noticeBand = WorldUi.Square(WorldUi.AddBox(panel, 0f, Size.y - 56f, Size.x, 56f, NoticeBand));
        notice = WorldUi.AddText(panel, 30f, Size.y - 56f, 800f, 56f, 28, TextAnchor.MiddleLeft);
        notice.color = WorldUi.Palette.Ink;
        WorldUi.Bold(notice);
        Pos.Changed += Refresh;
        if (Desk) Desk.CashChanged += Refresh;
        Refresh();
    }

    /// <summary>Three cells side by side: received, change (the one to act on, in the store's navy), laid on the tray.</summary>
    void BuildCashStrip(RectTransform panel)
    {
        cash = WorldUi.AddGroup(panel, CashLeft, CashTop, CashWidth, CashHeight);
        float cell = CashWidth / 3f;
        WorldUi.Square(WorldUi.AddBox(cash, 0f, 0f, cell, CashHeight, WorldUi.Palette.Band));
        WorldUi.Square(WorldUi.AddBox(cash, cell, 0f, cell, CashHeight, WorldUi.Palette.Navy));
        laidCell = WorldUi.Square(WorldUi.AddBox(cash, cell * 2f, 0f, cell, CashHeight, WorldUi.Palette.Band));
        received = Cell(cash, 0f, cell, "받은 돈", WorldUi.Palette.InkDim, WorldUi.Palette.Ink, out _);
        change = Cell(cash, cell, cell, "거스름돈", WorldUi.Palette.TextDim, WorldUi.Palette.Text, out _);
        laid = Cell(cash, cell * 2f, cell, "접시에 놓은 돈", WorldUi.Palette.InkDim, WorldUi.Palette.Ink, out laidLabel);
        cash.gameObject.SetActive(false);

        // With a customer: the two amounts a till shows. What lies on the tray is for the clerk to count.
        plain = WorldUi.AddGroup(panel, CashLeft, CashTop, CashWidth, CashHeight);
        float half = CashWidth / 2f;
        WorldUi.Square(WorldUi.AddBox(plain, 0f, 0f, half, CashHeight, WorldUi.Palette.Band));
        WorldUi.Square(WorldUi.AddBox(plain, half, 0f, half, CashHeight, WorldUi.Palette.Navy));
        plainReceived = Cell(plain, 0f, half, "받은 돈", WorldUi.Palette.InkDim, WorldUi.Palette.Ink, out _);
        plainChange = Cell(plain, half, half, "거스름돈", WorldUi.Palette.TextDim, WorldUi.Palette.Text, out _);
        plain.gameObject.SetActive(false);
    }

    static Text Cell(RectTransform strip, float x, float width, string label, Color labelColor, Color amountColor, out Text labelText)
    {
        labelText = WorldUi.AddText(strip, x + 18f, 10f, width - 36f, 34f, 24, TextAnchor.MiddleLeft);
        labelText.text = label;
        labelText.color = labelColor;
        var amount = WorldUi.AddText(strip, x + 18f, 44f, width - 36f, 66f, 46, TextAnchor.MiddleRight);
        amount.color = amountColor;
        WorldUi.Bold(amount);
        return amount;
    }

    static void Column(RectTransform panel, float x, float width, string label, TextAnchor anchor)
    {
        var text = WorldUi.AddText(panel, x, 56f, width, 40f, 22, anchor);
        text.text = label;
        text.color = WorldUi.Palette.InkDim;
    }

    void OnDestroy()
    {
        if (Pos) Pos.Changed -= Refresh;
        if (Desk) Desk.CashChanged -= Refresh;
    }

    void CancelRow(int row)
    {
        if (row < Pos.Lines.Count) Pos.CancelOne(Pos.Lines[row].Data);
    }

    void Refresh()
    {
        for (int i = 0; i < MaxRows; i++)
        {
            bool used = i < Pos.Lines.Count && Pos.Lines[i].Scanned > 0;
            CancelButtons[i].gameObject.SetActive(used);
            rows[i].text = used ? $"{WorldUi.Clean(Pos.Lines[i].Data.Name)}{(Pos.IsOnePlusLine(Pos.Lines[i]) ? "  1+1" : "")}" : "";
            quantities[i].text = used ? $"{Pos.Lines[i].Scanned}" : "";
            prices[i].text = used ? $"{Pos.LinePrice(Pos.Lines[i]):N0}" : "";
        }
        // With nobody at the counter the till still shows a total, as a till does. In the tutorial's cash sale the manager
        // has rung the amount up, so it is on the screen without any rows.
        bool cashSale = Desk != null && Desk.CashActive;
        bool selling = Pos.Current != null || cashSale;
        total.text = $"{(Pos.Current != null ? Pos.Total : cashSale ? Desk.CashDue : 0):N0}원";
        total.color = selling ? WorldUi.Palette.Ink : WorldUi.Palette.InkDim;
        RefreshCash(cashSale && Desk.Register != null && Desk.Register.IsOpen);
        notice.text = WorldUi.Clean(Pos.Notice);
        noticeBand.enabled = notice.text.Length > 0;
    }

    /// <summary>The strip is there from the moment the drawer opens. Its amounts fill in once the customer's money is in the drawer.</summary>
    void RefreshCash(bool show)
    {
        bool lesson = show && Desk.Guided, customer = show && !Desk.Guided;
        if (cash.gameObject.activeSelf != lesson) cash.gameObject.SetActive(lesson);
        if (plain.gameObject.activeSelf != customer) plain.gameObject.SetActive(customer);
        if (!show) return;
        bool taken = Desk.CashReceived > 0;
        if (customer)
        {
            plainReceived.text = taken ? $"{Desk.CashReceived:N0}" : "–";
            plainChange.text = taken ? $"{Desk.ChangeDue:N0}" : "–";
            return;
        }
        received.text = taken ? $"{Desk.CashReceived:N0}" : "–";
        change.text = taken ? $"{Desk.ChangeDue:N0}" : "–";
        laid.text = taken ? $"{Desk.ChangeLaid:N0}" : "–";
        bool exact = taken && Desk.ChangeLaid == Desk.ChangeDue, over = taken && Desk.ChangeLaid > Desk.ChangeDue;
        laidCell.color = exact ? LaidExact : over ? LaidOver : WorldUi.Palette.Band;
        laid.color = exact ? LaidExactInk : over ? LaidOverInk : WorldUi.Palette.Ink;
        laidLabel.color = exact ? LaidExactInk : over ? LaidOverInk : WorldUi.Palette.InkDim;
    }
}
