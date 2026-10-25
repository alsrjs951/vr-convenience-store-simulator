using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

/// <summary>P0-L1 패키지 설치 자동화 — batch 모드에서 Client.Add를 순차 실행 후 종료.</summary>
public static class P0PackageSetup
{
    static readonly string[] Packages =
    {
        "com.unity.render-pipelines.universal",
        "com.unity.xr.management",
        "com.unity.xr.openxr",
        "com.unity.xr.interaction.toolkit",
        "https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#v1.1.0",
    };

    static readonly Queue<string> Pending = new Queue<string>(Packages);
    static readonly List<string> Results = new List<string>();
    static AddRequest Current;

    public static void Run()
    {
        Debug.Log("[P0] 패키지 설치 시작");
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (Current != null && !Current.IsCompleted)
            return;

        if (Current != null)
        {
            if (Current.Status == StatusCode.Success && Current.Result != null)
                Results.Add("OK " + Current.Result.packageId + " @ " + Current.Result.version);
            else if (Current.Error != null)
                Results.Add("FAIL " + Current.Error.message);
            else
                Results.Add("FAIL 원인 미상");
            Current = null;
        }

        if (Pending.Count == 0)
        {
            var fails = 0;
            foreach (var r in Results)
            {
                Debug.Log("[P0] " + r);
                if (r.StartsWith("FAIL")) fails++;
            }
            EditorApplication.Exit(fails == 0 ? 0 : 2);
            return;
        }

        var next = Pending.Dequeue();
        Debug.Log("[P0] 설치 중: " + next);
        Current = Client.Add(next);
    }
}
