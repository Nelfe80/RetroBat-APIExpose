# -*- coding: utf-8 -*-
"""Valide les traductions .MEM -> MAME contre l'oracle CFG_AUTO de DOFLinx.

CFG_AUTO/<rom>.cfg contient des adresses MAME NATIVES validees en conditions
reelles (memwatchv2). Pour chaque rom : on demande au pont apiexpose_ingame
les watches traduits (HELLO simule) et on verifie que chaque adresse native
du cfg y figure. Verdict par jeu sans lancer un seul emulateur.

Usage : python mame_banks_crosscheck.py [--cfg C:\\...\\DOFLinx_V909\\CFG_AUTO]
"""
import argparse
import io
import json
import os
import re
import socket
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_ROOT = os.path.dirname(os.path.dirname(HERE))
BRIDGE = ("127.0.0.1", 12347)
WATCH_LINE = re.compile(r"^\s*(\w+)\s*=\s*(0[xX][0-9A-Fa-f]+)\s+(\w+)", re.M)


def parse_cfg(path):
    text = io.open(path, encoding="utf-8", errors="replace").read()
    return {label: int(addr, 16) for label, addr, _typ in WATCH_LINE.findall(text)}


def bridge_watches(rom):
    try:
        s = socket.create_connection(BRIDGE, timeout=5)
        f = s.makefile("rw", encoding="utf-8", newline="\n")
        if f.readline().strip() != "HELLO?":
            return None
        f.write(f"HELLO|{rom}|crosscheck\n")
        f.flush()
        watches = set()
        while True:
            line = f.readline().strip()
            if line.startswith("WATCH"):
                watches.add(int(line.split("|")[2], 16))
            if line.startswith("READY"):
                break
        s.close()
        return watches
    except OSError:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cfg", default=r"C:\Users\vince\Downloads\DOFLinx_V909\CFG_AUTO")
    args = ap.parse_args()

    results = []
    counts = Counter()
    for f in sorted(os.listdir(args.cfg)):
        if not f.endswith(".cfg"):
            continue
        rom = f[:-4]
        cfg = parse_cfg(os.path.join(args.cfg, f))
        if not cfg:
            continue
        watches = bridge_watches(rom)
        if watches is None:
            counts["pont-injoignable"] += 1
            continue
        if not watches:
            counts["sans-mem"] += 1
            results.append({"rom": rom, "status": "sans-mem", "cfg": {k: hex(v) for k, v in cfg.items()}})
            continue
        hits = {label: addr in watches for label, addr in cfg.items()}
        ok = sum(hits.values())
        status = ("confirme" if ok == len(cfg) else
                  "partiel" if ok else "ecart-total")
        counts[status] += 1
        detail = {"rom": rom, "status": status,
                  "natifs": {k: hex(v) for k, v in cfg.items()},
                  "presents": [k for k, h in hits.items() if h],
                  "absents": {k: hex(cfg[k]) for k, h in hits.items() if not h}}
        if status != "confirme":
            detail["watches"] = sorted(hex(w) for w in watches)[:16]
        results.append(detail)

    out = os.path.join(PLUGIN_ROOT, ".log", "mame-banks-crosscheck.json")
    io.open(out, "w", encoding="utf-8", newline="\n").write(
        json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    print("bilan:", json.dumps(counts, ensure_ascii=False))
    print("details:", out)


if __name__ == "__main__":
    sys.exit(main())
