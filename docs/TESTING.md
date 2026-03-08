# Testing Guide

This document describes how to test the CommunicateTheSpire2 mod and controller behavior.

---

## 1. Controllers

### random_controller.py

- Sends `ready`, then loops: reads messages (state, choice_request), responds to choices with a random option, and periodically sends `STATE`.
- Use for: smoke testing, checking that the protocol flows correctly.
- Run: `python -u controller/random_controller.py` (with mod config pointing to it).

### simple_policy_controller.py

- Policy: in combat, **PLAY** first playable card else **END**; for events/rest/map, picks first option.
- Uses `available_commands` and `hand_cards[].playable` from state.
- Use for: deterministic-ish runs, testing that commands are accepted and state evolves.
- Run: `python -u controller/simple_policy_controller.py`

---

## 2. C# unit tests (protocol parser)

**Tests/ProtocolCommandParserTests.cs** exercises `ProtocolCommandParser.TryParse` (plain and JSON commands, null/empty, invalid JSON). No game required.

From the repo root:

```bash
dotnet test CommunicateTheSpire2/Tests/CtS2.Tests.csproj
```

If the mod’s `lib/` has **sts2.dll** and **0Harmony.dll**, the test project copies them to its output so the mod DLL can load. Tests only call the parser; they do not start the game.

---

## 3. Automated failure-mode test (integration)

**controller/tests/test_failure_modes.py** runs as the controller and verifies that invalid input produces error JSON. Use it to confirm the mod’s failure behavior without playing.

1. Set config so the controller is this script, e.g. `"command": "python -u controller/tests/test_failure_modes.py"` and correct `working_directory`.
2. Start the game (main menu is enough).
3. The script sends invalid commands (empty line, unknown command, bad JSON, `PLAY`/`END`/`POTION`/`EVENT_CHOOSE` with bad or missing args) and asserts each response has `"type": "error"`.
4. Exits 0 if all checks pass, 1 otherwise. See **controller/tests/README.md** for details.

---

## 4. Failure Modes (reference)

### Controller crash / exit

- **Behavior:** Mod stops sending to the controller. If `restart_on_exit` is true in config, the mod will restart the controller after a delay (`restart_backoff_ms`, `max_restart_attempts`).
- **Check:** Kill the controller process; confirm mod log shows "Controller exited" and optionally "Scheduling controller restart".

### Invalid command

- **Behavior:** Mod responds with a JSON error message: `{"type":"error","error":"...","details":"..."}`.
- **Examples:** `PLAY 99` (invalid hand index), `END` when not in combat, `EVENT_CHOOSE 5` when only 2 options.
- **Check:** Send an invalid command (e.g. `INVALID` or `PLAY -1`); confirm error JSON on controller stdin.

### Handshake timeout

- **Behavior:** If the controller does not print `ready` within `handshake_timeout_seconds`, the mod treats startup as failed and does not enable the protocol.
- **Check:** Set config to run a process that never prints "ready"; confirm mod log shows "Controller startup failed (handshake timed out...)".

### Choice timeout

- **Behavior:** When the mod sends `choice_request`, it waits for `CHOOSE_RESPONSE`. If the controller never responds, the bridge times out (after a delay) and treats the choice as skip.
- **Check:** Use a controller that ignores `choice_request`; the game should eventually continue (skip) or hang depending on game logic.

---

## 5. Determinism and checksums

- **Same seed, same controller, same policy:** With a deterministic controller (e.g. `simple_policy_controller` always picking first option), the same run seed should yield the same sequence of states and decisions. This is best verified by the game’s own run replay or by comparing state snapshots at key points.
- **State checksum (optional):** With `verbose_protocol_logs: true` in config, the mod logs a line `[STATE_CHECKSUM] <hex>` after each state send. The checksum is the first 16 hex characters of SHA256(state JSON). You can grep the log for `STATE_CHECKSUM` to get a sequence of checksums; the same run with the same controller should produce the same sequence.

Example (PowerShell):

```powershell
# After a run, extract checksums from the mod log
Select-String -Path "$env:APPDATA\SlayTheSpire2\CommunicateTheSpire2.log" -Pattern "STATE_CHECKSUM"
```

---

## 6. Manual test flow

1. Build and install the mod; set config to use `simple_policy_controller.py` with `enabled: true`.
2. Start the game, start a run, enter combat.
3. Confirm the controller receives state and sends PLAY/END; combat proceeds without clicking.
4. After combat, confirm card reward choice_request and CHOOSE_RESPONSE; reward is chosen.
5. On map, confirm MAP_CHOOSE; travel proceeds.
6. Optionally enable `verbose_protocol_logs` and inspect the log for STATE_CHECKSUM and protocol lines.

---

## 7. Config reference for testing

**In-game config:** Settings → "Configure CommunicateTheSpire2" to edit enabled, command, and working directory without editing the JSON file. Changes apply on Save (controller restarts if enabled).

**JSON config** (`%APPDATA%\\SlayTheSpire2\\CommunicateTheSpire2.config.json`):

```json
{
  "enabled": true,
  "command": "python -u controller/simple_policy_controller.py",
  "working_directory": "C:\\path\\to\\CommunicateTheSpire2",
  "handshake_timeout_seconds": 10,
  "verbose_protocol_logs": true,
  "restart_on_exit": false,
  "max_restart_attempts": 0
}
```

- `verbose_protocol_logs: true` — mod logs each controller line and each state checksum.
- `restart_on_exit: true` — mod restarts the controller when it exits (useful for stress tests).
