using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

#pragma warning disable CS8618

namespace Improve;

/// <summary>
/// Persistent save data stored in a separate config file.
/// Tracks lifetime haul and per-stat point allocations.
/// </summary>
public static class SaveData
{
    // ── Haul ──
    public static ConfigEntry<int> LifetimeHaul;

    // ── Stat Allocations (points spent per stat) ──
    public static ConfigEntry<int> AllocHealth;
    public static ConfigEntry<int> AllocSpeed;
    public static ConfigEntry<int> AllocStamina;
    public static ConfigEntry<int> AllocExtraJump;
    public static ConfigEntry<int> AllocGrabRange;
    public static ConfigEntry<int> AllocGrabStrength;
    public static ConfigEntry<int> AllocGrabThrow;
    public static ConfigEntry<int> AllocTumbleLaunch;
    public static ConfigEntry<int> AllocTumbleClimb;
    public static ConfigEntry<int> AllocTumbleWings;
    public static ConfigEntry<int> AllocCrouchRest;
    public static ConfigEntry<int> AllocMapPlayerCount;
    public static ConfigEntry<int> AllocDeathHeadBattery;

    internal static void Initialize()
    {
        ConfigFile save = new(
            Path.Combine(Application.persistentDataPath, "REPOModData/Improve/save.cfg"),
            false);

        LifetimeHaul = save.Bind("Progress", "LifetimeHaul", 0,
            "Total haul accumulated across all sessions.");

        // Skills
        AllocHealth = save.Bind("Skills", "Health", 0, "Points spent on Health.");
        AllocSpeed = save.Bind("Skills", "SprintSpeed", 0, "Points spent on Sprint Speed.");
        AllocStamina = save.Bind("Skills", "Stamina", 0, "Points spent on Stamina.");
        AllocExtraJump = save.Bind("Skills", "ExtraJump", 0, "Points spent on Extra Jump.");
        AllocGrabRange = save.Bind("Skills", "GrabRange", 0, "Points spent on Grab Range.");
        AllocGrabStrength = save.Bind("Skills", "GrabStrength", 0, "Points spent on Grab Strength.");
        AllocGrabThrow = save.Bind("Skills", "GrabThrow", 0, "Points spent on Throw.");
        AllocTumbleLaunch = save.Bind("Skills", "TumbleLaunch", 0, "Points spent on Tumble Launch.");
        AllocTumbleClimb = save.Bind("Skills", "TumbleClimb", 0, "Points spent on Tumble Climb.");
        AllocTumbleWings = save.Bind("Skills", "TumbleWings", 0, "Points spent on Tumble Wings.");
        AllocCrouchRest = save.Bind("Skills", "CrouchRest", 0, "Points spent on Crouch Rest.");
        AllocMapPlayerCount = save.Bind("Skills", "MapPlayerCount", 0, "Points spent on Map Player Count.");
        AllocDeathHeadBattery = save.Bind("Skills", "DeathHeadBattery", 0, "Points spent on Death Head Battery.");
    }

    // ── Level Calculations ──

    /// <summary>
    /// Total level (stat points earned) from lifetime haul using doubling cost formula.
    /// Cost for level N = baseCost × 2^(N-1) × difficulty
    /// Total cost for N levels = baseCost × (2^N - 1) × difficulty
    /// So: N = floor(log2(lifetimeHaul / (baseCost × difficulty) + 1))
    /// </summary>
    public static int CurrentLevel()
    {
        float baseCost = Improve.BaseCost.Value;
        float difficulty = Improve.DifficultyMultiplier.Value;
        float effectiveCost = baseCost * difficulty;

        if (effectiveCost <= 0 || LifetimeHaul.Value <= 0)
            return 0;

        int level = (int)Math.Floor(Math.Log((double)LifetimeHaul.Value / effectiveCost + 1, 2));
        return Math.Max(level, 0);
    }

    /// <summary>Total haul needed to reach the next level.</summary>
    public static int TotalHaulForLevel(int level)
    {
        if (level <= 0) return 0;
        float baseCost = Improve.BaseCost.Value;
        float difficulty = Improve.DifficultyMultiplier.Value;
        // Total cost for 'level' levels = baseCost × (2^level - 1) × difficulty
        return (int)(baseCost * (Math.Pow(2, level) - 1) * difficulty);
    }

    /// <summary>Haul still needed for the next level.</summary>
    public static int HaulNeededForNextLevel()
    {
        int nextLevelTotal = TotalHaulForLevel(CurrentLevel() + 1);
        return Math.Max(nextLevelTotal - LifetimeHaul.Value, 0);
    }

