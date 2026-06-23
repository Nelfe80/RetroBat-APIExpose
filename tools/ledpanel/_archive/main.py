import sys, select, time
from machine import Pin, PWM

try:
    from profiles_db import PROFILES_LIBRARY
except:
    PROFILES_LIBRARY = {}

# ============================================================
# SAFE BOOT
# ============================================================

SAFE_GPIOS = list(range(0, 23)) + [26, 27, 28]

for gp in SAFE_GPIOS:
    try:
        Pin(gp, Pin.OUT).value(1)
    except:
        pass

time.sleep_ms(100)

# ============================================================
# CONFIG
# ============================================================

BUTTON_PINS = {
    "B1": (0, 1, 2),
    "B2": (3, 4, 5),
    "B3": (6, 7, 8),
    "B4": (9, 10, 11),
    "B5": (12, 13, 14),
    "B6": (15, 16, 17),
    "B7": (18, 19, 20),
    "B8": (21, 22, 26),
}

# slot physique -> bouton lumineux
SLOT_TO_BUTTON = {
    "1": "B1",
    "2": "B2",
    "3": "B3",
    "4": "B4",
    "5": "B5",
    "6": "B6",
    "7": "B7",
    "8": "B8",
}

PANEL_LAYOUT = {
    "top":    ["4", "3", "5", "7"],
    "bottom": ["1", "2", "6", "8"],
}

PWM_FREQ = 1000

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

# ============================================================
# INIT
# ============================================================

buttons = {}
current = {}
pwm_objects = {}
quiet = True

for btn, gps in BUTTON_PINS.items():
    btn = btn.upper()
    buttons[btn] = []
    for gp in gps:
        p = Pin(gp, Pin.OUT)
        p.value(1)
        buttons[btn].append(gp)
    current[btn] = "BLACK"

poll = select.poll()
poll.register(sys.stdin, select.POLLIN)

heartbeat = Pin(25, Pin.OUT)
last_hb = time.ticks_ms()

# ============================================================
# PWM CONFLICTS
# ============================================================

def pwm_key(gp):
    return gp % 16

gpio_owner = {}
for btn, gps in buttons.items():
    for idx, gp in enumerate(gps):
        gpio_owner[gp] = (btn, idx)

conflicts = {}
for gp in gpio_owner:
    conflicts[gp] = []
    for other_gp in gpio_owner:
        if other_gp != gp and pwm_key(other_gp) == pwm_key(gp):
            conflicts[gp].append(other_gp)

# ============================================================
# CORE
# ============================================================

def duty(percent):
    return int((int(percent) / 100) * 65535)

def deinit_pwm(gp):
    if gp in pwm_objects:
        try:
            pwm_objects[gp].deinit()
        except:
            pass
        del pwm_objects[gp]

def write_channel(gp, percent):
    percent = int(percent)

    if percent == 0 or percent == 100:
        deinit_pwm(gp)
        Pin(gp, Pin.OUT).value(1 if percent == 100 else 0)
    else:
        if gp not in pwm_objects:
            pwm = PWM(Pin(gp))
            pwm.freq(PWM_FREQ)
            pwm_objects[gp] = pwm
        pwm_objects[gp].duty_u16(duty(percent))

def normalize_color(color):
    if not color:
        return "BLACK"
    return str(color).strip().upper()

def is_primary(values):
    return all(v in (0, 100) for v in values)

def pwm_is_safe(btn, values):
    btn = btn.upper()

    for idx, percent in enumerate(values):
        if percent in (0, 100):
            continue

        gp = buttons[btn][idx]

        for other_gp in conflicts.get(gp, []):
            other_btn, _ = gpio_owner[other_gp]
            if other_btn != btn and current.get(other_btn) != "BLACK":
                return False

    return True

def apply_values(btn, values):
    btn = btn.upper()
    for gp, percent in zip(buttons[btn], values):
        write_channel(gp, percent)

def set_button(btn, color, force=False):
    btn = btn.upper()
    color = normalize_color(color)

    if btn not in buttons:
        return

    if color not in COLOR_MAP:
        color = "BLACK"

    values = COLOR_MAP[color]

    if not force and not is_primary(values) and not pwm_is_safe(btn, values):
        color = FALLBACK.get(color, "BLACK")
        values = COLOR_MAP[color]

    apply_values(btn, values)
    current[btn] = color

def set_slot(slot, color, force=False):
    slot = str(slot).upper()
    if slot in SLOT_TO_BUTTON:
        set_button(SLOT_TO_BUTTON[slot], color, force)

def clear():
    for btn in buttons:
        set_button(btn, "BLACK", force=True)

def all_color(color):
    color = normalize_color(color)

    if color not in COLOR_MAP:
        color = "BLACK"

    # ALL doit toujours être homogène sur tout le panel.
    # Donc si on demande une nuance PWM, on force sa primaire proche.
    if color in SHADES:
        color = FALLBACK.get(color, "BLACK")

    values = PRIMARY.get(color, COLOR_MAP["BLACK"])

    for btn in buttons:
        apply_values(btn, values)
        current[btn] = color

