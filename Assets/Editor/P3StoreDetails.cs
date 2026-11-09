using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// What makes the room read as a working store rather than a showroom (2026-11-09): the terrazzo floor, price tags,
/// misted fridge doors and closed cartons waiting on the bare shelves. The textures come from
/// scripts/windows/make_store_signage.py. Light and shadow are not painted in: P3StoreLighting bakes them.
///
/// Words only where a real store has them (2026-11-10): the name, section names, price tags, the legal notices. A place
/// with no product stays plain rather than being filled with a slogan, a poster or a label.
///
/// Looks only, and no colliders, so the NavMesh, the routes and every rule stay as they were. Everything is an opaque
/// surface on purpose: world-space UI draws over the splat products, which write no depth. No picture or model of a product
/// is used (D9): the cartons are shipping cartons, the same as the delivery boxes.
/// </summary>
public static class P3StoreDetails
{
    const string Folder = "Assets/Art/Signage";
    const string MaterialFolder = "Assets/Materials/P3/Signage";
    const string P2Materials = "Assets/Materials/P2";

    const float InnerFar = P2StoreBuilder.RoomFar + 0.06f, InnerRight = P2StoreBuilder.RoomNear - 0.06f, InnerBack = P2StoreBuilder.RoomBack + 0.06f;
    const float InnerFront = P2StoreBuilder.RoomFront - 0.06f;
    const float ShelfFront = P2StoreBuilder.ShelfFront, ShelfBack = P2StoreBuilder.ShelfBack, SpineZ = (ShelfFront + ShelfBack) * 0.5f;
    const float ShelfA = P2StoreBuilder.ShelfAX, ShelfB = P2StoreBuilder.ShelfBX, ShelfD = P2StoreBuilder.ShelfDX;
    const float ShelfDFront = P2StoreBuilder.ShelfDFront, ShelfDBack = P2StoreBuilder.ShelfDBack, SpineDZ = (ShelfDFront + ShelfDBack) * 0.5f;
    const float StorefrontZ = P2StoreBuilder.StorefrontZ, PartitionX = P2StoreBuilder.PartitionX;
    const float RailLength = 1.46f, RailHeight = 0.042f;
    static readonly float[] GondolaBoards = { 0.1f, 0.55f, 1.0f, 1.4f };
    // Shelf C (P3StoreDressing.ShelfC).
    const float ShelfCFace = 3.99f, ShelfCNear = 1.4f, ShelfCFar = 4.42f;
    static readonly float[] ShelfCBoards = { 0.1f, 0.5f, 0.9f, 1.3f };

    static readonly Quaternion SeenLookingFront = Quaternion.identity;
    static readonly Quaternion SeenLookingBack = Quaternion.Euler(0f, 180f, 0f);
    static readonly Quaternion SeenLookingRight = Quaternion.Euler(0f, 90f, 0f);
    static readonly Quaternion SeenLookingLeft = Quaternion.Euler(0f, -90f, 0f);

    static readonly Dictionary<string, Material> Made = new Dictionary<string, Material>();
    static System.Random random;
    static Material cardboard;

    public static void Apply(Transform environment)
    {
        Directory.CreateDirectory(MaterialFolder);
        ConfigureImports();
        Made.Clear();
        random = new System.Random(20261109);
        cardboard = Load<Material>($"{P2Materials}/P2_Cardboard.mat");

        var root = new GameObject("Store Details").transform;
        root.SetParent(environment, false);
        Floor(environment, root);
        GondolaBacks(root);
        PriceRails(root);
        Cartons(root);
        EndBay(root);
        Posters(root);
        CategorySign(root);
        FridgeDoors(environment, root);
        ChilledTrays(root);
        TobaccoFaces(root);
        FreezerStock(root);
        CounterExtensionThings(root);
        Entrance(root);
        Street(root);
        SmallThings(root);

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
        Debug.Log($"[P3] Store details: {root.GetComponentsInChildren<Renderer>(true).Length} renderers, {Made.Count} materials");
    }

