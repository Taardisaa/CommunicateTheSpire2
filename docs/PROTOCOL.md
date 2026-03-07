# CommunicateTheSpire2 — Protocol Specification

This document describes the IPC protocol between the CommunicateTheSpire2 mod and an external controller process. The controller observes game state and issues commands to control gameplay.

**Protocol version:** 1  
**Transport:** NDJSON over stdio (one JSON object per line)

---

## 1. Overview

1. **Mod spawns controller** (process with configurable command, e.g. `python -u controller.py`).
2. **Controller prints `ready`** within timeout (default 10s); mod sends `hello`.
3. **Mod sends `state`** snapshots when the game is stable (e.g. after combat actions) or when the controller sends `STATE`.
4. **Controller sends commands** (plain text or JSON) based on `state.available_commands` and the current state.
5. **Mod sends `choice_request`** when the game needs a choice (card reward, card select); controller responds with `CHOOSE_RESPONSE <choice_id> <index>` or `skip`.

**Direction:** Mod writes to controller **stdin**; controller writes to mod via **stdout**; controller **stderr** is logged by the mod.

---

## 2. Transport

- **Mod → controller:** JSON lines to controller's **stdin**
- **Controller → mod:** Plain text or JSON lines to **stdout**
- **Controller stderr:** Mod logs to `communicate_the_spire2_controller_errors.log`

All messages are newline-delimited. Empty lines are ignored.

---

## 3. Handshake

1. Mod spawns controller process.
2. Controller must print `ready\n` within the configured timeout (default 10s).
3. If handshake succeeds, mod sends `hello` and enables protocol handling.
4. If handshake fails, mod terminates the controller.

---

## 4. Game State

The mod exposes game state via the **state** message. The structure depends on the current context (main menu, in run, in combat, etc.).

### 4.1 When state is sent

- **On request:** Controller sends `STATE` → mod replies with state.
- **Automatically:** Mod pushes state when the game becomes "stable" (e.g. combat play phase, no actions running). Debounced (~150ms) to avoid flooding.

### 4.2 State structure

```
state
├── type: "state"
├── timestamp_unix_ms: number
├── in_run: bool          ← true if a run is in progress
├── in_combat: bool       ← true if in combat
├── screen: string | null ← current room/screen (see 4.3)
├── run: object | null    ← present when in_run
├── combat: object | null ← present when in_combat (hand_cards, draw_pile, discard_pile, exhaust_pile, enemies, local_player)
├── event_options: array  ← options when screen = "event"
├── rest_site_options: array ← options when screen = "rest_site"
├── map: object | null    ← present when screen = "map"
├── potions: array        ← local player potions (when in run)
└── available_commands: array ← commands valid right now
```

### 4.3 Screen values

| `screen`   | Meaning        | Relevant state fields                         |
|------------|----------------|-----------------------------------------------|
| `null`     | Main menu      | —                                             |
| `"combat"` | In combat      | `combat` (hand_cards, enemies, local_player)  |
| `"event"`  | Event room     | `event_options`                               |
| `"rest_site"` | Rest site   | `rest_site_options`                           |
| `"map"`    | Map (choose path) | `map` (current_coord, reachable)           |
| `"shop"`   | Shop           | —                                             |
| `"treasure"` | Treasure room | —                                             |
| `"unknown"` | Other        | —                                             |

### 4.4 Top-level fields

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Always `"state"` |
| `timestamp_unix_ms` | number | Unix timestamp (ms) when state was built |
| `in_run` | bool | True if a run is in progress |
| `in_combat` | bool | True if in combat |
| `screen` | string \| null | Current screen (see table above) |
| `run` | object \| null | Run summary; present when `in_run` |
| `combat` | object \| null | Combat summary; present when `in_combat` |
| `event_options` | array | Event options; populated when `screen` = `"event"` |
| `rest_site_options` | array | Rest options; populated when `screen` = `"rest_site"` |
| `map` | object \| null | Map state; populated when `screen` = `"map"` |
| `potions` | array | Local player potion slots; when `in_run` |
| `available_commands` | array | Commands valid in this state (see §6) |

### 4.5 run (when in_run)

| Field | Type | Description |
|-------|------|-------------|
| `act_index` | int | Current act (0-based) |
| `act_floor` | int | Floor within act |
| `total_floor` | int | Total floors in act |
| `ascension` | int | Ascension level |
| `gold` | int | Player gold |
| `room_type` | string | Current room type (e.g. `"Monster"`, `"RestSite"`) |

### 4.6 combat (when in_combat)