def apply_profile(profile_name, force_pwm=True):
    profile_name = normalize_color(profile_name)

    if profile_name not in PROFILES_LIBRARY:
        print("ERR PROFILE", profile_name)
        return

    clear()

    profile = PROFILES_LIBRARY[profile_name]
    slots = profile.get("slots", {})

    for slot, data in slots.items():
        slot_key = str(slot).upper()

        if slot_key in SLOT_TO_BUTTON:
            color = data.get("color", "BLACK")
            set_slot(slot_key, color, force=force_pwm)

    print("PANEL", profile_name)

def list_panels():
    print("PANELS", ",".join(sorted(PROFILES_LIBRARY.keys())))

def scan():
    print("BUTTONS", ",".join(sorted(buttons.keys())))
    print("SLOTS_TOP", ",".join(PANEL_LAYOUT["top"]))
    print("SLOTS_BOTTOM", ",".join(PANEL_LAYOUT["bottom"]))
    print("COLORS", ",".join(sorted(COLOR_MAP.keys())))
    print("PROFILES", len(PROFILES_LIBRARY))

def get_state():
    for slot in PANEL_LAYOUT["top"]:
        btn = SLOT_TO_BUTTON[slot]
        print("TOP", slot, btn, current[btn])
    for slot in PANEL_LAYOUT["bottom"]:
        btn = SLOT_TO_BUTTON[slot]
        print("BOTTOM", slot, btn, current[btn])

def demo_panels(delay_ms=1200):
    names = sorted(PROFILES_LIBRARY.keys())
    for name in names:
        apply_profile(name, force_pwm=True)
        time.sleep_ms(delay_ms)
    clear()
    print("DEMOPANELS DONE")

def demo_buttons(delay_ms=800):
    clear()
    time.sleep_ms(200)

    for slot in ["1", "2", "3", "4", "5", "6", "7", "8"]:
        clear()
        print("BUTTON", slot, "->", SLOT_TO_BUTTON[slot])
        set_slot(slot, "WHITE", force=True)
        time.sleep_ms(delay_ms)

    clear()
    print("DEMOBUTTONS DONE")
    
# ============================================================
# COMMANDES
# ============================================================
# PING
# SCAN
# GET
# CLEAR
# ALL RED
# SET B1 RED
# SLOT 4 BLUE
# PANEL NEOGEO
# PROFILE NEOGEO
# PANELS
# DEMOPANELS
# DEMOPANELS 700

def handle(line):
    global quiet

    line = line.strip()
    if not line:
        return

    parts = line.split(" ", 2)
    cmd = parts[0].upper()

    if cmd == "PING":
        print("PONG")
        return

    if cmd == "SCAN":
        scan()
        return

    if cmd == "GET":
        get_state()
        return

    if cmd == "PANELS":
        list_panels()
        return

    if cmd == "CLEAR":
        clear()
        return

    if cmd == "ALL":
        if len(parts) >= 2:
            all_color(parts[1])
        return

    if cmd == "SET":
        if len(parts) >= 3:
            set_button(parts[1], parts[2], force=False)
        return

    if cmd == "SETPWM":
        if len(parts) >= 3:
            set_button(parts[1], parts[2], force=True)
        return

    if cmd == "SLOT":
        if len(parts) >= 3:
            set_slot(parts[1], parts[2], force=False)
        return

    if cmd == "SLOTPWM":
        if len(parts) >= 3:
            set_slot(parts[1], parts[2], force=True)
        return

    if cmd == "PANEL" or cmd == "PROFILE":
        if len(parts) >= 2:
            apply_profile(parts[1], force_pwm=True)
        return

    if cmd == "DEMOPANELS":
        delay = 1200
        args = line.split()
        if len(args) >= 2:
            try:
                delay = int(args[1])
            except:
                pass
        demo_panels(delay)
        return
    
    if cmd == "DEMOBUTTONS":
        delay = 800
        args = line.split()
        if len(args) >= 2:
            try:
                delay = int(args[1])
            except:
                pass
        demo_buttons(delay)
        return
    
    if cmd == "BATCH":
        payload = line.split(" ", 1)[1] if " " in line else ""
        for item in payload.split(";"):
            vals = item.strip().split()
            if len(vals) == 2:
                set_button(vals[0], vals[1], force=False)
        return

    if cmd == "BATCHPWM":
        payload = line.split(" ", 1)[1] if " " in line else ""
        for item in payload.split(";"):
            vals = item.strip().split()
            if len(vals) == 2:
                set_button(vals[0], vals[1], force=True)
        return

# ============================================================
# START
# ============================================================

clear()
print("READY PROFILE DRIVER")
scan()

while True:
    if poll.poll(0):
        handle(sys.stdin.readline())

    now = time.ticks_ms()
    if time.ticks_diff(now, last_hb) > 500:
        heartbeat.toggle()
        last_hb = now

    time.sleep_ms(1)
