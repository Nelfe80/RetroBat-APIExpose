# MEM Nomenclature Specification

## Overview

This document defines the complete naming, structure, and normalization rules for `.MEM` files used to describe game memory variables, derived gameplay events, state changes, ROM identity, and system targeting across multiple platforms.

It is written in an API-style format, but it is a nomenclature specification.

The goal is to ensure that all generated `.MEM` files are:

- consistent across systems
- machine-readable
- human-readable
- predictable for tooling
- stable over time
- suitable for automated parsing and runtime event monitoring

---

## 1. Scope

This specification applies to all `.MEM` files generated for supported games and systems.

It defines:

- file structure
- header structure
- ROM identity rules
- system identity rules
- variable naming conventions
- event naming conventions
- state naming conventions
- section ordering
- field ordering
- type normalization
- condition normalization
- mapping normalization
- fallback rules
- classification rules
- rejected and discouraged naming forms

---

## 2. Design Principles

### 2.1 Primary goals

The nomenclature must:

1. prioritize gameplay meaning over raw source wording
2. keep raw memory data usable without polluting gameplay-facing semantics
3. distinguish variables, states, and events
4. normalize synonymous concepts into stable names
5. support multi-system ROM identity through hashes and RetroBat folder targeting
6. remain extensible without breaking older files

### 2.2 General philosophy

- A memory variable is not necessarily an event.
- A state is not necessarily a resource.
- A technical structure is not necessarily gameplay-relevant.
- A file must preserve useful information even when categorization confidence is low.
- Unknown but valid memory should be preserved under a technical fallback namespace.

---

## 3. File Format

Each `.MEM` file must be a valid Lua table returned by the file.

### 3.1 Required top-level structure

```lua
return {
  game = { ... },
  rom = { ... },
  memory = { ... },
  events = { ... }
}
```

### 3.2 Top-level sections

The following top-level sections are allowed:

- `game`
- `rom`
- `memory`
- `events`

No other top-level section should be introduced unless the specification is revised.

---

## 4. `game` Header

The `game` block identifies the logical game entity.

### 4.1 Structure

```lua
game = {
  title = "Super Mario Bros.",
  system = "nes",
  system_name = "NES/Famicom",
  game_id = 1446
}
```

### 4.2 Fields

#### `title`
- type: string
- required
- human-readable title of the game
- should preserve canonical punctuation when known

#### `system`
- type: string
- required
- must use the RetroBat folder name
- this is the primary system key for storage and lookup

Examples:
- `nes`
- `snes`
- `gba`
- `gb`
- `mame`
- `megadrive`
- `mastersystem`
- `psx`

#### `system_name`
- type: string or `nil`
- recommended
- human-readable system name

Examples:
- `NES/Famicom`
- `Super Nintendo`
- `Arcade`

#### `game_id`
- type: integer or `nil`
- optional
- source database identifier if available

---

## 5. `rom` Header

The `rom` block identifies the technical ROM target(s).

### 5.1 Structure

```lua
rom = {
  name = "super_mario_bros",
  source_name = nil,
  hashes = {
    {
      hash = "8e3630186e35d477231bf8fd50e54cdd",
      label = "Super Mario Bros. (World).nes",
      tags = { "nointro" }
    }
  }
}
```

### 5.2 Fields

#### `name`
- type: string
- required
- normalized ROM identity used by the `.MEM` file
- snake_case only
- lowercase only
- no extension
- no spaces
- no accents

#### `source_name`
- type: string or `nil`
- optional
- raw source ROM name if available from metadata

#### `hashes`
- type: array
- recommended
- list of compatible ROM binary identities

### 5.3 Hash entry structure

```lua
{
  hash = "8e3630186e35d477231bf8fd50e54cdd",
  label = "Super Mario Bros. (World).nes",
  tags = { "nointro" }
}
```

#### `hash`
- type: string
- required inside a hash entry
- hash string exactly as provided by the source database

#### `label`
- type: string or `nil`
- optional
- human-readable ROM filename or variant label

#### `tags`
- type: array of strings
- optional
- source tags such as:
  - `nointro`
  - `redump`
  - `retrobat`
  - `translated`
  - `prototype`
  - `beta`

### 5.4 Header naming rules

- `system` must always reflect the RetroBat folder, not the source wording.
- `name` must remain stable across documentation updates.
- `hashes` may include multiple variants for the same game.
- Hash identity should never replace gameplay identity.

---

## 6. `memory` Section

The `memory` block describes observed variables and technical memory entries.

### 6.1 Structure

```lua
memory = {
  variables = {
    lives = {
      address = 0x075A,
      type = "u8",
      desc = "Player lives"
    }
  }
}
```

### 6.2 Allowed sub-sections

