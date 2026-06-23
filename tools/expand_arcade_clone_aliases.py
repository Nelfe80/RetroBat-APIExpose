#!/usr/bin/env python3
"""Expand arcade MEM aliases from RetroBat gamelist metadata.

The MEM files stay universal/logical. This tool only enriches alias.json so
MAME/FBNeo machine ids, names, archive labels, clones, and revisions can reuse
the covered canonical MEM selected from the RetroBat gamelist.
"""

from __future__ import annotations

import argparse
import json
from collections import OrderedDict, defaultdict
from pathlib import Path


def slugify(value: str) -> str:
    out = []
    prev_dash = False
    for char in value.lower():
        if char.isalnum():
            out.append(char)
            prev_dash = False
        elif not prev_dash:
            out.append("-")
            prev_dash = True
    return "".join(out).strip("-")


def entry_values(entry: dict) -> list[str]:
    values: list[str] = []
    for key in ("id", "set", "n", "fn"):
        value = entry.get(key)
        if isinstance(value, str) and value.strip():
            values.append(value.strip())
    for alias in entry.get("aka") or []:
        if not isinstance(alias, dict):
            continue
        for key in ("id", "set", "n", "fn"):
            value = alias.get(key)
            if isinstance(value, str) and value.strip():
                values.append(value.strip())
    return list(dict.fromkeys(values))


def alias_keys_for_entry(entry: dict) -> list[str]:
    keys: list[str] = []
    for value in entry_values(entry):
        keys.append(value)
        keys.append(slugify(value))
    entry_id = entry.get("id")
    full_name = entry.get("fn")
    if isinstance(entry_id, str) and entry_id.strip():
        keys.append(entry_id.strip())
        if isinstance(full_name, str) and full_name.strip():
            keys.append(f"{entry_id.strip()}.zip <{full_name.strip()}>")
            keys.append(f"{entry_id.strip()}.7z <{full_name.strip()}>")
    return [key for key in dict.fromkeys(keys) if key]


def identity_alias_keys_for_entry(entry: dict) -> list[str]:
    keys: list[str] = []
    for key in ("id", "set"):
        value = entry.get(key)
        if isinstance(value, str) and value.strip():
            keys.append(value.strip())
    for alias in entry.get("aka") or []:
        if not isinstance(alias, dict):
            continue
        for key in ("id", "set"):
            value = alias.get(key)
            if isinstance(value, str) and value.strip():
                keys.append(value.strip())
    entry_id = entry.get("id")
    full_name = entry.get("fn")
    if isinstance(entry_id, str) and entry_id.strip() and isinstance(full_name, str) and full_name.strip():
        keys.append(f"{entry_id.strip()}.zip <{full_name.strip()}>")
        keys.append(f"{entry_id.strip()}.7z <{full_name.strip()}>")
    return [key for key in dict.fromkeys(keys) if key]


def load_json_ordered(path: Path) -> OrderedDict[str, str]:
    if not path.exists():
        return OrderedDict()
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle, object_pairs_hook=OrderedDict)
    if not isinstance(data, dict):
        return OrderedDict()
    return OrderedDict((str(key), str(value)) for key, value in data.items())


