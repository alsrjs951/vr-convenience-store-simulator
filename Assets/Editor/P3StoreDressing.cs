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
    const string Signage = "Assets/Art/Signage";
    const string CashArt = "Assets/Art/Cash";
    const float CeilingHeight = 3f;

    // Store inside faces (P2StoreBuilder: walls 0.1 m thick, the store runs along x from the near wall to the far wall).
    const float InnerFar = P2StoreBuilder.RoomFar + 0.06f, InnerRight = P2StoreBuilder.RoomNear - 0.06f, InnerFront = P2StoreBuilder.RoomFront - 0.06f, InnerBack = P2StoreBuilder.RoomBack + 0.06f;
    const float ShelfWidth = P2StoreBuilder.ShelfWidth;
    const float StorefrontZ = P2StoreBuilder.StorefrontZ, PartitionX = P2StoreBuilder.PartitionX;
    static readonly float DoorLeft = P2StoreBuilder.DoorCenter.x - P2StoreBuilder.DoorWidth * 0.5f, DoorRight = P2StoreBuilder.DoorCenter.x + P2StoreBuilder.DoorWidth * 0.5f;

    // The tobacco wall behind the clerk (the locomotion check pushes against its front).
    public const float TobaccoFront = -1.35f, TobaccoBack = -1.75f, TobaccoLeft = PartitionX + 0.05f, TobaccoRight = 1.85f;
    // The fridges along the far wall, then the microwave corner, then the end of the chilled cases.
    public const float FridgeStart = -1.12f, ChilledFront = 4.6f, ChilledFar = -3.8f, ChilledNear = 4.2f;
    public const float FreezerX0 = 0.35f, FreezerX1 = 1.1f, FreezerZ0 = 2.75f, FreezerZ1 = 4.15f, FreezerHeight = 0.86f;
    public const int FridgeCount = 5, ChilledModules = 4;

    static readonly Color Navy = new Color(0.016f, 0.118f, 0.259f);
    static readonly Color Grey = new Color(0.635f, 0.667f, 0.678f);
    static readonly Color White = new Color(0.95f, 0.95f, 0.94f);

    static Material navy, white, grey, graphite, steel, strip;

    public static void Dress(Transform environment)
    {
        Directory.CreateDirectory(MaterialFolder);
        ConfigureImports();

        // ---------- Surfaces: the P2 materials change, so every box that uses them changes at once ----------
        ApplyPbr(Load<Material>($"{P2Materials}/P2_Floor.mat"), $"{Generated}/floor_tile", new Vector2(9f / 1.2f, 8f / 1.2f), Color.white);
        Paint(Load<Material>($"{P2Materials}/P2_Wall.mat"), $"{Generated}/wall_paint", new Color(0.96f, 0.96f, 0.95f), 0.22f, new Vector2(6f, 2f), 0.35f);
        // Dark gondolas, as in the reference photo: the goods and the price rails stand out against them.
        Paint(Load<Material>($"{P2Materials}/P2_Shelf.mat"), $"{Root}/AmbientCG/Metal027/Metal027", new Color(0.1f, 0.105f, 0.115f), 0.42f, Vector2.one, 0.25f);
        Paint(Load<Material>($"{P2Materials}/P2_Counter.mat"), null, new Color(0.93f, 0.925f, 0.91f), 0.35f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Device.mat"), null, new Color(0.055f, 0.06f, 0.07f), 0.62f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Stand.mat"), null, Grey, 0.4f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Bin.mat"), null, new Color(0.38f, 0.4f, 0.42f), 0.45f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_RecycleBin.mat"), null, Navy, 0.4f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_WarehouseDoor.mat"), null, new Color(0.62f, 0.64f, 0.67f), 0.45f, Vector2.one, 0f);
        Paint(Load<Material>($"{P2Materials}/P2_Porch.mat"), null, new Color(0.46f, 0.46f, 0.44f), 0.2f, Vector2.one, 0f);
        ApplyPbr(Load<Material>($"{P2Materials}/P2_Cardboard.mat"), $"{Root}/PolyHaven/cardboard_box_01/cardboard_side", Vector2.one, Color.white);

        navy = Plain("P3_Navy", Navy, 0.45f);
        white = Plain("P3_White", White, 0.3f);
        grey = Plain("P3_Grey", Grey, 0.4f);
        graphite = Plain("P3_Graphite", new Color(0.05f, 0.055f, 0.065f), 0.7f);
        steel = Plain("P3_Steel", new Color(0.78f, 0.8f, 0.83f), 0.7f, 1f);
        strip = Emissive("P3_LedStrip", Color.white, new Color(1f, 1f, 0.98f) * 3.2f);

        // The lit strips are only the look of a light: what lights the room is P3StoreLighting, once the scene is saved.

        var dressing = new GameObject("Store Dressing").transform;
        dressing.SetParent(environment, false);

        // Rounded edges on the furniture boxes the builder made (colliders keep their size).
        foreach (var t in environment.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name;
            if (n.StartsWith("Shelf A ") || n.StartsWith("Shelf B ") || n.StartsWith("Shelf D ")) P3Meshes.Round(t.gameObject, n.EndsWith("Spine") || n.EndsWith("Base") ? 0.012f : 0.007f);
            else if (n.StartsWith("Stand ") && n.EndsWith(" Body")) P3Meshes.Round(t.gameObject, 0.02f);
            else if (n == "Cart Body") P3Meshes.Round(t.gameObject, 0.03f);
        }

        Ceiling(dressing);
        MusicSpeakers(environment);
        StreetFront(dressing);
        Baseboards(dressing);
        foreach (var (x, front, back) in new[]
        {
            (P2StoreBuilder.ShelfAX, P2StoreBuilder.ShelfFront, P2StoreBuilder.ShelfBack),
            (P2StoreBuilder.ShelfBX, P2StoreBuilder.ShelfFront, P2StoreBuilder.ShelfBack),
            (P2StoreBuilder.ShelfDX, P2StoreBuilder.ShelfDFront, P2StoreBuilder.ShelfDBack),
        })
            GondolaTrim(dressing, x, front, back);
        FridgeWall(dressing);
        ChilledCases(dressing);
        TobaccoWall(dressing);
        ChestFreezer(dressing);
        ShelfC(dressing);
        MicrowaveCorner(dressing);

        // ---------- Back of house, behind the tobacco wall: stock shelf with a crate, a hand truck (kept from the first pass) ----------
        var shelf = Place($"{Root}/PolyHaven/steel_frame_shelves_01/steel_frame_shelves_01.fbx", "Stock Shelf", dressing, 0f,
            _ => Lit("P3_SteelShelves", $"{Root}/PolyHaven/steel_frame_shelves_01/steel_frame_shelves_01", Vector2.one, Color.white));
        Align(shelf, new Vector3(-0.3f, 0f, InnerBack + 0.01f), new Vector3(0.5f, 0f, 0f));
        AddBoxCollider(shelf);
        var shelfBounds = Bounds(shelf);
        var crate = Place($"{Root}/PolyHaven/industrial_pastic_container/industrial_pastic_container.fbx", "Crate On Shelf", dressing, 0f,
            _ => Lit("P3_PlasticContainer", $"{Root}/PolyHaven/industrial_pastic_container/industrial_pastic_container", Vector2.one, Color.white));
        Align(crate, new Vector3(shelfBounds.center.x, shelfBounds.max.y, shelfBounds.center.z), new Vector3(0.5f, 0f, 0.5f));
        FitWithin(crate, new Vector2(shelfBounds.size.x - 0.04f, shelfBounds.size.z - 0.04f));
        var truck = Place($"{Root}/PolyHaven/hand_truck/hand_truck.fbx", "Hand Truck", dressing, 0f,
            _ => Lit("P3_HandTruck", $"{Root}/PolyHaven/hand_truck/hand_truck", Vector2.one, Color.white));
        Align(truck, new Vector3(1.1f, 0f, InnerBack + 0.02f), new Vector3(0.5f, 0f, 0f));

        foreach (var placed in new[] { "Stock Shelf", "Hand Truck", "Drinks Fridge 1", "Shelf C", "Microwave Cabinet", "Chilled Cases", "Tobacco Wall", "Chest Freezer" })
        {
            var t = dressing.Find(placed);
            if (t == null) continue;
            var b = Bounds(t.gameObject);
            Debug.Log($"[P3] {placed}: center {b.center.ToString("F2")} size {b.size.ToString("F2")}");
        }
    }

    // ---------- Room ----------

    /// <summary>A tiled T-bar ceiling with rows of slim strip lights running across the store, as in the reference photo.</summary>
    static void Ceiling(Transform parent)
    {
        float width = P2StoreBuilder.RoomNear - P2StoreBuilder.RoomFar, depth = P2StoreBuilder.RoomFront - P2StoreBuilder.RoomBack;
        var material = Emissive("P3_Ceiling", new Color(0.96f, 0.96f, 0.96f), new Color(0.42f, 0.42f, 0.43f));
        material.SetFloat("_Smoothness", 0.15f);
        var tile = Load<Texture2D>($"{Signage}/ceiling_tile.png");
        material.SetTexture("_BaseMap", tile);
        material.SetTextureScale("_BaseMap", new Vector2(width / 0.6f, depth / 0.6f));
        material.SetTexture("_EmissionMap", tile);
        var center = new Vector3((P2StoreBuilder.RoomNear + P2StoreBuilder.RoomFar) * 0.5f, CeilingHeight, (P2StoreBuilder.RoomFront + P2StoreBuilder.RoomBack) * 0.5f);
        Quad("Ceiling", parent, center, new Vector2(width, depth), material).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

        var housing = Plain("P3_LightHousing", new Color(0.93f, 0.93f, 0.92f), 0.4f);
        foreach (var x in LightRows)
            foreach (var (z0, z1) in LightSpans)
                LightStrip(parent, housing, new Vector3(x, CeilingHeight, (z0 + z1) * 0.5f), new Vector2(0.09f, z1 - z0));
        // Back of house, one short row along it.
        LightStrip(parent, housing, new Vector3(1f, CeilingHeight, -2.05f), new Vector2(2.4f, 0.09f));

        // An air-conditioning cassette, as over the far aisle in the photo.
        var cassette = new Vector3(-1.9f, CeilingHeight - 0.02f, 3.6f);
        Visual("AC Cassette", parent, cassette, new Vector3(0.8f, 0.04f, 0.8f), white, 0.008f);
        Visual("AC Vent", parent, cassette + new Vector3(0f, -0.021f, 0f), new Vector3(0.42f, 0.004f, 0.42f), grey, 0.001f);
        foreach (var (offset, size) in new[]
        {
            (new Vector3(0f, -0.021f, -0.31f), new Vector3(0.62f, 0.004f, 0.06f)), (new Vector3(0f, -0.021f, 0.31f), new Vector3(0.62f, 0.004f, 0.06f)),
            (new Vector3(-0.31f, -0.021f, 0f), new Vector3(0.06f, 0.004f, 0.62f)), (new Vector3(0.31f, -0.021f, 0f), new Vector3(0.06f, 0.004f, 0.62f)),
        })
            Visual("AC Slot", parent, cassette + offset, size, graphite, 0.001f);
    }

    /// <summary>
    /// Round ceiling speakers, there once the store has bought its music (docs/p2/해금_성장_기획_2026-11-10.md 5): two over
    /// the aisle in front of the counter, where the clerk sees them, and one further in. Between the light rows and clear of
    /// the cameras and the air conditioner.
    /// </summary>
    static void MusicSpeakers(Transform environment)
    {
        var group = EquipmentGroup(environment, GameManager.MusicLook);
        var grille = Plain("P3_SpeakerGrille", new Color(0.56f, 0.57f, 0.59f), 0.35f, 0.5f);
        foreach (var at in new[] { new Vector3(-0.2f, CeilingHeight, 1.9f), new Vector3(1.2f, CeilingHeight, 1.9f), new Vector3(-3f, CeilingHeight, 2.9f) })
        {
            Disc("Speaker Ring", group, at + Vector3.down * 0.008f, 0.26f, 0.016f, white);
            Disc("Speaker Grille", group, at + Vector3.down * 0.018f, 0.2f, 0.006f, grille);
        }
    }

    /// <summary>Where the rows are; the baked area lights hang at the same places (P3StoreLighting).</summary>
    public static readonly float[] LightRows = { -5.1f, -3.7f, -2.3f, -0.9f, 0.5f, 1.9f, 3.3f };
    public static readonly (float z0, float z1)[] LightSpans = { (-1.2f, 0.95f), (1.1f, 3.25f), (3.4f, 5.3f) };

    static void LightStrip(Transform parent, Material housing, Vector3 atCeiling, Vector2 size)
    {
        Visual("Light Housing", parent, atCeiling + Vector3.down * 0.012f, new Vector3(size.x + 0.07f, 0.024f, size.y + 0.07f), housing, 0.006f).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        Quad("LED Strip", parent, atCeiling + Vector3.down * 0.0245f, size, strip).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>Aluminium frames for the street front: posts, rails, the two sliding leaves parked open, the store name over the door.</summary>
    static void StreetFront(Transform parent)
    {
        var root = new GameObject("Street Front").transform;
        root.SetParent(parent, false);
        var aluminium = Plain("P3_Aluminium", new Color(0.72f, 0.74f, 0.76f), 0.6f, 0.6f);
        float height = P2StoreBuilder.DoorHeight, roomFar = P2StoreBuilder.RoomFar;
        foreach (var x in new[] { roomFar + 0.09f, (roomFar + DoorLeft) * 0.5f, DoorLeft, DoorRight, PartitionX - 0.07f })
            Visual("Post", root, new Vector3(x, height * 0.5f, StorefrontZ), new Vector3(0.07f, height, 0.1f), aluminium, 0.006f);
        Visual("Top Rail", root, new Vector3((roomFar + PartitionX) * 0.5f, height - 0.03f, StorefrontZ), new Vector3(PartitionX - roomFar, 0.06f, 0.1f), aluminium, 0.006f);
        foreach (var (x0, x1) in new[] { (roomFar, DoorLeft), (DoorRight, PartitionX) })
            Visual("Bottom Rail", root, new Vector3((x0 + x1) * 0.5f, 0.05f, StorefrontZ), new Vector3(x1 - x0, 0.1f, 0.1f), aluminium, 0.006f);

        // The leaves slid aside over the fixed glass (Title's closed leaves are separate and only there on the title).
        float leaf = P2StoreBuilder.DoorWidth * 0.5f, z = StorefrontZ + 0.07f;
        foreach (float near in new[] { DoorLeft - leaf * 0.5f, DoorRight + Mathf.Min(leaf, PartitionX - DoorRight) * 0.5f })
        {
            float width = near < DoorLeft ? leaf : Mathf.Min(leaf, PartitionX - DoorRight);
            foreach (float side in new[] { -1f, 1f })
                Visual("Door Leaf Stile", root, new Vector3(near + side * (width * 0.5f - 0.03f), height * 0.5f, z), new Vector3(0.05f, height - 0.08f, 0.04f), aluminium, 0.005f);
            foreach (float y in new[] { 0.08f, height - 0.1f })
                Visual("Door Leaf Rail", root, new Vector3(near, y, z), new Vector3(width, 0.08f, 0.04f), aluminium, 0.005f);
        }

        float frontCenter = (roomFar + PartitionX) * 0.5f, length = PartitionX - roomFar;
        Sign(root, "Outside Name", new Vector3(P2StoreBuilder.DoorCenter.x, 2.6f, StorefrontZ - 0.056f), Quaternion.identity, new Vector2(2.6f, 0.46f), "동아편의점", "", Navy, Color.white, false);
        Visual("Inside Header Band", root, new Vector3(frontCenter, 2.3f, StorefrontZ + 0.056f), new Vector3(length, 0.1f, 0.012f), navy, 0.003f);
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

    /// <summary>Navy skirting along the walls the customer sees (the far and front walls are behind the fridges and chilled cases).</summary>
    static void Baseboards(Transform parent)
    {
        const float h = 0.1f, t = 0.012f;
        Visual("Skirting Back", parent, new Vector3((PartitionX + InnerRight) * 0.5f, h * 0.5f, InnerBack + t * 0.5f), new Vector3(InnerRight - PartitionX, h, t), navy, 0.003f);
        Visual("Skirting Right", parent, new Vector3(InnerRight - t * 0.5f, h * 0.5f, 1.5f), new Vector3(t, h, InnerFront - InnerBack), navy, 0.003f);
    }

    // ---------- Gondolas: price rails, plinth ----------

    static void GondolaTrim(Transform parent, float x, float front, float back)
    {
        var root = new GameObject($"Gondola Trim {x:0.00}").transform;
        root.SetParent(parent, false);
        float w = ShelfWidth - 0.04f;
        foreach (var top in new[] { 0.1f, 0.55f, 1.0f, 1.4f })
        {
            Visual("Price Rail Front", root, new Vector3(x, top - 0.021f, front - 0.004f), new Vector3(w, 0.035f, 0.008f), navy, 0.002f);
            Visual("Price Rail Back", root, new Vector3(x, top - 0.021f, back + 0.004f), new Vector3(w, 0.035f, 0.008f), navy, 0.002f);
        }
        Visual("Plinth Front", root, new Vector3(x, 0.045f, front - 0.003f), new Vector3(ShelfWidth, 0.09f, 0.006f), graphite, 0.002f);
        Visual("Plinth Back", root, new Vector3(x, 0.045f, back + 0.003f), new Vector3(ShelfWidth, 0.09f, 0.006f), graphite, 0.002f);
    }

    // ---------- Fridge wall: the far wall, the photo's back wall ----------

    static void FridgeWall(Transform parent)
    {
        // The OBJ keeps the glTF material names as its slots: FridgeCase (case and door frame), FridgeInterior, FridgeGlass.
        var fridgeCase = Lit("P3_FridgeCase", $"{Root}/Khronos/CommercialRefrigerator/FridgeCase", Vector2.one, Color.white);
        var fridgeInterior = Lit("P3_FridgeInterior", $"{Root}/Khronos/CommercialRefrigerator/FridgeInterior", Vector2.one, Color.white, 1f);
        var fridgeGlass = Glass("P3_FridgeGlass");
        Func<string, Material> material = slot => slot.Contains("Glass") ? fridgeGlass : slot.Contains("Interior") ? fridgeInterior : fridgeCase;
        const float gap = 0.01f;
        float width = 0f, depth = 0f, height = 0f;
        for (int i = 0; i < FridgeCount; i++)
        {
            // Yaw 90 turns the doors toward +x, into the store.
            var fridge = Place($"{Root}/Khronos/CommercialRefrigerator/CommercialRefrigerator.obj", $"Drinks Fridge {i + 1}", parent, 90f, material);
            var size = Bounds(fridge).size;
            width = size.z; depth = size.x; height = size.y;
            Align(fridge, new Vector3(InnerFar + 0.005f, 0f, FridgeStart + i * (size.z + gap)), new Vector3(0f, 0f, 0f));
            AddBoxCollider(fridge);
        }
        float z0 = StorefrontZ + 0.05f, z1 = FridgeStart + FridgeCount * width + (FridgeCount - 1) * gap;
        float front = InnerFar + 0.005f + depth;

        // A white filler from the street front to the first fridge, solid so nobody walks behind the fridges.
        var filler = Visual("Fridge Filler", parent, new Vector3((InnerFar + front) * 0.5f, height * 0.5f, (z0 + FridgeStart) * 0.5f), new Vector3(depth, height, FridgeStart - z0), white, 0.006f);
        filler.AddComponent<BoxCollider>().size = new Vector3(depth, height, FridgeStart - z0);

        // White soffit over the fridges up to the ceiling; a slim navy band over the doors and the section name above it.
        float zc = (z0 + z1) * 0.5f, length = z1 - z0;
        var seenLookingFar = Quaternion.Euler(0f, -90f, 0f);
        Visual("Fridge Soffit", parent, new Vector3((InnerFar + front) * 0.5f, (height + CeilingHeight) * 0.5f, zc), new Vector3(depth, CeilingHeight - height, length), white, 0.004f);
        Visual("Fridge Band", parent, new Vector3(front + 0.006f, height + 0.07f, zc), new Vector3(0.012f, 0.12f, length), navy, 0.003f);
        Sign(parent, "Fridge Section Name", new Vector3(front + 0.003f, height + 0.46f, zc + 1f), seenLookingFar, new Vector2(2.4f, 0.42f), "음료 · 주류", "", new Color(0, 0, 0, 0), Navy, false);
    }

    // ---------- Chilled cases: the front wall, the photo's right wall ----------

    /// <summary>
    /// Open multi-deck chilled cases along the front wall: a dark base with a grille, white decks that step back as they rise,
    /// a lit strip under each deck and a navy header leaning out over them. One solid box for the whole run.
    /// </summary>
    static void ChilledCases(Transform parent)
    {
        var root = new GameObject("Chilled Cases").transform;
        root.SetParent(parent, false);
        const float front = ChilledFront, back = InnerFront, depth = back - front;
        float moduleWidth = (ChilledNear - ChilledFar) / ChilledModules;
        var collider = root.gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3((ChilledFar + ChilledNear) * 0.5f, 1.15f, (front + back) * 0.5f);
        collider.size = new Vector3(ChilledNear - ChilledFar, 2.3f, depth);

        var liner = Plain("P3_CaseLiner", new Color(0.86f, 0.87f, 0.87f), 0.55f);
        var deckMaterial = Plain("P3_CaseDeck", new Color(0.93f, 0.93f, 0.92f), 0.6f, 0.15f);
        var strip2 = Emissive("P3_CaseLight", Color.white, new Color(1f, 1f, 0.97f) * 2.6f);
        float xc = (ChilledFar + ChilledNear) * 0.5f, length = ChilledNear - ChilledFar;
        Visual("Case Back", root, new Vector3(xc, 1.25f, back - 0.02f), new Vector3(length, 2.5f, 0.04f), liner, 0.004f);
        Visual("Case Base", root, new Vector3(xc, 0.33f, front + depth * 0.5f), new Vector3(length, 0.66f, depth), graphite, 0.008f);
        Visual("Case Kick", root, new Vector3(xc, 0.05f, front - 0.004f), new Vector3(length, 0.1f, 0.008f), Plain("P3_CaseKick", new Color(0.22f, 0.24f, 0.26f), 0.5f), 0.002f);
        Visual("Case Lip", root, new Vector3(xc, 0.68f, front + 0.04f), new Vector3(length, 0.05f, 0.08f), steel, 0.01f);
        Visual("Case Rail", root, new Vector3(xc, 0.58f, front - 0.005f), new Vector3(length, 0.1f, 0.01f), navy, 0.002f);

        foreach (var (y, reach) in ChilledDecks)
        {
            float zDeck = back - 0.04f - reach * 0.5f;
            Visual("Deck", root, new Vector3(xc, y, zDeck), new Vector3(length, 0.016f, reach), deckMaterial, 0.004f).transform.rotation = Quaternion.Euler(-DeckTilt, 0f, 0f);
            float zEdge = back - 0.04f - reach + 0.004f, yEdge = y - Mathf.Sin(DeckTilt * Mathf.Deg2Rad) * reach * 0.5f - 0.012f;
            Visual("Deck Rail", root, new Vector3(xc, yEdge, zEdge), new Vector3(length, 0.042f, 0.008f), navy, 0.002f);
            if (y > 0.8f) Visual("Deck Light", root, new Vector3(xc, y - 0.02f, zDeck - reach * 0.15f), new Vector3(length, 0.006f, 0.03f), strip2, 0.001f).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        // Uprights between the modules and at both ends.
        for (int i = 0; i <= ChilledModules; i++)
            Visual("Case Upright", root, new Vector3(ChilledFar + i * moduleWidth, 1.45f, back - 0.22f), new Vector3(0.025f, 1.66f, 0.36f), liner, 0.005f);

        // Canopy with a light under it, and the navy header leaning out over the aisle.
        Visual("Case Canopy", root, new Vector3(xc, 2.28f, front + depth * 0.5f), new Vector3(length, 0.06f, depth), white, 0.006f);
        Visual("Case Canopy Light", root, new Vector3(xc, 2.245f, front + 0.2f), new Vector3(length, 0.006f, 0.05f), strip2, 0.001f).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        var lean = Quaternion.Euler(-14f, 0f, 0f);
        var header = new Vector3(xc, 2.53f, front + 0.02f);
        Visual("Case Header", root, header, new Vector3(length, 0.42f, 0.04f), navy, 0.01f).transform.rotation = lean;
        var seenLookingFront = lean;
        var faceAt = header + lean * new Vector3(0f, 0f, -0.024f);
        foreach (float offset in new[] { -2f, 2f })
            Sign(root, "Case Header Name", faceAt + new Vector3(offset, 0f, 0f), seenLookingFront, new Vector2(3.4f, 0.38f), "도시락 · 샌드위치 · 김밥", "", new Color(0, 0, 0, 0), Color.white, false, 0.002f, 0.5f);

        // The corner past the far end of the cases, white cupboards like the photo's.
        float cornerX0 = InnerFar + 0.5f, cornerX1 = ChilledFar - 0.02f;
        var cupboard = Visual("Corner Cupboard", parent, new Vector3((cornerX0 + cornerX1) * 0.5f, 0.45f, (front + back) * 0.5f), new Vector3(cornerX1 - cornerX0, 0.9f, depth), white, 0.01f);
        cupboard.AddComponent<BoxCollider>().size = new Vector3(cornerX1 - cornerX0, 0.9f, depth);
        Visual("Corner Cupboard Top", parent, new Vector3((cornerX0 + cornerX1) * 0.5f, 0.915f, (front + back) * 0.5f), new Vector3(cornerX1 - cornerX0 + 0.02f, 0.03f, depth + 0.02f), grey, 0.006f);
    }

    /// <summary>Height of each deck's middle and how far it reaches out from the back of the case.</summary>
    public static readonly (float y, float reach)[] ChilledDecks = { (0.74f, 0.72f), (1.06f, 0.6f), (1.38f, 0.52f), (1.7f, 0.44f), (2.02f, 0.36f) };
    public const float DeckTilt = 8f;

    // ---------- Tobacco wall: behind the clerk, the photo's left wall ----------

    /// <summary>
    /// Base cupboards, two lit display bays with numbered shelf rails (the printed faces are P3StoreDetails', words and colour
    /// only, D9), a backlit header over them and a tall lightbox at the door end. One solid box, against which the clerk stops.
    /// </summary>
    static void TobaccoWall(Transform parent)
    {
        var root = new GameObject("Tobacco Wall").transform;
        root.SetParent(parent, false);
        float x0 = TobaccoLeft, x1 = TobaccoRight, xc = (x0 + x1) * 0.5f, length = x1 - x0, zc = (TobaccoFront + TobaccoBack) * 0.5f, depth = TobaccoFront - TobaccoBack;
        var collider = root.gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3(xc, CeilingHeight * 0.5f, zc);
        collider.size = new Vector3(length, CeilingHeight, depth);

        Visual("Tobacco Back", root, new Vector3(xc, 1.5f, TobaccoBack + 0.02f), new Vector3(length, 3f, 0.04f), graphite, 0.004f);
        Visual("Tobacco Base", root, new Vector3(xc, 0.45f, zc), new Vector3(length, 0.9f, depth), graphite, 0.01f);
        Visual("Tobacco Base Top", root, new Vector3(xc, 0.91f, zc + 0.01f), new Vector3(length + 0.02f, 0.02f, depth + 0.02f), steel, 0.004f);
        var drawerFront = Plain("P3_TobaccoDrawer", new Color(0.16f, 0.17f, 0.19f), 0.5f);
        int drawers = 6;
        float drawerWidth = (x1 - TobaccoBayLeft) / drawers;
        for (int i = 0; i < drawers; i++)
            Visual("Tobacco Drawer", root, new Vector3(TobaccoBayLeft + (i + 0.5f) * drawerWidth, 0.47f, TobaccoFront + 0.006f), new Vector3(drawerWidth - 0.02f, 0.72f, 0.012f), drawerFront, 0.003f);

        var railLight = Emissive("P3_TobaccoRailLight", new Color(0.8f, 0.9f, 1f), new Color(0.75f, 0.9f, 1f) * 2.2f);
        for (int bay = 0; bay < TobaccoBays; bay++)
        {
            float bayCenter = TobaccoBayLeft + (bay + 0.5f) * TobaccoBayWidth;
            foreach (float side in new[] { -1f, 1f })
                Visual("Tobacco Upright", root, new Vector3(bayCenter + side * (TobaccoBayWidth * 0.5f), 1.55f, zc), new Vector3(0.03f, 1.3f, depth), graphite, 0.005f);
            for (int row = 0; row <= TobaccoRows; row++)
            {
                float y = TobaccoBottom + row * TobaccoRowHeight;
                Visual("Tobacco Shelf", root, new Vector3(bayCenter, y, zc + 0.03f), new Vector3(TobaccoBayWidth - 0.03f, 0.012f, depth - 0.06f), steel, 0.002f);
                Visual("Tobacco Rail", root, new Vector3(bayCenter, y - 0.012f, TobaccoFront + 0.002f), new Vector3(TobaccoBayWidth - 0.03f, 0.016f, 0.006f), railLight, 0.001f).GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        // Backlit header, tipped a little toward the clerk; its printed face is P3StoreDetails'.
        Visual("Tobacco Header", root, new Vector3(TobaccoBayLeft + TobaccoBays * TobaccoBayWidth * 0.5f, TobaccoHeaderY, TobaccoFront + 0.02f), new Vector3(TobaccoBays * TobaccoBayWidth, TobaccoHeaderHeight + 0.06f, 0.08f), graphite, 0.01f);
        // The tall lightbox at the door end, in the photo beside the header.
        Visual("Tobacco Lightbox", root, new Vector3((x0 + TobaccoBayLeft) * 0.5f, 1.72f, TobaccoFront + 0.02f), new Vector3(TobaccoBayLeft - x0, 2.5f, 0.08f), graphite, 0.01f);
    }

    public const float TobaccoBayLeft = TobaccoLeft + 0.35f, TobaccoBayWidth = 1.6f, TobaccoBottom = 0.94f, TobaccoRowHeight = 0.25f;
    public const int TobaccoBays = 2, TobaccoRows = 5;
    public const float TobaccoHeaderY = 2.56f, TobaccoHeaderHeight = 0.72f;

    // ---------- Ice cream chest freezer ----------

    /// <summary>A chest freezer in the aisle, its long side toward the near end of the store: white body, navy band, glass lids.</summary>
    static void ChestFreezer(Transform parent)
    {
        var root = new GameObject("Chest Freezer").transform;
        root.SetParent(parent, false);
        float xc = (FreezerX0 + FreezerX1) * 0.5f, zc = (FreezerZ0 + FreezerZ1) * 0.5f, dx = FreezerX1 - FreezerX0, dz = FreezerZ1 - FreezerZ0, h = FreezerHeight;
        var collider = root.gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3(xc, (h + 0.08f) * 0.5f, zc);
        collider.size = new Vector3(dx, h + 0.08f, dz);

        Visual("Freezer Kick", root, new Vector3(xc, 0.04f, zc), new Vector3(dx - 0.04f, 0.08f, dz - 0.04f), graphite, 0.006f);
        Visual("Freezer Body", root, new Vector3(xc, 0.08f + (h - 0.08f) * 0.5f, zc), new Vector3(dx, h - 0.08f, dz), Plain("P3_FreezerBody", new Color(0.88f, 0.88f, 0.87f), 0.5f), 0.02f);
        Visual("Freezer Band", root, new Vector3(xc, h - 0.09f, zc), new Vector3(dx + 0.006f, 0.05f, dz + 0.006f), navy, 0.004f);
        Visual("Freezer Inside", root, new Vector3(xc, h - 0.012f, zc), new Vector3(dx - 0.09f, 0.008f, dz - 0.09f), Emissive("P3_FreezerFrost", new Color(0.82f, 0.9f, 0.97f), new Color(0.3f, 0.36f, 0.42f)), 0.002f);

        var rim = Plain("P3_FreezerRim", new Color(0.7f, 0.72f, 0.75f), 0.75f, 0.5f);
        foreach (var (offset, size) in new[]
        {
            (new Vector3(-dx * 0.5f + 0.02f, 0f, 0f), new Vector3(0.04f, 0.05f, dz)), (new Vector3(dx * 0.5f - 0.02f, 0f, 0f), new Vector3(0.04f, 0.05f, dz)),
            (new Vector3(0f, 0f, -dz * 0.5f + 0.02f), new Vector3(dx, 0.05f, 0.04f)), (new Vector3(0f, 0f, dz * 0.5f - 0.02f), new Vector3(dx, 0.05f, 0.04f)),
            (Vector3.zero, new Vector3(dx - 0.04f, 0.05f, 0.03f)),
        })
            Visual("Freezer Rim", root, new Vector3(xc, h + 0.025f, zc) + offset, size, rim, 0.006f);
        var lidGlass = Tinted("P3_FreezerLid", new Color(0.82f, 0.92f, 0.98f, 0.22f));
        foreach (float side in new[] { -1f, 1f })
            Visual("Freezer Lid", root, new Vector3(xc, h + 0.05f, zc + side * dz * 0.25f), new Vector3(dx - 0.05f, 0.012f, dz * 0.5f - 0.05f), lidGlass, 0.004f);
    }

    // ---------- Near wall: shelf C ----------

    static void ShelfC(Transform parent)
    {
        const float z0 = 1.4f, z1 = 4.42f, depth = 0.45f, height = 1.8f;
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
    // The corner is built at these near-wall coordinates, then P2StoreBuilder turns it half round and sets it at CabinetPlace:
    // the far wall, at the end of the fridges, beside the chilled cases, where the photo has it.
    public static readonly Vector3 CabinetPlace = new Vector3(-1.5f, 0f, 5.6f);
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
        // One sign over each: a store that is kept can have either without the other (GameManager.ShowCooking goes by the names).
        Sign(root, "Microwave Sign", new Vector3(InnerRight - 0.004f, 1.72f, mw.z), Quaternion.Euler(0f, 90f, 0f), new Vector2(0.62f, 0.2f), "전자레인지", "", Navy, Color.white, false);
        Sign(root, "Dispenser Sign", new Vector3(InnerRight - 0.004f, 1.72f, hw.z), Quaternion.Euler(0f, 90f, 0f), new Vector2(0.36f, 0.2f), "온수", "", Navy, Color.white, false);
    }

    // ---------- Counter (after the builder made it): looks only ----------

    /// <summary>
    /// A fixed bioptic scanner, as at a supermarket checkout: the flat window on the counter (the builder's pad, whose
    /// collider and scan zone stay as they are) in a steel frame, joined by a base to an angular tower behind it, with a
    /// tall smoked window facing the clerk, a green lamp and side vents. The red light is in the upright window, where the
    /// builder's ScanBeam sends it out from; the flat one is plain dark glass. All added parts are looks only, with no
    /// collider, so scanning and the hand's paths over the pad are unchanged. The tower stands a few centimetres behind the
    /// flat window so a product lying on the pad does not reach into it.
    /// </summary>
    static void BiopticScanner(Transform environment, Transform parent)
    {
        const float towerGap = P2StoreBuilder.ScannerTowerGap, towerHeight = P2StoreBuilder.ScannerTowerHeight, towerLean = P2StoreBuilder.ScannerTowerLean;
        const float towerDepth = 0.14f, towerTopDepth = 0.06f;
        var body = Plain("P3_ScannerBody", new Color(0.16f, 0.17f, 0.19f), 0.5f, 0.15f);
        var frame = Plain("P3_ScannerSteel", new Color(0.56f, 0.58f, 0.61f), 0.82f, 0.5f);
        var flatGlass = Plain("P3_ScannerFlatGlass", new Color(0.02f, 0.022f, 0.026f), 0.95f);
        // Dim while it waits. ScannerZone turns it bright for a moment on a read.
        var ready = Emissive("P3_ScannerReady", new Color(0.05f, 0.2f, 0.08f), new Color(0.03f, 0.22f, 0.05f));

        // The flat window: steel housing, dark glass, a thin steel rim.
        var pad = Find(environment, "Scanner Pad");
        var size = pad.transform.localScale;
        P3Meshes.Round(pad, 0.008f);
        pad.GetComponent<Renderer>().sharedMaterial = frame;
        Find(environment, "Scanner Glass").GetComponent<Renderer>().sharedMaterial = flatGlass;
        // The upright window: the same dark glass with the red laser star glowing behind it.
        var glass = Lit("P3_ScannerGlass", $"{Generated}/scanner_glass", Vector2.one, Color.white, 2.2f);
        glass.SetFloat("_Smoothness", 0.95f);
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

        // On the front face (it leans back by atan(lean / height)): the window the red light comes out of, and the lamp.
        float slant = Mathf.Atan2(towerLean, towerHeight);
        var faceNormal = new Vector3(0f, Mathf.Sin(slant), -Mathf.Cos(slant));
        var faceRotation = Quaternion.Euler(slant * Mathf.Rad2Deg, 0f, 0f);
        var origin = tower.transform.position;
        Vector3 OnFront(float y, float outward) => origin + new Vector3(0f, y, towerLean * y / towerHeight) + faceNormal * outward;
        Visual("Scanner Tower Window", parent, OnFront(P2StoreBuilder.ScannerWindowHeight, 0.002f), new Vector3(size.x - 0.03f, 0.19f, 0.004f), flatGlass, 0.003f).transform.rotation = faceRotation;
        // A quad carries the star: the rounded box has no texture coordinates to speak of.
        Quad("Scanner Tower Light", parent, OnFront(P2StoreBuilder.ScannerWindowHeight, 0.0046f), new Vector2(size.x - 0.04f, size.x - 0.04f), glass).transform.rotation = faceRotation;
        var lamp = Visual("Scanner Ready Light", parent, OnFront(towerHeight - 0.017f, 0.003f) + new Vector3(size.x * 0.27f, 0f, 0f), new Vector3(0.04f, 0.01f, 0.004f), ready, 0.002f);
        lamp.transform.rotation = faceRotation;
        Find(null, "ScannerZone").GetComponent<ScannerZone>().Indicator = lamp.GetComponent<Renderer>();

        // Vent slots low on both sides, toward the back.
        foreach (float side in new[] { -1f, 1f })
            for (int i = 0; i < 5; i++)
                Visual("Scanner Vent", parent, origin + new Vector3(side * (size.x * 0.5f + 0.0012f), 0.1f + i * 0.022f, 0.09f), new Vector3(0.003f, 0.006f, 0.045f), graphite, 0.001f);

        // The newer model a store that is kept can order in (docs/p2/해금_성장_기획_2026-11-10.md 5, reads at a wider angle):
        // a head on the tower with a blue light bar, and a reading window in each side. Looks only, above and beside the
        // tower, so the clerk's view of the upright window and the hand's paths over the pad stay as they are.
        var upgrade = EquipmentGroup(environment, GameManager.ScannerLook);
        var bar = Emissive("P3_ScannerBar", new Color(0.08f, 0.22f, 0.5f), new Color(0.2f, 0.55f, 1.3f));
        const float headHeight = 0.06f, headDepth = 0.075f;
        var headCenter = new Vector3(p.x, bottom + towerHeight + headHeight * 0.5f, towerFront + towerLean + headDepth * 0.5f);
        Visual("Scanner Head", upgrade, headCenter, new Vector3(size.x + 0.02f, headHeight, headDepth), body, 0.01f);
        Visual("Scanner Head Bar", upgrade, headCenter + new Vector3(0f, 0f, -headDepth * 0.5f - 0.001f), new Vector3(size.x - 0.03f, 0.012f, 0.003f), bar, 0.001f);
        foreach (float side in new[] { -1f, 1f })
            Visual("Scanner Side Window", upgrade, origin + new Vector3(side * (size.x * 0.5f + 0.0015f), 0.24f, towerLean * 0.24f / towerHeight + 0.034f), new Vector3(0.003f, 0.07f, 0.05f), flatGlass, 0.001f);
    }

    /// <summary>The counter, the devices on it and the two screens' housings. Colliders and zones are untouched.</summary>
    public static void DressCounter(Transform environment, Vector3 posScreen, Vector3 orderScreen, Vector3 eye)
    {
        var parent = new GameObject("Counter Dressing").transform;
        parent.SetParent(environment, false);
        // Mid blue, the reference photo's counter top in the store's colour: dark enough to be a colour, light enough that
        // the black devices on it still read.
        var counterTop = Plain("P3_CounterTop", new Color(0.09f, 0.3f, 0.45f), 0.62f);
        var screenGlow = Emissive("P3_TerminalScreen", new Color(0.06f, 0.16f, 0.32f), new Color(0.12f, 0.32f, 0.7f) * 0.9f);

        // Counter: warm white body, blue top running on over the extension, navy front with the store name toward the customers.
        var counter = Find(environment, "Counter");
        counter.GetComponent<Renderer>().enabled = false;
        Find(environment, "Counter Extension").GetComponent<Renderer>().enabled = false;
        var c = counter.transform.position;
        var size = counter.transform.localScale;
        float wholeLeft = c.x - size.x * 0.5f, wholeRight = P2StoreBuilder.CounterExtensionEnd;
        float wholeCenter = (wholeLeft + wholeRight) * 0.5f, wholeLength = wholeRight - wholeLeft;
        var counterBody = Load<Material>($"{P2Materials}/P2_Counter.mat");
        Visual("Counter Body", parent, new Vector3(wholeCenter, 0.08f + (size.y - 0.115f) * 0.5f, c.z), new Vector3(wholeLength - 0.02f, size.y - 0.115f, size.z - 0.02f), counterBody, 0.012f);
        Visual("Counter Kick", parent, new Vector3(wholeCenter, 0.04f, c.z - 0.01f), new Vector3(wholeLength - 0.06f, 0.08f, size.z - 0.06f), graphite, 0.004f);
        Visual("Counter Top", parent, new Vector3(wholeCenter, size.y - 0.0175f, c.z), new Vector3(wholeLength + 0.04f, 0.035f, size.z + 0.04f), counterTop, 0.01f);
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

        // The till: a steel box with a drawer, the insert that holds the money, and the money in it.
        CashRegisterLooks(environment);
        float tillTop = 0.9f + P2StoreBuilder.RegisterHeight;

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

        // Coin dispenser, there once the store has ordered it (docs/p2/해금_성장_기획_2026-11-10.md 5): a small machine left
        // of the change tray, under the POS screen, with a chute onto the tray, where PaymentDesk lays the coins. Looks only.
        var changeTray = Find(environment, "Change Tray").transform;
        var coins = EquipmentGroup(environment, GameManager.CoinLook);
        var machine = new Vector3(changeTray.position.x - changeTray.localScale.x * 0.5f - 0.075f, size.y, changeTray.position.z + 0.02f);
        Visual("Coin Dispenser Body", coins, machine + new Vector3(0f, 0.065f, 0f), new Vector3(0.11f, 0.13f, 0.13f), graphite, 0.012f);
        Visual("Coin Dispenser Hopper", coins, machine + new Vector3(0f, 0.134f, 0f), new Vector3(0.09f, 0.01f, 0.11f), steel, 0.004f);
        Visual("Coin Dispenser Display", coins, machine + new Vector3(0f, 0.095f, -0.066f), new Vector3(0.07f, 0.028f, 0.004f), screenGlow, 0.002f);
        Visual("Coin Dispenser Chute", coins, machine + new Vector3(0.075f, 0.035f, 0f), new Vector3(0.07f, 0.004f, 0.05f), steel, 0.001f).transform.rotation = Quaternion.Euler(0f, 0f, -28f);

        // The bag: thin white film with the store's mark printed on both sides, standing open where the builder put its foot.
        var film = Lit("P3_BagFilm", $"{Generated}/bag_film", Vector2.one, Color.white);
        film.SetFloat("_Smoothness", 0.56f);
        var bag = new GameObject("Plastic Bag", typeof(MeshFilter), typeof(MeshRenderer));
        bag.transform.SetParent(parent, false);
        bag.transform.position = Find(null, "Bag").transform.position;
        bag.GetComponent<MeshFilter>().sharedMesh = P3Meshes.PlasticBag();
        var bagRenderer = bag.GetComponent<MeshRenderer>();
        bagRenderer.sharedMaterial = film;
        // A third of a metre across: at the room's texel size (P3StoreLighting) each of its folds would get less than one texel.
        bagRenderer.scaleInLightmap = 5f;

        // The same bag as a customer carries it out: the pattern the director copies, switched off, so it takes no part in
        // the bake. A copy moves with a customer and is lit by the probes.
        var carried = new GameObject("Carried Bag", typeof(MeshFilter), typeof(MeshRenderer), typeof(CarriedBag));
        carried.transform.SetParent(Find(null, "Bag").transform, false);
        carried.GetComponent<MeshFilter>().sharedMesh = P3Meshes.CarriedBag();
        carried.GetComponent<MeshRenderer>().sharedMaterial = film;
        carried.SetActive(false);
        var director = UnityEngine.Object.FindFirstObjectByType<CustomerDirector>() ?? throw new Exception("Dressing: CustomerDirector missing.");
        director.BagToCarry = carried.GetComponent<CarriedBag>();
        // Not drawn while a customer lifts theirs out of its place (CustomerDirector.TakeBag).
        director.BagOnCounter = bagRenderer;

        // Two screens on stands: the POS on the left, the order screen on the right. Plain backs.
        foreach (var (center, sizePx) in new[] { (posScreen, new Vector2(860f, 660f)), (orderScreen, new Vector2(620f, 470f)) })
        {
            var rotation = Quaternion.LookRotation(center - eye);
            var panel = sizePx * 0.0007f;
            var housing = Visual("Screen Housing", parent, center + rotation * new Vector3(0f, 0f, 0.019f), new Vector3(panel.x + 0.04f, panel.y + 0.04f, 0.03f), graphite, 0.012f);
            housing.transform.rotation = rotation;
            var neckTop = center + rotation * new Vector3(0f, -panel.y * 0.5f + 0.04f, 0.04f);
            // The POS monitor stands on the till, as it does in a store; the order screen stands on the counter.
            bool onTill = center.x < 0f;
            var baseAt = onTill
                ? new Vector3(P2StoreBuilder.RegisterX + 0.09f, tillTop, far - 0.09f)
                : new Vector3(center.x, 0.9f, Mathf.Min(center.z - 0.1f, far - 0.08f));
            var neck = Visual("Screen Neck", parent, (neckTop + baseAt) * 0.5f, new Vector3(0.04f, Vector3.Distance(neckTop, baseAt), 0.025f), steel, 0.008f);
            neck.transform.rotation = Quaternion.FromToRotation(Vector3.up, (neckTop - baseAt).normalized);
            Visual("Screen Foot", parent, baseAt + new Vector3(0f, 0.006f, 0f), onTill ? new Vector3(0.18f, 0.012f, 0.12f) : new Vector3(0.2f, 0.012f, 0.13f), graphite, 0.005f);
        }

        // Receipt printer on the till, in front of the monitor's left end and below it.
        var printer = new Vector3(P2StoreBuilder.RegisterX - 0.125f, tillTop + 0.05f, P2StoreBuilder.RegisterFront + 0.1f);
        Visual("Receipt Printer", parent, printer, new Vector3(0.13f, 0.1f, 0.15f), graphite, 0.014f);
        Visual("Receipt Paper", parent, printer + new Vector3(0f, 0.055f, 0.04f), new Vector3(0.07f, 0.003f, 0.06f), white, 0.001f).transform.rotation = Quaternion.Euler(-25f, 0f, 0f);
    }

    /// <summary>
    /// The till P2StoreBuilder.BuildCashRegister laid out (2026-11-10): a dark steel body open toward the clerk, and in it the
    /// drawer with its front and lock, the insert with four note slots and four coin cups, the wads and heaps of money in
    /// them, the clips that hold the notes down, and a label on the lip in front of each coin cup, where a store sticks
    /// one. Everything under "Cash Drawer" slides with the drawer, so none of it is lightmapped (P3StoreLighting).
    /// </summary>
    static void CashRegisterLooks(Transform environment)
    {
        var root = Find(environment, "Cash Register").transform;
        var drawer = root.Find("Cash Drawer") ?? throw new Exception("Dressing: the till has no drawer.");
        var o = root.position;
        float w = P2StoreBuilder.RegisterWidth, d = P2StoreBuilder.RegisterDepth, h = P2StoreBuilder.RegisterHeight;
        float floor = P2StoreBuilder.TillFloor, rim = P2StoreBuilder.TillRim, half = P2StoreBuilder.TillHalfWidth;
        float coinFront = P2StoreBuilder.CoinFront, coinBack = P2StoreBuilder.CoinBack, noteFront = P2StoreBuilder.NoteFront, noteBack = P2StoreBuilder.NoteBack;
        var shell = Plain("P3_TillShell", new Color(0.075f, 0.08f, 0.09f), 0.55f, 0.3f);
        var insert = Plain("P3_TillInsert", new Color(0.12f, 0.125f, 0.14f), 0.35f);
        var insertFloor = Plain("P3_TillInsertFloor", new Color(0.17f, 0.18f, 0.195f), 0.25f);

        // Body: top, sides, bottom and back. The face toward the clerk is the drawer's.
        const float plate = 0.01f, wall = 0.008f, frontThick = 0.014f;
        // The top reaches out over the drawer's front, as a till's cover does: from above, nothing inside shows past it.
        float brow = frontThick + 0.003f;
        Visual("Till Top", root, o + new Vector3(0f, h - plate * 0.5f, (d - brow) * 0.5f), new Vector3(w, plate, d + brow), shell, 0.004f);
        foreach (float side in new[] { -1f, 1f })
            Visual("Till Side", root, o + new Vector3(side * (w - wall) * 0.5f, (h - plate) * 0.5f, d * 0.5f), new Vector3(wall, h - plate, d), shell, 0.003f);
        Visual("Till Bottom", root, o + new Vector3(0f, 0.003f, d * 0.5f), new Vector3(w, 0.006f, d), shell, 0.002f);
        Visual("Till Back", root, o + new Vector3(0f, h * 0.5f, d - wall * 0.5f), new Vector3(w, h, wall), shell, 0.003f);

        // Drawer: the front with its lock, and the steel tray the insert sits in.
        const float frontBottom = 0.01f, frontTop = 0.092f;
        Visual("Drawer Front", drawer, o + new Vector3(0f, (frontBottom + frontTop) * 0.5f, -frontThick * 0.5f), new Vector3(w - 0.004f, frontTop - frontBottom, frontThick), shell, 0.004f);
        var lockAt = o + new Vector3(0f, 0.064f, -frontThick - 0.001f);
        Round("Drawer Lock", drawer, lockAt, 0.02f, 0.003f, Quaternion.Euler(90f, 0f, 0f), steel);
        Visual("Drawer Keyhole", drawer, lockAt + new Vector3(0f, 0f, -0.0018f), new Vector3(0.003f, 0.009f, 0.001f), graphite, 0.0004f);
        float trayLength = noteBack + 0.005f, trayWidth = half * 2f + 0.016f;
        Visual("Drawer Tray Floor", drawer, o + new Vector3(0f, 0.014f, trayLength * 0.5f), new Vector3(trayWidth, 0.004f, trayLength), shell, 0.0015f);
        foreach (float side in new[] { -1f, 1f })
            Visual("Drawer Tray Side", drawer, o + new Vector3(side * (trayWidth - 0.004f) * 0.5f, 0.047f, trayLength * 0.5f), new Vector3(0.004f, 0.07f, trayLength), shell, 0.0015f);
        Visual("Drawer Tray Back", drawer, o + new Vector3(0f, 0.047f, trayLength + 0.002f), new Vector3(trayWidth, 0.07f, 0.004f), shell, 0.0015f);

        // The insert: a floor, a rim, a wide lip in front for the labels, a wall between coins and notes, three dividers.
        float insertFront = 0.004f, insertBack = noteBack + 0.004f;
        float insertLength = insertBack - insertFront, insertMiddle = (insertFront + insertBack) * 0.5f;
        float wallHeight = rim - floor + 0.006f, wallY = (floor - 0.006f + rim) * 0.5f;
        Visual("Till Insert Floor", drawer, o + new Vector3(0f, floor - 0.003f, insertMiddle), new Vector3(half * 2f, 0.006f, insertLength), insertFloor, 0.002f);
        foreach (float side in new[] { -1f, 1f })
            Visual("Till Insert Side", drawer, o + new Vector3(side * (half - 0.002f), wallY, insertMiddle), new Vector3(0.004f, wallHeight, insertLength), insert, 0.0015f);
        Visual("Till Insert Back", drawer, o + new Vector3(0f, wallY, insertBack - 0.002f), new Vector3(half * 2f, wallHeight, 0.004f), insert, 0.0015f);
        float lip = coinFront - insertFront;
        Visual("Till Insert Lip", drawer, o + new Vector3(0f, wallY, insertFront + lip * 0.5f), new Vector3(half * 2f, wallHeight, lip), insert, 0.002f);
        Visual("Till Insert Cross", drawer, o + new Vector3(0f, wallY - 0.003f, (coinBack + noteFront) * 0.5f), new Vector3(half * 2f, wallHeight - 0.006f, noteFront - coinBack), insert, 0.0015f);
        for (int i = 1; i < 4; i++)
            Visual("Till Insert Divider", drawer, o + new Vector3(-half + i * half * 0.5f, wallY - 0.003f, (coinFront + noteBack) * 0.5f), new Vector3(0.004f, wallHeight - 0.006f, noteBack - coinFront), insert, 0.0015f);

        var noteMesh = P3Meshes.Note();
        var coinMesh = P3Meshes.Coin();
        var random = new System.Random(1110);
        float Jitter(float range) => ((float)random.NextDouble() * 2f - 1f) * range;

        // Notes: a wad in each slot under a sprung clip. The piece on top, which the clerk takes, is laid there while playing.
        for (int i = 0; i < P2StoreBuilder.NoteValues.Length; i++)
        {
            int value = P2StoreBuilder.NoteValues[i];
            float x = P2StoreBuilder.TillColumn(i), z = (noteFront + noteBack) * 0.5f, length = P2StoreBuilder.NoteLength(value);
            var material = Load<Material>($"{P2Materials}/P2_Note{value}.mat");
            // Two layers a little askew: a wad of used notes is never square.
            float lower = P2StoreBuilder.NoteStack * 0.65f, upper = P2StoreBuilder.NoteStack - lower;
            Money("Note Wad", drawer, noteMesh, material, o + new Vector3(x, floor + lower * 0.5f, z), Quaternion.Euler(0f, -90f + Jitter(1.2f), 0f), new Vector3(length, lower, P2StoreBuilder.NoteWidth));
            Money("Note Wad", drawer, noteMesh, material, o + new Vector3(x + Jitter(0.0012f), floor + lower + upper * 0.5f, z + Jitter(0.002f)), Quaternion.Euler(0f, -90f + Jitter(1.5f), 0f), new Vector3(length, upper, P2StoreBuilder.NoteWidth));

            float top = floor + P2StoreBuilder.NoteStack + P2StoreBuilder.NoteThickness;
            var hinge = o + new Vector3(x, top + 0.014f, noteBack - 0.006f);
            // Short, so it holds the notes by their end and the number in the middle stays in sight.
            var pad = o + new Vector3(x, top + 0.0045f, noteBack - 0.03f);
            Visual("Note Clip Hinge", drawer, hinge, new Vector3(0.026f, 0.012f, 0.012f), steel, 0.003f);
            var arm = Visual("Note Clip Arm", drawer, (hinge + pad) * 0.5f, new Vector3(0.012f, 0.0025f, Vector3.Distance(hinge, pad)), steel, 0.001f);
            arm.transform.rotation = Quaternion.LookRotation(pad - hinge);
            Visual("Note Clip Pad", drawer, pad + new Vector3(0f, -0.001f, -0.004f), new Vector3(0.03f, 0.004f, 0.012f), steel, 0.0015f);
        }

        // Coins: a heap in each cup, lying flat in layers and thinning toward the top, and the cup's label on the lip.
        for (int i = 0; i < P2StoreBuilder.CoinValues.Length; i++)
        {
            int value = P2StoreBuilder.CoinValues[i];
            float x = P2StoreBuilder.TillColumn(i), z = (coinFront + coinBack) * 0.5f;
            float diameter = P2StoreBuilder.CoinDiameter(value), thickness = P2StoreBuilder.CoinThickness;
            var material = Load<Material>($"{P2Materials}/P2_Coin{value}.mat");
            float cupX = half * 0.5f - 0.006f, cupZ = coinBack - coinFront - 0.004f;
            int across = Mathf.Max(1, Mathf.FloorToInt(cupX / (diameter + 0.001f))), along = Mathf.Max(1, Mathf.FloorToInt(cupZ / (diameter + 0.001f)));
            float cellX = cupX / across, cellZ = cupZ / along;
            int layers = Mathf.RoundToInt(P2StoreBuilder.CoinHeap / thickness);
            for (int layer = 0; layer < layers; layer++)
                for (int ix = 0; ix < across; ix++)
                    for (int iz = 0; iz < along; iz++)
                    {
                        // The middle is always there, for the coin on top to lie on.
                        bool middle = Mathf.Abs(ix - (across - 1) * 0.5f) <= 0.5f && Mathf.Abs(iz - (along - 1) * 0.5f) <= 0.5f;
                        if (!middle && random.NextDouble() < layer * 0.28) continue;
                        var at = o + new Vector3(
                            x + (ix - (across - 1) * 0.5f) * cellX + Jitter((cellX - diameter) * 0.5f),
                            floor + thickness * (layer + 0.5f),
                            z + (iz - (along - 1) * 0.5f) * cellZ + Jitter((cellZ - diameter) * 0.5f));
                        Money("Coin", drawer, coinMesh, material, at, Quaternion.Euler(0f, Jitter(180f), 0f), new Vector3(diameter, thickness, diameter));
                    }

            var label = GameObject.CreatePrimitive(PrimitiveType.Quad);
            label.name = "Coin Cup Label";
            UnityEngine.Object.DestroyImmediate(label.GetComponent<Collider>());
            label.transform.SetParent(drawer, false);
            // Read from the clerk's side, looking down at the lip.
            label.transform.SetPositionAndRotation(o + new Vector3(x, rim + 0.0006f, insertFront + lip * 0.5f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
            label.transform.localScale = new Vector3(0.04f, 0.015f, 1f);
            var printed = Asset($"P3_TillLabel{value}");
            Paint(printed, null, Color.white, 0.2f, Vector2.one, 0f);
            printed.SetTexture("_BaseMap", Load<Texture2D>($"{CashArt}/label_{value}.png"));
            var labelRenderer = label.GetComponent<Renderer>();
            labelRenderer.sharedMaterial = printed;
            labelRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    /// <summary>A note, a wad of notes or a coin: one of the two money meshes, scaled to size.</summary>
    static void Money(string name, Transform parent, Mesh mesh, Material material, Vector3 at, Quaternion rotation, Vector3 size)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(at, rotation);
        go.transform.localScale = size;
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>A small round part, such as a lock: Unity's cylinder, whose axis is y before <paramref name="rotation"/>.</summary>
    static void Round(string name, Transform parent, Vector3 center, float diameter, float thickness, Quaternion rotation, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rotation);
        // The cylinder is 2 units tall.
        go.transform.localScale = new Vector3(diameter, thickness * 0.5f, diameter);
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    // ---------- Pieces ----------

    /// <summary>
    /// The looks of one piece of equipment a store that is kept orders in: a group under "Equipment Looks", off as built
    /// (so the fixed days never show it and the bake leaves it out) and switched on by GameManager.OpenStore.
    /// </summary>
    static Transform EquipmentGroup(Transform environment, string name)
    {
        var root = environment.Find(GameManager.EquipmentLooks);
        if (root == null)
        {
            root = new GameObject(GameManager.EquipmentLooks).transform;
            root.SetParent(environment, false);
        }
        var group = new GameObject(name).transform;
        group.SetParent(root, false);
        group.gameObject.SetActive(false);
        return group;
    }

    /// <summary>A flat cylinder with no collider, its axis upright.</summary>
    static GameObject Disc(string name, Transform parent, Vector3 center, float diameter, float thickness, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = new Vector3(diameter, thickness * 0.5f, diameter);
        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return go;
    }

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
            if (importer.materialImportMode == materials && !importer.importAnimation && !importer.importCameras && !importer.importLights && importer.generateSecondaryUV) continue;
            importer.materialImportMode = materials;
            importer.generateSecondaryUV = true;  // for the baked light (P3StoreLighting)
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
        // A scene opened in batch mode draws its baked reflection probes black until their picture is set again (seen
        // 2026-11-10, Unity 6000.3.26f1). Only for these stills: the scene is not saved.
        foreach (var probe in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
        {
            var picture = probe.bakedTexture;
            probe.mode = ReflectionProbeMode.Custom;
            probe.customBakedTexture = picture;
        }
        var views = new List<(string name, Vector3 position, Vector3 lookAt)>
        {
            // The reference photo's viewpoint: over the near end of the counter, looking along it toward the fridges.
            ("00_photo_match", new Vector3(3.5f, 1.62f, 1.4f), new Vector3(-6f, 0.95f, 1.15f)),
            ("01_clerk_view", new Vector3(0f, 1.6f, -0.3f), new Vector3(0f, 1.25f, 4f)),
            ("02_from_door", new Vector3(-3.6f, 1.6f, -1.2f), new Vector3(0.5f, 1.0f, 1.8f)),
            ("02b_first_look", new Vector3(-3.6f, 1.6f, -2.6f), new Vector3(-3.6f, 1.25f, 2f)),
            ("03_fridge_wall", new Vector3(-2.2f, 1.6f, 2.2f), new Vector3(-6f, 1.3f, 1.4f)),
            ("04_counter_clerk", new Vector3(0f, 1.5f, -0.2f), new Vector3(0f, 0.95f, 0.7f)),
            ("05_counter_customer", new Vector3(0.3f, 1.55f, 2.4f), new Vector3(0f, 0.95f, 0.6f)),
            ("06_chilled_cases", new Vector3(1.6f, 1.6f, 3.2f), new Vector3(-2f, 1.2f, 5.4f)),
            ("07_aisle_back", new Vector3(-4.6f, 1.6f, 4f), new Vector3(2f, 1f, 4.2f)),
            ("08_overview", new Vector3(4.1f, 2.7f, 4.3f), new Vector3(-3f, 0.4f, 0.5f)),
            ("09_tobacco_wall", new Vector3(0.1f, 1.6f, 0.2f), new Vector3(0.1f, 1.5f, -1.75f)),
            ("10_scanner_clerk", new Vector3(0.22f, 1.28f, 0.12f), new Vector3(0f, 1.0f, 0.62f)),
            ("11_scanner_side", new Vector3(0.6f, 1.12f, 0.42f), new Vector3(0f, 1.02f, 0.7f)),
            ("12_microwave_corner", new Vector3(-4.4f, 1.55f, 3.9f), new Vector3(-5.8f, 1.05f, 3.9f)),
            ("15_counter_extension", new Vector3(2.6f, 1.45f, 1.5f), new Vector3(1f, 0.85f, 0.65f)),
            ("16_freezer", new Vector3(2.4f, 1.5f, 3.4f), new Vector3(0.7f, 0.7f, 3.45f)),
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
            camera.fieldOfView = name.StartsWith("00_") ? 62f : 75f;  // about the reference photo's lens
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
        Shot("13_microwave_door_open", new Vector3(-4.6f, 1.4f, 4f), new Vector3(-5.75f, 1.05f, 4f));
        hinge.localRotation = Quaternion.identity;
        Find(null, "Microwave Lit").SetActive(true);
        Find(null, "Dispenser Water").SetActive(true);
        Shot("14_running_and_pouring", new Vector3(-4.6f, 1.4f, 3.9f), new Vector3(-5.75f, 1.05f, 3.9f));

        // The till shut, then with its drawer out as for a cash sale (not saved). The pieces the clerk takes are laid on the
        // wads and heaps while playing, so these show the drawer as it is under them.
        Shot("17_till_shut", new Vector3(-0.42f, 1.45f, 0f), new Vector3(-0.78f, 0.98f, 0.62f));
        var till = Find(null, "Cash Drawer").transform;
        till.localPosition += Vector3.back * P2StoreBuilder.DrawerTravel;
        Shot("18_till_open", new Vector3(-0.42f, 1.45f, 0f), new Vector3(-0.78f, 0.96f, 0.5f));
        Shot("19_till_open_from_above", new Vector3(-0.78f, 1.42f, 0.16f), new Vector3(-0.78f, 0.95f, 0.4f));
        Shot("20_till_open_from_the_start", new Vector3(0f, 1.6f, -0.05f), new Vector3(-0.72f, 0.95f, 0.5f));
        // The bag: from where the clerk stands, close from the scanner's side, and from the customer's side.
        Shot("21_bag_from_the_clerk", new Vector3(-0.2f, 1.5f, -0.1f), new Vector3(-0.4f, 1.0f, 0.56f));
        Shot("22_bag_close", new Vector3(-0.08f, 1.22f, 0.22f), new Vector3(-0.4f, 1.0f, 0.56f));
        Shot("23_bag_from_the_customer", new Vector3(-0.15f, 1.5f, 1.55f), new Vector3(-0.4f, 1.0f, 0.56f));

        RenderTexture.active = null;
        UnityEngine.Object.DestroyImmediate(go);
        Debug.Log($"[P3] Rendered {views.Count + 6} views to {dir}");
    }
}
