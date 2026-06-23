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
        """def process_arcade_doflinx_game(args, doflinx_file):
    rom = os.path.splitext(os.path.basename(doflinx_file))[0]
    slug = slugify_title(rom)
""",
        """def process_arcade_doflinx_game(args, doflinx_file):
    rom = os.path.splitext(os.path.basename(doflinx_file))[0]
    raw_slug = slugify_title(rom)
    _gamelist_path, _gamelist_entries, gamelist_entry = find_arcade_gamelist_entry_by_rom(
        args.system, rom, args.gamelist_dir
    )
    slug = arcade_canonical_slug_from_entry(gamelist_entry, raw_slug)
""",
        "entry slug",
    )

    text = replace_once(
        text,
        """            parent_slug = slugify_title(parent_rom)
            score_delta_report["alias_target"] = parent_slug
""",
        """            parent_raw_slug = slugify_title(parent_rom)
            _parent_gamelist_path, _parent_gamelist_entries, parent_gamelist_entry = find_arcade_gamelist_entry_by_rom(
                args.system, parent_rom, args.gamelist_dir
            )
            parent_slug = arcade_canonical_slug_from_entry(parent_gamelist_entry, parent_raw_slug)
            score_delta_report["alias_target"] = parent_slug
""",
        "parent slug",
    )

    text = replace_once(
        text,
        """            aliases = load_aliases(alias_path)
            add_alias(aliases, rom, parent_slug)
            if resolved_rom:
                add_alias(aliases, resolved_rom, parent_slug)
            save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
""",
        """            aliases = load_aliases(alias_path)
            merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, parent_slug), overwrite=True)
            merge_aliases(aliases, arcade_exact_entry_aliases(parent_gamelist_entry, parent_slug), overwrite=True)
            add_alias(aliases, rom, parent_slug)
            if resolved_rom:
                add_alias(aliases, resolved_rom, parent_slug)
            save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
""",
        "parent aliases",
    )

    text = replace_once(
        text,
        """    aliases = load_aliases(alias_path)
    official_aliases = build_retrobatofficial_aliases(data, args.system, slug, args.gamelist_dir)
    merge_aliases(aliases, official_aliases, overwrite=True)
    add_alias(aliases, rom, slug)
    if resolved_rom:
        add_alias(aliases, resolved_rom, slug)
    save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
""",
        """    aliases = load_aliases(alias_path)
    merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, slug), overwrite=True)
    official_aliases = build_retrobatofficial_aliases(data, args.system, slug, args.gamelist_dir)
    merge_aliases(aliases, official_aliases, overwrite=True)
    add_alias(aliases, rom, slug)
    if resolved_rom:
        add_alias(aliases, resolved_rom, slug)
    save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
""",
        "final aliases",
    )

    TARGET.write_text(text, encoding="utf-8")
    print(f"patched {TARGET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
