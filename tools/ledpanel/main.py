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

try:
    from hardware_profiles import get_init_commands, list_profiles as list_hardware_profiles, describe_profile as describe_hardware_profile
except Exception:
    get_init_commands = None
    list_hardware_profiles = None
    describe_profile = None

# ============================================================
# SAFE BOOT
# ============================================================
# GP25 est gardé pour la LED interne heartbeat.
SAFE_GPIOS = list(range(0, 23)) + [26, 27, 28]
PWM_FREQ = 1000
DEFAULT_ACTIVE_LOW = True

# Inversion logique des commandes ON/OFF simples.
# True signifie :
#   START ON  -> envoie le signal que le firmware utilisait avant pour OFF
#   START OFF -> envoie le signal que le firmware utilisait avant pour ON
# C'est le cas constaté sur le panel actuel.
ONOFF_COMMAND_INVERT = True

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
#   "order": "RGB" | "GRB" | ...
# }
#
# matrix_cfg[name] = {
#   "width": 8 | 16 | 32,
#   "height": 8 | 16 | 32,
#   "layout": "SERPENTINE" | "LINEAR",
# }

outputs = {}
slot_to_output = {}
current = {}

led_buses = {}
addr_owner = {}  # (bus, index) -> output name
matrix_cfg = {}

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


def apply_rgb_order(rgb, order):
    # Les commandes restent toujours en RGB logique.
    # Si un ruban/panneau affiche les couleurs inversées, on peut changer
    # l'ordre du bus avec BUSORDER PANEL GRB, BRG, etc.
    order = normalize_token(order or "RGB")
    if len(order) != 3:
        order = "RGB"
    values = {"R": int(rgb[0]), "G": int(rgb[1]), "B": int(rgb[2])}
    return tuple(values.get(ch, 0) for ch in order[:3])


def parse_rgb_value(value):
    token = str(value or "").strip().upper()
    if token in ADDR_RGB:
        return ADDR_RGB[token]
    if token in ON_WORDS:
        return ADDR_RGB["WHITE"]
    if token in OFF_WORDS:
        return ADDR_RGB["BLACK"]

    if token.startswith("#"):
        token = token[1:]

    if "," in token:
        try:
            parts = [int(x.strip()) for x in token.split(",")[:3]]
            while len(parts) < 3:
                parts.append(0)
            return (max(0, min(255, parts[0])), max(0, min(255, parts[1])), max(0, min(255, parts[2])))
        except Exception:
            return ADDR_RGB["BLACK"]

    if len(token) == 6:
        try:
            return (int(token[0:2], 16), int(token[2:4], 16), int(token[4:6], 16))
        except Exception:
            pass

    return ADDR_RGB["BLACK"]


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


def add_bus(name, bus_type, gp, count, brightness=1.0, order="RGB"):
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

    order = normalize_token(order or "RGB")
    if len(order) != 3:
        order = "RGB"

    led_buses[name] = {
        "type": bus_type,
        "pin": gp,
        "count": count,
        "brightness": b,
        "order": order,
        "driver": np,
    }

    print("BUS", name, bus_type, "GP", gp, "COUNT", count, "BRIGHTNESS", b, "ORDER", order)


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
    rgb = apply_rgb_order(rgb, bus.get("order", "RGB"))

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
    global outputs, slot_to_output, current, led_buses, addr_owner, matrix_cfg, config_committed

    clear_all_outputs()
    clear_all_buses()

    outputs = {}
    slot_to_output = {}
    current = {}
    led_buses = {}
    addr_owner = {}
    matrix_cfg = {}
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

    if kind == "MATRIX":
        register_matrix_output(name)

    print("PTR", name, kind, "ADDR", slot, bus_name + ":" + ",".join([str(i) for i in clean_indices]))


def infer_matrix_dimensions(pixel_count):
    if pixel_count == 64:
        return 8, 8
    if pixel_count == 256:
        return 16, 16
    if pixel_count == 1024:
        return 32, 32
    n = int(pixel_count)
    i = 1
    while i * i <= n:
        if i * i == n:
            return i, i
        i += 1
    return n, 1


