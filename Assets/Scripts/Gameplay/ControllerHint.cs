using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A short line right at the controller while a control is new (2026-11-09): which stick, which button, in the words of the
/// fingers rather than the names on a spec sheet. Someone who has never held these looks at their hands when nothing happens.
/// </summary>
public class ControllerHint : MonoBehaviour
{
    const float MetersPerPixel = 0.00026f;
    static readonly Vector2 PanelPixels = new Vector2(580f, 84f);
    const float AboveHandMeters = 0.1f;

    public Transform LeftHand, RightHand;

    public string LeftText => Shown(left) ? leftText.text : "";
    public string RightText => Shown(right) ? rightText.text : "";

    RectTransform left, right;
    Text leftText, rightText;
    Camera eyes;

    void Awake()
    {
        left = Panel(LeftHand, out leftText);
        right = Panel(RightHand, out rightText);
    }

    /// <summary>Above the hand and turned to the eyes: however a beginner holds a controller, the line stays readable.</summary>
    void LateUpdate()
    {
        if (eyes == null) eyes = Camera.main;
        if (eyes == null) return;
        Face(left, LeftHand);
        Face(right, RightHand);
    }

    void Face(RectTransform panel, Transform hand)
    {
        if (!Shown(panel)) return;
        var position = hand.position + Vector3.up * AboveHandMeters;
        panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - eyes.transform.position));
    }

    static bool Shown(RectTransform panel) => panel != null && panel.gameObject.activeSelf;

    RectTransform Panel(Transform hand, out Text text)
    {
        var panel = WorldUi.CreatePanel(hand, "Controller Hint", hand.position, hand.rotation, PanelPixels, MetersPerPixel, WorldUi.Palette.Panel);
        text = WorldUi.AddText(panel, 8f, 0f, PanelPixels.x - 16f, PanelPixels.y, 44, TextAnchor.MiddleCenter);
        text.fontStyle = FontStyle.Bold;
        panel.gameObject.SetActive(false);
        return panel;
    }

    /// <param name="leftLine">Null or empty hides that hand's line.</param>
    public void Show(string leftLine, string rightLine)
    {
        Set(left, leftText, leftLine);
        Set(right, rightText, rightLine);
    }

    public void Hide() => Show(null, null);

    static void Set(RectTransform panel, Text text, string line)
    {
        bool show = !string.IsNullOrEmpty(line);
        if (show) text.text = line;
        panel.gameObject.SetActive(show);
    }
}