| Field | Type | Description |
|-------|------|-------------|
| `round_number` | int | Current combat round |
| `current_side` | string | `"Player"` or `"Enemy"` |
| `local_player` | object | HP, block, energy, stars |
| `enemies` | array | Enemy HP, block, combat_id; intent, move_id, damage, hits |
| `hand_cards` | array | Cards in hand with index, id, energy_cost, target_type, playable |
| `draw_pile` | array | Draw pile (top first); each has `id`, `upgraded` |
| `discard_pile` | array | Discard pile; each has `id`, `upgraded` |
| `exhaust_pile` | array | Exhaust pile; each has `id`, `upgraded` |

**local_player:** `net_id`, `current_hp`, `max_hp`, `block`, `energy`, `stars`

**enemies:** each has `combat_id`, `name`, `current_hp`, `max_hp`, `block`, `intent`, `move_id`, `damage`, `hits`

**hand_cards:** each has `index`, `id`, `energy_cost`, `target_type`, `playable`

**draw_pile / discard_pile / exhaust_pile:** each entry has `id` (card id), `upgraded` (bool). Order: draw_pile top-to-bottom, discard/exhaust as stored.

**enemies (intent):** `intent` = IntentType (Attack, Buff, Debuff, Defend, etc.). `move_id` = move state id. `damage` = total damage from attack intents (0 if not attacking). `hits` = number of hits (1 for single, N for multi-attack).

### 4.7 event_options (when screen = "event")

Each option: `index`, `text_key`, `title`, `is_locked`, `is_proceed`

### 4.8 rest_site_options (when screen = "rest_site")

Each option: `index`, `option_id`, `title`, `is_enabled`  
Typical indices: 0 = Heal, 1 = Smith.

### 4.9 map (when screen = "map")

| Field | Type | Description |
|-------|------|-------------|
| `current_coord` | object | `col`, `row`, `point_type` of current node |
| `reachable` | array | Reachable nodes; each has `col`, `row`, `point_type` |

`reachable` is sorted (col, row). Use index for `MAP_CHOOSE`.

### 4.10 potions (when in_run)

Each slot: `index`, `id`, `target_type` (e.g. `"Self"`, `"AnyEnemy"`)

### 4.11 available_commands

Lists commands valid in this state. Controller should only send listed commands (except `STATE`, `PING`, `CHOOSE_RESPONSE`).

---

## 5. Messages (Mod → Controller)

### hello

Sent once after handshake.

```json
{
  "type": "hello",
  "protocol_version": 1,
  "mod_version": "0.1.0",
  "transport": "stdio",
  "capabilities": []
}
```

### state

See §4. Example:

```json
{
  "type": "state",
  "timestamp_unix_ms": 1710000000000,
  "in_run": true,
  "in_combat": true,
  "screen": "combat",
  "run": {
    "act_index": 0,
    "act_floor": 1,
    "total_floor": 5,
    "ascension": 0,
    "gold": 99,
    "room_type": "Monster"
  },
  "combat": {
    "round_number": 1,
    "current_side": "Player",
    "local_player": {
      "net_id": 1,
      "current_hp": 72,
      "max_hp": 72,
      "block": 0,
      "energy": 3,
      "stars": 0
    },
    "enemies": [
      {
        "combat_id": 1,
        "name": "Cultist",
        "current_hp": 48,
        "max_hp": 48,
        "block": 0,
        "intent": "Attack",
        "move_id": "DARK_STRIKE",
        "damage": 6,
        "hits": 1
      }
    ],
    "hand_cards": [
      {
        "index": 0,
        "id": "Strike",
        "energy_cost": 1,
        "target_type": "AnyEnemy",
        "playable": true
      }
    ],
    "draw_pile": [{"id": "Defend", "upgraded": false}, {"id": "Strike", "upgraded": false}],
    "discard_pile": [],
    "exhaust_pile": []
  },
  "event_options": [],
  "rest_site_options": [],
  "map": null,
  "potions": [
    {"index": 0, "id": "PotionOfStrength", "target_type": "Self"}
  ],
  "available_commands": ["STATE", "PING", "END", "PLAY", "POTION"]
}
```

### choice_request

Sent when the game needs a choice (card reward, card select). Controller must respond with `CHOOSE_RESPONSE`.

```json
{
  "type": "choice_request",
  "choice_id": "a1b2c3d4e5f6",
  "choice_type": "card_reward",
  "min_select": 0,
  "max_select": 1,
  "options": [
    {"index": 0, "id": "Strike", "name": "Strike"},
    {"index": 1, "id": "Defend", "name": "Defend"}
  ],
  "alternatives": ["Skip", "Reroll"]
}
```

### pong

Response to PING.

```json
{
  "type": "pong",
  "timestamp_unix_ms": 1710000000000
}
```

### Command acknowledgments (*_queued)

