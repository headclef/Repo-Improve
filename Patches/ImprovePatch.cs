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
        if (PlayerAvatar.instance == null) return;
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
    private static Coroutine? _deferredApply;

    /// <summary>
    /// When the player is added to a level, defer initial application
    /// and start the watchdog coroutine.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        if (!SemiFunc.RunIsLevel()) return;
        if (PlayerAvatar.instance == null) return;
        if (_steamID != PlayerAvatar.instance.steamID) return;

        // ApplyStats is an idempotent reconcile, so repeated PlayerAdd / sync triggers
        // never double-apply. ScheduleApply just debounces rapid back-to-back triggers.
        Improve.Logger.LogDebug("Player data added — scheduling Improve stat application...");
        ScheduleApply();
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

        // Sync may have overwritten our local values — reconcile immediately.
        Improve.Logger.LogDebug("Sync complete — reconciling Improve allocations...");
        SaveData.ApplyStats();
    }

    /// <summary>
    /// When leaving a level, just stop our coroutines. We deliberately KEEP the per-run
    /// tracking (_appliedBase / _appliedDelta): the next level reconciles against it,
    /// which both prevents re-stacking our bonus AND folds in any shop purchase made
    /// between levels. We do NOT strip the bonus here — values stay consistent in the
    /// truck/shop, and the next level's reconcile derives the correct base.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix()
    {
        if (!SemiFunc.RunIsLevel()) return;
        StopWatchdog();
        StopDeferredApply();
    }

    /// <summary>
    /// The run was reset (team wipe / new game). The game wipes the upgrade
    /// dictionaries itself, so stand down: stop our coroutines and forget the tracked
    /// base. We do NOT restore here — writing our recorded base back over the game's
    /// reset would carry the old upgrades into the fresh run.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.ResetProgress))]
    [HarmonyPostfix]
    private static void ResetProgress_Postfix()
    {
        StopWatchdog();
        StopDeferredApply();
        SaveData.ClearTracking();
    }

    /// <summary>
    /// Schedule a single deferred apply, cancelling any still-pending one so that
    /// rapid back-to-back triggers (PlayerAdd + post-sync) collapse into one run.
    /// </summary>
    private static void ScheduleApply()
    {
        StopDeferredApply();
        _deferredApply = Improve.Instance.StartCoroutine(DeferredApply());
    }

    private static void StopDeferredApply()
    {
        if (_deferredApply != null)
        {
            Improve.Instance.StopCoroutine(_deferredApply);
            _deferredApply = null;
        }
    }

    /// <summary>
    /// Wait a few frames for the game, host sync, and other mods to finish setting
    /// base stat values, then apply Improve allocations. The FIRST run this level
    /// establishes the base via ApplyStats; any later run enforces (idempotent) so
    /// allocations are never stacked on top of an already-boosted value.
    /// </summary>
    private static IEnumerator DeferredApply()
    {
        yield return null;
        yield return null;
        yield return null; // 3 frames — give host sync and stat-sharing mods time

        _deferredApply = null;
        if (!SemiFunc.RunIsLevel()) yield break;

        SaveData.ApplyStats();  // idempotent reconcile — correct whether first apply or re-apply
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


/// <summary>
/// Keeps Improve's bonus out of the game's save file. The game serializes the
/// playerUpgrade* dictionaries verbatim (StatsManager.SaveGame → ES3), and we write our
/// bonus directly into those dicts — so without this the bonus gets baked into the .es3
/// save and, on the next launch (in-memory tracking gone), re-applied on top of the
/// already-inflated value, compounding every save/quit/relaunch. We strip the bonus just
/// before serialization and restore it immediately after. SaveGame is synchronous and the
/// single serialization entry point, so this fully covers manual saves and autosaves.
/// </summary>
[HarmonyPatch]
internal static class SaveLeakGuardPatch
{
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SaveGame))]
    [HarmonyPrefix]
    private static void SaveGame_Prefix() => SaveData.StripBonusForSave();

    // Finalizer (not postfix) so the live bonus is restored even if the save throws.
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SaveGame))]
    [HarmonyFinalizer]
    private static void SaveGame_Finalizer() => SaveData.RestoreBonusAfterSave();
}
