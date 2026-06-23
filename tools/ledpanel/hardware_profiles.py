# hardware_profiles.py
# ============================================================
# Profils matériels génériques pour le Dynamic Panel Driver Pico.
#
# Objectif : ne plus coder le panel en dur dans main.py.
# Le PC/RetroBat, ou le firmware Pico, choisit un profil matériel
# et pousse les lignes INIT / BUS / PTR / COMMIT au démarrage.
#
# Compatible avec le protocole du fichier main_dynamic_panel_addr.py :
#   INIT
#   BUS <nom_bus> NEOPIXEL <gpio_data> <nombre_leds> [luminosité]
#   PTR <nom> <type> <mode> <slot> <gpio... ou bus:index>
#   COMMIT
#
# Règles de conception :
# - GPIO directs utilisables : GP0..GP22 + GP26..GP28.
# - GP25 est gardé pour la LED interne du Pico.
# - 1 sortie ONOFF = 1 GPIO.
# - 1 sortie RGB GPIO direct = 3 GPIO.
# - 1 bus adressable WS2812/NeoPixel = 1 GPIO data + N pixels.
# - Les boutons B1..B8 ne mélangent pas GPIO et adressable dans un même profil.
# - On peut en revanche avoir des boutons adressables + START/SELECT en GPIO.
#
# Utilisation côté PC :
#   from hardware_profiles import get_init_commands
#   for line in get_init_commands("GPIO_8B_SS_GPIO"):
#       serial.write((line + "\n").encode("utf-8"))
#
# Utilisation dans main.py côté Pico, si tu veux une commande unique :
#   from hardware_profiles import apply_hardware_profile
#   ...
#   if cmd in ("HW", "HARDWARE"):
#       apply_hardware_profile(args[1], handle)
#       return
#
# Puis dans Thonny / serial :
#   HW GPIO_8B_SS_GPIO
#
# ============================================================

SAFE_GPIOS = list(range(0, 23)) + [26, 27, 28]
DEFAULT_BRIGHTNESS = 0.35

# ------------------------------------------------------------
# Outils internes
# ------------------------------------------------------------

def _pin_csv(pins):
    return ",".join(str(p) for p in pins)


def _addr(bus, start, count):
    if count <= 1:
        return "%s:%d" % (bus, start)
    return "%s:%d-%d" % (bus, start, start + count - 1)


def _used_gpios(commands):
    """Retourne les GPIO déclarés par BUS et PTR GPIO direct."""
    used = []
    for line in commands:
        parts = line.split()
        if not parts:
            continue
        if parts[0] == "BUS" and len(parts) >= 4:
            try:
                gp = int(parts[3])
                if gp not in used:
                    used.append(gp)
            except Exception:
                pass
        elif parts[0] == "PTR" and len(parts) >= 6:
            mode = parts[3].upper()
            if mode in ("ONOFF", "GPIO", "RGB"):
                for token in parts[5:]:
                    if token.upper() in ("LOW", "ACTIVE_LOW", "HIGH", "ACTIVE_HIGH", "SHARED"):
                        continue
                    for p in token.replace(",", " ").split():
                        try:
                            gp = int(p)
                            if gp not in used:
                                used.append(gp)
                        except Exception:
                            pass
    return used


def _validate_profile(name, profile):
    commands = profile.get("commands", [])
    if not commands or commands[0] != "INIT" or commands[-1] != "COMMIT":
        raise ValueError("%s: profil invalide, il doit commencer par INIT et finir par COMMIT" % name)

    used = _used_gpios(commands)
    invalid = [gp for gp in used if gp not in SAFE_GPIOS]
    if invalid:
        raise ValueError("%s: GPIO interdits ou réservés: %s" % (name, invalid))

    duplicates = []
    seen = set()
    for gp in used:
        if gp in seen and gp not in duplicates:
            duplicates.append(gp)
        seen.add(gp)

    # _used_gpios déduplique, donc pas de doublon ici. On laisse la logique
    # explicite au cas où la fonction change.
    if duplicates:
        raise ValueError("%s: GPIO dupliqués: %s" % (name, duplicates))


