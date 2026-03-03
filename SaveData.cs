using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    /// <summary>Map stat name → current Improve allocation for that stat.</summary>
    internal static int GetAllocationForStat(string statName) => statName switch
    {
        "playerUpgradeHealth" => AllocHealth.Value,
        "playerUpgradeSpeed" => AllocSpeed.Value,
        "playerUpgradeStamina" => AllocStamina.Value,
        "playerUpgradeExtraJump" => AllocExtraJump.Value,
        "playerUpgradeRange" => AllocGrabRange.Value,
        "playerUpgradeStrength" => AllocGrabStrength.Value,
        "playerUpgradeThrow" => AllocGrabThrow.Value,
        "playerUpgradeLaunch" => AllocTumbleLaunch.Value,
        "playerUpgradeTumbleClimb" => AllocTumbleClimb.Value,
        "playerUpgradeTumbleWings" => AllocTumbleWings.Value,
        "playerUpgradeCrouchRest" => AllocCrouchRest.Value,
        "playerUpgradeMapPlayerCount" => AllocMapPlayerCount.Value,
        "playerUpgradeDeathHeadBattery" => AllocDeathHeadBattery.Value,
        _ => 0
    };

    internal static readonly string[] AllStatNames =
    {
        "playerUpgradeHealth", "playerUpgradeSpeed", "playerUpgradeStamina",
        "playerUpgradeExtraJump", "playerUpgradeRange", "playerUpgradeStrength",
        "playerUpgradeThrow", "playerUpgradeLaunch", "playerUpgradeTumbleClimb",
        "playerUpgradeTumbleWings", "playerUpgradeCrouchRest",
        "playerUpgradeMapPlayerCount", "playerUpgradeDeathHeadBattery"
    };

    // ── Cached reflection handles for StatsManager dictionaries ──
    private static readonly Dictionary<string, FieldInfo> _dictFields = new();

    private static Dictionary<string, int>? GetStatDict(string statName)
    {
        if (!_dictFields.TryGetValue(statName, out var fi))
        {
            fi = typeof(StatsManager).GetField(statName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _dictFields[statName] = fi!;
        }

        return fi?.GetValue(StatsManager.instance) as Dictionary<string, int>;
    }

    /// <summary>
    /// Read a single stat value directly from StatsManager dictionaries (no network).
    /// </summary>
    private static int ReadStatLocal(string statName, string steamId)
    {
        var dict = GetStatDict(statName);
        if (dict != null && dict.TryGetValue(steamId, out int val))
            return val;
        return 0;
    }

    /// <summary>
    /// Write a single stat value directly into StatsManager dictionaries.
    /// LOCAL-ONLY — no network broadcast. This avoids the host (or its mods)
    /// seeing our value and overwriting it back.
    /// </summary>
    private static void WriteStatLocal(string statName, string steamId, int value)
    {
        var dict = GetStatDict(statName);
        if (dict != null)
            dict[steamId] = value;
    }

    // ── Tracking applied base values ──
    // After applying, _appliedBase[stat] = the base (pre-Improve) value we saw.
    // This lets the watchdog distinguish "external wipe" from "shop upgrade".
    internal static readonly Dictionary<string, int> _appliedBase = new();

    /// <summary>
    /// Apply Improve allocations on top of whatever the game currently reports.
    /// Writes directly to StatsManager dictionaries (local-only, no network).
    /// </summary>
    public static void ApplyStats()
    {
        if (PlayerController.instance == null) return;
        if (StatsManager.instance == null) return;

        string steamId = PlayerController.instance.playerSteamID;

        _appliedBase.Clear();

        foreach (string stat in AllStatNames)
        {
            int alloc = GetAllocationForStat(stat);
            int cur = ReadStatLocal(stat, steamId);

            // Record the base before we add
            _appliedBase[stat] = cur;

            if (alloc <= 0) continue;
            WriteStatLocal(stat, steamId, cur + alloc);
        }

        Improve.Logger.LogInfo($"Improve stats applied locally (Level {CurrentLevel()}, {TotalSpent()} spent, {AvailablePoints()} available).");
    }

    /// <summary>
    /// Check if any external force (host sync, stat-sharing mod) has overwritten
    /// our local stat values. If the current value for any allocated stat differs
    /// from (base + alloc), an external change happened — re-derive base and re-apply.
    /// Returns true if a re-apply was needed.
    /// </summary>
    public static bool EnforceStats()
    {
        if (PlayerController.instance == null) return false;
        if (StatsManager.instance == null) return false;
        if (_appliedBase.Count == 0) return false;

        string steamId = PlayerController.instance.playerSteamID;
        bool dirty = false;

        foreach (string stat in AllStatNames)
        {
            int alloc = GetAllocationForStat(stat);
            int cur = ReadStatLocal(stat, steamId);
            int expectedBase = _appliedBase.TryGetValue(stat, out int b) ? b : 0;
            int expected = expectedBase + alloc;

            if (cur == expected) continue;

            // Something changed this stat externally.
            // Derive the new base: whatever the current value is minus our alloc
            // (if our alloc is still present) or just the raw current value.
            int newBase;
            if (cur < expected)
            {
                // Stat decreased → our allocation was (partially or fully) wiped.
                // Treat current value as the new base.
                newBase = cur;
            }
            else
            {
                // Stat increased beyond expected → something added on top (shop buy).
                // New base = current minus our allocation.
                newBase = cur - alloc;
            }

            _appliedBase[stat] = newBase;
            dirty = true;

            if (alloc > 0)
            {
                int newTotal = newBase + alloc;
                WriteStatLocal(stat, steamId, newTotal);
            }
        }

        if (dirty)
        {
            Improve.Logger.LogDebug("Watchdog: detected external stat change — re-applied allocations locally.");
        }

        return dirty;
    }
}