- `variables`

No other sub-section is currently standardized.

---

## 7. `memory.variables`

`memory.variables` defines base observable variables.

These are raw or semi-normalized memory variables used as the source layer for higher-level event generation.

### 7.1 Variable structure

```lua
lives = {
  address = 0x075A,
  type = "u8",
  desc = "Player lives"
}
```

### 7.2 Allowed fields

- `address` (required)
- `type` (required)
- `desc` (required)
- `map` (optional)
- `format` (optional)
- `no_log` (optional)

### 7.3 Purpose

Variables should describe:
- current values
- mode registers
- counters
- player properties
- progression fields
- inventory slots
- internal flags
- technical values when still useful

Variables should not try to narrate change.

Example:
- valid variable description: `Player lives`
- invalid variable description: `Player loses a life`

---

## 8. `events` Section

The `events` block defines interpreted gameplay events, state changes, flow transitions, and resource updates.

### 8.1 Required structure

```lua
events = {
  flow = { ... },
  progression = { ... },
  resources = { ... },
  inventory = { ... },
  combat = { ... },
  scoring = { ... },
  state = { ... },
  system = { ... }
}
```

### 8.2 Allowed event families

- `flow`
- `progression`
- `resources`
- `inventory`
- `combat`
- `scoring`
- `state`
- `system`

---

## 9. Event Entry Format

Each event entry must be a Lua table.

### 9.1 Canonical event entry

```lua
{ address=0x075A, type="u8", condition="decrease", desc="Player loses a life" }
```

### 9.2 Allowed fields

#### Required
- `address`
- `type`
- `condition`
- `desc`

#### Optional
- `min`
- `max`
- `map`
- `factor`
- `is_score`
- `format`
- `no_log`
- `no_survey`

### 9.3 Canonical field order

1. `address`
2. `type`
3. `condition`
4. `min`
5. `max`
6. `desc`
7. `map`
8. `factor`
9. `is_score`
10. `format`
11. `no_log`
12. `no_survey`

---

## 10. Primitive Types

### 10.1 Allowed types

Unsigned:
- `u8`
- `u16be`
- `u16le`
- `u24be`
- `u24le`
- `u32be`
- `u32le`

Signed:
- `s8`
- `s16be`
- `s16le`
- `s32be`
- `s32le`

### 10.2 Type rules

- `u8` is the default fallback when an entry is clearly byte-sized and no better typing is available.
- Multi-byte types must only be used if width and endian are known or reliably inferred.
- Unknown endian multi-byte fields should not be guessed aggressively.

---

## 11. Conditions

### 11.1 Allowed conditions

- `change`
- `increase`
- `decrease`
- `equal`
- `any`

### 11.2 Meaning

#### `change`
Use when a value changes without reliable directional semantics.

Examples:
- current level
- game mode
- room id
- player state

#### `increase`
Use when a value is expected to rise meaningfully.

Examples:
- score
- coins
- rings
- experience
- combo count

#### `decrease`
Use when a value is expected to fall meaningfully.

Examples:
- lives
- health
- timer
- oxygen
- ammo if consumed

#### `equal`
Use when a specific state or threshold is the event trigger.

Examples:
- title screen active
- invincibility active
- boss defeated flag
- continue screen shown

#### `any`
Use only as a fallback when the observation is useful but not semantically directional.

---

## 12. `map` Normalization

`map` translates numeric values into human-readable semantics.

### 12.1 Structure

```lua
map = {
  [0] = "off",
  [1] = "on"
}
```

### 12.2 Rules

- keys must be numeric
- values must be strings
- values must be short, stable, lowercase where appropriate
- state values should prefer canonical terminology

### 12.3 Preferred canonical values

Binary:
- `off`
- `on`

State:
- `none`
- `idle`
- `jumping`
- `falling`
- `hurt`
- `dying`
- `dead`
- `paused`
- `active`

Powerups:
- `small`
- `big`
- `fire`
- `super`
- `hyper`
- `shield`
- `invincible`

Flow:
- `boot`
- `attract`
- `title`
- `menu`
- `options`
- `in_game`
- `pause`
- `game_over`
- `continue`
- `ending`
- `credits`

---

## 13. Description (`desc`) Rules

### 13.1 General rules

- English only
- concise
- gameplay-oriented when possible
- Title Case or sentence-style stability must be consistent across the project; recommended style is sentence-like with first capital letter only
- no trailing period
- no address in the text
- no source markup
- no HTML remnants
- no parenthetical noise unless necessary for disambiguation

### 13.2 Good examples

- `Player lives`
- `Current level`
- `Collected rings`
- `Invincibility active`
- `Boss hit counter`
- `Player powerup state`

### 13.3 Bad examples

