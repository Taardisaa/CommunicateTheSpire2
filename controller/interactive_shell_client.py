#!/usr/bin/env python3
"""
Simple terminal client for interactive_shell_controller.py.
"""
from __future__ import annotations

import argparse
import socket
import sys
import time
from typing import Any

RESPONSE_END = "__CTS2_EOT__"


def log(*args: Any) -> None:
    print(*args, file=sys.stderr, flush=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Interactive shell client for CommunicateTheSpire2.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--connect-timeout", type=float, default=30.0)
    parser.add_argument("--retries", type=int, default=1, help="Number of 1s retry attempts.")
    return parser.parse_args()


def connect_with_retry(host: str, port: int, connect_timeout: float, retries: int) -> socket.socket:
    last_error: Exception | None = None
    attempts = max(1, retries)
    for attempt in range(1, attempts + 1):
        try:
            sock = socket.create_connection((host, port), timeout=connect_timeout)
            sock.settimeout(None)  # Keep shell session blocking; no idle timeout disconnect.
            return sock
        except Exception as ex:
            last_error = ex
            if attempt == attempts:
                break
            log(f"[shell-client] connect attempt {attempt}/{attempts} failed: {ex}; retrying...")
            time.sleep(4.0)
    raise RuntimeError(f"Failed to connect to {host}:{port} after {attempts} attempts: {last_error}")


def read_block(reader) -> str:
    lines: list[str] = []
    while True:
        line = reader.readline()
        if line == "":
            raise EOFError("Server closed connection.")
        text = line.rstrip("\n")
        if text == RESPONSE_END:
            return "\n".join(lines).rstrip()
        lines.append(text)


def main() -> None:
    args = parse_args()

    with connect_with_retry(args.host, args.port, args.connect_timeout, args.retries) as sock:
        reader = sock.makefile("r", encoding="utf-8", newline="\n")
        writer = sock.makefile("w", encoding="utf-8", newline="\n")
        try:
            welcome = read_block(reader)
            if welcome:
                print(welcome)

            while True:
                try:
                    line = input("cts2> ")
                except EOFError:
                    line = "quit"

                if not line.strip():
                    continue

                writer.write(line.strip() + "\n")
                writer.flush()

                response = read_block(reader)
                if response:
                    print(response)

                if line.strip().lower() == "quit":
                    return
        finally:
            try:
                reader.close()
            except Exception:
                print("[shell-client] Warning: failed to close reader", file=sys.stderr)
            try:
                writer.close()
            except Exception:
                print("[shell-client] Warning: failed to close writer", file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