def register_matrix_output(name, width=None, height=None, layout="SERPENTINE"):
    name = normalize_token(name)
    out = outputs.get(name)
    if not out or out.get("mode") != "ADDR":
        return
    count = len(out.get("indices", []))
    if width is None or height is None:
        width, height = infer_matrix_dimensions(count)
    matrix_cfg[name] = {
        "width": int(width),
        "height": int(height),
        "layout": normalize_token(layout or "SERPENTINE"),
    }


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
        requested_off = color in OFF_WORDS or color == "BLACK"

        # Ancienne logique :
        #   OFF -> 100
        #   ON  -> 0
        #
        # Logique inversée demandée :
        #   OFF -> 0
        #   ON  -> 100
        #
        # Le flag permet de revenir à l'ancien comportement si besoin.
        if ONOFF_COMMAND_INVERT:
            if requested_off:
                return "BLACK", (0,)
            return "WHITE", (100,)
        else:
            if requested_off:
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
    print("MATRICES", ",".join(sorted(matrix_cfg.keys())) if matrix_cfg else "NONE")
    print("COLORS", ",".join(sorted(ADDR_RGB.keys())))
    print("PROFILES", len(PROFILES_LIBRARY))
    if list_hardware_profiles:
        try:
            print("HWPROFILES", len(list_hardware_profiles()))
        except Exception:
            print("HWPROFILES ERR")
    else:
        print("HWPROFILES MISSING")


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
        print("BUS", name, bus["type"], "GP", bus["pin"], "COUNT", bus["count"], "BRIGHTNESS", bus["brightness"], "ORDER", bus.get("order", "RGB"))


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
# MATRICES WS2812B FLEXIBLES 8x8 / 16x16 / 32x32
# ============================================================

FONT_3X5 = {
    "0": ("111", "101", "101", "101", "111"),
    "1": ("010", "110", "010", "010", "111"),
    "2": ("111", "001", "111", "100", "111"),
    "3": ("111", "001", "111", "001", "111"),
    "4": ("101", "101", "111", "001", "001"),
    "5": ("111", "100", "111", "001", "111"),
    "6": ("111", "100", "111", "101", "111"),
    "7": ("111", "001", "010", "010", "010"),
    "8": ("111", "101", "111", "101", "111"),
    "9": ("111", "101", "111", "001", "111"),
    "A": ("111", "101", "111", "101", "101"),
    "B": ("110", "101", "110", "101", "110"),
    "C": ("111", "100", "100", "100", "111"),
    "D": ("110", "101", "101", "101", "110"),
    "E": ("111", "100", "110", "100", "111"),
    "F": ("111", "100", "110", "100", "100"),
    "G": ("111", "100", "101", "101", "111"),
    "H": ("101", "101", "111", "101", "101"),
    "I": ("111", "010", "010", "010", "111"),
    "J": ("001", "001", "001", "101", "111"),
    "K": ("101", "101", "110", "101", "101"),
    "L": ("100", "100", "100", "100", "111"),
    "M": ("101", "111", "111", "101", "101"),
    "N": ("101", "111", "111", "111", "101"),
    "O": ("111", "101", "101", "101", "111"),
    "P": ("111", "101", "111", "100", "100"),
    "Q": ("111", "101", "101", "111", "001"),
    "R": ("111", "101", "111", "110", "101"),
    "S": ("111", "100", "111", "001", "111"),
    "T": ("111", "010", "010", "010", "010"),
    "U": ("101", "101", "101", "101", "111"),
    "V": ("101", "101", "101", "101", "010"),
    "W": ("101", "101", "111", "111", "101"),
    "X": ("101", "101", "010", "101", "101"),
    "Y": ("101", "101", "010", "010", "010"),
    "Z": ("111", "001", "010", "100", "111"),
    "-": ("000", "000", "111", "000", "000"),
    ":": ("0", "1", "0", "1", "0"),
    ".": ("0", "0", "0", "0", "1"),
    " ": ("0", "0", "0", "0", "0"),
}


def first_matrix_name():
    if not matrix_cfg:
        return None
    return sorted(matrix_cfg.keys())[0]


def matrix_get(name):
    name = normalize_token(name)
    out = outputs.get(name)
    cfg = matrix_cfg.get(name)
    if not out or not cfg or out.get("mode") != "ADDR":
        return None, None, None, None
    bus = led_buses.get(out.get("bus"))
    if not bus:
        return None, None, None, None
    return name, out, cfg, bus


