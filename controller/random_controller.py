import json
import random
import sys
import time


def log(*args):
    """Log to stderr so we don't confuse the protocol."""
    print(*args, file=sys.stderr, flush=True)


def send_line(line: str):
    print(line, file=sys.stdout, flush=True)


def read_line() -> str | None:
    return sys.stdin.readline() or None


def main():
    # Handshake: tell the mod we're ready.
    send_line("ready")
    log("[controller] sent: ready")

    # Expect an initial hello JSON from the mod.
    hello_line = read_line()
    if hello_line is None:
        log("[controller] no hello received; exiting.")
        return

    log("[controller] recv HELLO:", hello_line.strip())
    try:
        hello = json.loads(hello_line)
        log("[controller] parsed hello:", hello)
    except Exception as e:
        log("[controller] failed to parse hello JSON:", e)

    # Kick off by requesting state.
    send_line(json.dumps({"type": "command", "command": "STATE"}))
    log("[controller] sent initial STATE")

    # Main loop: read lines (state, choice_request, etc.) and respond.
    while True:
        line = read_line()
        if line is None:
            log("[controller] stdin closed; exiting.")
            break

        line = line.strip()
        if not line:
            continue

        try:
            msg = json.loads(line)
        except Exception as e:
            log("[controller] failed to parse JSON:", e, "raw:", line)
            continue

        msg_type = msg.get("type", "")

        if msg_type == "choice_request":
            # Respond to card/reward choice. Pick random option or skip.
            choice_id = msg.get("choice_id", "")
            options = msg.get("options", [])
            if options:
                idx = random.randint(0, len(options) - 1)
                send_line(f"CHOOSE_RESPONSE {choice_id} {idx}")
                log(f"[controller] choice_response: {choice_id} index={idx}")
            else:
                send_line(f"CHOOSE_RESPONSE {choice_id} skip")
                log(f"[controller] choice_response: {choice_id} skip")
            continue

        if msg_type != "state":
            log("[controller] unhandled message type:", msg_type)
            continue

        state = msg

        # Random delay, then request next state.
        time.sleep(random.uniform(0.5, 2.0))
        send_line(json.dumps({"type": "command", "command": "STATE"}))
        log("[controller] sent STATE")

        # Very naive "random strategy": just log a random description
        # based on current HP and number of enemies.
        combat = state.get("combat") or {}
        player = combat.get("local_player") or {}
        enemies = combat.get("enemies") or []

        hp = player.get("current_hp")
        max_hp = player.get("max_hp")
        enemy_count = len(enemies)

        if hp is not None and max_hp:
            hp_ratio = hp / max_hp if max_hp > 0 else 0.0
        else:
            hp_ratio = None

        strategies = []
        if hp_ratio is not None and hp_ratio < 0.3:
            strategies.append("DEFENSIVE")
        if enemy_count >= 3:
            strategies.append("AOE")
        strategies.append("RANDOM_ATTACK")

        chosen = random.choice(strategies)
        log(
            f"[controller] state: in_run={state.get('in_run')} "
            f"in_combat={state.get('in_combat')} "
            f"hp={hp}/{max_hp}, enemies={enemy_count}, strategy={chosen}"
        )


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass

