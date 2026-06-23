import sys, select, time
from machine import Pin, PWM

try:
    import neopixel
except Exception:
    neopixel = None

try:
    from profiles_db import PROFILES_LIBRARY
except Exception:
    PROFILES_LIBRARY = {}

# ============================================================
# SAFE BOOT
# ============================================================
# GP25 est gardé pour la LED interne heartbeat.
SAFE_GPIOS = list(range(0, 23)) + [26, 27, 28]
PWM_FREQ = 1000
DEFAULT_ACTIVE_LOW = True

for gp in SAFE_GPIOS:
    try:
        Pin(gp, Pin.OUT).value(1)
    except Exception:
        pass

time.sleep_ms(100)

# ============================================================
# COULEURS GPIO DIRECT
# Valeurs en pourcentage d'extinction canal par canal.
# 0 = canal pleinement allumé, 100 = canal éteint.
# Cette table conserve la logique validée avec les boutons SJ@JX.
# ============================================================

PRIMARY = {
    "WHITE":  (0, 0, 0),
    "PINK":   (100, 0, 0),
    "CYAN":   (0, 100, 0),
    "YELLOW": (0, 0, 100),
    "BLUE":   (100, 100, 0),
    "RED":    (100, 0, 100),
    "GREEN":  (0, 100, 100),
    "BLACK":  (100, 100, 100),
}

SHADES = {
    "ORANGE": (50, 0, 100),
    "LIME": (25, 100, 100),
    "VIOLET": (100, 75, 0),
    "PURPLE": (75, 25, 0),
    "GRAY": (50, 50, 50),
    "GREY": (50, 50, 50),
    "GOLD": (75, 25, 100),
    "TURQUOISE": (25, 100, 0),
    "AQUA": (50, 100, 0),
    "TEAL": (25, 75, 0),
    "MAGENTA": (100, 50, 0),
    "LEMON": (0, 25, 100),
}

FALLBACK = {
    "ORANGE": "RED",
    "GOLD": "YELLOW",
    "LIME": "GREEN",
    "VIOLET": "BLUE",
    "PURPLE": "BLUE",
    "GRAY": "BLACK",
    "GREY": "BLACK",
    "TURQUOISE": "CYAN",
    "AQUA": "CYAN",
    "TEAL": "CYAN",
    "MAGENTA": "PINK",
    "LEMON": "YELLOW",
}

COLOR_MAP = {}
COLOR_MAP.update(PRIMARY)
COLOR_MAP.update(SHADES)

# Table standard pour LEDs adressables. Ici les valeurs sont de vrais RGB.
ADDR_RGB = {
    "BLACK": (0, 0, 0),
    "WHITE": (255, 255, 255),
    "RED": (255, 0, 0),
    "GREEN": (0, 255, 0),
    "BLUE": (0, 0, 255),
    "CYAN": (0, 255, 255),
    "YELLOW": (255, 255, 0),
    "PINK": (255, 0, 160),
    "MAGENTA": (255, 0, 255),
    "ORANGE": (255, 80, 0),
    "LIME": (120, 255, 0),
    "VIOLET": (120, 0, 255),
    "PURPLE": (160, 0, 255),
    "GRAY": (80, 80, 80),
    "GREY": (80, 80, 80),
    "GOLD": (255, 180, 0),
    "TURQUOISE": (0, 220, 180),
    "AQUA": (0, 180, 255),
    "TEAL": (0, 120, 120),
    "LEMON": (220, 255, 0),
}

ON_WORDS = ("ON", "1", "TRUE", "YES", "WHITE")
OFF_WORDS = ("OFF", "0", "FALSE", "NO", "BLACK")

