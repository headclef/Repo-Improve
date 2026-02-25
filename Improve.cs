using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Improve;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Improve : BaseUnityPlugin
{
    private const string PluginGuid = "headclef.Improve";
    private const string PluginName = "Improve";
    private const string PluginVersion = "1.0.1";

    internal static Improve Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    // ── Config ──
    internal static ConfigEntry<float> DifficultyMultiplier = null!;
    internal static ConfigEntry<int> BaseCost = null!;

    private void Awake()
    {
        Instance = this;
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        BindConfiguration();
        SaveData.Initialize();
        ImproveMenu.Initialize();

        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();

        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    private void OnDestroy()
    {
        Harmony?.UnpatchSelf();
    }

    private void BindConfiguration()
    {
        const string section = "Leveling";

        DifficultyMultiplier = Config.Bind(section, "Difficulty Multiplier", 0.5f,
            new ConfigDescription(
                "Difficulty multiplier for level-up cost. Easy=0.25, Standard=0.5, Hard=0.75, Hardest=1.0",
                new AcceptableValueRange<float>(0.1f, 2.0f)));

        BaseCost = Config.Bind(section, "Base Cost", 1000000,
            new ConfigDescription(
                "Base haul cost for the first level-up. Cost doubles each level.",
                new AcceptableValueRange<int>(100000, 10000000)));
    }
}
