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

    /// <summary>
    /// A unit slab for a banknote or a wad of them, scaled to size where it is used: the whole picture on its top (x along
    /// the picture's width), the same picture on its underside, and the paper's edge colour on its four sides.
    /// </summary>
    public static Mesh Note()
    {
        const string key = "cash_note";
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        // The picture's border is plain paper; the sides take their colour from the middle of its lower edge.
        var edge = new Vector2(0.5f, 0.004f);
        void Face(Vector3 normal, Vector3 u, Vector3 v, System.Func<Vector2, Vector2> uv)
        {
            int start = vertices.Count;
            foreach (var corner in new[] { new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f) })
            {
                vertices.Add(normal * 0.5f + u * corner.x + v * corner.y);
                normals.Add(normal);
                uvs.Add(uv(corner));
            }
            // v x u = normal, so (0, 3, 2) and (0, 2, 1) face outward.
            triangles.AddRange(new[] { start, start + 3, start + 2, start, start + 2, start + 1 });
        }
        Face(Vector3.up, Vector3.right, Vector3.forward, c => new Vector2(c.x + 0.5f, c.y + 0.5f));
        Face(Vector3.down, Vector3.left, Vector3.forward, c => new Vector2(c.x + 0.5f, c.y + 0.5f));
        Face(Vector3.right, Vector3.forward, Vector3.up, _ => edge);
        Face(Vector3.left, Vector3.back, Vector3.up, _ => edge);
        Face(Vector3.forward, Vector3.left, Vector3.up, _ => edge);
        Face(Vector3.back, Vector3.right, Vector3.up, _ => edge);
        return Save(key, vertices, normals, uvs, triangles);
    }

    /// <summary>A unit disc for a coin: the round picture on both faces, the metal around the picture's disc on its edge.</summary>
    public static Mesh Coin()
    {
        const string key = "cash_coin";
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;
        const int sides = 28;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        var edge = new Vector2(0.5f, 0.006f);
        foreach (float face in new[] { 1f, -1f })
        {
            int center = vertices.Count;
            vertices.Add(new Vector3(0f, face * 0.5f, 0f));
            normals.Add(Vector3.up * face);
            uvs.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                vertices.Add(new Vector3(Mathf.Cos(a) * 0.5f, face * 0.5f, Mathf.Sin(a) * 0.5f));
                normals.Add(Vector3.up * face);
                // Mirrored underneath, so the number reads the right way round from below too.
                uvs.Add(new Vector2(0.5f + face * Mathf.Cos(a) * 0.49f, 0.5f + Mathf.Sin(a) * 0.49f));
            }
            for (int i = 0; i < sides; i++)
                triangles.AddRange(face > 0f ? new[] { center, center + i + 2, center + i + 1 } : new[] { center, center + i + 1, center + i + 2 });
        }
        int rim = vertices.Count;
        for (int i = 0; i <= sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            vertices.Add(outward * 0.5f + Vector3.up * 0.5f);
            vertices.Add(outward * 0.5f + Vector3.down * 0.5f);
            normals.Add(outward);
            normals.Add(outward);
            uvs.Add(edge);
            uvs.Add(edge);
        }
        for (int i = 0; i < sides; i++)
        {
            int top = rim + i * 2, bottom = top + 1, nextTop = top + 2, nextBottom = top + 3;
            triangles.AddRange(new[] { top, nextTop, bottom, bottom, nextTop, nextBottom });
        }
        return Save(key, vertices, normals, uvs, triangles);
    }

    // ---------- The counter's plastic bag ----------

    /// <summary>How much of the bag the film texture is wide (scripts/windows/make_bag_assets.py): u goes once round the bag, v is height over half of this.</summary>
    public const float BagAtlasMeters = 0.9f;
    /// <summary>How high the film stands at the middle of its long sides, and at the foot of a handle.</summary>
    public const float BagNeck = 0.205f, BagShoulder = 0.255f;

    /// <summary>
    /// An open carrier bag of thin film standing on a counter, its origin the middle of its floor: square-footed where the
    /// gusset lies flat, bellied out above, drawn in toward the two handles, which loop from the side toward the clerk to
    /// the side toward the customer at each end. The creases are real, because the baked light has no direction and a
    /// normal map alone would leave it a smooth tub. An inner skin a film's thickness inside the outer one takes its own
    /// baked light, so the bag is darker inside than out.
    /// </summary>
    public static Mesh PlasticBag()
    {
        const string key = "plastic_bag";
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;

        const int around = 84, up = 20, across = 6, along = 14, fine = 2880;
        const float halfWidth = 0.15f, halfDepth = 0.08f, film = 0.0012f;
        // Where a handle's foot is, in degrees from the bag's long axis, and how far to each side of it the rim is lifted (a share of the way round).
        const float footDegrees = 42f, footReach = 0.052f;

        float Ease(float from, float to, float x) { x = Mathf.Clamp01((x - from) / (to - from)); return x * x * (3f - 2f * x); }

        // The bag's outline without its creases, by angle: a superellipse, squarer at the counter than at the mouth.
        Vector3 Outline(float degrees, float t, out Vector3 outward)
        {
            float a = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            float n = Mathf.Lerp(4.5f, 2.6f, Ease(0f, 0.6f, t));
            float tuck = t < 0.08f ? 1f - 0.07f * (1f - t / 0.08f) * (1f - t / 0.08f) : 1f;
            // The belly hangs low: a bag set down sinks into itself.
            float belly = Mathf.Max(0f, Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.7f)));
            float ax = halfWidth * tuck * (1f + 0.05f * belly - 0.14f * Ease(0.45f, 1f, t));
            float az = halfDepth * tuck * (1f + 0.24f * belly + 0.02f * t);
            outward = new Vector3(Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f - 2f / n) / ax, 0f, Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f - 2f / n) / az).normalized;
            return new Vector3(ax * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / n), 0f, az * Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f / n));
        }

        // Equal steps of the way round are equal lengths of film, not equal angles: the angle for each step, measured half-way up.
        var angleAt = new float[fine + 1];
        float footShare;
        {
            var lengthAt = new float[fine + 1];
            var last = Outline(0f, 0.5f, out _);
            for (int i = 1; i <= fine; i++)
            {
                var p = Outline(360f * i / fine, 0.5f, out _);
                lengthAt[i] = lengthAt[i - 1] + Vector3.Distance(last, p);
                last = p;
            }
            int at = 0;
            for (int i = 0; i <= fine; i++)
            {
                float want = lengthAt[fine] * i / fine;
                while (at < fine - 1 && lengthAt[at + 1] < want) at++;
                float span = lengthAt[at + 1] - lengthAt[at];
                angleAt[i] = 360f * (at + (span > 0f ? (want - lengthAt[at]) / span : 0f)) / fine;
            }
            footShare = lengthAt[Mathf.RoundToInt(footDegrees / 360f * fine)] / lengthAt[fine];
        }
        float Degrees(float share)
        {
            float f = Mathf.Repeat(share, 1f) * fine;
            int i = Mathf.Min((int)f, fine - 1);
            return Mathf.Lerp(angleAt[i], angleAt[i + 1], f - i);
        }

        float Near(float share, float where, float reach)
        {
            float d = Mathf.Abs(Mathf.Repeat(share - where + 0.5f, 1f) - 0.5f);
            return d < reach ? 0.5f * (1f + Mathf.Cos(Mathf.PI * d / reach)) : 0f;
        }
        float Feet(float share) => Mathf.Max(
            Mathf.Max(Near(share, footShare, footReach), Near(share, 0.5f - footShare, footReach)),
            Mathf.Max(Near(share, 0.5f + footShare, footReach), Near(share, 1f - footShare, footReach)));
        float Rim(float share)
        {
            float a = share * Mathf.PI * 2f;
            // Between a handle's two feet the film dips only a little: that is the gusset.
            float gusset = 0.35f * Mathf.Max(Near(share, 0f, footShare), Near(share, 0.5f, footShare));
            // One end has sunk a little more than the other.
            float sunk = 1f - 0.05f * (0.5f + 0.5f * Mathf.Cos(a - 3.6f));
            return (BagNeck + (BagShoulder - BagNeck) * Mathf.Max(Feet(share), gusset)) * sunk + 0.004f * Mathf.Sin(3f * a + 1.1f) + 0.003f * Mathf.Sin(5f * a + 0.3f);
        }
        // A point of the outer skin: share of the way round (0 = the end toward +x, 0.25 = the customer's side), t from the counter (0) to the rim (1).
        Vector3 Skin(float share, float t)
        {
            float a = share * Mathf.PI * 2f;
            var p = Outline(Degrees(share), t, out var outward);
            // Pleats run up the bag and gather at the handles; where the bag has sunk under its own weight it folds across.
            // Neither is even: the pleats wander and come and go round the bag, or it would look turned on a lathe.
            float wander = 1.1f * Mathf.Sin(2f * a + 0.5f) + 0.6f * Mathf.Sin(5f * a + 1.9f);
            float uneven = 0.55f + 0.45f * Mathf.Sin(3f * a + 1f + 2f * t);
            float pleats = (0.3f + 0.7f * t) * (1f + 0.8f * t * Feet(share))
                * (0.0042f * uneven * Mathf.Sin(7f * a + 0.8f + 1.5f * t + wander) + 0.003f * Mathf.Sin(11f * a + 2.1f - 2.2f * t - 0.8f * wander) + 0.0018f * Mathf.Sin(17f * a + 0.3f + 3f * t));
            float folds = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.8f))), 0.7f)
                * (0.0065f * Mathf.Sin(2f * Mathf.PI * 2.3f * t + 1.4f * Mathf.Sin(2f * a + 0.4f) + 0.5f) + 0.0035f * Mathf.Sin(2f * Mathf.PI * 4.1f * t + 1.8f * Mathf.Sin(3f * a) + 1.7f));
            p += outward * (pleats + folds);
            p.y = Rim(share) * t;
            // It leans a little, as a bag set down does.
            p.x += 0.01f * t * t;
            p.z -= 0.006f * t * t;
            return p;
        }
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        int Add(Vector3 p, Vector3 n, Vector2 uv)
        {
            vertices.Add(p);
            normals.Add(n);
            uvs.Add(uv);
            return vertices.Count - 1;
        }
        // Wound so that the face looks the way its corners' normals do.
        void Triangle(int a, int b, int c)
        {
            var facing = normals[a] + normals[b] + normals[c];
            if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), facing) < 0f) (b, c) = (c, b);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }
        void Quad(int a, int b, int c, int d)
        {
            Triangle(a, b, c);
            Triangle(b, d, c);
        }
        float vPerMeter = 2f / BagAtlasMeters;

        // The two skins. Normals come from the points themselves, not from the finer shape between them: a normal that
        // disagrees with its own faces shades as a nick in the film.
        int row = around + 1;
        var skinAt = new Vector3[row, up + 1];
        for (int j = 0; j <= up; j++)
            for (int i = 0; i <= around; i++)
                skinAt[i, j] = Skin(i / (float)around, j / (float)up);
        int outer = vertices.Count;
        for (int j = 0; j <= up; j++)
            for (int i = 0; i <= around; i++)
            {
                // The last column is the first again.
                var round = skinAt[i == around ? 1 : i + 1, j] - skinAt[i == 0 ? around - 1 : i - 1, j];
                var rise = skinAt[i, Mathf.Min(up, j + 1)] - skinAt[i, Mathf.Max(0, j - 1)];
                // Round goes counter-clockwise seen from above, so up x round points out.
                Add(skinAt[i, j], Vector3.Cross(rise, round).normalized, new Vector2(i / (float)around, skinAt[i, j].y * vPerMeter));
            }
        int inner = vertices.Count;
        // Inside, the film is plain: the print is on the outside, and its half of the texture is left below.
        for (int k = 0; k < inner - outer; k++)
            Add(vertices[outer + k] - normals[outer + k] * film, -normals[outer + k], new Vector2(uvs[outer + k].x, 0.5f + uvs[outer + k].y * 0.85f));
        // Wound by where the points are in the grid, never by their normals: a pleat can turn a normal past its face.
        for (int j = 0; j < up; j++)
            for (int i = 0; i < around; i++)
            {
                int a = outer + j * row + i, b = inner + j * row + i;
                triangles.AddRange(new[] { a, a + row, a + 1, a + 1, a + row, a + row + 1 });
                triangles.AddRange(new[] { b, b + 1, b + row, b + 1, b + row + 1, b + row });
            }

        // The cut edge of the film, between the two skins.
        int edge = vertices.Count;
        for (int i = 0; i <= around; i++)
        {
            Add(vertices[outer + up * row + i], Vector3.up, uvs[outer + up * row + i]);
            Add(vertices[inner + up * row + i], Vector3.up, uvs[outer + up * row + i]);
        }
        for (int i = 0; i < around; i++)
            Quad(edge + i * 2, edge + i * 2 + 1, edge + i * 2 + 2, edge + i * 2 + 3);

        // The floor, seen through the mouth. Plain film from the top of the texture.
        const float floorHeight = 0.0025f;
        Vector2 FloorUv(Vector3 p) => new Vector2(0.5f + p.x / BagAtlasMeters, 0.85f + p.z * vPerMeter * 0.7f);
        int floor = Add(new Vector3(0f, floorHeight, 0f), Vector3.up, FloorUv(Vector3.zero));
        for (int i = 0; i <= around; i++)
        {
            var p = vertices[inner + i];
            p.y = floorHeight;
            Add(p, Vector3.up, FloorUv(p));
        }
        for (int i = 0; i < around; i++)
            Triangle(floor, floor + 1 + i, floor + 2 + i);

        // The handles. Each is a strap from the rim on the clerk's side over to the rim on the customer's side, as wide as
        // its foot where it leaves the rim and gathered to half that at the top. They have flopped outward, one more than the other.
        int footColumn = Mathf.RoundToInt(footShare * around);
        var handles = new (int end, float lean, float rise, float shift, float twist, float uStart)[]
        {
            (1, 0.036f, 0.086f, 0.012f, 24f, 0.02f),
            (-1, 0.052f, 0.064f, -0.016f, -34f, 0.52f),
        };
        foreach (var (end, lean, rise, shift, twist, uStart) in handles)
        {
            // Column of the foot toward the clerk and of the one toward the customer; k counts across the strap toward the bag's end.
            int front = end > 0 ? around - footColumn : around / 2 + footColumn;
            int back = end > 0 ? footColumn : around / 2 - footColumn;
            Vector3 RimAt(int column) => vertices[outer + up * row + (column % around + around) % around];
            var from = RimAt(front);
            var to = RimAt(back);
            var pull = new Vector3(end * lean, rise, shift) * (4f / 3f);
            var c1 = from + pull + Vector3.forward * 0.01f;
            var c2 = to + pull + Vector3.back * 0.01f;
            Vector3 Curve(float s) => Mathf.Pow(1f - s, 3f) * from + 3f * (1f - s) * (1f - s) * s * c1 + 3f * (1f - s) * s * s * c2 + s * s * s * to;

            var points = new Vector3[along + 1, across + 1];
            for (int r = 0; r <= along; r++)
            {
                float s = r / (float)along;
                var middle = Curve(s);
                var heading = (Curve(Mathf.Min(1f, s + 0.02f)) - Curve(Mathf.Max(0f, s - 0.02f))).normalized;
                // Never below zero: sin(pi) is a hair under it, and a power of a negative number is not a number.
                float arch = Mathf.Max(0f, Mathf.Sin(Mathf.PI * s));
                float gathered = 1f - 0.5f * Mathf.Pow(arch, 1.2f);
                var turned = Quaternion.AngleAxis(twist * arch, heading);
                for (int k = 0; k <= across; k++)
                {
                    int step = k - across / 2;
                    var atFront = RimAt(front + end * step) - from;
                    var atBack = RimAt(back - end * step) - to;
                    points[r, k] = middle + turned * (Vector3.Lerp(atFront, atBack, Ease(0f, 1f, s)) * gathered);
                }
            }
            // Film has two faces: the same strap twice, a hair apart, looking opposite ways.
            foreach (float face in new[] { 1f, -1f })
            {
                int start = vertices.Count;
                for (int r = 0; r <= along; r++)
                    for (int k = 0; k <= across; k++)
                    {
                        var alongStrap = points[Mathf.Min(along, r + 1), k] - points[Mathf.Max(0, r - 1), k];
                        var acrossStrap = points[r, Mathf.Min(across, k + 1)] - points[r, Mathf.Max(0, k - 1)];
                        var n = Vector3.Cross(alongStrap, acrossStrap).normalized * face;
                        Add(points[r, k] + n * 0.0003f, n, new Vector2(uStart + 0.33f * r / along, 0.62f + 0.13f * k / across));
                    }
                // along x across is this face's normal, so (a, next along, next across) looks that way.
                int next = across + 1;
                for (int r = 0; r < along; r++)
                    for (int k = 0; k < across; k++)
                    {
                        int a = start + r * next + k;
                        triangles.AddRange(face > 0f ? new[] { a, a + next, a + 1, a + 1, a + next, a + next + 1 } : new[] { a, a + 1, a + next, a + 1, a + next + 1, a + next });
                    }
            }
        }
        // The lightmap unwrap never returns from a mesh with such a point in it (2026-11-11), so stop here instead.
        for (int i = 0; i < vertices.Count; i++)
            if (float.IsNaN(vertices[i].x + vertices[i].y + vertices[i].z + normals[i].x + normals[i].y + normals[i].z))
                throw new System.Exception($"Plastic bag mesh: vertex {i} of {vertices.Count} is not a number.");
        return Save(key, vertices, normals, uvs, triangles, true);
    }

    /// <summary>
    /// The same bag carried by its handles, full: its origin is where the hand holds it and it hangs down -y, its broad
    /// side along x. The mouth is drawn shut between the two handles, which run up taut to the hand; below, the goods
    /// round it out. Not for the bake: it moves with whoever carries it.
    /// </summary>
    public static Mesh CarriedBag()
    {
        const string key = "plastic_bag_carried";
        if (made.TryGetValue(key, out var cached) && cached != null) return cached;

        const int around = 64, up = 16, across = 12, along = 8;
        const float bottom = -0.47f, mouth = -0.185f, shoulder = -0.15f, film = 0.003f;

        float Ease(float from, float to, float x) { x = Mathf.Clamp01((x - from) / (to - from)); return x * x * (3f - 2f * x); }
        // share: the way round, 0 at the end toward +x, 0.25 at the side toward +z. t: 0 at the foot, 1 at the mouth.
        Vector3 Skin(float share, float t)
        {
            float a = share * Mathf.PI * 2f;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            // The foot is rounded under the weight; from there the bag narrows to a mouth pulled shut.
            float foot = t < 0.16f ? Mathf.Pow(Mathf.Sin(Mathf.PI * 0.5f * t / 0.16f), 0.55f) : 1f;
            float ax = foot * Mathf.Lerp(0.135f, 0.062f, Ease(0.25f, 1f, t));
            float az = foot * Mathf.Lerp(0.07f, 0.011f, Mathf.Pow(Ease(0.2f, 1f, t), 0.8f));
            const float n = 2.4f;
            var p = new Vector3(ax * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / n), 0f, az * Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f / n));
            var outward = new Vector3(c / Mathf.Max(ax, 0.001f), 0f, s / Mathf.Max(az, 0.001f)).normalized;
            // What is inside pushes out here and there; the film gathers into pleats toward the handles.
            float lumps = Mathf.Max(0f, Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.75f))) * (0.011f * Mathf.Sin(2f * a + 0.9f + 2.2f * t) + 0.007f * Mathf.Sin(3f * a + 2.3f - 1.4f * t));
            float pleats = t * t * foot * (0.0045f * Mathf.Sin(9f * a + 0.6f + 1.2f * t) + 0.0025f * Mathf.Sin(15f * a + 1.7f - 2f * t));
            p += outward * (lumps + pleats);
            // The mouth is highest at the two ends, where the handles pull it up.
            float rim = mouth + (shoulder - mouth) * Mathf.Pow(Mathf.Abs(c), 1.5f);
            p.y = bottom + (rim - bottom) * (t < 0.16f ? 0.1f * (t / 0.16f) * (t / 0.16f) + 0f : 0.1f + 0.9f * (t - 0.16f) / 0.84f);
            return p;
        }

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        int Add(Vector3 p, Vector3 n, Vector2 uv)
        {
            vertices.Add(p);
            normals.Add(n);
            uvs.Add(uv);
            return vertices.Count - 1;
        }
        float vPerMeter = 2f / BagAtlasMeters;

        int row = around + 1;
        var skinAt = new Vector3[row, up + 1];
        for (int j = 0; j <= up; j++)
            for (int i = 0; i <= around; i++)
                skinAt[i, j] = Skin(i / (float)around, j / (float)up);
        int outer = vertices.Count;
        for (int j = 0; j <= up; j++)
            for (int i = 0; i <= around; i++)
            {
                var round = skinAt[i == around ? 1 : i + 1, j] - skinAt[i == 0 ? around - 1 : i - 1, j];
                var rise = skinAt[i, Mathf.Min(up, j + 1)] - skinAt[i, Mathf.Max(0, j - 1)];
                // The foot closes to a point, where there is no way round to measure.
                var n = j == 0 ? Vector3.down : Vector3.Cross(rise, round).normalized;
                Add(skinAt[i, j], n, new Vector2(i / (float)around, (skinAt[i, j].y - bottom) * vPerMeter));
            }
        // The inside shows only through the slit of the mouth: plain film.
        int inner = vertices.Count;
        for (int k = 0; k < inner - outer; k++)
            Add(vertices[outer + k] - normals[outer + k] * film, -normals[outer + k], new Vector2(uvs[outer + k].x, 0.62f + uvs[outer + k].y * 0.2f));
        for (int j = 0; j < up; j++)
            for (int i = 0; i < around; i++)
            {
                int a = outer + j * row + i, b = inner + j * row + i;
                triangles.AddRange(new[] { a, a + row, a + 1, a + 1, a + row, a + row + 1 });
                triangles.AddRange(new[] { b, b + 1, b + row, b + 1, b + row + 1, b + row });
            }

        // The handles: from the rim round each end of the mouth, gathered and taut, up to the hand.
        foreach (int end in new[] { 1, -1 })
        {
            int middle = end > 0 ? 0 : around / 2;
            Vector3 RimAt(int column) => vertices[outer + up * row + (column % around + around) % around];
            var from = RimAt(middle);
            var points = new Vector3[along + 1, across + 1];
            for (int r = 0; r <= along; r++)
            {
                float s = r / (float)along;
                // Nearly straight, bowed out a little where the film still has some width.
                var at = Vector3.Lerp(from, Vector3.zero, s) + Vector3.right * (end * 0.012f * Mathf.Sin(Mathf.PI * s));
                for (int k = 0; k <= across; k++)
                {
                    int step = k - across / 2;
                    var atRim = RimAt(middle + step) - from;
                    // In the hand the strap is a cord a finger wide, lying front to back.
                    var inHand = new Vector3(0f, 0f, 0.007f * step / (across * 0.5f) * (end > 0 ? 1f : -1f));
                    points[r, k] = at + Vector3.Lerp(atRim, inHand, Mathf.Pow(s, 0.6f));
                }
            }
            foreach (float face in new[] { 1f, -1f })
            {
                int start = vertices.Count;
                for (int r = 0; r <= along; r++)
                    for (int k = 0; k <= across; k++)
                    {
                        var alongStrap = points[Mathf.Min(along, r + 1), k] - points[Mathf.Max(0, r - 1), k];
                        var acrossStrap = points[r, Mathf.Min(across, k + 1)] - points[r, Mathf.Max(0, k - 1)];
                        var n = Vector3.Cross(alongStrap, acrossStrap).normalized * face;
                        Add(points[r, k] + n * 0.0003f, n, new Vector2((end > 0 ? 0.02f : 0.52f) + 0.33f * r / along, 0.62f + 0.13f * k / across));
                    }
                int next = across + 1;
                for (int r = 0; r < along; r++)
                    for (int k = 0; k < across; k++)
                    {
                        int a = start + r * next + k;
                        triangles.AddRange(face > 0f ? new[] { a, a + next, a + 1, a + 1, a + next, a + next + 1 } : new[] { a, a + 1, a + next, a + 1, a + next + 1, a + next });
                    }
            }
        }
        for (int i = 0; i < vertices.Count; i++)
            if (float.IsNaN(vertices[i].x + vertices[i].y + vertices[i].z + normals[i].x + normals[i].y + normals[i].z))
                throw new System.Exception($"Carried bag mesh: vertex {i} of {vertices.Count} is not a number.");
        return Save(key, vertices, normals, uvs, triangles);
    }

    static Mesh Save(string key, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, bool lightmapUvs = false)
    {
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
        // For the baked light, when the mesh stays where it is (P3StoreLighting).
        if (lightmapUvs) Unwrapping.GenerateSecondaryUVSet(mesh);
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