    /// <summary>One picture of the whole floor: polished terrazzo tiles, and the pavement outside the glass. The floor box under it stays hidden.</summary>
    static void Floor(Transform environment, Transform root)
    {
        foreach (var t in environment.GetComponentsInChildren<Transform>(true))
            if (t.name == "Floor Visual") t.GetComponent<Renderer>().enabled = false;
        var floor = Printed("floor_terrazzo", 0.72f);
        float width = P2StoreBuilder.RoomNear - P2StoreBuilder.RoomFar, depth = P2StoreBuilder.RoomFront - P2StoreBuilder.RoomBack;
        Surface("Floor Painted", root, new Vector3((P2StoreBuilder.RoomNear + P2StoreBuilder.RoomFar) * 0.5f, 0.002f, (P2StoreBuilder.RoomFront + P2StoreBuilder.RoomBack) * 0.5f), new Vector2(width, depth), Quaternion.Euler(90f, 0f, 0f), floor);
    }

    // ---------- Gondolas and shelf C ----------

    /// <summary>The dark back panel behind each side of each gondola.</summary>
    static void GondolaBacks(Transform root)
    {
        var back = Plain("Back_Dark", new Color(0.17f, 0.18f, 0.19f));
        foreach (var (x, spine) in new[] { (ShelfA, SpineZ), (ShelfB, SpineZ), (ShelfD, SpineDZ) })
        {
            Surface("Gondola Back Front", root, new Vector3(x, 0.95f, spine - 0.016f), new Vector2(RailLength, 1.7f), SeenLookingFront, back);
            Surface("Gondola Back Rear", root, new Vector3(x, 0.95f, spine + 0.016f), new Vector2(RailLength, 1.7f), SeenLookingBack, back);
        }
    }

    static void PriceRails(Transform root)
    {
        float front = ShelfFront - 0.0095f, back = ShelfBack + 0.0095f;
        var frontA = new[] { "rail_lower_snack", "rail_lower_noodle", "rail_a_1000", "rail_a_1400" };
        var frontB = new[] { "rail_lower_noodle", "rail_lower_snack", "rail_b_1000", "rail_b_1400" };
        var rowD = new[] { "rail_lower_noodle", "rail_lower_snack", "rail_back_a", "rail_lower_noodle" };
        for (int i = 0; i < GondolaBoards.Length; i++)
        {
            float y = GondolaBoards[i] - 0.021f;
            Surface("Price Tags", root, new Vector3(ShelfA, y, front), new Vector2(RailLength, RailHeight), SeenLookingFront, Printed(frontA[i]));
            Surface("Price Tags", root, new Vector3(ShelfB, y, front), new Vector2(RailLength, RailHeight), SeenLookingFront, Printed(frontB[i]));
            Surface("Price Tags", root, new Vector3(ShelfA, y, back), new Vector2(RailLength, RailHeight), SeenLookingBack, Printed("rail_back_a"));
            Surface("Price Tags", root, new Vector3(ShelfB, y, back), new Vector2(RailLength, RailHeight), SeenLookingBack, Printed("rail_back_b"));
            Surface("Price Tags", root, new Vector3(ShelfD, y, ShelfDFront - 0.0095f), new Vector2(RailLength, RailHeight), SeenLookingFront, Printed(rowD[i]));
            Surface("Price Tags", root, new Vector3(ShelfD, y, ShelfDBack + 0.0095f), new Vector2(RailLength, RailHeight), SeenLookingBack, Printed(rowD[(i + 1) % rowD.Length]));
        }
        float zc = (ShelfCNear + ShelfCFar) * 0.5f, length = ShelfCFar - ShelfCNear - 0.06f;
        foreach (var top in ShelfCBoards)
            Surface("Price Tags", root, new Vector3(ShelfCFace - 0.0095f, top - 0.021f, zc), new Vector2(length, RailHeight), SeenLookingRight, Printed("rail_shelf_c"));
    }

