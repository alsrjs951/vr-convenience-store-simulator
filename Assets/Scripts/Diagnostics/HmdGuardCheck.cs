using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
using UnityEngine.XR.Management;

/// <summary>
/// Runs only with -hmd-guard-check. Waits for the scene to settle, reports whether the XR Device Simulator is running
/// and which HMD devices exist, then quits. It reads state and changes nothing. With the OpenXR Mock Runtime this
/// exercises the "headset present" path without a headset; it says nothing about a real Quest.
/// </summary>
public class HmdGuardCheck : MonoBehaviour
{
    const float SettleSeconds = 6f;
    const float MaxSeconds = 30f;

    [Serializable] class Report
    {
        public string runtime;
        public bool xrDeviceActive;
        public string activeLoader;
        public string xrRuntimeJson;
        public int simulatorObjects;
        public int simulatorObjectsActive;
        public bool simulatorInstance;
        public bool stereoEnabled;
        public float secondsWaited;
        public List<string> hmdDevices = new List<string>();
        public List<string> allDevices = new List<string>();
        public List<string> nativeXrDevices = new List<string>();
        public string leftStickMove = "";
        public string rightStickSnapTurn = "";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Begin()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-hmd-guard-check") < 0) return;
        Application.runInBackground = true;
        var runner = new GameObject("HMD Guard Check");
        DontDestroyOnLoad(runner);
        runner.AddComponent<HmdGuardCheck>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(SettleSeconds);

        // Without a runtime only the simulator's devices will ever exist, so there is nothing further to wait for.
        float begun = Time.realtimeSinceStartup;
        while (XRSettings.isDeviceActive && !HasRealHmd() && Time.realtimeSinceStartup - begun < MaxSeconds)
            yield return null;

        var report = new Report
        {
            runtime = Application.isEditor ? "Editor Play Mode" : "Player",
            secondsWaited = SettleSeconds + Time.realtimeSinceStartup - begun,
            xrDeviceActive = XRSettings.isDeviceActive,
        };
        var settings = XRGeneralSettings.Instance;
        var loader = settings != null && settings.Manager != null ? settings.Manager.activeLoader : null;
        report.activeLoader = loader != null ? loader.name : "none";
        report.xrRuntimeJson = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
        foreach (var simulator in FindObjectsByType<XRDeviceSimulator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            report.simulatorObjects++;
            if (simulator.isActiveAndEnabled) report.simulatorObjectsActive++;
        }
        report.simulatorInstance = XRDeviceSimulator.instance != null;
        report.stereoEnabled = Camera.main != null && Camera.main.stereoEnabled;
        foreach (var device in InputSystem.devices)
        {
            var description = $"{device.GetType().Name}:{device.displayName}";
            report.allDevices.Add(description);
            if (device is XRHMD) report.hmdDevices.Add(description);
        }
        var native = new List<UnityEngine.XR.InputDevice>();
        InputDevices.GetDevices(native);
        foreach (var device in native)
            report.nativeXrDevices.Add($"{device.name}|{device.characteristics}");

        var move = FindFirstObjectByType<DynamicMoveProvider>();
        if (move != null) report.leftStickMove = Describe(move.leftHandMoveInput.inputActionReference.action);
        var snap = FindFirstObjectByType<SnapTurnProvider>();
        if (snap != null) report.rightStickSnapTurn = Describe(snap.rightHandTurnInput.inputActionReference.action);

        Debug.Log($"[HMD Guard Check] leftStickMove=[{report.leftStickMove}] rightStickSnapTurn=[{report.rightStickSnapTurn}]");
        Debug.Log($"[HMD Guard Check] waited={report.secondsWaited:0.0}s xrActive={report.xrDeviceActive} loader={report.activeLoader} simulatorActive={report.simulatorObjectsActive}/{report.simulatorObjects} instance={report.simulatorInstance} stereo={report.stereoEnabled} hmd=[{string.Join(", ", report.hmdDevices)}] devices=[{string.Join(", ", report.allDevices)}] native=[{string.Join(", ", report.nativeXrDevices)}]");

        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-hmd-guard-evidence");
        if (index >= 0 && index + 1 < args.Length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(args[index + 1]));
            File.WriteAllText(args[index + 1], JsonUtility.ToJson(report, true));
        }
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(0);
#else
        Application.Quit(0);
#endif
    }

    static string Describe(InputAction action)
    {
        var controls = new List<string>();
        foreach (var control in action.controls) controls.Add(control.path);
        return $"enabled={action.enabled} {string.Join("|", controls)}";
    }

    static bool HasRealHmd()
    {
        foreach (var device in InputSystem.devices)
            if (device is XRHMD && device is not XRSimulatedHMD) return true;
        return false;
    }
}