# ============================================================
# ETAT DYNAMIQUE DU PANEL
# ============================================================
# outputs[name] = {
#   "kind": "BUTTON" | "START" | "SELECT" | "JOY" | "STRIP" | "CIRCLE" | "AUX",
#   "mode": "RGB" | "ONOFF" | "ADDR",
#   "slot": "1" | "2" | "START" | "JOY1" | ...,
#   "pins": [0,1,2] ou [27] pour RGB/ONOFF,
#   "bus": "PANEL" et "indices": [0,1,2] pour ADDR,
#   "active_low": True,
# }
#
# led_buses[name] = {
#   "driver": neopixel.NeoPixel,
#   "pin": 28,
#   "count": 60,
#   "brightness": 1.0,
# }

outputs = {}
slot_to_output = {}
current = {}

led_buses = {}
addr_owner = {}  # (bus, index) -> output name

pwm_objects = {}
gpio_owner = {}
conflicts = {}
config_committed = False

poll = select.poll()
poll.register(sys.stdin, select.POLLIN)

heartbeat = Pin(25, Pin.OUT)
last_hb = time.ticks_ms()

# ============================================================
# OUTILS GENERAUX
# ============================================================

def normalize_token(value):
    if value is None:
        return ""
    return str(value).strip().upper()


def normalize_color(color):
    color = normalize_token(color)
    if not color:
        return "BLACK"
    if color in ON_WORDS:
        return "WHITE"
    if color in OFF_WORDS:
        return "BLACK"
    return color


def parse_pins(tokens):
    pins = []
    for token in tokens:
        token = token.replace(",", " ")
        for part in token.split():
            try:
                pins.append(int(part))
            except Exception:
                pass
    return pins


def parse_float(value, default=1.0):
    try:
        return float(value)
    except Exception:
        return default


def scale_rgb(rgb, brightness):
    try:
        b = float(brightness)
    except Exception:
        b = 1.0
    if b < 0:
        b = 0
    if b > 1:
        b = 1
    return (int(rgb[0] * b), int(rgb[1] * b), int(rgb[2] * b))


def addr_rgb_for_color(color):
    color = normalize_color(color)
    if color not in ADDR_RGB:
        color = "BLACK"
    return color, ADDR_RGB[color]

# ============================================================
# GPIO / PWM
# ============================================================

def duty(percent):
    return int((int(percent) / 100) * 65535)


def deinit_pwm(gp):
    if gp in pwm_objects:
        try:
            pwm_objects[gp].deinit()
        except Exception:
            pass
        del pwm_objects[gp]


def write_gpio(gp, percent, active_low=True):
    percent = int(percent)

    if percent == 0 or percent == 100:
        deinit_pwm(gp)
        if active_low:
            Pin(gp, Pin.OUT).value(1 if percent == 100 else 0)
        else:
            Pin(gp, Pin.OUT).value(0 if percent == 100 else 1)
    else:
        # PWM prévu pour montage actif bas actuel.
        # Avec active_low False, les nuances sont volontairement simplifiées.
        if not active_low:
            percent = 0 if percent < 50 else 100
            Pin(gp, Pin.OUT).value(0 if percent == 100 else 1)
            return

        if gp not in pwm_objects:
            pwm = PWM(Pin(gp))
            pwm.freq(PWM_FREQ)
            pwm_objects[gp] = pwm
        pwm_objects[gp].duty_u16(duty(percent))


def off_pin(gp, active_low=True):
    deinit_pwm(gp)
    Pin(gp, Pin.OUT).value(1 if active_low else 0)


def pwm_key(gp):
    return gp % 16


def is_primary(values):
    return all(v in (0, 100) for v in values)


def rebuild_conflicts():
    global gpio_owner, conflicts
    gpio_owner = {}
    conflicts = {}

    for name, out in outputs.items():
        if out.get("mode") == "ADDR":
            continue
        for idx, gp in enumerate(out.get("pins", [])):
            gpio_owner[gp] = (name, idx)

    for gp in gpio_owner:
        conflicts[gp] = []
        for other_gp in gpio_owner:
            if other_gp != gp and pwm_key(other_gp) == pwm_key(gp):
                conflicts[gp].append(other_gp)


