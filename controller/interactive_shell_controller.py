#!/usr/bin/env python3
"""
Interactive controller for CommunicateTheSpire2.

Design:
- Uses stdin/stdout exclusively for mod IPC.
- Exposes a separate localhost TCP shell for user interaction.
- Optionally auto-launches a shell client window on Windows.
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import shlex
import socketserver
import subprocess
import sys
import threading
from typing import Any


def log(*args: Any) -> None:
    print(*args, file=sys.stderr, flush=True)


def send_line(line: str) -> None:
    print(line, file=sys.stdout, flush=True)


def read_line() -> str | None:
    return sys.stdin.readline() or None


def build_command_json(command: str, args: str | None = None) -> str:
    payload: dict[str, Any] = {"type": "command", "command": command}
    if args:
        payload["args"] = args
    return json.dumps(payload)


def first_non_empty(*values: Any, default: str = "?") -> str:
    for value in values:
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return default


def state_summary(state: dict[str, Any] | None) -> str:
    if not state:
        return "No state received yet."

    lines: list[str] = []
    lines.append(
        "in_run={} in_combat={} screen={}".format(
            state.get("in_run"),
            state.get("in_combat"),
            state.get("screen"),
        )
    )

    run = state.get("run") or {}
    if run:
        lines.append(
            "run: act={} floor={}/{} asc={} gold={} room={}".format(
                run.get("act_index"),
                run.get("act_floor"),
                run.get("total_floor"),
                run.get("ascension"),
                run.get("gold"),
                run.get("room_type"),
            )
        )

    combat = state.get("combat") or {}
    if combat:
        player = combat.get("local_player") or {}
        enemies = combat.get("enemies") or []
        hand = combat.get("hand_cards") or []
        lines.append(
            "combat: round={} side={} hp={}/{} block={} energy={} enemies={} hand={}".format(
                combat.get("round_number"),
                combat.get("current_side"),
                player.get("current_hp"),
                player.get("max_hp"),
                player.get("block"),
                player.get("energy"),
                len(enemies),
                len(hand),
            )
        )
        for enemy in enemies:
            lines.append(
                "  enemy[{i}] {name} hp={hp}/{max_hp} block={block} intent={intent} dmg={dmg}x{hits}".format(
                    i=enemy.get("combat_id", "?"),
                    name=first_non_empty(enemy.get("name")),
                    hp=enemy.get("current_hp", "?"),
                    max_hp=enemy.get("max_hp", "?"),
                    block=enemy.get("block", "?"),
                    intent=first_non_empty(enemy.get("intent")),
                    dmg=enemy.get("damage", 0),
                    hits=enemy.get("hits", 0),
                )
            )
        for card in hand:
            lines.append(
                "  hand[{idx}] {name} id={id} cost={cost} playable={playable} target={target}".format(
                    idx=card.get("index"),
                    name=first_non_empty(card.get("name"), card.get("id")),
                    id=first_non_empty(card.get("id")),
                    cost=card.get("energy_cost"),
                    playable=card.get("playable"),
                    target=first_non_empty(card.get("target_type")),
                )
            )

    event_opts = state.get("event_options") or []
    if event_opts:
        lines.append("event options:")
        for opt in event_opts:
            lines.append(
                "  [{}] {} locked={} proceed={}".format(
                    opt.get("index"),
                    first_non_empty(opt.get("title"), opt.get("text_key")),
                    opt.get("is_locked"),
                    opt.get("is_proceed"),
                )
            )

    rest_opts = state.get("rest_site_options") or []
    if rest_opts:
        lines.append("rest options:")
        for opt in rest_opts:
            lines.append(
                "  [{}] {} enabled={}".format(
                    opt.get("index"),
                    first_non_empty(opt.get("title"), opt.get("option_id")),
                    opt.get("is_enabled"),
                )
            )

    map_state = state.get("map") or {}
    reachable = map_state.get("reachable") or []
    if reachable:
        lines.append("map reachable:")
        for i, node in enumerate(reachable):
            lines.append(
                "  [{}] col={} row={} type={}".format(
                    i,
                    node.get("col"),
                    node.get("row"),
                    first_non_empty(node.get("point_type")),
                )
            )

    rewards = state.get("rewards") or []
    if rewards:
        lines.append("rewards:")
        for reward in rewards:
            lines.append(
                "  [{}] type={} id={} amount={}".format(
                    reward.get("index"),
                    reward.get("type"),
                    reward.get("id"),
                    reward.get("amount"),
                )
            )

    boss_reward = state.get("boss_reward") or []
    if boss_reward:
        lines.append("boss rewards:")
        for reward in boss_reward:
            lines.append("  [{}] id={}".format(reward.get("index"), reward.get("id")))

    shop = state.get("shop") or {}
    if shop:
        lines.append(
            "shop: purge_available={} purge_cost={}".format(
                shop.get("purge_available"),
                shop.get("purge_cost"),
            )
        )
        for card in shop.get("cards") or []:
            lines.append(
                "  card[{}] {} cost={}".format(
                    card.get("index"),
                    first_non_empty(card.get("name"), card.get("id")),
                    card.get("cost"),
                )
            )
        for relic in shop.get("relics") or []:
            lines.append(
                "  relic[{}] {} cost={}".format(
                    relic.get("index"),
                    first_non_empty(relic.get("id")),
                    relic.get("cost"),
                )
            )
        for potion in shop.get("potions") or []:
            lines.append(
                "  potion[{}] {} cost={}".format(
                    potion.get("index"),
                    first_non_empty(potion.get("id")),
                    potion.get("cost"),
                )
            )

    available = state.get("available_commands") or []
    lines.append("available_commands: " + ", ".join(str(x) for x in available))
    return "\n".join(lines)


class SharedData:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.hello: dict[str, Any] | None = None
        self.last_state: dict[str, Any] | None = None
        self.last_non_state: dict[str, Any] | None = None
        self.pending_choices: dict[str, dict[str, Any]] = {}

    def set_hello(self, msg: dict[str, Any]) -> None:
        with self._lock:
            self.hello = msg

    def update_message(self, msg: dict[str, Any]) -> None:
        msg_type = str(msg.get("type") or "")
        with self._lock:
            if msg_type == "state":
                self.last_state = msg
            elif msg_type == "choice_request":
                choice_id = str(msg.get("choice_id") or "")
                if choice_id:
                    self.pending_choices[choice_id] = msg
                self.last_non_state = msg
            else:
                self.last_non_state = msg

    def consume_choice(self, choice_id: str) -> None:
        with self._lock:
            self.pending_choices.pop(choice_id, None)

    def snapshot(self) -> tuple[dict[str, Any] | None, dict[str, Any] | None, dict[str, dict[str, Any]]]:
        with self._lock:
            return self.last_state, self.last_non_state, dict(self.pending_choices)


class InteractiveShellServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, server_address: tuple[str, int], controller: "InteractiveShellController"):
        self.controller = controller
        super().__init__(server_address, ShellRequestHandler)


class ShellRequestHandler(socketserver.StreamRequestHandler):
    def send_text(self, text: str) -> None:
        self.wfile.write(text.encode("utf-8", errors="replace"))
        self.wfile.flush()

    def handle(self) -> None:
        self.send_text(self.server.controller.banner())
        while not self.server.controller.stop_event.is_set():
            self.send_text("cts2> ")
            line = self.rfile.readline()
            if not line:
                return

            raw = line.decode("utf-8", errors="replace").strip()
            if not raw:
                continue

            response, close_client = self.server.controller.handle_shell_command(raw)
            self.send_text(response + "\n")
            if close_client:
                return


class InteractiveShellController:
    def __init__(self, host: str, port: int, auto_launch_client: bool) -> None:
        self.host = host
        self.port = port
        self.auto_launch_client = auto_launch_client

        self.shared = SharedData()
        self.outbound_queue: "queue.Queue[str]" = queue.Queue()
        self.stop_event = threading.Event()
        self.server: InteractiveShellServer | None = None
        self.server_thread: threading.Thread | None = None

    def enqueue(self, line: str) -> None:
        self.outbound_queue.put(line)

    def banner(self) -> str:
        return (
            "CommunicateTheSpire2 interactive shell\n"
            "Type 'help' for commands.\n"
        )

    def command_help(self) -> str:
        return "\n".join(
            [
                "Commands:",
                "  help                     Show this help.",
                "  state                    Show compact state summary.",
                "  json                     Show full latest state JSON.",
                "  cmds                     Show available_commands.",
                "  pending                  Show pending choice_request entries.",
                "  refresh                  Request immediate STATE.",
                "  ping                     Send PING.",
                "  send <raw>               Send raw protocol line.",
                "  play <hand> [target]     PLAY card index, optional target.",
                "  end                      END turn.",
                "  event <index>            EVENT_CHOOSE <index>.",
                "  rest <index>             REST_CHOOSE <index>.",
                "  map <index>              MAP_CHOOSE <index>.",
                "  proceed                  PROCEED.",
                "  return                   RETURN.",
                "  potion use <slot> [tgt]  POTION use.",
                "  potion discard <slot>    POTION discard.",
                "  reward <index>           REWARD_CHOOSE.",
                "  boss <index>             BOSS_REWARD_CHOOSE.",
                "  shop card <index>        SHOP_BUY_CARD.",
                "  shop relic <index>       SHOP_BUY_RELIC.",
                "  shop potion <index>      SHOP_BUY_POTION.",
                "  purge                    SHOP_PURGE.",
                "  start [char] [seed] [asc] START run.",
                "  choose <id> <...|skip>   CHOOSE_RESPONSE.",
                "  quit                     Disconnect this shell client.",
            ]
        )

    def handle_shell_command(self, raw: str) -> tuple[str, bool]:
        try:
            parts = shlex.split(raw)
        except ValueError as ex:
            return f"Parse error: {ex}", False

        if not parts:
            return "", False

        cmd = parts[0].lower()
        args = parts[1:]

        if cmd == "help":
            return self.command_help(), False
        if cmd == "quit":
            return "Bye.", True

        state, last_non_state, pending = self.shared.snapshot()

        if cmd == "state":
            return state_summary(state), False
        if cmd == "json":
            if not state:
                return "No state received yet.", False
            return json.dumps(state, indent=2), False
        if cmd == "cmds":
            if not state:
                return "No state received yet.", False
            available = state.get("available_commands") or []
            return "available_commands: " + ", ".join(str(x) for x in available), False
        if cmd == "pending":
            if not pending:
                msg = "No pending choices."
                if last_non_state:
                    msg += " Last non-state message type: {}".format(last_non_state.get("type"))
                return msg, False
            lines = ["Pending choices:"]
            for choice_id, choice in pending.items():
                lines.append(
                    "  {} type={} options={} min={} max={}".format(
                        choice_id,
                        choice.get("choice_type"),
                        len(choice.get("options") or []),
                        choice.get("min_select"),
                        choice.get("max_select"),
                    )
                )
            return "\n".join(lines), False
        if cmd == "refresh":
            self.enqueue(build_command_json("STATE"))
            return "Requested STATE.", False
        if cmd == "ping":
            self.enqueue(build_command_json("PING"))
            return "Sent PING.", False
        if cmd == "send":
            if not args:
                return "Usage: send <raw protocol line>", False
            self.enqueue(" ".join(args))
            return f"Queued raw: {' '.join(args)}", False

        if cmd == "play":
            if len(args) < 1:
                return "Usage: play <hand> [target]", False
            payload_args = args[0] if len(args) == 1 else f"{args[0]} {args[1]}"
            self.enqueue(build_command_json("PLAY", payload_args))
            return f"Queued PLAY {payload_args}", False
        if cmd == "end":
            self.enqueue(build_command_json("END"))
            return "Queued END", False
        if cmd == "event" and args:
            self.enqueue(build_command_json("EVENT_CHOOSE", args[0]))
            return f"Queued EVENT_CHOOSE {args[0]}", False
        if cmd == "rest" and args:
            self.enqueue(build_command_json("REST_CHOOSE", args[0]))
            return f"Queued REST_CHOOSE {args[0]}", False
        if cmd == "map" and args:
            self.enqueue(build_command_json("MAP_CHOOSE", args[0]))
            return f"Queued MAP_CHOOSE {args[0]}", False
        if cmd == "proceed":
            self.enqueue(build_command_json("PROCEED"))
            return "Queued PROCEED", False
        if cmd == "return":
            self.enqueue(build_command_json("RETURN"))
            return "Queued RETURN", False
        if cmd == "reward" and args:
            self.enqueue(build_command_json("REWARD_CHOOSE", args[0]))
            return f"Queued REWARD_CHOOSE {args[0]}", False
        if cmd == "boss" and args:
            self.enqueue(build_command_json("BOSS_REWARD_CHOOSE", args[0]))
            return f"Queued BOSS_REWARD_CHOOSE {args[0]}", False
        if cmd == "purge":
            self.enqueue(build_command_json("SHOP_PURGE"))
            return "Queued SHOP_PURGE", False
        if cmd == "potion":
            if len(args) < 2:
                return "Usage: potion use <slot> [target] | potion discard <slot>", False
            action = args[0].lower()
            if action == "use":
                payload_args = args[1] if len(args) == 2 else f"{args[1]} {args[2]}"
                self.enqueue(build_command_json("POTION", f"use {payload_args}"))
                return f"Queued POTION use {payload_args}", False
            if action == "discard":
                self.enqueue(build_command_json("POTION", f"discard {args[1]}"))
                return f"Queued POTION discard {args[1]}", False
            return "Usage: potion use <slot> [target] | potion discard <slot>", False
        if cmd == "shop":
            if len(args) != 2:
                return "Usage: shop card|relic|potion <index>", False
            item_type = args[0].lower()
            index = args[1]
            command_map = {
                "card": "SHOP_BUY_CARD",
                "relic": "SHOP_BUY_RELIC",
                "potion": "SHOP_BUY_POTION",
            }
            command = command_map.get(item_type)
            if not command:
                return "Usage: shop card|relic|potion <index>", False
            self.enqueue(build_command_json(command, index))
            return f"Queued {command} {index}", False
        if cmd == "start":
            payload_args = " ".join(args).strip()
            self.enqueue(build_command_json("START", payload_args or None))
            return f"Queued START {payload_args}".rstrip(), False
        if cmd == "choose":
            if len(args) < 2:
                return "Usage: choose <choice_id> <index...|skip>", False
            choice_id = args[0]
            selection = " ".join(args[1:])
            self.enqueue(f"CHOOSE_RESPONSE {choice_id} {selection}")
            if selection != "skip":
                self.shared.consume_choice(choice_id)
            return f"Queued CHOOSE_RESPONSE {choice_id} {selection}", False

        return "Unknown command. Type 'help'.", False

    def start_shell(self) -> None:
        self.server = InteractiveShellServer((self.host, self.port), self)
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()
        log(f"[interactive] shell listening on {self.host}:{self.port}")

        if self.auto_launch_client:
            self.try_launch_client()

    def try_launch_client(self) -> None:
        if os.name != "nt":
            log("[interactive] auto-launch client only implemented for Windows.")
            return

        client_path = os.path.join(os.path.dirname(__file__), "interactive_shell_client.py")
        if not os.path.exists(client_path):
            log("[interactive] client script missing:", client_path)
            return

        try:
            subprocess.Popen(
                ["python", "-u", client_path, "--host", self.host, "--port", str(self.port)],
                creationflags=subprocess.CREATE_NEW_CONSOLE,  # type: ignore[attr-defined]
                close_fds=True,
            )
            log("[interactive] launched shell client window.")
        except Exception as ex:
            log("[interactive] failed to launch shell client:", ex)

    def stop(self) -> None:
        self.stop_event.set()
        if self.server is not None:
            try:
                self.server.shutdown()
            except Exception:
                pass
            try:
                self.server.server_close()
            except Exception:
                pass


def stdin_reader_loop(controller: InteractiveShellController) -> None:
    while not controller.stop_event.is_set():
        line = read_line()
        if line is None:
            log("[interactive] stdin closed; stopping.")
            controller.stop_event.set()
            return

        line = line.strip()
        if not line:
            continue

        try:
            msg = json.loads(line)
        except Exception:
            log("[interactive] non-JSON from mod:", line)
            continue

        controller.shared.update_message(msg)
        msg_type = msg.get("type", "")
        if msg_type == "state":
            state = msg
            screen = state.get("screen")
            available = state.get("available_commands") or []
            log(f"[interactive] state screen={screen} cmds={available}")
        elif msg_type == "choice_request":
            choice_id = msg.get("choice_id")
            choice_type = msg.get("choice_type")
            option_count = len(msg.get("options") or [])
            log(f"[interactive] choice_request id={choice_id} type={choice_type} options={option_count}")
        elif msg_type == "error":
            log("[interactive] mod error:", msg)
        else:
            log("[interactive] mod message:", msg)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Interactive CommunicateTheSpire2 controller.")
    parser.add_argument("--host", default=os.environ.get("CTS2_SHELL_HOST", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("CTS2_SHELL_PORT", "8765")))
    parser.add_argument(
        "--no-auto-client",
        action="store_true",
        help="Do not auto-launch controller/interactive_shell_client.py",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    controller = InteractiveShellController(
        host=args.host,
        port=args.port,
        auto_launch_client=not args.no_auto_client,
    )

    send_line("ready")
    log("[interactive] sent ready")

    hello_line = read_line()
    if hello_line is None:
        log("[interactive] no hello received; exiting.")
        return

    try:
        hello = json.loads(hello_line.strip())
        controller.shared.set_hello(hello)
        log(
            "[interactive] hello protocol_version={} mod_version={}".format(
                hello.get("protocol_version"),
                hello.get("mod_version"),
            )
        )
    except Exception as ex:
        log("[interactive] failed to parse hello:", ex)

    controller.start_shell()

    stdin_thread = threading.Thread(target=stdin_reader_loop, args=(controller,), daemon=True)
    stdin_thread.start()

    controller.enqueue(build_command_json("STATE"))

    while not controller.stop_event.is_set():
        try:
            outbound = controller.outbound_queue.get(timeout=0.2)
        except queue.Empty:
            continue
        send_line(outbound)

    controller.stop()
    log("[interactive] stopped.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
