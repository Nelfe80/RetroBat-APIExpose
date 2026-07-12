"""mem-curator v5 : generateur de fichiers .MEM pour APIExpose/RetroBat.

Successeur du curator v4 (DOFLinx_V909/API/FINAL). Le contrat de sortie est
aligne sur le parseur runtime qui fait foi : plugins/Wrapper/wrapper.cpp
(LoadMemFile + ProcessWatchValue), puis MameLuaIngameProvider.cs.

Deltas v4 -> v5 : voir README.md dans ce dossier.
"""

import argparse
import copy
import glob
import json
import os
import re
import sys
import time
import unicodedata
from collections import OrderedDict

try:
    import requests
except ImportError:  # requests n'est requis que pour la passe LLM
    requests = None

VERSION = "5.0.0"
GENERATED_STAMP = time.strftime("%Y-%m-%d")


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


def _default_source_base():
    """Racine des sources de curation (contient sources/ra, sources/doflinx...).

    Ordre : env AG_SOURCE_BASE, fichier local .source-base.local (non
    versionne, une ligne = chemin), dossier parent du script.
    """
    candidates = [os.environ.get("AG_SOURCE_BASE", "")]
    local_cfg = os.path.join(SCRIPT_DIR, ".source-base.local")
    if os.path.exists(local_cfg):
        with open(local_cfg, "r", encoding="utf-8-sig") as fh:
            candidates.append(fh.read().strip())
    candidates.append(os.path.abspath(os.path.join(SCRIPT_DIR, "..")))
    candidates.append(SCRIPT_DIR)
    for candidate in candidates:
        if candidate and os.path.isdir(os.path.join(candidate, "sources")):
            return candidate
    return SCRIPT_DIR


DEFAULT_SOURCE_BASE = _default_source_base()

SYSTEM_NAMES = {
    "3do": "3DO Interactive Multiplayer",
    "3ds": "Nintendo 3DS",
    "amstradcpc": "Amstrad CPC",
    "apple2": "Apple II",
    "arcade": "Arcade",
    "arcadia": "Emerson Arcadia 2001",
    "arduboy": "Arduboy",
    "atari2600": "Atari 2600",
    "atari7800": "Atari 7800",
    "channelf": "Fairchild Channel F",
    "colecovision": "ColecoVision",
    "dos": "DOS",
    "dreamcast": "Sega Dreamcast",
    "fds": "Famicom Disk System",
    "gamecube": "Nintendo GameCube",
    "gamegear": "Sega Game Gear",
    "gb": "Game Boy",
    "gba": "Game Boy Advance",
    "gbc": "Game Boy Color",
    "intellivision": "Mattel Intellivision",
    "jaguar": "Atari Jaguar",
    "jaguarcd": "Atari Jaguar CD",
    "lynx": "Atari Lynx",
    "mastersystem": "Sega Master System",
    "megacd": "Sega CD / Mega-CD",
    "megadrive": "Sega Genesis / Mega Drive",
    "megaduck": "Mega Duck / Cougar Boy",
    "msx1": "MSX",
    "n64": "Nintendo 64",
    "nds": "Nintendo DS",
    "neogeocd": "Neo Geo CD",
    "nes": "NES/Famicom",
    "ngp": "Neo Geo Pocket",
    "o2em": "Magnavox Odyssey 2 / Philips Videopac",
    "oricatmos": "Oric Atmos",
    "pc88": "NEC PC-8800",
    "pcengine": "PC Engine / TurboGrafx-16",
    "pcenginecd": "PC Engine CD / TurboGrafx-CD",
    "pcfx": "PC-FX",
    "pokemini": "Pokemon Mini",
    "ps2": "PlayStation 2",
    "psp": "PlayStation Portable",
    "psx": "PlayStation",
    "saturn": "Sega Saturn",
    "scv": "Super Cassette Vision",
    "sega32x": "Sega 32X",
    "sg1000": "Sega SG-1000",
    "snes": "SNES / Super Famicom",
    "supervision": "Watara Supervision",
    "tvgames": "TV Games",
    "uzebox": "Uzebox",
    "vc4000": "Interton VC 4000",
    "vectrex": "Vectrex",
    "virtualboy": "Virtual Boy",
    "wasm4": "WASM-4",
    "wii": "Nintendo Wii",
    "wswan": "WonderSwan",
}

SYSTEM_ENDIANNESS = {
    "gamecube": "be",
    "jaguar": "be",
    "jaguarcd": "be",
    "megacd": "be",
    "megadrive": "be",
    "n64": "be",
    "neogeocd": "be",
    "sega32x": "be",
    "vectrex": "be",
    "wii": "be",
}

DEFAULT_RETROBAT_GAMELIST_SYSTEMS = r"E:\RetroBat\plugins\APIExpose\resources\gamelist\systems"
DEFAULT_MAME_DIR = os.path.abspath(os.path.join(DEFAULT_SOURCE_BASE, "..", "MAME"))

FAMILY_ORDER = [
    "flow.lifecycle",
    "flow.settings",
    "flow.events",
    "progression.level",
    "progression.zone",
    "progression.stage",
    "resources.lives",
    "resources.health",
    "resources.secondary",
    "resources.environmental",
    "inventory.items",
    "inventory.weapon",
    "scoring.points",
    "scoring.collectibles",
    "scoring.experience",
    "combat.enemies",
    "combat.boss",
    "combat.tactical",
    "racing.vehicle",
    "state.temporary",
    "state.player",
    "state.mount",
    "world_interaction.objects",
    "system.movement",
    "system.timer",
    "system.memory",
    "system.unmapped",
    "system.internal",
]

ACTION_FAMILY = {
    "TITLE_SCREEN": "flow.lifecycle",
    "GAME_PLAYING": "flow.lifecycle",
    "GAME_OVER": "flow.lifecycle",
    "PAUSE_ON": "flow.lifecycle",
    "PAUSE_OFF": "flow.lifecycle",
    "DEMO_MODE": "flow.lifecycle",
    "CONTINUE_SCREEN": "flow.lifecycle",
    "CORPORATE_SCREEN": "flow.lifecycle",
    "CREDITS_SCREEN": "flow.lifecycle",
    "INTRO_SCREEN": "flow.lifecycle",
    "SELECT_SCREEN": "flow.lifecycle",
    "STAGE_SELECT": "progression.stage",
    "WORLD_MAP": "flow.lifecycle",
    "LOADING_SCREEN": "flow.lifecycle",
    "SETTINGS_CHANGED": "flow.settings",
    "MODE_STATE": "flow.settings",
    "EVENT_TRIGGER": "flow.events",
    "NEW_LEVEL": "progression.level",
    "LEVEL_CLEAR": "progression.level",
    "PROGRESSION_ZONE": "progression.zone",
    "PROGRESSION_STAGE": "progression.stage",
    "LAP_STATE": "progression.stage",
    "RANK_STATE": "progression.stage",
    "LIVES_STATE": "resources.lives",
    "LOSE_LIFE": "resources.lives",
    "GAIN_LIFE": "resources.lives",
    "UNIT_COUNT": "resources.lives",
    "UNIT_GAIN": "resources.lives",
    "UNIT_LOSE": "resources.lives",
    "HEALTH_STATE": "resources.health",
    "HIT": "resources.health",
    "HEAL": "resources.health",
    "LOW_HEALTH_WARN": "resources.health",
    "DROWNING": "resources.environmental",
    "RESOURCE_STATE": "resources.secondary",
    "RESOURCE_GAIN": "resources.secondary",
    "RESOURCE_LOSE": "resources.secondary",
    "COIN_GAIN": "scoring.collectibles",
    "COIN_LOSE": "scoring.collectibles",
    "MONEY_STATE": "scoring.collectibles",
    "AMMO_STATE": "scoring.collectibles",
    "AMMO_GAIN": "scoring.collectibles",
    "AMMO_LOSE": "scoring.collectibles",
    "SCORE_STATE": "scoring.points",
    "EXPERIENCE_STATE": "scoring.experience",
    "INVENTORY_ITEM": "inventory.items",
    "DYNAMIC_INVENTORY": "inventory.items",
    "TREASURE": "inventory.items",
    "KEY_GET": "inventory.items",
    "WEAPON_UPGRADE": "inventory.weapon",
    "WEAPON_STATE": "inventory.weapon",
    "INVINCIBILITY_START": "state.temporary",
    "INVINCIBILITY_STOP": "state.temporary",
    "INVINCIBILITY_TIMER": "state.temporary",
    "SPEED_START": "state.temporary",
    "SPEED_STOP": "state.temporary",
    "SPEED_TIMER": "state.temporary",
    "SHIELD_GAIN": "state.temporary",
    "SHIELD_LOST": "state.temporary",
    "SHIELD_TIMER": "state.temporary",
    "STATUS_EFFECT_START": "state.temporary",
    "STATUS_EFFECT_STOP": "state.temporary",
    "TRANSFORMATION": "state.temporary",
    "SPECIAL_ACTION": "state.temporary",
    "JUMPING": "state.player",
    "RUNNING": "state.player",
    "CROUCHING": "state.player",
    "FALLING": "state.player",
    "SPINNING": "state.player",
    "SWIMMING": "state.player",
    "ATTACKING": "state.player",
    "MOUNT_START": "state.mount",
    "MOUNT_STOP": "state.mount",
    "MOUNT_STATE": "state.mount",
    "BOSS_HIT": "combat.enemies",
    "BOSS_DEFEATED": "combat.enemies",
    "ENEMY_HIT": "combat.enemies",
    "BOMB_FIRED": "combat.enemies",
    "BATTLE_START": "combat.tactical",
    "BATTLE_END": "combat.tactical",
    "CRITICAL_HIT": "combat.tactical",
    "PARRY_SUCCESS": "combat.tactical",
    "KO": "combat.tactical",
    "FATALITY": "combat.tactical",
    "CRASH": "racing.vehicle",
    "TURBO_BOOST": "racing.vehicle",
    "BALL_LOCK": "flow.events",
    "MULTIBALL_START": "flow.events",
    "GENERAL_TIMER": "system.timer",
    "LEVEL_TIMER": "system.timer",
    "BOMB_TIMER": "system.timer",
    "TIMER_LOW_WARN": "system.timer",
    "COMBO_HIT": "flow.events",
    "COMBO_TIMER": "system.timer",
    "SPEED_STATE": "system.movement",
    "OBJECT_INTERACTION": "world_interaction.objects",
    "OBJECT_INTERACTION_CHECKPOINT": "world_interaction.objects",
    "OBJECT_DESTROYED": "world_interaction.objects",
    "DOOR_OPENED": "world_interaction.objects",
    "ROOM_DISCOVERED": "world_interaction.objects",
    "ENVIRONMENT_FORCE": "world_interaction.objects",
    "UNKNOWN": "system.unmapped",
    "IGNORE": "system.internal",
}

COMPOSITE_PREFIX_FAMILY = {
    "LEVEL_": "progression.level",
    "TREASURE_": "inventory.items",
    "TRANSFORMATION_": "state.temporary",
    "PLAYER_STATE_": "state.temporary",
    "OBJECT_INTERACTION_": "world_interaction.objects",
    "ENVIRONMENT_": "world_interaction.objects",
}

PROFILE_HINTS = {
    "platformer": [
        "sonic",
        "mario",
        "bonk",
        "alex kidd",
        "wonder boy",
        "megaman",
        "mega man",
        "metroid",
        "castlevania",
        "donkey kong",
        "kirby",
        "yoshi",
        "gex",
    ],
    "racing": [
        "racing",
        "racer",
        "kart",
        "f-zero",
        "f zero",
        "gran turismo",
        "ridge racer",
        "virtua racing",
        "outrun",
        "road rash",
        "need for speed",
    ],
    "rpg": [
        "final fantasy",
        "dragon quest",
        "phantasy star",
        "pokemon",
        "zelda",
        "chrono",
        "fallout",
        "ys",
        "lunar",
        "shining",
        "secret of mana",
    ],
    "shmup": [
        "1942",
        "1943",
        "gradius",
        "rtype",
        "r-type",
        "thunder force",
        "space invaders",
        "galaga",
        "aero fighters",
    ],
    "puzzle": [
        "tetris",
        "columns",
        "puyo",
        "dr mario",
        "dr. mario",
        "arkanoid",
        "breakout",
        "2048",
        "puzzle",
    ],
    "fighting": [
        "street fighter",
        "mortal kombat",
        "tekken",
        "virtua fighter",
        "king of fighters",
        "samurai shodown",
    ],
}

PRIMARY_ACTIONS = {
    "TITLE_SCREEN",
    "DEMO_MODE",
    "CORPORATE_SCREEN",
    "GAME_PLAYING",
    "GAME_OVER",
    "CONTINUE_SCREEN",
    "PAUSE_ON",
    "PAUSE_OFF",
    "CREDITS_SCREEN",
    "NEW_LEVEL",
    "LEVEL_CLEAR",
    "PROGRESSION_STAGE",
    "PROGRESSION_ZONE",
    "HIT",
    "HEAL",
    "GAIN_LIFE",
    "LOSE_LIFE",
    "LIVES_STATE",
    "COIN_GAIN",
    "COIN_LOSE",
    "MONEY_STATE",
    "SCORE_STATE",
    "RANK_STATE",
    "LAP_STATE",
    "TREASURE",
    "KEY_GET",
    "WEAPON_UPGRADE",
    "BOSS_HIT",
    "BOSS_DEFEATED",
    "INVINCIBILITY_START",
    "INVINCIBILITY_STOP",
    "SHIELD_GAIN",
    "SHIELD_LOST",
    "SPEED_START",
    "SPEED_STOP",
    "OBJECT_INTERACTION_CHECKPOINT",
    "OBJECT_DESTROYED",
    "DOOR_OPENED",
}

GENRE_PRIMARY_ACTIONS = {
    "action": {
        "ATTACKING",
        "BOSS_HIT",
        "BOSS_DEFEATED",
        "ENEMY_HIT",
        "HEALTH_STATE",
        "OBJECT_INTERACTION",
        "RESOURCE_GAIN",
        "RESOURCE_LOSE",
        "SPECIAL_ACTION",
    },
    "adventure": {
        "DOOR_OPENED",
        "HEALTH_STATE",
        "INVENTORY_ITEM",
        "KEY_GET",
        "OBJECT_INTERACTION",
        "ROOM_DISCOVERED",
        "TREASURE",
        "WEAPON_STATE",
        "WEAPON_UPGRADE",
    },
    "beat_em_up": {
        "BATTLE_START",
        "BATTLE_END",
        "BOSS_HIT",
        "BOSS_DEFEATED",
        "ENEMY_HIT",
        "HEALTH_STATE",
        "HIT",
        "KO",
        "UNIT_COUNT",
        "WEAPON_STATE",
    },
    "board": {
        "GENERAL_TIMER",
        "LEVEL_TIMER",
        "MODE_STATE",
        "SCORE_STATE",
        "SETTINGS_CHANGED",
    },
    "casino": {
        "COIN_GAIN",
        "COIN_LOSE",
        "MONEY_STATE",
        "SCORE_STATE",
    },
    "educational": {
        "LEVEL_CLEAR",
        "NEW_LEVEL",
        "SCORE_STATE",
    },
    "platformer": {
        "LEVEL_TIMER",
        "GENERAL_TIMER",
        "DROWNING",
        "RESOURCE_GAIN",
        "RESOURCE_LOSE",
        "ENVIRONMENT_UNDERWATER",
        "ENVIRONMENT_FORCE",
        "OBJECT_INTERACTION",
    },
    "pinball": {
        "BALL_LOCK",
        "LEVEL_TIMER",
        "MULTIBALL_START",
        "SCORE_STATE",
        "SPECIAL_ACTION",
    },
    "racing": {
        "LEVEL_TIMER",
        "GENERAL_TIMER",
        "LAP_STATE",
        "RANK_STATE",
        "SPEED_STATE",
        "CRASH",
        "TURBO_BOOST",
        "OBJECT_INTERACTION_CHECKPOINT",
    },
    "rpg": {
        "HEALTH_STATE",
        "RESOURCE_STATE",
        "RESOURCE_GAIN",
        "RESOURCE_LOSE",
        "MONEY_STATE",
        "EXPERIENCE_STATE",
        "INVENTORY_ITEM",
        "TREASURE",
        "BATTLE_START",
        "BATTLE_END",
    },
    "music": {
        "COMBO_HIT",
        "COMBO_TIMER",
        "LEVEL_CLEAR",
        "SCORE_STATE",
    },
    "shmup": {
        "SCORE_STATE",
        "WEAPON_STATE",
        "WEAPON_UPGRADE",
        "BOMB_FIRED",
        "AMMO_STATE",
        "BOSS_HIT",
        "BOSS_DEFEATED",
    },
    "shooter": {
        "AMMO_STATE",
        "AMMO_GAIN",
        "AMMO_LOSE",
        "BOSS_HIT",
        "BOSS_DEFEATED",
        "ENEMY_HIT",
        "HEALTH_STATE",
        "WEAPON_STATE",
        "WEAPON_UPGRADE",
    },
    "simulation": {
        "RESOURCE_STATE",
        "RESOURCE_GAIN",
        "RESOURCE_LOSE",
        "SCORE_STATE",
        "UNIT_COUNT",
    },
    "sports": {
        "LEVEL_TIMER",
        "GENERAL_TIMER",
        "RANK_STATE",
        "SCORE_STATE",
    },
    "strategy": {
        "RESOURCE_STATE",
        "RESOURCE_GAIN",
        "RESOURCE_LOSE",
        "UNIT_COUNT",
        "UNIT_GAIN",
        "UNIT_LOSE",
        "BATTLE_START",
        "BATTLE_END",
    },
    "puzzle": {
        "LEVEL_TIMER",
        "GENERAL_TIMER",
        "SCORE_STATE",
        "LEVEL_CLEAR",
        "NEW_LEVEL",
        "COMBO_HIT",
        "COMBO_TIMER",
    },
    "fighting": {
        "HIT",
        "KO",
        "BATTLE_START",
        "BATTLE_END",
        "CRITICAL_HIT",
        "FATALITY",
        "LEVEL_TIMER",
    },
}