class GpioAllocator:
    def __init__(self, safe=None, reserved=None):
        self.safe = list(safe or SAFE_GPIOS)
        self.used = []
        for gp in reserved or []:
            self.reserve(gp)

    def reserve(self, gp):
        gp = int(gp)
        if gp not in self.safe:
            raise ValueError("GPIO non autorisé: GP%d" % gp)
        if gp in self.used:
            raise ValueError("GPIO déjà utilisé: GP%d" % gp)
        self.used.append(gp)

    def take(self, count=1):
        result = []
        for gp in self.safe:
            if gp not in self.used:
                self.used.append(gp)
                result.append(gp)
                if len(result) == count:
                    return result
        raise ValueError("Pas assez de GPIO disponibles: besoin de %d" % count)

    def take_preferred(self, preferred, count=1):
        result = []
        for gp in preferred:
            gp = int(gp)
            if gp in self.safe and gp not in self.used:
                self.used.append(gp)
                result.append(gp)
                if len(result) == count:
                    return result
        # Complète avec les GPIO libres standards si les préférés ne suffisent pas.
        while len(result) < count:
            result.extend(self.take(1))
        return result


class AddrAllocator:
    def __init__(self, start=0):
        self.next = int(start)

    def take(self, count=1):
        start = self.next
        self.next += int(count)
        return start


# ------------------------------------------------------------
# Constructeurs de lignes PTR / BUS
# ------------------------------------------------------------

def ptr_gpio(name, kind, mode, slot, pins, active_low=True):
    suffix = "LOW" if active_low else "HIGH"
    return "PTR %s %s %s %s %s %s" % (
        str(name).upper(),
        str(kind).upper(),
        str(mode).upper(),
        str(slot).upper(),
        _pin_csv(pins),
        suffix,
    )


def ptr_addr(name, kind, slot, bus, start, count):
    return "PTR %s %s ADDR %s %s" % (
        str(name).upper(),
        str(kind).upper(),
        str(slot).upper(),
        _addr(str(bus).upper(), int(start), int(count)),
    )


def bus_neopixel(name, gpio, count, brightness=DEFAULT_BRIGHTNESS):
    return "BUS %s NEOPIXEL %d %d %.2f" % (
        str(name).upper(),
        int(gpio),
        int(count),
        float(brightness),
    )


# ------------------------------------------------------------
# Générateurs génériques
# ------------------------------------------------------------

