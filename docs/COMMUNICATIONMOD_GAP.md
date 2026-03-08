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
| RETURN (Cancel/Skip/Leave) | ✅ | Close map overlay or shop inventory |
| KEY | ✅ | Simulate keypress via Godot Input (Confirm, Map, Deck, Draw_Pile, etc.) |
| CLICK | ✅ | Mouse click at (X,Y); 1920×1080 reference space |
| WAIT | ✅ | Wait N frames, then send state (StS1 compatible) |
| STATE | ✅ | Supported |

---

## 2. Game State — Implemented in CtS2

- Run summary (act, floor, gold, ascension, room_type)
- Combat: hand_cards, draw_pile, discard_pile, exhaust_pile, limbo (Play pile), card_in_play, enemies, local_player (hp, block, energy, stars, powers, cards_discarded_this_turn, orbs)
- Event options, rest_site options
- Map: current_coord, reachable
- Potions, relics
- Deck (full deck outside combat)
- Shop (when screen = shop): cards, relics, potions with prices; purge_available, purge_cost. **Buy/purge via commands:** SHOP_BUY_CARD, SHOP_BUY_RELIC, SHOP_BUY_POTION, SHOP_PURGE (pure API, no CLICK).
- **Combat reward** (StS2 has a post-combat rewards screen; when screen = "rewards"): `state.rewards` (index, type: gold/relic/potion/card, amount?, id?); **REWARD_CHOOSE** &lt;index&gt; to claim (pure API). Card reward then sends choice_request. Any future key/special reward on the same screen would use this same structure (extend type or id as needed).
- **Boss reward** (StS2 has a “choose a relic” overlay after boss; when screen = "boss_reward"): `state.boss_reward` (index, id); **BOSS_REWARD_CHOOSE** &lt;index&gt; to pick one relic (pure API).
- **Richer card model:** card entries (hand_cards, draw/discard/exhaust/deck, shop.cards) include optional `name`, `type` (Attack/Skill/Power/…), `rarity`, `exhausts`, `ethereal`; hand already has `target_type` (has_target).
- available_commands
- choice_request for card reward / card select

---

## 3. Game State — Still Missing in CtS2

| StS1 State | Description |
|------------|-------------|
| ~~deck (master deck)~~ | ✅ DONE — Full deck (id, upgraded) when outside combat |
| ~~Shop screen~~ | ✅ DONE — shop.cards, shop.relics, shop.potions (index, id, cost), purge_available, purge_cost |
| ~~Combat reward~~ | ✅ DONE — Combat reward exists in StS2; CtS2 exposes state.rewards + REWARD_CHOOSE |
| Combat reward (StS1 extras) | Special reward options (e.g. Sapphire Key) that appear as extra buttons on the same combat reward screen. If StS2 adds these, they can use the same **state.rewards** + **REWARD_CHOOSE** flow (extend reward type in snapshot, e.g. type "key" or id when known). |
| ~~Boss reward~~ | ✅ DONE — StS2 has boss/relic choice screen; CtS2 exposes state.boss_reward + BOSS_REWARD_CHOOSE |
| ~~Richer card model~~ | ✅ DONE — name, type, rarity, exhausts, ethereal on hand_cards, piles, deck, shop cards (has_target = target_type in hand) |
| ~~Player orbs~~ | ✅ DONE — `local_player.orbs` (id, name, evoke_amount, passive_amount) from StS2 OrbQueue |
| ~~limbo~~ | ✅ DONE — StS2 Play pile exposed as `combat.limbo` (cards being played, not yet discard/exhaust) |
| ~~card_in_play~~ | ✅ DONE — `combat.card_in_play` = single card whose effects are currently executing |
| ~~cards_discarded_this_turn~~ | ✅ DONE — `local_player.cards_discarded_this_turn` (int; for Tactician etc.) |
| times_damaged | For Blood for Blood etc. |
| keys | Act 4 keys (ruby, emerald, sapphire); may not apply to StS2 |

*Already implemented: relics, enemy intent, player/enemy powers, deck, shop, combat reward, boss reward, richer card model, limbo (see §2).*

---

## 4. Low-Level / Input Simulation

All StS1 input simulation commands are implemented in CtS2:

| Feature | CtS2 |
|---------|------|
| KEY | ✅ Simulate keypress (Godot Input.ParseInputEvent) |
| CLICK | ✅ Mouse click at (X,Y); 1920×1080 reference |
| WAIT | ✅ Wait N frames, then send state |
| RETURN | ✅ Close map overlay or shop inventory |

---

## 5. Recommended Next Steps (priority order)

### High priority (AI / automation needs)

1. ~~**Shop screen**~~ ✅ DONE — State + SHOP_BUY_CARD/RELIC/POTION, SHOP_PURGE (pure API)
2. ~~**Combat reward**~~ ✅ DONE — state.rewards + REWARD_CHOOSE &lt;index&gt; (gold/relic/potion/card); card sends choice_request
3. ~~**Master deck**~~ ✅ DONE — Full deck (id, upgraded) outside combat

### Medium priority

4. ~~**Boss reward screen**~~ ✅ DONE — state.boss_reward + BOSS_REWARD_CHOOSE &lt;index&gt;
5. ~~**Richer card model**~~ ✅ DONE — name, type, rarity, exhausts, ethereal on all card entries
6. limbo ✅, card_in_play ✅, cards_discarded_this_turn ✅, times_damaged

### Done

- Enemy intent, relics, player/enemy powers, RETURN, KEY, CLICK, WAIT
