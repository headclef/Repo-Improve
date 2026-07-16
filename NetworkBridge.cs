using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Improve;

/// <summary>
/// OPT-IN co-op bridge for the four stats R.E.P.O. simulates on the HOST's machine.
///
/// Grab Strength, Tumble Launch, Throw and Tumble Wings are consumed by the master client
/// only, and always from the master's OWN replica of the player, which it derives from ITS
/// OWN dictionaries:
///   • launch force — <c>PlayerTumble.TumbleSet</c>, reached by a <c>RpcTarget.MasterClient</c>
///     RPC, computing <c>forward * (3f * tumbleLaunch)</c> from the master's field;
///   • grab / throw forces — <c>PhysGrabObject.PhysicsGrabbingManipulation</c> and
///     <c>PhysGrabObject.Throw</c>, which return early on non-masters;
///   • glide force — <c>ItemUpgradePlayerTumbleWingsLogic</c>, master-gated.
/// A client's local writes never reach any of them, and every stat-writing game RPC is
/// MasterOnly (<c>PunManager.UpdateStatRPC</c> is guarded by <c>SemiFunc.MasterOnlyRPC</c>,
/// which only accepts <c>_info.Sender == PhotonNetwork.MasterClient</c>), so a client cannot
/// push a value through the game's own channel. Hence a custom Photon event.
///
/// Each client reports its allocation totals for the four stats; the receiver on the MASTER
/// applies them as live component deltas to that player's replica.
///
/// ── Why this exists behind an OFF-by-default switch ──
/// An earlier always-on version of this bridge (Improve 1.1.5, and its 1.1.6 guard attempt)
/// wedged multiplayer: the public server list would not populate and picking a region hung
/// the game on a loading screen forever, with NO exception in the log. The root cause was
/// never proven, so the two things that version did which this one does not are both removed:
///   1. it subscribed to <c>PhotonNetwork.NetworkingClient.EventReceived</c> at plugin Awake,
///      i.e. through the entire connect / region-select / lobby flow;
///   2. it read <c>PhotonNetwork</c> state from a per-frame <c>Update()</c>.
/// This version touches Photon ONLY from Improve's existing in-level watchdog coroutine (twice
/// a second, and only once a level is actually running), subscribes lazily and only as the
/// master, and tears the subscription down when the level ends. With the config off, not one
/// line of this file runs and Improve behaves exactly as it does without the bridge.
///
/// ── Safety rules this file must keep ──
///   • NEVER write the master's StatsManager dictionaries. Only live component fields are
///     touched, so the client's bonus can never reach the host's <c>.es3</c>: the host is the
///     only peer that saves (<c>StatsManager.SaveGame</c> returns unless master), it
///     serializes the dictionaries only, and nothing copies a component value back into one.
///     The bonus therefore dies with the component when the player leaves or the level ends.
///   • Resolve the player from the event's SENDER actor number, never from the payload —
///     the mirror of the game's own OwnerOnlyRPC, so an event can only ever speak for its own
///     player.
///   • Key the per-instance bookkeeping to the component we applied to, so a respawned avatar
///     re-baselines instead of stacking a second bonus on top.
/// On a host without Improve (or with the switch off) the event code is simply never handled,
/// which is harmless.
/// </summary>
internal static class NetworkBridge
{
    private const byte EventCode = 117;
    private const string Magic = "headclef.Improve";
    private const float Eps = 0.001f;

    // Re-broadcast every N ticks (~10s at the watchdog's 0.5s) even when nothing changed, so a
    // host that started listening late — or cleared its state — still learns our totals.
    private const int HeartbeatTicks = 20;

    // Bridged stats in wire order; per-level scales mirror the game's own derivation
    // (PhysGrabber: grabStrength += lvl * 0.2f, throwStrength += lvl * 0.3f).
    private static readonly string[] StatNames =
    {
        "playerUpgradeStrength", "playerUpgradeLaunch",
        "playerUpgradeThrow", "playerUpgradeTumbleWings"
    };
    private static readonly float[] PerLevel = { 0.2f, 1f, 0.3f, 1f };

    private static bool _subscribed;
    private static int _ticksSinceSend;

    // ── Sender state (this machine is a non-master client) ──
    private static readonly int[] _lastSent = { -1, -1, -1, -1 };

