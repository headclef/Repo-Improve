using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Improve.Patches;

[HarmonyPatch]
internal static class HaulTrackerPatch
{
    /// <summary>
    /// Fold any haul earned since we last looked into the lifetime total, just before the game
    /// runs its end-of-scene logic. We bank on EVERY scene switch with no "are we in a level?"
    /// gate: when a level is completed the game has already advanced the current level to the
    /// post-level shop (RunManager.ChangeLevel / UpdateLevel set levelCurrent before calling
    /// OnSceneSwitch), so gating on RunIsLevel() — which reads the current level — would skip the
    /// very transition that ends a map. SaveData.BankRunHaul is idempotent against the cumulative
    /// run haul, so banking on shop / lobby transitions too is harmless: the delta is 0 when no
    /// new haul was earned. Fires on host (ChangeLevel) and clients (UpdateLevel via RPC) alike.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix() => SaveData.BankRunHaul();

    /// <summary>
    /// A saved run was loaded (Continue). Its restored haul was already banked by the session
    /// that saved it, so re-baseline rather than banking the whole loaded total a second time.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.LoadGame))]
    [HarmonyPostfix]
    private static void LoadGame_Postfix() => SaveData.MarkHaulBaselineStale();
}


[HarmonyPatch]
internal static class StatApplyPatch
{
    private static Coroutine? _watchdog;
    private static Coroutine? _deferredApply;

    /// <summary>
    /// When the player is added to a level, defer initial application and start the
    /// watchdog coroutine. Gated to actual levels: these hooks fire from PUN RPC handlers
    /// (PlayerAdd via AddToStatsManagerRPC, ReceiveSyncData) during the join/scene-sync
    /// handshake, so they must do NOTHING while the game is connecting or loading — the
    /// avatar re-derives everything from the dictionaries on the next level spawn anyway.
    /// The whole body is wrapped so a stray exception can never propagate back into the
    /// game's RPC dispatch and freeze networking (a permanent loading screen).
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        try
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
        catch (System.Exception ex)
        {
            Improve.Logger.LogWarning($"PlayerAdd hook skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// After a network sync completes, external data may have overwritten
    /// our local stat values. Immediately enforce our allocations. Runs from a PUN RPC
    /// handler, so it is level-gated and fully guarded (see PlayerAdd_Postfix).
    /// </summary>
    [HarmonyPatch(typeof(PunManager), nameof(PunManager.ReceiveSyncData))]
    [HarmonyPostfix]
    private static void ReceiveSyncData_Postfix(bool finalChunk)
    {
        try
        {
            if (!finalChunk) return;
            if (!SemiFunc.RunIsLevel()) return;

            // Sync may have overwritten our local values — reconcile immediately.
            Improve.Logger.LogDebug("Sync complete — reconciling Improve allocations...");
            SaveData.ApplyStats();
        }
        catch (System.Exception ex)
        {
            Improve.Logger.LogWarning($"ReceiveSyncData hook skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// When leaving a level, just stop our coroutines. We deliberately KEEP the per-run
    /// tracking (_appliedBase / _appliedDelta): the next level reconciles against it,
    /// which both prevents re-stacking our bonus AND folds in any shop purchase made
    /// between levels.
    ///
    /// We do NOT invalidate <see cref="StatEffectApplier"/> here: R.E.P.O. keeps the same
    /// PlayerController alive across truck↔level scene switches within a run, so its live
    /// fields still carry the client-side bonus. Wiping the applier's tracking while the
    /// avatar survives would make the next tick re-add the bonus onto a field that already
    /// has it — doubling Sprint/Stamina/Grab Range on every "leave and re-enter". The applier
    /// reconciles against the live field itself and re-baselines only when the avatar instance
    /// actually changes (a genuine fresh spawn), so no scene-switch reset is needed.
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
        StatEffectApplier.Invalidate();
        SaveData.ClearTracking();
        SaveData.MarkHaulBaselineStale();
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
    /// base stat values, then apply Improve allocations. The FIRST run this scene
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

        try
        {
            SaveData.ApplyStats();       // idempotent reconcile of the dictionaries
            StatEffectApplier.Apply();   // co-op client only: top the live components up to full
        }
        catch (System.Exception ex) { Improve.Logger.LogWarning($"Deferred apply skipped: {ex.Message}"); }
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

    /// <summary>
    /// Stops the watchdog. Also tears the bridge down: the watchdog is the only thing that
    /// drives it, so leaving the subscription up here would keep Improve listening on Photon
    /// outside a level — exactly the state that wedged the connect flow in 1.1.5.
    /// </summary>
    private static void StopWatchdog()
    {
        NetworkBridge.Teardown();
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
            try
            {
                SaveData.EnforceStats();
                StatEffectApplier.Apply(); // co-op client only: keep the live components at full
                NetworkBridge.Tick();      // opt-in: the four host-simulated stats
                SaveData.BankRunHaul(); // also bank in-level, so the final map isn't lost if its scene switch is missed
            }
            catch (System.Exception ex) { Improve.Logger.LogWarning($"Watchdog tick skipped: {ex.Message}"); }
            yield return new WaitForSeconds(0.5f);
        }

        NetworkBridge.Teardown();
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
