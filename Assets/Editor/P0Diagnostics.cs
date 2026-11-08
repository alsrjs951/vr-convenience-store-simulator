using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using GaussianSplatting.Runtime;

/// <summary>Play mode evidence for P0; does not move the camera or simulate input.</summary>
public static class P0Diagnostics
{
    static double movementStart;
    static Vector3 startPosition;
    static Keyboard testKeyboard;
    static Camera testCamera;

    [MenuItem("Tools/P0/Test Simulator Movement %#F10")]
    static void TestMovement()
    {
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || Keyboard.current == null || Camera.main == null)
        {
            Debug.LogWarning("[P0] Run the simulator in unpaused Play mode first.");
            return;
        }
        if (testKeyboard != null) return;
        testKeyboard = InputSystem.AddDevice<Keyboard>();
        testCamera = Camera.main;
        startPosition = testCamera.transform.position;
        movementStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += MovementTick;
    }

    static void MovementTick()
    {
        if (!EditorApplication.isPlaying || testCamera == null)
        {
            EndMovementTest();
            return;
        }
        var elapsed = EditorApplication.timeSinceStartup - movementStart;
        // Exercise the real XRI input actions, without changing any Transform.
        InputSystem.QueueStateEvent(testKeyboard, elapsed < 0.5 ? new KeyboardState(Key.D) : new KeyboardState());
        if (elapsed < 0.8) return;
        var endPosition = testCamera.transform.position;
        var distance = Vector3.Distance(startPosition, endPosition);
        var result = $"[P0 Movement] {(distance > 0.01f ? "PASS" : "FAIL")} synthetic Keyboard D -> XR Device Simulator -> camera: {startPosition:F4} -> {endPosition:F4}; distance={distance:F4}m";
        EndMovementTest();
        Directory.CreateDirectory("docs/p0/evidence");
        File.WriteAllText("docs/p0/evidence/simulator_movement.txt", result);
        Debug.Log(result);
        Capture();
    }

    static void EndMovementTest()
    {
        EditorApplication.update -= MovementTick;
        if (testKeyboard != null && testKeyboard.added) InputSystem.RemoveDevice(testKeyboard);
        testKeyboard = null;
        testCamera = null;
    }

    [MenuItem("Tools/P0/Capture Play Evidence %#F9")]
    static void Capture()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[P0] Enter Play mode before capturing evidence.");
            return;
        }
        var camera = Camera.main;
        var splat = Object.FindFirstObjectByType<GaussianSplatRenderer>();
        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = "docs/p0/evidence/" + stamp;
        Directory.CreateDirectory("docs/p0/evidence");
        var report = $"Unity: {Application.unityVersion}\nGPU: {SystemInfo.graphicsDeviceName}\nAPI: {SystemInfo.graphicsDeviceType}\n" +
            $"Keyboard: {Keyboard.current != null}\nMouse: {Mouse.current != null}\n" +
            $"Camera: {camera?.name}\nPosition: {camera?.transform.position.ToString("F4")}\nRotation: {camera?.transform.eulerAngles.ToString("F4")}\n" +
            $"Splat: {splat?.name}\nSplat position: {splat?.transform.position}\nCount: {splat?.m_Asset?.splatCount}\n";
        if (camera != null && splat != null)
            report += $"Splat viewport: {camera.WorldToViewportPoint(splat.transform.position)}\n";
        File.WriteAllText(path + ".txt", report);
        ScreenCapture.CaptureScreenshot(Path.GetFullPath(path + ".png"));
        Debug.Log("[P0] " + path + "\n" + report);
    }
}
