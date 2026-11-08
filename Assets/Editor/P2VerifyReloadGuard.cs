using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A verification run (-p2-verify, -p2-day2-verify, -p2-locomotion-verify, -p1-verify) is only valid if scripts stay
/// loaded from start to finish. If a script changes while the run is in Play mode, the editor recompiles and reloads the
/// domain in the middle of it: the test's coroutine is gone, game state is half reset, and the window would sit there
/// forever. This ends such a run at once with an error and exit code 3, so the reload is visible instead of a hang.
/// (2026-11-08: a check was edited while the Day 2 run was playing.) Entering Play mode reloads too, but isPlaying is
/// still false at that point, so a normal start is unaffected.
/// </summary>
[InitializeOnLoad]
static class P2VerifyReloadGuard
{
    static readonly string[] Flags = { "-p2-verify", "-p2-day2-verify", "-p2-locomotion-verify", "-p1-verify" };

    static P2VerifyReloadGuard()
    {
        if (!EditorApplication.isPlaying) return;
        var args = Environment.GetCommandLineArgs();
        foreach (var flag in Flags)
        {
            if (Array.IndexOf(args, flag) < 0) continue;
            Debug.LogError($"[P2 Verify] Scripts were reloaded during a verification run ({flag}); its results are no longer valid. Exiting with code 3.");
            EditorApplication.delayCall += () => EditorApplication.Exit(3);
            return;
        }
    }
}
