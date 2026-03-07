## CommunicateTheSpire2 — Planning Doc (v0)

Goal: create a **Slay the Spire 2** mod that provides an **IPC protocol** so an external process (AI, Twitch chat controller, tester, etc.) can **observe game state** and **issue commands** to control gameplay—similar in spirit to the StS1 `CommunicationMod`, but built on StS2’s C#/Godot + Harmony + GameAction architecture.

This document is intentionally **step-by-step** and implementation-oriented. We will use it as the checklist for building the mod later.

**Status legend:** ✅ Done | 🔄 Partial | ⬜ Not started

| Step | Status | Notes |
|------|--------|-------|
| 0 — Repository scaffold | ✅ | Build, manifest, naming all updated |
| 1 — Logging + config | 🔄 | Config JSON, main log, controller stderr; no in-game settings UI |
| 2 — Process host | ✅ | StdioProcessHost with async stdio, handshake, SendLine |
| 3 — Protocol schema | ✅ | hello, state, error, command, pong; ProtocolCommandParser; choice_request/response |
| 4 — Snapshot builder | ✅ | Run/combat/player/enemy/hand_cards, screen, event/rest_site/map/potions, available_commands |
| 5 — Stability detector | ✅ | CombatStateChanged + AfterActionExecuted; 150ms debounce; combat play-phase auto-send |
| 6 — Command executors | ✅ | STATE, PING, END, PLAY, EVENT_CHOOSE, REST_CHOOSE, MAP_CHOOSE, POTION, PROCEED, START; available_commands in state |
| 7 — Choice integration | ✅ | IpcCardSelector, choice_request/CHOOSE_RESPONSE; card reward + card_select; patch NCardRewardSelectionScreen |
| 8 — Expand coverage | ✅ | Potions, PROCEED (event/rest_site/treasure/shop), MAP_CHOOSE, START (out-of-run); state fields for all |
| 9 — Testing | ✅ | random + simple_policy controllers; STATE_CHECKSUM; C# parser unit tests; test_failure_modes.py; docs/TESTING.md |
| 10 — Packaging + docs | 🔄 | README + docs/PROTOCOL.md (build, install, commands); versioning in manifest/hello; no in-game settings UI |

---

## 1) General plan (high-level architecture)

### 1.1 What we’re building

