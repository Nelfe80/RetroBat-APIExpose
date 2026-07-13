# -*- coding: utf-8 -*-
"""Banc de validation de masse des .MEM arcade contre MAME standalone.

Pour chaque rom presente dans le dossier roms : lance MAME headless avec
insertion credit + start automatique (coinstart.lua), laisse le pont
apiexpose_ingame poser les watches, puis interroge /api/v1/mamelua/sessions
pour compter quelles adresses ont reellement parle. Aucune intervention
manuelle : le rapport classe chaque jeu.

Usage : python mame_batch_validate.py [--roms E:\\RetroBat\\roms\\mame]
                                      [--seconds 75] [--limit 0] [--only rom1,rom2]
"""
import argparse
import io
import json
import os
import socket
import subprocess
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_ROOT = os.path.dirname(os.path.dirname(HERE))
MAME_DIR = r"E:\RetroBat\emulators\mame"
API = "http://127.0.0.1:12345"
BRIDGE = ("127.0.0.1", 12347)

COINSTART = os.path.join(HERE, "coinstart.lua")
COINSTART_BODY = """-- insere un credit et demarre la partie (banc de validation .MEM)
local coin_field, start_field, frame = nil, nil, 0
_G.coinstart_sub = emu.add_machine_frame_notifier(function()
    frame = frame + 1
    if not coin_field then
        pcall(function()
            for _, port in pairs(manager.machine.ioport.ports) do
                for fname, field in pairs(port.fields) do
                    if not coin_field and fname:lower():find("coin") then coin_field = field end
                    if not start_field and fname:lower():find("start") and fname:find("1") then start_field = field end
                end
            end
        end)
        return
    end
    if frame == 300 then coin_field:set_value(1) end
    if frame == 310 then coin_field:set_value(0) end
    if start_field then
        if frame == 420 then start_field:set_value(1) end
        if frame == 435 then start_field:set_value(0) end
    end
end)
"""


def resolve_watches(rom):
    """Interroge le pont comme le ferait le plugin : combien de watches ?"""
    try:
        s = socket.create_connection(BRIDGE, timeout=5)
        f = s.makefile("rw", encoding="utf-8", newline="\n")
        if f.readline().strip() != "HELLO?":
            return -1
        f.write(f"HELLO|{rom}|batch-probe\n")
        f.flush()
        count = 0
        while True:
            line = f.readline().strip()
            if line.startswith("WATCH"):
                count += 1
            if line.startswith("READY"):
                break
        s.close()
        return count
    except OSError:
        return -1


def last_session(rom):
    try:
        with urllib.request.urlopen(f"{API}/api/v1/mamelua/sessions", timeout=5) as r:
            sessions = json.load(r)
    except Exception:
        return None
    # la session s'enregistre sous le slug RESOLU (windjammers), pas le nom
    # de set (wjammers) : on prend la plus recente, c'est notre run
    sessions.sort(key=lambda s: s.get("lastMessageAt") or "")
    return sessions[-1] if sessions else None


def run_mame(rom, seconds):
    cmd = [os.path.join(MAME_DIR, "mame.exe"), rom,
           "-str", str(seconds), "-video", "none", "-sound", "none", "-w",
           "-skip_gameinfo",
           "-rp", r"E:\RetroBat\bios;E:\RetroBat\roms\mame",
           "-inipath", r"E:\RetroBat\bios\mame\ini",
           "-cfg_directory", r"E:\RetroBat\saves\mame\cfg",
           "-nvram_directory", r"E:\RetroBat\saves\mame\nvram",
           "-hash", r"E:\RetroBat\bios\mame\hash",
           "-autoboot_script", COINSTART]
    try:
        proc = subprocess.run(cmd, cwd=MAME_DIR, capture_output=True, text=True,
                              timeout=seconds * 4 + 120)
        return proc.returncode
    except subprocess.TimeoutExpired:
        return -999


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--roms", default=r"E:\RetroBat\roms\mame")
    ap.add_argument("--seconds", type=int, default=75)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--only", default="")
    args = ap.parse_args()

    io.open(COINSTART, "w", encoding="utf-8", newline="\n").write(COINSTART_BODY)

    roms = sorted(os.path.splitext(f)[0] for f in os.listdir(args.roms)
                  if f.endswith((".zip", ".7z")))
    if args.only:
        wanted = {r.strip() for r in args.only.split(",")}
        roms = [r for r in roms if r in wanted]

    results = []
    for i, rom in enumerate(roms):
        watches = resolve_watches(rom)
        if watches <= 0:
            results.append({"rom": rom, "status": "sans-mem" if watches == 0 else "pont-injoignable",
                            "watches": watches})
            print(f"[{i + 1}/{len(roms)}] {rom}: {results[-1]['status']}")
            continue

        code = run_mame(rom, args.seconds)
        time.sleep(2)
        session = last_session(rom)
        if session is None:
            status, fired, silent = "session-absente", {}, watches
        else:
            fired = session.get("firedByAddress") or {}
            silent = sum(1 for v in fired.values() if v == 0)
            active = len(fired) - silent
            status = ("ok" if silent == 0 else
                      "partiel" if active > 0 else "muet")
        results.append({"rom": rom, "status": status, "watches": watches,
                        "silencieux": silent, "exit": code, "fired": fired})
        print(f"[{i + 1}/{len(roms)}] {rom}: {status} "
              f"({watches - silent}/{watches} adresses actives)")

        if args.limit and len([r for r in results if r["status"] != "sans-mem"]) >= args.limit:
            break

    out = os.path.join(PLUGIN_ROOT, ".log", "mame-batch-validate.json")
    io.open(out, "w", encoding="utf-8", newline="\n").write(
        json.dumps(results, indent=2, ensure_ascii=False) + "\n")
    counts = {}
    for r in results:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    print("\nbilan:", json.dumps(counts, ensure_ascii=False))
    print("details:", out)


if __name__ == "__main__":
    sys.exit(main())