def iter_jsonl(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(item, dict):
                yield item


def is_excluded_variant(entry: dict, include_hacks: bool, include_bootlegs: bool) -> bool:
    flags = {str(flag).lower() for flag in entry.get("flg") or []}
    kind = str(entry.get("rk") or "").lower()
    item_type = str(entry.get("t") or "game").lower()
    text = " ".join(str(entry.get(key) or "") for key in ("id", "n", "fn", "ed", "edt")).lower()
    if not include_hacks and (item_type == "hack" or kind == "hack" or "hack" in flags):
        return True
    if not include_bootlegs and (kind == "bootleg" or "bootleg" in flags):
        return True
    if not include_hacks:
        suspicious = (
            "enable hidden",
            "hidden character",
            "trainer",
            "cheat",
            "prototype",
            "homebrew",
            "pirate",
            "unlicensed",
            "no protection",
            "supercharger",
        )
        if any(marker in text for marker in suspicious):
            return True
    return False


def is_safe_clone_revision(entry: dict) -> bool:
    if entry.get("reg"):
        return True
    if int(entry.get("revr") or 0) > 0:
        return True
    text = " ".join(str(entry.get(key) or "") for key in ("id", "n", "fn")).lower()
    safe_markers = (
        " rev ",
        " rev.",
        "revision",
        " set ",
        " version ",
        " ver ",
        " ver.",
        "world",
        "europe",
        "euro",
        "usa",
        "japan",
        "asia",
        "korea",
        "taiwan",
        "hong kong",
    )
    return any(marker in f" {text} " for marker in safe_markers)


def resolve_direct_target(entry: dict, aliases: OrderedDict[str, str], mem_slugs: set[str]) -> str:
    candidates = entry_values(entry)
    for key in alias_keys_for_entry(entry):
        target = aliases.get(key)
        if target in mem_slugs:
            return target
        normalized = slugify(key)
        target = aliases.get(normalized)
        if target in mem_slugs:
            return target
    for value in candidates:
        slug = slugify(value)
        if slug in mem_slugs:
            return slug
    return ""


def resolve_identity_target(entry: dict, aliases: OrderedDict[str, str], mem_slugs: set[str]) -> str:
    for key in identity_alias_keys_for_entry(entry):
        target = aliases.get(key)
        if target in mem_slugs:
            return target
        slug = slugify(key)
        if slug in mem_slugs:
            return slug
    return ""


def build_parent_targets(entries: list[dict], aliases: OrderedDict[str, str], mem_slugs: set[str]) -> dict[str, str]:
    parent_targets: dict[str, str] = {}
    for entry in entries:
        target = resolve_direct_target(entry, aliases, mem_slugs)
        if not target:
            continue
        for key in ("id", "set"):
            value = entry.get(key)
            if isinstance(value, str) and value.strip():
                parent_targets[value.strip().lower()] = target
    return parent_targets


def expand_aliases(
    aliases: OrderedDict[str, str],
    entries: list[dict],
    mem_slugs: set[str],
    include_hacks: bool,
    include_bootlegs: bool,
    include_identity_aliases: bool,
) -> tuple[OrderedDict[str, str], dict[str, int], dict[str, list[str]]]:
    additions: dict[str, list[str]] = defaultdict(list)
    stats = {
        "entries": len(entries),
        "added": 0,
        "identity_added": 0,
        "clone_added": 0,
        "kept_existing": 0,
        "conflicts": 0,
        "excluded": 0,
        "without_parent": 0,
    }

    if include_identity_aliases:
        for entry in entries:
            target = resolve_direct_target(entry, aliases, mem_slugs)
            if not target:
                continue
            for key in alias_keys_for_entry(entry):
                existing = aliases.get(key)
                if existing:
                    if existing == target:
                        stats["kept_existing"] += 1
                    else:
                        stats["conflicts"] += 1
                    continue
                aliases[key] = target
                additions[target].append(key)
                stats["added"] += 1
                stats["identity_added"] += 1

    parent_targets = build_parent_targets(entries, aliases, mem_slugs)
    stats["parents_with_mem"] = len(set(parent_targets.values()))

    for entry in entries:
        role = str(entry.get("role") or "parent").lower()
        if role not in {"clone", "variant"}:
            continue
        if is_excluded_variant(entry, include_hacks, include_bootlegs):
            stats["excluded"] += 1
            continue
        if not is_safe_clone_revision(entry):
            stats["excluded"] += 1
            continue
        group = str(entry.get("grp") or "").strip().lower()
        if not group or group not in parent_targets:
            stats["without_parent"] += 1
            continue
        target = parent_targets[group]
        direct = resolve_identity_target(entry, aliases, mem_slugs)
        if direct:
            stats["kept_existing"] += 1
            continue
        for key in alias_keys_for_entry(entry):
            existing = aliases.get(key)
            if existing:
                if existing != target:
                    stats["conflicts"] += 1
                continue
            aliases[key] = target
            additions[target].append(key)
            stats["added"] += 1
            stats["clone_added"] += 1

    return aliases, stats, additions


def main() -> int:
    parser = argparse.ArgumentParser(description="Expand arcade alias.json from RetroBat gamelist clone metadata")
    parser.add_argument("--plugin-root", default=str(Path(__file__).resolve().parents[1]))
    parser.add_argument("--ram-system", default="arcade")
    parser.add_argument("--gamelist-system", default="mame")
    parser.add_argument("--include-hacks", action="store_true")
    parser.add_argument("--include-bootlegs", action="store_true")
    parser.add_argument(
        "--include-identity-aliases",
        action="store_true",
        help="Also add parent/name/archive aliases. Prefer generating these in the curator source of truth.",
    )
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    plugin_root = Path(args.plugin_root)
    ram_dir = plugin_root / "resources" / "ram" / args.ram_system
    alias_path = ram_dir / "alias.json"
    gamelist_path = plugin_root / "resources" / "gamelist" / "systems" / f"{args.gamelist_system}_lt.json"

    aliases = load_json_ordered(alias_path)
    entries = list(iter_jsonl(gamelist_path))
    mem_slugs = {path.stem for path in ram_dir.glob("*.MEM")}

    updated, stats, additions = expand_aliases(
        aliases,
        entries,
        mem_slugs,
        include_hacks=args.include_hacks,
        include_bootlegs=args.include_bootlegs,
        include_identity_aliases=args.include_identity_aliases,
    )

    print(f"alias: {alias_path}")
    print(f"gamelist: {gamelist_path}")
    print(f"mem files: {len(mem_slugs)}")
    for key, value in stats.items():
        print(f"{key}: {value}")
    for target, keys in list(additions.items())[:12]:
        preview = ", ".join(keys[:8])
        if len(keys) > 8:
            preview += ", ..."
        print(f"target {target}: +{len(keys)} [{preview}]")

    if not args.dry_run:
        with alias_path.open("w", encoding="utf-8") as handle:
            json.dump(updated, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        print("wrote: yes")
    else:
        print("wrote: dry-run")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