def pwm_is_safe(name, values):
    name = normalize_token(name)
    out = outputs.get(name)
    if not out:
        return False
    if out.get("mode") == "ADDR":
        return True

    pins = out.get("pins", [])
    for idx, percent in enumerate(values):
        if percent in (0, 100):
            continue
        if idx >= len(pins):
            return False
        gp = pins[idx]
        for other_gp in conflicts.get(gp, []):
            other_name, _ = gpio_owner[other_gp]
            if other_name != name and current.get(other_name) != "BLACK":
                return False
    return True

# ============================================================
# LEDs ADRESSABLES / BUSES
# ============================================================

def parse_index_range(spec):
    # Accepte "0", "0-7", "0,1,2", "0-3,8,10-12".
    result = []
    spec = str(spec).replace(";", ",")
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            try:
                start = int(a)
                end = int(b)
                if end >= start:
                    result += list(range(start, end + 1))
                else:
                    result += list(range(start, end - 1, -1))
            except Exception:
                pass
        else:
            try:
                result.append(int(part))
            except Exception:
                pass
    return result


def parse_addr_spec(tokens):
    # Syntaxe recommandée : PANEL:0-7 ou PANEL:0,1,2,3
    # Variante acceptée : PANEL 0-7
    if not tokens:
        return None, []

    first = tokens[0]
    if ":" in first:
        bus, rng = first.split(":", 1)
        return normalize_token(bus), parse_index_range(rng)

    if len(tokens) >= 2:
        return normalize_token(tokens[0]), parse_index_range(tokens[1])

    return None, []


def validate_bus_gpio(gp):
    if gp not in SAFE_GPIOS:
        return False, "UNSAFE_GPIO_{}".format(gp)
    if gp in used_gpios():
        return False, "GPIO_ALREADY_USED_{}".format(gp)
    return True, "OK"


def add_bus(name, bus_type, gp, count, brightness=1.0):
    name = normalize_token(name)
    bus_type = normalize_token(bus_type)

    if not name:
        print("ERR BUS NO_NAME")
        return
    if name in led_buses:
        print("ERR BUS EXISTS", name)
        return
    if bus_type in ("ADDR", "ADDRESSABLE", "WS2812", "WS2812B", "RGBLED", "LED"):
        bus_type = "NEOPIXEL"
    if bus_type != "NEOPIXEL":
        print("ERR BUS TYPE", bus_type)
        return
    if neopixel is None:
        print("ERR BUS NEOPIXEL_MODULE_MISSING")
        return

    try:
        gp = int(gp)
        count = int(count)
    except Exception:
        print("ERR BUS SYNTAX")
        return

    if count <= 0:
        print("ERR BUS COUNT", count)
        return

    ok, msg = validate_bus_gpio(gp)
    if not ok:
        print("ERR BUS", msg)
        return

    b = parse_float(brightness, 1.0)
    if b < 0:
        b = 0
    if b > 1:
        b = 1

    try:
        np = neopixel.NeoPixel(Pin(gp), count)
        for i in range(count):
            np[i] = (0, 0, 0)
        np.write()
    except Exception as e:
        print("ERR BUS INIT", name, e)
        return

    led_buses[name] = {
        "type": bus_type,
        "pin": gp,
        "count": count,
        "brightness": b,
        "driver": np,
    }

    print("BUS", name, bus_type, "GP", gp, "COUNT", count, "BRIGHTNESS", b)


def clear_bus(name):
    name = normalize_token(name)
    bus = led_buses.get(name)
    if not bus:
        return
    np = bus.get("driver")
    try:
        for i in range(bus.get("count", 0)):
            np[i] = (0, 0, 0)
        np.write()
    except Exception:
        pass


def clear_all_buses():
    for name in list(led_buses.keys()):
        clear_bus(name)


