using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The look of 동아편의점 (2026-11-08): a bright, clean Korean convenience store in white with Dong-A navy (Pantone 282C)
/// as the one accent. Free external assets (Assets/ThirdParty/StoreAssets, README there), the store's own generated
/// textures (Assets/Art/Generated, scripts/windows/make_store_textures.py) and rounded meshes (P3Meshes).
///
/// It changes looks, not rules: every gameplay collider keeps its size and place. New solid things (the fridge wall,
/// shelf C, the microwave corner, the stock shelf) stand where customers do not path and the locomotion check does not
/// push. P2StoreBuilder runs Dress before the NavMesh bake and DressCounter at the end.
/// </summary>
public static class P3StoreDressing
{
    const string Root = "Assets/ThirdParty/StoreAssets";
    const string Generated = "Assets/Art/Generated";
    const string MaterialFolder = "Assets/Materials/P3";
    const string P2Materials = "Assets/Materials/P2";
    const float CeilingHeight = 3f;

    // Store inside faces (P2StoreBuilder: 9 m x 8 m room, walls 0.1 m thick at x = +-4.5, z = -2.5 and 5.5).
    const float InnerLeft = -4.44f, InnerRight = 4.44f, InnerFront = 5.44f, InnerBack = -2.44f;
    // From P2StoreBuilder: gondola fronts and backs, the door in the left wall.
    const float ShelfFront = 2.95f, ShelfBack = 3.45f, ShelfWidth = 1.5f;
    const float DoorNear = 1.2f, DoorFar = 3.6f;

    static readonly Color Navy = new Color(0.016f, 0.118f, 0.259f);
    static readonly Color Grey = new Color(0.635f, 0.667f, 0.678f);
    static readonly Color White = new Color(0.95f, 0.95f, 0.94f);

    static Material navy, white, grey, graphite, steel, led;

