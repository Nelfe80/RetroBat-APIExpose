# -*- coding: utf-8 -*-
"""Genere resources/ram/arcade/mame-banks.json par analyse statique des sources.

Principe (valide sur 1943, triple concordance sonde/FBNeo/MAME) :
- les .MEM arcade portent des offsets du blob RAM FBNeo (MemIndex,
  section RamStart -> RamEnd : ordre et tailles des banques) ;
- les sources MAME donnent les adresses cibles (map(...).ram().share("x")) ;
- l'appariement nom+taille traduit chaque banque, et chaque adresse .MEM
  est validee contre les banques traduites.

Usage : python mame_banks_gen.py [--fbneo E:\\FBNeo-master\\FBNeo-master]
                                 [--mame E:\\mame-master\\mame-master]
"""
import argparse
import io
import json
import os
import re
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_ROOT = os.path.dirname(os.path.dirname(HERE))
ARCADE = os.path.join(PLUGIN_ROOT, "resources", "ram", "arcade")

SET_NAME = re.compile(r"^[a-z0-9_]{2,16}$")
MEM_ADDR = re.compile(r"address\s*=\s*0[xX]([0-9A-Fa-f]+)")
BANK_LINE = re.compile(r"^\s*(\w+)\s*=\s*Next;\s*Next\s*\+=\s*([0-9a-fA-Fx*() \t]+);")
BURNDRV = re.compile(r'struct\s+BurnDriver\w*\s+BurnDrv\w+\s*=\s*\{\s*\n?\s*"([^"]+)"', re.S)
MAP_FN = re.compile(r"::(\w+)\s*\(\s*address_map\s*&map")
MAP_ENTRY = re.compile(r"map\((0x[0-9a-fA-F]+),\s*(0x[0-9a-fA-F]+)\)([^;]*);")
SHARE = re.compile(r'share\("(\w+)"\)')
ROM_START = re.compile(r"ROM_START\(\s*(\w+)\s*\)")


def parse_size(expr):
    expr = expr.strip()
    try:
        # expressions simples uniquement (0x1000, 2048, 128 * 32 * 32)
        if re.fullmatch(r"[0-9a-fA-Fx*() \t]+", expr):
            return int(eval(expr, {"__builtins__": {}}, {}))
    except Exception:
        pass
    return None


def classify(name):
    """Categorie semantique d'une banque FBNeo ou d'un share MAME."""
    n = name.lower().removeprefix("drv").removeprefix("m_")
    if "sprite" in n or n.startswith("spr"):
        return "sprite"
    if "palette" in n or "color" in n or "colour" in n or "attr" in n or n.startswith("col"):
        return "color"
    if "video" in n or "vid" in n or n.startswith("fg") or n.startswith("bg") or \
       n.startswith("tx") or "text" in n or "tile" in n or "scroll" in n:
        return "video"
    if "nvram" in n or "nv" == n:
        return "nvram"
    if "share" in n:
        return "shared"
    if re.search(r"(ram\d*|workram|mainram)$", n) or "ram" in n:
        return "work"
    return "other"


MEM_LABEL = re.compile(r'label\s*=\s*"([a-z0-9_]{2,16})\.(?:7z|zip)', re.I)
MEM_ROMNAME = re.compile(r'name\s*=\s*"([a-z0-9_]{2,16})"')


def load_mems():
    """slug -> (adresses .MEM, noms de set declares par le fichier)."""
    mems = {}
    for f in os.listdir(ARCADE):
        if not f.endswith(".MEM"):
            continue
        text = io.open(os.path.join(ARCADE, f), encoding="utf-8", errors="replace").read()
        addrs = sorted({int(m, 16) for m in MEM_ADDR.findall(text)})
        # les labels de hash (wjammers.7z) donnent le nom de set exact
        sets = {m.lower() for m in MEM_LABEL.findall(text)}
        rom_block = re.search(r"rom\s*=\s*\{(.*?)\n\s*\}", text, re.S)
        if rom_block:
            sets.update(m.lower() for m in MEM_ROMNAME.findall(rom_block.group(1)))
        mems[f[:-4]] = (addrs, sets)
    return mems


def load_alias():
    """slug -> noms de set candidats (cles courtes de alias.json)."""
    path = os.path.join(ARCADE, "alias.json")
    table = json.load(io.open(path, encoding="utf-8"))
    by_slug = defaultdict(set)
    for key, slug in table.items():
        k = key.lower().removesuffix(".7z").removesuffix(".zip")
        if SET_NAME.fullmatch(k):
            by_slug[slug].add(k)
    return by_slug


