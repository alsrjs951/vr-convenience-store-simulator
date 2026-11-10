using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The scanner's red light, where it lands (2026-11-10). Fans of rays leave a point behind the upright glass, along this
/// object's forward, and a thin red line is drawn on whatever they reach first: the flat glass in the counter when nothing
/// is there, and the product as soon as one is held in front. So the light is seen to come out of the upright glass, as on
/// a real scanner, instead of being painted on the counter.
///
/// The lines are opaque. Splats are blended in before the transparent things and tested against depth, so an opaque line
/// sits correctly in front of or behind a product; a transparent one would show through it.
/// </summary>
public class ScanBeam : MonoBehaviour
{
    const int Samples = 33;
    // Where a line breaks: the next ray landed on something else, or on a face turned away from this one.
    const float SameFace = 0.98f;
    const float BlankSeconds = 0.12f;

    public ScannerZone Zone;
    public Material Material;
    /// <summary>Each line: x is how far below this object's forward it leaves, y how far it is rolled about forward, in degrees.</summary>
    public Vector2[] Lines = { new Vector2(0f, 0f) };
    public float HalfAngle = 24f;
    /// <summary>Past this the light is too faint to see, as when it falls on the floor behind the counter.</summary>
    public float Range = 0.36f;
    public float Width = 0.0024f;
    /// <summary>How far off the surface the line is drawn, clear of the glass plate lying on the scanner's housing.</summary>
    public float Lift = 0.0034f;
    public LayerMask Reaches = ~0;

    Mesh mesh;
    MeshRenderer drawn;
    float blankUntil;
    readonly List<Vector3> vertices = new List<Vector3>();
    readonly List<int> triangles = new List<int>();
    readonly RaycastHit[] hits = new RaycastHit[Samples];
    readonly bool[] landed = new bool[Samples];

    void Awake()
    {
        var holder = new GameObject("Scan Lines", typeof(MeshFilter), typeof(MeshRenderer));
        // World space: the points come straight from the raycasts.
        holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        mesh = new Mesh { name = "Scan Lines" };
        mesh.MarkDynamic();
        holder.GetComponent<MeshFilter>().sharedMesh = mesh;
        drawn = holder.GetComponent<MeshRenderer>();
        drawn.sharedMaterial = Material;
        drawn.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        drawn.receiveShadows = false;
        drawn.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        drawn.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    void OnEnable() { if (Zone) Zone.Scanned += Blank; }
    void OnDisable() { if (Zone) Zone.Scanned -= Blank; }
    void OnDestroy() { if (drawn) Destroy(drawn.gameObject); if (mesh) Destroy(mesh); }

    /// <summary>A real scanner's light blinks off for a moment on a good read.</summary>
    void Blank(ProductPrefab product) => blankUntil = Time.time + BlankSeconds;

    void LateUpdate()
    {
        vertices.Clear();
        triangles.Clear();
        if (Time.time >= blankUntil)
            foreach (var line in Lines) Draw(line);
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
    }

    void Draw(Vector2 line)
    {
        var origin = transform.position;
        var aim = transform.rotation * Quaternion.Euler(line.x, 0f, line.y);
        for (int i = 0; i < Samples; i++)
        {
            float across = Mathf.Lerp(-HalfAngle, HalfAngle, i / (Samples - 1f));
            var direction = aim * Quaternion.Euler(0f, across, 0f) * Vector3.forward;
            landed[i] = Physics.Raycast(origin, direction, out hits[i], Range, Reaches, QueryTriggerInteraction.Ignore);
        }
        for (int i = 0; i + 1 < Samples; i++)
        {
            if (!landed[i] || !landed[i + 1] || hits[i].collider != hits[i + 1].collider) continue;
            var normal = hits[i].normal;
            if (Vector3.Dot(normal, hits[i + 1].normal) < SameFace) continue;
            var from = hits[i].point + normal * Lift;
            var to = hits[i + 1].point + normal * Lift;
            var side = Vector3.Cross(normal, to - from).normalized * (Width * 0.5f);
            int first = vertices.Count;
            vertices.Add(from - side);
            vertices.Add(from + side);
            vertices.Add(to + side);
            vertices.Add(to - side);
            triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
            triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
        }
    }
}
