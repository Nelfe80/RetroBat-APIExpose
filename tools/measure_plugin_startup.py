#!/usr/bin/env python3
r"""Measure plugin startup latency.

Examples:
  py tools\measure_plugin_startup.py api --exe E:\RetroBat\plugins\APIExpose\RetroBat.Api.exe --health http://127.0.0.1:12345/api/v1/health --kill
  py tools\measure_plugin_startup.py log --exe E:\RetroBat\plugins\MarqueeManager\MarqueeManager.exe --log E:\RetroBat\plugins\MarqueeManager\logs\debug.log --ready-text "Service running" --kill
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def kill_processes(names: list[str]) -> None:
    for name in names:
        subprocess.run(
            ["powershell", "-NoProfile", "-Command", f"Get-Process -Name '{name}' -ErrorAction SilentlyContinue | Stop-Process -Force"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )


def wait_health(url: str, timeout_s: float, interval_s: float) -> tuple[bool, str]:
    deadline = time.monotonic() + timeout_s
    last_error = ""
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=min(2.0, interval_s)) as response:
                body = response.read(4096).decode("utf-8", errors="replace")
                if 200 <= response.status < 300:
                    return True, body
                last_error = f"HTTP {response.status}: {body[:240]}"
        except Exception as exc:  # noqa: BLE001 - diagnostic tool
            last_error = str(exc)
        time.sleep(interval_s)
    return False, last_error


def wait_log(log_path: Path, ready_text: str, start_pos: int, timeout_s: float, interval_s: float) -> tuple[bool, str]:
    deadline = time.monotonic() + timeout_s
    last_text = ""
    while time.monotonic() < deadline:
        if log_path.exists():
            with log_path.open("r", encoding="utf-8", errors="replace") as handle:
                handle.seek(start_pos)
                text = handle.read()
            if text:
                last_text = text[-1000:]
            if ready_text.lower() in text.lower():
                return True, ready_text
        time.sleep(interval_s)
    return False, last_text


def append_result(path: Path, result: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(result, ensure_ascii=False, sort_keys=True) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Measure plugin startup latency.")
    sub = parser.add_subparsers(dest="mode", required=True)

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--exe", required=True, help="Executable to start.")
    common.add_argument("--workdir", default="", help="Working directory. Defaults to executable directory.")
    common.add_argument("--timeout-s", type=float, default=90.0)
    common.add_argument("--interval-s", type=float, default=0.25)
    common.add_argument("--kill", action="store_true", help="Kill process by executable stem before starting.")
    common.add_argument("--out", default="APIExpose/state/perf/plugin_startup_latency.jsonl")

    api = sub.add_parser("api", parents=[common], help="Wait for an HTTP health endpoint.")
    api.add_argument("--health", required=True)

    log = sub.add_parser("log", parents=[common], help="Wait for a log line.")
    log.add_argument("--log", required=True)
    log.add_argument("--ready-text", required=True)

    args = parser.parse_args()
    exe = Path(args.exe)
    workdir = Path(args.workdir) if args.workdir else exe.parent
    process_name = exe.stem

    if args.kill:
        kill_processes([process_name])
        time.sleep(1.0)

    log_start_pos = 0
    if args.mode == "log":
        log_path = Path(args.log)
        if log_path.exists():
            log_start_pos = log_path.stat().st_size

    started_wall = now_iso()
    started = time.monotonic()
    process = subprocess.Popen(
        [str(exe)],
        cwd=str(workdir),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )

    if args.mode == "api":
        ok, detail = wait_health(args.health, args.timeout_s, args.interval_s)
    else:
        ok, detail = wait_log(Path(args.log), args.ready_text, log_start_pos, args.timeout_s, args.interval_s)

    elapsed_ms = int((time.monotonic() - started) * 1000)
    result = {
        "ts": now_iso(),
        "mode": args.mode,
        "exe": str(exe),
        "pid": process.pid,
        "startedAtUtc": started_wall,
        "ready": ok,
        "elapsedMs": elapsed_ms,
        "detail": detail[:1000],
    }
    append_result(Path(args.out), result)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
