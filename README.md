# Improve

![Improve in the main menu](https://raw.githubusercontent.com/headclef/Repo-Improve/core/screenshots/improve-main-menu.jpg)

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **R.E.P.O.** that adds a haul-based leveling system with spendable stat points.

## What This Mod Does

Earn stat points by accumulating haul across all your sessions. Spend points on any of the 13 character stats to permanently power up your character — even during an active save.

### How Haul Tracking Works

Unlike other leveling mods that only count haul when you die, **Improve captures your haul after every level**. This means:

- Complete a level and extract 200K? → **+200K** added to your lifetime haul immediately
- Play another level and earn 300K? → **+300K** added
- Die on the next level? → No extra haul added (the level had 0 haul)
- Join a multiplayer session mid-run? → Only haul **you participate in earning** counts

This lets you **level up during a save**, not just after dying.

### Leveling Formula

**Haul to reach level N:** `baseCost × difficulty × N²`

Your level is the highest **N** whose threshold your lifetime haul has reached — i.e. `level = floor(sqrt(lifetimeHaul / (baseCost × difficulty)))`. Each level is a **single threshold**, not a running total: reaching level 4 needs the level-4 haul, not the sum of levels 1–4.

| Level | Hardest (×1.0) | Hard (×0.75) | Standard (×0.5) | Easy (×0.25) |
|-------|----------------|--------------|-----------------|--------------|
| 1     | 1,000,000      | 750,000      | 500,000         | 250,000      |
| 2     | 4,000,000      | 3,000,000    | 2,000,000       | 1,000,000    |
| 3     | 9,000,000      | 6,750,000    | 4,500,000       | 2,250,000    |
| 4     | 16,000,000     | 12,000,000   | 8,000,000       | 4,000,000    |
| 5     | 25,000,000     | 18,750,000   | 12,500,000      | 6,250,000    |

Each level awards **1 stat point** to spend on any stat you want.

### Difficulty Presets

| Difficulty | Multiplier | Description |
|------------|-----------|-------------|
| Easy       | 0.25      | Quick progression — great for casual play |
| Standard   | 0.5       | Balanced (default) |
| Hard       | 0.75      | Slower grind |
| Hardest    | 1.0       | Full cost — for dedicated players |

> **Changing difficulty mid-game?** The mod recalculates your level instantly. If you increase difficulty and end up with more spent points than your new level allows, the menu will warn you and lock further spending until you earn more haul or reset your stats.

### Spending Stat Points

Open the **Improve** menu (available in main menu, escape menu, and lobby) to see two panels:

<p align="center">
  <img src="https://raw.githubusercontent.com/headclef/Repo-Improve/core/screenshots/improve-no-given-stats.jpg" width="48%" alt="Improve menu before spending points" />
  <img src="https://raw.githubusercontent.com/headclef/Repo-Improve/core/screenshots/improve-given-stats.jpg" width="48%" alt="Improve menu after spending five points into Tumble Launch" />
</p>

> Before and after spending points — five allocated into Tumble Launch drops Available Points from 5 to 0.

**Left panel — Progress:**
- Lifetime Haul
- Current Level
- Available / Spent Points
- Haul needed for next level
- Current difficulty

**Right panel — Skills:**
- Sliders for all 13 stats (Health, Speed, Stamina, Extra Jump, Grab Range, Strength, Throw, Tumble Launch, Tumble Climb, Tumble Wings, Crouch Rest, Map Player Count, Death Head Battery)
- Overspent warning if difficulty was raised

### Two Types of Reset

| Button | What it does | Keep haul? | Keep level? |
|--------|-------------|------------|-------------|
| **Reset Stats** | Reclaim all spent points | ✅ Yes | ✅ Yes |
| **Reset All** | Wipe everything | ❌ No | ❌ No |

### When Stats Apply

Stats are applied **a few frames after you spawn into a level**, then continuously re-enforced by a watchdog (and again after each network sync) so they survive host/mod overwrites. This means:
- Your stat bonuses are active from the very start of each level
- Allocations are applied **idempotently** — never stacked twice, even across sync events
- Changing skill allocations in the menu takes effect **next level**, not mid-game
- Compatible with other mods that read stats later (like Character Stats)

<p align="center">
  <img src="https://raw.githubusercontent.com/headclef/Repo-Improve/core/screenshots/improve-in-truck.jpg" width="48%" alt="Allocated Tumble Launch points reflected in the game's own Upgrades panel" />
  <img src="https://raw.githubusercontent.com/headclef/Repo-Improve/core/screenshots/improve-ui-in-truck.jpg" width="48%" alt="Improve level shown in the UI overlay" />
</p>

Your allocations take effect in-game: the game's own **Upgrades** panel shows the five Tumble Launch points, and (with the [UI](https://github.com/headclef/Repo-UI) mod) the overlay shows your Improve level.

> **Your bonus never gets baked into the save file.** Improve writes its bonus into your live stats at runtime, but strips it out the instant the game saves and restores it right after — so the `.es3` save only ever stores legitimately purchased upgrades. This means the bonus can't stack on itself across save / quit / relaunch, and uninstalling Improve leaves no inflated stats behind.

## Configuration

Settings are in `BepInEx/config/headclef.Improve.cfg` or in the **in-game mod config menu**:

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| Difficulty Multiplier | `0.5` | 0.1–2.0 | Cost multiplier for leveling |
| Base Cost | `1,000,000` | 100k–10M | Base cost for the first level |

Save data is stored separately at:
`%AppData%/../LocalLow/semiwork/Repo/REPOModData/Improve/save.cfg`

## Requirements

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx) installed for R.E.P.O.

## Installation

1. Install via **Thunderstore** (recommended).
2. Or manually: place `Improve.dll` into your `BepInEx/plugins` folder.
3. Launch the game — save file is created automatically.
4. Open the **Improve** button in the main menu to start spending points.

## Multiplayer

- Each player tracks their own haul and stat allocations **independently** (client-side save). Your numbers are always your own — the host's allocations never mix into yours.
- Only haul earned during levels you participate in counts — no credit for joining a high-haul session midway.
- Fully client-side and safe in any lobby — Improve only ever reads and boosts your own local player, and never writes networked state.

**As the host or in single player,** every stat applies normally.

**As a co-op client (not the host),** most stats apply on your own machine and work as expected: Health, Sprint Speed, Stamina, Extra Jump, Grab Range, Tumble Climb, Crouch Rest, Map Player Count and Death Head Battery. A few — **Grab Strength, Tumble Launch, Throw and Tumble Wings** — are simulated by R.E.P.O. on the *host's* machine from the host's copy of your character, so a client-side mod cannot make them take effect for you (the game's stat-writing RPCs are host-only by design). They still apply in single player and when you host.

## Compatibility

- Works alongside other leveling mods (stats stack via `PunManager.UpdateStat`)
- Compatible with Character Stats, Agility, Constitution, Armor, Increase Tumble Damage, and other headclef mods
- Does **not** interfere with shop upgrades — your stat points add on top

## Development

### Project Structure
```
├── Improve.cs                      # Plugin entry point & config
├── SaveData.cs                     # Persistent save data & level calculations
├── StatEffectApplier.cs            # Co-op client — tops live components up to the full value
├── ImproveMenu.cs                  # MenuLib UI — progress & skill panels
├── Patches/
│   └── ImprovePatch.cs             # Haul capture & stat application hooks
└── README.md
```

### Building
```bash
dotnet build
```

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

---

These mods (all) generated by using AI tools. If there's anything wrong use NexusMods to give a feedback, I use there more than ThunderStore.
