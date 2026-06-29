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

    // ── Run-Haul Banking ──
    // _lastSeenRunHaul = the run's cumulative haul (in the game's K run-stat units) already folded
    //                    into LifetimeHaul this run.
    // _haulBaselined   = whether we've taken a baseline for the current run yet. Until we have, the
    //                    first value we see is treated as ALREADY banked — so continuing a saved
    //                    run, or joining a multiplayer run mid-way, never re-banks the haul earned
    //                    before we got here (only growth from here on counts).
    private static int _lastSeenRunHaul;
    private static bool _haulBaselined;

    /// <summary>
    /// Fold any newly-earned run haul into the lifetime total. The run's <c>totalHaul</c> is
    /// cumulative and only resets (to 0) when a new run starts, so we bank the positive growth
    /// since we last looked and advance our marker. Idempotent and self-correcting, so it is safe
    /// to call on every scene switch AND on a periodic tick, on host and client alike (both keep
    /// <c>runStats["totalHaul"]</c> in sync):
    ///   • not yet baselined → adopt the current value as the baseline (assume already banked)
    ///   • cur &gt; marker    → new haul earned → bank the difference ×1000 (the run stat is in K,
    ///                          but LifetimeHaul/baseCost are raw currency) and advance the marker
    ///   • cur &lt; marker    → run reset / different run loaded → re-baseline, never bank negative
    ///
    /// This replaces the old "baseline at level start, capture on leave" logic, which gated on
    /// <c>RunIsLevel()</c> at scene-switch time — but by then the game has already advanced the
    /// current level to the post-level shop, so the gate read false and each completed map's haul
    /// was skipped (captured only a cycle late, and the final map dropped entirely).
    /// </summary>
    public static void BankRunHaul()
    {
        if (StatsManager.instance == null) return;

        int cur = StatsManager.instance.GetRunStatTotalHaul();

        if (!_haulBaselined)
        {
            _lastSeenRunHaul = cur;
            _haulBaselined = true;
            return;
        }

        if (cur > _lastSeenRunHaul)
        {
            // GetRunStatTotalHaul is in thousands (the game banks item value /1000), but LifetimeHaul
            // and the level thresholds (baseCost) are in raw currency — so scale the K delta back up
            // by 1000. A 510K map (run-stat +510) banks +510,000, i.e. 15,000,000 -> 15,510,000.
            int delta = (cur - _lastSeenRunHaul) * 1000;
            LifetimeHaul.Value += delta;
            _lastSeenRunHaul = cur;
            Improve.Logger.LogInfo($"Haul banked: +{delta} (lifetime: {LifetimeHaul.Value}, level {CurrentLevel()})");
        }
        else if (cur < _lastSeenRunHaul)
        {
            _lastSeenRunHaul = cur; // run reset / different run loaded — re-baseline, don't bank negative
        }
    }

    /// <summary>
    /// Force the next <see cref="BankRunHaul"/> to re-take its baseline. Called when a run is reset
    /// or a saved run is loaded, so we never re-bank haul a previous session already counted.
    /// </summary>
    public static void MarkHaulBaselineStale() => _haulBaselined = false;

    // ── Level Calculations ──

    /// <summary>
    /// Current level (stat points earned) from lifetime haul.
    /// Each level N is a single haul threshold: haulForLevel(N) = baseCost × difficulty × N².
    /// Your level is the highest N whose threshold the haul has reached, so
    /// level = floor(sqrt(lifetimeHaul / (baseCost × difficulty))).
    /// </summary>
    public static int CurrentLevel()
    {
        float baseCost = Improve.BaseCost.Value;
        float difficulty = Improve.DifficultyMultiplier.Value;
        float effectiveCost = baseCost * difficulty;

        if (effectiveCost <= 0 || LifetimeHaul.Value <= 0)
            return 0;

        int level = (int)Math.Floor(Math.Sqrt(LifetimeHaul.Value / (double)effectiveCost));
        return Math.Max(level, 0);
    }

    /// <summary>
    /// Lifetime haul required to BE at the given level — a single threshold
    /// (baseCost × difficulty × level²), NOT a cumulative sum of every level below it.
    /// </summary>
    public static int TotalHaulForLevel(int level)
    {
        if (level <= 0) return 0;
        float baseCost = Improve.BaseCost.Value;
        float difficulty = Improve.DifficultyMultiplier.Value;
        double threshold = (double)baseCost * difficulty * level * level;
        return (int)Math.Min(threshold, int.MaxValue);
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

    // ── Tracking what we applied (persists across levels within a run) ──
    // _appliedBase[stat]  = the true (pre-Improve) base we derived for that stat.
    // _appliedDelta[stat] = the allocation we last wrote on top of that base.
    // Together they reconstruct the exact value we last wrote (base + delta), which
    // lets ApplyStats tell apart "untouched", "shop added on top", and "externally
    // wiped" — so our bonus is never stacked. Cleared only on a run reset.
    internal static readonly Dictionary<string, int> _appliedBase = new();
    internal static readonly Dictionary<string, int> _appliedDelta = new();

    /// <summary>
    /// Reconcile our allocations with the live stat values (local-only writes). For
    /// each stat we derive the true base from the value we last wrote:
    ///   • cur == lastWritten → base unchanged (already applied — never stack)
    ///   • cur  &gt; lastWritten → something ADDED on top (e.g. a shop purchase between
    ///                          levels) → fold the difference into the base
    ///   • cur  &lt; lastWritten → something WIPED it (host sync, or the game resetting
    ///                          upgrades to base on death) → treat cur as the new base
    /// then write base + currentAllocation. Idempotent and self-correcting, so it can
    /// run every level and repeatedly via the watchdog without ever double-adding.
    /// </summary>
    public static void ApplyStats()
    {
        if (PlayerController.instance == null) return;
        if (StatsManager.instance == null) return;

        string steamId = PlayerController.instance.playerSteamID;

        foreach (string stat in AllStatNames)
        {
            int alloc = GetAllocationForStat(stat);
            int cur = ReadStatLocal(stat, steamId);

            int baseVal;
            if (_appliedBase.TryGetValue(stat, out int prevBase) &&
                _appliedDelta.TryGetValue(stat, out int prevDelta))
            {
                int lastWritten = prevBase + prevDelta;
                if (cur > lastWritten)
                    baseVal = prevBase + (cur - lastWritten);  // external added (shop)
                else if (cur < lastWritten)
                    baseVal = cur;                             // external wiped (host/death)
                else
                    baseVal = prevBase;                        // intact — no change
            }
            else
            {
                baseVal = cur;  // first reconcile this run — current value is the base
            }

            _appliedBase[stat] = baseVal;
            _appliedDelta[stat] = alloc;

            WriteStatLocal(stat, steamId, baseVal + alloc);
        }

        Improve.Logger.LogDebug($"Improve stats reconciled (Level {CurrentLevel()}, {TotalSpent()} spent, {AvailablePoints()} available).");
    }

    /// <summary>
    /// Back-compat alias — the reconcile in <see cref="ApplyStats"/> already handles
    /// external changes (host sync / shop / reset), so "enforcing" is just reconciling.
    /// </summary>
    public static void EnforceStats() => ApplyStats();

    // ── Save-file leak protection ──
    // The game serializes the playerUpgrade* dictionaries verbatim to the .es3 save
    // (none of them are in StatsManager.doNotSaveTheseDictionaries). Because we write
    // our bonus straight into those dicts, a naive save would bake "base + alloc" into
    // the file. Our per-run tracking (_appliedBase/_appliedDelta) is in-memory only, so
    // after a relaunch the inflated value is mistaken for the true base and our bonus is
    // stacked again — compounding every save/quit/relaunch. To prevent this we strip the
    // bonus just before the game serializes and restore it right after.
    private static readonly List<string> _strippedForSave = new();

    /// <summary>
    /// Called from a SaveGame PREFIX: write the true base (without our allocation) into
    /// each stat we boosted, so the save file never contains Improve's bonus. Only strips
    /// entries whose live value still equals exactly what we last wrote (base + delta);
    /// anything an external force changed is left untouched to avoid corrupting the save.
    /// </summary>
    public static void StripBonusForSave()
    {
        _strippedForSave.Clear();
        if (PlayerController.instance == null || StatsManager.instance == null) return;

        string steamId = PlayerController.instance.playerSteamID;
        foreach (string stat in AllStatNames)
        {
            if (!_appliedDelta.TryGetValue(stat, out int delta) || delta == 0) continue;
            if (!_appliedBase.TryGetValue(stat, out int baseVal)) continue;

            if (ReadStatLocal(stat, steamId) == baseVal + delta)
            {
                WriteStatLocal(stat, steamId, baseVal);
                _strippedForSave.Add(stat);
            }
        }
    }

    /// <summary>
    /// Called from a SaveGame FINALIZER (runs even if the save throws): re-add our bonus
    /// to exactly the entries StripBonusForSave stripped, restoring the live in-game value.
    /// </summary>
    public static void RestoreBonusAfterSave()
    {
        if (_strippedForSave.Count == 0) return;

        if (PlayerController.instance != null && StatsManager.instance != null)
        {
            string steamId = PlayerController.instance.playerSteamID;
            foreach (string stat in _strippedForSave)
            {
                if (_appliedBase.TryGetValue(stat, out int baseVal) &&
                    _appliedDelta.TryGetValue(stat, out int delta))
                    WriteStatLocal(stat, steamId, baseVal + delta);
            }
        }

        _strippedForSave.Clear();
    }

    /// <summary>
    /// Forget all per-run tracking. Called on a run reset — the game wipes the upgrade
    /// dictionaries itself, so the next run starts a fresh reconcile from the true base.
    /// </summary>
    public static void ClearTracking()
    {
        _appliedBase.Clear();
        _appliedDelta.Clear();
    }
}
