import os
import re
import shlex
import json
import copy
import ast
import shutil
import configparser
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime
from urllib.parse import unquote
from urllib import request as urlrequest
import tkinter as tk
from tkinter import ttk, messagebox
from PIL import Image, ImageTk
from profiles_db import (
    SLOT_MAP as DB_SLOT_MAP,
    SYSTEM_SLOT_MAP as DB_SYSTEM_SLOT_MAP,
    JOYSTICK_DIRECTION_MAP as DB_JOYSTICK_DIRECTION_MAP,
    MAME_SLOT_KEYCODES as DB_MAME_SLOT_KEYCODES,
    MAME_SLOT_JOYCODES as DB_MAME_SLOT_JOYCODES,
    MAME_SYSTEM_KEYCODES as DB_MAME_SYSTEM_KEYCODES,
    MAME_SYSTEM_JOYCODES as DB_MAME_SYSTEM_JOYCODES,
    RMP_SLOT_BUTTONS_BY_LAYOUT as DB_RMP_SLOT_BUTTONS_BY_LAYOUT,
    RMP_SYSTEM_BUTTON_MAP as DB_RMP_SYSTEM_BUTTON_MAP,
    PROFILES_LIBRARY as DB_PROFILES_LIBRARY,
)


# ============================================================
# PATHS
# ============================================================

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
RESOURCES_DIR = os.path.join(BASE_DIR, "resources")
APP_INI_PATH = os.path.join(BASE_DIR, "panel_curator.ini")


def _resolve_config_path(value):
    if not value:
        return ""
    value = os.path.expandvars(os.path.expanduser(str(value).strip()))
    if os.path.isabs(value):
        return os.path.normpath(value)
    return os.path.normpath(os.path.join(BASE_DIR, value))


def load_app_ini():
    cfg = configparser.ConfigParser()
    cfg.read_dict({
        "paths": {
            "source_roms_mame_dir": os.path.join("..", "..", "roms", "mame"),
            "source_mame_exe": os.path.join("..", "..", "emulators", "mame", "mame.exe"),
            "source_retroarch_exe": os.path.join("..", "..", "emulators", "retroarch", "retroarch.exe"),
            "source_retroarch_mame_core": os.path.join("..", "..", "emulators", "retroarch", "cores", "mame_libretro.dll"),
            "source_myrient_panel_dir": os.path.join("..", "..", "..", "myrient", "mame-cpanel"),
            "source_resource_cpanel_dir": os.path.join("resources", "artwork", "cpanel"),
            "source_myrient_cabinets_dir": os.path.join("..", "..", "..", "myrient", "mame-cabinets"),
            "source_controls_ini": os.path.join("resources", "controls", "controls.ini"),
            "source_colors_ini": os.path.join("resources", "colors", "colors.ini"),
            "source_mame_cfg_dir": os.path.join("..", "..", "bios", "mame", "cfg"),
            "source_mame_ini_dir": os.path.join("..", "..", "bios", "mame", "ini"),
            "source_es_input_cfg": os.path.join("..", "..", "emulationstation", ".emulationstation", "es_input.cfg"),
            "source_mame_outputs_dir": os.path.join("resources", "outputs", "mame"),
            "source_systems_panel_dir": os.path.join("resources", "panels", "systems"),
            "source_libretro_core_md_dir": os.path.join("resources", "panels", "libreto"),
            "source_arcade_lip_dir": os.path.join("resources", "panels", "mame"),
            "source_genretroarch_py": os.path.join("projects-source", "ES-Panels", "genRetroarch.py"),
            "source_retrobat_bios_dir": os.path.join("..", "..", "bios"),
            "source_mame_cheats_dir": os.path.join("..", "..", "cheats", "mame"),
            "source_mame_artwork_dir": os.path.join("..", "..", "saves", "mame", "artwork"),
            "export_kpanels_mame_dir": os.path.join("resources", "dynpanels", "games"),
            "export_kpanels_systems_dir": os.path.join("resources", "dynpanels", "systems"),
            "export_dynpanels_cores_dir": os.path.join("resources", "dynpanels", "cores"),
            "export_mame_game_cfg_dir": os.path.join("..", "..", "saves", "mame", "cfg"),
            "export_mame_ctrlr_dir": os.path.join("..", "..", "saves", "mame", "ctrlr"),
            "export_mame_player_devices_file": os.path.join("resources", "controls", "mame", "mame_player_devices.json"),
            "export_retroarch_remaps_dir": os.path.join("..", "..", "emulators", "retroarch", "config", "remaps"),
            "export_retrobat_inputmapping_mame_dir": os.path.join("..", "..", "user", "inputmapping", "mame"),
            "export_user_inputmapping_dir": os.path.join("..", "..", "user", "inputmapping"),
            "export_generated_mame_copy_dir": os.path.join("resources", "controls", "mame"),
            "export_generated_retroarch_fbneo_copy_dir": os.path.join("resources", "controls", "retroarch", "fbneo"),
            "export_generated_retrobat_xml_copy_dir": os.path.join("resources", "controls", "retrobat", "mame"),
            "export_retroarch_log_file": os.path.join("..", "..", "emulationstation", ".emulationstation", "es_launch_stdout.log"),
        },
        "export": {
            "retroarch_fbneo_rmp_core_dir": "FinalBurn Neo",
            "mame_cfg_joycode_player": "auto",
            "mame_cfg_joycode_fallback_player": "2",
            "mame_cfg_include_button_keycodes": "false",
            "mame_ctrlr_name": "controls_mapping",
            "panel_layout": "4-Button",
        },
        "launch": {
            "retroarch_extra_args": "--verbose",
            "retroarch_content_template": "{rom_path}",
        },
        "ledpanel": {
            "mode": "ledmanager",
            "apiexpose_base_url": "http://127.0.0.1:12345",
            "port": "",
            "baudrate": "115200",
            "timeout_ms": "350",
            "auto_detect": "true",
            "ledmanager_dir": os.path.join("..", "LedManager"),
            "ledmanager_exe": os.path.join("..", "LedManager", "LedManager.exe"),
            "ledmanager_ini": os.path.join("..", "LedManager", "LedManager.ini"),
        },
    })
    if os.path.exists(APP_INI_PATH):
        cfg.read(APP_INI_PATH, encoding="utf-8")
    return cfg


def cfg_path(name, legacy=None, fallback=""):
    value = APP_CONFIG.get("paths", name, fallback="")
    if (not value) and legacy:
        value = APP_CONFIG.get("paths", legacy, fallback="")
    if not value:
        value = fallback
    return _resolve_config_path(value)


def cfg_text(section, name, fallback=""):
    return APP_CONFIG.get(section, name, fallback=fallback).strip()


APP_CONFIG = load_app_ini()
ROMS_DIR = cfg_path("source_roms_mame_dir", "roms_dir")
MAME_EXE = cfg_path("source_mame_exe", "mame_exe")
RETROARCH_EXE = cfg_path("source_retroarch_exe")
RETROARCH_MAME_CORE = cfg_path("source_retroarch_mame_core")
PANEL_DIR = cfg_path("source_myrient_panel_dir", "panel_dir")
RESOURCE_CPANEL_DIR = cfg_path("source_resource_cpanel_dir", "resource_cpanel_dir", os.path.join("resources", "artwork", "cpanel"))
MYRIENT_CABINETS_DIR = cfg_path("source_myrient_cabinets_dir", "myrient_cabinets_dir")
CONTROLS_INI = cfg_path("source_controls_ini", "controls_ini", os.path.join("resources", "controls", "controls.ini"))
COLORS_INI = cfg_path("source_colors_ini", "colors_ini", os.path.join("resources", "colors", "colors.ini"))
MAME_CFG_DIR = cfg_path("source_mame_cfg_dir", "mame_cfg_dir")
MAME_INI_DIR = cfg_path("source_mame_ini_dir", fallback=os.path.join("..", "..", "bios", "mame", "ini"))
ES_INPUT_CFG = cfg_path("source_es_input_cfg", fallback=os.path.join("..", "..", "emulationstation", ".emulationstation", "es_input.cfg"))
MAME_GAME_CFG_DIR = cfg_path("export_mame_game_cfg_dir", fallback=os.path.join("..", "..", "saves", "mame", "cfg"))
MAME_CTRLR_DIR = cfg_path("export_mame_ctrlr_dir", "mame_ctrlr_dir", os.path.join("..", "..", "saves", "mame", "ctrlr"))
MAME_PLAYER_DEVICES_FILE = cfg_path("export_mame_player_devices_file", fallback=os.path.join("resources", "controls", "mame", "mame_player_devices.json"))
RETROARCH_REMAPS_DIR = cfg_path("export_retroarch_remaps_dir", "retroarch_remaps_dir")
RETROBAT_INPUTMAPPING_MAME_DIR = cfg_path("export_retrobat_inputmapping_mame_dir", fallback=os.path.join("..", "..", "user", "inputmapping", "mame"))
USER_INPUTMAPPING_DIR = cfg_path("export_user_inputmapping_dir", fallback=os.path.join("..", "..", "user", "inputmapping"))
RETROARCH_FBNEO_RMP_CORE_DIR = APP_CONFIG.get("export", "retroarch_fbneo_rmp_core_dir", fallback="FinalBurn Neo")
MAME_CFG_JOYCODE_PLAYER = APP_CONFIG.get("export", "mame_cfg_joycode_player", fallback="auto").strip()
MAME_CFG_JOYCODE_FALLBACK_PLAYER = APP_CONFIG.getint("export", "mame_cfg_joycode_fallback_player", fallback=2)
MAME_CFG_INCLUDE_BUTTON_KEYCODES = APP_CONFIG.getboolean("export", "mame_cfg_include_button_keycodes", fallback=False)
MAME_CTRLR_NAME = APP_CONFIG.get("export", "mame_ctrlr_name", fallback="controls_mapping").strip() or "controls_mapping"
MAME_OUTPUTS_DIR = cfg_path("source_mame_outputs_dir", "mame_outputs_dir", os.path.join("resources", "outputs", "mame"))
SYSTEMS_PANEL_DIR = cfg_path("source_systems_panel_dir", "systems_panel_dir", os.path.join("resources", "panels", "systems"))
LIBRETRO_CORE_MD_DIR = cfg_path("source_libretro_core_md_dir", fallback=os.path.join("resources", "panels", "libreto"))
ARCADE_LIP_DIR = cfg_path("source_arcade_lip_dir", "arcade_lip_dir", os.path.join("resources", "panels", "mame"))
GENRETROARCH_SOURCE = cfg_path("source_genretroarch_py", fallback=os.path.join("projects-source", "ES-Panels", "genRetroarch.py"))
RETROBAT_BIOS_DIR = cfg_path("source_retrobat_bios_dir")
MAME_CHEATS_DIR = cfg_path("source_mame_cheats_dir")
MAME_ARTWORK_DIR = cfg_path("source_mame_artwork_dir")
OUTPUT_DIR = cfg_path("export_kpanels_mame_dir", "output_dir", os.path.join("resources", "dynpanels", "games"))
SYSTEM_OUTPUT_DIR = cfg_path("export_kpanels_systems_dir", "system_output_dir", os.path.join("resources", "dynpanels", "systems"))
CORE_OUTPUT_DIR = cfg_path("export_dynpanels_cores_dir", fallback=os.path.join("resources", "dynpanels", "cores"))
GENERATED_MAME_COPY_DIR = cfg_path("export_generated_mame_copy_dir", "generated_mame_copy_dir", os.path.join("resources", "controls", "mame"))
GENERATED_RETROARCH_FBNEO_COPY_DIR = cfg_path("export_generated_retroarch_fbneo_copy_dir", "generated_retroarch_fbneo_copy_dir", os.path.join("resources", "controls", "retroarch", "fbneo"))
GENERATED_RETROBAT_XML_COPY_DIR = cfg_path("export_generated_retrobat_xml_copy_dir", fallback=os.path.join("resources", "controls", "retrobat", "mame"))
RETROARCH_LOG_FILE = cfg_path("export_retroarch_log_file")
RETROARCH_EXTRA_ARGS = cfg_text("launch", "retroarch_extra_args", "--verbose")
RETROARCH_CONTENT_TEMPLATE = cfg_text("launch", "retroarch_content_template", "{rom_path}")
LEDPANEL_PORT = cfg_text("ledpanel", "port", "")
LEDPANEL_BAUDRATE = APP_CONFIG.getint("ledpanel", "baudrate", fallback=115200)
LEDPANEL_TIMEOUT_MS = APP_CONFIG.getint("ledpanel", "timeout_ms", fallback=350)
LEDPANEL_AUTO_DETECT = APP_CONFIG.getboolean("ledpanel", "auto_detect", fallback=True)
LEDPANEL_MODE = cfg_text("ledpanel", "mode", "ledmanager").lower()
LEDPANEL_APIEXPOSE_BASE_URL = cfg_text("ledpanel", "apiexpose_base_url", "http://127.0.0.1:12345").rstrip("/")
LEDMANAGER_DIR = _resolve_config_path(cfg_text("ledpanel", "ledmanager_dir", os.path.join("..", "LedManager")))
LEDMANAGER_EXE = _resolve_config_path(cfg_text("ledpanel", "ledmanager_exe", os.path.join("..", "LedManager", "LedManager.exe")))
LEDMANAGER_INI = _resolve_config_path(cfg_text("ledpanel", "ledmanager_ini", os.path.join("..", "LedManager", "LedManager.ini")))

os.makedirs(OUTPUT_DIR, exist_ok=True)
os.makedirs(SYSTEM_OUTPUT_DIR, exist_ok=True)
os.makedirs(CORE_OUTPUT_DIR, exist_ok=True)
os.makedirs(USER_INPUTMAPPING_DIR, exist_ok=True)
os.makedirs(GENERATED_MAME_COPY_DIR, exist_ok=True)
os.makedirs(GENERATED_RETROARCH_FBNEO_COPY_DIR, exist_ok=True)
os.makedirs(GENERATED_RETROBAT_XML_COPY_DIR, exist_ok=True)

ROM_EXTS = {".zip", ".7z", ".chd", ".cue", ".bin", ".iso", ".rom"}

LIBRETRO_MAME_BUTTON_IDS = {
    1: 0,
    2: 8,
    3: 1,
    4: 9,
    5: 10,
    6: 11,
    7: 12,
    8: 13,
    9: 14,
    10: 15,
}

LIBRETRO_USER_YAML_VARIANTS = {
    "default": ["btn_y", "btn_b", "btn_a", "btn_x", "btn_l", "btn_r", "btn_l2", "btn_r2"],
    "modern8": ["btn_y", "btn_x", "btn_r", "btn_b", "btn_a", "btn_r2", "btn_l", "btn_l2"],
    "8alternative": ["btn_b", "btn_a", "btn_y", "btn_x", "btn_l", "btn_r", "btn_l2", "btn_r2"],
    "6alternative": ["btn_y", "btn_x", "btn_l", "btn_b", "btn_a", "btn_r"],
}

LIBRETRO_USER_YAML_BUTTON_ORDER = ["btn_a", "btn_b", "btn_x", "btn_y", "btn_l", "btn_r", "btn_l2", "btn_r2"]

LEDPANEL_COLOR_MAP = {
    "red": "RED",
    "blue": "BLUE",
    "cyan": "CYAN",
    "lime": "LIME",
    "green": "GREEN",
    "yellow": "YELLOW",
    "orange": "ORANGE",
    "white": "WHITE",
    "black": "BLACK",
    "pink": "PINK",
    "violet": "VIOLET",
    "purple": "PURPLE",
    "gray": "GRAY",
    "grey": "GRAY",
}


def ledpanel_color_name(name, default="WHITE"):
    key = str(name or "").strip().lower()
    if key in LEDPANEL_COLOR_MAP:
        return LEDPANEL_COLOR_MAP[key]
    canonical = canonical_color_name(name, default)
    return LEDPANEL_COLOR_MAP.get(str(canonical or default).strip().lower(), LEDPANEL_COLOR_MAP.get(default.lower(), "WHITE"))


def powershell_single_quote(text):
    return str(text).replace("'", "''")


def datetime_stamp():
    return datetime.now().strftime("%Y%m%d-%H%M%S")


def normalize_mame_mapdevice_id(device_id):
    text = str(device_id or "")
    match = re.search(r"\b(product_[0-9a-fA-F]+)\b", text)
    if match:
        return match.group(1).lower()
    match = re.search(r"VID_([0-9a-fA-F]{4})&PID_([0-9a-fA-F]{4})", text)
    if match:
        return f"VID_{match.group(1).upper()}&PID_{match.group(2).upper()}"
    return text.strip()


def normalize_hardware_label(text):
    return re.sub(r"\s+", " ", str(text or "")).strip()


def combine_hardware_labels(*labels):
    parts = []
    seen = set()
    for label in labels:
        for part in str(label or "").split("/"):
            normalized = normalize_hardware_label(part)
            if not normalized:
                continue
            key = normalized.lower()
            if key in seen:
                continue
            seen.add(key)
            parts.append(normalized)
    return " / ".join(parts)


def mame_product_from_es_guid(guid):
    text = re.sub(r"[^0-9a-fA-F]", "", str(guid or "")).lower()
    if len(text) < 24:
        return ""
    vendor = text[8:16]
    product = text[16:24]
    if vendor == "00000000" or product == "00000000":
        return ""
    vendor_id = vendor[2:4] + vendor[0:2]
    product_id = product[2:4] + product[0:2]
    return f"product_{product_id}{vendor_id}"


def mame_joycode_number(value):
    match = re.search(r"JOYCODE_(\d+)", str(value or "").upper())
    return int(match.group(1)) if match else None


def mame_device_choice_label(mapping, include_joycode=False):
    joycode = str(mapping.get("joycode") or "").upper() if include_joycode else ""
    label = normalize_hardware_label(mapping.get("label") or "")
    device = str(mapping.get("device") or "").strip()
    parts = [part for part in (joycode, label, device) if part]
    return " - ".join(parts) if parts else "No hardware mapping"


def manual_mame_device_choices():
    return [{
        "joycode": "",
        "label": "No hardware mapping",
        "device": "",
        "source": "manual",
    }]


class LedPanelBridge:
    def __init__(
        self,
        port="",
        baudrate=115200,
        timeout_ms=350,
        auto_detect=True,
        mode="serial",
        apiexpose_base_url="",
        ledmanager_dir="",
        ledmanager_exe="",
        ledmanager_ini="",
    ):
        self.port = (port or "").strip()
        self.baudrate = int(baudrate or 115200)
        self.timeout_ms = max(int(timeout_ms or 350), 150)
        self.auto_detect = bool(auto_detect)
        self.mode = (mode or "serial").strip().lower()
        self.apiexpose_base_url = (apiexpose_base_url or "").strip().rstrip("/")
        self.ledmanager_dir = os.path.normpath(ledmanager_dir or "")
        self.ledmanager_exe = os.path.normpath(ledmanager_exe or "")
        self.ledmanager_ini = os.path.normpath(ledmanager_ini or "")
        self.ledmanager_process = None
        self.ledmanager_runtime_ini = (
            os.path.join(self.ledmanager_dir, "apiexpose-curator-ledmanager.ini")
            if self.ledmanager_dir else ""
        )
        self.connected = False
        self.last_port = ""
        self.last_error = ""

    def _ledmanager_runtime_ini_path(self):
        if not self.ledmanager_dir or not self.ledmanager_ini or not os.path.exists(self.ledmanager_ini):
            return ""
        cfg = configparser.ConfigParser()
        cfg.read(self.ledmanager_ini, encoding="utf-8")
        if not cfg.has_section("APIExpose"):
            cfg.add_section("APIExpose")
        cfg.set("APIExpose", "Enabled", "false")
        runtime_ini = self.ledmanager_runtime_ini
        os.makedirs(os.path.dirname(runtime_ini), exist_ok=True)
        with open(runtime_ini, "w", encoding="utf-8") as f:
            cfg.write(f)
        return runtime_ini

    def _start_ledmanager(self):
        if self.ledmanager_process and self.ledmanager_process.poll() is None:
            return True, "LedManager", "running"
        if not self.ledmanager_exe or not os.path.exists(self.ledmanager_exe):
            return False, "", f"LedManager executable not found: {self.ledmanager_exe}"
        runtime_ini = self._ledmanager_runtime_ini_path()
        if not runtime_ini:
            return False, "", f"LedManager ini not found: {self.ledmanager_ini}"
        try:
            self.ledmanager_process = subprocess.Popen(
                [self.ledmanager_exe, "--ini", runtime_ini],
                cwd=self.ledmanager_dir or None,
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                text=True,
                encoding="utf-8",
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except Exception as exc:
            return False, "", str(exc)
        return True, "LedManager", "started"

    def _build_panel_preview_event(self, slot_map):
        normalized = {
            str(slot): ledpanel_color_name(color, default="WHITE")
            for slot, color in (slot_map or {}).items()
        }
        slots = [
            {
                "Slot": slot,
                "Player": 1,
                "Color": normalized.get(str(slot), "BLACK"),
            }
            for slot in range(1, 9)
        ]
        event = {
            "stream": "panel",
            "type": "panel.state",
            "Source": "panel_curator.preview",
            "system": "panel_curator",
            "rom": "preview",
            "ActivePanel": {
                "Id": "panel-curator-preview",
                "Slots": slots,
            },
            "ActiveLayout": {
                "Id": "Panel Curator Preview",
            },
        }
        return event

    def _post_apiexpose_panel_preview(self, slot_map):
        if not self.apiexpose_base_url:
            return False, "", "APIExpose base URL is not configured"
        event = self._build_panel_preview_event(slot_map)
        url = self.apiexpose_base_url + "/api/v1/Panels/preview"
        payload = json.dumps(event, separators=(",", ":")).encode("utf-8")
        req = urlrequest.Request(
            url,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlrequest.urlopen(req, timeout=max(0.2, self.timeout_ms / 1000)) as response:
                if 200 <= response.status < 300:
                    self.connected = True
                    self.last_port = "APIExpose"
                    self.last_error = ""
                    return True, "APIExpose", "preview posted"
                detail = f"HTTP {response.status}"
        except Exception as exc:
            detail = str(exc)

        self.connected = False
        self.last_error = detail
        return False, "", detail

    def _send_ledmanager_panel_state(self, slot_map):
        ok, port, detail = self._post_apiexpose_panel_preview(slot_map)
        if ok:
            return ok, port, detail

        ok, port, detail = self._start_ledmanager()
        if not ok:
            self.connected = False
            self.last_error = detail
            return False, "", detail
        if not self.ledmanager_process or self.ledmanager_process.poll() is not None or not self.ledmanager_process.stdin:
            self.connected = False
            self.last_error = "LedManager process is not available"
            return False, "", self.last_error

        event = self._build_panel_preview_event(slot_map)
        try:
            self.ledmanager_process.stdin.write(json.dumps(event, separators=(",", ":")) + "\n")
            self.ledmanager_process.stdin.flush()
        except Exception as exc:
            self.connected = False
            self.last_error = str(exc)
            return False, "", self.last_error

        self.connected = True
        self.last_port = port
        self.last_error = ""
        return True, port, detail

    def _powershell_script(self, commands=None):
        port_literal = powershell_single_quote(self.port)
        timeout_ms = self.timeout_ms
        baudrate = self.baudrate
        auto_detect = "$true" if self.auto_detect else "$false"
        command_block = ""
        if commands:
            quoted = ",".join(f"'{powershell_single_quote(cmd)}'" for cmd in commands if cmd)
            command_block = f"$commands = @({quoted})"
        else:
            command_block = "$commands = @()"

        return f"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$preferredPort = '{port_literal}'
$baudRate = {baudrate}
$timeoutMs = {timeout_ms}
$autoDetect = {auto_detect}
{command_block}

function Test-LedPanelPort([string]$portName, [string[]]$commands) {{
    $serial = $null
    try {{
        $serial = New-Object System.IO.Ports.SerialPort $portName, $baudRate, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
        $serial.NewLine = "`n"
        $serial.ReadTimeout = $timeoutMs
        $serial.WriteTimeout = $timeoutMs
        $serial.DtrEnable = $false
        $serial.RtsEnable = $false
        $serial.Open()
        Start-Sleep -Milliseconds 120
        $serial.DiscardInBuffer()
        $serial.DiscardOutBuffer()
        $serial.WriteLine('PING')
        Start-Sleep -Milliseconds 80
        $buffer = ''
        $deadline = [Environment]::TickCount + $timeoutMs
        while ([Environment]::TickCount -lt $deadline) {{
            try {{
                $buffer += $serial.ReadExisting()
            }} catch {{}}
            if ($buffer -match 'PONG') {{
                break
            }}
            Start-Sleep -Milliseconds 20
        }}
        if ($buffer -notmatch 'PONG') {{
            throw "No PONG response"
        }}
        foreach ($cmd in $commands) {{
            if ([string]::IsNullOrWhiteSpace($cmd)) {{
                continue
            }}
            $serial.WriteLine($cmd)
            Start-Sleep -Milliseconds 15
        }}
        return @{{
            ok = $true
            port = $portName
            message = 'connected'
        }}
    }} catch {{
        return @{{
            ok = $false
            port = $portName
            message = $_.Exception.Message
        }}
    }} finally {{
        if ($serial) {{
            try {{ $serial.Close() }} catch {{}}
            try {{ $serial.Dispose() }} catch {{}}
        }}
    }}
}}

$ports = @()
if ($preferredPort) {{
    $ports += $preferredPort
}}
if ($autoDetect) {{
    foreach ($p in [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object) {{
        if ($ports -notcontains $p) {{
            $ports += $p
        }}
    }}
}}
if (-not $ports) {{
    throw 'No serial port configured or detected.'
}}

$lastFailure = $null
foreach ($port in $ports) {{
    $result = Test-LedPanelPort -portName $port -commands $commands
    if ($result.ok) {{
        Write-Output ('OK|' + $result.port + '|' + $result.message)
        exit 0
    }}
    $lastFailure = $result
}}

if ($lastFailure) {{
    throw ('Unable to reach LED panel on ' + $lastFailure.port + ': ' + $lastFailure.message)
}}
throw 'Unable to reach LED panel.'
"""

    def _run(self, commands=None):
        try:
            completed = subprocess.run(
                [
                    "powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    self._powershell_script(commands=commands),
                ],
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=max(5, int((self.timeout_ms * 4) / 1000)),
                check=False,
            )
        except Exception as exc:
            self.connected = False
            self.last_error = str(exc)
            return False, "", self.last_error

        stdout = (completed.stdout or "").strip()
        stderr = (completed.stderr or "").strip()
        if completed.returncode == 0 and stdout.startswith("OK|"):
            parts = stdout.split("|", 2)
            self.connected = True
            self.last_port = parts[1] if len(parts) > 1 else ""
            self.last_error = ""
            return True, self.last_port, parts[2] if len(parts) > 2 else "connected"

        self.connected = False
        self.last_error = stderr or stdout or f"exit code {completed.returncode}"
        return False, "", self.last_error

    def probe(self):
        if self.mode == "ledmanager":
            if self.apiexpose_base_url:
                try:
                    with urlrequest.urlopen(self.apiexpose_base_url + "/api/v1/Health", timeout=max(0.2, self.timeout_ms / 1000)) as response:
                        if 200 <= response.status < 300:
                            self.connected = True
                            self.last_port = "APIExpose"
                            self.last_error = ""
                            return True, self.last_port, "APIExpose active"
                except Exception:
                    pass
            ok, port, detail = self._start_ledmanager()
            self.connected = ok
            self.last_port = port if ok else ""
            self.last_error = "" if ok else detail
            return ok, self.last_port, detail
        return self._run(commands=None)

    def send_slots(self, slot_map):
        if self.mode == "ledmanager":
            return self._send_ledmanager_panel_state(slot_map)
        commands = ["CLEAR"]
        for slot in sorted(slot_map, key=lambda value: int(str(value)) if str(value).isdigit() else str(value)):
            color = ledpanel_color_name(slot_map[slot], default="WHITE")
            commands.append(f"SLOT {slot} {color}")
        return self._run(commands=commands)


# ============================================================
# CONVENTIONS
# ============================================================

PANEL_CONVENTION = "retrobat_standard"

LIBRETRO_RETROPAD_IDS = {
    "b": 0,
    "y": 1,
    "select": 2,
    "start": 3,
    "dpad_up": 4,
    "dpad_down": 5,
    "dpad_left": 6,
    "dpad_right": 7,
    "a": 8,
    "x": 9,
    "l1": 10,
    "r1": 11,
    "l2": 12,
    "r2": 13,
    "l3": 14,
    "r3": 15,
}


def _normalize_profile_entry(entry, fallback=None):
    fallback = fallback or {}
    return {
        "retrobat_button": entry.get("retrobat", fallback.get("retrobat_button", "")),
        "retropad_id": entry.get("retropad_id", fallback.get("retropad_id")),
        "libretro_button": entry.get("libretro", fallback.get("libretro_button", "")),
        "fbneo_button": entry.get("dinput", fallback.get("fbneo_button", "")),
        "rmp_button": entry.get("rmp_button", fallback.get("rmp_button")),
        "mame_button": entry.get("mame_btn", fallback.get("mame_button", "")),
    }


SLOT_MAP = {
    key: _normalize_profile_entry(value)
    for key, value in DB_SLOT_MAP.items()
}

SYSTEM_SLOT_MAP = {
    key: _normalize_profile_entry(value, {"rmp_button": DB_RMP_SYSTEM_BUTTON_MAP.get(key)})
    for key, value in DB_SYSTEM_SLOT_MAP.items()
}

RMP_SLOT_BUTTONS_BY_LAYOUT = {
    layout_name: dict(mapping)
    for layout_name, mapping in DB_RMP_SLOT_BUTTONS_BY_LAYOUT.items()
}
RMP_SYSTEM_BUTTON_MAP = DB_RMP_SYSTEM_BUTTON_MAP
PROFILES_LIBRARY = DB_PROFILES_LIBRARY


def slot_source_retropad_id(slot):
    mapping = SLOT_MAP.get(str(slot), {})
    return mapping.get("retropad_id")


def system_source_retropad_id(system_key):
    mapping = SYSTEM_SLOT_MAP.get(system_key, {})
    return mapping.get("retropad_id")


def mame_source_retropad_id(mame):
    raw = str((mame or {}).get("type") or (mame or {}).get("input_id") or "").upper()
    match = re.search(r"(?:P\d+_)?BUTTON(\d+)$", raw)
    if match:
        return slot_source_retropad_id(match.group(1))
    if re.search(r"(?:P\d+_)?START\d*$", raw):
        return system_source_retropad_id("start")
    if raw in {"COIN", "COIN1", "COIN2", "P1_COIN", "P2_COIN", "SELECT"}:
        return system_source_retropad_id("coin")
    return None


def logical_button_source_retropad_id(button):
    game_button = button.get("game_button")
    if str(game_button).isdigit():
        retropad_id = slot_source_retropad_id(game_button)
        if retropad_id is not None:
            return retropad_id
    return mame_source_retropad_id(button.get("mame", {}))

JOYSTICK_DIRECTION_MAP = {
    key: {
        "retrobat_button": value.get("retrobat", ""),
        "retropad_id": value.get("retropad_id"),
        "libretro_button": value.get("libretro", ""),
        "fbneo_button": value.get("dinput", ""),
    }
    for key, value in DB_JOYSTICK_DIRECTION_MAP.items()
}

MAME_SLOT_KEYCODES = DB_MAME_SLOT_KEYCODES
MAME_SLOT_JOYCODES = DB_MAME_SLOT_JOYCODES
MAME_SYSTEM_KEYCODES = DB_MAME_SYSTEM_KEYCODES
MAME_SYSTEM_JOYCODES = DB_MAME_SYSTEM_JOYCODES

MAME_JOYSTICK_KEYCODES = {
    "up": "KEYCODE_UP",
    "down": "KEYCODE_DOWN",
    "left": "KEYCODE_LEFT",
    "right": "KEYCODE_RIGHT",
    "up_left": "KEYCODE_UP KEYCODE_LEFT",
    "up_right": "KEYCODE_UP KEYCODE_RIGHT",
    "down_left": "KEYCODE_DOWN KEYCODE_LEFT",
    "down_right": "KEYCODE_DOWN KEYCODE_RIGHT",
}

MAME_JOYSTICK_JOYCODES = {
    "up": "JOYCODE_{player}_HAT1UP OR JOYCODE_{player}_YAXIS_UP_SWITCH",
    "down": "JOYCODE_{player}_HAT1DOWN OR JOYCODE_{player}_YAXIS_DOWN_SWITCH",
    "left": "JOYCODE_{player}_HAT1LEFT OR JOYCODE_{player}_XAXIS_LEFT_SWITCH",
    "right": "JOYCODE_{player}_HAT1RIGHT OR JOYCODE_{player}_XAXIS_RIGHT_SWITCH",
    "up_left": (
        "JOYCODE_{player}_HAT1UP JOYCODE_{player}_HAT1LEFT OR "
        "JOYCODE_{player}_YAXIS_UP_SWITCH JOYCODE_{player}_XAXIS_LEFT_SWITCH"
    ),
    "up_right": (
        "JOYCODE_{player}_HAT1UP JOYCODE_{player}_HAT1RIGHT OR "
        "JOYCODE_{player}_YAXIS_UP_SWITCH JOYCODE_{player}_XAXIS_RIGHT_SWITCH"
    ),
    "down_left": (
        "JOYCODE_{player}_HAT1DOWN JOYCODE_{player}_HAT1LEFT OR "
        "JOYCODE_{player}_YAXIS_DOWN_SWITCH JOYCODE_{player}_XAXIS_LEFT_SWITCH"
    ),
    "down_right": (
        "JOYCODE_{player}_HAT1DOWN JOYCODE_{player}_HAT1RIGHT OR "
        "JOYCODE_{player}_YAXIS_DOWN_SWITCH JOYCODE_{player}_XAXIS_RIGHT_SWITCH"
    ),
}

RMP_BUTTON_OUTPUT_ORDER = ["a", "b", "l", "l2", "r", "r2", "x", "y"]

RMP_STATIC_LINES = [
    'input_turbo_allow_dpad = "false"',
    'input_turbo_bind = "-1"',
    'input_turbo_button = "0"',
    'input_turbo_duty_cycle = "0"',
    'input_turbo_enable = "false"',
    'input_turbo_mode = "0"',
    'input_turbo_period = "6"',
]

GENERIC_JOYSTICK_PORT_BLOCKS = {"model2"}

ANALOG_INPUT_TYPES = {
    "PADDLE",
    "PADDLE_V",
    "PEDAL",
    "PEDAL2",
    "PEDAL3",
    "DIAL",
    "DIAL_V",
    "TRACKBALL_X",
    "TRACKBALL_Y",
    "AD_STICK_X",
    "AD_STICK_Y",
    "AD_STICK_Z",
    "LIGHTGUN_X",
    "LIGHTGUN_Y",
}

LAYOUTS = ("2-Button", "4-Button", "6-Button", "8-Button")

JOYSTICK_PANEL_DIRECTIONS = (
    ("up_left", "UL"),
    ("up", "UP"),
    ("up_right", "UR"),
    ("left", "LEFT"),
    ("right", "RIGHT"),
    ("down_left", "DL"),
    ("down", "DOWN"),
    ("down_right", "DR"),
)

JOYSTICK_PANEL_COORDS = {
    "up_left": (95, 36),
    "up": (170, 30),
    "up_right": (245, 36),
    "left": (75, 96),
    "right": (265, 96),
    "down_left": (95, 156),
    "down": (170, 162),
    "down_right": (245, 156),
}

LAYOUT_SLOTS = {
    "2-Button": [1, 2],
    "4-Button": [4, 3, 1, 2],
    "6-Button": [4, 3, 5, 1, 2, 6],
    "8-Button": [4, 3, 5, 7, 1, 2, 6, 8],
}

GRID_COORDS = {
    "2-Button": {
        1: (120, 125),
        2: (300, 125),
    },
    "4-Button": {
        4: (100, 68),
        3: (300, 68),
        1: (100, 162),
        2: (300, 162),
    },
    "6-Button": {
        4: (82, 68),
        3: (210, 68),
        5: (338, 68),
        1: (82, 162),
        2: (210, 162),
        6: (338, 162),
    },
    "8-Button": {
        4: (65, 68),
        3: (170, 68),
        5: (275, 68),
        7: (380, 68),
        1: (65, 162),
        2: (170, 162),
        6: (275, 162),
        8: (380, 162),
    },
}

RETROBAT_XML_LAYOUT_VARIANTS = {
    "default": ["JOY_west", "JOY_south", "JOY_east", "JOY_north", "JOY_l1", "JOY_r1", "JOY_l2trigger", "JOY_r2trigger"],
    "modern8": ["JOY_west", "JOY_north", "JOY_r1", "JOY_south", "JOY_east", "JOY_r2trigger", "JOY_l1", "JOY_l2trigger"],
    "6alternative": ["JOY_west", "JOY_north", "JOY_l1", "JOY_south", "JOY_east", "JOY_r1"],
    "8alternative": ["JOY_south", "JOY_east", "JOY_west", "JOY_north", "JOY_l1", "JOY_r1", "JOY_l2trigger", "JOY_r2trigger"],
}

RETROBAT_XML_JOYSTICK_MAP = {
    "left": "JOY_left OR JOY_lsleft",
    "right": "JOY_right OR JOY_lsright",
    "up": "JOY_up OR JOY_lsup",
    "down": "JOY_down OR JOY_lsdown",
}

COLOR_MAP = {
    "Red": "#d23c3c",
    "Blue": "#3b82f6",
    "Cyan": "#06b6d4",
    "Lime": "#84cc16",
    "Green": "#22c55e",
    "Yellow": "#eab308",
    "Orange": "#f97316",
    "White": "#f3f4f6",
    "Black": "#111827",
    "Pink": "#ec4899",
    "Violet": "#8b5cf6",
    "Purple": "#8b5cf6",
    "Gray": "#9ca3af",
    "Grey": "#9ca3af",
}

COLOR_CHOICES = list(COLOR_MAP.keys())
COLOR_NAME_INDEX = {name.lower(): name for name in COLOR_MAP}

DEVICE_TYPE_CHOICES = [
    "unknown",
    "joy2wayhorizontal",
    "joy2wayvertical",
    "joy4way",
    "joy8way",
    "double_joystick",
    "double_joystick_4way",
    "double_joystick_8way",
    "double_joystick_2way_vertical",
    "triggerstick",
    "top_fire_joystick",
    "rotary_joystick",
    "mechanical_rotary_joystick",
    "optical_rotary_joystick",
    "trackball",
    "spinner",
    "dial",
    "vertical_dial",
    "paddle",
    "vertical_paddle",
    "wheel",
    "pedal",
    "pedal2",
    "pedal3",
    "analog_stick",
    "yoke",
    "throttle",
    "shifter",
    "turntable",
    "roller",
    "handlebar",
    "gun",
    "lightgun",
    "mahjong_panel",
    "hanafuda_panel",
    "keypad",
    "gambling_panel",
    "poker_panel",
    "slot_panel",
    "misc",
    "only_buttons",
]

PATTERN_LIBRARY = {
    "line_1": {
        "1": {"2-Button": 1, "4-Button": 1, "6-Button": 1, "8-Button": 1},
    },
    "line_2": {
        "1": {"2-Button": 1, "4-Button": 1, "6-Button": 1, "8-Button": 1},
        "2": {"2-Button": 2, "4-Button": 2, "6-Button": 2, "8-Button": 2},
    },
    "line_3_bottom": {
        "1": {"6-Button": 1, "8-Button": 1},
        "2": {"6-Button": 2, "8-Button": 2},
        "3": {"6-Button": 6, "8-Button": 6},
    },
    "full_8": {
        "1": {"2-Button": 1, "4-Button": 1, "6-Button": 1, "8-Button": 1},
        "2": {"2-Button": 2, "4-Button": 2, "6-Button": 2, "8-Button": 2},
        "3": {"4-Button": 3, "6-Button": 3, "8-Button": 3},
        "4": {"4-Button": 4, "6-Button": 4, "8-Button": 4},
        "5": {"6-Button": 5, "8-Button": 5},
        "6": {"6-Button": 6, "8-Button": 6},
        "7": {"8-Button": 7},
        "8": {"8-Button": 8},
    },
    "triangle_top1": {
        "1": {"6-Button": 3, "8-Button": 3},
        "2": {"6-Button": 1, "8-Button": 1},
        "3": {"6-Button": 6, "8-Button": 6},
    },
    "square_4": {
        "1": {"4-Button": 4, "6-Button": 4, "8-Button": 4},
        "2": {"4-Button": 3, "6-Button": 3, "8-Button": 3},
        "3": {"4-Button": 1, "6-Button": 1, "8-Button": 1},
        "4": {"4-Button": 2, "6-Button": 2, "8-Button": 2},
    },
    "neo_4": {
        "1": {"4-Button": 4, "6-Button": 1, "8-Button": 1},
        "2": {"4-Button": 1, "6-Button": 4, "8-Button": 3},
        "3": {"4-Button": 3, "6-Button": 3, "8-Button": 5},
        "4": {"4-Button": 2, "6-Button": 5, "8-Button": 7},
        "A": {"4-Button": 4, "6-Button": 1, "8-Button": 1},
        "B": {"4-Button": 1, "6-Button": 4, "8-Button": 3},
        "C": {"4-Button": 3, "6-Button": 3, "8-Button": 5},
        "D": {"4-Button": 2, "6-Button": 5, "8-Button": 7},
    },
    "neo_3": {
        "1": {"6-Button": 1, "8-Button": 1},
        "2": {"6-Button": 2, "8-Button": 2},
        "3": {"6-Button": 6, "8-Button": 6},
        "A": {"6-Button": 1, "8-Button": 1},
        "B": {"6-Button": 2, "8-Button": 2},
        "C": {"6-Button": 6, "8-Button": 6},
    },
    "capcom_6_straight": {
        "1": {"6-Button": 4, "8-Button": 4},
        "2": {"6-Button": 3, "8-Button": 3},
        "3": {"6-Button": 5, "8-Button": 5},
        "4": {"6-Button": 1, "8-Button": 1},
        "5": {"6-Button": 2, "8-Button": 2},
        "6": {"6-Button": 6, "8-Button": 6},
    },
    "mk5_block": {
        "HP": {"6-Button": 4, "8-Button": 4},
        "BL": {"6-Button": 3, "8-Button": 5},
        "HK": {"6-Button": 5, "8-Button": 7},
        "LP": {"6-Button": 1, "8-Button": 2},
        "LK": {"6-Button": 6, "8-Button": 8},
    },
    "mk6": {
        "HP": {"6-Button": 4, "8-Button": 4},
        "BL": {"6-Button": 3, "8-Button": 5},
        "HK": {"6-Button": 5, "8-Button": 7},
        "LP": {"6-Button": 1, "8-Button": 2},
        "RN": {"6-Button": 2, "8-Button": 1},
        "LK": {"6-Button": 6, "8-Button": 8},
    },
}

PATTERN_CHOICES = list(PATTERN_LIBRARY.keys())


# ============================================================
# HELPERS
# ============================================================

def load_ini(path: str) -> configparser.ConfigParser:
    if not os.path.exists(path):
        raise RuntimeError(f"INI file not found: {path}")

    last_error = None
    for enc in ("utf-8-sig", "utf-8", "cp1252", "latin-1"):
        try:
            cp = configparser.ConfigParser(
                interpolation=None,
                strict=False,
                delimiters=("=",),
                comment_prefixes=("#", ";"),
                inline_comment_prefixes=None,
                allow_no_value=True,
            )
            cp.optionxform = str

            with open(path, "r", encoding=enc, errors="ignore") as f:
                content = f.read()

            content = content.replace("\x00", "").replace("\ufeff", "")

            cleaned_lines = []
            for line in content.splitlines():
                stripped = line.strip()
                if not stripped:
                    cleaned_lines.append("")
                elif stripped.startswith("[") and stripped.endswith("]"):
                    cleaned_lines.append(line)
                elif stripped.startswith(";") or stripped.startswith("#"):
                    cleaned_lines.append(line)
                elif "=" in line:
                    cleaned_lines.append(line)
                else:
                    cleaned_lines.append(";" + line)

            cp.read_string("\n".join(cleaned_lines))
            return cp
        except Exception as e:
            last_error = e

        raise RuntimeError(f"Unable to read INI file: {path}\nLast error: {repr(last_error)}")


def safe_get(section, key, default=""):
    if section is None:
        return default
    try:
        return section.get(key, fallback=default)
    except Exception:
        return default


def list_roms():
    roms = set()
    if not os.path.isdir(ROMS_DIR):
        return []

    for name in os.listdir(ROMS_DIR):
        full = os.path.join(ROMS_DIR, name)
        if os.path.isdir(full):
            continue
        base, ext = os.path.splitext(name)
        if ext.lower() in ROM_EXTS:
            roms.add(base.lower())

    roms = sorted(roms)
    return roms


def find_rom_content_path(rom):
    if not os.path.isdir(ROMS_DIR):
        return ""
    candidates = []
    for ext in sorted(ROM_EXTS):
        path = os.path.join(ROMS_DIR, rom + ext)
        if os.path.exists(path):
            candidates.append(path)
    return candidates[0] if candidates else ""


def find_exact_image_in_dir(directory, rom, extensions=(".png",)):
    if not os.path.isdir(directory):
        return ""
    for ext in extensions:
        exact = os.path.join(directory, rom + ext)
        if os.path.exists(exact):
            return exact
    return ""


def find_fuzzy_image_in_dir(directory, rom, extensions=(".png",)):
    if not os.path.isdir(directory):
        return ""
    candidates = []
    prefix = rom.lower()
    for name in os.listdir(directory):
        base, ext = os.path.splitext(name)
        if ext.lower() not in extensions:
            continue
        lower_base = base.lower()
        if lower_base.startswith(prefix) or prefix.startswith(lower_base):
            candidates.append(os.path.join(directory, name))

    if not candidates:
        return ""

    return sorted(candidates, key=lambda p: (len(os.path.splitext(os.path.basename(p))[0]), p.lower()))[0]


def find_panel_image(rom):
    for directory in (PANEL_DIR, RESOURCE_CPANEL_DIR, MYRIENT_CABINETS_DIR):
        path = find_exact_image_in_dir(directory, rom, extensions=(".png",))
        if path:
            return path
    for directory in (PANEL_DIR, RESOURCE_CPANEL_DIR, MYRIENT_CABINETS_DIR):
        path = find_fuzzy_image_in_dir(directory, rom, extensions=(".png",))
        if path:
            return path
    return ""


def color_to_hex(name):
    raw = (name or "").strip()
    return COLOR_MAP.get(raw) or COLOR_MAP.get(COLOR_NAME_INDEX.get(raw.lower(), ""), "#d1d5db")


def canonical_color_name(name, default=""):
    raw = (name or "").strip()
    if not raw:
        return default
    return COLOR_NAME_INDEX.get(raw.lower(), raw)


def mame_xml_indent(elem, level=0):
    ET.indent(elem, space="    ", level=level)


def mame_port_identity(mame):
    if not isinstance(mame, dict):
        return None
    tag = canonical_mame_tag(mame.get("tag_raw") or mame.get("mame_tag") or mame.get("cfg_tag") or mame.get("tag"))
    mtype = (mame.get("input_id") or mame.get("type") or mame.get("input") or "").strip()
    mask_dec = mame.get("mask_dec")
    defvalue_dec = mame.get("defvalue_dec")
    if mask_dec is None:
        mask_dec = parse_int_value(mame.get("mask"))
    if defvalue_dec is None:
        defvalue_dec = parse_int_value(mame.get("defvalue"))
    if not tag or not mtype or mask_dec is None or defvalue_dec is None:
        return None
    return tag, mtype, int(mask_dec), int(defvalue_dec)


def mame_axis_sequence_type_for_direction(direction):
    if direction in {"left", "up", "up_left", "down_left"}:
        return "decrement"
    if direction in {"right", "down", "up_right", "down_right"}:
        return "increment"
    return "standard"


def mame_axis_sequence_type_for_item(item, direction):
    mame = item.get("mame", {}) if isinstance(item, dict) else {}
    axis_text = " ".join(
        str(mame.get(key, ""))
        for key in ("input", "input_id", "type", "normalized_type", "control_type", "function", "label")
    ).lower()
    if "pedal" in axis_text:
        def as_number(value):
            try:
                return float(value)
            except (TypeError, ValueError):
                return None

        min_value = as_number(mame.get("min"))
        max_value = as_number(mame.get("max"))
        def_value = as_number(mame.get("defvalue_dec") if mame.get("defvalue_dec") not in (None, "") else mame.get("defvalue"))
        if min_value is not None and def_value is not None and def_value <= min_value:
            return "increment"
        if max_value is not None and def_value is not None and def_value >= max_value:
            return "decrement"
        return "increment"
    return mame_axis_sequence_type_for_direction(direction)


def natural_sort_key(value):
    return [
        int(part) if part.isdigit() else part.lower()
        for part in re.split(r"(\d+)", str(value or ""))
    ]


def output_sort_key(output):
    if not isinstance(output, dict):
        return natural_sort_key(output)
    return natural_sort_key(output.get("name") or output.get("label") or output.get("function") or "")


def normalize_control_type(raw):
    t = (raw or "").lower()
    if "doublejoy2wayv" in t or "double joystick 2-way vertical" in t:
        return "double_joystick_2way_vertical"
    if "doublejoy4way" in t or "double joystick 4-way" in t:
        return "double_joystick_4way"
    if "doublejoy8way" in t or "double joystick 8-way" in t:
        return "double_joystick_8way"
    if "doublejoy" in t:
        return "double_joystick"
    if "mechanical rotary" in t:
        return "mechanical_rotary_joystick"
    if "optical rotary" in t:
        return "optical_rotary_joystick"
    if "rotary" in t:
        return "rotary_joystick"
    if "trigger" in t:
        return "triggerstick"
    if "top fire" in t or "top-fire" in t:
        return "top_fire_joystick"
    if "4-way joystick" in t or "joy4way" in t:
        return "joy4way"
    if "8-way joystick" in t or "joy8way" in t:
        return "joy8way"
    if "2-way joystick (horizontal)" in t or ("joy2way" in t and ("left/right" in t or "horizontal" in t)):
        return "joy2wayhorizontal"
    if "2-way joystick (vertical)" in t or ("joy2way" in t and ("up/down" in t or "vertical" in t)):
        return "joy2wayvertical"
    if "trackball" in t:
        return "trackball"
    if "spinner" in t:
        return "spinner"
    if "vertical dial" in t or "dialv" in t or "dial v" in t:
        return "vertical_dial"
    if "dial" in t:
        return "dial"
    if "vertical paddle" in t or "paddlev" in t or "paddle v" in t:
        return "vertical_paddle"
    if "paddle" in t:
        return "paddle"
    if "wheel" in t:
        return "wheel"
    if "pedal3" in t or "pedal 3" in t:
        return "pedal3"
    if "pedal2" in t or "pedal 2" in t:
        return "pedal2"
    if "pedal" in t:
        return "pedal"
    if "yoke" in t:
        return "yoke"
    if "throttle" in t:
        return "throttle"
    if "shifter" in t or "gear" in t:
        return "shifter"
    if "turntable" in t:
        return "turntable"
    if "roller" in t:
        return "roller"
    if "handlebar" in t:
        return "handlebar"
    if "lightgun" in t or "light gun" in t:
        return "lightgun"
    if "gun" in t:
        return "gun"
    if "mahjong" in t:
        return "mahjong_panel"
    if "hanafuda" in t:
        return "hanafuda_panel"
    if "keypad" in t:
        return "keypad"
    if "gambling" in t:
        return "gambling_panel"
    if "poker" in t:
        return "poker_panel"
    if "slot" in t:
        return "slot_panel"
    if "flightstick" in t or "stick" in t:
        return "analog_stick"
    if "just buttons" in t or "trivia buttons" in t:
        return "only_buttons"
    return "unknown"


def normalize_mk_role(function_name):
    f = (function_name or "").strip().lower()
    mapping = {
        "high punch": "HP",
        "low punch": "LP",
        "high kick": "HK",
        "low kick": "LK",
        "block": "BL",
        "run": "RN",
        "hp": "HP",
        "lp": "LP",
        "hk": "HK",
        "lk": "LK",
        "bl": "BL",
        "rn": "RN",
    }
    return mapping.get(f)


def dec_to_hex4(v):
    try:
        return f"0x{int(v):04X}"
    except Exception:
        return ""


def parse_int_value(value):
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    try:
        return int(text, 0)
    except Exception:
        return None


def hex_value(value, width=4):
    parsed = parse_int_value(value)
    if parsed is None:
        return ""
    return f"0x{parsed:0{width}X}"


def canonical_mame_tag(value):
    text = str(value or "").strip()
    if not text:
        return ""
    return text if text.startswith(":") else f":{text}"


def layout_panel_slot_value(payload):
    if not isinstance(payload, dict):
        return None
    if "panel_slots" in payload:
        slots = payload.get("panel_slots")
        if isinstance(slots, list):
            clean = [slot for slot in slots if slot is not None]
            if len(clean) > 1:
                return clean
            if len(clean) == 1:
                return clean[0]
        return None
    return payload.get("panel_slot")


def logical_name_for_button(function_name, index):
    fn = (function_name or "").strip().upper()
    if fn in {"A", "B", "C", "D"}:
        return fn
    return str(index)


def infer_button_function_from_mame(mame_data, player):
    label = (mame_data or {}).get("input_label", "") or ""
    if label and "PORT_BIT" not in label.upper():
        label = re.sub(rf"^P{player}\s+", "", label, flags=re.IGNORECASE).strip()
        if label:
            return label

    input_function = (mame_data or {}).get("input_function", "") or ""
    prefix = f"p{player}_"
    if input_function.lower().startswith(prefix):
        return input_function[len(prefix):].replace("_", " ").title()

    return ""


def canonical_direction_mame(player, direction):
    return f"P{player}_{direction.upper()}"


def direction_candidates(player, direction, function_name):
    direction = (direction or "").upper()
    candidates = [
        f"P{player}_{direction}",
        f"P{player}_JOYSTICK_{direction}",
        f"JOYSTICK_{direction}",
    ]
    fn = normalize_function_to_action_token(function_name)
    if fn:
        candidates.extend([fn, f"P{player}_{fn}", f"P{player}_BUTTON_{fn}"])
    return candidates


def is_specific_joystick_input(entry):
    entry = entry or {}
    mame = entry.get("mame", {})
    if entry.get("function"):
        return True
    if mame.get("type") or mame.get("tag") or mame.get("mask_dec") is not None:
        return True
    port_block = (mame.get("port_block") or "").strip().lower()
    if port_block and port_block not in GENERIC_JOYSTICK_PORT_BLOCKS:
        return True
    return False


def is_analog_input_entry(entry):
    input_name = normalize_mame_token(entry.get("input", ""))
    input_id = normalize_mame_token(entry.get("input_id", ""))
    if input_name in ANALOG_INPUT_TYPES:
        return True
    return any(input_id.endswith("_" + token) for token in ANALOG_INPUT_TYPES)


def analog_short_label(label, input_name):
    text = (label or input_name or "").strip().upper()
    mapping = {
        "PADDLE": "PDL",
        "PEDAL": "ACC",
        "PEDAL2": "BRK",
        "PEDAL3": "PD3",
        "DIAL": "DIAL",
        "TRACKBALL_X": "TBX",
        "TRACKBALL_Y": "TBY",
        "AD_STICK_X": "ASX",
        "AD_STICK_Y": "ASY",
        "LIGHTGUN_X": "LGX",
        "LIGHTGUN_Y": "LGY",
    }
    token = normalize_mame_token(text)
    return mapping.get(token, text[:3] or "AX")


def analog_mame_candidates(player, entry):
    input_id = entry.get("input_id", "")
    input_name = entry.get("input", "")
    candidates = [
        input_id,
        f"{input_id}_1" if input_id else "",
        input_name,
        f"P{player}_{input_name}_1" if input_name else "",
        entry.get("input_function", ""),
        entry.get("input_label", ""),
    ]

    match = re.match(r"^(.*?)(\d+)$", input_name or "")
    if match:
        base, suffix = match.groups()
        candidates.extend([
            f"P{player}_{base}_{suffix}",
            f"{base}_{suffix}",
        ])

    match = re.match(r"^(P\d+_.*?)(\d+)$", input_id or "")
    if match:
        base, suffix = match.groups()
        candidates.append(f"{base}_{suffix}")

    return candidates


def collect_analog_inputs(mame_output_data, mame_inputs, player):
    axes = []
    seen = set()
    for entry in mame_output_data.get("inputs", []):
        if entry.get("player") not in (None, player):
            continue
        if not is_analog_input_entry(entry):
            continue

        input_id = entry.get("input_id", "")
        input_name = entry.get("input", "")
        axis_id = input_id or f"P{player}_{input_name}"
        if axis_id in seen:
            continue
        seen.add(axis_id)

        mame = first_mame_match(
            mame_inputs,
            analog_mame_candidates(player, entry)
        )
        if not mame:
            mame = dict(entry)

        label = entry.get("input_label") or input_name or axis_id
        function_name = entry.get("input_function") or normalize_function_to_action_token(label).lower()
        axes.append({
            "id": axis_id,
            "input": input_name,
            "label": label,
            "function": function_name,
            "short": analog_short_label(label, input_name),
            "color": "Gray",
            "output": output_for_mame_input(mame, mame_output_data.get("mappings", [])),
            "mame": mame,
            "physical_joystick": "",
            "joystick": {"negative": "", "positive": ""},
            "physical_axis": "",
            "panel_joystick": None,
            "slots_by_layout": {layout: None for layout in LAYOUTS},
            "slots_by_polarity": {
                "negative": {layout: None for layout in LAYOUTS},
                "positive": {layout: None for layout in LAYOUTS},
            },
        })
    return axes


def analog_axis_device_color(axis, devices, fallback="Gray"):
    input_name = normalize_mame_token(axis.get("input", ""))
    function_name = normalize_function_to_action_token(axis.get("function", ""))

    for dev in devices or []:
        dev_type = normalize_mame_token(dev.get("type", ""))
        raw = normalize_mame_token(dev.get("raw", ""))
        codes = {normalize_mame_token(code) for code in dev.get("codes", [])}

        is_paddle_device = dev_type in {"PADDLE", "VERTICAL_PADDLE"} or "VPADDLE" in codes or "PADDLE" in raw
        is_pedal_device = dev_type.startswith("PEDAL") or "PEDAL" in raw
        is_dial_device = "DIAL" in dev_type or "DIAL" in raw

        if input_name in {"PEDAL", "PADDLE", "PADDLE_V"} and is_paddle_device:
            return dev.get("color", fallback)
        if input_name.startswith("PEDAL") and is_pedal_device:
            return dev.get("color", fallback)
        if input_name.startswith("DIAL") and is_dial_device:
            return dev.get("color", fallback)
        if function_name in {"THRUST", "INCREASE_THRUST", "DECREASE_THRUST"} and is_paddle_device:
            return dev.get("color", fallback)

    return fallback


def canonical_start_candidates(player):
    return [f"{player}_PLAYER_START", f"{player}_PLAYERS_START"]


def canonical_coin_candidates(player):
    return [f"COIN_{player}"]



SPECIAL_FUNCTION_ALIASES = {
    "JAB PUNCH": "JAB_PUNCH",
    "PUNCH - JAB": "JAB_PUNCH",
    "LIGHT PUNCH": "JAB_PUNCH",
    "PUNCH - LIGHT": "JAB_PUNCH",
    "PUNCH": "PUNCH",
    "STRONG PUNCH": "STRONG_PUNCH",
    "MIDDLE PUNCH": "STRONG_PUNCH",
    "MEDIUM PUNCH": "STRONG_PUNCH",
    "PUNCH - STRONG": "STRONG_PUNCH",
    "PUNCH - MEDIUM": "STRONG_PUNCH",
    "FIERCE PUNCH": "FIERCE_PUNCH",
    "HEAVY PUNCH": "FIERCE_PUNCH",
    "POWER PUNCH": "FIERCE_PUNCH",
    "PUNCH - FIERCE": "FIERCE_PUNCH",
    "PUNCH - HEAVY": "FIERCE_PUNCH",
    "SHORT KICK": "SHORT_KICK",
    "KICK - SHORT": "SHORT_KICK",
    "LIGHT KICK": "SHORT_KICK",
    "KICK - LIGHT": "SHORT_KICK",
    "KICK": "KICK",
    "FORWARD KICK": "FORWARD_KICK",
    "STRONG KICK": "FORWARD_KICK",
    "MIDDLE KICK": "FORWARD_KICK",
    "MEDIUM KICK": "FORWARD_KICK",
    "KICK - FORWARD": "FORWARD_KICK",
    "KICK - STRONG": "FORWARD_KICK",
    "KICK - MEDIUM": "FORWARD_KICK",
    "ROUNDHOUSE KICK": "ROUNDHOUSE_KICK",
    "FIERCE KICK": "ROUNDHOUSE_KICK",
    "HEAVY KICK": "ROUNDHOUSE_KICK",
    "POWER KICK": "ROUNDHOUSE_KICK",
    "KICK - ROUNDHOUSE": "ROUNDHOUSE_KICK",
    "KICK - HEAVY": "ROUNDHOUSE_KICK",
    "HIGH PUNCH": "HIGH_PUNCH",
    "LOW PUNCH": "LOW_PUNCH",
    "HIGH KICK": "HIGH_KICK",
    "LOW KICK": "LOW_KICK",
    "BLOCK": "BLOCK",
    "DEFENSE": "BLOCK",
    "RUN": "RUN",
    "A": "A",
    "B": "B",
    "C": "C",
    "D": "D",
}

SPECIAL_BUTTON_ROOTS = {
    "ACTION",
    "AIM",
    "ATTACK",
    "BET",
    "BLOCK",
    "BOMB",
    "BRAKE",
    "COLLECT",
    "DEFENSE",
    "FIRE",
    "FLAP",
    "HORN",
    "HYPERSPACE",
    "INVISO",
    "JUMP",
    "KICK",
    "MAGIC",
    "MISSILE",
    "MOVE",
    "POWER",
    "PUNCH",
    "REVERSE",
    "RUN",
    "SELECT",
    "SHOT",
    "SMART_BOMB",
    "SPELL",
    "SWORD",
    "THRUST",
    "TRIGGER",
    "VIEW",
    "WEAPON",
    "WHIP",
}

DIRECTION_SUFFIXES = {"UP", "DOWN", "LEFT", "RIGHT"}

SPECIAL_PREFIXES = {
    "DEFENDER",
    "STARGATE",
}

CONFIG_PREFIX_BLACKLIST = (
    "ATTRACT",
    "BONUS",
    "BOOKKEEPING",
    "CABINET",
    "CALIBRATE",
    "CALIBRATION",
    "COINAGE",
    "CREDITS_",
    "CREDIT_",
    "DEBUG",
    "DEMO",
    "DIAG",
    "DIAGNOSTIC",
    "DIFFICULT",
    "DIFFICULTY",
    "DIP",
    "DIPSW",
    "DSW",
    "EXTEND",
    "EXTRA_",
    "FREE_PLAY",
    "GAME_",
    "INITIAL_",
    "INPUT_TEST",
    "LANGUAGE",
    "LEVEL_",
    "LIVES",
    "MAX_",
    "MINIMUM_",
    "MODE",
    "MONITOR",
    "NUMBER_OF_",
    "OPTION",
    "OUTPUT_TEST",
    "PLAYER_",
    "PLAY_TIME",
    "PRICE_",
    "SERVICE_MODE",
    "SOUND_",
    "SPECIAL_",
    "SPRITE_",
    "STAGE_",
    "STARTING_",
    "TEST",
)

CONFIG_EXACT_BLACKLIST = {
    "BUTTONS",
    "BUTTONS_LAYOUT",
    "CONTROL",
    "CONTROLS",
    "CONTROLLER",
    "CONTROLLER_TYPE",
    "CONTROL_PANEL",
    "CONTROL_TYPE",
    "DISPLAY",
    "GAME",
    "GAMEPLAY",
    "JOYSTICK",
    "JOYSTICKS",
    "LANGUAGE",
    "MONITOR",
    "NETWORK",
    "OPTION",
}

MAIN_CONTROL_EXACT = {
    "DIAL", "DIAL_V",
    "POSITIONAL", "POSITIONAL_V",
    "ACCELERATOR", "BRAKE",
    "GAS_PEDAL", "STEERING_WHEEL",
    "LIGHTGUN_X", "LIGHTGUN_Y",
    "AD_STICK_X", "AD_STICK_Y", "AD_STICK_Z",
    "TRACKBALL_X", "TRACKBALL_Y",
    "MOUSE_X", "MOUSE_Y",
    "PADDLE", "PADDLE_V",
    "WHEEL",
    "PEDAL", "PEDAL2",
}

SYSTEM_EXACT = {
    "START", "START_1P", "START_2P", "START_ALL",
    "COIN_1", "COIN_2", "COIN_3", "COIN_4",
    "SERVICE", "TEST", "TILT", "CREDIT_SERVICE",
}

SYSTEM_PATTERNS = [
    re.compile(r"^\d+_PLAYER_START$"),
    re.compile(r"^\d+_PLAYERS_START$"),
    re.compile(r"^(LEFT|RIGHT|TOP|BOTTOM)_\d+_PLAYER_START$"),
    re.compile(r"^(LEFT|RIGHT|TOP|BOTTOM)_\d+_PLAYERS_START$"),
    re.compile(r"^COIN_\d+$"),
]

LETTER_BUTTONS = ("A", "B", "C", "D")
NEOGEO_BY_INDEX = {1: "A", 2: "B", 3: "C", 4: "D"}

def inherited_button_function(controls_sec, player, index, default_value):
    value = safe_get(controls_sec, f"P{player}_BUTTON{index}", "")
    if value.strip():
        return value

    p1_value = safe_get(controls_sec, f"P1_BUTTON{index}", "")
    if p1_value.strip():
        return p1_value

    return default_value


def inherited_joystick_function(controls_sec, player, direction):
    value = safe_get(controls_sec, f"P{player}_JOYSTICK_{direction}", "")
    if value.strip():
        return value

    p1_value = safe_get(controls_sec, f"P1_JOYSTICK_{direction}", "")
    if p1_value.strip():
        return p1_value

    return ""


def inherited_controls_string(controls_sec, colors_sec, player):
    value = safe_get(controls_sec, f"P{player}Controls", "")
    if value.strip():
        return value

    p1_value = safe_get(controls_sec, "P1Controls", "")
    if p1_value.strip():
        return p1_value

    return safe_get(colors_sec, "controls", "")


def logical_name_for_button(function_name, index):
    fn = (function_name or "").strip().upper()
    canon = SPECIAL_FUNCTION_ALIASES.get(fn, fn)
    if canon in {"A", "B", "C", "D"}:
        return canon
    return str(index)


def canonical_direction_mame(player, direction):
    return f"P{player}_{direction.upper()}"


def canonical_start_candidates(player):
    candidates = [
        f"{player}_PLAYER_START",
        f"{player}_PLAYERS_START",
        f"START_{player}P",
        f"START{player}",
        f"LEFT_{player}_PLAYER_START",
        f"RIGHT_{player}_PLAYER_START",
        f"TOP_{player}_PLAYER_START",
        f"BOTTOM_{player}_PLAYER_START",
        f"LEFT_{player}_PLAYERS_START",
        f"RIGHT_{player}_PLAYERS_START",
        f"TOP_{player}_PLAYERS_START",
        f"BOTTOM_{player}_PLAYERS_START",
    ]
    if player == 1:
        candidates.append("START")
    return candidates


def canonical_select_candidates(player):
    return [
        f"SELECT{player}",
        f"SELECT_{player}",
        f"START{player + 1}",
        f"{player}_PLAYER_SELECT",
        "SELECT",
        "SELECT_GAME",
    ]


def canonical_coin_candidates(player):
    return [f"COIN_{player}", f"COIN{player}"]


def normalize_mame_token(token):
    token = (token or "").strip().upper()
    token = token.replace("&AMP;", "&")
    token = token.replace("-", "_")
    token = token.replace(" ", "_")
    token = token.replace("__", "_")
    token = re.sub(r"_*\(.*?\)$", "", token)
    token = token.strip("_/")

    if "/" in token:
        parts = [p.strip("_") for p in token.split("/") if p.strip("_")]
        token = "/".join(parts)

    return token


def split_root_and_direction(token):
    token = normalize_mame_token(token)
    for direction in DIRECTION_SUFFIXES:
        suffix = "_" + direction
        if token.endswith(suffix):
            base = token[:-len(suffix)]
            if base:
                return base, direction
    return token, None


def is_config_input(token):
    t = normalize_mame_token(token)
    if t in CONFIG_EXACT_BLACKLIST:
        return True
    return any(t.startswith(prefix) for prefix in CONFIG_PREFIX_BLACKLIST)


def is_system_input(token):
    t = normalize_mame_token(token)
    if t in SYSTEM_EXACT:
        return True
    return any(rx.match(t) for rx in SYSTEM_PATTERNS)


def is_main_control(token):
    t = normalize_mame_token(token)
    if t in MAIN_CONTROL_EXACT:
        return True
    if re.fullmatch(r"P\d+_(UP|DOWN|LEFT|RIGHT)", t):
        return True
    return False


def normalize_function_to_action_token(function_name):
    raw = (function_name or "").strip().upper()
    canon = SPECIAL_FUNCTION_ALIASES.get(raw, raw)
    canon = canon.replace(" / ", "_").replace("/", "_")
    canon = canon.replace(" - ", "_").replace("-", "_")
    canon = canon.replace("&", "_AND_")
    canon = canon.replace("'", "")
    canon = canon.replace(".", "")
    canon = re.sub(r"\s+", "_", canon)
    canon = re.sub(r"_+", "_", canon).strip("_")
    return canon


def function_candidate_tokens(function_name, logical_name, index):
    tokens = []
    fn = normalize_function_to_action_token(function_name)
    ln = normalize_function_to_action_token(logical_name)

    def add(value):
        value = normalize_mame_token(value)
        if value and value not in tokens:
            tokens.append(value)

    for value in (fn, ln):
        if value:
            add(value)

    if fn in {"JAB_PUNCH", "STRONG_PUNCH", "FIERCE_PUNCH", "SHORT_KICK", "FORWARD_KICK", "ROUNDHOUSE_KICK",
              "HIGH_PUNCH", "LOW_PUNCH", "HIGH_KICK", "LOW_KICK", "BLOCK", "RUN",
              "PUNCH", "KICK", "FIRE", "JUMP", "BOMB", "THRUST", "SMART_BOMB",
              "HYPERSPACE", "INVISO", "REVERSE", "MAGIC", "SPELL", "SWORD", "ATTACK",
              "ACTION", "WEAPON", "SHOT", "TRIGGER", "MOVE", "AIM", "FLAP", "MISSILE"}:
        add(fn)

    if index in NEOGEO_BY_INDEX:
        add(NEOGEO_BY_INDEX[index])

    if fn in {"A", "B", "C", "D"}:
        add(fn)

    return tokens


def canonical_button_candidates(player, index, logical_name, function_name):
    out = []

    def add(candidate):
        if candidate:
            out.append(candidate)

    ln = normalize_function_to_action_token(logical_name)
    fn = normalize_function_to_action_token(function_name)

    prefer_function_match = bool(fn) and fn != f"BUTTON_{index}"

    if not prefer_function_match:
        add(f"P{player}_BUTTON_{index}")

        if index <= 9:
            add(f"P{player}_BUTTON{index}")

    for token in function_candidate_tokens(function_name, logical_name, index):
        add(f"P{player}_{token}")
        add(f"P{player}_BUTTON_{token}")
        add(f"P{player}_{token}_BUTTON")
        add(token)

        root, direction = split_root_and_direction(token)
        if root in SPECIAL_BUTTON_ROOTS:
            add(root)
            if direction:
                add(f"{root}_{direction}")
            for prefix in SPECIAL_PREFIXES:
                add(f"{prefix}/{token}")
                add(f"{prefix}/{root}")
            add(f"JOUST/P{player}/{token}")
            add(f"JOUST/P{player}/{root}")
            for side in ("LEFT", "RIGHT"):
                add(f"SPLAT/P{player}/{side}/{token}")
                add(f"SPLAT/P{player}/{side}/{root}")
                if direction:
                    add(f"SPLAT/P{player}/{side}/{root}_{direction}")

    if prefer_function_match:
        add(f"P{player}_BUTTON_{index}")

        if index <= 9:
            add(f"P{player}_BUTTON{index}")

    if ln in LETTER_BUTTONS:
        add(f"P{player}_{ln}")
        add(f"P{player}_{ln}_BUTTON")
        add(f"P{player}_BUTTON_{ln}")
    if fn in LETTER_BUTTONS:
        add(f"P{player}_{fn}")
        add(f"P{player}_{fn}_BUTTON")
        add(f"P{player}_BUTTON_{fn}")

    if index in NEOGEO_BY_INDEX:
        token = NEOGEO_BY_INDEX[index]
        add(f"P{player}_{token}")
        add(f"P{player}_{token}_BUTTON")
        add(f"P{player}_BUTTON_{token}")

    uniq = []
    seen = set()
    for x in out:
        norm = normalize_mame_token(x)
        if norm and norm not in seen:
            uniq.append(x)
            seen.add(norm)
    return uniq


def first_mame_match(mame_inputs, candidates):
    fallback = None
    def merged_with_fallback(match):
        if fallback and match is not fallback:
            return {**fallback, **match}
        return match

    for c in candidates:
        if c in mame_inputs:
            match = mame_inputs[c]
            if match.get("type") or match.get("mask_dec") is not None:
                return merged_with_fallback(match)
            if fallback is None:
                fallback = match

    normalized_index = {}
    for raw_key, value in mame_inputs.items():
        normalized_index.setdefault(normalize_mame_token(raw_key), value)

    for candidate in candidates:
        match = normalized_index.get(normalize_mame_token(candidate))
        if match:
            if match.get("type") or match.get("mask_dec") is not None:
                return merged_with_fallback(match)
            if fallback is None:
                fallback = match
    return fallback or {}


def parse_controls_segments(raw_controls):
    segments = []
    raw_controls = raw_controls or ""
    if not raw_controls.strip():
        return segments

    parts = [p.strip() for p in raw_controls.split("|") if p.strip()]
    for idx, part in enumerate(parts, start=1):
        tokens = [t.strip() for t in part.split("+") if t.strip()]
        label = tokens[0] if tokens else f"Device {idx}"
        codes = tokens[1:] if len(tokens) > 1 else []
        segments.append({
            "id": f"D{idx}",
            "label": label,
            "type": normalize_control_type(part),
            "raw": part,
            "codes": codes,
        })
    return segments



def parse_mame_inputs(rom):
    result = {}
    extend_path = os.path.join(MAME_CFG_DIR, f"{rom}_inputs_extend.cfg")
    path = extend_path if os.path.exists(extend_path) else os.path.join(MAME_CFG_DIR, f"{rom}_inputs.cfg")
    if not os.path.exists(path):
        return result

    try:
        tree = ET.parse(path)
        root = tree.getroot()
    except Exception:
        return result

    for port in root.findall(".//port"):
        mtype = (port.attrib.get("type", "") or "").strip()
        if not mtype:
            continue

        tag_raw = canonical_mame_tag(port.attrib.get("tag", ""))
        mask = port.attrib.get("mask", "")
        defvalue = port.attrib.get("defvalue", "")
        mask_dec = parse_int_value(mask)
        defvalue_dec = parse_int_value(defvalue)

        extend = {}
        for key, value in port.attrib.items():
            if not key.startswith("ext_"):
                continue
            clean_key = key[4:]
            if clean_key in {"mask_int", "defvalue_int", "min", "max", "sensitivity", "keydelta", "runtime_type"}:
                parsed = parse_int_value(value)
                extend[clean_key] = parsed if parsed is not None else value
            else:
                extend[clean_key] = value

        item = {
            "type": mtype,
            "normalized_type": normalize_mame_token(mtype),
            "tag_raw": tag_raw,
            "tag": tag_raw.lstrip(":"),
            "mask_dec": mask_dec,
            "mask_hex": hex_value(mask),
            "defvalue_dec": defvalue_dec,
            "defvalue_hex": hex_value(defvalue),
        }
        if extend:
            item["extend"] = extend
            item["input_id"] = extend.get("input_id", item.get("input_id", ""))
            item["input_label"] = extend.get("label", item.get("input_label", ""))
            item["input_function"] = extend.get("function", item.get("input_function", ""))
            item["input"] = extend.get("input_id", item.get("input", mtype))
            item["port_block"] = extend.get("port_block", item.get("port_block", ""))
            if "ipt" in extend:
                item["ipt"] = extend["ipt"]
            if "runtime_tag" in extend:
                item["runtime_tag"] = extend["runtime_tag"]
            if "control_type" in extend:
                item["control_type"] = extend["control_type"]
            if "source" in extend:
                item["source"] = extend["source"]
        result[mtype] = item
        for alias in (
            item.get("input_id", ""),
            item.get("input", ""),
            item.get("ipt", ""),
            item.get("input_label", ""),
            item.get("input_function", ""),
        ):
            alias_norm = normalize_mame_token(alias)
            if alias_norm and alias_norm not in result:
                result[alias] = item

    return result


def normalize_output_input(entry):
    out = dict(entry)
    out["input_label"] = entry.get("input_label", entry.get("label", ""))
    out["input_function"] = entry.get("input_function", entry.get("function", ""))
    input_name = out.get("input") or out.get("input_id") or ""
    tag_raw = canonical_mame_tag(out.get("cfg_tag") or out.get("mame_tag") or out.get("tag") or "")
    out.setdefault("type", input_name)
    out.setdefault("normalized_type", normalize_mame_token(input_name))
    out["tag_raw"] = tag_raw
    out["tag"] = tag_raw.lstrip(":")
    out["mask_dec"] = out.get("mask_int", parse_int_value(out.get("mask")))
    out["mask_hex"] = hex_value(out.get("mask_dec"))
    out["defvalue_dec"] = out.get("defvalue_int", parse_int_value(out.get("defvalue")))
    out["defvalue_hex"] = hex_value(out.get("defvalue_dec"))
    out.setdefault("runtime_tag", canonical_mame_tag(out.get("mame_tag") or out.get("cfg_tag") or tag_raw))
    out.setdefault("source", "outputs_mame")
    return out


def normalize_output_mapping(entry):
    out = dict(entry)
    out["input_label"] = entry.get("input_label", entry.get("label", ""))
    out["input_function"] = entry.get("input_function", entry.get("function", ""))
    out["output_label"] = entry.get("output_label", "")
    out["output_function"] = entry.get("output_function", "")
    return out


def load_mame_output_data(rom):
    path = os.path.join(MAME_OUTPUTS_DIR, f"{rom}.json")
    if not os.path.exists(path):
        return {
            "source_file": "",
            "inputs": [],
            "outputs": [],
            "mappings": [],
            "stats": {},
            "game_context": [],
        }

    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            raw = json.load(f)
    except Exception:
        return {
            "source_file": "",
            "inputs": [],
            "outputs": [],
            "mappings": [],
            "stats": {},
            "game_context": [],
        }

    outputs = []
    for entry in raw.get("outputs", []):
        if not isinstance(entry, dict):
            continue
        item = dict(entry)
        item["value_type"] = item.get("value_type") or "unknown"
        outputs.append(item)

    return {
        "source_file": raw.get("source_file", ""),
        "inputs": [normalize_output_input(x) for x in raw.get("inputs", []) if isinstance(x, dict)],
        "outputs": outputs,
        "mappings": [normalize_output_mapping(x) for x in raw.get("mappings", []) if isinstance(x, dict)],
        "stats": raw.get("stats", {}),
        "game_context": raw.get("game_context", []),
    }


def merge_mame_input_indexes(port_inputs, output_inputs):
    merged = {}
    normalized_index = {}

    def alias_keys(entry):
        input_id = entry.get("input_id", "")
        player_specific = bool(re.match(r"^P\d+_", normalize_mame_token(input_id)))
        keys = [input_id]
        if not player_specific:
            keys.extend([
                entry.get("input", ""),
                entry.get("type", ""),
                entry.get("ipt", ""),
                entry.get("input_label", ""),
                entry.get("input_function", ""),
            ])
        else:
            player = entry.get("player")
            try:
                player = int(player) if player not in (None, "") else None
            except Exception:
                player = None
            for source_key in ("input_label", "input_function", "label", "function"):
                value = entry.get(source_key, "")
                token = normalize_mame_token(value)
                if not token:
                    continue
                keys.append(value)
                if player is not None:
                    keys.append(f"P{player}_{token}")
        if input_id:
            keys.append(input_id.replace("BUTTON", "BUTTON_"))
            keys.append(input_id.replace("JOYSTICK_", ""))
        return [key for key in keys if key]

    def add_entry(key, entry, overwrite=False):
        if not key:
            return
        norm = normalize_mame_token(key)
        if not norm:
            return
        if norm in normalized_index and not overwrite:
            return
        merged[key] = entry
        normalized_index[norm] = key

    def merge_missing(target, fallback):
        out = dict(target)
        for key, value in fallback.items():
            if key == "extend":
                out.setdefault("cfg_extend", value)
                continue
            if key not in out or out.get(key) in (None, ""):
                out[key] = value
        return out

    for entry in output_inputs or []:
        if not isinstance(entry, dict):
            continue
        primary = entry.get("input_id") or entry.get("input") or entry.get("type")
        if not primary:
            continue
        if primary in merged:
            continue
        merged[primary] = entry
        normalized_index[normalize_mame_token(primary)] = primary
        for key in alias_keys(entry):
            add_entry(key, entry)

    for key, entry in (port_inputs or {}).items():
        if not isinstance(entry, dict):
            continue
        target_key = None
        for candidate in alias_keys(entry) + [key]:
            norm = normalize_mame_token(candidate)
            if norm in normalized_index:
                target_key = normalized_index[norm]
                break
        if target_key:
            combined = merge_missing(merged[target_key], entry)
            merged[target_key] = combined
            for alias in [target_key] + alias_keys(combined):
                add_entry(alias, combined, overwrite=True)
        else:
            primary = entry.get("input_id") or key
            merged[primary] = entry
            normalized_index[normalize_mame_token(primary)] = primary
            for alias in alias_keys(entry):
                add_entry(alias, entry)

    return merged


def output_for_mame_input(mame_data, mappings):
    if not mame_data:
        return ""
    input_id = normalize_mame_token(mame_data.get("input_id", ""))
    input_name = normalize_mame_token(mame_data.get("input", ""))
    type_name = normalize_mame_token(mame_data.get("type", ""))
    normalized_type = normalize_mame_token(mame_data.get("normalized_type", ""))
    player = mame_data.get("player")
    try:
        player = int(player) if player not in (None, "") else None
    except Exception:
        player = None

    def mapping_player(mapping):
        try:
            value = mapping.get("player")
            return int(value) if value not in (None, "") else None
        except Exception:
            return None

    exact_ids = {input_id}
    exact_ids.discard("")
    fallback_ids = {input_name, type_name, normalized_type}
    fallback_ids.discard("")

    for mapping in mappings or []:
        mapping_ids = {
            normalize_mame_token(mapping.get("input_id", "")),
        }
        mapping_ids.discard("")
        if exact_ids.intersection(mapping_ids) and (mapping_player(mapping) in (None, player) or player is None):
            return mapping.get("output", "")

    for mapping in mappings or []:
        mp = mapping_player(mapping)
        if player is not None and mp not in (None, player):
            continue
        mapping_ids = {
            normalize_mame_token(mapping.get("input_id", "")),
            normalize_mame_token(mapping.get("input", "")),
        }
        mapping_ids.discard("")
        if fallback_ids.intersection(mapping_ids):
            return mapping.get("output", "")
    return ""


def output_for_system_input(system_key, mame_data, mame_output_data):
    outputs = mame_output_data.get("outputs", [])
    if system_key == "select":
        for out in outputs:
            haystack = " ".join([
                str(out.get("name", "")),
                str(out.get("function", "")),
                str(out.get("comment", "")),
            ]).lower()
            if "start" in haystack and "select" in haystack:
                return out.get("name", "")
    return output_for_mame_input(mame_data, mame_output_data.get("mappings", []))


def is_select_game_mame_input(mame_data):
    if not mame_data:
        return False
    haystack = " ".join([
        str(mame_data.get("input_label", "")),
        str(mame_data.get("input_function", "")),
        str(mame_data.get("comment", "")),
        str(mame_data.get("extend", {}).get("label", "")),
        str(mame_data.get("extend", {}).get("function", "")),
        str(mame_data.get("extend", {}).get("runtime_label", "")),
    ]).lower()
    return "select" in haystack


def empty_events():
    return {
        "lamps": [],
        "groups": [],
        "events": [],
        "lifecycle": [],
        "sequences": [],
    }


def parse_lip_file(path):
    events = empty_events()
    if not os.path.exists(path):
        return events
    try:
        root = ET.parse(path).getroot()
    except Exception:
        return events

    for lamp in root.findall(".//lamp"):
        item = dict(lamp.attrib)
        if "pressAction" in item:
            item["press_action"] = item.pop("pressAction")
        events["lamps"].append(item)

    for group in root.findall(".//group"):
        item = dict(group.attrib)
        members = []
        for child in list(group):
            if child.tag.lower() in {"member", "button"}:
                members.append(child.attrib.get("button") or child.attrib.get("id") or (child.text or "").strip())
        if members:
            item["members"] = [m for m in members if m]
        events["groups"].append(item)

    for event in root.findall(".//event"):
        item = dict(event.attrib)
        macros = []
        for child in list(event):
            macros.append({"type": child.tag, **dict(child.attrib)})
        if macros:
            item["macros"] = macros
        events["events"].append(item)

    for node in root.findall(".//lifecycle/*"):
        item = {"event": node.tag}
        if node.attrib:
            item.update(node.attrib)
        macros = [{"type": child.tag, **dict(child.attrib)} for child in list(node)]
        if macros:
            item["macros"] = macros
        events["lifecycle"].append(item)

    for seq in root.findall(".//sequence"):
        item = dict(seq.attrib)
        steps = []
        macros = []
        for child in list(seq):
            if child.tag.lower() == "step":
                steps.append(dict(child.attrib))
            else:
                macros.append({"type": child.tag, **dict(child.attrib)})
        if steps:
            item["steps"] = steps
        if macros:
            item["macros"] = macros
        events["sequences"].append(item)

    return events


def load_arcade_events(rom):
    return parse_lip_file(os.path.join(ARCADE_LIP_DIR, f"{rom}.lip"))


def parse_optional_int(value):
    try:
        text = str(value).strip()
        return int(text) if text else None
    except Exception:
        return None


def normalize_profile_lookup_key(value):
    return re.sub(r"[^A-Z0-9]+", "", str(value or "").upper())


PROFILE_KEY_INDEX = {
    normalize_profile_lookup_key(key): key
    for key in PROFILES_LIBRARY.keys()
}

SYSTEM_PROFILE_DEFAULTS = {
    "AMIGACD32": "AMIGACD32",
    "AMSTRADCPC": "AMSTRAD",
    "APPLE2": "APPLE_2",
    "ATARI2600": "ATARI2600",
    "ATARI5200": "ATARI5200",
    "ATARI7800": "ATARI7800",
    "BANDAIWONDERSWAN": "BANDAIWONDERSWAN",
    "BBCMICRO": "BBCMICRO",
    "C64": "COMMODORE",
    "GAMECUBE": "GAMECUBE_DEFAULT",
    "MASTERSYSTEM": "MASTER_SYSTEM",
    "MEGADRIVE": "MEGADRIVE_6B",
    "N64": "N64_DEFAULT",
    "PCENGINE": "PC_ENGINE",
    "PCENGINECD": "PC_ENGINE",
    "PSX": "PSX_DEFAULT",
    "SATURN": "SATURN_DEFAULT",
    "SNES": "SNES_DEFAULT",
    "WSWAN": "WSWAN",
    "WSWANC": "WSWANC",
    "XBOX": "XBOX_DEFAULT",
    "ZXSPECTRUM": "SINCLAIR",
}

SYSTEM_LAYOUT_PROFILE_ALIASES = {
    ("GAMECUBE", "FIGHTINGSTICKCUBE"): "GAMECUBE_FIGHTING_STICK",
    ("N64", "ARCADESHARK6B"): "N64_ARCADE_SHARK",
    ("N64", "ARCADESHARK8B"): "N64_ARCADE_SHARK",
    ("N64", "ARCADESHARKYELLOWMODE"): "N64_ARCADE_SHARK",
    ("PSX", "HORI8B"): "PSX_HORI_8B",
    ("SATURN", "DEFAULT"): "SATURN_DEFAULT",
    ("SNES", "SCOREMASTER"): "SNES_SCORE_MASTER",
    ("SNES", "SUPERADVANTAGE"): "SNES_SUPER_ADVANTAGE",
    ("XBOX", "FIGHTINGSTICKEX"): "XBOX_FIGHTING_STICK_EX",
}

RETROPAD_ID_BY_CONTROLLER = {}
for _slot_key, _slot_entry in SLOT_MAP.items():
    rb_name = str(_slot_entry.get("retrobat_button", "")).strip().upper()
    retropad_id = _slot_entry.get("retropad_id")
    if rb_name and retropad_id is not None and rb_name not in RETROPAD_ID_BY_CONTROLLER:
        RETROPAD_ID_BY_CONTROLLER[rb_name] = retropad_id
for _sys_key, _sys_entry in SYSTEM_SLOT_MAP.items():
    rb_name = str(_sys_entry.get("retrobat_button", "")).strip().upper()
    retropad_id = _sys_entry.get("retropad_id")
    if rb_name and retropad_id is not None and rb_name not in RETROPAD_ID_BY_CONTROLLER:
        RETROPAD_ID_BY_CONTROLLER[rb_name] = retropad_id


def resolve_system_profile_key(system_name, layout_type="", layout_name=""):
    system_norm = normalize_profile_lookup_key(system_name)
    layout_norm = normalize_profile_lookup_key(layout_name)
    direct_layout_key = SYSTEM_LAYOUT_PROFILE_ALIASES.get((system_norm, layout_norm))
    if direct_layout_key:
        return direct_layout_key

    candidate = PROFILE_KEY_INDEX.get(system_norm)
    if candidate:
        return candidate

    default_candidate = SYSTEM_PROFILE_DEFAULTS.get(system_norm)
    if default_candidate:
        return default_candidate

    if layout_norm:
        profile_candidates = [
            key for key in PROFILES_LIBRARY.keys()
            if normalize_profile_lookup_key(key).startswith(system_norm)
        ]
        for profile_key in profile_candidates:
            profile_norm = normalize_profile_lookup_key(profile_key)
            if layout_norm in profile_norm or profile_norm.endswith(layout_norm):
                return profile_key
    return None


def build_profile_lookup_maps(profile_key):
    profile = PROFILES_LIBRARY.get(profile_key) or {}
    slots = profile.get("slots", {})
    by_slot = {}
    by_label = {}
    for slot_key, slot_data in slots.items():
        if not isinstance(slot_data, dict):
            continue
        by_slot[str(slot_key).upper()] = slot_data
        label = str(slot_data.get("label", "")).strip()
        retropad_id = slot_data.get("retropad")
        if label and retropad_id is not None:
            by_label[normalize_profile_lookup_key(label)] = retropad_id
    return by_slot, by_label


def clean_export_dict(data):
    if isinstance(data, dict):
        out = {}
        for key, value in data.items():
            cleaned = clean_export_dict(value)
            if cleaned is None:
                continue
            if cleaned == "" and key not in {"function", "game_button", "controller", "color"}:
                continue
            out[key] = cleaned
        return out
    if isinstance(data, list):
        return [clean_export_dict(item) for item in data]
    return data


def infer_button_retropad_id(item, profile_by_slot, profile_by_label):
    retropad_id = item.get("retropad_id")
    if retropad_id is not None:
        return retropad_id

    button_id = str(item.get("button_id") or "").upper()
    if button_id in profile_by_slot:
        profile_retropad = profile_by_slot[button_id].get("retropad")
        if profile_retropad is not None:
            return profile_retropad

    for candidate in (
        item.get("function"),
        item.get("game_button"),
        item.get("controller"),
    ):
        key = normalize_profile_lookup_key(candidate)
        if key in profile_by_label:
            return profile_by_label[key]

    controller_key = str(item.get("controller", "")).strip().upper()
    return RETROPAD_ID_BY_CONTROLLER.get(controller_key)


def infer_button_function(item, profile_by_slot):
    current = str(item.get("function", "")).strip()
    if current:
        return current

    button_id = str(item.get("button_id") or "").upper()
    slot_profile = profile_by_slot.get(button_id, {})
    profile_label = str(slot_profile.get("label", "")).strip()
    if profile_label:
        return profile_label

    game_button = str(item.get("game_button", "")).strip()
    if game_button and game_button.upper() not in {"NONE", "START", "COIN", "SELECT"}:
        return game_button

    controller = str(item.get("controller", "")).strip()
    if controller and controller.upper() not in {"START", "SELECT"} and str(item.get("color", "")).strip().lower() != "black":
        return controller

    return "None"


def enrich_system_layout(system_name, layout_type, layout_name, layout_data):
    profile_key = resolve_system_profile_key(system_name, layout_type, layout_name)
    profile_by_slot, profile_by_label = build_profile_lookup_maps(profile_key)

    layout_data["rmp_slots"] = dict(RMP_SLOT_BUTTONS_BY_LAYOUT.get(layout_type) or {})

    for button_id, item in layout_data.get("buttons", {}).items():
        item["button_id"] = button_id
        item["retropad_id"] = infer_button_retropad_id(item, profile_by_slot, profile_by_label)
        item["function"] = infer_button_function(item, profile_by_slot)
        if str(button_id).isdigit():
            item["rmp_button"] = (RMP_SLOT_BUTTONS_BY_LAYOUT.get(layout_type) or {}).get(str(button_id))
        elif str(button_id).upper() == "START":
            item["rmp_button"] = RMP_SYSTEM_BUTTON_MAP.get("start")
        elif str(button_id).upper() in {"COIN", "SELECT"}:
            item["rmp_button"] = RMP_SYSTEM_BUTTON_MAP.get("coin")
        item.pop("button_id", None)

    return clean_export_dict(layout_data)


def build_system_panel_payload():
    slots = {}
    for slot_key, entry in SLOT_MAP.items():
        payload = dict(entry)
        payload["rmp_button_by_layout"] = {
            layout_name: mapping.get(str(slot_key))
            for layout_name, mapping in RMP_SLOT_BUTTONS_BY_LAYOUT.items()
            if mapping.get(str(slot_key))
        }
        slots[slot_key] = clean_export_dict(payload)

    system_slots = {
        key: clean_export_dict(dict(value))
        for key, value in SYSTEM_SLOT_MAP.items()
    }

    return {
        "convention": PANEL_CONVENTION,
        "slots": slots,
        "system_slots": system_slots,
        "rmp_slots_by_layout": RMP_SLOT_BUTTONS_BY_LAYOUT,
        "rmp_system_buttons": RMP_SYSTEM_BUTTON_MAP,
    }


def slugify_text(value):
    text = str(value or "").strip().lower()
    text = re.sub(r"[^a-z0-9]+", "-", text)
    return text.strip("-")


def markdown_clean_text(raw):
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", str(raw or ""))
    text = re.sub(r"[\[\]\(\)]", "", text)
    return re.sub(r"\s+", " ", text).strip()


def markdown_extract_table(lines, start_idx):
    rows = []
    for line in lines[start_idx + 1:]:
        if not line.startswith("|"):
            break
        if set(line.strip()) <= set("|- :"):
            continue
        rows.append(line.rstrip())
    return rows


def markdown_find_header_indices(headers):
    idx_desc = next((i for i, header in enumerate(headers) if "remap" in header.lower() or "descriptor" in header.lower()), None)
    idx_retropad = next((i for i, header in enumerate(headers) if "retropad" in header.lower()), None)
    system_cols = [i for i in range(len(headers)) if i not in (idx_desc, idx_retropad)]
    return idx_desc, idx_retropad, system_cols


def markdown_extract_image_name(cell):
    match = re.search(r"/([^/]+?)\.(?:png|jpg|svg)", str(cell or ""))
    return match.group(1) if match else ""


def markdown_extract_system_entry(cell):
    name = markdown_extract_image_name(cell)
    if name:
        if "_" in name:
            name = name.split("_", 1)[1]
        return markdown_clean_text(name)
    return markdown_clean_text(cell)


def fallback_retropad_entries(lines):
    seen = set()
    out = []
    for line in lines:
        for match in re.finditer(r"\.\./image/retropad/retro_([a-z0-9_]+)\.png", line, flags=re.IGNORECASE):
            key = match.group(1).lower()
            if key in seen:
                continue
            seen.add(key)
            out.append({
                "retropad_key": key,
                "retropad_id": LIBRETRO_RETROPAD_IDS.get(key),
                "label": key.upper(),
                "system_entry": "",
            })
    return out


def parse_libretro_core_md(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.read().splitlines()
    except Exception:
        return None

    groups = []
    last_heading = "default"
    i = 0

    while i < len(lines):
        line = lines[i]
        if line.startswith("####"):
            last_heading = markdown_clean_text(line.lstrip("#").strip()) or "default"
            i += 1
            continue

        if line.startswith("|"):
            table_rows = markdown_extract_table(lines, i)
            if any("../image/retropad/" in row for row in table_rows):
                headers = [cell.strip() for cell in line.strip().strip("|").split("|")]
                idx_desc, idx_retropad, system_cols = markdown_find_header_indices(headers)
                if idx_retropad is None and table_rows:
                    first = [cell.strip() for cell in table_rows[0].strip().strip("|").split("|")]
                    idx_retropad = next((j for j, value in enumerate(first) if "../image/retropad/" in value), None)

                for system_col in system_cols:
                    group_name = markdown_clean_text(headers[system_col] or last_heading) or "default"
                    mappings = []
                    for row in table_rows:
                        cells = [cell.strip() for cell in row.strip().strip("|").split("|")]
                        if idx_retropad is None or idx_retropad >= len(cells):
                            continue
                        raw_name = markdown_extract_image_name(cells[idx_retropad])
                        if not raw_name:
                            continue
                        key = raw_name.replace("retro_", "").lower()
                        label = cells[idx_desc] if idx_desc is not None and idx_desc < len(cells) else ""
                        system_entry = cells[system_col] if system_col < len(cells) else ""
                        mappings.append({
                            "retropad_key": key,
                            "retropad_id": LIBRETRO_RETROPAD_IDS.get(key),
                            "label": markdown_clean_text(label) or markdown_extract_system_entry(system_entry) or key.upper(),
                            "system_entry": markdown_extract_system_entry(system_entry),
                        })
                    if mappings:
                        groups.append({
                            "name": group_name,
                            "slug": slugify_text(group_name) or "default",
                            "mappings": mappings,
                        })
                i += len(table_rows) + 1
                continue
        i += 1

    if not groups:
        fallback = fallback_retropad_entries(lines)
        if fallback:
            groups = [{
                "name": "default",
                "slug": "default",
                "mappings": fallback,
            }]

    core = os.path.splitext(os.path.basename(path))[0]
    display_name = markdown_clean_text(lines[0].lstrip("#").strip()) if lines and lines[0].startswith("#") else core
    content_databases = []
    for line in lines:
        for match in re.finditer(r"/rdb/([^)\]]+?)\.rdb", line, flags=re.IGNORECASE):
            database_name = markdown_clean_text(unquote(os.path.basename(match.group(1))))
            if database_name and database_name not in content_databases:
                content_databases.append(database_name)

    default_group = groups[0] if groups else {"slug": "default", "mappings": []}
    unique_buttons = {}
    for group in groups:
        for mapping in group.get("mappings", []):
            key = mapping.get("retropad_key", "")
            if key and key not in unique_buttons:
                unique_buttons[key] = clean_export_dict({
                    "retropad_id": mapping.get("retropad_id"),
                    "label": mapping.get("label", ""),
                })

    return {
        "schema": "api_expose.panel.v1",
        "scope": "core",
        "core": core,
        "display_name": display_name or core,
        "panel": build_system_panel_payload(),
        "core_template": {
            "retropad": {
                "buttons": unique_buttons,
            },
            "default_group": default_group.get("slug", "default"),
            "groups": {
                group["slug"]: {
                    "name": group["name"],
                    "mappings": group["mappings"],
                }
                for group in groups
            },
            "content_databases": content_databases,
        },
        "events": empty_events(),
    }


def export_core_templates():
    if not os.path.isdir(LIBRETRO_CORE_MD_DIR):
        return 0

    count = 0
    for name in sorted(os.listdir(LIBRETRO_CORE_MD_DIR)):
        if not name.lower().endswith(".md"):
            continue
        data = parse_libretro_core_md(os.path.join(LIBRETRO_CORE_MD_DIR, name))
        if not data:
            continue
        out_path = os.path.join(CORE_OUTPUT_DIR, f"{data['core']}.json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
        count += 1
    return count


def load_machine_to_emus_map():
    if not os.path.exists(GENRETROARCH_SOURCE):
        return {}
    try:
        source = open(GENRETROARCH_SOURCE, "r", encoding="utf-8").read()
        tree = ast.parse(source, filename=GENRETROARCH_SOURCE)
    except Exception:
        return {}

    for node in tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id == "MACHINE_TO_EMUS":
                    try:
                        value = ast.literal_eval(node.value)
                        if isinstance(value, dict):
                            return {
                                str(system): [str(core) for core in cores]
                                for system, cores in value.items()
                                if isinstance(cores, (list, tuple))
                            }
                    except Exception:
                        return {}
    return {}


def default_core_system_yaml_header(core, system_name):
    core_label = str(core or "").strip() or "core"
    system_label = str(system_name or "").strip() or "system"
    return [
        f"# {system_label.upper()} INPUT REMAPS FOR RETROARCH",
        "#",
        "# This file is generated by panel_curator_ultimate.py",
        "#",
        f"# It is used to provide default input remaps for Retroarch for {system_label} games with {core_label} core",
        "# Each container must be named exactly the same as your game rom file (without the extension)",
        "#",
        "# The elements listed are the buttons for which you want the function to be remapped",
        "# The key represents the RetroArch button code",
        "# The value represents the original button ID for the specific system/core default layout",
        "# you can use the -1 value to unmap a button",
    ]


def parse_simple_yaml_mapping_file_global(path):
    header = []
    mapping = {}
    current_key = None
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.read().splitlines()
    except Exception:
        return header, mapping

    for line in lines:
        stripped = line.strip()
        if current_key is None and (not stripped or stripped.startswith("#")):
            header.append(line)
            continue
        if not stripped or stripped.startswith("#"):
            continue
        if not line.startswith(" ") and stripped.endswith(":"):
            current_key = stripped[:-1]
            mapping.setdefault(current_key, {})
            continue
        if current_key and line.startswith("  ") and ":" in stripped:
            key, value = stripped.split(":", 1)
            value = value.strip()
            parsed = int(value) if re.fullmatch(r"-?\d+", value) else value
            mapping[current_key][key.strip()] = parsed
    return header, mapping


def dump_simple_yaml_mapping_file_global(path, header_lines, mapping):
    lines = list(header_lines or [])
    if lines and lines[-1].strip():
        lines.append("")

    for name, entry in mapping.items():
        lines.append(f"{name}:")
        if isinstance(entry, dict):
            for key, value in entry.items():
                lines.append(f"  {key}: {value}")
        lines.append("")

    text = "\n".join(lines).rstrip() + "\n"
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def preferred_system_layout(layouts):
    if not isinstance(layouts, dict):
        return None
    unnamed = [layout for layout in layouts.values() if not layout.get("name")]
    named = [layout for layout in layouts.values() if layout.get("name")]

    unnamed_max = max([int(layout.get("panel_buttons") or 0) for layout in unnamed] or [0])
    named_counts = sorted({int(layout.get("panel_buttons") or 0) for layout in named if int(layout.get("panel_buttons") or 0) > unnamed_max})
    if named_counts:
        target = named_counts[0]
        for layout in named:
            if int(layout.get("panel_buttons") or 0) == target:
                return layout

    if unnamed:
        unnamed_sorted = sorted(unnamed, key=lambda layout: int(layout.get("panel_buttons") or 0), reverse=True)
        if unnamed_sorted:
            return unnamed_sorted[0]

    named_sorted = sorted(named, key=lambda layout: int(layout.get("panel_buttons") or 0), reverse=True)
    return named_sorted[0] if named_sorted else None


def system_layout_buttons_for_yaml(layout):
    buttons = []
    for button_id, button in (layout.get("buttons") or {}).items():
        if not str(button_id).isdigit():
            continue
        retropad_id = button.get("retropad_id")
        if retropad_id is None:
            continue
        panel_slot = button.get("panel_slot")
        try:
            panel_slot = int(panel_slot)
        except Exception:
            continue
        buttons.append({
            "button_id": int(button_id),
            "panel_slot": panel_slot,
            "retropad_id": int(retropad_id),
        })
    return sorted(buttons, key=lambda item: (item["panel_slot"], item["button_id"]))


def core_system_yaml_entry_from_buttons(buttons, variant_name, analog_dpad_mode=0):
    entry = {"analog_dpad_mode": int(analog_dpad_mode or 0)}
    for key in LIBRETRO_USER_YAML_BUTTON_ORDER:
        entry[key] = -1

    sequence = LIBRETRO_USER_YAML_VARIANTS.get(variant_name, [])
    for index, button in enumerate(buttons):
        if index >= len(sequence):
            break
        entry[sequence[index]] = int(button.get("retropad_id", -1))
    return entry


def build_core_system_yaml_entries(system_data):
    layouts = ((system_data or {}).get("system_template") or {}).get("layouts") or {}
    layout = preferred_system_layout(layouts)
    if not layout:
        return {}
    buttons = system_layout_buttons_for_yaml(layout)
    if not buttons:
        return {}
    analog_dpad_mode = layout.get("retropad_analog_dpad_mode", 0)
    return {
        "default": core_system_yaml_entry_from_buttons(buttons, "default", analog_dpad_mode),
        "default_modern8": core_system_yaml_entry_from_buttons(buttons, "modern8", analog_dpad_mode),
        "default_8alternative": core_system_yaml_entry_from_buttons(buttons, "8alternative", analog_dpad_mode),
        "default_6alternative": core_system_yaml_entry_from_buttons(buttons, "6alternative", analog_dpad_mode),
    }


def export_core_inputmapping_templates():
    machine_to_emus = load_machine_to_emus_map()
    if not machine_to_emus:
        return 0
    if not os.path.isdir(SYSTEM_OUTPUT_DIR):
        return 0

    count = 0
    for system_filename in sorted(os.listdir(SYSTEM_OUTPUT_DIR)):
        if not system_filename.lower().endswith(".json"):
            continue
        system_path = os.path.join(SYSTEM_OUTPUT_DIR, system_filename)
        try:
            system_data = json.load(open(system_path, "r", encoding="utf-8"))
        except Exception:
            continue
        system_name = str(system_data.get("system") or os.path.splitext(system_filename)[0])
        entries = build_core_system_yaml_entries(system_data)
        if not entries:
            continue

        for core in machine_to_emus.get(system_name, []):
            path = os.path.join(USER_INPUTMAPPING_DIR, f"libretro_{core}_{system_name}.yml")
            header, existing = parse_simple_yaml_mapping_file_global(path) if os.path.exists(path) else ([], {})
            if not header:
                fallback = os.path.join(os.path.dirname(os.path.dirname(USER_INPUTMAPPING_DIR)), "system", "resources", "inputmapping", os.path.basename(path))
                if os.path.exists(fallback):
                    header, existing = parse_simple_yaml_mapping_file_global(fallback)
            if not header:
                header = default_core_system_yaml_header(core, system_name)
            merged = dict(existing)
            merged.update(entries)
            dump_simple_yaml_mapping_file_global(path, header, merged)
            count += 1
    return count


def layout_key(layout_type, layout_name):
    if layout_name:
        return f"{layout_type}:{layout_name}"
    return layout_type


def parse_system_template_xml(path):
    try:
        root = ET.parse(path).getroot()
    except Exception:
        return None

    system = root.attrib.get("name") or os.path.splitext(os.path.basename(path))[0]
    layouts = {}
    for layout in root.findall(".//layout"):
        layout_type = layout.attrib.get("type", "")
        layout_name = layout.attrib.get("name", "")
        key = layout_key(layout_type, layout_name)
        joystick = layout.find("joystick")

        buttons = {}
        for button in layout.findall("button"):
            button_id = button.attrib.get("id", "")
            if not button_id:
                continue
            item = {
                "physical": parse_optional_int(button.attrib.get("physical")),
                "controller": button.attrib.get("controller", ""),
                "retropad_id": parse_optional_int(button.attrib.get("retropad_id")),
                "game_button": button.attrib.get("gameButton", ""),
                "function": button.attrib.get("function", ""),
                "color": button.attrib.get("color", ""),
            }
            if str(button_id).isdigit():
                item["panel_slot"] = int(button_id)
            buttons[button_id] = item

        layout_payload = {
            "type": layout_type,
            "name": layout_name or None,
            "panel_buttons": parse_optional_int(layout.attrib.get("panelButtons")),
            "retropad_device": parse_optional_int(layout.attrib.get("retropad_device")),
            "retropad_analog_dpad_mode": parse_optional_int(layout.attrib.get("retropad_analog_dpad_mode")),
            "joystick": dict(joystick.attrib) if joystick is not None else {},
            "buttons": buttons,
        }
        layouts[key] = enrich_system_layout(system, layout_type, layout_name, layout_payload)

    return {
        "schema": "api_expose.panel.v1",
        "scope": "system",
        "system": system,
        "panel": build_system_panel_payload(),
        "system_template": {
            "layouts": layouts,
        },
        "events": parse_lip_file(os.path.join(SYSTEMS_PANEL_DIR, f"{system}.lip")),
    }


def export_system_templates():
    if not os.path.isdir(SYSTEMS_PANEL_DIR):
        return 0

    count = 0
    for name in sorted(os.listdir(SYSTEMS_PANEL_DIR)):
        if not name.lower().endswith(".xml"):
            continue
        data = parse_system_template_xml(os.path.join(SYSTEMS_PANEL_DIR, name))
        if not data:
            continue
        out_path = os.path.join(SYSTEM_OUTPUT_DIR, f"{data['system']}.json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
        count += 1
    return count


def guess_pattern(button_functions, num_buttons):
    upper = [(x or "").strip().upper() for x in button_functions]
    mk_roles = [normalize_mk_role(x) for x in button_functions if x]
    mk_roles = [x for x in mk_roles if x]

    if {"HP", "LP", "HK", "LK", "BL", "RN"}.issubset(set(mk_roles)):
        return "mk6"
    if {"HP", "LP", "HK", "LK", "BL"}.issubset(set(mk_roles)):
        return "mk5_block"
    if upper[:4] == ["A", "B", "C", "D"]:
        return "neo_4"
    if upper[:3] == ["A", "B", "C"]:
        return "neo_3"

    try:
        n = int(num_buttons)
    except Exception:
        n = len(button_functions)

    if n == 1:
        return "line_1"
    if n == 2:
        return "line_2"
    if n == 3:
        return "line_3_bottom"
    if n == 4:
        return "neo_4"
    if n == 6:
        return "capcom_6_straight"
    if n >= 7:
        return "full_8"

    return "line_3_bottom"


def build_common_button_vars(players_data):
    max_buttons = 0
    for pdata in players_data:
        max_buttons = max(max_buttons, len(pdata["buttons"]))

    common = {}
    for idx in range(1, max_buttons + 1):
        logical_name = ""
        function = ""
        for pdata in players_data:
            if idx <= len(pdata["buttons"]):
                b = pdata["buttons"][idx - 1]
                logical_name = b["logical_name"]
                function = b["function"]
                break

        common[str(idx)] = {
            "logical_name": logical_name,
            "function": function,
        }
    return common


# ============================================================
# REPOSITORY
# ============================================================

class DataRepository:
    def __init__(self):
        self.controls = load_ini(CONTROLS_INI)
        self.colors = load_ini(COLORS_INI)

    def get_game_data(self, rom):
        controls_sec = self.controls[rom] if rom in self.controls else None
        colors_sec = self.colors[rom] if rom in self.colors else None
        mame_output_data = load_mame_output_data(rom)
        mame_inputs = merge_mame_input_indexes(parse_mame_inputs(rom), mame_output_data.get("inputs", []))

        game_name = safe_get(controls_sec, "gamename", rom)

        players = int(safe_get(controls_sec, "numPlayers", safe_get(colors_sec, "numPlayers", "1")) or "1")
        alternating = int(safe_get(controls_sec, "alternating", "0") or "0")
        mirrored = int(safe_get(controls_sec, "mirrored", "0") or "0")
        tilt = int(safe_get(controls_sec, "tilt", "0") or "0")
        cocktail = int(safe_get(controls_sec, "cocktail", "0") or "0")
        uses_service = int(safe_get(controls_sec, "usesService", "0") or "0")
        misc_details = safe_get(controls_sec, "miscDetails", "")

        players_data = []

        for player in range(1, players + 1):
            raw_controls = inherited_controls_string(controls_sec, colors_sec, player)
            joystick_color = safe_get(colors_sec, f"P{player}_JOYSTICK", "")
            start_color = safe_get(colors_sec, f"P{player}_START", "")
            coin_color = safe_get(colors_sec, f"P{player}_COIN", "")
            num_buttons = safe_get(controls_sec, f"P{player}NumButtons", safe_get(colors_sec, "numButtons", "0"))

            try:
                nbtn = int(num_buttons)
            except Exception:
                nbtn = 0

            devices = parse_controls_segments(raw_controls)
            if not devices:
                devices = [{
                    "id": "D1",
                    "label": "Device",
                    "type": normalize_control_type(raw_controls),
                    "raw": raw_controls,
                    "codes": [],
                }]

            device_inputs = {}
            for direction in ("up", "down", "left", "right"):
                function_name = inherited_joystick_function(controls_sec, player, direction.upper())
                mame_data = first_mame_match(
                    mame_inputs,
                    direction_candidates(player, direction, function_name)
                )
                device_inputs[direction] = {
                    "function": function_name,
                    "color": joystick_color,
                    "output": output_for_mame_input(mame_data, mame_output_data.get("mappings", [])),
                    "mame": mame_data,
                    "panel_joystick": None,
                    "slots_by_layout": {layout: None for layout in LAYOUTS},
                }

            devices_export = []
            first_device = True
            for dev in devices:
                entry = {
                    "id": dev["id"],
                    "label": dev["label"],
                    "type": dev["type"],
                    "raw": dev.get("raw", ""),
                    "codes": dev.get("codes", []),
                    "color": joystick_color,
                }
                entry["inputs"] = device_inputs if first_device else {}
                first_device = False
                devices_export.append(entry)

            button_functions = []
            buttons = []

            for i in range(1, nbtn + 1):
                function_name = inherited_button_function(controls_sec, player, i, f"Button {i}")
                color_name = safe_get(colors_sec, f"P{player}_BUTTON{i}", "")
                logical_name = logical_name_for_button(function_name, i)

                mame_data = first_mame_match(
                    mame_inputs,
                    canonical_button_candidates(player, i, logical_name, function_name)
                )
                if function_name == f"Button {i}":
                    inferred_function = infer_button_function_from_mame(mame_data, player)
                    if inferred_function:
                        function_name = inferred_function
                        logical_name = logical_name_for_button(function_name, i)

                buttons.append({
                    "game_button": i,
                    "logical_name": logical_name,
                    "function": function_name,
                    "color": color_name,
                    "output": output_for_mame_input(mame_data, mame_output_data.get("mappings", [])),
                    "mame": mame_data,
                    "panel_joystick": None,
                    "slots_by_layout": {layout: None for layout in LAYOUTS},
                })
                button_functions.append(function_name)

            axes = collect_analog_inputs(mame_output_data, mame_inputs, player)
            for axis in axes:
                axis["color"] = analog_axis_device_color(axis, devices_export, axis.get("color", "Gray"))

            start_mame = first_mame_match(mame_inputs, canonical_start_candidates(player))
            select_mame = first_mame_match(mame_inputs, canonical_select_candidates(player))
            coin_mame = first_mame_match(mame_inputs, canonical_coin_candidates(player))
            has_select_game = is_select_game_mame_input(select_mame)
            system_inputs = {
                "start": {
                    "label": "Start",
                    "color": start_color,
                    "output": output_for_system_input("select" if has_select_game else "start", start_mame, mame_output_data),
                    "mame": start_mame,
                    "panel_joystick": None,
                    "slots_by_layout": {layout: None for layout in LAYOUTS},
                },
                "coin": {
                    "label": "Coin",
                    "color": coin_color,
                    "output": output_for_system_input("coin", coin_mame, mame_output_data),
                    "mame": coin_mame,
                    "panel_joystick": None,
                    "slots_by_layout": {layout: None for layout in LAYOUTS},
                }
            }
            if has_select_game:
                system_inputs["select"] = {
                    "label": "Select",
                    "color": start_color,
                    "output": output_for_system_input("select", select_mame, mame_output_data),
                    "mame": select_mame,
                    "panel_joystick": None,
                    "slots_by_layout": {layout: None for layout in LAYOUTS},
                }

            system_outputs = []
            for out_idx, out in enumerate(mame_output_data.get("outputs", [])):
                output_player = out.get("player")
                try:
                    output_player = int(output_player) if output_player not in (None, "") else None
                except Exception:
                    output_player = None
                if output_player not in (None, player):
                    continue
                output_entry = copy.deepcopy(out)
                output_name = output_entry.get("name") or f"output_{out_idx + 1}"
                output_entry.setdefault("id", f"{output_name}#{out_idx + 1}")
                output_entry.setdefault("label", output_entry.get("function") or output_name)
                output_entry.setdefault("color", "Gray")
                output_entry.setdefault("input_ref", "")
                output_entry.setdefault("panel_joystick", None)
                output_entry.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                system_outputs.append(output_entry)
            system_outputs.sort(key=output_sort_key)

            players_data.append({
                "player": player,
                "devices": devices_export,
                "system_inputs": system_inputs,
                "buttons": buttons,
                "axes": axes,
                "mame_inputs_extra": [],
                "system_outputs": system_outputs,
                "output_choices": [""] + sorted({
                    out.get("name", "")
                    for out in system_outputs
                    if out.get("name")
                }, key=natural_sort_key),
                "pattern": guess_pattern(button_functions, nbtn),
            })

        data = {
            "system": rom,
            "game_name": game_name,
            "meta": {
                "players": players,
                "alternating": alternating,
                "mirrored": mirrored,
                "tilt": tilt,
                "cocktail": cocktail,
                "uses_service": uses_service,
                "misc_details": misc_details,
            },
            "players_data": players_data,
            "common_button_vars": build_common_button_vars(players_data),
            "mame": mame_output_data,
            "events": load_arcade_events(rom),
        }

        existing_path = os.path.join(OUTPUT_DIR, rom + ".json")
        if os.path.exists(existing_path):
            try:
                with open(existing_path, "r", encoding="utf-8") as f:
                    old = json.load(f)
                self.merge_existing(data, old)
            except Exception:
                pass

        return data

    def merge_existing(self, data, old):
        old_players = old.get("players", {})
        for p in data["players_data"]:
            pkey = str(p["player"])
            old_p = old_players.get(pkey)
            if not old_p:
                continue

            old_buttons = old_p.get("buttons", {})
            old_layouts = old_p.get("layouts", {})
            old_devices = old_p.get("devices", [])
            old_sys = old_p.get("system_inputs", {})
            old_outputs_raw = old_p.get("system_outputs", [])
            if isinstance(old_outputs_raw, dict):
                old_outputs = old_outputs_raw
            else:
                old_outputs = {
                    (out.get("id") or out.get("name")): out
                    for out in old_outputs_raw
                    if isinstance(out, dict) and (out.get("id") or out.get("name"))
                }
            old_axes_raw = old_p.get("axes", [])
            if isinstance(old_axes_raw, dict):
                old_axes = old_axes_raw
            else:
                old_axes = {
                    axis.get("id"): axis
                    for axis in old_axes_raw
                    if isinstance(axis, dict) and axis.get("id")
                }

            for i, dev in enumerate(p["devices"]):
                if i < len(old_devices):
                    dev["type"] = old_devices[i].get("type", dev["type"])
                    dev["color"] = old_devices[i].get("color", dev["color"])
                    old_inputs = old_devices[i].get("inputs", {})
                    for direction, entry in dev.get("inputs", {}).items():
                        old_entry = old_inputs.get(direction, {})
                        entry["color"] = old_entry.get("color", entry.get("color", dev.get("color", "")))
                        entry["output"] = old_entry.get("output", entry.get("output", ""))
                        entry["panel_joystick"] = old_entry.get("panel_joystick", entry.get("panel_joystick"))
                        old_entry_layouts = old_entry.get("layouts", {})
                        for layout_name in LAYOUTS:
                            old_layout = old_entry_layouts.get(layout_name, {})
                            old_slot_value = layout_panel_slot_value(old_layout)
                            if old_slot_value is not None:
                                entry["slots_by_layout"][layout_name] = old_slot_value

            if "start" in old_sys:
                p["system_inputs"]["start"]["color"] = old_sys["start"].get("color", p["system_inputs"]["start"]["color"])
                p["system_inputs"]["start"]["output"] = old_sys["start"].get("output", p["system_inputs"]["start"].get("output", ""))
            if "coin" in old_sys:
                p["system_inputs"]["coin"]["color"] = old_sys["coin"].get("color", p["system_inputs"]["coin"]["color"])
                p["system_inputs"]["coin"]["output"] = old_sys["coin"].get("output", p["system_inputs"]["coin"].get("output", ""))
            if "select" in old_sys and "select" in p["system_inputs"]:
                p["system_inputs"]["select"]["color"] = old_sys["select"].get("color", p["system_inputs"]["select"]["color"])
                p["system_inputs"]["select"]["output"] = old_sys["select"].get("output", p["system_inputs"]["select"].get("output", ""))
            for key, entry in p["system_inputs"].items():
                entry.setdefault("label", key.capitalize())
                entry.setdefault("panel_joystick", None)
                entry.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                old_entry = old_sys.get(key, {})
                if old_entry:
                    entry["panel_joystick"] = old_entry.get("panel_joystick", entry.get("panel_joystick"))
                    for layout_name in LAYOUTS:
                        old_slot_value = layout_panel_slot_value(old_entry.get("layouts", {}).get(layout_name, {}))
                        if old_slot_value is not None:
                            entry["slots_by_layout"][layout_name] = old_slot_value
                for layout_name in LAYOUTS:
                    old_layout = old_layouts.get(layout_name, {})
                    old_layout_system_inputs = old_layout.get("system_inputs", {})
                    old_si = old_layout_system_inputs.get(key)
                    old_slot_value = layout_panel_slot_value(old_si)
                    if old_slot_value is not None:
                        entry["slots_by_layout"][layout_name] = old_slot_value

            for output in p.get("system_outputs", []):
                output.setdefault("id", output.get("name") or "")
                output.setdefault("label", output.get("function") or output.get("name") or output.get("id"))
                output.setdefault("color", "Gray")
                output.setdefault("input_ref", "")
                output.setdefault("panel_joystick", None)
                output.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                old_output = old_outputs.get(output.get("id")) or old_outputs.get(output.get("name")) or {}
                if old_output:
                    output["label"] = old_output.get("label", output.get("label", ""))
                    output["color"] = old_output.get("color", output.get("color", "Gray"))
                    output["input_ref"] = old_output.get("input_ref", output.get("input_ref", ""))
                    output["panel_joystick"] = old_output.get("panel_joystick", output.get("panel_joystick"))
                    for layout_name in LAYOUTS:
                        old_slot_value = layout_panel_slot_value(old_output.get("layouts", {}).get(layout_name, {}))
                        if old_slot_value is not None:
                            output["slots_by_layout"][layout_name] = old_slot_value
                for layout_name in LAYOUTS:
                    old_layout = old_layouts.get(layout_name, {})
                    old_layout_outputs = old_layout.get("system_outputs", {})
                    old_lo = old_layout_outputs.get(output.get("id")) or old_layout_outputs.get(output.get("name"))
                    old_slot_value = layout_panel_slot_value(old_lo)
                    if old_slot_value is not None:
                        output["slots_by_layout"][layout_name] = old_slot_value

            for layout_name in LAYOUTS:
                if layout_name in old_layouts:
                    p["pattern"] = old_layouts[layout_name].get("pattern", p["pattern"])
                    break

            for old_key, old_button in old_buttons.items():
                if not isinstance(old_button, dict):
                    continue
                instance_id = old_button.get("instance_id") or (old_key if "#" in str(old_key) else "")
                if not instance_id:
                    continue
                base_id = old_button.get("duplicate_of", old_button.get("game_button"))
                try:
                    base_id = int(base_id)
                except Exception:
                    continue
                base_button = next((b for b in p["buttons"] if b["game_button"] == base_id), None)
                if not base_button:
                    continue
                clone = copy.deepcopy(base_button)
                clone["instance_id"] = str(instance_id)
                clone["duplicate_of"] = old_button.get("duplicate_of", base_id)
                clone["slots_by_layout"] = {layout: None for layout in LAYOUTS}
                p["buttons"].append(clone)

            for b in p["buttons"]:
                ob = old_buttons.get(str(b.get("instance_id") or b["game_button"]))
                if ob:
                    b["logical_name"] = ob.get("logical_name", b["logical_name"])
                    b["function"] = ob.get("function", b["function"])
                    b["color"] = ob.get("color", b["color"])
                    b["output"] = ob.get("output", b.get("output", ""))
                    b["panel_joystick"] = ob.get("panel_joystick", b.get("panel_joystick"))

                for layout_name in LAYOUTS:
                    old_layout = old_layouts.get(layout_name, {})
                    old_layout_buttons = old_layout.get("buttons", {})
                    old_lb = old_layout_buttons.get(str(b.get("instance_id") or b["game_button"]))
                    if old_lb:
                        b["slots_by_layout"][layout_name] = layout_panel_slot_value(old_lb)

            for axis in p.get("axes", []):
                old_axis = old_axes.get(axis.get("id"), {})
                if old_axis:
                    axis["color"] = old_axis.get("color", axis.get("color", "Gray"))
                    axis["output"] = old_axis.get("output", axis.get("output", ""))
                    axis["panel_joystick"] = old_axis.get("panel_joystick", axis.get("panel_joystick"))
                    axis["physical_joystick"] = old_axis.get("physical_joystick", axis.get("physical_joystick", ""))
                    old_joystick = old_axis.get("joystick", {})
                    if old_joystick:
                        axis["joystick"] = {
                            "negative": old_joystick.get("negative", axis.get("joystick", {}).get("negative", "")),
                            "positive": old_joystick.get("positive", axis.get("joystick", {}).get("positive", "")),
                        }
                    elif axis["physical_joystick"]:
                        axis["joystick"] = {
                            "negative": axis.get("joystick", {}).get("negative", ""),
                            "positive": axis["physical_joystick"],
                        }
                    axis["physical_axis"] = old_axis.get("physical_axis", axis.get("physical_axis", ""))
                    old_axis_layouts = old_axis.get("layouts", {})
                    for layout_name in LAYOUTS:
                        old_layout = old_axis_layouts.get(layout_name, {})
                        old_slot_value = layout_panel_slot_value(old_layout)
                        if old_slot_value is not None:
                            axis["slots_by_layout"][layout_name] = old_slot_value
                            axis.setdefault("slots_by_polarity", {}).setdefault("positive", {})[layout_name] = old_slot_value

                    for polarity, old_direction in old_axis.get("directions", {}).items():
                        if polarity not in ("negative", "positive"):
                            continue
                        for layout_name, old_layout in old_direction.get("layouts", {}).items():
                            old_slot_value = layout_panel_slot_value(old_layout)
                            if layout_name in LAYOUTS and old_slot_value is not None:
                                axis.setdefault("slots_by_polarity", {}).setdefault(polarity, {})[layout_name] = old_slot_value
                                if axis["slots_by_layout"].get(layout_name) is None:
                                    axis["slots_by_layout"][layout_name] = old_slot_value

                for layout_name in LAYOUTS:
                    old_layout = old_layouts.get(layout_name, {})
                    old_layout_axes = old_layout.get("axes", {})
                    old_la = old_layout_axes.get(axis.get("id"))
                    old_slot_value = layout_panel_slot_value(old_la)
                    if old_slot_value is not None:
                        axis["slots_by_layout"][layout_name] = old_slot_value
                        axis.setdefault("slots_by_polarity", {}).setdefault("positive", {})[layout_name] = old_slot_value
                    elif isinstance(old_la, dict):
                        for polarity in ("negative", "positive"):
                            pol = old_la.get(polarity, {})
                            pol_slot_value = layout_panel_slot_value(pol)
                            if pol_slot_value is not None:
                                axis.setdefault("slots_by_polarity", {}).setdefault(polarity, {})[layout_name] = pol_slot_value
                                if axis["slots_by_layout"].get(layout_name) is None:
                                    axis["slots_by_layout"][layout_name] = pol_slot_value

        data["common_button_vars"] = build_common_button_vars(data["players_data"])


# ============================================================
# SCROLLABLE FRAME
# ============================================================

class ScrollableFrame(ttk.Frame):
    def __init__(self, parent):
        super().__init__(parent)
        self.canvas = tk.Canvas(self, highlightthickness=0)
        self.vscroll = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)

        self.inner.bind(
            "<Configure>",
            lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all"))
        )

        self.window_id = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=self.vscroll.set)

        self.canvas.pack(side="left", fill="both", expand=True)
        self.vscroll.pack(side="right", fill="y")

        self.canvas.bind(
            "<Configure>",
            lambda e: self.canvas.itemconfig(self.window_id, width=e.width)
        )


# ============================================================
# GRID VIEW
# ============================================================

class GridView(ttk.Frame):
    def __init__(self, parent, layout_name, on_slot_click):
        super().__init__(parent)
        self.layout_name = layout_name
        self.on_slot_click = on_slot_click
        self.canvas = tk.Canvas(
            self,
            width=470,
            height=205,
            bg="#0f172a",
            highlightthickness=1,
            highlightbackground="#334155"
        )
        self.canvas.pack(fill="both", expand=True)
        self.canvas.bind("<Button-1>", self._click)
        self.hitboxes = {}

    def render(self, assigned_map):
        self.canvas.delete("all")
        self.hitboxes = {}

        self.canvas.create_text(
            235, 18,
            text=self.layout_name,
            fill="#e2e8f0",
            font=("Segoe UI", 13, "bold")
        )

        coords = GRID_COORDS[self.layout_name]
        slots = LAYOUT_SLOTS[self.layout_name]

        for slot in slots:
            x, y = coords[slot]
            r = 36

            self.canvas.create_oval(
                x-r, y-r, x+r, y+r,
                fill="#334155",
                outline="#94a3b8",
                width=3
            )

            self.canvas.create_text(
                x, y - 28,
                text=str(slot),
                fill="#cbd5e1",
                font=("Segoe UI", 11, "bold")
            )
            self.canvas.create_text(
                x, y + 28,
                text=SLOT_MAP[str(slot)]["retrobat_button"],
                fill="#cbd5e1",
                font=("Segoe UI", 11, "bold")
            )

            assigned = assigned_map.get(slot)
            if assigned:
                self.canvas.create_oval(
                    x-22, y-22, x+22, y+22,
                    fill=color_to_hex(assigned["color"]),
                    outline="#111827",
                    width=3
                )
                self.canvas.create_text(
                    x, y,
                    text=assigned["short"],
                    fill="#111827",
                    font=("Segoe UI", 12, "bold")
                )

            self.hitboxes[slot] = (x-r, y-r, x+r, y+r)

    def _click(self, event):
        for slot, (x1, y1, x2, y2) in self.hitboxes.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_slot_click(self.layout_name, slot)
                return


class CompactLayoutCard(tk.Frame):
    def __init__(self, parent, layout_name, on_slot_click, on_slot_right_click=None):
        super().__init__(parent, bg="#f2f4f7", bd=1, relief="solid", highlightthickness=1, highlightbackground="#d0d5dd")
        self.layout_name = layout_name
        self.on_slot_click = on_slot_click
        self.on_slot_right_click = on_slot_right_click
        self.active = False
        self.canvas = tk.Canvas(self, height=155, bg="#f2f4f7", highlightthickness=0)
        self.canvas.pack(fill="x", expand=True, padx=8, pady=(8, 2))
        self.label = tk.Label(self, text=layout_name.upper(), bg="#f2f4f7", fg="#1d2939", font=("Segoe UI", 8, "bold"))
        self.label.pack(pady=(0, 10))
        self.hitboxes = {}
        self.assigned_map = {}
        self.canvas.bind("<Button-1>", self._click)
        self.canvas.bind("<Button-3>", self._right_click)
        self.canvas.bind("<Configure>", lambda e: self.render(self.assigned_map))

    def set_active(self, active):
        self.active = active
        bg = "#ffffff" if active else "#f2f4f7"
        self.configure(bg=bg, highlightbackground="#2459d3" if active else "#d0d5dd", highlightthickness=2 if active else 1)
        self.canvas.configure(bg=bg)
        self.label.configure(bg=bg, fg="#2459d3" if active else "#1d2939")

    def _scaled_coords(self, slot):
        x, y = GRID_COORDS[self.layout_name][slot]
        width = max(self.canvas.winfo_width(), 170)
        return int(18 + (x / 470) * (width - 36)), int(16 + (y / 205) * 120)

    def render(self, assigned_map):
        self.assigned_map = assigned_map
        bg = "#ffffff" if self.active else "#f2f4f7"
        self.canvas.configure(bg=bg)
        self.canvas.delete("all")
        self.hitboxes = {}
        for slot in LAYOUT_SLOTS[self.layout_name]:
            x, y = self._scaled_coords(slot)
            r = 13
            self.canvas.create_text(x, y - 22, text=str(slot), fill="#475467", font=("Segoe UI", 7, "bold"))
            self.canvas.create_oval(x-r, y-r, x+r, y+r, fill="#cbd5e1", outline="#98a2b3", width=2)
            self.canvas.create_text(x, y + 22, text=SLOT_MAP[str(slot)]["retrobat_button"], fill="#667085", font=("Segoe UI", 7, "bold"))
            assigned = assigned_map.get(slot)
            if assigned:
                self.canvas.create_oval(x-r+2, y-r+2, x+r-2, y+r-2, fill=color_to_hex(assigned["color"]), outline="white", width=1)
                self.canvas.create_text(x, y, text=str(assigned["short"])[:3], fill="#111827", font=("Segoe UI", 7, "bold"))
            self.hitboxes[slot] = (x-r, y-r, x+r, y+r)

    def _click(self, event):
        for slot, (x1, y1, x2, y2) in self.hitboxes.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_slot_click(self.layout_name, slot)
                return

    def _right_click(self, event):
        if not self.on_slot_right_click:
            return
        for slot, (x1, y1, x2, y2) in self.hitboxes.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_slot_right_click(self.layout_name, slot)
                return


class Joystick8Card(tk.Frame):
    def __init__(self, parent, on_direction_click, on_center_click=None, on_direction_right_click=None):
        super().__init__(parent, bg="#f2f4f7", bd=1, relief="solid", highlightthickness=1, highlightbackground="#d0d5dd")
        self.on_direction_click = on_direction_click
        self.on_center_click = on_center_click
        self.on_direction_right_click = on_direction_right_click
        self.canvas = tk.Canvas(self, height=155, bg="#f2f4f7", highlightthickness=0)
        self.canvas.pack(fill="x", expand=True, padx=8, pady=(8, 2))
        self.label = tk.Label(self, text="JOYSTICK 8-WAY", bg="#f2f4f7", fg="#1d2939", font=("Segoe UI", 8, "bold"))
        self.label.pack(pady=(0, 10))
        self.hitboxes = {}
        self.center_hitbox = None
        self.assigned_map = {}
        self.canvas.bind("<Button-1>", self._click)
        self.canvas.bind("<Button-3>", self._right_click)
        self.canvas.bind("<Configure>", lambda e: self.render(self.assigned_map))

    def _scaled_coords(self, direction):
        width = max(self.canvas.winfo_width(), 170)
        cx = width // 2
        cy = 74
        x_outer = max(34, min(58, (width // 2) - 32))
        y_outer = 46
        x_diag = max(42, min(48, x_outer - 8))
        y_diag = 36
        offsets = {
            "up_left": (-x_diag, -y_diag),
            "up": (0, -y_outer),
            "up_right": (x_diag, -y_diag),
            "left": (-x_outer, 0),
            "right": (x_outer, 0),
            "down_left": (-x_diag, y_diag),
            "down": (0, y_outer),
            "down_right": (x_diag, y_diag),
        }
        dx, dy = offsets[direction]
        return int(cx + dx), int(cy + dy)

    def render(self, assigned_map):
        self.assigned_map = assigned_map
        self.canvas.delete("all")
        self.hitboxes = {}
        width = max(self.canvas.winfo_width(), 170)
        cx = width // 2
        cy = 74
        self.canvas.create_oval(cx - 19, cy - 19, cx + 19, cy + 19, fill="#cbd5e1", outline="#98a2b3", width=2)
        self.canvas.create_text(cx, cy, text="JOY", fill="#475467", font=("Segoe UI", 8, "bold"))
        self.center_hitbox = (cx - 23, cy - 23, cx + 23, cy + 23)

        for direction, label in JOYSTICK_PANEL_DIRECTIONS:
            x, y = self._scaled_coords(direction)
            r = 13
            self.canvas.create_oval(x-r, y-r, x+r, y+r, fill="#e4e7ec", outline="#98a2b3", width=2)
            self.canvas.create_text(x, y - 21, text=label, fill="#475467", font=("Segoe UI", 7, "bold"))
            assigned = assigned_map.get(direction)
            if assigned:
                self.canvas.create_oval(x-r+2, y-r+2, x+r-2, y+r-2, fill=color_to_hex(assigned["color"]), outline="white", width=1)
                self.canvas.create_text(x, y, text=str(assigned["short"])[:3], fill="#111827", font=("Segoe UI", 7, "bold"))
            self.hitboxes[direction] = (x-r, y-r, x+r, y+r)

    def _click(self, event):
        if self.center_hitbox and self.on_center_click:
            x1, y1, x2, y2 = self.center_hitbox
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_center_click()
                return
        for direction, (x1, y1, x2, y2) in self.hitboxes.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_direction_click(direction)
                return

    def _right_click(self, event):
        if not self.on_direction_right_click:
            return
        for direction, (x1, y1, x2, y2) in self.hitboxes.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.on_direction_right_click(direction)
                return


# ============================================================
# PLAYER EDITOR
# ============================================================

class PlayerEditor(ttk.Frame):
    def __init__(
        self,
        parent,
        player_data,
        on_apply_p1=None,
        on_layout_select=None,
        on_state_change=None,
        mame_device_choices=None,
        on_mame_device_select=None,
        on_mame_device_probe=None,
    ):
        super().__init__(parent)
        self.player_data = player_data
        self.on_apply_p1 = on_apply_p1
        self.on_layout_select = on_layout_select
        self.on_state_change = on_state_change
        self.mame_device_choices = list(mame_device_choices or manual_mame_device_choices())
        self.on_mame_device_select = on_mame_device_select
        self.on_mame_device_probe = on_mame_device_probe
        self.selected_button = None
        self.selected_system_input = None
        self.selected_system_output = None
        self.selected_joystick = None
        self.selected_axis = None
        self.active_layout = "4-Button"
        self.pattern_var = tk.StringVar(value=player_data["pattern"])
        self.mame_device_var = tk.StringVar(value=self.current_mame_device_label())
        self.autocopy_var = tk.BooleanVar(value=(player_data["player"] == 1))
        self.single_slot_var = tk.BooleanVar(value=True)
        self.selected_label_var = tk.StringVar(value="")
        self.button_widgets = {}
        self.system_input_widgets = {}
        self.system_output_widgets = {}
        self.joystick_widgets = []
        self.axis_widgets = []
        self.grid_views = {}
        self.layout_cards = {}
        self.logic_scroll_canvases = []
        self.joystick_card = None
        self.delete_duplicate_button = None
        self._build_ui()
        self._apply_pattern_if_empty()
        self.refresh()

    def current_mame_device_label(self):
        mapping = self.player_data.get("mame_device_mapping") or {}
        if not mapping.get("device"):
            mapping = {
                "joycode": "",
                "label": "No hardware mapping",
                "device": "",
            }
        return mame_device_choice_label(mapping)

    def mame_device_labels(self):
        labels = []
        for choice in self.mame_device_choices:
            label = mame_device_choice_label(choice)
            if label and label not in labels:
                labels.append(label)
        return labels

    def set_mame_device_choices(self, choices):
        self.mame_device_choices = list(choices or manual_mame_device_choices())
        labels = self.mame_device_labels()
        if hasattr(self, "mame_device_combo"):
            self.mame_device_combo.configure(values=labels)
        current = self.current_mame_device_label()
        if current in labels:
            self.mame_device_var.set(current)
        else:
            self.player_data["mame_device_mapping"] = {
                "joycode": "",
                "label": "No hardware mapping",
                "device": "",
            }
            self.mame_device_var.set("No hardware mapping")

    def selected_mame_device_choice(self):
        selected = self.mame_device_var.get().strip()
        for choice in self.mame_device_choices:
            if mame_device_choice_label(choice) == selected:
                return dict(choice)
        joycode = re.search(r"JOYCODE_(\d+)", selected.upper())
        if joycode:
            return {
                "joycode": f"JOYCODE_{joycode.group(1)}",
                "label": "",
                "device": "",
            }
        if selected == "No hardware mapping":
            return {
                "joycode": "",
                "label": "No hardware mapping",
                "device": "",
                "source": "manual",
            }
        return {}

    def on_mame_device_change(self, event=None):
        choice = self.selected_mame_device_choice()
        if not choice:
            return
        self.player_data["mame_device_mapping"] = choice
        if self.on_mame_device_select:
            self.on_mame_device_select(self, choice)
        if self.on_state_change:
            self.on_state_change(self)

    def probe_mame_devices(self):
        if self.on_mame_device_probe:
            self.on_mame_device_probe(self)

    def set_active_layout(self, layout_name, notify=False):
        if layout_name not in LAYOUTS:
            return
        self.active_layout = layout_name
        if notify and self.on_layout_select:
            self.on_layout_select(layout_name)
        if getattr(self, "layout_cards", None):
            self.refresh()

    def _build_ui(self):
        action = tk.Frame(self, bg="#ffffff")
        action.pack(fill="x", padx=6, pady=(6, 2))

        ttk.Label(action, text="Pattern").pack(side="left", padx=4)
        self.pattern_combo = ttk.Combobox(
            action,
            textvariable=self.pattern_var,
            values=PATTERN_CHOICES,
            width=18,
            state="readonly"
        )
        self.pattern_combo.pack(side="left", padx=4)

        ttk.Button(action, text="Auto pattern", command=self.apply_pattern).pack(side="left", padx=8)
        ttk.Button(action, text="Clear all", command=self.clear_all).pack(side="left", padx=4)
        ttk.Checkbutton(
            action,
            text="One slot per input",
            variable=self.single_slot_var,
        ).pack(side="left", padx=(14, 4))
        self.delete_duplicate_button = ttk.Button(
            action,
            text="Delete duplicate",
            command=self.delete_selected_duplicate_button,
            state="disabled",
        )
        self.delete_duplicate_button.pack(side="left", padx=4)
        ttk.Label(
            action,
            textvariable=self.selected_label_var,
            foreground="#1d4ed8"
        ).pack(side="left", padx=20)

        if self.player_data["player"] == 1:
            ttk.Checkbutton(
                action,
                text="Auto-config other players",
                variable=self.autocopy_var
            ).pack(side="left", padx=(20, 4))
            ttk.Button(
                action,
                text="Apply P1 to others",
                command=self.apply_to_others
            ).pack(side="left", padx=4)

        hardware = tk.Frame(self, bg="#ffffff")
        hardware.pack(fill="x", padx=6, pady=(0, 10))
        ttk.Label(hardware, text="MAME hardware").pack(side="left", padx=4)
        self.mame_device_combo = ttk.Combobox(
            hardware,
            textvariable=self.mame_device_var,
            values=self.mame_device_labels(),
            width=68,
            state="readonly",
        )
        self.mame_device_combo.pack(side="left", padx=4)
        self.mame_device_combo.bind("<<ComboboxSelected>>", self.on_mame_device_change)
        ttk.Button(hardware, text="Probe", command=self.probe_mame_devices).pack(side="left", padx=4)

        devices_box = ttk.LabelFrame(self, text="Devices / System Inputs")
        devices_box.pack(fill="x", padx=6, pady=6)

        dev_frame = ttk.Frame(devices_box)
        dev_frame.pack(fill="x", padx=4, pady=4)
        ttk.Label(dev_frame, text="Device", width=18).grid(row=0, column=0, padx=2, sticky="w")
        ttk.Label(dev_frame, text="Type", width=18).grid(row=0, column=1, padx=2, sticky="w")
        ttk.Label(dev_frame, text="Color", width=12).grid(row=0, column=2, padx=2, sticky="w")

        for i, dev in enumerate(self.player_data["devices"], start=1):
            dev["color"] = canonical_color_name(dev.get("color", ""))
            ttk.Label(dev_frame, text=dev["label"], width=18).grid(row=i, column=0, padx=2, pady=2, sticky="w")

            type_var = tk.StringVar(value=dev["type"])
            type_cmb = ttk.Combobox(dev_frame, textvariable=type_var, values=DEVICE_TYPE_CHOICES, width=18)
            type_cmb.grid(row=i, column=1, padx=2, pady=2, sticky="w")
            type_cmb.bind("<<ComboboxSelected>>", lambda e, dd=dev, vv=type_var: self.on_device_type_change(dd, vv))
            type_cmb.bind("<FocusOut>", lambda e, dd=dev, vv=type_var: self.on_device_type_change(dd, vv))

            color_var = tk.StringVar(value=canonical_color_name(dev.get("color", "")))
            color_cmb = ttk.Combobox(dev_frame, textvariable=color_var, values=COLOR_CHOICES, width=12)
            color_cmb.grid(row=i, column=2, padx=2, pady=2, sticky="w")
            color_cmb.bind("<<ComboboxSelected>>", lambda e, dd=dev, vv=color_var: self.on_device_color_change(dd, vv))
            color_cmb.bind("<FocusOut>", lambda e, dd=dev, vv=color_var: self.on_device_color_change(dd, vv))

        ttk.Separator(devices_box, orient="horizontal").pack(fill="x", padx=4, pady=6)

        self.system_input_rows_frame = ttk.Frame(devices_box)
        self.system_input_rows_frame.pack(fill="x", padx=4, pady=4)

        ttk.Separator(devices_box, orient="horizontal").pack(fill="x", padx=4, pady=6)

        outputs_scroll_host = ttk.Frame(devices_box)
        outputs_scroll_host.pack(fill="x", padx=4, pady=4)

        self.system_output_canvas = tk.Canvas(outputs_scroll_host, height=145, highlightthickness=0)
        self.system_output_canvas.pack(side="left", fill="x", expand=True)
        output_scroll_bar = ttk.Scrollbar(outputs_scroll_host, orient="vertical", command=self.system_output_canvas.yview)
        output_scroll_bar.pack(side="right", fill="y")
        self.system_output_canvas.configure(yscrollcommand=output_scroll_bar.set)

        self.system_output_rows_frame = ttk.Frame(self.system_output_canvas)
        self.system_output_window = self.system_output_canvas.create_window((0, 0), window=self.system_output_rows_frame, anchor="nw")
        self.system_output_rows_frame.bind("<Configure>", self.on_system_output_content_configure)
        self.system_output_canvas.bind("<Configure>", self.on_system_output_canvas_configure)
        self.system_output_canvas.bind("<Enter>", self.bind_system_output_mousewheel)
        self.system_output_canvas.bind("<Leave>", self.unbind_system_output_mousewheel)

        logic_tabs = ttk.Notebook(self)
        logic_tabs.pack(fill="x", padx=6, pady=6)

        buttons_tab = ttk.Frame(logic_tabs)
        logic_tabs.add(buttons_tab, text="Logical Buttons")

        self.buttons_rows_frame = self.create_scrollable_rows(buttons_tab, height=150)

        self.joystick_rows_frame = None
        if self.has_joystick_inputs():
            joystick_tab = ttk.Frame(logic_tabs)
            logic_tabs.add(joystick_tab, text="Logical Joystick")
            self.joystick_rows_frame = self.create_scrollable_rows(joystick_tab, height=150)

        self.axes_rows_frame = None
        if self.has_axes():
            axes_tab = ttk.Frame(logic_tabs)
            logic_tabs.add(axes_tab, text="Logical Axes")
            self.axes_rows_frame = self.create_scrollable_rows(axes_tab, height=150)

        panels_box = ttk.LabelFrame(self, text="Visual Panels")
        panels_box.pack(fill="both", expand=True, padx=6, pady=6)

        cards_row = tk.Frame(panels_box, bg="#ffffff")
        cards_row.pack(fill="x", padx=4, pady=(4, 2))

        cards_row.grid_columnconfigure(0, weight=1, uniform="panel_cards")
        self.joystick_card = Joystick8Card(
            cards_row,
            self.on_panel_joystick_click,
            self.on_panel_joystick_center_click,
            self.on_panel_joystick_right_click,
        )
        self.joystick_card.grid(row=0, column=0, padx=6, sticky="nsew")

        for i, layout_name in enumerate(LAYOUTS):
            col = i + 1
            cards_row.grid_columnconfigure(col, weight=1, uniform="panel_cards")
            card = CompactLayoutCard(cards_row, layout_name, self.on_slot_click, self.on_slot_right_click)
            card.grid(row=0, column=col, padx=6, sticky="nsew")
            self.layout_cards[layout_name] = card

        self._rebuild_button_rows()
        self._rebuild_system_input_rows()
        self._rebuild_system_output_rows()
        if self.joystick_rows_frame is not None:
            self._rebuild_joystick_rows()
        if self.axes_rows_frame is not None:
            self._rebuild_axis_rows()

    def create_scrollable_rows(self, parent, height=150):
        host = ttk.Frame(parent)
        host.pack(fill="x", padx=4, pady=(2, 4))
        canvas = tk.Canvas(host, height=height, highlightthickness=0)
        canvas.pack(side="left", fill="x", expand=True)
        scroll_bar = ttk.Scrollbar(host, orient="vertical", command=canvas.yview)
        scroll_bar.pack(side="right", fill="y")
        canvas.configure(yscrollcommand=scroll_bar.set)

        rows_frame = ttk.Frame(canvas)
        window = canvas.create_window((0, 0), window=rows_frame, anchor="nw")
        rows_frame.bind("<Configure>", lambda event, cc=canvas: cc.configure(scrollregion=cc.bbox("all")))
        canvas.bind("<Configure>", lambda event, cc=canvas, ww=window: cc.itemconfigure(ww, width=event.width))
        canvas.bind("<Enter>", lambda event, cc=canvas: self.bind_canvas_mousewheel(cc))
        canvas.bind("<Leave>", lambda event: self.unbind_canvas_mousewheel())
        self.logic_scroll_canvases.append(canvas)
        return rows_frame

    def bind_canvas_mousewheel(self, canvas):
        canvas.bind_all("<MouseWheel>", lambda event, cc=canvas: self.on_canvas_mousewheel(event, cc))

    def unbind_canvas_mousewheel(self):
        self.unbind_system_output_mousewheel()

    def on_canvas_mousewheel(self, event, canvas):
        canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    def on_system_output_content_configure(self, event=None):
        self.system_output_canvas.configure(scrollregion=self.system_output_canvas.bbox("all"))

    def on_system_output_canvas_configure(self, event):
        self.system_output_canvas.itemconfigure(self.system_output_window, width=event.width)

    def bind_system_output_mousewheel(self, event=None):
        self.system_output_canvas.bind_all("<MouseWheel>", self.on_system_output_mousewheel)

    def unbind_system_output_mousewheel(self, event=None):
        self.system_output_canvas.unbind_all("<MouseWheel>")

    def on_system_output_mousewheel(self, event):
        self.system_output_canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    def on_device_type_change(self, device, var):
        device["type"] = var.get().strip()

    def on_device_color_change(self, device, var):
        device["color"] = canonical_color_name(var.get())
        var.set(device["color"])
        self.refresh()

    def on_system_input_color_change(self, key, var):
        self.player_data["system_inputs"][key]["color"] = canonical_color_name(var.get())
        var.set(self.player_data["system_inputs"][key]["color"])
        self.refresh()

    def on_system_output_color_change(self, output, var):
        output["color"] = canonical_color_name(var.get(), "Gray")
        var.set(output["color"])
        self.refresh()

    def on_system_output_input_change(self, output, var):
        output["input_ref"] = var.get().strip()
        self.refresh()

    def system_input_order(self):
        preferred = [key for key in ("start", "select", "coin") if key in self.player_data["system_inputs"]]
        extras = [key for key in self.player_data["system_inputs"] if key not in preferred]
        return preferred + extras

    def _rebuild_system_input_rows(self):
        for w in self.system_input_rows_frame.winfo_children():
            w.destroy()
        self.system_input_widgets = {}

        colspecs = [
            ("COL", 4, "center"),
            ("Input", 26, "w"),
            ("Color", 10, "w"),
            ("Output", 12, "w"),
            ("2", 4, "center"),
            ("4", 4, "center"),
            ("6", 4, "center"),
            ("8", 4, "center"),
            ("RetroBat", 8, "w"),
            ("RetroPad", 8, "w"),
            ("Libretro / FBNeo", 16, "w"),
            ("MAME", 16, "w"),
            ("Tag", 14, "w"),
            ("Mask", 8, "w"),
        ]
        for col, (txt, width, anchor) in enumerate(colspecs):
            ttk.Label(self.system_input_rows_frame, text=txt, width=width, anchor=anchor).grid(row=0, column=col, padx=2, pady=(0, 4), sticky="w")

        for row_idx, key in enumerate(self.system_input_order(), start=1):
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            row_bg = "#ffffff"
            bubble = tk.Canvas(self.system_input_rows_frame, width=24, height=24, bg=row_bg, highlightthickness=0)
            bubble.grid(row=row_idx, column=0, padx=2, pady=3, sticky="w")

            label_widgets = []
            label = entry.get("label") or key.capitalize()
            lbl = tk.Label(
                self.system_input_rows_frame,
                text=f"{key.upper()} | {label}",
                bg=row_bg,
                fg="#101828",
                font=("Segoe UI", 10, "bold"),
                width=26,
                anchor="w",
            )
            lbl.grid(row=row_idx, column=1, padx=2, pady=3, sticky="w")
            label_widgets.append(lbl)

            color_var = tk.StringVar(value=canonical_color_name(entry.get("color", "")))
            color_cmb = ttk.Combobox(self.system_input_rows_frame, textvariable=color_var, values=COLOR_CHOICES, width=10)
            color_cmb.grid(row=row_idx, column=2, padx=2, pady=2, sticky="w")
            color_cmb.bind("<<ComboboxSelected>>", lambda e, kk=key, vv=color_var: self.on_system_input_color_change(kk, vv))
            color_cmb.bind("<FocusOut>", lambda e, kk=key, vv=color_var: self.on_system_input_color_change(kk, vv))

            output_var = tk.StringVar(value=entry.get("output", ""))
            output_cmb = ttk.Combobox(
                self.system_input_rows_frame,
                textvariable=output_var,
                values=self.player_data.get("output_choices", [""]),
                width=18,
            )
            output_cmb.grid(row=row_idx, column=3, padx=2, pady=2, sticky="w")
            output_cmb.bind("<<ComboboxSelected>>", lambda e, ee=entry, vv=output_var: self.on_output_change(ee, vv))
            output_cmb.bind("<FocusOut>", lambda e, ee=entry, vv=output_var: self.on_output_change(ee, vv))

            slot_labels = {}
            for col_offset, layout_name in enumerate(LAYOUTS, start=4):
                sl = tk.Label(
                    self.system_input_rows_frame,
                    text=self.slot_text(entry["slots_by_layout"].get(layout_name)),
                    bg=row_bg,
                    fg="#2459d3",
                    width=4,
                    font=("Segoe UI", 10, "bold"),
                    anchor="center",
                )
                sl.grid(row=row_idx, column=col_offset, padx=2, pady=3)
                slot_labels[layout_name] = sl
                label_widgets.append(sl)

            rb_var = tk.StringVar(value="")
            rp_var = tk.StringVar(value="")
            core_var = tk.StringVar(value="")
            tk.Label(self.system_input_rows_frame, textvariable=rb_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=8, padx=2, pady=3, sticky="w")
            tk.Label(self.system_input_rows_frame, textvariable=rp_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=9, padx=2, pady=3, sticky="w")
            tk.Label(self.system_input_rows_frame, textvariable=core_var, bg=row_bg, fg="#2459d3", width=16, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=10, padx=2, pady=3, sticky="w")

            mame = entry.get("mame", {})
            mame_label = tk.Label(self.system_input_rows_frame, text=mame.get("type", ""), bg=row_bg, fg="#101828", width=16, anchor="w")
            mame_label.grid(row=row_idx, column=11, padx=2, pady=3, sticky="w")
            label_widgets.append(mame_label)
            tag_label = tk.Label(self.system_input_rows_frame, text=mame.get("tag", ""), bg=row_bg, fg="#667085", width=14, anchor="w")
            tag_label.grid(row=row_idx, column=12, padx=2, pady=3, sticky="w")
            label_widgets.append(tag_label)
            mask_label = tk.Label(self.system_input_rows_frame, text=mame.get("mask_hex", ""), bg=row_bg, fg="#101828", width=8, anchor="w", font=("Consolas", 9))
            mask_label.grid(row=row_idx, column=13, padx=2, pady=3, sticky="w")
            label_widgets.append(mask_label)

            def _sel(evt=None, kk=key, ee=entry):
                self.select_system_input(kk, ee)

            bubble.bind("<Button-1>", _sel)
            lbl.bind("<Button-1>", _sel)
            for widget in label_widgets:
                widget.bind("<Button-1>", _sel)

            self.system_input_widgets[key] = {
                "entry": entry,
                "bubble": bubble,
                "label_widgets": label_widgets,
                "slot_labels": slot_labels,
                "rb_var": rb_var,
                "rp_var": rp_var,
                "core_var": core_var,
            }

    def system_output_key(self, output):
        return output.get("id") or output.get("name") or str(id(output))

    def ensure_system_output_entry(self, output):
        output.setdefault("id", output.get("name") or str(id(output)))
        output.setdefault("label", output.get("function") or output.get("name") or output.get("id"))
        output.setdefault("color", "Gray")
        output["color"] = canonical_color_name(output.get("color", "Gray"), "Gray")
        output.setdefault("input_ref", "")
        output.setdefault("panel_joystick", None)
        output.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
        for layout_name in LAYOUTS:
            output["slots_by_layout"].setdefault(layout_name, None)

    def system_output_short_label(self, output):
        text = output.get("function") or output.get("label") or output.get("name") or output.get("id", "")
        parts = re.split(r"[_\s]+", str(text).strip())
        if len(parts) > 1:
            return "".join(p[:1] for p in parts if p)[:3].upper()
        return str(text)[:3].upper()

    def system_output_input_choices(self):
        choices = [""]
        for b in self.player_data["buttons"]:
            prefix = f'B{b["game_button"]}'
            if b.get("instance_id"):
                prefix = f'{prefix} {b["instance_id"]}'
            label = f'{prefix} | {b.get("function", "")}'
            choices.append(label)
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            label = entry.get("label") or key.capitalize()
            choices.append(f"SYS {key.upper()} | {label}")
        return choices

    def mame_for_system_output_input(self, output):
        ref = (output.get("input_ref") or "").strip()
        if not ref:
            return {}
        if ref.upper().startswith("SYS "):
            key = ref[4:].split("|", 1)[0].strip().lower()
            entry = self.player_data.get("system_inputs", {}).get(key, {})
            return entry.get("mame", {})
        match = re.match(r"B(\d+)(?:\s+([^\|]+))?", ref, re.IGNORECASE)
        if match:
            game_button = int(match.group(1))
            instance_id = (match.group(2) or "").strip()
            for button in self.player_data["buttons"]:
                if button.get("game_button") != game_button:
                    continue
                if instance_id and button.get("instance_id") != instance_id:
                    continue
                return button.get("mame", {})
        return {}

    def _rebuild_system_output_rows(self):
        for w in self.system_output_rows_frame.winfo_children():
            w.destroy()
        self.system_output_widgets = {}

        outputs = sorted(self.player_data.get("system_outputs", []), key=output_sort_key)
        title = f"System Outputs ({len(outputs)})"
        ttk.Label(self.system_output_rows_frame, text=title, width=26, anchor="w").grid(row=0, column=0, columnspan=2, padx=2, pady=(0, 4), sticky="w")

        colspecs = [
            ("COL", 4, "center"),
            ("Output", 12, "w"),
            ("Function", 24, "w"),
            ("Label / Comment", 24, "w"),
            ("Color", 10, "w"),
            ("Input", 26, "w"),
            ("2", 4, "center"),
            ("4", 4, "center"),
            ("6", 4, "center"),
            ("8", 4, "center"),
            ("RetroBat", 8, "w"),
            ("RetroPad", 8, "w"),
            ("Libretro / FBNeo", 16, "w"),
            ("MAME", 16, "w"),
            ("Tag", 14, "w"),
            ("Mask", 8, "w"),
        ]
        for col, (txt, width, anchor) in enumerate(colspecs):
            ttk.Label(self.system_output_rows_frame, text=txt, width=width, anchor=anchor).grid(row=1, column=col, padx=2, pady=(0, 4), sticky="w")

        input_choices = self.system_output_input_choices()
        for row_idx, output in enumerate(outputs, start=2):
            self.ensure_system_output_entry(output)
            row_bg = "#ffffff"
            output_key = self.system_output_key(output)
            bubble = tk.Canvas(self.system_output_rows_frame, width=24, height=24, bg=row_bg, highlightthickness=0)
            bubble.grid(row=row_idx, column=0, padx=2, pady=3, sticky="w")

            label_widgets = []
            output_label_text = output.get("name") or output.get("id", "")
            output_label = tk.Label(
                self.system_output_rows_frame,
                text=output_label_text,
                bg=row_bg,
                fg="#101828",
                font=("Segoe UI", 10, "bold"),
                width=12,
                anchor="w",
            )
            output_label.grid(row=row_idx, column=1, padx=2, pady=3, sticky="w")
            label_widgets.append(output_label)

            function_label = tk.Label(
                self.system_output_rows_frame,
                text=output.get("function", ""),
                bg=row_bg,
                fg="#101828",
                width=24,
                anchor="w",
            )
            function_label.grid(row=row_idx, column=2, padx=2, pady=3, sticky="w")
            label_widgets.append(function_label)

            comment_text = output.get("comment") or output.get("label", "")
            comment_label = tk.Label(
                self.system_output_rows_frame,
                text=comment_text,
                bg=row_bg,
                fg="#667085",
                width=24,
                anchor="w",
            )
            comment_label.grid(row=row_idx, column=3, padx=2, pady=3, sticky="w")
            label_widgets.append(comment_label)

            color_var = tk.StringVar(value=canonical_color_name(output.get("color", "Gray"), "Gray"))
            color_cmb = ttk.Combobox(self.system_output_rows_frame, textvariable=color_var, values=COLOR_CHOICES, width=10)
            color_cmb.grid(row=row_idx, column=4, padx=2, pady=2, sticky="w")
            color_cmb.bind("<<ComboboxSelected>>", lambda e, oo=output, vv=color_var: self.on_system_output_color_change(oo, vv))
            color_cmb.bind("<FocusOut>", lambda e, oo=output, vv=color_var: self.on_system_output_color_change(oo, vv))

            input_var = tk.StringVar(value=output.get("input_ref", ""))
            input_cmb = ttk.Combobox(self.system_output_rows_frame, textvariable=input_var, values=input_choices, width=26)
            input_cmb.grid(row=row_idx, column=5, padx=2, pady=2, sticky="w")
            input_cmb.bind("<<ComboboxSelected>>", lambda e, oo=output, vv=input_var: self.on_system_output_input_change(oo, vv))
            input_cmb.bind("<FocusOut>", lambda e, oo=output, vv=input_var: self.on_system_output_input_change(oo, vv))

            slot_labels = {}
            for col_offset, layout_name in enumerate(LAYOUTS, start=6):
                sl = tk.Label(
                    self.system_output_rows_frame,
                    text=self.slot_text(output["slots_by_layout"].get(layout_name)),
                    bg=row_bg,
                    fg="#2459d3",
                    width=4,
                    font=("Segoe UI", 10, "bold"),
                    anchor="center",
                )
                sl.grid(row=row_idx, column=col_offset, padx=2, pady=3)
                slot_labels[layout_name] = sl
                label_widgets.append(sl)

            rb_var = tk.StringVar(value="")
            rp_var = tk.StringVar(value="")
            core_var = tk.StringVar(value="")
            rb_label = tk.Label(self.system_output_rows_frame, textvariable=rb_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w")
            rb_label.grid(row=row_idx, column=10, padx=2, pady=3, sticky="w")
            label_widgets.append(rb_label)
            rp_label = tk.Label(self.system_output_rows_frame, textvariable=rp_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w")
            rp_label.grid(row=row_idx, column=11, padx=2, pady=3, sticky="w")
            label_widgets.append(rp_label)
            core_label = tk.Label(self.system_output_rows_frame, textvariable=core_var, bg=row_bg, fg="#2459d3", width=16, font=("Segoe UI", 10, "bold"), anchor="w")
            core_label.grid(row=row_idx, column=12, padx=2, pady=3, sticky="w")
            label_widgets.append(core_label)

            mame = self.mame_for_system_output_input(output)
            mame_var = tk.StringVar(value=mame.get("type") or mame.get("input_id") or "")
            tag_var = tk.StringVar(value=mame.get("tag", ""))
            mask_var = tk.StringVar(value=mame.get("mask_hex", ""))
            mame_label = tk.Label(self.system_output_rows_frame, textvariable=mame_var, bg=row_bg, fg="#101828", width=16, anchor="w")
            mame_label.grid(row=row_idx, column=13, padx=2, pady=3, sticky="w")
            label_widgets.append(mame_label)
            tag_label = tk.Label(self.system_output_rows_frame, textvariable=tag_var, bg=row_bg, fg="#667085", width=14, anchor="w")
            tag_label.grid(row=row_idx, column=14, padx=2, pady=3, sticky="w")
            label_widgets.append(tag_label)
            mask_label = tk.Label(self.system_output_rows_frame, textvariable=mask_var, bg=row_bg, fg="#101828", width=8, anchor="w", font=("Consolas", 9))
            mask_label.grid(row=row_idx, column=15, padx=2, pady=3, sticky="w")
            label_widgets.append(mask_label)

            def _sel(evt=None, oo=output):
                self.select_system_output(oo)

            bubble.bind("<Button-1>", _sel)
            for widget in label_widgets:
                widget.bind("<Button-1>", _sel)

            self.system_output_widgets[output_key] = {
                "entry": output,
                "bubble": bubble,
                "label_widgets": label_widgets,
                "slot_labels": slot_labels,
                "rb_var": rb_var,
                "rp_var": rp_var,
                "core_var": core_var,
                "mame_var": mame_var,
                "tag_var": tag_var,
                "mask_var": mask_var,
            }

    def _rebuild_button_rows(self):
        for w in self.buttons_rows_frame.winfo_children():
            w.destroy()
        self.button_widgets = {}

        colspecs = [
            ("COL", 4, "center"),
            ("Input", 26, "w"),
            ("Color", 10, "w"),
            ("Output", 18, "w"),
            ("2", 4, "center"),
            ("4", 4, "center"),
            ("6", 4, "center"),
            ("8", 4, "center"),
            ("RetroBat", 8, "w"),
            ("RetroPad", 8, "w"),
            ("Libretro / FBNeo", 16, "w"),
            ("MAME", 16, "w"),
            ("Tag", 14, "w"),
            ("Mask", 8, "w"),
        ]
        for col, (txt, width, anchor) in enumerate(colspecs):
            ttk.Label(
                self.buttons_rows_frame,
                text=txt,
                width=width,
                anchor=anchor,
            ).grid(row=0, column=col, padx=2, pady=(0, 4), sticky="w")

        for row_idx, b in enumerate(self.player_data["buttons"]):
            grid_row = row_idx + 1
            row_bg = "#ffffff"
            b["color"] = canonical_color_name(b.get("color", ""))
            bubble = tk.Canvas(self.buttons_rows_frame, width=24, height=24, bg=row_bg, highlightthickness=0)
            bubble.grid(row=grid_row, column=0, padx=2, pady=3, sticky="w")

            label_prefix = f'B{b["game_button"]}'
            if b.get("instance_id"):
                label_prefix = f'{label_prefix} {b["instance_id"]}'
            lbl = tk.Label(
                self.buttons_rows_frame,
                text=f'{label_prefix} | {b["function"]}',
                bg=row_bg,
                fg="#101828",
                font=("Segoe UI", 10, "bold"),
                width=26,
                anchor="w",
            )
            lbl.grid(row=grid_row, column=1, padx=2, pady=3, sticky="w")

            color_var = tk.StringVar(value=canonical_color_name(b["color"]))
            cmb = ttk.Combobox(self.buttons_rows_frame, textvariable=color_var, values=COLOR_CHOICES, width=10)
            cmb.grid(row=grid_row, column=2, padx=2, pady=2, sticky="w")
            cmb.bind("<<ComboboxSelected>>", lambda e, bb=b, vv=color_var: self.on_color_change(bb, vv))
            cmb.bind("<FocusOut>", lambda e, bb=b, vv=color_var: self.on_color_change(bb, vv))

            output_var = tk.StringVar(value=b.get("output", ""))
            output_cmb = ttk.Combobox(
                self.buttons_rows_frame,
                textvariable=output_var,
                values=self.player_data.get("output_choices", [""]),
                width=18,
            )
            output_cmb.grid(row=grid_row, column=3, padx=2, pady=2, sticky="w")
            output_cmb.bind("<<ComboboxSelected>>", lambda e, bb=b, vv=output_var: self.on_output_change(bb, vv))
            output_cmb.bind("<FocusOut>", lambda e, bb=b, vv=output_var: self.on_output_change(bb, vv))

            slot_labels = {}
            col = 4
            for layout_name in LAYOUTS:
                sl = tk.Label(
                    self.buttons_rows_frame,
                    text=self.slot_text(b["slots_by_layout"][layout_name]),
                    bg=row_bg,
                    fg="#2459d3",
                    width=4,
                    font=("Segoe UI", 10, "bold"),
                    anchor="center",
                )
                sl.grid(row=grid_row, column=col, padx=2, pady=3)
                slot_labels[layout_name] = sl
                col += 1

            rb_var = tk.StringVar(value="")
            rp_var = tk.StringVar(value="")
            core_var = tk.StringVar(value="")

            tk.Label(self.buttons_rows_frame, textvariable=rb_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=grid_row, column=8, padx=2, pady=3, sticky="w")
            tk.Label(self.buttons_rows_frame, textvariable=rp_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=grid_row, column=9, padx=2, pady=3, sticky="w")
            tk.Label(self.buttons_rows_frame, textvariable=core_var, bg=row_bg, fg="#2459d3", width=16, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=grid_row, column=10, padx=2, pady=3, sticky="w")
            tk.Label(self.buttons_rows_frame, text=b["mame"].get("type", ""), bg=row_bg, fg="#101828", width=16, anchor="w").grid(row=grid_row, column=11, padx=2, pady=3, sticky="w")
            tk.Label(self.buttons_rows_frame, text=b["mame"].get("tag", ""), bg=row_bg, fg="#667085", width=14, anchor="w").grid(row=grid_row, column=12, padx=2, pady=3, sticky="w")
            tk.Label(self.buttons_rows_frame, text=b["mame"].get("mask_hex", ""), bg=row_bg, fg="#101828", width=8, anchor="w", font=("Consolas", 9)).grid(row=grid_row, column=13, padx=2, pady=3, sticky="w")

            def _sel(evt=None, button=b):
                self.select_button(button)

            bubble.bind("<Button-1>", _sel)
            lbl.bind("<Button-1>", _sel)

            self.button_widgets[self.button_row_key(b)] = {
                "bubble": bubble,
                "lbl": lbl,
                "row_bg": row_bg,
                "slot_labels": slot_labels,
                "rb_var": rb_var,
                "rp_var": rp_var,
                "core_var": core_var,
            }

    def iter_joystick_inputs(self):
        for dev in self.player_data["devices"]:
            for direction in ("up", "down", "left", "right"):
                entry = dev.get("inputs", {}).get(direction)
                if not entry:
                    continue
                entry.setdefault("panel_joystick", None)
                if "slots_by_layout" not in entry:
                    entry["slots_by_layout"] = {layout: None for layout in LAYOUTS}
                if is_specific_joystick_input(entry):
                    yield dev, direction, entry

    def has_joystick_inputs(self):
        return any(True for _ in self.iter_joystick_inputs())

    def iter_assignable_items(self):
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            yield "system_input", key, entry
        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            yield "system_output", self.system_output_key(output), output
        for b in self.player_data["buttons"]:
            b.setdefault("panel_joystick", None)
            yield "button", str(b["game_button"]), b
        for _, direction, entry in self.iter_joystick_inputs():
            yield "joystick", direction, entry
        for axis in self.iter_axes():
            yield "axis", axis.get("id", ""), axis

    def iter_axes(self):
        for axis in self.player_data.get("axes", []):
            if not isinstance(axis, dict):
                continue
            axis.setdefault("panel_joystick", None)
            if "slots_by_layout" not in axis:
                axis["slots_by_layout"] = {layout: None for layout in LAYOUTS}
            if "slots_by_polarity" not in axis:
                axis["slots_by_polarity"] = {
                    "negative": {layout: None for layout in LAYOUTS},
                    "positive": {layout: axis["slots_by_layout"].get(layout) for layout in LAYOUTS},
                }
            else:
                for polarity in ("negative", "positive"):
                    axis["slots_by_polarity"].setdefault(polarity, {})
                    for layout in LAYOUTS:
                        axis["slots_by_polarity"][polarity].setdefault(layout, None)
            axis.setdefault("joystick", {"negative": "", "positive": ""})
            yield axis

    def has_axes(self):
        return any(True for _ in self.iter_axes())

    def joystick_choice_label(self, direction):
        if not direction:
            return ""
        for _, candidate_direction, entry in self.iter_joystick_inputs():
            if candidate_direction != direction:
                continue
            mame = entry.get("mame", {})
            parts = [
                direction.upper(),
                mame.get("type") or mame.get("input_id") or "",
                mame.get("tag", ""),
                mame.get("mask_hex", ""),
            ]
            return " | ".join([p for p in parts if p])
        return direction.upper()

    def joystick_choices_for_axes(self):
        choices = [""]
        for _, direction, entry in self.iter_joystick_inputs():
            mame = entry.get("mame", {})
            parts = [
                direction.upper(),
                mame.get("type") or mame.get("input_id") or "",
                mame.get("tag", ""),
                mame.get("mask_hex", ""),
            ]
            choices.append(" | ".join([p for p in parts if p]))
        return choices

    def direction_from_joystick_choice(self, value):
        head = (value or "").split("|", 1)[0].strip().lower()
        return head if head in JOYSTICK_DIRECTION_MAP else ""

    def selected_axis_joystick_mame(self, axis, polarity):
        direction = axis.get("joystick", {}).get(polarity, "")
        if not direction:
            return {}
        for _, candidate_direction, entry in self.iter_joystick_inputs():
            if candidate_direction == direction:
                return entry.get("mame", {})
        return {}

    def _rebuild_joystick_rows(self):
        if self.joystick_rows_frame is None:
            return
        for w in self.joystick_rows_frame.winfo_children():
            w.destroy()
        self.joystick_widgets = []

        colspecs = [
            ("COL", 4, "center"),
            ("Input", 26, "w"),
            ("Color", 10, "w"),
            ("Output", 18, "w"),
            ("2", 4, "center"),
            ("4", 4, "center"),
            ("6", 4, "center"),
            ("8", 4, "center"),
            ("RetroBat", 8, "w"),
            ("RetroPad", 8, "w"),
            ("Libretro / FBNeo", 16, "w"),
            ("MAME", 16, "w"),
            ("Tag", 14, "w"),
            ("Mask", 8, "w"),
        ]
        for col, (txt, width, anchor) in enumerate(colspecs):
            ttk.Label(
                self.joystick_rows_frame,
                text=txt,
                width=width,
                anchor=anchor,
            ).grid(row=0, column=col, padx=2, pady=(0, 4), sticky="w")

        rows = list(self.iter_joystick_inputs())
        if not rows:
            ttk.Label(
                self.joystick_rows_frame,
                text="No usable joystick input for this player.",
            ).grid(row=1, column=0, columnspan=len(colspecs), padx=2, pady=6, sticky="w")
            return

        for row_idx, (dev, direction, entry) in enumerate(rows, start=1):
            row_bg = "#ffffff"
            entry["color"] = canonical_color_name(entry.get("color", dev.get("color", "")))
            bubble = tk.Canvas(self.joystick_rows_frame, width=24, height=24, bg=row_bg, highlightthickness=0)
            bubble.grid(row=row_idx, column=0, padx=2, pady=3, sticky="w")

            label_widgets = []
            lbl = tk.Label(
                self.joystick_rows_frame,
                text=f'{direction.upper()} | {entry.get("function") or dev.get("label", "")}',
                bg=row_bg,
                fg="#101828",
                font=("Segoe UI", 10, "bold"),
                width=26,
                anchor="w",
            )
            lbl.grid(row=row_idx, column=1, padx=2, pady=3, sticky="w")
            label_widgets.append(lbl)

            color_var = tk.StringVar(value=canonical_color_name(entry.get("color", dev.get("color", ""))))
            color_cmb = ttk.Combobox(self.joystick_rows_frame, textvariable=color_var, values=COLOR_CHOICES, width=10)
            color_cmb.grid(row=row_idx, column=2, padx=2, pady=2, sticky="w")
            color_cmb.bind("<<ComboboxSelected>>", lambda e, ee=entry, vv=color_var: self.on_joystick_input_color_change(ee, vv))
            color_cmb.bind("<FocusOut>", lambda e, ee=entry, vv=color_var: self.on_joystick_input_color_change(ee, vv))

            output_var = tk.StringVar(value=entry.get("output", ""))
            output_cmb = ttk.Combobox(
                self.joystick_rows_frame,
                textvariable=output_var,
                values=self.player_data.get("output_choices", [""]),
                width=18,
            )
            output_cmb.grid(row=row_idx, column=3, padx=2, pady=2, sticky="w")
            output_cmb.bind("<<ComboboxSelected>>", lambda e, ee=entry, vv=output_var: self.on_output_change(ee, vv))
            output_cmb.bind("<FocusOut>", lambda e, ee=entry, vv=output_var: self.on_output_change(ee, vv))

            slot_labels = {}
            for col_offset, layout_name in enumerate(LAYOUTS, start=4):
                sl = tk.Label(
                    self.joystick_rows_frame,
                    text=self.slot_text(entry["slots_by_layout"].get(layout_name)),
                    bg=row_bg,
                    fg="#2459d3",
                    width=4,
                    font=("Segoe UI", 10, "bold"),
                    anchor="center",
                )
                sl.grid(row=row_idx, column=col_offset, padx=2, pady=3)
                slot_labels[layout_name] = sl
                label_widgets.append(sl)

            rb_var = tk.StringVar(value="")
            rp_var = tk.StringVar(value="")
            core_var = tk.StringVar(value="")

            rb_label = tk.Label(self.joystick_rows_frame, textvariable=rb_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w")
            rb_label.grid(row=row_idx, column=8, padx=2, pady=3, sticky="w")
            label_widgets.append(rb_label)
            rp_label = tk.Label(self.joystick_rows_frame, textvariable=rp_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w")
            rp_label.grid(row=row_idx, column=9, padx=2, pady=3, sticky="w")
            label_widgets.append(rp_label)
            core_label = tk.Label(self.joystick_rows_frame, textvariable=core_var, bg=row_bg, fg="#2459d3", width=16, font=("Segoe UI", 10, "bold"), anchor="w")
            core_label.grid(row=row_idx, column=10, padx=2, pady=3, sticky="w")
            label_widgets.append(core_label)

            mame = entry.get("mame", {})
            mame_label = tk.Label(self.joystick_rows_frame, text=mame.get("type") or mame.get("input_id") or "", bg=row_bg, fg="#101828", width=16, anchor="w")
            mame_label.grid(row=row_idx, column=11, padx=2, pady=3, sticky="w")
            label_widgets.append(mame_label)
            tag_label = tk.Label(self.joystick_rows_frame, text=mame.get("tag", ""), bg=row_bg, fg="#667085", width=14, anchor="w")
            tag_label.grid(row=row_idx, column=12, padx=2, pady=3, sticky="w")
            label_widgets.append(tag_label)
            mask_label = tk.Label(self.joystick_rows_frame, text=mame.get("mask_hex", ""), bg=row_bg, fg="#101828", width=8, anchor="w", font=("Consolas", 9))
            mask_label.grid(row=row_idx, column=13, padx=2, pady=3, sticky="w")
            label_widgets.append(mask_label)

            def _sel(evt=None, d=dev, dd=direction, ee=entry):
                self.select_joystick(d, dd, ee)

            bubble.bind("<Button-1>", _sel)
            for widget in label_widgets:
                widget.bind("<Button-1>", _sel)

            self.joystick_widgets.append({
                "dev": dev,
                "direction": direction,
                "entry": entry,
                "bubble": bubble,
                "label_widgets": label_widgets,
                "slot_labels": slot_labels,
                "rb_var": rb_var,
                "rp_var": rp_var,
                "core_var": core_var,
            })

    def _rebuild_axis_rows(self):
        if self.axes_rows_frame is None:
            return
        for w in self.axes_rows_frame.winfo_children():
            w.destroy()
        self.axis_widgets = []

        colspecs = [
            ("COL", 4, "center"),
            ("Input", 26, "w"),
            ("Color", 10, "w"),
            ("Output", 18, "w"),
            ("2", 4, "center"),
            ("4", 4, "center"),
            ("6", 4, "center"),
            ("8", 4, "center"),
            ("RetroBat", 8, "w"),
            ("RetroPad", 8, "w"),
            ("Libretro / FBNeo", 16, "w"),
            ("MAME", 16, "w"),
            ("Tag", 14, "w"),
            ("Mask", 8, "w"),
        ]
        for col, (txt, width, anchor) in enumerate(colspecs):
            ttk.Label(self.axes_rows_frame, text=txt, width=width, anchor=anchor).grid(row=0, column=col, padx=2, pady=(0, 4), sticky="w")

        for row_idx, axis in enumerate(self.iter_axes(), start=1):
            row_bg = "#ffffff"
            label_widgets = []
            axis["color"] = canonical_color_name(axis.get("color", "Gray"), "Gray")

            bubble = tk.Canvas(self.axes_rows_frame, width=24, height=24, bg=row_bg, highlightthickness=0)
            bubble.grid(row=row_idx, column=0, padx=2, pady=3, sticky="w")

            label_text = axis.get("label") or axis.get("input") or axis.get("id")
            short = axis.get("short") or analog_short_label(axis.get("label"), axis.get("input"))
            short_label = tk.Label(self.axes_rows_frame, text=f"{short} | {label_text}", bg=row_bg, fg="#101828", font=("Segoe UI", 10, "bold"), width=26, anchor="w")
            short_label.grid(row=row_idx, column=1, padx=2, pady=3, sticky="w")
            label_widgets.append(short_label)

            color_var = tk.StringVar(value=canonical_color_name(axis.get("color", "Gray"), "Gray"))
            color_cmb = ttk.Combobox(self.axes_rows_frame, textvariable=color_var, values=COLOR_CHOICES, width=10)
            color_cmb.grid(row=row_idx, column=2, padx=2, pady=2, sticky="w")
            color_cmb.bind("<<ComboboxSelected>>", lambda e, aa=axis, vv=color_var: self.on_axis_color_change(aa, vv))
            color_cmb.bind("<FocusOut>", lambda e, aa=axis, vv=color_var: self.on_axis_color_change(aa, vv))

            output_var = tk.StringVar(value=axis.get("output", ""))
            output_cmb = ttk.Combobox(
                self.axes_rows_frame,
                textvariable=output_var,
                values=self.player_data.get("output_choices", [""]),
                width=18,
            )
            output_cmb.grid(row=row_idx, column=3, padx=2, pady=2, sticky="w")
            output_cmb.bind("<<ComboboxSelected>>", lambda e, aa=axis, vv=output_var: self.on_output_change(aa, vv))
            output_cmb.bind("<FocusOut>", lambda e, aa=axis, vv=output_var: self.on_output_change(aa, vv))

            slot_labels = {}
            for col_offset, layout_name in enumerate(LAYOUTS, start=4):
                sl = tk.Label(
                    self.axes_rows_frame,
                    text=self.slot_text(axis["slots_by_layout"].get(layout_name)),
                    bg=row_bg,
                    fg="#2459d3",
                    width=4,
                    font=("Segoe UI", 10, "bold"),
                    anchor="center",
                )
                sl.grid(row=row_idx, column=col_offset, padx=2, pady=3)
                slot_labels[layout_name] = sl
                label_widgets.append(sl)

            mame = axis.get("mame", {})
            rb_var = tk.StringVar(value="")
            rp_var = tk.StringVar(value="")
            core_var = tk.StringVar(value="")
            tk.Label(self.axes_rows_frame, textvariable=rb_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=8, padx=2, pady=3, sticky="w")
            tk.Label(self.axes_rows_frame, textvariable=rp_var, bg=row_bg, fg="#2459d3", width=8, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=9, padx=2, pady=3, sticky="w")
            tk.Label(self.axes_rows_frame, textvariable=core_var, bg=row_bg, fg="#2459d3", width=16, font=("Segoe UI", 10, "bold"), anchor="w").grid(row=row_idx, column=10, padx=2, pady=3, sticky="w")

            mame_label = tk.Label(self.axes_rows_frame, text=mame.get("type") or mame.get("input_id") or axis.get("id", ""), bg=row_bg, fg="#101828", width=16, anchor="w")
            mame_label.grid(row=row_idx, column=11, padx=2, pady=3, sticky="w")
            label_widgets.append(mame_label)
            tag_label = tk.Label(self.axes_rows_frame, text=mame.get("tag", ""), bg=row_bg, fg="#667085", width=14, anchor="w")
            tag_label.grid(row=row_idx, column=12, padx=2, pady=3, sticky="w")
            label_widgets.append(tag_label)
            mask_label = tk.Label(self.axes_rows_frame, text=mame.get("mask_hex", ""), bg=row_bg, fg="#101828", width=8, anchor="w", font=("Consolas", 9))
            mask_label.grid(row=row_idx, column=13, padx=2, pady=3, sticky="w")
            label_widgets.append(mask_label)

            def _sel(evt=None, aa=axis):
                self.select_axis(aa)

            bubble.bind("<Button-1>", _sel)
            for widget in label_widgets:
                widget.bind("<Button-1>", _sel)

            self.axis_widgets.append({
                "axis": axis,
                "bubble": bubble,
                "label_widgets": label_widgets,
                "slot_labels": slot_labels,
                "rb_var": rb_var,
                "rp_var": rp_var,
                "core_var": core_var,
            })

    def slot_values(self, slot_value):
        if slot_value is None:
            return []
        if isinstance(slot_value, (list, tuple, set)):
            return [slot for slot in slot_value if slot is not None]
        return [slot_value]

    def slot_text(self, slot_value):
        slots = self.slot_values(slot_value)
        if not slots:
            return "-"
        return ",".join(str(slot) for slot in slots)

    def slot_export_payload(self, slot_value):
        slots = self.slot_values(slot_value)
        if not slots:
            return None
        if len(slots) == 1:
            return {"panel_slot": slots[0]}
        return {"panel_slots": slots}

    def first_slot_mapping(self, slot_value):
        slots = self.slot_values(slot_value)
        if not slots:
            return None
        return SLOT_MAP.get(str(slots[0]))

    def system_input_default_mapping(self, key):
        return SYSTEM_SLOT_MAP.get(key, {})

    def ensure_system_input_entry(self, key, entry):
        entry.setdefault("label", key.capitalize())
        entry.setdefault("color", "")
        entry["color"] = canonical_color_name(entry.get("color", ""))
        entry.setdefault("output", "")
        entry.setdefault("panel_joystick", None)
        entry.setdefault("mame", {})
        entry.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
        for layout_name in LAYOUTS:
            entry["slots_by_layout"].setdefault(layout_name, None)

    def button_row_key(self, button):
        return button.get("instance_id") or str(button["game_button"])

    def next_button_instance_id(self, button):
        base = str(button["game_button"])
        used = {
            str(b.get("instance_id"))
            for b in self.player_data["buttons"]
            if b.get("instance_id")
        }
        idx = 2
        while f"{base}#{idx}" in used:
            idx += 1
        return f"{base}#{idx}"

    def duplicate_button_for_slot(self, button, layout_name, slot):
        clone = copy.deepcopy(button)
        clone["instance_id"] = self.next_button_instance_id(button)
        clone["duplicate_of"] = button.get("duplicate_of", button["game_button"])
        clone["slots_by_layout"] = {layout: None for layout in LAYOUTS}
        clone["slots_by_layout"][layout_name] = slot
        clone["panel_joystick"] = None
        idx = self.player_data["buttons"].index(button) + 1
        self.player_data["buttons"].insert(idx, clone)
        self.selected_button = clone
        self._rebuild_button_rows()
        return clone

    def button_allocation_text(self, button):
        parts = []
        for layout_name in LAYOUTS:
            text = self.slot_text(button["slots_by_layout"].get(layout_name))
            if text != "-":
                parts.append(f"{layout_name}:{text}")
        return ", ".join(parts) if parts else "none"

    def update_delete_duplicate_state(self):
        if not self.delete_duplicate_button:
            return
        state = "normal" if self.selected_button and self.selected_button.get("instance_id") else "disabled"
        self.delete_duplicate_button.configure(state=state)

    def delete_selected_duplicate_button(self):
        button = self.selected_button
        if not button or not button.get("instance_id"):
            return
        if button in self.player_data["buttons"]:
            self.player_data["buttons"].remove(button)
        self.selected_button = None
        self.selected_label_var.set("")
        self._rebuild_button_rows()
        self.refresh()
        self._auto_copy_if_needed()

    def on_color_change(self, button, var):
        button["color"] = canonical_color_name(var.get())
        var.set(button["color"])
        self.refresh()

    def on_output_change(self, button, var):
        button["output"] = var.get().strip()

    def on_axis_color_change(self, axis, var):
        axis["color"] = canonical_color_name(var.get(), "Gray")
        var.set(axis["color"])
        self.refresh()

    def on_joystick_input_color_change(self, entry, var):
        entry["color"] = canonical_color_name(var.get())
        var.set(entry["color"])
        self.refresh()

    def on_axis_joystick_change(self, axis, polarity, var):
        axis.setdefault("joystick", {"negative": "", "positive": ""})
        axis["joystick"][polarity] = self.direction_from_joystick_choice(var.get())
        axis["physical_joystick"] = axis["joystick"].get("positive", "")
        self.refresh()
        self._auto_copy_if_needed()

    def select_button(self, button):
        self.selected_button = button
        self.selected_system_input = None
        self.selected_system_output = None
        self.selected_joystick = None
        self.selected_axis = None
        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()
        if self.on_state_change:
            self.on_state_change(self)
        self.update_delete_duplicate_state()
        instance = f' {button["instance_id"]}' if button.get("instance_id") else ""
        self.selected_label_var.set(
            f'Selected button: B{button["game_button"]}{instance} {button["function"]} ({button["color"]}) | Allocated: {self.button_allocation_text(button)}'
        )

    def select_system_input(self, key, entry):
        self.selected_button = None
        self.selected_system_input = {
            "key": key,
            "entry": entry,
        }
        self.selected_system_output = None
        self.selected_joystick = None
        self.selected_axis = None
        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()
        self.update_delete_duplicate_state()
        self.selected_label_var.set(
            f'Selected system input: {key.capitalize()} | Allocated: {self.button_allocation_text(entry)}'
        )

    def select_system_output(self, output):
        self.selected_button = None
        self.selected_system_input = None
        self.selected_system_output = output
        self.selected_joystick = None
        self.selected_axis = None
        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()
        self.update_delete_duplicate_state()
        output_name = output.get("name") or output.get("id") or ""
        output_function = output.get("function") or output.get("label") or ""
        details = output_name
        if output_function and output_function != output_name:
            details = f"{output_name} ({output_function})"
        self.selected_label_var.set(
            f'Selected system output: {details} | Allocated: {self.button_allocation_text(output)}'
        )

    def select_joystick(self, dev, direction, entry):
        self.selected_button = None
        self.selected_system_input = None
        self.selected_system_output = None
        self.selected_axis = None
        self.selected_joystick = {
            "dev": dev,
            "direction": direction,
            "entry": entry,
        }
        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()
        self.update_delete_duplicate_state()
        self.selected_label_var.set(
            f'Selected joystick: {dev.get("label", "Device")} {direction.upper()}'
        )

    def select_axis(self, axis):
        self.selected_button = None
        self.selected_system_input = None
        self.selected_system_output = None
        self.selected_joystick = None
        self.selected_axis = axis
        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()
        self.update_delete_duplicate_state()
        self.selected_label_var.set(
            f'Selected axis: {axis.get("label") or axis.get("id")}'
        )

    def refresh_button_selection(self):
        for b in self.player_data["buttons"]:
            widgets = self.button_widgets[self.button_row_key(b)]
            selected = b is self.selected_button
            bg = "#eef4ff" if selected else "#ffffff"
            fg = "#2459d3" if selected else "#101828"
            widgets["lbl"].configure(bg=bg, fg=fg)
            widgets["bubble"].configure(bg=bg)
            for sl in widgets["slot_labels"].values():
                sl.configure(bg=bg)
            widgets["bubble"].configure(
                highlightthickness=2 if selected else 0,
                highlightbackground="#2563eb" if selected else "#ffffff",
            )

    def refresh_system_input_selection(self):
        selected_entry = self.selected_system_input["entry"] if self.selected_system_input else None
        for widgets in self.system_input_widgets.values():
            selected = widgets["entry"] is selected_entry
            bg = "#eef4ff" if selected else "#ffffff"
            fg = "#2459d3" if selected else "#101828"
            widgets["bubble"].configure(
                bg=bg,
                highlightthickness=2 if selected else 0,
                highlightbackground="#2563eb" if selected else "#ffffff",
            )
            for label in widgets["label_widgets"]:
                label.configure(bg=bg)
            for label in widgets["slot_labels"].values():
                label.configure(bg=bg)
            if widgets["label_widgets"]:
                widgets["label_widgets"][0].configure(fg=fg)

    def refresh_system_output_selection(self):
        selected_entry = self.selected_system_output
        for widgets in self.system_output_widgets.values():
            selected = widgets["entry"] is selected_entry
            bg = "#eef4ff" if selected else "#ffffff"
            fg = "#2459d3" if selected else "#101828"
            widgets["bubble"].configure(
                bg=bg,
                highlightthickness=2 if selected else 0,
                highlightbackground="#2563eb" if selected else "#ffffff",
            )
            for label in widgets["label_widgets"]:
                label.configure(bg=bg)
            for label in widgets["slot_labels"].values():
                label.configure(bg=bg)
            if widgets["label_widgets"]:
                widgets["label_widgets"][0].configure(fg=fg)

    def refresh_joystick_selection(self):
        selected_entry = self.selected_joystick["entry"] if self.selected_joystick else None
        for widgets in self.joystick_widgets:
            selected = widgets["entry"] is selected_entry
            bg = "#eef4ff" if selected else "#ffffff"
            fg = "#2459d3" if selected else "#101828"
            widgets["bubble"].configure(
                bg=bg,
                highlightthickness=2 if selected else 0,
                highlightbackground="#2563eb" if selected else "#ffffff",
            )
            for label in widgets["label_widgets"]:
                label.configure(bg=bg)
            for label in widgets["slot_labels"].values():
                label.configure(bg=bg)
            if len(widgets["label_widgets"]) > 1:
                widgets["label_widgets"][1].configure(fg=fg)

    def refresh_axis_selection(self):
        for widgets in self.axis_widgets:
            selected = widgets["axis"] is self.selected_axis
            bg = "#eef4ff" if selected else "#ffffff"
            fg = "#2459d3" if selected else "#101828"
            widgets["bubble"].configure(
                bg=bg,
                highlightthickness=2 if selected else 0,
                highlightbackground="#2563eb" if selected else "#ffffff",
            )
            for label in widgets["label_widgets"]:
                label.configure(bg=bg)
            for label in widgets["slot_labels"].values():
                label.configure(bg=bg)
            if widgets["label_widgets"]:
                widgets["label_widgets"][0].configure(fg=fg)

    def assigned_map_for_layout(self, layout_name):
        out = {}
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            slots = self.slot_values(entry["slots_by_layout"].get(layout_name))
            for slot in slots:
                out[slot] = {
                    "short": (entry.get("label") or key)[:3].upper(),
                    "color": entry.get("color", "Gray"),
                }
        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            slots = self.slot_values(output["slots_by_layout"].get(layout_name))
            for slot in slots:
                out[slot] = {
                    "short": self.system_output_short_label(output),
                    "color": output.get("color", "Gray"),
                }
        for b in self.player_data["buttons"]:
            slots = self.slot_values(b["slots_by_layout"].get(layout_name))
            for slot in slots:
                out[slot] = {
                    "short": normalize_mk_role(b["function"]) or b["logical_name"],
                    "color": b["color"],
                }
        for dev, direction, entry in self.iter_joystick_inputs():
            slots = self.slot_values(entry["slots_by_layout"].get(layout_name))
            for slot in slots:
                if slot in out:
                    continue
                out[slot] = {
                    "short": direction[:1].upper(),
                    "color": entry.get("color", dev.get("color", "")),
                }
        for axis in self.iter_axes():
            slots = self.slot_values(axis["slots_by_layout"].get(layout_name))
            for slot in slots:
                if slot in out:
                    continue
                out[slot] = {
                    "short": axis.get("short") or analog_short_label(axis.get("label"), axis.get("input")),
                    "color": axis.get("color", "Gray"),
                }
        return out

    def selected_assignable_item(self):
        if self.selected_button:
            return "button", str(self.selected_button["game_button"]), self.selected_button
        if self.selected_system_input:
            return "system_input", self.selected_system_input["key"], self.selected_system_input["entry"]
        if self.selected_system_output:
            return "system_output", self.system_output_key(self.selected_system_output), self.selected_system_output
        if self.selected_joystick:
            return "joystick", self.selected_joystick["direction"], self.selected_joystick["entry"]
        if self.selected_axis:
            return "axis", self.selected_axis.get("id", ""), self.selected_axis
        return None, "", None

    def panel_joystick_assignments_for_item(self, item):
        value = item.get("panel_joystick")
        if not value:
            return []
        if isinstance(value, dict):
            out = []
            for polarity in ("negative", "positive"):
                direction = value.get(polarity)
                if direction:
                    out.append((direction, polarity))
            return out
        return [(value, "")]

    def assigned_panel_joystick_map(self):
        out = {}
        for kind, item_id, item in self.iter_assignable_items():
            if kind == "button":
                short = normalize_mk_role(item.get("function")) or item.get("logical_name") or item_id
                color = item.get("color", "Gray")
            elif kind == "system_input":
                short = (item.get("label") or item_id)[:3].upper()
                color = item.get("color", "Gray")
            elif kind == "system_output":
                short = self.system_output_short_label(item)
                color = item.get("color", "Gray")
            elif kind == "joystick":
                short = str(item_id)[:1].upper()
                color = item.get("color", "Gray")
            else:
                short = item.get("short") or analog_short_label(item.get("label"), item.get("input")) or item_id
                color = item.get("color", "Gray")
            for direction, polarity in self.panel_joystick_assignments_for_item(item):
                out[direction] = {
                    "kind": kind,
                    "id": item_id,
                    "polarity": polarity,
                    "short": short,
                    "color": color,
                }
        return out

    def export_panel_joystick(self):
        out = {}
        for kind, item_id, item in self.iter_assignable_items():
            for direction, polarity in self.panel_joystick_assignments_for_item(item):
                payload = {
                    "kind": kind,
                    "id": item_id,
                }
                if polarity:
                    payload["polarity"] = polarity
                out[direction] = payload
        return out

    def clear_panel_joystick_conflicts(self, directions, selected_item=None):
        if isinstance(directions, str):
            directions = {directions}
        else:
            directions = set(directions)
        for _, _, item in self.iter_assignable_items():
            if item is selected_item:
                continue
            value = item.get("panel_joystick")
            if isinstance(value, dict):
                for polarity, assigned_direction in list(value.items()):
                    if assigned_direction in directions:
                        value[polarity] = ""
                if not any(value.values()):
                    item["panel_joystick"] = None
            elif value in directions:
                item["panel_joystick"] = None

    def on_panel_joystick_click(self, direction):
        _, _, selected_item = self.selected_assignable_item()
        if selected_item is None:
            return
        self.clear_panel_joystick_conflicts(direction, selected_item=selected_item)
        selected_item["panel_joystick"] = direction
        self.refresh()
        self._auto_copy_if_needed()

    def on_panel_joystick_right_click(self, direction):
        changed_item = None
        for _, _, item in self.iter_assignable_items():
            value = item.get("panel_joystick")
            if isinstance(value, dict):
                for polarity, assigned_direction in list(value.items()):
                    if assigned_direction == direction:
                        value[polarity] = ""
                        changed_item = item
                if not any(value.values()):
                    item["panel_joystick"] = None
            elif value == direction:
                item["panel_joystick"] = None
                changed_item = item
        if changed_item is None:
            return
        self.refresh()
        if changed_item is self.selected_button:
            self.select_button(self.selected_button)
        elif self.selected_system_input and changed_item is self.selected_system_input.get("entry"):
            self.select_system_input(self.selected_system_input["key"], self.selected_system_input["entry"])
        elif changed_item is self.selected_system_output:
            self.select_system_output(self.selected_system_output)
        elif self.selected_joystick and changed_item is self.selected_joystick.get("entry"):
            self.refresh_joystick_selection()
        elif changed_item is self.selected_axis:
            self.refresh_axis_selection()
        self._auto_copy_if_needed()

    def auto_panel_joystick_mapping_for_axis(self, axis):
        mame = axis.get("mame", {})
        tokens = {
            normalize_mame_token(axis.get("input", "")),
            normalize_mame_token(axis.get("id", "")),
            normalize_mame_token(mame.get("type", "")),
            normalize_mame_token(mame.get("ipt", "")),
            normalize_mame_token(mame.get("extend", {}).get("ipt", "")),
        }
        if tokens.intersection({"DIAL", "IPT_DIAL", "PADDLE", "IPT_PADDLE", "TRACKBALL_X", "IPT_TRACKBALL_X", "AD_STICK_X", "IPT_AD_STICK_X", "LIGHTGUN_X", "IPT_LIGHTGUN_X"}):
            return {"negative": "left", "positive": "right"}
        if tokens.intersection({"DIAL_V", "IPT_DIAL_V", "PADDLE_V", "IPT_PADDLE_V", "TRACKBALL_Y", "IPT_TRACKBALL_Y", "AD_STICK_Y", "IPT_AD_STICK_Y", "LIGHTGUN_Y", "IPT_LIGHTGUN_Y"}):
            return {"negative": "up", "positive": "down"}
        return None

    def on_panel_joystick_center_click(self):
        if not self.selected_axis:
            return
        mapping = self.auto_panel_joystick_mapping_for_axis(self.selected_axis)
        if not mapping:
            return
        self.clear_panel_joystick_conflicts(mapping.values(), selected_item=self.selected_axis)
        self.selected_axis["panel_joystick"] = mapping
        self.refresh()
        self._auto_copy_if_needed()

    def clear_slot_conflicts(self, layout_name, slot, selected_button=None, selected_system_input=None, selected_system_output=None, selected_joystick_entry=None, selected_axis=None):
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            if entry is selected_system_input:
                continue
            slots = self.slot_values(entry["slots_by_layout"].get(layout_name))
            if slot in slots:
                slots = [s for s in slots if s != slot]
                entry["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)
        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            if output is selected_system_output:
                continue
            slots = self.slot_values(output["slots_by_layout"].get(layout_name))
            if slot in slots:
                slots = [s for s in slots if s != slot]
                output["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)
        for b in self.player_data["buttons"]:
            if b is selected_button:
                continue
            slots = self.slot_values(b["slots_by_layout"].get(layout_name))
            if slot in slots:
                slots = [s for s in slots if s != slot]
                b["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)
        for _, _, entry in self.iter_joystick_inputs():
            if entry is selected_joystick_entry:
                continue
            slots = self.slot_values(entry["slots_by_layout"].get(layout_name))
            if slot in slots:
                slots = [s for s in slots if s != slot]
                entry["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)
        for axis in self.iter_axes():
            if axis is selected_axis:
                continue
            slots = self.slot_values(axis["slots_by_layout"].get(layout_name))
            if slot in slots:
                slots = [s for s in slots if s != slot]
                axis["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)

    def assign_slot_to_item(self, item, layout_name, slot):
        if self.single_slot_var.get():
            item["slots_by_layout"][layout_name] = slot
            return
        slots = self.slot_values(item["slots_by_layout"].get(layout_name))
        if slot in slots:
            slots = [s for s in slots if s != slot]
        else:
            slots.append(slot)
        item["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)

    def remove_slot_from_item(self, item, layout_name, slot):
        slots = self.slot_values(item["slots_by_layout"].get(layout_name))
        if slot not in slots:
            return False
        slots = [s for s in slots if s != slot]
        item["slots_by_layout"][layout_name] = slots if len(slots) > 1 else (slots[0] if slots else None)
        return True

    def on_slot_click(self, layout_name, slot):
        self.set_active_layout(layout_name, notify=True)
        if self.selected_button:
            self.clear_slot_conflicts(layout_name, slot, selected_button=self.selected_button)
            if not self.single_slot_var.get():
                current_slots = self.slot_values(self.selected_button["slots_by_layout"].get(layout_name))
                if current_slots and slot not in current_slots:
                    self.duplicate_button_for_slot(self.selected_button, layout_name, slot)
                else:
                    self.assign_slot_to_item(self.selected_button, layout_name, slot)
            else:
                self.assign_slot_to_item(self.selected_button, layout_name, slot)
        elif self.selected_system_input:
            entry = self.selected_system_input["entry"]
            self.clear_slot_conflicts(layout_name, slot, selected_system_input=entry)
            self.assign_slot_to_item(entry, layout_name, slot)
        elif self.selected_system_output:
            self.clear_slot_conflicts(layout_name, slot, selected_system_output=self.selected_system_output)
            self.assign_slot_to_item(self.selected_system_output, layout_name, slot)
        elif self.selected_joystick:
            entry = self.selected_joystick["entry"]
            self.clear_slot_conflicts(layout_name, slot, selected_joystick_entry=entry)
            self.assign_slot_to_item(entry, layout_name, slot)
        elif self.selected_axis:
            self.clear_slot_conflicts(layout_name, slot, selected_axis=self.selected_axis)
            self.assign_slot_to_item(self.selected_axis, layout_name, slot)

        self.refresh()
        if self.selected_button:
            self.select_button(self.selected_button)
        elif self.selected_system_input:
            self.select_system_input(self.selected_system_input["key"], self.selected_system_input["entry"])
        elif self.selected_system_output:
            self.select_system_output(self.selected_system_output)
        if self.selected_button or self.selected_system_input or self.selected_system_output or self.selected_joystick or self.selected_axis:
            self._auto_copy_if_needed()

    def on_slot_right_click(self, layout_name, slot):
        self.set_active_layout(layout_name, notify=True)
        changed_item = None
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            if self.remove_slot_from_item(entry, layout_name, slot):
                changed_item = entry
                break
        for b in self.player_data["buttons"]:
            if changed_item is not None:
                break
            if self.remove_slot_from_item(b, layout_name, slot):
                changed_item = b
                break
        if changed_item is None:
            for output in self.player_data.get("system_outputs", []):
                self.ensure_system_output_entry(output)
                if self.remove_slot_from_item(output, layout_name, slot):
                    changed_item = output
                    break
        if changed_item is None:
            for _, _, entry in self.iter_joystick_inputs():
                if self.remove_slot_from_item(entry, layout_name, slot):
                    changed_item = entry
                    break
        if changed_item is None:
            for axis in self.iter_axes():
                if self.remove_slot_from_item(axis, layout_name, slot):
                    changed_item = axis
                    break
        if changed_item is None:
            return
        self.refresh()
        if changed_item is self.selected_button:
            self.select_button(self.selected_button)
        elif self.selected_system_input and changed_item is self.selected_system_input.get("entry"):
            self.select_system_input(self.selected_system_input["key"], self.selected_system_input["entry"])
        elif changed_item is self.selected_system_output:
            self.select_system_output(self.selected_system_output)
        elif self.selected_joystick and changed_item is self.selected_joystick.get("entry"):
            self.refresh_joystick_selection()
        elif changed_item is self.selected_axis:
            self.refresh_axis_selection()
        self._auto_copy_if_needed()

    def clear_all(self):
        self.set_active_layout("4-Button", notify=True)
        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            for layout_name in LAYOUTS:
                entry["slots_by_layout"][layout_name] = None
            entry["panel_joystick"] = None
        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            for layout_name in LAYOUTS:
                output["slots_by_layout"][layout_name] = None
            output["panel_joystick"] = None
        for b in self.player_data["buttons"]:
            for layout_name in LAYOUTS:
                b["slots_by_layout"][layout_name] = None
            b["panel_joystick"] = None
        for _, _, entry in self.iter_joystick_inputs():
            for layout_name in LAYOUTS:
                entry["slots_by_layout"][layout_name] = None
            entry["panel_joystick"] = None
        for axis in self.iter_axes():
            for layout_name in LAYOUTS:
                axis["slots_by_layout"][layout_name] = None
            axis["physical_joystick"] = ""
            axis["joystick"] = {"negative": "", "positive": ""}
            axis["panel_joystick"] = None
        self.selected_button = None
        self.selected_system_input = None
        self.selected_system_output = None
        self.selected_joystick = None
        self.selected_axis = None
        self.selected_label_var.set("")
        self.update_delete_duplicate_state()
        self.refresh()
        self._auto_copy_if_needed()

    def _apply_pattern_if_empty(self):
        has_any = any(any(v is not None for v in b["slots_by_layout"].values()) for b in self.player_data["buttons"])
        if not has_any:
            self.apply_pattern()

    def apply_pattern(self):
        pattern = self.pattern_var.get()
        mapping = PATTERN_LIBRARY.get(pattern, {})

        for b in self.player_data["buttons"]:
            role = normalize_mk_role(b["function"])
            lname = (b["logical_name"] or "").strip().upper()
            index_key = str(b["game_button"])

            for layout_name in LAYOUTS:
                slot = None
                if role and role in mapping:
                    slot = mapping[role].get(layout_name)
                elif lname in mapping:
                    slot = mapping[lname].get(layout_name)
                elif index_key in mapping:
                    slot = mapping[index_key].get(layout_name)
                b["slots_by_layout"][layout_name] = slot

        self.refresh()
        self._auto_copy_if_needed()

    def _auto_copy_if_needed(self):
        if self.player_data["player"] == 1 and self.autocopy_var.get() and self.on_apply_p1:
            self.on_apply_p1(self)

    def apply_to_others(self):
        if self.player_data["player"] == 1 and self.on_apply_p1:
            self.on_apply_p1(self)

    def refresh(self):
        for layout_name, gv in self.grid_views.items():
            gv.render(self.assigned_map_for_layout(layout_name))
        for layout_name, card in self.layout_cards.items():
            card.set_active(layout_name == self.active_layout)
            card.render(self.assigned_map_for_layout(layout_name))
        if self.joystick_card:
            self.joystick_card.render(self.assigned_panel_joystick_map())

        for key in self.system_input_order():
            entry = self.player_data["system_inputs"][key]
            self.ensure_system_input_entry(key, entry)
            widgets = self.system_input_widgets.get(key)
            if not widgets:
                continue
            widgets["bubble"].delete("all")
            short = (entry.get("label") or key)[:3].upper()
            widgets["bubble"].create_oval(2, 2, 22, 22, fill=color_to_hex(entry.get("color", "Gray")), outline="#111827")
            widgets["bubble"].create_text(12, 12, text=short, fill="#111827", font=("Segoe UI", 7, "bold"))

            for layout_name in LAYOUTS:
                widgets["slot_labels"][layout_name].configure(text=self.slot_text(entry["slots_by_layout"].get(layout_name)))

            rb = ""
            rp = ""
            core = ""
            layout_order = [self.active_layout] + [x for x in LAYOUTS if x != self.active_layout]
            for layout_name in layout_order:
                sm = self.first_slot_mapping(entry["slots_by_layout"].get(layout_name))
                if sm:
                    rb = sm["retrobat_button"]
                    rp = str(sm["retropad_id"])
                    lr = sm.get("libretro_button", "")
                    fb = sm.get("fbneo_button", "")
                    core = lr if lr == fb else f"{lr} / {fb}"
                    break
            if not rb:
                sm = self.system_input_default_mapping(key)
                rb = sm.get("retrobat_button", "")
                rp = "" if sm.get("retropad_id") is None else str(sm.get("retropad_id", ""))
                lr = sm.get("libretro_button", "")
                fb = sm.get("fbneo_button", "")
                core = lr if lr == fb else f"{lr} / {fb}"
            source_rp = system_source_retropad_id(key)
            if source_rp is not None:
                rp = str(source_rp)
            widgets["rb_var"].set(rb)
            widgets["rp_var"].set(rp)
            widgets["core_var"].set(core)

        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            widgets = self.system_output_widgets.get(self.system_output_key(output))
            if not widgets:
                continue
            widgets["bubble"].delete("all")
            short = self.system_output_short_label(output)
            widgets["bubble"].create_oval(2, 2, 22, 22, fill=color_to_hex(output.get("color", "Gray")), outline="#111827")
            widgets["bubble"].create_text(12, 12, text=short[:3], fill="#111827", font=("Segoe UI", 7, "bold"))

            for layout_name in LAYOUTS:
                widgets["slot_labels"][layout_name].configure(text=self.slot_text(output["slots_by_layout"].get(layout_name)))

            rb = ""
            rp = ""
            core = ""
            layout_order = [self.active_layout] + [x for x in LAYOUTS if x != self.active_layout]
            for layout_name in layout_order:
                sm = self.first_slot_mapping(output["slots_by_layout"].get(layout_name))
                if sm:
                    rb = sm["retrobat_button"]
                    rp = str(sm["retropad_id"])
                    lr = sm.get("libretro_button", "")
                    fb = sm.get("fbneo_button", "")
                    core = lr if lr == fb else f"{lr} / {fb}"
                    break
            mame = self.mame_for_system_output_input(output)
            source_rp = mame_source_retropad_id(mame)
            if source_rp is not None:
                rp = str(source_rp)
            widgets["rb_var"].set(rb)
            widgets["rp_var"].set(rp)
            widgets["core_var"].set(core)
            widgets["mame_var"].set(mame.get("type") or mame.get("input_id") or "")
            widgets["tag_var"].set(mame.get("tag", ""))
            widgets["mask_var"].set(mame.get("mask_hex", ""))

        for b in self.player_data["buttons"]:
            widgets = self.button_widgets[self.button_row_key(b)]
            widgets["bubble"].delete("all")
            short = normalize_mk_role(b["function"]) or b["logical_name"]
            widgets["bubble"].create_oval(2, 2, 22, 22, fill=color_to_hex(b["color"]), outline="#111827")
            widgets["bubble"].create_text(12, 12, text=short, fill="#111827", font=("Segoe UI", 8, "bold"))

            for layout_name in LAYOUTS:
                widgets["slot_labels"][layout_name].configure(text=self.slot_text(b["slots_by_layout"][layout_name]))

            rb = ""
            rp = ""
            core = ""
            layout_order = [self.active_layout] + [x for x in LAYOUTS if x != self.active_layout]
            for layout_name in layout_order:
                sm = self.first_slot_mapping(b["slots_by_layout"][layout_name])
                if sm:
                    rb = sm["retrobat_button"]
                    rp = str(sm["retropad_id"])
                    lr = sm.get("libretro_button", "")
                    fb = sm.get("fbneo_button", "")
                    core = lr if lr == fb else f"{lr} / {fb}"
                    break
            source_rp = logical_button_source_retropad_id(b)
            if source_rp is not None:
                rp = str(source_rp)
            widgets["rb_var"].set(rb)
            widgets["rp_var"].set(rp)
            widgets["core_var"].set(core)

        for widgets in self.joystick_widgets:
            entry = widgets["entry"]
            widgets["bubble"].delete("all")
            short = widgets["direction"][:1].upper()
            widgets["bubble"].create_oval(2, 2, 22, 22, fill=color_to_hex(entry.get("color", widgets["dev"].get("color", ""))), outline="#111827")
            widgets["bubble"].create_text(12, 12, text=short, fill="#111827", font=("Segoe UI", 8, "bold"))

            for layout_name in LAYOUTS:
                widgets["slot_labels"][layout_name].configure(text=self.slot_text(entry["slots_by_layout"].get(layout_name)))

            rb = ""
            rp = ""
            core = ""
            layout_order = [self.active_layout] + [x for x in LAYOUTS if x != self.active_layout]
            for layout_name in layout_order:
                sm = self.first_slot_mapping(entry["slots_by_layout"].get(layout_name))
                if sm:
                    rb = sm["retrobat_button"]
                    rp = str(sm["retropad_id"])
                    lr = sm.get("libretro_button", "")
                    fb = sm.get("fbneo_button", "")
                    core = lr if lr == fb else f"{lr} / {fb}"
                    break
            if not rb:
                sm = JOYSTICK_DIRECTION_MAP.get(widgets["direction"], {})
                rb = sm.get("retrobat_button", "")
                rp = "" if sm.get("retropad_id") is None else str(sm.get("retropad_id", ""))
                lr = sm.get("libretro_button", "")
                fb = sm.get("fbneo_button", "")
                core = lr if lr == fb else f"{lr} / {fb}"
            source_rp = JOYSTICK_DIRECTION_MAP.get(widgets["direction"], {}).get("retropad_id")
            if source_rp is not None:
                rp = str(source_rp)
            widgets["rb_var"].set(rb)
            widgets["rp_var"].set(rp)
            widgets["core_var"].set(core)

        for widgets in self.axis_widgets:
            axis = widgets["axis"]
            widgets["bubble"].delete("all")
            short = axis.get("short") or analog_short_label(axis.get("label"), axis.get("input"))
            widgets["bubble"].create_oval(2, 2, 22, 22, fill=color_to_hex(axis.get("color", "Gray")), outline="#111827")
            widgets["bubble"].create_text(12, 12, text=str(short)[:3], fill="#111827", font=("Segoe UI", 7, "bold"))
            for layout_name in LAYOUTS:
                widgets["slot_labels"][layout_name].configure(text=self.slot_text(axis["slots_by_layout"].get(layout_name)))

            rb = ""
            rp = ""
            core = ""
            layout_order = [self.active_layout] + [x for x in LAYOUTS if x != self.active_layout]
            for layout_name in layout_order:
                sm = self.first_slot_mapping(axis["slots_by_layout"].get(layout_name))
                if sm:
                    rb = sm["retrobat_button"]
                    rp = str(sm["retropad_id"])
                    lr = sm.get("libretro_button", "")
                    fb = sm.get("fbneo_button", "")
                    core = lr if lr == fb else f"{lr} / {fb}"
                    break
            source_rp = mame_source_retropad_id(axis.get("mame", {}))
            if source_rp is not None:
                rp = str(source_rp)
            widgets["rb_var"].set(rb)
            widgets["rp_var"].set(rp)
            widgets["core_var"].set(core)

        self.refresh_button_selection()
        self.refresh_system_input_selection()
        self.refresh_system_output_selection()
        self.refresh_joystick_selection()
        self.refresh_axis_selection()

    def export_player(self):
        player_id = str(self.player_data["player"])

        devices = []
        for dev in self.player_data["devices"]:
            inputs = {}
            for direction, entry in dev.get("inputs", {}).items():
                if direction in JOYSTICK_DIRECTION_MAP and not is_specific_joystick_input(entry):
                    continue
                direction_mapping = JOYSTICK_DIRECTION_MAP.get(direction, {})
                exported_entry = {
                    k: v
                    for k, v in entry.items()
                    if k != "slots_by_layout"
                }
                if direction_mapping:
                    exported_entry["mapping"] = direction_mapping
                layouts = {}
                for layout_name in LAYOUTS:
                    payload = self.slot_export_payload(entry.get("slots_by_layout", {}).get(layout_name))
                    if payload:
                        layouts[layout_name] = payload
                if layouts:
                    exported_entry["layouts"] = layouts
                inputs[direction] = exported_entry

            devices.append({
                "id": dev["id"],
                "label": dev["label"],
                "type": dev["type"],
                "raw": dev.get("raw", ""),
                "codes": dev.get("codes", []),
                "color": dev.get("color", ""),
                "inputs": inputs,
                "axes": dev.get("axes", {}),
            })

        buttons = {}
        for b in self.player_data["buttons"]:
            button_key = self.button_row_key(b)
            buttons[button_key] = {
                "game_button": b["game_button"],
                "logical_name": b["logical_name"],
                "function": b["function"],
                "color": b["color"],
                "output": b.get("output", ""),
                "panel_joystick": b.get("panel_joystick"),
                "mame": b["mame"],
            }
            if b.get("instance_id"):
                buttons[button_key]["instance_id"] = b["instance_id"]
                buttons[button_key]["duplicate_of"] = b.get("duplicate_of", b["game_button"])

        axes = []
        for axis in self.iter_axes():
            exported_axis = {
                k: v
                for k, v in axis.items()
                if k not in {"slots_by_layout", "slots_by_polarity"}
            }
            layouts_for_axis = {}
            for layout_name in LAYOUTS:
                payload = self.slot_export_payload(axis.get("slots_by_layout", {}).get(layout_name))
                if payload:
                    layouts_for_axis[layout_name] = payload
            if layouts_for_axis:
                exported_axis["layouts"] = layouts_for_axis
            axes.append(exported_axis)

        system_outputs = []
        for output in self.player_data.get("system_outputs", []):
            self.ensure_system_output_entry(output)
            exported_output = {
                k: v
                for k, v in output.items()
                if k != "slots_by_layout"
            }
            layouts_for_output = {}
            for layout_name in LAYOUTS:
                payload = self.slot_export_payload(output.get("slots_by_layout", {}).get(layout_name))
                if payload:
                    layouts_for_output[layout_name] = payload
            if layouts_for_output:
                exported_output["layouts"] = layouts_for_output
            system_outputs.append(exported_output)

        layouts = {}
        for layout_name in LAYOUTS:
            layout_system_inputs = {}
            for key in self.system_input_order():
                entry = self.player_data["system_inputs"][key]
                self.ensure_system_input_entry(key, entry)
                payload = self.slot_export_payload(entry["slots_by_layout"].get(layout_name))
                if not payload:
                    continue
                layout_system_inputs[key] = payload

            layout_system_outputs = {}
            for output in self.player_data.get("system_outputs", []):
                self.ensure_system_output_entry(output)
                payload = self.slot_export_payload(output["slots_by_layout"].get(layout_name))
                if not payload:
                    continue
                layout_system_outputs[self.system_output_key(output)] = payload

            layout_buttons = {}
            for b in self.player_data["buttons"]:
                payload = self.slot_export_payload(b["slots_by_layout"][layout_name])
                if not payload:
                    continue
                layout_buttons[self.button_row_key(b)] = payload

            layout_axes = {}
            for axis in self.iter_axes():
                payload = self.slot_export_payload(axis["slots_by_layout"].get(layout_name))
                if not payload:
                    continue
                layout_axes[axis["id"]] = payload

            layouts[layout_name] = {
                "pattern": self.pattern_var.get(),
                "system_inputs": layout_system_inputs,
                "system_outputs": layout_system_outputs,
                "buttons": layout_buttons,
                "axes": layout_axes,
            }

        return player_id, {
            "devices": devices,
            "system_inputs": self.player_data["system_inputs"],
            "system_outputs": system_outputs,
            "buttons": buttons,
            "axes": axes,
            "panel_joystick": self.export_panel_joystick(),
            "mame_device_mapping": self.player_data.get("mame_device_mapping", {}),
            "mame_inputs_extra": self.player_data.get("mame_inputs_extra", []),
            "layouts": layouts,
        }


# ============================================================
# COMMON BUTTON VARS EDITOR
# ============================================================

class CommonButtonsEditor(ttk.Frame):
    def __init__(self, parent, common_vars, on_apply):
        super().__init__(parent)
        self.common_vars = common_vars
        self.on_apply = on_apply
        self.vars = {}
        self._build_ui()

    def _build_ui(self):
        ttk.Label(self, text="Edit common logical names and functions, then apply them to all players.").pack(anchor="w", padx=4, pady=(2, 6))

        header = ttk.Frame(self)
        header.pack(fill="x", padx=4, pady=2)

        ttk.Label(header, text="Index", width=6).grid(row=0, column=0, padx=2, sticky="w")
        ttk.Label(header, text="Logical Name", width=14).grid(row=0, column=1, padx=2, sticky="w")
        ttk.Label(header, text="Function", width=22).grid(row=0, column=2, padx=2, sticky="w")

        body = ttk.Frame(self)
        body.pack(fill="x", padx=4, pady=4)

        row_idx = 0
        for key in sorted(self.common_vars.keys(), key=lambda x: int(x)):
            entry = self.common_vars[key]
            logical_var = tk.StringVar(value=entry.get("logical_name", ""))
            function_var = tk.StringVar(value=entry.get("function", ""))

            ttk.Label(body, text=key, width=6).grid(row=row_idx, column=0, padx=2, pady=2, sticky="w")
            ttk.Entry(body, textvariable=logical_var, width=14).grid(row=row_idx, column=1, padx=2, pady=2, sticky="w")
            ttk.Entry(body, textvariable=function_var, width=22).grid(row=row_idx, column=2, padx=2, pady=2, sticky="w")

            self.vars[key] = {
                "logical_name": logical_var,
                "function": function_var,
            }
            row_idx += 1

        actions = ttk.Frame(self)
        actions.pack(fill="x", padx=4, pady=4)
        ttk.Button(actions, text="Apply to all players", command=self.apply_all).pack(side="left", padx=4)

    def apply_all(self):
        payload = {}
        for key, entry in self.vars.items():
            payload[key] = {
                "logical_name": entry["logical_name"].get().strip(),
                "function": entry["function"].get().strip(),
            }
        self.on_apply(payload)


# ============================================================
# APP
# ============================================================

class CuratorApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Panel Curator Ultimate")
        self.repo = DataRepository()
        self.roms = list_roms()
        if not self.roms:
            raise RuntimeError("No MAME ROM with panel image found.")

        style = ttk.Style()
        style.configure("Selected.TLabel", background="#dbeafe", foreground="#111827")

        self.index = 0
        self.current_data = None
        self.current_photo = None
        self.player_editors = []
        self.mame_player_devices = self.load_mame_player_devices()
        self.es_mame_device_choices = self.load_es_mame_device_choices()
        self.mame_device_choices = self.build_mame_device_choices()
        self.common_editor = None
        configured_panel_layout = APP_CONFIG.get("export", "panel_layout", fallback="4-Button").strip()
        if configured_panel_layout not in LAYOUTS:
            configured_panel_layout = "4-Button"
        self.export_layout_var = tk.StringVar(value=configured_panel_layout)
        self.ledpanel = LedPanelBridge(
            port=LEDPANEL_PORT,
            baudrate=LEDPANEL_BAUDRATE,
            timeout_ms=LEDPANEL_TIMEOUT_MS,
            auto_detect=LEDPANEL_AUTO_DETECT,
            mode=LEDPANEL_MODE,
            apiexpose_base_url=LEDPANEL_APIEXPOSE_BASE_URL,
            ledmanager_dir=LEDMANAGER_DIR,
            ledmanager_exe=LEDMANAGER_EXE,
            ledmanager_ini=LEDMANAGER_INI,
        )
        self.ledpanel_status_var = tk.StringVar(value="LED PANEL: offline")
        self._ledpanel_sync_job = None
        self._ultimate_batch = None

        self._build_ui()
        self.load_current()
        self.root.after(120, lambda: self.probe_mame_devices(silent=True))
        self.root.after(300, self.refresh_ledpanel_status)

    def load_mame_player_devices(self):
        if not os.path.exists(MAME_PLAYER_DEVICES_FILE):
            return {"players": {}, "devices": []}
        try:
            with open(MAME_PLAYER_DEVICES_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            return {"players": {}, "devices": []}
        if not isinstance(data, dict):
            return {"players": {}, "devices": []}
        data.setdefault("players", {})
        data.setdefault("devices", [])
        return data

    def save_mame_player_devices(self):
        os.makedirs(os.path.dirname(MAME_PLAYER_DEVICES_FILE), exist_ok=True)
        with open(MAME_PLAYER_DEVICES_FILE, "w", encoding="utf-8") as f:
            json.dump(self.mame_player_devices, f, indent=2, ensure_ascii=False)

    def load_es_mame_device_choices(self):
        if not ES_INPUT_CFG or not os.path.exists(ES_INPUT_CFG):
            return []
        try:
            tree = ET.parse(ES_INPUT_CFG)
        except Exception:
            return []
        devices = []
        seen = set()
        for input_config in tree.getroot().findall("inputConfig"):
            if (input_config.attrib.get("type") or "").lower() != "joystick":
                continue
            label = normalize_hardware_label(input_config.attrib.get("deviceName") or "")
            guid = (input_config.attrib.get("deviceGUID") or "").strip()
            device = mame_product_from_es_guid(guid)
            if not label or not device:
                continue
            key = device or label.lower()
            if key in seen:
                continue
            seen.add(key)
            devices.append({
                "label": label,
                "device": device,
                "raw_device_id": guid,
                "source": "es_input",
            })
        return devices

    def label_for_mame_device_id(self, device_id):
        normalized = normalize_mame_mapdevice_id(device_id)
        for device in self.es_mame_device_choices:
            if device.get("device") == normalized:
                return normalize_hardware_label(device.get("label", ""))
        return ""

    def build_mame_device_choices(self):
        choices = []
        seen = set()
        probed_devices = list(self.mame_player_devices.get("devices", []))
        sources = [
            probed_devices,
            list(self.mame_player_devices.get("players", {}).values()) if not probed_devices else [],
            [] if probed_devices else list(self.es_mame_device_choices),
            manual_mame_device_choices(),
        ]
        for source in sources:
            for choice in source:
                if not isinstance(choice, dict):
                    continue
                joycode = str(choice.get("joycode") or "").upper()
                normalized = {
                    "joycode": joycode,
                    "label": normalize_hardware_label(choice.get("label", "")),
                    "device": normalize_mame_mapdevice_id(choice.get("device", "") or choice.get("raw_device_id", "")),
                    "raw_device_id": choice.get("raw_device_id", ""),
                    "source": choice.get("source", ""),
                }
                key = normalized["device"] or normalized["label"].lower()
                if key in seen:
                    continue
                seen.add(key)
                choices.append(normalized)
        return choices or manual_mame_device_choices()

    def mame_mapping_for_player(self, player):
        player_key = str(player)
        mapping = self.mame_player_devices.get("players", {}).get(player_key)
        if isinstance(mapping, dict) and mapping.get("device"):
            return dict(mapping)
        return {
            "joycode": "",
            "label": "No hardware mapping",
            "device": "",
        }

    def apply_mame_mapping_to_player_data(self, pdata):
        pdata["mame_device_mapping"] = self.mame_mapping_for_player(pdata.get("player", 1))

    def on_player_mame_device_selected(self, editor, choice):
        player_key = str(editor.player_data.get("player", 1))
        mapping = dict(choice)
        mapping["joycode"] = f"JOYCODE_{player_key}"
        if not mapping.get("device"):
            self.mame_player_devices.setdefault("players", {}).pop(player_key, None)
            editor.player_data["mame_device_mapping"] = {
                "joycode": "",
                "label": "No hardware mapping",
                "device": "",
            }
        else:
            self.mame_player_devices.setdefault("players", {})[player_key] = mapping
            editor.player_data["mame_device_mapping"] = mapping
        self.save_mame_player_devices()
        self.mame_device_choices = self.build_mame_device_choices()
        for player_editor in self.player_editors:
            player_editor.set_mame_device_choices(self.mame_device_choices)

    def parse_mame_verbose_devices(self, text):
        devices = []
        for line in (text or "").splitlines():
            match = re.search(r"Input:\s+Adding joystick #(\d+):\s+(.*?)\s+\(device id:\s*(.*?)\)\s*$", line)
            if not match:
                continue
            idx = int(match.group(1))
            mame_label = normalize_hardware_label(match.group(2))
            raw_device_id = match.group(3).strip()
            device_id = normalize_mame_mapdevice_id(raw_device_id)
            es_label = self.label_for_mame_device_id(device_id)
            label = combine_hardware_labels(es_label, mame_label)
            devices.append({
                "joycode": f"JOYCODE_{idx}",
                "label": label,
                "device": device_id,
                "raw_device_id": raw_device_id,
            })
        return devices

    def probe_mame_devices(self, editor=None, silent=False):
        rom = (self.current_data or {}).get("system") or (self.roms[self.index] if self.roms else "")
        if not rom or not os.path.exists(MAME_EXE):
            if not silent:
                messagebox.showerror("MAME Devices", f"MAME executable not found:\n{MAME_EXE}")
            return
        probe_cfg = os.path.join("C:\\tmp", "apiexpose-mame-cfg-probe")
        probe_input = os.path.join("C:\\tmp", "apiexpose-mame-input-probe")
        os.makedirs(probe_cfg, exist_ok=True)
        os.makedirs(probe_input, exist_ok=True)
        cmd = [
            MAME_EXE,
            rom,
            "-rompath", ROMS_DIR,
            "-inipath", MAME_INI_DIR,
            "-cfg_directory", probe_cfg,
            "-input_directory", probe_input,
            "-video", "none",
            "-sound", "none",
            "-seconds_to_run", "1",
            "-skip_gameinfo",
            "-joystick",
            "-verbose",
        ]
        try:
            completed = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=20,
                check=False,
            )
        except Exception as exc:
            if not silent:
                messagebox.showerror("MAME Devices", f"Unable to probe MAME devices:\n{exc}")
            return
        devices = self.parse_mame_verbose_devices((completed.stdout or "") + "\n" + (completed.stderr or ""))
        if not devices:
            if not silent:
                messagebox.showwarning("MAME Devices", "No joystick devices found in MAME verbose output.")
            return
        self.mame_player_devices["devices"] = devices
        self.mame_device_choices = self.build_mame_device_choices()
        for player_editor in self.player_editors:
            player_editor.set_mame_device_choices(self.mame_device_choices)
        self.save_mame_player_devices()
        if not silent:
            detail = "\n".join(mame_device_choice_label(device, include_joycode=True) for device in devices)
            messagebox.showinfo("MAME Devices", f"Detected MAME joystick devices:\n\n{detail}")

    def _build_ui(self):
        self.root.geometry("1920x1080")

        action_bar = ttk.Frame(self.root)
        action_bar.pack(fill="x", padx=8, pady=8)

        ttk.Button(action_bar, text="Previous", command=self.prev_rom).pack(side="left", padx=4)
        ttk.Button(action_bar, text="Save", command=self.save_current).pack(side="left", padx=4)
        ttk.Button(action_bar, text="Save + Next", command=self.save_and_next).pack(side="left", padx=4)
        ttk.Button(action_bar, text="Next", command=self.next_rom).pack(side="left", padx=4)
        ttk.Button(action_bar, text="Reload", command=self.load_current).pack(side="left", padx=4)
        ttk.Button(action_bar, text="Export systems", command=self.export_systems).pack(side="left", padx=4)

        ttk.Separator(action_bar, orient="vertical").pack(side="left", fill="y", padx=8)

        ttk.Label(action_bar, text="Search ROM").pack(side="left", padx=(4, 2))
        self.search_var = tk.StringVar()
        self.search_entry = ttk.Entry(action_bar, textvariable=self.search_var, width=18)
        self.search_entry.pack(side="left", padx=2)
        ttk.Button(action_bar, text="Go", command=self.goto_search).pack(side="left", padx=2)

        ttk.Label(action_bar, text="Index").pack(side="left", padx=(12, 2))
        self.goto_var = tk.StringVar()
        self.goto_entry = ttk.Entry(action_bar, textvariable=self.goto_var, width=8)
        self.goto_entry.pack(side="left", padx=2)
        ttk.Button(action_bar, text="Go to", command=self.goto_index).pack(side="left", padx=2)

        self.status_var = tk.StringVar()
        ttk.Label(action_bar, textvariable=self.status_var).pack(side="right", padx=8)

        header = ttk.Frame(self.root)
        header.pack(fill="x", padx=8, pady=4)

        self.rom_var = tk.StringVar()
        self.game_var = tk.StringVar()

        ttk.Label(header, textvariable=self.rom_var, font=("Segoe UI", 16, "bold")).pack(side="left", padx=8)
        ttk.Label(header, textvariable=self.game_var, font=("Segoe UI", 13)).pack(side="left", padx=8)

        body = ttk.Panedwindow(self.root, orient="horizontal")
        body.pack(fill="both", expand=True)

        left = ttk.Frame(body)
        right = ttk.Frame(body)
        body.add(left, weight=2)
        body.add(right, weight=3)

        panel_box = ttk.LabelFrame(left, text="Panel")
        panel_box.pack(fill="x", padx=8, pady=8)

        self.image_label = ttk.Label(panel_box)
        self.image_label.pack(fill="x", padx=8, pady=8)

        meta_box = ttk.LabelFrame(left, text="Metadata")
        meta_box.pack(fill="x", padx=8, pady=8)

        self.meta_text = tk.Text(meta_box, height=10, wrap="word")
        self.meta_text.pack(fill="x", padx=6, pady=6)

        common_box = ttk.LabelFrame(left, text="Common Button Variables")
        common_box.pack(fill="x", padx=8, pady=8)

        self.common_container = ttk.Frame(common_box)
        self.common_container.pack(fill="x", padx=4, pady=4)

        self.notebook = ttk.Notebook(right)
        self.notebook.pack(fill="both", expand=True, padx=8, pady=8)

    def clear_notebook(self):
        for tab_id in list(self.notebook.tabs()):
            try:
                widget_name = self.notebook.nametowidget(tab_id)
            except Exception:
                widget_name = None
            self.notebook.forget(tab_id)
            if widget_name is not None:
                try:
                    widget_name.destroy()
                except Exception:
                    pass
        self.player_editors = []

    def clear_common_editor(self):
        for w in self.common_container.winfo_children():
            w.destroy()
        self.common_editor = None

    def load_image(self, rom):
        path = find_panel_image(rom)
        if not path:
            self.current_photo = None
            self.image_label.configure(image="", text="No image")
            return

        img = Image.open(path)
        img.thumbnail((520, 300))
        self.current_photo = ImageTk.PhotoImage(img)
        self.image_label.configure(image=self.current_photo)

    def load_current(self):
        rom = self.roms[self.index]
        self.current_data = self.repo.get_game_data(rom)

        self.rom_var.set(rom)
        self.game_var.set(self.current_data["game_name"])
        if hasattr(self, "top_game_var"):
            self.top_game_var.set("PANEL CURATOR")
        self.status_var.set(f"{self.index + 1} / {len(self.roms)}")

        self.load_image(rom)

        self.meta_text.delete("1.0", "end")
        meta = self.current_data["meta"]
        self.meta_text.insert(
            "end",
            (
                f"system: {self.current_data['system']}\n"
                f"players: {meta['players']}\n"
                f"alternating: {meta['alternating']}\n"
                f"mirrored: {meta['mirrored']}\n"
                f"tilt: {meta['tilt']}\n"
                f"cocktail: {meta['cocktail']}\n"
                f"uses_service: {meta['uses_service']}\n\n"
                f"{meta['misc_details']}\n"
            )
        )

        self.clear_common_editor()
        self.common_editor = CommonButtonsEditor(
            self.common_container,
            self.current_data["common_button_vars"],
            on_apply=self.apply_common_button_vars
        )
        self.common_editor.pack(fill="x")

        self.clear_notebook()
        for pdata in self.current_data["players_data"]:
            self.apply_mame_mapping_to_player_data(pdata)
            editor = PlayerEditor(
                self.notebook,
                pdata,
                on_apply_p1=self.copy_p1_to_others,
                on_layout_select=self.on_visual_panel_selected,
                on_state_change=self.on_editor_state_changed,
                mame_device_choices=self.mame_device_choices,
                on_mame_device_select=self.on_player_mame_device_selected,
                on_mame_device_probe=self.probe_mame_devices,
            )
            editor.set_active_layout(self.export_layout_var.get())
            self.player_editors.append(editor)
            self.notebook.add(editor, text=f'Player {pdata["player"]}')
        self.schedule_ledpanel_sync()

    def apply_common_button_vars(self, payload):
        self.current_data["common_button_vars"] = payload

        for pdata in self.current_data["players_data"]:
            for b in pdata["buttons"]:
                key = str(b["game_button"])
                if key in payload:
                    if payload[key]["logical_name"]:
                        b["logical_name"] = payload[key]["logical_name"]
                    if payload[key]["function"]:
                        b["function"] = payload[key]["function"]

        current_tab = self.notebook.index(self.notebook.select()) if self.notebook.tabs() else 0
        self.clear_notebook()
        for pdata in self.current_data["players_data"]:
            self.apply_mame_mapping_to_player_data(pdata)
            editor = PlayerEditor(
                self.notebook,
                pdata,
                on_apply_p1=self.copy_p1_to_others,
                on_layout_select=self.on_visual_panel_selected,
                on_state_change=self.on_editor_state_changed,
                mame_device_choices=self.mame_device_choices,
                on_mame_device_select=self.on_player_mame_device_selected,
                on_mame_device_probe=self.probe_mame_devices,
            )
            editor.set_active_layout(self.export_layout_var.get())
            self.player_editors.append(editor)
            self.notebook.add(editor, text=f'Player {pdata["player"]}')
        if self.notebook.tabs():
            self.notebook.select(min(current_tab, len(self.notebook.tabs()) - 1))
        self.schedule_ledpanel_sync()

    def on_visual_panel_selected(self, layout_name):
        if layout_name in LAYOUTS:
            self.export_layout_var.set(layout_name)
            self.save_user_panel_layout(layout_name)
            self.schedule_ledpanel_sync()

    def on_export_layout_changed(self, event=None):
        layout_name = self.export_layout_var.get()
        if layout_name not in LAYOUTS:
            return
        self.save_user_panel_layout(layout_name)
        for editor in self.player_editors:
            editor.set_active_layout(layout_name)
        self.schedule_ledpanel_sync()

    def save_user_panel_layout(self, layout_name):
        if layout_name not in LAYOUTS:
            return
        cfg = configparser.ConfigParser()
        if os.path.exists(APP_INI_PATH):
            cfg.read(APP_INI_PATH, encoding="utf-8")
        if not cfg.has_section("export"):
            cfg.add_section("export")
        cfg.set("export", "panel_layout", layout_name)
        with open(APP_INI_PATH, "w", encoding="utf-8") as f:
            cfg.write(f)
        APP_CONFIG.set("export", "panel_layout", layout_name)

    def set_export_layout(self, layout_name, sync_ledpanel=True):
        if layout_name not in LAYOUTS:
            return
        self.export_layout_var.set(layout_name)
        for editor in self.player_editors:
            editor.set_active_layout(layout_name)
        if sync_ledpanel:
            self.schedule_ledpanel_sync()

    def auto_export_layout_for_current_game(self):
        layout_scores = {}
        for layout_name in LAYOUTS:
            button_score = 0
            extra_score = 0
            for editor in self.player_editors:
                for button in editor.player_data.get("buttons", []):
                    if button.get("slots_by_layout", {}).get(layout_name) is not None:
                        button_score += 1
                for key, entry in editor.player_data.get("system_inputs", {}).items():
                    if entry.get("slots_by_layout", {}).get(layout_name) is not None:
                        extra_score += 1
                for _, _, entry in editor.iter_joystick_inputs():
                    if entry.get("slots_by_layout", {}).get(layout_name) is not None:
                        extra_score += 1
                for axis in editor.iter_axes():
                    if axis.get("slots_by_layout", {}).get(layout_name) is not None:
                        extra_score += 1
                for output in editor.player_data.get("system_outputs", []):
                    if output.get("slots_by_layout", {}).get(layout_name) is not None:
                        extra_score += 1
            layout_size = int(layout_name.split("-", 1)[0])
            layout_scores[layout_name] = (button_score, extra_score, -layout_size)

        best_layout, best_score = max(layout_scores.items(), key=lambda item: item[1])
        if best_score[0] > 0 or best_score[1] > 0:
            return best_layout

        unique_buttons = set()
        for editor in self.player_editors:
            for button in editor.player_data.get("buttons", []):
                try:
                    unique_buttons.add(int(button.get("game_button", 0)))
                except Exception:
                    continue
        count = len(unique_buttons)
        if count <= 2:
            return "2-Button"
        if count <= 4:
            return "4-Button"
        if count <= 6:
            return "6-Button"
        return "8-Button"

    def center_modal_on_root(self, win, width=None, height=None):
        try:
            self.root.update_idletasks()
            win.update_idletasks()
            width = width or max(win.winfo_reqwidth(), 720)
            height = height or max(win.winfo_reqheight(), 360)
            x = self.root.winfo_rootx() + max((self.root.winfo_width() - width) // 2, 0)
            y = self.root.winfo_rooty() + max((self.root.winfo_height() - height) // 2, 0)
            win.geometry(f"{width}x{height}+{x}+{y}")
        except Exception:
            pass

    def update_ultimate_modal_controls(self):
        batch = self._ultimate_batch
        if not batch:
            return
        mode = batch["mode_var"].get()
        if not batch.get("started"):
            if mode == "auto":
                batch["layout_status_var"].set("Mode: Auto per game")
            else:
                batch["layout_status_var"].set(f"Mode: Fixed {batch['layout_var'].get()}")
        state = "disabled" if batch.get("started") else "readonly"
        if mode == "manual":
            batch["layout_combo"].configure(state=state)
        else:
            batch["layout_combo"].configure(state="disabled")
        exports_editable = not batch.get("started")
        export_radio_state = "normal" if exports_editable else "disabled"
        for key, radio in batch.get("export_radios", {}).items():
            radio.configure(state=export_radio_state)
        selected_export = batch.get("selected_export_var", tk.StringVar(value="")).get().strip()
        any_export_selected = bool(selected_export)
        json_selected = selected_export == "json"
        batch["confirm_json_check"].configure(state="normal" if exports_editable and json_selected else "disabled")
        running = batch.get("running", False)
        paused = batch.get("paused", False)
        finished = batch.get("finished", False)
        batch["start_button"].configure(state="disabled" if batch.get("started") or not any_export_selected else "normal")
        batch["stop_button"].configure(state="normal" if running else "disabled")
        batch["resume_button"].configure(state="normal" if paused and not finished else "disabled")
        batch["close_button"].configure(text="Close" if finished or not running else "Cancel")

    def close_ultimate_generation_modal(self):
        batch = self._ultimate_batch
        if not batch:
            return
        should_restore = False
        if batch.get("running") or batch.get("paused"):
            if not messagebox.askyesno("Ultimate Generation", "Cancel the current batch?"):
                return
            after_id = batch.get("after_id")
            if after_id:
                try:
                    self.root.after_cancel(after_id)
                except Exception:
                    pass
            should_restore = True
        elif batch.get("started") and not batch.get("finished"):
            should_restore = True
        if should_restore:
            self.index = min(max(batch.get("original_index", self.index), 0), max(len(self.roms) - 1, 0))
            self.load_current()
            self.set_export_layout(batch.get("original_layout", "4-Button"))
        modal = batch.get("window")
        self._ultimate_batch = None
        if modal:
            try:
                modal.grab_release()
            except Exception:
                pass
            try:
                modal.destroy()
            except Exception:
                pass

    def start_ultimate_generation_batch(self):
        batch = self._ultimate_batch
        if not batch or batch.get("started"):
            return
        selected_export = batch.get("selected_export_var", tk.StringVar(value="")).get().strip()
        export_selection = self.normalize_export_selection({
            "json": selected_export == "json",
            "mame_cfg": selected_export == "mame_cfg",
            "ra_rmp": selected_export == "ra_rmp",
            "rb_xml": selected_export == "rb_xml",
            "libretro_yml": selected_export == "libretro_yml",
        })
        if not any(export_selection.values()):
            messagebox.showwarning("Ultimate Generation", "Select at least one export.", parent=batch.get("window"))
            return
        try:
            self.save_current(silent=True, export_selection=export_selection)
        except Exception as exc:
            messagebox.showwarning("Ultimate Generation", f"Unable to save current game before batch:\n{exc}")
            return

        batch["started"] = True
        batch["running"] = True
        batch["paused"] = False
        batch["finished"] = False
        batch["current_index"] = 0
        batch["generated"] = 0
        batch["failures"] = []
        batch["original_index"] = self.index
        batch["original_layout"] = self.export_layout_var.get()
        batch["status_var"].set("Starting batch from the first ROM...")
        batch["count_var"].set(f"0 / {len(self.roms)}")
        batch["layout_status_var"].set(
            "Mode: Auto per game" if batch["mode_var"].get() == "auto" else f"Mode: Fixed {batch['layout_var'].get()}"
        )
        batch["export_selection"] = export_selection
        batch["skipped"] = 0
        batch["progress"]["value"] = 0
        self.update_ultimate_modal_controls()
        batch["window"].update_idletasks()
        self.schedule_ultimate_generation_step(10)

    def schedule_ultimate_generation_step(self, delay_ms=10):
        batch = self._ultimate_batch
        if not batch or batch.get("finished") or batch.get("paused"):
            return
        after_id = self.root.after(delay_ms, self.run_ultimate_generation_step)
        batch["after_id"] = after_id

    def stop_ultimate_generation_batch(self):
        batch = self._ultimate_batch
        if not batch or not batch.get("running"):
            return
        batch["paused"] = True
        batch["running"] = False
        after_id = batch.get("after_id")
        if after_id:
            try:
                self.root.after_cancel(after_id)
            except Exception:
                pass
            batch["after_id"] = None
        batch["status_var"].set("Batch paused.")
        self.update_ultimate_modal_controls()

    def resume_ultimate_generation_batch(self):
        batch = self._ultimate_batch
        if not batch or not batch.get("paused") or batch.get("finished"):
            return
        batch["paused"] = False
        batch["running"] = True
        batch["status_var"].set("Resuming batch...")
        self.update_ultimate_modal_controls()
        self.schedule_ultimate_generation_step(10)

    def finish_ultimate_generation_batch(self):
        batch = self._ultimate_batch
        if not batch:
            return
        batch["running"] = False
        batch["paused"] = False
        batch["finished"] = True
        batch["after_id"] = None
        self.index = min(max(batch.get("original_index", 0), 0), max(len(self.roms) - 1, 0))
        self.load_current()
        self.set_export_layout(batch.get("original_layout", "4-Button"))
        total = len(self.roms)
        generated = batch.get("generated", 0)
        failures = batch.get("failures", [])
        skipped = batch.get("skipped", 0)
        if failures:
            preview = "\n".join(failures[:8])
            more = "" if len(failures) <= 8 else f"\n... and {len(failures) - 8} more."
            batch["status_var"].set(f"Completed with {len(failures)} failure(s).")
            self.update_ultimate_modal_controls()
            messagebox.showwarning(
                "Ultimate Generation",
                f"{generated} generated, {skipped} skipped, {len(failures)} failed.\n\nFailures:\n{preview}{more}",
            )
            return
        batch["status_var"].set("Completed successfully." if not skipped else "Completed with skipped games.")
        self.update_ultimate_modal_controls()
        messagebox.showinfo(
            "Ultimate Generation",
            f"{generated} games generated successfully from the first ROM to the last." + (f"\n{skipped} games skipped." if skipped else ""),
        )

    def run_ultimate_generation_step(self):
        batch = self._ultimate_batch
        if not batch or batch.get("paused") or batch.get("finished"):
            return
        batch["after_id"] = None
        idx = batch.get("current_index", 0)
        total = len(self.roms)
        if idx >= total:
            self.finish_ultimate_generation_batch()
            return

        rom = self.roms[idx]
        self.index = idx
        self.load_current()
        if batch["mode_var"].get() == "auto":
            layout_name = self.auto_export_layout_for_current_game()
        else:
            layout_name = batch["layout_var"].get()
        if layout_name not in LAYOUTS:
            layout_name = "4-Button"
        self.set_export_layout(layout_name)

        batch["status_var"].set(f"[{idx + 1}/{total}] {rom}")
        batch["layout_status_var"].set(f"Layout: {layout_name}")
        batch["progress"]["value"] = idx
        batch["count_var"].set(f"{idx} / {total}")
        batch["window"].update()
        try:
            result = self.save_current(
                silent=True,
                export_selection=batch.get("export_selection"),
                confirm_richer_json=bool(batch.get("confirm_json_var", tk.BooleanVar(value=False)).get()),
                confirm_parent=batch.get("window"),
                skip_all_if_json_declined=True,
            )
            if result.get("skipped"):
                batch["skipped"] += 1
                batch["status_var"].set(f"[{idx + 1}/{total}] {rom} skipped")
            else:
                batch["generated"] += 1
        except Exception as exc:
            batch["failures"].append(f"{rom}: {exc}")
        batch["current_index"] = idx + 1
        batch["progress"]["value"] = idx + 1
        batch["count_var"].set(f"{idx + 1} / {total}")
        batch["window"].update()
        if batch.get("paused"):
            self.update_ultimate_modal_controls()
            return
        self.schedule_ultimate_generation_step(10)

    def copy_p1_to_others(self, source_editor):
        if source_editor.player_data["player"] != 1:
            return

        src_buttons = {b["game_button"]: b for b in source_editor.player_data["buttons"]}
        src_axes = {a.get("id"): a for a in source_editor.player_data.get("axes", []) if isinstance(a, dict)}
        src_outputs = {
            source_editor.system_output_key(out): out
            for out in source_editor.player_data.get("system_outputs", [])
            if isinstance(out, dict)
        }
        src_pattern = source_editor.pattern_var.get()

        for editor in self.player_editors:
            if editor is source_editor:
                continue

            tgt_buttons = {b["game_button"]: b for b in editor.player_data["buttons"]}
            editor.pattern_var.set(src_pattern)

            for key, src_entry in source_editor.player_data.get("system_inputs", {}).items():
                tgt_entry = editor.player_data.get("system_inputs", {}).get(key)
                if not tgt_entry:
                    continue
                tgt_entry["color"] = src_entry.get("color", tgt_entry.get("color", ""))
                tgt_entry["output"] = src_entry.get("output", tgt_entry.get("output", ""))
                tgt_entry["panel_joystick"] = src_entry.get("panel_joystick")
                tgt_entry.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                for layout_name in LAYOUTS:
                    tgt_entry["slots_by_layout"][layout_name] = src_entry.get("slots_by_layout", {}).get(layout_name)

            tgt_outputs = {
                editor.system_output_key(out): out
                for out in editor.player_data.get("system_outputs", [])
                if isinstance(out, dict)
            }
            for output_key, src_output in src_outputs.items():
                tgt_output = tgt_outputs.get(output_key)
                if not tgt_output:
                    continue
                editor.ensure_system_output_entry(tgt_output)
                tgt_output["color"] = src_output.get("color", tgt_output.get("color", "Gray"))
                tgt_output["input_ref"] = src_output.get("input_ref", tgt_output.get("input_ref", ""))
                tgt_output["panel_joystick"] = src_output.get("panel_joystick")
                for layout_name in LAYOUTS:
                    tgt_output["slots_by_layout"][layout_name] = src_output.get("slots_by_layout", {}).get(layout_name)

            for idx, srcb in src_buttons.items():
                tgtb = tgt_buttons.get(idx)
                if not tgtb:
                    continue
                for layout_name in LAYOUTS:
                    tgtb["slots_by_layout"][layout_name] = srcb["slots_by_layout"][layout_name]
                tgtb["panel_joystick"] = srcb.get("panel_joystick")

            for dev_idx, src_dev in enumerate(source_editor.player_data["devices"]):
                if dev_idx >= len(editor.player_data["devices"]):
                    continue
                tgt_dev = editor.player_data["devices"][dev_idx]
                for direction, src_entry in src_dev.get("inputs", {}).items():
                    tgt_entry = tgt_dev.get("inputs", {}).get(direction)
                    if not tgt_entry:
                        continue
                    for layout_name in LAYOUTS:
                        tgt_entry.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                        tgt_entry["slots_by_layout"][layout_name] = src_entry.get("slots_by_layout", {}).get(layout_name)
                    tgt_entry["panel_joystick"] = src_entry.get("panel_joystick")

            tgt_axes = {a.get("id"): a for a in editor.player_data.get("axes", []) if isinstance(a, dict)}
            for axis_id, src_axis in src_axes.items():
                tgt_axis = tgt_axes.get(axis_id)
                if not tgt_axis:
                    continue
                tgt_axis["physical_joystick"] = src_axis.get("physical_joystick", "")
                tgt_axis["joystick"] = {
                    "negative": src_axis.get("joystick", {}).get("negative", ""),
                    "positive": src_axis.get("joystick", {}).get("positive", ""),
                }
                tgt_axis["physical_axis"] = src_axis.get("physical_axis", "")
                tgt_axis["panel_joystick"] = src_axis.get("panel_joystick")
                for layout_name in LAYOUTS:
                    tgt_axis.setdefault("slots_by_layout", {layout: None for layout in LAYOUTS})
                    tgt_axis["slots_by_layout"][layout_name] = src_axis.get("slots_by_layout", {}).get(layout_name)
                    tgt_axis.setdefault("slots_by_polarity", {
                        "negative": {layout: None for layout in LAYOUTS},
                        "positive": {layout: None for layout in LAYOUTS},
                    })
                    for polarity in ("negative", "positive"):
                        tgt_axis["slots_by_polarity"].setdefault(polarity, {})
                        tgt_axis["slots_by_polarity"][polarity][layout_name] = src_axis.get("slots_by_polarity", {}).get(polarity, {}).get(layout_name)

            editor.refresh()
        self.schedule_ledpanel_sync()

    def on_editor_state_changed(self, editor=None):
        self.schedule_ledpanel_sync()

    def current_player_editor(self):
        if not self.player_editors:
            return None
        if not hasattr(self, "notebook") or not self.notebook.tabs():
            return self.player_editors[0]
        try:
            current_tab = self.notebook.select()
            current_index = self.notebook.index(current_tab)
        except Exception:
            current_index = 0
        if 0 <= current_index < len(self.player_editors):
            return self.player_editors[current_index]
        return self.player_editors[0]

    def build_ledpanel_slot_colors(self):
        editor = self.current_player_editor()
        if not editor:
            return {}
        layout_name = editor.active_layout if editor.active_layout in LAYOUTS else self.export_layout_var.get()
        assigned = editor.assigned_map_for_layout(layout_name)
        return {
            str(slot): ledpanel_color_name(data.get("color", "White"), default="WHITE")
            for slot, data in assigned.items()
            if slot is not None
        }

    def update_ledpanel_indicator(self, connected, port="", detail=""):
        color = "#16a34a" if connected else "#dc2626"
        text = "LP"
        if port:
            text += f" ({port})"
        if not connected:
            text = "LP"
        self.ledpanel_status_var.set(text)
        if hasattr(self, "ledpanel_indicator"):
            self.ledpanel_indicator.itemconfigure(self.ledpanel_indicator_dot, fill=color, outline=color)

    def schedule_ledpanel_sync(self, delay_ms=160):
        if self._ledpanel_sync_job:
            try:
                self.root.after_cancel(self._ledpanel_sync_job)
            except Exception:
                pass
        self._ledpanel_sync_job = self.root.after(delay_ms, self.sync_ledpanel_now)

    def sync_ledpanel_now(self):
        self._ledpanel_sync_job = None
        slot_colors = self.build_ledpanel_slot_colors()
        connected, port, detail = self.ledpanel.send_slots(slot_colors)
        self.update_ledpanel_indicator(connected, port=port, detail=detail)

    def refresh_ledpanel_status(self):
        connected, port, detail = self.ledpanel.probe()
        self.update_ledpanel_indicator(connected, port=port, detail=detail)
        self.root.after(5000, self.refresh_ledpanel_status)

    def slot_values_for_export(self, slot_value):
        if slot_value is None:
            return []
        if isinstance(slot_value, (list, tuple, set)):
            return [slot for slot in slot_value if slot is not None]
        return [slot_value]

    def configured_mame_cfg_joycode_base_player(self):
        raw = (MAME_CFG_JOYCODE_PLAYER or "auto").strip().lower()
        if raw and raw != "auto":
            try:
                return max(1, int(raw))
            except ValueError:
                pass

        rom = (self.current_data or {}).get("system", "")
        detected = self.detect_mame_cfg_joycode_base_player(rom)
        if detected:
            return detected

        if self.es_input_has_arcade_panel():
            return max(1, MAME_CFG_JOYCODE_FALLBACK_PLAYER)
        return max(1, MAME_CFG_JOYCODE_FALLBACK_PLAYER)

    def mame_cfg_joycode_player(self, game_player=1):
        try:
            player_index = max(1, int(game_player))
        except (TypeError, ValueError):
            player_index = 1
        mapping = self.mame_player_devices.get("players", {}).get(str(player_index))
        if isinstance(mapping, dict):
            mapped_number = mame_joycode_number(mapping.get("joycode"))
            if mapped_number:
                return mapped_number
        return self.configured_mame_cfg_joycode_base_player() + player_index - 1

    def detect_mame_cfg_joycode_base_player(self, rom):
        if not rom:
            return None
        path = os.path.join(MAME_GAME_CFG_DIR, f"{rom}.cfg")
        if not os.path.exists(path):
            return None
        try:
            tree = ET.parse(path)
        except Exception:
            return None

        counts = {}
        for newseq in tree.getroot().findall(".//input/port/newseq"):
            text = "".join(newseq.itertext() or "")
            for match in re.finditer(r"\bJOYCODE_(\d+)_BUTTON\d+\b", text):
                player = int(match.group(1))
                counts[player] = counts.get(player, 0) + 1
        if not counts:
            return None
        return sorted(counts.items(), key=lambda item: (-item[1], item[0]))[0][0]

    def es_input_has_arcade_panel(self):
        if not ES_INPUT_CFG or not os.path.exists(ES_INPUT_CFG):
            return False
        try:
            tree = ET.parse(ES_INPUT_CFG)
        except Exception:
            return False
        for input_config in tree.getroot().findall("inputConfig"):
            if (input_config.attrib.get("type") or "").lower() != "joystick":
                continue
            name = (input_config.attrib.get("deviceName") or "").lower()
            if "circuitpython hid" in name or "generic usb joystick" in name:
                return True
        return False

    def mame_slot_joycode(self, slot, player=1):
        joycode = MAME_SLOT_JOYCODES.get(str(slot))
        return joycode.format(player=player) if joycode else None

    def mame_cfg_slot_for_layout(self, slot, layout_name):
        key = str(slot)
        if layout_name in ("4-Button", "6-Button", "8-Button"):
            if key == "3":
                return "4"
            if key == "4":
                return "3"
        return key

    def slot_keycodes(self, slot_value, player=1, layout_name=None):
        slots = self.slot_values_for_export(slot_value)
        tokens = []
        for slot in slots:
            key = self.mame_cfg_slot_for_layout(slot, layout_name)
            keycode = MAME_SLOT_KEYCODES.get(key)
            joycode = self.mame_slot_joycode(key, player)
            if MAME_CFG_INCLUDE_BUTTON_KEYCODES and keycode and keycode not in tokens:
                tokens.append(keycode)
            if joycode:
                if joycode not in tokens:
                    tokens.append(joycode)
        return tokens

    def slot_mame_types(self, slot_value, player=1):
        return [
            f"P{player}_BUTTON{slot}"
            for slot in self.slot_values_for_export(slot_value)
            if str(slot).isdigit()
        ]

    def joystick_keycodes(self, direction, player=1):
        tokens = []
        keycode = MAME_JOYSTICK_KEYCODES.get(direction)
        joycode = MAME_JOYSTICK_JOYCODES.get(direction)
        if keycode:
            tokens.append(keycode)
        if joycode:
            tokens.append(joycode.format(player=player))
        return tokens

    def system_input_tokens(self, key, player=1):
        tokens = []
        keycode = MAME_SYSTEM_KEYCODES.get(key)
        joycode = MAME_SYSTEM_JOYCODES.get(key)
        if keycode:
            tokens.append(keycode)
        if joycode:
            tokens.append(joycode.format(player=player))
        return tokens

    def joystick_retropad_id(self, direction):
        mapping = JOYSTICK_DIRECTION_MAP.get(direction, {})
        return mapping.get("retropad_id")

    def rmp_key_from_slot(self, slot, layout_name):
        layout_map = RMP_SLOT_BUTTONS_BY_LAYOUT.get(layout_name) or {}
        return layout_map.get(str(slot))

    def rmp_keys_from_slot_value(self, slot_value, layout_name):
        keys = []
        for slot in self.slot_values_for_export(slot_value):
            key = self.rmp_key_from_slot(slot, layout_name)
            if key and key not in keys:
                keys.append(key)
        return keys

    def rmp_keys_from_button(self, button, layout_name):
        button_number = None
        mame = button.get("mame", {}) if isinstance(button, dict) else {}
        raw = str((mame or {}).get("type") or (mame or {}).get("input_id") or "").upper()
        match = re.search(r"(?:P\d+_)?BUTTON(\d+)$", raw)
        if match:
            button_number = match.group(1)
        else:
            game_button = str((button or {}).get("game_button") or "")
            if game_button.isdigit():
                button_number = game_button

        key = self.rmp_key_from_slot(button_number, layout_name) if button_number else None
        return [key] if key else []

    def rmp_default_retropad_id_from_button(self, button):
        return logical_button_source_retropad_id(button)

    def rmp_retropad_id_from_mame(self, mame):
        source_rp = mame_source_retropad_id(mame)
        if source_rp is not None:
            return source_rp
        raw = str((mame or {}).get("type") or (mame or {}).get("input_id") or "").upper()
        match = re.search(r"(?:P\d+_)?JOYSTICK_?(UP|DOWN|LEFT|RIGHT)$", raw)
        if match:
            return self.joystick_retropad_id(match.group(1).lower())
        return None

    def rmp_system_source_value(self, player, system_key):
        retropad_id = system_source_retropad_id(system_key)
        return None if retropad_id is None else str(retropad_id)

    def rmp_add_assignment(self, remaps, conflicts, player, button_key, retropad_id, source):
        if not button_key or retropad_id is None:
            return
        key = f"input_player{player}_btn_{button_key}"
        value = str(retropad_id)
        existing = remaps.get(key)
        if existing is None:
            remaps[key] = value
        elif existing != value:
            conflicts.append(f"{key}: {existing} kept, {value} ignored ({source})")

    def rmp_add_slot_destinations(self, remaps, conflicts, player, slot_value, layout_name, source_retropad_id, source):
        keys = self.rmp_keys_from_slot_value(slot_value, layout_name)
        if not keys:
            return
        self.rmp_add_assignment(remaps, conflicts, player, keys[0], source_retropad_id, source)
        for extra_key in keys[1:]:
            self.rmp_add_assignment(remaps, conflicts, player, extra_key, source_retropad_id, source)

    def rmp_add_system_default(self, remaps, conflicts, player, system_key, button_key, source):
        self.rmp_add_assignment(remaps, conflicts, player, button_key, self.rmp_system_source_value(player, system_key), source)

    def rmp_add_panel_joystick_destinations(self, remaps, conflicts, player, item, source_retropad_id, source):
        value = item.get("panel_joystick")
        if not value:
            return
        directions = value.values() if isinstance(value, dict) else [value]
        for direction in directions:
            key = direction if direction in JOYSTICK_DIRECTION_MAP else None
            self.rmp_add_assignment(remaps, conflicts, player, key, source_retropad_id, source)

    def collect_retroarch_rmp_assignments(self):
        remaps = {}
        conflicts = []
        export_layout = self.export_layout_var.get()
        if export_layout not in LAYOUTS:
            export_layout = "4-Button"

        for editor in self.player_editors:
            layout_name = export_layout
            player = editor.player_data.get("player", 1)

            for key in editor.system_input_order():
                entry = editor.player_data["system_inputs"][key]
                editor.ensure_system_input_entry(key, entry)
                button_key = RMP_SYSTEM_BUTTON_MAP.get(key) or SYSTEM_SLOT_MAP.get(key, {}).get("rmp_button")
                source_retropad_id = self.rmp_system_source_value(player, key)
                self.rmp_add_system_default(remaps, conflicts, player, key, button_key, f"system input {key}")
                self.rmp_add_panel_joystick_destinations(remaps, conflicts, player, entry, source_retropad_id, f"system input {key}")

            for button in editor.player_data["buttons"]:
                source = f'button {button.get("game_button")}'
                source_retropad_id = self.rmp_default_retropad_id_from_button(button)
                button_keys = self.rmp_keys_from_button(button, layout_name)
                if button_keys:
                    for button_key in button_keys:
                        self.rmp_add_assignment(remaps, conflicts, player, button_key, source_retropad_id, source)
                else:
                    self.rmp_add_slot_destinations(remaps, conflicts, player, button.get("slots_by_layout", {}).get(layout_name), layout_name, source_retropad_id, source)
                self.rmp_add_panel_joystick_destinations(remaps, conflicts, player, button, source_retropad_id, source)

            for _, direction, entry in editor.iter_joystick_inputs():
                source = f"joystick {direction}"
                source_retropad_id = self.joystick_retropad_id(direction)
                self.rmp_add_slot_destinations(remaps, conflicts, player, entry.get("slots_by_layout", {}).get(layout_name), layout_name, source_retropad_id, source)
                self.rmp_add_panel_joystick_destinations(remaps, conflicts, player, entry, source_retropad_id, source)

            for axis in editor.iter_axes():
                source = axis.get("id") or axis.get("input") or "axis"
                source_retropad_id = self.rmp_retropad_id_from_mame(axis.get("mame", {}))
                self.rmp_add_slot_destinations(remaps, conflicts, player, axis.get("slots_by_layout", {}).get(layout_name), layout_name, source_retropad_id, source)
                self.rmp_add_panel_joystick_destinations(remaps, conflicts, player, axis, source_retropad_id, source)

            for output in editor.player_data.get("system_outputs", []):
                editor.ensure_system_output_entry(output)
                if not output.get("input_ref"):
                    continue
                mame = editor.mame_for_system_output_input(output)
                source = f'system output {output.get("name") or output.get("id") or ""}'.strip()
                source_retropad_id = self.rmp_retropad_id_from_mame(mame)
                self.rmp_add_slot_destinations(remaps, conflicts, player, output.get("slots_by_layout", {}).get(layout_name), layout_name, source_retropad_id, source)
                self.rmp_add_panel_joystick_destinations(remaps, conflicts, player, output, source_retropad_id, source)

        return remaps, conflicts

    def build_retroarch_rmp_text(self):
        remaps, conflicts = self.collect_retroarch_rmp_assignments()
        if not remaps:
            return "", conflicts

        player_numbers = sorted({
            int(editor.player_data.get("player", 1))
            for editor in self.player_editors
            if str(editor.player_data.get("player", 1)).isdigit()
        })
        max_player = max(player_numbers or [1])
        max_player = max(4, max_player)

        lines = []
        for player in range(1, max_player + 1):
            lines.append(f'input_libretro_device_p{player} = "1"')

        for player in range(1, max_player + 1):
            lines.append(f'input_player{player}_analog_dpad_mode = "0"')
            prefix = f"input_player{player}_btn_"
            player_keys = sorted(
                [key for key in remaps if key.startswith(prefix)],
                key=natural_sort_key,
            )
            if not player_keys:
                continue

            suffixes = {key[len(prefix):] for key in player_keys}
            if any(suffix in suffixes for suffix in RMP_BUTTON_OUTPUT_ORDER):
                for suffix in RMP_BUTTON_OUTPUT_ORDER:
                    key = f"{prefix}{suffix}"
                    lines.append(f'{key} = "{remaps.get(key, "-1")}"')

            ordered_standard_keys = {f"{prefix}{suffix}" for suffix in RMP_BUTTON_OUTPUT_ORDER}
            for key in player_keys:
                if key in ordered_standard_keys:
                    continue
                lines.append(f'{key} = "{remaps[key]}"')

        for player in range(1, max_player + 1):
            lines.append(f'input_remap_port_p{player} = "{player - 1}"')
        lines.extend(RMP_STATIC_LINES)
        return "\n".join(lines) + "\n", conflicts

    def add_cfg_assignment(self, port_map, mame, seq_type, token):
        identity = mame_port_identity(mame)
        if not identity or not token:
            return
        entry = port_map.setdefault(identity, {
            "standard": [],
            "increment": [],
            "decrement": [],
        })
        tokens = entry.setdefault(seq_type, [])
        if token not in tokens:
            tokens.append(token)

    def add_cfg_item_slots(self, port_map, item, layout_name, player=1, mame=None):
        mame = mame or item.get("mame", {})
        for token in self.slot_keycodes(item.get("slots_by_layout", {}).get(layout_name), player, layout_name):
            self.add_cfg_assignment(port_map, mame, "standard", token)

    def add_cfg_axis_slot(self, port_map, axis, layout_name, player=1):
        slot_value = axis.get("slots_by_layout", {}).get(layout_name)
        if not slot_value:
            return

        direction = axis.get("panel_joystick")
        if isinstance(direction, dict):
            direction = direction.get("negative") or direction.get("positive") or ""
        seq_type = mame_axis_sequence_type_for_item(axis, direction) if direction else "standard"
        for token in self.slot_keycodes(slot_value, player, layout_name):
            self.add_cfg_assignment(port_map, axis.get("mame", {}), seq_type, token)

    def add_cfg_panel_joystick(self, port_map, item, player=1, mame=None, analog=False):
        mame = mame or item.get("mame", {})
        value = item.get("panel_joystick")
        if not value:
            return
        if isinstance(value, dict):
            for polarity, direction in value.items():
                seq_type = "decrement" if polarity == "negative" else "increment"
                for token in self.joystick_keycodes(direction, player):
                    self.add_cfg_assignment(port_map, mame, seq_type, token)
            return

        seq_type = mame_axis_sequence_type_for_item(item, value) if analog else "standard"
        for token in self.joystick_keycodes(value, player):
            self.add_cfg_assignment(port_map, mame, seq_type, token)

    def collect_mame_cfg_assignments(self):
        port_map = {}
        export_layout = self.export_layout_var.get()
        if export_layout not in LAYOUTS:
            export_layout = "4-Button"
        for editor in self.player_editors:
            layout_name = export_layout
            player = editor.player_data.get("player", 1)
            cfg_player = self.mame_cfg_joycode_player(player)

            for key in editor.system_input_order():
                entry = editor.player_data["system_inputs"][key]
                editor.ensure_system_input_entry(key, entry)
                if not entry.get("slots_by_layout", {}).get(layout_name):
                    for token in self.system_input_tokens(key, cfg_player):
                        self.add_cfg_assignment(port_map, entry.get("mame", {}), "standard", token)
                self.add_cfg_item_slots(port_map, entry, layout_name, cfg_player)
                self.add_cfg_panel_joystick(port_map, entry, cfg_player)

            for button in editor.player_data["buttons"]:
                self.add_cfg_item_slots(port_map, button, layout_name, cfg_player)
                self.add_cfg_panel_joystick(port_map, button, cfg_player)

            for _, _, entry in editor.iter_joystick_inputs():
                self.add_cfg_item_slots(port_map, entry, layout_name, cfg_player)
                self.add_cfg_panel_joystick(port_map, entry, cfg_player)

            for axis in editor.iter_axes():
                for polarity, seq_type in (("negative", "decrement"), ("positive", "increment")):
                    slot_value = axis.get("slots_by_polarity", {}).get(polarity, {}).get(layout_name)
                    for token in self.slot_keycodes(slot_value, cfg_player, layout_name):
                        self.add_cfg_assignment(port_map, axis.get("mame", {}), seq_type, token)
                self.add_cfg_axis_slot(port_map, axis, layout_name, cfg_player)
                self.add_cfg_panel_joystick(port_map, axis, cfg_player, analog=True)

            for output in editor.player_data.get("system_outputs", []):
                editor.ensure_system_output_entry(output)
                if not output.get("input_ref"):
                    continue
                mame = editor.mame_for_system_output_input(output)
                for token in self.slot_keycodes(output.get("slots_by_layout", {}).get(layout_name), cfg_player, layout_name):
                    self.add_cfg_assignment(port_map, mame, "standard", token)
                self.add_cfg_panel_joystick(port_map, output, cfg_player, mame=mame)

        return port_map

    def build_mame_cfg_tree(self):
        rom = self.current_data["system"]
        port_map = self.collect_mame_cfg_assignments()
        root = ET.Element("mameconfig", {"version": "10"})
        system = ET.SubElement(root, "system", {"name": rom})
        input_node = ET.SubElement(system, "input")

        for tag, mtype, mask_dec, defvalue_dec in sorted(port_map, key=lambda x: (x[0], x[1], x[2], x[3])):
            seqs = port_map[(tag, mtype, mask_dec, defvalue_dec)]
            if not any(seqs.values()):
                continue
            port = ET.SubElement(input_node, "port", {
                "tag": tag,
                "type": mtype,
                "mask": str(mask_dec),
                "defvalue": str(defvalue_dec),
            })
            for seq_type in ("standard", "increment", "decrement"):
                tokens = seqs.get(seq_type, [])
                if not tokens:
                    continue
                newseq = ET.SubElement(port, "newseq", {"type": seq_type})
                newseq.text = " OR ".join(tokens)

        mame_xml_indent(root)
        return ET.ElementTree(root), len(input_node.findall("port"))

    def mame_cfg_port_identity_from_xml(self, port):
        return (
            port.attrib.get("tag", ""),
            port.attrib.get("type", ""),
            str(parse_int_value(port.attrib.get("mask", "0"))),
            str(parse_int_value(port.attrib.get("defvalue", "0"))),
        )

    def merge_existing_mame_cfg_tree(self, path, generated_tree):
        if not os.path.exists(path):
            return generated_tree
        try:
            existing_tree = ET.parse(path)
        except Exception:
            return generated_tree

        rom = self.current_data["system"]
        root = existing_tree.getroot()
        system = root.find(f"./system[@name='{rom}']")
        if system is None:
            system = root.find("./system")
        if system is None:
            system = ET.SubElement(root, "system", {"name": rom})

        input_node = system.find("input")
        if input_node is None:
            input_node = ET.SubElement(system, "input")

        generated_input = generated_tree.getroot().find("./system/input")
        generated_ports = list(generated_input.findall("port")) if generated_input is not None else []
        generated_identities = {
            self.mame_cfg_port_identity_from_xml(port)
            for port in generated_ports
        }

        for port in list(input_node.findall("port")):
            if self.mame_cfg_port_identity_from_xml(port) in generated_identities:
                input_node.remove(port)
        for port in generated_ports:
            input_node.append(copy.deepcopy(port))

        mame_xml_indent(root)
        return existing_tree

    def backup_runtime_file_to_versioning(self, path, label):
        if not path or not os.path.exists(path):
            return
        try:
            stamp = datetime_stamp()
        except Exception:
            stamp = "runtime"
        backup_dir = os.path.join(BASE_DIR, ".versioning", f"{stamp}-{label}")
        os.makedirs(backup_dir, exist_ok=True)
        shutil.copy2(path, os.path.join(backup_dir, os.path.basename(path)))

    def build_mame_ctrlr_tree(self):
        root = ET.Element("mameconfig", {"version": "10"})
        system = ET.SubElement(root, "system", {"name": "default"})
        input_node = ET.SubElement(system, "input")
        mappings = []
        for player_key, mapping in sorted(
            self.mame_player_devices.get("players", {}).items(),
            key=lambda item: natural_sort_key(str(item[0])),
        ):
            if not isinstance(mapping, dict):
                continue
            joycode = str(mapping.get("joycode") or "").upper()
            device = str(mapping.get("device") or "").strip()
            if not joycode or not device:
                continue
            mappings.append((device, joycode))
        seen = set()
        for device, joycode in mappings:
            key = (device, joycode)
            if key in seen:
                continue
            seen.add(key)
            ET.SubElement(input_node, "mapdevice", {
                "device": device,
                "controller": joycode,
            })
        mame_xml_indent(root)
        return ET.ElementTree(root), len(seen)

    def write_mame_ctrlr_file(self):
        os.makedirs(MAME_CTRLR_DIR, exist_ok=True)
        path = os.path.join(MAME_CTRLR_DIR, f"{MAME_CTRLR_NAME}.cfg")
        if os.path.exists(path):
            self.backup_runtime_file_to_versioning(path, "before-mame-controls-mapping")
        tree, count = self.build_mame_ctrlr_tree()
        tree.write(path, encoding="utf-8", xml_declaration=True)
        return {"path": path, "mapdevice_count": count}

    def ensure_mame_ini_ctrlr(self):
        os.makedirs(MAME_INI_DIR, exist_ok=True)
        path = os.path.join(MAME_INI_DIR, "mame.ini")
        desired = f"ctrlr                    {MAME_CTRLR_NAME}"
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
        else:
            text = ""
        if re.search(r"(?im)^\s*ctrlr\s+" + re.escape(MAME_CTRLR_NAME) + r"\s*$", text):
            return {"path": path, "changed": False}
        if os.path.exists(path):
            self.backup_runtime_file_to_versioning(path, "before-mame-ini-ctrlr")
        if re.search(r"(?im)^\s*ctrlr\s+", text):
            new_text = re.sub(r"(?im)^\s*ctrlr\s+.*$", desired, text, count=1)
        else:
            new_text = text.rstrip() + ("\n\n" if text.strip() else "") + desired + "\n"
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new_text)
        return {"path": path, "changed": True}

    def ensure_mame_ctrlr_export(self):
        ctrlr_result = self.write_mame_ctrlr_file()
        ini_result = self.ensure_mame_ini_ctrlr()
        return {
            "ctrlr": ctrlr_result,
            "ini": ini_result,
        }

    def save_mame_cfg_export(self):
        tree, port_count = self.build_mame_cfg_tree()
        if port_count == 0:
            return None

        rom = self.current_data["system"]
        self.ensure_mame_ctrlr_export()
        path = os.path.join(MAME_GAME_CFG_DIR, f"{rom}.cfg")
        os.makedirs(MAME_GAME_CFG_DIR, exist_ok=True)
        tree = self.merge_existing_mame_cfg_tree(path, tree)
        tree.write(path, encoding="utf-8", xml_declaration=True)

        os.makedirs(GENERATED_MAME_COPY_DIR, exist_ok=True)
        copy_path = os.path.join(GENERATED_MAME_COPY_DIR, f"{rom}.cfg")
        shutil.copy2(path, copy_path)
        return {"path": path, "copy_path": copy_path, "port_count": port_count}

    def generate_mame_cfg(self):
        tree, port_count = self.build_mame_cfg_tree()
        if port_count == 0:
            messagebox.showwarning("MAME CFG", "No mapped MAME input found for the active layouts.")
            return

        rom = self.current_data["system"]
        path = os.path.join(MAME_GAME_CFG_DIR, f"{rom}.cfg")
        if os.path.exists(path):
            if not messagebox.askyesno("MAME CFG", f"Overwrite existing MAME cfg?\n\n{path}"):
                return

        self.ensure_mame_ctrlr_export()
        os.makedirs(MAME_GAME_CFG_DIR, exist_ok=True)
        tree = self.merge_existing_mame_cfg_tree(path, tree)
        tree.write(path, encoding="utf-8", xml_declaration=True)
        os.makedirs(GENERATED_MAME_COPY_DIR, exist_ok=True)
        copy_path = os.path.join(GENERATED_MAME_COPY_DIR, f"{rom}.cfg")
        shutil.copy2(path, copy_path)

        if messagebox.askyesno(
            "MAME CFG",
            f"Controller CFG generated with {port_count} input ports:\n{path}\n\nCopy saved to:\n{copy_path}\n\nLaunch MAME now for testing?",
        ):
            self.launch_mame_current()

    def launch_mame_current(self):
        rom = self.current_data["system"]
        if not os.path.exists(MAME_EXE):
            messagebox.showerror("MAME", f"MAME executable not found:\n{MAME_EXE}")
            return
        try:
            subprocess.Popen([
                MAME_EXE,
                rom,
                "-rompath", ROMS_DIR,
                "-inipath", MAME_INI_DIR,
                "-cfg_directory", MAME_GAME_CFG_DIR,
                "-ctrlrpath", MAME_CTRLR_DIR,
                "-ctrlr", MAME_CTRLR_NAME,
                "-joystick",
            ])
        except Exception as exc:
            messagebox.showerror("MAME", f"Unable to launch MAME:\n{exc}")

    def launch_retroarch_current(self):
        rom = self.current_data["system"]
        if not os.path.exists(RETROARCH_EXE):
            messagebox.showerror("RetroArch", f"RetroArch executable not found:\n{RETROARCH_EXE}")
            return
        if not os.path.exists(RETROARCH_MAME_CORE):
            messagebox.showerror("RetroArch", f"RetroArch MAME core not found:\n{RETROARCH_MAME_CORE}")
            return

        rom_path = find_rom_content_path(rom)
        if not rom_path:
            messagebox.showerror("RetroArch", f"ROM file not found in:\n{ROMS_DIR}\n\nROM: {rom}")
            return

        fmt = {
            "rom": rom,
            "rom_path": rom_path,
            "roms_dir": ROMS_DIR,
            "bios_dir": RETROBAT_BIOS_DIR,
            "cheats_dir": MAME_CHEATS_DIR,
            "artwork_dir": MAME_ARTWORK_DIR,
            "log_file": RETROARCH_LOG_FILE,
        }

        content_arg = RETROARCH_CONTENT_TEMPLATE.format(**fmt).strip() if RETROARCH_CONTENT_TEMPLATE else rom_path
        cmd = [RETROARCH_EXE]
        if RETROARCH_LOG_FILE:
            cmd.extend(["--log-file", RETROARCH_LOG_FILE])
        if RETROARCH_EXTRA_ARGS:
            cmd.extend(shlex.split(RETROARCH_EXTRA_ARGS, posix=False))
        cmd.extend(["-L", RETROARCH_MAME_CORE, content_arg])

        try:
            subprocess.Popen(cmd)
        except Exception as exc:
            messagebox.showerror("RetroArch", f"Unable to launch RetroArch:\n{exc}")

    def generate_retroarch_rmp_to(self, title, core_dir, copy_dir, launch_after=False):
        text, conflicts = self.build_retroarch_rmp_text()
        if not text:
            messagebox.showwarning(title, "No mapped RetroArch input found for the active layout.")
            return

        rom = self.current_data["system"]
        target_dir = os.path.join(RETROARCH_REMAPS_DIR, core_dir)
        path = os.path.join(target_dir, f"{rom}.rmp")
        if os.path.exists(path):
            if not messagebox.askyesno(title, f"Overwrite existing RetroArch remap?\n\n{path}"):
                return

        os.makedirs(target_dir, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        os.makedirs(copy_dir, exist_ok=True)
        copy_path = os.path.join(copy_dir, f"{rom}.rmp")
        shutil.copy2(path, copy_path)

        detail = f"RMP generated:\n{path}\n\nCopy saved to:\n{copy_path}"
        if conflicts:
            preview = "\n".join(conflicts[:8])
            more = "" if len(conflicts) <= 8 else f"\n... and {len(conflicts) - 8} more."
            detail += f"\n\nSome remaps could not be represented more than once in {title}:\n{preview}{more}"
        if launch_after and messagebox.askyesno(title, detail + "\n\nLaunch RetroArch now for testing?"):
            self.launch_retroarch_current()
            return
        messagebox.showinfo(title, detail)

    def generate_retroarch_rmp(self):
        text, conflicts = self.build_retroarch_rmp_text()
        if not text:
            messagebox.showwarning("FBNEO-RMP", "No mapped RetroArch input found for the active layout.")
            return

        rom = self.current_data["system"]
        outputs = [
            ("FinalBurn Neo", RETROARCH_FBNEO_RMP_CORE_DIR, GENERATED_RETROARCH_FBNEO_COPY_DIR),
        ]
        details = []

        for label, core_dir, copy_dir in outputs:
            target_dir = os.path.join(RETROARCH_REMAPS_DIR, core_dir)
            path = os.path.join(target_dir, f"{rom}.rmp")
            if os.path.exists(path):
                if not messagebox.askyesno("FBNEO-RMP", f"Overwrite existing RetroArch remap for {label}?\n\n{path}"):
                    continue

            os.makedirs(target_dir, exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)

            os.makedirs(copy_dir, exist_ok=True)
            copy_path = os.path.join(copy_dir, f"{rom}.rmp")
            shutil.copy2(path, copy_path)
            details.append(f"{label}:\n{path}\nCopy:\n{copy_path}")

        if not details:
            return

        detail = "FBNEO-RMP generated:\n\n" + "\n\n".join(details)
        if conflicts:
            preview = "\n".join(conflicts[:8])
            more = "" if len(conflicts) <= 8 else f"\n... and {len(conflicts) - 8} more."
            detail += f"\n\nSome remaps could not be represented more than once in FBNEO-RMP:\n{preview}{more}"
        if messagebox.askyesno("FBNEO-RMP", detail + "\n\nLaunch RetroArch now for testing?"):
            self.launch_retroarch_current()

    def save_retroarch_rmp_exports(self):
        text, conflicts = self.build_retroarch_rmp_text()
        if not text:
            return [], conflicts

        rom = self.current_data["system"]
        outputs = [
            ("FinalBurn Neo", RETROARCH_FBNEO_RMP_CORE_DIR, GENERATED_RETROARCH_FBNEO_COPY_DIR),
        ]
        details = []

        for label, core_dir, copy_dir in outputs:
            target_dir = os.path.join(RETROARCH_REMAPS_DIR, core_dir)
            path = os.path.join(target_dir, f"{rom}.rmp")
            os.makedirs(target_dir, exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)

            os.makedirs(copy_dir, exist_ok=True)
            copy_path = os.path.join(copy_dir, f"{rom}.rmp")
            shutil.copy2(path, copy_path)
            details.append({"label": label, "path": path, "copy_path": copy_path})

        return details, conflicts

    def retrobat_xml_source_layout(self, button_count):
        if button_count <= 2:
            return "2-Button"
        if button_count <= 4:
            return "4-Button"
        if button_count <= 6:
            return "6-Button"
        return "8-Button"

    def retrobat_xml_player_buttons(self, editor):
        by_index = {}
        for button in editor.player_data["buttons"]:
            key = str(button.get("game_button", ""))
            if not key.isdigit():
                continue
            if key not in by_index and mame_port_identity(button.get("mame", {})):
                by_index[key] = button
        return [by_index[key] for key in sorted(by_index, key=lambda value: int(value))]

    def libretro_user_yaml_template_path(self):
        return os.path.join(USER_INPUTMAPPING_DIR, "libretro_mame.yml")

    def resolve_libretro_user_inputmapping_targets(self):
        targets = [{
            "path": self.libretro_user_yaml_template_path(),
            "include_defaults": True,
            "label": "libretro_mame.yml",
        }]

        seen = {targets[0]["path"].lower()}
        meta = self.current_data.get("meta", {}) if self.current_data else {}

        core = slugify_text(meta.get("libretro_core", ""))
        system_name = slugify_text(meta.get("libretro_system", ""))
        if core:
            filename = f"libretro_{core}_{system_name}.yml" if system_name else f"libretro_{core}.yml"
            path = os.path.join(USER_INPUTMAPPING_DIR, filename)
            if path.lower() not in seen:
                targets.append({
                    "path": path,
                    "include_defaults": False,
                    "label": filename,
                })
                seen.add(path.lower())

        return targets

    def parse_simple_yaml_mapping_file(self, path):
        header = []
        mapping = {}
        current_key = None

        try:
            with open(path, "r", encoding="utf-8") as f:
                lines = f.read().splitlines()
        except Exception:
            return header, mapping

        for line in lines:
            stripped = line.strip()
            if current_key is None and (not stripped or stripped.startswith("#")):
                header.append(line)
                continue
            if not stripped or stripped.startswith("#"):
                continue
            if not line.startswith(" ") and stripped.endswith(":"):
                current_key = stripped[:-1]
                mapping.setdefault(current_key, {})
                continue
            if current_key and line.startswith("  ") and ":" in stripped:
                key, value = stripped.split(":", 1)
                value = value.strip()
                parsed = value
                if re.fullmatch(r"-?\d+", value):
                    parsed = int(value)
                mapping[current_key][key.strip()] = parsed
        return header, mapping

    def default_libretro_user_yaml_header(self):
        return [
            "# MAME(current) INPUT REMAPS FOR RETROARCH",
            "#",
            "# This file is generated by panel_curator_ultimate.py",
            "#",
            "# It is used to automatically generate input remaps for Retroarch for MAME games with MAME(current) core",
            "# Each container must be named exactly the same as your game rom file (without the extension)",
            "#",
            "# The elements listed are the buttons for which you want the function to be remapped",
            "# The key represents the controller button code (a = EAST, b = SOUTH, x = NORTH, y = WEST)",
            "# The value represents the original button ID for the specific system/game",
            "# you can use the -1 value to unmap a button",
            "#",
            "# This is the MAME core button numbering",
            "#0 (BUTTON 1)",
            "#8 (BUTTON 2)",
            "#1 (BUTTON 3)",
            "#9 (BUTTON 4)",
            "#10 (BUTTON 5)",
            "#11 (BUTTON 6)",
            "#12 (BUTTON 7)",
            "#13 (BUTTON 8)",
            "#14 (BUTTON 9)",
            "#15 (BUTTON 10)",
        ]

    def libretro_user_yaml_entry_from_buttons(self, buttons, variant_name):
        entry = {"analog_dpad_mode": 0}
        for key in LIBRETRO_USER_YAML_BUTTON_ORDER:
            entry[key] = -1

        sequence = LIBRETRO_USER_YAML_VARIANTS.get(variant_name, [])
        for index, button in enumerate(buttons):
            if index >= len(sequence):
                break
            try:
                game_button = int(button.get("game_button"))
            except Exception:
                continue
            core_button_id = LIBRETRO_MAME_BUTTON_IDS.get(game_button)
            if core_button_id is None:
                continue
            entry[sequence[index]] = core_button_id
        return entry

    def build_libretro_user_yaml_entries(self, include_defaults=True):
        if not self.player_editors:
            return {}

        buttons = self.retrobat_xml_player_buttons(self.player_editors[0])
        if not buttons:
            return {}

        rom = self.current_data["system"]
        entries = {
            rom: self.libretro_user_yaml_entry_from_buttons(buttons, "default"),
            f"{rom}_modern8": self.libretro_user_yaml_entry_from_buttons(buttons, "modern8"),
            f"{rom}_8alternative": self.libretro_user_yaml_entry_from_buttons(buttons, "8alternative"),
            f"{rom}_6alternative": self.libretro_user_yaml_entry_from_buttons(buttons, "6alternative"),
        }
        if include_defaults:
            entries.update({
                "default": self.libretro_user_yaml_entry_from_buttons(buttons, "default"),
                "default_modern8": self.libretro_user_yaml_entry_from_buttons(buttons, "modern8"),
                "default_8alternative": self.libretro_user_yaml_entry_from_buttons(buttons, "8alternative"),
                "default_6alternative": self.libretro_user_yaml_entry_from_buttons(buttons, "6alternative"),
            })
        return entries

    def dump_simple_yaml_mapping_file(self, path, header_lines, mapping):
        lines = list(header_lines or [])
        if lines and lines[-1].strip():
            lines.append("")

        for name, entry in mapping.items():
            lines.append(f"{name}:")
            if isinstance(entry, dict):
                for key, value in entry.items():
                    lines.append(f"  {key}: {value}")
            lines.append("")

        text = "\n".join(lines).rstrip() + "\n"
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)

    def save_libretro_user_inputmapping_export(self):
        results = []
        system_inputmapping_dir = os.path.join(os.path.dirname(os.path.dirname(USER_INPUTMAPPING_DIR)), "system", "resources", "inputmapping")

        for target in self.resolve_libretro_user_inputmapping_targets():
            path = target["path"]
            source_path = path
            if not os.path.exists(source_path):
                fallback = os.path.join(system_inputmapping_dir, os.path.basename(path))
                if os.path.exists(fallback):
                    source_path = fallback

            header, existing = self.parse_simple_yaml_mapping_file(source_path) if os.path.exists(source_path) else ([], {})
            if not header:
                header = self.default_libretro_user_yaml_header()

            updates = self.build_libretro_user_yaml_entries(include_defaults=target.get("include_defaults", False))
            if not updates:
                continue

            merged = dict(existing)
            merged.update(updates)
            self.dump_simple_yaml_mapping_file(path, header, merged)
            results.append({
                "path": path,
                "keys": list(updates.keys()),
                "label": target.get("label") or os.path.basename(path),
            })
        return results

    def generate_libretro_user_inputmapping(self):
        results = self.save_libretro_user_inputmapping_export()
        if not results:
            messagebox.showwarning("LIBRETRO-YML", "No libretro inputmapping YAML could be generated for the current game.")
            return
        detail = "Libretro inputmapping YAML generated:\n\n" + "\n\n".join(
            f"{result['label']}:\n{result['path']}" for result in results
        )
        messagebox.showinfo("LIBRETRO-YML", detail)

    def retrobat_xml_joystick_entries(self, editor):
        out = {}
        for _, direction, entry in editor.iter_joystick_inputs():
            if direction in out or direction not in RETROBAT_XML_JOYSTICK_MAP:
                continue
            if not mame_port_identity(entry.get("mame", {})):
                continue
            out[direction] = entry
        return out

    def retrobat_xml_button_slot(self, button, layout_name):
        slot_value = button.get("slots_by_layout", {}).get(layout_name)
        slots = self.slot_values_for_export(slot_value)
        for slot in slots:
            key = int(slot) if str(slot).isdigit() else slot
            if str(key).strip():
                return str(key)
        ordered = LAYOUT_SLOTS.get(layout_name, [])
        try:
            button_index = max(int(button.get("game_button", 1)) - 1, 0)
        except Exception:
            button_index = 0
        if button_index < len(ordered):
            return str(ordered[button_index])
        return ""

    def retrobat_xml_append_button(self, layout_el, item, mapping_name, slot_name):
        identity = mame_port_identity(item.get("mame", {}))
        if not identity or not mapping_name:
            return
        tag, mtype, mask_dec, defvalue_dec = identity
        attrs = {
            "type": mtype,
            "tag": tag,
            "mask": str(mask_dec),
            "defvalue": str(defvalue_dec),
            "slot": str(slot_name),
            "color": canonical_color_name(item.get("color", "Blue"), "Blue"),
            "function": str(item.get("mame", {}).get("type") or item.get("mame", {}).get("input_id") or item.get("function") or mtype),
        }
        btn_el = ET.SubElement(layout_el, "button", attrs)
        ET.SubElement(btn_el, "mapping", {"type": "standard", "name": mapping_name})

    def build_retrobat_xml_tree(self):
        root = ET.Element("game", {
            "name": self.current_data["system"],
            "rom": self.current_data["system"],
        })
        layouts_el = ET.SubElement(root, "layouts")

        editor_payloads = []
        joystick_colors = []
        for editor in self.player_editors:
            buttons = self.retrobat_xml_player_buttons(editor)
            if not buttons:
                continue
            editor_payloads.append({
                "editor": editor,
                "buttons": buttons,
                "source_layout": self.retrobat_xml_source_layout(len(buttons)),
                "joystick_entries": self.retrobat_xml_joystick_entries(editor),
            })
            if editor.player_data.get("devices"):
                joystick_colors.append(canonical_color_name(editor.player_data["devices"][0].get("color", "Black"), "Black"))

        joystick_color = joystick_colors[0] if joystick_colors else "Black"
        for variant_name, sequence in RETROBAT_XML_LAYOUT_VARIANTS.items():
            layout_el = ET.SubElement(layouts_el, "layout", {
                "type": variant_name,
                "joystickcolor": joystick_color,
            })
            for payload in editor_payloads:
                for direction in ("left", "right", "up", "down"):
                    entry = payload["joystick_entries"].get(direction)
                    if not entry:
                        continue
                    self.retrobat_xml_append_button(layout_el, entry, RETROBAT_XML_JOYSTICK_MAP[direction], direction.upper())

                for index, button in enumerate(payload["buttons"]):
                    if index >= len(sequence):
                        break
                    slot_name = self.retrobat_xml_button_slot(button, payload["source_layout"])
                    self.retrobat_xml_append_button(layout_el, button, sequence[index], slot_name)

        return ET.ElementTree(root)

    def save_retrobat_xml_export(self):
        tree = self.build_retrobat_xml_tree()
        rom = self.current_data["system"]
        path = os.path.join(RETROBAT_INPUTMAPPING_MAME_DIR, f"{rom}.xml")
        os.makedirs(RETROBAT_INPUTMAPPING_MAME_DIR, exist_ok=True)
        mame_xml_indent(tree.getroot())
        tree.write(path, encoding="utf-8", xml_declaration=True)

        os.makedirs(GENERATED_RETROBAT_XML_COPY_DIR, exist_ok=True)
        copy_path = os.path.join(GENERATED_RETROBAT_XML_COPY_DIR, f"{rom}.xml")
        shutil.copy2(path, copy_path)
        return {"path": path, "copy_path": copy_path}

    def generate_retrobat_xml_placeholder(self):
        tree = self.build_retrobat_xml_tree()
        rom = self.current_data["system"]
        target_dir = RETROBAT_INPUTMAPPING_MAME_DIR
        path = os.path.join(target_dir, f"{rom}.xml")
        copy_path = os.path.join(GENERATED_RETROBAT_XML_COPY_DIR, f"{rom}.xml")

        if os.path.exists(path):
            if not messagebox.askyesno("RB-XML", f"Overwrite existing RetroBat mapping XML?\n\n{path}"):
                return

        os.makedirs(target_dir, exist_ok=True)
        mame_xml_indent(tree.getroot())
        tree.write(path, encoding="utf-8", xml_declaration=True)
        os.makedirs(GENERATED_RETROBAT_XML_COPY_DIR, exist_ok=True)
        shutil.copy2(path, copy_path)
        messagebox.showinfo("RB-XML", f"RetroBat XML generated:\n{path}\n\nCopy saved to:\n{copy_path}")

    def generate_libretro_placeholder(self):
        messagebox.showinfo("Libretro", "Libretro export will be implemented after the MAME CFG target.")

    def generate_fbneo_placeholder(self):
        messagebox.showinfo("FBNeo", "FBNeo export will be implemented after the MAME CFG target.")

    def build_export_json(self):
        players_obj = {}
        for editor in self.player_editors:
            pkey, pobj = editor.export_player()
            players_obj[pkey] = pobj

        return {
            "schema": "api_expose.panel.v1",
            "scope": "game",
            "system": "mame",
            "rom": self.current_data["system"],
            "game_name": self.current_data["game_name"],
            "meta": self.current_data["meta"],
            "panel": {
                "convention": PANEL_CONVENTION,
                "slots": SLOT_MAP,
                "system_slots": SYSTEM_SLOT_MAP,
            },
            "players": players_obj,
            "system_template": {},
            "events": self.current_data.get("events", empty_events()),
            "context": [],
        }

    def normalize_export_selection(self, selection=None):
        defaults = {
            "json": True,
            "mame_cfg": True,
            "ra_rmp": True,
            "rb_xml": True,
            "libretro_yml": True,
        }
        if not selection:
            return defaults
        resolved = dict(defaults)
        for key in defaults:
            if key in selection:
                resolved[key] = bool(selection[key])
        return resolved

    def export_selection_label(self, selection):
        labels = {
            "json": "JSON",
            "mame_cfg": "MAME-CFG",
            "ra_rmp": "FBNEO-RMP",
            "rb_xml": "RB-XML",
            "libretro_yml": "LIBRETRO-YML",
        }
        return ", ".join(labels[key] for key, enabled in selection.items() if enabled) or "none"

    def export_json_mapping_richness(self, data):
        counts = {
            "layout_buttons": 0,
            "layout_system_inputs": 0,
            "layout_system_outputs": 0,
            "layout_axes": 0,
            "panel_joystick_links": 0,
            "output_links": 0,
            "duplicates": 0,
        }
        players = data.get("players") or {}
        if not isinstance(players, dict):
            return counts, 0
        for player in players.values():
            if not isinstance(player, dict):
                continue
            for layout in (player.get("layouts") or {}).values():
                if not isinstance(layout, dict):
                    continue
                counts["layout_buttons"] += len(layout.get("buttons") or {})
                counts["layout_system_inputs"] += len(layout.get("system_inputs") or {})
                counts["layout_system_outputs"] += len(layout.get("system_outputs") or {})
                counts["layout_axes"] += len(layout.get("axes") or {})
            for button in (player.get("buttons") or {}).values():
                if not isinstance(button, dict):
                    continue
                if button.get("panel_joystick"):
                    counts["panel_joystick_links"] += 1
                if button.get("output"):
                    counts["output_links"] += 1
                if button.get("instance_id"):
                    counts["duplicates"] += 1
            for axis in (player.get("axes") or []):
                if isinstance(axis, dict) and axis.get("panel_joystick"):
                    counts["panel_joystick_links"] += 1
            for output in (player.get("system_outputs") or []):
                if not isinstance(output, dict):
                    continue
                if output.get("panel_joystick"):
                    counts["panel_joystick_links"] += 1
                if output.get("input_ref"):
                    counts["output_links"] += 1
            panel_joystick = player.get("panel_joystick")
            if isinstance(panel_joystick, dict):
                counts["panel_joystick_links"] += sum(1 for value in panel_joystick.values() if value)
        total = sum(counts.values())
        return counts, total

    def existing_json_is_richer(self, path, export_data):
        if not os.path.exists(path):
            return False, "", None, None
        try:
            with open(path, "r", encoding="utf-8") as f:
                existing = json.load(f)
        except Exception:
            return False, "", None, None
        old_counts, old_total = self.export_json_mapping_richness(existing)
        new_counts, new_total = self.export_json_mapping_richness(export_data)
        if old_total <= new_total:
            return False, "", old_counts, new_counts
        detail = (
            f"existing={old_total} "
            f"(buttons:{old_counts['layout_buttons']}, sys-in:{old_counts['layout_system_inputs']}, "
            f"sys-out:{old_counts['layout_system_outputs']}, axes:{old_counts['layout_axes']}, "
            f"joy-links:{old_counts['panel_joystick_links']}, outputs:{old_counts['output_links']}, dup:{old_counts['duplicates']})\n"
            f"new={new_total} "
            f"(buttons:{new_counts['layout_buttons']}, sys-in:{new_counts['layout_system_inputs']}, "
            f"sys-out:{new_counts['layout_system_outputs']}, axes:{new_counts['layout_axes']}, "
            f"joy-links:{new_counts['panel_joystick_links']}, outputs:{new_counts['output_links']}, dup:{new_counts['duplicates']})"
        )
        return True, detail, old_counts, new_counts

    def save_current(self, silent=False, export_selection=None, confirm_richer_json=False, confirm_parent=None, skip_all_if_json_declined=False):
        # Default Save behavior in the editor should only generate the JSON.
        if export_selection is None:
            selection = self.normalize_export_selection({
                "json": True,
                "mame_cfg": False,
                "ra_rmp": False,
                "rb_xml": False,
                "libretro_yml": False,
            })
        else:
            selection = self.normalize_export_selection(export_selection)
        export = self.build_export_json()
        path = os.path.join(OUTPUT_DIR, export["rom"] + ".json")
        json_written = False
        json_skipped = False
        richer_detail = ""
        if selection["json"]:
            richer, richer_detail, _, _ = self.existing_json_is_richer(path, export)
            if richer and confirm_richer_json:
                proceed = messagebox.askyesno(
                    "Ultimate Generation",
                    (
                        f"Existing JSON seems richer for {export['rom']}.\n\n"
                        f"{richer_detail}\n\n"
                        + "Overwrite anyway?"
                    ),
                    parent=confirm_parent,
                )
                if not proceed:
                    if skip_all_if_json_declined:
                        return {"skipped": True, "reason": "json_richer_declined", "path": path}
                    json_skipped = True
                else:
                    with open(path, "w", encoding="utf-8") as f:
                        json.dump(export, f, indent=2, ensure_ascii=False)
                    json_written = True
            else:
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(export, f, indent=2, ensure_ascii=False)
                json_written = True
        mame_result = self.save_mame_cfg_export() if selection["mame_cfg"] else None
        rmp_results, rmp_conflicts = self.save_retroarch_rmp_exports() if selection["ra_rmp"] else ([], [])
        retrobat_result = self.save_retrobat_xml_export() if selection["rb_xml"] else None
        libretro_inputmapping_results = self.save_libretro_user_inputmapping_export() if selection["libretro_yml"] else []
        self.schedule_ledpanel_sync(delay_ms=20)
        if not silent:
            details = [f"Selected exports:\n{self.export_selection_label(selection)}"]
            if json_written:
                details.append(f"File saved:\n{path}")
            elif selection["json"] and json_skipped:
                details.append(f"JSON skipped:\n{path}")
            if mame_result:
                details.append(f"MAME CFG:\n{mame_result['path']}")
            if rmp_results:
                details.append("FBNEO-RMP:\n" + "\n".join(item["path"] for item in rmp_results))
            if retrobat_result:
                details.append(f"RB-XML:\n{retrobat_result['path']}")
            if libretro_inputmapping_results:
                details.append("LIBRETRO YML:\n" + "\n".join(result["path"] for result in libretro_inputmapping_results))
            if rmp_conflicts:
                preview = "\n".join(rmp_conflicts[:6])
                more = "" if len(rmp_conflicts) <= 6 else f"\n... and {len(rmp_conflicts) - 6} more."
                details.append(f"FBNEO-RMP conflicts:\n{preview}{more}")
            messagebox.showinfo("Save", "\n\n".join(details))
        return {
            "skipped": False,
            "json_written": json_written,
            "json_skipped": json_skipped,
            "mame_result": mame_result,
            "rmp_results": rmp_results,
            "rmp_conflicts": rmp_conflicts,
            "retrobat_result": retrobat_result,
            "libretro_inputmapping_results": libretro_inputmapping_results,
        }

    def save_and_next(self):
        self.save_current()
        self.next_rom()

    def ultimate_generation(self):
        if not self.roms:
            messagebox.showwarning("Ultimate Generation", "No ROM detected.")
            return
        if self._ultimate_batch and self._ultimate_batch.get("window"):
            try:
                self._ultimate_batch["window"].lift()
                self._ultimate_batch["window"].focus_force()
            except Exception:
                pass
            return
        total = len(self.roms)
        modal = tk.Toplevel(self.root)
        modal.title("Ultimate Generation")
        modal.transient(self.root)
        modal.resizable(False, False)
        modal.attributes("-topmost", True)
        modal.protocol("WM_DELETE_WINDOW", self.close_ultimate_generation_modal)
        frame = tk.Frame(modal, bg="#ffffff", padx=20, pady=18)
        frame.pack(fill="both", expand=True)
        tk.Label(
            frame,
            text="This will regenerate all detected games from the first ROM.",
            bg="#ffffff",
            fg="#101828",
            font=("Segoe UI", 11, "bold"),
        ).pack(anchor="w")
        tk.Label(
            frame,
            text=(
                "Choose the exports to generate and how the panel layout should be selected.\n"
                f"Detected games: {total}"
            ),
            bg="#ffffff",
            fg="#475467",
            font=("Segoe UI", 10),
            justify="left",
        ).pack(anchor="w", pady=(8, 10))

        mode_var = tk.StringVar(value="auto")
        layout_var = tk.StringVar(value=self.export_layout_var.get() if self.export_layout_var.get() in LAYOUTS else "4-Button")
        options = tk.Frame(frame, bg="#ffffff")
        options.pack(fill="x", pady=(0, 10))
        tk.Label(options, text="Export layout mode", bg="#ffffff", fg="#101828", font=("Segoe UI", 10, "bold")).grid(row=0, column=0, sticky="w", padx=(0, 12))
        tk.Radiobutton(options, text="Auto (recommended)", value="auto", variable=mode_var, bg="#ffffff", anchor="w").grid(row=1, column=0, sticky="w")
        tk.Radiobutton(options, text="Fixed layout", value="manual", variable=mode_var, bg="#ffffff", anchor="w").grid(row=2, column=0, sticky="w")
        layout_combo = ttk.Combobox(options, textvariable=layout_var, values=LAYOUTS, width=12, state="disabled")
        layout_combo.grid(row=2, column=1, sticky="w", padx=(8, 0))

        exports_var_frame = tk.Frame(frame, bg="#ffffff")
        exports_var_frame.pack(fill="x", pady=(0, 10))
        tk.Label(exports_var_frame, text="Exports", bg="#ffffff", fg="#101828", font=("Segoe UI", 10, "bold")).grid(row=0, column=0, sticky="w", padx=(0, 12))
        selected_export_var = tk.StringVar(value="json")
        export_labels = {
            "json": "JSON",
            "mame_cfg": "MAME-CFG",
            "ra_rmp": "FBNEO-RMP",
            "rb_xml": "RB-XML",
            "libretro_yml": "LIBRETRO-YML",
        }
        export_radios = {}
        for idx, key in enumerate(("json", "mame_cfg", "ra_rmp", "rb_xml", "libretro_yml"), start=1):
            radio = tk.Radiobutton(
                exports_var_frame,
                text=export_labels[key],
                value=key,
                variable=selected_export_var,
                bg="#ffffff",
                anchor="w",
            )
            radio.grid(row=idx, column=0, sticky="w")
            export_radios[key] = radio
        confirm_json_var = tk.BooleanVar(value=False)
        confirm_json_check = tk.Checkbutton(
            exports_var_frame,
            text="Ask confirmation if existing JSON seems richer",
            variable=confirm_json_var,
            bg="#ffffff",
            anchor="w",
        )
        confirm_json_check.grid(row=1, column=1, rowspan=2, sticky="nw", padx=(18, 0))

        status_var = tk.StringVar(value="Ready.")
        tk.Label(frame, textvariable=status_var, bg="#ffffff", fg="#475467", font=("Segoe UI", 10)).pack(anchor="w", pady=(0, 8))
        layout_status_var = tk.StringVar(value="Mode: Auto per game")
        tk.Label(frame, textvariable=layout_status_var, bg="#ffffff", fg="#667085", font=("Segoe UI", 9, "bold")).pack(anchor="w", pady=(0, 8))
        progress = ttk.Progressbar(frame, mode="determinate", maximum=total, length=620)
        progress.pack(fill="x")
        count_var = tk.StringVar(value=f"0 / {total}")
        tk.Label(frame, textvariable=count_var, bg="#ffffff", fg="#667085", font=("Segoe UI", 9, "bold")).pack(anchor="e", pady=(8, 12))

        buttons = tk.Frame(frame, bg="#ffffff")
        buttons.pack(fill="x")
        start_button = tk.Button(
            buttons,
            text="START",
            bd=0,
            bg="#2459d3",
            fg="#ffffff",
            disabledforeground="#dbeafe",
            activebackground="#1d4ed8",
            activeforeground="#ffffff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=6,
            command=self.start_ultimate_generation_batch,
        )
        start_button.pack(side="left", padx=(0, 8))
        stop_button = tk.Button(
            buttons,
            text="STOP",
            bd=0,
            bg="#b42318",
            fg="#ffffff",
            disabledforeground="#fee4e2",
            activebackground="#912018",
            activeforeground="#ffffff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=6,
            command=self.stop_ultimate_generation_batch,
            state="disabled",
        )
        stop_button.pack(side="left", padx=8)
        resume_button = tk.Button(
            buttons,
            text="RESUME",
            bd=0,
            bg="#b54708",
            fg="#ffffff",
            disabledforeground="#ffead5",
            activebackground="#93370d",
            activeforeground="#ffffff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=6,
            command=self.resume_ultimate_generation_batch,
            state="disabled",
        )
        resume_button.pack(side="left", padx=8)
        close_button = tk.Button(
            buttons,
            text="CLOSE",
            bd=0,
            bg="#f2f4f7",
            fg="#101828",
            disabledforeground="#667085",
            activebackground="#e4e7ec",
            activeforeground="#101828",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=6,
            command=self.close_ultimate_generation_modal,
        )
        close_button.pack(side="right")

        self._ultimate_batch = {
            "window": modal,
            "mode_var": mode_var,
            "layout_var": layout_var,
            "layout_combo": layout_combo,
            "selected_export_var": selected_export_var,
            "export_radios": export_radios,
            "confirm_json_var": confirm_json_var,
            "confirm_json_check": confirm_json_check,
            "status_var": status_var,
            "layout_status_var": layout_status_var,
            "progress": progress,
            "count_var": count_var,
            "start_button": start_button,
            "stop_button": stop_button,
            "resume_button": resume_button,
            "close_button": close_button,
            "started": False,
            "running": False,
            "paused": False,
            "finished": False,
            "after_id": None,
        }
        mode_var.trace_add("write", lambda *_: self.update_ultimate_modal_controls())
        selected_export_var.trace_add("write", lambda *_: self.update_ultimate_modal_controls())
        self.update_ultimate_modal_controls()
        self.center_modal_on_root(modal)
        modal.grab_set()
        modal.lift()
        modal.focus_force()

    def reset_current_json(self):
        rom = self.current_data["system"] if self.current_data else self.roms[self.index]
        path = os.path.join(OUTPUT_DIR, rom + ".json")
        if os.path.exists(path):
            if not messagebox.askyesno("Reset JSON", f"Delete generated JSON and reload from sources?\n\n{path}"):
                return
            os.remove(path)
            self.load_current()
            messagebox.showinfo("Reset JSON", f"Generated JSON deleted and view reset:\n{path}")
            return

        self.load_current()
        messagebox.showinfo("Reset JSON", "No generated JSON found. View reloaded from sources.")

    def export_systems(self):
        count = export_system_templates()
        messagebox.showinfo("Systems", f"{count} system templates exported to:\n{SYSTEM_OUTPUT_DIR}")

    def export_cores(self):
        json_count = export_core_templates()
        yml_count = export_core_inputmapping_templates()
        messagebox.showinfo(
            "Cores",
            (
                f"{json_count} core templates exported to:\n{CORE_OUTPUT_DIR}\n\n"
                f"{yml_count} libretro core/system YAML files exported to:\n{USER_INPUTMAPPING_DIR}"
            ),
        )

    def next_rom(self):
        current_rom = self.current_data["system"] if self.current_data else None
        if current_rom:
            path = os.path.join(OUTPUT_DIR, current_rom + ".json")
            if not os.path.exists(path):
                self.save_current(silent=True)

        if self.index < len(self.roms) - 1:
            self.index += 1
            self.load_current()

    def prev_rom(self):
        if self.index > 0:
            self.index -= 1
            self.load_current()

    def goto_search(self):
        q = self.search_var.get().strip().lower()
        if not q:
            return
        for i, rom in enumerate(self.roms):
            if q in rom.lower():
                self.index = i
                self.load_current()
                return
        messagebox.showwarning("Search", f"No ROM found for: {q}")

    def goto_index(self):
        raw = self.goto_var.get().strip()
        if not raw.isdigit():
            messagebox.showwarning("Index", "Enter a valid number.")
            return
        idx = int(raw)
        if 1 <= idx <= len(self.roms):
            self.index = idx - 1
            self.load_current()
        else:
            messagebox.showwarning("Index", f"Index out of range: 1 to {len(self.roms)}")


    def _build_ui(self):
        self.root.geometry("1680x980")
        self.root.configure(bg="#f6f8fb")
        self.root.grid_rowconfigure(1, weight=1)
        self.root.grid_columnconfigure(0, weight=1)

        top = tk.Frame(self.root, bg="#f6f8fb")
        top.grid(row=0, column=0, sticky="ew")
        top.grid_columnconfigure(0, weight=1)
        top_main = tk.Frame(top, bg="#f6f8fb", height=60)
        top_main.grid(row=0, column=0, sticky="ew")
        top_main.grid_propagate(False)
        top_exports = tk.Frame(top, bg="#f6f8fb", height=46)
        top_exports.grid(row=1, column=0, sticky="ew")
        top_exports.grid_propagate(False)
        self.top_game_var = tk.StringVar(value="PANEL CURATOR")
        tk.Label(top_main, textvariable=self.top_game_var, bg="#f6f8fb", fg="#101828", font=("Segoe UI", 18, "bold")).pack(side="left", padx=(24, 30))
        tk.Frame(top_main, width=1, bg="#d0d5dd", height=28).pack(side="left", padx=8)
        tk.Button(top_main, text="PREV", bd=0, bg="#f6f8fb", fg="#667085", font=("Segoe UI", 10, "bold"), command=self.prev_rom).pack(side="left", padx=4)
        tk.Button(top_main, text="NEXT", bd=0, bg="#f6f8fb", fg="#667085", font=("Segoe UI", 10, "bold"), command=self.next_rom).pack(side="left", padx=4)
        tk.Button(top_main, text="SAVE", bd=0, bg="#f6f8fb", fg="#2459d3", font=("Segoe UI", 10, "bold"), command=self.save_current).pack(side="left", padx=8)
        tk.Button(top_main, text="SAVE + NEXT", bd=0, bg="#f6f8fb", fg="#2459d3", font=("Segoe UI", 10, "bold"), command=self.save_and_next).pack(side="left", padx=4)

        tk.Label(top_exports, text="EXPORT PANEL", bg="#f6f8fb", fg="#667085", font=("Segoe UI", 9, "bold")).pack(side="left", padx=(24, 4))
        export_layout_combo = ttk.Combobox(top_exports, textvariable=self.export_layout_var, values=LAYOUTS, width=9, state="readonly")
        export_layout_combo.pack(side="left", padx=4)
        export_layout_combo.bind("<<ComboboxSelected>>", self.on_export_layout_changed)
        tk.Button(top_exports, text="MAME-CFG", bd=0, bg="#f6f8fb", fg="#0f766e", font=("Segoe UI", 10, "bold"), command=self.generate_mame_cfg).pack(side="left", padx=4)
        tk.Button(top_exports, text="FBNEO-RMP", bd=0, bg="#f6f8fb", fg="#0f766e", font=("Segoe UI", 10, "bold"), command=self.generate_retroarch_rmp).pack(side="left", padx=4)
        tk.Button(top_exports, text="RB-XML", bd=0, bg="#f6f8fb", fg="#0f766e", font=("Segoe UI", 10, "bold"), command=self.generate_retrobat_xml_placeholder).pack(side="left", padx=4)
        tk.Button(top_exports, text="LIBRETRO-YML", bd=0, bg="#f6f8fb", fg="#0f766e", font=("Segoe UI", 10, "bold"), command=self.generate_libretro_user_inputmapping).pack(side="left", padx=4)
        tk.Button(top_exports, text="ULTIMATE GENERATION", bd=0, bg="#f6f8fb", fg="#b54708", font=("Segoe UI", 10, "bold"), command=self.ultimate_generation).pack(side="left", padx=4)
        tk.Button(top_exports, text="RESET JSON", bd=0, bg="#f6f8fb", fg="#b42318", font=("Segoe UI", 10, "bold"), command=self.reset_current_json).pack(side="left", padx=4)
        tk.Button(top_exports, text="EXPORT SYSTEMS", bd=0, bg="#f6f8fb", fg="#2459d3", font=("Segoe UI", 10, "bold"), command=self.export_systems).pack(side="left", padx=4)
        tk.Button(top_exports, text="EXPORT CORES", bd=0, bg="#f6f8fb", fg="#2459d3", font=("Segoe UI", 10, "bold"), command=self.export_cores).pack(side="left", padx=4)

        self.status_var = tk.StringVar()
        tk.Label(top_main, textvariable=self.status_var, bg="#f6f8fb", fg="#667085", font=("Segoe UI", 10, "bold")).pack(side="right", padx=(8, 18))
        ledpanel_box = tk.Frame(top_main, bg="#f6f8fb")
        ledpanel_box.pack(side="right", padx=(8, 8))
        self.ledpanel_indicator = tk.Canvas(ledpanel_box, width=16, height=16, bg="#f6f8fb", highlightthickness=0)
        self.ledpanel_indicator.pack(side="left", padx=(0, 6))
        self.ledpanel_indicator_dot = self.ledpanel_indicator.create_oval(2, 2, 14, 14, fill="#dc2626", outline="#dc2626")
        tk.Label(ledpanel_box, textvariable=self.ledpanel_status_var, bg="#f6f8fb", fg="#667085", font=("Segoe UI", 9, "bold")).pack(side="left")
        search = tk.Frame(top_main, bg="#edf1f7", bd=0)
        search.pack(side="right", padx=8)
        self.search_var = tk.StringVar()
        ent = tk.Entry(search, textvariable=self.search_var, bd=0, bg="#edf1f7", fg="#101828", width=20)
        ent.pack(side="left", padx=10, pady=10)
        ent.bind("<Return>", lambda e: self.goto_search())
        tk.Button(search, text="SEARCH", bd=0, bg="#edf1f7", fg="#667085", font=("Segoe UI", 9, "bold"), command=self.goto_search).pack(side="left", padx=8)
        self.goto_var = tk.StringVar()
        goto = tk.Entry(top_main, textvariable=self.goto_var, width=6, bd=1)
        goto.pack(side="right", padx=(0, 8))
        goto.bind("<Return>", lambda e: self.goto_index())
        tk.Button(top_main, text="GO", bd=0, bg="#f6f8fb", fg="#101828", font=("Segoe UI", 9, "bold"), command=self.goto_index).pack(side="right")

        self.rom_var = tk.StringVar()
        self.game_var = tk.StringVar()

        content = tk.PanedWindow(
            self.root,
            orient="horizontal",
            bg="#98a2b3",
            sashwidth=14,
            sashpad=0,
            sashrelief="flat",
            bd=0,
            showhandle=False,
            sashcursor="sb_h_double_arrow",
        )
        content.grid(row=1, column=0, sticky="nsew")

        left = tk.Frame(content, bg="#f6f8fb")
        right = tk.Frame(content, bg="#f6f8fb")
        right.grid_rowconfigure(0, weight=1)
        right.grid_columnconfigure(0, weight=1)
        content.add(left, minsize=360, stretch="always", padx=20, pady=14)
        content.add(right, minsize=760, stretch="always", padx=20, pady=14)
        self.main_pane = content
        self.root.after_idle(self.reset_main_pane_position)

        preview_card = tk.Frame(left, bg="#ffffff")
        preview_card.pack(fill="x", pady=(0, 18))
        tk.Label(preview_card, textvariable=self.rom_var, bg="#ffffff", fg="#101828", font=("Segoe UI", 16, "bold"), anchor="w").pack(fill="x", padx=20, pady=(18, 0))
        tk.Label(preview_card, textvariable=self.game_var, bg="#ffffff", fg="#667085", font=("Segoe UI", 10, "bold"), anchor="w", wraplength=430, justify="left").pack(fill="x", padx=20, pady=(2, 10))
        self.image_label = tk.Label(preview_card, bg="#ffffff")
        self.image_label.pack(fill="x", padx=20, pady=(0, 20))
        tk.Label(preview_card, text="SURFACE_PREVIEW", bg="#2459d3", fg="white", font=("Segoe UI", 9, "bold")).place(x=18, y=76)

        meta_box = tk.Frame(left, bg="#ffffff")
        meta_box.pack(fill="x", pady=(0, 18))
        tk.Label(meta_box, text="METADATA", bg="#ffffff", fg="#667085", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=14, pady=(12, 6))
        meta_scroll_host = tk.Frame(meta_box, bg="#ffffff")
        meta_scroll_host.pack(fill="x", padx=14, pady=(0, 14))
        self.meta_text = tk.Text(meta_scroll_host, height=10, wrap="word", bd=0, bg="#ffffff", fg="#101828", font=("Consolas", 10))
        meta_scroll = ttk.Scrollbar(meta_scroll_host, orient="vertical", command=self.meta_text.yview)
        self.meta_text.configure(yscrollcommand=meta_scroll.set)
        self.meta_text.pack(side="left", fill="x", expand=True)
        meta_scroll.pack(side="right", fill="y")

        common_box = tk.Frame(left, bg="#ffffff")
        common_box.pack(fill="both", expand=True)
        tk.Label(common_box, text="COMMON BUTTON VARIABLES", bg="#ffffff", fg="#667085", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=14, pady=(12, 6))
        self.common_scroll = ScrollableFrame(common_box)
        self.common_scroll.pack(fill="both", expand=True, padx=12, pady=(0, 12))
        self.common_container = self.common_scroll.inner

        self.notebook = ttk.Notebook(right)
        self.notebook.grid(row=0, column=0, sticky="nsew")
        self.notebook.bind("<<NotebookTabChanged>>", lambda e: self.schedule_ledpanel_sync())

    def reset_main_pane_position(self):
        if not hasattr(self, "main_pane"):
            return
        width = max(self.main_pane.winfo_width(), 1)
        self.main_pane.sash_place(0, int(width * 0.34), 0)


# ============================================================
# MAIN
# ============================================================

if __name__ == "__main__":
    root = tk.Tk()
    app = CuratorApp(root)
    root.mainloop()
