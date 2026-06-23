#!/usr/bin/env python3
"""
Measure APIExpose latency when navigation is triggered through the ES controller API.

The script calls POST /api/v1/es/controller/tap and listens to APIExpose
WebSocket streams until they become quiet. This is useful for measuring the
"5 fast joystick moves, last state wins" scenario without writing events.ini
directly.
"""

from __future__ import annotations

import argparse
import json
import queue
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib import request
from urllib.error import HTTPError, URLError

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from listen_api_ws import build_stream_url, connect_websocket, read_frame, send_close  # noqa: E402


DEFAULT_STREAMS = ("frontend", "panel", "marquee", "topper", "instruction-card")


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def ws_worker(
    stream: str,
    base_url: str,
    out: "queue.Queue[dict[str, Any]]",
    stop: threading.Event,
    connect_timeout: float,
    read_timeout: float,
) -> None:
    url = build_stream_url(base_url, stream)
    while not stop.is_set():
        sock = None
        try:
            sock = connect_websocket(url, connect_timeout, read_timeout)
            parts: list[bytes] = []
            out.put({"kind": "listener", "stream": stream, "status": "connected", "perf": time.perf_counter()})
            while not stop.is_set():
                try:
                    opcode, payload, fin = read_frame(sock)
                except TimeoutError:
                    continue
                if opcode == 0x8:
                    break
                if opcode == 0x9:
                    sock.sendall(b"\x8A" + bytes([len(payload)]) + payload)
                    continue
                if opcode not in (0x1, 0x0):
                    continue
                parts.append(payload)
                if not fin:
                    continue
                text = b"".join(parts).decode("utf-8", errors="replace")
                parts.clear()
                try:
                    parsed = json.loads(text)
                except json.JSONDecodeError:
                    parsed = None
                out.put({
                    "kind": "message",
                    "stream": stream,
                    "type": read_type(parsed),
                    "ts": now_iso(),
                    "perf": time.perf_counter(),
                    "json": parsed,
                })
        except Exception as exc:
            out.put({"kind": "listener", "stream": stream, "status": "error", "error": str(exc), "perf": time.perf_counter()})
            time.sleep(0.5)
        finally:
            if sock is not None:
                send_close(sock)
                try:
                    sock.close()
                except OSError:
                    pass


def read_type(payload: Any) -> str:
    if isinstance(payload, dict):
        return str(payload.get("type") or payload.get("Type") or "")
    return ""


def is_raw_selection(message: dict[str, Any]) -> bool:
    return (
        message.get("stream") == "frontend"
        and message.get("type") in ("ui.system.selected.raw", "ui.game.selected.raw")
    )


def describe_selection(payload: Any) -> str:
    if not isinstance(payload, dict):
        return ""
    event_payload = payload.get("payload", payload.get("Payload", payload))
    if not isinstance(event_payload, dict):
        return ""

    values: list[str] = []
    for key in (
        "SystemId",
        "systemId",
        "FrontendSystem",
        "frontendSystem",
        "GamePath",
        "gamePath",
        "GameName",
        "gameName",
        "Name",
        "name",
    ):
        value = event_payload.get(key)
        if value not in (None, ""):
            values.append(str(value))

    selection = event_payload.get("Selection", event_payload.get("selection"))
    if isinstance(selection, dict):
        for key in (
            "SystemId",
            "systemId",
            "FrontendSystem",
            "frontendSystem",
            "GamePath",
            "gamePath",
            "GameName",
            "gameName",
        ):
            value = selection.get(key)
            if value not in (None, ""):
                values.append(str(value))
    return " | ".join(dict.fromkeys(values))


def read_payload_value(payload: Any, *names: str) -> Any:
    if not isinstance(payload, dict):
        return None
    event_payload = payload.get("payload", payload.get("Payload", payload))
    if not isinstance(event_payload, dict):
        return None
    for name in names:
        if name in event_payload:
            return event_payload[name]
    return None


def drain(messages: "queue.Queue[dict[str, Any]]") -> None:
    while True:
        try:
            messages.get_nowait()
        except queue.Empty:
            return


