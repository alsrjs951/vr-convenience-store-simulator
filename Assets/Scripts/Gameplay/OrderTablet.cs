using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The store's ordering terminal (docs/p2/해금_성장_기획_2026-11-10.md): between days the player spends the budget on
/// products for the empty shelves and on equipment, which are in the store the next morning. It is one of the store's own
/// screens, so it is laid out like the POS: a navy header, a light ruled page, square corners. Pressed with the ray and
/// trigger or a fingertip, like the POS [-]. The monitor it is on stands at the end of the counter all day (the scene
/// builder makes it); during a shift its screen is off, which is this panel hidden.
/// </summary>
public class OrderTablet : MonoBehaviour
{
    public const int Rows = 9;
    const float RowHeight = 56f, RowsTop = 108f;
    public const float MetersPerPixel = 0.0008f;
    public static readonly Vector2 Size = new Vector2(900f, 668f);
    static readonly Color NoticeBand = new Color(1f, 0.82f, 0.36f);

    public bool Shown => panel != null && panel.gameObject.activeSelf;
    public UnlockKind Tab { get; private set; } = UnlockKind.Product;
    public Button ProductsTab { get; private set; }
    public Button EquipmentTab { get; private set; }
    /// <summary>One per row; a row with nothing to order hides its button.</summary>
    public readonly List<Button> BuyButtons = new List<Button>();
    public string BudgetText => budget != null ? budget.text : "";
    public string SummaryText => summary != null ? summary.text : "";
    public string NoticeText => notice != null ? notice.text : "";
    public string RowText(int row) => row < names.Count ? $"{names[row].text}|{prices[row].text}|{states[row].text}" : "";
    /// <summary>Something was ordered. The progress is already charged and saved.</summary>
    public event Action<UnlockItem> Bought;

    RectTransform panel;
    Text budget, summary, notice;
    Image noticeBand;
    readonly List<Text> names = new List<Text>(), notes = new List<Text>(), prices = new List<Text>(), states = new List<Text>();
    readonly List<UnlockItem> shown = new List<UnlockItem>();
    StoreProgress progress;
    List<UnlockItem> items = new List<UnlockItem>();
    int openingProducts;
    string message = "";

    void Awake()
    {
        panel = WorldUi.CreatePanel(transform, "Order Tablet Panel", transform.position, transform.rotation, Size, MetersPerPixel, WorldUi.Palette.Paper, interactive: true);
        WorldUi.Square(panel.GetComponent<Image>());

        WorldUi.Square(WorldUi.AddBox(panel, 0f, 0f, Size.x, 56f, WorldUi.Palette.Navy));
        var header = WorldUi.AddText(panel, 30f, 0f, 400f, 56f, 28, TextAnchor.MiddleLeft);
        header.text = "동아편의점  발주";
        WorldUi.Bold(header);
        summary = WorldUi.AddText(panel, 430f, 0f, 440f, 56f, 24, TextAnchor.MiddleRight);
        summary.color = WorldUi.Palette.TextDim;

        WorldUi.Square(WorldUi.AddBox(panel, 0f, 56f, Size.x, 52f, WorldUi.Palette.Band));
        ProductsTab = Press(WorldUi.AddButton(panel, 30f, 62f, 140f, 40f, "상품", 26));
        EquipmentTab = Press(WorldUi.AddButton(panel, 180f, 62f, 140f, 40f, "설비", 26));
        ProductsTab.onClick.AddListener(() => Show(UnlockKind.Product));
        EquipmentTab.onClick.AddListener(() => Show(UnlockKind.Equipment));
        var budgetLabel = WorldUi.AddText(panel, 440f, 56f, 160f, 52f, 24, TextAnchor.MiddleRight);
        budgetLabel.text = "발주 예산";
        budgetLabel.color = WorldUi.Palette.InkDim;
        budget = WorldUi.AddText(panel, 610f, 56f, 260f, 52f, 34, TextAnchor.MiddleRight);
        budget.color = WorldUi.Palette.Ink;
        WorldUi.Bold(budget);

        for (int i = 0; i < Rows; i++)
        {
            float y = RowsTop + i * RowHeight;
            var name = WorldUi.AddText(panel, 30f, y, 310f, RowHeight, 28, TextAnchor.MiddleLeft);
            name.color = WorldUi.Palette.Ink;
            name.horizontalOverflow = HorizontalWrapMode.Overflow;
            names.Add(name);
            var note = WorldUi.AddText(panel, 300f, y, 340f, RowHeight, 21, TextAnchor.MiddleLeft);
            note.color = WorldUi.Palette.InkDim;
            note.horizontalOverflow = HorizontalWrapMode.Overflow;
            notes.Add(note);
            var price = WorldUi.AddText(panel, 620f, y, 130f, RowHeight, 28, TextAnchor.MiddleRight);
            price.color = WorldUi.Palette.Ink;
            prices.Add(price);
            var state = WorldUi.AddText(panel, 762f, y, 116f, RowHeight, 23, TextAnchor.MiddleCenter);
            state.color = WorldUi.Palette.InkDim;
            states.Add(state);
            WorldUi.Square(WorldUi.AddBox(panel, 30f, y + RowHeight - 1f, 840f, 1f, WorldUi.Palette.Rule));
            var button = Press(WorldUi.AddButton(panel, 770f, y + 8f, 100f, RowHeight - 16f, "발주", 26));
            int row = i;
            button.onClick.AddListener(() => Buy(row));
            BuyButtons.Add(button);
        }

        noticeBand = WorldUi.Square(WorldUi.AddBox(panel, 0f, Size.y - 56f, Size.x, 56f, NoticeBand));
        notice = WorldUi.AddText(panel, 30f, Size.y - 56f, 840f, 56f, 26, TextAnchor.MiddleLeft);
        notice.color = WorldUi.Palette.Ink;
        WorldUi.Bold(notice);
        Close();
    }

