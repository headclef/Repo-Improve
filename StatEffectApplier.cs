using UnityEngine;

namespace Improve;

/// <summary>
/// Makes Improve's allocations FELT for co-op clients — purely locally, with no networking,
/// so it can never affect the multiplayer connection (unlike the removed bridge).
///
/// Improve writes base+allocation into the StatsManager dictionaries, and the game derives
/// each player's live stats from those dictionaries once, at spawn. On the host that is
/// enough: the dictionary already holds the bonus when the components read it. But a co-op
/// CLIENT has the host's dictionary values synced over its own at every scene switch, and the
/// host only knows the client's *purchased* upgrades — the client's Improve allocation was
/// never sent to the host, by design. So the client's components derive the base only, and
/// re-writing the dictionary afterwards doesn't help: the components already read it once and
/// never re-derive. The result the player sees is exactly "I can see my points, but they do
/// nothing."
///
/// This applier runs ONLY on a co-op client and tops the live components up to the full
/// value, mirroring the game's own per-stat formulas:
///   • absolute stats (Health, Extra Jump, Tumble Climb, Crouch Rest) are SET straight from
///     the dictionary Improve maintains (base+alloc), so they land at the full value whatever
///     the component derived — idempotent, never doubles;
///   • relative stats with no dictionary re-read (Sprint Speed, Stamina, Grab Range) are
///     additive on the live field, so they are reconciled AGAINST THE LIVE FIELD ITSELF, the
///     same way SaveData reconciles the dictionaries: we remember the value we left the field
///     at, and on the next tick decide from how it moved whether our bonus is still applied
///     (leave it), was re-derived away on a surviving avatar / revive (re-apply), or had a
///     vanilla upgrade added on top (fold in, don't re-add). This is self-correcting, so it
///     NEVER double-adds across a scene switch — crucially, R.E.P.O. keeps the same
///     PlayerController alive across truck↔level transitions within a run, so a naive
///     "reset the counter every scene switch" would re-add the bonus onto a field that still
///     carried it. We therefore reset the per-spawn tracking ONLY when the avatar instance
///     actually changes (fresh spawn) or the field genuinely dropped.
///
/// On the host or in single player this does nothing: the game's own derivation already
/// applied everything, so there is no work to do and no risk of double-counting.
///
/// The four host-simulated stats (Grab Strength, Tumble Launch, Throw, Tumble Wings force) are
/// deliberately absent: R.E.P.O. computes them on the host from the host's copy of the player,
/// so no client-side write can change them for a non-host player.
/// </summary>
internal static class StatEffectApplier
{
    private static PlayerController? _pc;
    private static PlayerAvatar? _avatar;

    // Per relative stat: the Improve levels we have applied to the live field for the CURRENT
    // avatar, and the field value we left behind afterwards. Comparing the current field value
    // to _last* tells us whether our bonus is still present (unchanged), was re-derived away on
    // a surviving avatar / revive (dropped below what we left), or had a vanilla upgrade added
    // on top (rose above it) — exactly the reconcile SaveData does for the dictionaries.
    private static int _appliedSpeed;
    private static int _appliedStamina;
    private static int _appliedRange;
    private static float _lastSpeed;
    private static float _lastStamina;
    private static float _lastRange;

    // A field is treated as "dropped" (re-derived / wiped) only if it fell by more than this,
    // to shrug off floating-point noise in the volatile sprint fields.
    private const float DropEpsilon = 0.001f;

    internal static void Invalidate()
    {
        _pc = null;
        _avatar = null;
        _appliedSpeed = 0;
        _appliedStamina = 0;
        _appliedRange = 0;
        _lastSpeed = 0f;
        _lastStamina = 0f;
        _lastRange = 0f;
    }

