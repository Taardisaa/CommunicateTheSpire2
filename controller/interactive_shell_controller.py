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
import time
from typing import Any

RESPONSE_END = "__CTS2_EOT__"


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


def display_map_point_type(raw: Any) -> str:
    text = first_non_empty(raw)
    if text.lower() == "unknown":
        return "Unrevealed"
    return text


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
    if state.get("in_run") and not state.get("screen"):
        lines.append("note: run exists but current room/screen is null (loading or failed transition).")

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
        for enemy_idx, enemy in enumerate(enemies):
            lines.append(
                "  enemy[{idx}] combat_id={cid} {name} hp={hp}/{max_hp} block={block} intent={intent} dmg={dmg}x{hits}".format(
                    idx=enemy_idx,
                    cid=enemy.get("combat_id", "?"),
                    name=first_non_empty(enemy.get("name")),
                    hp=enemy.get("current_hp", "?"),
                    max_hp=enemy.get("max_hp", "?"),
                    block=enemy.get("block", "?"),
                    intent=first_non_empty(enemy.get("intent")),
                    dmg=enemy.get("damage", 0),
                    hits=enemy.get("hits", 0),
                )
            )
        playable_cards = 0
        for card in hand:
            playable = bool(card.get("playable"))
            if playable:
                playable_cards += 1
            lines.append(
                "  hand[{idx}] {prefix}{name} id={id} cost={cost} playable={playable} target={target}".format(
                    idx=card.get("index"),
                    prefix="" if playable else "# ",
                    name=first_non_empty(card.get("name"), card.get("id")),
                    id=first_non_empty(card.get("id")),
                    cost=card.get("energy_cost"),
                    playable=playable,
                    target=first_non_empty(card.get("target_type")),
                )
            )
        if hand and playable_cards == 0:
            lines.append("  note: no playable cards this turn (PLAY unavailable).")

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
    current_coord = map_state.get("current_coord") or {}
    if current_coord:
        lines.append(
            "map current: col={} row={} type={}".format(
                current_coord.get("col"),
                current_coord.get("row"),
                display_map_point_type(current_coord.get("point_type")),
            )
        )
    reachable = map_state.get("reachable") or []
    if reachable:
        lines.append("map reachable (use: map <index>):")
        for i, node in enumerate(reachable):
            lines.append(
                "  [{}] col={} row={} type={}".format(
                    i,
                    node.get("col"),
                    node.get("row"),
                    display_map_point_type(node.get("point_type")),
                )
            )

    rewards = state.get("rewards") or []
    if rewards:
        lines.append("rewards:")
        for reward in rewards:
            lines.append(
                "  [{}] type={} name={} id={} amount={} enabled={}".format(
                    reward.get("index"),
                    reward.get("type"),
                    first_non_empty(reward.get("name")),
                    reward.get("id"),
                    reward.get("amount"),
                    reward.get("enabled"),
                )
            )

    pending_choice = state.get("pending_choice") or {}
    if pending_choice:
        lines.append(
            "pending_choice: id={} type={} min={} max={}".format(
                pending_choice.get("choice_id"),
                pending_choice.get("choice_type"),
                pending_choice.get("min_select"),
                pending_choice.get("max_select"),
            )
        )
        for opt in pending_choice.get("options") or []:
            lines.append(
                "  option[{}] id={} name={}".format(
                    opt.get("index"),
                    first_non_empty(opt.get("id")),
                    first_non_empty(opt.get("name")),
                )
            )
        alts = pending_choice.get("alternatives") or []
        if alts:
            lines.append("  alternatives: " + ", ".join(str(x) for x in alts))

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

    def send_block(self, text: str) -> None:
        payload = text.rstrip("\n") + "\n" + RESPONSE_END + "\n"
        self.send_text(payload)

    def handle(self) -> None:
        self.send_block(self.server.controller.banner_with_state())
        while not self.server.controller.stop_event.is_set():
            line = self.rfile.readline()
            if not line:
                return

            raw = line.decode("utf-8", errors="replace").strip()
            if not raw:
                continue

            response, close_client = self.server.controller.handle_shell_command(raw)
            if self.server.controller.should_append_state(raw):
                response = response + "\n\n" + self.server.controller.latest_state_text()
            self.send_block(response)
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

    def banner_with_state(self) -> str:
        return self.banner() + "\n" + self.latest_state_text()

    def latest_state_text(self) -> str:
        state, _, _ = self.shared.snapshot()
        return state_summary(state)

    def should_append_state(self, raw: str) -> bool:
        # Command handlers now include explicit state refresh/results where needed.
        return False

    def _state_timestamp(self, state: dict[str, Any] | None) -> int | None:
        if not state:
            return None
        ts = state.get("timestamp_unix_ms")
        if isinstance(ts, int):
            return ts
        return None

    def request_fresh_state(self, timeout_s: float = 1.5) -> tuple[dict[str, Any] | None, bool]:
        before_state, _, _ = self.shared.snapshot()
        before_ts = self._state_timestamp(before_state)
        self.enqueue(build_command_json("STATE"))

        deadline = time.time() + max(0.05, timeout_s)
        while time.time() < deadline:
            state, _, _ = self.shared.snapshot()
            state_ts = self._state_timestamp(state)
            if state is not None and (before_ts is None or state_ts != before_ts):
                return state, True
            time.sleep(0.05)

        state, _, _ = self.shared.snapshot()
        return state, False

    def _available(self, state: dict[str, Any] | None) -> list[str]:
        if not state:
            return []
        raw = state.get("available_commands") or []
        return [str(x) for x in raw]

    def _require_available(self, state: dict[str, Any] | None, command: str) -> str | None:
        available = self._available(state)
        if command in available:
            return None
        if not available:
            return f"{command} is not available: no state/commands yet."
        return f"{command} is not available in current state. available_commands: {', '.join(available)}"

    def queue_and_refresh(
        self,
        command: str,
        args: str | None = None,
        timeout_s: float = 1.5,
        expect_change: bool = False,
        settle_timeout_s: float = 1.5,
    ) -> str:
        before_state, before_non_state, _ = self.shared.snapshot()
        before_state_sig = json.dumps(before_state, sort_keys=True, ensure_ascii=False) if before_state is not None else None
        before_sig = json.dumps(before_non_state, sort_keys=True, ensure_ascii=False) if before_non_state is not None else None
        self.enqueue(build_command_json(command, args))
        state, fresh = self.request_fresh_state(timeout_s=timeout_s)

        # Some actions (reward/proceed/map transitions) apply after animations/fades.
        # If first refresh looks unchanged, wait once more before returning.
        if expect_change and state is not None:
            state_sig = json.dumps(state, sort_keys=True, ensure_ascii=False)
            if before_state_sig is not None and state_sig == before_state_sig:
                state, fresh2 = self.request_fresh_state(timeout_s=settle_timeout_s)
                fresh = fresh or fresh2

        _, after_non_state, _ = self.shared.snapshot()
        after_sig = json.dumps(after_non_state, sort_keys=True, ensure_ascii=False) if after_non_state is not None else None
        queued = f"Queued {command}{(' ' + args) if args else ''}"
        extra: list[str] = []
        if after_non_state and after_sig != before_sig and str(after_non_state.get("type")) == "error":
            extra.append("mod error: " + json.dumps(after_non_state, ensure_ascii=False))
        if state is None:
            if extra:
                return queued + "\n" + "\n".join(extra) + "\n\nNo state received yet."
            return queued + "\n\nNo state received yet."
        if command in {"START", "CONTINUE"} and not bool(state.get("in_run")):
            extra.append(
                f"note: {command} was queued but run has not started yet; check controller stderr/mod log for async load errors if this persists."
            )
        if fresh:
            if extra:
                return queued + "\n" + "\n".join(extra) + "\n\n" + state_summary(state)
            return queued + "\n\n" + state_summary(state)
        if extra:
            return queued + "\n" + "\n".join(extra) + "\n\n(note: STATE refresh timed out; showing latest snapshot)\n" + state_summary(state)
        return queued + "\n\n(note: STATE refresh timed out; showing latest snapshot)\n" + state_summary(state)

    def command_help(self, topic: str | None = None) -> str:
        help_topics: dict[str, str] = {
            "help": "\n".join(
                [
                    "help",
                    "Usage: help [command]",
                    "Show command list, or detailed help for one command.",
                    "Examples:",
                    "  help",
                    "  help start",
                    "  help map",
                ]
            ),
            "state": "\n".join(
                [
                    "state",
                    "Usage: state",
                    "Request a fresh STATE and print compact summary.",
                    "Includes map reachable nodes, combat hand, rewards, and available_commands.",
                    "In hand list, '# ' prefix marks unplayable cards.",
                    "Example:",
                    "  state",
                ]
            ),
            "json": "\n".join(
                [
                    "json",
                    "Usage: json",
                    "Request a fresh STATE and print full state JSON.",
                    "Example:",
                    "  json",
                ]
            ),
            "cmds": "\n".join(
                [
                    "cmds",
                    "Usage: cmds",
                    "Print state.available_commands from latest snapshot.",
                    "Use this before sending actions.",
                    "Example:",
                    "  cmds",
                ]
            ),
            "pending": "\n".join(
                [
                    "pending",
                    "Usage: pending",
                    "Show pending choice_request entries (choice_id, type, option counts).",
                    "Use with: choose <choice_id> <index...|skip>",
                    "Example:",
                    "  pending",
                ]
            ),
            "refresh": "\n".join(
                [
                    "refresh",
                    "Usage: refresh",
                    "Queue STATE command to request immediate snapshot from mod.",
                    "Example:",
                    "  refresh",
                ]
            ),
            "ping": "\n".join(
                [
                    "ping",
                    "Usage: ping",
                    "Send PING command; mod should respond with pong.",
                    "Example:",
                    "  ping",
                ]
            ),
            "send": "\n".join(
                [
                    "send",
                    "Usage: send <raw protocol line>",
                    "Send any raw protocol line directly to the mod.",
                    "Examples:",
                    "  send STATE",
                    "  send {\"type\":\"command\",\"command\":\"PING\"}",
                ]
            ),
            "play": "\n".join(
                [
                    "play",
                    "Usage: play <handIndex> [targetIndex]",
                    "Queue PLAY with hand index and optional target.",
                    "Use card indices from state combat hand entries: hand[<index>].",
                    "If target_type is AnyEnemy/AnyAlly and multiple targets exist, pass targetIndex from enemy[<index>].",
                    "Examples:",
                    "  play 0",
                    "  play 1 0",
                ]
            ),
            "end": "\n".join(
                [
                    "end",
                    "Usage: end",
                    "Queue END turn in combat.",
                    "Example:",
                    "  end",
                ]
            ),
            "event": "\n".join(
                [
                    "event",
                    "Usage: event <index>",
                    "Queue EVENT_CHOOSE for current event option index.",
                    "Indices are listed in state under event options.",
                    "Example:",
                    "  event 0",
                ]
            ),
            "rest": "\n".join(
                [
                    "rest",
                    "Usage: rest <index>",
                    "Queue REST_CHOOSE at rest site.",
                    "Indices come from state rest options.",
                    "Example:",
                    "  rest 1",
                ]
            ),
            "map": "\n".join(
                [
                    "map",
                    "Usage: map <index>",
                    "Queue MAP_CHOOSE using index in state map reachable list.",
                    "If called without index, prints current reachable nodes.",
                    "Workflow: refresh -> read \"map reachable\" -> map <index>.",
                    "Example:",
                    "  map 0",
                ]
            ),
            "proceed": "\n".join(
                [
                    "proceed",
                    "Usage: proceed",
                    "Queue PROCEED to leave current room/screen when available.",
                    "Valid at event/rest/treasure/shop, and rewards after all reward picks are resolved.",
                    "Example:",
                    "  proceed",
                ]
            ),
            "return": "\n".join(
                [
                    "return",
                    "Usage: return",
                    "Queue RETURN (close map overlay / close shop inventory).",
                    "Example:",
                    "  return",
                ]
            ),
            "return_menu": "\n".join(
                [
                    "return_menu",
                    "Usage: return_menu",
                    "Queue RETURN_TO_MENU to confirm game-over/post-run transition.",
                    "Example:",
                    "  return_menu",
                ]
            ),
            "potion": "\n".join(
                [
                    "potion",
                    "Usage: potion use <slot> [targetIndex]",
                    "   or: potion discard <slot>",
                    "Use/discard potion by slot index from state potions list.",
                    "Examples:",
                    "  potion use 0",
                    "  potion use 1 0",
                    "  potion discard 2",
                ]
            ),
            "reward": "\n".join(
                [
                    "reward",
                    "Usage: reward <index>",
                    "Queue REWARD_CHOOSE on combat reward screen.",
                    "If called without index, prints current reward entries.",
                    "Indices come from state rewards list.",
                    "Example:",
                    "  reward 0",
                ]
            ),
            "boss": "\n".join(
                [
                    "boss",
                    "Usage: boss <index>",
                    "Queue BOSS_REWARD_CHOOSE on boss relic screen.",
                    "Indices come from state boss rewards list.",
                    "Example:",
                    "  boss 2",
                ]
            ),
            "shop": "\n".join(
                [
                    "shop",
                    "Usage: shop card <index>",
                    "   or: shop relic <index>",
                    "   or: shop potion <index>",
                    "Queue direct shop purchase commands by index from state shop section.",
                    "Examples:",
                    "  shop card 0",
                    "  shop relic 1",
                    "  shop potion 0",
                ]
            ),
            "purge": "\n".join(
                [
                    "purge",
                    "Usage: purge",
                    "Queue SHOP_PURGE (card removal) in shop.",
                    "Usually followed by choice_request; answer with choose <id> <index>.",
                    "Example:",
                    "  purge",
                ]
            ),
            "start": "\n".join(
                [
                    "start",
                    "Usage: start [character] [seed] [ascension]",
                    "Queue START when not in run.",
                    "character can be index (0-4) or id (Ironclad, Silent, Regent, Necrobinder, Defect).",
                    "seed is optional numeric seed; ascension optional integer.",
                    "Examples:",
                    "  start",
                    "  start Ironclad",
                    "  start Silent 123456789 20",
                    "  start 0 987654321 10",
                ]
            ),
            "continue": "\n".join(
                [
                    "continue",
                    "Usage: continue",
                    "Queue CONTINUE to load the current saved run from main menu.",
                    "This follows the same load path as the in-game Continue button.",
                    "Examples:",
                    "  continue",
                    "  cont",
                ]
            ),
            "choose": "\n".join(
                [
                    "choose",
                    "Usage: choose <choice_id> <index...|skip>",
                    "Send CHOOSE_RESPONSE for pending choice_request.",
                    "Find choice_id with: pending",
                    "Examples:",
                    "  choose a1b2c3d4e5f6 0",
                    "  choose a1b2c3d4e5f6 0 2",
                    "  choose a1b2c3d4e5f6 skip",
                ]
            ),
            "quit": "\n".join(
                [
                    "quit",
                    "Usage: quit",
                    "Disconnect shell client.",
                    "Example:",
                    "  quit",
                ]
            ),
        }

        aliases = {
            "r": "refresh",
            "q": "quit",
            "cont": "continue",
        }

        if topic:
            key = aliases.get(topic.lower(), topic.lower())
            detail = help_topics.get(key)
            if detail is not None:
                return detail
            return f"Unknown help topic '{topic}'. Type 'help' for command list."

        return "\n".join(
            [
                "Commands:",
                "  help [command]           Show help or detailed command help.",
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
                "  return_menu              RETURN_TO_MENU.",
                "  potion use/discard ...   POTION commands.",
                "  reward <index>           REWARD_CHOOSE.",
                "  boss <index>             BOSS_REWARD_CHOOSE.",
                "  shop card|relic|potion   SHOP buy commands.",
                "  purge                    SHOP_PURGE.",
                "  start [char] [seed] [asc] START run.",
                "  continue                 CONTINUE saved run.",
                "  choose <id> <...|skip>   CHOOSE_RESPONSE.",
                "  quit                     Disconnect this shell client.",
                "",
                "Tip: type 'help start' (or any command) for details and examples.",
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
        if cmd == "cont":
            cmd = "continue"

        if cmd == "help":
            return self.command_help(args[0] if args else None), False
        if cmd == "quit":
            return "Bye.", True

        state, last_non_state, pending = self.shared.snapshot()

        if cmd == "state":
            fresh_state, fresh = self.request_fresh_state(timeout_s=1.5)
            if fresh_state is None:
                return "No state received yet.", False
            if fresh:
                return state_summary(fresh_state), False
            return "(note: STATE refresh timed out; showing latest snapshot)\n" + state_summary(fresh_state), False
        if cmd == "json":
            fresh_state, fresh = self.request_fresh_state(timeout_s=1.5)
            if not fresh_state:
                return "No state received yet.", False
            if fresh:
                return json.dumps(fresh_state, indent=2, ensure_ascii=False), False
            return "(note: STATE refresh timed out; showing latest snapshot)\n" + json.dumps(fresh_state, indent=2, ensure_ascii=False), False
        if cmd == "cmds":
            fresh_state, _ = self.request_fresh_state(timeout_s=1.0)
            if not fresh_state:
                return "No state received yet.", False
            available = fresh_state.get("available_commands") or []
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
            fresh_state, fresh = self.request_fresh_state(timeout_s=1.5)
            if fresh_state is None:
                return "Requested STATE, but no state received yet.", False
            if fresh:
                return state_summary(fresh_state), False
            return "(note: STATE refresh timed out; showing latest snapshot)\n" + state_summary(fresh_state), False
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
            unavailable = self._require_available(state, "PLAY")
            if unavailable is not None:
                return unavailable, False
            payload_args = args[0] if len(args) == 1 else f"{args[0]} {args[1]}"
            return self.queue_and_refresh("PLAY", payload_args, timeout_s=1.5), False
        if cmd == "end":
            unavailable = self._require_available(state, "END")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("END", timeout_s=1.5), False
        if cmd == "event":
            if len(args) != 1:
                return "Usage: event <index>", False
            unavailable = self._require_available(state, "EVENT_CHOOSE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("EVENT_CHOOSE", args[0], timeout_s=1.5), False
        if cmd == "rest":
            if len(args) != 1:
                return "Usage: rest <index>", False
            unavailable = self._require_available(state, "REST_CHOOSE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("REST_CHOOSE", args[0], timeout_s=1.5), False
        if cmd == "map":
            if len(args) != 1:
                map_state = (state or {}).get("map") or {}
                reachable = map_state.get("reachable") or []
                if reachable:
                    lines = ["Usage: map <index>", "Current reachable nodes:"]
                    for i, node in enumerate(reachable):
                        lines.append(
                            "  [{}] col={} row={} type={}".format(
                                i,
                                node.get("col"),
                                node.get("row"),
                                display_map_point_type(node.get("point_type")),
                            )
                        )
                    return "\n".join(lines), False
                return "Usage: map <index>", False
            unavailable = self._require_available(state, "MAP_CHOOSE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("MAP_CHOOSE", args[0], timeout_s=2.0, expect_change=True, settle_timeout_s=2.0), False
        if cmd == "proceed":
            unavailable = self._require_available(state, "PROCEED")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("PROCEED", timeout_s=2.0, expect_change=True, settle_timeout_s=2.0), False
        if cmd == "return":
            unavailable = self._require_available(state, "RETURN")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("RETURN", timeout_s=1.5), False
        if cmd in {"return_menu", "rtm"}:
            unavailable = self._require_available(state, "RETURN_TO_MENU")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("RETURN_TO_MENU", timeout_s=2.0, expect_change=True, settle_timeout_s=2.0), False
        if cmd == "reward":
            if len(args) != 1:
                rewards = (state or {}).get("rewards") or []
                if rewards:
                    lines = ["Usage: reward <index>", "Current rewards:"]
                    for reward in rewards:
                        lines.append(
                            "  [{}] type={} name={} id={} enabled={}".format(
                                reward.get("index"),
                                reward.get("type"),
                                first_non_empty(reward.get("name")),
                                reward.get("id"),
                                reward.get("enabled"),
                            )
                        )
                    return "\n".join(lines), False
                return "Usage: reward <index>", False
            unavailable = self._require_available(state, "REWARD_CHOOSE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("REWARD_CHOOSE", args[0], timeout_s=2.0, expect_change=True, settle_timeout_s=2.5), False
        if cmd == "boss":
            if len(args) != 1:
                return "Usage: boss <index>", False
            unavailable = self._require_available(state, "BOSS_REWARD_CHOOSE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("BOSS_REWARD_CHOOSE", args[0], timeout_s=2.0, expect_change=True, settle_timeout_s=2.0), False
        if cmd == "purge":
            unavailable = self._require_available(state, "SHOP_PURGE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("SHOP_PURGE", timeout_s=2.0), False
        if cmd == "potion":
            if len(args) < 2:
                return "Usage: potion use <slot> [target] | potion discard <slot>", False
            unavailable = self._require_available(state, "POTION")
            if unavailable is not None:
                return unavailable, False
            action = args[0].lower()
            if action == "use":
                payload_args = args[1] if len(args) == 2 else f"{args[1]} {args[2]}"
                return self.queue_and_refresh("POTION", f"use {payload_args}", timeout_s=1.5), False
            if action == "discard":
                return self.queue_and_refresh("POTION", f"discard {args[1]}", timeout_s=1.5), False
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
            unavailable = self._require_available(state, command)
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh(command, index, timeout_s=2.0), False
        if cmd == "start":
            unavailable = self._require_available(state, "START")
            if unavailable is not None:
                return unavailable, False
            payload_args = " ".join(args).strip()
            return self.queue_and_refresh("START", payload_args or None, timeout_s=8.0), False
        if cmd == "continue":
            unavailable = self._require_available(state, "CONTINUE")
            if unavailable is not None:
                return unavailable, False
            return self.queue_and_refresh("CONTINUE", timeout_s=8.0), False
        if cmd == "choose":
            if len(args) < 2:
                return "Usage: choose <choice_id> <index...|skip>", False
            choice_id = args[0]
            selection = " ".join(args[1:])
            self.enqueue(f"CHOOSE_RESPONSE {choice_id} {selection}")
            if selection != "skip":
                self.shared.consume_choice(choice_id)
            fresh_state, fresh = self.request_fresh_state(timeout_s=2.0)
            response = f"Queued CHOOSE_RESPONSE {choice_id} {selection}"
            if fresh_state is None:
                return response, False
            if fresh:
                return response + "\n\n" + state_summary(fresh_state), False
            return response + "\n\n(note: STATE refresh timed out; showing latest snapshot)\n" + state_summary(fresh_state), False

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
            connect_host = self.host
            if connect_host in ("0.0.0.0", "::", ""):
                connect_host = "127.0.0.1"
            subprocess.Popen(
                ["python", "-u", client_path, "--host", connect_host, "--port", str(self.port)],
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
