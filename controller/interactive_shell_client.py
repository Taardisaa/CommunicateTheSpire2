#!/usr/bin/env python3
"""
Simple terminal client for interactive_shell_controller.py.
"""
from __future__ import annotations

import argparse
import socket
import sys
import threading
from typing import Any


def log(*args: Any) -> None:
    print(*args, file=sys.stderr, flush=True)


def reader_loop(sock: socket.socket, stop_event: threading.Event) -> None:
    stream = sock.makefile("rb")
    try:
        while not stop_event.is_set():
            chunk = stream.readline()
            if not chunk:
                stop_event.set()
                return
            sys.stdout.write(chunk.decode("utf-8", errors="replace"))
            sys.stdout.flush()
    except Exception as ex:
        log("[shell-client] reader error:", ex)
        stop_event.set()
    finally:
        try:
            stream.close()
        except Exception:
            pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Interactive shell client for CommunicateTheSpire2.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    stop_event = threading.Event()

    with socket.create_connection((args.host, args.port), timeout=10) as sock:
        reader = threading.Thread(target=reader_loop, args=(sock, stop_event), daemon=True)
        reader.start()

        writer = sock.makefile("wb")
        try:
            while not stop_event.is_set():
                try:
                    line = input()
                except EOFError:
                    line = "quit"
                writer.write((line.strip() + "\n").encode("utf-8"))
                writer.flush()
                if line.strip().lower() == "quit":
                    stop_event.set()
                    break
        finally:
            try:
                writer.close()
            except Exception:
                pass


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
