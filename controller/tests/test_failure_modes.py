#!/usr/bin/env python3
"""
Failure-mode tests for the CtS2 protocol.

Run this script AS the controller (set mod config command to this script).
The game will spawn it; we send invalid commands and assert the mod responds
with error JSON. Exits 0 if all checks pass, 1 otherwise.

Example config:
  "command": "python -u controller/tests/test_failure_modes.py",
  "working_directory": "C:\\path\\to\\CommunicateTheSpire2",
"""
import json
import sys


def log(*args):
    print(*args, file=sys.stderr, flush=True)


def send(line: str) -> None:
    print(line, file=sys.stdout, flush=True)


def read_line() -> str | None:
    out = sys.stdin.readline()
    return out if out else None


def read_json_line() -> dict | None:
    line = read_line()
    if line is None:
        return None
    line = line.strip()
    if not line:
        return None
    try:
        return json.loads(line)
    except json.JSONDecodeError:
        return None


def main() -> int:
    failures = 0

    # Handshake
    send("ready")
    hello = read_json_line()
    if not hello or hello.get("type") != "hello":
        log("FAIL: expected hello after ready, got:", hello)
        return 1

    log("OK: hello received")

    # Test 1: Empty line -> error
    send("")
    resp = read_json_line()
    if resp is None:
        # Mod might not send for empty; try sending a space
        send("   ")
        resp = read_json_line()
    if resp is not None and resp.get("type") == "error":
        log("OK: empty/whitespace line produced error:", resp.get("error"))
    else:
        log("SKIP or FAIL: empty line response:", resp)
        if resp is not None and resp.get("type") != "error":
            failures += 1

    # Test 2: Unknown command -> error
    send("NOTACOMMAND")
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: unknown command produced error:", resp.get("error"))
    else:
        log("FAIL: expected error for NOTACOMMAND, got:", resp)
        failures += 1

    # Test 3: Invalid JSON command (malformed) -> error
    send('{"type":"command"}')  # missing "command" field in JSON command
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: invalid JSON command produced error:", resp.get("error"))
    else:
        log("FAIL: expected error for malformed JSON command, got:", resp)
        failures += 1

    # Test 4: PLAY with bad args (no args when PLAY needs at least hand index) or invalid
    send("PLAY")
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: PLAY with no args produced error:", resp.get("error"))
    else:
        log("FAIL: expected error for PLAY with no args, got:", resp)
        failures += 1

    # Test 5: END when not in combat -> error (we might be on main menu)
    send("END")
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: END when not in combat produced error:", resp.get("error"))
    else:
        # If we're in combat this might succeed; treat as skip
        if resp and resp.get("type") != "error":
            log("SKIP: END returned non-error (might be in combat):", resp.get("type"))
        else:
            log("FAIL: expected error for END when not in combat, got:", resp)
            failures += 1

    # Test 6: POTION with bad subcommand
    send("POTION")
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: POTION with no args produced error:", resp.get("error"))
    else:
        log("FAIL: expected error for POTION with no args, got:", resp)
        failures += 1

    # Test 7: EVENT_CHOOSE with no args
    send("EVENT_CHOOSE")
    resp = read_json_line()
    if resp and resp.get("type") == "error":
        log("OK: EVENT_CHOOSE with no args produced error:", resp.get("error"))
    else:
        log("FAIL: expected error for EVENT_CHOOSE with no args, got:", resp)
        failures += 1

    if failures > 0:
        log("FAILURES:", failures)
        return 1
    log("All failure-mode checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
