using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>POS monitor: scanned rows with [-] buttons, total, change and notices (doc 5.3, 5.5).</summary>
public class POSScreen : MonoBehaviour
{
    const int MaxRows = 6;
    const float RowHeight = 62f;
    const float MetersPerPixel = 0.0007f;
    static readonly Vector2 Size = new Vector2(860f, 660f);

    public POSController Pos;

    /// <summary>The [-] button of each row, exposed so tests can aim at the real control.</summary>
    public readonly List<Button> CancelButtons = new List<Button>();

    readonly List<Text> rows = new List<Text>();
    readonly List<Text> prices = new List<Text>();
    Text total;
    Text change;
    Text notice;

    void Start()
    {
        var panel = WorldUi.CreatePanel(transform, "POS Panel", transform.position, transform.rotation, Size, MetersPerPixel, new Color(0.05f, 0.07f, 0.1f), interactive: true);
        var header = WorldUi.AddText(panel, 30f, 18f, 800f, 44f, 28);
        header.text = "동아편의점";
        header.color = WorldUi.Palette.Grey;
        header.fontStyle = FontStyle.Bold;
        WorldUi.AddBox(panel, 30f, 70f, 800f, 3f, new Color(1f, 1f, 1f, 0.12f));
        for (int i = 0; i < MaxRows; i++)
        {
            float y = 84f + i * RowHeight;
            rows.Add(WorldUi.AddText(panel, 30f, y + 8f, 500f, RowHeight - 8f, 34));
            var price = WorldUi.AddText(panel, 520f, y + 8f, 210f, RowHeight - 8f, 34, TextAnchor.UpperRight);
            prices.Add(price);
            var button = WorldUi.AddButton(panel, 756f, y + 4f, 74f, RowHeight - 12f, "−", 40);
            button.GetComponent<Image>().color = new Color(0.38f, 0.16f, 0.18f);
            int row = i;
            button.onClick.AddListener(() => CancelRow(row));
            CancelButtons.Add(button);
        }
        WorldUi.AddBox(panel, 30f, 462f, 800f, 3f, new Color(1f, 1f, 1f, 0.12f));
        total = WorldUi.AddText(panel, 30f, 476f, 800f, 70f, 54, TextAnchor.UpperRight);
        total.fontStyle = FontStyle.Bold;
        change = WorldUi.AddText(panel, 30f, 548f, 800f, 44f, 30, TextAnchor.UpperRight);
        change.color = WorldUi.Palette.TextDim;
        notice = WorldUi.AddText(panel, 30f, 600f, 800f, 50f, 30);
        notice.color = WorldUi.Palette.Warn;
        Pos.Changed += Refresh;
        Refresh();
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
            rows[i].text = used ? $"{WorldUi.Clean(Pos.Lines[i].Data.Name)}  ×{Pos.Lines[i].Scanned}" : "";
            prices[i].text = used ? $"{Pos.Lines[i].Data.Price * Pos.Lines[i].Scanned:N0}" : "";
        }
        total.text = Pos.Current == null ? "" : $"{Pos.Total:N0}원";
        change.text = Pos.Current != null && Pos.Received > 0 ? $"받은 돈 {Pos.Received:N0} · 거스름 {Pos.ChangeDue:N0}" : "";
        notice.text = WorldUi.Clean(Pos.Notice);
    }
}
