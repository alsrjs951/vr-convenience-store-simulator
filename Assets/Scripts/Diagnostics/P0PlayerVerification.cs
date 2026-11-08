using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// Runs only with -p0-verify. Normal simulator and HMD sessions are unaffected.
public class P0PlayerVerification : MonoBehaviour
{
    public int expectedSplatCount = 12970;
    Keyboard keyboard;
    Camera view;
    Vector3 start;
    float started;
    int stage;
    string output;
    bool verify;
    bool ownedKeyboard;

    void Start()
    {
        verify = Array.IndexOf(Environment.GetCommandLineArgs(), "-p0-verify") >= 0;
        if (!verify) return;
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        output = Path.Combine(Application.dataPath, "../docs/p0/evidence");
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "-p0-evidence");
        if (index >= 0 && index + 1 < args.Length) output = args[index + 1];
        Directory.CreateDirectory(output);
        started = Time.realtimeSinceStartup;
    }

    void Update()
    {
        if (!verify) return;
        var elapsed = Time.realtimeSinceStartup - started;
        if (stage == 0 && elapsed > 4)
        {
            view = Camera.main;
            if (!view) { Finish(false, "No main camera"); return; }
            start = view.transform.position;
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "trained_before.png"));
            keyboard = Keyboard.current;
            if (keyboard == null) { keyboard = InputSystem.AddDevice<Keyboard>(); ownedKeyboard = true; }
            stage = 1;
        }
        if (stage == 1)
        {
            InputSystem.QueueStateEvent(keyboard, elapsed < 4.2f ? new KeyboardState(Key.W) : new KeyboardState());
            if (elapsed > 5.5f)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                if (ownedKeyboard) InputSystem.RemoveDevice(keyboard);
                keyboard = null;
                ScreenCapture.CaptureScreenshot(Path.Combine(output, "trained_after.png"));
                stage = 2;
            }
        }
        if (stage == 2 && elapsed > 7)
        {
            var splat = FindFirstObjectByType<GaussianSplatRenderer>();
            float distance = Vector3.Distance(start, view.transform.position);
            bool pass = distance > 0.01f && splat && splat.HasValidRenderSetup && splat.m_Asset && splat.m_Asset.splatCount == expectedSplatCount
                && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
            Finish(pass, $"Unity={Application.unityVersion}; GPU={SystemInfo.graphicsDeviceName}; API={SystemInfo.graphicsDeviceType}; splats={splat?.m_Asset?.splatCount}; synthetic W via XR Simulator: {start:F4} -> {view.transform.position:F4}; distance={distance:F4}; validRenderSetup={splat?.HasValidRenderSetup}");
        }
    }

    void Finish(bool pass, string details)
    {
        verify = false;
        string result = (pass ? "PASS " : "FAIL ") + details;
        File.WriteAllText(Path.Combine(output, "trained_runtime.txt"), result);
        Debug.Log("[P0 Trained] " + result);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(pass ? 0 : 1);
#else
        Application.Quit(pass ? 0 : 1);
#endif
    }

    void OnDestroy() { if (ownedKeyboard && keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard); }
}