def extract_ram_sections(text):
    """Sections RAM d'un source FBNeo (conventions RamStart|AllRam -> RamEnd|MemEnd)."""
    return re.findall(
        r"(?:RamStart|AllRam)\s*=\s*Next;(.*?)(?:RamEnd|MemEnd)\s*=\s*Next;", text, re.S)


def parse_banks(section):
    banks = []
    for line in section.splitlines():
        m = BANK_LINE.match(line)
        if m:
            size = parse_size(m.group(2))
            if size:
                banks.append((m.group(1), size))
    return banks


def scan_fbneo(root):
    """set -> (fichier driver, banques ordonnees [(nom, taille)])."""
    by_set = {}
    multi_memindex = []
    for dirpath, _, files in os.walk(os.path.join(root, "src", "burn", "drv")):
        # allocation partagee de la famille (ex. galaxian : gal_run.cpp) :
        # utilisee par les d_*.cpp du dossier qui n'ont pas leur propre MemIndex
        shared = []
        for f in files:
            if f.startswith("d_") or not f.endswith(".cpp"):
                continue
            for section in extract_ram_sections(
                    io.open(os.path.join(dirpath, f), encoding="utf-8", errors="replace").read()):
                banks = parse_banks(section)
                if banks:
                    shared.append((f, banks))
        for f in files:
            if not (f.startswith("d_") and f.endswith(".cpp")):
                continue
            path = os.path.join(dirpath, f)
            text = io.open(path, encoding="utf-8", errors="replace").read()
            sets = set(BURNDRV.findall(text))
            if not sets:
                continue
            sections = extract_ram_sections(text)
            if len(sections) > 1:
                multi_memindex.append(f)
            banks = parse_banks(sections[0]) if sections else None
            if not banks and len(shared) == 1:
                banks = shared[0][1]
            if banks:
                for s in sets:
                    by_set[s.lower()] = (f, banks)
    return by_set, multi_memindex


def index_mame(root):
    """set -> fichier source MAME (via ROM_START)."""
    idx = {}
    src = os.path.join(root, "src", "mame")
    for dirpath, _, files in os.walk(src):
        for f in files:
            if not f.endswith(".cpp"):
                continue
            path = os.path.join(dirpath, f)
            text = io.open(path, encoding="utf-8", errors="replace").read()
            for name in ROM_START.findall(text):
                idx.setdefault(name.lower(), path)
    return idx


def parse_mame_maps(path, cache={}):
    """fichier -> {fonction: [(debut, fin, share|None)]} (entrees .ram())."""
    if path in cache:
        return cache[path]
    maps = defaultdict(list)
    current = None
    for line in io.open(path, encoding="utf-8", errors="replace"):
        fn = MAP_FN.search(line)
        if fn:
            current = fn.group(1)
            continue
        if current is None:
            continue
        for m in MAP_ENTRY.finditer(line):
            rest = m.group(3)
            if ".ram()" not in rest and ".ram()" not in rest.replace(" ", ""):
                continue
            share = SHARE.search(rest)
            maps[current].append((int(m.group(1), 16), int(m.group(2), 16),
                                  share.group(1) if share else None))
    cache[path] = dict(maps)
    return cache[path]


def merged_candidates(regions):
    """Regions + fusions des contigues : FBNeo alloue souvent UN bloc la ou
    MAME decoupe (pac-man : video+color+work = 0x4000-0x4FFF)."""
    out = list(regions)
    ordered = sorted(regions, key=lambda r: (r[0], r[1]))
    for i in range(len(ordered)):
        start, end, share = ordered[i]
        names = [share] if share else []
        for j in range(i + 1, len(ordered)):
            nstart, nend, nshare = ordered[j]
            if nstart != end + 1:
                break
            end = nend
            if nshare:
                names.append(nshare)
            out.append((start, end, "+".join(names) if names else None))
    return out