    /// <summary>Total stat points spent across all skills.</summary>
    public static int TotalSpent()
    {
        return AllocHealth.Value + AllocSpeed.Value + AllocStamina.Value +
               AllocExtraJump.Value + AllocGrabRange.Value + AllocGrabStrength.Value +
               AllocGrabThrow.Value + AllocTumbleLaunch.Value + AllocTumbleClimb.Value +
               AllocTumbleWings.Value + AllocCrouchRest.Value + AllocMapPlayerCount.Value +
               AllocDeathHeadBattery.Value;
    }

    /// <summary>Available unspent stat points. Can be negative if overspent (difficulty increased).</summary>
    public static int AvailablePoints()
    {
        return CurrentLevel() - TotalSpent();
    }

    /// <summary>True if more points are spent than the current level allows (difficulty was increased).</summary>
    public static bool IsOverspent()
    {
        return AvailablePoints() < 0;
    }

    // ── Resets ──

    /// <summary>Reset stat allocations only — keep haul and level.</summary>
    public static void ResetStats()
    {
        AllocHealth.Value = 0;
        AllocSpeed.Value = 0;
        AllocStamina.Value = 0;
        AllocExtraJump.Value = 0;
        AllocGrabRange.Value = 0;
        AllocGrabStrength.Value = 0;
        AllocGrabThrow.Value = 0;
        AllocTumbleLaunch.Value = 0;
        AllocTumbleClimb.Value = 0;
        AllocTumbleWings.Value = 0;
        AllocCrouchRest.Value = 0;
        AllocMapPlayerCount.Value = 0;
        AllocDeathHeadBattery.Value = 0;
        Improve.Logger.LogInfo("Stat allocations reset. Points reclaimed.");
    }

    /// <summary>Full reset — wipe haul, level, and all allocations.</summary>
    public static void ResetAll()
    {
        LifetimeHaul.Value = 0;
        ResetStats();
        Improve.Logger.LogInfo("Full reset — haul and stats wiped.");
    }

    // ── Stat Application ──

    /// <summary>Apply all allocated stats via PunManager.UpdateStat.</summary>
    public static void ApplyStats(bool force = false)
    {
        if (PlayerController.instance == null) return;

        string steamId = PlayerController.instance.playerSteamID;

        if (!force)
        {
            var upgrades = StatsManager.instance.FetchPlayerUpgrades(steamId);
            if (upgrades != null && upgrades.Values.GetEnumerator().MoveNext())
            {
                // Check if any values are non-zero
                bool hasValues = false;
                foreach (var v in upgrades.Values) { if (v != 0) { hasValues = true; break; } }
                if (hasValues) return;
            }
        }

        Improve.Logger.LogDebug("Applying Improve stats...");

        PunManager.instance.UpdateStat("playerUpgradeHealth", steamId, AllocHealth.Value);
        PunManager.instance.UpdateStat("playerUpgradeSpeed", steamId, AllocSpeed.Value);
        PunManager.instance.UpdateStat("playerUpgradeStamina", steamId, AllocStamina.Value);
        PunManager.instance.UpdateStat("playerUpgradeExtraJump", steamId, AllocExtraJump.Value);
        PunManager.instance.UpdateStat("playerUpgradeRange", steamId, AllocGrabRange.Value);
        PunManager.instance.UpdateStat("playerUpgradeStrength", steamId, AllocGrabStrength.Value);
        PunManager.instance.UpdateStat("playerUpgradeThrow", steamId, AllocGrabThrow.Value);
        PunManager.instance.UpdateStat("playerUpgradeLaunch", steamId, AllocTumbleLaunch.Value);
        PunManager.instance.UpdateStat("playerUpgradeTumbleClimb", steamId, AllocTumbleClimb.Value);
        PunManager.instance.UpdateStat("playerUpgradeTumbleWings", steamId, AllocTumbleWings.Value);
        PunManager.instance.UpdateStat("playerUpgradeCrouchRest", steamId, AllocCrouchRest.Value);
        PunManager.instance.UpdateStat("playerUpgradeMapPlayerCount", steamId, AllocMapPlayerCount.Value);
        PunManager.instance.UpdateStat("playerUpgradeDeathHeadBattery", steamId, AllocDeathHeadBattery.Value);

        Improve.Logger.LogInfo($"Improve stats applied (Level {CurrentLevel()}, {TotalSpent()} spent, {AvailablePoints()} available).");
    }
}
