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

    /// <summary>True in scenes where no gameplay stats exist (main menu, lobby menu).
    /// Everywhere else — levels AND the truck/shop — stats must be applied: the host
    /// re-syncs all dictionaries on EVERY scene switch (SemiFunc.OnSceneSwitch →
    /// StatSyncAll), which wipes a client's local bonus, and the player components
    /// re-derive their live values from the dictionaries at every spawn, truck included
    /// (PlayerHealth.Fetch even clamps and persists health against the un-boosted max).
    /// Gating the reconcile to RunIsLevel() left clients at vanilla stats in the truck
    /// and shop — and bleeding max-health there — while the host, whose dictionaries are
    /// never wiped, kept its bonus everywhere.</summary>
    private static bool InMenuScene() => SemiFunc.MenuLevel() || SemiFunc.RunIsLobbyMenu();

    /// <summary>
    /// When the player is added to a scene, defer initial application
    /// and start the watchdog coroutine.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        if (InMenuScene()) return;
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
        if (InMenuScene()) return;

        // Sync may have overwritten our local values — reconcile immediately.
        Improve.Logger.LogDebug("Sync complete — reconciling Improve allocations...");
        SaveData.ApplyStats();
    }

    /// <summary>
    /// On any scene switch, just stop our coroutines — PlayerAdd restarts them in the next
    /// scene (the avatar is recreated every scene). We deliberately KEEP the per-run
    /// tracking (_appliedBase / _appliedDelta): the next scene reconciles against it,
    /// which both prevents re-stacking our bonus AND folds in any shop purchase made
    /// between levels. The network bridge drops its per-instance tracking too — the
    /// components it applied to die with the scene.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix()
    {
        StopWatchdog();
        StopDeferredApply();
        NetworkBridge.OnSceneSwitch();
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
        if (InMenuScene()) yield break;

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

        while (!InMenuScene())
        {
            SaveData.EnforceStats();
            SaveData.BankRunHaul(); // also bank in-level, so the final map isn't lost if its scene switch is missed
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
