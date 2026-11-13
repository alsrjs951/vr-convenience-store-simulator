using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The player's hands (2026-11-11), cut out of a Rocketbox avatar that is already in the project (MIT, see
/// Assets/ThirdParty/Rocketbox/README.md), so nothing new is downloaded and the hands look like the customers'.
/// Makes one prefab per hand: the skin from the wrist down with its finger joints, a short piece of forearm, and the cuff
/// of the clerk's uniform over where the forearm was cut. Idempotent; P2StoreBuilder.Build runs it and puts the hands on
/// the rig's controllers with <see cref="Attach"/>.
/// </summary>
public static class PlayerHandSetup
{
    public const string PrefabFolder = "Assets/Prefabs/Hands";
    const string MeshFolder = "Assets/Art/Generated/Hands";
    const string MaterialFolder = "Assets/Materials/Hands";
    // The women's body texture gives a hand about twice the texels the men's does (166 x 451 against 216 x 152 of 1024).
    const string AvatarName = "Female_Adult_01", TexturePrefix = "f001";
    // Rocketbox women's hands are about 17 cm from wrist to fingertip; this brings them to a middling adult hand.
    const float HandScale = 1.06f;
    // How much forearm is kept behind the wrist joint. All of it is inside the cuff.
    const float ForearmKeep = 0.11f;
    // The cuff ends this far behind the wrist joint, has this much room around the arm, and a hem this long and thick.
    const float CuffStart = 0.022f, CuffRoom = 0.005f, CuffHem = 0.014f, CuffHemThick = 0.0018f;
    const int CuffSides = 28;
    // The manager's apron is this navy (P2StoreBuilder), so the clerk's sleeve is too.
    static readonly Color UniformNavy = new Color(0.03f, 0.16f, 0.36f);

    // ---------- Where a hand sits on its controller ----------

    // The rig's controller transform is the pointing pose: its origin is where the poke point and the ray start, and the
    // ray goes along +z. The pointing index finger ends there and points that way, so what the finger touches is what
    // the game touches. Not seen in a headset yet: how well this lies over the real hand is for the Quest 2 to say.
    static readonly Vector3 IndexTipAt = new Vector3(0f, 0f, 0.0075f);
    // The back of the hand faces away from the other hand, turned this much toward the ceiling, as hands hold controllers.
    const float BackOfHandUp = 25f;

    static string PrefabPath(bool left) => $"{PrefabFolder}/PlayerHand_{(left ? "Left" : "Right")}.prefab";

    [MenuItem("Tools/Hands/Build Player Hands")]
    public static void SetupMenu() => Setup();

    /// <returns>The left and the right hand prefab.</returns>
    public static PlayerHand[] Setup()
    {
        Directory.CreateDirectory(PrefabFolder);
        Directory.CreateDirectory(MeshFolder);
        Directory.CreateDirectory(MaterialFolder);
        AssetDatabase.Refresh();

        var textures = $"{P2CustomerSetup.Root}/Avatars/{AvatarName}/Textures/{TexturePrefix}";
        var skin = Lit($"{MaterialFolder}/PlayerHand_Skin.mat", Color.white, 0.32f,
            Load<Texture2D>($"{textures}_body_color.png"), Load<Texture2D>($"{textures}_body_normal.png"));
        var cuff = Lit($"{MaterialFolder}/PlayerHand_Cuff.mat", UniformNavy, 0.12f, null, null);
        var model = Load<GameObject>($"{P2CustomerSetup.Root}/Avatars/{AvatarName}/{AvatarName}.fbx");

        var hands = new[] { Build(model, true, skin, cuff), Build(model, false, skin, cuff) };
        AssetDatabase.SaveAssets();
        Debug.Log($"[Hands] built from {AvatarName}: " + string.Join("; ", hands.Select(h =>
            $"{h.name} wrist to middle fingertip {h.transform.InverseTransformPoint(h.Tips[PlayerHand.Middle].position).magnitude * 100f:0.0} cm, pointing tip {h.PointTip.ToString("F3")}")));
        return hands;
    }

    /// <summary>Puts a hand on a controller of the rig and gives it that controller's buttons.</summary>
    public static PlayerHand Attach(Transform controller, PlayerHand prefab)
    {
        var hand = (PlayerHand)PrefabUtility.InstantiatePrefab(prefab, controller);
        // The pointing finger along the ray, the back of the hand outward and a little up.
        float side = prefab.Left ? -1f : 1f;
        var outward = Quaternion.AngleAxis(side * BackOfHandUp, Vector3.forward) * new Vector3(side, 0f, 0f);
        var rotation = Quaternion.LookRotation(Vector3.forward, outward) * Quaternion.Inverse(Quaternion.LookRotation(prefab.PointDirection, Vector3.up));
        hand.transform.localRotation = rotation;
        hand.transform.localPosition = IndexTipAt - rotation * (prefab.PointTip * prefab.transform.localScale.x);
        hand.Interactor = controller.GetComponentInChildren<NearFarInteractor>(true);
        PrefabUtility.RecordPrefabInstancePropertyModifications(hand);
        PrefabUtility.RecordPrefabInstancePropertyModifications(hand.transform);
        return hand;
    }

    // ---------- What things sound like in the hand (TouchFeel) ----------

    const string SoundFolder = "Assets/Audio/Generated";
    const string ManifestFolder = "Assets/Data/Provisional";
    const string StoreScene = "Assets/Scenes/StoreScene.unity";

    [Serializable] class PackageManifest { public string id, package; }

