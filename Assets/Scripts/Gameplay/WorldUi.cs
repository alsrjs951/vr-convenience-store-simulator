using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Builds the world-space panels. Three looks, so a thing can be told by what it is (2026-11-10):
/// the store's signs are Dong-A navy (Pantone 282C); what the game says to the player floats on a plain dark glass that is
/// nobody's brand; the store's own screens (the POS, the order screen) are light, ruled and square like till software.
/// The type is Pretendard (Assets/Resources/Fonts, SIL OFL), the family the store's printed surfaces use too
/// (scripts/windows/make_store_signage.py).
/// </summary>
public static class WorldUi
{
    /// <summary>Store colors. Navy and grey are Dong-A University's 282C and 429C; the rest are UI states.</summary>
    public static class Palette
    {
        public static readonly Color Navy = new Color(0.016f, 0.118f, 0.259f);
        /// <summary>The dark glass behind what the game tells the player.</summary>
        public static readonly Color Panel = new Color(0.07f, 0.08f, 0.095f, 0.9f);
        public static readonly Color PanelSoft = new Color(0.07f, 0.08f, 0.095f, 0.72f);
        /// <summary>The store's screens: the page, what is written on it, its rules and its shaded bands.</summary>
        public static readonly Color Paper = new Color(0.9f, 0.91f, 0.925f);
        public static readonly Color Ink = new Color(0.09f, 0.11f, 0.15f);
        public static readonly Color InkDim = new Color(0.09f, 0.11f, 0.15f, 0.58f);
        public static readonly Color Rule = new Color(0.09f, 0.11f, 0.15f, 0.2f);
        public static readonly Color Band = new Color(0.8f, 0.825f, 0.855f);
        public static readonly Color Accent = new Color(0.24f, 0.49f, 0.86f);
        public static readonly Color Grey = new Color(0.635f, 0.667f, 0.678f);
        public static readonly Color Text = Color.white;
        public static readonly Color TextDim = new Color(0.78f, 0.84f, 0.92f);
        public static readonly Color Good = new Color(0.32f, 0.82f, 0.5f);
        public static readonly Color Warn = new Color(1f, 0.74f, 0.22f);
        public static readonly Color Bad = new Color(0.95f, 0.33f, 0.3f);
    }

    // Small: a card's corner, not a pill.
    const int CornerPixels = 10;

    static Font regular, bold, heavy;
    static Sprite rounded;

    // Each weight is its own font file, so bold text takes the bold file rather than a thickened regular.
    static Font Regular => regular != null ? regular : regular = Face("Pretendard-Regular");
    static Font BoldFace => bold != null ? bold : bold = Face("Pretendard-Bold");
    static Font HeavyFace => heavy != null ? heavy : heavy = Face("Pretendard-ExtraBold");

    static Font Face(string name)
    {
        var face = Resources.Load<Font>("Fonts/" + name);
        if (face != null) return face;
        Debug.LogWarning($"[UI] Font {name} is missing from Resources/Fonts; using a Windows font.");
        return Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Arial" }, 40);
    }

    /// <summary>Bold text: panel titles, buttons, amounts.</summary>
    public static void Bold(Text text)
    {
        text.font = BoldFace;
        text.fontStyle = FontStyle.Normal;
    }

    /// <summary>The heaviest weight, for the store's signs.</summary>
    public static void Heavy(Text text)
    {
        text.font = HeavyFace;
        text.fontStyle = FontStyle.Normal;
    }

    /// <summary>A white rounded square, sliced so the corners keep their size on any panel.</summary>
    public static Sprite Rounded
    {
        get
        {
            if (rounded != null) return rounded;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            float r = CornerPixels;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, size - r), cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d + 0.5f) * 255));
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            rounded = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(CornerPixels + 2, CornerPixels + 2, CornerPixels + 2, CornerPixels + 2));
            return rounded;
        }
    }

    public static RectTransform CreatePanel(Transform parent, string name, Vector3 position, Quaternion rotation, Vector2 sizePixels, float metersPerPixel, Color background, bool interactive = false)
    {
        var go = new GameObject(name, typeof(Canvas));
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = Vector3.one * metersPerPixel;
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = sizePixels;
        if (interactive) go.AddComponent<TrackedDeviceGraphicRaycaster>();
        var image = go.AddComponent<Image>();
        image.color = background;
        image.raycastTarget = interactive;
        image.sprite = Rounded;
        image.type = Image.Type.Sliced;
        return rect;
    }

    /// <summary>Square corners: a screen's page, or a band or rule on it.</summary>
    public static Image Square(Image image)
    {
        image.sprite = null;
        image.type = Image.Type.Simple;
        return image;
    }

    /// <summary>Text anchored to a pixel rectangle measured from the panel's top-left corner.</summary>
    public static Text AddText(RectTransform panel, float x, float y, float width, float height, int fontSize, TextAnchor anchor = TextAnchor.UpperLeft)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(panel, false);
        Place((RectTransform)go.transform, x, y, width, height);
        var text = go.AddComponent<Text>();
        text.font = Regular;
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = Palette.Text;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.lineSpacing = 1.1f;
        return text;
    }

    public static Button AddButton(RectTransform panel, float x, float y, float width, float height, string label, int fontSize)
    {
        var image = AddBox(panel, x, y, width, height, Palette.Accent);
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var text = AddText((RectTransform)image.transform, 0f, 0f, width, height, fontSize, TextAnchor.MiddleCenter);
        Bold(text);
        text.text = label;
        return button;
    }

    /// <summary>A rounded colored rectangle, for bars, chips and dividers.</summary>
    public static Image AddBox(RectTransform panel, float x, float y, float width, float height, Color color)
    {
        var go = new GameObject("Box", typeof(RectTransform));
        go.transform.SetParent(panel, false);
        Place((RectTransform)go.transform, x, y, width, height);
        var image = go.AddComponent<Image>();
        image.sprite = Rounded;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = Mathf.Max(1f, CornerPixels * 2f / Mathf.Max(1f, Mathf.Min(width, height)));
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>Product names without the development suffix the catalog still carries.</summary>
    public static string Clean(string name) => string.IsNullOrEmpty(name) ? "" : name.Replace(" (개발용)", "").Replace("(개발용)", "").Trim();

    static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }
}