def set_addr_indices(bus_name, indices, rgb):
    bus_name = normalize_token(bus_name)
    bus = led_buses.get(bus_name)
    if not bus:
        return False

    np = bus.get("driver")
    count = bus.get("count", 0)
    rgb = scale_rgb(rgb, bus.get("brightness", 1.0))

    try:
        for idx in indices:
            if 0 <= idx < count:
                np[idx] = rgb
        np.write()
        return True
    except Exception:
        return False

# ============================================================
# CONFIG DYNAMIQUE / POINTEURS
# ============================================================

def used_gpios():
    pins = []
    for bus in led_buses.values():
        pins.append(bus.get("pin"))
    for out in outputs.values():
        if out.get("mode") != "ADDR":
            pins += out.get("pins", [])
    return sorted([p for p in pins if p is not None])


def used_gpio_count():
    return len(used_gpios())


def free_gpios():
    used = used_gpios()
    return [gp for gp in SAFE_GPIOS if gp not in used]


def reset_config():
    global outputs, slot_to_output, current, led_buses, addr_owner, config_committed

    clear_all_outputs()
    clear_all_buses()

    outputs = {}
    slot_to_output = {}
    current = {}
    led_buses = {}
    addr_owner = {}
    config_committed = False
    rebuild_conflicts()
    print("INIT RESET")


def validate_gpio_list(pins):
    if not pins:
        return False, "NO_PINS"
    for gp in pins:
        if gp not in SAFE_GPIOS:
            return False, "UNSAFE_GPIO_{}".format(gp)
    for gp in pins:
        if gp in used_gpios():
            return False, "GPIO_ALREADY_USED_{}".format(gp)
    return True, "OK"


def normalize_mode(mode):
    mode = normalize_token(mode)
    if mode in ("SIMPLE", "MONO", "ON", "OFF", "LED", "ON/OFF"):
        return "ONOFF"
    if mode in ("RGBGPIO", "GPIO_RGB"):
        return "RGB"
    if mode in ("ADDR", "ADDRESSABLE", "NEOPIXEL", "WS2812", "WS2812B", "RING", "CIRCLE"):
        return "ADDR"
    return mode


def add_gpio_pointer(name, kind, mode, slot, pins, active_low=True):
    name = normalize_token(name)
    kind = normalize_token(kind)
    mode = normalize_mode(mode)
    slot = normalize_token(slot)

    if not name:
        print("ERR PTR NO_NAME")
        return
    if name in outputs:
        print("ERR PTR EXISTS", name)
        return
    if mode not in ("RGB", "ONOFF"):
        print("ERR PTR MODE", mode)
        return

    expected = 3 if mode == "RGB" else 1
    if len(pins) != expected:
        print("ERR PTR PIN_COUNT", name, mode, len(pins), "EXPECTED", expected)
        return

    ok, msg = validate_gpio_list(pins)
    if not ok:
        print("ERR PTR", msg)
        return

    outputs[name] = {
        "kind": kind,
        "mode": mode,
        "slot": slot,
        "pins": pins,
        "active_low": active_low,
    }
    slot_to_output[slot] = name
    current[name] = "BLACK"

    for gp in pins:
        off_pin(gp, active_low)

    rebuild_conflicts()
    print("PTR", name, kind, mode, slot, ",".join([str(gp) for gp in pins]))