def match_banks(banks, regions):
    """Apparie chaque banque FBNeo a une region MAME : taille EXACTE
    obligatoire (un mauvais rebasage est pire que pas de table)."""
    candidates = merged_candidates(regions)
    used = []
    assigned = []
    for name, size in banks:
        cat = classify(name)
        exact = [c for c in candidates
                 if (c[1] - c[0] + 1) == size and
                 not any(u[0] <= c[1] and c[0] <= u[1] for u in used)]
        best = None
        for c in exact:
            rcat = classify(c[2].split("+")[0]) if c[2] else "work"
            if rcat == cat or (cat == "work" and c[2] and "+" in c[2]):
                best = c
                break
        if best is None and cat == "work" and len(exact) == 1:
            best = exact[0]  # une seule region de la bonne taille : sans ambiguite
        if best is not None:
            used.append((best[0], best[1]))
            assigned.append((name, size, best[0], best[2]))
        else:
            assigned.append((name, size, None, None))
    return assigned


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fbneo", default=r"E:\FBNeo-master\FBNeo-master")
    ap.add_argument("--mame", default=r"E:\mame-master\mame-master")
    ap.add_argument("--out", default=os.path.join(ARCADE, "mame-banks.json"))
    args = ap.parse_args()

    print("scan .MEM ...", flush=True)
    mems = load_mems()
    by_slug = load_alias()
    print(f"  {len(mems)} .MEM")

    print("scan FBNeo ...", flush=True)
    fbneo, multi = scan_fbneo(args.fbneo)
    print(f"  {len(fbneo)} sets avec MemIndex ({len(multi)} drivers multi-MemIndex, 1er retenu)")

    print("index MAME ...", flush=True)
    mame_idx = index_mame(args.mame)
    print(f"  {len(mame_idx)} sets ROM_START")

    out = {"$comment": "GENERE par tools/mem-curator/mame_banks_gen.py - blob FBNeo (MemIndex) -> "
                       "espace programme MAME (map .ram/.share). Ne pas editer a la main."}
    stats = defaultdict(list)

    for slug, (addrs, own_sets) in sorted(mems.items()):
        if not addrs:
            stats["sans-adresse"].append(slug)
            continue
        # les sets declares par le .MEM (labels de hash) passent en premier
        sets = sorted(own_sets, key=len) + sorted(by_slug.get(slug, set()) - own_sets, key=len)
        fb = next(((s, fbneo[s]) for s in sets if s in fbneo), None)
        if fb is None:
            stats["fbneo-introuvable"].append(slug)
            continue
        set_name, (fb_file, banks) = fb
        blob_size = sum(size for _, size in banks)
        mame_file = next((mame_idx[s] for s in sets if s in mame_idx), None)
        if mame_file is None:
            stats["mame-introuvable"].append(slug)
            continue

        maps = parse_mame_maps(mame_file)
        best_fn, best_assign, best_score = None, None, -1
        for fn, regions in maps.items():
            assign = match_banks(banks, regions)
            score = sum(1 for _, _, base, _ in assign if base is not None)
            if score > best_score:
                best_fn, best_assign, best_score = fn, assign, score
        if not best_assign or best_score == 0:
            stats["aucun-appariement"].append(slug)
            continue

        # validation : chaque adresse .MEM doit tomber dans une banque traduite
        table, offset = [], 0
        spans = []
        for name, size, base, share in best_assign:
            spans.append((offset, offset + size, base, name, share))
            offset += size
        unmapped, absolute = [], []
        for a in addrs:
            hit = next((s for s in spans if s[0] <= a < s[1]), None)
            if hit is None:
                # hors blob : adresse deja absolue (le plugin la laisse telle
                # quelle si elle tombe dans une region RAM de la machine)
                absolute.append(a)
            elif hit[2] is None:
                unmapped.append(a)

        used_spans = [sp for sp in spans
                      if sp[2] is not None and any(sp[0] <= a < sp[1] for a in addrs)]
        # garde-fou : dans un fichier MAME multi-machines, un appariement
        # purement anonyme peut viser la mauvaise machine -> quarantaine
        if used_spans and len(maps) > 1 and all(sp[4] is None for sp in used_spans):
            stats["ambigu-quarantaine"].append(slug)
            used_spans = []
        for start, stop, base, name, _ in used_spans:
            table.append({"from": f"0x{start:04X}", "size": f"0x{stop - start:04X}",
                          "base": f"0x{base:04X}", "bank": name})

        if absolute and not unmapped and not table:
            stats["adresses-absolues"].append(slug)
        if unmapped:
            stats["adresses-non-mappees"].append(
                f"{slug} ({', '.join(f'0x{a:X}' for a in unmapped[:4])})")
        if table:
            main_only = len(table) == 1 and table[0]["from"] == "0x0000"
            out[slug] = table
            stats["main-seule" if main_only else "multi-banques"].append(slug)
        else:
            stats["aucune-banque-utile"].append(slug)

    io.open(args.out, "w", encoding="utf-8", newline="\n").write(
        json.dumps(out, indent=2, ensure_ascii=False) + "\n")

    report = os.path.join(PLUGIN_ROOT, ".log", "mame-banks-report.txt")
    with io.open(report, "w", encoding="utf-8", newline="\n") as r:
        for key in sorted(stats):
            r.write(f"== {key} : {len(stats[key])}\n")
            for item in stats[key]:
                r.write(f"  {item}\n")
    print(f"\n{args.out} : {len(out) - 1} jeux")
    for key in sorted(stats):
        print(f"  {key}: {len(stats[key])}")
    print(f"rapport : {report}")


if __name__ == "__main__":
    sys.exit(main())
