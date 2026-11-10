using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Where the arrow points: its tip, the way it points, and the one or two words under it.</summary>
public struct PointerAim
{
    const float AboveMeters = 0.05f;

    public Vector3 Tip, Direction;
    public string Label;

    /// <summary>Pointing straight down at something from just above it.</summary>
    public static PointerAim Down(Vector3 top, string label) => new PointerAim { Tip = top + Vector3.up * AboveMeters, Direction = Vector3.down, Label = label };

    public static PointerAim Along(Vector3 tip, Vector3 direction, string label) => new PointerAim { Tip = tip, Direction = direction.normalized, Label = label };
}

/// <summary>
/// A yellow arrow over the thing to touch next (2026-11-09). The board above the counter says what to do; someone new to VR
/// is looking at their hands and the counter, so this says where, on the thing itself, from the moment the step begins.
/// Opaque, so it never blends with splats. The steps hand it a function, since what it points at moves.
/// </summary>
public class TargetPointer : MonoBehaviour
{
    const float HeadLength = 0.05f, HeadRadius = 0.032f, ShaftLength = 0.055f, ShaftRadius = 0.012f;
    const int Sides = 14;
    const float BobMeters = 0.018f, BobPerSecond = 1.5f;
    const float LabelGapMeters = 0.035f;
    const float LabelMetersPerPixel = 0.0005f;

    public Material Material;
    public Func<PointerAim?> Aim { get; set; }

    public bool Shown => arrow != null && arrow.gameObject.activeSelf;
    public string Label => labelText != null && labelPanel.gameObject.activeSelf ? labelText.text : "";

    Transform arrow;
    RectTransform labelPanel;
    Text labelText;
    Camera eyes;

    void Awake()
    {
        var go = new GameObject("Arrow", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        go.GetComponent<MeshFilter>().sharedMesh = BuildArrow();
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = Material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        arrow = go.transform;
        arrow.gameObject.SetActive(false);

        labelPanel = WorldUi.CreatePanel(transform, "Arrow Label", transform.position, transform.rotation, new Vector2(280f, 70f), LabelMetersPerPixel, WorldUi.Palette.Panel);
        labelText = WorldUi.AddText(labelPanel, 0f, 0f, 280f, 70f, 42, TextAnchor.MiddleCenter);
        WorldUi.Bold(labelText);
        labelPanel.gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        var wanted = Aim?.Invoke();
        if (arrow.gameObject.activeSelf != wanted.HasValue) arrow.gameObject.SetActive(wanted.HasValue);
        bool hasLabel = wanted.HasValue && !string.IsNullOrEmpty(wanted.Value.Label);
        if (labelPanel.gameObject.activeSelf != hasLabel) labelPanel.gameObject.SetActive(hasLabel);
        if (!wanted.HasValue) return;

        var aim = wanted.Value;
        float bob = BobMeters * (0.5f + 0.5f * Mathf.Sin(Time.time * BobPerSecond * Mathf.PI * 2f));
        var tip = aim.Tip - aim.Direction * bob;
        // Any roll will do: the arrow is round.
        arrow.SetPositionAndRotation(tip, Quaternion.LookRotation(aim.Direction));
        if (!hasLabel) return;

        if (eyes == null) eyes = Camera.main;
        var tail = tip - aim.Direction * (HeadLength + ShaftLength);
        var position = tail + Vector3.up * LabelGapMeters;
        labelPanel.position = position;
        if (eyes != null) labelPanel.rotation = Quaternion.LookRotation(position - eyes.transform.position);
        labelText.text = aim.Label;
    }

    /// <summary>A round arrow along +z with its tip at the origin.</summary>
    static Mesh BuildArrow()
    {
        int ringVertices = Sides + 1;
        var vertices = new Vector3[ringVertices * 5 + 2];
        var normals = new Vector3[vertices.Length];
        var triangles = new System.Collections.Generic.List<int>();
        Vector3 Spoke(int side) => new Vector3(Mathf.Cos(side * Mathf.PI * 2f / Sides), Mathf.Sin(side * Mathf.PI * 2f / Sides), 0f);

        void Ring(int ring, float z, float radius, Func<Vector3, Vector3> normal)
        {
            for (int side = 0; side <= Sides; side++)
            {
                var spoke = Spoke(side);
                vertices[ring * ringVertices + side] = spoke * radius + Vector3.forward * z;
                normals[ring * ringVertices + side] = normal(spoke);
            }
        }

        void Band(int near, int far)
        {
            for (int side = 0; side < Sides; side++)
            {
                int a = near * ringVertices + side, b = near * ringVertices + side + 1, c = far * ringVertices + side, d = far * ringVertices + side + 1;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
        }

        var headSlope = new Vector3(HeadLength, 0f, HeadRadius).normalized;
        Vector3 HeadNormal(Vector3 spoke) => spoke * headSlope.x + Vector3.forward * headSlope.z;
        Ring(0, 0f, 0.0005f, HeadNormal);
        Ring(1, -HeadLength, HeadRadius, HeadNormal);
        Ring(2, -HeadLength, HeadRadius, _ => Vector3.back);
        Ring(3, -HeadLength, ShaftRadius, spoke => spoke);
        Ring(4, -HeadLength - ShaftLength, ShaftRadius, spoke => spoke);
        int centerBack = ringVertices * 5, centerTail = centerBack + 1;
        vertices[centerBack] = Vector3.forward * -HeadLength;
        normals[centerBack] = Vector3.back;
        vertices[centerTail] = Vector3.forward * (-HeadLength - ShaftLength);
        normals[centerTail] = Vector3.back;
        Band(0, 1);
        Band(3, 4);
        for (int side = 0; side < Sides; side++)
        {
            triangles.AddRange(new[] { 2 * ringVertices + side, centerBack, 2 * ringVertices + side + 1 });
            triangles.AddRange(new[] { 4 * ringVertices + side, centerTail, 4 * ringVertices + side + 1 });
        }

        var mesh = new Mesh { name = "Pointer Arrow", vertices = vertices, normals = normals, triangles = triangles.ToArray() };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>The card first, then the slot it goes in. Shared by the time cards and the customer's card.</summary>
    public static PointerAim? AimAtCard(PaymentDesk desk)
    {
        var card = desk.CustomerCard;
        if (card == null) return null;
        if (!card.Grab.isSelected) return PointerAim.Down(card.transform.position, "집기");
        // The slot is in the face toward the clerk, so the arrow comes from the clerk's side.
        var slot = desk.CardSlot.transform.position;
        return PointerAim.Along(slot + new Vector3(0f, 0f, -0.06f), Vector3.forward, "여기에 꽂기");
    }
}
