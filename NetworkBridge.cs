using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Improve;

/// <summary>
/// Co-op bridge for the stats the game simulates on the HOST's machine.
///
/// Grab Strength, Tumble Launch, Throw and Tumble Wings are consumed by the master client
/// only (grab forces in <c>PhysGrabObject.PhysicsGrabbingManipulation</c>, launch force in
/// <c>PlayerTumble.TumbleSet</c> via a MasterClient RPC, throw impulse in
/// <c>PhysGrabObject.Throw</c>, glide force in <c>ItemUpgradePlayerTumbleWingsLogic</c>) —
/// and always from the master's OWN replica of the player's components, which the master
/// derives from ITS OWN dictionaries. A client's local dictionary writes never reach it,
/// and every stat-writing game RPC is MasterOnly, so without help a client's Improve levels
/// for these four stats are invisible in multiplayer.
///
/// The bridge fixes that when the host also runs Improve: each client broadcasts its
/// allocation totals for the four stats over a custom Photon event, and the receiver on the
/// MASTER applies them as live component deltas to that player's replica — using the game's
/// own per-level formulas, reconciled so the game's spawn derivation and vanilla upgrade
/// pickups are never double-counted. The master's dictionaries are never touched, so nothing
/// can leak into the host's save file.
///
/// Security mirrors the game's OwnerOnlyRPC: the receiver resolves the player from the
/// Photon actor number of the event's SENDER, so a payload can never speak for another
/// player. On a host without Improve the event code is simply never handled — exactly
/// today's behavior, silently.
/// </summary>
internal static class NetworkBridge
{
    private const byte EventCode = 117;
    private const string Magic = "headclef.Improve";
    private const float TickInterval = 0.5f;
    private const float Eps = 0.001f;

    // Bridged stats in wire order; per-level scales mirror PunManager's Update…RightAway.
    private static readonly string[] StatNames =
    {
        "playerUpgradeStrength", "playerUpgradeLaunch",
        "playerUpgradeThrow", "playerUpgradeTumbleWings"
    };
    private static readonly float[] PerLevel = { 0.2f, 1f, 0.3f, 1f };

    private static bool _initialized;
    private static float _nextTick;

    // ── Sender state (this machine is a non-master client) ──
    private static readonly int[] _lastSent = { -1, -1, -1, -1 };

    // ── Receiver state (this machine is the master) ──
    private static readonly Dictionary<int, int[]> _desired = new();          // actor → levels
    private static readonly Dictionary<(int actor, int stat), AppliedStat> _applied = new();

    private sealed class AppliedStat
    {
        public Object? component;   // instance we applied to — a new spawn resets the baseline
        public float baseValue;     // the player's value without our bonus
        public float lastWritten;   // exactly what we last wrote (base + bonus)
    }

    internal static void Initialize()
    {
        if (_initialized) return;
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _initialized = true;
    }

    internal static void Shutdown()
    {
        if (!_initialized) return;
        PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
        _initialized = false;
    }

    /// <summary>Called every frame from the plugin; does real work at most twice a second.</summary>
    internal static void Update()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + TickInterval;

        try
        {
            SenderTick();
            ReceiverTick();
        }
        catch (System.Exception ex)
        {
            Improve.Logger.LogDebug($"NetworkBridge tick skipped: {ex.Message}");
        }
    }

    /// <summary>Scene changed: the components we applied to are gone. Keep the desired
    /// levels (they persist across levels) — only the per-instance tracking resets.</summary>
    internal static void OnSceneSwitch() => _applied.Clear();

    // ══════════════════════ Sender ══════════════════════

    private static void SenderTick()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient)
        {
            // Re-broadcast on the next join — the master's cache from a previous room is gone.
            _lastSent[0] = _lastSent[1] = _lastSent[2] = _lastSent[3] = -1;
            return;
        }

        bool changed = false;
        var levels = new int[4];
        for (int i = 0; i < 4; i++)
        {
            // Includes Train's trained levels — Train postfixes GetAllocationForStat.
            levels[i] = Mathf.Max(0, SaveData.GetAllocationForStat(StatNames[i]));
            if (levels[i] != _lastSent[i]) changed = true;
        }
        if (!changed) return;

        var payload = new object[] { Magic, levels[0], levels[1], levels[2], levels[3] };
        PhotonNetwork.RaiseEvent(EventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

        for (int i = 0; i < 4; i++) _lastSent[i] = levels[i];
        Improve.Logger.LogDebug(
            $"Bridge sent: Strength {levels[0]}, Launch {levels[1]}, Throw {levels[2]}, Wings {levels[3]}.");
    }

    // ══════════════════════ Receiver (master only) ══════════════════════

    private static void OnEvent(EventData photonEvent)
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

    private static void ReceiverTick()
    {
        if (!PhotonNetwork.InRoom)
        {
            _desired.Clear();
            _applied.Clear();
            return;
        }
        if (!PhotonNetwork.IsMasterClient || _desired.Count == 0) return;
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
    ///   • cur &gt; lastWritten → the game added on top (vanilla upgrade pickup) → fold into base
    ///   • cur &lt; lastWritten → the game re-assigned it (PlayerTumble.SetupDone /
    ///     PlayerAvatar.LateStart write the dictionary value over our bonus at spawn) → new base
    /// Idempotent, so racing the spawn derivation only costs one 0.5s tick.
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

    private static Object? StatComponent(PlayerAvatar avatar, int stat) => stat switch
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