    /// <summary>
    /// Closed shipping cartons where no product stands, as in a store mid-restock. The delivery slots (A-1, A-2, A-3, B-1,
    /// B-2 on the boards at 1.0 and 1.4) are left clear.
    /// </summary>
    static void Cartons(Transform root)
    {
        var parent = new GameObject("Cartons").transform;
        parent.SetParent(root, false);
        const float inset = 0.71f, front = ShelfFront + 0.02f, back = ShelfBack - 0.02f, depth = 0.19f;
        foreach (var x in new[] { ShelfA, ShelfB })
            foreach (var board in new[] { 0.1f, 0.55f })
            {
                RowAlongX(parent, x - inset, x + inset, board, front + depth * 0.5f, depth, 0.38f);
                RowAlongX(parent, x - inset, x + inset, board, back - depth * 0.5f, depth, 0.38f);
            }
        foreach (var x in new[] { ShelfA, ShelfB })
            RowAlongX(parent, x - inset, x + inset, 1.0f, back - depth * 0.5f, depth, 0.34f);
        // The boards at 1.4 and row D's face toward the street hold real products (P2StoreBuilder.BuildDecorSlots).
        RowAlongX(parent, ShelfB - inset, ShelfB - inset + 0.34f, 1.0f, front + depth * 0.5f, depth, 0.34f);

        // Row D holds no deliveries: every board on the far side, the bottom board on the near side, and the overstock on top.
        foreach (var board in GondolaBoards)
        {
            if (board < 0.5f) RowAlongX(parent, ShelfD - inset, ShelfD + inset, board, ShelfDFront + 0.02f + depth * 0.5f, depth, 0.34f);
            RowAlongX(parent, ShelfD - inset, ShelfD + inset, board, ShelfDBack - 0.02f - depth * 0.5f, depth, 0.34f);
        }
        RowAlongX(parent, ShelfD - inset, ShelfD + inset, 1.8f, SpineDZ, 0.3f, 0.3f);
        RowAlongX(parent, ShelfA - inset, ShelfB + inset, 1.8f, SpineZ, 0.3f, 0.3f);

        foreach (var board in ShelfCBoards)
        {
            float z = ShelfCNear + 0.04f;
            while (z < ShelfCFar - 0.32f)
            {
                float width = Range(0.28f, 0.36f), height = Range(0.24f, 0.34f);
                Cube("Carton", parent, new Vector3(ShelfCFace + 0.22f, board + height * 0.5f, z + width * 0.5f), new Vector3(0.38f, height, width), cardboard);
                z += width + Range(0.02f, 0.06f);
            }
        }
    }

    static void RowAlongX(Transform parent, float from, float to, float board, float z, float depth, float maxHeight)
    {
        float x = from;
        while (true)
        {
            float width = Range(0.26f, 0.34f), height = Range(0.22f, maxHeight);
            if (x + width > to) break;
            Cube("Carton", parent, new Vector3(x + width * 0.5f, board + height * 0.5f, z), new Vector3(width, height, depth), cardboard);
            x += width + Range(0.015f, 0.05f);
        }
    }

    /// <summary>A shallow bay of shelves on the end of row D that faces the near end of the store, the end the photo looks at.</summary>
    static void EndBay(Transform root)
    {
        var parent = new GameObject("Row D End Bay").transform;
        parent.SetParent(root, false);
        var frame = Plain("Back_Dark", new Color(0.17f, 0.18f, 0.19f));
        float x0 = ShelfD + 0.75f, depth = P2StoreBuilder.ShelfDBayDepth, xc = x0 + depth * 0.5f;
        const float bayWidth = ShelfDBack - ShelfDFront;
        Cube("End Bay Back", parent, new Vector3(x0 + 0.01f, 0.95f, SpineDZ), new Vector3(0.02f, 1.9f, bayWidth), frame);
        foreach (float side in new[] { -1f, 1f })
            Cube("End Bay Side", parent, new Vector3(xc, 0.95f, SpineDZ + side * (bayWidth * 0.5f - 0.01f)), new Vector3(depth, 1.9f, 0.02f), frame);
        foreach (var board in new[] { 0.1f, 0.5f, 0.9f, 1.3f })
        {
            Cube("End Bay Board", parent, new Vector3(xc, board - 0.01f, SpineDZ), new Vector3(depth, 0.02f, bayWidth - 0.04f), frame);
            Surface("End Bay Tags", parent, new Vector3(x0 + depth + 0.003f, board - 0.021f, SpineDZ), new Vector2(bayWidth - 0.05f, RailHeight), SeenLookingLeft, Printed("rail_lower_snack"));
            float z = ShelfDFront + 0.03f;
            while (z < ShelfDBack - 0.17f)
            {
                float w = Range(0.13f, 0.17f), h = Range(0.2f, 0.3f);
                Cube("Carton", parent, new Vector3(xc + 0.03f, board + h * 0.5f, z + w * 0.5f), new Vector3(0.22f, h, w), cardboard);
                z += w + 0.012f;
            }
        }
        Cube("End Bay Top", parent, new Vector3(xc, 1.9f, SpineDZ), new Vector3(depth, 0.02f, bayWidth), frame);
    }

