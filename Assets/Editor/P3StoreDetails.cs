using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// What makes the room read as a working store rather than a showroom (2026-11-09): a warmer light, the floor with its
/// pools of light and contact shadows painted in, price tags, posters, hanging signs, misted fridge doors and closed cartons
/// waiting on the bare shelves. The textures come from scripts/windows/make_store_signage.py.
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
    const string P3Materials = "Assets/Materials/P3";

    const float InnerLeft = -4.44f, InnerRight = 4.44f, InnerBack = -2.44f;
    const float ShelfFront = 2.95f, ShelfBack = 3.45f, SpineZ = 3.2f;
    const float ShelfA = -2.2f, ShelfB = 2.2f;
    const float RailLength = 1.46f, RailHeight = 0.042f;
    static readonly float[] GondolaBoards = { 0.1f, 0.55f, 1.0f, 1.4f };
    // Shelf C (P3StoreDressing.ShelfC).
    const float ShelfCFace = 3.99f, ShelfCNear = 2.6f, ShelfCFar = 4.42f;
    static readonly float[] ShelfCBoards = { 0.1f, 0.5f, 0.9f, 1.3f };

    static readonly Quaternion SeenLookingFront = Quaternion.identity;
    static readonly Quaternion SeenLookingBack = Quaternion.Euler(0f, 180f, 0f);
    static readonly Quaternion SeenLookingRight = Quaternion.Euler(0f, 90f, 0f);
    static readonly Quaternion SeenLookingLeft = Quaternion.Euler(0f, -90f, 0f);

    static readonly Dictionary<string, Material> Made = new Dictionary<string, Material>();
    static System.Random random;
    static Material cardboard;
    static Material[] cartonLabels;

    public static void Apply(Transform environment)
    {
        Directory.CreateDirectory(MaterialFolder);
        ConfigureImports();
        Made.Clear();
        random = new System.Random(20261109);
        cardboard = Load<Material>($"{P2Materials}/P2_Cardboard.mat");
        cartonLabels = new[] { Printed("carton_snack"), Printed("carton_noodle"), Printed("carton_daily"), Printed("carton_ready") };

        var root = new GameObject("Store Details").transform;
        root.SetParent(environment, false);
        Lighting();
        FloorAndWalls(environment, root);
        GondolaBacks(root);
        PriceRails(root);
        Cartons(root);
        EndCaps(root);
        Posters(root);
        HangingSigns(root);
        FridgeDoors(environment, root);
        Entrance(root);
        SmallThings(root);

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
        Debug.Log($"[P3] Store details: {root.GetComponentsInChildren<Renderer>(true).Length} renderers, {Made.Count} materials");
    }

    // ---------- Light ----------

    /// <summary>Warmer and less flat. The splats are unlit, so this moves the room around them, a little, and not them.</summary>
    static void Lighting()
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.97f, 0.94f, 0.88f);
        RenderSettings.ambientEquatorColor = new Color(0.88f, 0.85f, 0.8f);
        RenderSettings.ambientGroundColor = new Color(0.58f, 0.54f, 0.48f);

        var sun = (GameObject.Find("Directional Light") ?? throw new Exception("Directional Light missing.")).GetComponent<Light>();
        sun.color = new Color(1f, 0.96f, 0.9f);
        sun.intensity = 0.45f;

        var led = Load<Material>($"{P3Materials}/P3_LedPanel.mat");
        led.SetColor("_BaseColor", new Color(1f, 0.95f, 0.85f));
        led.SetColor("_EmissionColor", new Color(1f, 0.95f, 0.86f) * 2.4f);
        EditorUtility.SetDirty(led);
        var ceiling = Load<Material>($"{P3Materials}/P3_Ceiling.mat");
        ceiling.SetColor("_BaseColor", new Color(0.96f, 0.95f, 0.92f));
        ceiling.SetColor("_EmissionColor", new Color(0.46f, 0.445f, 0.41f));
        EditorUtility.SetDirty(ceiling);
    }

    static void FloorAndWalls(Transform environment, Transform root)
    {
        // One picture of the whole floor, so the light can differ from place to place. The tiled floor box stays for its collider-free shape only.
        foreach (var t in environment.GetComponentsInChildren<Transform>(true))
            if (t.name == "Floor Visual") t.GetComponent<Renderer>().enabled = false;
        var floor = Printed("floor_baked", 0.55f);
        Surface("Floor Painted", root, new Vector3(0f, 0.002f, 1.5f), new Vector2(9f, 8f), Quaternion.Euler(90f, 0f, 0f), floor);

        var wall = Load<Material>($"{P2Materials}/P2_Wall.mat");
        wall.SetTexture("_BaseMap", Load<Texture2D>($"{Folder}/wall_shade.png"));
        wall.SetTextureScale("_BaseMap", new Vector2(6f, 1f));
        wall.SetColor("_BaseColor", new Color(0.98f, 0.975f, 0.96f));
        EditorUtility.SetDirty(wall);
    }

    // ---------- Gondolas and shelf C ----------

    /// <summary>A colored back behind each side of the gondola: the white boards stop reading as one empty white box.</summary>
    static void GondolaBacks(Transform root)
    {
        var a = Plain("Back_Cream", new Color(0.93f, 0.8f, 0.55f));
        var b = Plain("Back_Sky", new Color(0.62f, 0.76f, 0.88f));
        foreach (var (x, material) in new[] { (ShelfA, a), (ShelfB, b) })
        {
            Surface("Gondola Back Front", root, new Vector3(x, 0.95f, SpineZ - 0.016f), new Vector2(RailLength, 1.7f), SeenLookingFront, material);
            Surface("Gondola Back Rear", root, new Vector3(x, 0.95f, SpineZ + 0.016f), new Vector2(RailLength, 1.7f), SeenLookingBack, material);
        }
    }

    static void PriceRails(Transform root)
    {
        float front = ShelfFront - 0.0095f, back = ShelfBack + 0.0095f;
        var frontA = new[] { "rail_lower_snack", "rail_lower_noodle", "rail_a_1000", "rail_a_1400" };
        var frontB = new[] { "rail_lower_noodle", "rail_lower_snack", "rail_b_1000", "rail_b_1400" };
        for (int i = 0; i < GondolaBoards.Length; i++)
        {
            float y = GondolaBoards[i] - 0.021f;
            Surface("Price Tags", root, new Vector3(ShelfA, y, front), new Vector2(RailLength, RailHeight), SeenLookingFront, Printed(frontA[i]));
            Surface("Price Tags", root, new Vector3(ShelfB, y, front), new Vector2(RailLength, RailHeight), SeenLookingFront, Printed(frontB[i]));
            Surface("Price Tags", root, new Vector3(ShelfA, y, back), new Vector2(RailLength, RailHeight), SeenLookingBack, Printed("rail_back_a"));
            Surface("Price Tags", root, new Vector3(ShelfB, y, back), new Vector2(RailLength, RailHeight), SeenLookingBack, Printed("rail_back_b"));
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
                RowAlongX(parent, x - inset, x + inset, board, front + depth * 0.5f, depth, 0.38f, -1f);
                RowAlongX(parent, x - inset, x + inset, board, back - depth * 0.5f, depth, 0.38f, 1f);
            }
        foreach (var x in new[] { ShelfA, ShelfB })
            RowAlongX(parent, x - inset, x + inset, 1.0f, back - depth * 0.5f, depth, 0.34f, 1f);
        RowAlongX(parent, ShelfA - inset, ShelfA + 0.0f, 1.4f, front + depth * 0.5f, depth, 0.34f, -1f);
        RowAlongX(parent, ShelfB - inset, ShelfB + inset, 1.4f, front + depth * 0.5f, depth, 0.34f, -1f);
        RowAlongX(parent, ShelfB - inset, ShelfB - inset + 0.34f, 1.0f, front + depth * 0.5f, depth, 0.34f, -1f);

        foreach (var board in ShelfCBoards)
        {
            float z = ShelfCNear + 0.04f;
            while (z < ShelfCFar - 0.32f)
            {
                float width = Range(0.28f, 0.36f), height = Range(0.24f, 0.34f);
                var center = new Vector3(ShelfCFace + 0.22f, board + height * 0.5f, z + width * 0.5f);
                Carton(parent, center, new Vector3(0.38f, height, width), new Vector3(-1f, 0f, 0f));
                z += width + Range(0.02f, 0.06f);
            }
        }
    }

    static void RowAlongX(Transform parent, float from, float to, float board, float z, float depth, float maxHeight, float faceZ)
    {
        float x = from;
        while (true)
        {
            float width = Range(0.26f, 0.34f), height = Range(0.22f, maxHeight);
            if (x + width > to) break;
            Carton(parent, new Vector3(x + width * 0.5f, board + height * 0.5f, z), new Vector3(width, height, depth), new Vector3(0f, 0f, faceZ));
            x += width + Range(0.015f, 0.05f);
        }
    }

    static void Carton(Transform parent, Vector3 center, Vector3 size, Vector3 facing)
    {
        var box = Cube("Carton", parent, center, size, cardboard);
        float half = Mathf.Abs(Vector3.Dot(size, facing)) * 0.5f;
        var labelAt = center + facing * (half + 0.002f) + Vector3.up * (size.y * 0.08f);
        // Seen looking against the way the face points.
        Surface("Carton Label", box.transform.parent, labelAt, new Vector2(0.16f, 0.08f), Quaternion.LookRotation(-facing), cartonLabels[random.Next(cartonLabels.Length)]);
    }

    static void EndCaps(Transform root)
    {
        const float outerY = 1.33f, innerY = 1f, width = 0.46f, height = 0.92f;
        Surface("End Cap", root, new Vector3(ShelfA - 0.753f, outerY, SpineZ), new Vector2(width, height), SeenLookingRight, Printed("endcap_noodle"));
        Surface("End Cap", root, new Vector3(ShelfA + 0.753f, innerY, SpineZ), new Vector2(width, height), SeenLookingLeft, Printed("endcap_snack"));
        Surface("End Cap", root, new Vector3(ShelfB - 0.753f, innerY, SpineZ), new Vector2(width, height), SeenLookingRight, Printed("endcap_ready"));
        Surface("End Cap", root, new Vector3(ShelfB + 0.753f, outerY, SpineZ), new Vector2(width, height), SeenLookingLeft, Printed("endcap_daily"));
    }

    // ---------- Walls, ceiling, doors ----------

    static void Posters(Transform root)
    {
        var a2 = new Vector2(0.56f, 0.79f);
        var a3 = new Vector2(0.4f, 0.565f);
        Surface("Poster", root, new Vector3(InnerRight - 0.003f, 1.6f, 0.45f), a2, SeenLookingRight, Printed("poster_1plus1"));
        Surface("Poster", root, new Vector3(InnerRight - 0.003f, 1.6f, -0.3f), a2, SeenLookingRight, Printed("poster_2plus1"));
        Surface("Poster", root, new Vector3(InnerRight - 0.003f, 1.6f, -1.05f), a3, SeenLookingRight, Printed("poster_expiry"));
        Surface("Poster", root, new Vector3(1.5f, 1.6f, InnerBack + 0.003f), a2, SeenLookingBack, Printed("poster_recruit"));
        Surface("Poster", root, new Vector3(2.2f, 1.6f, InnerBack + 0.003f), a3, SeenLookingBack, Printed("poster_service"));
        Surface("Poster", root, new Vector3(-2.95f, 1.5f, InnerBack + 0.003f), a2, SeenLookingBack, Printed("poster_nineteen"));
        Surface("Poster", root, new Vector3(InnerLeft + 0.003f, 1.55f, 4.05f), a2, SeenLookingLeft, Printed("poster_2plus1"));
        Surface("Poster", root, new Vector3(InnerLeft + 0.003f, 1.55f, 0.72f), a3, SeenLookingLeft, Printed("poster_service"));
        // On the counter's customer side, either side of the store name.
        const float counterFace = 0.9635f;
        Surface("Counter Notice", root, new Vector3(-0.8f, 0.47f, counterFace), new Vector2(0.26f, 0.368f), SeenLookingBack, Printed("poster_nineteen"));
        Surface("Counter Notice", root, new Vector3(0.8f, 0.47f, counterFace), new Vector2(0.26f, 0.368f), SeenLookingBack, Printed("poster_expiry"));
    }

    static void HangingSigns(Transform root)
    {
        // Across the view from the door, and across the view from the counter. The ones facing the counter hang well to the
        // side of the eye line from the door, where seen edge-on they would cut the sign behind them in two.
        Hang(root, new Vector3(-1.8f, 2.42f, 2.75f), SeenLookingRight, "hang_event");
        Hang(root, new Vector3(-0.2f, 2.42f, 4.1f), SeenLookingRight, "hang_drinks");
        Hang(root, new Vector3(-3.2f, 2.42f, 2.75f), SeenLookingFront, "hang_new");
        Hang(root, new Vector3(3.1f, 2.42f, 2.75f), SeenLookingFront, "hang_two");
    }

    static void Hang(Transform root, Vector3 center, Quaternion seenLooking, string texture)
    {
        var size = new Vector2(1f, 0.5f);
        var backing = Plain("Sign_Backing", new Color(0.92f, 0.92f, 0.9f));
        var axis = seenLooking * Vector3.forward;
        var along = seenLooking * Vector3.right;
        var board = Cube("Hanging Board", root, center, new Vector3(1f, 0.5f, 1f), backing);
        board.transform.rotation = seenLooking;
        board.transform.localScale = new Vector3(size.x + 0.02f, size.y + 0.02f, 0.012f);
        Surface("Hanging Face", root, center - axis * 0.0075f, size, seenLooking, Printed(texture));
        Surface("Hanging Face", root, center + axis * 0.0075f, size, seenLooking * Quaternion.Euler(0f, 180f, 0f), Printed(texture));
        float top = center.y + size.y * 0.5f, ceiling = 3f;
        foreach (float side in new[] { -0.42f, 0.42f })
            Cube("Hanging Wire", root, center + along * side + Vector3.up * (top - center.y + (ceiling - top) * 0.5f), new Vector3(0.006f, ceiling - top, 0.006f), Plain("Wire", new Color(0.3f, 0.3f, 0.32f)));
    }

    /// <summary>Misted film on the fridge doors: the fridges stay empty until there is a Gaussian budget for them.</summary>
    static void FridgeDoors(Transform environment, Transform root)
    {
        var films = new[] { "fridge_soda", "fridge_juice", "fridge_coffee", "fridge_milk", "fridge_water" };
        for (int i = 1; i <= 10; i++)
        {
            Transform fridge = null;
            foreach (var t in environment.GetComponentsInChildren<Transform>(true))
                if (t.name == $"Drinks Fridge {i}") fridge = t;
            if (fridge == null) throw new Exception($"Drinks Fridge {i} missing.");
            var bounds = BoundsOf(fridge.gameObject);
            var center = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.5f + FilmLift, bounds.min.z - 0.003f);
            Surface("Fridge Film", root, center, new Vector2(bounds.size.x * FilmWidth, bounds.size.y * FilmHeight), SeenLookingFront, Printed(films[(i - 1) % films.Length]));
        }
    }

    const float FilmWidth = 0.84f, FilmHeight = 0.74f, FilmLift = 0.05f;

    static void Entrance(Transform root)
    {
        Surface("Entrance Mat", root, new Vector3(-3.85f, 0.004f, 2.4f), new Vector2(2f, 1f), Quaternion.LookRotation(Vector3.down, Vector3.right), Printed("floor_mat"));
        foreach (var spot in new[] { new Vector3(0.85f, 0.004f, 1.5f), new Vector3(1.7f, 0.004f, 1.65f) })
            Surface("Queue Sticker", root, spot, new Vector2(0.5f, 0.5f), Quaternion.LookRotation(Vector3.down, Vector3.back), Printed("floor_queue"));

        // A stack of red baskets inside the door, the same red as the ones customers carry.
        var red = Plain("Basket_Red", new Color(0.78f, 0.12f, 0.1f));
        var dark = Plain("Basket_Inside", new Color(0.34f, 0.05f, 0.04f));
        var at = new Vector3(-3.95f, 0f, 0.92f);
        Cube("Basket Stack", root, at + Vector3.up * 0.17f, new Vector3(0.38f, 0.34f, 0.28f), red);
        for (int i = 0; i < 6; i++)
            Cube("Basket Rim", root, at + Vector3.up * (0.09f + i * 0.055f), new Vector3(0.42f, 0.014f, 0.32f), red);
        Cube("Basket Top", root, at + Vector3.up * 0.345f, new Vector3(0.36f, 0.008f, 0.26f), dark);
    }

    static void SmallThings(Transform root)
    {
        Surface("Clock", root, new Vector3(1.75f, 2.42f, InnerBack + 0.003f), new Vector2(0.36f, 0.36f), SeenLookingBack, Printed("clock_face"));

        var exit = Printed("exit_sign", 0.3f, new Color(0.45f, 1.5f, 0.75f));
        Cube("Exit Box", root, new Vector3(3.5f, 2.38f, InnerBack + 0.03f), new Vector3(0.46f, 0.18f, 0.05f), Plain("Sign_Backing", new Color(0.92f, 0.92f, 0.9f)));
        Surface("Exit Sign", root, new Vector3(3.5f, 2.38f, InnerBack + 0.0565f), new Vector2(0.42f, 0.158f), SeenLookingBack, exit);

        var dome = Plain("Cctv", new Color(0.09f, 0.09f, 0.1f));
        foreach (var at in new[] { new Vector3(-3.7f, 2.95f, 0.9f), new Vector3(3.7f, 2.95f, 4.2f), new Vector3(0.9f, 2.95f, -1.6f) })
            Primitive(PrimitiveType.Sphere, "CCTV", root, at, new Vector3(0.16f, 0.1f, 0.16f), dome);

        var red = Plain("Extinguisher", new Color(0.8f, 0.1f, 0.08f));
        var black = Plain("Cctv", new Color(0.09f, 0.09f, 0.1f));
        var foot = new Vector3(InnerLeft + 0.1f, 0f, 0.9f);
        Primitive(PrimitiveType.Cylinder, "Extinguisher", root, foot + Vector3.up * 0.25f, new Vector3(0.14f, 0.25f, 0.14f), red);
        Primitive(PrimitiveType.Cylinder, "Extinguisher Top", root, foot + Vector3.up * 0.54f, new Vector3(0.05f, 0.04f, 0.05f), black);
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
            int size = path.EndsWith("floor_baked.png") ? 4096 : path.Contains("/rail_") ? 2048 : 1024;
            int aniso = path.EndsWith("floor_baked.png") ? 16 : 8;
            var wrap = path.EndsWith("wall_shade.png") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
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
