# Gear profiles

Every `*.json` file in this folder is embedded into the service (see `SkyPlayerState.csproj`) and loaded by
`GearProfileCatalog`. The catalog tests (`Services/Tasks/Gear/GearProfileCatalog.Tests.cs`) validate every
embedded file, so a broken or unsourced profile fails CI. Only add profiles that come from a sourced research
dossier: real item tags (checked against NotEnoughUpdates-REPO), real stats, https sources.

One file per activity family, e.g. `farming.json`, `mining-gem.json`, `fishing-water.json`, `kuudra.json`.

## Schema

```jsonc
{
  "id": "farming",                    // lower case letters, digits, dashes; unique across files
  "name": "Crop farming",
  "anchor": "budget",                 // setup id the community rate is assumed to be earned with (default budget)
  "researchedAt": "2026-11-07",       // optional ISO date
  "sources": ["https://..."],         // required, https only
  "model": { "kind": "fortune", "fortuneStat": "Farming Fortune", "rareStat": "Magic Find" },
  "tiers": ["Kuudra T1", "Kuudra T2"], // tierUnlock only (required there): task ladder, lowest tier first
  "modelConfidence": "medium",        // optional cap on the model confidence: high | medium | low
  "maxMultiplier": 8.6,               // optional, 5 to 20 (default clamp 5); raises the upper clamp, caps confidence at medium
  "statCaps": { "Fishing Speed": 300 }, // optional, applied to every setup's stats before the model
  "baseline": {                       // id must be "baseline"; what a player is assumed to own already
    "id": "baseline", "name": "Starter gear",
    "items": [ { "slot": "tool", "tag": "ITEM_TAG", "name": "Display name", "reason": "why it is here" } ],
    "stats": { "Farming Fortune": 50, "Magic Find": 0 }
  },
  "setups": [                         // exactly one each of tier budget, mid, endgame
    {
      "id": "budget", "tier": "budget", "name": "...",
      "items": [ { "slot": "helmet", "tag": "ITEM_TAG", "name": "...", "reason": "+X Farming Fortune",
                   "priceFilters": { "PetLevel": "100" },       // optional (variant priced items)
                   "tradeable": true } ],                       // optional, false = soulbound: listed, not costed
      "stats": { "Farming Fortune": 150, "Magic Find": 0 },     // stat totals of the whole setup
      "runSeconds": null,             // runTime only (required there)
      "unlocksTask": null,            // tierUnlock only (required on budget/mid/endgame): the tier this gear is for
      "sources": [], "assumptions": []
    }
  ],
  "bindings": [                       // tasks (MethodTask names, e.g. "Wheat Farming") the profile applies to
    {
      "task": "Wheat Farming",
      "itemOverrides": { "tool": { "tag": "...", "name": "...", "reason": "..." },  // every setup
                         "endgame:tool": { "tag": "...", "name": "...", "reason": "..." } }, // one setup, wins
      "rareDropTags": ["ITEM_TAG"],   // drops that scale with magic find instead of fortune
      "modelOverrides": { "blockHardness": 50 },
      "statOverrides": { "endgame": { "Farming Fortune": 1500 } }
    }
  ]
}
```

Item `name` must be the item's display name in NotEnoughUpdates-REPO (pets may add "Pet" or a "(Rarity, level N)"
note, scrolls a "Scroll" suffix); the catalog tests compare them.

Rules: setup ids unique (baseline included), item slots unique per setup (after overrides), every item has
slot/tag/name/reason, every stat a model parameter names must be listed in every setup (0 is fine).

## Rate models (`model.kind`)

Defaults in brackets; every number is a parameter so the dossier's values win.

| kind | required | optional |
| --- | --- | --- |
| `fortune` | `fortuneStat` | `fortuneDivisor` [100], `rareStat`, `rareDivisor` [100], `formulaSource` |
| `mining` | `speedStat`, `fortuneStats` (list, summed; `[]` = no fortune applies; or a single `fortuneStat`), `blockStrength` | `breakTickFactor` [30], `minTicks` [4, ≥ 1], `instamineFactor` [none; 1 tick when speed > factor × strength], `overheadTicks` [0], `ticksPerHour` [72000], `fortuneDivisor` [100], `spreadStat`, `spreadDivisor` [100], `pristineStat`, `pristineFactor` [0.79], `chanceStat`, `chanceDivisor` [100], `rareChanceStat`, `rareChanceDivisor` [100], `rareStat`, `rareDivisor` [100], `formulaSource` |
| `fishing` | `fishingSpeedStat`, `seaCreatureChanceStat`, `baseWaitSeconds` | `reelSeconds` [0], `fishingSpeedCap` [none], `sccCap` [100], `killSecondsPerCreature` [0], `fishingSpeedDivisor` [100], `seaCreatureChanceDivisor` [100], `bulkFrom` [creatures] or catches, `rareStat`, `rareDivisor` [100], `formulaSource` |
| `runTime` | `runSeconds` on every setup | `overheadSeconds` [0], `mfAffected` [false], `rareStat` (required when mfAffected), `rareDivisor` [100], `formulaSource` |
| `tierUnlock` | profile `tiers`; `unlocksTask` on budget, mid and endgame, climbing the ladder | none (no `runSeconds`, no baseline `unlocksTask`) |
| `fixed` | `multipliers`: `{ "baseline": 1, "budget": 1, "mid": 1.2, "endgame": 1.5 }` | |

Mining: ticks = max(minTicks, round(breakTickFactor × strength / speed)), BPH = ticksPerHour / (ticks + overheadTicks),
bulk = BPH × (1 + Σfortune/100) × (1 + spread/100) × pristine × chance with
pristine = 1 + pristineFactor × Pristine (gemstones, Pristine in stat points) and chance = chanceStat/100
(scales bulk), and rare = BPH × rareChanceStat/100 × (1 + MF/100) when `rareChanceStat` is set (e.g. treasure chest
loot, listed in the binding's `rareDropTags`). The anchor setup must give positive, finite factors (e.g. chance > 0 when chanceStat is set).
A parameter a kind does not list is an error, so typos and leftovers fail validation.

tierUnlock: every bound task and every `unlocksTask` must be in `tiers`. On the page of the task at ladder index i
the baseline earns that task's community rate; a setup whose `unlocksTask` is at index ≤ i is `reached` (gear
typically used there, no Δ), a higher one is an `upgrade` earning the unlocked task's community rate.

`formulaSource` is an https link to where the formula is documented; with it fortune/mining/fishing estimates
count as high model confidence, without it medium. `modelConfidence` can only lower that.
