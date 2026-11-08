using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P1ProductTrial
{
    const string Folder = "Assets/GaussianSplats/DakdariTrial";
    const string Scene = "Assets/Scenes/DakdariTrial.unity";
    [Serializable] class View { public Vector3 position, forward, up; public int count; }

    [MenuItem("Tools/P1/Prepare Dakdari Trial")]
    public static void Prepare() => PrepareAsset(Folder, Scene, false);

    [MenuItem("Tools/P1/Prepare Isolated Dakdari")]
    public static void PrepareIsolated() => PrepareAsset("Assets/GaussianSplats/DakdariIsolated", "Assets/Scenes/DakdariIsolated.unity", true);

    [MenuItem("Tools/P1/Prepare Refined Dakdari")]
    public static void PrepareRefined() => PrepareAsset("Assets/GaussianSplats/DakdariRefined", "Assets/Scenes/DakdariRefined.unity", true);

    static void PrepareAsset(string folder, string scenePath, bool isolated)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Exit Play first.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        AssetDatabase.Refresh();
        var view = JsonUtility.FromJson<View>(File.ReadAllText(folder + "/view.json"));
        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(folder + "/dakdari.asset");
        if (asset == null)
        {
            var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
            try
            {
                var so = new SerializedObject(creator);
                so.FindProperty("m_InputFile").stringValue = Path.GetFullPath(folder + "/dakdari.ply");
                so.FindProperty("m_OutputFolder").stringValue = folder;
                foreach (var f in new[] { "m_FormatPos", "m_FormatScale", "m_FormatColor", "m_FormatSH" }) so.FindProperty(f).enumValueIndex = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
                typeof(GaussianSplatAssetCreator).GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(creator, null);
                var error = typeof(GaussianSplatAssetCreator).GetField("m_ErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(creator) as string;
                if (!string.IsNullOrEmpty(error)) throw new Exception(error);
            }
            finally { UnityEngine.Object.DestroyImmediate(creator); }
            asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(folder + "/dakdari.asset");
        }
        if (!asset || asset.splatCount != view.count) throw new Exception("Unexpected trial asset count.");
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SplatTest.unity");
        var splat = UnityEngine.Object.FindFirstObjectByType<GaussianSplatRenderer>();
        splat.name = isolated ? "Dakdari Isolated Trial" : "Dakdari Trial (background included)";
        splat.m_Asset = asset;
        splat.transform.rotation = Quaternion.Inverse(Quaternion.LookRotation(view.forward, view.up));
        splat.transform.position = new Vector3(0, 1.36144f, 0) - splat.transform.rotation * view.position;
        splat.transform.localScale = Vector3.one;
        var parent = new GameObject("Nerfstudio Coordinate Conversion").transform;
        splat.transform.SetParent(parent, false);
        parent.localScale = new Vector3(-1, 1, 1);
        new GameObject("Trial Verification").AddComponent<P0PlayerVerification>().expectedSplatCount = view.count;
        if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new Exception("Scene save failed.");
        AssetDatabase.SaveAssets();
        Debug.Log($"[P1] Import PASS: {asset.splatCount} splats -> {scenePath}");
    }

    public static void Verify()
    {
        EditorSceneManager.OpenScene(Scene);
        EditorApplication.isPlaying = true;
    }

    public static void VerifyIsolated()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/DakdariIsolated.unity");
        EditorApplication.isPlaying = true;
    }

    public static void VerifyRefined()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/DakdariRefined.unity");
        EditorApplication.isPlaying = true;
    }
}
