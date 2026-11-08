using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// P0 종료 기준 ① 보조 — SplatTest 씬에 XR Origin(XR Rig)·XR Device Simulator 배치, TestCam 비활성화,
/// 샘플 스플랫을 눈높이 앞(0, 1.3, 0.6)으로 이동. batch: -executeMethod P0XRSceneSetup.Run
/// </summary>
public static class P0XRSceneSetup
{
    const string ScenePath = "Assets/Scenes/SplatTest.unity";
    const string Samples = "Assets/Samples/XR Interaction Toolkit/3.6.1/";
    const string OriginPrefab = Samples + "Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
    const string SimulatorPrefab = Samples + "XR Device Simulator/XR Device Simulator.prefab";

    public static void Run()
    {
        try
        {
            Setup();
            Debug.Log("[P0-XRScene] 완료");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0-XRScene] 실패: " + e);
            if (Application.isBatchMode) EditorApplication.Exit(3);
        }
    }

    [MenuItem("Tools/P0/Add XR Rig to SplatTest %#F8")]
    static void RunFromMenu() => Run();

    static void Setup()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        Instantiate(OriginPrefab, Vector3.zero);
        Instantiate(SimulatorPrefab, Vector3.zero);

        // Starter Assets locomotion applies gravity. Without a collider the rig
        // falls below the sample immediately, making a valid splat look invisible.
        var floor = GameObject.Find("P0 Floor");
        if (floor == null) floor = new GameObject("P0 Floor");
        floor.transform.position = new Vector3(0f, -0.05f, 0f);
        var floorCollider = floor.GetComponent<BoxCollider>();
        if (floorCollider == null) floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(20f, 0.1f, 20f);

        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name == "TestCam") go.SetActive(false);
            if (go.name == "SampleSplat") go.transform.position = new Vector3(0f, 1.3f, 0.6f);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new Exception("씬 저장 실패");
    }

    static void Instantiate(string path, Vector3 pos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new Exception("프리팹 없음: " + path);
        var name = prefab.name;
        foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            if (root.name == name) return; // 중복 배치 방지
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.position = pos;
    }
}