TELEMETRY_ACTIONS = {
    "INVINCIBILITY_TIMER",
    "SPEED_TIMER",
    "SHIELD_TIMER",
    "STATUS_EFFECT_TIMER",
    "COOLDOWN_TIMER",
    "COMBO_TIMER",
    "RESOURCE_STATE",
    "AMMO_STATE",
    "SPEED_STATE",
    "HEALTH_STATE",
    "LIVES_STATE",
    "MONEY_STATE",
    "SCORE_STATE",
}

RETROBAT_GENRE_PROFILE = {
    "action": "action",
    "action adventure": "adventure",
    "adventure": "adventure",
    "beat em up": "beat_em_up",
    "beat them up": "beat_em_up",
    "board game": "board",
    "breakout games": "puzzle",
    "casino": "casino",
    "climbing": "platformer",
    "construction and management simulation": "simulation",
    "educational": "educational",
    "fighting": "fighting",
    "flight combat": "shmup",
    "labyrinth": "adventure",
    "mahjong": "board",
    "music": "music",
    "pinball": "pinball",
    "platform": "platformer",
    "run and jump": "platformer",
    "run jump": "platformer",
    "racing": "racing",
    "driving": "racing",
    "role-playing": "rpg",
    "role playing": "rpg",
    "rpg": "rpg",
    "role playing game": "rpg",
    "shoot'em up": "shmup",
    "shoot em up": "shmup",
    "shoot them up": "shmup",
    "shmup": "shmup",
    "shooter": "shooter",
    "platform shooter": "shooter",
    "light gun": "shooter",
    "gun": "shooter",
    "puzzle": "puzzle",
    "quiz": "puzzle",
    "sports": "sports",
    "sport": "sports",
    "simulation": "simulation",
    "simulator": "simulation",
    "strategy": "strategy",
    "tactical": "strategy",
    "wargame": "strategy",
}

GENRE_PROFILE_HINTS = {
    "platformer": ("platform", "run and jump", "climbing"),
    "racing": ("racing", "driving", "motorcycle", "boat", "plane", "vehicle"),
    "rpg": ("role playing", "rpg", "dungeon", "rogue"),
    "shmup": ("shoot em up", "shoot'em up", "flight combat", "space shooter"),
    "shooter": ("shooter", "gun", "fps", "first person shooter", "third person shooter"),
    "fighting": ("fighting", "versus", "martial"),
    "beat_em_up": ("beat em up", "beat them up", "belt scroll", "brawler"),
    "sports": ("sports", "football", "soccer", "baseball", "basketball", "tennis", "golf", "hockey"),
    "puzzle": ("puzzle", "quiz", "breakout", "tile", "falling block"),
    "board": ("board", "mahjong", "chess", "cards"),
    "casino": ("casino", "poker", "slot"),
    "pinball": ("pinball",),
    "music": ("music", "rhythm", "dance"),
    "adventure": ("adventure", "visual novel", "interactive movie", "survival horror", "point and click"),
    "simulation": ("simulation", "management", "flight simulator", "life simulator"),
    "strategy": ("strategy", "tactical", "wargame", "real time strategy", "turn based strategy"),
    "educational": ("educational", "edutainment", "training"),
    "action": ("action", "arcade"),
}

GENRE_MECHANIC_TAG_HINTS = {
    "ammo": ("ammo", "gun", "shooter", "light gun"),
    "combat": ("action", "beat", "fighting", "shooter", "combat", "boss"),
    "collectibles": ("platform", "collect", "coin", "maze", "casino"),
    "environment": ("adventure", "survival", "labyrinth", "platform"),
    "inventory": ("adventure", "role playing", "rpg", "survival", "dungeon"),
    "movement": ("platform", "racing", "driving", "sports"),
    "score": ("action", "arcade", "puzzle", "sports", "pinball", "casino", "music"),
    "timer": ("racing", "sports", "fighting", "puzzle", "platform", "music"),
    "units": ("strategy", "simulation", "management"),
}

PROFILE_PRIORITY = [
    "platformer",
    "racing",
    "rpg",
    "shmup",
    "shooter",
    "beat_em_up",
    "fighting",
    "sports",
    "puzzle",
    "pinball",
    "music",
    "adventure",
    "action",
    "simulation",
    "strategy",
    "board",
    "casino",
    "educational",
    "generic",
]

PRIMARY_TIMER_PROFILES = {
    "beat_em_up",
    "fighting",
    "music",
    "platformer",
    "puzzle",
    "racing",
    "sports",
}

PRIMARY_LEVEL_PROFILES = PRIMARY_TIMER_PROFILES.union({"adventure", "action", "shmup", "shooter"})
PRIMARY_TREASURE_PROFILES = {"adventure", "action", "platformer", "rpg"}
PRIMARY_OBJECT_PROFILES = {"adventure", "action", "platformer", "rpg"}
PRIMARY_RESOURCE_PROFILES = {"action", "adventure", "racing", "rpg", "shmup", "shooter", "strategy", "simulation"}
PLAYER_ACTIONS = {"JUMPING", "RUNNING", "CROUCHING", "FALLING", "SPINNING", "SWIMMING", "ATTACKING"}
PLAYER_IDENTITY_ACTIONS = {"PLAYER_STATE", "CURRENT_PLAYER", "ACTIVE_PLAYER", "PLAYER_TURN"}
COLOR_HINTS = {
    "blue": ("blue", "dodger blue", "dodger_blue"),
    "cyan": ("cyan", "light cyan", "light_cyan"),
    "green": ("green", "dark green", "dark_green"),
    "orange": ("orange", "orange red", "orange_red"),
    "pink": ("pink", "deep pink", "deep_pink"),
    "red": ("red", "dark red", "dark_red"),
    "silver": ("silver",),
    "white": ("white",),
    "yellow": ("yellow",),
}

ALLOWED_CONDITIONS = {"eq", "neq", "change", "increase", "decrease", "bit_true", "bit_false", "any"}
NOISE_WORDS = re.compile(
    r"\b(pointer|position|x-position|y-position|x position|y position|coordinate|screen x|screen y|camera|scroll|velocity|gravity|subpixel|unused|debug|checksum|game genie|moon jump|button input|controller status|new buttons pressed)\b",
    re.I,
)
COUNTER_NOISE_RE = re.compile(
    r"\b(score|level|stage|screen|sound|music|sfx|song|timer|counter|sprite|object|enemy|active level data|level complete flags)\b.*\b(start|end)\b"
    r"|\b(start|end)\b.*\b(score|level|stage|screen|sound|music|sfx|song|timer|counter|sprite|object|enemy|active level data|level complete flags)\b",
    re.I,
)
EXCLUDED_SOURCE_RE = re.compile(r"^(?:z-)?(hack|prototype|homebrew|unlicensed|unl|test-kit)-", re.I)
# Race position / rank notes (kart, racing). "position" alone is caught by NOISE_WORDS, so these
# must be recognised explicitly and exempted, while coordinate/item positions stay noise.
RACE_RANK_RE = re.compile(
    r"\b(current\s+position|player\s+position|position\s+or\s+place\s+in\s+race|place\s+in\s+race|position\s+in\s+race|race\s+position|current\s+place|current\s+rank|race\s+rank)\b",
    re.I,
)
RACE_RANK_EXCLUDE_RE = re.compile(
    r"\b(x[-\s]?position|y[-\s]?position|item\s+position|location|screen|camera|sprite|proximity|first\s+item|second\s+item)\b",
    re.I,
)
GAMELIST_PATH_CACHE = {}
GAMELIST_ENTRY_CACHE = {}


def load_text(path):
    if not path or not os.path.exists(path):
        return ""
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def save_text(path, content):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(content)


def load_json(path, default=None):
    if default is None:
        default = {}
    text = load_text(path)
    if not text:
        return default
    try:
        return json.loads(text)
    except Exception:
        return default


def normalize_hex(value, width=0):
    if value is None:
        return ""
    text = str(value).strip().strip('"').strip("'")
    if not text:
        return ""
    text = text.replace("00x", "0x").replace("00X", "0X")
    match = re.search(r"0[xX]([0-9A-Fa-f]+)", text)
    if match:
        digits = match.group(1).upper()
    else:
        match = re.search(r"([0-9A-Fa-f]+)$", text)
        if not match:
            return ""
        digits = match.group(1).upper()
    if width:
        digits = digits.zfill(width)
    return "0X" + digits


def parse_int(value):
    hx = normalize_hex(value)
    if hx:
        return int(hx[2:], 16)
    try:
        return int(str(value).strip())
    except Exception:
        return None


def arcade_universal_address(address):
    value = parse_int(address)
    if value is None:
        return address
    if 0xE000 <= value <= 0xEFFF:
        return f"0X{value - 0xE000:X}"
    if 0xF000 <= value <= 0xFFFF:
        return f"0X{value - 0xD000:X}"
    return f"0X{value:X}"


def strip_ra_id(slug):
    return re.sub(r"-\d+$", "", slug or "")


def slugify_title(title):
    text = unicodedata.normalize("NFKD", title or "").encode("ascii", "ignore").decode("ascii").lower()
    text = re.sub(r"\([^)]*\)", "", text)
    text = re.sub(r"['`]", "", text)
    text = re.sub(r"[^a-z0-9]+", "-", text)
    text = re.sub(r"-+", "-", text).strip("-")
    return text or "unknown-game"


def clean_slug_from_ra(path, data):
    filename_slug = os.path.splitext(os.path.basename(path))[0]
    stripped = strip_ra_id(filename_slug)
    title_slug = slugify_title(data.get("title") or stripped)
    if filename_slug.startswith(("hack-", "prototype-", "unlicensed-", "homebrew-")):
        return stripped
    return title_slug or stripped


def normalize_type(note_type, system="", context=""):
    text = f"{note_type or ''} {context or ''}".lower()
    explicit = re.search(r"\b(8|16|24|32)[-\s]*bit\s*(be|le|big[-\s]*endian|little[-\s]*endian)?", text)
    if explicit:
        bits = explicit.group(1)
        endian_text = explicit.group(2) or ""
        if bits == "8":
            return "u8"
        if endian_text.startswith("be") or "big" in endian_text:
            return f"u{bits}be"
        if endian_text.startswith("le") or "little" in endian_text:
            return f"u{bits}le"
    endian = SYSTEM_ENDIANNESS.get(system, "le")
    if "32" in text:
        return f"u32{endian}"
    if "24" in text:
        return f"u24{endian}"
    if "16" in text:
        return f"u16{endian}"
    return "u8"


def normalize_hash_label(label):
    return (label or "").strip()


