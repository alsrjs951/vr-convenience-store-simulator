using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Makes the shelf splats from the lighter PLYs of scripts/windows/make_splat_lods.py (doc 4.6, 7.2, 7.3, P4-K1, P4-K4):
/// {id}_LOD1 for box contents and the placement ghost, and per slot Shelf_{slot}_LOD0 (front row near, back row lod1, Medium)
/// and Shelf_{slot}_LOD1 (far, VeryLow).
///
/// A slot holds one product, all facing the same way, so a merged PLY is that product's gaussians repeated with a shift
/// expressed in the product's own frame. The renderer then takes the first place's splat transform. Nothing is rotated,
/// which keeps the view-dependent color (spherical harmonics) correct without having to rotate it.
///
/// Reads the slots from the built scene, so run after P2StoreBuilder.Build, and build again to assign the assets.
/// </summary>
public static class P4ShelfSplats
{
    const string LodFolder = "capture/lod";
    const string WorkFolder = "Temp/ShelfSplats";
    const string ProductFolder = "Assets/GaussianSplats/Products";
    public const string MergedFolder = "Assets/GaussianSplats/Merged";
    const string StoreScene = "Assets/Scenes/StoreScene.unity";

    sealed class Ply
    {
        public string[] Properties;
        public byte[] Bytes;
        public int Start, Count, Stride, X;
    }