def matrix_pos(name, x, y):
    item = matrix_get(name)
    if item[0] is None:
        return None
    _, out, cfg, _ = item
    w = int(cfg.get("width", 0))
    h = int(cfg.get("height", 0))
    x = int(x)
    y = int(y)
    if x < 0 or y < 0 or x >= w or y >= h:
        return None
    layout = normalize_token(cfg.get("layout", "SERPENTINE"))
    if layout in ("SERPENTINE", "ZIGZAG", "SNAKE") and (y % 2) == 1:
        logical = y * w + (w - 1 - x)
    else:
        logical = y * w + x
    indices = out.get("indices", [])
    if logical < 0 or logical >= len(indices):
        return None
    return indices[logical]


def matrix_set_physical(name, physical_index, rgb):
    item = matrix_get(name)
    if item[0] is None:
        return False
    _, out, cfg, bus = item
    np = bus.get("driver")
    rgb = scale_rgb(rgb, bus.get("brightness", 1.0))
    rgb = apply_rgb_order(rgb, bus.get("order", "RGB"))
    try:
        np[physical_index] = rgb
        return True
    except Exception:
        return False


def matrix_write(name):
    item = matrix_get(name)
    if item[0] is None:
        return
    _, _, _, bus = item
    try:
        bus.get("driver").write()
    except Exception:
        pass


def matrix_clear(name):
    matrix_fill(name, (0, 0, 0), report=False)
    print("MATRIXCLEAR", normalize_token(name))


def matrix_fill(name, color, report=True):
    item = matrix_get(name)
    if item[0] is None:
        print("ERR MATRIX", normalize_token(name))
        return
    name, out, cfg, bus = item
    rgb = parse_rgb_value(color) if not isinstance(color, tuple) else color
    np = bus.get("driver")
    rgb2 = apply_rgb_order(scale_rgb(rgb, bus.get("brightness", 1.0)), bus.get("order", "RGB"))
    try:
        for idx in out.get("indices", []):
            np[idx] = rgb2
        np.write()
        current[name] = "CUSTOM"
        if report:
            print("MATRIXFILL", name)
    except Exception as e:
        print("ERR MATRIXFILL", e)


def matrix_pixel(name, x, y, color):
    idx = matrix_pos(name, x, y)
    if idx is None:
        print("ERR MATRIXPIXEL", normalize_token(name), x, y)
        return
    matrix_set_physical(name, idx, parse_rgb_value(color))
    matrix_write(name)
    print("MATRIXPIXEL", normalize_token(name), x, y)


def matrix_rect(name, x, y, w, h, color):
    rgb = parse_rgb_value(color)
    try:
        x = int(x); y = int(y); w = int(w); h = int(h)
    except Exception:
        print("ERR MATRIXRECT SYNTAX")
        return
    for yy in range(y, y + h):
        for xx in range(x, x + w):
            idx = matrix_pos(name, xx, yy)
            if idx is not None:
                matrix_set_physical(name, idx, rgb)
    matrix_write(name)
    print("MATRIXRECT", normalize_token(name))


def clean_hex_payload(payload):
    payload = str(payload or "").strip()
    for ch in (" ", "\t", "\r", "\n", ";", ",", "#"):
        payload = payload.replace(ch, "")
    return payload.upper()