def normalize_match_key(text):
    text = unicodedata.normalize("NFKD", text or "").encode("ascii", "ignore").decode("ascii").lower()
    text = re.sub(r"\([^)]*\)", " ", text)
    text = re.sub(r"\[[^\]]*\]", " ", text)
    text = text.replace("&", " and ")
    text = re.sub(r"['`]", "", text)
    text = re.sub(r"[^a-z0-9]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def unique_keep_order(items):
    out = []
    seen = set()
    for item in items:
        if not item or item in seen:
            continue
        seen.add(item)
        out.append(item)
    return out


def genre_to_profiles_and_tags(genre):
    low = normalize_match_key(genre)
    profiles = []
    tags = []
    if not low:
        return profiles, tags

    for key, profile in RETROBAT_GENRE_PROFILE.items():
        normalized_key = normalize_match_key(key)
        if low == normalized_key or normalized_key in low:
            profiles.append(profile)

    for profile, hints in GENRE_PROFILE_HINTS.items():
        if any(hint in low for hint in hints):
            profiles.append(profile)

    for tag, hints in GENRE_MECHANIC_TAG_HINTS.items():
        if any(hint in low for hint in hints):
            tags.append(tag)

    if "platform" in low and "shooter" in low:
        profiles.extend(["platformer", "shooter"])
        tags.extend(["movement", "ammo", "combat"])
    if "survival horror" in low:
        profiles.extend(["adventure", "shooter"])
        tags.extend(["ammo", "environment", "inventory"])
    if "racing" in low or "driving" in low:
        tags.extend(["movement", "timer"])

    return unique_keep_order(profiles), unique_keep_order(tags)


def genre_to_profile(genre):
    profiles, _tags = genre_to_profiles_and_tags(genre)
    return profiles[0] if profiles else ""


def pick_profile(profiles):
    profiles = [p for p in profiles if p]
    if not profiles:
        return "generic"
    for profile in PROFILE_PRIORITY:
        if profile in profiles:
            return profile
    return profiles[0]


def build_genre_context(genres, source="", match="", confidence="", fallback_profile=""):
    profiles = []
    tags = []
    for genre in genres or []:
        genre_profiles, genre_tags = genre_to_profiles_and_tags(genre)
        profiles.extend(genre_profiles)
        tags.extend(genre_tags)
    if fallback_profile and fallback_profile != "generic":
        profiles.append(fallback_profile)

    profiles = unique_keep_order(profiles)
    tags = unique_keep_order(tags)
    primary = pick_profile(profiles)
    secondary = [profile for profile in profiles if profile != primary]
    return {
        "raw_cats": [str(genre) for genre in genres or [] if str(genre).strip()],
        "primary_profile": primary,
        "secondary_profiles": secondary,
        "profiles": profiles,
        "mechanic_tags": tags,
        "source": source or "",
        "match": match or "",
        "confidence": confidence or "",
    }


def gamelist_jsonl_path(system, gamelist_dir):
    if not gamelist_dir:
        return ""
    cache_key = (os.path.abspath(gamelist_dir), system)
    if cache_key in GAMELIST_PATH_CACHE:
        return GAMELIST_PATH_CACHE[cache_key]
    aliases_path = os.path.join(gamelist_dir, "aliases.json")
    aliases = load_json(aliases_path, {})
    if isinstance(aliases, dict):
        alias = aliases.get(system)
        if isinstance(alias, dict) and alias.get("jsonl"):
            path = os.path.join(gamelist_dir, alias["jsonl"])
            if os.path.exists(path):
                GAMELIST_PATH_CACHE[cache_key] = path
                return path
    direct = os.path.join(gamelist_dir, f"{system}_lt.json")
    path = direct if os.path.exists(direct) else ""
    GAMELIST_PATH_CACHE[cache_key] = path
    return path


def gamelist_entries(path):
    if not path:
        return []
    cache_key = os.path.abspath(path)
    if cache_key in GAMELIST_ENTRY_CACHE:
        return GAMELIST_ENTRY_CACHE[cache_key]
    entries = []
    try:
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    entries.append(json.loads(line))
                except Exception:
                    continue
    except OSError:
        entries = []
    GAMELIST_ENTRY_CACHE[cache_key] = entries
    return entries


def ra_hashes(data):
    hashes = set()
    for item in data.get("hashes") or []:
        if not isinstance(item, dict):
            continue
        for key in ("hash", "md5", "sha1", "crc"):
            value = (item.get(key) or "").strip().lower()
            if value:
                hashes.add(value)
    return hashes


def retrobat_entry_hashes(entry):
    cached = entry.get("_ag_hashes") if isinstance(entry, dict) else None
    if isinstance(cached, set):
        return cached
    hashes = set()
    for item in entry.get("hsh") or []:
        if not isinstance(item, dict):
            continue
        for key in ("md5", "sha1", "crc"):
            value = (item.get(key) or "").strip().lower()
            if value:
                hashes.add(value)
    entry["_ag_hashes"] = hashes
    return hashes


def retrobat_entry_keys(entry):
    cached = entry.get("_ag_keys") if isinstance(entry, dict) else None
    if isinstance(cached, set):
        return cached
    keys = set()
    for key in ("id", "grp", "n", "fn", "set"):
        value = entry.get(key)
        if value:
            keys.add(normalize_match_key(str(value)))
            keys.add(slugify_title(str(value)).replace("-", " "))
    ra = entry.get("ra")
    if isinstance(ra, dict) and ra.get("n"):
        keys.add(normalize_match_key(str(ra.get("n"))))
    for alias in entry.get("aka") or []:
        if not isinstance(alias, dict):
            continue
        for key in ("id", "n", "fn", "set"):
            value = alias.get(key)
            if value:
                keys.add(normalize_match_key(str(value)))
                keys.add(slugify_title(str(value)).replace("-", " "))
    keys = {key for key in keys if key}
    entry["_ag_keys"] = keys
    return keys


def retrobat_target_keys(data, slug):
    title = data.get("title") or slug
    target_keys = {
        normalize_match_key(title),
        normalize_match_key(slug),
        slugify_title(title).replace("-", " "),
        slug.replace("-", " "),
        strip_ra_id(slug).replace("-", " "),
    }
    for item in data.get("hashes") or []:
        if isinstance(item, dict) and item.get("label"):
            label = os.path.splitext(item.get("label"))[0]
            target_keys.add(normalize_match_key(label))
            target_keys.add(slugify_title(label).replace("-", " "))
    return {key for key in target_keys if key}


def retrobat_entry_is_official(entry):
    flags = {str(item).lower() for item in entry.get("flg") or []}
    bad_flags = {"hack", "prototype", "beta", "trainer", "homebrew", "unlicensed", "demo"}
    if flags.intersection(bad_flags):
        return False
    if str(entry.get("t") or "").lower() in {"hack", "prototype", "homebrew", "unlicensed"}:
        return False
    if str(entry.get("rk") or "").lower() in {"hack", "prototype", "beta", "homebrew", "unlicensed"}:
        return False
    if str(entry.get("bld") or "").lower() in {"beta", "prototype"}:
        return False
    return True


def source_slug_is_variant(slug, data):
    text = f"{slug} {data.get('title') or ''}".lower()
    return bool(
        re.search(r"\b(subset|hack|prototype|homebrew|unlicensed|trainer|beta)\b", text)
        or text.startswith(("z-", "hack-", "prototype-", "homebrew-", "unlicensed-"))
    )


def retrobat_match_score(entry, data, slug, target_hashes, target_keys, game_id):
    score = 0
    common_hashes = target_hashes.intersection(retrobat_entry_hashes(entry))
    if common_hashes:
        score += 1000 + (10 * len(common_hashes))
    ra = entry.get("ra")
    if game_id is not None and isinstance(ra, dict) and str(ra.get("id") or "") == str(game_id):
        score += 500
    common_keys = target_keys.intersection(retrobat_entry_keys(entry))
    if common_keys:
        score += 100 + (5 * len(common_keys))
    if normalize_match_key(entry.get("n") or "") == normalize_match_key(data.get("title") or ""):
        score += 80
    return score


def retrobat_match_kind(entry, data, target_hashes, target_keys, game_id, score):
    if target_hashes.intersection(retrobat_entry_hashes(entry)):
        return "hash", "high"
    ra = entry.get("ra")
    if game_id is not None and isinstance(ra, dict) and str(ra.get("id") or "") == str(game_id):
        return "ra.id", "high"
    if target_keys.intersection(retrobat_entry_keys(entry)):
        return "normalized-title", "medium"
    if normalize_match_key(entry.get("n") or "") == normalize_match_key(data.get("title") or ""):
        return "title", "medium"
    if score >= 500:
        return "mixed", "medium"
    return "weak", "low"


def find_retrobatofficial_match(data, system, slug, gamelist_dir):
    path = gamelist_jsonl_path(system, gamelist_dir)
    if not path:
        return "", [], None, 0, set(), set(), None

    target_hashes = ra_hashes(data)
    target_keys = retrobat_target_keys(data, slug)
    game_id = data.get("game_id")
    entries = gamelist_entries(path)

    best = None
    best_score = 0
    for entry in entries:
        score = retrobat_match_score(entry, data, slug, target_hashes, target_keys, game_id)
        if score > best_score:
            best = entry
            best_score = score
    return path, entries, best, best_score, target_hashes, target_keys, game_id


def load_retrobatofficial_genres(data, system, slug, gamelist_dir):
    path, _entries, best, best_score, target_hashes, target_keys, game_id = find_retrobatofficial_match(
        data, system, slug, gamelist_dir
    )
    if not best or best_score < 100:
        return [], "", "", ""
    genres = [str(item) for item in best.get("cat") or [] if item]
    source = f"{os.path.basename(path)}:{best.get('id') or best.get('n') or 'match'}"
    match, confidence = retrobat_match_kind(best, data, target_hashes, target_keys, game_id, best_score)
    return genres, source, match, confidence


def infer_profile_from_ra(data):
    profiles = []
    categories = " ".join(str(note.get("category") or "") for note in data.get("code_notes") or [] if isinstance(note, dict)).lower()
    names = " ".join(str(note.get("name") or "") for note in data.get("code_notes") or [] if isinstance(note, dict)).lower()
    text = f"{data.get('title') or ''} {categories} {names}".lower()

    for profile, hints in PROFILE_HINTS.items():
        if any(hint in text for hint in hints):
            profiles.append(profile)
    if re.search(r"\b(lap|rank|race|vehicle|car speed)\b", text):
        profiles.append("racing")
    if re.search(r"\b(exp|experience|mana|mp|party|quest)\b", text):
        profiles.append("rpg")
    if re.search(r"\b(shot|bomb|weapon|boss|enemy)\b", text) and ("score" in text or "lives" in text):
        profiles.append("shmup")
    if re.search(r"\b(line|block|board|puzzle|tile)\b", text):
        profiles.append("puzzle")
    if "lives" in text and ("ring" in text or "coin" in text) and ("stage" in text or "level" in text or "zone" in text):
        profiles.append("platformer")
    return pick_profile(profiles)


def resolve_game_profile(data, system, slug, gamelist_dir):
    genres, genre_source, match, confidence = load_retrobatofficial_genres(data, system, slug, gamelist_dir)
    inferred_profile = infer_profile_from_ra(data)
    context = build_genre_context(genres, genre_source, match, confidence, fallback_profile=inferred_profile)
    profile = context["primary_profile"]
    if profile != "generic":
        return profile, genres, genre_source or "retrobat", context

    ra_genre = data.get("genre")
    if ra_genre:
        if isinstance(ra_genre, list):
            genres = [str(genre) for genre in ra_genre]
        else:
            genres = [str(ra_genre)]
        context = build_genre_context(genres, "ra.genre", "ra.genre", "medium", fallback_profile=inferred_profile)
        profile = context["primary_profile"]
        if profile != "generic":
            return profile, genres, "ra.genre", context

    profile = inferred_profile
    context = build_genre_context(genres, "ra-notes", "ra-notes", "low", fallback_profile=profile)
    return profile, genres, "ra-notes", context


def normalize_alias_key(key):
    key = str(key or "").strip()
    return key.lower()


def add_alias(aliases, key, slug, overwrite=True):
    raw_key = str(key or "").strip()
    slug = str(slug or "").strip()
    if not raw_key or not slug:
        return
    # wrapper.cpp fait un match texte exact (sensible a la casse) sur le nom
    # de fichier ROM : on enregistre la cle d'origine ET sa forme minuscule.
    for candidate in (raw_key, raw_key.lower()):
        if not overwrite and candidate in aliases:
            continue
        aliases[candidate] = slug


def merge_aliases(aliases, incoming, overwrite=True):
    for key, value in (incoming or {}).items():
        add_alias(aliases, key, value, overwrite=overwrite)


REGION_SLUG_SUFFIXES = {
    "europe": ("e", "eu", "europe"),
    "usa": ("u", "usa"),
    "japan": ("j", "jp", "japan"),
    "world": ("w", "world"),
}


def regional_slug_aliases(label):
    stem, _ext = os.path.splitext(label or "")
    base_slug = slugify_title(stem)
    if not base_slug:
        return []
    regions = []
    for group in re.findall(r"\(([^)]*)\)", stem):
        for part in re.split(r"[,/]", group):
            part = part.strip().lower()
            if part in REGION_SLUG_SUFFIXES:
                regions.extend(REGION_SLUG_SUFFIXES[part])
    return [f"{base_slug}-{suffix}" for suffix in unique_keep_order(regions)]


def rom_label_aliases(label):
    label = normalize_hash_label(label)
    if not label:
        return []

    aliases = [label]
    if "<" in label and ">" in label:
        archive, display = label.split("<", 1)
        archive = archive.strip()
        display = display.rsplit(">", 1)[0].strip()
        if archive:
            aliases.append(archive)
            archive_stem, _archive_ext = os.path.splitext(archive)
            if archive_stem:
                aliases.append(archive_stem)
        if display:
            aliases.append(display)
            aliases.extend(regional_slug_aliases(display))
    else:
        stem, ext = os.path.splitext(label)
        if ext and stem:
            aliases.append(stem)
        aliases.extend(regional_slug_aliases(label))

    return unique_keep_order(aliases)


def retrobat_entry_alias_labels(entry):
    aliases = []
    for key in ("n", "fn", "id", "set"):
        value = str(entry.get(key) or "").strip()
        if value:
            aliases.append(value)
            aliases.append(slugify_title(value))
    for item in entry.get("aka") or []:
        if not isinstance(item, dict):
            continue
        for key in ("n", "fn", "id", "set"):
            value = str(item.get(key) or "").strip()
            if value:
                aliases.append(value)
                aliases.append(slugify_title(value))
        fn = str(item.get("fn") or "").strip()
        ext = str(item.get("oe") or "").strip()
        if fn and ext and ext.startswith("."):
            aliases.extend(rom_label_aliases(f"{fn}{ext}"))
    for item in entry.get("hsh") or []:
        if not isinstance(item, dict):
            continue
        for key in ("md5", "sha1", "crc"):
            value = str(item.get(key) or "").strip().lower()
            if value:
                aliases.append(value)
    return unique_keep_order(aliases)


def build_retrobatofficial_aliases(data, system, slug, gamelist_dir):
    aliases = OrderedDict()
    if source_slug_is_variant(slug, data):
        return aliases

    path, entries, best, best_score, target_hashes, _target_keys, game_id = find_retrobatofficial_match(
        data, system, slug, gamelist_dir
    )
    if not path or not best or best_score < 100:
        return aliases

    ra = best.get("ra")
    hash_match = bool(target_hashes.intersection(retrobat_entry_hashes(best)))
    ra_id_match = game_id is not None and isinstance(ra, dict) and str(ra.get("id") or "") == str(game_id)
    if not (hash_match or ra_id_match or best_score >= 500):
        return aliases

    group = best.get("grp") or best.get("set") or best.get("id")
    best_name_key = normalize_match_key(best.get("n") or "")
    if group:
        group_entries = [
            entry
            for entry in entries
            if retrobat_entry_is_official(entry)
            and (
                entry.get("grp") == group
                or entry.get("set") == group
                or entry.get("id") == group
                or (best_name_key and normalize_match_key(entry.get("n") or "") == best_name_key)
            )
        ]
    else:
        group_entries = [best] if retrobat_entry_is_official(best) else []
    if not group_entries and retrobat_entry_is_official(best):
        group_entries = [best]

    for entry in group_entries:
        for alias in retrobat_entry_alias_labels(entry):
            add_alias(aliases, alias, slug)
    return aliases

def find_arcade_gamelist_entry_by_rom(system, rom, gamelist_dir):
    path = gamelist_jsonl_path(system, gamelist_dir)
    if not path or not rom:
        return "", [], None
    normalized = str(rom or "").strip().lower()
    entries = gamelist_entries(path)
    for entry in entries:
        for key in ("id", "set"):
            value = entry.get(key)
            if isinstance(value, str) and value.strip().lower() == normalized:
                return path, entries, entry
        for alias in entry.get("aka") or []:
            if not isinstance(alias, dict):
                continue
            for key in ("id", "set"):
                value = alias.get(key)
                if isinstance(value, str) and value.strip().lower() == normalized:
                    return path, entries, entry
    return path, entries, None


def arcade_canonical_slug_from_entry(entry, fallback_slug):
    if not entry or not retrobat_entry_is_official(entry):
        return fallback_slug
    title = entry.get("n") or entry.get("fn") or entry.get("id") or fallback_slug
    return slugify_title(title)


def arcade_exact_entry_aliases(entry, slug):
    aliases = OrderedDict()
    if not entry or not retrobat_entry_is_official(entry):
        return aliases
    for alias in retrobat_entry_alias_labels(entry):
        add_alias(aliases, alias, slug)
    return aliases


def build_aliases(data, slug, include_hash_aliases=True):
    aliases = OrderedDict()
    title = (data.get("title") or "").strip()
    if title:
        add_alias(aliases, title, slug)
    add_alias(aliases, slug, slug)
    if not include_hash_aliases:
        return aliases
    for item in data.get("hashes") or []:
        if not isinstance(item, dict):
            continue
        hash_value = (item.get("hash") or "").strip().lower()
        if hash_value:
            add_alias(aliases, hash_value, slug)
        for alias in rom_label_aliases(item.get("label") or ""):
            add_alias(aliases, alias, slug)
    return aliases


def load_aliases(alias_path):
    loaded = load_json(alias_path, OrderedDict())
    if not isinstance(loaded, dict):
        return OrderedDict()
    aliases = OrderedDict()
    for key, value in loaded.items():
        add_alias(aliases, key, value)
    return aliases


def action_allowed(action):
    if action in ACTION_FAMILY:
        return True
    return any(action.startswith(prefix) and len(action) > len(prefix) for prefix in COMPOSITE_PREFIX_FAMILY)


def family_for_action(action):
    if action in ACTION_FAMILY:
        return ACTION_FAMILY[action]
    for prefix, family in COMPOSITE_PREFIX_FAMILY.items():
        if action.startswith(prefix):
            return family
    return "system.unmapped"


def compact_desc(text):
    text = re.sub(r"\s+", " ", str(text or "")).strip()
    text = text.replace('"', "'")
    # Le parseur du wrapper decoupe les entrees sur le mot "address" et repere
    # les cles via "cle=" : ces motifs sont interdits dans une desc.
    text = re.sub(r"address", "addr", text, flags=re.I)
    text = text.replace("=", ":")
    return text[:96] if text else "State change"


def infer_audio_action(label):
    low = label.lower()
    if "big ring" in low:
        return "OBJECT_INTERACTION"
    if re.search(r"\brings?\b", low) and ("collect" in low or "get" in low):
        return "COIN_GAIN"
    if re.search(r"\brings?\b", low) and ("loss" in low or "lose" in low or "dropped" in low):
        return "COIN_LOSE"
    if "jump" in low:
        return "JUMPING"
    if "hurt" in low or "damage" in low or "ring loss" in low or "rings dropped" in low:
        return "HIT"
    if "death" in low or "die" in low:
        return "LOSE_LIFE"
    if "1-up" in low or "1up" in low or "extra life" in low:
        return "GAIN_LIFE"
    if "goal" in low or "clear" in low or "act complete" in low:
        return "LEVEL_CLEAR"
    if "checkpoint" in low or "signpost" in low or "star post" in low:
        return "OBJECT_INTERACTION_CHECKPOINT"
    if "destroy" in low or "broken" in low or "monitor" in low:
        return "OBJECT_DESTROYED"
    return ""


def infer_action(name, label="", category=""):
    text = f"{name} {label} {category}".lower()
    if NOISE_WORDS.search(text):
        return "IGNORE"
    audio = infer_audio_action(text)
    if audio:
        return audio
    if "boss" in text:
        if "defeat" in text or "dead" in text:
            return "BOSS_DEFEATED"
        return "BOSS_HIT"
    if "title" in text:
        return "TITLE_SCREEN"
    if "demo" in text or "attract" in text:
        return "DEMO_MODE"
    if "pause" in text:
        if "off" in text or "unpause" in text or "resume" in text:
            return "PAUSE_OFF"
        return "PAUSE_ON"
    if "continue" in text:
        return "CONTINUE_SCREEN"
    if "game over" in text:
        return "GAME_OVER"
    if "credits" in text:
        return "CREDITS_SCREEN"
    if "special stage" in text:
        return "STAGE_SELECT"
    if "combo" in text:
        if "timer" in text or "counter" in text:
            return "COMBO_HIT"
        return "COMBO_HIT"
    if re.search(r"\b(power[- ]?ups?|powerup|player form|current form|change .* form|suit|reserve item|item box)\b", text):
        if not re.search(r"\b(enemy|object type|container)\b", text):
            return "DYNAMIC_INVENTORY"
    if "flight" in text and "p-meter" in text:
        return "SPECIAL_ACTION"
    if re.search(r"\b(kuriboh|kuribo).*(shoe|boot)\b", text):
        return "SPECIAL_ACTION"
    if "swimming flag" in text:
        return "SPECIAL_ACTION"
    if "p-meter" in text or "power meter" in text:
        return "SPEED_TIMER"
    if "star mario" in text or "starman" in text:
        if "timer" in text or "time" in text or "counter" in text:
            return "INVINCIBILITY_TIMER"
        return "INVINCIBILITY_START"
    if "in-level" in text and ("active" in text or "set" in text):
        return "GAME_PLAYING"
    if "stage" in text or "level" in text or "zone" in text or "world" in text or re.search(r"\bact\b", text) or "round" in text:
        if "clear" in text or "complete" in text or "finish" in text:
            return "LEVEL_CLEAR"
        if "current zone" in text or text.strip() == "zone" or re.search(r"\b(current )?world( id)?\b", text):
            return "PROGRESSION_ZONE"
        if "act" in text or "stage" in text:
            return "PROGRESSION_STAGE"
        return "NEW_LEVEL"
    if "death" in text or "dead" in text or "die" in text:
        return "LOSE_LIFE"
    if "lives" in text or "life" in text or "1up" in text or "1-up" in text:
        return "LIVES_STATE"
    if "player is hit" in text or "received damage" in text or "damage received" in text:
        return "HIT"
    if "health" in text or "hp" in text or "energy" in text or "damage" in text or "hurt" in text:
        if "damage" in text or "hurt" in text:
            return "HIT"
        return "HEALTH_STATE"
    if "big ring" in text:
        return "OBJECT_INTERACTION"
    if re.search(r"\b(ring|rings|coin|coins|money|gold)\b", text):
        if "loss" in text or "lose" in text or "dropped" in text:
            return "COIN_LOSE"
        if "gain" in text or "collect" in text or "increase" in text:
            return "COIN_GAIN"
        return "MONEY_STATE"
    if "score" in text or "points" in text:
        return "SCORE_STATE"
    if "shield" in text:
        if "lost" in text or "off" in text:
            return "SHIELD_LOST"
        return "SHIELD_GAIN"
    if "invincible" in text or "invincibility" in text:
        if "counter" in text or "timer" in text:
            return "INVINCIBILITY_TIMER"
        return "INVINCIBILITY_START"
    if "speed shoes" in text or "speed shoe" in text or "p-meter" in text or "power meter" in text:
        if "counter" in text or "timer" in text:
            return "SPEED_TIMER"
        return "SPEED_START"
    if "weapon" in text or "ammo" in text:
        return "AMMO_STATE" if "ammo" in text else "WEAPON_STATE"
    if "emerald" in text or "treasure" in text:
        return "TREASURE"
    if "checkpoint" in text or "signpost" in text or "midway" in text:
        return "OBJECT_INTERACTION_CHECKPOINT"
    if "swim" in text or "swimming" in text:
        return "SWIMMING"
    if "door" in text:
        return "DOOR_OPENED"
    if "monitor" in text or "object" in text or "block" in text or "switch" in text:
        return "OBJECT_INTERACTION"
    if "timer" in text or "time" in text or "clock" in text:
        return "GENERAL_TIMER"
    return "UNKNOWN"


def action_token(text, max_len=56):
    token = unicodedata.normalize("NFKD", text or "").encode("ascii", "ignore").decode("ascii").upper()
    token = re.sub(r"[^A-Z0-9]+", "_", token).strip("_")
    token = re.sub(r"_+", "_", token)
    return token[:max_len].strip("_") or "STATE"


def value_is_zero(value):
    normalized = normalize_hex(value)
    return bool(normalized and int(normalized[2:] or "0", 16) == 0)


def value_is_nonzero(value):
    normalized = normalize_hex(value)
    return bool(normalized and int(normalized[2:] or "0", 16) != 0)


def parse_hex_int(value):
    normalized = normalize_hex(value)
    if not normalized:
        return None
    try:
        return int(normalized[2:], 16)
    except Exception:
        return None


def note_low(name, category="", raw=""):
    return f"{name or ''} {category or ''} {raw or ''}".lower()


def is_currency_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return bool(re.search(r"\b(ring|rings|coin|coins|money|gold|currency)\b", low))


def is_life_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return "lives" in low or re.search(r"\blife\b|1up|1-up", low)


def is_score_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return "score" in low or "points" in low


def is_race_rank_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if RACE_RANK_EXCLUDE_RE.search(low):
        return False
    return bool(RACE_RANK_RE.search(low))


def score_mask_from_raw(raw):
    match = re.search(r"(?<![A-Z0-9])([0X]{0,16}XX[0X]{0,16})(?![A-Z0-9])", raw or "", re.I)
    if not match:
        return ""
    return match.group(1).upper()


def score_encoding_from_raw(name="", raw=""):
    low = note_low(name, "", raw)
    if "bcd" in low or "binary coded decimal" in low:
        return "bcd"
    if "hex" in low:
        return "hex"
    return ""


def timer_role_from_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if "round" in low or "match" in low or "versus" in low or "vs " in f" {low} ":
        return "versus"
    if "puzzle" in low:
        return "puzzle"
    if "level" in low or "stage" in low or "time remaining" in low or "time left" in low:
        return "level"
    if "timer" in low and ("minute" in low or "second" in low):
        return "level"
    if "combo" in low:
        return "combo"
    if "invinc" in low or "invulnerab" in low or "star mario" in low or "starman" in low:
        return "powerup"
    if "speed" in low or "p-meter" in low or "power meter" in low:
        return "powerup"
    if "cooldown" in low:
        return "cooldown"
    return "unknown"


def timer_direction_from_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if "inverted" in low or "count up limit" in low or "counts up to" in low or "count up to" in low:
        return "inverted_countdown"
    if "time remaining" in low or "time left" in low or "countdown" in low or "decrement" in low or "decrease" in low:
        return "countdown"
    if "timer" in low and ("minute" in low or "second" in low):
        return "elapsed"
    if "elapsed" in low or "count up" in low or "increment" in low or "increase" in low:
        return "elapsed"
    return "unknown"


def timer_unit_from_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if "frame" in low:
        return "frame"
    if "seconds" in low or "second" in low or "secs" in low:
        return "second"
    if "minutes" in low or "minute" in low:
        return "minute"
    if "tick" in low:
        return "tick"
    if "bcd" in low:
        return "bcd"
    return "unknown"


def score_desc_for_note(name, raw):
    desc = compact_desc(name or "Score")
    mask = score_mask_from_raw(raw)
    if mask and mask not in desc.upper():
        return compact_desc(f"{desc} {mask}")
    return desc


def is_timer_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return "timer" in low or "counter" in low or " time" in f" {low}" or "clock" in low or category == "timer"


def is_noisy_timer_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return bool("frame" in low or re.search(r"\b(bgm|music|sfx|sound).*(counter|timer|fade)", low))


def is_internal_range_marker(name, category="", raw=""):
    low = note_low(name, category, raw)
    return bool(COUNTER_NOISE_RE.search(low))


def note_name_category_low(name, category=""):
    return f"{name or ''} {category or ''}".lower()


def is_multiplexed_event_register(name, category="", raw=""):
    low = note_low(name, category, raw)
    return bool(re.search(r"\b(sound|music|sfx|song|currently playing|event id|event code)\b", low))


def explicit_currency_loss_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    return bool(re.search(r"\b(lose|loss|lost|drop|dropped|spend|spent|damage|hurt|reset after damage)\b", low))


def is_power_inventory_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if re.search(r"\b(enemy|object type|container)\b", low):
        return False
    return bool(re.search(r"\b(power[- ]?ups?|powerup|player form|current form|change .* form|suit|reserve item|item box)\b", low))


def transformation_action_for_label(label):
    low = (label or "").lower()
    if "glitched" in low or "unused" in low or "n/a" in low:
        return ""
    if "raccoon" in low or "racoon" in low:
        return "TRANSFORMATION_RACCOON"
    if "frog" in low:
        return "TRANSFORMATION_FROG"
    if "tanooki" in low:
        return "TRANSFORMATION_TANOOKI"
    if "hammer" in low:
        return "TRANSFORMATION_HAMMER"
    if "fire" in low or "flower" in low:
        return "TRANSFORMATION_FIRE"
    if "super" in low or "big" in low:
        return "TRANSFORMATION_SUPER"
    if "small" in low:
        return "TRANSFORMATION_SMALL"
    return ""


def is_malformed_inline_enum_label(label):
    return bool("," in (label or "") and re.search(r"\b[0-9A-Fa-f]{1,2}\s*=", label or ""))


def inline_flag_events_from_raw(addr, typ, name, category, raw, genre_context):
    if not raw or "flags:" not in raw.lower():
        return []
    out = []
    flags_text = re.split(r"flags\s*:", raw, maxsplit=1, flags=re.I)[-1]
    for mask_text, label in re.findall(r"\b([0-9A-Fa-f]{1,2})\s*=\s*([^,\n]+)", flags_text):
        if not label.strip():
            continue
        mask = "0X" + mask_text.upper().zfill(2)
        action = infer_action(name, label, category)
        low_label = label.lower()
        if "statue" in low_label or "stone" in low_label or "boot" in low_label or "swim" in low_label:
            action = "DYNAMIC_INVENTORY"
        if action in {"IGNORE", "UNKNOWN"}:
            continue
        if not note_allowed_for_context({"name": name, "category": category, "note_raw": raw}, action, genre_context):
            continue
        out.append(event(addr, typ, "bit_true", action, label.strip(), mask=mask))
    return out


def timer_action_for_note(name, category="", raw=""):
    low = note_low(name, category, raw)
    if "combo" in low:
        return "COMBO_TIMER"
    if "speed shoe" in low or "p-meter" in low or "power meter" in low:
        return "SPEED_TIMER"
    if "invincib" in low or "invulnerab" in low or "star mario" in low or "starman" in low:
        return "INVINCIBILITY_TIMER"
    if "seconds" in low or "minutes" in low or "level" in low or "match timer" in low:
        return "LEVEL_TIMER"
    return "GENERAL_TIMER"


def score_meta_for_note(name, category="", raw=""):
    meta = {"score_kind": "game"}
    mask = score_mask_from_raw(raw)
    encoding = score_encoding_from_raw(name, raw)
    if mask:
        meta["score_mask"] = mask
    if encoding:
        meta["score_encoding"] = encoding
    player = player_from_note_text(name, category, raw)
    if player:
        meta["player"] = player
    return meta


def timer_meta_for_note(name, category="", raw=""):
    meta = {
        "timer_kind": "game",
        "timer_role": timer_role_from_note(name, category, raw),
        "timer_direction": timer_direction_from_note(name, category, raw),
        "timer_unit": timer_unit_from_note(name, category, raw),
    }
    player = player_from_note_text(name, category, raw)
    if player:
        meta["player"] = player
    return meta


def player_from_note_text(name, category="", raw=""):
    match = re.search(r"\b(?:p|player\s*)([1-4])\b|\b([1-4])p\b", note_low(name, category, raw), re.I)
    if not match:
        return None
    raw_player = match.group(1) or match.group(2)
    try:
        return int(raw_player)
    except Exception:
        return None


def refine_type_for_note(note, typ, system):
    name = note.get("name") or ""
    category = note.get("category") or ""
    raw = note.get("note_raw") or ""
    low = note_low(name, category, raw)
    values = [item for item in note.get("values") or [] if isinstance(item, dict)]
    parsed_values = [parse_hex_int(item.get("key") or "") for item in values]
    parsed_values = [value for value in parsed_values if value is not None]
    if typ == "u32le" and parsed_values and max(parsed_values) <= 0xFFFF and re.search(r"\b(stage|level|screen|scene|mode|state|id)\b", low):
        return "u16le"
    if system != "megadrive":
        return typ
    if typ == "u16be" and (is_currency_note(name, category, raw) or is_score_note(name, category, raw)):
        return "u16le"
    keys = {normalize_hex(item.get("key") or "") for item in values}
    if typ == "u16be" and "counter" in low and "0X0120" in keys:
        return "u16le"
    return typ


def action_for_value_note(name, category, label):
    low_name = note_low(name, category)
    low_label = (label or "").lower()
    inferred = infer_action(name, label, category)

    if category == "audio" or re.search(r"\b(sound|music|sfx|song|currently playing)\b", low_name):
        if inferred in {"PROGRESSION_ZONE", "PROGRESSION_STAGE", "NEW_LEVEL", "LEVEL_CLEAR", "GAME_PLAYING"}:
            return "UNKNOWN"

    if "end of level" in low_label:
        if low_label.startswith("not ") or "not end" in low_label:
            return "UNKNOWN"
        return "LEVEL_CLEAR"
    if low_label.startswith(("not ", "no ")) and inferred in {"LEVEL_CLEAR", "NEW_LEVEL", "PROGRESSION_STAGE"}:
        return "UNKNOWN"

    if "pause" in low_name:
        if low_label in {"no", "off", "inactive", "not active"}:
            return "PAUSE_OFF"
        if low_label in {"yes", "on", "active", "enabled"}:
            return "PAUSE_ON"

    if "game state" in low_name:
        if "sega" in low_label or "corporate" in low_label:
            return "CORPORATE_SCREEN"
        if "title" in low_label:
            return "TITLE_SCREEN"
        if "demo" in low_label:
            return "DEMO_MODE"
        if "continue" in low_label:
            return "CONTINUE_SCREEN"
        if "credit" in low_label:
            return "CREDITS_SCREEN"
        if "ending" in low_label or "game over" in low_label:
            return "GAME_OVER"
        if "level" in low_label or "stage" in low_label or "pre-level" in low_label:
            return "GAME_PLAYING"

    if "credits sequence" in low_name:
        return "CREDITS_SCREEN"
    if "level select" in low_name:
        return "LEVEL_" + action_token(label)
    if "current zone" in low_name:
        return "LEVEL_" + action_token(label.replace("(", " ").replace(")", " "))
    if "special stage" in low_name or "emerald" in low_name:
        if "not obtained" in low_label:
            return "UNKNOWN"
        if "emerald" in low_label:
            return "TREASURE_" + action_token(label) + "_CHAOS_EMERALD"

    if "ring loss timer" in low_name:
        return "MONEY_STATE"

    if "empty" in low_label or "not active" in low_label or low_label == "no":
        if "speed shoe" in low_name:
            return "SPEED_STOP"
        if "invincib" in low_name or "invulnerab" in low_name:
            return "INVINCIBILITY_STOP"
        if "shield" in low_name:
            return "SHIELD_LOST"
    if "full" in low_label or "active" in low_label or "enabled" in low_label or low_label == "yes":
        if "speed shoe" in low_name:
            return "SPEED_START"
        if "invincib" in low_name or "invulnerab" in low_name:
            return "INVINCIBILITY_START"
        if "shield" in low_name:
            return "SHIELD_GAIN"

    if low_label in {"no", "off", "none", "default", "inactive", "not active", "not obtained", "not defeated"}:
        return "UNKNOWN"

    return inferred


def note_allowed_for_context(note, action, genre_context, value_count=0):
    name = note.get("name") or ""
    category = note.get("category") or ""
    raw = note.get("note_raw") or ""
    low = note_low(name, category, raw)
    profile = (genre_context or {}).get("primary_profile") or "generic"
    profiles = set((genre_context or {}).get("profiles") or [profile])
    tags = set((genre_context or {}).get("mechanic_tags") or [])

    if NOISE_WORDS.search(low) and not is_race_rank_note(name, category, raw):
        return False
    if is_internal_range_marker(name, category, raw):
        return False
    if value_count > 16 and action in {"PROGRESSION_STAGE", "NEW_LEVEL", "LEVEL_CLEAR"}:
        return False
    if action in PLAYER_ACTIONS and re.search(r"\b(selection|select|gravity|modifier|ability|cheat|timer|counter)\b", low):
        return False
    if action == "GENERAL_TIMER":
        if (
            category == "timer"
            and re.search(r"\b(timer|time)\b", low)
            and re.search(r"\b(minutes?|seconds?|secs?|bcd|elapsed|remaining|left|countdown)\b", low)
            and "frame" not in low
        ):
            return True
        if "time remaining" in low or "level time" in low or "match timer" in low:
            return True
        return "timer" in tags and not profiles.intersection({"platformer", "action", "adventure"})
    if action == "COIN_LOSE" and not explicit_currency_loss_note(name, category, raw):
        return False
    if action == "MONEY_STATE" and "heaven" in low:
        return False
    return True


WRAPPER_TYPES = {"u8", "u16be", "u16le", "u24be", "u24le", "u32be", "u32le"}


def clamp_type_for_wrapper(typ, address):
    """wrapper.cpp ne lit que 1 a 4 octets ; un type inconnu retombe en u8.

    Les types larges des sources DOFLinx (u40be..u64be, scores BCD) sont
    recentres sur les 4 octets de poids faible (decalage d'adresse en BE)
    pour que les deltas min/max restent observables.
    """
    if typ in WRAPPER_TYPES:
        return typ, address
    match = re.match(r"^u(\d+)(be|le)$", typ)
    if not match:
        return "u8", address
    bits = int(match.group(1))
    endian = match.group(2)
    if bits <= 8:
        return "u8", address
    if bits <= 32:
        return f"u32{endian}", address
    extra_bytes = bits // 8 - 4
    if endian == "be":
        value = parse_hex_int(address)
        if value is not None:
            address = f"0X{value + extra_bytes:X}"
    return f"u32{endian}", address


def event(
    address,
    typ,
    condition,
    action,
    desc,
    value="",
    mask="",
    bit=None,
    min_value=None,
    max_value=None,
    color="",
    no_log=False,
    no_survey=False,
    meta=None,
):
    condition = (condition or "change").lower()
    if condition == "any":
        # wrapper.cpp ne connait pas "any" (CondType::unknown ne declenche jamais)
        condition = "change"
    if condition not in ALLOWED_CONDITIONS:
        condition = "change"
    action = (action or "UNKNOWN").upper().strip()
    if action == "OBJECT_INTERACON":
        action = "OBJECT_INTERACTION"
    if action == "IGNORE":
        return None
    if not action_allowed(action):
        action = infer_action(desc) or "UNKNOWN"
    address = normalize_hex(address)
    if not address:
        return None
    typ = (typ or "u8").lower()
    typ, address = clamp_type_for_wrapper(typ, address)
    value = normalize_hex(value) if value not in ("", None) else ""
    mask = normalize_hex(mask) if mask not in ("", None) else ""
    if condition in {"eq", "neq"} and not value:
        condition = "change"
    if condition in {"bit_true", "bit_false"} and not mask:
        return None
    if condition in {"bit_true", "bit_false"}:
        value = ""
    result = {
        "address": address,
        "type": typ,
        "condition": condition,
        "value": value,
        "mask": mask,
        "bit": bit,
        "action": action,
        "desc": compact_desc(desc),
        "no_log": bool(no_log),
        "no_survey": bool(no_survey),
    }
    if min_value not in ("", None):
        try:
            result["min"] = int(str(min_value), 10)
        except Exception:
            pass
    if max_value not in ("", None):
        try:
            result["max"] = int(str(max_value), 10)
        except Exception:
            pass
    if color:
        result["color"] = str(color).strip().lower()
    if isinstance(meta, dict):
        for key, value in meta.items():
            if value in ("", None):
                continue
            result[str(key)] = value
    return result


def deterministic_events_from_ra(data, system, genre_context=None):
    events = []
    for note in data.get("code_notes") or []:
        addr = note.get("address") or ""
        name = note.get("name") or ""
        category = note.get("category") or ""
        raw = note.get("note_raw") or ""
        text = f"{name} {raw}"
        typ = normalize_type(note.get("type") or note.get("size"), system, text)
        typ = refine_type_for_note(note, typ, system)
        if NOISE_WORDS.search(text) and not is_race_rank_note(name, category, raw):
            continue

        values = [v for v in note.get("values") or [] if isinstance(v, dict)]
        flags = [f for f in note.get("flags") or [] if isinstance(f, dict)]
        base_action = infer_action(name, "", category)
        if is_race_rank_note(name, category, raw):
            base_action = "RANK_STATE"
        low_note = note_low(name, category, raw)

        if flags:
            for flag in flags:
                label = flag.get("label") or name
                action = infer_action(name, label, category)
                low_label = (label or "").lower()
                if "underwater" in low_label:
                    action = "ENVIRONMENT_UNDERWATER"
                elif "in air" in low_label or "jump" in low_label:
                    action = "JUMPING"
                elif "on object" in low_label or "standing on" in low_label:
                    action = "OBJECT_INTERACTION"
                if action == "IGNORE":
                    continue
                if action == "UNKNOWN":
                    continue
                if not note_allowed_for_context(note, action, genre_context, len(values)):
                    continue
                bit_index = flag.get("bit_index")
                mask = flag.get("mask_hex") or (1 << int(bit_index) if bit_index is not None else "")
                condition = "bit_true"
                events.append(event(addr, typ, condition, action, label, mask=mask, bit=bit_index))
            continue

        inline_flags = inline_flag_events_from_raw(addr, typ, name, category, raw, genre_context)

        if values:
            emit_value_enums = not (len(values) > 16 and base_action in {"PROGRESSION_STAGE", "NEW_LEVEL", "LEVEL_CLEAR"})
            if emit_value_enums:
                for item in values:
                    label = item.get("label") or ""
                    value = item.get("key") or ""
                    action = action_for_value_note(name, category, label)
                    if is_power_inventory_note(name, category, raw):
                        if is_malformed_inline_enum_label(label):
                            continue
                        if re.search(r"\b(glitched|unused|n/a)\b", label.lower()):
                            continue
                        events.append(event(addr, typ, "eq", "DYNAMIC_INVENTORY", label or name, value=value))
                        transform_action = transformation_action_for_label(label)
                        if transform_action and transform_action != "TRANSFORMATION_SMALL":
                            events.append(event(addr, typ, "eq", transform_action, f"Transformed into {label}", value=value))
                        continue
                    if action in {"IGNORE", "UNKNOWN"}:
                        continue
                    if not note_allowed_for_context(note, action, genre_context, len(values)):
                        continue
                    low = label.lower()
                    if action_allowed(action) or action.startswith(("LEVEL_", "TREASURE_", "ENVIRONMENT_")):
                        events.append(event(addr, typ, "eq", action, label or name, value=value))
                    low_name = note_low(name, category, raw)
                    counter_like = re.search(r"\b(possessed|count|counter|amount|total)\b", low_name)
                    if (
                        is_currency_note(name, category, raw)
                        and counter_like
                        and value_is_zero(value)
                        and explicit_currency_loss_note(name, category, raw)
                    ):
                        events.append(event(addr, typ, "eq", "COIN_LOSE", label or name, value=value))
                    elif (
                        low not in {"no", "off", "inactive", "not active", "not obtained"}
                        and (
                            low in {"yes", "on", "active", "enabled", "obtained", "clear", "complete", "sonic dead"}
                            or re.search(r"\b(active|enabled|dead|obtained|clear|complete)\b", low)
                        )
                    ):
                        fallback = infer_action(name, label, category)
                        if fallback not in {"IGNORE", "UNKNOWN"} and note_allowed_for_context(note, fallback, genre_context, len(values)):
                            events.append(event(addr, typ, "eq", fallback, label or name, value=value))
            if any((item.get("label") or "").strip() for item in values):
                if is_power_inventory_note(name, category, raw) and re.search(r"\bpower[- ]?ups?\b", low_note):
                    events.append(event(addr, typ, "decrease", "HIT", f"{name} decreased"))
                elif is_life_note(name, category, "") and not is_multiplexed_event_register(name, category, raw):
                    if note_allowed_for_context(note, "GAIN_LIFE", genre_context, len(values)):
                        events.append(event(addr, typ, "increase", "GAIN_LIFE", f"{name} increased"))
                    if note_allowed_for_context(note, "LOSE_LIFE", genre_context, len(values)):
                        events.append(event(addr, typ, "decrease", "LOSE_LIFE", f"{name} decreased"))
                elif base_action in {"PROGRESSION_STAGE", "PROGRESSION_ZONE", "NEW_LEVEL", "SCORE_STATE", "HEALTH_STATE", "RANK_STATE"}:
                    if note_allowed_for_context(note, base_action, genre_context, len(values)):
                        desc = score_desc_for_note(name, raw) if base_action == "SCORE_STATE" else name
                        meta = score_meta_for_note(name, category, raw) if base_action == "SCORE_STATE" else None
                        events.append(event(addr, typ, "change", base_action, desc, no_log=base_action in {"SCORE_STATE"}, meta=meta))
                elif is_timer_note(name, category, raw) and not is_noisy_timer_note(name, category, raw):
                    timer_action = timer_action_for_note(name, category, raw)
                    events.append(event(addr, typ, "change", timer_action, name, no_log=False, no_survey=False, meta=timer_meta_for_note(name, category, raw)))
            events.extend(inline_flags)
            continue

        action = base_action
        if action in {"IGNORE", "UNKNOWN"}:
            continue
        if not note_allowed_for_context(note, action, genre_context, len(values)):
            continue
        if "in-level" in low_note and ("active" in low_note or "set" in low_note):
            events.append(event(addr, typ, "neq", "GAME_PLAYING", f"{name} active", value="0x0"))
        elif ("p-meter" in low_note or "power meter" in low_note) and "countdown timer" not in low_note:
            events.append(event(addr, typ, "eq", "SPEED_START", "P-meter fully charged", value="0x7F"))
            events.append(event(addr, typ, "decrease", "SPEED_STOP", "P-meter draining down", no_log=True))
            events.append(event(addr, typ, "change", "SPEED_TIMER", name, no_log=True))
        elif ("p-meter" in low_note or "power meter" in low_note) and "countdown timer" in low_note:
            continue
        elif "starman flag" in low_note:
            events.append(event(addr, typ, "change", "INVINCIBILITY_START", name, no_log=True))
        elif "star mario" in low_note or ("starman" in low_note and re.search(r"\b(timer|time|counter)\b", low_note)):
            events.append(event(addr, typ, "increase", "INVINCIBILITY_START", f"{name} started"))
            events.append(event(addr, typ, "decrease", "INVINCIBILITY_STOP", f"{name} ended"))
        elif "change suit poof" in low_note or "suit-change" in low_note:
            events.append(event(addr, typ, "increase", "TRANSFORMATION", f"{name} started", no_log=True))
        elif "swimming flag" in low_note:
            events.append(event(addr, typ, "bit_true", "SPECIAL_ACTION", "Swimming state active", mask="0x01", no_log=True))
        elif "flight" in low_note and "p-meter" in low_note:
            events.append(event(addr, typ, "eq", "SPECIAL_ACTION", "Flight active", value="0x01"))
        elif re.search(r"\b(kuriboh|kuribo).*(shoe|boot)\b", low_note):
            events.append(event(addr, typ, "eq", "SPECIAL_ACTION", name, value="0x01"))
        elif is_life_note(name, category, raw):
            events.append(event(addr, typ, "increase", "GAIN_LIFE", f"{name} increased"))
            events.append(event(addr, typ, "decrease", "LOSE_LIFE", f"{name} decreased"))
        elif is_currency_note(name, category, raw):
            events.append(event(addr, typ, "increase", "COIN_GAIN", f"{name} increased"))
            if explicit_currency_loss_note(name, category, raw):
                events.append(event(addr, typ, "eq", "COIN_LOSE", f"{name} empty", value="0x0"))
        elif is_score_note(name, category, raw):
            low = text.lower()
            noisy = "low" in low or "655360" in low
            events.append(event(addr, typ, "change", "SCORE_STATE", score_desc_for_note(name, raw), no_log=noisy, meta=score_meta_for_note(name, category, raw)))
        elif is_timer_note(name, category, raw):
            if is_noisy_timer_note(name, category, raw):
                continue
            timer_action = timer_action_for_note(name, category, raw)
            no_log = timer_action in {"INVINCIBILITY_TIMER", "SPEED_TIMER"}
            events.append(event(addr, typ, "change", timer_action, name, no_log=no_log, no_survey=False, meta=timer_meta_for_note(name, category, raw)))
        elif action == "HEALTH_STATE":
            events.append(event(addr, typ, "decrease", "HIT", f"{name} decreased"))
            events.append(event(addr, typ, "increase", "HEAL", f"{name} increased"))
        elif action == "HIT" and "timer that is set after player is hit" in low_note:
            events.append(event(addr, typ, "increase", "HIT", "Post-damage timer started"))
        elif action == "HIT" and re.search(r"\b0?1\s*=", low_note):
            events.append(event(addr, typ, "eq", "HIT", name, value="0x01"))
        else:
            events.append(event(addr, typ, "change", action, name))
    return [e for e in events if e]


def is_timer_action(action):
    return action == "GENERAL_TIMER" or action.endswith("_TIMER") or action in {"LEVEL_TIMER", "BOMB_TIMER", "TIMER_LOW_WARN"}


def classify_event_visibility(ev, context):
    action = ev.get("action") or "UNKNOWN"
    desc = (ev.get("desc") or "").lower()
    profile = context.get("primary_profile") or "generic"
    profiles = set(context.get("profiles") or [profile])
    tags = set(context.get("mechanic_tags") or [])

    if action in {"UNKNOWN", "IGNORE"}:
        return "hidden"
    if "frame" in desc and is_timer_action(action):
        return "hidden"

    if is_timer_action(action):
        if action in {"LEVEL_TIMER", "BOMB_TIMER", "TIMER_LOW_WARN"}:
            if profiles.intersection(PRIMARY_TIMER_PROFILES) or "timer" in tags:
                return "primary"
            return "telemetry"
        if action == "GENERAL_TIMER":
            if profiles.intersection(PRIMARY_TIMER_PROFILES) or "timer" in tags:
                return "primary"
            return "telemetry"
        return "telemetry"

    if action in PRIMARY_ACTIONS:
        return "primary"
    if any(action in GENRE_PRIMARY_ACTIONS.get(item, set()) for item in profiles):
        return "primary"
    if action.endswith(("_START", "_STOP", "_GAIN", "_LOSE")):
        return "primary"

    if action.startswith("LEVEL_"):
        return "primary" if profiles.intersection(PRIMARY_LEVEL_PROFILES) else "secondary"
    if action.startswith("TREASURE_"):
        return "primary" if profiles.intersection(PRIMARY_TREASURE_PROFILES) or "inventory" in tags else "secondary"
    if action.startswith("ENVIRONMENT_"):
        return "primary" if profiles.intersection({"platformer", "adventure"}) or "environment" in tags else "secondary"
    if action.startswith("OBJECT_INTERACTION_"):
        return "primary" if profiles.intersection(PRIMARY_OBJECT_PROFILES) else "secondary"

    if action in TELEMETRY_ACTIONS:
        return "telemetry"
    if action in {"RESOURCE_STATE", "AMMO_STATE", "WEAPON_STATE", "SPEED_STATE"}:
        if profiles.intersection(PRIMARY_RESOURCE_PROFILES) or tags.intersection({"ammo", "movement", "units"}):
            return "primary"
        return "telemetry"
    if action in PLAYER_IDENTITY_ACTIONS:
        return "telemetry"
    if action in PLAYER_ACTIONS:
        return "hidden"

    return "secondary"


def apply_contextual_visibility(events, data, system, slug, gamelist_dir):
    if data.get("_resolved_genre_context"):
        context = data.get("_resolved_genre_context") or {}
        genres = data.get("_resolved_genres") or []
        profile = data.get("_resolved_profile") or context.get("primary_profile") or "generic"
        source = data.get("_resolved_genre_source") or context.get("source") or ""
    else:
        profile, genres, source, context = resolve_game_profile(data, system, slug, gamelist_dir)
        data["_resolved_genres"] = genres
        data["_resolved_profile"] = profile
        data["_resolved_genre_source"] = source
        data["_resolved_genre_context"] = context

    counts = OrderedDict((key, 0) for key in ("primary", "secondary", "telemetry", "hidden"))
    for ev in events:
        visibility = classify_event_visibility(ev, context)
        ev["visibility"] = visibility
        counts[visibility] = counts.get(visibility, 0) + 1
        if visibility == "primary":
            ev["no_log"] = False
            ev["no_survey"] = False
        elif visibility == "secondary":
            ev["no_log"] = bool(ev.get("no_log"))
            ev["no_survey"] = False
        elif visibility == "telemetry":
            ev["no_log"] = True
            ev["no_survey"] = False
        else:
            ev["no_log"] = True
            ev["no_survey"] = True
    return profile, genres, source, context, counts


def parse_pipe_events(text):
    cleaned = (text or "").split("<<<END_MEM>>>")[0]
    cleaned = re.sub(r"<\|.*?\|>", "", cleaned)
    cleaned = cleaned.replace("```text", "").replace("```lua", "").replace("```", "")
    events = []
    for line in cleaned.splitlines():
        line = line.strip()
        if not line or line.startswith("ADDR|") or "|" not in line:
            continue
        parts = [p.strip() for p in line.split("|")]
        if len(parts) < 7:
            continue
        addr, typ, cond, value, mask, action = parts[:6]
        desc = "|".join(parts[6:]).strip()
        if not desc:
            desc = action
        ev = event(addr, typ, cond, action, desc, value=value, mask=mask)
        if ev:
            events.append(ev)
    return events


def parse_decimal_int(text):
    text = str(text or "").strip().strip('"').strip("'")
    if not text:
        return None
    try:
        return int(text, 10)
    except Exception:
        return None


def parse_lua_entry_attrs(text):
    attrs = {}
    for key, value in re.findall(r"(\w+)\s*=\s*(\"[^\"]*\"|'[^']*'|[^,\s}]+)", text or ""):
        value = value.strip().strip('"').strip("'")
        attrs[key] = value
    return attrs


def parse_lua_bool(value):
    return str(value or "").strip().lower() == "true"


def parse_generated_lua_mem_events(text):
    events = []
    for item in re.findall(r"\{([^{}]*address\s*=[^{}]*)\}", text or ""):
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
    low = normalize_match_key(text).replace(" ", "_")
    words = f" {normalize_match_key(text)} "
    candidates = []
    for color, hints in COLOR_HINTS.items():
        for hint in hints:
            normalized = normalize_match_key(hint)
            candidates.append((len(normalized), color, normalized))
    for _length, color, normalized in sorted(candidates, reverse=True):
        if f" {normalized} " in words or normalized.replace(" ", "_") in low:
            return color
    return ""


def extract_doflinx_score_entries(text):
    match = re.search(r'\["score"\]\s*=\s*\{(?P<body>.*?)\n\s*\}', text or "", re.S)
    if not match:
        return []
    entries = []
    for item in re.findall(r"\{([^{}]+)\}", match.group("body")):
        attrs = parse_lua_entry_attrs(item)
        if attrs.get("is_score", "").lower() != "true" and "min" not in attrs and "max" not in attrs:
            continue
        entries.append(attrs)
    return entries


def extract_doflinx_event_sections(text):
    match = re.search(
        r"\bevents\s*=\s*\{(?P<body>.*?)(?:\n\s*\},\s*(?:\n\s*--|\n\s*local_events)|\n\s*\}\s*\n\s*\})",
        text or "",
        re.S,
    )
    body = match.group("body") if match else (text or "")
    sections = []
    for section, section_body in re.findall(r'\["([^"]+)"\]\s*=\s*\{(?P<body>.*?)\n\s*\},', body, re.S):
        entries = []
        for item in re.findall(r"\{([^{}]+)\}", section_body):
            attrs = parse_lua_entry_attrs(item)
            if attrs:
                entries.append(attrs)
        if entries:
            sections.append((section, entries))
    return sections


def doflinx_event_action(section, attrs):
    section_low = (section or "").lower()
    desc = attrs.get("desc") or section
    desc_low = desc.lower()
    condition = (attrs.get("condition") or "change").lower()

    if section_low == "score" or attrs.get("is_score", "").lower() == "true":
        return ""
    if section_low in {"coin", "credits"} or "credit" in desc_low:
        return "MONEY_STATE"
    if "bomb" in section_low or re.search(r"\bbombs?\b", desc_low):
        return "BOMB_FIRED" if condition == "decrease" else "AMMO_STATE"
    if "fire power" in desc_low or "firepower" in desc_low:
        return "WEAPON_STATE"
    if "energy" in desc_low or "health" in desc_low or "hp" in desc_low:
        return "HIT" if condition == "decrease" else "HEALTH_STATE"
    if "die" in section_low or "death" in section_low:
        return "LOSE_LIFE"
    return infer_action(desc, section)


def arcade_doflinx_extra_events(text):
    events = []
    report = {"source": "doflinx-extra", "events": 0, "rules": []}
    for section, entries in extract_doflinx_event_sections(text):
        if section == "score":
            continue
        for attrs in entries:
            action = doflinx_event_action(section, attrs)
            if not action:
                continue
            desc = attrs.get("desc") or section
            full_desc = f"{section}: {desc}"
            address = arcade_universal_address(attrs.get("address"))
            ev = event(
                address,
                attrs.get("type") or "u8",
                attrs.get("condition") or "change",
                action,
                full_desc,
                no_log=False,
                no_survey=False,
            )
            if ev:
                events.append(ev)
                report["rules"].append(
                    {
                        "section": section,
                        "address": ev.get("address"),
                        "source_address": normalize_hex(attrs.get("address")),
                        "type": ev.get("type"),
                        "condition": ev.get("condition"),
                        "action": action,
                        "desc": desc,
                    }
                )
    report["events"] = len(events)
    return events, report


def score_delta_action(desc, min_value=None, max_value=None):
    low = (desc or "").lower()
    if re.search(r"\b(boss|dome)\b", low):
        return "BOSS_HIT"
    if re.search(r"\b(gift|pow|bamboo|sprout|holstein|strawberry|dragonfly|barrel|medal|bonus|mobi)\b", low):
        return "TREASURE"
    if re.search(r"\b(destroy|planes?|ships?|tanks?|helicopters?|antiair|cranes?|houses?|enemies|enemy|targets?)\b", low):
        return "OBJECT_DESTROYED"
    if min_value is not None and max_value is not None and min_value >= 5000 and min_value == max_value:
        return "TREASURE"
    return "SCORE_STATE"


def mame_uses_target(text):
    match = re.search(r"^\s*USES\s*=\s*([A-Za-z0-9_.-]+)", text or "", re.I | re.M)
    return match.group(1).strip() if match else ""


def load_text_following_uses(path, base_dir):
    text = load_text(path)
    target = mame_uses_target(text)
    if target:
        target_path = os.path.join(base_dir, f"{target}.MAME")
        if os.path.exists(target_path):
            return load_text(target_path), target
    return text, os.path.splitext(os.path.basename(path))[0] if path else ""


def extract_mame_score_rules(text):
    rules = []
    in_score = False
    pending_comment = ""
    for raw_line in (text or "").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if line.startswith("[") and line.endswith("]"):
            in_score = line.upper() == "[SCORE]"
            pending_comment = ""
            continue
        if not in_score:
            continue
        if line.startswith("#"):
            pending_comment = line.lstrip("#").strip()
            continue
        match = re.match(r"SC\s*=\s*([0-9]+)\s*:\s*([0-9]+)\s*:(.*)$", line, re.I)
        if match:
            desc = pending_comment or f"Score delta {match.group(1)}-{match.group(2)}"
            rules.append(
                {
                    "min": parse_decimal_int(match.group(1)),
                    "max": parse_decimal_int(match.group(2)),
                    "desc": desc,
                    "color": color_hint_from_text(f"{desc} {match.group(3)}"),
                }
            )
            pending_comment = ""
    return rules


def mame_score_rule_map(rom, args):
    if not rom or not getattr(args, "mame_dir", ""):
        return {}
    mame_path = os.path.join(args.mame_dir, f"{rom}.MAME")
    if not os.path.exists(mame_path):
        return {}
    mame_text, _resolved_rom = load_text_following_uses(mame_path, args.mame_dir)
    rules = {}
    for rule in extract_mame_score_rules(mame_text):
        key = (rule.get("min"), rule.get("max"))
        if key[0] is None or key[1] is None:
            continue
        rules[key] = rule
    return rules


def doflinx_uses_target(text):
    match = re.search(r"^\s*USES\s*=\s*([A-Za-z0-9_.-]+)", text or "", re.I | re.M)
    if match:
        return match.group(1).strip()
    match = re.search(r'rom\s*=\s*["\']([^"\']+)["\']', text or "", re.I)
    return match.group(1).strip() if match else ""


def load_doflinx_mem_text(rom, source_base):
    source_dir = os.path.join(source_base, "sources", "doflinx")
    path = os.path.join(source_dir, f"{rom}.MEM")
    text = load_text(path)
    if not text:
        return "", "", ""
    target = doflinx_uses_target(text)
    if target and target.lower() != rom.lower():
        target_path = os.path.join(source_dir, f"{target}.MEM")
        target_text = load_text(target_path)
        if target_text:
            return target_text, target, target_path
    return text, rom, path


def arcade_mame_id_candidates(data, slug, genre_context):
    candidates = []
    for item in data.get("hashes") or []:
        if isinstance(item, dict) and item.get("label"):
            label = os.path.basename(item.get("label"))
            label = re.split(r"\s+|<", label, maxsplit=1)[0]
            candidates.append(os.path.splitext(label)[0])
    source = (genre_context or {}).get("source") or ""
    confidence = (genre_context or {}).get("confidence") or ""
    match = (genre_context or {}).get("match") or ""
    if ":" in source and (confidence == "high" or match in {"hash", "ra.id"}):
        candidates.append(source.split(":", 1)[1].strip())
    candidates.append(slug)
    candidates.append(strip_ra_id(slug))
    return unique_keep_order([item for item in candidates if item])


def score_anchor_from_events(events):
    score_events = [ev for ev in events if ev.get("action") == "SCORE_STATE"]
    if not score_events:
        return None
    score_events = sorted(score_events, key=lambda ev: parse_int(ev.get("address")) or 0)
    first = score_events[0]
    return {"address": first.get("address"), "type": first.get("type") or "u8", "confidence": "low"}


def arcade_score_delta_events(data, slug, events, args, genre_context):
    if args.system != "arcade":
        return [], {}

    report = {
        "enabled": True,
        "candidates": [],
        "source": "",
        "rom": "",
        "score_anchor": {},
        "rules": [],
        "events": 0,
    }
    for rom in arcade_mame_id_candidates(data, slug, genre_context):
        report["candidates"].append(rom)
        text, resolved_rom, path = load_doflinx_mem_text(rom, args.source_base)
        entries = extract_doflinx_score_entries(text)
        if entries:
            out = []
            mame_rules = mame_score_rule_map(resolved_rom or rom, args)
            for attrs in entries:
                min_value = parse_decimal_int(attrs.get("min"))
                max_value = parse_decimal_int(attrs.get("max"))
                if min_value is None or max_value is None:
                    continue
                desc = attrs.get("desc") or f"Score delta {min_value}-{max_value}"
                action = score_delta_action(desc, min_value, max_value)
                color = (mame_rules.get((min_value, max_value)) or {}).get("color") or color_hint_from_text(desc)
                address = arcade_universal_address(attrs.get("address"))
                ev = event(
                    address,
                    attrs.get("type") or "u32be",
                    attrs.get("condition") or "change",
                    action,
                    f"Score delta {min_value}-{max_value}: {desc}",
                    min_value=min_value,
                    max_value=max_value,
                    color=color,
                    no_log=False,
                    no_survey=False,
                    meta={"score_kind": "threshold"},
                )
                if ev:
                    out.append(ev)
                    report["rules"].append(
                        {
                            "min": min_value,
                            "max": max_value,
                            "desc": desc,
                            "action": action,
                            "color": color,
                            "address": ev.get("address"),
                            "source_address": normalize_hex(attrs.get("address")),
                        }
                    )
            report["source"] = path
            report["rom"] = resolved_rom
            report["events"] = len(out)
            return out, report

        mame_path = os.path.join(args.mame_dir, f"{rom}.MAME")
        if os.path.exists(mame_path):
            mame_text, resolved_rom = load_text_following_uses(mame_path, args.mame_dir)
            rules = extract_mame_score_rules(mame_text)
            anchor = score_anchor_from_events(events)
            if rules and anchor:
                out = []
                for rule in rules:
                    min_value = rule.get("min")
                    max_value = rule.get("max")
                    desc = rule.get("desc") or f"Score delta {min_value}-{max_value}"
                    action = score_delta_action(desc, min_value, max_value)
                    color = rule.get("color") or color_hint_from_text(desc)
                    ev = event(
                        anchor["address"],
                        anchor["type"],
                        "change",
                        action,
                        f"Score delta {min_value}-{max_value}: {desc}",
                        min_value=min_value,
                        max_value=max_value,
                        color=color,
                        no_log=False,
                        no_survey=False,
                        meta={"score_kind": "threshold"},
                    )
                    if ev:
                        out.append(ev)
                        report["rules"].append(
                            {"min": min_value, "max": max_value, "desc": desc, "action": action, "color": color}
                        )
                report["source"] = mame_path
                report["rom"] = resolved_rom
                report["score_anchor"] = anchor
                report["events"] = len(out)
                return out, report

    report["enabled"] = False
    return [], report


SUMMARY = {"version": VERSION, "generated": GENERATED_STAMP, "profile": "", "systems": OrderedDict()}


def filter_events_for_profile(events, profile, drop_inactive=False):
    """Filtre optionnel des events inactifs (no_log/no_survey=true).

    Par defaut les entrees inactives SONT emises avec leurs flags a true :
    wrapper.cpp les saute au chargement (l.1105) et l'utilisateur final peut
    les reactiver en passant le flag a false, sans avoir besoin du curator.
    --drop-inactive produit la variante allegee. Si un jeu n'a QUE des events
    inactifs, la sortie complete est conservee.
    """
    if profile != "wrapper" or not drop_inactive:
        return events, {"dropped_hidden": 0, "dropped_telemetry": 0, "kept_all_fallback": False}
    kept = []
    hidden = 0
    telemetry = 0
    for ev in events:
        if ev.get("no_survey"):
            hidden += 1
        elif ev.get("no_log"):
            telemetry += 1
        else:
            kept.append(ev)
    if not kept and events:
        return events, {"dropped_hidden": 0, "dropped_telemetry": 0, "kept_all_fallback": True}
    return kept, {"dropped_hidden": hidden, "dropped_telemetry": telemetry, "kept_all_fallback": False}


def record_summary(system, slug, candidate_events, emitted_events, filter_stats):
    sys_entry = SUMMARY["systems"].setdefault(system, {
        "files": 0,
        "events_emitted": 0,
        "events_candidates": 0,
        "dropped_hidden": 0,
        "dropped_telemetry": 0,
        "telemetry_only_files": 0,
    })
    sys_entry["files"] += 1
    sys_entry["events_emitted"] += len(emitted_events)
    sys_entry["events_candidates"] += len(candidate_events)
    sys_entry["dropped_hidden"] += filter_stats["dropped_hidden"]
    sys_entry["dropped_telemetry"] += filter_stats["dropped_telemetry"]
    if filter_stats["kept_all_fallback"]:
        sys_entry["telemetry_only_files"] += 1


def dedupe_events(events):
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


def event_to_lua(ev, profile="wrapper"):
    parts = [
        f"address={ev['address']}",
        f'type="{ev["type"]}"',
        f'condition="{ev["condition"]}"',
    ]
    if ev.get("value"):
        parts.append(f"value={ev['value']}")
    if profile != "wrapper" and ev.get("bit") is not None and ev.get("bit") != "":
        parts.append(f"bit={int(ev['bit'])}")
    if ev.get("mask"):
        parts.append(f"mask={ev['mask']}")
    parts.append(f'action="{ev["action"]}"')
    if ev.get("color"):
        parts.append(f'color="{compact_desc(ev["color"])}"')
    if ev.get("min") is not None and ev.get("min") != "":
        parts.append(f"min={int(ev['min'])}")
    if ev.get("max") is not None and ev.get("max") != "":
        parts.append(f"max={int(ev['max'])}")
    # Cles meta toujours emises : LiveScoreAggregatorProvider et
    # LiveTimerAggregatorProvider (APIExpose) parsent les .MEM et exploitent
    # score_kind/score_mask/score_encoding et timer_* pour /ws/score et /ws/timer.
    for key in (
        "player",
        "score_kind",
        "score_mask",
        "score_encoding",
        "timer_kind",
        "timer_role",
        "timer_direction",
        "timer_unit",
    ):
        value = ev.get(key)
        if value in ("", None):
            continue
        if isinstance(value, int):
            parts.append(f"{key}={value}")
        else:
            parts.append(f'{key}="{compact_desc(value)}"')
    if profile == "wrapper":
        # wrapper.cpp saute les watchers no_log/no_survey au chargement et les
        # parseurs C# considerent l'absence comme false : on n'emet qu'a true.
        if ev.get("no_log"):
            parts.append("no_log=true")
        if ev.get("no_survey"):
            parts.append("no_survey=true")
    else:
        parts.append(f'no_log={str(bool(ev.get("no_log"))).lower()}')
        parts.append(f'no_survey={str(bool(ev.get("no_survey"))).lower()}')
    parts.append(f'desc="{compact_desc(ev.get("desc"))}"')
    return "{ " + ", ".join(parts) + " }"


def build_lua_mem(data, slug, events, system, profile="wrapper"):
    title = data.get("title") or slug.replace("-", " ").title()
    system_name = data.get("system_name") or SYSTEM_NAMES.get(system, system.upper())
    resolved_genres = data.get("_resolved_genres") or []
    if resolved_genres:
        genre = ", ".join(resolved_genres)
    elif isinstance(data.get("genre"), list):
        genre = ", ".join(str(item) for item in data.get("genre") if item)
    else:
        genre = data.get("genre") or "Unknown"
    hashes = data.get("hashes") or []

    grouped = OrderedDict((family, []) for family in FAMILY_ORDER)
    for ev in events:
        family = family_for_action(ev["action"])
        grouped.setdefault(family, []).append(ev)

    for items in grouped.values():
        items.sort(key=lambda ev: (
            parse_int(ev.get("address")) or 0,
            ev.get("value") or "",
            ev.get("mask") or "",
            ev.get("action") or "",
            ev.get("desc") or "",
        ))

    lines = [f"-- mem-curator v{VERSION} ({GENERATED_STAMP}) profile={profile}"]
    lines.extend(["return {", "  game = {"])
    lines.append(f'    title = "{title}",')
    lines.append(f'    system = "{system}",')
    lines.append(f'    system_name = "{system_name}",')
    lines.append(f'    genre = "{genre}"')
    lines.append("  },")
    lines.append("")
    lines.append("  rom = {")
    lines.append(f'    name = "{slug}",')
    if profile != "wrapper":
        lines.append(f'    file = "{slug}.zip",')
    lines.append("    hashes = {")
    for item in hashes:
        if not isinstance(item, dict):
            continue
        hash_value = (item.get("hash") or "").strip().lower()
        label = normalize_hash_label(item.get("label") or "")
        if hash_value:
            lines.append(f'      {{ hash = "{hash_value}", label = "{label}" }},')
    lines.append("    }")
    lines.append("  },")
    lines.append("")
    lines.append("  events = {")

    emitted_categories = OrderedDict()
    for family, items in grouped.items():
        if not items:
            continue
        category, subfamily = family.split(".", 1)
        emitted_categories.setdefault(category, []).append((subfamily, items))

    category_seen = []
    for family in FAMILY_ORDER:
        category = family.split(".", 1)[0]
        if category in category_seen or category not in emitted_categories:
            continue
        category_seen.append(category)
        lines.append(f"    {category} = {{")
        for subfamily, items in emitted_categories[category]:
            lines.append(f"      {subfamily} = {{")
            for ev in items:
                lines.append(f"        {event_to_lua(ev, profile)},")
            lines.append("      },")
        lines.append("    },")
    lines.append("  },")
    lines.append("}")
    return "\n".join(lines) + "\n"


def build_prompt(game_title, system, notes):
    return f"""Game: "{game_title}"
System: {system}
RAM notes to map. Preserve explicit values/bit masks exactly.
Return ONLY pipe lines in this format:
ADDR|TYPE|COND|VALUE|MASK|ACTION|DESC

Allowed conditions: eq, neq, change, increase, decrease, bit_true, bit_false, any.
Use standard V11 actions only. If unsure use UNKNOWN.
Discard positions, pointers, animation frames, camera/scroll, cheat-only entries.
Prefer stable gameplay: lifecycle, progression, lives/health, score, rings/coins, powerups, boss, objects.
For score notes, preserve explicit masks from raw notes in DESC, e.g. Score XX0000, Score 00XX00, Score 0000XX.
For timer notes, preserve timer intent in DESC, e.g. level timer, countdown, round timer, temporary power timer.

RAM notes:
{notes}
<<<END_MEM>>>"""


def note_to_prompt_line(note, system):
    addr = normalize_hex(note.get("address") or "")
    name = note.get("name") or ""
    typ = normalize_type(note.get("type") or note.get("size"), system, f"{name} {note.get('note_raw') or ''}")
    bits = []
    values = []
    for item in note.get("values") or []:
        if isinstance(item, dict):
            values.append(f"{normalize_hex(item.get('key'))}={item.get('label')}")
    for item in note.get("flags") or []:
        if isinstance(item, dict):
            bits.append(f"bit{item.get('bit_index')} {normalize_hex(item.get('mask_hex'))}={item.get('label')}")
    extra = ""
    if values:
        extra += " values: " + "; ".join(values[:24])
    if bits:
        extra += " flags: " + "; ".join(bits[:16])
    raw = note.get("note_raw") or ""
    score_mask = score_mask_from_raw(raw)
    if score_mask:
        extra += f" score_mask: {score_mask}"
    if raw and (is_score_note(name, note.get("category") or "", raw) or is_timer_note(name, note.get("category") or "", raw)):
        extra += " raw: " + compact_desc(raw)[:180]
    return f"- {addr} {typ}: {name}{extra}"


def collect_prompt_notes(data, system):
    lines = []
    for note in data.get("code_notes") or []:
        text = f"{note.get('name') or ''} {note.get('note_raw') or ''}"
        if NOISE_WORDS.search(text):
            continue
        action = infer_action(note.get("name") or "", note.get("note_raw") or "", note.get("category") or "")
        if action == "IGNORE":
            continue
        lines.append(note_to_prompt_line(note, system))
    return lines


def call_local_model(prompt, system_instruction, server_url, model_name, api_type, api_key):
    if requests is None:
        raise RuntimeError("package python 'requests' absent ; utiliser --no-llm ou installer requests")
    server_url = server_url.rstrip("/")
    headers = {"Content-Type": "application/json"}
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"

    is_gemma_4 = "gemma-4" in model_name.lower() or "qat" in model_name.lower()
    if api_type == "ollama" and "/v1" not in server_url and "chat/completions" not in server_url:
        url = f"{server_url}/api/chat"
        payload = {
            "model": model_name,
            "messages": [{"role": "user", "content": f"{system_instruction}\n\n{prompt}"}],
            "options": {"temperature": 0.1, "num_predict": 4000},
            "stream": False,
        }
    elif api_type == "openai" and is_gemma_4:
        base_url = server_url
        if base_url.endswith("/chat/completions"):
            base_url = base_url[: -len("/chat/completions")]
        if not base_url.endswith("/v1"):
            base_url = base_url + "/v1"
        url = f"{base_url}/completions"
        full_prompt = f"<|im_start|>system\n{system_instruction}<|im_end|>\n<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n"
        payload = {
            "model": model_name,
            "prompt": full_prompt,
            "temperature": 0.1,
            "max_tokens": 4000,
            "stop": ["<|im_end|>", "<|im_start|>"],
        }
    else:
        url = server_url if server_url.endswith("/chat/completions") else f"{server_url}/chat/completions"
        payload = {
            "model": model_name,
            "messages": [
                {"role": "system", "content": system_instruction},
                {"role": "user", "content": prompt},
            ],
            "temperature": 0.1,
            "max_tokens": 4000,
        }

    response = requests.post(url, json=payload, headers=headers, timeout=600)
    response.raise_for_status()
    result = response.json()
    if api_type == "ollama" and "/v1" not in server_url and "chat/completions" not in server_url:
        return result["message"]["content"]
    if api_type == "openai" and is_gemma_4:
        return result["choices"][0]["text"].strip()
    return result["choices"][0]["message"]["content"]


def source_paths(source_base, system, ra_file):
    filename = os.path.basename(ra_file)
    slug = os.path.splitext(filename)[0]
    return {
        "gh": os.path.join(source_base, "sources", "gamehacking", system, filename),
        "dc": os.path.join(source_base, "sources", "datacrystal", system, f"{slug}.MEM"),
    }


def lua_unescape(text):
    return (text or "").replace('\\"', '"').replace("\\'", "'").replace("\\n", " ")


def parse_lua_source_attrs(line):
    attrs = {}
    for key, pattern in [
        ("address", r"address\s*=\s*([^,\s}]+)"),
        ("type", r"type\s*=\s*\"([^\"]+)\""),
        ("condition", r"condition\s*=\s*\"([^\"]+)\""),
        ("desc", r"desc\s*=\s*\"((?:\\.|[^\"\\])*)\""),
    ]:
        match = re.search(pattern, line)
        if match:
            attrs[key] = lua_unescape(match.group(1))
    for key in ["no_log", "no_survey"]:
        match = re.search(rf"{key}\s*=\s*(true|false)", line, re.I)
        if match:
            attrs[key] = match.group(1).lower() == "true"
    return attrs


def parse_lua_inline_map(line):
    match = re.search(r"map\s*=\s*\{(.*?)\}", line)
    if not match:
        return []
    out = []
    for value, label in re.findall(r"\[\s*(0x[0-9A-Fa-f]+|\d+)\s*\]\s*=\s*\"((?:\\.|[^\"\\])*)\"", match.group(1)):
        out.append((normalize_hex(value), lua_unescape(label)))
    return out


def source_addr(value):
    parsed = parse_hex_int(value)
    if parsed is None:
        return normalize_hex(value)
    if parsed <= 0xFFFF:
        return "0X" + f"{parsed:04X}"
    return "0X" + f"{parsed:X}"


def datacrystal_events_from_text(text, genre_context=None):
    out = []
    section = ""
    for raw_line in (text or "").splitlines():
        section_match = re.search(r"\[\"([^\"]+)\"\]\s*=", raw_line)
        if section_match:
            section = section_match.group(1).lower()
            continue
        if "address=" not in raw_line or "desc=" not in raw_line:
            continue
        attrs = parse_lua_source_attrs(raw_line)
        addr = source_addr(attrs.get("address") or "")
        typ = attrs.get("type") or "u8"
        condition = attrs.get("condition") or "change"
        desc = attrs.get("desc") or ""
        low = f"{section} {desc}".lower()
        no_log = bool(attrs.get("no_log"))
        no_survey = bool(attrs.get("no_survey"))

        if "object set" in low and addr == "0X070A":
            out.append(event(addr, typ, "neq", "GAME_PLAYING", "In-level object set active", value="0x00"))
            continue
        if "world number" in low or low.strip() == "world id":
            out.append(event(addr, typ, "change", "PROGRESSION_ZONE", desc))
            continue
        if "p-meter in status bar" in low:
            out.append(event(addr, typ, "eq", "SPEED_START", "P-meter fully charged", value="0x7F", no_log=no_log))
            out.append(event(addr, typ, "decrease", "SPEED_STOP", "P-meter draining down", no_log=True))
            out.append(event(addr, typ, "change", "SPEED_TIMER", desc, no_log=True, meta=timer_meta_for_note(desc, section, desc)))
            continue
        if "countdown timer for advancing the p-meter" in low:
            continue
        if "timer for star mario" in low:
            out.append(event(addr, typ, "increase", "INVINCIBILITY_START", "Star Mario timer started", no_log=no_log))
            out.append(event(addr, typ, "decrease", "INVINCIBILITY_STOP", "Star Mario timer ended", no_log=no_log))
            out.append(event(addr, typ, "change", "INVINCIBILITY_TIMER", desc, no_log=True, meta=timer_meta_for_note(desc, section, desc)))
            continue
        if "map starman flag" in low:
            out.append(event(addr, typ, "change", "INVINCIBILITY_START", desc, no_log=True))
            continue
        if "timer that is set after player is hit" in low:
            out.append(event(addr, typ, "increase", "HIT", "Post-damage timer started", no_log=no_log))
            continue
        if "change suit poof" in low:
            out.append(event(addr, typ, "increase", "TRANSFORMATION", "Suit-change poof started", no_log=True))
            continue
        if "indicates flight" in low:
            out.append(event(addr, typ, "eq", "SPECIAL_ACTION", "Flight active", value="0x01", no_log=no_log))
            continue
        if "swimming flag" in low:
            out.append(event(addr, typ, "bit_true", "SPECIAL_ACTION", "Swimming state active", mask="0x01", no_log=True))
            continue
        if re.search(r"\b(kuribos?|kuriboh).*(boot|shoe)\b", low):
            out.append(event(addr, typ, "eq", "SPECIAL_ACTION", "Kuribo shoe equipped", value="0x01", no_log=True))
            continue
        if "stomp counter" in low:
            out.append(event(addr, typ, "increase", "ENEMY_HIT", "Enemy stomp counter increased"))
            out.append(event(addr, typ, "increase", "OBJECT_DESTROYED", "Enemy defeated by stomp"))
            continue
        if section in {"powerup_state", "inventory"} and ("form" in low or "item" in low):
            maps = parse_lua_inline_map(raw_line)
            if maps and "form" in low and addr == "0X00ED":
                for value, label in maps:
                    if re.search(r"\b(glitched|unused|n/a)\b", label.lower()):
                        continue
                    out.append(event(addr, typ, "eq", "DYNAMIC_INVENTORY", f"{label} form", value=value))
                    transform_action = transformation_action_for_label(label)
                    if transform_action and transform_action != "TRANSFORMATION_SMALL":
                        out.append(event(addr, typ, "eq", transform_action, f"Transformed into {label}", value=value))
            elif "item" in low:
                out.append(event(addr, typ, condition, "INVENTORY_ITEM", desc, no_log=no_log, no_survey=no_survey))
            continue
    return [item for item in out if item]


def auxiliary_source_events(source_base, system, ra_file, genre_context=None):
    paths = source_paths(source_base, system, ra_file)
    events = []
    reports = []
    dc_text = load_text(paths.get("dc"))
    if dc_text:
        dc_events = datacrystal_events_from_text(dc_text, genre_context)
        events.extend(dc_events)
        reports.append(("datacrystal", paths.get("dc"), len(dc_events)))
    return events, reports


def write_genre_context_report(logs_dir, slug, data, events, visibility_counts):
    action_counts = OrderedDict()
    family_counts = OrderedDict()
    for ev in events:
        action = ev.get("action") or "UNKNOWN"
        family = family_for_action(action)
        action_counts[action] = action_counts.get(action, 0) + 1
        family_counts[family] = family_counts.get(family, 0) + 1
    report = OrderedDict(
        [
            ("title", data.get("title") or slug),
            ("slug", slug),
            ("genre_context", data.get("_resolved_genre_context") or {}),
            ("visibility_counts", visibility_counts),
            ("family_counts", family_counts),
            ("action_counts", action_counts),
        ]
    )
    save_text(
        os.path.join(logs_dir, f"{slug}_genre_context.json"),
        json.dumps(report, indent=2, ensure_ascii=False) + "\n",
    )


def write_arcade_score_delta_report(logs_dir, slug, report):
    if not report:
        return
    save_text(
        os.path.join(logs_dir, f"{slug}_arcade_score_delta.json"),
        json.dumps(report, indent=2, ensure_ascii=False) + "\n",
    )


def arcade_score_delta_events_from_doflinx_text(text, mame_rules=None):
    out = []
    report = {"source": "doflinx-only", "rules": [], "events": 0}
    mame_rules = mame_rules or {}
    for attrs in extract_doflinx_score_entries(text):
        min_value = parse_decimal_int(attrs.get("min"))
        max_value = parse_decimal_int(attrs.get("max"))
        if min_value is None or max_value is None:
            continue
        desc = attrs.get("desc") or f"Score delta {min_value}-{max_value}"
        action = score_delta_action(desc, min_value, max_value)
        color = (mame_rules.get((min_value, max_value)) or {}).get("color") or color_hint_from_text(desc)
        address = arcade_universal_address(attrs.get("address"))
        ev = event(
            address,
            attrs.get("type") or "u32be",
            attrs.get("condition") or "change",
            action,
            f"Score delta {min_value}-{max_value}: {desc}",
            min_value=min_value,
            max_value=max_value,
            color=color,
            no_log=False,
            no_survey=False,
        )
        if ev:
            out.append(ev)
            report["rules"].append(
                {
                    "min": min_value,
                    "max": max_value,
                    "desc": desc,
                    "action": action,
                    "color": color,
                    "address": ev.get("address"),
                    "source_address": normalize_hex(attrs.get("address")),
                }
            )
    report["events"] = len(out)
    return out, report


def find_arcade_doflinx_files(source_base, game_filter):
    source_dir = os.path.join(source_base, "sources", "doflinx")
    if not os.path.isdir(source_dir):
        return []
    files = sorted(glob.glob(os.path.join(source_dir, "*.MEM")))
    if game_filter:
        needle = game_filter.lower()
        files = [path for path in files if needle in os.path.basename(path).lower()]
    return files


def arcade_parent_rom_with_score(rom, source_base, system="", gamelist_dir=""):
    candidates = []
    _path, _entries, entry = find_arcade_gamelist_entry_by_rom(system, rom, gamelist_dir)
    group = str((entry or {}).get("grp") or "").strip()
    if group and group.lower() != str(rom or "").strip().lower():
        candidates.append(group)
    match = re.match(r"^(.+\d)[a-z]+$", rom or "", re.I)
    if match:
        candidates.append(match.group(1))
    for candidate in unique_keep_order(candidates):
        text, resolved_rom, _path = load_doflinx_mem_text(candidate, source_base)
        if extract_doflinx_score_entries(text):
            return resolved_rom or candidate
    return ""


def process_arcade_doflinx_game(args, doflinx_file):
    rom = os.path.splitext(os.path.basename(doflinx_file))[0]
    raw_slug = slugify_title(rom)
    _gamelist_path, _gamelist_entries, gamelist_entry = find_arcade_gamelist_entry_by_rom(
        args.system, rom, args.gamelist_dir
    )
    slug = arcade_canonical_slug_from_entry(gamelist_entry, raw_slug)
    output_dir = os.path.join(SCRIPT_DIR, args.output_dir, args.system)
    logs_dir = os.path.join(SCRIPT_DIR, args.log_dir, args.system)
    os.makedirs(output_dir, exist_ok=True)
    os.makedirs(logs_dir, exist_ok=True)
    mem_path = os.path.join(output_dir, f"{slug}.MEM")
    alias_path = os.path.join(output_dir, "alias.json")
    text, resolved_rom, resolved_path = load_doflinx_mem_text(rom, args.source_base)

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
        save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\n")
        print(f"   [+] alias-only {rom} -> {target_slug}")
        print(f"   [+] wrote {alias_path}")
        return alias_path

    if os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path
    score_events, score_delta_report = arcade_score_delta_events_from_doflinx_text(
        text, mame_score_rule_map(resolved_rom or rom, args)
    )
    extra_events, extra_report = arcade_doflinx_extra_events(text)
    events = score_events + extra_events
    score_delta_report["source"] = resolved_path
    score_delta_report["rom"] = resolved_rom
    score_delta_report["enabled"] = bool(score_events)
    score_delta_report["extra_events"] = extra_report
    print(f"   [i] arcade doflinx score delta events: {len(score_events)}")
    print(f"   [i] arcade doflinx extra events: {len(extra_events)}")

    if not score_events:
        parent_rom = arcade_parent_rom_with_score(rom, args.source_base, args.system, args.gamelist_dir)
        if parent_rom:
            parent_raw_slug = slugify_title(parent_rom)
            _parent_gamelist_path, _parent_gamelist_entries, parent_gamelist_entry = find_arcade_gamelist_entry_by_rom(
                args.system, parent_rom, args.gamelist_dir
            )
            parent_slug = arcade_canonical_slug_from_entry(parent_gamelist_entry, parent_raw_slug)
            score_delta_report["alias_target"] = parent_slug
            score_delta_report["alias_reason"] = "variant_without_score_delta"
            write_arcade_score_delta_report(logs_dir, slug, score_delta_report)

            aliases = load_aliases(alias_path)
            merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, parent_slug), overwrite=True)
            merge_aliases(aliases, arcade_exact_entry_aliases(parent_gamelist_entry, parent_slug), overwrite=True)
            add_alias(aliases, rom, parent_slug)
            if resolved_rom:
                add_alias(aliases, resolved_rom, parent_slug)
            save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\n")

            stale_mem_path = os.path.join(output_dir, f"{raw_slug}.MEM")
            if args.force and stale_mem_path != os.path.join(output_dir, f"{parent_slug}.MEM") and os.path.exists(stale_mem_path):
                os.remove(stale_mem_path)
                print(f"   [i] removed stale empty {stale_mem_path}")
            print(f"   [+] alias {rom} -> {parent_slug}")
            print(f"   [+] wrote {alias_path}")
            return os.path.join(output_dir, f"{parent_slug}.MEM")

    data = {
        "title": resolved_rom or rom,
        "genre": ["Shoot'em Up"],
        "hashes": [],
        "system_name": SYSTEM_NAMES.get(args.system, args.system.upper()),
    }
    events = dedupe_events(events)
    profile, genres, source, genre_context, visibility_counts = apply_contextual_visibility(
        events, data, args.system, slug, args.gamelist_dir
    )
    write_arcade_score_delta_report(logs_dir, slug, score_delta_report)
    write_genre_context_report(logs_dir, slug, data, events, visibility_counts)
    emitted_events, filter_stats = filter_events_for_profile(
        events, args.emit_profile, getattr(args, "drop_inactive", False)
    )
    record_summary(args.system, slug, events, emitted_events, filter_stats)
    save_text(mem_path, build_lua_mem(data, slug, emitted_events, args.system, args.emit_profile))

    aliases = load_aliases(alias_path)
    merge_aliases(aliases, arcade_exact_entry_aliases(gamelist_entry, slug), overwrite=True)
    official_aliases = build_retrobatofficial_aliases(data, args.system, slug, args.gamelist_dir)
    merge_aliases(aliases, official_aliases, overwrite=True)
    add_alias(aliases, rom, slug)
    if resolved_rom:
        add_alias(aliases, resolved_rom, slug)
    save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\n")

    count_text = ", ".join(f"{key}={value}" for key, value in visibility_counts.items())
    print(f"   [i] genre profile: {profile} ({', '.join(genres) if genres else 'none'}, source={source})")
    print(f"   [i] visibility: {count_text}")
    print(f"   [+] wrote {mem_path} ({len(emitted_events)} events, {len(events)} candidats)")
    print(f"   [+] wrote {alias_path}")
    return mem_path