    // ---------- Walls, ceiling, doors ----------

    /// <summary>One notice, back of house by the warehouse door. The walls the customers see carry no posters.</summary>
    static void Posters(Transform root)
    {
        Surface("Poster", root, new Vector3(1.9f, 1.6f, InnerBack + 0.003f), new Vector2(0.56f, 0.79f), SeenLookingBack, Printed("poster_recruit"));
    }

    /// <summary>The photo's box sign, high up at the near end of the rows, read from the counter side and from the door side.</summary>
    static void CategorySign(Transform root)
    {
        var center = new Vector3(ShelfD + 0.9f, 2.55f, SpineDZ);
        var size = new Vector3(0.1f, 0.58f, 0.52f);
        Cube("Category Box", root, center, size, Plain("Sign_Backing", new Color(0.92f, 0.92f, 0.9f)));
        Surface("Category Face", root, center + new Vector3(0.052f, 0f, 0f), new Vector2(0.5f, 0.56f), SeenLookingLeft, Printed("sign_ready_meals"));
        Surface("Category Face", root, center + new Vector3(-0.052f, 0f, 0f), new Vector2(0.5f, 0.56f), SeenLookingRight, Printed("sign_ready_meals"));
        Surface("Category Edge", root, center + new Vector3(0f, 0f, -0.262f), new Vector2(0.09f, 0.56f), SeenLookingFront, Plain("Sign_Navy", new Color(0.03f, 0.13f, 0.28f)));
        const float top = 2.84f, ceiling = 3f;
        foreach (float side in new[] { -0.2f, 0.2f })
            Cube("Hanging Wire", root, new Vector3(center.x, (top + ceiling) * 0.5f, center.z + side), new Vector3(0.006f, ceiling - top, 0.006f), Plain("Wire", new Color(0.3f, 0.3f, 0.32f)));
    }

    /// <summary>
    /// Misted film on the fridge doors, with nothing written on it: the fridges stay empty until there is a Gaussian budget
    /// for them. A plain lit panel covers the model's own "Icey Drinks" sign over each door.
    /// </summary>
    static void FridgeDoors(Transform environment, Transform root)
    {
        var film = Printed("fridge_mist", 0.55f);
        var topper = Asset("Fridge_Topper");
        topper.SetColor("_BaseColor", new Color(0.62f, 0.7f, 0.8f));
        topper.SetFloat("_Smoothness", 0.4f);
        topper.SetFloat("_Metallic", 0f);
        topper.EnableKeyword("_EMISSION");
        topper.SetColor("_EmissionColor", new Color(0.16f, 0.2f, 0.26f));
        topper.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        EditorUtility.SetDirty(topper);
        for (int i = 1; i <= P3StoreDressing.FridgeCount; i++)
        {
            Transform fridge = null;
            foreach (var t in environment.GetComponentsInChildren<Transform>(true))
                if (t.name == $"Drinks Fridge {i}") fridge = t;
            if (fridge == null) throw new Exception($"Drinks Fridge {i} missing.");
            var bounds = BoundsOf(fridge.gameObject);
            var center = new Vector3(bounds.max.x + 0.003f, bounds.min.y + bounds.size.y * 0.5f + FilmLift, bounds.center.z);
            Surface("Fridge Film", root, center, new Vector2(bounds.size.z * FilmWidth, bounds.size.y * FilmHeight), SeenLookingLeft, film);
            // A box rather than a face, and as wide as the fridge and its gap, so the sign does not show past it from the side.
            float topperHeight = bounds.size.y * TopperHeight;
            Cube("Fridge Topper", root, new Vector3(bounds.max.x + 0.003f - TopperDepth * 0.5f, bounds.max.y - topperHeight * 0.5f, bounds.center.z), new Vector3(TopperDepth, topperHeight, bounds.size.z + 0.012f), topper);
        }
    }

