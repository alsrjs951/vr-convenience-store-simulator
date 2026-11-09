using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Store signage: the 동아편의점 name, aisle names, the backs of the counter screens. Built at runtime like the other
/// panels, since the UI font is the Windows font and cannot be saved into the scene. The sign is readable when looking
/// along this transform's forward (the canvas faces back toward the viewer).
/// </summary>
public class StoreSign : MonoBehaviour
{
    const float MetersPerPixel = 0.001f;

    public string Title = "";
    public string Subtitle = "";
    public Vector2 SizeMeters = new Vector2(1f, 0.25f);
    public Color Background = new Color(0.016f, 0.118f, 0.259f);
    public Color TextColor = Color.white;
    /// <summary>Title height as a share of the sign's height.</summary>
    public float TitleScale = 0.56f;
    public bool DoubleSided;
    public TextAnchor Alignment = TextAnchor.MiddleCenter;
    /// <summary>How far each face sits toward its viewer from this transform (clears the backing it is mounted on).</summary>
    public float FaceOffset = 0.002f;

    bool built;

    void Start() => BuildNow();

    /// <summary>Also called by the editor's still renders, which do not enter Play.</summary>
    public void BuildNow()
    {
        if (built) return;
        built = true;
        Build(transform.rotation, "Sign Front");
        if (DoubleSided) Build(transform.rotation * Quaternion.Euler(0f, 180f, 0f), "Sign Back");
    }

    void Build(Quaternion rotation, string name)
    {
        // Big signs use coarser pixels so the font stays at a size the dynamic font atlas handles well.
        float metersPerPixel = Mathf.Max(MetersPerPixel, SizeMeters.y / 180f);
        var size = SizeMeters / metersPerPixel;
        var position = transform.position - rotation * Vector3.forward * FaceOffset;
        var panel = WorldUi.CreatePanel(transform, name, position, rotation, size, metersPerPixel, Background);
        float pad = size.y * 0.12f;
        bool two = !string.IsNullOrEmpty(Subtitle);
        float titleHeight = two ? size.y * 0.58f : size.y - pad * 2f;
        var title = WorldUi.AddText(panel, pad * 1.5f, two ? pad * 0.6f : pad, size.x - pad * 3f, titleHeight, Mathf.RoundToInt(size.y * TitleScale), Alignment);
        WorldUi.Heavy(title);
        title.color = TextColor;
        title.text = Title;
        title.verticalOverflow = VerticalWrapMode.Truncate;
        if (!two) return;
        var sub = WorldUi.AddText(panel, pad * 1.5f, pad * 0.6f + titleHeight, size.x - pad * 3f, size.y - titleHeight - pad, Mathf.RoundToInt(size.y * 0.22f), Alignment);
        sub.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0.8f);
        sub.text = Subtitle;
    }
}