def process_game(args, ra_file, system_instruction):
    data = load_json(ra_file, {})
    system = args.system
    slug = clean_slug_from_ra(ra_file, data)
    output_dir = os.path.join(SCRIPT_DIR, args.output_dir, system)
    logs_dir = os.path.join(SCRIPT_DIR, args.log_dir, system)
    os.makedirs(output_dir, exist_ok=True)
    os.makedirs(logs_dir, exist_ok=True)
    mem_path = os.path.join(output_dir, f"{slug}.MEM")
    alias_path = os.path.join(output_dir, "alias.json")
    if getattr(args, "alias_only", False):
        aliases = load_aliases(alias_path)
        add_alias(aliases, slug, slug)
        merge_aliases(aliases, build_retrobatofficial_aliases(data, system, slug, args.gamelist_dir), overwrite=True)
        merge_aliases(
            aliases,
            build_aliases(data, slug, include_hash_aliases=not source_slug_is_variant(slug, data)),
            overwrite=False,
        )
        save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\n")
        print(f"   [+] alias-only {slug}")
        print(f"   [+] wrote {alias_path}")
        return alias_path

    existing_events = []
    if os.path.exists(mem_path) and getattr(args, "preserve_existing_mem", False):
        existing_events = parse_generated_lua_mem_events(load_text(mem_path))
        print(f"   [i] existing DOFLinx MEM events: {len(existing_events)}")
    elif os.path.exists(mem_path) and not args.force:
        print(f"   [skip] {slug}.MEM existe deja")
        return mem_path

    prompt_lines = collect_prompt_notes(data, system)
    if not prompt_lines:
        prompt_lines = ["- No useful RAM notes after filtering."]

    _profile, _genres, _source, pre_context = resolve_game_profile(data, system, slug, args.gamelist_dir)
    data["_resolved_genres"] = _genres
    data["_resolved_profile"] = _profile
    data["_resolved_genre_source"] = _source
    data["_resolved_genre_context"] = pre_context

    all_events = []
    det_events = deterministic_events_from_ra(data, system, pre_context)
    print(f"   [i] deterministic RA events: {len(det_events)}")
    all_events.extend(det_events)

    aux_events, aux_reports = auxiliary_source_events(args.source_base, system, ra_file, pre_context)
    for source_name, source_path, count in aux_reports:
        rel_path = os.path.relpath(source_path, args.source_base) if source_path else source_name
        print(f"   [i] deterministic {source_name} events: {count} ({rel_path})")
    all_events.extend(aux_events)

    if not args.no_llm:
        chunks = [prompt_lines[i : i + args.chunk_size] for i in range(0, len(prompt_lines), args.chunk_size)]
        for idx, chunk in enumerate(chunks, start=1):
            prompt = build_prompt(data.get("title") or slug, system, "\n".join(chunk))
            save_text(os.path.join(logs_dir, f"{slug}_sent_prompt_chunk_{idx}.txt"), prompt)
            print(f"   [llm] chunk {idx}/{len(chunks)} ({len(chunk)} notes)")
            raw = call_local_model(prompt, system_instruction, args.server_url, args.model, args.api_type, args.api_key)
            save_text(os.path.join(logs_dir, f"{slug}_raw_response_chunk_{idx}.txt"), raw)
            parsed = parse_pipe_events(raw)
            print(f"   [llm] parsed events: {len(parsed)}")
            all_events.extend(parsed)
            if args.min_delay:
                time.sleep(args.min_delay)

    ra_events = dedupe_events(all_events)
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

    profile, genres, source, genre_context, visibility_counts = apply_contextual_visibility(
        events, data, system, slug, args.gamelist_dir
    )
    genre_text = ", ".join(genres) if genres else "none"
    count_text = ", ".join(f"{key}={value}" for key, value in visibility_counts.items())
    match_text = genre_context.get("match") or "unknown"
    confidence_text = genre_context.get("confidence") or "unknown"
    secondary_text = ",".join(genre_context.get("secondary_profiles") or []) or "-"
    tags_text = ",".join(genre_context.get("mechanic_tags") or []) or "-"
    print(
        f"   [i] genre profile: {profile} secondary={secondary_text} tags={tags_text} "
        f"({genre_text}, source={source}, match={match_text}, confidence={confidence_text})"
    )
    print(f"   [i] visibility: {count_text}")
    write_genre_context_report(logs_dir, slug, data, events, visibility_counts)
    emitted_events, filter_stats = filter_events_for_profile(
        events, args.emit_profile, getattr(args, "drop_inactive", False)
    )
    if filter_stats["kept_all_fallback"]:
        print("   [i] emit: jeu telemetry-only, sortie v4 conservee")
    elif filter_stats["dropped_hidden"] or filter_stats["dropped_telemetry"]:
        print(f"   [i] emit: dropped hidden={filter_stats['dropped_hidden']} telemetry={filter_stats['dropped_telemetry']}")
    record_summary(args.system, slug, events, emitted_events, filter_stats)
    lua_code = build_lua_mem(data, slug, emitted_events, system, args.emit_profile)
    save_text(mem_path, lua_code)

    aliases = load_aliases(alias_path)
    add_alias(aliases, slug, slug)
    merge_aliases(aliases, build_retrobatofficial_aliases(data, system, slug, args.gamelist_dir), overwrite=True)
    merge_aliases(
        aliases,
        build_aliases(data, slug, include_hash_aliases=not source_slug_is_variant(slug, data)),
        overwrite=False,
    )
    save_text(alias_path, json.dumps(aliases, indent=2, ensure_ascii=False) + "\n")

    print(f"   [+] wrote {mem_path} ({len(emitted_events)} events, {len(events)} candidats)")
    print(f"   [+] wrote {alias_path}")
    return mem_path