def build_gpio_profile(button_count=8,
                       start_select="GPIO",
                       joysticks=0,
                       joystick_mode="NONE",
                       addr_bus=False,
                       strips=0,
                       strip_pixels=30,
                       circles=0,
                       circle_pixels=16,
                       matrix=None,
                       bus_gpio=28,
                       bus_name="PANEL",
                       brightness=DEFAULT_BRIGHTNESS,
                       name=None,
                       description=None):
    """
    Profil avec boutons B1..Bn en RGB GPIO direct.

    start_select:
      - "NONE"
      - "GPIO"  -> START/SELECT ONOFF
      - "RGB"   -> START/SELECT RGB GPIO direct

    joystick_mode:
      - "NONE"
      - "GPIO"  -> JOY1..JOYn ONOFF
      - "RGB"   -> JOY1..JOYn RGB GPIO direct
      - "ADDR"  -> JOY1..JOYn adressables sur bus NeoPixel

    addr_bus=True est automatiquement activé si STRIP/CIRCLE/MATRIX/JOY_ADDR.
    """
    button_count = int(button_count)
    if button_count not in (2, 4, 6, 8):
        raise ValueError("button_count doit être 2, 4, 6 ou 8")

    start_select = str(start_select or "NONE").upper()
    joystick_mode = str(joystick_mode or "NONE").upper()
    joysticks = int(joysticks or 0)
    strips = int(strips or 0)
    circles = int(circles or 0)

    needs_addr = bool(addr_bus or strips or circles or matrix or joystick_mode == "ADDR")
    reserved = [bus_gpio] if needs_addr else []
    gp = GpioAllocator(reserved=reserved)
    addr_alloc = AddrAllocator(0)
    commands = ["INIT"]

    if needs_addr:
        # Taille globale du bus adressable : on additionne les blocs prévus.
        total_pixels = 0
        if joystick_mode == "ADDR":
            total_pixels += joysticks * 12
        total_pixels += strips * strip_pixels
        total_pixels += circles * circle_pixels
        if matrix:
            total_pixels += int(matrix[0]) * int(matrix[1])
        total_pixels = max(total_pixels, 1)
        commands.append(bus_neopixel(bus_name, bus_gpio, total_pixels, brightness))

    for i in range(1, button_count + 1):
        commands.append(ptr_gpio("B%d" % i, "BUTTON", "RGB", str(i), gp.take(3)))

    if start_select == "GPIO":
        commands.append(ptr_gpio("START", "START", "ONOFF", "START", gp.take(1)))
        commands.append(ptr_gpio("SELECT", "SELECT", "ONOFF", "SELECT", gp.take(1)))
    elif start_select == "RGB":
        commands.append(ptr_gpio("START", "START", "RGB", "START", gp.take(3)))
        commands.append(ptr_gpio("SELECT", "SELECT", "RGB", "SELECT", gp.take(3)))
    elif start_select == "NONE":
        pass
    else:
        raise ValueError("start_select invalide: %s" % start_select)

    if joysticks:
        for j in range(1, joysticks + 1):
            if joystick_mode == "GPIO":
                commands.append(ptr_gpio("JOY%d" % j, "JOY", "ONOFF", "JOY%d" % j, gp.take(1)))
            elif joystick_mode == "RGB":
                commands.append(ptr_gpio("JOY%d" % j, "JOY", "RGB", "JOY%d" % j, gp.take(3)))
            elif joystick_mode == "ADDR":
                start = addr_alloc.take(12)
                commands.append(ptr_addr("JOY%d" % j, "JOY", "JOY%d" % j, bus_name, start, 12))
            elif joystick_mode == "NONE":
                pass
            else:
                raise ValueError("joystick_mode invalide: %s" % joystick_mode)

    for s in range(1, strips + 1):
        start = addr_alloc.take(strip_pixels)
        commands.append(ptr_addr("STRIP%d" % s, "STRIP", "STRIP%d" % s, bus_name, start, strip_pixels))

    for c in range(1, circles + 1):
        start = addr_alloc.take(circle_pixels)
        commands.append(ptr_addr("CIRCLE%d" % c, "CIRCLE", "CIRCLE%d" % c, bus_name, start, circle_pixels))

    if matrix:
        w, h = int(matrix[0]), int(matrix[1])
        start = addr_alloc.take(w * h)
        commands.append(ptr_addr("MATRIX1", "MATRIX", "MATRIX1", bus_name, start, w * h))

    commands.append("COMMIT")

    meta = {
        "name": name or "GPIO_%dB" % button_count,
        "family": "GPIO_BUTTONS",
        "button_mode": "RGB_GPIO",
        "button_count": button_count,
        "start_select": start_select,
        "joysticks": joysticks,
        "joystick_mode": joystick_mode,
        "strips": strips,
        "circles": circles,
        "matrix": matrix,
        "used_gpios": sorted(_used_gpios(commands)),
        "description": description or "Profil GPIO direct avec %d boutons RGB." % button_count,
    }
    return {"meta": meta, "commands": commands}


