using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Game-rule scan (doc 5.3): a product counts when its BarcodeTrigger volume is in one of the scanner's windows and faces
/// it. This is collision logic only. It does not read the barcode image.
///
/// A window is a trigger volume whose up axis points out of its glass. This object's own collider is one (the flat glass
/// in the counter, read on contact); a <see cref="ScanWindow"/> adds another (the upright glass, read in the air in front
/// of it). Like a real scanner, the same product is read once for as long as it stays in sight: it has to be taken out of
/// every window for <see cref="CooldownSeconds"/> before it can be read again, so a hand hovering at the edge of the
/// reading range does not ring the item up twice.
/// </summary>
[RequireComponent(typeof(Collider), typeof(AudioSource))]
public class ScannerZone : MonoBehaviour
{
    const string BarcodeTag = "Barcode";
    const float CooldownSeconds = 0.8f;
    const float FlashSeconds = 0.18f;
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    /// <summary>What lights up on a read: the green lamp on the scanner.</summary>
    public Renderer Indicator;
    public Color FlashColor = new Color(0.2f, 1f, 0.3f, 0.6f);
    /// <summary>When above zero, the barcode must face into a window within this angle. Zero keeps the plain volume test.</summary>
    public float MaxBarcodeAngle;
    /// <summary>The scan beep (Assets/Audio/Generated/sfx_scan.wav). Empty falls back to a generated 2.2 kHz tone.</summary>
    public AudioClip BeepClip;
    /// <summary>The middle of the glass a newcomer is shown, its up axis pointing out of the glass. Empty means this object.</summary>
    public Transform Aim;

    public event Action<ProductPrefab> Scanned;
    public int ScanCount { get; private set; }
    public int IgnoredBodyEntries { get; private set; }
    public int AngledBarcodeEntries { get; private set; }
    public ProductPrefab LastScanned { get; private set; }
    /// <summary>Where to hold a barcode up to: <see cref="Aim"/>, or this object when there is no upright window.</summary>
    public Transform Shown => Aim ? Aim : transform;

    /// <summary>One product's time in front of the scanner.</summary>
    class Visit
    {
        public readonly HashSet<Transform> Windows = new HashSet<Transform>();
        public float LeftAt = float.NegativeInfinity;
        public bool Read;
    }

    AudioSource speaker;
    AudioClip beep;
    MaterialPropertyBlock flashBlock;
    readonly Dictionary<ProductPrefab, Visit> visits = new Dictionary<ProductPrefab, Visit>();
    readonly Dictionary<Collider, ProductPrefab> barcodes = new Dictionary<Collider, ProductPrefab>();

    void Awake()
    {
        speaker = GetComponent<AudioSource>();
        speaker.spatialBlend = 1f;
        speaker.playOnAwake = false;
        beep = BeepClip != null ? BeepClip : CreateBeep();
        flashBlock = new MaterialPropertyBlock();
    }

    void OnTriggerEnter(Collider other) => Entered(other, transform);
    void OnTriggerStay(Collider other) => Stayed(other, transform);
    void OnTriggerExit(Collider other) => Left(other, transform);

    public void Entered(Collider other, Transform window)
    {
        // The layer matrix still allows Interactable x Scanner, so the tag is what
        // keeps the product body from counting as a scan.
        if (!other.CompareTag(BarcodeTag))
        {
            var body = other.GetComponentInParent<ProductPrefab>();
            if (body != null && body.Data != null) IgnoredBodyEntries++;
            return;
        }
        var product = Barcode(other);
        if (product == null) return;

        Forget();
        if (!visits.TryGetValue(product, out var visit)) visits[product] = visit = new Visit();
        if (visit.Windows.Count == 0 && Time.time - visit.LeftAt >= CooldownSeconds) visit.Read = false;
        visit.Windows.Add(window);
        if (!Faces(product, window)) AngledBarcodeEntries++;
        Look(product, visit, window);
    }

    /// <summary>Coming into a window at an angle and then turning the barcode to it still counts.</summary>
    public void Stayed(Collider other, Transform window)
    {
        if (!other.CompareTag(BarcodeTag)) return;
        var product = Barcode(other);
        if (product != null && visits.TryGetValue(product, out var visit)) Look(product, visit, window);
    }

    public void Left(Collider other, Transform window)
    {
        if (!other.CompareTag(BarcodeTag)) return;
        var product = Barcode(other);
        if (product == null || !visits.TryGetValue(product, out var visit)) return;
        visit.Windows.Remove(window);
        if (visit.Windows.Count == 0) visit.LeftAt = Time.time;
    }

    void Look(ProductPrefab product, Visit visit, Transform window)
    {
        if (visit.Read || !Faces(product, window)) return;
        visit.Read = true;
        Scan(product);
    }

    ProductPrefab Barcode(Collider collider)
    {
        if (!barcodes.TryGetValue(collider, out var product))
            barcodes[collider] = product = collider.GetComponentInParent<ProductPrefab>();
        return product != null && product.Data != null ? product : null;
    }

    /// <summary>Products of orders that are over have been destroyed; their entries go the next time something comes in.</summary>
    void Forget()
    {
        List<ProductPrefab> gone = null;
        foreach (var product in visits.Keys)
            if (product == null) (gone ??= new List<ProductPrefab>()).Add(product);
        if (gone == null) return;
        foreach (var product in gone) visits.Remove(product);
        List<Collider> lost = null;
        foreach (var pair in barcodes)
            if (pair.Key == null || pair.Value == null) (lost ??= new List<Collider>()).Add(pair.Key);
        if (lost != null) foreach (var collider in lost) barcodes.Remove(collider);
    }

    bool Faces(ProductPrefab product, Transform window)
    {
        if (MaxBarcodeAngle <= 0f) return true;
        var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        return Vector3.Angle(normal, -window.up) <= MaxBarcodeAngle;
    }

    void Scan(ProductPrefab product)
    {
        if (!product.TryMarkScanned(Time.time, CooldownSeconds)) return;

        ScanCount++;
        LastScanned = product;
        speaker.PlayOneShot(beep);
        if (Indicator) StartCoroutine(Flash());
        foreach (var interactor in product.Grab.interactorsSelecting)
            if (interactor is XRBaseInputInteractor hand) hand.SendHapticImpulse(0.6f, 0.1f);
        Debug.Log($"[Scanner] {product.Data.Name} ({product.Data.Id}) price={product.Data.Price}");
        Scanned?.Invoke(product);
    }

    IEnumerator Flash()
    {
        flashBlock.SetColor(BaseColor, FlashColor);
        flashBlock.SetColor(EmissionColor, new Color(FlashColor.r, FlashColor.g, FlashColor.b) * 2.5f);
        Indicator.SetPropertyBlock(flashBlock);
        yield return new WaitForSeconds(FlashSeconds);
        Indicator.SetPropertyBlock(null);
    }

    static AudioClip CreateBeep()
    {
        const int rate = 44100;
        const float seconds = 0.1f;
        var samples = new float[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.4f * Mathf.Sin(2f * Mathf.PI * 2200f * i / rate);
        var clip = AudioClip.Create("Scanner Beep", samples.Length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