def add_addr_pointer(name, kind, slot, bus_name, indices, shared=False):
    name = normalize_token(name)
    kind = normalize_token(kind)
    slot = normalize_token(slot)
    bus_name = normalize_token(bus_name)

    if not name:
        print("ERR PTR NO_NAME")
        return
    if name in outputs:
        print("ERR PTR EXISTS", name)
        return
    if bus_name not in led_buses:
        print("ERR PTR BUS", bus_name)
        return
    if not indices:
        print("ERR PTR NO_INDICES", name)
        return

    bus = led_buses[bus_name]
    count = bus.get("count", 0)
    clean_indices = []
    for idx in indices:
        if idx < 0 or idx >= count:
            print("ERR PTR INDEX", idx, "COUNT", count)
            return
        if idx not in clean_indices:
            clean_indices.append(idx)

    if not shared:
        for idx in clean_indices:
            owner = addr_owner.get((bus_name, idx))
            if owner and owner != name:
                print("ERR PTR ADDR_ALREADY_USED", bus_name, idx, owner)
                return

    for idx in clean_indices:
        addr_owner[(bus_name, idx)] = name

    outputs[name] = {
        "kind": kind,
        "mode": "ADDR",
        "slot": slot,
        "bus": bus_name,
        "indices": clean_indices,
    }
    slot_to_output[slot] = name
    current[name] = "BLACK"

    set_addr_indices(bus_name, clean_indices, (0, 0, 0))
    print("PTR", name, kind, "ADDR", slot, bus_name + ":" + ",".join([str(i) for i in clean_indices]))


def commit_config():
    global config_committed
    config_committed = True
    clear_all_outputs()
    print("CONFIG OK", len(outputs), "OUTPUTS", len(led_buses), "BUSES", used_gpio_count(), "GPIO")

# ============================================================
# PILOTAGE DES SORTIES
# ============================================================

def output_values_for_color(name, color, force=False):
    out = outputs.get(name)
    if not out:
        return "BLACK", None

    color = normalize_color(color)

    if out["mode"] == "ONOFF":
        if color in OFF_WORDS or color == "BLACK":
            return "BLACK", (100,)
        return "WHITE", (0,)

    if out["mode"] == "ADDR":
        return addr_rgb_for_color(color)

    if color not in COLOR_MAP:
        color = "BLACK"

    values = COLOR_MAP[color]
    if not force and not is_primary(values) and not pwm_is_safe(name, values):
        color = FALLBACK.get(color, "BLACK")
        values = COLOR_MAP[color]

    return color, values


def apply_values(name, values):
    out = outputs.get(name)
    if not out:
        return

    if out.get("mode") == "ADDR":
        set_addr_indices(out.get("bus"), out.get("indices", []), values)
        return

    active_low = out.get("active_low", DEFAULT_ACTIVE_LOW)
    for gp, percent in zip(out.get("pins", []), values):
        write_gpio(gp, percent, active_low)


def set_output(name, color, force=False):
    name = normalize_token(name)
    if name not in outputs:
        print("ERR OUTPUT", name)
        return

    color, values = output_values_for_color(name, color, force)
    if values is None:
        print("ERR COLOR", color)
        return

    apply_values(name, values)
    current[name] = color


def set_slot(slot, color, force=False):
    slot = normalize_token(slot)
    if slot not in slot_to_output:
        print("ERR SLOT", slot)
        return
    set_output(slot_to_output[slot], color, force)


def clear_all_outputs():
    # On coupe d'abord les GPIO déclarés.
    for name in list(outputs.keys()):
        try:
            out = outputs.get(name)
            if out and out.get("mode") != "ADDR":
                color, values = output_values_for_color(name, "BLACK", True)
                apply_values(name, values)
                current[name] = "BLACK"
        except Exception:
            pass

    for gp in list(pwm_objects.keys()):
        deinit_pwm(gp)

    # Pour les adressables, CLEAR éteint toute la chaîne pour éviter les restes visuels.
    clear_all_buses()
    for name, out in outputs.items():
        if out.get("mode") == "ADDR":
            current[name] = "BLACK"


def all_color(color):
    color = normalize_color(color)
    if color in SHADES:
        # Sur GPIO direct, ALL évite les conflits PWM.
        # Sur adressable ce fallback n'est pas nécessaire, mais on garde un rendu homogène.
        pass

    for name in outputs:
        set_output(name, color, force=True)

# ============================================================
# PROFILS
# ============================================================

