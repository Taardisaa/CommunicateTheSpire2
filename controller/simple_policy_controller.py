#!/usr/bin/env python3
"""
Minimal controller that uses available_commands and a simple policy:
- In combat: PLAY first playable card (by index), else END turn.
- For choices: pick first option or skip.

Useful for testing and as a reference for deterministic-ish behavior.
"""
import json
import sys


def log(*args):
    print(*args, file=sys.stderr, flush=True)


def send_line(line: str):
    print(line, file=sys.stdout, flush=True)


def read_line() -> str | None:
    return sys.stdin.readline() or None


def main():
    send_line("ready")
    log("[policy] sent ready")

    hello_line = read_line()
    if hello_line is None:
        log("[policy] no hello; exiting")
        return

    try:
        hello = json.loads(hello_line.strip())
        log("[policy] hello protocol_version=", hello.get("protocol_version"))
    except Exception as e:
        log("[policy] parse hello failed:", e)

    send_line(json.dumps({"type": "command", "command": "STATE"}))
    log("[policy] sent initial STATE")

    while True:
        line = read_line()
        if line is None:
            log("[policy] stdin closed; exiting")
            break

        line = line.strip()
        if not line:
            continue

        try:
            msg = json.loads(line)
        except Exception as e:
            log("[policy] parse failed:", e, "raw:", line[:80])
            continue

        msg_type = msg.get("type", "")

        if msg_type == "choice_request":
            choice_id = msg.get("choice_id", "")
            options = msg.get("options", [])
            if options:
                send_line(f"CHOOSE_RESPONSE {choice_id} 0")
                log("[policy] choice index=0")
            else:
                send_line(f"CHOOSE_RESPONSE {choice_id} skip")
                log("[policy] choice skip")
            continue

        if msg_type != "state":
            log("[policy] ignored type:", msg_type)
            continue

        state = msg
        available = state.get("available_commands") or []

        if "PLAY" in available:
            hand = (state.get("combat") or {}).get("hand_cards") or []
            play_index = None
            for c in hand:
                if c.get("playable"):
                    play_index = c.get("index", 0)
                    break
            if play_index is not None:
                send_line(json.dumps({"type": "command", "command": "PLAY", "args": str(play_index)}))
                log("[policy] PLAY", play_index)
                continue

        if "END" in available:
            send_line(json.dumps({"type": "command", "command": "END"}))
            log("[policy] END")
            continue

        if "EVENT_CHOOSE" in available:
            opts = state.get("event_options") or []
            if opts:
                send_line(json.dumps({"type": "command", "command": "EVENT_CHOOSE", "args": "0"}))
                log("[policy] EVENT_CHOOSE 0")
                continue

        if "REST_CHOOSE" in available:
            opts = state.get("rest_site_options") or []
            if opts:
                send_line(json.dumps({"type": "command", "command": "REST_CHOOSE", "args": "0"}))
                log("[policy] REST_CHOOSE 0")
                continue

        if "MAP_CHOOSE" in available:
            reachable = (state.get("map") or {}).get("reachable") or []
            if reachable:
                send_line(json.dumps({"type": "command", "command": "MAP_CHOOSE", "args": "0"}))
                log("[policy] MAP_CHOOSE 0")
                continue

        send_line(json.dumps({"type": "command", "command": "STATE"}))
        log("[policy] no action; requested STATE")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
