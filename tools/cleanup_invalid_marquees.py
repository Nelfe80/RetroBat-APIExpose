#!/usr/bin/env python3
"""
Archive game marquee files that are too small or badly shaped for the configured
MarqueeManager generated profile.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from datetime import datetime
from pathlib import Path


def run_identify(convert: Path, image: Path) -> tuple[int, int] | None:
    try:
        result = subprocess.run(
            [str(convert), str(image), "-format", "%w %h", "info:"],
            check=True,
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (OSError, subprocess.SubprocessError):
        return None

    parts = result.stdout.strip().split()
    if len(parts) < 2:
        return None
    try:
        return int(parts[0]), int(parts[1])
    except ValueError:
        return None


def is_invalid(width: int, height: int, profile_width: int, profile_height: int) -> tuple[bool, str]:
    aspect = width / max(1, height)
    min_width = round(profile_width * 0.50)
    min_height = round(profile_height * 0.50)

    reasons: list[str] = []
    if width < min_width:
        reasons.append(f"width<{min_width}")
    if height < min_height:
        reasons.append(f"height<{min_height}")
    return bool(reasons), ",".join(reasons)


def files_have_same_content(left: Path, right: Path) -> bool:
    try:
        if not left.is_file() or not right.is_file() or left.stat().st_size != right.stat().st_size:
            return False
        with left.open("rb") as left_file, right.open("rb") as right_file:
            while True:
                left_chunk = left_file.read(1024 * 1024)
                right_chunk = right_file.read(1024 * 1024)
                if left_chunk != right_chunk:
                    return False
                if not left_chunk:
                    return True
    except OSError:
        return False


def is_generated_dmd_copy(marquee: Path) -> bool:
    directory = marquee.parent
    for pattern in ("generated-dmd.*", "generated-system-dmd.*"):
        for dmd in directory.glob(pattern):
            if dmd.resolve() != marquee.resolve() and files_have_same_content(marquee, dmd):
                return True
    return False


def iter_marquees(media_root: Path):
    for path in media_root.glob("*/games/*/artwork/marquee/marquee.*"):
        if path.is_file():
            yield path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", default=str(Path(__file__).resolve().parents[1]))
    parser.add_argument("--profile", default="xl-1920x360")
    parser.add_argument("--apply", action="store_true", help="Archive invalid files. Without this, only reports.")
    parser.add_argument("--summary-only", action="store_true", help="Only print the final summary.")
    parser.add_argument("--max-report", type=int, default=0, help="Print at most N invalid entries before the summary.")
    args = parser.parse_args()

    plugin_root = Path(args.plugin_root).resolve()
    convert = plugin_root / "tools" / "imagemagick" / "convert.exe"
    media_root = plugin_root / "media" / "systems"
    if args.profile != "xl-1920x360":
        raise SystemExit(f"Unsupported profile for cleanup: {args.profile}")

    profile_width, profile_height = 1920, 360
    archive_root = plugin_root / ".archive" / ("invalid-marquees-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    report: list[dict[str, object]] = []

    for marquee in iter_marquees(media_root):
        dimensions = run_identify(convert, marquee)
        if dimensions is None:
            continue
        width, height = dimensions
        invalid, reason = is_invalid(width, height, profile_width, profile_height)
        if is_generated_dmd_copy(marquee):
            invalid = True
            reason = ",".join(part for part in (reason, "generated-dmd-copy") if part)
        if not invalid:
            continue

        relative = marquee.relative_to(plugin_root)
        entry = {
            "path": str(relative).replace("\\", "/"),
            "width": width,
            "height": height,
            "reason": reason,
            "bytes": marquee.stat().st_size,
        }
        report.append(entry)

        if args.apply:
            destination = archive_root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(marquee), str(destination))

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