    const float FilmWidth = 0.84f, FilmHeight = 0.74f, FilmLift = 0.05f, TopperHeight = 0.11f, TopperDepth = 0.14f;

    static void Entrance(Transform root)
    {
        var door = P2StoreBuilder.DoorCenter;
        Surface("Entrance Mat", root, new Vector3(door.x, 0.004f, StorefrontZ + 0.6f), new Vector2(2f, 1f), Quaternion.LookRotation(Vector3.down, Vector3.forward), Printed("floor_mat"));
        foreach (var spot in new[] { new Vector3(0.85f, 0.004f, 1.5f), new Vector3(1.7f, 0.004f, 1.65f) })
            Surface("Queue Sticker", root, spot, new Vector2(0.5f, 0.5f), Quaternion.LookRotation(Vector3.down, Vector3.back), Printed("floor_queue"));

        // A stack of red baskets inside the door, the same red as the ones customers carry.
        var red = Plain("Basket_Red", new Color(0.78f, 0.12f, 0.1f));
        var dark = Plain("Basket_Inside", new Color(0.34f, 0.05f, 0.04f));
        var at = new Vector3(-2.15f, 0f, StorefrontZ + 0.3f);
        Cube("Basket Stack", root, at + Vector3.up * 0.17f, new Vector3(0.38f, 0.34f, 0.28f), red);
        for (int i = 0; i < 6; i++)
            Cube("Basket Rim", root, at + Vector3.up * (0.09f + i * 0.055f), new Vector3(0.42f, 0.014f, 0.32f), red);
        Cube("Basket Top", root, at + Vector3.up * 0.345f, new Vector3(0.36f, 0.008f, 0.26f), dark);
    }

    /// <summary>The street seen through the glass: an opaque painted backdrop, a little brighter than the store.</summary>
    static void Street(Transform root)
    {
        float centerX = (P2StoreBuilder.RoomFar + PartitionX) * 0.5f, length = PartitionX - P2StoreBuilder.RoomFar;
        var backdrop = Printed("street_backdrop", 0.1f, new Color(0.85f, 0.85f, 0.85f));
        Surface("Street Picture", root, new Vector3(centerX, 1.5f, StorefrontZ - P2StoreBuilder.VestibuleDepth + 0.002f), new Vector2(length, 3f), SeenLookingBack, backdrop);
        float sideZ = StorefrontZ - P2StoreBuilder.VestibuleDepth * 0.5f, sideLength = P2StoreBuilder.VestibuleDepth;
        Surface("Street Picture", root, new Vector3(P2StoreBuilder.RoomFar + 0.052f, 1.5f, sideZ), new Vector2(sideLength, 3f), SeenLookingLeft, backdrop);
        Surface("Street Picture", root, new Vector3(PartitionX - 0.052f, 1.5f, sideZ), new Vector2(sideLength, 3f), SeenLookingRight, backdrop);
    }

