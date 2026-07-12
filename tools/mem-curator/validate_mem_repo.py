# -*- coding: utf-8 -*-
"""validate_mem_repo : audit d'un arbre resources/ram contre le contrat runtime.

Simule le chargement des .MEM tel que le fait plugins/Wrapper/wrapper.cpp
(LoadMemFile + ProcessWatchValue) qui est le parseur de reference, et signale
tout ce qui serait ignore, mort ou casse au runtime :

- condition inconnue du wrapper (ne declenchera jamais)
- type non supporte (retombe en u8 taille 1)
- bit_true/bit_false sans mask explicite (mask par defaut 0xFFFFFFFF)
- eq/neq sans value= (compare a 0)
- entrees mortes : no_log=true ou no_survey=true (sautees au chargement)
- desc contenant le mot "address" (casse le decoupage des entrees)
- action hors charset [A-Z0-9_]+ (illisible par la regex APIExpose)
- famille category.subfamily inconnue de la taxonomie V11
- alias.json : cibles orphelines (aucun <slug>.MEM)
- fichiers sans aucun watcher vivant

Usage :
    py validate_mem_repo.py <ram_dir> [--json rapport.json] [--per-file]
"""

import argparse
import json
import os
import re
import sys
from collections import OrderedDict

KNOWN_CONDITIONS = {
    "change", "eq", "equal", "neq", "not_equal",
    "increase", "decrease", "bit_true", "bit_false", "color",
}
KNOWN_TYPES = {"u8", "u16be", "u16le", "u24be", "u24le", "u32be", "u32le"}
KNOWN_FAMILIES = {
    "flow.lifecycle", "flow.settings", "flow.events",
    "progression.level", "progression.zone", "progression.stage",
    "resources.lives", "resources.health", "resources.secondary", "resources.environmental",
    "inventory.items", "inventory.weapon",
    "scoring.points", "scoring.collectibles", "scoring.experience",
    "combat.enemies", "combat.boss", "combat.tactical",
    "racing.vehicle",
    "state.temporary", "state.player", "state.mount",
    "world_interaction.objects",
    "system.movement", "system.timer", "system.memory", "system.unmapped", "system.internal",
}
ACTION_RE = re.compile(r"^[A-Z0-9_]+$")
CATEGORY_LINE_RE = re.compile(r"^ {4}([A-Za-z_][A-Za-z0-9_]*) = \{")
SUBFAMILY_LINE_RE = re.compile(r"^ {6}([A-Za-z_][A-Za-z0-9_]*) = \{")


def parse_kv_string(entry, key):
    match = re.search(r'\b' + key + r'\s*=\s*"([^"]*)"', entry)
    return match.group(1) if match else None


def parse_kv_number(entry, key):
    match = re.search(r'\b' + key + r'\s*=\s*(0[xX][0-9A-Fa-f]+|-?\d+)', entry)
    if not match:
        return None
    raw = match.group(1)
    try:
        return int(raw, 16) if raw.lower().startswith("0x") else int(raw, 10)
    except ValueError:
        return None


def parse_kv_flag(entry, key):
    return re.search(r'\b' + key + r'\s*=\s*true\b', entry) is not None


def wrapper_entries(content):
    """Decoupe le contenu comme wrapper.cpp : un morceau par occurrence de 'address'."""
    positions = [m.start() for m in re.finditer(r"address", content)]
    out = []
    for i, pos in enumerate(positions):
        end = positions[i + 1] if i + 1 < len(positions) else len(content)
        out.append((pos, content[pos:end]))
    return out


def line_context(content):
    """category/subfamily par ligne (layout canonique du generateur)."""
    context = {}
    category = ""
    subfamily = ""
    offset = 0
    for line in content.splitlines(keepends=True):
        stripped = line.rstrip("\n")
        cat = CATEGORY_LINE_RE.match(stripped)
        if cat and cat.group(1) not in {"game", "rom"}:
            category = cat.group(1)
        sub = SUBFAMILY_LINE_RE.match(stripped)
        if sub:
            subfamily = sub.group(1)
        context[offset] = (category, subfamily)
        offset += len(line)
    return context


def context_for(context_map, pos):
    best = ("", "")
    for offset in context_map:
        if offset > pos:
            break
        best = context_map[offset]
    return best


