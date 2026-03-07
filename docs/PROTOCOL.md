# CommunicateTheSpire2 — Protocol Specification

This document describes the IPC protocol between the CommunicateTheSpire2 mod and an external controller process.

**Protocol version:** 1  
**Transport:** NDJSON over stdio (one JSON object per line)

---

## 1. Transport

- **Mod → controller:** mod writes JSON lines to controller's **stdin**
- **Controller → mod:** controller writes JSON lines (or plain text for some commands) to **stdout**
- **Controller stderr:** logged by mod to `communicate_the_spire2_controller_errors.log`

All messages are newline-delimited. Empty lines are ignored.

---

## 2. Handshake

1. Mod spawns controller process.
2. Controller must print `ready\n` within the configured timeout (default 10s).
3. If handshake succeeds, mod sends `hello` and enables protocol handling.
4. If handshake fails, mod terminates the controller.

---

## 3. Message Flow

- **Mod sends:** `hello`, `state`, `choice_request`, `pong`, `error`, and command responses (`play_queued`, `end_queued`, etc.).
- **Mod auto-sends state** when the game becomes stable (combat play phase, after actions resolve).
- **Controller sends:** commands (plain or JSON) and `CHOOSE_RESPONSE` for choices.
- **Controller may send STATE** anytime to request an immediate snapshot.

---

## 4. Messages (Mod → Controller)

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

