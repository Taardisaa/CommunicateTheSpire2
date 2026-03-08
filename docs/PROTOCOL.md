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
├── relics: array         ← local player relics (when in run)
├── deck: array           ← full deck (when in run, outside combat); each has id, upgraded, upgrade_level
├── shop: object | null   ← present when screen = "shop" (cards, relics, potions, purge_available, purge_cost)
├── rewards: array        ← combat reward options when screen = "rewards" (index, type, amount?, id?)
├── boss_reward: array    ← boss/relic choice options when screen = "boss_reward" (index, id)
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
| `"shop"`   | Shop           | `shop` (cards, relics, potions, purge_available, purge_cost) |
| `"rewards"` | Combat rewards | `rewards` (gold/relic/potion/card options; use REWARD_CHOOSE) |
| `"boss_reward"` | Boss / relic choice | `boss_reward` (relic options; use BOSS_REWARD_CHOOSE) |
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
| `relics` | array | Local player relics; when `in_run` |
| `deck` | array | Full deck (id, upgraded, upgrade_level per card); when `in_run` and not `in_combat` |
| `shop` | object \| null | Shop inventory; present when `screen` = `"shop"` |
| `rewards` | array | Combat reward options; present when `screen` = `"rewards"`. Each has `index`, `type` ("gold" \| "relic" \| "potion" \| "card"), optional `amount` (gold), optional `id` (relic/potion). |
| `boss_reward` | array | Boss/relic choice options; present when `screen` = `"boss_reward"`. Each has `index`, `id` (relic id). |
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
| `hand_cards` | array | Cards in hand: index, id, energy_cost, target_type, playable, upgraded, upgrade_level; richer: name, type, rarity, exhausts, ethereal |
| `draw_pile` | array | Draw pile (top first); each has `id`, `upgraded`, `upgrade_level`; richer: name, type, rarity, exhausts, ethereal |
| `discard_pile` | array | Discard pile; each has `id`, `upgraded`, `upgrade_level` |
| `exhaust_pile` | array | Exhaust pile; each has `id`, `upgraded`, `upgrade_level` |
| `limbo` | array | Cards being played (StS2 Play pile); same format as draw_pile. Non-empty during card resolution. |
| `card_in_play` | object or null | Single card whose effects are currently executing; null when no card play in progress. Same format as draw_pile entry (id, upgraded, upgrade_level; richer: name, type, rarity, exhausts, ethereal). |

**local_player:** `net_id`, `current_hp`, `max_hp`, `block`, `energy`, `stars`, `powers`, `cards_discarded_this_turn` (int; count of cards the local player discarded this turn; for Tactician etc.), `orbs` (array; orb slots from StS2 OrbQueue; each has `id`, `name`, `evoke_amount`, `passive_amount`; empty if character has no orbs)

**enemies:** each has `combat_id`, `name`, `current_hp`, `max_hp`, `block`, `intent`, `move_id`, `damage`, `hits`, `powers`

**hand_cards:** each has `index`, `id`, `energy_cost`, `target_type`, `playable`, `upgraded` (bool), `upgrade_level` (int). Richer model (optional): `name` (localized title), `type` (Attack/Skill/Power/Status/Curse/Quest), `rarity` (Basic/Common/Uncommon/Rare/…), `exhausts` (exhausts when played), `ethereal` (exhausts if not played). StS2 supports multiple upgrades (e.g. +2).

**draw_pile / discard_pile / exhaust_pile / limbo / deck:** each entry has `id` (card id), `upgraded` (bool), `upgrade_level` (int). Richer model (optional): `name`, `type`, `rarity`, `exhausts`, `ethereal` (same as hand_cards). `limbo` = StS2 Play pile (cards being played, not yet in discard/exhaust). Order: draw_pile top-to-bottom, discard/exhaust/limbo as stored.

**enemies (intent):** `intent` = IntentType (Attack, Buff, Debuff, Defend, etc.). `move_id` = move state id. `damage` = total damage from attack intents (0 if not attacking). `hits` = number of hits (1 for single, N for multi-attack).

**powers (player/enemy):** each power has `id`, `name`, `amount` (Strength, Weakness, Vulnerable, etc.).

**orbs (local_player):** StS2 orb slots (OrbQueue). Each orb has `id`, `name`, `evoke_amount`, `passive_amount`. Empty for characters without orbs.

