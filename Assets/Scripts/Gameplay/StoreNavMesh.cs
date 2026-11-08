using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Puts the store's NavMesh into the scene. P2StoreBuilder bakes it with the built-in NavMeshBuilder (AI module), so no
/// AI Navigation package is needed; this does what NavMeshSurface would do at runtime.
/// </summary>
public class StoreNavMesh : MonoBehaviour
{
    public NavMeshData Data;

    NavMeshDataInstance instance;

    void OnEnable()
    {
        if (Data != null) instance = NavMesh.AddNavMeshData(Data, transform.position, transform.rotation);
    }

    void OnDisable()
    {
        if (instance.valid) instance.Remove();
    }
}
