using GaussianSplatting.Runtime;
using UnityEngine;

public enum BarcodeFace { Bottom, Back, Side }

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
    public int OrderCatalogFlags;

    [Tooltip("False while SizeCm is a development placeholder, not a tape measurement.")]
    public bool SizeIsMeasured;

    [Tooltip("Printed barcode's face in product space. Doc 4.4 fixes side=+X, but real boxes can print it on -X.")]
    public Vector3 BarcodeLocalNormal = Vector3.down;
}