def build_addr_buttons_profile(button_count=8,
                               pixels_per_button=4,
                               start_select="ADDR",
                               joysticks=0,
                               joystick_mode="ADDR",
                               strips=0,
                               strip_pixels=30,
                               circles=0,
                               circle_pixels=16,
                               matrix=None,
                               bus_gpio=22,
                               bus_name="PANEL",
                               brightness=DEFAULT_BRIGHTNESS,
                               name=None,
                               description=None):
    """
    Profil avec boutons B1..Bn adressables sur un bus WS2812/NeoPixel.

    Important : les boutons B1..B8 sont tous en ADDR dans ce générateur.
    On peut ajouter START/SELECT en GPIO ou en ADDR.

    start_select:
      - "NONE"
      - "GPIO" -> START/SELECT ONOFF sur GPIO directs
      - "ADDR" -> START/SELECT pixels adressables
    """
    button_count = int(button_count)
    if button_count not in (2, 4, 6, 8):
        raise ValueError("button_count doit être 2, 4, 6 ou 8")

    start_select = str(start_select or "NONE").upper()
    joystick_mode = str(joystick_mode or "NONE").upper()
    joysticks = int(joysticks or 0)
    strips = int(strips or 0)
    circles = int(circles or 0)

    # Si START/SELECT sont GPIO, on réserve le GPIO du bus pour éviter les collisions.
    gp = GpioAllocator(reserved=[bus_gpio])
    addr_alloc = AddrAllocator(0)

    total_pixels = button_count * int(pixels_per_button)
    if start_select == "ADDR":
        total_pixels += 2
    if joystick_mode == "ADDR":
        total_pixels += joysticks * 12
    total_pixels += strips * strip_pixels
    total_pixels += circles * circle_pixels
    if matrix:
        total_pixels += int(matrix[0]) * int(matrix[1])
    total_pixels = max(total_pixels, 1)

    commands = ["INIT", bus_neopixel(bus_name, bus_gpio, total_pixels, brightness)]

    for i in range(1, button_count + 1):
        start = addr_alloc.take(pixels_per_button)
        commands.append(ptr_addr("B%d" % i, "BUTTON", str(i), bus_name, start, pixels_per_button))

    if start_select == "ADDR":
        commands.append(ptr_addr("START", "START", "START", bus_name, addr_alloc.take(1), 1))
        commands.append(ptr_addr("SELECT", "SELECT", "SELECT", bus_name, addr_alloc.take(1), 1))
    elif start_select == "GPIO":
        # Sur les panels adressables, on garde par convention GP27/GP28 pour
        # START/SELECT simples si ces GPIO ne sont pas réservés par le bus.
        commands.append(ptr_gpio("START", "START", "ONOFF", "START", gp.take_preferred([27], 1)))
        commands.append(ptr_gpio("SELECT", "SELECT", "ONOFF", "SELECT", gp.take_preferred([28], 1)))
    elif start_select == "NONE":
        pass
    else:
        raise ValueError("start_select invalide: %s" % start_select)

    for j in range(1, joysticks + 1):
        if joystick_mode == "ADDR":
            start = addr_alloc.take(12)
            commands.append(ptr_addr("JOY%d" % j, "JOY", "JOY%d" % j, bus_name, start, 12))
        elif joystick_mode == "GPIO":
            commands.append(ptr_gpio("JOY%d" % j, "JOY", "ONOFF", "JOY%d" % j, gp.take(1)))
        elif joystick_mode == "RGB":
            commands.append(ptr_gpio("JOY%d" % j, "JOY", "RGB", "JOY%d" % j, gp.take(3)))
        elif joystick_mode == "NONE":
            pass
        else:
            raise ValueError("joystick_mode invalide: %s" % joystick_mode)

    for s in range(1, strips + 1):
        start = addr_alloc.take(strip_pixels)
        commands.append(ptr_addr("STRIP%d" % s, "STRIP", "STRIP%d" % s, bus_name, start, strip_pixels))

    for c in range(1, circles + 1):
        start = addr_alloc.take(circle_pixels)
        commands.append(ptr_addr("CIRCLE%d" % c, "CIRCLE", "CIRCLE%d" % c, bus_name, start, circle_pixels))

    if matrix:
        w, h = int(matrix[0]), int(matrix[1])
        start = addr_alloc.take(w * h)
        commands.append(ptr_addr("MATRIX1", "MATRIX", "MATRIX1", bus_name, start, w * h))

    commands.append("COMMIT")

    meta = {
        "name": name or "ADDR_%dB" % button_count,
        "family": "ADDR_BUTTONS",
        "button_mode": "ADDR",
        "button_count": button_count,
        "pixels_per_button": pixels_per_button,
        "start_select": start_select,
        "joysticks": joysticks,
        "joystick_mode": joystick_mode,
        "strips": strips,
        "circles": circles,
        "matrix": matrix,
        "bus_gpio": bus_gpio,
        "bus_pixels": total_pixels,
        "used_gpios": sorted(_used_gpios(commands)),
        "description": description or "Profil adressable avec %d boutons." % button_count,
    }
    return {"meta": meta, "commands": commands}