    static void SmallThings(Transform root)
    {
        Surface("Clock", root, new Vector3(InnerRight - 0.003f, 2.42f, 2.2f), new Vector2(0.36f, 0.36f), SeenLookingRight, Printed("clock_face"));

        // The green exit sign over the door, inside, as in the photo.
        var exit = Printed("exit_sign", 0.3f, new Color(0.45f, 1.5f, 0.75f));
        var door = P2StoreBuilder.DoorCenter;
        Cube("Exit Box", root, new Vector3(door.x, 2.62f, StorefrontZ + 0.08f), new Vector3(0.46f, 0.18f, 0.05f), Plain("Sign_Backing", new Color(0.92f, 0.92f, 0.9f)));
        Surface("Exit Sign", root, new Vector3(door.x, 2.62f, StorefrontZ + 0.107f), new Vector2(0.42f, 0.158f), SeenLookingBack, exit);

        var dome = Plain("Cctv", new Color(0.09f, 0.09f, 0.1f));
        foreach (var at in new[] { new Vector3(-4.4f, 2.95f, -0.9f), new Vector3(-1.2f, 2.95f, 2.0f), new Vector3(2.6f, 2.95f, 2.4f), new Vector3(0.5f, 2.95f, -0.8f) })
            Primitive(PrimitiveType.Sphere, "CCTV", root, at, new Vector3(0.16f, 0.1f, 0.16f), dome);

        var red = Plain("Extinguisher", new Color(0.8f, 0.1f, 0.08f));
        var black = Plain("Cctv", new Color(0.09f, 0.09f, 0.1f));
        var foot = new Vector3(InnerRight - 0.12f, 0f, -1.15f);
        Primitive(PrimitiveType.Cylinder, "Extinguisher", root, foot + Vector3.up * 0.25f, new Vector3(0.14f, 0.25f, 0.14f), red);
        Primitive(PrimitiveType.Cylinder, "Extinguisher Top", root, foot + Vector3.up * 0.54f, new Vector3(0.05f, 0.04f, 0.05f), black);
    }

    // ---------- The fixtures' printed faces (colour only, D9) ----------

    /// <summary>
    /// Delivery crates on the decks of the chilled cases, no food shown: a row on every bottom deck and on a few of the
    /// decks above, as when a delivery waits to be put out. The other decks stay bare and lit.
    /// </summary>
    static void ChilledTrays(Transform root)
    {
        var parent = new GameObject("Chilled Trays").transform;
        parent.SetParent(root, false);
        var front = Printed("tray_crate");
        var body = Plain("Tray_Body", new Color(0.275f, 0.361f, 0.463f));
        var pick = new System.Random(7);
        float moduleWidth = (P3StoreDressing.ChilledNear - P3StoreDressing.ChilledFar) / P3StoreDressing.ChilledModules;
        var decks = P3StoreDressing.ChilledDecks;
        for (int module = 0; module < P3StoreDressing.ChilledModules; module++)
        {
            float xc = P3StoreDressing.ChilledFar + (module + 0.5f) * moduleWidth;
            for (int deck = 0; deck < decks.Length; deck++)
            {
                if (deck > 0 && pick.NextDouble() > 0.3) continue;
                var (y, reach) = decks[deck];
                // The trays stand level, set back from the deck's lower front edge.
                float height = deck == 0 ? 0.2f : 0.15f, trayDepth = reach - 0.1f, zCenter = InnerFront - 0.09f - trayDepth * 0.5f;
                var center = new Vector3(xc, y + 0.02f + height * 0.5f, zCenter);
                Cube("Tray", parent, center, new Vector3(moduleWidth - 0.1f, height, trayDepth), body);
                Surface("Tray Face", parent, center + new Vector3(0f, 0f, -trayDepth * 0.5f - 0.002f), new Vector2(moduleWidth - 0.1f, height), SeenLookingFront, front);
            }
        }
    }

