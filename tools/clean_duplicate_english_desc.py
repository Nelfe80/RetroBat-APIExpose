#!/usr/bin/env python3
"""
Remove localized descriptions that are exact duplicates of the English desc.

This is a one-shot cleanup helper for stale metadata bundles such as:
  media/systems/<system>/games/<slug>/texts/metadata-fr.json

It only removes a non-English "desc" when the same game also has
metadata-en.json and both descriptions match after light normalization.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import html
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


LANG_FILE_RE = re.compile(r"^metadata-([a-z]{2})(?:[_-][a-z]{2})?\.json$", re.IGNORECASE)
WHITESPACE_RE = re.compile(r"\s+")
CANONICAL_MEDIA_RE = re.compile(
    r"(?:^|[\\/])media[\\/]systems[\\/](?P<system>[^\\/]+)[\\/]games[\\/](?P<slug>[^\\/]+)[\\/]",
    re.IGNORECASE,
)


def plugin_root() -> Path:
    return Path(__file__).resolve().parents[1]


def normalize_text(value: Any) -> str:
    if not isinstance(value, str):
        return ""

    text = value
    for _ in range(2):
        decoded = html.unescape(text)
        if decoded == text:
            break
        text = decoded

    return WHITESPACE_RE.sub(" ", text.replace("\r\n", "\n").replace("\r", "\n")).strip()


def load_json(path: Path) -> dict[str, Any] | None:
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            data = json.load(stream)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"[skip] invalid json: {path} ({exc})")
        return None

    return data if isinstance(data, dict) else None


def write_json(path: Path, data: dict[str, Any]) -> None:
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    with tmp_path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    tmp_path.replace(path)


def write_xml(path: Path, tree: ET.ElementTree) -> None:
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    try:
        ET.indent(tree, space="\t")
    except AttributeError:
        pass

    tree.write(tmp_path, encoding="utf-8", xml_declaration=True)
    tmp_path.replace(path)


def get_desc(bundle: dict[str, Any]) -> str:
    fields = bundle.get("Fields")
    if not isinstance(fields, dict):
        return ""
    return normalize_text(fields.get("desc"))


def remove_desc(bundle: dict[str, Any]) -> bool:
    fields = bundle.get("Fields")
    if not isinstance(fields, dict) or "desc" not in fields:
        return False

    del fields["desc"]
    bundle["UpdatedAtUtc"] = _dt.datetime.now(tz=_dt.timezone.utc).isoformat().replace("+00:00", "Z")
    return True


def iter_text_roots(root: Path) -> list[Path]:
    roots = [
        root / "media" / "systems",
        root / "media" / "user" / "systems",
    ]

    result: list[Path] = []
    for base in roots:
        if not base.exists():
            continue
        result.extend(path for path in base.glob("*/games/*/texts") if path.is_dir())
    return result


def iter_gamelist_paths(root: Path) -> list[tuple[Path, str, str, bool]]:
    """Return gamelist path, language, frontend system, live flag."""

    result: list[tuple[Path, str, str, bool]] = []
    localized_root = root / "resources" / "gamelist" / "localized"
    if localized_root.exists():
        for gamelist_path in localized_root.glob("*/*/gamelist.xml"):
            try:
                language = gamelist_path.relative_to(localized_root).parts[0].lower()
                system = gamelist_path.relative_to(localized_root).parts[1]
            except IndexError:
                continue

            if language != "en":
                result.append((gamelist_path, language, system, False))

    roms_root = root.parent.parent / "roms"
    if roms_root.exists():
        for gamelist_path in roms_root.glob("*/gamelist.xml"):
            system = gamelist_path.parent.name
            result.append((gamelist_path, "", system, True))

    return result


def detect_language_from_name(path: Path) -> str:
    match = LANG_FILE_RE.match(path.name)
    return match.group(1).lower() if match else ""


def normalize_language(value: Any) -> str:
    if not isinstance(value, str):
        return ""

    normalized = value.strip().lower().replace("-", "_")
    return normalized[:2] if len(normalized) >= 2 else ""


def normalize_slug(value: str) -> str:
    cleaned = Path(value.replace("\\", "/")).stem.lower()
    cleaned = re.sub(r"[^a-z0-9]+", "_", cleaned)
    return cleaned.strip("_")


def build_english_metadata_index(root: Path) -> dict[tuple[str, str], str]:
    index: dict[tuple[str, str], str] = {}
    for text_root in iter_text_roots(root):
        english_path = text_root / "metadata-en.json"
        if not english_path.exists():
            continue

        bundle = load_json(english_path)
        if bundle is None:
            continue

        desc = get_desc(bundle)
        if not desc:
            continue

        parts = text_root.parts
        try:
            games_index = len(parts) - 3
            system = parts[games_index - 1].lower()
            slug = parts[games_index + 1].lower()
        except IndexError:
            continue

        index[(system, slug)] = desc

    return index


def build_english_gamelist_index(root: Path) -> dict[tuple[str, str], str]:
    index: dict[tuple[str, str], str] = {}
    english_root = root / "resources" / "gamelist" / "localized" / "en"
    if not english_root.exists():
        return index

    for gamelist_path in english_root.glob("*/gamelist.xml"):
        system = gamelist_path.parent.name.lower()
        try:
            tree = ET.parse(gamelist_path)
        except (OSError, ET.ParseError) as exc:
            print(f"[skip] invalid xml: {gamelist_path} ({exc})")
            continue

        for game in tree.getroot().findall("game"):
            path_text = normalize_text(game.findtext("path"))
            desc = normalize_text(game.findtext("desc"))
            if path_text and desc:
                index[(system, normalize_path_key(path_text))] = desc

    return index


def normalize_path_key(value: str) -> str:
    return value.replace("\\", "/").strip().lower()


def find_canonical_metadata_desc(
    english_metadata: dict[tuple[str, str], str],
    game: ET.Element,
    fallback_system: str,
) -> str:
    for element in game:
        text = normalize_text(element.text)
        if not text:
            continue

        match = CANONICAL_MEDIA_RE.search(text.replace("\\", "/"))
        if not match:
            continue

        key = (match.group("system").lower(), match.group("slug").lower())
        desc = english_metadata.get(key)
        if desc:
            return desc

    path_text = normalize_text(game.findtext("path"))
    if path_text:
        desc = english_metadata.get((fallback_system.lower(), normalize_slug(path_text)))
        if desc:
            return desc

    return ""


def should_clean_live_entry(game: ET.Element) -> bool:
    language = normalize_language(game.findtext("lang"))
    return bool(language and language != "en")


def clean_gamelist_descs(
    root: Path,
    english_metadata: dict[tuple[str, str], str],
    english_gamelists: dict[tuple[str, str], str],
    apply: bool,
) -> dict[str, Any]:
    scanned_gamelists = 0
    scanned_games = 0
    cleaned_entries = 0
    cleaned_files: list[str] = []

    for gamelist_path, language, system, is_live in iter_gamelist_paths(root):
        try:
            tree = ET.parse(gamelist_path)
        except (OSError, ET.ParseError) as exc:
            print(f"[skip] invalid xml: {gamelist_path} ({exc})")
            continue

        scanned_gamelists += 1
        changed = False
        file_cleaned_entries = 0
        normalized_system = system.lower()
        for game in tree.getroot().findall("game"):
            if is_live and not should_clean_live_entry(game):
                continue

            desc_element = game.find("desc")
            desc = normalize_text(desc_element.text if desc_element is not None else "")
            if not desc:
                continue

            scanned_games += 1
            english_desc = find_canonical_metadata_desc(english_metadata, game, normalized_system)
            if not english_desc:
                path_text = normalize_text(game.findtext("path"))
                if path_text:
                    english_desc = english_gamelists.get((normalized_system, normalize_path_key(path_text)), "")

            if not english_desc or desc != english_desc:
                continue

            if desc_element is not None:
                game.remove(desc_element)
                changed = True
                file_cleaned_entries += 1
                cleaned_entries += 1

        if changed:
            try:
                relative = str(gamelist_path.relative_to(root))
            except ValueError:
                relative = str(gamelist_path)
            cleaned_files.append(relative)
            if apply:
                write_xml(gamelist_path, tree)

    return {
        "scannedGamelists": scanned_gamelists,
        "scannedGamelistEntriesWithDesc": scanned_games,
        "cleanedGamelistFiles": cleaned_files,
        "cleanedGamelistFilesCount": len(cleaned_files),
        "cleanedGamelistEntries": cleaned_entries,
    }


def clean(root: Path, apply: bool, clean_metadata: bool, clean_gamelists: bool) -> dict[str, Any]:
    scanned_games = 0
    scanned_bundles = 0
    cleaned_files: list[str] = []

    if clean_metadata:
        for text_root in iter_text_roots(root):
            english_path = text_root / "metadata-en.json"
            if not english_path.exists():
                continue

            english_bundle = load_json(english_path)
            if english_bundle is None:
                continue

            english_desc = get_desc(english_bundle)
            if not english_desc:
                continue

            scanned_games += 1
            for bundle_path in sorted(text_root.glob("metadata-*.json")):
                language = detect_language_from_name(bundle_path)
                if not language or language == "en":
                    continue

                scanned_bundles += 1
                bundle = load_json(bundle_path)
                if bundle is None:
                    continue

                localized_desc = get_desc(bundle)
                if not localized_desc or localized_desc != english_desc:
                    continue

                cleaned_files.append(str(bundle_path.relative_to(root)))
                if apply and remove_desc(bundle):
                    write_json(bundle_path, bundle)

    gamelist_summary = {
        "scannedGamelists": 0,
        "scannedGamelistEntriesWithDesc": 0,
        "cleanedGamelistFiles": [],
        "cleanedGamelistFilesCount": 0,
        "cleanedGamelistEntries": 0,
    }
    if clean_gamelists:
        gamelist_summary = clean_gamelist_descs(
            root,
            build_english_metadata_index(root),
            build_english_gamelist_index(root),
            apply,
        )

    return {
        "mode": "apply" if apply else "dry-run",
        "scannedGamesWithEnglishDesc": scanned_games,
        "scannedLocalizedBundles": scanned_bundles,
        "cleanedFiles": cleaned_files,
        "cleanedCount": len(cleaned_files),
        **gamelist_summary,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Remove non-English desc fields duplicated from metadata-en.json.")
    parser.add_argument("--root", default=str(plugin_root()), help="APIExpose plugin root.")
    parser.add_argument("--apply", action="store_true", help="Write changes. Without this flag, only reports matches.")
    parser.add_argument("--metadata-only", action="store_true", help="Clean metadata bundles only.")
    parser.add_argument("--gamelists-only", action="store_true", help="Clean gamelist.xml files only.")
    parser.add_argument("--json", action="store_true", help="Print JSON summary only.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    clean_metadata = not args.gamelists_only
    clean_gamelists = not args.metadata_only
    summary = clean(root, apply=args.apply, clean_metadata=clean_metadata, clean_gamelists=clean_gamelists)

    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0

    print(f"Mode: {summary['mode']}")
    print(f"Games with English desc scanned: {summary['scannedGamesWithEnglishDesc']}")
    print(f"Localized bundles scanned: {summary['scannedLocalizedBundles']}")
    print(f"Duplicated desc removed: {summary['cleanedCount']}")
    for path in summary["cleanedFiles"]:
        print(f" - {path}")
    print(f"Gamelists scanned: {summary['scannedGamelists']}")
    print(f"Gamelist entries with desc scanned: {summary['scannedGamelistEntriesWithDesc']}")
    print(f"Gamelist desc removed: {summary['cleanedGamelistEntries']}")
    for path in summary["cleanedGamelistFiles"]:
        print(f" - {path}")

    if not args.apply:
        print("Dry-run only. Re-run with --apply to modify files.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