### 4.7 deck (when in_run, outside combat)

Full deck; each entry has `id`, `upgraded`, `upgrade_level` (same format as draw_pile); optional richer fields as in draw_pile. Present when in run and not in combat.

### 4.8 shop (when screen = "shop")

| Field | Type | Description |
|-------|------|-------------|
| `cards` | array | Shop cards; each has `index`, `id`, `upgraded`, `upgrade_level`, `cost`; richer: `name`, `type`, `rarity`, `exhausts`, `ethereal` |
| `relics` | array | Shop relics; each has `index`, `id`, `cost` |
| `potions` | array | Shop potions; each has `index`, `id`, `cost` |
| `purge_available` | bool | True if card removal (purge) is available |
| `purge_cost` | int | Gold cost for card removal |

Compare `cost` to `run.gold` to see what the player can afford.

### 4.9 event_options (when screen = "event")

Each option: `index`, `text_key`, `title`, `is_locked`, `is_proceed`

### 4.10 rest_site_options (when screen = "rest_site")

Each option: `index`, `option_id`, `title`, `is_enabled`  
Typical indices: 0 = Heal, 1 = Smith.

### 4.11 map (when screen = "map")

| Field | Type | Description |
|-------|------|-------------|
| `current_coord` | object | `col`, `row`, `point_type` of current node |
| `reachable` | array | Reachable nodes; each has `col`, `row`, `point_type` |

`reachable` is sorted (col, row). Use index for `MAP_CHOOSE`.

### 4.12 potions (when in_run)

Each slot: `index`, `id`, `target_type` (e.g. `"Self"`, `"AnyEnemy"`)

### 4.13 relics (when in_run)

Each relic: `id`, `name`, `counter` (display amount; -1 when no counter)

### 4.14 available_commands

Lists commands valid in this state. Controller should only send listed commands (except `STATE`, `PING`, `CHOOSE_RESPONSE`).

---

## 5. Messages (Mod → Controller)

### hello

Sent once after handshake.

