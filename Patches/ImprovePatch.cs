using HarmonyLib;

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
    /// This prevents getting credit for haul earned before you joined (MP).
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix()
    {
        if (!SemiFunc.RunIsLevel()) return;

        // Remove Improve allocations so the game carries clean stats
        // to the next level/shop — prevents compounding across levels
        SaveData.RemoveStats();

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
    /// <summary>
    /// Apply stats immediately during the player setup window.
    /// Proper Upgrades has already captured the clean base (Priority.First),
    /// so ApplyStats reads from there and adds Improve allocations on top.
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayerAdd))]
    [HarmonyPostfix]
    private static void PlayerAdd_Postfix(string _steamID)
    {
        if (!SemiFunc.RunIsLevel())
            return;
        if (_steamID != PlayerAvatar.instance.steamID)
            return;

        Improve.Logger.LogDebug("Player data added — applying stats...");
        SaveData.ApplyStats();
    }

    /// <summary>
    /// Re-apply after network sync to persist through data overwrites.
    /// Proper Upgrades base is constant per level, so this is always idempotent.
    /// </summary>
    [HarmonyPatch(typeof(PunManager), nameof(PunManager.ReceiveSyncData))]
    [HarmonyPostfix]
    private static void ReceiveSyncData_Postfix(bool finalChunk)
    {
        if (!finalChunk) return;
        Improve.Logger.LogDebug("Sync complete — re-applying stats...");
        SaveData.ApplyStats();
    }
}