- `ram address for number of lives`
- `Current amount of player's remaining lives.`
- `0x075A - Lives`
- `Player lives (actual current variable)`

---

## 14. Section Ordering

### 14.1 Top-level order

1. `game`
2. `rom`
3. `memory`
4. `events`

### 14.2 `events` family order

1. `flow`
2. `progression`
3. `resources`
4. `inventory`
5. `combat`
6. `scoring`
7. `state`
8. `system`

---

## 15. `flow` Event Family

`flow` describes where the player is in the game lifecycle.

### 15.1 Canonical sub-keys

Allowed normalized keys:
- `boot`
- `attract_mode`
- `intro`
- `title_screen`
- `main_menu`
- `options_menu`
- `save_menu`
- `load_menu`
- `character_select`
- `file_select`
- `map_screen`
- `pause`
- `in_game`
- `continue_screen`
- `game_over`
- `ending`
- `credits`
- `demo_play`
- `settings`

### 15.2 Examples

```lua
flow = {
  title_screen = {
    { address=0x0010, type="u8", condition="equal", min=1, max=1, desc="Title screen" }
  },
  in_game = {
    { address=0x0010, type="u8", condition="equal", min=4, max=4, desc="Gameplay active" }
  }
}
```

---

## 16. `progression` Event Family

`progression` describes content traversal.

### 16.1 Canonical sub-keys

Allowed normalized keys:
- `world`
- `zone`
- `level`
- `act`
- `stage`
- `area`
- `room`
- `mission`
- `chapter`
- `map`
- `floor`
- `checkpoint`
- `lap`
- `quest`

### 16.2 Normalization rules

Normalize source terms as follows:
- `Current phase`, `chapter`, `episode` → best matching canonical progression key
- `Dungeon room`, `current room` → `room`
- `Current map`, `overworld map` → `map`
- `Stage number`, `round`, `scene` → `stage`

### 16.3 Examples

```lua
progression = {
  world = {
    { address=0x075F, type="u8", condition="change", desc="Current world" }
  },
  level = {
    { address=0x0760, type="u8", condition="change", desc="Current level" }
  }
}
```

---

## 17. `resources` Event Family

`resources` covers values that the player gains, loses, consumes, or refills.

### 17.1 Canonical sub-keys

Allowed normalized keys:
- `lives`
- `health`
- `energy`
- `hp`
- `mp`
- `stamina`
- `ammo`
- `magic`
- `air`
- `oxygen`
- `timer`
- `countdown`

### 17.2 Normalization rules

Preferred mappings:
- `Life`, `Lives`, `Current lives` → `lives`
- `HP`, `Current HP`, `Health` → `health` unless an RPG convention requires `hp`
- `MP`, `Mana`, `Magic points` → `mp`
- `Air`, `Oxygen`, `Breath` → `oxygen` or `air`

### 17.3 Examples

```lua
resources = {
  lives = {
    { address=0x075A, type="u8", condition="decrease", desc="Player loses a life" }
  },
  health = {
    { address=0x00A0, type="u8", condition="decrease", desc="Player takes damage" },
    { address=0x00A0, type="u8", condition="increase", desc="Player recovers health" }
  }
}
```

---

## 18. `inventory` Event Family

`inventory` covers collectible, held, equipped, and usable objects.

### 18.1 Canonical sub-keys

Allowed normalized keys:
- `inventory`
- `items`
- `keys`
- `equipment`
- `weapon`
- `armor`
- `powerup`
- `quest_items`
- `held_object`

### 18.2 Normalization rules

- Held but not persistent objects should prefer `held_object`.
- Inventory tables should prefer `items` or `inventory` depending on source detail.
- Equipment slots should prefer `equipment`, with `weapon` or `armor` when explicit.

### 18.3 Examples

```lua
inventory = {
  held_object = {
    { address=0x001D, type="u8", condition="change", desc="Held object changed" }
  },
  keys = {
    { address=0x0310, type="u8", condition="increase", desc="Key obtained" }
  }
}
```

---

## 19. `combat` Event Family

`combat` covers offensive and defensive interactions.

### 19.1 Canonical sub-keys

Allowed normalized keys:
- `enemy_state`
- `boss_state`
- `boss_hit`
- `damage_taken`
- `damage_dealt`
- `weapon_charge`
- `shield_state`
- `invulnerability_frames`

### 19.2 Examples

```lua
combat = {
  boss_hit = {
    { address=0x0400, type="u8", condition="increase", desc="Boss hit count" }
  },
  enemy_state = {
    { address=0x0410, type="u8", condition="change", desc="Enemy state changed", no_log=true }
  }
}
```

---

## 20. `scoring` Event Family

`scoring` covers performance and reward values.