Game state snapshot. Sent in response to STATE command or when the game becomes stable.

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
        "block": 0
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
    ]
  },
  "event_options": [],
  "rest_site_options": [],
  "map": null,
  "potions": [
    {"index": 0, "id": "PotionOfStrength", "target_type": "Self"},
    {"index": 1, "id": "FirePotion", "target_type": "AnyEnemy"}
  ],
  "available_commands": ["STATE", "PING", "END", "PLAY", "POTION"]
}
```

**State fields**

| Field | Type | Description |
|-------|------|-------------|
| `in_run` | bool | True if a run is in progress |
| `in_combat` | bool | True if in combat |
| `screen` | string \| null | `"combat"`, `"event"`, `"rest_site"`, `"map"`, `"shop"`, `"treasure"`, `"unknown"`, or null (main menu) |
| `run` | object \| null | Present when in run; `act_index`, `act_floor`, `total_floor`, `ascension`, `gold`, `room_type` |
| `combat` | object \| null | Present when in combat; `round_number`, `current_side`, `local_player`, `enemies`, `hand_cards` |
| `event_options` | array | Options when `screen` = `"event"`; each has `index`, `text_key`, `title`, `is_locked`, `is_proceed` |
| `rest_site_options` | array | Options when `screen` = `"rest_site"`; each has `index`, `option_id`, `title`, `is_enabled` |
| `map` | object \| null | Present when `screen` = `"map"`; `current_coord` {col, row, point_type}, `reachable` array |
| `potions` | array | Local player's potion slots (when in run); each has `index`, `id`, `target_type` |
| `available_commands` | array | Commands valid in this state; e.g. `["STATE","PING","END","PLAY","POTION"]`, `["EVENT_CHOOSE"]`, etc. |

### choice_request

Sent when the game needs a choice (card reward, card select, etc.). Controller must respond with `CHOOSE_RESPONSE`.

```json
{
  "type": "choice_request",
  "choice_id": "a1b2c3d4e5f6",
  "choice_type": "card_reward",
  "min_select": 0,
  "max_select": 1,
  "options": [
    {"index": 0, "id": "Strike", "name": "Strike"},
    {"index": 1, "id": "Defend", "name": "Defend"},
    {"index": 2, "id": "Bash", "name": "Bash"}
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

## 5. Commands (Controller → Mod)

Commands may be sent as **plain text** or **JSON**.

### Plain text format

```
COMMAND [arg1] [arg2] ...
```

Examples: `STATE`, `PING`, `END`, `PLAY 0 1`, `EVENT_CHOOSE 0`, `REST_CHOOSE 1`, `MAP_CHOOSE 0`, `POTION use 0`, `POTION discard 1`, `PROCEED`, `START`, `START Ironclad`, `START 0 myseed 5`

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
| `REST_CHOOSE` | `<index>` | Rest site | Choose rest option (0=Heal, 1=Smith, etc.) |
| `MAP_CHOOSE` | `<index>` | Map screen | Travel to reachable node by index |
| `POTION` | `use <slot> [targetIndex]` | In run, has potions | Use potion at slot; optional target for single-target potions (enemy/ally index) |
| `POTION` | `discard <slot>` | Out of combat, has potions | Discard potion at slot |
| `PROCEED` | — | Event, rest_site, treasure, or shop screen (not in combat) | Leave current room/screen and open map |
| `START` | `[character] [seed] [ascension]` | Not in run (main menu) | Start a new singleplayer run. Character: index 0-4 or id (Ironclad, Silent, Regent, Necrobinder, Defect). Seed/ascension optional; ascension 0-20. |

### Choice response (not a command)

When the mod sends `choice_request`, respond with:

**Plain format:**
```
CHOOSE_RESPONSE <choice_id> <index>
CHOOSE_RESPONSE <choice_id> <index1> <index2> ...
CHOOSE_RESPONSE <choice_id> skip
```

**JSON format:**
```json
{"type": "choice_response", "choice_id": "a1b2c3d4e5f6", "indices": [0]}
{"type": "choice_response", "choice_id": "a1b2c3d4e5f6", "skip": true}
```

---

## 6. Indexing Rules

All indices are **0-based**.

| Context | Index refers to |
|---------|------------------|
| `hand_cards` | Position in hand (0 = first card) |
| `PLAY` hand index | Same as `hand_cards.index` |
| `PLAY` target index | Enemy in `combat.enemies` (0 = first enemy) or ally |
| `event_options` | Position in event options list |
| `rest_site_options` | Position in rest options (0=Heal, 1=Smith, etc.) |
| `map.reachable` | Position in reachable nodes (sorted by col, row) |
| `choice_request` options | Position in `options` array |

---

## 7. Examples

### Minimal controller loop

```
Controller: ready
Mod: {"type":"hello",...}
Mod: {"type":"state",...}   (auto-sent when stable, or after STATE)
Controller: STATE
Mod: {"type":"state",...}
Controller: PLAY 0
Mod: {"type":"play_queued","ok":true,"hand_index":0}
Mod: {"type":"state",...}   (auto-sent after card resolves)
```

### Card reward choice

```
Mod: {"type":"choice_request","choice_id":"abc123","choice_type":"card_reward","options":[{"index":0,"id":"Strike","name":"Strike"},...],"alternatives":["Skip","Reroll"]}
Controller: CHOOSE_RESPONSE abc123 0
```

### Event choice

```
Controller: STATE
Mod: {"type":"state","screen":"event","event_options":[{"index":0,"text_key":"...","title":"Leave","is_locked":false,"is_proceed":true},...],...}
Controller: EVENT_CHOOSE 0
Mod: {"type":"event_choose_queued","ok":true,"index":0}
```

### Map travel

```
Controller: STATE
Mod: {"type":"state","screen":"map","map":{"current_coord":{"col":2,"row":3,"point_type":"RestSite"},"reachable":[{"col":1,"row":4,"point_type":"Monster"},{"col":3,"row":4,"point_type":"Event"}]},...}
Controller: MAP_CHOOSE 1
Mod: {"type":"map_choose_queued","ok":true,"index":1}
```

---

## 8. Versioning

- **Protocol version** is sent in `hello.protocol_version`. Controllers should check compatibility.
- **Mod version** is sent in `hello.mod_version` (semantic version).
- Backward compatibility: new minor protocol versions add optional fields; controllers should ignore unknown fields.