def find_ra_files(source_base, system, game_filter):
    ra_dir = os.path.join(source_base, "sources", "ra", system)
    files = sorted(glob.glob(os.path.join(ra_dir, "*.json")))
    if not getattr(find_ra_files, "include_excluded", False):
        files = [path for path in files if not EXCLUDED_SOURCE_RE.search(os.path.basename(path))]
    if game_filter:
        needle = game_filter.lower()
        if needle.isdigit():
            files = [
                path
                for path in files
                if os.path.splitext(os.path.basename(path))[0].lower() == needle
                or os.path.splitext(os.path.basename(path))[0].lower().startswith(f"{needle}-")
                or os.path.splitext(os.path.basename(path))[0].lower().endswith(f"-{needle}")
                or needle in strip_ra_id(os.path.splitext(os.path.basename(path))[0]).lower()
            ]
        else:
            files = [path for path in files if needle in os.path.basename(path).lower()]
    return files


def find_available_systems(source_base):
    ra_base = os.path.join(source_base, "sources", "ra")
    if not os.path.isdir(ra_base):
        return []
    systems = []
    for name in sorted(os.listdir(ra_base)):
        system_dir = os.path.join(ra_base, name)
        if os.path.isdir(system_dir) and glob.glob(os.path.join(system_dir, "*.json")):
            systems.append(name)
    return systems