def apply_profile(profile_name, force_pwm=True):
    profile_name = normalize_token(profile_name)

    if profile_name not in PROFILES_LIBRARY:
        print("ERR PROFILE", profile_name)
        return

    clear_all_outputs()

    profile = PROFILES_LIBRARY[profile_name]
    slots = profile.get("slots", {})

    for slot, data in slots.items():
        slot_key = normalize_token(slot)
        if slot_key in slot_to_output:
            color = data.get("color", "BLACK")
            set_slot(slot_key, color, force=force_pwm)

    print("PANEL", profile_name)

# ============================================================
# DIAGNOSTIC
# ============================================================

def scan():
    print("DRIVER DYNAMIC_PANEL_ADDR")
    print("CONFIG", "COMMITTED" if config_committed else "UNCOMMITTED")
    print("BUSES", ",".join(sorted(led_buses.keys())) if led_buses else "NONE")
    print("OUTPUTS", ",".join(sorted(outputs.keys())) if outputs else "NONE")
    print("SLOTS", ",".join(sorted(slot_to_output.keys())) if slot_to_output else "NONE")
    print("COLORS", ",".join(sorted(ADDR_RGB.keys())))
    print("PROFILES", len(PROFILES_LIBRARY))


def get_state():
    if not outputs:
        print("STATE EMPTY")
        return

    for name in sorted(outputs.keys()):
        out = outputs[name]
        if out["mode"] == "ADDR":
            print("OUT", name, "KIND", out["kind"], "MODE", out["mode"], "SLOT", out["slot"], "BUS", out["bus"], "INDICES", ",".join([str(i) for i in out["indices"]]), "COLOR", current.get(name, "BLACK"))
        else:
            print("OUT", name, "KIND", out["kind"], "MODE", out["mode"], "SLOT", out["slot"], "PINS", ",".join([str(gp) for gp in out["pins"]]), "COLOR", current.get(name, "BLACK"))


def gpio_state():
    print("GPIO_USED", ",".join([str(gp) for gp in used_gpios()]) if used_gpios() else "NONE")
    print("GPIO_FREE", ",".join([str(gp) for gp in free_gpios()]) if free_gpios() else "NONE")
    print("GPIO_COUNT", used_gpio_count(), "/", len(SAFE_GPIOS))


def list_buses():
    if not led_buses:
        print("BUSLIST NONE")
        return
    for name in sorted(led_buses.keys()):
        bus = led_buses[name]
        print("BUS", name, bus["type"], "GP", bus["pin"], "COUNT", bus["count"], "BRIGHTNESS", bus["brightness"])


def list_panels():
    print("PANELS", ",".join(sorted(PROFILES_LIBRARY.keys())))


def demo_outputs(delay_ms=600):
    for name in sorted(outputs.keys()):
        clear_all_outputs()
        print("DEMO", name)
        set_output(name, "WHITE", force=True)
        time.sleep_ms(delay_ms)
    clear_all_outputs()
    print("DEMOOUTPUTS DONE")

# ============================================================
# COMMANDES
# ============================================================
# PING
# SCAN
# GET
# GPIO
# INIT / RESETCFG
# BUS <name> NEOPIXEL <gpio> <count> [brightness]
# PTR <name> <kind> <mode> <slot> <pins...|bus:range> [HIGH|LOW|SHARED]
# COMMIT
# CLEAR
# ALL RED
# SET B1 RED
# SLOT 4 BLUE
# START ON
# SELECT OFF
# JOY1 GREEN
# PROFILE NEOGEO_MINI
# PANELS
# DEMOOUTPUTS 400
# BATCH B1 RED;B2 BLUE;START ON;JOY1 GREEN


def handle_bus(args):
    if len(args) < 5:
        print("ERR BUS SYNTAX")
        return
    name = args[1]
    bus_type = args[2]
    gp = args[3]
    count = args[4]
    brightness = args[5] if len(args) >= 6 else 1.0
    add_bus(name, bus_type, gp, count, brightness)


