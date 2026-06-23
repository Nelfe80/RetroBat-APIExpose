#!/usr/bin/env python3
"""
Measure APIExpose navigation latency from events.ini to WebSocket streams.

This tool is intentionally dependency-free. It writes synthetic
system-selected/game-selected events to APIExpose/events.ini, listens to the
specialized WebSocket streams, and records the first messages observed after
each write.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import re
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from listen_api_ws import (  # noqa: E402
    build_stream_url,
    connect_websocket,
    read_frame,
    send_close,
)


DEFAULT_STREAMS = ("frontend", "panel", "marquee", "topper", "instruction-card")
DEFAULT_SYSTEMS = ("megadrive", "gamegear", "snes", "arcade", "mame", "megadrive")


@dataclass(frozen=True)
class TestCase:
    kind: str
    label: str
    content: str
    expected_system: str = ""
    expected_game: str = ""


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def perf_ms(start: float) -> int:
    return int(round((time.perf_counter() - start) * 1000))


def write_events_ini(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.{os.getpid()}.{time.time_ns()}.tmp")
    tmp.write_text(content, encoding="utf-8")
    try:
        # Replace is atomic on the same volume. Retry because APIExpose may read
        # the file at the exact same moment.
        for attempt in range(1, 9):
            try:
                os.replace(tmp, path)
                return
            except OSError:
                if attempt == 8:
                    raise
                time.sleep(0.005 * attempt)
    finally:
        try:
            tmp.unlink()
        except FileNotFoundError:
            pass


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
            out.put({
                "kind": "listener",
                "stream": stream,
                "status": "connected",
                "ts": now_iso(),
                "perf": time.perf_counter(),
            })
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
                    "ts": now_iso(),
                    "perf": time.perf_counter(),
                    "text": text,
                    "json": parsed,
                })
        except Exception as exc:
            out.put({
                "kind": "listener",
                "stream": stream,
                "status": "error",
                "error": str(exc),
                "ts": now_iso(),
                "perf": time.perf_counter(),
            })
            time.sleep(0.5)
        finally:
            if sock is not None:
                send_close(sock)
                try:
                    sock.close()
                except OSError:
                    pass


def read_type(message: dict[str, Any]) -> str:
    payload = message.get("json")
    if isinstance(payload, dict):
        return str(payload.get("type") or payload.get("Type") or "")
    return ""


def read_payload(message: dict[str, Any]) -> Any:
    payload = message.get("json")
    if isinstance(payload, dict):
        return payload.get("payload", payload.get("Payload", payload))
    return None


def walk_values(value: Any) -> list[str]:
    found: list[str] = []
    if value is None:
        return found
    if isinstance(value, (str, int, float, bool)):
        found.append(str(value))
    elif isinstance(value, dict):
        for child in value.values():
            found.extend(walk_values(child))
    elif isinstance(value, list):
        for child in value:
            found.extend(walk_values(child))
    return found


def message_matches(case: TestCase, message: dict[str, Any]) -> bool:
    haystack = "\n".join(walk_values(read_payload(message))).lower()
    if case.expected_system and case.expected_system.lower() not in haystack:
        return False
    if case.expected_game:
        expected = case.expected_game.lower()
        if expected not in haystack and normalize_match_text(expected) not in normalize_match_text(haystack):
            return False
    return True


def normalize_match_text(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.lower())


def drain_messages(messages: "queue.Queue[dict[str, Any]]") -> None:
    while True:
        try:
            messages.get_nowait()
        except queue.Empty:
            return


def build_cases(args: argparse.Namespace) -> list[TestCase]:
    if args.case_file:
        cases: list[TestCase] = []
        data = json.loads(Path(args.case_file).read_text(encoding="utf-8"))
        for index, item in enumerate(data):
            kind = str(item.get("kind", "system")).strip().lower()
            label = str(item.get("label") or f"{kind}-{index + 1}")
            if kind == "game":
                system = str(item["system"])
                rom = str(item["rom"])
                name = str(item.get("name") or Path(rom).stem)
                cases.append(TestCase(
                    "game",
                    label,
                    f'event=game-selected\n{system} {rom} "{name}"\n',
                    expected_system=system,
                    expected_game=str(item.get("match") or name or Path(rom).stem),
                ))
            else:
                system = str(item["system"])
                cases.append(TestCase(
                    "system",
                    label,
                    f"event=system-selected\n{system}\n",
                    expected_system=system,
                ))
        return cases

    systems = [s.strip() for s in (args.systems or ",".join(DEFAULT_SYSTEMS)).split(",") if s.strip()]
    return [
        TestCase(
            "system",
            f"system-{system}-{index + 1}",
            f"event=system-selected\n{system}\n",
            expected_system=system,
        )
        for index, system in enumerate(systems)
    ]


def summarize(results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    buckets: dict[tuple[str, str], list[int]] = {}
    for row in results:
        if row.get("kind") != "ws-hit":
            continue
        key = (str(row.get("stream")), str(row.get("type")))
        buckets.setdefault(key, []).append(int(row.get("latency_ms", 0)))

    summary = []
    for (stream, event_type), values in sorted(buckets.items()):
        values.sort()
        count = len(values)
        p50 = values[count // 2]
        p95 = values[min(count - 1, int(round(count * 0.95)) - 1)]
        summary.append({
            "stream": stream,
            "type": event_type,
            "count": count,
            "min_ms": values[0],
            "p50_ms": p50,
            "p95_ms": p95,
            "max_ms": values[-1],
        })
    return summary


def run_burst(
    args: argparse.Namespace,
    cases: list[TestCase],
    messages: "queue.Queue[dict[str, Any]]",
    out_file: Any,
) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    run_id = datetime.now().strftime("%Y%m%d-%H%M%S")
    events_ini = Path(args.events_ini).resolve()
    burst_cases = cases[: max(1, args.burst_size)]
    if not burst_cases:
        return results

    final_case = burst_cases[-1]
    print(
        f"Burst mode: {len(burst_cases)} changes, gap={args.burst_gap_ms}ms, "
        f"measuring final={final_case.label}"
    )

    for iteration in range(1, max(1, args.iterations) + 1):
        drain_messages(messages)
        first_started_perf = time.perf_counter()
        last_started_perf = first_started_perf
        writes: list[dict[str, Any]] = []

        for case_index, case in enumerate(burst_cases, start=1):
            last_started_perf = time.perf_counter()
            started_at = now_iso()
            write_events_ini(events_ini, case.content)
            write_latency = perf_ms(last_started_perf)
            writes.append({
                "case_index": case_index,
                "case": case.label,
                "expected_system": case.expected_system,
                "expected_game": case.expected_game,
                "started_at": started_at,
                "write_latency_ms": write_latency,
            })
            if case_index < len(burst_cases) and args.burst_gap_ms > 0:
                time.sleep(args.burst_gap_ms / 1000)

        header = {
            "kind": "burst-write",
            "run_id": run_id,
            "iteration": iteration,
            "burst_size": len(burst_cases),
            "burst_gap_ms": args.burst_gap_ms,
            "first_case": burst_cases[0].label,
            "final_case": final_case.label,
            "final_expected_system": final_case.expected_system,
            "final_expected_game": final_case.expected_game,
            "duration_first_to_last_write_ms": int(round((last_started_perf - first_started_perf) * 1000)),
            "writes": writes,
        }
        print(json.dumps(header, ensure_ascii=False), file=out_file, flush=True)
        print(
            f"[burst {iteration}] wrote {len(burst_cases)} changes in "
            f"{header['duration_first_to_last_write_ms']}ms; waiting for {final_case.label}"
        )

        first_final_by_stream_type: set[tuple[str, str]] = set()
        old_or_other = 0
        total_messages = 0
        deadline = last_started_perf + (args.timeout_ms / 1000)
        while time.perf_counter() < deadline:
            try:
                message = messages.get(timeout=0.05)
            except queue.Empty:
                continue

            if message.get("kind") != "message":
                continue

            total_messages += 1
            latency = int(round((float(message["perf"]) - last_started_perf) * 1000))
            event_type = read_type(message) or "(raw)"
            matched = message_matches(final_case, message)
            if not matched:
                old_or_other += 1
                continue

            key = (str(message["stream"]), event_type)
            if key in first_final_by_stream_type:
                continue
            first_final_by_stream_type.add(key)

            row = {
                "kind": "burst-final-hit",
                "run_id": run_id,
                "iteration": iteration,
                "stream": message["stream"],
                "type": event_type,
                "final_case": final_case.label,
                "latency_from_last_write_ms": latency,
                "old_or_other_before_hit": old_or_other,
                "messages_seen": total_messages,
                "received_at": message["ts"],
            }
            results.append(row)
            print(json.dumps(row, ensure_ascii=False), file=out_file, flush=True)
            print(
                f"  final {message['stream']:<16} {event_type:<32} "
                f"{latency:>4}ms old_or_other={old_or_other}"
            )

        if args.settle_ms > 0:
            time.sleep(args.settle_ms / 1000)

    return results


def main() -> int:
    parser = argparse.ArgumentParser(description="Measure events.ini to APIExpose WebSocket latency.")
    parser.add_argument("--events-ini", default=str(SCRIPT_DIR.parent / "events.ini"))
    parser.add_argument("--url", default="ws://127.0.0.1:12345/ws")
    parser.add_argument("--streams", default=",".join(DEFAULT_STREAMS))
    parser.add_argument("--systems", help="Comma-separated system-selected test sequence.")
    parser.add_argument("--case-file", help="JSON array of custom system/game cases.")
    parser.add_argument("--iterations", type=int, default=1)
    parser.add_argument("--settle-ms", type=int, default=220)
    parser.add_argument("--timeout-ms", type=int, default=2500)
    parser.add_argument("--burst", action="store_true", help="Write a fast sequence and measure the final state only.")
    parser.add_argument("--burst-size", type=int, default=5, help="Number of cases to write in burst mode.")
    parser.add_argument("--burst-gap-ms", type=int, default=30, help="Delay between writes in burst mode.")
    parser.add_argument("--connect-timeout", type=float, default=5.0)
    parser.add_argument("--read-timeout", type=float, default=0.2)
    parser.add_argument("--out", default=str(SCRIPT_DIR.parent / "state" / "perf" / "events_latency.jsonl"))
    args = parser.parse_args()

    streams = [s.strip() for s in args.streams.split(",") if s.strip()]
    if not streams:
        print("No streams selected.", file=sys.stderr)
        return 2

    cases = build_cases(args)
    if not cases:
        print("No cases to run.", file=sys.stderr)
        return 2

    events_ini = Path(args.events_ini).resolve()
    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)

    stop = threading.Event()
    messages: "queue.Queue[dict[str, Any]]" = queue.Queue()
    threads = [
        threading.Thread(
            target=ws_worker,
            args=(stream, args.url, messages, stop, args.connect_timeout, args.read_timeout),
            daemon=True,
        )
        for stream in streams
    ]
    for thread in threads:
        thread.start()

    print(f"Events.ini: {events_ini}")
    print(f"Streams: {', '.join(streams)}")
    print(f"Results: {out_path}")
    print("Waiting for WebSocket listeners...")
    time.sleep(1.0)
    drain_messages(messages)

    results: list[dict[str, Any]] = []
    try:
        with out_path.open("a", encoding="utf-8") as out_file:
            if args.burst:
                results.extend(run_burst(args, cases, messages, out_file))
                return 0

            run_id = datetime.now().strftime("%Y%m%d-%H%M%S")
            for iteration in range(1, max(1, args.iterations) + 1):
                for case_index, case in enumerate(cases, start=1):
                    drain_messages(messages)
                    started_perf = time.perf_counter()
                    started_at = now_iso()
                    write_events_ini(events_ini, case.content)
                    write_latency = perf_ms(started_perf)
                    header = {
                        "kind": "events-write",
                        "run_id": run_id,
                        "iteration": iteration,
                        "case_index": case_index,
                        "case": case.label,
                        "event_kind": case.kind,
                        "expected_system": case.expected_system,
                        "expected_game": case.expected_game,
                        "started_at": started_at,
                        "write_latency_ms": write_latency,
                    }
                    print(json.dumps(header, ensure_ascii=False), file=out_file, flush=True)
                    print(f"[{iteration}/{case_index}] {case.label}: events.ini written in {write_latency}ms")

                    first_by_stream_type: set[tuple[str, str]] = set()
                    deadline = started_perf + (args.timeout_ms / 1000)
                    while time.perf_counter() < deadline:
                        try:
                            message = messages.get(timeout=0.05)
                        except queue.Empty:
                            continue

                        if message.get("kind") != "message":
                            continue

                        latency = int(round((float(message["perf"]) - started_perf) * 1000))
                        event_type = read_type(message) or "(raw)"
                        key = (str(message["stream"]), event_type)
                        if key in first_by_stream_type:
                            continue
                        first_by_stream_type.add(key)
                        matched = message_matches(case, message)
                        row = {
                            "kind": "ws-hit",
                            "run_id": run_id,
                            "iteration": iteration,
                            "case_index": case_index,
                            "case": case.label,
                            "stream": message["stream"],
                            "type": event_type,
                            "latency_ms": latency,
                            "matched_expected": matched,
                            "received_at": message["ts"],
                        }
                        results.append(row)
                        print(json.dumps(row, ensure_ascii=False), file=out_file, flush=True)
                        marker = "OK" if matched else "old/other"
                        print(f"  {message['stream']:<16} {event_type:<32} {latency:>4}ms {marker}")

                    if args.settle_ms > 0:
                        time.sleep(args.settle_ms / 1000)

            summary = summarize(results)
            if summary:
                print("\nSummary")
                for row in summary:
                    print(
                        f"{row['stream']:<16} {row['type']:<32} "
                        f"n={row['count']:<3} min={row['min_ms']}ms "
                        f"p50={row['p50_ms']}ms p95={row['p95_ms']}ms max={row['max_ms']}ms"
                    )
                    print(json.dumps({"kind": "summary", **row}, ensure_ascii=False), file=out_file, flush=True)
    finally:
        stop.set()
        for thread in threads:
            thread.join(timeout=0.5)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