### 20.1 Canonical sub-keys

Allowed normalized keys:
- `score`
- `coins_rings`
- `bonus`
- `currency`
- `experience`
- `combo`
- `chain`
- `multiplier`

### 20.2 Normalization rules

- `Coins`, `rings`, or similar arcade pick-ups should prefer `coins_rings` unless the game clearly uses a money economy.
- `Gold`, `money`, `gil`, `rupees`, `zenny` should normalize to `currency` unless game-specific tooling requires otherwise.
- `EXP`, `XP`, `experience points` → `experience`.

### 20.3 Examples

```lua
scoring = {
  score = {
    { address=0x0840, type="u24be", condition="increase", desc="Score increased", is_score=true }
  },
  coins_rings = {
    { address=0x075E, type="u8", condition="increase", desc="Collected rings" }
  }
}
```

---

## 21. `state` Event Family

`state` is the normalized namespace for player forms, temporary effects, status conditions, and general state machines.

This family must be highly standardized.

### 21.1 Canonical sub-keys

Allowed normalized keys:
- `player_state`
- `powerup_state`
- `temporary_state`
- `status_effect`
- `mode_state`
- `game_state`
- `vehicle_state`
- `environment_state`

### 21.2 `player_state`

Use for current player action or posture states.

Examples:
- idle
- walking
- running
- jumping
- falling
- crouching
- climbing
- swimming
- hurt
- dying
- dead
- stunned

Example:

```lua
player_state = {
  { address=0x1234, type="u8", condition="change", desc="Player state", no_log=true }
}
```

### 21.3 `powerup_state`

Use for durable or semi-durable player form changes.

Examples:
- small
- big
- fire
- cape
- raccoon
- super
- hyper
- armored

Example:

```lua
powerup_state = {
  { address=0x0756, type="u8", condition="change", desc="Player powerup state", map={
    [0]="small",
    [1]="big",
    [2]="fire"
  } }
}
```

### 21.4 `temporary_state`

Use for temporary, time-limited, or toggle-like player conditions.

Examples:
- invincible
- underwater
- shielded
- speed_boost
- intangible
- flashing
- star_power

Recommended normalized descriptions:
- `Invincibility active`
- `Shield active`
- `Underwater state`
- `Speed boost active`

Example:

```lua
temporary_state = {
  { address=0x079F, type="u8", condition="equal", min=1, max=1, desc="Invincibility active", no_log=true }
}
```

### 21.5 `status_effect`

Use for RPG-like or explicit status ailment systems.

Examples:
- poison
- burn
- freeze
- sleep
- curse
- silence
- haste
- slow

Example:

```lua
status_effect = {
  { address=0x4000, type="u8", condition="change", desc="Status effect changed" }
}
```

### 21.6 `mode_state`

Use for gameplay mode sub-states not covered by flow.

Examples:
- overworld mode
- battle mode
- puzzle mode
- vehicle mode

### 21.7 `game_state`

Use for internal or high-level game state machines when meaningful to runtime tooling.

Examples:
- active gameplay state
- loading state
- scripted state

---

## 22. `system` Event Family

`system` stores technical or low-semantic entries that still matter.

### 22.1 Canonical sub-keys

Allowed normalized keys:
- `memory`
- `flags`
- `prng`
- `internal_state`
- `debug`
- `input`
- `display`

### 22.2 Rules

Use `system` only when the value is:
- valid
- potentially useful
- not clearly gameplay-facing

Examples:
- PRNG
- technical counters
- unknown but stable flags
- internal state machine values

---

## 23. Global Variable and Event Name Dictionary

This section defines normalized names that should be preferred whenever matching source descriptions.

### 23.1 Progression dictionary

| Source patterns | Normalized key |
|---|---|
| world | `world` |
| zone | `zone` |
| act | `act` |
| stage, round, scene | `stage` |
| area | `area` |
| room | `room` |
| chapter | `chapter` |
| floor | `floor` |
| checkpoint | `checkpoint` |
| lap | `lap` |
| mission | `mission` |
| map | `map` |

### 23.2 Resource dictionary

| Source patterns | Normalized key |
|---|---|
| life, lives | `lives` |
| health | `health` |
| hp | `hp` or `health` |
| mp, mana | `mp` |
| stamina | `stamina` |
| ammo, bullets, shots | `ammo` |
| oxygen, air, breath | `oxygen` or `air` |
| timer, time left | `timer` |
| countdown | `countdown` |

### 23.3 Scoring dictionary

| Source patterns | Normalized key |
|---|---|
| score, points | `score` |
| coin, ring | `coins_rings` |
| gold, money, gil, rupees | `currency` |
| exp, xp, experience | `experience` |
| bonus | `bonus` |
| combo | `combo` |
| chain | `chain` |
| multiplier | `multiplier` |