    static void TobaccoFaces(Transform root)
    {
        float bayLeft = P3StoreDressing.TobaccoBayLeft, bayWidth = P3StoreDressing.TobaccoBayWidth;
        float z = P3StoreDressing.TobaccoFront;
        float rows = P3StoreDressing.TobaccoRows * P3StoreDressing.TobaccoRowHeight;
        for (int bay = 0; bay < P3StoreDressing.TobaccoBays; bay++)
            Surface("Tobacco Grid", root, new Vector3(bayLeft + (bay + 0.5f) * bayWidth, P3StoreDressing.TobaccoBottom + rows * 0.5f, z - 0.13f), new Vector2(bayWidth - 0.04f, rows), SeenLookingBack, Printed("tobacco_grid_" + (2 - bay), 0.3f, new Color(0.4f, 0.4f, 0.4f)));
        float headerCenter = bayLeft + P3StoreDressing.TobaccoBays * bayWidth * 0.5f;
        Surface("Tobacco Header Face", root, new Vector3(headerCenter, P3StoreDressing.TobaccoHeaderY, z + 0.062f), new Vector2(P3StoreDressing.TobaccoBays * bayWidth - 0.04f, P3StoreDressing.TobaccoHeaderHeight), SeenLookingBack, Printed("header_tobacco", 0.4f, new Color(1.1f, 1.1f, 1.1f)));
        Surface("Tobacco Lightbox Face", root, new Vector3((P3StoreDressing.TobaccoLeft + bayLeft) * 0.5f, 1.72f, z + 0.062f), new Vector2(bayLeft - P3StoreDressing.TobaccoLeft - 0.03f, 2.44f), SeenLookingBack, Printed("lightbox_tobacco", 0.4f, new Color(1.05f, 1.05f, 1.05f)));
    }

    /// <summary>Cartons of stock lying in the freezer, under the glass.</summary>
    static void FreezerStock(Transform root)
    {
        var parent = new GameObject("Freezer Stock").transform;
        parent.SetParent(root, false);
        float xc = (P3StoreDressing.FreezerX0 + P3StoreDressing.FreezerX1) * 0.5f;
        for (int i = 0; i < 6; i++)
        {
            float z = P3StoreDressing.FreezerZ0 + 0.14f + i * 0.225f;
            Cube("Freezer Carton", parent, new Vector3(xc + (i % 2 == 0 ? -0.1f : 0.1f), P3StoreDressing.FreezerHeight - 0.04f, z), new Vector3(0.3f, 0.06f, 0.2f), cardboard);
        }
    }

    /// <summary>What sits on the counter's far extension: a coin tray, a calculator, a basket of cartons, and bags on hooks.</summary>
    static void CounterExtensionThings(Transform root)
    {
        var parent = new GameObject("Counter Extension Things").transform;
        parent.SetParent(root, false);
        const float top = 0.9f;
        var dark = Plain("Device_Dark", new Color(0.07f, 0.075f, 0.09f));
        var steelish = Plain("Device_Steel", new Color(0.68f, 0.7f, 0.73f));
        var white = Plain("Bag_White", new Color(0.97f, 0.97f, 0.96f));

        // The calculator and the coin tray by the customer's edge.
        var calculator = new Vector3(1.6f, top + 0.012f, 0.74f);
        Cube("Calculator", parent, calculator, new Vector3(0.11f, 0.024f, 0.16f), dark);
        Surface("Calculator Face", parent, calculator + Vector3.up * 0.0125f, new Vector2(0.1f, 0.15f), Quaternion.LookRotation(Vector3.down, Vector3.forward), Printed("device_calculator"));

        var coinTray = new Vector3(1.48f, top + 0.018f, 0.5f);
        Cube("Coin Tray", parent, coinTray, new Vector3(0.24f, 0.036f, 0.14f), steelish);
        var coin = Plain("Coin_Stack", new Color(0.78f, 0.66f, 0.32f));
        for (int i = 0; i < 4; i++)
            Primitive(PrimitiveType.Cylinder, "Coin Stack", parent, coinTray + new Vector3(-0.09f + i * 0.06f, 0.03f, 0f), new Vector3(0.04f, 0.02f + i * 0.004f, 0.04f), coin);

        // A wire basket of small cartons, and a mug of pens at the end.
        var basket = new Vector3(1.86f, top + 0.05f, 0.6f);
        Cube("Counter Basket", parent, basket, new Vector3(0.22f, 0.1f, 0.3f), steelish);
        foreach (float z in new[] { -0.08f, 0.08f })
            Cube("Basket Carton", parent, basket + new Vector3(0f, 0.04f, z), new Vector3(0.16f, 0.08f, 0.12f), cardboard);
        Primitive(PrimitiveType.Cylinder, "Pen Mug", parent, new Vector3(1.14f, top + 0.06f, 0.48f), new Vector3(0.08f, 0.06f, 0.08f), dark);

        // Bags on hooks on the customers' face, under the top.
        const float rail = 0.76f, face = 0.962f;
        Cube("Bag Rail", parent, new Vector3(1.5f, rail, face + 0.02f), new Vector3(0.76f, 0.02f, 0.02f), steelish);
        foreach (float x in new[] { 1.28f, 1.5f, 1.74f })
        {
            Cube("Bag Hook", parent, new Vector3(x, rail - 0.03f, face + 0.03f), new Vector3(0.012f, 0.06f, 0.012f), steelish);
            Cube("Hanging Bag", parent, new Vector3(x, rail - 0.25f, face + 0.05f), new Vector3(0.2f, 0.36f, 0.05f), white);
            Surface("Bag Print", parent, new Vector3(x, rail - 0.25f, face + 0.077f), new Vector2(0.15f, 0.15f), SeenLookingBack, Printed("sign_bag_logo"));
        }
    }

