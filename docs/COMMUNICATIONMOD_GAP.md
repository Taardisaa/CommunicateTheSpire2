# CommunicationMod vs CommunicateTheSpire2 — Gap Analysis

Comparison of StS1 CommunicationMod features with CommunicateTheSpire2. Use this to prioritize implementation.

---

## 1. Commands

| StS1 Command | CtS2 | Notes |
|--------------|------|-------|
| START | ✅ | Character, seed, ascension |
| POTION Use/Discard | ✅ | Supported |
| PLAY | ✅ | CtS2 uses 0-based hand index (StS1 uses 1-based) |
| END | ✅ | Supported |
| CHOOSE | ✅ | CtS2: CHOOSE_RESPONSE, EVENT_CHOOSE, REST_CHOOSE, MAP_CHOOSE |
| PROCEED (Confirm) | ✅ | Supported |
| RETURN (Cancel/Skip/Leave) | ❌ | Not implemented |
| KEY | ❌ | Keypress simulation (Confirm, Map, Deck, Draw_Pile, etc.) |
| CLICK | ❌ | Mouse click at (X,Y) |
| WAIT | ❌ | Wait N frames, then send state |
| STATE | ✅ | Supported |

---

## 2. Game State — Implemented in CtS2

- Run summary (act, floor, gold, ascension, room_type)
- Combat: hand_cards, draw_pile, discard_pile, exhaust_pile, enemies, local_player (hp, block, energy, stars)
- Event options, rest_site options
- Map: current_coord, reachable
- Potions
- available_commands
- choice_request for card reward / card select

---

## 3. Game State — Not Implemented in CtS2

| StS1 State | Description |
|------------|-------------|
| relics | Player relics (id, name, counter) |
| deck (master deck) | Full deck outside combat |
| Enemy intent | Monster intent (attack/buff/debuff), move_id, move_base_damage, move_adjusted_damage, move_hits |
| Player powers | Player buffs/debuffs (id, name, amount, etc.) |
| Enemy powers | Monster buffs/debuffs |
| Player orbs | Orb slots (id, evoke_amount, passive_amount); StS2 has different orb model |
| Shop screen | Shop cards/relics/potions with prices, purge_available, purge_cost |
| Combat reward | Reward list (gold, relic, potion, sapphire key link) |
| Boss reward | Boss relics to choose |
| Richer card model | name, uuid, misc, type, rarity, has_target, exhausts, ethereal (beyond id/upgraded/cost) |
| limbo | Cards in limbo |
| card_in_play | Currently playing card |
| cards_discarded_this_turn | For Tactician etc. |
| times_damaged | For Blood for Blood etc. |
| keys | Act 4 keys (ruby, emerald, sapphire); may not apply to StS2 |

---

## 4. Low-Level / Input Simulation (StS1 only)

- **KEY** — Simulate keypresses (Confirm, Map, Deck, Draw_Pile, End_Turn, Card_1..10, etc.)
- **CLICK** — Mouse click at (X,Y) in screen coordinates
- **WAIT** — Wait N frames, then send state
- **RETURN** — Cancel/skip/leave (back button)

CtS2 uses GameActions/commands; low-level input injection not currently exposed.

---

## 5. Recommended Next Steps (priority order)

### High priority (AI / automation needs)

1. ~~**Enemy intent**~~ ✅ DONE — intent, move_id, damage, hits in enemies
2. **Relics** — Build and synergy context
3. **Player / enemy powers** — Strength, weakness, block, etc.
4. **RETURN** — Back/cancel for events and rewards
5. **Shop screen** — Buy cards/relics/potions, purge
6. **Combat reward** — Choose gold/relic/potion
7. **Master deck** — Full deck (e.g. at map/rest)

### Medium priority

8. Boss reward screen
9. Richer card model (exhausts, ethereal, type, rarity)
10. limbo, card_in_play, cards_discarded_this_turn, times_damaged

### Low priority (input simulation)

- KEY, CLICK, WAIT — Only if UI automation is required; prefer native commands
