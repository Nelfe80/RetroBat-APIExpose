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

    prompt_lines = collect_prompt_notes(data, system)
""",
        """    if os.path.exists(mem_path) and getattr(args, "preserve_existing_mem", False):
        print(f"   [skip] {slug}.MEM existe deja (preserve existing MEM)")
        return mem_path
    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path

    prompt_lines = collect_prompt_notes(data, system)
""",
        "process_game preserve existing mem",
    )

    text = replace_once(
        text,
        """            if not getattr(args, "alias_only", False):
                print(f"--- done {args.system} ---")
                return 0

    files = find_ra_files(args.source_base, args.system, args.game)
""",
        """            if not getattr(args, "alias_only", False):
                args.preserve_existing_mem = True
                print("[i] arcade RA pass: fill missing MEM files, preserve DOFLinx MEM files")

    files = find_ra_files(args.source_base, args.system, args.game)
""",
        "arcade continue to RA",
    )

    TARGET.write_text(text, encoding="utf-8")
    print(f"patched {TARGET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