    // ---------- Pieces ----------

    /// <summary>A flat printed face, readable when looking along <paramref name="seenLooking"/>'s forward.</summary>
    static GameObject Surface(string name, Transform parent, Vector3 center, Vector2 size, Quaternion seenLooking, Material material)
    {
        return Primitive(PrimitiveType.Quad, name, parent, center, new Vector3(size.x, size.y, 1f), material, seenLooking);
    }

    static GameObject Cube(string name, Transform parent, Vector3 center, Vector3 size, Material material) =>
        Primitive(PrimitiveType.Cube, name, parent, center, size, material);

    static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 center, Vector3 scale, Material material, Quaternion? rotation = null)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rotation ?? Quaternion.identity);
        go.transform.localScale = scale;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return go;
    }

    static float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

    static Bounds BoundsOf(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        return bounds;
    }

    // ---------- Materials and imports ----------

    static Material Printed(string texture, float smoothness = 0.3f, Color? glow = null)
    {
        if (Made.TryGetValue(texture, out var made)) return made;
        var material = Asset("Sign_" + texture);
        var map = Load<Texture2D>($"{Folder}/{texture}.png");
        material.SetTexture("_BaseMap", map);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", 0f);
        if (glow.HasValue)
        {
            material.EnableKeyword("_EMISSION");
            material.SetTexture("_EmissionMap", map);
            material.SetColor("_EmissionColor", glow.Value);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(material);
        Made[texture] = material;
        return material;
    }

    static Material Plain(string name, Color color)
    {
        if (Made.TryGetValue(name, out var made)) return made;
        var material = Asset(name);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", 0.3f);
        material.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(material);
        Made[name] = material;
        return material;
    }

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

    static T Load<T>(string path) where T : UnityEngine.Object =>
        AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception($"Missing {typeof(T).Name}: {path}");

    /// <summary>Print has to stay sharp at a slant, so these keep more resolution than the 1024 of the other store textures.</summary>
    static void ConfigureImports()
    {
        AssetDatabase.Refresh();
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
            int size = path.EndsWith("floor_terrazzo.png") ? 4096 : path.Contains("/rail_") ? 2048 : 1024;
            int aniso = path.EndsWith("floor_terrazzo.png") ? 16 : 8;
            var wrap = path.EndsWith("ceiling_tile.png") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            if (importer.maxTextureSize == size && importer.anisoLevel == aniso && importer.wrapMode == wrap && importer.sRGBTexture && importer.npotScale == TextureImporterNPOTScale.None) continue;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = size;
            importer.anisoLevel = aniso;
            importer.wrapMode = wrap;
            importer.SaveAndReimport();
        }
    }
}
