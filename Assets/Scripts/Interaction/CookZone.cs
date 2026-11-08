using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Day 3's heating requests (doc 6.4): the scanned product an order asks to have heated, kept here for Seconds, becomes
/// IsProcessed, and only then can it be bagged and paid for. Moving it out early starts over.
///
/// The zone looks for the product itself instead of waiting for trigger callbacks: a product resting on the floor of the
/// oven goes to sleep and would stop reporting. It only looks while an order wants this station, a few times a second.
/// </summary>
[RequireComponent(typeof(BoxCollider), typeof(AudioSource))]
public abstract class CookZone : MonoBehaviour
{
    const float SearchInterval = 0.1f;
    const float DoneNoticeSeconds = 3f;
    const float PromptMetersPerPixel = 0.0007f;
    static readonly Vector2 PromptSize = new Vector2(560f, 84f);

    public POSController Pos;
    public GameAudio Audio;
    /// <summary>Plays while working, looped.</summary>
    public AudioClip WorkingLoop;
    /// <summary>Where the one-line prompt hangs. Forward points away from the person reading it.</summary>
    public Transform PromptAnchor;

    protected abstract string Request { get; }
    protected abstract float Seconds { get; }
    /// <summary>The microwave needs the product put down inside; the hot water tap works while the cup is held under it.</summary>
    protected abstract bool MustBeLetGo { get; }
    protected abstract string WaitingPrompt { get; }
    protected abstract string WorkingPrompt { get; }
    protected abstract string DonePrompt { get; }

    protected virtual void ShowWorking(bool working) { }
    protected virtual void OnDone() { }

    public bool IsWorking { get; private set; }
    public ProductPrefab Working { get; private set; }
    public float Elapsed { get; private set; }
    public int Finished { get; private set; }
    public string PromptShown => prompt != null && prompt.activeSelf ? promptText.text : "";

    /// <summary>The order at the counter asks for this station, done or not.</summary>
    protected bool OrderHasRequest => Pos != null && Pos.Current != null && Pos.Current.Special == Request;
    bool Wanted => OrderHasRequest && !Pos.AllCooked;

    BoxCollider zone;
    AudioSource speaker;
    GameObject prompt;
    Text promptText;
    int productLayers;
    float nextSearch;
    float doneUntil = -1f;
    readonly HashSet<ProductPrefab> warned = new HashSet<ProductPrefab>();

    protected virtual void Awake()
    {
        zone = GetComponent<BoxCollider>();
        speaker = GetComponent<AudioSource>();
        speaker.spatialBlend = 1f;
        speaker.playOnAwake = false;
        speaker.loop = true;
        productLayers = LayerMask.GetMask("Interactable");
    }

    void Start()
    {
        if (PromptAnchor == null) return;
        var panel = WorldUi.CreatePanel(PromptAnchor, "Prompt", PromptAnchor.position, PromptAnchor.rotation, PromptSize, PromptMetersPerPixel, WorldUi.Palette.Panel);
        promptText = WorldUi.AddText(panel, 14f, 8f, PromptSize.x - 28f, PromptSize.y - 16f, 38, TextAnchor.MiddleCenter);
        prompt = panel.gameObject;
        prompt.SetActive(false);
    }

    void Update()
    {
        if (IsWorking)
        {
            // Also when the order ended (timeout): its products are destroyed with it.
            if (Working == null || !Wanted || !Inside(Working) || (MustBeLetGo && Working.Grab.isSelected))
            {
                Stop();
                return;
            }
            Elapsed += Time.deltaTime;
            SetPrompt($"{WorkingPrompt} {Mathf.CeilToInt(Mathf.Max(0f, Seconds - Elapsed))}초");
            if (Elapsed >= Seconds) Finish();
            return;
        }

        if (!Wanted)
        {
            warned.Clear();
            SetPrompt(Time.time < doneUntil ? DonePrompt : null);
            return;
        }
        SetPrompt(WaitingPrompt);
        if (Time.time < nextSearch) return;
        nextSearch = Time.time + SearchInterval;
        Search();
    }

    void Search()
    {
        var bounds = zone.bounds;
        foreach (var hit in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, productLayers, QueryTriggerInteraction.Ignore))
        {
            var product = hit.GetComponentInParent<ProductPrefab>();
            if (product == null || hit != product.Proxy || product.IsBagged || product.IsShelved || product.IsProcessed) continue;
            if (!Inside(product) || (MustBeLetGo && product.Grab.isSelected)) continue;
            if (Pos.NeedsCooking(product) && Pos.IsScanned(product))
            {
                Begin(product);
                return;
            }
            Warn(product);
        }
        warned.RemoveWhere(p => p == null || !Inside(p));
    }

    /// <summary>Its middle, so a bag brushing the door does not count; the same test decides whether it is still inside.</summary>
    bool Inside(ProductPrefab product) => zone.bounds.Contains(product.Proxy.bounds.center);

    void Warn(ProductPrefab product)
    {
        if (!warned.Add(product)) return;
        if (Audio) Audio.PlayError();
        Pos.SetNotice(product.Data == Pos.Current.CookTarget ? "스캔한 뒤에 넣으세요." : $"{WorldUi.Clean(Pos.Current.CookTarget.Name)}만 넣으세요.");
    }

    void Begin(ProductPrefab product)
    {
        Working = product;
        IsWorking = true;
        Elapsed = 0f;
        if (WorkingLoop != null)
        {
            speaker.clip = WorkingLoop;
            speaker.Play();
        }
        ShowWorking(true);
    }

    void Stop()
    {
        IsWorking = false;
        Working = null;
        Elapsed = 0f;
        speaker.Stop();
        ShowWorking(false);
    }

    void Finish()
    {
        var product = Working;
        Stop();
        product.IsProcessed = true;
        Finished++;
        doneUntil = Time.time + DoneNoticeSeconds;
        Debug.Log($"[Cook] {Request}: {product.Data.Id} done");
        Pos.MarkCooked(product);
        OnDone();
    }

    void SetPrompt(string text)
    {
        if (prompt == null) return;
        prompt.SetActive(text != null);
        if (text != null && promptText.text != text) promptText.text = text;
    }
}