    /// <summary>A manifest's "package": "Carton", or a film bag when it says nothing.</summary>
    public static ProductPackage PackageOf(string package) =>
        string.IsNullOrEmpty(package) ? ProductPackage.Bag : (ProductPackage)Enum.Parse(typeof(ProductPackage), package, true);

    /// <summary>The takes made by scripts/windows/make_hand_sounds.py. P2StoreBuilder calls this with its other sounds.</summary>
    public static void AssignTouchSounds(GameAudio audio)
    {
        AudioClip[] Takes(string stem, int count)
        {
            var clips = new AudioClip[count];
            for (int i = 0; i < count; i++) clips[i] = Load<AudioClip>($"{SoundFolder}/{stem}_{i + 1}.wav");
            return clips;
        }
        audio.CrinkleClips = Takes("sfx_crinkle", 3);
        audio.RustleClips = Takes("sfx_rustle", 4);
        audio.CartonClips = Takes("sfx_carton", 2);
        audio.BagDownClips = Takes("sfx_bag_down", 2);
        EditorUtility.SetDirty(audio);
    }

    /// <summary>
    /// Puts the hands' sounds and the products' packages into the store as it is, without building it again (a build
    /// bakes the light anew, two minutes and a dozen megabytes of lightmaps that did not change). A build does the same
    /// through P2StoreBuilder.
    /// Unity.exe -batchmode -executeMethod PlayerHandSetup.InstallTouchFeelInStore -quit
    /// </summary>
    [MenuItem("Tools/Hands/Install Touch Feel In The Store Scene")]
    public static void InstallTouchFeelInStore()
    {
        AssetDatabase.Refresh();
        int cartons = 0, products = 0;
        foreach (var path in Directory.GetFiles(ManifestFolder, "dev_*.json"))
        {
            if (path.EndsWith(".box_fit.json")) continue;
            var manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(path));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Products/P_{manifest.id}.prefab");
            var product = prefab != null ? prefab.GetComponent<ProductPrefab>() : null;
            if (product == null || product.Data == null) throw new Exception("No product data for " + manifest.id);
            product.Data.Package = PackageOf(manifest.package);
            EditorUtility.SetDirty(product.Data);
            products++;
            if (product.Data.Package == ProductPackage.Carton) cartons++;
        }
        Setup();
        var scene = EditorSceneManager.OpenScene(StoreScene, OpenSceneMode.Single);
        var audio = UnityEngine.Object.FindFirstObjectByType<GameAudio>() ?? throw new Exception("No GameAudio in " + StoreScene);
        AssignTouchSounds(audio);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new Exception("Scene save failed.");
        AssetDatabase.SaveAssets();
        Debug.Log($"[Hands] touch feel installed: {products} products, {cartons} cartons; crinkle {audio.CrinkleClips.Length}, rustle {audio.RustleClips.Length}, carton {audio.CartonClips.Length}, bag down {audio.BagDownClips.Length} takes; hands in the scene with TouchFeel: {UnityEngine.Object.FindObjectsByType<TouchFeel>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length}");
    }

    /// <summary>The rig's controller models go and the hands come in their place.</summary>
    public static void Install(Transform rig)
    {
        foreach (bool left in new[] { true, false })
        {
            string name = left ? "Left" : "Right";
            var controller = rig.Find($"Camera Offset/{name} Controller") ?? throw new Exception($"{name} Controller missing.");
            var model = controller.Find($"{name} Controller Visual") ?? throw new Exception($"{name} Controller Visual missing.");
            model.gameObject.SetActive(false);
            PrefabUtility.RecordPrefabInstancePropertyModifications(model.gameObject);
            Attach(controller, Load<PlayerHand>(PrefabPath(left)));
        }
    }

    // ---------- Cutting the hand out ----------

    static PlayerHand Build(GameObject model, bool left, Material skin, Material cuffMaterial)
    {
        var instance = UnityEngine.Object.Instantiate(model);
        var root = new GameObject(left ? "Hand Left" : "Hand Right");
        try
        {
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            string side = left ? " L " : " R ";
            var all = instance.GetComponentsInChildren<Transform>(true);
            Transform Bone(string suffix) => all.FirstOrDefault(t => t.name.EndsWith(side + suffix)) ?? throw new Exception($"{model.name} has no bone '{side}{suffix}'.");
            var wrist = Bone("Hand");
            var forearm = Bone("Forearm");
            var chains = new Transform[PlayerHand.Fingers][];
            for (int f = 0; f < PlayerHand.Fingers; f++)
                chains[f] = new[] { Bone("Finger" + f), Bone("Finger" + f + "1"), Bone("Finger" + f + "2") };

            var skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(s => s.bones.Contains(wrist))
                          ?? throw new Exception($"{model.name}: no skin on the hand bone.");
            var source = skinned.sharedMesh;
            var baked = new Mesh();
            skinned.BakeMesh(baked, true);
            var meshToWorld = skinned.transform.localToWorldMatrix;

            // The hand's own frame: z from the wrist to the middle knuckle, y out of the back of the hand. Which way the
            // palm faces follows from which hand it is: thumb side x fingers is the palm of a left hand.
            var w = wrist.position;
            var fingers = (chains[PlayerHand.Middle][0].position - w).normalized;
            var thumbSide = (chains[PlayerHand.Index][0].position - chains[PlayerHand.Little][0].position).normalized;
            var palm = Vector3.Cross(thumbSide, fingers).normalized * (left ? 1f : -1f);
            var frame = Quaternion.LookRotation(fingers, -palm);
            var toHand = Matrix4x4.TRS(w, frame, Vector3.one).inverse;
            var forearmAxis = (w - forearm.position).normalized;

            // New skeleton: the palm, then three joints per finger, where the avatar's are.
            var palmBone = NewBone("Palm", root.transform, wrist, toHand, frame);
            var bones = new List<Transform> { palmBone };
            var newIndex = new Dictionary<Transform, int> { [wrist] = 0 };
            string[] names = { "Thumb", "Index", "Middle", "Ring", "Little" };
            var hand = root.AddComponent<PlayerHand>();
            hand.Left = left;
            root.AddComponent<TouchFeel>();
            for (int f = 0; f < PlayerHand.Fingers; f++)
            {
                var parent = palmBone;
                for (int j = 0; j < PlayerHand.JointsPerFinger; j++)
                {
                    var joint = NewBone($"{names[f]} {j + 1}", parent, chains[f][j], toHand, frame);
                    newIndex[chains[f][j]] = bones.Count;
                    bones.Add(joint);
                    hand.Joints[f * PlayerHand.JointsPerFinger + j] = joint;
                    parent = joint;
                }
            }

            // Skin: what hangs on this arm from ForearmKeep behind the wrist on.
            var sourceBones = skinned.bones;
            var armBones = new HashSet<int>(Enumerable.Range(0, sourceBones.Length).Where(i => sourceBones[i] != null && sourceBones[i].IsChildOf(forearm)));
            var weights = source.boneWeights;
            var positions = baked.vertices;
            var normals = baked.normals;
            var tangents = baked.tangents;
            var uvs = source.uv;
            var remap = new Dictionary<int, int>();
            var outPositions = new List<Vector3>();
            var outNormals = new List<Vector3>();
            var outTangents = new List<Vector4>();
            var outUvs = new List<Vector2>();
            var outWeights = new List<BoneWeight>();
            var dominant = new List<int>();
            var toHandRotation = Quaternion.Inverse(frame);
            // The avatar's forearm has few rings of corners, so the triangles behind the wrist reach well past ForearmKeep.
            // They are kept whole when any corner is within it, and the corners beyond are slid up the arm to the cut.
            var onArm = new bool[positions.Length];
            var behindWrist = new float[positions.Length];
            for (int v = 0; v < positions.Length; v++)
            {
                var bw = weights[v];
                float weight = (armBones.Contains(bw.boneIndex0) ? bw.weight0 : 0f) + (armBones.Contains(bw.boneIndex1) ? bw.weight1 : 0f)
                               + (armBones.Contains(bw.boneIndex2) ? bw.weight2 : 0f) + (armBones.Contains(bw.boneIndex3) ? bw.weight3 : 0f);
                onArm[v] = weight > 0.5f;
                behindWrist[v] = -Vector3.Dot(meshToWorld.MultiplyPoint3x4(positions[v]) - w, forearmAxis);
            }
            int Corner(int v)
            {
                if (remap.TryGetValue(v, out int known)) return known;
                var world = meshToWorld.MultiplyPoint3x4(positions[v]);
                if (behindWrist[v] > ForearmKeep) world += forearmAxis * (behindWrist[v] - ForearmKeep);
                remap[v] = outPositions.Count;
                outPositions.Add(toHand.MultiplyPoint3x4(world));
                outNormals.Add(toHandRotation * meshToWorld.MultiplyVector(normals[v]).normalized);
                var tangent = toHandRotation * meshToWorld.MultiplyVector(tangents[v]).normalized;
                outTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, tangents[v].w));
                outUvs.Add(uvs[v]);
                outWeights.Add(Reweigh(weights[v], sourceBones, newIndex, out int most));
                dominant.Add(most);
                return remap[v];
            }
            var triangles = new List<int>();
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var sourceTriangles = source.GetTriangles(s);
                for (int t = 0; t < sourceTriangles.Length; t += 3)
                {
                    int a = sourceTriangles[t], b = sourceTriangles[t + 1], c = sourceTriangles[t + 2];
                    if (!onArm[a] || !onArm[b] || !onArm[c]) continue;
                    if (behindWrist[a] > ForearmKeep && behindWrist[b] > ForearmKeep && behindWrist[c] > ForearmKeep) continue;
                    triangles.Add(Corner(a));
                    triangles.Add(Corner(b));
                    triangles.Add(Corner(c));
                }
            }
            if (triangles.Count < 600) throw new Exception($"{model.name}: only {triangles.Count / 3} triangles on the {(left ? "left" : "right")} hand.");

            var mesh = new Mesh { name = left ? "PlayerHand_Left" : "PlayerHand_Right" };
            mesh.SetVertices(outPositions);
            mesh.SetNormals(outNormals);
            mesh.SetTangents(outTangents);
            mesh.SetUVs(0, outUvs);
            mesh.boneWeights = outWeights.ToArray();
            mesh.SetTriangles(triangles, 0);
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray();
            mesh.RecalculateBounds();
            mesh = Save(mesh, $"{MeshFolder}/{mesh.name}.asset");

            var skinObject = new GameObject("Skin");
            skinObject.transform.SetParent(root.transform, false);
            var renderer = skinObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones.ToArray();
            renderer.rootBone = palmBone;
            renderer.sharedMaterial = skin;
            // A closing hand moves well outside the bounds of the flat one, right in front of the eyes.
            renderer.updateWhenOffscreen = true;
            renderer.skinnedMotionVectors = false;
            renderer.shadowCastingMode = ShadowCastingMode.On;

            // Fingertips: the farthest skin of the last joint along the finger.
            for (int f = 0; f < PlayerHand.Fingers; f++)
            {
                var last = hand.Joints[f * PlayerHand.JointsPerFinger + 2];
                var along = (last.position - hand.Joints[f * PlayerHand.JointsPerFinger + 1].position).normalized;
                int lastIndex = bones.IndexOf(last);
                float reach = 0.015f;
                for (int v = 0; v < outPositions.Count; v++)
                    if (dominant[v] == lastIndex) reach = Mathf.Max(reach, Vector3.Dot(outPositions[v] - last.position, along));
                var tip = new GameObject($"{names[f]} Tip").transform;
                tip.SetParent(last, false);
                tip.position = last.position + along * (reach - 0.003f);
                tip.rotation = Quaternion.identity;
                hand.Tips[f] = tip;
            }

            // Hinges. Fingers close toward the palm (-y here). The thumb's first joint turns about the hand's length and
            // brings the thumb round in front of the palm; its other two fold it toward the little finger, which by then
            // is toward the fingers it closes on.
            var thumbAcross = new Vector3(left ? -1f : 1f, 0f, 0f);
            for (int f = 0; f < PlayerHand.Fingers; f++)
            {
                var along = (hand.Tips[f].position - hand.Joints[f * PlayerHand.JointsPerFinger].position).normalized;
                for (int j = 0; j < PlayerHand.JointsPerFinger; j++)
                {
                    var toward = f != PlayerHand.Thumb || j == 0 ? Vector3.down : thumbAcross;
                    var hinge = f == PlayerHand.Thumb && j == 0 ? Vector3.forward : Vector3.Cross(along, toward).normalized;
                    if (Vector3.Dot(Quaternion.AngleAxis(10f, hinge) * along, toward) < Vector3.Dot(along, toward)) hinge = -hinge;
                    int i = f * PlayerHand.JointsPerFinger + j;
                    hand.CurlAxis[i] = hand.Joints[i].parent.InverseTransformDirection(hinge);
                    hand.Rest[i] = hand.Joints[i].localRotation;
                }
            }

            // The thumb's first joint also swings the thumb down, away from the palm, which is how it gets under the
            // edge of a held thing.
            var thumbAlong = (hand.Tips[PlayerHand.Thumb].position - hand.Joints[0].position).normalized;
            var thumbDown = Vector3.Cross(thumbAlong, Vector3.down).normalized;
            if (Vector3.Dot(Quaternion.AngleAxis(10f, thumbDown) * thumbAlong, Vector3.down) < Vector3.Dot(thumbAlong, Vector3.down)) thumbDown = -thumbDown;
            hand.ThumbDownAxis = hand.Joints[0].parent.InverseTransformDirection(thumbDown);

            // What the four fingers lie on when the hand holds something flat: the plane along them from the knuckles to
            // the tips, moved toward the palm side as far as their skin goes. And the web of the thumb, where the edge
            // of the held thing sits.
            var knuckles = Vector3.zero;
            var ends = Vector3.zero;
            for (int f = PlayerHand.Index; f < PlayerHand.Fingers; f++)
            {
                knuckles += hand.Joints[f * PlayerHand.JointsPerFinger].position * 0.25f;
                ends += hand.Tips[f].position * 0.25f;
            }
            var across = (hand.Joints[PlayerHand.Index * PlayerHand.JointsPerFinger].position - hand.Joints[PlayerHand.Little * PlayerHand.JointsPerFinger].position).normalized;
            var backOfHand = Vector3.Cross((ends - knuckles).normalized, across).normalized;
            if (backOfHand.y < 0f) backOfHand = -backOfHand;
            float lowest = 0f;
            int firstFingerBone = 1 + PlayerHand.Index * PlayerHand.JointsPerFinger;
            for (int v = 0; v < outPositions.Count; v++)
                if (dominant[v] >= firstFingerBone) lowest = Mathf.Min(lowest, Vector3.Dot(outPositions[v] - knuckles, backOfHand));
            hand.PadNormal = backOfHand;
            hand.PadForward = Vector3.ProjectOnPlane(ends - knuckles, backOfHand).normalized;
            hand.PadPoint = knuckles + backOfHand * lowest;
            hand.Web = Vector3.Lerp((hand.Joints[1].position + hand.Joints[PlayerHand.Index * PlayerHand.JointsPerFinger].position) * 0.5f, hand.Joints[0].position, 0.3f);

            // The cuff over the cut.
            var cuffObject = new GameObject("Cuff");
            cuffObject.transform.SetParent(root.transform, false);
            cuffObject.AddComponent<MeshFilter>().sharedMesh = Save(CuffMesh(outPositions, triangles, toHand.MultiplyVector(forearmAxis).normalized, left), $"{MeshFolder}/PlayerHand_Cuff_{(left ? "Left" : "Right")}.asset");
            var cuffRenderer = cuffObject.AddComponent<MeshRenderer>();
            cuffRenderer.sharedMaterial = cuffMaterial;
            cuffRenderer.shadowCastingMode = ShadowCastingMode.On;

            // Where the index finger ends and which way it goes when it points, for Attach.
            hand.PoseNow(1f, 0f, false);
            hand.PointTip = hand.Tips[PlayerHand.Index].position;
            hand.PointDirection = (hand.Tips[PlayerHand.Index].position - hand.Joints[PlayerHand.Index * PlayerHand.JointsPerFinger].position).normalized;
            hand.PoseNow(0f, 0f, false);

            root.transform.localScale = Vector3.one * HandScale;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(left));
            return prefab.GetComponent<PlayerHand>();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    static Transform NewBone(string name, Transform parent, Transform source, Matrix4x4 toHand, Quaternion frame)
    {
        var bone = new GameObject(name).transform;
        bone.SetParent(parent, false);
        bone.SetPositionAndRotation(toHand.MultiplyPoint3x4(source.position), Quaternion.Inverse(frame) * source.rotation);
        return bone;
    }

    /// <summary>The avatar's weights on the new skeleton. Whatever is not a finger joint goes with the palm.</summary>
    static BoneWeight Reweigh(BoneWeight from, Transform[] sourceBones, Dictionary<Transform, int> newIndex, out int most)
    {
        var sum = new Dictionary<int, float>();
        void Add(int bone, float weight)
        {
            if (weight <= 0f) return;
            int index = sourceBones[bone] != null && newIndex.TryGetValue(sourceBones[bone], out int found) ? found : 0;
            sum[index] = (sum.TryGetValue(index, out float had) ? had : 0f) + weight;
        }
        Add(from.boneIndex0, from.weight0);
        Add(from.boneIndex1, from.weight1);
        Add(from.boneIndex2, from.weight2);
        Add(from.boneIndex3, from.weight3);
        var ordered = sum.OrderByDescending(p => p.Value).Take(4).ToArray();
        float total = ordered.Sum(p => p.Value);
        var to = new BoneWeight();
        if (ordered.Length > 0) { to.boneIndex0 = ordered[0].Key; to.weight0 = ordered[0].Value / total; }
        if (ordered.Length > 1) { to.boneIndex1 = ordered[1].Key; to.weight1 = ordered[1].Value / total; }
        if (ordered.Length > 2) { to.boneIndex2 = ordered[2].Key; to.weight2 = ordered[2].Value / total; }
        if (ordered.Length > 3) { to.boneIndex3 = ordered[3].Key; to.weight3 = ordered[3].Value / total; }
        most = ordered.Length > 0 ? ordered[0].Key : 0;
        return to;
    }

    /// <summary>
    /// A sleeve end around the forearm. At each ring it is as far out as the arm's skin is on that side plus some room,
    /// so no skin comes through it; closed at both ends so neither the cut nor the inside of the arm shows; with a hem
    /// toward the hand.
    /// </summary>
    static Mesh CuffMesh(List<Vector3> skin, List<int> triangles, Vector3 axis, bool left)
    {
        var e1 = (Vector3.right - axis * Vector3.Dot(Vector3.right, axis)).normalized;
        var e2 = Vector3.Cross(axis, e1);
        float near = -CuffStart, far = -ForearmKeep - 0.004f, middle = (near - CuffHem + far) * 0.5f;
        var sections = new Dictionary<float, (Vector2 center, float[] radius)>();
        (Vector2 center, float[] radius) Section(float s)
        {
            if (sections.TryGetValue(s, out var known)) return known;
            // The arm's outline where a plane across it at s cuts the skin: one piece of line per triangle it goes through.
            float at = Mathf.Clamp(s, -ForearmKeep + 0.004f, -0.004f);
            var outline = new List<(Vector2 from, Vector2 to)>();
            var crossing = new List<Vector2>(2);
            for (int t = 0; t < triangles.Count; t += 3)
            {
                crossing.Clear();
                for (int edge = 0; edge < 3; edge++)
                {
                    var p = skin[triangles[t + edge]];
                    var q = skin[triangles[t + (edge + 1) % 3]];
                    float dp = Vector3.Dot(p, axis) - at, dq = Vector3.Dot(q, axis) - at;
                    if (dp * dq >= 0f) continue;
                    var hit = Vector3.Lerp(p, q, dp / (dp - dq));
                    crossing.Add(new Vector2(Vector3.Dot(hit, e1), Vector3.Dot(hit, e2)));
                }
                if (crossing.Count == 2) outline.Add((crossing[0], crossing[1]));
            }
            if (outline.Count < 6) throw new Exception($"No forearm under the cuff at {s:0.000} ({outline.Count} pieces of outline).");
            var min = outline.Aggregate(outline[0].from, (m, piece) => Vector2.Min(m, Vector2.Min(piece.from, piece.to)));
            var max = outline.Aggregate(outline[0].from, (m, piece) => Vector2.Max(m, Vector2.Max(piece.from, piece.to)));
            var center = (min + max) * 0.5f;
            // How far out the skin is from the middle toward each corner of the ring.
            var reach = new float[CuffSides];
            for (int side = 0; side < CuffSides; side++)
            {
                float angle = side * Mathf.PI * 2f / CuffSides;
                var toward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                foreach (var piece in outline)
                {
                    var a = piece.from - center;
                    var along = piece.to - piece.from;
                    float cross = toward.x * along.y - toward.y * along.x;
                    if (Mathf.Abs(cross) < 1e-9f) continue;
                    float distance = (a.x * along.y - a.y * along.x) / cross;
                    float where = (a.x * toward.y - a.y * toward.x) / cross;
                    if (distance > 0f && where >= -0.001f && where <= 1.001f) reach[side] = Mathf.Max(reach[side], distance);
                }
            }
            // A ring is straight between its corners while the arm bulges, so each corner also covers its neighbours.
            var radius = new float[CuffSides];
            for (int i = 0; i < CuffSides; i++)
                radius[i] = Mathf.Max(reach[i], Mathf.Max(reach[(i + CuffSides - 1) % CuffSides], reach[(i + 1) % CuffSides])) + CuffRoom;
            return sections[s] = (center, radius);
        }

        // Stretches of rings from the hand end to the elbow end. Rings inside a stretch share their corners (smooth);
        // each stretch has its own, so the edges between stretches are crisp.
        var stretches = new (float s, float width, float extra)[][]
        {
            new[] { (near, 0f, 0f), (near, 1f, CuffHemThick) },
            new[] { (near, 1f, CuffHemThick), (near - CuffHem, 1f, CuffHemThick) },
            new[] { (near - CuffHem, 1f, CuffHemThick), (near - CuffHem, 1f, 0f) },
            new[] { (near - CuffHem, 1f, 0f), (middle, 1f, 0f), (far, 1f, 0f) },
            new[] { (far, 1f, 0f), (far, 0f, 0f) },
        };
        var vertices = new List<Vector3>();
        var faces = new List<int>();
        int tubeStart = 0;
        for (int stretch = 0; stretch < stretches.Length; stretch++)
        {
            if (stretch == 3) tubeStart = vertices.Count;
            int first = vertices.Count, rings = stretches[stretch].Length;
            foreach (var ring in stretches[stretch])
            {
                var section = Section(ring.s);
                for (int side = 0; side <= CuffSides; side++)
                {
                    float angle = side * Mathf.PI * 2f / CuffSides;
                    float radius = (section.radius[side % CuffSides] + ring.extra) * ring.width;
                    vertices.Add(axis * ring.s + e1 * (section.center.x + radius * Mathf.Cos(angle)) + e2 * (section.center.y + radius * Mathf.Sin(angle)));
                }
            }
            for (int ring = 0; ring + 1 < rings; ring++)
                for (int side = 0; side < CuffSides; side++)
                {
                    int a = first + ring * (CuffSides + 1) + side, b = a + CuffSides + 1, c = a + 1, d = b + 1;
                    faces.AddRange(new[] { a, c, b, b, c, d });
                }
        }
        var mesh = new Mesh { name = left ? "PlayerHand_Cuff_Left" : "PlayerHand_Cuff_Right" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(faces, 0);
        mesh.RecalculateNormals();
        // Facing outward: the sleeve itself must look away from the arm's middle.
        var tubeSection = Section(near - CuffHem);
        var corner = mesh.vertices[tubeStart];
        var outward = corner - axis * Vector3.Dot(corner, axis) - (e1 * tubeSection.center.x + e2 * tubeSection.center.y);
        if (Vector3.Dot(mesh.normals[tubeStart], outward) < 0f)
        {
            for (int t = 0; t < faces.Count; t += 3) (faces[t], faces[t + 1]) = (faces[t + 1], faces[t]);
            mesh.SetTriangles(faces, 0);
            mesh.RecalculateNormals();
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------- Assets ----------

    static T Load<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception("Missing " + path);

    /// <summary>Keeps the asset that is already there (and what points at it) and gives it the new contents.</summary>
    static Mesh Save(Mesh mesh, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }
        string name = mesh.name;
        EditorUtility.CopySerialized(mesh, existing);
        existing.name = name;
        EditorUtility.SetDirty(existing);
        UnityEngine.Object.DestroyImmediate(mesh);
        return existing;
    }

    static Material Lit(string path, Color color, float smoothness, Texture2D baseMap, Texture2D normal)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? throw new Exception("URP Lit missing."));
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", smoothness);
        if (baseMap != null)
        {
            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_MainTex", baseMap);
        }
        if (normal != null)
        {
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    // ---------- Pictures to look at (no store, no play) ----------

    const string ControllerModel = "Assets/Samples/XR Interaction Toolkit/3.6.1/Starter Assets/Prefabs/Controllers/XR Controller {0}.prefab";

    /// <summary>
    /// Builds the hands and draws them: one sheet of the left hand's poses from four sides, and both hands on controllers
    /// as the player sees them, with the rig's controller model drawn too so the two can be compared.
    /// Unity.exe -batchmode -executeMethod PlayerHandSetup.Preview -hands-evidence folder -quit
    /// </summary>
    [MenuItem("Tools/Hands/Preview Player Hands")]
    public static void Preview()
    {
        var folder = CommandLineValue("-hands-evidence") ?? "Temp/hands";
        Directory.CreateDirectory(folder);
        Setup();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        // Loaded after the new scene: opening one unloads what Setup returned.
        var hands = new[] { Load<PlayerHand>(PrefabPath(true)), Load<PlayerHand>(PrefabPath(false)) };
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.44f);
        var sun = new GameObject("Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.55f, 0.57f, 0.6f);
        camera.nearClipPlane = 0.02f;
        camera.farClipPlane = 10f;
        camera.fieldOfView = 30f;

        // Poses: loose, grip, grip and trigger, trigger only, holding.
        var poses = new (string name, float grip, float trigger, bool holding)[]
        {
            ("modelled", -1f, 0f, false), ("loose", 0f, 0f, false), ("point", 1f, 0f, false), ("fist", 1f, 1f, false), ("trigger", 0f, 1f, false), ("hold", 1f, 0f, true),
        };
        const int cell = 420;
        foreach (bool left in new[] { true, false })
        {
            var hand = (PlayerHand)PrefabUtility.InstantiatePrefab(hands[left ? 0 : 1]);
            var sheet = new Texture2D(cell * 4, cell * poses.Length, TextureFormat.RGB24, false);
            for (int p = 0; p < poses.Length; p++)
            {
                if (poses[p].grip < 0f) hand.PoseAsModelled();
                else hand.PoseNow(poses[p].grip, poses[p].trigger, poses[p].holding);
                ShowPosed(hand);
                var middle = new Vector3(0f, -0.01f, 0.07f);
                float thumb = left ? 1f : -1f;
                // Back of the hand, palm, thumb side, from the fingertips.
                var eyes = new[] { middle + Vector3.up * 0.55f, middle + Vector3.down * 0.55f, middle + Vector3.right * thumb * 0.55f, middle + Vector3.forward * 0.55f };
                var ups = new[] { Vector3.forward, Vector3.forward, Vector3.up, Vector3.up };
                for (int view = 0; view < 4; view++)
                    Draw(camera, eyes[view], middle, ups[view], sheet, view * cell, (poses.Length - 1 - p) * cell, cell);
            }
            File.WriteAllBytes($"{folder}/hand_poses_{(left ? "left" : "right")}.png", sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(hand.gameObject);
        }

        // On controllers, 26 cm apart, pointing forward and a little down as controllers are held.
        var rig = new GameObject("Rig").transform;
        var placed = new List<PlayerHand>();
        var models = new List<GameObject>();
        foreach (bool left in new[] { true, false })
        {
            var controller = new GameObject(left ? "Left Controller" : "Right Controller").transform;
            controller.SetParent(rig, false);
            controller.localPosition = new Vector3(left ? -0.13f : 0.13f, 0f, 0f);
            controller.localRotation = Quaternion.Euler(20f, 0f, 0f);
            var visual = AssetDatabase.LoadAssetAtPath<GameObject>(string.Format(ControllerModel, left ? "Left" : "Right"));
            if (visual != null) models.Add((GameObject)PrefabUtility.InstantiatePrefab(visual, controller));
            placed.Add(Attach(controller, hands[left ? 0 : 1]));
        }
        camera.fieldOfView = 50f;
        var views = new (string name, Vector3 eye, Vector3 target)[]
        {
            ("player", new Vector3(0f, 0.32f, -0.42f), new Vector3(0f, -0.02f, -0.04f)),
            ("left_side", new Vector3(-0.75f, 0.05f, -0.08f), new Vector3(0f, -0.02f, -0.08f)),
            ("above", new Vector3(0f, 0.7f, -0.09f), new Vector3(0f, 0f, -0.08f)),
            ("behind_left", new Vector3(-0.2f, 0.12f, -0.62f), new Vector3(-0.15f, -0.05f, -0.2f)),
        };
        var report = new StringBuilder();
        foreach (var pose in poses)
        {
            if (pose.grip < 0f) continue;
            foreach (var hand in placed)
            {
                hand.PoseNow(pose.grip, pose.trigger, pose.holding);
                ShowPosed(hand);
            }
            foreach (bool withModel in new[] { true, false })
            {
                foreach (var model in models) model.SetActive(withModel);
                var sheet = new Texture2D(640 * views.Length, 640, TextureFormat.RGB24, false);
                for (int v = 0; v < views.Length; v++)
                    Draw(camera, views[v].eye, views[v].target, v == 2 ? Vector3.forward : Vector3.up, sheet, v * 640, 0, 640);
                File.WriteAllBytes($"{folder}/on_controller_{pose.name}{(withModel ? "_with_model" : "")}.png", sheet.EncodeToPNG());
            }
            foreach (var hand in placed)
            {
                var posed = hand.GetComponentInChildren<SkinnedMeshRenderer>(true).transform.Find("Posed");
                var drawn = posed.GetComponent<MeshFilter>().sharedMesh.vertices.Select(v => hand.transform.InverseTransformPoint(posed.TransformPoint(v))).ToArray();
                report.AppendLine($"{pose.name} {hand.name}: drawn skin from z {drawn.Min(v => v.z):0.0000} to {drawn.Max(v => v.z):0.0000} in the hand's space; fingertips to {Enumerable.Range(0, 5).Max(f => hand.transform.InverseTransformPoint(hand.Tips[f].position).z):0.0000}");
            }
            foreach (var hand in placed)
                report.AppendLine($"{pose.name} {hand.name}: index tip in controller space {hand.transform.parent.InverseTransformPoint(hand.Tips[PlayerHand.Index].position).ToString("F4")}, palm {hand.transform.localPosition.ToString("F4")} euler {hand.transform.localEulerAngles.ToString("F1")}");
        }
        File.WriteAllText($"{folder}/hands_report.txt", report.ToString());
        Debug.Log("[Hands] preview written to " + folder + "\n" + report);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    /// <summary>
    /// Outside play, skin is worked out once per editor frame, so several poses drawn in one call would all show the first.
    /// The pictures draw a copy of the skin as it is posed now instead.
    /// </summary>
    static void ShowPosed(PlayerHand hand)
    {
        var skinned = hand.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var holder = skinned.transform.Find("Posed");
        if (holder == null)
        {
            holder = new GameObject("Posed").transform;
            holder.SetParent(skinned.transform, false);
            holder.gameObject.AddComponent<MeshFilter>().sharedMesh = new Mesh();
            holder.gameObject.AddComponent<MeshRenderer>().sharedMaterial = skinned.sharedMaterial;
            skinned.forceRenderingOff = true;
        }
        // useScale: the corners come out as the skin's own transform, scale and all, places them. (Without it they come
        // out already scaled, and under the scaled hand the copy was 6 % too large.)
        skinned.BakeMesh(holder.GetComponent<MeshFilter>().sharedMesh, true);
    }

    /// <summary>
    /// Draws the right hand holding boxes the sizes of the things in the store, each started the way the day's check
    /// starts a grab (the controller's origin at or near the middle of the thing), from the player's eyes, the side and
    /// below, four to a sheet.
    /// Unity.exe -batchmode -executeMethod PlayerHandSetup.PreviewGrasps -hands-evidence folder -quit
    /// </summary>
    [MenuItem("Tools/Hands/Preview Grasps")]
    public static void PreviewGrasps()
    {
        var folder = CommandLineValue("-hands-evidence") ?? "Temp/hands";
        Directory.CreateDirectory(folder);
        Setup();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.52f);
        var sun = new GameObject("Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1f;
        sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.55f, 0.57f, 0.6f);
        camera.nearClipPlane = 0.02f;
        camera.farClipPlane = 10f;
        camera.fieldOfView = 32f;
        var paint = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        paint.SetColor("_BaseColor", new Color(0.93f, 0.55f, 0.18f));
        paint.SetFloat("_Smoothness", 0.25f);

        // Size along the box's own x, y, z; how it is turned; where its middle is from the controller's origin.
        var cases = new (string name, Vector3 size, Vector3 euler, Vector3 middle)[]
        {
            ("snack bag standing", new Vector3(0.18f, 0.26f, 0.07f), Vector3.zero, Vector3.zero),
            ("snack bag lying", new Vector3(0.18f, 0.07f, 0.26f), Vector3.zero, new Vector3(0f, -0.03f, 0.02f)),
            ("snack bag turned", new Vector3(0.18f, 0.26f, 0.07f), new Vector3(20f, 50f, 10f), Vector3.zero),
            ("ramen pack standing", new Vector3(0.12f, 0.14f, 0.035f), Vector3.zero, Vector3.zero),
            ("small box lying", new Vector3(0.085f, 0.03f, 0.12f), Vector3.zero, new Vector3(0f, -0.01f, 0f)),
            ("card", new Vector3(0.065f, 0.008f, 0.1f), Vector3.zero, new Vector3(0f, -0.01f, 0f)),
            ("note", new Vector3(0.142f, 0.0012f, 0.068f), Vector3.zero, new Vector3(0f, -0.005f, 0f)),
            ("coin", new Vector3(0.033f, 0.003f, 0.033f), Vector3.zero, new Vector3(0f, -0.005f, 0f)),
            ("box handle", new Vector3(0.03f, 0.035f, 0.1f), new Vector3(0f, 90f, 0f), Vector3.zero),
            ("large box", new Vector3(0.36f, 0.3f, 0.3f), Vector3.zero, new Vector3(0f, -0.12f, 0.05f)),
            ("cutter in its holder", new Vector3(0.12f, 0.02f, 0.03f), new Vector3(0f, 0f, 70f), new Vector3(0.02f, 0f, 0f)),
        };
        var controller = new GameObject("Right Controller").transform;
        var hand = Attach(controller, Load<PlayerHand>(PrefabPath(false)));
        var homePosition = hand.transform.position;
        var homeRotation = hand.transform.rotation;
        const int cell = 520, perSheet = 4;
        Texture2D sheet = null;
        int rows = 0;
        var report = new StringBuilder();
        for (int c = 0; c < cases.Length; c++)
        {
            if (c % perSheet == 0)
            {
                rows = Mathf.Min(perSheet, cases.Length - c);
                sheet = new Texture2D(cell * 3, cell * rows, TextureFormat.RGB24, false);
            }
            int row = (rows - 1 - c % perSheet) * cell;
            var thing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            thing.transform.SetPositionAndRotation(cases[c].middle, Quaternion.Euler(cases[c].euler));
            thing.transform.localScale = cases[c].size;
            thing.GetComponent<Renderer>().sharedMaterial = paint;
            hand.transform.SetPositionAndRotation(homePosition, homeRotation);
            bool fits = hand.FitTo(thing.GetComponent<BoxCollider>(), homePosition, homeRotation);
            if (fits) hand.PoseFitNow();
            else hand.PoseNow(1f, 0f, true);
            ShowPosed(hand);
            var moved = hand.transform.position - homePosition;
            report.AppendLine($"{cases[c].name}: {(fits ? "fitted" : "plain hold")}; hand moved {moved.magnitude * 100f:0.0} cm and turned {Quaternion.Angle(homeRotation, hand.transform.rotation):0} deg from the controller; {hand.DescribeFit()}");
            var look = (hand.transform.TransformPoint(hand.PadPoint) + thing.transform.position) * 0.5f;
            Draw(camera, look + new Vector3(0.1f, 0.34f, -0.42f), look, Vector3.up, sheet, 0, row, cell);
            Draw(camera, look + new Vector3(-0.52f, 0.1f, 0.05f), look, Vector3.up, sheet, cell, row, cell);
            Draw(camera, look + new Vector3(0.12f, -0.42f, -0.3f), look, Vector3.up, sheet, cell * 2, row, cell);
            UnityEngine.Object.DestroyImmediate(thing);
            if (c % perSheet == perSheet - 1 || c == cases.Length - 1)
                File.WriteAllBytes($"{folder}/grasps_right_{c / perSheet + 1}.png", sheet.EncodeToPNG());
        }
        File.WriteAllText($"{folder}/grasps_report.txt", report.ToString());
        Debug.Log("[Hands] grasps written to " + folder);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    static void Draw(Camera camera, Vector3 eye, Vector3 target, Vector3 up, Texture2D sheet, int x, int y, int size)
    {
        camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, up));
        var texture = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 4);
        var resolved = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 1);
        camera.targetTexture = texture;
        camera.Render();
        Graphics.Blit(texture, resolved);
        var previous = RenderTexture.active;
        RenderTexture.active = resolved;
        sheet.ReadPixels(new Rect(0, 0, size, size), x, y);
        sheet.Apply();
        RenderTexture.active = previous;
        camera.targetTexture = null;
        RenderTexture.ReleaseTemporary(texture);
        RenderTexture.ReleaseTemporary(resolved);
    }

    static string CommandLineValue(string key)
    {
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, key);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