def build_addr_fx_profile(strips=0,
                          strip_pixels=30,
                          circles=0,
                          circle_pixels=16,
                          matrix=None,
                          bus_gpio=22,
                          bus_name="PANEL",
                          brightness=DEFAULT_BRIGHTNESS,
                          name=None,
                          description=None):
    """Profil uniquement effets lumineux adressables : bandeaux, circles, matrices."""
    strips = int(strips or 0)
    circles = int(circles or 0)
    addr_alloc = AddrAllocator(0)

    total_pixels = strips * strip_pixels + circles * circle_pixels
    if matrix:
        total_pixels += int(matrix[0]) * int(matrix[1])
    total_pixels = max(total_pixels, 1)

    commands = ["INIT", bus_neopixel(bus_name, bus_gpio, total_pixels, brightness)]

    for s in range(1, strips + 1):
        start = addr_alloc.take(strip_pixels)
        commands.append(ptr_addr("STRIP%d" % s, "STRIP", "STRIP%d" % s, bus_name, start, strip_pixels))

    for c in range(1, circles + 1):
        start = addr_alloc.take(circle_pixels)
        commands.append(ptr_addr("CIRCLE%d" % c, "CIRCLE", "CIRCLE%d" % c, bus_name, start, circle_pixels))

    if matrix:
        w, h = int(matrix[0]), int(matrix[1])
        start = addr_alloc.take(w * h)
        commands.append(ptr_addr("MATRIX1", "MATRIX", "MATRIX1", bus_name, start, w * h))

    commands.append("COMMIT")

    meta = {
        "name": name or "ADDR_FX",
        "family": "ADDR_FX",
        "button_mode": "NONE",
        "button_count": 0,
        "strips": strips,
        "strip_pixels": strip_pixels,
        "circles": circles,
        "circle_pixels": circle_pixels,
        "matrix": matrix,
        "bus_gpio": bus_gpio,
        "bus_pixels": total_pixels,
        "used_gpios": sorted(_used_gpios(commands)),
        "description": description or "Profil effets adressables sans boutons.",
    }
    return {"meta": meta, "commands": commands}


# ------------------------------------------------------------
# Base de profils
# ------------------------------------------------------------

HARDWARE_PROFILES = {}
PROFILE_ALIASES = {}


def register(name, profile, aliases=None):
    name = str(name).upper()
    profile["meta"]["name"] = name
    _validate_profile(name, profile)
    HARDWARE_PROFILES[name] = profile
    for alias in aliases or []:
        PROFILE_ALIASES[str(alias).upper()] = name


# ------------------------------------------------------------
# 1) Boutons GPIO directs : 2 / 4 / 6 / 8 boutons RGB
# ------------------------------------------------------------

for n in (2, 4, 6, 8):
    register(
        "GPIO_%dB" % n,
        build_gpio_profile(n, start_select="NONE",
                           description="%d boutons RGB GPIO directs, sans START/SELECT." % n),
    )
    register(
        "GPIO_%dB_SS_GPIO" % n,
        build_gpio_profile(n, start_select="GPIO",
                           description="%d boutons RGB GPIO directs + START/SELECT ONOFF." % n),
        aliases=["%dB_GPIO_SS" % n],
    )

# START/SELECT RGB seulement si le total GPIO reste raisonnable.
for n in (2, 4, 6):
    register(
        "GPIO_%dB_SS_RGB" % n,
        build_gpio_profile(n, start_select="RGB",
                           description="%d boutons RGB GPIO directs + START/SELECT RGB." % n),
    )

