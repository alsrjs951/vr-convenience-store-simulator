using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The left wrist during the shift (game design 13, 2026-11-09): how long the customer at the counter has, how many are
/// waiting, who has left and today's takings. Away at the shelves the counter's order card cannot be seen, so this is what
/// the choice between one more product and going back is made on. It moves with the hand, so it is not a head-locked HUD,
/// and the hand feels a pulse when a customer reaches the counter or starts to hurry.
/// </summary>
public class WristWatch : MonoBehaviour
{
    const float MetersPerPixel = 0.00024f;
    const float RefreshSeconds = 0.1f;
    const float AlertSeconds = 2f;
    const float BarWidth = 480f;

    public GameManager Game;
    public CustomerDirector Director;

    public bool Shown => panel != null && panel.gameObject.activeSelf;
    /// <summary>All the words on it, for the checks.</summary>
    public string Summary => panel == null ? "" : $"{dayText.text} | {waitingText.text} | {customerText.text} | {timeText.text} | {leftText.text} | {salesText.text}";
    public int Pulses { get; private set; }

    RectTransform panel;
    Text dayText, waitingText, customerText, timeText, leftText, salesText;
    Image background, bar;
    XRBaseInputInteractor hand;
    float nextRefresh, alertUntil = -1f;

    void Start()
    {
        panel = WorldUi.CreatePanel(transform, "Wrist Screen", transform.position, transform.rotation, new Vector2(520f, 268f), MetersPerPixel, WorldUi.Palette.Panel);
        background = panel.GetComponent<Image>();
        dayText = WorldUi.AddText(panel, 22f, 12f, 230f, 44f, 36);
        dayText.color = WorldUi.Palette.TextDim;
        waitingText = WorldUi.AddText(panel, 268f, 12f, 230f, 44f, 36, TextAnchor.UpperRight);
        customerText = WorldUi.AddText(panel, 22f, 62f, 290f, 80f, 48, TextAnchor.MiddleLeft);
        WorldUi.Bold(customerText);
        timeText = WorldUi.AddText(panel, 300f, 58f, 198f, 88f, 68, TextAnchor.MiddleRight);
        WorldUi.Bold(timeText);
        WorldUi.AddBox(panel, 20f, 152f, BarWidth, 26f, new Color(1f, 1f, 1f, 0.15f));
        bar = WorldUi.AddBox(panel, 20f, 152f, BarWidth, 26f, WorldUi.Palette.Good);
        bar.type = Image.Type.Filled;
        bar.fillMethod = Image.FillMethod.Horizontal;
        bar.sprite = null;
        WorldUi.AddBox(panel, 20f + BarWidth * CustomerDirector.FastShare - 2f, 146f, 4f, 38f, Color.white);
        leftText = WorldUi.AddText(panel, 22f, 196f, 230f, 56f, 38, TextAnchor.MiddleLeft);
        salesText = WorldUi.AddText(panel, 240f, 196f, 258f, 56f, 38, TextAnchor.MiddleRight);
        panel.gameObject.SetActive(false);

        if (transform.parent != null) hand = transform.parent.GetComponentInChildren<XRBaseInputInteractor>();
        Director.OrderArrived += OnArrived;
        Director.Hurried += OnHurried;
    }

    void OnDestroy()
    {
        if (Director == null) return;
        Director.OrderArrived -= OnArrived;
        Director.Hurried -= OnHurried;
    }

    void OnArrived(Order order, bool firstOfItsKind) => Alert(0.35f, 0.12f);

    void OnHurried() => Alert(0.6f, 0.2f);

    void Alert(float amplitude, float seconds)
    {
        alertUntil = Time.time + AlertSeconds;
        Pulses++;
        if (hand != null) hand.SendHapticImpulse(amplitude, seconds);
    }

    void Update()
    {
        bool show = Game.State == DayState.Shift;
        if (panel.gameObject.activeSelf != show) panel.gameObject.SetActive(show);
        if (!show) return;
        background.color = Time.time < alertUntil && Mathf.Repeat(Time.time, 0.5f) < 0.25f ? new Color(0.62f, 0.44f, 0.06f, 0.96f) : WorldUi.Palette.Panel;
        if (Time.time < nextRefresh) return;
        nextRefresh = Time.time + RefreshSeconds;
        Refresh();
    }

    void Refresh()
    {
        var order = Director.Pos.Current;
        dayText.text = $"{Game.Day}일차";
        waitingText.text = $"대기 {Director.Waiting.Count}명";
        leftText.text = Game.Day > 1 ? $"이탈 {Director.Left}/{Director.LeaveLimit}" : "";
        leftText.color = Director.Left + 1 >= Director.LeaveLimit && Game.Day > 1 ? WorldUi.Palette.Bad : WorldUi.Palette.Text;
        salesText.text = $"{Director.Sales + Director.Tips:N0}원";

        if (order == null)
        {
            customerText.text = Director.AtCounter != null ? "손님 오는 중" : "계산대 비었음";
            timeText.text = "";
            bar.fillAmount = 0f;
            return;
        }
        float share = order.TimeLimitSeconds > 0f ? Mathf.Clamp01(Director.Remaining / order.TimeLimitSeconds) : 0f;
        int seconds = Mathf.CeilToInt(Mathf.Max(0f, Director.Remaining));
        var color = share > CustomerDirector.FastShare ? WorldUi.Palette.Good : share > 0.25f ? WorldUi.Palette.Warn : WorldUi.Palette.Bad;
        customerText.text = $"손님 {Director.CustomerNumber}";
        timeText.text = $"{seconds / 60}:{seconds % 60:00}";
        timeText.color = color;
        bar.color = color;
        bar.fillAmount = share;
    }
}