def handle_pointer(args):
    if len(args) < 6:
        print("ERR PTR SYNTAX")
        return

    name = args[1]
    kind = args[2]
    mode = normalize_mode(args[3])
    slot = args[4]
    tail = args[5:]

    active_low = DEFAULT_ACTIVE_LOW
    shared = False

    # Options finales : HIGH / LOW / SHARED.
    changed = True
    while tail and changed:
        changed = False
        last = normalize_token(tail[-1])
        if last in ("HIGH", "ACTIVE_HIGH"):
            active_low = False
            tail = tail[:-1]
            changed = True
        elif last in ("LOW", "ACTIVE_LOW"):
            active_low = True
            tail = tail[:-1]
            changed = True
        elif last == "SHARED":
            shared = True
            tail = tail[:-1]
            changed = True

    if mode == "ADDR":
        bus_name, indices = parse_addr_spec(tail)
        add_addr_pointer(name, kind, slot, bus_name, indices, shared)
        return

    pins = parse_pins(tail)
    add_gpio_pointer(name, kind, mode, slot, pins, active_low)


def handle(line):
    line = line.strip()
    if not line:
        return

    args = line.split()
    cmd = normalize_token(args[0])

    if cmd == "PING":
        print("PONG")
        return

    if cmd == "SCAN":
        scan()
        return

    if cmd == "GET":
        get_state()
        return

    if cmd == "GPIO":
        gpio_state()
        return

    if cmd in ("BUSES", "BUSLIST"):
        list_buses()
        return

    if cmd in ("INIT", "RESETCFG"):
        reset_config()
        return

    if cmd == "BUS":
        handle_bus(args)
        return

    if cmd in ("PTR", "MAP"):
        handle_pointer(args)
        return

    if cmd == "COMMIT":
        commit_config()
        return

    if cmd == "PANELS":
        list_panels()
        return

    if cmd == "CLEAR":
        clear_all_outputs()
        return

    if cmd == "ALL":
        if len(args) >= 2:
            all_color(args[1])
        return

    if cmd == "SET":
        if len(args) >= 3:
            set_output(args[1], args[2], force=False)
        return

    if cmd == "SETPWM":
        if len(args) >= 3:
            set_output(args[1], args[2], force=True)
        return

    if cmd == "SLOT":
        if len(args) >= 3:
            set_slot(args[1], args[2], force=False)
        return

    if cmd == "SLOTPWM":
        if len(args) >= 3:
            set_slot(args[1], args[2], force=True)
        return

    if cmd in ("PANEL", "PROFILE"):
        if len(args) >= 2:
            apply_profile(args[1], force_pwm=True)
        return

    if cmd == "DEMOOUTPUTS":
        delay = 600
        if len(args) >= 2:
            try:
                delay = int(args[1])
            except Exception:
                pass
        demo_outputs(delay)
        return

    if cmd == "BATCH":
        payload = line.split(" ", 1)[1] if " " in line else ""
        for item in payload.split(";"):
            vals = item.strip().split()
            if len(vals) == 2:
                set_output(vals[0], vals[1], force=False)
        return

    # Raccourci : START ON / SELECT OFF / B1 RED / JOY1 GREEN
    if cmd in outputs and len(args) >= 2:
        set_output(cmd, args[1], force=False)
        return

    # Raccourci par slot : 1 RED / 4 BLUE / START ON si START est un slot.
    if cmd in slot_to_output and len(args) >= 2:
        set_slot(cmd, args[1], force=False)
        return

    print("ERR CMD", cmd)

# ============================================================
# START
# ============================================================

print("READY DYNAMIC PANEL ADDRESSABLE DRIVER")
scan()

while True:
    if poll.poll(0):
        handle(sys.stdin.readline())

    now = time.ticks_ms()
    if time.ticks_diff(now, last_hb) > 500:
        heartbeat.toggle()
        last_hb = now

    time.sleep_ms(1)