After a valid command is accepted, the mod may send an acknowledgment. These indicate the command was queued; the actual game effect happens asynchronously.

| Type | Extra fields | When sent |
|------|--------------|-----------|
| `play_queued` | `ok: true`, `hand_index: int` | After `PLAY` |
| `end_queued` | `ok: true` | After `END` |
| `event_choose_queued` | `ok: true`, `index: int` | After `EVENT_CHOOSE` |
| `rest_choose_queued` | `ok: true`, `index: int` | After `REST_CHOOSE` |
| `map_choose_queued` | `ok: true`, `index: int` | After `MAP_CHOOSE` |
| `potion_use_queued` | `ok: true`, `slot`, `target?` | After `POTION use` |
| `potion_discard_queued` | `ok: true`, `slot` | After `POTION discard` |
| `proceed_queued` | `ok: true` | After `PROCEED` |
| `start_queued` | `ok: true`, `character`, `seed?`, `ascension?` | After `START` |

### error

Sent when a command fails or parsing fails.

```json
{
  "type": "error",
  "error": "InvalidHandIndex",
  "details": "Hand index 5 out of range (hand size 7). Use 0-based indexing."
}
```

---

## 6. Commands (Controller → Mod)

Commands may be sent as **plain text** or **JSON**.

### Plain text format

```
COMMAND [arg1] [arg2] ...
```

### JSON format

```json
{
  "type": "command",
  "command": "PLAY",
  "args": "0 1",
  "request_id": "optional-correlation-id"
}
```

### Command list

| Command | Args | When valid | Description |
|---------|------|------------|-------------|
| `STATE` | — | Always | Request immediate state snapshot |
| `PING` | — | Always | Health check; mod responds with `pong` |
| `END` | — | Combat, player turn | End the current turn |
| `PLAY` | `<handIndex> [targetIndex]` | Combat, player turn | Play card at hand index; optional target for AnyEnemy/AnyAlly |
| `EVENT_CHOOSE` | `<index>` | Event screen | Choose event option by index |
| `REST_CHOOSE` | `<index>` | Rest site | Choose rest option |
| `MAP_CHOOSE` | `<index>` | Map screen | Travel to reachable node by index |
| `POTION` | `use <slot> [targetIndex]` | In run, has potions | Use potion at slot |
| `POTION` | `discard <slot>` | Out of combat, has potions | Discard potion at slot |
| `PROCEED` | — | Event, rest_site, treasure, shop (not in combat) | Leave current room, open map |
| `START` | `[character] [seed] [ascension]` | Not in run | Start new run. Character: index 0–4 or id (Ironclad, Silent, Regent, Necrobinder, Defect). |

### Choice response (not a command)

When the mod sends `choice_request`, respond with:

```
CHOOSE_RESPONSE <choice_id> <index>
CHOOSE_RESPONSE <choice_id> <index1> <index2> ...
CHOOSE_RESPONSE <choice_id> skip
```

---

## 7. Indexing Rules

All indices are **0-based**.

| Context | Index refers to |
|---------|------------------|
| `hand_cards` | Position in hand |
| `PLAY` hand index | Same as `hand_cards[].index` |
| `PLAY` target index | Enemy in `combat.enemies` or ally |
| `event_options` | Position in event options |
| `rest_site_options` | Position in rest options (0=Heal, 1=Smith) |
| `map.reachable` | Position in reachable nodes (sorted col, row) |
| `choice_request` options | Position in `options` array |
| `potions` | Potion slot index |

---

## 8. Examples

### Minimal loop

```
Controller: ready
Mod: {"type":"hello",...}
Mod: {"type":"state",...}   (auto-sent when stable)
Controller: STATE
Mod: {"type":"state",...}
Controller: PLAY 0
Mod: {"type":"play_queued","ok":true,"hand_index":0}
Mod: {"type":"state",...}   (auto-sent after action)
```

### Card reward choice

```
Mod: {"type":"choice_request","choice_id":"abc123","choice_type":"card_reward","options":[...],"alternatives":["Skip","Reroll"]}
Controller: CHOOSE_RESPONSE abc123 0
```

### Event / Map / START

```
Mod: {"type":"state","screen":"event","event_options":[...],...}
Controller: EVENT_CHOOSE 0

Mod: {"type":"state","screen":"map","map":{"reachable":[...]},...}
Controller: MAP_CHOOSE 1

Mod: {"type":"state","in_run":false,...}
Controller: START Ironclad
```

---

## 9. Versioning

- **Protocol version** in `hello.protocol_version`. Controllers should check compatibility.
- **Mod version** in `hello.mod_version`.
- Unknown fields should be ignored for backward compatibility.