### 23.4 Inventory dictionary

| Source patterns | Normalized key |
|---|---|
| inventory | `inventory` |
| item | `items` |
| key | `keys` |
| weapon | `weapon` |
| armor, armour | `armor` |
| equipment, equipped | `equipment` |
| held object | `held_object` |
| powerup | `powerup` |

### 23.5 State dictionary

| Source patterns | Normalized key |
|---|---|
| player state, status | `player_state` |
| form, powerup, transformation | `powerup_state` |
| invincible, shield, star, underwater | `temporary_state` |
| poison, sleep, burn, curse | `status_effect` |
| mode | `mode_state` |
| game state | `game_state` |
| vehicle state | `vehicle_state` |

---

## 24. Standardized Event Semantics

This section defines common normalized event intents.

### 24.1 Resource event patterns

#### Lives
- variable: `Player lives`
- decrease event: `Player loses a life`
- increase event: `Player gains a life`

#### Health
- variable: `Player health`
- decrease event: `Player takes damage`
- increase event: `Player recovers health`

#### Score
- variable: `Player score`
- event: `Score increased`

#### Coins/Rings
- variable: `Collected rings` or `Collected coins`
- increase event: `Collected rings`
- decrease event: `Rings lost`

### 24.2 Progression event patterns

#### Level/Stage/Room
- variable: `Current level`, `Current stage`, `Current room`
- event: `Current level`, `Current stage`, `Current room`
- condition: `change`

### 24.3 State event patterns

#### Invincibility
- section: `state.temporary_state`
- preferred desc: `Invincibility active`
- preferred condition: `equal`
- map or threshold should resolve active/inactive clearly

#### Powerup form
- section: `state.powerup_state`
- preferred desc: `Player powerup state`
- preferred condition: `change`

#### Hurt/Dying action state
- section: `state.player_state`
- preferred desc: `Player state`
- preferred condition: `change`

---

## 25. Fallback Strategy

### 25.1 If a valid address exists but gameplay classification is unclear
Preserve it under:
- `memory.variables`
- and optionally `events.system.memory` if runtime observation is still useful

### 25.2 If a page only exposes technical values
Generate a `.MEM` anyway if addresses are valid.

### 25.3 If parsing succeeds but no canonical event family fits
Use the nearest family or fallback to `system.memory`.

### 25.4 If an entry is pure PRNG or heap data
Preserve only if useful to advanced tooling; otherwise omit from `events` but it may remain in `memory.variables`.

---

## 26. Noise Rejection Rules

The following should not be promoted to gameplay-facing sections unless there is a clear reason:

- heap markers
- buffer markers
- unknown pointer tables
- raw object structure offsets with no semantic labeling
- duplicate display mirrors when an authoritative variable exists

These can remain in:
- `memory.variables`
- `events.system.memory`

---

## 27. Duplicate Resolution Rules

When multiple source entries point to similar meanings:

1. prefer the most authoritative variable
2. prefer gameplay value over display mirror
3. prefer the clearest source description
4. preserve both only if one is display and one is actual game logic

Examples:
- `Lives display` and `Lives actual` may coexist if both are useful
- `Displayed score` and `Internal score` may coexist if the runtime needs both

---

## 28. Naming Style Rules

### 28.1 All keys
- lowercase only
- snake_case only
- no spaces
- no punctuation other than underscore

### 28.2 Allowed examples
- `player_state`
- `temporary_state`
- `coins_rings`
- `boss_hit`
- `game_over`

### 28.3 Forbidden examples
- `playerState`
- `PlayerState`
- `coins/rings`
- `game-over`

---

## 29. Example Full File

```lua
return {
  game = {
    title = "Super Mario Bros.",
    system = "nes",
    system_name = "NES/Famicom",
    game_id = 1446
  },

  rom = {
    name = "super_mario_bros",
    source_name = nil,
    hashes = {
      {
        hash = "8e3630186e35d477231bf8fd50e54cdd",
        label = "Super Mario Bros. (World).nes",
        tags = { "nointro" }
      }
    }
  },

  memory = {
    variables = {
      lives = {
        address = 0x075A,
        type = "u8",
        desc = "Player lives"
      },
      world = {
        address = 0x075F,
        type = "u8",
        desc = "Current world"
      },
      powerup_state = {
        address = 0x0756,
        type = "u8",
        desc = "Player powerup state"
      }
    }
  },

  events = {
    flow = {
      in_game = {
        { address=0x0010, type="u8", condition="equal", min=4, max=4, desc="Gameplay active" }
      }
    },

    progression = {
      world = {
        { address=0x075F, type="u8", condition="change", desc="Current world" }
      },
      level = {
        { address=0x0760, type="u8", condition="change", desc="Current level" }
      }
    },

    resources = {
      lives = {
        { address=0x075A, type="u8", condition="decrease", desc="Player loses a life" }
      },
      timer = {
        { address=0x07F8, type="u8", condition="decrease", desc="Timer countdown", no_log=true }
      }
    },

    scoring = {
      coins_rings = {
        { address=0x075E, type="u8", condition="increase", desc="Collected coins" }
      }
    },

    state = {
      powerup_state = {
        { address=0x0756, type="u8", condition="change", desc="Player powerup state", map={
          [0]="small",
          [1]="big",
          [2]="fire"
        } }
      },
      temporary_state = {
        { address=0x079F, type="u8", condition="equal", min=1, max=1, desc="Invincibility active", no_log=true }
      }
    },

    system = {
      memory = {
        { address=0x00FF, type="u8", condition="change", desc="PRNG value", no_log=true }
      }
    }
  }
}
```