# Joysticks GPIO simples ou RGB.
register("GPIO_2B_SS_GPIO_1JOY_GPIO", build_gpio_profile(2, start_select="GPIO", joysticks=1, joystick_mode="GPIO"))
register("GPIO_2B_SS_GPIO_2JOY_GPIO", build_gpio_profile(2, start_select="GPIO", joysticks=2, joystick_mode="GPIO"))
register("GPIO_2B_SS_GPIO_1JOY_RGB", build_gpio_profile(2, start_select="GPIO", joysticks=1, joystick_mode="RGB"))
register("GPIO_2B_SS_GPIO_2JOY_RGB", build_gpio_profile(2, start_select="GPIO", joysticks=2, joystick_mode="RGB"))

register("GPIO_4B_SS_GPIO_1JOY_GPIO", build_gpio_profile(4, start_select="GPIO", joysticks=1, joystick_mode="GPIO"))
register("GPIO_4B_SS_GPIO_2JOY_GPIO", build_gpio_profile(4, start_select="GPIO", joysticks=2, joystick_mode="GPIO"))
register("GPIO_4B_SS_GPIO_1JOY_RGB", build_gpio_profile(4, start_select="GPIO", joysticks=1, joystick_mode="RGB"))
register("GPIO_4B_SS_GPIO_2JOY_RGB", build_gpio_profile(4, start_select="GPIO", joysticks=2, joystick_mode="RGB"))
register("GPIO_4B_SS_RGB_1JOY_RGB", build_gpio_profile(4, start_select="RGB", joysticks=1, joystick_mode="RGB"))
register("GPIO_4B_SS_RGB_2JOY_RGB", build_gpio_profile(4, start_select="RGB", joysticks=2, joystick_mode="RGB"))

register("GPIO_6B_SS_GPIO_1JOY_GPIO", build_gpio_profile(6, start_select="GPIO", joysticks=1, joystick_mode="GPIO"))
register("GPIO_6B_SS_GPIO_2JOY_GPIO", build_gpio_profile(6, start_select="GPIO", joysticks=2, joystick_mode="GPIO"))
register("GPIO_6B_SS_GPIO_1JOY_RGB", build_gpio_profile(6, start_select="GPIO", joysticks=1, joystick_mode="RGB"))
register("GPIO_6B_SS_GPIO_2JOY_RGB", build_gpio_profile(6, start_select="GPIO", joysticks=2, joystick_mode="RGB"), aliases=["6B_GPIO_SS_2JOY_RGB"])

# 8 boutons RGB + START/SELECT ONOFF consomme déjà les 26 GPIO utiles du Pico.
# Il n'y a donc pas de variante 8B + START/SELECT + joystick GPIO direct.


# ------------------------------------------------------------
# 2) Boutons adressables : 2 / 4 / 6 / 8 boutons ADDR
# ------------------------------------------------------------

for n in (2, 4, 6, 8):
    register(
        "ADDR_%dB" % n,
        build_addr_buttons_profile(n, start_select="NONE",
                                   description="%d boutons adressables, sans START/SELECT." % n),
    )
    register(
        "ADDR_%dB_SS_ADDR" % n,
        build_addr_buttons_profile(n, start_select="ADDR",
                                   description="%d boutons adressables + START/SELECT adressables." % n),
    )
    register(
        "ADDR_%dB_SS_GPIO" % n,
        build_addr_buttons_profile(n, start_select="GPIO",
                                   description="%d boutons adressables + START/SELECT ONOFF GPIO." % n),
        aliases=["%dB_ADDR_SS_GPIO" % n],
    )
    register(
        "ADDR_%dB_SS_ADDR_1JOY_ADDR" % n,
        build_addr_buttons_profile(n, start_select="ADDR", joysticks=1, joystick_mode="ADDR"),
    )
    register(
        "ADDR_%dB_SS_ADDR_2JOY_ADDR" % n,
        build_addr_buttons_profile(n, start_select="ADDR", joysticks=2, joystick_mode="ADDR"),
    )
    register(
        "ADDR_%dB_SS_GPIO_1JOY_ADDR" % n,
        build_addr_buttons_profile(n, start_select="GPIO", joysticks=1, joystick_mode="ADDR"),
    )
    register(
        "ADDR_%dB_SS_GPIO_2JOY_ADDR" % n,
        build_addr_buttons_profile(n, start_select="GPIO", joysticks=2, joystick_mode="ADDR"),
    )


