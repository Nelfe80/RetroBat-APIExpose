import argparse
import socket
import sys
import time
from datetime import datetime


def timestamp() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def main() -> int:
    parser = argparse.ArgumentParser(description="Connect to MAME output network TCP stream.")
    parser.add_argument("--host", default="127.0.0.1", help="MAME host/IP. Default: 127.0.0.1")
    parser.add_argument("--port", type=int, default=8000, help="MAME TCP port. Default: 8000")
    parser.add_argument(
        "--retry-delay",
        type=float,
        default=1.0,
        help="Delay in seconds before reconnecting. Default: 1.0",
    )
    args = parser.parse_args()

    while True:
        try:
            print(f"[{timestamp()}] Connecting to {args.host}:{args.port}...", flush=True)
            with socket.create_connection((args.host, args.port), timeout=5) as conn:
                print(f"[{timestamp()}] Connected.", flush=True)
                file_obj = conn.makefile("r", encoding="utf-8", errors="replace")
                for line in file_obj:
                    line = line.rstrip("\r\n")
                    if line:
                        print(f"[{timestamp()}] {line}", flush=True)
                print(f"[{timestamp()}] Connection closed by remote host.", flush=True)
        except KeyboardInterrupt:
            print(f"\n[{timestamp()}] Stopped.", flush=True)
            return 0
        except OSError as exc:
            print(f"[{timestamp()}] Connection error: {exc}", file=sys.stderr, flush=True)

        try:
            time.sleep(args.retry_delay)
        except KeyboardInterrupt:
            print(f"\n[{timestamp()}] Stopped.", flush=True)
            return 0


if __name__ == "__main__":
    raise SystemExit(main())