    [MenuItem("Tools/VR Store/Generate Shelf Splats")]
    public static void Generate()
    {
        Directory.CreateDirectory(WorkFolder);
        Directory.CreateDirectory(MergedFolder);

        foreach (var path in Directory.GetFiles(LodFolder, "*_lod1.ply"))
        {
            var id = Path.GetFileName(path).Replace("_lod1.ply", "");
            if (Made($"{ProductFolder}/{id}_LOD1.asset")) continue;
            var copy = $"{WorkFolder}/{id}_LOD1.ply";
            File.Copy(path, copy, true);
            CreateAsset(copy, ProductFolder, false);
            Debug.Log($"[ShelfSplats] {id}_LOD1");
        }

        var scene = EditorSceneManager.OpenScene(StoreScene);
        long nearTotal = 0, farTotal = 0;
        int madeSlots = 0;
        foreach (var slot in UnityEngine.Object.FindObjectsByType<ShelfSlotZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // Slots made before keep their assets. A slot whose size has changed needs its Shelf_{id}_* files deleted first.
            if (Made($"{MergedFolder}/Shelf_{slot.SlotId}_LOD1.asset") && (slot.FarOnly || Made($"{MergedFolder}/Shelf_{slot.SlotId}_LOD0.asset"))) continue;
            madeSlots++;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{slot.ProductId}.prefab")?.GetComponent<ProductPrefab>()
                         ?? throw new Exception("No prefab for " + slot.ProductId);
            var splat = prefab.Splat.transform;
            if (splat.parent != prefab.transform) throw new Exception("Splat is not a direct child: " + prefab.name);

            var toProduct = Quaternion.Inverse(slot.UnitRotation * splat.localRotation);
            var near = Clip(Read($"{LodFolder}/{slot.ProductId}_near.ply"), prefab);
            var back = Clip(Read($"{LodFolder}/{slot.ProductId}_lod1.ply"), prefab);
            var far = Clip(Read($"{LodFolder}/{slot.ProductId}_far.ply"), prefab);
            var nearParts = new List<(Ply, Vector3)>();
            var farParts = new List<(Ply, Vector3)>();
            for (int unit = 0; unit < slot.Capacity; unit++)
            {
                var shift = toProduct * (slot.UnitPosition(unit) - slot.UnitPosition(0));
                var offset = new Vector3(shift.x / splat.localScale.x, shift.y / splat.localScale.y, shift.z / splat.localScale.z);
                // Display stock is only ever passed by, so even its front row takes the lighter copy.
                nearParts.Add((!slot.DisplayOnly && unit % slot.Rows == 0 ? near : back, offset));
                farParts.Add((far, offset));
            }
            if (!slot.FarOnly)
            {
                nearTotal += Merge($"{WorkFolder}/Shelf_{slot.SlotId}_LOD0.ply", nearParts);
                CreateAsset($"{WorkFolder}/Shelf_{slot.SlotId}_LOD0.ply", MergedFolder, true);
            }
            farTotal += Merge($"{WorkFolder}/Shelf_{slot.SlotId}_LOD1.ply", farParts);
            CreateAsset($"{WorkFolder}/Shelf_{slot.SlotId}_LOD1.ply", MergedFolder, false);
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[ShelfSplats] PASS new slots={madeSlots} merged near={nearTotal} far={farTotal} in {scene.path}");
    }

    // -p4-regenerate: the product PLYs changed, so every light copy and merged slot is made again over the old files.
    // The .meta files stay, so nothing that points at these assets loses them.
    static readonly bool Regenerate = Array.IndexOf(Environment.GetCommandLineArgs(), "-p4-regenerate") >= 0;

    static bool Made(string assetPath) => !Regenerate && File.Exists(assetPath);

    static Ply Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var properties = new List<string>();
        int count = 0, position = 0;
        while (true)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', position);
            if (end < 0) throw new Exception("No header end in " + path);
            var line = Encoding.ASCII.GetString(bytes, position, end - position).Trim();
            position = end + 1;
            if (line.StartsWith("element vertex")) count = int.Parse(line.Substring(15));
            else if (line.StartsWith("property"))
            {
                var parts = line.Split(' ');
                if (parts[1] != "float") throw new Exception($"Unexpected type {parts[1]} in {path}");
                properties.Add(parts[2]);
            }
            else if (line == "end_header") break;
        }
        int x = properties.IndexOf("x");
        if (x < 0 || properties.IndexOf("y") != x + 1 || properties.IndexOf("z") != x + 2) throw new Exception("x, y, z are not together in " + path);
        var ply = new Ply { Properties = properties.ToArray(), Bytes = bytes, Start = position, Count = count, Stride = properties.Count * 4, X = x * 4 };
        if (bytes.Length - position != count * ply.Stride) throw new Exception("Unexpected body size in " + path);
        return ply;
    }

    /// <summary>Keeps the gaussians inside the volume the place's cutout hides, so an empty place shows nothing left over.</summary>
    static Ply Clip(Ply ply, ProductPrefab prefab)
    {
        var volume = SplatShelfSlot.PlaceVolume(prefab.Proxy);
        var splat = prefab.Splat.transform;
        var toRoot = Matrix4x4.TRS(splat.localPosition, splat.localRotation, splat.localScale);
        var kept = new byte[ply.Count * ply.Stride];
        int count = 0;
        for (int i = 0; i < ply.Count; i++)
        {
            int at = ply.Start + i * ply.Stride;
            var position = new Vector3(BitConverter.ToSingle(ply.Bytes, at + ply.X), BitConverter.ToSingle(ply.Bytes, at + ply.X + 4), BitConverter.ToSingle(ply.Bytes, at + ply.X + 8));
            if (!volume.Contains(toRoot.MultiplyPoint3x4(position))) continue;
            Buffer.BlockCopy(ply.Bytes, at, kept, count * ply.Stride, ply.Stride);
            count++;
        }
        Debug.Log($"[ShelfSplats] {prefab.name}: kept {count} of {ply.Count} gaussians inside the place");
        if (count < ply.Count * 0.8f) throw new Exception($"{prefab.name}: the place volume cuts too much ({count}/{ply.Count}).");
        Array.Resize(ref kept, count * ply.Stride);
        return new Ply { Properties = ply.Properties, Bytes = kept, Start = 0, Count = count, Stride = ply.Stride, X = ply.X };
    }

    static long Merge(string path, List<(Ply ply, Vector3 offset)> parts)
    {
        var layout = parts[0].ply;
        int total = 0;
        foreach (var (ply, _) in parts)
        {
            if (string.Join(",", ply.Properties) != string.Join(",", layout.Properties)) throw new Exception("Different PLY layouts for " + path);
            total += ply.Count;
        }
        var header = new StringBuilder("ply\nformat binary_little_endian 1.0\n");
        header.Append($"element vertex {total}\n");
        foreach (var property in layout.Properties) header.Append($"property float {property}\n");
        header.Append("end_header\n");
        var head = Encoding.ASCII.GetBytes(header.ToString());
        var output = new byte[head.Length + total * layout.Stride];
        Buffer.BlockCopy(head, 0, output, 0, head.Length);

        int at = head.Length;
        foreach (var (ply, offset) in parts)
        {
            int length = ply.Count * ply.Stride;
            Buffer.BlockCopy(ply.Bytes, ply.Start, output, at, length);
            for (int i = 0; i < ply.Count; i++)
            {
                int field = at + i * ply.Stride + ply.X;
                for (int axis = 0; axis < 3; axis++)
                {
                    var span = new Span<byte>(output, field + axis * 4, 4);
                    BitConverter.TryWriteBytes(span, BitConverter.ToSingle(span) + offset[axis]);
                }
            }
            at += length;
        }
        File.WriteAllBytes(path, output);
        return total;
    }

    static void CreateAsset(string ply, string folder, bool medium)
    {
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            var so = new SerializedObject(creator);
            so.FindProperty("m_InputFile").stringValue = Path.GetFullPath(ply);
            so.FindProperty("m_OutputFolder").stringValue = folder;
            // The plugin's Medium and VeryLow presets (doc 7.3).
            SetFormat(so, "m_FormatPos", "Norm11");
            SetFormat(so, "m_FormatScale", medium ? "Norm11" : "Norm6");
            SetFormat(so, "m_FormatColor", medium ? "Norm8x4" : "BC7");
            SetFormat(so, "m_FormatSH", medium ? "Norm6" : "Cluster4k");
            so.ApplyModifiedPropertiesWithoutUndo();
            typeof(GaussianSplatAssetCreator).GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(creator, null);
            var error = typeof(GaussianSplatAssetCreator).GetField("m_ErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(creator) as string;
            if (!string.IsNullOrEmpty(error)) throw new Exception($"{ply}: {error}");
        }
        finally { UnityEngine.Object.DestroyImmediate(creator); }
    }

    static void SetFormat(SerializedObject so, string field, string value)
    {
        var property = so.FindProperty(field);
        int index = Array.IndexOf(property.enumNames, value);
        if (index < 0) throw new Exception($"{field} has no {value}");
        property.enumValueIndex = index;
    }
}
