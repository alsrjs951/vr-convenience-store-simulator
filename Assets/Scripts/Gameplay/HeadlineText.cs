using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws a guide text as a headline and what follows it: the first line bold, the rest lighter and a little smaller, the
/// two centred as one block. The text itself stays where it was. Everything that writes or reads it (the tutorials, the
/// day's messages, the checks) goes on using the same Text; that Text is just no longer what is drawn.
/// </summary>
public class HeadlineText : MonoBehaviour
{
    const float BodyScale = 0.82f, HeadlineLine = 1.3f, Gap = 6f;

    Text source, headline, body;
    string shownText;
    int shownSize = -1;
    Vector2 shownRect;

    public static HeadlineText On(Text source)
    {
        var view = source.gameObject.AddComponent<HeadlineText>();
        view.source = source;
        source.enabled = false;
        var rect = (RectTransform)source.transform;
        view.headline = WorldUi.AddText(rect, 0f, 0f, 10f, 10f, source.fontSize, TextAnchor.MiddleCenter);
        WorldUi.Bold(view.headline);
        view.headline.horizontalOverflow = HorizontalWrapMode.Overflow;
        view.body = WorldUi.AddText(rect, 0f, 0f, 10f, 10f, source.fontSize, TextAnchor.UpperCenter);
        view.body.color = WorldUi.Palette.TextDim;
        return view;
    }

    void LateUpdate()
    {
        var size = ((RectTransform)transform).rect.size;
        // The same string object means the same words: this runs every frame for every guide.
        if (ReferenceEquals(source.text, shownText) && source.fontSize == shownSize && size == shownRect) return;
        shownText = source.text;
        shownSize = source.fontSize;
        shownRect = size;
        Lay(shownText ?? "", size);
    }

    void Lay(string text, Vector2 size)
    {
        int cut = text.IndexOf('\n');
        headline.text = cut < 0 ? text : text.Substring(0, cut);
        body.text = cut < 0 ? "" : text.Substring(cut + 1);
        headline.fontSize = shownSize;
        body.fontSize = Mathf.RoundToInt(shownSize * BodyScale);

        float headlineHeight = shownSize * HeadlineLine;
        var bodyRect = body.rectTransform;
        bodyRect.sizeDelta = new Vector2(size.x, 10f);
        float bodyHeight = body.text.Length > 0 ? body.preferredHeight : 0f;
        float block = headlineHeight + (bodyHeight > 0f ? Gap + bodyHeight : 0f);
        float top = Mathf.Max(0f, (size.y - block) * 0.5f);
        headline.rectTransform.anchoredPosition = new Vector2(0f, -top);
        headline.rectTransform.sizeDelta = new Vector2(size.x, headlineHeight);
        bodyRect.anchoredPosition = new Vector2(0f, -(top + headlineHeight + Gap));
        bodyRect.sizeDelta = new Vector2(size.x, Mathf.Max(bodyHeight, 10f));
    }
}
