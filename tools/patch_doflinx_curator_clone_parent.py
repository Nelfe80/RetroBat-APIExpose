from pathlib import Path


TARGET = Path(r"C:\Users\vince\Downloads\DOFLinx_V909\API\FINAL\ag_mass_curator_local_v2_all_systems.py")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"target block not found: {label}")
    return text.replace(old, new, 1)


def main() -> int:
    text = TARGET.read_text(encoding="utf-8")

    text = replace_once(
        text,
        """def arcade_parent_rom_with_score(rom, source_base):
    candidates = []
    match = re.match(r"^(.+\\d)[a-z]$", rom or "", re.I)
    if match:
        candidates.append(match.group(1))
    for candidate in unique_keep_order(candidates):
        text, resolved_rom, _path = load_doflinx_mem_text(candidate, source_base)
        if extract_doflinx_score_entries(text):
            return resolved_rom or candidate
    return ""
""",
        """def arcade_parent_rom_with_score(rom, source_base, system="", gamelist_dir=""):
    candidates = []
    _path, _entries, entry = find_arcade_gamelist_entry_by_rom(system, rom, gamelist_dir)
    group = str((entry or {}).get("grp") or "").strip()
    if group and group.lower() != str(rom or "").strip().lower():
        candidates.append(group)
    match = re.match(r"^(.+\\d)[a-z]+$", rom or "", re.I)
    if match:
        candidates.append(match.group(1))
    for candidate in unique_keep_order(candidates):
        text, resolved_rom, _path = load_doflinx_mem_text(candidate, source_base)
        if extract_doflinx_score_entries(text):
            return resolved_rom or candidate
    return ""
""",
        "parent rom helper",
    )

    text = replace_once(
        text,
        """        parent_rom = arcade_parent_rom_with_score(rom, args.source_base)
""",
        """        parent_rom = arcade_parent_rom_with_score(rom, args.source_base, args.system, args.gamelist_dir)
""",
        "parent rom call",
    )

    text = replace_once(
        text,
        """            if args.force and os.path.exists(mem_path):
                os.remove(mem_path)
                print(f"   [i] removed stale empty {mem_path}")
""",
        """            stale_mem_path = os.path.join(output_dir, f"{raw_slug}.MEM")
            if args.force and stale_mem_path != os.path.join(output_dir, f"{parent_slug}.MEM") and os.path.exists(stale_mem_path):
                os.remove(stale_mem_path)
                print(f"   [i] removed stale empty {stale_mem_path}")
""",
        "stale clone cleanup",
    )

    TARGET.write_text(text, encoding="utf-8")
    print(f"patched {TARGET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