def post_json(url: str, payload: dict[str, Any], timeout: float) -> tuple[int, str]:
    data = json.dumps(payload).encode("utf-8")
    req = request.Request(
        url,
        data=data,
        method="POST",
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with request.urlopen(req, timeout=timeout) as response:
            body = response.read().decode("utf-8", errors="replace")
            return response.status, body
    except HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        return exc.code, body
    except URLError as exc:
        raise RuntimeError(f"HTTP request failed: {exc}") from exc


def main() -> int:
    parser = argparse.ArgumentParser(description="Measure ES controller tap latency through APIExpose.")
    parser.add_argument("--api-base", default="http://127.0.0.1:12345")
    parser.add_argument("--ws-base", default="ws://127.0.0.1:12345/ws")
    parser.add_argument("--events-ini", default=str(SCRIPT_DIR.parent / "events.ini"))
    parser.add_argument("--streams", default=",".join(DEFAULT_STREAMS))
    parser.add_argument("--input", default="down")
    parser.add_argument("--count", type=int, default=5)
    parser.add_argument(
        "--progressive",
        action="store_true",
        help="Send one input, wait for its raw ES selection event, then send the next input.",
    )
    parser.add_argument(
        "--step-timeout-ms",
        type=int,
        default=4000,
        help="Maximum wait for each progressive raw selection event.",
    )
    parser.add_argument("--hold-ms", type=int, default=45)
    parser.add_argument("--gap-ms", type=int, default=45)
    parser.add_argument("--iterations", type=int, default=1)
    parser.add_argument("--quiet-ms", type=int, default=650)
    parser.add_argument("--timeout-ms", type=int, default=5000)
    parser.add_argument("--connect-timeout", type=float, default=5.0)
    parser.add_argument("--read-timeout", type=float, default=0.2)
    parser.add_argument("--out", default=str(SCRIPT_DIR.parent / "state" / "perf" / "es_controller_latency.jsonl"))
    args = parser.parse_args()

    streams = [s.strip() for s in args.streams.split(",") if s.strip()]
    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    events_ini = Path(args.events_ini).resolve()

    messages: "queue.Queue[dict[str, Any]]" = queue.Queue()
    stop = threading.Event()
    threads = [
        threading.Thread(
            target=ws_worker,
            args=(stream, args.ws_base, messages, stop, args.connect_timeout, args.read_timeout),
            daemon=True,
        )
        for stream in streams
    ]
    for thread in threads:
        thread.start()

    print(f"Endpoint: {args.api_base.rstrip('/')}/api/v1/es/controller/tap")
    print(f"Streams: {', '.join(streams)}")
    print(f"Results: {out_path}")
    time.sleep(1.0)
    drain(messages)

    endpoint = args.api_base.rstrip("/") + "/api/v1/es/controller/tap"
    payload = {
        "input": args.input,
        "count": 1 if args.progressive else max(1, args.count),
        "holdMs": max(1, args.hold_ms),
        "gapMs": 0 if args.progressive else max(0, args.gap_ms),
    }

    try:
        with out_path.open("a", encoding="utf-8") as out_file:
            run_id = datetime.now().strftime("%Y%m%d-%H%M%S")
            for iteration in range(1, max(1, args.iterations) + 1):
                drain(messages)
                started = time.perf_counter()
                started_at = now_iso()
                seen: list[dict[str, Any]] = []
                progressive_steps: list[dict[str, Any]] = []
                status = 0
                body = ""
                response_ms = 0
                response_perf = started

                if args.progressive:
                    for step in range(1, max(1, args.count) + 1):
                        step_started = time.perf_counter()
                        step_started_wall_ns = time.time_ns()
                        baseline_mtime_ns = events_ini.stat().st_mtime_ns if events_ini.exists() else 0
                        status, body = post_json(
                            endpoint,
                            payload,
                            timeout=max(1.0, args.step_timeout_ms / 1000),
                        )
                        response_perf = time.perf_counter()
                        step_http_ms = int(round((response_perf - step_started) * 1000))
                        response_ms += step_http_ms
                        raw_message: dict[str, Any] | None = None
                        events_write_latency_ms: int | None = None
                        events_write_time: str | None = None
                        deadline = step_started + (args.step_timeout_ms / 1000)
                        while time.perf_counter() < deadline:
                            if events_write_latency_ms is None and events_ini.exists():
                                current_mtime_ns = events_ini.stat().st_mtime_ns
                                if current_mtime_ns != baseline_mtime_ns:
                                    events_write_latency_ms = int(round(
                                        (current_mtime_ns - step_started_wall_ns) / 1_000_000
                                    ))
                                    events_write_time = datetime.fromtimestamp(
                                        current_mtime_ns / 1_000_000_000,
                                        timezone.utc,
                                    ).isoformat(timespec="milliseconds")
                            try:
                                message = messages.get(timeout=0.05)
                            except queue.Empty:
                                continue
                            if message.get("kind") != "message":
                                continue
                            seen.append(message)
                            if is_raw_selection(message):
                                raw_message = message
                                break

                        raw_ms = (
                            int(round((float(raw_message["perf"]) - step_started) * 1000))
                            if raw_message is not None
                            else None
                        )
                        selection = describe_selection(raw_message.get("json")) if raw_message else ""
                        received_at_utc = (
                            read_payload_value(raw_message.get("json"), "ReceivedAtUtc", "receivedAtUtc")
                            if raw_message
                            else None
                        )
                        source_latency = (
                            read_payload_value(raw_message.get("json"), "Latency", "latency")
                            if raw_message
                            else None
                        )
                        progressive_steps.append({
                            "step": step,
                            "http_status": status,
                            "http_latency_ms": step_http_ms,
                            "events_ini_write_latency_ms": events_write_latency_ms,
                            "events_ini_write_time_utc": events_write_time,
                            "raw_selection_latency_ms": raw_ms,
                            "raw_received_at_utc": received_at_utc,
                            "raw_source_latency": source_latency,
                            "selection": selection,
                        })
                        print(
                            f"[{iteration}.{step}] {args.input} HTTP={step_http_ms}ms "
                            f"file={events_write_latency_ms if events_write_latency_ms is not None else 'timeout'}ms "
                            f"raw={raw_ms if raw_ms is not None else 'timeout'}ms "
                            f"selection={selection or '(unknown)'}"
                        )
                        if raw_message is None or status < 200 or status >= 300:
                            break
                else:
                    status, body = post_json(endpoint, payload, timeout=max(1.0, args.timeout_ms / 1000))
                    response_perf = time.perf_counter()
                    response_ms = int(round((response_perf - started) * 1000))
                    print(f"[{iteration}] HTTP {status} response in {response_ms}ms")

                last_message_perf: float | None = None
                deadline = started + (args.timeout_ms / 1000)
                quiet_deadline: float | None = None
                while time.perf_counter() < deadline:
                    if quiet_deadline is not None and time.perf_counter() >= quiet_deadline:
                        break
                    try:
                        message = messages.get(timeout=0.05)
                    except queue.Empty:
                        continue
                    if message.get("kind") != "message":
                        continue
                    seen.append(message)
                    last_message_perf = float(message["perf"])
                    quiet_deadline = last_message_perf + (args.quiet_ms / 1000)

                last_latency_ms = int(round(((last_message_perf or response_perf) - started) * 1000))
                by_stream: dict[str, dict[str, Any]] = {}
                for message in seen:
                    by_stream[str(message["stream"])] = {
                        "type": message.get("type") or "(raw)",
                        "latency_ms": int(round((float(message["perf"]) - started) * 1000)),
                        "received_at": message["ts"],
                    }

                row = {
                    "kind": "controller-tap",
                    "run_id": run_id,
                    "iteration": iteration,
                    "started_at": started_at,
                    "payload": payload,
                    "progressive": args.progressive,
                    "progressive_steps": progressive_steps,
                    "http_status": status,
                    "http_latency_ms": response_ms,
                    "last_ws_latency_ms": last_latency_ms,
                    "message_count": len(seen),
                    "last_by_stream": by_stream,
                    "response_body": body[:1000],
                }
                print(json.dumps(row, ensure_ascii=False), file=out_file, flush=True)
                print(f"  WS messages={len(seen)} last_ws={last_latency_ms}ms")
                for stream, item in sorted(by_stream.items()):
                    print(f"  {stream:<16} {item['type']:<32} {item['latency_ms']:>4}ms")
    finally:
        stop.set()
        for thread in threads:
            thread.join(timeout=0.5)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