- **A StS2 mod** packaged as:
  - **`CommunicateTheSpire2.pck`** (Godot resource pack; must contain `mod_manifest.json` at root)
  - **`CommunicateTheSpire2.dll`** (C# code; optional but required for IPC + logic)
- The mod will:
  - Start (or connect to) an **external controller process**
  - Produce **JSON snapshots** of relevant game state
  - Accept **commands** from the controller and execute them via **StS2-native actions** (prefer GameActions over input simulation)

### 1.2 IPC / protocol choices (recommended baseline)

- **Transport** (recommended):
  - Spawn a configured process and talk over **stdio** (newline-delimited messages), matching StS1’s usability.
  - Keep message format **NDJSON** (one JSON object per line), so both ends can stream.
- **Handshake**:
  - External process must print `ready\n` within timeout (e.g. 10s) after launch.
  - If handshake fails: terminate the process and surface error in a log.
- **Main loop behavior** (mirrors StS1):
  - When the game is “stable” and ready for player input, send a snapshot:
    - `available_commands`
    - `ready_for_command`
    - `in_game`
    - `game_state` (or `run_state` + `combat_state`)
  - Wait for one command message, execute it, and then continue.
- **Error model**:
  - If a command is invalid, respond with a JSON object like:
    - `{"error":"...","ready_for_command":true}`

### 1.3 Core sub-systems in the mod

- **Process host**
  - Spawn external process, manage stdin/stdout/stderr, enforce timeouts, restart/stop.
- **Protocol layer**
  - Serialize state snapshots; parse incoming commands; validate; report errors.
- **State publisher**
  - Builds snapshots from StS2 model state; decides when to publish.
- **Stability detector**
  - Detects “stable” moments (waiting for input, no actions executing, choice prompt active).
- **Command router + executors**
  - Maps protocol commands to StS2 operations, primarily through `GameAction`s and synchronizers.
- **Choice integration**
  - For “choose a card / pick reward / select options”, use selector hooks where available (preferred).

### 1.4 Command set (initial scope)

Recommended first milestone (small but end-to-end):

- **`STATE`**: force immediate state snapshot
- **`PLAY`**: play a card by hand index (and optional target)
- **`END`**: end turn

Then expand:

- **`CHOOSE`**: choose from presented choice list (rewards, screens, etc.)
- **`POTION`**: use/discard potion (if StS2 has a stable action path)
- Optional escape hatches:
  - **`KEY`/`CLICK`/`WAIT`** via Godot input simulation (only when needed; more brittle)

---

## 2) Know the game design (overview + coordination)

This section grounds the mod design in the actual StS2 architecture and conventions.

### 2.1 Architectural model (from STS2_Learner)

Key references:

- `STS2_Learner/sts2_framework.md`
  - Defines the layered split:
    - **Nodes (View)**: `src/Core/Nodes/*` (Godot UI, input, presentation)
    - **Controllers**: `src/Core/GameActions/*`, `src/Core/Combat/*`, `src/Core/Runs/*`
    - **Data**: `src/Core/Models/*`, `src/Core/Entities/*`
    - **Event hooks**: `src/Core/Hooks/Hook.cs`
- `STS2_Learner/sts2mod_guide.md`
  - Documents mod loading and authoring:
    - `.pck` + optional `.dll`
    - `mod_manifest.json`
    - `[ModInitializer]`
    - Harmony patching and Hook system usage

For CommunicateTheSpire2, the key takeaway is:

- **Prefer controller-level integration** (GameActions + Hooks + model APIs)
- Avoid UI-level simulation except as a last resort

### 2.2 How mods load in StS2

Core loader implementation:

- `src/Core/Modding/ModManager.cs`
  - Finds `.pck` files under `mods/` (and Steam Workshop)
  - Mounts the PCK and requires `res://mod_manifest.json`
  - Loads `{pckName}.dll` if present
  - Calls `[ModInitializer("...")]` methods if present, else `Harmony.PatchAll(assembly)`

Implications:

- Our mod should provide a **DLL** and use `[ModInitializer]` to bootstrap:
  - set up logging
  - start IPC host (optionally delayed)
  - register Harmony patches / event subscribers

### 2.3 How gameplay actually “happens” (why GameActions matter)

StS2’s controller layer uses a command/queue model:

- `src/Core/GameActions/ActionExecutor.cs`
  - Runs actions frame-by-frame, supports pause/unpause, exposes `IsRunning` and `CurrentlyRunningAction`
- `src/Core/GameActions/Multiplayer/ActionQueueSet.cs`
  - Holds per-player queues
  - Filters “ready” actions and blocks those waiting for player choice (`GameActionState.GatheringPlayerChoice`)
- `src/Core/GameActions/Multiplayer/ActionQueueSynchronizer.cs`
  - The public “enqueue” surface (`RequestEnqueue(...)`)
  - In multiplayer, routes through net messages; in singleplayer, enqueues directly

Implication for CommunicateTheSpire2:

- Our command executors should prefer:
  - `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new SomeAction(...))`
  - rather than trying to push UI buttons or simulate clicks

### 2.4 Combat flow and “stable state”

Combat control:

- `src/Core/Combat/CombatManager.cs`
  - Tracks phases: `IsPlayPhase`, `EndingPlayerTurnPhaseOne/Two`, etc.
  - Has `StateTracker` and multiple events (`TurnStarted`, `TurnEnded`, etc.)

State change notifications:

- `CombatManager.StateTracker.CombatStateChanged` is a central “something changed” signal.

Action queue stability:

- `RunManager.Instance.ActionExecutor.IsRunning` indicates action execution is active.
- `ActionQueueSet.GetReadyAction()` refuses to execute actions in `GatheringPlayerChoice`, so “stable” can include “waiting for choice”.

Implication:

We can define “stable/ready_for_command” roughly as:

- **in combat**:
  - not ending/transitioning, and
  - either:
    - `IsPlayPhase == true` AND queue not running, or
    - a player-choice screen/selector is active (waiting for selection)
- **out of combat**:
  - depends on current overlay/screen state (map, rewards, events, etc.)

### 2.5 Choice points (where `CHOOSE` can be made robust)

StS2 already has a selector abstraction used by AutoSlay/tests:

- `src/Core/Commands/CardSelectCmd.cs`
  - Supports `CardSelectCmd.UseSelector(...)` with `MegaCrit.Sts2.Core.TestSupport.ICardSelector`
- `src/Core/TestSupport/ICardSelector.cs`
  - `GetSelectedCards(...)`
  - `GetSelectedCardReward(...)`

Implication:

- Implement an **IPC-backed selector** and register it via `CardSelectCmd.UseSelector(...)`.
- This turns many “UI choose” situations into a deterministic “provide options → return selected items” workflow, without scraping UI nodes.

### 2.6 State snapshots (re-use existing “network snapshot” patterns)

StS2 already has a serialized combat snapshot builder:

- `src/Core/Entities/Multiplayer/NetFullCombatState.cs`
  - `NetFullCombatState.FromRun(IRunState runState, GameAction? justFinishedAction)`

Implication:

- We can build our JSON snapshots from:
  - Run state + combat state in models
  - And/or a simplified “NetFullCombatState-like” structure that’s stable and easy for clients

---

## 3) Step-by-step implementation plan (detailed breakdown)

This is the “do it in order” plan. Each step includes deliverables and verification.

### Step 0 — Repository scaffold and naming cleanup ✅

**Goal**: turn the copied demo mod into a clean CommunicateTheSpire2 starting point.

- **0.1** Copy demo mod scaffolding (done in this repo):
  - `CommunicateTheSpire2/` contains the demo mod sources + manifests + pck_only project.
- **0.2** Rename from `DemoMod` to `CommunicateTheSpire2` everywhere:
  - assembly name in `.csproj`
  - namespaces / logging file name
  - manifest `pck_name` and display name
  - `project.godot` and export preset output name
- **0.3** Confirm build + install steps match the new folder + new names.

**Deliverables**

- Compiles to `CommunicateTheSpire2.dll`
- Exports `CommunicateTheSpire2.pck`
- Installs as `{pck_name}.pck` + `{pck_name}.dll` in game `mods/`

**Verification**

- Game loads the mod (Settings → Modding) and a log line confirms initializer ran.

---

### Step 1 — Logging + configuration layer 🔄

**Goal**: implement robust logging and configuration before IPC.

- **1.1** Logging:
  - Write to `%APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log`
  - Keep a separate controller stderr log (like StS1) e.g. `communicate_the_spire2_controller_errors.log`
- **1.2** Configuration:
  - Where to store:
    - simplest: a JSON config in `%APPDATA%\SlayTheSpire2/CommunicateTheSpire2.config.json`
    - optional later: add an in-game mod settings UI
  - Minimum config fields:
    - `enabled` (bool)
    - `mode`: `spawn` / `connect` (future)
    - `command`: string (process command line)
    - `working_directory` (optional)
    - `handshake_timeout_seconds`
    - `verbose_protocol_logs` (bool)

**Deliverables**

- Config loader with defaults + validation
- Log writer utilities

**Verification**

- Mod starts, reads config, logs resolved settings.

---

### Step 2 — External process host (stdio-based) ✅

**Goal**: create a reliable child-process integration without blocking the main thread.

- **2.1** Spawn with `System.Diagnostics.Process`:
  - redirect stdin/stdout/stderr
  - set `UseShellExecute = false`
  - optionally set environment variables (e.g. run id)
- **2.2** Async line IO:
  - `ReadLineAsync` loop for stdout → enqueue messages
  - write queue for stdin; always terminate lines with `\n`
- **2.3** Handshake:
  - wait for `ready` within timeout
  - if timeout: kill process and log errors
- **2.4** Lifecycle:
  - restart on demand (future)
  - graceful shutdown on game exit / mod unload (best-effort)

**Deliverables**

- `ExternalProcessHost` class with:
  - `Start()`, `Stop()`, `IsRunning`
  - `SendLine(string)`, `OnLineReceived` event

**Verification**

- Point config command to a tiny script that prints `ready` then echoes input; confirm roundtrip.

---

### Step 3 — Protocol schema (NDJSON) ✅

**Goal**: define stable message types so external clients can integrate.

- **3.1** Messages game → controller:
  - `hello` (once): versioning, capabilities, mod/game identifiers
  - `state` snapshots: main payload
  - `error` payloads
- **3.2** Messages controller → game:
  - `command` object (or keep a “command line” string for parity)
  - `ping` (optional)
- **3.3** Versioning:
  - `protocol_version: 1`
  - `schema_version` for state payload

**Deliverables**

- C# DTOs for protocol messages (serialize with `System.Text.Json`)
- One-line-per-message writer/reader

**Verification**

- Logs show JSON messages are emitted and parsed correctly.

---

### Step 4 — State snapshot builder (minimum useful state) 🔄

**Goal**: produce stable, compact, deterministic state payloads.

Recommended approach: start small, then expand.

- **4.1** High-level envelope:
  - `available_commands`
  - `ready_for_command`
  - `in_game`
  - `timestamp` (local monotonic or wall)
- **4.2** Run-level state (first pass):
  - `RunManager.Instance.DebugOnlyGetState()` (if available)
  - act/floor, gold, seed-ish identity (if accessible), current room type
- **4.3** Combat state (first pass):
  - if in combat: use `NetFullCombatState.FromRun(runState, justFinishedAction)` as a base input
  - include:
    - player HP/block/energy/stars
    - hand cards (IDs + costs + target requirements)
    - enemies (IDs + HP/block + intents if accessible)
- **4.4** Identity strategy:
  - Prefer stable IDs (`ModelId`, `CombatId`, indices where unavoidable)
  - Clearly document 0-based vs 1-based indexing (StS1 used 1-based hand indices)

**Deliverables**

- `SnapshotBuilder` producing `StateMessage`

**Verification**

- With a run in progress, controller receives snapshots that reflect hand/enemies and update after actions.

---

### Step 5 — Stability detector (“when to send state”) ⬜

**Goal**: replicate the “send when stable” behavior from StS1, adapted to StS2.

- **5.1** Inputs/signals to watch:
  - `RunManager.Instance.ActionExecutor.IsRunning`
  - `CombatManager.Instance.IsPlayPhase`
  - `CombatManager.StateTracker.CombatStateChanged` events
  - action completion: `ActionExecutor.AfterActionExecuted`
  - choice gathering: detect when a selector is being queried (Step 7)
- **5.2** Debounce:
  - avoid sending multiple snapshots in a single frame burst
  - send when:
    - stability condition becomes true, OR
    - controller requests `STATE`, OR
    - a command finished executing and the world settled

**Deliverables**

- `StabilityDetector` that triggers “publish state now”

**Verification**

- In combat: snapshot arrives at start of play phase and after each played card resolves.

---

### Step 6 — Command executors (start with deterministic actions) 🔄

**Goal**: execute a small command set via the action system.

Initial commands:

- **6.1** `STATE`
  - immediate snapshot send
- **6.2** `END`
  - enqueue `EndPlayerTurnAction` (or the same action invoked by UI)
  - path reference: `src/Core/Nodes/Combat/NEndTurnButton.cs` calls `RequestEnqueue(new EndPlayerTurnAction(...))`
- **6.3** `PLAY {handIndex} [targetIndex]`
  - map hand index → `CardModel` from local player’s hand pile
  - target index → `Creature` from `CombatState.Creatures` / enemies list
  - enqueue `new PlayCardAction(card, target)`

**Deliverables**

- `CommandRouter` and `CommandValidator`
- “available_commands” reflects real availability:
  - `PLAY` only if in combat play phase and there exists a playable card
  - `END` only if in combat play phase and end turn is allowed

**Verification**

- Controller can play a strike and end turn without UI interaction.

---

### Step 7 — Choice integration (IPC-backed selectors) ⬜

**Goal**: implement `CHOOSE` robustly without UI scraping.

- **7.1** Implement `ICardSelector`:
  - On `GetSelectedCards(...)`:
    - send `choice_request` to controller containing:
      - choice type (e.g. `simple_grid`, `upgrade`, etc.)
      - options list (IDs + summary)
      - min/max selection
    - await controller response → return selected cards
  - On `GetSelectedCardReward(...)`:
    - include reward options + alternatives
- **7.2** Register selector:
  - `CardSelectCmd.UseSelector(new IpcCardSelector(...))`
- **7.3** Protocol additions:
  - correlate requests/responses with `choice_id`
  - handle timeouts and default behaviors (e.g. skip if allowed)

**Deliverables**

- IPC selector implementation
- Protocol messages: `choice_request`, `choice_response`

**Verification**

- Card reward screen: controller selects a card deterministically.

---

### Step 8 — Expand state + command coverage 🔄

**Goal**: reach parity with the most valuable StS1 commands.

Incremental expansions:

- **8.1** Potions (`POTION use|discard slot [target]`) ✅
  - StS2 action pathway: `UsePotionAction` / `DiscardPotionGameAction`; state includes `potions` list.
- **8.2** Screen navigation (`PROCEED`) ✅
  - Event/rest_site/treasure/shop: `NEventRoom.Proceed()`, `NMapScreen.Open()`, `RunManager.ProceedFromTerminalRewardsScreen()`. RETURN not implemented.
- **8.3** Map selection ✅
  - `MAP_CHOOSE` with existing synchronizers/commands.
- **8.4** Out-of-run (`START [character] [seed] [ascension]`) ✅
  - Uses `NGame.Instance.StartNewSingleplayerRun` when not in run; character by index or id, optional seed/ascension; `available_commands` includes START when `!in_run`.

**Deliverables**

- Additional executors and snapshot fields

**Verification**

- Play an entire act with the controller (manual or scripted).

---

### Step 9 — Testing strategy (practical + repeatable) 🔄

**Goal**: ensure changes don’t require “eyeballing” only.

- **9.1** Local loopback controller ✅
  - `random_controller.py`, `simple_policy_controller.py`; print `ready`, log state, issue PLAY/END or first-option policy.
- **9.2** Determinism checks ✅
  - State checksum logged when `verbose_protocol_logs`; same seed + controller → comparable evolution.
- **9.3** Failure mode testing ✅
  - **C#:** `Tests/ProtocolCommandParserTests.cs` — 12 unit tests for parser (null, empty, plain/JSON, invalid JSON). Run: `dotnet test CommunicateTheSpire2/Tests/CtS2.Tests.csproj`.
  - **Integration:** `controller/tests/test_failure_modes.py` — run as controller; sends invalid commands, asserts error JSON. Doc: `docs/TESTING.md`, `controller/tests/README.md`.
  - Controller crash / invalid command / timeouts documented in TESTING.md.

---

### Step 10 — Packaging + documentation 🔄

**Goal**: make this usable by others.

- **10.1** Clear README
  - how to build `.dll` + `.pck`
  - where to put the mod
  - how to set controller command/config
- **10.2** Protocol docs
  - schema, examples, command list, indexing rules
- **10.3** Versioning
  - semantic versioning for mod
  - protocol versioning for clients

---

## Appendix A — Out-of-combat state & commands (from decompiled source)

This section documents the StS2 structures for map, shop, event, rest site, etc., so we can extend the protocol.

### Room types (`RoomType` enum)

`Unassigned`, `Monster`, `Elite`, `Boss`, `Treasure`, `Shop`, `Event`, `RestSite`, `Map`

### Current room & run state

- `IRunState.CurrentRoom` → `AbstractRoom?` (null when on map)
- `IRunState.Map` → `ActMap`
- `IRunState.CurrentMapCoord` → `MapCoord?` (col, row)
- `IRunState.CurrentMapPoint` → `MapPoint?`
- `MapPoint`: `coord`, `PointType` (`MapPointType`: Monster, Elite, Boss, Shop, Event, RestSite, Treasure, Unknown, …), `Children`, `parents`

### Map travel

- **Action**: `MoveToMapCoordAction(player, MapCoord)` → `ActionQueueSynchronizer.RequestEnqueue(...)`
- **Or** `RunManager.Instance.EnterMapCoord(MapCoord)` (direct)
- **Or** `NMapScreen.Instance.TravelToMapCoord(MapCoord)` (triggers UI flow)
- Reachable nodes: `NMapScreen.IsTravelEnabled` + `MapPointState.Travelable` on `NMapPoint`
- Current position: `runState.CurrentMapCoord`; children of current point = next reachable nodes

### Shop (MerchantRoom)

- **Room**: `runState.CurrentRoom is MerchantRoom`
- **Inventory**: `MerchantRoom.Inventory` → `MerchantInventory`
  - `CharacterCardEntries`, `ColorlessCardEntries`, `RelicEntries`, `PotionEntries`, `CardRemovalEntry`
- **Entry**: `MerchantEntry.Cost`, `EnoughGold`, `IsStocked`; purchase via `entry.OnTryPurchaseWrapper(inventory)`
- **Note**: AutoSlay uses `NMerchantSlot` UI + `UiHelper.Click`. Model-level purchase exists via `OnTryPurchaseWrapper`; no dedicated GameAction for “buy slot X”.
- **Proceed**: `NProceedButton` click after leaving shop

### Event (EventRoom)

- **Room**: `runState.CurrentRoom is EventRoom`
- **Options**: `RunManager.Instance.EventSynchronizer.GetLocalEvent().CurrentOptions` → `IReadOnlyList<EventOption>`
  - `EventOption`: `TextKey`, `Title`, `Description`, `IsLocked`, `IsProceed`, `Chosen()` (async)
- **Choose**: `EventSynchronizer.ChooseLocalOption(optionIndex)` — model-level, no UI click
- **Event ID**: `EventRoom.CanonicalEvent.Id` (ModelId)

### Rest site (RestSiteRoom)

- **Room**: `runState.CurrentRoom is RestSiteRoom`
- **Options**: `RestSiteRoom.Options` → `IReadOnlyList<RestSiteOption>`
  - `RestSiteOption`: `OptionId` (e.g. "HEAL", "SMITH", "MEND"), `Title`, `IsEnabled`, `OnSelect()` (async)
- **Choose**: call `option.OnSelect()` on the chosen option
- **Proceed**: `NRestSiteRoom.ProceedButton` click

### Treasure room

- Single relic choice; uses reward/card selection flow (`CardSelectCmd`, `ICardSelector`-style)
- Proceed after claiming

### Map room (room selection)

- Shown when `CurrentRoom is MapRoom`
- Reachable children from `CurrentMapPoint.Children` (filter by travel state)
- Travel: `MoveToMapCoordAction` or `EnterMapCoord(coord)`

### Commands to add (out-of-combat)

| Command        | When                    | Implementation                                                                 |
|----------------|-------------------------|--------------------------------------------------------------------------------|
| `MAP_CHOOSE`   | Map room, travel enabled | `RequestEnqueue(new MoveToMapCoordAction(me, coord))` for a child of current  |
| `EVENT_CHOOSE` | Event room              | `EventSynchronizer.ChooseLocalOption(index)`                                   |
| `REST_CHOOSE`  | Rest site               | `RestSiteRoom.Options[index].OnSelect()`                                       |
| `SHOP_BUY`     | Shop                    | `MerchantInventory` entry by index → `OnTryPurchaseWrapper` (need slot mapping)|
| `PROCEED`      | Generic                 | Click `NProceedButton` or equivalent (room-specific)                           |

### State snapshot additions (out-of-combat)

- `screen`: `"map"` | `"shop"` | `"event"` | `"rest_site"` | `"treasure"` | `"combat"`
- `map`: `{ current_coord: {col, row}, reachable: [{col, row, point_type}...] }`
- `shop`: `{ gold, entries: [{index, type, id, cost, affordable}] }` (flatten AllEntries with index)
- `event`: `{ event_id, options: [{index, text_key, title, is_proceed}] }`
- `rest_site`: `{ options: [{index, option_id, title}] }`

---

## Appendix B — Key code touchpoints (for later implementation)

- **Mod loading**: `src/Core/Modding/ModManager.cs`
- **Hooks**: `src/Core/Hooks/Hook.cs`
- **Run state**: `src/Core/Runs/RunManager.cs`
- **Combat control**: `src/Core/Combat/CombatManager.cs`
- **Action execution**: `src/Core/GameActions/ActionExecutor.cs`
- **Queue selection**: `src/Core/GameActions/Multiplayer/ActionQueueSet.cs`
- **Enqueue API**: `src/Core/GameActions/Multiplayer/ActionQueueSynchronizer.cs`
- **Card selection**: `src/Core/Commands/CardSelectCmd.cs`, `src/Core/TestSupport/ICardSelector.cs`
- **Snapshot inspiration**: `src/Core/Entities/Multiplayer/NetFullCombatState.cs`
- **Noninteractive/test automation reference**: `src/Core/AutoSlay/AutoSlayer.cs`, `src/Core/AutoSlay/Handlers/Rooms/*.cs`
- **Out-of-combat**: `src/Core/Rooms/MerchantRoom.cs`, `EventRoom.cs`, `RestSiteRoom.cs`, `MapRoom.cs`
- **Event options**: `src/Core/Multiplayer/Game/EventSynchronizer.cs` (ChooseLocalOption)
- **Map travel**: `src/Core/GameActions/MoveToMapCoordAction.cs`, `RunManager.EnterMapCoord`, `NMapScreen.TravelToMapCoord`