def validate_file(path, issues, stats):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        content = fh.read()

    entries = wrapper_entries(content)
    context_map = line_context(content)
    live = 0

    for pos, entry in entries:
        stats["entries"] += 1
        first_line = entry.split("\n", 1)[0]

        addr_match = re.match(r"address\s*=\s*([^,\s}]+)", entry)
        if not addr_match:
            issues.append((path, "entry-sans-adresse", first_line[:90]))
            stats["broken"] += 1
            continue
        addr_raw = addr_match.group(1)
        try:
            int(addr_raw, 16) if addr_raw.lower().startswith("0x") else int(addr_raw, 0)
        except ValueError:
            issues.append((path, "adresse-illisible", addr_raw[:40]))
            stats["broken"] += 1
            continue

        typ = (parse_kv_string(entry, "type") or "u8").lower()
        if typ not in KNOWN_TYPES:
            issues.append((path, "type-non-supporte", typ))
            stats["bad_type"] += 1

        condition = (parse_kv_string(entry, "condition") or "change").lower()
        if condition not in KNOWN_CONDITIONS:
            issues.append((path, "condition-inconnue-wrapper", condition))
            stats["bad_condition"] += 1

        value = parse_kv_number(entry, "value")
        mask = parse_kv_number(entry, "mask")
        if condition in {"bit_true", "bit_false"} and mask is None:
            issues.append((path, "bit-sans-mask", first_line[:90]))
            stats["bit_no_mask"] += 1
        if condition in {"eq", "equal", "neq", "not_equal"} and value is None:
            issues.append((path, "eq-sans-value", first_line[:90]))
            stats["eq_no_value"] += 1

        no_log = parse_kv_flag(entry, "no_log")
        no_survey = parse_kv_flag(entry, "no_survey")
        if no_log or no_survey:
            stats["dead"] += 1
        else:
            live += 1

        desc = parse_kv_string(entry, "desc") or ""
        if "address" in desc.lower():
            issues.append((path, "desc-contient-address", desc[:90]))
            stats["desc_address"] += 1

        action = parse_kv_string(entry, "action")
        if action is None:
            issues.append((path, "action-absente", first_line[:90]))
            stats["no_action"] += 1
        elif not ACTION_RE.match(action):
            issues.append((path, "action-charset", action[:60]))
            stats["bad_action"] += 1

        category, subfamily = context_for(context_map, pos)
        if category and subfamily:
            family = f"{category}.{subfamily}"
            if family not in KNOWN_FAMILIES:
                issues.append((path, "famille-inconnue", family))
                stats["bad_family"] += 1

    if entries and live == 0:
        stats["files_all_dead"] += 1
        issues.append((path, "fichier-sans-watcher-vivant", f"{len(entries)} entrees toutes mortes"))
    if not entries:
        stats["files_empty"] += 1
    stats["live"] += live
    return len(entries), live


def validate_system(system_dir, issues, stats, per_file_rows):
    mem_files = sorted(
        name for name in os.listdir(system_dir)
        if name.upper().endswith(".MEM")
    )
    slugs = {os.path.splitext(name)[0] for name in mem_files}

    for name in mem_files:
        path = os.path.join(system_dir, name)
        stats["files"] += 1
        total, live = validate_file(path, issues, stats)
        if per_file_rows is not None:
            per_file_rows.append({"file": path, "entries": total, "live": live})

    alias_path = os.path.join(system_dir, "alias.json")
    if os.path.exists(alias_path):
        try:
            with open(alias_path, "r", encoding="utf-8") as fh:
                aliases = json.load(fh)
        except Exception as exc:
            issues.append((alias_path, "alias-json-illisible", str(exc)[:90]))
            stats["alias_broken_files"] += 1
            return
        stats["alias_keys"] += len(aliases)
        orphan_targets = sorted({v for v in aliases.values() if v not in slugs})
        for target in orphan_targets:
            issues.append((alias_path, "alias-cible-orpheline", target))
            stats["alias_orphans"] += 1
    elif mem_files:
        issues.append((system_dir, "alias-json-absent", ""))


def main():
    parser = argparse.ArgumentParser(description="Validation d'un arbre resources/ram (contrat wrapper.cpp)")
    parser.add_argument("ram_dir")
    parser.add_argument("--json", default="", help="Chemin du rapport JSON detaille")
    parser.add_argument("--per-file", action="store_true", help="Inclut le detail par fichier dans le JSON")
    args = parser.parse_args()

    ram_dir = os.path.abspath(args.ram_dir)
    if not os.path.isdir(ram_dir):
        print(f"[error] dossier introuvable: {ram_dir}")
        return 1

    issues = []
    per_file_rows = [] if args.per_file else None
    stats = OrderedDict([
        ("files", 0), ("entries", 0), ("live", 0), ("dead", 0),
        ("broken", 0), ("bad_type", 0), ("bad_condition", 0),
        ("bit_no_mask", 0), ("eq_no_value", 0), ("desc_address", 0),
        ("no_action", 0), ("bad_action", 0), ("bad_family", 0),
        ("files_all_dead", 0), ("files_empty", 0),
        ("alias_keys", 0), ("alias_orphans", 0), ("alias_broken_files", 0),
    ])

    systems = sorted(
        name for name in os.listdir(ram_dir)
        if os.path.isdir(os.path.join(ram_dir, name)) and not name.startswith((".", "_"))
        and name != "tools"
    )
    for system in systems:
        validate_system(os.path.join(ram_dir, system), issues, stats, per_file_rows)

    print(f"=== validate_mem_repo : {ram_dir} ===")
    print(f"systemes: {len(systems)}")
    for key, value in stats.items():
        print(f"{key}: {value}")

    kinds = OrderedDict()
    for _path, kind, _detail in issues:
        kinds[kind] = kinds.get(kind, 0) + 1
    if kinds:
        print("--- anomalies par type ---")
        for kind, count in sorted(kinds.items(), key=lambda item: -item[1]):
            print(f"{kind}: {count}")

    if args.json:
        report = {
            "ram_dir": ram_dir,
            "systems": len(systems),
            "stats": stats,
            "issue_kinds": kinds,
            "issues": [
                {"path": path, "kind": kind, "detail": detail}
                for path, kind, detail in issues[:20000]
            ],
        }
        if per_file_rows is not None:
            report["files"] = per_file_rows
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2, ensure_ascii=False)
        print(f"[+] rapport: {args.json}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
