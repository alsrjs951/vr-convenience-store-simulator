using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Game-rule scan (doc 5.3): a product counts when its BarcodeTrigger volume enters this zone.
/// This is collision logic only. It does not read the barcode image.
/// </summary>
[RequireComponent(typeof(Collider), typeof(AudioSource))]
public class ScannerZone : MonoBehaviour
{
    const string BarcodeTag = "Barcode";
    const float CooldownSeconds = 0.8f;
    const float FlashSeconds = 0.15f;
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    public Renderer Indicator;
    public Color FlashColor = new Color(0.2f, 1f, 0.3f, 0.6f);
    /// <summary>When above zero, the barcode must face down the zone's axis within this angle. Zero keeps the plain volume test.</summary>
    public float MaxBarcodeAngle;
    /// <summary>The scan beep (Assets/Audio/Generated/sfx_scan.wav). Empty falls back to a generated 2.2 kHz tone.</summary>
    public AudioClip BeepClip;

    public event Action<ProductPrefab> Scanned;
    public int ScanCount { get; private set; }
    public int IgnoredBodyEntries { get; private set; }
    public int AngledBarcodeEntries { get; private set; }
    public ProductPrefab LastScanned { get; private set; }

    AudioSource speaker;
    AudioClip beep;
    MaterialPropertyBlock flashBlock;
    readonly HashSet<ProductPrefab> waitingForAngle = new HashSet<ProductPrefab>();

    void Awake()
    {
        speaker = GetComponent<AudioSource>();
        speaker.spatialBlend = 1f;
        speaker.playOnAwake = false;
        beep = BeepClip != null ? BeepClip : CreateBeep();
        flashBlock = new MaterialPropertyBlock();
    }

    void OnTriggerEnter(Collider other)
    {
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product == null || product.Data == null) return;

        // The layer matrix still allows Interactable x Scanner, so the tag is what
        // keeps the product body from counting as a scan.
        if (!other.CompareTag(BarcodeTag))
        {
            IgnoredBodyEntries++;
            return;
        }
        if (!FacesScanner(product))
        {
            AngledBarcodeEntries++;
            waitingForAngle.Add(product);
            return;
        }
        Scan(product);
    }

    /// <summary>Touching the pad at an angle and then laying the barcode flat still counts.</summary>
    void OnTriggerStay(Collider other)
    {
        if (waitingForAngle.Count == 0 || !other.CompareTag(BarcodeTag)) return;
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product == null || !waitingForAngle.Contains(product) || !FacesScanner(product)) return;
        waitingForAngle.Remove(product);
        Scan(product);
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(BarcodeTag)) return;
        var product = other.GetComponentInParent<ProductPrefab>();
        if (product != null) waitingForAngle.Remove(product);
    }

    bool FacesScanner(ProductPrefab product)
    {
        if (MaxBarcodeAngle <= 0f) return true;
        var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        return Vector3.Angle(normal, -transform.up) <= MaxBarcodeAngle;
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