---

## 30. Runtime API Behavior Flags

To prevent overloading the emulator runtime or the networking output, the following flags can be used to control how and when events are observed.

### 30.1 `no_log = true`
Use `no_log` on events that must be tracked internally by the API but should not spam text consoles or default log streams when their value changes rapidly.
- Recommended for: `timer`, `countdown`, `player_state` (animations).

### 30.2 `no_survey = true`
Use `no_survey` for verbose, low-priority payloads that the emulator should completely skip reading by default. The API core will ignore this memory address unless a remote system explicitly requests it.
- Recommended for: `player_state` (animations), `Demo state`, `Pause state`, secondary timers.

---

## 31. ROM Dictionary Aliasing (`alias.json`)

To centralize `.MEM` files (e.g., `sonic_the_hedgehog.MEM`) without renaming them for each region or romhack, a system folder should contain an `alias.json` file mapping raw ROM filenames to their canonical `.MEM` name.

### 31.1 Structure
```json
{
  "Sonic The Hedgehog (USA, Europe)": "sonic_the_hedgehog",
  "Sonic The Hedgehog (Japan)": "sonic_the_hedgehog",
  "Sonic 1 - Boomed (QoL Fix)": "sonic_the_hedgehog"
}
```

---

## 32. Runtime ACTION Event Mapping

To simplify external hardware and frontend integration, the Lua API automatically translates categorized `.MEM` memory changes into standard universal `ACTION` string payloads. The mapping connects the raw memory category (`category.event`) plus the logic direction (e.g., `increase` vs `decrease`) into single commands.

### 32.1 Standard Mapped Actions

| UDP Payload Key | Value | Trigger Conditions (from `.MEM` category) | Use Cases |
|---|---|---|---|
| `ACTION:` | `1UP` | `[POSITIVE] lives` | The player gains an extra life. Flash Green / Play 1UP sound. |
| `ACTION:` | `DEAD` | `[NEGATIVE] lives` or `[CRITICAL] health` | The player dies. The `CRITICAL` tag is emitted automatically when a health value drops to 0. Flash Red / Play Death sound. |
| `ACTION:` | `HEAL` | `[POSITIVE] health` or `[POSITIVE] hp` or `[POSITIVE] energy` | Player restores some health or energy. |
| `ACTION:` | `HIT` | `[NEGATIVE] health` or `[NEGATIVE] hp` or `[NEGATIVE] energy` | Player takes damage without dying. Flash Red / Shaker motor. |
| `ACTION:` | `BOSS_HIT` | `[NEGATIVE] boss_hit` | Enemy or Boss takes damage. Flash White / Short Shaker motor. |
| `ACTION:` | `BOSS_HEAL`| `[POSITIVE] boss_hit` | Enemy or Boss regenerates energy. |
| `ACTION:` | `ITEM_GET` | `[POSITIVE] inventory.*` family | Player picks up an item, key, or weapon. Play item collect sound. |
| `ACTION:` | `ITEM_USE` | `[NEGATIVE] inventory.*` family | Player drops, loses, or consumes an item (e.g. Bombs in Arcade). Play explosion/use sound. |
| `ACTION:` | `SCORE` | `scoring.*` family or `is_score=true` flags | Points or currency collected. Can flash score digits or strobe lights. |
| `STATE:` | `NEW_LEVEL`| `stage` or `level` variable changes | Loading into a new level, act, or zone. Lifecycle event. |
| `STATE:` | `TITLE_SCREEN`| `flow.*` with map values containing 'title', 'menu', 'select' | Transitions to Title screens / Main Menus. |
| `STATE:` | `DEMO_MODE`| `flow.*` with map values containing 'demo', 'attract', 'intro' | Transitions to Demo or Attract Mode. |
| `STATE:` | `GAMEPLAY`| `flow.*` with map values containing 'game', 'play', 'normal' | Transitions to standard active Gameplay. |
| `STATE:` | `GAME_OVER`| `flow.*` with map values containing 'over', 'end', 'credit' | Transitions to Game Over or Ending sequences. |
| `STATE:` | `PAUSED`| `flow.*` with map values containing 'pause' | Transitions to Paused state. |
| `ANIM:` | `{custom verb}`| `state.*` or `player_state` combined with `action_map` | Character animations (JUMP, RUN, CROUCH) if exposed in UDP. |
| `ACTION:` | `UPDATE` | All other variables | Generic status update triggers (e.g., Invincibility flag changed). |

