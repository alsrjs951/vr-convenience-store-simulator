using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>P0-L2·L3 설정 자동화 — URP 에셋·스플랫 URP Feature·레이어·태그·충돌 매트릭스·Windows DX12 프리셋.</summary>
public static class P0Configure
{
    public static void Run()
    {
        var log = new StringBuilder();
        try
        {
            Configure(log);
            Debug.Log("[P0] 설정 완료\n" + log);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0] 설정 실패: " + e + "\n" + log);
            EditorApplication.Exit(3);
        }
    }

    static void Configure(StringBuilder log)
    {
        // 1. URP 에셋 + 렌더러 데이터 생성
        Directory.CreateDirectory("Assets/Settings");
        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        var urpAsset = UniversalRenderPipelineAsset.Create(rendererData);

        // 2. GaussianSplatURPFeature 등록 (internal 타입 → 리플렉션)
        var featureType = Type.GetType("GaussianSplatting.Runtime.GaussianSplatURPFeature, GaussianSplatting");
        if (featureType == null) throw new Exception("GaussianSplatURPFeature 타입을 찾을 수 없음");
        var feature = ScriptableObject.CreateInstance(featureType);
        var so = new SerializedObject(rendererData);
        var feats = so.FindProperty("m_RendererFeatures");
        feats.arraySize++;
        feats.GetArrayElementAtIndex(feats.arraySize - 1).objectReferenceValue = feature;
        var map = so.FindProperty("m_RendererFeaturesMap");
        if (map != null)
        {
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).intValue = feats.arraySize - 1;
        }
        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(rendererData, "Assets/Settings/PC_Renderer.asset");
        // Feature는 렌더러 데이터의 서브 에셋으로 저장해야 참조가 유지됨(fileID: 0 깨짐 방지)
        AssetDatabase.AddObjectToAsset(feature, rendererData);
        AssetDatabase.CreateAsset(urpAsset, "Assets/Settings/PC_RPAsset.asset");
        AssetDatabase.SaveAssets();
        log.AppendLine("URP 에셋 생성: Assets/Settings/PC_RPAsset.asset + PC_Renderer.asset (+ GaussianSplatURPFeature)");

        // 3. 활성 파이프라인 지정 + 전 품질 레벨 적용
        GraphicsSettings.defaultRenderPipeline = urpAsset;
        for (var i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urpAsset;
        }
        log.AppendLine("기본 파이프라인: URP (전 품질 레벨)");

        // 4. MSAA Off (스플랫 렌더러 미지원)
        urpAsset.msaaSampleCount = 1;
        EditorUtility.SetDirty(urpAsset);
        log.AppendLine("URP MSAA: Off");

        // 5. Render Graph 호환모드 — Unity 6000.3에서 설정 자체가 제거됨(항상 Render Graph). 상태만 확인.
        try
        {
            var rg = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
            log.AppendLine("RenderGraph 호환모드: " + (rg != null ? rg.enableRenderCompatibilityMode.ToString() + " (6000.3에서 항상 false — 요건 자동 충족)" : "설정 없음"));
        }
        catch (Exception e)
        {
            log.AppendLine("RenderGraph 호환모드 확인 생략: " + e.Message);
        }

        // 6. 레이어·태그 (P0-L3)
        AssetDatabase.SaveAssets();
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        string[] names = { "Splat", "Interactable", "BarcodeFace", "Scanner", "Zone" };
        for (var i = 0; i < names.Length; i++)
            layers.GetArrayElementAtIndex(8 + i).stringValue = names[i];
        var tags = tagManager.FindProperty("tags");
        var hasBarcode = false;
        for (var i = 0; i < tags.arraySize; i++)
            if (tags.GetArrayElementAtIndex(i).stringValue == "Barcode") hasBarcode = true;
        if (!hasBarcode)
        {
            tags.arraySize++;
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = "Barcode";
        }
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        log.AppendLine("레이어: Splat/Interactable/BarcodeFace/Scanner/Zone, 태그: Barcode");

        // 7. 충돌 매트릭스 — Splat 전면 OFF, BarcodeFace는 Scanner와만 허용
        var splat = LayerMask.NameToLayer("Splat");
        var face = LayerMask.NameToLayer("BarcodeFace");
        var scanner = LayerMask.NameToLayer("Scanner");
        for (var i = 0; i < 32; i++) Physics.IgnoreLayerCollision(splat, i, true);
        for (var i = 0; i < 32; i++) Physics.IgnoreLayerCollision(face, i, i != scanner);
        AssetDatabase.SaveAssets();
        log.AppendLine("충돌 매트릭스: Splat 전면 OFF, BarcodeFace↔Scanner만 허용");

        // 8. Windows 빌드 그래픽 API DX12 고정 (체크리스트 §6 첫 항목 선반영)
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
        log.AppendLine("Windows Graphics APIs: Direct3D12만");

        // 9. XRI 샘플은 Samples~ 폴더 복사로 별도 임포트(스크립트 밖) — 여기서는 안내만 기록
        log.AppendLine("XRI 샘플(Starter Assets·XR Device Simulator): Samples~ 복사 방식으로 별도 임포트됨");

        // 10. Input System 백엔드 Both (XRI 요구 — 다음 에디터 실행부터 적용)
        try
        {
            PlayerSettings.SetPropertyInt("activeInputHandler", 2);
            log.AppendLine("Active Input Handling: Both");
        }
        catch (Exception e)
        {
            log.AppendLine("Input Handler 설정 생략: " + e.Message);
        }

        AssetDatabase.SaveAssets();
    }
}
