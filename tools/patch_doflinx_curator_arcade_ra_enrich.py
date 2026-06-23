from pathlib import Path

path = Path(r"C:\Users\vince\Downloads\DOFLinx_V909\API\FINAL\ag_mass_curator_local_v2_all_systems.py")
text = path.read_text(encoding="utf-8")

old = '''def parse_lua_entry_attrs(text):
    attrs = {}
    for key, value in re.findall(r"(\\w+)\\s*=\\s*(\\"[^\\"]*\\"|'[^']*'|[^,\\s}]+)", text or ""):
        value = value.strip().strip('"').strip("'")
        attrs[key] = value
    return attrs


def color_hint_from_text(text):
'''
new = '''def parse_lua_entry_attrs(text):
    attrs = {}
    for key, value in re.findall(r"(\\w+)\\s*=\\s*(\\"[^\\"]*\\"|'[^']*'|[^,\\s}]+)", text or ""):
        value = value.strip().strip('"').strip("'")
        attrs[key] = value
    return attrs


def parse_lua_bool(value):
    return str(value or "").strip().lower() == "true"


def parse_generated_lua_mem_events(text):
    events = []
    for item in re.findall(r"\\{([^{}]*address\\s*=[^{}]*)\\}", text or ""):
        attrs = parse_lua_entry_attrs(item)
        if not attrs.get("address"):
            continue
        ev = event(
            attrs.get("address"),
            attrs.get("type") or "u8",
            attrs.get("condition") or "change",
            attrs.get("action") or "UNKNOWN",
            attrs.get("desc") or attrs.get("action") or "Imported MEM event",
            value=attrs.get("value") or "",
            mask=attrs.get("mask") or "",
            bit=parse_decimal_int(attrs.get("bit")) if attrs.get("bit") not in ("", None) else None,
            min_value=attrs.get("min"),
            max_value=attrs.get("max"),
            color=attrs.get("color") or "",
            no_log=parse_lua_bool(attrs.get("no_log")),
            no_survey=parse_lua_bool(attrs.get("no_survey")),
        )
        if ev:
            events.append(ev)
    return events


def color_hint_from_text(text):
'''
if old not in text:
    raise SystemExit("parse_lua_entry_attrs block not found")
text = text.replace(old, new)

old = '''def dedupe_events(events):
    out = []
    seen = set()
    for ev in events:
        if not ev:
            continue
        sig = (
            ev["address"],
            ev["type"],
            ev["condition"],
            ev.get("value") or "",
            ev.get("mask") or "",
            ev["action"],
            ev.get("min", ""),
            ev.get("max", ""),
            ev.get("color", ""),
        )
        if sig in seen:
            continue
        seen.add(sig)
        out.append(ev)
    return out


def event_to_lua(ev):
'''
new = '''def dedupe_events(events):
    out = []
    seen = set()
    for ev in events:
        if not ev:
            continue
        sig = (
            ev["address"],
            ev["type"],
            ev["condition"],
            ev.get("value") or "",
            ev.get("mask") or "",
            ev["action"],
            ev.get("min", ""),
            ev.get("max", ""),
            ev.get("color", ""),
        )
        if sig in seen:
            continue
        seen.add(sig)
        out.append(ev)
    return out


def event_priority_key(ev):
    key = (
        ev.get("address") or "",
        ev.get("type") or "",
        ev.get("condition") or "",
        ev.get("value") or "",
        ev.get("mask") or "",
        ev.get("bit") if ev.get("bit") is not None else "",
        ev.get("action") or "",
    )
    if ev.get("min") not in ("", None) or ev.get("max") not in ("", None):
        key += (ev.get("min", ""), ev.get("max", ""))
    return key


def merge_priority_events(primary_events, secondary_events):
    out = []
    seen = set()
    for ev in dedupe_events(primary_events):
        key = event_priority_key(ev)
        if key in seen:
            continue
        seen.add(key)
        out.append(ev)
    for ev in dedupe_events(secondary_events):
        key = event_priority_key(ev)
        if key in seen:
            continue
        seen.add(key)
        out.append(ev)
    return out


def event_to_lua(ev):
'''
if old not in text:
    raise SystemExit("dedupe_events block not found")
text = text.replace(old, new)

old = '''    if os.path.exists(mem_path) and getattr(args, "preserve_existing_mem", False):
        print(f"   [skip] {slug}.MEM existe deja (preserve existing MEM)")
        return mem_path
    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path
'''
new = '''    existing_events = []
    if os.path.exists(mem_path) and getattr(args, "preserve_existing_mem", False):
        existing_events = parse_generated_lua_mem_events(load_text(mem_path))
        print(f"   [i] existing DOFLinx MEM events: {len(existing_events)}")
    elif os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path
'''
if old not in text:
    raise SystemExit("preserve_existing_mem skip block not found")
text = text.replace(old, new)

old = '''    events = dedupe_events(all_events)
    score_delta_events, score_delta_report = arcade_score_delta_events(data, slug, events, args, pre_context)
    if score_delta_events:
        print(f"   [i] arcade score delta events: {len(score_delta_events)}")
        events = dedupe_events(events + score_delta_events)
    write_arcade_score_delta_report(logs_dir, slug, score_delta_report)
'''
new = '''    ra_events = dedupe_events(all_events)
    score_delta_events, score_delta_report = arcade_score_delta_events(data, slug, ra_events, args, pre_context)
    if score_delta_events:
        print(f"   [i] arcade score delta events: {len(score_delta_events)}")
        ra_events = dedupe_events(ra_events + score_delta_events)
    if existing_events:
        events = merge_priority_events(existing_events, ra_events)
        print(
            f"   [i] enriched existing MEM: doflinx={len(existing_events)} "
            f"ra={len(ra_events)} merged={len(events)}"
        )
    else:
        events = ra_events
    write_arcade_score_delta_report(logs_dir, slug, score_delta_report)
'''
if old not in text:
    raise SystemExit("process_game events block not found")
text = text.replace(old, new)

old = '''                args.preserve_existing_mem = True
                print("[i] arcade RA pass: fill missing MEM files, preserve DOFLinx MEM files")
'''
new = '''                args.preserve_existing_mem = True
                print("[i] arcade RA pass: enrich DOFLinx MEM files and fill missing MEM files")
'''
if old not in text:
    raise SystemExit("arcade RA pass log block not found")
text = text.replace(old, new)

path.write_text(text, encoding="utf-8")
