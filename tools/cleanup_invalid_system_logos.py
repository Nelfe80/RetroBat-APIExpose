#!/usr/bin/env python3
"""
Archive APIExpose-cached system logos that were converted as grayscale.

Only files with a sibling .apiexpose-cache marker are considered generated cache
and are eligible for archival. User-provided media without that marker is left
untouched.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from datetime import datetime
from pathlib import Path


def run_identify(convert: Path, image: Path) -> dict[str, str] | None:
    try:
        result = subprocess.run(
            [
                str(convert),
                str(image),
                "-format",
                "%w\t%h\t%[type]\t%[colorspace]\t%k",
                "info:",
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (OSError, subprocess.SubprocessError):
        return None

    parts = result.stdout.strip().split("\t")
    if len(parts) < 5:
        return None
    return {
        "width": parts[0],
        "height": parts[1],
        "type": parts[2],
        "colorspace": parts[3],
        "colors": parts[4],
    }


def is_invalid_logo(info: dict[str, str]) -> tuple[bool, str]:
    image_type = info.get("type", "").lower()
    colorspace = info.get("colorspace", "").lower()
    reasons: list[str] = []
    if colorspace in {"gray", "grey"}:
        reasons.append("colorspace-gray")
    if "grayscale" in image_type or "bilevel" in image_type:
        reasons.append("type-grayscale")
    return bool(reasons), ",".join(reasons)


def iter_cached_system_logos(media_root: Path):
    for path in media_root.glob("*/ui/wheels/wheel.*"):
        if not path.is_file():
            continue
        if path.name.endswith(".apiexpose-cache"):
            continue
        marker = path.with_name(path.name + ".apiexpose-cache")
        if marker.is_file():
            yield path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", default=str(Path(__file__).resolve().parents[1]))
    parser.add_argument("--apply", action="store_true", help="Archive invalid cached logos. Without this, only reports.")
    parser.add_argument("--summary-only", action="store_true", help="Only print the final summary.")
    parser.add_argument("--max-report", type=int, default=0, help="Print at most N invalid entries before the summary.")
    args = parser.parse_args()

    plugin_root = Path(args.plugin_root).resolve()
    convert = plugin_root / "tools" / "imagemagick" / "convert.exe"
    media_root = plugin_root / "media" / "systems"
    archive_root = plugin_root / ".archive" / ("invalid-system-logos-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    report: list[dict[str, object]] = []

    for logo in iter_cached_system_logos(media_root):
        info = run_identify(convert, logo)
        if info is None:
            continue
        invalid, reason = is_invalid_logo(info)
        if not invalid:
            continue

        relative = logo.relative_to(plugin_root)
        marker = logo.with_name(logo.name + ".apiexpose-cache")
        entry = {
            "path": str(relative).replace("\\", "/"),
            "width": int(info["width"]),
            "height": int(info["height"]),
            "type": info["type"],
            "colorspace": info["colorspace"],
            "colors": int(info["colors"]),
            "reason": reason,
            "bytes": logo.stat().st_size,
        }
        report.append(entry)

        if args.apply:
            destination = archive_root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(logo), str(destination))
            if marker.is_file():
                marker_destination = archive_root / marker.relative_to(plugin_root)
                marker_destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(marker), str(marker_destination))

    if not args.summary_only:
        for entry in report[: args.max_report or None]:
            print(json.dumps(entry, ensure_ascii=False))

    print(json.dumps({
        "invalidCount": len(report),
        "applied": args.apply,
        "archiveRoot": str(archive_root if args.apply else ""),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
