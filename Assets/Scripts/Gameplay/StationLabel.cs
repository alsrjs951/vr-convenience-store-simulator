using UnityEngine;

/// <summary>A small name tag above a counter station, turned toward the clerk's side. Shown during the tutorials only.</summary>
public class StationLabel : MonoBehaviour
{
    static readonly System.Collections.Generic.List<StationLabel> all = new System.Collections.Generic.List<StationLabel>();
    static bool shown = true;

    /// <summary>The tags are on. Statics outlive a scene: the demo's restart switches them on again (GameManager.RestartDemo).</summary>
    public static bool Shown => shown;

    /// <summary>All tags on or off (GameManager: off when the shift starts; the tutorials turn them on).</summary>
    public static void ShowAll(bool show)
    {
        shown = show;
        all.RemoveAll(l => l == null);
        foreach (var label in all)
            if (label.panel != null) label.panel.gameObject.SetActive(show);
    }

    RectTransform panel;

    // Play mode may start without a domain reload; the tags of the last run must not carry over.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        shown = true;
    }

    void OnDestroy() => all.Remove(this);

    const float Height = 0.13f;
    // Behind the station as the clerk sees it, so a product held over the station is not covered.
    const float BehindMeters = 0.1f;
    const float MetersPerPixel = 0.0006f;

    public string Label;
    /// <summary>For things behind the counter: read from the counter side, looking toward the back wall.</summary>
    public bool FacesBackWall;

    void Start()
    {
        float side = FacesBackWall ? -1f : 1f;
        var position = transform.position + new Vector3(0f, Height, BehindMeters * side);
        panel = WorldUi.CreatePanel(transform, "Label", position, Quaternion.Euler(20f, FacesBackWall ? 180f : 0f, 0f), new Vector2(360f, 56f), MetersPerPixel, WorldUi.Palette.PanelSoft);
        WorldUi.AddText(panel, 0f, 0f, 360f, 56f, 30, TextAnchor.MiddleCenter).text = Label;
        all.Add(this);
        panel.gameObject.SetActive(shown);
    }
}
