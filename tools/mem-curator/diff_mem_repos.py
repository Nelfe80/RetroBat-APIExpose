# -*- coding: utf-8 -*-
"""diff_mem_repos : compare deux arbres resources/ram (deploye vs staging).

Compare par systeme : ensembles de fichiers .MEM (ajoutes/supprimes), nombre
d'entrees vivantes/mortes (au sens wrapper.cpp : no_log/no_survey = mort),
et cles d'alias. Sert de rapport de non-regression avant deploiement.

Usage :
    py diff_mem_repos.py <ram_avant> <ram_apres> [--json rapport.json]
"""

import argparse
import json
import os
import re
import sys
from collections import OrderedDict

DEAD_RE = re.compile(r"no_(?:log|survey)\s*=\s*true")


def scan_system(system_dir):
    files = {}
    for name in sorted(os.listdir(system_dir)):
        if not name.upper().endswith(".MEM"):
            continue
        path = os.path.join(system_dir, name)
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            content = fh.read()
        entries = len(re.findall(r"\baddress\s*=", content))
        dead = 0
        positions = [m.start() for m in re.finditer(r"\baddress\s*=", content)]
        for i, pos in enumerate(positions):
            end = positions[i + 1] if i + 1 < len(positions) else len(content)
            if DEAD_RE.search(content[pos:end]):
                dead += 1
        files[name] = {"entries": entries, "live": entries - dead, "dead": dead}
    alias_keys = 0
    alias_path = os.path.join(system_dir, "alias.json")
    if os.path.exists(alias_path):
        try:
            with open(alias_path, "r", encoding="utf-8") as fh:
                alias_keys = len(json.load(fh))
        except Exception:
            alias_keys = -1
    return files, alias_keys


def scan_tree(ram_dir):
    tree = OrderedDict()
    for system in sorted(os.listdir(ram_dir)):
        system_dir = os.path.join(ram_dir, system)
        if not os.path.isdir(system_dir) or system.startswith((".", "_")) or system == "tools":
            continue
        tree[system] = scan_system(system_dir)
    return tree


def main():
    parser = argparse.ArgumentParser(description="Diff entre deux arbres ram")
    parser.add_argument("before")
    parser.add_argument("after")
    parser.add_argument("--json", default="")
    args = parser.parse_args()

    before = scan_tree(os.path.abspath(args.before))
    after = scan_tree(os.path.abspath(args.after))

    systems = sorted(set(before) | set(after))
    totals = {
        "files_before": 0, "files_after": 0, "added": 0, "removed": 0,
        "live_before": 0, "live_after": 0, "dead_before": 0, "dead_after": 0,
        "files_live_lost": 0,
    }
    detail = OrderedDict()
    live_lost_samples = []

    for system in systems:
        files_b, alias_b = before.get(system, ({}, 0))
        files_a, alias_a = after.get(system, ({}, 0))
        added = sorted(set(files_a) - set(files_b))
        removed = sorted(set(files_b) - set(files_a))
        live_b = sum(f["live"] for f in files_b.values())
        live_a = sum(f["live"] for f in files_a.values())
        dead_b = sum(f["dead"] for f in files_b.values())
        dead_a = sum(f["dead"] for f in files_a.values())

        lost = []
        for name in set(files_b) & set(files_a):
            if files_a[name]["live"] < files_b[name]["live"]:
                lost.append((name, files_b[name]["live"], files_a[name]["live"]))
        if lost:
            totals["files_live_lost"] += len(lost)
            for name, lb, la in lost[:3]:
                live_lost_samples.append(f"{system}/{name}: live {lb} -> {la}")

        totals["files_before"] += len(files_b)
        totals["files_after"] += len(files_a)
        totals["added"] += len(added)
        totals["removed"] += len(removed)
        totals["live_before"] += live_b
        totals["live_after"] += live_a
        totals["dead_before"] += dead_b
        totals["dead_after"] += dead_a

        detail[system] = {
            "files_before": len(files_b), "files_after": len(files_a),
            "added": added, "removed": removed,
            "live_before": live_b, "live_after": live_a,
            "dead_before": dead_b, "dead_after": dead_a,
            "alias_keys_before": alias_b, "alias_keys_after": alias_a,
            "files_live_lost": len(lost),
        }

    print(f"=== diff : {args.before} -> {args.after} ===")
    for key, value in totals.items():
        print(f"{key}: {value}")
    if live_lost_samples:
        print("--- exemples de pertes d'events vivants ---")
        for sample in live_lost_samples[:20]:
            print(f"  {sample}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"totals": totals, "systems": detail}, fh, indent=2, ensure_ascii=False)
        print(f"[+] rapport: {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
