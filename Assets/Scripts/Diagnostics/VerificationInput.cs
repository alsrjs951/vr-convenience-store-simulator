using System.Collections.Generic;
using System.Text;
using UnityEngine.InputSystem;

/// <summary>
/// Takes the person's own mouse and keyboard out of a verification (2026-11-13).
///
/// The checks drive the XR Device Simulator with a keyboard and a mouse they add themselves, but the simulator listens to
/// every mouse and keyboard there is. So whoever sat at the PC could not touch the mouse while a check ran: its motion
/// moved the simulated hand too (a product was read a second time by it twice on 2026-11-11), and all the checks could do
/// was count that motion afterwards (externalMouseMotion). For the length of a check the real devices are now switched
/// off in the Input System, and on again at its end. Only this program stops hearing them: the editor's own windows and
/// everything else on the PC do not get their mouse and keys through the Input System.
/// </summary>
public static class VerificationInput
{
    static readonly List<InputDevice> silenced = new List<InputDevice>();
    static readonly List<InputDevice> own = new List<InputDevice>();
    static bool listening;

    /// <summary>The real pointers and keyboards switched off for this check.</summary>
    public static IReadOnlyList<InputDevice> Silenced => silenced;

    /// <summary>Switches off every pointer and keyboard but the check's own, and any that turns up while it runs.</summary>
    public static void SilenceAllBut(params InputDevice[] keep)
    {
        own.Clear();
        own.AddRange(keep);
        foreach (var device in InputSystem.devices) Silence(device);
        if (listening) return;
        InputSystem.onDeviceChange += OnDeviceChange;
        listening = true;
    }

    /// <summary>The person's devices are theirs again. Safe to call twice.</summary>
    public static void Restore()
    {
        if (listening) InputSystem.onDeviceChange -= OnDeviceChange;
        listening = false;
        foreach (var device in silenced)
            if (device != null && device.added) InputSystem.EnableDevice(device);
        silenced.Clear();
        own.Clear();
    }

    public static string Describe()
    {
        var text = new StringBuilder();
        foreach (var device in silenced) text.Append(text.Length > 0 ? ", " : "").Append(device.displayName).Append(device.enabled ? " (STILL ON)" : "");
        return text.Length > 0 ? text.ToString() : "none found";
    }

    static void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        // A mouse plugged in, or back from sleep, in the middle of a check.
        if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected) Silence(device);
    }

    static void Silence(InputDevice device)
    {
        if (!(device is Pointer || device is Keyboard) || own.Contains(device) || silenced.Contains(device) || !device.enabled) return;
        InputSystem.DisableDevice(device);
        silenced.Add(device);
    }
}
