using GaussianSplatting.Runtime;
using UnityEngine;

public enum BarcodeFace { Bottom, Back, Side }

/// <summary>What a product is packed in, which is what a hand hears and feels: a film bag, or a carton.</summary>
public enum ProductPackage { Bag, Carton }

/// <summary>Layer 4 of the product prefab contract (implementation doc 4.3).</summary>
[CreateAssetMenu(menuName = "VR Store/Product Data")]
public class ProductData : ScriptableObject
{
    public string Id;
    public string Name;
    public int Price;
    public Vector3 SizeCm;
    public BarcodeFace BarcodeFace;
    public GaussianSplatAsset SplatAsset;
    [Tooltip("Lighter copy for box contents and the placement ghost (doc 4.6). Empty keeps the full one there too.")]
    public GaussianSplatAsset LightSplat;
    public int OrderCatalogFlags;

    [Tooltip("False while SizeCm is a development placeholder, not a tape measurement.")]
    public bool SizeIsMeasured;

    [Tooltip("Printed barcode's face in product space. Doc 4.4 fixes side=+X, but real boxes can print it on -X.")]
    public Vector3 BarcodeLocalNormal = Vector3.down;

    [Tooltip("A film bag crackles in the hand, a carton knocks. From the manifest's \"package\"; a bag when it says nothing.")]
    public ProductPackage Package;
}