    /// <summary>Tints multiply the color, so the resting state sits below full: pointing at a button has to be seen on Quest 2.</summary>
    static Button Press(Button button)
    {
        var colors = button.colors;
        colors.normalColor = new Color(0.86f, 0.86f, 0.86f);
        colors.highlightedColor = Color.white;
        colors.selectedColor = new Color(0.86f, 0.86f, 0.86f);
        colors.pressedColor = new Color(0.6f, 0.6f, 0.6f);
        colors.disabledColor = new Color(0.86f, 0.86f, 0.86f, 0.35f);
        button.colors = colors;
        return button;
    }

    /// <param name="openingAssortment">How many products the store sells without any order, for the "구색 n/m" line.</param>
    public void Open(StoreProgress storeProgress, List<UnlockItem> unlocks, int openingAssortment, string firstNotice = "")
    {
        progress = storeProgress;
        items = unlocks ?? new List<UnlockItem>();
        openingProducts = openingAssortment;
        message = firstNotice;
        panel.gameObject.SetActive(true);
        Show(UnlockKind.Product);
    }

    public void Close()
    {
        if (panel != null) panel.gameObject.SetActive(false);
    }

    void Show(UnlockKind tab)
    {
        Tab = tab;
        Refresh();
    }

    void Buy(int row)
    {
        if (progress == null || row >= shown.Count) return;
        var item = shown[row];
        if (!progress.Buy(item))
        {
            message = progress.Ordered(item.Id) ? "" : $"예산이 {item.Price - progress.budget:N0}원 모자랍니다";
            Refresh();
            return;
        }
        progress.Save();
        message = items.TrueForAll(other => progress.Ordered(other.Id)) ? "더 들일 것이 없습니다  ·  들어오는 대로 진열하세요"
            : item.Kind == UnlockKind.Product ? $"{item.Name}  ·  박스로 들어옵니다. 진열하면 팔립니다"
            : $"{item.Name}  ·  내일 아침에 들어옵니다";
        Debug.Log($"[Tablet] Ordered {item.Id} for {item.Price}; budget={progress.budget}");
        Refresh();
        Bought?.Invoke(item);
    }

    public void Refresh()
    {
        if (progress == null) return;
        int products = 0, productsIn = 0, equipment = 0, equipmentIn = 0;
        shown.Clear();
        foreach (var item in items)
        {
            bool isProduct = item.Kind == UnlockKind.Product;
            if (isProduct) products++; else equipment++;
            if (progress.Ordered(item.Id))
            {
                if (isProduct) productsIn++; else equipmentIn++;
            }
            if (item.Kind == Tab && shown.Count < Rows) shown.Add(item);
        }
        budget.text = $"{progress.budget:N0}원";
        summary.text = $"구색 {openingProducts + productsIn}/{openingProducts + products}  ·  설비 {equipmentIn}/{equipment}";
        Mark(ProductsTab, Tab == UnlockKind.Product);
        Mark(EquipmentTab, Tab == UnlockKind.Equipment);

        for (int i = 0; i < Rows; i++)
        {
            bool used = i < shown.Count;
            var item = used ? shown[i] : null;
            bool have = used && progress.Has(item.Id), coming = used && !have && progress.Ordered(item.Id);
            names[i].text = used ? item.Name : "";
            notes[i].text = used ? item.Note : "";
            prices[i].text = used ? $"{item.Price:N0}" : "";
            // A product is in the store when its box has come, and on sale when it stands on its shelf. Boxes come two a day.
            bool isProduct = used && item.Kind == UnlockKind.Product;
            states[i].text = !isProduct ? (have ? "들어옴" : coming ? "내일 도착" : "")
                : have ? (progress.Shelved(item.Id) ? "판매 중" : "진열 대기") : coming ? "입고 예정" : "";
            var faded = have || coming ? WorldUi.Palette.InkDim : WorldUi.Palette.Ink;
            names[i].color = prices[i].color = faded;
            BuyButtons[i].gameObject.SetActive(used && !have && !coming);
            BuyButtons[i].interactable = used && progress.budget >= item.Price;
        }
        if (message.Length == 0 && progress.completed) message = "매장을 다 채웠습니다";
        notice.text = message;
        noticeBand.enabled = message.Length > 0;
    }

    static void Mark(Button tab, bool current)
    {
        tab.GetComponent<Image>().color = current ? WorldUi.Palette.Navy : WorldUi.Palette.Grey;
    }
}
