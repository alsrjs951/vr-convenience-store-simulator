using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// POS monitor (doc 5.3, 5.5), laid out like till software: a header bar, a ruled table of what was scanned with a cancel
/// button per row, the total with what was received and the change, and a bar at the foot for notices.
/// </summary>
public class POSScreen : MonoBehaviour
{
    const int MaxRows = 6;
    const float RowHeight = 58f, RowsTop = 100f;
    const float MetersPerPixel = 0.0007f;
    static readonly Vector2 Size = new Vector2(860f, 660f);
    static readonly Color NoticeBand = new Color(1f, 0.82f, 0.36f);

    public POSController Pos;

    /// <summary>The cancel button of each row, exposed so tests can aim at the real control.</summary>
    public readonly List<Button> CancelButtons = new List<Button>();

    readonly List<Text> rows = new List<Text>();
    readonly List<Text> quantities = new List<Text>();
    readonly List<Text> prices = new List<Text>();
    Text total;
    Text change;
    Text notice;
    Image noticeBand;

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
            var button = WorldUi.AddButton(panel, 756f, y + 8f, 74f, RowHeight - 16f, "−", 36);
            button.GetComponent<Image>().color = new Color(0.78f, 0.24f, 0.24f);
            int row = i;
            button.onClick.AddListener(() => CancelRow(row));
            CancelButtons.Add(button);
        }

        float foot = RowsTop + MaxRows * RowHeight;
        WorldUi.Square(WorldUi.AddBox(panel, 0f, foot, Size.x, 2f, WorldUi.Palette.Ink));
        var totalLabel = WorldUi.AddText(panel, 30f, foot + 14f, 200f, 70f, 30, TextAnchor.MiddleLeft);
        totalLabel.text = "합계";
        totalLabel.color = WorldUi.Palette.InkDim;
        total = WorldUi.AddText(panel, 230f, foot + 14f, 600f, 70f, 56, TextAnchor.MiddleRight);
        total.color = WorldUi.Palette.Ink;
        WorldUi.Bold(total);
        change = WorldUi.AddText(panel, 30f, foot + 88f, 800f, 44f, 28, TextAnchor.MiddleRight);
        change.color = WorldUi.Palette.InkDim;

        noticeBand = WorldUi.Square(WorldUi.AddBox(panel, 0f, Size.y - 56f, Size.x, 56f, NoticeBand));
        notice = WorldUi.AddText(panel, 30f, Size.y - 56f, 800f, 56f, 28, TextAnchor.MiddleLeft);
        notice.color = WorldUi.Palette.Ink;
        WorldUi.Bold(notice);
        Pos.Changed += Refresh;
        Refresh();
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
        // With nobody at the counter the till still shows a total, as a till does.
        total.text = $"{(Pos.Current == null ? 0 : Pos.Total):N0}원";
        total.color = Pos.Current == null ? WorldUi.Palette.InkDim : WorldUi.Palette.Ink;
        change.text = Pos.Current != null && Pos.Received > 0 ? $"받은 돈 {Pos.Received:N0}  ·  거스름 {Pos.ChangeDue:N0}" : "";
        notice.text = WorldUi.Clean(Pos.Notice);
        noticeBand.enabled = notice.text.Length > 0;
    }
}