# ------------------------------------------------------------
# 3) Bandeaux LED, circles / rings et matrices WS2812
# ------------------------------------------------------------

for count in (1, 2, 3, 4):
    register(
        "ADDR_%dSTRIP" % count,
        build_addr_fx_profile(strips=count, strip_pixels=30,
                              description="%d bandeau(x) LED adressable(s), 30 pixels chacun." % count),
    )
    register(
        "ADDR_%dCIRCLE" % count,
        build_addr_fx_profile(circles=count, circle_pixels=16,
                              description="%d circle(s)/ring(s) LED adressable(s), 16 pixels chacun." % count),
    )
    register(
        "ADDR_%dSTRIP_%dCIRCLE" % (count, count),
        build_addr_fx_profile(strips=count, strip_pixels=30, circles=count, circle_pixels=16,
                              description="%d bandeau(x) + %d circle(s) adressables." % (count, count)),
    )

register("ADDR_MATRIX_8X8", build_addr_fx_profile(matrix=(8, 8), description="Matrice WS2812 8x8, 64 pixels."))
register("ADDR_MATRIX_16X16", build_addr_fx_profile(matrix=(16, 16), description="Matrice WS2812 16x16, 256 pixels."))
register("ADDR_MATRIX_32X32", build_addr_fx_profile(matrix=(32, 32), description="Matrice WS2812 32x32, 1024 pixels."))


# ------------------------------------------------------------
# 4) Combinaisons réalistes prêtes à l'emploi
# ------------------------------------------------------------

register(
    "GPIO_6B_SS_GPIO_1JOY_RGB_2STRIP",
    build_gpio_profile(6, start_select="GPIO", joysticks=1, joystick_mode="RGB",
                       strips=2, strip_pixels=30, bus_gpio=28,
                       description="6 boutons RGB GPIO + START/SELECT GPIO + 1 joystick RGB + 2 bandeaux adressables."),
)

register(
    "GPIO_4B_SS_RGB_2JOY_RGB_2STRIP_2CIRCLE",
    build_gpio_profile(4, start_select="RGB", joysticks=2, joystick_mode="RGB",
                       strips=2, strip_pixels=30, circles=2, circle_pixels=16, bus_gpio=28,
                       description="4 boutons RGB GPIO + START/SELECT RGB + 2 joysticks RGB + 2 bandeaux + 2 circles adressables."),
)

register(
    "ADDR_8B_SS_GPIO_2JOY_ADDR_2STRIP_2CIRCLE",
    build_addr_buttons_profile(8, start_select="GPIO", joysticks=2, joystick_mode="ADDR",
                               strips=2, strip_pixels=30, circles=2, circle_pixels=16,
                               bus_gpio=22,
                               description="8 boutons adressables + START/SELECT GPIO + 2 joysticks adressables + 2 bandeaux + 2 circles."),
    aliases=["8B_ADDR_SS_GPIO_FULL"],
)

register(
    "ADDR_8B_SS_ADDR_2JOY_ADDR_4STRIP_4CIRCLE",
    build_addr_buttons_profile(8, start_select="ADDR", joysticks=2, joystick_mode="ADDR",
                               strips=4, strip_pixels=30, circles=4, circle_pixels=16,
                               bus_gpio=22,
                               description="8 boutons adressables + START/SELECT adressables + 2 joysticks + 4 bandeaux + 4 circles."),
)

register(
    "ADDR_6B_SS_GPIO_2JOY_ADDR_MATRIX8X8",
    build_addr_buttons_profile(6, start_select="GPIO", joysticks=2, joystick_mode="ADDR",
                               matrix=(8, 8), bus_gpio=22,
                               description="6 boutons adressables + START/SELECT GPIO + 2 joysticks adressables + matrice 8x8."),
)