    // ── Receiver state (this machine is the master) ──
    private static readonly Dictionary<int, int[]> _desired = new();          // actor → levels
    private static readonly Dictionary<(int actor, int stat), AppliedStat> _applied = new();

    private sealed class AppliedStat
    {
        public UnityEngine.Object? component;   // instance we applied to — a new spawn resets the baseline
        public float baseValue;                 // the player's value without our bonus
        public float lastWritten;               // exactly what we last wrote (base + bonus)
    }

    /// <summary>
    /// Driven from the in-level watchdog coroutine (twice a second, levels only). This is the
    /// ONLY entry point that may touch Photon — there is deliberately no Awake subscription
    /// and no per-frame tick.
    /// </summary>
    internal static void Tick()
    {
        try
        {
            if (!Improve.CoopBridge.Value) { Teardown(); return; }
            if (!SemiFunc.IsMultiplayer()) { Teardown(); return; }

            // IsMessageQueueRunning is false while PUN loads a synced scene on join; the whole
            // tick stays out of Photon until the room is live and the queue is pumping.
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMessageQueueRunning) return;

            if (PhotonNetwork.IsMasterClient)
            {
                Subscribe();
                ReceiverTick();
            }
            else
            {
                SenderTick();
            }
        }
        catch (Exception ex)
        {
            Improve.Logger.LogDebug($"Bridge tick skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Level ended, run reset, or the bridge was switched off: drop the subscription and the
    /// per-instance tracking (those components are gone). The reported levels are kept — they
    /// are just data — and clients re-broadcast on the next level anyway.
    /// </summary>
    internal static void Teardown()
    {
        Unsubscribe();
        _applied.Clear();
        _lastSent[0] = _lastSent[1] = _lastSent[2] = _lastSent[3] = -1;
        _ticksSinceSend = 0;
    }

    private static void Subscribe()
    {
        if (_subscribed) return;
        try
        {
            if (PhotonNetwork.NetworkingClient == null) return;
            PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
            _subscribed = true;
            Improve.Logger.LogDebug("Bridge listening (host).");
        }
        catch { /* try again next tick */ }
    }

    private static void Unsubscribe()
    {
        if (!_subscribed) return;
        try
        {
            if (PhotonNetwork.NetworkingClient != null)
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
        }
        catch { /* going away anyway */ }
        _subscribed = false;
    }

    // ══════════════════════ Sender (non-master client) ══════════════════════

    private static void SenderTick()
    {
        if (!SemiFunc.RunIsLevel()) return;

        var levels = new int[4];
        bool changed = false;
        for (int i = 0; i < 4; i++)
        {
            // Includes Train's trained levels — Train postfixes GetAllocationForStat.
            levels[i] = Mathf.Max(0, SaveData.GetAllocationForStat(StatNames[i]));
            if (levels[i] != _lastSent[i]) changed = true;
        }

        MirrorLaunchLocallyForSound();

        _ticksSinceSend++;
        if (!changed && _ticksSinceSend < HeartbeatTicks) return;

        PhotonNetwork.RaiseEvent(
            EventCode,
            new object[] { Magic, levels[0], levels[1], levels[2], levels[3] },
            new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
            SendOptions.SendReliable);

        for (int i = 0; i < 4; i++) _lastSent[i] = levels[i];
        _ticksSinceSend = 0;

        if (changed)
            Improve.Logger.LogDebug(
                $"Bridge sent: Strength {levels[0]}, Launch {levels[1]}, Throw {levels[2]}, Wings {levels[3]}.");
    }

    /// <summary>
    /// Cosmetic only. <c>PlayerTumble.TumbleSetRPC</c> plays the launch sound from the LOCAL
    /// <c>tumbleLaunch</c>, while the force itself is computed on the host. Without this the
    /// bridge would give us the push with no sound. Writing this field on a client cannot
    /// affect the force — <c>TumbleSet</c> only ever runs on the master.
    /// </summary>
    private static void MirrorLaunchLocallyForSound()
    {
        var avatar = PlayerAvatar.instance;
        if (avatar == null || avatar.tumble == null) return;
        var sm = StatsManager.instance;
        if (sm == null) return;

        string id = avatar.steamID;
        if (string.IsNullOrEmpty(id)) return;
        if (sm.playerUpgradeLaunch != null && sm.playerUpgradeLaunch.TryGetValue(id, out int lvl))
            avatar.tumble.tumbleLaunch = lvl;
    }

    // ══════════════════════ Receiver (master only) ══════════════════════

    private static void OnEvent(EventData photonEvent)
    {
        try
        {
            if (photonEvent.Code != EventCode) return;
            if (!PhotonNetwork.IsMasterClient) return;
            if (photonEvent.CustomData is not object[] data || data.Length < 5) return;
            if (data[0] is not string magic || magic != Magic) return;

            var levels = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (data[i + 1] is not int lvl) return;
                levels[i] = Mathf.Max(0, lvl);
            }

            // Keyed by the SENDER's actor number — an event can only ever affect its own player.
            _desired[photonEvent.Sender] = levels;
        }
        catch (Exception ex)
        {
            Improve.Logger.LogDebug($"Bridge event ignored: {ex.Message}");
        }
    }

    private static void ReceiverTick()
    {
        if (!SemiFunc.RunIsLevel() || _desired.Count == 0) return;
        if (GameDirector.instance == null) return;

        foreach (var pair in _desired)
        {
            var avatar = AvatarFromActor(pair.Key);
            if (avatar == null) continue;

            for (int stat = 0; stat < 4; stat++)
                ReconcileStat(pair.Key, avatar, stat, pair.Value[stat]);
        }
    }

    private static PlayerAvatar? AvatarFromActor(int actorNumber)
    {
        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isLocal || player.photonView == null) continue;
            if (player.photonView.OwnerActorNr == actorNumber) return player;
        }
        return null;
    }

    /// <summary>
    /// Keep <c>component value == base + perLevel × desired</c>, deriving the true base from
    /// what we last wrote — the same self-correcting reconcile Improve uses on its own
    /// dictionaries, applied to the live replica fields:
    ///   • cur &gt; lastWritten → the game added on top (a vanilla upgrade pickup, or
    ///     <c>PhysGrabber</c>'s <c>+=</c> spawn derivation) → fold the extra into the base;
    ///   • cur &lt; lastWritten → the game re-assigned it (<c>PlayerTumble.Setup</c> and
    ///     <c>PlayerAvatar</c> write the dictionary value straight over our bonus at spawn) →
    ///     treat the new value as the base and re-apply.
    /// Idempotent, so racing the spawn derivation only ever costs one 0.5s tick.
    /// </summary>
    private static void ReconcileStat(int actor, PlayerAvatar avatar, int stat, int desired)
    {
        var component = StatComponent(avatar, stat);
        if (component == null) return;

        float cur = StatGet(avatar, stat);
        var key = (actor, stat);

        if (!_applied.TryGetValue(key, out var applied) || applied.component != component)
        {
            if (desired == 0) return; // nothing wanted, nothing applied to this instance
            applied = new AppliedStat { component = component, baseValue = cur, lastWritten = cur };
            _applied[key] = applied;
        }
        else if (cur > applied.lastWritten + Eps)
        {
            applied.baseValue += cur - applied.lastWritten;
        }
        else if (cur < applied.lastWritten - Eps)
        {
            applied.baseValue = cur;
        }

        float want = applied.baseValue + PerLevel[stat] * desired;
        if (Mathf.Abs(want - cur) > Eps) StatSet(avatar, stat, want);
        applied.lastWritten = StatGet(avatar, stat); // read back — Launch rounds to int
    }

    private static UnityEngine.Object? StatComponent(PlayerAvatar avatar, int stat) => stat switch
    {
        0 or 2 => avatar.physGrabber,
        1 => avatar.tumble,
        3 => avatar,
        _ => null
    };

    private static float StatGet(PlayerAvatar avatar, int stat) => stat switch
    {
        0 => avatar.physGrabber.grabStrength,
        1 => avatar.tumble.tumbleLaunch,
        2 => avatar.physGrabber.throwStrength,
        3 => avatar.upgradeTumbleWings,
        _ => 0f
    };

    private static void StatSet(PlayerAvatar avatar, int stat, float value)
    {
        switch (stat)
        {
            case 0: avatar.physGrabber.grabStrength = value; break;
            case 1: avatar.tumble.tumbleLaunch = Mathf.RoundToInt(value); break;
            case 2: avatar.physGrabber.throwStrength = value; break;
            case 3: avatar.upgradeTumbleWings = value; break;
        }
    }
}
