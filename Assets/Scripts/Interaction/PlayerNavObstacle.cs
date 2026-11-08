using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Doc 5.1: customers walk around the player instead of bumping into them. Physics between the two is off (layers), so
/// a NavMeshObstacle sits on the body capsule, which RoomScaleBody moves under the head.
///
/// 2026-11-08 (Day 2): while the player stands still it carves the customers' NavMesh. Day 2 has the clerk shelving in
/// the customer area, and local avoidance alone left a customer walking into the clerk until the stuck warp. Moving, it
/// does not carve, so a walking player does not keep re-cutting the mesh. Behind the counter there is no customer
/// NavMesh, so Day 1 is unaffected.
/// </summary>
[RequireComponent(typeof(NavMeshObstacle), typeof(CharacterController))]
public class PlayerNavObstacle : MonoBehaviour
{
    public const float Radius = 0.3f;
    public const float Height = 1.7f;

    NavMeshObstacle obstacle;
    CharacterController capsule;

    void Awake()
    {
        obstacle = GetComponent<NavMeshObstacle>();
        capsule = GetComponent<CharacterController>();
        obstacle.shape = NavMeshObstacleShape.Capsule;
        obstacle.carving = true;
        obstacle.carveOnlyStationary = true;
        obstacle.carvingTimeToStationary = 0.5f;
        obstacle.carvingMoveThreshold = 0.1f;
        obstacle.radius = Radius;
        obstacle.height = Height;
    }

    void LateUpdate()
    {
        var center = capsule.center;
        obstacle.center = new Vector3(center.x, Height * 0.5f, center.z);
    }
}