register(
    "ADDR_8B_SS_GPIO_MATRIX16X16",
    build_addr_buttons_profile(8, start_select="GPIO", matrix=(16, 16), bus_gpio=22,
                               description="8 boutons adressables + START/SELECT GPIO + matrice 16x16."),
)

register(
    "ADDR_8B_SS_ADDR_MATRIX32X32",
    build_addr_buttons_profile(8, start_select="ADDR", matrix=(32, 32), bus_gpio=22,
                               description="8 boutons adressables + START/SELECT adressables + matrice 32x32."),
)


# ------------------------------------------------------------
# API publique
# ------------------------------------------------------------

def normalize_profile_name(name):
    key = str(name or "").strip().upper()
    return PROFILE_ALIASES.get(key, key)


def list_profiles(family=None):
    names = sorted(HARDWARE_PROFILES.keys())
    if family:
        family = str(family).upper()
        names = [n for n in names if HARDWARE_PROFILES[n]["meta"].get("family") == family]
    return names


def get_profile(name):
    key = normalize_profile_name(name)
    if key not in HARDWARE_PROFILES:
        raise KeyError("Profil matériel inconnu: %s" % name)
    return HARDWARE_PROFILES[key]


def get_init_commands(name):
    return list(get_profile(name)["commands"])


def get_profile_meta(name):
    return dict(get_profile(name)["meta"])


def apply_hardware_profile(name, send_line):
    """
    Envoie un profil à une fonction send_line.

    Côté firmware Pico :
      apply_hardware_profile("GPIO_8B_SS_GPIO", handle)

    Côté PC :
      apply_hardware_profile("GPIO_8B_SS_GPIO", lambda s: serial.write((s+"\n").encode()))
    """
    for line in get_init_commands(name):
        send_line(line)


def print_profile(name):
    for line in get_init_commands(name):
        print(line)


def describe_profile(name):
    meta = get_profile_meta(name)
    lines = []
    lines.append("%s" % meta.get("name"))
    lines.append("  %s" % meta.get("description", ""))
    lines.append("  family: %s" % meta.get("family"))
    lines.append("  buttons: %s %s" % (meta.get("button_count"), meta.get("button_mode")))
    if meta.get("start_select"):
        lines.append("  start/select: %s" % meta.get("start_select"))
    if meta.get("joysticks"):
        lines.append("  joysticks: %s %s" % (meta.get("joysticks"), meta.get("joystick_mode")))
    if meta.get("strips"):
        lines.append("  strips: %s" % meta.get("strips"))
    if meta.get("circles"):
        lines.append("  circles: %s" % meta.get("circles"))
    if meta.get("matrix"):
        lines.append("  matrix: %sx%s" % (meta.get("matrix")[0], meta.get("matrix")[1]))
    if meta.get("bus_pixels"):
        lines.append("  bus pixels: %s" % meta.get("bus_pixels"))
    lines.append("  gpios: %s" % ",".join("GP%d" % gp for gp in meta.get("used_gpios", [])))
    return "\n".join(lines)


# ------------------------------------------------------------
# CLI CPython utile côté Windows / PC :
#   python hardware_profiles.py list
#   python hardware_profiles.py describe GPIO_8B_SS_GPIO
#   python hardware_profiles.py GPIO_8B_SS_GPIO
# ------------------------------------------------------------

if __name__ == "__main__":
    import sys

    if len(sys.argv) <= 1 or sys.argv[1].lower() in ("list", "ls"):
        for profile_name in list_profiles():
            print(profile_name)
        raise SystemExit(0)

    if sys.argv[1].lower() in ("describe", "desc", "info"):
        if len(sys.argv) < 3:
            print("Usage: python hardware_profiles.py describe PROFILE_NAME")
            raise SystemExit(1)
        print(describe_profile(sys.argv[2]))
        raise SystemExit(0)

    print_profile(sys.argv[1])