def process_system_batch(args, system_instruction):
    print(f"\n================ SYSTEM: {args.system} ================")
    if args.reset_alias:
        alias_dir = os.path.join(SCRIPT_DIR, args.output_dir, args.system)
        os.makedirs(alias_dir, exist_ok=True)
        alias_path = os.path.join(alias_dir, "alias.json")
        save_text(alias_path, json.dumps(OrderedDict(), indent=2, ensure_ascii=False) + "\n")
        print(f"[+] reset alias: {alias_path}")

    if args.system == "arcade":
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
                args.preserve_existing_mem = True
                print("[i] arcade RA pass: enrich DOFLinx MEM files and fill missing MEM files")

    files = find_ra_files(args.source_base, args.system, args.game)
    if not files:
        if args.system == "arcade":
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
        print(f"[error] no RA files found for {args.system} in {args.source_base}")
        return 1

    print(f"[+] RA files: {len(files)}")
    for idx, ra_file in enumerate(files, start=1):
        print(f"[{idx}/{len(files)}] {os.path.basename(ra_file)}")
        try:
            process_game(args, ra_file, system_instruction)
        except Exception as exc:
            print(f"   [error] {exc}")
            if len(files) == 1:
                raise
    print(f"--- done {args.system} ---")
    return 0


def load_system_instruction():
    for name in [
        "superefficient_runtime_prompt_v2.md",
        "superefficient_prompt_v2.md",
        "superefficient_runtime_prompt.md",
        "superefficient_prompt.md",
    ]:
        path = os.path.join(SCRIPT_DIR, name)
        content = load_text(path)
        if content:
            print(f"[+] loaded prompt: {name}")
            return content
    return "You are a strict DOFLinx V11 MEM event extractor. Output only ADDR|TYPE|COND|VALUE|MASK|ACTION|DESC lines."


