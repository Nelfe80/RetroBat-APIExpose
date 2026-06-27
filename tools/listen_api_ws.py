#!/usr/bin/env python3
"""
Listen to APIExpose WebSocket events.

The API broadcasts EventEnvelope JSON messages on ws://127.0.0.1:12345/ws.
This script intentionally avoids third-party dependencies so it can run from a
plain Python installation bundled with many dev machines.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time
from datetime import datetime
from typing import Any
from urllib.parse import urlparse, urlunparse


GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
DEFAULT_URL = "ws://127.0.0.1:12345/ws"
SUPPORTED_STREAMS = (
    "frontend",
    "marquee",
    "topper",
    "instruction-card",
    "panel",
    "ingame",
    "arcade",
    "score",
    "timer",
    "hiscore",
    "media",
    "roms",
    "system",
    "control",
)


def timestamp() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def log(message: str, *, stream: Any = sys.stdout, log_file: Any | None = None) -> None:
    print(f"[{timestamp()}] {message}", file=stream, flush=True)
    if log_file is not None:
        print(f"[{timestamp()}] {message}", file=log_file, flush=True)


def build_websocket_key() -> str:
    return base64.b64encode(os.urandom(16)).decode("ascii")


def normalize_stream(stream: str | None) -> str:
    normalized = (stream or "").strip().strip("/").lower()
    aliases = {
        "": "",
        "ws": "",
        "global": "",
        "debug": "",
        "score": "score",
        "scores": "score",
        "highscore": "hiscore",
        "highscores": "hiscore",
        "hiscores": "hiscore",
        "instructioncard": "instruction-card",
        "instruction_card": "instruction-card",
        "instructions": "instruction-card",
    }
    return aliases.get(normalized, normalized)


def build_stream_url(url: str, stream: str | None) -> str:
    normalized_stream = normalize_stream(stream)
    if not normalized_stream:
        return url

    parsed = urlparse(url)
    if parsed.scheme != "ws":
        raise ValueError("Only ws:// URLs are supported.")

    base_path = parsed.path or "/ws"
    path_parts = [part for part in base_path.split("/") if part]
    if not path_parts:
        path_parts = ["ws"]

    if path_parts[-1].lower() in SUPPORTED_STREAMS:
        path_parts[-1] = normalized_stream
    else:
        path_parts.append(normalized_stream)

    return urlunparse((
        parsed.scheme,
        parsed.netloc,
        "/" + "/".join(path_parts),
        parsed.params,
        parsed.query,
        parsed.fragment,
    ))


def print_streams() -> None:
    print("global (/ws)")
    for stream in SUPPORTED_STREAMS:
        print(stream)


def recv_until(sock: socket.socket, marker: bytes, timeout: float) -> bytes:
    sock.settimeout(timeout)
    data = bytearray()
    while marker not in data:
        chunk = sock.recv(4096)
        if not chunk:
            raise ConnectionError("connection closed during HTTP handshake")
        data.extend(chunk)
        if len(data) > 65536:
            raise ConnectionError("HTTP handshake response is too large")
    return bytes(data)


def connect_websocket(url: str, timeout: float, read_timeout: float | None) -> socket.socket:
    parsed = urlparse(url)
    if parsed.scheme != "ws":
        raise ValueError("Only ws:// URLs are supported.")

    host = parsed.hostname or "127.0.0.1"
    port = parsed.port or 80
    path = parsed.path or "/"
    if parsed.query:
        path += "?" + parsed.query

    key = build_websocket_key()
    expected_accept = base64.b64encode(
        hashlib.sha1((key + GUID).encode("ascii")).digest()
    ).decode("ascii")

    sock = socket.create_connection((host, port), timeout=timeout)
    request = (
        f"GET {path} HTTP/1.1\r\n"
        f"Host: {host}:{port}\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "\r\n"
    )
    sock.sendall(request.encode("ascii"))
    response = recv_until(sock, b"\r\n\r\n", timeout)
    header_text = response.decode("iso-8859-1", errors="replace")
    status_line = header_text.splitlines()[0] if header_text else ""
    if " 101 " not in status_line:
        raise ConnectionError(f"WebSocket upgrade failed: {status_line}")

    headers: dict[str, str] = {}
    for line in header_text.split("\r\n")[1:]:
        if ":" in line:
            name, value = line.split(":", 1)
            headers[name.strip().lower()] = value.strip()

    if headers.get("sec-websocket-accept") != expected_accept:
        raise ConnectionError("WebSocket handshake accept key mismatch")

    sock.settimeout(read_timeout)
    return sock


def recv_exact(sock: socket.socket, count: int) -> bytes:
    data = bytearray()
    while len(data) < count:
        try:
            chunk = sock.recv(count - len(data))
        except socket.timeout:
            if not data:
                raise TimeoutError
            continue
        if not chunk:
            raise ConnectionError("connection closed")
        data.extend(chunk)
    return bytes(data)


def read_frame(sock: socket.socket) -> tuple[int, bytes, bool]:
    header = recv_exact(sock, 2)
    first, second = header
    fin = (first & 0x80) != 0
    opcode = first & 0x0F
    masked = (second & 0x80) != 0
    length = second & 0x7F

    if length == 126:
        length = struct.unpack("!H", recv_exact(sock, 2))[0]
    elif length == 127:
        length = struct.unpack("!Q", recv_exact(sock, 8))[0]

    mask = recv_exact(sock, 4) if masked else b""
    payload = recv_exact(sock, length) if length else b""
    if masked:
        payload = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))

    return opcode, payload, fin


def send_close(sock: socket.socket) -> None:
    try:
        sock.sendall(b"\x88\x80" + os.urandom(4))
    except OSError:
        pass


def format_event(text: str, raw: bool) -> str:
    if raw:
        return text

    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        return text

    event_type = payload.get("type") or payload.get("Type") or "event"
    body = payload.get("payload", payload.get("Payload", payload))
    formatted_body = json.dumps(body, ensure_ascii=False, indent=2)
    return f"{event_type}\n{formatted_body}"


def write_event(text: str, raw: bool, log_file: Any | None) -> None:
    formatted = format_event(text, raw)
    log("Event received.", log_file=log_file)
    print(formatted, flush=True)
    if log_file is not None:
        print(formatted, file=log_file, flush=True)


def listen(
    url: str,
    timeout: float,
    read_timeout: float | None,
    raw: bool,
    once: bool,
    log_file: Any | None,
) -> None:
    log(f"Connecting to {url}...", log_file=log_file)
    with connect_websocket(url, timeout, read_timeout) as sock:
        log(f"Listening on {url}. Waiting for API events...", log_file=log_file)
        message_parts: list[bytes] = []

        while True:
            try:
                opcode, payload, fin = read_frame(sock)
            except TimeoutError:
                continue

            if opcode == 0x8:
                log("WebSocket closed by API.", log_file=log_file)
                return

            if opcode == 0x9:
                # Ping: answer with pong carrying the same payload.
                sock.sendall(b"\x8A" + bytes([len(payload)]) + payload)
                continue

            if opcode in (0x1, 0x0):
                message_parts.append(payload)
                if not fin:
                    continue

                text = b"".join(message_parts).decode("utf-8", errors="replace")
                message_parts.clear()
                write_event(text, raw, log_file)
                if once:
                    send_close(sock)
                    return


def main() -> int:
    parser = argparse.ArgumentParser(description="Listen to APIExpose WebSocket events.")
    parser.add_argument("--url", default=DEFAULT_URL, help="Base WebSocket URL.")
    parser.add_argument(
        "--stream",
        help=(
            "Specialized stream to listen to. Examples: marquee, topper, "
            "instruction-card, panel, ingame, arcade, hiscore, media, roms, "
            "system, control. Empty/default listens to the global /ws stream."
        ),
    )
    parser.add_argument("--list-streams", action="store_true", help="List known streams and exit.")
    parser.add_argument("--timeout", type=float, default=5.0, help="Connection timeout in seconds.")
    parser.add_argument(
        "--read-timeout",
        type=float,
        default=1.0,
        help="Internal socket read timeout in seconds. Timeouts are silent keep-alive waits.",
    )
    parser.add_argument("--retry-delay", type=float, default=1.0, help="Reconnect delay in seconds.")
    parser.add_argument("--raw", action="store_true", help="Print raw JSON messages.")
    parser.add_argument("--log-file", help="Also append all listener output to this UTF-8 file.")
    parser.add_argument("--once", action="store_true", help="Exit after the first event.")
    parser.add_argument("--no-retry", action="store_true", help="Do not reconnect after errors.")
    args = parser.parse_args()

    if args.list_streams:
        print_streams()
        return 0

    url = build_stream_url(args.url, args.stream)
    log_file = None
    try:
        if args.log_file:
            log_dir = os.path.dirname(os.path.abspath(args.log_file))
            if log_dir:
                os.makedirs(log_dir, exist_ok=True)
            log_file = open(args.log_file, "a", encoding="utf-8")

        while True:
            try:
                read_timeout = None if args.read_timeout <= 0 else args.read_timeout
                listen(url, args.timeout, read_timeout, args.raw, args.once, log_file)
                return 0
            except KeyboardInterrupt:
                log("Stopped.", log_file=log_file)
                return 0
            except Exception as exc:  # Diagnostic tool: keep the listener alive by default.
                log(f"Connection error: {exc}", stream=sys.stderr, log_file=log_file)
                if args.no_retry or args.once:
                    return 1
                log(f"Reconnecting in {args.retry_delay:.1f}s...", log_file=log_file)
                time.sleep(args.retry_delay)
    finally:
        if log_file is not None:
            log_file.close()


if __name__ == "__main__":
    raise SystemExit(main())