def matrix_image_hex(name, payload):
    item = matrix_get(name)
    if item[0] is None:
        print("ERR MATRIX", normalize_token(name))
        return
    name, out, cfg, bus = item
    w = int(cfg.get("width", 0))
    h = int(cfg.get("height", 0))
    payload = clean_hex_payload(payload)
    max_pixels = min(w * h, len(payload) // 6)
    if max_pixels <= 0:
        print("ERR MATRIXIMAGE EMPTY")
        return
    for logical in range(max_pixels):
        off = logical * 6
        rgb = parse_rgb_value(payload[off:off + 6])
        x = logical % w
        y = logical // w
        idx = matrix_pos(name, x, y)
        if idx is not None:
            matrix_set_physical(name, idx, rgb)
    matrix_write(name)
    current[name] = "IMAGE"
    print("MATRIXIMAGE", name, max_pixels, "PIXELS")


def matrix_row_hex(name, y, payload):
    item = matrix_get(name)
    if item[0] is None:
        print("ERR MATRIX", normalize_token(name))
        return
    name, out, cfg, bus = item
    try:
        y = int(y)
    except Exception:
        print("ERR MATRIXROW Y")
        return
    w = int(cfg.get("width", 0))
    payload = clean_hex_payload(payload)
    max_pixels = min(w, len(payload) // 6)
    for x in range(max_pixels):
        off = x * 6
        rgb = parse_rgb_value(payload[off:off + 6])
        idx = matrix_pos(name, x, y)
        if idx is not None:
            matrix_set_physical(name, idx, rgb)
    matrix_write(name)
    current[name] = "IMAGE"
    print("MATRIXROW", name, y, max_pixels, "PIXELS")


def matrix_draw_text(name, text, color="WHITE", scale=1, clear_first=True):
    item = matrix_get(name)
    if item[0] is None:
        print("ERR MATRIX", normalize_token(name))
        return
    name, out, cfg, bus = item
    try:
        scale = int(scale)
    except Exception:
        scale = 1
    if scale < 1:
        scale = 1
    text = str(text or "").upper()
    rgb = parse_rgb_value(color)
    w = int(cfg.get("width", 0))
    h = int(cfg.get("height", 0))
    if clear_first:
        matrix_fill(name, (0, 0, 0), report=False)

    # Largeur approximative pour centrer.
    total = 0
    for ch in text:
        glyph = FONT_3X5.get(ch, FONT_3X5.get(" "))
        total += (len(glyph[0]) + 1) * scale
    if total > 0:
        total -= scale
    x0 = max(0, (w - total) // 2)
    y0 = max(0, (h - 5 * scale) // 2)

    x_cursor = x0
    for ch in text:
        glyph = FONT_3X5.get(ch, FONT_3X5.get(" "))
        gw = len(glyph[0])
        for gy, row in enumerate(glyph):
            for gx, bit in enumerate(row):
                if bit == "1":
                    for sy in range(scale):
                        for sx in range(scale):
                            idx = matrix_pos(name, x_cursor + gx * scale + sx, y0 + gy * scale + sy)
                            if idx is not None:
                                matrix_set_physical(name, idx, rgb)
        x_cursor += (gw + 1) * scale
    matrix_write(name)
    current[name] = "TEXT"
    print("MATRIXTEXT", name, text)


def matrix_score(name, score, color="GREEN", scale=None):
    item = matrix_get(name)
    if item[0] is None:
        print("ERR MATRIX", normalize_token(name))
        return
    _, _, cfg, _ = item
    text = str(score)
    if scale is None:
        w = int(cfg.get("width", 0))
        h = int(cfg.get("height", 0))
        raw_width = max(1, len(text) * 4 - 1)
        s1 = w // raw_width
        s2 = h // 5
        scale = max(1, min(s1, s2))
    matrix_draw_text(name, text, color, scale, True)


def matrix_info(name=None):
    if name:
        names = [normalize_token(name)]
    else:
        names = sorted(matrix_cfg.keys())
    if not names:
        print("MATRIXINFO NONE")
        return
    for n in names:
        cfg = matrix_cfg.get(n)
        out = outputs.get(n)
        if not cfg or not out:
            print("ERR MATRIX", n)
            continue
        print("MATRIX", n, cfg.get("width"), "x", cfg.get("height"), "LAYOUT", cfg.get("layout"), "BUS", out.get("bus"), "PIXELS", len(out.get("indices", [])))


def matrix_configure(args):
    if len(args) < 4:
        print("ERR MATRIXCFG SYNTAX")
        return
    name = normalize_token(args[1])
    try:
        width = int(args[2])
        height = int(args[3])
    except Exception:
        print("ERR MATRIXCFG SIZE")
        return
    layout = args[4] if len(args) >= 5 else "SERPENTINE"
    register_matrix_output(name, width, height, layout)
    print("MATRIXCFG", name, width, "x", height, normalize_token(layout))


def set_bus_order(name, order):
    name = normalize_token(name)
    order = normalize_token(order or "RGB")
    if name not in led_buses:
        print("ERR BUS", name)
        return
    if len(order) != 3:
        print("ERR BUSORDER", order)
        return
    led_buses[name]["order"] = order
    print("BUSORDER", name, order)


def list_hardware():
    if not list_hardware_profiles:
        print("HWLIST MISSING hardware_profiles.py")
        return
    try:
        print("HWLIST", ",".join(list_hardware_profiles()))
    except Exception as e:
        print("ERR HWLIST", e)


def describe_hardware(name):
    if not describe_profile:
        print("HWDESC MISSING hardware_profiles.py")
        return
    try:
        print(describe_profile(name))
    except Exception as e:
        print("ERR HWDESC", e)


def apply_hardware(name):
    if not get_init_commands:
        print("ERR HW MISSING hardware_profiles.py")
        return
    try:
        lines = get_init_commands(name)
    except Exception as e:
        print("ERR HW", e)
        return
    for cmdline in lines:
        handle(cmdline)
    print("HW", normalize_token(name), "DONE")


def handle_matrix_command(cmd, args, line):
    if cmd in ("MATRIXINFO", "MINfo".upper()):
        matrix_info(args[1] if len(args) >= 2 else None)
        return True
    if cmd == "MATRIXCFG":
        matrix_configure(args)
        return True
    if cmd == "MATRIXCLEAR":
        matrix_clear(args[1] if len(args) >= 2 else first_matrix_name())
        return True
    if cmd == "MATRIXFILL":
        if len(args) >= 3:
            matrix_fill(args[1], args[2])
        return True
    if cmd == "MATRIXPIXEL":
        if len(args) >= 5:
            matrix_pixel(args[1], args[2], args[3], args[4])
        return True
    if cmd == "MATRIXRECT":
        if len(args) >= 7:
            matrix_rect(args[1], args[2], args[3], args[4], args[5], args[6])
        return True
    if cmd in ("MATRIXIMAGE", "MATRIXBLIT", "MATRIXHEX"):
        if len(args) >= 3:
            payload = line.split(" ", 2)[2]
            matrix_image_hex(args[1], payload)
        return True
    if cmd == "MATRIXROW":
        if len(args) >= 4:
            payload = line.split(" ", 3)[3]
            matrix_row_hex(args[1], args[2], payload)
        return True
    if cmd == "MATRIXTEXT":
        # MATRIXTEXT MATRIX1 RED HELLO 123
        if len(args) >= 4:
            payload = line.split(" ", 3)[3]
            matrix_draw_text(args[1], payload, args[2], 1, True)
        return True
    if cmd == "MATRIXSCORE":
        # MATRIXSCORE MATRIX1 12345 GREEN [scale]
        if len(args) >= 3:
            color = args[3] if len(args) >= 4 else "GREEN"
            scale = args[4] if len(args) >= 5 else None
            matrix_score(args[1], args[2], color, scale)
        return True
    if cmd in ("MAME", "MAMEOUT"):
        # MAME SCORE 12345 [MATRIX1] [GREEN]
        if len(args) >= 3 and normalize_token(args[1]) in ("SCORE", "SCORE1", "P1SCORE"):
            target = args[3] if len(args) >= 4 and normalize_token(args[3]) in matrix_cfg else first_matrix_name()
            color = args[4] if len(args) >= 5 else "GREEN"
            matrix_score(target, args[2], color)
            return True
    return False

# ============================================================
# COMMANDES
# ============================================================
# PING
# SCAN
# GET
# GPIO
# ONOFFINVERT ON|OFF
# INIT / RESETCFG
# HW <hardware_profile_name>
# HWLIST / HWDESC <profile>
# BUS <name> NEOPIXEL <gpio> <count> [brightness] [RGB|GRB|BRG...]
# BUSORDER <name> RGB|GRB|BRG...
# PTR <name> <kind> <mode> <slot> <pins...|bus:range> [HIGH|LOW|SHARED]
# COMMIT
# MATRIXINFO / MATRIXCFG <name> <w> <h> [SERPENTINE|LINEAR]
# MATRIXFILL <name> RED / MATRIXPIXEL <name> x y #RRGGBB
# MATRIXIMAGE <name> <RRGGBB...> / MATRIXROW <name> y <RRGGBB...>
# MATRIXSCORE <name> 12345 GREEN
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
    order = args[6] if len(args) >= 7 else "RGB"
    add_bus(name, bus_type, gp, count, brightness, order)


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

    if cmd == "ONOFFINVERT":
        global ONOFF_COMMAND_INVERT
        if len(args) >= 2:
            v = normalize_token(args[1])
            ONOFF_COMMAND_INVERT = v in ("1", "ON", "TRUE", "YES", "INVERT", "INVERTED")
        print("ONOFFINVERT", "ON" if ONOFF_COMMAND_INVERT else "OFF")
        return


    if cmd in ("BUSES", "BUSLIST"):
        list_buses()
        return

    if cmd in ("HWLIST", "HARDWARES"):
        list_hardware()
        return

    if cmd in ("HWDESC", "HWINFO"):
        if len(args) >= 2:
            describe_hardware(args[1])
        return

    if cmd in ("HW", "HARDWARE"):
        if len(args) >= 2:
            apply_hardware(args[1])
        return

    if cmd == "BUSORDER":
        if len(args) >= 3:
            set_bus_order(args[1], args[2])
        return

    if handle_matrix_command(cmd, args, line):
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