### 32.2 Standard Generic Overrides (`action=` and `action_map=`)

To push generic universal behaviors across completely different games (like Sonic's Speed Shoes or a Mario Star), creators should explicitly use the `action` or `action_map` attributes inside their `.MEM` definitions. **These are the official generic terms to use when overriding states, grouped by family:**

#### A. Core Progression & Global States (Mapped to `STATE:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `CORPORATE_SCREEN` | Boot logo (Sega, Capcom, SNK) | Fades to Corporate colors (e.g., Sega Blue) |
| `TITLE_SCREEN` | Main Menu / Title Screen | Attract Mode lighting / Ambient music loop |
| `DEMO_MODE` | Attract Mode / CPU Gameplay Playback | Muted inputs / Light show |
| `GAME_PLAYING` | Active standard gameplay | Playfield fully lit |
| `GAME_OVER` | Game Over / Continue prompt | Slow Red pulse / Somber lighting |
| `CREDITS` | Game credits rolling | Theatrical lighting / Celebration |
| `PAUSE_ON` / `PAUSE_OFF` | Player hits Start/Pause | Playfield dims / Playfield restores |
| `LEVEL_CLEAR` | Act, Zone, or Match finished | Fanfare lighting / Fireworks |
| `QUEST_COMPLETE` | Talked to NPC / RPG Quest advance | Short happy chime / Ambient flash |
| `SETTINGS_CHANGED` | Sound, Difficulty or Language adjusted | Subtle generic tick sound / Menu flash |

#### B. Resources & Survival (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `LOSE_LIFE` | Explicit override for Death/Life loss | Flash Red / Death sound |
| `GAIN_LIFE` | Explicit override for 1UP/Extend | Flash Green / 1UP Chime |
| `LOSE_HEALTH` / `HIT` | Non-lethal damage taken | Short Shaker Motor / Red Strobe |
| `GAIN_HEALTH` / `HEAL` | Health or Energy recovered | Soft Green/Blue glow |
| `LOW_HEALTH_WARN` | Health drops below critical threshold | Continuous Red heartbeat pulse |
| `TIMER_LOW` | Time almost out (e.g., 10 seconds left) | Fast Yellow blinking / Ticking track |
| `DROWNING` | Underwater air running out | Blue ambient pulse accelerating |

#### C. Power-ups & Temporary States (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `INVINCIBILITY_START` | Star / Invincibility mode starts | Strobes pulsate White/Gold continuously |
| `INVINCIBILITY_STOP` | Immortality buff ends | Strobes turn OFF |
| `SPEED_START` | Speed Shoes / Boost powerup acquired | Fan turns ON, RGB strips accelerate |
| `SPEED_STOP` | Temporary speed buff ends | Fan turns OFF |
| `SHIELD_GAIN` | Character acquires an extra-hit shield | Single Green LED sweep / Shield sound |
| `SHIELD_LOST` | Character loses their shield | Single Red LED sweep |

#### D. Collectibles & Inventory (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `RING_GAIN` / `COIN_GAIN` | Currency picked up (Use for discrete items) | Coin chink sound / Yellow LED blink |
| `RING_LOSE` / `COIN_LOSE` | Currency lost/dropped (e.g., Sonic hit) | Ring scatter sound / Heavy Shaker |
| `TREASURE` | Big collectible (Emerald, Key, Triforce) | Sustained Gold flash / Epic Fanfare |
| `WEAPON_UPGRADE` | Firepower increased (e.g., Shmup laser) | Aggressive White flash / Power-up sound |
| `BOMB_FIRED` | Screen-clearing item used (e.g., 1944) | Mega Solenoid fire / Blinding Flash |

#### E. Action & Combat (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `FIRE_SIDEARM` | Auxiliary gun fired | Small Solenoid click / Pistol flash |
| `BATTLE_START` | Random encounter or arena fight begins | Sharp Red strobe / Aggressive tone |
| `BATTLE_END` | Combat resolves | Lighting fades back to normal |
| `COMBO_HIT` | Fighting game combo counter increase | Strobe scales with combo length |
| `FATALITY` | Mortal Kombat / Finishing move | Dark Red/Black lighting |
| `BOSS_HIT` | Boss enemy takes damage | White Strobe to signify enemy hurt |
| `BOSS_DEFEATED` | Boss dies | Massive Shaker / Explosions |

#### F. Racing & Vehicles (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `LAP_COMPLETE` | Crossed the finish line / start line | Chequered flag lighting / Roar |
| `GEAR_SHIFT` | Upshift or Downshift | Force feedback clunk / Solenoid |
| `TURBO_BOOST` | Nitro or Boost pad hit | Fan max speed / Backward LED sweep |
| `CRASH` / `COLLISION` | Car hits wall or another car | Extremely heavy Shaker / Flash Red |

#### G. Hardware & Interaction (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `KEY_PRESSED` | Raw button or pad input detected | Haptic feedback click / Very short flash |
| `CAMERA_MOVE` | Screen, Map, or display coordinates change | Subtle positional pan (Audio/Illumination) |

#### H. Cutscenes & Interactivity (Mapped to `STATE:` or `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `CINEMATIC_PLAYING` | Non-interactive story sequence starts | Lights dim / Theatrical ambiance |
| `CINEMATIC_END` | Cinematics finish | Playfield restores to active |
| `DIALOGUE_SCENE` | Characters conversing starts | Soft steady front light / Focus effect |
| `DIALOGUE_END` | Characters conversing ends | Ambient lighting restored |
| `CHOICE_PROMPT` | Waiting for player narrative choice | Alternating buttons blinking |
| `CHOICE_END` | Choice made | Blinking stops / Soft confirmation flash |
| `MAP_VIEWING` | Player is looking at the world/dungeon map | Cool blue glow / Static ambiance |
| `MAP_CLOSED` | Player closes the map | Blue glow fades out |

#### I. Exploration & Secrets (Mapped to `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `DOOR_OPENED` | A physical door is unlocked and opened | Low clunk (shaker/haptic) / Quick flash |
| `CHEST_OPENED` | Loot container or chest opened | Short chime / Twinkling light spread |
| `ROOM_DISCOVERED` | Entering a new unique room/location | Sweeping ambient reveal / Fade up |
| `SECRET_REVEALED` | Hidden path, Warp Zone, or secret found | Tension buildup / Swirling RGB LED |

#### J. Simulation & Environment (Mapped to `STATE:` or `ACTION:`)
| Generic Action String | Context / Source | Expected Pincab Effect |
|---|---|---|
| `DAY_TIME` | Clock moves to day / morning | Warm orange/yellow global ambiance |
| `NIGHT_TIME` | Clock moves to night | Deep purple/dark blue global ambiance |
| `WEATHER_RAIN` | Rain starts | Flickering blue/grey droplets |
| `WEATHER_CLEAR` | Rain/Snow stops | Bright sunshine light / Fade back to normal |
| `CRAFTING_START` | Crafting or cooking minigame starts | Warm workstation light / Anvil clinks |
| `CRAFTING_END` | Crafting finishes | Sparkle effect / Final forge struck |
| `FUNDS_SPENT` | Buying items, real estate, simulation money loss | Cash register sound / Quick Red |
| `FUNDS_GAINED` | Selling items, simulation income | Cash register sound / Quick Green |

*Important Note: When mapping pure character animations (e.g., `JUMP`, `SKID`, `RUN`, `CROUCH`, `SPIN`), use raw uppercase verbs in the `action_map`. They represent specific visceral movements.*

---

## 33. Compliance Checklist

A `.MEM` file is compliant if:

- it uses the canonical top-level structure
- `game.system` uses the RetroBat folder name
- `rom.name` is normalized and stable
- `hashes` preserve variant identity when available
- `memory.variables` describes raw observed variables
- `events` uses only approved event families
- state-related entries are separated into standardized sub-keys
- descriptions are normalized and readable
- field order is canonical
- unknown but valid values are preserved in technical fallback sections instead of being silently discarded

---

## 34. Future Extensions

Possible future standard additions may include:

- `derived` section for composite inferred events
- confidence metadata outside the final runtime `.MEM`
- per-entry source provenance in auxiliary debug files
- schema version field if the ecosystem requires strict migration management

These are not part of the current naming standard.

---

## 35. Final Recommendation

For all new generation logic:

- parse broadly
- normalize aggressively
- preserve useful technical values
- separate variables from interpreted events
- standardize player state, powerups, temporary effects, and status ailments
- target RetroBat folder names for system grouping
- preserve ROM variant identity through hashes

This specification is the authoritative reference for `.MEM` nomenclature.