def main():
    parser = argparse.ArgumentParser(description="mem-curator v5 (APIExpose)")
    parser.add_argument("system", nargs="?", default="megadrive")
    parser.add_argument("--game", default="", help="Substring filter on RA filename")
    parser.add_argument("--source-base", default=os.environ.get("AG_SOURCE_BASE", DEFAULT_SOURCE_BASE))
    parser.add_argument("--output-dir", default="MEM_v5")
    parser.add_argument("--log-dir", default="MEM_v5_logs")
    parser.add_argument(
        "--emit-profile",
        choices=["wrapper", "full"],
        default="wrapper",
        help="wrapper: sortie minimale alignee sur wrapper.cpp ; full: parite champs v4",
    )
    parser.add_argument(
        "--drop-inactive",
        action="store_true",
        help="retire les entrees no_log/no_survey=true au lieu de les emettre avec leur flag (variante allegee, non re-activable par l'utilisateur final)",
    )
    parser.add_argument("--gamelist-dir", default=os.environ.get("AG_GAMELIST_DIR", DEFAULT_RETROBAT_GAMELIST_SYSTEMS))
    parser.add_argument("--mame-dir", default=os.environ.get("AG_MAME_DIR", DEFAULT_MAME_DIR))
    parser.add_argument("--server-url", default=os.environ.get("AG_LOCAL_SERVER_URL", "http://127.0.0.1:1234/v1"))
    parser.add_argument("--model", default=os.environ.get("AG_LOCAL_MODEL", "google/gemma-4-12b-qat"))
    parser.add_argument("--api-type", choices=["openai", "ollama"], default=os.environ.get("AG_LOCAL_API_TYPE", "openai"))
    parser.add_argument("--api-key", default=os.environ.get("AG_LOCAL_API_KEY", ""))
    parser.add_argument("--chunk-size", type=int, default=18)
    parser.add_argument("--min-delay", type=float, default=0.5)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--no-llm", action="store_true", help="Use deterministic RA extraction only")
    parser.add_argument("--alias-only", action="store_true", help="Only rebuild alias.json files; do not generate MEM files")
    parser.add_argument("--include-excluded", action="store_true", help="Include hack/prototype/homebrew/unlicensed RA sources")
    parser.add_argument("--reset-alias", action="store_true", help="Rewrite alias.json for this run instead of merging")
    args = parser.parse_args()

    args.source_base = os.path.abspath(args.source_base)
    print("==========================================================")
    print(f"      mem-curator v{VERSION} (APIExpose)")
    print("==========================================================")
    print(f"Emit profile: {args.emit_profile}")
    print(f"Source base: {args.source_base}")
    print(f"Output dir:  {os.path.join(SCRIPT_DIR, args.output_dir)}")
    print(f"Gamelists:   {args.gamelist_dir}")
    if args.system == "arcade":
        print(f"MAME dir:    {args.mame_dir}")
    print(f"System:      {args.system}")
    if args.game:
        print(f"Game filter: {args.game}")
    if args.alias_only:
        args.no_llm = True
        print("Mode:        alias-only")
    if args.no_llm:
        print("LLM:         disabled")
    else:
        print(f"LLM:         {args.api_type} {args.server_url} / {args.model}")

    system_instruction = load_system_instruction()
    find_ra_files.include_excluded = args.include_excluded

    if args.system.lower() in {"all", "*", "all-systems"}:
        systems = find_available_systems(args.source_base)
        if not systems:
            print(f"[error] no systems found in {os.path.join(args.source_base, 'sources', 'ra')}")
            return 1
        print(f"[+] systems: {len(systems)}")
    else:
        systems = [args.system]

    exit_code = 0
    for system in systems:
        system_args = copy.copy(args)
        system_args.system = system
        try:
            result = process_system_batch(system_args, system_instruction)
            if result != 0:
                exit_code = result
        except Exception as exc:
            print(f"[error] system {system}: {exc}")
            exit_code = 1

    SUMMARY["profile"] = args.emit_profile
    if not args.alias_only:
        summary_path = os.path.join(SCRIPT_DIR, args.output_dir, "_summary.json")
        save_text(summary_path, json.dumps(SUMMARY, indent=2, ensure_ascii=False) + "\n")
        print(f"[+] summary: {summary_path}")
    print("--- all done ---")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())



