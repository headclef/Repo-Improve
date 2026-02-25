# Improve

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

**Cost doubles each level:** `baseCost × 2^(level-1) × difficulty`

| Level | Hardest (×1.0) | Hard (×0.75) | Standard (×0.5) | Easy (×0.25) |
|-------|----------------|--------------|-----------------|--------------|
| 1     | 1,000,000      | 750,000      | 500,000         | 250,000      |
| 2     | 2,000,000      | 1,500,000    | 1,000,000       | 500,000      |
| 3     | 4,000,000      | 3,000,000    | 2,000,000       | 1,000,000    |
| 4     | 8,000,000      | 6,000,000    | 4,000,000       | 2,000,000    |
| Total 4 | 15,000,000  | 11,250,000   | 7,500,000       | 3,750,000    |

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

Stats are applied **0.25 seconds after entering a level** — early in the load process. This means:
- Your stat bonuses are active from the very start of each level
- Changing skill allocations in the menu takes effect **next level**, not mid-game
- Compatible with other mods that read stats later (like Character Stats at 1s)

## Configuration

Settings are in `BepInEx/config/headclef.Improve.cfg` or in the **in-game mod config menu**:

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| Difficulty Multiplier | `0.5` | 0.1–2.0 | Cost multiplier for leveling |
| Base Cost | `1,000,000` | 100k–10M | Base cost for the first level |

Save data is stored separately at:
`%AppData%/../LocalLow/semiwork/REPO/REPOModData/Improve/save.cfg`

## Requirements

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx) installed for R.E.P.O.

## Installation

1. Install via **Thunderstore** (recommended).
2. Or manually: place `Improve.dll` into your `BepInEx/plugins` folder.
3. Launch the game — save file is created automatically.
4. Open the **Improve** button in the main menu to start spending points.

## Multiplayer

- Each player tracks their own haul and stat allocations **independently** (client-side save)
- Stats are applied per-client at level start
- Only haul earned during levels you participate in counts — no credit for joining a high-haul session midway

## Compatibility

- Works alongside other leveling mods (stats stack via `PunManager.UpdateStat`)
- Compatible with Character Stats, Agility, Constitution, Armor, Increase Tumble Damage, and other headclef mods
- Does **not** interfere with shop upgrades — your stat points add on top

## Development

### Project Structure
```
├── Improve.cs                      # Plugin entry point & config
├── SaveData.cs                     # Persistent save data & level calculations
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
