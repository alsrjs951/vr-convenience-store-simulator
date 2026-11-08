using System.Collections.Generic;
using UnityEngine;

/// <summary>Runtime list of product prefabs. Orders refer to products by ProductData.Id.</summary>
[CreateAssetMenu(menuName = "VR Store/Product Catalog")]
public class ProductCatalog : ScriptableObject
{
    public List<ProductPrefab> Products = new List<ProductPrefab>();

    public ProductPrefab Find(string id)
    {
        foreach (var product in Products)
            if (product && product.Data && product.Data.Id == id)
                return product;
        return null;
    }
}
