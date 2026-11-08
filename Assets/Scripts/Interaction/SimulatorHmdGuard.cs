using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Management;

/// <summary>
/// Sits on the XR Device Simulator prefab and turns it off when an XR runtime is up. Left on, the simulator removes
/// every other HMD device from the Input System, so head tracking would stop. The removal happens in the
/// SimulatedDeviceLifecycleManager's OnEnable, the earliest of the simulator's components, so this has to run before
/// that. In a player the headset device already exists by then, so running even slightly later loses it for good.
/// It asks for the active loader because XRSettings.isDeviceActive and the display's running flag are still false this
/// early, even with a working runtime.
/// </summary>
[DefaultExecutionOrder(XRInteractionUpdateOrder.k_SimulatedDeviceLifecycleManager - 1)]
public class SimulatorHmdGuard : MonoBehaviour
{
    void Awake()
    {
        var settings = XRGeneralSettings.Instance;
        var loader = settings != null && settings.Manager != null ? settings.Manager.activeLoader : null;
        if (loader == null) return;
        Debug.Log($"[Simulator Guard] {loader.name} is running; XR Device Simulator stays off.");
        // In a headset the desktop window rarely has focus. The game and its head and hand tracking must not stop for that.
        Application.runInBackground = true;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        gameObject.SetActive(false);
    }
}
