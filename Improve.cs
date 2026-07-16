using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Improve;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.HardDependency)]
public class Improve : BaseUnityPlugin
{
    private const string PluginGuid = "headclef.Improve";
    private const string PluginName = "Improve";
    private const string PluginVersion = "1.1.9";

    internal static Improve Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    // ── Config ──
    internal static ConfigEntry<float> DifficultyMultiplier = null!;
    internal static ConfigEntry<int> BaseCost = null!;
    internal static ConfigEntry<bool> CoopBridge = null!;

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
                "Base haul cost for level 1. Haul to reach level N is baseCost x difficulty x N^2.",
                new AcceptableValueRange<int>(100000, 10000000)));

        CoopBridge = Config.Bind("Multiplayer", "Host-Simulated Stat Bridge", false,
            new ConfigDescription(
                "EXPERIMENTAL - OFF BY DEFAULT. Grab Strength, Tumble Launch, Throw and Tumble Wings " +
                "are simulated by R.E.P.O. on the host's machine, from the host's own copy of your " +
                "character, so as a co-op client they normally do nothing for you. Turn this on and " +
                "your client tells the host its levels for those four stats, and Improve ON THE HOST " +
                "applies them to its copy of you. The host must also run Improve with this turned on. " +
                "An earlier always-on version of this bridge broke multiplayer connect (the server " +
                "list would not load and picking a region hung the game), so it is off unless you opt " +
                "in: with it off, Improve behaves exactly as it does without the bridge. Your other " +
                "stats are unaffected either way - they already work as a client."));
    }
}
