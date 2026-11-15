using System.Text;
using Unity.XR.CoreUtils;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Lists what the player's capsule could walk up onto in the store: every solid collider the player layer meets whose
/// top is between the floor and a little over the capsule's step. Written after a headset session (2026-11-15) in which
/// the view suddenly stood too high. Run with -executeMethod P2StepReport.Run; the lines start with [StepReport].
/// </summary>
public static class P2StepReport
{
    const float LowestTop = 0.03f;
    const float HighestTop = 0.6f;

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/StoreScene.unity");
        Physics.SyncTransforms();
        var rig = Object.FindFirstObjectByType<XROrigin>();
        var capsule = rig.GetComponent<CharacterController>();
        Debug.Log($"[StepReport] capsule step={capsule.stepOffset:0.###} slope={capsule.slopeLimit:0.#} radius={capsule.radius:0.###} height={capsule.height:0.###} skin={capsule.skinWidth:0.###} rig=({rig.transform.position.x:0.00}, {rig.transform.position.y:0.000}, {rig.transform.position.z:0.00})");

        int found = 0;
        foreach (var collider in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (collider.isTrigger || !collider.enabled) continue;
            if (Physics.GetIgnoreLayerCollision(rig.gameObject.layer, collider.gameObject.layer)) continue;
            if (collider.transform.IsChildOf(rig.transform)) continue;
            // Bounds of an inactive collider are empty; say so rather than leave it out.
            bool active = collider.gameObject.activeInHierarchy;
            var bounds = collider.bounds;
            if (active && (bounds.max.y < LowestTop || bounds.max.y > HighestTop)) continue;
            if (!active && !(collider.attachedRigidbody != null || collider.GetComponentInParent<Rigidbody>(true) != null)) continue;
            var body = collider.GetComponentInParent<Rigidbody>(true);
            string kind = body == null ? "static" : body.isKinematic ? "kinematic" : "dynamic";
            string where = active
                ? $"top={bounds.max.y:0.000} bottom={bounds.min.y:0.000} size=({bounds.size.x:0.00} x {bounds.size.z:0.00}) at=({bounds.center.x:0.00}, {bounds.center.z:0.00})"
                : "inactive (comes into play later)";
            Debug.Log($"[StepReport] {where} {kind} layer={LayerMask.LayerToName(collider.gameObject.layer)} {PathOf(collider.transform)}");
            found++;
        }
        Debug.Log($"[StepReport] {found} colliders listed");
    }

    static string PathOf(Transform transform)
    {
        var path = new StringBuilder(transform.name);
        for (var parent = transform.parent; parent != null; parent = parent.parent) path.Insert(0, parent.name + "/");
        return path.ToString();
    }
}
