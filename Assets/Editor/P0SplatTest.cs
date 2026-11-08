using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;

/// <summary>P0 종료 기준 1 자동 검증 — 샘플 PLY → GaussianSplatAsset 변환, 씬 구성, 플레이 모드 스크린샷.</summary>
public static class P0SplatTest
{
    const string kInputPly = "Assets/GaussianSplats/Sample/sample_sphere.ply";
    const string kOutputFolder = "Assets/GaussianSplats/Sample";
    const string kScreenshot = "splat_check.png";

    [MenuItem("Tools/P0/Reimport Sample Splat %#F7")]
    static void ReimportSample()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play mode before reimporting the sample.");
        var asset = CreateSplatAsset();
        if (asset == null || asset.splatCount != 1200)
            throw new Exception("Expected the 1200-splat P0 sample.");
        Debug.Log($"[P0 Import] PASS {SystemInfo.graphicsDeviceType}: {kInputPly} -> {asset.name}, splats={asset.splatCount}");
    }

    public static void Run()
    {
        try
        {
            // 플레이 모드 진입 시 도메인 리로드 비활성 — update 콜백과 정적 상태를 유지하기 위함
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            var asset = CreateSplatAsset();
            if (asset == null) throw new Exception("GaussianSplatAsset이 생성되지 않았음");
            Debug.Log($"[P0] GaussianSplatAsset 생성 완료: {asset.name} — splats={asset.splatCount}");

            BuildScene(asset);

            EditorApplication.update += PlayTick;
            EditorApplication.isPlaying = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[P0] 스플랫 검증 실패: " + e);
            EditorApplication.Exit(4);
        }
    }

    static GaussianSplatAsset CreateSplatAsset()
    {
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        var so = new SerializedObject(creator);
        so.FindProperty("m_InputFile").stringValue = Path.GetFullPath(kInputPly);
        so.FindProperty("m_OutputFolder").stringValue = kOutputFolder;
        so.FindProperty("m_FormatPos").enumValueIndex = 0;   // Float32 (무손실, 청킹 없음)
        so.FindProperty("m_FormatScale").enumValueIndex = 0;
        so.FindProperty("m_FormatColor").enumValueIndex = 0; // Float32x4
        so.FindProperty("m_FormatSH").enumValueIndex = 0;    // Float32
        so.ApplyModifiedPropertiesWithoutUndo();

        var mi = typeof(GaussianSplatAssetCreator).GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi == null) throw new Exception("GaussianSplatAssetCreator.CreateAsset 메서드를 찾을 수 없음");
        mi.Invoke(creator, null);

        var errField = typeof(GaussianSplatAssetCreator).GetField("m_ErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        var err = errField?.GetValue(creator) as string;
        if (!string.IsNullOrEmpty(err)) throw new Exception("에셋 생성 오류: " + err);

        return AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(kOutputFolder + "/sample_sphere.asset");
    }

    static void BuildScene(GaussianSplatAsset asset)
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var go = new GameObject("SampleSplat");
        var renderer = go.AddComponent<GaussianSplatRenderer>();
        renderer.m_Asset = asset;

        var camGo = new GameObject("TestCam");
        var cam = camGo.AddComponent<Camera>();
        camGo.transform.position = new Vector3(0f, 0f, -0.55f);
        cam.fieldOfView = 50f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);

        // batch 모드에는 GameView가 없으므로 카메라를 RenderTexture로 직접 렌더 → ReadPixels로 캡처
        m_RT = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = m_RT;

        EditorSceneManager.SaveScene(scene, "Assets/Scenes/SplatTest.unity");
        Debug.Log("[P0] 검증 씬 저장: Assets/Scenes/SplatTest.unity (카메라 → RenderTexture 캡처 모드)");
    }

    static int m_Frames;
    static bool m_Captured;
    static RenderTexture m_RT;

    static void PlayTick()
    {
        if (!EditorApplication.isPlaying) return;
        m_Frames++;
        if (m_Frames == 40 && !m_Captured)
        {
            m_Captured = true;
            var prev = RenderTexture.active;
            RenderTexture.active = m_RT;
            var tex = new Texture2D(640, 480, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 640, 480), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.GetFullPath(kScreenshot), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Debug.Log("[P0] RenderTexture 캡처 완료 → " + Path.GetFullPath(kScreenshot));
        }
        if (m_Frames >= 90)
        {
            EditorApplication.isPlaying = false;
            Debug.Log($"[P0] 플레이 검증 종료 — 총 {m_Frames}프레임");
            EditorApplication.Exit(0);
        }
    }
}