    /// <summary>
    /// Reconcile the local player's live components up to the full (base + allocation) value.
    /// Idempotent and safe to call on a timer. No-op unless we are a co-op client.
    /// </summary>
    internal static void Apply()
    {
        try
        {
            // Only co-op clients need this. On the host / in single player the game's own
            // spawn derivation already applied the full value, so there is nothing to add.
            if (SemiFunc.IsMasterClientOrSingleplayer()) return;
            if (!SemiFunc.RunIsLevel()) return;

            var pc = PlayerController.instance;
            if (pc == null) return;
            var avatar = pc.playerAvatarScript;
            if (avatar == null || avatar.deadSet) return;
            var sm = StatsManager.instance;
            if (sm == null) return;

            string id = pc.playerSteamID;
            if (string.IsNullOrEmpty(id)) return;

            // A fresh controller/avatar (level start after death, revive) has re-derived the
            // base from the dictionary, so the relative top-ups must re-apply from zero. A
            // SURVIVING avatar across a truck↔level scene switch must NOT reset — its live
            // fields still carry our bonus, and re-adding would double it.
            bool fresh = _pc != pc || _avatar != avatar;
            if (fresh)
            {
                _pc = pc;
                _avatar = avatar;
                _appliedSpeed = 0;
                _appliedStamina = 0;
                _appliedRange = 0;
            }

            // ── Absolute stats — SET to the dictionary value (base+alloc) the game would have
            //    derived if it had seen our bonus. Idempotent; corrects a base-only client and
            //    can never double because it assigns rather than adds. ──
            if (avatar.playerHealth != null)
            {
                int desiredMax = 100 + sm.GetPlayerMaxHealth(id);   // GetPlayerMaxHealth = dict * 20
                int curMax = avatar.playerHealth.maxHealth;
                if (desiredMax > curMax)
                {
                    avatar.playerHealth.maxHealth = desiredMax;
                    avatar.playerHealth.Heal(desiredMax - curMax, effect: false); // fill the new headroom
                }
            }

            pc.JumpExtra = DictValue(sm.playerUpgradeExtraJump, id);
            avatar.upgradeTumbleClimb = DictValue(sm.playerUpgradeTumbleClimb, id);
            avatar.upgradeCrouchRest = DictValue(sm.playerUpgradeCrouchRest, id);

            // ── Relative stats — additive on the live field, so reconciled against the field
            //    itself. The shortfall is exactly our allocation (the part the host doesn't
            //    know). See ReconcileRelative for the anti-double / anti-loss bookkeeping. ──
            ReconcileRelative(
                fresh,
                alloc: SaveData.GetAllocationForStat("playerUpgradeSpeed"),
                readStable: () => pc.playerOriginalSprintSpeed, // stable source (SprintSpeed itself is reset from it every frame)
                applied: ref _appliedSpeed,
                lastStable: ref _lastSpeed,
                addLevels: diff =>
                {
                    pc.SprintSpeed += diff;
                    pc.SprintSpeedUpgrades += diff;
                    pc.playerOriginalSprintSpeed += diff;
                });

            ReconcileRelative(
                fresh,
                alloc: SaveData.GetAllocationForStat("playerUpgradeStamina"),
                readStable: () => pc.EnergyStart,
                applied: ref _appliedStamina,
                lastStable: ref _lastStamina,
                addLevels: diff =>
                {
                    pc.EnergyStart += 10f * diff;
                    pc.EnergyCurrent = Mathf.Min(pc.EnergyCurrent + 10f * diff, pc.EnergyStart);
                });

            ReconcileRelative(
                fresh,
                alloc: SaveData.GetAllocationForStat("playerUpgradeRange"),
                readStable: () => avatar.physGrabber != null ? avatar.physGrabber.grabRange : _lastRange,
                applied: ref _appliedRange,
                lastStable: ref _lastRange,
                addLevels: diff =>
                {
                    if (avatar.physGrabber != null)
                        avatar.physGrabber.grabRange += diff;
                });
        }
        catch (System.Exception ex)
        {
            Improve.Logger.LogWarning($"Client stat apply skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Reconcile one additive relative stat against its live field, so the bonus is applied
    /// exactly once and never doubles nor gets lost:
    ///   • fresh avatar               → the field holds the freshly-derived base; apply the
    ///                                  whole allocation.
    ///   • field dropped below _last  → our bonus was re-derived away (revive / re-spawn on the
    ///                                  same instance); re-apply from zero.
    ///   • field == _last             → our bonus is still present; only apply the change if the
    ///                                  allocation itself grew (a point spent this session).
    ///   • field rose above _last     → a vanilla upgrade landed on top; keep our applied count,
    ///                                  fold the extra into the baseline (don't re-add).
    /// The current field value is re-read after applying and stored as the new baseline.
    /// </summary>
    private static void ReconcileRelative(
        bool fresh,
        int alloc,
        System.Func<float> readStable,
        ref int applied,
        ref float lastStable,
        System.Action<int> addLevels)
    {
        float cur = readStable();

        if (fresh)
        {
            applied = 0;                 // field is the freshly-derived base
        }
        else if (cur + DropEpsilon < lastStable)
        {
            applied = 0;                 // re-derived / wiped on a surviving avatar — re-apply
        }
        // cur >= lastStable → our bonus is intact (==) or a vanilla upgrade was added (>);
        // either way keep `applied` so we never re-add what is already there.

        int want = Mathf.Max(0, alloc);
        int diff = want - applied;
        if (diff != 0)
            addLevels(diff);
        applied = want;

        lastStable = readStable();       // remember where we left the field for the next tick
    }

    private static int DictValue(System.Collections.Generic.Dictionary<string, int> dict, string id)
        => dict != null && dict.TryGetValue(id, out int v) ? v : 0;
}