```json
{
  "type": "hello",
  "protocol_version": 1,
  "mod_version": "1.0.0",
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
      "stars": 0,
      "powers": [{"id": "StrengthPower", "name": "Strength", "amount": 2}]
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
        "hits": 1,
        "powers": [{"id": "VulnerablePower", "name": "Vulnerable", "amount": 1}]
      }
    ],
    "hand_cards": [
      {
        "index": 0,
        "id": "Strike",
        "energy_cost": 1,
        "target_type": "AnyEnemy",
        "playable": true,
        "upgraded": false,
        "upgrade_level": 0
      }
    ],
    "draw_pile": [{"id": "Defend", "upgraded": false, "upgrade_level": 0}, {"id": "Strike", "upgraded": false, "upgrade_level": 0}],
    "discard_pile": [],
    "exhaust_pile": []
  },
  "event_options": [],
  "rest_site_options": [],
  "map": null,
  "potions": [
    {"index": 0, "id": "PotionOfStrength", "target_type": "Self"}
  ],
  "relics": [
    {"id": "BurningBlood", "name": "Burning Blood", "counter": -1},
    {"id": "NeowsBlessing", "name": "Neow's Lament", "counter": 2}
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
| `reward_choose_queued` | `ok: true`, `index: int` | After `REWARD_CHOOSE` |
| `boss_reward_choose_queued` | `ok: true`, `index: int` | After `BOSS_REWARD_CHOOSE` |
| `shop_buy_ok` | `item_type`: `"card"` \| `"relic"` \| `"potion"` \| `"purge"`, `index`? (for card/relic/potion) | After `SHOP_BUY_*` when purchase succeeds |
| `start_queued` | `ok: true`, `character`, `seed?`, `ascension?` | After `START` |
| `continue_queued` | `ok: true` | After `CONTINUE` |

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
| `RETURN` | — | Map overlay open, or shop inventory open | Close map overlay or close shop inventory (back/cancel button) |
| `KEY` | `<keyname>` | In run | Simulate keypress. Keys: CONFIRM, CANCEL, MAP, DECK, DRAW_PILE, DISCARD_PILE, EXHAUST_PILE, END_TURN, UP, DOWN, LEFT, RIGHT, DROP_CARD, CARD_1..CARD_10, TOP_PANEL, PEEK, SELECT |
| `CLICK` | `Left|Right X Y` | In run | Simulate mouse click at screen coordinates. Reference: 1920×1080 (0,0 top-left; 960,540 center). |
| `WAIT` | `<frames>` | In run | Wait for the specified number of frames (~17ms per frame at 60fps), then send state. Useful after KEY/CLICK to let animations settle. |
| `REWARD_CHOOSE` | `<index>` | Screen = "rewards" | Claim the combat reward at the given index (use `state.rewards[].index`). Gold/relic/potion claimed immediately; card opens choice_request for which card. Pure API. |
| `BOSS_REWARD_CHOOSE` | `<index>` | Screen = "boss_reward" | Choose the boss/relic reward at the given index (use `state.boss_reward[].index`). Pure API. |
| `SHOP_BUY_CARD` | `<index>` | Shop, `state.shop.cards` non-empty | Buy the card at the given index (0-based; use `state.shop.cards[].index`). Pure API; no CLICK. |
| `SHOP_BUY_RELIC` | `<index>` | Shop, `state.shop.relics` non-empty | Buy the relic at the given index. Pure API. |
| `SHOP_BUY_POTION` | `<index>` | Shop, `state.shop.potions` non-empty | Buy the potion at the given index. Pure API. |
| `SHOP_PURGE` | — | Shop, `state.shop.purge_available` true | Use card removal (purge). Opens card selection; respond with `CHOOSE_RESPONSE` to pick which card to remove. Pure API. |
| `START` | `[character] [seed] [ascension]` | Not in run | Start new run. Character: index 0–4 or id (Ironclad, Silent, Regent, Necrobinder, Defect). |
| `CONTINUE` | — | Not in run, saved run exists | Continue the current saved run (same path as main-menu Continue). |

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
| `state.rewards` | Combat reward option (0-based); use with `REWARD_CHOOSE <index>` |
| `state.boss_reward` | Boss/relic choice option (0-based); use with `BOSS_REWARD_CHOOSE <index>` |
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

### Event / Map / START / CONTINUE

```
Mod: {"type":"state","screen":"event","event_options":[...],...}
Controller: EVENT_CHOOSE 0

Mod: {"type":"state","screen":"map","map":{"reachable":[...]},...}
Controller: MAP_CHOOSE 1

Mod: {"type":"state","in_run":false,...}
Controller: START Ironclad

Mod: {"type":"state","in_run":false,"available_commands":["STATE","PING","START","CONTINUE"],...}
Controller: CONTINUE
```

### KEY (keypress simulation)

```
Controller: KEY MAP
Mod: {"type":"key_queued","ok":true,"key":"MAP"}

Controller: KEY CARD_1
Mod: {"type":"key_queued","ok":true,"key":"CARD_1"}
```

### CLICK (mouse click at coordinates)

```
Controller: CLICK Left 960 540
Mod: {"type":"click_queued","ok":true,"button":"Left","x":960,"y":540}
```

Coordinates use 1920×1080 reference space (0,0 = top-left; 960,540 = center).

### WAIT (delay then state)

```
Controller: WAIT 60
Mod: {"type":"wait_queued","ok":true,"frames":60}
Mod: {"type":"state",...}   (sent after ~1 sec at 60fps)
```

Waits for the specified number of frames, then sends the current state. Useful after KEY/CLICK when animations need time to settle.

### Shop buy (pure API, no CLICK)

Shop purchases use direct game API; no mouse simulation.

```
Controller: SHOP_BUY_CARD 0
Mod: {"type":"shop_buy_ok","item_type":"card","index":0}
```

Use indices from `state.shop.cards[].index`, `state.shop.relics[].index`, `state.shop.potions[].index`. On failure the mod sends an `error` (e.g. `NotInShop`, `InvalidShopIndex`, `CannotAfford`). After `SHOP_PURGE`, the game sends a `choice_request` to pick which card to remove; respond with `CHOOSE_RESPONSE <choice_id> <card_index>`.

---

## 9. Versioning

- **Protocol version** in `hello.protocol_version`. Controllers should check compatibility.
- **Mod version** in `hello.mod_version`.
- Unknown fields should be ignored for backward compatibility.
