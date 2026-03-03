using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Improve.Patches;

[HarmonyPatch]
internal static class HaulTrackerPatch
{
    private static int _haulAtLevelStart;

    /// <summary>
    /// Snapshot the run haul at the start of each level.
    /// This is our baseline — we only count haul earned DURING this level.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        if (_steamID != PlayerAvatar.instance.steamID) return;

        _haulAtLevelStart = StatsManager.instance.GetRunStatTotalHaul();
        Improve.Logger.LogDebug($"Level start baseline: {_haulAtLevelStart}");
    }

    /// <summary>
    /// Capture haul delta when leaving a level.
    /// Only the haul earned during THIS level is added to lifetime total.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix()
    {
        if (!SemiFunc.RunIsLevel()) return;

        int currentRunHaul = StatsManager.instance.GetRunStatTotalHaul();
        int delta = currentRunHaul - _haulAtLevelStart;

        if (delta <= 0) return;

        SaveData.LifetimeHaul.Value += delta;
        Improve.Logger.LogInfo($"Level haul captured: +{delta} (lifetime: {SaveData.LifetimeHaul.Value})");
    }
}


[HarmonyPatch]
internal static class StatApplyPatch
{
    private static Coroutine? _watchdog;

    /// <summary>
    /// When the player is added to a level, defer initial application
    /// and start the watchdog coroutine.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        if (!SemiFunc.RunIsLevel()) return;
        if (_steamID != PlayerAvatar.instance.steamID) return;

        SaveData._appliedBase.Clear();
        Improve.Logger.LogDebug("Player data added — deferring Improve stat application...");
        Improve.Instance.StartCoroutine(DeferredApply());
        StartWatchdog();
    }

    /// <summary>
    /// After a network sync completes, external data may have overwritten
    /// our local stat values. Immediately enforce our allocations.
    /// </summary>
    [HarmonyPatch(typeof(PunManager), nameof(PunManager.ReceiveSyncData))]
    [HarmonyPostfix]
    private static void ReceiveSyncData_Postfix(bool finalChunk)
    {
        if (!finalChunk) return;
        if (!SemiFunc.RunIsLevel()) return;

        if (SaveData._appliedBase.Count > 0)
        {
            // Our allocations were applied before sync — enforce them.
            Improve.Logger.LogDebug("Sync complete — enforcing Improve allocations...");
            SaveData.EnforceStats();
        }
        else
        {
            // Haven't applied yet, defer.
            Improve.Logger.LogDebug("Sync complete, not yet applied — deferring...");
            Improve.Instance.StartCoroutine(DeferredApply());
        }
    }

    /// <summary>
    /// Stop the watchdog when leaving a level.
    /// No need to "remove" stats — writes are local-only and PlayerAdd
    /// resets everything to 0 on next level anyway.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix()
    {
        if (!SemiFunc.RunIsLevel()) return;
        StopWatchdog();
        SaveData._appliedBase.Clear();
    }

    /// <summary>
    /// Wait a few frames for the game, host sync, and other mods to finish
    /// setting base stat values, then layer Improve allocations on top.
    /// </summary>
    private static IEnumerator DeferredApply()
    {
        yield return null;
        yield return null;
        yield return null; // 3 frames — give host sync and stat-sharing mods time

        if (!SemiFunc.RunIsLevel()) yield break;
        SaveData.ApplyStats();
    }

    /// <summary>
    /// Starts a watchdog coroutine that periodically checks whether external
    /// forces (host sync, stat-sharing mods, etc.) have overwritten our local
    /// stat allocations. If so, it re-derives the base and re-applies.
    /// </summary>
    private static void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = Improve.Instance.StartCoroutine(WatchdogLoop());
    }

    private static void StopWatchdog()
    {
        if (_watchdog != null)
        {
            Improve.Instance.StopCoroutine(_watchdog);
            _watchdog = null;
        }
    }

    private static IEnumerator WatchdogLoop()
    {
        // Wait for initial apply to settle
        yield return new WaitForSeconds(1f);

        while (SemiFunc.RunIsLevel())
        {
            SaveData.EnforceStats();
            yield return new WaitForSeconds(0.5f);
        }

        _watchdog = null;
    }
}
