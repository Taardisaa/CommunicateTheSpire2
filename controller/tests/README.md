# Controller tests

## test_failure_modes.py

Integration test: run **as the controller** (game spawns this script). Sends invalid commands and asserts the mod responds with `type: "error"` JSON.

**How to run:**

1. Build and install the mod.
2. Set config so the controller command is this script, e.g.:

   ```json
   {
     "enabled": true,
     "command": "python -u controller/tests/test_failure_modes.py",
     "working_directory": "C:\\path\\to\\CommunicateTheSpire2"
   }
   ```

3. Start the game (main menu is enough).
4. The mod will spawn the script; it prints "ready", receives hello, then sends invalid commands and checks for error responses.
5. Script exits 0 if all checks pass, 1 on failure. Check the game/mod log if needed.

**What it checks:**

- Empty or whitespace line → error
- Unknown command (`NOTACOMMAND`) → error
- Malformed JSON command (missing `command` field) → error
- `PLAY` with no args → error
- `END` when not in combat → error
- `POTION` with no args → error
- `EVENT_CHOOSE` with no args → error