    public static void Dress(Transform environment)
    {
        Directory.CreateDirectory(MaterialFolder);
        ConfigureImports();

        // ---------- Surfaces: the P2 materials change, so every box that uses them changes at once ----------
        ApplyPbr(Load<Material>($"{P2Materials}/P2_Floor.mat"), $"{Generated}/floor_tile", new Vector2(9f / 1.2f, 8f / 1.2f), Color.white);
        Paint(Load<Material>($"{P2Materials}/P2_Wall.mat"), $"{Generated}/wall_paint", new Color(0.96f, 0.96f, 0.95f), 0.22f, new Vector2(6f, 2f), 0.35f);
        Paint(Load<Material>($"{P2Materials}/P2_Shelf.mat"), $"{Root}/AmbientCG/Metal027/Metal027", new Color(0.97f, 0.97f, 0.97f), 0.5f, Vector2.one, 0.25f);
        Paint(Load<Material>($"{P2Materials}/P2_Counter.mat"), null, new Color(0.93f, 0.925f, 0.91f), 0.35f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Device.mat"), null, new Color(0.055f, 0.06f, 0.07f), 0.62f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Stand.mat"), null, Grey, 0.4f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Bin.mat"), null, new Color(0.38f, 0.4f, 0.42f), 0.45f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_RecycleBin.mat"), null, Navy, 0.4f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_WarehouseDoor.mat"), null, new Color(0.62f, 0.64f, 0.67f), 0.45f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Bag.mat"), null, new Color(0.98f, 0.98f, 0.97f), 0.55f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Porch.mat"), null, new Color(0.22f, 0.23f, 0.25f), 0.3f, Vector2.one, 0f);
        ApplyPbr(Load<Material>($"{P2Materials}/P2_Cardboard.mat"), $"{Root}/PolyHaven/cardboard_box_01/cardboard_side", Vector2.one, Color.white);

        navy = Plain("P3_Navy", Navy, 0.45f);
        white = Plain("P3_White", White, 0.3f);
        grey = Plain("P3_Grey", Grey, 0.4f);
        graphite = Plain("P3_Graphite", new Color(0.05f, 0.055f, 0.065f), 0.7f);
        steel = Plain("P3_Steel", new Color(0.78f, 0.8f, 0.83f), 0.7f, 1f);
        led = Lit("P3_LedPanel", $"{Generated}/led_panel", Vector2.one, Color.white, 2.2f);

        // The lit panels do not light anything by themselves (no baked GI): a bright, even indoor ambient stands in for
        // them. The Gaussian splat products are unlit and do not change.
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.9f, 0.91f, 0.93f);
        RenderSettings.ambientEquatorColor = new Color(0.78f, 0.78f, 0.79f);
        RenderSettings.ambientGroundColor = new Color(0.45f, 0.44f, 0.43f);

        var dressing = new GameObject("Store Dressing").transform;
        dressing.SetParent(environment, false);

        // Rounded edges on the furniture boxes the builder made (colliders keep their size).
        foreach (var t in environment.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name;
            if (n.StartsWith("Shelf A ") || n.StartsWith("Shelf B ")) P3Meshes.Round(t.gameObject, n.EndsWith("Spine") || n.EndsWith("Base") ? 0.012f : 0.007f);
            else if (n.StartsWith("Stand ") && n.EndsWith(" Body")) P3Meshes.Round(t.gameObject, 0.02f);
            else if (n == "Cart Body") P3Meshes.Round(t.gameObject, 0.03f);
        }

        Ceiling(dressing);
        Baseboards(dressing);
        foreach (var (x, title) in new[] { (-2.2f, "라면 · 과자"), (2.2f, "즉석식품") }) GondolaTrim(dressing, x, title);
        FridgeWall(dressing);
        ShelfC(dressing);
        MicrowaveCorner(dressing);

        // The store name on the back wall, for the customers' side (left of the warehouse door).
        Sign(dressing, "Back Wall Sign", new Vector3(-0.6f, 2.45f, InnerBack + 0.004f), Quaternion.Euler(0f, 180f, 0f), new Vector2(3.2f, 0.52f), "동아편의점", "", Navy, Color.white, false);

        // ---------- Behind the counter: stock shelf with a crate, a hand truck (kept from the first pass) ----------
        var shelf = Place($"{Root}/PolyHaven/steel_frame_shelves_01/steel_frame_shelves_01.fbx", "Stock Shelf", dressing, 90f,
            _ => Lit("P3_SteelShelves", $"{Root}/PolyHaven/steel_frame_shelves_01/steel_frame_shelves_01", Vector2.one, Color.white));
        Align(shelf, new Vector3(InnerLeft + 0.01f, 0f, -1.2f), new Vector3(0f, 0f, 0.5f));
        AddBoxCollider(shelf);
        var shelfBounds = Bounds(shelf);
        var crate = Place($"{Root}/PolyHaven/industrial_pastic_container/industrial_pastic_container.fbx", "Crate On Shelf", dressing, 90f,
            _ => Lit("P3_PlasticContainer", $"{Root}/PolyHaven/industrial_pastic_container/industrial_pastic_container", Vector2.one, Color.white));
        Align(crate, new Vector3(shelfBounds.center.x, shelfBounds.max.y, shelfBounds.center.z), new Vector3(0.5f, 0f, 0.5f));
        FitWithin(crate, new Vector2(shelfBounds.size.x - 0.04f, shelfBounds.size.z - 0.04f));
        var truck = Place($"{Root}/PolyHaven/hand_truck/hand_truck.fbx", "Hand Truck", dressing, 90f,
            _ => Lit("P3_HandTruck", $"{Root}/PolyHaven/hand_truck/hand_truck", Vector2.one, Color.white));
        Align(truck, new Vector3(InnerLeft + 0.02f, 0f, 0.1f), new Vector3(0f, 0f, 0.5f));

        foreach (var placed in new[] { "Stock Shelf", "Hand Truck", "Drinks Fridge 1", "Shelf C", "Microwave Cabinet" })
        {
            var t = dressing.Find(placed);
            if (t == null) continue;
            var b = Bounds(t.gameObject);
            Debug.Log($"[P3] {placed}: center {b.center.ToString("F2")} size {b.size.ToString("F2")}");
        }
    }

    // ---------- Room ----------

    static void Ceiling(Transform parent)
    {
        var ceiling = Quad("Ceiling", parent, new Vector3(0f, CeilingHeight, 1.5f), new Vector2(9f, 8f), Emissive("P3_Ceiling", new Color(0.96f, 0.96f, 0.96f), new Color(0.42f, 0.42f, 0.43f)));
        ceiling.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        // 60 cm LED panels: two rows over the shop floor, one behind the counter.
        foreach (var x in new[] { -3.0f, -1.0f, 1.0f, 3.0f })
            foreach (var z in new[] { 1.9f, 3.95f })
                Quad("LED Panel", parent, new Vector3(x, CeilingHeight - 0.004f, z), new Vector2(0.6f, 0.6f), led).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        foreach (var x in new[] { -2.4f, 0f, 2.4f })
            Quad("LED Panel", parent, new Vector3(x, CeilingHeight - 0.004f, -1.1f), new Vector2(0.6f, 0.6f), led).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>A downward-facing quad: seen from below, invisible from above (overhead evidence cameras still see in).</summary>
    static GameObject Quad(string name, Transform parent, Vector3 center, Vector2 size, Material material)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        quad.transform.SetParent(parent, false);
        quad.transform.SetPositionAndRotation(center, Quaternion.Euler(-90f, 0f, 0f));
        quad.transform.localScale = new Vector3(size.x, size.y, 1f);
        UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.GetComponent<Renderer>().sharedMaterial = material;
        return quad;
    }

    /// <summary>Navy skirting along the walls the customer sees (the front wall is behind the fridges).</summary>
    static void Baseboards(Transform parent)
    {
        const float h = 0.1f, t = 0.012f;
        Visual("Skirting Back", parent, new Vector3(0f, h * 0.5f, InnerBack + t * 0.5f), new Vector3(InnerRight - InnerLeft, h, t), navy, 0.003f);
        Visual("Skirting Right", parent, new Vector3(InnerRight - t * 0.5f, h * 0.5f, 1.5f), new Vector3(t, h, InnerFront - InnerBack), navy, 0.003f);
        Visual("Skirting Left Back", parent, new Vector3(InnerLeft + t * 0.5f, h * 0.5f, (InnerBack + DoorNear) * 0.5f), new Vector3(t, h, DoorNear - InnerBack), navy, 0.003f);
        Visual("Skirting Left Front", parent, new Vector3(InnerLeft + t * 0.5f, h * 0.5f, (DoorFar + InnerFront) * 0.5f), new Vector3(t, h, InnerFront - DoorFar), navy, 0.003f);
        // A navy frame round the doorway so the entrance reads from across the store.
        Visual("Door Frame Near", parent, new Vector3(InnerLeft + 0.02f, 1.1f, DoorNear + 0.03f), new Vector3(0.05f, 2.2f, 0.06f), navy, 0.01f);
        Visual("Door Frame Far", parent, new Vector3(InnerLeft + 0.02f, 1.1f, DoorFar - 0.03f), new Vector3(0.05f, 2.2f, 0.06f), navy, 0.01f);
        Visual("Door Frame Top", parent, new Vector3(InnerLeft + 0.02f, 2.23f, (DoorNear + DoorFar) * 0.5f), new Vector3(0.05f, 0.06f, DoorFar - DoorNear), navy, 0.01f);
        Sign(parent, "Door Sign", new Vector3(InnerLeft + 0.004f, 2.5f, (DoorNear + DoorFar) * 0.5f), Quaternion.Euler(0f, -90f, 0f), new Vector2(1.6f, 0.3f), "동아편의점", "", Navy, Color.white, false);
    }

    // ---------- Gondolas A and B: price rails, plinth, topper ----------

    static void GondolaTrim(Transform parent, float x, string title)
    {
        var root = new GameObject("Gondola Trim " + title).transform;
        root.SetParent(parent, false);
        float w = ShelfWidth - 0.04f;
        foreach (var top in new[] { 0.1f, 0.55f, 1.0f, 1.4f })
        {
            Visual("Price Rail Front", root, new Vector3(x, top - 0.021f, ShelfFront - 0.004f), new Vector3(w, 0.035f, 0.008f), navy, 0.002f);
            Visual("Price Rail Back", root, new Vector3(x, top - 0.021f, ShelfBack + 0.004f), new Vector3(w, 0.035f, 0.008f), navy, 0.002f);
        }
        Visual("Plinth Front", root, new Vector3(x, 0.045f, ShelfFront - 0.003f), new Vector3(ShelfWidth, 0.09f, 0.006f), navy, 0.002f);
        Visual("Plinth Back", root, new Vector3(x, 0.045f, ShelfBack + 0.003f), new Vector3(ShelfWidth, 0.09f, 0.006f), navy, 0.002f);
        float z = (ShelfFront + ShelfBack) * 0.5f;
        Visual("Topper", root, new Vector3(x, 1.885f, z), new Vector3(ShelfWidth, 0.17f, 0.02f), navy, 0.006f);
        Sign(root, "Topper Sign", new Vector3(x, 1.885f, z), Quaternion.identity, new Vector2(ShelfWidth - 0.06f, 0.15f), title, "", new Color(0, 0, 0, 0), Color.white, true, 0.012f);
    }

    // ---------- Fridge wall ----------

    static void FridgeWall(Transform parent)
    {
        // The OBJ keeps the glTF material names as its slots: FridgeCase (case and door frame), FridgeInterior, FridgeGlass.
        var fridgeCase = Lit("P3_FridgeCase", $"{Root}/Khronos/CommercialRefrigerator/FridgeCase", Vector2.one, Color.white);
        var fridgeInterior = Lit("P3_FridgeInterior", $"{Root}/Khronos/CommercialRefrigerator/FridgeInterior", Vector2.one, Color.white, 1f);
        var fridgeGlass = Glass("P3_FridgeGlass");
        Func<string, Material> material = slot => slot.Contains("Glass") ? fridgeGlass : slot.Contains("Interior") ? fridgeInterior : fridgeCase;
        const int count = 10;
        const float gap = 0.01f;
        float width = 0f, depth = 0f, height = 0f;
        for (int i = 0; i < count; i++)
        {
            var fridge = Place($"{Root}/Khronos/CommercialRefrigerator/CommercialRefrigerator.obj", $"Drinks Fridge {i + 1}", parent, 180f, material);
            var size = Bounds(fridge).size;
            width = size.x; depth = size.z; height = size.y;
            float total = count * size.x + (count - 1) * gap;
            Align(fridge, new Vector3(total * 0.5f - i * (size.x + gap), 0f, InnerFront - 0.005f), new Vector3(1f, 0f, 1f));
            AddBoxCollider(fridge);
        }
        float wall = count * width + (count - 1) * gap;
        float front = InnerFront - 0.005f - depth;
        // White soffit over the fridges up to the ceiling, with the navy name band on its face.
        Visual("Fridge Soffit", parent, new Vector3(0f, (height + CeilingHeight) * 0.5f, (front + InnerFront) * 0.5f), new Vector3(InnerRight - InnerLeft, CeilingHeight - height, InnerFront - front), white, 0.004f);
        Visual("Fridge Band", parent, new Vector3(0f, height + 0.36f, front - 0.006f), new Vector3(InnerRight - InnerLeft, 0.46f, 0.012f), navy, 0.003f);
        Sign(parent, "Fridge Band Name", new Vector3(-wall * 0.5f + 1.5f, height + 0.36f, front - 0.014f), Quaternion.identity, new Vector2(2.8f, 0.42f), "동아편의점", "", new Color(0, 0, 0, 0), Color.white, false);
        Sign(parent, "Fridge Band Section", new Vector3(wall * 0.5f - 1.6f, height + 0.36f, front - 0.014f), Quaternion.identity, new Vector2(3f, 0.42f), "음료 · 유제품", "", new Color(0, 0, 0, 0), new Color(0.8f, 0.86f, 0.95f), false, 0.002f, 0.5f);
    }

    // ---------- Right wall: shelf C and the microwave / hot water corner ----------

    static void ShelfC(Transform parent)
    {
        const float z0 = 2.6f, z1 = 4.42f, depth = 0.45f, height = 1.8f;
        float x0 = InnerRight - depth;
        var root = new GameObject("Shelf C").transform;
        root.SetParent(parent, false);
        float zc = (z0 + z1) * 0.5f, len = z1 - z0;
        Visual("Back Panel", root, new Vector3(InnerRight - 0.015f, height * 0.5f, zc), new Vector3(0.03f, height, len), white, 0.006f);
        Visual("Side Near", root, new Vector3(x0 + depth * 0.5f, height * 0.5f, z0 + 0.01f), new Vector3(depth, height, 0.02f), white, 0.006f);
        Visual("Side Far", root, new Vector3(x0 + depth * 0.5f, height * 0.5f, z1 - 0.01f), new Vector3(depth, height, 0.02f), white, 0.006f);
        foreach (var top in new[] { 0.1f, 0.5f, 0.9f, 1.3f, 1.78f })
        {
            Visual("Board", root, new Vector3(x0 + depth * 0.5f, top - 0.01f, zc), new Vector3(depth, 0.02f, len - 0.04f), white, 0.006f);
            if (top < 1.7f) Visual("Price Rail", root, new Vector3(x0 - 0.004f, top - 0.021f, zc), new Vector3(0.008f, 0.035f, len - 0.06f), navy, 0.002f);
        }
        Visual("Plinth", root, new Vector3(x0 - 0.003f, 0.045f, zc), new Vector3(0.006f, 0.09f, len), navy, 0.002f);
        var collider = root.gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3(x0 + depth * 0.5f, height * 0.5f, zc);
        collider.size = new Vector3(depth, height, len);
        Visual("Topper", root, new Vector3(x0 + depth * 0.5f, 1.885f, zc), new Vector3(0.02f, 0.17f, len), navy, 0.006f);
        Sign(root, "Topper Sign", new Vector3(x0 + depth * 0.5f - 0.012f, 1.885f, zc), Quaternion.Euler(0f, 90f, 0f), new Vector2(len - 0.1f, 0.15f), "생활용품", "", new Color(0, 0, 0, 0), Color.white, false);
    }

    // Where Day 3's rule zones go (P2StoreBuilder.BuildCookingZones). The cabinet top is the floor the products rest on.
    const float CabinetTop = 0.9f;
    static readonly Vector3 MicrowaveCenter = new Vector3(InnerRight - 0.2f, CabinetTop + 0.15f, 1.45f);
    static readonly Vector3 DispenserCenter = new Vector3(InnerRight - 0.17f, CabinetTop + 0.23f, 2.02f);
    public static readonly Vector3 MicrowaveCavity = new Vector3(MicrowaveCenter.x - 0.015f, MicrowaveCenter.y, 1.515f);
    public static readonly Vector3 MicrowaveCavitySize = new Vector3(0.35f, 0.3f, 0.38f);
    public static readonly Vector3 UnderTap = DispenserCenter + new Vector3(-0.2f, -0.13f, 0f);
    public static readonly Vector3 UnderTapSize = new Vector3(0.24f, 0.26f, 0.26f);

    static void MicrowaveCorner(Transform parent)
    {
        const float z0 = 1.05f, z1 = 2.35f, depth = 0.5f, top = CabinetTop;
        float x0 = InnerRight - depth, xc = InnerRight - depth * 0.5f, zc = (z0 + z1) * 0.5f;
        var root = new GameObject("Microwave Cabinet").transform;
        root.SetParent(parent, false);
        var body = Visual("Cabinet", root, new Vector3(xc, (top - 0.04f) * 0.5f, zc), new Vector3(depth, top - 0.04f, z1 - z0), white, 0.01f);
        Visual("Cabinet Kick", root, new Vector3(x0 - 0.002f, 0.04f, zc), new Vector3(0.006f, 0.08f, z1 - z0 - 0.02f), navy, 0.002f);
        Visual("Cabinet Top", root, new Vector3(xc - 0.01f, top - 0.02f, zc), new Vector3(depth + 0.02f, 0.04f, z1 - z0 + 0.02f), grey, 0.008f);
        var collider = root.gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3(xc, top * 0.5f, zc);
        collider.size = new Vector3(depth, top, z1 - z0);
        _ = body;

        // Microwave: a hollow steel box you can really put something into, opening toward the aisle, with the keypad on the
        // clerk's right and the door hinged on the left. No colliders on it: a held product is not stopped by the walls, and
        // one let go rests on the cabinet top, which is the oven's floor.
        var mw = MicrowaveCenter;
        Visual("Microwave Top", root, mw + new Vector3(0f, 0.14f, 0f), new Vector3(0.36f, 0.02f, 0.52f), steel, 0.008f);
        Visual("Microwave Floor", root, mw + new Vector3(0f, -0.147f, 0f), new Vector3(0.36f, 0.006f, 0.52f), steel, 0.002f);
        Visual("Microwave Back", root, mw + new Vector3(0.17f, 0f, 0f), new Vector3(0.02f, 0.28f, 0.52f), steel, 0.004f);
        Visual("Microwave Far Side", root, mw + new Vector3(0f, 0f, 0.25f), new Vector3(0.36f, 0.28f, 0.02f), steel, 0.004f);
        Visual("Microwave Keypad Block", root, mw + new Vector3(0f, 0f, -0.19f), new Vector3(0.36f, 0.28f, 0.14f), steel, 0.004f);
        Visual("Microwave Keypad", root, mw + new Vector3(-0.181f, 0f, -0.19f), new Vector3(0.006f, 0.24f, 0.1f), navy, 0.004f);

        // The door turns around its hinge; shut is the rotation it is built in. MicrowaveZone opens it for an order that asks.
        var hinge = new GameObject("Microwave Door Hinge").transform;
        hinge.SetParent(root, false);
        hinge.position = mw + new Vector3(-0.185f, 0f, 0.255f);
        float doorX = mw.x - 0.19f, doorZ = mw.z + 0.065f;
        foreach (var (partName, offset, size) in new[]
        {
            ("Microwave Door Top", new Vector3(0f, 0.125f, 0f), new Vector3(0.012f, 0.024f, 0.36f)),
            ("Microwave Door Bottom", new Vector3(0f, -0.125f, 0f), new Vector3(0.012f, 0.024f, 0.36f)),
            ("Microwave Door Hinge Side", new Vector3(0f, 0f, 0.168f), new Vector3(0.012f, 0.226f, 0.024f)),
            ("Microwave Door Free Side", new Vector3(0f, 0f, -0.168f), new Vector3(0.012f, 0.226f, 0.024f)),
        })
            Visual(partName, hinge, new Vector3(doorX, mw.y, doorZ) + offset, size, graphite, 0.003f);
        Visual("Microwave Door Glass", hinge, new Vector3(doorX, mw.y, doorZ), new Vector3(0.004f, 0.23f, 0.31f), Tinted("P3_MicrowaveGlass", new Color(0.04f, 0.05f, 0.06f, 0.55f)), 0.001f);
        Visual("Microwave Handle", hinge, new Vector3(doorX - 0.012f, mw.y, doorZ - 0.155f), new Vector3(0.016f, 0.2f, 0.016f), steel, 0.006f);

        // On only while it runs (MicrowaveZone.LitParts): the oven light on the ceiling and a warm tint in the window.
        var lit = new GameObject("Microwave Lit").transform;
        lit.SetParent(root, false);
        Visual("Microwave Lamp", lit, MicrowaveCavity + new Vector3(0f, 0.127f, 0f), new Vector3(0.14f, 0.006f, 0.14f), Emissive("P3_MicrowaveLamp", new Color(1f, 0.85f, 0.6f), new Color(2.4f, 1.8f, 0.9f)), 0.002f);
        Visual("Microwave Glow", lit, new Vector3(doorX + 0.004f, mw.y, doorZ), new Vector3(0.002f, 0.23f, 0.31f), Tinted("P3_MicrowaveGlow", new Color(1f, 0.72f, 0.3f, 0.28f)), 0.0005f);
        lit.gameObject.SetActive(false);

        // Hot water dispenser: white body, navy front with a chrome tap and a drip tray.
        var hw = DispenserCenter;
        Visual("Dispenser Body", root, hw, new Vector3(0.3f, 0.46f, 0.28f), white, 0.03f);
        Visual("Dispenser Front", root, hw + new Vector3(-0.151f, 0.08f, 0f), new Vector3(0.006f, 0.22f, 0.22f), navy, 0.005f);
        Visual("Dispenser Hot", root, hw + new Vector3(-0.156f, 0.13f, -0.05f), new Vector3(0.006f, 0.03f, 0.04f), Plain("P3_Red", new Color(0.85f, 0.18f, 0.16f), 0.5f), 0.004f);
        Visual("Dispenser Tap", root, hw + new Vector3(-0.17f, -0.05f, 0f), new Vector3(0.04f, 0.03f, 0.03f), steel, 0.008f);
        Visual("Dispenser Tray", root, hw + new Vector3(-0.17f, -0.215f, 0f), new Vector3(0.1f, 0.02f, 0.18f), grey, 0.005f);
        // From the tap's mouth to the tray; shown while pouring (WaterDispenserZone.Stream).
        Visual("Dispenser Water", root, hw + new Vector3(-0.17f, -0.135f, 0f), new Vector3(0.012f, 0.14f, 0.012f), Plain("P3_HotWater", new Color(0.82f, 0.91f, 1f), 0.92f), 0.005f).SetActive(false);
        Sign(root, "Corner Sign", new Vector3(InnerRight - 0.004f, 1.72f, zc), Quaternion.Euler(0f, 90f, 0f), new Vector2(1.2f, 0.2f), "전자레인지 · 온수", "", Navy, Color.white, false);
    }

    // ---------- Counter (after the builder made it): looks only ----------

    /// <summary>
    /// A fixed bioptic scanner, as at a supermarket checkout: the flat window on the counter (the builder's pad, whose
    /// collider and scan zone stay as they are) in a steel frame, joined by a base to an angular tower behind it, with a
    /// tall smoked window facing the clerk, a green ready light and side vents. All added parts are looks only, with no
    /// collider, so scanning and the hand's paths over the pad are unchanged. The tower stands a few centimetres behind the
    /// window so a product lying on the pad does not reach into it.
    /// </summary>
    static void BiopticScanner(Transform environment, Transform parent)
    {
        const float towerGap = 0.03f, towerDepth = 0.14f, towerTopDepth = 0.06f, towerHeight = 0.3f, towerLean = 0.05f;
        var body = Plain("P3_ScannerBody", new Color(0.16f, 0.17f, 0.19f), 0.5f, 0.15f);
        var frame = Plain("P3_ScannerSteel", new Color(0.56f, 0.58f, 0.61f), 0.82f, 0.5f);
        var smoked = Emissive("P3_ScannerTowerGlass", new Color(0.01f, 0.012f, 0.015f), new Color(0.018f, 0.002f, 0.0015f));
        smoked.SetFloat("_Smoothness", 0.96f);
        var ready = Emissive("P3_ScannerReady", new Color(0.1f, 0.65f, 0.2f), new Color(0.18f, 1.7f, 0.3f));

        // The flat window: steel housing, the red-star glass (it flashes green on a scan), a thin steel rim.
        var pad = Find(environment, "Scanner Pad");
        var size = pad.transform.localScale;
        P3Meshes.Round(pad, 0.008f);
        pad.GetComponent<Renderer>().sharedMaterial = frame;
        var glass = Lit("P3_ScannerGlass", $"{Generated}/scanner_glass", Vector2.one, Color.white, 1.6f);
        glass.SetFloat("_Smoothness", 0.95f);
        Find(environment, "Scanner Red Mark").GetComponent<Renderer>().sharedMaterial = glass;
        var p = pad.transform.position;
        float top = p.y + size.y * 0.5f, bottom = p.y - size.y * 0.5f, back = p.z + size.z * 0.5f;
        foreach (var (offset, rim) in new[]
        {
            (new Vector3(0f, 0f, -size.z * 0.5f + 0.005f), new Vector3(size.x, 0.003f, 0.01f)),
            (new Vector3(0f, 0f, size.z * 0.5f - 0.005f), new Vector3(size.x, 0.003f, 0.01f)),
            (new Vector3(-size.x * 0.5f + 0.005f, 0f, 0f), new Vector3(0.01f, 0.003f, size.z)),
            (new Vector3(size.x * 0.5f - 0.005f, 0f, 0f), new Vector3(0.01f, 0.003f, size.z)),
        })
            Visual("Scanner Rim", parent, new Vector3(p.x, top + 0.0015f, p.z) + offset, rim, frame, 0.001f);

        // The base from the window back under the tower, flush with the window's top.
        float towerFront = back + towerGap;
        float baseFront = back - 0.004f, baseBack = towerFront + towerDepth + 0.01f;
        Visual("Scanner Base", parent, new Vector3(p.x, p.y, (baseFront + baseBack) * 0.5f), new Vector3(size.x, size.y, baseBack - baseFront), frame, 0.006f);

        // The tower: a prism leaning away from the clerk, deep at the foot and narrow on top.
        var profile = new[]
        {
            new Vector2(0f, 0f), new Vector2(towerDepth, 0f),
            new Vector2(towerLean + towerTopDepth, towerHeight), new Vector2(towerLean, towerHeight),
        };
        var tower = new GameObject("Scanner Tower", typeof(MeshFilter), typeof(MeshRenderer));
        tower.transform.SetParent(parent, false);
        tower.transform.position = new Vector3(p.x, bottom, towerFront);
        tower.GetComponent<MeshFilter>().sharedMesh = P3Meshes.Prism(profile, size.x);
        tower.GetComponent<MeshRenderer>().sharedMaterial = body;

        // On the front face (it leans back by atan(lean / height)): the smoked window and the ready light.
        float slant = Mathf.Atan2(towerLean, towerHeight);
        var faceNormal = new Vector3(0f, Mathf.Sin(slant), -Mathf.Cos(slant));
        var faceRotation = Quaternion.Euler(slant * Mathf.Rad2Deg, 0f, 0f);
        var origin = tower.transform.position;
        Vector3 OnFront(float y, float outward) => origin + new Vector3(0f, y, towerLean * y / towerHeight) + faceNormal * outward;
        Visual("Scanner Tower Window", parent, OnFront(0.165f, 0.002f), new Vector3(size.x - 0.03f, 0.19f, 0.004f), smoked, 0.003f).transform.rotation = faceRotation;
        Visual("Scanner Ready Light", parent, OnFront(towerHeight - 0.017f, 0.003f) + new Vector3(size.x * 0.3f, 0f, 0f), new Vector3(0.028f, 0.008f, 0.004f), ready, 0.002f).transform.rotation = faceRotation;

        // Vent slots low on both sides, toward the back.
        foreach (float side in new[] { -1f, 1f })
            for (int i = 0; i < 5; i++)
                Visual("Scanner Vent", parent, origin + new Vector3(side * (size.x * 0.5f + 0.0012f), 0.1f + i * 0.022f, 0.09f), new Vector3(0.003f, 0.006f, 0.045f), graphite, 0.001f);
    }

    /// <summary>The counter, the devices on it and the two screens' housings. Colliders and zones are untouched.</summary>
    public static void DressCounter(Transform environment, Vector3 posScreen, Vector3 orderScreen, Vector3 eye)
    {
        var parent = new GameObject("Counter Dressing").transform;
        parent.SetParent(environment, false);
        var counterTop = Plain("P3_CounterTop", new Color(0.97f, 0.97f, 0.965f), 0.72f);
        var screenGlow = Emissive("P3_TerminalScreen", new Color(0.06f, 0.16f, 0.32f), new Color(0.12f, 0.32f, 0.7f) * 0.9f);

        // Counter: warm white body, white top, navy front with the store name toward the customers.
        var counter = Find(environment, "Counter");
        counter.GetComponent<Renderer>().enabled = false;
        var c = counter.transform.position;
        var size = counter.transform.localScale;
        var counterBody = Load<Material>($"{P2Materials}/P2_Counter.mat");
        Visual("Counter Body", parent, new Vector3(c.x, 0.08f + (size.y - 0.115f) * 0.5f, c.z), new Vector3(size.x - 0.02f, size.y - 0.115f, size.z - 0.02f), counterBody, 0.012f);
        Visual("Counter Kick", parent, new Vector3(c.x, 0.04f, c.z - 0.01f), new Vector3(size.x - 0.06f, 0.08f, size.z - 0.06f), graphite, 0.004f);
        Visual("Counter Top", parent, new Vector3(c.x, size.y - 0.0175f, c.z), new Vector3(size.x + 0.04f, 0.035f, size.z + 0.04f), counterTop, 0.01f);
        float far = c.z + size.z * 0.5f;
        Visual("Counter Front", parent, new Vector3(c.x, 0.47f, far + 0.006f), new Vector3(size.x - 0.1f, 0.6f, 0.012f), navy, 0.004f);
        Sign(parent, "Counter Name", new Vector3(c.x, 0.5f, far + 0.013f), Quaternion.Euler(0f, 180f, 0f), new Vector2(1.3f, 0.24f), "동아편의점", "", new Color(0, 0, 0, 0), Color.white, false);

        BiopticScanner(environment, parent);

        // Card terminal: rounded graphite body, a lit screen, a keypad. The slot and its frame are unchanged.
        foreach (var (name, radius) in new[] { ("Terminal Base", 0.004f), ("Terminal Top", 0.01f), ("Terminal Side L", 0.003f), ("Terminal Side R", 0.003f) })
        {
            var part = Find(environment, name);
            P3Meshes.Round(part, radius);
            part.GetComponent<Renderer>().sharedMaterial = graphite;
        }
        var screen = Find(environment, "Terminal Screen");
        screen.GetComponent<Renderer>().sharedMaterial = screenGlow;
        var topPart = Find(environment, "Terminal Top").transform.position;
        float keysY = topPart.y + Find(environment, "Terminal Top").GetComponent<BoxCollider>().size.y * 0.5f + 0.002f;
        var key = Plain("P3_Key", new Color(0.82f, 0.84f, 0.87f), 0.5f);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                Visual("Key", parent, new Vector3(topPart.x + (col - 1) * 0.034f, keysY, topPart.z - 0.078f + row * 0.02f), new Vector3(0.028f, 0.004f, 0.016f), key, 0.0015f);

        // Cash drawer: a black tray with a cross divider, one compartment per denomination.
        var drawer = Find(environment, "Drawer Base");
        var d = drawer.transform.position;
        var ds = drawer.transform.localScale;
        drawer.GetComponent<Renderer>().sharedMaterial = graphite;
        foreach (var (offset, s) in new[]
        {
            (new Vector3(0f, 0.012f, -ds.z * 0.5f), new Vector3(ds.x, 0.024f, 0.008f)), (new Vector3(0f, 0.012f, ds.z * 0.5f), new Vector3(ds.x, 0.024f, 0.008f)),
            (new Vector3(-ds.x * 0.5f, 0.012f, 0f), new Vector3(0.008f, 0.024f, ds.z)), (new Vector3(ds.x * 0.5f, 0.012f, 0f), new Vector3(0.008f, 0.024f, ds.z)),
            (new Vector3(0f, 0.008f, 0f), new Vector3(0.005f, 0.016f, ds.z - 0.01f)), (new Vector3(0f, 0.008f, 0f), new Vector3(ds.x - 0.01f, 0.016f, 0.005f)),
        })
            Visual("Drawer Rim", parent, d + offset, s, graphite, 0.002f);

        // Trays: light grey with a low lip.
        foreach (var trayName in new[] { "Change Tray", "Customer Plate" })
        {
            var tray = Find(environment, trayName);
            tray.GetComponent<Renderer>().sharedMaterial = grey;
            var p = tray.transform.position;
            var s = tray.transform.localScale;
            foreach (var (offset, rim) in new[]
            {
                (new Vector3(0f, 0.006f, -s.z * 0.5f), new Vector3(s.x, 0.012f, 0.006f)), (new Vector3(0f, 0.006f, s.z * 0.5f), new Vector3(s.x, 0.012f, 0.006f)),
                (new Vector3(-s.x * 0.5f, 0.006f, 0f), new Vector3(0.006f, 0.012f, s.z)), (new Vector3(s.x * 0.5f, 0.006f, 0f), new Vector3(0.006f, 0.012f, s.z)),
            })
                Visual(trayName + " Lip", parent, p + offset, rim, grey, 0.002f);
        }

        // The bag: white plastic with the store name on the clerk's side.
        var bagFront = Find(null, "Bag Wall F");
        Sign(parent, "Bag Name", bagFront.transform.position + new Vector3(0f, 0.005f, -0.006f), Quaternion.identity, new Vector2(0.26f, 0.06f), "동아편의점", "", new Color(0, 0, 0, 0), Navy, false);

        // Two screens on stands: the POS on the left, the order screen on the right. Backs carry the name.
        foreach (var (center, sizePx) in new[] { (posScreen, new Vector2(860f, 660f)), (orderScreen, new Vector2(620f, 470f)) })
        {
            var rotation = Quaternion.LookRotation(center - eye);
            var panel = sizePx * 0.0007f;
            var housing = Visual("Screen Housing", parent, center + rotation * new Vector3(0f, 0f, 0.019f), new Vector3(panel.x + 0.04f, panel.y + 0.04f, 0.03f), graphite, 0.012f);
            housing.transform.rotation = rotation;
            Sign(parent, "Screen Back", center + rotation * new Vector3(0f, 0f, 0.036f), rotation * Quaternion.Euler(0f, 180f, 0f), new Vector2(panel.x * 0.5f, 0.07f), "동아편의점", "", new Color(0, 0, 0, 0), new Color(1f, 1f, 1f, 0.75f), false);
            var neckTop = center + rotation * new Vector3(0f, -panel.y * 0.5f + 0.04f, 0.04f);
            var baseAt = new Vector3(center.x, 0.9f, Mathf.Min(center.z - 0.1f, far - 0.08f));
            var neck = Visual("Screen Neck", parent, (neckTop + baseAt) * 0.5f, new Vector3(0.04f, Vector3.Distance(neckTop, baseAt), 0.025f), steel, 0.008f);
            neck.transform.rotation = Quaternion.FromToRotation(Vector3.up, (neckTop - baseAt).normalized);
            Visual("Screen Foot", parent, baseAt + new Vector3(0f, 0.006f, 0f), new Vector3(0.2f, 0.012f, 0.13f), graphite, 0.005f);
        }

        // Receipt printer behind the drawer.
        var printer = new Vector3(d.x - 0.12f, 0.9f + 0.055f, far - 0.1f);
        Visual("Receipt Printer", parent, printer, new Vector3(0.13f, 0.11f, 0.15f), graphite, 0.014f);
        Visual("Receipt Paper", parent, printer + new Vector3(0f, 0.06f, 0.04f), new Vector3(0.07f, 0.003f, 0.06f), white, 0.001f).transform.rotation = Quaternion.Euler(-25f, 0f, 0f);
    }

    // ---------- Pieces ----------

    /// <summary>A rounded box with no collider: looks only.</summary>
    static GameObject Visual(string name, Transform parent, Vector3 center, Vector3 size, Material material, float radius)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.GetComponent<MeshFilter>().sharedMesh = P3Meshes.RoundedBox(size, radius, 2);
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    /// <summary>A runtime sign (StoreSign). Readable when looking along <paramref name="rotation"/>'s forward.</summary>
    static StoreSign Sign(Transform parent, string name, Vector3 position, Quaternion rotation, Vector2 size, string title, string subtitle, Color background, Color text, bool doubleSided, float faceOffset = 0.002f, float titleScale = 0.56f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        var sign = go.AddComponent<StoreSign>();
        sign.Title = title;
        sign.Subtitle = subtitle;
        sign.SizeMeters = size;
        sign.Background = background;
        sign.TextColor = text;
        sign.DoubleSided = doubleSided;
        sign.FaceOffset = faceOffset;
        sign.TitleScale = titleScale;
        return sign;
    }

    static GameObject Find(Transform under, string name)
    {
        if (under != null)
        {
            foreach (var t in under.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t.gameObject;
        }
        else
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t.gameObject;
        }
        throw new Exception("Dressing: object missing: " + name);
    }

    // ---------- Imports ----------

    static void ConfigureImports()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root, Generated }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
            bool normal = path.Contains("_NormalGL."), mask = path.Contains("_Mask.");
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool srgb = !normal && !mask;
            if (importer.textureType == type && importer.sRGBTexture == srgb && importer.maxTextureSize == 1024 && importer.anisoLevel == 4) continue;
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.maxTextureSize = 1024;  // Quest 2 budget (doc D4).
            importer.anisoLevel = 4;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Root }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
            // The OBJ's slots are told apart by the names from its .mtl, so it keeps its (unused) imported materials.
            var materials = path.EndsWith(".obj") ? ModelImporterMaterialImportMode.ImportViaMaterialDescription : ModelImporterMaterialImportMode.None;
            if (importer.materialImportMode == materials && !importer.importAnimation && !importer.importCameras && !importer.importLights) continue;
            importer.materialImportMode = materials;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.SaveAndReimport();
        }
    }

    // ---------- Materials ----------

    static Material Asset(string name)
    {
        var path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? throw new Exception("URP Lit missing."));
            AssetDatabase.CreateAsset(material, path);
        }
        return material;
    }

    static Material Lit(string name, string texturePrefix, Vector2 tiling, Color tint, float emission = 0f)
    {
        var material = Asset(name);
        ApplyPbr(material, texturePrefix, tiling, tint, emission);
        return material;
    }

    static Material Plain(string name, Color color, float smoothness, float metallic = 0f)
    {
        var material = Asset(name);
        Paint(material, null, color, smoothness, Vector2.one, 0f);
        material.SetFloat("_Metallic", metallic);
        return material;
    }

    static Material Emissive(string name, Color color, Color glow)
    {
        var material = Asset(name);
        Paint(material, null, color, 0.85f, Vector2.one, 0f);
        Keyword(material, "_EMISSION", true);
        material.SetColor("_EmissionColor", glow);
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        return material;
    }

    /// <summary>Base color, OpenGL normal and the packed mask (R metallic, G occlusion, A smoothness). Prefix is a full asset path.</summary>
    static void ApplyPbr(Material material, string texturePrefix, Vector2 tiling, Color tint, float emission = 0f)
    {
        var color = Texture($"{texturePrefix}_Color");
        var normal = Texture($"{texturePrefix}_NormalGL");
        var mask = Texture($"{texturePrefix}_Mask");
        var glow = Texture($"{texturePrefix}_Emission");
        material.SetTexture("_BaseMap", color);
        material.SetColor("_BaseColor", tint);
        material.SetTextureScale("_BaseMap", tiling);
        Keyword(material, "_NORMALMAP", normal != null);
        material.SetTexture("_BumpMap", normal);
        material.SetFloat("_BumpScale", 1f);
        Keyword(material, "_METALLICSPECGLOSSMAP", mask != null);
        Keyword(material, "_OCCLUSIONMAP", mask != null);
        material.SetTexture("_MetallicGlossMap", mask);
        material.SetTexture("_OcclusionMap", mask);
        material.SetFloat("_Smoothness", mask != null ? 1f : 0.3f);
        material.SetFloat("_OcclusionStrength", 1f);
        bool emits = emission > 0f && glow != null;
        Keyword(material, "_EMISSION", emits);
        material.SetTexture("_EmissionMap", emits ? glow : null);
        material.SetColor("_EmissionColor", emits ? Color.white * emission : Color.black);
        material.globalIlluminationFlags = emits ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        EditorUtility.SetDirty(material);
    }

    /// <summary>A painted surface: a flat color, optionally the normal map of a texture set, a fixed smoothness.</summary>
    static void Paint(Material material, string normalPrefix, Color color, float smoothness, Vector2 tiling, float bump)
    {
        var normal = normalPrefix != null ? Texture($"{normalPrefix}_NormalGL") : null;
        material.SetTexture("_BaseMap", null);
        material.SetColor("_BaseColor", color);
        material.SetTextureScale("_BaseMap", tiling);
        Keyword(material, "_NORMALMAP", normal != null);
        material.SetTexture("_BumpMap", normal);
        material.SetFloat("_BumpScale", bump);
        Keyword(material, "_METALLICSPECGLOSSMAP", false);
        Keyword(material, "_OCCLUSIONMAP", false);
        material.SetTexture("_MetallicGlossMap", null);
        material.SetTexture("_OcclusionMap", null);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", smoothness);
        Keyword(material, "_EMISSION", false);
        material.SetColor("_EmissionColor", Color.black);
        EditorUtility.SetDirty(material);
    }

    static Texture2D Texture(string pathWithoutExtension)
    {
        foreach (var extension in new[] { ".png", ".jpg" })
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(pathWithoutExtension + extension);
            if (texture != null) return texture;
        }
        return null;
    }

    /// <summary>The fridge door glass: the glTF uses transmission, which URP Lit does not have, so a faint transparent sheet.</summary>
    static Material Glass(string name)
    {
        var material = Asset(name);
        material.SetColor("_BaseColor", new Color(0.85f, 0.92f, 0.95f, 0.12f));
        material.SetFloat("_Smoothness", 0.95f);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>Glass of another color and opacity.</summary>
    static Material Tinted(string name, Color color)
    {
        var material = Glass(name);
        material.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void Keyword(Material material, string keyword, bool on)
    {
        if (on) material.EnableKeyword(keyword);
        else material.DisableKeyword(keyword);
    }

    // ---------- Placing models ----------

    /// <param name="material">Gets the imported slot's material name (the renderer's name when the slot is empty).</param>
    static GameObject Place(string modelPath, string name, Transform parent, float yaw, Func<string, Material> material)
    {
        var model = Load<GameObject>(modelPath);
        var instance = UnityEngine.Object.Instantiate(model, parent);
        instance.name = name;
        instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f) * instance.transform.localRotation;
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
        {
            var imported = renderer.sharedMaterials;
            var slots = new Material[imported.Length];
            for (int i = 0; i < slots.Length; i++) slots[i] = material(imported[i] != null ? imported[i].name : renderer.name);
            renderer.sharedMaterials = slots;
        }
        foreach (var collider in instance.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
        return instance;
    }

    /// <summary>Moves the object so the point <paramref name="pivot"/> of its bounds (0..1 per axis) lands on <paramref name="target"/>.</summary>
    static void Align(GameObject go, Vector3 target, Vector3 pivot)
    {
        var b = Bounds(go);
        var at = b.min + Vector3.Scale(b.size, pivot);
        go.transform.position += target - at;
    }

    /// <summary>Scales down about the bottom center until the footprint fits.</summary>
    static void FitWithin(GameObject go, Vector2 footprint)
    {
        var b = Bounds(go);
        float scale = Mathf.Min(1f, footprint.x / b.size.x, footprint.y / b.size.z);
        if (scale >= 1f) return;
        var bottom = new Vector3(b.center.x, b.min.y, b.center.z);
        go.transform.localScale *= scale;
        Align(go, bottom, new Vector3(0.5f, 0f, 0.5f));
    }

    static Bounds Bounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) throw new Exception(go.name + " has no renderer.");
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    /// <summary>One solid box around the whole model, on its root, so the NavMesh bake and the player both see it.</summary>
    static void AddBoxCollider(GameObject go)
    {
        var b = Bounds(go);
        var collider = go.AddComponent<BoxCollider>();
        collider.center = go.transform.InverseTransformPoint(b.center);
        var size = go.transform.InverseTransformVector(b.size);
        collider.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
    }

    static T Load<T>(string path) where T : UnityEngine.Object =>
        AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception($"Missing {typeof(T).Name}: {path}");

    // ---------- Looking at it ----------

    /// <summary>
    /// -executeMethod P3StoreDressing.RenderViews -p3-shots &lt;dir&gt;: opens the store scene and saves still views from fixed
    /// camera poses (edit mode, no play; signs are built for the shot). For checking the look by eye; it is not a test.
    /// </summary>
    public static void RenderViews()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-p3-shots");
        var dir = index >= 0 && index + 1 < args.Length ? args[index + 1] : "Temp/P3Shots";
        Directory.CreateDirectory(dir);
        EditorSceneManager.OpenScene("Assets/Scenes/StoreScene.unity");
        // Otherwise the first frames show Unity's placeholder shader while variants compile.
        ShaderUtil.allowAsyncCompilation = false;
        foreach (var sign in UnityEngine.Object.FindObjectsByType<StoreSign>(FindObjectsSortMode.None)) sign.BuildNow();
        var views = new List<(string name, Vector3 position, Vector3 lookAt)>
        {
            ("01_clerk_view", new Vector3(0f, 1.6f, -0.3f), new Vector3(0f, 1.25f, 4f)),
            ("02_from_door", new Vector3(-4.1f, 1.6f, 2.35f), new Vector3(1.5f, 1.0f, 1.2f)),
            ("03_fridge_wall", new Vector3(-0.5f, 1.6f, 2.1f), new Vector3(0.4f, 1.3f, 5.4f)),
            ("04_counter_clerk", new Vector3(0f, 1.5f, -0.2f), new Vector3(0f, 0.95f, 0.7f)),
            ("05_counter_customer", new Vector3(0.3f, 1.55f, 2.4f), new Vector3(0f, 0.95f, 0.6f)),
            ("06_right_wall", new Vector3(1.4f, 1.6f, 1.6f), new Vector3(4.4f, 1.0f, 3.0f)),
            ("07_aisle_back", new Vector3(-4.0f, 1.6f, 4.0f), new Vector3(2.0f, 1.0f, 3.6f)),
            ("08_overview", new Vector3(4.1f, 2.6f, -2.1f), new Vector3(-1.2f, 0.6f, 3.0f)),
            ("09_clerk_side_left", new Vector3(0.5f, 1.6f, 0.2f), new Vector3(-4.2f, 1.0f, -1.0f)),
            ("10_scanner_clerk", new Vector3(0.22f, 1.28f, 0.12f), new Vector3(0f, 1.0f, 0.62f)),
            ("11_scanner_side", new Vector3(0.6f, 1.12f, 0.42f), new Vector3(0f, 1.02f, 0.7f)),
            ("12_microwave_corner", new Vector3(3.2f, 1.55f, 1.7f), new Vector3(4.3f, 1.05f, 1.7f)),
        };
        var go = new GameObject("P3 Shot Camera");
        var camera = go.AddComponent<Camera>();
        camera.fieldOfView = 75f;
        camera.nearClipPlane = 0.05f;
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = target;
        var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        camera.Render();  // warm-up frame
        void Shot(string name, Vector3 position, Vector3 lookAt)
        {
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position));
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), image.EncodeToPNG());
        }
        foreach (var (name, position, lookAt) in views) Shot(name, position, lookAt);

        // Day 3's corner as it looks during a request: door open, then running with the light on and the water pouring (not saved).
        var hinge = Find(null, "Microwave Door Hinge").transform;
        hinge.localRotation = Quaternion.Euler(0f, 105f, 0f);
        Shot("13_microwave_door_open", new Vector3(3.3f, 1.4f, 1.55f), new Vector3(4.25f, 1.05f, 1.6f));
        hinge.localRotation = Quaternion.identity;
        Find(null, "Microwave Lit").SetActive(true);
        Find(null, "Dispenser Water").SetActive(true);
        Shot("14_running_and_pouring", new Vector3(3.3f, 1.4f, 1.75f), new Vector3(4.25f, 1.05f, 1.75f));

        RenderTexture.active = null;
        UnityEngine.Object.DestroyImmediate(go);
        Debug.Log($"[P3] Rendered {views.Count + 2} views to {dir}");
    }
}
