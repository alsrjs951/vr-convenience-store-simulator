using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rounded boxes for the store's fixtures and counter devices (P3 dressing), saved as mesh assets so the scene keeps
/// them. A box with soft edges reads as a made object; a sharp cube reads as a placeholder. Each mesh carries a second
/// UV set for the baked light (P3StoreLighting).
/// </summary>
public static class P3Meshes
{
    const string Folder = "Assets/Art/Generated/Meshes";
    static readonly Dictionary<string, Mesh> made = new Dictionary<string, Mesh>();

    /// <summary>A box of <paramref name="size"/> centered on its origin, edges rounded by <paramref name="radius"/>. UVs are in meters.</summary>
    public static Mesh RoundedBox(Vector3 size, float radius, int segments = 3)
    {
        float maxRadius = Mathf.Min(size.x, size.y, size.z) * 0.5f - 0.0001f;
        radius = Mathf.Clamp(radius, 0.0005f, Mathf.Max(0.0005f, maxRadius));
        string key = $"rbox_{size.x:0.####}_{size.y:0.####}_{size.z:0.####}_r{radius:0.####}_s{segments}".Replace('.', 'p');
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;

        Directory.CreateDirectory(Folder);
        var path = $"{Folder}/{key}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool fresh = mesh == null;
        if (fresh) mesh = new Mesh { name = key };
        Fill(mesh, size * 0.5f, radius, segments);
        if (fresh) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        made[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// A flat-shaded prism: a convex side profile, points (z, y) in counter-clockwise order, pushed out along x to
    /// <paramref name="width"/> and centered on x = 0. For angular housings such as the scanner tower. UVs are in meters.
    /// </summary>
    public static Mesh Prism(Vector2[] profile, float width)
    {
        var key = $"prism_w{width:0.####}";
        foreach (var p in profile) key += $"_{p.x:0.####}_{p.y:0.####}";
        key = key.Replace('.', 'p').Replace('-', 'm');
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        float h = width * 0.5f;
        void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            // Outward when cross(b - a, c - a) points out (the same rule as the rounded box's faces).
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) { (b, c) = (c, b); (ub, uc) = (uc, ub); }
            int start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            normals.Add(outward); normals.Add(outward); normals.Add(outward);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
        }
        // Side faces, one quad per profile edge.
        for (int i = 0; i < profile.Length; i++)
        {
            var p = profile[i];
            var q = profile[(i + 1) % profile.Length];
            // Edge direction (dz, dy); for a counter-clockwise profile the outward normal is z = dy, y = -dz.
            var faceNormal = new Vector3(0f, -(q.x - p.x), q.y - p.y).normalized;
            float length = Vector2.Distance(p, q);
            var a = new Vector3(-h, p.y, p.x);
            var b = new Vector3(h, p.y, p.x);
            var c = new Vector3(h, q.y, q.x);
            var d = new Vector3(-h, q.y, q.x);
            Triangle(a, b, c, faceNormal, new Vector2(0f, 0f), new Vector2(width, 0f), new Vector2(width, length));
            Triangle(a, c, d, faceNormal, new Vector2(0f, 0f), new Vector2(width, length), new Vector2(0f, length));
        }
        // The two flat ends, fanned from the first point.
        foreach (float side in new[] { -1f, 1f })
            for (int i = 1; i < profile.Length - 1; i++)
            {
                var p0 = profile[0];
                var p1 = profile[i];
                var p2 = profile[i + 1];
                Triangle(new Vector3(side * h, p0.y, p0.x), new Vector3(side * h, p1.y, p1.x), new Vector3(side * h, p2.y, p2.x), new Vector3(side, 0f, 0f),
                    new Vector2(p0.x, p0.y), new Vector2(p1.x, p1.y), new Vector2(p2.x, p2.y));
            }

        Directory.CreateDirectory(Folder);
        var path = $"{Folder}/{key}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool fresh = mesh == null;
        if (fresh) mesh = new Mesh { name = key };
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        Unwrapping.GenerateSecondaryUVSet(mesh);
        if (fresh) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        made[key] = mesh;
        return mesh;
    }

    /// <summary>Turns a Box() cube (unit mesh scaled to its size) into a rounded box of the same size; the collider keeps its size.</summary>
    public static void Round(GameObject box, float radius)
    {
        var size = box.transform.localScale;
        box.transform.localScale = Vector3.one;
        box.GetComponent<MeshFilter>().sharedMesh = RoundedBox(size, radius);
        if (box.TryGetComponent<BoxCollider>(out var collider)) collider.size = size;
    }

    static void Fill(Mesh mesh, Vector3 h, float r, int segments)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        Vector3 inner = new Vector3(h.x - r, h.y - r, h.z - r);

        // Six faces: (normal, u, v) with v x u = normal, so the triangles (p, p+v, p+u) face outward.
        var faces = new (Vector3 n, Vector3 u, Vector3 v)[]
        {
            (Vector3.right, Vector3.forward, Vector3.up), (Vector3.left, Vector3.back, Vector3.up),
            (Vector3.up, Vector3.right, Vector3.forward), (Vector3.down, Vector3.right, Vector3.back),
            (Vector3.forward, Vector3.left, Vector3.up), (Vector3.back, Vector3.right, Vector3.up),
        };
        foreach (var (n, u, v) in faces)
        {
            float hu = Mathf.Abs(Vector3.Dot(h, u)), hv = Mathf.Abs(Vector3.Dot(h, v)), hn = Mathf.Abs(Vector3.Dot(h, n));
            var su = Samples(hu, r, segments);
            var sv = Samples(hv, r, segments);
            int start = vertices.Count;
            foreach (var b in sv)
                foreach (var a in su)
                {
                    var p = n * hn + u * a + v * b;
                    var clamped = new Vector3(Mathf.Clamp(p.x, -inner.x, inner.x), Mathf.Clamp(p.y, -inner.y, inner.y), Mathf.Clamp(p.z, -inner.z, inner.z));
                    var d = p - clamped;
                    var normal = d.sqrMagnitude > 1e-12f ? d.normalized : n;
                    vertices.Add(clamped + normal * r);
                    normals.Add(normal);
                    uvs.Add(new Vector2(a + hu, b + hv));
                }
            int cols = su.Count;
            for (int j = 0; j < sv.Count - 1; j++)
                for (int i = 0; i < cols - 1; i++)
                {
                    int i0 = start + j * cols + i, i1 = i0 + 1, i2 = i0 + cols, i3 = i2 + 1;
                    triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                    triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                }
        }
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        Unwrapping.GenerateSecondaryUVSet(mesh);
    }

    /// <summary>Coordinates along one face axis: dense over the rounded strips at both ends, one step across the flat middle.</summary>
    static List<float> Samples(float half, float r, int segments)
    {
        var list = new List<float>();
        for (int i = 0; i <= segments; i++) list.Add(-half + r * i / segments);
        for (int i = 0; i <= segments; i++)
        {
            float x = half - r + r * i / segments;
            if (x > list[list.Count - 1] + 1e-6f) list.Add(x);
        }
        return list;
    }
}
