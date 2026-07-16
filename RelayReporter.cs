namespace Improve;

/// <summary>
/// Reports Improve's allocations for the four stats R.E.P.O. simulates on the host to
/// <see cref="global::Relay.Relay"/>, which carries them there.
///
/// Grab Strength, Tumble Launch, Throw and Tumble Wings are computed on the master, from the
/// master's own replica of the player, derived from the master's own dictionaries — which only
/// ever learn a client's purchased base. Improve cannot reach them from a client at all, and
/// the game's stat-writing RPCs are all master-only, so there is no channel to push them
/// through either. Relay is that channel.
///
/// Improve used to carry its own copy of this bridge (1.1.9). It moved to Relay so there is
/// exactly ONE writer for those fields: they are additive, and each writer has to infer "the
/// value without my bonus" from what it last wrote, so two mods reconciling the same field
/// independently would read each other's retractions as the game re-deriving and re-apply on
/// top — doubling. Relay sums every contributor and writes once, which makes that impossible.
///
/// Nothing here touches Photon; Relay owns all of it, including the off-by-default switch and
/// the whole lifecycle. Improve just says what it wants the player to have.
/// </summary>
internal static class RelayReporter
{
    private const string SourceId = "headclef.Improve";

    /// <summary>
    /// Push the current allocations. Reading GetAllocationForStat means Train's trained levels
    /// come along for free — Train postfixes it — so Train needs no bridge code of its own.
    /// Relay only sends when a total actually changes, so calling this on a timer is fine.
    /// </summary>
    internal static void Report()
    {
        global::Relay.Relay.Report(SourceId, global::Relay.Relay.StatGrabStrength,
            SaveData.GetAllocationForStat("playerUpgradeStrength"));
        global::Relay.Relay.Report(SourceId, global::Relay.Relay.StatTumbleLaunch,
            SaveData.GetAllocationForStat("playerUpgradeLaunch"));
        global::Relay.Relay.Report(SourceId, global::Relay.Relay.StatThrow,
            SaveData.GetAllocationForStat("playerUpgradeThrow"));
        global::Relay.Relay.Report(SourceId, global::Relay.Relay.StatTumbleWings,
            SaveData.GetAllocationForStat("playerUpgradeTumbleWings"));
    }

    /// <summary>Withdraw our contribution (run reset — the allocations no longer apply).</summary>
    internal static void Clear() => global::Relay.Relay.Clear(SourceId);
}
