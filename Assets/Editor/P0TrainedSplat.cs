using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0TrainedSplat
{
    const string Folder = "Assets/GaussianSplats/Poster";
    public const string Scene = "Assets/Scenes/TrainedSplatTest.unity";
    [Serializable] class View { public Vector3 position, forward, up; }

    [MenuItem("Tools/P0/Import Trained Poster")]
    public static void Import()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Exit Play first.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        AssetDatabase.Refresh();
        var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(Folder + "/poster.asset");
        if (existing == null)
        {
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            var so = new SerializedObject(creator);
            so.FindProperty("m_InputFile").stringValue = Path.GetFullPath(Folder + "/poster.ply");
            so.FindProperty("m_OutputFolder").stringValue = Folder;
            foreach (var field in new[] { "m_FormatPos", "m_FormatScale", "m_FormatColor", "m_FormatSH" })
                so.FindProperty(field).enumValueIndex = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            typeof(GaussianSplatAssetCreator).GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(creator, null);
            var error = typeof(GaussianSplatAssetCreator).GetField("m_ErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(creator) as string;
            if (!string.IsNullOrEmpty(error)) throw new Exception(error);
        }
        finally { UnityEngine.Object.DestroyImmediate(creator); }
        }
        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(Folder + "/poster.asset");
        if (asset == null || asset.splatCount != 12970) throw new Exception("Expected 12970 trained splats.");
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SplatTest.unity");
        var splat = UnityEngine.Object.FindFirstObjectByType<GaussianSplatRenderer>();
        splat.name = "TrainedPoster";
        splat.m_Asset = asset;
        var view = JsonUtility.FromJson<View>(File.ReadAllText(Folder + "/view.json"));
        // Map the recorded OpenGL camera (-Z forward, +Y up) to the simulator's
        // initial +Z view. This is a preview alignment, not a physical scale calibration.
        splat.transform.rotation = Quaternion.Inverse(Quaternion.LookRotation(view.forward, view.up));
        splat.transform.position = new Vector3(0, 1.36144f, 0) - splat.transform.rotation * view.position;
        splat.transform.localScale = Vector3.one;
        // OpenGL camera coordinates are right-handed; Unity views are left-handed.
        // Reflect the aligned X axis so the preview matches the source photograph.
        var coordinateRoot = new GameObject("Nerfstudio Coordinate Conversion").transform;
        splat.transform.SetParent(coordinateRoot, false);
        coordinateRoot.localScale = new Vector3(-1, 1, 1);
        new GameObject("P0 Verification").AddComponent<P0PlayerVerification>();
        if (!EditorSceneManager.SaveScene(scene, Scene)) throw new Exception("Scene save failed.");
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("docs/p0/evidence");
        File.WriteAllText("docs/p0/evidence/trained_import.txt", $"PASS: poster.ply -> poster.asset; splats={asset.splatCount}; bounds={asset.boundsMin}..{asset.boundsMax}; scene={Scene}\n");
        Debug.Log("[P0 Trained] Import PASS");
    }

    public static void Build()
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { Scene, "Assets/Scenes/SplatTest.unity" },
            locationPathName = "Builds/P0Windows/P0Simulator.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        var s = report.summary;
        File.WriteAllText("docs/p0/evidence/project_il2cpp_build.txt", $"Unity {Application.unityVersion}; actual project Windows x64 IL2CPP; result={s.result}; errors={s.totalErrors}; warnings={s.totalWarnings}; bytes={s.totalSize}; duration={s.totalTime}; scenes={Scene}, SplatTest\n");
        if (s.result != BuildResult.Succeeded) throw new Exception("Project build failed: " + s.result);
    }

    public static void ImportAndBuild() { Import(); Build(); }

    public static void VerifyEditor()
    {
        EditorSceneManager.OpenScene(Scene);
        EditorApplication.isPlaying = true;
    }
}
