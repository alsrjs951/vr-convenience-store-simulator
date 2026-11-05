using System;
using System.Text;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

/// <summary>
/// P0-L2 OpenXR 보강 — PC(Standalone) OpenXR 로더 등록, Oculus Touch Controller Profile(Quest 2) 활성화,
/// Stereo Rendering Mode = Multi-pass(마일스톤 P0-L2 기본값). batch: -executeMethod P0XRConfigure.Run
/// </summary>
public static class P0XRConfigure
{
    const string PerBuildTargetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

    public static void Run()
    {
        var log = new StringBuilder();
        try
        {
            Configure(log);
            Debug.Log("[P0-XR] 설정 완료\n" + log);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0-XR] 설정 실패: " + e + "\n" + log);
            if (Application.isBatchMode) EditorApplication.Exit(3);
        }
    }

    [MenuItem("Tools/P0/Configure OpenXR (Quest 2)")]
    static void RunFromMenu() => Run();

    static void Configure(StringBuilder log)
    {
        // 1. XRGeneralSettingsPerBuildTarget 확보(기존 에셋 재사용)
        EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perBT);
        if (perBT == null) perBT = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(PerBuildTargetPath);
        if (perBT == null)
        {
            perBT = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perBT, PerBuildTargetPath);
        }
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBT, true);

        // 2. Standalone 설정 + 매니저
        var settings = perBT.SettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            settings.name = "Standalone Settings";
            perBT.SetSettingsForBuildTarget(BuildTargetGroup.Standalone, settings);
            AssetDatabase.AddObjectToAsset(settings, perBT);
        }
        if (settings.Manager == null)
        {
            var mgr = ScriptableObject.CreateInstance<XRManagerSettings>();
            mgr.name = "Standalone Providers";
            AssetDatabase.AddObjectToAsset(mgr, perBT);
            settings.Manager = mgr;
        }
        settings.InitManagerOnStart = true;

        var ok = XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Standalone);
        if (!ok && !XRPackageMetadataStore.IsLoaderAssigned("UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Standalone))
            throw new Exception("OpenXR 로더 할당 실패");
        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(settings.Manager);
        EditorUtility.SetDirty(perBT);
        log.AppendLine("XR Plug-in Management(PC): OpenXR 로더 할당, Initialize XR on Startup = true");

        // 3. OpenXR — Oculus Touch 프로필 + Multi-pass
        var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        if (oxr == null) throw new Exception("OpenXR Standalone 설정을 찾을 수 없음");
        var touch = oxr.GetFeature<OculusTouchControllerProfile>();
        if (touch == null) throw new Exception("OculusTouchControllerProfile 기능 없음");
        touch.enabled = true;
        EditorUtility.SetDirty(touch);
        oxr.renderMode = OpenXRSettings.RenderMode.MultiPass;
        EditorUtility.SetDirty(oxr);
        log.AppendLine("OpenXR(PC): Oculus Touch Controller Profile ON, Render Mode = Multi-pass");

        AssetDatabase.SaveAssets();
    }
}
