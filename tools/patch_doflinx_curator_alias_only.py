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
        """    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path

    text, resolved_rom, resolved_path = load_doflinx_mem_text(rom, args.source_base)
""",
        """    text, resolved_rom, resolved_path = load_doflinx_mem_text(rom, args.source_base)

    if getattr(args, "alias_only", False):
        aliases = load_aliases(alias_path)
        parent_rom = arcade_parent_rom_with_score(rom, args.source_base, args.system, args.gamelist_dir)
        if parent_rom:
            parent_raw_slug = slugify_title(parent_rom)
            _parent_gamelist_path, _parent_gamelist_entries, parent_gamelist_entry = find_arcade_gamelist_entry_by_rom(
                args.system, parent_rom, args.gamelist_dir
            )
            target_slug = arcade_canonical_slug_from_entry(parent_gamelist_entry, parent_raw_slug)
            merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, target_slug), overwrite=True)
            merge_aliases(aliases, arcade_exact_entry_aliases(parent_gamelist_entry, target_slug), overwrite=True)
        else:
            target_slug = slug
            merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, target_slug), overwrite=True)
        add_alias(aliases, rom, target_slug)
        if resolved_rom:
            add_alias(aliases, resolved_rom, target_slug)
        save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
        print(f"   [+] alias-only {rom} -> {target_slug}")
        print(f"   [+] wrote {alias_path}")
        return alias_path

    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path
""",
        "arcade doflinx alias-only",
    )

    text = replace_once(
        text,
        """    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path

    prompt_lines = collect_prompt_notes(data, system)
""",
        """    if getattr(args, "alias_only", False):
        aliases = load_aliases(alias_path)
        add_alias(aliases, slug, slug)
        merge_aliases(aliases, build_retrobatofficial_aliases(data, system, slug, args.gamelist_dir), overwrite=True)
        merge_aliases(
            aliases,
            build_aliases(data, slug, include_hash_aliases=not source_slug_is_variant(slug, data)),
            overwrite=False,
        )
        save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\\n")
        print(f"   [+] alias-only {slug}")
        print(f"   [+] wrote {alias_path}")
        return alias_path

    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path

    prompt_lines = collect_prompt_notes(data, system)
""",
        "ra alias-only",
    )

    text = replace_once(
        text,
        """    if args.system == "arcade":
        doflinx_files = find_arcade_doflinx_files(args.source_base, args.game)
        if doflinx_files:
            print(f"[+] arcade DOFLinx files: {len(doflinx_files)}")
            for idx, doflinx_file in enumerate(doflinx_files, start=1):
                print(f"[{idx}/{len(doflinx_files)}] {os.path.basename(doflinx_file)}")
                try:
                    process_arcade_doflinx_game(args, doflinx_file)
                except Exception as exc:
                    print(f"   [error] {exc}")
                    if len(doflinx_files) == 1:
                        raise
            print(f"--- done {args.system} ---")
            return 0
""",
        """    if args.system == "arcade":
        doflinx_files = find_arcade_doflinx_files(args.source_base, args.game)
        if doflinx_files:
            print(f"[+] arcade DOFLinx files: {len(doflinx_files)}")
            for idx, doflinx_file in enumerate(doflinx_files, start=1):
                print(f"[{idx}/{len(doflinx_files)}] {os.path.basename(doflinx_file)}")
                try:
                    process_arcade_doflinx_game(args, doflinx_file)
                except Exception as exc:
                    print(f"   [error] {exc}")
                    if len(doflinx_files) == 1:
                        raise
            if not getattr(args, "alias_only", False):
                print(f"--- done {args.system} ---")
                return 0
""",
        "arcade doflinx return",
    )

    text = replace_once(
        text,
        """    parser.add_argument("--no-llm", action="store_true", help="Use deterministic RA extraction only")
""",
        """    parser.add_argument("--no-llm", action="store_true", help="Use deterministic RA extraction only")
    parser.add_argument("--alias-only", action="store_true", help="Only rebuild alias.json files; do not generate MEM files")
""",
        "parser alias-only",
    )

    text = replace_once(
        text,
        """    if args.no_llm:
        print("LLM:         disabled")
    else:
        print(f"LLM:         {args.api_type} {args.server_url} / {args.model}")
""",
        """    if args.alias_only:
        args.no_llm = True
        print("Mode:        alias-only")
    if args.no_llm:
        print("LLM:         disabled")
    else:
        print(f"LLM:         {args.api_type} {args.server_url} / {args.model}")
""",
        "main alias-only print",
    )

    TARGET.write_text(text, encoding="utf-8")
    print(f"patched {TARGET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
